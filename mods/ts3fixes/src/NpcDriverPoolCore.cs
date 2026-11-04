using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of npcdriverpool (NpcDriverPool.cs, issue #166 build 37, docs/514). Self-check:
    // tools/selftest/npcdriverpool.
    public static class NpcDriverPoolCore
    {
        // Whether a pooled driver description must not be handed out again: no entry, a description that is no longer
        // valid (Dispose clears the flag), or one that left the NPC household (where every driver is made).
        public static bool Bad(bool isNull, bool invalid, bool notNpcHousehold)
        {
            return isNull || invalid || notNpcHousehold;
        }

        // Pops the bad entries off the top of a pool and returns how many. EA reads only the top entry (Peek), so the
        // entries below a good one are left for a later call.
        public static int PruneTop<T>(Stack<T> pool, Predicate<T> bad)
        {
            int n = 0;
            while (pool != null && pool.Count > 0 && bad(pool.Peek())) { pool.Pop(); n++; }
            return n;
        }
    }
}
