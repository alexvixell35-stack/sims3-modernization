using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // firecount (tuning kFireCount, off by default; docs/868). EA bug fix in the firefighter's large-fire and earthquake
    // emergencies: the "free N Sims" / dig-out count takes tasks for Sims the job never trapped and for the dead.
    //
    // [verified, decompiled] A rescue (ConvinceToRun.Run, CarryOutChild.Run, PlaceSimDown.InteractionDone for TrappedSims;
    // FireRubblePile.ClearFireRubble.Run for TrappedRubble) calls Occupation.SimCompletedTask(actor, task, null): the target
    // is always null. The occupation's listener (OnSimCompletedOccupationTask, synchronous EventTracker dispatch, owner's
    // events only) pays the task and calls CurrentJob.OnTaskComplete(task, null), which takes one pending task of that id
    // with no look at who was freed. So:
    //   (1) a Sim the job never trapped (a passer-by who joined on the lot, or a rescued victim) whose run outside fails
    //       becomes TrappedInRoom again (Panic.InteractionFail); freeing them takes a real victim's task;
    //   (2) a rubble victim reset mid-emergency gets a second pile (Panic.AddSimInteractionOnReset ->
    //       AddRubbleTrappedPerson; the first pile stays), and each dig takes a task;
    //   (3) SimDied (FirefighterEmergencySituation.DelayReaper, from Urnstone.KillSim.Run) takes one pending task for any
    //       Sim of the job's lists, freed or not, with no OnTaskComplete: the last one leaves the job at 0 pending, never
    //       completed, until the timeout.
    // Fix: the three rescue paths for TrappedSims all call FirefighterEmergencySituation.SetVictimHasBeenConvincedToRun
    // (target) just before SimCompletedTask, on the same script task with no yield between [verified]: a swap on it notes
    // the Sim, its task and the tick. For TrappedRubble the digger is the Sim whose task runs (Simulator.CurrentTask) and
    // its current interaction's target is the pile [inferred], still in the situation's RubblePiles (the pile is destroyed
    // on the second of the fade callback and the dig's Cleanup [verified]; Cleanup runs after Run [inferred]);
    // RubblePiles maps it to its Sim [verified]. A swap on FireFightingJob.OnTaskComplete then
    // lets a rescue take a task only for a Sim of the job's list not yet accounted in this job (FcCore.Rescue): its first
    // rescue counts and marks it; a passer-by, a re-trapped Sim, a second pile, or a pile of a Sim whose task
    // firetrapcount dropped when it left the lot take none. A completion the fix cannot place (firetrapcount's and
    // fireleftover's drops, other paths) is EA's. A swap on SimDied undoes EA's removal and, for a Sim not accounted yet
    // (still trapped, or freed by another firefighter, whose rescue the owner's listener ignores [verified]), drops one task
    // as firetrapcount's FtcLeft does (Tasks.Remove, then EA's OnTaskComplete, so the job completes when nothing is left);
    // a Sim whose rescue counted takes none. EA's bSimHasDied / bFailedJob flags stay as EA's body set them (bFailedJob is
    // read only by SimDied itself [verified]).
    // "Accounted" is firetrapcount's per-job list sFtcDropped (job, Sim id; cleared at world quit, never saved): FtcLeft
    // skips a Sim firecount counted (no double drop for a re-trapped Sim who leaves), and firecount skips a Sim FtcLeft
    // dropped. After a load the list is empty, so every listed Sim's next rescue counts as EA does [inferred safe fallback].
    // With the switch off EA's code runs as is; the hooks only count what they would change. Household Sims' jobs are
    // included (EA bug fix): only the job's task count changes, never a Sim.
    // Counters: [2358] rescues not counted (passer-by, re-trapped, second pile), [2359] deaths handled, [2360] cases seen
    // with the switch off, [2361] errors. Check: FireCountCheck.
    public static partial class Fixes
    {
        static readonly FieldInfo sFcSituation = typeof(FireFightingJob).GetField("mSituation", kInstAll);
        static Sim sFcFreed;            // the Sim SetVictimHasBeenConvincedToRun last marked, with its script task and tick
        static ObjectGuid sFcFreedTask;
        static long sFcFreedTick;
        static bool sFcDirect;          // our own drop is running: EA's OnTaskComplete as is
        static bool sFcHooked;

        internal static string FcMissing()
        {
            string m = FtcMissing();
            if (sFcSituation == null) m += (m.Length > 0 ? ", " : "") + "FireFightingJob.mSituation";
            return m;
        }

        static void ApplyFireCount()
        {
            if (sFcHooked) return;
            sFcHooked = true;
            if (FcMissing().Length > 0) { AppliedLog().Add("firecount: members not found (" + FcMissing() + "), not patched"); return; }
            World.OnWorldQuitEventHandler += delegate { sFcFreed = null; sFtcDropped.Clear(); };  // no old world's jobs held
            Patch(typeof(FirefighterEmergencySituation), "SetVictimHasBeenConvincedToRun", typeof(FcHook), "FreedHook", "FreedOrig");
            Patch(typeof(FireFightingJob), "OnTaskComplete", typeof(FcHook), "TaskHook", "TaskOrig");
            Patch(typeof(FireFightingJob), "SimDied", typeof(FcHook), "DiedHook", "DiedOrig");
        }

        // firetrapcount's key for a Sim in sFtcDropped (FtcLeft)
        static ulong FcId(Sim sim) { return sim.SimDescription == null ? sim.ObjectId.Value : sim.SimDescription.SimDescriptionId; }

        static bool FcAccounted(FireFightingJob job, Sim sim)
        {
            ulong id = FcId(sim);
            foreach (KeyValuePair<FireFightingJob, ulong> d in sFtcDropped) if (d.Key == job && d.Value == id) return true;
            return false;
        }

        static void FcMark(FireFightingJob job, Sim sim)
        {
            if (FcAccounted(job, sim)) return;
            sFtcDropped.Add(new KeyValuePair<FireFightingJob, ulong>(job, FcId(sim)));
            if (sFtcDropped.Count > 64) sFtcDropped.RemoveAt(0);  // FtcLeft's cap
        }

        // The Sim a TrappedSims rescue just freed: noted by SetVictimHasBeenConvincedToRun on this task in this tick.
        static Sim FcFreedNow()
        {
            Sim v = sFcFreed;
            bool now = v != null && sFcFreedTask == Simulator.CurrentTask && sFcFreedTick == SimClock.CurrentTicks;
            sFcFreed = null;
            return now ? v : null;
        }

        // The pile the running Sim is digging and the Sim EA maps it to (null when the pile's Sim is gone); false when the
        // running Sim is not digging a pile of this emergency (an alarm, firetrapcount's drop on the leaving Sim's task).
        static bool FcPileSim(FirefighterEmergencySituation s, out Sim sim)
        {
            sim = null;
            Sim actor = GameObject.GetObject<Sim>(Simulator.CurrentTask);
            InteractionInstance cur = actor == null ? null : actor.CurrentInteraction;
            GameObject pile = cur == null ? null : cur.Target as GameObject;
            return pile != null && s.RubblePiles != null && s.RubblePiles.TryGetValue(pile, out sim);
        }

        // True when the hook keeps EA's OnTaskComplete from taking a task.
        static bool FcTask(FireFightingJob job, TaskId t)
        {
            if (sFcDirect || job == null || (t != TaskId.TrappedSims && t != TaskId.TrappedRubble)) return false;
            Sim sim = null;
            if (t == TaskId.TrappedSims) sim = FcFreedNow();
            FirefighterEmergencySituation s = sFcSituation.GetValue(job) as FirefighterEmergencySituation;
            List<Sim> list = (t == TaskId.TrappedSims ? sFtcTrapped : sFtcRubble).GetValue(job) as List<Sim>;
            if (s == null || list == null) return false;  // a rabbit-hole job, or no list: EA's
            bool mapped = t == TaskId.TrappedSims ? sim != null : FcPileSim(s, out sim);
            int d = FcCore.Rescue(mapped, sim != null && list.Contains(sim), sim != null && FcAccounted(job, sim));
            if (d == FcCore.Ea) return false;
            bool on = ShortcutOn("firecount");
            if (d == FcCore.Skip) { PatchCounters()[on ? 2358 : 2360]++; return on; }
            if (on) FcMark(job, sim);
            return false;
        }

        // The task the fix takes for this death: Invalid for an accounted Sim (its rescue counted, or its task dropped), or
        // one in neither list. Room list first, as EA's SimDied (a Sim is never in both: FindSimToTrap skips both lists
        // [verified]).
        static TaskId FcDeathDrop(FireFightingJob job, Sim sim)
        {
            List<Sim> trapped = sFtcTrapped.GetValue(job) as List<Sim>, rubble = sFtcRubble.GetValue(job) as List<Sim>;
            bool drops = FcCore.DeathDrops(FcAccounted(job, sim));
            if (trapped != null && trapped.Contains(sim)) return drops ? TaskId.TrappedSims : TaskId.Invalid;
            if (rubble != null && rubble.Contains(sim)) return drops ? TaskId.TrappedRubble : TaskId.Invalid;
            return TaskId.Invalid;
        }

        // After EA's SimDied: pend / all are the task lists before it.
        static void FcDied(FireFightingJob job, Sim sim, List<TaskId> pend, List<TaskId> all, TaskId drop)
        {
            List<TaskId> p = job.PendingTasks, a = job.Tasks;  // EA's live lists (SimDied removes through these getters)
            if (p.Count == pend.Count && a.Count == all.Count) return;  // EA took nothing: bFailedJob set, or not a listed Sim
            bool differs = drop == TaskId.Invalid || (p.Count == 0 && pend.Count > 0);  // a freed Sim's task taken / the job left at 0, never completed
            if (!ShortcutOn("firecount")) { if (differs) PatchCounters()[2360]++; return; }
            p.Clear(); p.AddRange(pend);
            a.Clear(); a.AddRange(all);
            if (drop != TaskId.Invalid)
            {
                FcMark(job, sim);
                a.Remove(drop);  // as SimDied and FtcLeft
                sFcDirect = true;
                try { job.OnTaskComplete(drop, null); }  // EA: PendingTasks.Remove, then OnCompletion if nothing is left
                finally { sFcDirect = false; }
            }
            if (differs) PatchCounters()[2359]++;
        }

        // `this` is the FireFightingJob at run time (method swap); FreedHook replaces a static method.
        public class FcHook
        {
            public static void FreedHook(Sim victim)
            {
                try { sFcFreed = victim; sFcFreedTask = Simulator.CurrentTask; sFcFreedTick = SimClock.CurrentTicks; }
                catch (Exception) { PatchCounters()[2361]++; }
                FreedOrig(victim);
            }
            public static void FreedOrig(Sim victim) { }

            public void TaskHook(TaskId taskId, GameObject target)
            {
                bool skip = false;
                try { skip = FcTask((object)this as FireFightingJob, taskId); }
                catch (Exception) { PatchCounters()[2361]++; skip = false; }  // a throw here would drop the occupation's listener
                if (!skip) TaskOrig(taskId, target);
            }
            public void TaskOrig(TaskId taskId, GameObject target) { }

            public void DiedHook(Sim diedSim)
            {
                FireFightingJob job = (object)this as FireFightingJob;
                List<TaskId> pend = null, all = null;
                TaskId drop = TaskId.Invalid;
                try
                {
                    FirefighterEmergencySituation s = job == null ? null : sFcSituation.GetValue(job) as FirefighterEmergencySituation;
                    if (s != null && diedSim != null && job.PendingTasks != null && job.Tasks != null)
                    {
                        drop = FcDeathDrop(job, diedSim);
                        pend = new List<TaskId>(job.PendingTasks);
                        all = new List<TaskId>(job.Tasks);
                    }
                }
                catch (Exception) { PatchCounters()[2361]++; pend = null; }
                DiedOrig(diedSim);
                if (pend == null) return;
                try { FcDied(job, diedSim, pend, all, drop); }
                catch (Exception) { PatchCounters()[2361]++; }
            }
            public void DiedOrig(Sim diedSim) { }
        }

        static int FcCountOf(List<TaskId> tasks, TaskId t)
        {
            int n = 0;
            if (tasks != null) foreach (TaskId x in tasks) if (x == t) n++;
            return n;
        }

        // Listed Sims still trapped (state a or b) and not accounted.
        static int FcStill(FireFightingJob job, FirefighterEmergencySituation s, List<Sim> sims, FirefighterEmergencySituation.VictimState a, FirefighterEmergencySituation.VictimState b)
        {
            int n = 0;
            if (sims != null)
                foreach (Sim sim in sims)
                {
                    if (sim == null) continue;
                    FirefighterEmergencySituation.VictimState v = FtcState(s, sim);
                    if ((v == a || v == b) && !FcAccounted(job, sim)) n++;
                }
            return n;
        }

        static bool FcListed(List<Sim> sims, SimDescription d)
        {
            if (sims != null) foreach (Sim sim in sims) if (sim != null && sim.SimDescription == d) return true;
            return false;
        }

        // The live emergency: pending rescue tasks by id against the job's Sims still trapped by state. Read-only.
        static string FcScan()
        {
            FireFightingJob job; Lot lot; Sim owner;
            if (FcMissing().Length > 0 || !FgJob(out job, out lot, out owner)) return "no emergency";
            FirefighterEmergencySituation s = sFcSituation.GetValue(job) as FirefighterEmergencySituation;
            List<TaskId> p = job.PendingTasks;
            if (s == null || p == null) return "emergency " + job.Id + " not set up";
            List<Sim> room = sFtcTrapped.GetValue(job) as List<Sim>, rubble = sFtcRubble.GetValue(job) as List<Sim>;
            int pRoom = FcCountOf(p, TaskId.TrappedSims), pRubble = FcCountOf(p, TaskId.TrappedRubble);
            int tRoom = FcStill(job, s, room, FirefighterEmergencySituation.VictimState.TrappedInRoom, FirefighterEmergencySituation.VictimState.TryingToBeCarriedOut);
            int tRubble = FcStill(job, s, rubble, FirefighterEmergencySituation.VictimState.TrappedUnderRubble, FirefighterEmergencySituation.VictimState.TrappedUnderRubble);
            int stray = 0, piles = 0, orphans = 0;
            if (s.Victims != null)
                foreach (KeyValuePair<SimDescription, FirefighterEmergencySituation.VictimState> kv in s.Victims)
                    if (kv.Value == FirefighterEmergencySituation.VictimState.TrappedInRoom && !FcListed(room, kv.Key)) stray++;
            if (s.RubblePiles != null)
                foreach (KeyValuePair<GameObject, Sim> kv in s.RubblePiles)
                {
                    piles++;
                    Sim v = kv.Value;
                    if (v == null || rubble == null || !rubble.Contains(v) || FcAccounted(job, v) || FtcState(s, v) != FirefighterEmergencySituation.VictimState.TrappedUnderRubble) orphans++;
                }
            bool match = pRoom == tRoom && pRubble == tRubble;
            return "emergency " + job.Id + " on " + lot.Name + ": pending TrappedSims " + pRoom + " vs listed Sims still trapped " + tRoom
                + ", pending TrappedRubble " + pRubble + " vs " + tRubble + "; trapped Sims not in the job's list " + stray + "; rubble piles "
                + piles + " (no trapped listed Sim behind it " + orphans + "); " + (match ? "match" : "MISMATCH");
        }

        // FireCountCheck run: the reported layouts through FcCore (read-only).
        //   off BROKEN = EA's count in every case; on FIXED = (a) a passer-by's rescue leaves the count, (b) a freed Sim's
        //   death leaves it, (c) the last trapped Sim's death completes the job, (d) a dig of a second pile (or of a pile
        //   whose Sim's task was dropped) leaves it. Controls (EA's count in both arms): a listed Sim's first rescue takes
        //   one, a still-trapped Sim's death with another left takes one, a completion the fix cannot place takes one.
        //   scan    the live emergency: pending tasks against trapped Sims; result  counters 2358-2361
        public static object FireCountCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = FcMissing();
            string counters = "firecount: rescues not counted 2358=" + c[2358] + ", deaths handled 2359=" + c[2359] + ", seen off 2360=" + c[2360]
                + ", errors 2361=" + c[2361] + "; accounted Sims " + sFtcDropped.Count + "; members missing: " + (missing.Length == 0 ? "none" : missing);
            if (what == "result") return counters;
            if (what == "scan") return "firecount scan: " + FcScan() + "; " + counters;
            if (what != "run") return "firecount <run|scan|result>";
            bool on = ShortcutOn("firecount"), bDone, cDone, kDone;
            bool a = FcCore.AfterRescue(on, 2, FcCore.Rescue(true, false, false)) == 2;
            bool b = FcCore.AfterDeath(on, 1, true, out bDone) == 1 && !bDone;
            bool cc = FcCore.AfterDeath(on, 1, false, out cDone) == 0 && cDone;
            bool d = FcCore.AfterRescue(on, 1, FcCore.Rescue(true, true, true)) == 1;
            int fixedN = (a ? 1 : 0) + (b ? 1 : 0) + (cc ? 1 : 0) + (d ? 1 : 0);
            bool controlsOk = FcCore.AfterRescue(on, 2, FcCore.Rescue(true, true, false)) == 1
                && FcCore.AfterDeath(on, 2, false, out kDone) == 1 && !kDone
                && FcCore.AfterRescue(on, 1, FcCore.Rescue(false, false, false)) == 0;
            string v = !on ? (fixedN == 0 ? "BROKEN" : "UNEXPECTED") : (fixedN == 4 ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || missing.Length > 0 ? "UNEXPECTED" : v;
            return "firecount check (" + (on ? "on" : "off") + "): passer-by rescue left the count=" + a + ", freed Sim's death left it=" + b
                + ", last trapped Sim's death completed the job=" + cc + ", second pile dig left it=" + d + " (" + v + "); controls ok=" + controlsOk
                + "; verdict: " + verdict + "; " + counters;
        }
    }
}
