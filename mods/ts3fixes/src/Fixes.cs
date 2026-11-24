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
            KeepAccess(saved, repl);
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

        // The original keeps its own access bits (MethodAttributes.MemberAccessMask, flags word bits 0-2); the rest of the
        // flags word stays the hook's (or the original's whole word, KeepVirtual). Before, a hooked private or protected
        // EA method read as public afterwards, so a lookup with NonPublic and without Public (ours, another mod's, EA's)
        // returned null, and a hook's stub read as the target's access. The bits do not change how a call runs: TS3W's
        // Mono 1.2.3 makes no access checks (docs/200 [verified]), and vtable setup reads them only for virtual methods
        // [inferred, Mono 1.2 class.c], whose whole flags word KeepVirtual already keeps. Check: VirtualSwapCheck.
        static void KeepAccess(byte[] saved, byte[] repl)
        {
            const int access = (int)MethodAttributes.MemberAccessMask;  // 7, low byte of the flags word
            repl[0] = (byte)((repl[0] & ~access) | (saved[0] & access));
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
            // rhoutside (RabbitHoleDirect.cs, docs/806): the 7-argument overload every RouteOutside call ends in
            Patch(typeof(RabbitHole), "RouteOutside", new Type[] { typeof(Sims3.Gameplay.Actors.Sim), typeof(List<Sims3.Gameplay.Actors.Sim>), typeof(Slot), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, typeof(RabbitHoleHook), "OutsideHook", "OutsideOrig");
            Patch(typeof(VisitCommunityLot), "Run", typeof(VisitCommunityLotHook), "RunHook", "RunOrig");
            // spawnfix / spawnoffworld (SpawnFix.cs, docs/857), spawnfix default ON, spawnoffworld default off: a spawn or removal refreshes one spawner, not every spawner in the world
            Patch(typeof(Sims3.Gameplay.Objects.Rewards.CollectionHelperSingleton), "FireEvent", typeof(SpawnFixHook), "FireHook", "FireOrig");
            Patch(typeof(ObjectSpawner), "AddObjectToSpawner", typeof(SpawnFixHook), "AddHook", "AddOrig");
            Patch(typeof(ObjectSpawner), "RemoveObjectFromSpawner", typeof(SpawnFixHook), "RemoveHook", "RemoveOrig");
            Patch(typeof(ObjectSpawner), "ImportContent", new Type[] { typeof(Sims3.SimIFace.CustomContent.ResKeyTable), typeof(Sims3.SimIFace.CustomContent.ObjectIdTable), typeof(Sims3.SimIFace.IPropertyStreamReader) }, typeof(SpawnFixHook), "ImportHook", "ImportOrig");
            Patch(typeof(ObjectSpawner), "TrySpawnObject", typeof(SpawnFixHook), "TrySpawnHook", "TrySpawnOrig");
            Patch(typeof(SpawnerBase), "OnStartup", typeof(SpawnFixHook), "StartHook", "StartOrig");
            Patch(typeof(SpawnerBase), "Dispose", typeof(SpawnFixHook), "DisposeHook", "DisposeOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Fishing.FishingSpotData), "ActivateSpot", typeof(SpawnFixHook), "ActivateHook", "ActivateOrig");
            Patch(typeof(Sims3.Gameplay.Objects.Fishing.FishingSpotData), "DeactivateSpot", typeof(SpawnFixHook), "DeactivateHook", "DeactivateOrig");
            Patch(typeof(VisitLot), "Run", typeof(VisitLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToLot), "Run", typeof(GoToLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToCommunityLot), "Run", typeof(GoToCommunityLotHook), "RunHook", "RunOrig");
            ApplyTripSubclasses();
            ApplyMotiveBuf();  // motivebuf (MotiveBuf.cs): pass-through while off
            ApplySocialStc();  // stcsocials (SocialStc.cs): pass-through while off
            ApplyCallCensus();  // callcensus (CallCensus.cs): pass-through, counts only while on
            ApplyUnseenVisitTrip();  // visitcar (UnseenVisitTrip.cs): the visit's driving chance, pass-through unless a Run asked
            // No tuning gate here: Apply runs in Instantiator's static constructor, before the tuning loader sets the [Tunable]
            // fields, so every tuning reads its C# default (docs/124 §17.8). Hooks read their tuning per call; patches that must
            // stay out unless a tuning is on go in ApplyTuned.
            Patch(typeof(Sims3.Gameplay.Actors.SimRoutingComponent).GetNestedType("WalkStyleComparer", BindingFlags.NonPublic), "Compare", typeof(WalkStyleCompareHook), "CompareHook", "CompareOrig");
            Patch(typeof(Sims3.Gameplay.Autonomy.ReactionBroadcaster), "PulseSims", typeof(BroadcasterPulseHook), "PulseHook", "PulseOrig");
            ApplyRouteFixes();
            ApplyRouteBackoff();
            ApplyRouteFailLog();  // docs/226: logging only (routefaillog)
            ApplyDoorGen();       // DoorGen.cs: door-answer generation for ts3native DoorMemoX=3 (switch doorgen, on)
            ApplyDoorShare();     // DoorShare.cs (docs/674): Sim class and shareable door answers for ts3native DoorShare (switch doorshare, off)
            ApplyFglInBound();    // FglInBound.cs (docs/682 §9): FindGoodLocationNearby swap, the indoors stage announced to ts3native [FglInside] (switch fglinbound, off)
            ApplyOverwatchStuck(false);
            ApplyOverwatchSpread(false);
            ApplyOverwatchNotice(false);
            ApplyOverwatchProbe(false);
            ApplyErrorTrapSpread(false);
            ApplyPortalAdaptorLeak();
            ApplyCasGrid();
            ApplySwapInvokeDiag();  // SwapInvokeDiag.cs, docs/312 (load-time tuning kSwapInvokeDiag)
            ApplyBlankTns();        // BlankTns.cs, docs/869 (blanktns: names the sender of a blank notice; counts only while off)
            ApplySpatialCache();
            ApplyFewerPlans();
            ApplyCandidateLoops();
            ApplyUnseenTraffic();  // docs/855: npccarexit's hook; unseentraffic lives in ParkingSpotHook / TrafficReleaseHook
            // festivalhidden (FestivalHidden.cs, docs/856), default ON: out-of-season festival objects stay out of autonomy's map
            Patch(typeof(GameObject), "UpdateCommodityInteractionMap", Type.EmptyTypes, typeof(FestivalHiddenHook), "CimHook", "CimOrig");
            // commodmapfast / commodmapshadow (CommodMapFast.cs, docs/313), default off: exact one-pass RemoveObject
            Patch(typeof(Sims3.Gameplay.Autonomy.CommodityInteractionMap), "RemoveObject", new Type[] { typeof(Sims3.Gameplay.Interfaces.IGameObject) }, typeof(CmfHook), "RemoveHook", "RemoveOrig");
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
            ApplyBoatRoamCap();       // docs/853 §4, ON by default (BoatRoamCap.cs)
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
            ApplyBurglarApt();     // docs/866 (BurglarApt.cs)
            ApplyGigWaitLeave();    // docs/568 §2 (GigWaitLeave.cs)
            ApplySculptPose();    // docs/568 §3 (SculptPose.cs)
            ApplyTrainBoost();    // docs/569 §1 (TrainBoost.cs)
            ApplyEatOutsideCount();    // docs/569 §2 (EatOutsideCount.cs)
            ApplyFireActiveJob();    // docs/570 §1 (FireActiveJob.cs)
            ApplyFireTrapCount();    // docs/570 §5 (FireTrapCount.cs)
            ApplyAptPossess();       // docs/863 (AptJobTargets.cs)
            ApplyFireDoorKeep();     // docs/864 (FireDoorKeep.cs)
            ApplyFireNre();          // docs/867 (FireNre.cs)
            ApplyFireCount();        // docs/868 (FireCount.cs)
            ApplyGenerationsLogic(); // docs/571 §1-4 (GenerationsLogic.cs)
            ApplySeasonsLogic();     // docs/571 §5-7 (SeasonsLogic.cs)
            ApplySupernaturalLogic(); // docs/571 §8 (SupernaturalLogic.cs)
            ApplyShowtimeLogic();    // docs/571 §9-11 (ShowtimeLogic.cs)
            ApplyUniversityLogic();  // docs/572 §4-5 (UniversityLogic.cs)
            ApplyIslandParadiseLogic(); // docs/572 §6-7 (IslandParadiseLogic.cs)
            ApplyFutureLogic();      // docs/572 §9-12 (FutureLogic.cs)
            ApplyPetsLogic();        // docs/572 §13-15 (PetsLogic.cs)
            ApplyPhoneLogic();       // docs/573 §1-6 (PhoneLogic.cs)
            ApplyUniTravelLogic();   // docs/573 §7-9 (UniTravelLogic.cs)
            ApplySmartPhoneGive();   // docs/573 §10-11 (SmartPhoneGive.cs)
            ApplyHouseholdLogic();   // docs/574 §1-3 (HouseholdLogic.cs)
            ApplyWaSkillsLogic();    // docs/574 §4-7 (WaSkillsLogic.cs)
            ApplyCareerLogic();      // docs/574 §8-9 (CareerLogic.cs)
            ApplyObjectUseLogic();   // docs/575 §2-5 (ObjectUseLogic.cs)
            ApplyInUseLogic();       // docs/576 §2-3 (InUseLogic.cs)
            ApplyAutonomyInUseBlock(); // docs/577 §1 (AutonomyInUseBlock.cs)
            ApplyNpcBusyPartner();   // docs/577 §4 (NpcBusyPartner.cs)
            ApplySocialReplanLog();  // docs/577 §2 (SocialReplanLog.cs), logging only
            ApplyBarOrderLog();      // docs/578 §1 (BarOrderLog.cs), logging only
            ApplyPetPutDownWait();   // docs/578 §2 (PetPutDownWait.cs)
            ApplyPuddleReach();   // docs/578 §3 (PuddleReach.cs)
            ApplyMopUnreachable();   // s58 lead (MopUnreachable.cs)
            ApplyPetPuddleTile();   // MopUnreachable.cs
            ApplyInitStackTrace();   // docs/594 §1 (SeasonsHunt594.cs)
            ApplyWeatherStoneListener();   // docs/594 §2 (SeasonsHunt594.cs)
            ApplyRainFires();   // docs/594 §3-4 weathermanagerthrow + rainsheltercheap (SeasonsHunt594b.cs)
            ApplyPondClearScan();   // docs/594 §5 (SeasonsHunt594b.cs)
            ApplySoakedStat();   // docs/594 §6 (SeasonsHunt594b.cs)
            ApplyHoverTrainTest();   // docs/598 §5 (HoverTrain598.cs)
            ApplyNewCarTest();   // docs/598 §6 (NewCar598.cs)
            ApplyOuterwearAutoLoop();   // docs/596 §1 (SeasonsWeather596.cs)
            ApplyOuterwearRestore();   // docs/596 §2 (SeasonsWeather596.cs)
            ApplyQueueProximity();   // docs/597 §2 (SleepQueue597.cs); spoonslavechecks rides spoonreset's SrHook
            ApplySubRouteFail();   // docs/597 §3 (SleepQueue597b.cs)
            ApplyPetWakeBed();   // docs/597 §4 (SleepQueue597b.cs)
            ApplySlaveWakeReason();   // docs/597 §5 (SleepQueue597b.cs)
            ApplyRouteCauseLog();   // docs/580 (RouteCauseLog.cs), logging only
            ApplyScarePriority();   // docs/580 §14 (ScarePriority.cs)
            ApplyGroupChatPriority();   // docs/638 §3 (GroupChatPriority.cs)
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
            ApplyTrashPileImport();   // docs/582 B8 §1
            ApplyCatGymWatch();   // docs/582 B8 §2
            ApplyBadSecretKey();   // docs/582 B8 §3
            ApplyTraitListenerRemove();   // docs/582 B8 §4
            ApplyWeddingListener();   // docs/582 B8 §5
            ApplyPortAskNoHome();   // docs/582 B8 §6
            ApplyLectureBroadcaster();   // docs/582 B8 §7
            ApplyMidlifeWishSave();   // docs/582 B8b §1
            ApplyInnerBeautyOnce();   // docs/582 B8b §2
            ApplyBetrayedNullSim();   // docs/582 B8b §3
            ApplyOppTrackerRelisten();   // docs/582 B8b §5
            ApplyTraitRemoveNull();   // docs/582 B9 §1
            ApplyHomeworkCopyOld();   // docs/582 B9 §2
            ApplyLaundryMoodEach();   // docs/582 B9 §3
            ApplyPartyCleanupNull();   // docs/582 B9 §4
            ApplySafeLocUnhide();   // docs/582 B9 §5
            ApplyMidlifeUiEnding();   // docs/582 B9 §6
            ApplyNemesisNoRomance();   // docs/582 B9 §7
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
            ApplyPutDownPlaced();   // docs/592 S13 B12 §44
            ApplyDisplayCaseReset();   // docs/587 EP1 §1
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
            ApplyButlerPayAlarm();   // docs/593 V1 B13 §16
            ApplyPetHarvestVfx();   // docs/593 H1 B13 §17
            ApplySuatGradeBonus();   // docs/587 EP1 §2 (was inside the line above's comment, never called; docs/604 §5)
            ApplyPushWaitSkip();   // docs/604 §2 (PushWait604.cs)
            ApplyTreasureSpawnInvDup();   // docs/607 §2 (TreasureSpawn74.cs)
            ApplySarcMummyNpc();   // docs/607 §3 (TreasureSpawn74.cs)
            ApplySpeedSkipLevel();   // docs/609 §2-3 (GameSpeed78.cs): speedskiplevel, speedskipnoactor
            ApplySpeedRecalcLock();   // docs/609 §4 (GameSpeed78.cs)
            ApplySpeedLockOwner();   // docs/609 §5 (GameSpeed78.cs)
            ApplyAlarmZeroPeriod();   // docs/609 §6 (GameSpeed78.cs); also carries the alarmtime stamp (HitchCause.cs)
            ApplyHeartBrokenBcast();   // docs/609 §7 (GameSpeed78.cs)
            ApplySpawnerSkip();   // docs/610 §2 (SpawnerMapTag79.cs); also carries the alarmtime spawn timing (HitchCause.cs)
            ApplyMapTagTaskAlive();   // docs/610 §3 (SpawnerMapTag79.cs)
            ApplyLotMgrCallbacks();   // docs/611 §2 (LotCallbacks80.cs)
            ApplyFireplaceHerb();   // docs/611 §3 (LotCallbacks80.cs)
            ApplyRummageBcast();   // docs/611 §4 (LotCallbacks80.cs)
            ApplyStairWaitEnd();   // docs/612 §2 (StairDup82.cs)
            ApplyCompatPrecedence();   // docs/328 §7 (CompatResolve.cs): our cleanupvfx / lifeguardquit / stairwaitend on mod overrides that skip the game's method
            ApplySimDisposeLink();   // docs/612 §3 (StairDup82.cs)
            ApplyRhExitUnseen();   // docs/613 §2 (StuckJobs83.cs)
            ApplyRhEntryRelax();   // docs/627 §4 (RhEntry627.cs), after RabbitHoleHook (chains)
            ApplyTipsNpcEnd();   // docs/613 §3 (StuckJobs83.cs)
            ApplyBarPutDownWait();   // docs/614 §1 (Scan84.cs)
            ApplyPutDownSurfaceUse();   // docs/614 §2 (Scan84.cs)
            ApplySwipeRestore();   // docs/614 §3 (Scan84.cs)
            ApplyUnicornSpawnFail();   // docs/614 §4 (Scan84.cs)
            ApplyDataDiscPutBack();   // docs/614 §5 (Scan84.cs)
            ApplySpoilBroadcast();   // docs/615 §1 (Scan85.cs)
            ApplyWeatherStoneRelisten();   // docs/615 §2 (Scan85.cs)
            ApplyBirthdayAgeRecord();   // docs/615 §3 (Scan85.cs)
            ApplyUnseenService();   // docs/628 (UnseenService88.cs)
            ApplyUnseenLoopSkip();   // docs/632 (LoopSkip632.cs)
            ApplyLifeEventIdFirst();   // docs/622 §5 (Hitch622.cs)
            ApplyBuffPausedTimeout();   // docs/622 §7 (Hitch622.cs)
            ApplyTimerAlarmLeak();   // docs/623 §1 (Wishes86.cs)
            ApplyWishFeedbackKeep();   // docs/623 §2 (Wishes86.cs)
            ApplyWishSubjectGone();   // docs/623 §3 (Wishes86.cs)
            ApplyWishUpdateLost();   // docs/623 §4 (Wishes86.cs)
            ApplySocialWorkerPromise();   // docs/623 §5 (Wishes86.cs)
            ApplyAlloc87();   // docs/625 (Alloc87Comparers.cs): enumcmpnobox, alarmcmpnobox
            ApplyMegaphoneRun();   // docs/608 §2 (WalkStyleLeak77.cs)
            ApplyDragonCurseRun();   // docs/608 §3 (WalkStyleLeak77.cs)
            ApplyMermaidSharkNull();   // docs/587 EP1 §3
            ApplyJetpackFlyHereCleanup();   // docs/587 EP1 §4
            ApplyNaniteRouteFail();   // docs/587 EP1 §5
            ApplySpringDanceClear();   // docs/587 EP1 §6
            ApplyNectarCopyLevel10();   // docs/587 EP1 §7
            ApplyHarvestEatVfx();   // docs/587 EP1 §8
            ApplyBuffetEmptyPick();   // docs/587 EP1 §9
            ApplyKrakenFriendSailor();   // docs/587 EP1 §10
            ApplyMummyCurseTrait();   // docs/587 EP1 §12
            ApplyBullRiderCleanup();   // docs/587 EP1 §13
            ApplyBroomTipPerformer();   // docs/587 EP1 §14
            ApplyBalancingBallCareer();   // docs/587 EP1 §15
            ApplyElvisGnome();   // docs/587 EP1 §16
            ApplyCatToyPickerRel();   // docs/587 EP1 §17
            ApplyGourmetPetRecipe();   // docs/587 EP1 §18
            ApplyChipComponents();   // docs/587 EP1 §19
            ApplyNaniteMerge();   // docs/587 EP1 §20
            ApplyConsignmentLastDay();   // docs/587 EP2 §7.1
            ApplyBartendingHourlyPay();   // docs/587 EP2 §7.2
            ApplyInfluenceFollowerBonus();   // docs/587 EP2 §7.3
            ApplyBroomArenaChoice();   // docs/587 EP2 §7.4
            ApplyFortuneTellerScamOdds();   // docs/587 EP3 §8.1
            ApplyConsignmentSkillCurve();   // docs/587 EP3 §8.2
            ApplyConsignmentStockLeak();   // docs/587 EP3 §8.3
            ApplyHerbQualityFloor();   // docs/587 EP3 §8.4
            ApplySnackBowlSlobVfx();   // docs/587 EP3 §8.5
            ApplyWorldPondSkateTest();   // docs/587 EP3 §8.6
            ApplyPingPongOpponent();   // docs/587 EP3 §8.7
            ApplyMermaidDehydrateLook();   // docs/587 EP3 §8.8
            ApplyMeteorMagnetOrphan();   // docs/587 EP3 §8.9
            ApplyDaycareLateAlarm();   // docs/587 EP3 §8.10
            ApplyBoxOfMysteryWooHoo();   // docs/587 EP3 §8.11
            ApplyBodySculptorWeight();   // docs/587 EP3 §8.12
            ApplyNectarConsignValue();   // docs/587 EP3 §8.13
            ApplyStrayGnomeTns();   // docs/587 EP3 §8.14
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
            ApplyLaneSnap();  // docs/807: unseenlanesnap (ON 2026-11-20, unseen townies): portal other-lane replans
            ApplyNearVenue();  // docs/805: nearvenue (ON 2026-11-20, townies only); before ApplyAutShadow, which reads Lot's callees
            ApplyIceCreamDirect();  // docs/805: icecreamdirect (ON 2026-11-20): an unseen ice cream truck skips its plan and drive
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
            ApplyUnseenOutfitDefer();  // issue #451, ON by default (UnseenOutfitDefer.cs)
            ApplyTagUnseen();          // docs/60 §3 (TagUnseen.cs), off by default
            ApplyUnseenOutfitNoSpin();  // docs/809, ON 2026-11-20, needs unseenoutfitdefer (UnseenOutfitNoSpin.cs)
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
            ApplyUnseenLoopSkipStep();   // docs/632 option (a), after WakePark: its WaitForExitReason hook chains under ours
            ApplyAutLiveB14();
            ApplyHitchCause();
            ApplyStandWait();          // docs/686 (StandWait.cs): standwaitfix / standwaitfixhh, off by default (counts either way)
            ApplyShareLanes();         // docs/686 §8 (ShareLanes.cs): sharelanes / laddershare / spiralshare ON, simpass / angleentry and the household switches off by default (counts either way)
            ApplyBlockedUse();         // docs/686 §10 (BlockedUse.cs): blockeduse ON, blockedusehh off by default (counts either way)
            ApplyMakeBedBoth();        // docs/686 §13 (MakeBedBoth.cs): makebedboth ON, makebedbothhh off by default (counts either way)
            ApplyClipWait();           // docs/686 §9 (ClipWait.cs): clipwait / clipwaithh, off by default (counts either way); after ApplyStandWait and ApplyShareLanes
            ApplyPetRelax();           // docs/686 §11 (PetRelax.cs): petrelax / petrelaxhh, off by default (counts either way); after ApplyShareLanes and ApplyClipWait
            ApplySocialReach();        // docs/686 §17 (SocialReach.cs): socialreach / socialreachhh, off by default (counts either way); its DoRoute step is in ApplyBlockedUse's hook, as unstuck's (§18, Unstuck.cs)
            ApplyWooHooSnap();         // docs/686 §20 (WooHooSnap.cs): woohoosnap / woohoosnaphh, pairsnap / pairsnaphh, off by default; their steps are in ApplyBlockedUse's, ApplySocialReach's and ApplyShowerShare's hooks
            ApplyQueueWake();          // docs/686 §23 (QueueWake.cs): queuewake, off by default (counts either way); swaps Sim.LoopIdle and InteractionInstance.OnAddedToQueue (stacked)
            ApplyReachUse();           // docs/686 §12 (ReachUse.cs): reachuse / reachusehh, off by default (counts either way); the DoRoute hook is ApplyBlockedUse's
            ApplyObjShare();           // docs/686 §14 (ObjShare.cs): rhshare / servingshare and their household switches, linewaitlog, off by default (counts either way); the line hook is ApplyShareLanes's
            ApplyShowerShare();        // docs/686 §15 (ShowerShare.cs): showertogether / showerpee and their household switches, off by default (counts either way); the line hook is ApplyShareLanes's
            ApplySmcSetActor();        // SmcSetActor.cs: StateMachineClient.SetActor for servingshare (docs/686 §14), a one-int test while nothing is armed
            ApplyCrowdPort();          // docs/686 §19 (CrowdPort.cs): crowdport / crowdporthh, off by default; its line and enter-slot steps are in ApplyShareLanes's and ApplyObjShare's hooks
            ApplyPetStartleSkip();     // docs/656 §8.1 (PetStartleSkip.cs), off by default
            ApplyPetArrivePoint();     // docs/656 §4.1 (PetArrivePoint.cs), off by default
            ApplyStrayLeave();         // docs/656 §4.2 (StrayLeave.cs), off by default
            ApplyWildLotSkip();        // docs/656 §8.3 (WildLotSkip.cs; FindGoodLot rides wildhorselot's BEp4WhHook), off by default
            try { Alloc87Sync(); } catch (Exception) { }  // docs/625: before any save loads, when the tuning turns them on
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
        // [3544] outfitwaitpoll household rule (OutfitWait.cs, docs/580 §15): stale waits of a live player Sim kept on EA's per-tick loop.
        // [2124-2127] outfitwaitend (OutfitWait.cs, docs/601 §1): off-screen NPC outfit waits ended with EA's instant switch / would end (switch off) / kept past the mark / errors.
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
        // [1345-1347] fightorphanwait (FightOrphan.cs): snow/water fight Waits the fight no longer holds (or whose fight / controller alarm is gone) ended after two hourly passes / left per pass (switch off) / errors.
        // [1348-1350] poolnolane (PoolNoLane.cs): autonomous GetInPool / DoggyGetInPool offers refused for a pool with no enabled Inside lane / would have been refused (switch off) / errors.
        // [1351-1352] spare (his 3998-3999 under the PR's numbering).
        // [1353-1356] venuepicktime (VenuePickTime.cs, logging only): venue arrival picks timed / picks whose searches took >= 200 ms / ms in those searches / errors.
        // [1357-1359] venuepickfast (VenuePickTime.cs, timing only, NPCs only): yields before a venue arrival motive search / searches not yielded (not the Sim's own task, no yield context, a player Sim) / errors.
        // [1360-1369] autonomyfailcooldown (AutonomyFailCooldown.cs): autonomous offers refused in a cooldown / would have been refused (switch off) / route failures recorded / cooldowns started / route failures of household, selected or unknown-scope Sims not recorded / errors / continuation route failures recorded on their pusher / all-targets cooldowns started / route failures not counted after a locked door / route failures of a Hidden (script-pushed) head not recorded.
        // [1374-1375] autonomyfailcooldown (AutonomyFailCooldown.cs, follow-up): route failures of a head with search type None not recorded / NPC pianist breaks after a failed PianistPerform route.
        // [2778-2779] errortraploop (ErrorTrapLoop.cs): ErrorTrap re-run loops ended (same error 3 times on one non-Sim task) / errors.
        // [2784] errortraploop (ErrorTrapLoop.cs, diagnostic): OnScriptError hook calls with a re-run answer and the switch on.
        // [2783] tagunseen (TagUnseen.cs): Tag players sent out at the no-route cap (120 sim min of laps with no route).
        // [2788] watchdog (RouteFixes.cs NotePlanResult): failed plans of a bouncer in VelvetRopes+WatchDoor, not counted for "route plans failing".
        // [2785-2787] socialnoeffect guard (SocialNoEffectGuard.cs): socials with no rule ended at GetStateMachine / no rule with the switch off (EA throws) / errors.
        // [2775-2777] rolenullpurge (RoleNullPurge.cs): null role lists removed / null Roles removed / errors, before EA's VerifyRoleGivingObjectRoles.
        // [2797-2799] loopidlebridge (LoopIdleBridge.cs): stale bridges (host reset, no C++ driver) dropped in Sim.LoopIdle / seen with the switch off (EA throws) / guard errors.
        // [2719-2721] fightstaycap (FightStayCap.cs): Sims released from a snow/water fight after 120 sim min / hook errors / fight members not found.
        // [2780-2782] fireworkswatchlate (FireworksWatchLate.cs): late watchers sent through LoopWatch before Exit / members not found (EA's Run used) / EA's Run threw SacsErrorException (switch off).
        // [4078-4079] unseenrelax (RouteBackoff.cs UrxBegin, contributor range): unseen townie plans run without ReplanToFindObstacleWhenPathPlanFails / BlockedByPeople / BlockedByDynamicFootprints / of them still failed.
        // [4080-4082] iafailmemo (RouteBackoff.cs IaAnswer, contributor range): townie failures remembered (Sim + interaction + start lot + goal lot + options) / repeat plans answered from the memo / ms of the remembered plans the answers stood for.
        // [3982-3984] boatroutenull (BoatRouteNull.cs): Boat.RouteBoatToLandOnWayToPosition false for a boat with no RoutingComponent / seen with the switch off (EA throws) / errors.
        // [4070-4074] boattrip (BoatTrip.cs): boats placed or moved to an open-water cell / trip starts that took a boat start, a walk or another shore / boat plans a step 1-4 answered / trips where every step failed / leftover boats released, parked or footprint off.
        // [4075] boatplace (BoatPlace.cs): boats placed on an open-water cell outside the lot. [4076] retryscope (RetryScope.cs): scope answers sent again before boattrip's sync plans. [4077] boatlandstuck (BoatLandStuck.cs): Sims put ashore by Boat.GetOut after a failed GetOutOfBoat.Run.
        // [3985-3987] outfitmissing (OutfitMissing.cs): SwitchOutfitHelper.Init for a missing outfit (pregnant Sims' special outfits) made no-change / seen with the switch off (EA throws) / guard errors.
        // [3988-3990] ageupdupinst (AgeUpDupInst.cs, #553): second instantiations during a deferred age-up answered with the first call's Sim / seen with the switch off (EA throws) / no Sim made (rethrown).
        // [3991-3993] overwatchrelretry (OwRelRetry.cs): Overwatch CleanupRelationships re-runs after "out of sync" / seen with the switch off / still failing after 50 runs.
        // [3994-3997] OneOffGuards.cs: overwatchresetgone destroyed Sims skipped in a reset / seen off; motivesnohousehold CreateMotives with the NpcHousehold stand-in / seen off.
        // [4052] frontstageowner (EaNullGuards.cs): unowned FrontStageKits answered null in GetOwner. [4053-4054] careerskillnull (EaNullGuards.cs): skills EA could not add skipped in GiveCareerLevelSkills / household or unknown owners left to EA.
        // [4055] stuckhbskip (FewerPlans.cs): townie stuck checks on a non-household houseboat ended as after a success. [4056-4057] turnjigskip (JobYieldRoad.cs FpYieldHook): top-level turn-in-place checks by unseen townie boats or animals / of them skipped.
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
        // [1496-1499] swapinvokediag (SwapInvokeDiag.cs, docs/312): TargetException out of MethodInfo.Invoke on a method we swapped / other exception from one / TargetException from a method we did not swap / lines not written.
        // [2362-2365] blanktns (BlankTns.cs, docs/869): blank notices renamed with their sender / blank notices seen with the switch off / blank notices not logged (session cap or log channel) / errors.
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
        // [2195-2198] burglarapt (BurglarApt.cs, docs/866): burglar loot answers turned to no in hidden / public apartment rooms / would be with the switch off / spare / errors.
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
        // [3953-3955] fireactivejob (FireActiveJob.cs, docs/570 §1): stale ActiveFireFighter.sActiveJob cleared at world quit / dropped at load / errors.
        // [3956-3959] firetrapcount (FireTrapCount.cs, docs/570 §5): trapped-Sim tasks dropped for Sims gone from the lot / hidden apartment trap spots removed / jobs completed by a drop / errors.
        // [2178-2180] fireleftover (FireLeftover.cs, docs/862): emergency rescue tasks dropped with nobody trapped left / window-fire tasks completed with no window fire left / errors.
        // [2181-2183] aptgasleak (AptJobTargets.cs, docs/863): earthquake gas leaks in hidden apartment units dropped / seen with the switch off / errors.
        // [2184-2186] aptpossess (AptJobTargets.cs, docs/863): ghost-hunter spirit hops into hidden apartment units re-picked / seen with the switch off / errors.
        // [2187-2190] firedoorkeep (FireDoorKeep.cs, docs/864): door locks given back after a large-fire cleanup / job door locks undone after another job type / doors seen with the switch off / errors.
        // [2191-2194] horseshoesolo (HorseshoeSolo.cs, docs/865): solo horseshoe turns ended / gone or idle players removed from court queues / seen with the switch off / errors.
        // [2354-2357] firenre (FireNre.cs, docs/867): RestartTimer skipped for a firefighter with no current job / thank-you rolls set to 0 for a pick with no fire career / either seen with the switch off / errors.
        // [2358-2361] firecount (FireCount.cs, docs/868): rescues not counted (passer-by, re-trapped, second rubble pile) / deaths handled / cases seen with the switch off / errors.
        // [3960-3962] birthdayagers (GenerationsLogic.cs, docs/571 §1): later agers recorded / listeners removed at cleanup / errors.
        // [3963-3966] sneakoutlistener (GenerationsLogic.cs, docs/571 §2): stale sneak-outs skipped / duplicate return-home listeners removed / left-over sneak listeners removed / errors.
        // [3618-3619] promcarpool (GenerationsLogic.cs, docs/571 §3): prom carpools removed / errors.
        // [3967-3968] curfewalarm (GenerationsLogic.cs, docs/571 §4): handles reset at quit / errors.
        // [3969-3971] trickortreatalarm (SeasonsLogic.cs, docs/571 §5): handles reset at quit / off-holiday runs skipped / errors.
        // [2591-2592] snowfightjoinwait (SeasonsLogic.cs, docs/571 §6): arenas reset at game end / errors.
        // [2593-2594] hauntedumbrella (SeasonsLogic.cs, docs/571 §7): answers changed / errors.
        // [2595-2596] permazombiemoon (SupernaturalLogic.cs, docs/571 §8): listeners re-registered at load / errors.
        // [2597-2599] steadygigmiss (ShowtimeLogic.cs, docs/571 §9): other Sims' misses ignored / listeners registered at load / errors.
        // [2600-2601] npcshowstuck (ShowtimeLogic.cs, docs/571 §10): stale flags cleared / errors.
        // [2602-2603] gigpullaudience (ShowtimeLogic.cs, docs/571 §11): stale pulls skipped / errors.
        // [2604-2606] lunacysocials (SupernaturalLogic.cs, docs/572 §1): per-Sim alarms scheduled / re-armed at load / errors.
        // [2607-2608] fairyauratimer (SupernaturalLogic.cs, docs/572 §2): timers restored / errors.
        // [2609-2611] npcseasonalparty (SeasonsLogic.cs, docs/572 §3): listeners registered at load / NPC parties skipped / errors.
        // [2612-2613] professorterm (UniversityLogic.cs, docs/572 §4): professors kept / errors.
        // [2614-2615] protestlastday (UniversityLogic.cs, docs/572 §5): resets at quit / errors.
        // [2616-2617] seasickpaused (IslandParadiseLogic.cs, docs/572 §6): buffs unpaused or alarms dropped / errors.
        // [2618-2619] resortweddingalarm (IslandParadiseLogic.cs, docs/572 §7): handles released / errors.
        // [2622-2623] plumbotfacelistener (FutureLogic.cs, docs/572 §9): other Sims' events ignored / errors.
        // [2624-2625] efficientchip (FutureLogic.cs, docs/572 §10): modifiers corrected / errors.
        // [2626-2627] sentiencename (FutureLogic.cs, docs/572 §11): names repaired / errors.
        // [2628-2629] jetpackflyingbuff (FutureLogic.cs, docs/572 §12): buffs unpaused / errors.
        // [2630-2631] horsetraitscore (PetsLogic.cs, docs/572 §13): scores computed on the horse / errors.
        // [2632-2633] wildhorsegender (PetsLogic.cs, docs/572 §14): genders swapped / errors.
        // [2634-2635] littercap (PetsLogic.cs, docs/572 §15): caps lowered / errors.
        // [2636-2637] phoneloadreset (PhoneLogic.cs, docs/573 §1): phones cleared / errors.
        // [2638-2639] phonecallschedule (PhoneLogic.cs, docs/573 §2): windows corrected / errors.
        // [2640-2641] phonecallrearm (PhoneLogic.cs, docs/573 §3): alarms re-armed / errors.
        // [2642-2643] woohootywait (PhoneLogic.cs, docs/573 §4): waits ended early / errors.
        // [2644-2645] invitecallreply (PhoneLogic.cs, docs/573 §5): replies corrected / errors.
        // [2646-2647] gradvisitors (PhoneLogic.cs, docs/573 §6): entries dropped / errors.
        // [2648-2649] resumetns (UniTravelLogic.cs, docs/573 §7): notices shown / errors.
        // [2650-2651] unitravelflag (UniTravelLogic.cs, docs/573 §8): flags cleared / errors.
        // [2652-2653] returnhomealarms (UniTravelLogic.cs, docs/573 §9): handles reset / errors.
        // [2668-2669] phonefixup (SmartPhoneGive.cs, docs/573 §10): phones checked after a grant / errors.
        // [2670-2671] phonegive (SmartPhoneGive.cs, docs/573 §11): phones given with a correction / errors.
        // [2654-2655] wildherdmother (HouseholdLogic.cs, docs/574 §1): mothers replaced / errors.
        // [2656-2657] splitpartner (HouseholdLogic.cs, docs/574 §2): partners cleared or splits refused / errors.
        // [2658-2659] movingflag (HouseholdLogic.cs, docs/574 §3): flags cleared / errors.
        // [2660-2661] sandscoins (WaSkillsLogic.cs, docs/574 §4): piles given / errors.
        // [2674-2675] tombmusic (WaSkillsLogic.cs, docs/574 §5): resets / errors.
        // [2662-2663] artsellnoartist (WaSkillsLogic.cs, docs/574 §6): sales completed without an artist / errors.
        // [2664-2665] royaltyorphan (WaSkillsLogic.cs, docs/574 §7): alarms removed / errors.
        // [2666-2667] promoteshift (CareerLogic.cs, docs/574 §8): answers corrected / errors.
        // [2672-2673] gotoworkstale (CareerLogic.cs, docs/574 §9): stale pushes skipped / errors.
        // [2679-2680] handsetreset (ObjectUseLogic.cs, docs/575 §2): handsets put back / errors.
        // [2681-2682] flowersinuse (ObjectUseLogic.cs, docs/575 §3): bouquets freed / errors.
        // [2683-2684] uselistprune (ObjectUseLogic.cs, docs/575 §4): use/reference entries dropped / errors.
        // [2685-2686] chestuselist (ObjectUseLogic.cs, docs/575 §5): Sims taken off / errors.
        // [2687-2688] swapdelegates (SwapDelegates.cs, docs/576 §1, ON): hook classes mapped / left alone (gauges, not counts).
        // [2689-2690] resetinusefree (InUseLogic.cs, docs/576 §2): items freed / errors.
        // [2691-2692] putdowninuse (InUseLogic.cs, docs/576 §3): put-downs repaired / errors.
        // [2693-2695] autonomyinuseblock (AutonomyInUseBlock.cs, docs/577 §1): taken objects blocked / seen with switch off / errors.
        // [2742-2746] stcsocials / stcsocialscheck (SocialStc.cs, docs/584 §9): calls served / lists or lookups saved / checks / check mismatches / fallbacks.
        // [2753-2755] tpaptgoal (UnseenTeleportGoal.cs, docs/584 §8): home apartment / shell goals allowed / refused as tomb or hidden room / refused otherwise (no way out, raised, unknown).
        // [2739-2741] tpdoorrights (UnseenTeleportGoal.cs, docs/584 §7): lots open by the Sim's own door rights that upstream closes / of those only by household access / closed by a door the Sim cannot pass.
        // [2736-2738] routableasync (RouteBackoff.cs, docs/584 §3): kind-2 STARTs let through (unseen routable checks) / refused here / let through but not started.
        // [2733-2735] buffbufcheck (MotiveBuf.cs, docs/584 §5): UpdateBuffs calls checked / empty delayed list (ilp_buffbuf skips the copy) / Count vs enumeration mismatches (must be 0).
        // [2729-2732] motivebuf (MotiveBuf.cs, docs/584 §5): updates from a pooled list / snapshots checked / check mismatches (must be 0) / fallbacks or errors.
        // [2722-2724] visitcar (UnseenVisitTrip.cs, docs/584 §1): long autonomous NPC visits judged / flips that kept driving / left to EA's flip (household, selected, seen).
        // [2725-2728] visitwalkfinish (UnseenVisitTrip.cs, docs/584 §1): interrupts asked / walks finished at the goal / refused at the callback / errors.
        // [2696-2698] npcbusypartner (NpcBusyPartner.cs, docs/577 §4): NPC waits ended / seen with switch off / errors.
        // [3972-3973] socialreplanlog (SocialReplanLog.cs, docs/577 §2): partner-blocked receiver replans / lines dropped by the 300 cap.
        // [3974-3975] barorderlog (BarOrderLog.cs, docs/578 §1): conversations timed / lines dropped by the 300 cap.
        // [3976-3978] petputdownwait (PetPutDownWait.cs, docs/578 §2): pet waits ended / seen with switch off / errors.
        // [1378-1381] houseboatlegmemo (HouseboatLegMemo.cs, docs/853 §2): NPC houseboat boat-leg no-path plans recorded / repeats answered / ms of the recorded plans those answers stand for / exempt (household, selected, unknown scope, no Sim or boat router) or errors.
        // [1382-1391] houseboatgangwayrescue (HouseboatGangwayRescue.cs, docs/853 §3): NPC Sims marked by a no-path gangway leg to their Docked houseboat / placed by EA's PlaceSimAtEntrance after GoHome failed / refused / errors; refusals: exempt / no, another or undocked houseboat / once-per-hour rule / busy / on the lot, far or stale / no spot.
        // [1392-1399] boatroamcap (BoatRoamCap.cs, docs/853 §4): NPC BoatAround runs capped (no roam) / ReturnToShore pushed / returns failed (precheck, push, run or never ran; EA roams) / exempt runs / errors / returns that reached the shore / service, role or AlarmOneShot-task boating logged / caps by night or low motive.
        // [3979-3981] puddlereach (PuddleReach.cs, docs/578 §3): splash tests withheld after a failed run / seen with switch off / errors.
        // [2709-2710] routecauselog (RouteCauseLog.cs, docs/580): lines / dropped by the caps.
        // [3541-3543] scarepriority (ScarePriority.cs, docs/580 §14): receivers raised / seen with switch off / errors.
        // [3841-3844] groupchatpriority (GroupChatPriority.cs, docs/638 §3): GroupChatB receivers raised / seen with switch off / errors / spare.
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
        // [3545-3548] trashpileimport (Batch582gLogic.cs, docs/582 B8 §1): stale age setups released / Ancient piles built through Old / seen with switch off / errors.
        // [3612-3614] catgymwatch (Batch582gLogic.cs, docs/582 B8 §2): gym references filled / seen with switch off / errors.
        // [3615-3617] badsecretkey (Batch582gLogic.cs, docs/582 B8 §3): texts corrected / seen with switch off / errors.
        // [3639-3641] traitlistenerremove (Batch582gLogic.cs, docs/582 B8 §4): listeners removed / left with switch off / errors.
        // [2920-2922] weddinglistener (Batch582gLogic.cs, docs/582 B8 §5): listeners removed / left with switch off / errors.
        // [3642-3644] portasknohome (Batch582gLogic.cs, docs/582 B8 §6): throws avoided / seen with switch off / errors.
        // [3645-3647] lecturebroadcaster (Batch582gLogic.cs, docs/582 B8 §7): earlier broadcasters disposed / repeat lectures with switch off / errors.
        // [3648-3650] residentspawnwalk (ResidentSpawnSlot.cs, docs/582 B8 §8): points accepted by the walk / would accept with switch off / errors.
        // [3651-3653] midlifewishsave (Batch582hLogic.cs, docs/582 B8b §1): values saved or restored / exports with wishes dropped (switch off) / errors.
        // [2923-2925] innerbeautyonce (Batch582hLogic.cs, docs/582 B8b §2): duplicate calls skipped / duplicates seen with switch off / errors.
        // [3654-3656] betrayednullsim (Batch582hLogic.cs, docs/582 B8b §3): notices shown and break-ups done / seen with switch off / errors.
        // [3657-3659] topictestnull (Batch582hLogic.cs, docs/582 B8b §4): throws avoided / seen with switch off / errors.
        // [3690-3692] opptrackerrelisten (Batch582hLogic.cs, docs/582 B8b §5): duplicate registrations avoided / seen with switch off / errors.
        // [3693-3695] traitremovenull (Batch582iLogic.cs, docs/582 B9 §1): removals finished / would have thrown (switch off) / errors.
        // [3696-3698] homeworkcopyold (Batch582iLogic.cs, docs/582 B9 §2): loop ticks at the old rate / seen with switch off / errors.
        // [3820-3822] laundrymoodeach (Batch582iLogic.cs, docs/582 B9 §3): buffs given the multiplier / Sims EA left without it (switch off) / errors.
        // [3823-3825] partycleanupnull (Batch582iLogic.cs, docs/582 B9 §4): cleanups finished null-safe / would have thrown (switch off) / errors.
        // [3826-3828] safelocunhide (Batch582iLogic.cs, docs/582 B9 §5): fallback placements unhidden / seen with switch off / errors.
        // [3829-3831] midlifeuiending (Batch582iLogic.cs, docs/582 B9 §6): reads that would have ended the crisis / seen with switch off / errors.
        // [3832-3834] nemesisnoromance (Batch582iLogic.cs, docs/582 B9 §7): nemeses made where EA's romance test refused / seen with switch off / errors.
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
        // [1895-1897] unused (fishscoremounted withdrawn after s58: docs/593 F2 was a false positive).
        // [2766-2771] mopunreachable (MopUnreachable.cs, s58 lead): puddles skipped in a run / runs that mopped after a skip / runs ended on an unreachable puddle with others left (switch off) / memo withholds / memo seen with switch off / errors. [2772-2774] petpuddletile: hits moved to the puddle used / seen with switch off / errors.
        // [2128-2131] initstacktrace (SeasonsHunt594.cs, docs/594 §1): Init walks skipped / OnAddedToQueue walks skipped / walks run (switch off) / errors. [2132-2134] weatherstonelistener (docs/594 §2): listeners removed at Dispose / left (switch off) / errors.
        // [2135-2137] weathermanagerthrow (SeasonsHunt594b.cs, docs/594 §3): null-dictionary lookups skipped / hit with the switch off (EA throws) / errors. [2138-2141] rainsheltercheap (§4): shelter tests skipped / run / passes / errors. [2142-2145] pondclearscan (§5): scans skipped (world pond in use) / skipped (remembered pond in use) / run / errors. [2146-2148] soakedstat (§6): rain re-adds not counted / counted (switch off) / errors.
        // [2149-2153] outerwearautoloop (SeasonsWeather596.cs, docs/596 §1): into / out / toddler autonomous tests answered no / would loop with the switch off / errors. [2154-2156] outerwearrestore (§2): outfit to return to stored / not stored (switch off) / errors.
        // [2163-2166] spoonslavechecks (SleepQueue597.cs via SpoonReset.cs SrHook, docs/597 §1): slave ticks given EA's per-sleeper checks / light-sleeper wakes / slave ticks skipped (switch off) / errors. [2760-2762] queueproximity (§2): places by squared distance / places that differ (switch off) / errors.
        // [2763-2765] subroutefail (SleepQueue597b.cs, docs/597 §3): route fails skipped (replacement pushed) / would be skipped (switch off) / errors. [2167-2169] petwakebed (§4): bed parts cleared / left holding the pet (switch off) / errors. [2170-2173] slavewakereason (§5): slave waits handled / multi-pass waits / slave waits seen (switch off) / errors or ceiling.
        // [2157-2162] hovertraintest (HoverTrain598.cs, docs/598 §5): NPCUseHoverTrain tests answered from the station memo / tests that ran EA's query (switch off: every gated test) / memo forgets / check mismatches (must be 0) / Run throws avoided / errors.
        // [2747-2752] newcartest (NewCar598.cs, docs/598 §6): TestHasNewCar / TestHasExpensiveNewCar answered from the new-car memo / tests that ran EA's (switch off: every call) / memo forgets / check mismatches (must be 0) / errors / ImportContent forgets.
        // [3229-3231] firefightercount (Batch593Logic.cs, B13 §13, docs/593 V8): extra firefighters not sent / sent (switch off) / errors.
        // [3232-3234] repairmanskill (Batch593Logic.cs, B13 §14, docs/593 V9): levels set to the roll / stacked (switch off) / errors.
        // [3235-3237] roundupreturn (Batch593Logic.cs, B13 §15, docs/593 V3): inits ended / run on (switch off) / errors.
        // [2926-2928] butlerpayalarm (Batch593Logic.cs, B13 §16, docs/593 V1): alarms removed / left (switch off) / errors.
        // [3238-3240] petharvestvfx (Batch593Logic.cs, B13 §17, docs/593 H1): effects kept and stopped / leaked (switch off) / errors.
        // [3061-3063] moodidleflavor (Batch589dLogic.cs, docs/589 B11 §26): rows appended / rows dropped (switch off) / errors.
        // [3835-3837] displaycasereset (EpBatch1aLogic.cs, docs/587 EP1 §1): resets kept contents / destroyed with switch off / errors.
        // [3241-3243] suatgradebonus (EpBatch1aLogic.cs, docs/587 EP1 §2): scores corrected / differ with switch off / errors.
        // [3838-3840] mermaidsharknull (EpBatch1aLogic.cs, docs/587 EP1 §3): fallbacks taken / NRE with switch off / errors.
        // [3845-3847] jetpackflyherecleanup (EpBatch1aLogic.cs, docs/587 EP1 §4): cleanups without jetpack / seen with switch off / errors.
        // [3848-3850] naniteroutefail (EpBatch1aLogic.cs, docs/587 EP1 §5): terrariums kept / destroyed with switch off / errors.
        // [3851-3853] springdanceclear (EpBatch1aLogic.cs, docs/587 EP1 §6): tallies cleared / kept with switch off / errors.
        // [3854-3856] nectarcopylevel10 (EpBatch1aLogic.cs, docs/587 EP1 §7): copies completed / incomplete with switch off / errors.
        // [3857-3859] harvesteatvfx (EpBatch1aLogic.cs, docs/587 EP1 §8): effects kept / leaking starts with switch off / errors.
        // [3860-3862] buffetemptypick (EpBatch1aLogic.cs, docs/587 EP1 §9): picks limited / could throw with switch off / errors.
        // [3863-3865] krakenfriendsailor (EpBatch1aLogic.cs, docs/587 EP1 §10): attacks dropped / scheduled with switch off / errors.
        // [3866-3868] mummycursetrait (EpBatch1bLogic.cs, docs/587 EP1 §12): visible picks / any-trait picks with switch off / errors.
        // [3869-3871] bullridercleanup (EpBatch1bLogic.cs, docs/587 EP1 §13): riders kept / cleared with switch off / errors.
        // [3872-3874] broomtipperformer (EpBatch1bLogic.cs, docs/587 EP1 §14): tips skipped / no performer with switch off / errors.
        // [3875-3877] balancingballcareer (EpBatch1bLogic.cs, docs/587 EP1 §15): non-acrobats refused / allowed with switch off / errors.
        // [3878-3880] elvisgnomeguard (EpBatch1bLogic.cs, docs/587 EP1 §16): guarded / seen with switch off / errors.
        // [3881-3883] elvisgnomecap (EpBatch1bLogic.cs, docs/587 EP1 §16): spawns skipped / rolled with a gnome, switch off / errors.
        // [3244-3246] cattoypickerrel (EpBatch1bLogic.cs, docs/587 EP1 §17): strangers added / left out with switch off / errors.
        // [3884-3886] gourmetpetrecipe (EpBatch1bLogic.cs, docs/587 EP1 §18): choices corrected / seen with switch off / errors.
        // [3887-3889] chipfamilycrystal (EpBatch1bLogic.cs, docs/587 EP1 §19): family crystals consumed / left with switch off / errors.
        // [3890-3892] chipcomponentorphan (EpBatch1bLogic.cs, docs/587 EP1 §19): orphans destroyed / left with switch off / errors.
        // [3893-3895] chipanycount (EpBatch1bLogic.cs, docs/587 EP1 §19): over-asks trimmed / over-asks with switch off / errors.
        // [3896-3898] nanitemerge (EpBatch1bLogic.cs, docs/587 EP1 §20): lists merged / left with switch off / errors.
        // [3899-3901] consignmentlastday (EpBatch2Logic.cs, docs/587 EP2 §7.1): last-day prices or returns / would throw with switch off / errors.
        // [3902-3904] bartendinghourlypay (EpBatch2Logic.cs, docs/587 EP2 §7.2): wages paid / unpaid with switch off / errors.
        // [3905-3907] influencefollowerbonus (EpBatch2Logic.cs, docs/587 EP2 §7.3): modifiers scaled / stepped with switch off / errors.
        // [3908-3910] broomarenachoice (EpBatch2Logic.cs, docs/587 EP2 §7.4): chosen brooms used / dropped with switch off / errors.
        // [3911-3913] fortunetellerscamodds (EpBatch3Logic.cs, docs/587 EP3 §8.1): scam rows rebuilt / shared rows with switch off / errors.
        // [3914-3916] consignmentskillcurve (EpBatch3Logic.cs, docs/587 EP3 §8.2): curves rebuilt / frequency curve with switch off / errors.
        // [2366-2369] simbotworld (SimbotWorld.cs, docs/870): MakeFrankensim tunings widened to Downtown + Future / tuning not in EA's shape, left alone / widening undone (switch off) / errors.
        // [3917-3919] consignmentstockleak (EpBatch3Logic.cs, docs/587 EP3 §8.3): objects destroyed / leaked with switch off / errors.
        // [3920-3922] herbqualityfloor (EpBatch3Logic.cs, docs/587 EP3 §8.4): decreases floored / below floor with switch off / errors.
        // [3923-3925] snackbowlslobvfx (EpBatch3Logic.cs, docs/587 EP3 §8.5): one-shots fired / leaked with switch off / errors.
        // [3926-3928] worldpondskatetest (EpBatch3Logic.cs, docs/587 EP3 §8.6): skate entries refused / offered against test with switch off / errors.
        // [3929-3931] pingpongopponent (EpBatch3Logic.cs, docs/587 EP3 §8.7): Sims removed / non-players with switch off / errors.
        // [3932-3934] mermaiddehydratelook (EpBatch3Logic.cs, docs/587 EP3 §8.8): look-ats re-enabled / left disabled with switch off / errors.
        // [3935-3937] meteormagnetorphan (EpBatch3Logic.cs, docs/587 EP3 §8.9): fragments destroyed / leaked with switch off / errors.
        // [3938-3940] daycarelatealarm (EpBatch3Logic.cs, docs/587 EP3 §8.10): stale alarms skipped / would throw with switch off / errors.
        // [3941-3943] boxofmysterywoohoo (EpBatch3Logic.cs, docs/587 EP3 §8.11): failed woohoos without roll / rolled after failure with switch off / errors.
        // [3944-3946] bodysculptorweight (EpBatch3Logic.cs, docs/587 EP3 §8.12): weights corrected / mirrored with switch off / errors.
        // [3947-3949] nectarconsignvalue (EpBatch3Logic.cs, docs/587 EP3 §8.13): values re-rolled / minimum with switch off / errors.
        // [3950-3952] straygnometns (EpBatch3Logic.cs, docs/587 EP3 §8.14): runs with right notice / stale notices with switch off / errors.
        // [4058-4061, 4084-4089] UnseenFallback.cs docs/202 §3.10 (townies only; unseenfbghost and unseenfbnosync default ON since the 2026-11-20 import, the rest per kUnseenFallback*): unseenfbghost holds with a ghost line (4058) / last legs planned after a ghost or
        //   G3 hold (4059) / of those, failed or cut (4060) / G3 recoveries refused, no async start or colour not proven (4061); unseenhouseboatview sightings asked at the shown
        //   spot (4084) / answered otherwise than the hidden-spot rule (4085); unseenfbnosync asked / recovered (4086-4087, refused = asked - recovered);
        //   safespotplanyield (Fixes.cs SafeSpot) yields after a verdict plan / given up after the yield, Sim gone or seen (4088-4089).
        // [1264-1269] boattaximemo (BoatTaxiMemo.cs, docs/851): boat (any Boat subclass) no-path failures recorded / rider routes planned with the boat off / of those, succeeded / stranded boat plans answered / ms of the recorded plans those answers stand for / refused (household, selected, unknown) or errors.
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
        // [2570-2588] unseenoutfitdefer (UnseenOutfitDefer.cs, issue #451, docs/586): 1981 instant sets deferred, 1982 of those replacing a waiting one, 1983 applied: Sim seen, 1984 applied: selected / not a townie now, 1985 applied before a delayed set or model, 1986 applied: flush (switch off, not live, travel, save), 1987 dropped: Sim gone, 1988 dropped: EA's own set ran, 1989 dropped: model set, 1990 waiting now, 1991 most waiting, 1992 not deferred: first set after instantiate, 1993 not deferred: delayed swap open, 1994 not deferred: category or not a Sim outfit path, 1995 apply threw, 1996 not deferred: within the 40 m margin, 1997 applied: entered the margin, 1998 worst seen-to-apply ms (an upper bound), 1999 seen applies later than 250 ms.
        // [2700-2701] safespotpool (SafeSpot, docs/74 §13): placement answers in a pool or under the ground skipped / cached start spots skipped.
        // [2702-2703] spotcheck (docs/560 §7): GoHome spot in view -> walk / lot-trip spot in view -> game's own trip.
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
        // [3388-3390] tspawninvdup (TreasureSpawn74.cs, docs/607 §2): extra treasure add-to-inventory subscriptions removed / seen with the switch off / errors.
        // [3394-3397] megaphonerun (WalkStyleLeak77.cs, docs/608 §2): WakeWithMegaphone Run requests moved to the RouteAway / released (no route-away) / left with the switch off / errors.
        // [3398-3401] dragoncurserun (WalkStyleLeak77.cs, docs/608 §3): Phantasmal Curse Run requests moved to the RouteAway / released / left with the switch off / errors.
        // [3402-3404] vhomewalk (WalkStyleLeak77.cs + RouteFixes.cs VirtualHomeHook, docs/608 §4): GoToVirtualHomeInternal walk-style requests released / left with the switch off / errors.
        // [3405-3410] speedskiplevel / speedskipnoactor (GameSpeed78.cs, docs/609 §2-3): Skip fallbacks given level 1 / level 3 for Normal (off) / errors; no-actor Skips run as Normal / time frozen (off) / errors.
        // [3411-3413] speedrecalclock (GameSpeed78.cs, docs/609 §4): recalculations kept the lock / lock cleared (off) / errors.
        // [3414-3416] speedlockowner (GameSpeed78.cs, docs/609 §5): build/buy exit unlocks skipped (lock not ours) / another holder released (off) / errors.
        // [3417-3420] alarmzeroperiod (GameSpeed78.cs, docs/609 §6): periods <= 0 as 1 tick / catch-ups by one division / repeating timers checked / errors.
        // [3421-3423] heartbrokenbcast (GameSpeed78.cs, docs/609 §7): replaced heartbroken broadcasters disposed / left registered (off) / errors.
        // [3424-3429] spawnerskip (SpawnerMapTag79.cs, docs/610 §2): spawns ended without search / armed full searches / streaks armed / errors / spawner searches / spawns placed by search.
        // [3430-3434] maptagtaskalive (SpawnerMapTag79.cs, docs/610 §3): visibility restarts / visibility ends (off) / controller restarts / controller ends (off) / errors.
        // [3435-3438] lotmgrcallbacks (LotCallbacks80.cs, docs/611 §2): stacked copies removed at PreWorldLoad / stacked copies seen (off) / copies removed at Shutdown / errors.
        // [3439-3441] fireplaceherb (LotCallbacks80.cs, docs/611 §3): burning herbs removed before a new one / left (off) / errors.
        // [3442-3445] rummagebcast (LotCallbacks80.cs, docs/611 §4): disposed at Cleanup / stale disposed at create / left (off) / errors.
        // [3446-3449] stairwaitend (StairDup82.cs, docs/612 §2): stair waits ended at 30 sim-min / past deadline (off) / past deadline household kept / errors.
        // [3450-3452] simdisposelink (StairDup82.cs, docs/612 §3): links kept / unbound disposes (off) / errors.
        // [3453-3456] rhexitunseen (StuckJobs83.cs, docs/613 §2): exit waits skipped / unseen NPC waits (off) / household or seen kept / errors.
        // [3580-3583] routefailprobe (RhEntry627.cs, docs/627 §2): probe plans tagged / counted as the Sim's (off) / errors / spare.
        // [3584-3587] rhentryrelax (RhEntry627.cs, docs/627 §4): unseen arrivals / kept EA / beforeEntering refused / errors.
        // [3588-3592] rhentryrelaxnear (RhEntry627.cs, docs/627 §4): alternate-slot entries / no slot or plan failed / on screen at action / entry refused / errors. [3593-3599] spare.
        // [3660-3664] playerfirst (Responsive631.cs, docs/631 §2): marks honoured / not honoured / replays first / player routes, switch off / errors. [3665-3669] spare.
        // [3670-3672] zoomtier (Responsive631.cs, docs/631 §3): tier-2 lifted by zoom / would lift, switch off / errors. [3673-3689] spare.
        // [3457-3460] tipsnpcend (StuckJobs83.cs, docs/613 §3): tips loops ended at low-LOD cap / past cap (off) / past cap household or seen kept / errors.
        // [3461-3463] barputdownwait (Scan84.cs, docs/614 §1): finished inline / hangs entered (off) / errors.
        // [3464-3466] putdownsurfaceuse (Scan84.cs, docs/614 §2): releases / leaks (off) / errors.
        // [3467-3469] swiperestore (Scan84.cs, docs/614 §3): restores / lost (off) / errors.
        // [3470-3472] unicornspawnfail (Scan84.cs, docs/614 §4): failed spawns ended / left invisible (off) / errors.
        // [3473-3475] datadiscputback (Scan84.cs, docs/614 §5): discs put back / lost (put-back refused) / errors.
        // [3476-3478] spoilbroadcast (Scan85.cs, docs/615 §1): broadcasters disposed / leaked (off) / errors.
        // [3479-3481] weatherstonerelisten (Scan85.cs, docs/615 §2): stale listeners removed / stacked (off) / errors.
        // [3482-3484] birthdayagerecord (Scan85.cs, docs/615 §3): age-ups with listener kept / dropped (off) / errors.
        // [3750-3773] docs/634 families (LoopSkip632.cs), 6 slots each: unseenpianist 3750, unseenmusicwatch 3756, unseenjam 3762, unseenshow 3768: unseen / off-screen tier / out of scope / check mismatches (+4,+5 spare); 3774-3779 spare.
        // [3780-3802] docs/635: unseenservicewait + unseenservice per-reason split (UnseenService88.cs, 3780-3795), spotbusydefer (3800-3802), spotcut (3803-3805); 3796-3799 spare.
        // [3806-3813] spotedgememo (SpotEdgeMemo.cs, docs/636): memo hits / misses / fallbacks / ms saved est / goals learned / check mismatches / seed hits / seed misses / first single try hits; 3815-3819 spare.
        // [3740-3745] bouncerplace (RouteSources.cs, docs/633): loop routes replaced / Run arrivals placed / out of scope / slot seen / errors / check mismatches; 3746-3749 spare.
        // [3700-3708] unseenloopskip (LoopSkip632.cs, docs/632): (b) capped unseen / offscreen, (a) stepped unseen / offscreen, listed out of scope, class not listed, off own task, errors, check mismatches; 3709-3739 spare.
        // [3600-3611] unseenservice (UnseenService88.cs, docs/628): unseen conv/drink/food served / off-screen tier the same / out of scope / precondition kept EA / off-screen re-check refused / RunBehavior false / errors / check mismatches; 3612-3619 spare.
        // [3490-3492] autmetarows (Hitch622.cs, AutShadow.cs AddObj, docs/622 §1): pair rows from the cache / rows computed / check mismatches.
        // [3493-3495] buffremovemerge (Hitch622.cs, Batch589cLogic.cs BBrdHook, docs/622 §2): no-op removes after one lookup / removes to EA's body / errors.
        // [3496-3498] swslots (Hitch622.cs SwOn, docs/622 §3): slot refills / check mismatches / check runs.
        // [3499-3501] stuckwatchscan (RouteFixes.cs StuckWatchdog, docs/622 §4): idle passes on the shared scan / on their own scan (off) / check mismatches.
        // [3502-3504] lifeeventidfirst (Hitch622.cs, docs/622 §5): events returned before AllActors / events to EA's body / errors.
        // [3505-3507] buffsrcempty (Hitch622.cs, MoodDrift.cs MdHook, docs/622 §6): emptied entries dropped / empty entries seen by the check / errors.
        // [3508-3510] buffpausedtimeout (Hitch622.cs, docs/622 §7): re-adds given the tuned timeout / MinValue re-adds (off) / errors.
        // [3511-3513] mooddriftnoalloc (Hitch622.cs, MoodDrift.cs MdHook, docs/622 §8): updates summed without allocation / check mismatches / reserved.
        // [3514-3519] free (docs/622 block).
        // [3520-3524] timeralarmleak (Wishes86.cs, docs/623 §1): alarms removed at leave / left (off) / orphans ended at fire / orphan fires (off) / errors.
        // [3525-3528] wishfeedbackkeep (Wishes86.cs, docs/623 §2): kept after a feedback throw / dropped (off) / broken node throws (EA kept) / errors.
        // [3529-3533] wishsubjectgone (Wishes86.cs, docs/623 §3): notices given / kept nodes filed again / notices lost (off) / kept unfiled (off) / errors.
        // [3620-3638] dormantprobe (DormantProbe.cs, docs/630 §2): plans / starts / pulses of unseen and dormant-candidate Sims and lots, hourly census; 3639-3659 reserved for dormantlots.
        // [3534-3537] wishupdatelost (Wishes86.cs, docs/623 §4): updates kept / lost (off) / loop guard / errors.
        // [3538-3540] socialworkerpromise (Wishes86.cs, docs/623 §5): promised wishes removed / left in slot (off) / errors.
        // [3550-3553] enumcmpnobox (Alloc87Comparers.cs, docs/625 §2): comparers installed / restored / errors / Check answer mismatches.
        // [3554-3558] alarmcmpnobox (Alloc87Comparers.cs, docs/625 §3): installed / restored / mTimers swapped in place / errors / Check answer mismatches.
        // [3485-3489] spotasync (Fixes.cs SpotVerdict, docs/616): checks answered by a job (sync plans avoided) / their wall ms / eligible but planned sync / placements dropped after the wait / shadow mismatches.
        // [3391-3393] sarcmummynpc (TreasureSpawn74.cs, docs/607 §3): tomb mummy descriptions removed from NpcHousehold at sarcophagus cleanup / left with the switch off / errors.
        // [2790-2792] pushwaitskip (PushWait604.cs, docs/604 §2): done Sims EA's double-step wait pass would have skipped / waiting calls on EA's body (switch off) / errors.
        // [2793-2796] detailprioexact (DetailPrio603.cs, docs/605 §3): exact answers / EA's body (null list or description, error) / check mismatches / errors.
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
        // [1158-1181] autt2local (AutShadowLocal.cs, docs/276 rank 5 piece 3): post gate codes, posts refused by the session, reference /
        // worker runs done, worker results dropped, pairs walked / dup / lpcap / skip / pass / fail / abort / exc, H8 local no-op / abort, sim-thread exc, reference pairs
        // [1182-1184] autt2local piece 4 (AutShadowLocal.cs LocalRows): local target object rows added, requests capped at 256, most rows in one request
        // [1185-1199] autt2local piece 5 (AutShadowLocal.cs LocalCompare): A0..A5 by walk index, walk-order misses, game report (pairs, bit-equal,
        // Test agree, top 1 valid / agree), game C2 order misses, game-only pairs, game records capped
        // [1270-1291] jobyieldroad / jobyieldroadow / jobyieldspawn / jobyieldready / jobyieldfp (JobYieldRoad.cs, RouteBackoff.cs AsyncWait, docs/800): step-aside asks, sleeps, ms slept and cars / objects gone per site
        // [1057] householdtoo (PlayerSims.cs HouseholdExempt, docs/271, ts3native [Experimental] HouseholdToo=1): household, selected or
        // unknown-scope asks the flag let through to a realistic-only path (worker answers, exact go-live); 0 with the flag off
        // [1230-1240] envlistmemoapply / envlistmemoverify (EnvBcProbes.cs EnvListApply, docs/274 §9): scans tried, memo reuse, live listed,
        // game's call (actor / other Sim out of scope, not indoor, seq overflow), errors, verify checks, differences, reorder-only differences
        // [1376-1377] bouncermemo (RouteSources.cs, docs/801): townie bouncer WatchDoor loops skipped / routed
        // [1370-1373] festivalhidden (FestivalHidden.cs, docs/856): see the list there.
        // [1500-1507] commodmapfast / commodmapshadow (CommodMapFast.cs, docs/313): see the list there.
        // [1328-1344] spawnfix / spawnoffworld (SpawnFix.cs, docs/857): see the list there.
        // [1301-1327] unseentraffic (1301-1315) + npccarexit (1316-1327) (UnseenTraffic.cs, docs/855): see the list there.
        // [1292-1300] trip arrivals (Fixes.cs, docs/804): visitarrive [1292] visits that arrived after the shortcut without a route;
        // visitonlot [1293] townie visits that started on the lot and arrived without the door route; gohomeshell [1294] apartment
        // homes asked, [1295] placed inside, [1296] walked (EA's trip), split [1297] near or no room point, [1298] no safe spot,
        // [1299] spot on another floor or lot, [1300] spot in view
        // [1482-1491] unseenoutfitnospin (UnseenOutfitNoSpin.cs, docs/809): 1482 spin changes taken, 1483-1489 refused by reason (helper started or
        // active, no change, posture / age / animation, form / ghost / age test / no stored outfit, category not safe, pending / open / fresh,
        // seen or near), 1490 taken whose set then ran at once, 1491 checks that threw
        // [1400-1420] stagedarrival (TripStage.cs, docs/805): see the list there; [1421-1430] icecreamdirect (IceCreamDirect.cs);
        // [1431-1440] nearvenue (NearVenue.cs): see the lists there
        // [1456-1470] unseenlanesnap (LaneSnap.cs, docs/807): see the list there; [1471-1475] unseenlegsnap (RouteSources.cs LegSnap)
        // [1441-1455] rabbitholedirect [1441-1452] and rhoutside [1453-1455] (RabbitHoleDirect.cs, docs/806): see the lists there
        // [1476-1481] SafeSpot repeat-search counters and safespotdedupe (Fixes.cs SafeSpot, docs/808)
        // [1585-1586] modquitguard (CompatResolve.cs CrQg0, docs/328 §7.5): mod world-quit handler exceptions caught / seen with the switch off.
        // [1587-1588] unseenfbgohere (UnseenFallbackFinish.cs UfTripOk, docs/328 §7.5): GoHere trip eligible for the unseen fallback, pre / finish.
        // [1589] compat precedence store targets not loaded at world load (CompatResolve.cs CompatPrecedenceLog, docs/328 §7.5).
        // [1508-1517] fglinbound (FglInBound.cs, docs/682 §9): indoors stages by our path / ts3native [FglInside] not answering / found / no indoor spot /
        // household or unknown Sim / shadow compares / shadow different (must stay 0) / errors / our ms / EA's stage ms in shadow.
        // [1543-1584] our loop cost (docs/330): deadrefsweep / deadrefcadence (DeadRefs.cs) and viewerpushdelta (UnseenViewers.cs): see the lists there
        // [1518-1529] docs/321 lane 0 / H1 / H3 table fill (Caps.cs, always on): AutShadow requests capped / by objects / by pairs /
        // most objects / most pairs; NearVenue groups over 32 / most open lots; spawnerskip hw / clears; backoff hw / clears; cookclick hw.
        // [1658-1661] deadrefexact (DeadRefs.cs, docs/334): partitions, objects in the last one, shared event lists, re-reads; [1669-1670] deadrefexactverify lists compared / differed
        // One size for every slot a fix may take (maintainer slots below 2000, contributor ranges up to 3999 since 2026-11-17, then
        // the contributor block 4000-4199, docs/registry.md), so a new fix never needs the array resized by hand.
        // [4352-4363] ladderfootshoo (LadderFoot.cs, docs/183 §9): scans of a lot's level links for a cross-level no-path plan / plans whose needed link is closed by
        // standing Sims / would ask them, off / exempt (household planner without ladderfootshoohh) / Sims asked to step away / Sims not asked (the game's push rules,
        // or household without its switch) / waits of the planner / second plan succeeded / gave up (timeout, cancel, nobody askable, second plan failed) / asked but
        // not waited for (not the Sim's own task) / a later plan of such a Sim succeeded within 60 sim-minutes / errors. Same slots as the contributor's combo branch
        // (4200-4351 left free; the contributor ranges up to 4089 are full).
        // [4300-4319] standwaitfix (StandWait.cs, docs/686; 4290-4299 left for a parallel branch): obstacle stand-and-waits / blocked cycles (replan failed, the game's
        // early return) / waits with blocked cycles / of them ran to the game's timeout / yields (on) / would yield (off) / push cycles / Sims pushed / would push (off) /
        // mutual cycles / pushes withheld (outranked) / plans without people / passes / would pass (off) / exempt cycles (household without standwaitfixhh) /
        // waits resolved by the fix / their timeout cycles left (upper bound of cycles saved) / errors / longest blocked run (gauge) / lines written.
        // [4364-4389] sharelanes / laddershare / simpass / angleentry (ShareLanes.cs, docs/686 §8): lane locks the game refused on stairs, doors, gates and ladders / of them
        // by the direction test only / shared / errors (all four) / would share, off / exempt (household without its switch) / lane waits begun / of them household /
        // tenths of sim-minutes waited / of them household / waits given up / stand-and-wait at a portal closed by standing Sims: passed / would / locked without
        // people too / ladder line skipped / would / exempt / simpass at the obstacle event / at the wait / would / exempt / kept (an object, or a Sim at the route's
        // end) / angleentry stairs / ladder / would / kept (exempt, or the approach plan failed).
        // [4390-4397] spiralshare (ShareLanes.cs, docs/686 §8): lane locks the game refused on spiral stairs / of them by the direction test only / shared / would
        // share, off / exempt (household without its switch) / the staircase's line skipped / would (somebody in the line), off / exempt.
        // [4414-4429] blockeduse (BlockedUse.cs, docs/686 §10): failed plans to an object's slots in DoRoute / would handle, off / exempt (household without
        // blockedusehh) / walked up to the object first / last stretch walked through objects (clip) / Sim put on the slot (snap in) / put back where it came from
        // (snap out) / walked out by itself / not this kind (a Sim blocks, portal locked, pet, object kind) / kept: object out of reach / kept: no slot in range /
        // walk up stopped / errors / clip walk stopped / snap out to a found spot / kept: cooling down.
        // [4620-4639] docs/656 (pets; moved from the combo branch's 4074-4089, which upstream uses): [4620-4621] petstartleskip (PetStartleSkip.cs, §8.1): NPC pets recorded after a PortalLocked startle flee route / startles skipped; [4622-4623] petlonghome (PetLongHome.cs, §8.2): unseen NPC pets / horses placed on the lot of a long GoHome / GoToLot walk / refused (seen, lot seen, no spot, lot kind); [4624] exempt (household, selected, unknown scope) and [4625] errors, shared by petstartleskip and petlonghome; [4626-4630] wildlotskip (WildLotSkip.cs, §8.3): wild horse / NPC animal (animal, lot) pairs remembered after a PortalLocked or NoPath GoToLot / picks drawn again without them / every weighted lot remembered (EA's pick kept) / long GoToLot walks cancelled / exempt or errors.
        // [4170-4180, 4196-4198] docs/656 (pets): [4170-4173] petarrivepoint (PetArrivePoint.cs, §4.1): inside front-door points skipped for NPC animals the door refuses / of those on a lot not validated yet / exempt / errors; [4174-4177] strayleave (StrayLeave.cs, §4.2): strays sent home / seen without the StrayPet buff / exempt / errors; [4178] petlonghome no-spot memo searches saved, [4179-4180] petlongvisit placed / refused (PetLongHome.cs, §4.3); [4196-4198] petlonghome herd spot share: shared / waited / no-spot not noted (§4.5). 4181-4195 are left to unseenboatwide (another PR).
        // [4462-4469] makebedboth (MakeBedBoth.cs, docs/686 §13): Make Bed runs on a bed with exactly one unmade free half / second half made from the first
        // side / closed half made from the other side (first make) / kept: the half can be reached / kept: no side to make it from / errors / would (off,
        // or household without makebedbothhh) / run ended early, flags put back.
        // [4398-4413] clipwait (ClipWait.cs, docs/686 §9): waits that reached the grace with a Sim or pet in the way / walked through / destination accepted (a floor
        // spot, or an object's spot taken by a Sim not using it) / errors / would, off / exempt (household without its switch) / kept: object in the way / Sim at the
        // object in use / social / player Go Here / plan fails without people too / destination-obstructed waits begun / tenths of sim-minutes stood in them / of
        // them ended as route failed / destination events let through after a walk / walks by household or selected Sims.
        // [4430-4445] petrelax (PetRelax.cs, docs/686 §11): walked through at the obstacle event / at the obstacle wait / stand-and-waits ended before the clipwait
        // grace / destination accepted / routes given the pass through lying pets' floor jigs / pets walked off a lane end before lying down / errors / would,
        // off / exempt (household without its switch) / kept: horse, foal or deer / object in the way / route's end (in use, social, player Go Here) / no free
        // spot for the lie-down / autonomous floor lie-downs seen / of them at a portal lane end / acts with a household Sim or pet.
        // [4446-4461] reachuse (ReachUse.cs, docs/686 §12): failed point plans of a reach-kind interaction in DoRoute / would handle, off / exempt (household
        // without reachusehh) / done from range: fire / floor (puddle, burnt tile) / small thing picked up or cleaned away / plant / of them without a walk /
        // slot chores given to blockeduse's rules / kept: nothing in reach / kept: other level or lot / kept: wall between / walk stopped / errors / kept:
        // cooling down / other failed point plans (not a reach kind, not to the target, a Sim blocks, portal locked, pet).
        // [4470-4493] objshare (ObjShare.cs, docs/686 §14): linewait: line waits from half a sim-minute / tenths of sim-minutes / timed out / cancelled;
        // rabbit hole enter-slot waits / tenths / timed out (15 sim-min) / cancelled; errors; rhshare: walked to a held slot / would (off) / exempt / kept
        // (holder out of scope, horse, rider, carrier) / left the wait on a cancel / dead reservations removed; servingshare: runs at a busy platter / lines
        // skipped / took with another Sim / would / exempt / kept (a Sim out of scope there) / last serving refused; linewait: line full / shorter waits.
        // [4494-4509] showershare (ShowerShare.cs, docs/686 §15): showertogether: joined a partner in a shower / would (off) / exempt / kept: not romantic
        // or disliked / kept: a teen or child / kept: other (several Sims, not showering, a third Sim) / exits held for the partner's; errors;
        // showerpee: bladders refilled in a shower / showers queued at the last warning / failures replaced by a shower / kept: no usable shower /
        // would / exempt / given back to the game (the shower never began); showertogether: joined in an all-in-one bathroom.
        // [4510-4525] viewlenient (ViewLenient.cs, docs/686 §16): failed plans of a view-kind interaction in DoRoute / would handle, off / exempt (household
        // without viewlenienthh) / handled, by kind: contemplate / art (ViewObjects) / performer / tv / stars / by outcome: stayed in place / walked to the
        // nearest place with a view / quiet ends (no balloon) / kept as the game has it (aborted plan, slot route, a route with no balloon, pet, another
        // walk) / walk stopped or ended with no view / errors / quiet ends while cooling down / tries after a walk (failed replan).
        // [4526-4537] socialreach (SocialReach.cs, docs/686 §17): failed plans and placements of talk socials (plain SocialInteractionA/B, one-person jig,
        // two humans) / would handle, off / exempt (a household Sim in the pair without socialreachhh, none without socialreach) / paired or jig socials left
        // alone / handled, by kind: the starter's route / the partner's route / the pair's placement / by outcome: from where they stand / walked to the
        // nearest place / kept as the game has it (too far, other level, wall, sitting, still walking, placement jig walk, cooling down) / walk stopped or
        // ended out of reach / errors.
        // [4538-4549] unstuck (Unstuck.cs, docs/686 §18): failed plans left to the game (no path, no destination, obstructed) / stuck signals (3 from one
        // spot over 2.5 sim-minutes, or the game's stuck check) / Sims the game's stuck check found no way for / would look, off / exempt (household
        // without unstuckhh) / kept: sitting, lying, swimming, riding, carried, cancelled / kept: can walk 3 m away / kept: walled in (no way through
        // objects either) / walked out through objects / walk out stopped / the failed route planned again after the walk out: ok / errors.
        // [4550-4565] crowdport (CrowdPort.cs, docs/686 §19): elevator skips (the game's teleport skip, early) / rabbit hole in (set on the enter slot) /
        // rabbit hole out (NPC exits released with the crowd) / station (subway, hover train: in or out) / other lines with a crowd (tube elevator,
        // firefighter pole; kept) / kept (switched off or holder changed during our wait) / errors / would, off / exempt (household without crowdporthh) /
        // horse, rider, carrier, toddler / elevator: line timeouts cut / elevator: no skip now (doors closed, car in use, other direction) / rabbit
        // hole in: holder out of scope or Sim far away / in: short wait ended by a free slot or a cancel / out: NPC exit waits (switch on) / out: ended
        // by the game's pacing.
        // [4566-4581] woohoosnap, pairsnap (WooHooSnap.cs, docs/686 §20): failed plans of WooHoo partners / would, off / exempt (household without
        // woohoosnaphh) / snaps at a bed / at a shower, tub, all-in-one bathroom or sauna / at another object / kept (other lot, not a route failure, no
        // spot, pet or rider, a social's own route, walk up cancelled) / snaps after a walk up / snap outs (both rules) / errors; paired socials:
        // failed placements, plans and walks / would, off / exempt (household without pairsnaphh) / placed (snap, turn, jig slot, plan through
        // objects) / kept (too far, other level, sitting, still walking, no spot) / errors.
        // [4640-4641] unseenboatgenboat (UnseenBoat.cs UbOwnsBoat, docs/851 §7): generated NPC boats in the NPC's own inventory not counted as its own boat / errors. Moved from the combo branch's 4060-4061, which main uses.
        // [4642-4650] unseenboatland (UnseenBoatLand.cs, docs/687): land plans tried / land kept / boat may be much shorter (EA plan) / land plan failed or swims (EA plan) / refused: not one land component / refused: household, selected, unknown scope, seen, water posture or interaction, owned boat / land plan ms / of it ms followed by EA's plan / errors. Moved from the combo branch's 3845-3853, which main uses (PR #540 items).
        // [4181-4195] unseenboatwide (UnseenBoatWide.cs, docs/248 §12): gate passes per relaxed rule: household lot visitor / indoor or raised goal / goal with no verdict / group / followers / child with a parent / unknown colours resolved to a crossing; unknown colours resolved to one land area (exact); owned boat left parked; party members placed; parties refused; no street side or mailbox spot; errors; shortcuts with a wide rule (subset of 839); members left (no spot). 4196-4199 spare.
        // [4320-4327] failreplanboat (FailReplanBoat.cs, docs/851 §8; block 4320-4329): failed plans with a Boat follower / kept (no driver, or the driver or an
        // occupant is household, selected, unknown scope or seen) / would switch the route-fail behaviour off (switch off) / switched off (DoRouteFail = false) /
        // DoRouteFail off already (EA's fail-path plan, or the caller's choice) / errors / lines written / flag set back (the cleared route planned again, succeeded).
        // [4462-4469] makebedboth (MakeBedBoth.cs, docs/686 §13): Make Bed runs on a bed with exactly one unmade free half / second half made from the first
        // side / closed half made from the other side (first make) / kept: the half can be reached / kept: no side to make it from / errors / would (off,
        // or household without makebedbothhh) / run ended early, flags put back.
        // [4596-4601] queuewake (QueueWake.cs, docs/686 §23): idle loops woken / would, off / loop ticks saved (on and off) / left: past the deadline /
        // left: not the first entry / errors.
        const int kCounterSlots = 4660;
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
            try { Alloc87Sync(); } catch (Exception) { }  // docs/625: the comparer swaps follow their switches
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
                case "visitarrive": return Instantiator.kVisitArrive;
                case "visitonlot": return Instantiator.kVisitOnLot;
                case "gohomeshell": return Instantiator.kGoHomeShell;
                case "rabbithole": return Instantiator.kBuildingTripShortcut;
                case "visit": case "visitlot": case "gotolot": case "gotocommunity": return Instantiator.kLotTripShortcut;
                case "rabbitholedirect": return Instantiator.kRabbitHoleDirect;
                case "rhoutside": return Instantiator.kRhOutside;
                case "unseenlanesnap": return Instantiator.kUnseenLaneSnap;
                case "unseenlegsnap": return Instantiator.kUnseenLegSnap;
                case "broadcast": return Instantiator.kBroadcasterThrottle;
                case "stagedarrival": return Instantiator.kStagedArrival;
                case "icecreamdirect": return Instantiator.kIceCreamDirect;
                case "nearvenue": return Instantiator.kNearVenue;
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
                case "envlistmemoapply": case "envlistmemoverify": return false; // docs/274 §9 R1 apply (unseen NPCs only) + sampled verify: test runs
                case "envlistmemoexact": case "envscancost": return false; // docs/274 §11 R1 exact (every Sim, the game's own scan) + env scan cost counters: test runs
                case "autofftick": return false; // docs/206 §15 Mode R tick item: test runs only
                case "autofft2": case "autofft2ic": case "autofflot": return false; // docs/208 §9 Mode R items (tier-2 rows, closest lot): test runs only
                case "autt2inuse": case "autt2fire": case "autt2dragwarm": return false; // docs/219 §3.1 tier-2 P1 worker guards + drag pre-warm (AutShadow.cs): test runs only
                case "autt2drag": case "autt2cache": case "autt2tuning": case "autt2lod": return false; // docs/126 §3 tier-2 H7-H10 worker hooks (AutShadow.cs): test runs only
                case "autt2cewarm": case "autt2frontdoor": case "autt2suite": return false; // tier-2 P2 pre-warm and guards (AutShadow.cs): test runs only
                case "autt2callpet": return false; // tier-2 P3 CallPet worker-owned picker list (AutShadow.cs): test runs only
                case "autt2l0": return false;  // tier-2 L0 local-phase probe (AutLocalProbe.cs): measurement only, test runs
                case "autt2lcov": return false;  // docs/276 rank 5 piece 1: local-phase C2 split by the local allow-list (AutLocalProbe.cs, needs autt2l0): measurement only, test runs
                case "autt2local": return false;  // docs/276 rank 5 piece 2+: HouseholdToo local exact path (AutShadowLocalCore.cs); nothing reads it before piece 3, and it also needs [Experimental] HouseholdToo=1
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
                case "dormantprobe": return false;  // log only: route plans, interaction starts, pulses and alarms of dormant-candidate lots per sim-hour (DormantProbe.cs, docs/630)
                case "callcensus": return false;  // log only: calls per sim-hour of the docs/589-590 speedup candidates, pass-through (CallCensus.cs, docs/584 §10)
                case "stcsocials": return false;  // proven-exact speedup, off until measured: GetSocialsForSTC's autonomous branch without its two redundant copies (SocialStc.cs, docs/584 §9)
                case "stcsocialscheck": return false;  // pure check for stcsocials: EA's method runs too, results compared (SocialStc.cs)
                case "motivebuf": return false;  // proven-exact speedup, off until measured: motive updates snapshot into a pooled list (MotiveBuf.cs, docs/584 §5)
                case "motivebufcheck": return false;
                case "buffbufcheck": return false;
                case "spotedgememo": return true;  // docs/636: IsPointInLotSafelyRoutable plans to the edge goals a full plan reached first; failure = EA's full call (exact)
                case "spotcut": return true;  // docs/635: a spotasync check's job gets the unseen-fallback MARK (deadline), a cut = no verdict
                case "spotbusydefer": return true;  // docs/635: a spotasync check whose tagged START was refused answers no verdict (no sync plan behind the worker's lock)
                case "unseenservicewait": return true;  // docs/635 (UnseenService88.cs): an unseen bar order waits for a server between orders instead of the linked hand-off
                case "spotasync": return true;  // docs/616: SafeSpot's full routability check of an unseen NPC as a watchdog-tagged async job (needs [RouteAsync] WatchdogKind2=1 or Kinds bit 2; uncut)
                case "routableasync": return Instantiator.kRoutableAsync;  // ON 2026-11-19 (Rodney), with [RouteAsync] Kinds=2: async PlanFromPoint only for unseen NPCs' IsPointInLotSafelyRoutable (RouteBackoff.cs, docs/584 §3)  // check for ts3native ilp_buffbuf: UpdateBuffs' delayed-buff list empty and consistent (MotiveBuf.cs, docs/584 §5)  // pure check for motivebuf: each snapshot compared with the game's copy (MotiveBuf.cs)
                case "tpdoorrights": return true;  // F1's unseen-teleport lot test by the Sim's own door rights, EA's one-way sentinel (UnseenTeleportGoal.cs, docs/584 §7)
                case "tpaptgoal": return true;  // F1: an unseen NPC's own home apartment / shell lot judged by the goal room only (hidden rooms still refused; UnseenTeleportGoal.cs, docs/584 §8)
                case "visitcar": return false;  // unseen NPC visits keep driving at EA's coin flip (UnseenVisitTrip.cs, docs/584 §1)
                case "visitwalkfinish": return false;  // unseen NPC still walking to a visit after 60 sim-min finishes at the lot (UnseenVisitTrip.cs, docs/584 §1)
                case "autlastsearch": return false;  // log only: each Sim's last autonomy search and its result, for the census (AutLive.cs, docs/584 §4)
                case "ufplansplit": return false;  // log only: where a slow unseen-fallback plan's wall time goes (UnseenFallback.cs)
                case "doorshare": return Instantiator.kDoorShare;  // DoorShare.cs (docs/674): mark Sim-independent door answers for ts3native [RouteSnap] DoorShare, off
                case "doorgen": return Instantiator.kDoorGen;  // DoorGen.cs: bump ts3native's door generation when a door answer input changes (DoorMemoX=3)
                case "alarmtime": case "boatreplanlog": case "owresettime": case "synctime": return false; // docs/247 §9: logging, hooks only with kHitchCauseHooks
                case "legreplanskip": return Instantiator.kLegReplanSkip;
                case "bouncermemo": return Instantiator.kBouncerMemo;
                case "bouncerskip": return Instantiator.kBouncerSlotSkip;
                case "bouncerplace": return Instantiator.kBouncerPlace;  // docs/633 (RouteSources.cs), unseen bouncer placed on its slot, off
                case "idlememo": return Instantiator.kIdlePlanMemo;
                case "stuckchecknear": return Instantiator.kStuckCheckNear;
                case "failrepeat": return Instantiator.kFailRepeat;
                case "jobyield": return Instantiator.kJobYield;
                case "ladderfootshoo": return Instantiator.kLadderFootShoo;  // LadderFoot.cs (docs/183 §9): Sims standing on the lane ends of a ladder or stair that a failed plan needs are asked to step away the game's way, then the plan is made once more, off
                case "ladderfootshoohh": return Instantiator.kLadderFootShooHousehold;  // LadderFoot.cs (docs/183 §9): ladderfootshoo for household and selected Sims too (as planner and as the Sim asked), off
                case "jobyieldroad": return Instantiator.kJobYieldRoad;
                case "jobyieldroadow": return Instantiator.kJobYieldRoadOw;
                case "jobyieldspawn": return Instantiator.kJobYieldSpawn;
                case "jobyieldready": return Instantiator.kJobYieldReady;
                case "jobyieldfp": return Instantiator.kJobYieldFp;
                case "drivertaskasync": return Instantiator.kDriverTaskAsync;
                case "stuckcheckown": return Instantiator.kStuckCheckOwn;
                case "overwatchstuckslice": return Instantiator.kOverwatchStuckSlice;
                case "seasonmarkercache": return Instantiator.kSeasonMarkerCache;
                case "safespotyield": return Instantiator.kSafeSpotYield;
                case "safespotplanyield": return Instantiator.kSafeSpotPlanYield;
                case "disposespread": return Instantiator.kDisposeSpread;
                case "overwatchstucknotice": return Instantiator.kOverwatchStuckNoticeSummary;
                case "preplancb": return Instantiator.kPrePlanCallback;
                case "stuckcheckcap": return Instantiator.kStuckCheckCap;
                case "owprobeplans": return Instantiator.kOverwatchIgnoreStuckProbes;
                case "unseenfbstuck": return Instantiator.kUnseenFallbackStuck;
                case "unseenfbplan": return Instantiator.kUnseenFallbackPlan;
                case "unseenfbteleport": return Instantiator.kUnseenFallbackTeleport;
                case "unseenfbghost": return Instantiator.kUnseenFallbackGhost;
                case "unseenfbnosync": return Instantiator.kUnseenFallbackNoSync;
                case "unseenhouseboatview": return Instantiator.kUnseenHouseboatView;
                case "unseenfbsites": return Instantiator.kUnseenFallbackSites;
                case "unseenfbpre": return Instantiator.kUnseenFallbackPre;
                case "unseenfbarrive": return Instantiator.kUnseenFallbackArrive;
                case "unseenfbleave": return Instantiator.kUnseenFallbackLeave;
                case "unseenfbtrip": return Instantiator.kUnseenFallbackTrip;
                case "unseenfbgohere": return Instantiator.kUnseenFbGoHere;
                case "modquitguard": return Instantiator.kModQuitGuard;
                case "unseenfbtriphold": return Instantiator.kUnseenFallbackTripHold;
                case "unseenfbloopdet": return Instantiator.kUnseenFallbackLoopDetect;
                case "goalhhexempt": return Instantiator.kGoalHouseholdExempt;
                case "unseenfbloopbreak": return Instantiator.kUnseenFallbackLoopBreak;
                case "unseenfbfinish": return Instantiator.kUnseenFallbackFinish;
                case "unseenroute": return Instantiator.kUnseenRouteRelaxed;
                case "unseenboat": return Instantiator.kUnseenBoat;
                case "unseenboatstart": return Instantiator.kUnseenBoatStart;
                case "failreplanboat": return Instantiator.kFailReplanBoat;  // FailReplanBoat.cs (docs/851 §8): an unseen NPC boat's failed plan skips EA's route-fail behaviour, off
                case "unseenboatland": return Instantiator.kUnseenBoatLand;
                case "unseenboatgenboat": return Instantiator.kUnseenBoatGenBoat;
                case "unseenboatwide": return Instantiator.kUnseenBoatWide;      // off (UnseenBoatWide.cs, docs/248 §12)
                case "boattaximemo": return Instantiator.kBoatTaxiMemo;
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
                case "unseentraffic": return Instantiator.kUnseenTraffic;
                case "npccarexit": return Instantiator.kNpcCarExit;
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
                case "burglarapt": return Instantiator.kBurglarApt;
                case "gigwaitleave": return Instantiator.kGigWaitLeave;
                case "sculptpose": return Instantiator.kSculptPose;
                case "festivalhidden": return Instantiator.kFestivalHidden;
                case "commodmapfast": return Instantiator.kCommodMapFast;
                case "commodmapshadow": return Instantiator.kCommodMapShadow;
                case "trainboost": return Instantiator.kTrainBoost;
                case "tagunseen": return Instantiator.kTagUnseen;
                case "eatoutsidecount": return Instantiator.kEatOutsideCount;
                case "spawnfix": return Instantiator.kSpawnFix;
                case "spawnoffworld": return Instantiator.kSpawnOffWorld;
                case "fireactivejob": return Instantiator.kFireActiveJob;
                case "firegreet": return Instantiator.kFireGreet;
                case "horseshoesolo": return Instantiator.kHorseshoeSolo;
                case "fglinbound": return Instantiator.kFglInBound;  // FglInBound.cs (docs/682 §9): FindGoodLocationNearby's indoors stage announced to ts3native [FglInside] (no placement tests on tiles without a room; EA's answer), off
                case "fglinboundhh": return Instantiator.kFglInBoundHousehold;  // FglInBound.cs (docs/682): fglinbound for household and selected Sims too, off
                case "fireleftover": return Instantiator.kFireLeftover;
                case "aptgasleak": return Instantiator.kAptGasLeak;
                case "aptpossess": return Instantiator.kAptPossess;
                case "firedoorkeep": return Instantiator.kFireDoorKeep;
                case "standwaitfix": return Instantiator.kStandWaitFix;  // StandWait.cs (docs/686): a blocked obstacle stand-and-wait yields to pushes, pushes its blockers, passes when only people block, off
                case "standwaitfixhh": return Instantiator.kStandWaitFixHousehold;  // StandWait.cs (docs/686): standwaitfix for household and selected Sims too, off
                case "sharelanes": return Instantiator.kShareLanes;  // ShareLanes.cs (docs/686 §8): a stair, door, gate or ladder lane held from the other end is shared; a portal closed by standing Sims is tried without people at once, off
                case "sharelaneshh": return Instantiator.kShareLanesHousehold;  // ShareLanes.cs: sharelanes for household and selected Sims too (as the arriving Sim and as a lane holder), off
                case "laddershare": return Instantiator.kLadderShare;  // ShareLanes.cs (docs/686 §8): a ladder climb does not wait for its turn in the ladder's line, off
                case "laddersharehh": return Instantiator.kLadderShareHousehold;  // ShareLanes.cs: laddershare for household and selected Sims too, off
                case "spiralshare": return Instantiator.kSpiralShare;  // ShareLanes.cs (docs/686 §8): a spiral staircase's one lane is shared in both directions and its walk does not wait for its turn in the staircase's line, off
                case "spiralsharehh": return Instantiator.kSpiralShareHousehold;  // ShareLanes.cs: spiralshare for household and selected Sims too, off
                case "simpass": return Instantiator.kSimPass;  // ShareLanes.cs (docs/686 §8): a walking Sim with only Sims in its way walks through them (the game's ignore-obstacles timer) instead of the stand-and-wait, off
                case "simpasshh": return Instantiator.kSimPassHousehold;  // ShareLanes.cs: simpass for household and selected Sims too (as walker and as the Sim walked through), off
                case "angleentry": return Instantiator.kAngleEntry;  // ShareLanes.cs (docs/686 §8): stairs and ladders climbed from below are approached by a point plan without facing, off
                case "angleentryhh": return Instantiator.kAngleEntryHousehold;  // ShareLanes.cs: angleentry for household and selected Sims too, off
                case "lanewaitlog": return Instantiator.kLaneWaitLog;  // ShareLanes.cs (docs/686 §8): one line per wait for a portal lane, off
                case "clipwait": return Instantiator.kClipWait;  // ClipWait.cs (docs/686 §9): a Sim that stood past the grace with only Sims or pets in its way walks through them, off
                case "clipwaithh": return Instantiator.kClipWaitHousehold;  // ClipWait.cs: clipwait for household and selected Sims too (as walker and as the Sim walked through), off
                case "petrelax": return Instantiator.kPetRelax;  // PetRelax.cs (docs/686 §11): small pets and the Sims around them walk through each other at once, lying pets are not obstacles, no lie-down on a lane end, off
                case "petrelaxhh": return Instantiator.kPetRelaxHousehold;  // PetRelax.cs: petrelax for household and selected Sims and pets too, off
                case "petrelaxlog": return Instantiator.kPetRelaxLog;  // PetRelax.cs (docs/686 §11): one line per act, off
                case "clipwaitlog": return Instantiator.kClipWaitLog;  // ClipWait.cs (docs/686 §9): one line per walk, off
                case "linewaitlog": return Instantiator.kLineWaitLog;  // ObjShare.cs (docs/686 §14): one line per wait in an object's line or for a rabbit hole's enter slot, and per share, off
                case "rhshare": return Instantiator.kRhShare;  // ObjShare.cs (docs/686 §14): an NPC walks to a rabbit hole enter slot another Sim holds; a cancelled Sim leaves the wait, off
                case "rhsharehh": return Instantiator.kRhShareHousehold;  // ObjShare.cs: the same for household and selected Sims, off
                case "servingshare": return Instantiator.kServingShare;  // ObjShare.cs (docs/686 §14): NPCs grab servings from one platter at once, no line, last serving taken once, off
                case "servingsharehh": return Instantiator.kServingShareHousehold;  // ObjShare.cs: the same for household and selected Sims, off
                case "showertogether": return Instantiator.kShowerTogether;  // ShowerShare.cs (docs/686 §15): an NPC joins its romantic partner (young adult or older) in a shower or all-in-one bathroom, no line, off
                case "showertogetherhh": return Instantiator.kShowerTogetherHousehold;  // ShowerShare.cs: the same for household and selected Sims, off
                case "showerpee": return Instantiator.kShowerPee;  // ShowerShare.cs (docs/686 §15): an NPC that has to pee with no usable toilet on the lot takes a shower that refills its bladder, off
                case "showerpeehh": return Instantiator.kShowerPeeHousehold;  // ShowerShare.cs: the same for household and selected Sims, off
                case "showersharelog": return Instantiator.kShowerShareLog;  // ShowerShare.cs: one line per showertogether / showerpee act, off
                case "crowdport": return Instantiator.kCrowdPort;  // CrowdPort.cs (docs/686 §19): an NPC at a crowded elevator, rabbit hole door or station skips the queue by the game's own teleport paths; NPC exits leave together, off
                case "crowdporthh": return Instantiator.kCrowdPortHousehold;  // CrowdPort.cs: the same for household and selected Sims, off
                case "crowdportlog": return Instantiator.kCrowdPortLog;  // CrowdPort.cs (docs/686 §19): one line per act, off
                case "standwaitlog": return Instantiator.kStandWaitLog;  // StandWait.cs (docs/686): one line per wait with blocked cycles, off
                case "blockeduse": return Instantiator.kBlockedUse;  // BlockedUse.cs (docs/686 §10): an NPC whose route to an object's slot fails on furniture or a wall walks the last stretch through objects or is put on the slot, and back afterwards, off
                case "blockedusehh": return Instantiator.kBlockedUseHousehold;  // BlockedUse.cs: the same for household and selected Sims (stands alone), off
                case "makebedboth": return Instantiator.kMakeBedBoth;  // MakeBedBoth.cs (docs/686 §13): an NPC makes a bed half it cannot reach from the other side, off
                case "makebedbothhh": return Instantiator.kMakeBedBothHousehold;  // MakeBedBoth.cs: the same for household and selected Sims (stands alone), off
                case "makebedbothlog": return Instantiator.kMakeBedBothLog;  // MakeBedBoth.cs (docs/686 §13): one line per act, off
                case "reachuse": return Instantiator.kReachUse;  // ReachUse.cs (docs/686 §12): an NPC's chore (fire, mop, pick up, plant) whose spot is closed is done from within reach; chores at a slot get blockeduse's rules, off
                case "reachusehh": return Instantiator.kReachUseHousehold;  // ReachUse.cs: the same for household and selected Sims (stands alone), off
                case "reachuselog": return Instantiator.kReachUseLog;  // ReachUse.cs (docs/686 §12): one line per act, off
                case "viewlenient": return Instantiator.kViewLenient;  // ViewLenient.cs (docs/686 §16): an NPC whose route to contemplate, view or watch fails looks from where it stands or the nearest place with a view, no balloon, off
                case "viewlenienthh": return Instantiator.kViewLenientHousehold;  // ViewLenient.cs: the same for household and selected Sims (stands alone), off
                case "viewlenientlog": return Instantiator.kViewLenientLog;  // ViewLenient.cs (docs/686 §16): one line per act, off
                case "socialreach": return Instantiator.kSocialReach;  // SocialReach.cs (docs/686 §17): a talk social between NPCs whose route or placement fails is done from where the two stand (3 m, same level, no wall) or after a walk to the nearest place, off
                case "socialreachhh": return Instantiator.kSocialReachHousehold;  // SocialReach.cs: the same for pairs with a household or selected Sim (stands alone), off
                case "socialreachlog": return Instantiator.kSocialReachLog;  // SocialReach.cs (docs/686 §17): one line per act, off
                case "unstuck": return Instantiator.kUnstuck;  // Unstuck.cs (docs/686 §18): an NPC or pet that fails 3 plans from one spot over 2.5 sim-minutes and has no normal way out walks out through the objects (walls count), off
                case "unstuckhh": return Instantiator.kUnstuckHousehold;  // Unstuck.cs: the same for household and selected Sims and pets (stands alone), off
                case "unstucklog": return Instantiator.kUnstuckLog;  // Unstuck.cs (docs/686 §18): one line per act, off
                case "firetrapcount": return Instantiator.kFireTrapCount;
                case "firenre": return Instantiator.kFireNre;
                case "firecount": return Instantiator.kFireCount;
                case "birthdayagers": return Instantiator.kBirthdayAgers;
                case "sneakoutlistener": return Instantiator.kSneakOutListener;
                case "promcarpool": return Instantiator.kPromCarpool;
                case "curfewalarm": return Instantiator.kCurfewAlarm;
                case "trickortreatalarm": return Instantiator.kTrickOrTreatAlarm;
                case "snowfightjoinwait": return Instantiator.kSnowFightJoinWait;
                case "hauntedumbrella": return Instantiator.kHauntedUmbrella;
                case "permazombiemoon": return Instantiator.kPermaZombieMoon;
                case "steadygigmiss": return Instantiator.kSteadyGigMiss;
                case "npcshowstuck": return Instantiator.kNpcShowStuck;
                case "gigpullaudience": return Instantiator.kGigPullAudience;
                case "lunacysocials": return Instantiator.kLunacySocials;
                case "fairyauratimer": return Instantiator.kFairyAuraTimer;
                case "npcseasonalparty": return Instantiator.kNpcSeasonalParty;
                case "professorterm": return Instantiator.kProfessorTerm;
                case "protestlastday": return Instantiator.kProtestLastDay;
                case "seasickpaused": return Instantiator.kSeasickPaused;
                case "resortweddingalarm": return Instantiator.kResortWeddingAlarm;
                case "plumbotfacelistener": return Instantiator.kPlumbotFaceListener;
                case "efficientchip": return Instantiator.kEfficientChip;
                case "sentiencename": return Instantiator.kSentienceName;
                case "jetpackflyingbuff": return Instantiator.kJetpackFlyingBuff;
                case "horsetraitscore": return Instantiator.kHorseTraitScore;
                case "wildhorsegender": return Instantiator.kWildHorseGender;
                case "littercap": return Instantiator.kLitterCap;
                case "phoneloadreset": return Instantiator.kPhoneLoadReset;
                case "phonecallschedule": return Instantiator.kPhoneCallSchedule;
                case "phonecallrearm": return Instantiator.kPhoneCallRearm;
                case "woohootywait": return Instantiator.kWooHootyWait;
                case "invitecallreply": return Instantiator.kInviteCallReply;
                case "gradvisitors": return Instantiator.kGradVisitors;
                case "resumetns": return Instantiator.kResumeTns;
                case "unitravelflag": return Instantiator.kUniTravelFlag;
                case "returnhomealarms": return Instantiator.kReturnHomeAlarms;
                case "phonefixup": return Instantiator.kPhoneFixUp;
                case "phonegive": return Instantiator.kPhoneGive;
                case "wildherdmother": return Instantiator.kWildHerdMother;
                case "splitpartner": return Instantiator.kSplitPartner;
                case "movingflag": return Instantiator.kMovingFlag;
                case "sandscoins": return Instantiator.kSandsCoins;
                case "tombmusic": return Instantiator.kTombMusic;
                case "artsellnoartist": return Instantiator.kArtSellNoArtist;
                case "royaltyorphan": return Instantiator.kRoyaltyOrphan;
                case "promoteshift": return Instantiator.kPromoteShift;
                case "gotoworkstale": return Instantiator.kGoToWorkStale;
                case "handsetreset": return Instantiator.kHandsetReset;
                case "flowersinuse": return Instantiator.kFlowersInUse;
                case "uselistprune": return Instantiator.kUseListPrune;
                case "chestuselist": return Instantiator.kChestUseList;
                case "resetinusefree": return Instantiator.kResetInUseFree;
                case "putdowninuse": return Instantiator.kPutDownInUse;
                case "autonomyinuseblock": return Instantiator.kAutonomyInUseBlock;
                case "npcbusypartner": return Instantiator.kNpcBusyPartner;
                case "socialreplanlog": return Instantiator.kSocialReplanLog;
                case "barorderlog": return Instantiator.kBarOrderLog;
                case "petputdownwait": return Instantiator.kPetPutDownWait;
                case "puddlereach": return Instantiator.kPuddleReach;
                case "mopunreachable": return Instantiator.kMopUnreachable;
                case "petpuddletile": return Instantiator.kPetPuddleTile;
                case "initstacktrace": return Instantiator.kInitStackTrace;
                case "weatherstonelistener": return Instantiator.kWeatherStoneListener;
                case "weathermanagerthrow": return Instantiator.kWeatherManagerThrow;
                case "rainsheltercheap": return Instantiator.kRainShelterCheap;
                case "pondclearscan": return Instantiator.kPondClearScan;
                case "soakedstat": return Instantiator.kSoakedStat;
                case "hovertraintest": return Instantiator.kHoverTrainTest;
                case "speedskiplevel": return Instantiator.kSpeedSkipLevel;  // docs/609 §2 (GameSpeed78.cs), EA bug fix, off
                case "speedskipnoactor": return Instantiator.kSpeedSkipNoActor;  // docs/609 §3 (GameSpeed78.cs), EA bug fix, off
                case "speedrecalclock": return Instantiator.kSpeedRecalcLock;  // docs/609 §4 (GameSpeed78.cs), EA bug fix, off
                case "speedlockowner": return Instantiator.kSpeedLockOwner;  // docs/609 §5 (GameSpeed78.cs), EA bug fix, off
                case "heartbrokenbcast": return Instantiator.kHeartBrokenBcast;  // docs/609 §7 (GameSpeed78.cs), EA bug fix, off
                case "spawnerskip": return Instantiator.kSpawnerSkip;  // docs/610 §2 (SpawnerMapTag79.cs), speedup with EA's failure ending, off
                case "maptagtaskalive": return Instantiator.kMapTagTaskAlive;  // docs/610 §3 (SpawnerMapTag79.cs), EA bug fix, off
                case "lotmgrcallbacks": return Instantiator.kLotMgrCallbacks;  // docs/611 §2 (LotCallbacks80.cs), EA bug fix, off
                case "fireplaceherb": return Instantiator.kFireplaceHerb;  // docs/611 §3 (LotCallbacks80.cs), EA bug fix, off
                case "rummagebcast": return Instantiator.kRummageBcast;  // docs/611 §4 (LotCallbacks80.cs), EA bug fix, off
                case "stairwaitend": return Instantiator.kStairWaitEnd;  // docs/612 §2 (StairDup82.cs), EA bug fix, off
                case "simdisposelink": return Instantiator.kSimDisposeLink;  // docs/612 §3 (StairDup82.cs), EA bug fix, off
                case "rhexitunseen": return Instantiator.kRhExitUnseen;  // docs/613 §2 (StuckJobs83.cs), EA bug fix, off
                case "routefailprobe": return Instantiator.kRouteFailProbe;  // docs/627 §2 (RhEntry627.cs), log attribution fix, off
                case "rhentryrelax": return Instantiator.kRhEntryRelax;  // docs/627 §4 (RhEntry627.cs), unseen NPC relaxed rabbit hole entry, off
                case "rhentryrelaxnear": return Instantiator.kRhEntryRelaxNear;  // docs/627 §4 (RhEntry627.cs), middle tier sub-key, off
                case "playerfirst": return Instantiator.kPlayerFirst;  // docs/631 §2 (Responsive631.cs), player-ordered routes and replays first, off
                case "zoomtier": return Instantiator.kZoomTier;  // docs/631 §3 (Responsive631.cs), on-screen but too small counts as off screen for tier 2, off
                case "tipsnpcend": return Instantiator.kTipsNpcEnd;  // docs/613 §3 (StuckJobs83.cs), EA bug fix, off
                case "barputdownwait": return Instantiator.kBarPutDownWait;  // docs/614 §1 (Scan84.cs), EA bug fix, off
                case "putdownsurfaceuse": return Instantiator.kPutDownSurfaceUse;  // docs/614 §2 (Scan84.cs), EA bug fix, off
                case "swiperestore": return Instantiator.kSwipeRestore;  // docs/614 §3 (Scan84.cs), EA bug fix, off
                case "unicornspawnfail": return Instantiator.kUnicornSpawnFail;  // docs/614 §4 (Scan84.cs), EA bug fix, off
                case "datadiscputback": return Instantiator.kDataDiscPutBack;  // docs/614 §5 (Scan84.cs), EA bug fix, off
                case "spoilbroadcast": return Instantiator.kSpoilBroadcast;  // docs/615 §1 (Scan85.cs), EA bug fix, off
                case "weatherstonerelisten": return Instantiator.kWeatherStoneRelisten;  // docs/615 §2 (Scan85.cs), EA bug fix, off
                case "unseenservice": return Instantiator.kUnseenService;  // docs/628 (UnseenService88.cs), unseen bar orders served at once, off
                case "unseenloopskip": return Instantiator.kUnseenLoopSkip;  // docs/632 (LoopSkip632.cs), unseen allow-listed loops end early / step, off
                case "unseenloopskip_offscreen": return Instantiator.kUnseenLoopSkipOffscreen;  // docs/632 tier 2 (off-screen near / zoomtier), off
                case "unseenpianist": return Instantiator.kUnseenPianist;  // docs/634 (LoopSkip632.cs), unseen pianist loop stepped, off
                case "unseenmusicwatch": return Instantiator.kUnseenMusicWatch;  // docs/634, unseen piano watcher ends at the cap, off
                case "unseenjam": return Instantiator.kUnseenJam;  // docs/634, unseen guitar jam stepped, off
                case "unseenshow": return Instantiator.kUnseenShow;  // docs/634, unseen show-venue visit stepped, off
                case "unseenservice_offscreen": return Instantiator.kUnseenServiceOffscreen;  // docs/628 tier 2 (off-screen, not far), off
                case "birthdayagerecord": return Instantiator.kBirthdayAgeRecord;  // docs/615 §3 (Scan85.cs), EA bug fix, off
                case "autmetarows": return Instantiator.kAutMetaRows;  // docs/622 §1 (Hitch622.cs), exact speedup, off
                case "buffremovemerge": return Instantiator.kBuffRemoveMerge;  // docs/622 §2 (Hitch622.cs), exact speedup, off
                case "swslots": return Instantiator.kSwSlots;  // docs/622 §3 (Hitch622.cs), exact speedup, off
                case "stuckwatchscan": return Instantiator.kStuckWatchScan;  // docs/622 §4 (RouteFixes.cs), exact speedup, off
                case "lifeeventidfirst": return Instantiator.kLifeEventIdFirst;  // docs/622 §5 (Hitch622.cs), exact speedup, off
                case "buffsrcempty": return Instantiator.kBuffSrcEmpty;  // docs/622 §6 (Hitch622.cs), EA bug fix, off
                case "buffpausedtimeout": return Instantiator.kBuffPausedTimeout;  // docs/622 §7 (Hitch622.cs), EA bug fix, off
                case "mooddriftnoalloc": return Instantiator.kMoodDriftNoAlloc;  // docs/622 §8 (Hitch622.cs), exact speedup, off
                case "timeralarmleak": return Instantiator.kTimerAlarmLeak;  // docs/623 §1 (Wishes86.cs), EA bug fix, off
                case "wishfeedbackkeep": return Instantiator.kWishFeedbackKeep;  // docs/623 §2 (Wishes86.cs), EA bug fix, off
                case "wishsubjectgone": return Instantiator.kWishSubjectGone;  // docs/623 §3 (Wishes86.cs), EA bug fix, off
                case "wishupdatelost": return Instantiator.kWishUpdateLost;  // docs/623 §4 (Wishes86.cs), EA bug fix, off
                case "socialworkerpromise": return Instantiator.kSocialWorkerPromise;  // docs/623 §5 (Wishes86.cs), EA bug fix, off
                case "enumcmpnobox": return Instantiator.kEnumCmpNoBox;  // docs/625 §2 (Alloc87Comparers.cs), exact allocation cut, off
                case "alarmcmpnobox": return Instantiator.kAlarmCmpNoBox;  // docs/625 §3 (Alloc87Comparers.cs), exact allocation cut, off
                case "alarmzeroperiod": return Instantiator.kAlarmZeroPeriod;  // docs/609 §6 (GameSpeed78.cs), EA bug fix (hang guard), off
                case "tspawninvdup": return Instantiator.kTreasureSpawnInvDup;  // docs/607 §2 (TreasureSpawn74.cs), EA bug fix, off
                case "megaphonerun": return Instantiator.kMegaphoneRun;  // docs/608 §2 (WalkStyleLeak77.cs), EA bug fix, off
                case "dragoncurserun": return Instantiator.kDragonCurseRun;  // docs/608 §3 (WalkStyleLeak77.cs), EA bug fix, off
                case "vhomewalk": return Instantiator.kVirtualHomeWalk;  // docs/608 §4 (WalkStyleLeak77.cs), EA bug fix, off
                case "sarcmummynpc": return Instantiator.kSarcMummyNpc;  // docs/607 §3 (TreasureSpawn74.cs), EA bug fix, off
                case "pushwaitskip": return Instantiator.kPushWaitSkip;  // docs/604 §2 (PushWait604.cs), EA bug fix, off
                case "newcartest": return Instantiator.kNewCarTest;
                case "outerwearautoloop": return Instantiator.kOuterwearAutoLoop;
                case "outerwearrestore": return Instantiator.kOuterwearRestore;
                case "spoonslavechecks": return Instantiator.kSpoonSlaveChecks;
                case "queueproximity": return Instantiator.kQueueProximity;
                case "subroutefail": return Instantiator.kSubRouteFail;
                case "petwakebed": return Instantiator.kPetWakeBed;
                case "slavewakereason": return Instantiator.kSlaveWakeReason;
                case "routecauselog": return Instantiator.kRouteCauseLog;
                case "scarepriority": return Instantiator.kScarePriority;
                case "groupchatpriority": return Instantiator.kGroupChatPriority;
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
                case "trashpileimport": return Instantiator.kTrashPileImport;
                case "catgymwatch": return Instantiator.kCatGymWatch;
                case "badsecretkey": return Instantiator.kBadSecretKey;
                case "traitlistenerremove": return Instantiator.kTraitListenerRemove;
                case "weddinglistener": return Instantiator.kWeddingListener;
                case "portasknohome": return Instantiator.kPortAskNoHome;
                case "lecturebroadcaster": return Instantiator.kLectureBroadcaster;
                case "residentspawnwalk": return Instantiator.kResidentSpawnWalk;
                case "midlifewishsave": return Instantiator.kMidlifeWishSave;
                case "innerbeautyonce": return Instantiator.kInnerBeautyOnce;
                case "betrayednullsim": return Instantiator.kBetrayedNullSim;
                case "topictestnull": return Instantiator.kTopicTestNull;
                case "opptrackerrelisten": return Instantiator.kOppTrackerRelisten;
                case "traitremovenull": return Instantiator.kTraitRemoveNull;
                case "homeworkcopyold": return Instantiator.kHomeworkCopyOld;
                case "laundrymoodeach": return Instantiator.kLaundryMoodEach;
                case "partycleanupnull": return Instantiator.kPartyCleanupNull;
                case "safelocunhide": return Instantiator.kSafeLocUnhide;
                case "midlifeuiending": return Instantiator.kMidlifeUiEnding;
                case "nemesisnoromance": return Instantiator.kNemesisNoRomance;
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
                case "displaycasereset": return Instantiator.kDisplayCaseReset;
                case "suatgradebonus": return Instantiator.kSuatGradeBonus;
                case "mermaidsharknull": return Instantiator.kMermaidSharkNull;
                case "jetpackflyherecleanup": return Instantiator.kJetpackFlyHereCleanup;
                case "naniteroutefail": return Instantiator.kNaniteRouteFail;
                case "springdanceclear": return Instantiator.kSpringDanceClear;
                case "nectarcopylevel10": return Instantiator.kNectarCopyLevel10;
                case "harvesteatvfx": return Instantiator.kHarvestEatVfx;
                case "buffetemptypick": return Instantiator.kBuffetEmptyPick;
                case "krakenfriendsailor": return Instantiator.kKrakenFriendSailor;
                case "mummycursetrait": return Instantiator.kMummyCurseTrait;
                case "bullridercleanup": return Instantiator.kBullRiderCleanup;
                case "broomtipperformer": return Instantiator.kBroomTipPerformer;
                case "balancingballcareer": return Instantiator.kBalancingBallCareer;
                case "elvisgnomeguard": return Instantiator.kElvisGnomeGuard;
                case "elvisgnomecap": return Instantiator.kElvisGnomeCap;
                case "cattoypickerrel": return Instantiator.kCatToyPickerRel;
                case "gourmetpetrecipe": return Instantiator.kGourmetPetRecipe;
                case "chipfamilycrystal": return Instantiator.kChipFamilyCrystal;
                case "chipcomponentorphan": return Instantiator.kChipComponentOrphan;
                case "chipanycount": return Instantiator.kChipAnyCount;
                case "nanitemerge": return Instantiator.kNaniteMerge;
                case "consignmentlastday": return Instantiator.kConsignmentLastDay;
                case "bartendinghourlypay": return Instantiator.kBartendingHourlyPay;
                case "influencefollowerbonus": return Instantiator.kInfluenceFollowerBonus;
                case "broomarenachoice": return Instantiator.kBroomArenaChoice;
                case "fortunetellerscamodds": return Instantiator.kFortuneTellerScamOdds;
                case "consignmentskillcurve": return Instantiator.kConsignmentSkillCurve;
                case "simbotworld": return Instantiator.kSimbotWorld;
                case "consignmentstockleak": return Instantiator.kConsignmentStockLeak;
                case "herbqualityfloor": return Instantiator.kHerbQualityFloor;
                case "snackbowlslobvfx": return Instantiator.kSnackBowlSlobVfx;
                case "worldpondskatetest": return Instantiator.kWorldPondSkateTest;
                case "pingpongopponent": return Instantiator.kPingPongOpponent;
                case "mermaiddehydratelook": return Instantiator.kMermaidDehydrateLook;
                case "meteormagnetorphan": return Instantiator.kMeteorMagnetOrphan;
                case "daycarelatealarm": return Instantiator.kDaycareLateAlarm;
                case "boxofmysterywoohoo": return Instantiator.kBoxOfMysteryWooHoo;
                case "bodysculptorweight": return Instantiator.kBodySculptorWeight;
                case "nectarconsignvalue": return Instantiator.kNectarConsignValue;
                case "straygnometns": return Instantiator.kStrayGnomeTns;
                case "dogswimslots": return Instantiator.kDogSwimSlots;
                case "fragmentreconstruct": return Instantiator.kFragmentReconstruct;
                case "labobjectpush": return Instantiator.kLabObjectPush;
                case "businessplannerrun": return Instantiator.kBusinessPlannerRun;
                case "futuredescendantsize0": return Instantiator.kFutureDescendantSize0;
                case "junglegymexitqueue": return Instantiator.kJungleGymExitQueue;
                case "wildhorselot": return Instantiator.kWildHorseLot;
                case "wildlotskip": return Instantiator.kWildLotSkip;        // off (WildLotSkip.cs, docs/656 §8.3)
                case "petstartleskip": return Instantiator.kPetStartleSkip;  // off (PetStartleSkip.cs, docs/656 §8.1)
                case "petlonghome": return Instantiator.kPetLongHome;        // off (PetLongHome.cs, docs/656 §8.2)
                case "petarrivepoint": return Instantiator.kPetArrivePoint;  // off (PetArrivePoint.cs, docs/656 §4.1)
                case "strayleave": return Instantiator.kStrayLeave;          // off (StrayLeave.cs, docs/656 §4.2)
                case "petlongvisit": return Instantiator.kPetLongVisit;      // off (PetLongHome.cs, docs/656 §4.3)
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
                case "safespotpool": return Instantiator.kSafeSpotPool;
                case "spotcheck": return Instantiator.kSpotCheck;
                case "houseboatlegmemo": return Instantiator.kHouseboatLegMemo;              // off (HouseboatLegMemo.cs, docs/853 §2)
                case "houseboatgangwayrescue": return Instantiator.kHouseboatGangwayRescue;  // off (HouseboatGangwayRescue.cs, docs/853 §3)
                case "boatroamcap": return Instantiator.kBoatRoamCap;                        // off (BoatRoamCap.cs, docs/853 §4)
                case "safespotdedupe": return Instantiator.kSafeSpotDedupe;
                case "woohoosnap": return Instantiator.kWooHooSnap;  // WooHooSnap.cs (docs/686 §20): a WooHoo partner (NPC pairs) whose route to the bed, shower or other object fails is put on its spot, off
                case "woohoosnaphh": return Instantiator.kWooHooSnapHousehold;  // WooHooSnap.cs: the same for pairs with a household or selected Sim, off
                case "woohoosnaplog": return Instantiator.kWooHooSnapLog;  // WooHooSnap.cs (docs/686 §20): one line per act, off
                case "pairsnap": return Instantiator.kPairSnap;  // WooHooSnap.cs (docs/686 §20): a paired social (kiss, hug) of two NPCs within 3 m whose placement or route fails is lined up by a snap, off
                case "pairsnaphh": return Instantiator.kPairSnapHousehold;  // WooHooSnap.cs: the same for pairs with a household or selected Sim, off
                case "pairsnaplog": return Instantiator.kPairSnapLog;  // WooHooSnap.cs (docs/686 §20): one line per act, off
                case "queuewake": return Instantiator.kQueueWake;  // QueueWake.cs (docs/686 §23): the first entry of an idle household or selected Sim wakes its loop at once, off
                case "queuewakelog": return Instantiator.kQueueWakeLog;  // QueueWake.cs (docs/686 §23): one line per wake, off
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
                case "boatroutenull": return Instantiator.kBoatRouteNull;
                case "unseenrelax": return Instantiator.kUnseenRelax;
                case "iafailmemo": return Instantiator.kIaFailMemo;
                case "boattrip": return Instantiator.kBoatTrip;
                case "boatplace": return Instantiator.kBoatPlace;
                case "retryscope": return Instantiator.kRetryScope;
                case "boatlandstuck": return Instantiator.kBoatLandStuck;
                case "outfitmissing": return Instantiator.kOutfitMissing;
                case "ageupdupinst": return Instantiator.kAgeUpDupInst;
                case "overwatchresetgone": return Instantiator.kOverwatchResetGone;
                case "motivesnohousehold": return Instantiator.kMotivesNoHousehold;
                case "frontstageowner": return Instantiator.kFrontStageOwner;
                case "careerskillnull": return Instantiator.kCareerSkillNull;
                case "stuckhbskip": return Instantiator.kStuckHbSkip;
                case "turnjigskip": return Instantiator.kTurnJigSkip;
                case "overwatchrelretry": return Instantiator.kOverwatchRelRetry;
                case "fightorphanwait": return Instantiator.kFightOrphanWait;
                case "poolnolane": return Instantiator.kPoolNoLane;
                case "autonomyfailcooldown": return Instantiator.kAutonomyFailCooldown;
                case "venuepicktime": return Instantiator.kVenuePickTime;
                case "venuepickfast": return Instantiator.kVenuePickFast;
                case "fireworkswatchlate": return Instantiator.kFireworksWatchLate;
                case "rolenullpurge": return Instantiator.kRoleNullPurge;
                case "errortraploop": return Instantiator.kErrorTrapLoop;
                case "fightstaycap": return Instantiator.kFightStayCap;
                case "loopidlebridge": return Instantiator.kLoopIdleBridge;
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
                case "deadrefcadence": return Instantiator.kDeadRefCadence;  // docs/330, ON (sub-switch of deadrefsweep: old cadence when off)
                case "deadrefexact": return Instantiator.kDeadRefExact;  // docs/334, OFF (LC-1 exact cuts of the cadence pass)
                case "deadrefexactverify": return false;  // docs/334, OFF, test only: partition lists compared with the filter passes
                case "viewerpushdelta": return Instantiator.kViewerPushDelta;  // docs/330, ON (old every-wake push when off)
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
                case "swapinvokediag": return Instantiator.kSwapInvokeDiag;  // diagnostics only, off (SwapInvokeDiag.cs, docs/312)
                case "blanktns": return Instantiator.kBlankTns;  // diagnostics only, off (on in betas): a blank notice names its sender (BlankTns.cs, docs/869)
                case "boatgetoutdiag": return Instantiator.kBoatGetOutDiag;  // diagnostics only, off (BoatGetOutDiag.cs, docs/257 §7)
                case "boatgetoutguard": return Instantiator.kBoatGetOutGuard;  // EA bug fix, off until the boat-getout-stress queue (BoatGetOutDiag.cs, docs/257 §9)
                case "unseenoutfitnospin": return Instantiator.kUnseenOutfitNoSpin;
                case "unseenoutfitdefer": return Instantiator.kUnseenOutfitDefer;  // issue #451, off (UnseenOutfitDefer.cs, docs/586)
                case "boatparkgone": return Instantiator.kBoatParkGone;  // EA bug fix, on by default (BoatParkGone.cs, docs/257)
                case "deadrefcooking": return Instantiator.kDeadRefCooking;  // EA bug fix, off (DeadRefs.cs phase 14, docs/205 §4)
                case "deadrefconvo": return Instantiator.kDeadRefConvo;  // EA bug fix, off (DeadRefs.cs phase 15, docs/517 §4)
                case "takeariskguard": return Instantiator.kTakeARiskGuard;  // EA bug fix, off (TakeARiskGuard.cs, docs/529 §4)
                case "minihomelot": return Instantiator.kMiniHomeLot;  // EA bug fix, on (MiniHomeLot.cs, docs/266)
                case "dancefloorlistguard": return Instantiator.kDanceFloorListGuard;  // EA bug fix, ON in release (Rodney 2026-11-09; DanceFloorDiag.cs, docs/253 §12)
                case "compactionfail": return false;  // test only (Compaction.cs, docs/204 §6): the reload throws after the screen is up
                case "spotspread": return true;
                case "routefaillog": return true;  // ON in release (Rodney 2026-11-08): helps triage player reports in early releases
                case "asyncroute": case "asyncreplan": case "asyncreplanforeign": case "overwatchstuck": case "savedstacks": case "swapdelegates": case "gm6trace": case "savecleanup": return true;  // were on through the old default: true
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

        // docs/293: the callee bodies the game's IL transform inlines at a call site instead of emitting a call (GenerateCode
        // 0xE5DE08-0xE5E3A9 [verified, capstone]; tested on the body alone: no exception clause and no local unless it
        // starts with ret). 1 = starts with ret (the arguments are popped), 2 = ldsfld; ret, 3 = ldarg.0; ldfld; ret,
        // 4 = ldarg.0; ldarg.1; stfld; ret, 5 = volatile. ldsfld; ret, 6 = ldsfld; ldfld; ret; 0 = a call.
        internal static int InlineShape(byte[] il, bool localsOrClauses, int paramCount)
        {
            if (il == null || il.Length == 0) return 0;
            if (il[0] == 0x2A) return 1;
            if (localsOrClauses) return 0;
            int n = il.Length;
            if (n == 6 && paramCount == 0 && il[0] == 0x7E && il[5] == 0x2A) return 2;
            if (n == 7 && paramCount == 0 && il[0] == 2 && il[1] == 0x7B && il[6] == 0x2A) return 3;
            if (n == 8 && paramCount == 1 && il[0] == 2 && il[1] == 3 && il[2] == 0x7D && il[7] == 0x2A) return 4;
            if (n == 8 && paramCount == 0 && il[0] == 0xFE && il[1] == 0x13 && il[2] == 0x7E && il[7] == 0x2A) return 5;
            if (n == 11 && paramCount == 0 && il[0] == 0x7E && il[5] == 0x7B && il[10] == 0x2A) return 6;
            return 0;
        }

        // Pure: does replacing a body of this shape by one the transform calls (or the reverse) change the length of the
        // callers' code? Not for an instance field getter or setter of a non-struct type: ldfld / stfld take 2 words, as a
        // call does (struct fields take 4). Every other inlined shape differs from the 2-word call.
        internal static bool InlineShapeMovesLayout(int shape, bool structField)
        {
            return shape != 0 && ((shape != 3 && shape != 4) || structField);
        }

        static bool IsStructType(Type t) { return t != null && t.IsValueType && !t.IsPrimitive && !t.IsEnum; }

        // 0 = swapping this method's body leaves its callers' code layout alone, else its inline shape. A saved task frame
        // holds a raw word index into its method's code (ScriptCore.TaskFrame.mIPOffset, guarded only by an IL checksum), so
        // a swap that turns an inlined call site into a call, or back, makes saves resume inside an instruction: in the
        // game without this mod, in a build with another set of hooks, and a vanilla save here (the McCall+0x42 crash of
        // 2026-11-19: Lot.get_ChanceOfGoingToCommunityLotByCar, one word in MetaRabbitHoleInteraction.Run).
        static int SwapMovesLayout(MethodInfo m)
        {
            try
            {
                MethodBody b = m.GetMethodBody();
                if (b == null) return 0;
                ParameterInfo[] ps = m.GetParameters();
                int shape = InlineShape(b.GetILAsByteArray(), b.LocalVariables.Count != 0 || b.ExceptionHandlingClauses.Count != 0, ps.Length);
                bool structField = shape == 3 ? IsStructType(m.ReturnType) : shape == 4 && IsStructType(ps[0].ParameterType);
                return InlineShapeMovesLayout(shape, structField) ? shape : 0;
            }
            catch (Exception) { return 0; }  // no readable body: nothing the transform could inline
        }

        // Why m has no IL body to swap (abstract, pinvoke, icall / runtime / native implementation), null when it has one.
        internal static string NoBody(MethodInfo m)
        {
            if (m == null) return null;
            if (m.IsAbstract) return m.Name + " is abstract";
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return m.Name + " is pinvoke";
            if ((m.GetMethodImplementationFlags() & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime | MethodImplAttributes.Native)) != 0) return m.Name + " has no IL (icall/runtime)";
            return null;
        }

        // Swaps that went in, per hook class. A method swapped by two hooks (HitchCause's and a fix's on Lot.UpdateDoorPrivacy,
        // PetPoolOrphan's and petpoolghost's on PetPool.RandomRemovePet) chains: the later hook's stub receives the earlier
        // hook's body. sPatched maps the method to the later hook only, so the live-path asserts count here (s58).
        static readonly Dictionary<Type, int> sSwapsByHook = new Dictionary<Type, int>();
        static readonly Dictionary<IntPtr, List<Type>> sHooksOn = new Dictionary<IntPtr, List<Type>>();  // every hook swapped onto a method (PATCHED TWICE guard)

        static bool Patch(Type target, string method, Type[] args, Type hook, string replacement, string stub)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                MethodInfo t = args == null ? target.GetMethod(method, all) : target.GetMethod(method, all, null, args, null), r = hook.GetMethod(replacement, all);
                if (Compiled(t)) throw new InvalidOperationException("already compiled");
                // the same hook on the same method twice (an Apply call listed twice, 2026-11-18): the stub would then hold the hook's
                // body and call itself (StackOverflowException on the first call; Gameflow.SetGameSpeed killed every world load)
                // also A,B,A: sPatched keeps only the last hook, so every hook ever swapped onto the method is checked
                List<Type> onIt;
                if (!sHooksOn.TryGetValue(t.MethodHandle.Value, out onIt)) sHooksOn[t.MethodHandle.Value] = onIt = new List<Type>();
                if (onIt.Contains(hook)) throw new InvalidOperationException("PATCHED TWICE by " + hook.Name + ", second swap refused");
                // a swapped body keeps the hook's signature: a void hook for a method that returns a value makes every
                // caller's pop/use of the result underflow Mono's transform (transform.c:1170 `td.sp >= td.stack`, int3 at
                // 0x58D9B6: the game dies on the first call, buycellrecycle v1/v2 2026-11-11), so refuse such pairs
                PatchSigCheck(t, r, "hook");
                // docs/293: never move the callers' saved frame offsets (counter [1263] = swaps refused for it)
                int moved = SwapMovesLayout(t);
                if (moved != 0) { PatchCounters()[1263]++; throw new InvalidOperationException("REFUSED: the game inlines this method at its call sites (shape " + moved + "), a hook would move saved task frames (docs/293)"); }
                moved = SwapMovesLayout(r);
                if (moved != 0) { PatchCounters()[1263]++; throw new InvalidOperationException("REFUSED: the hook body " + r.Name + " would be inlined at the call sites (shape " + moved + "), that would move saved task frames (docs/293)"); }
                if (stub != null) PatchSigCheck(t, hook.GetMethod(stub, all), "stub");
                // a body-less method (abstract, pinvoke, icall) on either side of a swap: EA's transform reads its NULL IL header
                // and the game dies at 0x00E6463F (docs/51 I-530-3); refuse the pair
                string nb = NoBody(t) ?? NoBody(r) ?? (stub != null ? NoBody(hook.GetMethod(stub, all)) : null);
                if (nb != null) throw new InvalidOperationException(nb + ": not patched");
                Type sdOwner = t.DeclaringType;  // docs/576 §1: the class the save must name (SwapMethod moves the class word)
                string swName = AutShadowCore.MemberName(t);  // G-M6: callers' IL hashes name t as before the swap
                if (swName != null) AutShadowCore.SwapNames[t.MethodHandle.Value] = swName;
                if (stub != null) { MethodInfo s = hook.GetMethod(stub, all); SwapMethod(s, t); StubDirect(s); }
                SwapMethod(t, r);
                sStubs.Add(t);  // docs/242: a frame in the hook's body saves as the hook's class, which often cannot find it again
                int nSw; sSwapsByHook.TryGetValue(hook, out nSw); sSwapsByHook[hook] = nSw + 1;  // per hook: sPatched keeps only the last hook of a stacked swap (s58)
                onIt.Add(hook);
                sPatched[t.MethodHandle.Value] = hook;  // docs/128 G-M4: our own swap, accepted by the closure sweep (by MonoMethod: GetMethods hands out other MethodInfo objects)
                AppliedLog().Add(target.Name + "." + method + ": ok");
                SdRecord(hook, sdOwner, t, r, stub != null ? hook.GetMethod(stub, all) : null);  // docs/576 §1 swapdelegates
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
                PlhBefore(Actor, Target, SimFollowers, "gohome");  // docs/656 §8.2 petlonghome (PetLongHome.cs): pets only, off by default
                // docs/560 gohomewalk: EA requests WalkStyle on the Sim and never unrequests it
                int ghBefore = -1;
                if (WalkStyle != Sims3.Gameplay.Actors.Sim.WalkStyle.AutoSelect && ShortcutOn("gohomewalk")) ghBefore = GhWalkCount(Actor, WalkStyle);
                try
                {
                    if (sCounters == null) sCounters = PatchCounters();
                    sCounters[0]++;
                    sGhDestSeen = false;
                    if (!ShortcutOn("gohome") || !Unseen(Actor, Target))
                    {
                        if (sGhDestSeen) Stage();
                        return HgrRun(RunOrig());
                    }
                    // a Sim shortcut twice within 10 sim-minutes is in some loop the game drives: let it walk
                    ulong id = Actor.SimDescription.SimDescriptionId;
                    long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                    long last;
                    if (sLastShortcut.TryGetValue(id, out last) && now - last < kLoopTicks)
                    {
                        sCounters[3]++;
                        return HgrRun(RunOrig());
                    }
                    Sims3.SimIFace.Vector3 pos, fwd;
                    if (sGhShell) return ShellArrive(id, now);
                    if (!SafeSpot(Actor, Target, Target.GetRandomPosition(false, true), out pos, out fwd, "gohome", true))
                    {
                        sCounters[2]++;
                        return HgrRun(RunOrig());
                    }
                    // spotcheck (default ON): Unseen(Actor, Target) judged the home lot, but the spot is a random outdoor point
                    // anywhere on it (up to ~45 m from the centre on a 64 m lot): one in view = the Sim walks.
                    // (A merged copy of this check counted into 2713, which docs/registry.md never gave spotcheck; 2702 is its slot.)
                    if (ShortcutOn("spotcheck") && !UfGoalUnseen(pos)) { sCounters[2702]++; return HgrRun(RunOrig()); }
                    // docs/202 §3.6.4 unseenfbtriphold: D's walk-time hold first; given up = the game's own trip
                    if (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) == 2 && !UfTripHold(Actor, Target, pos)) return HgrRun(RunOrig());
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
            // houseboatgangwayrescue (HouseboatGangwayRescue.cs): EA's GoHome.Run gave up; a Sim marked by a no-path gangway leg
            // is placed on its houseboat here, in its own task after Run, never inside the routing callback
            bool HgrRun(bool ok)
            {
                if (!ok && sHgr.Count > 0 && ShortcutOn("houseboatgangwayrescue")) HgrAfterGoHome(Actor, Target, this);
                return ok;
            }

            // stagedarrival: home in view or near the household (Unseen's last test): the Sim leaves its lot as GoHome.Run's lead-in
            // does, is placed at the nearest unseen street point, then GoHome.Run walks it in. Apartment shells keep EA's walk.
            static bool sGhDestSeen;  // read right after Unseen returns (no yield in between)
            void Stage()
            {
                if (!ShortcutOn("stagedarrival")) return;
                Sims3.Gameplay.Actors.Sim a = Actor;
                StageNear(a, Target.EntryPoint(), Target, kStageGoHome, UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")), delegate
                {
                    Sims3.Gameplay.Situations.VisitSituation.OnSimLeavingLot(a);
                    Sims3.Gameplay.Situations.GroupingSituation.RemoveSimFromGroupSituation(a);
                });
            }

            // gohomeshell (default off, townies only): an apartment home (a lot with a building shell) is always left to the walk: a
            // random spot on the lot can be inside the closed building. The shortcut ends the trip the way the house shortcut does, with
            // a safe spot inside the apartment's own rooms (Lot.GetRandomPosition(insideValid: true, outsideValid: false), found on that
            // point's floor and on this lot), out of view; then GoHome succeeds without a route: no door, no elevator. A home near the
            // camera or the household (UnseenBody refuses it), no room point, no such spot or a spot in view = EA's walk. (A first
            // version placed the Sim in front of the shell's door and let EA's Run walk it in: about 420 townies a game then went
            // through the shell door and up the tower, and Bridgeport apartment games crashed in EA's slot attach on freed physics;
            // that is what the native LotLevelPastEnd fix of PR #449 clamps, so this switch should be tested with it.)
            // Counters: [1294] shell homes asked, [1295] placed inside, [1296] walked, split [1297-1300].
            static bool sGhShell;
            bool ShellArrive(ulong id, long now)
            {
                Sims3.SimIFace.Vector3 pos = Sims3.SimIFace.Vector3.Invalid, fwd = Sims3.SimIFace.Vector3.Invalid;
                Sims3.SimIFace.Vector3 room = Target.GetRandomPosition(true, false);
                int walk = room == Sims3.SimIFace.Vector3.Invalid ? 1297 : !SafeSpot(Actor, Target, room, out pos, out fwd, "homeshell", false) ? 1298
                    : Math.Abs(pos.y - room.y) > 1f || LotManager.GetLotAtPoint(pos) != Target ? 1299 : !UfGoalUnseen(pos) ? 1300 : 0;
                if (walk != 0)
                {
                    sCounters[1296]++;
                    sCounters[walk]++;
                    return RunOrig();
                }
                sLastShortcut[id] = now;
                ShortcutLog(Actor.FullName + " -> inside " + Target.Name + " from " + (Actor.LotCurrent != null ? Actor.LotCurrent.Name : "street"));
                // same lead-in as GoHome.Run
                if (!Sims3.Gameplay.Objects.Vehicles.CarpoolManager.WaitForCarpool(Actor)) return false;
                Sims3.Gameplay.Situations.VisitSituation.OnSimLeavingLot(Actor);
                Sims3.Gameplay.Situations.GroupingSituation.RemoveSimFromGroupSituation(Actor);
                PlaceSim(Actor, pos, fwd, "homeshell " + Target.Name);
                sCounters[1295]++;
                NumSuccess++;
                BeginCommodityUpdates();
                EndCommodityUpdates(true);
                return true;
            }
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
                sGhShell = false;
                if (home == null || !TownieUnseen(sim, SimFollowers)) return false;
                // GoHome.Run may add the household horse as a follower after this point; keep pet households on foot
                if (sim.Household != null && sim.Household.Pets.Count > 0) return false;
                if (sim.LotHome != home || sim.LotCurrent == home || home == Household.ActiveHouseholdLot) return false;
                if (!HouseboatOk(home)) return false;
                if (home.BuildableShells.Length > 0)
                {
                    if (!ShortcutOn("gohomeshell")) return false;
                    sCounters[1294]++;
                    sGhShell = true;  // ShellArrive, read right after Unseen returns (no yield in between)
                }
                if (FarFromViewers(home.Position)) return true;
                sGhDestSeen = !sGhShell;  // stagedarrival: apartment shells keep EA's walk
                return false;
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
        // who (docs/616): the caller, for the ufplan "src spot" tag; asyncOk: the caller gated on TownieUnseen + FarFromViewers(near)
        // itself, so spotasync may send the fresh check as a job and re-apply that gate after the wait (SpotPlaceAfter).
        static bool SafeSpot(Sims3.Gameplay.Actors.Sim sim, Lot lot, Sims3.SimIFace.Vector3 near, out Sims3.SimIFace.Vector3 pos, out Sims3.SimIFace.Vector3 fwd,
            string who, bool asyncOk)
        {
            ulong task = Simulator.CurrentTask.Value;
            SpotCtx was;
            bool had = sSpotCtx.TryGetValue(task, out was);
            if (sSpotCtx.Count >= 64) sSpotCtx.Clear();  // bounded: a task killed mid-check never removes itself
            sSpotCtx[task] = new SpotCtx { Who = who + "@" + lot.Name, AsyncOk = asyncOk };
            try { return SafeSpotBody(sim, lot, near, out pos, out fwd); }
            finally { if (had) sSpotCtx[task] = was; else sSpotCtx.Remove(task); }
        }
        static bool SafeSpotBody(Sims3.Gameplay.Actors.Sim sim, Lot lot, Sims3.SimIFace.Vector3 near, out Sims3.SimIFace.Vector3 pos, out Sims3.SimIFace.Vector3 fwd)
        {
            SpotVerdicts v = Verdicts(lot);
            RouteProfile prof = ProfileOf(sim);
            // "spotspread" off = the earlier build: always start from the lot's last verified spot (A/B for the Bridgeport slowdown)
            bool spread = ShortcutOn("spotspread");
            bool reuse = ShortcutOn("safespotreuse");
            int[] c = PatchCounters();
            // safespotpool (default ON): spots in a pool or under the ground outdoors are refused, so the caller runs EA's trip
            bool noPool = ShortcutOn("safespotpool");
            // safespotdedupe (ON 2026-11-20; docs/808): the three fresh tries all search from the same `near`, and with one verified
            // spot (or spotspread off) both verified tries start from the same spot, so a deterministic search repeats an answer this
            // call already turned down. Counted (always, counting only): fresh retry found [1476] the same spot as the try before,
            // [1477] another spot, [1478] none; [1479] a spot after none. With the switch on, such a repeat stops the fresh tries
            // [1480], and the second verified try is skipped when it would start from the same spot [1481].
            bool dedupe = ShortcutOn("safespotdedupe");
            Sims3.SimIFace.Vector3 lastFresh = Sims3.SimIFace.Vector3.Invalid;
            int freshTries = 0;
            bool lastFound = false;
            for (int i = 0; i < 2 + 3; i++)
            {
                bool fresh = i >= 2;
                if (!fresh && v.Good.Count == 0) continue;
                if (dedupe && i == 1 && (v.Good.Count == 1 || !spread)) { c[1481]++; continue; }
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
                if (fresh)
                {
                    bool same = found && lastFound && (pos - lastFresh).Length() < 0.001f;
                    if (freshTries > 0) c[!found ? 1478 : !lastFound ? 1479 : same ? 1476 : 1477]++;
                    freshTries++;
                    lastFound = found;
                    if (found) lastFresh = pos;
                    if (same && dedupe) { c[1480]++; break; }
                }
                if (ShortcutOn("safespotyield") && !SafeSpotYield(sim, near)) break;
                if (!found) continue;
                if (noPool && BelowGroundOutdoors(pos)) { c[2700]++; continue; }
                if (verify && (pos - start).Length() > 0.5f) c[92]++;
                int plans0 = c[39];
                long tk = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                bool good = LotManager.GetLotAtPoint(pos) == lot && SpotVerdict(sim, lot, v, prof, pos, fresh, false, true) == true;
                // safespotplanyield: the verdict's IsPointInLotSafelyRoutable plan and the next FindGoodLocation (or the caller's
                // placement) no longer share one sim slice; after the yield the Sim must still be there and unseen
                if (c[39] != plans0 && ShortcutOn("safespotplanyield") && !SafeSpotYield(sim, near, 4088, 4089, -1)) break;
                if (!good) continue;
                // spotasync (docs/616): the check may have slept on its job; place only if the caller's unseen gate still holds
                bool yielded = Sims3.Gameplay.Utilities.SimClock.CurrentTicks != tk && ShortcutOn("spotasync");  // other yields keep today's rule
                if (!H4Core.SpotPlaceAfter(yielded, !sim.HasBeenDestroyed && sim.InWorld, yielded && TownieUnseen(sim, null), yielded && FarFromViewers(near)))
                { c[3488]++; break; }
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
        // counters: yields / given up after the yield / not yielded (-1 = not counted): 276-278 safespotyield, 4088-4089 safespotplanyield
        static bool SafeSpotYield(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 near) { return SafeSpotYield(sim, near, 276, 277, 278); }
        static bool SafeSpotYield(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 near, int yields, int givenUp, int notYielded)
        {
            int[] c = PatchCounters();
            if (Simulator.CurrentTask != sim.ObjectId || !MayYield()) { if (notYielded >= 0) c[notYielded]++; return true; }
            c[yields]++;
            Simulator.Sleep(0);
            if (sim.HasBeenDestroyed || !TownieUnseen(sim, null) || !FarFromViewers(near)) { c[givenUp]++; return false; }
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

        // ---- docs/616 spotasync (default ON) and the spot caller tag ----------------------------------------------------------
        // The only game hitch of three Bridgeport sessions (314-370 ms, Sun 12:46 PM, tick ~28760) was one SpotVerdict full check:
        // Sim.IsPointInLotSafelyRoutable's PlanFromPoint, ~280 ms sync on the sim thread ("START refused": [RouteAsync] Kinds=0).
        // With the switch on, a SafeSpot full check of an unseen NPC in its own task that may yield, when ts3native would start it
        // (WdA1Would: q 9 = WatchdogKind2 or Kinds bit 2), sends its PlanFromPoint STARTs with the watchdog's tag (RouteBackoff.cs
        // AsyncKindStart): ts3native starts them as own jobs with no KindDeadlines cut (plandeadline.cpp: a tagged job is
        // "uncut: watchdog", KdUncutCeilMs only), so the verdict is the planner's own, one or more ticks later; it is cached as
        // today. SafeSpot places the Sim after a wait only when the caller's unseen gate still holds (H4Core.SpotPlaceAfter).
        // Shadow arm: watchdogasyncshadow also re-checks each async spot verdict sync (counter 3489, mismatch lines).
        // Counters: [3485] full checks answered by a job (sync plans avoided), [3486] their wall ms (the sync plan's cost moved
        // off the sim thread [inferred: job time ~ sync time]), [3487] eligible checks that still planned sync (START refused),
        // [3488] placements dropped after the wait (Sim seen or gone: the game's own trip runs), [3489] shadow mismatches.
        struct SpotCtx { public string Who; public bool AsyncOk; }
        static readonly Dictionary<ulong, SpotCtx> sSpotCtx = new Dictionary<ulong, SpotCtx>();  // by script task: SafeSpot's caller
        static readonly Dictionary<ulong, int> sSpotAsyncTasks = new Dictionary<ulong, int>();  // tasks whose check may go async -> jobs started
        static string SpotWhoHere()
        {
            SpotCtx sc;
            return sSpotCtx.Count > 0 && sSpotCtx.TryGetValue(Simulator.CurrentTask.Value, out sc) ? sc.Who : null;
        }
        // RouteBackoffHook.AsyncKindStart: this PlanFromPoint belongs to a spotasync check of this task
        static bool SpotAsyncHere()
        {
            return sSpotAsyncTasks.Count > 0 && sInRoutableCheck && sSpotAsyncTasks.ContainsKey(Simulator.CurrentTask.Value);
        }
        static void SpotAsyncStarted(bool started)
        {
            ulong task = Simulator.CurrentTask.Value;
            int n;
            if (started && sSpotAsyncTasks.TryGetValue(task, out n)) sSpotAsyncTasks[task] = n + 1;
        }
        static bool SpotAsyncWould(Sims3.Gameplay.Actors.Sim sim)
        {
            if (!ShortcutOn("spotasync")) return false;
            bool ownTask = Simulator.CurrentTask == sim.ObjectId;
            bool household = IsPlayerSim(sim) || sim.SimDescription == null || UfWhy(sim) != 0;  // household rule: unknown scope counts as household
            return H4Core.SpotAsync(true, true, ownTask, ownTask && MayYield(), ownTask && StuckWatchdog.WdA1Would(), household, sInWdCheck);
        }
        static void SpotAsyncDone(Sims3.Gameplay.Actors.Sim sim, Lot lot, Sims3.SimIFace.Vector3 pos, bool ok, int starts, int ms)
        {
            int[] c = PatchCounters();
            if (starts == 0) { c[3487]++; return; }
            c[3485]++;
            if (ms > 0) c[3486] += ms;
            if (!ShortcutOn("watchdogasyncshadow")) return;
            bool sync;
            sInOwnCheck = true;
            try { sync = sim.IsPointInLotSafelyRoutable(lot, pos); } finally { sInOwnCheck = false; }
            if (sync == ok) return;
            c[3489]++;
            DiagLine("spotasync shadow mismatch: " + sim.FullName + " async " + ok + " sync " + sync + " @" + Math.Round(pos.x, 2) + "," + Math.Round(pos.z, 2) + " on " + lot.Name);
        }

        // ---- docs/635 spotbusydefer (default ON) ------------------------------------------------------------------------------
        // The recurring Sun 12:47 PM hitch (s84 phase-20261117-1604, 305 ms; s86 0857 / 0127 / 0923 the same tick ~28760) with
        // spotasync on: Armstrong's spot:visit check started a tagged kind-2 job (route async: long job 286 ms, own START, watchdog
        // check, uncut); ~10 ms later Bianca's spot:rabbithole check sent its own tagged START, ts3native refused it (the worker
        // holds world+0x918 for that job: AsyncStart's M1 lock test) and RouteBackoff planned it sync ("sync 284 ms (START
        // refused)"), so the sim thread waited on the lock for the rest of the other job. With the switch on such a refused
        // spotasync START plans nothing: the check answers no verdict (null, not cached), as SpotVerdict already does past its
        // per-tick budget, and the caller keeps EA's own trip. Only spotasync's own unseen-NPC checks (H4Core.SpotAsync: never
        // household, selected or unknown scope). Counters: [3800] checks deferred (sync lock waits avoided), [3801] refused tagged
        // STARTs seen, [3802] check mismatches.
        static readonly Dictionary<ulong, bool> sSpotDeferred = new Dictionary<ulong, bool>();  // task -> this check's START was refused
        static void SpotStartRefused()
        {
            PatchCounters()[3801]++;
            if (!ShortcutOn("spotbusydefer")) return;
            if (sSpotDeferred.Count >= 64) sSpotDeferred.Clear();  // bounded: a task killed mid-check never takes its entry
            sSpotDeferred[Simulator.CurrentTask.Value] = true;
        }
        // RouteBackoffHook.PlanFromPointAsync: this task's spot check had its START refused just now
        static bool SpotBusyDeferNow()
        {
            return sSpotDeferred.Count > 0 && SpotAsyncHere() && sSpotDeferred.ContainsKey(Simulator.CurrentTask.Value);
        }
        static bool SpotDeferTake(ulong task)
        {
            return sSpotDeferred.Count > 0 && sSpotDeferred.Remove(task);
        }
        // ---- docs/635 spotcut (default ON) -------------------------------------------------------------------------------------
        // s82 (phase-20261117-1648, r93, spotbusydefer + spotasync on): the Sun 12:47 PM hitch stayed (326 ms, tick 28768) in a
        // new shape: no refused START, but Bart Hammer's spot:visit check of Bridgeport Eternal Rest Cemetery ran as a tagged job
        // of 275 ms ("watchdog check, uncut: watchdog (KdUncutCeilMs), scope unseen"); the hitch frame spans that job and every
        // task in it slept one poll of 341-356 ms. plandeadline.cpp exempts every watchdog-tagged job from KindDeadlines
        // (UCW_WATCHDOG), so only the 30 s ceiling bounds it while the worker holds the routing lock. With the switch on, a
        // spotasync check (unseen NPC only: H4Core.SpotAsync) also sends the [UnseenFallback] MARK before its START; ts3native
        // then caps the job at the unseen-fallback deadline (ufUs, 220 ms less the learned overrun) like any unseen Sim's plan,
        // and a cut check answers no verdict (null, not cached), as spotbusydefer does: the caller keeps EA's own trip.
        // Counters: [3803] spot checks marked, [3804] cut (no verdict), [3805] check mismatches.
        static readonly Dictionary<ulong, bool> sSpotCut = new Dictionary<ulong, bool>();  // task -> this check's job was cut
        static bool SpotCutMark()
        {
            return SpotAsyncHere() && ShortcutOn("spotcut");
        }
        static void SpotCutNote()
        {
            PatchCounters()[3804]++;
            if (sSpotCut.Count >= 64) sSpotCut.Clear();
            sSpotCut[Simulator.CurrentTask.Value] = true;
        }
        static bool SpotCutTake(ulong task)
        {
            return sSpotCut.Count > 0 && sSpotCut.Remove(task);
        }
        // Pure: 0 = the job runs uncut and its verdict is cached (today), 1 = marked: cut at the unseen deadline, a cut = no verdict
        internal static int SpotCutDecision(bool on, bool spotAsyncCheck)
        {
            return on && spotAsyncCheck ? 1 : 0;
        }
        // Pure: what SpotVerdict does with the answer: 0 cache it, 1 no verdict
        internal static int SpotCutVerdict(bool marked, bool cut)
        {
            return marked && cut ? 1 : 0;
        }
        public static object SpotCutCheck(string what)
        {
            string counters = B582Counters("spotcut", 3803, new[] { "spot checks marked", "cut (no verdict)", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run") return "spotcut <run|result>";
            bool on = ShortcutOn("spotcut");
            bool eaBug = SpotCutDecision(false, true) == 0;   // off: the tagged job runs uncut (the hitch)
            bool fixedPure = SpotCutDecision(on, true) == 1 && SpotCutVerdict(true, true) == 1;
            bool controls = eaBug && SpotCutDecision(true, false) == 0     // not a spotasync check (household, A1, other plans): unchanged
                && SpotCutVerdict(true, false) == 0                          // marked, not cut: the verdict is cached as today
                && SpotCutVerdict(false, true) == 0
                && !H4Core.SpotAsync(true, true, true, true, true, true, false);  // household never takes the spot async path
            bool liveOk = sSpotCut.Count < 64;
            if (!liveOk) PatchCounters()[3805]++;
            return B582Line("spotcut", on, "a spotasync check's job is capped at the unseen-fallback deadline and a cut answers no verdict", fixedPure,
                "today the tagged job is uncut (" + eaBug + "), other plans unchanged, a finished job's verdict cached as today, household never async; needs spotasync ("
                + ShortcutOn("spotasync") + ") and ts3native [UnseenFallback]; pending cuts " + sSpotCut.Count, controls && liveOk, "", counters);
        }

        // Pure: 0 = sync plan (EA / today), 1 = defer (no verdict now)
        internal static int SpotBusyDecision(bool on, bool spotAsyncCheck, bool startRefused)
        {
            return on && spotAsyncCheck && startRefused ? 1 : 0;
        }
        public static object SpotBusyDeferCheck(string what)
        {
            string counters = B582Counters("spotbusydefer", 3800, new[] { "checks deferred (sync lock waits avoided)", "refused tagged STARTs", "check mismatches" }, "");
            if (what == "result") return counters;
            if (what != "run") return "spotbusydefer <run|result>";
            bool on = ShortcutOn("spotbusydefer");
            bool eaBug = SpotBusyDecision(false, true, true) == 0;   // off: the refused START plans sync (the hitch)
            bool fixedPure = SpotBusyDecision(on, true, true) == 1;
            bool controls = eaBug && SpotBusyDecision(true, false, true) == 0   // not a spotasync check (household, other plans): sync as EA
                && SpotBusyDecision(true, true, false) == 0                       // START accepted: the job answers
                && H4Core.SpotAsync(true, true, true, true, true, false, false) && !H4Core.SpotAsync(true, true, true, true, true, true, false);  // household never async
            bool liveOk = sSpotDeferred.Count < 64;
            if (!liveOk) PatchCounters()[3802]++;
            return B582Line("spotbusydefer", on, "a spotasync check whose START was refused answers no verdict instead of a sync plan behind the worker's lock", fixedPure,
                "EA plans sync (" + eaBug + "), other plans and accepted STARTs unchanged, household never takes the spot async path; needs spotasync (" + ShortcutOn("spotasync")
                + "); pending deferrals " + sSpotDeferred.Count, controls && liveOk, "", counters);
        }

        // SpotAsyncCheck run: the hitch case (a SafeSpot full check of an unseen NPC in its own yielding task, ts3native would
        // start it) leaves the sim thread only with the switch on; controls: household / not own task / cannot yield / ts3native
        // refuses / inside the watchdog all stay sync, and a placement after a wait needs the Sim still there and unseen. Live
        // part: ts3native's q 9 answer now, and one unseen NPC's spot verdict planned sync here (this call is not the Sim's task,
        // so it never goes async; the async verdicts are compared in play by the watchdogasyncshadow arm, counter 3489).
        public static object SpotAsyncCheck(string what)
        {
            string counters = B582Counters("spotasync", 3485, new[] { "checks answered by a job (sync plans avoided)", "their wall ms", "eligible but planned sync",
                "placements dropped after the wait", "shadow mismatches" }, "");
            if (what == "result") return counters;
            // test only (repro spotasync-trip-loop): drop every lot's verdicts so the next shortcut's SafeSpot check is a cold miss
            if (what == "clear") { int n = sSpotVerdicts.Count; sSpotVerdicts.Clear(); return "spotasync: cleared verdicts of " + n + " lots; " + counters; }
            if (what != "run") return "spotasync <run|result|clear>";
            bool on = ShortcutOn("spotasync");
            bool hitchAsync = H4Core.SpotAsync(on, true, true, true, true, false, false);
            bool eaSync = !H4Core.SpotAsync(false, true, true, true, true, false, false);
            bool controls = eaSync && !H4Core.SpotAsync(true, true, true, true, true, true, false) && !H4Core.SpotAsync(true, true, false, true, true, false, false)
                && !H4Core.SpotAsync(true, true, true, false, true, false, false) && !H4Core.SpotAsync(true, true, true, true, false, false, false)
                && !H4Core.SpotAsync(true, true, true, true, true, false, true) && !H4Core.SpotAsync(true, false, true, true, true, false, false)
                && H4Core.SpotPlaceAfter(false, true, true, true) && !H4Core.SpotPlaceAfter(true, true, false, true) && H4Core.SpotPlaceAfter(true, true, true, true);
            string live = "no unseen NPC on a lot";
            try
            {
                bool would = StuckWatchdog.WdA1Would();
                live = "ts3native would start a tagged PlanFromPoint now=" + would;
                foreach (Sims3.Gameplay.Actors.Sim s in Sims3.Gameplay.Queries.GetObjects<Sims3.Gameplay.Actors.Sim>())
                {
                    if (s == null || s.LotCurrent == null || s.LotCurrent.IsWorldLot || IsPlayerSim(s) || s.SimDescription == null || UfWhy(s) != 0) continue;
                    bool ok;
                    int t0 = System.Environment.TickCount;
                    sInOwnCheck = true;
                    try { ok = s.IsPointInLotSafelyRoutable(s.LotCurrent, s.Position); } finally { sInOwnCheck = false; }
                    live += ", sync verdict " + ok + " in " + unchecked(System.Environment.TickCount - t0) + " ms for an unseen NPC on " + s.LotCurrent.Name;
                    break;
                }
            }
            catch (Exception e) { live += ", error " + e.GetType().Name; }
            return B582Line("spotasync", on, "the hitch case's routability plan leaves the sim thread (uncut job, same verdict)", hitchAsync,
                "today sync (" + eaSync + "), household / other task / no yield / refused / watchdog stay sync, placement re-gated after a wait; " + live, controls, "", counters);
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
            // spotasync (docs/616): the same check as a watchdog-tagged own-task job, the Sim's task sleeping until READY
            ulong task = Simulator.CurrentTask.Value;
            SpotCtx sc;
            bool async = sSpotCtx.TryGetValue(task, out sc) && sc.AsyncOk && SpotAsyncWould(sim);
            int t0 = System.Environment.TickCount, starts = 0;
            if (async) { sSpotAsyncTasks[task] = 0; sSpotDeferred.Remove(task); sSpotCut.Remove(task); }
            sInOwnCheck = true;
            try { ok = sim.IsPointInLotSafelyRoutable(lot, pos); }
            finally
            {
                sInOwnCheck = false;
                if (async) { sSpotAsyncTasks.TryGetValue(task, out starts); sSpotAsyncTasks.Remove(task); }
            }
            bool deferred = async && SpotDeferTake(task);
            if (deferred) { c[3800]++; return null; }   // docs/635: no verdict, nothing cached; the caller takes EA's own trip
            if (async && SpotCutTake(task)) return null;   // docs/635 spotcut: the job was cut, no verdict, nothing cached
            if (async) SpotAsyncDone(sim, lot, pos, ok, starts, unchecked(System.Environment.TickCount - t0));
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
            if (ShortcutOn("unseenhouseboatview")) { int hv = UfHbvTownie(sim); if (hv >= 0) return hv == 1; }  // docs/202 §3.10: a Sim on a foreign houseboat is judged where its boat shows
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
        public partial class RabbitHoleHook : Sims3.Gameplay.Abstracts.RabbitHole
        {
            static int[] sCounters;
            public override RabbitHoleTuningClass RabbitHoleTuning { get { return null; } } // never instantiated; required by the base class
            public bool EnterHook(Sims3.Gameplay.Actors.Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            {
                if (sCounters == null) sCounters = PatchCounters();
                sCounters[4]++;
                if (ShortcutOn("rabbitholedirect")) { int d = RhDirect(a, inst, beforeEntering, routeMetaType); if (d != kRhdPass) return d == kRhdEntered; }
                Sims3.Gameplay.Abstracts.RabbitHole proxy = RabbitHoleProxy;
                if (ShortcutOn("rabbithole") && proxy != null && proxy.EnterSlots.Count > 0 && TownieUnseen(a, inst != null ? inst.SimFollowers : null))
                {
                    Lot lot = proxy.LotCurrent;
                    Sims3.SimIFace.Vector3 door = proxy.GetPositionOfSlot(proxy.EnterSlots[0]);
                    bool doorSeen = UcOnScreen(proxy) || !FarFromViewers(door);
                    if (lot != null && !lot.IsWorldLot && a.LotCurrent != lot && (a.Position - door).Length() > kShortcutMinDistance && doorSeen)
                    {
                        // stagedarrival: only the door is in view
                        if (ShortcutOn("stagedarrival")) StageNear(a, door, lot, kStageRabbit, UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")), null);
                    }
                    else if (lot != null && !lot.IsWorldLot && a.LotCurrent != lot && (a.Position - door).Length() > kShortcutMinDistance)
                    {
                        Sims3.SimIFace.Vector3 pos, fwd;
                        if (SafeSpot(a, lot, door, out pos, out fwd, "rabbithole", true)
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
            if (!TownieUnseen(sim, null) || (sim.Position - lot.Position).Length() <= kShortcutMinDistance) return false;
            // stagedarrival: only the destination is in view: walk on from the nearest unseen street point (true = staged, so the
            // visit hooks preset no followers, as for a shortcut)
            if (!FarFromViewers(lot.Position)) return ShortcutOn("stagedarrival") && StageNear(sim, lot.EntryPoint(), lot, kStageLot, mode, null);
            Sims3.SimIFace.Vector3 pos, fwd;
            if (!SafeSpot(sim, lot, lot.EntryPoint(), out pos, out fwd, name, true)) { c[idx + 2]++; return false; }
            // spotcheck (default ON): the gate judged the lot's position; the spot itself must be out of view too
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
                if (DpOn()) DpPulseNote(o);  // dormantprobe (DormantProbe.cs, docs/630)
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
                PlhBefore(sim, Target, Followers, "gotolot");  // docs/656 §8.2 petlonghome (PetLongHome.cs): pets only, off by default
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
                // visitonlot (default ON, townies only; nothing is moved): a townie already standing on the lot it visits arrives
                // without EA's route to the front door. EA's Run routes there anyway (RouteToFrontDoor, then PlanToLotEx); on a lot whose
                // front door cannot be reached from inside every such visit fails, idles and is picked again. Arrive() is the game's own
                // arrival. Household, selected Sims, followers, apartment shells, resorts and diving lots keep EA's Run.
                if (ShortcutOn("visitonlot") && (mFollowers == null || mFollowers.Count == 0) && Actor.LotCurrent == Target && VisitOnLotOk(Actor, Target))
                {
                    PatchCounters()[1293]++;
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
                    return Arrive();
                }
                if (mFollowers == null || mFollowers.Count == 0) PlhBefore(Actor, Target, null, "visit");  // petlongvisit (PetLongHome.cs, docs/656 §4.3)
                if ((mFollowers == null || mFollowers.Count == 0) && ShortcutToLot(Actor, Target, "visit", 7))
                {
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
                    if (ShortcutOn("visitarrive")) return Arrive();
                }
                Sims3.Gameplay.Actors.Sim actor = Actor;
                VwfTrip trip = VisitTripBegin(actor, Target, Autonomous);  // visitcar / visitwalkfinish (UnseenVisitTrip.cs); null when both are off
                try
                {
                    bool r = RunOrig();
                    if (r || trip == null || !trip.Finished || actor.HasBeenDestroyed || actor.LotCurrent != Target) return r;
                    // visitwalkfinish ended the walk on the lot and Run took it as a failed route: Run's own arrival step
                    actor.RemoveExitReason(Sims3.Gameplay.Actors.ExitReason.RouteFailed);
                    if (Autonomous && VwfLooks(this)) Target.MakeSimDoSomethingAfterArrivingAtVenue(Target.GetMetaAutonomyVenueType(), actor);
                    if (!Sims3.Gameplay.Situations.GroupingSituation.DoesGroupingSituationExistForFamily(actor))
                        Sims3.Gameplay.Situations.GroupingSituation.StartGroupingSitatuationWithFollowers(actor, mFollowers ?? new List<Sims3.Gameplay.Actors.Sim>());
                    return true;
                }
                finally { VisitTripEnd(actor, trip); }
            }
            static bool VisitOnLotOk(Sims3.Gameplay.Actors.Sim sim, Lot lot)
            {
                if (IsPlayerSim(sim) || sim.Household == null || lot == null || lot.IsWorldLot || lot.BuildableShells.Length > 0 || lot == Household.ActiveHouseholdLot) return false;
                return lot.CommercialLotSubType != CommercialLotSubType.kEP10_Resort && lot.CommercialLotSubType != CommercialLotSubType.kEP10_Diving;
            }

            public bool RunOrig() { return false; } // receives VisitCommunityLot.Run's original header

            // visitarrive (default ON; only after the shortcut above placed an unseen townie on the lot): the game's Run then still
            // routed to the front door (RouteToFrontDoor) or planned to the lot (PlanToLotEx), one 110-150 ms plan for a Sim already
            // standing at the lot's entry point. Here the game's Run is followed line by line minus the route: carpool wait, outfits
            // (cocktail venue, own workplace), then its success tail (NumSuccess, "do something after arriving" when autonomous,
            // grouping situation). A Sim carrying a child (EA re-queues it standing first) keeps the game's Run. Resorts and diving
            // lots never get here (ShortcutToLot). Counter [1292] arrivals without a route.
            bool Arrive()
            {
                if (Actor.CarryingChildPosture != null) return RunOrig();
                if (!Sims3.Gameplay.Objects.Vehicles.CarpoolManager.WaitForCarpool(Actor)) return false;
                Sims3.Gameplay.Actors.Sim.SwitchToFormalOutfitIfCocktail(Actor, Target);  // its answer only dresses followers: none here
                Sims3.Gameplay.Careers.Occupation occ = Actor.Occupation;
                if (occ != null && occ.ActiveCareerLotID == Target.LotId && occ.IsAllowedToWork())
                {
                    occ.EnsureSimHasOccupationOutfit();
                    if (Actor.Posture == Actor.Standing) Actor.SwitchToOutfitWithSpin(Sims3.Gameplay.Actors.Sim.ClothesChangeReason.GoingToWork);
                    else
                    {
                        Sims3.SimIFace.CAS.OutfitCategories cat;
                        Actor.GetOutfitForClothingChange(Sims3.Gameplay.Actors.Sim.ClothesChangeReason.GoingToWork, out cat);
                        Actor.OutfitCategoryToUseForRoutingOffLot = cat;
                    }
                }
                PatchCounters()[1292]++;
                NumSuccess++;
                VisitCommunityLot.Definition def = InteractionDefinition as VisitCommunityLot.Definition;
                if (Autonomous && (def == null || def.mLookForAutonomousActionsUponArrival))
                    Target.MakeSimDoSomethingAfterArrivingAtVenue(Target.GetMetaAutonomyVenueType(), Actor);
                if (!Sims3.Gameplay.Situations.GroupingSituation.DoesGroupingSituationExistForFamily(Actor))
                    Sims3.Gameplay.Situations.GroupingSituation.StartGroupingSitatuationWithFollowers(Actor, mFollowers);
                return true;
            }
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
                if (mFollowers != null && mFollowers.Count > 0) return;
                // docs/328 §7.5: a mod Run that is EA's for the lots the shortcut takes gets the game's visit shortcut ("visit", counters
                // [7]-[9]) instead of the opt-in unseenfbtrip path (CompatResolveCore.VisitEaShortcut)
                Type t = GetType();
                bool ea = CompatResolveCore.VisitEaShortcut(t.Assembly.GetName().Name, t.FullName, GameUtils.IsUniversityWorld(), ShortcutOn("unseenfbgohere"));
                if (ea ? ShortcutToLot(Actor, Target, "visit", 7) : ShortcutToLot(Actor, Target, "unseenfbtrip", 341, true))
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
            }
        }
        public class VclSubHook0 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook1 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook2 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        // 3-5 (docs/328 §7.5): GoHere (VisitCommunityLotEx, VisitCommunityLotWithEx) and More Shell Entries (two) in one install take 4
        public class VclSubHook3 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook4 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook5 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        static readonly Type[] kVclSubHooks = { typeof(VclSubHook0), typeof(VclSubHook1), typeof(VclSubHook2), typeof(VclSubHook3), typeof(VclSubHook4), typeof(VclSubHook5) };
        static readonly List<Type> sVclSubPatched = new List<Type>();
        static readonly List<Type> sVclSubOk = new List<Type>();  // Patch went in (CompatResolve's VisitRun line reports it)

        // at tuning time and again at world load (script mods may load after us); Run must not be compiled yet (Patch checks)
        internal static void ApplyTripSubclasses()
        { // reapply-safe: each subclass once (sVclSubPatched)
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
                    if (Patch(t, "Run", Type.EmptyTypes, kVclSubHooks[slot], "RunHook", "RunOrig")) sVclSubOk.Add(t);
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
