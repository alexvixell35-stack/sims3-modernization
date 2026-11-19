namespace Ts3Fixes
{
    // Game-independent rule of petarrivepoint (PetArrivePoint.cs, docs/656 §4.1). Self-check: tools/selftest/petarrivepoint.
    // Decide: what Lot.PlanRouteIntoLot does for one call. EA's body first plans to the lot's cached "best route into the lot"
    // point (validated once per lot, for whoever asks first: on a lot without apartment slots, a spot just inside the front
    // door), or validates it now, and only then falls back to the mailbox and PlanToLotEx. An animal the front door does not
    // let through (EA's CommonDoor.IsAllowedThrough: horses through any non-barn door, strays on a lot they were not greeted
    // on, a door locked for pets) cannot reach that inside point, so the plan fails PortalLocked, and when the animal is the
    // first to ask, EA clears the lot's point for every later Sim too. Skip = run EA's mailbox / PlanToLotEx tail only.
    public static class PetArrivePointCore
    {
        public const int kEa = 0, kSkip = 1, kExempt = 2, kNothing = 3;

        // animal: not human; exempt: household, selected or unknown scope; apartments: lot with virtual residential slots (its
        // point is found per actor through portals, not the front door); frontDoor: FindFrontDoor() found one; allowed: EA's
        // IsAllowedThrough(actor) on it; validated / hasPoint: the lot's cache state.
        public static int Decide(bool animal, bool exempt, bool worldLot, bool apartments, bool frontDoor, bool allowed, bool validated, bool hasPoint)
        {
            if (!animal || worldLot || apartments) return kEa;
            if (validated && !hasPoint) return kNothing;  // EA goes straight to the mailbox anyway
            if (!frontDoor || allowed) return kEa;
            if (exempt) return kExempt;
            return kSkip;
        }

        public static string Name(int d)
        {
            switch (d)
            {
                case kEa: return "ea";
                case kSkip: return "skip";
                case kExempt: return "exempt";
                case kNothing: return "nothing to skip";
                default: return "?";
            }
        }
    }
}
