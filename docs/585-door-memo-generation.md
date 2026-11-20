# docs/585 DoorMemoX=3: cross-job door memo with ts3fixes' door inputs in the generation

Status: built, default off (`[RouteSnap] DoorMemoX=-1`, switch `doorgen` off). One in-game A/B arm (below). Not proven exact by
argument alone; the 1-in-8 self-check and the self-disable stay on.

## 1. What it does

The route worker asks the sim thread "may this Sim pass this door?" (managed `Sim.OnPortalPathPlan` / `CommonDoor.IsAllowedThrough`,
door stub at 0x64F4D3, docs/159 §4.1) several times per plan, and each ask waits for the sim thread to serve it. DoorMemoX=1
(docs/189 phase 3) reuses another job's "unlocked" answer for the same key within `DoorMemoXMs` (250 ms) while the door
generation is unchanged. Its generation only moved on portal ICs, lot world events and event 59, so a change made by script code
(a lock set, a greeting, a role) could leave a stale row for up to 250 ms. That is why it stayed off.

Mode 3 adds the managed inputs of the door answer to the generation. ts3fixes `DoorGen.cs` (switch `doorgen`) hooks every setter
we found and sends UserDiag op 36 (lo 0 = bump) after the original ran; at world load it sends lo 1 (arm). Until armed, mode 3
memoizes nothing (an old package, or `doorgen` off, never gets a memo whose inputs are not tracked).

Inputs bumped (hooks call the original first, then bump):
- door locks and owner lists: `CommonDoor.SetLockTypeAndOwner` (both), `SetLockTypeOwnerList`, `AddToOwnerList`, `RemoveFromOwnerList`
- greeting and household membership: `Household.AddGreetedLotToHousehold`, `RemoveGreetedLotForHousehold`, `Add`, `Remove`
- per-lot handlers: `Lot.add_PortalPathPlan`, `remove_PortalPathPlan`
- fire on the lot (`Lot.IsFireOnLot`): `FireManager.RegisterFire`, `UnregisterFire`, `BuffOnFire` / `BuffTorched` `OnAddition`, `OnRemoval`
- `Sim.FlagSet` when the `IgnoreDoorLocks` bit changes
- tomb mummy: `OccultManager.AddOccultType`, `RemoveOccultType` (the NPC-household half is `Household.Add` / `Remove`)
- roles (proprietors): `Role.CreateRole`, `Role.RemoveSimFromRole`, `RoleManagerTask.FillInWorldRole`
- resort VIP rooms and closing: `ResortManager.set_Closed`, `DoUpdate` (when `Closed` changed), `CreateNewVIPDoorGroup`,
  `RemoveDoorFromVIPRoom`, `FindVIPRoomData` (when it added a row), `RefreshVIPRooms`, `VIPRoomData.AddGuest`, `RemoveGuest`
- gender doors and the diving board: `SimDescription.set_Gender`, `set_Age`, `set_IsVisuallyPregnant`

Not covered: portal types with their own `IsAllowedThrough` beyond these (elevators, box stalls, horse jumps, spiral stairs,
pools, tube elevators, ladders) and role teardown on travel (a new world starts a new table). Those stay on the safety net:
every 8th valid row is asked anyway and compared, and one mismatch turns the memo off for the session (`FAIL door memo x` line).

Household Sims: the memo only answers asks the worker makes inside a route job; the answer it returns is the one another job got
from the same managed code for the same key and generation, so a household Sim gets the same answer it would have got live.

## 2. Settings

```ini
[RouteSnap]
DoorMemoX=3        ; -1 = stub not installed, 0 = off, 1 / 2 as docs/189, 3 = this
DoorMemoXMs=250
```
plus the ts3fixes switch `doorgen` on (`EARLYFIX` / settings). Counters: `ts3native_doorgen` export (docs/registry.md), the
`door memo x mode 3:` and `door memo x misses:` log lines.

## 3. In-game evidence (run phase-20261115-1935, DoorMemoX=3, doorgen on; final log line)

- armed at world load; 2560 managed-input bumps.
- hits 116,120, misses 203,112 (36% answered from the memo), invalidations 19,126, self-check re-asks 2,475, **mismatches 0**.
- host calls per route job 9.4 (shadow arm 1727) -> 5.3; host-call queue wait per job 11.2 -> 10.0 ms (HostSplit counters,
  diagnostic build). Most calls are served promptly at the pump sites; the READY-poll site keeps ~6 ms per job.
- 6 game hitches vs 1-2 in the other arms: one 1 s sim frame (a single poll, cause not logged), one hourly, the rest at game
  times that hitch in other arms too (9:12, 12:47). None attributable to the memo or the DoorGen hooks.
- Invalidations are at most 9% of the misses, so generation churn from the bumps is not what limits the hit rate. The
  `door memo x misses:` line (no row / other generation / older than DoorMemoXMs / answer not unlocked) splits the rest in the
  next run.

## 4. With [DoorFast] (PR #395, docs/548, open)

DoorFast predicts the same door asks natively from mirrored door data and runs first in the same stub; an ask it answers never
reaches DoorMemo / DoorMemoX, and an ask it lets through is memoized as now. Both are exact under their own self-checks, so both
on is consistent. Merging both needs a textual merge in `HcDoorEnter` (both edit its first lines) and in `ApplyHostCalls`
(PR #395 still rejects `DoorMemoX` > 2: keep 3).

## 5. Household rule (2026-11-16)

In the acting modes (1, 3) a memo answer is used only when the running route job is known unseen (scope 2 or a ts3fixes
`uf` mark) or a seen NPC's (scope 3, ts3fixes op 13). Household and selected Sims (op 10, scope 1) and unknown scope (no
answer, 0) always get the live host call, so their door answers are exactly vanilla [verified: selftest `TestAskLent`, one
case per scope]. Their live answer is still kept for others and compared with a valid row, so a stale row seen on a household
ask turns the memo off for the session. Counter `ts3native_doorgen.exempt` (0x20) counts those asks. The scope (`t_hcScope`) is set at each worker job start from `j->kdScope`. PR #430's 0 mismatches covered all scopes, NPCs
included, so the restricted memo ships ON: `DoorMemoX=3` in the ini, `doorgen` on in ts3fixes.

## 7. Second hit: same lot, same rooms (2026-11-20, chain-gcgate-rr3-1119, job sv-gcgate-ab)

Master build set (asi gchold2-1117, ts3fixes dalexlol-1117b; the §6 fix is not on master), SV-Spring, `DoorMemoX=3`, `doorgen` on,
0 mismatches in the first 5 counter lines (hits 15,550, miss 20,191, 328 bumps), then `FAIL door memo x` at game time 233,219,750 ms:
`door memo x shadow: answer 1, other job's 2 (121 ms before), key 8B52BB30 6C11001B 844E9B20 38B400F5 5F050F00 6C110020 0001 0004 00`.
Lot 0x6C11001B8B52BB30 and rooms 1 -> 4 are the same as §6; the actor differs (38B400F5844E9B20, scope household-or-seen, so it got the
live answer; its route 250862E5 then logged `routefail ... result 16` 204 ms, the "Razta Dog LookOutWindow" fail). Same lot, same
room pair in two sessions 4 days of game save apart: the stable thing is the home lot's bathroom door, which fits the §6 cause
(PrivacySituation handler answers from the asker's current interaction, no generation bump), not a lock-state change of a pet door
[inferred; a pet door lock change is bumped by `SetLockTypeAndOwner`]. Counters of the run show no `held` column (master build).
The harness guard killed the game 60 s after the self-check FAIL (the job stops on `ts3native hook failed`); EA wrote an xcpt
(`? at ?`, no stack) 1 s later and queue.ts filed it CRASH; it now reads KILLED (`bugfix/guard-kill-label-1120`; the 10-18 f4b and
travel rows had the same pattern, not reclassified).

Repro and correctness queue are the §6 ones, no second repro: `repros.json` `doormemox-privacy` (4 townies teleported into the home
bathroom, toilet forced twice, Go Here back in through the door) and `hhsearch-sv-1116-rerun2-1117` on the fix build, both on
`bugfix/doormemox-privacy-1117` (pending merge). Fix build: expect no `FAIL door memo x` and `(live, not kept)` > 0. Reproduced
on current code = the same FAIL on the master build with that repro; run it as the pre-fix arm only after the branch merges (the
repro entry is not on master). Until then SV timing queues that do not test DoorMemoX set `RouteSnap.DoorMemoX` 0
(`gc-idle-gate-time-1117-rerun4-1120`).
