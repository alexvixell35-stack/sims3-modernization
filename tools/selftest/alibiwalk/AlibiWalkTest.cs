using System;
using Ts3Fixes;

// Offline checks of AlibiWalkCore (alibiwalk, docs/691): which planned walks are cut and where the tail starts.
static class AlibiWalkTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        uint w = AlibiWalkCore.kSimPath, d = AlibiWalkCore.kPortalPath, s = AlibiWalkCore.kPortalTraversalPath;
        const uint car = 4027457938u, boat = 2370016522u, horse = 1584002891u;
        Check(AlibiWalkCore.Verdict(200f, new uint[] { w }, 200f, 60f, 10f) == AlibiWalkCore.kAct, "one long walk: cut");
        Check(AlibiWalkCore.Verdict(200f, new uint[] { w, d, w, s, w }, 30f, 60f, 10f) == AlibiWalkCore.kAct, "walk, door, walk, stairs, walk: cut");
        Check(AlibiWalkCore.Verdict(59.9f, new uint[] { w }, 59.9f, 60f, 10f) == AlibiWalkCore.kShort, "under the minimum: kept");
        Check(AlibiWalkCore.Verdict(200f, new uint[0], 0f, 60f, 10f) == AlibiWalkCore.kShort, "no paths: kept");
        Check(AlibiWalkCore.Verdict(200f, null, 0f, 60f, 10f) == AlibiWalkCore.kShort, "null paths: kept");
        Check(AlibiWalkCore.Verdict(200f, new uint[] { w, car, w }, 30f, 60f, 10f) == AlibiWalkCore.kKind, "a car leg: kept");
        Check(AlibiWalkCore.Verdict(200f, new uint[] { w, boat }, 30f, 60f, 10f) == AlibiWalkCore.kKind, "a boat leg: kept");
        Check(AlibiWalkCore.Verdict(200f, new uint[] { horse }, 200f, 60f, 10f) == AlibiWalkCore.kKind, "a horse lane: kept");
        Check(AlibiWalkCore.Verdict(200f, new uint[] { w, d }, 2f, 60f, 10f) == AlibiWalkCore.kLastNotWalk, "ends in a portal: kept");
        Check(AlibiWalkCore.Verdict(70f, new uint[] { w, d, w }, 45f, 60f, 45f) == AlibiWalkCore.kTailTooLong, "tail leaves too little to cut: kept");
        Check(AlibiWalkCore.TailFrom(4f, 10f) == 4f, "short last path: walk all of it");
        Check(AlibiWalkCore.TailFrom(40f, 10f) == 10f, "long last path: walk the tail");
        Check(AlibiWalkCore.TailMax(10f) == 40f, "tail sanity bound");
        Console.WriteLine("alibiwalk selftest: " + (sRun - sFail) + "/" + sRun + " passed");
        return sFail == 0 ? 0 : 1;
    }
}
