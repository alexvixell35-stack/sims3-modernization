namespace Ts3Fixes
{
    // autonomyfailblock harness check (docs/565 §3). Read-only: nothing in the world changes.
    public static partial class Fixes
    {
        // AutonomyFailBlockCheck run: an autonomous action that failed with no exit reason (posture unreachable), target valid,
        //   test still passing, is blocked. Controls: player-directed, cancelled, an exit reason set (EA's own path), a Sim
        //   target, a failed test, an invalid target -> never blocked by the fix.
        //   off BROKEN = not blocked (autonomy re-picks the object); on FIXED = blocked.
        //   result  counters 2530-2532
        public static object AutonomyFailBlockCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "autonomyfailblock: posture failures blocked 2530=" + c[2530] + ", failures left alone 2531=" + c[2531] + ", errors 2532=" + c[2532];
            if (what == "result") return counters;
            if (what != "run") return "autonomyfailblock <run|result>";
            bool on = ShortcutOn("autonomyfailblock");
            bool block = AfbShouldBlock(on, true, true, false, false, false, true);
            bool control = AfbShouldBlock(on, false, true, false, false, false, true) || AfbShouldBlock(on, true, true, true, false, false, true)
                || AfbShouldBlock(on, true, true, false, true, false, true) || AfbShouldBlock(on, true, true, false, false, true, true)
                || AfbShouldBlock(on, true, true, false, false, false, false) || AfbShouldBlock(on, true, false, false, false, false, true);
            string verdict = control ? "UNEXPECTED" : !on ? (block ? "UNEXPECTED" : "BROKEN") : (block ? "FIXED" : "UNEXPECTED");
            return "autonomyfailblock check (switch " + (on ? "on" : "off") + "): posture failure blocked=" + block + ", controls=" + control + "; verdict: " + verdict + "; " + counters;
        }
    }
}
