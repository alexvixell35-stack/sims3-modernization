using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Pools;
using Sims3.Gameplay.Routing;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Where route plans come from and three cuts of redundant ones (docs/151 §4-§6).
    //
    // routesrc (observe only, off): at each of the four plan ICs (RouteBackoffHook) the caller is classified from up to 10
    // managed frames above our pass-through frames into the docs/151 §2 buckets S1-S14 (V1: plans, wall us from the IC
    // hook incl. nested houseboat legs and async waits, fails per bucket), repeats per follower (V2), the action that
    // asked for a leg-end replan and how far the remainder starts from the Sim (V3), houseboat fixup legs (V4), the
    // bouncer already at its slot (V5) and watchdog checks a successful plan already answered (V7). Per-bucket data in
    // AppDomain "ts3fixes.routesrc" (int[64], layout at kSrc*); totals in the main counters 228-237.
    //
    // legreplanskip (D1, off): BoatFollowSubPathRouteAction / VehicleFollowSubPathRouteAction return
    // ContinueAndPopPathAndReplan after a get-out when ReplanWhenFinished is set (Boat.GetOutOntoLand / GetOutIntoWater on
    // every boat exit, Car/Bicycle/MagicBroom when FGL moved the get-out spot); RoutingComponent.DoRoute then pops the leg
    // and replans the rest from the Sim's spot. When the planned remainder already starts within 0.5 m of the Sim (and
    // RouteSrcCore.LegSkipEligible holds), the hook returns plain ContinueAndPopPath: DoRoute pops the leg and pushes the
    // remainder's actions (its code path after a successful replan), no ReplanFromPoint. Portal mbReplan untouched.
    //
    // bouncerskip (D3, off): VelvetRopes+WatchDoor.LoopDel re-routes the bouncer to RoutingSlot_2 every 5 s loop; skipped
    // when the bouncer already stands on the slot facing its way (RouteSrcCore.AtSlot).
    //
    // idlememo (D5, off; RouteFixes.cs): the watchdog's idle routability check is skipped for a Sim that planned
    // successfully from within 1 m of its spot since it went still, and at most 4 checks run per pass (16 without).
    //
    // D2 (houseboat direct legs) is not built: spec only, docs/151 §6.2.
    //
    // Counters: [228] plans classified, [229] classifier us summed (overhead), [230] V3 leg-end replans D1 could skip
    // (eligible, remainder < 0.5 m), [231] V4 houseboat fixup leg plans, [232] V5 bouncer loop routes with the bouncer at
    // its slot, [233] V7 watchdog checks a successful plan from the spot already answered, [234] D1 replans skipped,
    // [235] D3 bouncer routes skipped, [236] D5 watchdog checks skipped by the memo, [237] D5 queued Sims left for a later
    // pass by the cap of 4.
    public static partial class Fixes
    {
        const int kSrcPlans = 0, kSrcUs = 16, kSrcFails = 32, kSrcRepeat = 48, kSrcLegAction = 54, kSrcLegDist = 60, kSrcHbReplan = 63;

        static void ApplyRouteSources()
        {
            Patch(typeof(BoatFollowSubPathRouteAction), "PerformAction", typeof(BoatLegHook), "PerformActionHook", "PerformActionOrig");
            Patch(typeof(VehicleFollowSubPathRouteAction), "PerformAction", typeof(VehicleLegHook), "PerformActionHook", "PerformActionOrig");
            Type watch = typeof(VelvetRopes).GetNestedType("WatchDoor", BindingFlags.NonPublic);
            if (watch == null) { AppliedLog().Add("VelvetRopes+WatchDoor: not found, bouncerskip off"); return; }
            Patch(watch, "LoopDel", typeof(BouncerHook), "LoopDelHook", "LoopDelOrig");
            Patch(watch, "Run", Type.EmptyTypes, typeof(BouncerRunHook), "RunHook", "RunOrig");  // docs/633 bouncerplace
        }

        static int[] sRouteSrc;
        static int[] RouteSrcData()
        {
            if (sRouteSrc != null) return sRouteSrc;
            int[] a = AppDomain.CurrentDomain.GetData("ts3fixes.routesrc") as int[];
            if (a == null) { a = new int[64]; AppDomain.CurrentDomain.SetData("ts3fixes.routesrc", a); }
            return sRouteSrc = a;
        }

        static StopWatch sSrcWatch;
        static uint SrcNowUs()
        {
            if (sSrcWatch == null) { sSrcWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds); sSrcWatch.Start(); }
            return sSrcWatch.GetElapsedTime(); // free-running; differences are unchecked uint (wraps after ~71 min)
        }

        struct RouteSrcCall { public int Bucket, Kind; public uint T0; public Vector3 Goal; }
        static readonly List<string> sSrcFrames = new List<string>(10);
        static readonly RepeatRing sRepeats = new RepeatRing();

        // At the IC hook, before the plan. Bucket -1 = routesrc off (End does nothing).
        static RouteSrcCall RouteSrcBegin(ScriptCore.Route r, int kind, Vector3 fromPt)
        {
            RouteSrcCall call = default(RouteSrcCall);
            call.Bucket = -1;
            if (kind == 3) HcBoatBegin(r, fromPt); // boatreplanlog (HitchCause.cs)
            else if (kind == 1) RlBegin(r); // replanlog (ReplanLog.cs, docs/261)
            if (!ShortcutOn("routesrc")) return call;
            try
            {
                uint t = SrcNowUs();
                List<string> frames = sSrcFrames;
                frames.Clear();
                bool started = false;
                for (int i = 1; i < 48 && frames.Count < 10; i++)
                {
                    MethodBase m = new StackFrame(i, false).GetMethod();
                    if (m == null) break;
                    string ft = m.DeclaringType != null ? m.DeclaringType.FullName : null;
                    if (!started && RouteSrcCore.IsPassThrough(ft, m.Name)) continue;
                    started = true;
                    frames.Add(ft + "." + m.Name);
                }
                call.Kind = kind;
                call.Bucket = RouteSrcCore.Classify(frames, kind);
                call.Goal = r.GetApproximateOriginalDestination();
                int[] a = RouteSrcData(), c = PatchCounters();
                if (call.Bucket == RouteSrcCore.kLegEnd) LegEndCounts(r, fromPt, a);
                else if (call.Bucket == RouteSrcCore.kHouseboat)
                {
                    c[231]++;
                    for (int i = 1; i < frames.Count; i++)
                        if (frames[i].IndexOf(".Replan", StringComparison.Ordinal) >= 0) { a[kSrcHbReplan]++; break; }
                }
                c[229] += (int)unchecked(SrcNowUs() - t);
                call.T0 = SrcNowUs();
            }
            catch (Exception) { call.Bucket = -1; }
            return call;
        }

        // V3 at the leg-end replan: the action DoRoute just ran (its previous action) and the distance from the replan
        // point (the Sim's spot) to the start of the remainder, which DoRoute has already popped to path 0.
        static void LegEndCounts(ScriptCore.Route r, Vector3 fromPt, int[] a)
        {
            IScriptProxy f = r.Follower;
            GameObject o = f != null ? f.Target as GameObject : null;
            RouteAction prev = o != null && o.RoutingComponent != null ? o.RoutingComponent.GetPreviousRouteAction() : null;
            int type = prev is BoatFollowSubPathRouteAction ? 0 : prev is VehicleFollowSubPathRouteAction ? 1 : prev is PortalTraversalAction ? 2
                : prev is PortalAction ? 3 : prev != null && prev.GetType().Name == "GetInBoatRouteAction" ? 4 : 5;
            a[kSrcLegAction + type]++;
            if (r.GetNumPaths() == 0) return;
            Vector3 s = Vector3.Invalid;
            r.GetPathStartPoint(0u, ref s);
            a[kSrcLegDist + RouteSrcCore.DistanceBand(s.x - fromPt.x, s.z - fromPt.z)]++;
        }

        static void RouteSrcEnd(ScriptCore.Route r, RouteSrcCall call, bool ok)
        {
            RouteLenEnd(r, ok);  // routelen counters (RouteLen.cs, docs/246 §6)
            if (call.Bucket < 0) return;
            try
            {
                int[] a = RouteSrcData();
                a[kSrcPlans + call.Bucket]++;
                a[kSrcUs + call.Bucket] += (int)unchecked(SrcNowUs() - call.T0);
                if (!ok) a[kSrcFails + call.Bucket]++;
                PatchCounters()[228]++;
                IScriptProxy f = r.Follower;
                if (f != null)
                {
                    int rep = sRepeats.Note(f.ObjectId.Value, call.Kind, call.Goal.x, call.Goal.y, call.Goal.z, SrcNowUs() / 1000, ok);
                    if (rep >= 0) a[kSrcRepeat + rep]++;
                }
            }
            catch (Exception) { }
        }

        // D1 / V3 share. wasSim = the route's follower was a Sim before the action ran.
        static RouteAction.ActionResult LegEnd(FollowRouteAction action, Route route, RouteAction.ActionResult res, bool replanWhenFinished, bool wasSim)
        {
            if (res != RouteAction.ActionResult.ContinueAndPopPathAndReplan) return res;
            bool src = ShortcutOn("routesrc"), skip = ShortcutOn("legreplanskip");
            if (!src && !skip) return res;
            try
            {
                Sim sim = route.Follower != null ? route.Follower.Target as Sim : null;
                if (sim == null || sim.RoutingComponent == null) return res;
                if (!RouteSrcCore.LegSkipEligible(true, replanWhenFinished, action.ForciblyReplanned, route.GetNumPaths(), route.IsLoop(),
                        route.PlanResult.Succeeded(), wasSim, sim.RoutingComponent.RoutingParent != null, sim.Posture is SwimmingInPool))
                    return res;
                Vector3 p = sim.PositionOnFloor, s = Vector3.Invalid;
                route.GetPathStartPoint(1u, ref s);
                if (!RouteSrcCore.LegSkipNear(s.x - p.x, s.y - p.y, s.z - p.z)) return res;
                int[] c = PatchCounters();
                c[230]++;
                if (!skip) return res;
                c[234]++;
                return RouteAction.ActionResult.ContinueAndPopPath;
            }
            catch (Exception) { return res; }
        }

        static bool FollowerIsSim(Route r) { return r != null && r.Follower != null && r.Follower.Target is Sim; }

        public class BoatLegHook : BoatFollowSubPathRouteAction
        {
            public ActionResult PerformActionHook()
            {
                bool wasSim = FollowerIsSim(mRoute);
                return LegEnd(this, mRoute, PerformActionOrig(), ReplanWhenFinished, wasSim);
            }
            public ActionResult PerformActionOrig() { return ActionResult.Terminate; } // receives BoatFollowSubPathRouteAction.PerformAction's original header
        }

        public class VehicleLegHook : VehicleFollowSubPathRouteAction
        {
            public ActionResult PerformActionHook()
            {
                bool wasSim = FollowerIsSim(mRoute);
                return LegEnd(this, mRoute, PerformActionOrig(), ReplanWhenFinished, wasSim);
            }
            public ActionResult PerformActionOrig() { return ActionResult.Terminate; } // receives VehicleFollowSubPathRouteAction.PerformAction's original header
        }

        // WatchDoor is private and sealed: `this` taken as object (ReplanSpreadHook pattern).
        // D3 bouncerskip (docs/151 §6; s58 world-rank: WatchDoor.LoopDel was the #1 interaction by route-plan time, ~15
        // Bridgeport bouncers x 150-270 plans of 170-230 ms in 2.5 h). EA's LoopDel calls RouteToSlot(Target, RoutingSlot_2)
        // every 5 s of the shift and ignores the result; RouteToSlot is plan + DoRoute, nothing else. A bouncer already on
        // the slot (no parent, < 0.1 m XZ, < 0.25 m height, facing the slot's way within ~10 degrees) gets a zero-length
        // route, so skipping it gives EA's result. Off the slot or off-facing it routes as EA. [232] at slot, [235] skipped.
        public class BouncerHook
        {
            public void LoopDelHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                bool src = ShortcutOn("routesrc"), skip = ShortcutOn("bouncerskip");
                if ((src || skip) && BouncerAtSlot(this, skip)) return;
                if (ShortcutOn("bouncerplace") && BouncerPlace(this, false)) return;  // docs/633: unseen, placed instead of a route
                LoopDelOrig(smc, ld);
            }
            public void LoopDelOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { } // receives WatchDoor.LoopDel's original header
        }

        // Pure: skip EA's re-route? Only with the switch on and the bouncer on the slot (RouteSrcCore.AtSlot).
        internal static bool BsSkip(bool on, float dx, float dy, float dz, float fwdDot) { return on && RouteSrcCore.AtSlot(dx, dy, dz, fwdDot); }

        // XZ cosine between the slot's forward and the Sim's (-1 when either is vertical or zero).
        internal static float BsFacing(float fx, float fz, float ax, float az)
        {
            float n1 = (float)Math.Sqrt(fx * fx + fz * fz), n2 = (float)Math.Sqrt(ax * ax + az * az);
            return n1 > 0f && n2 > 0f ? (fx * ax + fz * az) / (n1 * n2) : -1f;
        }

        static bool BouncerAtSlot(object self, bool skip)
        {
            try
            {
                InteractionInstance ii = self as InteractionInstance;
                Sim actor = ii != null ? ii.InstanceActor as Sim : null;
                GameObject target = ii != null ? ii.Target as GameObject : null;
                if (actor == null || target == null || actor.Parent != null) return false;
                Vector3 slot = target.GetPositionOfSlot(Slot.RoutingSlot_2), fwd = target.GetForwardOfSlot(Slot.RoutingSlot_2), a = actor.Position, af = actor.ForwardVector;
                float dot = BsFacing(fwd.x, fwd.z, af.x, af.z);
                if (!RouteSrcCore.AtSlot(a.x - slot.x, a.y - slot.y, a.z - slot.z, dot)) return false;
                int[] c = PatchCounters();
                c[232]++;
                if (!BsSkip(skip, a.x - slot.x, a.y - slot.y, a.z - slot.z, dot)) return false;
                c[235]++;
                return true;
            }
            catch (Exception) { return false; }
        }

        // ---- docs/633 bouncerplace ----------------------------------------------------------------------------------------
        // [verified, ilspycmd VelvetRopes+WatchDoor] Run: RouteToSlot(Target, RoutingSlot_2), Enter - Watch Door, Watch Loop,
        // DoLoop(Default, LoopDel, 5 min), Exit; LoopDel: RouteToSlot(Target, RoutingSlot_2), result ignored. phase-2026111*
        // logs: 250+ "routefail job: ... WatchDoor target VelvetRopes" (failed or cut bouncer routes). With the switch on, an
        // unseen bouncer (TownieUnseen: not a player Sim, standing, alone, off screen, far from viewers; not the selected Sim;
        // no parent) whose slot is off screen and far from viewers is put on RoutingSlot_2 facing the slot's forward
        // (PlaceSim, as rhentryrelax's route-less arrival) instead of a route. In LoopDel EA's route is then skipped; in Run
        // EA's RouteToSlot follows from the slot (a zero-length route) and the rest of Run is EA's. Seen bouncers route as EA.
        // Counters: [3740] loop routes replaced, [3741] Run arrivals placed, [3742] out of scope (player / selected / seen /
        // parented), [3743] slot on screen or near viewers, [3744] errors, [3745] Check mismatches (must stay 0), 3746-3749 spare.
        internal static bool BpPlace(bool on, bool player, bool selected, bool unseen, bool parented, bool slotSeen)
        {
            return on && !player && !selected && unseen && !parented && !slotSeen;
        }

        // WatchDoor is private and sealed: `this` taken as object (BouncerHook pattern).
        public class BouncerRunHook
        {
            public bool RunHook()
            {
                if (ShortcutOn("bouncerplace")) BouncerPlace(this, true);
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives VelvetRopes+WatchDoor.Run
        }

        // Places the bouncer when in scope; true = placed (the caller skips EA's route).
        static bool BouncerPlace(object self, bool run)
        {
            int[] c = PatchCounters();
            try
            {
                InteractionInstance ii = self as InteractionInstance;
                Sim actor = ii != null ? ii.InstanceActor as Sim : null;
                GameObject target = ii != null ? ii.Target as GameObject : null;
                if (actor == null || target == null || actor.HasBeenDestroyed) return false;
                bool player = IsPlayerSim(actor), selected = actor == Sims3.Gameplay.Core.PlumbBob.SelectedActor, parented = actor.Parent != null;
                bool unseen = !player && !selected && !parented && TownieUnseen(actor, null);
                if (!BpPlace(true, player, selected, unseen, parented, false)) { c[3742]++; return false; }
                Vector3 slot = target.GetPositionOfSlot(Slot.RoutingSlot_2), fwd = target.GetForwardOfSlot(Slot.RoutingSlot_2);
                bool slotSeen = UcOnScreen(target) || !FarFromViewers(slot);
                if (slotSeen) { c[3743]++; return false; }
                PlaceSim(actor, slot, fwd, "bouncerplace");
                c[run ? 3741 : 3740]++;
                return true;
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[3744]++; return false; }
        }

        // BouncerPlaceCheck run: the placement decision for an unseen bouncer with an unseen slot (off: EA routes, BROKEN;
        // on: placed, FIXED). Controls: player / selected / seen / parented bouncer and a seen slot keep EA's route; the off
        // rule never places. Live: bouncers on WatchDoor now, by unseen / seen. Read-only.
        public static object BouncerPlaceCheck(string what)
        {
            string counters = B582Counters("bouncerplace", 3740, new[] { "loop routes replaced", "Run arrivals placed", "out of scope", "slot seen", "errors", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run") return "bouncerplace <run|result>";
            bool on = ShortcutOn("bouncerplace");
            bool fixedNow = BpPlace(on, false, false, true, false, false);
            bool controls = !BpPlace(false, false, false, true, false, false)
                && !BpPlace(true, true, false, true, false, false)    // player Sim
                && !BpPlace(true, false, true, true, false, false)    // selected Sim
                && !BpPlace(true, false, false, false, false, false)  // seen bouncer
                && !BpPlace(true, false, false, true, true, false)    // parented
                && !BpPlace(true, false, false, true, false, true);   // slot seen
            if (!controls) PatchCounters()[3745]++;
            Type watch = typeof(VelvetRopes).GetNestedType("WatchDoor", BindingFlags.NonPublic);
            string missing = watch == null ? "VelvetRopes+WatchDoor" : "";
            int unseenN = 0, seenN = 0;
            try
            {
                if (watch != null)
                    foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        InteractionInstance cur = s == null ? null : s.CurrentInteraction;
                        if (cur == null || cur.GetType() != watch) continue;
                        if (!IsPlayerSim(s) && TownieUnseen(s, null)) unseenN++; else seenN++;
                    }
            }
            catch (Exception) { }
            return B582Line("bouncerplace", on, "an unseen bouncer is placed on its slot instead of a route", fixedNow,
                "player / selected / seen / parented bouncer or seen slot route as EA, off as EA, bouncers on WatchDoor now unseen " + unseenN + " seen " + seenN,
                controls, missing, counters);
        }

        // BouncerSkipCheck run: the hook's decision on an on-slot bouncer (3 cm off, facing 2.6 degrees off): EA re-plans
        // (BROKEN), the fix skips. Controls: a bouncer pushed 0.3 m off, one turned 25 degrees, and one 0.4 m above (stairs)
        // still route; the off rule never skips. Read-only.
        public static object BouncerSkipCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bouncerskip: bouncer loops at the slot 232=" + c[232] + ", re-routes skipped 235=" + c[235];
            if (what == "result") return counters;
            if (what != "run") return "bouncerskip <run|result>";
            bool on = ShortcutOn("bouncerskip");
            float near = BsFacing(1f, 0f, (float)Math.Cos(0.045), (float)Math.Sin(0.045)), turned = BsFacing(1f, 0f, (float)Math.Cos(0.44), (float)Math.Sin(0.44));
            bool fixedNow = BsSkip(on, 0.03f, 0f, 0.0f, near);
            bool controls = !BsSkip(true, 0.3f, 0f, 0f, near) && !BsSkip(true, 0.02f, 0f, 0.01f, turned) && !BsSkip(true, 0f, 0.4f, 0f, near)
                && !BsSkip(false, 0.03f, 0f, 0f, near) && BsSkip(true, 0f, 0f, 0f, 1f);
            Type watch = typeof(VelvetRopes).GetNestedType("WatchDoor", BindingFlags.NonPublic);
            string missing = watch == null ? "VelvetRopes+WatchDoor" : "";
            return B582Line("bouncerskip", on, "on-slot bouncer re-route skipped", fixedNow, "displaced / turned / raised still route, off as EA=" + controls, controls, missing, counters);
        }
    }
}
