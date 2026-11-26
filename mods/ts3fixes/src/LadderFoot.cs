using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ladderfootshoo / ladderfootshoohh (docs/183 §9, default off). Behaviour fix: a floor that is reached only by a
    // ladder (or one stair) is cut off while Sims stand on the link's lane ends.
    //
    // [verified, native decompile] The route search costs a level link's traverse edge in PortalEdgeCost 0x634500: after
    // the lock test it runs a point test on each lane end (LotPlan_PortalPointCheck 0x6338A0, the routing cell's class),
    // and a failed test gives the edge the cost of a locked portal (1e6, a wall). A standing Sim's footprint is in the
    // routing cells, so a Sim that stops on a lane end closes that lane for every plan, in both directions; with all
    // lanes closed the plan answers "no path" and the Sim's route fails at once.
    // [verified, decompiled script] Doors (Door, NonAnimatedDoor) and spiral stairs ask Sims standing on their lane ends
    // to step away (DoAdditionalLaneChecks: Sims within 0.5 m that are not walking, RoutingComponent.PushSimsStatic), but
    // only when a Sim arrives at them, after a plan that succeeded. Ladder.LadderPortalComponent has no such step and
    // its lane test ignores Sims; a ladder puts each climber exactly on a lane end (PortalClimb.PositionSim), and its
    // two lanes lie so close that Sims who stay where they arrived close it.
    //
    // Fix (rules in LadderFootCore): for a plan of a Sim to another level of its lot that failed with "no path" (or, for
    // a Sim already at the link, "destination portal locked"), the
    // level links on the way are tested with the game's slot test. A link whose lanes are free of objects but taken by
    // Sims, with no other link between the same two levels free, gets the game's own step-away for the Sims standing on
    // its taken ends (PushSimsStatic, the door's distances and "out of a conversation", at the planning Sim's priority:
    // an idle Sim goes at once, a busy one only when the plan outranks what it does, else after it; the game's own
    // refusals hold: toddlers and below, push immunity, Sims being pushed). Then the planning Sim waits in its own task,
    // as a Sim at a door waits for the Sims it pushed, up to kLadderFootWaitMinutes, and plans once more.
    // Nothing is teleported; nothing is cancelled that the game's priority rule would not cancel.
    //
    // Scope: ladderfootshoo covers Sims the player does not control, as planner and as the Sim asked to step away;
    // household and selected Sims in either role need ladderfootshoohh as well (or [Experimental] HouseholdToo).
    // Off = compare mode for household plans while routefaillog is on: counted, nothing asked.
    // Counters 4352-4363. Check: LadderFootCheck. The level-link scan is in LevelLink.cs.
    public static partial class Fixes
    {
        const int kLfsScans = 4352, kLfsDetected = 4353, kLfsWould = 4354, kLfsExempt = 4355, kLfsShooed = 4356, kLfsRefused = 4357,
            kLfsWaits = 4358, kLfsRetryOk = 4359, kLfsGaveUp = 4360, kLfsNoWait = 4361, kLfsLaterOk = 4362, kLfsErrors = 4363;
        const float kLfsRadius = 0.5f;  // the game's radius at doors and spiral stairs
        const int kLfsMaxLanes = 8, kLfsMaxLines = 120, kLfsLaterMinutes = 60;
        const uint kLfsPollTicks = 10;  // the game's poll when it waits for pushed Sims

        sealed class LfsCase
        {
            public Sim Planner;
            public GameObject Link;
            public string LinkText = "";
            public List<Sim> Blockers = new List<Sim>();  // standing on taken lane ends
            public List<Sim> Asked = new List<Sim>();     // have the step-away queued or running
            public int NewlyAsked, Refused;
            public bool Locked;  // the plan answered "portal locked", not "no path"
        }

        static readonly Dictionary<ulong, long> sLfsLast = new Dictionary<ulong, long>();   // SimDescriptionId -> ticks of its last scan
        static readonly Dictionary<ulong, long> sLfsOpen = new Dictionary<ulong, long>();   // planners whose case ended without a successful plan
        static int sLfsLines;
        static string sLfsLastLine = "";

        static Sim LfsFollower(ScriptCore.Route r)
        {
            IScriptProxy f = r.Follower;
            return f != null ? f.Target as Sim : null;
        }

        // The two answers a link closed by Sims gives (forced repro ladderfoot-force, Colonial Casa storms s422 / s424):
        // "no path" from a plan that still has to reach the link, "destination portal locked" from the replans of a Sim
        // that already stands at it (EA's stand-and-wait cycles, which then run to their 50 sim-min timeout).
        static bool LfsKind(RoutePlanResultType t)
        {
            return t == RoutePlanResultType.FailedBecauseNoPathToDestination || t == RoutePlanResultType.FailedBecauseDestinationPortalLocked;
        }

        static bool LfsStepAwayPending(Sim s)
        {
            return s.InteractionQueue != null && (s.InteractionQueue.HasInteractionOfType(SimRoutingComponent.BePushed.Singleton)
                || (s.SimRoutingComponent != null && s.SimRoutingComponent.IsInvolvedInPush));
        }

        // The Sims standing on the taken ends of the Sim-closed links that the way between the two levels needs.
        // Null = no such link. Reads only.
        static LfsCase LfsScan(Sim sim, Lot lot, int simLevel, int goalLevel)
        {
            int portals;
            List<LevelLink> links = LevelLinks(lot, out portals);
            Dictionary<int, bool> freePair = new Dictionary<int, bool>();  // level pair -> some link between them has a free lane
            List<LevelLink> closed = new List<LevelLink>();
            List<int[]> closedLanes = new List<int[]>();
            foreach (LevelLink k in links)
            {
                if (!k.TwoLevels || !k.Ends || !LevelLinkOnWay(k, simLevel, goalLevel)) continue;
                int n = (int)Math.Min(k.Lanes, (uint)kLfsMaxLanes);
                int[] lanes = new int[n];
                for (int i = 0; i < n; i++)
                {
                    Slot[] s = LevelLinkLane(k, (uint)i);
                    lanes[i] = s == null ? LadderFootCore.kLaneOther : LadderFootCore.Lane(LevelLinkSlot(sim, k.Obj, s[0], false, false),
                        LevelLinkSlot(sim, k.Obj, s[0], true, false), LevelLinkSlot(sim, k.Obj, s[1], false, true), LevelLinkSlot(sim, k.Obj, s[1], true, true));
                }
                int pair = Math.Min(k.Al.mLevel, k.Zl.mLevel) * 1024 + Math.Max(k.Al.mLevel, k.Zl.mLevel);
                if (LadderFootCore.HasFreeLane(lanes, n)) freePair[pair] = true;
                else if (LadderFootCore.ClosedBySims(lanes, n)) { closed.Add(k); closedLanes.Add(lanes); }
            }
            LfsCase cs = null;
            for (int j = 0; j < closed.Count; j++)
            {
                LevelLink k = closed[j];
                int pair = Math.Min(k.Al.mLevel, k.Zl.mLevel) * 1024 + Math.Max(k.Al.mLevel, k.Zl.mLevel);
                if (freePair.ContainsKey(pair)) continue;  // another link between these levels is open: this one is not why the plan failed
                if (cs == null) { cs = new LfsCase(); cs.Planner = sim; cs.Link = k.Obj; }
                cs.LinkText += (cs.LinkText.Length > 0 ? "; " : "") + k.Type + " 0x" + k.Obj.ObjectId.Value.ToString("X") + " level " + k.Al.mLevel + ">" + k.Zl.mLevel + " lanes";
                int[] lanes = closedLanes[j];
                for (int i = 0; i < lanes.Length; i++)
                {
                    cs.LinkText += " " + LadderFootCore.LaneName(lanes[i]);
                    if (lanes[i] != LadderFootCore.kLaneSims) continue;
                    Slot[] s = LevelLinkLane(k, (uint)i);
                    for (int e = 0; s != null && e < 2; e++)
                    {
                        if (LevelLinkSlot(sim, k.Obj, s[e], true, e == 1) != LadderFootCore.kSim) continue;
                        foreach (Sim o in Sims3.Gameplay.Queries.GetObjects<Sim>(k.Obj.GetSlotPosition(s[e]), kLfsRadius))
                            if (o != null && LadderFootCore.Blocker(o == sim, o.HasBeenDestroyed, o.IsRouting) && !cs.Blockers.Contains(o)) cs.Blockers.Add(o);
                    }
                }
            }
            return cs;
        }

        // A failed plan (any of the four plan hooks). Returns the case when Sims were asked to step away (or already
        // were) and the planner may now wait for them; null otherwise.
        static LfsCase LfsFailed(ScriptCore.Route r, Sim sim, int kind, RoutePlanResult res)
        {
            int[] c = PatchCounters();
            try
            {
                if (!LfsKind(res.mType)) return null;
                bool on = ShortcutOn("ladderfootshoo");
                // off: only household plans are counted while routefaillog is on (a scan of the lot per look)
                if (!on && !(sim.IsInActiveHousehold && ShortcutOn("routefaillog"))) return null;
                Lot lot = sim.LotCurrent;
                if (lot == null || lot.IsWorldLot || sim.SimDescription == null) return null;
                Vector3 goal = r.GetApproximateOriginalDestination();
                LotLocation gl = LotLocation.Invalid;
                ulong goalLot = World.GetLotLocation(goal, ref gl, eRoomDefinition.Default);
                if (!LadderFootCore.Wants(true, true, goalLot == lot.LotId, sim.Level, gl.mLevel)) return null;
                ulong id = sim.SimDescription.SimDescriptionId;
                long now = SimClock.CurrentTicks, last;
                bool seen = sLfsLast.TryGetValue(id, out last);
                if (!LadderFootCore.Due(seen, now, last, SimClock.ConvertToTicks(Instantiator.kLadderFootCooldownMinutes, TimeUnit.Minutes))) return null;
                c[kLfsScans]++;
                LfsCase cs = LfsScan(sim, lot, sim.Level, gl.mLevel);
                if (cs == null) return null;
                cs.Locked = res.mType == RoutePlanResultType.FailedBecauseDestinationPortalLocked;
                // the cooldown starts only on a found case: a first failure while the ladder was in use (a climber on it,
                // not standing Sims) must not hide the next failure, when Sims stand on its end (s411)
                if (sLfsLast.Count > 256) sLfsLast.Clear();
                sLfsLast[id] = now;
                bool hh = ShortcutOn("ladderfootshoohh");
                int what = LadderFootCore.Decide(true, cs.Blockers.Count, on, hh, HouseholdExempt(sim));
                if (what == LadderFootCore.kKeep) return null;
                c[kLfsDetected]++;
                if (what == LadderFootCore.kWould) { c[kLfsWould]++; LfsLine(cs, kind, gl.mLevel, "fix off: would ask " + cs.Blockers.Count); return null; }
                if (what == LadderFootCore.kExempt) { c[kLfsExempt]++; LfsLine(cs, kind, gl.mLevel, "exempt (household)"); return null; }
                List<Sim> ask = new List<Sim>();
                foreach (Sim o in cs.Blockers)
                {
                    bool pending = LfsStepAwayPending(o);
                    if (pending) cs.Asked.Add(o);
                    else if (LadderFootCore.MayAsk(hh, HouseholdExempt(o), false)) ask.Add(o);
                    else cs.Refused++;
                }
                if (ask.Count > 0)
                {
                    // the door's own call (Door / StaircaseSpiral DoAdditionalLaneChecks), not waited for here; a busy Sim
                    // is asked by the game's priority rule (AllowForHigherPriority) at the planning Sim's priority
                    RoutingComponent.PushSimsStatic(sim, ask, SimRoutingComponent.PushSimsAwayDistanceMin, SimRoutingComponent.PushSimsAwayDistanceMax,
                        true, false, sim.InheritedPriority(), true);
                    foreach (Sim o in ask)
                    {
                        if (LfsStepAwayPending(o)) { cs.Asked.Add(o); cs.NewlyAsked++; }
                        else cs.Refused++;
                    }
                }
                c[kLfsShooed] += cs.NewlyAsked;
                c[kLfsRefused] += cs.Refused;
                if (sLfsOpen.Count > 64) sLfsOpen.Clear();
                sLfsOpen[id] = now;
                if (cs.Asked.Count == 0) { c[kLfsGaveUp]++; LfsLine(cs, kind, gl.mLevel, "asked 0 of " + cs.Blockers.Count + " (the game's push rules refuse them): gave up"); return null; }
                cs.LinkText += ", goal level " + gl.mLevel;
                return cs;
            }
            catch (ResetException) { throw; }
            catch (Exception) { c[kLfsErrors]++; return null; }
        }

        // The planner's wait for the Sims it asked, in its own task. One of LadderFootCore.kWait*.
        static int LfsWait(LfsCase cs, float capMinutes, out float waited)
        {
            DateAndTime t0 = SimClock.CurrentTime();
            waited = 0f;
            for (;;)
            {
                int pending = 0;
                foreach (Sim o in cs.Asked) if (!o.HasBeenDestroyed && LfsStepAwayPending(o)) pending++;
                int step = LadderFootCore.WaitStep(pending, waited, capMinutes, cs.Planner.HasBeenDestroyed || cs.Planner.HasExitReason(ExitReason.Canceled));
                if (step != LadderFootCore.kWaitOn) return step;
                Simulator.Sleep(kLfsPollTicks);
                waited = SimClock.ElapsedTime(TimeUnit.Minutes, t0);
            }
        }

        // A plan of a Sim succeeded: was it one whose ladder case ended without a path? (one dictionary test per plan
        // while such cases are open, nothing otherwise)
        static void LfsSucceeded(Sim sim)
        {
            try
            {
                if (sim.SimDescription == null) return;
                ulong id = sim.SimDescription.SimDescriptionId;
                long t;
                if (!sLfsOpen.TryGetValue(id, out t)) return;
                sLfsOpen.Remove(id);
                long age = SimClock.CurrentTicks - t;
                if (age >= 0 && age <= SimClock.ConvertToTicks(kLfsLaterMinutes, TimeUnit.Minutes)) PatchCounters()[kLfsLaterOk]++;
            }
            catch (Exception) { }
        }

        static void LfsLine(LfsCase cs, int kind, int goalLevel, string tail)
        {
            try
            {
                StringBuilder b = new StringBuilder("ladderfoot: ").Append(cs.Planner.FullName).Append(", ").Append(kRflKind[kind & 3]).Append(cs.Locked ? " portal locked from level " : " no path from level ")
                    .Append(cs.Planner.Level).Append(goalLevel >= -64 ? " to level " + goalLevel : "").Append(", ").Append(RflIa(cs.Planner.CurrentInteraction))
                    .Append(", closed by Sims: ").Append(cs.LinkText).Append(", standing there:");
                int n = 0;
                foreach (Sim o in cs.Blockers)
                {
                    if (n++ >= 4) { b.Append(" +").Append(cs.Blockers.Count - 4); break; }
                    b.Append(' ').Append(o.FullName).Append('@').Append(V(o.Position)).Append(" (").Append(RflIa(o.CurrentInteraction)).Append(')');
                }
                b.Append("; ").Append(tail);
                sLfsLastLine = b.ToString();
                if (sLfsLines++ < kLfsMaxLines) DiagLine(sLfsLastLine);
            }
            catch (Exception) { }
        }

        public partial class RouteBackoffHook
        {
            static ScriptCore.Route sLfsRetrying;  // the route whose second plan runs: its hooks do not ask again (keyed by route: other plans made meanwhile are not this one)

            // After each plan hook's own bookkeeping. A plan that failed for Sims on a level link's lane ends: they are
            // asked to step away, the planner waits for them and this plans once more (through the same hook).
            RoutePlanResult LfsAfterPlan(int kind, Vector3 point, RoutePlanResult r)
            {
                if (object.ReferenceEquals(sLfsRetrying, this)) return r;  // the second plan: counted by the first
                if (r.mType == RoutePlanResultType.Succeeded)
                {
                    if (sLfsOpen.Count > 0) { Sim ok = LfsFollower(this); if (ok != null) LfsSucceeded(ok); }
                    return r;
                }
                if (!LfsKind(r.mType)) return r;
                Sim sim = LfsFollower(this);
                if (sim == null) return r;
                LfsCase cs = LfsFailed(this, sim, kind, r);
                if (cs == null) return r;
                int[] c = PatchCounters();
                float cap = 0f, waited = 0f;
                bool mayWait = false;
                try
                {
                    cap = LadderFootCore.WaitCap(Instantiator.kLadderFootWaitMinutes, SimRoutingComponent.TotalSimMinutesToWaitForSimsToBePushed);
                    IScriptProxy tp = Simulator.GetProxy(Simulator.CurrentTask);
                    mayWait = LadderFootCore.MayWait(cs.Asked.Count, tp != null && object.ReferenceEquals(tp.Target, sim), MayYield(), JigPending(sim) != 0, cap);
                }
                catch (Exception) { c[kLfsErrors]++; }
                string asked = "asked " + cs.NewlyAsked + " (" + cs.Asked.Count + " stepping away, " + cs.Refused + " refused)";
                if (!mayWait)
                {
                    c[kLfsNoWait]++;
                    LfsLine(cs, kind, -999, asked + ", not waited for (not the Sim's own task, or no wait allowed)");
                    return r;
                }
                c[kLfsWaits]++;
                int step = LfsWait(cs, cap, out waited);  // sleeps; a reset leaves through here
                if (step != LadderFootCore.kWaitReplan)
                {
                    c[kLfsGaveUp]++;
                    LfsLine(cs, kind, -999, asked + ", waited " + waited.ToString("0.0") + " min: gave up (" + LadderFootCore.kWaitNames[step] + ")");
                    return r;
                }
                RoutePlanResult r2;
                sLfsRetrying = this;
                try { r2 = kind == 0 ? PlanHook() : kind == 1 ? ReplanHook() : kind == 2 ? PlanFromPointHook(point) : ReplanFromPointHook(point); }
                finally { sLfsRetrying = null; }
                bool ok2 = r2.mType == RoutePlanResultType.Succeeded;
                c[ok2 ? kLfsRetryOk : kLfsGaveUp]++;
                if (ok2) { try { if (sim.SimDescription != null) sLfsOpen.Remove(sim.SimDescription.SimDescriptionId); } catch (Exception) { } }
                LfsLine(cs, kind, -999, asked + ", waited " + waited.ToString("0.0") + " min: " + (ok2 ? "retry ok" : "gave up (second plan " + r2.mType + ")"));
                return r2;
            }
        }

        // LadderFootCheck run: the rules with controls (read-only, nothing in the world is touched).
        //   Bug case: both lanes of a ladder free of objects, their lower ends taken by Sims; with the switch on they are asked.
        // LadderFootCheck result: the counters and the last line.
        // LadderFootCheck lot: the level links of the selected Sim's lot, their lanes by the game's slot test and the Sims on their ends (read-only).
        public static object LadderFootCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "ladderfoot: scans " + c[kLfsScans] + ", links closed by Sims " + c[kLfsDetected] + "; on: Sims asked to step away " + c[kLfsShooed]
                + " (refused by the game's push rules or exempt " + c[kLfsRefused] + "), waits " + c[kLfsWaits] + ", retry ok " + c[kLfsRetryOk] + ", gave up " + c[kLfsGaveUp]
                + ", not waited for " + c[kLfsNoWait] + ", a later plan of the Sim succeeded " + c[kLfsLaterOk] + ", exempt (household planner) " + c[kLfsExempt]
                + "; off: would ask " + c[kLfsWould] + "; errors " + c[kLfsErrors] + "; wait cap " + Instantiator.kLadderFootWaitMinutes + " min, cooldown "
                + Instantiator.kLadderFootCooldownMinutes + " min" + (sLfsLastLine.Length > 0 ? "; last: " + sLfsLastLine : "");
            if (what == "result") return counters;
            if (what == "lot") return LfsLot();
            if (what != "run") return "ladderfoot <run|result|lot>";
            bool on = ShortcutOn("ladderfootshoo"), hh = ShortcutOn("ladderfootshoohh");
            const int F = LadderFootCore.kFree, S = LadderFootCore.kSim, O = 3;
            // the logged case: a ladder's two lanes, upper ends free, lower ends free of objects and taken by Sims
            int[] ladder = { LadderFootCore.Lane(F, F, F, S), LadderFootCore.Lane(F, F, F, S) };
            bool closed = LadderFootCore.ClosedBySims(ladder, 2);
            bool wants = LadderFootCore.Wants(true, true, true, 1, 2);
            bool fixedNow = closed && wants && LadderFootCore.Decide(true, 2, on, true, false) == LadderFootCore.kShoo
                && LadderFootCore.MayAsk(true, false, false) && LadderFootCore.MayWait(2, true, true, false, 5f)
                && LadderFootCore.WaitStep(0, 0.5f, 5f, false) == LadderFootCore.kWaitReplan;
            bool wouldNow = closed && wants && LadderFootCore.Decide(true, 2, false, true, false) == LadderFootCore.kWould;
            bool ctlFreeLane = !LadderFootCore.ClosedBySims(new int[] { LadderFootCore.Lane(F, F, F, S), LadderFootCore.Lane(F, F, F, F) }, 2);
            bool ctlObject = LadderFootCore.Lane(O, O, F, S) == LadderFootCore.kLaneOther
                && !LadderFootCore.ClosedBySims(new int[] { LadderFootCore.Lane(O, O, F, F), LadderFootCore.Lane(F, F, O, O) }, 2);
            bool ctlSameLevel = !LadderFootCore.Wants(true, true, true, 1, 1) && !LadderFootCore.Wants(false, true, true, 1, 2) && !LadderFootCore.Wants(true, true, false, 1, 2);
            bool ctlPlanner = !LadderFootCore.Blocker(true, false, false) && !LadderFootCore.Blocker(false, false, true) && LadderFootCore.Blocker(false, false, false);
            bool ctlScope = LadderFootCore.Decide(true, 2, true, false, true) == LadderFootCore.kExempt && !LadderFootCore.MayAsk(false, true, false)
                && LadderFootCore.MayAsk(false, false, false);
            bool ctlOnce = !LadderFootCore.MayAsk(true, false, true);
            bool ctlWait = !LadderFootCore.MayWait(2, false, true, false, 5f) && !LadderFootCore.MayWait(2, true, false, false, 5f) && !LadderFootCore.MayWait(2, true, true, true, 5f)
                && LadderFootCore.WaitStep(1, 5f, 5f, false) == LadderFootCore.kWaitTimeout && LadderFootCore.WaitStep(1, 1f, 5f, true) == LadderFootCore.kWaitCanceled
                && LadderFootCore.WaitStep(1, 1f, 5f, false) == LadderFootCore.kWaitOn && LadderFootCore.WaitCap(30f, 15f) == 15f;
            bool controls = closed && wants && wouldNow && ctlFreeLane && ctlObject && ctlSameLevel && ctlPlanner && ctlScope && ctlOnce && ctlWait;
            return "ladderfoot check (switch " + (on ? "on" : "off") + ", household " + (hh ? "on" : "off") + "): Sims on a ladder's lane ends are asked and the plan is made again=" + fixedNow
                + "; control: off counts it=" + wouldNow + ", a free lane keeps the game's result=" + ctlFreeLane + ", an object on an end is not this=" + ctlObject
                + ", same level or another failure is not this=" + ctlSameLevel + ", the planner and walking Sims are not asked=" + ctlPlanner
                + ", household without its switch exempt=" + ctlScope + ", a Sim is asked once=" + ctlOnce + ", wait rules=" + ctlWait + "; controls ok=" + controls
                + "; verdict: " + GlVerdict(on, fixedNow, controls, true) + "; " + counters;
        }

        // The Sims at one lane end with what decides whether the game's own pushes reach them: walking, in a conversation
        // (EA's stand-and-wait push leaves those out; the door's push and this fix do not), what they do.
        static void LfsLotSims(StringBuilder b, Vector3 end)
        {
            Sim[] at = Sims3.Gameplay.Queries.GetObjects<Sim>(end, kLfsRadius);
            b.Append(' ').Append(at.Length);
            for (int i = 0; i < at.Length && i < 4; i++)
            {
                Sim o = at[i];
                if (o == null) continue;
                b.Append(i == 0 ? " (" : ", ").Append(o.FullName).Append(o.IsRouting ? " walking" : " standing").Append(o.Conversation != null ? ", in a conversation" : "")
                    .Append(", ").Append(o.Posture != null ? o.Posture.GetType().Name : "no posture").Append(", ").Append(RflIa(o.CurrentInteraction));
            }
            if (at.Length > 0) b.Append(')');
        }

        static string LfsLot()
        {
            try
            {
                Sim sim = PlumbBob.SelectedActor;
                if (sim == null || sim.LotCurrent == null || sim.LotCurrent.IsWorldLot) return "ladderfoot lot: no selected Sim on a lot";
                int portals;
                List<LevelLink> links = LevelLinks(sim.LotCurrent, out portals);
                StringBuilder b = new StringBuilder("ladderfoot lot: ").Append(sim.FullName).Append(" on level ").Append(sim.Level).Append(", portal objects ").Append(portals).Append(", level links:");
                int shown = 0;
                foreach (LevelLink k in links)
                {
                    if (!k.TwoLevels || !k.Ends) continue;
                    if (shown++ >= 8) { b.Append(" ..."); break; }
                    b.Append(' ').Append(k.Type).Append(" level ").Append(k.Al.mLevel).Append('>').Append(k.Zl.mLevel).Append(" [");
                    int n = (int)Math.Min(k.Lanes, (uint)kLfsMaxLanes);
                    for (int i = 0; i < n; i++)
                    {
                        Slot[] s = LevelLinkLane(k, (uint)i);
                        int lane = s == null ? LadderFootCore.kLaneOther : LadderFootCore.Lane(LevelLinkSlot(sim, k.Obj, s[0], false, false),
                            LevelLinkSlot(sim, k.Obj, s[0], true, false), LevelLinkSlot(sim, k.Obj, s[1], false, true), LevelLinkSlot(sim, k.Obj, s[1], true, true));
                        b.Append(i > 0 ? " " : "").Append(LadderFootCore.LaneName(lane));
                    }
                    b.Append("] Sims within ").Append(kLfsRadius).Append(" m of end A");
                    LfsLotSims(b, k.A);
                    b.Append(", of end B");
                    LfsLotSims(b, k.Z);
                    b.Append(';');
                }
                return shown == 0 ? b.Append(" none").ToString() : b.ToString();
            }
            catch (ResetException) { throw; }
            catch (Exception e) { return "ladderfoot lot: " + e.GetType().Name; }
        }
    }
}
