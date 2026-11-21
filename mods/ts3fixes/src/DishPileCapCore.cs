namespace Ts3Fixes
{
    // Game-independent rule of dishpilecap (DishPileCap.cs, docs/652). Self-check: tools/selftest/dishpilecap.
    public static class DishPileCapCore
    {
        // per-dish verdicts
        public const int kEligible = 0, kFresh = 1, kUsed = 2, kPlace = 3, kPending = 4, kSeen = 5;

        // The per-lot cap and the per-pass budget from the tunables (bad values fall back to the defaults).
        public static int Cap(int tuned) { return tuned >= 1 && tuned <= 1000 ? tuned : 10; }
        public static int Budget(int tuned) { return tuned >= 1 && tuned <= 1000 ? tuned : 20; }

        // Lot gate. The household rule comes first: the active household's home, a venue it owns, any lot with a household,
        // selected or unknown-scope Sim on it (IsPlayerSim), and a lot the camera can see (UfLotSeen) are never touched.
        public static bool LotOk(bool activeHome, bool ownedByActive, bool playerSimOnLot, bool lotSeen)
        {
            return !activeHome && !ownedByActive && !playerSimOnLot && !lotSeen;
        }

        // One dish. dirty: EA's own cleanup test for an autonomous Sim (CanCleanUpOrRecycleTest: !HasFood || IsSpoiled);
        // a fresh serving with food left is someone's meal and stays. used: InUse, ActorsUsingMe or RoutingReferenceList
        // (EA's OnAutoDestroy test) or held by a Sim. place: in an inventory or out of the world, or on a buffet table,
        // picnic blanket or bake sale table (EA's CleanUp refuses those too: they have their own owners). pending: EA's
        // auto-destroy alarm already runs on it (bar food, servings from a group that has one): EA removes it itself.
        // far: outside the camera's reach and away from every household Sim (FarFromViewers).
        public static int Dish(bool dirty, bool used, bool place, bool pending, bool far)
        {
            if (place) return kPlace;
            if (used) return kUsed;
            if (!dirty) return kFresh;
            if (pending) return kPending;
            if (!far) return kSeen;
            return kEligible;
        }

        // How many of a lot's eligible dirty dishes go this pass: only the part of the pile above the cap (EA's pending ones
        // count as part of the pile, since they leave anyway), never more than are eligible, nor more than the budget left.
        public static int Quota(int eligible, int pending, int cap, int budgetLeft)
        {
            if (eligible <= 0 || budgetLeft <= 0) return 0;
            int over = eligible + (pending > 0 ? pending : 0) - cap;
            if (over <= 0) return 0;
            if (over > eligible) over = eligible;
            return over < budgetLeft ? over : budgetLeft;
        }
    }
}
