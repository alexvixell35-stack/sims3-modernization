namespace Ts3Fixes
{
    // Game-independent decisions of the docs/500 fixes (EaBugs500.cs), kept free of game types so the offline self-test
    // (tools/selftest/eabugs500) compiles it alone.
    public static class Eb500Core
    {
        // playpenflags, TakeOut: after an aborted run, a pick-up mark still naming this toddler is stale.
        public static bool PpClearId(ulong mark, ulong toddlerId) { return mark != 0 && mark == toddlerId; }

        // playpenflags, PlayWithOutsideSim: after an aborted run, "playing with an outside Sim" is stale when it names this adult.
        public static bool PpClearOutside(bool playing, ulong outsideId, ulong adultId) { return playing && adultId != 0 && outsideId == adultId; }

        // playpenflags, PlaySocial: after an aborted run, the sync mark is stale when it was clear at entry (this run set it).
        public static bool PpClearSync(bool atEntry, bool now) { return !atEntry && now; }

        // voodoobindwait: the wait for a sleeping Sim ends when the binder was cancelled or the sleeper is gone.
        public static bool VbStop(bool cancelled, bool gone) { return cancelled || gone; }

        // lotentrypet: a non-human plan that left the lot's entry cache validated with no spot must not decide for everyone.
        public static bool LeUnvalidate(bool validatedAtEntry, bool validatedNow, bool human, bool spotInvalid)
        {
            return !validatedAtEntry && validatedNow && !human && spotInvalid;
        }

        // lotentrypet, world load: a validated cache with no spot is decided again by the next plan (EA's build/buy reset).
        public static bool LeRepair(bool validated, bool spotInvalid) { return validated && spotInvalid; }

        // horselotentry: Lot.PlanRouteIntoLot's door plan is doomed for this horse. It plans its own route from the ground
        // (level 0), the door spot is on another level, every portal of the lot that links two levels refuses horses, and
        // the lot has a mailbox (so EA's fallback overwrites the whole route state). Player Sims are left out.
        public static bool HlDoomed(bool horse, bool player, bool followerIsActor, int horseLevel, int goalLevel, bool mailbox, bool portalsRefuse)
        {
            return horse && !player && followerIsActor && horseLevel == 0 && goalLevel != 0 && mailbox && portalsRefuse;
        }

        // horselotentry: a portal whose openings all lie on one level (a door, an arch, a fence gate) cannot take a horse to
        // another level. A portal with no openings is not known to be one-level, so it must refuse the horse too.
        public static bool HlOneLevel(int[] openingLevels)
        {
            if (openingLevels == null || openingLevels.Length == 0) return false;
            foreach (int l in openingLevels) if (l != openingLevels[0]) return false;
            return true;
        }

        // horselotentry, off arm: EA's door plan reached the door after all (site 2 keeps the spot) or the route ends above
        // or below the ground on this lot. Either one means the doomed prediction was wrong.
        public static bool HlMispredict(int site, bool spotKept, bool succeeded, int destLevel, bool destOnLot)
        {
            return (site == 2 && spotKept) || (succeeded && destOnLot && destLevel != 0);
        }
    }
}
