using System.Collections.Generic;

namespace Ts3Fixes
{
    // eventcost (docs/545 §5 "listener cost"): per event id, how often EventTracker.ProcessEvent ran and how long it took.
    // ProcessEvent nests (a listener sends another event), so each row keeps the inclusive time and the exclusive time (minus
    // the nested ProcessEvent calls under it). Row layout: [0] calls, [1] inclusive us, [2] exclusive us, [3] max inclusive us.
    // No game types here: tools/selftest/eventcost checks it offline.
    public static class EventCostCore
    {
        public const int kCalls = 0, kIncl = 1, kExcl = 2, kMax = 3, kWidth = 4;
        static long sChild;  // time of the finished nested calls under the open call (plain static: EA's Mono has no thread statics)

        // Before the original call: returns the outer call's child time so far, to hand back to Exit.
        public static long Enter() { long saved = sChild; sChild = 0; return saved; }

        // After the original call (also when it threw). us = inclusive time of this call.
        public static void Exit(Dictionary<ulong, long[]> table, ulong id, long us, long saved)
        {
            if (us < 0) us = 0;  // a wrapped clock: count the call, not a negative time
            long excl = us - sChild;
            if (excl < 0) excl = 0;
            long[] row;
            if (!table.TryGetValue(id, out row)) { row = new long[kWidth]; table[id] = row; }
            row[kCalls]++;
            row[kIncl] += us;
            row[kExcl] += excl;
            if (us > row[kMax]) row[kMax] = us;
            sChild = saved + us;  // this call is a finished child of the outer one
        }

        public static void Reset(Dictionary<ulong, long[]> table) { table.Clear(); sChild = 0; }
    }
}
