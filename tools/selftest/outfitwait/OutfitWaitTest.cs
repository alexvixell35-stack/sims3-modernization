using System;
using Ts3Fixes;

// Offline self-check of OutfitWaitCore (issue #115): a simulated wait loop, ready at a random tick or never. Off: every
// tick is polled (EA's loop). On: the wait sees ready at the same tick as EA's before the stale mark and at most one
// sim-minute later after it, and a wait that never gets ready wakes once per sim-minute after the mark.
static class OutfitWaitTest
{
    const long kStale = 60 * 37;   // 60 sim-minutes at 37.5 ticks per sim-minute, as SimClock.ConvertToTicks truncates
    const uint kMinute = 37;
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    // ticks from the wait's start to the poll that sees ready (or the horizon), and the wakes taken
    static long Run(long readyAt, long horizon, bool on, out int wakes)
    {
        long t = 0;
        wakes = 0;
        while (t < readyAt && t < horizon)
        {
            t += OutfitWaitCore.SleepTicks(t, kStale, kMinute, on);
            wakes++;
        }
        return t;
    }

    static void Main()
    {
        Random rnd = new Random(115);
        for (int round = 0; round < 5000; round++)
        {
            long readyAt = rnd.Next(4) == 0 ? rnd.Next(0, 40) : rnd.Next(0, 20000);
            int wOff, wOn;
            long off = Run(readyAt, long.MaxValue, false, out wOff);
            long on = Run(readyAt, long.MaxValue, true, out wOn);
            Check(off == readyAt, "off polls every tick " + readyAt);
            if (readyAt <= kStale) Check(on == readyAt, "on before the mark is EA's " + readyAt);
            else Check(on >= readyAt && on - readyAt < kMinute, "on after the mark within a sim-minute " + readyAt + " -> " + on);
            Check(wOn <= wOff, "on never wakes more " + readyAt);
        }
        int wakesOff, wakesOn;
        long day = 24 * 60 * 37;
        Run(long.MaxValue, day, false, out wakesOff);
        Run(long.MaxValue, day, true, out wakesOn);
        Check(wakesOff == day, "off: one wake per tick for a day");
        Check(wakesOn == kStale + (day - kStale + kMinute - 1) / kMinute, "on: one wake per sim-minute after the mark (" + wakesOn + ")");
        Console.WriteLine("never-ready wait over one sim-day: " + wakesOff + " wakes off, " + wakesOn + " on");
        const int none = 0;
        for (int m = 0; m < 4; m++)
            for (int pc = 0; pc < 3; pc++)
                for (int pi = 0; pi < 2; pi++)
                    for (int hc = 1; hc < 3; hc++)
                    {
                        bool ab = (m & 1) != 0, act = (m & 2) != 0;
                        int want = ab ? OutfitWaitCore.kCauseAborted : pc == none ? OutfitWaitCore.kCauseCleared
                            : act && pc == hc && pi == 0 ? OutfitWaitCore.kCauseOwn : OutfitWaitCore.kCauseReplaced;
                        Check(OutfitWaitCore.StaleCause(ab, act, pc, pi, hc, 0, none) == want, "cause " + m + " " + pc + " " + pi + " " + hc);
                    }
        Check(OutfitWaitCore.StaleCause(true, false, none, 0, 1, 0, none) == OutfitWaitCore.kCauseAborted, "an abort wins over a cleared pending outfit (Abort clears it too)");
        Check(OutfitWaitCore.StaleCause(false, true, 2, 0, 1, 0, none) == OutfitWaitCore.kCauseReplaced, "another pending outfit: replaced even with this helper active");
        Check(OutfitWaitCore.StaleCause(false, false, 1, 0, 1, 0, none) == OutfitWaitCore.kCauseReplaced, "same outfit pending but another or no active helper: replaced");
        Check(OutfitWaitCore.EndDead(true, false), "issue #40: a destroyed Sim's wait on a task of no Sim ends");
        Check(!OutfitWaitCore.EndDead(true, true), "issue #40: a wait on another Sim's task keeps EA's loop");
        Check(!OutfitWaitCore.EndDead(false, false) && !OutfitWaitCore.EndDead(false, true), "issue #40: a live Sim's wait never ends early");
        Console.WriteLine(sFails == 0 ? "outfitwait selftest ok" : "outfitwait selftest: " + sFails + " failures");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
