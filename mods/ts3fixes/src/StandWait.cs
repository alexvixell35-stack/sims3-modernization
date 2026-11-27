using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Routing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // standwaitfix / standwaitfixhh (docs/686, default off). EA bug fix: Sims that stand for up to 50 sim-minutes at a stair
    // top, on a landing or in a doorway, replanning the same short route every wait cycle.
    //
    // [verified, decompiled] ObstacleEncounteredRouteInterruptAction runs a StandAndWaitController for
    // kDefaultStandAndWaitDuration (50 sim-minutes in the code, 20 in the game's tuning file: docs/686 §7). Its cycle
    // handler replans the interrupted route (with "blocked by people" set) when the plan has failed, and when that replan fails too it answers "keep waiting" at once. Everything
    // below that return is skipped for the cycle: the test for a push request or an outside cancel, the collecting of
    // obstacles, the push of the Sims in the way and the "ignore obstacles" exit. So two Sims that stand in each other's
    // footprint (same stair exit slot, 0.1-0.4 m apart) each fail every replan, neither pushes the other, neither obeys a
    // push and a player's cancel is not seen; both stand until the 50 minutes are over and the route fails. The sibling
    // action for an obstructed destination tests the exit reasons first and pushes every cycle.
    //
    // Fix, in a cycle where the game returned through that early exit (StandWaitCore has the rules):
    //   1. a push request or outside cancel is honoured with the game's own exit (the pushed Sim steps aside in the game's
    //      MidRouteBePushed, a cancelled route ends);
    //   2. the Sims in the way are pushed with the game's own push: the plan's blocker Sim, Sims within the game's 0.6 m of
    //      the destination and within its 0.5 m of the waiter; not the waiter's own partner; in a mutual wait only the
    //      higher-ranked Sim pushes;
    //   3. from blocked cycle kStandWaitPassCycle on the route is planned once without "blocked by people"; when that
    //      succeeds the Sim follows it with the game's ignore-obstacles timer for kStandWaitPassMinutes (what the game
    //      itself does after the timeout in a crowd), unless another Sim stands at the destination.
    // Nothing is teleported or skipped and no check is dropped; a wait whose plan also fails without people (locked door,
    // object in the way) keeps the game's wait.
    //
    // Scope: standwaitfix covers Sims the player does not control; household and selected Sims (as waiter or as the
    // pushed Sim) need standwaitfixhh as well. With the switches off the hooks only
    // count: blocked cycles, waits that ran to the timeout, and what the fix would have done (compare mode).
    // standwaitlog writes one line per wait that had blocked cycles. Counters 4300-4319. Check: StandWaitCheck.
    public static partial class Fixes
    {
        const int kSwfWaits = 4300, kSwfBlockedCycles = 4301, kSwfBlockedWaits = 4302, kSwfTimeouts = 4303, kSwfYields = 4304, kSwfWouldYield = 4305,
            kSwfPushCycles = 4306, kSwfPushed = 4307, kSwfWouldPush = 4308, kSwfMutual = 4309, kSwfWithheld = 4310, kSwfProbes = 4311, kSwfPasses = 4312,
            kSwfWouldPass = 4313, kSwfExempt = 4314, kSwfResolved = 4315, kSwfCyclesLeft = 4316, kSwfErrors = 4317, kSwfLongest = 4318, kSwfLines = 4319;
        const int kSwfMaxBlockers = 8;
        const float kSwfDestRadius = 0.6f, kSwfSelfRadius = 0.5f;  // the game's own push radii (obstructed destination, end of route)

        class SwfState
        {
            public object Action;
            public DateAndTime Start;
            public int Cycles, Blocked, RunBlocked, PushCycles, Pushed, LastPushCycle, NextPass, Delays, YieldCycle, PassCycle, Probes;
            public int WouldYield, WouldPush, WouldPass;
            public bool Yielded, Passed, Exempt, On, PushAcceptedLast;
            // StandWaitSocial.cs: failed people-free passes of a social, the cycle it gave up in / would have
            public int SocFails, SocGaveUp, SocWould;
            public bool SocExempt;
            public bool SpsCounted;  // ShareLanes.cs: simpass judged this wait past the grace
            public bool ClwCounted;  // ClipWait.cs: this wait is in its counters
            public bool PrxCounted;  // PetRelax.cs: this wait is in its kept / would counters
            public readonly ulong[] Named = new ulong[kSwfMaxBlockers];
            public int NamedN;
            public string Plan = "", Mutual = "";
            // what the Sims near the waiter were doing (standwaitlog: which interactions clog the way)
            public string Who = "";
        }

        static readonly Dictionary<ulong, SwfState> sSwf = new Dictionary<ulong, SwfState>();
        static readonly Sim[] sSwfCand = new Sim[kSwfMaxBlockers];
        static readonly StandWaitCore.Blocker[] sSwfBlk = new StandWaitCore.Blocker[kSwfMaxBlockers];
        static readonly bool[] sSwfPush = new bool[kSwfMaxBlockers];
        static Type sSwfType;
        static FieldInfo sSwfElected, sSwfReturn;
        static string sSwfMissing = "", sSwfLast = "";

        static void ApplyStandWait()
        {
            sSwfMissing = "";
            Assembly asm = typeof(RouteInterruptAction).Assembly;
            sSwfType = asm.GetType("Sims3.Gameplay.Routing.ObstacleEncounteredRouteInterruptAction");
            Type ctl = asm.GetType("Sims3.Gameplay.Routing.StandAndWaitController");
            if (sSwfType == null) sSwfMissing += "ObstacleEncounteredRouteInterruptAction ";
            if (ctl == null) sSwfMissing += "StandAndWaitController ";
            if (sSwfType != null)
            {
                sSwfElected = sSwfType.GetField("mbElectedToExit", kInstAll);
                sSwfReturn = sSwfType.GetField("mReturnValue", kInstAll);
                if (sSwfElected == null || sSwfElected.FieldType != typeof(bool)) sSwfMissing += "mbElectedToExit ";
                if (sSwfReturn == null || sSwfReturn.FieldType != typeof(RouteAction.ActionResult)) sSwfMissing += "mReturnValue ";
            }
            sSwfMissing = sSwfMissing.Trim();
            if (sSwfMissing.Length > 0) { AppliedLog().Add("standwaitfix: not found: " + sSwfMissing); return; }
            if (!Patch(sSwfType, "StandAndWaitCycleHandler", new Type[] { ctl, typeof(object) }, typeof(SwfObstacleHook), "CycleHook", "CycleOrig"))
                sSwfMissing = "StandAndWaitCycleHandler";
            else if (!Patch(sSwfType, "PerformAction", Type.EmptyTypes, typeof(SwfObstacleHook), "PerformHook", "PerformOrig"))
                sSwfMissing = "PerformAction";
        }

        // `this` is the game's ObstacleEncounteredRouteInterruptAction (internal); the public base gives its Sim and route.
        public class SwfObstacleHook : RouteInterruptAction
        {
            public bool CycleHook(object controller, object userData)
            {
                bool failedBefore = SwfPlanFailed(mInterruptedPath);
                // simpass (ShareLanes.cs, docs/686 §8.8) and clipwait (ClipWait.cs, §9), off by default: a wait that goes on past the grace with only Sims in the way ends
                return SwfCycle(this, mRoutingSim, mInterruptedPath, failedBefore, CycleOrig(controller, userData)) && SpsObstacleCycle(this, mRoutingSim, mInterruptedPath)
                    && ClwObstacleCycle(this, mRoutingSim, mInterruptedPath);
            }
            public bool CycleOrig(object controller, object userData) { return true; } // receives StandAndWaitCycleHandler's original body

            public RouteAction.ActionResult PerformHook()
            {
                // simpass (ShareLanes.cs, docs/686 §8) and petrelax (PetRelax.cs, §11), off by default: only Sims or pets in the way near a portal = walk on, no wait
                if (SpsAction(mRoutingSim, mInterruptedPath)) return RouteAction.ActionResult.ContinueAndFollowPath;
                SwfBegin(this, mRoutingSim);
                try { return PerformOrig(); }
                finally { SwfEnd(this, mRoutingSim, mInterruptedPath); }
            }
            public RouteAction.ActionResult PerformOrig() { return RouteAction.ActionResult.Terminate; } // receives PerformAction's original body
        }

        static bool SwfPlanFailed(Route path)
        {
            try { return path != null && !path.PlanResult.Succeeded(); }
            catch (ResetException) { throw; }
            catch (Exception) { return false; }
        }

        static void SwfBegin(object action, Sim sim)
        {
            try
            {
                if (sim == null) return;
                if (sSwf.Count > 64) sSwf.Clear();  // entries leave in SwfEnd; this only bounds a table left behind by errors
                SwfState st = new SwfState();
                st.Action = action;
                st.Start = SimClock.CurrentTime();
                st.NextPass = Instantiator.kStandWaitPassCycle;
                sSwf[sim.ObjectId.Value] = st;
                PatchCounters()[kSwfWaits]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kSwfErrors]++; }
        }

        static SwfState SwfStateOf(Sim sim, object action)
        {
            SwfState st;
            if (sSwf.TryGetValue(sim.ObjectId.Value, out st) && object.ReferenceEquals(st.Action, action)) return st;
            st = new SwfState();  // a wait that began before the hooks went in (or after a table clear)
            st.Action = action;
            st.Start = SimClock.CurrentTime();
            st.NextPass = Instantiator.kStandWaitPassCycle;
            sSwf[sim.ObjectId.Value] = st;
            return st;
        }

        static bool SwfInScope(Sim s, bool hhOn) { return hhOn || !IsPlayerSim(s); }  // not HouseholdExempt: HouseholdToo never lifts pass or push

        static bool SwfPartner(Sim a, Sim b)
        {
            InteractionInstance ia = a.CurrentInteraction, ib = b.CurrentInteraction;
            if (ia != null && (object.ReferenceEquals(ia.Target, b) || (ia.LinkedInteractionInstance != null && ia.LinkedInteractionInstance.InstanceActor == b))) return true;
            return ib != null && (object.ReferenceEquals(ib.Target, a) || (ib.LinkedInteractionInstance != null && ib.LinkedInteractionInstance.InstanceActor == a));
        }

        static int SwfRank(Sim s)
        {
            return StandWaitCore.Rank((int)s.InheritedPriority().Level, s == PlumbBob.SelectedActor);
        }

        static int SwfAdd(Sim sim, Sim b, bool atDest, int n)
        {
            if (b == null || b == sim || b.HasBeenDestroyed || b.SimDescription == null) return n;
            for (int i = 0; i < n; i++)
                if (sSwfCand[i] == b) { if (atDest) sSwfBlk[i].AtDest = true; return n; }
            if (n >= kSwfMaxBlockers) return n;
            sSwfCand[n] = b;
            sSwfBlk[n] = new StandWaitCore.Blocker();
            sSwfBlk[n].AtDest = atDest;
            return n + 1;
        }

        // The Sims that can be what keeps the plan failing: the plan's own blocker, Sims at the destination point, Sims
        // standing in the waiter.
        static int SwfBlockers(Sim sim, Route path, RoutePlanResult res, bool hhOn)
        {
            int n = 0;
            if (res.mBlockerObjectId != 0) n = SwfAdd(sim, GameObject.GetObject(new ObjectGuid(res.mBlockerObjectId)) as Sim, false, n);
            Vector3 dest = path.GetApproximateOriginalDestination();
            if (dest != Vector3.Invalid && dest != Vector3.Origin)
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>(dest, kSwfDestRadius)) n = SwfAdd(sim, s, true, n);
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>(sim.Position, kSwfSelfRadius)) n = SwfAdd(sim, s, false, n);
            ulong me = sim.ObjectId.Value;
            for (int i = 0; i < n; i++)
            {
                Sim b = sSwfCand[i];
                sSwfBlk[i].Id = b.ObjectId.Value;
                sSwfBlk[i].Rank = SwfRank(b);
                sSwfBlk[i].Pushable = RoutingComponent.IsSimPushable(sim, b);
                sSwfBlk[i].InScope = SwfInScope(b, hhOn);
                sSwfBlk[i].Partner = SwfPartner(sim, b);
                SwfState o;
                if (sSwf.TryGetValue(sSwfBlk[i].Id, out o) && o.RunBlocked > 0)
                {
                    sSwfBlk[i].Waiting = true;
                    for (int k = 0; k < o.NamedN; k++) if (o.Named[k] == me) sSwfBlk[i].NamedMe = true;
                }
            }
            return n;
        }

        // After the game's cycle handler. keep = its answer; failedBefore = the plan had failed when the cycle began.
        // failedBefore, keep and a plan that is still failed = the game left through the early return.
        static bool SwfCycle(object action, Sim sim, Route path, bool failedBefore, bool keep)
        {
            if (sim == null || path == null) return keep;
            int[] c = PatchCounters();
            try
            {
                SwfState st = SwfStateOf(sim, action);
                st.Cycles++;
                RoutePlanResult res = path.PlanResult;
                if (!keep || !failedBefore || res.Succeeded())
                {
                    st.RunBlocked = 0; st.NamedN = 0;
                    return keep;
                }
                st.Blocked++; st.RunBlocked++;
                c[kSwfBlockedCycles]++;
                if (st.Blocked == 1) c[kSwfBlockedWaits]++;
                if (st.Blocked > c[kSwfLongest]) c[kSwfLongest] = st.Blocked;
                st.Plan = res.mType + " blocker " + res.mBlockerType;

                bool fixOn = ShortcutOn("standwaitfix"), hhOn = ShortcutOn("standwaitfixhh");
                bool on = fixOn && SwfInScope(sim, hhOn);
                st.On = on;
                if (fixOn && !on) { st.Exempt = true; c[kSwfExempt]++; }

                int n = SwfBlockers(sim, path, res, hhOn || !fixOn);  // compare mode counts what both switches would do
                st.NamedN = n;
                for (int i = 0; i < n && st.Who.Length < 160; i++)
                {
                    string w = sSwfCand[i].FullName;
                    if (st.Who.IndexOf(w, StringComparison.Ordinal) >= 0) continue;
                    st.Who += (st.Who.Length > 0 ? "; " : "") + w + " (" + SwfIa(sSwfCand[i]) + (sSwfBlk[i].Pushable ? "" : ", unpushable") + ")";
                }
                for (int i = 0; i < n; i++) st.Named[i] = sSwfBlk[i].Id;
                StandWaitCore.Decision d = StandWaitCore.Decide(sim.HasExitReason(ExitReason.MidRoutePushRequested), sim.HasExitReason(ExitReason.CancelExternal),
                    SwfRank(sim), sim.ObjectId.Value, sSwfBlk, n, sSwfPush);
                if (d.Mutual > 0)
                {
                    c[kSwfMutual]++;
                    for (int i = 0; i < n; i++)
                        if (sSwfBlk[i].Waiting && sSwfBlk[i].NamedMe && st.Mutual.Length < 60 && st.Mutual.IndexOf(sSwfCand[i].FullName, StringComparison.Ordinal) < 0)
                            st.Mutual += (st.Mutual.Length > 0 ? ", " : "") + sSwfCand[i].FullName;
                }
                c[kSwfWithheld] += d.Withheld;

                if (d.Yield)
                {
                    if (!on) { if (st.WouldYield == 0) { st.WouldYield = st.Blocked; c[kSwfWouldYield]++; } return keep; }
                    // the game's own exit for a pushed or cancelled waiter, which its early return skipped
                    if (d.YieldTerminate) sSwfReturn.SetValue(action, RouteAction.ActionResult.Terminate);
                    ((RouteAction)action).AddFailureExplanation(RouteAction.FailureExplanation.CancelledByScript);
                    sSwfElected.SetValue(action, true);
                    st.Yielded = true; st.YieldCycle = st.Blocked;
                    c[kSwfYields]++;
                    return false;
                }

                // sharelanes (ShareLanes.cs, docs/686 §8), off by default: a portal the plan calls locked is tried without people at once
                bool shl = d.PassAllowed && ShlPortalPass(sim, res, st.Blocked);
                if (d.PassAllowed && (shl || StandWaitCore.PassDue(st.Blocked, ref st.NextPass, ref st.Delays, st.PushAcceptedLast)))
                {
                    if (!on && !shl)
                    {
                        if (st.WouldPass == 0) { st.WouldPass = st.Blocked; c[kSwfWouldPass]++; }
                        StandWaitCore.PassFailed(st.Blocked, ref st.NextPass, Instantiator.kStandWaitPassEvery);
                    }
                    else
                    {
                        c[kSwfProbes]++; st.Probes++;
                        bool ok = false;
                        path.SetOption(Route.RouteOption.BlockedByPeople, false);
                        try { ok = path.Replan().Succeeded(); }
                        finally { if (!ok) path.SetOption(Route.RouteOption.BlockedByPeople, true); }  // the wait goes on as the game left it
                        if (ok)
                        {
                            // people were the only thing in the way: walk on as the game does after its timeout in a crowd
                            sim.SimRoutingComponent.StartIgnoringObstacles(Instantiator.kStandWaitPassMinutes);
                            sSwfReturn.SetValue(action, RouteAction.ActionResult.ContinueAndFollowPath);
                            sSwfElected.SetValue(action, true);
                            st.Passed = true; st.PassCycle = st.Blocked;
                            c[shl && !on ? kShlPortalPass : kSwfPasses]++;
                            return false;
                        }
                        if (shl) c[kShlPortalStillLocked]++;  // locked without people too: a real lock, the wait goes on
                        if (on) StandWaitCore.PassFailed(st.Blocked, ref st.NextPass, Instantiator.kStandWaitPassEvery);
                        // docs/686 §30 (StandWaitSocial.cs): a social's spot out of reach without people too ends the wait as the timeout does
                        if (on && SwsPassFailed(st, sim, sim.HasExitReason(ExitReason.CancelExternal | ExitReason.MidRoutePushRequested), c)) return false;
                    }
                }

                st.PushAcceptedLast = false;
                if (d.Pushes > 0)
                {
                    if (!on) { if (st.WouldPush == 0) c[kSwfWouldPush]++; st.WouldPush += d.Pushes; return keep; }
                    List<Sim> list = new List<Sim>(d.Pushes);
                    for (int i = 0; i < n; i++) if (sSwfPush[i]) list.Add(sSwfCand[i]);
                    c[kSwfPushCycles]++; c[kSwfPushed] += list.Count;
                    st.PushCycles++; st.Pushed += list.Count; st.LastPushCycle = st.Blocked;
                    sim.RoutingComponent.PushSims(list, false);  // the game's call in this handler: no wait for the pushed Sims
                    foreach (Sim b in list)
                        if (!b.HasBeenDestroyed && ((b.SimRoutingComponent != null && b.SimRoutingComponent.IsInvolvedInPush)
                            || (b.InteractionQueue != null && b.InteractionQueue.HasInteractionOfType(SimRoutingComponent.BePushed.Singleton))))
                            st.PushAcceptedLast = true;
                }
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[kSwfErrors]++; }
            finally { for (int i = 0; i < kSwfMaxBlockers; i++) sSwfCand[i] = null; }
            return keep;
        }

        // The Sim's running interaction; in a posture transition there is none, then the transition and what it is for.
        static string SwfIa(Sim sim)
        {
            InteractionInstance cur = sim.CurrentInteraction;
            if (cur != null || sim.InteractionQueue == null) return RflIa(cur);
            InteractionInstance tr = sim.InteractionQueue.TransitionInteraction;
            if (tr == null) return RflIa(null);
            return "none (transition " + RflIa(tr) + " for " + RflIa(sim.InteractionQueue.GetHeadInteraction()) + ")";
        }

        static void SwfEnd(object action, Sim sim, Route path)
        {
            try
            {
                if (sim == null) return;
                SwfState st;
                ulong id = sim.ObjectId.Value;
                if (!sSwf.TryGetValue(id, out st) || !object.ReferenceEquals(st.Action, action)) return;
                sSwf.Remove(id);
                if (st.Blocked == 0) return;
                int[] c = PatchCounters();
                float elapsed = SimClock.ElapsedTime(TimeUnit.Minutes, st.Start), duration = SimRoutingComponent.DefaultStandAndWaitDuration;
                bool planOk = path != null && path.PlanResult.Succeeded();
                int kind = StandWaitCore.EndKind(st.Blocked, st.Yielded, st.Passed, planOk, st.LastPushCycle, elapsed, duration, false);
                int left = StandWaitCore.TimeoutCyclesLeft(st.Cycles, elapsed, duration);
                if (kind == StandWaitCore.kEndTimeout) c[kSwfTimeouts]++;
                if (kind == StandWaitCore.kEndYield || kind == StandWaitCore.kEndPass || kind == StandWaitCore.kEndPush) { c[kSwfResolved]++; c[kSwfCyclesLeft] += left; }
                sSwfLast = "standwait: " + sim.FullName + (sim == PlumbBob.SelectedActor ? " selected" : sim.IsInActiveHousehold ? " household" : " npc")
                    + ", " + SwfIa(sim) + ": blocked " + st.Blocked + " of " + st.Cycles + " cycles, " + elapsed.ToString("0.0") + " sim-min, at " + V(sim.Position)
                    + ", plan " + st.Plan + (st.Mutual.Length > 0 ? ", mutual with " + st.Mutual : "") + ", end " + StandWaitCore.kEndNames[kind]
                    + ", fix " + (st.On ? "on" : st.Exempt ? "exempt" : "off")
                    + (st.On ? ": yield cycle " + st.YieldCycle + ", pushed " + st.Pushed + " in " + st.PushCycles + " cycles, pass cycle " + st.PassCycle + " (" + st.Probes + " plans)"
                             : ": would yield cycle " + st.WouldYield + ", push " + st.WouldPush + ", pass cycle " + st.WouldPass)
                    + ", timeout cycles left " + left + SwsEnd(st)
                    + ", near: " + (st.Who.Length > 0 ? st.Who : "nobody");
                if (ShortcutOn("standwaitlog") && c[kSwfLines] < Instantiator.kStandWaitLogLines && DiagLine(sSwfLast)) c[kSwfLines]++;
            }
            catch (ResetException) { throw; }
            catch (Exception) { PatchCounters()[kSwfErrors]++; }
        }

        // StandWaitCheck run: the decisions of a blocked cycle with controls (read-only, nothing in the world is touched).
        //   Bug case: two Sims that named each other wait blocked; with the switch on one of them is pushed and yields.
        // StandWaitCheck result: the counters and the last wait's line.
        public static object StandWaitCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "standwait: waits " + c[kSwfWaits] + ", with blocked cycles " + c[kSwfBlockedWaits] + " (" + c[kSwfBlockedCycles] + " cycles, longest " + c[kSwfLongest]
                + "), ran to the timeout " + c[kSwfTimeouts] + "; on: yields " + c[kSwfYields] + ", push cycles " + c[kSwfPushCycles] + " (" + c[kSwfPushed] + " Sims), plans without people "
                + c[kSwfProbes] + ", passes " + c[kSwfPasses] + ", waits resolved " + c[kSwfResolved] + " (timeout cycles left " + c[kSwfCyclesLeft] + "), exempt cycles " + c[kSwfExempt]
                + "; off: would yield " + c[kSwfWouldYield] + ", would push " + c[kSwfWouldPush] + ", would pass " + c[kSwfWouldPass] + "; mutual cycles " + c[kSwfMutual]
                + ", pushes withheld " + c[kSwfWithheld] + ", errors " + c[kSwfErrors] + ", lines " + c[kSwfLines] + ", open " + sSwf.Count + "; " + GlMembers(sSwfMissing)
                + (sSwfLast.Length > 0 ? "; last: " + sSwfLast : "");
            if (what == "result") return counters;
            if (what != "run") return "standwait <run|result>";
            bool on = ShortcutOn("standwaitfix");
            StandWaitCore.Blocker[] b = new StandWaitCore.Blocker[2];
            bool[] push = new bool[2];
            // A (id 1) and B (id 2) wait blocked and named each other; equal rank, so A outranks B
            b[0] = new StandWaitCore.Blocker(); b[0].Id = 2; b[0].Pushable = true; b[0].InScope = true; b[0].Waiting = true; b[0].NamedMe = true;
            StandWaitCore.Decision a = StandWaitCore.Decide(false, false, 0, 1, b, 1, push);
            bool aPushes = a.Pushes == 1 && push[0] && a.Mutual == 1;
            b[0].Id = 1;
            StandWaitCore.Decision bd = StandWaitCore.Decide(false, false, 0, 2, b, 1, push);
            bool bWithholds = bd.Pushes == 0 && bd.Withheld == 1;
            StandWaitCore.Decision by = StandWaitCore.Decide(true, false, 0, 2, b, 1, push);
            bool bYields = by.Yield && !by.YieldTerminate;
            bool fixedNow = on && aPushes && bWithholds && bYields;
            StandWaitCore.Decision cancel = StandWaitCore.Decide(false, true, 0, 2, b, 1, push);
            bool ctlCancel = cancel.Yield && cancel.YieldTerminate;
            b[0].Partner = true;
            bool ctlPartner = StandWaitCore.Decide(false, false, 0, 2, b, 1, push).Pushes == 0;
            b[0].Partner = false; b[0].InScope = false;
            bool ctlScope = StandWaitCore.Decide(false, false, 0, 2, b, 1, push).Pushes == 0;
            b[0].InScope = true; b[0].Waiting = false; b[0].NamedMe = false; b[0].AtDest = true;
            StandWaitCore.Decision dest = StandWaitCore.Decide(false, false, 0, 2, b, 1, push);
            bool ctlDest = !dest.PassAllowed && dest.Pushes == 1;
            int next = 2, delays = 0;
            bool ctlPass = !StandWaitCore.PassDue(1, ref next, ref delays, false) && !StandWaitCore.PassDue(2, ref next, ref delays, true)
                && StandWaitCore.PassDue(3, ref next, ref delays, true);
            bool controls = ctlCancel && ctlPartner && ctlScope && ctlDest && ctlPass;
            return "standwaitfix check (switch " + (on ? "on" : "off") + ", household " + (ShortcutOn("standwaitfixhh") ? "on" : "off") + "): mutual wait broken (one pushes, the other yields)=" + fixedNow
                + "; control: cancel ends the route=" + ctlCancel + ", partner not pushed=" + ctlPartner + ", out-of-scope Sim not pushed=" + ctlScope
                + ", no pass into a Sim at the destination=" + ctlDest + ", pass waits a cycle for an accepted push=" + ctlPass + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, sSwfMissing.Length == 0) + "; " + counters;
        }
    }
}
