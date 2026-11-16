using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Step-asides B (docs/803; draft, needs test). Same mechanism as docs/183 and docs/800: the asking task sleeps a tick at a
    // time while ts3native answers JOBBUSY (StepAside: 10 s cap, MayYield guard) so a plan or footprint flush that would wait for
    // a running route job finds none. All switches need jobyield; default off.
    //
    // jobyieldfpend: before RoutingComponent.UpdateDynamicFootprint(owner, true). The biggest "disc" wait group on Sunset Valley
    // (and the boat one on Isla Paradiso) is DynamicSimFootprint.set_Enabled < (Boat)RoutingComponent.EnableDynamicFootprint <
    // UpdateDynamicFootprint: the footprint switch-on waits for a running job (a forced discourage drain). Callers with bEnable true
    // [verified in the decompiled script DLL]: the end of RoutingComponent.DoRoute (after the route actions ran, the Sim or its
    // boat stands at the route end; what follows runs after our sleep exactly as before; the footprint stays off during the sleep,
    // as during the whole route; the call sits in DoRoute's try body, so a reset during the sleep lands in EA's own catch) and
    // BoatFollowSubPathRouteAction.PerformAction (a route action inside DoRoute, where EA itself yields all the time). Not taken:
    // bEnable false (OnReset, no yield allowed there), BoatTrip's and BoatTaxi's switch-offs, the route start.
    // Scope (StepTownie): a Sim owner and every Sim its posture routes with it (Posture.HasSlaveRouteObjects, the list
    // UpdateDynamicFootprint switches); a boat owner and all its occupants (no occupant: no step-aside). A household, selected or
    // unknown-scope Sim in that group keeps EA's call. Counters: [3427] townie asks, [3428] slept, [3429] ms slept.
    //
    // jobyieldpark: the same step-aside before ParkingSpace.ParkVehicle(IUsesParkingSpace, bool), whose ParentToSlot waits for a
    // running job. EA's method: if GetParkedVehicle() is null: FadeOut (no yield), mReservedVehicle = vehicle, ParentToSlot,
    // PushSimsFromFootprint, a FadeCarIn alarm. Callers: Vehicle.TryParkCarInSpace (from CarOwnable.Park and
    // OnReservingSimDisposed), ParkingSpace.TryParkVehicle (the buy-a-car UI), food / ice cream trucks' one-shot function.
    // Parking state after a sleep: before sleeping we store mReservedVehicle = vehicle, the value EA's own call stores before
    // ParentToSlot, so the space looks reserved and GetClosestUnreservedParkingSpace skips it. The only writer that ignores the
    // reservation is TryParkVehicle (buy-a-car UI, active lot only): left out by taking no space on the active household's lot or
    // on a lot where an active household Sim is. After the sleep EA's ParkVehicle runs with the caller's arguments. If the car or
    // the space was destroyed meanwhile, EA's call is skipped and the reservation put back; if something got parked anyway, the
    // reservation goes back and EA's call does nothing, as it does for a taken space; a reset during the sleep puts the reservation
    // back (finally). Taken only for a townie on its own task arriving home (PkTownie: the current task's Sim reserved this car,
    // stands on the space's lot, which is its LotHome; Sim.InternalOnSetLotCurrent on the stack, no Dispose before it), every
    // occupant a townie, not a CarOwnable of the active household's lot, not a food or ice cream truck, and a space whose
    // ReservedVehicle is null or this car; all asked only while JOBBUSY says a job runs (no job: EA's call at once, no stack walk).
    // Counters: [3430] asks, [3431] slept, [3432] ms slept, [3433] left out (household actor / occupant / lot / car), [3434] car or
    // space gone after the sleep (EA's call skipped), [3435] space taken after the sleep (EA's call does nothing).
    // Cleanup: no loop of our own (StepAside's is capped), no retry; the reservation is restored on every early exit.
    public static partial class Fixes
    {
        // the household rule: household, selected, unknown-scope, destroyed or household-less Sims are not townies
        static bool StepTownie(Sim s) { return s != null && IsNpcSim(s) && s.Household != null; }

        static void ApplyJobYieldPark()
        {
            Patch(typeof(Sims3.Gameplay.ObjectComponents.RoutingComponent), "UpdateDynamicFootprint", new Type[] { typeof(GameObject), typeof(bool) }, typeof(FpEndHook), "UpdateHook", "UpdateOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Vehicles.ParkingSpace), "ParkVehicle", new Type[] { typeof(Sims3.Gameplay.Interfaces.IUsesParkingSpace), typeof(bool) }, typeof(ParkSpaceHook), "ParkHook", "ParkOrig");
        }

        public class FpEndHook
        {
            public void UpdateHook(GameObject owner, bool bEnable)
            {
                if (bEnable && owner != null && ShortcutOn("jobyieldfpend")) RouteBackoffHook.FpEndStepAside(owner);  // may sleep: outside every try
                UpdateOrig(owner, bEnable);
            }
            public void UpdateOrig(GameObject owner, bool bEnable) { } // receives RoutingComponent.UpdateDynamicFootprint(GameObject, bool)'s original header
        }

        public class ParkSpaceHook
        {
            public void ParkHook(IUsesParkingSpace vehicle, bool doFade)
            {
                if (ShortcutOn("jobyieldpark") && !RouteBackoffHook.ParkStepAside((ParkingSpace)(object)this, vehicle)) return;
                ParkOrig(vehicle, doFade);
            }
            public void ParkOrig(IUsesParkingSpace vehicle, bool doFade) { } // receives ParkingSpace.ParkVehicle(IUsesParkingSpace, bool)'s original header
        }

        public partial class RouteBackoffHook
        {
            internal static void FpEndStepAside(GameObject owner)
            {
                if (!FpEndTownie(owner)) return;
                int[] c = PatchCounters();
                c[3427]++;
                int n0 = c[248], t0 = System.Environment.TickCount;
                StepAside();  // may sleep: outside every catch, a task reset passes through as in EA's own waits
                if (c[248] == n0) return;
                c[3428]++;
                c[3429] += unchecked(System.Environment.TickCount - t0);
            }

            static bool FpEndTownie(GameObject owner)
            {
                Sim s = owner as Sim;
                if (s != null)
                {
                    if (!StepTownie(s)) return false;
                    List<GameObject> slaves;
                    bool route;
                    if (s.Posture != null && s.Posture.HasSlaveRouteObjects(out slaves, out route) && route && slaves != null)
                        foreach (GameObject g in slaves)
                        {
                            Sim t = g as Sim;
                            if (t != null && !StepTownie(t)) return false;
                        }
                    return true;
                }
                Vehicle v = owner as Vehicle;
                Sim[] occ = v != null ? v.Occupants : null;
                if (occ == null || occ.Length == 0) return false;
                foreach (Sim o in occ) if (o == null || !StepTownie(o)) return false;
                return true;
            }

            static readonly FieldInfo sPkReserved = typeof(ParkingSpace).GetField("mReservedVehicle", BindingFlags.Instance | BindingFlags.NonPublic);

            // false = skip EA's ParkVehicle (the car or the space is gone after our sleep)
            internal static bool ParkStepAside(ParkingSpace ps, IUsesParkingSpace vehicle)
            {
                Vehicle v = vehicle as Vehicle;
                if (v == null || v is FoodTruckBase || sPkReserved == null || ps.GetParkedVehicle() != null) return true;
                IUsesParkingSpace prev = ps.ReservedVehicle;
                if (prev != null && prev != vehicle) return true;
                // no job, nothing to wait for: EA's call as is (and no stack walk); StepAside asks again below
                if (!ShortcutOn("jobyield") || !MayYield() || !sYieldProbe.GetOption2(kJobBusy)) return true;
                int[] c = PatchCounters();
                if (!PkTownie(ps, v)) { c[3433]++; return true; }
                c[3430]++;
                sPkReserved.SetValue(ps, vehicle);  // EA's own next store, made before the sleep (see the header)
                int n0 = c[248], t0 = System.Environment.TickCount;
                bool done = false;
                try { StepAside(); done = true; }  // may sleep; a reset passes through (no catch)
                finally { if (!done && ps.ReservedVehicle == vehicle) sPkReserved.SetValue(ps, prev); }
                if (c[248] == n0) return true;
                c[3431]++;
                c[3432] += unchecked(System.Environment.TickCount - t0);
                if (v.HasBeenDestroyed || ps.HasBeenDestroyed)
                {
                    c[3434]++;
                    if (ps.ReservedVehicle == vehicle) sPkReserved.SetValue(ps, prev);
                    return false;
                }
                if (ps.GetParkedVehicle() != null)
                {
                    c[3435]++;
                    if (ps.ReservedVehicle == vehicle) sPkReserved.SetValue(ps, prev);
                }
                return true;
            }

            static bool PkTownie(ParkingSpace ps, Vehicle v)
            {
                IScriptProxy p = Simulator.GetProxy(Simulator.CurrentTask);
                Sim actor = p != null ? p.Target as Sim : null;
                if (actor == null || !StepTownie(actor)) return false;
                Sim[] occ = v.Occupants;
                if (occ != null) foreach (Sim o in occ) if (o != null && !StepTownie(o)) return false;
                Lot lot = ps.LotCurrent, home = Household.ActiveHouseholdLot;
                if (lot == null || lot == home) return false;
                CarOwnable co = v as CarOwnable;
                if (co != null && home != null && co.LotHome == home) return false;
                Household hh = Household.ActiveHousehold;
                if (hh != null && hh.AllActors != null)
                    foreach (Sim h in hh.AllActors) if (h != null && h.LotCurrent == lot) return false;
                // the one proven caller shape: Sim.InternalOnSetLotCurrent's arrival home (mLotCurrent == LotHome, ReservedVehicle not
                // in the inventory, not on the lot, not in use -> Park(mLotCurrent, this) -> TryParkCarInSpace; SetReservedVehicle(null)
                // follows). The Sim of the current task is that Sim. Not taken: Sim.Dispose -> OnReservingSimDisposed (a Sim being
                // torn down), the buy-a-car UI, the trucks' one-shot functions.
                if (actor.GetReservedVehicle() != v || actor.LotHome != lot || actor.LotCurrent != lot) return false;
                return PkFromArrival();
            }

            // the stack check runs only here (every cheaper gate passed): a frame Sim.InternalOnSetLotCurrent within 12 frames, and no
            // Dispose / OnReservingSimDisposed frame before it
            static bool PkFromArrival()
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0; i < st.FrameCount && i < 12; i++)
                {
                    MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    if (m.Name == "Dispose" || m.Name == "OnReservingSimDisposed") return false;
                    if (m.Name == "InternalOnSetLotCurrent") return m.DeclaringType == typeof(Sim);
                }
                return false;
            }
        }
    }
}
