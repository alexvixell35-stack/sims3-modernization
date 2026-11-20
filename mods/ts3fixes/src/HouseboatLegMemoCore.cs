using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of houseboatlegmemo (HouseboatLegMemo.cs, docs/853 §2). Self-check: tools/selftest/houseboatlegmemo.
    // One entry per router (Sim or boat object id): the start and goal of its last no-path boat-leg plan, the sim-tick window it
    // stands for and the plan's ms. A plan is answered only from the same start and goal (each within kNearM) inside the window.
    public sealed class HouseboatLegMemoCore
    {
        public const float kNearM = 0.5f;
        public const int kMax = 64;

        public struct Entry
        {
            public float Sx, Sy, Sz, Gx, Gy, Gz;
            public long From, Until;
            public int Ms;
        }

        readonly Dictionary<ulong, Entry> mMap = new Dictionary<ulong, Entry>();

        public int Count { get { return mMap.Count; } }

        static bool Near(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx, dy = ay - by, dz = az - bz;
            return dx * dx + dy * dy + dz * dz <= kNearM * kNearM;
        }

        // true = this plan (router id, start, goal) at sim tick now repeats a recorded no-path plan; e = that entry.
        // An entry out of its window (expired, or a clock that went back: another save) is dropped here.
        public bool Hit(ulong id, float sx, float sy, float sz, float gx, float gy, float gz, long now, out Entry e)
        {
            if (!mMap.TryGetValue(id, out e)) return false;
            if (now < e.From || now >= e.Until) { mMap.Remove(id); return false; }
            return Near(sx, sy, sz, e.Sx, e.Sy, e.Sz) && Near(gx, gy, gz, e.Gx, e.Gy, e.Gz);
        }

        // a plan that ran and found no path (no Sim or vehicle blocker): its entry, replacing the router's older one.
        // A full table drops its expired entries first, then everything (bounded, no per-call scan otherwise).
        public void Record(ulong id, float sx, float sy, float sz, float gx, float gy, float gz, long now, long lifeTicks, int ms)
        {
            if (lifeTicks <= 0) return;
            if (mMap.Count >= kMax && !mMap.ContainsKey(id))
            {
                List<ulong> old = new List<ulong>();
                foreach (KeyValuePair<ulong, Entry> kv in mMap)
                    if (now < kv.Value.From || now >= kv.Value.Until) old.Add(kv.Key);
                for (int i = 0; i < old.Count; i++) mMap.Remove(old[i]);
                if (mMap.Count >= kMax) mMap.Clear();
            }
            Entry e = new Entry();
            e.Sx = sx; e.Sy = sy; e.Sz = sz; e.Gx = gx; e.Gy = gy; e.Gz = gz;
            e.From = now; e.Until = now + lifeTicks; e.Ms = ms;
            mMap[id] = e;
        }

        public void Clear() { mMap.Clear(); }
    }
}
