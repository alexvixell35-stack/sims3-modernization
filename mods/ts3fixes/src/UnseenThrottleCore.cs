using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent parts of the unseen script throttle (UnseenThrottle.cs, docs/144), kept free of game types so the
    // offline self-test (tools/selftest/unseen) compiles them alone.

    // "Run one call in n" per key (a Sim or a broadcaster) while the key is unseen. A key's first unseen call is staggered
    // by the key itself, so Sims that turn unseen together (a world load, a camera jump) do not all run on the same call.
    // Seen(key) forgets the key: a seen caller always runs, and its next unseen stretch starts staggered again.
    public sealed class EveryN
    {
        public const int kMaxKeys = 4096; // destroyed Sims and broadcasters leave keys behind; a full table starts over
        readonly Dictionary<int, int> mLeft = new Dictionary<int, int>();
        public int Clears;

        public int Count { get { return mLeft.Count; } }

        // True = run this call. n <= 1 always runs.
        public bool Unseen(int key, int n)
        {
            if (n <= 1) return true;
            int left;
            if (!mLeft.TryGetValue(key, out left))
            {
                if (mLeft.Count >= kMaxKeys) { mLeft.Clear(); Clears++; }
                left = (key & 0x7FFFFFFF) % n;
            }
            if (left <= 0) { mLeft[key] = n - 1; return true; }
            mLeft[key] = left - 1;
            return false;
        }

        public void Seen(int key) { mLeft.Remove(key); }
        public void Clear() { mLeft.Clear(); }
    }

    // At most max unseen motive catch-ups per sim tick (docs/144 §8). Sims that turned unseen together (load, camera jump,
    // switch on) become due on the same sim-minute; one over the limit waits for the next tick, unless force (its gap
    // reached the hard limit, so it never drifts toward the 60-minute auto-satisfy switch).
    public sealed class TickCap
    {
        long mTick = long.MinValue;
        int mUsed;
        public int Most, Held;

        public bool Take(long tick, int max, bool force)
        {
            if (tick != mTick) { mTick = tick; mUsed = 0; }
            if (!force && mUsed >= max) { Held++; return false; }
            if (++mUsed > Most) Most = mUsed;
            return true;
        }
    }

    public static class UnseenMath
    {
        public const int kCatchUpsPerTick = 4;   // unseen motive catch-ups run in one sim tick (TickCap)
        public const float kHardGapMin = 45f;    // a catch-up this late runs even over the per-tick cap (< 60, auto-satisfy)

        // Motive updates (Autonomy.Update) integrate the sim-minutes since the Sim's last update. An unseen Sim's update
        // runs only once that gap reaches n base steps, so one update integrates the whole gap (n x dt). The gap stays
        // under capMin: Motive.Update switches a high-LOD Sim to the time-of-day auto-satisfy curve past
        // kMaxUpdateHoursBeforeAutoSatisfy (1 sim-hour), which would change the result, not just delay it.
        public static bool Due(float elapsedMin, int n, float baseMin, float capMin)
        {
            if (n <= 1) return true;
            return elapsedMin >= Math.Min(n * baseMin, capMin);
        }

        // RunAutonomy's motive refresh for a low-LOD Sim (docs/245). Below 1 sim-minute Autonomy.Update is EA's own no-op, so
        // that is no refresh to count; the histogram buckets use the same edges.
        public const float kRefreshFloorMin = 1f;
        public const float kRefreshCapMin = 30f;

        // unseenpark (docs/243 D1): hold a low-LOD Sim out of the search queue while the gap since its last refresh is under N.
        // No clock (0) or a clock ahead of the game (< 0) never holds. The cap stays under NRaas Overwatch StuckCheck's limit
        // (about a sim day; I-243-4 allows up to 240 sim-minutes).
        public const float kParkCapMin = 240f;
        public static bool ParkHold(float lastMin, float nowMin, float minutes)
        {
            if (lastMin == 0f || minutes <= 0f) return false;
            float gap = nowMin - lastMin;
            return gap >= 0f && gap < Math.Min(minutes, kParkCapMin);
        }

        // Stagger (Rodney 2026-11-06): a held Sim that became seen passes only in its own slot, so a camera jump does not make
        // every held Sim on the lot search and route in the same ticks. A slot is kParkSlotTicks wide, twice the 2-tick poll
        // (kSimLoopSleepTicksWhenNotInQueue), so a Sim polling with any gap up to kParkSlotTicks lands in every slot at least
        // once (a 1-tick slot is skipped forever when poll phase and slot disagree). Stateless: tick and Sim id only, no RNG.
        // After a gap over kParkSlotTicks (a stalled tick) a slot can be missed; the next is staggerTicks later, so the delay
        // stays a small multiple of staggerTicks. staggerTicks under two slots wide = no stagger.
        public const int kParkSlotTicks = 4;
        public static int ParkSlotHash(ulong simId)
        {
            ulong x = simId * 0x9E3779B97F4A7C15UL;
            return (int)((x >> 33) & 0x7FFFFFFF);
        }
        public static bool ParkStaggerPass(long tick, ulong simId, int staggerTicks)
        {
            long slots = staggerTicks / kParkSlotTicks;
            if (slots <= 1) return true;
            return (tick / kParkSlotTicks + ParkSlotHash(simId)) % slots == 0;
        }

        public const int kParkHold = 0, kParkSnap = 1, kParkExempt = 2, kParkUnseen = 3, kParkSeen = 4;
        public static int ParkVerdict(bool unseen, bool hold, bool exempt)
        {
            if (!hold) return unseen ? kParkUnseen : kParkSeen;
            if (!unseen) return kParkSnap;
            return exempt ? kParkExempt : kParkHold;
        }

        // Gap histogram slot: 0 = under 1 sim-minute (EA's no-op), 1 = 1-5, 2 = 5-15, 3 = 15-30, 4 = 30 or more.
        public static int RefreshBucket(float gapMin)
        {
            return gapMin < kRefreshFloorMin ? 0 : gapMin < 5f ? 1 : gapMin < 15f ? 2 : gapMin < kRefreshCapMin ? 3 : 4;
        }

        // Stable small key for an object id (ulong) or a reference hash.
        public static int Key(ulong id) { return unchecked((int)(id ^ (id >> 32))); }
    }
}
