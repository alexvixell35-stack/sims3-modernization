// Compiles mods/ts3fixes (the player mod with the stutter fixes, docs/04 F8-F12) against the game's own DLLs and packs
// it into build/ts3fixes.package. Windows-only by nature: it needs the .NET Framework C# compiler (csc.exe, C# 5).
//   node tools/setup/build-fixes.ts [--on kTuningA,kTuningB=value] [--tag name]   (--on: also a package with those tunings True, or =value;
//   --tag: a `<!-- build name -->` comment in the tuning XML, so the package's tuningDigest names this build; the game ignores it)
// Also builds the TEST-ONLY mod mods/portalplan-test (MonoPatcher [TypePatch] on Lot.OnPortalPathPlan, docs/94 §20 job 17)
// into build/ts3-portalplan-test.package when MonoPatcher's package is in the installer cache; skipped otherwise.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { TYPE_S3SA, fnv64, s3saEncode, writePackage } from '../dbpf.ts';
import { publicize } from './publicize.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const CSC = process.env.TS3_CSC ?? 'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe'; // TS3_CSC: another C# compiler, e.g. Roslyn on Linux
const REF_DIR = process.env.TS3_REF_DIR ?? join(ROOT, 'game-copy', 'assemblies'); // from tools/extract-assemblies.ts
const REFS = ['mscorlib', 'System', 'System.Xml', 'ScriptCore', 'SimIFace', 'Sims3Metadata',
              'Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3StoreObjects', 'UI'];
const ASSEMBLY = 'Ts3Fixes';
const TYPE_XML = 0x0333406c;
// Tuning for Ts3Fixes.Instantiator: loading it applies the fixes; the other values are the player's settings.
const TUNING_XML = `<?xml version="1.0" encoding="utf-8"?>
<base>
  <Current_Tuning>
    <kInstantiator value="True" />
    <kGoHomeShortcut value="True" />
    <kBuildingTripShortcut value="True" />
    <kLotTripShortcut value="True" />
    <kBroadcasterThrottle value="True" />
    <kWalkStyleComparer value="True" />
    <kVirtualHomeShortcut value="True" />
    <kNoRouteFailWalkOffscreen value="True" />
    <kStuckWatchdog value="True" />
    <kStuckWatchdogIdle value="True" />
    <kRouteFailureBackoff value="True" />
    <kSaveCleanup value="False" />
    <kSnapshotCap value="0" />
    <kSaveCleanupAggressive value="False" />
    <kAggressiveSnapshotKeep value="10" />
    <kAggressiveQuietDays value="56" />
    <kPlayedGenerations value="2" />
    <kPlayedSnapshotKeep value="40" />
    <kNoStationDetourOffscreen value="True" />
    <kPortalAdaptorLeak value="True" />
    <kCasGrid value="False" />
    <kSpatialCache value="False" />
    <kRoutableCache value="False" />
    <kStuckCheckBusy value="False" />
    <kStuckCheckAsync value="True" />
    <kSafeSpotCap value="False" />
    <kFarthestLotSorted value="False" />
    <kIndoorPositionCap value="False" />
    <kFastRefTable value="True" />
    <kCachedReflection value="False" />
    <kMotiveBarImageCache value="True" />
    <kBuffGridInPlace value="True" />
    <kSimNameCache value="True" />
    <kDetailPriorityCache value="False" />
    <kHoursMemo value="False" />
    <kVenueMemo value="False" />
    <kPermaMoodletMemo value="False" />
    <kBuffRemoveNoOp value="False" />
    <kCatalogNameCache value="False" />
    <kAutonomyRoomMemo value="False" />
    <kAutonomySliceTime value="False" />
    <kPortalInvocationCache value="False" />
    <kZombieDanceEmptyOut value="False" />
    <kDecayMultMemo value="False" />
    <kAutLive value="True" />
    <kAutLiveGuard value="True" />
    <kAutLiveGuardOffPm value="1000" />
    <kAutLiveGuardOnPm value="800" />
    <kAutLiveGuardMinSearches value="40" />
    <kAutLiveGuardMinFrames value="300" />
    <kAutLiveLate value="False" />
    <kAutLiveLateTicks value="3" />
    <kAutLiveGuardHold value="600" />
    <kAutUnseenPace value="True" />
    <kAutUnseenCmp value="False" />
    <kAutUnseenRabbitHoleGame value="True" />
    <kAutUnseenPacePct value="100" />
    <kSafeSpotReuse value="False" />
    <kWildflowerProbe value="False" />
    <kWildflowerTask value="True" />
    <kCaneEarlyOut value="False" />
    <kTooltipThrottle value="False" />
    <kDoorMirror value="False" />
    <kAutShadow value="False" />
    <kReplanDefer value="False" />
    <kReplanSpread value="False" />
    <kUnseenEnvThrottle value="False" />
    <kUnseenMotiveThrottle value="False" />
    <kUnseenBroadcastThrottle value="False" />
    <kUnseenPark value="False" />
    <kUnseenParkMinutes value="30" />
    <kUnseenParkStaggerTicks value="16" />
    <kUnseenThrottleEvery value="4" />
    <kUnseenBroadcastEvery value="16" />
    <kUnseenCarThrottle value="False" />
    <kHitchCauseHooks value="False" />
    <kDetailPriorityExact value="True" />
    <kMegaphoneRun value="True" />
    <kDragonCurseRun value="True" />
    <kVirtualHomeWalk value="True" />
    <kSpeedSkipLevel value="True" />
    <kSpeedSkipNoActor value="True" />
    <kSpeedRecalcLock value="True" />
    <kSpeedLockOwner value="True" />
    <kAlarmZeroPeriod value="True" />
    <kSpoilBroadcast value="True" />
    <kWeatherStoneRelisten value="True" />
    <kBouncerMemo value="True" />
    <kBirthdayAgeRecord value="True" />
    <kHeartBrokenBcast value="True" />
    <kRhExitUnseen value="True" />
    <kTipsNpcEnd value="True" />
    <kBarPutDownWait value="True" />
    <kPutDownSurfaceUse value="True" />
    <kSwipeRestore value="True" />
    <kUnicornSpawnFail value="True" />
    <kDataDiscPutBack value="True" />
    <kSpawnerSkip value="True" />
    <kMapTagTaskAlive value="True" />
    <kLotMgrCallbacks value="True" />
    <kFireplaceHerb value="True" />
    <kRummageBcast value="True" />
    <kStairWaitEnd value="True" />
    <kSimDisposeLink value="True" />
    <kWakeHooks value="False" />
    <kWakePark value="False" />
    <kWakePoll value="False" />
    <kWakeParkTicks value="12" />
    <kWakePollTicks value="30" />
    <kUnseenCarEvery value="4" />
    <kOverwatchSpread value="False" />
    <kOverwatchChain value="True" />
    <kErrorTrapSpread value="True" />
    <kEaSpreadLots value="True" />
    <kEaSpreadPets value="True" />
    <kEaSpreadDecay value="True" />
    <kRouteSources value="False" />
    <kLegReplanSkip value="False" />
    <kBouncerSlotSkip value="True" />
    <kIdlePlanMemo value="False" />
    <kStuckCheckNear value="False" />
    <kFailRepeat value="False" />
    <kJobYield value="True" />
    <kJobYieldRoad value="True" />
    <kJobYieldRoadOw value="True" />
    <kJobYieldSpawn value="True" />
    <kJobYieldReady value="True" />
    <kJobYieldFp value="True" />
    <kDriverTaskAsync value="False" />
    <kWatchdogAsync value="True" />
    <kStuckCheckOwn value="False" />
    <kOverwatchStuckSlice value="True" />
    <kTagUnseen value="True" />
    <kSeasonMarkerCache value="False" />
    <kSpotCheck value="True" />
    <kSafeSpotPool value="True" />
    <kSafeSpotDedupe value="True" />
    <kSafeSpotYield value="False" />
    <kSafeSpotPlanYield value="True" />
    <kDisposeSpread value="False" />
    <kOverwatchStuckNoticeSummary value="False" />
    <kPrePlanCallback value="False" />
    <kStuckCheckCap value="False" />
    <kUnseenFallbackStuck value="True" />
    <kUnseenFallbackPlan value="True" />
    <kUnseenFallbackTeleport value="True" />
    <kUnseenFallbackGhost value="True" />
    <kUnseenFallbackNoSync value="True" />
    <kUnseenHouseboatView value="False" />
    <kUnseenGhostMediumM value="200" />
    <kUnseenGhostGoalM value="120" />
    <kUnseenFallbackSites value="False" />
    <kUnseenFallbackPre value="True" />
    <kUnseenFallbackArrive value="True" />
    <kUnseenFallbackLeave value="True" />
    <kUnseenFallbackTrip value="False" />
    <kUnseenFallbackTripHold value="False" />
    <kGoalHouseholdExempt value="True" />
    <kUnseenFallbackLoopDetect value="True" />
    <kUnseenFallbackLoopBreak value="False" />
    <kUnseenFallbackFinish value="False" />
    <kUnseenRouteRelaxed value="False" />
    <kUnseenBoat value="True" />
    <kUnseenBoatStart value="True" />
    <kBoatTaxiMemo value="True" />
    <kBoatTaxiMemoHours value="4" />
    <kHouseboatLegMemo value="True" />
    <kHouseboatLegMemoHours value="2" />
    <kHouseboatGangwayRescue value="True" />
    <kBoatRoamCap value="True" />
    <kBoatRoamCapHours value="2" />
    <kBoatRoamCapRoams value="3" />
    <kBoatRoamCapGapHours value="4" />
    <kBoatRoamCapBackoffHours value="1" />
    <kBoatRoamCapNightFrom value="23" />
    <kBoatRoamCapNightTo value="6" />
    <kBoatRoamCapMotive value="-50" />
    <kPosturePopCycle value="False" />
    <kOverwatchIgnoreStuckProbes value="True" />
    <kSeasonImposter value="False" />
    <kSeasonImposterCold value="False" />
    <kPreferredService value="False" />
    <kSocialWorkerOnLot value="False" />
    <kEaNullGuards value="False" />
    <kShowStageThrowables value="False" />
    <kFirePitChair value="False" />
    <kAgeUpKeepOutfits value="True" />
    <kAgeUpSwap value="False" />
    <kOutfitWaitPoll value="True" />
    <kUniformBackup value="True" />
    <kConvTurn value="False" />
    <kDeadRefGuards value="False" />
    <kBakeSaleSeller value="True" />
    <kNullService value="True" />
    <kRabbitHoleDoor value="True" />
    <kPhotoSession value="True" />
    <kEggSpot value="True" />
    <kGraduationTable value="True" />
    <kJazzParam value="True" />
    <kBuffUpdate value="True" />
    <kCarNoRoute value="True" />
    <kNpcDriverPool value="True" />
    <kBookCarry value="True" />
    <kAgeUpAbort value="True" />
    <kRewardGiver value="True" />
    <kDatingProfile value="True" />
    <kLeaveJobDispose value="True" />
    <kInvNotYet value="True" />
    <kMetricSkillMissing value="True" />
    <kFightOrphanWait value="True" />
    <kPoolNoLane value="True" />
    <kAutonomyFailCooldown value="True" />
    <kVenuePickTime value="True" />
    <kVenuePickFast value="False" />
    <kFireworksFizzle value="True" />
    <kBoatRouteNull value="False" />
    <kUnseenRelax value="True" />
    <kIaFailMemo value="False" />
    <kBoatTrip value="True" />
    <kBoatPlace value="True" />
    <kBoatPlaceMaxM value="24" />
    <kRetryScope value="True" />
    <kBoatLandStuck value="True" />
    <kOutfitMissing value="True" />
    <kAgeUpDupInst value="False" />
    <kOverwatchResetGone value="False" />
    <kMotivesNoHousehold value="False" />
    <kFrontStageOwner value="True" />
    <kCareerSkillNull value="True" />
    <kStuckHbSkip value="True" />
    <kTurnJigSkip value="True" />
    <kOverwatchRelRetry value="True" />
    <kFireworksWatchLate value="True" />
    <kRoleNullPurge value="True" />
    <kErrorTrapLoop value="True" />
    <kFightStayCap value="True" />
    <kLoopIdleBridge value="True" />
    <kBookHand value="True" />
    <kBookHandDiag value="False" />
    <kPetPoolOrphan value="True" />
    <kErrorTrapSims value="True" />
    <kCelebrityDeadSim value="True" />
    <kSeasonsDeadSim value="False" />
    <kGhostReactDeadSim value="True" />
    <kCasUiHandlers value="False" />
    <kErrorTrapLookup value="True" />
    <kErrorTrapCtl value="True" />
    <kDreamNodeDeadSim value="True" />
    <kOverwatchDeadSim value="True" />
    <kSimRefsDeadSim value="False" />
    <kOutfitWaitSaved value="True" />
    <kDeadSimCount value="False" />
    <kDeadSimHollow value="False" />
    <kDnpSubjectCache value="True" />
    <kHudUnsub value="True" />
    <kMapViewKeep value="True" />
    <kTinyUiEventReg value="True" />
    <kCustomCtlSweep value="True" />
    <kExitCbSweep value="True" />
    <kDnpPetKeys value="True" />
    <kDnpObjKeys value="True" />
    <kRelTablesApart value="True" />
    <kAsyncJigSync value="True" />
    <kDeadRefSweep value="True" />
    <kVacExport value="False" />
    <kVacStoreGuard value="False" />
    <kVacReturnSkip value="False" />
    <kVacTeenDelete value="False" />
    <kVacExitReturn value="False" />
    <kVacListenerNull value="False" />
    <kVacImportFix value="False" />
    <kVacListenerPad value="False" />
    <kSpawnCapCount value="False" />
    <kMenuMaven value="True" />
    <kDanceFloorListGuard value="True" />
    <kBoatGetOutDiag value="False" />
    <kBoatGetOutGuard value="False" />
    <kUnseenOutfitDefer value="True" />
    <kUnseenOutfitNoSpin value="True" />
    <kBoatParkGone value="True" />
    <kDeadRefCooking value="True" />
    <kDeadRefConvo value="True" />
    <kTakeARiskGuard value="True" />
    <kPlaypenFlags value="False" />
    <kVoodooBindWait value="False" />
    <kSaunaWooHooPreg value="False" />
    <kLotEntryPet value="False" />
    <kHorseLotEntry value="False" />
    <kMiniHomeLot value="True" />
    <kVehParkReserve value="False" />
    <kVehGetInLeak value="False" />
    <kVehSeatDestroy value="False" />
    <kVehFrankenTaxi value="False" />
    <kVehProtestCar value="False" />
    <kBuyIconLeak value="True" />
    <kUnseenTraffic value="True" />
    <kNpcCarExit value="True" />
    <kCasRowLeak value="True" />
    <kCompositorLeak value="True" />
    <kGenealogyCycle value="True" />
    <kSpouseUnlink value="True" />
    <kRelRemoveSafe value="True" />
    <kFamilyInvGhost value="True" />
    <kSellCursorLeak value="True" />
    <kAttractionAlarm value="True" />
    <kUncleGuard value="True" />
    <kMoodDrift value="True" />
    <kDnpDisposeListener value="True" />
    <kSeasonDup value="True" />
    <kDreamRootOrphan value="True" />
    <kBlogOrphan value="True" />
    <kDnpUpdateGuard value="True" />
    <kBuyCellRecycle value="True" />
    <kBuyClearRecycle value="True" />
    <kBuyGridSweep value="True" />
    <kMoodPending value="True" />
    <kJoyrideCostume value="True" />
    <kAlarmKillLog value="True" />
    <kEtDerefDiag value="True" />
    <kSpawnFix value="True" />
    <kSpawnOffWorld value="False" />
    <kSocialFinish value="True" />
    <kSocialReceiveFail value="True" />
    <kPetFirePlan value="False" />
    <kBurntTileFloor value="False" />
    <kSplashRoof value="False" />
    <kShuffleboardBcast value="True" />
    <kCookClickListener value="True" />
    <kVipRoomRefresh value="True" />
    <kCourseListener value="True" />
    <kObservatoryMsg value="True" />
    <kBubblePipeGuard value="True" />
    <kBuffState value="True" />
    <kStoreWorldEvents value="True" />
    <kBowlingLane value="True" />
    <kGenieBanish value="True" />
    <kSpaceSimPartner value="True" />
    <kBandJam value="True" />
    <kStatueLine value="True" />
    <kCleanFlag value="True" />
    <kBirdTvNull value="True" />
    <kStagedArrival value="False" />
    <kIceCreamDirect value="True" />
    <kNearVenue value="True" />
    <kVisitArrive value="True" />
    <kVisitOnLot value="True" />
    <kGoHomeShell value="False" />
    <kGoHomeWalk value="True" />
    <kRabbitHoleDirect value="True" />
    <kRhOutside value="True" />
    <kUnseenLaneSnap value="True" />
    <kUnseenLegSnap value="True" />
    <kPhotoBoothFunds value="True" />
    <kShellLighting value="True" />
    <kBonfireSpice value="True" />
    <kDjCleanup value="True" />
    <kVampireHuntGlow value="True" />
    <kHotTubWoohooFx value="True" />
    <kBribeGuard value="True" />
    <kOppDeadListeners value="True" />
    <kDaycareQuit value="True" />
    <kPrivateEyeCase value="True" />
    <kWeddingCancel value="True" />
    <kGhostSpawnArgs value="True" />
    <kMoveInPartner value="True" />
    <kReaperFlow value="True" />
    <kServiceLeave value="True" />
    <kVisitGreetAlarm value="True" />
    <kFieldTripAdult value="True" />
    <kAutonomyFailBlock value="True" />
    <kVisitTooLate value="True" />
    <kSpoonReset value="True" />
    <kHomeworkHelp value="True" />
    <kGroupTalkStale value="True" />
    <kConvMerge value="True" />
    <kPortraitPose value="True" />
    <kPicnicCamera value="True" />
    <kUpgradeFailAlarm value="True" />
    <kRepairTimeCache value="True" />
    <kBurglarIdle value="True" />
    <kGigWaitLeave value="True" />
    <kSculptPose value="True" />
    <kFestivalHidden value="True" />
    <kTrainBoost value="True" />
    <kEatOutsideCount value="True" />
    <kQueueProximity value="True" />
    <kSubRouteFail value="True" />
    <kMopUnreachable value="True" />
    <kPetPuddleTile value="True" />
    <kOutfitWaitEnd value="True" />
    <kOutfitWaitEndMinutes value="30" />
    <kInitStackTrace value="True" />
    <kWeatherStoneListener value="True" />
    <kWeatherManagerThrow value="True" />
    <kRainShelterCheap value="True" />
    <kPondClearScan value="True" />
    <kSoakedStat value="True" />
    <kOuterwearAutoLoop value="True" />
    <kOuterwearRestore value="True" />
    <kHoverTrainTest value="True" />
    <kNewCarTest value="True" />
    <kSpoonSlaveChecks value="True" />
    <kPetWakeBed value="True" />
    <kSlaveWakeReason value="True" />
    <kHandsetReset value="True" />
    <kFlowersInUse value="True" />
    <kUseListPrune value="True" />
    <kChestUseList value="True" />
    <kResetInUseFree value="True" />
    <kPutDownInUse value="True" />
    <kAutonomyInUseBlock value="True" />
    <kNpcBusyPartner value="True" />
    <kButlerPayAlarm value="True" />
    <kSandwichAlarm value="True" />
    <kDreamCleanup value="True" />
    <kVehicleFxHelper value="True" />
    <kPrankDitchEmpty value="True" />
    <kServiceAssignExit value="True" />
    <kServiceShowAlarm value="True" />
    <kMailDogChaser value="True" />
    <kLifeguardSurvey value="True" />
    <kMoonRiseListener value="True" />
    <kHolidayLights value="True" />
    <kDoorLockOwner value="True" />
    <kElevatorKitten value="True" />
    <kElevatorResetRiders value="True" />
    <kElevatorSavedLog value="True" />
    <kStarLightReset value="True" />
    <kMoonDialListener value="True" />
    <kSnubListener value="True" />
    <kElevatorRiderTimeout value="True" />
    <kRoleNullGuard value="True" />
    <kHomeworkSkillBonus value="True" />
    <kHorseRanchChallenge value="True" />
    <kDigSiteEmptyPick value="True" />
    <kOppChainWorld value="True" />
    <kTeleportCommunityLot value="True" />
    <kPhotoSubjectStars value="True" />
    <kResidentSpawnSlot value="True" />
    <kTalkOccupationChance value="True" />
    <kShellDoorSummon value="True" />
    <kDisposeBase value="True" />
    <kBoardingSchoolMoney value="True" />
    <kFreshClothingResume value="True" />
    <kTwinBellyShape value="True" />
    <kBoatStaleSwimmer value="True" />
    <kOppCategoryProtect value="True" />
    <kNoCommercialCar value="True" />
    <kScavengerNoLot value="True" />
    <kPartyLotListener value="True" />
    <kWeddingListener value="True" />
    <kInnerBeautyOnce value="True" />
    <kSpRelCount value="True" />
    <kSpCollectNull value="True" />
    <kSpCloneDict value="True" />
    <kSpLotLookup value="True" />
    <kSpBucketIndex value="True" />
    <kSpCreateMoveIn value="True" />
    <kSpLotDensity value="True" />
    <kSpRoommateJob value="True" />
    <kSpOccultPick value="True" />
    <kSpJobAtWork value="True" />
    <kSpRecheck value="True" />
    <kSpPensionPrompt value="True" />
    <kHhCleanupDup value="True" />
    <kVirtualHomeShare value="True" />
    <kCelebAlarmNull value="True" />
    <kDisposeVirtualSlot value="True" />
    <kAgingAlarmCancel value="True" />
    <kVirtualMoveInDup value="True" />
    <kPetPoolGhost value="True" />
    <kEatPostOnce value="True" />
    <kMotiveMultOnce value="True" />
    <kFightBroadcastEnd value="True" />
    <kReactionCopy value="True" />
    <kCarpoolSchoolSkip value="True" />
    <kTvSyncAlarm value="True" />
    <kPhotoFunds value="True" />
    <kLtrHistoryBits value="True" />
    <kGoodbyePure value="True" />
    <kBathCleanup value="True" />
    <kTvWatchSkill value="True" />
    <kComputerGameType value="True" />
    <kMoodTankReset value="True" />
    <kReactionGrumpy value="True" />
    <kTrainSimGone value="True" />
    <kStereoUpgrade value="True" />
    <kBuffRemoveDelayed value="True" />
    <kTvDiscListener value="True" />
    <kFridgeTestNull value="True" />
    <kCancelPostureDef value="True" />
    <kFacialIdleWeight value="True" />
    <kBathSituationFx value="True" />
    <kSocialNoEffect value="True" />
    <kGuitarGnomeBoost value="True" />
    <kDistressGate value="True" />
    <kMoodIdleFlavor value="True" />
    <kProtestExit value="True" />
    <kJogTerrain value="True" />
    <kLifeguardQuit value="True" />
    <kFrolicExit value="True" />
    <kShooDef value="True" />
    <kIglooLookats value="True" />
    <kGoHereBoatList value="True" />
    <kBouncerRole value="True" />
    <kTrickSocials value="True" />
    <kFearFaintUndo value="True" />
    <kLotPrune value="True" />
    <kWerewolfFind value="True" />
    <kCopyHomeworkFx value="True" />
    <kSpellTargetEnd value="True" />
    <kFoodTrayGrill value="True" />
    <kCompositionNull value="True" />
    <kPetDreamTimer value="True" />
    <kAwkwardScience value="True" />
    <kChessDefeated value="True" />
    <kPrivacyOthers value="True" />
    <kGigMaster value="True" />
    <kIntMultiple value="True" />
    <kDoorPrivacyNull value="True" />
    <kLeafBurnFail value="True" />
    <kCatchPreyOdds value="True" />
    <kPicnicAnts value="True" />
    <kLoyalDogLay value="True" />
    <kVisitListeners value="True" />
    <kScienceFxStop value="True" />
    <kDaycareLiking value="True" />
    <kKegBreakage value="True" />
    <kRomanceCooldown value="True" />
    <kLightCustom value="True" />
    <kEatPreyCancel value="True" />
    <kMeltAlarm value="True" />
    <kNewspaperDef value="True" />
    <kLeaveConvNull value="True" />
    <kVacationAlarms value="True" />
    <kCleanupVfx value="True" />
    <kLookAtCopy value="True" />
    <kLottoTooltip value="True" />
    <kTraitIconDislike value="True" />
    <kRoleUnprotect value="True" />
    <kPutDownPlaced value="True" />
    <kNullSituation value="True" />
    <kLightningWeights value="True" />
    <kPlantDormantFlip value="True" />
    <kPlantDormantTime value="True" />
    <kHarvestExtras value="True" />
    <kShroomSlots value="True" />
    <kHarvestRegion value="True" />
    <kWellStealCooldown value="True" />
    <kOppFailCharge value="True" />
    <kTempForecast value="True" />
    <kMountFishCost value="True" />
    <kFirefighterCount value="True" />
    <kRepairmanSkill value="True" />
    <kRoundUpReturn value="True" />
    <kPetHarvestVfx value="True" />
    <kSuatGradeBonus value="True" />
    <kCatToyPickerRel value="True" />
    <kDogSwimSlots value="True" />
    <kFragmentReconstruct value="True" />
    <kLabObjectPush value="True" />
    <kBusinessPlannerRun value="True" />
    <kFutureDescendantSize0 value="True" />
    <kJungleGymExitQueue value="True" />
    <kWildHorseLot value="True" />
    <kHarvesterUnripe value="True" />
    <kHotTubSeatPosture value="True" />
    <kTeenPartyBreakUp value="True" />
    <kDogSwimAroundList value="True" />
    <kTutorBalloonAlarm value="True" />
    <kChemistryVfx value="True" />
    <kDartboardVfx value="True" />
    <kWardrobeWandVfx value="True" />
    <kPetDroolVfx value="True" />
    <kTimePortalVfx value="True" />
    <kHayStackVfx value="True" />
    <kHayScatterTime value="True" />
    <kHauntedGnomeInv value="True" />
    <kHoudiniGnomeLeak value="True" />
    <kShowStageFxEnd value="True" />
    <kSoakedPetSelfReact value="True" />
    <kTagBagSurfaceLeak value="True" />
    <kEquestrianTrophyName value="True" />
    <kRubbleUseList value="True" />
    <kBackpackLotLeak value="True" />
    <kStreetArtMasterBoost value="True" />
    <kBubblingSpringsReset value="True" />
    <kFloorSwitchUseList value="True" />
    <kDonationBuffChance value="True" />
    <kVampireFruitSnack value="True" />
    <kFallenRockCollected value="True" />
    <kSpellBookSummonLoad value="True" />
    <kChocFountainGeyser value="True" />
    <kHypnoTrickRelease value="True" />
    <kVoodooFairyFadeIn value="True" />
    <kSaunaTubClear value="True" />
    <kSlotMachineDefs value="True" />
    <kTreeWishingWell value="True" />
    <kPlaypenTalkCast value="True" />
    <kOvenMenuNull value="True" />
    <kFvStandSkillCurve value="True" />
    <kPokerDeadPlayer value="True" />
    <kRouletteHackReset value="True" />
    <kCoopEggQuality value="True" />
    <kFireActiveJob value="True" />
    <kFireTrapCount value="True" />
    <kBirthdayAgers value="True" />
    <kSneakOutListener value="True" />
    <kPromCarpool value="True" />
    <kCurfewAlarm value="True" />
    <kTrickOrTreatAlarm value="True" />
    <kSnowFightJoinWait value="True" />
    <kHauntedUmbrella value="True" />
    <kPermaZombieMoon value="True" />
    <kSteadyGigMiss value="True" />
    <kNpcShowStuck value="True" />
    <kGigPullAudience value="True" />
    <kLunacySocials value="True" />
    <kFairyAuraTimer value="True" />
    <kNpcSeasonalParty value="True" />
    <kProfessorTerm value="True" />
    <kProtestLastDay value="True" />
    <kSeasickPaused value="True" />
    <kResortWeddingAlarm value="True" />
    <kPlumbotFaceListener value="True" />
    <kEfficientChip value="True" />
    <kSentienceName value="True" />
    <kJetpackFlyingBuff value="True" />
    <kHorseTraitScore value="True" />
    <kWildHorseGender value="True" />
    <kLitterCap value="True" />
    <kPhoneLoadReset value="True" />
    <kPhoneCallSchedule value="True" />
    <kPhoneCallRearm value="True" />
    <kWooHootyWait value="True" />
    <kInviteCallReply value="True" />
    <kGradVisitors value="True" />
    <kResumeTns value="True" />
    <kUniTravelFlag value="True" />
    <kReturnHomeAlarms value="True" />
    <kPhoneFixUp value="True" />
    <kPhoneGive value="True" />
    <kWildHerdMother value="True" />
    <kSplitPartner value="True" />
    <kMovingFlag value="True" />
    <kSandsCoins value="True" />
    <kTombMusic value="True" />
    <kArtSellNoArtist value="True" />
    <kRoyaltyOrphan value="True" />
    <kPromoteShift value="True" />
    <kGoToWorkStale value="True" />
    <kSocialReplanLog value="False" />
    <kBarOrderLog value="False" />
    <kPetPutDownWait value="True" />
    <kPuddleReach value="True" />
    <kPushWaitSkip value="True" />
    <kRoutableAsync value="True" />
    <kRouteCauseLog value="False" />
    <kScarePriority value="True" />
    <kGroupChatPriority value="True" />
    <kTrashPileImport value="False" />
    <kCatGymWatch value="False" />
    <kBadSecretKey value="False" />
    <kTraitListenerRemove value="False" />
    <kPortAskNoHome value="False" />
    <kLectureBroadcaster value="False" />
    <kResidentSpawnWalk value="False" />
    <kMidlifeWishSave value="False" />
    <kBetrayedNullSim value="False" />
    <kTopicTestNull value="False" />
    <kOppTrackerRelisten value="False" />
    <kTraitRemoveNull value="False" />
    <kHomeworkCopyOld value="False" />
    <kLaundryMoodEach value="False" />
    <kPartyCleanupNull value="False" />
    <kSafeLocUnhide value="False" />
    <kMidlifeUiEnding value="False" />
    <kNemesisNoRomance value="False" />
    <kDisplayCaseReset value="True" />
    <kMermaidSharkNull value="True" />
    <kJetpackFlyHereCleanup value="True" />
    <kNaniteRouteFail value="True" />
    <kSpringDanceClear value="True" />
    <kNectarCopyLevel10 value="True" />
    <kHarvestEatVfx value="True" />
    <kBuffetEmptyPick value="True" />
    <kKrakenFriendSailor value="True" />
    <kMummyCurseTrait value="True" />
    <kBullRiderCleanup value="True" />
    <kBroomTipPerformer value="True" />
    <kBalancingBallCareer value="True" />
    <kElvisGnomeGuard value="True" />
    <kElvisGnomeCap value="True" />
    <kGourmetPetRecipe value="True" />
    <kChipFamilyCrystal value="True" />
    <kChipComponentOrphan value="True" />
    <kChipAnyCount value="True" />
    <kNaniteMerge value="True" />
    <kConsignmentLastDay value="True" />
    <kBartendingHourlyPay value="True" />
    <kInfluenceFollowerBonus value="True" />
    <kBroomArenaChoice value="True" />
    <kFortuneTellerScamOdds value="True" />
    <kConsignmentSkillCurve value="True" />
    <kConsignmentStockLeak value="True" />
    <kHerbQualityFloor value="True" />
    <kSnackBowlSlobVfx value="True" />
    <kWorldPondSkateTest value="True" />
    <kPingPongOpponent value="True" />
    <kMermaidDehydrateLook value="True" />
    <kMeteorMagnetOrphan value="True" />
    <kDaycareLateAlarm value="True" />
    <kBoxOfMysteryWooHoo value="True" />
    <kBodySculptorWeight value="True" />
    <kNectarConsignValue value="True" />
    <kStrayGnomeTns value="True" />
    <kBirthRoute value="True" />
    <kPutDownPlace value="True" />
    <kAwardKeep value="False" />
    <kCeremonySpot value="True" />
    <kEventCrowdRing value="True" />
    <kEventRouteRetry value="True" />
    <kSimFestKeep value="False" />
    <kWeddingArchKeep value="True" />
    <kWeddingCleanup value="True" />
    <kHsGradKeep value="True" />
    <kUniGradKeep value="False" />
    <kBsRefund value="False" />
    <kComboProtest value="True" />
    <kComboVenue value="False" />
    <kComboSituation value="True" />
    <kComboJob value="True" />
    <kComboProtestLot value="True" />
    <kComboOpp value="True" />
    <kHiddenRoomNpc value="True" />
    <kHiddenRoomSpawn value="True" />
    <kAptLeave value="True" />
    <kPartyOutcome value="False" />
    <kPartyMotive value="True" />
    <kFilterGridLeak value="True" />
    <kCompositorGhost value="True" />
    <kCasPuckEvents value="True" />
    <kFamCursorLeak value="True" />
    <kCasExitSweep value="True" />
    <kSeasonListener value="True" />
    <kCasFilterGrid value="True" />
    <kUnseenLoopSkipStep value="False" />
    <kDoorGen value="True" />
    <kTreasureSpawnInvDup value="True" />
    <kRouteFailProbe value="True" />
    <kRhEntryRelax value="True" />
    <kRhEntryRelaxNear value="True" />
    <kPlayerFirst value="True" />
    <kZoomTier value="True" />
    <kAutMetaRows value="True" />
    <kBuffRemoveMerge value="True" />
    <kSwSlots value="True" />
    <kStuckWatchScan value="True" />
    <kLifeEventIdFirst value="True" />
    <kBuffSrcEmpty value="True" />
    <kBuffPausedTimeout value="True" />
    <kMoodDriftNoAlloc value="True" />
    <kUnseenService value="True" />
    <kUnseenServiceOffscreen value="True" />
    <kBouncerPlace value="True" />
    <kUnseenPianist value="True" />
    <kUnseenMusicWatch value="True" />
    <kUnseenJam value="True" />
    <kUnseenShow value="True" />
    <kUnseenLoopSkip value="True" />
    <kUnseenLoopSkipOffscreen value="False" />
    <kTimerAlarmLeak value="True" />
    <kWishFeedbackKeep value="True" />
    <kWishSubjectGone value="True" />
    <kWishUpdateLost value="True" />
    <kSocialWorkerPromise value="True" />
    <kEnumCmpNoBox value="True" />
    <kAlarmCmpNoBox value="True" />
    <kSarcMummyNpc value="True" />
  </Current_Tuning>
</base>
`;

const missing = REFS.filter(r => !existsSync(join(REF_DIR, `${r}.dll`)));
if (missing.length) {
  console.error(`missing game DLLs in ${REF_DIR}: ${missing.join(', ')}. Run tools/extract-assemblies.ts first.`);
  process.exit(1);
}
const outDir = process.env.TS3_BUILD_DIR ?? join(ROOT, 'build');
mkdirSync(outDir, { recursive: true });
const tagArg = process.argv.indexOf('--tag'), tag = tagArg > 0 ? process.argv[tagArg + 1] ?? '' : '';
if (tagArg > 0 && !/^[\w.]+(-[\w.]+)*$/.test(tag)) { console.error(`--tag: '${tag}' must be letters, digits, '.', '_' or single '-'`); process.exit(1); }
const tagXml = (xml: string) => tag ? xml.replace('<base>', `<base>\n  <!-- build ${tag} -->`) : xml;
const src = join(ROOT, 'mods', 'ts3fixes', 'src');
const dll = join(outDir, `${ASSEMBLY}.dll`);
// CheckLiveMap.cs (the batch Checks' live-path assert) is generated from the source; it also gates the constant gap.
execFileSync(process.execPath, [join(ROOT, 'tools', 'static', 'check-live-map.ts')], { stdio: 'inherit' });
// EA's Mono never attaches a MonoThread, so any [ThreadStatic] / [ContextStatic] field access faults in
// mono_get_special_static_data (TS3W 0x00EBAB5B, null+0x40; docs/191). Refuse to build them.
const lintStatics = (srcDir: string) => {
  for (const f of readdirSync(srcDir).filter(f => f.endsWith('.cs')))
    readFileSync(join(srcDir, f), 'utf8').split('\n').forEach((l, i) => {
      if (/^\s*\[\s*(System\.)?(ThreadStatic|ContextStatic)(Attribute)?\s*\]/.test(l))
        throw new Error(`${f}:${i + 1}: [ThreadStatic]/[ContextStatic] crashes the game's Mono (no thread statics); use a plain static`);
    });
};
// docs/274 §11: ts3fixes implements EA's private Lot.IObjectCache (envlistmemoexact); csc gets a reference copy of
// Sims3GameplaySystems with that one type's visibility bits set to nested public (tools/setup/publicize.ts, build/ only)
const PUB_REF: Record<string, string[]> = { Sims3GameplaySystems: ['IObjectCache'] };
const refPath = (r: string) => {
  if (!PUB_REF[r]) return join(REF_DIR, `${r}.dll`);
  const pub = join(outDir, 'ref', `${r}.dll`);
  mkdirSync(join(outDir, 'ref'), { recursive: true });
  writeFileSync(pub, publicize(readFileSync(join(REF_DIR, `${r}.dll`)), PUB_REF[r]));
  return pub;
};
const compile = (srcDir: string, out: string, extraRefs: string[] = []) => (lintStatics(srcDir),
  execFileSync(CSC, ['/nologo', '/nostdlib+', '/noconfig', '/target:library', '/optimize+', '/langversion:5', '/warn:4',
    '/nowarn:414,169,649', `/out:${out}`, ...REFS.map(r => `/reference:${refPath(r)}`), ...extraRefs.map(r => `/reference:${r}`),
    // file names relative to cwd: absolute paths from a worktree overflow Windows' 32 K command line
    ...readdirSync(srcDir).filter(f => f.endsWith('.cs'))], { stdio: 'inherit', cwd: srcDir }));
compile(src, dll);
// what only the game's Mono finds: unresolved refs and name-only GetMethod lookups that are ambiguous in the game's
// assemblies (one in a static initializer killed every fix in r43). Local-only: skipped without game-copy.
execFileSync(process.execPath, [join(ROOT, 'tools', 'static', 'refs-resolve-check.ts'), dll, '--src', src], { stdio: 'inherit' });
const pkg = writePackage([
  { type: TYPE_S3SA, group: 0, instance: fnv64(ASSEMBLY), data: s3saEncode(readFileSync(dll)) },
  { type: TYPE_XML, group: 0, instance: fnv64(`${ASSEMBLY}.Instantiator`), data: Buffer.from(tagXml(TUNING_XML), 'utf8') },
]);
const out = join(outDir, 'ts3fixes.package');
writeFileSync(out, pkg);
console.log(`built ${out} (${pkg.length} bytes)`);

// --on kA,kB: also writes build/ts3fixes-on-kA-kB.package, the same DLL with those tunings True (test queues that need a
// hook patched at startup, e.g. kVenueMemo, docs/122 §14.24.7, pick it with fixesPackage).
const onArg = process.argv.indexOf('--on');
if (onArg > 0) {
  const names = (process.argv[onArg + 1] ?? '').split(',').filter(Boolean);
  let xml = tagXml(TUNING_XML);
  for (const n of names) {
    const [key, value] = n.split('=');
    if (value !== undefined) { // any tuning, e.g. kAutUnseenPacePct=50 or kAutLive=False
      const line = new RegExp(`<${key} value="[^"]*" />`);
      if (!line.test(xml)) { console.error(`--on: ${key} is not in TUNING_XML`); process.exit(1); }
      xml = xml.replace(line, `<${key} value="${value}" />`);
      continue;
    }
    const off = `<${n} value="False" />`;
    if (!xml.includes(off)) { console.error(`--on: ${n} is not a False tuning in TUNING_XML`); process.exit(1); }
    xml = xml.replace(off, `<${n} value="True" />`);
  }
  const variant = writePackage([
    { type: TYPE_S3SA, group: 0, instance: fnv64(ASSEMBLY), data: s3saEncode(readFileSync(dll)) },
    { type: TYPE_XML, group: 0, instance: fnv64(`${ASSEMBLY}.Instantiator`), data: Buffer.from(xml, 'utf8') },
  ]);
  const vOut = join(outDir, `ts3fixes-on-${names.join('-')}.package`);
  writeFileSync(vOut, variant);
  console.log(`built ${vOut} (${variant.length} bytes, ${names.join(', ')})`);
}

// Test-only mod. MonoPatcher has no license, so its DLL never goes in the repo: take it from install-fixes.ts's unpacked download.
const MP_PKG = join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'share'), 'ts3-fixes', 'unpacked', 'monopatcher', 'Packages', 'ld_MonoPatcher.package');
if (!existsSync(MP_PKG)) {
  console.log(`skipped portalplan-test (test only): no ${MP_PKG}; run node tools/setup/install-fixes.ts install --only monopatcher first`);
} else {
  const refDir = join(outDir, 'refs');
  mkdirSync(refDir, { recursive: true });
  execFileSync(process.execPath, [join(ROOT, 'tools', 'extract-assemblies.ts'), refDir, MP_PKG], { stdio: ['ignore', 'ignore', 'inherit'] });
  const TEST = 'Ts3PortalPlanTest';
  const testDll = join(outDir, `${TEST}.dll`);
  compile(join(ROOT, 'mods', 'portalplan-test', 'src'), testDll, [join(refDir, 'MonoPatcher.dll')]);
  const testPkg = writePackage([{ type: TYPE_S3SA, group: 0, instance: fnv64(TEST), data: s3saEncode(readFileSync(testDll)) }]);
  const testOut = join(outDir, 'ts3-portalplan-test.package');
  writeFileSync(testOut, testPkg);
  console.log(`built ${testOut} (${testPkg.length} bytes, TEST ONLY)`);
}
