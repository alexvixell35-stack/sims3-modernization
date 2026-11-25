using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Stairs without the stop and start animations when the walk already heads along the lane (docs/686 §32).
    // [verified, decompiled Sims3.Gameplay.Routing] PortalAnimateThroughAction.DoPortal walks the approach to the lane start
    // with RouteStartFlags.UseStopAnim unless the portal is an IPortalIgnoreStartStopAnim (horse jumps, houseboats), and
    // FollowRouteAction.GetDefaultRouteStartFlags gives the walk after a portal UseStartAnim while the portal action's
    // UseStartAnimWhenExitingPortal is true (false for horse jumps, the ocean, houseboats). So a Sim using straight stairs plays
    // walk -> stop -> stand -> stair walk -> stand -> start -> walk, even when it walks straight in and straight out. The stair
    // walk itself (Route.StartStairTraversal, native) places the Sim at the lane start facing along the lane.
    //   stairflow (stairflowhh for household and selected Sims): straight stairs only (exact StairsPortalComponent), the game's
    //     planned lane only: when the approach's last heading is within kStairFlowDeg of the lane, the approach is walked
    //     without the stop animation; when the walk after the stairs starts within kStairFlowDeg of the lane, it starts without
    //     the start animation. Both are flags EA itself clears for other portals; no animation is changed, nothing is moved.
    //     A larger angle keeps EA's stop-and-face (counted "kept"). Ladders and spiral stairs: unchanged (their climbs are
    //     interactions that place the Sim themselves).
    //   stairtimelog: one line per use of stairs, a ladder or spiral stairs (either switch state): approach time and length,
    //     its end heading against the lane, the time to the first step, the angle and distance left at the first step, the
    //     time on the stairs, the exit flag. Times in sim-minutes (= seconds at speed 1).
    // Counters 4692-4699 (kCounterSlots 4700). Check: StairFlowCheck.
    public static partial class Fixes
    {
        const int kSflUses = 4692, kSflNoStop = 4693, kSflWould = 4694, kSflKept = 4695, kSflNoStart = 4696, kSflExitWould = 4697, kSflExempt = 4698, kSflErrors = 4699;

        sealed class SflUse
        {
            public object Action;     // the portal action (its exit flag is read on it)
            public string Kind;
            public bool Straight, NoStop, NoStart, Up;
            public float T0, T1 = -1f, ApproachLen, ApproachDeg = -1f, StepDeg = -1f, StepOff = -1f, ExitDeg = -1f;
            public Vector3 LaneA, LaneB;
            public uint Lane;
        }
        static readonly Dictionary<ulong, SflUse> sSflUseOf = new Dictionary<ulong, SflUse>();  // per Sim: its last portal use
        static readonly List<object> sSflNoStopActions = new List<object>(4);                     // approaches walked without the stop anim
        static readonly LineCapCore sSflCap = new LineCapCore(30, 60000, 400);
        static int sSflN, sSflStairsN;
        static double sSflApproachSum, sSflStairsSum, sSflStepDegSum;
        static float sSflStepOffMax;
        static string sSflMissing = "", sSflLast = "";

        static void ApplyStairFlow()
        {
            // DoPortal is swapped by ApplyPortalAdaptorLeak (its hook calls SflBefore / SflAfter); the stair start by
            // ApplyStairWaitEnd (its hook calls SflStep). Two swaps of our own here.
            if (!Patch(typeof(FollowRouteAction), "PerformRoute", new Type[] { typeof(RouteStartFlags) }, typeof(SflRouteHook), "PerformHook", "PerformOrig"))
                sSflMissing = (sSflMissing + " FollowRouteAction.PerformRoute").Trim();
            if (!Patch(typeof(PortalAnimateThroughAction), "get_UseStartAnimWhenExitingPortal", Type.EmptyTypes, typeof(SflExitHook), "ExitHook", "ExitOrig"))
                sSflMissing = (sSflMissing + " PortalAnimateThroughAction.UseStartAnimWhenExitingPortal").Trim();
        }

        static bool SflWatched(PortalComponent pc, out string kind, out bool straight)
        {
            kind = null; straight = false;
            if (pc == null) return false;
            Type t = pc.GetType();
            if (t == typeof(Stairs.StairsPortalComponent)) { kind = "Stairs"; straight = true; }
            else if (t == typeof(Ladder.LadderPortalComponent)) kind = "Ladder";
            else if (t == typeof(StaircaseSpiral.StaircaseSpiralPortalComponent)) kind = "SpiralStairs";
            return kind != null;
        }

        static float SflAngle(Vector3 a, Vector3 b) { return StairFlowCore.AngleDeg(a.x, a.z, b.x, b.z); }
        static float SflNow() { return SimClock.ElapsedTime(TimeUnit.Minutes); }

        // DoPortal's hook, before the game's body: the approach is path 0 of the route.
        internal static void SflBefore(object action, Sim sim, Route route, GameObject portal, ref PortalComponent.LaneInfo laneInfo, bool preferredLaneSelected)
        {
            try
            {
                string kind; bool straight;
                if (sim == null || route == null || portal == null || !SflWatched(portal.PortalComponent, out kind, out straight)) return;
                int[] c = PatchCounters();
                c[kSflUses]++;
                SflUse u = new SflUse();
                u.Action = action; u.Kind = kind; u.Straight = straight; u.T0 = SflNow(); u.Lane = laneInfo.mLaneIndex;
                if (laneInfo.mLaneSlots != null && laneInfo.mLaneSlots.Length >= 2)
                {
                    u.LaneA = portal.GetPositionOfSlot(laneInfo.mLaneSlots[0]);
                    u.LaneB = portal.GetPositionOfSlot(laneInfo.mLaneSlots[1]);
                    u.Up = u.LaneA.y <= u.LaneB.y;
                }
                if (route.GetNumPaths() > 0)
                {
                    u.ApproachLen = route.GetPathLength(0);
                    Vector3 end = Vector3.Zero;
                    route.GetPathEndDirection(0, ref end);
                    if (u.LaneA != u.LaneB) u.ApproachDeg = SflAngle(end, u.LaneB - u.LaneA);
                }
                sSflUseOf[sim.ObjectId.Value] = u;
                // the game replans the approach itself (another lane, or the lane says so): its heading is not known here
                if (!straight || u.ApproachDeg < 0f || !preferredLaneSelected || laneInfo.ForcePortalPlan) return;
                int d = StairFlowCore.Decide(ShortcutOn("stairflow"), ShlScope(sim, ShortcutOn("stairflowhh")), true, u.ApproachDeg, Instantiator.kStairFlowDeg);
                if (d == StairFlowCore.kKept) c[kSflKept]++;
                else if (d == StairFlowCore.kWould) c[kSflWould]++;
                else if (d == StairFlowCore.kExempt) c[kSflExempt]++;
                else if (d == StairFlowCore.kGo) { u.NoStop = true; sSflNoStopActions.Add(action); c[kSflNoStop]++; }
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kSflErrors]++; }
        }

        // The stair start's hook (StairsPortalComponent.OnPortalStart), before the game's body: the first step.
        internal static void SflStep(Stairs.StairsPortalComponent pc, Sim s)
        {
            try
            {
                if (s == null) return;
                SflUse u;
                if (!sSflUseOf.TryGetValue(s.ObjectId.Value, out u)) return;
                if (u.T1 >= 0f) return;
                u.T1 = SflNow();
                if (u.LaneA != u.LaneB)
                {
                    u.StepDeg = SflAngle(s.ForwardVector, u.LaneB - u.LaneA);
                    u.StepOff = (s.Position - u.LaneA).Length();
                }
                Route r = s.RoutingComponent == null ? null : s.RoutingComponent.GetCurrentRoute();
                if (r == null || r.GetNumPaths() < 2 || u.LaneA == u.LaneB) return;
                Vector3 outDir = Vector3.Zero;
                r.GetPathStartDirection(1u, ref outDir);
                u.ExitDeg = SflAngle(outDir, u.LaneB - u.LaneA);
                int[] c = PatchCounters();
                int d = StairFlowCore.Decide(ShortcutOn("stairflow"), ShlScope(s, ShortcutOn("stairflowhh")), u.Straight, u.ExitDeg, Instantiator.kStairFlowDeg);
                if (d == StairFlowCore.kKept) c[kSflKept]++;
                else if (d == StairFlowCore.kWould) c[kSflExitWould]++;
                else if (d == StairFlowCore.kExempt) c[kSflExempt]++;
                else if (d == StairFlowCore.kGo) u.NoStart = true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kSflErrors]++; }
        }

        // DoPortal's hook, after the game's body.
        internal static void SflAfter(object action, Sim sim, string result)
        {
            try
            {
                sSflNoStopActions.Remove(action);
                if (sim == null) return;
                SflUse u;
                if (!sSflUseOf.TryGetValue(sim.ObjectId.Value, out u) || u.Action != action) return;
                float t2 = SflNow();
                float approach = (u.T1 >= 0f ? u.T1 : t2) - u.T0, on = u.T1 >= 0f ? t2 - u.T1 : -1f;
                sSflN++; sSflApproachSum += approach;
                if (on >= 0f) { sSflStairsN++; sSflStairsSum += on; sSflStepDegSum += u.StepDeg; if (u.StepOff > sSflStepOffMax) sSflStepOffMax = u.StepOff; }
                StringBuilder b = new StringBuilder(220);
                b.Append("stairtime: ").Append(sim.FullName).Append(' ').Append(ShlWho(sim)).Append(", ").Append(u.Kind).Append(u.Up ? " up" : " down")
                    .Append(" lane ").Append(u.Lane).Append(": approach ").Append(approach.ToString("0.00")).Append(" s over ").Append(u.ApproachLen.ToString("0.0"))
                    .Append(" m, stop anim ").Append(u.NoStop ? "no" : "yes").Append(", end heading ").Append(u.ApproachDeg.ToString("0")).Append(" deg off the lane");
                if (on >= 0f)
                    b.Append(", first step ").Append(u.StepDeg.ToString("0")).Append(" deg / ").Append(u.StepOff.ToString("0.00")).Append(" m from the lane start, on the stairs ")
                        .Append(on.ToString("0.00")).Append(" s, exit heading ").Append(u.ExitDeg.ToString("0")).Append(" deg, start anim ").Append(u.NoStart ? "no" : "yes");
                else b.Append(", climb included");
                b.Append(", result ").Append(result).Append(", at ").Append(V(sim.Position));
                sSflLast = b.ToString();
                if (ShortcutOn("stairtimelog") && sSflCap.Take(System.Environment.TickCount)) DiagLine(sSflLast);
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kSflErrors]++; }
        }

        // stairflow: the approach to straight stairs walked without the stop animation (the flag only, for the action SflBefore marked)
        public abstract class SflRouteHook : FollowRouteAction
        {
            public ActionResult PerformHook(RouteStartFlags routeStartFlags)
            {
                if ((routeStartFlags & RouteStartFlags.UseStopAnim) != 0 && this is PortalAnimateThroughAction)
                {
                    bool drop;
                    drop = sSflNoStopActions.Remove(this);
                    if (drop) routeStartFlags &= ~RouteStartFlags.UseStopAnim;
                }
                return PerformOrig(routeStartFlags);
            }
            public ActionResult PerformOrig(RouteStartFlags routeStartFlags) { return ActionResult.Terminate; }  // receives PerformRoute's original body
        }

        // stairflow: the walk right after straight stairs starts without the start animation (the flag only, once per marked use)
        public class SflExitHook : PortalAnimateThroughAction
        {
            public bool ExitHook()
            {
                try
                {
                    Sim s = mRoutingSim;
                    SflUse u = null;
                    if (s != null) sSflUseOf.TryGetValue(s.ObjectId.Value, out u);
                    if (u != null && u.NoStart && u.Action == (object)this)
                    {
                        u.NoStart = false;  // once: the walk right after the stairs
                        PatchCounters()[kSflNoStart]++;
                        return false;
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[kSflErrors]++; }
                return ExitOrig();
            }
            public bool ExitOrig() { return true; }  // receives the property getter's original body
        }

        static string SflCounters()
        {
            int[] c = PatchCounters();
            return "stairflow: portal uses " + c[kSflUses] + ", approaches without the stop anim " + c[kSflNoStop] + " (would, off " + c[kSflWould]
                + "), exits without the start anim " + c[kSflNoStart] + " (would, off " + c[kSflExitWould] + "), kept (angle over " + Instantiator.kStairFlowDeg.ToString("0")
                + " deg) " + c[kSflKept] + ", exempt " + c[kSflExempt] + ", errors " + c[kSflErrors]
                + "; this session: " + sSflN + " timed, approach mean " + (sSflN > 0 ? sSflApproachSum / sSflN : 0).ToString("0.00")
                + " s, straight stairs " + sSflStairsN + " on the stairs mean " + (sSflStairsN > 0 ? sSflStairsSum / sSflStairsN : 0).ToString("0.00")
                + " s, first-step angle mean " + (sSflStairsN > 0 ? sSflStepDegSum / sSflStairsN : 0).ToString("0") + " deg, max offset " + sSflStepOffMax.ToString("0.00")
                + " m; " + GlMembers(sSflMissing) + (sSflLast.Length > 0 ? "; last: " + sSflLast : "");
        }

        // Route.IsPointRoutable at both ends of each lane of the links (EA's answer for this Sim's route, Sims included).
        static string SflEnds(Route p, List<LevelLink> links)
        {
            StringBuilder b = new StringBuilder("lane ends routable:");
            foreach (LevelLink k in links)
            {
                b.Append(' ').Append(k.Type);
                for (uint i = 0; k.Ends && i < k.Lanes && i < 3; i++)
                {
                    Slot[] s = LevelLinkLane(k, i);
                    if (s == null) continue;
                    b.Append(" [").Append(i).Append(": A ").Append(p.IsPointRoutable(k.Obj.GetSlotPosition(s[0])) ? "yes" : "NO")
                        .Append(", B ").Append(p.IsPointRoutable(k.Obj.GetSlotPosition(s[1])) ? "yes" : "NO").Append(']');
                }
            }
            return b.ToString();
        }

        // StairFlowCheck result: counters and the session's timing. plan <sim hex id> x y z: a test plan from the Sim to the point
        // (nobody moves), with Route.IsPointRoutable at every lane end of the level links of the Sim's lot (the ladder lead of
        // docs/670 §29.3). run: the rules with controls.
        public static object StairFlowCheck(string what)
        {
            try
            {
                if (what == null || what == "result") return SflCounters();
                string[] w = what.Split(' ');
                if (w[0] == "run")
                {
                    bool ok = StairFlowCore.Decide(true, true, true, 10f, 30f) == StairFlowCore.kGo
                        && StairFlowCore.Decide(true, true, true, 60f, 30f) == StairFlowCore.kKept
                        && StairFlowCore.Decide(false, true, true, 10f, 30f) == StairFlowCore.kWould
                        && StairFlowCore.Decide(true, false, true, 10f, 30f) == StairFlowCore.kExempt
                        && StairFlowCore.Decide(true, true, false, 0f, 30f) == StairFlowCore.kNo;
                    return "StairFlowCheck run: rules " + (ok ? "ok" : "WRONG") + ", switch stairflow " + (ShortcutOn("stairflow") ? "on" : "off") + ", stairflowhh "
                        + (ShortcutOn("stairflowhh") ? "on" : "off") + "; " + SflCounters();
                }
                if (w[0] == "plan" && w.Length >= 5)
                {
                    string hex = w[1].StartsWith("0x") ? w[1].Substring(2) : w[1];
                    ulong id;
                    if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out id)) return "plan: bad id";
                    Sim sim = null;
                    foreach (Sim a in LotManager.Actors) if (a != null && a.SimDescription != null && (a.ObjectId.Value == id || a.SimDescription.SimDescriptionId == id)) { sim = a; break; }
                    if (sim == null) return "plan: no Sim " + hex;
                    Vector3 goal = new Vector3(float.Parse(w[2]), float.Parse(w[3]), float.Parse(w[4]));
                    Lot lot = sim.LotCurrent;
                    List<LevelLink> links = new List<LevelLink>();
                    int portals;
                    if (lot != null && !lot.IsWorldLot) foreach (LevelLink k in LevelLinks(lot, out portals)) if (k.TwoLevels) links.Add(k);
                    Route p = sim.CreateRoute();
                    p.DoRouteFail = false;
                    p.ExecutionFromNonSimTaskIsSafe = true;
                    RoutePlanResult res = p.PlanToPoint(goal);
                    StringBuilder near = new StringBuilder();
                    foreach (LevelLink k in links)
                        foreach (Vector3 e in new Vector3[] { k.A, k.Z })
                        foreach (Sim o in Sims3.Gameplay.Queries.GetObjects<Sim>(e, 1.5f))
                            if (o != null && o != sim) near.Append(' ').Append(o.FullName).Append('@').Append(V(o.Position)).Append(o.SimRoutingComponent != null && o.SimRoutingComponent.IsDynamicFootprintEnabled() ? " fp on" : " fp off")
                                .Append(o.SimRoutingComponent != null && o.SimRoutingComponent.IsRouting ? " routing" : "");
                    string line = "stairflow plan: " + sim.FullName + " at " + V(sim.Position) + " to " + V(goal) + ": " + (res.Succeeded() ? "SUCCEEDS" : "fails " + res.mType)
                        + "; " + SflEnds(p, links) + "; Sims within 1.5 m of the lane ends:" + (near.Length > 0 ? near.ToString() : " none");
                    DiagLine(line);
                    return line;
                }
                return "StairFlowCheck: result | run | plan <sim hex id> x y z";
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "StairFlowCheck: " + e.GetType().Name + ": " + e.Message; }
        }
    }
}
