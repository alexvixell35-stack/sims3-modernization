using System;
using Ts3Fixes;

// Offline checks of StairFlowCore (stairflow, docs/686 §32).
static class StairFlowTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static bool Near(float a, float b) { return Math.Abs(a - b) < 0.01f; }

    static int Main()
    {
        Check(Near(StairFlowCore.AngleDeg(1, 0, 1, 0), 0), "same heading: 0 deg");
        Check(Near(StairFlowCore.AngleDeg(1, 0, 0, 1), 90), "right angle: 90 deg");
        Check(Near(StairFlowCore.AngleDeg(1, 0, -1, 0), 180), "turn around: 180 deg");
        Check(Near(StairFlowCore.AngleDeg(2, 2, 1, 1), 0), "length does not count");
        Check(Near(StairFlowCore.AngleDeg(0, 0, 1, 0), 180), "no heading: treated as a full turn (kept)");
        Check(Near(StairFlowCore.AngleDeg(1, 0, 1, (float)Math.Tan(Math.PI / 6)), 30), "30 deg");
        Check(StairFlowCore.Decide(true, true, true, 12, 30) == StairFlowCore.kGo, "straight in: no stop anim");
        Check(StairFlowCore.Decide(true, true, true, 30, 30) == StairFlowCore.kGo, "at the limit: go");
        Check(StairFlowCore.Decide(true, true, true, 31, 30) == StairFlowCore.kKept, "over the limit: EA's stop-and-face");
        Check(StairFlowCore.Decide(false, true, true, 5, 30) == StairFlowCore.kWould, "off: would");
        Check(StairFlowCore.Decide(true, false, true, 5, 30) == StairFlowCore.kExempt, "household without stairflowhh: exempt");
        Check(StairFlowCore.Decide(true, true, false, 0, 30) == StairFlowCore.kNo, "ladder or spiral: never");
        Check(StairFlowCore.Decide(false, false, true, 90, 30) == StairFlowCore.kKept, "a turn is kept in every state");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
