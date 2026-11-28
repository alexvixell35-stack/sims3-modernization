using System;
using Ts3Fixes;

// Offline self-check of SocialUnlinkEndCore (docs/650 §3).
static class SocialUnlinkEndTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const int Started = SocialUnlinkEndCore.kStarted, Routed = SocialUnlinkEndCore.kRouted,
        Committed = SocialUnlinkEndCore.kCommitted, Completed = SocialUnlinkEndCore.kCompleted;
    const int NotCase = SocialUnlinkEndCore.kNotCase, End = SocialUnlinkEndCore.kEnd, Exempt = SocialUnlinkEndCore.kExempt;

    // defaults = the logged case: receiver in B, no link, partner Completed/None on none
    static int D(int level, bool receiver = true, bool inB = true, bool linked = false, bool syncsMe = false, bool aims = false, bool exempt = false)
    {
        return SocialUnlinkEndCore.Decide(level, receiver, inB, linked, syncsMe, aims, exempt);
    }

    static void Main()
    {
        Check(D(Routed) == End, "Routed wait, B unlinked, starter gone: ended");
        Check(D(Committed) == End, "Committed wait, same case: ended");
        Check(D(0) == NotCase && D(Started) == NotCase && D(Completed) == NotCase, "NotStarted / Started / Completed waits left to EA");
        Check(D(Routed, receiver: false) == NotCase, "initiator side left to socialreceivefail");
        Check(D(Routed, inB: false) == NotCase, "not in a SocialInteractionB aimed at the partner: left");
        Check(D(Routed, linked: true) == NotCase, "B still linked: the wait can still succeed, left");
        Check(D(Routed, syncsMe: true) == NotCase, "partner still syncs with the receiver: left");
        Check(D(Routed, aims: true) == NotCase, "partner's interaction aims at the receiver: left");
        Check(D(Routed, exempt: true) == Exempt && D(Committed, exempt: true) == Exempt, "exempt receiver: counted, not ended");
        Check(D(Routed, linked: true, exempt: true) == NotCase, "exempt and not the case: not counted");
        Check(D(Completed, exempt: true) == NotCase, "exempt Completed wait: not counted");

        if (sFails > 0) { Console.WriteLine("socialunlinkend selftest: " + sFails + " FAIL"); Environment.Exit(1); }
        Console.WriteLine("socialunlinkend selftest: ok");
    }
}
