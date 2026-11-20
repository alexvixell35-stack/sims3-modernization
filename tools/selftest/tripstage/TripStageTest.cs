using System;
using Ts3Fixes;

// Offline self-check of TripStageCore (stagedarrival, icecreamdirect, nearvenue).
static class TripStageTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }
    static bool Near(float a, float b) { return Math.Abs(a - b) < 1e-4f * Math.Max(1f, Math.Abs(b)); }

    static void Main()
    {
        // staging candidates: every one at least kStageMinGainM short of the Sim, the first kStageFirstM from the destination
        Check(TripStageCore.StageCount(30f) == 0 && TripStageCore.StageCount(float.NaN) == 0 && TripStageCore.StageCount(-5f) == 0, "no room: none");
        Check(TripStageCore.StageCount(31f) == 1, "just room for one");
        Check(TripStageCore.StageCount(100f) == 8 && Near(TripStageCore.StageAt(7), 80f), "100 m: 10..80 m");
        Check(TripStageCore.StageCount(5000f) == TripStageCore.kStageMaxCand, "capped");
        for (float d = 0f; d < 600f; d += 0.7f)
        {
            int n = TripStageCore.StageCount(d);
            for (int i = 0; i < n; i++)
                if (TripStageCore.StageAt(i) > d - TripStageCore.kStageMinGainM + 1e-3f || TripStageCore.StageAt(i) < TripStageCore.kStageFirstM) { Check(false, "candidate " + i + " of trip " + d); break; }
        }

        // gravity: equal lots keep their summed score; nearer gets more; water costs; clamps hold
        float w1 = TripStageCore.Weight(100f, false), w2 = TripStageCore.Weight(100f, false);
        Check(Near(TripStageCore.Factor(w1, w1 + w2, 2), 1f), "two equal lots: factor 1 each");
        float near = TripStageCore.Weight(100f, false), far = TripStageCore.Weight(400f, false), sum = near + far;
        float fn = TripStageCore.Factor(near, sum, 2), ff = TripStageCore.Factor(far, sum, 2);
        Check(fn > 1f && ff < 1f, "near raised, far lowered");
        Check(Near(fn + ff, 2f), "unclamped factors sum to the group size");
        Check(TripStageCore.Weight(300f, true) < TripStageCore.Weight(300f, false), "water costs");
        Check(TripStageCore.Weight(500f, false) > TripStageCore.Weight(300f, true), "a 300 m crossing weighs less than a 500 m walk");
        Check(Near(TripStageCore.Weight(1f, false), TripStageCore.Weight(TripStageCore.kNvMinDistM, false)), "distance floor");
        Check(TripStageCore.Factor(1f, 1.0001f, 2) <= TripStageCore.kNvMaxFactor, "max clamp");
        Check(TripStageCore.Factor(1e-9f, 1f, 2) >= TripStageCore.kNvMinFactor, "min clamp");
        Check(TripStageCore.Factor(w1, w1, 1) == 1f, "alone in its group: EA's score");
        Check(TripStageCore.Factor(0f, 1f, 3) == 1f && TripStageCore.Factor(1f, 0f, 3) == 1f && TripStageCore.Factor(float.NaN, 1f, 3) == 1f
            && TripStageCore.Factor(2f, 1f, 3) == 1f, "bad input: EA's score");

        // stagedarrival lot-entry candidates and colour ring (2026-11-14)
        Check(TripStageCore.EntryOk(10f, 0f, 100f) && TripStageCore.EntryOk(80f, 60f, 100f), "entry at the limits is ok");
        Check(!TripStageCore.EntryOk(9.9f, 0f, 100f) && !TripStageCore.EntryOk(80.1f, 0f, 100f) && !TripStageCore.EntryOk(50f, 60.1f, 100f) && !TripStageCore.EntryOk(50f, float.NaN, 100f), "entry outside the limits is not");
        float rx, rz; int rn = 0;
        while (TripStageCore.ColourRing(rn, out rx, out rz)) { float rr = (float)Math.Sqrt(rx * rx + rz * rz); Check(Near(rr, rn < 8 ? 10f : 20f), "ring radius " + rn); rn++; }
        Check(rn == TripStageCore.kColourRingN && !TripStageCore.ColourRing(-1, out rx, out rz), "ring has 16 points, none before the first");

        // icecreamdirect decision: household lot first, then the truck, then the parking spot
        Check(TripStageCore.IcDecide(true, true, true) == TripStageCore.kIcHhLot && TripStageCore.IcDecide(false, true, true) == TripStageCore.kIcSeenStart
            && TripStageCore.IcDecide(false, false, true) == TripStageCore.kIcSeenGoal && TripStageCore.IcDecide(false, false, false) == TripStageCore.kIcGo, "ice cream truck decision order");

        Console.WriteLine(sFails == 0 ? "tripstage selftest: ok" : "tripstage selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
