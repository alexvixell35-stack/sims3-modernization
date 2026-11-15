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

    // Lane L4 1115 exact memos (ExactMemos.cs, AutRoomMemo.cs, ScriptHotSpots.cs invcache). Game-independent tables.

    // A direct-mapped ulong -> T table: one slot per hash, a store overwrites the slot's old key. Clear is O(1) (the
    // generation moves on); stale values stay referenced until overwritten or Wipe.
    public sealed class DirectMemo<T>
    {
        readonly ulong[] mKeys;
        readonly T[] mVals;
        readonly int[] mGen;
        readonly int mMask;
        int mCur = 1;
        bool mUsed;

        public DirectMemo(int slotsPow2)
        {
            if (slotsPow2 <= 0 || (slotsPow2 & (slotsPow2 - 1)) != 0) throw new ArgumentException("slots must be a power of two");
            mKeys = new ulong[slotsPow2]; mVals = new T[slotsPow2]; mGen = new int[slotsPow2]; mMask = slotsPow2 - 1;
        }

        public static int Hash(ulong k)
        {
            uint h = (uint)k ^ (uint)(k >> 32);
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return (int)(h & 0x7FFFFFFF);
        }

        public bool TryGet(ulong k, out T v)
        {
            int i = Hash(k) & mMask;
            if (mGen[i] == mCur && mKeys[i] == k) { v = mVals[i]; return true; }
            v = default(T);
            return false;
        }

        public void Store(ulong k, T v)
        {
            int i = Hash(k) & mMask;
            mKeys[i] = k; mVals[i] = v; mGen[i] = mCur; mUsed = true;
        }

        // True when something was stored since the last clear.
        public bool Clear()
        {
            bool any = mUsed;
            mUsed = false;
            if (++mCur == int.MaxValue) Wipe();
            return any;
        }

        public void Wipe()
        {
            Array.Clear(mKeys, 0, mKeys.Length); Array.Clear(mVals, 0, mVals.Length); Array.Clear(mGen, 0, mGen.Length);
            mCur = 1; mUsed = false;
        }
    }

    // docs/245 §5 H permamemo: BuffManager.IsPermaMoodlet(name) = static buff table lookup -> instance.mBuff -> its
    // PermaMoodlet flag. Kept per name with the instance and Buff it came from; the whole table is dropped when the static
    // table's Count or the parse generation (bumped by every ParseBuffData call) moved. The caller checks on a hit that
    // instance.mBuff is still the kept Buff (a public field). Not-found names keep a null instance.
    public sealed class PermaMemo
    {
        sealed class Entry { public object Inst, Buff; public bool Ans; }
        readonly DirectMemo<Entry> mTable = new DirectMemo<Entry>(256);
        int mCount = -1, mParse = -1;

        // True when the stamp moved and something was dropped.
        public bool Stamp(int count, int parseGen)
        {
            if (count == mCount && parseGen == mParse) return false;
            mCount = count; mParse = parseGen;
            return mTable.Clear();
        }

        public bool TryGet(ulong name, out object inst, out object buff, out bool ans)
        {
            Entry e;
            if (mTable.TryGet(name, out e)) { inst = e.Inst; buff = e.Buff; ans = e.Ans; return true; }
            inst = null; buff = null; ans = false;
            return false;
        }

        public void Store(ulong name, object inst, object buff, bool ans)
        {
            Entry e = new Entry(); e.Inst = inst; e.Buff = buff; e.Ans = ans;
            mTable.Store(name, e);
        }

        public bool Clear() { mCount = -1; return mTable.Clear(); }
    }

    // docs/96 B2 catnamecache: UserToolUtils.GetProductCatalogName(product) per product object. A hit needs the same
    // BuildBuyProduct object with the same native handle (Dispose sets it to 0, and a destroyed handle may be reused by
    // another product object, which is then a miss). Handle 0 is never kept. Clear drops the references too.
    public sealed class CatalogNameMemo
    {
        sealed class Entry { public object Product; public string Name; }
        readonly DirectMemo<Entry> mTable = new DirectMemo<Entry>(1024);
        bool mUsed;

        public bool TryGet(object product, uint handle, out string name)
        {
            Entry e;
            if (handle != 0 && mTable.TryGet(handle, out e) && ReferenceEquals(e.Product, product)) { name = e.Name; return true; }
            name = null;
            return false;
        }

        public void Store(object product, uint handle, string name)
        {
            if (handle == 0) return;
            Entry e = new Entry(); e.Product = product; e.Name = name;
            mTable.Store(handle, e);
            mUsed = true;
        }

        public bool Clear() { bool any = mUsed; mUsed = false; mTable.Wipe(); return any; }
    }

    // docs/95 §5.5 buffremovenoop: BuffManager.RemoveElement(BuffNames) does nothing exactly when the buff is not active
    // (mValues), no ref-count list is kept at all (mBuffCountList empty, so no ContainsKey hit) and no delayed buff is
    // queued (mDelayedBuffs null or empty: the scan finds nothing). A null mBuffCountList is left to the game (it throws).
    public static class BuffRemoveRule
    {
        public static bool IsNoOp(bool active, bool countListNull, int countListCount, bool delayedNull, int delayedCount)
        {
            return !active && !countListNull && countListCount == 0 && (delayedNull || delayedCount == 0);
        }
    }

    // docs/115 invcache: the last few (handler field value -> GetInvocationList()) pairs, matched by reference only
    // (Delegate.Equals and GetHashCode are value-based). A published delegate never changes its chain, and every add or
    // remove makes a new reference, so a hit is the list the game would build now (I-105-10).
    public sealed class InvListCache
    {
        public const int kSlots = 4;
        readonly Delegate[] mKey = new Delegate[kSlots];
        readonly Delegate[][] mList = new Delegate[kSlots][];
        int mNext;

        // c[hit]++ on a hit, c[miss]++ when the list is built and kept (oldest slot replaced).
        public Delegate[] Get(Delegate h, int[] c, int hit, int miss)
        {
            for (int i = 0; i < kSlots; i++)
                if (ReferenceEquals(mKey[i], h)) { c[hit]++; return mList[i]; }
            Delegate[] l = h.GetInvocationList();
            mKey[mNext] = h; mList[mNext] = l;
            mNext = (mNext + 1) % kSlots;
            c[miss]++;
            return l;
        }

        // Verify: a fresh list; on any difference count it and use the fresh one (the game's answer).
        public static Delegate[] Check(Delegate h, Delegate[] cached, int[] c, int mismatch)
        {
            Delegate[] fresh = h.GetInvocationList();
            if (Same(fresh, cached)) return cached;
            c[mismatch]++;
            return fresh;
        }

        public static bool Same(Delegate[] a, Delegate[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (!ReferenceEquals(a[i].Target, b[i].Target) || !a[i].Method.Equals(b[i].Method)) return false;
            return true;
        }

        public void Clear() { Array.Clear(mKey, 0, kSlots); Array.Clear(mList, 0, kSlots); mNext = 0; }
    }

    // docs/97 §7 AM-1 H2: Sim.IsAllowedInRoom answers keyed (Sim reference, lot id, room id), kept only for one scoring
    // segment (Bump starts a new one). The Sim's current lot at store time must still be the Sim's current lot on a hit
    // (the game's answer reads it). Open addressing, linear probe; a full table stores nothing.
    public sealed class RoomMemo
    {
        public const int kSlots = 64;
        readonly object[] mSim = new object[kSlots], mLotCur = new object[kSlots];
        readonly ulong[] mLot = new ulong[kSlots];
        readonly int[] mRoom = new int[kSlots], mGen = new int[kSlots];
        readonly bool[] mAns = new bool[kSlots];
        int mCur = 1;

        static int Start(ulong simId, ulong lot, int room)
        {
            uint h = unchecked((uint)simId * 0x9E3779B1u ^ ((uint)lot ^ (uint)(lot >> 32)) * 0x85EBCA77u ^ (uint)room * 0xC2B2AE3Du);
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return (int)(h & (kSlots - 1));
        }

        public void Bump() { if (++mCur == int.MaxValue) Wipe(); }

        public void Wipe()
        {
            Array.Clear(mSim, 0, kSlots); Array.Clear(mLotCur, 0, kSlots); Array.Clear(mGen, 0, kSlots);
            mCur = 1;
        }

        public bool TryGet(object sim, ulong simId, object lotCur, ulong lot, int room, out bool ans)
        {
            int i = Start(simId, lot, room);
            for (int n = 0; n < kSlots; n++, i = (i + 1) & (kSlots - 1))
            {
                if (mGen[i] != mCur) break;
                if (ReferenceEquals(mSim[i], sim) && mLot[i] == lot && mRoom[i] == room)
                {
                    if (!ReferenceEquals(mLotCur[i], lotCur)) break;
                    ans = mAns[i];
                    return true;
                }
            }
            ans = false;
            return false;
        }

        // False when the table is full (nothing stored).
        public bool Store(object sim, ulong simId, object lotCur, ulong lot, int room, bool ans)
        {
            int i = Start(simId, lot, room);
            for (int n = 0; n < kSlots; n++, i = (i + 1) & (kSlots - 1))
            {
                bool empty = mGen[i] != mCur;
                if (empty || (ReferenceEquals(mSim[i], sim) && mLot[i] == lot && mRoom[i] == room))
                {
                    mSim[i] = sim; mLotCur[i] = lotCur; mLot[i] = lot; mRoom[i] = room; mAns[i] = ans; mGen[i] = mCur;
                    return true;
                }
            }
            return false;
        }
    }

    // docs/97 §7 H1: one scoring segment = one (Autonomy, sim tick, script task). Renew is true when a new one opens.
    public sealed class PairScope
    {
        object mOwner;
        long mTick = -1;
        ulong mTask;
        bool mOpen;
        public int Depth;

        public bool Open { get { return mOpen; } }

        // Inside a pair of the open segment, on its tick and task (a yield inside a pair must not leak the memo to the
        // task that runs next, nor to a later tick).
        public bool Inside(long tick, ulong task) { return mOpen && Depth > 0 && tick == mTick && task == mTask; }

        public bool Renew(object owner, long tick, ulong task)
        {
            if (mOpen && tick == mTick && task == mTask && ReferenceEquals(owner, mOwner)) return false;
            mOpen = true; mOwner = owner; mTick = tick; mTask = task;
            return true;
        }

        public void Reset() { mOpen = false; mOwner = null; mTick = -1; Depth = 0; }
    }

    // docs/97 §7 H5: segment scoring time buckets < 1, 1-2, 2-5, 5-10, >= 10 ms at c[b..b+4], max us at c[b+5].
    public static class SliceHistogram
    {
        public static int Bucket(long us) { return us < 1000 ? 0 : us < 2000 ? 1 : us < 5000 ? 2 : us < 10000 ? 3 : 4; }

        public static void Add(int[] c, int b, long us)
        {
            if (us < 0) return;
            c[b + Bucket(us)]++;
            int u = us > int.MaxValue ? int.MaxValue : (int)us;
            if (u > c[b + 5]) c[b + 5] = u;
        }
    }
}
