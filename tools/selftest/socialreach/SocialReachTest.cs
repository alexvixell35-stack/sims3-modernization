using System;
using Ts3Fixes;

// Offline checks of SocialReachCore (socialreach / socialreachhh, docs/686 §17).
static class SocialReachTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int T = SocialReachCore.kTalk, C = SocialReachCore.kContact, O = SocialReachCore.kOther;
        const int A = SocialReachCore.kApproach, R = SocialReachCore.kReceive, P = SocialReachCore.kPlace;
        const int Keep = SocialReachCore.kKeep, Here = SocialReachCore.kHere, Walk = SocialReachCore.kWalk;
        const int Ok = ReachUseCore.kStandOk, Far = ReachUseCore.kStandFar, Level = ReachUseCore.kStandLevel, Sight = ReachUseCore.kStandSight;
        const float Max = SocialReachCore.kTalkMax, Dy = 1.5f;

        // which failed plans
        Check(SocialReachCore.PlanKind(16) && SocialReachCore.PlanKind(10) && SocialReachCore.PlanKind(15) && SocialReachCore.PlanKind(17), "no path, no destination, obstructed, a Sim on the place");
        Check(!SocialReachCore.PlanKind(18), "a locked door kept");
        Check(!SocialReachCore.PlanKind(3), "an aborted plan kept");
        Check(!SocialReachCore.PlanKind(12) && !SocialReachCore.PlanKind(13) && !SocialReachCore.PlanKind(14), "jig slot failures kept");
        Check(!SocialReachCore.PlanKind(0) && !SocialReachCore.PlanKind(1) && !SocialReachCore.PlanKind(2) && !SocialReachCore.PlanKind(11), "unknown, succeeded, plain failure, object not found kept");
        for (int t = 4; t <= 9; t++) Check(!SocialReachCore.PlanKind(t), "start failure " + t + " kept");

        // which socials
        Check(SocialReachCore.Cover(true, true, 0, false, true, false, false, false) == T, "chat: plain classes, one-person jig, two humans");
        for (int jig = 1; jig <= 7; jig++) Check(SocialReachCore.Cover(true, true, jig, false, true, false, false, false) == C, "paired social, jig kind " + jig + " (hug, kiss, slap, growl, underwater)");
        Check(SocialReachCore.Cover(true, true, 0, true, true, false, false, false) == C, "a social with its own jig (forced, horse, big dog)");
        Check(SocialReachCore.Cover(true, true, 2, true, false, false, false, false) == C, "a paired pet social is contact first");
        Check(SocialReachCore.Cover(false, true, 0, false, true, false, false, false) == O, "a subclass (pillow fight, tag, catch, pool social)");
        Check(SocialReachCore.Cover(true, false, 0, false, true, false, false, false) == O, "no social data");
        Check(SocialReachCore.Cover(true, true, 0, false, false, false, false, false) == O, "a pet takes part");
        Check(SocialReachCore.Cover(true, true, 0, false, true, true, false, false) == O, "a baby takes part");
        Check(SocialReachCore.Cover(true, true, 0, false, true, false, true, false) == O, "a rider or a ridden horse");
        Check(SocialReachCore.Cover(true, true, 0, false, true, false, false, true) == O, "a swimmer or diver");

        // the place: the game's own numbers
        Check(ReachUseCore.Stand(3f, 0f, false, Max, Dy) == Ok && ReachUseCore.Stand(0.4f, 0.1f, false, Max, Dy) == Ok, "within 3 m on the level");
        Check(ReachUseCore.Stand(3.1f, 0f, false, Max, Dy) == Far, "farther than 3 m");
        Check(ReachUseCore.Stand(2f, 3f, false, Max, Dy) == Level && ReachUseCore.Stand(2f, -3f, false, Max, Dy) == Level, "another level");
        Check(ReachUseCore.Stand(2f, 0f, true, Max, Dy) == Sight, "a wall between");
        Check(SocialReachCore.kWalkMin < SocialReachCore.kWalkMax && SocialReachCore.kWalkMax < SocialReachCore.kTalkMax, "the walk ends inside the talk range");

        // the step
        Check(SocialReachCore.Step(A, Ok, false, false, false, false) == Here, "starter's route, in reach: talk from here");
        Check(SocialReachCore.Step(A, Ok, false, false, true, false) == Here, "starter's turn failed, in reach: talk from here");
        Check(SocialReachCore.Step(R, Ok, false, false, false, false) == Here && SocialReachCore.Step(R, Ok, false, false, true, false) == Here, "partner's route or turn, in reach: talk from here");
        Check(SocialReachCore.Step(P, Ok, false, false, false, false) == Here, "placement, in reach: placed where they stand");
        Check(SocialReachCore.Step(P, Ok, false, true, false, false) == Here, "placement, the starter's walk ends in reach");
        Check(SocialReachCore.Step(P, Ok, true, false, false, false) == Here, "placement, in reach, the starter in a posture: the game placed it there itself");
        Check(SocialReachCore.Step(A, Far, false, false, false, false) == Walk && SocialReachCore.Step(A, Sight, false, false, false, false) == Walk, "starter too far or behind a wall: walk");
        Check(SocialReachCore.Step(P, Far, false, false, false, false) == Walk && SocialReachCore.Step(P, Sight, false, false, false, false) == Walk, "placement too far or behind a wall: the starter walks");
        Check(SocialReachCore.Step(A, Level, false, false, false, false) == Keep && SocialReachCore.Step(R, Level, false, false, false, false) == Keep
            && SocialReachCore.Step(P, Level, false, false, false, false) == Keep, "another level: kept");
        Check(SocialReachCore.Step(R, Far, false, false, false, false) == Keep && SocialReachCore.Step(R, Sight, false, false, false, false) == Keep, "the partner never walks");
        Check(SocialReachCore.Step(R, Ok, false, false, false, true) == Keep && SocialReachCore.Step(R, Far, false, false, false, true) == Keep, "the partner's walk to the game's placement jig: kept (the game tries on)");
        Check(SocialReachCore.Step(A, Ok, false, false, false, true) == Here, "the placement jig is the partner's matter only");
        Check(SocialReachCore.Step(A, Ok, true, false, false, false) == Keep && SocialReachCore.Step(R, Ok, true, false, false, false) == Keep, "sitting or lying with a failed route: kept");
        Check(SocialReachCore.Step(A, Far, true, false, false, false) == Keep && SocialReachCore.Step(P, Far, true, false, false, false) == Keep, "sitting or lying: no walk");
        Check(SocialReachCore.Step(P, Far, false, true, false, false) == Keep && SocialReachCore.Step(P, Sight, false, true, false, false) == Keep, "the starter still walks and ends out of reach: kept");
        Check(SocialReachCore.Step(A, Far, false, false, true, false) == Keep, "a failed turn out of reach (call over): kept");

        // scope
        Check(SocialReachCore.Decide(false, false, true, false) == BlockedUseCore.kGo, "socialreach: two NPCs");
        Check(SocialReachCore.Decide(true, false, true, false) == BlockedUseCore.kExempt && SocialReachCore.Decide(false, true, true, false) == BlockedUseCore.kExempt, "socialreach: a household Sim on either side is exempt");
        Check(SocialReachCore.Decide(true, false, false, true) == BlockedUseCore.kGo && SocialReachCore.Decide(false, true, false, true) == BlockedUseCore.kGo
            && SocialReachCore.Decide(true, true, false, true) == BlockedUseCore.kGo, "socialreachhh: a household Sim on either side");
        Check(SocialReachCore.Decide(false, false, false, true) == BlockedUseCore.kWould, "socialreachhh alone: two NPCs only counted");
        Check(SocialReachCore.Decide(false, false, false, false) == BlockedUseCore.kWould && SocialReachCore.Decide(true, true, false, false) == BlockedUseCore.kWould, "both off: counted only");
        Check(SocialReachCore.Decide(true, false, true, true) == BlockedUseCore.kGo && SocialReachCore.Decide(false, false, true, true) == BlockedUseCore.kGo, "both on: everyone");

        Console.WriteLine(sFail == 0 ? "socialreach selftest: " + sRun + " checks ok" : "socialreach selftest: " + sFail + " of " + sRun + " FAILED");
        return sFail == 0 ? 0 : 1;
    }
}
