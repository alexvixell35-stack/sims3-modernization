using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Ts3Fixes;

// Offline self-check of DeadRefsCore (issue #91): random holders with random gone entries; after a drop no gone entry
// is left, every live entry is kept in order, counts match, and arrays come back null when nothing was gone.
static class DeadRefsTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        Random rnd = new Random(91);
        for (int round = 0; round < 3000; round++)
        {
            int n = rnd.Next(0, 40);
            HashSet<int> gone = new HashSet<int>();
            List<int> list = new List<int>();
            int[] arr = new int[n];
            Dictionary<int, string> dict = new Dictionary<int, string>();
            for (int i = 0; i < n; i++)
            {
                list.Add(i); arr[i] = i; dict[i] = "v" + i;
                if (rnd.Next(4) == 0) gone.Add(i);
            }
            Predicate<int> isGone = delegate(int v) { return gone.Contains(v); };

            int dl = DeadRefsCore.DropAll(list, isGone);
            Check(dl == gone.Count && list.Count == n - gone.Count, "list count " + round);
            for (int i = 1; i < list.Count; i++) Check(list[i - 1] < list[i], "list order kept " + round);
            foreach (int v in list) Check(!gone.Contains(v), "list has no gone entry " + round);

            int removed;
            int[] copy = DeadRefsCore.Without(arr, isGone, out removed);
            Check(removed == gone.Count, "array count " + round);
            if (gone.Count == 0) Check(copy == null, "nothing gone = null " + round);
            else
            {
                Check(copy != null && copy.Length == n - gone.Count, "array length " + round);
                foreach (int v in copy) Check(!gone.Contains(v), "array has no gone entry " + round);
                Check(arr.Length == n, "old array untouched " + round);
            }

            int dk = DeadRefsCore.DropKeys(dict, isGone);
            Check(dk == gone.Count && dict.Count == n - gone.Count, "dict count " + round);
            foreach (int k in dict.Keys) Check(!gone.Contains(k), "dict has no gone key " + round);
        }
        Check(DeadRefsCore.DropAll<int>(null, delegate { return true; }) == 0, "null list");
        int r0;
        Check(DeadRefsCore.Without<int>(null, delegate { return true; }, out r0) == null && r0 == 0, "null array");
        Check(DeadRefsCore.DropKeys<int, int>(null, delegate { return true; }) == 0, "null dict");
        Check(!DeadRefsCore.LooksLikeLoad(15, 15), "small holder never refused");
        Check(DeadRefsCore.LooksLikeLoad(60, 100), "most gone = load signature");
        Check(!DeadRefsCore.LooksLikeLoad(50, 100), "half gone is still acted on");
        Check(!DeadRefsCore.LooksLikeLoad(7, 103), "real leftovers acted on");
        // conversation-gone-check per-sample verdicts (expectLeft, on, gone, members, removedHere, hostLeft)
        Check(DeadRefsCore.ConvSampleVerdict(true, false, 1, 2, 0, false) == "pass", "left: gone member kept");
        Check(DeadRefsCore.ConvSampleVerdict(true, false, 1, 2, 0, true) == "pass", "left: kept, host threw out (#166)");
        Check(DeadRefsCore.ConvSampleVerdict(true, false, 0, 0, 0, true) == "na", "left: host left before the check = n/a");
        Check(DeadRefsCore.ConvSampleVerdict(true, false, 0, 1, 0, false) == "fail", "left: removed with the host still there");
        Check(DeadRefsCore.ConvSampleVerdict(true, true, 1, 2, 0, false) == "fail", "left: switches on is not the off arm");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 0, 1, 1, false) == "pass", "cleaned: host still listed");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 0, 0, 1, true) == "pass", "cleaned: phase removed it, host left afterwards");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 0, 0, 0, true) == "na", "cleaned: EA's exit ran before the phase = n/a");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 0, 0, 0, false) == "fail", "cleaned: emptied, host still in it, no phase");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 1, 2, 0, false) == "fail", "cleaned: dead member never removed fails");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 1, 1, 1, false) == "fail", "cleaned: gone member still listed after a removal");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 1, 2, 0, true) == "na", "cleaned: host left with the gone member = n/a");
        Check(DeadRefsCore.ConvSampleVerdict(false, false, 0, 1, 1, false) == "fail", "cleaned: switches off fails");
        Check(DeadRefsCore.ConvSampleVerdict(false, true, 0, 2, 1, false) == "fail", "cleaned: two live members is not this seed");
        Check(DeadRefsCore.ConvSeedCleanGate(true, null) == null, "seed clean: runs with the sweep on and a safe state");
        Check(DeadRefsCore.ConvSeedCleanGate(false, null) == "deadrefsweep off", "seed clean: never with the sweep off");
        Check(DeadRefsCore.ConvSeedCleanGate(true, "saving") == "saving", "seed clean: the pass's unsafe state refuses it too");
        Check(DeadRefsCore.ConvSeedCleanGate(false, "saving") == "deadrefsweep off", "seed clean: switch first");
        Check(DeadRefsCore.ConvArmVerdict(5, 0, 0) == "pass", "arm: all pass");
        Check(DeadRefsCore.ConvArmVerdict(3, 0, 2) == "pass", "arm: 3 conclusive + 2 n/a");
        Check(DeadRefsCore.ConvArmVerdict(2, 0, 3) == "inconclusive", "arm: 2 conclusive");
        Check(DeadRefsCore.ConvArmVerdict(4, 1, 0) == "fail", "arm: one fail fails the arm");
        DeltaIndexChecks();
        PinChecks();
        PerLotChecks();
        DescRevChecks();
        // SimFrozenCore (docs/205 §2.3b): the gate, and the clock-still memory
        Check(!SimFrozenCore.FrozenOrNotLive(true, false, false, false), "frozen: live, running = act");
        Check(SimFrozenCore.FrozenOrNotLive(false, false, false, false), "frozen: CAS / Buy / Build / Edit Town = wait");
        Check(SimFrozenCore.FrozenOrNotLive(true, true, false, false), "frozen: travelling / shutting down");
        Check(SimFrozenCore.FrozenOrNotLive(true, false, true, false), "frozen: paused");
        Check(SimFrozenCore.FrozenOrNotLive(true, false, false, true), "frozen: clock stopped (minimized)");
        long last = 0; int moved = 0;
        Check(!SimFrozenCore.ClockStill(500, 1000, ref last, ref moved) && last == 500 && moved == 1000, "clock: first read is a move");
        Check(!SimFrozenCore.ClockStill(500, 1050, ref last, ref moved), "clock: 50 ms between steps is not frozen");
        Check(!SimFrozenCore.ClockStill(500, 1099, ref last, ref moved), "clock: 99 ms not frozen");
        Check(SimFrozenCore.ClockStill(500, 1100, ref last, ref moved), "clock: 100 ms still = frozen");
        Check(SimFrozenCore.ClockStill(500, 61000, ref last, ref moved), "clock: still frozen a minute later");
        Check(!SimFrozenCore.ClockStill(533, 61010, ref last, ref moved) && moved == 61010, "clock: a move unfreezes at once");
        Check(!SimFrozenCore.ClockStill(533, 61010, ref last, ref moved), "clock: second caller in the same frame sees the recent move");
        last = 7; moved = int.MaxValue - 20;
        Check(!SimFrozenCore.ClockStill(7, unchecked(int.MaxValue + 50), ref last, ref moved), "clock: TickCount wrap, 70 ms not frozen");
        Check(SimFrozenCore.ClockStill(7, unchecked(int.MaxValue + 90), ref last, ref moved), "clock: TickCount wrap, 110 ms frozen");
        // SimFrozenCore.Hold (docs/205 §2.3c): the drains' wait
        Check(SimFrozenCore.Hold(true, false, false), "hold: paused / not live = wait, work kept");
        Check(!SimFrozenCore.Hold(false, false, false), "hold: live and running = act");
        Check(!SimFrozenCore.Hold(true, true, false), "hold: world going away = caller's exit, no endless wait");
        Check(!SimFrozenCore.Hold(true, false, true), "hold: save started (wildflower) = caller's exit");
        Check(!SimFrozenCore.Hold(false, true, true), "hold: running, leaving and saving = no wait");
        WriterScanCases();
        Console.WriteLine(sFails == 0 ? "deadrefs selftest: PASS" : "deadrefs selftest: FAIL " + sFails);
        Environment.Exit(sFails == 0 ? 0 : 1);
    }


    // docs/336 deadrefdelta + deadrefslimuse, the DeadRefsDelta.cs protocol on a model world. Holders: owner / thief Sim
    // (relinked to the description's live Sim when one exists, else dropped), relationship handlers per description (gone
    // when disposed or decayed), cooking handlers (go stale without a hook), use lists (gone Sim entries). Writers: hooked
    // (SetStealActors / SetRewardGiver / import, GetNewRelationship, AddObjectListeners, AddToUseList...), events (Sim
    // destroyed, description disposed), the decay hook, unhooked (a process letting go; bypass use and owner writers),
    // object destruction, load (reset; objects come back from the save with gone entries), switch toggles. After every pass
    // no live object may hold a gone entry (= what the full walk leaves) unless a bypass writer wrote since the last full
    // walk; verify finds exactly those. Leak: after each delta pass no index entry names a dead object, the size stays
    // under the cap, a load leaves it empty.
    class MObj { public ulong id, owner, thief; public List<ulong> rels = new List<ulong>(), decayed = new List<ulong>(); public bool cook, cookStale, dead; public List<ulong> use = new List<ulong>(); }
    static Dictionary<ulong, MObj> sObj;
    static Dictionary<ulong, ulong> sSimDesc;           // Sim id -> desc
    static Dictionary<ulong, bool> sSimDead, sDescDead;
    static Dictionary<ulong, ulong> sLiveSim;           // desc -> live Sim id (0 none)
    static DeltaIndex sX;
    static bool sDeltaOn, sSlimOn;
    static ulong sNextId;
    static int sMiss, sUseMiss;

    static bool MSimGone(ulong s) { return s != 0 && sSimDead[s]; }
    static bool MRelGone(MObj o, int i) { return sDescDead[o.rels[i]] || o.decayed.Contains(o.rels[i]); }
    static int MHolderGone(MObj o)
    {
        int n = (MSimGone(o.owner) ? 1 : 0) + (MSimGone(o.thief) ? 1 : 0) + (o.cook && o.cookStale ? 1 : 0);
        for (int i = 0; i < o.rels.Count; i++) if (MRelGone(o, i)) n++;
        return n;
    }
    static int MUseGone(MObj o) { int n = 0; foreach (ulong s in o.use) if (sSimDead[s]) n++; return n; }
    static ulong MLive(ulong sim) { ulong d = sSimDesc[sim]; ulong l; return !sDescDead[d] && sLiveSim.TryGetValue(d, out l) ? l : 0; }

    // OwnerBody / HandlerBody / CookingBody / UseListBody on the model
    static void MCleanHolders(MObj o)
    {
        bool og = MSimGone(o.owner), tg = MSimGone(o.thief);
        ulong lo = og ? MLive(o.owner) : 0, lt = tg ? MLive(o.thief) : 0;
        if (og && lo == 0 && o.thief == 0) o.owner = 0;
        else { if (og && lo != 0) o.owner = lo; if (tg && lt != 0) o.thief = lt; }
        for (int i = o.rels.Count - 1; i >= 0; i--) if (MRelGone(o, i)) { o.decayed.Remove(o.rels[i]); o.rels.RemoveAt(i); }
        if (o.cook && o.cookStale) o.cook = o.cookStale = false;
    }
    static void MCleanUse(MObj o) { o.use.RemoveAll(delegate(ulong s) { return sSimDead[s]; }); }

    // DrIdxNoteHolders
    static void MNoteOwner(ulong s, ulong id) { if (s == 0) return; if (sSimDead[s]) sX.NoteUnkeyed(id); else sX.NoteSim(s, id); }
    static void MNoteHolders(MObj o)
    {
        MNoteOwner(o.owner, o.id); MNoteOwner(o.thief, o.id);
        for (int i = 0; i < o.rels.Count; i++) { if (MRelGone(o, i)) sX.NoteUnkeyed(o.id); else sX.NoteDesc(o.rels[i], o.id); }
        if (o.cook) sX.NoteUnkeyed(o.id);
    }
    static bool MLiveIdx() { return sX.Seeded || sX.Seeding; }
    static void MHookHolder(MObj o) { if (!MLiveIdx()) return; if (!sDeltaOn) { sX.Reset(); return; } MNoteHolders(o); }
    static void MHookUse(MObj o) { if (!MLiveIdx() || !sX.SeedSlim) return; if (!sDeltaOn || !sSlimOn) { sX.Reset(); return; } sX.NoteUse(o.id); }

    static MObj MClone(MObj o) { MObj c = new MObj(); c.id = o.id; c.owner = o.owner; c.thief = o.thief; c.rels = new List<ulong>(o.rels); c.decayed = new List<ulong>(o.decayed); c.cook = o.cook; c.cookStale = o.cookStale; c.dead = o.dead; c.use = new List<ulong>(o.use); return c; }
    static string MList(List<ulong> l) { return string.Join(",", l.ConvertAll(delegate(ulong v) { return v.ToString(); }).ToArray()); }
    static string MState(MObj o) { return o.owner + "/" + o.thief + "/" + MList(o.rels) + "/" + o.cook + o.cookStale + "/" + MList(o.use); }
    // what the full walk would leave: every live object cleaned (EA's bodies leave some gone entries, e.g. a gone owner
    // with a thief and no live instance)
    static Dictionary<ulong, string> MFullWalkResult()
    {
        Dictionary<ulong, string> r = new Dictionary<ulong, string>();
        foreach (MObj o in sObj.Values) if (!o.dead) { MObj c = MClone(o); MCleanUse(c); MCleanHolders(c); r[o.id] = MState(c); }
        return r;
    }

    static int MLiveCount() { int n = 0; foreach (MObj o in sObj.Values) if (!o.dead) n++; return n; }

    // DeadRefSweepRun phase 10 with DrDeltaBegin / DrDeltaItems / DrDeltaStep / DrDeltaEnd; true when it was a delta pass
    static bool MPass(bool byEvent, bool verify)
    {
        if (!sDeltaOn) { if (MLiveIdx()) sX.Reset(); foreach (MObj o in sObj.Values) if (!o.dead) { MCleanUse(o); MCleanHolders(o); } return false; }
        bool delta = DeadRefsCore.UseIndex(true, sX.Seeded && sX.SeedSlim == sSlimOn, byEvent);
        if (!delta)
        {
            sX.BeginSeed(sSlimOn);
            foreach (MObj o in sObj.Values) if (!o.dead) { MCleanUse(o); MCleanHolders(o); MNoteHolders(o); if (sSlimOn && o.use.Count > 0) sX.NoteUse(o.id); }
            sX.EndSeed();
            return false;
        }
        Predicate<ulong> dead = delegate(ulong id) { MObj o; return !sObj.TryGetValue(id, out o) || o.dead; };
        sX.Prune(dead, false);
        Check(sX.Prune(dead, true) == 0, "leak: no entry names a dead object after the prune");
        int k, u;
        Dictionary<ulong, bool> holders = sX.TakeHolders(out k, out u);
        Dictionary<ulong, bool> use = sSlimOn ? sX.TakeUse() : null;
        List<MObj> items = new List<MObj>();
        if (verify || !sSlimOn) { foreach (MObj o in sObj.Values) items.Add(o); }
        else
        {
            Dictionary<ulong, bool> ids = new Dictionary<ulong, bool>(use);
            foreach (ulong id in holders.Keys) ids[id] = true;
            foreach (ulong id in ids.Keys) { MObj o; if (sObj.TryGetValue(id, out o)) items.Add(o); }
        }
        foreach (MObj o in items)
        {
            if (o.dead) continue;
            bool hold = holders.ContainsKey(o.id), useIt = !sSlimOn || use.ContainsKey(o.id);
            if (verify)
            {
                if (!useIt && MUseGone(o) > 0) { sUseMiss++; useIt = true; }
                if (!hold && MHolderGone(o) > 0) { sMiss++; hold = true; }
            }
            if (useIt) MCleanUse(o);
            if (hold) { MCleanHolders(o); MNoteHolders(o); }
            if (sSlimOn && useIt && o.use.Count > 0) sX.NoteUse(o.id);
        }
        sX.OverCap(MLiveCount());
        return true;
    }

    static ulong MNewSim(ulong desc) { ulong s = ++sNextId; sSimDesc[s] = desc; sSimDead[s] = false; sLiveSim[desc] = s; return s; }
    static void MNewWorld(Random rnd)
    {
        sObj = new Dictionary<ulong, MObj>(); sSimDesc = new Dictionary<ulong, ulong>(); sSimDead = new Dictionary<ulong, bool>();
        sDescDead = new Dictionary<ulong, bool>(); sLiveSim = new Dictionary<ulong, ulong>(); sX = new DeltaIndex(); sNextId = 0;
        for (int d = 0; d < 12; d++) { ulong desc = ++sNextId; sDescDead[desc] = false; MNewSim(desc); }
        for (int i = 0; i < 40; i++) { MObj o = new MObj(); o.id = ++sNextId; sObj[o.id] = o; }
    }
    static ulong MAnySim(Random rnd) { List<ulong> l = new List<ulong>(sSimDead.Keys); return l[rnd.Next(l.Count)]; }
    static ulong MAnyDesc(Random rnd) { List<ulong> l = new List<ulong>(sDescDead.Keys); return l[rnd.Next(l.Count)]; }
    static MObj MAnyObj(Random rnd) { List<MObj> l = new List<MObj>(sObj.Values); return l[rnd.Next(l.Count)]; }

    // one random world op; returns true when it was an unhooked (bypass) write that may leave a gone entry outside the index
    static bool MOp(Random rnd, bool bypassAllowed, ref int destroyedSims)
    {
        MObj o = MAnyObj(rnd);
        switch (rnd.Next(16))
        {
            case 0: o.owner = MAnySim(rnd); o.thief = rnd.Next(3) == 0 ? MAnySim(rnd) : 0; MHookHolder(o); return false; // SetStealActors / SetRewardGiver / relink by a writer
            case 1: { MObj n = new MObj(); n.id = ++sNextId; n.owner = MAnySim(rnd); sObj[n.id] = n; MHookHolder(n); return false; } // import
            case 2: { ulong d = MAnyDesc(rnd); if (!o.rels.Contains(d)) o.rels.Add(d); MHookHolder(o); return false; } // GetNewRelationship
            case 3: if (o.rels.Count > 0) { ulong d = o.rels[rnd.Next(o.rels.Count)]; if (!o.decayed.Contains(d)) o.decayed.Add(d); if (MLiveIdx()) sX.MarkDesc(d); } return false; // decay hook
            case 4: o.cook = true; o.cookStale = false; MHookHolder(o); return false; // AddObjectListeners
            case 5: if (o.cook) o.cookStale = true; return false; // a process lets go, no hook: unkeyed covers it
            case 6: o.use.Add(MAnySim(rnd)); MHookUse(o); return false; // AddToUseList and friends
            case 7: case 8:
                {
                    ulong s = MAnySim(rnd); if (sSimDead[s]) return false;
                    sSimDead[s] = true; destroyedSims++;
                    ulong d = sSimDesc[s]; if (sLiveSim.ContainsKey(d) && sLiveSim[d] == s) sLiveSim.Remove(d);
                    if (rnd.Next(2) == 0 && !sDescDead[d]) MNewSim(d); // a new instance of the same description (relink case)
                    if (MLiveIdx()) sX.MarkSim(s); return false; // kSimDestroyed
                }
            case 9: { ulong d = MAnyDesc(rnd); if (!sDescDead[d]) { sDescDead[d] = true; sLiveSim.Remove(d); if (MLiveIdx()) sX.MarkDesc(d); } return false; } // disposed
            case 10: o.dead = true; return false; // object destroyed
            case 11: { MObj n = new MObj(); n.id = ++sNextId; sObj[n.id] = n; return false; }
            case 12: if (rnd.Next(8) == 0) sSlimOn = !sSlimOn; return false;
            case 13: if (rnd.Next(10) == 0) sDeltaOn = !sDeltaOn; return false;
            case 14:
                if (!bypassAllowed || rnd.Next(2) == 0) return false;
                if (rnd.Next(2) == 0) { o.use.Add(MAnySim(rnd)); return sSlimOn; } // one of EA's direct ActorsUsingMe.Add sites
                o.owner = MAnySim(rnd); return true; // another mod's AddComponent
            default: return false;
        }
    }

    // docs/333 LC-7 (deadrefdescrev): holder map + index; a disposed holder leaves at once; 50 cycles end at 0
    sealed class Desc { public ulong Id; public bool Gone; public ulong Ref; }
    static void DescRevChecks()
    {
        DescRevIndex<Desc> x = new DescRevIndex<Desc>();
        Desc a = new Desc { Id = 1, Ref = 2 }, b = new Desc { Id = 2, Ref = 1 };
        x.Hold(1, a); Check(x.Holders == 0, "descrev: nothing held while unseeded");
        x.BeginSeed(); x.Hold(1, a); x.Idx.NoteDesc(2, 1); x.Hold(2, b); x.Idx.NoteDesc(1, 2); x.Idx.EndSeed();
        Check(x.Holders == 2 && x.Idx.Entries == 2 && x.Get(1) == a, "descrev: seeded");
        b.Gone = true; x.Drop(2); x.Idx.MarkDesc(2); // b disposed: its event drops it and marks its key
        int fk, fu; Dictionary<ulong, bool> t = x.Idx.TakeHolders(out fk, out fu);
        Check(t.Count == 1 && t.ContainsKey(1) && fk == 1, "descrev: the holder of the disposed one is taken");
        Check(x.Prune(delegate(Desc d) { return d.Gone; }) == 1 && x.Idx.Entries == 0 && x.Holders == 1, "descrev: prune drops entries of a dropped holder");
        for (int i = 0; i < kPendingOverflowProbe; i++) x.Idx.MarkDesc((ulong)(1000 + i));
        Check(!x.Live && x.Holders == 0, "descrev: an index overflow clears the holder map");
        x.BeginSeed(); x.Idx.EndSeed();
        List<Desc> all = new List<Desc>();
        Random rng = new Random(7);
        for (int cycle = 0; cycle < 50; cycle++)
        {
            List<Desc> mine = new List<Desc>();
            for (int k = 0; k < 20; k++)
            {
                Desc d = new Desc { Id = (ulong)(10000 + cycle * 100 + k), Ref = (ulong)rng.Next(1, 40) };
                mine.Add(d); x.Hold(d.Id, d); x.Idx.NoteDesc(d.Ref, d.Id);
            }
            x.Idx.MarkDesc((ulong)rng.Next(1, 40));
            foreach (ulong id in x.Idx.TakeHolders(out fk, out fu).Keys) { Desc d = x.Get(id); if (d != null && !d.Gone) x.Idx.NoteDesc(d.Ref, d.Id); } // re-note
            foreach (Desc d in mine) { d.Gone = true; x.Drop(d.Id); } // every holder of this cycle disposed
            x.Prune(delegate(Desc d) { return d.Gone; });
            if (x.OverCap(5)) Check(false, "descrev: cap hit in the cycle test");
        }
        Check(x.Holders == 0 && x.Idx.Entries == 0 && x.Live, "descrev: 50 cycles end at 0 holders and 0 entries");
        x.Reset(); Check(!x.Live && x.Holders == 0, "descrev: load reset");
    }
    const int kPendingOverflowProbe = DeltaIndex.kPendingMax + 2;

    static void DeltaIndexChecks()
    {
        Check(!DeadRefsCore.UseIndex(false, true, true), "index: switch off walks");
        Check(!DeadRefsCore.UseIndex(true, false, true), "index: unseeded walks");
        Check(!DeadRefsCore.UseIndex(true, true, false), "index: 24 h floor walks");
        Check(DeadRefsCore.UseIndex(true, true, true), "index: event pass on a seeded index");

        // unit cases: add, relink, import, save-load seed, a Sim destroyed with 0 / many holders
        DeltaIndex x = new DeltaIndex();
        x.MarkSim(1);
        Check(x.PendingCount == 0, "map: marks ignored while unseeded");
        x.BeginSeed(true); x.NoteSim(1, 100); x.NoteSim(1, 101); x.NoteSim(1, 101); x.NoteDesc(7, 100); x.NoteUnkeyed(102); x.NoteUse(103); x.EndSeed();
        Check(x.Seeded && x.SimEntries == 2 && x.DescEntries == 1 && x.Entries == 5, "map: add, duplicates once");
        x.MarkSim(9); // a Sim with 0 holders
        int k, u; Dictionary<ulong, bool> h = x.TakeHolders(out k, out u);
        Check(k == 0 && u == 1 && h.Count == 1 && h.ContainsKey(102), "map: Sim destroyed with 0 holders steps only the unkeyed set");
        Check(x.UnkeyedCount == 0, "map: unkeyed set consumed");
        x.MarkSim(1); h = x.TakeHolders(out k, out u);
        Check(k == 2 && h.ContainsKey(100) && h.ContainsKey(101) && x.SimEntries == 0, "map: Sim destroyed with many holders steps them and drops its key");
        x.NoteSim(2, 100); // relink: the step notes the live instance's key
        x.MarkSim(2); h = x.TakeHolders(out k, out u);
        Check(k == 1 && h.ContainsKey(100), "map: relinked owner found under the new Sim");
        x.MarkDesc(7); h = x.TakeHolders(out k, out u);
        Check(k == 1 && h.ContainsKey(100) && x.DescEntries == 0, "map: description disposed / decayed steps its handlers");
        x.NoteSim(3, 200); x.NoteUse(201);
        Check(x.Prune(delegate(ulong id) { return id == 200 || id == 201; }, false) == 2 && x.SimEntries == 0 && x.UseCount == 1 && x.Prune(delegate(ulong id) { return true; }, true) == 1, "map: prune drops dead objects in every set");
        x.Reset(); // load: import and save-load start from an empty, unseeded index
        Check(x.Entries == 0 && x.PendingCount == 0 && !x.Seeded, "map: load resets to empty");
        x.BeginSeed(false); x.MarkSim(5); x.EndSeed();
        Check(x.PendingCount == 1, "map: a Sim destroyed during the seed walk stays marked");
        for (ulong i = 0; i < DeltaIndex.kPendingMax + 2; i++) x.MarkSim(1000 + i);
        Check(!x.Seeded && x.Entries == 0 && x.Overflows == 1, "map: too many marks reset (walk next pass)");
        x.BeginSeed(false); for (ulong i = 0; i < 2000; i++) x.NoteSim(i, i); x.EndSeed();
        Check(x.OverCap(100) && x.Entries == 0 && !x.Seeded, "map: entries over 2 x live + 1024 reset");

        // model world
        Random rnd = new Random(336);
        int deltaPasses = 0, slimPasses = 0, bypassRounds = 0, maxEntries = 0, destroyed = 0;
        for (int round = 0; round < 300; round++)
        {
            bool bypassAllowed = round % 4 == 3;
            MNewWorld(rnd); sDeltaOn = true; sSlimOn = rnd.Next(2) == 0;
            // save-load: gone entries come back from the save without a hook
            foreach (MObj o in sObj.Values) if (rnd.Next(4) == 0) { o.owner = MAnySim(rnd); o.use.Add(MAnySim(rnd)); }
            ulong ds = MAnySim(rnd); sSimDead[ds] = true;
            bool bypassSinceWalk = false;
            for (int step = 0; step < 150; step++)
            {
                if (rnd.Next(5) != 0) { if (MOp(rnd, bypassAllowed, ref destroyed)) bypassSinceWalk = true; continue; }
                if (rnd.Next(15) == 0) { sX.Reset(); continue; } // world reload (StartDeadRefSweep)
                bool byEvent = rnd.Next(6) != 0, verify = rnd.Next(2) == 0;
                int m0 = sMiss, um0 = sUseMiss;
                Dictionary<ulong, string> expect = MFullWalkResult();
                bool wasDelta = MPass(byEvent, verify);
                if (!wasDelta) bypassSinceWalk = false;
                if (wasDelta) { deltaPasses++; if (sSlimOn && !verify) slimPasses++; }
                if (!bypassSinceWalk || verify)
                    foreach (MObj o in sObj.Values) if (!o.dead) Check(MState(o) == expect[o.id], "delta pass leaves what the full walk leaves, round " + round);
                if (!bypassAllowed) Check(sMiss == m0 && sUseMiss == um0, "verify: no miss without a bypass writer, round " + round);
                if (wasDelta) Check(sX.Entries <= 2 * MLiveCount() + 1024, "leak: size under the cap");
                if (sX.Entries > maxEntries) maxEntries = sX.Entries;
            }
            if (bypassSinceWalk) bypassRounds++;
            sX.Reset(); // quit / load
            Check(sX.Entries == 0 && sX.PendingCount == 0, "leak: quit / load leaves the index empty");
        }
        Check(deltaPasses > 2000 && slimPasses > 300, "index: delta passes exercised (" + deltaPasses + ", slim " + slimPasses + ")");
        Check(bypassRounds > 5, "index: bypass rounds exercised (" + bypassRounds + ")");

        // churn: destroy-heavy, 20k ops on one world, passes every 10 ops; the index must stay bounded by live objects
        MNewWorld(rnd); sDeltaOn = true; sSlimOn = true; MPass(true, false);
        int churnMax = 0;
        for (int i = 0; i < 20000; i++)
        {
            int dummy = 0;
            if (i % 3 == 0) { MObj n = new MObj(); n.id = ++sNextId; n.owner = MAnySim(rnd); n.use.Add(MAnySim(rnd)); sObj[n.id] = n; MHookHolder(n); MHookUse(n); }
            else if (i % 3 == 1) MAnyObj(rnd).dead = true;
            else MOp(rnd, false, ref dummy);
            sDeltaOn = true;
            if (i % 10 == 9)
            {
                MPass(true, false);
                if (sX.Entries > churnMax) churnMax = sX.Entries;
                Check(sX.Prune(delegate(ulong id) { MObj o; return !sObj.TryGetValue(id, out o) || o.dead; }, true) == 0, "churn: no stale entry after a pass");
            }
        }
        Check(churnMax <= 4 * MLiveCount() + 64, "churn: index bounded by live objects (max " + churnMax + ", live " + MLiveCount() + ")");
        for (int i = 0; i < 50; i++) { sX.Reset(); MPass(true, false); MPass(true, false); }
        sX.Reset();
        Check(sX.Entries == 0, "churn: repeated load / quit leaves 0 entries");
    }

    // docs/336 §9 part 2: pins for the 17 direct use-list writers. Model: writer tasks open a pin (their target), may create
    // objects (Init attaches them), add a Sim to the target or a created object with no hook, yield any number of times (a
    // delta pass may run between any two steps), then close; some tasks die without closing; Sims and objects die; hooked
    // adds note as before; loads reset. After every pass no live object may list a dead Sim (= what the full walk leaves):
    // the slim walk is exact with no yield analysis. Leak: pins drain to 0, loads empty them, sizes stay bounded.
    class PW { public ulong task, target, made; public int step; public bool make, nested; }
    static void PinChecks()
    {
        Random rnd = new Random(594);
        Dictionary<ulong, List<ulong>> obj = new Dictionary<ulong, List<ulong>>();
        Dictionary<ulong, bool> simDead = new Dictionary<ulong, bool>(), taskDead = new Dictionary<ulong, bool>();
        List<PW> writers = new List<PW>();
        DeltaIndex x = new DeltaIndex();
        UsePins pins = new UsePins();
        bool useAllNext = false;
        ulong next = 1;
        int passes = 0, misses = 0, maxPins = 0;
        Predicate<ulong> deadTask = delegate(ulong t) { return taskDead.ContainsKey(t); };
        for (int i = 0; i < 40; i++) obj[next++] = new List<ulong>();
        for (int i = 0; i < 30; i++) simDead[next++] = false;
        x.BeginSeed(true); x.EndSeed();
        for (int op = 0; op < 60000; op++)
        {
            int r = rnd.Next(100);
            List<ulong> live = new List<ulong>(obj.Keys), sims = new List<ulong>();
            foreach (KeyValuePair<ulong, bool> kv in simDead) if (!kv.Value) sims.Add(kv.Key);
            if (sims.Count < 5) { simDead[next++] = false; continue; }
            if (live.Count < 10) { obj[next++] = new List<ulong>(); continue; }
            ulong sim = sims[rnd.Next(sims.Count)];
            if (r < 12)
            {
                PW w = new PW(); w.task = next++; w.target = live[rnd.Next(live.Count)]; w.make = rnd.Next(3) == 0;
                pins.Open(w.task, new ulong[] { w.target, 0 }); writers.Add(w);
                if (rnd.Next(4) == 0) { pins.Open(w.task, null); w.nested = true; } // a hooked writer inside another, same task
            }
            else if (r < 40 && writers.Count > 0)
            {
                PW w = writers[rnd.Next(writers.Count)];
                if (w.step == 0)
                {
                    if (w.make) { ulong n = next++; obj[n] = new List<ulong>(); pins.Attach(w.task, n); w.made = n; }
                    w.step = 1;
                }
                else if (w.step == 1)
                {
                    ulong into = w.made != 0 && obj.ContainsKey(w.made) ? w.made : w.target;
                    if (obj.ContainsKey(into)) obj[into].Add(sim); // the direct write: no hook
                    w.step = 2;
                }
                else
                {
                    bool all;
                    List<ulong> ids = pins.Close(w.task, out all);
                    if (w.nested) { Check(ids == null, "pins: an inner close keeps the pin"); ids = pins.Close(w.task, out all); }
                    Check(ids != null, "pins: the outer close hands back the ids");
                    if (all) useAllNext = true;
                    if (ids != null) foreach (ulong id in ids) x.NoteUse(id);
                    writers.Remove(w);
                }
            }
            else if (r < 44 && writers.Count > 0) { PW w = writers[rnd.Next(writers.Count)]; taskDead[w.task] = true; writers.Remove(w); }
            else if (r < 52) simDead[sim] = true;
            else if (r < 56) obj.Remove(live[rnd.Next(live.Count)]);
            else if (r < 62) { ulong o = live[rnd.Next(live.Count)]; obj[o].Add(sim); x.NoteUse(o); } // AddToUseList (hooked)
            else if (r < 66) { ulong n = next++; obj[n] = new List<ulong>(); pins.Attach(next++, n); } // created outside any writer
            else if (r < 67)
            {   // a writer that creates more than kPinIds objects: all mode
                ulong t = next++; pins.Open(t, null);
                ulong last = 0;
                for (int k = 0; k <= UsePins.kPinIds; k++) { last = next++; obj[last] = new List<ulong>(); pins.Attach(t, last); }
                Check(pins.AnyAll, "pins: over the id cap turns to all");
                obj[last].Add(sim);
                PW w = new PW(); w.task = t; w.step = 2; writers.Add(w);
            }
            else if (r < 68)
            {   // load: everything resets, objects come back from the save; the first pass is a full walk (seed)
                pins.Reset(); x.Reset(); writers.Clear(); useAllNext = false;
                foreach (List<ulong> l in obj.Values) l.RemoveAll(delegate(ulong s) { return simDead[s]; });
                x.BeginSeed(true); foreach (KeyValuePair<ulong, List<ulong>> kv in obj) if (kv.Value.Count > 0) x.NoteUse(kv.Key); x.EndSeed();
            }
            else if (r < 80)
            {   // a delta pass (DrDeltaBegin slim + DrPinsIntoPass + the slim walk)
                passes++;
                bool allOut;
                foreach (ulong id in pins.DropDead(deadTask, out allOut)) x.NoteUse(id);
                if (allOut) useAllNext = true;
                x.Prune(delegate(ulong id) { return !obj.ContainsKey(id); }, false);
                Dictionary<ulong, bool> taken = x.TakeUse();
                pins.CopyIds(taken);
                bool all = useAllNext || pins.AnyAll; useAllNext = false;
                foreach (ulong id in all ? new List<ulong>(obj.Keys) : new List<ulong>(taken.Keys))
                {
                    List<ulong> l;
                    if (!obj.TryGetValue(id, out l)) continue;
                    l.RemoveAll(delegate(ulong s) { return simDead[s]; });
                    if (l.Count > 0) x.NoteUse(id);
                }
                foreach (KeyValuePair<ulong, List<ulong>> kv in obj) foreach (ulong s in kv.Value) if (simDead[s]) misses++;
                if (pins.Count > maxPins) maxPins = pins.Count;
                Check(x.UseCount <= obj.Count, "pins: use set bounded by live objects");
            }
        }
        Check(passes > 3000, "pins: enough passes ran");
        Check(misses == 0, "pins: no live object lists a dead Sim after a pass (use misses " + misses + ")");
        Check(pins.Opens > 1000 && pins.Attached > 100 && pins.DeadDropped > 50 && pins.Overflows > 0, "pins: every path exercised");
        foreach (PW w in writers) taskDead[w.task] = true;
        bool ao; pins.DropDead(deadTask, out ao);
        Check(pins.Count == 0 && !pins.AnyAll, "pins: dead tasks drain every pin");
        for (int i = 0; i < 50; i++) { pins.Open(next++, new ulong[] { 1, 2 }); pins.Reset(); }
        Check(pins.Count == 0 && pins.IdCount == 0, "pins: 50 load / quit cycles leave nothing");
        Check(maxPins < 200, "pins: open pins bounded by running writers (" + maxPins + ")");
    }

    // docs/336 §9 part 1: per-lot lists against the snapshot under churn: creation (Init hook), destruction (dropped by the
    // hook once the world no longer resolves the id, unhooked overrides, destroyed objects the world still lists), lot moves
    // (hook), reseeds, loads. Every gather equals the snapshot's typed set with the right kinds; lot keys follow moves; the
    // index never holds more than the indexed live objects; 50 load / quit cycles leave it empty.
    class PO { public byte kinds; public ulong lot; }
    static void PerLotChecks()
    {
        Random rnd = new Random(1715);
        Dictionary<ulong, PO> w = new Dictionary<ulong, PO>();
        PerLotIndex ix = new PerLotIndex();
        ulong next = 1;
        int gathers = 0, diff = 0, wrongLot = 0;
        byte[] kinds = { 0, 0, 0, PerLotIndex.kSim, PerLotIndex.kFish, PerLotIndex.kVehicle, PerLotIndex.kHorseshoe, PerLotIndex.kDartboard };
        for (int op = 0; op < 80000; op++)
        {
            int r = rnd.Next(100);
            List<ulong> ids = new List<ulong>(w.Keys);
            if (r < 25 || ids.Count < 20)
            {   // GameObject.Init
                PO o = new PO(); o.kinds = kinds[rnd.Next(kinds.Length)]; o.lot = (ulong)rnd.Next(12);
                ulong id = next++; w[id] = o;
                if (ix.Seeded) ix.Note(id, o.kinds, o.lot);
            }
            else if (r < 40)
            {   // Destroy: a quarter stay listed by the world (kept in both); the rest leave it, the hook drops 2 of 3 (the
                // others are overrides without base: pruned at the next gather)
                ulong id = ids[rnd.Next(ids.Count)];
                if (rnd.Next(4) == 0) continue;
                if (rnd.Next(3) != 0 && ix.Seeded) ix.Drop(id);
                w.Remove(id);
            }
            else if (r < 60)
            {   // InternalOnSetLotCurrent
                ulong id = ids[rnd.Next(ids.Count)];
                w[id].lot = (ulong)rnd.Next(12);
                if (ix.Seeded) ix.Move(id, w[id].lot);
            }
            else if (r < 62)
            {   // a snapshot pass: reseed
                ix.Reset();
                foreach (KeyValuePair<ulong, PO> kv in w) ix.Note(kv.Key, kv.Value.kinds, kv.Value.lot);
                ix.Seeded = true;
            }
            else if (r < 63) ix.Reset(); // load / switch off
            else if (ix.Seeded)
            {   // gather vs snapshot
                gathers++;
                Dictionary<ulong, byte> got = new Dictionary<ulong, byte>();
                ix.Walk(null, delegate(ulong id, byte k) { if (!w.ContainsKey(id)) return false; got[id] = k; return true; });
                int typed = 0;
                foreach (KeyValuePair<ulong, PO> kv in w)
                {
                    if (kv.Value.kinds == 0) continue;
                    typed++;
                    byte k;
                    if (!got.TryGetValue(kv.Key, out k) || k != kv.Value.kinds) diff++;
                    if (ix.LotOf(kv.Key) != kv.Value.lot) wrongLot++;
                }
                if (got.Count != typed) diff++;
                Check(ix.Count <= typed, "perlot: index bounded by indexed live objects after a gather");
                Check(!ix.OverCap(w.Count), "perlot: never over the cap");
            }
        }
        Check(gathers > 10000, "perlot: enough gathers (" + gathers + ")");
        Check(diff == 0, "perlot: per-lot gather equals the snapshot's typed set (" + diff + " differences)");
        Check(wrongLot == 0, "perlot: lot keys follow the moves (" + wrongLot + ")");
        ix.Reset(); ix.Note(1, PerLotIndex.kSim, 5); ix.Note(2, PerLotIndex.kSim, 6); ix.Seeded = true;
        int seen = 0; ix.Walk(delegate(ulong lot) { return lot == 5; }, delegate(ulong id, byte k) { seen++; return true; });
        Check(seen == 1, "perlot: lotLive filters lots (LC-6 hook point)");
        Check(ix.Move(1, 6) && !ix.Move(1, 6) && ix.Lots == 1, "perlot: a move empties the old lot bucket");
        Check(ix.Drop(1) && ix.Drop(2) && ix.Count == 0 && ix.Lots == 0, "perlot: drops leave nothing");
        ix.Note(3, PerLotIndex.kFish, 1); Check(ix.OverCap(-1100) && ix.Count == 0 && ix.Overflows == 1, "perlot: over the cap resets");
        for (int i = 0; i < 50; i++) { for (int k = 0; k < 100; k++) ix.Note(next++, PerLotIndex.kSim, (ulong)k); ix.Seeded = true; ix.Reset(); }
        Check(ix.Count == 0 && ix.Lots == 0 && !ix.Seeded, "perlot: 50 load / quit cycles leave 0 entries");
    }

    // docs/336 §11: DescWriterScan on real csc IL (WsSubjects) and on synthetic bodies
    static int Scan(string method, Dictionary<string, int> f, Dictionary<string, bool> l)
    {
        return Scan(typeof(WsSubjects), method, f, l);
    }

    static int Scan(Type t, string method, Dictionary<string, int> f, Dictionary<string, bool> l)
    {
        MethodInfo m = t.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        bool bad;
        int k = DescWriterScan.ScanMethod(m, DescWriterScan.ReflectionBody, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(!bad, "writerscan: " + method + " readable");
        return k;
    }

    static void WriterScanCases()
    {
        Dictionary<string, int> f = new Dictionary<string, int>();
        f["WsJob::Coworkers"] = DescWriterScan.KCoworkers; f["WsJob::FormerBoss"] = DescWriterScan.KFormerBoss;
        Dictionary<string, bool> l = new Dictionary<string, bool>();
        l["WsJob::Coworkers"] = true;
        int KC = DescWriterScan.KCoworkers, KB = DescWriterScan.KFormerBoss;
        Check(Scan("Stfld", f, l) == KB, "writerscan: direct stfld is a FormerBoss write");
        Check(Scan("StNull", f, l) == 0, "writerscan: `= null` is a removal, not a write");
        // LC-7f: the finding names its IL offset, opcode, field and the member it writes through (ts3native.log line)
        Check(Scan("Stfld", f, l) == KB && DescWriterScan.Hit != null && DescWriterScan.Hit.StartsWith("IL_") && DescWriterScan.Hit.Contains(" stfld WsJob::FormerBoss <- "), "writerscan: hit names the store (" + DescWriterScan.Hit + ")");
        Check(Scan("ListAdd", f, l) == KC && DescWriterScan.Hit != null && DescWriterScan.Hit.Contains(" ldfld WsJob::Coworkers -> call ") && DescWriterScan.Hit.Contains("Add"), "writerscan: hit names the list and its consumer (" + DescWriterScan.Hit + ")");
        Check(Scan("Both", f, l) == (KC | KB) && DescWriterScan.Hit != null && DescWriterScan.Hit.Split(';').Length == 2, "writerscan: two kinds, two hits (" + DescWriterScan.Hit + ")");
        Check(Scan("StNull", f, l) == 0 && DescWriterScan.Hit == null, "writerscan: no write, no hit");
        Check(Scan("StCond", f, l) == KB, "writerscan: `= c ? null : x` (store is a branch target) is a write");
        Check(Scan("StOther", f, l) == 0, "writerscan: another field is not a target");
        Check(Scan("Addr", f, l) == KB, "writerscan: ref to the field (ldflda) is a write");
        Check(Scan("ListAdd", f, l) == KC, "writerscan: ldfld + Add is a Coworkers write");
        Check(Scan("ListInsert", f, l) == KC, "writerscan: ldfld + Insert is a Coworkers write");
        Check(Scan("ListRead", f, l) == 0, "writerscan: null test, Contains, Count, foreach, copy are reads");
        Check(Scan("ListLocal", f, l) == KC, "writerscan: list kept in a local counts as a write");
        Check(Scan("ListPass", f, l) == 0, "writerscan: list passed to a helper that only reads");
        Check(Scan("ListPassAdd", f, l) == KC, "writerscan: list passed to a helper that adds = write");
        Check(Scan("ListPassRemove", f, l) == 0, "writerscan: list passed to a helper that only removes");
        Check(Scan("ListRemove", f, l) == 0, "writerscan: Remove / RemoveAt / Clear are removals");
        Check(Scan("ClearViaHelper", f, l) == 0, "writerscan: ref passed to `o = default(S)`");
        Check(Scan("ClearViaHelper2", f, l) == 0, "writerscan: ref passed to a read + `o = null`");
        Check(Scan("SetViaHelper", f, l) == KB, "writerscan: ref passed to a helper that stores a value = write");
        Check(Scan("ListReplace", f, l) == 0, "writerscan: a fresh empty list stored holds nothing (LC-7c)");
        Dictionary<string, int> fj = new Dictionary<string, int>(f);
        fj["WsMgr::mJob"] = KC | KB; // LC-7d: a job object stored brings its references
        Check(Scan("JobSwap", fj, l) == (KC | KB), "writerscan: storing a held job object = coworkers + formerboss write");
        Check(Scan("JobClear", fj, l) == 0, "writerscan: mJob = null = removal");
        Check(Scan("ListReplaceCopy", f, l) == KC, "writerscan: a list copied from another stored is a write");
        Check(Scan("ListAlias", f, l) == KC, "writerscan: an empty list kept in a local and filled after the store is a write");
        Check(Scan("ListRemoveProp", f, l) == 0, "writerscan: Remove(j.FormerBoss): ldfld between the list and the read (LC-7c)");
        Check(Scan("ListRemoveCall", f, l) == 0, "writerscan: Remove(Pick(j)): a call on later pushes only (LC-7c)");
        Check(Scan("ListPassAddCall", f, l) == KC, "writerscan: list passed with a call argument to a helper that adds = write");
        Check(Scan("ListSource", f, l) == 0, "writerscan: list as the source of another list's AddRange is a read (LC-7c)");
        Check(Scan("ListAddRangeInto", f, l) == KC, "writerscan: AddRange into the list is a write");
        // reflection guard (LC-7c)
        Check(Scan(typeof(RfSubjects), "ReflectHit", f, l) == KB && DescWriterScan.Reflect, "writerscan: GetField(\"FormerBoss\").SetValue = FormerBoss writer");
        Check(Scan(typeof(RfSubjects), "ReflectMiss", f, l) == 0 && !DescWriterScan.Reflect, "writerscan: GetField of another field = no writer");
        Check(Scan(typeof(RfSubjects), "NameOnly", f, l) == 0, "writerscan: the name as a string without reflection = no writer");
        Check(Scan(typeof(RfSubjects), "Emit", f, l) == DescWriterScan.KAll && DescWriterScan.Reflect, "writerscan: Reflection.Emit = every kind");
        Check(Scan(typeof(RfSubjects), "Invoke", f, l) == DescWriterScan.KAll, "writerscan: Type.InvokeMember = every kind");
        Check(Scan("Both", f, l) == (KC | KB), "writerscan: both kinds in one method");
        Check(Scan("GenericWrite", f, l) == KB, "writerscan: a generic method writing the field");
        MethodInfo gs = typeof(WsGen<>).GetMethod("Set");
        bool bad;
        int gk = DescWriterScan.ScanMethod(gs, DescWriterScan.ReflectionBody, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(!bad && gk == KC, "writerscan: generic type: own generic field skipped, Coworkers.Add found (" + gk + ", bad " + bad + ")");
        // fail-safe: unreadable bodies count for every kind
        MethodInfo st = typeof(WsSubjects).GetMethod("StOther", BindingFlags.Static | BindingFlags.NonPublic);
        int k = DescWriterScan.ScanMethod(st, delegate(MethodBase mb) { throw new InvalidOperationException("no body"); }, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(bad && k == DescWriterScan.KAll, "writerscan: body read throws = every kind");
        k = DescWriterScan.ScanMethod(st, delegate(MethodBase mb) { return null; }, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(bad && k == DescWriterScan.KAll, "writerscan: no body for a method with IL = every kind");
        k = DescWriterScan.ScanMethod(st, delegate(MethodBase mb) { return new byte[] { 0x02, 0x7B, 0xFF, 0xFF, 0x00, 0x0A, 0x26, 0x2A }; }, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(bad && k == DescWriterScan.KAll, "writerscan: unresolvable field token outside a generic context = every kind");
        k = DescWriterScan.ScanMethod(st, delegate(MethodBase mb) { return new byte[] { 0x02, 0x7D, 0x01 }; }, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
        Check(bad && k == DescWriterScan.KAll, "writerscan: truncated IL = every kind");
        Check(DescWriterScan.NoBody(typeof(WsAbstract).GetMethod("M")), "writerscan: abstract method has no body");
        // synthetic: ldnull; stfld plain, and with the store as a switch target
        FieldInfo fb = typeof(WsJob).GetField("FormerBoss");
        DescWriterScan.Resolver r = delegate(int tok) { if (tok == 0x04000001) return fb; throw new ArgumentException("token"); };
        Check(DescWriterScan.ScanIl(new byte[] { 0x02, 0x14, 0x7D, 0x01, 0x00, 0x00, 0x04, 0x2A }, r, f, l, null) == 0, "writerscan: synthetic ldnull; stfld = removal");
        Check(DescWriterScan.ScanIl(new byte[] { 0x02, 0x03, 0x45, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x14, 0x7D, 0x01, 0x00, 0x00, 0x04, 0x2A }, r, f, l, null) == KB,
            "writerscan: synthetic switch into the store = write");
        // LC-7c unresolvable token: Missing() separates a member / type reference that does not resolve from a corrupt token
        Check(DescWriterScan.Missing(new ArgumentException("x"), 0x0A000005), "writerscan: MemberRef ArgumentException = missing target");
        Check(DescWriterScan.Missing(new MissingFieldException(), 0x0A000005) && DescWriterScan.Missing(new TypeLoadException(), 0x01000002), "writerscan: .NET Missing* / TypeLoad = missing target");
        Check(!DescWriterScan.Missing(new ArgumentOutOfRangeException("x"), 0x0A000005), "writerscan: out-of-range row = unreadable");
        Check(!DescWriterScan.Missing(new ArgumentException("x"), 0x70000001) && !DescWriterScan.Missing(new ArgumentException("x"), 0x11000001), "writerscan: string / signature table token = unreadable");
        Check(!DescWriterScan.Missing(new InvalidOperationException("x"), 0x0A000005), "writerscan: other exception = unreadable");
        // a resolver that answers null for a missing token (as For does): the store names no target, the rest is still scanned
        DescWriterScan.Resolver rm = delegate(int tok) { if (tok == 0x04000001) return fb; return null; };
        Check(DescWriterScan.ScanIl(new byte[] { 0x02, 0x03, 0x7D, 0x09, 0x00, 0x00, 0x0A, 0x2A }, rm, f, l, null) == 0, "writerscan: stfld of an unresolvable token writes nothing");
        Check(DescWriterScan.ScanIl(new byte[] { 0x02, 0x03, 0x7D, 0x09, 0x00, 0x00, 0x0A, 0x02, 0x03, 0x7D, 0x01, 0x00, 0x00, 0x04, 0x2A }, rm, f, l, null) == KB,
            "writerscan: a write next to an unresolvable token is still found");
        // assembly scan: per-kind counts, allowed methods skipped
        DescWriterScan.Result res = new DescWriterScan.Result();
        Dictionary<string, bool> allowed = new Dictionary<string, bool>();
        allowed["WsSubjects::Stfld"] = true;
        DescWriterScan.ScanAssembly(typeof(WsSubjects).Assembly, delegate(Type t) { return !t.Name.StartsWith("Ws"); }, allowed, DescWriterScan.ReflectionBody, f, l, res, 50);
        Check(res.Unreadable == 0 && res.Mask == (KC | KB), "writerscan: assembly mask " + res.Mask + ", unreadable " + res.Unreadable);
        Check(res.PerKind[1] == 5 && res.PerKind[0] == 10, "writerscan: per kind formerboss " + res.PerKind[1] + " (5, Stfld allowed), coworkers " + res.PerKind[0] + " (10)");
        foreach (string line in res.Log) if (line.Contains("::Stfld ")) Check(false, "writerscan: allowed method logged");
        bool at = res.Log.Count > 0;
        foreach (string line in res.Log) if (!line.Contains(" at IL_") && !line.Contains("(unreadable")) at = false;
        Check(at, "writerscan: every logged writer carries its IL offset (LC-7f)");
        // LC-7c pins: a hash match covers the method (only that signature), a mismatch leaves it scanned
        MethodInfo sm = typeof(WsSubjects).GetMethod("Stfld", BindingFlags.Static | BindingFlags.NonPublic);
        string an = typeof(WsSubjects).Assembly.GetName().Name;
        ulong h = AutShadowCore.MethodIlHash(AutShadowCore.SigKey(sm), sm, new Dictionary<Module, Dictionary<int, string>>());
        DescWriterScan.Pin good = new DescWriterScan.Pin(an, "WsSubjects", "Stfld", AutShadowCore.ParamsKey(sm), h, KB, "Test");
        DescWriterScan.Pin badPin = new DescWriterScan.Pin(an, "WsSubjects", "Stfld", AutShadowCore.ParamsKey(sm), h + 1, KB, "Test");
        DescWriterScan.Pin other = new DescWriterScan.Pin("NoSuchAssembly", "WsSubjects", "Stfld", AutShadowCore.ParamsKey(sm), h, KB, "Test");
        List<string> mism = new List<string>();
        List<KeyValuePair<DescWriterScan.Pin, MethodBase>> hit = DescWriterScan.MatchPins(typeof(WsSubjects).Assembly, new DescWriterScan.Pin[] { good, other }, mism);
        Check(hit.Count == 1 && hit[0].Value == sm && mism.Count == 0, "writerscan: pin hash match found (" + hit.Count + ", mismatches " + mism.Count + "), other assembly ignored");
        Dictionary<string, bool> covered = new Dictionary<string, bool>();
        DescWriterScan.Cover(covered, sm);
        res = new DescWriterScan.Result();
        DescWriterScan.ScanAssembly(typeof(WsSubjects).Assembly, delegate(Type t) { return !t.Name.StartsWith("Ws"); }, covered, DescWriterScan.ReflectionBody, f, l, res, 50);
        Check(res.PerKind[1] == 5, "writerscan: pinned Stfld not scanned (formerboss " + res.PerKind[1] + ", 5)");
        // LC-7d: one key, several known builds: any equal hash matches, no mismatch reported
        mism.Clear();
        DescWriterScan.Pin anyAsm = new DescWriterScan.Pin(null, "WsSubjects", "Stfld", AutShadowCore.ParamsKey(sm), h, KB, "Test");
        hit = DescWriterScan.MatchPins(typeof(WsSubjects).Assembly, new DescWriterScan.Pin[] { badPin, anyAsm }, mism);
        Check(hit.Count == 1 && hit[0].Key == anyAsm && mism.Count == 0, "writerscan: second build's hash of the same key matches (" + hit.Count + ", mismatches " + mism.Count + ")");
        mism.Clear();
        hit = DescWriterScan.MatchPins(typeof(WsSubjects).Assembly, new DescWriterScan.Pin[] { badPin }, mism);
        Check(hit.Count == 0 && mism.Count == 1, "writerscan: pin hash mismatch reported (" + mism.Count + ")");
        res = new DescWriterScan.Result();
        DescWriterScan.ScanAssembly(typeof(WsSubjects).Assembly, delegate(Type t) { return !t.Name.StartsWith("Ws"); }, new Dictionary<string, bool>(), DescWriterScan.ReflectionBody, f, l, res, 50);
        Check(res.PerKind[1] == 6 && (res.Mask & KB) != 0, "writerscan: mismatched pin leaves FormerBoss untrusted (formerboss " + res.PerKind[1] + ", 6)");
        // the shipped pins are well formed
        foreach (DescWriterScan.Pin pin in DescWriterScan.Pins) Check(pin.Key.StartsWith(pin.Type + "::" + pin.Name + "(") && pin.Hash != 0 && pin.Kind != 0, "writerscan: pin " + pin.Key);
    }
}

public class WsJob { public List<object> Coworkers; public object FormerBoss; public object Other; }
public class WsMgr { public WsJob mJob; }
static class RfSubjects
{
    static void ReflectHit(WsJob j, object x) { typeof(WsJob).GetField("FormerBoss").SetValue(j, x); }
    static void ReflectMiss(WsJob j, object x) { typeof(WsJob).GetField("Other").SetValue(j, x); }
    static void NameOnly() { Console.Write("FormerBoss"); }
    static void Emit() { new System.Reflection.Emit.DynamicMethod("x", null, null); }
    static void Invoke(WsJob j, object x) { typeof(WsJob).InvokeMember("Other", BindingFlags.SetField, null, j, new object[] { x }); }
}
public abstract class WsAbstract { public abstract void M(); }
public class WsGen<T> { public T Val; public void Set(T v, WsJob j) { Val = v; j.Coworkers.Add(v); } }
static class WsSubjects
{
    static void Stfld(WsJob j, object x) { j.FormerBoss = x; }
    static void StNull(WsJob j) { j.FormerBoss = null; }
    static void StCond(WsJob j, object x, bool c) { j.FormerBoss = c ? null : x; }
    static void StOther(WsJob j, object x) { j.Other = x; }
    static void Addr(WsJob j, object x) { Interlocked.Exchange(ref j.FormerBoss, x); }
    static void ListAdd(WsJob j, object x) { j.Coworkers.Add(x); }
    static void ListInsert(WsJob j, object x) { j.Coworkers.Insert(0, x); }
    static int ListRead(WsJob j, object x)
    {
        int n = 0;
        if (j.Coworkers != null && j.Coworkers.Contains(x)) n++;
        n += j.Coworkers.Count;
        foreach (object o in j.Coworkers) if (o != null) n++;
        List<object> copy = new List<object>(j.Coworkers);
        return n + copy.Count;
    }
    static void ListLocal(WsJob j, object x) { List<object> c = j.Coworkers; c.Add(x); }
    static void ListPass(WsJob j, object x) { Helper(j.Coworkers, x); }
    static void ListPassAdd(WsJob j, object x) { AddTo(j.Coworkers, x); }
    static void ListPassRemove(WsJob j, object x) { RemoveFrom(j.Coworkers, x); }
    static void ListRemove(WsJob j, object x) { j.Coworkers.Remove(x); j.Coworkers.RemoveAt(0); j.Coworkers.Clear(); }
    static void AddTo(List<object> l, object x) { l.Add(x); }
    static void RemoveFrom(List<object> l, object x) { for (int i = l.Count - 1; i >= 0; i--) if (l[i] == x) l.RemoveAt(i); }
    static void ClearViaHelper(WsJob j) { Clear(ref j.FormerBoss); }
    static void ClearViaHelper2(WsJob j) { Clear2(ref j.FormerBoss); }
    static void SetViaHelper(WsJob j, object x) { SetRef(ref j.FormerBoss, x); }
    static void Clear<S>(ref S o) { o = default(S); }
    static void Clear2(ref object o) { if (o != null) o = null; }
    static void SetRef(ref object o, object x) { o = x; }
    static void Helper(List<object> l, object x) { if (l.Count > 100) Console.Write(""); }
    static void ListReplace(WsJob j) { j.Coworkers = new List<object>(); }
    static void JobSwap(WsMgr m, WsJob j) { m.mJob = j; }
    static void JobClear(WsMgr m) { m.mJob = null; }
    static void ListReplaceCopy(WsJob j, List<object> src) { j.Coworkers = new List<object>(src); }
    static void ListAlias(WsJob j, object x) { List<object> c = new List<object>(); j.Coworkers = c; c.Add(x); }
    static void ListRemoveProp(WsJob j) { j.Coworkers.Remove(j.FormerBoss); }
    static void ListRemoveCall(WsJob j) { j.Coworkers.Remove(Pick(j)); }
    static void ListPassAddCall(WsJob j) { AddTo(j.Coworkers, Pick(j)); }
    static object Pick(WsJob j) { return j.Other; }
    static int ListSource(WsJob j) { List<object> c = new List<object>(); c.AddRange(j.Coworkers); return c.Count; }
    static void ListAddRangeInto(WsJob j, List<object> other) { j.Coworkers.AddRange(other); }
    static void Both(WsJob j, object x) { j.Coworkers.Add(x); j.FormerBoss = x; }
    static void GenericWrite<T>(WsJob j, T x) { j.FormerBoss = x; }
}
