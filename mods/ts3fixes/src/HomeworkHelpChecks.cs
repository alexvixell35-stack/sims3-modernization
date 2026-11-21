namespace Ts3Fixes
{
    // homeworkhelp harness check (docs/565 §7). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // HomeworkHelpCheck run: a HelpWithHomework that starts after the child's jig is gone (or with no child Sim) ends
        //   before EA's null dereference. Control: jig and child present -> EA's code.
        //   off BROKEN = EA's Run (NullReferenceException); on FIXED = ends cleanly.
        //   result  counters 2538-2539
        public static object HomeworkHelpCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "homeworkhelp: helpers stopped 2538=" + c[2538] + ", errors 2539=" + c[2539];
            if (what == "result") return counters;
            if (what != "run") return "homeworkhelp <run|result>";
            bool on = ShortcutOn("homeworkhelp");
            bool stop = HwhStop(on, false, true) && HwhStop(on, true, false);
            bool control = HwhStop(on, true, true);
            string verdict = control ? "UNEXPECTED" : !on ? (stop ? "UNEXPECTED" : "BROKEN") : (stop ? "FIXED" : "UNEXPECTED");
            return "homeworkhelp check (switch " + (on ? "on" : "off") + "): helper stops with no jig / no child=" + stop + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
