namespace Ts3Fixes
{
    // bonfirespice harness check (docs/561 §6). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // BonfireSpiceCheck run: the herb step after Run: a failed route (herb removed, out of the world, not in an
        //   inventory) and a finished spice. off BROKEN = left (lost / leaked); on FIXED = back to the inventory /
        //   destroyed; a herb still in the inventory (Run stopped before taking it) is never touched.
        //   result  counters 2483-2485
        public static object BonfireSpiceCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bonfirespice: herbs returned/destroyed 2483=" + c[2483] + ", overlapping spice fixed 2484=" + c[2484] + ", errors 2485=" + c[2485];
            if (what == "result") return counters;
            if (what != "run") return "bonfirespice <run|result>";
            bool on = ShortcutOn("bonfirespice");
            int fail = BfHerbAction(on, false, true, false, false), done = BfHerbAction(on, true, true, false, false);
            bool control = BfHerbAction(on, false, true, false, true) != 0 || BfHerbAction(on, true, true, true, false) != 0;
            string verdict = control ? "UNEXPECTED" : !on ? (fail == 0 && done == 0 ? "BROKEN" : "UNEXPECTED") : (fail == 1 && done == 2 ? "FIXED" : "UNEXPECTED");
            return "bonfirespice check (switch " + (on ? "on" : "off") + "): failed route -> " + new string[] { "left", "back to inventory", "destroyed" }[fail]
                + ", finished spice -> " + new string[] { "left", "back to inventory", "destroyed" }[done] + "; verdict: " + verdict + "; " + counters;
        }
    }
}
