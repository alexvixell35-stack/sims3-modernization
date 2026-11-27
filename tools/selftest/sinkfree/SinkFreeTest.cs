// Usage: node tools/selftest/sinkfree/run.ts  (compiles this with mods/ts3fixes/src/SinkFreeCore.cs)
// Offline self-check of SinkFreeCore.cs (docs/686 §40): when a busy sink pick for dishes becomes the nearest free sink,
// switch and scope, the busy test and the nearest-sink choice with EA's level factor. Exit code 1 on failure.
using System;
using Ts3Fixes;

static class SinkFreeTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    const int N = SinkFreeCore.kNone, W = SinkFreeCore.kWould, X = SinkFreeCore.kExempt, G = SinkFreeCore.kGo;

    static void Rules()
    {
        Check(SinkFreeCore.Decide(true, true, true, true, 0) == G, "busy sink, a free one exists: wash at the free one");
        Check(SinkFreeCore.Decide(false, true, true, true, 0) == W, "switch off: would");
        Check(SinkFreeCore.Decide(true, false, true, true, 0) == X, "household Sim without sinkfreehh: exempt");
        Check(SinkFreeCore.Decide(false, false, true, true, 0) == X, "out of scope wins over off (counted as exempt)");
        Check(SinkFreeCore.Decide(true, true, true, false, 0) == N, "EA's sink is free: EA's pick");
        Check(SinkFreeCore.Decide(true, true, true, true, -1) == N, "no free sink: EA's pick");
        Check(SinkFreeCore.Decide(true, true, false, true, 0) == N, "EA picked a dishwasher or synthesizer: never changed");
        // every combination: Go only with on, in scope, a busy sink and a free one
        for (int m = 0; m < 32; m++)
        {
            bool on = (m & 1) != 0, sc = (m & 2) != 0, sink = (m & 4) != 0, busy = (m & 8) != 0; int free = (m & 16) != 0 ? 1 : -1;
            int d = SinkFreeCore.Decide(on, sc, sink, busy, free);
            bool want = on && sc && sink && busy && free >= 0;
            Check((d == G) == want, "combination " + m + ": go only when everything holds");
            if (!(sink && busy && free >= 0)) Check(d == N, "combination " + m + ": nothing to change = none");
        }
    }

    static void Busy()
    {
        Check(!SinkFreeCore.Busy(0, false), "nobody else: free");
        Check(SinkFreeCore.Busy(1, false), "another user: busy");
        Check(SinkFreeCore.Busy(0, true), "another Sim heads the line: busy");
    }

    static void Nearest()
    {
        float lv = 0.25f;
        Check(SinkFreeCore.Nearest(new float[] { 3f, 6f }, new bool[] { true, true }, new bool[] { false, false }, lv) == 0, "the nearer of two");
        Check(SinkFreeCore.Nearest(new float[] { 3f, 6f }, new bool[] { false, true }, new bool[] { false, false }, lv) == 1, "unusable skipped");
        Check(SinkFreeCore.Nearest(new float[] { 3f, 6f }, new bool[] { false, false }, null, lv) == -1, "none usable");
        Check(SinkFreeCore.Nearest(new float[] { 2f, 6f }, new bool[] { true, true }, new bool[] { true, false }, lv) == 1, "2 m upstairs counts as 8 m: the 6 m one on this level");
        Check(SinkFreeCore.Nearest(new float[] { 1f, 6f }, new bool[] { true, true }, new bool[] { true, false }, lv) == 0, "1 m upstairs counts as 4 m: still nearer");
        Check(SinkFreeCore.Nearest(null, null, null, lv) == -1, "no sinks");
        Check(SinkFreeCore.Nearest(new float[0], new bool[0], new bool[0], lv) == -1, "empty lot");
    }

    static int Main()
    {
        Rules(); Busy(); Nearest();
        Console.WriteLine(sFails == 0 ? "sinkfree selftest: all passed" : "sinkfree selftest: " + sFails + " failed");
        return sFails == 0 ? 0 : 1;
    }
}
