using System;
using Ts3Fixes;

// Offline checks of LineSwitchCore (lineswitch, docs/686 §25).
static class LineSwitchTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        int why;
        Check(LineSwitchCore.Decide(true, true, true, true, true, out why) == LineSwitchCore.kGo && why == LineSwitchCore.kWhyNone, "on, a free twin: switch");
        Check(LineSwitchCore.Decide(false, true, true, true, false, out why) == LineSwitchCore.kWould, "off: would, household too");
        Check(LineSwitchCore.Decide(true, false, true, true, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyNotHuman, "pets kept");
        Check(LineSwitchCore.Decide(true, true, false, true, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyPlayer, "a player's order keeps its object");
        Check(LineSwitchCore.Decide(true, true, true, false, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyNoFree, "no free twin: wait on");
        Check(LineSwitchCore.Decide(false, true, true, false, true, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyNoFree, "off, no free twin: no would");
        Check(LineSwitchCore.Decide(true, true, true, true, false, out why) == LineSwitchCore.kKeep && why == LineSwitchCore.kWhyExempt, "household without lineswitchhh kept");
        Check(LineSwitchCore.Due(2f, 2f) && !LineSwitchCore.Due(1.99f, 2f), "switch after the tuned wait");
        Check(LineSwitchCore.After(0f) == LineSwitchCore.kMinMinutes && LineSwitchCore.After(float.NaN) == LineSwitchCore.kMinMinutes && LineSwitchCore.After(99f) == LineSwitchCore.kMaxMinutes, "tuning clamped");
        Check(LineSwitchCore.Rank(true, true, 20f) < LineSwitchCore.Rank(false, true, 1f) && LineSwitchCore.Rank(false, true, 30f) < LineSwitchCore.Rank(false, false, 1f)
            && LineSwitchCore.Rank(true, true, 1f) < LineSwitchCore.Rank(true, true, 2f), "same room, then same level, then nearest");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
