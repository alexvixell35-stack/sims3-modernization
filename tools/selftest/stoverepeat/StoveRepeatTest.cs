using System;
using System.Collections.Generic;
using Ts3Agent;

static class StoveRepeatTest
{
    static void Is(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

    static int Main()
    {
        string noPath = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51DEA6380 region footprint (Null, DynamicSlot) at 1392.3,1666.1 at 1392.3,1666.1, path 0.0 m, candidates 0/0/0, plan 516 ms, from 1392.2,1666.5 room 1";
        string ghost = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51DACAB20 no object at 1394.5,1668.4, path 0.0 m, candidates 1/1/0, plan 16 ms";
        string good = "bin 0, Succeeded, blocker None 0x0, path 31.8 m, candidates 62/60/2, plan 3 ms";
        string simBlock = "bin 0, FailedBecauseNoPathToDestination, blocker Sim 0x38B400F51DAB3960 Sim at 1392.1,1667.5, path 0.0 m, candidates 1/1/0";
        Is(StoveRepeatCore.Split(noPath) == "0/0/0", "0/0/0 split");
        Is(StoveRepeatCore.Split(ghost) == "1/1/0", "1/1/0 split");
        Is(StoveRepeatCore.Split(good) == "other", "62/60/2 is other");
        Is(StoveRepeatCore.Split("no plan here") == "other" && StoveRepeatCore.Candidates("no plan here") == null, "no candidates in a line");
        Is(StoveRepeatCore.Candidates(good)[0] == 62 && StoveRepeatCore.Candidates(good)[1] == 60, "candidates parsed");
        Is(StoveRepeatCore.Failed(noPath) && StoveRepeatCore.Failed(ghost), "failed plans");
        Is(!StoveRepeatCore.Failed(good) && !StoveRepeatCore.Failed(simBlock) && !StoveRepeatCore.Failed(null), "good, Sim-blocked and no line are not failures");
        Is(StoveRepeatCore.NoObject(ghost) && !StoveRepeatCore.NoObject(noPath), "no object blocker");
        Is(StoveRepeatCore.WalkAway(good) && !StoveRepeatCore.WalkAway(noPath), "the 60-candidate walk-away is a good plan with a wide search");
        // the 11-07 run: a live region at the first plan that clears at +5 s is a transient, the ghost is a stop at once, two fails in a row are a stop
        Is(!StoveRepeatCore.Permanent(noPath, true, false), "clears at +5 s: transient");
        Is(!StoveRepeatCore.Permanent(noPath, false, true), "clears at +30 s: transient");
        Is(StoveRepeatCore.Permanent(noPath, false, false), "failing at +5 s and +30 s: permanent");
        Is(StoveRepeatCore.Permanent(ghost, true, true), "a no object blocker is permanent even when the rechecks pass");
        // the 11-07 cycles 1 and 2: the chain reached the oven, Eat was skipped ("no entry Eat" is not a step)
        List<string> c1 = new List<string>(new string[] { "ReadBook", "Fridge_Have", "FoodTray_Prepare", "BakingDish_Prepare", "BakingDish_CookInOven", "PutDownHeldObject", "PutAwayLeftovers", "no entry Eat", "PutAwayLeftovers", "ServingContainer_CleanUp", "UseDishwasher" });
        Is(StoveRepeatCore.Baked(c1) && !StoveRepeatCore.Ate(c1), "cycle 1 of 11-07: a bake, not ate");
        List<string> full = new List<string>(c1);
        full.Remove("no entry Eat");
        full.Add("Eat");
        Is(StoveRepeatCore.Baked(full) && StoveRepeatCore.Ate(full), "with Eat: baked and ate");
        Is(!StoveRepeatCore.Baked(new List<string>(new string[] { "ReadBook", "Fridge_Have", "ChangeIntoOuterwear" })), "no oven, no bake");
        // the 11-07 cycle 26 of v0210e: plan, recheck +5 s and +30 s all failed while the Sim was in SocialInteractionB (live region, then SocialJigTwoPerson), all probes fine after the stop
        string c26a = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51E390C50 region footprint (Null, DynamicSlot) at 1392.3,1666.0 at 1392.3,1666.0, path 0.0 m, candidates 0/0/0, plan 203 ms, from 1392.4,1665.7 room 1";
        string c26b = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51E391200 region footprint (Null, DynamicSlot) at 1392.3,1666.0 at 1392.3,1666.0, path 0.0 m, candidates 0/0/0, plan 157 ms, from 1392.4,1665.7 room 1";
        string c26c = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51E395550 SocialJigTwoPerson at 1392.8,1666.1 at 1392.8,1666.1, path 0.0 m, candidates 0/0/0, plan 125 ms, from 1392.9,1665.5 room 1";
        Is(!StoveRepeatCore.PermanentFail(new string[] { c26a, c26b, c26c }, new bool[] { false, false, false }), "cycle 26: failing while the Sim is in a social is not permanent");
        Is(StoveRepeatCore.PermanentFail(new string[] { c26a, c26b }, new bool[] { true, true }), "two fails with the Sim free are permanent");
        Is(!StoveRepeatCore.PermanentFail(new string[] { c26a, c26b }, new bool[] { false, true }), "one free fail is not enough");
        Is(!StoveRepeatCore.PermanentFail(new string[] { c26a, good }, new bool[] { true, true }), "a recheck that passes clears it");
        string g1 = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51DACAB20 no object at 1394.5,1668.4, path 0.0 m, candidates 1/1/0, plan 16 ms";
        string g2 = "bin 0, FailedBecauseNoPathToDestination, blocker Object 0x38B400F51DADE8D0 no object at 1394.5,1668.4, path 0.0 m, candidates 1/1/0, plan 16 ms";
        Is(StoveRepeatCore.NoObjectId(g1) == "38B400F51DACAB20" && StoveRepeatCore.NoObjectId(c26a) == null && StoveRepeatCore.NoObjectId(c26c) == null, "no object ids");
        Is(StoveRepeatCore.PermanentFail(new string[] { g1, g1 }, new bool[] { false, false }), "the same no object id twice is permanent even with the Sim busy");
        Is(!StoveRepeatCore.PermanentFail(new string[] { g1, g2 }, new bool[] { false, false }), "two different no object ids busy: not the same ghost");
        // no use (the 11-07 cycle 9, free will picked SplashInPuddle): never a stop alone, a stop only 5 in a row with a plan failure
        Is(!StoveRepeatCore.NoUseStop(1, 0) && !StoveRepeatCore.NoUseStop(4, 1), "one to four no use cycles go on");
        Is(!StoveRepeatCore.NoUseStop(5, 0) && !StoveRepeatCore.NoUseStop(9, 0), "no use without a plan failure goes on");
        Is(StoveRepeatCore.NoUseStop(5, 1) && StoveRepeatCore.NoUseStop(6, 2), "5 in a row with a plan failure stops");
        Console.WriteLine("stove repeat core ok");
        return 0;
    }
}
