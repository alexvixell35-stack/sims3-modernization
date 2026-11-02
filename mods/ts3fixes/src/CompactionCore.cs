using System;

namespace Ts3Fixes
{
    // Game-free rules of the Compaction (Compaction.cs, docs/204), shared with the offline self-check
    // (tools/selftest/compaction).
    public static class CompactionCore
    {
        public const float kNearCameraM = 30f;   // §5 scope: Sims within this of the camera target
        public const float kObjectMatchM = 0.5f; // §5: an object target is found again by type + lot, nearest within this
        public const float kCameraCheckM = 2f;   // §4: the save restores the camera; set it ourselves only when off by more
        public const int kQueueMax = 8;          // §5: queue entries recorded per Sim, head first
        public const int kRestoreMaxAgeMs = 10 * 60 * 1000; // a pending restore older than this (real time) is dropped

        // §5: Sims the player can see or would notice
        public static bool InScope(bool activeHousehold, bool onActiveLot, float distToCameraTarget)
        {
            return activeHousehold || onActiveLot || distToCameraTarget <= kNearCameraM;
        }

        // The "~N s" of the offer: the last world load's real time rounded up to 10 s (60 s when unknown)
        public static int EstimateSeconds(int lastLoadMs)
        {
            if (lastLoadMs <= 0) return 60;
            int s = (lastLoadMs + 999) / 1000;
            return Math.Max(10, (s + 9) / 10 * 10);
        }

        public static string PromptText(int seconds)
        {
            return "Memory is getting low. Compact now? (save and reload, ~" + seconds + " s)";
        }

        public const string kLoadingText = "Compacting... (your game is saved)";
        public const string kStillLowText = "Memory is still low after compacting; restart the game to get the most room.";
        public const string kSkippedText = "Compaction skipped; your game is saved. Compaction is off until the game restarts.";

        public static string DoneText(int gainMB)
        {
            return gainMB > 0 ? "Memory compacted: +" + gainMB + " MB of headroom" : "Memory compacted";
        }

        // Index of the nearest candidate within maxM (distances in metres), -1 when none
        public static int PickNearest(float[] dist, float maxM)
        {
            int best = -1;
            for (int i = 0; i < dist.Length; i++)
                if (dist[i] <= maxM && (best < 0 || dist[i] < dist[best])) best = i;
            return best;
        }

        // Value from the native op 7 bit answers (bit i set when bits[i])
        public static int FromBits(bool[] bits)
        {
            int v = 0;
            for (int i = 0; i < bits.Length && i < 16; i++) if (bits[i]) v |= 1 << i;
            return v;
        }
    }
}
