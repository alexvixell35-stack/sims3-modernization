namespace Ts3Fixes
{
    // Game-independent parts of the watchdog's async idle check (docs/250 §7.1, §7.4), checked offline by tools/selftest/h4core.
    public static class H4Core
    {
        // docs/250 §7.4: what StuckWatchdog.CheckIdle does with one queued Sim's route check. Cached verdict: no plan. Else, with a
        // job holding +0x918 and watchdoglockskip on, skip the Sim this pass (2026-11-10: checked before A1, which used to win
        // and disabled the skip, docs/247 §12.4). Else A1 (watchdogasync, the check as an own-task job on the route worker) when
        // on and ts3native would start it, never for a household, selected or unknown-scope Sim, lock held or not. Else the sync check.
        public const int kWdSync = 0, kWdA1 = 1, kWdSkip = 2;
        public static int WdRoute(bool cached, bool lockHeld, bool a1On, bool a1Would, bool lockSkip, bool household)
        {
            if (cached) return kWdSync;
            if (lockHeld && lockSkip) return kWdSkip;
            if (a1On && a1Would && !household) return kWdA1;
            return kWdSync;
        }

        // An A1 verdict arrives one or more ticks after the check started. The watchdog acts on it only when the Sim is still
        // exactly as checked: alive and in the world, still not household / selected / unknown scope, on the checked spot (1 cm),
        // in the same interaction, on the same lot, standing and not routing. Anything else drops the verdict ([930] watchdogasync_stale) and
        // the next pass checks again, so no action ever uses state older than the check.
        public const float kWdSameSpot = 0.01f;
        public static bool WdFresh(bool alive, bool household, float moved, bool sameInteraction, bool sameLot, bool standing, bool routing)
        {
            return alive && !household && moved <= kWdSameSpot && sameInteraction && sameLot && standing && !routing;
        }
    }
}
