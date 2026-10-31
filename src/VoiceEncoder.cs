using System;
using Concentus.Enums;
using Concentus.Structs;

namespace TavernVoice
{
    internal sealed class VoiceEncoder
    {
        internal const int SampleRate = 48000;
        internal const int FrameSamples = 960;

        private const float OpenThreshold = 0.012f;
        private const float CloseThreshold = 0.008f;
        private const int HangoverFrames = 20;

        private const float TargetRms = 0.3f;
        private const float MaximumAgcGain = 16f;
        private const float GainRiseRate = 0.05f;
        private const float GainFallRate = 0.3f;
        private readonly OpusEncoder encoder;
        private readonly PeakLimiter limiter = new PeakLimiter();
        private readonly float[] gained = new float[FrameSamples];
        private readonly short[] pcm = new short[FrameSamples];
        private readonly byte[] encoded = new byte[VoiceProtocol.MaximumPayload];
        private bool talking;
        private int hangover;
        private float agcGain = 1f;

        internal VoiceEncoder()
        {
            encoder = OpusEncoder.Create(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 24000;
            encoder.UseVBR = true;
            encoder.Complexity = 5;
            encoder.UseInbandFEC = true;
            encoder.PacketLossPercent = 10;
            encoder.UseDTX = false;
        }

        internal static void SelfTest()
        {
            var testEncoder = new VoiceEncoder();
            var input = new float[FrameSamples];
            for (int i = 0; i < input.Length; i++)
                input[i] = (float)Math.Sin(i * 2.0 * Math.PI * 440.0 / SampleRate) * 0.25f;
            if (!testEncoder.Encode(input, out byte[] payload, out _, out _) || payload.Length == 0)
                throw new InvalidOperationException("Opus self-test produced no packet");
            var testDecoder = OpusDecoder.Create(SampleRate, 1);
            var output = new short[FrameSamples];
            int samples = testDecoder.Decode(payload, 0, payload.Length, output, 0, FrameSamples, false);
            if (samples != FrameSamples || Array.TrueForAll(output, value => value == 0))
                throw new InvalidOperationException("Opus self-test produced invalid PCM");
        }

        internal void ResetTalkspurt()
        {
            talking = false;
            hangover = 0;
        }

        internal bool Encode(float[] frame, out byte[] payload, out bool newTalkspurt, out float energy)
        {
            double sum = 0;
            for (int i = 0; i < FrameSamples; i++)
            {
                float value = Math.Max(-1f, Math.Min(1f, frame[i]));
                sum += value * value;
            }
            float rawEnergy = (float)Math.Sqrt(sum / FrameSamples);

            newTalkspurt = false;
            if (rawEnergy >= OpenThreshold)
            {
                newTalkspurt = !talking;
                talking = true;
                hangover = HangoverFrames;
            }
            else if (talking && rawEnergy < CloseThreshold && --hangover <= 0)
            {
                talking = false;
            }
            if (!talking)
            {
                payload = null;
                energy = rawEnergy;
                return false;
            }

            if (rawEnergy >= OpenThreshold)
            {
                float desired = Math.Min(MaximumAgcGain, Math.Max(1f, TargetRms / rawEnergy));
                agcGain += (desired - agcGain) * (desired < agcGain ? GainFallRate : GainRiseRate);
            }
            for (int i = 0; i < FrameSamples; i++)
                gained[i] = frame[i] * agcGain;
            limiter.Process(gained, FrameSamples);
            for (int i = 0; i < FrameSamples; i++)
                pcm[i] = (short)(gained[i] * short.MaxValue);
            energy = Math.Min(1f, rawEnergy * agcGain);

            int length = encoder.Encode(pcm, 0, FrameSamples, encoded, 0, encoded.Length);
            payload = new byte[length];
            Buffer.BlockCopy(encoded, 0, payload, 0, length);
            return true;
        }
    }
}
