using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace WpfVoiceAgent.Audio
{
    public sealed class WasapiAudioPlayback : IAudioPlayback
    {
        private WaveOutEvent _output;
        private AudioFileReader _reader;

        private readonly IAecProcessor _aec;

        public bool IsPlaying { get; private set; }

        public event EventHandler PlaybackStarted;
        public event EventHandler PlaybackStopped;

        public WasapiAudioPlayback()
            : this(null)
        {
        }

        public WasapiAudioPlayback(IAecProcessor aec)
        {
            _aec = aec;
        }

        public async Task PlayWavAsync(
            byte[] wavBytes,
            CancellationToken cancellationToken)
        {
            Stop();

            string path = Path.Combine(
                Path.GetTempPath(),
                "wpf_voice_agent_" +
                Guid.NewGuid().ToString("N") +
                ".wav");

            File.WriteAllBytes(path, wavBytes);

            try
            {
                var tcs = new TaskCompletionSource<bool>();

                _reader = new AudioFileReader(path);

                var referenceProvider =
                    new AecReferenceSampleProvider(
                        _reader,
                        _aec);

                _output = new WaveOutEvent();

                _output.Init(referenceProvider);

                _output.PlaybackStopped += (s, e) =>
                {
                    IsPlaying = false;

                    PlaybackStopped?.Invoke(
                        this,
                        EventArgs.Empty);

                    tcs.TrySetResult(true);
                };

                IsPlaying = true;

                PlaybackStarted?.Invoke(
                    this,
                    EventArgs.Empty);

                _output.Play();

                using (cancellationToken.Register(() =>
                {
                    try
                    {
                        _output?.Stop();
                    }
                    catch
                    {
                    }

                    tcs.TrySetCanceled();
                }))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                IsPlaying = false;

                try
                {
                    _output?.Stop();
                }
                catch
                {
                }

                _output?.Dispose();
                _reader?.Dispose();

                _output = null;
                _reader = null;

                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
        }

        public void Stop()
        {
            try
            {
                _output?.Stop();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private sealed class AecReferenceSampleProvider
            : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly IAecProcessor _aec;

            private readonly float[] _buffer;
            private readonly byte[] _pcmBuffer;

            public WaveFormat WaveFormat
            {
                get { return _source.WaveFormat; }
            }

            public AecReferenceSampleProvider(
                ISampleProvider source,
                IAecProcessor aec)
            {
                _source = source;
                _aec = aec;

                _buffer = new float[4096];
                _pcmBuffer = new byte[4096 * 2];
            }

            public int Read(
                float[] buffer,
                int offset,
                int count)
            {
                int samplesRead =
                    _source.Read(
                        _buffer,
                        0,
                        Math.Min(
                            count,
                            _buffer.Length));

                if (samplesRead <= 0)
                    return 0;

                int channels =
                    _source.WaveFormat.Channels;

                int frames =
                    samplesRead / channels;

                int pcmIndex = 0;

                for (int frame = 0;
                     frame < frames;
                     frame++)
                {
                    float sum = 0.0f;

                    for (int channel = 0;
                         channel < channels;
                         channel++)
                    {
                        sum +=
                            _buffer[
                                frame * channels +
                                channel];
                    }

                    float mono =
                        sum / channels;

                    if (mono > 1.0f)
                        mono = 1.0f;

                    if (mono < -1.0f)
                        mono = -1.0f;

                    short pcm =
                        (short)(mono * 32767.0f);

                    _pcmBuffer[pcmIndex++] =
                        (byte)(pcm & 0xFF);

                    _pcmBuffer[pcmIndex++] =
                        (byte)((pcm >> 8) & 0xFF);
                }

                if (pcmIndex > 0)
                {
                    byte[] reference =
                        new byte[pcmIndex];

                    Buffer.BlockCopy(
                        _pcmBuffer,
                        0,
                        reference,
                        0,
                        pcmIndex);

                    _aec?.PushPlaybackReference(
                        reference);
                }

                Buffer.BlockCopy(
                    _buffer,
                    0,
                    buffer,
                    offset * sizeof(float),
                    samplesRead * sizeof(float));

                return samplesRead;
            }
        }
    }
}