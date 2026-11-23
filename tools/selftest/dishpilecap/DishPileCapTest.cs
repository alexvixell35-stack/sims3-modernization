using System;
using Ts3Fixes;

// Offline self-check of DishPileCapCore (docs/652).
static class DishPileCapTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static void Main()
    {
        // household rule: any household tie or a view keeps the lot
        Check(DishPileCapCore.LotOk(false, false, false, false), "plain unseen NPC lot is capped");
        Check(!DishPileCapCore.LotOk(true, false, false, false), "active home never");
        Check(!DishPileCapCore.LotOk(false, true, false, false), "owned venue never");
        Check(!DishPileCapCore.LotOk(false, false, true, false), "household / selected / unknown-scope Sim on the lot");
        Check(!DishPileCapCore.LotOk(false, false, false, true), "lot in view never");
        Check(!DishPileCapCore.LotOk(true, true, true, true), "all ties");

        const int E = DishPileCapCore.kEligible;
        Check(DishPileCapCore.Dish(true, false, false, false, true) == E, "dirty, unused, far: eligible");
        Check(DishPileCapCore.Dish(false, false, false, false, true) == DishPileCapCore.kFresh, "food left: someone's meal");
        Check(DishPileCapCore.Dish(true, true, false, false, true) == DishPileCapCore.kUsed, "in use / held / routed to");
        Check(DishPileCapCore.Dish(true, false, true, false, true) == DishPileCapCore.kPlace, "buffet, picnic, inventory");
        Check(DishPileCapCore.Dish(true, false, false, true, true) == DishPileCapCore.kPending, "EA auto-destroy already set");
        Check(DishPileCapCore.Dish(true, false, false, false, false) == DishPileCapCore.kSeen, "near a viewer");
        Check(DishPileCapCore.Dish(false, true, true, true, false) == DishPileCapCore.kPlace, "place wins over every other reason");
        Check(DishPileCapCore.Dish(true, true, false, true, false) == DishPileCapCore.kUsed, "used wins over pending and seen");

        // quota: only the part above the cap, bounded by the eligible ones and the budget
        Check(DishPileCapCore.Quota(10, 0, 10, 20) == 0, "at the cap: none");
        Check(DishPileCapCore.Quota(9, 0, 10, 20) == 0, "below the cap: none");
        Check(DishPileCapCore.Quota(90, 0, 10, 20) == 20, "203-broadcaster lot (90 dishes): budget 20");
        Check(DishPileCapCore.Quota(90, 0, 10, 100) == 80, "big budget: down to the cap");
        Check(DishPileCapCore.Quota(5, 10, 10, 20) == 5, "pending count toward the pile, never more than eligible");
        Check(DishPileCapCore.Quota(12, 0, 10, 0) == 0, "budget spent");
        Check(DishPileCapCore.Quota(0, 50, 10, 20) == 0, "nothing eligible");
        Check(DishPileCapCore.Quota(12, -3, 10, 20) == 2, "negative pending ignored");

        Check(DishPileCapCore.Cap(10) == 10 && DishPileCapCore.Cap(0) == 10 && DishPileCapCore.Cap(-5) == 10 && DishPileCapCore.Cap(5000) == 10 && DishPileCapCore.Cap(1) == 1, "cap tunable bounds");
        Check(DishPileCapCore.Budget(20) == 20 && DishPileCapCore.Budget(0) == 20 && DishPileCapCore.Budget(1000) == 1000, "budget tunable bounds");

        Console.WriteLine(sFails == 0 ? "dishpilecap selftest: PASS" : "dishpilecap selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
