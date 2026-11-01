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
        Console.WriteLine(sFails == 0 ? "outfitwait selftest ok" : "outfitwait selftest: " + sFails + " failures");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
