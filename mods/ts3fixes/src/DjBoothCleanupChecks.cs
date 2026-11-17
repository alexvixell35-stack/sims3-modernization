namespace Ts3Fixes
{
    // djcleanup harness check (docs/561 §7). Pure decisions; nothing in the world changes.
    public static partial class Fixes
    {
        // DjBoothCleanupCheck run: (a) Sim B's cancelled "Play Track" cleaning up while Sim A owns the playing booth with a
        //   tip jar, (b) a cancelled music request that never routed while another patron's request is pending. Controls:
        //   the owner's own cleanup, an idle booth, a request that ran. off BROKEN = A's set ended / request cleared;
        //   on FIXED = both kept, controls untouched.
        //   result  counters 2486-2488
        public static object DjBoothCleanupCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "djcleanup: other sets kept 2486=" + c[2486] + ", other requests kept 2487=" + c[2487] + ", errors 2488=" + c[2488];
            if (what == "result") return counters;
            if (what != "run") return "djcleanup <run|result>";
            bool on = ShortcutOn("djcleanup");
            bool set = DjKeepOtherSet(on, true, false, true, true);
            bool req = DjKeepRequest(on, false, 3);
            bool control = DjKeepOtherSet(on, true, true, true, true) || DjKeepOtherSet(on, false, false, false, false)
                || DjKeepOtherSet(on, true, false, false, false) || DjKeepRequest(on, true, 3) || DjKeepRequest(on, false, -1);
            string verdict = control ? "UNEXPECTED" : !on ? (!set && !req ? "BROKEN" : "UNEXPECTED") : (set && req ? "FIXED" : "UNEXPECTED");
            return "djcleanup check (switch " + (on ? "on" : "off") + "): other Sim's set kept=" + set + ", other patron's request kept=" + req
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
