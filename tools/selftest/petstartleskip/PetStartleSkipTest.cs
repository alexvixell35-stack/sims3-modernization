using System;
using Ts3Fixes;

// Offline self-check of PetStartleSkipCore (docs/656 §8.1).
static class PetStartleSkipTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        PetStartleSkipCore m = new PetStartleSkipCore();
        const long half = 500;  // 30 sim-minutes in this test's ticks
        const int Locked = PetStartleSkipCore.kPortalLocked;

        // recording: PortalLocked (18) only, pets only, exempt never
        Check(m.Record(1, true, false, 16, 100, half) == PetStartleSkipCore.kNotRecorded, "no path: not recorded");
        Check(m.Record(1, true, false, 10, 100, half) == PetStartleSkipCore.kNotRecorded, "no destination: not recorded");
        Check(m.Record(1, false, false, Locked, 100, half) == PetStartleSkipCore.kNotRecorded, "a human: not recorded");
        Check(m.Record(2, true, true, Locked, 100, half) == PetStartleSkipCore.kExempt && m.Count == 0, "exempt pet: never recorded");
        Check(m.Record(1, true, false, Locked, 100, 0) == PetStartleSkipCore.kNotRecorded, "zero life: not recorded");
        Check(m.Record(1, true, false, Locked, 100, half) == PetStartleSkipCore.kRecorded && m.Count == 1, "NPC pet, locked door: recorded");

        // startles while it lives
        Check(m.Startle(1, false, false, 200) == PetStartleSkipCore.kSkip, "ordinary startle: skipped");
        Check(m.Startle(1, true, false, 200) == PetStartleSkipCore.kPassForced, "forced (ignoreCooldown, fire, reaper ...): runs");
        Check(m.Startle(1, false, true, 200) == PetStartleSkipCore.kPassExempt, "pet turned exempt (adopted, selected): runs");
        Check(m.Startle(3, false, false, 200) == PetStartleSkipCore.kPass, "another pet: runs");
        Check(m.Startle(1, false, false, 100 + half - 1) == PetStartleSkipCore.kSkip, "last tick: skipped");
        Check(m.Startle(1, false, false, 100 + half) == PetStartleSkipCore.kPass && m.Count == 0, "after the window: runs, entry gone");
        m.Record(1, true, false, Locked, 700, half);
        Check(m.Startle(1, false, false, 600) == PetStartleSkipCore.kPass && m.Count == 0, "clock went back: dropped");

        // bounded table
        for (ulong i = 1; i <= (ulong)PetStartleSkipCore.kMax; i++) m.Record(i, true, false, Locked, i <= 5 ? 0 : 5000, half);
        Check(m.Count == PetStartleSkipCore.kMax, "full table");
        m.Record(1000, true, false, Locked, 5000, half);
        Check(m.Count == PetStartleSkipCore.kMax - 5 + 1, "full: expired entries dropped first");
        for (ulong i = 2000; m.Count < PetStartleSkipCore.kMax; i++) m.Record(i, true, false, Locked, 5000, half);
        m.Record(3000, true, false, Locked, 5000, half);
        Check(m.Count == 1 && m.Live(3000, 5000), "full of live entries: cleared, new one kept");

        Console.WriteLine(sFails == 0 ? "petstartleskip selftest: ok" : "petstartleskip selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
