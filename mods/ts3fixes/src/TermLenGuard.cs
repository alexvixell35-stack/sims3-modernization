using System;
using System.Reflection;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;

namespace Ts3Fixes
{
    // termlenguard (docs/692 §2, EA bug fix, switch off by default; every Sim, it is a crash guard). [verified decompiled]
    // Three EA sites scale academic performance by
    //   AcademicCourse.kCourseLoadMultipliersToAcademicPerformance[NumberOfCoursesPerDay - 1] (4 entries) and
    //   AcademicCourse.kTermLengthMultipliersToAcademicPerformance[(uint)(AcademicCareer.sTermLengthEx - 1)] (2 entries)
    // with no bounds check: Motive.UpdateMotiveWithDecay (the AcademicPerformance motive while the Sim is in an
    // AcademicSituationBase), AcademicCourse.AdjustAcademicPerformanceForTest and
    // AcademicCourse.GiveAcademicPerformanceAsIfSimDidThisInteraction. The global term length is kInvalid (0) until an
    // enrolment sets it (AcademicCareer.ClearStatics resets it; a new game started from the Sims University world file has
    // students with an AcademicCareer and no enrolment), and a three- or four-week term (TermLength has them) indexes past
    // the two-entry table: IndexOutOfRangeException. In Sim.Simulate that ends the Sim's update and the Sim is reset (EA's
    // handler, or NRaas ErrorTrap's ResetSimTask: a full re-instantiation, a new wish manager with every tree activated and
    // a new lifetime wish, a safe-location search). Soaks s570-s612 (SU): one reset per class change and household student,
    // 0.3-1.8 s frames each, the biggest script-CPU hitches of SU (docs/692 §1). EA's own guarded copy of the same product
    // (AcademicCourse.cs, the class-tone multiplier) skips a factor whose index is out of range, i.e. multiplies by 1;
    // NRaas Career sets kOneWeek at its own enrolment only. The guard: when an index is out of range, for the duration of
    // EA's call the table is a one-entry {1} array and the index source points at entry 0 (term length kOneWeek,
    // NumberOfCoursesPerDay 1), both restored after the call (finally). Valid indices: EA's call unchanged.
    // Counters: [4904] decay updates guarded, [4905] test adjustments guarded, [4906] skipped-class grants guarded,
    // [4907] would guard (switch off: EA throws), [4908] errors.
    public static partial class Fixes
    {
        static readonly int kSwTermLenGuard = SwSlot("termlenguard");
        static readonly float[] sTlOne = { 1f };
        static FieldInfo sTlCourses;  // AcademicCareer.mNumberOfCoursesPerDay
        static int sTlDepth;
        static PropertyInfo sTlActor;  // Job.Actor (protected)
        static bool sTlActorRead;
        static Sim TlActor(object course)
        {
            try
            {
                if (!sTlActorRead) { sTlActorRead = true; sTlActor = typeof(Sims3.Gameplay.Careers.Job).GetProperty("Actor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); }
                return sTlActor == null ? null : sTlActor.GetValue(course, null) as Sim;
            }
            catch (Exception) { PatchCounters()[4908]++; return null; }
        }

        static void ApplyTermLenGuard()
        {
            sTlCourses = typeof(AcademicCareer).GetField("mNumberOfCoursesPerDay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (sTlCourses == null || sTlCourses.FieldType != typeof(uint)) { sTlCourses = null; AppliedLog().Add("termlenguard: AcademicCareer.mNumberOfCoursesPerDay not found, course-load guard off"); }
            Patch(typeof(AcademicCourse), "AdjustAcademicPerformanceForTest", new Type[] { typeof(bool) }, typeof(TlCourseHook), "TestHook", "TestOrig");
            Patch(typeof(AcademicCourse), "GiveAcademicPerformanceAsIfSimDidThisInteraction", new Type[] { typeof(Type) }, typeof(TlCourseHook), "GiveHook", "GiveOrig");
            // the decay site is in BMmHook.DecayHook (Batch589Logic.cs): shared hook, one patch per method
        }

        // the saved state of one guarded call (sim thread only; a nested guarded call leaves the outer one's state alone)
        static float[] sTlTermArr, sTlCourseArr;
        static AcademicCareer.TermLength sTlTerm;
        static AcademicCareer sTlCareer;
        static uint sTlN;
        static bool sTlTermSet, sTlCourseSet;

        static bool TlTermBad()
        {
            float[] a = AcademicCourse.kTermLengthMultipliersToAcademicPerformance;
            return a != null && TermLenGuardCore.IndexBad((uint)AcademicCareer.sTermLengthEx, a.Length);
        }
        static bool TlCourseBad(AcademicCareer car)
        {
            float[] a = AcademicCourse.kCourseLoadMultipliersToAcademicPerformance;
            return car != null && a != null && TermLenGuardCore.IndexBad(car.NumberOfCoursesPerDay, a.Length);
        }

        // true: the caller runs EA's method between TlBegin and TlEnd (finally); false: run it plainly (indices valid, the
        // switch off, or a nested call)
        static bool TlBegin(Sim sim, int counter)
        {
            try
            {
                if (sTlDepth > 0) return false;
                AcademicCareer car = sim == null ? null : sim.OccupationAsAcademicCareer;
                bool term = TlTermBad(), course = TlCourseBad(car) && sTlCourses != null;
                if (!term && !course) return false;
                int[] c = PatchCounters();
                if (!SwOn(kSwTermLenGuard)) { c[4907]++; return false; }
                sTlTermSet = term; sTlCourseSet = course;
                if (term)
                {
                    sTlTerm = AcademicCareer.sTermLengthEx; sTlTermArr = AcademicCourse.kTermLengthMultipliersToAcademicPerformance;
                    AcademicCourse.kTermLengthMultipliersToAcademicPerformance = sTlOne; AcademicCareer.sTermLengthEx = AcademicCareer.TermLength.kOneWeek;
                }
                if (course)
                {
                    sTlCareer = car; sTlN = car.NumberOfCoursesPerDay; sTlCourseArr = AcademicCourse.kCourseLoadMultipliersToAcademicPerformance;
                    AcademicCourse.kCourseLoadMultipliersToAcademicPerformance = sTlOne; sTlCourses.SetValue(car, 1u);
                }
                sTlDepth = 1;
                c[counter]++;
                return true;
            }
            catch (Exception) { PatchCounters()[4908]++; TlEnd(); return false; }
        }

        static void TlEnd()
        {
            try
            {
                if (sTlTermSet) { AcademicCourse.kTermLengthMultipliersToAcademicPerformance = sTlTermArr; AcademicCareer.sTermLengthEx = sTlTerm; }
                if (sTlCourseSet) { AcademicCourse.kCourseLoadMultipliersToAcademicPerformance = sTlCourseArr; sTlCourses.SetValue(sTlCareer, sTlN); }
            }
            catch (Exception) { PatchCounters()[4908]++; }
            sTlTermSet = sTlCourseSet = false; sTlTermArr = sTlCourseArr = null; sTlCareer = null; sTlDepth = 0;
        }

        public class TlCourseHook
        {
            public void TestHook(bool passedTest)
            {
                Sim a = TlActor(this);
                if (!TlBegin(a, 4905)) { TestOrig(passedTest); return; }
                try { TestOrig(passedTest); }
                finally { TlEnd(); }
            }
            public void TestOrig(bool passedTest) { }

            public void GiveHook(Type definitionType)
            {
                Sim a = TlActor(this);
                if (!TlBegin(a, 4906)) { GiveOrig(definitionType); return; }
                try { GiveOrig(definitionType); }
                finally { TlEnd(); }
            }
            public void GiveOrig(Type definitionType) { }
        }
    }
}

namespace Ts3Fixes
{
    public static partial class Fixes
    {
        // TermLenGuardCheck 'run' (repro termlenguard-invalid-term): on a Sim with an AcademicCareer, a throwaway course
        // (uninitialized AcademicCourse whose job spec names that career) runs EA's AdjustAcademicPerformanceForTest(true)
        // with the global term length set to kInvalid, as in a world-file Sims University game; the Sim's academic
        // performance and the term length are put back after. Off: EA throws IndexOutOfRange (BROKEN). On: no throw, the
        // motive moved by EA's passing boost x the course-load factor x 1 (FIXED). 'result' prints the line.
        static string sTlCheckLine = "termlenguard check: not run";

        public static object TermLenGuardCheck(string what)
        {
            string counters = B582Counters("termlenguard", 4904, new[] { "decay updates guarded", "test adjustments guarded", "skipped-class grants guarded", "would guard (off)", "errors" }, "");
            if (what == "result") return sTlCheckLine + "; " + counters;
            if (what != "run") return "termlenguard <run|result>";
            bool on = SwOn(kSwTermLenGuard);
            Sim sim = null;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                if (s != null && s.OccupationAsAcademicCareer != null && s.Motives != null && s.Motives.HasMotive(Sims3.Gameplay.Autonomy.CommodityKind.AcademicPerformance)) { sim = s; break; }
            if (sim == null) return sTlCheckLine = "termlenguard check: no Sim with an AcademicCareer and the AcademicPerformance motive (needs a Sims University game)";
            AcademicCareer.TermLength saved = AcademicCareer.sTermLengthEx;
            float before = sim.Motives.GetValue(Sims3.Gameplay.Autonomy.CommodityKind.AcademicPerformance), after = before;
            string threw = "no";
            try
            {
                AcademicCourse course = (AcademicCourse)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AcademicCourse));
                FieldInfo specFi = typeof(Sims3.Gameplay.Careers.Job).GetField("Specification", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Sims3.Gameplay.Careers.JobCreationSpec spec = (Sims3.Gameplay.Careers.JobCreationSpec)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Sims3.Gameplay.Careers.JobCreationSpec));
                spec.mOccupation = sim.OccupationAsAcademicCareer;
                specFi.SetValue(course, spec);
                MethodInfo adj = typeof(AcademicCourse).GetMethod("AdjustAcademicPerformanceForTest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(bool) }, null);
                AcademicCareer.sTermLengthEx = AcademicCareer.TermLength.kInvalid;
                // a delegate, not MethodInfo.Invoke: Invoke on a swapped method throws TargetException (tools/selftest/swapinvoke)
                Action<bool> call = (Action<bool>)Fixes.Bind(typeof(Action<bool>), course, adj);
                try { call(true); }
                catch (IndexOutOfRangeException e) { threw = e.GetType().Name; }
                after = sim.Motives.GetValue(Sims3.Gameplay.Autonomy.CommodityKind.AcademicPerformance);
            }
            catch (Exception e) { threw = "check error " + e.GetType().Name; }
            finally
            {
                AcademicCareer.sTermLengthEx = saved;
                try { sim.Motives.SetValue(Sims3.Gameplay.Autonomy.CommodityKind.AcademicPerformance, before); } catch (Exception) { }
            }
            bool fixedNow = threw == "no" && after != before;
            bool restored = AcademicCareer.sTermLengthEx == saved && AcademicCourse.kTermLengthMultipliersToAcademicPerformance != sTlOne && AcademicCourse.kCourseLoadMultipliersToAcademicPerformance != sTlOne;
            string ctl = "Sim " + sim.FullName + ", courses a day " + sim.OccupationAsAcademicCareer.NumberOfCoursesPerDay + ", term length kInvalid for the call (was " + saved + "), threw " + threw
                + ", academic performance " + before.ToString("0.0") + " -> " + after.ToString("0.0") + " (put back), tables and term length restored " + restored;
            sTlCheckLine = B582Line("termlenguard", on, "EA's test adjustment with term length kInvalid runs and scales by 1", fixedNow, ctl, restored && (on || threw == "IndexOutOfRangeException"), "", counters);
            return sTlCheckLine;
        }
    }
}
