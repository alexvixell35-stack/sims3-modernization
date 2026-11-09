// Offline self-check of mods/ts3fixes/src/H4Core.cs (docs/250 §7.1, §7.4): the watchdog's async idle check decisions.
using System;
using Ts3Fixes;

static class H4CoreTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // docs/250 §7.4 CheckIdle order (2026-11-10): cache -> sync; else job holds +0x918 and lockskip on -> skip; else A1 if on and would start; else sync
        Check(H4Core.WdRoute(true, true, true, true, true, false) == H4Core.kWdSync, "cached verdict: no plan path change");
        Check(H4Core.WdRoute(false, false, true, true, true, false) == H4Core.kWdA1, "no job holds the lock, A1 on and accepted: A1 (the plan leaves the sim thread)");
        Check(H4Core.WdRoute(false, false, true, false, true, false) == H4Core.kWdSync, "no job holds the lock, A1 refused: sync as today (no skip)");
        Check(H4Core.WdRoute(false, false, false, false, true, false) == H4Core.kWdSync, "A1 off, no lock held: sync as today");
        // docs/247 §12.4: the lock check runs before A1 (A1 used to win here, so watchdoglockskip never fired with watchdogasync on)
        Check(H4Core.WdRoute(false, true, true, true, true, false) == H4Core.kWdSkip, "lock held, lockskip on, A1 on and accepted: lockskip first");
        Check(H4Core.WdRoute(false, true, true, true, false, false) == H4Core.kWdA1, "lock held, lockskip off, A1 on and accepted: A1");
        Check(H4Core.WdRoute(false, true, true, false, true, false) == H4Core.kWdSkip, "A1 refused: lockskip");
        Check(H4Core.WdRoute(false, true, false, true, true, false) == H4Core.kWdSkip, "A1 off: lockskip");
        Check(H4Core.WdRoute(false, true, true, false, false, false) == H4Core.kWdSync, "A1 refused, lockskip off: sync");
        // household rule (CLAUDE.md, unknown scope counts as household): a household Sim never reaches A1, in any combination
        for (int m = 0; m < 32; m++)
            Check(H4Core.WdRoute((m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0, (m & 16) != 0, true) != H4Core.kWdA1,
                "household Sim never reaches A1 (case " + m + ")");
        Check(H4Core.WdRoute(false, true, true, true, false, true) == H4Core.kWdSync, "household, lock held, A1 would: sync (today's wait)");
        // stale-verdict rule: only an unchanged Sim keeps its A1 verdict; any one change drops it
        Check(H4Core.WdFresh(true, false, 0f, true, true, true, false), "unchanged Sim: verdict kept");
        Check(H4Core.WdFresh(true, false, 0.009f, true, true, true, false), "under 1 cm: same spot");
        Check(!H4Core.WdFresh(false, false, 0f, true, true, true, false), "destroyed / out of world: dropped");
        Check(!H4Core.WdFresh(true, true, 0f, true, true, true, false), "became household or selectable: dropped");
        Check(!H4Core.WdFresh(true, false, 0.02f, true, true, true, false), "moved: dropped");
        Check(!H4Core.WdFresh(true, false, 0f, false, true, true, false), "other interaction: dropped");
        Check(!H4Core.WdFresh(true, false, 0f, true, false, true, false), "other lot: dropped");
        Check(!H4Core.WdFresh(true, false, 0f, true, true, false, false), "not standing: dropped");
        Check(!H4Core.WdFresh(true, false, 0f, true, true, true, true), "routing: dropped");
        Console.WriteLine(fails == 0 ? "h4core selftest: ok" : "h4core selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
