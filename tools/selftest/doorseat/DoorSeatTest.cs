using System;
using Ts3Fixes;

// Offline checks of DoorSeatCore (doorseat, docs/686 §39): which refused seat tests count, scope, door-local geometry.
static class DoorSeatTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-3f; }

    static int Main()
    {
        // Kind(eaResult, hasSim, doorsNear)
        Check(DoorSeatCore.Kind(false, true, 1), "refused for a Sim with a door near: case");
        Check(!DoorSeatCore.Kind(true, true, 1), "passed: no case");
        Check(!DoorSeatCore.Kind(false, false, 1), "build-mode / load call (no Sim): no case");
        Check(!DoorSeatCore.Kind(false, true, 0), "no door near: no case");

        // Decide(kind, human, player, on, hhOn)
        Check(DoorSeatCore.Decide(false, true, false, true, true) == DoorSeatCore.kNone, "not a case");
        Check(DoorSeatCore.Decide(true, false, false, true, true) == DoorSeatCore.kExempt, "a pet: exempt");
        Check(DoorSeatCore.Decide(true, true, false, false, false) == DoorSeatCore.kWould, "both off: would");
        Check(DoorSeatCore.Decide(true, true, true, false, false) == DoorSeatCore.kWould, "both off, household: would");
        Check(DoorSeatCore.Decide(true, true, false, true, false) == DoorSeatCore.kGo, "NPC, doorseat: go");
        Check(DoorSeatCore.Decide(true, true, true, true, false) == DoorSeatCore.kExempt, "household, only doorseat: exempt");
        Check(DoorSeatCore.Decide(true, true, true, false, true) == DoorSeatCore.kGo, "household, doorseathh alone: go");
        Check(DoorSeatCore.Decide(true, true, false, false, true) == DoorSeatCore.kWould, "NPC, only doorseathh: would");

        float f, s;
        DoorSeatCore.DoorLocal(0f, 0.8f, 0f, 1f, out f, out s);
        Check(Near(f, 0.8f) && Near(s, 0f), "a point 0.8 m in front of a +z door");
        DoorSeatCore.DoorLocal(0.5f, -0.4f, 0f, 2f, out f, out s);
        Check(Near(f, -0.4f) && Near(s, -0.5f), "behind and beside, forward not unit");
        DoorSeatCore.DoorLocal(1f, 1f, 0f, 0f, out f, out s);
        Check(f == 0f && s == 0f, "zero forward: no answer");
        Check(DoorSeatCore.InClearArea(0.8f, 0.3f, 0.75f, 1f), "in front: clear area");
        Check(!DoorSeatCore.InClearArea(0.8f, 1.2f, 0.75f, 1f), "beside along the wall: not");
        Check(!DoorSeatCore.InClearArea(1.4f, 0f, 0.75f, 1f), "far in front: not");

        Check(DoorSeatCore.DoorNear(2.9f, 0.2f, 3f), "door 2.9 m away on the level");
        Check(!DoorSeatCore.DoorNear(3.1f, 0f, 3f), "door 3.1 m away");
        Check(!DoorSeatCore.DoorNear(1f, 2.8f, 3f), "door on another level");

        Check(DoorSeatCore.FirstPass(new bool[] { false, true, true }, 3) == 1, "first door that passes");
        Check(DoorSeatCore.FirstPass(new bool[] { false, false }, 2) == -1, "none passes");
        Check(DoorSeatCore.FirstPass(new bool[] { true }, 0) == -1, "empty list");
        Check(DoorSeatCore.FirstPass(new bool[] { false }, 4) == -1, "n beyond the array");

        Console.WriteLine("doorseat selftest: " + (sRun - sFail) + "/" + sRun + " ok");
        return sFail == 0 ? 0 : 1;
    }
}
