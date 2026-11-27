using System;
using System.Collections.Generic;
using Ts3Fixes;

// docs/325: the instprobe summary math.
static class InstProbeTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static List<int> L(params int[] v) { return new List<int>(v); }

    static int Main()
    {
        // nearest rank: p50 of 1..4 = 2, p95 of 1..20 = 19, p100 = max, unsorted input, one sample, none
        Check(InstProbeCore.Pct(L(4, 1, 3, 2), 50) == 2, "p50 of 1..4 is 2 (nearest rank)");
        List<int> twenty = new List<int>();
        for (int i = 20; i >= 1; i--) twenty.Add(i);
        Check(InstProbeCore.Pct(twenty, 95) == 19 && InstProbeCore.Pct(twenty, 100) == 20 && InstProbeCore.Pct(twenty, 50) == 10, "p95 / max / p50 of 20 samples");
        Check(InstProbeCore.Pct(L(7), 50) == 7 && InstProbeCore.Pct(L(7), 95) == 7, "one sample: every percentile is it");
        Check(InstProbeCore.Pct(L(), 50) == 0 && InstProbeCore.Pct(null, 95) == 0, "no samples: 0");
        List<int> keep = L(3, 1, 2);
        InstProbeCore.Pct(keep, 50);
        Check(keep[0] == 3 && keep[1] == 1, "Pct leaves the samples in place");

        // frame median: lower middle of an even count, only the filled part of the ring
        Check(InstProbeCore.Median(new int[] { 9, 1, 5, 0, 0 }, 3) == 5, "median of the 3 filled entries");
        Check(InstProbeCore.Median(new int[] { 4, 2, 8, 6 }, 4) == 4, "even count: lower middle");
        Check(InstProbeCore.Median(new int[4], 0) == 0 && InstProbeCore.Median(new int[] { 3 }, 9) == 3, "empty ring 0, n clamped to the ring");

        // split: total 100, inner 90 (spot 10), ageup 20, create 50 (oncreation 15, onstartup 10, native 25), footprint 5, initobj 5, rest 10
        int[] p = InstProbeCore.Split(100, 90, 20, 50, 15, 10, 5, 5);
        Check(p.Length == 11, "split has 11 parts");
        Check(p[0] == 100 && p[1] == 80 && p[2] == 10 && p[3] == 20 && p[4] == 50 && p[5] == 25 && p[6] == 15 && p[7] == 10 && p[8] == 5 && p[9] == 5 && p[10] == 10,
            "split: total busy spot ageup create native oncreation onstartup footprint initobj rest");
        int[] q = InstProbeCore.Split(10, 10, 0, 30, 20, 20, 0, 0);
        Check(q[5] == 0 && q[10] == 0 && q[2] == 0, "split never goes below 0 (timer jitter, nested create)");
        int[] big = InstProbeCore.Split(uint.MaxValue, uint.MaxValue, 0, 0, 0, 0, 0, 0);
        Check(big[0] == int.MaxValue && big[10] == int.MaxValue, "split clips to int");
        Check(InstProbeCore.kParts[0] == "total" && InstProbeCore.kParts[10] == "rest" && InstProbeCore.kParts[13] == "f1x", "part names line up with Split");

        // phase: mark wins; before the first load = load; 2 min window; TickCount wrap
        Check(InstProbeCore.Phase(5, 0, false, null) == "load", "before the first world load: load");
        Check(InstProbeCore.Phase(1000 + InstProbeCore.kLoadWindowMs - 1, 1000, true, null) == "load", "inside the window: load");
        Check(InstProbeCore.Phase(1000 + InstProbeCore.kLoadWindowMs, 1000, true, "") == "steady", "after the window: steady");
        Check(InstProbeCore.Phase(int.MinValue + 10, int.MaxValue - 10, true, null) == "load", "TickCount wrap inside the window");
        Check(InstProbeCore.Phase(999999, 1000, true, "visitor") == "visitor", "a harness mark names the phase");

        // stats + line format (the queue's summary regex reads this exact text)
        InstProbeCore.Stats s = new InstProbeCore.Stats();
        s.Add("steady", "total", 2000); s.Add("steady", "total", 4000); s.Add("steady", "total", 30000);
        s.Add("load", "destroy", 1500);
        List<string> lines = s.Lines("end");
        Check(lines.Count == 2, "one line per (phase, part) with samples");
        Check(lines[0] == "instprobe: summary end load destroy n 1 p50 1.50 p95 1.50 max 1.50 ms", "load line: " + lines[0]);
        Check(lines[1] == "instprobe: summary end steady total n 3 p50 4.00 p95 30.00 max 30.00 ms", "steady line: " + lines[1]);
        for (int i = 0; i < InstProbeCore.kMaxSamples + 3; i++) s.Add("cap", "total", 1);
        Check(s.Get("cap", "total").Count == InstProbeCore.kMaxSamples && s.Dropped == 3, "sample cap per (phase, part), drops counted");
        s.Clear();
        Check(s.Lines("x").Count == 0 && s.Dropped == 0, "clear");

        Console.WriteLine(sFail == 0 ? "instprobe: PASS" : "instprobe: " + sFail + " FAIL");
        return sFail == 0 ? 0 : 1;
    }
}
