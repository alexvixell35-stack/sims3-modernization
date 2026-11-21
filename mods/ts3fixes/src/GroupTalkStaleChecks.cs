namespace Ts3Fixes
{
    // grouptalkstale harness check (docs/566 §1). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // GroupTalkStaleCheck run: a group-talk member that is destroyed or on another lot is dropped from the speaker's
        //   group. Controls: a member on the same lot, the speaker itself -> kept (EA's code).
        //   off BROKEN = departed members kept; on FIXED = dropped.
        //   result  counters 2541-2543
        public static object GroupTalkStaleCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "grouptalkstale: members dropped 2541=" + c[2541] + ", left old group on re-register 2542=" + c[2542] + ", errors 2543=" + c[2543];
            if (what == "result") return counters;
            if (what != "run") return "grouptalkstale <run|result>";
            bool on = ShortcutOn("grouptalkstale");
            bool drop = GtMemberGone(on, false, false, false) && GtMemberGone(on, false, true, false);
            bool control = GtMemberGone(on, false, false, true) || GtMemberGone(on, true, false, false);
            string verdict = control ? "UNEXPECTED" : !on ? (drop ? "UNEXPECTED" : "BROKEN") : (drop ? "FIXED" : "UNEXPECTED");
            return "grouptalkstale check (switch " + (on ? "on" : "off") + "): departed member dropped=" + drop + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
