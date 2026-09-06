using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
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
        private readonly IAudioPlayback _playback;
        private readonly IAecProcessor _aec;
        private readonly IWakeWordDetector _wakeWord;
        private readonly OpenAiClient _openAi;
        private readonly EnergyVad _vad;
        private readonly MultiSpeakerOverlapDetector _overlapDetector;
        private bool _heardSpeech;

        private readonly List<Dictionary<string, string>> _messages =
            new List<Dictionary<string, string>>();

        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private MemoryStream _turnAudio;
        private bool _recordingTurn;
        private DateTime _lastWakeUtc = DateTime.MinValue;
        private DateTime _lastSpeechUtc = DateTime.MinValue;
        private int _silentFrames;

        private readonly KeywordWakeWordDetector _keywordWakeWord;

        public AgentState State { get; private set; } = AgentState.Idle;

        public event Action<AgentState> StateChanged;
        public event Action<string> Transcript;
        public event Action<string> Status;
        public event Action<string> Error;

        public VoiceAgentController(
            IAudioCapture capture,
            IAudioPlayback playback,
            IAecProcessor aec,
            IWakeWordDetector wakeWord,
            OpenAiClient openAi)
        {
            _capture = capture;
            _playback = playback;
            _aec = aec;
            _wakeWord = wakeWord;
            _openAi = openAi;
            int sampleRate = Math.Max(8000, capture.WaveFormat.SampleRate);

            _vad = new EnergyVad(sampleRate);
            _overlapDetector =
                new MultiSpeakerOverlapDetector(sampleRate);

            _capture.DataAvailable += OnAudio;
            _playback.PlaybackStarted += (s, e) =>
            {
                Status?.Invoke("Agent speaking");
            };
            _playback.PlaybackStopped += (s, e) =>
            {
                _aec.Reset();

                _wakeWord.SetEnabled(true);

                SetState(AgentState.Idle);

                Status?.Invoke(
                    "Waiting for wake word: Computer");
            };

            _messages.Add(new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] =
                    "You are a concise desktop voice assistant. " +
                     "Always respond in English. " +
                    "Answer naturally and briefly unless more detail is requested."
            });
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _capture.Start();
            SetState(AgentState.Idle);
            //Status?.Invoke("Microphone active. Format: " +_capture.WaveFormat.ToString());

            //BeginTurn();
            Status?.Invoke("Microphone active. Waiting for wake word.");
        }

        public void Stop()
        {
            _cts?.Cancel();
            _capture.Stop();
            _recordingTurn = false;
            _turnAudio?.Dispose();
            _turnAudio = null;
            SetState(AgentState.Idle);
        }

        private void OnAudio(object sender, WaveInEventArgs e)
        {
            if (_cts == null || _cts.IsCancellationRequested)
                return;

            byte[] raw = new byte[e.BytesRecorded];

            Buffer.BlockCopy(
                e.Buffer,
                0,
                raw,
                0,
                e.BytesRecorded);

            byte[] pcm16 =
                ConvertToPcm16Mono(
                    raw,
                    _capture.WaveFormat);

            byte[] cleaned =
                _aec.ProcessCapture(pcm16);

            if (_playback.IsPlaying)
                return;

            if (State == AgentState.Idle)
            {
                // Feed EVERY microphone frame to the wake-word detector.
                bool detected = _wakeWord.DetectAsync(
                    cleaned,
                    _cts.Token).GetAwaiter().GetResult();

                if (detected)
                {
                    _lastWakeUtc = DateTime.UtcNow;
                    BeginTurn();
                }

                return;
            }

            if (State != AgentState.Listening)
                return;

            HandleListeningFrame(cleaned);
        }


        private void BeginTurn()
        {
            lock (_gate)
            {
                if (_recordingTurn)
                    return;

                _recordingTurn = true;
                _turnAudio = new MemoryStream();
                _silentFrames = 0;
                _heardSpeech = false;
                _lastSpeechUtc = DateTime.UtcNow;
            }

            /*
             * Wake word has already been detected.
             * Ignore further wake-word matches while processing
             * this conversational turn.
             */
            _wakeWord.SetEnabled(false);

            SetState(AgentState.Listening);
            Status?.Invoke("Wake word detected. Listening...");
        }

        private void HandleListeningFrame(byte[] pcm)
        {
            lock (_gate)
            {
                if (!_recordingTurn || _turnAudio == null)
                    return;

                _turnAudio.Write(pcm, 0, pcm.Length);

                bool speech = _vad.HasSpeech(pcm);

                if (speech)
                {
                    _heardSpeech = true;
                    _silentFrames = 0;
                    _lastSpeechUtc = DateTime.UtcNow;
                }
                else
                {
                    _silentFrames++;
                }

                // Do not end the turn until the user has actually spoken.
                if (!_heardSpeech)
                    return;

                // End after 900 ms of silence following speech.
                if ((DateTime.UtcNow - _lastSpeechUtc) > TimeSpan.FromMilliseconds(900))
                {
                    _recordingTurn = false;

                    MemoryStream completed = _turnAudio;
                    _turnAudio = null;

                    byte[] completedAudio = completed.ToArray();

                    completed.Dispose();

                    if (_overlapDetector.HasProbableOverlap(completedAudio))
                    {
                        Status?.Invoke(
                            "Multiple speakers or overlapping speech detected. Please repeat.");

                        SetState(AgentState.Idle);

                        return;
                    }

                    _ = ProcessTurnAsync(completedAudio);

                    completed.Dispose();
                }
            }
        }

        private async Task ProcessTurnAsync(byte[] pcm)
        {
            try
            {
                SetState(AgentState.Processing);
                Status?.Invoke("Transcribing...");

                byte[] wav = PcmToWav(
                                    pcm,
                                    _capture.WaveFormat.SampleRate,
                                    1,
                                    16);

                Status?.Invoke(
                        "Transcribing... Audio bytes: " +
                        pcm.Length +
                        ", WAV bytes: " +
                        wav.Length);

                //string text = await _openAi.TranscribeWavAsync(
                //    wav, _cts.Token).ConfigureAwait(false);


                Status?.Invoke("Sending audio to OpenAI...");

                string text = await _openAi.TranscribeWavAsync(
                    wav, _cts.Token);

                Status?.Invoke(
                    "Transcription result: [" + text + "]");

                if (string.IsNullOrWhiteSpace(text))
                {
                    SetState(AgentState.Idle);
                    Status?.Invoke("No speech recognized.");
                    return;
                }

                Transcript?.Invoke("You: " + text);

                _messages.Add(new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = text
                });

                Status?.Invoke("Thinking...");

                string answer = await _openAi.ChatAsync(
                    _messages, _cts.Token).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(answer))
                    return;

                _messages.Add(new Dictionary<string, string>
                {
                    ["role"] = "assistant",
                    ["content"] = answer
                });

                Transcript?.Invoke("Agent: " + answer);

                Status?.Invoke("Generating speech...");
                byte[] speech = await _openAi.TextToSpeechAsync(
                    answer, _cts.Token).ConfigureAwait(false);

                SetState(AgentState.Speaking);

                // IMPORTANT:
                // A production AEC implementation must receive the exact render
                // reference here, synchronized with speaker playback.
                await _playback.PlayWavAsync(
                    speech, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SetState(AgentState.Error);
                Error?.Invoke(ex.ToString());
                await Task.Delay(500).ConfigureAwait(false);
                SetState(AgentState.Idle);
            }
        }

        private void SetState(AgentState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        private static byte[] ConvertToPcm16Mono(byte[] input, WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat &&
                format.BitsPerSample == 32)
            {
                int channels = format.Channels;
                int bytesPerSample = 4;
                int frameSize = channels * bytesPerSample;
                int frameCount = input.Length / frameSize;

                byte[] output = new byte[frameCount * 2];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float sum = 0;

                    for (int channel = 0; channel < channels; channel++)
                    {
                        int offset = frame * frameSize + channel * bytesPerSample;
                        float sample = BitConverter.ToSingle(input, offset);
                        sum += sample;
                    }

                    float mono = sum / channels;

                    if (mono > 1.0f) mono = 1.0f;
                    if (mono < -1.0f) mono = -1.0f;

                    short pcm16 = (short)(mono * 32767.0f);

                    int outputOffset = frame * 2;
                    output[outputOffset] = (byte)(pcm16 & 0xFF);
                    output[outputOffset + 1] = (byte)((pcm16 >> 8) & 0xFF);
                }

                return output;
            }

            throw new NotSupportedException(
                "Unsupported microphone format: " + format);
        }

        private static byte[] PcmToWav(
            byte[] pcm,
            int sampleRate,
            int channels,
            int bitsPerSample)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                int byteRate = sampleRate * channels * bitsPerSample / 8;
                short blockAlign = (short)(channels * bitsPerSample / 8);

                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + pcm.Length);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(sampleRate);
                writer.Write(byteRate);
                writer.Write(blockAlign);
                writer.Write((short)bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(pcm.Length);
                writer.Write(pcm);
                writer.Flush();
                return ms.ToArray();
            }
        }

        public void Dispose()
        {
            Stop();
            _capture.DataAvailable -= OnAudio;
            _capture.Dispose();
            _playback.Dispose();
            _openAi.Dispose();
        }
    }
}
