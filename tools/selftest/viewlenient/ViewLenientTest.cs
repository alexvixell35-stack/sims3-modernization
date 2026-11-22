using System;
using Ts3Fixes;

// Offline checks of ViewLenientCore (viewlenient / viewlenienthh, docs/686 §16).
static class ViewLenientTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int N = ViewLenientCore.kNoKind, Co = ViewLenientCore.kContemplate, Ar = ViewLenientCore.kArt, Pe = ViewLenientCore.kPerformer, Tv = ViewLenientCore.kTv, St = ViewLenientCore.kStars;
        const int Keep = ViewLenientCore.kKeep, Here = ViewLenientCore.kHere, View = ViewLenientCore.kView, Quiet = ViewLenientCore.kQuiet;
        const int Ok = ReachUseCore.kStandOk, Far = ReachUseCore.kStandFar, Level = ReachUseCore.kStandLevel, Sight = ReachUseCore.kStandSight;
        const string con = "|Terrain+Contemplate|TerrainInteraction|Interaction`2|InteractionInstance|";
        const string tv = "|TV+WatchTV|Interaction`2|InteractionInstance|";

        // kinds: ia chain, targetIsSim, targetIsTerrain
        Check(ViewLenientCore.Kind(con, false, true) == Co, "contemplate on the terrain");
        Check(ViewLenientCore.Kind("|ViewObjects|Interaction`2|InteractionInstance|", false, false) == Ar, "view a painting");
        Check(ViewLenientCore.Kind("|Guitar+Watch|MusicalInstrument+WatchBase`1|Interaction`2|", false, false) == Pe, "watch a guitar player");
        Check(ViewLenientCore.Kind("|Drums+Watch|MusicalInstrument+WatchBase`1|Interaction`2|", false, false) == Pe, "watch a drummer");
        Check(ViewLenientCore.Kind(tv, false, false) == Tv, "watch TV");
        Check(ViewLenientCore.Kind("|Sim+WatchTheStars|Interaction`2|", true, false) == St, "watch the stars");
        Check(ViewLenientCore.Kind("|Terrain+GoHere|TerrainInteraction|", false, true) == N, "Go Here untouched");
        Check(ViewLenientCore.Kind("|Terrain+GoHereWithFollower|Terrain+GoHere|", false, true) == N, "Go Here with a follower untouched");
        Check(ViewLenientCore.Kind("|SocialInteractionA|SocialInteraction|InteractionInstance|", true, false) == N, "socials untouched");
        Check(ViewLenientCore.Kind("|Sim+ListenToGhostStory|Interaction`2|", true, false) == N, "a ghost story is told to a group: untouched");
        Check(ViewLenientCore.Kind("|TV+RepairTV|Interaction`2|", false, false) == N && ViewLenientCore.Kind("|TV+WatchTVAutonomously|", false, false) == N
            && ViewLenientCore.Kind("|TV+PlayVideoGameSystemOnTV|", false, false) == N, "other TV interactions untouched");
        Check(ViewLenientCore.Kind("|FishTank+Watch|Interaction`2|", false, false) == N && ViewLenientCore.Kind("|Windows+LookOutWindow|", false, false) == N, "slot-bound watching untouched");
        Check(ViewLenientCore.Kind(con, false, false) == N && ViewLenientCore.Kind(con, true, false) == N, "contemplate only on the terrain");
        Check(ViewLenientCore.Kind(tv, true, false) == N && ViewLenientCore.Kind("|ViewObjects|", true, false) == N && ViewLenientCore.Kind("|ViewObjects|", false, true) == N, "a Sim or the terrain is no view target");
        Check(ViewLenientCore.Kind("|Sim+WatchTheStars|", false, false) == N, "stars only with a Sim");
        Check(ViewLenientCore.Kind(null, false, false) == N && ViewLenientCore.Kind("", false, true) == N, "no interaction");

        // failed plans
        Check(ViewLenientCore.PlanKind(10) && ViewLenientCore.PlanKind(16) && ViewLenientCore.PlanKind(15) && ViewLenientCore.PlanKind(17), "no destination, no path, obstructed, a Sim on the spot");
        Check(ViewLenientCore.PlanKind(18), "portal locked: no balloon for looking");
        Check(ViewLenientCore.PlanKind(12) && ViewLenientCore.PlanKind(13) && ViewLenientCore.PlanKind(14), "a jig with no free slot");
        Check(!ViewLenientCore.PlanKind(3), "an aborted plan kept");
        Check(!ViewLenientCore.PlanKind(0) && !ViewLenientCore.PlanKind(1) && !ViewLenientCore.PlanKind(2) && !ViewLenientCore.PlanKind(11), "unknown, succeeded, plain failure, object not found kept");
        Check(!ViewLenientCore.PlanKind(4) && !ViewLenientCore.PlanKind(9), "start failures kept");

        // modes: kind, planKind, simOk, routeShowsFail, slotRoute
        Check(ViewLenientCore.Mode(Co, true, true, true, false) == Here, "contemplate: stay");
        Check(ViewLenientCore.Mode(Ar, true, true, true, false) == View && ViewLenientCore.Mode(Pe, true, true, true, false) == View && ViewLenientCore.Mode(Tv, true, true, true, false) == View, "view kinds: a place with a view");
        Check(ViewLenientCore.Mode(St, true, true, true, true) == Quiet && ViewLenientCore.Mode(St, true, true, true, false) == Quiet, "stars: quiet end, jig slots or not");
        Check(ViewLenientCore.Mode(Co, false, true, true, false) == Keep && ViewLenientCore.Mode(St, false, true, true, true) == Keep, "not a plan of the kind: kept");
        Check(ViewLenientCore.Mode(Ar, true, false, true, false) == Keep, "pet, rider, in a vehicle: kept");
        Check(ViewLenientCore.Mode(Pe, true, true, false, false) == Keep && ViewLenientCore.Mode(St, true, true, false, true) == Keep, "a route with no balloon: kept");
        Check(ViewLenientCore.Mode(Tv, true, true, true, true) == Keep && ViewLenientCore.Mode(Co, true, true, true, true) == Keep, "a slot route: kept");
        Check(ViewLenientCore.Mode(N, true, true, true, false) == Keep, "no kind: kept");

        // ranges
        Check(ViewLenientCore.Range(Tv, 0f) == 6f && ViewLenientCore.Range(Tv, 20f) == 6f, "tv: the game's cone length");
        Check(ViewLenientCore.Range(Ar, 3f) == 3f && ViewLenientCore.Range(Ar, 0f) == 4f, "art: the viewable's own number, else 4");
        Check(ViewLenientCore.Range(Pe, 9f) == 9f && ViewLenientCore.Range(Pe, 0f) == 8f, "performer: the game's radius, else 8");
        Check(ViewLenientCore.Range(Ar, 0.2f) == 1.5f && ViewLenientCore.Range(Pe, 40f) == 12f, "clamped to 1.5 .. 12");
        Check(ViewLenientCore.Range(Co, 0f) == 6f && ViewLenientCore.Range(St, 5f) == 0f && ViewLenientCore.Range(N, 5f) == 0f, "contemplate 6, stars and none 0");
        Check(ViewLenientCore.MinDist(Pe) == 1f && ViewLenientCore.MinDist(Tv) == 1f && ViewLenientCore.MinDist(Ar) == 0.5f && ViewLenientCore.MinDist(Co) == 0f, "nearest distance per kind");
        foreach (int k in new int[] { Ar, Pe, Tv })
            Check(ViewLenientCore.MinDist(k) < ViewLenientCore.Range(k, 0.1f), "the ring is not empty at the smallest range, kind " + k);

        // the route is the one to the target
        Check(ViewLenientCore.GoalAtTarget(Ar, true, 3f, 4f, 1.5f) && ViewLenientCore.GoalAtTarget(Tv, true, 7.5f, 6f, 1.5f), "goal near the target");
        Check(!ViewLenientCore.GoalAtTarget(Ar, true, 9f, 4f, 1.5f) && !ViewLenientCore.GoalAtTarget(Pe, true, 30f, 8f, 1.5f), "another walk of the interaction kept");
        Check(ViewLenientCore.GoalAtTarget(Tv, false, 0f, 6f, 1.5f), "goal unknown: taken as the target's");
        Check(ViewLenientCore.GoalAtTarget(Co, true, 50f, 6f, 1.5f) && ViewLenientCore.GoalAtTarget(St, false, 0f, 0f, 1.5f), "contemplate and stars have one route");

        // staying in place
        Check(ViewLenientCore.StayHere(false, false), "a standing Sim stays");
        Check(!ViewLenientCore.StayHere(true, false) && !ViewLenientCore.StayHere(false, true) && !ViewLenientCore.StayHere(true, true), "sitting, lying or swimming: walks");

        // a place to look from: kind, flat, dy, wall, inFront, range, maxDy
        Check(ViewLenientCore.Stand(Tv, 4f, 0f, false, true, 6f, 1.5f) == Ok, "in front of the TV in range");
        Check(ViewLenientCore.Stand(Tv, 4f, 0f, false, false, 6f, 1.5f) == Sight, "behind the TV: no view");
        Check(ViewLenientCore.Stand(Ar, 2f, 0f, false, false, 4f, 1.5f) == Ok && ViewLenientCore.Stand(Pe, 2f, 0f, false, false, 8f, 1.5f) == Ok, "a sculpture or a performer from any side");
        Check(ViewLenientCore.Stand(Ar, 2f, 0f, true, true, 4f, 1.5f) == Sight && ViewLenientCore.Stand(Pe, 2f, 0f, true, true, 8f, 1.5f) == Sight, "a wall between: no view");
        Check(ViewLenientCore.Stand(Pe, 8.1f, 0f, false, true, 8f, 1.5f) == Far && ViewLenientCore.Stand(Tv, 6.1f, 0f, false, true, 6f, 1.5f) == Far, "out of range");
        Check(ViewLenientCore.Stand(Ar, 2f, 3f, false, true, 4f, 1.5f) == Level && ViewLenientCore.Stand(Tv, 2f, -3f, false, true, 6f, 1.5f) == Level, "another level");
        Check(ViewLenientCore.Stand(Tv, 9f, 0f, true, false, 6f, 1.5f) == Far, "the first reason is the distance");
        Check(ViewLenientCore.Stand(Co, 3f, 0f, false, false, 6f, 1.5f) == Ok, "contemplate: any place in range on the level");

        // scope (BlockedUseCore.Decide: kind, player, on, hhOn)
        Check(BlockedUseCore.Decide(true, false, true, false) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, true, true, false) == BlockedUseCore.kExempt, "viewlenient: NPCs only");
        Check(BlockedUseCore.Decide(true, true, false, true) == BlockedUseCore.kGo && BlockedUseCore.Decide(true, false, false, true) == BlockedUseCore.kWould, "viewlenienthh: household only");
        Check(BlockedUseCore.Decide(true, false, false, false) == BlockedUseCore.kWould && BlockedUseCore.Decide(true, true, false, false) == BlockedUseCore.kWould, "off: would");

        Console.WriteLine(sFail == 0 ? "viewlenient selftest: " + sRun + " checks ok" : "viewlenient selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
