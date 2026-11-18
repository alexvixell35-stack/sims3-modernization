using System;
using System.Collections.Generic;
using Ts3Fixes;

// Offline self-check of EaSpreadCore (docs/147): every item exactly once per pass at any slice size, and the sliced daily
// relationship decay ending with the same relationships as the game's one-tick DecayAll, day after day.
static class EaSpreadTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void SlicedPassCoversEachItemOnce(Random rnd)
    {
        for (int size = 0; size < 60; size++)
            for (int maxItems = 1; maxItems < 10; maxItems++)
            {
                List<int> items = new List<int>();
                for (int i = 0; i < size; i++) items.Add(i);
                SlicedPass<int> pass = new SlicedPass<int>(items);
                List<int> seen = new List<int>();
                long clock = 0;
                int slices = 0;
                while (!pass.Done)
                {
                    long start = clock;
                    int n = pass.RunSlice(delegate(int v) { seen.Add(v); clock += rnd.Next(0, 900); }, maxItems, 2000, delegate { return clock - start; });
                    Check(n >= 1 && n <= maxItems, "slice size " + n + " within 1.." + maxItems);
                    if (++slices > size + 1) { Check(false, "pass never ends"); break; }
                }
                Check(seen.Count == size, "size " + size + ": " + seen.Count + " handed out");
                for (int i = 0; i < seen.Count; i++) Check(seen[i] == i, "item " + i + " in order, once");
            }
        // the time budget ends a slice early, but never before its first item
        SlicedPass<int> slow = new SlicedPass<int>(new int[] { 1, 2, 3 });
        long t = 0;
        Check(slow.RunSlice(delegate(int v) { t += 5000; }, 10, 2000, delegate { return t; }) == 1, "over budget after one item");
    }

    // Model of the decay: pairs keyed both ways like Relationship.sAllRelationships; the owner (lower id) steps the pair's
    // liking one unit toward 0; at 0 the first pass notes the day, a pass >= 1 day later drops the pair at the end.
    sealed class Pair { public int Liking; public int ZeroSince = -1; public int Steps; }
    sealed class World
    {
        public Dictionary<ulong, Dictionary<ulong, Pair>> All = new Dictionary<ulong, Dictionary<ulong, Pair>>();
        public List<Pair> Drops = new List<Pair>();
        public Dictionary<Pair, ulong[]> Ends = new Dictionary<Pair, ulong[]>();

        public void Add(ulong a, ulong b, int liking)
        {
            Pair p = new Pair();
            p.Liking = liking;
            Side(a)[b] = p;
            Side(b)[a] = p;
            Ends[p] = new ulong[] { a, b };
        }
        Dictionary<ulong, Pair> Side(ulong id)
        {
            Dictionary<ulong, Pair> d;
            if (!All.TryGetValue(id, out d)) { d = new Dictionary<ulong, Pair>(); All[id] = d; }
            return d;
        }
        public void StepKey(ulong key, int day)
        {
            Dictionary<ulong, Pair> d;
            if (!All.TryGetValue(key, out d)) return;
            foreach (ulong other in new List<ulong>(d.Keys))
            {
                if (!EaSpreadCore.OwnsPair(key, other)) continue;
                Step(d[other], day);
            }
        }
        // The game's side of DailyPairPass (EaSpread DecayPartners / DecayStep): partners read at the Sim's turn; a pair
        // removed or a Sim gone since then is skipped.
        public List<ulong> Partners(ulong key)
        {
            Dictionary<ulong, Pair> d;
            return All.TryGetValue(key, out d) ? new List<ulong>(d.Keys) : null;
        }
        public bool StepPair(ulong key, ulong other, int day)
        {
            Dictionary<ulong, Pair> d;
            Pair p;
            if (!EaSpreadCore.OwnsPair(key, other) || !All.TryGetValue(key, out d) || !d.TryGetValue(other, out p)) return false;
            Step(p, day);
            return true;
        }
        public void Remove(ulong a, ulong b) { All[a].Remove(b); All[b].Remove(a); }
        public void RemoveSim(ulong a)
        {
            foreach (ulong b in new List<ulong>(All[a].Keys)) All[b].Remove(a);
            All.Remove(a);
        }
        // drops a pass starting on this day makes: pairs already at 0, noted >= 1 day ago (the verify count)
        public int DropsDue(int day)
        {
            int n = 0;
            foreach (KeyValuePair<ulong, Dictionary<ulong, Pair>> kv in All)
                foreach (KeyValuePair<ulong, Pair> p in kv.Value)
                    if (EaSpreadCore.OwnsPair(kv.Key, p.Key) && p.Value.Liking == 0 && p.Value.ZeroSince >= 0 && day - p.Value.ZeroSince >= 1) n++;
            return n;
        }
        void Step(Pair p, int day)
        {
            p.Steps++;
            if (p.Liking > 0) p.Liking--;
            else if (p.Liking < 0) p.Liking++;
            if (p.Liking != 0) { p.ZeroSince = -1; return; }
            if (p.ZeroSince < 0) p.ZeroSince = day;
            else if (day - p.ZeroSince >= 1) Drops.Add(p);
        }
        public void EndPass()
        {
            foreach (Pair p in Drops) { ulong[] e = Ends[p]; All[e[0]].Remove(e[1]); All[e[1]].Remove(e[0]); }
            Drops.Clear();
        }
        public void DecayAllAtOnce(int day)
        {
            foreach (ulong key in new List<ulong>(All.Keys)) StepKey(key, day);
            EndPass();
        }
        public string State()
        {
            List<string> rows = new List<string>();
            foreach (KeyValuePair<ulong, Dictionary<ulong, Pair>> kv in All)
                foreach (KeyValuePair<ulong, Pair> p in kv.Value)
                    if (kv.Key < p.Key) rows.Add(kv.Key + "-" + p.Key + ":" + p.Value.Liking + "/" + p.Value.ZeroSince + "/" + p.Value.Steps);
            rows.Sort(StringComparer.Ordinal);
            return string.Join(",", rows.ToArray());
        }
    }

    static World MakeWorld(int seed)
    {
        Random r = new Random(seed);
        World w = new World();
        List<ulong> ids = new List<ulong>();
        for (int i = 0; i < 40; i++) ids.Add((ulong)r.Next(1, 1000000) * 7919UL);
        for (int i = 0; i < 300; i++)
        {
            ulong a = ids[r.Next(ids.Count)], b = ids[r.Next(ids.Count)];
            if (a != b && !(w.All.ContainsKey(a) && w.All[a].ContainsKey(b))) w.Add(a, b, r.Next(-6, 7));
        }
        return w;
    }

    // The same world decayed by DailyPass in random slices (sometimes none before the next alarm, so Begin has to finish
    // the old pass) must equal the one-tick reference after every day.
    static void DailyDecayMatchesReference()
    {
        for (int seed = 1; seed <= 200; seed++)
        {
            World reference = MakeWorld(seed), sliced = MakeWorld(seed);
            Random r = new Random(seed * 31);
            DailyPairPass<ulong, ulong> pass = new DailyPairPass<ulong, ulong>();
            int forced = 0;
            for (int day = 0; day < 12; day++)
            {
                int refDue = reference.DropsDue(day), refDrops = 0;
                foreach (ulong key in new List<ulong>(reference.All.Keys)) reference.StepKey(key, day);
                refDrops = reference.Drops.Count;
                reference.EndPass();
                Check(refDue == refDrops, "seed " + seed + " day " + day + ": drops due at start (" + refDue + ") = drops made (" + refDrops + ")");
                int passDay = day; // the alarm time, as EaSpread captures it
                if (pass.Begin(new List<ulong>(sliced.All.Keys), sliced.Partners, delegate(ulong k, ulong o) { return sliced.StepPair(k, o, passDay); }, sliced.EndPass)) forced++;
                int slices = r.Next(0, 40), stepped;
                for (int s = 0; s < slices; s++)
                {
                    int max = r.Next(1, 9);
                    bool done = pass.Slice(max, 2000, delegate { return 0; }, out stepped);
                    Check(stepped <= max, "slice steps at most its budget of pairs");
                    if (done) break;
                }
                if (!pass.Running) Check(reference.State() == sliced.State(), "seed " + seed + " day " + day + ": sliced pass equals the reference");
            }
            pass.Finish();
            Check(reference.State() == sliced.State(), "seed " + seed + ": equal after the last pass (" + forced + " passes finished by the next alarm)");
        }
    }

    static int OwnedSteps(World w)
    {
        int n = 0;
        foreach (KeyValuePair<ulong, Dictionary<ulong, Pair>> kv in w.All)
            foreach (KeyValuePair<ulong, Pair> p in kv.Value) if (kv.Key < p.Key) n += p.Value.Steps;
        return n;
    }

    // A Sim with 300 relationships is split over ticks (a slice resumes at its next partner); while it is mid-list, it gains
    // and loses relationships and another Sim is destroyed. Pairs present throughout get exactly one step; removed ones at
    // most one; ones added after its list was read none (DecayAll reads each Sim's list at its turn too); no pair two.
    static void GiantSimChangesMidPass()
    {
        World w = new World();
        for (ulong b = 2; b < 302; b++) w.Add(1, b, 3);          // Sim 1 owns 300 pairs
        for (ulong b = 1001; b < 1011; b++) w.Add(1000, b, 3);   // Sim 1000, destroyed mid-pass
        List<ulong> keys = new List<ulong>(w.All.Keys);
        keys.Remove(1); keys.Insert(0, 1);                       // Sim 1 first
        DailyPairPass<ulong, ulong> pass = new DailyPairPass<ulong, ulong>();
        pass.Begin(keys, w.Partners, delegate(ulong k, ulong o) { return w.StepPair(k, o, 0); }, w.EndPass);
        int stepped, slices = 0;
        Check(!pass.Slice(16, 2000, delegate { return 0; }, out stepped) && stepped == 16, "first slice stops after 16 pairs of the giant Sim");
        slices++;
        Pair removed = w.All[1][300];
        w.Remove(1, 300);                                        // not reached yet: never stepped
        Pair removedDone = w.All[1][2];
        w.Remove(1, 2);                                          // already stepped: stays at one
        w.Add(1, 5000, 3);                                       // added after Sim 1's list was read
        Pair late = w.All[1][5000];
        w.RemoveSim(1000);
        while (!pass.Slice(16, 2000, delegate { return 0; }, out stepped)) { Check(stepped == 16, "full slices step exactly 16 pairs"); slices++; }
        Check(slices >= 300 / 16, "the giant Sim took >= 18 slices (" + slices + ")");
        Check(removed.Steps == 0 && removedDone.Steps == 1 && late.Steps == 0, "removed later: 0, removed after its step: 1, added after the read: 0");
        foreach (KeyValuePair<ulong, Dictionary<ulong, Pair>> kv in w.All)
            foreach (KeyValuePair<ulong, Pair> p in kv.Value)
                if (kv.Key < p.Key && p.Value != late) Check(p.Value.Steps == 1, "pair " + kv.Key + "-" + p.Key + " stepped once");
        Check(!pass.Running, "pass over");

        // the time cap ends a slice after one pair even when the pair budget is large
        World t = new World();
        for (ulong b = 2; b < 50; b++) t.Add(1, b, 3);
        DailyPairPass<ulong, ulong> timed = new DailyPairPass<ulong, ulong>();
        timed.Begin(new List<ulong>(t.All.Keys), t.Partners, delegate(ulong k, ulong o) { return t.StepPair(k, o, 0); }, t.EndPass);
        timed.Slice(1000, 2000, delegate { return 5000; }, out stepped);
        Check(OwnedSteps(t) <= 1, "over the time cap after the first entry");
    }

    // Model of EA's pet pool alarm (one stored handle; the callback clears it, builds, re-arms only when it is clear) with
    // our hook (sync or in a task that starts a tick later and sleeps while building), a mid-period trigger, other code
    // arming the alarm while a build sleeps, and switch flips. Returns the alarm chains alive at the end.
    sealed class PoolModel
    {
        public Dictionary<int, int> Alarms = new Dictionary<int, int>(); // handle -> due minute
        public int Stored, NextId = 1, Dropped;
        public List<int[]> Builds = new List<int[]>(); // { clear minute, end minute }
        public bool Guard;

        public void StartAlarm(int now) { if (Stored == 0) { Stored = NextId++; Alarms[Stored] = now + 30; } }
        float LeftOfStored(int now) { int due; return Alarms.TryGetValue(Stored, out due) && due > now ? due - now : 0f; }
        public void Hook(int now, bool ours, int buildMinutes)
        {
            if (Guard && EaSpreadCore.ExtraPoolFire(LeftOfStored(now))) { Dropped++; return; }
            if (!ours) { Stored = 0; StartAlarm(now); return; }
            Builds.Add(new int[] { now + 1, now + 1 + buildMinutes }); // our task: callback body a tick later
        }
        public void Minute(int now)
        {
            foreach (int[] b in Builds) if (b[0] == now) Stored = 0;
            foreach (int[] b in Builds) if (b[1] == now) StartAlarm(now);
            Builds.RemoveAll(delegate(int[] b) { return b[1] <= now; });
        }
    }

    static int PoolChains(int seed, bool guardAndTriggerFix)
    {
        Random r = new Random(seed);
        PoolModel m = new PoolModel();
        m.Guard = guardAndTriggerFix;
        m.StartAlarm(0);
        bool ours = r.Next(2) == 0;
        for (int now = 0; now < 3000; now++)
        {
            m.Minute(now);
            foreach (KeyValuePair<int, int> a in new List<KeyValuePair<int, int>>(m.Alarms))
                if (a.Value == now) { m.Alarms.Remove(a.Key); m.Hook(now, ours, r.Next(0, 4)); }
            if (r.Next(200) == 0) ours = !ours;                         // switch flip
            if (r.Next(150) == 0) m.StartAlarm(now);                    // other code (e.g. a pet leaving a pool) arms the alarm
            if (r.Next(400) == 0)                                       // EaSpreadNow("pets")
            {
                if (guardAndTriggerFix) m.Alarms.Remove(m.Stored);
                m.Hook(now, ours, r.Next(0, 4));
            }
        }
        return m.Alarms.Count + m.Builds.Count;
    }

    static void PetAlarmStaysOneChain()
    {
        int buggy = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            Check(PoolChains(seed, true) == 1, "seed " + seed + ": one pet pool alarm chain with the guard and the trigger fix");
            if (PoolChains(seed, false) > 1) buggy++;
        }
        Check(buggy > 0, "the model reproduces the duplicate chain without the fix (" + buggy + " of 300 seeds)");
        Check(!EaSpreadCore.ExtraPoolFire(0f) && EaSpreadCore.ExtraPoolFire(0.5f), "extra fire only while the stored alarm is due later");
    }

    sealed class Obj { public int Id; public bool Dirty, Gone, Cleaned; public int Acts; }

    // Venue cleanup units: a lot with 1,000 objects (every third dirty) and two small lots, sliced at 64 units, with objects
    // destroyed while the big lot is mid-split. Found set = the one-shot predicate's; each found object still there is
    // cleaned once, in list order; destroyed ones are not touched; no slice runs more than its budget.
    static void GiantLotUnits()
    {
        List<List<Obj>> lots = new List<List<Obj>>();
        int[] sizes = { 1000, 5, 0 };
        for (int l = 0; l < sizes.Length; l++)
        {
            List<Obj> objs = new List<Obj>();
            for (int i = 0; i < sizes[l]; i++) { Obj o = new Obj(); o.Id = l * 10000 + i; o.Dirty = i % 3 == 0; objs.Add(o); }
            lots.Add(objs);
        }
        List<int> order = new List<int>();
        DailyPairPass<List<Obj>, SpreadUnit> pass = new DailyPairPass<List<Obj>, SpreadUnit>();
        pass.Begin(lots, delegate(List<Obj> lot)
        {
            List<SpreadUnit> u = new List<SpreadUnit>();
            EaSpreadCore.AddFindThenAct<Obj>(u, lot, delegate(Obj o) { return o.Dirty; },
                delegate(Obj o) { o.Acts++; o.Cleaned = true; order.Add(o.Id); }, delegate(Obj o) { return !o.Gone; });
            return u;
        }, delegate(List<Obj> lot, SpreadUnit unit) { unit(); return true; }, delegate { });
        int slices = 0, units;
        bool done = false;
        while (!done)
        {
            done = pass.Slice(EaSpreadCore.kMaxLotUnits, 2000, delegate { return 0; }, out units);
            Check(units <= EaSpreadCore.kMaxLotUnits, "slice within the unit budget");
            if (++slices == 20) foreach (Obj o in lots[0]) if (o.Id % 7 == 0 && o.Acts == 0) o.Gone = true; // destroyed mid-split (find phase done, act phase partway)
        }
        Check(slices >= 2000 / EaSpreadCore.kMaxLotUnits, "the big lot took >= 31 slices (" + slices + ")");
        foreach (List<Obj> lot in lots)
            foreach (Obj o in lot)
                Check(o.Acts == (o.Dirty && !o.Gone ? 1 : 0), "object " + o.Id + " cleaned " + o.Acts + " times");
        for (int i = 1; i < order.Count; i++) Check(order[i] > order[i - 1], "cleaned in list order");
    }

    static int Main()
    {
        GiantLotUnits();
        PetAlarmStaysOneChain();
        SlicedPassCoversEachItemOnce(new Random(1));
        DailyDecayMatchesReference();
        GiantSimChangesMidPass();
        Check(EaSpreadCore.OwnsPair(1, 2) && !EaSpreadCore.OwnsPair(2, 1) && !EaSpreadCore.OwnsPair(3, 3), "pair owner = lower id");
        Check(EaSpreadCore.SliceFull(EaSpreadCore.kMaxLotUnits, 0, EaSpreadCore.kMaxLotUnits, EaSpreadCore.kSliceUs) && EaSpreadCore.SliceFull(1, EaSpreadCore.kSliceUs, 99, EaSpreadCore.kSliceUs)
            && !EaSpreadCore.SliceFull(1, 0, 2, EaSpreadCore.kSliceUs), "slice full by count or time");
        Console.WriteLine(sFails == 0 ? "easpread selftest: PASS" : "easpread selftest: " + sFails + " FAIL");
        return sFails == 0 ? 0 : 1;
    }
}
