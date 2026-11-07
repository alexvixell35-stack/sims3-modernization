using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent parts of the docs/74 hot-spot fixes (ScriptHotSpots.cs), kept free of game types so the offline
    // self-test (tools/selftest/hotspots) compiles them alone.

    // When data kept for "this sim tick" must be rebuilt: another Simulator tick, another owner (e.g. the active
    // household), or more than kMaxAgeMs of wall time since it was built (modes where ticks stop), as SpatialCache.cs.
    public sealed class TickStamp
    {
        public const int kMaxAgeMs = 100;
        long mTick = -1;
        object mOwner;
        int mStampMs;
        bool mValid;

        // True = stale: the caller rebuilds its data, and the stamp now names (tick, owner, nowMs).
        public bool Renew(long tick, object owner, int nowMs)
        {
            int age = unchecked(nowMs - mStampMs);
            if (mValid && tick == mTick && ReferenceEquals(owner, mOwner) && age >= 0 && age <= kMaxAgeMs) return false;
            mValid = true; mTick = tick; mOwner = owner; mStampMs = nowMs;
            return true;
        }

        public void Reset() { mValid = false; mOwner = null; }
    }

    // A handful of objects compared by reference (the lots the active household's Sims stand on: at most a few).
    public sealed class RefSet
    {
        object[] mItems = new object[8];
        int mCount;

        public int Count { get { return mCount; } }
        public void Clear() { Array.Clear(mItems, 0, mCount); mCount = 0; }
        public bool Contains(object o)
        {
            for (int i = 0; i < mCount; i++) if (ReferenceEquals(mItems[i], o)) return true;
            return false;
        }
        public void Add(object o)
        {
            if (o == null || Contains(o)) return;
            if (mCount == mItems.Length) Array.Resize(ref mItems, mCount * 2);
            mItems[mCount++] = o;
        }
    }

    // A poll that may be skipped while its input point stands still: Skip is true when the last run was at the same
    // point less than PeriodMs ago (TooltipManager's window walk under a resting cursor).
    public sealed class PollGate
    {
        public const int PeriodMs = 100;
        float mX, mY;
        int mLastMs;
        bool mHas;

        public bool Skip(float x, float y, int nowMs)
        {
            if (!mHas || x != mX || y != mY) return false;
            int age = unchecked(nowMs - mLastMs);
            return age >= 0 && age < PeriodMs;
        }

        public void Ran(float x, float y, int nowMs) { mHas = true; mX = x; mY = y; mLastMs = nowMs; }
    }

    // Answers of a bool method with two ref float outputs, per lot id, for one sim tick (Lot.GetHoursOfOperation). A
    // false answer leaves the outputs as the caller passed them, as the game's method does.
    public sealed class HoursMemo
    {
        struct Entry { public bool Result; public float Open, Close; }
        sealed class IdComparer : IEqualityComparer<ulong> // explicit: the default one boxes in this Mono
        {
            public bool Equals(ulong a, ulong b) { return a == b; }
            public int GetHashCode(ulong k) { return (int)k ^ (int)(k >> 32); }
        }

        public readonly TickStamp Stamp = new TickStamp();
        readonly Dictionary<ulong, Entry> mEntries = new Dictionary<ulong, Entry>(new IdComparer());

        public int Count { get { return mEntries.Count; } }
        public void Clear() { if (mEntries.Count > 0) mEntries.Clear(); }

        public bool TryGet(ulong id, ref float open, ref float close, out bool result)
        {
            Entry e;
            if (!mEntries.TryGetValue(id, out e)) { result = false; return false; }
            result = e.Result;
            if (e.Result) { open = e.Open; close = e.Close; }
            return true;
        }

        public void Store(ulong id, bool result, float open, float close)
        {
            Entry e;
            e.Result = result; e.Open = open; e.Close = close;
            mEntries[id] = e;
        }
    }

    // docs/110 §7.2: one spring wildflower fire (ScriptHotSpots.cs WildflowerTaskHook). The placement loop it books
    // follows the game's: stop at the first placed try or after MaxTries; then the fire adds itself to counters [93-97].
    public sealed class WildflowerFire
    {
        public readonly int MaxTries;
        public int Tries, MaxTryUs, SumUs;
        public bool Placed;

        public WildflowerFire(int maxTries) { MaxTries = maxTries; }

        // Books one try; true when the fire is over (placed, or out of tries).
        public bool Try(bool placed, int us)
        {
            Tries++;
            SumUs += us;
            if (us > MaxTryUs) MaxTryUs = us;
            if (placed) Placed = true;
            return Placed || Tries >= MaxTries;
        }

        // Between tries the flower is out of world, and the save or travel must not store it. Every save path sets its flag and then
        // sleeps at least once before the snapshot (OptionsModel.SaveGameSynchronous, GameStates' SaveTravel calls), so a task on Sleep(0) wakes first.
        public static bool MustStop(bool saving, bool travelling) { return saving || travelling; }

        // [93] fires, [94] tries, [95] placed, [96] failed, [97] longest ms: the whole fire when it ran in the alarm,
        // else the longest single try (one slice of the task).
        public void Count(int[] c, bool wholeFire)
        {
            c[93]++;
            c[94] += Tries;
            if (Placed) c[95]++; else c[96]++;
            int ms = (wholeFire ? SumUs : MaxTryUs) / 1000;
            if (ms > c[97]) c[97] = ms;
        }
    }
}
