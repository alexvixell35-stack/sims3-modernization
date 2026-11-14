using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent parts of replanlog (ReplanLog.cs, docs/261 §6 L1), checked offline by tools/selftest/replanlog.
    public static class ReplanLogCore
    {
        public const int kHousehold = 0, kSelected = 1, kSeenNpc = 2, kUnseen = 3, kUnknown = 4;
        public static readonly string[] kScope = { "household", "selected", "seenNpc", "unseen", "unknown" };

        // why = UfWhy (UnseenFallbackCore codes, -1 = the check threw, -2 = no Sim follower). Not live / gone / failed = unknown.
        public static int Scope(int why, bool selected)
        {
            if (selected) return kSelected;
            if (why == UnseenFallbackCore.kHousehold) return kHousehold;
            if (why == UnseenFallbackCore.kSeen) return kUnseen;
            if (UnseenFallbackCore.SeenNpc(why)) return kSeenNpc;
            return kUnknown;
        }

        // "a:b" pairs sorted by b (ms) then a (calls), largest first, at most n
        public static string Top(Dictionary<string, int[]> d, int n)
        {
            List<KeyValuePair<string, int[]>> l = new List<KeyValuePair<string, int[]>>(d);
            l.Sort(delegate(KeyValuePair<string, int[]> a, KeyValuePair<string, int[]> b)
            {
                int c = b.Value[1].CompareTo(a.Value[1]);
                return c != 0 ? c : b.Value[0].CompareTo(a.Value[0]);
            });
            System.Text.StringBuilder s = new System.Text.StringBuilder();
            for (int i = 0; i < l.Count && i < n; i++)
            {
                if (i > 0) s.Append("; ");
                s.Append(l[i].Key).Append(' ').Append(l[i].Value[0]).Append('/').Append(l[i].Value[1]).Append(" ms");
            }
            return s.Length > 0 ? s.ToString() : "none";
        }
    }

    // Burst = more than kHotN - 1 Replan calls within kWinMs (docs/261: bursts ~7 calls/s). A burst starts at the call that makes the
    // window hot (the window's calls seed it) and ends at the first call more than kWinMs after the last hot call (EndIfQuiet,
    // checked before that call is added). Times are int ms (Environment.TickCount), compared unchecked.
    // shortcut: an open burst ends only at the next Replan call (none in the last seconds of a session = no end line); add a
    // timer if a session ever ends mid-burst.
    public sealed class ReplanBurst
    {
        public const int kWinMs = 1000, kHotN = 4, kMaxKeys = 64;
        const int kRing = 64;
        readonly int[] mT = new int[kRing], mMs = new int[kRing], mScope = new int[kRing];
        readonly string[] mSim = new string[kRing], mIa = new string[kRing];
        int mHead, mCount;
        public bool Active;
        public int Id, StartMs, LastHotMs, LastMs, Calls, Ms;
        public readonly int[] ScopeCalls = new int[5], ScopeMs = new int[5];
        public readonly Dictionary<string, int[]> Sims = new Dictionary<string, int[]>(), Ias = new Dictionary<string, int[]>();

        public bool EndIfQuiet(int now)
        {
            if (!Active || unchecked(now - LastHotMs) <= kWinMs) return false;
            Active = false;
            return true;
        }

        // after the call; true = this call started a burst
        public bool Add(int now, int ms, int scope, string sim, string ia)
        {
            mHead = (mHead + 1) % kRing;
            mT[mHead] = now; mMs[mHead] = ms; mScope[mHead] = scope; mSim[mHead] = sim; mIa[mHead] = ia;
            if (mCount < kRing) mCount++;
            int n = InWindow(now, null);
            bool hot = n >= kHotN, started = false;
            if (hot) LastHotMs = now;
            if (hot && !Active)
            {
                Active = true; started = true; Id++;
                Calls = Ms = 0;
                System.Array.Clear(ScopeCalls, 0, 5); System.Array.Clear(ScopeMs, 0, 5);
                Sims.Clear(); Ias.Clear();
                StartMs = now;
                for (int k = 0, i = mHead; k < n; k++, i = (i + kRing - 1) % kRing)
                {
                    Count(mMs[i], mScope[i], mSim[i], mIa[i]);
                    if (unchecked(mT[i] - StartMs) < 0) StartMs = mT[i];
                }
            }
            else if (Active) Count(ms, scope, sim, ia);
            if (Active) LastMs = now;
            return started;
        }

        // calls in the last kWinMs (sim != null: that Sim's only), newest first, ring order = time order
        public int InWindow(int now, string sim)
        {
            int n = 0;
            for (int k = 0, i = mHead; k < mCount; k++, i = (i + kRing - 1) % kRing)
            {
                if (unchecked(now - mT[i]) >= kWinMs) break;
                if (sim == null || sim == mSim[i]) n++;
            }
            return n;
        }

        void Count(int ms, int scope, string sim, string ia)
        {
            Calls++; Ms += ms;
            ScopeCalls[scope]++; ScopeMs[scope] += ms;
            Bump(Sims, sim, ms);
            Bump(Ias, ia, ms);
        }

        static void Bump(Dictionary<string, int[]> d, string key, int ms)
        {
            int[] v;
            if (!d.TryGetValue(key, out v))
            {
                if (d.Count >= kMaxKeys) key = "(other)";
                if (!d.TryGetValue(key, out v)) d[key] = v = new int[2];
            }
            v[0]++; v[1] += ms;
        }
    }
}
