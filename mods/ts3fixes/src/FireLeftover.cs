using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // fireleftover (tuning kFireLeftover, off by default; docs/862). EA bug fix in the firefighter's emergencies: the job
    // waits for a rescue or window-fire task nobody can do any more, so the player has to sit out the timeout (a player
    // report: "trying to find the last person to be rescued, but there's nobody left ... that one took 12h").
    //
    // [verified, decompiled] A FireFightingJob ends when its pending task list is empty. EA has one leftover check: 15
    // sim-minutes after the last fire tile goes out (FireManager.UnregisterFire -> StartFireCompleteAlarm) it completes the
    // fire tasks if no fire is left. Nothing like it exists for the other tasks:
    //   - rescue tasks (TrappedSims / TrappedRubble) are done only by the job owner's own ConvinceToRun / carry / dig
    //     (Occupation.OnSimCompletedOccupationTask ignores any other Sim); a trapped Sim who stops being trapped another way
    //     (rescued by another firefighter, leaves or is taken off the lot, deleted, a rubble pile gone without the owner's
    //     dig) keeps its task. firetrapcount drops it on the two events EA hands out (lot change, deletion); this check
    //     also covers the rest, by state;
    //   - the window-fire task is done only when one of the owner's own extinguish runs sees the last spreading window fire
    //     go out; window fires put out by rain, sprinklers, a unicorn or a reset mid-run leave it pending.
    // Meanwhile Job.OnJobTimeout extends the timeout for as long as the firefighter stays on the lot.
    // Fix: once a sim-minute (FireGreet's alarm) while an emergency is set up: rescue tasks above the number of the job's
    // trapped Sims still on its lot and still trapped (TrappedInRoom / being carried out, TrappedUnderRubble) are dropped
    // the way SimDied drops one; a pending window-fire task with no spreading window fire left is completed with EA's own
    // call. Each needs the same state for 15 sim-minutes in a row (FloCore.kConfirmTicks, as EA's fire-task fallback), so
    // EA's own short gap between a victim going free and the task completing is never taken for a leftover. The job then
    // ends through EA's OnTaskComplete -> OnCompletion; a late EA completion of the same task is a no-op there (Job.
    // OnTaskComplete acts only when the task is still pending; UnlockEntrances only unlocks doors still locked).
    // Nothing else changes; the household firefighter is affected only through the job's outcome (EA bug fix).
    // Counters: [2178] rescue tasks dropped, [2179] window-fire tasks completed, [2180] errors. Check: FireLeftoverCheck.
    public static partial class Fixes
    {
        static readonly FieldInfo sFloSituation = typeof(FireFightingJob).GetField("mSituation", kInstAll);
        static FireFightingJob sFloJob;
        static int sFloExcessRoom, sFloExcessRubble, sFloTicksRoom, sFloTicksRubble, sFloTicksWindow;

        internal static string FloMissing()
        {
            string m = FtcMissing();
            if (sFloSituation == null) m += (m.Length > 0 ? ", " : "") + "FireFightingJob.mSituation";
            if (sFgJobLot == null) m += (m.Length > 0 ? ", " : "") + "Job.Lot";
            return m;
        }

        // The job's Sims (one list) still on the lot in one of the two given states.
        static int FloStillTrapped(FirefighterEmergencySituation s, List<Sim> sims, Lot lot, FirefighterEmergencySituation.VictimState a, FirefighterEmergencySituation.VictimState b)
        {
            int n = 0;
            if (sims == null) return 0;
            foreach (Sim sim in sims)
            {
                if (sim == null || sim.HasBeenDestroyed || sim.LotCurrent != lot) continue;
                FirefighterEmergencySituation.VictimState v = FtcState(s, sim);
                if (v == a || v == b) n++;
            }
            return n;
        }

        static int FloCount(List<TaskId> tasks, TaskId t)
        {
            int n = 0;
            if (tasks != null) foreach (TaskId x in tasks) if (x == t) n++;
            return n;
        }

        // Drops up to n tasks of type t; false once the job has ended.
        static bool FloDropTasks(FireFightingJob job, TaskId t, int n)
        {
            for (int i = 0; i < n && ActiveFireFighter.sActiveJob == job && job.PendingTasks != null && job.PendingTasks.Contains(t); i++)
            {
                if (job.Tasks != null) job.Tasks.Remove(t);  // as SimDied
                job.OnTaskComplete(t, null);                  // EA: PendingTasks.Remove, then OnCompletion if nothing is left
                PatchCounters()[2178]++;
            }
            return ActiveFireFighter.sActiveJob == job;
        }

        // Called from FireGreetTick, once a sim-minute.
        static void FireLeftoverTick()
        {
            try
            {
                FireFightingJob job; Lot lot; Sim owner;
                if (!ShortcutOn("fireleftover") || FloMissing().Length > 0 || !FgJob(out job, out lot, out owner)) { sFloJob = null; return; }
                FirefighterEmergencySituation s = sFloSituation.GetValue(job) as FirefighterEmergencySituation;
                List<TaskId> pending = job.PendingTasks;
                if (s == null || pending == null) { sFloJob = null; return; }  // not set up yet (EA builds the situation last)
                if (job != sFloJob) { sFloJob = job; sFloExcessRoom = sFloExcessRubble = sFloTicksRoom = sFloTicksRubble = sFloTicksWindow = 0; }

                const FirefighterEmergencySituation.VictimState inRoom = FirefighterEmergencySituation.VictimState.TrappedInRoom,
                    carried = FirefighterEmergencySituation.VictimState.TryingToBeCarriedOut,
                    rubble = FirefighterEmergencySituation.VictimState.TrappedUnderRubble;
                int pRoom = FloCount(pending, TaskId.TrappedSims), pRubble = FloCount(pending, TaskId.TrappedRubble);
                int sRoom = FloStillTrapped(s, sFtcTrapped.GetValue(job) as List<Sim>, lot, inRoom, carried);
                int sRubble = FloStillTrapped(s, sFtcRubble.GetValue(job) as List<Sim>, lot, rubble, rubble);
                int eRoom = pRoom - sRoom, eRubble = pRubble - sRubble;
                sFloTicksRoom = FloCore.Seen(eRoom, sFloExcessRoom, sFloTicksRoom); sFloExcessRoom = eRoom;
                sFloTicksRubble = FloCore.Seen(eRubble, sFloExcessRubble, sFloTicksRubble); sFloExcessRubble = eRubble;
                int dRoom = FloCore.Drop(true, eRoom, sFloTicksRoom), dRubble = FloCore.Drop(true, eRubble, sFloTicksRubble);
                if (dRoom > 0 && !FloDropTasks(job, TaskId.TrappedSims, dRoom)) return;
                if (dRubble > 0 && !FloDropTasks(job, TaskId.TrappedRubble, dRubble)) return;

                bool pWindow = pending.Contains(TaskId.WindowFires), major = lot.FireManager != null && lot.FireManager.HasMajorWindowFires();
                sFloTicksWindow = pWindow && !major ? sFloTicksWindow + 1 : 0;
                if (!FloCore.WindowDone(true, pWindow, major, sFloTicksWindow)) return;
                job.OnTaskComplete(TaskId.WindowFires, lot);  // EA's call shape (ExtinguishBase.Run); it also unlocks the entrances
                PatchCounters()[2179]++;
            }
            catch (Exception) { PatchCounters()[2180]++; }
        }

        // FireLeftoverCheck run: the reported case (one rescue task left, nobody trapped on the lot, for 15 sim-minutes) and a
        // window-fire task with no window fire left as long, with controls; then the live emergency, if any. Read-only.
        //   off BROKEN = tasks kept; on FIXED = rescue task dropped and window task completed. Controls (EA's outcome in both):
        //   excess seen for 14 minutes only (EA's own dig gap), no excess, a window fire still burning, a window clear for
        //   14 minutes, no window task, and the tick counting. The verdict needs every EA member the tick reads to be found.
        //   result  counters 2178-2180
        public static object FireLeftoverCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = FloMissing();
            string counters = "fireleftover: rescue tasks dropped 2178=" + c[2178] + ", window tasks completed 2179=" + c[2179] + ", errors 2180=" + c[2180]
                + "; members missing: " + (missing.Length == 0 ? "none" : missing);
            if (what == "result") return counters;
            if (what != "run") return "fireleftover <run|result>";
            bool on = ShortcutOn("fireleftover");
            const int k = FloCore.kConfirmTicks;
            bool drop = FloCore.Drop(on, 1, k) == 1;
            bool window = FloCore.WindowDone(on, true, false, k);
            bool controlsOk = FloCore.Drop(on, 1, k - 1) == 0 && FloCore.Drop(on, 0, k) == 0 && FloCore.Drop(on, -1, k) == 0
                && !FloCore.WindowDone(on, true, true, k) && !FloCore.WindowDone(on, true, false, k - 1) && !FloCore.WindowDone(on, false, false, k)
                && FloCore.Seen(1, 1, 3) == 4 && FloCore.Seen(2, 1, 9) == 1 && FloCore.Seen(0, 1, 9) == 0;
            bool all = drop && window, none = !drop && !window;
            string verdict = !controlsOk || missing.Length > 0 ? "UNEXPECTED" : !on ? (none ? "BROKEN" : "UNEXPECTED") : (all ? "FIXED" : "UNEXPECTED");
            string live = "no emergency";
            FireFightingJob job; Lot lot; Sim owner;
            if (missing.Length == 0 && FgJob(out job, out lot, out owner))
            {
                FirefighterEmergencySituation s = sFloSituation.GetValue(job) as FirefighterEmergencySituation;
                List<TaskId> p = job.PendingTasks;
                live = "emergency " + job.Id + " on " + lot.Name + ": set up=" + (s != null && p != null) + ", leftover minutes seen (rescue "
                    + sFloTicksRoom + ", rubble " + sFloTicksRubble + ", window " + sFloTicksWindow + ")";
                if (s != null && p != null)
                    live += ", pending " + p.Count + " (rescue " + FloCount(p, TaskId.TrappedSims) + ", trapped still on lot "
                        + FloStillTrapped(s, sFtcTrapped.GetValue(job) as List<Sim>, lot, FirefighterEmergencySituation.VictimState.TrappedInRoom, FirefighterEmergencySituation.VictimState.TryingToBeCarriedOut)
                        + "; rubble " + FloCount(p, TaskId.TrappedRubble) + "; window fires " + p.Contains(TaskId.WindowFires) + ", spreading window fires left "
                        + (lot.FireManager != null && lot.FireManager.HasMajorWindowFires()) + "; fires " + (lot.FireManager == null ? -1 : lot.FireManager.FireCount) + ")";
            }
            return "fireleftover check (switch " + (on ? "on" : "off") + "): one rescue task left, nobody trapped on the lot -> dropped=" + drop
                + ", window-fire task with no window fire left -> completed=" + window + "; controls ok=" + controlsOk + "; " + live + "; verdict: " + verdict + "; " + counters;
        }
    }
}
