# 222. Outfit waits that never end (#115, `outfitwaitpoll`)

Issue #115 (round 2, task 3) found one magical-makeover alarm on the day-406 save that spends all of its time in
`Sim.SwitchOutfitHelper.Wait`. It cost 3.0 ms of sim thread per sim-minute, in both LR arms. This doc explains why such
a wait never ends and describes the ts3fixes switch that makes it cheap. The switch is off by default. Status
(2026-10-31): built and self-tested, not run in game. Counters 607-609 (registry `fixes-607`).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`, method and IL offset given; no code reproduced
here); **[measured]** = from #115; **[inferred]** = our reading, not yet checked in game.

## 1. Why the wait never ends

All methods are in `Sims3GameplaySystems` unless named otherwise.

- `Sim+SwitchOutfitHelper::Wait(bool)` [verified]: it returns if `mDisposed` (IL_0001), calls `Start` if not `mStarted`
  (IL_0012), returns if `mNoChange` (IL_0018) and can call `Sim::LoopIdle` (IL_0031). Then it loops on
  `Simulator.Sleep(1)` until `mReady` is set (IL_0038-IL_0044). The loop reads no other field.
- `mReady` has two writers [verified]: `Ready()` sets it. `Start` passes `Ready` as the callback to
  `Sim::SwitchToOutfitDelayed`. `Abort()` clears it.
- `Abort()` [verified] sets `mAborted` and clears `mStarted` and `mReady`. If the helper is the Sim's active helper, it
  also clears `Sim.mActiveSwitchOutfitHelper` and calls `Sim::AbortOutfitDelayedSwap`, which cancels the delayed model
  swap (`CASUtils.AbortDelayedModelSwap`).
- So an `Abort` while a task is in the loop leaves it there: no callback for the cancelled swap sets `mReady` again
  [inferred: the callback was not traced in native code]. The task wakes every tick, 37.5 times per sim-minute
  (`SimClock.kSimulatorTicksPerSimMinute`) [verified], until the game closes.
- `Abort` has 12 callers (`tools/managed-callgraph.ts callers`) [verified]. They include `Start` of another helper on the
  same Sim, `Sim::DoReset`, `Dispose`, and the `Cleanup` of several interactions. `Wait` has 60 direct callers. One of
  them is `ChangeOutfit`, which has 40 callers of its own.
- The makeover path (`Sims3GameplayObjects`, `MagicMirror+MagicalMakeover`) [verified]:
  - `MagicalMakeoverDelegate`, an animation event, adds a one-sim-minute alarm with `ChangeOutfitCB` (AlarmType 1,
    DeleteOnReset).
  - `ChangeOutfitCB` calls `SwitchOutfitHelper::ChangeOutfit`, which calls `Wait(false)` while the helper is not ready.
    The alarm runs as a yielding alarm, in its own `AlarmManager+AlarmOneShot` task.
  - `Cleanup` aborts the helper and drops it, but it leaves the alarm in place. If the interaction ends after the alarm
    fired and before the swap was ready, the alarm task waits forever [inferred: the #115 case fits this].
- Open: how the stuck wait is present in both LR arms. Either the makeover state comes back from the save
  (`AlarmManager` is `IPersistable`), or a Sim starts it again soon after load [inferred, not checked].

## 2. The fix (`outfitwaitpoll`, `mods/ts3fixes/src/OutfitWait.cs`)

- **Switch off:** EA's `Wait` runs.
- **Switch on:** the hook runs EA's steps up to the loop in EA's order. It then polls `mReady` every tick for the first
  60 sim-minutes, and once per sim-minute (37 ticks) after that. It returns at the first poll that sees `mReady`, as
  EA's loop does.
- What the player sees does not change:
  - A swap that becomes ready in the first hour completes on the same tick as in EA's game.
  - A later swap completes less than one sim-minute after EA's.
  - A wait that never becomes ready never returns, as in EA's game, so `ChangeOutfit` never runs.
- Cost of a stuck wait: 53,280 wakes per sim-day become 3,600 (`tools/selftest/outfitwait`). That should bring #115's
  3.0 ms per sim-minute to about 0.08 ms [inferred: assumes a constant cost per wake].
- Not done: ending a stuck wait. If `Wait` returned without `mReady`, `ChangeOutfit` would go on to
  `Sim::DoOutfitDelayedSwap`, while the abort has already cleared the pending outfit [inferred]. Ending the wait safely
  needs a second hook that skips the rest of `ChangeOutfit`. That outcome also differs from EA's when a later `Start`
  makes the same helper ready again.
- The first stale poll writes a `ts3native.log` line. It names the Sim, `mAborted`, and the waiting task (for an alarm
  task, its callback).
- In-load A/B: the hook reads the switch at entry and again at each stale poll. A wait that started in an on arm drops
  back to one poll per tick in the off arms. A wait that started in an off arm stays in EA's loop.

| switch | tunable | default | counters |
|---|---|---|---|
| `outfitwaitpoll` | `kOutfitWaitPoll` | off | 607 `outfitwaitpoll_stale` (waits past 60 sim-minutes), 608 `_polls` (minute polls), 609 `_ended` (stale waits that later became ready) |

## 3. Checks

- **Self-test** (`node tools/selftest/outfitwait/run.ts`): a simulated wait loop, ready at a random tick or never. The
  off arm polls every tick. The on arm matches EA up to the stale mark and is at most 36 ticks late after it.
- **Repro** `outfit-wait-stale-check` (Baseline-BP, `Fixes.OutfitWaitCheck`): it puts the helper into EA's
  abort-in-the-loop state on an active-household Sim. One test task runs `Wait(false)`. A second task aborts the helper
  after `Start` and before `Ready`. The `ready` step then sets `mReady`, as a late callback would. The Sim's outfit does
  not change in either arm.
- **In-load A/B on the LR save** (to do, needs the game): load with the switch on, then read `outfitwaitpoll_stale` and
  the log line's task name. Then run the `--ab outfitwaitpoll` arms and compare the `SwitchOutfitHelper.Wait` row of
  #115 (ms per sim-minute).
