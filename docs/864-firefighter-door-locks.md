# 864: Firefighter emergencies and door locks, `firedoorkeep`

2026-11-22 (UTC), static (EA code read in a local decompile, not committed). Fix built, default off, NOT RUN.
Follows docs/570 §2 row 5 (lead: large-fire cleanup unlocks every door) and docs/863 §2 row 3 (locks spread through
elevators); not redone here.

## 1. Question

What do the firefighter's emergency jobs (`FireFightingJob`) do to the door locks of the job lot, which of those changes
outlive the job, and what can be given back without touching locks the job never made?

## 2. Findings [verified, decompiled]

A door's lock is `CommonDoor` state: `LockType` (`tLock`: Nobody 0, SelectedActor 1, SelectedHousehold 2, Anybody 3, Pets 4,
OwnerList 5, VIPRoom 6), `LockOwner`, the owner-id list `OwnerList`, and `OneWayRoomDestinationId` (36863 = none).
`IsLocked` is `LockType != Anybody`. `SetLockType(t)` is `SetLockTypeAndOwner(t, null, 36863)`: it clears the owner and the
one-way room but keeps the owner-id list; `OnLockChanged` drops the owner list's household listener when the new type is not
OwnerList (`SetLockTypeOwnerList` adds it back). The player's Lock menu sets the acting Sim as owner, also for "Nobody";
`MakeNPCDoor` (apartment NPC doors), tomb triggers and world-builder data use `SetLockType`, so their Nobody has no owner.

What the job does to doors:

| Where | What |
|---|---|
| `SetupTask(TrappedSims)` -> `LockDoorsInRoom(room, new list)` | per trapped Sim: every unlocked door (not `IAmNotAFrontDoor`) of the room, and of rooms reached through its other portals (stairs, elevators), -> Nobody; `IBreakDoorNotAllowed` doors go to `mPreLockedDoors`, the others get the axe effect |
| `SetupTasks` (LargeFires with a WindowFires task) | unlocked outside doors with an indoor fire position -> Nobody, into `mPreLockedDoors` |
| `UnlockEntrances` | `mPreLockedDoors` still locked -> Anybody; runs in `Cleanup` before `CleanUpJob` for every job id |
| `Door.BreakDownDoor` | an active firefighter's break-in on a locked door (not VIPRoom, not hidden, not `IBreakDoorNotAllowed`) -> Anybody |
| `CleanUpJob` (every job end; `RabbitholeFirefightingJob` calls base) | **only for `JobId.LargeFires`**: every door of the lot (not `IAmNotAFrontDoor`) with `IsLocked` -> `SetLockType(Anybody)` + `StopEffect` |

So:

1. After a large fire every lock on the lot is gone, not only the job's: one-Sim, household, pets and owner-list locks the
   player set at that home, resort VIP room doors (their ResortManager group stays, the door is just open), apartment NPC
   doors and the builder's Nobody doors. A one-way door the job locked comes back as a plain door (no one-way room).
2. A trapped-Sim lock from any other job id stays Nobody, axe effect included, for good: only LargeFires unlocks.
   `SetupTasks` prepares trap spots (`FindPossiblePositions`) only for LargeFires, Earthquake and EarthquakeSubJob; which ids
   actually roll TrappedSims tasks is job data. [verified in game, `FireDoorKeepCheck jobs` on Isla Paradiso with all packs]
   EA's data gives TrappedSims only to LargeFires (2-4; Earthquake and EarthquakeSubJob roll TrappedRubble, GasLeak and
   Fires). So case 2 needs a mod or tuning that adds TrappedSims to another job; with stock data only case 1 happens.

Jobs are saved with the game (`Job` is `[Persistable]`); nothing below is.

## 3. Fix: `firedoorkeep` (ts3fixes `FireDoorKeep.cs`, `FireDoorKeepCore.cs`, tuning `kFireDoorKeep`, default off)

- Swap on `FireFightingJob.LockDoorsInRoom` (one overload, recursive): the top-level call (empty traversed list) reads every
  door's state (not `IAmNotAFrontDoor`) before EA's body; the first such call of a job keeps it as the job's "before" record,
  and every call records which doors EA's body changed (with their state just before it). Recursion passes straight through.
- Swap on `FireFightingJob.CleanUpJob`: reads every door's state, calls EA's body, then for each door still alive decides
  with `FdkCore.Restore(pre, cur, before, jobLocked)`:
  - the door is left alone unless its state after EA's body is one the job leaves (bare Nobody or bare Anybody: no owner,
    no one-way room) and it was locked before the cleanup;
  - a lock the job never sets (any type with an owner or one-way room, or SelectedActor / SelectedHousehold / Pets /
    OwnerList / VIPRoom) comes back as it was just before the cleanup (OwnerList also gets EA's household listener back);
  - a bare Nobody door the job locked goes back to its state before the job (unlocked, with its one-way room; the axe effect
    stopped, as EA's large-fire unlock does);
  - a bare Nobody door that was already so before the job stays locked;
  - an apartment neighbour's door (`Door.IsNPCDoor`, locked bare Nobody by EA) stays locked even without a record: the
    job's code skips doors that are already locked, so that lock is never the job's;
  - any other bare Nobody door without a record (no trapped Sim in this job, or a game loaded mid-job) keeps EA's outcome.
- The record is per job object and stays until the next job or a world quit, so a second `CleanUpJob` of the same job decides
  the same way. With the switch off the hooks only count (2189).
- A door a firefighter broke down, or that a Sim unlocked during the job, is Anybody before the cleanup and is never touched.
- Household: the household's own doors are included (EA bug fix): their locks come back as the player set them.
- Swap stack audit (docs/data/swap-stack-audit.csv): `LockDoorsInRoom` has no path to a yield (keep); `CleanUpJob` can yield
  (`mSituation.Exit` -> .. -> `StateMachineClient.RequestState`): stub `CleanOrig`, keep with the stub remap.

Alternative not taken: snapshot only at the first `LockDoorsInRoom` and restore that snapshot after the cleanup (the first
brief). It misses large fires without a trapped Sim (no lock call, so no snapshot, yet `CleanUpJob` still unlocks everything)
and would undo a firefighter's break-in or a player's own change during the job.

## 4. Counters (contributor range, reserved by hand: free in source and registry)

[2187] door locks given back after a large-fire cleanup (incl. one-way rooms), [2188] job door locks undone after another job
type, [2189] doors the fix would change, seen with the switch off, [2190] errors.

## 5. Check and repro

`Ts3Fixes.Fixes.FireDoorKeepCheck`: `run` puts four reported layouts (household lock after a large fire, a job lock after
another job, a one-way door the job locked, an old bare Nobody door) and five controls (unchanged lock, unlocked before and
after, a door left in a state the job never sets, bare Nobody without a record, a job lock the firefighter broke) through
`FdkCore.Restore`: off `verdict: BROKEN`, on `verdict: FIXED`; `jobs` lists every firefighter job id's task kinds and count
bounds; `result` the counters and the current record. Repro `fire-door-keep` (pure check plus `jobs`); offline selftest
`tools/selftest/firedoorkeep`.

End to end (not done): a LargeFires job (testingcheatsenabled, debug Spawn Job) on a lot with a locked door (the household's
home with a household lock, or a resort VIP room); after the job, off: 2189 up and the door unlocked; on: 2187 up and the lock
kept. (2) does not occur with EA's job data (§2).

## 6. Not done / next

- In game (contributor beta v0.3.2-exp.20261122d, Isla Paradiso): repro `fire-door-keep` off `verdict: BROKEN`, on
  `verdict: FIXED`, 2190 = 0; `jobs` table in §2. Still to do: the end-to-end large fire.
- Window-fire door locks (`SetupTasks`, LargeFires): a one-way door locked there loses its one-way room through
  `UnlockEntrances` before `CleanUpJob`; not covered.
- A VIP room checked out during the job gets its old owner back [inferred rare: a resort fire with a checked-in VIP room].
- No `live` mode (staging a job on a real lot was judged not cheap or safe enough).

## Import 2026-11-22 (maintainer)

PR #640 (stacked on #638 and #639), counters 2187-2190 kept. `firedoorkeep` is **ON by default** at import: in-game repro `fire-door-keep` off BROKEN (4 cases False) / on FIXED (4 True), errors 0, `jobs` table in §2 (Isla Paradiso). [verified in his run] The end-to-end large fire on a lot with a locked door is not done. Household rule: it only puts back lock states the player set before the job (EA bug fix); the household's own doors are included.
