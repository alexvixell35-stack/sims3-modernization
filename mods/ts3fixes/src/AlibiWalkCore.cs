using System;

namespace Ts3Fixes
{
    // Game-independent rules of alibiwalk (AlibiWalk.cs, docs/691): which planned walk of an unseen NPC is cut to its tail,
    // and where the tail starts. Offline selftest tools/selftest/alibiwalk.
    public static class AlibiWalkCore
    {
        // Sims3.SimIFace.PathType values a cut may skip over: walking paths and portals (doors, stairs, elevators). Every other
        // path (car, boat, ocean, wade, frozen pond, horse lane, stroller, UFO, elevated train) carries a route action that
        // changes the Sim's state on the way, so a route with one is never cut.
        public const uint kSimPath = 3328183143u, kPortalPath = 1348865362u, kPortalTraversalPath = 1144997326u;

        public const int kAct = 0, kShort = 1, kKind = 2, kLastNotWalk = 3, kTailTooLong = 4;

        public static bool KindOk(uint t) { return t == kSimPath || t == kPortalPath || t == kPortalTraversalPath; }

        // len: the planned route length; types: its paths in order; lastLen: the last path's length; minM / tailM: the
        // switch's tunables. kAct = cut: the Sim is set at the tail start and walks the last TailFrom(lastLen, tailM) metres.
        public static int Verdict(float len, uint[] types, float lastLen, float minM, float tailM)
        {
            if (types == null || types.Length == 0 || len < minM) return kShort;
            for (int i = 0; i < types.Length; i++) if (!KindOk(types[i])) return kKind;
            if (types[types.Length - 1] != kSimPath) return kLastNotWalk;  // the route ends in a portal: nowhere to stand before it
            if (len - TailFrom(lastLen, tailM) < minM * 0.5f) return kTailTooLong;  // too little of the walk would be skipped
            return kAct;
        }

        // The tail the Sim walks: tailM metres of the last walking path, or the whole last path when it is shorter (the tail
        // then starts where the last portal, a door or stair, set the Sim down).
        public static float TailFrom(float lastLen, float tailM) { return lastLen <= tailM ? lastLen : tailM; }

        // A replanned tail longer than this is not the tail the cut asked for (the replan went another way): undone.
        public static float TailMax(float tailM) { return tailM * 3f + 10f; }
    }
}
