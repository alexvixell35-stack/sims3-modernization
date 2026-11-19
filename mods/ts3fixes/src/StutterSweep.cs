namespace Ts3Fixes
{
    // Harness checks of the docs/662 stutter sweep fixes (pure decisions with controls, StutterSweepCore.cs); nothing in the
    // world is touched. Repros in tools/harness/repros.json (stucktaskflag-check).
    public static partial class Fixes
    {
        // StuckTaskFlagCheck run: a plan in another task while a stuck check sleeps is not a stuck-check probe with the switch on.
        //   Controls: the check's own task still sees the flag (its probes keep their tags); no check running = nobody sees it.
        public static object StuckTaskFlagCheck(string what)
        {
            string counters = B582Counters("stucktaskflag", 4240, new[] { "other tasks' plans inside a check (old flag marked them)" }, "");
            if (what == "result") return counters;
            if (what != "run") return "stucktaskflag <run|result>";
            bool on = ShortcutOn("stucktaskflag");
            const ulong run = 0x1234, other = 0x5678;
            bool eaBug = StutterSweepCore.StuckInTask(true, false, run, other);       // old flag: another Sim's plan reads "stuck check"
            bool fixedNow = !StutterSweepCore.StuckInTask(true, on, run, other);
            bool controls = eaBug && StutterSweepCore.StuckInTask(true, true, run, run) && StutterSweepCore.StuckInTask(true, false, run, run)
                && !StutterSweepCore.StuckInTask(false, true, run, run) && !StutterSweepCore.StuckInTask(false, false, run, other)
                && StutterSweepCore.StuckLeak(true, run, other) && !StutterSweepCore.StuckLeak(true, run, run) && !StutterSweepCore.StuckLeak(false, run, other);
            return B582Line("stucktaskflag", on, "a plan of another task during a sleeping stuck check keeps its own scope (not a probe)", fixedNow,
                "old flag leaks to other tasks (" + eaBug + "), the check's own task still tagged, no check = no tag", controls, "", counters);
        }
    }
}
