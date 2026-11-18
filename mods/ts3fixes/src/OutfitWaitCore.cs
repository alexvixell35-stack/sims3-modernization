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

        // Household rule (docs/580 §15): a live Sim of the player (household, selected, selectable or unknown scope) keeps
        // EA's per-tick loop; only an NPC's wait, or a wait whose Sim is gone (issue #40), is read once per sim-minute.
        public static bool PollAllowed(bool simLive, bool playerSim)
        {
            return !simLive || !playerSim;
        }

        // issue #40: a stale wait ends when its helper's Sim is gone (null or destroyed), unless the waiting task belongs to
        // another Sim (that Sim keeps EA's loop: a live Sim is never changed).
        public static bool EndDead(bool simGone, bool waiterIsOtherSim)
        {
            return simGone && !waiterIsOtherSim;
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

        // outfitwaitend (docs/583 §1): end a wait EA's loop would hold forever with EA's own instant switch. Only an own-cause
        // wait (the Sim's pending outfit is still this helper's: the native swap was never reported ready) of a live NPC that
        // is off screen, past the end mark, and not inside the helper's ChangeOutfit (whose tail runs DoOutfitDelayedSwap and
        // would set the visible outfit to none after an abort). Everything else keeps EA's loop.
        public static bool EndNow(bool on, long waited, long endAfter, bool liveNpc, bool onScreen, int cause, bool inChangeOutfit)
        {
            return on && waited >= endAfter && liveNpc && !onScreen && cause == kCauseOwn && !inChangeOutfit;
        }
    }
}
