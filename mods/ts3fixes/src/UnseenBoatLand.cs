using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/687 unseenboatland (default off): an unseen townie's trip that EA may plan by boat is planned on land first (car or
    // walk, EnablePlanningAsBoat off). The land route stays when no boat trip could be much shorter: a boat route is never
    // shorter than the straight line, so straight >= kUnseenBoatLandRatio x land length proves the water trip is not under
    // that share of the road. Otherwise, or when the land plan fails or swims, the option goes back on and EA's plan runs as
    // before. Only trips whose ends share one land component (UfSameComponent 1): across water the land plan would fail after
    // a full search. Household, selected and unknown-scope Sims (Fixes.HouseholdExempt, no household) are never touched.
    // Counters [4642-4650], see Fixes.cs.
    public static partial class Fixes
    {
        static int sUblMinute = -1, sUblLogged;

        static void UblLog(string verdict, Sim sim, float straight, float land, int ms)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sUblMinute) { sUblMinute = minute; sUblLogged = 0; }
            if (sUblLogged++ >= 20) return;
            DiagLine("unseenboatland: " + verdict + ", " + (sim != null ? sim.FullName : "?") + ", straight " + (int)straight + " m, land "
                + (int)land + " m, ratio " + (land > 0f ? (straight / land).ToString("0.00") : "-") + ", land plan " + ms + " ms");
        }

        public partial class RouteBackoffHook
        {
            // PlanBackoff, after UbTrip. true = r is the land plan the caller returns; false = run EA's plan as before
            bool UblLand(Sim sim, out RoutePlanResult r)
            {
                r = default(RoutePlanResult);
                if (sStuckIn || sInOwnCheck || sInRoutableCheck || sUfInLeg || Simulator.CurrentTask != sim.ObjectId) return false;  // UbTrip's rule
                if (!GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat) || UfCarOrBoat(true)
                    || GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat) || GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater)) return false;
                Vector3 dest = GetApproximateOriginalDestination(), start = sim.Position;
                if (dest == Vector3.Origin || dest == Vector3.Invalid) return false;
                float straight = (dest - start).Length();
                if (!UbCore.FarEnough(straight) || sUfPlanDepth.Depth(Simulator.CurrentTask.Value) > 1) return false;
                int[] c = PatchCounters();
                try
                {
                    if (UblRefused(sim)) { c[4647]++; return false; }
                    Lot goal = LotManager.GetLotAtPoint(dest);
                    if ((goal == null || goal != sim.LotCurrent) && UfSameComponent(sim, start, dest, goal) != 1) { c[4646]++; return false; }  // water needed, or unknown
                }
                catch (Exception) { c[4650]++; return false; }
                c[4642]++;
                SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, false);
                int t0 = System.Environment.TickCount;
                bool keep = false;
                float land = 0f;
                try
                {
                    r = PlanBackoffBody(sim);
                    if (r.mType == RoutePlanResultType.Succeeded && !UblWater())
                    {
                        land = GetRouteLength();
                        keep = straight >= Instantiator.kUnseenBoatLandRatio * land;
                        if (!keep) c[4644]++;
                    }
                    else c[4645]++;
                }
                finally
                {
                    int ms = unchecked(System.Environment.TickCount - t0);
                    c[4648] += ms;
                    if (keep) c[4643]++;
                    else
                    {
                        // EA's plan runs next: the boat option back on, and the land failure must not fail it through backoff's memo
                        SetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat, true);
                        IScriptProxy f = Follower;
                        if (f != null) sBackoff.Remove(new BackoffKey(f.ObjectId.Value, dest));
                        c[4649] += ms;
                    }
                    UblLog(keep ? "land" : land > 0f ? "boat may be shorter, EA plan" : "land plan failed or swims, EA plan", sim, straight, land, ms);
                }
                return keep;
            }

            // a sub-path on the water: a boat leg, or a swim the planner took with the boat off
            bool UblWater()
            {
                uint n = GetNumPaths();
                for (uint i = 0; i < n; i++)
                {
                    PathType t = GetPathData(i).PathType;
                    if (t == PathType.BoatPath || t == PathType.OceanPath || t == PathType.WadePath) return true;
                }
                return false;
            }

            // household rule plus the trips that need the boat itself
            static bool UblRefused(Sim sim)
            {
                if (sim.Household == null || HouseholdExempt(sim)) return true;  // household, selected, unknown scope
                if (UfWhy(sim) != 0 || !TownieUnseen(sim, null)) return true;
                if (sim.Posture != null && UbCore.WaterPosture(sim.Posture.GetType().Name)) return true;
                if (sim.CurrentInteraction != null && UbCore.Denied(sim.CurrentInteraction.GetType().FullName)) return true;  // Boat Here, sail, fish, ...
                return UbOwnsBoat(sim);  // its own boat must come along (a generated NPC boat in its inventory: not, with unseenboatgenboat)
            }
        }
    }
}
