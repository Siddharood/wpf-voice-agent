using System;

namespace WpfVoiceAgent.Audio
{
    public sealed class SpeakerChangeDetector
    {
        private readonly double _threshold;

        private float[] _referenceEmbedding;

        private int _consecutiveDifferentSegments;

        public SpeakerChangeDetector(
            double threshold = 0.70)
        {
            _threshold =
                Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        threshold));
        }

        public bool IsLocked
        {
            get
            {
                return _referenceEmbedding != null;
            }
        }

        public void Reset()
        {
            _referenceEmbedding = null;

            _consecutiveDifferentSegments = 0;
        }

        public void SetReference(
            float[] embedding)
        {
            if (embedding == null ||
                embedding.Length == 0)
            {
                return;
            }

            _referenceEmbedding =
                Normalize(
                    embedding);

            _consecutiveDifferentSegments = 0;
        }

        public SpeakerComparisonResult Compare(
            float[] embedding)
        {
            if (embedding == null ||
                embedding.Length == 0)
            {
                return SpeakerComparisonResult.Unknown;
            }

            if (_referenceEmbedding == null)
            {
                SetReference(
                    embedding);

                return SpeakerComparisonResult.SameSpeaker;
            }

            float[] normalized =
                Normalize(
                    embedding);

            double similarity =
                CosineSimilarity(
                    _referenceEmbedding,
                    normalized);

            /*
             * Hysteresis.
             *
             * One bad embedding isn't enough to reject
             * the conversation.
             */
            if (similarity < _threshold)
            {
                _consecutiveDifferentSegments++;

                /*
                 * Require two consecutive different
                 * speech windows.
                 */
                if (_consecutiveDifferentSegments >= 2)
                {
                    return SpeakerComparisonResult.DifferentSpeaker;
                }

                return SpeakerComparisonResult.PossiblyDifferentSpeaker;
            }

            _consecutiveDifferentSegments = 0;

            return SpeakerComparisonResult.SameSpeaker;
        }

        public double GetSimilarity(
            float[] embedding)
        {
            if (_referenceEmbedding == null ||
                embedding == null ||
                embedding.Length == 0)
            {
                return 0.0;
            }

            return CosineSimilarity(
                _referenceEmbedding,
                Normalize(embedding));
        }

        private static float[] Normalize(
            float[] input)
        {
            float[] output =
                new float[input.Length];

            double sum = 0.0;

            for (
                int i = 0;
                i < input.Length;
                i++)
            {
                sum +=
                    input[i] *
                    input[i];
            }

            double norm =
                Math.Sqrt(sum);

            if (norm < 0.000001)
                return output;

            for (
                int i = 0;
                i < input.Length;
                i++)
            {
                output[i] =
                    (float)(
                        input[i] /
                        norm);
            }

            return output;
        }

        private static double CosineSimilarity(
            float[] a,
            float[] b)
        {
            if (a == null ||
                b == null ||
                a.Length == 0 ||
                a.Length != b.Length)
            {
                return 0.0;
            }

            double dot = 0.0;

            for (
                int i = 0;
                i < a.Length;
                i++)
            {
                dot +=
                    a[i] *
                    b[i];
            }

            return dot;
        }
    }

    public enum SpeakerComparisonResult
    {
        Unknown,

        SameSpeaker,

        PossiblyDifferentSpeaker,

        DifferentSpeaker
    }
}