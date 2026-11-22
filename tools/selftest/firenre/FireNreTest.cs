using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/FireNreCore.cs (docs/867): FnCore for the bug layouts and controls.
static class FireNreTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // AddThrows(hasOccupation, hasCurrentJob): EA's Occupation?.CurrentJob.RestartTimer
        Check(FnCore.AddThrows(true, false), "occupation, no current job (visibly pregnant firefighter): EA throws");
        Check(!FnCore.AddThrows(true, true), "firefighter with a current job: EA's call runs");
        Check(!FnCore.AddThrows(false, false), "no occupation: EA's ?. skips the call");

        // GratefulMayThrow(listed, ownerListed, ownerFirefighter, listedWithoutCareer)
        Check(FnCore.GratefulMayThrow(2, false, false, 1), "owner not listed, one listed Sim out of the career: EA may pick it");
        Check(FnCore.GratefulMayThrow(1, false, false, 1), "owner not listed, the only listed Sim out of the career");
        Check(FnCore.GratefulMayThrow(1, true, false, 1), "owner listed but out of the career");
        Check(!FnCore.GratefulMayThrow(2, true, true, 1), "owner listed and in the career: EA picks the owner");
        Check(!FnCore.GratefulMayThrow(2, false, false, 0), "every listed Sim in the career");
        Check(!FnCore.GratefulMayThrow(0, false, false, 0), "nobody listed: EA skips the whole block");

        foreach (bool on in new bool[] { false, true })
        {
            string s = on ? "on" : "off";
            // AddSkips: only where EA throws, only with the switch on
            Check(FnCore.AddSkips(on, true, false) == on, s + ": no current job skipped only when on");
            Check(!FnCore.AddSkips(on, true, true), s + ": current job keeps EA's RestartTimer");
            Check(!FnCore.AddSkips(on, false, false), s + ": no occupation keeps EA's path");
            // ThanksChance: 0 for a pick out of the career when on; EA's chance otherwise
            Check(FnCore.ThanksChance(on, false, 60) == (on ? 0 : 60), s + ": pick out of the career");
            Check(FnCore.ThanksChance(on, true, 60) == 60, s + ": pick in the career keeps EA's roll");
            Check(FnCore.ThanksChance(on, true, 0) == 0, s + ": a tuned 0 stays 0");
        }

        Console.WriteLine(sFail == 0 ? "firenre selftest: all passed" : "firenre selftest: " + sFail + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
