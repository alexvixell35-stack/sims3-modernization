using System;

namespace Ts3Fixes
{
    // Game-independent parts of stagedarrival (TripStage.cs), icecreamdirect (IceCreamDirect.cs) and nearvenue (NearVenue.cs),
    // stagedarrival default off, icecreamdirect and nearvenue default ON. Self-check: tools/selftest/tripstage.
    public static class TripStageCore
    {
        // ---- stagedarrival: candidate street points from the destination back along the straight line to the Sim ----
        // first candidate kStageFirstM from the destination, then every kStageStepM; the last one stays kStageMinGainM short of
        // the Sim (a staging point next to the Sim saves nothing); kStageMaxCand caps the search (one walkable check each)
        public const float kStageFirstM = 10f, kStageStepM = 10f, kStageMinGainM = 20f;
        public const int kStageMaxCand = 24;
        public static int StageCount(float distM)
        {
            if (!(distM > kStageFirstM + kStageMinGainM)) return 0;
            int n = (int)((distM - kStageMinGainM - kStageFirstM) / kStageStepM) + 1;
            return n > kStageMaxCand ? kStageMaxCand : n;
        }
        public static float StageAt(int i) { return kStageFirstM + i * kStageStepM; }
        // a Sim staged again within this many sim-minutes is in a loop the game drives: let it walk; the table is pruned at the cap
        public const float kStageLoopMin = 10f;
        public const int kStageGuardCap = 256;

        // ---- stagedarrival, second source of candidates (local 2026-11-14): the street side of lots near the line ----
        // The straight line from the destination to the Sim almost never meets a street at a 10 m step (it runs over yards and
        // houses, each refused as "inside a lot"), so the lots' own entry points (street side) near the line are tried too.
        // along = metres from the destination towards the Sim, lateral = metres off that line.
        public const float kStageLateralM = 60f;
        public const int kStageMaxEntry = 24;
        public static bool EntryOk(float along, float lateral, float distM)
        {
            return along >= kStageFirstM && along <= distM - kStageMinGainM && lateral >= 0f && lateral <= kStageLateralM;
        }
        // destination colour unknown (no waypoint within the native 12 m): the colour is read again at kColourRingN points around the
        // destination, 8 directions at 10 m then 8 at 20 m (turned half a step), nearest first; false past the last point
        public const int kColourRingN = 16;
        public static bool ColourRing(int i, out float dx, out float dz)
        {
            dx = dz = 0f;
            if (i < 0 || i >= kColourRingN) return false;
            double a = (i % 8) * 0.7853981633974483 + (i / 8) * 0.39269908169872414;
            float r = i < 8 ? 10f : 20f;
            dx = r * (float)Math.Cos(a);
            dz = r * (float)Math.Sin(a);
            return true;
        }
        // refusal reasons of one staging candidate
        public const int kCandLot = 0, kCandGround = 1, kCandSeen = 2, kCandColour = 3, kCandRoute = 4, kCandReasons = 5;

        // ---- icecreamdirect (IceCreamDirect.cs) ----
        public const int kIcGo = 0, kIcHhLot = 1, kIcSeenStart = 2, kIcSeenGoal = 3;
        // one answer for a truck's trip: the active household's lot first, then where the truck is, then where it parks
        public static int IcDecide(bool hhLot, bool startSeen, bool goalSeen)
        {
            if (hhLot) return kIcHhLot;
            if (startSeen) return kIcSeenStart;
            return goalSeen ? kIcSeenGoal : kIcGo;
        }

        // ---- nearvenue: gravity weight of a candidate lot of the venue type ----
        // 1/(d + water)^2; d is floored at kNvMinDistM so a lot next door does not take the whole type; a lot whose entry point
        // is proven in another flood colour than the Sim (no land path) counts kNvWaterPenaltyM farther
        public const float kNvMinDistM = 40f, kNvWaterPenaltyM = 400f, kNvMinFactor = 0.1f, kNvMaxFactor = 4f;
        public const int kNvGroupMax = 32;
        public static float Weight(float distM, bool water)
        {
            float d = (distM > kNvMinDistM ? distM : kNvMinDistM) + (water ? kNvWaterPenaltyM : 0f);
            return 1f / (d * d);
        }
        // the factor on EA's score of one lot: its share of the group's weight times the group size, so lots EA scores alike keep
        // their summed score (the Sim visits the venue type as often as before, only nearer); clamped to [kNvMinFactor, kNvMaxFactor].
        // A lot alone in its group, or bad input, keeps EA's score.
        public static float Factor(float w, float sumW, int n)
        {
            if (n < 2 || !(sumW > 0f) || !(w > 0f) || w > sumW) return 1f;
            float f = n * w / sumW;
            return f < kNvMinFactor ? kNvMinFactor : f > kNvMaxFactor ? kNvMaxFactor : f;
        }
    }
}
