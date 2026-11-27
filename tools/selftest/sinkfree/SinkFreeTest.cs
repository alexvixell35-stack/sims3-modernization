// Usage: node tools/selftest/sinkfree/run.ts  (compiles this with mods/ts3fixes/src/SinkFreeCore.cs)
// Offline self-check of SinkFreeCore.cs (docs/686 §40): kitchen sinks first, never kitchen -> bathroom, the hand-in
// decision with its cap, the extra wash time and what happens to handed-in dishes when the wash ends or stops early.
// Exit code 1 on failure.
using System;
using Ts3Fixes;

static class SinkFreeTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    const int N = SinkFreeCore.kNone, W = SinkFreeCore.kWould, X = SinkFreeCore.kExempt, G = SinkFreeCore.kGo, C = SinkFreeCore.kCapped;
    const float L = 0.25f;

    static void Decide()
    {
        Check(SinkFreeCore.Decide(true, true, true, true) == G, "another sink: go");
        Check(SinkFreeCore.Decide(false, true, true, true) == W, "switch off: would");
        Check(SinkFreeCore.Decide(true, false, true, true) == X, "household Sim without sinkfreehh: exempt");
        Check(SinkFreeCore.Decide(true, true, true, false) == N, "same sink: nothing");
        Check(SinkFreeCore.Decide(true, true, false, true) == N, "EA picked a dishwasher or synthesizer: never changed");
        Check(!SinkFreeCore.Busy(0, false) && SinkFreeCore.Busy(1, false) && SinkFreeCore.Busy(0, true), "busy = another user or another line head");
        Check(SinkFreeCore.Kitchen(false, false) && !SinkFreeCore.Kitchen(true, false) && !SinkFreeCore.Kitchen(false, true), "kitchen = counter sink outside a bathroom");
    }

    static void Pick()
    {
        // sinks: 0 bathroom near, 1 kitchen far, 2 kitchen farther
        bool[] kit = { false, true, true };
        float[] d = { 1f, 5f, 8f };
        Check(SinkFreeCore.Pick(0, kit, new bool[] { false, false, false }, d, null, L) == 1, "free bathroom pick, free kitchen exists: kitchen first");
        Check(SinkFreeCore.Pick(0, kit, new bool[] { true, true, false }, d, null, L) == 2, "nearest kitchen busy: the free one");
        Check(SinkFreeCore.Pick(0, kit, new bool[] { true, true, true }, d, null, L) == 1, "every kitchen busy: the nearest kitchen (hand-in or its line), not the bathroom");
        Check(SinkFreeCore.Pick(1, kit, new bool[] { false, true, true }, d, null, L) == 1, "EA's busy kitchen, only the bathroom free: stays (never kitchen -> bathroom)");
        Check(SinkFreeCore.Pick(1, kit, new bool[] { false, true, false }, d, null, L) == 2, "EA's busy kitchen, another kitchen free: that one");
        Check(SinkFreeCore.Pick(2, kit, new bool[] { false, false, false }, d, null, L) == 2, "EA's free kitchen: kept");
        bool[] bathOnly = { false, false };
        Check(SinkFreeCore.Pick(0, bathOnly, new bool[] { true, false }, new float[] { 1f, 4f }, null, L) == 1, "no kitchen sink: the free bathroom sink");
        Check(SinkFreeCore.Pick(0, bathOnly, new bool[] { true, true }, new float[] { 1f, 4f }, null, L) == 0, "no kitchen, none free: EA's");
        Check(SinkFreeCore.Pick(0, bathOnly, new bool[] { false, true }, new float[] { 1f, 4f }, null, L) == 0, "no kitchen, EA's free: EA's");
        Check(SinkFreeCore.Pick(-1, kit, new bool[3], d, null, L) == -1, "EA's pick not on the lot: EA's");
        Check(SinkFreeCore.Pick(0, kit, new bool[] { false, false, false }, new float[] { 1f, 2f, 6f }, new bool[] { false, true, false }, L) == 2, "kitchen upstairs counts 4x: the one on this level");
        // never kitchen -> bathroom, every combination of busy for a two-sink house (0 bathroom, 1 kitchen)
        for (int m = 0; m < 4; m++)
        {
            bool[] b = { (m & 1) != 0, (m & 2) != 0 };
            Check(SinkFreeCore.Pick(1, new bool[] { false, true }, b, new float[] { 1f, 5f }, null, L) == 1, "kitchen pick stays kitchen, busy mask " + m);
            Check(SinkFreeCore.Pick(0, new bool[] { false, true }, b, new float[] { 1f, 5f }, null, L) == 1, "bathroom pick goes to the kitchen, busy mask " + m);
        }
    }

    static void Nearest()
    {
        Check(SinkFreeCore.Nearest(new float[] { 3f, 6f }, new bool[] { true, true }, null, L) == 0, "the nearer of two");
        Check(SinkFreeCore.Nearest(new float[] { 3f, 6f }, new bool[] { false, false }, null, L) == -1, "none usable");
        Check(SinkFreeCore.Nearest(null, null, null, L) == -1 && SinkFreeCore.Nearest(new float[0], new bool[0], null, L) == -1, "no sinks");
    }

    static void Handoff()
    {
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 2, 0) == G, "kitchen sink, someone washing, no free kitchen: hand in");
        Check(SinkFreeCore.Handoff(false, true, true, true, false, 2, 0) == W, "off: would");
        Check(SinkFreeCore.Handoff(true, false, true, true, false, 2, 0) == X, "out of scope: exempt");
        Check(SinkFreeCore.Handoff(true, true, false, true, false, 2, 0) == N, "bathroom sink: never");
        Check(SinkFreeCore.Handoff(true, true, true, false, false, 2, 0) == N, "nobody washing (a teeth brusher, or free): EA's wash");
        Check(SinkFreeCore.Handoff(true, true, true, true, true, 2, 0) == N, "another kitchen sink free: no hand-in (the pick sends it there)");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 0, 0) == N, "nothing held: nothing");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 3, 0) == G, "3 dishes, none handed yet: at the cap, ok");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 1, 2) == G, "third dish of this wash: ok");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 1, 3) == C, "cap reached: EA's line");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 2, 2) == C, "would go past the cap: EA's line");
        Check(SinkFreeCore.Handoff(true, true, true, true, false, 4, 0) == C, "a stack of 4: EA's line");
        Check(SinkFreeCore.Handoff(false, false, true, true, false, 4, 0) == C, "cap counted before scope and switch");
        for (int h = 0; h <= 4; h++)
            for (int n = 1; n <= 4; n++)
            {
                int r = SinkFreeCore.Handoff(true, true, true, true, false, n, h);
                Check((r == G) == (h + n <= SinkFreeCore.kHandCap), "cap " + h + "+" + n);
            }
    }

    static void Times()
    {
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, 0, 1) - 2.8125f) < 1e-4, "45 sim-min wash: 1 dish = 45/16 = 2.81 sim-min");
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, 0, 2) - 5.625f) < 1e-4, "45 sim-min wash: 2 dishes = 5.63 sim-min");
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, 0, 3) - 8.4375f) < 1e-4, "45 sim-min wash: 3 dishes = 8.44 sim-min (under the 9 cap)");
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, 0, 5) - 9f) < 1e-4, "5 dishes capped at 20 % = 9 sim-min");
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, 2, 2) - 3.375f) < 1e-4, "2 more after 2: up to the cap, 9 - 5.625 = 3.375");
        Check(SinkFreeCore.ExtraMinutes(45f, 4, 1) == 0f, "past the cap: nothing more");
        float sum = 0f; for (int i = 0; i < 10; i++) sum += SinkFreeCore.ExtraMinutes(45f, i, 1);
        Check(Math.Abs(sum - 9f) < 1e-4, "one at a time never passes the cap");
        Check(SinkFreeCore.ExtraMinutes(45f, 0, 0) == 0f && SinkFreeCore.ExtraMinutes(0f, 0, 2) == 0f && SinkFreeCore.ExtraMinutes(-5f, 0, 2) == 0f, "no time for nothing");
        Check(Math.Abs(SinkFreeCore.ExtraMinutes(45f, -3, 1) - 2.8125f) < 1e-4, "a bad count before counts as none");
    }

    static void End()
    {
        Check(SinkFreeCore.EndDish(true, true) == SinkFreeCore.kEndWashed, "wash done, dishes still down: washed");
        Check(SinkFreeCore.EndDish(false, true) == SinkFreeCore.kEndLeft, "washer stopped early: dishes stay down, dirty (EA's cleanup)");
        Check(SinkFreeCore.EndDish(true, false) == SinkFreeCore.kEndTaken, "picked up by another Sim meanwhile: left to that Sim (no duplicate)");
        Check(SinkFreeCore.EndDish(false, false) == SinkFreeCore.kEndTaken, "stopped early and taken: left to that Sim");
    }

    static int Main()
    {
        Decide(); Pick(); Nearest(); Handoff(); Times(); End();
        Console.WriteLine(sFails == 0 ? "sinkfree selftest: all passed" : "sinkfree selftest: " + sFails + " failed");
        return sFails == 0 ? 0 : 1;
    }
}
