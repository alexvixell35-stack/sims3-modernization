# 609: Game speed, speed lock, repeating-alarm and heartbroken-broadcaster fixes

2026-11-17, static, r66, s78, NOT RUN. Six EA bugs in `Sims3.Gameplay.Gameflow` (speed setting and the speed lock),
`LotManager` build/buy, `AlarmManager` and `SocialCallback.AddHeartBroken`, built as default-off ts3fixes switches in
`mods/ts3fixes/src/GameSpeed78.cs`. Each one is fixed the way the EA code next to it already works. RE was done with
tools only: `ilspycmd` on `game-copy/assemblies/*.dll` for the bodies, `node tools/managed-callgraph.ts callers` for
callers, and `node tools/swap-stack-audit.ts --recompute` for yield paths.

Household rule: the speed buttons, the speed lock and the alarm clock are global, so EA meant them to behave this way in
every household. These fixes have no household exemption, because the result is what EA intended. heartbrokenbcast also
applies to household Sims: it only disposes a broadcaster that EA's own code had already stopped referencing.

| switch | slots | hook (swap) | Check / repro |
|---|---|---|---|
| `speedskiplevel` | 3405-3407 | `Gameflow.SetGameSpeed(GameSpeed, bool)` (GfSpeedHook) | `SpeedSkipLevelCheck`, `speedskiplevel-check` |
| `speedskipnoactor` | 3408-3410 | same hook | `SpeedSkipNoActorCheck`, `speedskipnoactor-check` |
| `speedrecalclock` | 3411-3413 | `Gameflow.RecalculateGameSpeed` (GfRecalcHook) | `SpeedRecalcLockCheck`, `speedrecalclock-check` |
| `speedlockowner` | 3414-3416 | `LotManager.EnterBuildBuyMode`, `RealEstateManager.OnExitBuildBuy`, `Gameflow.UnlockGameSpeed` (BbLockHook) | `SpeedLockOwnerCheck`, `speedlockowner-check` |
| `alarmzeroperiod` | 3417-3420 | `AlarmManager.Timer.get_YieldRequired` (AzTimerHook, shared with the alarmtime stamp) | `AlarmZeroPeriodCheck`, `alarmzeroperiod-check` |
| `heartbrokenbcast` | 3421-3423 | `SocialCallback.AddHeartBroken` (HbBcastHook) | `HeartBrokenBcastCheck`, `heartbrokenbcast-check` |

Every Check has a pure part with controls (off BROKEN, on FIXED) plus the EpLive swap and member asserts. The alarm
Check also runs the fix on a private Timer that is never queued. The heartbroken Check replays EA's overwrite on the
selected Sim with two broadcasters, and disposes both afterwards.

## 1. Gameflow.SetGameSpeed (summary) [verified, ilspycmd]

`SetGameSpeed(GameSpeed requestedSpeed, bool lockSpeed)` has two switches:
- The first switch sets the time scale and `mGameSpeed`.
- The second switch sends `GameUtils.SetGameTimeSpeedLevel`, but it switches on `requestedSpeed` again (IL `ldarg.0` at
  IL_0095).

Skip has two branches that do not end at Skip:
- An active Sim with no head interaction falls back to Normal.
- No active Sim at all runs no branch.

Both swapped methods can reach a yield (`StateMachineClient.RequestState` through `GameSpeedChanged`), so their swap-stack-audit rows keep stubs.

## 2. speedskiplevel

- **Bug:** when Skip falls back to Normal, time runs at 1x but the engine is told level 3 (the Triple/Skip level).
- **Fix:** the level follows the speed that was set (`mGameSpeed`).
- **How:** in that case the hook hands EA's body `Normal`. EA's Normal path sets the same time scale, `mGameSpeed`,
  persisted speed and event as the fallback, plus level 1. With only this switch on and no active Sim, the level is
  re-sent after EA's body from the unchanged `CurrentGameSpeed`. That is literally "switch on mGameSpeed".

## 3. speedskipnoactor

- **Bug:** Skip with `Sim.ActiveActor == null` leaves `gameTimeScale` at 0 and `mGameSpeed` unchanged. Time freezes,
  but `CurrentGameSpeed`, the persisted speed and `GameSpeedChanged` still report the old speed.
- **Fix:** fall back to Normal, as the no-head-interaction branch next to it does. The hook hands EA's body `Normal`.

## 4. speedrecalclock [verified, ilspycmd + callgraph]

**Bug:** `RecalculateGameSpeed()` calls `SetGameSpeed(mGameSpeed, GameStates)`. That call skips the lock test and calls
`SetGameSpeed(.., lockSpeed: false)`. While a lock is held, this:
- clears `mGameSpeedLocked`;
- writes `sPersistedGameSpeed`, which is saved.

The only callers are `PlayFlowState.Shutdown` and `EditTownState.Shutdown`. The locks it can drop are the
ThumbnailHelper lot thumbnails, `MiniSimDescription.AddMiniSims` and `CASModeState.Startup`. The holder's
`UnlockGameSpeed` then does nothing, and the forced Pause is saved as the player's speed.

**Fix:** with a lock held, the recalculation re-applies the current speed as a locked set, by calling the live
`SetGameSpeed(speed, true)` through reflection. The lock stays and nothing is persisted, because EA's own `!lockSpeed`
test skips it. With no lock held, EA's body runs unchanged.

## 5. speedlockowner [verified, ilspycmd LotManager ~1398 / ~1508]

**Bug:** `EnterBuildBuyMode` locks Pause only when no lock is held (`if (!ClockSpeedModel.GameSpeedLocked)`).
`ExitBuildBuyMode` always calls `Gameflow.UnlockGameSpeed()`, so it releases another holder's lock and restores the
speed that holder saved.

**Fix:** build/buy unlocks only if it took the lock itself.
- `ExitBuildBuyMode` sleeps (`Simulator.Sleep(0)` first), so it is not swapped.
- Enter is wrapped to remember ownership: free before, held after.
- `RealEstateManager.OnExitBuildBuy` has one caller, `ExitBuildBuyMode`, and runs three statements before the unlock.
  Our hook arms the next `UnlockGameSpeed` on the same task.
- The `UnlockGameSpeed` hook checks that task and that `LotManager.sIsBuildBuyModeOn` is false. It then skips the unlock
  when the lock was not build/buy's.
- `OnExitBuildBuy` can yield (`PropertyData+Venue.OnExitBuildBuy`), but the arm is set after it returns. Nothing between
  that point and the unlock can yield: only field sets and `UnlockActiveLot`.

**Fix 2026-11-20 (mode switch) [verified in the scripts; live A/B `speedlock-modeswitch` queued]:** v0.3.0 shipped a regression here.
- A switch between Buy and Build (or Blueprint) enters build/buy again without an exit. `InWorldState` sets `BuyModeState.TransitionToBuildMode` (or `BuildModeState.TransitionToBuyMode`), and that state's `Shutdown` then skips `ExitBuildBuyMode`. The next mode calls `LotManager.EnterBuildBuyMode` again.
- That second enter finds the lock held, by build/buy's own first enter. EA correctly skips locking again, but the hook recorded "not ours".
- So the exit to live mode skipped the unlock. The game stayed paused, and every time control was dead (`SetGameSpeed` returns early while `mGameSpeedLocked` is set) until a restart. A player reported it.
- Fix: `BbOwns(lockedBefore, lockedAfter, modeSwitch, ownedBefore)`. An enter while `sIsBuildBuyModeOn` is already true keeps the ownership the first enter took.
- Player workaround on v0.3.0: save (saving does not touch the lock), then restart the game. The lock is not stored in the save.

## 6. alarmzeroperiod [verified, ilspycmd AlarmManager, SimClock]

### 6.1 Bug

`Simulate` gives a fired repeating timer its next time with:

```
p = ConvertToTicks(RepeatLength, UnitOfTime)
for (n = t + p; n < now; n += p) {}
```

`ConvertToTicks` truncates: 0.625 ticks per sim-second, with a `(long)` cast. A unit of 6 or 7 from the 3-bit field
gives 0. The Timer constructor only replaces a `RepeatLength` of 0 or less with 2 s, so:
- any period under 1.6 sim-seconds is p = 0, and the loop never ends: the game hangs;
- the same happens for a bad unit, or for a length set later by a mod or a bad save.

EA's own callers use 2 s or more. For p > 0, the loop takes one step per missed period.

### 6.2 Fix

`Simulate` is not swapped, because an inline alarm may sleep (docs/141, HitchCause alarmtime notes). Instead, the hook
uses the leaf `Timer.get_YieldRequired`, which `Simulate` reads once per fired timer after `RemoveAt(0)` and before the
loop. It moves the timer's due time so that EA's loop needs no steps:
- **p > 0:** t' = t + (K-1)p, where K = max(1, ceil((now-t)/p)). EA's loop then ends at t + Kp, which is identical to its
  answer from t.
- **p ≤ 0:** the period is treated as 1 tick. For a due t < now, a 1-tick period gives next = now. Setting t' = now - p
  makes EA's loop start at now and stop at once.

Timers that are not due are left alone. The reset and dispose paths read the getter only on timers they clear right away.

This getter used to be swapped by HitchCause's `HcTimerHook`, which existed only with `kHitchCauseHooks`. `AzTimerHook`
now carries that stamp: it calls `HcAlarmDue` when alarmtime is on. The swap is always in place, and costs one cached
`ShortcutOn` per fired alarm when both switches are off.

### 6.3 Shortcuts and leads

- The hook uses the time at the leaf as `now`. `Simulate` reads `now` once, before the pass. The two differ only if an
  inline alarm in the same pass sleeps, which is EA-wrong. In that case a p > 0 timer lands one period later than EA's
  stale answer, which was already in the past. [inferred]
- **Lead, not fixed:** `InternalFixLoadAlarms` (the load path) computes `(ticksToAdd - t) / p`, which throws
  DivideByZeroException at load for p = 0. It needs an IL patch (`native/ts3native/ilpatch-specs.h`) or a hook on
  `FixLoadAlarms`. It is not drafted, because no EA caller reaches it.

## 7. heartbrokenbcast [verified, ilspycmd SocialCallback, BuffHeartBroken, ReactionBroadcaster]

**Bug:**
- `AddHeartBroken` adds HeartBroken, then sets `HeartBrokenBroadcaster` to a new never-expiring `ReactionBroadcaster`
  on the dumper.
- If the Sim is already HeartBroken, the add re-uses the same instance, and the old broadcaster is dropped without
  `Dispose`.
- `BuffInstanceHeartBroken.Dispose` frees only the current broadcaster. The old one stays on the first ex's lot (in the
  reaction and active-broadcaster lists) until that Sim is destroyed, and keeps pulsing `HeartBrokenReaction`.

**Fix:** after EA's body, the broadcaster the instance held before is disposed if EA replaced it. Nothing else
references it, so this is the same as disposing it just before the assignment.

`AddHeartBroken` can reach `Simulator.Sleep` (BuffManager.AddElement), so its swap keeps a stub. `managed-callgraph`
lists no direct callers, because it is a social callback bound by name.
