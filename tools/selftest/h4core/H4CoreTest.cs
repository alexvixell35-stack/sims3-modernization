// Offline self-check of mods/ts3fixes/src/H4Core.cs (docs/250 §7.0, §7.1, §7.4): the gvattr source class and scope sent to
// ts3native, the watchdog's async idle check decisions.
using System;
using Ts3Fixes;

static class H4CoreTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static int Main()
    {
        // gvattr (docs/250 §7.0): watchdog first, then stuck check, other own checks, the Sim's own task, another task, none
        Check(H4Core.SrcClass(true, true, true, true, true) == H4Core.kSrcWatchdog, "watchdog check wins");
        Check(H4Core.SrcClass(false, true, true, true, true) == H4Core.kSrcStuck, "stuck check");
        Check(H4Core.SrcClass(false, false, true, true, true) == H4Core.kSrcOwnCheck, "other own check");
        Check(H4Core.SrcClass(false, false, false, true, true) == H4Core.kSrcOwn, "own task");
        Check(H4Core.SrcClass(false, false, false, false, true) == H4Core.kSrcForeign, "foreign task");
        Check(H4Core.SrcClass(false, false, false, false, false) == H4Core.kSrcNone, "no task");
        // scope: only UfWhy 0 is unseen; seen NPC 1; everything else (household, selected, unknown) 0
        Check(H4Core.Scope(0, false) == 2, "unseen");
        Check(H4Core.Scope(7, true) == 1, "seen NPC");
        Check(H4Core.Scope(1, false) == 0 && H4Core.Scope(-1, false) == 0, "household / unknown = 0");
        Check(H4Core.Message(H4Core.kSrcWatchdog, 2) == 0x201u, "message layout class | scope << 8");
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
        // watchdog stillness: an interaction ended in place restarts the record; nothing else changes it
        Check(H4Core.WdStillSince(100, 50, 200) == 100, "queue emptied before the record: still since 100");
        Check(H4Core.WdStillSince(100, 150, 200) == 150, "interaction ended in place at 150: restarts there");
        Check(H4Core.WdStillSince(100, 200, 200) == 200, "ended this pass: restarts now");
        Check(H4Core.WdStillSince(100, 250, 200) == 100, "stamp in the future: ignored");
        Check(H4Core.WdStillSince(100, 0, 200) == 100, "never emptied / no autonomy: unchanged (real stuck Sim still counts)");
        Check(H4Core.WdStillSince(100, 100, 200) == 100, "emptied as the record began: unchanged");
        Check(H4Core.WdStationaryResets(false, true), "human on terrain a day: reset");
        Check(H4Core.WdStationaryResets(true, false), "horse on a lot a day: reset");
        Check(!H4Core.WdStationaryResets(true, true), "horse grazing on terrain: left alone");
        // watchdogfailcap: the third rule-49 reset with no successful plan since is refused; a success clears after the grace
        Check(!H4Core.WdFailCapRefuses(0) && !H4Core.WdFailCapRefuses(1), "first and second route-fail resets run");
        Check(H4Core.WdFailCapRefuses(2) && H4Core.WdFailCapRefuses(3), "third route-fail reset refused");
        Check(!H4Core.WdFailResetClears(104, 100, 5), "success inside the grace (the reset's own placement): kept");
        Check(H4Core.WdFailResetClears(105, 100, 5) && H4Core.WdFailResetClears(500, 100, 5), "success after the grace: cleared");
        Check(H4Core.WdFailResetClears(50, 100, 5), "reset stamp in the future (load): cleared");
        // docs/616 spotasync: only a full check of an unseen NPC in its own yielding task that ts3native would start goes async
        Check(H4Core.SpotAsync(true, true, true, true, true, false, false), "spotasync: unseen NPC, own task, would start: async");
        Check(!H4Core.SpotAsync(false, true, true, true, true, false, false), "spotasync off: sync as today");
        Check(!H4Core.SpotAsync(true, false, true, true, true, false, false), "cache hit or no budget: no plan at all");
        Check(!H4Core.SpotAsync(true, true, false, true, true, false, false), "not the Sim's own task: sync");
        Check(!H4Core.SpotAsync(true, true, true, false, true, false, false), "cannot yield: sync");
        Check(!H4Core.SpotAsync(true, true, true, true, false, false, false), "ts3native would refuse (no WatchdogKind2 / Kinds bit 2): sync");
        Check(!H4Core.SpotAsync(true, true, true, true, true, false, true), "inside the watchdog's check: A1's own path");
        for (int m = 0; m < 64; m++)
            Check(!H4Core.SpotAsync((m & 1) != 0, (m & 2) != 0, (m & 4) != 0, (m & 8) != 0, (m & 16) != 0, true, (m & 32) != 0),
                "household Sim never goes async (case " + m + ")");
        Check(H4Core.SpotPlaceAfter(false, false, false, false), "no yield: today's path, whatever the state");
        Check(H4Core.SpotPlaceAfter(true, true, true, true), "yielded, still there and unseen: placed");
        Check(!H4Core.SpotPlaceAfter(true, false, true, true), "yielded, Sim gone: not placed");
        Check(!H4Core.SpotPlaceAfter(true, true, false, true), "yielded, now seen: the game's own trip");
        Check(!H4Core.SpotPlaceAfter(true, true, true, false), "yielded, a viewer came near: the game's own trip");
        Console.WriteLine(fails == 0 ? "h4core selftest: ok" : "h4core selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
