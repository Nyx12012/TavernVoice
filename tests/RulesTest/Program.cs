using System;
using System.Collections.Generic;
using TavernVoice;

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        Sequences();
        Bucket();
        Console.WriteLine(failures == 0 ? "rules ok" : failures + " rule check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    private static void Sequences()
    {
        var cases = new Dictionary<string, (int spurt, int seq, double after, bool want)[]>
        {
            ["first packet near the start"] = new[] { (5, 3, 0.0, true) },
            ["first packet late in a spurt"] = new[] { (5, 25, 0.0, false) },
            ["a jump forward resyncs"] = new[] { (5, 0, 0.0, true), (5, 300, 0.02, true), (5, 301, 0.02, true) },
            ["a packet from before is old"] = new[] { (5, 300, 0.0, false), (5, 10, 0.0, true), (5, 400, 1.0, true), (5, 387, 0.0, false) },
            ["one frame late passes"] = new[] { (5, 300, 0.0, false), (5, 10, 0.0, true), (5, 400, 1.0, true), (5, 399, 0.0, true) },
            ["late passes once"] = new[] { (5, 0, 0.0, true), (5, 2, 0.0, true), (5, 1, 0.0, true), (5, 1, 0.0, false), (5, 2, 0.0, false) },
            ["twelve late passes"] = new[] { (5, 0, 0.0, true), (5, 20, 0.0, true), (5, 8, 0.0, true), (5, 7, 0.0, false) },
            ["late across a jump"] = new[] { (5, 0, 0.0, true), (5, 100, 0.0, true), (5, 99, 0.0, true), (5, 0, 0.0, false) },
            ["late at a spurt start"] = new[] { (5, 1, 0.0, true), (5, 0, 0.0, true), (5, 2, 0.0, true) },
            ["late across the wrap"] = new[] { (5, 0, 0.0, true), (5, 20000, 0.0, true), (5, 40000, 0.0, true), (5, 60000, 0.0, true), (5, 65535, 0.0, true), (5, 1, 0.0, true), (5, 0, 0.0, true), (5, 65535, 0.0, false), (5, 65534, 0.0, true) },
            ["duplicate"] = new[] { (5, 4, 0.0, true), (5, 4, 0.02, false) },
            ["sequence wraps"] = new[] { (5, 0, 0.0, true), (5, 40, 1.0, true), (5, 65530, 0.0, false) },
            ["new spurt"] = new[] { (5, 20, 0.0, true), (6, 0, 1.0, true) },
            ["new spurt starting late"] = new[] { (5, 20, 0.0, true), (6, 30, 1.0, false) },
            ["spurt wraps"] = new[] { (65535, 0, 0.0, true), (0, 0, 1.0, true) },
            ["older spurt soon after"] = new[] { (20, 0, 0.0, true), (19, 0, 1.0, false) },
            ["older spurt after 10 s"] = new[] { (20, 0, 0.0, true), (10, 0, 10.001, true) },
            ["spurt 16 back resyncs"] = new[] { (20, 0, 0.0, true), (4, 0, 1.0, true) },
            ["spurt 15 back is refused"] = new[] { (20, 0, 0.0, true), (5, 0, 1.0, false) },
        };
        foreach (KeyValuePair<string, (int spurt, int seq, double after, bool want)[]> c in cases)
        {
            double now = 1000.0;
            var check = new SpurtCheck(now);
            for (int i = 0; i < c.Value.Length; i++)
            {
                var step = c.Value[i];
                now += step.after;
                bool got = check.Accept((ushort)step.spurt, (ushort)step.seq, now) <= Verdict.PassedLate;
                if (got != step.want)
                {
                    Console.WriteLine($"{c.Key}, step {i} (spurt {step.spurt} seq {step.seq}): got {got}");
                    failures++;
                }
            }
        }
    }

    private static void Bucket()
    {
        var check = new SpurtCheck(0.0);
        int accepted = 0;
        for (int i = 0; i < 100; i++)
            if (check.Accept(1, (ushort)i, 0.0) <= Verdict.PassedLate)
                accepted++;
        if (accepted != 55)
        {
            Console.WriteLine($"a burst passes 55, got {accepted}");
            failures++;
        }
        var steady = new SpurtCheck(0.0);
        for (int i = 0; i < 50 * 120; i++)
        {
            if (steady.Accept(1, (ushort)i, (i + 1) * 0.02) > Verdict.PassedLate)
            {
                Console.WriteLine($"frame {i} of steady 50/s talk refused");
                failures++;
                break;
            }
        }
    }
}
