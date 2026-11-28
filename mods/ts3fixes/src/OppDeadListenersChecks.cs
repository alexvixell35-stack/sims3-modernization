namespace Ts3Fixes
{
    // oppdeadlisteners harness check (docs/562 §1). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // OppDeadListenersCheck run: an ending Sim-target opportunity with its kSimDied listener still registered.
        //   Controls: no listener set, a listener already completed. off BROKEN = left registered; on FIXED = removed.
        //   result  counters 2495-2496
        public static object OppDeadListenersCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "oppdeadlisteners: listeners removed 2495=" + c[2495] + ", errors 2496=" + c[2496];
            if (what == "result") return counters;
            if (what != "run") return "oppdeadlisteners <run|result>";
            bool on = ShortcutOn("oppdeadlisteners");
            bool r = OdlRemove(on, true, false);
            bool control = OdlRemove(on, false, false) || OdlRemove(on, true, true);
            string verdict = control ? "UNEXPECTED" : !on ? (r ? "UNEXPECTED" : "BROKEN") : (r ? "FIXED" : "UNEXPECTED");
            return "oppdeadlisteners check (switch " + (on ? "on" : "off") + "): ended opportunity's Sim listener -> removed=" + r
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
