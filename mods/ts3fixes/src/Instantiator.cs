using System;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Script-mod entry point: the game parses the tuning resource whose instance is FNV64("Ts3Fixes.Instantiator"),
    // which runs this static constructor at startup, before any world loads (the patches must go in before the game
    // compiles the methods they replace). The other tunables are the player's settings (true = on); they are read when
    // a fix runs, so a changed tuning file takes effect at the next game start.
    public class Instantiator
    {
        [Tunable] protected static bool kInstantiator = false;
        [Tunable] public static bool kGoHomeShortcut = true;       // unseen townies arrive home without the long walk
        [Tunable] public static bool kBuildingTripShortcut = true; // ... at work, school and shops
        [Tunable] public static bool kLotTripShortcut = true;      // ... at parks, venues and other homes
        [Tunable] public static bool kBroadcasterThrottle = true;  // off-screen reaction broadcasters pulse less often
        [Tunable] public static bool kWalkStyleComparer = true;    // faster walk-style lookups (same result)
        [Tunable] public static bool kVirtualHomeShortcut = true;  // unseen homeless townies leave the world without the walk
        [Tunable] public static bool kNoRouteFailWalkOffscreen = true; // unseen townies skip the extra route-fail searches
        [Tunable] public static bool kStuckWatchdog = true;        // reset unseen townies stuck going home for 60 sim-minutes
        [Tunable] public static bool kStuckWatchdogIdle = true;    // ... also idle Sims on unroutable spots or in hidden rooms, a day in one spot, failing route plans (needs kStuckWatchdog)
        [Tunable] public static bool kSaveCleanup = false;       // opt-in: once a sim day drop weak NPC-NPC relationships, unlinked other-world MiniSims and empty life-event lists (docs/14)
        [Tunable] public static int kSnapshotCap = 0;            // opt-in: keep only the newest N memory pictures per active-household Sim, 0 = off (docs/14 7b)
        [Tunable] public static bool kSaveCleanupAggressive = false; // opt-in, changes save data: long multi-generation saves, on top of kSaveCleanup (spread pass, docs/14 §8)
        [Tunable] public static int kAggressiveSnapshotKeep = 10;  // ... newest memory pictures kept per Sim outside the active household
        [Tunable] public static float kAggressiveQuietDays = 56f;  // ... friend-or-weaker NPC pairs not talked for this many sim days are dropped (2 years of 4 default 7-day seasons)
        [Tunable] public static int kPlayedGenerations = 2;        // ... Sims within this many parent/child steps of a living member of a played household get the lighter tier
        [Tunable] public static int kPlayedSnapshotKeep = 40;      // ... newest memory pictures kept per Sim under the lighter tier
        [Tunable] public static bool kRouteFailureBackoff = true;  // unseen townies do not re-plan a route that just failed (30 sim-minutes)
        [Tunable] public static bool kNoStationDetourOffscreen = true; // unseen townies keep their route instead of a subway/hover train detour
        [Tunable] public static bool kPortalAdaptorLeak = true;    // ocean/houseboat portals no longer leave a dead route callback on the Sim
        [Tunable] public static bool kCasGrid = false;             // opt-in: CAS hair/makeup grids fill a few tiles per tick; hair preset data cached per CAS session (docs/21)
        [Tunable] public static bool kSpatialCache = false;        // opt-in: repeated "objects of type T on lot L" queries in one sim tick share one native lot walk (docs/20)

        [Tunable] public static bool kRoutableCache = false;       // opt-in until measured: the game's "is this spot safely routable" checks share the SafeSpot verdict cache
        [Tunable] public static bool kStuckCheckBusy = false;      // opt-in until measured: the 3 AM stuck check skips Sims held by a bed, chair or other object
        [Tunable] public static bool kStuckCheckAsync = false;     // opt-in until measured: the 3 AM stuck check runs in our own task, one Sim per tick, so [RouteAsync] FallbackAsync plans it on the worker without the world lot task sleeping in the alarm (docs/134 §8)
        [Tunable] public static bool kSafeSpotCap = false;         // opt-in until measured: a reset's safe-spot search near the front door checks at most 6 cells (EA's own vacation-start limit), docs/141 §7.1
        [Tunable] public static bool kFarthestLotSorted = false;   // opt-in until measured: GetFarthestLot with routing plans the farthest lot first (same answer), at most 3 plans, sim-day cache, docs/141 §7.2
        [Tunable] public static bool kIndoorPositionCap = false;   // opt-in until measured: Lot.GetRoutableIndoorPositions gives a room up after 4 failed plans, docs/141 §7.3
        [Tunable] public static bool kFastRefTable = true;         // on since v0.1.2 (save matrix 27/28 PASS, saves -14..-24%): the save serializer's object identity table no longer degrades past 16k objects (RefTable.cs)
        [Tunable] public static bool kCachedReflection = false;    // opt-in until measured: the save serializer answers repeated per-type questions from a per-save cache (CachedReflection.cs)
        [Tunable] public static bool kMotiveBarImageCache = true;  // on since v0.1.2 (BP A/B PASS, UI only): motive bars keep their moodlet images while the moodlet is unchanged (UiCaches.cs)
        [Tunable] public static bool kBuffGridInPlace = true;      // on since v0.1.2 (BP A/B PASS, UI only): the moodlet grid updates its cells in place unless moodlets were added, removed or changed icon (UiCaches.cs)
        [Tunable] public static bool kSimNameCache = true;         // on since v0.1.2 (BP A/B PASS, UI only): Sim first/last/full names cached by their stored strings (NameCache.cs)
        [Tunable] public static bool kDetailPriorityCache = false; // opt-in until measured: lots learn whether an active-household Sim stands on them from one lot set per sim tick (ScriptHotSpots.cs, docs/74)
        [Tunable] public static bool kCaneEarlyOut = false;        // opt-in until measured: route walk styles skip the inventory search for a cane when the style can never need one (same answer; ScriptHotSpots.cs, docs/74)
        [Tunable] public static bool kTooltipThrottle = false;     // opt-in until measured: with the mouse at rest the tooltip task looks for the window under the cursor at most every 100 ms (ScriptHotSpots.cs, docs/74)
        [Tunable] public static bool kSafeSpotReuse = false;       // opt-in until measured: SafeSpot reuses the FindGoodLocation result stored with a verified spot instead of searching again (docs/95 5.1)
        [Tunable] public static bool kWildflowerProbe = false;     // test only: the spring wildflower drain logs tries and times per fire, same behaviour (ScriptHotSpots.cs, docs/110)
        [Tunable] public static bool kWildflowerTask = true;       // on (docs/247 §11.2, 923 ms on BP): spring wildflower placement runs one try per sim tick in its own task instead of all tries inside the alarm (docs/110 W1)
        [Tunable] public static bool kDoorMirror = false;          // opt-in: per-lot managed door records, pushed to the ts3native door mirror at each plan (DoorMirror.cs, docs/105 §10.5)
        [Tunable] public static bool kAutShadow = false;          // test only: [AutShadow] pilot, meta autonomy scored again in shadow on ts3native's worker and compared (AutShadow.cs, docs/122)
        [Tunable] public static bool kReplanDefer = false;         // opt-in: door/elevator route replans go to the Sim's own route loop instead of the callback (ReplanDefer.cs, docs/141 §6)
        [Tunable] public static bool kReplanSpread = false;        // opt-in: build/buy exit and replan-all spread over sim ticks, 4 routing Sims a tick (ReplanDefer.cs, docs/141 §6)
        [Tunable] public static bool kOverwatchSpread = false;     // opt-in until measured: NRaas Overwatch's nightly cleanups run one type at a time, 10 sim-minutes apart, its inventory cleanup as our sliced pass; off by itself with a spreading Overwatch build (OverwatchSpread.cs, docs/145)
        [Tunable] public static bool kOverwatchChain = false;      // opt-in until measured: NRaas Overwatch's nightly cleanups one sim tick apart in its own order and task, its inventory cleanup as our sliced pass; overwatchspread and a spreading Overwatch build win (OverwatchSpread.cs, docs/145 §10)
        [Tunable] public static bool kErrorTrapSpread = false;     // opt-in until measured: NRaas ErrorTrap's load-up inventory check skips the name lookup for objects without an inventory and spreads over ticks; off by itself with a spreading ErrorTrap build (ErrorTrapSpread.cs, docs/146)
        [Tunable] public static bool kVenueMemo = false;           // opt-in until measured: meta venue score with GetTuning kept per world and the rain/hail list scanned without boxing (VenueMemo.cs, docs/122 §14.24)
        [Tunable] public static bool kAutLive = true;              // patch B14 (Autonomy.ScoreInteractionsForMetaObject) once the tuning is set (Fixes.ApplyTuned) for the [AutShadow] Mode=3 go-live consumer: one pass-through frame per meta B14, push and pick; the runtime switch is autlive, [AutShadow] Unseen=1 for players (AutLive.cs, docs/124 §17)
        [Tunable] public static bool kAutLiveGuard = true;         // go-live crowd cost guard (docs/124 §17.10): with autlive acting, skip posting for a while when a window's sim-side cost tops the scoring it saved; runtime switch autliveguard (A/B off arm)
        [Tunable] public static int kAutLiveGuardOffPm = 1000;     // ... skip when cost > this per mille of the scoring saved
        [Tunable] public static int kAutLiveGuardOnPm = 800;       // ... the window after a skip must come in at or under this (hysteresis)
        [Tunable] public static int kAutLiveGuardMinSearches = 40; // ... a window holds at least this many eligible searches
        [Tunable] public static int kAutLiveGuardMinFrames = 300;  // ... and at least this many frames
        [Tunable] public static bool kAutLiveLate = false;         // docs/217 L1: go-live keeps consuming after B13 yields (resume checks re-run per slice, ObjStale per object); runtime switch autlivelate
        [Tunable] public static int kAutLiveLateTicks = 3;         // ... a later slice consumes only up to this many ticks after the resume
        [Tunable] public static int kAutLiveGuardHold = 600;       // ... frames without posting after a skip window, doubled per skip in a row (16 x max)
        [Tunable] public static bool kAutUnseenPace = true;        // docs/222 §8: with autunseen on, charge the skipped ads' vanilla B14 cost to B13's yield timer (searches yield on vanilla's schedule); runtime switch autunseenpace
        [Tunable] public static bool kAutUnseenCmp = false;        // docs/222 §9: Dry measurement, per definition the worker's Test/score vs the game's C2 for unseen actors on every slice; runtime switch autunseencmp (measurement only)
        [Tunable] public static bool kAutUnseenRabbitHoleGame = true; // docs/222 §10: with autunseen on, RabbitHole-target pairs go to the game's own C1 (worker-pass/game-fail 7.2% of unseen pairs, mostly rabbit holes); runtime switch autunseenrh
        [Tunable] public static int kAutUnseenPacePct = 100;        // docs/222 §10: pacing charges this percent of the measured vanilla B14 cost per skipped ad (100 overshot alone: BP unseen searches/Sim-hour 0.541 vs 0.429); 100 with autunseenpacecredit = gate 2 credit arm, ON in release (Rodney 2026-11-08)
        [Tunable] public static bool kHoursMemo = false;           // opt-in until measured: a lot's opening hours are worked out once per sim tick (ScriptHotSpots.cs, docs/74)
        [Tunable] public static bool kUnseenEnvThrottle = false;   // opt-in until measured: unseen high-LOD Sims score their surroundings one time in kUnseenThrottleEvery (UnseenThrottle.cs, docs/144)
        [Tunable] public static bool kUnseenMotiveThrottle = false; // opt-in until measured: unseen high-LOD Sims update needs, moodlets and mood every kUnseenThrottleEvery sim-minutes, same totals (docs/144)
        [Tunable] public static bool kUnseenBroadcastThrottle = false; // opt-in until measured: unseen reaction broadcasters pulse one time in kUnseenBroadcastEvery each, instead of the shared 1 in 4 (docs/144)
        [Tunable] public static bool kUnseenPark = false;          // opt-in until measured: an idle unseen low-LOD Sim waits kUnseenParkMinutes after its last autonomy search before the next (docs/243 D1)
        [Tunable] public static int kUnseenParkStaggerTicks = 16;  // kUnseenPark: a held Sim that became seen passes in its own slot of this many ticks (slots of 4); 1 = no stagger (docs/243 §9)
        [Tunable] public static int kUnseenParkMinutes = 30;      // wait for kUnseenPark in sim-minutes; 0 = off, capped at 240
        [Tunable] public static int kUnseenThrottleEvery = 4;      // N for the two Sim throttles above
        [Tunable] public static int kUnseenBroadcastEvery = 16;    // N for the broadcaster throttle (kBroadcasterThrottle is already 1 in 4)
        [Tunable] public static bool kEaSpreadLots = true;        // on since v0.2.0 (repro-verified, docs/RELEASE.md): EA's 3 AM venue cleanup does a few lots per sim tick in its own task, same steps (EaSpread.cs, docs/147)
        [Tunable] public static bool kEaSpreadPets = true;        // on since v0.2.0 (repro-verified, docs/RELEASE.md): the pet pool alarm builds its pet in a task, so EA's own sleeps between CAS steps take effect (docs/147)
        [Tunable] public static bool kEaSpreadDecay = true;       // on since v0.2.0 (repro-verified, docs/RELEASE.md): midnight relationship decay, a slice of Sims per sim tick, one step per pair per day (docs/147)
        [Tunable] public static bool kUnseenCarThrottle = false;   // opt-in until measured: an unseen traffic car (LiteDriver) bound for an unseen spot stays parked on kUnseenCarEvery-1 of kUnseenCarEvery rounds instead of planning a drive (UnseenThrottle.cs, docs/143 §8.3)
        [Tunable] public static bool kHitchCauseHooks = false;   // test builds: patch the docs/247 §9 hitch-cause stamps (alarmtime / boatreplanlog / owresettime switches), HitchCause.cs
        [Tunable] public static bool kWakeHooks = false;   // test builds: patch the docs/211 wake-up hooks (wakeprobe / wakepark / wakepoll switches) at startup; they add a call on every SimUpdate pass (WakePark.cs)
        [Tunable] public static bool kWakePark = false;    // opt-in until measured: an unseen low-LOD Sim's SimUpdate task sleeps kWakeParkTicks ticks between passes instead of 1 (WakePark.cs, docs/211)
        [Tunable] public static bool kWakePoll = false;    // opt-in until measured: an unseen Sim's exit-reason wait sleeps up to kWakePollTicks per poll, capped at its deadline, woken by AddExitReason (WakePark.cs, docs/211)
        [Tunable] public static int kWakeParkTicks = 12;   // park length (ticks) for kWakePark; also how long an unseen verdict is cached
        [Tunable] public static int kWakePollTicks = 30;   // longest parked poll (ticks) for kWakePoll; the AddExitReason wake ends it earlier
        [Tunable] public static int kUnseenCarEvery = 4;           // N for the traffic car rule
        [Tunable] public static bool kRouteSources = false;        // test only: route plans counted by source (docs/151 §2 buckets), repeats, leg-end and bouncer shares; no behaviour change (RouteSources.cs)
        [Tunable] public static bool kLegReplanSkip = false;       // opt-in until measured: no replan after a boat/vehicle get-out when the planned rest of the route starts within 0.5 m of the Sim (RouteSources.cs, docs/151 D1)
        [Tunable] public static bool kBouncerSlotSkip = false;     // opt-in until measured: a velvet-rope bouncer standing on its spot does not re-route to it every 5 s (RouteSources.cs, docs/151 D3)
        [Tunable] public static bool kStuckCheckNear = false;      // opt-in until measured: the 3 AM stuck check plans its random lot points nearest first (same points, same verdict), so fewer failed plans before the first success (FewerPlans.cs, docs/163 §3)
        [Tunable] public static bool kJobYield = false;            // opt-in until measured: a route plan that would wait natively for a running route job sleeps its task a tick at a time instead (needs ts3native [RouteAsync] JobYield; RouteBackoff.cs StepAside, docs/183)
        [Tunable] public static bool kDriverTaskAsync = false;     // opt-in until measured: a traffic car's plan in its LiteDriver task and a ridden vehicle's plan in its rider's task go async as own-task plans (RouteBackoff.cs OwnerTaskStart, docs/163 §7)
        [Tunable] public static bool kOverwatchStuckSlice = false; // opt-in until measured: NRaas Overwatch's Stuck Check takes a sim tick inside its walk over every Sim once 8 ms have passed since its last tick (OverwatchStuck.cs, docs/145 §12)
        // docs/250 §7.1 A1: StuckWatchdog.CheckIdle's route check as an own-task route job, verdict re-validated before acting.
        // OFF (rc1110b, 2026-11-09) until the wdasync-fix-1110 tests: the A1 path never sent a job (SpotVerdict -> PlanFromPoint ->
        // AsyncKindStart, no own-task START) and H4Core.WdRoute returned A1 before the lock check (docs/247 §12.4); both fixed 2026-11-10.
        [Tunable] public static bool kWatchdogAsync = false;
        [Tunable] public static bool kStuckCheckOwn = false;      // opt-in until measured: with stuckcheckasync, the stuck check's plans in our drain task go async as own-task plans (RouteBackoff.cs OwnerTaskStart, docs/163 §7)
        [Tunable] public static bool kFailRepeat = false;         // opt-in until measured: a Sim's plan that repeats its own no-path failure (same goal, same spot) within 500 ms returns that failure without searching again (RouteBackoff.cs, docs/163 §4)
        [Tunable] public static bool kDisposeSpread = false;       // opt-in until measured: Sims leaving the world, service Sims and traffic cars at despawn are destroyed one per sim tick by our task instead of in one tick (DisposeSpread.cs, docs/74 §12)
        [Tunable] public static bool kIdlePlanMemo = false;        // opt-in until measured: the idle watchdog skips Sims that planned a route from their spot since they went still, at most 4 checks a pass (RouteFixes.cs, docs/151 D5)
        [Tunable] public static bool kSeasonMarkerCache = false;   // opt-in until measured: Queries.GetGlobalObjects(typeof(SeasonalLotMarker)) (every world-lot object Dispose) answered from a live marker list, 1 in 8 checked against the game (SeasonMarkerCache.cs, docs/74 §11 (a))
        [Tunable] public static bool kOverwatchStuckNoticeSummary = false; // opt-in until tried: NRaas Overwatch's per-Sim stuck-reset notices held, one summary per 2 sim-hours (OverwatchNotice.cs, docs/145 §13)
        [Tunable] public static bool kOverwatchIgnoreStuckProbes = true; // NRaas Overwatch's Stuck Check V2 does not count the game's own stuck-check probe plans as the Sim's route failures (no double resets; OverwatchProbe.cs, docs/163 §9)
        [Tunable] public static bool kSafeSpotYield = false;       // opt-in until measured: SafeSpot sleeps one tick after each FindGoodLocation in the Sim's own task (Fixes.cs SafeSpotYield, docs/74 §11 (b))
        [Tunable] public static bool kStuckCheckCap = false;      // opt-in until verified: with stuckcheckown, the stuck check's probe jobs of rounds 1-2 are cut at ts3native's PlanDeadlineMs like a foreign sync plan (FewerPlans.cs, RouteBackoff.cs OwnerTaskStart, docs/163 §10)
        [Tunable] public static bool kUnseenFallbackStuck = false;    // opt-in until verified: the 3 AM stuck check plans nothing for a Sim the player cannot see (live mode) and re-arms itself (UnseenFallback.cs, docs/202 §3.3)
        [Tunable] public static bool kUnseenFallbackPlan = false;     // opt-in until verified: route plans of unseen Sims are cut at ts3native's [UnseenFallback] DeadlineMs (docs/202 §3.1)
        [Tunable] public static bool kUnseenFallbackSites = false;    // opt-in until verified: pushes the unseen Sims' ids to ts3native's [UnseenFallback] Sites=1 (C1-C4 short lock waits, docs/202 §3.4)
        [Tunable] public static bool kUnseenFallbackTeleport = false; // opt-in until verified: a cut own plan of an unseen townie places it at its route goal (docs/202 §3.2)
        [Tunable] public static bool kUnseenFallbackArrive = false;   // opt-in until verified: an unseen townie walking to a spot in view skips the hidden part of the walk (hold, placed at a hand-off point out of view; docs/202 §3.6)
        [Tunable] public static bool kUnseenFallbackLeave = false;    // opt-in until verified: a townie leaving the view walks to a hand-off point out of view, then the rest is held and skipped (docs/202 §3.6)
        [Tunable] public static bool kUnseenFallbackTrip = false;     // opt-in until verified: lot trips the trip shortcuts miss (mod subclasses of the community visit) get the shortcut after D's walk-time hold (docs/202 §3.6.4)
        [Tunable] public static bool kUnseenFallbackTripHold = false; // opt-in until verified: the trip shortcuts (go home, visits, lot trips, buildings) hold for the walk time before the move (docs/202 §3.6.4)
        [Tunable] public static bool kGoalHouseholdExempt = true;  // on since 11-07 (sv-goal-household-exempt PASS): an unseen NPC whose route works for the household is not cut or shortcut (docs/197 §7)
        [Tunable] public static bool kUnseenFallbackLoopDetect = false;  // opt-in, logging only: names Sims that restart the same interaction on the same target after failed plans (docs/202 §5.7)
        [Tunable] public static bool kUnseenFallbackLoopBreak = false;   // opt-in until verified: an unseen looping Sim gets motives/moodlets reset and, on an unseen lot the player does not own, the lot tidied over several ticks (docs/202 §5.7)
        [Tunable] public static bool kUnseenFallbackFinish = false;   // opt-in until verified: an unseen walker that would wait for a busy route lock is held for its remaining walk time, then placed at its route end and the route ends as succeeded (needs ts3native [UnseenFallback] FinishWalk=1 and unseenfbsites; docs/202 §3.4.2)
        [Tunable] public static bool kUnseenRouteRelaxed = false;     // opt-in until verified: unseen townies skip route plans whenever exactness is the only reason (D without verdict/colour/lot-kind proofs, fast-leg travel time, snap-back; docs/202 §3.9)
        [Tunable] public static bool kUnseenBoatStart = true;          // opt-in until verified, needs kUnseenBoat: an unseen townie starting on a houseboat leaves it the same way, no boat (docs/248 §10)
        [Tunable] public static bool kUnseenBoat = true;               // opt-in until verified: an unseen lone townie crossing the water skips the boat: waits the travel time, placed at the goal or on its houseboat (docs/248)
        [Tunable] public static bool kPosturePopCycle = false;   // opt-in until verified: a posture pop that EA would turn into a two-posture loop (Compatible branch) runs the setter's unwind instead (issue #89, PosturePop.cs)
        [Tunable] public static bool kUnseenFallbackPre = false;      // opt-in until verified: an unseen townie's own walk to an unseen lot goal is held for the walk time, then placed at the goal (no search; docs/202 §3.5)
        [Tunable] public static bool kPrePlanCallback = false;     // opt-in until measured: Route.AboutToPlanCallback runs before an async START, the job's locked-door list survives the original plan's second callback (RouteBackoff.cs PreCb, docs/185 §5)
        [Tunable] public static bool kSeasonImposter = false;      // opt-in until measured: lot imposter rebuilds after a season change queued, one per 2 s for lots near the player, the rest at the next save (SeasonImposter.cs, issue #82)
        [Tunable] public static bool kSeasonImposterCold = false;  // with kSeasonImposter: lots away from the player rebuilt in play too, one per 2 s, nearest first (else at the next save)
        [Tunable] public static bool kPreferredService = false;    // opt-in, changes save data: service 'regular Sim' entries whose Sim left the pool dropped and re-picked (EaFixes.cs, issue #86)
        [Tunable] public static bool kSocialWorkerOnLot = false;   // opt-in until verified: a social worker already on the lot starts the visit 1 sim-minute later from a yielding alarm, not inside a constructor (EaFixes.cs, issue #87)
        [Tunable] public static bool kEaNullGuards = false;        // opt-in until verified: four EA null guards (bribe table, AddToSim(null), householdless pet motives, EnterWhileHolding(null)) (EaFixes.cs, issue #88)
        [Tunable] public static bool kShowStageThrowables = false; // opt-in, changes save data: show stage throwable items made only to read a cost are destroyed (EaFixes.cs, issue #90)
        [Tunable] public static bool kFirePitChair = false;        // opt-in, changes save data: fire pit test chairs destroyed before a save (EaFixes.cs, issue #90)
        [Tunable] public static bool kAgeUpKeepOutfits = true;    // on by default (2026-11-08): a failed or cancelled age-up builder keeps the Sim's outfits instead of wiping them (EaFixes.cs, issue #100)
        [Tunable] public static bool kAgeUpSwap = false;           // opt-in until verified: an age-up outfit swap EA refuses goes to Everyday or is released, so the age-up task finishes (AgeUpSwap.cs, docs/503)
        [Tunable] public static bool kOutfitWaitPoll = true;      // on by default: an outfit wait not ready after 60 sim-minutes reads its flag once per sim-minute instead of every tick (OutfitWait.cs, issue #115)
        [Tunable] public static bool kConvTurn = false;           // opt-in until verified: a local Sim takes a conversation turn that its holder cannot or does not take (off screen, gone, busy, silent 15 sim minutes) (ConvTurn.cs, issue #166 build 19, docs/509)
        [Tunable] public static bool kUniformBackup = true;       // on by default (2026-11-08): the shared career uniform backup is cleared once uncached, so a later re-uniform cannot uncache the new Career outfit (UniformBackup.cs, issue #101)
        [Tunable] public static bool kDeadRefGuards = false;       // opt-in until verified: Relationship.Get makes no entry for a disposed description, dead aging states skipped (DeadRefs.cs, issue #91)
        [Tunable] public static bool kNullService = true;          // on since v0.2.5 (Rodney 2026-11-04; bp-rel025c-nullservice PASS + contributor LR PASS 0d23cf37): Services.AllServices drops the null elements a save can hold, so EA's loops over it do not throw (NullService.cs, docs/504)
        [Tunable] public static bool kRabbitHoleDoor = true;       // on since v0.2.5 (Rodney 2026-11-03): a rabbit hole door the animation cannot open or close no longer ends the Sim's entry or exit (RabbitHoleDoor.cs, docs/505)
        [Tunable] public static bool kPhotoSession = true;       // on by default: a greeting-card photo session drops expected Sims that are not at it, so the booth is not left in use (PhotoSession.cs, docs/508)
        [Tunable] public static bool kEggSpot = true;            // on by default: the egg hunt places its eggs only on the hunt lot (EggSpot.cs, docs/508)
        [Tunable] public static bool kGraduationTable = true;    // on by default: a graduation waiting loop with no graduation table ends instead of throwing (GraduationTable.cs, docs/508)
        [Tunable] public static bool kJazzParam = true;           // on by default (2026-11-08): a jazz parameter for a Sim with no posture no longer throws out of a music buff's add or remove (JazzParam.cs, docs/511)
        [Tunable] public static bool kBuffUpdate = true;          // on by default (2026-11-08): a buff that adds or removes a buff during the buff update no longer ends the Sim's update task (BuffUpdate.cs, docs/510)
        [Tunable] public static bool kCarNoRoute = true;          // on by default (2026-11-08): a car with no car routing component no longer throws when it drives to a lot; the drive fails as a failed plan does (CarNoRoute.cs, docs/513)
        [Tunable] public static bool kNpcDriverPool = true;       // on by default (2026-11-08): a taxi, limo, bus or fire-truck driver is never built from a pooled description that is no longer a live NPC (NpcDriverPool.cs, docs/514)
        [Tunable] public static bool kBookCarry = true;            // opt-in until verified: the carry state machine Book.StopUsingBook drops without an exit is ended and its engine driver released (BookCarry.cs, docs/515; not the cause of the stuck arm pose)
        [Tunable] public static bool kAgeUpAbort = true;          // on by default (2026-11-08): a Sim whose deferred age-up build is aborted while it is instantiated is still created (AgeUpAbort.cs, docs/512)
        [Tunable] public static bool kRewardGiver = true;        // on by default (2026-11-08): opportunity reward objects sent by a named Sim get their owner component instead of EA's NullReferenceException (RewardGiver.cs, docs/525)
        [Tunable] public static bool kDatingProfile = true;      // on by default (repro docs/528): only a Sim who made an online dating profile has one; the sim thread's DidSimsMeetOnline makes no empty record (DatingProfile.cs, docs/528)
        [Tunable] public static bool kRelTablesApart = true;     // on by default (2026-11-08): a relationship whose two tables came apart is rejoined instead of EA's "same key" ArgumentException (RelTablesApart.cs, docs/535)
        [Tunable] public static bool kPetPoolOrphan = true;      // on by default (2026-11-08): a pet the adoption pools drop with no family links is disposed instead of staying alive with no household (PetPoolOrphan.cs, docs/534)
        [Tunable] public static bool kErrorTrapSims = false;       // opt-in until measured: dead Sim objects dropped hourly from NRaas ErrorTrap's add-only CheckSim.mInited, which keeps each one alive (ErrorTrapSims.cs)
        [Tunable] public static bool kCelebrityDeadSim = false;    // opt-in until measured: EA CelebrityManager listeners that still hold a destroyed Sim unregistered hourly (DeadSimHolders.cs)
        [Tunable] public static bool kSeasonsDeadSim = false;      // opt-in until measured: EA Motives season-change listeners of gone Sims / replaced Motives removed hourly (DeadSimHolders.cs)
        [Tunable] public static bool kGhostReactDeadSim = false;   // opt-in until measured: destroyed Sims removed from live ghosts' SimsReactedToGhost hourly (DeadSimHolders.cs)
        [Tunable] public static bool kCasUiHandlers = false;       // opt-in until measured: closed CAS windows' never-removed event handlers dropped hourly, CASController duplicates collapsed (CasUiHandlers.cs)
        [Tunable] public static bool kErrorTrapLookup = false;     // opt-in until measured: NRaas ErrorTrap's ObjectLookup.sLookup cache emptied hourly (its Clear() forgets it; ErrorTrapSims.cs)
        [Tunable] public static bool kErrorTrapCtl = false;        // opt-in until measured: NRaas ErrorTrap's dereference controllers' item lists of the last world-load scan dropped hourly once the scan is over (ErrorTrapSims.cs)
        [Tunable] public static bool kDreamNodeDeadSim = false;    // opt-in until measured: EA dream nodes' owner-less listeners of gone Sim objects removed hourly (DeadSimHolders.cs)
        [Tunable] public static bool kOverwatchDeadSim = false;    // opt-in until measured: NRaas Overwatch stuck-check entries of gone Sim objects removed hourly (DeadSimHolders.cs)
        [Tunable] public static bool kSimRefsDeadSim = false;     // opt-in until measured: live Sims' last talking-to, mid-route pusher and celebrity reactor references to gone Sim objects dropped hourly (SimRefsDeadSim.cs)
        [Tunable] public static bool kOutfitWaitSaved = false;    // opt-in until measured: MagicalMakeover alarm tasks resumed from the save, actor gone, destroyed hourly (OutfitWaitSaved.cs)
        [Tunable] public static bool kDeadSimCount = false;       // diagnostic, read-only: counts destroyed Sim objects still in memory through weak references, hourly (DeadSimCount.cs)
        [Tunable] public static bool kDeadSimHollow = false;      // test: clears the own reference fields of destroyed Sims still held 6+ sim hours by something unknown (DeadSimCount.cs)
        [Tunable] public static bool kFireworksFizzle = true;    // on by default (2026-11-08): a fireworks watcher finishes on a fizzle instead of EA's SacsTimeoutException and reset (FireworksFizzle.cs, docs/533)
        [Tunable] public static bool kBookHand = false;           // opt-in until verified: a book Book.StopUsingBook left in the hand with no carry machine goes through EA's own PutBookInInventoryHandler at the put-down that would throw (BookHand.cs, docs/251)
        [Tunable] public static bool kBookHandDiag = false;       // opt-in: logs a book still in the hand with no carry machine 10 s after StopUsingBook (BookHand.cs, docs/251)
        [Tunable] public static bool kMetricSkillMissing = true; // on by default (2026-11-08): a career skill metric whose skill is not registered gives an empty name and a neutral value instead of EA's NullReferenceException (MetricSkillMissing.cs, docs/532)
        [Tunable] public static bool kInvNotYet = true;         // on by default (2026-11-08): a Sim with no inventory yet (mid-creation) refuses the object instead of EA's NullReferenceException (InvNotYet.cs, docs/531)
        [Tunable] public static bool kLeaveJobDispose = true;    // on by default (repro docs/529): a Sim deleted at work leaves its job at once (LeaveJobDispose.cs, docs/529)
        [Tunable] public static bool kAsyncJigSync = true;        // on: restores vanilla order inside RouteAsync: no async plan while the Sim's interaction (or its linked one) holds a SocialJig not yet in the world (paired socials, AsyncJigSync.cs, docs/240)
        [Tunable] public static bool kBakeSaleSeller = true;      // on by default: a bake sale table's deleted seller reads as no seller, an orphaned NPC table gets its clean-up alarm back (BakeSale.cs, issue #102)
        [Tunable] public static bool kDeadRefSweep = false;        // opt-in, changes save data: EA holders of disposed descriptions / destroyed Sims cleaned, spread hourly + cheap pre-save pass (DeadRefs.cs, issue #91)
        [Tunable] public static bool kVacExport = false;           // opt-in until verified: free vacation adults keep dreams, opportunities, travel moodlets (EaVacation.cs, issue #97 row 1)
        [Tunable] public static bool kVacStoreGuard = false;       // opt-in until verified: opportunity travel store guarded + stuck trips repaired at load (EaVacation.cs, issue #97 row 2)
        [Tunable] public static bool kVacReturnSkip = false;       // opt-in until verified: dead / moved-out adults not re-created; a failed return ends the trip (EaVacation.cs, issue #97 row 3)
        [Tunable] public static bool kVacTeenDelete = false;       // opt-in until verified: a deleted teen's Sim no longer runs the return inside its Dispose (EaVacation.cs, issue #97 row 4)
        [Tunable] public static bool kVacExitReturn = false;       // opt-in until verified: a trip exit while the parents are away brings them back (EaVacation.cs, issue #97 row 5)
        [Tunable] public static bool kVacListenerNull = false;     // opt-in until verified: RestoreState(null) starts the listener fresh (EaVacation.cs, issue #97 row 6)
        [Tunable] public static bool kVacImportFix = false;        // opt-in until verified: travel opportunity import refreshes stale Sim ids, no instant fail without a time limit (EaVacation.cs, issue #97 row 7)
        [Tunable] public static bool kMenuMaven = false;           // opt-in: the Menu Maven challenge and the Culinary Librarian wish need every recipe of this install that a Sim can learn, counted live (MenuMaven.cs, docs/501)
        [Tunable] public static bool kBoatParkGone = true;        // EA bug fix, on (shipped without repro: Rodney 11-11, static review): BoatOwnable.Park on a destroyed boat returns instead of throwing (BoatParkGone.cs, docs/257)
        [Tunable] public static bool kDanceFloorListGuard = true; // ON in release (Rodney 2026-11-09): DanceFloor.FindAndRouteOntoADanceFloorWithFollower no longer throws "Collection was modified" (DanceFloorDiag.cs, docs/253 §11)

        public const string Version = "0.1.1";

        // True once the tuning loader has written the fields (every ts3fixes tuning sets kInstantiator True). The static
        // constructor runs before that, so Fixes.Apply sees C# defaults only (docs/124 §17.8): tuning-gated patches go in
        // Fixes.ApplyTuned, at app startup when the tuning is already set, else at the first world load.
        internal static bool TuningSet { get { return kInstantiator; } }

        static Instantiator()
        {
            try { Fixes.Apply(); } catch (Exception) { } // never take the game down at startup
            World.OnStartupAppEventHandler += delegate { try { if (kInstantiator) Fixes.ApplyTuned("app startup"); } catch (Exception) { } };
            World.OnWorldQuitEventHandler += delegate { try { Fixes.DiagWorldQuit(); } catch (Exception) { } try { Fixes.HeldRefsWorldQuit(); } catch (Exception) { } };
            World.OnWorldLoadFinishedEventHandler += delegate { try { Fixes.ApplyTuned("first world load"); } catch (Exception) { } try { Fixes.HcApplyOverwatch(); } catch (Exception) { } try { Fixes.PhotoSessionLoadSweep(); } catch (Exception) { } try { Fixes.StartWatchdog(); } catch (Exception) { } try { Fixes.StartPlayTrace(); } catch (Exception) { } try { Fixes.StartViewerPush(); } catch (Exception) { } try { Fixes.StartUfSitesPush(); } catch (Exception) { } try { Fixes.StartUrEditWatch(); } catch (Exception) { } try { Fixes.StartSaveCleanup(); } catch (Exception) { } try { Fixes.StartDeadRefSweep(); } catch (Exception) { }try { Fixes.ApplyOverwatchStuck(true); } catch (Exception) { } try { Fixes.ApplyTripSubclasses(); } catch (Exception) { } try { Fixes.ApplyOverwatchSpread(true); Fixes.OverwatchSpreadWorldLoad(); } catch (Exception) { } try { Fixes.ApplyOverwatchNotice(true); Fixes.OverwatchNoticeWorldLoad(); } catch (Exception) { } try { Fixes.ApplyOverwatchProbe(true); } catch (Exception) { } try { Fixes.ApplyErrorTrapSpread(true); Fixes.ErrorTrapSpreadWorldLoad(); } catch (Exception) { } Fixes.ClearNameCache(); Fixes.AutShadowWorldLoad(); Fixes.SeasonMarkerWorldLoad(); try { Fixes.DiagWorldLoad(); } catch (Exception) { } try { Fixes.MemSusWorldLoad(); } catch (Exception) { } try { Fixes.CompactionWorldLoad(); } catch (Exception) { } try { Fixes.WakeParkWorldLoad(); } catch (Exception) { } try { Fixes.VacationWorldLoad(); } catch (Exception) { } try { Fixes.StartErrorTrapSims(); } catch (Exception) { } };
        }
    }
}
