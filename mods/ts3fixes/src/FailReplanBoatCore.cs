namespace Ts3Fixes
{
    // Game-independent rule of failreplanboat (FailReplanBoat.cs, docs/851 §8). Self-check: tools/selftest/failreplanboat.
    // Asked after every plan. A failed plan whose follower is a boat driven by an NPC nobody can see gets the route's own
    // "no route-fail show" flag (Route.DoRouteFail = false), so SimRoutingComponent.DoRouteFailureBehavior returns at its
    // first test: no fail-path plan toward the blocker, no walk, no turn, no animation.
    public static class FailReplanBoatCore
    {
        public const int kNotOurs = 0;   // succeeded, or the follower is not a boat: nothing counted
        public const int kNoDriver = 1;  // a boat without a driver: kept (nobody to judge the scope by)
        public const int kFlagOff = 2;   // DoRouteFail is false already (the caller's choice, or EA's own fail-path plan): nothing to do
        public const int kExempt = 3;    // the driver is household, selected, of unknown scope, seen, or some occupant is: kept
        public const int kWould = 4;     // the rule holds, switch off: kept, counted
        public const int kOff = 5;       // the rule holds, switch on: DoRouteFail = false

        // heldUnseen: UfWhy answered kHeld for the driver (every household and camera test passed; the Sim is held by something);
        // npcOnly: UfVehicleNpcOnly (held only by the vehicle it sits in, no household or selectable occupant)
        public static int Decide(bool succeeded, bool boat, bool driver, bool doRouteFail, bool heldUnseen, bool npcOnly, bool on)
        {
            if (succeeded || !boat) return kNotOurs;
            if (!driver) return kNoDriver;
            if (!doRouteFail) return kFlagOff;
            if (!heldUnseen || !npcOnly) return kExempt;
            return on ? kOff : kWould;
        }
    }
}
