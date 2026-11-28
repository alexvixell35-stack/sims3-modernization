namespace Ts3Fixes
{
    // Harness checks for EpBatch1aLogic.cs / EpBatch1bLogic.cs (docs/587, batch EP1). Pure decisions with controls;
    // nothing in the world is touched. Each "run" prints BROKEN with the switch off and FIXED with it on.
    public static partial class Fixes
    {
        static string EpMiss(params object[] namesAndMembers)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (int i = 0; i + 1 < namesAndMembers.Length; i += 2)
                if (namesAndMembers[i + 1] == null) b.Append(b.Length == 0 ? "" : ", ").Append((string)namesAndMembers[i]);
            return b.ToString();
        }

        // DisplayCaseResetCheck run: a reset with 3 stored items keeps them. Controls: the build-mode move, an empty case
        //   and Dispose (not in a reset) run EA's code.
        public static object DisplayCaseResetCheck(string what)
        {
            string miss = EpJoin(EpMiss("GameObject+ResetInformation", sDcResetInfo), EpLive(typeof(BDcHook), 2, "GameObject+ResetInformation", sDcResetInfo, null));
            string counters = B582Counters("displaycasereset", 3835, new[] { "resets kept contents", "destroyed with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "displaycasereset <run|result>";
            bool on = ShortcutOn("displaycasereset");
            bool fixedNow = DcKeep(on, true, false, 3), controls = !DcKeep(on, true, true, 3) && !DcKeep(on, true, false, 0) && !DcKeep(on, false, false, 3);
            return B582Line("displaycasereset", on, "reset keeps 3 items", fixedNow, "build-mode move / empty / Dispose run EA's code=" + controls, controls, miss, counters);
        }

        // SuatGradeBonusCheck run: GPA A uses kGradeBonus[0] (best), school A uses [0]. Controls: GPA C and school B
        //   unchanged, GPA F and no grade get no multiplier.
        public static object SuatGradeBonusCheck(string what)
        {
            string miss = EpJoin(EpMiss("TakeAptitudeTest.kGradeBonus", sSuBonus), EpLive(typeof(BSuHook), 1, "UniversityWelcomeKit+TakeAptitudeTest", sSuTest, null, "TakeAptitudeTest.kGradeBonus", sSuBonus, typeof(float[])));
            string counters = B582Counters("suatgradebonus", 3241, new[] { "scores corrected", "differ with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "suatgradebonus <run|result>";
            bool on = ShortcutOn("suatgradebonus");
            bool fixedNow = SuIndex(on, true, 4) == 0 && SuIndex(on, false, 0) == 0 && SuIndex(on, true, 1) == 3 && SuCorrects(on, true, 4) && SuCorrects(on, false, 0);
            bool controls = SuIndex(on, true, 2) == 2 && SuIndex(on, false, 1) == 1 && SuIndex(on, true, 0) == -1 && SuIndex(on, false, -1) == -1
                && SuClamp(50) == 100 && SuClamp(520) == 400 && !SuCorrects(on, true, 2) && !SuCorrects(on, false, 1) && !SuCorrects(on, true, 0);
            return B582Line("suatgradebonus", on, "GPA A, school A best bonus, GPA D second worst", fixedNow, "GPA C / school B / F / no grade / clamp unchanged=" + controls, controls, miss, counters);
        }

        // MermaidSharkNullCheck run: a failed placement no longer reaches the null RequestState. Controls: placed / lot full
        //   never reach it.
        public static object MermaidSharkNullCheck(string what)
        {
            string miss = EpJoin(EpMiss("SummonShark.kMaxSharksOnLot", sMsMax, "SummonShark.kMaxDistanceForExistingShark", sMsDist),
                EpLive(typeof(BMsHook), 1, "SummonShark.kMaxSharksOnLot", sMsMax, typeof(int), "SummonShark.kMaxDistanceForExistingShark", sMsDist, typeof(float)));
            string counters = B582Counters("mermaidsharknull", 3838, new[] { "failed placements took the fallback", "NRE with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "mermaidsharknull <run|result>";
            bool on = ShortcutOn("mermaidsharknull");
            bool fixedNow = !MsReachesNull(on, 2, 10, true, false), controls = !MsReachesNull(!on, 2, 10, true, true) && !MsReachesNull(!on, 10, 10, true, false);
            return B582Line("mermaidsharknull", on, "failed placement ends without the NRE", fixedNow, "placed / lot full unchanged=" + controls, controls, miss, counters);
        }

        // JetpackFlyHereCleanupCheck run: no active jetpack skips StopFX. Control: with a jetpack EA's Cleanup runs.
        public static object JetpackFlyHereCleanupCheck(string what)
        {
            string miss = EpJoin(EpMiss("FlyHere.mTakeOffJig", sJfTakeOff, "FlyHere.mLandingJig", sJfLanding),
                EpLive(typeof(BJfHook), 1, "FlyHere.mTakeOffJig", sJfTakeOff, typeof(Sims3.Gameplay.Abstracts.GameObject), "FlyHere.mLandingJig", sJfLanding, typeof(Sims3.Gameplay.Abstracts.GameObject)));
            string counters = B582Counters("jetpackflyherecleanup", 3845, new[] { "cleanups without jetpack", "seen with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "jetpackflyherecleanup <run|result>";
            bool on = ShortcutOn("jetpackflyherecleanup");
            bool fixedNow = JfSkip(on, false), controls = !JfSkip(on, true);
            return B582Line("jetpackflyherecleanup", on, "no-jetpack cleanup skips StopFX", fixedNow, "with jetpack runs EA's code=" + controls, controls, miss, counters);
        }

        // NaniteRouteFailCheck run: route failed before pickup keeps the terrarium. Controls: picked up, canceled, no exit
        //   reason run EA's code.
        public static object NaniteRouteFailCheck(string what)
        {
            string miss = EpJoin(EpMiss("ReverseEngineer.mProp", sNrProp), EpLive(typeof(BNrHook), 1, "ReverseEngineer.mProp", sNrProp, typeof(Sims3.Gameplay.Abstracts.GameObject)));
            string counters = B582Counters("naniteroutefail", 3848, new[] { "terrariums kept", "destroyed with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "naniteroutefail <run|result>";
            bool on = ShortcutOn("naniteroutefail");
            bool fixedNow = NrKeep(on, true, false, false), controls = !NrKeep(on, true, false, true) && !NrKeep(on, true, true, false) && !NrKeep(on, false, false, false);
            return B582Line("naniteroutefail", on, "un-picked-up terrarium kept", fixedNow, "picked up / canceled / no exit unchanged=" + controls, controls, miss, counters);
        }

        // SpringDanceClearCheck run: a tally of 4 is cleared after judging. Control: an empty tally is left alone.
        public static object SpringDanceClearCheck(string what)
        {
            string miss = EpJoin(EpMiss("SpringDanceFloor.SimDancers", sSdDancers),
                EpLive(typeof(BSpdHook), 1, "SpringDanceFloor.SimDancers", sSdDancers, typeof(Sims3.Gameplay.Utilities.PairedListDictionary<Sims3.Gameplay.Actors.Sim, float>)));
            string counters = B582Counters("springdanceclear", 3851, new[] { "tallies cleared", "kept with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "springdanceclear <run|result>";
            bool on = ShortcutOn("springdanceclear");
            bool fixedNow = SdClear(on, 4), controls = !SdClear(on, 0);
            return B582Line("springdanceclear", on, "tally cleared after judging", fixedNow, "empty tally untouched=" + controls, controls, miss, counters);
        }

        // NectarCopyLevel10Check run: a level-10 source gives a level-10 copy. Control: a normal source gives a normal copy.
        public static object NectarCopyLevel10Check(string what)
        {
            string miss = EpJoin(EpMiss("NectarBottle.mBottleInfo", sNcInfo, "NectarBottle.UpdateVisualState", sNcVisual, "NectarBottle.CheckStartVFx", sNcFx),
                EpLive(typeof(BNcbHook), 1, "NectarBottle.mBottleInfo", sNcInfo, typeof(Sims3.Gameplay.Objects.CookingObjects.NectarBottle.NectarBottleGlassInfo),
                    "NectarBottle.UpdateVisualState", sNcVisual, 0, "NectarBottle.CheckStartVFx", sNcFx, 0));
            string counters = B582Counters("nectarcopylevel10", 3854, new[] { "copies completed", "incomplete with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "nectarcopylevel10 <run|result>";
            bool on = ShortcutOn("nectarcopylevel10");
            bool fixedNow = NcCopied(on, true), controls = !NcCopied(on, false);
            return B582Line("nectarcopylevel10", on, "level-10 flag copied", fixedNow, "normal bottle stays normal=" + controls, controls, miss, counters);
        }

        // HarvestEatVfxCheck run: a started effect is kept; a cat gets no effect. Controls: horse / small dog / piggy names as EA.
        public static object HarvestEatVfxCheck(string what)
        {
            string miss = EpJoin(EpMiss("EatHarvestablePet.mEatGroundVfx", sHeGround, "EatHarvestablePet.mEatFallVfx", sHeFall),
                EpLive(typeof(BHeHook), 2, "EatHarvestablePet", sHeType, null, "EatHarvestablePet.mEatGroundVfx", sHeGround, typeof(Sims3.SimIFace.VisualEffect),
                    "EatHarvestablePet.mEatFallVfx", sHeFall, typeof(Sims3.SimIFace.VisualEffect)));
            string counters = B582Counters("harvesteatvfx", 3857, new[] { "effects kept", "leaking starts with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "harvesteatvfx <run|result>";
            bool on = ShortcutOn("harvesteatvfx");
            bool fixedNow = HeKept(on, true) && !HeCreates(on, HeName(false, false, false, false, true));
            bool controls = HeName(true, false, false, false, true) == "ep5HarvestBitsGroundEat" && HeName(false, false, true, true, false) == "ep5HarvestBitsFallEatSmallDogPiggy"
                && HeCreates(on, "ep5HarvestBitsGroundEat") && !HeKept(on, false);
            return B582Line("harvesteatvfx", on, "effect kept, none for cats", fixedNow, "EA names / horse creates / nothing kept when none made=" + controls, controls, miss, counters);
        }

        // BuffetEmptyPickCheck run: 2 room tables, 1 with food -> no empty pick. Controls: all tables with food -> the room
        //   list as EA; no table with food -> nothing.
        public static object BuffetEmptyPickCheck(string what)
        {
            string miss = EpJoin(EpMiss("ResortBuffetTable.kFoodSlots", sBfSlots), EpLive(typeof(BBfHook), 1, "ResortBuffetTable.kFoodSlots", sBfSlots, typeof(Sims3.SimIFace.Slot[])));
            string counters = B582Counters("buffetemptypick", 3860, new[] { "picks limited to food", "could throw with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "buffetemptypick <run|result>";
            bool on = ShortcutOn("buffetemptypick");
            bool fixedNow = !BfMayPickEmpty(on, 2, 1, 3, 1) && BfFixes(on, 2, 1, 3, 1);
            bool controls = !BfMayPickEmpty(!on, 2, 2, 3, 3) && !BfFixes(on, 2, 2, 3, 3) && BfFixList(2, 3) == 1 && BfFixList(0, 3) == 2 && BfFixList(0, 0) == 0;
            return B582Line("buffetemptypick", on, "empty table never picked", fixedNow, "full tables as EA, lot fallback, none=" + controls, controls, miss, counters);
        }

        // KrakenFriendSailorCheck run: [Sailor, Friend] keeps the immunity. Controls: [Friend, Sailor], no friend, no sailor.
        public static object KrakenFriendSailorCheck(string what)
        {
            string miss = EpJoin(EpMiss("KrakenManager.mBoatList", sKrList), EpLive(typeof(BKrHook), 2, "KrakenManager.mBoatList", sKrList, typeof(System.Collections.ICollection)));
            string counters = B582Counters("krakenfriendsailor", 3863, new[] { "attacks dropped", "scheduled with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "krakenfriendsailor <run|result>";
            bool on = ShortcutOn("krakenfriendsailor");
            bool fixedNow = !KrImmunityLost(on, new[] { true, false }, new[] { false, true }) && KrDrop(on, true);
            bool controls = !KrMisses(new[] { false, true }, new[] { true, false }) && !KrMisses(new[] { true, false }, new[] { false, false })
                && !KrMisses(new[] { false, false }, new[] { false, true }) && KrMisses(new[] { true }, new[] { true }) && !KrDrop(on, false);
            return B582Line("krakenfriendsailor", on, "sailor-before-friend boat immune", fixedNow, "friend first / no friend / no sailor / both traits=" + controls, controls, miss, counters);
        }

        public static object MummyCurseTraitCheck(string what)
        {
            string miss = EpLive(typeof(BMcHook), 1);
            string counters = B582Counters("mummycursetrait", 3866, new[] { "visible picks", "any-trait picks with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "mummycursetrait <run|result>";
            bool on = ShortcutOn("mummycursetrait");
            bool fixedNow = !McMayRemoveHidden(on, false, false) && McVisiblePick(on, false, false);
            bool controls = !McMayRemoveHidden(!on, true, false) && !McMayRemoveHidden(!on, false, true) && !McVisiblePick(on, true, false) && !McVisiblePick(on, false, true);
            return B582Line("mummycursetrait", on, "only a visible trait removed", fixedNow, "Evil / Good Sims unchanged=" + controls, controls, miss, counters);
        }

        public static object BullRiderCleanupCheck(string what)
        {
            string miss = EpJoin(EpMiss("MechanicalBull+RideBull", sBrRide, "MechanicalBull.mCurrentRider", sBrRider),
                EpLive(typeof(BBrHook), 1, "MechanicalBull+RideBull", sBrRide, typeof(Sims3.Gameplay.Interactions.InteractionInstance), "MechanicalBull.mCurrentRider", sBrRider, typeof(Sims3.Gameplay.Actors.Sim)));
            string counters = B582Counters("bullridercleanup", 3869, new[] { "riders kept", "cleared with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "bullridercleanup <run|result>";
            bool on = ShortcutOn("bullridercleanup");
            bool fixedNow = BrKeep(on, true, true), controls = !BrKeep(on, false, true) && !BrKeep(on, true, false);
            return B582Line("bullridercleanup", on, "riding Sim kept as rider", fixedNow, "own ride / stale rider cleared=" + controls, controls, miss, counters);
        }

        public static object BroomTipPerformerCheck(string what)
        {
            string miss = EpJoin(EpMiss("MagicBroomArena.mCurrentPerformer", sBtPerformer), EpLive(typeof(BBtHook), 1, "MagicBroomArena.mCurrentPerformer", sBtPerformer, null));
            string counters = B582Counters("broomtipperformer", 3872, new[] { "tips skipped", "no performer with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "broomtipperformer <run|result>";
            bool on = ShortcutOn("broomtipperformer");
            bool fixedNow = !BtTips(on, true, false), controls = BtTips(on, true, true) && !BtTips(on, false, true);
            return B582Line("broomtipperformer", on, "no tip after the ride ended", fixedNow, "performer present tips / route failed none=" + controls, controls, miss, counters);
        }

        public static object BalancingBallCareerCheck(string what)
        {
            string miss = EpJoin(EpMiss("BalanceBall+Definition", sBbDef), EpLive(typeof(BBbHook), 1, "BalanceBall+Definition", sBbDef, null));
            string counters = B582Counters("balancingballcareer", 3875, new[] { "non-acrobats refused", "allowed with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "balancingballcareer <run|result>";
            bool on = ShortcutOn("balancingballcareer");
            bool fixedNow = !BbAllowed(on, true, false), controls = BbAllowed(on, true, true) && !BbAllowed(on, false, true);
            return B582Line("balancingballcareer", on, "singer refused", fixedNow, "performance artist allowed / EA false kept=" + controls, controls, miss, counters);
        }

        public static object ElvisGnomeGuardCheck(string what)
        {
            string miss = EpJoin(EpMiss("kChanceGnomeElvisSpawnsSandwich", sEgChanceSandwich, "kMusicPlaysFor", sEgPlaysFor, "mElvisMusicHandle", sEgHandle, "SpawnElvis(V,V)", sEgSpawnAt, "SpawnElvis()", sEgSpawnLot),
                EpLive(typeof(BEgHook), 5, "kChanceGnomeElvisSpawnsSandwich", sEgChanceSandwich, typeof(float), "kMusicPlaysFor", sEgPlaysFor, typeof(float),
                    "mElvisMusicHandle", sEgHandle, typeof(Sims3.SimIFace.ObjectSound), "SpawnElvis(V,V)", sEgSpawnAt, 2, "SpawnElvis()", sEgSpawnLot, 0));
            string counters = B582Counters("elvisgnomeguard", 3878, new[] { "throws / leaks guarded", "seen with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "elvisgnomeguard <run|result>";
            bool on = ShortcutOn("elvisgnomeguard");
            bool fixedNow = !EgSpawnRuns(on, false, false, 0) && EgLoops(on, 3) == 1;
            bool controls = EgSpawnRuns(on, false, true, 0) && EgLoops(on, 1) == 1 && EgLoops(on, 0) == 0;
            return B582Line("elvisgnomeguard", on, "no home lot skipped, one loop for 3 Sims", fixedNow, "home lot spawns / one Sim / none=" + controls, controls, miss, counters);
        }

        public static object ElvisGnomeCapCheck(string what)
        {
            string miss = EpLive(typeof(BEgHook), 5);
            string counters = B582Counters("elvisgnomecap", 3881, new[] { "spawns skipped", "rolled with a gnome, switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "elvisgnomecap <run|result>";
            bool on = ShortcutOn("elvisgnomecap");
            bool fixedNow = !EgSpawnRuns(false, on, true, 1), controls = EgSpawnRuns(false, on, true, 0);
            return B582Line("elvisgnomecap", on, "second gnome not spawned", fixedNow, "first gnome spawns=" + controls, controls, miss, counters);
        }

        public static object CatToyPickerRelCheck(string what)
        {
            string miss = EpJoin(EpMiss("CatToyWallDancer.IsValidSim", sCtValidSim, "CatToyWallDancer.IsValidPet", sCtValidPet),
                EpLive(typeof(BCtHook), 1, "CatToyWallDancer.IsValidSim", sCtValidSim, 1, "CatToyWallDancer.IsValidPet", sCtValidPet, 1));
            string counters = B582Counters("cattoypickerrel", 3244, new[] { "strangers added", "left out with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "cattoypickerrel <run|result>";
            bool on = ShortcutOn("cattoypickerrel");
            bool fixedNow = CtAdmit(on, false, 0f, -10f);
            // (the positive-threshold control used to expect EA to leave the stranger out: EA admits it, so it failed with the switch off)
            bool controls = CtAdmit(on, true, -5f, -10f) && !CtAdmit(on, true, -20f, -10f) && CtAdmit(on, false, 0f, 10f) == !on;
            return B582Line("cattoypickerrel", on, "stranger listed at -10", fixedNow, "liking above / below as EA, positive threshold: stranger out only with the fix=" + controls, controls, miss, counters);
        }

        public static object GourmetPetRecipeCheck(string what)
        {
            string miss = EpJoin(EpMiss("MakeGourmetFoodForPet", sGpType), EpLive(typeof(BGpHook), 1, "MakeGourmetFoodForPet", sGpType, null));
            string counters = B582Counters("gourmetpetrecipe", 3884, new[] { "choices corrected", "seen with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "gourmetpetrecipe <run|result>";
            bool on = ShortcutOn("gourmetpetrecipe");
            bool fixedNow = !GpOtherForHorses(on, false), controls = GpOtherForHorses(on, true);
            return B582Line("gourmetpetrecipe", on, "horse-only household keeps granola", fixedNow, "household with dogs/cats may switch=" + controls, controls, miss, counters);
        }

        public static object ChipAnyCountCheck(string what)
        {
            string miss = EpJoin(EpMiss("TraitChipStaticData.RemoveComponent(Type)", sChRemoveType), EpLive(typeof(BChHook), 3, "TraitChipStaticData.RemoveComponent(Type)", sChRemoveType, 3));
            string counters = B582Counters("chipanycount", 3893, new[] { "over-asks trimmed", "over-asks with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "chipanycount <run|result>";
            bool on = ShortcutOn("chipanycount");
            bool fixedNow = ChAsk(on, 2, true, 1) == 1, controls = ChAsk(on, 2, false, 1) == 2 && ChAsk(on, 2, true, 2) == 2;
            return B582Line("chipanycount", on, "medium call asks 1 after 1 small", fixedNow, "named component / met count unchanged=" + controls, controls, miss, counters);
        }

        public static object ChipFamilyCrystalCheck(string what)
        {
            string miss = EpJoin(EpMiss("TraitChipStaticData.RemoveComponent(ChargeTypes)", sChRemoveCrystal), EpLive(typeof(BChHook), 3, "TraitChipStaticData.RemoveComponent(ChargeTypes)", sChRemoveCrystal, 3));
            string counters = B582Counters("chipfamilycrystal", 3887, new[] { "family crystals consumed", "left with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "chipfamilycrystal <run|result>";
            bool on = ShortcutOn("chipfamilycrystal");
            bool fixedNow = ChFamilyRemoved(on, true) && ChNoFamilyGuard(on, false), controls = !ChFamilyRemoved(on, false) && !ChNoFamilyGuard(on, true);
            return B582Line("chipfamilycrystal", on, "family crystal removed from the family inventory", fixedNow, "own-inventory crystal not taken from the family=" + controls, controls, miss, counters);
        }

        public static object ChipComponentOrphanCheck(string what)
        {
            string miss = EpJoin(EpMiss("TraitChipStaticData.RemoveComponent(Type)", sChRemoveType), EpLive(typeof(BChHook), 3, "TraitChipStaticData.RemoveComponent(Type)", sChRemoveType, 3));
            string counters = B582Counters("chipcomponentorphan", 3890, new[] { "orphans destroyed", "left with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "chipcomponentorphan <run|result>";
            bool on = ShortcutOn("chipcomponentorphan");
            bool fixedNow = ChDestroy(on, true, false, false), controls = !ChDestroy(on, false, true, false) && !ChDestroy(on, true, true, false) && !ChDestroy(on, true, false, true);
            return B582Line("chipcomponentorphan", on, "consumed component destroyed", fixedNow, "kept / moved / already destroyed untouched=" + controls, controls, miss, counters);
        }

        public static object NaniteMergeCheck(string what)
        {
            string miss = EpJoin(EpMiss("BotBuildingSkill.mNanitesCollected", sNmList, "BotBuildingSkill.mTraitChipSchematicsLearned", sNmChips),
                EpLive(typeof(BNmHook), 1, "BotBuildingSkill.mNanitesCollected", sNmList, typeof(System.Collections.Generic.List<Sims3.Gameplay.Objects.Insect.InsectType>),
                    "BotBuildingSkill.mTraitChipSchematicsLearned", sNmChips, typeof(System.Collections.Generic.List<Sims3.Gameplay.ActorSystems.TraitChipName>)));
            string counters = B582Counters("nanitemerge", 3896, new[] { "lists merged", "left with switch off", "errors" }, miss);
            if (what == "result") return counters;
            if (what != "run") return "nanitemerge <run|result>";
            bool on = ShortcutOn("nanitemerge");
            bool fixedNow = !NmCountsAgain(on, true) && NmMerge(on, true), controls = !NmCountsAgain(on, false) && !NmMerge(on, false);
            return B582Line("nanitemerge", on, "collected nanite not counted again", fixedNow, "new nanite counts=" + controls, controls, miss, counters);
        }
    }
}
