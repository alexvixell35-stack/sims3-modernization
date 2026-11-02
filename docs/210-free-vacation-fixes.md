# 210 Generations free vacation: seven EA defects (issue #97)

Issue #97 (contributor dalexlol) lists seven defects in the Generations "Free Vacation!" trip: the adults leave for two
days, the teens stay home, and on return the adults' Sims are re-created. This doc checks each claim against the
decompiled scripts (`Sims3GameplaySystems`: `ParentsLeavingTownSituation`, `FreeVacationPickupSituation`,
`OpportunityHistory`, `OpportunityManager`, `Sim`, the `EventListener` family) and records our fixes in
`mods/ts3fixes/src/EaVacation.cs`. Rodney's rule (2026-10-30): EA bugs get our fix too, default off, counters, self-check,
repro. The fixes are written from our own reading of the game code. No code came from the issue (it only describes its
changes) or from NRaas.

Tags: **verified** = read in the decompiled code (the path and the unchecked read are both there); **inferred** = follows
from the code but not seen by us in a run; **community** = the reporter's run evidence. None of this was run by us yet.

## 1. How the trip works (verified)

- The dialog's yes (`TryShowingDialogForVacation`) calls `ParentsLeavingTownSituation.Create`. The constructor fills the
  `[PersistableStatic]` dictionaries `sAdultsInventories` / `sAdultBotsTraitChips` (keys = adult description ids) and enters
  `ParentsAreGoingAway`. That state sets `ParentsAreGettingPickedUp = true`, creates `FreeVacationPickupSituation` and
  enters `ParentsAreAway` at once.
- The pickup's taxi drives up and the adults get in. Its `Dispose` (not cancelled) stores each adult's inventory
  (`DestroyInventoryAndStoreInList`) and bot chips, then calls `Sim.Destroy()`, then clears `ParentsAreGettingPickedUp`.
- `ParentsAreAway` alarms: reminder at 2 days minus 2 h, then `ParentsNeedToReturn`. Early return, police and the
  `kSelectableSimDied` listener lead there too. Then `ParentsAreBack.Init` runs `BringAdultsBackToWorld`, which runs
  `InstantiateOffScreen` per stored adult, restores the inventory and chips, calls `ResetStatics()` and pushes `GoHome`.
  `OnGetHome` is the only `Exit()` of that state.

## 2. Verdicts

| # | Claim | Verdict | Evidence |
|---|---|---|---|
| 1 | The pickup destroys the adults without exporting dreams, opportunities or travel buffs; `SetupSelectableSimManagers` honours `DnPExportData` only in a travel world or for a Passport Sim | **verified** | `FreeVacationPickupSituation.Dispose`: inventory and chips only, then `Destroy()`. EA's travel paths (`GameStates` early departure, `TravelUtil`, Passport) set `DnPExportData`, call `StoreOpportunitiesForTravel` and `StoreBuffsForTravel`. `Sim.SetupSelectableSimManagers`: `IsAnyTravelBasedWorld()` or `Passport.IsPassportSim`, else `CreateAndIntitForSim` and the export is nulled. `Sim.OnCreation` already reads `TravelBuffs` back, and `NeedsOpportunityImport` already triggers `TravelFixup`, so the return needs only the export plus the dream branch |
| 2 | `StoreOpportunityForTravel` reads its category slot unchecked; a null slot throws and stops the pickup's `Dispose` before `Destroy()` | **verified** (read), trigger **inferred** | `mCurrentOpportunities[(int)op.OpportunityCategory]` is dereferenced with no null check. `CancelAllOpportunities` removes slots when `NeedsOpportunityImport` is false, and only `OpportunityManager.Fixup` (load) re-adds a missing one. We did not find an EA path that leaves an opportunity in the manager with an empty slot in play: mods or a missed `Fixup` are the likely sources. Second finding: `ListenerStates` is a fixed `new EventListenerExportInfo[3]`, so a store with more than three listeners throws `IndexOutOfRange` [inferred: no opportunity counted]. Stuck aftermath: the flag stays true, `ParentsNeedToReturn` finds no pickup and returns, and the trip never ends |
| 3 | `BringAdultsBackToWorld` re-creates dead adults and adults who left the household | **verified** (read), NRE **community** | The skip rule is only: no description, a live Sim, or a bot without chips. Second finding: if every stored adult is skipped, no `GoHome` is pushed, so `OnGetHome` never runs and the trip stays in `ParentsAreBack` [verified] |
| 4 | Deleting the teen's Sim while the parents are away runs the whole return inside the teen's `Dispose` | **verified** (path), the throw **community** | `Situation.NotifyParticipantDeleted` -> `ParentsLeavingTownSituation.OnParticipantDeleted` -> `ParentsAreAway.ParentsNeedToReturn` -> `SetState(ParentsAreBack)` -> `InstantiateOffScreen` while the deleted Sim is still being disposed |
| 5 | An `Exit()` while the parents are away resets the statics with nobody brought back | **verified** | `ParentsLeavingTownSituation.CleanUp` calls `ResetStatics()` unconditionally. `Situation.Exit` cleans the leaf (`ParentsAreAway`) first, then the root |
| 6 | The import's `ListenerStates[i]` read can be null and `RestoreState(null)` throws | **verified** | `AcceptOpportunityFromTravel`: `exportInfo.ListenerStates[num++]` once per listener `SetupOneListener` makes now. The existing-opportunity branch of `AddOpportunityFromTravel` reads `[i]` per current listener. `EventListener.RestoreState` reads `info.IsCompleted`, and its overrides (`CountListener`, `CountListenerDistinct`, `CountListenerExclusive`, `TotaledListener`) read `info` again after `base` |
| 7a | `OriginalTarget` keeps the object id from acceptance; `TryResumeOpportunity` moves `TargetObject` to a re-created Sim but not `OriginalTarget`, so the import drops the opportunity | **verified** | `Opportunity.TryResumeOpportunity` sets `TargetObject` only. `AddOpportunityFromTravel` prefers `OriginalTarget.ObjectId` in the same world (`ConvertGuidToObject`), and a null result with a valid id and the same world goes to `mOppsRemovedDuringTravelBecauseOfNullSourceOrTarget` (dropped). Any townie whose Sim object was re-created since acceptance hits this, with or without `TryResume` |
| 7b | `ExpirationTime` is exported from `TimeoutTime`, set only for time-based timeouts, so every other opportunity imports as expired | **verified** | `mTimeoutTime` is set only through `SetTime(.., bTimeout: true)` from the time-based branch of `CreateTimeoutListener`. Otherwise it stays `default` (tick 0). `AcceptOpportunityFromTravel` ends with `if (exportInfo.ExpirationTime < SimClock.CurrentTime()) FailOpportunity`. This also affects EA's own vacation return |

## 3. Fixes (all default off, one switch per row)

| Switch | Hook | What it does |
|---|---|---|
| `vacexport` | `FreeVacationPickupSituation.Dispose`, `Sim.SetupSelectableSimManagers` | Before EA's body: for each adult it will destroy, run EA's travel export (`DnPExportData`, `StoreOpportunitiesForTravel`, `StoreBuffsForTravel`), each step in its own try. At re-creation: an adult who is a key of `sAdultsInventories`, has an export and has no dream manager gets `SetUpDnPManagerFromExport()` before EA's body, so EA keeps it. If that throws, the manager is nulled and EA makes a fresh one |
| `vacstoreguard` | `OpportunityHistory.StoreOpportunityForTravel` + world load | A missing slot is re-added the way `OpportunityManager.Fixup` does it (`AddCurrentOpportunity` with the target Sim). If EA's store still throws, only that slot is removed and logged (a half-written entry would import wrong). The rest store normally. **Load repair** for each trip with the parents away (`EaVacationCore.Repair`): first, household teens with a Sim rejoin. A pickup that died (flag still true, no adult in a pickup) with every adult still in the world ends the trip. A dead pickup with adults gone, no teen, or no `ParentsAreAway` alarm clears the flag and calls `BringParentsBack()` 60 sim-minutes later from a yielding, non-persisted alarm (the repair runs again at every load) |
| `vacreturnskip` | `ParentsAreBack.BringAdultsBackToWorld` | Before EA's body, a stored adult who is dead or no longer in the trip's household (and has no Sim) is dropped from the trip dictionaries. If EA's body throws, the trip `Exit()`s and the exception is rethrown (ErrorTrap still reports it). If nobody was going to be re-created, the trip exits after EA's body (EA would sit in `ParentsAreBack` forever) |
| `vacteendelete` | `ParentsLeavingTownSituation.OnParticipantDeleted` | A trip teen deleted while the parents are away only leaves the trip (`RemoveRole`, removed from `Teens`). 10 sim-minutes later (yielding alarm), household teens with a live Sim rejoin. With none left, `BringParentsBack()` runs. Other cases use EA's body |
| `vacexitreturn` | `ParentsLeavingTownSituation.CleanUp` | Before EA's `ResetStatics`, while the statics are set and the pickup is done: every stored adult who is alive, in the household and without a Sim is re-created on the home lot with the stored inventory and bot chips. Not while `GameStates.IsTravelling` or after `World.OnWorldQuitEventHandler` (quit flag, cleared at world load) |
| `vaclistenernull` | `RestoreState` on `EventListener`, `CountListener`, `CountListenerDistinct`, `CountListenerExclusive`, `TotaledListener` | `RestoreState(null)` returns false: the listener starts fresh. Each override is hooked too, because each one reads `info` after `base` |
| `vacimportfix` | `OpportunityManager.AddOpportunityFromTravel` | (a) `OriginalTarget` / `OriginalSource` ids in this world that no longer find a live object, or find a different Sim than the exported description's current Sim, are pointed at that Sim (`EaVacationCore.RefreshId`). An id that still finds a non-Sim object is kept. (b) `ExpirationTime` tick 0 is raised to `long.MaxValue` for the call only and put back afterwards. Real deadlines are still checked |

**Save data.** Our own state (deferred checks, quit flag, repair alarms) lives in memory only, and the repair alarms are
`DeleteOnReset`. `vacexport` writes EA's own fields (`SimDescription.DnPExportData`, `NeedsOpportunityImport`,
`OpportunityHistory` entries). EA's own travel writes the same fields, and a save made during the trip carries them.
`TravelBuffs` is `[Persistable(false)]`, so moodlets do not survive a save made during the trip [verified]. With the switch
turned off later, `NeedsOpportunityImport` still makes EA import the opportunities, and EA discards the dream export: both
are harmless. The save path changes, so the queue runs the save matrix.

**Differences from the reporter's design.** (1) Their store guard sets the import flag only when every opportunity was
stored. We keep EA's flag and drop the one bad slot, so the rest still come back. (2) Their row-2 load repair schedules a
bring-back for any trip without alarms. A dead pickup leaves `ParentsAreGettingPickedUp = true`, which makes
`ParentsNeedToReturn` a no-op, so we clear the flag first, or end the trip when the adults never left. (3) Row 3 also ends a
return that brings nobody back (found while checking row 3). (4) Row 6 hooks the four overrides as well as the base, because
each override reads `info` after `base`.

## 4. Counters (466-480, registry `fixes-466`)

466 adults exported, 467 dream managers from the export; 468 slots re-added, 469 stores that threw, 470 bring-backs
scheduled at load, 471 dead-pickup trips ended at load; 472 adults not re-created, 473 returns ended; 474 teen
deletions deferred, 475 teens rejoined, 476 returns from the check; 477 adults brought back at exit; 478 null listener
states; 479 ids refreshed, 480 expiry checks skipped. Agent `patches` names: `vacexport_exported` ... `vacimportfix_expiry`.
Log lines start with the switch name (`ts3native.log` via `ShortcutLog`).

## 5. Checks

- Self-check `tools/selftest/vacation` (`EaVacationCore`): every input combination of the adult verdict, id refresh,
  expiry and load-repair rules against the rule written out a second time, plus the issue's named cases.
- Agent check `Ts3Fixes.Fixes.VacationCheck(what)` through `reflect_call`: `info`, `maketeen` (ages up a child of the
  active household when there is no teen), `start` (skips the dialog: `Create`, as its yes does), `back` (EA's debug
  bring-back), `compare` (dreams / opportunities / buffs / inventory per adult before vs now, same session only),
  `teendelete`, `orphan`, `exit`, `stall` (removes the `ParentsAreAway` alarms), `repair`.
- Repros (`tools/harness/repros.json`, labelled cheat by `realism.ts`): `bp-vacation-roundtrip` (rows 1, 2, 6, 7: off arm,
  then on arm), `bp-vacation-exit` (row 5), `bp-vacation-teendelete` (row 4), `bp-vacation-orphan` (row 3), each with the
  on arm first wherever the off arm loses Sims, and `bp-vacation-saves` (row 2 load repair plus the save matrix: save while
  the taxi is on its way (the reporter's open case), new, overwrite x2, back-to-back, unpaused, two reloads with play,
  save after reload).
- Queue `tools/harness/queues/ea-vacation.json`: correctness and save matrix, no timing, 6 jobs (~85 min of game time,
  BP + IP), not run.

## 6. Open

- Rows 6/7 need an adult holding an opportunity at the pickup. The repro does not create one, so check the `start`
  line, and add an opportunity by hand on a test save if the counters stay 0.
- An opportunity with more than three listeners throws `IndexOutOfRange` in both the store (`vacstoreguard` drops that
  slot) and the import (not hooked: the read is mid-body of `AcceptOpportunityFromTravel`) [inferred: no tuning count].
- The `vacexitreturn` quit flag assumes `World.OnWorldQuitEventHandler` fires before `AutonomyManager.Shutdown` exits
  the situations [inferred]. If it does not, the adults are re-created during shutdown, which the reporter saw as harmless
  (run 107) [community].
- A trip teen who travels (world change) while the parents are away: EA's `CleanUp` loses the parents, and we skip
  while travelling. Not fixed.
- `Situation.GetSituations<ParentsLeavingTownSituation>()` at world-load finish is assumed to list the loaded trip
  [inferred]. `bp-vacation-saves` checks it.
- The reporter's side finding (phone chat portrait from a missing Everyday outfit, `Sim.DoOneRoundOfRemoteChat`) is not
  part of this issue. It is a candidate for `eanullguards`.
