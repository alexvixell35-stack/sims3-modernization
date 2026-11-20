using System;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ---- unseenlanesnap (ON 2026-11-20, unseen townies only; docs/807) ----
    // Cause (3 Bridgeport apartment games, routefail trace "prev" field): 70% of the unseen townies' main-thread mid-route
    // replans are EA's portal fix-up replan. A portal action (PortalAnimateThroughAction: elevator cabs; PortalTraversalAction:
    // doors, hero doors, gates "otherlane"; stairs, pool ladders) returns ContinueAndPopPathAndReplan when the Sim came out of
    // another lane than the planned one (!preferredLaneSelected; the elevator assigns each rider a cab slot,
    // ElevatorPortalComponent.GetStartSlotsForApproachingSim). RoutingComponent.DoRoute then pops the portal path and calls
    // Route.ReplanFromPoint(Sim) on the Sim's own task: a sync plan, because ts3native never runs a route that is being followed
    // on the worker. The plan costs a fixed ~70 ms in the towers whatever its length, so only not calling it helps.
    // With the switch, for an unseen townie (SafeUfWhy 0, a household, human, not held) the hooked PerformAction turns that result
    // into plain ContinueAndPopPath, the same answer legreplanskip gives vehicle legs: DoRoute pops the portal path and pushes the
    // actions of the rest of the planned route as it does after a replan, minus the replan. The Sim is first put on the next
    // path's start (routable, out of view): the planned lane's exit of the same portal. No distance, floor, room or lot limit
    // when the Sim and the spot are both out of view (LaneSnapCore). Within 0.5 m nothing moves. Everything after is EA's own
    // route: same destination, slot, timing, arrival.
    // Left to the game (counted by reason): forced replans (the world changed under the route), walk-style restarts, killed
    // actions, forcibly replanned routes, pets and horses (platform lanes), riders, carried / carrying Sims, slaves (strollers,
    // led horses), swimmers, a next path that is a portal (other than an elevator's own continuation) or does not continue
    // the portal's planned exit, undocked houseboats, unroutable, pool or below-ground spots, spots in view, household, selected
    // and unknown-scope Sims. A Sim seen at the moment of the decision gets EA's replan; nothing waits, so a reset or delete
    // cannot catch it mid-way (all in one call on the Sim's own task); no table, no saved state (counters only).
    // Counters 1456-1470: [1456] portal replans asked, [1457] snapped, [1458] near (no move), [1459-1464] refused: shape, next
    // path, scope, bad point, spot, seen; [1465] errors, [1466] longest snap (cm), [1467-1469] acts at elevators, doors / gates,
    // other portals, [1470] elevator cab leg followed by the same elevator's portal path (counted as a walk).
    // Cleanup: no loop, no retry; every exception returns EA's own result; ResetException passes through.
    public static partial class Fixes
    {
        static void ApplyLaneSnap()
        {
            Patch(typeof(PortalAction), "PerformAction", typeof(LaneSnapHook), "PerformActionHook", "PerformActionOrig");
            Patch(typeof(PortalTraversalAction), "PerformAction", typeof(LaneSnapTraversalHook), "PerformActionHook", "PerformActionOrig");
        }

        // the one place that decides; a = the portal action that just ran (mRoutingSim, mRoute, mPortalObject are its fields)
        static RouteAction.ActionResult LaneSnapAfter(PortalAction a, Sim sim, Route route, GameObject portal, bool forced, bool walkStyle,
            bool killed, RouteAction.ActionResult res)
        {
            if (res != RouteAction.ActionResult.ContinueAndPopPathAndReplan) return res;
            if (!ShortcutOn("unseenlanesnap")) return res;
            int[] c = PatchCounters();
            try
            {
                c[1456]++;
                bool shape = !forced && !walkStyle && !killed && LaneSnapShape(a, sim, route);
                bool unseen = shape && LaneSnapUnseen(sim);
                bool walk = false;
                float gap = float.NaN, dx = 0f, dy = 0f, dz = 0f;
                Vector3 at = Vector3.Invalid, s = Vector3.Invalid;
                if (shape)
                {
                    Vector3 e0 = Vector3.Invalid;
                    route.GetPathEndPoint(0u, ref e0);
                    route.GetPathStartPoint(1u, ref s);
                    walk = route.GetPathData(1u).PathType == PathType.SimPath;
                    // horizontal only: an elevator's portal path ends on the floor it left and the next path starts on the
                    // floor it reached
                    float gx = s.x - e0.x, gz = s.z - e0.z;
                    gap = (float)Math.Sqrt(gx * gx + gz * gz);
                    at = sim.PositionOnFloor;
                    dx = s.x - at.x; dy = s.y - at.y; dz = s.z - at.z;
                    // elevators: EA's OnPortalStart leaves the Sim in its assigned slot of the cab on the floor reached, and on
                    // another lane DoPortal swapped path 0 for the walk to that lane on the floor left (GetPathToLane), so the
                    // gap is the cab spacing, not a sign of a foreign leg. Path 1 continuing the ride = it starts on the Sim's
                    // floor (the reached cab's planned slot). A route through an elevator is walk, doors (into the cab), one
                    // cab leg per floor, doors (out), walk, so after a cab leg path 1 is often a portal path of the same
                    // elevator: that is the planned continuation, and DoRoute follows it after the snap as after a planned-lane
                    // ride.
                    if (portal != null && LaneSnapCore.PortalKind(portal.GetType().Name) == 0 && Math.Abs(dy) < LaneSnapCore.kMaxDY)
                    {
                        gap = 0f;
                        if (!walk && LaneSnapElevatorNext(route)) { walk = true; c[1470]++; }
                    }
                }
                bool spot = false, seenOk = false;
                if (LaneSnapCore.NeedsGeo(shape, unseen, walk, gap, dx, dy, dz))
                {
                    // the spot must still hold: on a docked houseboat if any, not below ground or in a pool, routable, out of view
                    Lot ls = LotManager.GetLotAtPoint(s);
                    spot = (ls == null || HouseboatOk(ls)) && !BelowGroundOutdoors(s) && route.IsPointRoutable(s);
                    seenOk = spot && UfGoalUnseen(s);
                }
                int why;
                int d = LaneSnapCore.Decide(shape, unseen, walk, gap, dx, dy, dz, spot, seenOk, out why);
                if (d == LaneSnapCore.kPass) { c[1458 + why]++; return res; }  // 1459 shape .. 1464 seen
                string kind = portal != null ? portal.GetType().Name : "none";
                if (d == LaneSnapCore.kSnap)
                {
                    int cm = (int)(Math.Sqrt(dx * dx + dz * dz) * 100f);
                    if (cm > c[1466]) c[1466] = cm;
                    PlaceSim(sim, s, sim.ForwardVector, "lanesnap " + kind);
                    c[1457]++;
                }
                else c[1458]++;
                c[1467 + LaneSnapCore.PortalKind(kind)]++;
                return RouteAction.ActionResult.ContinueAndPopPath;
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[1465]++; return res; }
        }

        // the replan only fixes the start point: no world change (forced), the route is the one DoRoute follows, a remainder to
        // follow, and a follower whose movement is the Sim's own
        static bool LaneSnapShape(PortalAction a, Sim sim, Route route)
        {
            if (sim == null || route == null || a.ForciblyReplanned || !sim.IsHuman || sim.SimDescription == null || sim.SimDescription.IsPet) return false;
            if (route.Follower == null || route.Follower.Target != sim || route.GetNumPaths() < 2 || route.IsLoop() || !route.PlanResult.Succeeded()) return false;
            if (sim.RoutingComponent == null || sim.RoutingComponent.RoutingParent != null || sim.Posture is SwimmingInPool) return false;
            return a.mSlaveObjectIds == null || a.mSlaveObjectIds.Count == 0;
        }

        // path 1 is a portal path of an elevator (its doors or the next floor's cab): the planned way on after a cab leg
        static bool LaneSnapElevatorNext(Route route)
        {
            PathData p1 = route.GetPathData(1u);
            if (p1.PathType != PathType.PortalPath) return false;
            return Sims3.Gameplay.GlobalFunctions.ConvertGuidToObject<GameObject>(p1.ObjectId) is Sims3.Gameplay.Interfaces.IElevatorObject;
        }

        // unseen townie: the unseen fallback's own rule (live mode, not household / selectable, lot, screen, camera reach, 80 m
        // viewers, not held, no seen social target) and a household: household, selected and unknown-scope Sims never match
        static bool LaneSnapUnseen(Sim sim) { return !IsPlayerSim(sim) && sim.Household != null && SafeUfWhy(sim) == 0; }

        // PortalAction.PerformAction (PortalAnimateThroughAction, PortalRouteThroughAction). The class only gives the hook access
        // to PortalAction's protected fields; at run time `this` is whichever PortalAction subclass is running.
        public class LaneSnapHook : PortalAnimateThroughAction
        {
            public ActionResult PerformActionHook()
            {
                ActionResult r = PerformActionOrig();
                return LaneSnapAfter(this, mRoutingSim, mRoute, mPortalObject, mbReplan, mbWalkStyleChanged,
                    mKillType != KillType.None && mKillType != KillType.Success, r);
            }
            public ActionResult PerformActionOrig() { return ActionResult.Terminate; } // receives PortalAction.PerformAction's original header
        }

        // PortalTraversalAction overrides PerformAction whole (doors, gates: "otherlane")
        public class LaneSnapTraversalHook : PortalTraversalAction
        {
            public ActionResult PerformActionHook()
            {
                ActionResult r = PerformActionOrig();
                return LaneSnapAfter(this, mRoutingSim, mRoute, mPortalObject, mbReplan, mbWalkStyleChanged,
                    mKillType != KillType.None && mKillType != KillType.Success, r);
            }
            public ActionResult PerformActionOrig() { return ActionResult.Terminate; } // receives PortalTraversalAction.PerformAction's original header
        }
    }
}
