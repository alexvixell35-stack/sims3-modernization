using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of boatroamcap (BoatRoamCap.cs, docs/857 §4). Self-check: tools/selftest/boatroamcap.
    // One entry per NPC driver: the streak of Boat.BoatAround runs (first run's tick, last run's tick, roams EA ran, its boat),
    // a pending ReturnToShore this fix pushed, and a back-off window after a return that failed. A streak ends when the boat
    // changes, the gap since the last run passes Tun.GapTicks, the clock went back (another save) or a return reached the shore.
    // A run is capped (no roam, ReturnToShore instead) when the streak is long enough (Tun.MaxRoams roams done or Tun.CapTicks
    // since its first run), at night (Tun.NightFrom..NightTo, wraps midnight) or with energy or bladder below Tun.MotiveLow.
    public sealed class BoatRoamCapCore
    {
        public const int kMax = 64;

        // Check verdicts
        public const int kRoam = 0;        // EA's roam runs (counted in the streak)
        public const int kCap = 1;         // no roam: push ReturnToShore (Entry.Reason says why)
        public const int kBackoff = 2;     // EA's roam runs: a recent return failed
        public const int kReturnLost = 3;  // EA's roam runs: the return pushed last time never ran or failed; back-off starts

        // cap reasons (Rule)
        public const int kNone = 0, kStreak = 1, kNight = 2, kMotive = 3;

        public struct Tun
        {
            public int MaxRoams;       // roams done before the next run is capped
            public long CapTicks;      // sim ticks since the streak's first run before the next run is capped
            public long GapTicks;      // a run later than this after the last one starts a new streak
            public long BackoffTicks;  // after a failed return, EA roams this long before the next try
            public int NightFrom, NightTo;  // night hours [from, to), wrapping midnight; from == to = no night window
            public float MotiveLow;    // energy or bladder below this caps
        }

        public struct Entry
        {
            public ulong Boat;
            public long Start, Last, Pending, BackoffUntil;  // Pending / BackoffUntil 0 = none
            public int Roams, Reason;
        }

        readonly Dictionary<ulong, Entry> mMap = new Dictionary<ulong, Entry>();

        public int Count { get { return mMap.Count; } }

        public static bool Night(int hour, int from, int to)
        {
            if (from == to) return false;
            return from < to ? hour >= from && hour < to : hour >= from || hour < to;
        }

        // why a run with this streak and state is capped, or kNone
        public static int Rule(int roams, long since, int hour, float energy, float bladder, Tun t)
        {
            if ((t.MaxRoams > 0 && roams >= t.MaxRoams) || (t.CapTicks > 0 && since >= t.CapTicks)) return kStreak;
            if (Night(hour, t.NightFrom, t.NightTo)) return kNight;
            if (energy < t.MotiveLow || bladder < t.MotiveLow) return kMotive;
            return kNone;
        }

        public static string Why(int reason)
        {
            switch (reason)
            {
                case kStreak: return "streak";
                case kNight: return "night";
                case kMotive: return "low motive";
                default: return "none";
            }
        }

        // a BoatAround run of a non-exempt NPC driver (id) in boat at sim tick now; e = its entry after this run
        public int Check(ulong id, ulong boat, long now, int hour, float energy, float bladder, Tun t, out Entry e)
        {
            bool had = mMap.TryGetValue(id, out e);
            if (!had || e.Boat != boat || now < e.Start || now < e.Last || (t.GapTicks > 0 && now - e.Last > t.GapTicks))
            {
                if (!had) Bound(now, t.GapTicks);
                e = new Entry();
                e.Boat = boat; e.Start = now; e.Last = now;
            }
            e.Last = now;
            e.Reason = kNone;
            int v;
            if (e.Pending != 0)
            {
                // the Sim is still driving and back in BoatAround: the pushed return never got it out
                e.Pending = 0;
                e.BackoffUntil = now + t.BackoffTicks;
                e.Roams++;
                v = kReturnLost;
            }
            else if (e.BackoffUntil != 0 && now < e.BackoffUntil) { e.Roams++; v = kBackoff; }
            else
            {
                e.BackoffUntil = 0;
                e.Reason = Rule(e.Roams, now - e.Start, hour, energy, bladder, t);
                if (e.Reason == kNone) { e.Roams++; v = kRoam; }
                else v = kCap;
            }
            mMap[id] = e;
            return v;
        }

        // the capped run pushed ReturnToShore
        public void Pushed(ulong id, long now)
        {
            Entry e;
            if (!mMap.TryGetValue(id, out e)) return;
            e.Pending = now;
            mMap[id] = e;
        }

        // the capped run could not push or prepare the return (EA roams instead): back-off from now
        public void Failed(ulong id, long now, long backoffTicks)
        {
            Entry e;
            if (!mMap.TryGetValue(id, out e)) return;
            e.Pending = 0;
            e.BackoffUntil = now + backoffTicks;
            mMap[id] = e;
        }

        // ReturnToShore.Run of id ended with ok; true when it was a return this fix pushed (counted by the caller).
        // ok: the streak ends (the Sim is ashore); failed: back-off from now.
        public bool Returned(ulong id, bool ok, long now, long backoffTicks)
        {
            Entry e;
            if (!mMap.TryGetValue(id, out e) || e.Pending == 0) return false;
            if (ok) { mMap.Remove(id); return true; }
            e.Pending = 0;
            e.BackoffUntil = now + backoffTicks;
            mMap[id] = e;
            return true;
        }

        public bool TryGet(ulong id, out Entry e) { return mMap.TryGetValue(id, out e); }

        // a full table drops its stale streaks first, then everything (bounded; only when a new Sim is added)
        void Bound(long now, long gapTicks)
        {
            if (mMap.Count < kMax) return;
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, Entry> kv in mMap)
                if (now < kv.Value.Last || (gapTicks > 0 && now - kv.Value.Last > gapTicks)) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mMap.Remove(old[i]);
            if (mMap.Count >= kMax) mMap.Clear();
        }

        public void Clear() { mMap.Clear(); }
    }
}
