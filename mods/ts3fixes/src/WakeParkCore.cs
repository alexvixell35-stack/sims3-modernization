using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent parts of the unseen wake-up probe and the two park switches (WakePark.cs, docs/211), kept free of game
    // types so the offline self-test (tools/selftest/wakepark) compiles them alone.
    public static class WakeParkCore
    {
        // Class of a wake-up: 0 seen high-LOD, 1 seen low-LOD, 2 unseen high-LOD, 3 unseen low-LOD (counter order).
        public static int Class(bool unseen, bool highLod) { return (unseen ? 2 : 0) + (highLod ? 0 : 1); }

        // Ticks one exit-reason poll may sleep: the game polls every tick until its deadline (Sim.WaitForExitReason); the
        // parked poll sleeps up to k ticks but never past the deadline, so it returns false on the same tick the game would.
        // end = long.MaxValue for an open-ended wait. Always >= 1 (the game's own step).
        public static uint PollSleep(long now, long end, int k)
        {
            if (k <= 1) return 1u;
            long left = end - now;
            if (left <= 1) return 1u;
            return (uint)Math.Min((long)k, left);
        }

        // Extra ticks a parked SimUpdate pass sleeps (the game's own Sleep(0) after it adds one more tick): k - 1, at least 0.
        public static uint ParkSleep(int k) { return k <= 1 ? 0u : (uint)(k - 1); }

        // Cached seen/unseen verdict of a Sim or lot: a seen verdict is kept up to seenMax ticks; an unseen one only up to
        // unseenMax (the park length), so a parked Sim is judged again at every wake and a Sim that turns visible stays parked
        // at most one park plus one cache step.
        public static bool Stale(long age, bool unseen, int unseenMax, int seenMax)
        {
            return age < 0 || age >= (unseen ? Math.Max(1, unseenMax) : Math.Max(1, seenMax));
        }
    }

    // Direct-mapped verdict cache keyed by an object id (no Dictionary: hooks ask it on every SimUpdate pass). A collision
    // just evicts; an epoch bump (camera jump, household switch, world load) drops every entry at once.
    public sealed class WpCache
    {
        public const int kSize = 1024; // power of two
        readonly ulong[] mIds = new ulong[kSize];
        readonly long[] mTick = new long[kSize];
        readonly int[] mVal = new int[kSize];
        readonly int[] mEpoch = new int[kSize];
        public int Epoch = 1;

        static int Slot(ulong id) { return (int)((id ^ (id >> 17) ^ (id >> 32)) & (kSize - 1)); }

        // True with the stored value and its tick when id has a current entry.
        public bool Get(ulong id, out int val, out long tick)
        {
            int i = Slot(id);
            if (mIds[i] == id && mEpoch[i] == Epoch && id != 0) { val = mVal[i]; tick = mTick[i]; return true; }
            val = 0; tick = 0;
            return false;
        }

        public void Put(ulong id, int val, long tick)
        {
            int i = Slot(id);
            mIds[i] = id; mVal[i] = val; mTick[i] = tick; mEpoch[i] = Epoch;
        }

        public void Clear() { Epoch++; }
    }

    // Exit-reason waits sleeping in a parked poll: Sim key -> (task, accepted reasons). One waiter per Sim; a second wait on
    // the same Sim (rare: two tasks waiting on one Sim) polls every tick as the game does.
    public sealed class WpWaiters
    {
        struct W { public ulong Task; public uint Mask; public long Since; }
        readonly Dictionary<ulong, W> mBySim = new Dictionary<ulong, W>(new WpIdCmp());
        public int Count { get { return mBySim.Count; } }

        public bool TryAdd(ulong sim, ulong task, uint mask, long tick)
        {
            if (mBySim.ContainsKey(sim)) return false;
            W w; w.Task = task; w.Mask = mask; w.Since = tick;
            mBySim[sim] = w;
            return true;
        }

        public void Remove(ulong sim, ulong task)
        {
            W w;
            if (mBySim.TryGetValue(sim, out w) && w.Task == task) mBySim.Remove(sim);
        }

        // The task to wake for reasons just added to sim, or 0 when nobody waits on any of them. The entry stays until the
        // woken task removes it (it may be woken twice; the second wake finds it running and does nothing).
        public ulong Match(ulong sim, uint reasons)
        {
            W w;
            return mBySim.TryGetValue(sim, out w) && (w.Mask & reasons) != 0 ? w.Task : 0UL;
        }

        public void AllTasks(List<ulong> into) { foreach (W w in mBySim.Values) into.Add(w.Task); }

        // Entries older than maxAge ticks belong to tasks that were destroyed without unwinding (never woken again).
        public int Prune(long now, long maxAge)
        {
            List<ulong> old = null;
            foreach (KeyValuePair<ulong, W> p in mBySim) if (now - p.Value.Since > maxAge) (old ?? (old = new List<ulong>())).Add(p.Key);
            if (old != null) foreach (ulong k in old) mBySim.Remove(k);
            return old == null ? 0 : old.Count;
        }

        public void Clear() { mBySim.Clear(); }
    }

    // Parked SimUpdate tasks (task -> park start tick), for the wake-all on a view change.
    public sealed class WpParked
    {
        readonly Dictionary<ulong, long> mSince = new Dictionary<ulong, long>(new WpIdCmp());
        public int Count { get { return mSince.Count; } }
        public void Add(ulong task, long tick) { mSince[task] = tick; }
        public bool Remove(ulong task) { return mSince.Remove(task); }
        public void AllTasks(List<ulong> into) { foreach (ulong t in mSince.Keys) into.Add(t); }
        public int Prune(long now, long maxAge)
        {
            List<ulong> old = null;
            foreach (KeyValuePair<ulong, long> p in mSince) if (now - p.Value > maxAge) (old ?? (old = new List<ulong>())).Add(p.Key);
            if (old != null) foreach (ulong k in old) mSince.Remove(k);
            return old == null ? 0 : old.Count;
        }
        public void Clear() { mSince.Clear(); }
    }

    // A view change that may turn parked Sims visible at once: the camera target jumped, the active household or the
    // selected Sim changed, or the game left live mode. Fed once per tick; the first feed only records.
    public sealed class WpView
    {
        bool mHave;
        float mX, mZ;
        ulong mHousehold, mSelected;
        bool mLive;
        public const float kJumpM = 20f; // a pan faster than this per tick is a jump (map click, lot select)

        public bool Changed(float x, float z, ulong household, ulong selected, bool live)
        {
            bool changed = mHave && (live != mLive || household != mHousehold || selected != mSelected
                || (x - mX) * (x - mX) + (z - mZ) * (z - mZ) > kJumpM * kJumpM);
            mHave = true; mX = x; mZ = z; mHousehold = household; mSelected = selected; mLive = live;
            return changed;
        }

        public void Reset() { mHave = false; }
    }

    // explicit: the default ulong comparer boxes in this Mono (TickMemo.cs)
    sealed class WpIdCmp : IEqualityComparer<ulong>
    {
        public bool Equals(ulong a, ulong b) { return a == b; }
        public int GetHashCode(ulong k) { return (int)k ^ (int)(k >> 32); }
    }
}
