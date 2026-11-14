using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Entertainment;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.UI.Hud;

namespace Ts3Fixes
{
    // Gameplay logic flaws found by the rule scan of docs/559 (tools/static/gameplay-scan.sh), each off by default.
    //
    // bowlinglane (tuning kBowlingLane; docs/559 §3). BowlingLane+BowlInBowlingLane [verified, decompiled]:
    //  (a) Run's start wait `while (!Target.ReadyToStart()) Simulator.Sleep(5)` checks no exit reason, so the first bowler
    //      cannot be cancelled while it waits for the others; a listed bowler that never reaches the lane (AskToJoin's
    //      JoinGame adds the id before PushAsContinuation and never removes it when the push fails; a Sim gone from the
    //      world) keeps it waiting for good. Fix (ReadyToStart hook): a waiting bowler with a cancel exit reason is let
    //      through (EA's own DoLoop then ends the game at once, StopBowling as in any cancel), and listed bowlers that
    //      have no Sim in the world or nothing for this lane in their queue are dropped from the list.
    //  (b) BowlingLoop returns without ending the turn when RouteToBowl fails; the turn stays with that Sim, so it retries
    //      the same failed route every loop and the other bowler waits until someone cancels. Fix (RouteToBowl hook):
    //      after kBlRouteFails failures in a row inside the game loop the Sim gets ExitReason.RouteFailed (EA's exit).
    //  (c) EndBowlerTurn restarts the game after a join by testing `sim` (the bowler ending the turn) instead of `sim2`
    //      (each listed bowler), so only that bowler's frames restart; with more than two bowlers (kMaxBowlers tuned up)
    //      the others keep their old frame count and throw IndexOutOfRange past their last frame. Fix: the restart is
    //      applied to every listed bowler whose game is running (EA's own RestartGame). Latent at EA's kMaxBowlers 2.
    //  (d) EveryoneIsDone and the finish loop dereference SimDescription.Find(id).CreatedSim without a null test. Fix:
    //      ids with no Sim in the world are dropped before EveryoneIsDone runs.
    //  Counters [2448] cancelled waits released, [2449] ghost bowlers dropped, [2450] route-fail exits, [2451] restarts
    //  applied to other bowlers, [2452] errors.
    //
    // geniebanish (tuning kGenieBanish; docs/559 §4). OccultGenie+RemoveSim.Run picks the destination lot with
    // allCommunityLots[RandomUtil.GetInt(allCommunityLots.Count)]; RandomUtil.GetInt(max) is inclusive (min +
    // (max-min+1)*NextDouble) [verified], so 1 cast in Count+1 throws ArgumentOutOfRange after the liking hit and after
    // the target's queue was cancelled: no banish, no magic points used, the genie is reset. A world with no community
    // lot throws every time. Fix: the same Run with GetInt(Count - 1) and EA's own failure exit for an empty list.
    // Counters [2453] banishes run through the fixed copy, [2454] errors.
    //
    // spacesimpartner (tuning kSpaceSimPartner; docs/559 §5). SpaceSimulator+RideWith.Run pushes DoRideB on the partner,
    // then may return before the ride starts (rider cancelled while waiting, partner failed route) [verified]. DoRideB
    // waits `while (!SimAInRide)` and then `while (!RideOver)`, leaving only when the rider still has a cancel exit reason;
    // once the rider's interaction is over that reason is gone, so the partner stands in the simulator waiting for good
    // (NPCs forever; a household Sim until the player cancels). Fix (RideWith.Run hook, after EA's Run): when the ride
    // never started (RideOver false) and the rider did not already signal a failed route, the partner's DoRideB for this
    // simulator is cancelled (EA's CancelInteraction) and SimAFailedToRoute set. Counters [2455] partners released,
    // [2456] partners left waiting (counted with the switch off too), [2457] errors.
    // Same switch, second flaw (docs/559 §10): RideWith.Run sets RideOver = true right before DoTimedLoop (the ride), so
    // the partner's DoRideB leaves its `while (!RideOver)` wait, plays its end reaction and finishes at the START of the
    // ride while the rider rides on [verified order; the effect on the partner's animation is inferred: see the in-game
    // counters]. Fix: the RideOver getter reads false while the rider's RideWith.Run is still running, so the partner's
    // wait ends with the ride. Counters (always on, they measure the flaw in game): [2467] rides whose partner finished
    // before the rider's ride ended, [2468] rides whose partner was still riding at the end.
    public static partial class Fixes
    {
        // ---------- shared reflection ----------
        static Type sBlLane, sBlBowl;
        static FieldInfo sBlIds, sBlCur, sBlRestart, sBlWatchers;
        static MethodInfo sBlRestartGame, sBlStop;
        static FieldInfo sGbCost, sGbLtr;
        const int kBlRouteFails = 3;
        static readonly Dictionary<ulong, int> sBlFails = new Dictionary<ulong, int>();

        static void ApplyGameplayLogic559()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // bowlinglane: BowlingLane is internal to Sims3GameplayObjects
            sBlLane = typeof(SpaceSimulator).Assembly.GetType("Sims3.Gameplay.Objects.HobbiesSkills.BowlingLane");
            sBlBowl = sBlLane == null ? null : sBlLane.GetNestedType("BowlInBowlingLane", BindingFlags.NonPublic | BindingFlags.Public);
            if (sBlLane != null)
            {
                sBlIds = sBlLane.GetField("mBowlingSimIDs", inst);
                sBlCur = sBlLane.GetField("mCurrentSimID", inst);
                sBlRestart = sBlLane.GetField("mRestartGame", inst);
                sBlWatchers = sBlLane.GetField("mWatchers", inst);
                sBlStop = sBlLane.GetMethod("StopBowling", inst);
            }
            if (sBlBowl != null) sBlRestartGame = sBlBowl.GetMethod("RestartGame", inst);
            if (sBlIds == null || sBlCur == null || sBlRestart == null || sBlStop == null || sBlRestartGame == null)
                AppliedLog().Add("bowlinglane: BowlingLane members not found, not patched");
            else
            {
                Patch(sBlLane, "ReadyToStart", typeof(BlLaneHook), "ReadyHook", "ReadyOrig");
                Patch(sBlLane, "RouteToBowl", typeof(BlLaneHook), "RouteHook", "RouteOrig");
                Patch(sBlLane, "EndBowlerTurn", typeof(BlLaneHook), "EndTurnHook", "EndTurnOrig");
                Patch(sBlBowl, "EveryoneIsDone", typeof(BlBowlHook), "DoneHook", "DoneOrig");
            }
            // geniebanish
            sGbCost = typeof(OccultGenie.RemoveSim).GetField("kMagicPointCost", stat);
            sGbLtr = typeof(OccultGenie.RemoveSim).GetField("kLTRHitFromRemoving", stat);
            if (sGbCost == null || sGbLtr == null) AppliedLog().Add("geniebanish: RemoveSim tunables not found, not patched");
            else Patch(typeof(OccultGenie.RemoveSim), "Run", typeof(GbHook), "RunHook", "RunOrig");
            // spacesimpartner
            Patch(typeof(SpaceSimulator.RideWith), "Run", typeof(SsHook), "RunHook", "RunOrig");
            Patch(typeof(SpaceSimulator), "get_RideOver", typeof(SsSimHook), "RideOverHook", "RideOverOrig");
        }

        // ---------- bowlinglane ----------
        public class BlLaneHook
        {
            public bool ReadyHook()
            {
                GameObject lane = (object)this as GameObject;
                bool release = false;
                try { release = BlBeforeReady(lane); } catch (Exception) { PatchCounters()[2452]++; }
                return ReadyOrig() || release;
            }
            public bool ReadyOrig() { return false; } // receives BowlingLane.ReadyToStart's original body

            public bool RouteHook(Sim sim, bool checkInUse)
            {
                bool ok = RouteOrig(sim, checkInUse);
                try { BlAfterRoute(sim, checkInUse, ok); } catch (Exception) { PatchCounters()[2452]++; }
                return ok;
            }
            public bool RouteOrig(Sim sim, bool checkInUse) { return false; } // receives BowlingLane.RouteToBowl's original body

            public void EndTurnHook(Sim sim)
            {
                try { BlRestartOthers((object)this as GameObject, sim); } catch (Exception) { PatchCounters()[2452]++; }
                EndTurnOrig(sim);
            }
            public void EndTurnOrig(Sim sim) { } // receives BowlingLane.EndBowlerTurn's original body
        }

        // bowlinglane: EveryoneIsDone drops bowlers with no Sim in the world first (switch on)
        public class BlBowlHook
        {
            public bool DoneHook()
            {
                try
                {
                    InteractionInstance ia = (object)this as InteractionInstance;
                    if (ia != null && ShortcutOn("bowlinglane")) BlDropGhosts(ia.Target as GameObject, false);
                }
                catch (Exception) { PatchCounters()[2452]++; }
                return DoneOrig();
            }
            public bool DoneOrig() { return false; } // receives BowlInBowlingLane.EveryoneIsDone's original body
        }

        static Sim BlSim(ulong id)
        {
            SimDescription d = SimDescription.Find(id);
            return d == null ? null : d.CreatedSim;
        }

        // Switch on: drop listed ids that cannot bowl (no Sim in the world; with queueCheck also a Sim whose queue has
        // nothing targeting this lane). The bowler whose turn it is (the waiter) is never dropped here.
        static int BlDropGhosts(GameObject lane, bool queueCheck)
        {
            if (lane == null) return 0;
            List<ulong> ids = sBlIds.GetValue(lane) as List<ulong>;
            if (ids == null || ids.Count == 0) return 0;
            ulong cur = (ulong)sBlCur.GetValue(lane);
            int dropped = 0;
            foreach (ulong id in new List<ulong>(ids))
            {
                if (id == cur) continue;
                Sim s = BlSim(id);
                bool ghost = s == null || s.HasBeenDestroyed;
                if (!ghost && queueCheck && s.InteractionQueue != null)
                {
                    ghost = true;
                    foreach (InteractionInstance i in s.InteractionQueue.InteractionList)
                        if (i != null && ReferenceEquals(i.Target, lane)) { ghost = false; break; }
                }
                if (!ghost) continue;
                if (s != null) sBlStop.Invoke(lane, new object[] { s }); else ids.Remove(id);
                dropped++;
                PatchCounters()[2449]++;
            }
            return dropped;
        }

        // Called on every poll of Run's start wait (the waiter is the bowler whose turn it is). True = let it through.
        static bool BlBeforeReady(GameObject lane)
        {
            if (lane == null || !ShortcutOn("bowlinglane")) return false;
            BlDropGhosts(lane, true);
            Sim waiter = BlSim((ulong)sBlCur.GetValue(lane));
            if (waiter != null && waiter.HasExitReason(ExitReason.Canceled))
            {
                PatchCounters()[2448]++;
                return true;
            }
            return false;
        }

        static void BlAfterRoute(Sim sim, bool checkInUse, bool ok)
        {
            if (sim == null || checkInUse || sim.SimDescription == null) return;
            InteractionInstance cur = sim.InteractionQueue == null ? null : sim.InteractionQueue.GetCurrentInteraction();
            // only the game loop's route (state machine entered); Run's first route already takes EA's exit
            bool inLoop = cur != null && sBlBowl.IsInstanceOfType(cur) && cur.mCurrentStateMachine != null;
            if (BlRouteFailStep(sim.SimDescription.SimDescriptionId, inLoop, ok, ShortcutOn("bowlinglane")))
            {
                sim.AddExitReason(ExitReason.RouteFailed);
                PatchCounters()[2450]++;
            }
        }

        // Counts failures in a row per Sim; true when the switch is on and the limit is reached (count reset).
        static bool BlRouteFailStep(ulong id, bool inLoop, bool ok, bool on)
        {
            if (!inLoop || ok) { sBlFails.Remove(id); return false; }
            int n;
            sBlFails.TryGetValue(id, out n);
            n++;
            if (n < kBlRouteFails || !on) { sBlFails[id] = n; return false; }
            sBlFails.Remove(id);
            return true;
        }

        static void BlRestartOthers(GameObject lane, Sim sim)
        {
            if (lane == null || !ShortcutOn("bowlinglane") || !(bool)sBlRestart.GetValue(lane)) return;
            List<ulong> ids = sBlIds.GetValue(lane) as List<ulong>;
            if (ids == null) return;
            foreach (ulong id in new List<ulong>(ids))
            {
                Sim s2 = BlSim(id);
                if (ReferenceEquals(s2, null) || ReferenceEquals(s2, sim) || s2.InteractionQueue == null) continue;
                InteractionInstance cur = s2.InteractionQueue.GetCurrentInteraction();
                if (cur == null || !sBlBowl.IsInstanceOfType(cur) || cur.mCurrentStateMachine == null) continue;
                sBlRestartGame.Invoke(cur, null);
                PatchCounters()[2451]++;
            }
        }

        // ---------- geniebanish ----------
        public class GbHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("geniebanish")) return RunOrig();
                PatchCounters()[2453]++;
                return GbRun((object)this as OccultGenie.RemoveSim);
            }
            public bool RunOrig() { return false; } // receives OccultGenie+RemoveSim.Run's original body
        }

        // The pick both ways for a given NextDouble() r (check only): EA's GetInt(Count) is inclusive, so Count itself comes
        // up; the fixed GetInt(Count - 1) stays in range (-1 when empty).
        static int GbPickIndex(int count, double r, bool fixedPick)
        {
            int max = fixedPick ? count - 1 : count;
            if (fixedPick && max < 0) return -1;
            return (int)((double)(max + 1) * r);  // RandomUtil.GetInt(0, max) with NextDouble() = r
        }

        // EA's Run (OccultGenie+RemoveSim, docs/559 §4) with the in-range lot pick; everything else unchanged.
        static bool GbRun(OccultGenie.RemoveSim ia)
        {
            Sim actor = ia.Actor, target = ia.Target;
            ia.StandardEntry();
            ia.BeginCommodityUpdates();
            if (actor.SimDescription.ChildOrBelow) actor.PlaySoloAnimation("c_genie_removeSim_x", true, ProductVersion.EP6);
            else actor.PlaySoloAnimation("a_genie_removeSim_x", true, ProductVersion.EP6);
            ia.EndCommodityUpdates(true);
            Relationship relationship = Relationship.Get(actor, target, true);
            relationship.LTR.UpdateLiking((float)sGbLtr.GetValue(null));
            target.InteractionQueue.CancelAllInteractions();
            List<Lot> lots = LotManager.GetAllCommunityLots();
            if (lots == null || lots.Count == 0) { ia.StandardExit(); return false; }  // no community lot: EA's give-up exit
            int num = 0;
            Lot lot;
            do
            {
                lot = lots[RandomUtil.GetInt(lots.Count - 1)];  // EA: GetInt(lots.Count), inclusive
                if (num++ > lots.Count) { ia.StandardExit(); return false; }
            }
            while (lot.CommercialLotSubType == CommercialLotSubType.kEP6_BigShow);
            Vector3 pos = lot.GetCenterPosition();
            Vector3 fwd = new Vector3(target.ForwardVector);
            World.FindGoodLocationParams fglParams = new World.FindGoodLocationParams(pos);
            fglParams.BooleanConstraints = FindGoodLocationBooleans.Routable;
            fglParams.RequiredRoomID = 0;
            if (GlobalFunctions.FindGoodLocation(target, fglParams, out pos, out fwd))
            {
                Terrain.TeleportMeHere.Definition definition = new Terrain.TeleportMeHere.Definition(true, "_Genie");
                InteractionObjectPair iop = new InteractionObjectPair(definition, Terrain.Singleton);
                InteractionInstanceParameters parameters = new InteractionInstanceParameters(iop, target, new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), false, false);
                Terrain.TeleportMeHere teleport = ((InteractionDefinition)definition).CreateInstanceFromParameters(ref parameters) as Terrain.TeleportMeHere;
                teleport.SetAndReserveDestination(pos);
                target.InteractionQueue.Add(teleport);
                EventTracker.SendEvent(EventTypeId.kRemoveSim, actor, target);
            }
            ia.StandardExit();
            OccultGenie genie = actor.OccultManager.GetOccultType(OccultTypes.Genie) as OccultGenie;
            if (genie != null) genie.MagicPoints.UsePoints((int)sGbCost.GetValue(null));
            return true;
        }

        // ---------- spacesimpartner ----------
        public class SsHook
        {
            public bool RunHook()
            {
                SpaceSimulator.RideWith rw = (object)this as SpaceSimulator.RideWith;
                SpaceSimulator ss = rw == null ? null : rw.Target;
                if (ss != null) SsRiding(ss, +1);
                try { return RunOrig(); }
                finally
                {
                    if (ss != null) SsRiding(ss, -1);
                    try { SsRideEnd(rw); SsAfterRun(rw); } catch (Exception) { PatchCounters()[2457]++; }
                }
            }
            public bool RunOrig() { return false; } // receives SpaceSimulator+RideWith.Run's original body
        }

        // spacesimpartner (§10): RideOver reads false while the rider still rides
        public class SsSimHook
        {
            public bool RideOverHook()
            {
                bool v = RideOverOrig();
                return v && !(ShortcutOn("spacesimpartner") && SsIsRiding((object)this as SpaceSimulator));
            }
            public bool RideOverOrig() { return false; } // receives SpaceSimulator.get_RideOver's original body
        }

        // Simulators whose RideWith.Run is running (a Dictionary: no HashSet in the game's mscorlib/System).
        static readonly Dictionary<SpaceSimulator, int> sSsRiding = new Dictionary<SpaceSimulator, int>();

        static void SsRiding(SpaceSimulator ss, int delta)
        {
            int n;
            sSsRiding.TryGetValue(ss, out n);
            n += delta;
            if (n <= 0) sSsRiding.Remove(ss); else sSsRiding[ss] = n;
        }

        static bool SsIsRiding(SpaceSimulator ss)
        {
            return ss != null && sSsRiding.ContainsKey(ss);
        }

        static DoRideBState SsPartnerRide(SpaceSimulator ss)
        {
            Sim b = ss.SimB;
            if (ReferenceEquals(b, null) || b.HasBeenDestroyed || b.InteractionQueue == null) return DoRideBState.Gone;
            foreach (InteractionInstance i in b.InteractionQueue.InteractionList)
            {
                SpaceSimulator.DoRideB dr = i as SpaceSimulator.DoRideB;
                if (dr != null && ReferenceEquals(dr.spaceSim, ss)) return DoRideBState.Riding;
            }
            return DoRideBState.Gone;
        }

        enum DoRideBState { Gone, Riding }

        // After a ride that ran (RideOver set by this rider, both Sims in): was the partner still in its DoRideB?
        static void SsRideEnd(SpaceSimulator.RideWith rw)
        {
            if (rw == null) return;
            SpaceSimulator ss = rw.Target;
            if (ss == null || !ReferenceEquals(ss.SimA, rw.Actor) || !ss.RideOver || !ss.SimAInRide || !ss.SimBInRide) return;
            if (SsPartnerRide(ss) == DoRideBState.Riding) PatchCounters()[2468]++; else PatchCounters()[2467]++;
        }

        // True when this rider's ride never started and its partner has not been told: DoRideB would wait for good.
        static bool SsPartnerStranded(SpaceSimulator ss, Sim rider)
        {
            return ss != null && rider != null && ReferenceEquals(ss.SimA, rider) && !ReferenceEquals(ss.SimB, null) && !ReferenceEquals(ss.SimB, rider) && !ss.RideOver && !ss.SimAFailedToRoute;
        }

        static void SsAfterRun(SpaceSimulator.RideWith rw)
        {
            if (rw == null) return;
            SpaceSimulator ss = rw.Target;
            if (!SsPartnerStranded(ss, rw.Actor)) return;
            Sim b = ss.SimB;
            if (b.HasBeenDestroyed || b.InteractionQueue == null) return;
            List<InteractionInstance> waiting = new List<InteractionInstance>();
            foreach (InteractionInstance i in b.InteractionQueue.InteractionList)
            {
                SpaceSimulator.DoRideB dr = i as SpaceSimulator.DoRideB;
                if (dr != null && ReferenceEquals(dr.spaceSim, ss)) waiting.Add(dr);
            }
            if (waiting.Count == 0) return;
            PatchCounters()[2456]++;
            if (!ShortcutOn("spacesimpartner")) return;
            ss.SimAFailedToRoute = true;  // DoRideB's first wait leaves on this flag
            foreach (InteractionInstance i in waiting) b.InteractionQueue.CancelInteraction(i, false);
            PatchCounters()[2455]++;
        }

        // ---------- harness checks (throwaway objects, never added to the world; nothing in the world changes) ----------
        // BowlingLaneCheck
        //   run     (a) a throwaway lane (uninitialized, never placed) whose list holds the waiting bowler and a ghost id
        //           (no Sim): EA's ReadyToStart through the hook. off BROKEN = false (the waiter would wait for good);
        //           on FIXED = true, ghost dropped. (b) the route-fail counter fed 3 failed in-loop routes:
        //           off BROKEN = no exit; on FIXED = exit on the 3rd.
        //   result  counters 2448-2452
        public static object BowlingLaneCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bowlinglane: released 2448=" + c[2448] + ", ghosts 2449=" + c[2449] + ", route exits 2450=" + c[2450] + ", restarts 2451=" + c[2451] + ", errors 2452=" + c[2452];
            if (what == "result") return counters;
            if (what != "run") return "bowlinglane <run|result>";
            if (sBlIds == null || sBlWatchers == null) return "bowlinglane check: not patched (N/A)";
            try { return BlCheckRun(counters); }
            catch (Exception e)
            {
                Exception x = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                return "bowlinglane check: exception " + x.GetType().Name + ": " + x.Message + " at " + (x.StackTrace ?? "").Replace("\n", " | ") + "; " + counters;
            }
        }

        // (a) runs the patched decision directly (the hook's ghost drop, then EA's ReadyToStart rule: every listed bowler
        // but the current one has a registered watcher; the throwaway lane has none), so it does not depend on calling
        // the swapped method on an object that was never placed (the 1114 in-game run returned nothing there).
        static string BlCheckRun(string counters)
        {
            bool on = ShortcutOn("bowlinglane");
            object lane = FormatterServices.GetUninitializedObject(sBlLane);
            List<ulong> ids = new List<ulong>();
            const ulong waiter = 0x7E57B0A1E0000001UL, ghost = 0x7E57B0A1E0000002UL;  // ids no SimDescription has
            ids.Add(waiter);
            ids.Add(ghost);
            sBlIds.SetValue(lane, ids);
            sBlCur.SetValue(lane, waiter);
            sBlWatchers.SetValue(lane, Activator.CreateInstance(sBlWatchers.FieldType));
            if (on) BlDropGhosts(lane as GameObject, false);
            bool ready = true;
            foreach (ulong id in ids) if (id != waiter) { ready = false; break; }  // no watchers registered
            string a = ready && !ids.Contains(ghost) ? "FIXED" : !ready && ids.Contains(ghost) ? "BROKEN" : "UNEXPECTED";
            const ulong probe = 0x7E57B0A1E0000003UL;
            bool exit = false;
            for (int i = 0; i < kBlRouteFails; i++) exit = BlRouteFailStep(probe, true, false, on);
            sBlFails.Remove(probe);
            string b = exit ? "FIXED" : "BROKEN";
            string verdict = a == b ? a : "UNEXPECTED";
            return "bowlinglane check (switch " + (on ? "on" : "off") + "): start wait with a ghost bowler " + a + " (ready=" + ready + "), route-fail loop " + b + "; verdict: " + verdict + "; " + counters;
        }

        // GenieBanishCheck run: the lot index for NextDouble() = 0.9999 over the world's community lots (read only).
        //   off BROKEN = EA's pick lands on Count (out of range); on FIXED = the fixed pick stays below Count. Nothing is cast.
        public static object GenieBanishCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "geniebanish: fixed runs 2453=" + c[2453] + ", errors 2454=" + c[2454];
            if (what == "result") return counters;
            if (what != "run") return "geniebanish <run|result>";
            if (sGbCost == null) return "geniebanish check: not patched (N/A)";
            bool on = ShortcutOn("geniebanish");
            List<Lot> lots = LotManager.GetAllCommunityLots();
            int n = lots == null ? 0 : lots.Count;
            if (n == 0) return "geniebanish check: no community lots in this world (N/A)";
            int idx = GbPickIndex(n, 0.9999, on);
            string verdict = idx >= n ? "BROKEN" : idx >= 0 ? "FIXED" : "UNEXPECTED";
            return "geniebanish check (switch " + (on ? "on" : "off") + "): " + n + " community lots, pick at r=0.9999 -> index " + idx + "; verdict: " + verdict + "; " + counters;
        }

        // SpaceSimPartnerCheck ride: IN GAME, shows the RideOver timing (§10). The selected Sim rides a Space Simulator on the
        //   active lot with another instantiated Sim on that lot as partner (EA's RideWith, Medium, pushed user-directed;
        //   costs EA's ride fee off the owner's lot). Wait for the ride to end (~kTimeRideLasts sim-minutes), then 'result':
        //   off BROKEN = 2467 +1 (partner finished at the ride's start); on FIXED = 2468 +1 (partner rode to the end).
        // SpaceSimPartnerCheck run: a throwaway simulator (uninitialized, never placed) in the state RideWith leaves after
        //   a cancelled wait (rider in, ride not started), rider = partner placeholder objects (no queue touched).
        //   off BROKEN = the release step is skipped (partner waits); on FIXED = it runs (decision only; nothing cancelled).
        public static object SpaceSimPartnerCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "spacesimpartner: released 2455=" + c[2455] + ", stranded seen 2456=" + c[2456] + ", errors 2457=" + c[2457];
            counters += ", ride ends: partner already gone 2467=" + c[2467] + ", partner still riding 2468=" + c[2468];
            if (what == "result") return counters;
            if (what == "ride") return SsPushRide();
            if (what != "run") return "spacesimpartner <run|ride|result>";
            bool on = ShortcutOn("spacesimpartner");
            Sim rider = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Sim partner = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            SpaceSimulator ss = (SpaceSimulator)FormatterServices.GetUninitializedObject(typeof(SpaceSimulator));
            ss.SimA = rider;
            ss.SimB = partner;
            ss.SimAInRide = true;
            ss.RideOver = false;
            ss.SimAFailedToRoute = false;
            bool stranded = SsPartnerStranded(ss, rider);
            ss.RideOver = true;
            bool afterRide = SsPartnerStranded(ss, rider);  // a ride that ran must never release
            string verdict = !stranded || afterRide ? "UNEXPECTED" : on ? "FIXED" : "BROKEN";
            return "spacesimpartner check (switch " + (on ? "on" : "off") + "): stranded partner detected=" + stranded + ", after a ride=" + afterRide + ", release " + (on ? "runs" : "skipped") + "; verdict: " + verdict + "; " + counters;
        }

        static string SsPushRide()
        {
            Sim a = Sims3.Gameplay.Actors.Sim.ActiveActor;
            if (a == null || a.LotCurrent == null) return "spacesimpartner ride: no selected Sim on a lot (N/A)";
            SpaceSimulator ss = null;
            foreach (SpaceSimulator x in a.LotCurrent.GetObjects<SpaceSimulator>()) { if (x != null && !x.InUse) { ss = x; break; } }
            if (ss == null) return "spacesimpartner ride: no free Space Simulator on the selected Sim's lot (N/A)";
            Sim b = null;
            foreach (Sim s in a.LotCurrent.GetAllActors()) { if (s != null && !ReferenceEquals(s, a) && s.SimDescription != null && s.SimDescription.ChildOrAbove && !s.IsPet) { b = s; break; } }
            if (b == null) return "spacesimpartner ride: no second Sim on the lot (N/A)";
            Type defType = typeof(SpaceSimulator.RideWith).GetNestedType("RideWithDefinition", BindingFlags.NonPublic | BindingFlags.Public);
            if (defType == null) return "spacesimpartner ride: RideWithDefinition not found";
            SpaceSimulator.RideItems item = new SpaceSimulator.RideItems(SpaceSimulator.RideType.Medium, 10, "Medium");
            InteractionDefinition def = (InteractionDefinition)Activator.CreateInstance(defType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { item }, null);
            InteractionObjectPair iop = new InteractionObjectPair(def, ss);
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, a, new InteractionPriority(InteractionPriorityLevel.UserDirected), false, true);
            InteractionInstance inst = def.CreateInstanceFromParameters(ref p);
            List<object> sel = new List<object>();
            sel.Add(b);
            inst.SelectedObjects = sel;
            bool added = a.InteractionQueue.Add(inst);
            return "spacesimpartner ride: " + a.FullName + " rides with " + b.FullName + " (queued=" + added + ", switch " + (ShortcutOn("spacesimpartner") ? "on" : "off") + "); read 'result' after the ride";
        }
    }
}
