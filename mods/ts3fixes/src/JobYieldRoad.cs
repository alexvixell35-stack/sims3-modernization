using System;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Port to the combo branch of upstream's jobyieldroad / jobyieldroadow (docs/800, upstream JobYieldRoad.cs, ON there since
    // the import of 2026-11-19); the combo branch predates it, and the 30-min heap-512 soaks s560d / s562a-c on Bridgeport show
    // what that costs (docs/689): ~470 sim-thread waits a run, 27-34 s, in ts3native's RunPlan for the sim's own route job,
    // the stall leaf World_FindPlaceOnRoadOffScreen. Only the road and traffic sites are ported (jobyieldspawn, jobyieldready,
    // jobyieldfp and turnjigskip are not on this branch). Default off here; same names, logic and household rule as upstream.
    //
    // jobyieldroad: before ScriptCore.World.FindPlaceOnRoadOffScreen (that IC plans inside the game) the asking task sleeps a
    // tick at a time while ts3native answers JOBBUSY (RouteBackoffHook.StepAside(), 10 s cap, MayYield guard), then runs the
    // original. Callers [verified, decompiled]: TrafficManager.UpdateTrafficTask.Simulate (ambient traffic) and
    // CarNpcManager.FindOffScreenPlaceOnRoad (taxis, NPC and service cars). Not taken: no proxy or a Sim proxy, food and ice
    // cream trucks, any car with an occupant that is not a townie (carpools carry household Sims).
    // jobyieldroadow (acts only with jobyieldroad; use both): a CarNpcWithNoDriver's LiteDriver task destroys its car when it
    // finds it out of the world, and EA never yields between CreateObjectOutOfWorld and PlaceAt (upstream: 294 of 297 slept cars
    // lost without it). So (1) an out-of-world CarNpcWithNoDriver gets no sleep at the IC; (2) the traffic task steps aside
    // before the car exists (CreateObjectOutOfWorld("CarTaxiLightweight") on UpdateTrafficTask's own task); (3) any other car
    // gone after a sleep gets "no place" (false) without the IC, which every caller's failure path handles.
    // Timing only, "better, not identical": the same call with the same arguments, a few ticks later.
    // Counters (upstream 1270-1274 / 1285-1291 renumbered): [4860] road asks for a taken car, [4861] slept, [4862] ms slept,
    // [4863] car gone after the sleep, [4864] left out: occupant not a townie, [4865] roadow (1) taken, [4866] (3) taken,
    // [4867] cars gone that were out of the world, [4868] pre-create asks, [4869] slept, [4870] ms slept, [4871] pre-create calls.
    public static partial class Fixes
    {
        // the household rule: household, selected, unknown-scope, destroyed or household-less Sims are not townies
        static bool YieldTownie(Sim s) { return s != null && IsNpcSim(s) && s.Household != null; }

        static void ApplyJobYieldRoad()
        {
            Patch(typeof(ScriptCore.World), "FindPlaceOnRoadOffScreen", typeof(RoadOffScreenHook), "FindHook", "FindOrig");
            Patch(typeof(GlobalFunctions), "CreateObjectOutOfWorld", new Type[] { typeof(string), typeof(ProductVersion) }, typeof(TrafficCreateHook), "CreateHook", "CreateOrig");
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

        public partial class RouteBackoffHook
        {
            // true = jobyieldroadow (3): the car was destroyed during our sleep, answer "no place" without the IC
            internal static bool RoadStepAside(IScriptProxy p)
            {
                CarNpc car = p != null ? p.Target as CarNpc : null;
                if (car == null || car is FoodTruckBase) return false;
                int[] c = PatchCounters();
                Sim[] occ = car.Occupants;
                if (occ != null)
                    foreach (Sim s in occ)
                        if (s != null && !YieldTownie(s)) { c[4864]++; return false; }
                bool ow = ShortcutOn("jobyieldroadow"), wasOut = !car.InWorld;
                if (ow && wasOut && car is CarNpcWithNoDriver) { c[4865]++; return false; }  // (1): its LiteDriver destroys it if we yield
                c[4860]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();  // may sleep: outside every catch, a task reset passes through as in EA's own waits
                if (c[248] == n0) return false;
                c[4861]++;
                c[4862] += unchecked(System.Environment.TickCount - t0);
                if (!car.HasBeenDestroyed) return false;
                c[4863]++;
                if (wasOut) c[4867]++;
                if (!ow) return false;
                c[4866]++;
                return true;
            }

            // jobyieldroadow (2): only on the traffic manager's own task. Simulator.GetProxy hands back the ScriptProxy that
            // ScriptCore.Simulator.AddObject wraps every IScriptLogic in, so the task is the proxy's Target.
            internal static void TrafficStepAside()
            {
                int[] c = PatchCounters();
                c[4871]++;
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                if (p == null || !(p.Target is TrafficManager.UpdateTrafficTask)) return;
                c[4868]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();
                if (c[248] == n0) return;
                c[4869]++;
                c[4870] += unchecked(System.Environment.TickCount - t0);
            }
        }
    }
}
