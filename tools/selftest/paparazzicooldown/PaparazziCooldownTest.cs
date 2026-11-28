using System;
using Ts3Fixes;

// Offline self-check of PaparazziCooldownCore (docs/648).
static class PaparazziCooldownTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const long H = 100;  // ticks per sim-hour in this test

    static void Main()
    {
        bool first;

        // pure skip rule
        Check(!PaparazziCooldownCore.Skip(false, false, true, 5f, 0f, 10f), "not eligible: EA runs");
        Check(!PaparazziCooldownCore.Skip(true, true, true, 5f, 0f, 10f), "exit due: EA runs");
        Check(!PaparazziCooldownCore.Skip(true, false, false, 5f, 0f, 10f), "no earlier run: EA runs");
        Check(PaparazziCooldownCore.Skip(true, false, true, 9.9f, 0f, 10f), "9.9 min after the last run: skip");
        Check(!PaparazziCooldownCore.Skip(true, false, true, 10f, 0f, 10f), "10 min after the last run: EA runs");
        Check(!PaparazziCooldownCore.Skip(true, false, true, 3f, 5f, 10f), "lifetime went back: EA runs");
        Check(!PaparazziCooldownCore.Skip(true, false, true, 5f, 0f, 0f), "stretch 0: EA runs");

        // a 40-minute unseen follow with a callback each sim-minute: EA's body at 1, 11, 21, 31 (4 runs), then the due exit at 41
        PaparazziCooldownCore c = new PaparazziCooldownCore();
        int runs = 0, skips = 0, firsts = 0;
        for (int m = 1; m <= 40; m++)
        {
            int v = c.Callback(5, 77, m, true, false, 10f, out first);
            if (v == PaparazziCooldownCore.kRun) runs++; else skips++;
            if (first) firsts++;
        }
        Check(runs == 4 && skips == 36 && firsts == 1, "unseen follow: 4 EA runs, 36 skips, one first-stretch log (got " + runs + "/" + skips + "/" + firsts + ")");
        Check(c.Callback(5, 77, 41, true, true, 10f, out first) == PaparazziCooldownCore.kRun, "due exit runs EA's body");
        // seen: every callback runs
        Check(c.Callback(5, 77, 42, false, false, 10f, out first) == PaparazziCooldownCore.kRun
            && c.Callback(5, 77, 43, false, false, 10f, out first) == PaparazziCooldownCore.kRun, "seen: EA every minute");
        // a new instance starts over (its first callback runs)
        Check(c.Callback(5, 78, 1, true, false, 10f, out first) == PaparazziCooldownCore.kRun, "new follow instance: first callback runs");
        Check(c.Callback(5, 78, 2, true, false, 10f, out first) == PaparazziCooldownCore.kSkip && first, "new follow instance: first skip logged again");

        // cooldown
        PaparazziCooldownCore d = new PaparazziCooldownCore();
        Check(!d.Cooling(1, 2, 0, 2 * H), "never followed: allowed");
        d.Ended(1, 2, 100);
        Check(d.Cooling(1, 2, 100, 2 * H) && d.Cooling(1, 2, 299, 2 * H), "same pair within 2 h: refused");
        Check(d.Left(1, 2, 150, 2 * H) == 150, "time left");
        Check(!d.Cooling(1, 3, 150, 2 * H) && !d.Cooling(4, 2, 150, 2 * H), "another celebrity or paparazzo: allowed");
        Check(!d.Cooling(1, 2, 300, 2 * H) && d.EndedCount == 0, "after 2 h: allowed, entry gone");
        d.Ended(1, 2, 100);
        Check(!d.Cooling(1, 2, 50, 2 * H), "clock back: allowed");
        d.Ended(1, 2, 100);
        Check(!d.Cooling(1, 2, 100, 0), "cooldown 0: allowed");
        // Ended clears the follow's stretch state
        d.Callback(9, 1, 1, true, false, 10f, out first);
        d.Ended(9, 2, 0);
        Check(d.Callback(9, 1, 2, true, false, 10f, out first) == PaparazziCooldownCore.kRun, "after an end the same token runs EA first");

        // bounded
        PaparazziCooldownCore b = new PaparazziCooldownCore();
        for (ulong i = 1; i <= 2000; i++) { b.Ended(i, i + 1, (long)i); b.Callback(i, 1, 1f, true, false, 10f, out first); }
        Check(b.EndedCount <= PaparazziCooldownCore.kMax, "ended table bounded (" + b.EndedCount + ")");
        Check(b.Cooling(2000, 2001, 2000, 2 * H), "newest entry kept after pruning");

        if (sFails == 0) Console.WriteLine("paparazzicooldown selftest: OK");
        else { Console.WriteLine("paparazzicooldown selftest: " + sFails + " FAILED"); Environment.Exit(1); }
    }
}
