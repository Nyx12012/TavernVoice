using System;

namespace TavernVoice
{

    internal sealed class PeakLimiter
    {
        private const float Ceiling = 0.98f;
        private const float ReleasePerSample = 0.00026f;
        private float gain = 1f;

        internal void Process(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                float magnitude = Math.Abs(samples[i]);
                if (magnitude * gain > Ceiling)
                    gain = Ceiling / magnitude;
                else if (gain < 1f)
                    gain = Math.Min(1f, gain + (1f - gain) * ReleasePerSample);
                samples[i] *= gain;
            }
        }
    }
}
