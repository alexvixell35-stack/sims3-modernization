using System;
using Ts3Fixes;

// Offline checks of ReachUseCore (reachuse / reachusehh, docs/686 §12).
static class ReachUseTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = ReachUseCore.kNoKind, Fi = ReachUseCore.kFire, Fl = ReachUseCore.kFloor, Sm = ReachUseCore.kSmall, Pl = ReachUseCore.kPlant, Ch = ReachUseCore.kChore;
        const string obj = "|GameObject|", terrain = "|Terrain|GameObject|", sim = "|Sim|GameObject|";

        // failed plans
        Check(ReachUseCore.PlanKind(16) && ReachUseCore.PlanKind(10) && ReachUseCore.PlanKind(14) && ReachUseCore.PlanKind(15), "no path, no destination, obstructed");
        Check(ReachUseCore.PlanKind(17), "a Sim on the spot: done from beside it");
        Check(!ReachUseCore.PlanKind(18), "portal locked kept");
        Check(!ReachUseCore.PlanKind(3) && !ReachUseCore.PlanKind(11) && !ReachUseCore.PlanKind(2), "aborted, object not found, plain failure kept");
        Check(!ReachUseCore.PlanKind(1) && !ReachUseCore.PlanKind(0), "a plan that succeeded, unknown");

        // kinds
        Check(ReachUseCore.Kind("|Fire+Extinguish|ExtinguishBase|InteractionGameObjectHit`2|", "|Fire|GameObject|", false, false) == Fi, "put out a fire");
        Check(ReachUseCore.Kind("|Windows+ExtinguishWindow|ExtinguishBase|", "|Window|" , false, false) == Fi, "put out a burning window");
        Check(ReachUseCore.Kind("|Sim+ExtinguishSim|InteractionUsableDuringFireBase|", sim, true, false) == Fi, "put out a Sim on fire");
        Check(ReachUseCore.Kind("|Sim+BeExtinguished|Interaction`2|", sim, true, false) == N, "the burning Sim's own side");
        Check(ReachUseCore.Kind("|Terrain+MopPuddle|Terrain+Mopping|Terrain+PuddleInteraction|", terrain, false, false) == Fl, "mop a puddle");
        Check(ReachUseCore.Kind("|Terrain+MopBurnt|Terrain+Mopping|", terrain, false, false) == Fl, "mop a burnt tile");
        Check(ReachUseCore.Kind("|Terrain+PlayInPuddle|Terrain+PuddleInteraction|", terrain, false, false) == N, "playing in a puddle");
        Check(ReachUseCore.Kind("|Plant+WaterPlant|Interaction`2|", "|Plant|GameObject|", false, true) == Pl, "water a plant");
        Check(ReachUseCore.Kind("|HarvestPlant+Harvest|", "|HarvestPlant|Plant|GameObject|", false, true) == Pl, "harvest");
        Check(ReachUseCore.Kind("|TrashPile+CleanUp|", "|TrashPileIndoor|TrashPile|GameObject|", false, false) == Sm, "trash pile");
        Check(ReachUseCore.Kind("|AshPile+SweepUp|", "|AshPile|GameObject|", false, false) == Sm, "ash pile by its target");
        Check(ReachUseCore.Kind("|ServingContainer+ServingContainer_CleanUp|CleanUp|", "|Plate|ServingContainer|GameObject|", false, false) == Sm, "dishes");
        Check(ReachUseCore.Kind("|Newspaper+RecycleNewspaper|", "|Newspaper|GameObject|", false, false) == Sm, "newspaper");
        Check(ReachUseCore.Kind("|PutAwayLeftovers|", obj, false, false) == Sm, "leftovers");
        Check(ReachUseCore.Kind("|Homework+PickUpHomework|", "|Homework|GameObject|", false, false) == Sm, "homework");
        Check(ReachUseCore.Kind("|Sink+WashDishes|", "|SinkCounter|Sink|GameObject|", false, false) == Ch, "wash up at a sink");
        Check(ReachUseCore.Kind("|Toilet+Clean|", "|Toilet|GameObject|", false, false) == Ch, "clean a toilet");
        Check(ReachUseCore.Kind("|RepairableComponent+RepairShower|", "|Shower|GameObject|", false, false) == Ch, "repair");
        Check(ReachUseCore.Kind("|PetBowl+FillBowl|", "|PetBowl|GameObject|", false, false) == Ch, "fill a bowl");
        Check(ReachUseCore.Kind("|Toilet+UseToilet|", "|Toilet|GameObject|", false, false) == N, "object use is not a chore");
        Check(ReachUseCore.Kind("|Terrain+GoHere|", terrain, false, false) == N, "Go Here");
        Check(ReachUseCore.Kind("|SocialInteractionA|SocialInteraction|", sim, true, false) == N, "a social");
        Check(ReachUseCore.Kind("|PickUpChild|SocialInteraction|", sim, true, false) == N, "picking up a child: a Sim as target");
        Check(ReachUseCore.Kind("|Sim+CleanUpSim|", sim, true, false) == N, "a Sim as target, whatever the name");
        Check(ReachUseCore.Kind("", obj, false, false) == N && ReachUseCore.Kind(null, null, false, false) == N, "no interaction");

        // reach per kind
        Check(ReachUseCore.Reach(Fi) == 4f && ReachUseCore.Reach(Fl) == 2.5f && ReachUseCore.Reach(Sm) == 2f && ReachUseCore.Reach(Pl) == 2f, "reach distances");
        Check(ReachUseCore.Reach(Ch) == 0f && ReachUseCore.Reach(N) == 0f, "slot chores and other kinds are not done from a distance");
        Check(ReachUseCore.MinDist(Fi) == 0.5f && ReachUseCore.MinDist(Fl) == 0f, "not into the fire");

        // the failed route is the one to the target
        Check(ReachUseCore.GoalAtTarget(true, true, 0f, 1.5f), "terrain: the goal is the place");
        Check(ReachUseCore.GoalAtTarget(false, true, 0.6f, 1.5f), "ring round the object");
        Check(!ReachUseCore.GoalAtTarget(false, true, 4f, 1.5f), "the walk to the dishwasher");
        Check(!ReachUseCore.GoalAtTarget(true, false, 0f, 1.5f) && !ReachUseCore.GoalAtTarget(false, false, 0f, 1.5f), "goal unknown");

        // a place to stand
        Check(ReachUseCore.Stand(2f, 0f, false, 2.5f, 1.5f) == ReachUseCore.kStandOk, "in reach");
        Check(ReachUseCore.Stand(2.5f, -0.2f, false, 2.5f, 1.5f) == ReachUseCore.kStandOk, "at the edge, a step lower");
        Check(ReachUseCore.Stand(2.6f, 0f, false, 2.5f, 1.5f) == ReachUseCore.kStandFar, "too far");
        Check(ReachUseCore.Stand(1f, 3f, false, 2.5f, 1.5f) == ReachUseCore.kStandLevel, "a floor above");
        Check(ReachUseCore.Stand(1f, -3f, true, 2.5f, 1.5f) == ReachUseCore.kStandLevel, "a floor below");
        Check(ReachUseCore.Stand(1f, 0f, true, 2.5f, 1.5f) == ReachUseCore.kStandSight, "a wall between");
        Check(ReachUseCore.Stand(5f, 0f, true, 2.5f, 1.5f) == ReachUseCore.kStandFar, "too far comes before the wall");

        // slot chores: blockeduse's rules
        Check(ReachUseCore.SlotFallback(Ch, true) && ReachUseCore.SlotFallback(Sm, true), "the target's own slots (or its surface's)");
        Check(!ReachUseCore.SlotFallback(Ch, false), "another object's slots");
        Check(!ReachUseCore.SlotFallback(N, true), "not a chore");

        // a covered puddle tile
        Check(ReachUseCore.MopCovered(true, true, false), "furniture on the tile");
        Check(!ReachUseCore.MopCovered(true, true, true), "a Sim on the tile");
        Check(!ReachUseCore.MopCovered(false, true, false), "switch off");
        Check(!ReachUseCore.MopCovered(true, false, false), "no blocker known");

        // scope (BlockedUseCore.Decide with reachuse's switches)
        Check(BlockedUseCore.Decide(true, false, true, false) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt, "reachuse alone");
        Check(BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould, "reachusehh alone");

        Console.WriteLine(sFail == 0 ? "reachuse selftest: " + sRun + " checks passed" : "reachuse selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
