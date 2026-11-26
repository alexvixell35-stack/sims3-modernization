using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // docs/853 §2 houseboatlegmemo (default ON). EA's SimRoutingComponent.PostPlanRouteCallback runs CheckAndUpdateForHouseboats
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
    // Counters [1378] recorded, [1379] answered, [1380] ms of the recorded plans those answers stand for, [1381] exempt or errors.
    // §3 houseboatgangwayrescue (HouseboatGangwayRescue.cs) shares HlmPre / HlmPost: a gangway leg recorded or answered here marks
    // the NPC for a rescue after GoHome fails; with the memo off and the rescue on, legs are only seen, never recorded or answered.
    public static partial class Fixes
    {
        static readonly HouseboatLegMemoCore sHlm = new HouseboatLegMemoCore();
        static int sHlmMinute = -1, sHlmLogged, sHlmStacks;

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

            // EnterHouseboatThroughBoat's leg options (the only EA PlanFromPoint that sets all three), or
            // EnterHouseboatThroughGangway's leg (docked houseboat, start connected by land): water and boat planning both
            // turned off on a fresh route, planned inside CheckAndUpdateForHouseboats (Houseboat.sHouseboatStackDepth > 0).
            // SetRouteOptions turns boat planning on for every child-or-older human, so a Sim route with both off at that
            // depth is the gangway leg. s122/s123: every slow stranded-NPC leg had all boat options off.
            bool HlmLegRoute()
            {
                if (GetOption2(Sims3.SimIFace.Route.RouteOption2.EnablePlanningAsBoat))
                    return GetOption2(Sims3.SimIFace.Route.RouteOption2.EndAsBoat) && GetOption2(Sims3.SimIFace.Route.RouteOption2.DestinationMustBeInDeepWater);
                if (Sims3.Gameplay.Objects.Island.Houseboat.sHouseboatStackDepth == 0) return false;
                if (GetOption(Sims3.SimIFace.Route.RouteOption.EnableWaterPlanning)) return false;
                IScriptProxy f = Follower;
                return f != null && f.Target is Sim;
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

            // before a kind-2 plan (UfPlan). true = r is the recorded failure; *leg = this is a boat leg of a non-exempt router.
            // memo = houseboatlegmemo on (answer and count), rescue = houseboatgangwayrescue on (an answered gangway leg marks the Sim,
            // HouseboatGangwayRescue.cs); with the memo off, only *leg is set, so HlmPost can mark a gangway leg after the plan
            bool HlmPre(Vector3 start, out RoutePlanResult r, out ulong leg, bool memo, bool rescue)
            {
                r = default(RoutePlanResult);
                leg = 0;
                int[] c = PatchCounters();
                try
                {
                    if (!HlmLegRoute()) return false;
                    string name;
                    ulong id = HlmRouter(out name);
                    if (id == 0) { if (memo) c[1381]++; return false; }  // exempt routers are never marked for the rescue either
                    leg = id;
                    if (!memo) return false;
                    Vector3 g = HlmGoal();
                    HouseboatLegMemoCore.Entry e;
                    if (!sHlm.Hit(id, start.x, start.y, start.z, g.x, g.y, g.z, SimClock.CurrentTicks, out e)) return false;
                    c[1379]++;
                    c[1380] += e.Ms;
                    HlmLog("answered, " + name + ", stands for " + e.Ms + " ms, goal " + (int)g.x + "," + (int)g.z);
                    r = FailFast(RoutePlanResultType.FailedBecauseNoPathToDestination);
                    leg = 0;  // answered: nothing to record after
                    if (rescue) { Sim gs = HgrLegSim(); if (gs != null) HgrNote(gs, 0); }
                    return true;
                }
                catch (Exception) { if (memo) c[1381]++; leg = 0; return false; }
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
                        + ", goal " + (HlmGoal() == Vector3.Origin ? "none" : "set") + ", follower " + (t != null ? t.GetType().Name : "null") + (sim != null ? " " + sim.FullName + (HouseholdExempt(sim) ? " exempt" : " npc") : "")
                        + ", depth " + Sims3.Gameplay.Objects.Island.Houseboat.sHouseboatStackDepth + ", posture " + (sim != null && sim.Posture != null ? sim.Posture.GetType().Name : "-")
                        + (sim != null && HgrLegSim() != null && Sims3.Gameplay.Objects.Island.Houseboat.sHouseboatStackDepth > 0 ? ", gangway leg" + HgrWhere(sim) : "")
                        + (sHlmStacks++ < 20 ? ", at " + HlmStack() : ""));
                }
                catch (Exception ex) { HlmLog("skip error " + ex.GetType().Name); }
            }

            // the game frames above UfPlan (Ts3Fixes frames left out), 12 at most: which EA code planned this leg
            static string HlmStack()
            {
                System.Text.StringBuilder b = new System.Text.StringBuilder();
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace(1, false);
                for (int i = 0, n = 0; i < st.FrameCount && n < 12; i++)
                {
                    System.Reflection.MethodBase m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    Type t = m.DeclaringType;
                    if (t != null && t.Namespace == "Ts3Fixes") continue;
                    if (b.Length > 0) b.Append(" < ");
                    b.Append((t != null ? t.Name : "?") + "." + m.Name);
                    n++;
                }
                return b.ToString();
            }

            // after the plan ran (leg != 0 from HlmPre): record a no-path failure that no Sim or vehicle blocked, and no cut
            // memo / rescue as in HlmPre: record the memo entry, mark the Sim of a gangway leg (HouseboatGangwayRescue.cs)
            void HlmPost(ulong leg, Vector3 start, RoutePlanResult r, int ms, bool cut, bool memo, bool rescue)
            {
                if (leg == 0 || r.mType != RoutePlanResultType.FailedBecauseNoPathToDestination) return;
                if (cut || r.mBlockerType == RouteBlockerType.Sim || r.mBlockerType == RouteBlockerType.Vehicle)
                {
                    if (memo) { Sim ls = HgrLegSim(); HlmLog("skip record " + ms + " ms, " + (cut ? "cut" : "blocker " + r.mBlockerType) + (ls != null ? ", gangway leg, start " + HgrXz(start) + HgrWhere(ls) : "")); }
                    return;
                }
                if (rescue) { Sim gs = HgrLegSim(); if (gs != null) HgrNote(gs, ms); }
                if (!memo) return;
                int[] c = PatchCounters();
                try
                {
                    Vector3 g = HlmGoal();
                    long life = SimClock.ConvertToTicks(Instantiator.kHouseboatLegMemoHours, TimeUnit.Hours);
                    sHlm.Record(leg, start.x, start.y, start.z, g.x, g.y, g.z, SimClock.CurrentTicks, life, ms);
                    c[1378]++;
                    Sim ls = HgrLegSim();
                    HlmLog("recorded, router " + leg.ToString("X") + ", " + (ls != null ? "gangway" : "boat") + " leg " + ms + " ms no path, goal " + (int)g.x + "," + (int)g.z
                        + ", " + Instantiator.kHouseboatLegMemoHours + " sim-h" + (ls != null ? ", start " + HgrXz(start) + HgrWhere(ls) : ""));
                }
                catch (Exception ex) { c[1381]++; HlmLog("error " + ex.GetType().Name); }
            }
        }
    }
}
