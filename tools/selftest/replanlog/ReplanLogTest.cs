// Offline self-check of mods/ts3fixes/src/ReplanLogCore.cs (replanlog, docs/261 §6 L1).
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class ReplanLogTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        // scope: selected first, household, unseen (kSeen = 0), seen NPC reasons, everything else (not live, gone, -1, -2) unknown
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kHousehold, true) == ReplanLogCore.kSelected, "selected");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kSeen, true) == ReplanLogCore.kSelected, "selected wins over unseen");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kHousehold, false) == ReplanLogCore.kHousehold, "household");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kSeen, false) == ReplanLogCore.kUnseen, "unseen");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kOnScreen, false) == ReplanLogCore.kSeenNpc, "on screen NPC");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kSocialSeen, false) == ReplanLogCore.kSeenNpc, "social seen NPC");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kNotLive, false) == ReplanLogCore.kUnknown, "not live");
        Check(ReplanLogCore.Scope(UnseenFallbackCore.kGone, false) == ReplanLogCore.kUnknown, "gone");
        Check(ReplanLogCore.Scope(-1, false) == ReplanLogCore.kUnknown, "check threw");
        Check(ReplanLogCore.Scope(-2, false) == ReplanLogCore.kUnknown, "no Sim follower");

        // bursts: 1 call/s never starts one; the 4th call within 1 s starts it seeded with all 4; quiet > 1 s after the last hot call ends it
        ReplanBurst b = new ReplanBurst();
        int t = -500;  // TickCount can be negative: compares are unchecked
        for (int i = 0; i < 5; i++) { Check(!b.Add(t, 40, ReplanLogCore.kUnseen, "A", "GoToLot"), "slow calls no burst"); t += 1000; }
        Check(!b.Active && b.Id == 0, "no burst yet");
        Check(!b.Add(t, 40, ReplanLogCore.kUnseen, "A", "GoToLot"), "1");
        Check(!b.Add(t + 100, 30, ReplanLogCore.kSeenNpc, "B", "Wander"), "2");
        Check(!b.Add(t + 200, 40, ReplanLogCore.kUnseen, "A", "GoToLot"), "3");
        Check(b.Add(t + 300, 50, ReplanLogCore.kUnseen, "A", "GoToLot"), "4th within 1 s starts");
        Check(b.Active && b.Id == 1 && b.Calls == 4 && b.Ms == 160 && b.StartMs == t, "seeded with the window");
        Check(b.ScopeCalls[ReplanLogCore.kUnseen] == 3 && b.ScopeMs[ReplanLogCore.kSeenNpc] == 30, "scope split");
        Check(b.InWindow(t + 300, "A") == 3 && b.InWindow(t + 300, null) == 4, "window per Sim");
        Check(!b.Add(t + 900, 40, ReplanLogCore.kUnseen, "A", "GoToLot"), "in burst, no new start");
        Check(b.Calls == 5 && b.LastMs == t + 900, "counted");
        Check(!b.EndIfQuiet(t + 1300), "hot until 1 s after the last hot call");
        Check(b.EndIfQuiet(t + 2000) && !b.Active, "quiet ends it");
        Check(!b.EndIfQuiet(t + 2100), "ends once");
        Check(ReplanLogCore.Top(b.Sims, 6) == "A 4/170 ms; B 1/30 ms", "top Sims: " + ReplanLogCore.Top(b.Sims, 6));
        Check(ReplanLogCore.Top(new Dictionary<string, int[]>(), 3) == "none", "top empty");
        // a later call after the quiet gap is not in the old burst; a new hot window starts burst 2
        Check(!b.Add(t + 2000, 10, ReplanLogCore.kHousehold, "H", "GoHere") && b.Calls == 5, "after end not counted");
        for (int i = 1; i < 4; i++) b.Add(t + 2000 + i * 10, 10, ReplanLogCore.kHousehold, "H", "GoHere");
        Check(b.Active && b.Id == 2 && b.Calls == 4 && b.ScopeCalls[ReplanLogCore.kHousehold] == 4, "burst 2");
        // key cap: past kMaxKeys distinct Sims the rest go to "(other)"
        for (int i = 0; i < ReplanBurst.kMaxKeys + 10; i++) b.Add(t + 2100 + i, 1, ReplanLogCore.kUnseen, "S" + i, "X");
        Check(b.Sims.Count == ReplanBurst.kMaxKeys + 1 && b.Sims.ContainsKey("(other)"), "key cap " + b.Sims.Count);

        if (fails > 0) { Console.WriteLine(fails + " failure(s)"); Environment.Exit(1); }
        Console.WriteLine("replanlog core ok");
    }
}
