namespace Ts3Fixes
{
    // Game-independent part of Fixes.SimFrozenOrNotLive (SimFrozen.cs, docs/205 §2.3b). Self-check: tools/selftest/deadrefs.
    public static class SimFrozenCore
    {
        // Real time the clock may stand still while the game runs: one sim step is 1/30 s and any frame of >= 33 ms posts
        // at least one, so 100 ms without a move means the clock is stopped (pause bit, minimized window), not between steps.
        public const int kStillMs = 100;

        // True once the clock has not moved for kStillMs of real time. lastTicks / movedAtMs: the caller's memory (shared
        // by every caller: a second caller in the same frame sees a recent move, not a frozen clock).
        // shortcut: a clock that froze before the first call is seen kStillMs after that call, so a pass started on a
        // minimized window may act for up to 100 ms; read ts3native's GameClock pause bits if that ever matters.
        public static bool ClockStill(long ticks, int nowMs, ref long lastTicks, ref int movedAtMs)
        {
            if (ticks != lastTicks) { lastTicks = ticks; movedAtMs = nowMs; return false; }
            return unchecked(nowMs - movedAtMs) >= kStillMs;  // Environment.TickCount wraps every 49.7 days
        }

        // The whole gate: not live (Buy, Build, CAS, Edit Town, menus outside live mode), travelling or shutting down,
        // paused by game speed, or the clock stopped.
        public static bool FrozenOrNotLive(bool live, bool travellingOrShutdown, bool paused, bool clockStill)
        {
            return !live || travellingOrShutdown || paused || clockStill;
        }

        // Fixes.SfWait and the wildflower task (docs/205 §2.3c): hold queued work this frame while frozen or not live, unless
        // the world is going away (shutdown, travel, out of the world) or the caller has its own exit (a save for wildflowers),
        // so that exit runs instead of a wait that never ends.
        public static bool Hold(bool frozenOrNotLive, bool leaving, bool callerExit)
        {
            return frozenOrNotLive && !leaving && !callerExit;
        }
    }
}
