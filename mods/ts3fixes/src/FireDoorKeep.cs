using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // firedoorkeep (tuning kFireDoorKeep, off by default; docs/864). EA bug fix: the firefighter's emergencies and door locks.
    //
    // [verified, decompiled] FireFightingJob.SetupTask(TrappedSims) locks the trapped Sim's room: LockDoorsInRoom sets every
    // unlocked door (not IAmNotAFrontDoor) of that room, and of the rooms behind its stairs and elevators, to Nobody.
    // CleanUpJob, at the end of every job, unlocks only for JobId.LargeFires, and then every locked door of the lot
    // (SetLockType(Anybody) on any lock type), not just the job's. So
    //   (1) after a large fire every lock on the lot is gone: one-Sim, household, pets and owner-list locks, resort VIP room
    //       doors, the lot's own Nobody doors (apartment NPC doors, the builder's); a one-way door the job locked loses its
    //       one-way room (SetLockType clears it);
    //   (2) a door another job type locked for a trapped Sim stays Nobody for good.
    // Fix: the first top-level LockDoorsInRoom of a job (empty traversed list; recursion passes through) records every door's
    // state before EA's body, and each call records which doors EA's body changed. Around CleanUpJob (any job id), every
    // door's state is read before EA's body and FdkCore.Restore decides after it: a lock the job never sets comes back; a bare
    // Nobody door the job locked goes back to its state before the job (unlocked, one-way room kept; EA's lock effect stopped);
    // a bare Nobody door that was already so before the job stays locked. Without a record (no trapped Sim, or a game loaded
    // mid-job) a bare Nobody door keeps EA's outcome. A door whose state after EA's body is not one the job leaves (bare
    // Nobody / Anybody) is never touched. With the switch off the hooks only count. The household's doors are included
    // (EA bug fix, AGENTS.md household rule): their locks come back as the player set them.
    // Counters: [2187] locks given back after a large-fire cleanup, [2188] job locks undone after another job type,
    // [2189] doors seen with the switch off, [2190] errors. Check: FireDoorKeepCheck.
    public static partial class Fixes
    {
        static readonly MethodInfo sFdkHhCallback = typeof(CommonDoor).GetMethod("SetHouseholdCallback", kInstAll, null, Type.EmptyTypes, null);
        static FireFightingJob sFdkJob;                  // the job the record belongs to (one emergency at a time: ActiveFireFighter.sActiveJob)
        static Dictionary<Door, FdkState> sFdkBefore;    // each door's state before the job's first lock (a later call's state for doors it locked)
        static List<Door> sFdkLocked;                    // doors EA's LockDoorsInRoom changed
        static bool sFdkHooked;

        internal static string FdkMissing()
        {
            string m = "";
            if (sFgJobLot == null) m = "Job.Lot";
            if (sFdkHhCallback == null) m += (m.Length > 0 ? ", " : "") + "CommonDoor.SetHouseholdCallback";
            return m;
        }

        static void ApplyFireDoorKeep()
        {
            if (sFdkHooked) return;
            sFdkHooked = true;
            if (FdkMissing().Length > 0) { AppliedLog().Add("firedoorkeep: members not found (" + FdkMissing() + "), not patched"); return; }
            World.OnWorldQuitEventHandler += delegate { FdkForget(); };  // no old world's doors held past a quit
            Patch(typeof(FireFightingJob), "LockDoorsInRoom", typeof(FdkJobHook), "LockHook", "LockOrig");
            Patch(typeof(FireFightingJob), "CleanUpJob", typeof(FdkJobHook), "CleanHook", "CleanOrig");
        }

        static void FdkForget() { sFdkJob = null; sFdkBefore = null; sFdkLocked = null; }

        static FdkState FdkOf(CommonDoor d) { return new FdkState((int)d.LockType, d.LockOwner, d.OneWayRoomDestinationId); }

        // Every door EA's job code can touch on the job's lot (both EA loops skip IAmNotAFrontDoor), with its state now.
        static Dictionary<Door, FdkState> FdkRead(FireFightingJob job)
        {
            Lot lot = job == null ? null : sFgJobLot.GetValue(job, null) as Lot;
            if (lot == null) return null;
            Dictionary<Door, FdkState> r = new Dictionary<Door, FdkState>();
            foreach (Door d in lot.GetObjects<Door>())
                if (d != null && !(d is IAmNotAFrontDoor)) r[d] = FdkOf(d);
            return r;
        }

        static void FdkRecord(FireFightingJob job, Dictionary<Door, FdkState> was)
        {
            if (sFdkJob != job) { sFdkJob = job; sFdkBefore = new Dictionary<Door, FdkState>(was); sFdkLocked = new List<Door>(); }  // a new job replaces the old record
            foreach (KeyValuePair<Door, FdkState> kv in was)
            {
                if (sFdkLocked.Contains(kv.Key) || kv.Value.Same(FdkOf(kv.Key))) continue;
                sFdkLocked.Add(kv.Key);
                sFdkBefore[kv.Key] = kv.Value;
            }
        }

        static void FdkCleanUp(FireFightingJob job, Dictionary<Door, FdkState> pre)
        {
            bool mine = sFdkJob == job, on = ShortcutOn("firedoorkeep"), large = job.Id == JobId.LargeFires;
            foreach (KeyValuePair<Door, FdkState> kv in pre)
            {
                Door d = kv.Key;
                if (d.HasBeenDestroyed) continue;
                FdkState before = null;
                if (mine) sFdkBefore.TryGetValue(d, out before);
                FdkState want = FdkCore.Restore(kv.Value, FdkOf(d), before, mine && sFdkLocked.Contains(d), d.IsNPCDoor);
                if (want == null) continue;
                if (!on) { PatchCounters()[2189]++; continue; }
                d.SetLockTypeAndOwner((CommonDoor.tLock)want.Type, want.Owner as SimDescription, want.OneWay);
                // EA's SetLockTypeOwnerList re-subscribes the household listener; the id list itself was never cleared
                if (want.Type == (int)CommonDoor.tLock.OwnerList) sFdkHhCallback.Invoke(d, null);
                if (want.Type == FdkState.Anybody) d.StopEffect();  // the job's "locked" axe effect, as EA's large-fire unlock
                PatchCounters()[large ? 2187 : 2188]++;
            }
            // the record stays until the next job or a world quit: a second CleanUpJob of this job decides the same way
        }

        // `this` is the FireFightingJob at run time (method swap).
        public class FdkJobHook
        {
            public void LockHook(int roomId, List<int> traversedRooms)
            {
                Dictionary<Door, FdkState> was = null;
                try { if (traversedRooms != null && traversedRooms.Count == 0) was = FdkRead((object)this as FireFightingJob); }  // top level only
                catch (Exception) { PatchCounters()[2190]++; }
                LockOrig(roomId, traversedRooms);
                if (was == null) return;
                try { FdkRecord((object)this as FireFightingJob, was); }
                catch (Exception) { PatchCounters()[2190]++; }
            }
            public void LockOrig(int roomId, List<int> traversedRooms) { }

            public void CleanHook()
            {
                FireFightingJob job = (object)this as FireFightingJob;
                Dictionary<Door, FdkState> pre = null;
                try { pre = FdkRead(job); }
                catch (Exception) { PatchCounters()[2190]++; }
                CleanOrig();
                if (pre == null) return;
                try { FdkCleanUp(job, pre); }
                catch (Exception) { PatchCounters()[2190]++; }
            }
            public void CleanOrig() { }
        }

        // Per firefighter job id: its task kinds with EA's count bounds (which jobs trap Sims and so lock doors).
        static string FdkJobs()
        {
            string r = "";
            foreach (JobId id in Enum.GetValues(typeof(JobId)))
            {
                JobStaticData data = Occupation.GetJobStaticData(OccupationNames.Firefighter, id);
                if (data == null) continue;
                string t = "";
                if (data.Tasks != null)
                    foreach (TaskCreationStaticData x in data.Tasks) t += (t.Length > 0 ? ", " : "") + x.Id + " " + x.LowerBound + "-" + x.UpperBound;
                r += " | " + id + ": " + (t.Length > 0 ? t : "no tasks");
            }
            return r.Length == 0 ? "no firefighter job data (Ambitions not installed?)" : r.Substring(3);
        }

        // FireDoorKeepCheck run: FdkCore.Restore for the reported layouts with controls; read-only.
        //   off BROKEN = EA's outcome in every case; on FIXED = (a) a household lock survives a large fire, (b) a job lock is
        //   undone after another job type, (c) a one-way door the job locked gets its room back, (d) a bare Nobody door from
        //   before the job stays locked. Controls (EA's outcome in both): an unchanged door, a door unlocked before and after,
        //   a door left in a state EA's job never sets, a bare Nobody door without a record, a job lock the firefighter broke.
        //   jobs    per firefighter job id: task kinds and count bounds; result  counters 2187-2190
        public static object FireDoorKeepCheck(string what)
        {
            int[] c = PatchCounters();
            string missing = FdkMissing();
            string counters = "firedoorkeep: large-fire locks given back 2187=" + c[2187] + ", job locks undone 2188=" + c[2188]
                + ", seen off 2189=" + c[2189] + ", errors 2190=" + c[2190] + "; record " + (sFdkJob == null ? "none" : sFdkJob.Id + ", doors locked by the job " + sFdkLocked.Count)
                + "; members missing: " + (missing.Length == 0 ? "none" : missing);
            if (what == "result") return counters;
            if (what == "jobs") return "firedoorkeep jobs: " + FdkJobs() + "; " + counters;
            if (what != "run") return "firedoorkeep <run|jobs|result>";
            bool on = ShortcutOn("firedoorkeep");
            object sim = new object();
            const int N = FdkState.Nobody, A = FdkState.Anybody, HH = (int)CommonDoor.tLock.SelectedHousehold, R = FdkState.NoRoom;
            FdkState bareN = new FdkState(N, null, R), bareA = new FdkState(A, null, R), hh = new FdkState(HH, sim, R), oneWay = new FdkState(A, null, 5);
            bool a = FdkAct(on, hh, bareA, null, false) == hh, b = FdkAct(on, bareN, bareN, bareA, true) == bareA;
            bool cc = FdkAct(on, bareN, bareA, oneWay, true) == oneWay, d = FdkAct(on, bareN, bareA, bareN, false) == bareN;
            int fixedN = (a ? 1 : 0) + (b ? 1 : 0) + (cc ? 1 : 0) + (d ? 1 : 0);
            bool controlsOk = FdkAct(on, hh, hh, hh, false) == null && FdkAct(on, bareA, bareA, bareA, false) == null
                && FdkAct(on, bareN, hh, bareA, true) == null && FdkAct(on, bareN, bareA, null, false) == null && FdkAct(on, bareA, bareA, bareA, true) == null;
            string v = !on ? (fixedN == 0 ? "BROKEN" : "UNEXPECTED") : (fixedN == 4 ? "FIXED" : "UNEXPECTED");
            string verdict = !controlsOk || missing.Length > 0 ? "UNEXPECTED" : v;
            return "firedoorkeep check (" + (on ? "on" : "off") + "): household lock kept after a large fire=" + a + ", job lock undone after another job=" + b
                + ", one-way room back=" + cc + ", old Nobody door kept=" + d + " (" + v + "); controls ok=" + controlsOk + "; verdict: " + verdict + "; " + counters;
        }

        // The hooks' outcome: EA's (null) with the switch off.
        static FdkState FdkAct(bool on, FdkState pre, FdkState cur, FdkState before, bool jobLocked)
        {
            return on ? FdkCore.Restore(pre, cur, before, jobLocked) : null;
        }
    }
}
