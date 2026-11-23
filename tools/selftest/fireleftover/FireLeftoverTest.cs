using System;
using Ts3Fixes;

// Offline self-check of mods/ts3fixes/src/FireLeftoverCore.cs (docs/862): every decision against its truth table.
static class FireLeftoverTest
{
    static int sFail;
    static void Check(bool ok, string what) { if (!ok) { sFail++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        const int k = FloCore.kConfirmTicks;
        Check(k == 15, "confirm window is EA's 15-minute fire-task fallback");
        // tick counting: the same positive excess in a row counts up, anything else restarts
        Check(FloCore.Seen(1, 0, 0) == 1, "first sighting -> 1");
        Check(FloCore.Seen(1, 1, 3) == 4, "same excess again -> +1");
        Check(FloCore.Seen(2, 1, 9) == 1, "excess changed -> restart at 1");
        Check(FloCore.Seen(0, 1, 9) == 0, "no excess -> 0");
        Check(FloCore.Seen(-1, -1, 9) == 0, "more trapped Sims than tasks -> 0");
        int t = 0, e = 0;
        for (int i = 0; i < k; i++) { t = FloCore.Seen(1, e, t); e = 1; }
        Check(t == k && FloCore.Drop(true, 1, t) == 1, "one excess over 15 ticks reaches the drop");
        // rescue tasks: drop the excess once seen for the whole window
        Check(FloCore.Drop(true, 1, k) == 1, "reported case: one task left, nobody trapped, 15 minutes -> dropped");
        Check(FloCore.Drop(true, 2, k + 4) == 2, "two tasks without a Sim -> two dropped");
        Check(FloCore.Drop(false, 1, k) == 0, "switch off -> EA keeps the task");
        Check(FloCore.Drop(true, 1, k - 1) == 0, "seen 14 minutes (EA's own dig gap is far shorter) -> kept");
        Check(FloCore.Drop(true, 1, 1) == 0, "first sighting (mid-carry, mid-reset) -> kept");
        Check(FloCore.Drop(true, 0, k) == 0, "no excess -> nothing");
        Check(FloCore.Drop(true, -1, k) == 0, "more trapped Sims than tasks -> nothing");
        // window-fire task: done when no spreading window fire is left for the whole window
        Check(FloCore.WindowDone(true, true, false, k), "window task pending, no window fire for 15 minutes -> completed");
        Check(!FloCore.WindowDone(false, true, false, k), "switch off -> kept");
        Check(!FloCore.WindowDone(true, true, true, k), "a window still burns -> kept");
        Check(!FloCore.WindowDone(true, true, false, k - 1), "clear for 14 minutes -> wait");
        Check(!FloCore.WindowDone(true, false, false, k), "no window task -> nothing");
        Console.WriteLine(sFail == 0 ? "fireleftover selftest: all passed" : "fireleftover selftest: " + sFail + " failed");
        return sFail == 0 ? 0 : 1;
    }
}
