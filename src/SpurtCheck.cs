using System;

namespace TavernVoice
{
    internal enum Verdict { Passed, PassedLate, OverLimit, OutOfOrder }

    internal sealed class SpurtCheck
    {
        private const int SessionRestartSpurtJump = 16;
        private const double SessionRestartGapSeconds = 10.0;
        private const int LateFrames = 12;
        private ushort talkspurt;
        private ushort sequence;
        private ulong seen;
        private bool initialized;
        private double tokens = 55.0;
        private double lastTime;
        internal static long Accepted;
        internal static long Late;
        internal static long BucketDrops;
        internal static long OrderDrops;

        internal SpurtCheck(double now) => lastTime = now;

        internal Verdict Accept(ushort spurt, ushort seq, double now)
        {
            Verdict verdict = Judge(spurt, seq, now);
            switch (verdict)
            {
                case Verdict.Passed: Accepted++; break;
                case Verdict.PassedLate: Accepted++; Late++; break;
                case Verdict.OverLimit: BucketDrops++; break;
                default: OrderDrops++; break;
            }
            return verdict;
        }

        private Verdict Judge(ushort spurt, ushort seq, double now)
        {
            double gap = now - lastTime;
            tokens = Math.Min(55.0, tokens + gap * 50.0);
            lastTime = now;
            if (tokens < 1.0)
                return Verdict.OverLimit;
            tokens -= 1.0;
            if (!initialized || spurt != talkspurt)
            {
                short back = (short)(spurt - talkspurt);
                if ((initialized && back <= 0 && back > -SessionRestartSpurtJump && gap <= SessionRestartGapSeconds) || seq >= 25)
                    return Verdict.OutOfOrder;
                initialized = true;
                talkspurt = spurt;
                sequence = seq;
                seen = 1;
                return Verdict.Passed;
            }
            int advance = (ushort)(seq - sequence);
            if (advance == 0)
                return Verdict.OutOfOrder;
            if (advance < 0x8000)
            {
                seen = advance >= 64 ? 0 : seen << advance;
                seen |= 1;
                sequence = seq;
                return Verdict.Passed;
            }
            int behind = (ushort)(sequence - seq);
            if (behind > LateFrames)
                return Verdict.OutOfOrder;
            ulong bit = 1UL << behind;
            if ((seen & bit) != 0)
                return Verdict.OutOfOrder;
            seen |= bit;
            return Verdict.PassedLate;
        }
    }
}
