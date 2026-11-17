# 594. Seasons (EP8) hunt: scanner queue, hot paths, log (2026-11-16)

Static + RE, then proven in game (s61, s62; results at the end). Six switches built (default off): §1-2, then §3-6 from the hot-path leads (Built (2)); the rest verified false positives or leads.
Already built for Seasons before this pass (not repeated): tempforecast, holidaylights, hauntedumbrella, snowfightjoinwait,
trickortreatalarm, npcseasonalparty, splashroof, puddlereach, mopunreachable, petpuddletile, seasonimposter /
seasonmarkercache / easpreadlots (docs/44, docs/74, docs/147), lightning lead (docs/593).

## Built

### §1 `initstacktrace` (fixes 2128-2131, `InitStackTraceCheck`, repro `initstacktrace-check`) — lag, every Sim
- [verified, IL of Sims3GameplaySystems.dll, EA and simler90 UPDATE242] `InteractionInstance.Init` stores
  `new StackTrace(4, true)` in `mInitStackTrace`; `OnAddedToQueue` stores `new StackTrace(1, true)` in `mAddedStackTrace`.
  The only reader is `LogDebugInfo`, `[Conditional("EA_DEBUG")]` / `[Conditional("EA_DEBUGRELEASE")]`, so every call is
  compiled out of retail: both stores are dead.
- [verified, EA mscorlib] `StackTrace.init_frames` loops `new StackFrame(n, true)`, and each StackFrame walks the stack from
  the top: O(depth²) frame visits plus a StackFrame + MethodBase per frame, on every interaction instance created.
- Seasons multiplier [verified, decompiled]: in rain or hail, `Autonomy.CalculateScoreForObjectInteraction` creates a
  throwaway instance for every outdoor non-Sim candidate it scores, only to pass it to
  `SeasonsManager.IsShelteredFromPrecipitation(obj, ii)`, which reads it only for `ICanCountAsBeingIndoors` targets.
  Known since docs/127 (H11) as an offload blocker; nobody had removed the walk.
- Fix: our copies of `Init` / `OnAddedToQueue` without the walk; every other field set in EA's order with EA's values
  (private `mAutonomySearchType`, `mFlags`, `sNextId`, `mSelectedObjects` via reflection; error falls back to EA's body).
  Exact (no reader), so household Sims are included. Cost per walk [inferred, not measured]: the check's `result` times one
  walk and one instance in game (`timing:` field) to size the gain.
- Root cause left alone: the throwaway instance itself (Id increments are observable, so skipping it is not exact).

### §2 `weatherstonelistener` (fixes 2132-2134, `WeatherStoneListenerCheck`, repro `weatherstonelistener-check`) — bug, rare
- [verified, decompiled] `WeatherStone` holds the summon's `kWeatherStarted` / `kWeatherStopped` listener in
  `mWeatherChangedEventListener`; only the callbacks remove it. `Dispose` does not, so a stone destroyed mid-summon
  (`SetToDestroy`, build/buy delete) leaves a listener that keeps it alive and runs `AnimateChangeWeatherStoneState` on the
  disposed stone. Fix: remove it before EA's `Dispose`. Household-visible only through the stone (player summons).

## Built (2): the hot-path leads (2026-11-16, default off, proven in game s62)
All four act on household Sims too: an EA bug fix or a proven-exact reorder, no skip or throttle. Code:
`mods/ts3fixes/src/SeasonsHunt594b.cs`; checks print `verdict:` BROKEN (off) / FIXED (on), `controls ok`, `members found`;
live-path asserts registered with `LiveAdd` (`CheckVerdict.cs`); repros `<switch>-check`.

### §3 `weathermanagerthrow` (fixes 2135-2137, `WeatherManagerThrowCheck`) — bug, kills Seasons for the session
- [verified, decompiled] `SeasonsManager.Simulate` runs Progress, Temperature, Weather, TerrainCover and Tree `Update` in one
  loop inside `try { } catch (ResetException) { }`: any other throw ends the script, so weather, temperature, snow, ponds and
  leaves freeze until the next load. `RainEvent.PutOutFires` (every 10 sim-min of rain) extinguishes each unsheltered fire,
  then calls `Firefighter.sFirefighterDictionary.TryGetValue` with no null test; `Firefighter.Create` allocates the
  dictionary only when `ServiceNPCSpecifications.ValidForCurrentWorld(Firefighter)` and `DestroyFirefighter` nulls it.
- Fix (root cause): the lookup is skipped when the dictionary is null (no firefighter to mark `RainAssisted`). No blanket
  catch. Hook: our copy of `PutOutFires` (also carries §4); `UpdateData` is private, so the hook takes a field-for-field
  mirror struct and Apply refuses to patch unless EA's fields match (names, types, order); `mNextPutOutFiresTime` /
  `SchedulePutOutFires` through handles cached once (next time cached per RainEvent; the off path drops the cache).
- Audit of the rest of the loop [verified, decompiled; no other unguarded null found that can be reached]: WeatherManager
  (`TrimForecastQueue`, `UpdateWeatherQueue`, `UpdateCurrentWeather` -> event `Update`/`OnStart`/`OnStop`, `UpdateCloudiness`,
  `UpdateWindiness` -> `UpdateFlagState`), PrecipitationEvent / RainEvent (`DoLightning` picks only lists with weight > 0,
  `DoThunder`, `ConsiderCancellingSimInteractions`: `Lot.mSims` is a field-initialised List), TerrainCover (`UpdatePondFreezing`
  -> `PondManager.*`, puddles), Temperature (`UpdateHeatWave` reads `Responder.Instance.HudModel`, live in game), Progress
  (`GoToNextSeason` tests `Household.ActiveHousehold`), Tree (`DropLeaves` over `LotManager.AllLots`). `Sim.BuffManager`
  (read by `IsSimOnFire` in the same pass) is set in `Sim.OnCreation` and never nulled. Noted, not a null: `UpdateWeatherQueue`
  would never end if a profile's min and max length were both 0 (tuning has none).
- Same unguarded dictionary outside this loop (not reached from Simulate, left): `ExtinguishBase` line ~265, `FireDamage.Begin`
  (guarded by `Firefighter.Instance != null`).

### §4 `rainsheltercheap` (fixes 2138-2141, `RainShelterCheapCheck`) — exact speedup
- [verified, decompiled] The same pass tests every Sim and every Soil with `IsShelteredFromPrecipitation` (native
  `GetLotAtPoint` + `HasAnyRoofOrFloorAbovePos`) before the cheap test: `IsSimOnFire` (buff lookup) / `Soil.OnSprinkled`'s
  own condition (plant in the Plant slot with `WaterLevel < 100`). Both tests are pure and nothing runs between them, so the
  cheap test goes first; the Soil branch then sets `WaterLevel = 100` itself (OnSprinkled's whole body, non-virtual). A Sim
  with no BuffManager keeps EA's order. Fires keep EA's order (`HasLeafPileAtLocation` is not cheaper).
- How often: once per 10 sim-minutes of rain; cost is 2 natives per Sim and Soil in the world. `result` prints one shelter
  test vs one on-fire test in us and the Sim/Soil counts. `run` compares both predicates on every live Sim and Soil.

### §5 `pondclearscan` (fixes 2142-2145, `PondClearScanCheck`) — exact speedup
- [verified, decompiled] `TerrainCoverManager.UpdatePondFreezing` returns early on every SeasonsManager tick while
  `mPondState == TryingToMelt`, the melt rate is > 0 and `AllPondsClear()` is false; each call is a world
  `GetObjects<LotPond>` before the one-field world-pond test.
- Fix, same answer: world pond `UseCount` first, then the lot pond the last scan found in use (still registered:
  `Simulator.GetProxy(ObjectId) != null` [inferred = in GetObjects; `run` compares with EA's scan live]); EA's scan only when
  neither is in use. How often: every Seasons tick (per sim frame) only while ponds try to melt with a skater on them.

### §6 `soakedstat` (fixes 2146-2148, `SoakedStatCheck`) — bug, persisted stat (opt-in)
- [verified, decompiled] `SimTemperature.AddSimTemperatureBuffsIfNecessary` re-adds Soaked (`Origin.FromRain`) to every Sim
  outside in rain on each environment update (every 2 sim-min), and `BuffSoaked.OnReAddition` increments TombStats "times
  soaked" each time: ~30 per hour of rain. First soak still counts (`OnAddition`); a rain re-add does not; other re-adds (a
  trap or a balloon on a soaked Sim, EP1's case) count as before. Counted it a bug: the stat is "times soaked", and only the
  later Seasons rain loop turns one soak into dozens.

### `envscoreshelter`: not built
- [verified, decompiled] `SimUpdate.CalculateSimEnvironmentScore` ends with `flag6 || IsShelteredFromPrecipitation(mSim)` and
  `AddSimTemperatureBuffsIfNecessary` reads it only when outside (all three uses). How often: per Sim, toddler and up, every
  2 sim-minutes (`mTimePassedEnvironment >= 2`) with motive decay on and Seasons enabled; the saving is the 2 natives for
  each Sim indoors (skipped already when `IsProtectedFromTheSun`). A lazy test needs a copy of the whole ~500-line method
  plus three private helpers (`DoSimEnvironmentUpdateInternal` ~230 lines) called through reflection per neighbour, which
  costs more than it saves, and the method already carries two swaps (`unseenenv`, `autoffenv`). Left as a lead.

## Scanner queue (45 keys, all marked)
1 real (WeatherStone listener: astgrep + lifecycle keys, §2), 43 false positives, 1 duplicate of a built fix
(SnowWaterFight alarm: arena `DoEndOfGame` removes it; join-wait is `snowfightjoinwait`). Reasons in short: HolidayManager
alarms re-set only after `ClearAllAlarms` or inside their own callback; listeners die with the world's EventTracker;
`Anniversary` / wedding alarms are removed in `LeaveJobNow` / relationship code; DeepSnowEffectManager removes its listeners
in `Dispose`; LeafPile / Snowman / SnowAngel broadcasters and VFX are killed in `Cleanup` / `Dispose`; `Lot.ChangeSeason`
re-arms from its own alarm and the 3 AM kick runs once per lot per night; Umbrella's fx alarm is removed in `OnExitPosture`;
dynamic-challenge listeners are rebuilt per load; Roslyn IDISP / ReSharper hits are style.

## Hot paths read (leads; §3-6 built, envscoreshelter not built: Built (2))
- `SeasonsManager.Simulate` runs every tick: 5 managers + `WeatherControl.SendSettings` (new `float[23]` + SafeArray icall).
  [verified, Ghidra 0x7846E0] the icall converts, applies a mode at `obj+0x3DC`, copies 23 dwords under a lock to host
  `+0x924` and sets dirty `+0x9A0`. Skipping unchanged sends is not provably exact (mode and other writers), small gain.
- `Simulate` catches only `ResetException`: any throw in a manager stops weather for the session. One such throw:
  `RainEvent.PutOutFires` reads `Firefighter.sFirefighterDictionary` without a null test (null in worlds without the
  firefighter service). Also there: every 10 sim-min of rain, all Sims and all Soil get the two-native shelter test before
  the cheap on-fire / water-level test (reorder would be exact; hook needs the private `UpdateData` struct; small).
- `SimUpdate.CalculateSimEnvironmentScore` computes `IsShelteredFromPrecipitation(mSim)` for every Sim, used only when outside.
- `PondManager.AllPondsClear` (world `GetObjects<LotPond>`) every tick while ponds try to melt with a skater on them.
- `BuffSoaked.OnReAddition` increments TombStats "times soaked" on every rain re-add (cosmetic).
- Log (phase-20261116-0234): besides MopPuddle, only 3 household `AutonomousSplashInPuddle` NoDestination fails
  (puddlereach) and 3 `SnowWaterFightWait` NPC route fails; nothing new.

## In-game results (Bridgeport, 2026-11-16, one load per session, each check switch off then on)

| Switch | Session (run) | Off | On | Measured |
|---|---|---|---|---|
| `initstacktrace` | s61 (phase-20261116-1303) | BROKEN | FIXED | stack walk 2 us, one instance 37-46 us; on arm: 71 `Init` / 45 `OnAddedToQueue` walks skipped |
| `weatherstonelistener` | s61 | BROKEN | FIXED | no stone summoned in the run (counters 0) |
| `weathermanagerthrow` | s62 (phase-20261116-1409) | BROKEN | FIXED | dictionary live in Bridgeport (no throw to skip) |
| `rainsheltercheap` | s62 | BROKEN | FIXED | live Sims / Soil answer as EA 115-121 of 115-121; shelter test 1-2 us, on-fire test 0-1 us |
| `pondclearscan` | s62 | BROKEN | FIXED | same answer as EA's scan; world scan 558-573 us vs shortcut 0 us |
| `soakedstat` | s62 | BROKEN | FIXED | no rain in the arm (counters 0) |

- [verified] Each verdict, controls ok and members found, from the check lines in the run's `smallfix.txt`.
- [verified] The timings in the table (the checks' `timing:` fields, one sample each).
- [inferred] The live effect of `weatherstonelistener`, `weathermanagerthrow` and `soakedstat`: the run had no case
  (counters 0). The checks test the same decision the hooks call, plus the live path (swaps in place, handles usable).
- `initstacktrace` is exact but small: 2 us per walk.
