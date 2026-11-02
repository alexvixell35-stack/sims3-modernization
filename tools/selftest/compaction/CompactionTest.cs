using System;
using Ts3Fixes;

// Offline self-check of CompactionCore (docs/204 §4-§5).
static class CompactionTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // scope: active household, the active lot, or within 30 m of the camera target
        Check(CompactionCore.InScope(true, false, 999f), "active household anywhere");
        Check(CompactionCore.InScope(false, true, 999f), "on the active lot");
        Check(CompactionCore.InScope(false, false, 30f), "30 m from the camera target");
        Check(!CompactionCore.InScope(false, false, 30.01f), "just past 30 m");
        Check(!CompactionCore.InScope(false, false, float.MaxValue), "no camera reading");

        // "~N s": the last load rounded up to 10 s, 60 s when unknown
        Check(CompactionCore.EstimateSeconds(0) == 60 && CompactionCore.EstimateSeconds(-5) == 60, "unknown load time");
        Check(CompactionCore.EstimateSeconds(1) == 10, "under a second");
        Check(CompactionCore.EstimateSeconds(41000) == 50 && CompactionCore.EstimateSeconds(40000) == 40, "rounded up to 10 s");
        Check(CompactionCore.PromptText(50) == "Memory is getting low. Compact now? (save and reload, ~50 s)", "prompt text");

        // nearest object within 0.5 m
        Check(CompactionCore.PickNearest(new float[] { 0.4f, 0.1f, 0.3f }, 0.5f) == 1, "nearest");
        Check(CompactionCore.PickNearest(new float[] { 0.6f, 0.51f }, 0.5f) == -1, "none within 0.5 m");
        Check(CompactionCore.PickNearest(new float[0], 0.5f) == -1, "no candidates");
        Check(CompactionCore.PickNearest(new float[] { 0.5f }, 0.5f) == 0, "exactly 0.5 m");

        // native bit answers -> value
        bool[] b = new bool[16];
        b[0] = b[3] = b[9] = true;
        Check(CompactionCore.FromBits(b) == (1 | 8 | 512), "bits");
        Check(CompactionCore.FromBits(new bool[20]) == 0, "no bits, longer array");

        Check(CompactionCore.DoneText(412) == "Memory compacted: +412 MB of headroom" && CompactionCore.DoneText(0) == "Memory compacted", "done text");

        Console.WriteLine(sFails == 0 ? "compaction selftest: ok" : "compaction selftest: " + sFails + " failures");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
