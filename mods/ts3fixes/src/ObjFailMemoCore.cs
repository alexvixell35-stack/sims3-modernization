using System;
using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of objfailmemo (ObjFailMemo.cs, docs/650). Self-check: tools/selftest/objfailmemo.
    // A (Sim, object) pair collects route-plan failures with no path (RoutePlanResultType 16), no destination (10) or a
    // locked destination portal (18). Plans of one interaction instance count once (EA replans the same attempt several
    // times). K failures inside the window (from the first counted one) start a block of blockTicks; while it lives, the
    // pair's autonomous picks are refused. User-directed and pushed picks always pass. Exempt Sims (household, selected,
    // unknown scope) are never recorded, and a blocked pair whose Sim turned exempt is not refused. A failure during a
    // block does not extend it; after the block the pair starts again from zero.
    public sealed class ObjFailMemoCore
    {
        public const int kMax = 512;
        public const int kNoDestination = 10, kNoPath = 16, kPortalLocked = 18;  // RoutePlanResultType values
        // Record results
        public const int kCounted = 0, kBlocked = 1, kExempt = 2, kNotRecorded = 3, kSameAttempt = 4;
        // Test results
        public const int kPass = 0, kRefuse = 1, kPassExempt = 2;

        struct Key : IEquatable<Key>
        {
            public readonly ulong Sim, Obj;
            public Key(ulong sim, ulong obj) { Sim = sim; Obj = obj; }
            public bool Equals(Key o) { return Sim == o.Sim && Obj == o.Obj; }
            public override bool Equals(object o) { return o is Key && Equals((Key)o); }
            public override int GetHashCode() { return unchecked((int)Sim ^ (int)(Sim >> 32) * 31 ^ (int)Obj * 17 ^ (int)(Obj >> 32)); }
        }

        // First: tick of the first counted failure of the current window; Until > 0: blocked over [BlockFrom, Until)
        sealed class Entry { public long First, BlockFrom, Until; public int Fails, LastAttempt; }

        readonly Dictionary<Key, Entry> mMemo = new Dictionary<Key, Entry>();
        int mBlocked;  // entries with Until > 0 (cheap "nothing to refuse" test for the autonomy hook)

        public int Count { get { return mMemo.Count; } }
        public int BlockedCount { get { return mBlocked; } }

        public static bool Fails(int result) { return result == kNoPath || result == kNoDestination || result == kPortalLocked; }

        // attempt: a key of the failing interaction instance (0 = unknown, always counted). Fails of the pair after the
        // call are returned in fails.
        public int Record(ulong sim, ulong obj, bool exempt, int result, int attempt, long now, long windowTicks, long blockTicks, int k, out int fails)
        {
            fails = 0;
            if (!Fails(result) || sim == 0 || obj == 0 || windowTicks <= 0 || blockTicks <= 0 || k < 1) return kNotRecorded;
            if (exempt) return kExempt;
            Key key = new Key(sim, obj);
            Entry e;
            if (mMemo.TryGetValue(key, out e))
            {
                if (e.Until > 0)
                {
                    if (e.BlockFrom <= now && now < e.Until) { fails = e.Fails; return kNotRecorded; }  // blocked: not extended
                    Unblock(e);  // block over (or the clock went back): start again
                    e.Fails = 0;
                }
                else if (e.Fails > 0 && attempt != 0 && attempt == e.LastAttempt && e.First <= now && now - e.First < windowTicks)
                { fails = e.Fails; return kSameAttempt; }
                if (e.Fails == 0 || now < e.First || now - e.First >= windowTicks) { e.Fails = 0; e.First = now; }
            }
            else
            {
                if (mMemo.Count >= kMax) Prune(now, windowTicks);
                e = new Entry();
                e.First = now;
                mMemo[key] = e;
            }
            e.Fails++;
            e.LastAttempt = attempt;
            fails = e.Fails;
            if (e.Fails < k) return kCounted;
            e.BlockFrom = now;
            e.Until = now + blockTicks;
            mBlocked++;
            return kBlocked;
        }

        public int Test(ulong sim, ulong obj, bool autonomous, bool exempt, long now)
        {
            if (!autonomous || mBlocked == 0) return kPass;
            Key key = new Key(sim, obj);
            Entry e;
            if (!mMemo.TryGetValue(key, out e) || e.Until == 0) return kPass;
            if (now < e.BlockFrom || now >= e.Until) { Unblock(e); mMemo.Remove(key); return kPass; }
            return exempt ? kPassExempt : kRefuse;
        }

        void Unblock(Entry e)
        {
            if (e.Until == 0) return;
            e.Until = 0;
            e.BlockFrom = 0;
            mBlocked--;
        }

        // drops ended blocks and stale windows; a full table of live blocks is wiped (bounded memory beats a perfect memo)
        void Prune(long now, long windowTicks)
        {
            List<Key> old = new List<Key>();
            foreach (KeyValuePair<Key, Entry> kv in mMemo)
            {
                Entry e = kv.Value;
                bool dead = e.Until > 0 ? (now < e.BlockFrom || now >= e.Until) : (now < e.First || now - e.First >= windowTicks);
                if (dead) old.Add(kv.Key);
            }
            for (int i = 0; i < old.Count; i++) { Unblock(mMemo[old[i]]); mMemo.Remove(old[i]); }
            if (mMemo.Count >= kMax) Clear();
        }

        public void Clear() { mMemo.Clear(); mBlocked = 0; }
    }
}
