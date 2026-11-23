// Usage: node tools/selftest/ladderfoot/run.ts  (compiles this with mods/ts3fixes/src/LadderFootCore.cs)
// Offline self-check of LadderFootCore.cs (docs/183 §9): which plans are looked at, a lane and a link from the game's slot
// test, who is asked, the decision by switch and scope, and a model of the wait (asked Sims leave after some minutes,
// or never). Exit code 1 on failure.
using System;
using Ts3Fixes;

static class LadderFootTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    // BlockedStatus of the game's slot test: 0 free, 1 a Sim, 2 a vehicle, 3 an object, 4 a wall; -1 = our "threw"
    const int F = 0, S = 1, O = 3, W = 4, X = -1;
    const int kFree = LadderFootCore.kLaneFree, kSims = LadderFootCore.kLaneSims, kOther = LadderFootCore.kLaneOther;

    static void Plans()
    {
        Check(LadderFootCore.Wants(true, true, true, 1, 2) && LadderFootCore.Wants(true, true, true, 2, 0), "no path to another level of the same lot");
        Check(!LadderFootCore.Wants(true, true, true, 1, 1), "same level: not looked at");
        Check(!LadderFootCore.Wants(false, true, true, 1, 2), "another failure: not looked at");
        Check(!LadderFootCore.Wants(true, false, true, 1, 2) && !LadderFootCore.Wants(true, true, false, 1, 2), "off the lot or a goal on another lot: not looked at");
    }

    static void Lanes()
    {
        Check(LadderFootCore.Lane(F, F, F, F) == kFree, "free lane");
        Check(LadderFootCore.Lane(F, F, F, S) == kSims && LadderFootCore.Lane(F, S, F, F) == kSims && LadderFootCore.Lane(F, S, F, S) == kSims, "a Sim on either end");
        Check(LadderFootCore.Lane(O, O, F, S) == kOther && LadderFootCore.Lane(F, F, W, W) == kOther, "an object or wall on an end is not a Sim lane");
        Check(LadderFootCore.Lane(F, O, F, F) == kOther, "the test with Sims names something else: not a Sim lane");
        Check(LadderFootCore.Lane(X, X, F, S) == kOther && LadderFootCore.Lane(F, X, F, F) == kOther, "a test that threw is never a Sim lane");
        // the logged ladder: both lanes "A free, B free (with Sims: sim)"
        Check(LadderFootCore.ClosedBySims(new int[] { LadderFootCore.Lane(F, F, F, S), LadderFootCore.Lane(F, F, F, S) }, 2), "the logged ladder is closed by Sims");
        Check(!LadderFootCore.ClosedBySims(new int[] { kSims, kFree }, 2) && LadderFootCore.HasFreeLane(new int[] { kSims, kFree }, 2), "one free lane: open");
        Check(LadderFootCore.ClosedBySims(new int[] { kSims, kOther }, 2), "one lane behind an object, the other behind a Sim: closed by Sims");
        Check(!LadderFootCore.ClosedBySims(new int[] { kOther, kOther }, 2), "objects only: not this");
        Check(!LadderFootCore.ClosedBySims(new int[0], 0) && !LadderFootCore.HasFreeLane(new int[0], 0), "no lanes: nothing");
        // every combination: closed by Sims exactly when no lane is free and one is a Sim lane
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) for (int c = 0; c < 3; c++)
        {
            int[] l = { a, b, c };
            bool want = a != kFree && b != kFree && c != kFree && (a == kSims || b == kSims || c == kSims);
            Check(LadderFootCore.ClosedBySims(l, 3) == want, "closed table " + a + b + c);
        }
    }

    static void Asking()
    {
        Check(LadderFootCore.Blocker(false, false, false), "a standing Sim is asked");
        Check(!LadderFootCore.Blocker(true, false, false), "never the planning Sim");
        Check(!LadderFootCore.Blocker(false, false, true), "a walking Sim leaves by itself");
        Check(!LadderFootCore.Blocker(false, true, false), "a destroyed Sim is not asked");
        Check(LadderFootCore.MayAsk(false, false, false) && LadderFootCore.MayAsk(true, true, false), "NPC always, household with its switch");
        Check(!LadderFootCore.MayAsk(false, true, false), "household without its switch: not asked");
        Check(!LadderFootCore.MayAsk(true, false, true) && !LadderFootCore.MayAsk(true, true, true), "a Sim already stepping away is not asked twice");
    }

    static void Decisions()
    {
        Check(LadderFootCore.Decide(false, 2, true, true, false) == LadderFootCore.kKeep, "no needed link closed: keep");
        Check(LadderFootCore.Decide(true, 0, true, true, false) == LadderFootCore.kKeep, "nobody standing there: keep");
        Check(LadderFootCore.Decide(true, 2, false, false, false) == LadderFootCore.kWould && LadderFootCore.Decide(true, 2, false, true, true) == LadderFootCore.kWould, "off: counted only");
        Check(LadderFootCore.Decide(true, 2, true, false, true) == LadderFootCore.kExempt, "household planner without its switch: exempt");
        Check(LadderFootCore.Decide(true, 2, true, true, true) == LadderFootCore.kShoo && LadderFootCore.Decide(true, 2, true, false, false) == LadderFootCore.kShoo, "on and in scope: ask");
    }

    // The wait polled every 10 ticks (37.5 ticks a sim-minute): returns the end kind and the minutes waited.
    static int Wait(float leaveAfter, float cap, float cancelAt, out float waited)
    {
        waited = 0f;
        for (int polls = 0; polls < 100000; polls++)
        {
            int step = LadderFootCore.WaitStep(waited >= leaveAfter ? 0 : 1, waited, cap, waited >= cancelAt);
            if (step != LadderFootCore.kWaitOn) return step;
            waited += 10f / 37.5f;
        }
        return -1;
    }

    static void Waits()
    {
        Check(LadderFootCore.MayWait(1, true, true, false, 5f), "own task, may sleep: wait");
        Check(!LadderFootCore.MayWait(0, true, true, false, 5f), "nobody asked: no wait");
        Check(!LadderFootCore.MayWait(1, false, true, false, 5f), "another task plans for the Sim: no wait");
        Check(!LadderFootCore.MayWait(1, true, false, false, 5f), "no sleep allowed: no wait");
        Check(!LadderFootCore.MayWait(1, true, true, true, 5f), "unplaced jig: no wait");
        Check(!LadderFootCore.MayWait(1, true, true, false, 0f), "cap 0: ask only");
        Check(LadderFootCore.WaitCap(5f, 15f) == 5f && LadderFootCore.WaitCap(30f, 15f) == 15f && LadderFootCore.WaitCap(0f, 15f) == 0f && LadderFootCore.WaitCap(5f, 0f) == 5f, "cap: tuned, never above the game's");
        float w;
        Check(Wait(0f, 5f, 99f, out w) == LadderFootCore.kWaitReplan && w == 0f, "Sims already gone: plan at once");
        Check(Wait(1f, 5f, 99f, out w) == LadderFootCore.kWaitReplan && w >= 1f && w < 1.3f, "Sims gone after a minute: plan then");
        Check(Wait(99f, 5f, 99f, out w) == LadderFootCore.kWaitTimeout && w >= 5f && w < 5.3f, "Sims that stay: timeout at the cap");
        Check(Wait(99f, 5f, 2f, out w) == LadderFootCore.kWaitCanceled && w >= 2f && w < 2.3f, "the planner is cancelled: stop");
        Check(LadderFootCore.WaitStep(0, 1f, 5f, true) == LadderFootCore.kWaitCanceled, "cancel before the second plan");
        Random rng = new Random(1121);
        for (int i = 0; i < 500; i++)
        {
            float leave = (float)rng.NextDouble() * 12f, cap = 0.5f + (float)rng.NextDouble() * 10f;
            int end = Wait(leave, cap, 99f, out w);
            Check(w <= cap + 0.3f, "a wait never outlasts its cap");
            Check((end == LadderFootCore.kWaitReplan) == (leave <= w), "second plan exactly when the Sims left in time");
        }
    }

    static void Cooldown()
    {
        Check(LadderFootCore.Due(false, 100, 0, 75), "first look");
        Check(!LadderFootCore.Due(true, 100, 50, 75) && LadderFootCore.Due(true, 125, 50, 75), "one look per cooldown");
        Check(LadderFootCore.Due(true, 10, 50, 75), "clock went back (another save): look");
    }

    static int Main()
    {
        Plans(); Lanes(); Asking(); Decisions(); Waits(); Cooldown();
        Console.WriteLine(sFails == 0 ? "ladderfoot selftest: ok" : "ladderfoot selftest: " + sFails + " FAILED");
        return sFails == 0 ? 0 : 1;
    }
}
