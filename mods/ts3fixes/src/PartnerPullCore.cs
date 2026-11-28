namespace Ts3Fixes
{
    // Game-independent rules of partnerpull (PartnerPull.cs, docs/686 §21). Offline self-check: tools/selftest/partnerpull.
    public static class PartnerPullCore
    {
        // what a waiter's partner looks like at a pass
        public const int kWaitGrace = 0, kPull = 1, kKeepPlayer = 2, kKeepBusy = 3, kKeepRank = 4, kKeepTrip = 5, kKeepSocial = 6;
        // the kinds of wait
        public const int kReadToSleep = 0, kSocial = 1;
        public const float kGraceMinutes = 1f;  // a hand-off quicker than this is left alone

        // One waiter whose linked half is queued (not running) on the partner. `headAuto`: the partner's head was queued by
        // autonomy; `aheadAuto`: every entry between the head and the linked half was too. `headBusy`: the head cannot be
        // cancelled now (not cancellable by the player, must run, a posture transition on the partner, the head is linked
        // to the waiter itself). `headTrip`: the head takes the partner to another lot (go to lot, visit, go home, any
        // interaction on a Lot). `headSocial`: the head is a social (the partner is in a conversation). `rankCmp`: the linked half's priority compared with the head's (< 0: lower).
        public static int Step(float waitedMinutes, bool headAuto, bool aheadAuto, bool headBusy, bool headTrip, bool headSocial, int rankCmp)
        {
            if (waitedMinutes < kGraceMinutes) return kWaitGrace;
            if (!headAuto || !aheadAuto) return kKeepPlayer;  // never a player's action, never jump the player's queue
            if (headTrip) return kKeepTrip;  // never cut a trip short
            if (headSocial) return kKeepSocial;  // never break into another conversation
            if (headBusy) return kKeepBusy;
            if (rankCmp < 0) return kKeepRank;  // the head outranks the waiter's half (EA would not interrupt it either)
            return kPull;
        }

        // Off = compare mode: the case is counted, nothing is done.
        public static bool Act(bool on) { return on; }
    }
}
