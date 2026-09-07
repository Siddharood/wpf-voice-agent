using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfVoiceAgent.Audio;
using WpfVoiceAgent.Models;
using WpfVoiceAgent.OpenAI;
using WpfVoiceAgent.WakeWord;

namespace WpfVoiceAgent.Agent
{
    public sealed class VoiceAgentController : IDisposable
    {
        private readonly IAudioCapture _capture;
        private readonly IAecProcessor _aec;
        private readonly IWakeWordDetector _wakeWord;
        private readonly OpenAiRealtimeClient _realtime;
        private readonly RealtimeAudioPlayback _playback;

        private readonly object _gate =
            new object();

        private CancellationTokenSource _cts;

        private AgentState _state =
            AgentState.Idle;

        private bool _started;
        private bool _disposed;

        private bool _wakeDetectionInProgress;

        private bool _turnActive;
        private bool _responseStarted;
        private bool _responseFinished;

        private long _turnGeneration;
        private long _playbackGeneration;

        private string _responseId;

        private Task _turnCleanupTask;

        /*
         * UI transcript ordering.
         *
         * Realtime output transcript deltas can arrive before the completed
         * user input transcript. Therefore agent text is buffered until the
         * current turn's user transcript has been received.
         */
        private readonly StringBuilder _pendingAgentTranscript =
            new StringBuilder();

        private bool _userTranscriptReceived;
        private bool _agentUiStarted;

        public event Action<AgentState> StateChanged;
        public event Action<string> StatusChanged;
        public event Action<string> TranscriptReceived;
        public event Action<string> AgentResponseStarted;
        public event Action<string> Error;

        public VoiceAgentController(
            IAudioCapture capture,
            IAecProcessor aec,
            IWakeWordDetector wakeWord,
            OpenAiRealtimeClient realtime,
            RealtimeAudioPlayback playback)
        {
            _capture =
                capture ??
                throw new ArgumentNullException(
                    nameof(capture));

            _aec =
                aec ??
                throw new ArgumentNullException(
                    nameof(aec));

            _wakeWord =
                wakeWord ??
                throw new ArgumentNullException(
                    nameof(wakeWord));

            _realtime =
                realtime ??
                throw new ArgumentNullException(
                    nameof(realtime));

            _playback =
                playback ??
                throw new ArgumentNullException(
                    nameof(playback));

            _capture.DataAvailable +=
                Capture_DataAvailable;

            _realtime.Status +=
                Realtime_Status;

            _realtime.AudioReceived +=
                Realtime_AudioReceived;

            _realtime.UserTranscript +=
                Realtime_UserTranscript;

            _realtime.TranscriptDelta +=
                Realtime_TranscriptDelta;

            _realtime.ResponseStarted +=
                Realtime_ResponseStarted;

            _realtime.OutputAudioCompleted +=
                Realtime_OutputAudioCompleted;

            _realtime.ResponseCompleted +=
                Realtime_ResponseCompleted;

            _realtime.Disconnected +=
                Realtime_Disconnected;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        nameof(VoiceAgentController));
                }

                if (_started)
                    return;

                _started = true;

                _cts =
                    new CancellationTokenSource();

                _turnActive = false;
                _responseStarted = false;
                _responseFinished = false;
                _responseId = null;

                _turnGeneration = 0;
                _playbackGeneration = 0;

                _wakeDetectionInProgress = false;

                ResetTranscriptState();
            }

            try
            {
                _playback.Start();

                _wakeWord.SetEnabled(true);

                SetState(
                    AgentState.Idle);

                SetStatus(
                    "Waiting for wake word: Computer");

                _capture.Start();
            }
            catch (Exception ex)
            {
                ReportError(
                    "Agent start failed: " +
                    ex.Message);

                StopInternal();
                throw;
            }
        }

        private void Capture_DataAvailable(
            object sender,
            NAudio.Wave.WaveInEventArgs e)
        {
            if (e == null ||
                e.BytesRecorded <= 0 ||
                e.Buffer == null)
            {
                return;
            }

            CancellationToken token;

            bool turnActive;
            bool responseStarted;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    _cts == null)
                {
                    return;
                }

                token = _cts.Token;

                turnActive =
                    _turnActive;

                responseStarted =
                    _responseStarted;
            }

            byte[] microphoneData =
                new byte[e.BytesRecorded];

            Buffer.BlockCopy(
                e.Buffer,
                0,
                microphoneData,
                0,
                e.BytesRecorded);

            /*
             * Wake-word mode.
             *
             * SAPI listens independently to the default microphone.
             * We only use this capture callback as the lightweight gate
             * that consumes the detector's latched wake event.
             */
            if (!turnActive)
            {
                TryDetectWakeWord(
                    microphoneData,
                    token);

                return;
            }

            /*
             * Once the Realtime response has started, stop sending
             * microphone audio for this turn.
             */
            if (responseStarted)
                return;

            byte[] pcm16 =
                ConvertCaptureToPcm16Mono(
                    microphoneData,
                    _capture.WaveFormat);

            if (pcm16.Length == 0)
                return;

            byte[] aecProcessed =
                _aec.ProcessCapture(
                    pcm16);

            if (aecProcessed == null ||
                aecProcessed.Length == 0)
            {
                return;
            }

            byte[] pcm24k =
                PcmResampler.Resample16BitMono(
                    aecProcessed,
                    48000,
                    24000);

            if (pcm24k.Length == 0)
                return;

            _ = SendRealtimeAudioAsync(
                pcm24k,
                token);
        }

        private void TryDetectWakeWord(
            byte[] microphoneData,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    _turnActive ||
                    _wakeDetectionInProgress)
                {
                    return;
                }

                _wakeDetectionInProgress = true;
            }

            _ = DetectWakeWordAsync(
                microphoneData,
                cancellationToken);
        }

        private async Task DetectWakeWordAsync(
            byte[] microphoneData,
            CancellationToken cancellationToken)
        {
            try
            {
                bool detected =
                    await _wakeWord.DetectAsync(
                        microphoneData,
                        cancellationToken)
                        .ConfigureAwait(false);

                if (!detected)
                    return;

                await BeginTurnAsync(
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportError(
                    "Wake-word detection failed: " +
                    ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _wakeDetectionInProgress = false;
                }
            }
        }

        private async Task BeginTurnAsync(
            CancellationToken cancellationToken)
        {
            long generation;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    _turnActive)
                {
                    return;
                }

                _turnActive = true;

                _responseStarted = false;
                _responseFinished = false;
                _responseId = null;

                _turnGeneration++;

                generation =
                    _turnGeneration;

                ResetTranscriptState();
            }

            try
            {
                /*
                 * Wake detector is now logically closed for this turn.
                 */
                _wakeWord.SetEnabled(false);

                _aec.Reset();

                _playback.Clear();

                SetState(
                    AgentState.Listening);

                SetStatus(
                    "Wake word detected. Listening...");

                await EnsureRealtimeConnectedAsync(
                    cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed ||
                        !_started ||
                        !_turnActive ||
                        generation != _turnGeneration)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await AbortTurnAsync(
                    generation,
                    false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportError(
                    "Turn start failed: " +
                    ex.Message);

                await AbortTurnAsync(
                    generation,
                    true)
                    .ConfigureAwait(false);
            }
        }

        private async Task EnsureRealtimeConnectedAsync(
            CancellationToken cancellationToken)
        {
            if (_realtime.IsConnected)
                return;

            SetStatus(
                "Connecting to OpenAI Realtime...");

            await _realtime.ConnectAsync(
                cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task SendRealtimeAudioAsync(
            byte[] pcm24k,
            CancellationToken cancellationToken)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed ||
                        !_started ||
                        !_turnActive ||
                        _responseStarted)
                    {
                        return;
                    }
                }

                await _realtime.SendAudioAsync(
                    pcm24k,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportError(
                    "Realtime audio send failed: " +
                    ex.Message);
            }
        }

        private void Realtime_Status(
            string status)
        {
            SetStatus(status);
        }


        private void Realtime_UserTranscript(
    string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript))
                return;

            string pendingAgentText = null;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    !_turnActive)
                {
                    return;
                }

                _userTranscriptReceived = true;

                if (_pendingAgentTranscript.Length > 0)
                {
                    pendingAgentText =
                        _pendingAgentTranscript.ToString();

                    _pendingAgentTranscript.Clear();

                    _agentUiStarted = true;
                }
            }

            // New line BEFORE the next You message.
            TranscriptReceived?.Invoke(
                Environment.NewLine +
                Environment.NewLine +
                "You: " +
                transcript);

            // Agent stays directly after You on the same line.
            if (!string.IsNullOrEmpty(pendingAgentText))
            {
                TranscriptReceived?.Invoke(
                    Environment.NewLine +
                    "Agent: " +
                    pendingAgentText);
            }
        }
        private void Realtime_TranscriptDelta(
    string delta)
        {
            if (string.IsNullOrEmpty(delta))
                return;

            bool addAgentPrefix = false;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    !_turnActive)
                {
                    return;
                }

                if (!_userTranscriptReceived)
                {
                    _pendingAgentTranscript.Append(delta);
                    return;
                }

                if (!_agentUiStarted)
                {
                    _agentUiStarted = true;
                    addAgentPrefix = true;
                }
            }

            if (addAgentPrefix)
            {
                // NO newline here.
                TranscriptReceived?.Invoke(
                    "Agent: ");
            }

            TranscriptReceived?.Invoke(delta);
        }

        private void Realtime_ResponseStarted(
            string responseId)
        {
            long generation;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    !_turnActive)
                {
                    return;
                }

                if (_responseStarted)
                    return;

                _responseStarted = true;
                _responseFinished = false;

                _responseId =
                    responseId;

                generation =
                    _turnGeneration;
            }

            try
            {
                _playbackGeneration =
                    _playback.BeginResponse();
            }
            catch (Exception ex)
            {
                ReportError(
                    "Playback start failed: " +
                    ex.Message);

                return;
            }

            SetState(
                AgentState.Speaking);

            SetStatus(
                "Agent is responding. ResponseId=" +
                responseId);

            /*
             * Important:
             *
             * Do NOT print "Agent: " here.
             *
             * The actual transcript may arrive before or after the user
             * transcript. Realtime_TranscriptDelta controls the UI ordering.
             */
        }

        private void Realtime_AudioReceived(
            string responseId,
            byte[] pcm24k)
        {
            long playbackGeneration;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    !_turnActive ||
                    !_responseStarted ||
                    _responseFinished)
                {
                    return;
                }

                if (!string.Equals(
                    responseId,
                    _responseId,
                    StringComparison.Ordinal))
                {
                    /*
                     * Stale audio from a previous response must never enter
                     * the current speaker queue.
                     */
                    return;
                }

                playbackGeneration =
                    _playbackGeneration;
            }

            _playback.WritePcm(
                playbackGeneration,
                pcm24k);
        }

        private void Realtime_OutputAudioCompleted(
            string responseId)
        {
            /*
             * Do not finish the turn here.
             *
             * response.output_audio.done means model-generated audio is
             * finished streaming, but queued PCM may still be playing.
             */
            SetStatus(
                "Agent audio stream completed. " +
                "Waiting for response completion...");
        }

        private void Realtime_ResponseCompleted(
            string responseId)
        {
            long generation;
            long playbackGeneration;

            lock (_gate)
            {
                if (_disposed ||
                    !_started ||
                    !_turnActive ||
                    !_responseStarted ||
                    _responseFinished)
                {
                    return;
                }

                if (!string.Equals(
                    responseId,
                    _responseId,
                    StringComparison.Ordinal))
                {
                    return;
                }

                _responseFinished = true;

                generation =
                    _turnGeneration;

                playbackGeneration =
                    _playbackGeneration;

                if (_turnCleanupTask != null &&
                    !_turnCleanupTask.IsCompleted)
                {
                    return;
                }

                _turnCleanupTask =
                    FinishTurnAsync(
                        generation,
                        playbackGeneration,
                        _cts.Token);
            }
        }

        private async Task FinishTurnAsync(
            long generation,
            long playbackGeneration,
            CancellationToken cancellationToken)
        {
            try
            {
                SetStatus(
                    "Response complete. Finishing speaker playback...");

                await _playback.EndResponseAsync(
                    playbackGeneration,
                    cancellationToken)
                    .ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed ||
                        !_started ||
                        generation != _turnGeneration)
                    {
                        return;
                    }
                }

                _aec.Reset();

                lock (_gate)
                {
                    if (_disposed ||
                        !_started ||
                        generation != _turnGeneration)
                    {
                        return;
                    }

                    _turnActive = false;
                    _responseStarted = false;
                    _responseFinished = false;
                    _responseId = null;

                    /*
                     * Clear any transcript state left from this turn.
                     */
                    ResetTranscriptState();
                }

                /*
                 * Wake mode is reopened only after the previous response
                 * has completely drained.
                 */
                _wakeWord.SetEnabled(true);

                SetState(
                    AgentState.Idle);

                SetStatus(
                    "Ready. Waiting for wake word: Computer");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportError(
                    "Turn cleanup failed: " +
                    ex.Message);

                await AbortTurnAsync(
                    generation,
                    true)
                    .ConfigureAwait(false);
            }
        }

        private async Task AbortTurnAsync(
            long generation,
            bool reportIdle)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                        return;

                    if (generation != _turnGeneration)
                        return;
                }

                _playback.Stop();

                try
                {
                    await _realtime.CancelResponseAsync(
                        CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                }

                _aec.Reset();

                lock (_gate)
                {
                    if (_disposed ||
                        !_started ||
                        generation != _turnGeneration)
                    {
                        return;
                    }

                    _turnActive = false;
                    _responseStarted = false;
                    _responseFinished = false;
                    _responseId = null;

                    ResetTranscriptState();
                }

                _wakeWord.SetEnabled(true);

                if (reportIdle)
                {
                    SetState(
                        AgentState.Idle);

                    SetStatus(
                        "Turn reset. Waiting for wake word: Computer");
                }
            }
            catch
            {
            }
        }

        private void Realtime_Disconnected()
        {
            bool shouldReport;

            lock (_gate)
            {
                shouldReport =
                    !_disposed &&
                    _started;
            }

            if (!shouldReport)
                return;

            SetStatus(
                "OpenAI Realtime disconnected.");

            /*
             * Do not automatically destroy wake-word state here.
             * The next wake can establish a fresh Realtime connection.
             */
        }

        private void ResetTranscriptState()
        {
            _pendingAgentTranscript.Clear();

            _userTranscriptReceived = false;
            _agentUiStarted = false;
        }

        private static byte[] ConvertCaptureToPcm16Mono(
            byte[] input,
            NAudio.Wave.WaveFormat format)
        {
            if (input == null ||
                input.Length == 0 ||
                format == null)
            {
                return new byte[0];
            }

            if (format.Encoding ==
                NAudio.Wave.WaveFormatEncoding.IeeeFloat &&
                format.BitsPerSample == 32)
            {
                int channels =
                    Math.Max(
                        1,
                        format.Channels);

                int frameBytes =
                    channels * 4;

                int frameCount =
                    input.Length / frameBytes;

                byte[] output =
                    new byte[frameCount * 2];

                for (int frame = 0;
                     frame < frameCount;
                     frame++)
                {
                    double sum = 0.0;

                    for (int channel = 0;
                         channel < channels;
                         channel++)
                    {
                        int offset =
                            frame * frameBytes +
                            channel * 4;

                        float sample =
                            BitConverter.ToSingle(
                                input,
                                offset);

                        sum += sample;
                    }

                    double mono =
                        sum / channels;

                    if (mono > 1.0)
                        mono = 1.0;

                    if (mono < -1.0)
                        mono = -1.0;

                    short pcm =
                        (short)(
                            mono * 32767.0);

                    output[frame * 2] =
                        (byte)(
                            pcm & 0xFF);

                    output[frame * 2 + 1] =
                        (byte)(
                            (pcm >> 8) & 0xFF);
                }

                return output;
            }

            if (format.Encoding ==
                NAudio.Wave.WaveFormatEncoding.Pcm &&
                format.BitsPerSample == 16)
            {
                int channels =
                    Math.Max(
                        1,
                        format.Channels);

                if (channels == 1)
                    return input;

                int frameBytes =
                    channels * 2;

                int frameCount =
                    input.Length / frameBytes;

                byte[] output =
                    new byte[frameCount * 2];

                for (int frame = 0;
                     frame < frameCount;
                     frame++)
                {
                    int sum = 0;

                    for (int channel = 0;
                         channel < channels;
                         channel++)
                    {
                        int offset =
                            frame * frameBytes +
                            channel * 2;

                        sum +=
                            BitConverter.ToInt16(
                                input,
                                offset);
                    }

                    short mono =
                        (short)(
                            sum / channels);

                    output[frame * 2] =
                        (byte)(
                            mono & 0xFF);

                    output[frame * 2 + 1] =
                        (byte)(
                            (mono >> 8) & 0xFF);
                }

                return output;
            }

            return new byte[0];
        }

        private void SetState(
            AgentState state)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _state = state;
            }

            StateChanged?.Invoke(state);
        }

        private void SetStatus(
            string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return;

            lock (_gate)
            {
                if (_disposed)
                    return;
            }

            StatusChanged?.Invoke(status);
        }

        private void ReportError(
            string message)
        {
            Error?.Invoke(message);
        }

        private void StopInternal()
        {
            CancellationTokenSource cts;

            lock (_gate)
            {
                if (!_started)
                    return;

                _started = false;

                _turnActive = false;
                _responseStarted = false;
                _responseFinished = false;
                _responseId = null;

                ResetTranscriptState();

                cts = _cts;
                _cts = null;
            }

            try
            {
                _wakeWord.SetEnabled(false);
            }
            catch
            {
            }

            try
            {
                _capture.Stop();
            }
            catch
            {
            }

            try
            {
                _playback.Stop();
            }
            catch
            {
            }

            try
            {
                cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                cts?.Dispose();
            }
            catch
            {
            }

            SetState(
                AgentState.Idle);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            try
            {
                _wakeWord.SetEnabled(false);
            }
            catch
            {
            }

            try
            {
                _capture.DataAvailable -=
                    Capture_DataAvailable;
            }
            catch
            {
            }

            try
            {
                _realtime.Status -=
                    Realtime_Status;

                _realtime.AudioReceived -=
                    Realtime_AudioReceived;

                _realtime.UserTranscript -=
                    Realtime_UserTranscript;

                _realtime.TranscriptDelta -=
                    Realtime_TranscriptDelta;

                _realtime.ResponseStarted -=
                    Realtime_ResponseStarted;

                _realtime.OutputAudioCompleted -=
                    Realtime_OutputAudioCompleted;

                _realtime.ResponseCompleted -=
                    Realtime_ResponseCompleted;

                _realtime.Disconnected -=
                    Realtime_Disconnected;
            }
            catch
            {
            }

            try
            {
                _cts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _capture.Stop();
            }
            catch
            {
            }

            try
            {
                _playback.Stop();
            }
            catch
            {
            }

            try
            {
                _capture.Dispose();
            }
            catch
            {
            }

            try
            {
                _wakeWord.Dispose();
            }
            catch
            {
            }

            try
            {
                _realtime.Dispose();
            }
            catch
            {
            }

            try
            {
                _playback.Dispose();
            }
            catch
            {
            }

            try
            {
                _cts?.Dispose();
            }
            catch
            {
            }

            _cts = null;
        }
    }
}