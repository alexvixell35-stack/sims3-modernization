namespace Ts3Fixes
{
    // Game-independent part of RouteRelax.cs (docs/675): the stage rules of GlobalFunctions.FindGoodLocationNearby's three
    // lot-wide searches as one scan (fglnonescan), the relaxed indoor rule (fglnnearspot) and the follower try count
    // (followerlasttry). Self-check: tools/selftest/routerelax.
    public static class RouteRelaxCore
    {
        // FindGoodLocationStrategies bits
        public const int kSamePosition = 1, kSameRoom = 2, kAdjacent = 4, kIndoors = 8, kSameLot = 0x10, kAnywhere = 0x20, kOutdoors = 0x40;
        public const int kEarly = kSamePosition | kSameRoom | kAdjacent;
        // a spot's class by its light-blocking room id, as EA's IsInside (> 0) and IsOutside (== 0) read it
        public const int kInside = 0, kOutside = 1, kOther = 2;
        // the three lot-wide stages, in EA's order
        public const int kStIn = 1, kStOut = 2, kStLot = 4;

        public static int SpotClass(int room) { return room > 0 ? kInside : room == 0 ? kOutside : kOther; }

        // Which lot-wide stages EA would run: indoors needs a lot and a start in a room, outdoors a lot, same lot nothing.
        public static int Stages(int searches, bool onLot, int startRoom)
        {
            int s = 0;
            if (onLot && (searches & kIndoors) != 0 && startRoom > 0) s |= kStIn;
            if (onLot && (searches & kOutdoors) != 0) s |= kStOut;
            if ((searches & kSameLot) != 0) s |= kStLot;
            return s;
        }

        public static int StageCount(int stages) { return (stages & 1) + (stages >> 1 & 1) + (stages >> 2 & 1); }

        // The stage that returns a spot of this class when EA runs them in order: 0 indoors, 1 outdoors, 2 same lot
        // (the stage without a check takes whatever the earlier ones left); -1 = no stage takes it.
        public static int Rank(int stages, int cls)
        {
            if (cls == kInside && (stages & kStIn) != 0) return 0;
            if (cls == kOutside && (stages & kStOut) != 0) return 1;
            return (stages & kStLot) != 0 ? 2 : -1;
        }

        // The first stage that runs: a spot of that rank ends EA's search at once.
        public static int BestRank(int stages) { return (stages & kStIn) != 0 ? 0 : (stages & kStOut) != 0 ? 1 : (stages & kStLot) != 0 ? 2 : -1; }

        // EA's result over the spots a scan passes, in scan order (cls[i] = class of the i-th passing spot): the index it
        // returns (-1 none) and how many whole or partial scans it ran. For the self-check and the in-game check.
        public static int EaPick(int stages, int[] cls, out int scans)
        {
            scans = 0;
            for (int rank = 0; rank < 3; rank++)
            {
                if ((stages & 1 << rank) == 0) continue;
                scans++;
                for (int i = 0; i < cls.Length; i++)
                    if (rank == 2 || (rank == 0 && cls[i] == kInside) || (rank == 1 && cls[i] == kOutside)) return i;
            }
            return -1;
        }

        // followerlasttry: the last of EA's tries (and any later one at the same place) uses the full ring
        public static bool FlWiden(int tryNo, int tries) { return tries >= 2 && tryNo >= tries; }

        public const float kFlStep = 0.6f;  // EA moves the meeting point 1.5 x 0.3 m = 0.45 m between two tries

        // One AddRouteDest call of a follower with the two side arcs. Returns the try it belongs to (1 = first) and counts
        // the call within the try (EA adds two arcs per try at the same point). A point further than kFlStep from the
        // last one, or a call later than maxTicks, starts over.
        public static int FlNote(FlState st, float x, float z, long tick, long maxTicks)
        {
            bool fresh = st.Try == 0 || tick < st.Tick || tick - st.Tick > maxTicks;
            float dx = x - st.X, dz = z - st.Z, d2 = dx * dx + dz * dz;
            if (!fresh && d2 == 0f && st.Calls < 2) st.Calls++;
            else if (!fresh && d2 <= kFlStep * kFlStep) { if (st.Try < 1000) st.Try++; st.Calls = 1; }
            else { st.Try = 1; st.Calls = 1; }
            st.X = x; st.Z = z; st.Tick = tick;
            return st.Try;
        }
    }

    public sealed class FlState
    {
        public float X, Z; public long Tick; public int Try, Calls;
    }

    // One scan in place of EA's indoors, outdoors and same-lot searches. The native search offers every spot that passed
    // its placement test, nearest first; Offer answers as the search's managed check does (true ends the search).
    //   margin < 0 (fglnonescan): a spot of the first stage's class ends the scan; of the others the first one per rank
    //   is kept, and the lowest rank wins when the scan ends. That is the spot EA's stages return, in one scan.
    //   margin >= 0 (fglnnearspot): the scan also ends at the first spot more than `margin` further out than the
    //   nearest spot seen; a better class only wins inside that margin.
    public sealed class FglnPick
    {
        readonly int mStages, mBest;
        readonly float mMargin;
        float mNear = -1f;
        public int Rank = -1, Calls;
        public float X, Y, Z, Dist;
        public bool Accepted, Stopped;

        public FglnPick(int stages, float margin)
        {
            mStages = stages; mBest = RouteRelaxCore.BestRank(stages); mMargin = margin;
        }

        public bool Offer(int cls, float x, float y, float z, float dist)
        {
            Calls++;
            int r = RouteRelaxCore.Rank(mStages, cls);
            if (r < 0) return false;
            if (mMargin >= 0f && mNear >= 0f && dist > mNear + mMargin) { Stopped = true; return true; }
            if (r == mBest) { Rank = r; X = x; Y = y; Z = z; Dist = dist; Accepted = true; return true; }
            if (mNear < 0f) mNear = dist;
            if (Rank < 0 || r < Rank) { Rank = r; X = x; Y = y; Z = z; Dist = dist; }
            return false;
        }
    }
}
