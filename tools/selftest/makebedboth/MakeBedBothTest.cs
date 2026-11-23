using System;
using Ts3Fixes;

// Offline checks of MakeBedBothCore (makebedboth / makebedbothhh, docs/686 §13).
static class MakeBedBothTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = MakeBedBothCore.kNone, Ea = MakeBedBothCore.kKeepEa, No = MakeBedBothCore.kKeepNoSide, Go = MakeBedBothCore.kGo;

        // the case: one unmade free half with no route, the other half made, free and reachable
        Check(MakeBedBothCore.Decide(2, true, 1, true, false, true) == Go, "closed half, other side open");
        Check(MakeBedBothCore.Decide(2, true, 1, true, true, true) == Ea, "the half can be reached: the game's walk");
        Check(MakeBedBothCore.Decide(2, true, 1, true, true, false) == Ea, "reachable half wins over the other side's plan");
        Check(MakeBedBothCore.Decide(2, true, 1, false, false, true) == No, "other half unmade with a Sim in it, or in use");
        Check(MakeBedBothCore.Decide(2, true, 1, true, false, false) == No, "both sides closed");
        Check(MakeBedBothCore.Decide(2, true, 2, false, false, false) == N, "both halves unmade: the game picks the one it can reach");
        Check(MakeBedBothCore.Decide(2, true, 0, true, false, true) == N, "nothing to make");
        Check(MakeBedBothCore.Decide(2, false, 1, true, false, true) == N, "tent, igloo, pod, upper bunk");
        Check(MakeBedBothCore.Decide(1, true, 1, true, false, true) == N && MakeBedBothCore.Decide(3, true, 1, true, false, true) == N, "not two parts");

        // plans only when they matter
        Check(MakeBedBothCore.NeedPlans(2, true, 1), "plans for the case");
        Check(!MakeBedBothCore.NeedPlans(2, true, 2) && !MakeBedBothCore.NeedPlans(2, true, 0) && !MakeBedBothCore.NeedPlans(2, false, 1) && !MakeBedBothCore.NeedPlans(4, true, 1), "no plans otherwise");
        for (int parts = 0; parts < 4; parts++)
            for (int unmade = 0; unmade < 3; unmade++)
                for (int m = 0; m < 16; m++)
                {
                    bool plain = (m & 1) != 0, usable = (m & 2) != 0, pu = (m & 4) != 0, po = (m & 8) != 0;
                    int d = MakeBedBothCore.Decide(parts, plain, unmade, usable, pu, po);
                    Check(MakeBedBothCore.NeedPlans(parts, plain, unmade) == (d != N), "NeedPlans agrees with Decide " + parts + "/" + unmade + "/" + m);
                    Check(d != Go || (!pu && po && usable), "Go only for a closed half with an open usable other side " + parts + "/" + unmade + "/" + m);
                }

        // the end of the run
        Check(MakeBedBothCore.Made(true, true), "the game's run made the part the Sim stood at");
        Check(!MakeBedBothCore.Made(false, true), "walk failed after another Sim made it: flags go back");
        Check(!MakeBedBothCore.Made(true, false) && !MakeBedBothCore.Made(false, false), "run ended early");

        // second half or first make
        Check(MakeBedBothCore.Second(7, 3, 7, 3), "the same Sim just made the other half of this bed");
        Check(!MakeBedBothCore.Second(7, 3, 8, 3) && !MakeBedBothCore.Second(7, 3, 7, 4) && !MakeBedBothCore.Second(0, 0, 0, 0), "another Sim, another bed, nothing known");

        // the parameter
        Check(MakeBedBothCore.Opposite(true, true), "the Sim of the open case is in the part");
        Check(!MakeBedBothCore.Opposite(true, false), "another Sim uses the part during the walk");
        Check(!MakeBedBothCore.Opposite(false, true), "another part");

        // scope (BlockedUseCore.Decide with makebedboth's switches)
        Check(BlockedUseCore.Decide(true, false, true, false) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt, "makebedboth alone");
        Check(BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould, "makebedbothhh alone");
        Check(BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, true, false, false) == BlockedUseCore.kWould, "both off: counted only");

        Console.WriteLine(sFail == 0 ? "makebedboth selftest: " + sRun + " checks passed" : "makebedboth selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
