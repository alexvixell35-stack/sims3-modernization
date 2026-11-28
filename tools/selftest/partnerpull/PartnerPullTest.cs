using System;
using Ts3Fixes;

// Offline checks of PartnerPullCore (partnerpull, docs/686 §21).
static class PartnerPullTest
{
    static int sFail, sRun;

    static void Check(bool ok, string what)
    {
        sRun++;
        if (!ok) { sFail++; Console.WriteLine("FAIL " + what); }
    }

    static int Main()
    {
        const int G = PartnerPullCore.kWaitGrace, P = PartnerPullCore.kPull, KP = PartnerPullCore.kKeepPlayer, KB = PartnerPullCore.kKeepBusy, KR = PartnerPullCore.kKeepRank;
        Check(PartnerPullCore.Step(0f, true, true, false, false, false, 0) == G && PartnerPullCore.Step(0.99f, true, true, false, false, false, 1) == G, "a quick hand-off is left alone");
        Check(PartnerPullCore.Step(1f, true, true, false, false, false, 0) == P && PartnerPullCore.Step(20f, true, true, false, false, false, 1) == P, "an autonomous head past the grace is pulled");
        Check(PartnerPullCore.Step(2f, false, true, false, false, false, 0) == KP, "a player's head is never cancelled");
        Check(PartnerPullCore.Step(2f, true, false, false, false, false, 0) == KP, "the player's queue ahead is never jumped");
        Check(PartnerPullCore.Step(2f, false, true, true, false, false, -1) == KP, "player first, before busy or rank");
        Check(PartnerPullCore.Step(2f, true, true, true, false, false, 0) == KB, "a busy head (must run, transition, not cancellable, linked to the waiter) is kept");
        Check(PartnerPullCore.Step(2f, true, true, false, false, false, -1) == KR, "a head that outranks the half is kept");
        Check(PartnerPullCore.Step(0.5f, false, false, true, false, false, -1) == G, "grace before anything");
        Check(PartnerPullCore.Step(2f, true, true, false, true, false, 0) == PartnerPullCore.kKeepTrip, "a lot trip (go to lot, visit, go home) is never cut short");
        Check(PartnerPullCore.Step(2f, true, true, false, false, true, 0) == PartnerPullCore.kKeepSocial, "a partner in a conversation is never pulled out");
        Check(PartnerPullCore.Step(2f, true, true, true, true, true, -1) == PartnerPullCore.kKeepTrip, "trip first among the keeps");
        Check(PartnerPullCore.Step(2f, false, true, false, true, true, 0) == KP, "player first, before trip or conversation");
        Check(PartnerPullCore.Act(true) && !PartnerPullCore.Act(false), "off = compare mode");
        Console.WriteLine((sFail == 0 ? "ok " : "FAILED ") + (sRun - sFail) + "/" + sRun);
        return sFail == 0 ? 0 : 1;
    }
}
