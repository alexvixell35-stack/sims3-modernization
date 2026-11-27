namespace Ts3Fixes
{
    // simresetalarms harness check (docs/357). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // SimResetAlarmsCheck run: the keep / drop decisions with controls (one-shot and DeleteOnReset timers still
        //   dropped on the world lot, a normal-lot reset untouched in mode 1, the drop list only in mode 2 and only for
        //   DeleteOnReset timers of an NPC on a normal lot, never for a household / selected / unknown-scope Sim, nothing
        //   with the switch off) and the hook's live path.
        //   off BROKEN = repeating NeverPersisted timers dropped on the world lot; on FIXED = kept.
        //   last    the last Sim reset the hook saw (needs simresetalarms or simresetalarmprobe on): lot kind, household,
        //           and the Sim's timer counts before>after, read from the manager (scoped, npr = repeating
        //           NeverPersisted, vampire day alarms)
        //   result  counters 1828-1833
        public static object SimResetAlarmsCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "simresetalarms: resets world lot 1828=" + c[1828] + ", other lot 1829=" + c[1829] + ", timers kept 1830=" + c[1830]
                + ", dropped by list 1831=" + c[1831] + ", serviceleave live re-arms skipped 1832=" + c[1832] + ", errors 1833=" + c[1833];
            if (what == "result") return counters;
            if (what == "last") return "simresetalarms last: " + sSraLast;
            if (what != "run") return "simresetalarms <run|last|result>";
            bool on = ShortcutOn("simresetalarms");
            int mode = SraMode();
            bool keep = SraDecide(mode, true, 2, true, false, true) == 1;
            bool control = SraDecide(mode, true, 2, false, false, true) != 0 || SraDecide(mode, true, 1, true, false, true) != 0 || SraDecide(mode, true, 0, true, false, true) != 0
                || SraDecide(mode, false, 2, true, false, true) != 0 || SraDecide(mode, false, 1, true, false, true) != 0
                || SraDecide(0, true, 2, true, true, true) != 0 || SraDecide(0, false, 1, false, true, true) != 0
                || SraDecide(1, false, 1, false, true, true) != 0 || SraDecide(2, false, 1, false, true, true) != 2 || SraDecide(2, true, 1, false, true, true) != 0
                || SraDecide(2, false, 1, false, true, false) != 0 || SraDecide(1, true, 2, true, false, false) != 1 || SraDecide(2, false, 2, true, true, true) != 0 || SraDecide(2, true, 2, true, false, true) != 1;
            string live = CkLive("simresetalarms");
            string verdict = control || live.Length > 0 ? "UNEXPECTED" : !on ? (keep ? "UNEXPECTED" : "BROKEN") : (keep ? "FIXED" : "UNEXPECTED");
            return "simresetalarms check (switch " + (on ? "on" : "off") + ", mode " + mode + ", probe " + (ShortcutOn("simresetalarmprobe") ? "on" : "off")
                + "): world-lot repeating NeverPersisted kept=" + keep + ", drop list entries=" + sSraDropList.Length
                + ", controls=" + control + ", members=" + (live.Length == 0 ? "ok" : live) + "; verdict: " + verdict + "; " + counters;
        }
    }
}
