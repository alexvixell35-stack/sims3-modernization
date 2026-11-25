using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of deadrefsweep (DeadRefs.cs, issue #91): dropping gone entries from the holders' lists,
    // arrays and dictionaries, and the guard that refuses a pass that looks like a load-time mistake (every description
    // read from a save is invalid until SimDescription.Fixup runs). Self-check: tools/selftest/deadrefs.
    public static class DeadRefsCore
    {
        // Entries for which gone() is true, removed in place; returns how many.
        public static int DropAll<T>(List<T> list, Predicate<T> gone)
        {
            if (list == null) return 0;
            return list.RemoveAll(gone);
        }

        // A copy without the gone entries, or null when nothing is gone (the caller keeps the old array).
        public static T[] Without<T>(T[] items, Predicate<T> gone, out int removed)
        {
            removed = 0;
            if (items == null) return null;
            List<T> keep = new List<T>(items.Length);
            foreach (T t in items) { if (gone(t)) removed++; else keep.Add(t); }
            return removed == 0 ? null : keep.ToArray();
        }

        // Keys for which gone() is true, removed; returns how many.
        public static int DropKeys<K, V>(Dictionary<K, V> d, Predicate<K> gone)
        {
            if (d == null) return 0;
            List<K> drop = new List<K>();
            foreach (K k in d.Keys) if (gone(k)) drop.Add(k);
            foreach (K k in drop) d.Remove(k);
            return drop.Count;
        }

        // True when a pass should not act: more than half of a large holder gone is what a sweep run before
        // SimDescription.Fixup sees (every household member invalid), not real leftovers.
        public static bool LooksLikeLoad(int gone, int total)
        {
            return total >= 16 && gone * 2 > total;
        }

        // Repro conversation-gone-check, one seeded conversation (host + one destroyed member). "pass" / "fail" / "na".
        // left (expectLeft, switches off): the gone member still listed = pass; gone 0 with the host gone from it = na (it left
        // before the check, EA's exit or the #166 throw the queue reports); gone 0 with the host still there = fail.
        // cleaned (switches on): no gone member and the host still listed (members 1) = pass; an empty list = pass only with this
        // conversation's own phase-15 removal (removedHere > 0: the host left afterwards through EA's exit); empty without it and
        // the host left = na (EA's exit ran before the phase reached it); a gone member left while the host is still there = fail;
        // a gone member left after a removal = fail; the host left with the gone member still listed = na. Switches in the wrong
        // state = fail.
        public static string ConvSampleVerdict(bool expectLeft, bool on, int gone, int members, int removedHere, bool hostLeft)
        {
            if (expectLeft)
            {
                if (on) return "fail";
                if (gone >= 1) return "pass";
                return hostLeft ? "na" : "fail";
            }
            if (!on) return "fail";
            if (gone == 0)
            {
                if (members == 1 && !hostLeft) return "pass";
                if (members == 0) return removedHere > 0 ? "pass" : (hostLeft ? "na" : "fail");
                return hostLeft ? "na" : "fail";
            }
            if (removedHere > 0) return "fail";
            return hostLeft ? "na" : "fail";
        }

        // Repro "seed clean" (docs/330 §8.3): runs phase 15's step right after the seed only when the sweep is on and the pass's
        // own unsafe-state check passes; returns the reason it does not run, or null.
        public static string ConvSeedCleanGate(bool sweepOn, string unsafeWhy)
        {
            if (!sweepOn) return "deadrefsweep off";
            return unsafeWhy;
        }

        // Arm verdict over the samples: any fail = "fail"; fewer than 3 conclusive (pass) samples = "inconclusive"; else "pass".
        public static string ConvArmVerdict(int pass, int fail, int na)
        {
            if (fail > 0) return "fail";
            return pass >= 3 ? "pass" : "inconclusive";
        }

        // docs/336 deadrefdelta: an object pass steps the owner / handler / cooking holders only on indexed objects when the
        // switch is on, the index was seeded by a finished full walk, and an event (kSimDestroyed / kSimDescriptionDisposed, or
        // the agent's sweep) asked for the pass. The first pass after a load and the 24 h floor walk every object and re-seed.
        public static bool UseIndex(bool deltaOn, bool seeded, bool byEvent)
        {
            return deltaOn && seeded && byEvent;
        }
    }

    // docs/336 deadrefdelta (LC-5 of docs/333): the hybrid holder index of the deadrefsweep object pass. Every key and member
    // is an id (ObjectGuid value, SimDescriptionId), never an object reference, so the index keeps nothing alive.
    //   by Sim:   owner / thief Sim id -> objects whose owner component names that Sim (stepped when it is destroyed)
    //   by desc:  SimDescriptionId -> objects with a relationship handler of that description (stepped when it is disposed
    //             or one of its relationships decays: GameObjectRelationship.RemoveRelationship)
    //   unkeyed:  objects stepped on every delta pass: cooking handlers (a process lets go without a hook) and holders whose
    //             entry was already gone when noted
    //   use:      objects with a non-empty use, reference or routing-reference list (deadrefslimuse)
    // A full walk re-seeds (BeginSeed ... EndSeed); marks made during it are kept (the walk may have passed those objects).
    // Unkeyed and use are consumed by each delta pass (Take*) and re-noted by the step from what the object still holds; the
    // keyed maps lose a key when it is taken and lose dead objects at every Prune. Caps: kPendingMax marks or an entry count
    // over 2 x live objects + 1024 reset the index (the next pass walks every object), so its size stays bounded.
    public sealed class DeltaIndex
    {
        readonly Dictionary<ulong, Dictionary<ulong, bool>> mBySim = new Dictionary<ulong, Dictionary<ulong, bool>>();
        readonly Dictionary<ulong, Dictionary<ulong, bool>> mByDesc = new Dictionary<ulong, Dictionary<ulong, bool>>();
        Dictionary<ulong, bool> mUnkeyed = new Dictionary<ulong, bool>(), mUse = new Dictionary<ulong, bool>();
        readonly Dictionary<ulong, bool> mPendSim = new Dictionary<ulong, bool>(), mPendDesc = new Dictionary<ulong, bool>();
        public bool Seeded, Seeding, SeedSlim; // SeedSlim: the use set was kept through the seed (deadrefslimuse on)
        public int SimEntries, DescEntries, HighEntries, HighUse, Overflows;
        public const int kPendingMax = 4096;

        public int UnkeyedCount { get { return mUnkeyed.Count; } }
        public int UseCount { get { return mUse.Count; } }
        public int PendingCount { get { return mPendSim.Count + mPendDesc.Count; } }
        public int Entries { get { return SimEntries + DescEntries + mUnkeyed.Count + mUse.Count; } }

        public void Reset()
        {
            mBySim.Clear(); mByDesc.Clear(); mUnkeyed.Clear(); mUse.Clear(); mPendSim.Clear(); mPendDesc.Clear();
            SimEntries = DescEntries = 0; Seeded = Seeding = false;
        }
        public void BeginSeed(bool slim) { Reset(); Seeding = true; SeedSlim = slim; }
        public void EndSeed() { if (Seeding) { Seeding = false; Seeded = true; } }

        static bool AddTo(Dictionary<ulong, Dictionary<ulong, bool>> m, ulong key, ulong obj)
        {
            Dictionary<ulong, bool> s;
            if (!m.TryGetValue(key, out s)) m[key] = s = new Dictionary<ulong, bool>();
            if (s.ContainsKey(obj)) return false;
            s[obj] = true; return true;
        }
        void High() { int e = Entries; if (e > HighEntries) HighEntries = e; if (mUse.Count > HighUse) HighUse = mUse.Count; }
        public void NoteSim(ulong sim, ulong obj) { if (AddTo(mBySim, sim, obj)) { SimEntries++; High(); } }
        public void NoteDesc(ulong desc, ulong obj) { if (AddTo(mByDesc, desc, obj)) { DescEntries++; High(); } }
        public void NoteUnkeyed(ulong obj) { if (!mUnkeyed.ContainsKey(obj)) { mUnkeyed[obj] = true; High(); } }
        public void NoteUse(ulong obj) { if (!mUse.ContainsKey(obj)) { mUse[obj] = true; High(); } }

        // A Sim destroyed, a description disposed or one of its relationships decayed: its objects are stepped next delta pass.
        public void MarkSim(ulong sim) { Mark(mPendSim, sim); }
        public void MarkDesc(ulong desc) { Mark(mPendDesc, desc); }
        void Mark(Dictionary<ulong, bool> pend, ulong key)
        {
            if (!Seeded && !Seeding) return; // the next pass walks every object anyway
            pend[key] = true;
            if (PendingCount > kPendingMax) { Reset(); Overflows++; }
        }

        // The objects whose holders this pass steps: every object under a marked key, plus the unkeyed set. Marked keys and
        // the unkeyed set are emptied; the step re-notes what each object still holds.
        public Dictionary<ulong, bool> TakeHolders(out int fromKeys, out int fromUnkeyed)
        {
            Dictionary<ulong, bool> r = new Dictionary<ulong, bool>();
            fromKeys = Take(mBySim, mPendSim, r, ref SimEntries) + Take(mByDesc, mPendDesc, r, ref DescEntries);
            fromUnkeyed = mUnkeyed.Count;
            foreach (ulong o in mUnkeyed.Keys) r[o] = true;
            mUnkeyed = new Dictionary<ulong, bool>();
            return r;
        }
        static int Take(Dictionary<ulong, Dictionary<ulong, bool>> m, Dictionary<ulong, bool> pend, Dictionary<ulong, bool> into, ref int entries)
        {
            int n = 0;
            foreach (ulong k in pend.Keys)
            {
                Dictionary<ulong, bool> s;
                if (!m.TryGetValue(k, out s)) continue;
                foreach (ulong o in s.Keys) { into[o] = true; n++; }
                entries -= s.Count; m.Remove(k);
            }
            pend.Clear();
            return n;
        }
        public Dictionary<ulong, bool> TakeUse() { Dictionary<ulong, bool> r = mUse; mUse = new Dictionary<ulong, bool>(); return r; }

        // Drops (countOnly false) or counts the entries naming an object for which dead(id) is true, in every set.
        public int Prune(Predicate<ulong> dead, bool countOnly)
        {
            return PruneMap(mBySim, dead, countOnly, ref SimEntries) + PruneMap(mByDesc, dead, countOnly, ref DescEntries)
                 + PruneSet(mUnkeyed, dead, countOnly) + PruneSet(mUse, dead, countOnly);
        }
        static int PruneSet(Dictionary<ulong, bool> s, Predicate<ulong> dead, bool countOnly)
        {
            List<ulong> drop = new List<ulong>();
            foreach (ulong o in s.Keys) if (dead(o)) drop.Add(o);
            if (!countOnly) foreach (ulong o in drop) s.Remove(o);
            return drop.Count;
        }
        static int PruneMap(Dictionary<ulong, Dictionary<ulong, bool>> m, Predicate<ulong> dead, bool countOnly, ref int entries)
        {
            int n = 0; List<ulong> emptyKeys = null;
            foreach (KeyValuePair<ulong, Dictionary<ulong, bool>> kv in m)
            {
                List<ulong> drop = null;
                foreach (ulong o in kv.Value.Keys) if (dead(o)) { n++; if (!countOnly) { if (drop == null) drop = new List<ulong>(); drop.Add(o); } }
                if (drop == null) continue;
                foreach (ulong o in drop) kv.Value.Remove(o);
                entries -= drop.Count;
                if (kv.Value.Count == 0) { if (emptyKeys == null) emptyKeys = new List<ulong>(); emptyKeys.Add(kv.Key); }
            }
            if (emptyKeys != null) foreach (ulong k in emptyKeys) m.Remove(k);
            return n;
        }

        // An index larger than the world can explain (entries naming live objects under keys they no longer hold) resets.
        public bool OverCap(int liveObjects)
        {
            if (Entries <= 2 * liveObjects + 1024) return false;
            Reset(); Overflows++; return true;
        }
    }

    // docs/333 LC-7 (deadrefdescrev): a DeltaIndex over phase-4 holders (SimDescriptionId -> the description), keyed by
    // the description or Sim each one references. The holder objects are kept here so a pass resolves an id without
    // SimDescription.Find (a walk of every household). Drop takes a disposed holder out at its event; Prune drops ids whose
    // holder is gone and every entry naming an id no longer held; any reset of the index (overflow, a switch off) clears
    // the holder map too, so nothing outlives the index.
    public sealed class DescRevIndex<T> where T : class
    {
        public readonly DeltaIndex Idx = new DeltaIndex();
        readonly Dictionary<ulong, T> mObj = new Dictionary<ulong, T>();
        public int Holders { get { Sync(); return mObj.Count; } }
        public bool Live { get { return Idx.Seeded || Idx.Seeding; } }
        void Sync() { if (!Live && mObj.Count > 0) mObj.Clear(); } // the index reset itself (pending overflow)
        public void Reset() { Idx.Reset(); mObj.Clear(); }
        public void BeginSeed() { Reset(); Idx.BeginSeed(false); }
        public void Hold(ulong id, T o) { Sync(); if (Live) mObj[id] = o; }
        public T Get(ulong id) { T o; return mObj.TryGetValue(id, out o) ? o : null; }
        public bool Drop(ulong id) { return mObj.Remove(id); }
        public int Prune(Predicate<T> gone)
        {
            Sync();
            List<ulong> dead = new List<ulong>();
            foreach (KeyValuePair<ulong, T> kv in mObj) if (gone(kv.Value)) dead.Add(kv.Key);
            foreach (ulong id in dead) mObj.Remove(id);
            return Idx.Prune(delegate(ulong id) { return !mObj.ContainsKey(id); }, false);
        }
        public bool OverCap(int live)
        {
            if (!Idx.OverCap(live) && mObj.Count <= 2 * live + 1024) return false;
            if (Live) { Idx.Reset(); Idx.Overflows++; }
            mObj.Clear(); return true;
        }
    }

    // docs/336 §9 (LC-6a part 1, deadrefperlot): the objects the sweep's typed phases need (Sims, fish, cars and boats,
    // horseshoe courts, dartboards: kind bits), kept per lot so a pass gathers them without the world snapshot. Ids only:
    // lot id -> (object id -> kind bits), plus object id -> lot id. Filled from a full snapshot (Seed), kept by the
    // GameObject.Init / Destroy / InternalOnSetLotCurrent hooks, pruned at every gather (an id the world no longer resolves).
    // LC-6a gathers every lot (lotLive null); LC-6 will pass the live-lot test once lots park.
    public delegate bool PlVisit(ulong id, byte kinds);
    public sealed class PerLotIndex
    {
        public const byte kSim = 1, kFish = 2, kVehicle = 4, kHorseshoe = 8, kDartboard = 16;
        public const int kKinds = 5;
        readonly Dictionary<ulong, Dictionary<ulong, byte>> mByLot = new Dictionary<ulong, Dictionary<ulong, byte>>();
        readonly Dictionary<ulong, ulong> mLotOf = new Dictionary<ulong, ulong>();
        public bool Seeded;
        public int High, Overflows;

        public int Count { get { return mLotOf.Count; } }
        public int Lots { get { return mByLot.Count; } }
        public bool Has(ulong id) { return mLotOf.ContainsKey(id); }
        public ulong LotOf(ulong id) { ulong l; return mLotOf.TryGetValue(id, out l) ? l : ulong.MaxValue; }

        public void Reset() { mByLot.Clear(); mLotOf.Clear(); Seeded = false; }

        public void Note(ulong id, byte kinds, ulong lot)
        {
            if (kinds == 0) return;
            ulong was;
            if (mLotOf.TryGetValue(id, out was)) Remove(id, was);
            Dictionary<ulong, byte> b;
            if (!mByLot.TryGetValue(lot, out b)) mByLot[lot] = b = new Dictionary<ulong, byte>();
            b[id] = kinds; mLotOf[id] = lot;
            if (mLotOf.Count > High) High = mLotOf.Count;
        }

        // the object changed lot; false when it is not indexed or did not move
        public bool Move(ulong id, ulong lot)
        {
            ulong was;
            if (!mLotOf.TryGetValue(id, out was) || was == lot) return false;
            byte kinds = mByLot[was][id];
            Remove(id, was);
            Note(id, kinds, lot);
            return true;
        }

        public bool Drop(ulong id)
        {
            ulong was;
            if (!mLotOf.TryGetValue(id, out was)) return false;
            Remove(id, was); return true;
        }

        void Remove(ulong id, ulong lot)
        {
            Dictionary<ulong, byte> b;
            if (mByLot.TryGetValue(lot, out b)) { b.Remove(id); if (b.Count == 0) mByLot.Remove(lot); }
            mLotOf.Remove(id);
        }

        // Visits every member of the lots lotLive accepts (all when null); a visit that returns false drops that id.
        // Returns the dropped count.
        public int Walk(Predicate<ulong> lotLive, PlVisit visit)
        {
            List<KeyValuePair<ulong, ulong>> drop = null;
            foreach (KeyValuePair<ulong, Dictionary<ulong, byte>> lot in mByLot)
            {
                if (lotLive != null && !lotLive(lot.Key)) continue;
                foreach (KeyValuePair<ulong, byte> m in lot.Value)
                    if (!visit(m.Key, m.Value)) { if (drop == null) drop = new List<KeyValuePair<ulong, ulong>>(); drop.Add(new KeyValuePair<ulong, ulong>(m.Key, lot.Key)); }
            }
            if (drop == null) return 0;
            foreach (KeyValuePair<ulong, ulong> d in drop) Remove(d.Key, d.Value);
            return drop.Count;
        }

        // more members than the world has objects (+ slack): reset, the next pass gathers from a snapshot and re-seeds
        public bool OverCap(int liveObjects)
        {
            if (mLotOf.Count <= liveObjects + 1024) return false;
            Reset(); Overflows++; return true;
        }
    }

    // docs/336 §9 (LC-6a part 2): EA methods that write a use list directly (ActorsUsingMe.Add, not AddToUseList) are hooked
    // and open a pin for their task while they run: the objects they may write (resolved at entry) plus every object created
    // in that task meanwhile (GameObject.Init) stay in the slim use walk on every delta pass until the method returns; then
    // they are noted once more and the normal re-note (non-empty list) takes over. No yield analysis needed: whenever the
    // write happens inside the pinned window, the object is stepped. A pin whose task died without returning is noted one
    // last time and dropped. Ids only. A pin with more than kPinIds objects, or more than kPinMax open pins, turns into
    // "all": while it is open every delta pass walks every object for the use lists (exact, just slower).
    public sealed class UsePins
    {
        sealed class Pin { public int Depth; public readonly Dictionary<ulong, bool> Ids = new Dictionary<ulong, bool>(); public bool All; }
        readonly Dictionary<ulong, Pin> mPins = new Dictionary<ulong, Pin>();
        int mAll; // open pins with All set
        public const int kPinIds = 256, kPinMax = 4096;
        public int Opens, Attached, DeadDropped, HighPins, HighIds, Overflows;

        public int Count { get { return mPins.Count; } }
        public bool AnyAll { get { return mAll > 0; } }
        public int IdCount { get { int n = 0; foreach (Pin p in mPins.Values) n += p.Ids.Count; return n; } }

        public void Reset() { mPins.Clear(); mAll = 0; }

        // A hooked writer starts in task. ids: objects it may write (0 = none).
        public void Open(ulong task, ulong[] ids)
        {
            Pin p;
            if (!mPins.TryGetValue(task, out p)) { mPins[task] = p = new Pin(); if (mPins.Count > HighPins) HighPins = mPins.Count; }
            p.Depth++; Opens++;
            if (mPins.Count > kPinMax) SetAll(p);
            if (ids != null) foreach (ulong id in ids) Add(p, id);
        }

        // An object was created in task: if a writer is open there, it may write that object's list.
        public void Attach(ulong task, ulong id)
        {
            Pin p;
            if (mPins.TryGetValue(task, out p)) { Add(p, id); Attached++; }
        }

        void Add(Pin p, ulong id)
        {
            if (id == 0 || p.All) return;
            p.Ids[id] = true;
            if (p.Ids.Count > HighIds) HighIds = p.Ids.Count;
            if (p.Ids.Count > kPinIds) SetAll(p);
        }
        void SetAll(Pin p) { if (!p.All) { p.All = true; mAll++; Overflows++; p.Ids.Clear(); } }

        // The writer returned (or threw): when the task's outermost one returns, the ids to note once more (null otherwise).
        // allOut: the pin was in All mode (the caller marks the next pass to walk every object).
        public List<ulong> Close(ulong task, out bool allOut)
        {
            allOut = false;
            Pin p;
            if (!mPins.TryGetValue(task, out p)) return null;
            if (--p.Depth > 0) return null;
            mPins.Remove(task);
            if (p.All) { mAll--; allOut = true; }
            return new List<ulong>(p.Ids.Keys);
        }

        // Every pinned id (a delta pass steps them all).
        public void CopyIds(Dictionary<ulong, bool> into) { foreach (Pin p in mPins.Values) foreach (ulong id in p.Ids.Keys) into[id] = true; }

        // Pins of tasks for which dead(task) is true: dropped, their ids returned for one last note. allOut as in Close.
        public List<ulong> DropDead(Predicate<ulong> dead, out bool allOut)
        {
            allOut = false;
            List<ulong> ids = new List<ulong>(), gone = new List<ulong>();
            foreach (KeyValuePair<ulong, Pin> kv in mPins) if (dead(kv.Key)) gone.Add(kv.Key);
            foreach (ulong t in gone)
            {
                Pin p = mPins[t];
                if (p.All) { mAll--; allOut = true; }
                ids.AddRange(p.Ids.Keys);
                mPins.Remove(t); DeadDropped++;
            }
            return ids;
        }
    }
}
