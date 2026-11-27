namespace Ts3Fixes
{
    // Game-independent decisions of StandWaitSocial.cs (docs/686 §30), checked offline by tools/selftest/standwaitsocial.
    //
    // A "people-free pass" is standwaitfix's replan of a blocked stand-and-wait with Route.RouteOption.BlockedByPeople off
    // (StandWait.cs). When it fails, people are not what keeps the Sim from its goal. For the two halves of a social
    // (SocialInteractionA / B) the goal is the social jig spot next to the partner; when that spot cannot be reached
    // without people either, the wait runs to its timeout (20 sim-minutes in the game's tuning) with nothing to wait for.
    // After failedPasses such passes the wait ends the way the game's timeout ends it (route failed): the game's own
    // route-fail answer (or socialreach) comes about 17 sim-minutes sooner.
    public static class StandWaitSocialCore
    {
        public const int kKeep = 0, kGiveUp = 1, kWould = 2, kExempt = 3, kYieldFirst = 4;
        public const int kMinPasses = 1, kMaxPasses = 10;

        public static int Passes(int tuned) { return tuned < kMinPasses ? kMinPasses : tuned > kMaxPasses ? kMaxPasses : tuned; }

        // social: the waiter's running interaction is SocialInteractionA or B. failedPasses: people-free passes that failed in
        // this wait. fixOn: standwaitsocial; inScope: an NPC, or a household / selected Sim with standwaitsocialhh.
        // yieldDue: standwaitfix answers a push or cancel this cycle with the game's own exit, which goes first.
        public static int Decide(bool social, int failedPasses, int tunedPasses, bool fixOn, bool inScope, bool yieldDue)
        {
            if (!social || failedPasses < Passes(tunedPasses)) return kKeep;
            if (!fixOn) return kWould;
            if (!inScope) return kExempt;
            if (yieldDue) return kYieldFirst;
            return kGiveUp;
        }
    }
}
