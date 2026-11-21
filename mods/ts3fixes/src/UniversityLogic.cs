using System;
using System.Reflection;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // University Life logic fixes (docs/572 §4-5), each off by default.
    //
    // professorterm (kProfessorTerm, docs/572 §4) [verified, Household.cs OneDayPassedUniversityUpdates /
    //   AcademicCareer.cs]: on the last day of a university trip EA calls OccupationAsAcademicCareer.OnTermCompleted for
    //   every Sim of every household - professors included (AcademicCareer with IsProfessor, Level 2). OnTermCompleted
    //   commits degree credit, may graduate the Sim, and always calls LeaveJob, whose LeaveJobNow removes the professor
    //   from the major. Nothing replaces professors: SetupProfessorAlarm / RefreshProfessors have no caller in any
    //   assembly. After the first term the majors have no professor (lectures, professor socials, HUD lookup). Fix:
    //   OnTermCompleted does nothing for a professor. Counters [2612] professors kept, [2613] errors.
    // protestlastday (kProtestLastDay, docs/572 §5) [verified, ImprovedProtestSituation.cs]: the NPC protest cooldown
    //   static mLastDayHadProtest (absolute elapsed days of the last protest) is a plain static: it is 0 after a fresh
    //   start but survives a world quit. Loading a save whose clock is behind the old value suppresses NPC protests until
    //   that save's clock passes it. Fix: reset to 0 on world quit (the value a fresh start has).
    //   Counters [2614] resets at quit, [2615] errors.
    // Checks: UniversityLogicChecks.cs.
    public static partial class Fixes
    {
        static readonly FieldInfo sPlLastDay = typeof(ImprovedProtestSituation).GetField("mLastDayHadProtest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        static bool sUlHooked;

        static void ApplyUniversityLogic()
        {
            Patch(typeof(AcademicCareer), "OnTermCompleted", new Type[] { typeof(float) }, typeof(ProfessorTermHook), "TermHook", "TermOrig");
            if (!sUlHooked)
            {
                sUlHooked = true;
                World.OnWorldQuitEventHandler += delegate { ProtestLastDayQuit(); };
            }
        }

        // ---- professorterm ----
        internal static bool PtSkip(bool on, bool isProfessor)
        {
            return on && isProfessor;
        }

        public class ProfessorTermHook
        {
            public void TermHook(float gradeAsPercentage)
            {
                try
                {
                    if (PtSkip(ShortcutOn("professorterm"), ((AcademicCareer)(object)this).IsProfessor)) { PatchCounters()[2612]++; return; }
                }
                catch (Exception) { PatchCounters()[2613]++; }
                TermOrig(gradeAsPercentage);
            }
            public void TermOrig(float gradeAsPercentage) { }
        }

        // ---- protestlastday ----
        internal static bool PlReset(bool on, float lastDay)
        {
            return on && lastDay != 0f;
        }

        static void ProtestLastDayQuit()
        {
            try
            {
                if (sPlLastDay == null || !PlReset(ShortcutOn("protestlastday"), (float)sPlLastDay.GetValue(null))) return;
                sPlLastDay.SetValue(null, 0f);
                PatchCounters()[2614]++;
            }
            catch (Exception) { PatchCounters()[2615]++; }
        }
    }
}
