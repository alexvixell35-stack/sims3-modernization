using System;
using Ts3Fixes;

// Offline self-check of StutterSweepCore (docs/662).
static class StutterSweepTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        const ulong run = 0x14BC00F5, other = 0x14BC00F6;

        // §2 stucktaskflag
        Check(!StutterSweepCore.StuckInTask(false, true, run, run) && !StutterSweepCore.StuckInTask(false, false, run, other), "no check running: nobody is in it");
        Check(StutterSweepCore.StuckInTask(true, true, run, run), "per task: the check's own task is in it");
        Check(!StutterSweepCore.StuckInTask(true, true, run, other), "per task: another task's plan is not a probe");
        Check(StutterSweepCore.StuckInTask(true, false, run, other) && StutterSweepCore.StuckInTask(true, false, run, run), "switch off: the old global flag");
        Check(!StutterSweepCore.StuckInTask(true, true, 0, 0), "per task: no run task recorded = nobody");
        Check(StutterSweepCore.StuckLeak(true, run, other) && !StutterSweepCore.StuckLeak(true, run, run) && !StutterSweepCore.StuckLeak(false, run, other), "leak count only for other tasks inside a check");

        if (sFails > 0) { Console.WriteLine("stuttersweep: " + sFails + " FAILED"); Environment.Exit(1); }
        Console.WriteLine("stuttersweep: all checks passed");
    }
}
