using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // simdescindex (docs/359): the game-independent half of the description index, checked offline by
    // tools/selftest/simdescindex. The game's lookup walks every household in list order and every description in it
    // and returns the first one with the id; a household that is skipped (the previous-traveler one) is not walked.
    //
    // The index is a snapshot of that walk: id -> the first description in walk order, with where it sat (household
    // slot, member slot) and how many descriptions the walk reads to reach it. It is never patched in place:
    //   - a writer that can ADD a description, add a filled household or change an id calls Enter / Exit (hooks). While
    //     one is open every lookup is the game's; Exit marks the snapshot dirty.
    //   - a dirty snapshot is rebuilt by the second lookup after the last write (the first one is the game's), so a
    //     burst of writes with lookups in between costs no rebuild per write.
    //   - a REMOVAL needs no hook to be exact: a hit is returned only when the household is still at its slot, still not
    //     skipped, the description is still at its member slot and still has the id; a changed household count marks
    //     the snapshot dirty. A removal cannot make an absent id present.
    //   - "absent" is answered only while every household is still at its slot with as many descriptions as at the build
    //     (checked on every miss): an add that no hook saw is not answered from the old snapshot, unless an unseen
    //     removal in the same household hides it (the mod scan is the guard for unseen adds).
    //   - a null household or description (the game's walk would throw there) makes the snapshot unusable until the
    //     next rebuild: every lookup is the game's.
    // Holds references to descriptions and households between rebuilds: Reset at world load and quit (no leak across worlds).
    public sealed class DescIndexCore
    {
        public interface IWorld
        {
            int HouseCount { get; }
            object House(int slot);
            bool Skipped(object house);        // not walked by the game's lookup
            int MemberCount(object house);
            object Member(object house, int slot);
            ulong Id(object desc);
        }

        public const int Game = 0, Hit = 1, Absent = 2;
        // why the last lookup was left to the game
        public const int WhyNone = 0, WhyWriter = 1, WhyDirty = 2, WhyBroken = 3, WhyMoved = 4;

        struct Entry { public object Desc, House; public int H, M, Rank; }

        readonly Dictionary<ulong, Entry> mMap = new Dictionary<ulong, Entry>();
        object mSkipped;       // the skipped household seen at the build (at most one in the game)
        readonly List<object> mHouseAt = new List<object>(); // per household slot at the build
        readonly List<int> mCounts = new List<int>();        // its member count then, -1 when skipped
        int mHouses = -1, mTotal;
        int mDepth;
        bool mDirty = true, mArmed, mBroken;

        public int Rebuilds, Moved, Notes, HighWater, Why;
        public int Entries { get { return mMap.Count; } }
        public int Total { get { return mTotal; } }
        public bool InWriter { get { return mDepth > 0; } }

        // (the depth never goes below 0: a Reset may have closed an open window)
        // a writer starts / ends. Sim thread only (plain counter): a hook on another thread calls Note instead
        public void Enter() { mDepth++; mDirty = true; mArmed = false; }
        public void Exit() { mDirty = true; mArmed = false; Notes++; if (mDepth > 0) mDepth--; }
        // the same window for a writer that often changes nothing: dirty only when it wrote
        public void Open() { mDepth++; }
        public void Close(bool wrote) { if (wrote) { mDirty = true; mArmed = false; Notes++; } if (mDepth > 0) mDepth--; }
        // a writer ran (removal hooks: no window needed, see above)
        public void Note() { mDirty = true; mArmed = false; Notes++; }

        public void Reset()
        {
            mMap.Clear(); mHouseAt.Clear(); mCounts.Clear(); mSkipped = null; mHouses = -1; mTotal = 0; mDirty = true; mArmed = false; mBroken = false;
            mDepth = 0;
        }

        void Build(IWorld w)
        {
            mMap.Clear(); mSkipped = null; mBroken = false; mTotal = 0; mHouseAt.Clear(); mCounts.Clear();
            int hc = w.HouseCount, rank = 0, notes = Notes;
            for (int h = 0; h < hc; h++)
            {
                object house = w.House(h);
                if (house == null) { mBroken = true; break; }
                mHouseAt.Add(house);
                if (w.Skipped(house)) { mSkipped = house; mCounts.Add(-1); continue; }
                int mc = w.MemberCount(house);
                mCounts.Add(mc);
                for (int m = 0; m < mc; m++)
                {
                    object d = w.Member(house, m);
                    if (d == null) { mBroken = true; break; }
                    rank++;
                    ulong id = w.Id(d);
                    if (mMap.ContainsKey(id)) continue; // the walk returns the first one
                    Entry e = new Entry(); e.Desc = d; e.House = house; e.H = h; e.M = m; e.Rank = rank;
                    mMap[id] = e;
                }
                if (mBroken) break;
            }
            if (mBroken) mMap.Clear();
            mHouses = hc; mTotal = rank; mArmed = false; Rebuilds++;
            mDirty = Notes != notes; // a writer on another thread noted while this ran: not a snapshot, the next lookup rebuilds
            if (mMap.Count > HighWater) HighWater = mMap.Count;
        }

        // the exception path: nothing kept, dirty; an open writer window stays open
        public void Drop() { mMap.Clear(); mHouseAt.Clear(); mCounts.Clear(); mSkipped = null; mHouses = -1; mTotal = 0; mDirty = true; mArmed = false; mBroken = false; }

        // Hit: desc is what the game's walk returns, walked = descriptions it reads to get there.
        // Absent: no walked household holds the id, walked = all of them. Game: ask the game (Why says why).
        public int Lookup(ulong id, IWorld w, out object desc, out int walked)
        {
            desc = null; walked = 0; Why = WhyNone;
            if (mDepth > 0) { Why = WhyWriter; return Game; }
            int hc = w.HouseCount;
            if (!mDirty && (hc != mHouses || (mSkipped != null && !w.Skipped(mSkipped)))) { mDirty = true; mArmed = false; Moved++; }
            if (mDirty)
            {
                if (!mArmed) { mArmed = true; Why = WhyDirty; return Game; }
                Build(w);
                if (mDirty) { mArmed = true; Why = WhyDirty; return Game; }
            }
            if (mBroken) { Why = WhyBroken; return Game; }
            Entry e;
            if (!mMap.TryGetValue(id, out e))
            {
                // "absent" is not taken from the snapshot alone: every household must be the one at its slot at the build and
                // hold as many descriptions as then (an add no hook saw moves a count; a removal too, then the game answers
                // once). Not seen: an unseen add and an unseen removal in the same household between two lookups.
                bool same = hc == mCounts.Count;
                for (int h = 0; same && h < hc; h++)
                {
                    object house = w.House(h);
                    same = house != null && ReferenceEquals(house, mHouseAt[h]) && (w.Skipped(house) ? -1 : w.MemberCount(house)) == mCounts[h];
                }
                if (!same) { mDirty = true; mArmed = true; Moved++; Why = WhyMoved; return Game; }
                walked = mTotal; return Absent;
            }
            if (e.H < hc && ReferenceEquals(w.House(e.H), e.House) && !w.Skipped(e.House) && e.M < w.MemberCount(e.House)
                && ReferenceEquals(w.Member(e.House, e.M), e.Desc) && w.Id(e.Desc) == id)
            {
                desc = e.Desc; walked = e.Rank; return Hit;
            }
            mDirty = true; mArmed = true; Moved++; Why = WhyMoved; // this lookup is the game's one after the change
            return Game;
        }
    }

    // The urnstones the world may list (docs/359 §4): a superset of the world's own list, so "no urn here holds the id"
    // proves the game's urn loop finds none. Seeded from the game's own query, added to by the object-init hook, dropped
    // when the world no longer resolves the id; a destroy the hook did not see sets Stale and the next use re-seeds.
    // Holds object references: Clear at world load and quit; dropped on destroy.
    public sealed class UrnSet
    {
        readonly Dictionary<ulong, object> mUrns = new Dictionary<ulong, object>();
        public bool Seeded, Stale;
        public int Seeds, HighWater;
        public int Count { get { return mUrns.Count; } }
        public Dictionary<ulong, object>.ValueCollection Urns { get { return mUrns.Values; } }

        // false: the urn cannot be held (no id yet), so the set is no superset any more: not seeded, the next use re-seeds
        public bool Add(ulong id, object urn)
        {
            if (id == 0 || urn == null) { Seeded = false; return false; }
            mUrns[id] = urn;
            if (mUrns.Count > HighWater) HighWater = mUrns.Count;
            return true;
        }
        public bool Drop(ulong id) { return mUrns.Remove(id); }
        public void Clear() { mUrns.Clear(); Seeded = false; Stale = false; }
        public void Seed(ulong[] ids, object[] urns)
        {
            mUrns.Clear();
            bool all = true;
            for (int i = 0; i < ids.Length; i++) all &= Add(ids[i], urns[i]);
            Seeded = all; Stale = false; Seeds++; // an urn without an id in the world's own list: every miss re-seeds (the game's cost)
        }
    }
}
