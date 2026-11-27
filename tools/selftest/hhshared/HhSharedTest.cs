using System;
using Ts3Fixes;

// Offline checks of HhSharedCore (the household fixes' shared rules, docs/686 §41).
static class HhSharedTest
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
        Check(HhSharedCore.kGraceMin == 2.5f && HhSharedCore.GraceMs(2.5f) == 2500, "the grace: 2.5 sim-min = 2500 ms at normal speed");
        Check(HhSharedCore.GraceMin(-1f) == 0f && HhSharedCore.GraceMin(float.NaN) == 0f && HhSharedCore.GraceMs(float.NaN) == 0, "negative or NaN tunable: no grace");
        Check(HhSharedCore.GraceMin(3f) == 3f && HhSharedCore.GraceMs(3f) == 3000, "a tuned grace is used by every fix");
        Check(HhSharedCore.Scope(false, true, false, true, true) == HhSharedCore.kNone, "scope: not a case");
        Check(HhSharedCore.Scope(true, false, false, true, true) == X, "scope: pets exempt");
        Check(HhSharedCore.Scope(true, true, false, false, false) == W && HhSharedCore.Scope(true, true, true, false, false) == W, "scope: both off = would");
        Check(HhSharedCore.Scope(true, true, false, true, false) == G && HhSharedCore.Scope(true, true, true, true, false) == X, "scope: base switch = NPCs only");
        Check(HhSharedCore.Scope(true, true, true, false, true) == G && HhSharedCore.Scope(true, true, false, false, true) == W, "scope: hh switch stands alone");
        Check(HhSharedCore.Directed(true, false) && !HhSharedCore.Directed(true, true) && !HhSharedCore.Directed(false, false), "directed: an action that is not the Sim's own pick");
        Check(HhSharedCore.PlayerOrder(true, false, true) && !HhSharedCore.PlayerOrder(true, false, false) && !HhSharedCore.PlayerOrder(true, true, true), "player order: directed, for a player Sim");
        Check(!HhSharedCore.SnapFade(false, false, false) && HhSharedCore.SnapFade(true, false, false) && HhSharedCore.SnapFade(false, true, false) && HhSharedCore.SnapFade(false, false, true), "snap fade: whenever the Sim or either place can be seen");
        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " hhshared selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
