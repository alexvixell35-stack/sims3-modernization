using System;

namespace Ts3Fixes
{
    // Harness checks for UniversityLogic.cs (docs/572 §4-5). Read-only decisions with controls.
    public static partial class Fixes
    {
        // ProfessorTermCheck run: the term-end pass reaches a professor.
        //   Controls: a student's term end still runs (both states).
        public static object ProfessorTermCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "professorterm: professors kept 2612=" + c[2612] + ", errors 2613=" + c[2613] + "; " + GlMembers("");
            if (what == "result") return counters;
            if (what != "run") return "professorterm <run|result>";
            bool on = ShortcutOn("professorterm");
            bool kept = PtSkip(on, true);
            bool ctlStudent = !PtSkip(on, false);
            return "professorterm check (switch " + (on ? "on" : "off") + "): professor kept at term end=" + kept
                + "; control: student term end runs=" + ctlStudent + "; controls ok=" + ctlStudent
                + "; verdict: " + GlVerdict(on, kept, ctlStudent, true) + "; " + counters;
        }

        // ProtestLastDayCheck run: world quit with the protest cooldown at day 40.
        //   Controls: 0 (fresh start) is left alone.
        public static object ProtestLastDayCheck(string what)
        {
            int[] c = PatchCounters();
            string now = "?";
            try { if (sPlLastDay != null) now = ((float)sPlLastDay.GetValue(null)).ToString(); } catch (Exception) { }
            string counters = "protestlastday: resets at quit 2614=" + c[2614] + ", errors 2615=" + c[2615] + "; value now=" + now
                + "; " + GlMembers(sPlLastDay == null ? "ImprovedProtestSituation.mLastDayHadProtest" : "");
            if (what == "result") return counters;
            if (what != "run") return "protestlastday <run|result>";
            bool on = ShortcutOn("protestlastday");
            bool reset = PlReset(on, 40f);
            bool ctlZero = !PlReset(on, 0f);
            return "protestlastday check (switch " + (on ? "on" : "off") + "): cooldown reset at quit=" + reset
                + "; control: fresh value untouched=" + ctlZero + "; controls ok=" + ctlZero
                + "; verdict: " + GlVerdict(on, reset, ctlZero, sPlLastDay != null) + "; " + counters;
        }
    }
}
