using System;
using Ts3Fixes;

// Offline checks of QueueWakeCore (queuewake, docs/686 §23).
static class QueueWakeTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        Check(QueueWakeCore.Sleep(false, false, 20, 10, 2) == 20 && QueueWakeCore.Sleep(false, true, 20, 10, 2) == 20, "autonomy off: 20");
        Check(QueueWakeCore.Sleep(true, true, 20, 10, 2) == 10, "in the autonomy queue: 10");
        Check(QueueWakeCore.Sleep(true, false, 20, 10, 2) == 0, "2-tick sleep not marked");
        Check(QueueWakeCore.Decide(true, 100, 110, true, false, true) == QueueWakeCore.kWake, "first entry inside the sleep: wake");
        Check(QueueWakeCore.Decide(true, 110, 110, true, false, true) == QueueWakeCore.kStale && QueueWakeCore.Decide(true, 500, 110, true, false, true) == QueueWakeCore.kStale, "past the deadline: never");
        Check(QueueWakeCore.Decide(true, 100, 110, false, false, true) == QueueWakeCore.kNotIdle, "not the first entry");
        Check(QueueWakeCore.Decide(true, 100, 110, true, true, true) == QueueWakeCore.kOwnTask, "own task");
        Check(QueueWakeCore.Decide(false, 100, 110, true, false, true) == QueueWakeCore.kNoMark, "no mark");
        Check(QueueWakeCore.Decide(true, 100, 110, true, false, false) == QueueWakeCore.kNpc, "NPC");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
