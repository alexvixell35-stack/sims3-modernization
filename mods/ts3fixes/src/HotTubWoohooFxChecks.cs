namespace Ts3Fixes
{
    // hottubwoohoofx harness check (docs/561 §7). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // HotTubWoohooFxCheck run: a woohoo Cleanup after a reset between the effect's start and stop events. Controls:
        //   another couple still woohooing, a normal end (effect already stopped), a Sim no longer in the tub.
        //   off BROKEN = effect left on; on FIXED = stopped.
        //   result  counters 2491-2492
        public static object HotTubWoohooFxCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "hottubwoohoofx: effects stopped 2491=" + c[2491] + ", errors 2492=" + c[2492];
            if (what == "result") return counters;
            if (what != "run") return "hottubwoohoofx <run|result>";
            bool on = ShortcutOn("hottubwoohoofx");
            bool stop = HtStopFx(on, true, false, true);
            bool control = HtStopFx(on, true, true, true) || HtStopFx(on, true, false, false) || HtStopFx(on, false, false, true);
            string verdict = control ? "UNEXPECTED" : !on ? (stop ? "UNEXPECTED" : "BROKEN") : (stop ? "FIXED" : "UNEXPECTED");
            return "hottubwoohoofx check (switch " + (on ? "on" : "off") + "): reset mid-woohoo -> effect stopped=" + stop
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
