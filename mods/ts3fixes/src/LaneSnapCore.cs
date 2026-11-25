using System;

namespace Ts3Fixes
{
    // Game-free decision of unseenlanesnap (LaneSnap.cs, docs/807), the one place that decides; selftest tools/selftest/lanesnap.
    // Input: a portal route action (elevator, door, gate, stairs, pool ladder, subway) just returned EA's
    // ContinueAndPopPathAndReplan because the Sim came out of another lane than the planned one. The rest of the planned
    // route (path 1 on) is still in the route; EA replans it whole only because its start point and the Sim's spot differ.
    public static class LaneSnapCore
    {
        // answers
        public const int kPass = 0;       // EA's replan runs (the game's behaviour)
        public const int kNear = 1;       // unseen townie already at the next path's start: skip the replan, no move
        public const int kSnap = 2;       // unseen townie: place it on the next path's start (<= kSnapXZ), skip the replan
        // first refusal (counter order). kRjFar: only a spot that is no point (NaN / infinite).
        public const int kRjNone = 0, kRjShape = 1, kRjNext = 2, kRjScope = 3, kRjFar = 4, kRjSpot = 5, kRjSeen = 6;

        // near = the D1 legreplanskip band (RouteSrcCore.LegSkipNear): where the game's own replan would start anyway
        public const float kNearXZ = 0.5f, kMaxDY = 0.5f;
        // No distance, floor, room or lot limit: they would only keep a jump from being seen. With the Sim (UfWhy 0: screen,
        // camera reach, viewers) and the target spot
        // (UfGoalUnseen) both out of view, the snap has no distance, floor, room or lot limit. Kept, because they guard the
        // route, not the view: shape (a forced replan means the world changed and the old remainder may be stale), the next
        // path continuing the portal's planned exit, a routable, non-pool, non-below-ground spot on a docked houseboat if any.
        // path 1 must start where the portal path (path 0) was planned to end, measured horizontally (an elevator's path 0
        // ends on the floor it left): then path 0 is the portal just used and not an approach leg EA inserted in front of it
        // (PortalAnimateThroughAction.DoPortal GetPathToLane)
        public const float kGapMax = 0.25f;

        // shapeOk: replan result from an action that was not forced, killed or forcibly replanned, with a remainder (>= 2 paths,
        //   not a loop), a succeeded plan, the Sim as its own follower, human, no routing parent, no slaves, not swimming.
        // unseen: the Sim is an unseen townie (UfWhy 0, has a household).
        // nextIsWalk: path 1 is a plain walk (PathType.SimPath). gap: |path 1 start - path 0 end|.
        // dx/dy/dz: path 1 start minus the Sim's spot on the floor. The last two are read only when the order reaches them.
        public static int Decide(bool shapeOk, bool unseen, bool nextIsWalk, float gap, float dx, float dy, float dz,
            bool spotOk, bool spotUnseen, out int reason)
        {
            reason = kRjShape;
            if (!shapeOk) return kPass;
            reason = kRjNext;
            if (!nextIsWalk || !(gap <= kGapMax)) return kPass;  // NaN gap refuses
            float d2 = dx * dx + dz * dz;
            reason = kRjNone;
            if (IsNear(d2, dy)) return unseen ? kNear : Rj(kRjScope, out reason);
            if (!unseen) return Rj(kRjScope, out reason);
            if (!IsPoint(d2, dy)) return Rj(kRjFar, out reason);
            if (!spotOk) return Rj(kRjSpot, out reason);
            if (!spotUnseen) return Rj(kRjSeen, out reason);
            return kSnap;
        }
        static int Rj(int why, out int reason) { reason = why; return kPass; }
        static bool IsNear(float d2, float dy) { return Math.Abs(dy) < kMaxDY && d2 < kNearXZ * kNearXZ; }  // NaN: false
        static bool IsPoint(float d2, float dy) { return !float.IsNaN(d2) && !float.IsInfinity(d2) && !float.IsNaN(dy) && !float.IsInfinity(dy); }

        // the geometry checks (lot, routable spot, camera) cost ICs: only asked when Decide can reach them
        public static bool NeedsGeo(bool shapeOk, bool unseen, bool nextIsWalk, float gap, float dx, float dy, float dz)
        {
            if (!shapeOk || !unseen || !nextIsWalk || !(gap <= kGapMax)) return false;
            float d2 = dx * dx + dz * dz;
            return !IsNear(d2, dy) && IsPoint(d2, dy);
        }

        // portal kind for the act counters: 0 elevator (cab, doors, shaft, subway / hover train stations ride the same way),
        // 1 door or gate, 2 other (stairs, pool ladders, ...); by the portal object's class name
        public static int PortalKind(string typeName)
        {
            if (typeName == null) return 2;
            if (typeName.IndexOf("Elevator", StringComparison.Ordinal) >= 0) return 0;
            if (typeName.IndexOf("Door", StringComparison.Ordinal) >= 0 || typeName.IndexOf("Gate", StringComparison.Ordinal) >= 0) return 1;
            return 2;
        }
    }
}
