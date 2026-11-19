using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of houseboatgangwayrescue (HouseboatGangwayRescue.cs, docs/857 §3). Self-check:
    // tools/selftest/houseboatgangwayrescue. A Sim (object id) is marked with its houseboat (object id) when its gangway leg
    // found no path; the mark sums the ms and count of those legs. A rescue takes the mark; at most one rescue per Sim inside
    // kHourTicks-long windows (the caller passes one sim-hour in ticks). Exempt Sims are never marked and never rescued.
    public sealed class HouseboatGangwayRescueCore
    {
        public const int kMax = 64;

        // Mark results
        public const int kMarked = 0, kAdded = 1, kExempt = 2, kNotDocked = 3, kHour = 4, kNoBoat = 5;
        // Check results (kOk, or a refusal; kExempt / kNotDocked / kHour / kNoBoat as above)
        public const int kOk = 0, kNotMarked = 6, kBusy = 7, kOnLot = 8, kOtherBoat = 9, kFar = 10;

        public struct Mark
        {
            public ulong Boat;
            public long From;   // sim tick of the first failed leg of this mark
            public int Ms, Legs;
        }

        readonly Dictionary<ulong, Mark> mMarks = new Dictionary<ulong, Mark>();
        readonly Dictionary<ulong, long> mRescued = new Dictionary<ulong, long>();  // sim -> sim tick of its last rescue

        public int Count { get { return mMarks.Count; } }

        // inside the once-per-hour window of an earlier rescue (a clock that went back, another save, ends the window)
        bool TooSoon(ulong sim, long now, long hourTicks)
        {
            long last;
            if (!mRescued.TryGetValue(sim, out last)) return false;
            if (now >= last && now - last < hourTicks) return true;
            mRescued.Remove(sim);
            return false;
        }

        // a failed gangway leg of sim toward boat (0 = no houseboat found). A stale mark (older than one hour, or from a clock
        // that went back) or one for another houseboat starts over.
        public int Note(ulong sim, ulong boat, bool exempt, bool docked, int ms, long now, long hourTicks)
        {
            if (exempt) { mMarks.Remove(sim); return kExempt; }
            if (boat == 0) return kNoBoat;
            if (!docked) { mMarks.Remove(sim); return kNotDocked; }
            if (TooSoon(sim, now, hourTicks)) return kHour;
            Mark m;
            if (mMarks.TryGetValue(sim, out m) && m.Boat == boat && now >= m.From && now - m.From < hourTicks)
            {
                m.Ms += ms < 0 ? 0 : ms;
                m.Legs++;
                mMarks[sim] = m;
                return kAdded;
            }
            if (mMarks.Count >= kMax && !mMarks.ContainsKey(sim)) Prune(now, hourTicks);
            m = new Mark();
            m.Boat = boat; m.From = now; m.Ms = ms < 0 ? 0 : ms; m.Legs = 1;
            mMarks[sim] = m;
            return kMarked;
        }

        public bool IsMarked(ulong sim) { return mMarks.ContainsKey(sim); }

        // at the safe point: may sim be rescued to boat now? kOk leaves the mark for Done; a refusal that can never pass for this
        // mark (exempt, another or no houseboat, not docked, already on the lot, far from the gangway, the hour rule, a stale mark)
        // drops it; kBusy keeps it.
        public int Check(ulong sim, ulong boat, bool exempt, bool docked, bool onLot, bool far, bool busy, long now, long hourTicks, out Mark m)
        {
            if (!mMarks.TryGetValue(sim, out m)) return kNotMarked;
            int why = exempt ? kExempt : boat == 0 ? kNoBoat : boat != m.Boat ? kOtherBoat : !docked ? kNotDocked
                : onLot ? kOnLot : far ? kFar : TooSoon(sim, now, hourTicks) ? kHour : now < m.From || now - m.From >= hourTicks ? kNotMarked
                : busy ? kBusy : kOk;
            if (why != kOk && why != kBusy) mMarks.Remove(sim);
            return why;
        }

        // the rescue ran (placed or not): the mark is gone and the hour starts
        public void Done(ulong sim, long now)
        {
            mMarks.Remove(sim);
            if (mRescued.Count >= kMax && !mRescued.ContainsKey(sim)) mRescued.Clear();
            mRescued[sim] = now;
        }

        // a full mark table drops its stale marks, then everything (bounded, no scan otherwise)
        void Prune(long now, long hourTicks)
        {
            List<ulong> old = new List<ulong>();
            foreach (KeyValuePair<ulong, Mark> kv in mMarks)
                if (now < kv.Value.From || now - kv.Value.From >= hourTicks) old.Add(kv.Key);
            for (int i = 0; i < old.Count; i++) mMarks.Remove(old[i]);
            if (mMarks.Count >= kMax) mMarks.Clear();
        }

        public static string Why(int why)
        {
            switch (why)
            {
                case kExempt: return "exempt";
                case kNotDocked: return "not docked";
                case kHour: return "rescued within the hour";
                case kNoBoat: return "no houseboat";
                case kNotMarked: return "not marked";
                case kBusy: return "busy";
                case kOnLot: return "on the houseboat lot";
                case kOtherBoat: return "another houseboat";
                case kFar: return "far from the gangway";
                default: return "ok";
            }
        }

        public void Clear() { mMarks.Clear(); mRescued.Clear(); }
    }
}
