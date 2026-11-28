# 562: Career and opportunity logic batch (static, built, proven in game)

**In-game status:** `privateeyecase`, `oppdeadlisteners`, `daycarequit`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-15. Scope: opportunity lifecycle (OpportunityManager add / accept / reject, Opportunity Cleanup, suspend and
resume, target-deleted and Sim-died handling, completion delegates), active careers (PrivateEye / Investigator,
Firefighter, Stylist, Architect, Daycare, Ghost Hunter, performance careers and gigs), job performance, rabbit-hole
work and school files. Method: both semgrep configs over the 310 career / opportunity / rabbit-hole files, then a read
of the listener lifecycles the hits pointed at and of each system's main Run / Cleanup paths. Combo-building parts
(docs/551) skipped.

## 1. `oppdeadlisteners` (fixed, off by default)

`Opportunity` [verified, decompiled]:
- `AddTargetDeletedListener`, for a Sim target, adds two global listeners: `mSimDiedListener` (kSimDied) and
  `mSimDescriptionDisposedListener` (kSimDescriptionDisposed), both `SimDiedDelegate`, Keep for every other Sim.
- `TrySuspendOpportunity` (target Sim uninstantiated) adds `mSimCreatedListener` (kSimInstantiated, Keep until that
  Sim is instantiated again).
- `Cleanup(bSuccess)` -> `RemoveAllIncompleteListeners` removes the event, completion, loss, timeout and
  target-deleted listeners, but none of those three; no other code removes them.

Effect: every Sim-target opportunity that ends (completed, failed, cancelled, timed out, or rejected in the accept
dialog, which also runs Cleanup) leaves two global listeners holding the dead opportunity and its Actor for the
session, run on every Sim death and SimDescription disposal. One that ended while its target was uninstantiated also
keeps a kSimInstantiated listener (run on every Sim instantiation); when that Sim comes back, `TryResumeOpportunity`
re-registers the dead opportunity's suspended listeners (its completion listener among them) and its target-deleted
listener.

Fix (`OppDeadListeners.cs`, counters 2495-2496): after EA's `RemoveAllIncompleteListeners`, the three are removed
(when not completed) and cleared. `WashDishesOpportunity` overrides it but calls the base, so it is covered. Check
`OppDeadListenersCheck`, repro `opp-dead-listeners`.

## 2. `daycarequit` (fixed, off by default)

`Daycare` [verified, decompiled]: each enrolled child has a `DaycareChildManager` with two global listeners
(kSimAgeTransition -> `ChildAgedUp`, kSimDied -> `ChildDied`); only `RemoveDaycareChild` runs its `Cleanup`.
`LeaveJobNow` reaches it only through `EndDaycareDay(IncompleteAndRemoveAllChildren)`, scheduled only while a workday
situation runs. Quit (or be fired) outside work hours and every manager stays with its listeners and the old Daycare:
when a former client ages up, `ParentWithdrawChildIfAgedUpToIneligible` shows the ex-owner a "child withdrew from your
daycare" TNS (and throws if the ex-owner is not instantiated, `OwnerDescription.CreatedSim`); a former client's death
runs `ChildDied` for a career that is gone.

Fix (`DaycareQuit.cs`, counters 2497-2498): in `CleanupDaycareOwner` (LeaveJobNow's first step; LeaveJobNow itself
is not swapped because `LeaveJobDispose.cs` invokes it by reflection), when no workday situation was running and no
child is in a toddler transport situation (EA's own test there; it keeps babysitters in that case), each remaining
manager gets EA's `Cleanup` and the table is emptied. Check `DaycareQuitCheck`, repro `daycare-quit-managers`.

## 3. Read and ruled out

- Opportunity target-deleted / suspend / resume: guarded (`mSimDiedListener == null` dedupe, `IsTargetDeleted`
  checks death style); completion delegates re-check the current active opportunity.
- Job (`mRabbitHoleDisposed`, `mSimEnteredSpecificLotListener`), Stylist (`mObjectDisposed`): set once per job, removed
  in ShutDown / Unregister.
- PrivateEye broadcasters (garbage, mail, break-in): created and disposed in Run, disposed again in Cleanup.
- CityHall / Theatre / ProSports / Homework broadcasters, Gig audience check, GhostHunter / AfterschoolActivity
  else-if chains, CareerManager / Occupation enumerators: no reachable bug.

## 4. Leftovers

Both former leftovers (PrivateEye mail door, garbage search after the case ended) are fixed in §5.

## 5. `privateeyecase` (fixed, off by default)

All five [verified, decompiled `PrivateEyeInteractions`]; one switch for the Investigator case family
(`PrivateEyeCase.cs`, counters 2499-2504, check `PrivateEyeCaseCheck`, repro `privateeye-case`).

- (a) `LookThroughMail.Run` reads `GetActiveOpportunity(Career)` without a null check, then
  `mailboxDoor = sim == null ? Target.GetAnyDoor() : Target.GetDoorOfSim(sim)` and `mailboxDoor.Slot`; EA
  null-checks the door only later. `MailboxWall.GetDoorOfSim` returns null when the case Sim's household has no door
  assigned there and the mailbox is not on that Sim's home lot (case Sim moved out, or lives in another building), and
  throws on a Sim with no household. Fix: no case -> the interaction fails before anything happens; during that Run a
  door lookup on that mailbox for the case Sim that would be null (or throw) returns EA's own no-Sim choice,
  `GetAnyDoor()`; a wall mailbox with no doors fails cleanly. The check also runs a read-only lookup on a real
  Bridgeport wall mailbox for an instantiated Sim from another lot (EA's MoveIn branch not taken, so no assignment
  changes).
- (b) `SearchThroughGarbage.Run`: after a successful 30-minute search, `GetActiveOpportunity(Career).TargetObject`
  is read without a null check; a case that timed out or was cancelled during the search throws after StandardExit
  and resets the Sim. Fix: that NullReferenceException, with the search succeeded and no career opportunity left, is
  caught and Run returns EA's result; only the blackmail tail (which needs the case) is skipped. No reward is
  involved: the dead case has no listeners.
- (c) `SnoopForClues.Run` adds the "snoop finished" alarm (AlwaysPersisted, `kTimeToSnoop` x level multiplier, up to
  two hours) without storing the handle, so `RemoveAlarm` never removes it. When the snoop ends first (loop lifetime,
  or the player cancels), the alarm still fires and adds `ExitReason.Finished` to whatever the investigator does then.
  Fix: `SnoopAlarmDelegate` acts only while that snoop is the Sim's current interaction.
- (d) `BreakIntoHouse`: Run sets the door to `tLock.Nobody`, Cleanup to `tLock.Anybody`, whatever it was before, so
  a household-locked front door stays unlocked. (Apartment doors: `FindApartmentDoor` only returns unlocked doors, so
  no change there.) Fix: the lock (type, owner, one-way room) of the door EA will pick (`FindApartmentDoor` /
  `FindFrontDoor`, deterministic) is read before Run and put back after Cleanup when Cleanup reset that door.

- (e) `Stakeout.InitiateCrimeToWitness` (WitnessCrime result, random or required by the case listener): with a
  required Sim id it calls `SimDescription.Find(id).IsValidDescription` with no null check, so a case whose required
  Sim was deleted (died and culled, left town) throws in Run on every try. With no candidate Sims at all it returns
  false but leaves the result WitnessCrime (EA sets Fail only when candidates existed), and `WaitForCrimeLoop`
  dereferences the null `Sketchy` on its first tick (its `Sketchy == null` test adds an exit reason but does not
  return). Fix: a required id with no description is cleared for good (the Sim can never match again; no
  clear-and-restore, so no window for a save or another writer) and EA's not-instantiated fallback runs; an id whose
  description exists is left untouched; no crime pushed and no Sketchy -> `Fail`, EA's own result. Counter 2504.

Second pass (case flow): case choice (`GetCaseHelper`, per-career day lists, so several investigators in one
household do not share them), `PlaceOpportunityCall`, `CaseCompleted` and the completion path in
`Opportunity.OnCompletion` (rewards come from the opportunity data; `CaseCompleted` only for the last step),
blackmail socials (`TestBlackmail`, `OnBlackmailSucceeded/Failed`), report writing (`UpdateReport`), computer
`OpportunityHack` and `Hack` (identity theft): no further confirmed bug. Target Sim moved out / died mid-case goes through the
generic opportunity target-deleted path (§1 covers its listener leak); a case on an apartment lot uses
`FindApartmentDoor` (first unlocked unit door, not necessarily the target's unit: EA design, left alone). No separate
interrogate or follow interactions exist in this career (the "follow" step is the stakeout's witnessed-crime Sim, kept by
`StoreSimForLater`). Not fixed, by design or unconfirmed: a follow-up case step targeting the stored stakeout Sim
(`SavedStakeoutSim`) resolves only an instantiated Sim (`GetStoredSim` -> `CreatedSim`), so a witnessed Sim that was
uninstantiated before the next step leaves that step without a target.

Read, no bug found: DustForPrints (route-fail retargets the dust target), Stakeout start (guards the career),
BeSketchy (`StakeoutDetective.CurrentInteraction` checked), PrivateEyeCaught, CelebrateCaseClosed,
`CheckOpportunitiesForLot` and `ShouldCaseDisplayBlackmailTNS` (null-checked). Interrogate / hack / follow are socials
and computer interactions outside these files; not read in this pass.
