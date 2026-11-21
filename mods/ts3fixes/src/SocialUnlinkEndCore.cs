namespace Ts3Fixes
{
    // Game-independent rule of socialunlinkend (SocialUnlinkEnd.cs, docs/650 §3). Self-check: tools/selftest/socialunlinkend.
    // A Receiver's sync wait inside a SocialInteractionB whose link is gone (the starter's SocialInteractionA was dequeued,
    // which nulls both links) cannot succeed once the starter neither syncs with nor targets the receiver any more: the wait
    // needs the starter's SynchronizationTarget to be the receiver. Such a wait for Routed or Committed ends at once with the
    // vanilla failure. Started / Completed waits and every other shape are left to EA; exempt receivers are counted only.
    public static class SocialUnlinkEndCore
    {
        // Sim.SyncLevel values
        public const int kStarted = 1, kRouted = 2, kCommitted = 3, kCompleted = 4;
        // results
        public const int kNotCase = 0, kEnd = 1, kExempt = 2;

        // level: the wait's desired level. receiver: the waiting Sim's SynchronizationRole is Receiver and its
        // SynchronizationTarget is the wait's target. inB: its running interaction is a SocialInteractionB aimed at that
        // target. linked: that B still has a LinkedInteractionInstance. partnerSyncsMe: the target's SynchronizationTarget
        // is the waiting Sim. partnerTargetsMe: the target's current or transition interaction targets the waiting Sim.
        public static int Decide(int level, bool receiver, bool inB, bool linked, bool partnerSyncsMe, bool partnerTargetsMe, bool exempt)
        {
            if (level != kRouted && level != kCommitted) return kNotCase;
            if (!receiver || !inB || linked || partnerSyncsMe || partnerTargetsMe) return kNotCase;
            return exempt ? kExempt : kEnd;
        }
    }
}
