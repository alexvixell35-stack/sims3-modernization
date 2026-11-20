using System;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Fewer route plans on the sim thread (docs/04a §7, ranking from the managed profiles and /taskstall). Both default off
    // until measured: `node tools/harness/profile-world.ts --tag bp-fewer --ab routablecache,stuckcheckbusy`.
    public static partial class Fixes
    {
        static void ApplyFewerPlans()
        {
            Patch(typeof(Sim), "IsPointInLotSafelyRoutable", typeof(RoutableCacheHook), "RoutableHook", "RoutableOrig");
            SemHookWorld();  // docs/636 spotedgememo: drop the per-lot memo on world load / quit and lot add / remove
            Type helper = typeof(LotManager).GetNestedType("RunStuckCheckHelper", BindingFlags.NonPublic);
            if (helper == null) { AppliedLog().Add("LotManager.RunStuckCheckHelper: not found, skipped"); return; }
            Patch(helper, "RunStuckCheck", typeof(StuckCheckBusyHook), "RunHook", "RunOrig");
        }

        // Sim.IsPointInLotSafelyRoutable is a full route plan (spot to the lot's 4 corners and 4 edge midpoints) that the
        // game asks in loops: FindRoutablePointInsideNearFrontDoor tests candidate points near the door one plan each (every
        // reset with send-home, NRaas Instantiation), AttemptToFindSafeLocation tries the current spot then two nearby ones,
        // CreateTeleportInstanceToPosition one more. Profiles: ~60 of 15.4k managed samples outside our own checks, plus 345
        // and 456 ms stalls (taskstall 2026-10-13 01:49). The hook answers from the verdict cache SafeSpot and the watchdog
        // already use (0.5 m cell + exact route profile, one sim-day per lot, dropped on a household change or a lot move;
        // see SpotVerdicts), so the game's callers and ours share verdicts. Uncached: the world lot (no plan there) and a
        // Sim with a parent (profile not exact). Our own checks (SpotVerdict, sInOwnCheck) pass straight through.
        // Sim has no use for a subclass here, so the hook is a plain class (as BroadcasterPulseHook).
        // Switch "routablecache" (default off). Counters: [65] cache hits, [66] plans run and recorded.
        // Every EA call below goes through SemRoutable (docs/636 spotedgememo, SpotEdgeMemo.cs; EA's own call when off).
        public class RoutableCacheHook
        {
            public bool RoutableHook(Lot lot, Vector3 pos)
            {
                bool was = sInRoutableCheck;  // docs/202: unseenfbplan never marks these plans (a cut "false" sends the game to its safe-location logic)
                sInRoutableCheck = true;
                Vector3 rfpPrev;
                bool rfpHad = RfpEnter(pos, out rfpPrev);  // routefailprobe (docs/627 §2): the spot this check plans from
                try
                {
                    Sim sim = (Sim)(object)this;
                    if (RsOwnPlan(sim)) return RsAdjust(sim, RoutableOrig(lot, pos), lot, pos);  // residentspawnslot (docs/582 B5 §8)
                    return RsAdjust(sim, RoutableCheck(lot, pos), lot, pos);  // residentspawnwalk too (docs/582 B8 §8)
                }
                finally { sInRoutableCheck = was; RfpLeave(rfpHad, rfpPrev); }
            }
            bool RoutableCheck(Lot lot, Vector3 pos)
            {
                Sim sim = (Sim)(object)this;
                if (sInOwnCheck || lot == null || lot.IsWorldLot || !ShortcutOn("routablecache")) return SemRoutable(this, lot, pos);
                RouteProfile p = ProfileOf(sim);
                if (!p.Ok) return SemRoutable(this, lot, pos);
                SpotVerdicts v = Verdicts(lot);
                VerdictKey key = KeyOf(p, pos);
                int[] c = PatchCounters();
                bool ok;
                if (v.Cells.TryGetValue(key, out ok)) { c[65]++; return ok; }
                ok = SemRoutable(this, lot, pos);
                v.Cells[key] = ok;
                c[66]++;
                return ok;
            }
            public bool RoutableOrig(Lot lot, Vector3 pos) { return false; } // receives Sim.IsPointInLotSafelyRoutable's original header
        }

        // LotManager.RunStuckCheckHelper (3 AM, every actor): a Sim that has not moved 20-60 sim-minutes later gets up to
        // Sim.kNumberOfPlanAttemptsForStuckSim (10) plans to random lot points, synchronously in one alarm tick; up to 3
        // rounds before a reset. At 3-4 AM nearly every townie is asleep, so nearly every check plans from a bed.
        // Profiles: 110 of 15.4k managed samples (0.7%, all at night) and one 645 ms stall (taskstall 2026-10-13 08:06).
        // A Sim held by an object (bed, chair, any slotted posture; not a vehicle) is busy, not stuck: the hook ends the
        // check as the game does after a successful plan (no re-arm). Standing, swimming and in-vehicle Sims run the game's
        // check unchanged. Frozen in an object stays covered by our watchdog (watchdogidle: a sim-day in one spot) and
        // Overwatch. Switch "stuckcheckbusy" (default off). Counter: [67] checks skipped.
        public class StuckCheckBusyHook
        {
            public void RunHook(Sim sim)
            {
                if (ShortcutOn("stuckcheckbusy") && sim != null && sim.Parent != null && !(sim.Posture is Sim.StandingPosture)
                    && !(sim.Parent is Sims3.Gameplay.Objects.Vehicles.Vehicle))
                {
                    PatchCounters()[67]++;
                    return;
                }
                // stuckhbskip (contributor 2026-11-13, ON since import 2026-11-20): on a houseboat the check's random lot points (any level 0-4, sea or
                // lot terrain) are no goals in boat lot space, so nearly every plan fails (11-13: 113-236 probes a game, 85-90%
                // NoDestination, 3-20 s of plans, all townies on the same boat), and 3 failed rounds reset a Sim that is not stuck. A
                // townie on a houseboat that is not the household's lot ends the check as after a success (no re-arm; EA re-arms it
                // each night). Household, selected and no-household Sims keep EA's check (YieldTownie, the shared household rule); the
                // watchdog and Overwatch still catch a Sim really stuck. Counter: [4055] checks skipped.
                if (ShortcutOn("stuckhbskip") && YieldTownie(sim))
                {
                    Lot on = sim.LotCurrent;
                    if (on != null && on.IsHouseboatLot() && on != Household.ActiveHouseholdLot)
                    {
                        PatchCounters()[4055]++;
                        return;
                    }
                }
                // docs/202 §5.6 (4): an unseen Sim's check only re-arms (phase B), so do it here, not through the async queue:
                // queued + drained, every re-arm counted as a queued check (4,343 vs 769 in v020-natural IP) and cost a drain tick
                if (ShortcutOn("unseenfbstuck") && UfStuckDefer(this, sim)) return;
                if (sim != null && ShortcutOn("stuckcheckasync") && GameStates.IsLiveState && !GameStates.IsTravelling && !GameStates.IsWorldShuttingDown)
                {
                    StuckCheckDefer(this, sim);
                    return;
                }
                StuckCheckRun(this, sim);
            }
            public void RunOrig(Sim sim) { } // receives RunStuckCheckHelper.RunStuckCheck's original header
        }

        // docs/163 §3: the stuck check on a lot draws up to 10 random lot points (terrain incl. sea) and plans to them in
        // draw order until one succeeds; a failed plan floods the Sim's whole reachable area first (docs/142 §2), so the
        // freezes are the failed plans before the first success. Switch "stuckchecknear" (default off): the same draws
        // (same Lot.GetRandomPosition calls, same arguments, all drawn first), planned nearest first, stop at the first
        // success, then the game's own ending (reset after attempt 3, else re-arm through its private CreateAlarm). The
        // verdict equals the game's: "not stuck" iff any of the drawn points routes, independent of the order; only the
        // number of RandomUtil draws differs (the game stops drawing at its first success). The world lot branch (4 plans
        // of 2 m) and any helper whose fields cannot be read run the game's body. Counters: [238] checks run here,
        // [239] plans, [240] checks where every point failed (re-arm or reset, as the game).
        // Both bodies run marked as a stuck check, so Overwatch's StuckCheck V2 does not count their probe plans (owprobeplans,
        // OverwatchProbe.cs).
        // docs/163 §10: with stuckcheckown each probe plan is an own-task job that holds the routing lock (+0x918) for the whole
        // search, and a failing probe floods the Sim's reachable area for seconds, so the sim thread's next steering query
        // (0x85FE10, every walking Sim every tick) waits that long. Switch "stuckcheckcap" (default off, needs stuckcheckown and
        // ts3native [RouteAsync] PlanDeadlineMs): in the rounds before the last (attempt < 3) each probe START first sends the
        // PROBE mark, and ts3native cuts that job at PlanDeadlineMs as it cuts a foreign sync plan (failed result). A cut probe
        // can only turn "not stuck" into a re-arm (one more round 20-60 sim-minutes later, ended by the game's own "moved"
        // test), never into a reset: the last round, the only one that resets, plans uncut, so a reset still needs every point
        // to fail a full search. Outcome counters per check (any switch): [298] no plan, [299] a probe succeeded, [300] all
        // failed before the last round, [301] all failed in the last round; [297] marks ts3native accepted.
        // shortcut: one check at a time (drain task, or the alarm when stuckcheckasync is off); a second check running beside
        // a sleeping drain check would mix the outcome counts (never seen: both need the alarm path and the drain at once)
        // §10.1: sStuckRound = mAttempt + 1 (0 = unknown), sent with every stuck-check START as the PROBE tag (RouteBackoff.cs
        // OwnerTaskStart) so ts3native's own long-job and own-job wait lines name the round
        static bool sStuckIn, sStuckCap, sStuckLast;
        static int sStuckPlans, sStuckOk, sStuckRound;
        static ObjectGuid sStuckRunTask = ObjectGuid.InvalidObjectGuid;

        static void StuckCheckRun(StuckCheckBusyHook helper, Sim sim)
        {
            if (ShortcutOn("unseenfbstuck") && UfStuckDefer(helper, sim)) return; // docs/202 §3.3 (UnseenFallback.cs)
            ulong id, task;
            OwProbeEnter(sim, out id, out task);
            bool known = StuckNearReflect(helper.GetType());
            uint attempt = known ? (uint)sStuckAttempt.GetValue(helper) : 0;
            bool last = !known || !NoStartCore.StuckMayCut(attempt);
            sStuckRound = known ? (int)attempt + 1 : 0;
            sStuckLast = known && last;
            sStuckPlans = sStuckOk = 0;
            sStuckRunTask = Simulator.CurrentTask;
            sStuckCap = !last && ShortcutOn("stuckcheckcap") && ShortcutOn("stuckcheckown");
            sStuckIn = true;
            try
            {
                if (ShortcutOn("stuckchecknear") && StuckCheckNear(helper, sim)) return;
                helper.RunOrig(sim);
            }
            finally
            {
                sStuckIn = sStuckCap = false;
                PatchCounters()[NoStartCore.StuckOutcome(sStuckPlans, sStuckOk, last)]++;
                OwProbeExit(id, task);
            }
        }

        // PlanHook: one plan's result while a check runs in this task
        static void StuckCheckPlanned(bool ok)
        {
            if (!sStuckIn || Simulator.CurrentTask != sStuckRunTask) return;
            sStuckPlans++;
            if (ok) sStuckOk++;
        }

        static bool StuckCheckNear(object helper, Sim sim)
        {
            if (sim == null || !StuckNearReflect(helper.GetType())) return false;
            Lot lot = sim.LotCurrent;
            if (lot == null || lot.IsWorldLot) return false;
            // the game's own early return, in its order (the active lot test comes first there too)
            if (lot == Household.ActiveHouseholdLot || sim.Position != (Vector3)sStuckLoc.GetValue(helper) || sim.SimInRabbitHolePosture) return true;
            int[] c = PatchCounters();
            c[238]++;
            int n = Sim.kNumberOfPlanAttemptsForStuckSim, k = 0;
            Vector3[] pts = new Vector3[Math.Max(n, 0)];
            float[] d2 = new float[pts.Length];
            Vector3 at = sim.Position;
            for (int j = 0; j < n; j++)
            {
                Vector3 p = lot.GetRandomPosition(false, true, TerrainType.WorldTerrain | TerrainType.WorldSea | TerrainType.LotTerrain);
                if (p == Vector3.Invalid) continue;
                pts[k] = p;
                d2[k] = (p - at).LengthSqr();
                k++;
            }
            foreach (int i in NoStartCore.NearestFirst(d2, k))
            {
                c[239]++;
                if (sim.CreateRoute().PlanToPoint(pts[i]).Succeeded()) return true;
            }
            c[240]++;
            uint attempt = (uint)sStuckAttempt.GetValue(helper);
            if (attempt >= 3) sim.SetObjectToReset();
            else sStuckAlarm.Invoke(null, new object[] { sim, attempt + 1 });
            return true;
        }

        static bool StuckNearReflect(Type t)
        {
            if (sStuckLoc == null)
            {
                const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
                sStuckLoc = t.GetField("mLocation", f);
                sStuckAttempt = t.GetField("mAttempt", f);
                sStuckAlarm = t.GetMethod("CreateAlarm", BindingFlags.Static | BindingFlags.NonPublic, null, new Type[] { typeof(Sim), typeof(uint) }, null);
            }
            return sStuckLoc != null && sStuckAttempt != null && sStuckAlarm != null;
        }
        static FieldInfo sStuckLoc, sStuckAttempt;
        static MethodInfo sStuckAlarm;

        // docs/134 §8: the stuck check's alarm runs inside the world lot's task (Lot.Simulate -> AlarmManager.Simulate, a plain
        // alarm, not YieldRequired). Its plans are foreign-task plans there, so with [RouteAsync] FallbackAsync=1 the world lot
        // task sleeps inside the alarm while the worker plans (up to 10 plans per Sim), holding back every later world alarm
        // and the world lot's own updates; with FallbackAsync=0 they all run in one tick. Switch "stuckcheckasync" (default ON):
        // the alarm only queues the check; our one task runs the game's own RunStuckCheck (RunOrig, same helper object, so the
        // same position test, attempt count, reset or re-arm) for one Sim per tick. Its plans are still foreign-task plans (our
        // task is not the Sim's), so FallbackAsync sends them to the worker and only our task sleeps. The check runs >= 1 tick
        // after the alarm instead of in it. Counters: [127] queued, [128] run, [129] dropped (Sim gone, world left, exception),
        // [130] most waiting at once, [131] longest wait in sim-seconds.
        sealed class StuckCheck { public StuckCheckBusyHook Helper; public Sim Sim; } // Helper is really a RunStuckCheckHelper (swapped body)
        static readonly SliceQueue<StuckCheck> sStuckQueue = new SliceQueue<StuckCheck>();
        static ObjectGuid sStuckTask = ObjectGuid.InvalidObjectGuid;

        static void StuckCheckDefer(StuckCheckBusyHook helper, Sim sim)
        {
            int[] c = PatchCounters();
            if (!(sStuckTask.IsValid && Simulator.GetTask(sStuckTask) != null))
            {
                c[129] += sStuckQueue.Clear(); // left over from a task the world unload killed
                sStuckTask = Simulator.AddObject(new LoopTask(StuckCheckDrain));
            }
            StuckCheck e = new StuckCheck();
            e.Helper = helper;
            e.Sim = sim;
            sStuckQueue.Add(e, Sims3.Gameplay.Utilities.SimClock.CurrentTicks);
            c[127]++;
            c[130] = sStuckQueue.MaxDepth;
        }

        static void StuckCheckDrain()
        {
            int[] c = PatchCounters();
            try
            {
                StuckCheck e;
                while (sStuckQueue.Next(Sims3.Gameplay.Utilities.SimClock.CurrentTicks, out e))
                {
                    c[131] = (int)(sStuckQueue.MaxWait * 60 / Math.Max(1L, Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Minutes)));
                    if (GameStates.IsWorldShuttingDown || GameStates.IsTravelling || e.Sim.HasBeenDestroyed) { c[129]++; continue; }
                    c[128]++;
                    try { StuckCheckRun(e.Helper, e.Sim); }
                    catch (Exception) { c[129]++; } // the alarm's own caller (AlarmManager.Simulate) swallows these too
                    if (sStuckQueue.Count > 0) Simulator.Sleep(0); // one Sim per tick
                }
            }
            finally { sStuckTask = ObjectGuid.InvalidObjectGuid; }
        }
    }
}
