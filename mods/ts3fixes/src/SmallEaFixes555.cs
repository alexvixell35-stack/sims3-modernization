using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.SimIFace;
using Sims3.UI;

namespace Ts3Fixes
{
    // Three small EA bug fixes from the script logic audit (docs/555), each off by default and exact for every Sim.
    //
    // courselistener (tuning kCourseListener; docs/555 §4). AcademicCareer+CourseCreationSpec.SetRabbitHoleDisposedEventListener
    // adds a kEventObjectDisposed listener on the course's rabbit hole; EA removes it only when it fires [verified,
    // decompiled]. Leaving the university career (LeaveJobNow) and rebuilding the schedule (CreateCoursesSchedule) leave
    // every course's listener behind, holding the career and its Sim for the rest of the world session, and a later delete
    // of that building shows "course location destroyed" (or "forced to leave university") for a course the Sim no longer
    // has. A level-up moves generic courses to another rabbit hole (ChangeRabbitHoleTo) but the listener stays on the old
    // one, so deleting the class's new building never cancels it. The fix removes the listeners with EA's RemoveListener
    // when LeaveJobNow runs (its RemoveStudent step) and before CreateCoursesSchedule, and re-adds a moved course's listener on its new rabbit hole with EA's
    // own SetRabbitHoleDisposedEventListener. Counters [2420] listeners removed, [2421] listeners moved, [2422] courses
    // moved with a listener left on the old rabbit hole (counted with the switch off too), [2423] errors.
    //
    // observatorymsg (tuning kObservatoryMsg; docs/555 §6). StellarObservatory+TravelToSimulonV tests `num3 > 0 || num3 > 0`
    // (dark matter twice) before its summary notice, so a trip that found only stardust shows "found nothing" although the
    // stardust is in the inventory [verified, decompiled]. The fix: Sim.ShowTNSIfSelectable(TNSNames, actor, target,
    // params) turns SummaryOfNothingFoundOnSimulonV into SummaryOfEverythingFoundOnSimulonV when its stardust argument is
    // above 0 (the observatory is the only caller of that notice). Items, events and skill are untouched.
    // Counters [2424] notices corrected (counted with the switch off too), [2425] errors.
    //
    // bubblepipeguard (tuning kBubblePipeGuard; docs/555 §7). BubbleBar+BlowBubbles.Run calls SetHiddenFlags on the pipe
    // prop before its null check, so a prop that was not created throws a NullReferenceException instead of taking EA's
    // own exit (StandardExit, return false) [verified, decompiled]. The fix catches that one case (NRE, pipe still null,
    // state machine already entered: the only point between EnterStateMachine and the pipe's first use) and takes EA's
    // exit; anything else is rethrown. Counters [2426] clean exits, [2427] errors.
    public static partial class Fixes
    {
        static FieldInfo sClSchedule, sClListener;
        static Type sBbBlow;

        static void ApplySmallFixes555()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            sClSchedule = typeof(AcademicCareer).GetField("mCourseSchedule", inst);
            sClListener = typeof(AcademicCareer.CourseCreationSpec).GetField("mRabbitHoleDisposed", inst);
            if (sClSchedule == null || sClListener == null) AppliedLog().Add("courselistener: AcademicCareer members not found, not patched");
            else
            {
                Patch(typeof(AcademicCareer), "RemoveStudent", typeof(ClHook), "LeaveHook", "LeaveOrig");  // LeaveJobNow's own step (its only caller); LeaveJobNow itself is invoked by reflection in LeaveJobDispose.cs
                Patch(typeof(AcademicCareer), "CreateCoursesSchedule", typeof(ClHook), "ScheduleHook", "ScheduleOrig");
                Patch(typeof(AcademicCareer.CourseCreationSpec), "ChangeRabbitHoleTo", typeof(ClSpecHook), "MoveHook", "MoveOrig");
            }
            Patch(typeof(Sim), "ShowTNSIfSelectable", new Type[] { typeof(TNSNames), typeof(object), typeof(object), typeof(object[]) },
                typeof(ObsHook), "TnsHook", "TnsOrig");
            sBbBlow = typeof(BubbleBar).GetNestedType("BlowBubbles", BindingFlags.Public | BindingFlags.NonPublic);
            if (sBbBlow == null) AppliedLog().Add("bubblepipeguard: BubbleBar+BlowBubbles not found, not patched");
            else Patch(sBbBlow, "Run", typeof(BbHook), "RunHook", "RunOrig");
        }

        // ---------- courselistener ----------
        public class ClHook
        {
            public void LeaveHook()
            {
                ClDropAll((object)this as AcademicCareer);
                LeaveOrig();
            }
            public void LeaveOrig() { } // receives AcademicCareer.RemoveStudent's original body (called only from LeaveJobNow)

            public void ScheduleHook()
            {
                ClDropAll((object)this as AcademicCareer);
                ScheduleOrig();
            }
            public void ScheduleOrig() { } // receives AcademicCareer.CreateCoursesSchedule's original body
        }

        public class ClSpecHook
        {
            public void MoveHook(RabbitHole newRabbitHole)
            {
                AcademicCareer.CourseCreationSpec spec = (object)this as AcademicCareer.CourseCreationSpec;
                bool move = false;
                try { move = ClMoveBefore(spec, newRabbitHole); } catch (Exception) { PatchCounters()[2423]++; }
                MoveOrig(newRabbitHole);
                if (!move) return;
                try { spec.SetRabbitHoleDisposedEventListener(); PatchCounters()[2421]++; } catch (Exception) { PatchCounters()[2423]++; }
            }
            public void MoveOrig(RabbitHole newRabbitHole) { } // receives CourseCreationSpec.ChangeRabbitHoleTo's original body
        }

        // With the switch on: every scheduled course's listener removed (EA's RemoveListener), field cleared.
        static void ClDropAll(AcademicCareer career)
        {
            try
            {
                if (career == null || !ShortcutOn("courselistener")) return;
                List<AcademicCareer.CourseCreationSpec> list = sClSchedule.GetValue(career) as List<AcademicCareer.CourseCreationSpec>;
                if (list == null) return;
                foreach (AcademicCareer.CourseCreationSpec spec in list) ClDrop(spec);
            }
            catch (Exception) { PatchCounters()[2423]++; }
        }

        static bool ClDrop(AcademicCareer.CourseCreationSpec spec)
        {
            EventListener l = spec == null ? null : sClListener.GetValue(spec) as EventListener;
            if (l == null) return false;
            EventTracker.RemoveListener(l);
            sClListener.SetValue(spec, null);
            PatchCounters()[2420]++;
            return true;
        }

        // A live listener on another rabbit hole than the new one: counted; with the switch on removed (re-added after EA's move).
        static bool ClMoveBefore(AcademicCareer.CourseCreationSpec spec, RabbitHole to)
        {
            EventListener l = spec == null ? null : sClListener.GetValue(spec) as EventListener;
            if (l == null || to == null || ReferenceEquals(l.TargetObject, to)) return false;
            PatchCounters()[2422]++;
            if (!ShortcutOn("courselistener")) return false;
            EventTracker.RemoveListener(l);
            sClListener.SetValue(spec, null);
            return true;
        }

        // ---------- observatorymsg ----------
        public class ObsHook
        {
            public StyledNotification TnsHook(TNSNames notificationId, object actor, object target, params object[] parameters)
            {
                if (notificationId == TNSNames.SummaryOfNothingFoundOnSimulonV)
                {
                    try { notificationId = ObsPick(notificationId, parameters); } catch (Exception) { PatchCounters()[2425]++; }
                }
                return TnsOrig(notificationId, actor, target, parameters);
            }
            public StyledNotification TnsOrig(TNSNames notificationId, object actor, object target, params object[] parameters) { return null; } // receives the original body
        }

        // EA passes (actor, stardust, dark matter): stardust above 0 means something was found.
        static TNSNames ObsPick(TNSNames id, object[] p)
        {
            if (id != TNSNames.SummaryOfNothingFoundOnSimulonV || p == null || p.Length < 2 || !(p[1] is int) || (int)p[1] <= 0) return id;
            PatchCounters()[2424]++;
            return ShortcutOn("observatorymsg") ? TNSNames.SummaryOfEverythingFoundOnSimulonV : id;
        }

        // ---------- bubblepipeguard ----------
        public class BbHook
        {
            public bool RunHook()
            {
                try { return RunOrig(); }
                catch (NullReferenceException)
                {
                    if (!BbPipeFailed((object)this as InteractionInstance)) throw;
                    ((object)this as InteractionInstance).StandardExit();
                    return false;
                }
            }
            public bool RunOrig() { return false; } // receives BubbleBar+BlowBubbles.Run's original body
        }

        // The NRE came from the pipe's first use: state machine entered, pipe still null. Switch on only.
        static bool BbPipeFailed(InteractionInstance ia)
        {
            try
            {
                if (ia == null || !ShortcutOn("bubblepipeguard")) return false;
                FieldInfo pipe = ia.GetType().GetField("PipeUsed", BindingFlags.Instance | BindingFlags.Public);
                if (pipe == null || pipe.GetValue(ia) != null || ia.mCurrentStateMachine == null) return false;
                PatchCounters()[2426]++;
                return true;
            }
            catch (Exception) { PatchCounters()[2427]++; return false; }
        }

        // ---------- harness checks (throwaway objects; nothing in the world changes) ----------
        // CourseListenerCheck
        //   run     a throwaway course spec (no career) on a world rabbit hole: EA's SetRabbitHoleDisposedEventListener, then
        //           (a) EA's ChangeRabbitHoleTo another rabbit hole: off BROKEN = listener still on the old one; on FIXED =
        //           on the new one; (b) the leave/rebuild drop the hooks run: off BROKEN = listener still registered; on FIXED
        //           = removed (run through ClDropAll on a throwaway uninitialized career). The spec's listener is removed at the end in both arms.
        //   result  counters 2420-2423
        public static object CourseListenerCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "courselistener: removed 2420=" + c[2420] + ", moved 2421=" + c[2421] + ", stale moves 2422=" + c[2422] + ", errors 2423=" + c[2423];
            if (what == "result") return counters;
            if (what != "run") return "courselistener <run|result>";
            if (sClListener == null) return "courselistener check: not patched (N/A)";
            RabbitHole a = null, b = null;
            foreach (RabbitHole r in Sims3.Gameplay.Queries.GetObjects<RabbitHole>())
            {
                if (r == null || r.RabbitHoleProxy == null || r.RabbitHoleProxy.LotCurrent == null) continue;
                if (a == null) a = r; else { b = r; break; }
            }
            if (b == null) return "courselistener check: need two rabbit holes (N/A)";
            AcademicCareer.CourseCreationSpec spec = (AcademicCareer.CourseCreationSpec)FormatterServices.GetUninitializedObject(typeof(AcademicCareer.CourseCreationSpec));
            JobCreationSpec job = (JobCreationSpec)FormatterServices.GetUninitializedObject(typeof(JobCreationSpec));
            job.mRabbitHole = a;
            spec.mJobCreationSpec = job;
            bool on = ShortcutOn("courselistener");
            string moveVerdict, dropVerdict;
            try
            {
                spec.SetRabbitHoleDisposedEventListener();
                spec.ChangeRabbitHoleTo(b);
                EventListener l = sClListener.GetValue(spec) as EventListener;
                bool onNew = l != null && EventTracker.ContainsListener(l) && ReferenceEquals(l.TargetObject, b);
                moveVerdict = onNew ? "FIXED" : "BROKEN";
                // the hooks' own drop on a throwaway career (uninitialized, never registered) holding this course
                AcademicCareer career = (AcademicCareer)FormatterServices.GetUninitializedObject(typeof(AcademicCareer));
                List<AcademicCareer.CourseCreationSpec> list = new List<AcademicCareer.CourseCreationSpec>();
                list.Add(spec);
                sClSchedule.SetValue(career, list);
                ClDropAll(career);
                l = sClListener.GetValue(spec) as EventListener;
                dropVerdict = l == null || !EventTracker.ContainsListener(l) ? "FIXED" : "BROKEN";
            }
            finally
            {
                EventListener left = sClListener.GetValue(spec) as EventListener;
                if (left != null) EventTracker.RemoveListener(left);
            }
            string verdict = moveVerdict == dropVerdict ? moveVerdict : "UNEXPECTED";
            return "courselistener check (switch " + (on ? "on" : "off") + "): move " + moveVerdict + ", leave " + dropVerdict + "; verdict: " + verdict + "; " + counters;
        }

        // ObservatoryMsgCheck run: the notice choice for a stardust-only trip (3 stardust, 0 dark matter) and an empty trip.
        //   off BROKEN = "nothing found" for the stardust trip; on FIXED = "everything found"; the empty trip keeps "nothing".
        public static object ObservatoryMsgCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "observatorymsg: corrected 2424=" + c[2424] + ", errors 2425=" + c[2425];
            if (what == "result") return counters;
            if (what != "run") return "observatorymsg <run|result>";
            TNSNames dust = ObsPick(TNSNames.SummaryOfNothingFoundOnSimulonV, new object[] { null, 3, 0 });
            TNSNames none = ObsPick(TNSNames.SummaryOfNothingFoundOnSimulonV, new object[] { null, 0, 0 });
            bool fixedDust = dust == TNSNames.SummaryOfEverythingFoundOnSimulonV, emptyOk = none == TNSNames.SummaryOfNothingFoundOnSimulonV;
            string verdict = !emptyOk ? "UNEXPECTED" : fixedDust ? "FIXED" : "BROKEN";
            return "observatorymsg check (switch " + (ShortcutOn("observatorymsg") ? "on" : "off") + "): stardust-only " + dust + ", empty " + none + "; verdict: " + verdict + "; " + counters;
        }

        // BubblePipeCheck run: a throwaway BlowBubbles (uninitialized, never queued) in the failed-pipe state (state machine
        // set, pipe null). Off BROKEN = the guard declines (EA's NRE would escape Run); on FIXED = EA's exit taken; with a
        // pipe present the guard always declines (other NREs rethrown).
        public static object BubblePipeCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bubblepipeguard: exits 2426=" + c[2426] + ", errors 2427=" + c[2427];
            if (what == "result") return counters;
            if (what != "run") return "bubblepipe <run|result>";
            if (sBbBlow == null) return "bubblepipe check: not patched (N/A)";
            InteractionInstance ia = (InteractionInstance)FormatterServices.GetUninitializedObject(sBbBlow);
            StateMachineClient smc = (StateMachineClient)FormatterServices.GetUninitializedObject(typeof(StateMachineClient));
            GC.SuppressFinalize(smc);  // never had a driver: its finalizer must not run
            ia.mCurrentStateMachine = smc;
            bool failCase = BbPipeFailed(ia);
            ia.mCurrentStateMachine = null;
            bool beforeEntry = BbPipeFailed(ia);
            string verdict = beforeEntry ? "UNEXPECTED" : failCase ? "FIXED" : "BROKEN";
            return "bubblepipe check (switch " + (ShortcutOn("bubblepipeguard") ? "on" : "off") + "): pipe-failure exit " + failCase + ", NRE before the state machine handled " + beforeEntry + "; verdict: " + verdict + "; " + counters;
        }
    }
}
