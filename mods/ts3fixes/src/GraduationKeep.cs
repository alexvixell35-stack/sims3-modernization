using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.RabbitHoles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // hsgradkeep + unigradkeep (tunings kHsGradKeep / kUniGradKeep, off by default; docs/546 §12). EA bug fixes, every Sim:
    // they give the graduation EA's own fallback gives elsewhere; no Sim is moved and no route is skipped. A player cancel
    // keeps EA's outcome in both. [verified in the decompiled game code unless marked]
    //
    // hsgradkeep: high-school graduation is School.GraduateInCityHall. When it ends without graduating (route to City Hall
    // failed, ceremony never reached), its Cleanup sets the Sim's GraduationState back to None and, only for an autonomous
    // instance, queues GraduateInPlace (diploma, IGraduated buff, GraduationType). EA's force-graduation alarm (School
    // StartGraduationCallback, kHourToHoldGraduation 14:30) pushes an autonomous instance for every Sim still at None, but it
    // runs once. So a user-directed instance that fails after 14:30 (the player's own "Graduate" at City Hall, or the
    // household graduates the leading graduate queues with isAutonomous false) leaves the Sim graduating forever: no diploma,
    // no GraduationType. The fix queues EA's GraduateInPlace exactly as EA's autonomous branch does for that case: after
    // EA's Cleanup, not autonomous, not user-cancelled, still graduating, the alarm hour passed, no graduation queued.
    // Before 14:30 EA's alarm retries at City Hall, so nothing is done. Counter [2347] queued, [2348] errors.
    //
    // unigradkeep: university graduation is Annex.UniversityGraduationCeremony (pushed at 12:00, user-directed). Its Cleanup
    // re-adds the ceremony when mReaddOnClean is set and the player did not cancel, but nothing ever sets mReaddOnClean:
    // a failed route (or arriving after the ceremony) ends with no diploma, no IGraduatedFromUniversity, no bonus trait.
    // EA's only fallback is ReturnHomeBehaviorInHomeWorld (CollegeGraduation.GraduateInPlace for the active household's
    // LastStudiedDegree when its rewards were not granted), so NPC students, and a household student who studies another
    // degree first, lose it. The fix queues that same GraduateInPlace for the ceremony's degree after EA's Cleanup when the
    // degree is complete, its rewards were not granted and the player did not cancel. Re-adding the ceremony (EA's dead
    // branch) is not used: a route that just failed would usually fail again. Counter [2349] queued, [2350] errors.
    // Registry fixes-2345. Harness check: HsGradKeepCheck (EventChecks.cs); unigradkeep has none (needs a University world).
    public static partial class Fixes
    {
        static readonly FieldInfo sHgHoldHour = typeof(School).GetField("kHourToHoldGraduation", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly FieldInfo sUgDegree = typeof(Annex.UniversityGraduationCeremony).GetField("mDegree", kInstAll);
        static readonly FieldInfo sUgInPlaceDegree = typeof(CollegeGraduation.GraduateInPlace).GetField("mDegree", kInstAll);

        static void ApplyGraduationKeep()
        {
            if (sHgHoldHour == null) AppliedLog().Add("School.kHourToHoldGraduation: not found, hsgradkeep skipped");
            else Patch(typeof(School.GraduateInCityHall), "Cleanup", typeof(HsGradKeepHook), "CleanupHook", "CleanupOrig");
            if (sUgDegree == null || sUgInPlaceDegree == null) AppliedLog().Add("university graduation degree fields: not found, unigradkeep skipped");
            else Patch(typeof(Annex.UniversityGraduationCeremony), "Cleanup", typeof(UniGradKeepHook), "CleanupHook", "CleanupOrig");
        }

        public class HsGradKeepHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                HsGradInPlace((InteractionInstance)(object)this, false);
            }
            public void CleanupOrig() { } // receives School.GraduateInCityHall.Cleanup's original body
        }

        public class UniGradKeepHook
        {
            public void CleanupHook()
            {
                CleanupOrig();
                UniGradInPlace((InteractionInstance)(object)this);
            }
            public void CleanupOrig() { } // receives Annex.UniversityGraduationCeremony.Cleanup's original body
        }

        // ---- shared ----

        // The Sim of an ended graduation interaction that may still get its graduation: alive, with a queue, and the player
        // did not cancel it (EA's own test in both Cleanups: CancelledReason has UserCanceled). Null otherwise.
        static Sim GradActorToKeep(InteractionInstance ii)
        {
            Sim a = ii.InstanceActor;
            if (a == null || a.HasBeenDestroyed || a.InteractionQueue == null || a.SimDescription == null) return null;
            return (ii.CancelledReason & ExitReason.UserCanceled) != 0 ? null : a;
        }

        // The game's queue refused the fallback (InteractionQueue.InsertInteraction runs entry.Test()): logged once per Sim
        // and kind with the definition's InteractionTestResult, so a refusal shows up in the applied log instead of a
        // counter that claims the diploma was kept.
        static readonly List<string> sGradRefused = new List<string>();
        static void GradRefused(string kind, InteractionInstance inPlace)
        {
            Sim a = inPlace.InstanceActor;
            string key = kind + " " + a.SimDescription.SimDescriptionId;
            if (sGradRefused.Contains(key) || sGradRefused.Count >= 20) return;
            sGradRefused.Add(key);
            AppliedLog().Add("gradkeep: the queue refused the " + kind + " GraduateInPlace for " + a.FullName + " (" + GradTestResult(inPlace) + ")");
        }

        // Why an interaction instance fails the queue's Test: its definition's InteractionTestResult (Pass when it passes).
        static string GradTestResult(InteractionInstance ii)
        {
            try
            {
                InteractionInstanceParameters p = ii.GetInteractionParameters();
                GreyedOutTooltipCallback tip = null;
                return ii.InteractionDefinition.Test(ref p, ref tip).ToString();
            }
            catch (Exception e) { return "test threw " + e.GetType().Name; }
        }

        // ---- hsgradkeep ----

        // anyHour: the harness check's way past the 14:30 gate (EA's alarm cannot fire inside a synchronous check)
        static void HsGradInPlace(InteractionInstance gi, bool anyHour)
        {
            if (!ShortcutOn("hsgradkeep")) return;
            try
            {
                Sim a = GradActorToKeep(gi);
                if (a == null || gi.Autonomous || !School.IsSimGraduating(a.SimDescription)) return;  // autonomous: EA queued it
                if (!anyHour && SimClock.HoursPassedOfDay < (float)sHgHoldHour.GetValue(null)) return;  // EA's alarm still retries
                InteractionQueue q = a.InteractionQueue;
                if (q.HasInteractionOfType(School.GraduateInCityHall.Singleton) || q.HasInteractionOfType(School.GraduateInPlace.Singleton)) return;
                // as EA's autonomous branch (GraduateInPlace on the actor, the same priority, not cancellable, MustRun), but with
                // the failed instance's own autonomy: an autonomous copy for a player-directed graduation can fail the queue's Test
                InteractionInstance inPlace = School.GraduateInPlace.Singleton.CreateInstance(a, a, gi.GetPriority(), gi.Autonomous, false);
                inPlace.MustRun = true;
                if (q.Add(inPlace)) PatchCounters()[2347]++;
                else GradRefused("high school", inPlace);
            }
            catch (Exception) { PatchCounters()[2348]++; }
        }

        // ---- unigradkeep ----

        static void UniGradInPlace(InteractionInstance ceremony)
        {
            if (!ShortcutOn("unigradkeep")) return;
            try
            {
                Sim a = GradActorToKeep(ceremony);
                AcademicDegree degree = sUgDegree.GetValue(ceremony) as AcademicDegree;
                if (a == null || degree == null || !degree.IsDegreeCompleted || degree.IsRewardsGranted) return;
                InteractionQueue q = a.InteractionQueue;
                if (q.HasInteractionOfType(Annex.UniversityGraduationCeremony.Singleton) || q.HasInteractionOfType(typeof(CollegeGraduation.GraduateInPlace))) return;
                // as EA's CollegeGraduation.GraduateSim without a graduation rabbit hole: High, not autonomous, not cancellable, AddNext
                InteractionInstance inPlace = CollegeGraduation.GraduateInPlace.Singleton.CreateInstance(a, a, new InteractionPriority(InteractionPriorityLevel.High), false, false);
                sUgInPlaceDegree.SetValue(inPlace, degree);  // the ceremony's degree, not whatever LastStudiedDegree is by then
                if (q.AddNext(inPlace)) PatchCounters()[2349]++;
                else GradRefused("university", inPlace);
            }
            catch (Exception) { PatchCounters()[2350]++; }
        }
    }
}
