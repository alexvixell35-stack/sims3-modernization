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
    // bowlinglane: bowling lane flaws found by the docs/559 rule scan (tools/static/gameplay-scan.sh). Off by default.
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
    // Harness check: BowlingLaneChecks.cs.
    public static partial class Fixes
    {
        static Type sBlLane, sBlBowl;
        static FieldInfo sBlIds, sBlCur, sBlRestart, sBlWatchers;
        static MethodInfo sBlRestartGame, sBlStop;
        const int kBlRouteFails = 3;
        static readonly Dictionary<ulong, int> sBlFails = new Dictionary<ulong, int>();

        static void ApplyBowlingLane()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
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
        }

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
    }
}
