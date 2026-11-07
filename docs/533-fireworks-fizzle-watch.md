# 533. A firework that fizzles resets every Sim watching it (`fireworksfizzle`)

Issue #166 build 23. When a firework fails to launch, EA asks each watcher's animation for a state the watcher's actor
does not have; the request waits for the engine's timeout and throws, and the watcher is reset. This doc describes the
bug, the ts3fixes hook (switch `fireworksfizzle`, off by default; counter 2061, registry `fixes-2061`; ledger §937) and
the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `FireworksBase.Watch.Run` enters the `FireworksBigBooms` state machine as actor `x` (`Enter`) and loops with
  `LoopDel` until an exit reason; its other states are `LoopAnticipate`, `LoopWatch` and `Exit` **[verified]**.
- `LoopDel` switches on the firework's state when it changes: `Lit` -> `LoopAnticipate`, `ActiveSuccess` ->
  `LoopWatch`, `ActiveFailure` -> `AnimateSim("LaunchFailure")` then `AddExitReason(ExitReason.Finished)`, `Finished`
  -> `AddExitReason(ExitReason.Finished)` **[verified]**.
- A launch fails with `FireworksBase.Tuning.kChanceOfLaunchFailure` (10 % of launches in the player's tuning). The
  watcher's actor has no `LaunchFailure` state: the request times out with `SacsTimeoutException` "Request timed out
  (actor = 'x', state = 'LaunchFailure')", and ErrorTrap resets the watcher **[verified live: four reports on
  2026-10-25, Bridgeport Acres Summer Fest]**. The launcher's own fizzle animation is a different interaction and is not
  affected **[verified]**.
- The reset costs each watcher the rest of what it was doing and the "watched fireworks" bookkeeping of `Run`'s exit
  (`OnFinishedWatchingFireworks`, `EndCommodityUpdates`).

## 2. The fix

`mods/ts3fixes/src/FireworksFizzle.cs`, hook `FireworksFizzleHook.LoopHook` swapped over `FireworksBase.Watch.LoopDel`
(`EaFixes.cs` patch list; `Watch` is a private nested class, so the type and the two fields are found by reflection).
With the switch on, when the firework is in `ActiveFailure` and the watch has not handled it yet, the hook does the rest
of EA's case without the state request: `lastState = ActiveFailure`, `AddExitReason(ExitReason.Finished)`. The watch
then leaves its loop and runs `Exit` as after a normal show. Every other state, or the switch off, runs the original.
Counter [2061] `fireworksfizzle_finished`.

## 3. Test

Repro `fireworks-fizzle-check`, queue `tools/harness/queues/fireworks-fizzle.json`, save Baseline-BP, **simler90's
UPDATE242 core without the player's patches** (the player's core carries build 23). The agent check
`Ts3Fixes.Fixes.FireworksFizzleCheck` makes a firework out of world (the first `FireworksBase` among the instance names of
the Shopping XML's `FireworkBundles` table, EA's own list of what it sells) and a `Watch` instance on it for the
active Sim (made through `Watch.Singleton`, never run), sets the firework to `ActiveFailure` and the watch's `lastState`
to `Lit`, and calls `LoopDel` once. Off arm (`expect broken`): EA's path reaches `AnimateSim("LaunchFailure")` and
throws; the instance never entered a state machine, so here the throw is a `NullReferenceException`, not the timeout,
and no exit reason is added. On arm (`expect fixed`): no exception, `lastState` is `ActiveFailure` and the Sim has
`ExitReason.Finished`; `fireworksfizzle_finished` reaches 1. The check removes the exit reason it added and destroys the
firework. Early gate: the counter at 3 min.

Shortcut: the off arm shows that EA's path makes the state request, not the timeout itself. A real fizzle needs a lit
firework, a watcher and a forced failure (`kChanceOfLaunchFailure` = 1); the harness has no step for it.

Result, first run (2026-11-05, pure UPDATE242 core): the check never reached the hook: `CreateObjectOutOfWorld("FireworksBigBooms")`
made nothing (`FireworksBigBooms` is the class and state machine name, not an instance name). The check now takes the
names from `FireworkBundles`. The second run failed in the check too: its reflection call of the swapped `LoopDel` threw
`TargetException` in both arms (the swapped header names the hook's class). The check now runs the hook's own steps
(`FizzleFinish`, else the `LoopOrig` stub, which still names Watch). Third run (2026-11-05, same core): **PASS** in 11.2
min. Off: EA's path reached the state request (`NullReferenceException` on the missing state machine), no exit reason.
On: no exception, `ExitReason.Finished` added, lastState `ActiveFailure` (`fireworksfizzle_finished` 1).

## 4. What is left

- A real fizzle with watchers, as above.
