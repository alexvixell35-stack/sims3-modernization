namespace Ts3Fixes
{
    // daycarequit harness check (docs/562 §2). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // DaycareQuitCheck run: quitting the daycare career outside work hours with two children enrolled. Controls: a quit
        //   during a workday (EA removes them), a toddler transport running, no children. off BROKEN = managers and
        //   listeners left; on FIXED = cleaned.
        //   result  counters 2497-2498
        public static object DaycareQuitCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "daycarequit: managers cleaned 2497=" + c[2497] + ", errors 2498=" + c[2498];
            if (what == "result") return counters;
            if (what != "run") return "daycarequit <run|result>";
            bool on = ShortcutOn("daycarequit");
            bool clean = DqClean(on, false, false, 2);
            bool control = DqClean(on, true, false, 2) || DqClean(on, false, true, 2) || DqClean(on, false, false, 0);
            string verdict = control ? "UNEXPECTED" : !on ? (clean ? "UNEXPECTED" : "BROKEN") : (clean ? "FIXED" : "UNEXPECTED");
            return "daycarequit check (switch " + (on ? "on" : "off") + "): quit outside work hours -> managers cleaned=" + clean
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
