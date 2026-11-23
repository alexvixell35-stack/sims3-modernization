namespace Ts3Fixes
{
    // Game-independent rules of ViewLenient.cs (viewlenient / viewlenienthh, docs/686 §16), checked offline by
    // tools/selftest/viewlenient.
    //
    // A Sim that only looks at something (contemplates its surroundings, views a painting, watches a performer or the
    // TV) needs no exact spot: any place with a view will do. The rules here say which interactions count, which
    // failed plans, how far from the target a Sim may stand per kind, and what is done per kind.
    public static class ViewLenientCore
    {
        public const int kNoKind = 0, kContemplate = 1, kArt = 2, kPerformer = 3, kTv = 4, kStars = 5;

        static bool Has(string chain, string part) { return chain != null && chain.IndexOf(part) >= 0; }

        // The kind of a running interaction. ia = the names of its class and base classes between bars, nested classes
        // with their owner ("|Drums+Watch|MusicalInstrument+WatchBase`1|...").
        //   contemplate  Terrain.Contemplate: the Sim stands near a point and looks at an object in the room
        //   art          ViewObjects: paintings, sculptures, photos, altars, anything "viewable"
        //   performer    MusicalInstrument.WatchBase: watch a Sim play an instrument
        //   tv           TV.WatchTV, standing
        //   stars        Sim.WatchTheStars: two Sims lie down on a jig (a pose for two: no other place will do)
        // Socials, Go Here and every other interaction: none.
        public static int Kind(string ia, bool targetIsSim, bool targetIsTerrain)
        {
            if (ia == null || ia.Length == 0) return kNoKind;
            if (Has(ia, "|Sim+WatchTheStars|")) return targetIsSim ? kStars : kNoKind;
            if (targetIsSim) return kNoKind;
            if (Has(ia, "|Terrain+Contemplate|")) return targetIsTerrain ? kContemplate : kNoKind;
            if (targetIsTerrain) return kNoKind;
            if (Has(ia, "|ViewObjects|")) return kArt;
            if (Has(ia, "|MusicalInstrument+WatchBase")) return kPerformer;
            if (Has(ia, "|TV+WatchTV|")) return kTv;
            return kNoKind;
        }

        // A failed plan of this kind: reachuse's (no way, no free place, a Sim on the spot), a jig with no free slot and
        // "portal locked" (a locked room is no reason for a balloon when the Sim only wanted to look). An aborted plan
        // (it may work next time), a missing object and a plain failure keep the game's behaviour. Numbers = the game's
        // RoutePlanResultType.
        public static bool PlanKind(int resultType)
        {
            return ReachUseCore.PlanKind(resultType) || resultType == 12 || resultType == 13 || resultType == 18;
        }

        public const int kKeep = 0, kHere = 1, kView = 2, kQuiet = 3;

        // What is done for a failed plan.
        //   keep   the game's behaviour: not a plan of PlanKind, a Sim out of scope (pet, rider, in a vehicle), a route
        //          whose failure the game shows no balloon for (its caller has its own answer), a route to routing slots
        //   here   contemplate: the place is arbitrary, the Sim stays where it stands (or the nearest place it can walk to)
        //   view   art, performer, tv: the nearest place within the view range, same level, no wall between
        //   quiet  stars: the interaction ends as in the game, without the balloon
        public static int Mode(int kind, bool planKind, bool simOk, bool routeShowsFail, bool slotRoute)
        {
            if (kind == kNoKind || !planKind || !simOk || !routeShowsFail) return kKeep;
            if (kind == kStars) return kQuiet;
            if (slotRoute) return kKeep;
            return kind == kContemplate ? kHere : kView;
        }

        // Farthest the Sim may stand from the target, metres (flat). gameMax = the game's own number for this target
        // when it can be read (a viewable's MaxDistanceFromObjectToView, 0.9 x an instrument's radius), else 0.
        //   contemplate 6: how far from the chosen point a place is looked for when the Sim cannot stay where it is
        //   art 4, performer 8: when the game's number cannot be read
        //   tv 6: the game's cone length (TV.kMaximumConeLengthToWatchTv, private)
        public static float Range(int kind, float gameMax)
        {
            float r;
            switch (kind)
            {
                case kContemplate: return 6f;
                case kArt: r = gameMax > 0f ? gameMax : 4f; break;
                case kPerformer: r = gameMax > 0f ? gameMax : 8f; break;
                case kTv: return 6f;
                default: return 0f;
            }
            return r < 1.5f ? 1.5f : r > 12f ? 12f : r;
        }

        // Nearest the Sim is sent: not onto the performer, not with its nose on the screen or the canvas.
        public static float MinDist(int kind) { return kind == kPerformer || kind == kTv ? 1f : kind == kArt ? 0.5f : 0f; }

        // The failed route must be the one to the thing looked at (goal unknown: taken as that).
        public static bool GoalAtTarget(int kind, bool goalKnown, float goalToTarget, float range, float slack)
        {
            if (kind == kContemplate || kind == kStars || !goalKnown) return true;
            return goalToTarget <= range + slack;
        }

        // May a contemplating Sim stay where it is? Standing on the ground, not sitting, lying or swimming.
        public static bool StayHere(bool inPosture, bool swimming) { return !inPosture && !swimming; }

        // Is a place good to look from? ReachUseCore's answer (in range, same level, no wall between); a TV is watched
        // from its front side only.
        public static int Stand(int kind, float flat, float dy, bool wallBetween, bool inFront, float range, float maxDy)
        {
            int st = ReachUseCore.Stand(flat, dy, wallBetween, range, maxDy);
            if (st == ReachUseCore.kStandOk && kind == kTv && !inFront) return ReachUseCore.kStandSight;
            return st;
        }
    }
}
