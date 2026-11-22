namespace Ts3Fixes
{
    // Game-independent rules of ReachUse.cs (reachuse / reachusehh, docs/686 §12), checked offline by
    // tools/selftest/reachuse.
    //
    // A chore whose spot is closed (a puddle in a nook, a fire behind a sofa, a trash pile between two chairs) ends
    // with the route-fail balloon although the Sim could stand a step away and do it from there. The rules here say
    // which interactions count, how far a Sim may stand from the target per kind, when the place it stands on is good
    // enough, and when a chore that needs an object's routing slot is handed to blockeduse's rules instead.
    public static class ReachUseCore
    {
        public const int kNoKind = 0, kFire = 1, kFloor = 2, kSmall = 3, kPlant = 4, kChore = 5;

        // A failed plan of this kind: no way or no free place at the goal, whoever stands there (the chore is done
        // from beside it). "Portal locked" (a privacy-locked room), an aborted plan and a missing object keep the game's
        // behaviour. Numbers = the game's RoutePlanResultType.
        public static bool PlanKind(int resultType)
        {
            return resultType == 10 || resultType == 14 || resultType == 15 || resultType == 16 || resultType == 17;
        }

        static bool Has(string chain, string part) { return chain != null && chain.IndexOf(part) >= 0; }

        static bool HasAny(string chain, string[] parts)
        {
            foreach (string p in parts) if (Has(chain, p)) return true;
            return false;
        }

        // target type names are whole names between bars ("|Fire|GameObject|")
        static bool IsA(string chain, string[] names)
        {
            foreach (string n in names) if (Has(chain, "|" + n + "|")) return true;
            return false;
        }

        static readonly string[] kSmallTargets = { "TrashPile", "AshPile", "Newspaper", "Homework", "LitterPile", "FoodScrap", "ToyScraps" };
        static readonly string[] kSmallActs = { "CleanUp", "PutAwayLeftovers", "Recycle", "PickUp", "ThrowAway", "DisposeTrash", "PutInInventory" };
        static readonly string[] kChoreActs = { "Clean", "WashDishes", "Repair", "Unclog", "FillBowl", "EmptyTrash", "TakeOutTrash", "MakeBed" };

        // The kind of a running interaction. ia = the names of its class and base classes, target = the names of the
        // target's class and base classes, each between bars. A Sim as target counts only for putting out a Sim on fire.
        //   fire   put out a fire, a burning object or a burning Sim
        //   floor  mop a puddle or a burnt tile
        //   small  pick up or clean away a small thing on the floor (trash, ash, dishes, newspaper, homework, scraps)
        //   plant  tend a plant (water, weed, harvest, fertilize, ...)
        //   chore  clean, repair, wash up at, fill: done at the object's routing slot
        public static int Kind(string ia, string target, bool targetIsSim, bool targetGardening)
        {
            if (ia == null || ia.Length == 0) return kNoKind;
            if (Has(ia, "ExtinguishBase") || Has(ia, "ExtinguishSim")) return kFire;
            if (targetIsSim) return kNoKind;
            if (Has(ia, "Mopping")) return kFloor;
            if (targetGardening) return kPlant;
            if (IsA(target, kSmallTargets) || HasAny(ia, kSmallActs)) return kSmall;
            return HasAny(ia, kChoreActs) ? kChore : kNoKind;
        }

        // Farthest the Sim may stand from the target, metres (flat). 0 = this kind is not done from a distance.
        //   fire 4: the game's own extinguish ring ends at 2.5 m; the spray is drawn from the Sim to the fire
        //   floor 2.5: the game mops from 1 m; one more tile and a half
        //   small, plant 2: the game picks up from about 0.5 m and tends from 0.7 m; an arm and a tile more
        public static float Reach(int kind)
        {
            switch (kind)
            {
                case kFire: return 4f;
                case kFloor: return 2.5f;
                case kSmall: case kPlant: return 2f;
                default: return 0f;
            }
        }

        // Nearest the Sim is sent: not into the fire.
        public static float MinDist(int kind) { return kind == kFire ? 0.5f : 0f; }

        // The failed route must be the one to the interaction's target (a clean-up also walks to the dishwasher). A
        // terrain target (a puddle) has no place of its own: the route's goal is the place.
        public static bool GoalAtTarget(bool targetIsTerrain, bool goalKnown, float goalToTarget, float slack)
        {
            if (!goalKnown) return false;
            return targetIsTerrain || goalToTarget <= slack;
        }

        public const int kStandOk = 0, kStandFar = 1, kStandLevel = 2, kStandSight = 3;

        // Is a place good to do the chore from? In reach (flat), on the target's level, no wall between.
        public static int Stand(float flat, float dy, bool wallBetween, float reach, float maxDy)
        {
            float h = dy < 0f ? -dy : dy;
            if (h > maxDy) return kStandLevel;
            if (flat > reach) return kStandFar;
            return wallBetween ? kStandSight : kStandOk;
        }

        // A failed route to routing slots is blockeduse's case. reachuse lends it its switches when the interaction is
        // one of its kinds and the slots are the target's own (or those of the surface the target lies on).
        public static bool SlotFallback(int kind, bool slotsOfTarget) { return kind != kNoKind && slotsOfTarget; }

        // The puddle tile itself is covered by an object: the game refuses to mop it. With the fix on a tile covered by
        // anything but a Sim is mopped.
        public static bool MopCovered(bool on, bool blockerKnown, bool blockerIsSim) { return on && blockerKnown && !blockerIsSim; }
    }
}
