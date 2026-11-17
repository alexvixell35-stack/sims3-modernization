using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using AutonomyT = Sims3.Gameplay.Autonomy.Autonomy;

namespace Ts3Fixes
{
    // Stuck NPC interactions from the wait census (docs/613): rabbit hole exit pacing and NPC Perform For Tips. Both
    // default off; EA bug fixes built from EA's own rule next to each bug. Household Sims keep EA's behaviour (IsPlayerSim:
    // these are skips/cuts, so the experimental HouseholdToo lift never applies).
    public static partial class Fixes
    {
        // ---- docs/613 §2 rhexitunseen -------------------------------------------------------------------------------------
        // [verified, ilspycmd RabbitHole::AnimateExitRabbitHole(Sim, Slot, Sim, SimRabbitHoleState, bool)] unless skipWait or
        // CanSimSkipExitWait(sim), a Sim leaving a rabbit hole sleeps in Sleep(30) steps until kNpcExitFreq (15 sim-min,
        // tuning comment "Exit frequency in minutes for NPC sims leaving this rabbit hole") has passed since the hole's
        // TimeLastSimLeft, then stamps it: one NPC out per 15 sim-min per rabbit hole, whoever wakes first. [verified,
        // RabbitHole::CanSimSkipExitWait] base answer is sim.IsSelectable; SchoolRabbitHole overrides it so pupils leaving
        // school skip it (EA's own fix for a crowd leaving at once); ResortTower has a SkipWait exit. Census (6 Bridgeport
        // runs): 5-10 NPCs at Steve's Business Complex in InRabbitHolePosture with Go Home / Eat Out / Visit as the current
        // interaction, 50-314 sim-min still (Go Home x8 lead), all low LOD (motives frozen). The pacing is a staged walk-out
        // for the camera [inferred from the tuning comment and the selectable/school skips]; for a Sim no camera can see it
        // only queues the town. Fix as EA did for school: CanSimSkipExitWait is also true for an NPC that is not high LOD
        // (EA's own Autonomy.IsSufficientlyOnScreenForHighLODSimulation: not on the active lot, not with the player, not on
        // screen). Exit slots stay reserved by EA's GetExitSlot, so two Sims never share a door slot.
        // Counters: [3453] exit waits skipped, [3454] unused since r72 (off arm runs EA only), [3455] household or
        // seen Sims kept on EA's wait (switch on), [3456] errors.
        static string sRhxMissing = "";

        static void ApplyRhExitUnseen()
        {
            sRhxMissing = typeof(RabbitHole).GetMethod("CanSimSkipExitWait", kInstAll, null, new Type[] { typeof(Sim) }, null) == null
                ? "RabbitHole.CanSimSkipExitWait" : "";
            if (sRhxMissing.Length > 0) { AppliedLog().Add("rhexitunseen: not found: " + sRhxMissing); return; }
            Patch(typeof(RabbitHole), "CanSimSkipExitWait", new Type[] { typeof(Sim) }, typeof(RhxSkipHook), "SkipHook", "SkipOrig");
        }

        // Pure: skip the NPC exit pacing wait for this Sim? eaSkip = EA's own answer (selectable, school pupil).
        internal static bool RhxSkip(bool on, bool eaSkip, bool unseen, bool player)
        {
            return eaSkip || (on && unseen && !player);
        }

        // EA's low-LOD test, as DoLoop asks it (sim thread). Unknown = seen.
        static bool RhxUnseen(Sim s)
        {
            try { return s != null && !s.HasBeenDestroyed && s.Autonomy != null && !s.Autonomy.IsSufficientlyOnScreenForHighLODSimulation; }
            catch (ResetException) { throw; }
            catch (Exception) { return false; }
        }

        delegate bool RhxSkipFn(Sim s);

        public class RhxSkipHook
        {
            public bool SkipHook(Sim sim)
            {
                bool ea = SkipOrig(sim);
                if (ea) return true;
                try
                {
                    // switch off: EA's answer only, no extra game calls (r72: the off arm runs nothing but EA's body)
                    if (sRhxMissing.Length > 0 || sim == null || !ShortcutOn("rhexitunseen")) return false;
                    bool player = IsPlayerSim(sim), unseen = !player && RhxUnseen(sim);
                    if (RhxSkip(true, false, unseen, player)) { PatchCounters()[3453]++; return true; }
                    PatchCounters()[3455]++;
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3456]++; }
                return false;
            }
            public bool SkipOrig(Sim sim) { return false; } // receives RabbitHole.CanSimSkipExitWait
        }

        // RhExitUnseenCheck run: pure decision with controls, then a live replay through the swapped CanSimSkipExitWait on a
        // world rabbit hole with a low-LOD NPC (off: EA's wait, false; on: skip, true) and the player's Sim (EA's answer both
        // ways). Prints the Sims inside rabbit holes now (NPC / unseen).
        public static object RhExitUnseenCheck(string what)
        {
            string counters = B582Counters("rhexitunseen", 3453, new[] { "exit waits skipped", "unseen NPC waits, switch off", "household/seen kept", "errors" }, sRhxMissing);
            if (what == "result") return counters;
            if (what != "run") return "rhexitunseen <run|result>";
            bool on = ShortcutOn("rhexitunseen");
            bool eaBug = !RhxSkip(false, false, true, false);
            bool fixedPure = on && RhxSkip(on, false, true, false);
            bool controls = eaBug && !RhxSkip(true, false, false, false) && !RhxSkip(true, false, true, true) && RhxSkip(false, true, false, true);
            string live = "no rabbit hole or no unseen NPC in the world (pure only)";
            bool liveOk = true;
            int inside = 0, insideNpcUnseen = 0;
            RabbitHole hole = null;
            Sim npc = null;
            try
            {
                foreach (RabbitHole rh in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
                {
                    if (rh == null || rh is Sims3.Gameplay.Objects.RabbitHoles.SchoolRabbitHole) continue;  // school skips anyway
                    hole = rh;
                    break;
                }
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null) continue;
                    bool npcUnseen = !IsPlayerSim(s) && RhxUnseen(s);
                    if (s.Posture is Sims3.Gameplay.ActorSystems.InRabbitHolePosture) { inside++; if (npcUnseen) insideNpcUnseen++; }
                    if (npc == null && npcUnseen) npc = s;
                }
            }
            catch (Exception e) { liveOk = false; live = "scan threw " + e.GetType().Name; }
            if (hole != null && npc != null && sRhxMissing.Length == 0)
            {
                int[] c = PatchCounters();
                int c0 = c[3453], c1 = c[3454], c2 = c[3455];
                try
                {
                    System.Reflection.MethodInfo m = typeof(RabbitHole).GetMethod("CanSimSkipExitWait", kInstAll, null, new Type[] { typeof(Sim) }, null);
                    // through a delegate: MethodInfo.Invoke with a target throws on a swapped method (swapinvoke selftest)
                    RhxSkipFn fn = (RhxSkipFn)Delegate.CreateDelegate(typeof(RhxSkipFn), hole, m);
                    bool got = fn(npc);
                    bool ea = npc.IsSelectable || hole is Sims3.Gameplay.Objects.RabbitHoles.SchoolRabbitHole;
                    Sim player = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
                    bool playerOk = player == null || fn(player) == (player.IsSelectable || ea);
                    liveOk = playerOk && (ea || got == on);
                    live = "live replay on " + hole.GetType().Name + " " + hole.ObjectId.Value + " with unseen NPC " + npc.ObjectId.Value
                        + ": skip=" + got + (ea ? " (EA skips here anyway)" : "") + ", player Sim EA answer kept=" + playerOk;
                }
                catch (Exception e) { liveOk = false; live = "live replay threw " + (e.InnerException ?? e).GetType().Name; }
                finally { c[3453] = c0; c[3454] = c1; c[3455] = c2; }   // the check's own calls are not counted
            }
            return B582Line("rhexitunseen", on, "an unseen NPC skips the rabbit hole exit pacing wait", fixedPure && liveOk && sRhxMissing.Length == 0,
                "EA waits (" + eaBug + "), seen NPC / household Sim keep the wait, EA's own skips kept, " + live
                + ", Sims in rabbit holes now " + inside + " (unseen NPCs " + insideNpcUnseen + ")", controls && liveOk, sRhxMissing, counters);
        }

        // ---- docs/613 §3 tipsnpcend ---------------------------------------------------------------------------------------
        // [verified, ilspycmd NPCShowManager::CreateNPCTipsPerformer] the daily NPC tips performer gets PerformerPerformForTips
        // at CriticalNPCBehavior, isAutonomous false. [verified, Interaction`2::DoLoop(ExitReason, InsideLoopFunction,
        // StateMachineClient, float)] EA's end for a low-LOD Sim (420 sim-min by day, 120 by night: Autonomy
        // kMaxTimeForLowLODInteractionDuringTheDay/Night, "low LOD Sims will exit out of any interaction") only applies when
        // the interaction is autonomous or at Autonomous priority or below, so it never reaches this one. [verified,
        // PerformerPerformForTips::LoopHandler] the loop's own end is the Fatigued buff; [verified, SimUpdate::Simulate,
        // Autonomy::Update] a low-LOD Sim gets no motive update (UpdateMotives runs only on the high-LOD branch), so fatigue
        // never comes. LoopHandler then clears every exit reason but CancelExternal after each trick, so a High GoHome queued
        // behind is wiped too (census: Perform For Tips 316-589 sim-min still with queue 2, motives frozen). Fix as EA
        // intended: an NPC performer that is not high LOD ends its tips loop at EA's own low-LOD cap (Finished, as DoLoop's
        // cap does), then Run takes EA's normal ending (stop, wait for tips, TNS).
        // Counters: [3457] tips loops ended at the low-LOD cap, [3458] unused since r72 (off arm runs EA only), [3459] past the cap
        // kept by the household rule or a seen Sim (switch on), [3460] errors.
        static string sTneMissing = "";

        static void ApplyTipsNpcEnd()
        {
            sTneMissing = typeof(PerformanceCareer.PerformerPerformForTips).GetMethod("LoopHandler", kInstAll) == null
                ? "PerformerPerformForTips.LoopHandler" : "";
            if (sTneMissing.Length > 0) { AppliedLog().Add("tipsnpcend: not found: " + sTneMissing); return; }
            Patch(typeof(PerformanceCareer.PerformerPerformForTips), "LoopHandler", typeof(TneLoopHook), "LoopHook", "LoopOrig");
        }

        // EA's low-LOD cap for this time of day (DoLoop's own expression).
        static float TneCap()
        {
            return SimClock.IsTimeBetweenTimes(AutonomyT.LowLODBeginDayTime, AutonomyT.LowLODEndDayTime)
                ? AutonomyT.MaxTimeForLowLODInteractionDuringTheDay : AutonomyT.MaxTimeForLowLODInteractionDuringTheNight;
        }

        // Pure: end this tips loop now? (EA: only on Fatigued, which a low-LOD Sim never gets.)
        internal static bool TneEnd(bool on, bool unseen, bool player, float lifeMinutes, float cap)
        {
            return on && unseen && !player && lifeMinutes > cap;
        }

        public class TneLoopHook
        {
            public void LoopHook(StateMachineClient smc, InteractionInstance.LoopData ld)
            {
                try
                {
                    PerformanceCareer.PerformerPerformForTips self = (object)this as PerformanceCareer.PerformerPerformForTips;
                    Sim a = self == null ? null : self.Actor;
                    if (a != null && sTneMissing.Length == 0 && ShortcutOn("tipsnpcend"))   // off: EA's body only (r72)
                    {
                        float cap = TneCap();
                        if (ld.mLifeTime > cap)
                        {
                            bool player = IsPlayerSim(a), unseen = !player && RhxUnseen(a);
                            if (TneEnd(true, unseen, player, ld.mLifeTime, cap))
                            {
                                PatchCounters()[3457]++;
                                a.AddExitReason(ExitReason.Finished);   // DoLoop's low-LOD cap ending
                                return;
                            }
                            PatchCounters()[3459]++;
                        }
                    }
                }
                catch (ResetException) { throw; }
                catch (Exception) { PatchCounters()[3460]++; }
                LoopOrig(smc, ld);
            }
            public void LoopOrig(StateMachineClient smc, InteractionInstance.LoopData ld) { } // receives PerformerPerformForTips.LoopHandler
        }

        // TipsNpcEndCheck run: pure decision with controls (under the cap, seen, household), then a live replay of the
        // decision on a world NPC with EA's real tunables and clock, and a census of Sims performing for tips now.
        public static object TipsNpcEndCheck(string what)
        {
            string counters = B582Counters("tipsnpcend", 3457, new[] { "ended at low-LOD cap", "past cap, switch off", "past cap, household/seen kept", "errors" }, sTneMissing);
            if (what == "result") return counters;
            if (what != "run") return "tipsnpcend <run|result>";
            bool on = ShortcutOn("tipsnpcend");
            bool eaBug = !TneEnd(false, true, false, 600f, 420f);
            bool fixedPure = on && TneEnd(on, true, false, 421f, 420f);
            bool controls = eaBug && !TneEnd(true, true, false, 420f, 420f) && !TneEnd(true, false, false, 600f, 420f) && !TneEnd(true, true, true, 600f, 420f);
            string live = "no unseen NPC in the world (pure only)";
            bool liveOk = true;
            int performing = 0, performingUnseen = 0;
            Sim npc = null;
            try
            {
                foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                {
                    if (s == null) continue;
                    bool unseen = !IsPlayerSim(s) && RhxUnseen(s);
                    if (s.CurrentInteraction is PerformanceCareer.PerformerPerformForTips) { performing++; if (unseen) performingUnseen++; }
                    if (npc == null && unseen) npc = s;
                }
                float cap = TneCap();
                if (npc != null)
                {
                    bool end = TneEnd(on, RhxUnseen(npc), IsPlayerSim(npc), cap + 1f, cap);
                    bool keepUnder = !TneEnd(on, RhxUnseen(npc), IsPlayerSim(npc), cap - 1f, cap);
                    liveOk = end == on && keepUnder;
                    live = "live decision on unseen NPC " + npc.ObjectId.Value + " (cap now " + cap + " sim-min): past cap ends=" + end + ", under cap kept=" + keepUnder;
                }
            }
            catch (Exception e) { liveOk = false; live = "live replay threw " + e.GetType().Name; }
            return B582Line("tipsnpcend", on, "an unseen NPC tips performance ends at EA's low-LOD cap", fixedPure && liveOk && sTneMissing.Length == 0,
                "EA never ends it (" + eaBug + "), under cap / seen / household kept, " + live + ", performing for tips now " + performing
                + " (unseen NPCs " + performingUnseen + ")", controls && liveOk, sTneMissing, counters);
        }
    
        static readonly int sLive_rhexitunseen = LiveAdd("rhexitunseen", () => EpLive(typeof(RhxSkipHook), 1));
        static readonly int sLive_tipsnpcend = LiveAdd("tipsnpcend", () => EpLive(typeof(TneLoopHook), 1));
    }
}
