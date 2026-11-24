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

        PartitionChecks(rnd);
        SharedDropChecks(rnd);

        Console.WriteLine(sFails == 0 ? "loopcost selftest: PASS" : "loopcost selftest: FAIL " + sFails);
        Environment.Exit(sFails == 0 ? 0 : 1);
    }

    // docs/334 LC-1 (a): stand-ins for Sim, Fish, CarOwnable / BoatOwnable, HorseshoeCourt, Dartboard; mask as DrTypeMask
    class TSim { } class TSimKid : TSim { } class TFish { } class TCar { } class TBoat { } class TShoe { } class TDart { } class TOther { }
    static int Mask(object o) { int m = 0; if (o is TSim) m |= 1; if (o is TFish) m |= 2; if (o is TCar || o is TBoat) m |= 4; if (o is TShoe) m |= 8; if (o is TDart) m |= 16; return m; }
    static List<object> Filter(List<object> all, Predicate<object> keep) { List<object> l = new List<object>(); foreach (object o in all) if (keep(o)) l.Add(o); return l; }

    static void PartitionChecks(Random rnd)
    {
        Predicate<object>[] filters = { o => o is TSim, o => o is TFish, o => o is TCar || o is TBoat, o => o is TShoe, o => o is TDart };
        for (int round = 0; round < 300; round++)
        {
            List<object> all = new List<object>();
            int n = rnd.Next(0, 400);
            for (int i = 0; i < n; i++)
            {
                switch (rnd.Next(9))
                {
                    case 0: all.Add(new TSim()); break; case 1: all.Add(new TSimKid()); break; case 2: all.Add(new TFish()); break;
                    case 3: all.Add(new TCar()); break; case 4: all.Add(new TBoat()); break; case 5: all.Add(new TShoe()); break;
                    case 6: all.Add(new TDart()); break; case 7: all.Add(null); break; default: all.Add(new TOther()); break;
                }
            }
            // sliced as the sweep slices it: clock every 1-11 items, random budget
            List<object>[] parts = new List<object>[5];
            for (int i = 0; i < 5; i++) parts[i] = new List<object>();
            SlicedPass<object> pass = new SlicedPass<object>(all);
            long t = 0; int every = rnd.Next(1, 12);
            while (!pass.Done) { long t0 = t; pass.RunSliceEvery(delegate(object o) { t += 3; int m = Mask(o); if (m != 0) LoopCostCore.PartitionAdd(o, m, parts); }, int.MaxValue, rnd.Next(5, 200), delegate { return t - t0; }, every); }
            for (int k = 0; k < 5; k++)
            {
                List<object> want = Filter(all, filters[k]);
                Check(LoopCostCore.SameItems(want, parts[k]), "partition list " + k + " = its own filter pass (same items, same order)");
                if (want.Count > 1) { List<object> swapped = new List<object>(want); swapped.Reverse(); Check(want[0] == want[want.Count - 1] || !LoopCostCore.SameItems(want, swapped) || want.Count == 1, "SameItems sees order"); }
            }
        }
        // several bits at once land in each list
        List<object>[] p2 = { new List<object>(), new List<object>(), new List<object>() };
        object x = new object();
        LoopCostCore.PartitionAdd(x, 5, p2);
        Check(p2[0].Count == 1 && p2[1].Count == 0 && p2[2].Count == 1, "mask 101 adds to lists 0 and 2");
    }

    // docs/334 LC-1 (b): the old two-body flow (HandlerBody then CookingBody, each its own read and list) against SharedDrop
    class Holder { public Delegate Ev; public int Reads; public bool ThrowOnGone; public int Listeners; }
    class TRel { public bool Gone; public void H() { } }
    class TCook { public bool Stale; public object ObjectClickedOn; public bool Listener = true; public void H() { } }
    static Delegate Get(object h) { Holder x = (Holder)h; x.Reads++; return x.Ev; }
    static void Set(object h, Delegate v) { ((Holder)h).Ev = v; }
    static bool RelGone(Delegate d, object h) { TRel r = d.Target as TRel; if (r != null && ((Holder)h).ThrowOnGone) throw new InvalidOperationException("gone"); return r != null && r.Gone; }
    static bool CpStale(Delegate d, object h) { TCook cp = d.Target as TCook; return cp != null && (cp.Stale || cp.ObjectClickedOn != h); }
    static void CpDropped(Delegate d, object h) { TCook cp = (TCook)d.Target; if (cp.ObjectClickedOn == h) cp.ObjectClickedOn = null; if (cp.ObjectClickedOn == null && cp.Listener) { cp.Listener = false; ((Holder)h).Listeners++; } }

    static void OldFlow(Holder h, bool cookOn, int[] c)
    {
        try
        {
            Delegate ev = Get(h);
            if (ev != null)
            {
                bool changed = false;
                foreach (Delegate d in ev.GetInvocationList()) { if (!RelGone(d, h)) continue; ev = Delegate.Remove(ev, d); c[0]++; changed = true; }
                if (changed) Set(h, ev);
            }
        }
        catch (Exception) { c[2]++; }
        try
        {
            if (!cookOn) return;
            Delegate ev = Get(h);
            if (ev == null) return;
            foreach (Delegate d in ev.GetInvocationList())
            {
                if (!CpStale(d, h)) continue;
                Set(h, Delegate.Remove(Get(h), d));
                CpDropped(d, h);
                c[1]++;
            }
        }
        catch (Exception) { c[2]++; }
    }

    static void SharedDropChecks(Random rnd)
    {
        for (int round = 0; round < 2000; round++)
        {
            int nr = rnd.Next(0, 5), nc = rnd.Next(0, 5);
            bool cookOn = rnd.Next(4) != 0, thr = rnd.Next(10) == 0;
            Holder[] hs = { new Holder(), new Holder() };
            List<TRel> rels = new List<TRel>(); List<TCook> cooks = new List<TCook>();
            for (int i = 0; i < nr; i++) rels.Add(new TRel { Gone = rnd.Next(3) == 0 });
            // a template cooking process: Stale = no owner, Listener reused as "names this holder"
            for (int i = 0; i < nc; i++) cooks.Add(new TCook { Stale = rnd.Next(3) == 0, Listener = rnd.Next(3) != 0 });
            List<object> order = new List<object>();
            foreach (TRel r in rels) order.Add(r);
            foreach (TCook k in cooks) order.Add(k);
            for (int i = order.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); object tmp = order[i]; order[i] = order[j]; order[j] = tmp; }
            // twin states: one for each flow (the cooking targets are mutated, so each holder gets its own copies)
            List<TCook>[] ck = { new List<TCook>(), new List<TCook>() };
            for (int s = 0; s < 2; s++)
            {
                hs[s].ThrowOnGone = thr;
                foreach (object o in order)
                {
                    Action a;
                    if (o is TRel) a = ((TRel)o).H;
                    else { TCook src = (TCook)o; TCook cp = new TCook { Stale = src.Stale, ObjectClickedOn = src.Listener ? hs[s] : null }; ck[s].Add(cp); a = cp.H; }
                    hs[s].Ev = Delegate.Combine(hs[s].Ev, a);
                }
            }
            int[] co = new int[3], cn = new int[5];
            OldFlow(hs[0], cookOn, co);
            LoopCostCore.SharedDrop(hs[1], Get, Set, RelGone, cookOn ? (LoopCostCore.EvTest)CpStale : null, CpDropped, cn, 0, 1, 2, 3, 4);
            Check(co[0] == cn[0] && co[1] == cn[1] && co[2] == cn[2], "same removals and errors (" + co[0] + "/" + co[1] + "/" + co[2] + " vs " + cn[0] + "/" + cn[1] + "/" + cn[2] + ")");
            Check(hs[0].Listeners == hs[1].Listeners, "same listeners dropped");
            Delegate[] lo = hs[0].Ev != null ? hs[0].Ev.GetInvocationList() : new Delegate[0], ln = hs[1].Ev != null ? hs[1].Ev.GetInvocationList() : new Delegate[0];
            bool same = lo.Length == ln.Length;
            for (int i = 0; same && i < lo.Length; i++) same = lo[i].Target.GetType() == ln[i].Target.GetType() && (!(lo[i].Target is TRel) || lo[i].Target == ln[i].Target);
            Check(same, "same handlers left, same order");
            for (int i = 0; i < ck[0].Count; i++) Check((ck[0][i].ObjectClickedOn == hs[0]) == (ck[1][i].ObjectClickedOn == hs[1]) && ck[0][i].Listener == ck[1][i].Listener, "same cooking process state");
            // reads: one when the handler part dropped nothing and did not throw (plus one per cooking removal, as before)
            int cookRemovals = cn[1];
            bool reread = cn[0] > 0 || cn[2] > 0;
            if (!cookOn) Check(hs[1].Reads == 1, "cooking off: one read");
            else Check(hs[1].Reads == (reread ? 2 : 1) + cookRemovals, "reads: " + hs[1].Reads + " (reread " + reread + ", cook removals " + cookRemovals + ")");
            if (cookOn && !thr && hs[0].Reads > 0) Check(hs[1].Reads == hs[0].Reads - (reread ? 0 : 1), "one read fewer when the handler part changed nothing");
        }
        // no handler part (field getter missing): cooking reads once itself
        Holder solo = new Holder(); TCook only = new TCook { Stale = true }; solo.Ev = (Action)only.H;
        int[] c1 = new int[5];
        LoopCostCore.SharedDrop(solo, Get, Set, null, CpStale, CpDropped, c1, 0, 1, 2, 3, 4);
        Check(solo.Ev == null && c1[1] == 1 && solo.Reads == 2, "no handler part: cooking alone (read + removal read)");
    }
}
