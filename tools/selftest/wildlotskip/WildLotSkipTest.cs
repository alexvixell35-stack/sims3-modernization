using System;
using Ts3Fixes;

// Offline self-check of WildLotSkipCore (docs/656 §8.3).
static class WildLotSkipTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        WildLotSkipCore g = new WildLotSkipCore();
        const long span = 6000, walk = 8000;
        const ulong horse = 0x14BC00F565957CE0UL, raccoon = 0x2B99003623895C40UL, lotA = 0x5DE50042F8B87B80UL, lotB = 0x8C41002E5095DBD0UL;
        ulong pool = WildLotSkipCore.kHorsePool;

        // memo per owner and per herd
        Check(!g.Skipped(horse, lotA, 0, span), "empty: not skipped");
        Check(g.Note(horse, lotA, 100, span), "first failure: new");
        Check(!g.Note(horse, lotA, 150, span), "repeat failure: not new");
        Check(g.Skipped(horse, lotA, 150 + span - 1, span), "inside the span (from the latest failure): skipped");
        Check(!g.Skipped(raccoon, lotA, 200, span), "another animal: not skipped");
        Check(!g.Skipped(pool, lotA, 200, span), "herd not noted: not skipped");
        Check(g.Note(pool, lotA, 200, span) && g.Skipped(pool, lotA, 300, span), "herd noted: skipped for the herd");
        Check(!g.Skipped(horse, lotB, 300, span), "another lot: not skipped");
        Check(!g.Skipped(horse, lotA, 150 + span, span), "span over: forgotten");
        g.Note(raccoon, lotB, 1000, span);
        Check(!g.Skipped(raccoon, lotB, 900, span), "clock went back (another save): forgotten");
        Check(!g.Note(raccoon, 0, 10, span), "lot id 0: never noted");

        // draw again without the skipped lots
        float[] w = { 2f, 16f, 24f, 1f };
        bool[] none = { false, false, false, false };
        Check(WildLotSkipCore.Pick(w, none, 0f) == 0 && WildLotSkipCore.Pick(w, none, 2f) == 0 && WildLotSkipCore.Pick(w, none, 2.01f) == 1, "no skips: EA's running sum (roll <= sum)");
        Check(WildLotSkipCore.Pick(w, none, 43f) == 3, "no skips: last lot at the total");
        bool[] skip1 = { false, true, false, false };
        Check(WildLotSkipCore.Total(w, skip1) == 27f, "total without the skipped lot");
        Check(WildLotSkipCore.Pick(w, skip1, 2.5f) == 2, "skipped lot passed over: next kept lot");
        Check(WildLotSkipCore.Pick(w, skip1, 27f) == 3, "kept total: last kept lot");
        Check(WildLotSkipCore.Pick(w, skip1, 30f) == 0, "roll past the total (float rounding): first kept lot, as EA's first entry");
        bool[] all = { true, true, true, true };
        Check(WildLotSkipCore.Pick(w, all, 1f) == -1, "every lot skipped: -1, EA's pick stays");
        float[] z = { 0f, 3f, 0f };
        bool[] skipZ = { false, true, false };
        Check(WildLotSkipCore.Pick(z, skipZ, 0f) == -1, "only weight-0 lots kept: -1, EA's pick stays");
        Check(WildLotSkipCore.Pick(z, new bool[] { false, false, false }, 0f) == 1, "a weight-0 lot is never picked");
        // exact distribution: every roll over the kept total lands on a kept lot in proportion to its weight
        int[] hits = new int[4];
        for (int i = 0; i < 2700; i++) hits[WildLotSkipCore.Pick(w, skip1, (i + 0.5f) * 27f / 2700f)]++;
        Check(hits[1] == 0 && hits[0] == 200 && hits[2] == 2400 && hits[3] == 100, "kept lots hit in proportion 2:24:1 (" + hits[0] + "," + hits[2] + "," + hits[3] + ")");

        // unweighted fallback list
        bool[] sk = { true, false, true, false, false };
        Check(WildLotSkipCore.Kept(sk) == 3, "kept count");
        Check(WildLotSkipCore.Nth(sk, 0) == 1 && WildLotSkipCore.Nth(sk, 1) == 3 && WildLotSkipCore.Nth(sk, 2) == 4 && WildLotSkipCore.Nth(sk, 3) == -1, "n-th kept lot");

        // long-walk guard
        g.Clear();
        Check(g.Walk(raccoon, 0, 0, 0, walk) == WildLotSkipCore.kWalkNone, "not walking");
        Check(g.Walk(raccoon, 77, lotA, 1000, walk) == WildLotSkipCore.kWalkNew, "first sight of a GoToLot: new");
        Check(g.Walk(raccoon, 77, lotA, 1000 + walk - 1, walk) == WildLotSkipCore.kWalkOn, "one tick short: still walking");
        Check(g.WalkFrom(raccoon) == 1000, "walk start kept");
        Check(g.Walk(raccoon, 77, lotA, 1000 + walk, walk) == WildLotSkipCore.kWalkLong && g.Walks == 0, "too long: reported once, record dropped");
        Check(g.Walk(raccoon, 77, lotA, 1000 + walk + 1, walk) == WildLotSkipCore.kWalkNew, "seen again after the cancel: starts over");
        Check(g.Walk(raccoon, 78, lotB, 1000 + walk + 2, walk) == WildLotSkipCore.kWalkNew && g.WalkFrom(raccoon) == 1000 + walk + 2, "another GoToLot: starts over");
        Check(g.Walk(raccoon, 78, lotB, 5, walk) == WildLotSkipCore.kWalkNew, "clock went back: starts over");
        Check(g.Walk(raccoon, 0, 0, 6, walk) == WildLotSkipCore.kWalkNone && g.Walks == 0, "walk ended: record dropped");
        for (ulong i = 1; i <= (ulong)WildLotSkipCore.kMaxWalks; i++) g.Walk(1000 + i, 5, lotA, 0, walk);
        g.Walk(5000, 5, lotA, 0, walk);
        Check(g.Walks == 1, "walk table full: cleared, new one kept");

        // bounded memo
        g.Clear();
        for (ulong i = 1; i <= (ulong)WildLotSkipCore.kMax; i++) g.Note(horse, i, i <= 10 ? 0 : 9000, span);
        g.Note(horse, 99999, 9000, span);
        Check(g.Count == WildLotSkipCore.kMax - 10 + 1, "memo full: expired pairs dropped first");

        Console.WriteLine(sFails == 0 ? "wildlotskip selftest: ok" : "wildlotskip selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
