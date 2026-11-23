# 868: firefighter emergency "free N Sims" / dig-out count (`firecount`)

2026-11-22, static (EA code read with a decompiler, local only), built + offline selftest, default off, NOT RUN in game.
Follows docs/570 §5 (`firetrapcount`), whose side effect note (a passer-by's rescue takes a real victim's task) this
fixes. Switch `firecount` (tuning `kFireCount`), `mods/ts3fixes/src/FireCount.cs` + `FireCountCore.cs`.

## 1. How a rescue reaches the count

- [verified] The count is the job's pending `TrappedSims` / `TrappedRubble` tasks (`Job.mPendingTasks`).
  `FireFightingJob.SetupTask` adds one per Sim it traps and keeps the Sims in `mTrappedSims` / `mRubbleTrappedSims`;
  `FindSimToTrap` never picks a Sim already in either list, so a Sim is in at most one.
- [verified] Every rescue calls `Occupation.SimCompletedTask(actor, task, null)`, with a **null target**:
  `FirefighterEmergencySituation+ConvinceToRun.Run`, `+CarryOutChild.Run` and `+PlaceSimDown.InteractionDone` (the
  carry-out of a toddler or baby) for `TrappedSims`; `FireRubblePile+ClearFireRubble.Run` for `TrappedRubble` (the
  caller list of `SimCompletedTask`, from the managed call graph).
- [verified] `SimCompletedTask` sends an `OccupationTaskEvent`; `EventTracker.SendEvent` dispatches it synchronously.
  `Occupation.OnSimCompletedOccupationTask` acts only for its owner Sim: it pays the task's XP and cash, then calls
  `CurrentJob.OnTaskComplete(task, target)`. `Job.OnTaskComplete` removes one pending task of that id and completes the
  job when none is left. Nothing looks at who was freed.
- [verified] The three `TrappedSims` rescues call `SetVictimHasBeenConvincedToRun(target)` right before
  `SimCompletedTask`, with no yield between (ConvinceToRun's `AddExitReason` in between has no path to a yield in the
  call graph). The dig marks nothing first; its pile is removed from `RubblePiles` (and the
  Sim set `ConvincedToRun`) only when the pile is destroyed. `DestoryTargetIfSafe` destroys it on the second of its two
  calls: the fade-out callback and the interaction's `Cleanup`. [inferred] `Cleanup` runs after `Run` returns, so the
  pile is still in `RubblePiles` when the task completes. fireleftover's note "the dig destroys the pile before
  `SimCompletedTask`" (FireLeftoverCore.cs, not on main) does not match this reading.

## 2. Where the count goes wrong

| Row (audit) | What happens | Effect |
|---|---|---|
| 2-3 | [verified] `Panic.InteractionFail` sets a convinced Sim whose run fails indoors back to `TrappedInRoom`: a rescued victim, or a passer-by who joined through `OnSimLotChanged`. A firefighter who enters the lot later gets Convince to Run for them, and freeing them takes a `TrappedSims` task. A passer-by has no task; a rescued victim's task was already taken. | The count drops below the real trapped Sims. If it was the last task: the success notice while a victim is still locked in, then (LargeFires) `CleanUpJob` unlocks the doors |
| 5-6 | [verified] `FireFightingJob.SimDied` (only caller: `FirefighterEmergencySituation.DelayReaper`, from `Urnstone.KillSim.Run`) runs for any Sim of the two lists, freed or not (the lists are never pruned). It removes one pending task with `PendingTasks.Remove`, never `OnTaskComplete`, sets `bSimHasDied`, and sets `bFailedJob` when no teen+ killable Sim is left in that list (`bFailedJob` then makes later deaths return at once). | A freed Sim's death takes a real victim's task. The last trapped Sim's death leaves the job at 0 pending, never completed, until the timeout (failure notice, XP loss) |
| 7 | [verified] A rubble victim reset mid-emergency: `Panic.AddSimInteractionOnReset` -> `AddRubbleTrappedPerson` makes a second pile for the same Sim; the first stays. Each dig takes a task. | One victim can take two tasks |
| 8 | [verified] `firetrapcount`'s `FtcLeft` drops the task of a rubble victim who leaves the lot but leaves its pile; digging that pile takes another victim's task. | Count too low |
| 9, 12 | Not in scope: a child aging to teen mid-emergency; a second task listener (trigger not verified). | |

## 3. Fix

Hook points (three method swaps, all with stubs):

- `FirefighterEmergencySituation.SetVictimHasBeenConvincedToRun` (static): notes the Sim, the running script task
  (`Simulator.CurrentTask`) and the tick, then runs EA's body.
- `FireFightingJob.OnTaskComplete` (`FcHook.TaskHook`): the narrowest point that sees each completion once per job.
  The event target cannot be used (always null), and the listener `OnSimCompletedOccupationTask` also pays the task, so
  skipping there would change pay. For a `TrappedSims` / `TrappedRubble` completion of a job with a situation:
  - `TrappedSims`: the freed Sim is the one noted on this task in this tick (consumed once);
  - `TrappedRubble`: the digger is the Sim whose task runs (`GameObject.GetObject<Sim>(Simulator.CurrentTask)`), the
    pile is its current interaction's target, and the situation's `RubblePiles` (pile -> Sim, filled by
    `AddRubbleTrappedPerson`) gives the pile's Sim. A pile whose Sim is gone counts as not listed;
  - `FcCore.Rescue`: not placed (no note, not on a digger's task: firetrapcount's and fireleftover's drops) -> EA's;
    a Sim of the job's list not yet accounted -> EA's, and the Sim is accounted; anyone else (a passer-by, a re-trapped
    Sim, a second pile, the pile of a Sim whose task was dropped) -> the task stays. The freed Sim is still freed and
    the owner still gets EA's task pay (the listener pays before `OnTaskComplete`).
- `FireFightingJob.SimDied` (`FcHook.DiedHook`): copies both task lists, runs EA's body (so `bSimHasDied` /
  `bFailedJob` are exactly EA's), and when EA took a task puts the lists back; then, for a Sim not accounted yet, drops
  one task as `FtcLeft` does: `Tasks.Remove`, then EA's `OnTaskComplete` (with our own hook passing it through), so the
  job completes when nothing is left. "Not accounted" is a Sim still trapped, or one freed by a rescue that never
  reached the job (another firefighter's: the listener ignores any Sim but its owner [verified]), whose task is still
  pending; a victim state test alone would keep that task forever. A Sim whose rescue counted (freed, or re-trapped
  afterwards) takes no task. When EA took nothing (`bFailedJob` already set, or not a listed Sim) nothing changes.
  [verified] `bFailedJob` is read only by `SimDied` itself, so the job's end is the timeout in EA and `OnCompletion`
  with the fix; the grade still turns F past grade D time because `bSimHasDied` stays set.

Accounting: "accounted" is firetrapcount's per-job list `sFtcDropped` (job, Sim id; 64 entries, cleared at world quit,
never saved). firecount adds a Sim on its first counted rescue and on a death it drops; `FtcLeft` adds a Sim whose task
it dropped. So:

- pile to victim: each pile maps to its Sim through `RubblePiles`, and a Sim counts once, so a second pile, or a pile
  of a Sim whose task `FtcLeft` or the death drop took, takes no task (rows 7 and 8);
- after a load the list is empty, so the next rescue of each listed Sim counts and a listed Sim's death takes one task,
  as EA's would (a passer-by is still not counted) [inferred: the job's lists are saved with the job; if a list came
  back null, every completion is EA's].

With the switch off EA's code runs as is; the hooks only count the cases they would change (2360). Household: an EA bug
fix; it acts on the household firefighter's job and on household victims, but only the job's task count changes,
never a Sim. Nothing is saved. World size: everything is per job and per situation, nothing depends on world bounds.

## 4. Switch, counters, check, repro

- Switch `firecount`, tuning `kFireCount`, default off.
- Counters 2358-2361 (reserved by hand: `registry.ts reserve --contributor` finds no free block past 4089; free in this
  branch and in the open contributor branches, after firenre's 2354-2357): [2358] rescue completions not counted, [2359]
  deaths handled (a freed Sim's death kept the count, or the last trapped Sim's death completed the job), [2360] cases
  seen with the switch off, [2361] errors.
- Check `Ts3Fixes.Fixes.FireCountCheck`: `run` (layouts through `FcCore`; off `verdict: BROKEN`, on `verdict: FIXED`;
  cases: a passer-by's rescue, a freed Sim's death, the last trapped Sim's death, a second-pile dig; controls: a listed
  Sim's first rescue takes one, a still-trapped Sim's death with another left takes one, an unplaced completion takes
  one), `scan` (the live emergency: pending `TrappedSims` / `TrappedRubble` against listed Sims still trapped and not
  accounted, trapped Sims not in the job's list, piles with no trapped listed Sim behind them; `MISMATCH` when the
  counts differ), `result` (counters).
- Repro `fire-count` (run, scan, result, off then on). Offline: `node tools/selftest/firecount/run.ts`.
- End to end (not run): testingcheatsenabled, a LargeFires job (debug Spawn Job, docs/570 §5) with two trapped Sims;
  free one, send a passer-by in and let its run fail (or reset a freed victim inside), free that Sim: off 2360 +1 and the
  count drops; on 2358 +1 and `scan` still matches.

## 5. Interaction with other fixes

- `firetrapcount` (on by default): shares `sFtcDropped`, so no task is dropped or counted twice. firecount skips a Sim
  `FtcLeft` dropped (its piles too); `FtcLeft` skips a Sim firecount counted, which also closes a firetrapcount double
  drop: a rescued victim re-trapped by `InteractionFail` who then leaves the lot. `FtcLeft`'s own `OnTaskComplete` call
  is not placed by firecount (no note on that task, not a digger's task) and stays EA's [inferred: a note made on the
  same task in the same tick would need a run start and a lot change in one tick].
- `fireleftover` (contributor branch, docs/862, not on main): it compares pending rescue tasks with the job's listed
  Sims still trapped on the lot. A **listed** Sim re-trapped after its rescue (row 2-3) counts there as trapped although
  its task is gone, so a real leftover can stay hidden until that Sim is freed again; a passer-by is not in the job's
  lists and is not counted [verified by reading `FloStillTrapped`]. firecount does not change that count. Its drops go
  through `OnTaskComplete` from an alarm, which firecount leaves to EA. Not edited here.
- `firedoorkeep` (docs/864) and `firenre` (docs/867) swap `LockDoorsInRoom` / `CleanUpJob` and `OnCompletion` /
  `ShowOccupationTNS`: other methods, no overlap. A job firecount completes after a death runs EA's `OnCompletion`,
  so both still apply.

## 6. Not verified

- In game: nothing (NOT RUN). The row 2-3 trigger needs a failed run outside; the scan shows it when it happens.
- [inferred] `Simulator.CurrentTask` is the digger's own task while `ClearFireRubble.Run` runs, and `CurrentInteraction`
  is still the dig (the repo already relies on `Simulator.CurrentTask == sim.ObjectId` for a Sim's own task).
- [inferred] `Cleanup` of the dig runs after `Run`, so the pile is still mapped when the task completes (§1).
- [inferred] Nothing between `SetVictimHasBeenConvincedToRun` and `OnTaskComplete` yields in practice. The call graph
  finds a path from `EventTracker.SendEvent` to a yield through some listener type, and the task pay runs first; a
  yield there would move the tick, the note would not match, and that rescue would count as EA's (the safe side). The
  tick check is kept on purpose: it stops an old note on the same task from catching `FtcLeft`'s later drop.
- [inferred] An alien firefighter: the death drop goes through `Job.OnTaskComplete`, which applies EA's alien
  brain-power change for a task, as `FtcLeft`'s drops already do.

## Import 2026-11-23 (maintainer)

PR #646 by Dalexlol, counters 2358-2361 kept. `firecount` is **ON by default** at import: his harness repro `fire-count` (pass 13, Isla Paradiso) is off BROKEN / on FIXED for all four cases (passer-by rescue, the freed Sim's death, a second pile, the last death not completing the job), controls kept, errors 0, soak passed. The live-emergency `scan` had no emergency and is not covered. It only changes a firefighter job's task count, never a Sim; it builds on `firetrapcount` (ON on master). Interaction with `fireleftover` (PR #638, ON in the 637-644 import) unchanged. Repro `fire-count` added to repros.json.
