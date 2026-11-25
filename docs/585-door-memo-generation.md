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

## 6. Privacy and mod door handlers (2026-11-17, branch `bugfix/doormemox-privacy-1117`, 0.36.295-dmxp1120)

Job `sv-hhsearch` (queue hhsearch-sv-1116, 2026-11-16 19:39 UTC, master-1116c asi + ini, hoursmemo-svprobe ts3fixes) logged
`FAIL door memo x` once: `door memo x shadow: answer 1, other job's 2 (79 ms before), key 8B52BB30 6C11001B 6A938280 F87500F5
5F050F00 6C110020 0001 0004 00`, i.e. lot 0x6C11001B8B52BB30, actor F87500F56A938280 (Razta Dog, active household, scope
exempt: it got the live answer, so no wrong answer reached it), room 1 -> 4. The memo was off for the rest of the session.
No other mode-3 log in probe-out (72) has a mismatch; HouseholdToo was off; the branch's ts3fixes changes do not touch doors.

Cause [verified, static, ledger §550 V-550-2/3; that it caused this mismatch is inferred]: the only EA `Lot.PortalPathPlan` handler beyond `Sim.OnPortalPathPlan` is `PrivacySituation`
(shower, toilet, bath, sink, WooHoo). Its answer for room `SyncTarget.RoomId` reads the asking Sim's current (or transition)
interaction, autonomous flag and priority, which change between jobs without any generation bump. The add / remove of the
handler was bumped; its per-ask inputs were not. Also untracked (not this case): `Lot.IsRoomHidden`, `actor.Service`.

Fix: DoorGen.cs sends op 36 with hi 1 (held) / 2 (free) and the lot id's low dword after each handler add / remove and for
every lot at arm, held = the lot has any handler other than `Sim.OnPortalPathPlan` (or the field is unreadable). Native keeps
32 held lots (overflow or low dword 0 = every lot held until the next arm); an ask on a held lot skips the memo entirely (no
answer from it, nothing kept, no compare) and counts `ts3native_doorgen.held` (new last field), logged as "asks on lots
with a privacy / mod door handler (live, not kept)" on the `door memo x misses:` line [verified: selftest `TestAskLent`
hold / free / overflow / arm cases]. Release v0.2.11 ships `DoorMemoX=-1`: no player exposure (v0.3.0 too). Rerun queues
`hhsearch-sv-1116-rerun1-1117` / `-rerun2-1117` (SV, repro `doormemox-privacy`: forced toilet + shower with NPCs on the household lot).

Lot key (2026-11-20) [verified, static, ledger §550 V-550-1]: the script key `(uint)Lot.LotId` is the native door key's key[0]
(the lot id's low dword, pushed at `0x64F4D3`), so a held lot matches its asks; the logged key `8B52BB30 6C11001B` of §6 and §7 is
low, high. The reflected field `Lot.mOnPortalPathPlanHandler` and the two EA subscribers are right [verified, static, V-550-2].
Not covered: a mod that writes the field without the event [inferred, I-550-4].

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

## 8. Third hit: EA 1.69, no privacy handler, a fresh Sim (2026-11-24, bundle-day-1124-correct EA, job bp-vbowner-correct-5)

Build 0.36.301-eabundle1124b (test/bundle-day-1124 b8ba526 + eafinal2 EA rows), EA app 1.69.47, Baseline-BP, repro
`vb-owner-delete-stress` (4 rounds of 25 objects placed on the home lot in live mode, deleted in Buy mode). `DoorMemoX=3`,
`DoorMemoXMs=250`, doorgen armed. After 1274 clean compares: `door memo x shadow: answer 1, other job's 2 (7 ms before), key
BEC1DF80 28590035 A70A1940 7AB100F5 C1469290 28590035 0019 0016 00`, i.e. lot 0x28590035BEC1DF80 (the home lot: vbowner-1 logs
`routefail buildbuy: lot 0x28590035BEC1DF80 object removed`), actor 0x7AB100F5A70A1940, door 0x28590035C1469290, rooms 25 -> 22,
initialLocked 0. Totals: hits 6321, miss 9647, inval 712, shadow 1275, mismatch 1. vbowner-1..4: 0 mismatches.

What it is not [verified, log]:
- Not an EA port gap: the door stub (`OK door stub 0x00650AB3 -> 0x00792D90`), `World_FireEvent` (`OK detour ... 0x00790440`), all
  39 routesnap IC slots (each slot checked against its expected function before it is hooked) and the event 59 slot
  (`0x011D72C4` -> `0x01231374`) are in place on this build; DoorMemoX has no other address.
- Not the privacy case of §6/§7: `asks on lots with a privacy / mod door handler (live, not kept) 0` in this session while
  vbowner-2..4 on the same build count 81..465, so the EA hold works and this lot had no extra handler.
- Not a placed object: the actor id is not one of the 4 rounds' first objects and object placement feeds no door rule.

Actor [inferred]: 0x7AB100F5A70A1940 is newer than round 3's objects (A70A0B10, 03:49:21) and older than round 4's (A70A5C60,
03:50:19): a Sim instantiated in between. The line before the FAIL is `ufplan: kind 0 172 ms why -1 src pet Stray Cat` (same tick).

Untracked inputs found [verified, static, EA Sims3GameplaySystems; same code on Steam]:
- `SimDescription.Household`: DoorGen bumped `Household.Add` / `Remove(SimDescription)` only. `Household.AddTemporary`,
  `RemoveTemporary`, `RemoveSimKeepHousehold` and `PetPool.AddPet` (`AddSilent` + `OnHouseholdChanged`) / `RemovePet` change it
  with no bump; 45 call sites use the silent / temporary forms. It feeds `IsStray` (pet household + pool), `IsGreetedOnLot`
  (null household = not greeted), `SelectedHousehold` locks and `CanSimTreatAsHome`. A stray that routes before its pool add
  gets "unlocked" (no stray rule), then "locked" once it is a stray: the 2 -> 1 seen here.
- `Sim.Service` (CommonDoor: police, firefighter, reaper, burglar, repoman, social worker ignore locks): no bump.
- Routing ICs outside slots 4-14: `DestroyPortalHandle` (16), `OnExitBuildBuy` (22, rooms may get new numbers: DoorShare already
  clears on it), `DoorLockStateChanged` (24, `RotatingWallDoor` calls it without `SetLockTypeAndOwner`), `BuildRoutingData` (29),
  `BulldozeLot` (30), houseboat lot create / delete (31, 32).

Fix (bugfix/ea-doormemox-1124): DoorGen.cs hooks `SimDescription.OnHouseholdChanged` (the only writer of `mHousehold` besides
`Delete`, which calls `Household.Remove` first) and `Sim.set_Service` (every `Worker.Service = x`; `Service.EndService` writes the
field after `value.EndService()`, whose situations clear it through the setter [inferred]), and logs the first 40 such
changes of instantiated Sims as `doorgen: household|service <name> 0x<sim id> household <kind>, stray <b>, service <type>`, so the
next mismatch's actor can be matched. Native `DmxGen` adds the 7 IC slots above (selftest `TestAskLent`: each one invalidates a
row). The self-check (every 8th valid row re-asked, one mismatch turns the memo off) stays. Which input flipped this ask is not
proven: the next mismatch, if any, names the actor through the new lines. Steam runs the same managed code and ships
`DoorMemoX=3`: the gap is on both editions.

Household: the memo answers only scope 2 / 3 jobs (unseen, seen NPC); household, selected and unknown scope ask live
[verified, `HcDoorEnter` and selftest]. A stale "unlocked" can let an NPC plan through a door it may not use, once, until the
re-ask or age cap ends the row; no household Sim gets a memo answer. Verify queue: `doormemox-verify-ea-1124`.


## 9. Fourth hit: the same door and Sim, a time-dependent door rule (2026-11-24, eam1124c, bp-vbowner-correct-1)

Build eam1124c (rc/m1124c ba7618c1 + EA port, includes the §8 fix), EA app 1.69.47, Baseline-BP, repro `vb-owner-delete-stress`.
After 1578 clean compares: `door memo x shadow: answer 1, other job's 2 (25 ms before), key BEC1DF80 28590035 AA219C80 7AB100F5
C1469290 28590035 0019 0016 00`. Totals: shadow 1579, mismatch 1, hits 8260, miss 10272, held 0. No `doorgen:` line names the actor.

Who the actor is [verified, agent `sims` reply in the guard-kill dump `ts3w-26244-20261124T125124.dmp` and in the §8 dump
`ts3w-22868-20261124T035031.dmp`]: both actors are **Becca Bijou**, a community townie (not active household), interaction
`Watch Door`, lot `The Prosper Room`, at (693.5, 1705.5): the Bouncer role of the velvet ropes there. Ids 0x7AB100F5AA219C80 (this
run) and 0x7AB100F5A70A1940 (§8) are new object ids because the role instantiates her fresh each session, at the same game time of
the same repro (both between round 3 and round 4). So both mismatches are the same Sim, door, rooms and moment of the save.
§8 was wrong twice [verified, log]: lot 0x28590035BEC1DF80 is not the home lot (the home lot is 0x6C1100337657B720, the lot of
every `routefail buildbuy` line in both runs), and the actor was no stray (I-608-1 refuted).

Cause [the rule verified, static; that it fired here inferred]: `Sim.OnPortalPathPlan` asks the door's virtual
`IsAllowedThrough`. `DoorSingleGender.IsAllowedThrough` (restroom doors) restricts a human of the other gender only while
`lot.IsResidentialLot || lot.IsOpenVenue()`, and `Lot.IsOpenVenue()` reads `SimClock.HoursPassedOfDay` against the venue's hours
(and, for a gig venue, the gig start). The answer for the same Sim, door and rooms flips from 2 (closed venue: anyone) to 1
(open: restricted) when the clock crosses the opening hour. Nothing is written at that moment, so no hook can bump it: the input
is the clock. A bouncer is created just before opening and walks the club's rooms while the venue opens: the 2 -> 1 seen twice [inferred: door type, Becca's gender and the venue hours are not in the logs; the `doorgen: memo skips` line of the verify run names the door type].
The §8 fix (household / service hooks, 7 IC slots) is right but does not touch this; the first mismatch (§8) had this same cause
[inferred: same Sim, door, rooms, repro phase].

Other untracked inputs found in the same read [verified, static]: `SimDescription.Service` is a public field;
`Service.CreateNpcSim` (assign), `Service.EndService`, `Service.CheckIfSimsShouldBeRemovedFromPool` and two `TimeTravelerSituation`
paths write it without `Sim.set_Service` (I-608-2 refuted). `CommonDoor.HouseholdSimChanged` removes owner-list ids directly.
`PetPool.UpdateSimDescriptionId` / `Copy` put ids in a pool without a household change. Doors with their own rule:
`DoorSingleGender` (clock), `NonAnimatedDoor` (`mIsOpen`), `SecretDoor`, store `Turnstile`; resort VIP rooms read real estate
(`Lot.IsControllable`) and services.

Fix (bugfix/doormemox-mismatch2-1124, 0.36.304-dmx2-1124): an allow rule instead of more bumps. DoorMemoX=3 keeps a row only for
an answer ts3fixes vouched for **during that very ask**: DoorShare.cs's hook after `LotManager.PortalLockedCallback` (the game's
event 59 callback, inside the served call) calls `DoorGenMemoNote` for every "unlocked" answer and sends op 36 hi 3 (vouched) or
hi 4 (not) with `DsNoteHash` of the ask. Vouched = the lot runs the game's handler alone and event 59 the game's callback alone,
the router is a Sim of DoorShare's ordinary class (no horse, stray, tomb mummy, proprietor, service, IgnoreDoorLocks), and the
portal is a `Door` whose `IsAllowedThrough` is `CommonDoor`'s own on a non-resort lot, or a portal no rule of the handler names.
Native `HcDoorEnter` clears the note before its live ask and calls `DmxPut` only when the note is hi 3 with the ask's hash;
otherwise the answer is live and not kept (`ts3native_doorgen.unnoted`, new last field; on the `door memo x misses:` line). No
note (an older package) = nothing kept: fail closed. The first 40 portal types skipped are logged once each as
`doorgen: memo skips <Type> 0x<id> on lot 0x<lot> (<why>)`, so the verify run names door 0x28590035C1469290's type.

Why ordinary-only puts close the Sim side without hooking every service / role writer: leaving the class (service ends, role
ends, flag cleared) only returns the Sim to the answer it had while ordinary; entering the class from outside starts with no row.
The only ordinary -> locked paths are becoming a stray (household change: `OnHouseholdChanged`; pool: `AddPet` calls it,
`UpdateSimDescriptionId` / `Copy` now bump) and becoming a tomb mummy (`AddOccultType`, bumped). Door side for a plain door:
lock type / owner / one-way (`SetLockTypeAndOwner` both, owner-list methods, now `HouseholdSimChanged`), the lock owner's or
actor's household (`OnHouseholdChanged`, `Household.Remove` before `Dispose` clears it), hidden rooms (`WorldBuilderSetHiddenRoom`,
`UpdateRoomMarkers`), fire (pets), lot handlers (held), portal ICs and world events (native).

Writers list (no-gaps check, decompiled Steam = EA 1.69 code):

| Input | Writers | Covered by |
|---|---|---|
| door lock type, owner, one-way room | `SetLockTypeAndOwner` (2), `ImportContent` (load) | bump; load = arm |
| owner list | `SetLockTypeOwnerList`, `AddToOwnerList`, `RemoveFromOwnerList`, `HouseholdSimChanged`, ts3fixes Batch582b (`RemoveAll` then `SetLockTypeAndOwner`) | bump (new: `HouseholdSimChanged`) |
| actor / owner household | `OnHouseholdChanged`; `Dispose` after `Household.Remove` | bump |
| stray (pool) | `AddPet` (then `OnHouseholdChanged`), `UpdateSimDescriptionId`, `Copy`; removals only unlock | bump (new: 2) |
| tomb mummy | `AddOccultType` / `RemoveOccultType` (+ NRaas helpers via CompatResolve) | bump |
| service, proprietor role, IgnoreDoorLocks, horse | many, incl. direct field writes | not needed: such Sims never get a row, leaving the class never locks |
| gender, age, pregnancy, venue hours, clock | setters, CAS, `SimClock` | not needed: their doors (single-gender, diving board) never get a row |
| resort VIP data, `IsControllable` | resort hooks, real estate | not needed: resort lots never get a row |
| hidden rooms | `WorldBuilderSetHiddenRoom`, `UpdateRoomMarkers` | bump (DoorShareLotHook) |
| lot / event 59 handlers | `add/remove_PortalPathPlan`, the delegate slot | held / native generation; and not vouched |

Not covered [inferred]: a mod that replaces `Sim.OnPortalPathPlan` or `CommonDoor.IsAllowedThrough` by method swap (the rule it
reads is not ours to see); a lot type change (base camp) in Edit Town for a stray (strays never get a row, so only the door side,
which reads no lot type). The 1-in-8 re-ask and the self-disable stay.

Test-only `[RouteSnap] DoorMemoXCheckEvery=1` (1-8, default 8): every valid row is asked live and compared (no memo answers), for
verify runs that need many compares; logs `door memo x: test: every 1. valid row asked live and compared`.

Selftest `TestAskLent` [verified]: an "unlocked" answer with a hi 4 note, no note, or a note for another ask is not kept and a
later "locked" answer meets no row (no mismatch); a hi 3 note keeps it and the next job hits; CheckEvery=1 compares every row.

Household: unchanged; household, selected and unknown scope always ask live and are compared [verified, selftest].
Edition: the managed rule and the native change are the same on Steam and EA; the Steam asi compiles (0.36.304-dmx2-1124).

Writer-hook counters (0.36.305-dmx2b-1124): each new hook also sends op 36 hi 5 / 6 / 7; native counts them in
`ts3native_doorgen.writers[3]` and logs `door memo x writer hooks: CommonDoor.HouseholdSimChanged N, PetPool.UpdateSimDescriptionId N,
PetPool.Copy N` at session end. Triggers [verified, static]: `HouseholdSimChanged` runs on `Household.HouseholdSimsChanged` of a lot
that has doors (`SetHouseholdCallback`): repro step `pet_adopt` (a dog into a townie home lot). `PetPool.UpdateSimDescriptionId` is
called only from `CharacterImportOnGameLoad.PopulateDescriptionsInWorldMap` (world load) and `PetPool.Copy` only from
`PetPoolManager.FixupPools` (world-builder command `CharacterImportCommands.FixupPetPools`): no in-session player action reaches
them, so the verify reads 0 there and only requires the line.
m1124d (0.36.305-m1124d) [verified, static]: `SetHouseholdCallback` is called only from `SetLockTypeOwnerList`, so a loaded save has no door subscribed to `HouseholdSimsChanged` until something sets an owner-list lock; the counter for hi 5 now sits in the `HouseholdSimChanged` hook (it was in the `SetLockTypeOwnerList` hook), and the repro's `pet_adopt` passes `ownerLockDoor: true` (agent: only townie homes with a `CommonDoor`; one door gets an owner-list lock of the household first).

Forced repro `doormemox-venue-clock` (rewritten): agent `venue_hours` with `lot` reads The Prosper Room's hours
(`Lot.GetHoursOfOperation`: Bartending extended hours or the venue's MetaAutonomy tuning, data, not code) and with `jump` moves
the clock (`SimClock.TicksAdvanced`, as `AddOneHour` does) to the open / close hour + `offsetMin`: -3, Sims sent through door
0x28590035C1469290 (4 townies + Becca Bijou), then +1 (`open_now` flips, read back), the same Sims sent again; 5 crossings.
