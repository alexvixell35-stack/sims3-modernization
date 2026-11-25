namespace Ts3Fixes
{
    // Game-independent table of the docs/328 §7 runtime precedence targets (CompatResolve.cs): mod methods that replace a game
    // method one of our EA fixes hooks, without calling the game's (an override with no base call, or a same-named copy that
    // hides a non-virtual method), where the comparison found our fix better and safe to add around the mod's own body. The
    // mod's files are never changed: our hook goes onto its method at run time, the way it sits on the game's. Matched by
    // assembly and type name, any build: each hook is a no-op when the mod's method does not have the bug (see the kind notes).
    // Self-check: tools/selftest/compatresolve.
    public static class CompatResolveCore
    {
        // Kinds: the fix and the hook shape.
        //   Cleanup:     cleanupvfx after the mod's Cleanup: a live game disposes the interaction's effect list (mVfxs), then
        //                nulls it, so a build that does call the base (whose hook already disposed and nulled it) sees null.
        //   SurveyLot:   lifeguardquit before the mod's survey loop body: the mod's own exit test (no job) ends the loop at
        //                once, as EA's fix does, instead of running on into the rescue check that reads the missing job.
        //   PortalStart: stairwaitend around the mod's OnPortalStart: the 30 sim-minute deadline on the wait entry the mod's
        //                copy puts in its own mSleepingTasks (SweTasksOf); household Sims keep the wait (HouseholdExempt).
        //   HarvestCopy: harvestextras instead of a mod's static copy of HarvestPlant.DoHarvest (docs/328 rule static-copy):
        //                our fixed body (B13HeHook.HeDoHarvest); a MoneyTree keeps the mod's copy (its own money-tree journal).
        //   WishCopy:    wishupdatelost and dreamcleanup part 2 around a mod's static copy of DreamsAndPromisesManager.Update
        //                (the same W86UpdateBegin / DcBeforeUpdate / DcAfterUpdate / W86UpdateEnd calls as WlMgr.UpdateHook).
        //   OccultAdd / OccultRemove: doorgen's door-answer refresh (DoorGenBump, occult source) after NRaas Common's
        //                OccultTypeHelper.AddOccultType copy / Remove (it flips mCurrentOccultTypes itself, never calling EA's
        //                Add/RemoveOccultType), compiled into most NRaas mods: one target per assembly.
        //   BoatRun:     boatroamcap before a mod's Boat.BoatAround subclass Run that roams on its own for some boats (no base
        //                call there); while the mod's body runs, a base call it makes is not checked a second time.
        //   BirthHave / BirthCleanup: birthroute around a mod's own birth step (Enhanced Hospitals: donor and surrogate
        //                pregnancies override Pregnancy.HaveTheBaby without base and queue their own HaveBabyHospital*
        //                interactions, whose Cleanup re-haves 1 sim-minute later for ever). HaveTheBaby: F3 (stale or duplicate
        //                call does nothing), F1 (NPC re-haves wait 1, 15, then 60 min; an unseen NPC mom is placed at the
        //                hospital from the 2nd attempt), F2 (re-arm when nothing was queued, 3 h watchdog); Cleanup: F5 and the
        //                failed / completed birth counts. Household, selected and unknown-scope moms: EA's 1-minute re-have.
        //   VisitRun:    our visit shortcut ("visit", default on) on a mod's VisitCommunityLot subclass Run that ApplyTripSubclasses
        //                patched (VclSubHook*, no hook class of its own): only where the mod's Run is EA's for the lots the shortcut
        //                takes (VisitEaShortcut). The line reports that patch.
        //   ElevReset:   elevatorresetriders after a mod's copy of ElevatorInterior.ElevatorPortalComponent.OnReset (same fields:
        //                mAssignedSims, ActiveUserEntry.mbSleeping / mTask): waiting riders get EA's failsafe reset at once.
        //   BreakRun / BreakCleanup: privateeyecase part d around a mod's copy of the break-in interaction (Run sets the front
        //                door to Nobody, Cleanup to Anybody): the lock read before Run is put back after Cleanup.
        //   QuitGuard:   modquitguard (default ON) around a mod's world-quit handler that always throws: the exception stops
        //                at the handler, so the handlers after it in World.sOnWorldQuitEventHandler (EA's, ours, other mods') run.
        //   RouteOpts:   failreplan after a mod's static copy of SimRoutingComponent.SetRouteOptions (NRaas GoHere's routing
        //                component calls it for every Sim route): unseen townies only (TownieUnseen), as on EA's method.
        //   WooHootyWait: woohootywait before a mod's copy of SendWooHootyText.GoToLotSuccess (a subclass of EA's): our bounded
        //                wait for the texter; given up = the mod's body is skipped (EA's steps were never reached either).
        //   CasAdd / CasRow: casrowleak on a mod's static copy of CASClothingCategory.AddGridItem and its own row filler
        //                (NRaas MasterController): the empty row it neither adds nor disposes is disposed, as on EA's.
        public const int Cleanup = 0, SurveyLot = 1, PortalStart = 2, HarvestCopy = 3, WishCopy = 4, OccultAdd = 5, OccultRemove = 6, BoatRun = 7, BirthHave = 8, BirthCleanup = 9,
            VisitRun = 10, ElevReset = 11, BreakRun = 12, BreakCleanup = 13, QuitGuard = 14, RouteOpts = 15, WooHootyWait = 16, CasAdd = 17, CasRow = 18;
        public static readonly int[] Slots = { 12, 1, 1, 2, 1, 24, 24, 1, 2, 2, 4, 1, 3, 3, 1, 1, 1, 1, 1 };  // hook classes per kind in CompatResolve.cs (VisitRun: report only)

        public sealed class Target
        {
            public readonly int Kind; public readonly string Fix, Asm, Type, Method, Mod;
            public Target(int kind, string fix, string asm, string type, string method, string mod)
            { Kind = kind; Fix = fix; Asm = asm; Type = type; Method = method; Mod = mod; }
        }

        // From the docs/328 §3 rescan (static: no base call / hiding copy read from the IL; the method's tokens read for the
        // fix's field or test) and the NRaas source audit (docs/318: copies called by name). Within a kind, table order = hook slot.
        public static readonly Target[] Targets = {
            new Target(Cleanup, "cleanupvfx", "StoreObjectsWoodFireOven", "Sims3.Store.Objects.WoodFireOven+WOBake", "Cleanup", "Sims 3 Store Updates 021-040"),
            new Target(Cleanup, "cleanupvfx", "StoreObjectsTeppanyakiGrill", "Sims3.Store.Objects.TeppanyakiGrill+TGCook", "Cleanup", "Sims 3 Store Updates 041-051"),
            new Target(Cleanup, "cleanupvfx", "StoreObjectsTeppanyakiGrill", "Sims3.Store.Objects.TeppanyakiGrill+JuggleEggTrick", "Cleanup", "Sims 3 Store Updates 041-051"),
            new Target(Cleanup, "cleanupvfx", "StoreObjectsTeppanyakiGrill", "Sims3.Store.Objects.TeppanyakiGrill+OnionVolcano", "Cleanup", "Sims 3 Store Updates 041-051"),
            new Target(Cleanup, "cleanupvfx", "Savanita.RomanticInteractions", "Savanita.RomanticInteractions.RomanticInteractionsBase+TakeShowerWithSim+ShowerTogetherX", "Cleanup", "More Romantic Interactions v2 (691427)"),
            // not listed: Bistro Set (513025) OFBOven+DoWaiterIdles.Cleanup, an empty body the game inlines (shape 1): Patch refuses it (docs/293)
            new Target(Cleanup, "cleanupvfx", "PuddingFace_EnhancedVampires", "VampInteraction.DemoClass+CompelToDrink", "Cleanup", "Enhanced Vampires (648599)"),
            new Target(Cleanup, "cleanupvfx", "PuddingFace_EnhancedVampires", "VampInteraction.DemoClass+AskForPermissionToDrink", "Cleanup", "Enhanced Vampires (648599)"),
            new Target(Cleanup, "cleanupvfx", "PuddingFace_EnhancedVampires", "VampInteraction.DemoClass+CompelToDrinkAuto", "Cleanup", "Enhanced Vampires (648599)"),
            new Target(Cleanup, "cleanupvfx", "PuddingFace_EnhancedVampires", "VampInteraction.DemoClass+CompelToDrinkMaster", "Cleanup", "Enhanced Vampires (648599)"),
            new Target(Cleanup, "cleanupvfx", "PuddingFace_EnhancedVampires", "VampInteraction.DemoClass+CommandToDrink", "Cleanup", "Enhanced Vampires (648599)"),
            new Target(SurveyLot, "lifeguardquit", "icarusallsorts.PoolLifeguard", "Sims3.Gameplay.ActiveCareer.ActiveCareers.icarusallsorts.SurveyBeach", "SurveyLot", "Pool Jobs for Lifeguards (574660)"),
            new Target(PortalStart, "stairwaitend", "lizcandorToddlerIndependence", "Sims3.Gameplay.Core.lizcandor.ToddlerIndependence.lizcandorStairs+StairsPortalComponent", "OnPortalStart", "Toddler Independence (655350)"),
            new Target(HarvestCopy, "harvestextras", "NRaasOverwatch", "NRaas.OverwatchSpace.Helpers.HarvestPlantEx", "DoHarvest", "NRaas Overwatch"),
            new Target(HarvestCopy, "harvestextras", "NRaasStoryProgressionSkill", "NRaas.StoryProgressionSpace.Interactions.HarvestEx", "DoHarvest", "NRaas StoryProgression Skill"),
            new Target(BoatRun, "boatroamcap", "icarusallsorts.AutonomousFunInTheSun", "Sims3.Gameplay.Objects.Vehicles.icarusallsorts.BoatAround", "Run", "Autonomous Fun in the Sun (530601)"),
            new Target(BirthHave, "birthroute", "PuddingFace_EnhancedHospital", "PuddingFace_EnhancedHospital.DemoClass+CreateDonorBaby", "HaveTheBaby", "Enhanced Hospitals (666384)"),
            new Target(BirthHave, "birthroute", "PuddingFace_EnhancedHospital", "PuddingFace_EnhancedHospital.DemoClass+CreateDonorBabySurrogate", "HaveTheBaby", "Enhanced Hospitals (666384)"),
            new Target(BirthCleanup, "birthroute", "PuddingFace_EnhancedHospital", "PuddingFace_EnhancedHospital.DemoClass+HaveBabyHospitalDonor", "Cleanup", "Enhanced Hospitals (666384)"),
            new Target(BirthCleanup, "birthroute", "PuddingFace_EnhancedHospital", "PuddingFace_EnhancedHospital.DemoClass+HaveBabyHospital", "Cleanup", "Enhanced Hospitals (666384)"),
            new Target(VisitRun, "visit", "StudentUnionLibrary", "Sims3.Gameplay.Objects.LynMods.StudentUnionLibrary.VisitCommunityLotLabLecture", "Run", "University More Shell Entries (611709)"),
            new Target(VisitRun, "visit", "NRaasGoHere", "NRaas.GoHereSpace.Interactions.VisitCommunityLotEx", "Run", "NRaas GoHere"),
            new Target(ElevReset, "elevatorresetriders", "Sims3.Diriel.Elevator", "Sims3.Diriel.Elevator.ModernElevatorInterior+ElevatorPortalComponent", "OnReset", "Modern Elevators (469658)"),
            new Target(BreakRun, "privateeyecase", "_b_BreakIntoHouse", "Jadaytime.BreakIntoHouse", "Run", "Break Into House (650590)"),
            new Target(BreakRun, "privateeyecase", "BreakIntoHouseModFixed", "Jadaytime.BreakIntoHouse", "Run", "Break Into House (650590), FIXED build"),
            new Target(BreakRun, "privateeyecase", "BreakIntoHouseModFlavor2", "Jadaytime.BreakIntoHouse", "Run", "Break Into House (650590), Flavor2 FIXED build"),
            new Target(BreakCleanup, "privateeyecase", "_b_BreakIntoHouse", "Jadaytime.BreakIntoHouse", "Cleanup", "Break Into House (650590)"),
            new Target(BreakCleanup, "privateeyecase", "BreakIntoHouseModFixed", "Jadaytime.BreakIntoHouse", "Cleanup", "Break Into House (650590), FIXED build"),
            new Target(BreakCleanup, "privateeyecase", "BreakIntoHouseModFlavor2", "Jadaytime.BreakIntoHouse", "Cleanup", "Break Into House (650590), Flavor2 FIXED build"),
            new Target(QuitGuard, "modquitguard", "GlobalOptionsSewingTable", "Lyralei.GlobalOptionsSewingTable", "OnWorldQuit", "Functional Sewing Table (643837)"),
            new Target(RouteOpts, "failreplan", "NRaasGoHere", "NRaas.CommonSpace.Helpers.SimRoutingComponentEx", "SetRouteOptions", "NRaas GoHere"),
            new Target(WooHootyWait, "woohootywait", "NRaasWoohooer", "NRaas.WoohooerSpace.Interactions.SendWooHootyTextEx", "GoToLotSuccessEx", "NRaas Woohooer"),
            new Target(CasAdd, "casrowleak", "NRaasMasterController", "NRaas.MasterControllerSpace.CAS.CASClothingCategoryEx", "AddGridItem", "NRaas MasterController"),
            new Target(CasRow, "casrowleak", "NRaasMasterController", "NRaas.MasterControllerSpace.CAS.CASClothingRowEx", "CreateGridItems", "NRaas MasterController"),
            new Target(WishCopy, "wishupdatelost", "NRaasDreamer", "NRaas.DreamerSpace.Helpers.DreamsAndPromisesManagerEx", "Update", "NRaas Dreamer"),
            new Target(OccultAdd, "doorgen", "NRaasCareer", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasCareer"),
            new Target(OccultAdd, "doorgen", "NRaasConsigner", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasConsigner"),
            new Target(OccultAdd, "doorgen", "NRaasDebugEnabler", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasDebugEnabler"),
            new Target(OccultAdd, "doorgen", "NRaasDresser", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasDresser"),
            new Target(OccultAdd, "doorgen", "NRaasErrorTrap", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasErrorTrap"),
            new Target(OccultAdd, "doorgen", "NRaasGoHere", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasGoHere"),
            new Target(OccultAdd, "doorgen", "NRaasHybrid", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasHybrid"),
            new Target(OccultAdd, "doorgen", "NRaasMasterController", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasMasterController"),
            new Target(OccultAdd, "doorgen", "NRaasMover", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasMover"),
            new Target(OccultAdd, "doorgen", "NRaasOverwatch", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasOverwatch"),
            new Target(OccultAdd, "doorgen", "NRaasPorter", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasPorter"),
            new Target(OccultAdd, "doorgen", "NRaasPortraitPanel", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasPortraitPanel"),
            new Target(OccultAdd, "doorgen", "NRaasRegister", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasRegister"),
            new Target(OccultAdd, "doorgen", "NRaasRelationshipPanel", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasRelationshipPanel"),
            new Target(OccultAdd, "doorgen", "NRaasRelativity", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasRelativity"),
            new Target(OccultAdd, "doorgen", "NRaasSelector", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasSelector"),
            new Target(OccultAdd, "doorgen", "NRaasStoryProgression", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasStoryProgression"),
            new Target(OccultAdd, "doorgen", "NRaasTagger", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasTagger"),
            new Target(OccultAdd, "doorgen", "NRaasTraveler", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasTraveler"),
            new Target(OccultAdd, "doorgen", "NRaasWoohooer", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "AddOccultType", "NRaasWoohooer"),
            new Target(OccultRemove, "doorgen", "NRaasCareer", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasCareer"),
            new Target(OccultRemove, "doorgen", "NRaasConsigner", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasConsigner"),
            new Target(OccultRemove, "doorgen", "NRaasDebugEnabler", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasDebugEnabler"),
            new Target(OccultRemove, "doorgen", "NRaasDresser", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasDresser"),
            new Target(OccultRemove, "doorgen", "NRaasErrorTrap", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasErrorTrap"),
            new Target(OccultRemove, "doorgen", "NRaasGoHere", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasGoHere"),
            new Target(OccultRemove, "doorgen", "NRaasHybrid", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasHybrid"),
            new Target(OccultRemove, "doorgen", "NRaasMasterController", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasMasterController"),
            new Target(OccultRemove, "doorgen", "NRaasMover", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasMover"),
            new Target(OccultRemove, "doorgen", "NRaasOverwatch", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasOverwatch"),
            new Target(OccultRemove, "doorgen", "NRaasPorter", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasPorter"),
            new Target(OccultRemove, "doorgen", "NRaasPortraitPanel", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasPortraitPanel"),
            new Target(OccultRemove, "doorgen", "NRaasRegister", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasRegister"),
            new Target(OccultRemove, "doorgen", "NRaasRelationshipPanel", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasRelationshipPanel"),
            new Target(OccultRemove, "doorgen", "NRaasRelativity", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasRelativity"),
            new Target(OccultRemove, "doorgen", "NRaasSelector", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasSelector"),
            new Target(OccultRemove, "doorgen", "NRaasStoryProgression", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasStoryProgression"),
            new Target(OccultRemove, "doorgen", "NRaasTagger", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasTagger"),
            new Target(OccultRemove, "doorgen", "NRaasTraveler", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasTraveler"),
            new Target(OccultRemove, "doorgen", "NRaasWoohooer", "NRaas.CommonSpace.Helpers.OccultTypeHelper", "Remove", "NRaasWoohooer"),
        };

        // Hook slot of table entry i: its index among the entries of its kind; -1 when the kind has no slot left.
        public static int Slot(Target[] table, int i)
        {
            int n = 0;
            for (int k = 0; k < i; k++) if (table[k].Kind == table[i].Kind) n++;
            return n < Slots[table[i].Kind] ? n : -1;
        }

        // Table entries whose assembly is loaded, in table order (loaded: names of the loaded assemblies).
        public static int[] Loaded(Target[] table, string[] loaded)
        {
            int[] tmp = new int[table.Length];
            int n = 0;
            for (int i = 0; i < table.Length; i++)
                foreach (string a in loaded)
                    if (a == table[i].Asm) { tmp[n++] = i; break; }
            int[] r = new int[n];
            System.Array.Copy(tmp, r, n);
            return r;
        }

        // The SurveyLot stop test for the mod's copy: its own exit test (no occupation or no current job), not EA's
        // "is a Lifeguard" test, so a mod career that is no Lifeguard class keeps working.
        public static bool SurveyStop(bool hasOccupation, bool hasJob) { return !(hasOccupation && hasJob); }

        // BirthHave: what our hook does before the mod's HaveTheBaby body. Off = 3 (the mod's body alone). A call for a finished
        // pregnancy or while the mod's birth interaction is queued with the birth due = 1 (nothing, F3). An NPC mom inside her
        // re-have wait = 2 (deferred, F1); household / selected / unknown-scope moms (npc false) never wait: EA's 1 minute.
        // Else 0 (the mod's body, then BirthModRearm).
        public const int BmRun = 0, BmNothing = 1, BmDefer = 2, BmOff = 3;
        public static int BirthModBefore(bool on, bool current, bool queued, bool npc, bool inWait)
        {
            if (!on) return BmOff;
            if (!current || queued) return BmNothing;
            if (npc && inWait) return BmDefer;
            return BmRun;
        }

        // After the mod's body: re-arm (F2) only when it queued no birth interaction and the pregnancy is still the mom's (the
        // mod clears it itself when no hospital exists: nothing left to birth).
        public static bool BirthModRearm(bool queuedAfter, bool stillCurrent) { return !queuedAfter && stillCurrent; }

        // VisitRun: does this mod visit Run get our EA visit shortcut ("visit") instead of the opt-in unseenfbtrip path? LynMods'
        // Run is EA's line for line outside the University world [verified, docs/318 611709]; GoHere's is a near copy that the
        // placement composes with [inferred, docs/328 §7.5], so only with unseenfbgohere on. Anything else: no.
        public static bool VisitEaShortcut(string asm, string type, bool universityWorld, bool goHereOn)
        {
            if (asm == "StudentUnionLibrary" && type == "Sims3.Gameplay.Objects.LynMods.StudentUnionLibrary.VisitCommunityLotLabLecture") return !universityWorld;
            if (asm == "NRaasGoHere" && type == "NRaas.GoHereSpace.Interactions.VisitCommunityLotEx") return goHereOn;
            return false;
        }

        // Store content (StoreObjects* assemblies, also in the Store Updates repacks) loads its script assembly on demand, when
        // the save or catalog first needs the object [counted: 30 of 106 package assemblies loaded in ip-rules-r-resolve-on
        // 2026-11-23], so its targets may be absent at world load by design: the indexes of those not loaded, logged "not loaded".
        public static int[] NotLoadedOnDemand(Target[] table, string[] loaded)
        {
            int[] on = Loaded(table, loaded);
            System.Collections.Generic.List<int> r = new System.Collections.Generic.List<int>();
            for (int i = 0; i < table.Length; i++)
                if (table[i].Asm.StartsWith("StoreObjects", System.StringComparison.Ordinal) && System.Array.IndexOf(on, i) < 0) r.Add(i);
            return r.ToArray();
        }

        // One log line per target: "compat precedence: <fix> on <type>.<method> (<asm> <mvid8>): <result>".
        public static string Line(Target t, string mvid8, string result)
        {
            return "compat precedence: " + t.Fix + " on " + t.Type + "." + t.Method + " (" + t.Asm + " " + mvid8 + "): " + result;
        }
    }
}
