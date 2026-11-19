using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Step-asides (docs/183 JobYield, docs/800) at five more places where the game runs a route plan, or flushes the footprint
    // queue, on the sim thread while an async route job runs. Each one waits for the job at ts3native's RunPlan / disc sites
    // with the whole sim thread stopped. The hook lets the asking task sleep a tick at a time until the job is done
    // (StepAside: same JOBBUSY ask, same 10 s cap, same MayYield guard), then runs the original as before. Every switch needs
    // jobyield (default on) and a ts3native with [RouteAsync] JobYield; without it the game answers "not busy" and nothing changes.
    //
    // jobyieldroad: before ScriptCore.World.FindPlaceOnRoadOffScreen. That IC plans inside the game. Callers [verified in the
    // decompiled script DLL]: TrafficManager.UpdateTrafficTask.Simulate (ambient traffic spawn, a ScriptTask loop that already
    // sleeps between spawns) and CarNpcManager.FindOffScreenPlaceOnRoad < DriveAwayInNpcCar / InitCarForLotRoute /
    // DriveToWorldLotPositionInServiceCar (each already waits and plans a car route right after). Not taken: callers with no
    // proxy or a Sim proxy, food and ice cream trucks, and any car with an occupant that is not a townie (carpools leaving the
    // home lot carry household Sims). A car destroyed during our sleep is counted and still gets the original IC (or "no place"
    // with jobyieldroadow, below).
    //
    // jobyieldroadow (acts only with jobyieldroad; use both or neither): alone, jobyieldroad lost cars (294 of 297 slept cars
    // were gone after the sleep, Bridgeport). CarNpcWithNoDriver.OnStartup (run inside CreateObjectOutOfWorld) adds a LiteDriver
    // task whose Simulate starts with "if (!mCar.InWorld) { mCar.Destroy(); return; }". EA never yields between
    // CreateObjectOutOfWorld and PlaceAt, so the task always finds the car placed; a sleep lets it run first and destroy the
    // out-of-world car. EA then goes on with the dead car (the traffic class count is raised after Dispose and never taken back).
    // With the switch: (1) a CarNpcWithNoDriver out of the world gets no sleep at the IC; (2) the traffic task's step-aside moves
    // before the car exists (CreateObjectOutOfWorld("CarTaxiLightweight"), only on UpdateTrafficTask's own task: nothing exists yet
    // for another task to touch, and the IC after it then finds the job done); (3) any other car gone after a sleep gets "no
    // place" (false) without the IC, which every caller's own failure path handles.
    //
    // jobyieldspawn: at the start of ObjectSpawner.TryPlaceSpawnedObjectInWorld (the 3-argument form; the 1-argument one calls
    // it). AddToWorld queues the new object's lot-routing record while a job runs and the first SetPosition must flush it, which
    // waits for the job. The callers are the spawners' SpawnObject overrides, run by TrySpawnObject from TriggerSpawn (an alarm,
    // its own task) or ForceAnUnscheduledSpawnAttempt; the object is new and out of the world, nobody else holds it, so a later
    // placement changes only when it appears. After a sleep: the spawner destroyed -> the object is destroyed and retObj = null
    // (EA's own failure answer, every caller tests it); the object destroyed -> retObj = null.
    //
    // jobyieldfp: before a top-level Terrain.CanTurnInPlace / ModifyRouteForSpecies of a townie (a boat or animal that cannot
    // turn in place plans a test jig and switches footprints, each of which can wait for a running job). Callers: after a failed
    // route in the Sim's own task (DoRouteFailureBehavior) and before a route is followed (OnAboutToStartFollowingRoute); the Sim
    // still stands, so a sleep only holds it a few more ticks. Household, selected and unknown-scope Sims are left out
    // (YieldTownie): they take no step-aside here.
    //
    // jobyieldready (RouteBackoff.cs AsyncWait): no READY poll while JOBBUSY says a job runs; see there.
    //
    // MayYield keeps the native wait in native callbacks and non-yielding contexts (alarms). Counters: [1270] road asks for a taken
    // car, [1271] slept, [1272] ms slept, [1273] car gone after the sleep, [1274] left out: occupant not a townie; [1275] READY
    // polls skipped, [1276] waits with one; [1277] fp asks, [1278] slept, [1279] ms slept; [1280] spawn asks, [1281] slept,
    // [1282] ms slept, [1283] spawner gone, [1284] object gone; [1285] roadow (1) taken, [1286] (3) taken, [1287] cars gone that were out
    // of the world, [1288] pre-create asks, [1289] slept, [1290] ms slept, [1291] pre-create calls. Cleanup: no loop or retry of
    // our own (StepAside's is capped), each original runs once with the caller's arguments.
    public static partial class Fixes
    {
        // the household rule: household, selected, unknown-scope, destroyed or household-less Sims are not townies
        static bool YieldTownie(Sim s) { return s != null && IsNpcSim(s) && s.Household != null; }

        static void ApplyJobYieldRoad()
        {
            Patch(typeof(ScriptCore.World), "FindPlaceOnRoadOffScreen", typeof(RoadOffScreenHook), "FindHook", "FindOrig");
            Patch(typeof(GlobalFunctions), "CreateObjectOutOfWorld", new Type[] { typeof(string), typeof(ProductVersion) }, typeof(TrafficCreateHook), "CreateHook", "CreateOrig");
            Patch(typeof(Sims3.Gameplay.Abstracts.ObjectSpawner), "TryPlaceSpawnedObjectInWorld",
                new Type[] { typeof(IGameObject).MakeByRefType(), typeof(bool), typeof(FindGoodLocationBooleans) },
                typeof(SpawnPlaceHook), "PlaceHook", "PlaceOrig");
            Patch(typeof(Terrain), "CanTurnInPlace", typeof(FpYieldHook), "CanTurnHook", "CanTurnOrig");
            Patch(typeof(Terrain), "ModifyRouteForSpecies", typeof(FpYieldHook), "ModHook", "ModOrig");
        }

        public class RoadOffScreenHook
        {
            public bool FindHook(IScriptProxy scriptHandle, Vector3 pos, FindPlaceOnRoadOption option, float distance, ref float[] posArray, ref float[] orientArray)
            {
                if (ShortcutOn("jobyieldroad") && RouteBackoffHook.RoadStepAside(scriptHandle)) return false;  // jobyieldroadow (3): the car is gone
                return FindOrig(scriptHandle, pos, option, distance, ref posArray, ref orientArray);
            }
            public bool FindOrig(IScriptProxy scriptHandle, Vector3 pos, FindPlaceOnRoadOption option, float distance, ref float[] posArray, ref float[] orientArray) { return false; } // receives ScriptCore.World.FindPlaceOnRoadOffScreen's original header
        }

        // jobyieldroadow (2): the traffic spawn's step-aside before the car exists
        public class TrafficCreateHook
        {
            public static IGameObject CreateHook(string instanceName, ProductVersion productVersion)
            {
                if (instanceName == "CarTaxiLightweight" && ShortcutOn("jobyieldroad") && ShortcutOn("jobyieldroadow")) RouteBackoffHook.TrafficStepAside();
                return CreateOrig(instanceName, productVersion);
            }
            public static IGameObject CreateOrig(string instanceName, ProductVersion productVersion) { return null; } // receives GlobalFunctions.CreateObjectOutOfWorld(string, ProductVersion)'s original header
        }

        public abstract class SpawnPlaceHook : Sims3.Gameplay.Abstracts.ObjectSpawner
        {
            public void PlaceHook(ref IGameObject retObj, bool deleteObjOnFailure, FindGoodLocationBooleans booleanConstraints)
            {
                if (ShortcutOn("jobyieldspawn") && retObj != null && RouteBackoffHook.SpawnStepAside())
                {
                    int[] c = PatchCounters();
                    GameObject o = retObj as GameObject;
                    if (HasBeenDestroyed)
                    {
                        c[1283]++;
                        if (o == null || !o.HasBeenDestroyed) retObj.Destroy();
                        retObj = null;
                        return;
                    }
                    if (o != null && o.HasBeenDestroyed) { c[1284]++; retObj = null; return; }
                }
                PlaceOrig(ref retObj, deleteObjOnFailure, booleanConstraints);
            }
            public void PlaceOrig(ref IGameObject retObj, bool deleteObjOnFailure, FindGoodLocationBooleans booleanConstraints) { } // receives ObjectSpawner.TryPlaceSpawnedObjectInWorld(ref IGameObject, bool, FindGoodLocationBooleans)'s original header
        }

        public class FpYieldHook
        {
            static int sDepth;  // CanTurnInPlace runs inside ModifyRouteForSpecies: only the outer call asks

            public static bool CanTurnHook(Route r, Sim routingSim)
            {
                if (sDepth == 0) RouteBackoffHook.FpStepAside(routingSim);  // may sleep: outside every try, a task reset passes through as in EA's own waits
                sDepth++;
                try { return CanTurnOrig(r, routingSim); }
                finally { sDepth--; }
            }
            public static bool CanTurnOrig(Route r, Sim routingSim) { return true; }  // receives Terrain.CanTurnInPlace's original header

            public static void ModHook(Route r, Sim routingSim)
            {
                if (sDepth == 0) RouteBackoffHook.FpStepAside(routingSim);
                sDepth++;
                try { ModOrig(r, routingSim); }
                finally { sDepth--; }
            }
            public static void ModOrig(Route r, Sim routingSim) { }  // receives Terrain.ModifyRouteForSpecies's original header
        }

        public partial class RouteBackoffHook
        {
            // true = slept (StepAside ran at least one tick)
            internal static bool SpawnStepAside()
            {
                int[] c = PatchCounters();
                c[1280]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();  // may sleep: outside every catch, a task reset passes through as in EA's own waits
                if (c[248] == n0) return false;
                c[1281]++;
                c[1282] += unchecked(System.Environment.TickCount - t0);
                return true;
            }

            // jobyieldfp: before a top-level turn-in-place check of a boat or animal; townies only
            internal static void FpStepAside(Sim s)
            {
                if (!ShortcutOn("jobyieldfp") || !YieldTownie(s)) return;
                int[] c = PatchCounters();
                c[1277]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();
                if (c[248] == n0) return;
                c[1278]++;
                c[1279] += unchecked(System.Environment.TickCount - t0);
            }

            // true = jobyieldroadow (3): the car was destroyed during our sleep, answer "no place" without the IC
            internal static bool RoadStepAside(IScriptProxy p)
            {
                CarNpc car = p != null ? p.Target as CarNpc : null;
                if (car == null || car is FoodTruckBase) return false;
                int[] c = PatchCounters();
                Sim[] occ = car.Occupants;
                if (occ != null)
                    foreach (Sim s in occ)
                        if (s != null && !YieldTownie(s)) { c[1274]++; return false; }
                bool ow = ShortcutOn("jobyieldroadow"), wasOut = !car.InWorld;
                if (ow && wasOut && car is CarNpcWithNoDriver) { c[1285]++; return false; }  // (1): its LiteDriver destroys it if we yield
                c[1270]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();
                if (c[248] == n0) return false;
                c[1271]++;
                c[1272] += unchecked(System.Environment.TickCount - t0);
                if (!car.HasBeenDestroyed) return false;
                c[1273]++;
                if (wasOut) c[1287]++;
                if (!ow) return false;
                c[1286]++;
                return true;
            }

            // jobyieldroadow (2): only on the traffic manager's own task (other callers of that name, if a mod has one, keep EA's).
            // Simulator.GetTask hands back the ScriptProxy that ScriptCore.Simulator.AddObject wraps every IScriptLogic in, so
            // the task is the proxy's Target.
            internal static void TrafficStepAside()
            {
                int[] c = PatchCounters();
                c[1291]++;
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                if (p == null || !(p.Target is TrafficManager.UpdateTrafficTask)) return;
                c[1288]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();
                if (c[248] == n0) return;
                c[1289]++;
                c[1290] += unchecked(System.Environment.TickCount - t0);
            }
        }
    }
}
