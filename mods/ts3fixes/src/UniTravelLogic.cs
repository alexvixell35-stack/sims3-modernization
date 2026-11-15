using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Utilities;
using Sims3.Gameplay.Visa;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // University Life travel fixes around the phone (docs/573 §7-9), each off by default. EA bug fixes; no Sim is moved.
    //
    // resumetns (kResumeTns, docs/573 §7) [verified, ReturnHomeBehaviorInHomeWorld.TriggerDegreeUpdates / Career
    //   .ShowUploadResumeTns]: on the return home a Sim whose degree finished without a ceremony gets
    //   CollegeGraduation.GraduateInPlace queued, but EA shows the smartphone "upload your resume" notice first, and that
    //   notice needs the IGraduatedFromUniversity buff GraduateInPlace only adds later, so it never appears for them (the
    //   ceremony graduates, whose buff is already there, get it). Fix: after GraduateInPlace granted the rewards outside
    //   the University world, EA's ShowUploadResumeTns runs for that Sim. Counters [2648] notices shown, [2649] errors.
    // unitravelflag (kUniTravelFlag, docs/573 §8) [verified, AcademicCareer.EnrollInAcademicCareer / TravelUtil
    //   .TriggerTravelToUniversityWorld; trigger rare, inferred]: enrolling (phone, computer, school) sets
    //   TravelUtil.PlayerMadeTravelRequest before travel is set up; only the moving van's Run/Cleanup clears it. When
    //   TriggerTravelToUniversityWorld queues no van (no traveller had a degree manager, no lot, the queue refused the
    //   van, or it threw on a traveller that is not instantiated) the flag stays set: saving is refused and age-up,
    //   build/buy and all travel are blocked until the next world load. Fix: after that method, a set flag with no van in
    //   any traveller's queue is cleared (and also when it threw; the exception goes on). Counters [2650] flags cleared,
    //   [2651] errors.
    // returnhomealarms (kReturnHomeAlarms, docs/573 §9) [verified, ReturnHomeBehaviorInHomeWorld; trigger rare]: the
    //   return-home alarm handles are plain statics reset only when their one-minute alarm fires; quitting in that minute
    //   leaves them set into the next world (they survive a world quit), and the next return home in that session skips
    //   both alarms, so the travellers stay out of the world with no arrival, motive reset or degree update. Fix: at world
    //   quit both handles are reset (the alarms themselves end with the world). Counters [2652] handles reset, [2653] errors.
    // Checks: UniTravelLogicChecks.cs.
    public static partial class Fixes
    {
        const BindingFlags kUtAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static readonly FieldInfo sRtDegree = typeof(CollegeGraduation.GraduateInPlace).GetField("mDegree", kUtAll);
        static readonly FieldInfo sRhRide = typeof(ReturnHomeBehaviorInHomeWorld).GetField("mRideInCarAlarm", kUtAll);
        static readonly FieldInfo sRhDegree = typeof(ReturnHomeBehaviorInHomeWorld).GetField("mDegreeUpdatesAlarm", kUtAll);
        static readonly MethodInfo sUtTrigger = typeof(TravelUtil).GetMethod("TriggerTravelToUniversityWorld", kUtAll, null, new Type[] { typeof(List<SimDescription>), typeof(int) }, null);
        static bool sUtHooked;

        internal static string UtMissing()
        {
            string m = "";
            if (sRtDegree == null) m += "CollegeGraduation.GraduateInPlace.mDegree ";
            if (sRhRide == null) m += "ReturnHomeBehaviorInHomeWorld.mRideInCarAlarm ";
            if (sRhDegree == null) m += "ReturnHomeBehaviorInHomeWorld.mDegreeUpdatesAlarm ";
            if (sUtTrigger == null) m += "TravelUtil.TriggerTravelToUniversityWorld(List,int) ";
            return m.Trim();
        }

        static void ApplyUniTravelLogic()
        {
            if (sRtDegree != null) Patch(typeof(CollegeGraduation.GraduateInPlace), "Run", typeof(ResumeTnsHook), "RunHook", "RunOrig");
            if (sUtTrigger != null)
                Patch(typeof(TravelUtil), "TriggerTravelToUniversityWorld", new Type[] { typeof(List<SimDescription>), typeof(int) }, typeof(UniTravelFlagHook), "TriggerHook", "TriggerOrig");
            if (!sUtHooked)
            {
                sUtHooked = true;
                World.OnWorldQuitEventHandler += delegate { ReturnHomeAlarmsQuit(); };
            }
            if (UtMissing().Length > 0) AppliedLog().Add("uni travel logic: members not found (" + UtMissing() + ")");
        }

        // ---- resumetns ----
        internal static bool RtShow(bool on, bool universityWorld, bool grantedBefore, bool grantedAfter)
        {
            return on && !universityWorld && !grantedBefore && grantedAfter;
        }

        public class ResumeTnsHook
        {
            public bool RunHook()
            {
                bool before = true;
                AcademicDegree deg = null;
                try
                {
                    if (ShortcutOn("resumetns"))
                    {
                        deg = sRtDegree.GetValue((object)this) as AcademicDegree;
                        if (deg != null) before = deg.IsRewardsGranted;
                    }
                }
                catch (Exception) { PatchCounters()[2649]++; }
                bool r = RunOrig();
                try
                {
                    if (deg != null && RtShow(true, GameUtils.IsUniversityWorld(), before, deg.IsRewardsGranted))
                    {
                        Career.ShowUploadResumeTns(((CollegeGraduation.GraduateInPlace)(object)this).Actor);
                        PatchCounters()[2648]++;
                    }
                }
                catch (Exception) { PatchCounters()[2649]++; }
                return r;
            }
            public bool RunOrig() { return false; }
        }

        // ---- unitravelflag ----
        internal static bool UtClear(bool on, bool flagBefore, bool flagAfter, bool vanQueued)
        {
            return on && flagBefore && flagAfter && !vanQueued;
        }

        static bool UtVanQueued(List<SimDescription> travelers)
        {
            if (travelers == null) return false;
            foreach (SimDescription d in travelers)
            {
                Sim s = d == null ? null : d.CreatedSim;
                if (s != null && s.InteractionQueue != null && s.InteractionQueue.HasInteractionOfType(TravelUtil.SitInMovingVanToTriggerTravelToUniversity.Singleton)) return true;
            }
            return false;
        }

        public class UniTravelFlagHook
        {
            public static void TriggerHook(List<SimDescription> travelers, int tuitionCost)
            {
                bool on = ShortcutOn("unitravelflag"), before = TravelUtil.PlayerMadeTravelRequest;
                try { TriggerOrig(travelers, tuitionCost); }
                finally
                {
                    try
                    {
                        if (on && UtClear(true, before, TravelUtil.PlayerMadeTravelRequest, UtVanQueued(travelers)))
                        {
                            TravelUtil.PlayerMadeTravelRequest = false;
                            PatchCounters()[2650]++;
                        }
                    }
                    catch (Exception) { PatchCounters()[2651]++; }
                }
            }
            public static void TriggerOrig(List<SimDescription> travelers, int tuitionCost) { }
        }

        // ---- returnhomealarms ----
        internal static bool RhReset(bool on, bool handleSet)
        {
            return on && handleSet;
        }

        static void ReturnHomeAlarmsQuit()
        {
            try
            {
                if (!ShortcutOn("returnhomealarms") || sRhRide == null || sRhDegree == null) return;
                foreach (FieldInfo f in new FieldInfo[] { sRhRide, sRhDegree })
                {
                    if (RhReset(true, (AlarmHandle)f.GetValue(null) != AlarmHandle.kInvalidHandle))
                    {
                        f.SetValue(null, AlarmHandle.kInvalidHandle);
                        PatchCounters()[2652]++;
                    }
                }
            }
            catch (Exception) { PatchCounters()[2653]++; }
        }
    }
}
