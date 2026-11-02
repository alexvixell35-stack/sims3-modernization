namespace Ts3Fixes
{
    // Game-independent decisions of the free vacation fixes (EaVacation.cs, issue #97). Self-check: tools/selftest/vacation.
    public static class EaVacationCore
    {
        public const int kEaMakes = 0, kEaSkips = 1, kWeSkip = 2;

        // One stored adult at the return. EA's own rule (BringAdultsBackToWorld): no description, a live Sim object or a bot
        // without stored chips = skipped. Ours on top: dead, or no longer in the trip's household (EA re-creates those: a
        // householdless one throws in Motives.CreateMotives from Sim.OnCreation).
        public static int AdultVerdict(bool hasDesc, bool hasSim, bool botWithoutChips, bool dead, bool sameHousehold)
        {
            if (!hasDesc || hasSim || botWithoutChips) return kEaSkips;
            if (dead || !sameHousehold) return kWeSkip;
            return kEaMakes;
        }

        // At a situation exit while the parents are away: re-create this adult now (alive, still in the household, no Sim).
        public static bool BringBackAtExit(bool hasDesc, bool hasSim, bool botWithoutChips, bool dead, bool sameHousehold)
        {
            return AdultVerdict(hasDesc, hasSim, botWithoutChips, dead, sameHousehold) == kEaMakes;
        }

        // An exported opportunity source/target id that names an object of this world: point it at the exported Sim
        // description's current Sim when the id no longer finds that Sim (TryResumeOpportunity moved TargetObject to a re-created
        // Sim but never OriginalTarget). An id that still finds a live non-Sim object is kept (the target was not that Sim).
        public static bool RefreshId(bool idValid, bool sameWorld, bool descSimLive, bool curFound, bool curIsSim, bool curIsThatSim)
        {
            if (!idValid || !sameWorld || !descSimLive) return false;
            if (!curFound) return true;
            return curIsSim && !curIsThatSim;
        }

        // ExpirationTime is exported from Opportunity.TimeoutTime, set only by time-based timeouts; tick 0 = none, so EA's
        // "expired" check must not apply (the opportunity's own timeout listener ends the rest).
        public static bool ExpiryApplies(long exportedTicks) { return exportedTicks != 0; }

        public const int kRepairNone = 0, kRepairBringBack = 1, kRepairEnd = 2;

        // Load repair for a trip whose parents are away. pickedUp = ParentsAreGettingPickedUp; pickupAlive = an adult still has
        // its FreeVacationPickupSituation; allHaveSims = every stored adult has a Sim object; anyAlarm = ParentsAreAway holds a
        // valid alarm handle; teens = teens in the trip after rejoining household teens.
        public static int Repair(bool pickedUp, bool pickupAlive, bool allHaveSims, bool anyAlarm, int teens)
        {
            if (pickedUp)
            {
                if (pickupAlive) return kRepairNone;   // the car is on its way: EA's pickup runs
                return allHaveSims ? kRepairEnd : kRepairBringBack; // pickup died mid-Dispose (#97 row 2): never left / half left
            }
            if (teens == 0) return kRepairBringBack;   // nobody to own the trip (teen deleted, our check not persisted)
            return anyAlarm ? kRepairNone : kRepairBringBack;
        }
    }
}
