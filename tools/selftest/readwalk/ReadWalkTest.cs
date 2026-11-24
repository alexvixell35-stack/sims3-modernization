using System;
using Ts3Fixes;

// Offline checks of ReadWalkCore (readwalk, docs/686 §22).
static class ReadWalkTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int W = ReadWalkCore.kWalk;
        Check(ReadWalkCore.Step(false, true, true, 1f) == W, "part known, slot clear: walk");
        Check(ReadWalkCore.Step(true, true, true, 1f) == ReadWalkCore.kKeepChildIn, "child already in bed: the game walks now");
        Check(ReadWalkCore.Step(false, false, true, 1f) == ReadWalkCore.kWaitPart, "part unknown: wait");
        Check(ReadWalkCore.Step(false, true, false, 1f) == ReadWalkCore.kKeepNoSlot, "no sit slot kept");
        Check(ReadWalkCore.Step(false, true, true, 0.48f) == ReadWalkCore.kKeepOnRoute && ReadWalkCore.Step(false, true, true, 0.5f) == W, "the 0.7 m line");
        Check(ReadWalkCore.SegDistSq(0f, 1f, -1f, 0f, 1f, 0f) == 1f, "beside the segment");
        Check(ReadWalkCore.SegDistSq(3f, 0f, -1f, 0f, 1f, 0f) == 4f, "past the end");
        Check(ReadWalkCore.SegDistSq(2f, 2f, 0f, 0f, 0f, 0f) == 8f, "a point segment");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
