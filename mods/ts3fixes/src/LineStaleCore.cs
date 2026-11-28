namespace Ts3Fixes
{
    // Game-independent rules of linestale (LineStale.cs, docs/686 §29). Offline self-check: tools/selftest/linestale.
    public static class LineStaleCore
    {
        // The head of a line, as a waiter sees it on its per-minute check.
        public const int kFresh = 0;   // its action runs (walking over, from any distance, or using the object), or it uses the object
        public const int kQueued = 1;  // the action that took the turn is in its queue but does not run (another action runs first)
        public const int kGone = 2;    // that action ended without leaving the line, or the Sim is gone
        public const int kUnknown = 3; // no record of how it took the turn: left alone
        public const int kStrayUser = 4; // still in the object's use list, its action over, and away from the object
        public const float kUseNearMeters = 3f;  // a user closer than this to its object is using it (a seat, a counter spot)

        public const int kKeep = 0, kWait = 1, kWould = 2, kDrop = 3;
        public const float kMinMinutes = 1f, kMaxMinutes = 60f;

        public static float After(float tuned)
        {
            if (!(tuned >= kMinMinutes)) return kMinMinutes;  // NaN too
            return tuned > kMaxMinutes ? kMaxMinutes : tuned;
        }

        // `known`: the turn was taken through SimQueue.WaitForTurn while the fix watched. `destroyed`: the head Sim is gone.
        // `usesObject`: the head is in the object's use list; `dist`: its distance to the object (m, < 0 unknown).
        // `holderRuns`: the action that took the turn (or the action that ran it inline) is the Sim's running action.
        // `holderQueued`: one of them is still in the Sim's queue.
        public static int State(bool known, bool destroyed, bool usesObject, float dist, bool holderRuns, bool holderQueued)
        {
            if (!known) return kUnknown;
            if (destroyed) return kGone;
            if (holderRuns) return kFresh;
            if (usesObject && (dist < 0f || dist < kUseNearMeters)) return kFresh;  // sits on it, stands at it: a user
            if (holderQueued) return kQueued;
            return usesObject ? kStrayUser : kGone;
        }

        // `staleMinutes`: how long the head has been in a stale state without a break. `inScope`: the head is an NPC, or a
        // household / selected Sim with linestalehh. Off = compare mode: would.
        public static int Decide(bool on, bool inScope, int state, float staleMinutes, float tuned)
        {
            if (state == kFresh || state == kUnknown) return kKeep;
            if (!(staleMinutes >= After(tuned))) return kWait;
            if (!on) return kWould;
            return inScope ? kDrop : kKeep;
        }
    }
}
