# 674. The route worker's door asks: what they are, why DoorMemoX misses them, and a row shared between Sims

2026-11-20, branch `feat/doorask-misses` (base `work/combo-1117` 32967a77). Inputs: `ts3native-full.log` of s156 (off and on),
s158 and s159 (2-min forced-routing storms, Isla Paradiso), `hostcalls.cpp`, the decompiled C# of the door handler chain
(`game-copy/decompiled`), docs/105 §2-§3 (the answer's inputs), docs/159 (the proxy), docs/585 (DoorMemoX=3), docs/673 §9.
Built: `[RouteSnap] DoorShare` (native) + ts3fixes `doorshare`, both default off. §1-§8 are the first build (v1), which ran in
s163 (shadow) and s164 (answer): exact, small gain. §9 reads those runs and describes v2 (stamps per portal owner and per lot,
a class for routers that are no Sim, pets in the ordinary class), which has **not run**. Where §3-§4 and §9 differ, §9 holds.

Tags: [measured] = read from the named log, [verified] = read in the source or the decompiled C#, [inferred] = argued, not seen.

## 1. The host calls

Every managed call the worker proxies in these runs is the same one: World event 59, fired at `0x64F4D3` in
`Portal_LockState 0x64F2C0` ("may this Sim pass this portal"), managed side `LotManager.PortalLockedCallback` ->
`Lot.OnPortalPathPlan` -> each handler of the lot (`Sim.OnPortalPathPlan`, then any `PrivacySituation` handler) [verified,
docs/159 §1, docs/92 §4: the only managed exit of the four route ICs]. The logs carry no per-call kind, so "all of them" is a
count match, not a per-call record: s156 off made 32,420 host calls in 1,641 plans (19.8 a plan) with no memo, s156 on saw
37,071 door asks at the stub in 1,928 plans (19.2 a plan) [measured].

| | s156 on | s158 | s159 | how known |
|---|---|---|---|---|
| plans (`route plans: on`) | 1,928 | 2,362 | 1,712 | measured |
| door asks at the stub (DoorMemoX hits + misses + valid rows asked anyway) | 37,071 | 49,629 | 29,033 | measured |
| answered by DoorMemoX | 14,379 | 19,819 | 10,672 | measured |
| missed | 22,099 | 29,270 | 18,081 | measured |
| ... no row for the key | 19,612 (89%) | 25,857 (88%) | 15,942 (88%) | measured |
| ... row of another generation | 2,126 | 2,103 | 1,035 | measured |
| ... row older than 250 ms | 361 | 1,310 | 1,104 | measured |
| live answers not "unlocked" (never stored) | 1,210 | 1,663 | 1,074 | measured |
| asks of household / unknown-scope jobs (always live) | 6,384 | 0 | 4,640 | measured |
| host calls (`hostCalls`) | 21,859 | 28,750 | 17,580 | measured |
| ... served by TaskServe / queue ms | 14,822 / 8,405 | 20,435 / 11,966 | 13,070 / 8,109 | measured |
| ... inside `PlanHierarchical` (split's host frames) | - | - | 4,115 calls, 1,961 ms | measured |
| ... outside it: the goal step's lot search, and `Route_PlanLocked` around the search | - | - | 13,465 | measured (difference) |
| managed method | one: event 59 -> `PortalLockedCallback` | | | verified (static), count match |
| native call site | one: `0x64F4D3`; callers `PortalEdgeCost 0x634500` (lot search, lot A\*) and the in-room pre-pass `0x632460` | | | verified (docs/667 §2) |
| what is asked about | every portal of a lot whose `+0x8F` is set, and every lot sets it (each adds `Sim.OnPortalPathPlan` at load): doors, arches, gates, stairs, elevators, ladders, pool and diving-board portals. Not only doors | | | verified (docs/105 §2) |
| different (portal, rooms, Sim) keys vs calls | not in these logs | | | missing: the new `door share:` line counts both |

`hostCalls` is below misses + re-asks because the within-job memo (`[RouteAsync] DoorMemo`) answers a repeat that DoorMemoX
missed. The before / in / after split per job needs HoldProbe's `worker hold parts` line (built after s159) or the new
`door share:` line (asks before the plan, in `Route_PlanLocked`, after).

## 2. Why 15,942 asks had no row

Not for any of the four reasons the brief lists as guesses. [verified, `hostcalls.cpp`]

- **The key holds the asking Sim.** `P4DoorKey` is (lot id, Sim id, portal owner id, room from, room to, initialLocked). A row
  written for Sim A never answers Sim B, so the first ask of every Sim about every portal and direction is a host call.
- **A row lives 250 ms** (`DoorMemoXMs`), and the table is a ring of 256 rows that appends on every stored answer without
  replacing the key's old row (about 140 stored answers a second in s159: a key leaves the ring after about 1.8 s). "No row"
  therefore means: this Sim did not ask about this portal and direction in the last 1.8 s. With both limits, what DoorMemoX
  answers is mostly the same Sim's repeats inside one job and its replans, not another Sim's ask [inferred from the key and the
  ages; the logs have no per-key count].
- `doorgen` generates no rows. It only moves the generation; a miss by generation is the 1,035 "other generation" asks (6%).
  Its 1,387 bumps came in a burst (1,219 in the first minute, 36 in the second) while the no-row rate stayed the same in both
  minutes (8,155 and 7,787) [measured], so eager invalidation is not the cause.
- Portals that are not doors are stored like doors (any portal whose answer is 2). They are not the cause either.

So the lever is the key, not the invalidation.

## 3. What one answer depends on [verified, decompiled C#; docs/105 §2-§3 has the full table]

`Sim.OnPortalPathPlan(args)`, with S = the routing Sim, P = the portal object, in order: (1) S is a tomb mummy: locked;
(2) S's assigned role is a `Proprietor` whose role object is on P's lot: unlocked; (3) P is a `Door`: locked if already locked or
`P.IsAllowedThrough(from, to, S)` says restricted, **and P is added to `LockedDoorsDuringPlan`**; (4) firefighter pole, box
stall, horse jump, elevator doors: their own rule may lock; (5) diving board: age and pregnancy; (6) ladder: age and traits
discourage. Then each privacy handler on the lot.

`CommonDoor.IsAllowedThrough(from, to, S)`: lot hidden rooms (from, to) -> restricted; S's service ignores locks -> allowed; S
stray and not greeted -> restricted; the door's one-way room -> restricted; S a horse and the door kind; fire on the lot for a
barn door or a pet; S's `IgnoreDoorLocks`; then the lock type: Anybody allowed, Nobody restricted, SelectedActor /
SelectedHousehold / OwnerList / VIPRoom / Pets read S.

Read as "which answers are the same for a class of Sims":

| Sim | portal object | the "unlocked" answer depends on | shared? |
|---|---|---|---|
| ordinary: human, no tomb mummy, no proprietor role, `Service == null`, not stray, no `IgnoreDoorLocks` | a `Door` whose `IsAllowedThrough` is `CommonDoor`'s own, lock type Anybody | the lot's hidden rooms, the door's one-way room (and its lock type staying Anybody) | **yes** (kind 1) |
| ordinary | not a `Door`, and none of pole / box stall / horse jump / elevator doors / diving board / ladder (stairs, arches, non-`Door` gates, `NonAnimatedDoor`, `SecretDoor`, ...) | nothing: the handler names no rule for it; the answer is the planner's initialLocked | **yes** (kind 2) |
| ordinary | a `Door` with lock type SelectedActor, SelectedHousehold, OwnerList, VIPRoom, Pets | the Sim, its household, the resort's guest lists | no (Nobody never answers "unlocked") |
| ordinary | `DoorSingleGender`, `Turnstile` (own `IsAllowedThrough`) | gender, venue hours, tickets | no |
| ordinary | pole, box stall, horse jump, elevator doors, diving board, ladder | posture, running interaction, age, pregnancy, traits, greeting | no |
| any | any portal on a lot with a second handler (privacy situation, a mod), or with event 59 holding another callback | that handler | no |
| not ordinary (pet, horse, stray, service, mummy, proprietor, `IgnoreDoorLocks`) | any | the Sim | no |

The Sim facts of the first column have no setter to hook (`AssignedRole` and `Service` are public fields), so they are read
fresh at every plan instead (cheap: six field reads). What can change a shared answer, and what reports it:

| input | writers | reported by |
|---|---|---|
| lock type, lock owner, one-way room | `CommonDoor.SetLockTypeAndOwner` (both), `SetLockTypeOwnerList`, owner list add / remove | doorgen bump (existing) |
| ... without the setter | `CommonDoor.ImportContent` (a lot placed from the bin), save load by reflection (docs/105 §10.1) | a new object id has no row; a world load starts a new generation (new: the arm op bumps) |
| lot hidden rooms | `Lot.WorldBuilderSetHiddenRoom`, `Lot.UpdateRoomMarkers` (the only writers of `mHiddenRooms`) | doorgen bump (**new**, `DoorShare.cs`) |
| the lot's handler list | `Lot.add_PortalPathPlan` / `remove_PortalPathPlan` | doorgen bump (existing); ts3fixes also re-reads the list object at every note |
| event 59's callbacks | `World.OnPortalLockedCallback +=` (a mod) | the delegate slot `0x11D72C4` in the row (existing); ts3fixes re-reads the delegate object at every note |
| portals added / removed, lot load / unload / rebuild | portal ICs, lot world events | DoorMemoX's native generation (existing) |
| fire, greeting, roles, occults, resort, gender, age | - | not inputs of the two shared kinds; their bumps still clear rows (harmless) |

## 4. Built: `[RouteSnap] DoorShare` + ts3fixes `doorshare`

**ts3fixes `doorshare`** (`DoorShare.cs`, `DoorShareCore.cs`; tuning `kDoorShare`, default off; needs `doorgen`, read at every
plan). Observe-and-send only; no answer is changed in script.

- Per plan, next to the scope op in `UfPlanBody`: UserDiag **op 40** (route handle, 16 bits): bit 0 = the follower is of the
  ordinary class now, bit 1 = the route is the selected (or a selectable) Sim's own user-directed interaction (the rule of
  `npmhousehold`), bits 3-15 = a fold of the Sim's object id. A Sim outside the class sends 0. The answer says whether native
  DoorShare is on; notes go out only then.
- Per answered ask, a hook after `LotManager.PortalLockedCallback` (not `Sim.OnPortalPathPlan`: every lot holds a delegate to
  it; not `Lot.OnPortalPathPlan`: `invcache` swaps it): when the answer is "unlocked", the Sim is ordinary now, the lot's
  handler list is `Sim.OnPortalPathPlan` alone, event 59 holds `PortalLockedCallback` alone and the portal is of kind 1 or 2,
  UserDiag **op 41** (hash of portal id, Sim id and rooms; kind).
- Two more doorgen bumps: the hidden-room writers.

**ts3native `[RouteSnap] DoorShare`** (`doorshare.cpp`, `doorshare-core.h`, in the door stub before DoorMemoX):

- A live "unlocked" answer whose note matches the ask (hash) and whose job is of the ordinary class for the asked Sim becomes
  a row keyed **(lot, portal owner, room from, room to, initialLocked)**: no Sim. 1,024 rows, 4 a set, one row a key.
- A later ask for that key is answered 2 without a host call when the row's generation (DoorMemoX's native part + doorgen's,
  always) and delegate slot are the current ones, a live ask confirmed it within `DoorShareMs`, and the job may take it:
  ordinary class for the asked Sim, scope 2 or 3 (unseen, seen NPC), not a player command.
- Only "unlocked" is kept, as in DoorMemoX: a "locked" answer adds the door to `LockedDoorsDuringPlan` per plan.
- An ordinary-class Sim's live answer that is not a noted "unlocked" removes the key's row.
- Every `DoorShareCheck`-th valid answer is asked anyway and compared (a live ask of a household or command job with a
  valid row is compared too); one difference turns DoorShare off for the session: `FAIL door share`. Mode 2 answers nothing
  and compares every valid row.
- Nothing is answered or kept before ts3fixes doorgen armed the generation.

| key | default | meaning |
|---|---|---|
| `[RouteSnap] DoorShare` | 0 | 0 off (not installed), 1 answer, 2 shadow; runtime `native_set doorshare` (+0x00 of `ts3native_doorshare`) once installed |
| `DoorShareMs` | 60000 (v1: 5000) | a row answers only while a live ask confirmed it this recently (1-3600000) |
| `DoorShareCheck` | 8 | 1 in N valid answers is asked anyway |
| `DoorShareHousehold` | 0 | 1 = household, selected and unknown-scope jobs take shared answers too; a player command never does |
| ts3fixes `doorshare` (`kDoorShare`) | off | sends ops 40 / 41 |

Needs `DoorMemoX` present (even 0: the door stub and the generation hooks), `[UserDiag]`, ts3fixes `doorgen` on.

**Exactness.** For the two kinds the game's answer is a function of the key and of inputs that all move the generation or the
delegate slot (§3), so a row in the current generation is the answer a live ask would give: exact by argument, for the class
as read at the job's START (a job already answers all its asks "as of one moment" through the within-job memo, docs/159
§4.1). `DoorShareMs` is not part of the argument. It bounds the damage of an input nobody found: a mod that writes a door's
lock field directly, for example. **What could differ if such an input exists:** an NPC plans through a door that became
locked for it up to `DoorShareMs` ago (5 s, about 5 sim-seconds at speed 1) and is stopped at the door when it gets there, as
after any lock change during a walk; the sampled check then turns the feature off. No answer is ever turned from "unlocked"
to "locked", and no door the game forbids at the time of the live ask is stored. There is no deliberately stale answer in
this build, so there is no latitude switch; `DoorShareMs` is the knob if one is wanted (raise it for more hits, lower it to
250 to match DoorMemoX).

One known window [verified source]: a doorgen hook bumps after EA's setter returned. If the setter's own native call waits on
`+0x918` behind the running job, that job keeps using the old row until it ends. The job started before the change and a
vanilla sync plan in its place would have finished before it; the sampled check can still see it as a difference and turn
DoorShare off. Not seen with DoorMemoX (0 mismatches in 1,413 re-asks over the three on runs), which has the same window.

**Household.** Default: household, selected and unknown-scope jobs ask live, exactly as today (their answers are still
compared against a valid row). `DoorShareHousehold=1` (or `[Experimental] HouseholdToo`) lets them take rows; a route marked
as the selected Sim's own command never does. Sims off the ordinary class (pets, horses, service Sims, role proprietors,
mummies) never take or make rows.

**Lines.**

```
door share: mode M (household H, check 1 in N, T ms), ts3fixes class ops A notes B; door asks N (before the plan a, in
  Route_PlanLocked b, after c; of an ordinary-class job d), different keys >= K1 with the Sim, >= K2 without; answered H
  (~S ms at the mean live wait), checks C mismatch 0
door share misses: no row, other generation, older than DoorShareMs, doorgen not armed; a valid row not used: no class,
  another Sim, household / unknown scope, player command; live asks N (Q ms): rows written D door + I inert, unlocked
  without a note, unlocked for a Sim outside the class, not unlocked, rows dropped
```

K1 / K2 are the distinct-key counts §1 lacks (lower bounds: one bit a hash). `live asks N (Q ms)` is the wait of the asks
that still went out (queue + service + return, as the job's `host`).

**Expected** [inferred, to be measured]: the ceiling is the share of asks that are kind 1 or 2 from ordinary-class NPC jobs.
In s159 16% of asks came from household / unknown-scope jobs and 6% of live answers were not "unlocked"; if most of the
rest are plain doors and stairs, 60-75% of the 17,580 host calls go, which is 4-7 s of the 6.1-9.5 s host wait outside the
planner and most of the 2.0 s inside it. K2 against the ask count says how close the ceiling is.

## 5. The other two options

**Batch: one round trip for all portals of the lots on the route.** Not built. The ask set of a job is produced by the lot
search itself (`LotPortalSearch` has no early exit and walks the whole room graph the start room reaches); one round trip
would have to ask every portal of every touched lot in both directions for this Sim, which is 20-100 dispatches a plan
against 10 today (docs/105 §4.7, docs/159 §4 "prefetch") and adds each locked door to `LockedDoorsDuringPlan` whether the
search reaches it or not. With DoorShare the asks that remain are the Sim-dependent ones, where a batch would have to run
the same per-Sim managed code anyway.

**Skip asks that cannot change the outcome.** None found: a portal's answer is its edge cost in the lot search.

**The goal step's asks on the sim thread before the job is queued** (brief item 4). Not as such, and not built:

- The sim thread cannot know the keys. They come out of `LotPlanner_GoalRooms 0x63F490` -> `LotPortalSearch`, which runs on
  the lot graph under `+0x918` and writes the shared lot A\* state (docs/87 §1): running it at START is running the job's
  first part on the sim thread, which is the sync plan the async job replaced.
- A **predicted** prefetch is possible: at START, on the sim thread, ask the keys this Sim's last job (or any job on the same
  lots) asked, and seed the job's memo. It is small in code (the START IC already runs on the sim thread and a sync plan fires
  event 59 from the same place), but not safe as it stands: a predicted ask that the search then does not make has still
  run the handler, and a "locked" answer has put a door into `LockedDoorsDuringPlan` that this plan never met (read by the
  route-fail handler's nearest-locked-door scan). It also moves the dispatch cost (about 20 us an ask, docs/159 §2) onto the
  sim thread for every predicted key, hit or not.
- With DoorShare the prefetch has little left to fetch for an ordinary Sim. If the next storm's `door share misses` line
  still shows a large `no row` count from keys that are locked or Sim-dependent, the prefetch restricted to keys whose last
  answer was "unlocked" for this Sim (and seeded into the per-job memo only) is the next build; its size is K1 - K2.

## 6. Checks run (no game)

- `doorshare-host-test.cpp` (Linux, clang, no libc; `doorshare-test.h` as is): the table (put by note, hit for another Sim,
  generation / delegate / age, one row a key, a full set, drop), the class / Sim / scope / command rules, the sampled check
  and its self-disable, shadow, op 40's take, the switch without the install: pass. `workerhold-host-test.cpp` builds again
  (it lacked a `Sleep` stub since 32967a77): pass.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors, no
  warning in the new or changed lines.
- `tools/selftest/doorshare` (C# core; the same hash and fold vectors as the native test): ok.
- `TS3_CSC=... node tools/setup/build-fixes.ts --tag doorask`: built, 0 unresolved references. `node tools/selftest-all.ts
  --ci`: 0 FAIL. `docs/data/swap-stack-audit.csv` regenerated (three new swaps, none reaches a yield).
- No new address: `ts3addr_count` stays 1496.

Not run: MSVC (`/W4 /WX`), the Windows selftest (`TestDoorShare`), anything in the game. Unverified in particular:

- that ops 40 / 41 arrive as written (the op path is doorgen's, which runs) and the hash matches on real asks: `rows written`
  must be > 0 and `unlocked without a note` must be plausible (Sim-dependent portals only);
- the class and kind rules against the live game: the sampled check (`mismatch 0`) is the proof, shadow mode first;
- the cost of the `PortalLockedCallback` hook on sync plans: three id lookups, two reflection reads and one IC per
  "unlocked" answer while `doorshare` is on; nothing but one flag test while it is off;
- mods: a mod handler on a lot or on event 59 makes ts3fixes send no note for those asks (then DoorShare changes nothing
  there); a mod that replaces `Sim.OnPortalPathPlan`'s body is seen only by the sampled check.

## 7. Next storm

Repro `ip-doorshare-storm` (its first step turns ts3fixes `doorshare` on). The s159 on-arm ini plus:

```
[RouteJobPhases]
HoldProbe=1
HostSplit=1
[RouteSnap]
DoorShare=2        ; arm A: shadow (baseline + exactness: checks > 0, mismatch 0, answered 0)
;DoorShare=1       ; arm B
;DoorShareHousehold=1   ; arm C, on top of B
```

Read: `door share:` (asks by part, K1 / K2, answered, mismatch 0), `door share misses:` (what still goes out and why),
`route plans: ... hostCalls`, `route task serve: ... served ... queue`, `worker hold parts` (host ms before the plan), the
sim's `+0x918` waits in `hitch totals`. `doorshare-check` is the offline rule check of the switch.

## 8. Files

`native/ts3native/doorshare.cpp`, `doorshare-core.h`, `doorshare-test.h`, `doorshare-host-test.cpp`; `hostcalls.cpp` (the
stub calls it; `DmxGenNative`; the arm op bumps the generation), `ts3native.cpp` (job class, part counters, log call),
`userdiag.cpp` (ops 40 / 41), `ts3native.ini`; `mods/ts3fixes/src/DoorShare.cs`, `DoorShareCore.cs`, `UnseenFallback.cs` (one
call), `UserDiag.cs`, `Fixes.cs`, `Instantiator.cs`; `tools/selftest/doorshare`, `tools/harness/repros.json`
(`doorshare-check`, `ip-doorshare-storm`), `docs/registry.md`, `docs/68-hook-conflict-map.md`.

## 9. s163 / s164: exact, but one generation for all rows; v2 = stamps per owner and per lot

2026-11-20, same branch. Inputs: `ts3native-full.log` of s163 (`DoorShare=2`) and s164 (`DoorShare=1`), the source, the
decompiled C#. **v2 has not run in the game or under MSVC.**

### 9.1 What the runs say [measured]

| | s163 shadow | s164 answer |
|---|---|---|
| door asks (before the plan / in `Route_PlanLocked`) | 34,123 (19,725 / 14,398) | 33,669 (20,486 / 13,183) |
| different keys with / without the Sim (lower bounds) | 5,140 / 1,033 | 5,148 / 1,008 |
| answered | 0 | 14,592 |
| checks / mismatch | 3,260 / 0 | 692 / 0 |
| misses: no row / other generation / too old | 5,276 / 11,853 / 0 | 4,848 / 11,434 / 171 |
| asks of a job in no class | 1,937 | 2,717 |
| live: without a note / Sim outside the class / not unlocked | 683 / 1,029 / 1,110 | 550 / 1,335 / 924 |
| host calls | 18,510 | 14,789 |

Exactness held (3,952 checks, 0 differences). The table is a thousand keys, yet 11,434 asks found their row in an older
generation: v1's one generation is moved by every portal IC, every lot world event and every doorgen bump anywhere in the
world, and each move ends all rows. With 1,008 keys a steady state needs about a thousand live asks, not fifteen thousand.

Which source moved it cannot be read from these logs (the IC and event counts are not in a log line). Two indirect readings:
doorgen's managed bumps were 1,413 for the whole session and come in a burst at the storm's start (s159: 1,219 in the first
minute, 36 in the second) while DoorMemoX's own generation misses stay level between the minutes (621 / 414), so the steady
source is native: the portal ICs. The storm's boats, gangways and ladders add and remove their own portals as they move
(`AddPortal` / `RemovePortals`), and doors set their portal cost [inferred; the new `door share changes by portal IC:` line
counts each IC, `door share changes:` each world event and doorgen source, and `door share misses:` splits "a stamp moved" by
owner / lot / global / delegate].

### 9.2 Which events can change a row's answer [verified, decompiled C# + the IC signatures]

A row answers "unlocked" for key (lot, portal owner P, room from, room to, initialLocked). §3: for kind 1 the answer is a
function of P's lock type and one-way room and of the lot's hidden rooms; for kind 2 of nothing; both need the lot's handler
list and event 59 to hold the game's handlers alone. The planner's own lock test (cost >= 1e6, the AGS mask, the forbidden
list) is not in the answer: its result is the key's initialLocked.

| event | names | can it change a row's answer? | v2 clears |
|---|---|---|---|
| `AddPortal`, `AddPortalLocationVariation`, `Add{DivingBoard,WaterSlide,Pool,Platform,Stair}Portals`, `RemovePortals` (IC slots 4-11) | the object (arg 1 = `objId`) | not for another object. For this one only if the id now stands for another object | that owner's rows |
| `SetPortalCost`, `SetPortalAgeGenderFlags` (12, 13) | the object | no: they feed the planner's lock test, i.e. initialLocked, which is in the key | nothing |
| `SetPortalLockStatus` (14) | the lot (arg 1 = `lotId`) | turns the lot's asks on / off with its first / last handler | that lot's rows |
| `OnExitBuildBuy` (22; not in v1's generation) | - | rooms may have new numbers | all |
| World events 5, 7, 9, 0x0A (load) | - | a new world | all |
| 0x0D, 0x0E (lot added / removed) | - | kept conservative; a new lot also adds its handler (below) | all |
| 0x0F, 0x10 (lot bounds, lot type) | - | no: no shared rule reads either (`CommercialLotSubType` is read by the VIPRoom lock type only) | nothing |
| doorgen: `SetLockTypeAndOwner` (both), owner list set / add / remove | the door (`this`) | lock type, one-way room | that owner's rows |
| doorgen: `Lot.add_PortalPathPlan` / `remove_PortalPathPlan`; `WorldBuilderSetHiddenRoom`, `UpdateRoomMarkers` | the lot (`this`) | handler list; hidden rooms | that lot's rows |
| doorgen: greeting, household add / remove, fire and fire buffs, `IgnoreDoorLocks`, occults, roles, resort VIP data and closing, gender / age / pregnancy | - | no. Greeting is read for strays only; household for the SelectedHousehold lock and the tomb-mummy test; fire only makes a pet or a barn door skip the lock type, and Anybody allows either way; resort data for the VIPRoom lock; role, occult, flag are class facts read at every plan; age / gender for single-gender doors, diving boards, ladders, elevators (not shared) | nothing (DoorMemoX's generation still moves) |
| the arm at world load; a doorgen bump from an older package; a source this build does not know; a door or lot hook that could not read its id | - | unknown | all |
| event 59 delegate slot | - | another callback | per row, as before |

Stamps: 2,048 per-owner and 256 per-lot counters indexed by a hash of the id, plus one global. Two ids that share a counter
clear each other's rows: more asks, never a wrong answer. A row stores the three values it was written under and answers
only while all three are unchanged. doorgen's op 36 now carries the source (16 bits) and the id's hash; an older package's
plain bump clears everything, a newer package on an older ts3native is a plain bump (the hash is never 1, the arm value).

### 9.3 Routers outside the class

The storm had no pets (`pet_candidates 0`), and the logs do not say who the 2,717 asks of jobs in no class were. Two
additions, both from the same C# reading:

- **No actor** (new class, op 40 bit 2). `PortalLockedCallback` resolves the routing object with `ObjectFromId<Sim>`; for a
  boat or a car that is null, and `Sim.OnPortalPathPlan` starts with `if (args.Actor != null)`. So for such a router every
  portal on a lot with the game's handler alone answers initialLocked. ts3fixes sends the class when the route's follower is
  no Sim, and notes such answers as kind 3. A kind 3 row serves only no-actor routers; a kind 1 / 2 row serves both classes
  (its answer is "unlocked" for initialLocked 0 either way). The storm's boat routes are the likely bulk of "no class"
  [inferred; the new `door share no class:` line counts asks by ts3fixes' reason: no op 40, no description, horse, stray,
  tomb mummy, proprietor, service, IgnoreDoorLocks].
- **Cats, dogs and wild animals are ordinary** (v1 required a human). `CommonDoor.IsAllowedThrough` treats a non-stray pet
  like a human except for `(barn door or pet) and fire on the lot`, which skips the lock-type switch and allows; with lock
  type Anybody the switch allows as well. The Pets lock type is not shared. **Horses stay out**: their rule reads the door
  kind (barn door, double gate, electronic gate, `CanLargeAnimalUse`); a horse class with those door facts in the kind is
  provable but there were no horses to measure. Strays stay out (greeting per Sim and lot), as do service Sims (some
  services ignore locks: provable as a class per "ignores locks" bit, again unmeasured), proprietors and mummies.

### 9.4 The floor of live asks, and DoorMemoX

After v2 a storm like s164 should still make [inferred from s164's counts]:

| what | about | lever |
|---|---|---|
| first ask of a key, and again after its door's or lot's own change | 1,000 + the owner / lot bumps that hit used rows | none (exact) |
| one confirmation per key per `DoorShareMs` | at 60 s: up to 2 x the keys in use; the sampled checks cover part of it | `DoorShareMs` |
| the sampled check | hits / `DoorShareCheck`: about 3,500 at 1 in 8, 900 at 1 in 32 | `DoorShareCheck` (3,952 checks so far, 0 differences) |
| answers that are not "unlocked" | 900-1,100 (a locked door must be asked per plan: `LockedDoorsDuringPlan`) | none |
| "unlocked" answers that read the Sim (owner / household locks, elevators, ladders, privacy lots) | 550-700 | DoorMemoX (per Sim, 250 ms) |
| routers in no class | up to 2,700 before the no-actor class; what stays is the `no class` line | a horse or service class if it shows |
| household / unknown-scope jobs | 0 in s163 / s164 (16% of asks in s159) | `DoorShareHousehold` |

So about 5,000-6,000 host calls at `DoorShareCheck=32` against 14,789 (s164) and 18,510 (s163): the floor is the checks, the
locked answers and the Sim-dependent ones.

**Merge with DoorMemoX: no.** They answer different questions. DoorShare's rows are exact by argument and can live long;
DoorMemoX's rows are per Sim, cover answers whose inputs are not all hooked (posture, running interaction, privacy,
elevators, venue hours), and stay bounded by 250 ms and its coarse generation for that reason. With DoorShare in front,
DoorMemoX sees only what DoorShare cannot share (s164: 3,149 hits). Giving DoorMemoX the per-owner / per-lot stamps would
not help it: its misses are "no row", not "other generation" (13,924 vs 1,397).

### 9.5 Checks run (no game)

- `doorshare-host-test.cpp`: the stamps (each event that names the row's door or lot, and each global one, ends the row
  once; other objects', other lots' and the proven-irrelevant events do not), the miss split by stamp, the no-actor class
  (kind 3 rows, upgrade by an ordinary Sim's note, no downgrade, no cross-class check or drop), the reasons: pass.
- `tools/selftest/doorshare`: `IdHash`, class and reason bits, source numbers against the native values: ok.
- clang-cl `/Zs /W4` (`ts3native.cpp` Steam and `/DTS3A_TABLE`, `selftest.cpp`): 0 errors, warning counts as the base.
- ts3fixes builds (0 unresolved); `selftest-all --ci`: 0 FAIL.

Unverified: MSVC, the Windows selftest, the game. In particular that `slot[1]`, `slot[2]` of the portal ICs are the object /
lot id as the managed signatures say (`ulong objId` first; `AddPortalLocationVariation`'s `slot[3]` handle is already used
that way by P4), and that a route's follower id is the routing object id of the ask (if not, the class is never used for
that router: `another Sim` counts it).

### 9.6 Next run

ts3fixes package from this branch (`doorshare` on; `doorgen` on at world load). Arm A shadow first if a second exactness
pass is wanted for the new class and the stamps; then B:

```
[RouteSnap]
DoorShare=1            ; arm A: 2
DoorShareMs=60000
DoorShareCheck=32
;DoorShareHousehold=1   ; arm C
[RouteJobPhases]
HoldProbe=1
HostSplit=1
```

Read `door share misses:` (`a stamp moved` should fall from 11,434 to hundreds, split by owner / lot / global),
`door share changes:` and `... by portal IC:` (what would have ended every row in v1; if `everything` is not near zero, the
named source is the next thing to scope), `door share no class:`, `route plans: ... hostCalls`, `worker hold parts`.
