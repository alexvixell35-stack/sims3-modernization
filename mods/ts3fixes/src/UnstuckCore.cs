namespace Ts3Fixes
{
    // Game-independent rules of Unstuck.cs (unstuck / unstuckhh, docs/686 §18), checked offline by
    // tools/selftest/unstuck.
    //
    // A Sim boxed in by furniture (placed round it in build mode, or left there by a reset or a snap) fails every
    // route from its spot; the game's own stuck check looks once a night, only off the active lot, and answers with a
    // reset. The rules here say when a Sim counts as stuck (several failed plans from one spot over a grace time, or
    // the game's stuck check found no way), which Sims are never moved, and which probe result means "walk out
    // through the objects".
    public static class UnstuckCore
    {
        public const int kMinFails = 3;           // failed plans from one spot before the Sim is looked at
        public const float kGraceMinutes = HhSharedCore.kGraceMin;  // and at least this long since the first of them (2-3 s at normal speed)
        public const float kSpotTol = 0.6f;       // "one spot": this close to the first failure (flat), metres
        public const float kWindowMinutes = 120f; // a failure this long after the last one starts a new count
        public const float kAwayMin = 3f;         // "a way out": a normal plan to a place at least this far away
        public const float kAwayMax = 10f;
        public const float kClipMax = 12f;        // longest walk through objects, metres
        public const float kCooldownMinutes = 30f;

        // nearest first: the rings (inner radius) tried for the place to walk out to
        public static readonly float[] kRings = { 0.75f, 1.5f, 3f };

        // A failed plan that says "no way from here": no destination, the places at the goal are all obstructed, no
        // path. A Sim on the goal, a locked door, an aborted plan and slot or object failures say nothing about the
        // Sim's own spot. Numbers = the game's RoutePlanResultType.
        public static bool PlanKind(int resultType) { return resultType == 10 || resultType == 15 || resultType == 16; }

        public static bool SameSpot(float flat, float dy, float tol, float maxDy)
        {
            float h = dy < 0f ? -dy : dy;
            return flat <= tol && h <= maxDy;
        }

        // Does the count go on (true) or start again (false)?
        public static bool Continues(bool sameSpot, long now, long last, long window)
        {
            return sameSpot && now >= last && now - last <= window;
        }

        // Is the Sim looked at now? The game's stuck check (every plan to random lot points failed, the Sim has not
        // moved for 20-60 sim-minutes) stands for the count and the grace: one more failed plan is enough.
        public static bool Due(int fails, long sinceFirst, long grace, bool gameSignal)
        {
            if (gameSignal) return fails >= 1;
            return fails >= kMinFails && sinceFirst >= grace;
        }

        // Sims that are meant to stay where they are, or that something else moves, are never walked out.
        public static bool SimOk(bool inPosture, bool inWater, bool mounted, bool hasRoutingParent, bool baby, bool onALot, bool hasExitReason)
        {
            return !inPosture && !inWater && !mounted && !hasRoutingParent && !baby && onALot && !hasExitReason;
        }

        // A successful plan of the Sim from where it stands that leads this far away: it is not stuck.
        public static bool PlanClears(bool succeeded, bool fromOwnSpot, float length)
        {
            return succeeded && fromOwnSpot && length >= kAwayMin;
        }

        public const int kFree = 0, kClip = 1, kWalled = 2;

        // normalOk   a normal plan to a place kAwayMin..kAwayMax away succeeds: the Sim can walk, it is not boxed in
        // clipOk     a plan through objects (walls still count) to some ring succeeds, at most kClipMax long
        // endOpen    and from that plan's end a normal plan leads kAwayMin away (the end is no second box)
        public static int Verdict(bool normalOk, bool clipOk, float clipLen, bool endRoutable, bool endOpen)
        {
            if (normalOk) return kFree;
            return clipOk && clipLen <= kClipMax && endRoutable && endOpen ? kClip : kWalled;
        }
    }
}
