using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WpfVoiceAgent.Audio
{
    public sealed class RealtimeAudioPlayback : IDisposable
    {
        private const int OutputSampleRate = 48000;
        private const int OutputBits = 16;
        private const int OutputChannels = 1;

        // WaveOutEvent normally works well around this range.
        // This is device buffering, not a turn-completion delay.
        private const int OutputBufferMilliseconds = 100;

        private readonly object _gate = new object();
        private readonly IAecProcessor _aec;

        private WaveOutEvent _output;
        private BufferedWaveProvider _buffer;

        private bool _started;
        private bool _disposed;

        private long _responseGeneration;
        private bool _responseActive;

        public bool IsPlaying { get; private set; }

        public event EventHandler PlaybackStarted;
        public event EventHandler PlaybackStopped;

        public RealtimeAudioPlayback(
            IAecProcessor aec)
        {
            _aec = aec;
        }

        public void Start()
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (_started)
                    return;

                WaveFormat format =
                    new WaveFormat(
                        OutputSampleRate,
                        OutputBits,
                        OutputChannels);

                _buffer =
                    new BufferedWaveProvider(format)
                    {
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };

                _output =
                    new WaveOutEvent();


                _output.Init(_buffer);

                _output.PlaybackStopped +=
                    Output_PlaybackStopped;

                _output.Play();

                _started = true;
            }
        }

        public long BeginResponse()
        {
            lock (_gate)
            {
                ThrowIfDisposed();

                if (!_started)
                {
                    Start();
                }

                _responseGeneration++;

                _responseActive = true;

                IsPlaying = false;

                return _responseGeneration;
            }
        }

        public bool WritePcm(
            long generation,
            byte[] pcm24k)
        {
            if (pcm24k == null ||
                pcm24k.Length == 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_disposed)
                    return false;

                if (!_responseActive)
                    return false;

                if (generation != _responseGeneration)
                    return false;

                if (!_started)
                {
                    Start();
                }

                byte[] pcm48k =
                    PcmResampler.Resample16BitMono(
                        pcm24k,
                        24000,
                        OutputSampleRate);

                if (pcm48k.Length == 0)
                    return false;

                if (_aec != null)
                {
                    _aec.PushPlaybackReference(
                        pcm48k);
                }

                _buffer.AddSamples(
                    pcm48k,
                    0,
                    pcm48k.Length);

                if (!IsPlaying)
                {
                    IsPlaying = true;

                    PlaybackStarted?.Invoke(
                        this,
                        EventArgs.Empty);
                }

                return true;
            }
        }

        public async Task EndResponseAsync(
            long generation,
            CancellationToken cancellationToken)
        {
            int bufferedBytes;
            int sampleRate;
            int bytesPerSample;

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (generation != _responseGeneration)
                    return;

                if (!_responseActive)
                    return;

                bufferedBytes =
                    _buffer == null
                        ? 0
                        : _buffer.BufferedBytes;

                sampleRate =
                    _buffer == null
                        ? OutputSampleRate
                        : _buffer.WaveFormat.SampleRate;

                bytesPerSample =
                    _buffer == null
                        ? 2
                        : _buffer.WaveFormat.BlockAlign;
            }

            /*
             * BufferedWaveProvider is a live stream. Because ReadFully=true,
             * WaveOutEvent does not interpret a temporary empty buffer as
             * end-of-stream. Therefore response completion is explicitly
             * controlled here rather than by PlaybackStopped.
             *
             * First wait until all model audio has actually entered the
             * output device.
             */
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (_disposed)
                        return;

                    if (generation != _responseGeneration)
                        return;

                    if (!_responseActive)
                        return;

                    bufferedBytes =
                        _buffer == null
                            ? 0
                            : _buffer.BufferedBytes;

                    sampleRate =
                        _buffer == null
                            ? OutputSampleRate
                            : _buffer.WaveFormat.SampleRate;

                    bytesPerSample =
                        _buffer == null
                            ? 2
                            : _buffer.WaveFormat.BlockAlign;
                }

                if (bufferedBytes <= 0)
                    break;

                double bufferedMilliseconds =
                    bufferedBytes * 1000.0 /
                    (sampleRate * bytesPerSample);

                int delayMilliseconds =
                    (int)Math.Max(
                        10,
                        Math.Min(
                            50,
                            Math.Ceiling(
                                bufferedMilliseconds / 4.0)));

                await Task.Delay(
                    delayMilliseconds,
                    cancellationToken)
                    .ConfigureAwait(false);
            }

            /*
             * The final WaveOutEvent device buffer can still contain a small
             * amount of audio after BufferedBytes reaches zero.
             *
             * This is device latency, not an arbitrary conversation delay.
             */
            await Task.Delay(
                OutputBufferMilliseconds,
                cancellationToken)
                .ConfigureAwait(false);

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (generation != _responseGeneration)
                    return;

                if (!_responseActive)
                    return;

                _responseActive = false;
                IsPlaying = false;

                try
                {
                    _buffer?.ClearBuffer();
                }
                catch
                {
                }
            }

            PlaybackStopped?.Invoke(
                this,
                EventArgs.Empty);
        }

        public void Clear()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                try
                {
                    _buffer?.ClearBuffer();
                }
                catch
                {
                }

                IsPlaying = false;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _responseGeneration++;
                _responseActive = false;

                try
                {
                    _output?.Stop();
                }
                catch
                {
                }

                try
                {
                    _buffer?.ClearBuffer();
                }
                catch
                {
                }

                IsPlaying = false;
            }
        }

        private void Output_PlaybackStopped(
            object sender,
            StoppedEventArgs e)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
            }

            /*
             * Deliberately do not use PlaybackStopped as the logical
             * response-completion signal.
             *
             * OpenAI response.done + EndResponseAsync() owns that lifecycle.
             */
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(RealtimeAudioPlayback));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;

                _responseGeneration++;
                _responseActive = false;
                IsPlaying = false;

                try
                {
                    _output?.Stop();
                }
                catch
                {
                }

                if (_output != null)
                {
                    _output.PlaybackStopped -=
                        Output_PlaybackStopped;

                    try
                    {
                        _output.Dispose();
                    }
                    catch
                    {
                    }
                }

                _buffer = null;
                _output = null;

                _started = false;
            }
        }
    }
}