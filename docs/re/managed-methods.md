# Managed (C#) methods that matter

The game's script layer runs in the embedded Mono 1.2 **interpreter** (no JIT) on the one sim thread ([../04-re-findings.md](../04-re-findings.md) F6). This page lists the C# classes and methods that decide stutter, stuck Sims, crashes and save growth on the Steam 1.67 build, what each does (in our own words), where the time goes, and which of our fixes touches it.

Rules for this page: names, signatures and behaviour only. No EA code, no third-party mod code. To read the bodies, decompile your own copy ([README.md](README.md)).

**Confidence tags:** [verified] read in the decompiled scripts or the bytes; [measured] from our profiles (managed sampler at 5 Hz, `/taskstall`, stutter profiler); [inferred] reasoned, not yet confirmed.

**Percentages** are shares of managed (C#) samples on the sim thread, inclusive unless marked "self", from the 10-minute per-world profiles in [../04-re-findings.md](../04-re-findings.md) F7/F7b (Bridgeport = BP, Sunset Valley = SV, Isla Paradiso = IP), before our fixes unless noted.

**Native column:** the address of the internal call a `[DllImport("Sims3Common.dll")]` stub reaches, from the `{fn, name}` binding table in TS3W.exe (`tools/internal-calls-map.ts`). See [native-symbols.md](native-symbols.md).

**Fix column:** ts3fixes file, switch (test-agent name) and tuning (`Ts3Fixes.Instantiator.k*`), counters (index into `ts3fixes.counters`). Full table: [ts3fixes method replacements](#ts3fixes-method-replacements).

---

## 1. Scheduler, tasks and the Simulator

Script objects are not OS threads. Each long-lived object is a cooperative microthread that loops and calls `Simulator.Sleep`; the native host resumes them from four scheduler stages inside `MonoScriptHost::Simulate` (`0xD81840`): ScriptTick, TaskObjects, OneShotObjects, ThreadedObjects ([../04-re-findings.md](../04-re-findings.md) F17). A task that does not yield holds the whole sim-loop iteration.

| Method | What it does | Time / evidence | Fix | Native |
|---|---|---|---|---|
| `ScriptCore.Simulator.AddObject` | Wraps any `IScriptLogic` in a `ScriptProxy` and registers it with the native host; raw `ITask`s are passed directly [verified] | – | none | `Simulator_AddObjectImpl` `0x76CC70` |
| `ScriptCore.ScriptProxy.Simulate()` | Runs one object's own `Simulate()`; catches `ResetException` (calls `OnReset`); other exceptions are swallowed in retail [verified] | 66.8% incl. (BP), the wrapper of nearly all task time [measured] | none | – |
| `Sims3.SimIFace.Simulator.Sleep(uint)` | Checks there is a current task (`CheckYieldingContext`), then sleeps that many ticks; `0` = rest of this tick, `uint.MaxValue` = until `Wake` [verified] | – | used by our hooks to spread work (only when `MayYield()`, see §2) | `Simulator_SleepImpl` `0x76CB00` |
| `Sims3.SimIFace.Simulator.CheckYieldingContext(bool)` | True when there is a current task. Not enough to prove a yield is safe: a yield inside a native-to-managed call made during the task aborts the game (`interp.c:3595 pState->yieldReturn`) [verified, ../18-route-search-off-thread.md "Crash: yield in a nested call"] | – | ts3fixes `RouteBackoff.cs` `MayYield()` asks ts3native (GetOption2 bit 58\|61) | – |
| `Sims3.SimIFace.Simulator.Wake` | Resumes a task parked with `Sleep(uint.MaxValue)` [verified] | – | none | `Simulator_WakeImpl` `0x76CB30` |
| `Sims3.SimIFace.Simulator.DestroyObject(ObjectGuid)` | Destroys a script object | – | `SpatialCache.cs` hooks it to clear the lot query cache (counter 64) | `Simulator_DestroyObjectImpl` `0x76D120` |
| `Simulator.SetYieldingDisabled` / `EnableYieldAnalysis` | Debug switches for yield analysis [verified] | – | none | `Simulator_SetYieldingDisabledImpl` `0x76CD30` |
| `Sims3.Gameplay.Utilities.AlarmManager.Simulate()` | One manager per lot (`AlarmManager.Global` = world lot), serviced from `Lot.Simulate`. Drains **every** due alarm in one call, no count or time limit; yielding callbacks become `AlarmOneShot` tasks [verified] | Alarm bursts at 1 AM (story progression) and 3 AM (cleanup, stuck checks) [inferred] | none | – |
| `Sims3.Gameplay.Autonomy.AutonomyManager.Simulate()` | One global task; one autonomy search and one queued reaction per tick (`kNumSimsCanRunAutonomy = 1`), alternating high/low LOD queues [verified] | – | none | – |
| `Sims3.Gameplay.Actors.SimUpdate.Simulate()` | Per-Sim update; at most 2 high-LOD Sims per tick do a full update (`kMaxSimUpdates`). Low-LOD Sims and those over the limit resume **every tick** [verified] | 10.2% (BP) [measured] | none | – |
| `Sims3.Gameplay.Core.Lot.Simulate()` | Per-lot task: alarms, reactions. Runs every tick on the active lot, world lot, the active Sim's lot and **every commercial lot**; others sleep 30 ticks [verified] | 12.9% (BP) [measured] | none | – |
| `Sims3.Gameplay.Services.Services.Simulate()` | Round-robins one service every 30 ticks [verified] | – | none | – |
| Task-type objects: `ImmediateInteractionTask`, `OneShotFunctionTask`, `FunctionTask`, `StateMachine`, `FinalizerTask`, `AgingQueue`, `StoryProgressionTask`, `ReferenceWatchdog`, `AutoSave`, `Gameflow`, `LoadSaveManagerTask`, `SimBuilderTask` | Run by the TaskObjects stage (`0xD7FC60`) [inferred from map names and `ScriptExecuteType`] | TaskObjects stalls 0.27-6.9 s in one S3PP log; 5-7 s ones likely world load [measured, F17] | `/taskstall` probe attributes them (F18) | – |

## 2. Routing: `ScriptCore.Route` and the plan funnel

`Sims3.SimIFace.Route` is the abstract API; `ScriptCore.Route` implements it. Every `PlanTo*` helper ends in `ScriptCore.Route.Plan()` [verified]. The native search is synchronous on the sim thread under the routing lock (`Route_PlanLocked` `0x82C6E0`, [../04-re-findings.md](../04-re-findings.md) F1). There is no node, time or cost budget in the native planner ([../11-route-planner-re.md](../11-route-planner-re.md) Q1).

| Method | What it does | Time / evidence | Fix | Native |
|---|---|---|---|---|
| `ScriptCore.Route.Plan()` → `RoutePlanResult` | Calls `AboutToPlanCallback`, the native plan, then `PostPlanCallback(this, "Plan", result.ToString())` (a string per plan) [verified]. The one funnel for every plan; not overloaded | native `Route_Plan` self 9.9% BP / 11.5% SV / 13.3% IP; IP 246 ms of route search per second, 25 ms per plan, 12% failing [measured, F7b] | `RouteBackoff.cs` `RouteBackoffHook.PlanHook` (switch `backoff`, `kRouteFailureBackoff`, counters 24-26; also feeds the watchdog's route-fail count 49/51); async plan handshake `PlanAsync` (switch `asyncroute`, counters 56-58, needs ts3native `[RouteAsync]`, paused) | `Route_Plan` `0x765160` |
| `ScriptCore.Route.Replan()` | Re-plan of an existing route | `Route_Replan` 1.7% self (IP) [measured] | none | `Route_Replan` `0x765490` |
| `ScriptCore.Route.PlanFromPoint(...)` | Plan from an explicit start point; used by routability checks | 2.8% self IP; 6.4% after the go-home shortcut (its safety check) [measured, F9] | indirectly: `routablecache`, SafeSpot cache | `Route_PlanFromPoint` `0x765240` |
| `ScriptCore.Route.ReplanFromPosition` / `Route.ReplanFromPoint` | Re-plan from where the Sim stands (after obstacles, path pops) | **10.9% self in IP**; 613 replans inside hitches cost 50 s over 570 s [measured] | none (docs/18 D4 idea, not built) | `Route_ReplanFromPosition` `0x765550` |
| `ScriptCore.Route.IsPointRoutable` | Point routability query | profiled entry | none | `Route_IsPointRoutable` `0x7538C0` |
| `ScriptCore.Route.GetOption` / `GetOption2` / `SetOption` | Read/write `Route.RouteOption` / `RouteOption2` bits. Bit 58 has no enum name and the game never sets it [verified]; we use it as the ts3fixes-ts3native handshake (START/READY/CANCEL/NOYIELD) | – | ts3native detours the native getter | `Route_GetOption` `0x74D040` |
| `ScriptCore.Route.DestroyRoute` | Frees the native route | – | none | `Route_DestroyRoute` `0x750400` |
| `Sims3.SimIFace.Route.AboutToPlanCallback` / `PostPlanCallback` | Static callback slots run before and after every plan; `SimRoutingComponent.SimStartup` and mods (NRaas Overwatch) attach here [verified] | – | none | – |
| `Route.RouteOption.ReplanToFindObstacleWhenPathPlanFails` | Option that makes a failed search run a **second full search** with relaxed flags to collect blockers [verified, docs/11] | failures cost the most: one iteration with 4 failed plans took 1,010 ms (SV) [measured] | turned off for unseen townies by `RouteOptionsHook` (`failreplan`, counter 29) | – |
| `Route.RouteOption.DebugPathPlan` | Debug option (hi word bit 0x1); ts3native's route budget reads it as an opt-in flag (`OnlyFlagged`) [inferred use] | – | ts3native `[RouteBudget]` (off) | – |
| `RoutePlanResultType.FailedBecausePlannerAborted` / `FailedBecauseNoPathToDestination` | Result codes; native status 7/8 maps to "no path" [verified] | – | backoff returns the "no path, no blocker" shape | – |

### 2a. `Sims3.Gameplay.Actors.SimRoutingComponent` (and `RoutingComponent`)

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `RoutingComponent.DoRoute(Route)` | Follows a planned route; replans after obstacles and path pops; a water-entry cap resets the Sim home [verified] | 16.6% BP / 7.3% SV / 21.4% IP incl.; replans are 16% of all plan samples [measured, ../04a-script-layer-stutter.md §8] | none |
| `SimRoutingComponent.SetRouteOptions(Route, uint ageGenderFlags)` (private) | Sets every option from the Sim: age (car, water, boat planning), posture (carrying, scuba, riding, stroller, frozen ponds), ghost, mermaid, lifeguard, UFO, service pass-through, and `ReplanToFindObstacleWhenPathPlanFails` on every Sim route [verified] | – | `RouteFixes.cs` `RouteOptionsHook.SetRouteOptionsHook` (`failreplan`, `kNoRouteFailWalkOffscreen`, counter 29); also defines the SafeSpot cache key (route profile) |
| `SimRoutingComponent.GetRouteFailPath(Route)` (private) | Plans a short path to the blocker so the Sim can play the route-fail animation; up to 2 plans, called up to twice [verified] | – | `RouteOptionsHook.GetRouteFailPathHook` (`failreplan`, counter 30): unseen townies skip it |
| `SimRoutingComponent.DoRouteFailureBehavior(Route)` (private) | On failure: fail-path plans, then `PlanToLot` (1-2 plans), then the thought balloon [verified call sites] | 4-6 extra plans per failure [inferred count] | indirect (backoff returns no blocker, so no fail-path plans) |
| `SimRoutingComponent.CheckAndUpdateRouteForStations(Route)` (private static) | After a successful cross-lot plan, rolls a chance for a carless NPC to take the subway or hover train instead: scans every station, up to 3 more plans [verified, ../10-expansion-travel-shortcuts.md] | – | `RouteFixes.cs` `StationsHook` (first static hook; `stations`, `kNoStationDetourOffscreen`, counter 32) |
| `SimRoutingComponent.CheckAndUpdateForHouseboats(Route)` (private static) | Post-plan callback that throws a plan to or from a houseboat lot away and redoes it as gangway legs (with House Boat Fix: `EnterHouseboatThroughGangway`) [verified] | 13% of plan samples, IP only (~6% of IP script time); boarding 5-8% vs boats moving 0.3% [measured] | none (House Boat Fix code); docked-houseboat trips shortcut instead |
| `SimRoutingComponent.WalkStyleComparer.Compare(Sim.WalkStyle, Sim.WalkStyle)` (private nested) | Orders walk styles by priority through two enum-keyed dictionary lookups; each boxes the enum and calls `Enum.Equals`/`GetHashCode` in the interpreter [verified] | walk-style rules 6.0% (BP); `System.Enum.Equals` self 5.1% [measured, F7] | `Fixes.cs` `WalkStyleCompareHook` (array by enum value; `kWalkStyleComparer`, no switch): `Enum.Equals` 5.1% → 1.0%, `GetAdjustedWalkStyleForRoute` 6.0% → 1.7% [measured, F11] |
| `SimWalkStyleRules.GetAdjustedWalkStyleForRoute` / `PostProcessWalkStyle` | Picks the walk style per route (`SortedList<WalkStyle,…>` lookups) [verified] | 6.0% / 5.4% (BP) [measured] | via the comparer hook |
| `SimRoutingComponent.AboutToPlanRouteCallback` / `PostPlanRouteCallback` | The Sim's own pre/post plan callbacks [verified] | – | none |
| `RoutingComponent.LogFailure` | Only counts failures unless logging is on [verified] | – | none |
| `RouteToSlot`, `RouteToPoint`, `RouteToObjectRadius`, `RouteToObjectRadialRange`, `RouteToLot`, `RouteToDynamicObjectRadius*` | Interaction-facing route entry points [verified] | – | none |
| `Sims3.Gameplay.Routing.DestinationObstructedRouteInterruptAction.PerformAction` | Stand-and-wait for up to 50 sim-minutes (`kDefaultStandAndWaitDuration`), pushing Sims and replanning each cycle [verified] | `StandAndWaitController` 1.5% (IP) [measured] | watchdog (120 sim-minute routing rule) |
| `Sims3.Gameplay.Routing.StandAndWaitController.Run` | Wait animation loop between replans [inferred] | as above | none |
| `Sims3.Gameplay.Routing.FollowRouteAction.PerformRoute` / `Kill` | Runs a route; `Kill` is the only place that unsubscribes the route's script adaptors [verified] | – | see §8 portal leak |

### 2b. Plan callers, ranked ([../04a-script-layer-stutter.md](../04a-script-layer-stutter.md) §8, 20 profiles, 15,417 samples; plans = 15.2% of script time)

| Rank | Caller | Share of plan samples | Fix |
|---|---|---|---|
| 1 | `RoutingComponent.DoRoute` replans | 16% | none |
| 2 | `SimRoutingComponent.CheckAndUpdateForHouseboats` gangway re-plans | 13% (IP only) | none |
| 3 | `Lot.PlanToLotEx` (lot visits, `GoToVirtualHome`) | 11% | trip shortcuts |
| 4 | `Lot.PlanRouteIntoLot` (GoHome) | 8%; one 1,069 ms plan | go-home shortcut |
| 5 | `LotManager.RunStuckCheckHelper.RunStuckCheck` | 5% (night only); 645 ms stall | `stuckcheckbusy` |
| 6 | `Lot.RouteToDoor` / `RouteToFrontDoorOrMailbox` | 5% | none |
| 7 | `RabbitHole.RouteOutside` / `RouteNearEntranceAndEnterRabbitHole` | 6% | `rabbithole` |
| 8 | `Sim.IsPointInLotSafelyRoutable` | 4%; 345 and 456 ms stalls | `routablecache`, SafeSpot cache |
| 10 | `LiteDriver.Simulate` / `CarNpcManager.DriveAwayInNpcCar` (NPC cars) | 5% | none (car shortcut dropped) |

Autonomy scoring plans no routes in these profiles; it uses straight-line distance [measured].

## 3. Trips: go-home, visits, rabbit holes, situations

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `Sims3.Gameplay.Core.GoHome.Run()` | Plans up to three world-scale routes in a row (route, `PlanToLot` again, then with water planning); only the third plays the fail balloon; on failure bumps `GoHome.NumFail` [verified] | 7.2% BP / 5.2% SV / **11.1% IP** incl. → 5.6% IP with the shortcut [measured, F9] | `Fixes.cs` `GoHomeHook` (`gohome`, `kGoHomeShortcut`, counters 0-3) |
| `Sims3.Gameplay.Actors.Sim.MakeSimGoHome` | Queues `GoHome` only when `LotHome != null`, else `Sim.GoToVirtualHome` [verified] | – | none |
| `Sim.GoToVirtualHome.Run` / `GoToVirtualHomeMeta.Run` | Homeless or virtual-apartment townie "goes home": target is the virtual home or a **random lot** [verified] | – | none |
| `Sim.GoToVirtualHome.GoToVirtualHomeInternal.Run()` | Plans and walks to that lot, then fades the Sim out and destroys it; the walk result is not checked. Shown as "Go Home" in the UI [verified] | 4.4% BP / 1.2% SV / 1.1% IP [measured, F7b] | `RouteFixes.cs` `VirtualHomeHook` (`virtualhome`, `kVirtualHomeShortcut`, counters 27-28): unseen trips fade at once |
| `Sims3.Gameplay.Abstracts.RabbitHole.RouteNearEntranceAndEnterRabbitHole(Sim, IRabbitHoleFollowers, BeforeEnteringRabbitHoleDelegate, bool canUseCar, Route.RouteMetaType, bool playRouteFailure)` | Routes to the entrance and enters; 3 attempts with 1-5 sim-minute sleeps between [verified] | rabbit-hole trips 6.4% (IP) [measured] | `Fixes.cs` `RabbitHoleHook` (`rabbithole`, `kBuildingTripShortcut`, counters 4-6) |
| `Sims3.Gameplay.Core.VisitCommunityLot.Run()` | Community lot visit | visits 7.2% (IP, with the next three) [measured] | `VisitCommunityLotHook` (`visit`, `kLotTripShortcut`, counters 7-9) |
| `Sims3.Gameplay.Core.VisitLot.Run()` | Home visit | as above | `VisitLotHook` (`visitlot`, counters 10-12) |
| `Sims3.Gameplay.Core.GoToLot.Run()` | Plain lot trip | as above | `GoToLotHook` (`gotolot`, counters 13-15) |
| `Sims3.Gameplay.Core.GoToCommunityLot.Run()` | Plain community lot trip | as above | `GoToCommunityLotHook` (`gotocommunity`, counters 16-18) |
| `Sims3.Gameplay.Core.Lot.PlanToLotEx` / `PlanRouteIntoLot` / `PlanToPointRadialRange` | Lot-scale plans used by the trips above | see §2b | indirect |
| `Sims3.Gameplay.Objects.Island.Houseboat` (`IsHouseboatLot`, `GetHouseboat`) | Houseboat lot parented to a boat object (`World.ParentLotToHouseboat`); moves only through helm interactions. NPC households do cruise (3 of 6 in 10 min, IP) [measured] | boats moving 0.2-0.3% [measured] | trips allowed only while docked (`HouseboatOk`, counter 21) |
| `Sims3.Gameplay.Objects.Vehicles.LiteDriver` / `Route.PlanToPoint` for `CarNpcWithNoDriver` | Ambient traffic cars [verified] | ~1% (BP) | shortcut **dropped**: ~24 stuck Sims in 2.5 min vs 7 off (BP, F10) |
| `ShellWithDoor.RouteInsideShell`, `ElevatorDoors.*`, `GetInBoatRouteAction.PerformAction`, `Subway.SubwayPortalComponent`, `HoverTrainStation.TravelTo.Run`, `Sim.NPCUseHoverTrain.Run` | Expansion travel legs (apartments, elevators, boats, subway, hover train) live inside ordinary route plans, so the trip hooks already skip them [verified, docs/10] | – | not hooked: per-object state (queues, drivers, station lists) would be left behind |
| `CarpoolManager.WaitForCarpool` | Loops only while the Sim is in a carpool car (up to 360 sim-minutes) [verified] | – | none (ruled out as stuck cause, docs/12) |
| `Sims3.Gameplay.Actors.Sim.SetToResetAndSendHome()` | The game's own reset that sends a Sim home [verified] | – | used by our watchdog |
| `Actor.FadeOut(bool, bool)` | Fades a Sim out, optionally destroying it [verified] | – | used by `VirtualHomeHook` and the watchdog for homeless townies |

## 4. Autonomy and reactions

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `Sims3.Gameplay.Autonomy.Autonomy.RunAutonomy(CommodityKind, bool)` / `FindBestAction` / `TryFindBestAction` | One autonomy search; sends an idle NPC home when it finds nothing [verified] | scoring plans no routes [measured] | none |
| `Autonomy.ScoreInteractionsForLocalAutonomy` | Scores every interaction on the lot and every Sim on it, no yield; asks `GetObjects<Sim>` per commodity kind [verified] | spike size not measured [inferred] | indirect: `spatialcache` |
| `Autonomy.ScoreInteractionsForMetaAutonomy` | Venue scoring; yields every 5 venues past 1 ms [verified] | – | none |
| `Autonomy.CalculateScoreForObjectInteraction`, `ComputeCheckScore`, `CalculateScoreForSocial` | Scoring functions [verified] | not measured | none |
| `Autonomy.Update` | Motives, buffs, mood, skills, careers, events per Sim [verified] | – | none |
| `Sim.CalculateNumSimsAtHomeOrGoingHome()` | Loop over all actors, run on every autonomy search [verified] | small (O(sims²) per cycle) [inferred] | none |
| `AutonomyManager.AddWhileInteracting` / `DequeueActorFromQueue` | Linear `LinkedList` scans [verified] | small [inferred] | none |
| `Sims3.Gameplay.Autonomy.ReactionBroadcaster.PulseSims(float pulseRadius, bool fullSimulation)` (private) | Each broadcaster (music, fire, smells, ...) pulses about once per sim-minute: a native range query for Sims plus line-of-sight checks [verified] | 7.1% BP / 1.4% SV / 5.7% IP → 4.3% BP with the throttle [measured, F12] | `Fixes.cs` `BroadcasterPulseHook` (`broadcast`, `kBroadcasterThrottle`, counters 22-23): off-screen ones pulse 1 in 4 |
| `ReactionBroadcaster.GetInRadiusSims` | Allocates a `List` and a `Dictionary` per pulse; calls `Query_GetObjectsInRange` [verified] | 4.2% → 1.5% (BP) [measured] | via the throttle |
| `Sims3.Gameplay.Core.Lot.UpdateReactions` | Drives broadcasters every tick on every simulating lot; `ToArray()` each time [verified] | 8.9% BP / 1.9% SV / 6.6% IP → 5.5% BP [measured] | via the throttle |
| `Sims3.Gameplay.EventSystem.EventTracker.SendEvent` | Event fan-out | 4.6% (BP) [measured] | none |
| `Sim.CanSeeObject` | Line-of-sight check used by broadcasters [verified] | – | none; native `World_CanSeeObjectImpl` `0x78F090` |

## 5. Stuck checks

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `Sims3.Gameplay.Core.LotManager.CleanUpLotsAndInventories` | Daily 3 AM alarm (`kVenueCleanupTime`): cleans or re-seasons every lot, seeds NPC inventories, arms the stuck check for every actor [verified] | alarm burst [inferred] | none |
| `LotManager.RunStuckCheckHelper.RunStuckCheck(Sim)` (private, nested internal class) | A Sim that has not moved 20-60 sim-minutes later gets up to 10 plans to random lot points (`Sim.kNumberOfPlanAttemptsForStuckSim`) in one alarm tick, up to 3 rounds before a reset. At 3-4 AM nearly every townie is asleep, so it plans from beds [verified] | 0.7% (night only), one 645 ms stall [measured] | `FewerPlans.cs` `StuckCheckBusyHook` (`stuckcheckbusy`, `kStuckCheckBusy`, default off, counter 67): Sims held by an object end the check |
| `Sims3.Gameplay.Actors.Sim.IsPointInLotSafelyRoutable(Lot, Vector3)` | Plans with `CreateRouteAsAdult()` from the spot to the lot's 4 corners and 4 edge midpoints; a full route plan [verified] | 4% of plan samples; 345, 362 and 456 ms stalls [measured, F18] | `FewerPlans.cs` `RoutableCacheHook` (`routablecache`, `kRoutableCache`, default off, counters 65-66); SafeSpot verdict cache in `Fixes.cs` (counters 37-39) |
| `Sim.CreateRouteAsAdult()` | Route with the Sim's species/gender flags, age forced to adult, then `SetRouteOptions` [verified] | – | defines the verdict cache key |
| `Actor.AttemptToFindSafeLocation` / `AttemptToPutInSafeLocation(bool)` / `FindRoutablePointInsideNearFrontDoor` / `CreateTeleportInstanceToPosition` | Routability checks in loops after resets and placements [verified] | share of the 4% above [measured] | via `routablecache` |
| `Sim.SetObjectToReset` / `ScriptProxy.SetObjectToReset` | Request a reset (about 103 call sites) [verified] | – | used by the watchdog |
| `Sim.DEBUG_CheckStuckSim` | Cheat that runs the stuck helper on demand [verified] | – | none |
| `Sims3.Gameplay.Core.Lot.IsRoomHidden` | True for rooms the lot hides [verified] | – | watchdog case "hidden room" (counter 47) |
| NRaas Overwatch: `NRaas.CommonSpace.Helpers.SimEx.IsPointInLotSafelyRoutable(Sim, Lot, Vector3)` (static) | Overwatch's routability helper, called once per standing Sim by its nightly Stuck Check (`Overwatch.OnTimer` → `AlarmOption.PerformAlarm` → `StuckCheck.PrivatePerformAction`) inside one `FunctionTask` tick [verified, ../07-known-fixes-catalog.md §2.2] | 287-326 ms per sim night [measured, F18] | `OverwatchStuck.cs` `OverwatchStuckHook` (`overwatchstuck`, default on, counters 44-45): yields one tick after each check when `MayYield()`; installer override `overwatch-no-stuckcheck` (default off) |
| Our watchdog `Ts3Fixes.Fixes.StuckWatchdog` | Every 10 sim-minutes: unseen townie stuck going home 60 sim-minutes or routing 120 → `SetToResetAndSendHome` (homeless: fade out); idle cases (unroutable spot, hidden room, a sim-day in one spot, 10+ failed plans in 1 m) [built] | – | `RouteFixes.cs` (`watchdog`/`watchdogidle`, `kStuckWatchdog`/`kStuckWatchdogIdle`, counters 31, 33-36, 46-51) |

## 6. Spatial and object queries

| Method | What it does | Time / evidence | Fix | Native |
|---|---|---|---|---|
| `Sims3.SimIFace.Queries.GetObjects(Type, ulong lotId)` | Every `Lot.GetObjects<T>()` / `Queries.GetObjects<T>(lot)` ends here; a native walk of the lot's objects that returns a **new array** each call [verified] | IC 1.7% of managed samples + 1.1% native (docs/20 row 9); `Queries.GetObjects` 6.0% BP incl. [measured] | `SpatialCache.cs` `SpatialCacheHook.GetObjectsHook` (`spatialcache`, `kSpatialCache`, default off, counters 62-64): one result per (type, lot) per sim tick, copy on hit; +5% it/s in an in-load A/B [measured, PLAN] | `Query_GetObjectsOnLot` `0x749D30` (impl `0x7499B0`) |
| `Queries.GetObjects(Type, Vector3 center, float radius)` | Range query (broadcasters) | `Query_GetObjectsInRange` 2.6% self (BP) [measured] | none | `Query_GetObjectsInRange` `0x7496E0` |
| `Queries.CountObjects*` / `Lot.CountObjects` | Counting variants | – | none | `Query_CountObjectsOnLot` `0x749BC0` |
| Hot callers: `TombRoomManager.AboutToPlanRouteCallback` / `AddForbiddenPortalsToRouteForLot`, `Sim.GetMostExpensiveUsableParkedVehicle`, `LightGameObject.AutoLights*`, `Lot.FindCallbox`, `WorldLot.GetObjects` | Ask the same (type, lot) many times per tick [measured, docs/20 §4a] | 0.2-1.0% each | via `spatialcache` | – |
| `GameObject.get_Parent` → `get_LotCurrent` | Object-id lookups through the native hash (`0x939100`) | 0.7% (docs/20 row 5) [measured] | none | `Slots_GetParentIdImpl` `0x76F780` |
| `World.GetTerrainHeight` | Floor/terrain height raycast | native 1.9% (IP 2.5%) [measured] | none (cache idea, docs/20 #7) | `World_GetTerrainHeightImpl` `0x786200` |

## 7. Save, persistence and save growth

Saves grow 0.44 MB (BP) and 0.55 MB (SV) per sim day; the biggest grower is memory snapshots in `TravelDB.package` ([../09-save-bloat-research.md](../09-save-bloat-research.md)). Our cleanup is opt-in (`kSaveCleanup`, daily at 2:00, never while saving, travelling or on vacation; [../14-save-cleanup.md](../14-save-cleanup.md)).

| Method | What it does | Fix |
|---|---|---|
| `ScriptCore.LoadSaveManager.SaveGame` | Script entry of the native save (package write + script serialization on the sim thread) [verified] | ts3native `[SaveWrite]` times and offloads the backup copy (native side) ; IC `LoadSaveManager_SaveGame_Impl` `0x742A50` |
| `LoadSaveManager.SaveTravel`, `GameStates.TravelToVacationWorld`, `GameStates.SwitchWorlds`, `GameStates.ReturnFromVacationWorld` | Each travel leg = full save + cold world load; adds MiniSims for the world left [verified, ../19-travel-worlds.md] | none (measuring only: `travel-trip.ts`) |
| `Sims3.Gameplay.Socializing.Relationship.DecayAll` / `SocialComponent.RelationshipDecay` | Daily decay; NPC-NPC pairs decay at 0.001x so weak pairs pile up [verified] | `SaveCleanup.cs` (a) removes weak NPC-NPC pairs with `Relationship.RemoveRelationship` (counter 40) |
| `Relationship.sAllRelationships` | Global pair store [verified] | counted by `save-stats` / agent |
| `Sims3.Gameplay.CAS.MiniSimDescription.sMiniSims` / `RemoveMSD` / `AddMiniSims` | Lightweight records of Sims from other worlds; never aged out [verified] | `SaveCleanup.cs` (b) removes unlinked other-world MiniSims (counter 41) |
| `LifeEventManager.ProcessLifeEvent` / `PurgeLifeEvents` / `GetActiveNode` | Life-event (memories) store per Sim; readers recreate missing lists [verified] | `SaveCleanup.cs` (c) drops empty lists (counter 52) |
| `LifeEvent.Forget` / `World.DeleteTravelDBResource` / `LifeEvent.Snapshot` | Memory pictures in `TravelDB.package`; without a picture the stock image is shown [verified] | `SaveCleanup.cs` snapshot cap (`kSnapshotCap`, 0 = off, counter 53) |
| `SimDescription.PurgeDistantGenealogyRelations`, `Household.CleanupBadlySavedInventories`, `LotManager.CleanupObjectsNotOnLot`, `Autonomy.OnceADayCallback` | The game's own cleanups [verified, docs/09] | none (kept) |
| `World.CapturePhoto` | Memory snapshot capture | none |
| `ScriptCore.PersistWriter.AddObject` / `GetObjectIndex` / `End` | One writer for the whole world: every object gets the next global index on first visit (roots in lot order, then breadth-first over references inside `End`); class and resource-key tables are global too, so one new or removed object renumbers everything visited after it [verified, ../26-incremental-save.md] | none (docs/26: incremental save not feasible without stable numbering) |
| `ScriptCore.LoadSaveManager.SaveObjectGroup` / `SaveObject` / `SaveObjectStack` | Native callbacks per lot group, per object and per saved task stack; each returns the global index the native lot record (`0x4D1A5589`) stores [verified] | none |
| `ScriptCore.ScriptObjectGroup` (+ `ScriptObjectGroupFormatter`, `IPersistGroupAffinity`) | Per-lot script object set; written as one record in the global OBJS graph, not as its own resource. Group affinity only filters objects of groups not being saved (partial saves) [verified] | none |
| `Sims3.Gameplay.Core.Lot` persisted `ScriptCore.Route` objects | IP saves keep 0 → 224 routes over 11 days, held by portal actions and adaptors [measured] | §8 |

## 8. Portals, route adaptors, houseboats

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `Sims3.Gameplay.Routing.PortalAnimateThroughAction.DoPortal()` | Creates route adaptors (`ScriptAdaptor.Init` subscribes `Sim.OnExitReasonsAdded`) and then skips `PerformRoute` for the Ocean, a Houseboat, or a portal refusing the pre-route, so the only unsubscribe (`FollowRouteAction.Kill`) never runs [verified, ../09-save-bloat-research.md §5] | ~23 dead callbacks per sim day (IP); keeps Route, adaptors and action alive and in the save; can eat a Replan exit reason [measured/verified] | `PortalAdaptorLeak.cs` `PortalAdaptorHook` (`portaladaptorleak`, `kPortalAdaptorLeak`, counter 54); existing saves: cleanup (d) (counter 55) |
| `ScriptAdaptor.Init` / `ScriptAdaptor.Kill` | Subscribe/unsubscribe on `Sim.OnExitReasonsAdded` [verified] | – | hook does what `Kill` does minus the RouteFailed exit reason |
| `PortalAction.ForceReplan` | Replan through a portal action [verified] | – | none |
| `LotManager.PortalLockedCallback`, `World.OnPortalLocked`, `Lot.OnPortalPathPlan`, `Sim.OnPortalPathPlan`, `Route.SetPortalLockStatus`, `Door.IsAllowedThrough`, `Ladder.DiscourageSimFromUsing` | **Managed calls the native planner makes during a search** (portal lock queries) through `mono_runtime_invoke` [verified, ../18-route-search-off-thread.md] | why a native worker thread must proxy managed calls back to the sim thread | ts3native route worker (InvokeHook) |

## 9. Create-a-Sim and Buy

| Method | What it does | Time / evidence | Fix |
|---|---|---|---|
| `Sims3.UI.CAS.CASHair.PopulateTypesGrid(bool isHat)` (private) | Builds every hair tile in one call on the sim thread: a UI layout per tile, 2-3 synchronous CAS resource lookups and a preset build [verified] | hair 3.2 s / 261 items = ~1 s tab switch + ~2.2 s tile loop (~8.5 ms per tile) [measured + inferred, ../21-cas-offload.md] | `CasGrid.cs` `CasHairHook` (`casgrid`, `kCasGrid`, default off, counter 59) |
| `Sims3.UI.CAS.CASMakeup.PopulatePartsGrid(BodyTypes)` (private) | Same for makeup parts [verified] | makeup 1.0 s / 17 items [measured] | `CasGrid.cs` `CasMakeupHook` (`casgrid`) |
| `CASHair.LoadParts`, `CASModel.GetVisibleCASParts(BodyTypes)` | Hair part list from the native `CASPartCache` [verified] | – | none |
| `ObjectDesigner.SetCASPart` / `ObjectDesigner.DefaultPresetId` / `Simulator.LoadXMLString` / `CASUtils.PartDataNumPresets` | Per-part preset data [verified] | – | `casmemo` caches it per CAS session (counters 60-61) |
| `Sims3.UI.ItemGrid.BeginPopulating` / `AddItem` / `SelectByComparableTag` / `Clear` | Incremental grid fill EA already uses for clothing (`CASClothingCategory`) [verified] | – | used by `casgrid` |
| `StoreUI.GetCASFeaturedStoreItems` / `UserToolUtils.GetFeaturedCASItemsListFiltered` | Featured store tiles [verified] | – | kept from the original |
| `ThumbnailHelper.ThumbnailManager_OnStartPregeneration`, `UIManager.GetCASThumbnailImage` | Thumbnails: async, cached on disk; not in the 3.2 s [verified] | – | none |
| `StringTable.GetLocalizedString` | Localized strings (S3PP replaces the native cache side) [verified] | – | S3PP label check PASS |

## 10. GC and finalizers

The collector is Boehm inside Mono; GC runs every frame with a small budget, and collections are triggered by allocation ([../13-gc-tuning.md](../13-gc-tuning.md)).

| Method | What it does | Fix |
|---|---|---|
| `System.GC.Collect()` | Called only in rare paths: `DynamicChallengeManager.DestroyChallenges`, `ReferenceWatchdog.ValidateReferences` (debug), a cheat in `Commands.cs` [verified] | none; heap patch in ts3native `[GC]` |
| Allocation hot spots | `Queries.GetObjects` arrays, `Lot.UpdateReactions` `ToArray`, `GetInRadiusSims` collections, `RoutePlanResult.ToString()` per plan, `AlarmManager.InvokeYieldingAlarm` (`AlarmOneShot` per alarm), `Autonomy.PerformInteraction` (`ImmediateInteractionTask`), `RunStuckCheckHelper.CreateAlarm` (string per Sim per day) [verified] | `spatialcache` and the broadcaster throttle reduce some |
| `FinalizerTask` | Runs script finalizers as a task (TaskObjects stage) [inferred] | none |
| No LINQ, no `System.Threading` | No game script assembly uses `System.Linq` or `System.Threading` (no locks, threads, pools) [verified] | – |

---

## ts3fixes method replacements

Every managed method `mods/ts3fixes` replaces at startup through its patch host (copies the replacement's `MonoMethod` header over the game method's; an optional "orig" stub keeps the original callable; [../04-re-findings.md](../04-re-findings.md) F8). A switch is the name the test agent passes to `patches {name:false}`; with no switch set, the tuning decides. Static targets use a static hook and a static stub.

| Target method | Hook (class.method) | Switch | Default | File |
|---|---|---|---|---|
| `Sims3.Gameplay.Core.GoHome.Run()` | `GoHomeHook.RunHook` (stub `RunOrig`) | `gohome` | on (`kGoHomeShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Abstracts.RabbitHole.RouteNearEntranceAndEnterRabbitHole(...)` | `RheEnterHook.EnterHook` (`EnterOrig`), runs `RabbitHoleHook.EnterPre` first (2026-11-24, docs/130 §11) | `rabbithole` | on (`kBuildingTripShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Core.VisitCommunityLot.Run()` | `VisitCommunityLotHook.RunHook` (`RunOrig`) | `visit` | on (`kLotTripShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Core.VisitLot.Run()` | `VisitLotHook.RunHook` (`RunOrig`) | `visitlot` | on (`kLotTripShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Core.GoToLot.Run()` | `GoToLotHook.RunHook` (`RunOrig`) | `gotolot` | on (`kLotTripShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Core.GoToCommunityLot.Run()` | `GoToCommunityLotHook.RunHook` (`RunOrig`) | `gotocommunity` | on (`kLotTripShortcut`) | Fixes.cs |
| `Sims3.Gameplay.Actors.SimRoutingComponent.WalkStyleComparer.Compare(WalkStyle, WalkStyle)` (private nested) | `WalkStyleCompareHook.CompareHook` (no stub) | none (patched only when the tuning is on) | on (`kWalkStyleComparer`) | Fixes.cs |
| `Sims3.Gameplay.Autonomy.ReactionBroadcaster.PulseSims(float, bool)` | `BroadcasterPulseHook.PulseHook` (`PulseOrig`) | `broadcast` | on (`kBroadcasterThrottle`) | Fixes.cs |
| `Sims3.Gameplay.Actors.Sim.GoToVirtualHome.GoToVirtualHomeInternal.Run()` | `VirtualHomeHook.RunHook` (`RunOrig`) | `virtualhome` | on (`kVirtualHomeShortcut`) | RouteFixes.cs |
| `Sims3.Gameplay.Actors.SimRoutingComponent.SetRouteOptions(Route, uint)` | `RouteOptionsHook.SetRouteOptionsHook` (`SetRouteOptionsOrig`) | `failreplan` | on (`kNoRouteFailWalkOffscreen`) | RouteFixes.cs |
| `Sims3.Gameplay.Actors.SimRoutingComponent.GetRouteFailPath(Route)` | `RouteOptionsHook.GetRouteFailPathHook` (`GetRouteFailPathOrig`) | `failreplan` | on (`kNoRouteFailWalkOffscreen`) | RouteFixes.cs |
| `Sims3.Gameplay.Actors.SimRoutingComponent.CheckAndUpdateRouteForStations(Route)` (static) | `StationsHook.CheckStationsHook` (`CheckStationsOrig`) | `stations` | on (`kNoStationDetourOffscreen`) | RouteFixes.cs |
| `ScriptCore.Route.Plan()` | `RouteBackoffHook.PlanHook` (`PlanOrig`) | `backoff`; `asyncroute` (async handshake, ts3native decides) | on (`kRouteFailureBackoff`); async off in ts3native | RouteBackoff.cs |
| `NRaas.CommonSpace.Helpers.SimEx.IsPointInLotSafelyRoutable(Sim, Lot, Vector3)` (static, NRaas Overwatch; applied at startup and again at world load) | `OverwatchStuckHook.RoutableHook` (`RoutableOrig`) | `overwatchstuck` | on (no tuning) | OverwatchStuck.cs |
| `Sims3.Gameplay.Routing.PortalAnimateThroughAction.DoPortal()` | `PortalAdaptorHook.DoPortalHook` (`DoPortalOrig`) | `portaladaptorleak` | on (`kPortalAdaptorLeak`) | PortalAdaptorLeak.cs |
| `Sims3.UI.CAS.CASHair.PopulateTypesGrid(bool)` | `CasHairHook.PopulateHook` (`PopulateOrig`) | `casgrid`, `casmemo` | off (`kCasGrid`) | CasGrid.cs |
| `Sims3.UI.CAS.CASMakeup.PopulatePartsGrid(BodyTypes)` | `CasMakeupHook.PopulateHook` (`PopulateOrig`) | `casgrid` | off (`kCasGrid`) | CasGrid.cs |
| `Sims3.SimIFace.Simulator.DestroyObject(ObjectGuid)` | `SpatialCacheHook.DestroyHook` (`DestroyOrig`) | `spatialcache` | off (`kSpatialCache`) | SpatialCache.cs |
| `Sims3.SimIFace.Queries.GetObjects(Type, ulong)` | `SpatialCacheHook.GetObjectsHook` (`GetObjectsOrig`) | `spatialcache` | off (`kSpatialCache`) | SpatialCache.cs |
| `Sims3.Gameplay.Actors.Sim.IsPointInLotSafelyRoutable(Lot, Vector3)` | `RoutableCacheHook.RoutableHook` (`RoutableOrig`) | `routablecache` | off (`kRoutableCache`) | FewerPlans.cs |
| `Sims3.Gameplay.Core.LotManager.RunStuckCheckHelper.RunStuckCheck(Sim)` (private, nested internal class) | `StuckCheckBusyHook.RunHook` (`RunOrig`) | `stuckcheckbusy` | off (`kStuckCheckBusy`) | FewerPlans.cs |

Not method replacements, but ts3fixes behaviour started at world load: the stuck watchdog task (`watchdog`, `watchdogidle`; RouteFixes.cs), the 2:00 save cleanup alarm (`kSaveCleanup`, `kSnapshotCap`; SaveCleanup.cs) and the SafeSpot spot spreading (`spotspread`, always on; Fixes.cs).

Conflicts to know: MonoPatcher-style hosts and other mods that replace the same methods (House Boat Fix hooks the houseboat callback, not these targets; NRaas replaces whole assemblies, Overwatch's helper is hooked above). See [patch-points.md](patch-points.md).
