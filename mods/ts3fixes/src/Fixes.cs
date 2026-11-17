using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // The fixes (docs/04 F8-F12). Each replaces a game method by copying a hook's MonoMethod header over it before the
    // interpreter compiles it, so they are applied once, at tuning time (Instantiator), before any world loads. A hook
    // calls the original only through an instance stub on its own class that received the original's header.
    // State shared with test tools through AppDomain data (the ts3agent test mod reads and switches it):
    //   ts3fixes.applied (log), ts3fixes.counters (int[]), ts3fixes.switches (name -> bool, overrides the tuning),
    //   ts3fixes.recent (last shortcuts), ts3fixes.moved (every Sim a shortcut moved).
    public static partial class Fixes
    {
        const int kMethodHeader = 40, kNameOffset = 0x18, kSlotOffset = 0x1C;

        static byte[] SwapMethod(MethodBase original, MethodBase replacement, bool keepVirtual = true)  // false: VirtualSwapCheck's old-way pair only
        {
            IntPtr o = original.MethodHandle.Value, r = replacement.MethodHandle.Value;
            byte[] saved = new byte[kMethodHeader], repl = new byte[kMethodHeader];
            Marshal.Copy(o, saved, 0, kMethodHeader);
            Marshal.Copy(r, repl, 0, kMethodHeader);
            if (keepVirtual) KeepVirtual(saved, repl);
            Marshal.Copy(repl, 0, o, kNameOffset);
            Marshal.Copy(repl, kNameOffset + 4, new IntPtr(o.ToInt32() + kNameOffset + 4), kMethodHeader - kNameOffset - 4);
            return saved;
        }

        // docs/536: a virtual original keeps its flags word (+0) and vtable slot (+0x1C bits 12-31). The callvirt resolver
        // (0xE51FC0) reads both from the method a call site names; with the hook's (no VIRTUAL flag) every receiver ran this
        // body and no override did, e.g. every x.Destroy() ran only ScriptObject.Destroy.
        static void KeepVirtual(byte[] saved, byte[] repl)
        {
            if ((BitConverter.ToUInt16(saved, 0) & (ushort)MethodAttributes.Virtual) == 0) return;
            repl[0] = saved[0]; repl[1] = saved[1];
            int slot = (BitConverter.ToInt32(repl, kSlotOffset) & 0xFFF) | (BitConverter.ToInt32(saved, kSlotOffset) & ~0xFFF);
            Array.Copy(BitConverter.GetBytes(slot), 0, repl, kSlotOffset, 4);
        }


        public static void Apply()
        {
            if (AppDomain.CurrentDomain.GetData("ts3fixes.applied") != null) return;
            AppDomain.CurrentDomain.SetData("ts3fixes.applied", new List<object>());
            PatchCounters();
            ApplyTaskCountHook();  // first: the setter must not have run yet
            ApplyAutShadowLocHooks();  // next: any localized string before ApplyAutShadow compiles them (inert while autshadow is off)
            Patch(typeof(GoHome), "Run", typeof(GoHomeHook), "RunHook", "RunOrig");
            Patch(typeof(RabbitHole), "RouteNearEntranceAndEnterRabbitHole", typeof(RabbitHoleHook), "EnterHook", "EnterOrig");
            Patch(typeof(VisitCommunityLot), "Run", typeof(VisitCommunityLotHook), "RunHook", "RunOrig");
            Patch(typeof(VisitLot), "Run", typeof(VisitLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToLot), "Run", typeof(GoToLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToCommunityLot), "Run", typeof(GoToCommunityLotHook), "RunHook", "RunOrig");
            ApplyTripSubclasses();
            // No tuning gate here: Apply runs in Instantiator's static constructor, before the tuning loader sets the [Tunable]
            // fields, so every tuning reads its C# default (docs/124 §17.8). Hooks read their tuning per call; patches that must
            // stay out unless a tuning is on go in ApplyTuned.
            Patch(typeof(Sims3.Gameplay.Actors.SimRoutingComponent).GetNestedType("WalkStyleComparer", BindingFlags.NonPublic), "Compare", typeof(WalkStyleCompareHook), "CompareHook", "CompareOrig");
            Patch(typeof(Sims3.Gameplay.Autonomy.ReactionBroadcaster), "PulseSims", typeof(BroadcasterPulseHook), "PulseHook", "PulseOrig");
            ApplyRouteFixes();
            ApplyRouteBackoff();
            ApplyRouteFailLog();  // docs/226: logging only (routefaillog)
            ApplyDoorGen();       // DoorGen.cs: door-answer generation for ts3native DoorMemoX=3 (switch doorgen, on)
            ApplyOverwatchStuck(false);
            ApplyOverwatchSpread(false);
            ApplyOverwatchNotice(false);
            ApplyOverwatchProbe(false);
            ApplyErrorTrapSpread(false);
            ApplyPortalAdaptorLeak();
            ApplyCasGrid();
            ApplySpatialCache();
            ApplyFewerPlans();
            ApplyCandidateLoops();
            ApplyRefTable();
            ApplyCachedReflection();
            ApplyUiCaches();
            ApplyHudUnsub();
            ApplyMapViewKeep();
            ApplyBuyIconLeak();
            ApplyCasRowLeak();  // back 2026-11-11: the two CAS crashes (TS3W 0x00EBAB5B) were its [ThreadStatic] depth field (EA's Mono has no thread statics), now a plain static
            ApplyCompositorLeak();
            ApplyGenealogyGuard();
            ApplyRelRemoveSafe();
            ApplyFamilyInvGhost();
            ApplySellCursorLeak();
            ApplyRelAlarmGuard();
            ApplyMoodDrift();
            ApplyListenerFixes();
            ApplyEventCost();  // docs/545 §5 diagnostics, switch eventcost off
            ApplyWishListenerFixes();
            ApplyBuyCellRecycle();
            ApplyMoodPending();
            ApplyJoyrideCostume();  // docs/546 §4, on
            ApplyAlarmKillLog();  // docs/546 row 5, log only
            ApplySocialFinish();  // docs/546 §9, on
            ApplySocialReceiveFail();  // docs/546 §9, on
            ApplyPetFirePlan();  // docs/546 §10, off by default
            ApplySplashRoof();  // docs/546 §16, off by default (NPCs only)
            ApplyBurntTileFloor();  // docs/277 §4.1, off by default (EA bug fix, all Sims: outcome as vanilla)
            ApplyShuffleboardBcast();  // docs/546 §17, off by default (leak; exact for every Sim)
            ApplyCookClickListener();  // docs/546 §17, off by default (leak; exact for every Sim)
            ApplyVipRoomRefresh();  // docs/555 §3, off by default (EA throw; exact for every Sim)
            ApplyCourseListener();    // docs/555 §4 (CourseListener.cs)
            ApplyObservatoryMsg();    // docs/555 §6 (ObservatoryMsg.cs)
            ApplyBubblePipeGuard();   // docs/555 §7 (BubblePipeGuard.cs)
            ApplyBuffState();  // docs/557, off by default (per-Sim state on shared Buff objects; exact for one Sim, every Sim)
            ApplyBirthRoute();  // docs/558 §1, off by default (NPC moms only)
            ApplyPutDownPlace();  // docs/558 §2, off by default (NPC parent and child only)
            ApplyStoreWorldEvents();  // docs/557 §4.2, off by default (leak: dead store-object handlers on static World events)
            ApplyBowlingLane();       // docs/559 §3, off by default (BowlingLane.cs)
            ApplyGenieBanish();       // docs/559 §4 (GenieBanish.cs)
            ApplySpaceSimPartner();   // docs/559 §5, §10 (SpaceSimPartner.cs)
            ApplyBandJam();           // docs/559 §6 (BandJam.cs)
            ApplyStatueLine();        // docs/559 §7 (StatueLine.cs)
            ApplyCleanFlag();         // docs/559 §8 (CleanFlag.cs)
            ApplyBirdTvNull();        // docs/559 §9 (BirdTvNull.cs)
            ApplyPhotoBoothFunds();   // docs/561 §2 (PhotoBoothFunds.cs)
            ApplyShellLighting();     // docs/561 §5 (ShellLighting.cs)
            ApplyBonfireSpice();      // docs/561 §6 (BonfireSpice.cs)
            ApplyDjBoothCleanup();    // docs/561 §7 (DjBoothCleanup.cs)
            ApplyVampireHuntGlow();   // docs/561 §7 (VampireHuntGlow.cs)
            ApplyHotTubWoohooFx();    // docs/561 §7 (HotTubWoohooFx.cs)
            ApplyBouncerBribeGuard(); // docs/561 §7 (BouncerBribeGuard.cs)
            ApplyOppDeadListeners();  // docs/562 §1 (OppDeadListeners.cs)
            ApplyDaycareQuit();       // docs/562 §2 (DaycareQuit.cs)
            ApplyPrivateEyeCase();    // docs/562 §5 (PrivateEyeCase.cs)
            ApplyWeddingCancel();    // docs/563 §1 (WeddingCancel.cs)
            ApplyGhostSpawnArgs();    // docs/563 §2 (GhostSpawnArgs.cs)
            ApplyMoveInPartner();    // docs/563 §3 (MoveInPartner.cs)
            ApplyReaperFlow();    // docs/563 §4 (ReaperFlow.cs)
            ApplyServiceLeave();    // docs/564 §1 (ServiceLeave.cs)
            ApplyVisitGreetAlarm();    // docs/565 §1 (VisitGreetAlarm.cs)
            ApplyFieldTripAdult();    // docs/565 §2 (FieldTripAdult.cs)
            ApplyAutonomyFailBlock();    // docs/565 §3 (AutonomyFailBlock.cs)
            ApplyVisitTooLate();    // docs/565 §5 (VisitTooLate.cs)
            ApplySpoonReset();    // docs/565 §6 (SpoonReset.cs)
            ApplyHomeworkHelp();    // docs/565 §7 (HomeworkHelp.cs)
            ApplyGroupTalkStale();    // docs/566 §1 (GroupTalkStale.cs)
            ApplyConvMerge();    // docs/566 §2 (ConvMerge.cs)
            ApplyPortraitPose();    // docs/566 §4 (PortraitPose.cs)
            ApplyPicnicCamera();    // docs/566 §5 (PicnicCamera.cs)
            ApplyUpgradeFailAlarm();    // docs/567 §1 (UpgradeFailAlarm.cs)
            ApplyRepairTimeCache();    // docs/567 §2 (RepairTimeCache.cs)
            ApplyBurglarIdle();    // docs/568 §1 (BurglarIdle.cs)
            ApplyGigWaitLeave();    // docs/568 §2 (GigWaitLeave.cs)
            ApplySculptPose();    // docs/568 §3 (SculptPose.cs)
            ApplyTrainBoost();    // docs/569 §1 (TrainBoost.cs)
            ApplyEatOutsideCount();    // docs/569 §2 (EatOutsideCount.cs)
            ApplyTagUnseen();          // docs/60 §3 (TagUnseen.cs), off by default
            ApplyQueueProximity();   // docs/597 §2 (SleepQueue597.cs)
            ApplySubRouteFail();   // docs/597 §3 (SleepQueue597b.cs)
            ApplyMopUnreachable();   // docs/578 §3b (MopUnreachable.cs)
            ApplyPetPuddleTile();   // docs/578 §3b (MopUnreachable.cs)
            ApplyInitStackTrace();   // docs/594 §1 (SeasonsHunt594.cs)
            ApplyWeatherStoneListener();   // docs/594 §2 (SeasonsHunt594.cs)
            ApplyRainFires();   // docs/594 §3-4 weathermanagerthrow + rainsheltercheap (SeasonsHunt594b.cs)
            ApplyPondClearScan();   // docs/594 §5 (SeasonsHunt594b.cs)
            ApplySoakedStat();   // docs/594 §6 (SeasonsHunt594b.cs)
            ApplyOuterwearAutoLoop();   // docs/596 §1 (SeasonsWeather596.cs)
            ApplyOuterwearRestore();   // docs/596 §2 (SeasonsWeather596.cs)
            ApplyHoverTrainTest();   // docs/598 §5 (HoverTrain598.cs)
            ApplyNewCarTest();   // docs/598 §6 (NewCar598.cs)
            ApplyPetWakeBed();   // docs/543 §4 (PetWakeBed.cs); spoonslavechecks rides spoonreset's SrHook (SpoonReset.cs)
            ApplySlaveWakeReason();   // docs/543 §5 (PetWakeBed.cs)
            ApplyObjectUseLogic();   // docs/575 §2-5 (ObjectUseLogic.cs)
            ApplyInUseLogic();       // docs/576 §2-3 (InUseLogic.cs)
            ApplyAutonomyInUseBlock(); // docs/577 §1 (AutonomyInUseBlock.cs)
            ApplyNpcBusyPartner();   // docs/577 §4 (NpcBusyPartner.cs)
            ApplyButlerPayAlarm();   // docs/593 V1 B13 §16
            ApplySandwichAlarm();   // docs/580 §16 (SandwichAlarm.cs)
            ApplyDreamCleanup();   // docs/580 §18 (DreamCleanup.cs), memory only
            ApplyVehicleFxHelper();   // docs/582 §1 (Batch582Logic.cs)
            ApplyPrankDitchEmpty();   // docs/582 §2
            ApplyServiceAssignExit();   // docs/582 §3
            ApplyServiceShowAlarm();   // docs/582 §4
            ApplyMailDogChaser();   // docs/582 §6
            ApplyLifeguardSurvey();   // docs/582 §7
            ApplyMoonRiseListener();   // docs/582 §8
            ApplyHolidayLights();   // docs/582 §9
            ApplyDoorLockOwner();   // docs/582 B4 §1
            ApplyElevatorKitten();   // docs/582 B4 §2
            ApplyElevatorResetRiders();   // docs/582 B4 §3
            ApplyStarLightReset();   // docs/582 B4 §5
            ApplyMoonDialListener();   // docs/582 B4 §6
            ApplySnubListener();   // docs/582 B4 §7
            ApplyElevatorRiderTimeout();   // docs/582 B4 §3b
            ApplyRoleNullGuard();   // docs/582 B5 §1
            ApplyHomeworkSkillBonus();   // docs/582 B5 §2
            ApplyHorseRanchChallenge();   // docs/582 B5 §3
            ApplyDigSiteEmptyPick();   // docs/582 B5 §4
            ApplyOppChainWorld();   // docs/582 B5 §5
            ApplyTeleportCommunityLot();   // docs/582 B5 §6
            ApplyPhotoSubjectStars();   // docs/582 B5 §7
            ApplyResidentSpawnSlot();   // docs/582 B5 §8
            ApplyTalkOccupationChance();   // docs/582 B5 §9
            ApplyShellDoorSummon();   // docs/582 B5 §10
            ApplyDisposeBase();   // docs/582 B6 §1
            ApplyFreshClothingResume();   // docs/582 B6 §3
            ApplyTwinBellyShape();   // docs/582 B6 §4
            ApplyBoatStaleSwimmer();   // docs/582 B7 §1
            ApplyOppCategoryProtect();   // docs/582 B7 §2
            ApplyNoCommercialCar();   // docs/582 B7 §3
            ApplyScavengerNoLot();   // docs/582 B7 §4
            ApplyPartyLotListener();   // docs/582 B7 §5
            ApplyWeddingListener();   // docs/582 B8 §5
            ApplyInnerBeautyOnce();   // docs/582 B8b §2
            ApplySpRelCount();   // docs/588 B10 §1-2
            ApplySpCloneDict();   // docs/588 B10 §3
            ApplySpLotLookup();   // docs/588 B10 §4
            ApplySpBucketIndex();   // docs/588 B10 §5
            ApplySpCreateMoveIn();   // docs/588 B10 §6
            ApplySpLotDensity();   // docs/588 B10 §7
            ApplySpRoommateJob();   // docs/588 B10 §8
            ApplySpOccultPick();   // docs/588 B10 §9
            ApplySpJobAtWork();   // docs/588 B10 §10
            ApplySpRecheck();   // docs/588 B10 §11
            ApplySpPensionPrompt();   // docs/588 B10 §12
            ApplyHhCleanupDup();   // docs/588 B10 §13
            ApplyVirtualHomeShare();   // docs/588 B10 §14
            ApplyCelebAlarmNull();   // docs/588 B10 §15
            ApplyDisposeVirtualSlot();   // docs/588 B10 §16
            ApplyAgingAlarmCancel();   // docs/588 B10 §17
            ApplyVirtualMoveInDup();   // docs/588 B10 §18
            ApplyPetPoolGhost();   // docs/588 B10 §19
            ApplyEatPostOnce();   // docs/589 B11 §1
            ApplyMotiveMultOnce();   // docs/589 B11 §2
            ApplyFightBroadcastEnd();   // docs/589 B11 §3
            ApplyReactionCopy();   // docs/589 B11 §4
            ApplyCarpoolSchoolSkip();   // docs/589 B11 §5
            ApplyTvSyncAlarm();   // docs/589 B11 §6
            ApplyPhotoFunds();   // docs/589 B11 §7
            ApplyLtrHistoryBits();   // docs/589 B11 §8
            ApplyGoodbyePure();   // docs/589 B11 §9
            ApplyBathCleanup();   // docs/589 B11 §10
            ApplyTvWatchSkill();   // docs/589 B11 §11
            ApplyComputerGameType();   // docs/589 B11 §12
            ApplyMoodTankReset();   // docs/589 B11 §13
            ApplyReactionGrumpy();   // docs/589 B11 §14
            ApplyTrainSimGone();   // docs/589 B11 §15
            ApplyStereoUpgrade();   // docs/589 B11 §16
            ApplyBuffRemoveDelayed();   // docs/589 B11 §17
            ApplyTvDiscListener();   // docs/589 B11 §18
            ApplyFridgeTestNull();   // docs/589 B11 §19
            ApplyCancelPostureDef();   // docs/589 B11 §20
            ApplyFacialIdleWeight();   // docs/589 B11 §21
            ApplyBathSituationFx();   // docs/589 B11 §22
            ApplySocialNoEffect();   // docs/589 B11 §23
            ApplyGuitarGnomeBoost();   // docs/589 B11 §24
            ApplyDistressGate();   // docs/589 B11 §25
            ApplyMoodIdleFlavor();   // docs/589 B11 §26
            ApplyProtestExit();   // docs/590 B2 B12 §1
            ApplyJogTerrain();   // docs/590 B3 B12 §2
            ApplyLifeguardQuit();   // docs/590 B4 B12 §3
            ApplyFrolicExit();   // docs/590 B5 B12 §4
            ApplyShooDef();   // docs/590 B6 B12 §5
            ApplyIglooLookats();   // docs/590 B8 B12 §6
            ApplyGoHereBoatList();   // docs/590 B10 B12 §7
            ApplyBouncerRole();   // docs/590 B12 B12 §8
            ApplyTrickSocials();   // docs/590 B13 B12 §9
            ApplyFearFaintUndo();   // docs/590 B13 B12 §10
            ApplyLotPrune();   // docs/590 B14 B12 §11
            ApplyWerewolfFind();   // docs/590 B15 B12 §12
            ApplyCopyHomeworkFx();   // docs/590 A2 B12 §13
            ApplySpellTargetEnd();   // docs/590 A3 B12 §14
            ApplyFoodTrayGrill();   // docs/590 A6 B12 §15
            ApplyCompositionNull();   // docs/590 A7, 591 C8 B12 §16
            ApplyPetDreamTimer();   // docs/590 A10 B12 §17
            ApplyAwkwardScience();   // docs/591 C1 B12 §18
            ApplyChessDefeated();   // docs/591 C2 B12 §19
            ApplyPrivacyOthers();   // docs/591 C4 B12 §20
            ApplyGigMaster();   // docs/591 C5 B12 §21
            ApplyIntMultiple();   // docs/591 C6, 592 S4 B12 §22
            ApplyDoorPrivacyNull();   // docs/591 C7 B12 §23
            ApplyLeafBurnFail();   // docs/591 R1 B12 §24
            ApplyCatchPreyOdds();   // docs/591 R2 B12 §25
            ApplyPicnicAnts();   // docs/591 R3 B12 §26
            ApplyLoyalDogLay();   // docs/591 R5 B12 §27
            ApplyVisitListeners();   // docs/591 R6 B12 §28
            ApplyScienceFxStop();   // docs/591 R7 B12 §29
            ApplyDaycareLiking();   // docs/591 R9 B12 §30
            ApplyKegBreakage();   // docs/591 R10 B12 §31
            ApplyRomanceCooldown();   // docs/591 R11 B12 §32
            ApplyLightCustom();   // docs/591 R12 B12 §33
            ApplyEatPreyCancel();   // docs/591 R13 B12 §34
            ApplyMeltAlarm();   // docs/591 R16 B12 §35
            ApplyNewspaperDef();   // docs/591 R21 B12 §36
            ApplyLeaveConvNull();   // docs/592 S2 B12 §37
            ApplyVacationAlarms();   // docs/592 S3 B12 §38
            ApplyCleanupVfx();   // docs/592 S5 B12 §39
            ApplyLookAtCopy();   // docs/592 S6 B12 §40
            ApplyLottoTooltip();   // docs/592 S8 B12 §41
            ApplyTraitIconDislike();   // docs/592 S9 B12 §42
            ApplyRoleUnprotect();   // docs/592 S11 B12 §43
            ApplyPutDownPlaced();   // docs/592 S13 B12 §44            ApplyDisplayCaseReset();   // docs/587 EP1 §1
            ApplyNullSituation();   // docs/593 V2 B13 §1
            ApplyLightningWeights();   // docs/593 L1 B13 §2
            ApplyPlantDormantFlip();   // docs/593 G1 B13 §3
            ApplyPlantDormantTime();   // docs/593 G2 B13 §4
            ApplyHarvestExtras();   // docs/593 H2, H5 B13 §5
            ApplyShroomSlots();   // docs/593 H3 B13 §6
            ApplyHarvestRegion();   // docs/593 H4 B13 §7
            ApplyWellStealCooldown();   // docs/593 B2b B13 §8
            ApplyOppFailCharge();   // docs/593 O1 B13 §9
            ApplyTempForecast();   // docs/593 T1 B13 §10
            ApplyMountFishCost();   // docs/593 F1 B13 §11
            ApplyFirefighterCount();   // docs/593 V8 B13 §13
            ApplyRepairmanSkill();   // docs/593 V9 B13 §14
            ApplyRoundUpReturn();   // docs/593 V3 B13 §15
            ApplyPetHarvestVfx();   // docs/593 H1 B13 §17
            ApplySuatGradeBonus();   // docs/587 EP1 §2
            ApplyCatToyPickerRel();   // docs/587 EP1 §17
            ApplyDogSwimSlots();   // docs/587 EP4 §9.1
            ApplyFragmentReconstruct();   // docs/587 EP4 §9.2
            ApplyLabObjectPush();   // docs/587 EP4 §9.3
            ApplyBusinessPlannerRun();   // docs/587 EP4 §9.4
            ApplyFutureDescendantSize0();   // docs/587 EP4 §9.5
            ApplyJungleGymExitQueue();   // docs/587 EP4 §10.1
            ApplyWildHorseLot();   // docs/587 EP4 §10.2
            ApplyHarvesterUnripe();   // docs/587 EP4 §10.3
            ApplyHotTubSeatPosture();   // docs/587 EP4 §10.4
            ApplyTeenPartyBreakUp();   // docs/587 EP4 §10.5
            ApplyDogSwimAroundList();   // docs/587 EP4 §10.6
            ApplyTutorBalloonAlarm();   // docs/587 EP4 §10.7
            ApplyOneShotVfx();   // docs/587 EP5 §11.1
            ApplyEpBatch5b();   // docs/587 EP5 §11.2
            ApplyEpBatch6();   // docs/587 EP6 §12.1
            ApplyStoreBatch1();   // docs/587 store §13
            ApplyMegaphoneRun();   // docs/608 §2 (WalkStyleLeak77.cs)
            ApplyDragonCurseRun();   // docs/608 §3 (WalkStyleLeak77.cs)
            ApplySpeedSkipLevel();   // docs/609 §2-3 (GameSpeed78.cs): speedskiplevel, speedskipnoactor
            ApplySpeedRecalcLock();   // docs/609 §4 (GameSpeed78.cs)
            ApplySpeedLockOwner();   // docs/609 §5 (GameSpeed78.cs)
            ApplyAlarmZeroPeriod();   // docs/609 §6 (GameSpeed78.cs); also carries the alarmtime stamp (HitchCause.cs)
            ApplySpoilBroadcast();   // docs/615 §1 (Scan85.cs)
            ApplyWeatherStoneRelisten();   // docs/615 §2 (Scan85.cs)
            ApplyBirthdayAgeRecord();   // docs/615 §3 (Scan85.cs)
            ApplyHeartBrokenBcast();   // docs/609 §7 (GameSpeed78.cs)
            ApplyRhExitUnseen();   // docs/613 §2 (StuckJobs83.cs)
            ApplyTipsNpcEnd();   // docs/613 §3 (StuckJobs83.cs)
            ApplyBarPutDownWait();   // docs/614 §1 (Scan84.cs)
            ApplyPutDownSurfaceUse();   // docs/614 §2 (Scan84.cs)
            ApplySwipeRestore();   // docs/614 §3 (Scan84.cs)
            ApplyUnicornSpawnFail();   // docs/614 §4 (Scan84.cs)
            ApplyDataDiscPutBack();   // docs/614 §5 (Scan84.cs)
            ApplySpawnerSkip();   // docs/610 §2 (SpawnerMapTag79.cs); also carries the alarmtime spawn timing (HitchCause.cs)
            ApplyMapTagTaskAlive();   // docs/610 §3 (SpawnerMapTag79.cs)
            ApplyLotMgrCallbacks();   // docs/611 §2 (LotCallbacks80.cs)
            ApplyFireplaceHerb();   // docs/611 §3 (LotCallbacks80.cs)
            ApplyRummageBcast();   // docs/611 §4 (LotCallbacks80.cs)
            ApplyStairWaitEnd();   // docs/612 §2 (StairDup82.cs)
            ApplySimDisposeLink();   // docs/612 §3 (StairDup82.cs)
            ApplyAwardKeep();  // docs/546 §11, off by default
            ApplyEventRouteRetry();  // docs/546 §11, docs/548, off by default (ceremonyspot, eventcrowdring, eventrouteretry)
            ApplySimFestKeep();  // docs/546 §11, off by default
            ApplyWeddingArchKeep();  // docs/546 §12, off by default
            ApplyWeddingCleanup();  // WeddingCleanup.cs, default on (EA CleanUp NRE guard)
            ApplyGraduationKeep();  // docs/546 §12, off by default (hsgradkeep, unigradkeep)
            ApplyBoardingRefund();  // docs/546 §12, off by default
            ApplyHiddenRoomNpc();  // docs/552, off by default (no hook: a sweep alarm started at world load)
            ApplyHiddenRoomSpawn();  // docs/552, off by default (hiddenroomspawn)
            ApplyAptLeave();  // docs/553, off by default (household guests: exact, deferred to the real lot exit)
            ApplyPartyFixes();  // docs/554, off by default (partyoutcome, partymotive)
            ApplyComboRabbitHole();  // docs/551, off by default (comboprotest, combovenue, combosituation, combojob, comboprotestlot, comboopp)
            ApplyFilterGridLeak();
            ApplyCasPuckLeak();
            ApplyCasExitSweep();
            ApplySeasonListener();
            ApplyNameCache();
            ApplyScriptHotSpots();
            ApplyVenueMemo();
            ApplyExactMemos();  // lane L4 1115: permamemo, buffremovenoop, catnamecache (ExactMemos.cs), off by default
            ApplyWriterMemos();  // docs/275 Z1 zdemptyout + M1 decaymemo (WriterMemos.cs), off by default
            ApplyDoorMirror();
            ApplyAutShadow();
            ApplyAutRoomMemo();  // docs/97 AM-1 H1-H3/H5 (AutRoomMemo.cs), off by default; after AutShadow (rides on its IsAllowedInRoom swap)
            ApplyAutOffload();
            ApplyReplanDefer();
            ApplyUnseenThrottle();
            ApplyEaSpread();
            ApplySeasonMarkerCache();
            ApplyRouteSources();
            ApplySpawnCap();  // docs/147 §5 D1, off by default (spawncapcount, spawncapprobe)
            ApplyRouteLen();  // docs/246 §6 counters 1/3/4, diagnostic, off (routelen)
            ApplyDisposeSpread();
            ApplyMemAlive();
            ApplyUfFinish();
            ApplySavedStacks();  // docs/242
            ApplyPosturePop();
            ApplySeasonImposter();
            ApplyEaFixes();
            ApplyDeadRefs();
            ApplyEaVacation();
            ApplyCompaction();  // docs/204: hooks only with ts3native [Compaction] Enabled=1 or 2
            ApplyCasTrimKeep();  // docs/539: hooks only with [CasTrim] save hooks in native
            ApplyUnseenOutfitDefer();  // issue #451, off by default (UnseenOutfitDefer.cs)
        }

        // Patches that go in only when a tuning is on (docs/124 §17.8): Apply cannot read tunings (C# defaults there), so these
        // run once the tuning is set: at app startup when the tuning was already parsed (kInstantiator True: every
        // ts3fixes tuning sets it), else at the first world load. Each logs "tuned: <tuning> on: <what> patched|MISSING";
        // queue.ts fails a job on a MISSING line. A target the interpreter already compiled is refused (Patch), never swapped.
        static bool sTunedDone;
        internal static void ApplyTuned(string when)
        {
            if (sTunedDone) return;
            sTunedDone = true;
            AppliedLog().Add("tuned: tuning-gated patches at " + when + (Instantiator.TuningSet ? "" : " (tuning not set: kInstantiator False)"));
            ApplyVenueScore();
            ApplyWakePark();
            ApplyAutLiveB14();
            ApplyHitchCause();
        }

        static void TunedLog(string tuning, bool ok, string what)
        {
            AppliedLog().Add("tuned: " + tuning + " on: " + what + (ok ? " patched" : " MISSING"));
        }

        // Null when target.method exists and is not compiled yet (a Patch would go in), else "Type.method".
        static string NotPatchable(Type target, string method, Type[] args)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo t = target.GetMethod(method, all, null, args, null);
            return t != null && !Compiled(t) ? null : target.Name + "." + method;
        }

        static bool Compiled(MethodInfo t) { return Marshal.ReadInt32(new IntPtr(t.MethodHandle.Value.ToInt32() + 0x14)) != 0; }

        internal static List<object> AppliedLog() { return (List<object>)AppDomain.CurrentDomain.GetData("ts3fixes.applied"); }

        // A patch target from GetNestedType/GetType is null when that type is missing (e.g. a core mod without it)
        static string TypeName(Type t) { return t != null ? t.Name : "<null>"; }

        // Counters, read by test tools: [0-3] go-home runs/shortcuts/no spot/loop guard, [4-6] buildings, [7-18] lot
        // trips (visit, visitlot, gotolot, gotocommunity: runs/shortcuts/no spot each), [21] houseboat placements,
        // [22-23] broadcaster pulses/skipped, [24-26] route backoff, [27-36] route fixes (RouteFixes.cs), [37-39] SafeSpot
        // verdict cache hits/misses/full checks, [40-43] save cleanup (SaveCleanup.cs), [44-45] Overwatch stuck-check
        // plans/yields (OverwatchStuck.cs), [46-51] watchdog idle cases (RouteFixes.cs: idle on an unroutable spot, hidden
        // room, stationary a sim-day, route plans failing, routability checks asked, route-fail Sims tracked), [52] empty life-event lists trimmed, [53] memory snapshots capped (SaveCleanup.cs), [54-55] route
        // adaptor callbacks unsubscribed after DoPortal / stale ones removed by the save cleanup (PortalAdaptorLeak.cs), [56-58] async
        // route plans started/yields/poll cap reached (RouteBackoff.cs, with ts3native [RouteAsync]), [59-61] CAS incremental
        // fills started / hair preset cache hits / preset lookups run (CasGrid.cs), [62-64] lot object query cache hits /
        // misses / clears on object destroy (SpatialCache.cs), [65-67] routability cache hits / plans
        // recorded, stuck checks skipped for busy Sims (FewerPlans.cs), [68-70] save identity tables converted / most entries
        // in one table / table grows (RefTable.cs), [71-73] serializer type caches started / answers from the cache /
        // answers stored (CachedReflection.cs), [74-75] motive bar updates without / with image loads, [76-77] moodlet grid
        // in-place updates / rebuilds (UiCaches.cs), [78-79] Sim name cache hits / misses (NameCache.cs),
        // [80-82] lot detail priority answers from the tick set / tick sets built / verify mismatches,
        // [83-84] cane-walk checks answered early / passed to the game, [85-87] hours-of-operation memo hits / misses / verify mismatches,
        // [90-92] SafeSpot placements answered by a stored FindGoodLocation result / searches run / verify differences (switch safespotreuse),
        // [88-89] tooltip window walks skipped / run (ScriptHotSpots.cs),
        // [93-98] spring wildflower fires / tries / placed / failed / longest ms / save aborts (wildflowerprobe, wildflowertask; ScriptHotSpots.cs, docs/110),
        // [99-101] invcache (ScriptHotSpots.cs, docs/115): dispatches from the kept handler list / lists built / verify mismatches, [102-105] door mirror: door imports seen / lot invalidations / lot tables built /
        // builds refused (doormirror; DoorMirror.cs, docs/105 §10.5), [106-121] [AutShadow] pilot (AutShadow.cs, docs/122): A0..A5,
        // L pairs / equal / Test agree / top-1 agree, skips, warm-in reference runs, requests posted, Begin busy, mod-guard
        // skips, managed exceptions in the driver, [122-126] [AutShadow] mod guards (docs/128 §5): sf skips, G-M4 methods /
        // us / foreign swaps, G-M5 overriding mod types, [127-131] 3 AM stuck checks in our task (stuckcheckasync; FewerPlans.cs,
        // docs/134 §8): queued / run / dropped / most waiting / longest wait in sim-seconds,
        // [132-136] replan defer / spread (ReplanDefer.cs, docs/141 §6): interrupts inserted, eligible calls kept sync, calls
        // held back, calls replayed, replay exceptions,
        // [137-145] candidate-loop caps (CandidateLoops.cs, docs/141 §7): safe-spot calls capped / limit reached, farthest-lot
        // calls / plans / cache answers / unplanned after the cap, indoor-position calls / plans / rooms given up, [146-155] unseen script throttle
        // (UnseenThrottle.cs, docs/144): environment calls classified / unseen / skipped, motive updates classified / unseen /
        // skipped, broadcaster per-object rule unseen pulses run / skipped, unseen motive updates leaving a need <= -95, gate restarts,
        // [156-166] NRaas Overwatch nightly cleanups spread (OverwatchSpread.cs, docs/145),
        // [167-176] NRaas ErrorTrap load-up inventory check spread (ErrorTrapSpread.cs, docs/146).
        // [177-196] EA maintenance alarms spread (EaSpread.cs, docs/147): venue cleanup passes / lots / Sims stepped / ticks /
        // longest tick us / verify objects left to clean / passes stopped, pet pool alarms moved to our task / pets added / ticks /
        // most ticks for one alarm / alarms left to the game (busy), decay passes / Sims queued / pairs stepped / pairs dropped /
        // ticks / longest tick us / passes finished early / decay steps that threw, [197-200] unseen traffic cars: LiteDriver spot asks / unseen car + spot / turned down (stays parked) / seen (UnseenThrottle.cs, docs/143 §8.5), [201-206] reserved, [207] extra pet pool alarm fires dropped (second chain), [208] most decay pairs stepped in one tick, [209] most venue-cleanup units (objects tested, cleaned or repaired, per-lot steps) in one tick, [210] most Overwatch inventory items checked in one tick (OverwatchSpread.cs), [211-212] ErrorTrap inventory check: largest inventory checked whole (items), most items checked in one tick (ErrorTrapSpread.cs), [213-222] other branches (registry), [223] longest Overwatch cleanup type run whole at its slot (us), [224] Overwatch types that took >= 50 ms at their slot (OverwatchSpread.cs), [225-227] unseen motive catch-ups: most in one sim tick, held a tick by the per-tick cap, longest catch-up call (us) (UnseenThrottle.cs, docs/144 §8), [228-237] route sources and redundant-plan cuts (RouteSources.cs, docs/151 §6): plans classified / classifier us / V3 leg-end replans D1 could skip / V4 houseboat leg plans / V5 bouncer at its slot / V7 watchdog checks a successful plan answered / D1 replans skipped / D3 bouncer routes skipped / D5 checks skipped by the memo / D5 Sims left by the cap; per-bucket data in AppDomain ts3fixes.routesrc (int[64]),
        // [238-243] docs/163 sync-plan callers: stuckchecknear checks run / plans / checks where every point failed (FewerPlans.cs), failrepeat failures recorded / repeats answered / ms of the recorded plans those repeats skipped (RouteBackoff.cs).
        // [251-254] docs/163 §7 owner-task own STARTs: drivertaskasync traffic car / ridden vehicle STARTs sent, stuckcheckown STARTs sent, of those started by ts3native (RouteBackoff.cs).
        // [244-245] overwatchchain: Overwatch alarms run as a chain / ticks yielded between types (OverwatchSpread.cs, docs/145 §10).
        // [255-259] overwatchstuckslice: ticks taken in the Stuck Check's Sim walk / Sims walked / longest stretch without a tick (us) /
        // Stuck Check runs / stretch time summed (ms) (OverwatchStuck.cs, docs/145 §12; measured with the switch off too).
        // [270-275] seasonmarkercache (SeasonMarkerCache.cs, docs/74 §11 (a)): answers from the list / marker queries passed (not eligible) / verify checks / verify mismatches / seeds / markers in the list;
        // [276-278] safespotyield (SafeSpot, docs/74 §11 (b)): yields after a FindGoodLocation / placements given up after a yield (Sim seen or gone) / searches not yielded (no yield context or not the Sim's own task); [279] spare.
        // [280-289] disposespread (DisposeSpread.cs, docs/74 §12): destroys queued / destroyed by our task / destroyed at once by a flush
        // (save, travel, build/buy) / game path taken (unsafe or not live) / most waiting / longest slice us / already destroyed at
        // their turn / queue full (game path) / dropped (world unload, Destroy threw) / longest wait in ticks.
        // [290-292] overwatchstucknotice (OverwatchNotice.cs, docs/145 §13): Overwatch stuck notices held / summaries shown / Sims named.
        // [293-294] owprobeplans (OverwatchProbe.cs, docs/163 §9): EA stuck-check plans hidden from Overwatch's Stuck Check V2 / stuck-check runs flagged.
        // [295-296] preplancb (RouteBackoff.cs, docs/185 §5): AboutToPlanCallback pre-calls before a START / door lists restored after the original plan.
        // [297-301] stuck-check outcomes (FewerPlans.cs StuckCheckRun, docs/163 §10, counted with any switch): [297] stuckcheckcap PROBE marks
        // ts3native accepted, then per check: [298] no plan (moved / early return), [299] a probe succeeded (not stuck), [300] every probe
        // failed before the last round (re-armed), [301] every probe failed in the last round (reset).
        // [302] overwatchstuckslice: longest stretch seen at the IsSelectable hook (us) (OverwatchStuck.cs, docs/145 §12).
        // [303-312] unseen-Sim fallback (UnseenFallback.cs, docs/202): stuck checks deferred / run (seen), plans marked / cut,
        // teleports / refused, no-retry answers, plans seen, marks refused, longest marked plan (ms).
        // [313-318] unseenfbpre (UnseenFallback.cs UfPre, docs/202 §3.5): teleports after a walk-time hold / eligible but no reachability
        // proof (planned normally) / proven unreachable (failed at once) / holds given up (seen, moved, interrupted: planned normally) /
        // holds started / longest hold (sim-minutes); [319-322] unseenfbarrive / unseenfbleave (§3.6): arrive hand-offs / arrive no hand-off
        // or proof, leave teleports / leave no hand-off or proof; [323-325] other branches; [326-327] D paths skipped for a missing
        // verdict (normal plan instead, no sim-thread check) / D cells filled from those normal plans (UfFillNote).
        // [328-340] D / D-arrive / D-leave reject gates (UfPre / UfArrive / UfLeave, docs/202 §3.5.1, shared by the three switches,
        // counted for unseen Sims (D, D-arrive) and seen-but-movable ones (D-leave)): no goal or not the Sim's own task / too short /
        // Sim rules / not a standing unseen townie / car or boat / goal on the Sim's lot / goal lot refused / goal indoors or off the
        // ground / caller not a follow helper (frames in AppDomain ts3fixes.unseenfb.callers) / no yielding context / colour gives no
        // same-component proof / goal not routable or no route profile / D-leave goal in view.
        // [341-347] lot trips with D's hold (docs/202 §3.6.4, unseenfbtrip / unseenfbtriphold): mod subclass trip runs / shortcuts /
        // no safe spot (ShortcutToLot idx 341), trips placed after a walk-time hold (any kind) / holds given up or refused (seen at the
        // start, Sim or spot seen, moved or interrupted: the game's trip runs) / refused: start and lot proven in different flood
        // colours / refused: not the Sim's own task or no yielding context.
        // [348-353] loop detector / breaker (UnseenFallbackLoop.cs, docs/202 §5.7); [354-362] other branches (registry).
        // [363-370] unseenfbfinish (UnseenFallbackFinish.cs, docs/202 §3.4.2): holds claimed / walks finished at the route end /
        // not placed at the end (seen or gone at placement: replanned from where it stood) / holds given up (seen, route changed or
        // ended, not routable: the Sim walks on) / ids flagged finishable / not flagged: route end seen or a household Sim on its lot /
        // not flagged: route shape (not one open walking subpath, slaves, follow-up actions, Sim rules) / longest hold (sim-minutes).
        // [371] UfHeld posture chains that did not end within 16 postures (a loop: held, issue #89, UnseenFallback.cs), [372] posturepopcycle:
        // pops sent down the setter's unwind instead of Compatible (PosturePop.cs, issue #89).
        // [373-379] seasonimposter (SeasonImposter.cs, issue #82): lots queued / warm rebuilds in play / cold rebuilds in play
        // (seasonimpostercold) / rebuilt at a save or travel flush / dropped (lot gone, world unload, threw) / longest rebuild ms /
        // most waiting. [380-390] EA fixes (EaFixes.cs, issues #86-#90): preferredservice extra entries removed / stale entries
        // re-picked / swept at save, socialworkeronlot visits deferred / dropped, eanullguards bribe / topic / motives / carry,
        // showstagethrowables items destroyed, firepitchair chairs destroyed. [391-392] ageupkeepoutfits (EaFixes.cs, issue #100):
        // outfit swaps skipped after a failed build / descriptions with no Naked and no Everyday outfit at the last count.
        // [393-410] dead references (DeadRefs.cs, issue #91): deadrefguards Get refusals / aging states refused; deadrefsweep
        // relationship keys / relationship entries / aging states / alarm owners / react times / coworker-boss-chess / topics /
        // fishers / Sim fields / passenger-horseshoe entries removed, passes finished, slices, longest slice us, total ms,
        // longest save pass us, refused or stopped.
        // [411-422] save cleanup spread pass + savecleanupaggressive (SaveCleanupSpread.cs, docs/14 §8): passes finished, slices,
        // longest slice us, total ms, refused or stopped, outside-household pictures, quiet friend pairs, history entries,
        // dead outfits, dead Sims disposed, cleanse skipped for Overwatch, dead passport lists seen. [423-426] played households
        // (PlayedHouseholds.cs, docs/14 §8.6): set size, ids added, lighter-tier pictures removed, aggressive actions skipped.
        // [427-438] Compaction (Compaction.cs, docs/204): saves seen, compactions started, offers, accepted, declined, refused by state,
        // skipped / failed, queue entries recorded, re-pushed, not pushed, not restorable, pushes that threw; [439] restores finished.
        // [440-447] venue memo (VenueMemo.cs, docs/122 §14.24): GetTuning memo hits, misses, verify mismatches, clears; venue
        // scores by the hook's steps, passed to the game's method, verify mismatches, verify checks.
        // [466-480] free vacation (EaVacation.cs, issue #97, docs/210): vacexport adults exported / dream managers from the export,
        // vacstoreguard slots re-added / stores that threw / bring-backs scheduled at load / dead-pickup trips ended at load,
        // vacreturnskip adults skipped / returns ended, vacteendelete deletions deferred / teens rejoined / returns from the check,
        // vacexitreturn adults brought back at exit, vaclistenernull null states skipped, vacimportfix ids refreshed / expiry skipped.
        // [448-465] offload Mode R (AutOffload.cs, docs/208): env / bcast items, A0-A5, exceptions, dry checks. [481-490] offload
        // apply (docs/208 §8): env ready / applied / no answer / stale / applied = live, bcast ready / applied / no answer / stale / same order.
        // [534-537] autofftick (AutOffload.cs, docs/206 §15): items, A0, A2, unfinished.
        // [538-541] go-live cost guard (AutLive.cs, docs/124 §17.10): windows closed, skip windows, searches skipped, last ratio
        // (cost per mille of the scoring saved; -1 = no reference yet).
        // [550-565] autlivelate (AutLive.cs, docs/217 L1): later slices checked, refused by age/epoch/state/lot/head/actor/other,
        // late objects consumed / stale, consumed by age 1/2/3+ ticks, B14 us consumed / game late / game invalid.
        // [566-574] G8 (AutLive.cs AutPushHook, docs/124 §8): pushes, no-pick searches, MakeSimGoHome fallbacks, Sim-hours x100,
        // request-to-push latency 0/1/2/3/4+ ticks (Mode 3 searches).
        // [575-601] go-live per-search feature mask (AutLive.cs, AutLiveCore.Feat bits late, prelearn, tierreqlots, rain,
        // desirefn, satb, guard, cold, partial): 575-583 searches per bit, 584-592 mismatches (dry unequal or replay mismatch),
        // 593-601 give-ups (live-drop fallbacks, dry-nocmp).
        // [491-533] unseen wake-ups (WakePark.cs, docs/211: wakeprobe / wakepark / wakepoll): see the header of WakePark.cs.
        // [602-604] [AutShadow] G-M6 (AutShadow.cs ClosureSweep, PR #112): closure methods IL-hashed, hashes not from a checked
        // build (owners whose closure reaches it skipped), hashing us. [605] G-M6 guard faults (> 3 mismatches on checked-build MVIDs: IL check ignored). [606] G-M6 owners (definitions + implementors) skipped this load.
        // [641-646] unseenroute (UnseenFallback.cs UfPre relaxed / UfSnap, docs/202 §3.9): relaxed acts (placed at the goal after
        // the hold), acts without a verdict, acts with the colour unknown, holds shortened by the fast-leg estimate, snap-backs placed
        // mid-trip (exact plan from there), snap-backs that planned from where the Sim stood.
        // [647-658] unseenroute2 (UnseenFallback.cs, docs/202 §3.9.5-§3.9.7): acts of plans made in another task, plans refused
        // inside another plan's callback, group acts (members placed with the leader), groups refused (a member seen, held or
        // not human; or a member's own plan), child acts, B passes (street / public lot, same colour), C learn plans (real plan,
        // result cached), C cached hits, D checks (a placed Sim seen again), D repairs (moved to the lot entry), ghost starts
        // (ts3native [UnseenGhost] took the id, footprint removed), ghost exits (off the native set, footprint back).
        // [659-660] unseen-direct pacing (AutLive.cs PaceCharge, docs/222 §8): us charged to B13's yield timer, yields brought forward.
        // [661-669] G8 by actor class (AutLive.cs G8Note, docs/222 §8): unseen searches / pushes / no-pick / GoHome (661-664), seen
        // (665-668), unseen Sim-hours x100 (669).
        // [670-679] autunseencmp Dry per-definition compare (AutLive.cs DefCmpNote, docs/222 §9): searches, pairs, worker-fail /
        // game-pass, worker-pass / game-fail, both pass, both fail, late-slice pairs, late-slice mismatches, objects skipped, snapped slices.
        // [680-682] outfitwaitpoll (OutfitWait.cs, issue #115): outfit waits gone stale, minute polls, stale waits that ended.
        // [933] outfitwaitpoll (OutfitWait.cs, issue #40): stale waits ended because the helper's Sim is destroyed.
        // [2006-2007] ageupswap (AgeUpSwap.cs, docs/503): age-up swaps EA refused and moved to Everyday, age-up tasks released with no swap.
        // [2003-2005] outfitwaitpoll stale-wait causes (OutfitWait.cs, issue #115, docs/224 §5): own swap pending, replaced, nothing pending.
        // [2008-2009] outfitwaitpoll (OutfitWait.cs, docs/224 §5): pending-outfit resets while a helper waits: instant switches, delayed swaps by another path.
        // [2124-2127] outfitwaitend (OutfitWait.cs, docs/583 §1): off-screen NPC outfit waits ended with EA's instant switch / would end (switch off) / kept past the mark / errors.
        // [683-684] bakesaleseller (BakeSale.cs, issue #102): dead-seller reads, orphaned NPC tables given a new clean-up alarm.
        // [685-686] docs/222 §10: unseen-direct pairs with a RabbitHole target sent to the game's C1 (autunseenrh; in Dry the same pairs
        // skipped by autunseencmp), worker-pass / game-fail compare pairs whose worker score is 0 (BestScores drops both).
        // [2002] uniformbackup (UniformBackup.cs, issue #101, docs/502): stale career uniform backups cleared.
        // [2014] nullservice (NullService.cs, docs/504): null elements dropped from Services.AllServices.
        // [2015] rabbitholedoor (RabbitHoleDoor.cs, docs/505): rabbit hole door requests that threw SacsErrorException, skipped.
        // [2026-2028] photosession (PhotoSession.cs, docs/508): expected Sims dropped, sessions ended by the hook, sessions ended at world load.
        // [2029] eggspot (EggSpot.cs, docs/508): egg positions refused because they are off the hunt lot.
        // [2030] graduationtable (GraduationTable.cs, docs/508): graduation waiting-loop ticks that found no table.
        // [2035-2036] jazzparam (JazzParam.cs, issue #166 build 21, docs/511): UpdateJazzParameter NullReferenceExceptions with no posture, caught.
        // [778-784] unseenprobe/unseenpark (UnseenThrottle.cs, docs/245): low-LOD refreshes run, us in those runs, gap histogram.
        // [818-823] unseenpark (UnseenThrottle.cs, docs/243 §9): held, snap-back, exempt, searches unseen / seen, arrivals; [824] most snap releases in one tick (the stagger working), [825] polls of a seen held Sim deferred to its slot.
        // [836-849] unseenboat (UnseenBoat.cs, docs/248): get-in replans seen by UbCountReplan (switch on or off), of those seen, unseen; shortcuts, no spot, refused route, refused Sim, houseboat teleports, owned boat, candidates (transport or houseboat trips), seen or ended during the gate or wait, refused goal, longest wait (sim-minutes), colours unknown.
        // [948-949] unseenboat no-refusal split (UnseenBoat.cs UbBookReplan, docs/247 §12.11): boat replans booked with no sUbLastWhy entry (the Sim never reached UbTrip, or the 512 clear) / their ms.
        // [854-861] scopegap (UnseenFallback.cs UfDriverless, UfSlowPlanLog; counters only): driverless routes (who == null) by Vehicle+IOwnableVehicle / Vehicle not ownable / household or selectable Sim in Occupants / not a Vehicle; plans of Sims in 80 m viewer range only (viewer bit, no on-screen, no reach), of those >= 250 ms, their total ms; driverless total.
        // [850-853] unseenboatstart (docs/248 §10): Sims leaving a houseboat: candidates, shortcuts, no spot, left dirty (must stay 0). Subsets of [845], [839], [840].
        // [2033-2034] buffupdate (BuffUpdate.cs, issue #166 build 20, docs/510): out-of-sync exceptions from UpdateBuffs, caught.
        // [2039-2040] carnoroute (CarNoRoute.cs, issue #166 build 33, docs/513): car drives with no car routing component, returned false.
        // [2031-2032] convturn (ConvTurn.cs, issue #166 build 19, docs/509): stuck conversation turns seen by a scoring call, turns taken.
        // [2041-2042] npcdriverpool (NpcDriverPool.cs, issue #166 build 37, docs/514): bad pooled driver descriptions EA would hand out, dropped.
        // [687-689] routefaillog (RouteFailLog.cs, docs/226): routefail lines, showoff lines, lines dropped by the cap.
        // [760-762] savedstacks (SavedStacks.cs, docs/242): saved methods remapped to stubs, left dropped, stubs with no checksum.
        // [690-743] unseencost (UnseenCost.cs, docs/227): unseen-check calls / top-level / timed / us per site, interop calls,
        // Sim asks vs distinct Sims per tick, the probe's own calibrated cost. [750-757] unseencost sites utfetch + utlookup (docs/227 §9).
        // [2043-2044] bookcarry (BookCarry.cs, docs/515): carry state machines Book.StopUsingBook dropped without an exit, ended.
        // [2037-2038] ageupabort (AgeUpAbort.cs, issue #166 build 32, docs/512): deferred age-up builds aborted during instantiation, caught.
        // [2045] deadrefsweep phase 9 (DeadRefs.cs, issue #166 build 42, docs/516): dartboard entries that were gone (players quit, current player / cheating Sim cleared).
        // [2046] deadrefsweep phase 10 (DeadRefs.cs, issue #166 build 40, docs/517): object use, reference and routing-reference list entries that were gone.
        // [2047] deadrefsweep phase 11 (DeadRefs.cs, issue #166 build 43, docs/519): object owner components whose owner or thief Sim was gone (relinked or dropped).
        // [2048] deadrefsweep phase 12 (DeadRefs.cs, issue #166 build 41, docs/522): ObjectDisposed handlers of relationships nobody holds, removed.
        // [2049] deadrefsweep phase 13 (DeadRefs.cs, issue #166, docs/523): celebrity waiting-to-react entries that were gone, removed.
        // [994] deadrefcooking (DeadRefs.cs phase 14, docs/205 §4): CookingProcess ObjectDisposed handlers removed. [995-996] deadrefconvo (phase 15, docs/517 §4): gone conversation members removed / conversation visits skipped for a household member.
        // [997] takeariskguard (TakeARiskGuard.cs, docs/529 §4): Take a Risk liking updates skipped (null relationship, EA would throw).
        // [1014-1017] permamemo (ExactMemos.cs, docs/245 §5 H): hits / misses / verify mismatches / table clears. [1018-1019] buffremovenoop (docs/95 §5.5): early returns / calls passed on.
        // [1060-1063] decaymemo (WriterMemos.cs, docs/275 M1): hits / misses / verify mismatches / buff adds seen. [1064-1068] zdemptyout (docs/275 Z1): HasStereo calls / of them empty table / UpdateTimeOut calls / of them empty table / verify mismatches.
        // [1020-1023] catnamecache (ExactMemos.cs, docs/96 B2): hits / misses / verify mismatches / clears. [1024-1037] AM-1 (AutRoomMemo.cs, docs/97 §7): pairs, segments, room memo hits / misses / verify mismatches, calls outside a pair, floods inside a pair, memo drops inside a pair, segment time buckets <1/1-2/2-5/5-10/>=10 ms, max segment us.
        // [2050] rewardgiver (RewardGiver.cs, docs/525): owner components SetRewardGiver added in place of EA's NullReferenceException.
        // [2054] leavejobdispose (LeaveJobDispose.cs, docs/529 §2): deferred job leaves done at once for a description being disposed at work.
        // [2053] unused (a bedpartner fix was dropped: simler90's core already guards the partner in BedMultiPart.ClaimOwnership, docs/529 §1).
        // [2051-2052] datingprofile (DatingProfile.cs, AutShadow.cs MetPeek, docs/528): SimHasProfile answers turned false (a record with no profile) / DidSimsMeetOnline calls answered without making a record.
        // [2055-2057] deadrefsweep object walks (DeadRefs.cs, docs/205 §2.3a): GameObject walks shared by phases 10-12, ms in finished
        // walks, walks asked by kSimDestroyed / kSimDescriptionDisposed (the rest are the daily walk).
        // [2058] invnotyet (InvNotYet.cs, issue #166 build 13, docs/531): objects TryAddObjectToInventory destroyed and refused for a Sim with no inventory yet.
        // [2059-2060] metricskillmissing (MetricSkillMissing.cs, issue #166 build 94, docs/532): skill metric titles / descriptions given an empty skill name, values answered Map0 (neutral), for a skill with no static skill.
        // [2061] fireworksfizzle (FireworksFizzle.cs, issue #166 build 23, docs/533): fireworks watchers finished on a fizzle without EA's LaunchFailure state request.
        // [2062-2064] petpoolorphan (PetPoolOrphan.cs, issue #166 build 52, docs/534): pets dropped from a pool with no household and no family links disposed / left (a Sim in the world) / Dispose threw.
        // [2065] reltablesapart (RelTablesApart.cs, issue #166 build 14, docs/535): relationship pairs whose two tables had come apart, rejoined with the other side's relationship.
        // [2067-2068] errortrapsims (ErrorTrapSims.cs): dead Sim objects dropped from NRaas ErrorTrap's CheckSim.mInited, keys left after the last pass. [2069-2071] unused.
        // [2072] celebritydeadsim (DeadSimHolders.cs): CelebrityManagers whose event listeners held a destroyed Sim, unregistered.
        // [2073-2074] seasonsdeadsim (DeadSimHolders.cs): EA Motives kSeasonTransition listeners removed, whose Sim was gone / whose Motives was replaced (RecreateAllMotives).
        // [2075] ghostreactdeadsim (DeadSimHolders.cs): destroyed Sim objects removed from live Sims' SimsReactedToGhost (EA ghost reaction list).
        // [2076-2077] casuihandlers (CasUiHandlers.cs): closed CAS windows' handlers removed from DownloadDashboard, OptionsModel and CASLogic events / CASController duplicate handlers removed.
        // [2078] errortraplookup (ErrorTrapSims.cs): NRaas ErrorTrap ObjectLookup.sLookup cache entries dropped (its Clear() forgets that table).
        // [2079] dreamnodedeadsim (DeadSimHolders.cs): EA dream nodes' owner-less trigger listeners removed whose node's Sim object is gone.
        // [2080] overwatchdeadsim (DeadSimHolders.cs): NRaas Overwatch stuck-check entries removed whose last interaction's Sim object is gone.
        // [934-935] dreamnodedeadsim / overwatchdeadsim (DeadSimHolders.cs): listeners / entries kept by the household rule (owner household, selected or unknown scope).
        // [945-947] boatgetoutdiag (BoatGetOutDiag.cs, docs/257 §7): Boat.GetOut calls watched / SittingInBoat replaced by another task during the fade / NullReferenceException out of a watched GetOut (rethrown).
        // [953-954] boatgetoutguard (BoatGetOutDiag.cs, docs/257 §9): stale-posture NullReferenceExceptions out of Boat.GetOut swallowed for NPCs (Sim reset) / kept by the household rule (household, selected or unknown scope; EA's error stays).
        // [937] boatparkgone (BoatParkGone.cs, docs/257): BoatOwnable.Park calls skipped because the boat was already destroyed (no RoutingComponent; EA would throw).
        // [936] outfitwaitsaved (OutfitWaitSaved.cs): saved makeover tasks kept by the household rule.
        // [2081] errortrapctl (ErrorTrapSims.cs): NRaas ErrorTrap dereference controllers' item lists dropped after the world-load scan (they kept the whole graph until the next load).
        // [2083] outfitwaitsaved (OutfitWaitSaved.cs): MagicMirror MagicalMakeover alarm tasks resumed from the save inside EA's Wait (WaitOrig, never seen by the outfit-wait hook), actor gone, destroyed.
        // [2084-2087] simrefsdeadsim (SimRefsDeadSim.cs): live Sims' references to gone Sim objects dropped: SocialComponent.mLastTalkingTo cleared / SimRoutingComponent.mPusherSim cleared / Celebrity.mWaitingToReact entries removed / Occult*.mOwningSim reset to the current Sim or null.
        // [2088-2091] deadsimcount (DeadSimCount.cs, read-only): destroyed Sim objects still in memory now / of them destroyed 6+ sim hours ago / collected since the session began / Sim objects tracked.
        // [2200-2216] vehicle leaks (VehicleLeaks.cs, docs/544): 2200-2204 vehparkreserve dead reservations seen at a pick / cleared at a pick / seen hourly / cleared hourly / kept by the household rule;
        // 2205-2208 vehgetinleak get-ins that left their vehicle loose / throwaway released / NPC vehicle to inventory / kept; 2209-2211 vehseatdestroy seated Sims destroyed with the vehicle still there a tick later / dead refs cleared / released;
        // 2212-2213 vehfrankentaxi unused intro taxis seen / released; 2214-2216 vehprotestcar cop cars tracked / left with no driver or drive-away after a sim-hour / destroyed. "Seen" counters count with the switch off too.
        // [2235-2238] relremovesafe (RelRemoveSafe.cs): Sim removals where EA's relationship removal would throw / one-sided entries for the removed Sim / removals seen / errors.
        // [2290-2292] seasonlistener (SeasonListenerLeak.cs): stale / duplicate Motives season listeners (also off) / removed / errors or skipped.
        // [2287-2289] casfiltergrid (CasExitSweep.cs): CAS filter grids at CASPuck.Unload (also off) / restored / errors.
        // [2384-2386] splashroof (SplashRoof.cs, docs/546 §16): rain puddles on roofs dropped from NPC splash runs / NPC runs that dropped one / errors.
        // [2409-2411] shuffleboardbcast (ShuffleboardBroadcast.cs, docs/546 §17): broadcasters replaced by Shuffleboard+Play.TriggerReactions (also off) / disposed after Cleanup / errors.
        // [2417-2419] viproomrefresh (VipRoomRefresh.cs, docs/555 §3): RefreshVIPRooms calls where EA's foreach would throw (also off) / empty VIP rooms removed over a copy / errors.
        // [2420-2423] courselistener (SmallEaFixes555.cs, docs/555 §4): course listeners removed at leave / schedule rebuild / moved with the course / stale moves seen (also off) / errors.
        // [2424-2425] observatorymsg (SmallEaFixes555.cs, docs/555 §6): Simulon V stardust-only notices corrected (also off) / errors. [2426-2427] bubblepipeguard: failed-pipe exits / errors.
        // [2428-2434] buffstate (BuffSharedState.cs, docs/557): Downtrodden removals that would leave the Sim's alarm (also off) / Downtrodden alarms removed / curse removals hitting another Sim's listener (also off) / curse listeners fixed / Fertility Treatment traits removed / hypnosis+queasy handlers replaced / errors.
        // [2435-2437] storeworldevents (StoreWorldEvents.cs, docs/557 §4.2): dead object handlers on World quit / placed-in-lot and ObjectGroupSaving (also off) / removed / errors.
        // [2448-2452] bowlinglane (BowlingLane.cs, docs/559 §3): cancelled start waits released / ghost bowlers dropped / route-fail exits / join restarts applied to other bowlers / errors.
        // [2458-2460] bandjam (BandJam.cs, docs/559 §6): sweet-move requests refused for a non-playing jammer (also off) / empty-jam sounds stopped / errors. [2461-2462] statueline (§7): statue-line entries fixed (wrong dequeues also off) / errors.
        // [2926-2928] butlerpayalarm (Batch593Logic.cs, B13 §16, docs/593 V1): alarms removed / left (switch off) / errors.
        // [2463-2464] cleanflag (§8): BeingCleaned left set by an already-cleaned exit (also off; cleared when on) / errors. [2465-2466] birdtvnull (§9): TVs on with no channel skipped / errors. [2467-2468] spacesimpartner ride ends (§10, always): partner already gone / still riding.
        // [2477-2478] gohomewalk (docs/560 §2): walk-style requests GoHome.Run left set, released / errors (hook in GoHomeHook).
        // [2479-2480] photoboothfunds (PhotoBoothFunds.cs, docs/561 §2): Sims left faded out inside a photo booth taken out / errors.
        // [2481-2482] shelllighting (ShellLighting.cs, docs/561 §5): shells set to the current lighting at startup / errors.
        // [2483-2485] bonfirespice (BonfireSpice.cs, docs/561 §6): herbs returned or destroyed / overlapping spice events fixed / errors.
        // [2486-2488] djcleanup (DjBoothCleanup.cs, docs/561 §7): other Sims' DJ sets kept / other patrons' requests kept / errors.
        // [2489-2490] vampirehuntglow (VampireHuntGlow.cs, docs/561 §7): heat-vision glows cleared on hunt candidates that left the lot / errors.
        // [2491-2492] hottubwoohoofx (HotTubWoohooFx.cs, docs/561 §7): woohoo effects stopped after a cut-off woohoo / errors.
        // [2493-2494] bribeguard (BouncerBribeGuard.cs, docs/561 §7): bouncer bribe charges guarded after the nightly rope cleanup / errors.
        // [2495-2496] oppdeadlisteners (OppDeadListeners.cs, docs/562 §1): ended opportunities' Sim died / disposed / instantiated listeners removed / errors.
        // [2497-2498] daycarequit (DaycareQuit.cs, docs/562 §2): daycare child managers cleaned after a quit outside work hours / errors.
        // [2499-2504] privateeyecase (PrivateEyeCase.cs, docs/562 §5): mail no-case / door fallback / garbage dead-case exits / stale snoop alarms ignored / break-in locks restored / errors / stakeouts fixed.
        // [2505-2507] weddingcancel (WeddingCancel.cs, docs/563 §1): end alarms armed after the impatience cancel / guest insults set on the jilter relationship / errors.
        // [2508-2509] ghostspawnargs (GhostSpawnArgs.cs, docs/563 §2): angry ghost spawns forwarded / spawns forwarded with another lot.
        // [2510-2512] moveinpartner (MoveInPartner.cs, docs/563 §3): story move-ins swapped to the partner / skipped (no room either way) / errors; [2516] teen move skipped (docs/563 §5).
        // [2513-2515] reaperflow (ReaperFlow.cs, docs/563 §4): reaper soda pushes given a failure path / Challenge Death to Chess hidden with no grave / errors.
        // [2517-2521] serviceleave (ServiceLeave.cs, docs/564 §1): service timeouts re-armed after a worker reset (wait/leave, work limit) / repo man left with no mailbox / no delay found / errors.
        // [2522-2523] serviceleave (docs/564 §2): babysitter/butler periodic checks re-armed / repo-man no-op states left.
        // [2524-2526] visitgreetalarm (VisitGreetAlarm.cs, docs/565 §1): re-armed greeting wait alarms dropped / stale wait timeouts ignored / errors.
        // [2527-2529, 2533] fieldtripadult (FieldTripAdult.cs, docs/565 §2): trips ended with no possible chaperone (hang) / unfit chaperones skipped / deleted-chaperone or exited-trip calls / errors.
        // [2530-2532] autonomyfailblock (AutonomyFailBlock.cs, docs/565 §3): autonomous posture failures blocked / left alone / errors.
        // [2534-2535] visittoolate (VisitTooLate.cs, docs/565 §5): NPC guests sent home when outside and too late / errors.
        // [2536-2537] spoonreset (SpoonReset.cs, docs/565 §6): spoon pairs released after the partner was gone / errors.
        // [2538-2539] homeworkhelp (HomeworkHelp.cs, docs/565 §7): helpers stopped with no jig or no child / errors.
        // [2540] fieldtripadult (docs/565 §2d): carpool home with no selected Sim / no home handled.
        // [2541-2543] grouptalkstale (GroupTalkStale.cs, docs/566 §1): departed group-talk members dropped / Sims moved out of an old group on re-register / errors.
        // [2544-2545] convmerge (ConvMerge.cs, docs/566 §2): merged-away conversation discourage regions cleaned / errors.
        // [2546-2547] portraitpose (PortraitPose.cs, docs/566 §4): portrait subjects' poses cancelled after the painter failed / errors.
        // [2548-2549] picniccamera (PicnicCamera.cs, docs/566 §5): picnic Serve map-view callbacks removed / errors.
        // [2550-2552] upgradefailalarm (UpgradeFailAlarm.cs, docs/567 §1): pending failed-upgrade cancel alarms removed at booking / at restart / errors.
        // [2553-2555] repairtimecache (RepairTimeCache.cs, docs/567 §2): stale repair times dropped / own time while another Sim repairs / errors.
        // [2556-2558] burglaridle (BurglarIdle.cs, docs/568 §1): burglar leaves with no household / after a fire reaction that did not leave / errors.
        // [2559-2561] gigwaitleave (GigWaitLeave.cs, docs/568 §2): Stylist / Singer gig wait pulses stopped after their own timeout leave / errors.
        // [2562-2563] sculptpose (SculptPose.cs, docs/568 §3): Sculpt Sim subject poses cancelled after the sculptor failed / errors.
        // [2564-2565] free (released).
        // [2704-2707] tagunseen (TagUnseen.cs, docs/60 §3): Tag laps checked / laps run without a route / errors or members not found / a player seen (EA's lap).
        // [2566-2567] trainboost (TrainBoost.cs, docs/569 §1): Train Sim boosts undone after an unwound session / errors.
        // [2568-2569] eatoutsidecount (EatOutsideCount.cs, docs/569 §2): Bistro eat-outside double decrements undone / errors.
        // [2570-2588] unseenoutfitdefer (UnseenOutfitDefer.cs, issue #451, docs/586): 2570 instant sets deferred, 2571 of those replacing a waiting one, 2572 applied: Sim seen, 2573 applied: selected / not a townie now, 2574 applied before a delayed set or model, 2575 applied: flush (switch off, not live, travel, save), 2576 dropped: Sim gone, 2577 dropped: EA's own set ran, 2578 dropped: model set, 2579 waiting now, 2580 most waiting, 2581 not deferred: first set after instantiate, 2582 not deferred: delayed swap open, 2583 not deferred: category or not a Sim outfit path, 2584 apply threw, 2585 not deferred: within the 40 m margin, 2586 applied: entered the margin, 2587 worst seen-to-apply ms (an upper bound), 2588 seen applies later than 250 ms.
        // [2702-2703] spotcheck (docs/560 §7): GoHome spot in view -> walk / lot-trip spot in view -> game's own trip.
        // [2700-2701] safespotpool (SafeSpot, docs/74 §13): placement answers in a pool or under the ground skipped / cached start spots skipped.
        // [2128-2131] initstacktrace (SeasonsHunt594.cs, docs/594 §1): Init walks skipped / OnAddedToQueue walks skipped / walks run (switch off) / errors. [2132-2134] weatherstonelistener (docs/594 §2): listeners removed at Dispose / left (switch off) / errors.
        // [2135-2137] weathermanagerthrow (SeasonsHunt594b.cs, docs/594 §3): null-dictionary lookups skipped / hit with the switch off (EA throws) / errors. [2138-2141] rainsheltercheap (§4): shelter tests skipped / run / passes / errors. [2142-2145] pondclearscan (§5): scans skipped (world pond in use) / skipped (remembered pond in use) / run / errors. [2146-2148] soakedstat (§6): rain re-adds not counted / counted (switch off) / errors.
        // [2149-2153] outerwearautoloop (SeasonsWeather596.cs, docs/596 §1): into / out / toddler autonomous tests answered no / would loop with the switch off / errors. [2154-2156] outerwearrestore (§2): outfit to return to stored / not stored (switch off) / errors.
        // [2157-2162] hovertraintest (HoverTrain598.cs, docs/598 §5): NPCUseHoverTrain tests answered from the station memo / tests that ran EA's query (switch off: every gated test) / memo forgets / check mismatches (must be 0) / Run throws avoided / errors.
        // [2747-2752] newcartest (NewCar598.cs, docs/598 §6): TestHasNewCar / TestHasExpensiveNewCar answered from the new-car memo / tests that ran EA's (switch off: every call) / memo forgets / check mismatches (must be 0) / errors / ImportContent forgets.
        // [2163-2166] spoonslavechecks (SpoonSlaveChecks.cs via SpoonReset.cs SrHook, docs/543 §1): slave ticks given EA's per-sleeper checks / light-sleeper wakes / slave ticks skipped (switch off) / errors.
        // [2167-2169] petwakebed (PetWakeBed.cs, docs/543 §4): bed parts cleared / left holding the pet (switch off) / errors. [2170-2173] slavewakereason (§5): slave waits handled / waits of more than one pass (EA loses the reason) / slave waits seen (switch off) / errors or ceiling.
        // [2679-2680] handsetreset (ObjectUseLogic.cs, docs/575 §2): handsets put back / errors.
        // [2681-2682] flowersinuse (ObjectUseLogic.cs, docs/575 §3): bouquets freed / errors.
        // [2683-2684] uselistprune (ObjectUseLogic.cs, docs/575 §4): use/reference entries dropped / errors.
        // [2685-2686] chestuselist (ObjectUseLogic.cs, docs/575 §5): Sims taken off / errors.
        // [2689-2690] resetinusefree (InUseLogic.cs, docs/576 §2): items freed / errors.
        // [2691-2692] putdowninuse (InUseLogic.cs, docs/576 §3): put-downs repaired / errors.
        // [2693-2695] autonomyinuseblock (AutonomyInUseBlock.cs, docs/577 §1): taken objects blocked / seen with switch off / errors.
        // [2696-2698] npcbusypartner (NpcBusyPartner.cs, docs/577 §4): NPC waits ended / seen with switch off / errors.
        // [2800-2802] sandwichalarm (SandwichAlarm.cs, docs/580 §16): stale handles removed / seen with switch off / errors.
        // [2803] sandwichalarm (SandwichAlarm.cs, docs/580 §16): saved duplicate ResetSandwichBoards alarms removed at world load.
        // [2804-2807] dreamcleanup (DreamCleanup.cs, docs/580 §18): pet nodes unfiled / woken nodes filed / seen with switch off / errors.
        // [2808-2810] vehiclefxhelper (Batch582Logic.cs, docs/582 §1): destroyed before re-create / seen with switch off / errors.
        // [2811-2813] prankditchempty (Batch582Logic.cs, docs/582 §2): empty-list ends handled / thrown with switch off / errors.
        // [2814-2817] serviceassignexit (Batch582Logic.cs, docs/582 §3): entries removed at exit / seen with switch off / saved stale removed at load / errors.
        // [2818-2820] serviceshowalarm (Batch582Logic.cs, docs/582 §4): re-arms skipped / seen with switch off / errors.
        // [2821-2823] maildogchaser (Batch582Logic.cs, docs/582 §6): chasers cleared / seen with switch off / errors.
        // [2824-2826] lifeguardsurvey (Batch582Logic.cs, docs/582 §7): alarms removed / seen with switch off / errors.
        // [2827-2829] moonriselistener (Batch582Logic.cs, docs/582 §8): listeners removed before re-add / seen with switch off / errors.
        // [2830-2833] holidaylights (Batch582Logic.cs, docs/582 §9): pulses raised from 0 / held alarms removed / saved orphans removed at load / errors.
        // [2834-2838] doorlockowner (Batch582bLogic.cs, docs/582 B4 §1): locks fixed at removal or move-in / seen with switch off / saved stale fixed at load / owner-list callbacks re-registered at load / errors.
        // [2839-2841] elevatorkitten (Batch582bLogic.cs, docs/582 B4 §2): refused at planning / seen with switch off / errors.
        // [2842-2844] elevatorresetriders (Batch582bLogic.cs, docs/582 B4 §3): waiting riders released / seen with switch off / errors.
        // [2845-2848] elevatorsavedlog (Batch582bLogic.cs, docs/582 B4 §4): elevators with saved entries / entries not sleeping / rider missing or no live task / errors.
        // [2849-2851] starlightreset (Batch582bLogic.cs, docs/582 B4 §5): selected-Sim resets skipped / seen with switch off / errors.
        // [2852-2854] moondiallistener (Batch582bLogic.cs, docs/582 B4 §6): listeners removed at Dispose / seen with switch off / errors.
        // [2855-2857] snublistener (Batch582bLogic.cs, docs/582 B4 §7): listeners removed at Sim.Destroy / seen with switch off / errors.
        // [2858-2860] elevatorridertimeout (Batch582bLogic.cs, docs/582 B4 §3b): rides run by the replacement / own-30-minute failsafe resets / errors (EA ran).
        // [2861-2863] rolenullguard (Batch582cLogic.cs, docs/582 B5 §1): answered no role / seen with switch off / errors.
        // [2864-2866] homeworkskillbonus (Batch582cLogic.cs, docs/582 B5 §2): rates raised / seen with switch off / errors.
        // [2867-2869] horseranchchallenge (Batch582cLogic.cs, docs/582 B5 §3): answers hidden / seen with switch off / errors.
        // [2870-2872] digsiteemptypick (Batch582cLogic.cs, docs/582 B5 §4): empty digs kept the treasure / seen with switch off / errors.
        // [2873-2875] oppchainworld (Batch582cLogic.cs, docs/582 B5 §5): world-change branch taken / seen with switch off / errors.
        // [2876-2878] teleportcommunitylot (Batch582cLogic.cs, docs/582 B5 §6): options hidden / seen with switch off / errors.
        // [2879-2881] photosubjectstars (Batch582cLogic.cs, docs/582 B5 §7): values corrected / seen with switch off / errors.
        // [2882-2885] residentspawnslot (ResidentSpawnSlot.cs, docs/582 B5 §8): slots accepted / would accept with switch off / failures kept / errors.
        // [2886-2888] talkoccupationchance (Batch582dLogic.cs, docs/582 B5 §9): menus built / Unemployment through the fixed chance / errors.
        // [2889-2891] shelldoorsummon (Batch582dLogic.cs, docs/582 B5 §10): summons run / teleport refused, exit not queued / errors.
        // [2892-2894] disposebase (Batch582eLogic.cs, docs/582 B6 §1): base disposes run / seen with switch off / errors.
        // [2895-2897] boardingschoolmoney (Batch582eLogic.cs, docs/582 B6 §2): schools corrected / would change with switch off / errors.
        // [2898-2901] freshclothingresume (Batch582eLogic.cs, docs/582 B6 §3): resumed / skipped worn off / seen with switch off / errors.
        // [2902-2904] twinbellyshape (Batch582eLogic.cs, docs/582 B6 §4): shapes corrected / seen with switch off / errors.
        // [2905-2907] boatstaleswimmer (Batch582fLogic.cs, docs/582 B7 §1): stale keys removed / would throw with switch off / errors.
        // [2908-2910] oppcategoryprotect (Batch582fLogic.cs, docs/582 B7 §2): early returns kept unprotected / seen with switch off / errors.
        // [2911-2913] nocommercialcar (Batch582fLogic.cs, docs/582 B7 §3): cars idled / seen with switch off / errors.
        // [2914-2916] scavengernolot (Batch582fLogic.cs, docs/582 B7 §4): accepts handled / seen with switch off / errors.
        // [2917-2919] partylotlistener (Batch582fLogic.cs, docs/582 B7 §5): stale listeners removed / would throw with switch off / errors.
        // [2920-2922] weddinglistener (Batch582gLogic.cs, docs/582 B8 §5): listeners removed / left with switch off / errors.
        // [2923-2925] innerbeautyonce (Batch582hLogic.cs, docs/582 B8b §2): duplicate calls skipped / duplicates seen with switch off / errors.
        // [2929-2931] sprelcount (Batch588Logic.cs, docs/588 B10 §1): Collects with own counts / incremental Collects (switch off) / errors.
        // [2932-2934] spcollectnull (Batch588Logic.cs, docs/588 B10 §2): null households skipped / seen with switch off / errors.
        // [2935-2937] spclonedict (Batch588Logic.cs, docs/588 B10 §3): clones given their own dictionary / clones sharing (switch off) / errors.
        // [2938-2940] splotlookup (Batch588Logic.cs, docs/588 B10 §4): specs counted on their lot / dropped by EA (switch off) / errors.
        // [2941-2943] spbucketindex (Batch588Logic.cs, docs/588 B10 §5): targeted actions with the bucket / targeted draws (switch off) / errors.
        // [2944-2946] spcreatemovein (Batch588Logic.cs, docs/588 B10 §6): predictions made once / double predictions (switch off) / errors.
        // [2947-2949] splotdensity (Batch588Logic.cs, docs/588 B10 §7): left out by the lot test / off-lot households EA counted (switch off) / errors.
        // [2950-2952] sproommatejob (Batch588Logic.cs, docs/588 B10 §8): roommates given the job / left jobless (switch off) / errors.
        // [2953-2955] spoccultpick (Batch588Logic.cs, docs/588 B10 §9): random occult picks / first-in-list picks (switch off) / errors.
        // [2956-2958] spjobatwork (Batch588Logic.cs, docs/588 B10 §10): new jobs kept / new jobs EA dropped (switch off) / errors.
        // [2959-2961] sprecheck (Batch588Logic.cs, docs/588 B10 §11): actions not run on a played household / rounds with switch off / errors.
        // [2962-2964] sppensionprompt (Batch588Logic.cs, docs/588 B10 §12): pension prompts asked first / asked after the leave (switch off) / errors.
        // [2965-2967] hhcleanupdup (Batch588Logic.cs, docs/588 B10 §13): cleanups run fixed / shared-member loads (switch off) / errors.
        // [2968-2970] virtualhomeshare (Batch588Logic.cs, docs/588 B10 §14): homes from the tuned share / draws over every lot (switch off) / errors.
        // [2971-2973] celebalarmnull (Batch588Logic.cs, docs/588 B10 §15): alarms removed with no Sim / would have thrown (switch off) / errors.
        // [2974-2976] disposevirtualslot (Batch588bLogic.cs, docs/588 B10 §16): virtual move-outs at Dispose / left in their slot (switch off) / stale entries swept at load.
        // [2977-2979] agingalarmcancel (Batch588bLogic.cs, docs/588 B10 §17): graduation alarms cancelled / left armed (switch off) / errors.
        // [2980-2982] virtualmoveindup (Batch588bLogic.cs, docs/588 B10 §18): repeat move-ins not added twice / added twice (switch off) / errors.
        // [2983-2985] petpoolghost (Batch588bLogic.cs, docs/588 B10 §19): ghost ids dropped / ghost ids seen (switch off) / errors.
        // [2986-2988] eatpostonce (Batch589Logic.cs, docs/589 B11 §1): Cleanup posts skipped / doubled posts (switch off) / cut-off eats posted by Cleanup.
        // [2989-2991] motivemultonce (Batch589Logic.cs, docs/589 B11 §2): updates with the multiplier once / stacked updates (switch off) / errors.
        // [2992-2994] fightbroadcastend (Batch589Logic.cs, docs/589 B11 §3): broadcasters ended at Cleanup / left running (switch off) / errors.
        // [2995-2997] reactioncopy (Batch589Logic.cs, docs/589 B11 §4): Sims given the caller's reaction / Sims given an earlier Sim's trait reaction (switch off) / errors.
        // [2998-3000] carpoolschoolskip (Batch589Logic.cs, docs/589 B11 §5): skipping riders sent on their own / Runs that threw (switch off) / errors.
        // [3001-3003] tvsyncalarm (Batch589bLogic.cs, docs/589 B11 §6): handles reset / stale handles kept (switch off) / errors.
        // [3004-3006] photofunds (Batch589bLogic.cs, docs/589 B11 §7): photo payments taken / funds doubled (switch off) / errors.
        // [3007-3009] ltrhistorybits (Batch589bLogic.cs, docs/589 B11 §8): forced changes with history kept / history wiped (switch off) / errors.
        // [3010-3012] goodbyepure (Batch589bLogic.cs, docs/589 B11 §9): menu builds kept pure / side effects run (switch off) / errors.
        // [3013-3015] bathcleanup (Batch589bLogic.cs, docs/589 B11 §10): bathers' alarm and footprint kept / cleared by another Sim's Cleanup (switch off) / errors.
        // [3016-3018] tvwatchskill (Batch589bLogic.cs, docs/589 B11 §11): stray stops skipped / stray stops (switch off) / errors.
        // [3019-3021] computergametype (Batch589bLogic.cs, docs/589 B11 §12): shared picks reset / picks left in the Singleton (switch off) / errors.
        // [3022-3024] moodtankreset (Batch589bLogic.cs, docs/589 B11 §13): tank memories cleared / kept with a recovered mood (switch off) / errors.
        // [3025-3027] reactiongrumpy (Batch589bLogic.cs, docs/589 B11 §14): reactions set / left at Angry (switch off) / errors.
        // [3028-3030] trainsimgone (Batch589bLogic.cs, docs/589 B11 §15): trainee-less runs ended / runs that threw (switch off) / errors.
        // [3031-3033] stereoupgrade (Batch589bLogic.cs, docs/589 B11 §16): rebuilds skipped on a silent stereo / dismissals of other dancers skipped / off-switch hits.
        // [3034-3036] buffremovedelayed (Batch589cLogic.cs, docs/589 B11 §17): live buffs removed after a pending timer / left on the Sim (switch off) / errors.
        // [3037-3039] tvdisclistener (Batch589cLogic.cs, docs/589 B11 §18): old listeners removed / old listeners kept (switch off) / errors.
        // [3040-3042] fridgetestnull (Batch589cLogic.cs, docs/589 B11 §19): tests answered without the throw / tests that threw (switch off) / errors.
        // [3043-3045] cancelposturedef (Batch589cLogic.cs, docs/589 B11 §20): duplicate stand-ups not queued / queued again (switch off) / errors.
        // [3046-3048] facialidleweight (Batch589cLogic.cs, docs/589 B11 §21): lists built with float shares / lists with a truncated share (switch off) / errors.
        // [3049-3051] bathsituationfx (Batch589dLogic.cs, docs/589 B11 §22): second exits skipped / old effects disposed / seen with the switch off.
        // [3052-3054] socialnoeffect (Batch589dLogic.cs, docs/589 B11 §23): null rules seen / null rules with ilp_socialnoeffect expected on / errors.
        // [3055-3057] guitargnomeboost (Batch589dLogic.cs, docs/589 B11 §24): extra boosts removed / boosts left on (switch off) / errors.
        // [3058-3060] distressgate (Batch589dLogic.cs, docs/589 B11 §25): tests answered by a buff / missed (switch off) / errors.
        // [3061-3063] moodidleflavor (Batch589dLogic.cs, docs/589 B11 §26): rows appended / rows dropped (switch off) / errors.
        // [3064-3066] protestexit (Batch590Logic.cs, B12 §1, docs/590 B2): exits / root-only cancels (switch off) / errors.
        // [3067-3069] jogterrain (Batch590Logic.cs, B12 §2, docs/590 B3): walk-backs queued / fatigue checks with the switch off / errors.
        // [3070-3072] lifeguardquit (Batch590Logic.cs, B12 §3, docs/590 B4): loops ended / quit loops run on (switch off) / errors.
        // [3073-3075] frolicexit (Batch590Logic.cs, B12 §4, docs/590 B5): frolics ended / missed with the switch off / errors.
        // [3076-3078] shoodef (Batch590Logic.cs, B12 §5, docs/590 B6): shoos with an own definition / shared writes (switch off) / errors.
        // [3079-3081] igloolookats (Batch590Logic.cs, B12 §6, docs/590 B8): look-ats restored / left off (switch off) / errors.
        // [3082-3084] gohereboatlist (Batch590Logic.cs, B12 §7, docs/590 B10): Sims dropped / left in a list (switch off) / errors.
        // [3085-3087] bouncerrole (Batch590Logic.cs, B12 §8, docs/590 B12): notice tests answered our way / tests with the switch off / null-role runs ended.
        // [3088-3090] tricksocials (Batch590Logic.cs, B12 §9, docs/590 B13): restored / left off (switch off) / errors.
        // [3091-3093] fearfaintundo (Batch590Logic.cs, B12 §10, docs/590 B13): undone / left (switch off) / errors.
        // [3094-3096] lotprune (Batch590Logic.cs, B12 §11, docs/590 B14): lots pruned / lots kept (switch off) / errors.
        // [3097-3099] werewolffind (Batch590Logic.cs, B12 §12, docs/590 B15): null finds and insect sums fixed / seen with the switch off / errors.
        // [3100-3102] copyhomeworkfx (Batch590Logic.cs, B12 §13, docs/590 A2): cleared / left (switch off) / errors.
        // [3103-3105] spelltargetend (Batch590Logic.cs, B12 §14, docs/590 A3): targets released / left waiting (switch off) / errors.
        // [3106-3108] foodtraygrill (Batch590Logic.cs, B12 §15, docs/590 A6): fixed returns / seen with the switch off / errors.
        // [3109-3111] compositionnull (Batch590Logic.cs, B12 §16, docs/590 A7, 591 C8): imports cleaned / nulls kept (switch off) / errors.
        // [3112-3114] petdreamtimer (Batch590Logic.cs, B12 §17, docs/590 A10): timers reset / left due (switch off) / errors.
        // [3115-3117] awkwardscience (Batch591Logic.cs, B12 §18, docs/591 C1): gains corrected / bonus on another skill (switch off) / errors.
        // [3118-3120] chessdefeated (Batch591Logic.cs, B12 §19, docs/591 C2): wins recorded our way / EA's record (switch off) / errors.
        // [3121-3123] privacyothers (Batch591Logic.cs, B12 §20, docs/591 C4): Sims dropped / kept (switch off) / errors.
        // [3124-3126] gigmaster (Batch591Logic.cs, B12 §21, docs/591 C5): answers our way / off by one (switch off) / errors.
        // [3127-3129] intmultiple (Batch591Logic.cs, B12 §22, docs/591 C6, 592 S4): draws our way / EA's draws (switch off) / errors.
        // [3130-3132] doorprivacynull (Batch591Logic.cs, B12 §23, docs/591 C7): updates with a null target skipped / updates that threw (switch off) / errors.
        // [3133-3135] leafburnfail (Batch591Logic.cs, B12 §24, docs/591 R1): piles freed / left blocked (switch off) / errors.
        // [3136-3138] catchpreyodds (Batch591Logic.cs, B12 §25, docs/591 R2): rolls taken / sure catches (switch off) / errors.
        // [3139-3141] picnicants (Batch591Logic.cs, B12 §26, docs/591 R3): cleanups done / left (switch off) / errors.
        // [3142-3144] loyaldoglay (Batch591Logic.cs, B12 §27, docs/591 R5): lay-downs queued / refused targets (switch off) / errors.
        // [3145-3147] visitlisteners (Batch591Logic.cs, B12 §28, docs/591 R6): entries dropped / kept (switch off) / errors.
        // [3148-3150] sciencefxstop (Batch591Logic.cs, B12 §29, docs/591 R7): old sets stopped / leaked (switch off) / errors.
        // [3151-3153] daycareliking (Batch591Logic.cs, B12 §30, docs/591 R9): payouts corrected / payouts at liking 0 (switch off) / errors.
        // [3154-3156] kegbreakage (Batch591Logic.cs, B12 §31, docs/591 R10): chances computed our way / kegger rate on a non-kegger party (switch off) / errors.
        // [3157-3159] romancecooldown (Batch591Logic.cs, B12 §32, docs/591 R11): blocked our way / let through (switch off) / errors.
        // [3160-3162] lightcustom (Batch591Logic.cs, B12 §33, docs/591 R12): lights set our way / EA's copies (switch off) / errors.
        // [3163-3165] eatpreycancel (Batch591Logic.cs, B12 §34, docs/591 R13): prey kept or re-enabled / seen with the switch off / errors.
        // [3166-3168] meltalarm (Batch591Logic.cs, B12 §35, docs/591 R16): starts our way / starts that threw or doubled (switch off) / errors.
        // [3169-3171] newspaperdef (Batch591Logic.cs, B12 §36, docs/591 R21): reads with an own definition / shared writes (switch off) / errors.
        // [3172-3174] leaveconvnull (Batch592Logic.cs, B12 §37, docs/592 S2): empty leaves skipped / counted (switch off) / errors.
        // [3175-3177] vacationalarms (Batch592Logic.cs, B12 §38, docs/592 S3): arrivals fixed / arrivals with the double shift (switch off) / errors.
        // [3178-3180] cleanupvfx (Batch592Logic.cs, B12 §39, docs/592 S5): effect lists disposed / left to the finalizer (switch off) / errors.
        // [3181-3183] lookatcopy (Batch592Logic.cs, B12 §40, docs/592 S6): copies filtered / live lists cut (switch off) / errors.
        // [3184-3186] lottotooltip (Batch592Logic.cs, B12 §41, docs/592 S8): tooltips fixed / shifted tooltips (switch off) / errors.
        // [3187-3189] traiticondislike (Batch592Logic.cs, B12 §42, docs/592 S9): dislike icons shown / hidden (switch off) / errors.
        // [3190-3192] roleunprotect (Batch592Logic.cs, B12 §43, docs/592 S11): protections removed / left (switch off) / errors.
        // [3193-3195] putdownplaced (Batch592Logic.cs, B12 §44, docs/592 S13): placements fired as placed / fired as removed (switch off) / errors.
        // [3196-3198] nullsituation (Batch593Logic.cs, B13 §1, docs/593 V2): null assignments dropped or errors contained / seen with the switch off / errors.
        // [3199-3201] lightningweights (Batch593Logic.cs, B13 §2, docs/593 L1): weights restored / left zeroed (switch off) / errors.
        // [3202-3204] plantdormantflip (Batch593Logic.cs, B13 §3, docs/593 G1): flips undone / flips seen (switch off) / errors.
        // [3205-3207] plantdormanttime (Batch593Logic.cs, B13 §4, docs/593 G2): times refreshed / spans left to drain (switch off) / errors.
        // [3208-3210] harvestextras (Batch593Logic.cs, B13 §5, docs/593 H2, H5): extras kept and journals to the harvester / extras lost (switch off) / errors.
        // [3211-3213] shroomslots (Batch593Logic.cs, B13 §6, docs/593 H3): mushroom loops our way / EA's loops (switch off) / errors.
        // [3214-3216] harvestregion (Batch593Logic.cs, B13 §7, docs/593 H4): regions our way / Average answered as Good (switch off) / errors.
        // [3217-3219] wellstealcooldown (Batch593Logic.cs, B13 §8, docs/593 B2b): tests refused in the cooldown / passed in it (switch off) / errors.
        // [3220-3222] oppfailcharge (Batch593Logic.cs, B13 §9, docs/593 O1): second charges skipped / charged twice (switch off) / errors.
        // [3223-3225] tempforecast (Batch593Logic.cs, B13 §10, docs/593 T1): forecasts our way / mirrored (switch off) / errors.
        // [3226-3228] mountfishcost (Batch593Logic.cs, B13 §11, docs/593 F1): costs set to the run's fish / another fish's cost charged (switch off) / errors.
        // [3229-3231] firefightercount (Batch593Logic.cs, B13 §13, docs/593 V8): extra firefighters not sent / sent (switch off) / errors.
        // [3232-3234] repairmanskill (Batch593Logic.cs, B13 §14, docs/593 V9): levels set to the roll / stacked (switch off) / errors.
        // [3235-3237] roundupreturn (Batch593Logic.cs, B13 §15, docs/593 V3): inits ended / run on (switch off) / errors.
        // [3238-3240] petharvestvfx (Batch593Logic.cs, B13 §17, docs/593 H1): effects kept and stopped / leaked (switch off) / errors.
        // [3241-3243] suatgradebonus (EpBatch1aLogic.cs, docs/587 EP1 §2): scores corrected / differ with switch off / errors.
        // [3244-3246] cattoypickerrel (EpBatch1bLogic.cs, docs/587 EP1 §17): strangers added / left out with switch off / errors.
        // [3247-3249] dogswimslots (EpBatch4Logic.cs, docs/587 EP4 §9.1): loops skipped, no free slot / would throw with switch off / errors.
        // [3250-3252] fragmentreconstruct (EpBatch4Logic.cs, docs/587 EP4 §9.2): Target picked first / extra fragment used with switch off / errors.
        // [3253-3255] labobjectpush (EpBatch4Logic.cs, docs/587 EP4 §9.3): lot objects pushed / idle students with switch off / errors.
        // [3256-3258] businessplannerrun (EpBatch4Logic.cs, docs/587 EP4 §9.4): runs fixed / shared roll or pocketing with switch off / errors.
        // [3259-3261] futuredescendantsize0 (EpBatch4Logic.cs, docs/587 EP4 §9.5): unpacks, renames or stuck loops skipped / seen with switch off / errors.
        // [3262-3264] junglegymexitqueue (EpBatch4bLogic.cs, docs/587 EP4 §10.1): stale entries dropped / stale heads with switch off / errors.
        // [3265-3267] wildhorselot (EpBatch4bLogic.cs, docs/587 EP4 §10.2): picks with corrected weights / big parks under-weighted with switch off / errors.
        // [3268-3270] harvesterunripe (EpBatch4bLogic.cs, docs/587 EP4 §10.3): unripe suctions skipped / harvested unripe with switch off / errors.
        // [3271-3273] hottubseatposture (EpBatch4bLogic.cs, docs/587 EP4 §10.4): duplicate inits skipped / duplicate alarms with switch off / errors.
        // [3274-3276] teenpartybreakup (EpBatch4bLogic.cs, docs/587 EP4 §10.5): runs past a missing party / not counted with switch off / errors.
        // [3277-3279] dogswimaroundlist (EpBatch4bLogic.cs, docs/587 EP4 §10.6): dogs put back / left off with switch off / errors.
        // [3280-3282] tutorballoonalarm (EpBatch4bLogic.cs, docs/587 EP4 §10.7): alarms moved to tutoring start / started at creation with switch off / errors.
        // [3283-3285] chemistryvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3286-3288] dartboardvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3289-3291] wardrobewandvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3292-3294] petdroolvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3295-3297] timeportalvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3298-3300] haystackvfx (EpBatch5Logic.cs, docs/587 EP5 §11.1): dropped effects auto-destroyed / dropped with switch off / errors.
        // [3301-3303] hayscattertime (EpBatch5bLogic.cs, docs/587 EP5 §11.2.1): scatter times reset / kept with switch off / errors.
        // [3304-3306] hauntedgnomeinv (EpBatch5bLogic.cs, docs/587 EP5 §11.2.2): inventory gnomes cleaned / kept with switch off / errors.
        // [3307-3309] houdinignomeleak (EpBatch5bLogic.cs, docs/587 EP5 §11.2.3): orphan gnomes destroyed / fallback skipped, no home lot / errors.
        // [3310-3312] showstagefxend (EpBatch5bLogic.cs, docs/587 EP5 §11.2.4): one-shots ended, stale turn-ons skipped, failures fired, effects disposed / show ends dropped with switch off / errors.
        // [3313-3315] soakedpetselfreact (EpBatch5bLogic.cs, docs/587 EP5 §11.2.5): self reactions skipped / shakes with switch off / errors.
        // [3316-3318] tagbagsurfaceleak (EpBatch5bLogic.cs, docs/587 EP5 §11.2.6): surfaces destroyed / leaked with switch off / errors.
        // [3319-3321] equestriantrophyname (EpBatch5bLogic.cs, docs/587 EP5 §11.2.7): horse names restored / rider name kept with switch off / errors.
        // [3322-3324] rubbleuselist (EpBatch5bLogic.cs, docs/587 EP5 §11.2.8): Sims taken off / left on with switch off / errors.
        // [3325-3327] backpacklotleak (EpBatch5bLogic.cs, docs/587 EP5 §11.2.9): lot handlers dropped / left subscribed with switch off / errors.
        // [3328-3330] streetartmasterboost (EpBatch5bLogic.cs, docs/587 EP5 §11.2.10): Master rows boosted / boost missed with switch off / errors.
        // [3331-3333] bubblingspringsreset (EpBatch5bLogic.cs, docs/587 EP5 §11.2.11): broadcasters recreated / left missing with switch off / errors.
        // [3334-3336] floorswitchuselist (EpBatch6Logic.cs, docs/587 EP6 §12.1): Sims taken off / left on with switch off / errors.
        // [3337-3339] donationbuffchance (EpBatch6Logic.cs, docs/587 EP6 §12.2): rolls that changed outcome / rolls with switch off / errors.
        // [3340-3342] vampirefruitsnack (EpBatch6Logic.cs, docs/587 EP6 §12.3): Snacks at the threshold / Sated with switch off / errors.
        // [3343-3345] fallenrockcollected (EpBatch6Logic.cs, docs/587 EP6 §12.4): collected rocks kept / destroyed with switch off / errors.
        // [3346-3348] spellbooksummonload (StoreBatch1Logic.cs, docs/587 store §13.1): Summon spell progress restored on import.
        // [3349-3351] chocfountaingeyser (StoreBatch1Logic.cs, docs/587 store §13.2): old geyser alarms removed on refill.
        // [3352-3354] hypnotrickrelease (StoreBatch1Logic.cs, docs/587 store §13.3): volunteers released when the trick ends.
        // [3355-3357] voodoofairyfadein (StoreBatch1Logic.cs, docs/587 store §13.4): hidden fairies faded in on cleanup.
        // [3358-3360] saunatubclear (StoreBatch1Logic.cs, docs/587 store §13.5): sauna holds cleared when bathing ends.
        // [3361-3363] slotmachinedefs (StoreBatch1Logic.cs, docs/587 store §13.6): shared stakes entries kept.
        // [3364-3366] treewishingwell (StoreBatch1Logic.cs, docs/587 store §13.7): store wishing wells counted.
        // [3367-3369] playpentalkcast (StoreBatch1Logic.cs, docs/587 store §13.8): old talk broadcasters disposed.
        // [3370-3372] ovenmenunull (StoreBatch1Logic.cs, docs/587 store §13.9): null menu lists skipped.
        // [3373-3375] fvstandskillcurve (StoreBatch1Logic.cs, docs/587 store §13.10): stand skill curve rebuilt.
        // [3376-3378] pokerdeadplayer (StoreBatch1Logic.cs, docs/587 store §13.11): gone players unseated.
        // [3379-3381] roulettehackreset (StoreBatch1Logic.cs, docs/587 store §13.12): hacks cleared on reset.
        // [3382-3384] coopeggquality (StoreBatch1Logic.cs, docs/587 store §13.13): egg quality index shifted to the Quality enum.
        // [2453-2454] geniebanish (docs/559 §4): banishes run through the fixed copy / errors. [2455-2457] spacesimpartner (docs/559 §5): partners released / partners left waiting (also off) / errors.
        // [2438-2442] birthroute (BirthRoute.cs, docs/558 §1): birth plans kept uncut / failed hospital births (also off) / home births queued instead / homeless moms sent to a hospital / errors.
        // [2469-2476] birthroute (BirthRoute.cs, docs/558 §1): re-haves deferred / lost births re-armed / stale or duplicate calls dropped / BabyIsComing restored / unseen moms placed at the hospital / watchdogs / homeless after-birth / born after a failure.
        // [2443-2447] putdownplace (PutDownPlace.cs, docs/558 §2): put-down searches failed on routes (also off) / put down in place / children moved to a safe spot / declined / errors.
        // [2415-2416] etderefdiag (ErrorTrapDerefDiag.cs, docs/552 §11, log only, on): etderef: lines written / errors.
        // [2412-2414] cookclicklistener (CookClickListener.cs, docs/546 §17): clicked-object listeners of destroyed servings removed / ObjectDisposed handlers unhooked from the original object / errors.
        // [2387-2390] hiddenroomspawn (HiddenRoomSpawn.cs, docs/552): reset fallbacks re-picked / corner spots re-picked / created service NPCs moved / errors.
        // [2400-2404] party fixes (PartyFixes.cs, docs/554; 2398-2399 unused): partyoutcome lame restored / guest tier changed / errors; partymotive motives removed / errors.
        // [2391-2397] aptleave (AptLeave.cs, docs/553): apartment room-change calls skipped (not leaving) / run with the switch on / seen (any state) / errors / deferred party removals applied at the real lot exit / deferrals dropped (back in the apartment) / household skips (of 2391).
        // [2379-2383] hiddenroomnpc (HiddenRoom.cs, docs/552): trapped NPCs moved out (first try) / second tries (sent home) / household Sims seen trapped (log only) / NPCs left after two tries / errors.
        // [2300-2303] casexitsweep (CasExitSweep.cs): detached live entries at CAS exit (also off) / cleaned / skipped parented / errors.
        // [2284-2286] caspuckevents (CasPuckLeak.cs): CASPuck disposed with its dashboard handler attached (also off) / handlers removed / errors.
        // [2293-2295] famcursorleak (CasPuckLeak.cs): family-screen drag cursor windows at Unload (also off) / disposed / errors.
        // [2296-2298] compositorghost (CompositorLeak.cs): ghost controller entries after Unload (also off) / removed / left or errors; [2299] live orphan windows disposed.
        // [2281-2283] filtergridleak (FilterGridLeak.cs): filter grids at Buy/Build Unload (also off) / DisposeOnDetach restored / errors.
        // [2278-2280] moodpending (MoodPending.cs): ChangeMoodValue on a pending buff (also off) / mood moves skipped / errors.
        // [2272-2277] buyclearrecycle / buygridsweep (BuyCellRecycle.cs): cells at ShowCatalogGrid(false) (also off) / cached / errors / icons in other grids at Dispose (also off) / disposed / errors.
        // [3394-3397] megaphonerun (WalkStyleLeak77.cs, docs/608 §2): WakeWithMegaphone Run requests moved to the RouteAway / released (no route-away) / left with the switch off / errors.
        // [3398-3401] dragoncurserun (WalkStyleLeak77.cs, docs/608 §3): Phantasmal Curse Run requests moved to the RouteAway / released / left with the switch off / errors.
        // [3402-3404] vhomewalk (WalkStyleLeak77.cs + RouteFixes.cs VirtualHomeHook, docs/608 §4): GoToVirtualHomeInternal walk-style requests released / left with the switch off / errors.
        // [3405-3410] speedskiplevel / speedskipnoactor (GameSpeed78.cs, docs/609 §2-3): Skip fallbacks given level 1 / level 3 for Normal (off) / errors; no-actor Skips run as Normal / time frozen (off) / errors.
        // [3411-3413] speedrecalclock (GameSpeed78.cs, docs/609 §4): recalculations kept the lock / lock cleared (off) / errors.
        // [3414-3416] speedlockowner (GameSpeed78.cs, docs/609 §5): build/buy exit unlocks skipped (lock not ours) / another holder released (off) / errors.
        // [3417-3420] alarmzeroperiod (GameSpeed78.cs, docs/609 §6): periods <= 0 as 1 tick / catch-ups by one division / repeating timers checked / errors.
        // [3476-3478] spoilbroadcast (Scan85.cs, docs/615 §1): broadcasters disposed / leaked (off) / errors.
        // [3479-3481] weatherstonerelisten (Scan85.cs, docs/615 §2): stale listeners removed / stacked (off) / errors.
        // [3482-3484] birthdayagerecord (Scan85.cs, docs/615 §3): age-ups with listener kept / dropped (off) / errors.
        // [3421-3423] heartbrokenbcast (GameSpeed78.cs, docs/609 §7): replaced heartbroken broadcasters disposed / left registered (off) / errors.
        // [3453-3456] rhexitunseen (StuckJobs83.cs, docs/613 §2): exit waits skipped / unseen NPC waits (off) / household or seen kept / errors.
        // [3457-3460] tipsnpcend (StuckJobs83.cs, docs/613 §3): tips loops ended at low-LOD cap / past cap (off) / past cap household or seen kept / errors.
        // [3461-3463] barputdownwait (Scan84.cs, docs/614 §1): finished inline / hangs entered (off) / errors.
        // [3464-3466] putdownsurfaceuse (Scan84.cs, docs/614 §2): releases / leaks (off) / errors.
        // [3467-3469] swiperestore (Scan84.cs, docs/614 §3): restores / lost (off) / errors.
        // [3470-3472] unicornspawnfail (Scan84.cs, docs/614 §4): failed spawns ended / left invisible (off) / errors.
        // [3473-3475] datadiscputback (Scan84.cs, docs/614 §5): discs put back / lost (put-back refused) / errors.
        // [3424-3429] spawnerskip (SpawnerMapTag79.cs, docs/610 §2): spawns ended without search / armed full searches / streaks armed / errors / spawner searches / spawns placed by search.
        // [3430-3434] maptagtaskalive (SpawnerMapTag79.cs, docs/610 §3): visibility restarts / visibility ends (off) / controller restarts / controller ends (off) / errors.
        // [3435-3438] lotmgrcallbacks (LotCallbacks80.cs, docs/611 §2): stacked copies removed at PreWorldLoad / stacked copies seen (off) / copies removed at Shutdown / errors.
        // [3439-3441] fireplaceherb (LotCallbacks80.cs, docs/611 §3): burning herbs removed before a new one / left (off) / errors.
        // [3442-3445] rummagebcast (LotCallbacks80.cs, docs/611 §4): disposed at Cleanup / stale disposed at create / left (off) / errors.
        // [3446-3449] stairwaitend (StairDup82.cs, docs/612 §2): stair waits ended at 30 sim-min / past deadline (off) / past deadline household kept / errors.
        // [3450-3452] simdisposelink (StairDup82.cs, docs/612 §3): links kept / unbound disposes (off) / errors.
        // [2269-2271] buycellrecycle (BuyCellRecycle.cs): Buy catalog cells dropped by AddGridItem (also off) / returned to a cache / errors.
        // [2793-2796] detailprioexact (DetailPrioExact.cs, docs/605 §3): exact answers / EA's body (null list or description, error) / check mismatches / errors.
        // [2260-2268] dreamrootorphan / blogorphan / dnpupdateguard (WishListenerFixes.cs): root listeners left by a thrown-away test (also off) / removed / errors / blog one-off listeners left (also off) / removed / errors / Update exceptions seen / swallowed / errors.
        // [2254-2259] dnpdisposelistener / seasondup (ListenerFixes.cs): wish managers created (also off) / object listeners moved / errors / duplicate season adds (also off) / skipped / errors.
        // [2251-2253] mooddrift (MoodDrift.cs): Sims whose mood target had drifted off their buffs (also counted off) / mood points corrected / errors.
        // [2245-2250] attractionalarm / uncleguard (RelAlarmGuard.cs): attraction handlers seen / controllers stopped for a removed Sim / errors / uncle checks with a parent with no description (also off) / uncle checks / errors.
        // [2242-2244] sellcursorleak (SellCursorLeak.cs): sell panels closed with drag-cursor windows noted (also with the switch off) / windows disposed / errors.
        // [2239-2241] familyinvghost (FamilyInvGhost.cs): destroyed objects still listed in a family inventory (also with the switch off) / removed / errors.
        // [2235-2238] relremovesafe (RelRemoveSafe.cs): see the file.
        // [2229-2234] genealogycycle / spouseunlink (GenealogyGuard.cs): ancestor repeats skipped / parent loops stopped / Divorce with no spouse / old spouses unlinked / ApplySpouseUpdate calls / errors.
        // [2226-2228] unused: petwishleak (PR #368) dropped at import, duplicate of dnppetkeys (Rodney 2026-11-12).
        // [2223-2225] compositorleak (CompositorLeak.cs): CAS design-tool Unloads with a tool (also with the switch off) / dict entries of its tree unregistered / errors.
        // [2220-2222] casrowleak (CasRowLeak.cs): empty CAS clothing rows CASClothingCategory.AddGridItem left unattached (also with the switch off) / disposed the OnCustomDetach way / errors.
        // [2217-2219] buyiconleak (BuyIconLeak.cs): Buy/Build exits seen / stale custom-control entries removed after BuyController.Dispose / errors.
        // [2092-2093] deadsimhollow (DeadSimCount.cs): destroyed Sims held 6+ sim hours whose own reference fields were cleared / fields cleared.
        // [2106-2107] dnpsubjectcache (DnpSubjectCache.cs): cached descriptions of removed Sims cleared / wish-subject table entries.
        // [2108-2109] hudunsub (HudUnsub.cs): HUD panels whose leftover model handlers were removed at Dispose / handlers not found or errors.
        // [2103-2105] mapviewkeep (MapViewKeep.cs): map filter panels shown again / built by the game / kept panels dropped.
        // [2094-2096] tinyuieventreg (TinyUiEventReg.cs): Tiny UI Fix registry entries of detached windows removed / their handlers removed / entries left.
        // [2097-2099] customctlsweep (CustomCtlSweep.cs): UIManager custom-control wrappers of gone windows removed / not-attached kept / entries left.
        // [2120-2121] exitcbsweep (ExitCbSweep.cs): stale Sim.OnExitReasonsAdded route delegates removed / longest delegate list seen on one Sim before the pass.
        // [2122-2123] dnppetkeys, dnpobjkeys (DnpRemovalKeys.cs): ended Pet/SimOrPet wish nodes dropped from mPotentialRemovalNodes / ended nodes under a destroyed object key dropped.
        // [2739-2741] tpdoorrights (UnseenTeleportGoal.cs, docs/584 §7): lots open by the Sim's own door rights that upstream closes / of those only by household access / closed by a door the Sim cannot pass.
        // [2753-2755] tpaptgoal (UnseenTeleportGoal.cs, docs/584 §8): home apartment / shell goals allowed / refused as tomb or hidden room / refused otherwise (no way out, raised, unknown).
        // [2760-2762] queueproximity (SleepQueue597.cs, docs/597 §2): places by squared distance / places that differ (switch off) / errors.
        // [2763-2765] subroutefail (SleepQueue597b.cs, docs/597 §3): route fails skipped (replacement pushed) / would be skipped (switch off) / errors.
        // [2766-2771] mopunreachable (MopUnreachable.cs, docs/578 §3b): puddles skipped in a run / runs that mopped after a skip / runs ended on an unreachable puddle with others left (switch off) / memo withholds / memo seen with switch off / errors.
        // [2772-2774] petpuddletile (MopUnreachable.cs, docs/578 §3b): hits moved to the puddle used / hits that dried another puddle (switch off) / errors.
        // [876-877] bookhand (BookHand.cs, docs/251): books Book.StopUsingBook left in the hand with no carry machine, seen at an EA put-down that would throw (also with the switch off), moved out of the hand by EA's handler.
        // [758-759] asyncjigsync (AsyncJigSync.cs, RouteBackoff.cs AsyncEligible, docs/240): own plans kept sync while the Sim's interaction holds an unplaced SocialJig / of those with the jig only in the linked interaction.
        // [763-768] autofft2 (AutOffload.cs, docs/208 §9.1): items, A0, A2, A4, A5, exceptions
        // [878-883] autofft2ic (AutOffload.cs, docs/126 §8): items, equal, mismatch, aborts, not compared, exceptions
        // [769-776] autofflot (AutOffload.cs, docs/208 §9.2): items, A0, A2, A4, A5, exceptions, dry checks, dry equal
        // [785-817] G8 search queue wait, manager depth, seen path, household, worker wait, search length (AutLive.cs QueueNote, docs/222 §11.8)
        // [867-870] G8 household part of the seen waits: queue n, ticks, worker n, ticks (AutLive.cs QueueNote, WorkerWait)
        // [898-902] tier-2 H7-H10 (AutShadow.cs, docs/126 §3; autt2drag/autt2cache/autt2tuning/autt2lod): H8 `cache` aborts, H9 `tuning` aborts, H7 drag answers, H10 LOD answers, H10 answers past the game's 60-tick recompute.
        // [938-943] tier-2 P2 (AutShadow.cs, docs/data/tier2-rows35-handoff.md; autt2cewarm/autt2frontdoor/autt2suite): CauseEffect data pre-warm fills (sim thread), CauseEffect worker aborts, front-door answers, front-door aborts, BuyModeSuite answers, BuyModeSuite aborts.
        // [944] tier-2 P3 (AutShadow.cs, autt2callpet): CallPet self Tests run on the worker with the shared mSimPickerList put back.
        // [950]-[952] boatreplanlog gangway fixups (HitchCause.cs, docs/247 §13.3 R1): lines, ms with the Sim unseen, ms not unseen.
        // [903-927] autt2l0 (AutLocalProbe.cs, docs/data/tier2-local-phase-handoff.md L0): local-phase searches, us and pairs by actor class (household, seen, unseen), side entries, no-local floor, unseen B8 reasons, span ms, nested, exceptions
        // [892-897] dancefloorlistdiag (DanceFloorDiag.cs, docs/253 §7): FindAndRouteOntoADanceFloorWithFollower calls, returns, "Collection was modified" throws (all three also with the switch off), route steps checked, steps with a mismatch, of those with the list freed.
        // [928-929] dancefloorlistguard (DanceFloorDiag.cs, docs/253 §11): guarded FindAndRouteOntoADanceFloorWithFollower calls, list changes the snapshot loop got past (EA's next MoveNext would have thrown).
        // [871] watchdoglockskip (RouteFixes.cs CheckIdle, docs/250 §7.4): idle checks skipped while a route job held +0x918 (wdLockSkip)
        // [872-875] watchdogasync A1 (RouteFixes.cs CheckIdle, RouteBackoff.cs OwnerTaskSlot, docs/250 §7.1): own STARTs sent, started by
        // ts3native, refused before sending (A1 on, Sim eligible, ts3native would not start it), sim ticks the A1 checks took
        // [930-932] watchdogasync: A1 verdicts dropped as stale (H4Core.WdFresh), shadow sync re-checks run, shadow verdicts that differ
        // [969-984] replanlog (ReplanLog.cs, docs/261 §6 L1, ts3native [RouteAsync] ReplanLog=1): own Replans and ms, calls and ms
        // per scope (household, selected, seenNpc, unseen, unknown), bursts, call lines, lines dropped, would start but Kinds
        // [1069-1072] hoursmemo exactness (ScriptHotSpots.cs, docs/276 §1.3): hits verified (hoursmemoverify), bar hits with the live
        // bachelor-party check, misses because the lot's venue key changed within the tick, answers not kept (not the bar formula)
        // [1073-1084] hhsearchprobe (HhSearchProbe.cs, HhSearchRec in AutLocalProbeCore.cs, docs/276 §2.3 A): household searches,
        // local, pushed, no pick, not pushed under a user / autonomous head, same pick again, short gap, no-push runs, Sims, max per Sim, exceptions
        // [1135-1138] burnttilefloor (BurntTileFloor.cs, docs/277 §4.1): burnt tiles left out (no floor), lists that lost one, lists left empty, errors
        // [1115-1134] hhsearchprobe docs/277 §5: scope, why enqueued (immediate ok/fail, empty <5/5-10/10-60/>60 sim-min, none), gap ms bins, no pick after >60, L0 pairs, active lot, pets
        // [1057] householdtoo (PlayerSims.cs HouseholdExempt, docs/271, ts3native [Experimental] HouseholdToo=1): household, selected or
        // unknown-scope asks the flag let through to a realistic-only path (worker answers, exact go-live); 0 with the flag off
        // One size for every slot a fix may take (maintainer slots below 2000, contributor ranges up to 3999 since 2026-11-17, 16 KB,
        // docs/registry.md), so a new fix never needs the array resized by hand.
        const int kCounterSlots = 4000;
        static int[] sCountersCache;
        static int[] PatchCounters()
        {
            if (sCountersCache != null) return sCountersCache; // hooks call this hundreds of times a second
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            if (c == null || c.Length < kCounterSlots)
            {
                // a shorter array (left by an older build in this session) is copied into a full-size one
                int[] full = new int[kCounterSlots];
                if (c != null) Array.Copy(c, full, c.Length);
                c = full;
                AppDomain.CurrentDomain.SetData("ts3fixes.counters", c);
            }
            return sCountersCache = c;
        }

        // [AutShadow] Unseen=1 (UserDiag op 8, no icall: with [AutShadow] off ts3native registers none). Asked once.
        static int sIniUnseen = -1;
        static bool IniUnseen()
        {
            if (sIniUnseen < 0)
            {
                try { sIniUnseen = DiagSend(8, 0) ? 1 : 0; }
                catch (Exception) { sIniUnseen = 0; }
            }
            return sIniUnseen == 1;
        }

        // A fix is on when the test switch says so, else per the tuning (Instantiator).
        // docs/605 §2: ~360 call sites, ~2.7 ms/s of sim-thread self time measured in the lookup below. The answer for a
        // name changes only when (a) the switch table changes: every writer bumps "ts3fixes.switchgen" (SwitchesChanged:
        // agent patches / event_cost / reflect_set, NameCacheCheck), a new key also changes its Count, and names with an
        // entry are read from the table on each call (an older agent does not bump); (b) the tuning
        // is written: answers are cached only after ApplyTuned with the tuning set (the loader has run), and every world
        // load finished starts a new table; (c) IniUnseen's one ask, memoized by itself. So ShortcutOn answers from a
        // SwitchCache built for the current generation and count, else the slow path below and stores its answer.
        // is written: answers are cached only after ApplyTuned with the tuning set (the loader has run), and every world
        static Dictionary<string, bool> sSwitches;
        static SwitchCache sSwCache;     // null: not cacheable yet (tuning) or turned off
        static int[] sSwGen;             // "ts3fixes.switchgen" [0], shared with the agent
        static bool sSwCacheOff;         // A/B aid: reflect_set Ts3Fixes.Fixes.sSwCacheOff true (reflect_set bumps the generation)

        // A name with a table entry is stored as Live and read from the table on every call: an agent build without the
        // generation bump can overwrite an entry without changing Count, but cannot add or remove one unseen (only
        // NameCacheCheck removes, and it bumps). A name with no entry is stored with its tuning default.
        static bool ShortcutOn(string name)
        {
            SwitchCache t = SwCurrent();
            bool on;
            if (t != null)
            {
                int v = t.Get(name);
                if (v == SwitchCache.Live) { if (sSwitches.TryGetValue(name, out on)) return on; }
                else if (v >= 0) return v == SwitchCache.On;
            }
            else SwTable();
            if (sSwitches.TryGetValue(name, out on)) { if (t != null) t.Put(name, SwitchCache.Live); return on; }
            on = SwDefault(name);
            if (t != null) t.Put(name, on ? SwitchCache.On : SwitchCache.Off);
            return on;
        }

        // The table for the switch table as it is now. One read of sSwCache: its own Gen / Count say what it was built for.
        static SwitchCache SwCurrent()
        {
            SwitchCache t = sSwCache;
            if (t != null && t.Gen == sSwGen[0] && t.Count == sSwitches.Count) return t;  // t set implies both are
            return SwRenew();
        }

        static SwitchCache SwRenew()
        {
            SwTable();
            if (sSwCacheOff || !sTunedDone || !Instantiator.TuningSet) { sSwCache = null; return null; }
            SwitchCache t = new SwitchCache(sSwGen[0], sSwitches.Count);
            sSwCache = t;
            return t;
        }

        static void SwTable()
        {
            if (sSwitches == null)
            {
                sSwitches = AppDomain.CurrentDomain.GetData("ts3fixes.switches") as Dictionary<string, bool>;
                if (sSwitches == null) { sSwitches = new Dictionary<string, bool>(); AppDomain.CurrentDomain.SetData("ts3fixes.switches", sSwitches); }
            }
            if (sSwGen == null)
            {
                int[] g = AppDomain.CurrentDomain.GetData("ts3fixes.switchgen") as int[];
                if (g == null || g.Length < 1) { g = new int[1]; AppDomain.CurrentDomain.SetData("ts3fixes.switchgen", g); }
                sSwGen = g;
            }
        }

        // Every write to the switch table (or to a tuning field at run time) calls this: the next ShortcutOn starts a new table.
        internal static void SwitchesChanged()
        {
            SwTable();
            unchecked { sSwGen[0]++; }
        }

        // SwitchCacheCheck: every name asked so far plus the table's keys, cached answer vs slow answer (twice: fill, hits),
        // an override flip on a name nobody uses (set with a bump, overwritten without one, bumped, removed), and the cost
        // per call of both paths.
        public static string SwitchCacheCheck()
        {
            SwTable();
            string[] names = SwNames();
            if (names.Length == 0) names = new string[] { "gohome", "broadcast", "detailprio", "hovertraintest" };
            int bad = 0, n = 0;
            string firstBad = null;
            for (int pass = 0; pass < 2; pass++)
                foreach (string s in names)
                {
                    n++;
                    bool a = ShortcutOn(s), b = ShortcutOnSlow(s);
                    if (a != b) { bad++; if (firstBad == null) firstBad = s; }
                }
            const string probe = "switchcacheprobe";
            bool p0 = ShortcutOn(probe);
            sSwitches[probe] = true; SwitchesChanged(); bool p1 = ShortcutOn(probe), p1b = ShortcutOn(probe);
            sSwitches[probe] = false; bool p2 = ShortcutOn(probe);  // overwritten with no bump (an older agent): read live
            SwitchesChanged(); bool p2b = ShortcutOn(probe);
            sSwitches.Remove(probe); SwitchesChanged(); bool p3 = ShortcutOn(probe);
            bool flip = !p0 && p1 && p1b && !p2 && !p2b && !p3;
            SwitchCache t = SwCurrent();
            string cached = t == null ? "cache off (sSwCacheOff=" + sSwCacheOff + ", tuned=" + sTunedDone + ", tuning set=" + Instantiator.TuningSet + ")" : "cache on";
            foreach (string s in names) ShortcutOn(s);  // fill again for the timing and the probe length
            t = SwCurrent();
            string probeLen = t == null ? "" : ", stored " + t.Used + ", longest probe " + t.MaxProbe();
            uint t0 = SrcNowUs();
            for (int r = 0; r < 20; r++) foreach (string s in names) ShortcutOnSlow(s);
            uint t1 = SrcNowUs();
            for (int r = 0; r < 20; r++) foreach (string s in names) ShortcutOn(s);
            uint t2 = SrcNowUs();
            int calls = 20 * Math.Max(1, names.Length);
            return "switchcache: " + cached + ", names " + names.Length + ", compared " + n + ", mismatches " + bad + (firstBad != null ? " (first " + firstBad + ")" : "")
                + ", override flip ok=" + flip + probeLen + "; timing: slow " + ((t1 - t0) * 1000u / (uint)calls) + " ns, cached " + ((t2 - t1) * 1000u / (uint)calls) + " ns per call"
                + "; verdict: " + (bad == 0 && flip ? "PASS" : "FAIL");
        }

        // The names asked so far (the current table's keys: every site that ran since the last change) plus the switch
        // table's own keys, each once.
        static string[] SwNames()
        {
            List<string> l = new List<string>();
            SwitchCache t = SwCurrent();
            if (t != null) l.AddRange(t.Keys());
            foreach (string k in sSwitches.Keys) if (!l.Contains(k)) l.Add(k);
            return l.ToArray();
        }

        // The uncached answer: the switch table's entry, else the tuning default.
        static bool ShortcutOnSlow(string name)
        {
            SwTable();
            bool on;
            if (sSwitches.TryGetValue(name, out on)) return on;
            return SwDefault(name);
        }

        // The tuning default of a switch name (no table entry).
        static bool SwDefault(string name)
        {
            switch (name)
            {
                case "gohome": return Instantiator.kGoHomeShortcut;
                case "rabbithole": return Instantiator.kBuildingTripShortcut;
                case "visit": case "visitlot": case "gotolot": case "gotocommunity": return Instantiator.kLotTripShortcut;
                case "broadcast": return Instantiator.kBroadcasterThrottle;
                case "virtualhome": return Instantiator.kVirtualHomeShortcut;
                case "failreplan": return Instantiator.kNoRouteFailWalkOffscreen;
                case "watchdog": return Instantiator.kStuckWatchdog;
                case "watchdogidle": return Instantiator.kStuckWatchdogIdle;
                case "backoff": return Instantiator.kRouteFailureBackoff;
                case "stations": return Instantiator.kNoStationDetourOffscreen;
                case "portaladaptorleak": return Instantiator.kPortalAdaptorLeak;
                case "casgrid": case "casmemo": return Instantiator.kCasGrid;
                case "spatialcache": return Instantiator.kSpatialCache;
                case "routablecache": return Instantiator.kRoutableCache;
                case "stuckcheckbusy": return Instantiator.kStuckCheckBusy;
                case "stuckcheckasync": return Instantiator.kStuckCheckAsync;
                case "safespotcap": return Instantiator.kSafeSpotCap;
                case "farthestsorted": return Instantiator.kFarthestLotSorted;
                case "indoorcap": return Instantiator.kIndoorPositionCap;
                case "fastreftable": return Instantiator.kFastRefTable;
                case "cachedreflection": return Instantiator.kCachedReflection;
                case "motivebar": return Instantiator.kMotiveBarImageCache;
                case "buffgrid": return Instantiator.kBuffGridInPlace;
                case "namecache": return Instantiator.kSimNameCache;
                case "detailprio": return Instantiator.kDetailPriorityCache;
                case "hoursmemo": return Instantiator.kHoursMemo;
                case "venuememo": return Instantiator.kVenueMemo;
                case "venuememoverify": return false; // verify switch: test runs only
                case "permamemo": return Instantiator.kPermaMoodletMemo;  // exact memo, off (ExactMemos.cs, docs/245 §5 H)
                case "buffremovenoop": return Instantiator.kBuffRemoveNoOp;  // exact early-out, off (ExactMemos.cs, docs/95 §5.5)
                case "catnamecache": return Instantiator.kCatalogNameCache;  // exact memo, off (ExactMemos.cs, docs/96 B2)
                case "autroommemo": return Instantiator.kAutonomyRoomMemo;  // exact memo, off (AutRoomMemo.cs, docs/97 §7 H2)
                case "autslicetime": return Instantiator.kAutonomySliceTime;  // measure only, off (AutRoomMemo.cs, docs/97 §7 H5)
                case "invcache": return Instantiator.kPortalInvocationCache;  // exact, off (ScriptHotSpots.cs, docs/115)
                case "zdemptyout": return Instantiator.kZombieDanceEmptyOut;  // exact early-out, off (WriterMemos.cs, docs/275 Z1)
                case "decaymemo": return Instantiator.kDecayMultMemo;  // exact memo, off (WriterMemos.cs, docs/275 M1)
                case "zdemptyoutverify": case "decaymemoverify": return false; // verify switches: test runs only
                case "permamemoverify": case "catnamecacheverify": case "autroommemoverify": case "invcacheverify": return false; // verify switches: test runs only
                case "caneearly": return Instantiator.kCaneEarlyOut;
                case "tooltipthrottle": return Instantiator.kTooltipThrottle;
                case "detailprioverify": case "hoursmemoverify": return false; // verify switches: test runs only, no tuning
                case "safespotreuse": return Instantiator.kSafeSpotReuse;
                case "safespotreuseverify": return false; // verify switch: test runs only
                case "wildflowerprobe": return Instantiator.kWildflowerProbe;
                case "wildflowertask": return Instantiator.kWildflowerTask;
                case "doormirror": return Instantiator.kDoorMirror;
                case "autshadow": return Instantiator.kAutShadow || IniUnseen();
                case "autoffenv": case "autoffbcast": case "autoffdry": return false; // docs/208 offload Mode R (AutOffload.cs): test runs only
                // docs/208 §8 apply switches: unset read true here (default branch) while the agent showed them off
                case "autoffenvapply": case "autoffbcastapply": case "autoffnoref": return false;
                case "autoffunseen": return false; // docs/208 §10 unseen-only gate on the env / bcast apply (only acts with the apply switches)
                case "envlistmemo": case "bcposdry": return false; // docs/274 R1 / R2 dry probes (EnvBcProbes.cs): counts only, test runs
                case "autofftick": return false; // docs/206 §15 Mode R tick item: test runs only
                case "autofft2": case "autofft2ic": case "autofflot": return false; // docs/208 §9 Mode R items (tier-2 rows, closest lot): test runs only
                case "autt2inuse": case "autt2fire": case "autt2dragwarm": return false; // docs/219 §3.1 tier-2 P1 worker guards + drag pre-warm (AutShadow.cs): test runs only
                case "autt2drag": case "autt2cache": case "autt2tuning": case "autt2lod": return false; // docs/126 §3 tier-2 H7-H10 worker hooks (AutShadow.cs): test runs only
                case "autt2cewarm": case "autt2frontdoor": case "autt2suite": return false; // tier-2 P2 pre-warm and guards (AutShadow.cs): test runs only
                case "autt2callpet": return false; // tier-2 P3 CallPet worker-owned picker list (AutShadow.cs): test runs only
                case "autt2l0": return false;  // tier-2 L0 local-phase probe (AutLocalProbe.cs): measurement only, test runs
                case "hhsearchprobe": return false;  // docs/276 §2.3 A household search-rate probe (HhSearchProbe.cs): measurement only, test runs
                case "autlive": return IniUnseen(); // docs/124 §17 go-live consume ([AutShadow] Mode=3): test runs, or ini Unseen=1
                case "autlivetune": return false; // docs/124 §3.1 tuning rule: drop a go-live result when an allow-listed pair's tuning fingerprint (AutLiveCore.TunFp) changed since the request (only acts with autlive); test runs only
                case "autliveseen": case "autliveseendry": return false; // docs/124 §17.12: with autseengame, a proven seen NPC takes the exact go-live path (dry: compare only); household and unknown scope never; test runs only
                case "autlivelod": return false; // docs/124 §3.1 LOD rule: drop a go-live result when Autonomy.mbHighLOD flipped since the request (only acts with autlive); test runs only
                case "autlivelate": return Instantiator.kAutLiveLate; // docs/217 L1 late consume (only acts with autlive)
                case "autliveguard": return Instantiator.kAutLiveGuard; // docs/124 §17.10 go-live cost guard (only acts with autlive)
                case "autunseenpace": return Instantiator.kAutUnseenPace; // docs/222 §8 unseen-direct pacing (only acts with autunseen)
                case "autunseenrh": return Instantiator.kAutUnseenRabbitHoleGame; // docs/222 §10 rabbit-hole pairs to the game's C1 (only acts with autunseen)
                // docs/222 §11.8 V6 (only act with autunseen): on with ini Unseen=1, as in the gate 2 credit arm (Rodney 2026-11-08:
                // ON in release, gate 2 BP +6.4 / IP +12.2 ms/s); a seen actor (household, selected) runs the game's own search
                case "autseengame": case "autunseenpacecredit": return IniUnseen();
                case "autunseencmp": return Instantiator.kAutUnseenCmp; // docs/222 §9 Dry per-definition compare (only with Dry + autunseen)
                case "autunseen": return IniUnseen(); // docs/222 unseen-direct go-live (only acts with autlive, acting): test runs, or ini Unseen=1
                case "replandefer": return Instantiator.kReplanDefer;
                case "replanspread": return Instantiator.kReplanSpread;
                case "autshadowverify": return false; // verify switch: test runs only
                case "overwatchspread": return Instantiator.kOverwatchSpread;
                case "overwatchchain": return Instantiator.kOverwatchChain;
                case "overwatchspreadverify": case "overwatchspreadfakeupstream": return false; // test runs only
                case "errortrapspread": return Instantiator.kErrorTrapSpread;
                case "errortrapspreadverify": case "errortrapspreadfakeupstream": return false; // test runs only
                case "autledger": return false; // ledger probe switch (docs/51 mono ledger): test runs only
                case "unseenenv": return Instantiator.kUnseenEnvThrottle;
                case "unseenmotives": return Instantiator.kUnseenMotiveThrottle;
                case "unseenpark": return Instantiator.kUnseenPark;
                case "unseenbroadcast": return Instantiator.kUnseenBroadcastThrottle;
                case "unseenprobe": return false; // probe switch: test runs only
                case "wakeprobe": return false; // probe switch: test runs only (WakePark.cs, docs/211)
                case "unseencost": return false; // probe switch: test runs only (UnseenCost.cs, docs/227)
                case "unseenposcache": return true; // perf cache (UnseenTable.cs, docs/227 §7): on since v0.2.5 with [UnseenTable]
                case "unseentablecopy": return false; // A/B baseline (UnseenTable.cs, docs/227 §9): on = the per-tick copy of every row
                case "wakepark": return Instantiator.kWakePark;
                case "wakepoll": return Instantiator.kWakePoll;
                case "easpreadlots": return Instantiator.kEaSpreadLots;
                case "easpreadpets": return Instantiator.kEaSpreadPets;
                case "easpreaddecay": return Instantiator.kEaSpreadDecay;
                case "easpreadlotsverify": case "easpreaddecayverify": return false; // verify switches: test runs only
                case "unseencars": return Instantiator.kUnseenCarThrottle;
                case "routesrc": return Instantiator.kRouteSources;
                case "watchdoglockskip": return false;  // docs/247 §12.4, docs/250 §7.4: runtime only, default off
                // docs/250 §7.1 A1: OFF since rc1110b (2026-11-09) until the wdasync-fix-1110 tests (Instantiator.kWatchdogAsync)
                case "watchdogasync": return Instantiator.kWatchdogAsync;
                case "watchdogasyncshadow": return false;  // correctness arm only: re-runs each A1 check sync and counts mismatches
                case "detailprioexact": return Instantiator.kDetailPriorityExact;  // docs/605 §3 (DetailPrioExact.cs), exact, off
                case "doorgen": return Instantiator.kDoorGen;  // DoorGen.cs: bump ts3native's door generation when a door answer input changes (DoorMemoX=3)
                case "alarmtime": case "boatreplanlog": case "owresettime": case "synctime": return false; // docs/247 §9: logging, hooks only with kHitchCauseHooks
                case "legreplanskip": return Instantiator.kLegReplanSkip;
                case "bouncerskip": return Instantiator.kBouncerSlotSkip;
                case "idlememo": return Instantiator.kIdlePlanMemo;
                case "stuckchecknear": return Instantiator.kStuckCheckNear;
                case "failrepeat": return Instantiator.kFailRepeat;
                case "jobyield": return Instantiator.kJobYield;
                case "drivertaskasync": return Instantiator.kDriverTaskAsync;
                case "stuckcheckown": return Instantiator.kStuckCheckOwn;
                case "overwatchstuckslice": return Instantiator.kOverwatchStuckSlice;
                case "seasonmarkercache": return Instantiator.kSeasonMarkerCache;
                case "safespotyield": return Instantiator.kSafeSpotYield;
                case "disposespread": return Instantiator.kDisposeSpread;
                case "overwatchstucknotice": return Instantiator.kOverwatchStuckNoticeSummary;
                case "preplancb": return Instantiator.kPrePlanCallback;
                case "stuckcheckcap": return Instantiator.kStuckCheckCap;
                case "owprobeplans": return Instantiator.kOverwatchIgnoreStuckProbes;
                case "unseenfbstuck": return Instantiator.kUnseenFallbackStuck;
                case "unseenfbplan": return Instantiator.kUnseenFallbackPlan;
                case "unseenfbteleport": return Instantiator.kUnseenFallbackTeleport;
                case "unseenfbsites": return Instantiator.kUnseenFallbackSites;
                case "unseenfbpre": return Instantiator.kUnseenFallbackPre;
                case "unseenfbarrive": return Instantiator.kUnseenFallbackArrive;
                case "unseenfbleave": return Instantiator.kUnseenFallbackLeave;
                case "unseenfbtrip": return Instantiator.kUnseenFallbackTrip;
                case "unseenfbtriphold": return Instantiator.kUnseenFallbackTripHold;
                case "unseenfbloopdet": return Instantiator.kUnseenFallbackLoopDetect;
                case "goalhhexempt": return Instantiator.kGoalHouseholdExempt;
                case "unseenfbloopbreak": return Instantiator.kUnseenFallbackLoopBreak;
                case "unseenfbfinish": return Instantiator.kUnseenFallbackFinish;
                case "unseenroute": return Instantiator.kUnseenRouteRelaxed;
                case "unseenboat": return Instantiator.kUnseenBoat;
                case "unseenboatstart": return Instantiator.kUnseenBoatStart;
                case "posturepopcycle": return Instantiator.kPosturePopCycle;
                case "seasonimposter": return Instantiator.kSeasonImposter;
                case "seasonimpostercold": return Instantiator.kSeasonImposterCold;
                case "preferredservice": return Instantiator.kPreferredService;
                case "socialworkeronlot": return Instantiator.kSocialWorkerOnLot;
                case "eanullguards": return Instantiator.kEaNullGuards;
                case "showstagethrowables": return Instantiator.kShowStageThrowables;
                case "firepitchair": return Instantiator.kFirePitChair;
                case "ageupkeepoutfits": return Instantiator.kAgeUpKeepOutfits;
                case "outfitwaitpoll": return Instantiator.kOutfitWaitPoll;
                case "outfitwaitend": return Instantiator.kOutfitWaitEnd;
                case "uniformbackup": return Instantiator.kUniformBackup;
                case "deadrefguards": return Instantiator.kDeadRefGuards;
                case "bakesaleseller": return Instantiator.kBakeSaleSeller;
                case "nullservice": return Instantiator.kNullService;
                case "rabbitholedoor": return Instantiator.kRabbitHoleDoor;
                case "photosession": return Instantiator.kPhotoSession;
                case "eggspot": return Instantiator.kEggSpot;
                case "graduationtable": return Instantiator.kGraduationTable;
                case "jazzparam": return Instantiator.kJazzParam;
                case "buffupdate": return Instantiator.kBuffUpdate;
                case "carnoroute": return Instantiator.kCarNoRoute;
                case "vehparkreserve": return Instantiator.kVehParkReserve;
                case "vehgetinleak": return Instantiator.kVehGetInLeak;
                case "vehseatdestroy": return Instantiator.kVehSeatDestroy;
                case "butlerpayalarm": return Instantiator.kButlerPayAlarm;
                case "vehfrankentaxi": return Instantiator.kVehFrankenTaxi;
                case "vehprotestcar": return Instantiator.kVehProtestCar;
                case "buyiconleak": return Instantiator.kBuyIconLeak;
                case "casrowleak": return Instantiator.kCasRowLeak;
                case "compositorleak": return Instantiator.kCompositorLeak;
                case "genealogycycle": return Instantiator.kGenealogyCycle;
                case "spouseunlink": return Instantiator.kSpouseUnlink;
                case "relremovesafe": return Instantiator.kRelRemoveSafe;
                case "familyinvghost": return Instantiator.kFamilyInvGhost;
                case "sellcursorleak": return Instantiator.kSellCursorLeak;
                case "attractionalarm": return Instantiator.kAttractionAlarm;
                case "uncleguard": return Instantiator.kUncleGuard;
                case "mooddrift": return Instantiator.kMoodDrift;
                case "dnpdisposelistener": return Instantiator.kDnpDisposeListener;
                case "seasondup": return Instantiator.kSeasonDup;
                case "dreamrootorphan": return Instantiator.kDreamRootOrphan;
                case "blogorphan": return Instantiator.kBlogOrphan;
                case "dnpupdateguard": return Instantiator.kDnpUpdateGuard;
                case "buycellrecycle": return Instantiator.kBuyCellRecycle;
                case "buyclearrecycle": return Instantiator.kBuyClearRecycle;
                case "buygridsweep": return Instantiator.kBuyGridSweep;
                case "moodpending": return Instantiator.kMoodPending;
                case "joyridecostume": return Instantiator.kJoyrideCostume;
                case "alarmkilllog": return Instantiator.kAlarmKillLog;
                case "etderefdiag": return Instantiator.kEtDerefDiag;
                case "socialfinish": return Instantiator.kSocialFinish;
                case "socialreceivefail": return Instantiator.kSocialReceiveFail;
                case "petfireplan": return Instantiator.kPetFirePlan;
                case "splashroof": return Instantiator.kSplashRoof;
                case "burnttilefloor": return Instantiator.kBurntTileFloor;
                case "shuffleboardbcast": return Instantiator.kShuffleboardBcast;
                case "cookclicklistener": return Instantiator.kCookClickListener;
                case "viproomrefresh": return Instantiator.kVipRoomRefresh;
                case "courselistener": return Instantiator.kCourseListener;
                case "observatorymsg": return Instantiator.kObservatoryMsg;
                case "bubblepipeguard": return Instantiator.kBubblePipeGuard;
                case "buffstate": return Instantiator.kBuffState;
                case "storeworldevents": return Instantiator.kStoreWorldEvents;
                case "bowlinglane": return Instantiator.kBowlingLane;
                case "geniebanish": return Instantiator.kGenieBanish;
                case "spacesimpartner": return Instantiator.kSpaceSimPartner;
                case "bandjam": return Instantiator.kBandJam;
                case "statueline": return Instantiator.kStatueLine;
                case "cleanflag": return Instantiator.kCleanFlag;
                case "birdtvnull": return Instantiator.kBirdTvNull;
                case "gohomewalk": return Instantiator.kGoHomeWalk;
                case "photoboothfunds": return Instantiator.kPhotoBoothFunds;
                case "shelllighting": return Instantiator.kShellLighting;
                case "bonfirespice": return Instantiator.kBonfireSpice;
                case "djcleanup": return Instantiator.kDjCleanup;
                case "vampirehuntglow": return Instantiator.kVampireHuntGlow;
                case "hottubwoohoofx": return Instantiator.kHotTubWoohooFx;
                case "bribeguard": return Instantiator.kBribeGuard;
                case "oppdeadlisteners": return Instantiator.kOppDeadListeners;
                case "daycarequit": return Instantiator.kDaycareQuit;
                case "privateeyecase": return Instantiator.kPrivateEyeCase;
                case "weddingcancel": return Instantiator.kWeddingCancel;
                case "ghostspawnargs": return Instantiator.kGhostSpawnArgs;
                case "moveinpartner": return Instantiator.kMoveInPartner;
                case "reaperflow": return Instantiator.kReaperFlow;
                case "serviceleave": return Instantiator.kServiceLeave;
                case "visitgreetalarm": return Instantiator.kVisitGreetAlarm;
                case "fieldtripadult": return Instantiator.kFieldTripAdult;
                case "autonomyfailblock": return Instantiator.kAutonomyFailBlock;
                case "visittoolate": return Instantiator.kVisitTooLate;
                case "spoonreset": return Instantiator.kSpoonReset;
                case "homeworkhelp": return Instantiator.kHomeworkHelp;
                case "grouptalkstale": return Instantiator.kGroupTalkStale;
                case "convmerge": return Instantiator.kConvMerge;
                case "portraitpose": return Instantiator.kPortraitPose;
                case "picniccamera": return Instantiator.kPicnicCamera;
                case "upgradefailalarm": return Instantiator.kUpgradeFailAlarm;
                case "repairtimecache": return Instantiator.kRepairTimeCache;
                case "burglaridle": return Instantiator.kBurglarIdle;
                case "gigwaitleave": return Instantiator.kGigWaitLeave;
                case "sculptpose": return Instantiator.kSculptPose;
                case "trainboost": return Instantiator.kTrainBoost;
                case "tagunseen": return Instantiator.kTagUnseen;
                case "eatoutsidecount": return Instantiator.kEatOutsideCount;
                case "spotcheck": return Instantiator.kSpotCheck;
                case "safespotpool": return Instantiator.kSafeSpotPool;
                case "queueproximity": return Instantiator.kQueueProximity;
                case "subroutefail": return Instantiator.kSubRouteFail;
                case "mopunreachable": return Instantiator.kMopUnreachable;
                case "petpuddletile": return Instantiator.kPetPuddleTile;
                case "initstacktrace": return Instantiator.kInitStackTrace;
                case "weatherstonelistener": return Instantiator.kWeatherStoneListener;
                case "weathermanagerthrow": return Instantiator.kWeatherManagerThrow;
                case "rainsheltercheap": return Instantiator.kRainShelterCheap;
                case "pondclearscan": return Instantiator.kPondClearScan;
                case "soakedstat": return Instantiator.kSoakedStat;
                case "outerwearautoloop": return Instantiator.kOuterwearAutoLoop;
                case "outerwearrestore": return Instantiator.kOuterwearRestore;
                case "hovertraintest": return Instantiator.kHoverTrainTest;
                case "newcartest": return Instantiator.kNewCarTest;
                case "spoonslavechecks": return Instantiator.kSpoonSlaveChecks;
                case "petwakebed": return Instantiator.kPetWakeBed;
                case "slavewakereason": return Instantiator.kSlaveWakeReason;
                case "handsetreset": return Instantiator.kHandsetReset;
                case "flowersinuse": return Instantiator.kFlowersInUse;
                case "uselistprune": return Instantiator.kUseListPrune;
                case "chestuselist": return Instantiator.kChestUseList;
                case "resetinusefree": return Instantiator.kResetInUseFree;
                case "putdowninuse": return Instantiator.kPutDownInUse;
                case "autonomyinuseblock": return Instantiator.kAutonomyInUseBlock;
                case "npcbusypartner": return Instantiator.kNpcBusyPartner;
                case "sandwichalarm": return Instantiator.kSandwichAlarm;
                case "dreamcleanup": return Instantiator.kDreamCleanup;
                case "vehiclefxhelper": return Instantiator.kVehicleFxHelper;
                case "prankditchempty": return Instantiator.kPrankDitchEmpty;
                case "serviceassignexit": return Instantiator.kServiceAssignExit;
                case "serviceshowalarm": return Instantiator.kServiceShowAlarm;
                case "maildogchaser": return Instantiator.kMailDogChaser;
                case "lifeguardsurvey": return Instantiator.kLifeguardSurvey;
                case "moonriselistener": return Instantiator.kMoonRiseListener;
                case "holidaylights": return Instantiator.kHolidayLights;
                case "doorlockowner": return Instantiator.kDoorLockOwner;
                case "elevatorkitten": return Instantiator.kElevatorKitten;
                case "elevatorresetriders": return Instantiator.kElevatorResetRiders;
                case "elevatorsavedlog": return Instantiator.kElevatorSavedLog;
                case "starlightreset": return Instantiator.kStarLightReset;
                case "moondiallistener": return Instantiator.kMoonDialListener;
                case "snublistener": return Instantiator.kSnubListener;
                case "elevatorridertimeout": return Instantiator.kElevatorRiderTimeout;
                case "rolenullguard": return Instantiator.kRoleNullGuard;
                case "homeworkskillbonus": return Instantiator.kHomeworkSkillBonus;
                case "horseranchchallenge": return Instantiator.kHorseRanchChallenge;
                case "digsiteemptypick": return Instantiator.kDigSiteEmptyPick;
                case "oppchainworld": return Instantiator.kOppChainWorld;
                case "teleportcommunitylot": return Instantiator.kTeleportCommunityLot;
                case "photosubjectstars": return Instantiator.kPhotoSubjectStars;
                case "residentspawnslot": return Instantiator.kResidentSpawnSlot;
                case "talkoccupationchance": return Instantiator.kTalkOccupationChance;
                case "shelldoorsummon": return Instantiator.kShellDoorSummon;
                case "disposebase": return Instantiator.kDisposeBase;
                case "boardingschoolmoney": return Instantiator.kBoardingSchoolMoney;
                case "freshclothingresume": return Instantiator.kFreshClothingResume;
                case "twinbellyshape": return Instantiator.kTwinBellyShape;
                case "boatstaleswimmer": return Instantiator.kBoatStaleSwimmer;
                case "oppcategoryprotect": return Instantiator.kOppCategoryProtect;
                case "nocommercialcar": return Instantiator.kNoCommercialCar;
                case "scavengernolot": return Instantiator.kScavengerNoLot;
                case "partylotlistener": return Instantiator.kPartyLotListener;
                case "weddinglistener": return Instantiator.kWeddingListener;
                case "innerbeautyonce": return Instantiator.kInnerBeautyOnce;
                case "sprelcount": return Instantiator.kSpRelCount;
                case "spcollectnull": return Instantiator.kSpCollectNull;
                case "spclonedict": return Instantiator.kSpCloneDict;
                case "splotlookup": return Instantiator.kSpLotLookup;
                case "spbucketindex": return Instantiator.kSpBucketIndex;
                case "spcreatemovein": return Instantiator.kSpCreateMoveIn;
                case "splotdensity": return Instantiator.kSpLotDensity;
                case "sproommatejob": return Instantiator.kSpRoommateJob;
                case "spoccultpick": return Instantiator.kSpOccultPick;
                case "spjobatwork": return Instantiator.kSpJobAtWork;
                case "sprecheck": return Instantiator.kSpRecheck;
                case "sppensionprompt": return Instantiator.kSpPensionPrompt;
                case "hhcleanupdup": return Instantiator.kHhCleanupDup;
                case "virtualhomeshare": return Instantiator.kVirtualHomeShare;
                case "celebalarmnull": return Instantiator.kCelebAlarmNull;
                case "disposevirtualslot": return Instantiator.kDisposeVirtualSlot;
                case "agingalarmcancel": return Instantiator.kAgingAlarmCancel;
                case "virtualmoveindup": return Instantiator.kVirtualMoveInDup;
                case "petpoolghost": return Instantiator.kPetPoolGhost;
                case "eatpostonce": return Instantiator.kEatPostOnce;
                case "motivemultonce": return Instantiator.kMotiveMultOnce;
                case "fightbroadcastend": return Instantiator.kFightBroadcastEnd;
                case "reactioncopy": return Instantiator.kReactionCopy;
                case "carpoolschoolskip": return Instantiator.kCarpoolSchoolSkip;
                case "tvsyncalarm": return Instantiator.kTvSyncAlarm;
                case "photofunds": return Instantiator.kPhotoFunds;
                case "ltrhistorybits": return Instantiator.kLtrHistoryBits;
                case "goodbyepure": return Instantiator.kGoodbyePure;
                case "bathcleanup": return Instantiator.kBathCleanup;
                case "tvwatchskill": return Instantiator.kTvWatchSkill;
                case "computergametype": return Instantiator.kComputerGameType;
                case "moodtankreset": return Instantiator.kMoodTankReset;
                case "reactiongrumpy": return Instantiator.kReactionGrumpy;
                case "trainsimgone": return Instantiator.kTrainSimGone;
                case "stereoupgrade": return Instantiator.kStereoUpgrade;
                case "buffremovedelayed": return Instantiator.kBuffRemoveDelayed;
                case "tvdisclistener": return Instantiator.kTvDiscListener;
                case "fridgetestnull": return Instantiator.kFridgeTestNull;
                case "cancelposturedef": return Instantiator.kCancelPostureDef;
                case "facialidleweight": return Instantiator.kFacialIdleWeight;
                case "bathsituationfx": return Instantiator.kBathSituationFx;
                case "socialnoeffect": return Instantiator.kSocialNoEffect;
                case "guitargnomeboost": return Instantiator.kGuitarGnomeBoost;
                case "distressgate": return Instantiator.kDistressGate;
                case "moodidleflavor": return Instantiator.kMoodIdleFlavor;
                case "protestexit": return Instantiator.kProtestExit;
                case "jogterrain": return Instantiator.kJogTerrain;
                case "lifeguardquit": return Instantiator.kLifeguardQuit;
                case "frolicexit": return Instantiator.kFrolicExit;
                case "shoodef": return Instantiator.kShooDef;
                case "igloolookats": return Instantiator.kIglooLookats;
                case "gohereboatlist": return Instantiator.kGoHereBoatList;
                case "bouncerrole": return Instantiator.kBouncerRole;
                case "tricksocials": return Instantiator.kTrickSocials;
                case "fearfaintundo": return Instantiator.kFearFaintUndo;
                case "lotprune": return Instantiator.kLotPrune;
                case "werewolffind": return Instantiator.kWerewolfFind;
                case "copyhomeworkfx": return Instantiator.kCopyHomeworkFx;
                case "spelltargetend": return Instantiator.kSpellTargetEnd;
                case "foodtraygrill": return Instantiator.kFoodTrayGrill;
                case "compositionnull": return Instantiator.kCompositionNull;
                case "petdreamtimer": return Instantiator.kPetDreamTimer;
                case "awkwardscience": return Instantiator.kAwkwardScience;
                case "chessdefeated": return Instantiator.kChessDefeated;
                case "privacyothers": return Instantiator.kPrivacyOthers;
                case "gigmaster": return Instantiator.kGigMaster;
                case "intmultiple": return Instantiator.kIntMultiple;
                case "doorprivacynull": return Instantiator.kDoorPrivacyNull;
                case "leafburnfail": return Instantiator.kLeafBurnFail;
                case "catchpreyodds": return Instantiator.kCatchPreyOdds;
                case "picnicants": return Instantiator.kPicnicAnts;
                case "loyaldoglay": return Instantiator.kLoyalDogLay;
                case "visitlisteners": return Instantiator.kVisitListeners;
                case "sciencefxstop": return Instantiator.kScienceFxStop;
                case "daycareliking": return Instantiator.kDaycareLiking;
                case "kegbreakage": return Instantiator.kKegBreakage;
                case "romancecooldown": return Instantiator.kRomanceCooldown;
                case "lightcustom": return Instantiator.kLightCustom;
                case "eatpreycancel": return Instantiator.kEatPreyCancel;
                case "meltalarm": return Instantiator.kMeltAlarm;
                case "newspaperdef": return Instantiator.kNewspaperDef;
                case "leaveconvnull": return Instantiator.kLeaveConvNull;
                case "vacationalarms": return Instantiator.kVacationAlarms;
                case "cleanupvfx": return Instantiator.kCleanupVfx;
                case "lookatcopy": return Instantiator.kLookAtCopy;
                case "lottotooltip": return Instantiator.kLottoTooltip;
                case "traiticondislike": return Instantiator.kTraitIconDislike;
                case "roleunprotect": return Instantiator.kRoleUnprotect;
                case "putdownplaced": return Instantiator.kPutDownPlaced;
                case "nullsituation": return Instantiator.kNullSituation;
                case "lightningweights": return Instantiator.kLightningWeights;
                case "plantdormantflip": return Instantiator.kPlantDormantFlip;
                case "plantdormanttime": return Instantiator.kPlantDormantTime;
                case "harvestextras": return Instantiator.kHarvestExtras;
                case "shroomslots": return Instantiator.kShroomSlots;
                case "harvestregion": return Instantiator.kHarvestRegion;
                case "wellstealcooldown": return Instantiator.kWellStealCooldown;
                case "oppfailcharge": return Instantiator.kOppFailCharge;
                case "tempforecast": return Instantiator.kTempForecast;
                case "mountfishcost": return Instantiator.kMountFishCost;
                case "firefightercount": return Instantiator.kFirefighterCount;
                case "repairmanskill": return Instantiator.kRepairmanSkill;
                case "roundupreturn": return Instantiator.kRoundUpReturn;
                case "petharvestvfx": return Instantiator.kPetHarvestVfx;
                case "suatgradebonus": return Instantiator.kSuatGradeBonus;
                case "cattoypickerrel": return Instantiator.kCatToyPickerRel;
                case "dogswimslots": return Instantiator.kDogSwimSlots;
                case "fragmentreconstruct": return Instantiator.kFragmentReconstruct;
                case "labobjectpush": return Instantiator.kLabObjectPush;
                case "businessplannerrun": return Instantiator.kBusinessPlannerRun;
                case "futuredescendantsize0": return Instantiator.kFutureDescendantSize0;
                case "junglegymexitqueue": return Instantiator.kJungleGymExitQueue;
                case "wildhorselot": return Instantiator.kWildHorseLot;
                case "harvesterunripe": return Instantiator.kHarvesterUnripe;
                case "hottubseatposture": return Instantiator.kHotTubSeatPosture;
                case "teenpartybreakup": return Instantiator.kTeenPartyBreakUp;
                case "dogswimaroundlist": return Instantiator.kDogSwimAroundList;
                case "tutorballoonalarm": return Instantiator.kTutorBalloonAlarm;
                case "chemistryvfx": return Instantiator.kChemistryVfx;
                case "dartboardvfx": return Instantiator.kDartboardVfx;
                case "wardrobewandvfx": return Instantiator.kWardrobeWandVfx;
                case "petdroolvfx": return Instantiator.kPetDroolVfx;
                case "timeportalvfx": return Instantiator.kTimePortalVfx;
                case "haystackvfx": return Instantiator.kHayStackVfx;
                case "hayscattertime": return Instantiator.kHayScatterTime;
                case "hauntedgnomeinv": return Instantiator.kHauntedGnomeInv;
                case "houdinignomeleak": return Instantiator.kHoudiniGnomeLeak;
                case "showstagefxend": return Instantiator.kShowStageFxEnd;
                case "soakedpetselfreact": return Instantiator.kSoakedPetSelfReact;
                case "tagbagsurfaceleak": return Instantiator.kTagBagSurfaceLeak;
                case "equestriantrophyname": return Instantiator.kEquestrianTrophyName;
                case "rubbleuselist": return Instantiator.kRubbleUseList;
                case "backpacklotleak": return Instantiator.kBackpackLotLeak;
                case "streetartmasterboost": return Instantiator.kStreetArtMasterBoost;
                case "bubblingspringsreset": return Instantiator.kBubblingSpringsReset;
                case "floorswitchuselist": return Instantiator.kFloorSwitchUseList;
                case "donationbuffchance": return Instantiator.kDonationBuffChance;
                case "vampirefruitsnack": return Instantiator.kVampireFruitSnack;
                case "fallenrockcollected": return Instantiator.kFallenRockCollected;
                case "spellbooksummonload": return Instantiator.kSpellBookSummonLoad;
                case "chocfountaingeyser": return Instantiator.kChocFountainGeyser;
                case "hypnotrickrelease": return Instantiator.kHypnoTrickRelease;
                case "voodoofairyfadein": return Instantiator.kVoodooFairyFadeIn;
                case "saunatubclear": return Instantiator.kSaunaTubClear;
                case "slotmachinedefs": return Instantiator.kSlotMachineDefs;
                case "treewishingwell": return Instantiator.kTreeWishingWell;
                case "playpentalkcast": return Instantiator.kPlaypenTalkCast;
                case "ovenmenunull": return Instantiator.kOvenMenuNull;
                case "fvstandskillcurve": return Instantiator.kFvStandSkillCurve;
                case "pokerdeadplayer": return Instantiator.kPokerDeadPlayer;
                case "roulettehackreset": return Instantiator.kRouletteHackReset;
                case "coopeggquality": return Instantiator.kCoopEggQuality;
                case "megaphonerun": return Instantiator.kMegaphoneRun;  // docs/608 §2 (WalkStyleLeak77.cs), EA bug fix, off
                case "dragoncurserun": return Instantiator.kDragonCurseRun;  // docs/608 §3 (WalkStyleLeak77.cs), EA bug fix, off
                case "vhomewalk": return Instantiator.kVirtualHomeWalk;  // docs/608 §4 (WalkStyleLeak77.cs), EA bug fix, off
                case "speedskiplevel": return Instantiator.kSpeedSkipLevel;  // docs/609 §2 (GameSpeed78.cs), EA bug fix, off
                case "speedskipnoactor": return Instantiator.kSpeedSkipNoActor;  // docs/609 §3 (GameSpeed78.cs), EA bug fix, off
                case "speedrecalclock": return Instantiator.kSpeedRecalcLock;  // docs/609 §4 (GameSpeed78.cs), EA bug fix, off
                case "speedlockowner": return Instantiator.kSpeedLockOwner;  // docs/609 §5 (GameSpeed78.cs), EA bug fix, off
                case "spoilbroadcast": return Instantiator.kSpoilBroadcast;  // docs/615 §1 (Scan85.cs), EA bug fix, off
                case "weatherstonerelisten": return Instantiator.kWeatherStoneRelisten;  // docs/615 §2 (Scan85.cs), EA bug fix, off
                case "birthdayagerecord": return Instantiator.kBirthdayAgeRecord;  // docs/615 §3 (Scan85.cs), EA bug fix, off
                case "heartbrokenbcast": return Instantiator.kHeartBrokenBcast;  // docs/609 §7 (GameSpeed78.cs), EA bug fix, off
                case "rhexitunseen": return Instantiator.kRhExitUnseen;  // docs/613 §2 (StuckJobs83.cs), EA bug fix, off
                case "tipsnpcend": return Instantiator.kTipsNpcEnd;  // docs/613 §3 (StuckJobs83.cs), EA bug fix, off
                case "barputdownwait": return Instantiator.kBarPutDownWait;  // docs/614 §1 (Scan84.cs), EA bug fix, off
                case "putdownsurfaceuse": return Instantiator.kPutDownSurfaceUse;  // docs/614 §2 (Scan84.cs), EA bug fix, off
                case "swiperestore": return Instantiator.kSwipeRestore;  // docs/614 §3 (Scan84.cs), EA bug fix, off
                case "unicornspawnfail": return Instantiator.kUnicornSpawnFail;  // docs/614 §4 (Scan84.cs), EA bug fix, off
                case "datadiscputback": return Instantiator.kDataDiscPutBack;  // docs/614 §5 (Scan84.cs), EA bug fix, off
                case "spawnerskip": return Instantiator.kSpawnerSkip;  // docs/610 §2 (SpawnerMapTag79.cs), speedup with EA's failure ending, off
                case "maptagtaskalive": return Instantiator.kMapTagTaskAlive;  // docs/610 §3 (SpawnerMapTag79.cs), EA bug fix, off
                case "lotmgrcallbacks": return Instantiator.kLotMgrCallbacks;  // docs/611 §2 (LotCallbacks80.cs), EA bug fix, off
                case "fireplaceherb": return Instantiator.kFireplaceHerb;  // docs/611 §3 (LotCallbacks80.cs), EA bug fix, off
                case "rummagebcast": return Instantiator.kRummageBcast;  // docs/611 §4 (LotCallbacks80.cs), EA bug fix, off
                case "stairwaitend": return Instantiator.kStairWaitEnd;  // docs/612 §2 (StairDup82.cs), EA bug fix, off
                case "simdisposelink": return Instantiator.kSimDisposeLink;  // docs/612 §3 (StairDup82.cs), EA bug fix, off
                case "alarmzeroperiod": return Instantiator.kAlarmZeroPeriod;  // docs/609 §6 (GameSpeed78.cs), EA bug fix (hang guard), off
                case "birthroute": return Instantiator.kBirthRoute;
                case "putdownplace": return Instantiator.kPutDownPlace;
                case "awardkeep": return Instantiator.kAwardKeep;
                case "ceremonyspot": return Instantiator.kCeremonySpot;
                case "eventcrowdring": return Instantiator.kEventCrowdRing;
                case "eventrouteretry": return Instantiator.kEventRouteRetry;
                case "simfestkeep": return Instantiator.kSimFestKeep;
                case "weddingarchkeep": return Instantiator.kWeddingArchKeep;
                case "weddingcleanup": return Instantiator.kWeddingCleanup;
                case "hsgradkeep": return Instantiator.kHsGradKeep;
                case "unigradkeep": return Instantiator.kUniGradKeep;
                case "bsrefund": return Instantiator.kBsRefund;
                case "comboprotest": return Instantiator.kComboProtest;
                case "combovenue": return Instantiator.kComboVenue;
                case "combosituation": return Instantiator.kComboSituation;
                case "combojob": return Instantiator.kComboJob;
                case "comboprotestlot": return Instantiator.kComboProtestLot;
                case "comboopp": return Instantiator.kComboOpp;
                case "hiddenroomnpc": return Instantiator.kHiddenRoomNpc;
                case "hiddenroomspawn": return Instantiator.kHiddenRoomSpawn;
                case "aptleave": return Instantiator.kAptLeave;
                case "partyoutcome": return Instantiator.kPartyOutcome;
                case "partymotive": return Instantiator.kPartyMotive;
                case "filtergridleak": return Instantiator.kFilterGridLeak;
                case "compositorghost": return Instantiator.kCompositorGhost;
                case "caspuckevents": return Instantiator.kCasPuckEvents;
                case "famcursorleak": return Instantiator.kFamCursorLeak;
                case "casexitsweep": return Instantiator.kCasExitSweep;
                case "seasonlistener": return Instantiator.kSeasonListener;
                case "casfiltergrid": return Instantiator.kCasFilterGrid;
                case "convturn": return Instantiator.kConvTurn;
                case "npcdriverpool": return Instantiator.kNpcDriverPool;
                case "bookcarry": return Instantiator.kBookCarry;
                case "ageupabort": return Instantiator.kAgeUpAbort;
                case "rewardgiver": return Instantiator.kRewardGiver;
                case "datingprofile": return Instantiator.kDatingProfile;
                case "leavejobdispose": return Instantiator.kLeaveJobDispose;
                case "invnotyet": return Instantiator.kInvNotYet;
                case "metricskillmissing": return Instantiator.kMetricSkillMissing;
                case "fireworksfizzle": return Instantiator.kFireworksFizzle;
                case "bookhand": return Instantiator.kBookHand;
                case "bookhanddiag": return Instantiator.kBookHandDiag;
                case "petpoolorphan": return Instantiator.kPetPoolOrphan;
                case "errortrapsims": return Instantiator.kErrorTrapSims;
                case "celebritydeadsim": return Instantiator.kCelebrityDeadSim;
                case "seasonsdeadsim": return Instantiator.kSeasonsDeadSim;
                case "ghostreactdeadsim": return Instantiator.kGhostReactDeadSim;
                case "casuihandlers": return Instantiator.kCasUiHandlers;
                case "errortraplookup": return Instantiator.kErrorTrapLookup;
                case "errortrapctl": return Instantiator.kErrorTrapCtl;
                case "dreamnodedeadsim": return Instantiator.kDreamNodeDeadSim;
                case "overwatchdeadsim": return Instantiator.kOverwatchDeadSim;
                case "simrefsdeadsim": return Instantiator.kSimRefsDeadSim;
                case "outfitwaitsaved": return Instantiator.kOutfitWaitSaved;
                case "deadsimcount": return Instantiator.kDeadSimCount;
                case "deadsimhollow": return Instantiator.kDeadSimHollow;
                case "dnpsubjectcache": return Instantiator.kDnpSubjectCache;
                case "hudunsub": return Instantiator.kHudUnsub;
                case "mapviewkeep": return Instantiator.kMapViewKeep;
                case "tinyuieventreg": return Instantiator.kTinyUiEventReg;
                case "customctlsweep": return Instantiator.kCustomCtlSweep;
                case "exitcbsweep": return Instantiator.kExitCbSweep;
                case "dnppetkeys": return Instantiator.kDnpPetKeys;
                case "dnpobjkeys": return Instantiator.kDnpObjKeys;
                case "reltablesapart": return Instantiator.kRelTablesApart;
                case "ageupswap": return Instantiator.kAgeUpSwap;
                case "asyncjigsync": return Instantiator.kAsyncJigSync;
                case "deadrefsweep": return Instantiator.kDeadRefSweep;
                case "savecleanupaggressive": return Instantiator.kSaveCleanupAggressive;
                case "vacexport": return Instantiator.kVacExport;
                case "vacstoreguard": return Instantiator.kVacStoreGuard;
                case "vacreturnskip": return Instantiator.kVacReturnSkip;
                case "vacteendelete": return Instantiator.kVacTeenDelete;
                case "vacexitreturn": return Instantiator.kVacExitReturn;
                case "vaclistenernull": return Instantiator.kVacListenerNull;
                case "vacimportfix": return Instantiator.kVacImportFix;
                case "vaclistenerpad": return Instantiator.kVacListenerPad;  // EA bug fix, off (EaVacation.cs, docs/210 §6)
                case "spawncapcount": return Instantiator.kSpawnCapCount;  // exact, off until its probe run (SpawnCap.cs, docs/147 §5 D1)
                case "spawncapprobe": case "routelen": return false;  // diagnostics only (SpawnCap.cs, RouteLen.cs)
                case "menumaven": return Instantiator.kMenuMaven;
                case "dancefloorlistdiag": return false;  // diagnostics only (DanceFloorDiag.cs, docs/253 §7)
                case "boatgetoutdiag": return Instantiator.kBoatGetOutDiag;  // diagnostics only, off (BoatGetOutDiag.cs, docs/257 §7)
                case "boatgetoutguard": return Instantiator.kBoatGetOutGuard;  // EA bug fix, off until the boat-getout-stress queue (BoatGetOutDiag.cs, docs/257 §9)
                case "unseenoutfitdefer": return Instantiator.kUnseenOutfitDefer;  // issue #451, off (UnseenOutfitDefer.cs, docs/586)
                case "boatparkgone": return Instantiator.kBoatParkGone;  // EA bug fix, on by default (BoatParkGone.cs, docs/257)
                case "deadrefcooking": return Instantiator.kDeadRefCooking;  // EA bug fix, off (DeadRefs.cs phase 14, docs/205 §4)
                case "deadrefconvo": return Instantiator.kDeadRefConvo;  // EA bug fix, off (DeadRefs.cs phase 15, docs/517 §4)
                case "takeariskguard": return Instantiator.kTakeARiskGuard;  // EA bug fix, off (TakeARiskGuard.cs, docs/529 §4)
                case "minihomelot": return Instantiator.kMiniHomeLot;  // EA bug fix, on (MiniHomeLot.cs, docs/266)
                case "dancefloorlistguard": return Instantiator.kDanceFloorListGuard;  // EA bug fix, ON in release (Rodney 2026-11-09; DanceFloorDiag.cs, docs/253 §12)
                case "tpdoorrights": return true;  // F1's unseen-teleport lot test by the Sim's own door rights, EA's one-way sentinel (UnseenTeleportGoal.cs, docs/584 §7)
                case "tpaptgoal": return true;  // F1: an unseen NPC's own home apartment / shell lot judged by the goal room only (hidden rooms still refused; UnseenTeleportGoal.cs, docs/584 §8)
                case "compactionfail": return false;  // test only (Compaction.cs, docs/204 §6): the reload throws after the screen is up
                case "spotspread": return true;
                case "routefaillog": return true;  // ON in release (Rodney 2026-11-08): helps triage player reports in early releases
                case "asyncroute": case "asyncreplan": case "asyncreplanforeign": case "overwatchstuck": case "savedstacks": case "gm6trace": case "savecleanup": return true;  // were on through the old default: true
                default: return false;  // new switches default off (AGENTS.md); a typo stays off
            }
        }

        static void Patch(Type target, string method, Type hook, string replacement, string stub)
        {
            Patch(target, method, null, hook, replacement, stub);
        }

        // args picks one overload of target.method (null when the name is unique). True when the patch went in.
        static void PatchSigCheck(MethodInfo t, MethodInfo h, string what)
        {
            if (t == null || h == null) return;
            bool tv = t.ReturnType == typeof(void), hv = h.ReturnType == typeof(void);
            if (tv != hv) throw new InvalidOperationException(what + " " + h.Name + " returns " + h.ReturnType.Name + ", original returns " + t.ReturnType.Name + ": not patched");
            int tn = t.GetParameters().Length + (t.IsStatic ? 0 : 1), hn = h.GetParameters().Length + (h.IsStatic ? 0 : 1);
            if (tn != hn) throw new InvalidOperationException(what + " " + h.Name + " takes " + hn + " stack arguments, original " + tn + ": not patched");
            if (!tv && t.ReturnType.IsValueType != h.ReturnType.IsValueType)
                throw new InvalidOperationException(what + " " + h.Name + " returns " + h.ReturnType.Name + ", original returns " + t.ReturnType.Name + ": not patched");
        }

        // PetPoolOrphan's and petpoolghost's on PetPool.RandomRemovePet) chains: the later hook's stub receives the earlier
        static bool Patch(Type target, string method, Type[] args, Type hook, string replacement, string stub)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                MethodInfo t = args == null ? target.GetMethod(method, all) : target.GetMethod(method, all, null, args, null), r = hook.GetMethod(replacement, all);
                if (Compiled(t)) throw new InvalidOperationException("already compiled");
                // a swapped body keeps the hook's signature: a void hook for a method that returns a value makes every
                // caller's pop/use of the result underflow Mono's transform (transform.c:1170 `td.sp >= td.stack`, int3 at
                // 0x58D9B6: the game dies on the first call, buycellrecycle v1/v2 2026-11-11), so refuse such pairs
                PatchSigCheck(t, r, "hook");
                if (stub != null) PatchSigCheck(t, hook.GetMethod(stub, all), "stub");
                string swName = AutShadowCore.MemberName(t);  // G-M6: callers' IL hashes name t as before the swap
                if (swName != null) AutShadowCore.SwapNames[t.MethodHandle.Value] = swName;
                if (stub != null) { MethodInfo s = hook.GetMethod(stub, all); SwapMethod(s, t); StubDirect(s); }
                SwapMethod(t, r);
                sStubs.Add(t);  // docs/242: a frame in the hook's body saves as the hook's class, which often cannot find it again
                sPatched[t.MethodHandle.Value] = hook;  // docs/128 G-M4: our own swap, accepted by the closure sweep (by MonoMethod: GetMethods hands out other MethodInfo objects)
                AppliedLog().Add(target.Name + "." + method + ": ok");
                return true;
            }
            catch (Exception e) { AppliedLog().Add(TypeName(target) + "." + method + ": " + e.Message); return false; }
        }

        // Off-screen go-home (PLAN fix b): a townie whose trip home nobody can see skips the walk/drive and appears on its
        // home lot. Only the route is replaced: the game's own lead-in (carpool wait, leaving the visit and group) runs, and
        // the success bookkeeping matches a real arrival, so whatever the Sim does next is unchanged. Anything not clearly
        // safe runs the original. Counters: [0] runs, [1] shortcuts, [2] no safe spot on the lot, [3] loop guard.
        public class GoHomeHook : GoHome
        {
            static int[] sCounters;
            public bool RunHook()
            {
                // docs/560 gohomewalk: EA requests WalkStyle on the Sim and never unrequests it
                int ghBefore = -1;
                if (WalkStyle != Sims3.Gameplay.Actors.Sim.WalkStyle.AutoSelect && ShortcutOn("gohomewalk")) ghBefore = GhWalkCount(Actor, WalkStyle);
                try
                {
                    if (sCounters == null) sCounters = PatchCounters();
                    sCounters[0]++;
                    if (!ShortcutOn("gohome") || !Unseen(Actor, Target)) return RunOrig();
                    // a Sim shortcut twice within 10 sim-minutes is in some loop the game drives: let it walk
                    ulong id = Actor.SimDescription.SimDescriptionId;
                    long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                    long last;
                    if (sLastShortcut.TryGetValue(id, out last) && now - last < kLoopTicks)
                    {
                        sCounters[3]++;
                        return RunOrig();
                    }
                    Sims3.SimIFace.Vector3 pos, fwd;
                    if (!SafeSpot(Actor, Target, Target.GetRandomPosition(false, true), out pos, out fwd))
                    {
                        sCounters[2]++;
                        return RunOrig();
                    }
                    // spotcheck (default off): Unseen(Actor, Target) judged the home lot, but the spot is a random outdoor point
                    // anywhere on it (up to ~45 m from the centre on a 64 m lot): one in view = the Sim walks
                    if (ShortcutOn("spotcheck") && !UfGoalUnseen(pos)) { sCounters[2702]++; return RunOrig(); }
                    // docs/202 §3.6.4 unseenfbtriphold: D's walk-time hold first; given up = the game's own trip
                    if (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) == 2 && !UfTripHold(Actor, Target, pos)) return RunOrig();
                    sLastShortcut[id] = now;
                    ShortcutLog(Actor.FullName + " -> " + Target.Name + " from " + (Actor.LotCurrent != null ? Actor.LotCurrent.Name : "street"));
                    // same lead-in as GoHome.Run
                    if (!Sims3.Gameplay.Objects.Vehicles.CarpoolManager.WaitForCarpool(Actor)) return false;
                    Sims3.Gameplay.Situations.VisitSituation.OnSimLeavingLot(Actor);
                    Sims3.Gameplay.Situations.GroupingSituation.RemoveSimFromGroupSituation(Actor);
                    PlaceSim(Actor, pos, fwd, "home " + Target.Name);
                    sCounters[1]++;
                    NumSuccess++;
                    BeginCommodityUpdates();
                    EndCommodityUpdates(true);
                    return true;
                }
                finally { if (ghBefore >= 0) GhWalkRelease(Actor, WalkStyle, ghBefore); }
            }
            public bool RunOrig() { return false; } // receives GoHome.Run's original header
            static readonly Dictionary<ulong, long> sLastShortcut = new Dictionary<ulong, long>();
            static readonly long kLoopTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(10f, Sims3.Gameplay.Utilities.TimeUnit.Minutes);

            // Townie, alone, standing, going to its own ordinary lot, and neither it nor that lot near the camera or the
            // active household. Houseboats and apartment shells keep their own arrival code.
            bool Unseen(Sims3.Gameplay.Actors.Sim sim, Lot home)
            {
                long t = UcBegin(kUcGoHome, sim != null ? sim.ObjectId.Value : 0);  // docs/227 probe
                bool r = UnseenBody(sim, home);
                UcEnd(kUcGoHome, t);
                return r;
            }
            bool UnseenBody(Sims3.Gameplay.Actors.Sim sim, Lot home)
            {
                if (home == null || !TownieUnseen(sim, SimFollowers)) return false;
                // GoHome.Run may add the household horse as a follower after this point; keep pet households on foot
                if (sim.Household != null && sim.Household.Pets.Count > 0) return false;
                if (sim.LotHome != home || sim.LotCurrent == home || home == Household.ActiveHouseholdLot) return false;
                if (!HouseboatOk(home) || home.BuildableShells.Length > 0) return false;
                return FarFromViewers(home.Position);
            }
        }

        // Outside the camera's reach (a radius that grows as the camera zooms out) and not near any active-household Sim.
        static bool FarFromViewers(Sims3.SimIFace.Vector3 p)
        {
            long t = UcBegin(kUcFar, 0);  // docs/227 probe
            bool r = FarFromViewersBody(p);
            UcEnd(kUcFar, t);
            return r;
        }
        // docs/227 probe run 2 (bp-uc-probe): 3.55 us a call. Now squared distances in plain floats (Vector3 operator- and
        // Length() are calls, Length a double sqrt), the tick test inline (RefreshViewers only on a new tick), and a per-tick
        // answer cache by the exact position (the answer only depends on the point and this tick's viewers: same answer, up to
        // float rounding exactly at the 80 m / reach edge). Broadcasters, lots and doors ask the same points many times a tick.
        static bool FarFromViewersBody(Sims3.SimIFace.Vector3 p)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            bool r;
            uint bx = Bits(p.x), by = Bits(p.y), bz = Bits(p.z);
            if (sFarCache.Get(now, bx, by, bz, 0, out r)) return r;
            r = FarCompute(p.x, p.y, p.z);
            sFarCache.Put(now, bx, by, bz, 0, r);
            return r;
        }
        static bool FarCompute(float x, float y, float z)
        {
            float dx = x - sCamTarget.x, dy = y - sCamTarget.y, dz = z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            dx = x - sCamPos.x; dy = y - sCamPos.y; dz = z - sCamPos.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            const float kMin2 = kShortcutMinDistance * kShortcutMinDistance;
            for (int i = 0; i < sViewerCount; i++)
            {
                dx = x - sViewers[i].x; dy = y - sViewers[i].y; dz = z - sViewers[i].z;
                if (dx * dx + dy * dy + dz * dz < kMin2) return false;
            }
            return true;
        }
        static readonly UcPosCache sFarCache = new UcPosCache();

        // Camera and active-household positions, read once per sim tick (the broadcaster hook alone asks ~800 times/s).
        static long sViewTick = -1;
        static Sims3.SimIFace.Vector3 sCamTarget, sCamPos;
        static float sReach, sReach2;
        static Sims3.SimIFace.Vector3[] sViewers = new Sims3.SimIFace.Vector3[16];
        static int sViewerCount;
        // the caller tests the tick (probe run 2: 1373 calls/s at 0.77 us, almost all the same-tick early return plus the probe
        // pair around it); the refresh site now times only the real reads
        static void RefreshViewers(long now)
        {
            long t = UcBegin(kUcRefresh, 0);  // docs/227 probe
            RefreshViewersBody(now);
            UcEnd(kUcRefresh, t);
        }
        static void RefreshViewersBody(long now)
        {
            sViewTick = now;
            UcCount(kUcViewRead);
            sCamTarget = CameraController.GetTarget();
            sCamPos = CameraController.GetPosition();
            sReach = Math.Max(kShortcutMinDistance, 2f * Math.Abs(sCamPos.y - sCamTarget.y));
            sReach2 = sReach * sReach;
            sViewerCount = 0;
            if (Household.ActiveHousehold == null) return;
            foreach (Sims3.Gameplay.Actors.Sim s in Household.ActiveHousehold.AllActors)
            {
                if (s == null) continue;
                if (sViewerCount == sViewers.Length) Array.Resize(ref sViewers, sViewers.Length * 2);
                sViewers[sViewerCount++] = s.Position;
            }
        }

        const float kShortcutMinDistance = 80f;

        // A spot for a shortcut Sim on `lot` near `near`: on the lot (else the game still sees the Sim away) and passing the
        // game's Sim.IsPointInLotSafelyRoutable (a plan from the spot to the lot's corners and edge midpoints), so the Sim can
        // walk on from there instead of standing stuck. That check is a full route plan (362 ms once in Bridgeport, docs/04
        // F18), so its verdict is cached per 0.5 m cell and route profile (SpotVerdict). Spots next to the lot's verified ones are tried first,
        // rotating through them so arrivals spread out; then fresh spots from `near` (the entrance), whose cells may cost a
        // full check within the per-tick budget. No verified spot = false, and the caller runs the game's own trip.
        static bool SafeSpot(Sims3.Gameplay.Actors.Sim sim, Lot lot, Sims3.SimIFace.Vector3 near, out Sims3.SimIFace.Vector3 pos, out Sims3.SimIFace.Vector3 fwd)
        {
            SpotVerdicts v = Verdicts(lot);
            RouteProfile prof = ProfileOf(sim);
            // "spotspread" off = the earlier build: always start from the lot's last verified spot (A/B for the Bridgeport slowdown)
            bool spread = ShortcutOn("spotspread");
            bool reuse = ShortcutOn("safespotreuse");
            int[] c = PatchCounters();
            // safespotpool (default off): spots in a pool or under the ground outdoors are refused, so the caller runs EA's trip
            bool noPool = ShortcutOn("safespotpool");
            for (int i = 0; i < 2 + 3; i++)
            {
                bool fresh = i >= 2;
                if (!fresh && v.Good.Count == 0) continue;
                Sims3.SimIFace.Vector3 start = fresh ? near : v.Good[spread ? v.Next++ % v.Good.Count : v.Good.Count - 1];
                if (noPool && !fresh && BelowGroundOutdoors(start)) { c[2701]++; continue; }
                // A verified spot is itself an earlier FindGoodLocation result (a native placement search, hundreds of ms
                // in Isla Paradiso at the school run): answer with it and the facing stored then, while its cached verdict
                // (same cell and route profile; the lot's verdicts reset on a move-in, a moved lot or a day) still says
                // routable. A Sim already standing on the spot is not checked: placed Sims walk on at once.
                bool verify = false;
                Sims3.SimIFace.Vector3 sf;
                if (reuse && !fresh && prof.Ok && v.Fwds.TryGetValue(KeyOf(prof, start), out sf)
                    && LotManager.GetLotAtPoint(start) == lot && SpotVerdict(sim, lot, v, prof, start, false, false, false) == true)
                {
                    if (!VerifyNow("safespotreuseverify"))
                    {
                        c[90]++;
                        if (lot.IsHouseboatLot()) c[21]++;
                        pos = start; fwd = sf;
                        return true;
                    }
                    verify = true; // 1 in 8: also search, return the game's answer, count a different spot
                }
                c[91]++;
                bool found = GlobalFunctions.FindGoodLocation(sim, new World.FindGoodLocationParams(start), out pos, out fwd);
                if (ShortcutOn("safespotyield") && !SafeSpotYield(sim, near)) break;
                if (!found) continue;
                if (noPool && BelowGroundOutdoors(pos)) { c[2700]++; continue; }
                if (verify && (pos - start).Length() > 0.5f) c[92]++;
                if (LotManager.GetLotAtPoint(pos) != lot || SpotVerdict(sim, lot, v, prof, pos, fresh, false, true) != true) continue;
                if (reuse && prof.Ok && v.Fwds.Count < 64) v.Fwds[KeyOf(prof, pos)] = fwd;
                if (lot.IsHouseboatLot()) PatchCounters()[21]++;
                return true;
            }
            pos = fwd = Sims3.SimIFace.Vector3.Invalid;
            return false;
        }

        // safespotpool: a pool point, or an outdoor point under the ground (room 0, level < 0). FindGoodLocation can answer with
        // the floor of a pool and IsPointInLotSafelyRoutable passes it, so every verified spot of a lot can lie in the water
        // (Sunset Valley's community pool lot: those Sims' plans all failed). Basements (room > 0, level < 0) stay allowed.
        static bool BelowGroundOutdoors(Sims3.SimIFace.Vector3 p)
        {
            if (World.IsInPool(p)) return true;
            LotLocation l = LotLocation.Invalid;
            World.GetLotLocation(p, ref l, eRoomDefinition.Default);
            return l.mLevel < 0 && l.mRoom == 0;
        }

        // safespotyield (docs/74 §11 (b)): one native placement search per sim tick instead of up to 5 in one run. Yields
        // only in the Sim's own task (both hook callers, RabbitHoleHook.EnterHook and GoHomeHook.Run, and the lot trips run
        // in its interaction) and where a Sleep is safe (MayYield, RouteBackoff.cs). The Sim has not moved yet, so the yield
        // changes no outcome; after it the Sim must still be there and unseen, else false = the caller runs the game's trip.
        static bool SafeSpotYield(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 near)
        {
            int[] c = PatchCounters();
            if (Simulator.CurrentTask != sim.ObjectId || !MayYield()) { c[278]++; return true; }
            c[276]++;
            Simulator.Sleep(0);
            if (sim.HasBeenDestroyed || !TownieUnseen(sim, null) || !FarFromViewers(near)) { c[277]++; return false; }
            return true;
        }

        // Verdicts of the game's Sim.IsPointInLotSafelyRoutable for one lot (SafeSpot and the stuck watchdog). The check
        // plans with CreateRouteAsAdult(): the Sim's own species/gender flags with the age forced to adult, plus every option
        // SetRouteOptions sets from the Sim (real age, posture, ghost buff, mermaid, lifeguard, UFO, frozen ponds, pet,
        // horse, special-service pass-through, or none at all under an IHasCustomRouteOptions interaction), then water
        // planning = IsHuman; no valid-room limit (the adult flags skip it). So a verdict is keyed by the cell plus that
        // exact route profile (RouteProfile, read back from a route built the same way), never by IsHuman alone. Kept for
        // one sim-day, and dropped when the lot's household changes (a move-in or move-out refurnishes it), the lot moved
        // (a houseboat at another dock) or the clock went back (another save or world was loaded).
        class SpotVerdicts
        {
            public long Created;
            public ulong Household;
            public Sims3.SimIFace.Vector3 LotPos;
            public readonly Dictionary<VerdictKey, bool> Cells = new Dictionary<VerdictKey, bool>(new VerdictKeyComparer());
            public readonly List<Sims3.SimIFace.Vector3> Good = new List<Sims3.SimIFace.Vector3>();
            public readonly Dictionary<VerdictKey, bool> PlanOk = new Dictionary<VerdictKey, bool>(new VerdictKeyComparer()); // docs/202 D only: cells a normal plan proved (UfFillNote)
            public readonly Dictionary<VerdictKey, Sims3.SimIFace.Vector3> Fwds = new Dictionary<VerdictKey, Sims3.SimIFace.Vector3>(new VerdictKeyComparer()); // facing found with a verified spot (switch safespotreuse)
            public int Next;
        }
        static readonly Dictionary<ulong, SpotVerdicts> sSpotVerdicts = new Dictionary<ulong, SpotVerdicts>();
        static readonly long kVerdictTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Days);
        const int kMaxGoodSpots = 8, kMaxSpotChecksPerTick = 1;
        static long sSpotCheckTick = -1;
        static int sSpotChecks;

        static SpotVerdicts Verdicts(Lot lot)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            ulong hh = lot.Household != null ? lot.Household.HouseholdId : 0;
            SpotVerdicts v;
            if (!sSpotVerdicts.TryGetValue(lot.LotId, out v) || now < v.Created || now - v.Created > kVerdictTicks || v.Household != hh
                || (lot.Position - v.LotPos).Length() > 0.5f)
            {
                v = new SpotVerdicts();
                v.Created = now;
                v.Household = hh;
                v.LotPos = lot.Position;
                sSpotVerdicts[lot.LotId] = v;
            }
            return v;
        }

        // What the game's check feeds the planner for this Sim right now: the route's age/gender/species flags and every
        // RouteOption/RouteOption2 bit, read back from a route built exactly as IsPointInLotSafelyRoutable builds it. Not
        // cacheable (Ok = false) for a Sim with a parent (a boat route gets the boat as follower and ignore-object, which
        // the option bits do not show; vehicles, beds).
        struct RouteProfile { public bool Ok; public uint Flags; public long Opts, Opts2; }
        static Route.RouteOption[] sRouteOpts;
        static Route.RouteOption2[] sRouteOpts2;
        static RouteProfile ProfileOf(Sims3.Gameplay.Actors.Sim sim)
        {
            RouteProfile p = new RouteProfile();
            if (sim == null || sim.Parent != null || sim.SimRoutingComponent == null || sim.SimDescription == null) return p;
            if (sRouteOpts == null)
            {
                sRouteOpts = (Route.RouteOption[])Enum.GetValues(typeof(Route.RouteOption));
                sRouteOpts2 = (Route.RouteOption2[])Enum.GetValues(typeof(Route.RouteOption2));
            }
            Route r = sim.SimRoutingComponent.CreateRouteAsAdult();
            r.SetOption(Route.RouteOption.EnableWaterPlanning, sim.IsHuman); // as the game's check does next
            foreach (Route.RouteOption o in sRouteOpts) if (o != Route.RouteOption.None && r.GetOption(o)) p.Opts |= (long)o;
            foreach (Route.RouteOption2 o in sRouteOpts2) if (o != Route.RouteOption2.None && r.GetOption2(o)) p.Opts2 |= (long)o;
            p.Flags = (sim.SimDescription.SimFlags & 0xFFFFFF80u) | 0x20u; // CreateRouteAsAdult's flags
            p.Ok = true;
            return p;
        }

        // Cells are 0.5 m and floor-aligned, so a cell never straddles a wall or fence on a tile edge; y in 1 m steps keeps
        // floors apart.
        struct VerdictKey { public long Cell, Opts, Opts2; public uint Flags; }
        class VerdictKeyComparer : IEqualityComparer<VerdictKey> // explicit: the default one boxes structs in this Mono
        {
            public bool Equals(VerdictKey a, VerdictKey b) { return a.Cell == b.Cell && a.Opts == b.Opts && a.Opts2 == b.Opts2 && a.Flags == b.Flags; }
            public int GetHashCode(VerdictKey k) { return k.Cell.GetHashCode() ^ (k.Opts.GetHashCode() * 31) ^ (k.Opts2.GetHashCode() * 17) ^ (int)k.Flags; }
        }

        // The verdict for `pos`: cached when the profile is cacheable, else a full check when `mayCheck` and this sim tick's
        // budget (shared by SafeSpot and the watchdog) allow; null = no verdict now (SafeSpot treats it as unsafe, the
        // watchdog retries next tick). `recheckBad`: a cached "not routable" is re-checked (the watchdog acts on it; a stale
        // bad verdict must not reset a Sim). `addGood`: remember the spot for SafeSpot's spreading.
        // Counters: [37] cache hits, [38] misses, [39] full checks run.
        static VerdictKey KeyOf(RouteProfile p, Sims3.SimIFace.Vector3 pos)
        {
            VerdictKey key = new VerdictKey();
            key.Cell = ((long)((int)Math.Floor(pos.x * 2f) & 0xFFFFF) << 40) | ((long)((int)Math.Floor(pos.z * 2f) & 0xFFFFF) << 20)
                | (long)(uint)((int)Math.Floor(pos.y) & 0xFFFFF);
            key.Opts = p.Opts; key.Opts2 = p.Opts2; key.Flags = p.Flags;
            return key;
        }

        static bool? SpotVerdict(Sims3.Gameplay.Actors.Sim sim, Lot lot, SpotVerdicts v, RouteProfile p, Sims3.SimIFace.Vector3 pos, bool mayCheck, bool recheckBad, bool addGood)
        {
            int[] c = PatchCounters();
            VerdictKey key = KeyOf(p, pos);
            bool ok;
            if (p.Ok && v.Cells.TryGetValue(key, out ok) && (ok || !recheckBad)) { c[37]++; return ok; }
            c[38]++;
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sSpotCheckTick) { sSpotCheckTick = now; sSpotChecks = 0; }
            if (!mayCheck || sSpotChecks >= kMaxSpotChecksPerTick) return null;
            sSpotChecks++;
            c[39]++;
            sInOwnCheck = true;
            try { ok = sim.IsPointInLotSafelyRoutable(lot, pos); } finally { sInOwnCheck = false; }
            if (p.Ok) v.Cells[key] = ok;
            if (ok && addGood && p.Ok && v.Good.Count < kMaxGoodSpots) v.Good.Add(pos);
            return ok;
        }

        // Houseboat lots (PLAN fix b2): most of their cost is Sims boarding and leaving through gangways
        // (SimRoutingComponent.CheckAndUpdateForHouseboats, 5-8% in Isla Paradiso), not the boats moving, so trips to a
        // houseboat may be shortcut too, but only while it is docked and not moving.
        static bool HouseboatOk(Lot lot)
        {
            if (!lot.IsHouseboatLot()) return true;
            Sims3.Gameplay.Objects.Island.Houseboat b = lot.GetHouseboat();
            return b != null && b.IsDocked;
        }

        static void PlaceSim(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 pos, Sims3.SimIFace.Vector3 fwd)
        {
            sim.SimRoutingComponent.DisableDynamicFootprint();
            sim.SetPosition(pos);
            sim.SetForward(fwd);
        }

        // A townie on its own, on foot, standing, and not near anything the player can see. Shared by the trip shortcuts.
        static bool TownieUnseen(Sims3.Gameplay.Actors.Sim sim, List<Sims3.Gameplay.Actors.Sim> followers) { return TownieUnseen(sim, followers, false); }
        // hiddenOk: a Sim in a Late Night hidden room still counts (only the stuck watchdog's idle timer, which rescues it from there)
        static bool TownieUnseen(Sims3.Gameplay.Actors.Sim sim, List<Sims3.Gameplay.Actors.Sim> followers, bool hiddenOk)
        {
            long t = UcBegin(kUcTownie, sim != null ? sim.ObjectId.Value : 0);  // docs/227 probe
            bool r = TownieUnseenBody(sim, followers, hiddenOk);
            UcEnd(kUcTownie, t);
            return r;
        }
        static bool TownieUnseenBody(Sims3.Gameplay.Actors.Sim sim, List<Sims3.Gameplay.Actors.Sim> followers, bool hiddenOk)
        {
            if (IsPlayerSim(sim)) return false;  // household rule (PlayerSims.cs)
            if (followers != null && followers.Count > 0) return false;
            if (sim.Posture != sim.Standing || sim.IsInRidingPosture || sim.IsInBeingRiddenPosture) return false;
            // docs/267 F2: Late Night hidden rooms too (allowHidden), same EA lookup as the tomb-only one
            if (Sims3.Gameplay.Controllers.TombRoomManager.IsObjectInATombOrHiddenRoom(sim, !hiddenOk)) return false;
            int b = UtbBits(sim);  // docs/227 §6 central table
            if (b >= 0) return sUtbCheck ? UtbCompare(UtbUnseen(b), !UcOnScreen(sim) && FarFromViewers(sim.Position)) : UtbUnseen(b);
            if (UcOnScreen(sim)) return false;
            return FarFromViewers(sim.Position);
        }

        static void ShortcutLog(string line)
        {
            List<object> log = RecentLog();
            if (log.Count >= 40) log.RemoveAt(0);
            log.Add(line);
        }

        // Every Sim a shortcut moved this session (name -> last move and where it was placed), so a stuck Sim found
        // later can be matched for certain; the 40-line log above only covers the last few seconds.
        static Dictionary<string, object> MovedSims()
        {
            Dictionary<string, object> d = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
            if (d == null) { d = new Dictionary<string, object>(); AppDomain.CurrentDomain.SetData("ts3fixes.moved", d); }
            return d;
        }

        static void PlaceSim(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 pos, Sims3.SimIFace.Vector3 fwd, string what)
        {
            PlaceSim(sim, pos, fwd);
            MovedSims()[sim.FullName] = what + " @" + Math.Round(pos.x, 1) + "," + Math.Round(pos.z, 1) + " " + Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString();
        }

        // Trips into buildings (PLAN fix b3): work, school, shops, restaurants. An unseen townie heading to an unseen rabbit
        // hole far away is placed at a safe spot by the entrance, then the game's own method runs: it plans only the last
        // few metres and does everything after the walk unchanged (entering animation, success count, followers).
        // Counters: [4] calls, [5] shortcuts, [6] no safe spot.
        public class RabbitHoleHook : Sims3.Gameplay.Abstracts.RabbitHole
        {
            static int[] sCounters;
            public override RabbitHoleTuningClass RabbitHoleTuning { get { return null; } } // never instantiated; required by the base class
            public bool EnterHook(Sims3.Gameplay.Actors.Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            {
                if (sCounters == null) sCounters = PatchCounters();
                sCounters[4]++;
                Sims3.Gameplay.Abstracts.RabbitHole proxy = RabbitHoleProxy;
                if (ShortcutOn("rabbithole") && proxy != null && proxy.EnterSlots.Count > 0 && TownieUnseen(a, inst != null ? inst.SimFollowers : null))
                {
                    Lot lot = proxy.LotCurrent;
                    Sims3.SimIFace.Vector3 door = proxy.GetPositionOfSlot(proxy.EnterSlots[0]);
                    if (lot != null && !lot.IsWorldLot && a.LotCurrent != lot && (a.Position - door).Length() > kShortcutMinDistance
                        && !UcOnScreen(proxy) && FarFromViewers(door))
                    {
                        Sims3.SimIFace.Vector3 pos, fwd;
                        if (SafeSpot(a, lot, door, out pos, out fwd)
                            && (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) != 2 || UfTripHold(a, lot, pos)))  // docs/202 §3.6.4
                        {
                            string from = a.LotCurrent != null ? a.LotCurrent.Name : "street";
                            PlaceSim(a, pos, fwd, "building " + proxy.CatalogName);
                            sCounters[5]++;
                            ShortcutLog(a.FullName + " => " + proxy.CatalogName + " from " + from);
                        }
                        else sCounters[6]++;
                    }
                }
                return EnterOrig(a, inst, beforeEntering, canUseCar, routeMetaType, playRouteFailure);
            }
            public bool EnterOrig(Sims3.Gameplay.Actors.Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            { return false; } // receives RabbitHole.RouteNearEntranceAndEnterRabbitHole's original header
        }

        // Shared by the lot-trip shortcuts: counts a call in [idx], and if the townie and the lot are both unseen and far
        // apart, places the townie on a safe spot on the lot ([idx+1]) or gives up ([idx+2], no safe spot). Lots with their
        // own arrival code (moving houseboats, apartment shells, resorts, diving lots, the active household's lot) are left alone,
        // and so are townies whose household has pets (the game may add a pet or horse as a follower after the move, which
        // would then start far away).
        // newKind (docs/202 §3.6.4): a trip kind the shortcuts above miss (a mod subclass overriding Run); with D's walk-time hold
        // (UfTripHold, in transit) before the move, as covered kinds get with unseenfbtriphold.
        static bool ShortcutToLot(Sims3.Gameplay.Actors.Sim sim, Lot lot, string name, int idx) { return ShortcutToLot(sim, lot, name, idx, false); }
        static bool ShortcutToLot(Sims3.Gameplay.Actors.Sim sim, Lot lot, string name, int idx, bool newKind)
        {
            int[] c = PatchCounters();
            c[idx]++;
            int mode = UfPreCore.TripMode(newKind, ShortcutOn("unseenfbtrip"), ShortcutOn("unseenfbtriphold"));
            if (mode == 0 || !ShortcutOn(name) || sim == null || lot == null || lot.IsWorldLot || sim.LotCurrent == lot) return false;
            if (!HouseboatOk(lot) || lot.BuildableShells.Length > 0 || lot == Household.ActiveHouseholdLot) return false;
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return false;
            if (sim.Household != null && sim.Household.Pets.Count > 0) return false;
            if (!TownieUnseen(sim, null) || (sim.Position - lot.Position).Length() <= kShortcutMinDistance || !FarFromViewers(lot.Position)) return false;
            Sims3.SimIFace.Vector3 pos, fwd;
            if (!SafeSpot(sim, lot, lot.EntryPoint(), out pos, out fwd)) { c[idx + 2]++; return false; }
            // spotcheck (default off): the gate judged the lot's position; the spot itself must be out of view too
            if (ShortcutOn("spotcheck") && !UfGoalUnseen(pos)) { c[2703]++; return false; }
            if (mode == 2 && !UfTripHold(sim, lot, pos)) return false;  // the game's own trip runs (the Sim lost only the hold)
            string from = sim.LotCurrent != null ? sim.LotCurrent.Name : "street";
            PlaceSim(sim, pos, fwd, name + " " + lot.Name);
            c[idx + 1]++;
            ShortcutLog(sim.FullName + " => " + lot.Name + " (" + name + " from " + from + ")");
            return true;
        }

        // Walk-style comparer (PLAN fix a): SimRoutingComponent's private WalkStyleComparer.Compare looks both walk styles up
        // in a Dictionary<WalkStyle, int>, and with the default enum comparer each lookup boxes and calls Enum.Equals and
        // GetHashCode: 5% of script time as Enum.Equals alone in Bridgeport (docs/04 F7). The enum is sequential from 0, so
        // an array indexed by value gives the same priorities; a style missing from the table still throws, as before.
        // Always patched (routing may compile Compare before the first world load); the tuning kWalkStyleComparer is read per
        // call, because Apply runs before the tuning loader sets it (docs/124 §17.8): a player's False now runs the game's code.
        public class WalkStyleCompareHook
        {
            static int[] sPrio;
            public int CompareHook(Sims3.Gameplay.Actors.Sim.WalkStyle x, Sims3.Gameplay.Actors.Sim.WalkStyle y)
            {
                if (!Instantiator.kWalkStyleComparer) return CompareOrig(x, y);
                if (sPrio == null) sPrio = WalkStylePriorities();
                int px = (int)x < sPrio.Length ? sPrio[(int)x] : -1, py = (int)y < sPrio.Length ? sPrio[(int)y] : -1;
                if (px < 0 || py < 0) throw new KeyNotFoundException();
                return py - px;
            }
            public int CompareOrig(Sims3.Gameplay.Actors.Sim.WalkStyle x, Sims3.Gameplay.Actors.Sim.WalkStyle y) { return 0; } // receives Compare's original header

            static int[] WalkStylePriorities()
            {
                Sims3.Gameplay.Actors.Sim.WalkStyle[] order = (Sims3.Gameplay.Actors.Sim.WalkStyle[])typeof(Sims3.Gameplay.Actors.SimRoutingComponent)
                    .GetField("kWalkStylePriorities", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                int max = 0;
                foreach (Sims3.Gameplay.Actors.Sim.WalkStyle w in order) max = Math.Max(max, (int)w);
                int[] p = new int[max + 1];
                for (int i = 0; i < p.Length; i++) p[i] = -1;
                for (int i = 0; i < order.Length; i++) p[(int)order[i]] = i; // later duplicates win, as the dictionary's indexer
                return p;
            }
        }

        // Reaction broadcasters (PLAN fix c): every broadcaster (music, fires, smells, ...) pulses about once per second:
        // a spatial query for Sims in its radius plus line-of-sight checks per Sim (ReactionBroadcaster.PulseSims, 7% of
        // script time in Bridgeport). One far from the camera and the active household now pulses about one call in four
        // (staggered by a shared call counter), so an unseen Sim starts or stops reacting a few sim-seconds later; near the
        // player nothing changes. ReactionBroadcaster has no parameterless constructor, so the hook is a plain class.
        // Counters: [22] pulses, [23] skipped.
        public class BroadcasterPulseHook
        {
            static int sCalls;
            public void PulseHook(float pulseRadius, bool fullSimulation)
            {
                Sims3.Gameplay.Autonomy.ReactionBroadcaster b = (Sims3.Gameplay.Autonomy.ReactionBroadcaster)(object)this;
                int[] c = PatchCounters();
                c[22]++;
                Sims3.Gameplay.Interfaces.IGameObject o = b.BroadcastingObject;
                // docs/144: per-broadcaster one pulse in kUnseenBroadcastEvery, staggered by the broadcaster, instead of the
                // shared 1-in-4 counter below (which can starve the same broadcasters when their pulses line up with it)
                if (o != null && ShortcutOn("unseenbroadcast"))
                {
                    int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b);
                    if (FarFromViewers(o.Position))
                    {
                        long ut = UcBegin(kUcGate, 0);  // docs/227 probe
                        bool run = sBroadcastGate.Unseen(key, Math.Max(1, Instantiator.kUnseenBroadcastEvery));
                        UcEnd(kUcGate, ut);
                        NoteGateClears(c);
                        if (!run) { c[23]++; c[153]++; return; }
                        c[152]++;
                    }
                    else sBroadcastGate.Seen(key);
                    PulseOrig(pulseRadius, fullSimulation);
                    return;
                }
                if (ShortcutOn("broadcast") && o != null && (++sCalls & 3) != 0 && FarFromViewers(o.Position))
                {
                    c[23]++;
                    return;
                }
                PulseOrig(pulseRadius, fullSimulation);
            }
            public void PulseOrig(float pulseRadius, bool fullSimulation) { } // receives ReactionBroadcaster.PulseSims' original header
        }

        // Home visits (VisitLot) and plain lot trips (GoToLot, GoToCommunityLot): same as community visits, the game's
        // Run follows with a short route to the door and its own arrival. Counters: visitlot [10-12], gotolot [13-15],
        // gotocommunity [16-18].
        public class VisitLotHook : VisitLot
        {
            public bool RunHook() { ShortcutToLot(Actor, Target, "visitlot", 10); return RunOrig(); }
            public bool RunOrig() { return false; } // receives VisitLot.Run's original header
        }
        public class GoToLotHook : GoToLot
        {
            public bool RunHook()
            {
                Sims3.Gameplay.Actors.Sim sim = Actor as Sims3.Gameplay.Actors.Sim;
                if (sim != null && (Followers == null || Followers.Count == 0)) ShortcutToLot(sim, Target, "gotolot", 13);
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives GoToLot.Run's original header
        }
        public class GoToCommunityLotHook : GoToCommunityLot
        {
            public bool RunHook() { ShortcutToLot(Actor, Target, "gotocommunity", 16); return RunOrig(); }
            public bool RunOrig() { return false; } // receives GoToCommunityLot.Run's original header
        }

        // Visits to community lots (PLAN fix b3): parks, beaches, venues. An unseen townie going alone to an unseen lot far
        // away is placed at a safe spot on that lot, then VisitCommunityLot.Run runs as usual: its route to the front door
        // is now a few metres, and arrival (success count, "do something after arriving") is the game's own. The follower
        // list is preset empty so the random "bring a pet or housemate" pick, whose followers would start far away, is
        // skipped for that trip. Resorts, diving lots, houseboats and apartment shells keep their own code.
        // Counters: [7] runs, [8] shortcuts, [9] no safe spot.
        public class VisitCommunityLotHook : VisitCommunityLot
        {
            public bool RunHook()
            {
                if ((mFollowers == null || mFollowers.Count == 0) && ShortcutToLot(Actor, Target, "visit", 7))
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives VisitCommunityLot.Run's original header
        }

        // docs/202 §3.6.4 (unseenfbtrip): a mod that installs its own VisitCommunityLot subclass as the game's visit singletons and
        // overrides Run skips VisitCommunityLotHook (LynMods University More Shell Entries: VisitCommunityLotLabLecture runs every
        // community visit in this install, SV ~2.2 / IP ~7.6 ms/s of sync plan samples). Its Run gets the same shortcut with D's
        // walk-time hold, only for instances of the current visit singletons (the mod's follower interactions keep their walk).
        // One stub per patched Run (the stub receives that Run's header); more overriding types than slots: logged, left alone.
        public abstract class VclSubBase : VisitCommunityLot
        {
            protected void Shortcut()
            {
                object d = InteractionDefinition;
                if (!ReferenceEquals(d, VisitCommunityLot.Singleton) && !ReferenceEquals(d, VisitCommunityLot.NoAdditionalActionsSingleton)) return;
                if ((mFollowers == null || mFollowers.Count == 0) && ShortcutToLot(Actor, Target, "unseenfbtrip", 341, true))
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
            }
        }
        public class VclSubHook0 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook1 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook2 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        static readonly Type[] kVclSubHooks = { typeof(VclSubHook0), typeof(VclSubHook1), typeof(VclSubHook2) };
        static readonly List<Type> sVclSubPatched = new List<Type>();

        // at tuning time and again at world load (script mods may load after us); Run must not be compiled yet (Patch checks)
        internal static void ApplyTripSubclasses()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string ours = typeof(Fixes).Assembly.GetName().Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!UfPreCore.TripScanAssembly(a.GetName().Name, ours)) continue;
                Type[] ts;
                try { ts = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in ts)
                {
                    if (t.IsAbstract || !t.IsSubclassOf(typeof(VisitCommunityLot)) || sVclSubPatched.Contains(t) || t.GetMethod("Run", inst, null, Type.EmptyTypes, null) == null) continue;
                    sVclSubPatched.Add(t);
                    int slot = sVclSubPatched.Count - 1;
                    if (slot >= kVclSubHooks.Length) { AppliedLog().Add(t.FullName + ".Run: no unseenfbtrip hook slot, left alone"); continue; }
                    Patch(t, "Run", Type.EmptyTypes, kVclSubHooks[slot], "RunHook", "RunOrig");
                }
            }
        }

        static List<object> RecentLog()
        {
            List<object> l = AppDomain.CurrentDomain.GetData("ts3fixes.recent") as List<object>;
            if (l == null) { l = new List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.recent", l); }
            return l;
        }

    }
}
