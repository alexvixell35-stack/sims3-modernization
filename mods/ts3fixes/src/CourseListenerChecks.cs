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
    // courselistener harness check (docs/555 §4). Throwaway objects; nothing in the world changes.
    public static partial class Fixes
    {
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
    }
}
