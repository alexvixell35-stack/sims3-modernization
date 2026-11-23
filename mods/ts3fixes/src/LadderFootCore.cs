namespace Ts3Fixes
{
    // Game-independent rules of LadderFoot.cs (ladderfootshoo, docs/183 §9), checked offline by tools/selftest/ladderfoot.
    //
    // The route search treats a level link (ladder, stair) as a wall when a standing Sim's footprint covers a lane end.
    // A link is "closed by Sims" when the game's own slot test finds every lane end free of objects and walls, and at
    // least one end of every usable lane taken by a Sim. Doors and spiral stairs ask such Sims to step away when a Sim
    // arrives at them; a plan that never gets as far as the link has nobody to ask.
    public static class LadderFootCore
    {
        public const int kLaneFree = 0, kLaneSims = 1, kLaneOther = 2;
        // BlockedStatus of the game's slot test
        public const int kFree = 0, kSim = 1;

        // A failed plan of a Sim to another level of the lot it stands on, with no path (noPath also stands for "destination
        // portal locked": the answer when the Sim already stands at the closed link). Other failures have their own reasons
        // (no goal); a goal on the same level does not need a level link. A door that is really locked gives no case: the
        // scan wants a level link closed by Sims.
        public static bool Wants(bool noPath, bool onLot, bool sameLot, int simLevel, int goalLevel)
        {
            return noPath && onLot && sameLot && simLevel != goalLevel;
        }

        // One lane from the slot test at its two ends, without Sims counted (what the lane test of stairs and ladders
        // asks) and with them. -1 = the test threw.
        public static int Lane(int aNoSims, int aWithSims, int bNoSims, int bWithSims)
        {
            if (aNoSims != kFree || bNoSims != kFree) return kLaneOther;  // an object, a wall, or not known
            if (aWithSims == kFree && bWithSims == kFree) return kLaneFree;
            bool a = aWithSims == kFree || aWithSims == kSim, b = bWithSims == kFree || bWithSims == kSim;
            return a && b ? kLaneSims : kLaneOther;
        }

        // The link's lanes: closed by Sims = no lane is free and at least one is closed by Sims alone.
        public static bool ClosedBySims(int[] lanes, int n)
        {
            int sims = 0;
            for (int i = 0; i < n; i++)
            {
                if (lanes[i] == kLaneFree) return false;
                if (lanes[i] == kLaneSims) sims++;
            }
            return sims > 0;
        }
        public static bool HasFreeLane(int[] lanes, int n)
        {
            for (int i = 0; i < n; i++) if (lanes[i] == kLaneFree) return true;
            return false;
        }

        // Is a Sim found at a taken lane end one to ask? The game's rule at doors and spiral stairs: every Sim within the
        // radius that is not walking (a walking Sim leaves by itself), never the Sim that plans.
        public static bool Blocker(bool isPlanner, bool destroyed, bool routing)
        {
            return !isPlanner && !destroyed && !routing;
        }

        public const int kKeep = 0, kShoo = 1, kWould = 2, kExempt = 3;

        // What to do for a failed plan. needed = a link closed by Sims lies on the way and no other link between the same
        // two levels has a free lane.
        public static int Decide(bool needed, int blockers, bool fixOn, bool householdOn, bool plannerExempt)
        {
            if (!needed || blockers <= 0) return kKeep;
            if (!fixOn) return kWould;
            if (plannerExempt && !householdOn) return kExempt;
            return kShoo;
        }

        // May this standing Sim be asked? A household or selected Sim only with the household switch; a Sim that already
        // has the game's step-away queued or running is not asked twice (it is waited for).
        public static bool MayAsk(bool householdOn, bool blockerExempt, bool alreadyAsked)
        {
            return !alreadyAsked && (householdOn || !blockerExempt);
        }

        // The planning Sim may wait for the step-aways only in its own task, where a sleep is allowed, and not while it
        // holds an unplaced social jig (asyncjigsync: such a plan must not let other tasks run).
        public static bool MayWait(int asked, bool ownTask, bool mayYield, bool jigPending, float capMinutes)
        {
            return asked > 0 && ownTask && mayYield && !jigPending && capMinutes > 0f;
        }

        public const int kWaitOn = 0, kWaitReplan = 1, kWaitTimeout = 2, kWaitCanceled = 3;

        // One poll of the wait (the game's own wait for pushed Sims polls every 10 ticks): pending = asked Sims that still
        // have the step-away queued or running.
        public static int WaitStep(int pending, float minutesWaited, float capMinutes, bool canceled)
        {
            if (canceled) return kWaitCanceled;
            if (pending <= 0) return kWaitReplan;
            return minutesWaited >= capMinutes ? kWaitTimeout : kWaitOn;
        }

        // The wait's cap: the tuning value, never above the game's own cap for waiting on pushed Sims.
        public static float WaitCap(float tunedMinutes, float gameMinutes)
        {
            if (tunedMinutes <= 0f) return 0f;
            return gameMinutes > 0f && gameMinutes < tunedMinutes ? gameMinutes : tunedMinutes;
        }

        // One look per Sim per cooldown: a Sim that fails every few ticks does not scan the lot each time.
        public static bool Due(bool seenBefore, long nowTicks, long lastTicks, long cooldownTicks)
        {
            return !seenBefore || nowTicks < lastTicks || nowTicks - lastTicks >= cooldownTicks;
        }

        public static string LaneName(int lane)
        {
            return lane == kLaneFree ? "free" : lane == kLaneSims ? "Sims" : "blocked";
        }
        public static readonly string[] kWaitNames = { "waiting", "the Sims stepped away", "timeout", "canceled" };

        // Is an object with a portal component a way between levels (LevelLink.cs)? Its lane ends on two levels, or it is one
        // of EA's classes that link levels through a second object (elevator doors and cars, tube elevators).
        public static bool LinksLevels(int endLevelA, int endLevelB, string typeName)
        {
            if (endLevelA != endLevelB) return true;
            return typeName != null && typeName.IndexOf("Elevator", System.StringComparison.Ordinal) >= 0;
        }

        // Does a stair between these two levels lie on the way from simLevel to goalLevel (either direction)?
        public static bool StairOnWay(int bottomLevel, int topLevel, int simLevel, int goalLevel)
        {
            int lo = simLevel < goalLevel ? simLevel : goalLevel, hi = simLevel < goalLevel ? goalLevel : simLevel;
            int b = bottomLevel < topLevel ? bottomLevel : topLevel, t = bottomLevel < topLevel ? topLevel : bottomLevel;
            return b >= lo && t <= hi && b != t;
        }
    }
}
