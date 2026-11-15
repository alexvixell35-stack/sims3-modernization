namespace Ts3Fixes
{
    // weddingcancel harness check (docs/563 §1). Pure decisions; nothing in the world changes.
    public static partial class Fixes
    {
        // WeddingCancelCheck run: (a) the impatience cancel (no jilter, no end alarm) and (b) a guest who likes the jilted
        //   Sim after a jilt. Controls: a jilter set / an alarm already armed for (a); no jilter, an invalid guest, a guest
        //   who dislikes the jilted Sim for (b).
        //   off BROKEN = no end alarm, insult on the wrong relationship; on FIXED = both done.
        //   result  counters 2505-2507
        public static object WeddingCancelCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "weddingcancel: end alarms 2505=" + c[2505] + ", guest insults 2506=" + c[2506] + ", errors 2507=" + c[2507];
            if (what == "result") return counters;
            if (what != "run") return "weddingcancel <run|result>";
            bool on = ShortcutOn("weddingcancel");
            bool alarm = WcEndAlarm(on, false, false);
            bool insult = WcInsultGuest(on, true, true, true);
            bool control = WcEndAlarm(on, true, false) || WcEndAlarm(on, false, true)
                || WcInsultGuest(on, false, true, true) || WcInsultGuest(on, true, false, true) || WcInsultGuest(on, true, true, false);
            bool any = alarm || insult, all = alarm && insult;
            string verdict = control ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "weddingcancel check (switch " + (on ? "on" : "off") + "): impatience cancel gets end alarm=" + alarm + ", jilt insults guest=" + insult
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
