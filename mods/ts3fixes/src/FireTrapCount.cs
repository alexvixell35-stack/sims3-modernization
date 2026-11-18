using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // firetrapcount (tuning kFireTrapCount, off by default; docs/570 §5). EA bug fix in the firefighter's large-fire and
    // earthquake emergencies: "free N Sims" asks for more Sims than are trapped on the lot, and the job times out.
    //
    // [verified, decompiled] FireFightingJob.SetupTask(TrappedSims / TrappedRubble) adds one task per Sim it traps: a
    // household Sim at home, else any NPC of the world, reset and teleported to a spot from FindPossiblePositions
    // (Lot.GetRoutableIndoorPositions). A task is done only by ConvinceToRun / CarryOutSim / CarryOutChild (TrappedSims)
    // or digging out the rubble (TrappedRubble); only SimDied takes a task away. Two ways a task is left with no Sim:
    //   (a) the trapped Sim leaves the lot without being freed (the reset moves a Sim whose new spot is not safely
    //       routable, a GoHome / teleport, an NPC culled): FirefighterEmergencySituation.OnSimLotChanged just drops it
    //       from Victims, OnParticipantDeleted too; the task stays pending, so the count can never be reached;
    //   (b) apartment lots (HasVirtualResidentialSlots): GetRoutableIndoorPositions skips the public rooms only, so the
    //       spots include the neighbours' hidden units. EA's own FindIsolatedFirePosition rejects hidden and fogged rooms
    //       for fires; the trap spots never did. Every door of a hidden room is a locked portal for every Sim
    //       (docs/552 §1), so a Sim trapped there cannot be reached, convinced or carried out, and is invisible.
    // Fix: (a) when a Sim of the job's trapped lists leaves the lot or is deleted while still trapped (TrappedInRoom /
    // TrappedUnderRubble, i.e. not being carried out), its task is dropped the way SimDied drops one: removed from Tasks,
    // then EA's OnTaskComplete removes it from PendingTasks and completes the job if nothing else is pending.
    // (b) after FindPossiblePositions, spots in a hidden or fogged room are removed on lots with virtual residential slots
    // (EA's own test from FindIsolatedFirePosition). Everything else is EA's.
    // Counters: [3956] tasks dropped for a Sim that left / was deleted, [3957] hidden-room spots removed, [3958] jobs
    // completed by a drop, [3959] errors. Check: FireTrapCountChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sFtcTrapped = typeof(FireFightingJob).GetField("mTrappedSims", kInstAll);
        static readonly FieldInfo sFtcRubble = typeof(FireFightingJob).GetField("mRubbleTrappedSims", kInstAll);
        static readonly FieldInfo sFtcPositions = typeof(FireFightingJob).GetField("mPossiblePlacementPositions", kInstAll);
        static readonly FieldInfo sFtcSpec = typeof(Sims3.Gameplay.Careers.Job).GetField("Specification", kInstAll);
        static readonly List<KeyValuePair<FireFightingJob, ulong>> sFtcDropped = new List<KeyValuePair<FireFightingJob, ulong>>();

        // The EA members the hooks read, by name; empty when all were found.
        internal static string FtcMissing()
        {
            List<string> m = new List<string>();
            if (sFtcTrapped == null) m.Add("FireFightingJob.mTrappedSims");
            if (sFtcRubble == null) m.Add("FireFightingJob.mRubbleTrappedSims");
            if (sFtcPositions == null) m.Add("FireFightingJob.mPossiblePlacementPositions");
            if (sFtcSpec == null) m.Add("Job.Specification");
            return string.Join(", ", m.ToArray());
        }

        static void ApplyFireTrapCount()
        {
            if (FtcMissing().Length > 0)
            {
                AppliedLog().Add("firetrapcount: members not found (" + FtcMissing() + "), not patched");
                return;
            }
            Patch(typeof(FirefighterEmergencySituation), "OnSimLotChanged", typeof(FtcSitHook), "LotHook", "LotOrig");
            Patch(typeof(FirefighterEmergencySituation), "OnParticipantDeleted", typeof(FtcSitHook), "DeletedHook", "DeletedOrig");
            Patch(typeof(FireFightingJob), "FindPossiblePositions", typeof(FtcJobHook), "PosHook", "PosOrig");
        }

        // ---- decisions (also used by the check) ----
        // The task to drop when a Sim stops being on the lot: none unless the switch is on, the Sim is one the job trapped
        // and it is still trapped (not freed, not being carried out).
        internal static TaskId FtcDropTask(bool on, bool inTrapped, bool inRubble, FirefighterEmergencySituation.VictimState state, bool gone)
        {
            if (!on || !gone) return TaskId.Invalid;
            if (inTrapped && state == FirefighterEmergencySituation.VictimState.TrappedInRoom) return TaskId.TrappedSims;
            if (inRubble && state == FirefighterEmergencySituation.VictimState.TrappedUnderRubble) return TaskId.TrappedRubble;
            return TaskId.Invalid;
        }

        // Keep a trap spot? EA's FindIsolatedFirePosition test, applied to the trap spots.
        internal static bool FtcKeepSpot(bool on, bool virtualResidential, bool hiddenOrFogged)
        {
            return !(on && virtualResidential && hiddenOrFogged);
        }

        static void FtcLeft(FirefighterEmergencySituation s, Sim sim, FirefighterEmergencySituation.VictimState before)
        {
            FireFightingJob job = s.Job;
            if (job == null || sim == null) return;
            List<Sim> trapped = sFtcTrapped.GetValue(job) as List<Sim>, rubble = sFtcRubble.GetValue(job) as List<Sim>;
            TaskId t = FtcDropTask(true, trapped != null && trapped.Contains(sim), rubble != null && rubble.Contains(sim), before, true);
            if (t == TaskId.Invalid) return;
            ulong id = sim.SimDescription == null ? sim.ObjectId.Value : sim.SimDescription.SimDescriptionId;
            foreach (KeyValuePair<FireFightingJob, ulong> d in sFtcDropped) if (d.Key == job && d.Value == id) return;  // once per Sim
            if (job.PendingTasks == null || !job.PendingTasks.Contains(t)) return;
            sFtcDropped.Add(new KeyValuePair<FireFightingJob, ulong>(job, id));
            if (sFtcDropped.Count > 64) sFtcDropped.RemoveAt(0);
            if (job.Tasks != null) job.Tasks.Remove(t);  // as SimDied
            int pendingBefore = job.PendingTasks.Count;
            job.OnTaskComplete(t, null);                  // EA: PendingTasks.Remove, then OnCompletion if nothing is left
            PatchCounters()[3956]++;
            if (pendingBefore == 1) PatchCounters()[3958]++;
        }

        static FirefighterEmergencySituation.VictimState FtcState(FirefighterEmergencySituation s, Sim sim)
        {
            FirefighterEmergencySituation.VictimState v;
            if (s.Victims != null && sim != null && sim.SimDescription != null && s.Victims.TryGetValue(sim.SimDescription, out v)) return v;
            return FirefighterEmergencySituation.VictimState.NotAVictim;
        }

        public class FtcSitHook
        {
            public void LotHook(Sim s, Lot oldLot, Lot newLot)
            {
                FirefighterEmergencySituation me = (FirefighterEmergencySituation)(object)this;
                bool on = false;
                FirefighterEmergencySituation.VictimState before = FirefighterEmergencySituation.VictimState.NotAVictim;
                try
                {
                    on = ShortcutOn("firetrapcount") && oldLot != null && oldLot == me.Lot && newLot != me.Lot;
                    if (on) before = FtcState(me, s);
                }
                catch (Exception) { PatchCounters()[3959]++; on = false; }
                LotOrig(s, oldLot, newLot);
                if (!on) return;
                try { FtcLeft(me, s, before); } catch (Exception) { PatchCounters()[3959]++; }
            }
            public void LotOrig(Sim s, Lot oldLot, Lot newLot) { }

            public void DeletedHook(Sim participant)
            {
                FirefighterEmergencySituation me = (FirefighterEmergencySituation)(object)this;
                bool on = false;
                FirefighterEmergencySituation.VictimState before = FirefighterEmergencySituation.VictimState.NotAVictim;
                try
                {
                    on = ShortcutOn("firetrapcount") && participant != null && participant.SimDescription != null && !participant.SimDescription.IsDead;  // a death is SimDied's
                    if (on) before = FtcState(me, participant);
                }
                catch (Exception) { PatchCounters()[3959]++; on = false; }
                DeletedOrig(participant);
                if (!on) return;
                try { FtcLeft(me, participant, before); } catch (Exception) { PatchCounters()[3959]++; }
            }
            public void DeletedOrig(Sim participant) { }
        }

        public class FtcJobHook
        {
            public void PosHook()
            {
                PosOrig();
                if (!ShortcutOn("firetrapcount")) return;
                try
                {
                    FireFightingJob job = (FireFightingJob)(object)this;
                    JobCreationSpec spec = sFtcSpec.GetValue(job) as JobCreationSpec;
                    Lot lot = spec == null ? null : spec.mLot;
                    Dictionary<Vector3, Vector3> spots = sFtcPositions.GetValue(job) as Dictionary<Vector3, Vector3>;
                    if (lot == null || spots == null || !lot.HasVirtualResidentialSlots) return;
                    List<Vector3> drop = new List<Vector3>();
                    foreach (Vector3 p in spots.Keys)
                    {
                        LotLocation loc = LotLocation.Invalid;
                        World.GetLotLocation(p, ref loc);
                        int room = World.GetRoomId(lot.LotId, loc, eRoomDefinition.LightBlocking);
                        if (!FtcKeepSpot(true, true, lot.IsRoomHidden(room) || TombRoomManager.IsPointInAFoggedRoom(p))) drop.Add(p);
                    }
                    foreach (Vector3 p in drop) spots.Remove(p);
                    PatchCounters()[3957] += drop.Count;
                }
                catch (Exception) { PatchCounters()[3959]++; }
            }
            public void PosOrig() { }
        }
    }
}
