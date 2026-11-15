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
    // courselistener: docs/555 script logic audit. Off by default, exact for every Sim. Harness check: CourseListenerChecks.cs.
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
    public static partial class Fixes
    {
        static FieldInfo sClSchedule, sClListener;

        static void ApplyCourseListener()
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
        }

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
    }
}
