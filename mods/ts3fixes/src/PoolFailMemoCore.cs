using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of poolfailmemo (PoolFailMemo.cs, docs/645 §2). Self-check: tools/selftest/poolfailmemo.
    // A (Sim, pool) pair whose GetInPool plan failed with no path (RoutePlanResultType 16) or no destination (10) is kept for
    // a caller-given life in sim ticks. While it lives, the pair's autonomous GetInPool tests are refused; user-directed tests
    // always pass. Exempt Sims (household, selected, unknown scope) are never recorded, and a live pair whose Sim turned
    // exempt is not refused. A success is never recorded; an entry is not extended by a refusal.
    public sealed class PoolFailMemoCore
    {
        public const int kMax = 256;
        public const int kNoPath = 16, kNoDestination = 10;  // RoutePlanResultType values
        // Record results
        public const int kRecorded = 0, kExempt = 1, kNotRecorded = 2;
        // Test results
        public const int kPass = 0, kRefuse = 1, kPassExempt = 2;

        struct Key : IEquatable<Key>
        {
            public readonly ulong Sim, Pool;
            public Key(ulong sim, ulong pool) { Sim = sim; Pool = pool; }
            public bool Equals(Key o) { return Sim == o.Sim && Pool == o.Pool; }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode() { return unchecked((int)Sim ^ (int)(Sim >> 32) * 31 ^ (int)Pool * 17 ^ (int)(Pool >> 32)); }
        }

        struct Entry { public long From, Until; }

        readonly Dictionary<Key, Entry> mMemo = new Dictionary<Key, Entry>();

        public int Count { get { return mMemo.Count; } }

        public static bool Fails(int result) { return result == kNoPath || result == kNoDestination; }

        public int Record(ulong sim, ulong pool, bool exempt, int result, long now, long lifeTicks)
        {
            if (!Fails(result) || sim == 0 || pool == 0 || lifeTicks <= 0) return kNotRecorded;
            if (exempt) return kExempt;
            if (mMemo.Count >= kMax) Prune(now);
            Entry e;
            e.From = now;
            e.Until = now + lifeTicks;
            mMemo[new Key(sim, pool)] = e;
            return kRecorded;
        }

        // live: From <= now < Until (a clock that went back, another save, drops the entry)
        public bool Live(ulong sim, ulong pool, long now)
        {
            Key k = new Key(sim, pool);
            Entry e;
            if (!mMemo.TryGetValue(k, out e)) return false;
            if (e.From <= now && now < e.Until) return true;
            mMemo.Remove(k);
            return false;
        }

        public int Test(ulong sim, ulong pool, bool autonomous, bool exempt, long now)
        {
            if (!autonomous || mMemo.Count == 0 || !Live(sim, pool, now)) return kPass;
            return exempt ? kPassExempt : kRefuse;
        }

        void Prune(long now)
        {
            List<Key> old = new List<Key>();
            foreach (KeyValuePair<Key, Entry> kv in mMemo)
                if (kv.Value.From > now || now >= kv.Value.Until) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mMemo.Remove(old[i]);
            if (mMemo.Count >= kMax) mMemo.Clear();
        }

        public void Clear() { mMemo.Clear(); }
    }
}
