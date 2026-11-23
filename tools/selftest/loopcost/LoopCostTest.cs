using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of docs/330 (our own loop cost): LoopCostCore's group cadence, viewer-table threshold, held-walk
// cadence and id compare, and SlicedPass.RunSliceEvery. Simulations check the staleness bounds the doc states.
static class LoopCostTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // GroupDue: any reason runs the group
        Check(!LoopCostCore.GroupDue(false, false, 3.9f, 4f), "clean group under the cap skips");
        Check(LoopCostCore.GroupDue(true, false, 0f, 4f), "dirty runs");
        Check(LoopCostCore.GroupDue(false, true, 0f, 4f), "last run found something runs");
        Check(LoopCostCore.GroupDue(false, false, 4f, 4f), "cap reached runs");

        // Group cadence simulation: hourly passes, random events; an event-born dead ref is cleaned at the next pass, any
        // other within the cap (+1 h for the hourly alarm grain)
        Random rnd = new Random(330);
        for (int round = 0; round < 200; round++)
        {
            bool dirty = true, found = false; float lastH = 0f; int pending = 0; int bornH = -1; bool bornByEvent = false;
            for (int h = 1; h < 500; h++)
            {
                if (pending == 0 && rnd.Next(10) == 0) { pending = 1; bornH = h; bornByEvent = rnd.Next(2) == 0; if (bornByEvent) dirty = true; }
                if (!LoopCostCore.GroupDue(dirty, found, h - lastH, 4f)) continue;
                dirty = false;
                found = pending > 0;
                if (pending > 0)
                {
                    int delay = h - bornH;
                    Check(bornByEvent ? delay <= 1 : delay <= 4, "dead ref removed within its bound (" + delay + " h, event " + bornByEvent + ")");
                    pending = 0;
                }
                lastH = h;
            }
        }

        // ViewersDue
        List<float> a = new List<float>(new float[] { 0, 0, 0, 10, 0, 0 });
        Check(LoopCostCore.ViewersDue(null, a, 0.5f, 0, 500), "never sent: due");
        Check(LoopCostCore.ViewersDue(a, a, 0.5f, -1, 500), "no send time: due");
        Check(!LoopCostCore.ViewersDue(a, new List<float>(a), 0.5f, 100, 500), "same table, fresh: not due");
        Check(LoopCostCore.ViewersDue(a, new List<float>(a), 0.5f, 500, 500), "beat: due");
        Check(LoopCostCore.ViewersDue(a, new List<float>(new float[] { 0, 0, 0 }), 0.5f, 0, 500), "count changed: due");
        Check(!LoopCostCore.ViewersDue(a, new List<float>(new float[] { 0.3f, 0, 0.3f, 10, 0, 0 }), 0.5f, 0, 500), "0.42 m: not due");
        Check(LoopCostCore.ViewersDue(a, new List<float>(new float[] { 0, 0, 0, 10, 0.6f, 0 }), 0.5f, 0, 500), "second viewer 0.6 m: due");

        // Viewer simulation: per wake every viewer moves up to 0.8 m (a running Sim over 5 ticks); after each wake the native
        // table is within moveM of the wake's positions and never older than the beat
        for (int round = 0; round < 300; round++)
        {
            int n = rnd.Next(1, 9);
            List<float> cur = new List<float>(), sent = null; long ms = 0, sentMs = 0; int sends = 0, wakes = 0;
            for (int i = 0; i < n * 3; i++) cur.Add((float)rnd.NextDouble() * 100f);
            for (int w = 0; w < 400; w++)
            {
                ms += 60 + rnd.Next(80);  // 5 ticks
                bool still = rnd.Next(3) == 0;
                for (int i = 0; i < cur.Count; i++) if (!still) cur[i] += (float)(rnd.NextDouble() - 0.5) * 0.9f;
                wakes++;
                if (LoopCostCore.ViewersDue(sent, cur, 0.5f, sent == null ? -1 : ms - sentMs, 500)) { sent = new List<float>(cur); sentMs = ms; sends++; }
                for (int i = 0; i + 2 < cur.Count; i += 3)
                {
                    float dx = cur[i] - sent[i], dy = cur[i + 1] - sent[i + 1], dz = cur[i + 2] - sent[i + 2];
                    Check(dx * dx + dy * dy + dz * dz <= 0.25f + 1e-4f, "native table within 0.5 m after a wake");
                }
                Check(ms - sentMs < 500, "native table younger than the beat after a wake");
            }
            Check(sends <= wakes, "never more sends than wakes");
        }

        // HeldWalkDue: a ride that starts with none in progress is walked into within sparse wakes; while rides run, every wake
        Check(LoopCostCore.HeldWalkDue(true, 3, 4), "ride seen: walk");
        Check(LoopCostCore.HeldWalkDue(false, 8, 4), "sparse wake: walk");
        Check(!LoopCostCore.HeldWalkDue(false, 9, 4), "no ride, off wake: skip");
        Check(LoopCostCore.HeldWalkDue(false, 9, 1), "sparse 1 = every wake");
        for (int start = 1; start < 50; start++)
        {
            int found = -1;
            for (int w = start; w < start + 10; w++) if (LoopCostCore.HeldWalkDue(false, w, 4)) { found = w; break; }
            Check(found >= start && found - start <= 3, "new ride found within 4 wakes (start " + start + ")");
        }

        // SameIds
        Check(LoopCostCore.SameIds(new List<ulong> { 1, 2 }, new List<ulong> { 1, 2 }), "same ids");
        Check(!LoopCostCore.SameIds(new List<ulong> { 1, 2 }, new List<ulong> { 2, 1 }), "order matters");
        Check(!LoopCostCore.SameIds(new List<ulong> { 1 }, new List<ulong> { 1, 2 }), "count matters");

        // RunSliceEvery: every item once, in order; the clock read every `every` items; a slice at most every - 1 items past
        for (int round = 0; round < 500; round++)
        {
            int n = rnd.Next(0, 300), every = rnd.Next(1, 12), costUs = rnd.Next(1, 60);
            List<int> items = new List<int>();
            for (int i = 0; i < n; i++) items.Add(i);
            SlicedPass<int> pass = new SlicedPass<int>(items);
            List<int> seen = new List<int>();
            long t = 0; int reads = 0;
            while (!pass.Done)
            {
                long t0 = t; int before = seen.Count, readsBefore = reads;
                int got = pass.RunSliceEvery(delegate(int v) { seen.Add(v); t += costUs; }, int.MaxValue, 2000, delegate { reads++; return t - t0; }, every);
                Check(got == seen.Count - before && got > 0, "slice count");
                int maxItems = (int)Math.Ceiling(2000.0 / costUs);
                Check(got <= maxItems + every - 1, "slice overrun at most every - 1 items (" + got + " vs " + maxItems + ", every " + every + ")");
                Check(reads - readsBefore <= got / Math.Max(1, every) + 1, "clock reads per slice");
            }
            Check(seen.Count == n, "every item once");
            for (int i = 0; i < seen.Count; i++) Check(seen[i] == i, "items in order");
        }
        // maxItems still caps
        SlicedPass<int> capped = new SlicedPass<int>(new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        Check(capped.RunSliceEvery(delegate(int v) { }, 3, long.MaxValue, delegate { return 0; }, 8) == 3, "maxItems caps the slice");

        Console.WriteLine(sFails == 0 ? "loopcost selftest: PASS" : "loopcost selftest: FAIL " + sFails);
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
