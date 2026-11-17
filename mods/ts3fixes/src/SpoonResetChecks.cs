namespace Ts3Fixes
{
    // spoonreset harness check (docs/565 §6). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // SpoonResetCheck run: a Sim in a spoon pair whose partner's BedSleep is gone (null, destroyed, not in its queue)
        //   releases its side. Controls: partner still sleeping, Sim not in a pair -> EA's code.
        //   off BROKEN = pair kept; on FIXED = released.
        //   result  counters 2536-2537
        public static object SpoonResetCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "spoonreset: pairs released 2536=" + c[2536] + ", errors 2537=" + c[2537];
            if (what == "result") return counters;
            if (what != "run") return "spoonreset <run|result>";
            bool on = ShortcutOn("spoonreset");
            bool release = SrPartnerGone(on, true, false, false, false) && SrPartnerGone(on, true, true, true, false) && SrPartnerGone(on, true, false, true, false);
            bool control = SrPartnerGone(on, true, false, false, true) || SrPartnerGone(on, false, true, true, false);
            string verdict = control ? "UNEXPECTED" : !on ? (release ? "UNEXPECTED" : "BROKEN") : (release ? "FIXED" : "UNEXPECTED");
            return "spoonreset check (switch " + (on ? "on" : "off") + "): pair released when the partner is gone=" + release + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
