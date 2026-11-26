using System;
using Ts3Fixes;

// Offline checks of NextPlanCore (nextplan, docs/686 §38): who runs EA's pass at once, the claim table, the gap histogram.
static class NextPlanTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        // Decide(on, idle, autonomyOn, eaNow, keepWindow, household, highLod, budgetLeft)
        Check(NextPlanCore.Decide(true, true, true, true, false, true, true, 0) == NextPlanCore.kGo, "household, EA at once: go (no budget needed)");
        Check(NextPlanCore.Decide(true, true, true, false, false, true, false, 0) == NextPlanCore.kGo, "non-active household in EA's delay: go");
        Check(NextPlanCore.Decide(true, true, true, false, true, true, true, 5) == NextPlanCore.kActiveDelay, "active Sim keeps EA's delay");
        Check(NextPlanCore.Decide(true, true, true, true, true, true, true, 5) == NextPlanCore.kGo, "active Sim with EA at once: go");
        Check(NextPlanCore.Decide(true, true, true, false, false, false, true, 5) == NextPlanCore.kActiveDelay, "NPC in a delay keeps it");
        Check(NextPlanCore.Decide(true, true, true, true, false, false, false, 5) == NextPlanCore.kLowLod, "low-LOD NPC: EA's queue");
        Check(NextPlanCore.Decide(true, true, true, true, false, false, true, 0) == NextPlanCore.kBudget, "NPC over the budget");
        Check(NextPlanCore.Decide(true, true, true, true, false, false, true, 1) == NextPlanCore.kGo, "NPC within the budget");
        Check(NextPlanCore.Decide(false, true, true, true, false, true, true, 1) == NextPlanCore.kOff, "switch off");
        Check(NextPlanCore.Decide(true, false, true, true, false, true, true, 1) == NextPlanCore.kNotIdle, "not idle");
        Check(NextPlanCore.Decide(true, true, false, true, false, true, true, 1) == NextPlanCore.kNoAutonomy, "autonomy off");

        NextPlanCore k = new NextPlanCore();
        Check(k.Add(100, 1, 0, 50), "claim");
        Check(k.HeldByOther(100, 2, 10) && !k.HeldByOther(100, 1, 10), "held by 1, not by 2");
        Check(k.Add(200, 1, 10, 50) && k.Holder(100, 20) == 0 && k.TargetOf(1) == 200, "one claim per Sim: the old one goes");
        Check(k.Add(200, 2, 20, 50) && k.TargetOf(1) == 0 && k.Holder(200, 21) == 2, "newer claim of another Sim wins, old holder cleared");
        k.Release(2);
        Check(k.Count == 0 && k.Holder(200, 22) == 0, "release");
        k.Add(300, 3, 0, 10);
        Check(k.Holder(300, 10) == 0 && k.Expired == 1 && k.Count == 0 && k.TargetOf(3) == 0, "expired at the deadline, dropped on read");
        for (ulong i = 0; i < NextPlanCore.kMaxClaims; i++) k.Add(1000 + i, 10 + i, 0, 5);
        Check(k.Count == NextPlanCore.kMaxClaims, "table full");
        Check(k.Add(5000, 9999, 100, 5) && k.Count == 1, "full table: forced sweep drops the expired, new claim fits");
        for (ulong i = 0; i < NextPlanCore.kMaxClaims; i++) k.Add(2000 + i, 20000 + i, 200, 1000);
        Check(!k.Add(9000, 99999, 201, 5) && k.Full == 1, "full of live claims: refused, counted");
        k.Sweep(2000, false);
        Check(k.Count == 0, "sweep drops every expired claim");
        k.Add(1, 1, 3000, 100); k.Sweep(3001, false);
        Check(k.Count == 1, "a sweep within kSweepEvery is skipped, live claim kept");

        // contention: 1, 4 and 8 Sims on a lot with one toilet / shower / sink / fridge / double bed each, and 8 with two bathrooms
        int[] one = { 1, 1, 1, 1, 1 }, two = { 2, 2, 2, 2, 4 };
        Check(NextPlanCore.Level(NextPlanCore.Ratio(1, one)) == NextPlanCore.kLow, "1 Sim: low");
        Check(NextPlanCore.Level(NextPlanCore.Ratio(2, one)) == NextPlanCore.kMid, "2 Sims, one bathroom: mid");
        Check(NextPlanCore.Level(NextPlanCore.Ratio(4, two)) == NextPlanCore.kMid && NextPlanCore.Level(NextPlanCore.Ratio(4, new int[] { 4, 4, 4, 3, 2 })) == NextPlanCore.kLow, "4 Sims: mid with two bathrooms, low with four");
        Check(NextPlanCore.Level(NextPlanCore.Ratio(8, one)) == NextPlanCore.kHigh, "8 Sims, one bathroom: high");
        Check(NextPlanCore.Level(NextPlanCore.Ratio(8, two)) == NextPlanCore.kHigh && Math.Abs(NextPlanCore.Ratio(8, two) - 4f) < 0.01f, "8 Sims, two bathrooms: high (4 per toilet)");
        Check(NextPlanCore.Level(NextPlanCore.Ratio(6, new int[] { 4, 4, 5, 1, 5 })) == NextPlanCore.kMid, "6 Sims, 4 bathrooms, one fridge: mid (fridge 6 / 3 = 2)");
        Check(NextPlanCore.Ratio(3, new int[] { 0, 0, 0, 0, 0 }) == 3f && NextPlanCore.Ratio(0, one) == 0f, "no objects of a kind count as one; no Sims: 0");
        Check(NextPlanCore.ClaimMinutes(NextPlanCore.kLow, 5f) == 2f && NextPlanCore.ClaimMinutes(NextPlanCore.kMid, 5f) == 5f && NextPlanCore.ClaimMinutes(NextPlanCore.kHigh, 5f) == 10f, "claim life 2 / 5 / 10 sim-min");
        Check(NextPlanCore.ClaimFactor(NextPlanCore.kLow, 1f, 0.5f, 0.2f) == 0.5f && NextPlanCore.ClaimFactor(NextPlanCore.kMid, 1f, 0.5f, 0.2f) == 0.5f, "low / mid: EA's route-pending factor");
        Check(NextPlanCore.ClaimFactor(NextPlanCore.kHigh, 1f, 0.5f, 0.2f) == 0.2f, "high: EA's in-use factor");
        Check(NextPlanCore.ClaimFactor(NextPlanCore.kHigh, 0.1f, 0.5f, 0.2f) == 0.1f, "never raises a lower factor");

        NextPlanCore.Hist h = new NextPlanCore.Hist();
        Check(h.P(50) == 0 && h.Text() == "n=0", "empty histogram");
        for (int i = 1; i <= 100; i++) h.Add(i * 10);
        Check(h.P(50) >= 500 && h.P(50) <= 1024, "p50 upper bound in its bin (" + h.P(50) + ")");
        Check(h.P(90) >= 900 && h.P(90) <= 1000, "p90 capped at the max (" + h.P(90) + ")");
        Check(h.Max == 1000 && h.N == 100 && h.Sum == 50500, "max, n, sum");
        h.Add(-5); Check(h.Bins[0] == 1, "negative counts as 0");
        Check(NextPlanCore.Hist.BinOf(long.MaxValue) == NextPlanCore.Hist.kBins - 1, "huge value in the last bin");
        h.Reset(); Check(h.N == 0 && h.Max == 0, "reset");

        Console.WriteLine((sFail == 0 ? "OK" : "FAILED") + " nextplan selftest: " + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
