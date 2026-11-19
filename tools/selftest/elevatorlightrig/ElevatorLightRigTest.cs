using System;
using Ts3Fixes;

// Offline self-check of ElevatorLightRigCore (docs/658 §7.1).
static class ElevatorLightRigTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Check(ElevatorLightRigCore.Decide(true, true, true, false) == ElevatorLightRigCore.kClear, "rider reset out of the elevator: rig cleared");
        Check(ElevatorLightRigCore.Decide(false, true, true, false) == ElevatorLightRigCore.kSeenOff, "switch off: counted only");
        Check(ElevatorLightRigCore.Decide(true, true, false, false) == ElevatorLightRigCore.kNone, "never rode (reset at the door before entering): nothing");
        Check(ElevatorLightRigCore.Decide(true, true, true, true) == ElevatorLightRigCore.kNone, "still registered (handed to the next floor): keeps the rig");
        Check(ElevatorLightRigCore.Decide(true, false, true, false) == ElevatorLightRigCore.kNone, "Sim object gone: nothing to call");
        Console.WriteLine(sFails == 0 ? "elevatorlightrig selftest: ok" : "elevatorlightrig selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
