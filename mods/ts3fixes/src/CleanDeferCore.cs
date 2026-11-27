namespace Ts3Fixes
{
    // Game-independent rules of CleanDefer.cs (cleandefer / cleandeferhh / cleandeferlog, docs/686 §45), checked offline
    // by tools/selftest/cleandefer. A Sim's own pick to clean a toilet gives way while other Sims wait for that toilet;
    // an ordered clean never does, and a toilet is not left dirty for ever.
    public static class CleanDeferCore
    {
        public const int kClean = 0, kOrdered = 1, kDefer = 2, kWould = 3, kCap = 4, kExempt = 5;
        public const float kCapMinutes = 120f;  // after this long of giving way the toilet is cleaned anyway

        // waiters: other Sims in the toilet's line, with the toilet as a queued or claimed target, or (household, at home)
        // with a bladder need at bathroomwait's level. deferredMin: since this toilet's first deferral (0: none yet).
        public static int Decide(bool ordered, int waiters, float deferredMin, int scope)
        {
            if (waiters <= 0) return kClean;
            if (ordered) return kOrdered;
            if (scope == HhSharedCore.kExempt) return kExempt;
            if (scope != HhSharedCore.kGo) return kWould;
            return deferredMin >= kCapMinutes ? kCap : kDefer;
        }
    }
}
