namespace Ts3Fixes
{
    // weddingcleanup decisions (WeddingCleanup.cs), no game types: offline self-check tools/selftest/weddingcleanup.
    public static class WeddingCleanupCore
    {
        public const int kArchUseList = 1;   // the arch is destroyed: drop the couple from its use list directly (no EA RemoveFromUseList)
        public const int kHideGiver = 2;     // RingGiver's queue is gone: EA's CancelAllInteractionsByType would throw on it
        public const int kHideReceiver = 4;  // the same for RingReceiver
        public const int kHideAudience = 8;  // an audience Sim's queue is gone: EA's watch-cancel loop would throw on it

        // What the guard changes before EA's CleanUp body runs; 0 = EA's body runs unchanged. A ring Sim's queue is touched by
        // EA only while the arch is set; audience queues always. A destroyed arch with no ring Sim in its use list needs nothing.
        public static int Plan(bool on, bool archSet, bool archDestroyed, bool coupleUsesArch,
            bool giverSet, bool giverQueueGone, bool receiverSet, bool receiverQueueGone, bool audienceQueueGone)
        {
            if (!on) return 0;
            int p = 0;
            if (archSet && archDestroyed && coupleUsesArch) p |= kArchUseList;
            if (archSet && giverSet && giverQueueGone) p |= kHideGiver;
            if (archSet && receiverSet && receiverQueueGone) p |= kHideReceiver;
            if (audienceQueueGone) p |= kHideAudience;
            return p;
        }
    }
}
