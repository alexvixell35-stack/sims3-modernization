namespace Ts3Fixes
{
    // Game-independent parts of the docs/662 stutter sweep fixes, checked offline by tools/selftest/stuttersweep.
    public static class StutterSweepCore
    {
        // docs/662 §2 stucktaskflag: is the plan running now one of the stuck check's own probes? The check sets its flag for
        // the length of StuckCheckRun, but its probe plans can sleep on a route job (FallbackAsync foreign START, stuckcheckown),
        // and every other task that plans meanwhile read the flag too: their plans counted as stuck-check probes (ownCheck in
        // UfPlanBody: no unseen MARK, no goalhhexempt / birthroute scope, "src stuckcheck" in ufplan lines). With the switch on
        // only the task that runs the check (runTask) sees it, the shape of sInWdCheck / sInOwnCheck (PR #306). Off = the old
        // global flag.
        public static bool StuckInTask(bool checkOn, bool perTask, ulong runTask, ulong curTask)
        {
            if (!checkOn) return false;
            return !perTask || (runTask != 0 && runTask == curTask);
        }
        // the plan read the global flag but runs in another task: the case the switch fixes (counter 4244)
        public static bool StuckLeak(bool checkOn, ulong runTask, ulong curTask) { return checkOn && runTask != curTask; }

        // docs/662 §3 watchdogbusydefer: the watchdog's A1 idle check (CheckIdle -> SpotVerdict, never a household, selected or
        // unknown-scope Sim: H4Core.WdRoute) whose tagged PlanFromPoint START ts3native refused (the worker runs another job, queue
        // full) used to plan sync on the sim thread, waiting on +0x918 for that job. true = answer "no verdict now" instead (the
        // check retries in a later tick, as past SpotVerdict's per-tick budget), so the verdict is still the planner's own, later.
        // A Sim deferred kWdDeferMax times in a row plans sync as before, so a START that stays refused cannot hold a verdict off.
        public const int kWdDeferMax = 8;
        public static bool WdBusyDefer(bool on, bool a1Check, bool startRefused, int defersInARow)
        {
            return on && a1Check && startRefused && defersInARow >= 0 && defersInARow < kWdDeferMax;
        }
    }
}
