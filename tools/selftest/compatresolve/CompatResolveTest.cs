using System;
using Ts3Fixes;

// Offline self-check of CompatResolveCore (docs/328 §7): the runtime precedence table, its hook slots, the loaded-assembly
// match and the survey stop test.
static class CompatResolveTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static void Main()
    {
        CompatResolveCore.Target[] t = CompatResolveCore.Targets;
        // every entry has a hook slot, slots are unique per kind, no entry twice
        for (int i = 0; i < t.Length; i++)
        {
            int s = CompatResolveCore.Slot(t, i);
            Check(s >= 0, "slot for " + t[i].Type);
            for (int k = 0; k < i; k++)
            {
                Check(!(t[k].Kind == t[i].Kind && CompatResolveCore.Slot(t, k) == s), "unique slot " + t[i].Type);
                Check(!(t[k].Asm == t[i].Asm && t[k].Type == t[i].Type && t[k].Method == t[i].Method), "listed once " + t[i].Type);
            }
            string fix = new string[] { "cleanupvfx", "lifeguardquit", "stairwaitend", "harvestextras", "wishupdatelost", "doorgen", "doorgen", "boatroamcap", "birthroute", "birthroute",
                "visit", "elevatorresetriders", "privateeyecase", "privateeyecase", "modquitguard", "failreplan", "woohootywait", "casrowleak", "casrowleak" }[t[i].Kind];
            Check(t[i].Fix == fix, "fix name matches kind " + t[i].Type);
            string m = new string[] { "Cleanup", "SurveyLot", "OnPortalStart", "DoHarvest", "Update", "AddOccultType", "Remove", "Run", "HaveTheBaby", "Cleanup",
                "Run", "OnReset", "Run", "Cleanup", "OnWorldQuit", "SetRouteOptions", "GoToLotSuccessEx", "AddGridItem", "CreateGridItems" }[t[i].Kind];
            Check(t[i].Method == m, "method matches kind " + t[i].Type);
        }
        // a fixture table: more entries of a kind than slots -> the extra one gets -1
        CompatResolveCore.Target[] many = new CompatResolveCore.Target[3];
        for (int i = 0; i < 3; i++) many[i] = new CompatResolveCore.Target(CompatResolveCore.SurveyLot, "lifeguardquit", "A" + i, "T" + i, "SurveyLot", "fixture");
        Check(CompatResolveCore.Slot(many, 0) == 0 && CompatResolveCore.Slot(many, 1) == -1 && CompatResolveCore.Slot(many, 2) == -1, "slots run out: -1");

        // loaded match: only listed assemblies, table order, each entry once even if the name repeats
        CompatResolveCore.Target[] fx = {
            new CompatResolveCore.Target(CompatResolveCore.Cleanup, "cleanupvfx", "ModA", "A.X", "Cleanup", "fixture"),
            new CompatResolveCore.Target(CompatResolveCore.Cleanup, "cleanupvfx", "ModB", "B.Y", "Cleanup", "fixture"),
            new CompatResolveCore.Target(CompatResolveCore.Cleanup, "cleanupvfx", "ModA", "A.Z", "Cleanup", "fixture") };
        int[] l = CompatResolveCore.Loaded(fx, new string[] { "mscorlib", "Sims3GameplaySystems", "ModA", "ModA", "ts3fixes" });
        Check(l.Length == 2 && l[0] == 0 && l[1] == 2, "loaded: ModA entries only, in order (" + string.Join(",", Array.ConvertAll(l, x => x.ToString())) + ")");
        Check(CompatResolveCore.Loaded(fx, new string[] { "mscorlib", "Sims3GameplaySystems" }).Length == 0, "no mod loaded: nothing");
        Check(CompatResolveCore.Loaded(fx, new string[] { "moda" }).Length == 0, "assembly names match case-sensitively");
        Check(CompatResolveCore.Loaded(t, new string[] { "Sims3GameplaySystems", "Sims3GameplayObjects", "Sims3StoreObjects", "ts3fixes" }).Length == 0, "game assemblies alone: no target");

        // Enhanced Hospitals (birthroute on the mod's birth step): attach only with its assembly loaded, all four targets
        // (two HaveTheBaby overrides, two birth Cleanups), each in its own slot; stand down (nothing) without it
        const string eh = "PuddingFace_EnhancedHospital";
        int[] ehOn = CompatResolveCore.Loaded(t, new string[] { "Sims3GameplaySystems", eh, "ts3fixes" });
        int nHave = 0, nClean = 0;
        foreach (int i in ehOn)
        {
            Check(t[i].Asm == eh && t[i].Fix == "birthroute", "eh: only its targets attach (" + t[i].Type + ")");
            if (t[i].Kind == CompatResolveCore.BirthHave) nHave++;
            if (t[i].Kind == CompatResolveCore.BirthCleanup) nClean++;
        }
        Check(ehOn.Length == 4 && nHave == 2 && nClean == 2, "eh: 2 HaveTheBaby + 2 Cleanup targets (" + ehOn.Length + ")");
        Check(CompatResolveCore.Loaded(t, new string[] { "Sims3GameplaySystems", "PuddingFace_EnhancedVampires", "ts3fixes" }).Length == 5, "eh absent: only the Enhanced Vampires rows attach");
        Check(Array.IndexOf(ehOn, -1) < 0, "eh targets have slots");
        // the decision before the mod's HaveTheBaby body
        Check(CompatResolveCore.BirthModBefore(false, true, false, true, true) == CompatResolveCore.BmOff, "birthroute off: the mod's body alone");
        Check(CompatResolveCore.BirthModBefore(true, false, false, true, false) == CompatResolveCore.BmNothing, "finished pregnancy: nothing (F3)");
        Check(CompatResolveCore.BirthModBefore(true, true, true, false, false) == CompatResolveCore.BmNothing, "birth already queued: nothing (F3)");
        Check(CompatResolveCore.BirthModBefore(true, true, false, true, true) == CompatResolveCore.BmDefer, "npc inside its wait: deferred (F1)");
        Check(CompatResolveCore.BirthModBefore(true, true, false, false, true) == CompatResolveCore.BmRun, "household / unknown scope never waits: EA's 1 minute");
        Check(CompatResolveCore.BirthModBefore(true, true, false, true, false) == CompatResolveCore.BmRun, "npc past its wait: the mod's body");
        Check(CompatResolveCore.BirthModRearm(false, true), "nothing queued, pregnancy live: re-arm (F2)");
        Check(!CompatResolveCore.BirthModRearm(false, false), "the mod cleared the pregnancy (no hospital): no re-arm");
        Check(!CompatResolveCore.BirthModRearm(true, true), "queued: no re-arm");

        // docs/328 §7.5: the six mods with no hook before. Each attaches only with its own assembly loaded.
        Check(Array.Exists(t, x => x.Asm == "ani_BistroSet") == false, "bistro: not listed (inlined empty Cleanup, Patch refuses it)");
        foreach (string bih in new string[] { "_b_BreakIntoHouse", "BreakIntoHouseModFixed", "BreakIntoHouseModFlavor2" })
        {
            int[] on = CompatResolveCore.Loaded(t, new string[] { "Sims3GameplaySystems", bih, "ts3fixes" });
            Check(on.Length == 2 && t[on[0]].Kind == CompatResolveCore.BreakRun && t[on[1]].Kind == CompatResolveCore.BreakCleanup && t[on[0]].Type == "Jadaytime.BreakIntoHouse", "break into house " + bih + ": Run + Cleanup");
        }
        int[] gh = CompatResolveCore.Loaded(t, new string[] { "NRaasGoHere" });
        Check(gh.Length == 4, "gohere: visit Run, failreplan SetRouteOptions, doorgen x2 (" + gh.Length + ")");
        Check(CompatResolveCore.Loaded(t, new string[] { "NRaasMasterController" }).Length == 4, "mastercontroller: casrowleak x2 + doorgen x2");
        Check(CompatResolveCore.Loaded(t, new string[] { "NRaasWoohooer" }).Length == 3, "woohooer: woohootywait + doorgen x2");
        Check(CompatResolveCore.Loaded(t, new string[] { "Sims3.Diriel.Elevator" }).Length == 1, "modern elevators: elevatorresetriders");
        Check(CompatResolveCore.Loaded(t, new string[] { "GlobalOptionsSewingTable" }).Length == 1, "sewing table: modquitguard");
        Check(CompatResolveCore.Loaded(t, new string[] { "StudentUnionLibrary" }).Length == 1, "student union: visit Run");
        const string lyn = "Sims3.Gameplay.Objects.LynMods.StudentUnionLibrary.VisitCommunityLotLabLecture", ghv = "NRaas.GoHereSpace.Interactions.VisitCommunityLotEx";
        int[] nl = CompatResolveCore.NotLoadedOnDemand(CompatResolveCore.Targets, new string[] { "StoreObjectsTeppanyakiGrill", "NRaasGoHere" });
        bool nlOk = nl.Length > 0;
        foreach (int i in nl) nlOk &= CompatResolveCore.Targets[i].Asm.StartsWith("StoreObjects") && CompatResolveCore.Targets[i].Asm != "StoreObjectsTeppanyakiGrill";
        bool woodIn = false; foreach (int i in nl) woodIn |= CompatResolveCore.Targets[i].Asm == "StoreObjectsWoodFireOven";
        Check(nlOk && woodIn, "not loaded: store targets only, loaded store asm and mods left out");
        Check(CompatResolveCore.Line(CompatResolveCore.Targets[nl[0]], "-", "not loaded").EndsWith(" -): not loaded"), "not-loaded line format");
        Check(CompatResolveCore.VisitEaShortcut("StudentUnionLibrary", lyn, false, false), "lynmods outside university: EA visit shortcut");
        Check(!CompatResolveCore.VisitEaShortcut("StudentUnionLibrary", lyn, true, true), "lynmods in the university world: its own Run, opt-in path only");
        Check(!CompatResolveCore.VisitEaShortcut("StudentUnionLibrary", lyn + "X", false, false) && !CompatResolveCore.VisitEaShortcut("Other", lyn, false, false), "exact asm + type only");
        Check(CompatResolveCore.VisitEaShortcut("NRaasGoHere", ghv, true, true) && !CompatResolveCore.VisitEaShortcut("NRaasGoHere", ghv, false, false), "gohere: only with unseenfbgohere on");
        Check(!CompatResolveCore.VisitEaShortcut("NRaasGoHere", "NRaas.GoHereSpace.Interactions.VisitCommunityLotWithEx", false, true), "gohere With visit: never");

        // survey stop: the mod's own exit test (no occupation or no job), not EA's Lifeguard class test
        Check(CompatResolveCore.SurveyStop(false, false), "no occupation: stop");
        Check(CompatResolveCore.SurveyStop(true, false), "occupation without a job (quit or fired mid-shift): stop");
        Check(!CompatResolveCore.SurveyStop(true, true), "on the job: the mod's loop runs");

        string line = CompatResolveCore.Line(fx[0], "1234abcd", "ok");
        Check(line == "compat precedence: cleanupvfx on A.X.Cleanup (ModA 1234abcd): ok", "log line format (" + line + ")");

        Console.WriteLine(sFails == 0 ? "compatresolve selftest: ok" : "compatresolve selftest: " + sFails + " FAIL");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
