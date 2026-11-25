using System;
using Ts3Fixes;

// Offline checks of ElevParkedCore (elevparked, docs/686 §34).
static class ElevParkedTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        // Decide(on, inScope, doorsOpen, carHere, carEmpty)
        Check(ElevParkedCore.Decide(true, true, false, true, true) == ElevParkedCore.kGo, "car parked here, doors closed: open at once");
        Check(ElevParkedCore.Decide(false, true, false, true, true) == ElevParkedCore.kWould, "off: would");
        Check(ElevParkedCore.Decide(true, false, false, true, true) == ElevParkedCore.kExempt, "household without elevparkedhh: exempt");
        Check(ElevParkedCore.Decide(true, true, true, true, true) == ElevParkedCore.kNone, "doors open: EA has no wait");
        Check(ElevParkedCore.Decide(true, true, false, false, true) == ElevParkedCore.kNone, "car elsewhere (or unknown): EA's 7 minutes");
        Check(ElevParkedCore.Decide(true, true, false, true, false) == ElevParkedCore.kNone, "riders in the car: EA's code");
        Check(ElevParkedCore.Decide(false, false, false, false, true) == ElevParkedCore.kNone, "nothing to count when the car is away");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
