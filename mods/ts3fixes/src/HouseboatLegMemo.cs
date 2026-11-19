using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/642 §6 houseboatlegmemo (default off). EA's SimRoutingComponent.PostPlanRouteCallback runs CheckAndUpdateForHouseboats
    // after every Plan / Replan / ReplanFromPoint of a Sim or boat whose start or goal is a houseboat lot. Its boat entry
    // (EnterHouseboatThroughBoat) plans a new route from the router's start with PlanFromPoint: EnablePlanningAsBoat, EndAsBoat,
    // DestinationMustBeInDeepWater, a 5-15 m radial goal off the houseboat's RoutingSlot_0. Isla Paradiso phase-20261119-0746:
    // NPCs stranded ~17 m from their houseboat (Britta Colombo 18x, Rubisel Ichtaca 6x) get that leg planned again on every try
    // home, ~600 ms each, always FailedBecauseNoPathToDestination; EnterHouseboatThroughBoat then returns false and the outer
    // route stays as it was. The leg is an own-task kind 2 (Kinds=0: START refused NoStart; WatchdogKind2 sends the sync call
    // straight to the game IC, no SyncIc, so no unseen-fallback session), and no exit cut it ("world A* capped 0").
    // This memo: after such a boat-leg plan of an NPC router finds no path (no Sim or vehicle blocker), (router, start, goal)
    // is remembered for kHouseboatLegMemoHours sim-hours; the same plan from the same start to the same goal (0.5 m) is answered
    // with that failure without searching. EnterHouseboatThroughBoat reads only Succeeded(), so the outer route ends as before.
    // Household, selected or unknown-scope routers (Fixes.HouseholdExempt; a boat by every occupant, none aboard = unknown):
    // never recorded, never answered. Successes are never memoed; an answer does not extend its entry.
    // Counters [3870] recorded, [3871] answered, [3872] ms of the recorded plans those answers stand for, [3873] exempt or errors.
    public static partial class Fixes
    {
        static readonly HouseboatLegMemoCore sHlm = new HouseboatLegMemoCore();
        static int sHlmMinute = -1, sHlmLogged;

        static void HlmLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sHlmMinute) { sHlmMinute = minute; sHlmLogged = 0; }
            if (sHlmLogged++ < 10) DiagLine("houseboatlegmemo: " + s);
        }

        public partial class RouteBackoffHook
        {
            // the leg's goal for the memo key: a radial-range leg that found no path has no original destination (Invalid):
            // then the key is router + start alone (the leg always aims at the router's own houseboat slot)
            Vector3 HlmGoal()
            {
                Vector3 g = GetApproximateOriginalDestination();
                return g == Vector3.Invalid || float.IsNaN(g.x) || float.IsInfinity(g.x) ? Vector3.Origin : g;
            }

            // EnterHouseboatThroughBoat's leg options (the only EA PlanFromPoint that sets all three)
            bool HlmLegRoute()
            {
                return GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat) && GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat)
                    && GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater);
            }

            // the router's id, or 0 = not ours (no Sim or boat follower) / exempt (household, selected, unknown scope)
            ulong HlmRouter(out string name)
            {
                name = null;
                IScriptProxy f = Follower;
                object t = f != null ? f.Target : null;
                Sim sim = t as Sim;
                if (sim != null)
                {
                    if (HouseholdExempt(sim)) return 0;
                    name = sim.FullName;
                    return sim.ObjectId.Value;
                }
                Boat boat = t as Boat;
                if (boat == null) return 0;
                Sim[] occ = boat.Occupants;
                if (occ == null || occ.Length == 0) return 0;
                for (int i = 0; i < occ.Length; i++)
                    if (occ[i] == null || HouseholdExempt(occ[i])) return 0;
                name = boat.GetType().Name + (boat.Driver != null ? " " + boat.Driver.FullName : "");
                return boat.ObjectId.Value;
            }

            // before a kind-2 plan (UfPlan). true = r is the recorded failure; *leg = this is a boat leg of a non-exempt router
            bool HlmPre(Vector3 start, out RoutePlanResult r, out ulong leg)
            {
                r = default(RoutePlanResult);
                leg = 0;
                int[] c = PatchCounters();
                try
                {
                    if (!HlmLegRoute()) return false;
                    string name;
                    ulong id = HlmRouter(out name);
                    if (id == 0) { c[3873]++; return false; }
                    leg = id;
                    Vector3 g = HlmGoal();
                    HouseboatLegMemoCore.Entry e;
                    if (!sHlm.Hit(id, start.x, start.y, start.z, g.x, g.y, g.z, SimClock.CurrentTicks, out e)) return false;
                    c[3871]++;
                    c[3872] += e.Ms;
                    HlmLog("answered, " + name + ", stands for " + e.Ms + " ms, goal " + (int)g.x + "," + (int)g.z);
                    r = FailFast(RoutePlanResultType.FailedBecauseNoPathToDestination);
                    leg = 0;  // answered: nothing to record after
                    return true;
                }
                catch (Exception) { c[3873]++; leg = 0; return false; }
            }

            // a slow kind-2 no-path plan HlmPre did not take: log why (options, follower, scope), so a silent skip shows in the log
            void HlmMiss(RoutePlanResult r, int ms)
            {
                if (ms < 300 || r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) return;
                try
                {
                    IScriptProxy f = Follower;
                    object t = f != null ? f.Target : null;
                    Sim sim = t as Sim;
                    HlmLog("skip " + ms + " ms, opts boat " + GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat)
                        + " endboat " + GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat)
                        + " deep " + GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater)
                        + " noland " + GetOption2(Sims3.SimIFace.Route.RouteOption2.DisablePlanningOnLand)
                        + ", goal " + (HlmGoal() == Vector3.Origin ? "none" : "set") + ", follower " + (t != null ? t.GetType().Name : "null") + (sim != null ? " " + sim.FullName + (HouseholdExempt(sim) ? " exempt" : " npc") : ""));
                }
                catch (Exception ex) { HlmLog("skip error " + ex.GetType().Name); }
            }

            // after the plan ran (leg != 0 from HlmPre): record a no-path failure that no Sim or vehicle blocked, and no cut
            void HlmPost(ulong leg, Vector3 start, RoutePlanResult r, int ms, bool cut)
            {
                if (leg == 0 || r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) return;
                if (cut || r.mBlockerType == RouteBlockerType.Sim || r.mBlockerType == RouteBlockerType.Vehicle)
                {
                    HlmLog("skip record " + ms + " ms, " + (cut ? "cut" : "blocker " + r.mBlockerType));
                    return;
                }
                int[] c = PatchCounters();
                try
                {
                    Vector3 g = HlmGoal();
                    long life = SimClock.ConvertToTicks(Instantiator.kHouseboatLegMemoHours, TimeUnit.Hours);
                    sHlm.Record(leg, start.x, start.y, start.z, g.x, g.y, g.z, SimClock.CurrentTicks, life, ms);
                    c[3870]++;
                    HlmLog("recorded, router " + leg.ToString("X") + ", boat leg " + ms + " ms no path, goal " + (int)g.x + "," + (int)g.z
                        + ", " + Instantiator.kHouseboatLegMemoHours + " sim-h");
                }
                catch (Exception ex) { c[3873]++; HlmLog("error " + ex.GetType().Name); }
            }
        }
    }
}
