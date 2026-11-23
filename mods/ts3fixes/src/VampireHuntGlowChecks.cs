namespace Ts3Fixes
{
    // vampirehuntglow harness check (docs/561 §7). Pure decision; nothing in the world changes.
    public static partial class Fixes
    {
        // VampireHuntGlowCheck run: a hunt candidate that walked off the lot during the hunt. Controls: a candidate still on
        //   the lot (EA's own pick clears it), a destroyed one. off BROKEN = heat vision left on; on FIXED = cleared.
        //   result  counters 2489-2490
        public static object VampireHuntGlowCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "vampirehuntglow: glows cleared 2489=" + c[2489] + ", errors 2490=" + c[2490];
            if (what == "result") return counters;
            if (what != "run") return "vampirehuntglow <run|result>";
            bool on = ShortcutOn("vampirehuntglow");
            bool left = VhClearGlow(on, true, false);
            bool control = VhClearGlow(on, false, false) || VhClearGlow(on, true, true);
            string verdict = control ? "UNEXPECTED" : !on ? (left ? "UNEXPECTED" : "BROKEN") : (left ? "FIXED" : "UNEXPECTED");
            return "vampirehuntglow check (switch " + (on ? "on" : "off") + "): candidate that left the lot -> glow cleared=" + left
                + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
