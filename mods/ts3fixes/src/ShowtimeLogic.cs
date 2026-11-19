using System;
using System.Collections.Generic;
using Sims3.Gameplay.ActiveCareer.ActiveCareers;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Showtime logic fixes (docs/571 §9-11), each off by default.
    //
    // steadygigmiss (kSteadyGigMiss, docs/571 §9) [verified, PerformanceCareer.cs / Gig.cs / Job.cs / EventTracker.cs]:
    //   SetAttributesForNewJob registers PerformanceCareer.OnJobMissed for kOccupationJobMissed with no actor filter.
    //   OnJobMissed counts a miss on the performer's steady gig of today, and two misses cancel it. The event is sent for
    //   ANY Sim's missed job (Gig/Job.OnJobTimeout, AcademicCourse), so every performer with a steady gig today is charged
    //   for other Sims' missed gigs, jobs and classes and can lose a steady gig they performed. The listener is also not
    //   registered again after a load (EventTracker starts empty each world), so in a loaded save missed steady gigs are
    //   never counted. Fix: OnJobMissed counts only a missed job of the same career; at world load each performance
    //   career's listener is registered again (once per world). Counters [2597] other Sims' misses ignored,
    //   [2598] listeners registered at load, [2599] errors.
    // npcshowstuck (kNpcShowStuck, docs/571 §10) [verified, NPCShowManager.cs / PerformanceCareer.cs]: the daily 1:00
    //   CreateNPCShow returns at once while the [PersistableStatic] isNPCShowScheduled is true; only ForceStartGig clears
    //   it, and that returns early when the NPC's gig is gone (a household performer booked the same venue and day,
    //   which removes the NPC gig; the gig was cancelled; the townie was culled). The flag then stays true for the rest of
    //   the save: no NPC shows and no NPC tip performers ever again, and the newspaper / map tag keep the old show.
    //   Fix: at 1:00, a flag whose show started more than 12 sim hours ago (or has no time) is cleared with EA's
    //   ClearShowTags before EA's code runs. (The booked NPC's disabled autonomy is not touched: docs/571 §10.)
    //   Counters [2600] stale flags cleared, [2601] errors.
    // gigpullaudience (kGigPullAudience, docs/571 §11) [verified, Gig.cs / PerformanceCareer.cs / Occupation.cs]: the Gig
    //   constructor adds the always-persisted "PullAudience" alarm (one hour before start) and keeps no handle; ShutDown
    //   does not remove it. A gig removed or never scheduled (a booking that replaced an NPC gig, a cancelled steady gig,
    //   quitting the career, CreateGigFromSpec failing after the constructor ran) still resets the venue's show floors,
    //   spawns crowd monsters and routes NPC audiences to a venue with no show. Fix: PrepareVenueForShow does nothing for
    //   a gig that is not among its career's jobs, or whose career its Sim no longer has. Counters [2602] stale pulls
    //   skipped, [2603] errors.
    // Checks: ShowtimeLogicChecks.cs.
    public static partial class Fixes
    {
        static readonly List<PerformanceCareer> sSgRegistered = new List<PerformanceCareer>();
        static bool sSgHooked;

        static void ApplyShowtimeLogic()
        {
            Patch(typeof(PerformanceCareer), "OnJobMissed", typeof(SteadyGigHook), "MissedHook", "MissedOrig");
            Patch(typeof(NPCShowManager), "CreateNPCShow", typeof(NpcShowHook), "CreateHook", "CreateOrig");
            if (sFtcSpec == null) AppliedLog().Add("gigpullaudience: Job.Specification not found, not patched");
            else Patch(typeof(Gig), "PrepareVenueForShow", typeof(GigPullHook), "PullHook", "PullOrig");
            if (!sSgHooked)
            {
                sSgHooked = true;
                World.OnWorldQuitEventHandler += delegate { sSgRegistered.Clear(); };
            }
        }

        // ---- steadygigmiss ----
        // Count the miss for this career? EA: always. Fixed: only a missed job of this same career.
        internal static bool SgCount(bool on, bool jobOfThisCareer)
        {
            return !on || jobOfThisCareer;
        }

        public class SteadyGigHook
        {
            public ListenerAction MissedHook(Sims3.Gameplay.EventSystem.Event e)
            {
                try
                {
                    if (ShortcutOn("steadygigmiss"))
                    {
                        OccupationJobEvent oe = e as OccupationJobEvent;
                        Sims3.Gameplay.Careers.Job j = oe == null ? null : oe.Job;
                        bool mine = j != null && sFtcSpec != null && sFtcSpec.GetValue(j) is JobCreationSpec && ((JobCreationSpec)sFtcSpec.GetValue(j)).mOccupation == (object)this;
                        if (!SgCount(true, mine)) { PatchCounters()[2597]++; return ListenerAction.Keep; }
                    }
                }
                catch (Exception) { PatchCounters()[2599]++; }
                return MissedOrig(e);
            }
            public ListenerAction MissedOrig(Sims3.Gameplay.EventSystem.Event e) { return ListenerAction.Keep; }
        }

        // World load finished (Instantiator): register each performance career's missed-job listener, as
        // SetAttributesForNewJob did when the career began (EventTracker is empty after a load).
        public static void SteadyGigMissWorldLoad()
        {
            try
            {
                if (!ShortcutOn("steadygigmiss")) return;
                foreach (SimDescription d in Household.EveryHumanSimDescription())
                {
                    PerformanceCareer pc = d == null ? null : d.Occupation as PerformanceCareer;
                    if (pc == null || sSgRegistered.Contains(pc)) continue;
                    EventTracker.AddListener(EventTypeId.kOccupationJobMissed, pc.OnJobMissed);
                    sSgRegistered.Add(pc);
                    PatchCounters()[2598]++;
                }
            }
            catch (Exception) { PatchCounters()[2599]++; }
        }

        // ---- npcshowstuck ----
        internal static bool NsStale(bool on, bool flag, bool hasTime, float hoursSinceStart)
        {
            return on && flag && (!hasTime || hoursSinceStart > 12f);
        }

        public class NpcShowHook
        {
            public static void CreateHook()
            {
                try
                {
                    if (ShortcutOn("npcshowstuck") && NPCShowManager.isNPCShowScheduled)
                    {
                        DateAndTime t = NPCShowManager.DataTime;
                        bool hasTime = t != DateAndTime.Invalid;
                        if (NsStale(true, true, hasTime, hasTime ? SimClock.ElapsedTime(TimeUnit.Hours, t) : 0f))
                        {
                            NPCShowManager.isNPCShowScheduled = false;
                            NPCShowManager.ClearShowTags();
                            PatchCounters()[2600]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[2601]++; }
                // EA's body throws now and then (docs/651 §1); uncaught, AlarmManager drops the daily alarm for good
                try { CreateOrig(); }
                catch (ResetException) { throw; }
                catch (Exception e) { EaAlarmThrew("NPCShowManager.CreateNPCShow", 4110, e); }
            }
            public static void CreateOrig() { }
        }

        // ---- gigpullaudience ----
        internal static bool GpSkip(bool on, bool live)
        {
            return on && !live;
        }

        // Is the gig still one of its career's jobs, held by the career's Sim?
        internal static bool GpLive(Sims3.Gameplay.Careers.Job gig)
        {
            JobCreationSpec spec = gig == null ? null : sFtcSpec.GetValue(gig) as JobCreationSpec;
            Occupation occ = spec == null ? null : spec.mOccupation;
            if (occ == null || occ.OwnerDescription == null || occ.OwnerDescription.Occupation != occ || occ.Jobs == null) return false;
            foreach (Sims3.Gameplay.Careers.Job j in occ.Jobs) if (j == gig) return true;
            return false;
        }

        public class GigPullHook
        {
            public void PullHook()
            {
                try
                {
                    if (ShortcutOn("gigpullaudience") && GpSkip(true, GpLive((object)this as Sims3.Gameplay.Careers.Job))) { PatchCounters()[2602]++; return; }
                }
                catch (Exception) { PatchCounters()[2603]++; }
                PullOrig();
            }
            public void PullOrig() { }
        }
    }
}
