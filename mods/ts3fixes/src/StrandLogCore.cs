namespace Ts3Fixes
{
    // Game-independent part of the strand lines (StrandLog.cs, docs/183 §8), checked offline by tools/selftest/strandlog.
    public static class StrandCore
    {
        public const int kPerSim = 3, kEvery = 25, kMaxLines = 240, kMaxPrivacy = 4, kMaxStairs = 4, kMaxNear = 4;
        // InteractionPriorityLevel as EA's PrivacySituation.OnPortalPathPlan compares it
        public const int kUserDirected = 3, kPrivacy = 5;

        // A failed plan of an active-household Sim to another level of the lot it stands on: no path or no usable goal.
        // (A goal on the same level fails for many ordinary reasons; a Sim on the street has no stair to name.)
        public static bool Wants(bool on, bool activeHousehold, bool noPathOrNoGoal, bool onLot, bool sameLot, int simLevel, int goalLevel)
        {
            return on && activeHousehold && noPathOrNoGoal && onLot && sameLot && simLevel != goalLevel;
        }

        // The Sim's n-th such fail (1-based): the first kPerSim, then every kEvery-th, so a household stuck for hours still
        // shows whether the state changed.
        public static bool Due(int failsOfSim)
        {
            return failsOfSim >= 1 && (failsOfSim <= kPerSim || failsOfSim % kEvery == 0);
        }

        // EA's rule (PrivacySituation.OnPortalPathPlan): a portal into the room the situation's Sim stands in is locked for
        // a Sim with an interaction, unless the situation lets that Sim ignore it. An autonomous interaction is locked up
        // to priority Privacy, a pushed or user one only below UserDirected. No interaction (current or transition): open.
        public static bool PrivacyLocks(bool hasInteraction, bool autonomous, int priority, bool canIgnore)
        {
            if (!hasInteraction || canIgnore) return false;
            return autonomous ? priority <= kPrivacy : priority < kUserDirected;
        }

        // One stair lane end for the line: BlockedStatus 0 = free, else what EA's slot test named.
        public static string Blocked(int status)
        {
            switch (status)
            {
                case 0: return "free";
                case 1: return "sim";
                case 2: return "vehicle";
                case 3: return "object";
                case 4: return "wall";
                case 5: return "other";
                case 6: return "other reason";
                default: return "?" + status;
            }
        }

        // Does a stair between these two levels lie on the way from simLevel to goalLevel (either direction)?
        public static bool StairOnWay(int bottomLevel, int topLevel, int simLevel, int goalLevel)
        {
            int lo = simLevel < goalLevel ? simLevel : goalLevel, hi = simLevel < goalLevel ? goalLevel : simLevel;
            int b = bottomLevel < topLevel ? bottomLevel : topLevel, t = bottomLevel < topLevel ? topLevel : bottomLevel;
            return b >= lo && t <= hi && b != t;
        }
    }
}
