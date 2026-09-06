using System;
using System.Diagnostics;

namespace WpfVoiceAgent.Audio
{
    public sealed class EnergyVad
    {
        private readonly double _threshold;
        private readonly int _sampleRate;

        public EnergyVad(int sampleRate, double threshold = 0.015)
        {
            _sampleRate = sampleRate;
            _threshold = threshold;
        }

        public bool HasSpeech(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length < 2)
                return false;

            long sum = 0;
            int count = pcm16.Length / 2;

            for (int i = 0; i < pcm16.Length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(pcm16, i);
                sum += (long)sample * sample;
            }

            double rms =
                Math.Sqrt((double)sum / count) / 32768.0;

            bool speech = rms >= _threshold;

            Debug.WriteLine(
                "VAD RMS: " +
                rms.ToString("F4") +
                " | Threshold: " +
                _threshold.ToString("F4") +
                " | " +
                (speech ? "SPEECH" : "SILENCE"));

            return speech;
        }
    }
}