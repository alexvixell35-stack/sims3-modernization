using System;
using Ts3Fixes;

// Offline checks of UnstuckCore (unstuck / unstuckhh, docs/686 §18).
static class UnstuckTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int Free = UnstuckCore.kFree, Clip = UnstuckCore.kClip, Walled = UnstuckCore.kWalled;
        const float Tol = UnstuckCore.kSpotTol, Dy = 1.5f;
        const long G = 1000;  // the grace, in ticks of any size
        int N = UnstuckCore.kMinFails;

        // which failed plans say "no way from here"
        Check(UnstuckCore.PlanKind(16) && UnstuckCore.PlanKind(10) && UnstuckCore.PlanKind(15), "no path, no destination, all places obstructed");
        Check(!UnstuckCore.PlanKind(17), "a Sim on the goal says nothing about the Sim's spot");
        Check(!UnstuckCore.PlanKind(18) && !UnstuckCore.PlanKind(3), "a locked door, an aborted plan");
        Check(!UnstuckCore.PlanKind(11) && !UnstuckCore.PlanKind(12) && !UnstuckCore.PlanKind(13) && !UnstuckCore.PlanKind(14), "object and slot failures");
        Check(!UnstuckCore.PlanKind(0) && !UnstuckCore.PlanKind(1) && !UnstuckCore.PlanKind(2), "unknown, succeeded, plain failure");
        for (int t = 4; t <= 9; t++) Check(!UnstuckCore.PlanKind(t), "start failure " + t);

        // one spot
        Check(UnstuckCore.SameSpot(0f, 0f, Tol, Dy) && UnstuckCore.SameSpot(Tol, 0.2f, Tol, Dy), "the same spot");
        Check(!UnstuckCore.SameSpot(Tol + 0.1f, 0f, Tol, Dy), "a step away is another spot");
        Check(!UnstuckCore.SameSpot(0f, 3f, Tol, Dy) && !UnstuckCore.SameSpot(0f, -3f, Tol, Dy), "the floor above or below is another spot");
        Check(UnstuckCore.Continues(true, 500, 100, 1000), "the count goes on");
        Check(!UnstuckCore.Continues(false, 500, 100, 1000), "another spot: a new count");
        Check(!UnstuckCore.Continues(true, 1200, 100, 1000), "a long pause: a new count");
        Check(!UnstuckCore.Continues(true, 50, 100, 1000), "a clock that went back: a new count");

        // the signal and its grace
        Check(UnstuckCore.kGraceMinutes >= 2f && UnstuckCore.kGraceMinutes <= 3f, "the grace is 2-3 sim-minutes");
        Check(N >= 3, "several failed plans");
        Check(UnstuckCore.Due(N, G, G, false) && UnstuckCore.Due(N + 4, G * 9, G, false), "enough plans over the grace");
        Check(!UnstuckCore.Due(N - 1, G * 9, G, false), "too few plans, however long");
        Check(!UnstuckCore.Due(N, G - 1, G, false) && !UnstuckCore.Due(N * 10, 0, G, false), "inside the grace, however many plans");
        Check(UnstuckCore.Due(1, 0, G, true), "the game's stuck check found no way: the next failed plan is enough");
        Check(!UnstuckCore.Due(0, G, G, true), "the game's signal alone moves nobody: a failed plan must follow");

        // who is never moved
        Check(UnstuckCore.SimOk(false, false, false, false, false, true, false), "a standing Sim or pet on a lot");
        Check(!UnstuckCore.SimOk(true, false, false, false, false, true, false), "sitting, sleeping, in an object");
        Check(!UnstuckCore.SimOk(false, true, false, false, false, true, false), "swimming or diving");
        Check(!UnstuckCore.SimOk(false, false, true, false, false, true, false), "riding or ridden");
        Check(!UnstuckCore.SimOk(false, false, false, true, false, true, false), "moved by something else");
        Check(!UnstuckCore.SimOk(false, false, false, false, true, true, false), "a baby");
        Check(!UnstuckCore.SimOk(false, false, false, false, false, false, false), "no lot");
        Check(!UnstuckCore.SimOk(false, false, false, false, false, true, true), "cancelled");

        // a plan that leads away clears the count
        Check(UnstuckCore.PlanClears(true, true, UnstuckCore.kAwayMin) && UnstuckCore.PlanClears(true, true, 40f), "a plan of 3 m or more from its spot");
        Check(!UnstuckCore.PlanClears(true, true, UnstuckCore.kAwayMin - 0.1f) && !UnstuckCore.PlanClears(true, true, 0f), "a turn or a step inside the box");
        Check(!UnstuckCore.PlanClears(true, false, 40f), "a plan from another point");
        Check(!UnstuckCore.PlanClears(false, true, 40f), "a failed plan");

        // the probe
        Check(UnstuckCore.Verdict(true, false, 0f, false, false) == Free && UnstuckCore.Verdict(true, true, 2f, true, true) == Free, "a normal way out: not boxed in");
        Check(UnstuckCore.Verdict(false, true, 2f, true, true) == Clip && UnstuckCore.Verdict(false, true, UnstuckCore.kClipMax, true, true) == Clip, "only through objects: walk out");
        Check(UnstuckCore.Verdict(false, false, 0f, false, false) == Walled, "no plan through objects either: walled in");
        Check(UnstuckCore.Verdict(false, true, UnstuckCore.kClipMax + 0.1f, true, true) == Walled, "a way out longer than the cap");
        Check(UnstuckCore.Verdict(false, true, 2f, false, false) == Walled && UnstuckCore.Verdict(false, true, 2f, false, true) == Walled, "the end is not routable");
        Check(UnstuckCore.Verdict(false, true, 2f, true, false) == Walled, "the end is a second box");
        Check(UnstuckCore.kRings.Length >= 2 && UnstuckCore.kRings[UnstuckCore.kRings.Length - 1] < UnstuckCore.kAwayMax, "rings inside the range");
        for (int i = 1; i < UnstuckCore.kRings.Length; i++) Check(UnstuckCore.kRings[i] > UnstuckCore.kRings[i - 1], "rings nearest first, " + i);
        Check(UnstuckCore.kAwayMin < UnstuckCore.kAwayMax && UnstuckCore.kAwayMax <= UnstuckCore.kClipMax, "a walk out to the farthest ring fits the cap in a straight line");

        // scope
        Check(BlockedUseCore.Decide(true, false, true, false) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt, "unstuck: NPCs only");
        Check(BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould, "unstuckhh: household only");
        Check(BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, true, false, false) == BlockedUseCore.kWould, "both off: counted only");

        Console.WriteLine(sFail == 0 ? "unstuck selftest: " + sRun + " checks ok" : "unstuck selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
