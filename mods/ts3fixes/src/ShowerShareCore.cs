namespace Ts3Fixes
{
    // Game-independent rules of ShowerShare.cs (showertogether / showertogetherhh, showerpee / showerpeehh,
    // docs/686 §15), checked offline by tools/selftest/showershare.
    public static class ShowerShareCore
    {
        public const int kNone = 0, kGo = 1, kWould = 2, kExempt = 3, kKeepRomance = 4, kKeepAge = 5, kKeepOther = 6, kNoShower = 7;
        public const int kJoined = 0, kLeaving = 1, kExiting = 2;  // a Sim of a pair: in the shower, asked to leave, playing its exit
        public const float kExitCapMinutes = 10f;    // a partner's exit that takes longer no longer holds the other Sim
        public const float kPeeGiveUpMinutes = 30f;  // a pushed shower that has not begun by then: the game's own failure
        public const float kPeeFillMinutes = 5f;     // the all-in-one bathroom's own time to fill the bladder

        // ---- showertogether ----

        // A Sim starts a shower (or an all-in-one bathroom use) at an object other Sims have (using it or in its line).
        //   others          how many other Sims; otherShowering: the one other Sim runs the same interaction there
        //                   (not a bath, a repair, a WooHoo)
        //   simPlain        a human that is no robot
        //   bothAdult       both are young adults or older (teens and children never share)
        //   romantic        the game's own test for "may ignore this Sim's privacy": a romantic relationship
        //   willing         the two do not dislike each other
        //   kGo = the Sim skips the line and the in-use refusal and showers in the same place.
        public static int Join(bool on, int others, bool otherShowering, bool simPlain, bool bothAdult, bool romantic, bool willing, bool simScope, bool otherScope)
        {
            if (others <= 0) return kNone;
            if (others > 1 || !otherShowering || !simPlain) return kKeepOther;
            if (!bothAdult) return kKeepAge;
            if (!romantic || !willing) return kKeepRomance;
            if (!on) return kWould;
            return simScope && otherScope ? kGo : kExempt;
        }

        public static bool Willing(float liking) { return liking >= 0f; }

        // A Sim of a pair is about to play its exit. It waits while the other one plays its own and is still using the
        // shower, up to the cap.
        public static bool ExitWait(int otherState, bool otherStillUsing, float minutesOtherExiting)
        {
            return otherState == kExiting && otherStillUsing && minutesOtherExiting < kExitCapMinutes;
        }

        // The in-use test after the walk to the shower: every Sim using it must be of the pair.
        public static bool UseOk(int users, int usersOfPair) { return users == usersOfPair; }

        // ---- showerpee ----

        // A toilet, urinal or all-in-one bathroom this Sim could use now.
        public static bool ToiletUsable(bool broken, bool usedByOther, int othersInLine, bool sexOk)
        {
            return !broken && !usedByOther && othersInLine <= 0 && sexOk;
        }

        // A shower this Sim could use now: free, or held by a partner showertogether lets it join.
        public static bool ShowerUsable(bool broken, int others, int joinDecision)
        {
            if (broken) return false;
            return others <= 0 || joinDecision == kGo;
        }

        // A Sim whose bladder is about to fail (or has failed) on a lot.
        //   simOk   a human, child or older, no robot, not swimming
        //   kGo = it takes a shower and its bladder fills there; kNoShower = no shower either, the game's own way.
        public static int Pee(bool on, bool simOk, bool toiletUsable, bool showerUsable, bool simScope)
        {
            if (!simOk || toiletUsable) return kNone;
            if (!showerUsable) return kNoShower;
            if (!on) return kWould;
            return simScope ? kGo : kExempt;
        }

        // The shower we pushed still stands for the failure while it is queued or running and young enough, or once the
        // bladder is filling there.
        public static bool PeeHolds(bool queuedOrRunning, bool filling, float minutesSincePush)
        {
            if (!queuedOrRunning) return false;
            return filling || minutesSincePush < kPeeGiveUpMinutes;
        }
    }
}
