namespace Ts3Fixes
{
    // moveinpartner harness check (docs/563 §3). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // MoveInPartnerCheck run: the chosen Sim does not fit the partner's household (partner can move the other way /
        //   cannot). Control: the Sim fits (EA's move).
        //   off BROKEN = moved over the cap; on FIXED = partner moves / move skipped.
        //   (docs/563 §5) a teen couple: skipped. Control: adults.
        //   household rule: a household / selected / unknown-scope Sim on either side is left to EA (MipExempt). Control: two NPCs.
        //   result  counters 2510-2512, 2516, 1046
        public static object MoveInPartnerCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "moveinpartner: partner moved 2510=" + c[2510] + ", skipped 2511=" + c[2511] + ", errors 2512=" + c[2512] + ", teen moves skipped 2516=" + c[2516] + ", left to EA (household scope) 1046=" + c[1046];
            if (what == "result") return counters;
            if (what != "run") return "moveinpartner <run|result>";
            bool on = ShortcutOn("moveinpartner");
            bool swap = MipAction(on, false, true) == 1;
            bool skip = MipAction(on, false, false) == 2;
            bool teen = MipTeenSkip(on, true);
            bool control = MipAction(on, true, true) != 0 || MipAction(on, true, false) != 0 || MipTeenSkip(on, false);
            bool exemptOk = MipExempt(true, false) && MipExempt(false, true) && MipExempt(true, true) && !MipExempt(false, false);
            if (!exemptOk) control = true;
            bool any = swap || skip || teen, all = swap && skip && teen;
            string verdict = control ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "moveinpartner check (switch " + (on ? "on" : "off") + "): full household -> partner moves=" + swap + ", skipped=" + skip + ", teen move skipped=" + teen
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
