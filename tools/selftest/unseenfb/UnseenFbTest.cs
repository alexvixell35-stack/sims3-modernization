// Offline self-check of mods/ts3fixes/src/UnseenFallbackCore.cs (docs/202 unseen-Sim fallback: scope rule order, per-Sim
// no-retry window and cut cap).
using System;
using Ts3Fixes;

static class UnseenFbTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // scope rule: only all-clear is unseen; each test wins in the doc's order
        Check(UnseenFallbackCore.Why(true, true, false, false, false, true, false, false) == 0, "unseen");
        Check(UnseenFallbackCore.Why(false, true, false, false, false, true, false, false) == UnseenFallbackCore.kNotLive, "not live mode");
        Check(UnseenFallbackCore.Why(false, false, true, true, true, false, true, true) == UnseenFallbackCore.kNotLive, "not live first");
        Check(UnseenFallbackCore.Why(true, false, true, false, false, true, false, false) == UnseenFallbackCore.kGone, "gone");
        Check(UnseenFallbackCore.Why(true, true, true, false, false, true, false, false) == UnseenFallbackCore.kHousehold, "household / selected");
        Check(UnseenFallbackCore.Why(true, true, false, true, false, true, false, false) == UnseenFallbackCore.kActiveLot, "active lot");
        Check(UnseenFallbackCore.Why(true, true, false, false, true, true, false, false) == UnseenFallbackCore.kOnScreen, "on screen");
        Check(UnseenFallbackCore.Why(true, true, false, false, false, false, false, false) == UnseenFallbackCore.kNearViewers, "near camera or household");
        Check(UnseenFallbackCore.Why(true, true, false, false, false, true, true, false) == UnseenFallbackCore.kHeld, "rider / carrier");
        Check(UnseenFallbackCore.Why(true, true, false, false, false, true, false, true) == UnseenFallbackCore.kSocialSeen, "with a seen Sim");
        // every non-zero input combination is seen: exhaustive over the 8 flags
        for (int m = 0; m < 256; m++)
        {
            bool live = (m & 1) != 0, exists = (m & 2) != 0, hh = (m & 4) != 0, lot = (m & 8) != 0, scr = (m & 16) != 0, far = (m & 32) != 0, held = (m & 64) != 0, soc = (m & 128) != 0;
            bool unseen = live && exists && !hh && !lot && !scr && far && !held && !soc;
            Check((UnseenFallbackCore.Why(live, exists, hh, lot, scr, far, held, soc) == 0) == unseen, "rule mask " + m);
        }
        Check(UnseenFallbackCore.Cell(-0.5f) == -1 && UnseenFallbackCore.Cell(3.99f) == 3, "cell floor");

        // limiter
        UfLimiter u = new UfLimiter();
        object r;
        Check(u.MayMark(1, 1000) && !u.NoRetry(1, 0, 0, 0, 1000, out r) && r == null, "fresh Sim");
        u.Cut(1, 5, 0, 7, 1000, "fail-A");
        Check(u.NoRetry(1, 5, 0, 7, 1000 + UfLimiter.kNoRetryMs, out r) && (string)r == "fail-A", "no-retry inside the window");
        Check(!u.NoRetry(1, 5, 0, 7, 1001 + UfLimiter.kNoRetryMs, out r), "no-retry ends");
        Check(!u.NoRetry(1, 6, 0, 7, 1100, out r), "other cell plans");
        Check(!u.NoRetry(2, 5, 0, 7, 1100, out r), "other Sim plans");
        Check(!u.NoRetry(1, 5, 0, 7, 900, out r), "clock went back");
        for (int i = 1; i < UfLimiter.kMaxCuts; i++) { Check(u.MayMark(1, 1000 + i), "may mark before cap " + i); u.Cut(1, 5, 0, 7, 1000 + i, "fail"); }
        Check(!u.MayMark(1, 2000), "capped after kMaxCuts in the window");
        Check(u.MayMark(2, 2000), "cap is per Sim");
        Check(u.MayMark(1, 1001 + UfLimiter.kWindowMs), "cap ends with the window");
        u.Cut(1, 5, 0, 7, 1001 + UfLimiter.kWindowMs, "fail-B");
        Check(u.MayMark(1, 1002 + UfLimiter.kWindowMs), "new window counts from 1");
        // docs/202 §3.9.9 per-event log limiter + line format
        UrEvLog ev = new UrEvLog();
        for (int i = 0; i < UrEvLog.kMax; i++) Check(ev.May("act", 5000 + i), "ev line " + i);
        Check(!ev.May("act", 5100) && ev.Dropped == 1, "ev capped per kind in the window");
        Check(ev.May("ghost-start", 5100), "ev cap is per kind");
        Check(ev.May("act", 5000 + UrEvLog.kWindowMs), "ev window reopens");
        Check(ev.May("wrap", int.MaxValue - 10) && ev.May("wrap", unchecked(int.MaxValue + 20)), "ev tick wrap");
        Check(UrEvLog.Format("act", 12.34, 5678, 0x0000000100000002UL) == "ur: act sim-min=12.3 wall-ms=5678 sim=00000003", "ev format: " + UrEvLog.Format("act", 12.34, 5678, 0x0000000100000002UL));
        // wrap of Environment.TickCount
        UfLimiter w = new UfLimiter();
        w.Cut(9, 0, 0, 0, int.MaxValue - 100, "wrap");
        Check(w.NoRetry(9, 0, 0, 0, unchecked(int.MaxValue + 200), out r), "tick wrap");
        // table bound
        UfLimiter b = new UfLimiter();
        for (ulong s = 0; s < (ulong)UfLimiter.kMaxSims + 10; s++) b.Cut(s, 0, 0, 0, 5, "x");
        Check(b.Count <= UfLimiter.kMaxSims, "table bound");

        // §5.6 C-site push cadence and verdict cache
        Check(UnseenFallbackCore.SitesSleepTicks(0, 20, 500) == UnseenFallbackCore.kSleepDefault, "no rate: default sleep");
        Check(UnseenFallbackCore.SitesSleepTicks(8, 20, 500) == 200, "speed 3: 8 ticks / 20 ms -> 200 ticks per 500 ms");
        Check(UnseenFallbackCore.SitesSleepTicks(1, 1000, 500) == 1, "slow clock floors at 1");
        Check(UnseenFallbackCore.SitesSleepTicks(1000, 1, 500) == UnseenFallbackCore.kSleepMax, "cap");
        Check(UnseenFallbackCore.SitesSleepTicks(8, 20, -5) == 1, "late push: shortest sleep");
        Check(!UnseenFallbackCore.SitesRecheck(0.5f, 100, false) && UnseenFallbackCore.SitesRecheck(1f, 100, false) && UnseenFallbackCore.SitesRecheck(0f, UnseenFallbackCore.kRecheckMs, false) && UnseenFallbackCore.SitesRecheck(0f, -1, false), "recheck rule");
        Check(UnseenFallbackCore.SitesRecheck(0f, 100, true), "recheck on a new interaction or route");
        // docs/202 §5.8: per-tick budget of the push's Sim pass, every Sim covered once
        UfSitesPass sp = new UfSitesPass();
        for (int total = 0; total <= 3 * UnseenFallbackCore.kSitesPerTick + 1; total += 5)
        {
            sp.Begin(total);
            int covered = 0, wakes = 0, from;
            bool over = false, gap = false;
            while (!sp.Done)
            {
                int got = sp.Slice(out from);
                if (got > UnseenFallbackCore.kSitesPerTick || got <= 0) over = true;
                if (from != covered) gap = true;
                covered += got;
                if (++wakes > 100) break;
            }
            Check(!over && !gap && covered == total && wakes == (total + UnseenFallbackCore.kSitesPerTick - 1) / UnseenFallbackCore.kSitesPerTick, "sites pass: <= " + UnseenFallbackCore.kSitesPerTick + " Sims per wake, all " + total + " covered");
        }
        sp.Begin(0);
        int f0;
        Check(sp.Done && sp.Slice(out f0) == 0, "empty pass");

        // §5.7 E: loop detector (restart after a failed plan, same key, inside the window; flags once)
        UfLoopDetector ld = new UfLoopDetector(750);
        object i0 = new object();
        Check(!ld.Note(1, "Eat@5", i0, true, 0) && !ld.Note(1, "Eat@5", i0, false, 10), "first instance, replans: no restart");
        bool flagged = false;
        for (int i = 1; i <= UfLoopDetector.kLoopRestarts; i++) { bool f = ld.Note(1, "Eat@5", new object(), true, 10 + i); Check(f == (i == UfLoopDetector.kLoopRestarts), "flag on restart " + i); flagged |= f; }
        Check(flagged && !ld.Note(1, "Eat@5", new object(), true, 30), "flags once per streak");
        UfLoopDetector ld2 = new UfLoopDetector(750);
        ld2.Note(2, "K", new object(), false, 0);
        for (int i = 0; i < 10; i++) Check(!ld2.Note(2, "K", new object(), false, i + 1), "restarts after successes are not a loop");
        UfLoopDetector ld3 = new UfLoopDetector(750);
        ld3.Note(3, "K", new object(), true, 0);
        for (int i = 0; i < 3; i++) ld3.Note(3, "K", new object(), true, 1 + i);
        Check(!ld3.Note(3, "K", new object(), true, 800) && ld3.Restarts(3) == 0, "window end starts over");
        ld3.Note(3, "K", new object(), true, 801);
        Check(!ld3.Note(3, "Other", new object(), true, 802) && ld3.Restarts(3) == 0, "other key starts over");
        // §5.7 E: breaker scope (Sim side: unseen only; lot side: Rodney's lot rules) and the per-tick budget
        Check(UfLoopBreakCore.SimFix(true, UnseenFallbackCore.kSeen) && !UfLoopBreakCore.SimFix(false, UnseenFallbackCore.kSeen), "sim fix: live + unseen");
        foreach (int wy in new int[] { UnseenFallbackCore.kHousehold, UnseenFallbackCore.kActiveLot, UnseenFallbackCore.kOnScreen, UnseenFallbackCore.kNearViewers, UnseenFallbackCore.kHeld, UnseenFallbackCore.kSocialSeen })
            Check(!UfLoopBreakCore.SimFix(true, wy), "sim fix refused for why " + wy);
        Check(UfLoopBreakCore.LotFix(true, false, true, false, false, false), "lot fix: unseen own/community lot");
        Check(!UfLoopBreakCore.LotFix(true, false, true, false, true, false), "never a lot the active household owns");
        Check(!UfLoopBreakCore.LotFix(true, false, true, false, false, true), "never with a household member there or heading there");
        Check(!UfLoopBreakCore.LotFix(true, false, true, true, false, false), "never a lot in view");
        Check(!UfLoopBreakCore.LotFix(true, true, true, false, false, false) && !UfLoopBreakCore.LotFix(true, false, false, false, false, false) && !UfLoopBreakCore.LotFix(false, false, true, false, false, false), "world lot, other lot, out of live");
        UfTickBudget tb = new UfTickBudget(UfLoopBreakCore.kObjectsPerTick);
        int used = 0;
        for (int i = 0; i < 10; i++) if (tb.Try(100)) used++;
        Check(used == UfLoopBreakCore.kObjectsPerTick && tb.Try(101), "per-tick budget, resets next tick");

        // D (unseenfbpre, docs/202 §3.5): hold time, deny list, follow-up callers, in-transit table
        Check(UfPreCore.HoldMinutes(0f) == UfPreCore.kMinHoldMin, "hold floor");
        Check(System.Math.Abs(UfPreCore.HoldMinutes(100f) - 100f * UfPreCore.kDetour / UfPreCore.kWalkMPerSimMin) < 0.001f, "hold = distance x detour / speed");
        Check(UfPreCore.HoldMinutes(5000f) == UfPreCore.kMaxHoldMin, "hold capped at 2 sim-hours");
        Check(UfPreCore.Denied("Sims3.Gameplay.Core.Terrain+GoHere") && UfPreCore.Denied("Sims3.Gameplay.Actors.Sim+GoJogging")
            && UfPreCore.Denied("NRaas.GoHereSpace.Interactions.Anything") && UfPreCore.Denied(null), "walk-is-the-point denied");
        Check(!UfPreCore.Denied("Sims3.Gameplay.Objects.Seating.ChairDining+Sit") && !UfPreCore.Denied("Sims3.Gameplay.Core.GoHome"), "ordinary interactions allowed");
        Check(UfPreCore.FollowsRoute("Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToSlot")
            && UfPreCore.FollowsRoute("Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToObjectRadialRange"), "route-then-follow helpers");
        Check(!UfPreCore.FollowsRoute("Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToDynamicObjectRadiusWithCondition")
            && !UfPreCore.FollowsRoute("Sims3.Gameplay.Core.Lot.PlanToLotEx") && !UfPreCore.FollowsRoute("Sims3.Gameplay.Actors.Sim.IsPointInLotSafelyRoutable")
            && !UfPreCore.FollowsRoute(null), "plan-only and moving-target callers refused");
        const string vo = "Sims3.Gameplay.InteractionsShared.ViewObjects.";
        Check(UfPreCore.FollowsRoute(vo + "CreateRouteToObject", vo + "RouteToObject")
            && UfPreCore.FollowsRoute("Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToSlot", null)
            && !UfPreCore.FollowsRoute(vo + "CreateRouteToObject", "Sims3.Gameplay.Objects.HobbiesSkills.Trampoline+Jump.Run")
            && !UfPreCore.FollowsRoute(vo + "CreateRouteToObject", null) && !UfPreCore.FollowsRoute(vo + "RouteToObject", vo + "Run"),
            "ViewObjects: plan + DoRoute pair only");
        Check(UfPreCore.OwnFrame("Ts3Fixes.Fixes", "UfFollows") && UfPreCore.OwnFrame("Ts3Fixes.Fixes", "UfCaller")
            && !UfPreCore.OwnFrame("Ts3Fixes.Fixes", "RouteSrcNote") && !UfPreCore.OwnFrame("Sims3.Gameplay.Actors.Sim", "UfFollows")
            && !UfPreCore.OwnFrame(null, "UfFollows"), "own Uf* frames skipped by the caller read (10-27: callers = UfFollows)");
        // the game's default Sim route (EnablePlanningAsCar on, the rest off) is not a vehicle route (10-30: 2083 refused at 332)
        Check(!UfPreCore.VehicleRoute(false, false, false, false) && UfPreCore.VehicleRoute(true, false, false, false)
            && UfPreCore.VehicleRoute(false, true, false, false) && UfPreCore.VehicleRoute(false, false, true, false),
            "gate 332: drive/boat-capable walks pass (planning options are not checked), car/boat starters and no-walk refused");
        // docs/202 §3.9 relaxed: only vehicle starters refused
        Check(UfPreCore.VehicleRoute(true, false, false, true) && UfPreCore.VehicleRoute(false, false, true, true)
            && !UfPreCore.VehicleRoute(false, true, false, true), "relaxed 332: vehicle starters refused, no-walk routes pass");
        Check(System.Math.Abs(UfPreCore.TravelMinutes(100f, true) - UfPreCore.HoldMinutes(100f)) < 0.001f, "relaxed: short trips walk even with fast legs");
        Check(System.Math.Abs(UfPreCore.TravelMinutes(600f, false) - UfPreCore.HoldMinutes(600f)) < 0.001f, "relaxed: no fast legs = walk time");
        float ft = UfPreCore.TravelMinutes(600f, true);
        Check(ft < UfPreCore.HoldMinutes(600f) && System.Math.Abs(ft - (UfPreCore.kFastOverheadMin + 600f * UfPreCore.kDetour / UfPreCore.kFastMPerSimMin)) < 0.001f,
            "relaxed: long trip with fast legs = overhead + distance x detour / fast speed");
        Check(UfPreCore.TripShare(0, 100) == 0f && UfPreCore.TripShare(50, 100) == 0.5f && UfPreCore.TripShare(500, 100) == 1f && UfPreCore.TripShare(5, 0) == 0f, "snap-back share");
        Check(UfPreCore.SnapAt(0, 0.5f, 200f) == 100f && UfPreCore.SnapAt(1, 0.5f, 200f) == 90f && UfPreCore.SnapAt(9, 0.5f, 200f) == 10f
            && UfPreCore.SnapAt(10, 0.5f, 200f) < 0f, "snap-back candidates walk back to the start gap");
        Check(UfPreCore.SnapAt(0, 1f, 200f) == 190f && UfPreCore.SnapAt(0, 0.01f, 200f) < 0f && UfPreCore.SnapAt(0, 0.5f, 15f) < 0f, "snap-back: never inside the end gaps");
        // docs/202 §3.9.6-§3.9.7 unseenroute2
        Check(UrCore.BPass(1, true, false) && UrCore.BPass(1, false, true) && !UrCore.BPass(1, false, false) && !UrCore.BPass(0, true, true)
            && !UrCore.BPass(-1, true, false), "B: same colour and street / public lot only");
        Check(UrCore.GoalKey(77, false, 5f, 5f) == 77 && UrCore.GoalKey(77, false, 999f, -3f) == 77, "C key: a lot goal is its lot, any cell");
        Check(UrCore.GoalKey(0, true, 10f, 10f) == UrCore.GoalKey(0, true, 60f, 1f) && UrCore.GoalKey(0, true, 10f, 10f) != UrCore.GoalKey(0, true, 70f, 10f)
            && UrCore.GoalKey(0, true, -1f, 5f) != UrCore.GoalKey(0, true, 1f, 5f) && (UrCore.GoalKey(0, true, 1f, 1f) >> 63) == 1, "C key: 64 m street areas, flagged");
        Check(UrCore.StartKey(9, false) == 9 && UrCore.StartKey(9, true) == 0, "C start side: lot or street");
        UrCCache cc = new UrCCache();
        Check(cc.Get(1, 2) == null, "C: unknown first");
        cc.Set(1, 2, true); cc.Set(1, 0, false); cc.Set(5, 1, true);
        Check(cc.Get(1, 2) == true && cc.Get(1, 0) == false && cc.Get(2, 1) == null, "C: learned per (goal, start)");
        Check(cc.ClearLot(1) == 3 && cc.Count == 0, "C: a Build/Buy edit of a lot drops it as goal and as start");
        for (int k = 0; k < UrCCache.kMax + 5; k++) cc.Set((ulong)k + 10, 0, true);
        Check(cc.Count <= UrCCache.kMax, "C: bounded");
        Check(UrCore.HitPm(0, 0) == -1 && UrCore.HitPm(3, 1) == 750 && UrCore.HitPm(0, 4) == 0, "C hit rate per mille");
        Check(UrCore.GroupOk(true, 3, 3) && !UrCore.GroupOk(true, 3, 2) && !UrCore.GroupOk(false, 3, 3) && !UrCore.GroupOk(true, 0, 0), "groups: leader, all or none");
        // nested-plan test per task (2026-10-31 fix: a global count made every plan nested while another task slept in its plan)
        UrTaskDepth td = new UrTaskDepth();
        td.Enter(1);                                   // task 1 plans, then sleeps (route job wait / D hold)
        Check(td.Enter(2) == 1 && td.Depth(2) == 1, "depth: another task's plan meanwhile is not nested");
        td.Exit(2);
        Check(td.Enter(1) == 2 && td.Depth(1) == 2, "depth: a plan inside task 1's own plan (callback) is nested");
        td.Exit(1); td.Exit(1);
        Check(td.Depth(1) == 0 && td.Depth(2) == 0 && td.Tasks == 0, "depth: every exit balances, no leak");
        td.Exit(3);
        Check(td.Depth(3) == 0 && td.Tasks == 0, "depth: an exit without an enter is ignored");
        Check(!UrCore.DoorRefuses(false, true, false) && !UrCore.DoorRefuses(false, false, true) && UrCore.DoorRefuses(true, false, false),
            "relaxed lots: only a lock refuses (single-gender / one-way doors do not: sv-ur-car-loop beaches)");
        Check(!UrCore.StartLotCounts(true) && UrCore.StartLotCounts(false), "relaxed lots: the start lot counts only indoors");
        Check(UrCore.NearGoalAt(0) == 1f && UrCore.NearGoalAt(5) == 8f && UrCore.NearGoalAt(6) < 0f && UrCore.NearGoalAt(-1) < 0f,
            "object goals: placement tried 1-8 m back towards the start (sv-ur-car-loop: View goals are inside the statue's footprint)");
        Check(UrCore.PlacedCheck(true, false) == UrCore.kPlacedKeep && UrCore.PlacedCheck(true, true) == UrCore.kPlacedCheck
            && UrCore.PlacedCheck(false, true) == UrCore.kPlacedDrop && UrCore.PlacedCheck(false, false) == UrCore.kPlacedDrop,
            "D: a placed Sim is checked when it becomes visible, without waiting for a plan (sv-ur-fenced-repair)");
        Check(UrCore.PlacedVerdict(null) == UrCore.kPlacedKeep && UrCore.PlacedVerdict(true) == UrCore.kPlacedDrop
            && UrCore.PlacedVerdict(false) == UrCore.kPlacedRepair,
            "D: no verdict (check budget spent) keeps the Sim listed for the next sweep (sv-ur-fenced-repair, unseenroute5)");
        UrGhostLedger gl = new UrGhostLedger();
        Check(gl.Start(7) && !gl.Start(7) && gl.Live == 1, "ghost: one start per Sim");
        Check(gl.End(7) && !gl.End(7) && gl.Live == 0, "ghost: every start ends exactly once (the footprint re-insert)");
        Check(!gl.End(8), "ghost: an end without a start is refused");
        Check(UfPreCore.PlanToLotFrame("Sims3.Gameplay.Core.Lot", "PlanToLot") && UfPreCore.PlanToLotFrame("Sims3.Gameplay.Core.Lot", "PlanToLotEx")
            && !UfPreCore.PlanToLotFrame("Sims3.Gameplay.Core.Lot", "AddLotEntryPointDestinations") && !UfPreCore.PlanToLotFrame("Sims3.Gameplay.Core.LotManager", "PlanToLot")
            && !UfPreCore.PlanToLotFrame(null, "PlanToLot"), "RouteToLot: only Lot.PlanToLot(Ex) frames skipped");
        var holds = new System.Collections.Generic.Dictionary<ulong, long>();
        holds[7] = 1000;
        Check(UfPreCore.InTransit(holds, 7, 999) && UfPreCore.InTransit(holds, 7, 1000), "in transit until the end");
        Check(!UfPreCore.InTransit(holds, 7, 1001) && !UfPreCore.InTransit(holds, 8, 10) && !UfPreCore.InTransit(null, 7, 10), "past the end, other Sim, no table");

        // D-arrive / D-leave: who may leave in view, hand-off candidates
        Check(UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kActiveLot) && UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kOnScreen)
            && UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kNearViewers), "seen through the camera may leave");
        Check(!UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kHousehold) && !UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kHeld)
            && !UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kNotLive) && !UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kSocialSeen)
            && !UnseenFallbackCore.SeenButMovable(0), "household, held, social, not live, unseen: never a D-leave");
        Check(UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kHousehold) && UnseenFallbackCore.HouseholdScope(-1) && UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kNotLive) && UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kGone)
            && !UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kSeen) && !UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kActiveLot) && !UnseenFallbackCore.HouseholdScope(UnseenFallbackCore.kOnScreen), "household wait: household and unknown scope count, unseen and seen NPCs do not");
        Check(UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kActiveLot) && UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kOnScreen) && UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kNearViewers)
            && UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kHeld) && UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kSocialSeen), "seen NPC ceiling: the five proving reasons");
        Check(!UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kHousehold) && !UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kNotLive) && !UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kGone)
            && !UnseenFallbackCore.SeenNpc(-1) && !UnseenFallbackCore.SeenNpc(0) && !UnseenFallbackCore.SeenNpc(10), "household, not live, gone, failed check, unseen, unknown values: never the NPC ceiling");
        Check(UnseenFallbackCore.KdScopeOp(UnseenFallbackCore.kSeen) == 11 && UnseenFallbackCore.KdScopeOp(UnseenFallbackCore.kHeld) == 13 && UnseenFallbackCore.KdScopeOp(UnseenFallbackCore.kGoalHousehold) == 13
            && UnseenFallbackCore.KdScopeOp(UnseenFallbackCore.kHousehold) == 10 && UnseenFallbackCore.KdScopeOp(-1) == 10 && UnseenFallbackCore.KdScopeOp(UnseenFallbackCore.kGone) == 10, "scope op: unseen 11, seen NPC 13, household and unknown 10");
        Check(UnseenFallbackCore.KdHeldUnseen(true, false, false) && !UnseenFallbackCore.KdHeldUnseen(false, false, false)
            && !UnseenFallbackCore.KdHeldUnseen(true, true, false) && !UnseenFallbackCore.KdHeldUnseen(true, false, true), "held scope: only an NPC-only vehicle rider (not riding or carrying) is unseen to native");
        // docs/197 §7 goalhhexempt: seen NPC ceiling, no D-leave, a failed check exempts, the first reason wins
        Check(UnseenFallbackCore.SeenNpc(UnseenFallbackCore.kGoalHousehold) && !UnseenFallbackCore.SeenButMovable(UnseenFallbackCore.kGoalHousehold), "goal household: seen NPC scope, never a D-leave");
        Check(UnseenFallbackCore.GoalReason(true, false, false, false) == UnseenFallbackCore.kGoalNone, "nothing household: stays unseen");
        Check(UnseenFallbackCore.GoalReason(true, true, true, true) == UnseenFallbackCore.kGoalLot && UnseenFallbackCore.GoalReason(true, false, true, true) == UnseenFallbackCore.kGoalTarget
            && UnseenFallbackCore.GoalReason(true, false, false, true) == UnseenFallbackCore.kGoalWanted, "lot, then target, then wanted");
        Check(UnseenFallbackCore.GoalReason(false, false, false, false) == UnseenFallbackCore.kGoalFailed && UnseenFallbackCore.GoalReason(false, true, false, false) == UnseenFallbackCore.kGoalFailed, "failed check exempts");
        Check(UnseenFallbackCore.kGoalCounter + UnseenFallbackCore.kGoalLot == 862 && UnseenFallbackCore.kGoalCounter + UnseenFallbackCore.kGoalFailed == 865, "counter slots fixes-862");
        Check(UfPreCore.HandOffSteps(15f) == 0 && UfPreCore.HandOffSteps(20f) == 1 && UfPreCore.HandOffSteps(40f) == 3, "hand-off step count");
        int n = UfPreCore.HandOffSteps(100f);
        Check(UfPreCore.HandOffAt(0) == UfPreCore.kHandOffGapM && UfPreCore.HandOffAt(n - 1) <= 100f - UfPreCore.kHandOffGapM
            && UfPreCore.HandOffAt(n) > 100f - UfPreCore.kHandOffGapM, "candidates stay kHandOffGapM from both ends");

        // §3.6.4 lot trips: covered kinds unchanged unless unseenfbtriphold; mod subclasses only with unseenfbtrip, always held
        Check(UfPreCore.TripMode(false, false, false) == 1 && UfPreCore.TripMode(false, true, false) == 1, "covered trip: placed at once as shipped");
        Check(UfPreCore.TripMode(false, false, true) == 2 && UfPreCore.TripMode(false, true, true) == 2, "covered trip + unseenfbtriphold: held");
        Check(UfPreCore.TripMode(true, false, false) == 0 && UfPreCore.TripMode(true, false, true) == 0, "mod subclass trip off without unseenfbtrip");
        Check(UfPreCore.TripMode(true, true, false) == 2, "mod subclass trip with unseenfbtrip: held");
        Check(UfPreCore.TripScanAssembly("StudentUnionLibrary", "ts3fixes") && !UfPreCore.TripScanAssembly("ts3fixes", "ts3fixes")
            && !UfPreCore.TripScanAssembly("Sims3GameplaySystems", "ts3fixes") && !UfPreCore.TripScanAssembly("mscorlib", "ts3fixes")
            && !UfPreCore.TripScanAssembly(null, "ts3fixes"), "scan mod assemblies only");

        // docs/248 unseen boat trips (UbCore)
        Check(UbCore.Unseen(UnseenFallbackCore.kHeld) && UbCore.Unseen(UnseenFallbackCore.kSeen) && !UbCore.Unseen(UnseenFallbackCore.kOnScreen) && !UbCore.Unseen(UnseenFallbackCore.kNearViewers), "boat: no reason or held = unseen, a camera reason = seen");
        Check(UbCore.Trip(false, -1, false) == UbCore.kTransport, "boat: another land component is a crossing");
        Check(UbCore.Trip(false, 0, false) == UbCore.kExact && UbCore.Trip(false, 1, false) == UbCore.kExact, "boat: unknown or same land stays exact");
        Check(UbCore.Trip(true, 0, false) == UbCore.kHouseboat, "boat: a houseboat goal needs no colour");
        Check(UbCore.Trip(true, -1, true) == UbCore.kExact && UbCore.Trip(false, -1, true) == UbCore.kExact, "boat: same lot stays exact");
        Check(UbCore.Trip(false, 1, false, true) == UbCore.kTransport && UbCore.Trip(true, 1, false, true) == UbCore.kHouseboat && UbCore.Trip(false, 1, true, true) == UbCore.kExact && UbCore.Trip(false, 0, false, false) == UbCore.kExact, "boat: leaving a houseboat needs no colour, its own lot stays exact, switch off stays exact");
        Check(UbCore.LeaveWhy(true, false, false, false) == null && UbCore.LeaveWhy(false, false, false, false) == "no houseboat" && UbCore.LeaveWhy(true, true, false, false) == "boat moving"
            && UbCore.LeaveWhy(true, false, true, false) == "driver" && UbCore.LeaveWhy(true, false, false, true) == "start:household lot", "boat: leaving: a moving boat, its driver and the player's boat are refused by name");
        Check(UbCore.LeaveReason(true, "seen") == "houseboat start: seen" && UbCore.LeaveReason(false, "seen") == "seen", "boat: houseboat starts have their own reason rows");
        Check(UbCore.FarEnough(UbCore.kMinTripM) && !UbCore.FarEnough(UbCore.kMinTripM - 1f), "boat: short trips stay exact");
        Check(UbCore.Denied("Sims3.Gameplay.Objects.Vehicles.WindSurfboard+Sail") && UbCore.Denied("X.GoFishing") && UbCore.Denied("X.BoatTour") && !UbCore.Denied("Sims3.Gameplay.Autonomy.VisitCommunityLot"), "boat: interactions about the water stay exact");
        const string ubRun = "Ts3Fixes.Fixes+VclSubHook0.Run", ubLot = "Sims3.Gameplay.Core.Lot.";
        Check(UbCore.Follows(new[] { ubRun, "x" }) && UbCore.Follows(new[] { ubLot + "PlanToLotEx", "Sims3.Gameplay.Core.GoToCommunityLot.Run" })
            && UbCore.Follows(new[] { "Sims3.Gameplay.Abstracts.RabbitHole.RouteOutside" }), "boat: lot trips and rabbit holes follow their route");
        Check(UbCore.Follows(new[] { ubLot + "RouteToDoor", ubLot + "RouteToDoor", ubLot + "RouteToFrontDoor", ubRun }) && UbCore.Follows(new[] { ubLot + "PlanRouteIntoLot", "Ts3Fixes.Fixes+GoHomeHook.Run" }), "boat: a trip through the Lot helpers");
        // docs/247 §11.3 live stack shapes: a hooked Run has its stub between the Lot helper and the hook
        Check(UbCore.Follows(new[] { ubLot + "PlanRouteIntoLot", "Sims3.Gameplay.Core.GoHome.RunOrig", "Ts3Fixes.Fixes+GoHomeHook.Run" }), "boat: Go Home through the hook's stub");
        Check(UbCore.Follows(new[] { "Sims3.Gameplay.Objects.LynMods.StudentUnionLibrary.VisitCommunityLotLabLecture.RunOrig", "Ts3Fixes.Fixes+VclSubHook0.Run" })
            && UbCore.Follows(new[] { ubLot + "PlanToLotEx", "Sims3.Gameplay.Core.GoToLot.RunOrig", "Ts3Fixes.Fixes+GoToLotHook.Run" })
            && UbCore.Follows(new[] { ubLot + "PlanToLotEx", "Sims3.Gameplay.Core.GoToCommunityLot.RunOrig", "Ts3Fixes.Fixes+GoToCommunityLotHook.Run" })
            && UbCore.Follows(new[] { ubLot + "PlanToLotEx", "Sims3.Gameplay.Core.VisitCommunityLot.RunOrig", "Ts3Fixes.Fixes+VisitCommunityLotHook.Run" }), "boat: lot visits through their stubs");
        Check(UbCore.Follows(new[] { ubLot + "PlanToMailbox", ubLot + "RouteToMailbox", "Sims3.Gameplay.Core.VisitLot.RunOrig", "Ts3Fixes.Fixes+VisitLotHook.Run" })
            && UbCore.Follows(new[] { ubLot + "PlanToLotEx", "Sims3.Gameplay.Core.VisitLot.Run" }), "boat: VisitLot follows its route");
        Check(!UbCore.Follows(new[] { ubLot + "RouteToDoor" }) && !UbCore.Follows(new[] { ubLot + "RouteToDoor", "Sims3.Gameplay.Objects.Mailbox.RunOrig", "Ts3Fixes.Fixes+MailboxHook.Run" })
            && !UbCore.Follows(new[] { "Sims3.Gameplay.Core.GoHome.RunOrig" }) && !UbCore.Follows(new[] { "Sims3.Gameplay.Core.Terrain+GoHere.RunOrig", "Ts3Fixes.Fixes+GoHereHook.Run" })
            && !UbCore.Follows(new[] { "Sims3.Gameplay.Core.Terrain+GoHereWithHoldingInteraction.RunOrig", "Ts3Fixes.Fixes+GoHereHook.Run" }), "boat: the frame above decides, Go Here and Meet Up stay exact");
        Check(UbCore.Follows(new[] { "Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToPoint", "x" }) && UbCore.Follows(new[] { ubLot + "PlanToLot", "Sims3.Gameplay.ObjectComponents.RoutingComponent.RouteToLot" }), "boat: D's helpers still follow");
        Check(!UbCore.Follows(new[] { ubLot + "RouteToDoor", "Sims3.Gameplay.Objects.Mailbox.Run" }) && !UbCore.Follows(new[] { ubLot + "PlanToLotEx" }) && !UbCore.Follows(new string[0])
            && !UbCore.Follows(new[] { "Sims3.Gameplay.Actors.Sim.IsPointInLotSafelyRoutable", ubRun }) && !UbCore.Follows(new[] { "Sims3.Gameplay.Core.Terrain+GoHere.RouteActorToDestination" }), "boat: plan-only callers refused");
        Check(UbCore.ReplanUnseen(UnseenFallbackCore.kHeld, true) && UbCore.ReplanUnseen(UnseenFallbackCore.kSeen, true) && !UbCore.ReplanUnseen(UnseenFallbackCore.kHeld, false)
            && !UbCore.ReplanUnseen(UnseenFallbackCore.kOnScreen, true) && !UbCore.ReplanUnseen(-1, true), "boat: a held Sim out of view is an unseen replan");
        Check(UbCore.WaterPosture("SwimmingInPool") && UbCore.WaterPosture("ScubaDiving") && UbCore.WaterPosture("PondAndOceanRoutingPosture") && !UbCore.WaterPosture("Standing") && !UbCore.WaterPosture(null), "boat: start test is the posture");
        UbReasonMs ubMs = new UbReasonMs();
        ubMs.Add("caller a", 100); ubMs.Add(null, 5); ubMs.Add("caller a", 50);
        Check(ubMs.Lines().Count == 2 && (string)ubMs.Lines()[0] == "caller a | 2 replans, 150 ms" && (string)ubMs.Lines()[1] == UbReasonMs.kNoTrip + " | 1 replans, 5 ms", "boat: ms sum per reason, most first");
        for (int i = 0; i < 200; i++) ubMs.Add("caller " + i, 1);
        Check(ubMs.Lines().Count == UbReasonMs.kMaxRows, "boat: reason rows capped, the rest under one row");
        Check(UbCore.HoldMinutes(1000f) >= UbCore.HoldMinutes(200f) && UbCore.HoldMinutes(1e6f) <= UfPreCore.kMaxHoldMin, "boat: wait grows with the trip, capped");

        // docs/202 §3.4.2 finish the walk (UfFinishCore)
        Check(UfFinishCore.HoldMinutes(0f) == 0f && UfFinishCore.HoldMinutes(-3f) == 0f && UfFinishCore.HoldMinutes(float.NaN) == 0f, "finish: no length = finish at once");
        Check(System.Math.Abs(UfFinishCore.HoldMinutes(50f) - 50f / UfPreCore.kWalkMPerSimMin) < 0.001f, "finish: remaining length / speed, no detour");
        Check(UfFinishCore.HoldMinutes(1e6f) == UfPreCore.kMaxHoldMin, "finish: capped at 2 sim-hours");
        Check(UfFinishCore.RouteShape(true, false, 0, 1, UfFinishCore.kSimPath, false), "finish: last walking subpath");
        Check(!UfFinishCore.RouteShape(false, false, 0, 1, UfFinishCore.kSimPath, false), "finish: vehicle / boat / UFO subclass");
        Check(!UfFinishCore.RouteShape(true, true, 0, 1, UfFinishCore.kSimPath, false), "finish: already killed");
        Check(!UfFinishCore.RouteShape(true, false, 1, 1, UfFinishCore.kSimPath, false), "finish: slaves");
        Check(!UfFinishCore.RouteShape(true, false, 0, 2, UfFinishCore.kSimPath, false), "finish: a portal / door subpath left");
        Check(!UfFinishCore.RouteShape(true, false, 0, 0, UfFinishCore.kSimPath, false), "finish: no path");
        Check(!UfFinishCore.RouteShape(true, false, 0, 1, 1348865362u, false), "finish: portal path");
        Check(!UfFinishCore.RouteShape(true, false, 0, 1, UfFinishCore.kSimPath, true), "finish: an action queued after");
        Check(!UfFinishCore.Denied(UfFinishCore.kTerrainGoHere) && UfPreCore.Denied(UfFinishCore.kTerrainGoHere), "finish: a townie's Go Here allowed (D denies it)");
        Check(UfFinishCore.Denied("Sims3.Gameplay.Core.Terrain+GoHereWith") && UfFinishCore.Denied("NRaas.GoHereSpace.Interactions.GoHereEx")
            && UfFinishCore.Denied("Sims3.Gameplay.Actors.Sim+GoJogging") && !UfFinishCore.Denied("Sims3.Gameplay.Objects.Seating.Chair+Sit"), "finish: deny list otherwise D's");
        Check(UfFinishCore.Step(true, true, true, 10, 20) == UfFinishCore.kKeep, "finish: hold until the walk time");
        Check(UfFinishCore.Step(true, true, true, 20, 20) == UfFinishCore.kFinish, "finish: walk time over");
        Check(UfFinishCore.Step(false, true, true, 30, 20) == UfFinishCore.kGiveUp, "finish: native released (seen, expired)");
        Check(UfFinishCore.Step(true, false, true, 30, 20) == UfFinishCore.kGiveUp, "finish: route action changed");
        Check(UfFinishCore.Step(true, true, false, 30, 20) == UfFinishCore.kGiveUp, "finish: seen at the finish check");

        PostureChecks();

        if (fails > 0) { Console.WriteLine("unseenfb selftest FAILED: " + fails); Environment.Exit(1); }
        Console.WriteLine("unseenfb selftest ok");
    }

    // Issue #89 model: a posture chain and the setter's two branches as the issue and the decompiled setter describe them
    // (Compatible: link the new posture on top; else unwind down to it), with and without posturepopcycle's pop test.
    class P { public P Prev; public bool Carry; public bool CompatOverCarry; public string Name; public P(string n) { Name = n; } }
    static P sCur;
    static int Walk(P top, out bool carry)  // UfHeld's bounded walk: postures seen, -1 = did not end within the cap
    {
        int n = 0;
        carry = false;
        for (P p = top; p != null; p = p.Prev)
        {
            if (!UnseenFallbackCore.ChainStepOk(++n)) return -1;
            if (p.Carry) carry = true;
        }
        return n;
    }
    static void Set(P value, bool guard)
    {
        if (value == sCur) return;
        bool carry;
        bool compatible = value.CompatOverCarry && Walk(sCur, out carry) > 0 && carry;
        if (!(guard && UnseenFallbackCore.IsPop(sCur, sCur != null ? sCur.Prev : null, value)) && compatible) value.Prev = sCur;
        sCur = value;  // unwind: the target is below, already linked
    }
    static void PostureChecks()
    {
        for (int g = 0; g < 2; g++)
        {
            bool guard = g == 1, carry;
            P stand = new P("stand"), carryChild = new P("carry"), carA = new P("carA"), carB = new P("carB");
            carryChild.Carry = true; carA.CompatOverCarry = carB.CompatOverCarry = true;
            sCur = stand;
            carryChild.Prev = stand; sCur = carryChild;
            Set(carA, guard); Set(carB, guard);
            Check(sCur == carB && carB.Prev == carA && carA.Prev == carryChild, "posture: pushes link on top (guard " + guard + ")");
            Check(Walk(sCur, out carry) == 4 && carry, "posture: carry seen through the chain");
            Set(carB.Prev, guard);  // PopPosture: Posture = mPosture.PreviousPosture
            int n = Walk(sCur, out carry);
            if (guard) Check(sCur == carA && carA.Prev == carryChild && n == 3 && carry, "posture: guarded pop unwinds, chain intact");
            else Check(carA.Prev == carB && carB.Prev == carA && n == -1, "posture: EA pop makes the loop, bounded walk stops (held)");
        }
        Check(!UnseenFallbackCore.IsPop(null, null, new object()) && !UnseenFallbackCore.IsPop(new object(), null, null), "posture: no pop without both");
        object a = new object(), b = new object();
        Check(UnseenFallbackCore.IsPop(a, b, b) && !UnseenFallbackCore.IsPop(a, b, a) && !UnseenFallbackCore.IsPop(a, null, b), "posture: pop = set to the current previous");
        Check(UnseenFallbackCore.ChainStepOk(UnseenFallbackCore.kPostureChainCap) && !UnseenFallbackCore.ChainStepOk(UnseenFallbackCore.kPostureChainCap + 1), "posture: cap");
    }
}
