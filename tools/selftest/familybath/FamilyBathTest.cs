using System;
using Ts3Fixes;

// Offline checks of FamilyBathCore (familybath, docs/686 §44).
static class FamilyBathTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int G = HhSharedCore.kGo, W = HhSharedCore.kWould, X = HhSharedCore.kExempt;
        const int UT = FamilyBathCore.kUserToilet, UW = FamilyBathCore.kUserWash, TT = FamilyBathCore.kTargetToilet, TS = FamilyBathCore.kTargetSink, TO = FamilyBathCore.kTargetOther;
        // which fixtures go together
        Check(FamilyBathCore.Pair(UW, TT, false) && FamilyBathCore.Pair(UW, TS, false), "toilet or sink while someone showers or bathes");
        Check(FamilyBathCore.Pair(UT, TS, false) && !FamilyBathCore.Pair(UT, TT, false), "the sink while someone uses the toilet, never a second toilet");
        Check(!FamilyBathCore.Pair(UW, TO, false) && !FamilyBathCore.Pair(FamilyBathCore.kUserNone, TT, false), "no fixture action, or another kind of privacy (WooHoo): EA's rule");
        Check(!FamilyBathCore.Pair(UW, TS, true) && !FamilyBathCore.Pair(UT, TT, true), "never the fixture the Sim with privacy uses");
        // Decide(userKind, targetKind, sameFixture, doorLocked, sameHousehold, humans, modest, scope)
        Check(FamilyBathCore.Decide(UW, TT, false, false, true, true, false, G) == FamilyBathCore.kGrant, "EA's automatic privacy (shower): a household member is let in to the toilet");
        Check(FamilyBathCore.Decide(UT, TS, false, false, true, true, false, G) == FamilyBathCore.kGrant, "EA's automatic privacy (toilet): a household member is let in to the sink");
        // the player's door lock: never overridden, whatever else holds
        Check(FamilyBathCore.Decide(UW, TT, false, true, true, true, false, G) == FamilyBathCore.kDoorLocked, "a door the player locked against the Sim: EA's result, shower privacy or not");
        Check(FamilyBathCore.Decide(UT, TS, false, true, true, true, false, G) == FamilyBathCore.kDoorLocked, "a door the player locked against the Sim: EA's result, toilet privacy or not");
        Check(FamilyBathCore.Decide(UW, TT, false, true, true, true, false, W) == FamilyBathCore.kDoorLocked, "player lock: not even counted as a would-be pass");
        Check(FamilyBathCore.Decide(UW, TO, false, true, true, true, false, G) == FamilyBathCore.kNone, "player lock with no fixture action: EA's rule as before");
        // who counts
        Check(FamilyBathCore.Decide(UW, TT, false, false, false, true, false, G) == FamilyBathCore.kNotFamily, "a guest or visitor is never let in");
        Check(FamilyBathCore.Decide(UW, TT, false, false, true, false, false, G) == FamilyBathCore.kNotFamily, "pets: EA's rule");
        Check(FamilyBathCore.Decide(UW, TT, false, false, true, true, true, G) == FamilyBathCore.kModest, "Never Nude or Shy on either side: EA's rule");
        Check(FamilyBathCore.Decide(UW, TT, false, false, true, true, false, W) == FamilyBathCore.kWould && FamilyBathCore.Decide(UW, TT, false, false, true, true, false, X) == FamilyBathCore.kExempt, "switch off: would; out of scope: exempt");
        Check(FamilyBathCore.PassLive(10, 11) && !FamilyBathCore.PassLive(11, 11), "the pass ends at its time");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " familybath selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
