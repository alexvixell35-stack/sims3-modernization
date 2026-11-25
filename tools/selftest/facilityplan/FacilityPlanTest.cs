using System;
using Ts3Fixes;

// Offline checks of FacilityPlanCore (facilityplan, facilityquick, facilitychain, docs/686 §36).
static class FacilityPlanTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static bool Near(float a, float b) { return Math.Abs(a - b) < 0.05f; }

    static int Main()
    {
        const float Inf = FacilityPlanCore.kInfinite;
        int why;

        // EA's bladder bands: -60 at the live rate, then 14.5/h to -90, 9/h to -100
        Check(Near(FacilityPlanCore.MinutesTo(0f, -60f, -13.33334f), 270f), "0 -> -60 at 13.33/h: 4.5 sim-hours");
        Check(FacilityPlanCore.MinutesTo(-70f, -60f, -14.5f) == 0f, "already below the level: 0");
        Check(FacilityPlanCore.MinutesTo(50f, -60f, 0f) == Inf && FacilityPlanCore.MinutesTo(50f, -60f, float.NaN) == Inf && FacilityPlanCore.MinutesTo(50f, -60f, 5f) == Inf, "no decay (or rising): never");
        Check(Near(FacilityPlanCore.MinutesToAccident(-60f, -14.5f), 30f / 14.5f * 60f + 10f / 9f * 60f), "-60 to the accident: 2.1 + 1.1 sim-hours");
        Check(Near(FacilityPlanCore.MinutesToAccident(-95f, -9f), 5f / 9f * 60f), "inside ReallyHasToPee: 9/h");
        Check(FacilityPlanCore.MinutesToAccident(-100f, -9f) == 0f, "at the minimum: now");
        Check(FacilityPlanCore.MinutesToAccident(20f, 0f) == Inf, "no decay: never");

        // walk, eta, hysteresis
        Check(Near(FacilityPlanCore.Travel(15f, 0), 10f) && Near(FacilityPlanCore.Travel(0f, -1), FacilityPlanCore.kLevelMinutes) && FacilityPlanCore.Travel(float.NaN, 0) == 0f, "travel: metres and floors");
        Check(FacilityPlanCore.Eta(3f, 10f, 5f) == 15f && FacilityPlanCore.Eta(12f, 2f, 0f) == 12f && FacilityPlanCore.Eta(1f, -3f, -1f) == 1f, "eta: walk while the others finish");
        Check(FacilityPlanCore.Better(4f, 12f) && !FacilityPlanCore.Better(10f, 12f) && !FacilityPlanCore.Better(1f, 3.5f) && FacilityPlanCore.Better(20f, 40f) && !FacilityPlanCore.Better(31f, 40f), "better: 3 sim-min and 25 %");
        Check(!FacilityPlanCore.Better(12f, 12f) && !FacilityPlanCore.Better(Inf, 5f), "equal or none: not better");

        // the wait decision: on, human, autonomous, inScope, cooldownOk, curEta, bestEta, bestFree, minutesToUrgent, minutesToAccident
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, 4f, false, 300f, Inf, out why) == FacilityPlanCore.kSwitch, "a sooner object (busy or not): switch");
        Check(FacilityPlanCore.Decide(false, true, true, true, true, 12f, 4f, false, 300f, Inf, out why) == FacilityPlanCore.kWouldSwitch, "off: would switch");
        Check(FacilityPlanCore.Decide(true, true, true, false, true, 12f, 4f, false, 300f, Inf, out why) == FacilityPlanCore.kExempt, "household without facilityplanhh: exempt");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, 10f, false, 300f, Inf, out why) == FacilityPlanCore.kDefer, "small gain, not urgent, long wait: something else first");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, Inf, false, 300f, Inf, out why) == FacilityPlanCore.kDefer, "only object, not urgent: something else first");
        Check(FacilityPlanCore.Decide(false, true, true, true, true, 12f, Inf, false, 300f, Inf, out why) == FacilityPlanCore.kWouldDefer, "off: would defer");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 6f, Inf, false, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyShort, "short wait: stay");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, Inf, false, 20f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyUrgent, "urgent, nothing better: stay in the line");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, Inf, false, 0f, 5f, out why) == FacilityPlanCore.kKeepEa && why == FacilityPlanCore.kWhyUrgentNoBetter, "accident before the turn, nothing better: EA's way");
        Check(FacilityPlanCore.Decide(true, true, true, true, true, 12f, 3f, false, 0f, 5f, out why) == FacilityPlanCore.kSwitch, "accident close but a sooner object: go there");
        Check(FacilityPlanCore.Decide(true, false, true, true, true, 12f, 4f, true, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyNotHuman, "pets: EA's");
        Check(FacilityPlanCore.Decide(true, true, false, true, true, 12f, 4f, true, 300f, Inf, out why) == FacilityPlanCore.kSwitch, "player's order: free twin after 10 sim-min (lineswitch's rule)");
        Check(FacilityPlanCore.Decide(true, true, false, true, true, 8f, 1f, true, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyPlayer, "player's order, short wait: kept");
        Check(FacilityPlanCore.Decide(true, true, false, true, true, 20f, 4f, false, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyPlayer, "player's order, the other one busy: kept");
        Check(FacilityPlanCore.Decide(true, true, false, true, true, 20f, Inf, false, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyPlayer, "player's order is never deferred");
        Check(FacilityPlanCore.Decide(true, true, true, true, false, 12f, 4f, false, 300f, Inf, out why) == FacilityPlanCore.kWait && why == FacilityPlanCore.kWhyCooldown, "cooldown: no second move");
        Check(FacilityPlanCore.Urgent(20f, 10f) && !FacilityPlanCore.Urgent(26f, 10f), "urgent: within the wait plus 15");

        // pick factor: eta, bestEta, urgent
        Check(FacilityPlanCore.PickFactor(12f, 2f, 0f, false) == FacilityPlanCore.kPickLow, "pick: a clearly sooner object: x0.1");
        Check(FacilityPlanCore.PickFactor(2f, 2f, 0f, false) == 1f && FacilityPlanCore.PickFactor(4f, 2f, 0f, true) == 1f, "pick: the best one (or close): EA's score");
        Check(FacilityPlanCore.PickFactor(12f, 9f, 9f, false) == FacilityPlanCore.kPickDefer, "pick: all busy past the walk, not urgent: another action first");
        Check(FacilityPlanCore.PickFactor(12f, 12f, 0f, false) == 1f, "pick: a long walk to a free one is not late (s590: free toilets 10 sim-min away were deferred)");
        Check(FacilityPlanCore.PickFactor(11f, 9f, 9f, true) == 1f && FacilityPlanCore.PickFactor(20f, 9f, 9f, true) == FacilityPlanCore.kPickLow, "pick: urgent: never deferred, still the sooner one");

        // facilityquick: kind, value, minutes so far, waiterUrgent
        Check(FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 70f, 5f, true) && FacilityPlanCore.QuickEnd(FacilityPlanCore.kBath, 90f, 12f, true), "quick: good enough and old enough");
        Check(!FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 69f, 10f, true), "quick: not clean enough");
        Check(!FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 95f, 4.9f, true), "quick: never before the minimum");
        Check(!FacilityPlanCore.QuickEnd(FacilityPlanCore.kShower, 95f, 10f, false), "quick: nobody urgent waits (a player's shower stays whole)");
        Check(FacilityPlanCore.QuickEnd(FacilityPlanCore.kToilet, 75f, 6f, true) && !FacilityPlanCore.QuickEnd(FacilityPlanCore.kSink, 100f, 6f, true), "quick: toilet by the bladder, never a sink");
        Check(FacilityPlanCore.WaiterUrgent(30f) && FacilityPlanCore.WaiterUrgent(0f) && !FacilityPlanCore.WaiterUrgent(31f), "quick: waiter urgent within 30");
        Check(FacilityPlanCore.kQuickHygiene > -60f && FacilityPlanCore.kQuickHygiene < 81f, "quick: the bar sits in EA's neutral band");

        // facilitychain: otherValue, otherMinutesToUrgent, otherMax
        Check(FacilityPlanCore.ChainDue(-10f, 500f, 100f) && FacilityPlanCore.ChainDue(40f, 100f, 100f), "chain: low or due within 2 sim-hours");
        Check(!FacilityPlanCore.ChainDue(40f, 300f, 100f) && !FacilityPlanCore.ChainDue(100f, 0f, 100f), "chain: not due, or full");

        // remaining, prior, learn
        Check(Near(FacilityPlanCore.Remaining(100f, 40f, 4f, 30f, 10f), 15f), "remaining from the gain seen");
        Check(FacilityPlanCore.Remaining(100f, 40f, 0f, 30f, 10f) == 20f && FacilityPlanCore.Remaining(100f, 40f, 0f, 30f, 40f) == 0f, "remaining from the prior");
        Check(FacilityPlanCore.Prior(FacilityPlanCore.kToilet) == 30f && FacilityPlanCore.Prior(FacilityPlanCore.kSink) > 0f, "priors");
        Check(FacilityPlanCore.Learn(0f, 12f) == 12f && Near(FacilityPlanCore.Learn(12f, 20f), 14f) && FacilityPlanCore.Learn(12f, 0.1f) == 12f && FacilityPlanCore.Learn(12f, 500f) == 12f, "learn: first, mean, outliers dropped");

        Console.WriteLine(sFail == 0 ? "facilityplan selftest: " + sRun + " checks ok" : "facilityplan selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
