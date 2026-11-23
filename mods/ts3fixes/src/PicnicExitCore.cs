namespace Ts3Fixes
{
    // Game-independent rule of picnicexit (PicnicExit.cs, docs/645 §1). Self-check: tools/selftest/picnicexit.
    // A picnic situation is exited only when EA has finished with it and nothing still runs inside it:
    //   - not already exited (Situation.Exit marks the whole chain, so a second Exit would CleanUp / Dispose twice);
    //   - in EA's CleanUpPicnic state and no longer referenced by its basket (CleanUpPicnic.Init sets Basket.Situation null);
    //   - no Sim holds a role in it (BeAtPicnic's CleanUp removed them; anything else means a Sim is still in it);
    //   - no situation interaction still queued (ForceCleanUp's PicnicBasket.CleanUp with its OnFailure callback: Exit would
    //     clear that callback, so EA's AsyncCleanup fallback could no longer run). Such a picnic is tried again later.
    public static class PicnicExitCore
    {
        public const int kExit = 0, kExited = 1, kNotFinished = 2, kSimsPresent = 3, kPending = 4;

        public static int Decide(bool exited, bool cleanUpState, bool basketOwns, int simsWithRole, int pendingInteractions)
        {
            if (exited) return kExited;
            if (!cleanUpState || basketOwns) return kNotFinished;
            if (simsWithRole > 0) return kSimsPresent;
            if (pendingInteractions > 0) return kPending;
            return kExit;
        }

        public static string Name(int d)
        {
            switch (d)
            {
                case kExit: return "exit";
                case kExited: return "already exited";
                case kNotFinished: return "not finished";
                case kSimsPresent: return "sims present";
                case kPending: return "cleanup pending";
                default: return "?";
            }
        }
    }
}
