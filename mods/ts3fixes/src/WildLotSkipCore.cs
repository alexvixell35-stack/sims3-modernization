using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of wildlotskip (WildLotSkip.cs, docs/656 §8.3). Self-check: tools/selftest/wildlotskip.
    // An owner (an animal's SimDescription id, or kHorsePool for the whole wild horse herd) and a lot id are remembered for
    // `span` sim ticks after the owner's GoToLot to that lot failed with a locked portal or no path, or was cancelled as too
    // long. A pick that lands on a remembered lot is drawn again from EA's own weights with the remembered lots left out
    // (Pick), which gives exactly EA's distribution over the other lots; when every weighted lot is remembered, EA's pick stays.
    // Walk tracks one GoToLot per animal (interaction id and its first sight) for the long-walk guard.
    public sealed class WildLotSkipCore
    {
        public const int kMax = 256, kMaxWalks = 64;
        public const ulong kHorsePool = 1;  // SimDescription ids are 64-bit hashes, never 1

        // Walk results
        public const int kWalkNone = 0, kWalkNew = 1, kWalkOn = 2, kWalkLong = 3;

        struct Key
        {
            public ulong Owner, Lot;
            public Key(ulong o, ulong l) { Owner = o; Lot = l; }
        }
        sealed class KeyEq : IEqualityComparer<Key>
        {
            public bool Equals(Key a, Key b) { return a.Owner == b.Owner && a.Lot == b.Lot; }
            public int GetHashCode(Key k) { return (k.Owner ^ (k.Lot * 0x9E3779B97F4A7C15UL)).GetHashCode(); }
        }
        struct WalkRec { public ulong Ia, Lot; public long From; }

        readonly Dictionary<Key, long> mFrom = new Dictionary<Key, long>(new KeyEq());
        readonly Dictionary<ulong, WalkRec> mWalks = new Dictionary<ulong, WalkRec>();

        public int Count { get { return mFrom.Count; } }
        public int Walks { get { return mWalks.Count; } }
        public void Clear() { mFrom.Clear(); mWalks.Clear(); }

        // remembered: recorded at most span ticks ago (a clock that went back, another save, forgets it)
        public bool Skipped(ulong owner, ulong lot, long now, long span)
        {
            if (mFrom.Count == 0 || lot == 0) return false;
            Key k = new Key(owner, lot);
            long from;
            if (!mFrom.TryGetValue(k, out from)) return false;
            if (now >= from && now - from < span) return true;
            mFrom.Remove(k);
            return false;
        }

        // true when (owner, lot) was not remembered before (a new log line)
        public bool Note(ulong owner, ulong lot, long now, long span)
        {
            if (lot == 0) return false;
            bool fresh = !Skipped(owner, lot, now, span);
            if (fresh && mFrom.Count >= kMax)
            {
                List<Key> old = new List<Key>();
                foreach (KeyValuePair<Key, long> e in mFrom) if (now < e.Value || now - e.Value >= span) old.Add(e.Key);
                for (int i = 0; i < old.Count; i++) mFrom.Remove(old[i]);
                if (mFrom.Count >= kMax) mFrom.Clear();
            }
            mFrom[new Key(owner, lot)] = now;
            return fresh;
        }

        // sum of the weights of the lots not skipped (the caller draws roll in [0, total], as RandomUtil.GetFloat(total))
        public static float Total(float[] w, bool[] skip)
        {
            float t = 0f;
            for (int i = 0; i < w.Length; i++) if (!skip[i]) t += w[i];
            return t;
        }

        // RandomUtil.GetWeightedRandomObjectFromList over the lots not skipped: the first with roll <= running sum (a lot of
        // weight 0 is never picked; EA could pick one only on a roll of exactly 0).
        // -1 when every lot is skipped or the kept weights sum to 0 (the caller keeps EA's pick).
        public static int Pick(float[] w, bool[] skip, float roll)
        {
            int first = -1, kept = 0;
            float run = 0f;
            for (int i = 0; i < w.Length; i++)
            {
                if (skip[i]) continue;
                kept++;
                if (first < 0) first = i;
                run += w[i];
                if (roll <= run && w[i] > 0f) return i;
            }
            if (kept == 0 || Total(w, skip) <= 0f) return -1;
            return first;  // float rounding past the end: EA returns its first entry
        }

        // the n-th lot not skipped (an unweighted pick, as RandomUtil.GetRandomObjectFromList); -1 when none
        public static int Nth(bool[] skip, int n)
        {
            for (int i = 0; i < skip.Length; i++) if (!skip[i] && n-- == 0) return i;
            return -1;
        }

        public static int Kept(bool[] skip)
        {
            int n = 0;
            for (int i = 0; i < skip.Length; i++) if (!skip[i]) n++;
            return n;
        }

        // one look at an animal: ia = its running GoToLot's id (0 = not on a GoToLot). kWalkLong once the same GoToLot has run
        // maxTicks since we first saw it (the record is dropped: the caller cancels it and remembers the lot).
        public int Walk(ulong animal, ulong ia, ulong lot, long now, long maxTicks)
        {
            WalkRec w;
            bool had = mWalks.TryGetValue(animal, out w);
            if (ia == 0) { if (had) mWalks.Remove(animal); return kWalkNone; }
            if (!had || w.Ia != ia || now < w.From)
            {
                if (!had && mWalks.Count >= kMaxWalks) mWalks.Clear();
                w.Ia = ia; w.Lot = lot; w.From = now;
                mWalks[animal] = w;
                return kWalkNew;
            }
            if (now - w.From < maxTicks) return kWalkOn;
            mWalks.Remove(animal);
            return kWalkLong;
        }

        public long WalkFrom(ulong animal)
        {
            WalkRec w;
            return mWalks.TryGetValue(animal, out w) ? w.From : -1;
        }
    }
}
