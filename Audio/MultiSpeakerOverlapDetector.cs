using System;

namespace WpfVoiceAgent.Audio
{
    public sealed class MultiSpeakerOverlapDetector
    {
        private const int FrameDurationMs = 20;

        private readonly int _sampleRate;

        private int _speechFrames;
        private int _suspiciousFrames;
        private int _consecutiveSuspiciousFrames;

        public MultiSpeakerOverlapDetector(int sampleRate)
        {
            _sampleRate =
                Math.Max(8000, sampleRate);
        }

        public bool IsBlocked
        {
            get;
            private set;
        }

        public void Reset()
        {
            _speechFrames = 0;
            _suspiciousFrames = 0;
            _consecutiveSuspiciousFrames = 0;

            IsBlocked = false;
        }

        public bool Process(
            byte[] pcm16)
        {
            if (IsBlocked)
                return true;

            if (pcm16 == null ||
                pcm16.Length < 4)
            {
                return false;
            }

            int samplesPerFrame =
                Math.Max(
                    80,
                    _sampleRate *
                    FrameDurationMs /
                    1000);

            int totalSamples =
                pcm16.Length / 2;

            for (
                int start = 0;
                start + samplesPerFrame <= totalSamples;
                start += samplesPerFrame)
            {
                FrameFeatures features =
                    AnalyzeFrame(
                        pcm16,
                        start,
                        samplesPerFrame);

                /*
                 * Ignore silence.
                 */
                if (features.Rms < 0.015)
                {
                    _consecutiveSuspiciousFrames = 0;
                    continue;
                }

                _speechFrames++;

                bool suspicious =
                    IsSuspiciousFrame(
                        features);

                if (suspicious)
                {
                    _suspiciousFrames++;
                    _consecutiveSuspiciousFrames++;
                }
                else
                {
                    _consecutiveSuspiciousFrames = 0;
                }

                /*
                 * Require roughly 160 ms of continuous
                 * suspicious speech before rejecting.
                 */
                if (_consecutiveSuspiciousFrames >= 8)
                {
                    IsBlocked = true;
                    return true;
                }

                /*
                 * Long-term threshold.
                 *
                 * 20 frames = approximately 400 ms.
                 */
                if (_speechFrames >= 20)
                {
                    double ratio =
                        (double)_suspiciousFrames /
                        _speechFrames;

                    if (ratio >= 0.45)
                    {
                        IsBlocked = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsSuspiciousFrame(
            FrameFeatures features)
        {
            /*
             * Conservative heuristic.
             *
             * This does NOT identify speakers.
             * It only detects acoustic conditions that
             * are compatible with overlapping speech.
             */
            return
                features.Rms > 0.025 &&
                features.ZeroCrossingRate > 0.16 &&
                features.HighFrequencyRatio > 0.60;
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

            for (
                int i = 0;
                i < sampleCount;
                i++)
            {
                short current =
                    BitConverter.ToInt16(
                        pcm16,
                        (startSample + i) * 2);

                double sample =
                    current / 32768.0;

                sumSquares +=
                    sample * sample;

                if (
                    (previous < 0 &&
                     current >= 0) ||
                    (previous >= 0 &&
                     current < 0))
                {
                    zeroCrossings++;
                }

                previous = current;
            }

            double rms =
                Math.Sqrt(
                    sumSquares /
                    sampleCount);

            double zeroCrossingRate =
                (double)zeroCrossings /
                sampleCount;

            /*
             * First-difference energy.
             */
            double totalEnergy = 0.0;
            double highFrequencyEnergy = 0.0;

            previous =
                BitConverter.ToInt16(
                    pcm16,
                    startSample * 2);

            for (
                int i = 0;
                i < sampleCount;
                i++)
            {
                short current =
                    BitConverter.ToInt16(
                        pcm16,
                        (startSample + i) * 2);

                double sample =
                    current / 32768.0;

                double difference =
                    (current - previous) /
                    32768.0;

                totalEnergy +=
                    sample * sample;

                highFrequencyEnergy +=
                    difference * difference;

                previous = current;
            }

            double highFrequencyRatio =
                totalEnergy > 0.000001
                    ? highFrequencyEnergy /
                      totalEnergy
                    : 0.0;

            return new FrameFeatures
            {
                Rms = rms,

                ZeroCrossingRate =
                    zeroCrossingRate,

                HighFrequencyRatio =
                    highFrequencyRatio
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