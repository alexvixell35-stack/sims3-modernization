using System;

namespace Ts3Fixes
{
    // Game-independent rules of WooHooSnap.cs (woohoosnap / woohoosnaphh and pairsnap / pairsnaphh, docs/686 §20),
    // checked offline by tools/selftest/woohoosnap.
    //
    // WooHoo: the two partners agreed, then one of them cannot walk to its spot at the bed, shower or other object and
    // the WooHoo ends with the route-fail balloon. Paired socials (kiss, hug): the two stand next to each other, the
    // game finds no place to line them up, the social ends the same way. The rules here say which interactions and
    // failed plans count, who is in scope, and where a Sim is put.
    public static class WooHooSnapCore
    {
        // ---- woohoosnap ----

        // An interaction class is part of a WooHoo when its full name (with the classes it is nested in) says so:
        // Shower+WooHoo, Wardrobe+WooHooInWardrobeA, Beds.TryForBaby, UnderwaterCave+WoohooInCave ...
        public static bool WooHooName(string fullTypeName)
        {
            if (fullTypeName == null) return false;
            return fullTypeName.IndexOf("WooHoo", StringComparison.OrdinalIgnoreCase) >= 0 || fullTypeName.IndexOf("TryForBaby", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A failed plan of this kind is a route failure: no destination, no free slot, the places all obstructed, no
        // path, a Sim on the place, and the same at the start (the Sim cannot leave where it stands). A locked door
        // (18), an aborted plan (3), a missing object or an object without such slots keep the game's answer. Numbers
        // = the game's RoutePlanResultType.
        public static bool PlanKind(int resultType)
        {
            return resultType == 8 || resultType == 9 || resultType == 10 || resultType == 14 || resultType == 15 || resultType == 16 || resultType == 17;
        }

        // Scope: a player's Sim on either side needs the household switch.
        public static int Decide(bool playerA, bool playerB, bool on, bool hhOn)
        {
            return BlockedUseCore.Decide(true, playerA || playerB, on, hhOn);
        }

        public const int kBed = 0, kPlumbing = 1, kOtherObject = 2;

        // The object's group for the counters: beds (tents, igloos and the other bed classes with them), showers, tubs,
        // hot tubs, all-in-one bathrooms and saunas, anything else. The catalogue's showers and tubs are classes of
        // Objects.Plumbing.Mimics (ShowerBasic...): run ws-m1 counted their snaps as "other object".
        public static int Group(bool isBed, string typeNamespace, string typeName)
        {
            if (isBed) return kBed;
            if ((typeNamespace != null && (typeNamespace.EndsWith(".Plumbing", StringComparison.Ordinal) || typeNamespace.IndexOf(".Plumbing.", StringComparison.Ordinal) >= 0)) || (typeName != null && typeName.StartsWith("Sauna", StringComparison.Ordinal))) return kPlumbing;
            return kOtherObject;
        }

        public const int kKeep = 0, kSlot = 1, kPoint = 2;
        public const int kWhyNone = -1, kWhyLot = 0, kWhyPlan = 1, kWhySpot = 2, kWhySim = 3, kWhySocial = 4, kWhyStopped = 5;

        // Where a WooHoo partner whose plan failed is put.
        //   simOk      a human that walks by itself (not riding, not carried, not in a vehicle)
        //   planKind   PlanKind of the failed plan
        //   sameLot    the Sim, its partner and the object stand on one lot (not the world lot)
        //   slot       index of the nearest wanted slot of the route's object, -1 = the route has no slot destination
        //   point      the route names a place (a point, or a place found next to its object)
        //   social     the interaction is a social (its routes lead to the partner: pairsnap's and socialreach's)
        // Levels and rooms do not count: the Sim may be put on any floor of the lot.
        public static int Step(bool simOk, bool planKind, bool sameLot, int slot, bool point, bool social, out int why)
        {
            why = kWhyNone;
            if (!simOk) { why = kWhySim; return kKeep; }
            if (!planKind) { why = kWhyPlan; return kKeep; }
            if (!sameLot) { why = kWhyLot; return kKeep; }
            if (slot >= 0) return kSlot;
            if (social) { why = kWhySocial; return kKeep; }
            if (point) return kPoint;
            why = kWhySpot;
            return kKeep;
        }

        // The Sim walks up to the object's room first when a normal plan gets it there (BlockedUseCore.NeedNear says
        // whether it still has to); with no such plan it is put on its spot from where it stands.

        // ---- pairsnap ----

        public const float kPairMax = 3f;      // farthest the two stand apart, metres (flat)
        public const float kPairMaxDy = 0.133f; // the game's own height tolerance for a paired animation
        public const int kRing = 8;            // places tried round the Sim that stays

        // May this social be lined up by a snap? SocialReachCore.Cover's "contact" (a paired animation or a social with
        // its own jig), two humans, no baby, nobody riding or in water.
        public static bool PairCover(int cover, bool humans, bool baby, bool mounted, bool inWater)
        {
            return cover == SocialReachCore.kContact && humans && !baby && !mounted && !inWater;
        }

        public const int kPairKeep = 0, kPairSnap = 1, kPairTurn = 2;
        public const int kPairWhyFar = 0, kPairWhyLevel = 1, kPairWhyPosture = 2, kPairWhyMoving = 3, kPairWhyPlan = 4, kPairWhySpot = 5, kPairWhyCancel = 6;

        // What to do for a covered pair whose placement or route failed.
        //   flat, dy     the two Sims' distance and height difference; sameLevel: the game's level numbers agree
        //   inPosture    the Sim that would be moved sits or lies in something
        //   moverMoving  the Sim that would be moved is on a walk
        //   otherMoving  the Sim that stays is on a walk (its place is not final: the mover only turns to its goal)
        public static int PairStep(float flat, float dy, bool sameLevel, bool inPosture, bool moverMoving, bool otherMoving, out int why)
        {
            why = kWhyNone;
            float h = dy < 0f ? -dy : dy;
            if (!sameLevel || h > kPairMaxDy) { why = kPairWhyLevel; return kPairKeep; }
            if (flat > kPairMax) { why = kPairWhyFar; return kPairKeep; }
            if (inPosture) { why = kPairWhyPosture; return kPairKeep; }
            if (moverMoving) { why = kPairWhyMoving; return kPairKeep; }
            return otherMoving ? kPairTurn : kPairSnap;
        }

        // Place `i` (0..kRing-1) for the mover: `dist` from the Sim that stays (sx, sz), first on the line towards
        // where the mover stands (mx, mz), then 45, 90, 135 degrees to either side, last straight behind. (fx, fz) is
        // the mover's facing there: at the Sim that stays; that Sim faces (-fx, -fz).
        public static void Ring(float sx, float sz, float mx, float mz, float dist, int i, out float x, out float z, out float fx, out float fz)
        {
            float dx = mx - sx, dz = mz - sz, len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 0.001f) { dx = 1f; dz = 0f; } else { dx /= len; dz /= len; }
            int step = (i + 1) / 2;                       // 0, 1, 1, 2, 2, 3, 3, 4
            double a = step * (Math.PI / 4.0) * ((i & 1) == 1 ? 1.0 : -1.0);
            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
            float rx = dx * ca - dz * sa, rz = dx * sa + dz * ca;
            x = sx + rx * dist; z = sz + rz * dist;
            fx = -rx; fz = -rz;
        }

        // The game's own test before a paired animation (SocialInteractionA.VerifyPositionAndAlignment): the two face
        // each other (dot product -1 within 0.001), stand `dist` apart within 0.002 and at one height within 0.133.
        public static bool Aligned(float ax, float az, float afx, float afz, float bx, float bz, float bfx, float bfz, float dy, float dist)
        {
            float dot = afx * bfx + afz * bfz;
            if (Math.Abs(dot + 1f) > 0.001f) return false;
            float dx = ax - bx, dz = az - bz;
            float h = dy < 0f ? -dy : dy;
            return h <= kPairMaxDy && Math.Abs((float)Math.Sqrt(dx * dx + dz * dz) - dist) <= 0.002f;
        }

        // A plan through objects to a jig slot is walked when it is short; walls still count in such a plan.
        public static bool ClipWalk(bool planOk, float len, float clipMax) { return planOk && len <= clipMax; }
    }
}
