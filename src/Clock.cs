using System.Diagnostics;

namespace TavernVoice
{
    internal static class Clock
    {
        internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }
}
