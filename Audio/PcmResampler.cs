using System;

namespace WpfVoiceAgent.Audio
{
    public static class PcmResampler
    {
        public static byte[] Resample16BitMono(
            byte[] input,
            int inputSampleRate,
            int outputSampleRate)
        {
            if (input == null || input.Length < 4)
                return new byte[0];

            if (inputSampleRate == outputSampleRate)
                return input;

            int inputSamples = input.Length / 2;

            if (inputSamples < 2)
                return new byte[0];

            int outputSamples =
                (int)Math.Round(
                    inputSamples *
                    (double)outputSampleRate /
                    inputSampleRate);

            if (outputSamples < 1)
                return new byte[0];

            byte[] output =
                new byte[outputSamples * 2];

            for (int i = 0; i < outputSamples; i++)
            {
                double sourcePosition =
                    i *
                    (double)inputSampleRate /
                    outputSampleRate;

                int index =
                    (int)sourcePosition;

                double fraction =
                    sourcePosition - index;

                if (index >= inputSamples - 1)
                {
                    index = inputSamples - 2;
                    fraction = 1.0;
                }

                short sample1 =
                    BitConverter.ToInt16(
                        input,
                        index * 2);

                short sample2 =
                    BitConverter.ToInt16(
                        input,
                        (index + 1) * 2);

                double sample =
                    sample1 +
                    (sample2 - sample1) *
                    fraction;

                if (sample > short.MaxValue)
                    sample = short.MaxValue;

                if (sample < short.MinValue)
                    sample = short.MinValue;

                short result =
                    (short)sample;

                output[i * 2] =
                    (byte)(result & 0xFF);

                output[i * 2 + 1] =
                    (byte)((result >> 8) & 0xFF);
            }

            return output;
        }
    }
}