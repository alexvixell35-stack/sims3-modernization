using System;
using Ts3Fixes;

// Offline self-check of PerfJobMissedCore (docs/654 §1).
static class PerfJobMissedTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const int Keep = PerfJobMissedCore.kKeep, Remove = PerfJobMissedCore.kRemove, Seen = PerfJobMissedCore.kSeen;

    static void Main()
    {
        // on
        Check(PerfJobMissedCore.Decide(true, true, true) == Keep, "current career of a valid owner: kept");
        Check(PerfJobMissedCore.Decide(true, true, false) == Remove, "career the owner left (quit / fired / new occupation): removed");
        Check(PerfJobMissedCore.Decide(true, false, false) == Remove, "owner description disposed: removed");
        // a 'current' flag without a valid owner cannot happen in game (current needs the CareerManager), but stays stale
        Check(PerfJobMissedCore.Decide(true, false, true) == Remove, "invalid owner wins over current");
        // off: counted only, never removed
        Check(PerfJobMissedCore.Decide(false, true, true) == Keep, "off: current career kept, not counted");
        Check(PerfJobMissedCore.Decide(false, true, false) == Seen, "off: left career counted only");
        Check(PerfJobMissedCore.Decide(false, false, false) == Seen, "off: disposed owner counted only");
        Check(!PerfJobMissedCore.Stale(true, true) && PerfJobMissedCore.Stale(true, false) && PerfJobMissedCore.Stale(false, false), "Stale truth table");

        if (sFails > 0) { Console.WriteLine("perfjobmissedlistener selftest: " + sFails + " FAIL"); Environment.Exit(1); }
        Console.WriteLine("perfjobmissedlistener selftest: ok");
    }
}
