using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        static void ApplyRouteBackoff()
        {
            // ScriptCore.Route.Plan is the one funnel for every PlanTo* helper (Sims3.SimIFace.Route) and is not overloaded
            Patch(typeof(ScriptCore.Route), "Plan", typeof(RouteBackoffHook), "PlanHook", "PlanOrig");
            // A10 (docs/87 §5): the Replan family through the same async handshake; ts3native [RouteAsync] Kinds decides
            Patch(typeof(ScriptCore.Route), "Replan", typeof(RouteBackoffHook), "ReplanHook", "ReplanOrig");
            Patch(typeof(ScriptCore.Route), "PlanFromPoint", typeof(RouteBackoffHook), "PlanFromPointHook", "PlanFromPointOrig");
            Patch(typeof(ScriptCore.Route), "ReplanFromPoint", typeof(RouteBackoffHook), "ReplanFromPointHook", "ReplanFromPointOrig");
        }

        // Route-failure backoff (docs/11 recommendation 3): a failed plan expands every reachable node, then runs a native
        // obstacle replan and 4-6 C# fail-path plans. When an unseen townie's plan to a destination fails, its next plans to
        // the same destination (1 m cell) fail at once for kBackoffMinutes, with no blocker, so callers take their normal
        // failure branch and DoRouteFailureBehavior skips its fail-path plans. Successes are never cached; a transient
        // failure (blocked by a Sim or vehicle) is not recorded. Counters: [24] plans seen, [25] failures recorded, [26] skipped.
        public partial class RouteBackoffHook : ScriptCore.Route
        {
            public RoutePlanResult PlanHook()
            {
                DoorMirror.SyncRoute(this, Vector3.Invalid); // doormirror: push the start/goal lots' doors to ts3native (DoorMirror.cs)
                uint rflT0 = SrcNowUs(); // routefaillog (RouteFailLog.cs)
                RouteSrcCall src = RouteSrcBegin(this, 0, Vector3.Invalid); // routesrc: source of this plan (RouteSources.cs, docs/151)
                RoutePlanResult r = UfPlan(0, Vector3.Invalid); // docs/202 unseenfbplan wraps the whole plan (UnseenFallback.cs)
                if (!UfTakeNotFail()) NotePlanResult(Follower, r.mType == RoutePlanResultType.Succeeded, this); // the stuck watchdog's route-fail count and success memo (RouteFixes.cs); docs/202: not for a cut plan
                StuckCheckPlanned(r.mType == RoutePlanResultType.Succeeded); // docs/163 §10 stuck-check outcome counters (FewerPlans.cs)
                RouteSrcEnd(this, src, r.mType == RoutePlanResultType.Succeeded);
                RflPlan(this, 0, rflT0, r);
                return r;
            }

            RoutePlanResult PlanBackoff()
            {
                int[] c = PatchCounters();
                c[24]++;
                IScriptProxy proxy = Follower;
                Sims3.Gameplay.Actors.Sim sim = proxy != null ? proxy.Target as Sims3.Gameplay.Actors.Sim : null;
                if (sim != null && ShortcutOn("unseenboat")) UbTrip(sim);  // docs/248: the plan below runs from where it leaves the Sim
                if (ShortcutOn("failrepeat") && sim != null) return PlanFailRepeat(sim);
                return PlanBackoffBody(sim);
            }

            // docs/163 §4: the same Sim asking the same failing plan again right after the last one returned (the shape of the
            // SV crowd pair of failed NoStart plans, 7,361 + 7,350 ms ~0.3 s apart; same goal inferred, not logged). Switch "failrepeat" (default off): when this Sim's previous plan
            // to the same destination (1 m cell) from the same spot (0.25 m) failed with no path, blocked by neither a Sim nor
            // a vehicle, and returned at most kFailRepeatMs real milliseconds ago, the plan returns that failure (all result
            // fields, so callers take the same failure branch) without searching again. Short hops (< kBackoffMinDistance)
            // and non-Sim followers are untouched. Differs from the game only when the world opened a path inside the window.
            // Counters: [241] failures recorded, [242] repeats answered, [243] ms of the recorded plans those repeats skipped.
            RoutePlanResult PlanFailRepeat(Sims3.Gameplay.Actors.Sim sim)
            {
                Vector3 dest = GetApproximateOriginalDestination();
                Vector3 at = sim.Position;
                if (dest == Vector3.Origin || dest == Vector3.Invalid || (at - dest).Length() < kBackoffMinDistance)
                    return PlanBackoffBody(sim);
                int[] c = PatchCounters();
                BackoffKey key = new BackoffKey(Follower.ObjectId.Value, dest);
                FailEntry e;
                if (sFailRepeat.TryGetValue(key, out e) && NoStartCore.Within(System.Environment.TickCount, e.Tick, kFailRepeatMs)
                    && (at - e.Start).LengthSqr() <= 0.0625f)
                {
                    c[242]++;
                    c[243] += e.Ms;
                    return FailFast(e.Result);
                }
                int t0 = System.Environment.TickCount;
                RoutePlanResult r = PlanBackoffBody(sim);
                if (r.mType == RoutePlanResultType.FailedBecauseNoPathToDestination
                    && r.mBlockerType != RouteBlockerType.Sim && r.mBlockerType != RouteBlockerType.Vehicle)
                {
                    // shortcut: whole map dropped at 256 live entries (a burst of distinct failures); entries expire by time anyway
                    if (sFailRepeat.Count >= 256) sFailRepeat.Clear();
                    e.Tick = System.Environment.TickCount;
                    e.Ms = unchecked(e.Tick - t0);
                    e.Start = at;
                    e.Result = r;
                    sFailRepeat[key] = e;
                    c[241]++;
                }
                else sFailRepeat.Remove(key);
                return r;
            }

            // The plan itself (PlanAsync below) is inlined here, its only caller: one interpreter frame less above the original
            // Plan (issue #92, docs/51 §386).
            RoutePlanResult PlanBackoffBody(Sims3.Gameplay.Actors.Sim sim)
            {
                int[] c = PatchCounters();
                IScriptProxy proxy = Follower;
                BackoffKey key = default(BackoffKey);
                BackoffEntry e = default(BackoffEntry);
                long now = 0;
                bool record = false;
                if (ShortcutOn("backoff"))
                {
                    Vector3 dest = GetApproximateOriginalDestination();
                    // non-Sim routers, no destination yet, or a short hop (turn in place, fail-path near the Sim): untouched
                    if (sim != null && dest != Vector3.Origin && dest != Vector3.Invalid && (sim.Position - dest).Length() >= kBackoffMinDistance)
                    {
                        key = new BackoffKey(proxy.ObjectId.Value, dest);
                        now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                        // From <= now guards against a clock that went back (another save loaded)
                        if (sBackoff.TryGetValue(key, out e) && e.From <= now && now < e.Until && BackoffEligible(sim))
                        {
                            c[26]++;
                            return FailFast(e.Type);
                        }
                        record = true;
                    }
                }
                // PlanAsync (docs/18, see below), inlined
                RoutePlanResult r;
                bool pre = false;
                bool started = AsyncEligible(sim, "asyncroute") ? PreCb(0, ref pre) && GetOption(kAsyncStart)
                    : OwnerTaskStart(ref pre) ? true
                    : ForeignEligible() && PreCb(0, ref pre) && GetOption2((Sims3.SimIFace.Route.RouteOption2)kArStart);
                if (!started)
                {
                    StepAside();
                    r = PlanOrig();
                }
                else
                {
                    try
                    {
                        AsyncWait();
                        List<Door> doors = pre ? PrePlanCbCore.Snapshot(LockedDoors()) : null;
                        r = PlanOrig();
                        RestoreDoors(doors);
                    }
                    finally
                    {
                        GetOption2(kAsyncCancel); // disarm, also when the Sim is reset while sleeping
                    }
                }
                if (record && (r.mType == RoutePlanResultType.FailedBecauseNoPathToDestination) // not plain Failed: ts3native's route cache returns that on a hit (docs/15)
                    && r.mBlockerType != RouteBlockerType.Sim && r.mBlockerType != RouteBlockerType.Vehicle && BackoffEligible(sim))
                {
                    if (sBackoff.Count >= kMaxBackoffEntries) PruneBackoff(now);
                    e.From = now;
                    e.Until = now + kBackoffTicks;
                    e.Type = r.mType;
                    sBackoff[key] = e;
                    c[25]++;
                }
                return r;
            }
            public RoutePlanResult PlanOrig() { return default(RoutePlanResult); } // receives ScriptCore.Route.Plan's original header

            // Async route plan (docs/18 "Async plan", ts3native [RouteAsync]): when the plan comes from the Sim's own task and
            // that task may yield, ts3native searches on its worker thread while this task sleeps one tick at a time; the sim
            // thread keeps running everything else. Handshake through GetOption on RouteOption bit 58, which has no name and
            // is never set by the game: without ts3native (or with it off) START answers false and this is one extra call.
            // The original Plan then runs once, as without us, and its internal call hands over the worker's result.
            // CheckYieldingContext is not enough: when this plan runs inside a native->managed call during the task, a
            // yield aborts the game (interp.c:3595 `pState->yieldReturn'), so ts3native refuses START there (MayYield).
            // Switch "asyncroute" (default on; ts3native decides). Counters: [56] started, [57] yields, [58] poll cap.
            // H6 M1 (docs/60 §2.4): live mode only, no travel, no world shutdown (ts3native also refuses START then).
            // docs/134: a plan from a yielding task that is not the route's Sim (another Sim's interaction planning this one's
            // route, a vehicle or other non-Sim follower, a situation or one-shot task) sends the foreign START instead
            // (GetOption2 bits 58 + 59, kind 0). ts3native 0.36.7+ starts it only with [RouteAsync] FallbackAsync=1 and refuses
            // it as "Foreign" otherwise; older ts3native and the game answer false, so the plan stays synchronous as before.
            // (The code is inlined in PlanBackoffBody above, issue #92.)

            // docs/240 asyncjigsync: no START while the Sim's interaction (or its linked one) holds a SocialJig not yet in the
            // world, so the partner never runs while the initiator sleeps between CreateObjectOutOfWorld and AddToWorld.
            // Counters: [758] own plans kept sync, [759] of those with the jig only in the linked interaction.
            bool AsyncEligible(Sims3.Gameplay.Actors.Sim sim, string shortcut)
            {
                if (!(sim != null && ShortcutOn(shortcut) && Simulator.CurrentTask == sim.ObjectId
                    && GameStates.IsLiveState && !GameStates.IsTravelling && !GameStates.IsWorldShuttingDown
                    && Simulator.CheckYieldingContext(false))) return false;
                int jig = JigPending(sim);
                if (jig == 0) return true;
                int[] c = PatchCounters();
                c[758]++;
                if (jig == 2) c[759]++;
                return false;
            }

            // docs/163 §7: foreign plans whose task is the only one that drives the follower, so the own START (bit 58) is as safe
            // as a Sim's own plan with [RouteAsync] FallbackAsync=0 (dropped from v0.1.5). Only that task sleeps; the follower
            // does not move meanwhile. Switch "drivertaskasync" (default off): a traffic car's route planned in its own
            // CarNpcWithNoDriver+LiteDriver task (the task's only plan is its car's, IL of LiteDriver.Simulate), and a vehicle's
            // route planned in the task of the Sim riding it (Boat.RouteBoatNearHouseboat from GetOutOfBoat.Run; Sim.Parent ==
            // vehicle inferred). Switch "stuckcheckown" (default off, needs stuckcheckasync): the stuck check's throwaway routes
            // planned in our drain task (never followed; a Sim that moves meanwhile is not stuck anyway). ts3native still
            // refuses NoYield / Active / M1 / QueueFull as for any own START. Counters: [251] car STARTs sent, [252] ridden
            // vehicle STARTs sent, [253] stuck check STARTs sent, [254] of those started by ts3native.
            bool OwnerTaskStart(ref bool pre)
            {
                int slot = OwnerTaskSlot();
                if (slot < 0 || !ForeignEligible()) return false;
                int[] c = PatchCounters();
                c[slot]++;
                PreCb(0, ref pre);
                // docs/163 §10 stuckcheckcap (FewerPlans.cs StuckCheckRun); §10.1: every stuck-check START sends the tagged form (round,
                // cut bit only with stuckcheckcap), so ts3native's own-job log lines name the round; ts3native answers true only for a cut
                if (slot == 253 && GetOption2(kProbe | (Sims3.SimIFace.Route.RouteOption2)NoStartCore.StuckTag(sStuckIn ? sStuckRound : 0, sStuckIn && sStuckCap, sStuckIn && sStuckLast)) && sStuckCap)
                    c[297]++;
                if (!GetOption(kAsyncStart)) return false;
                c[254]++;
                return true;
            }

            int OwnerTaskSlot()
            {
                if (ShortcutOn("stuckcheckown") && sStuckTask.IsValid && Simulator.CurrentTask == sStuckTask) return 253;
                if (!ShortcutOn("drivertaskasync")) return -1;
                IScriptProxy proxy = Follower;
                Sims3.Gameplay.Objects.Vehicles.Vehicle v = proxy != null ? proxy.Target as Sims3.Gameplay.Objects.Vehicles.Vehicle : null;
                if (v == null) return -1;
                IScriptProxy task = Simulator.GetProxy(Simulator.CurrentTask);
                object owner = task != null ? task.Target : null;
                if (owner != null && owner.GetType().FullName == kLiteDriver && v is Sims3.Gameplay.Objects.Vehicles.CarNpcWithNoDriver) return 251;
                Sims3.Gameplay.Actors.Sim rider = owner as Sims3.Gameplay.Actors.Sim;
                return rider != null && rider.Parent == v ? 252 : -1;
            }
            const string kLiteDriver = "Sims3.Gameplay.Objects.Vehicles.CarNpcWithNoDriver+LiteDriver"; // private nested task class

            // any running script task that may yield here; ts3native still refuses a nested native->managed call (NoYield) and a
            // route being followed (Active)
            internal static bool ForeignEligible()
            {
                if (!(ShortcutOn("asyncroute") && GameStates.IsLiveState && !GameStates.IsTravelling && !GameStates.IsWorldShuttingDown
                    && Simulator.CheckYieldingContext(false))) return false;
                IScriptProxy task = Simulator.GetProxy(Simulator.CurrentTask);
                return JigPending(task != null ? task.Target as Sims3.Gameplay.Actors.Sim : null) == 0; // asyncjigsync (AsyncJigSync.cs), uncounted here
            }

            // after a START answered yes: sleep a tick at a time until READY
            void AsyncWait()
            {
                int[] c = PatchCounters();
                c[56]++;
                RflAsyncMark(); // routefaillog: this plan waits on a ts3native job
                for (int i = 0; !GetOption2(kAsyncReady); i++)
                {
                    // shortcut: a fixed cap (~10-20 s real time); past it the plan waits natively and is then made
                    // again synchronously. Upgrade to a time-based cap if long searches ever reach it.
                    if (i >= kAsyncMaxPolls) { c[58]++; StepAside(); break; }
                    c[57]++;
                    Simulator.Sleep(0);
                }
            }

            // docs/185 §5: the original Plan/Replan runs Route.AboutToPlanCallback only after the async job (it is called after
            // AsyncWait), so the job planned without that plan's callback inputs (forbidden closed-venue / tomb / GoHere doors,
            // ForceUseMainEntrance, RouteAsGhost, BeginAsCar/Boat, EnablePlanningAsCar), and EA's callback then clears the
            // follower's LockedDoorsDuringPlan the job's door answers just filled. Switch "preplancb" (default off until its
            // A/B): a plan about to send a START (own, owner-task or foreign) first runs the callback with the arguments the
            // original passes (IL: "Plan" + destination point for Plan / PlanFromPoint, "Replan" / "ReplanFromPoint" +
            // PlanResult.mDestination), skipped like the original when the destination object is gone; after the original
            // returns, the job's door list is put back if the original's second callback emptied it. The second callback is
            // kept: every known subscriber is idempotent per plan (options set, forbidden list cleared then refilled, ignore
            // list adds; EA's own PlanToLotCore already calls it twice), and mods that add to the delegate keep running.
            // Counters: [295] pre-calls made, [296] door lists restored.
            bool PreCb(int kind, ref bool pre)
            {
                if (pre || !ShortcutOn("preplancb")) return true;
                pre = true;
                Sims3.SimIFace.Route.AboutToPlanDelegate cb = Sims3.SimIFace.Route.AboutToPlanCallback;
                if (cb == null || DestObjectInvalid()) return true;
                cb(this, PrePlanCbCore.Name(kind), kind == 0 || kind == 2 ? GetApproximateOriginalDestination() : PlanResult.mDestination);
                PatchCounters()[295]++;
                return true;
            }

            // the original's IsDestObjectInvalid (private; sets the same failed result the original sets right after)
            bool DestObjectInvalid()
            {
                if (sDestInvalid == null)
                    sDestInvalid = typeof(ScriptCore.Route).GetMethod("IsDestObjectInvalid", BindingFlags.Instance | BindingFlags.NonPublic);
                return sDestInvalid != null && (bool)sDestInvalid.Invoke(this, null);
            }
            static MethodInfo sDestInvalid;

            List<Door> LockedDoors()
            {
                IScriptProxy proxy = Follower;
                Sims3.Gameplay.Actors.Sim sim = proxy != null ? proxy.Target as Sims3.Gameplay.Actors.Sim : null;
                return sim != null && sim.SimRoutingComponent != null ? sim.SimRoutingComponent.LockedDoorsDuringPlan : null;
            }

            void RestoreDoors(List<Door> doors)
            {
                if (doors != null && PrePlanCbCore.Restore(LockedDoors(), doors)) PatchCounters()[296]++;
            }

            // A10 (docs/87 §5, ts3native [RouteAsync] Kinds): Replan (kind 1), PlanFromPoint (2) and ReplanFromPoint (3) the
            // same way. START is GetOption2 with bits 58 + 59 and the kind in bits 56-57; kinds 2-3 first send the point,
            // one component per call (bit 60, component in bits 56-57, float bits in the low dword). The game has no
            // RouteOption2 names up there, so without ts3native START answers false (the point calls are reads whose
            // answer is ignored). ts3native refuses a route that is being followed. Switch "asyncreplan" (default on).
            public RoutePlanResult ReplanHook()
            {
                uint rflT0 = SrcNowUs(); // routefaillog (RouteFailLog.cs)
                RouteSrcCall src = RouteSrcBegin(this, 1, Vector3.Invalid);
                RoutePlanResult r = UfPlan(1, Vector3.Invalid);
                RouteSrcEnd(this, src, r.mType == RoutePlanResultType.Succeeded);
                RflPlan(this, 1, rflT0, r);
                return r;
            }
            RoutePlanResult ReplanAsync()
            {
                DoorMirror.SyncRoute(this, Vector3.Invalid);
                bool pre = false;
                if (!AsyncKindStart(1, Vector3.Origin, ref pre)) return ReplanOrig();
                try
                {
                    AsyncWait();
                    List<Door> doors = pre ? PrePlanCbCore.Snapshot(LockedDoors()) : null;
                    RoutePlanResult r = ReplanOrig();
                    RestoreDoors(doors);
                    return r;
                }
                finally { GetOption2(kAsyncCancel); }
            }
            public RoutePlanResult ReplanOrig() { return default(RoutePlanResult); }

            public RoutePlanResult PlanFromPointHook(Vector3 startPosition)
            {
                uint rflT0 = SrcNowUs(); // routefaillog (RouteFailLog.cs)
                RouteSrcCall src = RouteSrcBegin(this, 2, startPosition);
                RoutePlanResult r = UfPlan(2, startPosition);
                RouteSrcEnd(this, src, r.mType == RoutePlanResultType.Succeeded);
                RflPlan(this, 2, rflT0, r);
                return r;
            }
            RoutePlanResult PlanFromPointAsync(Vector3 startPosition)
            {
                DoorMirror.SyncRoute(this, startPosition);
                bool pre = false;
                if (!AsyncKindStart(2, startPosition, ref pre)) return PlanFromPointOrig(startPosition);
                try
                {
                    AsyncWait();
                    List<Door> doors = pre ? PrePlanCbCore.Snapshot(LockedDoors()) : null;
                    RoutePlanResult r = PlanFromPointOrig(startPosition);
                    RestoreDoors(doors);
                    return r;
                }
                finally { GetOption2(kAsyncCancel); }
            }
            public RoutePlanResult PlanFromPointOrig(Vector3 startPosition) { return default(RoutePlanResult); }

            public RoutePlanResult ReplanFromPointHook(Vector3 fromPt)
            {
                uint rflT0 = SrcNowUs(); // routefaillog (RouteFailLog.cs)
                RouteSrcCall src = RouteSrcBegin(this, 3, fromPt);
                RoutePlanResult r = UfPlan(3, fromPt);
                RouteSrcEnd(this, src, r.mType == RoutePlanResultType.Succeeded);
                RflPlan(this, 3, rflT0, r);
                return r;
            }
            RoutePlanResult ReplanFromPointAsync(Vector3 fromPt)
            {
                UbCountReplan();  // docs/248
                DoorMirror.SyncRoute(this, fromPt);
                bool pre = false;
                if (!AsyncKindStart(3, fromPt, ref pre)) return ReplanFromPointOrig(fromPt);
                try
                {
                    AsyncWait();
                    List<Door> doors = pre ? PrePlanCbCore.Snapshot(LockedDoors()) : null;
                    RoutePlanResult r = ReplanFromPointOrig(fromPt);
                    RestoreDoors(doors);
                    return r;
                }
                finally { GetOption2(kAsyncCancel); }
            }
            public RoutePlanResult ReplanFromPointOrig(Vector3 fromPt) { return default(RoutePlanResult); }

            bool AsyncKindStart(int kind, Vector3 p, ref bool pre)
            {
                IScriptProxy proxy = Follower;
                // docs/176: not the route's own Sim task (a boat or car follower driven from its Sim's task, another Sim's or a
                // one-shot task) but a task that may yield: foreign START (bit 61). ts3native starts it only with [RouteAsync]
                // ForeignKinds and refuses it as "Foreign" otherwise; older ts3native and the game answer false (sync as before).
                // Switch "asyncreplanforeign" (default on; ts3native decides). Counters: [246] foreign STARTs sent, [247] started.
                bool own = AsyncEligible(proxy != null ? proxy.Target as Sims3.Gameplay.Actors.Sim : null, "asyncreplan");
                if (!own && !(ShortcutOn("asyncreplan") && ShortcutOn("asyncreplanforeign") && ForeignEligible())) return false;
                PreCb(kind, ref pre);
                if (kind >= 2) { AsyncPoint(0, p.x); AsyncPoint(1, p.y); AsyncPoint(2, p.z); }
                bool started = GetOption2((Sims3.SimIFace.Route.RouteOption2)((own ? kArStart : kArStart | kArForeign) | (ulong)kind << 56));
                if (!own)
                {
                    int[] c = PatchCounters();
                    c[246]++;
                    if (started) c[247]++;
                }
                if (!started) StepAside();  // the kind's sync plan follows
                return started;
            }

            // docs/183 JobYield: a plan about to run here (START refused, or the READY poll cap) would wait natively for the
            // route job the worker runs, blocking the whole sim thread for the rest of it. Instead this task sleeps a tick at a
            // time while ts3native answers JOBBUSY (a job runs, this is the sim thread at the task's root state), then plans
            // with no job left to wait for. Off: one switch read. Without ts3native's [RouteAsync] JobYield the game answers
            // false (bit 63 has no name) and nothing changes. Switch "jobyield" (default off). Counters: [248] step-asides,
            // [249] ticks slept, [250] gave up at the cap (the plan then waits natively, as before).
            static void StepAside()
            {
                if (!ShortcutOn("jobyield") || !MayYield() || !sYieldProbe.GetOption2(kJobBusy)) return;
                int[] c = PatchCounters();
                c[248]++;
                int t0 = System.Environment.TickCount;
                do
                {
                    c[249]++;
                    Simulator.Sleep(0);
                    // shortcut: a fixed real-time cap; a job queue that stays busy longer than this gets the old native wait
                    if (unchecked(System.Environment.TickCount - t0) >= kStepAsideMaxMs) { c[250]++; return; }
                } while (sYieldProbe.GetOption2(kJobBusy));
            }
            const int kStepAsideMaxMs = 10000;
            static readonly Sims3.SimIFace.Route.RouteOption2 kJobBusy = unchecked((Sims3.SimIFace.Route.RouteOption2)0x8500000000000000UL);
            // docs/163 §10 PROBE mark (ts3native ScProbeMark): bits 63 + 58 + 57, no name; the game and older ts3native answer false
            static readonly Sims3.SimIFace.Route.RouteOption2 kProbe = unchecked((Sims3.SimIFace.Route.RouteOption2)0x8600000000000000UL);
            void AsyncPoint(int i, float f)
            {
                FloatBits b = default(FloatBits);
                b.F = f;
                GetOption2((Sims3.SimIFace.Route.RouteOption2)(kArPoint | (ulong)i << 56 | b.U));
            }

            const Sims3.SimIFace.Route.RouteOption kAsyncStart = (Sims3.SimIFace.Route.RouteOption)0x0400000000000000L;
            const Sims3.SimIFace.Route.RouteOption2 kAsyncReady = (Sims3.SimIFace.Route.RouteOption2)0x0400000000000000L;
            const Sims3.SimIFace.Route.RouteOption2 kAsyncCancel = (Sims3.SimIFace.Route.RouteOption2)0x4400000000000000L;
            const ulong kArStart = 0x0C00000000000000UL, kArPoint = 0x1C00000000000000UL, kArForeign = 0x2000000000000000UL;
            const int kAsyncMaxPolls = 1000;

            // What Plan leaves after a real failure, minus the blocker (so no fail-path plans): empty paths, invalid start points.
            RoutePlanResult FailFast(RoutePlanResultType type)
            {
                RoutePlanResult res = default(RoutePlanResult);
                res.mType = type;
                return FailFast(res);
            }

            // the same, with a recorded result (failrepeat keeps the blocker fields of the failure it repeats)
            RoutePlanResult FailFast(RoutePlanResult res)
            {
                ClearPaths();
                PlanResult = res;
                if (sStartFields == null)
                {
                    const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                    Type t = typeof(ScriptCore.Route);
                    sStartFields = new FieldInfo[] { t.GetField("mOriginalStartPoint", f), t.GetField("mCurrentStartPoint", f), t.GetField("mPlanStartPoint", f) };
                }
                foreach (FieldInfo fi in sStartFields)
                    if (fi != null) fi.SetValue(this, Vector3.Invalid);
                return res;
            }
            static FieldInfo[] sStartFields;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; }

        // Whether a Simulator.Sleep here is safe. CheckYieldingContext only tests for a current task; EA's yield also needs
        // the running code to be the task's own top-level interpreter state, not a native->managed call made during the
        // task, or the game aborts on "interp.c:3595 `pState->yieldReturn' not met" (docs/18 "Crash: yield in a nested
        // call"). ts3native answers that through GetOption2 bit 58|61 (true = may not yield) on any Route, here one with
        // handle 0. Without ts3native the game answers false (handle 0 has no route), so the old rule applies.
        static bool MayYield()
        {
            return Simulator.CheckYieldingContext(false) && !sYieldProbe.GetOption2(kNoYield);
        }
        static readonly ScriptCore.Route sYieldProbe = new ScriptCore.Route();
        const Sims3.SimIFace.Route.RouteOption2 kNoYield = (Sims3.SimIFace.Route.RouteOption2)0x2400000000000000L;

        // Unseen townie on foot, not a pet or horse (their routes carry their own follower/owner logic).
        static bool BackoffEligible(Sims3.Gameplay.Actors.Sim sim)
        {
            return sim.SimDescription != null && !sim.SimDescription.IsPet && TownieUnseen(sim, null);
        }

        // Drops expired entries; if all are live (a burst of distinct failures), starts over rather than grow.
        static void PruneBackoff(long now)
        {
            List<BackoffKey> dead = new List<BackoffKey>();
            foreach (KeyValuePair<BackoffKey, BackoffEntry> kv in sBackoff)
                if (now >= kv.Value.Until || kv.Value.From > now) dead.Add(kv.Key);
            foreach (BackoffKey k in dead) sBackoff.Remove(k);
            if (sBackoff.Count >= kMaxBackoffEntries) sBackoff.Clear();
        }

        const float kBackoffMinutes = 30f, kBackoffMinDistance = 5f;
        const int kMaxBackoffEntries = 2048;
        static readonly long kBackoffTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(kBackoffMinutes, Sims3.Gameplay.Utilities.TimeUnit.Minutes);
        static readonly Dictionary<BackoffKey, BackoffEntry> sBackoff = new Dictionary<BackoffKey, BackoffEntry>(new BackoffKeyComparer());

        struct BackoffEntry { public long From, Until; public RoutePlanResultType Type; }

        const int kFailRepeatMs = 500;
        struct FailEntry { public int Tick, Ms; public Vector3 Start; public RoutePlanResult Result; }
        static readonly Dictionary<BackoffKey, FailEntry> sFailRepeat = new Dictionary<BackoffKey, FailEntry>(new BackoffKeyComparer());

        // Follower object id plus the destination's 1 m cell (x, height, z).
        struct BackoffKey
        {
            public readonly ulong Sim;
            public readonly int X, Y, Z;
            public BackoffKey(ulong sim, Vector3 p)
            {
                Sim = sim;
                X = (int)Math.Floor(p.x); Y = (int)Math.Floor(p.y); Z = (int)Math.Floor(p.z);
            }
        }

        // explicit comparer: the default one boxes structs on every lookup in this Mono
        class BackoffKeyComparer : IEqualityComparer<BackoffKey>
        {
            public bool Equals(BackoffKey a, BackoffKey b) { return a.Sim == b.Sim && a.X == b.X && a.Y == b.Y && a.Z == b.Z; }
            public int GetHashCode(BackoffKey k) { return k.Sim.GetHashCode() ^ (k.X * 73856093) ^ (k.Y * 19349663) ^ (k.Z * 83492791); }
        }
    }
}
