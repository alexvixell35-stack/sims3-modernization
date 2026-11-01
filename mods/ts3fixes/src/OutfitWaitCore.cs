namespace Ts3Fixes
{
    // Game-independent part of outfitwaitpoll (OutfitWait.cs, issue #115): how many ticks a Sim.SwitchOutfitHelper wait
    // sleeps before it reads mReady again. Self-check: tools/selftest/outfitwait.
    public static class OutfitWaitCore
    {
        // EA's 1 tick; one sim-minute once the wait has run staleAfter ticks and the switch is on.
        public static uint SleepTicks(long waited, long staleAfter, uint minute, bool on)
        {
            return on && waited >= staleAfter ? minute : 1u;
        }

        public const int kCauseAborted = 0, kCauseOwn = 1, kCauseReplaced = 2, kCauseCleared = 3;
        // Why a wait is stale (docs/224 §5), from the helper and its Sim when the wait is marked:
        // aborted: Abort cleared mReady (the #115 cause). cleared: the Sim has no pending outfit, so its delayed swap was
        // completed or cancelled without this helper. own: the Sim's pending outfit is still this helper's and this helper is
        // the Sim's active one, so the game has not built it yet. replaced: any other state (another delayed swap of the Sim,
        // or another or no active helper).
        public static int StaleCause(bool aborted, bool activeIsThis, int pendingCat, int pendingIdx, int helperCat, int helperIdx, int noneCat)
        {
            if (aborted) return kCauseAborted;
            if (pendingCat == noneCat) return kCauseCleared;
            return activeIsThis && pendingCat == helperCat && pendingIdx == helperIdx ? kCauseOwn : kCauseReplaced;
        }
    }
}
