using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent decisions of docs/330 (our own loop cost): when deadrefsweep's phase groups run, when the viewer
    // table is re-sent, when LotManager.Actors is walked for held riders. Self-check: tools/selftest/loopcost.
    public static class LoopCostCore
    {
        // A deadrefsweep phase group runs on a pass when an event that can make its entries dead fired since its last start,
        // its last run removed something (a trickle keeps it hourly), or capHours sim hours passed since its last finished run.
        public static bool GroupDue(bool dirty, bool foundLast, float hoursSince, float capHours)
        {
            return dirty || foundLast || hoursSince >= capHours;
        }

        // The viewer table (x, y, z per viewer, in order) is sent again when the viewer count changed, any viewer moved more
        // than moveM from where it was last sent, or beatMs passed since the last send (ts3native drops a table older than
        // 2000 ms). sent == null or msSince < 0 = never sent.
        public static bool ViewersDue(List<float> sent, List<float> cur, float moveM, long msSince, long beatMs)
        {
            if (sent == null || msSince < 0 || msSince >= beatMs || sent.Count != cur.Count) return true;
            float m2 = moveM * moveM;
            for (int i = 0; i + 2 < cur.Count; i += 3)
            {
                float dx = cur[i] - sent[i], dy = cur[i + 1] - sent[i + 1], dz = cur[i + 2] - sent[i + 2];
                if (dx * dx + dy * dy + dz * dz > m2) return true;
            }
            return false;
        }

        // LotManager.Actors is walked for rides on every wake while the last walk (or the household) had a candidate, else
        // on every sparse-th wake: a ride that starts with none in progress is found within sparse wakes.
        public static bool HeldWalkDue(bool sawAny, int wake, int sparse)
        {
            return sawAny || sparse <= 1 || wake % sparse == 0;
        }

        // Same ids in the same order (the native table is replaced whole).
        public static bool SameIds(List<ulong> a, List<ulong> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
