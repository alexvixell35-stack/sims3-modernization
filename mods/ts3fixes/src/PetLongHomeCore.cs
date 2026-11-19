using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent rule of petlonghome (PetLongHome.cs, docs/645 §4). Self-check: tools/selftest/petlonghome.
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
    }
}
