using System;
using Ts3Fixes;

// Offline checks of StandWaitSocialCore (standwaitsocial, docs/686 §30).
static class StandWaitSocialTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        Check(StandWaitSocialCore.Decide(true, 2, 2, true, true, false) == StandWaitSocialCore.kGiveUp, "social, 2 failed people-free passes: the wait ends (s543a)");
        Check(StandWaitSocialCore.Decide(true, 1, 2, true, true, false) == StandWaitSocialCore.kKeep, "one failed pass: wait on");
        Check(StandWaitSocialCore.Decide(false, 9, 2, true, true, false) == StandWaitSocialCore.kKeep, "not a social: the game's wait");
        Check(StandWaitSocialCore.Decide(true, 2, 2, false, true, false) == StandWaitSocialCore.kWould, "off: would");
        Check(StandWaitSocialCore.Decide(true, 2, 2, true, false, false) == StandWaitSocialCore.kExempt, "household without standwaitsocialhh: exempt");
        Check(StandWaitSocialCore.Decide(true, 2, 2, true, true, true) == StandWaitSocialCore.kYieldFirst, "a push or cancel this cycle goes first");
        Check(StandWaitSocialCore.Passes(0) == 1 && StandWaitSocialCore.Passes(99) == 10 && StandWaitSocialCore.Passes(3) == 3, "tuning clamped");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
