namespace Ts3Fixes
{
    // Game-independent rules of SeatClash.cs (seatclash / seatclashhh / seatclashlog, docs/686 §42), checked offline by
    // tools/selftest/seatclash. Scope (who a relaxation covers) is HhSharedCore.Scope, the rule every household fix shares.
    public static class SeatClashCore
    {
        // How one seat approach (RouteToForSitting) ended, first cause wins: seated; another Sim sat down first; a Sim
        // behind the chair could not be pushed away (or the push wait ran out); the pull-out test refused; anything else
        // (the walk failed, the chair left its table).
        public const int kOk = 0, kTaken = 1, kPushFailed = 2, kScoot = 3, kOther = 4, kExits = 5;
        public static readonly string[] ExitNames = { "seated", "taken on arrival", "push failed", "pull-out refused", "walk failed / other" };

        public static int Exit(bool ok, bool pushFailed, bool takenByOther, bool scootRefused)
        {
            if (ok) return kOk;
            if (pushFailed) return kPushFailed;
            if (takenByOther) return kTaken;
            if (scootRefused) return kScoot;
            return kOther;
        }

        // The game plays its route-fail shrug at the chair for these (a bar stool taken on arrival ends without one).
        public static bool Shrug(int exit, bool stool) { return exit == kPushFailed || exit == kScoot || (exit == kTaken && !stool); }

        // The chair pick at an eating surface, EA's rule (EatingSurfaceImpl.GetClosestFreeSlotPair) over the chairs that
        // are free AND not held by another Sim: with wantFood the nearest chair whose food slot is free wins, else the
        // nearest chair. -1: every free chair is held (the table is full for this Sim: no walk, no shrug).
        public static int Pick(float[] dist, bool[] free, bool[] foodFree, int n, bool wantFood, out bool hasFood)
        {
            int best = -1, bestFood = -1;
            for (int i = 0; i < n; i++)
            {
                if (!free[i]) continue;
                if (best < 0 || dist[i] < dist[best]) best = i;
                if (foodFree[i] && (bestFood < 0 || dist[i] < dist[bestFood])) bestFood = i;
            }
            if (wantFood && bestFood >= 0) { hasFood = true; return bestFood; }
            hasFood = best >= 0 && foodFree[best];
            return best;
        }

        // The push wait behind the chair (PushSimsFromFootprint, waitForPushes): 0 keep waiting, 1 the area is clear (the
        // neighbour stepped aside, EA's own BePushed walk), 2 the grace is over: the chair pulls out past the neighbour (a
        // brief partial overlap, no walk through a standing Sim). Unseen (camsafe): no grace.
        public const int kWait = 0, kClear = 1, kGraceOver = 2;
        public static int PushStep(bool clear, bool unseen, float elapsedMin, float graceMin)
        {
            if (clear) return kClear;
            if (unseen || elapsedMin >= graceMin) return kGraceOver;
            return kWait;
        }

        // The pull-out test after a push wait that the grace ended: allowed when the game refused it and it passes with
        // every Sim allowed (only Sims are in the way). Furniture, walls and doors stay refused (doorseat / seatreach).
        public static bool ScootAllow(bool eaResult, bool graceEnded, bool passesWithSims, int scope)
        {
            return !eaResult && graceEnded && passesWithSims && scope == HhSharedCore.kGo;
        }

        // A hold or approach record older than this is stale (a reset or a load ended the walk without its exit).
        public const float kStaleMin = 60f;
        public static bool Stale(float ageMin) { return !(ageMin >= 0f && ageMin < kStaleMin); }
    }
}
