# 594. Seasons (EP8) hunt: scanner queue, hot paths, log (2026-11-16)

Static + RE only, nothing run in game. Two switches built (default off), the rest verified false positives or leads.
Already built for Seasons before this pass (not repeated): tempforecast, holidaylights, hauntedumbrella, snowfightjoinwait,
trickortreatalarm, npcseasonalparty, splashroof, puddlereach, mopunreachable, petpuddletile, seasonimposter /
seasonmarkercache / easpreadlots (docs/44, docs/74, docs/147), lightning lead (docs/593).

## Built

### §1 `initstacktrace` (fixes 1922-1925, `InitStackTraceCheck`, repro `initstacktrace-check`) — lag, every Sim
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

### §2 `weatherstonelistener` (fixes 1926-1928, `WeatherStoneListenerCheck`, repro `weatherstonelistener-check`) — bug, rare
- [verified, decompiled] `WeatherStone` holds the summon's `kWeatherStarted` / `kWeatherStopped` listener in
  `mWeatherChangedEventListener`; only the callbacks remove it. `Dispose` does not, so a stone destroyed mid-summon
  (`SetToDestroy`, build/buy delete) leaves a listener that keeps it alive and runs `AnimateChangeWeatherStoneState` on the
  disposed stone. Fix: remove it before EA's `Dispose`. Household-visible only through the stone (player summons).

## Scanner queue (45 keys, all marked)
1 real (WeatherStone listener: astgrep + lifecycle keys, §2), 43 false positives, 1 duplicate of a built fix
(SnowWaterFight alarm: arena `DoEndOfGame` removes it; join-wait is `snowfightjoinwait`). Reasons in short: HolidayManager
alarms re-set only after `ClearAllAlarms` or inside their own callback; listeners die with the world's EventTracker;
`Anniversary` / wedding alarms are removed in `LeaveJobNow` / relationship code; DeepSnowEffectManager removes its listeners
in `Dispose`; LeafPile / Snowman / SnowAngel broadcasters and VFX are killed in `Cleanup` / `Dispose`; `Lot.ChangeSeason`
re-arms from its own alarm and the 3 AM kick runs once per lot per night; Umbrella's fx alarm is removed in `OnExitPosture`;
dynamic-challenge listeners are rebuilt per load; Roslyn IDISP / ReSharper hits are style.

## Hot paths read, no fix (leads)
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
