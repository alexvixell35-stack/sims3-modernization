using System;
using Ts3Fixes;

// Offline checks of LineStaleCore (linestale, docs/686 §29).
static class LineStaleTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int F = LineStaleCore.kFresh, Q = LineStaleCore.kQueued, G = LineStaleCore.kGone, U = LineStaleCore.kUnknown;
        Check(LineStaleCore.State(true, false, false, 9f, true, true) == F, "its action runs (walking over, any distance): fresh");
        Check(LineStaleCore.State(true, false, true, 1f, false, false) == F && LineStaleCore.State(true, false, true, -1f, false, false) == F, "in the use list at the object: fresh");
        Check(LineStaleCore.State(true, false, false, 9f, false, true) == Q, "action queued behind another one: queued");
        Check(LineStaleCore.State(true, false, false, 9f, false, false) == G, "action ended without leaving the line: gone");
        Check(LineStaleCore.State(true, true, true, 0f, true, true) == G, "destroyed Sim: gone");
        Check(LineStaleCore.State(false, true, false, 0f, false, false) == U, "turn not seen: unknown");
        Check(LineStaleCore.State(true, false, true, 3f, false, false) == LineStaleCore.kStrayUser, "in the use list, action over, 3 m away: stray user (s545c)");
        Check(LineStaleCore.Decide(true, true, LineStaleCore.kStrayUser, 5f, 5f) == LineStaleCore.kDrop, "stray user dropped after 5 sim-min");
        Check(LineStaleCore.Decide(true, true, F, 99f, 5f) == LineStaleCore.kKeep, "fresh head keeps its turn however long");
        Check(LineStaleCore.Decide(true, true, U, 99f, 5f) == LineStaleCore.kKeep, "unknown head left alone");
        Check(LineStaleCore.Decide(true, true, Q, 4.9f, 5f) == LineStaleCore.kWait, "under 5 sim-min: wait");
        Check(LineStaleCore.Decide(true, true, Q, 5f, 5f) == LineStaleCore.kDrop && LineStaleCore.Decide(true, true, G, 6f, 5f) == LineStaleCore.kDrop, "stale 5 sim-min: drop");
        Check(LineStaleCore.Decide(false, true, G, 6f, 5f) == LineStaleCore.kWould, "off: would");
        Check(LineStaleCore.Decide(true, false, G, 6f, 5f) == LineStaleCore.kKeep, "household head without linestalehh: kept");
        Check(LineStaleCore.Decide(true, true, G, float.NaN, 5f) == LineStaleCore.kWait, "NaN minutes: wait");
        Check(LineStaleCore.After(0f) == LineStaleCore.kMinMinutes && LineStaleCore.After(float.NaN) == LineStaleCore.kMinMinutes && LineStaleCore.After(999f) == LineStaleCore.kMaxMinutes, "tuning clamped");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
