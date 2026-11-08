namespace Ts3Fixes
{
    // Game-independent part of the tier-2 L0 local-phase probe (AutLocalProbe.cs, switch autt2l0,
    // docs/data/tier2-local-phase-handoff.md §7 L0). Self-check: tools/selftest/autlocalprobe.
    public static class AutLocalProbeCore
    {
        // counter slots (docs/registry.md fixes-903)
        public const int kBase = 903;
        public const int kHh = 0, kSeen = 1, kUnseen = 2;                  // actor class
        public const int kSearches = 903, kUs = 906, kPairSearches = 909, kPairs = 912;  // + class
        public const int kSideN = 915, kSideUs = 916, kNoLocalN = 917, kNoLocalUs = 918;
        public const int kReason = 919;                                     // + reason, unseen non-household local searches only
        public const int kRActiveLot = 0, kRSameLot = 1, kRDaycare = 2, kRResort = 3, kRHousehold = 4, kROther = 5;
        public const int kSpanMs = 925, kNested = 926, kExc = 927;
        public const int kLast = 927;

        // UfWhy code -> class. Household, gone, not live and a failed check (-1) are household (unknown scope = household);
        // kSeen (0) is the only unseen answer.
        public static int Class(int why)
        {
            if (UnseenFallbackCore.HouseholdScope(why)) return kHh;
            return why == UnseenFallbackCore.kSeen ? kUnseen : kSeen;
        }

        // B8's branches in its own order (read-only replica, evaluated after the search): which one holds now. kROther = on
        // screen, a hosted-situation guest, or the 60-tick cached flag from an earlier state.
        public static int Reason(bool sameHousehold, bool lotActive, bool sameNonWorldLot, bool daycare, bool resort)
        {
            if (sameHousehold) return kRHousehold;
            if (lotActive) return kRActiveLot;
            if (sameNonWorldLot) return kRSameLot;
            if (daycare) return kRDaycare;
            if (resort) return kRResort;
            return kROther;
        }
    }
}
