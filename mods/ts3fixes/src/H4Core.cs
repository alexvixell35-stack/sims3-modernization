namespace Ts3Fixes
{
    // Game-independent parts of the H4 goal-validation work (docs/250): the gvattr source message (§7.0) and the watchdog's async
    // idle check (§7.1, §7.4), checked offline by tools/selftest/h4core.
    public static class H4Core
    {
        // docs/250 §7.0 gvattr: source class of a plan for ts3native's [GoalValidRO] Attr (UserDiag op 35), in ts3native's order:
        // 0 none (no script task), 1 the watchdog's idle check, 2 the stuck check, 3 the route's own Sim task, 4 another task,
        // 5 another of our own checks (SafeSpot, routable checks, unseen fallback legs)
        public const int kSrcNone = 0, kSrcWatchdog = 1, kSrcStuck = 2, kSrcOwn = 3, kSrcForeign = 4, kSrcOwnCheck = 5;
        public static int SrcClass(bool watchdogCheck, bool stuckCheck, bool ownCheck, bool simTask, bool anyTask)
        {
            if (watchdogCheck) return kSrcWatchdog;
            if (stuckCheck) return kSrcStuck;
            if (ownCheck) return kSrcOwnCheck;
            if (simTask) return kSrcOwn;
            return anyTask ? kSrcForeign : kSrcNone;
        }

        // scope for the same message: 2 = unseen non-household (UfWhy 0 only), 1 = a seen NPC, 0 = household, selected or unknown
        public static int Scope(int ufWhy, bool seenNpc) { return ufWhy == 0 ? 2 : seenNpc ? 1 : 0; }

        public static uint Message(int cls, int scope) { return (uint)(cls & 0xFF) | (uint)(scope & 0xFF) << 8; }

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

        // Watchdog idle stillness (RouteFixes CheckIdle): the start of a Sim's still record moves to the time its interaction
        // queue last became empty when that is later (an interaction ended in place since). A stamp in the future (clock
        // reset, load) or before the record is ignored.
        public static long WdStillSince(long since, long emptiedAt, long now)
        {
            return emptiedAt > since && emptiedAt <= now ? emptiedAt : since;
        }

        // Watchdog [48] stationary reset: every Sim but a horse on open terrain (the world lot), where standing a day is grazing
        // and the reset's placement would take it to its home's front door instead of ground it can route from.
        public static bool WdStationaryResets(bool horse, bool onWorldLot)
        {
            return !(horse && onWorldLot);
        }

        // Watchdog [49] repeat cap (switch watchdogfailcap): a Sim reset twice for failing route plans with no successful plan
        // since is not reset a third time; the reset did not fix what makes its plans fail ([48] still applies).
        public static bool WdFailCapRefuses(int resetsSinceOk)
        {
            return resetsSinceOk >= 2;
        }

        // A successful plan clears that count only once the grace after the last reset is over (the reset's own placement plans
        // succeed from the new spot); a reset stamp in the future (clock reset, load) clears it too.
        public static bool WdFailResetClears(long now, long resetAt, long grace)
        {
            return now - resetAt >= grace || resetAt > now;
        }

        // docs/616 spotasync: SafeSpot's own routability check (SpotVerdict's full check, the trip shortcuts' placement test) sent
        // as an own-task PlanFromPoint job with the watchdog's tag, so ts3native's [RouteAsync] WatchdogKind2 starts it (uncut: the
        // tagged job takes no KindDeadlines cut, so the verdict is the planner's own) and the Sim's task sleeps until READY instead
        // of the sim thread planning ~280 ms. Only when the switch is on, the plan would run anyway (a cache miss within the tick's
        // budget), the check runs in the Sim's own task and may yield, ts3native would start it (q 9), and the Sim is not household,
        // selected or of unknown scope; never inside the watchdog's own check (A1 has its own path). Else today's sync check.
        public static bool SpotAsync(bool on, bool fullCheck, bool ownTask, bool mayYield, bool would, bool household, bool inWatchdog)
        {
            return on && fullCheck && ownTask && mayYield && would && !household && !inWatchdog;
        }

        // After an async spot verdict the Sim's task slept one or more ticks: the caller places the Sim only when it is still
        // there and still unseen, as SafeSpotYield's rule; no yield = today's path. The verdict itself is cached either way.
        public static bool SpotPlaceAfter(bool yielded, bool alive, bool unseen, bool farFromViewers)
        {
            return !yielded || (alive && unseen && farFromViewers);
        }
    }
}
