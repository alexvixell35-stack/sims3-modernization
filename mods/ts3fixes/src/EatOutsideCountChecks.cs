namespace Ts3Fixes
{
    // eatoutsidecount harness check (docs/569 §2). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // EatOutsideCountCheck run: a follower that already reported done and is then cancelled is counted once.
        //   Controls: a cancel before done (EA's single decrement), a Sim not in the group -> EA.
        //   off BROKEN = counted twice; on FIXED = second decrement put back.
        //   result  counters 2568-2569, noted pairs and whether the internal members were found
        public static object EatOutsideCountCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "eatoutsidecount: restored 2568=" + c[2568] + ", errors 2569=" + c[2569] + ", noted now " + sEocDone.Count
                + "; members found=" + (sEocType != null && sEocLeft != null && sEocFollowers != null);
            if (what == "result") return counters;
            if (what != "run") return "eatoutsidecount <run|result>";
            bool on = ShortcutOn("eatoutsidecount");
            bool restore = EocRestore(on, true, true);
            bool control = EocRestore(on, false, true) || EocRestore(on, true, false);
            string verdict = control ? "UNEXPECTED" : !on ? (restore ? "UNEXPECTED" : "BROKEN") : (restore ? "FIXED" : "UNEXPECTED");
            return "eatoutsidecount check (switch " + (on ? "on" : "off") + "): second decrement restored=" + restore + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
