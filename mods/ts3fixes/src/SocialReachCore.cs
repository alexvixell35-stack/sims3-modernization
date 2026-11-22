namespace Ts3Fixes
{
    // Game-independent rules of SocialReach.cs (socialreach / socialreachhh, docs/686 §17), checked offline by
    // tools/selftest/socialreach.
    //
    // A social between two Sims needs both on exact spots: the starter walks to within 3 m of the partner, then the
    // partner's side places the starter at the social's distance in front of it. Either step fails when furniture, a
    // counter, a fence or a third Sim stands in the way, and the social ends with the route-fail balloon although the
    // two could talk from where they stand. The rules here say which socials may be done from a distance, which
    // failed plans count, and whether the Sims stay, the starter walks nearer, or the game's answer stays.
    public static class SocialReachCore
    {
        public const float kTalkMax = 3f;   // farthest two Sims talk from, metres (flat): the game's own outer radius of the starter's approach
        public const float kWalkMax = 2f;   // a starter that has to walk is sent to within this of the partner
        public const float kWalkMin = 0.7f; // and not nearer than the game's usual social distance
        public const float kTurnSlack = 0.3f;  // a route whose goal lies this close to its Sim is a turn on the spot

        // A failed plan of this kind: no destination, the places at the goal are all obstructed, no path, a Sim on the
        // place. "Portal locked" (a locked door), an aborted plan, slot failures and a missing object keep the game's
        // behaviour. Numbers = the game's RoutePlanResultType.
        public static bool PlanKind(int resultType)
        {
            return resultType == 10 || resultType == 15 || resultType == 16 || resultType == 17;
        }

        public const int kOther = 0, kTalk = 1, kContact = 2;

        // May this social be done from a distance?
        //   exact     both interactions are the game's plain social classes (a subclass is a social with its own steps:
        //             pillow fight, tag, catch, dance, a pool social)
        //   jigKind   the social's jig in its data (ActionData.SocialJig); 0 = "one person": each Sim plays its own
        //             animation. Anything else is a paired animation (hug, kiss, slap, handshake, fight): the game
        //             itself then checks the pair's distance and alignment before it plays
        //   ownJig    the social made its own jig (forced by its data, or a horse or a full-size dog takes part)
        //   humans    both are humans (pet socials are played nose to nose)
        //   baby      one of them is a baby (held or in a crib)
        //   mounted   one of them rides or is ridden
        //   inWater   one of them swims or dives
        public static int Cover(bool exact, bool dataKnown, int jigKind, bool ownJig, bool humans, bool baby, bool mounted, bool inWater)
        {
            if (!exact || !dataKnown) return kOther;
            if (jigKind != 0 || ownJig) return kContact;
            if (!humans || baby || mounted || inWater) return kOther;
            return kTalk;
        }

        // Socials whose hands or bodies meet although their data has no paired jig (seen: High Five played 2.3 m apart,
        // the hands did not meet; sr hand run 2026-11-22). Matched on the social's key, case-insensitive.
        static readonly string[] kTouchNames = { "high five", "fist bump", "secret handshake", "tickle", "poke", "noogie",
            "back rub", "massage", "pat on", "hold hands", "pinch", "nuzzle", "cuddle", "caress", "tackle", "pull finger" };
        public static bool TouchByName(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string k = key.ToLowerInvariant();
            for (int i = 0; i < kTouchNames.Length; i++) if (k.IndexOf(kTouchNames[i], System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public const int kApproach = 1, kReceive = 2, kPlace = 3;  // the starter's own route, the partner's own route, the pair's placement

        public const int kKeep = 0, kHere = 1, kWalk = 2;

        // What to do for a covered social whose route or placement failed.
        //   stand       ReachUseCore.kStand* of the place the Sim stands on (or walks to) against the partner, for kTalkMax
        //   inPosture   the Sim sits or lies in something
        //   moving      the starter is still on a walk (placement only: its end point is what was tested)
        //   turnRoute   the failed route is a turn on the spot
        //   helperRoute the partner's walk to a placement jig the game made for this try (it has more tries of its own)
        // The partner never walks to the starter (the game's own order); a wall or too great a distance sends the
        // starter to the nearest place within kWalkMax with no wall between; another level keeps the game's answer.
        public static int Step(int site, int stand, bool inPosture, bool moving, bool turnRoute, bool helperRoute)
        {
            if (site == kReceive && helperRoute) return kKeep;
            if (stand == ReachUseCore.kStandOk) return inPosture && site != kPlace ? kKeep : kHere;
            if (stand == ReachUseCore.kStandLevel) return kKeep;
            if (site == kReceive || inPosture || moving) return kKeep;
            if (site == kApproach && turnRoute) return kKeep;  // a turn that failed out of reach: not the walk to the partner
            return kWalk;
        }

        // A player's Sim on either side needs the household switch.
        public static int Decide(bool playerA, bool playerB, bool on, bool hhOn)
        {
            return BlockedUseCore.Decide(true, playerA || playerB, on, hhOn);
        }
    }
}
