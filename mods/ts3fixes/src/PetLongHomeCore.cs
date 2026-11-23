using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of petlonghome (PetLongHome.cs, docs/656 §8.2). Self-check: tools/selftest/petlonghome.
    // Pre: the cheap gates, in order: a pet (cats, dogs, horses), not exempt, not held (carried, ridden, riding, leashed or
    // parented), no followers, not already on the lot, and a long trip: straight distance > minDist m, or this trip (pet,
    // lot) first seen more than maxWalkMin sim-minutes ago. Post: after the unseen checks (the pet and the goal lot) and the
    // safe spot search. Trip table: the first GoHome / GoToLot Run of a pet toward a lot starts its clock; a run toward
    // another lot starts a new trip; Done (placed, arrived) ends it. Bounded (kMax).
    public sealed class PetLongHomeCore
    {
        public const int kMax = 256;
        public const int kOk = 0, kNotPet = 1, kExempt = 2, kHeld = 3, kFollowers = 4, kOnLot = 5, kShort = 6,
            kSeen = 7, kGoalSeen = 8, kNoSpot = 9;

        public static int Pre(bool pet, bool exempt, bool held, bool followers, bool onLot, float distM, double tripMin, float minDistM, float maxWalkMin)
        {
            if (!pet) return kNotPet;
            if (exempt) return kExempt;
            if (held) return kHeld;
            if (followers) return kFollowers;
            if (onLot) return kOnLot;
            if (distM > minDistM || (maxWalkMin > 0f && tripMin > maxWalkMin)) return kOk;
            return kShort;
        }

        public static int Post(bool petUnseen, bool goalUnseen, bool spot)
        {
            if (!petUnseen) return kSeen;
            if (!goalUnseen) return kGoalSeen;
            if (!spot) return kNoSpot;
            return kOk;
        }

        public static string Name(int d)
        {
            switch (d)
            {
                case kOk: return "ok";
                case kNotPet: return "not a pet";
                case kExempt: return "exempt";
                case kHeld: return "held";
                case kFollowers: return "followers";
                case kOnLot: return "on the lot";
                case kShort: return "short trip";
                case kSeen: return "pet seen";
                case kGoalSeen: return "lot seen";
                case kNoSpot: return "no safe spot";
                default: return "?";
            }
        }

        struct Trip { public ulong Lot; public long From; }
        readonly Dictionary<ulong, Trip> mTrips = new Dictionary<ulong, Trip>();

        public int Count { get { return mTrips.Count; } }

        // sim-minutes since this pet's trip to lot began (0 for a new trip; a clock that went back starts a new one)
        public double TripMinutes(ulong pet, ulong lot, long now, long ticksPerMinute)
        {
            Trip t;
            if (mTrips.TryGetValue(pet, out t) && t.Lot == lot && t.From <= now)
                return ticksPerMinute > 0 ? (double)(now - t.From) / ticksPerMinute : 0;
            if (mTrips.Count >= kMax) mTrips.Clear();
            t.Lot = lot;
            t.From = now;
            mTrips[pet] = t;
            return 0;
        }

        public void Done(ulong pet) { mTrips.Remove(pet); }

        // docs/656 §4.3 no-spot memo (kPetLongHomeNoSpotMinutes, 0 = off as before): a (lot, species class) whose SafeSpot
        // search found no spot is refused without a new search until `until`. Each search is a PlanFromPoint per candidate
        // (s130 ON arm: 180-500 ms ufplan lines "src spot:petlonghome@..."), and a wild-horse herd asks once per member.
        readonly Dictionary<ulong, Trip> mNoSpot = new Dictionary<ulong, Trip>();  // Lot = until, From = noted at
        public int NoSpotCount { get { return mNoSpot.Count; } }

        public static ulong NoSpotKey(ulong lot, int speciesClass) { return unchecked(lot * 8UL + (ulong)(speciesClass & 7)); }

        // live while noted <= now < until; a clock that went back drops the entry
        public bool NoSpotLive(ulong key, long now)
        {
            Trip t;
            if (!mNoSpot.TryGetValue(key, out t)) return false;
            if (t.From <= now && now < (long)t.Lot) return true;
            mNoSpot.Remove(key);
            return false;
        }

        public void NoSpotNote(ulong key, long now, long life)
        {
            if (life <= 0 || GoodLive(key, now)) return;  // another member of the herd found a spot meanwhile: that answer wins
            if (mNoSpot.Count >= kMax) mNoSpot.Clear();
            Trip t;
            t.From = now;
            t.Lot = (ulong)(now + life);
            mNoSpot[key] = t;
        }

        // docs/656 §4.5 herd spot share (same life as the no-spot memo; 0 = off). s136 (Appaloosa Plains): a wild-horse herd's
        // members each ran their own SafeSpot search on the same lot at the same time (spotasync lets one search wait on its
        // job while the next member starts), one 150-400 ms plan per member, and a member whose search ended without a spot
        // (also an unseen gate that failed during the wait) noted "no spot" while the others were placed, so later members
        // walked (16 of the ON arm's 28 Wild Horse walk plans followed a "(remembered)" refusal). Good: a spot that was just
        // verified and used for (lot, species class) is handed to the next member until it expires; Busy: a search for the
        // key is running in another task (started < staleTicks ago), so a member waits for its answer instead of searching.
        struct Spot { public long From, Until; public float X, Y, Z, FX, FY, FZ; }
        readonly Dictionary<ulong, Spot> mGood = new Dictionary<ulong, Spot>();
        readonly Dictionary<ulong, long> mBusy = new Dictionary<ulong, long>();
        public int GoodCount { get { return mGood.Count; } }

        public bool GoodLive(ulong key, long now)
        {
            Spot t;
            if (!mGood.TryGetValue(key, out t)) return false;
            if (t.From <= now && now < t.Until) return true;
            mGood.Remove(key);
            return false;
        }

        // the spot as x, y, z, fx, fy, fz (6 floats) when live
        public bool GoodGet(ulong key, long now, float[] v)
        {
            if (v == null || v.Length < 6 || !GoodLive(key, now)) return false;
            Spot t = mGood[key];
            v[0] = t.X; v[1] = t.Y; v[2] = t.Z; v[3] = t.FX; v[4] = t.FY; v[5] = t.FZ;
            return true;
        }

        // a verified spot: kept for `life`, and it clears that key's no-spot entry
        public void GoodNote(ulong key, long now, long life, float x, float y, float z, float fx, float fy, float fz)
        {
            if (life <= 0) return;
            if (mGood.Count >= kMax) mGood.Clear();
            Spot t;
            t.From = now; t.Until = now + life; t.X = x; t.Y = y; t.Z = z; t.FX = fx; t.FY = fy; t.FZ = fz;
            mGood[key] = t;
            mNoSpot.Remove(key);
        }

        // a bad spot (the placement found it unusable): drop it
        public void GoodDrop(ulong key) { mGood.Remove(key); }

        public bool Busy(ulong key, long now, long staleTicks)
        {
            long at;
            if (!mBusy.TryGetValue(key, out at)) return false;
            if (at <= now && now - at < staleTicks) return true;
            mBusy.Remove(key);  // stale (a task killed mid-search) or the clock went back
            return false;
        }

        public void Begin(ulong key, long now) { if (mBusy.Count >= kMax) mBusy.Clear(); mBusy[key] = now; }
        public void End(ulong key) { mBusy.Remove(key); }

        // species classes for the memo keys: horses, big dogs, little dogs, cats (each its own route profile)
        public static int SpeciesClass(bool horse, bool littleDog, bool dog) { return horse ? 1 : littleDog ? 3 : dog ? 2 : 4; }
    }
}
