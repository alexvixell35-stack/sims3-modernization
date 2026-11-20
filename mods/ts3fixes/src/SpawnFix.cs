using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.Fishing;
using Sims3.Gameplay.Objects.Rewards;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // spawnfix (2026-11-14, contributor, default ON) and spawnoffworld (default off, changes gameplay). Spawner code in general.
    //
    // Why [verified, decompile ObjectSpawner.cs 393 / 420 / 243, FishingSpotData.cs 84 / 95, CollectionHelperSingleton.cs 27-47,
    // SpawnerBase.cs 43-81]: every spawn, every removal of a spawned object that had a helper effect, every spawner at save load and
    // every fishing spot (de)activation calls CollectionHelper.Refresh(). That fires SetHelperSearchType, which EVERY SpawnerBase in
    // the world subscribes to (OnStartup), so one spawn = one handler call per spawner in the world, and a world load = N x N. A
    // handler removes the spawner's map tag from the active Sim (and with a helper selected re-adds it: a new MapTag, two map UI
    // events, a new VisualEffect per spawned object), and may walk the spawner's object set (SpawnerType getters).
    //
    // What it does with spawnfix on (EA runs unchanged with it off). FireEvent is the one door every Refresh goes through:
    // 1. Skip: the last fan-out was kNone for the same active Sim and this one is kNone again. Every handler would only Stop a spawner
    //    that has nothing started, so nothing is visible; LotIds is cleared as EA does. This is every spawn of a player without a helper.
    // 2. Single: the call came from Add/RemoveObjectFromSpawner, ObjectSpawner.ImportContent or FishingSpotData.Activate/DeactivateSpot
    //    (the hook sets the one spawner whose state changed). With the same search type and active Sim as the last fan-out, only that
    //    spawner's handler runs; the others would repeat what they already hold. LotIds is not cleared (only TreasureSpawnPoint uses it,
    //    and it is not touched). The spawner must have run OnStartup (EA's handler is not subscribed before that: an import at load).
    // 3. Anything else (a type or Sim change, SetMode, TearOfHorus, the helper removed, a spawner started since the last fan-out,
    //    ten sim minutes since the last fan-out) runs EA's full fan-out, which is also what re-syncs map tags after a Sim switch.
    // The last two keep EA's "any spawn re-syncs everything" for the cases it mattered: new spawners and a rebuilt map tag manager.
    //
    // spawnoffworld: GameObject.RemoveFromWorld clears the InWorld flag only; it keeps mLotCurrent and the spawn alarm
    // [GameObject.cs 4254-4277], and UpdateSpawnAlarm tests only mLotCurrent [ObjectSpawner.cs 485]. So a spawner taken out of the
    // world (a seasonal or festival swap) keeps creating objects around its old position. With the switch on TrySpawnObject does
    // nothing for such a spawner; TriggerSpawn still re-arms the alarm, so it spawns again once back in the world. Not part of
    // spawnfix: fewer objects appear.
    //
    // Counters 1328 FireEvent calls (switch on), 1329 fan-outs skipped (kNone again), 1330 single-spawner updates, 1331 single updates
    // not needed (spawner not subscribed yet), 1332 full fan-outs run, 1333 would-be single refused: a spawner started since the last
    // fan-out, 1334 refused: type, Sim or first call, 1335 refused: ten sim minutes passed, 1336 spawners seen starting, 1337 handler
    // calls avoided, 1338 ms in full fan-outs, 1339 spawn attempts by spawners not in the world, 1340 TrySpawnObject calls, 1341 of
    // those skipped (spawnoffworld), 1342 ms in TrySpawnObject, 1343 ms in single updates, 1344 fishing spot updates narrowed.
    // Not fixed (see the report): UpdateSpawnAlarm walks the lot's Sims for the Gatherer trait on every spawn and removal (timing
    // would change with a cache); TryPlaceSpawnedObjectInWorld tries kMaxSpawnAttempts + 1 find-good-location calls per spawn (RNG
    // stream and spawn result would change); CauseEffectSpawner.ClearSpawnedObjects destroys while enumerating mObjectSet.
    public static partial class Fixes
    {
        const long kSfResyncTicks = 10L * 600000000L;  // ten sim minutes (a sim minute is 600,000,000 ticks)

        static object sSfOwner;  // the CollectionHelperSingleton the state below belongs to: a new world has a new one
        static readonly Dictionary<SpawnerBase, bool> sSfSubs = new Dictionary<SpawnerBase, bool>();  // spawners whose OnStartup ran
        static bool sSfValid, sSfDirty, sSfLogged;
        static SpawnerBase.CollectionHelperSearchType sSfType;
        static Sim sSfActor;
        static long sSfStamp;
        static SpawnerBase sSfOnly;  // set by a hook around a call that Refreshes for one spawner; FireEvent takes it
        static long sSfFullTicks, sSfSpawnTicks, sSfOneTicks;

        // a new world has a new singleton (CollectionHelperSingleton.Setup): drop everything learned about the old one
        static void SfSync()
        {
            object cur = SpawnerBase.CollectionHelper;
            if (object.ReferenceEquals(cur, sSfOwner)) return;
            sSfOwner = cur;
            sSfSubs.Clear();
            sSfValid = sSfDirty = sSfLogged = false;
            sSfOnly = null;
            sSfActor = null;
        }

        static void SfMs(int slot, ref long acc, long t0)
        {
            acc += DateTime.UtcNow.Ticks - t0;
            PatchCounters()[slot] = (int)(acc / TimeSpan.TicksPerMillisecond);
        }

        // True when the call was answered here (skipped or narrowed); false = run EA's full fan-out.
        static bool SfFire(CollectionHelperSingleton s, SpawnerBase.CollectionHelperSearchType type)
        {
            int[] c = PatchCounters();
            c[1328]++;
            SfSync();
            SpawnerBase only = sSfOnly;
            sSfOnly = null;
            Sim act = Sim.ActiveActor;
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            bool same = sSfValid && type == sSfType && object.ReferenceEquals(act, sSfActor);
            if (same && type == SpawnerBase.CollectionHelperSearchType.kNone)
            {
                // nothing is started under kNone and the same Sim: every handler would only Stop a spawner that holds nothing
                s.LotIds.Clear();
                c[1329]++;
                c[1337] += sSfSubs.Count;
                SfFirst("skipped a kNone refresh");
                return true;
            }
            if (only != null)
            {
                if (!same) c[1334]++;
                else if (sSfDirty) c[1333]++;
                else if (now - sSfStamp >= kSfResyncTicks || now < sSfStamp) c[1335]++;
                else
                {
                    if (sSfSubs.ContainsKey(only))
                    {
                        long t0 = DateTime.UtcNow.Ticks;
                        only.OnSetHelperSearchType(type);
                        SfMs(1343, ref sSfOneTicks, t0);
                        c[1330]++;
                    }
                    else c[1331]++;
                    c[1337] += sSfSubs.Count > 1 ? sSfSubs.Count - 1 : 0;
                    SfFirst("narrowed a refresh to one spawner");
                    return true;
                }
            }
            return false;
        }

        // one log line per world, the first time a refresh was answered without the fan-out
        static void SfFirst(string what)
        {
            if (sSfLogged) return;
            sSfLogged = true;
            VlLog("spawnfix", "spawnfix: " + what + "; " + sSfSubs.Count + " spawners have started (a full fan-out calls every one)");
        }

        public class SpawnFixHook
        {
            // ---- the door every refresh goes through ----
            public void FireHook(SpawnerBase.CollectionHelperSearchType type)
            {
                if (!ShortcutOn("spawnfix")) { FireOrig(type); return; }
                try { if (SfFire((CollectionHelperSingleton)(object)this, type)) return; }
                catch (ResetException) { throw; }
                catch (Exception) { sSfOnly = null; }  // any doubt: EA's full fan-out
                int[] c = PatchCounters();
                c[1332]++;
                sSfValid = false;  // stays false if a handler throws: the next call fans out again
                long t0 = DateTime.UtcNow.Ticks;
                FireOrig(type);
                SfMs(1338, ref sSfFullTicks, t0);
                sSfType = type;
                sSfActor = Sim.ActiveActor;
                sSfStamp = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                sSfDirty = false;
                sSfValid = true;
            }
            public void FireOrig(SpawnerBase.CollectionHelperSearchType type) { }  // receives CollectionHelperSingleton.FireEvent's original header

            // ---- who asks for a refresh on behalf of one spawner ----
            public void AddHook(GameObject obj)
            {
                if (ShortcutOn("spawnfix")) sSfOnly = (SpawnerBase)(object)this;
                try { AddOrig(obj); } finally { sSfOnly = null; }
            }
            public void AddOrig(GameObject obj) { }  // ObjectSpawner.AddObjectToSpawner

            public void RemoveHook(GameObject obj, bool updateSpawnAlarms)
            {
                if (ShortcutOn("spawnfix")) sSfOnly = (SpawnerBase)(object)this;
                try { RemoveOrig(obj, updateSpawnAlarms); } finally { sSfOnly = null; }
            }
            public void RemoveOrig(GameObject obj, bool updateSpawnAlarms) { }  // ObjectSpawner.RemoveObjectFromSpawner

            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                if (ShortcutOn("spawnfix")) sSfOnly = (SpawnerBase)(object)this;
                try { return ImportOrig(resKeyTable, objIdTable, reader); } finally { sSfOnly = null; }
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; }  // ObjectSpawner.ImportContent

            public void ActivateHook() { FishSpot(); try { ActivateOrig(); } finally { sSfOnly = null; } }
            public void ActivateOrig() { }  // FishingSpotData.ActivateSpot
            public void DeactivateHook() { FishSpot(); try { DeactivateOrig(); } finally { sSfOnly = null; } }
            public void DeactivateOrig() { }  // FishingSpotData.DeactivateSpot
            void FishSpot()
            {
                if (!ShortcutOn("spawnfix")) return;
                sSfOnly = ((FishingSpotData)(object)this).SpotWhoOwnsMe;  // null (not started): EA's full fan-out
                if (sSfOnly != null) PatchCounters()[1344]++;
            }

            // ---- who is subscribed to the event (tracked whatever the switch says, so it can be turned on at any time) ----
            public void StartHook()
            {
                StartOrig();
                try
                {
                    SfSync();
                    sSfSubs[(SpawnerBase)(object)this] = true;
                    sSfDirty = true;  // EA's next refresh would give it its effect: the next one fans out
                    PatchCounters()[1336]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { }  // bookkeeping only: EA runs unchanged
            }
            public void StartOrig() { }  // SpawnerBase.OnStartup

            public void DisposeHook()
            {
                try { sSfSubs.Remove((SpawnerBase)(object)this); }
                catch (ResetException) { throw; }
                catch (Exception) { }  // bookkeeping only: EA runs unchanged
                DisposeOrig();
            }
            public void DisposeOrig() { }  // SpawnerBase.Dispose

            // ---- spawn attempts (all three callers: the alarm, the vacation pre-spawn, the force call) ----
            public void TrySpawnHook()
            {
                bool fix = ShortcutOn("spawnfix"), off = ShortcutOn("spawnoffworld");
                if (!fix && !off) { TrySpawnOrig(); return; }
                int[] c = PatchCounters();
                c[1340]++;
                if (!((GameObject)(object)this).InWorld)
                {
                    c[1339]++;
                    if (off) { c[1341]++; return; }  // EA's UpdateSpawnAlarm (the caller's next step) keeps the alarm going
                }
                long t0 = DateTime.UtcNow.Ticks;
                try { TrySpawnOrig(); }
                finally { SfMs(1342, ref sSfSpawnTicks, t0); }
            }
            public void TrySpawnOrig() { }  // ObjectSpawner.TrySpawnObject
        }
    }
}
