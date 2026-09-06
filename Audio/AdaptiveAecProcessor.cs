using System;

namespace WpfVoiceAgent.Audio
{
    /// <summary>
    /// Lightweight adaptive acoustic echo canceller.
    ///
    /// Input:
    ///   16-bit PCM mono microphone audio.
    ///
    /// Reference:
    ///   16-bit PCM mono speaker/render audio.
    ///
    /// This uses a normalized LMS adaptive filter to estimate
    /// the acoustic path from the speakers to the microphone.
    /// </summary>
    public sealed class AdaptiveAecProcessor : IAecProcessor
    {
        private const int FilterLength = 2048;
        private const float StepSize = 0.08f;
        private const float Regularization = 1e-6f;

        private readonly float[] _filter;
        private readonly float[] _referenceHistory;

        private readonly object _gate = new object();

        private int _referenceWritePosition;

        public AdaptiveAecProcessor()
        {
            _filter = new float[FilterLength];
            _referenceHistory = new float[FilterLength];
        }

        public byte[] ProcessCapture(byte[] microphonePcm)
        {
            if (microphonePcm == null || microphonePcm.Length < 2)
                return microphonePcm;

            int sampleCount = microphonePcm.Length / 2;
            byte[] output = new byte[microphonePcm.Length];

            lock (_gate)
            {
                for (int n = 0; n < sampleCount; n++)
                {
                    short micSample = BitConverter.ToInt16(
                        microphonePcm,
                        n * 2);

                    float microphone = micSample / 32768.0f;

                    float echoEstimate = 0.0f;
                    float referenceEnergy = Regularization;

                    int position = _referenceWritePosition;

                    for (int i = 0; i < FilterLength; i++)
                    {
                        float reference = _referenceHistory[position];

                        echoEstimate += _filter[i] * reference;
                        referenceEnergy += reference * reference;

                        position--;

                        if (position < 0)
                            position = FilterLength - 1;
                    }

                    float error = microphone - echoEstimate;

                    // Update the adaptive filter.
                    float adaptation = StepSize * error / referenceEnergy;

                    position = _referenceWritePosition;

                    for (int i = 0; i < FilterLength; i++)
                    {
                        _filter[i] += adaptation * _referenceHistory[position];

                        position--;

                        if (position < 0)
                            position = FilterLength - 1;
                    }

                    // Keep the residual microphone signal.
                    if (error > 1.0f)
                        error = 1.0f;

                    if (error < -1.0f)
                        error = -1.0f;

                    short outputSample =
                        (short)(error * 32767.0f);

                    output[n * 2] =
                        (byte)(outputSample & 0xFF);

                    output[n * 2 + 1] =
                        (byte)((outputSample >> 8) & 0xFF);
                }
            }

            return output;
        }

        public void PushPlaybackReference(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2)
                return;

            int sampleCount = pcm.Length / 2;

            lock (_gate)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    short sample = BitConverter.ToInt16(
                        pcm,
                        i * 2);

                    _referenceHistory[_referenceWritePosition] =
                        sample / 32768.0f;

                    _referenceWritePosition++;

                    if (_referenceWritePosition >= FilterLength)
                        _referenceWritePosition = 0;
                }
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                Array.Clear(_filter, 0, _filter.Length);
                Array.Clear(
                    _referenceHistory,
                    0,
                    _referenceHistory.Length);

                _referenceWritePosition = 0;
            }
        }
    }
}