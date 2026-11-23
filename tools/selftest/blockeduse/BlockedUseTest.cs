using System;
using Ts3Fixes;

// Offline checks of BlockedUseCore (blockeduse / blockedusehh, docs/686 §10).
static class BlockedUseTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = BlockedUseCore.kNone, W = BlockedUseCore.kWould, X = BlockedUseCore.kExempt, G = BlockedUseCore.kGo;

        // which failed plans count
        Check(BlockedUseCore.Kind(BlockedUseCore.kNoPath, false), "no path, furniture or wall");
        Check(BlockedUseCore.Kind(BlockedUseCore.kNoDestination, false), "every slot refused");
        Check(BlockedUseCore.Kind(BlockedUseCore.kNoUnobstructedSlots, false) && BlockedUseCore.Kind(BlockedUseCore.kPointsObstructed, false), "obstructed slots");
        Check(!BlockedUseCore.Kind(BlockedUseCore.kNoPath, true), "a Sim as blocker keeps the game's behaviour");
        Check(!BlockedUseCore.Kind(18, false), "portal locked kept");
        Check(!BlockedUseCore.Kind(17, false), "destination blocked by a Sim kept");
        Check(!BlockedUseCore.Kind(3, false) && !BlockedUseCore.Kind(11, false), "aborted, object not found kept");
        Check(!BlockedUseCore.Kind(BlockedUseCore.kSucceeded, false), "a plan that succeeded");

        // scope: each switch alone
        Check(BlockedUseCore.Decide(false, false, true, true) == N, "not our kind");
        Check(BlockedUseCore.Decide(true, false, false, false) == W && BlockedUseCore.Decide(true, true, false, false) == W, "both off: would");
        Check(BlockedUseCore.Decide(true, false, true, false) == G, "npc, blockeduse");
        Check(BlockedUseCore.Decide(true, true, true, false) == X, "household without blockedusehh");
        Check(BlockedUseCore.Decide(true, true, false, true) == G, "household with blockedusehh alone");
        Check(BlockedUseCore.Decide(true, false, false, true) == W, "npc with only blockedusehh");
        Check(BlockedUseCore.Decide(true, true, true, true) == G && BlockedUseCore.Decide(true, false, true, true) == G, "both on");

        // walk up first
        Check(!BlockedUseCore.NeedNear(true, true, true, 2f, 3f), "near, in the room");
        Check(BlockedUseCore.NeedNear(true, true, true, 3.5f, 3f), "too far");
        Check(BlockedUseCore.NeedNear(true, true, false, 1f, 3f), "other room");
        Check(BlockedUseCore.NeedNear(true, false, true, 1f, 3f), "other level");
        Check(BlockedUseCore.NeedNear(false, true, true, 1f, 3f), "other lot");

        // cooldown
        Check(BlockedUseCore.Cooling(100, 90, 20), "inside the cooldown");
        Check(!BlockedUseCore.Cooling(100, 80, 20), "cooldown over");
        Check(!BlockedUseCore.Cooling(100, 110, 20), "clock went back");

        // slot choice
        float[] dist = { 2.5f, 0.8f, 0.3f, 1.0f };
        float[] dy = { 0f, 0f, 0f, 3f };
        bool[] usable = { true, true, false, true };
        Check(BlockedUseCore.PickSlot(dist, dy, usable, 4, 4f, 1f) == 1, "nearest usable slot on the level");
        Check(BlockedUseCore.PickSlot(dist, dy, usable, 1, 2f, 1f) == -1, "out of range");
        Check(BlockedUseCore.PickSlot(dist, dy, usable, 0, 4f, 1f) == -1, "no slots");
        Check(BlockedUseCore.PickSlot(new float[] { 1f }, new float[] { -3f }, new bool[] { true }, 1, 4f, 1f) == -1, "a floor below");

        // clip, snap or keep
        Check(BlockedUseCore.Step(true, true, 1.5f, 6f, false, 1) == BlockedUseCore.kClip, "short plan through objects");
        Check(BlockedUseCore.Step(true, true, 1.5f, 6f, false, -1) == BlockedUseCore.kClip, "clip needs no snap slot");
        Check(BlockedUseCore.Step(true, true, 14f, 6f, false, 1) == BlockedUseCore.kSnap, "the way round a wall is too long: snap");
        Check(BlockedUseCore.Step(true, false, 0f, 6f, false, 1) == BlockedUseCore.kSnap, "no plan through objects: snap");
        Check(BlockedUseCore.Step(true, true, 1.5f, 6f, true, 1) == BlockedUseCore.kSnap, "a Sim at the plan's end, another slot free: snap");
        Check(BlockedUseCore.Step(true, true, 1.5f, 6f, true, -1) == BlockedUseCore.kKeep, "a Sim at the plan's end, no other slot");
        Check(BlockedUseCore.Step(true, false, 0f, 6f, false, -1) == BlockedUseCore.kKeep, "nothing in range");
        Check(BlockedUseCore.Step(false, true, 1f, 6f, false, 1) == BlockedUseCore.kKeep, "other level or lot");

        // out again
        Check(BlockedUseCore.ExitLook(true, false, 0.1f, 0.75f, 10, 1000) == BlockedUseCore.kExitWait, "the interaction still runs");
        Check(BlockedUseCore.ExitLook(false, true, 0.1f, 0.75f, 10, 1000) == BlockedUseCore.kExitWait, "lying in the bed");
        Check(BlockedUseCore.ExitLook(false, false, 2f, 0.75f, 10, 1000) == BlockedUseCore.kExitGone, "walked away");
        Check(BlockedUseCore.ExitLook(true, true, 0.1f, 0.75f, 2000, 1000) == BlockedUseCore.kExitGone, "too old");
        Check(BlockedUseCore.ExitLook(false, false, 0.1f, 0.75f, -5, 1000) == BlockedUseCore.kExitGone, "clock went back");
        Check(BlockedUseCore.ExitLook(false, false, 0.1f, 0.75f, 10, 1000) == BlockedUseCore.kExitCheck, "done, standing, at the slot");
        Check(BlockedUseCore.ExitSnap(false, true, false), "cannot walk away: put back");
        Check(!BlockedUseCore.ExitSnap(true, true, false), "the plan succeeded");
        Check(!BlockedUseCore.ExitSnap(false, false, false), "a plan from a named point");
        Check(!BlockedUseCore.ExitSnap(false, true, true), "can walk to where it came from");

        Console.WriteLine(sFail == 0 ? "blockeduse selftest: " + sRun + " checks passed" : "blockeduse selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
