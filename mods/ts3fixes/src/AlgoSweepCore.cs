using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ts3Fixes
{
    // docs/665 algo sweep: game-free helpers (selftest tools/selftest/algosweep). No game types here.
    //
    // RefSet665: a reference-identity set with a separate null flag. It answers "was this exact object added before?",
    // which is what List<T>.Contains answers for a class that does not override Equals (EqualityComparer<T>.Default is
    // then Object.Equals, a reference compare). One hash probe instead of a walk of the list: an O(n*n) dedup loop
    // becomes O(n), with the same members in the same order.
    public sealed class RefSet665
    {
        sealed class RefEq : IEqualityComparer<object>
        {
            public static readonly RefEq Instance = new RefEq();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        readonly Dictionary<object, bool> mSeen;
        bool mHasNull;

        public RefSet665() { mSeen = new Dictionary<object, bool>(RefEq.Instance); }
        public RefSet665(int capacity) { mSeen = new Dictionary<object, bool>(capacity, RefEq.Instance); }

        public int Count { get { return mSeen.Count + (mHasNull ? 1 : 0); } }

        // true when o was not in the set (and is now)
        public bool Add(object o)
        {
            if (o == null) { if (mHasNull) return false; mHasNull = true; return true; }
            if (mSeen.ContainsKey(o)) return false;
            mSeen.Add(o, true);
            return true;
        }

        public bool Contains(object o) { return o == null ? mHasNull : mSeen.ContainsKey(o); }

        public void AddAll<T>(IEnumerable<T> items) where T : class
        {
            foreach (T t in items) Add(t);
        }
    }

    public static class AlgoSweepCore
    {
        // EA's dedup, word for word: "if (!list.Contains(v)) list.Add(v)" for each value in order. The reference.
        public static void AppendDistinctNaive<T>(IEnumerable<T> values, List<T> list) where T : class
        {
            foreach (T v in values) if (!list.Contains(v)) list.Add(v);
        }

        // Same members in the same order as AppendDistinctNaive for a T without an Equals override, when list starts
        // empty or seen holds exactly list's members.
        public static void AppendDistinct<T>(IEnumerable<T> values, List<T> list, RefSet665 seen) where T : class
        {
            foreach (T v in values) if (seen.Add(v)) list.Add(v);
        }

        // Below this many values EA's linear Contains is as cheap as a hash set (no set is allocated).
        public const int kSetMin = 8;
    }
}
