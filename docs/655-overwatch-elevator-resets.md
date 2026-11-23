# 655. `owelevidle`: Overwatch's nightly elevator cleanup resets NPCs who are not riding

2026-11-19. Source: `phase-2026111*/ts3native.log` (`owreset:` lines, switch `owresettime`, HitchCause.cs), NRaas source
(Chain-Reaction/NRaas master: `NRaasOverwatch/OverwatchSpace/Alarms/CleanupElevators.cs`, `RecoverMissingSims.cs`,
`ResetCheck.cs`, `StuckCheck.cs`; `NRaas/Tasks/ResetSimTask.cs`, `NRaas/Helpers/Instantiation.cs`, `NRaasOverwatch/Overwatch.cs`),
decompiled Sims3GameplaySystems (`ElevatorInterior`, `ElevatorDoors`). Built, default off, NOT RUN. Managed only (ts3fixes),
slots 4150-4159, `kCounterSlots` 4160.

## 1. Symptom [measured]

- 858 `owreset: Perform` lines in the `ts3native.log` of the `phase-2026111*` runs with `owresettime` on. Each is one NRaas
  `ResetSimTask.Perform(sim, fadeOut)`: destroy the Sim, sleep, re-make it at home (`FixInvisibleTask.InstantiateAtHome`),
  50-85 ms of sim thread with sleeps.
- 698 of them are at hour 5 (Bridgeport runs), 160 at hour 3 (Appaloosa Plains and other worlds). Overwatch's alarm hour
  is 3, moved to 5 in a Downtown world (`Overwatch.cs`) [verified source].
- About 10 Sims a night, a different mix each night. In a 13-day run (`phase-20261116-0750`) only a few Sims repeat
  (Distinguished Vampire on days 2, 4, 9, 12; Lily Dog on 4, 7, 9, 12), most appear once.
- Most runs reset no one at the first 05:00 (day 1) and about 10 at the second (day 2).
- **All 858 lines say `fade True`.**
- The Sims reset in `phase-20261117-1045` (Bubbles and Hannah Ditto, Mr and Mrs Dink, Vampire ClanHead, Play Boy, Rafael
  Striker, clubby Vampire, Carrie Purser, Distinguished Vampire) all live in Bridgeport's elevator towers: Breach House,
  One Leo Center, Sanguine Arms, Bachelor Tower, Hieroglyph Condos, The Glass Pillar, Founders Keep (lot names from
  their `ufplansplit` lines). Play Boy was live and routing all day before his 05:29 reset (`ufplan` at tick 115826).

## 2. Not RecoverMissingSims [verified source]

`RecoverMissingSims.Perform(Household)` acts when `NumSims != AllSims.Count`, so only on members with no created Sim.
It calls `RecoverMissingSimTask.Perform`, which calls `Instantiation.Perform(desc, ResetSimTask.Perform)`. That only calls
the reset when the Sim already exists, and with `fadeOut` **false** (`reset(ths.CreatedSim, false)`). So none of the 858
`fade True` resets come from it. These Sims were not missing either: Play Boy was live before his reset.

## 3. Who calls `ResetSimTask.Perform(sim, true)` in the nightly alarm [verified source]

| caller | condition | fits? |
|---|---|---|
| `CleanupElevators` | every SimDescription in any elevator's `ElevatorPortalComponent.mAssignedSims` whose Sim exists | yes: all named Sims live in elevator towers |
| `ResetCheck` | only a Sim that had an `AgeUpNpcSituation` on two nightly checks in a row; `ValidAger` skips elders | no for Bubbles Ditto (elder); it may be some of the hour-3 pet and horse resets [inferred] |
| `StuckCheck.ResetTask.OnPerform` | its own task; patched by `HcOnPerformHook` when `kHitchCauseHooks` is on | no: 0 `owreset: OnPerform` lines |

So the Bridgeport 05:00 resets are `CleanupElevators` [inferred from the table: the names, the elder and the missing
OnPerform lines; no stack was logged].

## 4. Why the entries are there at 05:00 [verified, decompiled; leak path inferred]

- The entry door's `ElevatorDoors.OnPortalStart` registers the Sim (`RegisterSim`).
- The ride moves the entry to the destination floor's interior (`OnPortalStop`).
- Only these remove it:
  - the exit door's `OnPortalStart`;
  - `ElevatorOnRouteActionsFinished` while the Sim is still registered;
  - `OnPortalActionReset`;
  - `OnLaneUnlocked` of a destroyed Sim;
  - `ElevatorPortalComponent.OnReset`.
- `RegisterSim` also sets three routing flags on the Sim (`DisallowBeingPushed`, `ShouldIgnoreAllObstacles`, the
  `ForceWalk` walk style). `UnregisterSim` clears them.
- An entry whose Sim left the ride any other way stays. Examples: a Sim faded out and destroyed mid-route
  (`GoToVirtualHomeInternal`, which routes to the nearest elevator and then `FadeOut(destroyObject: true)`), or an
  interaction cancelled between the doors.
- The Sim is later re-made, and at 05:00 it is at home, often asleep. `CleanupElevators` still destroys it and re-makes it.

No ts3fixes code writes `mAssignedSims` (`Batch582bLogic.cs` only reads it, and only with `elevatorsavedlog` /
`elevatorresetriders` / `elevatorridertimeout` on, all off in these runs) [verified, grep]. Whether our unseen-route
shortcuts add stale entries is open: the switch-off census lines below answer that in the next run.

## 5. Fix: `owelevidle` (tuning `kOwElevIdle`, default off)

- Before Overwatch's `CleanupElevators.PrivatePerformAction` runs, a hook walks the same elevators (every
  `ElevatorDoors`' interior) and classifies each entry (`OwElevIdleCore`).
- **Riding**: the entry sleeps in `OnPortalStart`'s wait, has a live ride task, or its Sim is routing, is in the
  interior's use list or stands inside an elevator. Left to Overwatch.
- **Exempt**: `IsPlayerSim` (household, selected, unknown scope). Left to Overwatch: their result stays NRaas's vanilla.
- **Idle**: anything else. With the switch on, the entry is dropped with EA's `ElevatorPortalComponent.UnregisterSim`,
  which also clears the routing flags and frees the slot. Overwatch then never sees the entry, so it does not reset the Sim.
- With the switch off, idle entries are only counted and logged (`owelevidle: <Sim>, elevator <id>: idle (...), switch
  off, Overwatch resets it`). A plain run thus measures how many resets the switch would save.
- Overwatch absent or shaped differently: one applied-log line, nothing patched. Overwatch's code is not copied; the hook
  only runs before it.
- Counters: [4150] idle entries dropped, [4151] riding entries left, [4152] exempt idle entries left, [4153] idle entries
  seen with the switch off, [4154] errors, [4155] passes; 4156-4159 spare. Agent read-back `owelevidle_*`.
- Expected effect: about 10 fewer NPC resets per Bridgeport night (~0.5-0.8 s of sim-thread work with sleeps).

## 6. Checks

- Selftest: `node tools/selftest/owelevidle/run.ts` (with `TS3_CSC` on Linux): ok.
- Repro `owelevidle-check` (`tools/harness/repros.json`): Bridgeport after a sim-day. `census` is read-only, then
  switch on, `run` calls Overwatch's own `CleanupElevators.PerformAlarm`, `census` again, `result`. Expect idle > 0
  before, dropped = idle, idle 0 after.
- Live test: NOT RUN.

## 7. Upstream

- NRaas side: Overwatch resets idle Sims because `CleanupElevators` treats every assigned entry as a stuck rider. An
  upstream fix would unregister entries whose Sim is not riding, and reset only real riders.
- Open NRaas PRs checked 2026-11-19: none touches `CleanupElevators` (#45 spreads the nightly cleanups, other files).
- The PR is not sent yet: the switch-off census of one Bridgeport run should first confirm §3 and §4 live.
