using System;

namespace WpfVoiceAgent.Audio
{
    public sealed class MultiSpeakerOverlapDetector
    {
        private const int FrameDurationMs = 20;

        private readonly int _sampleRate;

        public MultiSpeakerOverlapDetector(int sampleRate)
        {
            _sampleRate = Math.Max(8000, sampleRate);
        }

        public bool HasProbableOverlap(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length < 4)
                return false;

            int samplesPerFrame =
                Math.Max(80, _sampleRate * FrameDurationMs / 1000);

            int totalSamples = pcm16.Length / 2;

            int suspiciousFrames = 0;
            int speechFrames = 0;

            for (int start = 0;
                 start + samplesPerFrame <= totalSamples;
                 start += samplesPerFrame)
            {
                FrameFeatures features =
                    AnalyzeFrame(
                        pcm16,
                        start,
                        samplesPerFrame);

                if (features.Rms < 0.015)
                    continue;

                speechFrames++;

                /*
                 * This is deliberately conservative.
                 *
                 * A single human voice normally has a reasonably
                 * coherent spectral structure. Simultaneous voices
                 * can produce a broader/more complex spectrum.
                 *
                 * This is only a heuristic. It does NOT identify
                 * individual speakers.
                 */
                if (features.HighFrequencyRatio > 0.72 &&
                    features.ZeroCrossingRate > 0.18)
                {
                    suspiciousFrames++;
                }
            }

            if (speechFrames < 5)
                return false;

            /*
             * Require a significant portion of the utterance to
             * look suspicious before rejecting it.
             */
            double suspiciousRatio =
                (double)suspiciousFrames / speechFrames;

            return suspiciousRatio >= 0.35;
        }

        private static FrameFeatures AnalyzeFrame(
            byte[] pcm16,
            int startSample,
            int sampleCount)
        {
            double sumSquares = 0.0;
            int zeroCrossings = 0;

            short previous =
                BitConverter.ToInt16(
                    pcm16,
                    startSample * 2);

            for (int i = 0; i < sampleCount; i++)
            {
                short current =
                    BitConverter.ToInt16(
                        pcm16,
                        (startSample + i) * 2);

                double normalized =
                    current / 32768.0;

                sumSquares +=
                    normalized * normalized;

                if ((previous < 0 && current >= 0) ||
                    (previous >= 0 && current < 0))
                {
                    zeroCrossings++;
                }

                previous = current;
            }

            double rms =
                Math.Sqrt(sumSquares / sampleCount);

            double zeroCrossingRate =
                (double)zeroCrossings / sampleCount;

            /*
             * Estimate high-frequency content using
             * first differences.
             */
            double totalEnergy = 0.0;
            double highFrequencyEnergy = 0.0;

            previous =
                BitConverter.ToInt16(
                    pcm16,
                    startSample * 2);

            for (int i = 0; i < sampleCount; i++)
            {
                short current =
                    BitConverter.ToInt16(
                        pcm16,
                        (startSample + i) * 2);

                double sample =
                    current / 32768.0;

                double difference =
                    (current - previous) / 32768.0;

                totalEnergy += sample * sample;
                highFrequencyEnergy +=
                    difference * difference;

                previous = current;
            }

            double highFrequencyRatio =
                totalEnergy > 0.000001
                    ? highFrequencyEnergy / totalEnergy
                    : 0.0;

            return new FrameFeatures
            {
                Rms = rms,
                ZeroCrossingRate = zeroCrossingRate,
                HighFrequencyRatio = highFrequencyRatio
            };
        }

        private sealed class FrameFeatures
        {
            public double Rms { get; set; }
            public double ZeroCrossingRate { get; set; }
            public double HighFrequencyRatio { get; set; }
        }
    }
}