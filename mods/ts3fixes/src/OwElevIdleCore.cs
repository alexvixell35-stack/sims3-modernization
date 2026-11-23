namespace Ts3Fixes
{
    // Game-independent rule of owelevidle (OwElevIdle.cs, docs/655). Self-check: tools/selftest/owelevidle.
    // One entry of an elevator's ElevatorPortalComponent.mAssignedSims, seen just before NRaas Overwatch's CleanupElevators
    // resets every Sim it finds there. A Sim that is not riding by any sign EA keeps (not in OnPortalStart's wait, no live
    // ride task, no route, not in the interior's use list, not standing inside an elevator) is an idle entry: its entry is
    // dropped with EA's UnregisterSim and Overwatch never sees it. Anything that looks like a ride is left to Overwatch;
    // exempt Sims (household, selected, unknown scope) are counted and left to Overwatch too.
    public static class OwElevIdleCore
    {
        public const int kNotCase = 0, kRiding = 1, kExempt = 2, kDrop = 3;

        // present: the entry's SimDescription has a created Sim (Overwatch skips the rest).
        public static int Decide(bool present, bool sleeping, bool liveTask, bool routing, bool usingInterior, bool insideElevator, bool exempt)
        {
            if (!present) return kNotCase;
            if (sleeping || liveTask || routing || usingInterior || insideElevator) return kRiding;
            return exempt ? kExempt : kDrop;
        }
    }
}
