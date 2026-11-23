# 863: Active careers on apartment lots: job targets in hidden units, `aptgasleak`, `aptpossess`

2026-11-21 (UTC), static (EA code read in a local decompile, not committed). Both fixes built, default off, NOT RUN.
Follows docs/570 §5 (`firetrapcount`: trapped Sims and rubble spots in hidden units), docs/681 (`firegreet`), docs/552
(hidden apartment rooms), docs/546 §10 (pet fire plans), docs/862 (`fireleftover`) and docs/562 §5 (`privateeyecase`);
not redone here.

## 1. Question

The Ambitions active careers (firefighter, ghost hunter, private investigator, stylist, architectural designer) and the
other careers that send a Sim to a client's home pick targets inside the job lot. On a Late Night apartment lot most
rooms belong to neighbour units the game hides (`Lot.IsRoomHidden`, from `HiddenRoomMarker`). Every door of a hidden room
refuses every Sim (`CommonDoor.IsAllowedThrough`, first test; docs/552 §1). So a target inside a hidden unit can never be
reached: the task never completes and `Job.OnJobTimeout` re-arms the timeout for as long as the job's Sim stays on the lot.
Only `InteriorDesigner` keeps apartment lots out of its jobs (`Occupation.AddLotIfValidForNewJob`) [verified]; the
firefighter and ghost hunter take them.

EA's own hidden test, used by `FindIsolatedFirePosition` (small fires) and `SpiritMaker.MoveToNewLocation` (spirits):
`lot.HasVirtualResidentialSlots && (lot.IsRoomHidden(World.GetRoomId(lotId, loc, eRoomDefinition.LightBlocking)) ||
TombRoomManager.IsPointInAFoggedRoom(pos))` [verified]. The fixes below reuse it as is.

## 2. Findings, ranked by player impact

| # | Case | EA location | Outcome for the player | Status |
|---|---|---|---|---|
| 1 | Earthquake gas leak on a stove in a hidden unit | `FireFightingJob.SetupTask(TaskId.GasLeak)`: the first stove of the lot without a leak gets `StartGasLeak`, no room test [verified] | The "fix the gas leak" task cannot be done; the emergency never completes, the timeout re-arms while the firefighter stays, leaving fails the job. Needs a stove inside a hidden unit [inferred; `scan` counts them] | fixed, `aptgasleak` |
| 2 | Ghost-hunter spirit hops into a hidden unit | `Exorcise.Run` on failure (5-30 % by level, x1.3 Unlucky, x1.4 Loser) calls `GhostHunterJob.GetPossessableObjectInRange`: any possessable object on the same level within `kPossessedObjectRange` (8 m), at random, no route or room test [verified] | The ExorciseObject task points at furniture behind a neighbour's wall; same stuck job as #1. Needs possessable objects in a hidden unit within 8 m [inferred: Bridgeport hidden rooms can be large, e.g. a whole ground floor; whether they hold furniture is what `scan` shows] | fixed, `aptpossess` |
| 3 | Locked rooms spread through elevators and stairs | `LockDoorsInRoom` recurses through non-door portals (stairs, `ElevatorInterior`) | "Nobody" locks may spread building-wide on apartment lots with an elevator [inferred]; the firefighter can break doors down and `CleanUpJob` unlocks all at the end | documented, lead |
| 4 | Isolated fire spot with no route check | `FindIsolatedFirePosition`: the route test runs only when `FindGoodLocationInSameRoom` succeeds [verified] | A fire spot no Sim can route to, any lot type (the hidden test still applies first) | documented, generic lead |
| 5 | Angry gnome placed in a hidden unit | `SetupTask(AngryGnomes)`: `PlaceObjectOnLot` / `Lot.GetRandomPosition`, no hidden test [verified] | The gnome's own trickery moves it every 10-60 minutes, so it should come out [inferred]; a failed placement throws on `magicGnome.IsAngry` (generic) | documented |
| 6 | Apartment door pick includes hidden NPC doors | `Lot.FindApartmentDoor`: lowest unlocked public / non-public door [verified] | PI `BreakIntoHouse` animates at a neighbour's door (cosmetic; `privateeyecase` restores the locks); daycare drop-off and puppy / gift visits fall back to other spots | documented, low |

Not bugs on apartment lots [verified]: window fires (`TestCanBeSetOnFire` needs a window on room 0 below level 2, route
from outside); fire spread (portal tiles chance 0, walls block); trap and rubble spots (`GetRoutableIndoorPositions`,
hidden spots filtered by `firetrapcount`); hidden ghost rooms (the hunter's allowed rooms minus hidden ones); spirits
(SpiritMaker hidden test); the first possessed objects (`IsObjectInValidLocation` route test); designer jobs (apartments
excluded); stylist and singagram targets (household members of the lot); PI DustForPrints (re-targets on route failure),
Snoop, mailbox (`privateeyecase`); the Respond route and map tag (lot-level); the greeting (`firegreet`).

## 3. Fixes (ts3fixes `AptJobTargets.cs`, `AptJobTargetsCore.cs`, default off)

- `aptgasleak` (`kAptGasLeak`): once a sim-minute (FireGreet's alarm, as `fireleftover`), while an emergency on a lot with
  virtual residential slots is set up and still waits for a GasLeak task, a leaking stove in a hidden or fogged room has
  its leak stopped, the job's own `OnStoveDisposed` handler taken off it (as EA's failed-job cleanup does), and one
  GasLeak task dropped the way `SimDied` drops a task (`Tasks.Remove` + `OnTaskComplete`, no task pay). The job then ends
  through EA's `OnCompletion` when nothing is left. With the switch off, each such stove is counted once (2182).
- `aptpossess` (`kAptPossess`): a swap on `GhostHunterJob.GetPossessableObjectInRange` calls EA's body; when the pick sits
  in a hidden or fogged room of such a lot, it re-picks with EA's own rule (same level, within `kPossessedObjectRange`, at
  random) over the objects outside hidden rooms. With none left it returns null, EA's own "nothing in range" outcome (the
  exorcise counts as a success). No yield on the path (docs/data/swap-stack-audit.csv: stub `RangeOrig`, keep).
- Household Sims: the household firefighter / ghost hunter is affected only through the job's outcome (EA bug fix).
- Counters (contributor range, reserved by hand: free in source and registry): [2181] hidden leaks dropped, [2182] seen
  with `aptgasleak` off, [2183] errors; [2184] hops re-picked, [2185] seen with `aptpossess` off, [2186] errors.
- Check `Ts3Fixes.Fixes.AptJobTargetsCheck`: `run` (off `verdict: BROKEN`, on `verdict: FIXED`, with controls), `scan`
  (per apartment lot: stoves and other unparented objects in hidden rooms), `result`. Repro `apt-job-targets`; offline
  selftest `tools/selftest/aptjobtargets`.

Alternative not taken for #1: re-target the leak at setup to a stove outside hidden rooms. It keeps the task's pay but
needs a swap on the whole `SetupTask`, and it would not repair a leak already placed in a save.

## 4. Not checked / next

- No game run. First step: `AptJobTargetsCheck scan` in Bridgeport, to see whether EA's apartments hold stoves or
  possessable objects in hidden units (if not, both bugs need a player-built apartment).
- Gas-leak side effects in the hidden unit before the drop (smell, explosion timer) were not read.
- Leads #3 and #4 need an in-game look before any fix.

## Import 2026-11-22 (maintainer)

PR #639 (stacked on #638), counters 2181-2186 kept. `aptgasleak` and `aptpossess` are **ON by default** at import: in-game repro `apt-job-targets` off BROKEN / on FIXED, controls ok, error counters 0 (Bridgeport apartment save, 28 apartment lots). [verified in his run] The live job path (a real earthquake with a stove in a hidden unit, a failed exorcise next to a hidden unit) is unseen. Household rule: EA bug fix, job outcome only.
