// unseentraffic + npccarexit (contributor, both default off). Drafted by the traffic agent (drafts_cars), wired and changed
// to the teleport-leg design here. Two hook bodies live in their old files, merged with what was there:
// UnseenThrottle.cs ParkingSpotHook.SpotHook (unseencars) and DisposeSpread.cs TrafficReleaseHook.ReleaseHook (disposespread).
// The selftest is tools/selftest/unseentraffic (UnseenTrafficCore.cs).
//
// ---------------------------------------------------------------------------------------------------------------------------
// unseentraffic. Ambient traffic = LightweightTaxi ("CarTaxiLightweight", EP3), the only CarNpcWithNoDriver subclass.
// TrafficManager.UpdateTrafficTask [ld-SGS.cs 931840-931925] spawns one per lot class up to its cap, only at its check
// (kTrafficCheckTime 0.5-1 sim h): the car is placed with PlaceAt(road point, spot.at) and gets DriveAroundFor(0.5-1 h)
// [931908-931909]. Its LiteDriver task [931678-931747] loops: random commercial lot -> StreetParking.GetParkingSpotForCar ->
// if the car is over 10 m from the spot, plan a car route and DoRoute -> free the spot -> sleep 5-10 sim min
// (kCarWaitingTimeRange). When the DriveAroundFor alarm sets mShouldRouteOff the loop ends in DriveOffscreenAndRelease =
// CarNpcManager.DriveAwayInNpcCar (wait, off-screen road point, one more car plan) + Release(fadeOut) + Destroy. Dispose
// takes the car out of the per-class count. Each plan costs 0.4-1.1 s of async route time in BP (jk2 routetrace).
//
// With the switch, (A) teleport leg at the spot ask (ParkingSpotHook, LiteDriver's own call only, spot granted): when the car,
// the granted spot and the straight corridor between them (a sample every 50 m) are all unseen, nobody (Sim or other
// vehicle) is within 4 m of the spot and the car is not leaving, the car is placed at the spot with Car.PlaceAt(spot.pos,
// spot.at) - the call EA's own traffic spawn uses for this car class [931908]; Vehicle.PlaceVehicleAt [379208-379249] is
// SetPosition + SetForward + UpdateHeadlights for an in-world car, Car.PlaceAt adds IdleAudio(true) [412357], the state
// CarRoutingComponent.DoRoute leaves at the end of a drive [460225]. The LiteDriver task then sleeps the leg's drive time
// (road length estimate / Vehicle.CarSpeed, UnseenTrafficCore.LegMinutes) with the spot still reserved, as during EA's
// DoRoute, and answers "spot granted". EA's loop goes on with its own arrival steps: the car is within 10 m of the spot, so
// no plan, FreeParkingSpotForCar, sleep 5-10 min, next leg. So the car keeps its whole 0.5-1 h life and its legs around
// town; the per-class count and TrafficManager's respawn rate are EA's. The old spot is left at once (as when EA's drive
// starts), so the hold never parks two cars on one spot.
// Why this is not the dropped docs/04 F10 shortcut (that one answered Route.PlanToPoint for the car inside a route): here no
// route exists yet, the car has no driver, passengers or assignment state, and EA's own loop does the arrival.
// A leg whose car, spot or corridor is seen plans and drives as EA's, so pass-bys into view along that line are kept.
// (B) drive-off (TrafficReleaseHook): a car still unseen there skips DriveAwayInNpcCar's wait and plan: the parking spot is
// freed as DriveAwayInNpcCar's finally does [372779-372835], then EA's Release(fadeOut: true) and Destroy (or
// DisposeSpreadDefer with disposespread). A seen car drives off as EA does. This retires the car only minutes earlier than
// EA's drive-off (after its full life); TrafficManager checks every 0.5-1 h, so the live count it sees is EA's.
// Seen = FarFromViewers false (camera reach or within 80 m of an active-household Sim) or World.IsObjectOnScreen(car).
//
// Exemptions (who can reach these two hooks):
//   CarNpcWithNoDriver/LightweightTaxi on LiteDriver : affected. No code path seats a Sim in one; an occupant anyway (any
//                                                      non-null Occupants entry) exempts the car.
//   CarNpcWithNpcDriver (carpool, taxi, school bus), service cars, food / ice-cream trucks, household cars (CarOwnable):
//                                                      not affected (other classes; every caller of both hooks passes a CarNpc).
//   TrafficManager's spawn query                     : not affected (not on a LiteDriver task).
//
// ---------------------------------------------------------------------------------------------------------------------------
// npccarexit (the traffic agent's design, unchanged). Sunset Valley: "ia ReleaseNpcCar" plans fail
// FailedBecauseNoPathToDestination 79 times in 4 games, all to the same goal from the household street 164.5 m away. Path:
// CarNpcWithNpcDriver.Release -> ReleaseNpcCar -> DriveOffscreenAndRelease(lot) -> DriveAwayInNpcCar(car, Driver, lot)
// [372779]. The driver is an NpcDriversManager Sim made by Service.CreateSimDescription(null, ...), so it gets no virtual home
// and the exit is FindOffScreenPlaceOnRoad(car, car.Position, 100 m) [372819] -> posResults[0] of
// World.FindPlaceOnRoadOffScreen [372844-372853]. Same start + same camera -> the same first answer, a road point no car route
// from that street reaches. EA plans to it anyway, never checks the result, and releases the car where it stands.
// With the switch: the drive-away ask (the only caller passing car.Position and 100 m) marks the car's exit as pending; a car
// plan from it that fails NoPath to that goal teaches the memo (start 8 m cell + goal 1 m cell). On a later ask whose first
// answer is in the memo, the other candidates of the same IC call are tried, then the IC again at kNpcExitFarMetres; the
// first candidate not in the memo is used. With none, "no place" is answered: EA's own branch for that, which skips only the
// doomed plan. Exempt: any household or selectable occupant, FoodTruckBase. Memo cleared on world load and when full.
//
// Counters 3800-3829:
//   [3800] unseentraffic: LiteDriver spot asks with the switch on   [3801] teleport legs (car placed at the spot)
//   [3802] driven: car seen   [3803] driven: spot seen   [3804] driven: corridor seen   [3805] driven: occupant aboard
//   [3806] driven: a Sim or another vehicle within 4 m of the spot   [3807] drive-offs with the switch on
//   [3808] released in place (unseen)   [3809] driven off (seen)   [3810] driven off: car out of the world or occupant (EA)
//   [3811] the leg decision threw (EA drove)   [3812] car out of the world after the hold (spot freed, EA's no-spot path)
//   [3813] sim minutes held, summed   [3814] not taken: leaving, or already within EA's 10 m (EA does not drive either)
//   [3815] npccarexit: drive-away exit asks with the switch on   [3816] exempt: household/selectable aboard
//   [3817] exempt: food/ice-cream truck   [3818] first answer in the memo   [3819] another candidate of the same call used
//   [3820] the far re-ask used   [3821] "no place" answered   [3822] learned (NoPath to a pending exit)
//   [3823] memo entries now   [3824] memo or pending table cleared (full)   [3825] pending exit's plan failed otherwise
//   [3826] pending exit's plan succeeded (dropped)   [3827-3829] free
//
// Cleanup (functions read: LiteDriver.Simulate, CarNpcWithNoDriver whole, TrafficManager spawn loop + tunables, Vehicle.PlaceAt
// / PlaceVehicleAt, Car.PlaceAt, CarRoutingComponent, RoutingComponent.DoRoute head, CarNpcManager.DriveAwayInNpcCar +
// FindOffScreenPlaceOnRoad, ParkingSpotHook, TrafficReleaseHook, RoadOffScreenHook, RflPlan):
//  - EA, LiteDriver loop: bounded by the DriveAroundFor alarm, sleeps each pass. A world whose only commercial lot is the
//    car's own gives an empty list and the pick throws; the finally destroys the car (no loop). Left.
//  - EA, DriveAwayInNpcCar: the plan result is not checked before DoRoute. npccarexit removes the known-bad goal; the
//    unchecked result itself is left (DoRoute of a failed plan returns at once).
//  - EA, FindOffScreenPlaceOnRoad: only posResults[0] is used; npccarexit tries the others on a memo hit.
//  - EA, TrafficManager spawn: GetParkingSpotForCar's matrix only orients the car (its position is the road point).
//  - Ours: no loops except the corridor samples (capped at 60), the two 4 m radius queries and the candidate scan (the IC's
//    array); no retries beyond one far re-ask; one sleep per teleport leg (1-90 sim min); tables capped (256 memo, 64
//    pending) and emptied when full.
using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        public static float kNpcExitFarMetres = 200f;
        const float kUtClearM = 4f;  // a Sim or another vehicle this close to the spot: EA drives (no car is placed onto anyone)

        static void ApplyUnseenTraffic()
        {
            Patch(typeof(CarNpcManager), "FindOffScreenPlaceOnRoad",
                new Type[] { typeof(Car), typeof(Vector3), typeof(float), typeof(Vector3).MakeByRefType() },
                typeof(NpcExitHook), "ExitHook", "ExitOrig");
        }

        // from VehicleLeaksWorldLoad (every world load)
        static void UtWorldLoad() { sNeMemo.Clear(); sNePending.Clear(); }

        static bool UtOccupied(Car car)
        {
            Sim[] o = car.Occupants;
            if (o == null) return false;
            foreach (Sim s in o) if (s != null) return true;
            return false;
        }

        static bool UtCorridorUnseen(Vector3 p, Vector3 spot)
        {
            int n = UnseenTrafficCore.CorridorSteps(p.x, p.z, spot.x, spot.z);
            for (int i = 1; i < n; i++)  // the ends are tested by the caller
            {
                Vector3 q = new Vector3(UnseenTrafficCore.Lerp(p.x, spot.x, i, n), UnseenTrafficCore.Lerp(p.y, spot.y, i, n), UnseenTrafficCore.Lerp(p.z, spot.z, i, n));
                if (!FarFromViewers(q)) return false;
            }
            return true;
        }

        static bool UtCrowded(Car car, Vector3 spot)
        {
            Sim[] sims = Sims3.Gameplay.Queries.GetObjects<Sim>(spot, kUtClearM);
            if (sims != null) foreach (Sim s in sims) if (s != null) return true;
            Vehicle[] cars = Sims3.Gameplay.Queries.GetObjects<Vehicle>(spot, kUtClearM);
            if (cars != null) foreach (Vehicle v in cars) if (v != null && v != car) return true;
            return false;
        }

        // (A) at LiteDriver's spot ask, the spot granted and reserved. 0 = EA plans and drives; 1 = the car was placed at the spot
        // and held for the drive time (EA's loop then finds it within 10 m: no plan, frees the spot, waits); 2 = the car left the
        // world during the hold (the caller frees the spot and answers "no spot": EA's next pass destroys an out-of-world car)
        static int UtLeg(CarNpcWithNoDriver car, Matrix44 mat, int[] c)
        {
            float min;
            try
            {
                Vector3 p = car.Position, spot = mat.pos.V3;
                float d2 = (p - spot).LengthSqr();
                if (car.ShouldDriveOff || d2 <= 100f) { c[3814]++; return 0; }
                if (UtOccupied(car)) { c[3805]++; return 0; }
                if (!FarFromViewers(p) || UcOnScreen(car)) { c[3802]++; return 0; }
                if (!FarFromViewers(spot)) { c[3803]++; return 0; }
                if (!UtCorridorUnseen(p, spot)) { c[3804]++; return 0; }
                if (UtCrowded(car, spot)) { c[3806]++; return 0; }
                min = UnseenTrafficCore.LegMinutes((float)Math.Sqrt(d2), car.CarSpeed);
                car.PlaceAt(spot, mat.at.V3, null);  // EA's own placement of this car class (TrafficManager spawn)
            }
            catch (ResetException) { throw; }
            catch (Exception e) { c[3811]++; return 0; }
            c[3801]++;
            c[3813] += (int)min;
            Simulator.Sleep((uint)SimClock.ConvertToTicks(min, TimeUnit.Minutes));  // the drive's duration; the spot stays reserved as during EA's DoRoute
            if (car.HasBeenDestroyed || !car.InWorld) { c[3812]++; return 2; }
            return 1;
        }

        // (B) the decision at the drive-off; true = release in place
        static bool UtReleaseInPlace(CarNpcWithNoDriver car, int[] c)
        {
            c[3807]++;
            if (!car.InWorld || UtOccupied(car)) { c[3810]++; return false; }
            if (!FarFromViewers(car.Position) || UcOnScreen(car)) { c[3809]++; return false; }
            c[3808]++;
            return true;
        }

        // ---- npccarexit ----
        sealed class NePend { public float Sx, Sz, Gx, Gz; }
        static readonly Dictionary<ulong, NePend> sNePending = new Dictionary<ulong, NePend>();  // car ObjectId -> exit asked
        static readonly Dictionary<ulong, bool> sNeMemo = new Dictionary<ulong, bool>();          // ExitKey -> unreachable
        const int kNeMemoMax = 256, kNePendingMax = 64;

        static bool NeKnownBad(Vector3 start, Vector3 goal)
        {
            return sNeMemo.ContainsKey(UnseenTrafficCore.ExitKey(start.x, start.z, goal.x, goal.z));
        }

        static void NeMark(Car car, Vector3 start, Vector3 goal, int[] c)
        {
            if (sNePending.Count >= kNePendingMax) { sNePending.Clear(); c[3824]++; }
            NePend p = new NePend();
            p.Sx = start.x; p.Sz = start.z; p.Gx = goal.x; p.Gz = goal.z;
            sNePending[car.ObjectId.Value] = p;
        }

        static bool NeHouseholdAboard(Car car)
        {
            Sim[] o = car.Occupants;
            if (o == null) return false;
            Household hh = Household.ActiveHousehold;
            foreach (Sim s in o)
                if (s != null && (s.IsSelectable || (hh != null && s.Household == hh))) return true;
            return false;
        }

        // RflPlan, after each plan, only while an exit is pending
        static void NeLearn(ScriptCore.Route r, IScriptProxy f, RoutePlanResult res)
        {
            Car car = f != null ? f.Target as Car : null;
            if (car == null) return;
            NePend p;
            ulong id = car.ObjectId.Value;
            if (!sNePending.TryGetValue(id, out p)) return;
            Vector3 goal = r.GetApproximateOriginalDestination();
            if (!UnseenTrafficCore.SameGoal(goal.x, goal.z, p.Gx, p.Gz)) return;  // another plan of the same car
            sNePending.Remove(id);
            int[] c = PatchCounters();
            if (res.mType == RoutePlanResultType.Succeeded) { c[3826]++; return; }
            if (res.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) { c[3825]++; return; }  // a timeout or a blocker is not a fact about the road
            if (sNeMemo.Count >= kNeMemoMax) { sNeMemo.Clear(); c[3824]++; }
            sNeMemo[UnseenTrafficCore.ExitKey(p.Sx, p.Sz, p.Gx, p.Gz)] = true;
            c[3822]++;
            c[3823] = sNeMemo.Count;
        }

        // CarNpcManager has only a private constructor, so the hook is a plain class (as EnvScoreHook)
        public class NpcExitHook
        {
            public bool ExitHook(Car car, Vector3 targetPosition, float distance, ref Vector3 outPosition)
            {
                if (!ShortcutOn("npccarexit") || car == null) return ExitOrig(car, targetPosition, distance, ref outPosition);
                Vector3 cp = car.Position;
                if (!UnseenTrafficCore.IsDriveAwayAsk(distance, targetPosition.x, targetPosition.y, targetPosition.z, cp.x, cp.y, cp.z))
                    return ExitOrig(car, targetPosition, distance, ref outPosition);
                int[] c = PatchCounters();
                c[3815]++;
                bool exempt;
                try
                {
                    if (car is FoodTruckBase) { c[3817]++; exempt = true; }
                    else if (NeHouseholdAboard(car)) { c[3816]++; exempt = true; }
                    else exempt = false;
                }
                catch (ResetException) { throw; }
                catch (Exception e) { exempt = true; }  // any doubt: EA's answer
                if (exempt) return ExitOrig(car, targetPosition, distance, ref outPosition);

                // EA's own ask first, the option EA passes (0); through RoadOffScreenHook, so jobyieldroad's step-aside still applies
                Vector3[] pos;
                Quaternion[] orient;
                if (!World.FindPlaceOnRoadOffScreen(car.Proxy, targetPosition, (FindPlaceOnRoadOption)0, distance, out pos, out orient) || pos == null || pos.Length == 0)
                    return false;  // EA: no place
                if (!NeKnownBad(targetPosition, pos[0])) { outPosition = pos[0]; NeMark(car, targetPosition, pos[0], c); return true; }
                c[3818]++;
                for (int i = 1; i < pos.Length; i++)
                    if (!NeKnownBad(targetPosition, pos[i])) { c[3819]++; outPosition = pos[i]; NeMark(car, targetPosition, pos[i], c); return true; }
                if (car.InWorld && World.FindPlaceOnRoadOffScreen(car.Proxy, targetPosition, (FindPlaceOnRoadOption)0, kNpcExitFarMetres, out pos, out orient) && pos != null)
                    for (int i = 0; i < pos.Length; i++)
                        if (!NeKnownBad(targetPosition, pos[i])) { c[3820]++; outPosition = pos[i]; NeMark(car, targetPosition, pos[i], c); return true; }
                c[3821]++;
                return false;  // EA's no-place branch: no plan, the car is released where it is, as after the failed plan
            }
            public bool ExitOrig(Car car, Vector3 targetPosition, float distance, ref Vector3 outPosition) { return false; } // receives CarNpcManager.FindOffScreenPlaceOnRoad's original header
        }
    }
}
