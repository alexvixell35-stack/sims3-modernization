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
            string counters = B582Counters("stucktaskflag", 4244, new[] { "other tasks' plans inside a check (old flag marked them)" }, "");
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

        // ---- docs/662 §3 watchdogbusydefer (tuning kWatchdogBusyDefer, default on) ------------------------------------------
        // Counters [4245] refused A1 STARTs seen, [4246] checks deferred (no sync plan behind the worker's lock), [4247] deferral
        // cap reached (that check planned sync as before).
        static readonly System.Collections.Generic.Dictionary<ulong, int> sWdDefers = new System.Collections.Generic.Dictionary<ulong, int>();
        // RouteBackoffHook.AsyncKindStart: the A1 check's tagged START was just refused
        static void WdStartRefused(Sims3.SimIFace.IScriptProxy proxy)
        {
            int[] c = PatchCounters();
            c[4245]++;
            if (!ShortcutOn("watchdogbusydefer")) return;
            Sims3.Gameplay.Actors.Sim sim = proxy != null ? proxy.Target as Sims3.Gameplay.Actors.Sim : null;
            if (sim == null) return;
            ulong id = sim.ObjectId.Value;
            int n;
            sWdDefers.TryGetValue(id, out n);
            if (!StutterSweepCore.WdBusyDefer(true, true, true, n)) { c[4247]++; sWdDefers.Remove(id); return; }
            if (sWdDefers.Count >= 256) sWdDefers.Clear();  // bounded: Sims that never get a verdict again
            sWdDefers[id] = n + 1;
            if (sSpotDeferred.Count >= 64) sSpotDeferred.Clear();
            sSpotDeferred[Sims3.SimIFace.Simulator.CurrentTask.Value] = true;
        }
        // RouteBackoffHook.PlanFromPointAsyncBody: this task's A1 check had its START refused just now
        static bool WdBusyDeferNow()
        {
            ulong task = Sims3.SimIFace.Simulator.CurrentTask.Value;
            return sSpotDeferred.Count > 0 && sWdA1Task != 0 && sWdA1Task == task && sInOwnCheck && sSpotDeferred.ContainsKey(task);
        }
        // SpotVerdict: the A1 check got a verdict (job or sync): its deferrals in a row start again
        static void WdDeferDone(Sims3.Gameplay.Actors.Sim sim)
        {
            if (sWdDefers.Count > 0 && sim != null) sWdDefers.Remove(sim.ObjectId.Value);
        }
        // WatchdogBusyDeferCheck run: the refused A1 START answers no verdict (on) instead of a sync plan (off).
        //   Controls: an accepted START and a non-A1 plan are unchanged; the cap gives the sync plan back.
        public static object WatchdogBusyDeferCheck(string what)
        {
            string counters = B582Counters("watchdogbusydefer", 4245, new[] { "refused A1 STARTs", "checks deferred", "deferral cap reached (sync)" }, "");
            if (what == "result") return counters;
            if (what != "run") return "watchdogbusydefer <run|result>";
            bool on = ShortcutOn("watchdogbusydefer");
            bool eaBug = !StutterSweepCore.WdBusyDefer(false, true, true, 0);   // off: the refused START plans sync (waits on +0x918)
            bool fixedNow = StutterSweepCore.WdBusyDefer(on, true, true, 0);
            bool controls = eaBug && !StutterSweepCore.WdBusyDefer(true, true, false, 0)   // START accepted: the job answers
                && !StutterSweepCore.WdBusyDefer(true, false, true, 0)                       // not the A1 check: unchanged
                && !StutterSweepCore.WdBusyDefer(true, true, true, StutterSweepCore.kWdDeferMax)  // capped: sync as before
                && StutterSweepCore.WdBusyDefer(true, true, true, StutterSweepCore.kWdDeferMax - 1);
            return B582Line("watchdogbusydefer", on, "the watchdog's A1 check whose START was refused answers no verdict now instead of a sync plan behind the worker's lock", fixedNow,
                "EA path plans sync (" + eaBug + "), accepted STARTs and other plans unchanged, sync again after " + StutterSweepCore.kWdDeferMax + " deferrals in a row; Sims pending "
                + sWdDefers.Count, controls, "", counters);
        }
    }
}
