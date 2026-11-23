using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // Spawner placement and map tag task fixes (docs/610, s79). Both default off. Household rule: spawners and map tags are
    // world and UI systems with no Sim in them; the end states are the ones EA itself produces, so there is no exemption.
    public static partial class Fixes
    {
        // ---- docs/610 §2 spawnerskip -----------------------------------------------------------------------------------------
        // [verified, ilspycmd ObjectSpawner] TryPlaceSpawnedObjectInWorld(ref obj, deleteObjOnFailure, constraints) adds the object
        // to the world, then runs up to kMaxSpawnAttempts + 1 = 11 PlaceAtGoodLocation searches from random start points in the
        // spawn square. When all fail it destroys the object (deleteObjOnFailure true: every logged failure, 70/70) or puts it at
        // the last random start point with a random facing (false). [verified, ts3native.log spawnplace lines] outcomes are
        // bimodal: 459 of 536 logged placements found a spot on the first try, 70 failed all 11, 7 between; InsectSpawner20 did
        // 66 tries for 2 spots, at 20-70 ms each on the main thread. A spawner whose search keeps failing is stuck on its
        // surroundings (mPlacedOutside, walls, objects), which change only when the spawner, its lot or its outside flag does.
        // Fix: per spawner, object class, constraints and failure mode, after kSpawnerSkipFails full searches fail in a row,
        // the next spawns take EA's own failure ending at once (same calls: AddToWorld, then Destroy, or one random start point,
        // snapped to the floor, with a random facing); every kSpawnerSkipRetry-th spawn runs the full search again, and any
        // change of the spawner's position, lot, the lot's last build/buy time or its outside flag clears the streak.
        // ICauseEffectSpawner keeps EA's body always (its zero-diameter branch does not search). The hook also replaces the
        // alarmtime spawn timing (HitchCause.cs, docs/546 row 5): same spawnplace line.
        // Counters: [3424] spawns given EA's failure ending without the search, [3425] full searches run while a streak was
        // armed, [3426] streaks armed, [3427] errors (EA's body ran), [3428] PlaceAtGoodLocation searches run by spawner
        // placement, [3429] spawns placed by a search.
        static string sSpkMissing = "";
        static PropertyInfo sSpkDiameter;   // ObjectSpawner.SpawnDiameter (protected abstract; virtual getter)
        static bool sSpkIn;                 // inside EA's TryPlaceSpawnedObjectInWorld body
        static int sSpkTries, sSpkOk;        // that body's PlaceAtGoodLocation calls / successes
        static Dictionary<string, SpkState> sSpkState = new Dictionary<string, SpkState>();

        internal class SpkState
        {
            public int Fails, Since;          // full searches failed in a row; spawns skipped since the last full search
            public ulong Lot; public long BuildBuy; public float X, Z; public bool Outside;
        }

        static void ApplySpawnerSkip()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sSpkDiameter = typeof(ObjectSpawner).GetProperty("SpawnDiameter", inst);
            Patch(typeof(ObjectSpawner), "TryPlaceSpawnedObjectInWorld", new Type[] { typeof(IGameObject).MakeByRefType(), typeof(bool), typeof(FindGoodLocationBooleans) },
                typeof(SpkPlaceHook), "PlaceHook", "PlaceOrig");
            Patch(typeof(Sims3.Gameplay.GlobalFunctions), "PlaceAtGoodLocation", new Type[] { typeof(IGameObject), typeof(World.FindGoodLocationParams), typeof(bool) },
                typeof(SpkPlaceHook), "FglHook", "FglOrig");
            sSpkMissing = EpLive(null, 0, "ObjectSpawner.SpawnDiameter", sSpkDiameter, "get");
        }

        // Pure: skip the search for this spawn? fails/since from the streak, envSame = spawner, lot and outside flag unchanged.
        internal static bool SpkSkip(bool on, bool causeEffect, int fails, int since, bool envSame, int nFails, int retry)
        {
            if (!on || causeEffect || !envSame || nFails < 1 || fails < nFails) return false;
            return retry < 1 || since < retry - 1;   // retry 0: never search again until the surroundings change
        }

        // Pure: the streak after a full search. Returns true when this search armed the streak.
        internal static bool SpkAfter(SpkState st, bool found, int nFails)
        {
            st.Since = 0;
            if (found) { st.Fails = 0; return false; }
            if (st.Fails < 1000) st.Fails++;
            return st.Fails == nFails;
        }

        // The streak for this placement; cleared when the surroundings changed. null for cause-and-effect spawners.
        static SpkState SpkGet(ObjectSpawner sp, IGameObject obj, FindGoodLocationBooleans c, bool delete)
        {
            if (sp is ICauseEffectSpawner) return null;
            string key = sp.ObjectId.Value.ToString("X") + "|" + obj.GetType().FullName + "|" + (int)c + "|" + (delete ? "d" : "p");
            SpkState st;
            if (!sSpkState.TryGetValue(key, out st))
            {
                if (sSpkState.Count >= 4096) { sSpkState.Clear(); CapCount(kCSpkClears); }   // spawners of worlds left behind
                st = new SpkState();
                sSpkState[key] = st;
                CapHw(kCSpkHw, sSpkState.Count);  // docs/321 lane 0
            }
            Lot lot = sp.LotCurrent;
            ulong lotId = lot != null ? lot.LotId : 0;
            long bb = lot != null ? lot.TimeOfLastBuildBuy.Ticks : 0;
            Vector3 p = sp.Position;
            bool outside = sp.mPlacedOutside;
            if (st.Lot != lotId || st.BuildBuy != bb || st.X != p.x || st.Z != p.z || st.Outside != outside)
            {
                st.Fails = 0; st.Since = 0;
                st.Lot = lotId; st.BuildBuy = bb; st.X = p.x; st.Z = p.z; st.Outside = outside;
            }
            return st;
        }

        // EA's failure ending of TryPlaceSpawnedObjectInWorld, without the searches (non-cause-and-effect spawners: EA snaps
        // the start point to the floor). Destroy, or the last random start point with a random facing.
        static void SpkFallback(ObjectSpawner sp, ref IGameObject retObj, bool deleteObjOnFailure)
        {
            float half = (float)sSpkDiameter.GetValue(sp, null) * 0.5f;   // read first: a throw leaves the object untouched
            retObj.AddToWorld();
            if (deleteObjOnFailure)
            {
                retObj.Destroy();
                retObj = null;
                return;
            }
            Vector3 p = sp.Position;
            float x = RandomUtil.GetFloat(p.x - half, p.x + half);
            float z = RandomUtil.GetFloat(p.z - half, p.z + half);
            float y = World.SnapToFloor(new Vector3(x, p.y, z)).y;
            retObj.SetForward(RandomUtil.GetRandomDirXZ());
            retObj.SetPosition(new Vector3(x, y, z));
        }

        // same signature as the target: a header swap hands callers the hook's
        public class SpkPlaceHook
        {
            public void PlaceHook(ref IGameObject retObj, bool deleteObjOnFailure, FindGoodLocationBooleans booleanConstraints)
            {
                object self = this;
                ObjectSpawner sp = self as ObjectSpawner;
                if (sSpkIn || sp == null || retObj == null) { PlaceOrig(ref retObj, deleteObjOnFailure, booleanConstraints); return; }
                int[] c = PatchCounters();
                SpkState st = null;
                int nFails = Instantiator.kSpawnerSkipFails;
                if (ShortcutOn("spawnerskip"))
                {
                    try
                    {
                        st = SpkGet(sp, retObj, booleanConstraints, deleteObjOnFailure);
                        if (st != null && SpkSkip(true, false, st.Fails, st.Since, true, nFails, Instantiator.kSpawnerSkipRetry))
                        {
                            SpkFallback(sp, ref retObj, deleteObjOnFailure);
                            st.Since++;
                            c[3424]++;
                            return;
                        }
                    }
                    catch (ResetException) { throw; }
                    catch (Exception) { c[3427]++; st = null; }
                }
                bool timed = Instantiator.kHitchCauseHooks && ShortcutOn("alarmtime");   // as before: test packages only
                uint t0 = timed ? SrcNowUs() : 0;
                sSpkIn = true; sSpkTries = 0; sSpkOk = 0;
                try { PlaceOrig(ref retObj, deleteObjOnFailure, booleanConstraints); }
                finally
                {
                    sSpkIn = false;
                    c[3428] += sSpkTries;
                    if (sSpkOk > 0) c[3429]++;
                    if (st != null)
                    {
                        if (st.Fails >= nFails) c[3425]++;
                        if (SpkAfter(st, sSpkOk > 0, nFails)) c[3426]++;
                    }
                    if (timed)
                    {
                        uint us = unchecked(SrcNowUs() - t0);
                        if (us >= 10000)
                            HcLine("spawnplace: " + (us / 1000) + " ms, " + sp.GetType().Name + " " + HcName(sp) + ", fgl tries " + sSpkTries + ", ok " + sSpkOk
                                + ", obj " + (retObj != null ? retObj.GetType().Name : "null(deleted)") + ", " + HcSimTime());
                    }
                }
            }
            public void PlaceOrig(ref IGameObject retObj, bool deleteObjOnFailure, FindGoodLocationBooleans booleanConstraints) { } // receives ObjectSpawner.TryPlaceSpawnedObjectInWorld(ref, bool, constraints)

            public static bool FglHook(IGameObject obj, World.FindGoodLocationParams fglParams, bool deleteOnFailure)
            {
                bool r = FglOrig(obj, fglParams, deleteOnFailure);
                if (sSpkIn) { sSpkTries++; if (r) sSpkOk++; }
                return r;
            }
            public static bool FglOrig(IGameObject obj, World.FindGoodLocationParams fglParams, bool deleteOnFailure) { return false; } // receives GlobalFunctions.PlaceAtGoodLocation
        }

        // SpawnerSkipCheck run: the skip decision over a failing and a succeeding spawner (pure: searches and placements counted
        // per arm, with controls). Live: the swaps and the SpawnDiameter getter on a real spawner of the world (a live spawn would
        // change the world); the counters show real searches, placements and skips.
        public static object SpawnerSkipCheck(string what)
        {
            string counters = B582Counters("spawnerskip", 3424, new[] { "spawns ended without search", "armed full searches", "streaks armed", "errors",
                "spawner searches", "spawns placed by search" }, sSpkMissing);
            if (what == "result") return counters;
            if (what != "run") return "spawnerskip <run|result>";
            bool on = ShortcutOn("spawnerskip");
            const int nFails = 3, retry = 8, spawns = 40;
            int offSearch, offPlaced, onSearch, onPlaced;
            // a spawner whose search always fails (InsectSpawner20's shape): 11 searches per spawn in EA
            SpkSim(false, false, spawns, nFails, retry, out offSearch, out offPlaced);
            SpkSim(on, false, spawns, nFails, retry, out onSearch, out onPlaced);
            bool eaBug = offSearch == spawns * 11 && offPlaced == 0;
            // the end state is EA's either way (no spot found), with far fewer searches when on
            bool fixedPure = on && onPlaced == 0 && onSearch < offSearch / 4;
            // controls: a spawner whose search succeeds keeps every search and placement; cause-and-effect never skips;
            // a changed lot clears the streak; retry 1 always searches
            int a, b, a2, b2;
            SpkSim(false, true, spawns, nFails, retry, out a, out b);
            SpkSim(true, true, spawns, nFails, retry, out a2, out b2);
            bool controls = eaBug && a == a2 && b == b2 && b == spawns
                && !SpkSkip(true, true, 9, 0, true, nFails, retry) && !SpkSkip(true, false, 9, 0, false, nFails, retry)
                && !SpkSkip(true, false, 9, 0, true, nFails, 1) && SpkSkip(true, false, nFails, 0, true, nFails, retry)
                && !SpkSkip(true, false, nFails, retry - 1, true, nFails, retry);
            string live = "no spawner in the world (pure only)";
            bool liveOk = sSpkMissing.Length == 0;
            try
            {
                ObjectSpawner[] all = Sims3.Gameplay.Queries.GetObjects<ObjectSpawner>();
                if (all.Length > 0)
                {
                    float d = (float)sSpkDiameter.GetValue(all[0], null);
                    live = all.Length + " spawners, " + all[0].GetType().Name + " SpawnDiameter " + d.ToString("0.0") + ", streaks " + sSpkState.Count;
                    liveOk &= d >= 0f;
                }
            }
            catch (Exception e) { liveOk = false; live = "live read threw " + e.GetType().Name; }
            return B582Line("spawnerskip", on, "failing spawner: " + onSearch + " searches for 40 spawns (EA " + offSearch + "), placed " + onPlaced, fixedPure && liveOk,
                "EA runs 11 searches per failing spawn (" + eaBug + "), succeeding spawner unchanged (" + a2 + " searches, " + b2 + " placed), cause-and-effect and changed lots never skip, " + live,
                controls && liveOk, sSpkMissing, counters);
        }

        // Pure: spawns through the hook's decision; ok = every search finds a spot on its first try, else all 11 fail.
        internal static void SpkSim(bool on, bool ok, int spawns, int nFails, int retry, out int searches, out int placed)
        {
            searches = 0; placed = 0;
            SpkState st = new SpkState();
            for (int i = 0; i < spawns; i++)
            {
                if (SpkSkip(on, false, st.Fails, st.Since, true, nFails, retry)) { st.Since++; continue; }
                searches += ok ? 1 : 11;
                if (ok) placed++;
                SpkAfter(st, ok, nFails);
            }
        }

        // ---- docs/610 §3 maptagtaskalive -------------------------------------------------------------------------------------
        // [verified, ilspycmd -il UI.dll] MapTagController+MapTagVisibilityUpdate.Simulate walks the current tags 5 per tick.
        // After each in-pass Simulator.Sleep(0), when mbForceUpdate got set (OnMapTagAdded, OnMapTagRemoved, OnMapTagRefresh)
        // or mCurrentMapTags became null, it leaves to IL_029a, the method's ret; the loop top leaves the same way when
        // !mbActive (CASModeState sets MapTagsActive false, then true), the model is null or the list is null (the controller
        // fills it in its own Simulate, so a first pass can find it null). The task is made once, in the controller's ctor
        // (LiveModeState.LoadUI), so map tag show/hide, culling, the active-Sim tag auto-hide and collision runs stop until the
        // next UI load. MapTagController.Simulate itself leaves on !mbActive or a null model the same way, which stops
        // UpdateMapTagCamera / UpdateMapTags. EA's forced update means "start the pass again".
        // Fix: when EA's body returns on one of those exits (the condition still holds: nothing runs between EA's check and
        // its ret) and the controller is still the live one (Dispose clears Instance and the task field), sleep one tick and
        // run EA's body again. A ResetException (Dispose destroys the task) ends it as before; a return with no exit
        // condition is EA's catch of a reset and ends it too.
        // Counters: [3430] visibility task restarts, [3431] visibility task ends EA's way with the switch off, [3432]
        // controller restarts, [3433] controller ends with the switch off, [3434] errors (the task ended as EA's).
        static string sMtkMissing = "";
        static Type sMtkVisType;
        static FieldInfo sMtkCtl, sMtkTask, sMtkActive, sMtkList, sMtkForce;
        static object sMtkVisIn;   // the visibility task now running under the hook (null: none)

        static void ApplyMapTagTaskAlive()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type ctl = typeof(MapTagController);
            sMtkVisType = ctl.GetNestedType("MapTagVisibilityUpdate", BindingFlags.NonPublic);
            sMtkCtl = sMtkVisType != null ? sMtkVisType.GetField("mController", inst) : null;
            sMtkTask = ctl.GetField("mVisibilityUpdateTask", inst);
            sMtkActive = ctl.GetField("mbActive", inst);
            sMtkList = ctl.GetField("mCurrentMapTags", inst);
            sMtkForce = ctl.GetField("mbForceUpdate", inst);
            if (sMtkVisType != null) Patch(sMtkVisType, "Simulate", Type.EmptyTypes, typeof(MtkVisHook), "SimHook", "SimOrig");
            Patch(typeof(MapTagController), "Simulate", Type.EmptyTypes, typeof(MtkCtlHook), "SimHook", "SimOrig");
            sMtkMissing = EpJoin(sMtkVisType == null ? "MapTagController+MapTagVisibilityUpdate" : "",
                EpLive(null, 0, "mController", sMtkCtl, null, "mVisibilityUpdateTask", sMtkTask, null, "mbActive", sMtkActive, typeof(bool),
                    "mCurrentMapTags", sMtkList, null, "mbForceUpdate", sMtkForce, typeof(bool)));
        }

        // Pure: run EA's body again? exitHeld = one of EA's exit conditions holds at its return; live = the controller and task
        // are still the ones in use.
        internal static bool MtkKeep(bool on, bool live, bool exitHeld) { return on && live && exitHeld; }

        static bool MtkModelNull() { return Responder.Instance == null || Responder.Instance.MapTagsModel == null; }

        static bool MtkAfterVis(object task)
        {
            try
            {
                MapTagController ctl = sMtkCtl.GetValue(task) as MapTagController;
                bool live = ctl != null && ReferenceEquals(MapTagController.Instance, ctl) && ReferenceEquals(sMtkTask.GetValue(ctl), task);
                bool held = ctl != null && (!(bool)sMtkActive.GetValue(ctl) || MtkModelNull() || sMtkList.GetValue(ctl) == null || (bool)sMtkForce.GetValue(ctl));
                bool on = ShortcutOn("maptagtaskalive");
                if (live && held) PatchCounters()[on ? 3430 : 3431]++;
                return MtkKeep(on, live, held);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3434]++; return false; }
        }

        static bool MtkAfterCtl(object self)
        {
            try
            {
                MapTagController ctl = self as MapTagController;
                bool live = ctl != null && ReferenceEquals(MapTagController.Instance, ctl);
                bool held = ctl != null && (!(bool)sMtkActive.GetValue(ctl) || MtkModelNull());
                bool on = ShortcutOn("maptagtaskalive");
                if (live && held) PatchCounters()[on ? 3432 : 3433]++;
                return MtkKeep(on, live, held);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[3434]++; return false; }
        }

        // same signature as the target: a header swap hands callers the hook's
        public class MtkVisHook
        {
            public void SimHook()
            {
                object self = this;
                sMtkVisIn = self;
                try
                {
                    while (true)
                    {
                        SimOrig();
                        if (!MtkAfterVis(self)) return;
                        Simulator.Sleep(0u);   // a ResetException here (Dispose) ends the task, as in EA's body
                    }
                }
                finally { if (ReferenceEquals(sMtkVisIn, self)) sMtkVisIn = null; }
            }
            public void SimOrig() { } // receives MapTagController+MapTagVisibilityUpdate.Simulate
        }

        public class MtkCtlHook
        {
            public void SimHook()
            {
                object self = this;
                while (true)
                {
                    SimOrig();
                    if (!MtkAfterCtl(self)) return;
                    Simulator.Sleep(0u);
                }
            }
            public void SimOrig() { } // receives MapTagController.Simulate
        }

        // Pure model of the visibility task: tags walked 5 per tick, a forced update at tick forceTick (-1 none), the controller
        // inactive in [offFrom, offTo). restart = the fix. Returns true when the task ended; passes = full passes done.
        internal static bool MtkModel(int tags, int ticks, int forceTick, int offFrom, int offTo, bool restart, out int passes)
        {
            passes = 0;
            bool force = false;
            int t = 0;
            while (t < ticks)
            {
                bool exited = false;
                while (t < ticks && !exited)
                {
                    if (t >= offFrom && t < offTo) { exited = true; break; }   // loop top: !mbActive
                    for (int i = 0; i < tags && !exited; i++)
                    {
                        if (i % 5 != 0 || force) continue;
                        t++;                                   // in-pass Sleep(0)
                        if (t == forceTick) force = true;
                        if (force) exited = true;              // EA: leave
                    }
                    if (exited) break;
                    force = false;
                    passes++;
                    t++;                                       // end-of-pass Sleep(0)
                    if (t == forceTick) force = true;
                }
                if (!exited) return false;
                if (!restart) return true;
                t++;                                           // the hook's Sleep(0) before EA's body again
                if (t == forceTick) force = true;
            }
            return false;
        }

        // A visibility task EA already ended (before the switch went on) comes back only at the next UI load; the check's run
        // with the switch on starts a new one for the live controller, as its ctor does.
        static string MtkRevive(MapTagController ctl)
        {
            if (sMtkVisIn != null) return "running";
            ConstructorInfo ci = sMtkVisType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(MapTagController) }, null);
            Task t = (Task)ci.Invoke(new object[] { ctl });
            sMtkTask.SetValue(ctl, t);
            Simulator.AddObject(t);
            return "restarted";
        }

        static int sMtkProbeForce = -1;   // the live probe: 1 = forced update set by run, read back by result

        // MapTagTaskAliveCheck run: the model with a forced update mid-pass and an inactive-then-active toggle (pure, with
        // controls); live, with a controller: reports whether the visibility task runs, (on) restarts one EA ended, then sets
        // mbForceUpdate as a tag add does. result: counters plus whether the task still runs and a pass ran since (the flag
        // cleared at the end of a full pass).
        public static object MapTagTaskAliveCheck(string what)
        {
            string counters = B582Counters("maptagtaskalive", 3430, new[] { "visibility restarts", "visibility ends (switch off)", "controller restarts",
                "controller ends (switch off)", "errors" }, sMtkMissing);
            MapTagController ctl = MapTagController.Instance;
            if (what == "result")
            {
                string probe = "";
                try
                {
                    if (ctl != null && sMtkForce != null)
                        probe = "; live: visibility task " + (sMtkVisIn != null ? "running" : "ended") + (sMtkProbeForce == 1 ? ", pass since run " + !(bool)sMtkForce.GetValue(ctl) : "");
                }
                catch (Exception e) { probe = "; live read threw " + e.GetType().Name; }
                return counters + probe;
            }
            if (what != "run") return "maptagtaskalive <run|result>";
            bool on = ShortcutOn("maptagtaskalive");
            int p0, p1, p2, p3, p4, p5;
            bool eaForceEnds = MtkModel(12, 40, 2, -1, -1, false, out p0);
            bool eaToggleEnds = MtkModel(12, 40, -1, 6, 9, false, out p1);
            bool onForceEnds = MtkModel(12, 40, 2, -1, -1, on, out p2);
            bool onToggleEnds = MtkModel(12, 40, -1, 6, 9, on, out p3);
            bool fixedPure = on && !onForceEnds && !onToggleEnds && p2 > p0 && p3 > p1;
            // controls: no forced update or toggle: same passes either way, never ends; a dead controller never restarts
            bool c0 = MtkModel(12, 40, -1, -1, -1, false, out p4), c1 = MtkModel(12, 40, -1, -1, -1, true, out p5);
            bool controls = eaForceEnds && eaToggleEnds && !c0 && !c1 && p4 == p5 && p4 > 0
                && !MtkKeep(true, false, true) && !MtkKeep(true, true, false) && !MtkKeep(false, true, true);
            string live = "no map tag controller (pure only)";
            bool liveOk = sMtkMissing.Length == 0;
            if (ctl != null && liveOk)
            {
                try
                {
                    string state = sMtkVisIn != null ? "running" : "ended";
                    if (on && sMtkVisIn == null) state += ", " + MtkRevive(ctl);
                    sMtkForce.SetValue(ctl, true);   // what OnMapTagAdded sets
                    sMtkProbeForce = 1;
                    live = "live: visibility task " + state + ", forced update set (see result)";
                    if (on) liveOk = sMtkVisIn != null || state.EndsWith("restarted");
                }
                catch (Exception e) { liveOk = false; live = "live probe threw " + e.GetType().Name; }
            }
            return B582Line("maptagtaskalive", on, "forced update and inactive toggle keep the task (passes " + p2 + "/" + p3 + ", EA " + p0 + "/" + p1 + ")", fixedPure && liveOk,
                "EA ends on both (" + eaForceEnds + "/" + eaToggleEnds + "), no event: same passes " + p4 + "/" + p5 + ", dead controller never restarts, " + live,
                controls && liveOk, sMtkMissing, counters);
        }
    }
}
