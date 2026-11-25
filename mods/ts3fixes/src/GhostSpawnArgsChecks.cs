namespace Ts3Fixes
{
    // ghostspawnargs harness check (docs/563 §2). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // GhostSpawnArgsCheck run: an angry spawn (Ghost Hunter) and a spawn for another lot (SpellBook, OccultGenie).
        //   Controls: not angry and no lot / the urn's own lot (EA's body is already right).
        //   off BROKEN = arguments dropped; on FIXED = both forwarded.
        //   result  counters 2508-2509
        public static object GhostSpawnArgsCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "ghostspawnargs: angry forwarded 2508=" + c[2508] + ", lot forwarded 2509=" + c[2509];
            if (what == "result") return counters;
            if (what != "run") return "ghostspawnargs <run|result>";
            bool on = ShortcutOn("ghostspawnargs");
            bool angry = GsForward(on, true, true, true) == 1;
            bool lot = GsForward(on, false, true, true) == 2;
            bool control = GsForward(on, false, false, true) != 0 || GsForward(on, false, true, false) != 0;
            bool any = angry || lot, all = angry && lot;
            string verdict = control ? "UNEXPECTED" : !on ? (any ? "UNEXPECTED" : "BROKEN") : (all ? "FIXED" : "UNEXPECTED");
            return "ghostspawnargs check (switch " + (on ? "on" : "off") + "): angry kept=" + angry + ", lot kept=" + lot
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
