using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of petstartleskip (PetStartleSkip.cs, docs/656 §8.1). Self-check: tools/selftest/petstartleskip.
    // A pet (object id) whose startle flee route failed with FailedBecauseDestinationPortalLocked (RoutePlanResultType 18)
    // is kept for a caller-given life in sim ticks. While it lives, further startles of that pet are skipped, except forced
    // ones (EA's ignoreCooldown, and the danger startles the caller names: electrocution, Grim Reaper, fire, meteor). Exempt
    // pets (household, selected, unknown scope) are never recorded and never skipped. Not extended by a skip.
    public sealed class PetStartleSkipCore
    {
        public const int kMax = 128;
        public const int kPortalLocked = 18;  // RoutePlanResultType.FailedBecauseDestinationPortalLocked
        public const int kRecorded = 0, kExempt = 1, kNotRecorded = 2;
        public const int kPass = 0, kSkip = 1, kPassExempt = 2, kPassForced = 3;

        struct Entry { public long From, Until; }
        readonly Dictionary<ulong, Entry> mMemo = new Dictionary<ulong, Entry>();

        public int Count { get { return mMemo.Count; } }

        public int Record(ulong pet, bool isPet, bool exempt, int result, long now, long lifeTicks)
        {
            if (!isPet || result != kPortalLocked || pet == 0 || lifeTicks <= 0) return kNotRecorded;
            if (exempt) return kExempt;
            if (mMemo.Count >= kMax) Prune(now);
            Entry e;
            e.From = now;
            e.Until = now + lifeTicks;
            mMemo[pet] = e;
            return kRecorded;
        }

        public bool Live(ulong pet, long now)
        {
            Entry e;
            if (!mMemo.TryGetValue(pet, out e)) return false;
            if (e.From <= now && now < e.Until) return true;
            mMemo.Remove(pet);
            return false;
        }

        public int Startle(ulong pet, bool forced, bool exempt, long now)
        {
            if (mMemo.Count == 0 || !Live(pet, now)) return kPass;
            if (exempt) return kPassExempt;
            if (forced) return kPassForced;
            return kSkip;
        }

        void Prune(long now)
        {
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, Entry> kv in mMemo)
                if (kv.Value.From > now || now >= kv.Value.Until) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mMemo.Remove(old[i]);
            if (mMemo.Count >= kMax) mMemo.Clear();
        }
    }
}
