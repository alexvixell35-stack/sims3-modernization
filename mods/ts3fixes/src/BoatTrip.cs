using System;
using System.Collections.Generic;
using System.Globalization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // ---- boattrip (tuning kBoatTrip, off by default, townies only): a failed boat trip is made to work, not dropped ----
        // An earlier version remembered a failed trip and failed its next plans at once (a memo design, withdrawn). That ends
        // the trip, autonomy starts another one, boardings rise (1288 vs 1132 in an A/B) and each boarding pays a sim-thread
        // wait, so the saved plan time came back as boarding waits. This version has no memory of failures and never answers a
        // plan without planning. Each step below is tried in order on the same trip; a step that works returns its plan to the
        // caller, which follows it as its own. The extra steps run at most once per Sim per kBtRetryMs (the plans between are
        // EA's, untouched), so EA's ~1/s retry does not multiply their cost.
        //
        // What goes wrong in EA's chain (Isla Paradiso):
        //  a. The walking Plan of a Sim on a lot with dead water needs a lot leg to the lot's one best exit (docs/99 2.8 step 6,
        //     no second exit) and fails with NoPathToDestination before any boarding.
        //  b. GetInBoatRouteAction places the boat on the first boat node of the Sim's plan, lot water or not, the same point
        //     for every Sim; boats block each other with their dynamic footprints (failed plans name ~boat id as blocker).
        //  c. A boat plan to a land goal fails (blocker None / Wall / Sea) when the one landing the planner picks is bad.
        //  d. After a failed boat route BoatScriptAdaptor.PostRoute calls OnRouteFinishedAsBoat: the Sim stays seated and plans
        //     again ~1/s; when it leaves later, Boat.GetOut puts it on the nearest shore, often the dead-water lot of (a).
        //
        // Boat follower, failed Plan / PlanFromPoint (BtAfter, BtBoat):
        //  1. The blocker is another boat (its id or its complement): that boat goes on the route's ignore list and the plan
        //     runs again. docs/107 3 steps 3 and 7: the footprint grid turns a complement key back into the object id before
        //     the ignore-list test, so the list covers dynamic footprints [verified there for the footprint grid; whether the
        //     water grid's blocked cells (docs/100, marks without ids) follow it is not verified].
        //  2. The boat sits on lot water and its driver is unseen: the plan runs from the open-water cell (BtCell) and the boat
        //     is moved there.
        //  3. Goal on land: EA's own Boat.RouteBoatToLandOnWayToPosition, rebuilt from public calls (the private original
        //     cannot be called without reflection, which would wrap a task reset): a boat route to the goal lot, else to 50 m
        //     and 150 m around the goal with DestinationMustBeOnLand; the boat drives to the end of that route's boat part,
        //     the Sim gets out at the start of its land part, the route gets its Sim back as follower
        //     (BoatFollowSubPathRouteAction's lines) and is planned again on foot through the normal Sim plan (UfPlan, so
        //     unseenboat and the unseen fallbacks see it).
        //  4. Still nothing: the Sim gets out on a shore that is not on a dead-water lot (EA's FindPlacesToDisembarkOnShoreline
        //     at the boat and at 30-120 m towards the goal; lots where a walking Plan failed as in (a), or a boat sat on lot
        //     water, are skipped), and the route is planned again on foot as in 3. An unseen Sim's plan passes UbTrip there:
        //     unseenboat's own gates decide about its teleport, unchanged.
        // Steps 3 and 4 run only for a Plan on the Sim's own task that may yield, with the Sim seated as driver and neither
        // Sim nor boat inside a route. Nothing here ends the interaction: when every step fails the caller gets EA's failure.
        // Sim follower, failed walking Plan on a real lot, outdoors (BtStart): the plan from the open-water cell with
        // BeginAsBoat (EA's GetInBoatRouteAction.OnLoad recipe; kept only when its first path is a boat path), else the same
        // Plan on foot only, else, for an unseen Sim, the Sim is placed on a shore as in 4 and plans from there.
        // Placement (BtSpot, Vehicle.PlaceAt hook): boatplace's rule plus spreading (a cell within kBtSpreadM of another boat
        // is skipped, ts3native boatprobe.cpp messages 5 / 6). Before it the boarding task steps aside while a route job runs
        // (RouteBackoffHook.StepAside, switch jobyield): the boarding slice (PlaceAt, ForceUpdateDynamicFootprint,
        // EnableDynamicFootprint, up to FadeOut's first Sleep) calls the private-footprint ICs for a boat with queued records,
        // and ts3native answers that with a forced drain that holds the whole sim thread until the job ends. A sleeping
        // townie task costs nothing; household boardings keep the forced drain.
        // The boat afterwards (BtSweep): loose, empty, no driver in the water, not routing for kBtIdleMs: taxi = footprint
        // off + destroyed (BoatTaxi.Release); NPC-generated boat or a boat of the Sim's home lot = EA's BoatOwnable.Park(null,
        // sim) (Boat.MakeSimExitAndParkBoatIfPossible); nobody to hand it to = kept, footprint off.
        // Household rule: a household, selected, no-household or unknown-scope Sim (BtTownie), a boat with such a Sim in or on
        // it, and a boat owned by the active household or with no known owner household (BoatTownie) keep EA's behaviour.
        // Nothing is saved. boatplace is not read while boattrip is on (BplSpot hands over). Steps 2 and the cell starts need
        // ts3native's water-grid answers (boatprobe.cpp); an older ts3native answers "no cell" and those steps are skipped.
        // shortcut: the trip start needs the Sim outdoors (room 0); a townie indoors on such a lot still fails as before.
        // Counters: [4070] boats placed or moved to an open-water cell (placement and step 2), [4071] trip starts that took a
        // boat start, an on-foot plan or (unseen Sim) another shore, [4072] boat plans a step 1-4 answered, [4073] trips where
        // every step failed (EA's failure kept), [4074] leftover boats the sweep released, parked or turned the footprint off.
        const int kBtMaxLog = 40, kBtIdleMs = 30000, kBtSweepMs = 5000, kBtRetryMs = 10000;
        const int kBtBusyStaleMs = 60000;  // a busy mark this old belongs to a task that died without an exception
        const int kBtMapCap = 256;         // per-Sim / per-boat maps: dropped whole at the cap (entries are short-lived)
        const float kBtSpreadM = 4f, kBtMinTripM = 20f;
        static int sBtLogged, sBtSweepAt;
        static bool sBtSweepQueued;
        sealed class BtBoat { public Boat B; public Sim S; public int At; }
        static readonly Dictionary<ulong, BtBoat> sBtBoats = new Dictionary<ulong, BtBoat>();
        static readonly Dictionary<ulong, int> sBtTried = new Dictionary<ulong, int>();   // Sim -> tick of its last extra steps
        static readonly Dictionary<ulong, int> sBtBusy = new Dictionary<ulong, int>();    // Sim -> tick it entered a step (its own plans pass)
        static readonly Dictionary<ulong, bool> sBtDeadLots = new Dictionary<ulong, bool>();  // lots seen as (a) or with a boat on their water

        static void BtWorldLoad()
        {
            sBtBoats.Clear();
            sBtTried.Clear();
            sBtBusy.Clear();
            sBtDeadLots.Clear();
            sBtSweepQueued = false;
        }

        static void BtLog(string line)
        {
            if (sBtLogged >= kBtMaxLog) return;
            sBtLogged++;
            DiagLine("boattrip: " + line);
        }
        static string BtId(GameObject o) { return o != null ? o.ObjectId.Value.ToString("X") : "0"; }
        static string BtM(float m) { return m.ToString("0.0", CultureInfo.InvariantCulture); }

        // the household rule for the Sim: false = EA's behaviour
        static bool BtTownie(Sim s) { return !IsPlayerSim(s) && s.Household != null; }

        static bool BtRealLot(Vector3 p)
        {
            Lot lot = LotManager.GetLotAtPoint(p);
            return lot != null && !lot.IsWorldLot && !lot.IsHouseboatLot();
        }
        static void BtDeadLot(Vector3 p)
        {
            Lot lot = LotManager.GetLotAtPoint(p);
            if (lot == null || lot.IsWorldLot) return;
            if (sBtDeadLots.Count >= 64) sBtDeadLots.Clear();
            sBtDeadLots[lot.LotId] = true;
        }
        static bool BtWater(Vector3 p)
        {
            TerrainType t = (TerrainType)World.GetTerrainType(p.x, p.z, 0);
            return t == TerrainType.WorldSea || t == TerrainType.LotPond || t == TerrainType.WorldPond;
        }

        // the Sim is inside a step: plans of that step (nested routes) pass through untouched and uncounted
        static bool BtBusy(Sim s)
        {
            int at;
            return sBtBusy.TryGetValue(s.ObjectId.Value, out at) && NoStartCore.Within(System.Environment.TickCount, at, kBtBusyStaleMs);
        }

        // the extra steps for this Sim are due (and marks them taken)
        static bool BtDue(Sim s)
        {
            ulong id = s.ObjectId.Value;
            int now = System.Environment.TickCount, at;
            if (BtBusy(s) || (sBtTried.TryGetValue(id, out at) && NoStartCore.Within(now, at, kBtRetryMs))) return false;
            if (sBtTried.Count >= kBtMapCap) sBtTried.Clear();
            sBtTried[id] = now;
            return true;
        }

        static bool BtBoatNear(Vector3 p, Boat self)
        {
            foreach (Boat o in Sims3.Gameplay.Queries.GetObjects<Boat>(p, kBtSpreadM))
                if (o != null && o != self && !o.HasBeenDestroyed && o.InWorld) return true;
            return false;
        }

        // the boat a failed plan names as blocker: the id itself or its complement (a dynamic footprint's key)
        static Boat BtBlockerBoat(ulong id, Boat self)
        {
            if (id == 0UL) return null;
            Boat b = GameObject.GetObject(new ObjectGuid(id)) as Boat;
            if (b == null) b = GameObject.GetObject(new ObjectGuid(~id)) as Boat;
            return b != null && b != self && !b.HasBeenDestroyed ? b : null;
        }

        // The open-water cell for a boat that would start at pos: the nearest boat-passable cell no lot covers and no other boat
        // is within kBtSpreadM of, in the start cell's water body when it has one. 0 = found, 1 = ts3native without channel
        // 0x89, 2 = none in range, 3 = only a cell of another body, 4 = too far or the cell's point is on a lot.
        static int BtCell(Vector3 pos, Boat self, bool log, out Vector3 cell, out float d, out bool same)
        {
            cell = pos; d = 0f; same = false;
            FloatBits b = default(FloatBits);
            if (BplAsk(5, 0))
            {
                foreach (Boat o in Sims3.Gameplay.Queries.GetObjects<Boat>(pos, Instantiator.kBoatPlaceMaxM + 2f * kBtSpreadM))
                {
                    if (o == null || o == self || o.HasBeenDestroyed || !o.InWorld) continue;
                    Vector3 op = o.Position;
                    b.F = op.x;
                    BplAsk(1, b.U);
                    b.F = op.z;
                    BplAsk(2, b.U);
                    if (!BplAsk(6, 0)) break;  // the list is full: the rest only stack as before
                }
            }
            b.F = pos.x;
            if (!BplAsk(1, b.U)) return 1;
            b.F = pos.z;
            BplAsk(2, b.U);
            BplAsk(3, (log ? 1u : 0u) | (uint)((self != null ? self.ObjectId.Value : 0UL) & 0xFFFFFFUL) << 8);  // answer unused: a start cell without a body counts too
            if (!BplAsk(4, 0)) return 2;
            same = BplAsk(4, 1);
            if (!same && BplAsk(4, 20)) return 3;  // the start cell has a body: only a cell of that body is connected
            int ox = 0, oz = 0;
            for (int i = 0; i < 8; i++)
            {
                if (BplAsk(4, (uint)(2 + i))) ox |= 1 << i;
                if (BplAsk(4, (uint)(10 + i))) oz |= 1 << i;
            }
            float dx = unchecked((sbyte)ox) * 0.5f, dz = unchecked((sbyte)oz) * 0.5f;
            d = (float)Math.Sqrt(dx * dx + dz * dz);
            cell = new Vector3(pos.x + dx, pos.y, pos.z + dz);
            Lot tl = LotManager.GetLotAtPoint(cell);
            return d > Instantiator.kBoatPlaceMaxM || (tl != null && !tl.IsWorldLot) ? 4 : 0;
        }

        // A shore point for the Sim that is not on a dead-water lot: EA's disembark search at `from`, then at 30-120 m towards
        // the goal. Points on a houseboat lot, on the household's lot or still in water are skipped.
        static bool BtShore(Sim sim, Vector3 from, Vector3 goal, out Vector3 shore)
        {
            shore = Vector3.Invalid;
            Vector3 dir = goal - from;
            dir.y = 0f;
            float len = dir.Length();
            for (int i = 0; i < 5; i++)
            {
                float step = i * 30f;
                if (i > 0 && (len < 1f || step > len)) break;
                Vector3 c = i == 0 ? from : from + dir * (step / len);
                Vector3[] pos;
                Quaternion[] rot;
                if (!World.FindPlacesToDisembarkOnShoreline(sim.Proxy, c, 4u, out pos, out rot) || pos == null) continue;
                foreach (Vector3 p in pos)
                {
                    if (p == Vector3.Invalid || p == Vector3.Zero || p == Vector3.OutOfWorld || BtWater(p)) continue;
                    Lot lot = LotManager.GetLotAtPoint(p);
                    if (lot != null && !lot.IsWorldLot && (lot.IsHouseboatLot() || sBtDeadLots.ContainsKey(lot.LotId) || lot.Household == Sims3.Gameplay.CAS.Household.ActiveHousehold)) continue;
                    shore = p;
                    return true;
                }
            }
            return false;
        }

        static void BtTrack(Boat boat, Sim sim)
        {
            BtBoat t;
            if (!sBtBoats.TryGetValue(boat.ObjectId.Value, out t)) sBtBoats[boat.ObjectId.Value] = t = new BtBoat();
            t.B = boat; t.S = sim; t.At = System.Environment.TickCount;
        }

        // the point Vehicle.PlaceAt gets (BplSpot hands over when the switch is on)
        static Vector3 BtSpot(Boat boat, Vector3 pos, Sim sim)
        {
            bool scope = false;
            try
            {
                RouteAction ra = sim != null && sim.RoutingComponent != null ? sim.RoutingComponent.GetCurrentRouteAction() : null;
                if (ra == null || ra.GetType() != sVlGiBoatType) return pos;
                scope = BtTownie(sim) && BoatTownie(boat);
            }
            catch (Exception __swx) { VlThrew("boattrip", __swx); }
            if (!scope) return pos;
            RouteBackoffHook.BtStepAside();  // may sleep: outside every catch, a task reset passes through as in EA's own waits
            try
            {
                BtTrack(boat, sim);
                BtSweepSoon();
                bool lotWater = BtRealLot(pos);
                if (lotWater) BtDeadLot(pos);
                if (!lotWater && !BtBoatNear(pos, boat)) return pos;
                Vector3 cell;
                float d;
                bool same;
                int why = BtCell(pos, boat, sBtLogged < kBtMaxLog, out cell, out d, out same);
                if (why != 0)
                {
                    BtLog("place: no cell (" + why + ") for boat " + BtId(boat) + " at " + V(pos) + (lotWater ? ", lot water" : ", another boat on the point"));
                    return pos;
                }
                PatchCounters()[4070]++;
                BtLog("place: " + boat.GetType().Name + " " + BtId(boat) + " from " + V(pos) + " to " + V(cell) + " (" + BtM(d) + " m, "
                    + (same ? "same body" : "start cell has no body") + (lotWater ? ", lot water" : ", another boat on the point") + "), Sim " + BtId(sim));
                return cell;
            }
            catch (Exception __swx)
            {
                VlThrew("boattrip", __swx);
                return pos;
            }
        }

        // the leftover-boat sweep, at most every kBtSweepMs of real time, on a task of its own (it destroys and parks)
        static void BtSweepSoon()
        {
            if (sBtSweepQueued || sBtBoats.Count == 0 || NoStartCore.Within(System.Environment.TickCount, sBtSweepAt, kBtSweepMs)) return;
            sBtSweepQueued = true;
            Simulator.AddObject(new LoopTask(BtSweepTask));
        }

        // the queued sweep; the hourly call in VehicleLeaks.cs runs BtSweep directly and must not clear the queued flag
        static void BtSweepTask()
        {
            sBtSweepQueued = false;
            BtSweep();
        }

        static void BtSweep()
        {
            int now = sBtSweepAt = System.Environment.TickCount;
            if (sBtBoats.Count == 0) return;
            if (!ShortcutOn("boattrip")) { sBtBoats.Clear(); return; }  // switched off (in-load A/B): nothing is watched any more
            int[] c = PatchCounters();
            foreach (KeyValuePair<ulong, BtBoat> p in new List<KeyValuePair<ulong, BtBoat>>(sBtBoats))
            {
                BtBoat t = p.Value;
                Boat b = t.B;
                try
                {
                    // destroyed, in an inventory, moored: EA finished the trip. A household Sim with it or a new owner: never ours.
                    if (VlGone(b) || !VlLoose(b) || !BoatTownie(b)) { sBtBoats.Remove(p.Key); continue; }
                    bool hh, other;
                    VlWho(b, null, out hh, out other);
                    var inWater = b.PassengersInTheWater;
                    if (other || b.Driver != null || b.SimIsGettingIn || b.DriverInTheWaterSimId != 0UL || (inWater != null && inWater.Count > 0)
                        || (b.RoutingComponent != null && b.RoutingComponent.IsRouting))
                    {
                        t.At = now;
                        continue;
                    }
                    if (NoStartCore.Within(now, t.At, kBtIdleMs)) continue;
                    sBtBoats.Remove(p.Key);
                    Vector3 at = b.Position;
                    string kind = b.GetType().Name, act = null;
                    Sim s = t.S;
                    BoatOwnable bo = b as BoatOwnable;
                    if (VlThrowaway(b))
                    {
                        if (b.RoutingComponent != null) b.RoutingComponent.UpdateDynamicFootprint(b, false);  // BoatTaxi.Release
                        VlRelease(b);
                        act = "taxi released";
                    }
                    else if (bo != null && s != null && !s.HasBeenDestroyed && BtTownie(s) && s.Inventory != null
                        && (bo.GeneratedOwnableForNpc || (s.LotHome != null && b.GetOwnerLot() == s.LotHome)))
                    {
                        bool gen = bo.GeneratedOwnableForNpc;
                        bo.Park(null, s);
                        if (!VlLoose(b)) act = gen ? "NPC boat parked with its Sim" : "owned boat parked with its owner";
                    }
                    if (act == null)
                    {
                        if (!VlGone(b) && b.RoutingComponent != null) b.RoutingComponent.UpdateDynamicFootprint(b, false);
                        act = "kept, footprint off";
                    }
                    c[4074]++;
                    BtLog("left: " + kind + " " + p.Key.ToString("X") + " at " + V(at) + ", Sim " + BtId(s) + ": " + act);
                }
                catch (ResetException) { throw; }  // Park yields: a task reset must pass through
                catch (Exception __swx)
                {
                    VlThrew("boattrip", __swx);
                    sBtBoats.Remove(p.Key);
                }
            }
        }

        // Step 3's drive, EA's Boat.RouteBoatToLandOnWayToPosition from public calls: true = the boat drove to the end of the
        // boat part of a route to (or near) the goal; land = where that route goes on by foot. Yields (boat.DoRoute).
        static bool BtDriveToLanding(Sim sim, Boat boat, Vector3 goal, out Vector3 land)
        {
            land = Vector3.Invalid;
            Vector3 waterEnd = Vector3.Invalid;
            Lot gl = LotManager.GetLotAtPoint(goal);
            for (int tryNo = 0; tryNo < 3 && waterEnd == Vector3.Invalid; tryNo++)
            {
                if (tryNo == 0 && (gl == null || gl.IsWorldLot || gl == sim.LotCurrent)) continue;
                Route route = boat.CreateRoute();
                route.SetOption2(Route.RouteOption2.DestinationMustBeOnLand, true);
                route.SetOption2(Route.RouteOption2.DestinationMustBeInDeepWater, false);
                route.SetOption2(Route.RouteOption2.EndAsBoat, false);
                route.SetOption2(Route.RouteOption2.DisablePlanningOnLand, false);
                route.SetOption(Route.RouteOption.EnableWaterPlanning, false);
                bool ok = tryNo == 0 ? gl.PlanToLot(route).Succeeded()
                    : route.PlanToPointRadialRange(goal, 0f, tryNo == 1 ? 50f : 150f, RouteDistancePreference.PreferNearestToRouteDestination, RouteOrientationPreference.TowardsObject, 0UL, null).Succeeded();
                if (!ok) continue;
                uint n = route.GetNumPaths();
                for (uint i = 0; i < n; i++)
                {
                    if (route.GetPathData(i).PathType == PathType.BoatPath) continue;
                    if (i != 0)
                    {
                        route.GetPathEndPoint(i - 1, ref waterEnd);
                        route.GetPathStartPoint(i, ref land);
                    }
                    break;
                }
            }
            if (waterEnd == Vector3.Invalid || land == Vector3.Invalid) return false;
            Route drive = boat.CreateRoute();
            drive.SetOption2(Route.RouteOption2.EndAsBoat, true);
            drive.SetOption2(Route.RouteOption2.DestinationMustBeInDeepWater, true);
            drive.SetOption2(Route.RouteOption2.DisablePlanningOnLand, true);
            drive.SetOption2(Route.RouteOption2.DestinationMustBeOnLand, false);
            if (!drive.PlanToPointRadialRange(waterEnd, 0f, 10f, RouteDistancePreference.PreferFurthestFromRouteDestination, RouteOrientationPreference.TowardsObject, 0UL, null).Succeeded()) return false;
            return boat.DoRoute(drive);
        }

        public partial class RouteBackoffHook
        {
            // the boarding task sleeps while a route job runs (switch jobyield, StepAside)
            internal static void BtStepAside() { StepAside(); }

            // the trip's Sim (the follower, or the driver of the follower boat) when Sim and boat are townie scope
            bool BtWho(out Sim s, out Boat boat)
            {
                s = null; boat = null;
                IScriptProxy f = Follower;
                object o = f != null ? f.Target : null;
                boat = o as Boat;
                if (boat != null)
                {
                    s = boat.Driver;
                    if (s != null) return BtTownie(s) && BoatTownie(boat);
                    // no driver yet (the boat's plan before its Sim sits, e.g. EnterHouseboatThroughBoat on a dead-water lot,
                    // which gets a new boat each try): the Sim whose task plans it. Its own task only, so the household rule
                    // reads that Sim; a household Sim on or in the boat keeps it EA's (BoatTownie). Tracked, so the leftover
                    // sweep parks or releases it once idle. Steps 3/4 need a seated driver and stay out.
                    IScriptProxy tp = Simulator.GetProxy(Simulator.CurrentTask);
                    s = tp != null ? tp.Target as Sim : null;
                    if (s == null || !BtTownie(s) || !BoatTownie(boat)) { s = null; return false; }
                    BtTrack(boat, s);
                    return true;
                }
                s = o as Sim;
                return s != null && BtTownie(s) && !(s.Posture is SittingInBoat) && GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat);
            }

            // a step's plan worked: the stuck watchdog already counted the failed plan before the step ran
            RoutePlanResult BtOk(RoutePlanResult s)
            {
                if (s.mType == RoutePlanResultType.Succeeded) NotePlanResult(Follower, true, this);
                return s;
            }

            // the route goes on with its Sim on foot: BoatFollowSubPathRouteAction's lines after a boat part, then the normal Sim plan
            RoutePlanResult BtOnFoot(Sim sim)
            {
                Follower = sim.Proxy;
                FollowerAgeGenderSpecies = (uint)sim.SimDescription.AgeGenderSpecies;
                RemoveObjectToIgnoreForRoute(sim.ObjectId);
                SetOption2(Sims3.SimIFace.Route.RouteOption2.BeginAsBoat, false);
                SetOption2(Sims3.SimIFace.Route.RouteOption2.UseFollowerStartOrientation, false);
                return UfPlan(0, Vector3.Invalid);
            }

            // r = the result of the plan that just ran (kind 0 Plan, 2 PlanFromPoint), t0 = its start. Returns what the hook
            // returns. Called from RouteBackoffHook.PlanHook / PlanFromPointHook with the switch on.
            RoutePlanResult BtAfter(int kind, Vector3 from, RoutePlanResult r, uint t0)
            {
                if ((kind & 1) != 0 || r.mType == RoutePlanResultType.Succeeded || r.mDistanceTotal > 0f) return r;
                Sim sim = null;
                Boat boat = null;
                Vector3 goal = Vector3.Invalid;
                try
                {
                    if (!BtWho(out sim, out boat)) return r;
                    goal = GetApproximateOriginalDestination();
                    if (goal == Vector3.Invalid || goal == Vector3.Origin) return r;
                    BtSweepSoon();
                }
                catch (Exception __swx) { VlThrew("boattrip", __swx); return r; }
                int ms = unchecked((int)((SrcNowUs() - t0) / 1000));
                return boat != null ? BtBoat(kind, from, r, ms, sim, boat, goal) : BtStart(kind, r, ms, sim, goal);
            }

            RoutePlanResult BtBoat(int kind, Vector3 from, RoutePlanResult r, int ms, Sim sim, Boat boat, Vector3 goal)
            {
                if (BtBusy(sim)) return r;  // a plan of our own step (step 3's drive): not a trip failure
                if (!BtDue(sim)) return r;
                int[] c = PatchCounters();
                ulong sid = sim.ObjectId.Value;
                bool planned = false, yieldOk = false;
                Vector3 start = kind >= 2 ? from : boat.Position;
                sBtBusy[sid] = System.Environment.TickCount;
                try
                {
                    try
                    {
                        // step 1
                        Boat other = BtBlockerBoat(r.mBlockerObjectId, boat);
                        if (other != null && BoatTownie(other))  // household rule: never drive through a household or unknown-owner boat
                        {
                            AddObjectToIgnoreForRoute(other.ObjectId);
                            planned = true;
                            KdRescope(); RoutePlanResult s1 = kind == 0 ? PlanOrig() : PlanFromPointOrig(from);
                            if (s1.mType == RoutePlanResultType.Succeeded)
                            {
                                c[4072]++;
                                BtLog("step 1: boat " + BtId(boat) + " ignores boat " + BtId(other) + ", path " + BtM(s1.mDistanceTotal) + " m, Sim " + BtId(sim));
                                return BtOk(s1);
                            }
                            RemoveObjectToIgnoreForRoute(other.ObjectId);
                        }
                        // step 2: the boat jumps to the cell, so only for an unseen driver; a seen boat goes on to step 3
                        if (BtRealLot(start) && UfWhy(sim) == 0)
                        {
                            BtDeadLot(start);
                            Vector3 cell;
                            float d;
                            bool same;
                            if (BtCell(start, boat, sBtLogged < kBtMaxLog, out cell, out d, out same) == 0)
                            {
                                planned = true;
                                KdRescope(); RoutePlanResult s2 = PlanFromPointOrig(cell);
                                if (s2.mType == RoutePlanResultType.Succeeded)
                                {
                                    boat.SetPosition(cell);
                                    BoatRoutingComponent brc = boat.RoutingComponent as BoatRoutingComponent;
                                    if (brc != null) brc.ForceUpdateDynamicFootprint();
                                    BtTrack(boat, sim);
                                    c[4070]++;
                                    c[4072]++;
                                    BtLog("step 2: boat " + BtId(boat) + " moved from lot water " + V(start) + " to " + V(cell) + " (" + BtM(d) + " m), path "
                                        + BtM(s2.mDistanceTotal) + " m, Sim " + BtId(sim));
                                    return BtOk(s2);
                                }
                            }
                        }
                        yieldOk = kind == 0 && MayYield() && Simulator.CurrentTask == sim.ObjectId && boat.Driver == sim && sim.Posture is SittingInBoat
                            && !(sim.RoutingComponent != null && sim.RoutingComponent.IsRouting) && !(boat.RoutingComponent != null && boat.RoutingComponent.IsRouting)
                            && !GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat) && !GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater);
                    }
                    catch (ResetException) { throw; }
                    catch (Exception __swx) { VlThrew("boattrip", __swx); yieldOk = false; }
                    if (yieldOk)
                    {
                        // steps 3 and 4 sleep (a boat route, EA's get-out): a task reset must pass through, anything else is ours
                        try
                        {
                            if (!BtWater(goal))
                            {
                                Vector3 land;
                                if (BtDriveToLanding(sim, boat, goal, out land))
                                {
                                    if (sim.Posture is SittingInBoat) boat.GetOut(sim, land, false, false, null, Vector3.Invalid);
                                    if (!(sim.Posture is SittingInBoat))
                                    {
                                        BtParkEmpty(boat, sim);
                                        RoutePlanResult s3 = BtOnFoot(sim);
                                        c[4072]++;
                                        BtLog("step 3: Sim " + BtId(sim) + " landed at " + V(sim.Position) + " from boat " + BtId(boat) + ", walk " + s3.mType + " "
                                            + BtM(s3.mDistanceTotal) + " m, goal " + V(goal));
                                        return BtOk(s3);
                                    }
                                }
                            }
                            if (sim.Posture is SittingInBoat && boat.Driver == sim && !boat.HasBeenDestroyed)
                            {
                                Vector3 shore;
                                if (BtShore(sim, boat.Position, goal, out shore))
                                {
                                    boat.GetOut(sim, shore, false, false, null, Vector3.Invalid);
                                    if (!(sim.Posture is SittingInBoat))
                                    {
                                        BtParkEmpty(boat, sim);
                                        RoutePlanResult s4 = BtOnFoot(sim);
                                        c[4072]++;
                                        BtLog("step 4: Sim " + BtId(sim) + " out at " + V(sim.Position) + " from boat " + BtId(boat) + ", walk " + s4.mType + " "
                                            + BtM(s4.mDistanceTotal) + " m, goal " + V(goal));
                                        return BtOk(s4);
                                    }
                                }
                            }
                        }
                        catch (ResetException) { throw; }
                        catch (Exception __swx)
                        {
                            VlThrew("boattrip", __swx);
                            // the Sim may be out of the boat by now: then the route is its own again and it plans on foot
                            if (!(sim.Posture is SittingInBoat) && !sim.HasBeenDestroyed) return BtOnFoot(sim);
                        }
                    }
                    c[4073]++;
                    BtLog("failed: boat " + BtId(boat) + " at " + V(start) + ", Sim " + BtId(sim) + ", " + r.mType + " blocker " + r.mBlockerType + " " + ms + " ms, goal " + V(goal)
                        + (yieldOk ? "" : ", no step 3/4 here"));
                    return planned ? FailFast(r) : r;
                }
                finally { sBtBusy.Remove(sid); }
            }

            // EA's Boat.MakeSimExitAndParkBoatIfPossible, second half: an owned boat its Sim left goes with the Sim; a taxi
            // released itself in GetOut. Anything else is the sweep's.
            static void BtParkEmpty(Boat boat, Sim sim)
            {
                try
                {
                    BoatOwnable bo = boat as BoatOwnable;
                    if (bo != null && !boat.HasBeenDestroyed && !boat.HasAnyOccupants() && BoatTownie(boat)) bo.Park(null, sim);
                }
                catch (ResetException) { throw; }  // Park yields: a task reset must pass through
                catch (Exception __swx) { VlThrew("boattrip", __swx); }
            }

            // the walking Plan of a Sim on a real lot
            RoutePlanResult BtStart(int kind, RoutePlanResult r, int ms, Sim sim, Vector3 goal)
            {
                bool planned = false;
                try
                {
                    int[] c = PatchCounters();
                    Vector3 at = sim.Position;
                    if (kind != 0 || r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination || GetOption2(Sims3.SimIFace.Route.RouteOption2.BeginAsBoat)
                        || !BtRealLot(at) || World.GetRoomId(at) != 0 || LotManager.GetLotAtPoint(goal) == LotManager.GetLotAtPoint(at)
                        || (goal - at).Length() < kBtMinTripM) return r;
                    if (!BtDue(sim)) return r;
                    string tried;
                    Vector3 cell;
                    float d;
                    bool same;
                    int why = BtCell(at, null, sBtLogged < kBtMaxLog, out cell, out d, out same);
                    if (why != 0) tried = "no cell " + why;
                    else
                    {
                        cell.y = World.GetTerrainHeight(cell.x, cell.z);  // as EA's own replan of a seated Sim (PositionOnFloor)
                        RoutePlanResult sp;
                        bool firstBoat = false;
                        planned = true;
                        SetOption2(Sims3.SimIFace.Route.RouteOption2.BeginAsBoat, true);
                        try
                        {
                            KdRescope(); sp = PlanFromPointOrig(cell);
                            firstBoat = sp.mType == RoutePlanResultType.Succeeded && GetNumPaths() > 0 && GetPathData(0).PathType == PathType.BoatPath;
                        }
                        finally { SetOption2(Sims3.SimIFace.Route.RouteOption2.BeginAsBoat, false); }
                        if (firstBoat)
                        {
                            c[4071]++;
                            BtDeadLot(at);  // proven only now: the walk failed while open water a few metres off works (a bare NoPath can be the goal's fault)
                            NotePlanResult(Follower, true, this);  // the stuck watchdog counted the failed Plan before this ran
                            BtLog("start: Sim " + BtId(sim) + " at " + V(at) + " boards at " + V(cell) + " (" + BtM(d) + " m), path " + BtM(sp.mDistanceTotal)
                                + " m, the walking Plan " + r.mType + " " + ms + " ms, goal " + V(goal));
                            return sp;
                        }
                        tried = "from " + V(cell) + " " + (sp.mType == RoutePlanResultType.Succeeded ? "first path not a boat path" : sp.mType.ToString());
                    }
                    // no boat start: the same Plan on foot only (no boat, no swim), so the lot's water exit is not the one exit chosen
                    bool water = GetOption(Sims3.SimIFace.Route.RouteOption.EnableWaterPlanning);
                    RoutePlanResult sl;
                    planned = true;
                    SetOption(Sims3.SimIFace.Route.RouteOption.EnableWaterPlanning, false);
                    SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, false);
                    try { KdRescope(); sl = PlanOrig(); }
                    finally
                    {
                        SetOption(Sims3.SimIFace.Route.RouteOption.EnableWaterPlanning, water);
                        SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, true);
                    }
                    if (sl.mType == RoutePlanResultType.Succeeded)
                    {
                        c[4071]++;
                        NotePlanResult(Follower, true, this);
                        BtLog("start: Sim " + BtId(sim) + " at " + V(at) + " walks (" + tried + "), path " + BtM(sl.mDistanceTotal) + " m, goal " + V(goal));
                        return sl;
                    }
                    // an unseen Sim leaves the dead-water lot for another shore and plans from there (a seen Sim stays: no pop in view)
                    Vector3 shore;
                    if (UfWhy(sim) == 0 && Simulator.CurrentTask == sim.ObjectId && !(sim.RoutingComponent != null && sim.RoutingComponent.IsRouting)
                        && BtShore(sim, at, goal, out shore))
                    {
                        c[4071]++;
                        PlaceSim(sim, shore, sim.ForwardVector, "boattrip shore");
                        if (sUrPlaced.Count >= 256) sUrPlaced.Clear();
                        sUrPlaced[sim.ObjectId.Value] = true;  // checked when it is seen and plans again (docs/202 3.9.7)
                        KdRescope(); RoutePlanResult ss = PlanOrig();
                        if (ss.mType == RoutePlanResultType.Succeeded) NotePlanResult(Follower, true, this);
                        BtLog("start: unseen Sim " + BtId(sim) + " placed from " + V(at) + " on the shore " + V(shore) + " (" + tried + ", on foot " + sl.mType + "), plan "
                            + ss.mType + " " + BtM(ss.mDistanceTotal) + " m, goal " + V(goal));
                        return ss;
                    }
                    c[4073]++;
                    BtLog("failed: Sim " + BtId(sim) + " at " + V(at) + ", " + tried + ", on foot " + sl.mType + ", goal " + V(goal));
                    return FailFast(r);
                }
                catch (ResetException) { throw; }
                catch (Exception __swx)
                {
                    VlThrew("boattrip", __swx);
                    if (planned)
                    {
                        try { return FailFast(r); }
                        catch (Exception __swy) { VlThrew("boattrip", __swy); }
                    }
                    return r;
                }
            }
        }
    }
}
