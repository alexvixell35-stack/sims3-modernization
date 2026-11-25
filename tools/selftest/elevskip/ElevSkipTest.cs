using System;
using Ts3Fixes;

// Offline checks of ElevSkipCore (elevskip, docs/686 §33).
static class ElevSkipTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        // Decide(on, hhOn, plain, npc, unseen, waited, wait, recent)
        Check(ElevSkipCore.Decide(true, false, true, true, true, 0f, 1f, false) == ElevSkipCore.kGo, "unseen NPC: through at once");
        Check(ElevSkipCore.Decide(false, true, true, true, true, 9f, 1f, false) == ElevSkipCore.kWould, "off: would (hh alone does nothing)");
        Check(ElevSkipCore.Decide(true, false, true, true, false, 9f, 1f, false) == ElevSkipCore.kSeen, "NPC in view without elevskiphh: kept");
        Check(ElevSkipCore.Decide(true, false, true, false, false, 9f, 1f, false) == ElevSkipCore.kExempt, "household without elevskiphh: exempt");
        Check(ElevSkipCore.Decide(true, false, true, false, true, 9f, 1f, false) == ElevSkipCore.kExempt, "household never counts as unseen");
        Check(ElevSkipCore.Decide(true, true, true, false, false, 0.9f, 1f, false) == ElevSkipCore.kWait, "household with elevskiphh: waits first");
        Check(ElevSkipCore.Decide(true, true, true, false, false, 1f, 1f, false) == ElevSkipCore.kGo, "household with elevskiphh: through after the wait");
        Check(ElevSkipCore.Decide(true, true, true, true, true, 0f, 1f, true) == ElevSkipCore.kRepeat, "set through a moment ago: EA's wait");
        Check(ElevSkipCore.Decide(true, true, false, true, true, 9f, 1f, false) == ElevSkipCore.kNone, "not plain (horse, carrier, toddler): never");
        // Decide(on, hhOn, hhUnseenOn, plain, npc, selected, unseen, waited, wait, recent): elevskiphhunseen
        Check(ElevSkipCore.Decide(true, true, true, true, false, false, true, 0f, 1f, false) == ElevSkipCore.kGo, "household unseen with elevskiphhunseen: through at once");
        Check(!ElevSkipCore.Fade(true, true, false, false, true), "household unseen with elevskiphhunseen: no fade");
        Check(ElevSkipCore.Decide(true, true, false, true, false, false, true, 0f, 1f, false) == ElevSkipCore.kWait, "household unseen without elevskiphhunseen: keeps the wait");
        Check(ElevSkipCore.Fade(true, false, false, false, true), "household unseen without elevskiphhunseen: fade");
        Check(ElevSkipCore.Decide(true, false, true, true, false, false, true, 9f, 1f, false) == ElevSkipCore.kExempt, "elevskiphhunseen without elevskiphh: exempt");
        Check(ElevSkipCore.Decide(true, true, true, true, false, true, true, 0f, 1f, false) == ElevSkipCore.kWait, "selected Sim: never unseen, waits");
        Check(ElevSkipCore.Fade(true, true, false, true, true), "selected Sim: fade");
        Check(ElevSkipCore.Decide(true, true, true, true, false, false, false, 0.5f, 1f, false) == ElevSkipCore.kWait, "household on screen with elevskiphhunseen: keeps the wait");
        Check(ElevSkipCore.Decide(false, true, true, true, false, false, true, 9f, 1f, false) == ElevSkipCore.kWould, "elevskip off: would");
        Check(!ElevSkipCore.Fade(true, true) && ElevSkipCore.Fade(true, false) && ElevSkipCore.Fade(false, true), "fade unless an unseen NPC");
        int C = ElevSkipCore.kStepCar, D = ElevSkipCore.kStepDoors, O = ElevSkipCore.kStepOther;
        Check(ElevSkipCore.ExitDoors(new int[] { C, D, O }, 3) == 1, "one hop, then the far doors");
        Check(ElevSkipCore.ExitDoors(new int[] { C, C, C, D }, 4) == 3, "three hops");
        Check(ElevSkipCore.ExitDoors(new int[] { D, C }, 2) == -1, "doors before any hop: not a ride");
        Check(ElevSkipCore.ExitDoors(new int[] { C, O, D }, 3) == -1, "another portal inside the ride: not ours");
        Check(ElevSkipCore.ExitDoors(new int[] { C, C }, 2) == -1, "no far doors: EA's wait");
        Check(ElevSkipCore.ExitDoors(new int[0], 0) == -1, "empty route");
        Check(ElevSkipCore.SameLaunch(true, 0.1f, 0.2f) && !ElevSkipCore.SameLaunch(true, 0.3f, 0.2f) && !ElevSkipCore.SameLaunch(false, 0f, 0.2f), "launch grouping");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
