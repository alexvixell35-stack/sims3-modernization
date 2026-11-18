using System;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // autonomyfailcooldown (tuning kAutonomyFailCooldown, off by default; a cooldown, not an exact fix). Autonomy re-picks
    // interactions whose route keeps failing.
    // 11-17/18 runs, active-household autonomous failed plans: BP DoggySwimAround 1018, DoggyGetInPool 834, GetInPool 735
    // (Lala / Tiara Angelista), SV MopBurnt 678 (Keatons, 336 in one run), BP LookOutWindow 453, TB laundry 556, IP Scare 270;
    // 49-73% of the lines on a fixed target repeat the same Sim's previous failure. [verified, decompiled EA and simler90
    // UPDATE242] EA's only failure memory is per object for every Sim (GameObject.UpdateBlockTime from
    // InteractionQueue.ProcessOneInteraction when an ExitReason.CausesBlocking bit is set: IsBlocked 1 sim-minute, then
    // GetBlockingScore fades back over 30 sim-minutes), so the same Sim picks the same unreachable target again.
    //
    // Fix: when the same Sim's autonomous run of one interaction definition on one target object ends unsucceeded with a
    // failed route twice within kAfcWinMin sim-minutes, autonomy's scoring call for that (Sim, definition type, target) is
    // answered 0 for kAfcLenMin sim-minutes (InteractionObjectPair.TestAndCalculateScore, the one scoring entry for
    // autonomy pairs; the Test is not even run). A different object (another pool, window) stays open.
    // - Failure (InteractionQueue.DeQueue(false), AibHook): the head interaction is autonomous (InteractionFlags.Autonomous),
    //   not Cancelled, its Sim has no ExitReason.CancelExternal bit (player cancel, higher priority, script, posture), and a
    //   route failed: EA's ExitReason.RouteFailed (DoRoute with a failed plan) or the Sim's last plan failed while this
    //   interaction was the head (RflPlan, RouteFailLog.cs). A succeeded run (DeQueue(true)) clears the row.
    // - Refusal: only scoring calls whose search flags are Autonomous and not UserDirected (Autonomy.GetDefaultSearchFlags
    //   gives the PieMenuChoices search UserDirected); player-queued interactions never pass this method and are never
    //   counted (not Autonomous). A refused offer is only a pick autonomy does not make: no route, plan or running interaction
    //   changes. Household rule: it is still a skip, so household, selected and unknown-scope Sims (IsPlayerSim, not the
    //   HouseholdToo-liftable HouseholdExempt) are never recorded or refused; their failures are only counted [4011].
    // - A continuation counts against the autonomous interaction that pushed it (2026-11-18): EA's continuation takes the
    //   pusher's GroupId (InteractionQueue.PushAsContinuation -> SetAsContinuation) [verified, decompiled]. Sim+MopBurntTiles
    //   (target the Sim) pushes Terrain.MopBurnt and itself ends succeeded, so a success with a continuation of its group
    //   queued behind it clears nothing; the continuation's end is the verdict, on the pusher's (definition, target) row.
    //   So a failing MopBurnt / MopPuddle chain cools down that Sim's MopBurntTiles / MopPuddles offer.
    // - Not keyed on their own: Terrain targets (MopBurnt, PlayInPuddle): each try goes to a new tile; burnttilefloor learns
    //   those tiles, and a pushed one counts on its pusher (above).
    // - All targets (2026-11-18): route failures of one Sim's runs of one definition type on kDistinct (3) different targets
    //   within kAfcWinMin also refuse that definition's offers on every target for kAfcLenMin (AfcDefRow). The per-target rows
    //   are unchanged; a success clears both.
    // - LookOutWindow is not a failing interaction (2026-11-18, etl14f af18-bp-31361-m, household dog Roxy, 18 failed plans on
    //   WindowEuroMullion2x1, 0 rows). Windows+LookOutWindow is autonomy-scored (only Windows.OnStartup adds it; pets: same level,
    //   LookOutWindowFunScoring 1) and one run is one window [verified, decompiled EA = simler90 UPDATE242, callgraph]. Its
    //   RouteToWindow plans the paws spot on both sides of the window (PlanToMatrix each) and follows the one that planned; only
    //   if that DoRoute fails does it try the front-of-window cone, and Run returns false only when the cone's DoRoute fails too
    //   (SimRoutingComponent.DoRoute adds ExitReason.RouteFailed for a failed plan). The 13 NoDestinationSpecified plans are the
    //   outside spot of an upstairs window (goal room 0, mid-air); Roxy's next route end ("gohere end: cmd 4") was "route ok" at
    //   the inside spot 12 times: DeQueue(true), the row cleared, correctly. The other runs (the 5 NoPathToDestination plans
    //   included) ended "route false exit HigherPriorityNext" (a push of higher priority, an ExitReason.CancelExternal bit:
    //   never counted) or "route ok" [verified, log]. Counting a succeeded run whose last plan failed would cool down windows
    //   she reaches, so it is not done. A real LookOutWindow failure (cone route failed, no cancel) counts as any other.
    // - Script pushes (2026-11-18): a Hidden head was never picked from a scored offer (AfcRow.OwnRow): its failure records no
    //   row [4016]. DoggySwimAround: SwimmingInPool.SwimAroundCallback pushes it Hidden from a repeating posture alarm (every
    //   kMinumumMinutesToSatisfySwimming = 30 sim-minutes); simler90's Run ends at the first failed Wander, so each failure is
    //   its own run [verified, decompiled simler90 UPDATE242]. etl14f m: 12 of Roxy's cooldown lines were DoggySwimAround on
    //   one Pool [inferred: the alarm's pushes]. Pool.OnStartup also offers it to autonomy (Test: swimming in that pool); those
    //   picks are not Hidden and still count.
    // - Locked doors (2026-11-18): a failure within kAfcLockMin (10) sim-minutes of a FailedBecauseDestinationPortalLocked
    //   plan of that Sim never counts [4015]. etl14e af19-ap-31373-j: household Birds' CustomUseToilet on ToiletRanch failed
    //   8 times PortalLocked (+1 PlanFromPoint NoPath fallback), the bathroom door locked for privacy while another Sim was in,
    //   and the cooldown refused their only toilet for 120 sim-minutes (bladder risk). A lock clears on its own; a door that
    //   stays locked keeps EA's behaviour (no cooldown), the same as before this fix.
    // In memory only (never saved), capped, cleared at world quit and load.
    // Counters [4007] autonomous offers refused, [4008] would have been refused (switch off), [4009] route failures recorded,
    // [4010] cooldowns started, [4011] route failures of a household, selected or unknown-scope Sim not recorded, [4012] errors, [4013] route failures of a
    // continuation recorded on its pusher, [4014] all-targets cooldowns started ("... on all targets for ..." line), [4015]
    // route failures not counted after a locked door, [4016] route failures of a Hidden (script-pushed) head not recorded.
    // One "routefail autonomyfailcooldown:" line per new cooldown (first kAfcLines): the
    // "routefail" prefix has its own ts3native cap (userdiag.cpp); plain lines share one 400-line cap that play/nearvenue/
    // iaoutcome lines use up early (etl14d j: capped at log line 1709, first cooldown at 19041, 0 lines). Self-test:
    // tools/selftest/autonomyfailcooldown.
    public static partial class Fixes
    {
        const float kAfcWinMin = 60f, kAfcLenMin = 120f;
        const int kAfcSims = 256, kAfcRowsPerSim = 16, kAfcLines = 200;
        // Sim object id -> rows
        static readonly Dictionary<ulong, List<AfcRow>> sAfcRows = new Dictionary<ulong, List<AfcRow>>();
        // Sim object id -> per-definition rows (failed targets, all-targets cooldown); same caps as sAfcRows
        static readonly Dictionary<ulong, List<AfcDefRow>> sAfcDefRows = new Dictionary<ulong, List<AfcDefRow>>();
        // Sim -> (head interaction, result) of its last plan when that plan failed and the head was autonomous
        static readonly Dictionary<Sim, KeyValuePair<InteractionInstance, RoutePlanResultType>> sAfcPlanFail = new Dictionary<Sim, KeyValuePair<InteractionInstance, RoutePlanResultType>>();
        // Sim object id -> the autonomous interaction whose success pushed a continuation (its group, definition type, target)
        static readonly Dictionary<ulong, AfcPusher> sAfcPusher = new Dictionary<ulong, AfcPusher>();
        sealed class AfcPusher { public ulong Group; public Type Def; public IGameObject Target; public string Name; }
        static long sAfcUntilMax, sAfcWin, sAfcLen;
        static int sAfcLogged;

        static void ApplyAutonomyFailCooldown()
        {
            Patch(typeof(InteractionObjectPair), "TestAndCalculateScore", new Type[] { typeof(AutonomyT), typeof(AutonomySearchType), typeof(InteractionFlags) },
                typeof(AutonomyFailCooldownHook), "ScoreHook", "ScoreOrig");
            // the failure side rides AibHook (AutonomyInUseBlock.cs, InteractionQueue.DeQueue) and RflPlan (RouteFailLog.cs)
        }

        public class AutonomyFailCooldownHook
        {
            public float ScoreHook(AutonomyT autonomy, AutonomySearchType autonomySearchType, InteractionFlags flags)
            {
                if (sAfcUntilMax != 0 && AfcRefuse((InteractionObjectPair)(object)this, autonomy, flags)) return 0f;
                return ScoreOrig(autonomy, autonomySearchType, flags);
            }
            public float ScoreOrig(AutonomyT autonomy, AutonomySearchType autonomySearchType, InteractionFlags flags) { return 0f; } // InteractionObjectPair.TestAndCalculateScore
        }

        // Sim object id -> sim tick of its last FailedBecauseDestinationPortalLocked plan (privacy-locked bathroom door)
        static readonly Dictionary<ulong, long> sAfcLock = new Dictionary<ulong, long>();
        const float kAfcLockMin = 10f;

        static void AfcClear() { sAfcRows.Clear(); sAfcDefRows.Clear(); sAfcPlanFail.Clear(); sAfcPusher.Clear(); sAfcLock.Clear(); sAfcUntilMax = 0; }

        static AfcDefRow AfcDefFind(List<AfcDefRow> rows, object def)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i].Def == def) return rows[i];
            return null;
        }

        static long AfcWin() { if (sAfcWin <= 0) sAfcWin = SimClock.ConvertToTicks(kAfcWinMin, TimeUnit.Minutes); return sAfcWin; }
        static long AfcLen() { if (sAfcLen <= 0) sAfcLen = SimClock.ConvertToTicks(kAfcLenMin, TimeUnit.Minutes); return sAfcLen; }

        static AfcRow AfcFind(List<AfcRow> rows, object def, ulong target)
        {
            for (int i = 0; i < rows.Count; i++) if (rows[i].Target == target && rows[i].Def == def) return rows[i];
            return null;
        }

        // scoring side: true = answer 0 (refused)
        static bool AfcRefuse(InteractionObjectPair p, AutonomyT a, InteractionFlags flags)
        {
            try
            {
                long now = SimClock.CurrentTicks;
                if (now >= sAfcUntilMax) return false;
                if (a.OverrideInteractionParameters.HasValue) flags |= a.OverrideInteractionParameters.Value.Flags;  // as EA's body does
                if (!AfcRow.AutonomousOffer((flags & InteractionFlags.Autonomous) != 0, (flags & InteractionFlags.UserDirected) != 0)) return false;
                Sim s = a.Actor;
                List<AfcRow> rows = null;
                List<AfcDefRow> drows = null;
                if (s == null) return false;
                bool have = sAfcRows.TryGetValue(s.ObjectId.Value, out rows);
                if (sAfcDefRows.Count > 0 && sAfcDefRows.TryGetValue(s.ObjectId.Value, out drows)) have = true;
                if (!have || IsPlayerSim(s)) return false;  // a Sim that joined the household after its rows were made
                IGameObject t = p.Target;
                InteractionDefinition d = p.InteractionDefinition;
                if (t == null || d == null) return false;
                AfcRow r = rows != null ? AfcFind(rows, d.GetType(), t.ObjectId.Value) : null;
                bool cool = r != null && r.Cooling(now, AfcLen());
                if (!cool && drows != null)
                {
                    AfcDefRow dr = AfcDefFind(drows, d.GetType());
                    cool = dr != null && dr.Cooling(now, AfcLen());  // the definition on every target (3 distinct targets failed)
                }
                if (!cool) return false;
                if (!ShortcutOn("autonomyfailcooldown")) { PatchCounters()[4008]++; return false; }
                PatchCounters()[4007]++;
                return true;
            }
            catch (Exception) { PatchCounters()[4012]++; return false; }
        }

        // RflPlan (RouteFailLog.cs): after every plan of a Sim
        static void AfcPlan(Sim sim, bool ok, RoutePlanResultType t)
        {
            if (ok) { if (sAfcPlanFail.Count > 0) sAfcPlanFail.Remove(sim); return; }
            if (t == RoutePlanResultType.FailedBecauseDestinationPortalLocked)
            {
                if (sAfcLock.Count >= kAfcSims) sAfcLock.Clear();
                sAfcLock[sim.ObjectId.Value] = SimClock.CurrentTicks;  // kept past later plans: EA's fallback plans after a locked door fail other ways
            }
            InteractionQueue q = sim.InteractionQueue;
            InteractionInstance h = q != null ? q.GetHeadInteraction() : null;
            if (h == null || !h.Autonomous && !h.PushedAsContinuation) { if (sAfcPlanFail.Count > 0) sAfcPlanFail.Remove(sim); return; }  // a continuation: AfcDeQueue checks its pusher
            if (sAfcPlanFail.Count >= 64) sAfcPlanFail.Clear();  // a Sim reset mid-run never reaches DeQueue
            sAfcPlanFail[sim] = new KeyValuePair<InteractionInstance, RoutePlanResultType>(h, t);
        }

        // AibHook.DeQueueHook (AutonomyInUseBlock.cs): before EA's DeQueue(ok), the head still queued
        static void AfcDeQueue(InteractionQueue q, bool ok)
        {
            try
            {
                InteractionInstance i = q.GetHeadInteraction();
                Sim a = i != null ? i.InstanceActor : null;
                if (a == null) return;
                KeyValuePair<InteractionInstance, RoutePlanResultType> pf = new KeyValuePair<InteractionInstance, RoutePlanResultType>(null, RoutePlanResultType.Unknown);
                bool planFail = sAfcPlanFail.Count > 0 && sAfcPlanFail.TryGetValue(a, out pf) && pf.Key == i;
                if (planFail) sAfcPlanFail.Remove(a);
                ulong sid = a.ObjectId.Value;
                AfcPusher pu = null;
                bool ofPusher = i.PushedAsContinuation && sAfcPusher.Count > 0 && sAfcPusher.TryGetValue(sid, out pu) && pu != null && pu.Group == i.GroupId;
                bool own = AfcRow.OwnRow(i.Autonomous, i.Hidden, ok);
                if (!own && !ofPusher)
                {
                    if (i.Autonomous && !ok && (planFail || a.HasExitReason(ExitReason.RouteFailed))) PatchCounters()[4016]++;  // a Hidden script push: no offer to refuse
                    return;
                }
                IGameObject t = i.Target;
                InteractionDefinition d = i.InteractionDefinition;
                InteractionInstance nx = ok ? q.GetNextInteraction() : null;  // EA's AddNext puts a continuation right behind the locked head
                bool contQueued = nx != null && nx.PushedAsContinuation && nx.GroupId == i.GroupId;
                int row = AfcRow.Row(ok, own, ofPusher, contQueued, t is Terrain);
                if (row == AfcRow.kWait)
                {
                    if (!ofPusher && i.Autonomous && !i.Hidden && t != null && d != null && !(t is Terrain))
                    {
                        if (sAfcPusher.Count >= kAfcSims) sAfcPusher.Clear();
                        AfcPusher np = new AfcPusher();
                        np.Group = i.GroupId; np.Def = d.GetType(); np.Target = t; np.Name = i.GetType().Name;
                        sAfcPusher[sid] = np;
                    }
                    return;
                }
                if (ofPusher) sAfcPusher.Remove(sid);  // the group's last continuation: its verdict is the pusher's
                if (row == AfcRow.kNone || sAfcRows.Count == 0 && sAfcDefRows.Count == 0 && ok) return;
                Type dt = null;
                string what = null;
                if (row == AfcRow.kPusher) { t = pu.Target; dt = pu.Def; what = pu.Name + " via " + i.GetType().Name; }
                else if (d != null) { dt = d.GetType(); what = i.GetType().Name; }
                if (t == null || dt == null) return;
                if (ok)
                {
                    List<AfcRow> have = null;
                    AfcRow done = sAfcRows.TryGetValue(sid, out have) ? AfcFind(have, dt, t.ObjectId.Value) : null;
                    if (done != null) done.Ok();
                    List<AfcDefRow> dhave = null;
                    AfcDefRow ddone = sAfcDefRows.Count > 0 && sAfcDefRows.TryGetValue(sid, out dhave) ? AfcDefFind(dhave, dt) : null;
                    if (ddone != null) ddone.Ok();
                    return;
                }
                bool routeFailed = a.HasExitReason(ExitReason.RouteFailed);
                long lockAt = 0;
                bool recentLock = sAfcLock.Count > 0 && sAfcLock.TryGetValue(sid, out lockAt) && SimClock.CurrentTicks - lockAt < SimClock.ConvertToTicks(kAfcLockMin, TimeUnit.Minutes);
                if (recentLock && (routeFailed || planFail)) PatchCounters()[4015]++;
                if (!AfcRow.CountsAsRouteFailure(true, i.Cancelled, a.HasExitReason(ExitReason.CancelExternal), routeFailed, planFail, recentLock)) return;
                if (IsPlayerSim(a)) { PatchCounters()[4011]++; return; }  // household rule (header): never recorded
                List<AfcRow> rows = null;
                if (!sAfcRows.TryGetValue(sid, out rows))
                {
                    // shortcut: more than 256 Sims with failures drops every row (in memory only anyway); grow if [4009] climbs while [4010] stalls
                    if (sAfcRows.Count >= kAfcSims) sAfcRows.Clear();
                    rows = new List<AfcRow>();
                    sAfcRows[sid] = rows;
                }
                AfcRow r = AfcFind(rows, dt, t.ObjectId.Value);
                if (r == null)
                {
                    if (rows.Count >= kAfcRowsPerSim) rows.RemoveAt(0);
                    r = new AfcRow();
                    r.Target = t.ObjectId.Value;
                    r.Def = dt;
                    rows.Add(r);
                }
                int[] c = PatchCounters();
                c[4009]++;
                if (row == AfcRow.kPusher) c[4013]++;
                long now = SimClock.CurrentTicks;
                bool one = r.Fail(now, AfcWin(), AfcLen());
                AfcDefRow dr = null;
                bool all = AfcDefFail(sid, dt, t.ObjectId.Value, now, out dr);
                if (!one && !all) return;
                if (one && r.Until > sAfcUntilMax) sAfcUntilMax = r.Until;
                if (all && dr.Until > sAfcUntilMax) sAfcUntilMax = dr.Until;
                if (one) c[4010]++;
                if (all) c[4014]++;
                string who = a.FullName + ", " + what + " on " + t.GetType().Name + " 0x" + t.ObjectId.Value.ToString("X");
                string last = " (last " + (planFail ? pf.Value.ToString() : "RouteFailed") + "), autonomous offers " + (ShortcutOn("autonomyfailcooldown") ? "refused" : "would be refused (switch off)");
                // "routefail" prefix: ts3native's own line cap (header); no ", route " in the line, so no job fields are appended
                if (one && sAfcLogged < kAfcLines)
                {
                    sAfcLogged++;
                    DiagLine("routefail autonomyfailcooldown: " + who + ": 2 failed routes within " + kAfcWinMin + " sim-minutes" + last + " for " + kAfcLenMin + " sim-minutes");
                }
                if (all && sAfcLogged < kAfcLines)
                {
                    sAfcLogged++;
                    DiagLine("routefail autonomyfailcooldown: " + who + ": failed routes on " + AfcDefRow.kDistinct + " different targets within " + kAfcWinMin + " sim-minutes" + last + " on all targets for " + kAfcLenMin + " sim-minutes");
                }
            }
            catch (Exception) { PatchCounters()[4012]++; }
        }

        // the definition's distinct failed targets (AfcDefRow); true = an all-targets cooldown started
        static bool AfcDefFail(ulong sid, Type dt, ulong target, long now, out AfcDefRow dr)
        {
            List<AfcDefRow> rows = null;
            if (!sAfcDefRows.TryGetValue(sid, out rows))
            {
                if (sAfcDefRows.Count >= kAfcSims) sAfcDefRows.Clear();  // shortcut as sAfcRows: more than 256 Sims drops every row
                rows = new List<AfcDefRow>();
                sAfcDefRows[sid] = rows;
            }
            dr = AfcDefFind(rows, dt);
            if (dr == null)
            {
                if (rows.Count >= kAfcRowsPerSim) rows.RemoveAt(0);
                dr = new AfcDefRow();
                dr.Def = dt;
                rows.Add(dr);
            }
            return dr.Fail(target, now, AfcWin(), AfcLen());
        }
    }
}
