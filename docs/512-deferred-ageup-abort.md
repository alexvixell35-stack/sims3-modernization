# 512. A deferred age-up aborted while the Sim is instantiated (`ageupabort`, issue #166 build 32)

An ErrorTrap report (issue #166, build 32) showed `NullReferenceException` in `AgingState.PreInstantiateSim`, called
from `Taxi.CreateDriver` (through `NpcDriversManager.CreateNpcDriver` and `SimDescription.Instantiate`): an NPC's taxi
home had no driver. This doc describes the ts3fixes switch `ageupabort`. Off by default. Counters 2037-2038 (registry
`fixes-2037`), ledger §910.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. The race

- **The wait** [verified, IL]. `SimDescription.Instantiate` calls `PreInstantiateSim(ref outfitKey)` for a description with
  an aging state (it is the method's only caller). When the age-up was deferred (`SimBuilderTaskDeferred`), the method
  starts the next-age outfit build (`StartSimBuilderTask`) and sleeps (`Simulator.Sleep(0)`) until the builder task is
  `Done`. Then it clears the deferred flag, takes the new outfit key and sets the next age.
- **The abort** [verified, IL]. `AgingState.AbortSimBuilderTask` stops the builder and sets the task field to null, unless
  a swap is pending. Its callers: the `AgingManager.Enabled` setter (aging turned off) and
  `AgingManager.CancelAgingAlarmsForSim` (docs/205 §1.1). Either can run in another task while `PreInstantiateSim` sleeps.
- **Then** [verified, IL]: the next read of the task's `Done` throws `NullReferenceException`. EA's catch stops the task if
  there is one and rethrows. `Instantiate` ends there and the Sim is not created.
- **Prior fixes**: none found in the NRaas assemblies we have (ErrorTrap and Traveler call `PreInstantiateSim` in their
  own instantiation paths, with no guard for this) or online.

## 2. The fix (`ageupabort`, `mods/ts3fixes/src/AgeUpAbort.cs`)

A hook on `AgingState.PreInstantiateSim(ref ResourceKey)`. EA's method runs unchanged inside a try/catch.
`AgeUpAbortCore.Decide` picks the outcome. A `NullReferenceException` while the task field is null and the age-up is
still deferred is counted [2037], also with the switch off, and rethrown when the switch is off. With the switch on it is
caught [2038]. The method then ends as it does for a Sim with no deferred age-up: the outfit key and the age are
unchanged, and the age-up stays deferred for a later instantiation or the regular age-up. `Instantiate` goes on with the
current outfit. That is what the player's core build does with a check in the wait. Every other exception passes.

- No loop can come of it [verified, IL]: `Instantiate` calls `PreInstantiateSim` once, with no retry around it. (docs/205
  §1.2 left a different throw of this method alone because of a loop in the travel merge. That loop does not call it.)

## 3. Checks

- **Self-test** (`node tools/selftest/ageupabort/run.ts`): every exception kind, task field, deferred flag and switch
  combination.
- **Repro** `age-up-abort-check` (queue `age-up-abort.json`, Baseline-BP, `Fixes.AgeUpAbortCheck`). It needs EA's method,
  so it runs with the original core mod (a core with the player's Build 32 leaves the wait when the task is gone). On a
  townie that is not instantiated (a lost race cannot age a Sim in the world), `arm` runs the race in a game task: it
  marks the age-up deferred, queues a one-shot task that calls EA's `AbortSimBuilderTask`, and calls `PreInstantiateSim`.
  The abort runs while the method sleeps. Then it puts the deferred flag back. `ageupkeepoutfits` (docs/205) is on in both
  arms, so the aborted build does not wipe the townie's outfits. `expect thrown` (off): EA's `NullReferenceException`,
  seen +1, age unchanged. `expect caught` (on): no exception, caught +1, age and outfit key unchanged. Each throws on a
  wrong outcome.
- **What the repro does not build**: aging turned off or an age-up cancelled by the game during a real instantiation.

## 4. Results

**Run 1** (2026-11-01, Baseline-BP, `bp-age-up-abort-check` PASS in 8.6 min, original core mod) **[verified, log]**:
on Shanita Jordon (Adult, not instantiated), both arms. `expect thrown` (off): EA's `NullReferenceException`, seen +1,
caught +0, age and outfit key unchanged. `expect caught` (on): the method returned, seen +1, caught +1, age and outfit
key unchanged. The arm puts the deferred flag back itself, so the run does not show that the age-up stays deferred.

## 5. The 2026-11-12 rethrow (paparazzo role NPC, rc1111c)

A stress job on the release pair (`freeze-stress-1112-01`, ts3fixes rc1111c, switch on) logged EA's
`NullReferenceException` out of the hook: `PreInstantiateSim` <- `Instantiate` <- `InstantiateOffScreen` <-
`RolePaparazzi.StartRole` <- `RoleManagerTask.FillInWorldRole`. The throw is the `Done` read after the sleep.

- **Why the hook let it through** [inferred from IL, not seen live]. With the task field null the only `Decide` input that
  gives `kPass` is the deferred flag already cleared (the switch is on: tuning true, the job sets no switch). In
  `Sims3GameplaySystems` the only write of `SimBuilderTaskDeferred = false` is `PreInstantiateSim`'s own success path
  [verified, IL], and `StartSimBuilderTask` hands an existing task to a second caller [verified, IL]. So a second
  `Instantiate` of the same Sim (both pass the `CreatedSim == null` check while the first sleeps) waited on the same
  build, woke first, cleared the flag, aged the Sim and nulled the task (`AgeTransitionCompleted`). The other waiter then
  read a null task.
- **Rethrow is intended**. `Instantiate` re-checks `CreatedSim` only before `PreInstantiateSim` [verified, IL]. Catching
  this case would let the second caller create a second Sim for the same description, with the pre-age-up outfit key on
  the new age's rig. EA's throw is what stops that, so the hook keeps it. A better outcome (return the Sim the other call
  made) needs a hook on `SimDescription.Instantiate`; not done.
- **Diagnostic** (bug-diag lifecycle): every rethrow from the hook now logs one line (first 5 per session, recent log):
  `ageupabort rethrow: <Sim>, <exception>, task null, deferred, switch, decision, created, scope`. A line with
  `deferred False` and `created True` confirms the reading above.
- **Self-test**: both 11-12 cases (still deferred: caught; deferred cleared: rethrown).
