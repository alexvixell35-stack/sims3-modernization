using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Fishing;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // deadrefperlot (docs/336 §9, LC-6a of docs/333; default OFF, needs deadrefcadence): the sweep's typed phases (5 Sims,
    // 6 fish, 7 cars / boats, 8 horseshoe courts, 9 dartboards, 13 and 15 Sims) gather from per-lot id lists
    // (DeadRefsCore.PerLotIndex) instead of filtering the world snapshot. The snapshot (GetObjects<GameObject>) is then taken
    // only by passes that walk every object for phase 10 (first pass after a load, the 24 h floor, an unseeded holder
    // index, deadrefslimuse off, verify); each such snapshot re-seeds the lists, so the floor walk also catches any bypass
    // writer. Kept between snapshots by hooks: GameObject.Init (every GameObject passes this sealed override once,
    // [verified decompiled]), GameObject.Destroy (dropped once the world no longer resolves the id) and
    // GameObject.InternalOnSetLotCurrent (the lot key; all 10 EA overrides call base [verified decompiled]). A gather drops
    // ids the world no longer resolves (GetObject null); destroyed objects the world still lists stay, as in the snapshot.
    // Same items as the snapshot filters (set equality; the order differs, phases act per item). deadrefdeltaverify compares
    // both on every pass that has both. No leak: ids only; bounded by indexed live objects (cap live + 1024); reset on load,
    // switch-off and overflow. Lot unload (CP-2 OnLotLoad) and parking (FO-4) are LC-6.
    // [1715] per-lot gathers, [1716] objects gathered, [1717] index entries, [1718] high-water entries, [1719] lots,
    // [1720] seeds from a snapshot, [1721] hook notes (adds + lot moves), [1722] drops (destroy + gather prune),
    // [1723] verify: snapshot items missing from the per-lot gather (must stay 0), [1724] verify: per-lot items the
    // snapshot lacks (must stay 0), [1725] verify: passes with more entries than live objects (must stay 0), [1726] last
    // per-lot gather in us.
    public static partial class Fixes
    {
        static readonly PerLotIndex sPl = new PerLotIndex();
        static StopWatch sPlWatch;

        static void ApplyDeadRefPerLot()
        {
            // GameObject.Init is patched in ApplyDeadRefPins (one hook serves both)
            Patch(typeof(GameObject), "Destroy", Type.EmptyTypes, typeof(DrPlDestroyHook), "DestroyHook", "DestroyOrig");
            Patch(typeof(GameObject), "InternalOnSetLotCurrent", new Type[] { typeof(Lot) }, typeof(DrPlLotHook), "LotHook", "LotOrig");
        }

        static byte DrPlKinds(object o)
        {
            if (o is Sim) return PerLotIndex.kSim;
            if (o is Fish) return PerLotIndex.kFish;
            if (o is CarOwnable || o is BoatOwnable) return PerLotIndex.kVehicle;
            if (o is HorseshoeCourt) return PerLotIndex.kHorseshoe;
            if (o is Dartboard) return PerLotIndex.kDartboard;
            return 0;
        }

        static ulong DrPlLot(GameObject o) { Lot l = o.InternalLotCurrent; return l != null ? l.LotId : 0; }

        static void DrPlGauges(int[] c) { c[1717] = sPl.Count; c[1718] = sPl.High; c[1719] = sPl.Lots; }

        static void DrPlLoadReset() { sPl.Reset(); }

        // a hook found the switch off while the lists are kept: they miss this write, drop them
        static bool DrPlLive()
        {
            if (!sPl.Seeded) return false;
            if (ShortcutOn("deadrefperlot") && ShortcutOn("deadrefcadence")) return true;
            sPl.Reset(); DrPlGauges(PatchCounters());
            return false;
        }

        static void DrPlInit(GameObject o)
        {
            if (!sPl.Seeded) return;
            try
            {
                byte k = DrPlKinds(o);
                if (k == 0 || !DrPlLive()) return;
                sPl.Note(o.ObjectId.Value, k, DrPlLot(o));
                int[] c = PatchCounters(); c[1721]++; DrPlGauges(c);
            }
            catch (Exception) { PatchCounters()[410]++; }
        }

        // A snapshot was taken: re-seed from it and hand back its typed lists (one pass over it instead of five filters).
        static List<object>[] DrPlSeed(List<object> objs)
        {
            int[] c = PatchCounters();
            List<object>[] r = DrPlLists();
            sPl.Reset();
            foreach (object o in objs)
            {
                byte k = DrPlKinds(o);
                if (k == 0) continue;
                DrPlAdd(r, o, k);
                GameObject g = (GameObject)o;
                try { sPl.Note(g.ObjectId.Value, k, DrPlLot(g)); } catch (Exception) { c[410]++; }
            }
            sPl.Seeded = true;
            c[1720]++; DrPlGauges(c);
            return r;
        }

        static List<object>[] DrPlLists() { List<object>[] r = new List<object>[PerLotIndex.kKinds]; for (int i = 0; i < r.Length; i++) r[i] = new List<object>(); return r; }
        static void DrPlAdd(List<object>[] r, object o, byte k) { for (int i = 0; i < PerLotIndex.kKinds; i++) if ((k & (1 << i)) != 0) r[i].Add(o); }

        // The per-lot gather: every lot (LC-6a), ids the world no longer resolves dropped.
        static List<object>[] DrPlGather()
        {
            int[] c = PatchCounters();
            if (sPlWatch == null) sPlWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sPlWatch.Restart();
            List<object>[] r = DrPlLists();
            int n = 0;
            c[1722] += sPl.Walk(null, delegate(ulong id, byte k)
            {
                GameObject g = GameObject.GetObject(new ObjectGuid(id));
                if (g == null) return false;
                DrPlAdd(r, g, k); n++;
                return true;
            });
            c[1715]++; c[1716] += n;
            c[1726] = (int)Math.Min(sPlWatch.GetElapsedTime(), int.MaxValue);
            DrPlGauges(c);
            return r;
        }

        // verify: the per-lot gather against the snapshot's typed lists, as id sets per kind
        static void DrPlVerify(List<object>[] mine, List<object>[] snap, int liveObjects)
        {
            int[] c = PatchCounters();
            for (int k = 0; k < PerLotIndex.kKinds; k++)
            {
                Dictionary<ulong, bool> a = new Dictionary<ulong, bool>();
                foreach (object o in mine[k]) a[((GameObject)o).ObjectId.Value] = true;
                foreach (object o in snap[k])
                {
                    ulong id = ((GameObject)o).ObjectId.Value;
                    if (a.Remove(id)) continue;
                    if (c[1723]++ < 5) ShortcutLog("deadrefperlot verify: " + o.GetType().Name + " " + id.ToString("X") + " is in the snapshot, not in the per-lot lists (unhooked add)");
                }
                c[1724] += a.Count;
            }
            if (liveObjects > 0 && sPl.Count > liveObjects) c[1725]++;
        }

        public class DrPlDestroyHook : GameObject
        {
            public void DestroyHook()
            {
                ulong id = 0;
                try { id = ObjectId.Value; } catch (Exception) { }
                DestroyOrig();
                SdiUrnDestroy(this, id);  // docs/359 (SimDescIndex.cs)
                if (id == 0 || !sPl.Seeded || !sPl.Has(id)) return;
                try { if (GameObject.GetObject(new ObjectGuid(id)) == null && sPl.Drop(id)) { int[] c = PatchCounters(); c[1722]++; DrPlGauges(c); } } catch (Exception) { PatchCounters()[410]++; }
            }
            public void DestroyOrig() { } // GameObject.Destroy (overrides that call base reach it; the rest are pruned at the next gather)
        }

        public class DrPlLotHook : GameObject
        {
            public void LotHook(Lot lot)
            {
                LotOrig(lot);
                if (!sPl.Seeded || !sPl.Has(ObjectId.Value) || !DrPlLive()) return;
                try { if (sPl.Move(ObjectId.Value, DrPlLot(this))) { int[] c = PatchCounters(); c[1721]++; DrPlGauges(c); } } catch (Exception) { PatchCounters()[410]++; }
            }
            public void LotOrig(Lot lot) { } // GameObject.InternalOnSetLotCurrent (all 10 EA overrides call base)
        }
    }
}
