# 670. The +0x918 waits and the unseen sync plans left after s143

> **Carve-out note (post/route-lock-1120, 2026-11-20):** this branch ports `[PlaceTestRO]`, `[EditPathRO]` (with `StartGuard` / `Retry`), `[GoalValidRO] Hits` / `HitsShadow` and `[ConnRoomsRO]` from the combo branch. The `KindsUnseen` mask line (§4) and `[Discourage] DeferPrivateFpAdd` (§16-§17) are described below but are not part of this branch: upstream's own-task gate differs from the combo's `KindsUnseen`, and DeferPrivateFpAdd was left for a later PR. Sections about those keys are history.

2026-11-20 (UTC). Inputs: the Isla Paradiso route storms s142 (profiling) and s143 (fixes off / on), 4 min each, their
`ts3native-full.log` and `route-stutter-report.ts` tables; Ghidra headless (DecompileTo, CallerTree, XrefsTo) and capstone
on the Steam 1.67 code; our source. Built: `[PlaceTestRO]` (ts3native, default off) and a log line for `KindsUnseen`.
**Nothing here has run in the game.** Branch `feat/lock918-syncplan`.

Added 2026-11-20, later the same day (§7-§10, branch `feat/lock918-rest`): the s146-s148 read, why the wait stays after
each site fix, the lock-level options, `[EditPathRO]` (built, NOT RUN) and the waiters left. §5 and §6 are the state before it.

Added 2026-11-20, after s151 (§11-§13, branch `feat/lock918-readers3`): `[EditPathRO]` held in the game; `[GoalValidRO] Hits`
(the validator's hit loop on the clone, built, NOT RUN); the two trampoline keys, the gate 0 / gate 12 callers and the own-job
wait read. §9 and §10 are the state before it.

## 1. What the lock is and who holds it [verified: source, docs/60, docs/81, docs/94; s143 logs]

- `+0x918` is the EA lock (critical section + count, `Lock_Acquire 0x4E16F0` / `Lock_Release 0x4E17B0`) inside the routing
  world singleton (`RoutingWorld_Get 0x8292D0`). It serialises everything under the lot planner `world+0x95C`: the lot
  table, the per-level footprint quadtrees, footprint objects and their refcounts, the planner's node pools, and one more
  thing found here: the global polygon clip scratch `0x1158580` (§3.2).
- **Holder:** the game's own `Route_PlanLocked` takes it for the whole plan. With `[RouteAsync]` the plan runs as a job on
  the route worker, so the worker holds it from the job's start to its end: goal step, candidates, world A*, cell A*,
  water, post passes. It is not held "too long" by our code: the hold is the game's, moved to another thread. Two
  windows already exist: while the worker waits for the sim thread's answer to a host call (the managed door ask) the sim
  serves that call from its `+0x918` wait pump and borrows the lock inside the callback (`rgBorrows`), and `TailUnlock`
  lends the lock during post passes 16-19. s143-on: 1859 jobs, 36.4 s of job time in 4 min (15% of wall time), four
  `long job` lines, the longest 535 ms.
- **Waiters:** every sim-thread taker waits for the rest of the running job. s143-on: 272 waits >= 1 ms, 7.67 s, longest
  170 ms; 1509 pump waits, 8.2 s in total. The frame class `lock918` (17 frames >= 150 ms in s143-on, 51 off) is several
  such waits in one frame, not one long hold: the on arm's totals show no wait behind a job >= 250 ms.

Wait keys (the return address that names the taker), `route lock wait:` lines >= 20 ms, ms per 4 min storm:

| key | taker | s142 | s143 off | s143 on | answered read-only today |
|---|---|---|---|---|---|
| `0x82F4AB` | `RoutingWorld_PortalCheckLocked 0x82F470`: placement footprint gather (§2) | 6077 | 4643 | 3222 | **no (this doc)** |
| `0x85F6CB` | `RouteFollow_EditPath 0x85F5A0` gather (S2's fallback path) | 2495 | 2191 | 1129 | S2 when it does not give up |
| `0x75A64C` | `GoalPoint_Validate` obstacle query | 1532 | 1343 | 995 | `[GoalValidRO]`, empty answers only (s143-on: 405 answered, 187 found footprints and waited) |
| `0x85FE10` | `Locomotion_ObstacleCheck` | - | 510 | 386 | S2 |
| `0x75AA16` | `GoalPoint_Validate` goal footprint gather | - | 129 | 302 | no |
| `0x833697` | `0x833680` (footprint update path, not read here) [inferred] | - | 138 | 275 | no |
| `0x62EF7A` | lot transform wrapper (gate 0) | 528 | 616 | 249 | `[Gate0RO]` for three callers |
| `0xD882D7`, `0x62FB74`, `0x760BFF`, others | | 1490 | 1562 | 695 | |

So one taker that nobody had looked at is 44% of the on arm's logged wait time (50% in s142). docs/60 and docs/81 list `0x82F470` as "build
tool, rare": true for a quiet town, wrong in a storm.

## 2. The placement footprint check [verified: decompile + disassembly]

`0x82F470` (thiscall world; lot, level, polygon, vec, mode, opt; `ret 0x18`): lot 0 answers 0 before the lock. Else it
takes `+0x918` (`0x82F4A6`, return = the key), empties the caller's vector, builds the same stack context as the obstacle
query `0x82EDB0` (record `{opt, +0x24 = 1, id 0, options object 0}`, `ctx+0x50 = 0`, `ctx+0x54 = lot`), calls `0x632060`
(`LotTable_Contains 0x628A90`, cell graph `0x62FB10` of the level, `QueryPolygon 0x667560`: pin `Q+0x48`, flush when
deferred, polygon walk `0x6641B0` with visitor `0x662DB0` for mode 0), copies the gathered set into the vector (`+1` on
each footprint), releases, answers 1.

Its only caller is `0xB41120` (cdecl, one record, `al`), called only at `0xB462B7` (in `0xB46160`) [verified XrefsTo /
CallerTree]. Above it: `IC_World_FindGoodLocation*`, `IC_World_FindPlaceOnRoad*`, `IC_World_FindPlacesToDisembarkOnShoreline`,
`IC_World_TestObjectPlacement*`, `IC_Slots_CanAttachToSlotWithOffsetImpl` (through `0xB463F0` / `0xB46800` / `0xB46B20` /
`0xB470C0` and `0xC79BB0`, `0xC7A570`, `0xC7BF70`, `0xCCDD80`). The storm's boat, go-home and meet routes call these on
the sim thread all the time.

`0xB41120`: `rec+0x30 == 0`, `rec+4` (lot) `== 0` or `rec+0` (object) `== 0` answer 1 at once. Else it reads the routing
world's byte `+0x30`, gathers with (lot `rec+4`, level `rec+0x24`, polygon `rec+0xA0`, a fresh vector, mode 0, opt
`0x100`) and loops: a footprint whose id (interface slot `+4`) is the object's (`[rec+0]+8/+0xC`) is skipped; else its
type (interface slot `+8`) goes through a switch (index bytes `0xB41290`, targets `0xB41280`): types 2, 4, 9, 0xC, 0xD,
0x11, 0x15, 0x17 answer 0; 0x12 answers 0 when the world byte is set; 0x13 and 0x18 answer 0 when `rec+0x84` is 0. No
blocking footprint: 1. The vector is released and freed on both paths. **Nothing it writes outlives the call** (refcounts
and the pin are `+1` then `-1`; the flush is a real write and is the one case we give back, §3.3).

## 3. `[PlaceTestRO]`: the check answered without the lock (ts3native, default off)

### 3.1 Design

One call-site retarget, `0xB462B7` -> `PtStub` (`placero.cpp`). On the sim (owner) thread, while a job runs, when the
thread is not the `+0x918` owner, in no job callback and no gate frame (`AvMayRun`), the stub answers the whole check from
S2's read-only protocol, exactly as `[PlacementRO]` / `[GoalValidRO]` do for their sites: QL shared (try only), tripwire
state, QL sequence and job generation checked before and after, `SrEntry` (Contains rules) and `SrCell` for the lot and
level, `SrVisit` / `SrCollect` in place of the game's collector (same filters, slot whitelist, no refcount), then the
loop of §2 inside the QL hold. Anything else calls the game's `0xB41120`: other threads, the three early outs, no job
running, and every doubt (QL busy, lot index or table miss, deferred flush, pin moved, unknown slot, buffer limit,
tripwire, job end, a walk 13 cells deep).

The answer goes to every Sim: it is the game's own answer computed from the same data, not a cap, skip or estimate, so
no household gate applies (as the other RO switches).

### 3.2 The polygon walk and the global scratch [verified disassembly]

The game's walk `0x6641B0` tests every quadtree cell with `QuadTreeCell_ClipScratch 0x66AB70`, which builds the cell's
rectangle and calls the Sutherland-Hodgman clip `0x67C070` (docs/669 §3.1) with `ecx = 0x1158580`: **one global scratch
object** (two point vectors) for all threads [verified: `0x66AC37 mov ecx, 0x1158580`, its only code reference]. Its users
are this walk's two callers: the placement gather and `0x634930` (footprint room ids, under `AddFootprint` /
`RemoveObjectFootprints` / `LotGraph_Build`). In the game they are serialised by `+0x918`. A sim-side clone that called the
game's walk beside a job could clip in the scratch a worker writer is using: wrong answers or a corrupted vector.

So the walk is ours (`PtWalk`, `placero-core.h`): the game's loop transliterated (13 iterators of 0x24 bytes, next-child
and last-child arrays) on the game's own pure iterator helpers `0x669BE0`, `0x669C30`, `0x669CB0`, `0x66A040` (the same
four the point walk `0x663E20` uses, which S2 has run beside jobs since docs/84), and `PtClip` builds the same rectangle
(`0x66AB73..0x66AC48`; the int + table sums are exact because the tables hold only 0, 1/8, 1/4, 1/2, 3/4, 1; the single
mul / add run as SSE scalars in the game's order) and calls `0x67C070` on **a scratch of our own** (sim thread only; the
allocator tags copied from the game's object). The game's loop writes a 14th iterator past its array when 13 nested
non-terminal cells overlap; `PtWalk` reports that as a doubt instead.

### 3.3 What the clone reads and writes

Reads: the lot table and the entry's cell graph vector (S2's `SrEntry` / `SrCell`), `Q+0x24/+0x38/+0x44/+0x48/+0x4C`, the
tree's nodes, leaves and entries, the footprints through whitelisted pure slots, the caller's record and polygon, the
routing world's byte `+0x30`. Those are S2's read set plus the polygon; the worker's writers of that set are W1-W6 under
QL exclusive, anything else is a tripwire. Writes: our scratch (and its growth through the game's allocator, which has
its own lock), our static context, the stub's stack. It skips the game's pin (`Q+0x48` `+1/-1`) and gives the call back
when the game would flush (`Q+0x4C` set with `Q+0x48 < 1`) or when the pin or flag moved during the walk.

### 3.4 Switches, counters, files

- `ts3native.ini` `[PlaceTestRO] Enabled=0` (install), `Mode=1` (start of the runtime switch: export
  `ts3native_placetestro` `+0x00`, `native_set placetestro`), `Shadow=0` (1: with no job running, the game's call then the
  clone, compared; `place test RO: SHADOW MISMATCH ...`, 16 lines). Needs `[RouteGate] Enforce=1`, `[RouteAsync]` and the
  S2 writer set.
- One line a minute: `place test RO: mode M shadow S calls N, with a job J: answered A (blocked B, no footprint E; T ms,
  max U us), the game's call G ms (max V us); given back: state, QL busy, lot, shape, tripwire, job end, deep, may not;
  shadow checks C mismatch X`. `the game's call G ms` is the wait that is left (and, with `Mode=0`, the whole wait).
- Files: `native/ts3native/placero.cpp`, `placero-core.h`, `placero-test.h`, `placero-host-test.cpp`; section PT in
  `steerro-test.cpp`; `ts3addr-ea169.h` (9 rows, unresolved: the EA 1.69 build logs SKIP), `tools/ini-sections.json`,
  `tools/ea-ini.ts`, `tools/harness/native-switches.ts`; repro `ip-placetestro-storm`.

### 3.5 Checks run (2026-11-20, no game)

- `placero-host-test.cpp` (Linux, 32-bit, freestanding): the type rule for every type and flag pair; `PtWalk` equals a
  recursive walk of the same fake quadtree (24 trees x 40 polygons, 6288 cells, same order), visitor stop, terminal
  root, 12 nested cells walked / 13 refused. Pass.
- Selftest cases `TestPlaceCore` and `TestPlaceTestRO` (stub on fakes: pass-through rules, answer from fake footprints,
  every fallback, shadow compare), built with clang-cl and run alone under Wine. Pass. The full selftest and the asi
  need the MSVC CI build.
- `steerro-test` section PT (clang-cl build under Wine, `SRT_ONLY=PT`, the Steam 1.67 code mapped at its addresses): the
  game's `0xB41120` (its whole path: `0x82F470`, `0x632060`, `0x667560`, `0x6641B0`, `0x66AB70` on the global scratch,
  the collector) against `PtRun` on random worlds and polygons: **60000 checks, 60000 equal** (11761 blocked, 48239 free,
  61758 footprints gathered, clone gave up 0), references balanced; the call edges `0xB462B7 .. 0x67C070`, the scratch
  load and both type tables match the image. Its fake trees are at most three cells deep.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: 0 errors, the same
  warning count as before the change.

## 4. The unseen kind 2 / kind 3 sync plans [verified: s143 logs + source]

`[RouteAsync] KindsUnseen` is a **bit mask** (1 Replan, 2 PlanFromPoint, 4 ReplanFromPosition; docs/662 §4 says "try 7").
s143 ran `KindsUnseen=1`. Its counter line `passes 32 0 0, not unseen 67 0 0` is that mask at work: only kind 1 is
admitted. Every own-task plan of kinds 2 and 3 of an unseen route therefore got `START refused` (`NoStart`, recorded as
"kinds off") and planned on the sim thread:

| s143-on `ufplansplit` lines >= 150 ms | n | ms |
|---|---|---|
| kind 3 why 0, `sync (START refused)` | 4 | 927 |
| kind 2 why 0, `sync (START refused)` | 2 | 617 |
| kind 1 why 0, async | 4 | 1038 |
| kind 1 why 4 (seen NPC), `sync (START refused)`: by design | 3 | 608 |

The two on-arm hitches (321 and 332 ms) are one Sim's PlanFromPoint and ReplanFromPoint in a row, both
`FailedBecauseNoPathToDestination` after 57 candidates with 115 cell searches of ~2.5 ms each (cell 286 ms). `FloodMemo`
does not see them as failed floods (stores 14, hits 0 in the whole arm), so the memo is not the answer here; the job is.

**Fix: `KindsUnseen=7`, no code.** The path is the one kind 1 already takes (`ArUnseenPass`: the START passes only when
ts3fixes scoped this very route unseen; household, selected, seen and unknown scope stay refused and plan sync as today).
The kind 2 and 3 hooks and `ArPass` are the ones `WatchdogKind2` and `ForeignKinds=4` already use; selftest case 13b covers the gate.

New log line (log only): with a mask that leaves a kind out, `route async: KindsUnseen=N leaves the own kind K START of
unseen route ... synchronous (bit B not set; unseen STARTs outside the mask a b c)` at the 1st, 2nd, 4th ... such START,
so a partial mask shows in the log.

Known difference of the job from the sync plan (docs/662 §4, unchanged): `KindDeadlines` applies to the own job (250 ms
for kinds 1-3, doubled on the retry, uncut on the third try), unseen Sims only. The two s143 plans above failed with no
path at ~305 ms; as jobs they are cut at 250 ms and fail the same way [inferred].

Why -1 lines (`src Boat...`, `src spot:...`) are async already (they wait in frame polls); the report row "sync plan:
ufplan kind 2 why -1 BoatSpeedFishingBoat" is the nearest `ufplan` line to a hitch, not a sync plan.

## 5. Not proven, not done

- **In game:** everything. First `[PlaceTestRO]` with `Shadow=1` (mismatch must stay 0), then the on arm; the gain is
  what the `0x82F4AB` waits lose minus what moves to the caller's next taker (FindGoodLocation goes on to other placement
  tests; `[PlacementRO]` covers the point test `0x631F10`).
- **MSVC:** the CI build (`/W4 /WX`) and the full selftest have not run on this branch.
- **Deep trees and sub-cell codes:** the offline worlds are shallow; the walk's deeper paths are covered by the host
  model only, and by `Shadow=1` in the game.
- **The other keys of §1:** `0x85F6CB` / `0x85FE10` are S2 giving up (reasons not in these logs); `0x75A64C` is
  `[GoalValidRO]` handing non-empty answers to the game (its hit loop writes refcounts and outputs); `0x75AA16` has no
  read-only answer; `0x833697` is not read yet. Each needs its own design.
- **`sync-plan/water` and `/post` frames:** 150-230 ms frames with 2-6 short sync plans each; none reaches
  `LogFallbackMs=200`, so the logs do not name them. Run the next storm with `[RouteAsync] LogFallbackMs=40` to get their
  `route async: sync plan ... reason` lines.
- **Seen NPC and household own kinds 1-3** stay sync by design (`KindsUnseen` is unseen only; `[Experimental]
  HouseholdToo=1` lifts it).

## 6. Next storm, on arm

```
[PlaceTestRO]
Enabled=1
Mode=1
Shadow=1
[RouteAsync]
KindsUnseen=7
LogFallbackMs=40
```

Read: `place test RO:` (answered > 0, mismatch 0), `route lock wait:` lines with key `0082F4AB`, the `KindsUnseen` pass
line (`passes a b c` with b and c > 0), `ufplansplit` kind 2 / 3 why 0 lines (async now). A cautious first pass runs
`Mode=0 Shadow=1` (nothing answered, only compared).

## 7. Why the wait stays after each site fix (2026-11-20, s146 / s147 / s148 logs, static; branch `feat/lock918-rest`)

Inputs: `ts3native-full.log` of s146-on, s147-on, s148-off and s148-on (4 min storms; s148-on = `[PlaceTestRO] Mode=1 Shadow=1`,
`KindsUnseen=7`), Ghidra headless (`CallerTree`, `DecompileTo`) and objdump on the Steam 1.67 code, our source, docs/152, docs/159,
docs/94 §22-24, docs/189 §17. `[PlaceTestRO]` itself held up in the game: s148-on answered 40,200 of 40,202 checks, shadow 10,223
checks with 0 mismatches, key `0x82F4AB` 2700 ms -> 0.

### 7.1 The finding [measured]

The cost is not a site. It is one lock held by the worker for a whole plan (job time ~15% of wall time) while the sim thread
takes the same lock from many call sites: a sim frame that touches `+0x918` while a job is live waits for the rest of that job,
whichever site touches it first. Remove one site and the next taker in the same frame inherits the wait (docs/152 §1 said this
for four earlier reducers; s148 repeats it):

| `route lock wait:` lines >= 20 ms, ms per storm | s148 off | s148 on | change |
|---|---|---|---|
| `0x82F4AB` placement gather (now `[PlaceTestRO]`) | 2700 | 0 | -2700 |
| `0x85F6CB` EditPath gather | 931 | 2448 | +1517 |
| `0x75A64C` GoalPoint_Validate query | 680 | 1265 | +585 |
| `0x62F782`, `0x83156D`, `0x62FB74`, `0xD866E3` | 558 | 1388 | +830 |
| every other key | 982 | 973 | -9 |
| **sum of the logged lines** | **5851** | **6074** | **+223** |

The arm's `hitch totals` did fall (waits >= 1 ms: 370 / 10,324 ms off, 243 / 7,436 ms on; `lock918` frames 26 -> 17), but the
on arm also ran the job-side switches (`KindsUnseen=7`, memos): no job >= 250 ms is left behind any wait (7 / 1,367 ms off, 0
on), and the longest job fell from 820 to 345 ms. The gain is the shorter holds, not the removed site.

So the sim's `+0x918` wait is roughly (job time share) x (chance that a frame touches the lock while a job is live) x (rest of
the job). A per-site clone pays only when no other taker in that frame still takes the real lock.

### 7.2 Takers ranked, with class (three on arms: s146, s147, s148-on; lines >= 20 ms)

Class as docs/152 §2-3 where it is listed there; "RO+bal" = reads planner state and writes only balanced counters (pin
`Q+0x48`, footprint refcounts) plus the flush / cache-rebuild cases a clone gives back.

| key | taker (sim side) | s146 | s147 | s148 on | sum | n | max | class | read-only answer today |
|---|---|---|---|---|---|---|---|---|---|
| `0x82F4AB` | placement gather `0x82F470` (§2) | 2223 | 2761 | 0 | 4984 | 78 | 165 | RO+bal | `[PlaceTestRO]` (proven s148) |
| `0x85F6CB` | `RouteFollow_EditPath 0x85F5A0` gather, route form (§8) | 1154 | 860 | 2448 | 4462 | 59 | 179 | RO+bal | **`[EditPathRO]` (this section's build)**; S2 only for locomotion |
| `0x75A64C` | `GoalPoint_Validate` obstacle query, footprints found (§9.1) | 651 | 889 | 1265 | 2805 | 46 | 202 | RO+bal, then a hit loop with refcounts and outputs | `[GoalValidRO]` for empty answers only |
| `0x62FB74` | gate 12 `LotTable_GetIfPresent 0x62FB60` (§9.2) | 469 | 63 | 666 | 1198 | 16 | 167 | mixed (reader and writer callers share the key) | `[IdleCheckRO]`, `[FootstepRO]` for their callers |
| `0xD882D7` | an IC body that takes the lock itself; the key is the Mono icall trampoline's return (§9.3) | 533 | 179 | 288 | 1000 | 15 | 148 | unknown | no |
| `0x85FE10` | `Locomotion_ObstacleCheck` steering query | 38 | 156 | 397 | 591 | 10 | 145 | RO+bal | S2, when it does not give back |
| `0x62EF7A` | gate 0 lot transform wrapper | 361 | 77 | 120 | 558 | 10 | 101 | RO on a hit, writer on a miss | `[Gate0RO]` for three callers |
| `0x631814` | gate 12 front door (`IC_World_GetLotFrontDoorImpl`) | 277 | 247 | 0 | 524 | 4 | 161 | RO | `[LotFrontDoorRO]` fallbacks |
| `0x62F782` | gate 12 `LotTable_Contains` (`PortalsForObject`) | 0 | 105 | 362 | 467 | 5 | 144 | RO for the slot helper; `RouteManager_SetPortalCost` is a writer | `[SlotEnabledRO]` |
| `0x760BFF` | gate 10, goal step of a sync plan | 252 | 0 | 142 | 394 | 6 | 115 | **writer, real dependency** (the plan itself) | none possible; `KindsUnseen` moves the plan off the sim |
| `0x83156D` | `RoutingWorld_RouteToObjectCheck` | 0 | 45 | 242 | 287 | 4 | 156 | RO+bal | `[RouteToObjRO]` fallbacks |
| `0x631F40` | gate 12 placement point test | 147 | 124 | 0 | 271 | 2 | 147 | RO | `[PlacementRO]` fallbacks |
| `0x833697` | `0x833680` -> footprint core `0x8321F0` (object footprint update) | 187 | 28 | 26 | 241 | 6 | 82 | **writer** | none (defer family, docs/89) |
| `0x846D21` | avoidance query (`RouteFollow_A`) | 116 | 55 | 0 | 171 | 3 | 94 | RO+bal | `[AvoidRO]` fallbacks |
| `0xD866E3` | as `0xD882D7`, another trampoline | 0 | 0 | 118 | 118 | 1 | 118 | unknown | no |

Share by class of the 18.1 s logged in the three arms: RO+bal / RO 15.1 s (84%; 5.0 s of it already gone with `[PlaceTestRO]`),
writers 0.6 s (3.5%), unknown and mixed 2.3 s (13%: `0x62FB74` and the two trampoline keys).
All of it is behind the route worker; none behind another holder.

### 7.3 What a plan needs exclusive, and what the sim takers do [verified docs/152 §3, docs/159 §3, §3.2 here]

A running `Route_PlanLocked` needs to be alone against: (1) writers of the lot table, the per-level quadtrees, footprint
objects and portals (its frames hold raw pointers to rows, nodes, records and cell tables through the whole search; the in-room
pre-pass also writes per-Sim locked bits and re-inserts tunnel footprints in the shared quadtree); (2) any other plan (one A*
object and node pools per planner); (3) the global clip scratch `0x1158580`; and (4), the catch, **every "reader" of the game**,
because the game's readers are not pure: each one pins the quadtree (`Q+0x48` plain +1 / -1), takes and drops footprint
references (plain +1 / -1, delete at 0), flushes the quadtree when the deferred flag is set and it is the outermost query (frees
nodes), and may rebuild a polygon cache (`0x80E5B0`, which can reach the lot table insert). The worker does the same counter
arithmetic without atomics, so even two readers race.

### 7.4 Lock-level options

| option | what it is | state | what breaks it, how it is seen | share of the wait it could remove |
|---|---|---|---|---|
| A. shared access for readers | the reader's answer from the same structures under the reader / writer lock QL (worker writers W1-W6 exclusive, tripwires for any other writer), without the pin, refcount, flush and cache writes | **this is what the RO clone family is**: the lock-level reader path, built per site because each site's read set and balanced writes had to be read in the disassembly. Running since docs/84; `[PlaceTestRO]` and `[EditPathRO]` are two more sites of it | a writer outside W1-W6 (tripwire T4 / T-S7, the clone drops its answer), a deferred flush or invalid cache (given back to the game), an unknown footprint class (slot whitelist) | up to the RO share, 84% of the logged wait, **only when every reader that still fires in a job-live frame is covered** (§7.1). Left to cover after this build: `0x75A64C` non-empty, the given-back calls of the existing clones, the `0x62FB74` readers |
| B. generic sharing at `Lock_Acquire` | let a proven-reader key through with the game's own code while the job runs | **not safe**: (4) above. The pin and refcount arithmetic of the sim and the worker interleave, a sim flush frees nodes under the worker, the clip scratch is shared. Making it safe means removing those writes per call path, which is option A | - | 0 |
| C. the worker parks at a safe point and hands the real lock over | a sim-waiting flag; at a consistent point the worker stops, the sim's taker runs the game's code alone, the worker continues | built in three narrow forms: the door-ask borrow (`rgBorrows`: the sim serves the job's managed call inside its `+0x918` wait pump), `TailUnlock` (post passes 16-19, proven closed passes, docs/150 §11) and P5b / P5c on the snapshot clone. **A general form is not provable**: at an arbitrary point of world / cell / lot A* the parked frames hold node, row and record pointers (docs/159 §3.1); a reader admitted there may flush a quadtree or rebuild a cache, a writer may free what the frames point at. Detecting the write afterwards does not help, because the worker cannot be unwound from the middle of the game's search without running the game's cleanup over the changed data. The existing 32nd-expansion stub is a deadline check, not a point with empty frames | not detectable in time for readers' flush / cache writes short of the pre-checks a clone already makes; writers: P1's writer leaves (`[RouteSnap] Count`) see them, too late | readers: nothing a clone does not already give without parking. Writers and sync plans: 3.5% + part of the unknown 13% |
| D. plan on a snapshot, lock free for the sim | `[RouteSnap] Mode=2` + `ReleaseLock` / `FreeLock` (P5, P5b, P5c, P6), version / write-ring revalidation, replan on a stale cut | **built and run in October, stopped 2026-10-25** (PLAN "stop P6, ship off", docs/189 §17): no rung lowered the sim's job wait (143-152 ms/s in every off arm, no on arm below that spread), only 20-27% of armed jobs were taken, and `FreeLock=1` was in crashes M, N and O (docs/67 §15-17) | stale cut (write ring), clone tripwires; the crashes were use-after-free outside the planner | measured: ~0 |
| E. one wait per frame: hold the next job's start after a sim wait | scheduling only (the `g_rgOffSimOpen` hold-off exists) | not built; measured ceiling from the logs: of the waits >= 20 ms, those that start < 8 ms after another wait ended behind a *new* job are 157 / 266 / 382 ms per storm (2.6-6.8%); the other back-to-back waits are behind the same job retaking after a `TailUnlock` window | starves the worker when the sim then waits for that very job (job-wait class) | <= 3-7% |

**Verdict.** No new lock-level change is provably safe beyond what exists: the lock already is shared for readers through QL
(option A), and the three hand-over forms that could be proven are in. What is left is to finish option A's coverage and to
keep shortening the holds on the worker (`KindsUnseen`, the memos, the cell index), which s148 shows is where the wait fell.
Expect little from any single clone and a step only when the frame's last real taker is gone; the tables above are the list.

The owner's later notes (answers from a slightly older consistent snapshot, relaxed spacing checks) do not open a new lock
design here: the doubts above are memory safety (frees, non-atomic counters), not exactness, and the snapshot design is option
D. Nothing in this section's build uses them; every answer built here is the game's own.

## 8. `[EditPathRO]`: the EditPath gather for the route form (ts3native, default off, NOT RUN)

### 8.1 The taker [verified `CallerTree`, decompile, disassembly; s146-s148 stall-watch stacks]

Key `0x85F6CB` is the return of `0x82F0B0` in `RouteFollow_EditPath 0x85F5A0`. EditPath has two callers: `Locomotion_ObstacleCheck
0x85FD10` (S2's detour, start == goal) and `RouteFollow_EditPathForRoute 0x85FF30` (`0x85FFC2`), itself called by
`IC_Route_SetAGS`, `Route_PlanCommon` (`0x760F92`, the plan's tail) and the two path insert ICs (`0x758D4A`, `0x7627F4`). The
storm's waits are the second caller, which no switch answered: the script stack under the 27 stall-watch lines with this key is
`Route_SetAGS` 20, `Route_Plan` / `Replan` / `PlanFromPoint` 5, `Route_InsertCustomPathAtIndex` 2. The s148 report's "S2 giving
up" is therefore not what happens: S2 is never asked. (S2's own give-back reasons were in no log; a `steer RO:` line now
prints them once a minute.)

### 8.2 What the call does [verified]

`0x85FF30` (cdecl path, ids, opt): for every node of the path (slot `+0x48` count, `+0xD4` node) it calls EditPath (cdecl: wm
`[0x11ECBC4]`, world, planner, ids, start, goal, opt, excl, ign1, n1, ign2, n2) with start = the node, goal = `0x81D7A0(node)` =
node + dir `+0x10` x len `+0x20`, excl = 0, both ignore lists empty [`0x85FF84..0x85FFD1`]. `ids` is the path's own id list
`+0x58` (docs/107 §9), a sim-side output.

EditPath: `0x85AE60` on both points; the goal's level is set to the start's (`0x85F66F`); `0x82F0B0(world; start, goal, vec,
mode 1, opt, width 0.0)` takes `+0x918`: lot = `World_LotLookup(world+0x964)` of the goal, else of the start with the two points
swapped (`0x82F1A1`), else al 0; then `0x631E20`: `LotTable_Contains`, the cell graph of the first point's level, both points
through `0x62ED40`, the segment walk `0x667400` (pin, flush when deferred, `0x663B20` with visitor `0x662DD0`, no record:
`ctx+0x44` is 0), the set copied into `vec` in pointer order with +1 each. Back in EditPath: the consumer's lot is
`World_LotLookup(wm)` of the goal's raw point, else the start's; both points go through `0x62EF30` of that lot; per gathered
footprint: id, type (0x14, 9, 0x11, 0xC, 0xD, 0x17 skipped), the ignore lists, "already in ids", then, when slot `+0xC0` holds
and a lot was found, the level-0 segment test `+0xE8(0, a, b)`; a pass appends the id (`0x572400`). The polygon work after the
test (`0x80E5B0`, slot `+0xDC`, `0x85AC20`, slot `+0xB8`) feeds a local flag that nothing reads; its only lasting effect is the
cache rebuild, which the clone gives back.

### 8.3 The clone

One call-site retarget, `0x85FFC2` -> `EpStub` (`epro.cpp`). On the sim (owner) thread, while a job runs, outside a `+0x918`
hold, a job callback and a gate frame (`AvMayRun`), `EpRun` answers one node's call under S2's protocol exactly as
`[PlaceTestRO]` does (QL shared, try only; tripwire state, QL sequence and job generation before and after): the lot lookups
and `0x85AE60` first (the game runs them outside its lock too), then `SrEntry` / `SrCell` / `SrXform` / `SrWalk2` (the game's
segment walk with our visitor: same filters, slot whitelist, no refcount, no pin), then S2's `SrConsume`, which gained the
goal point as a parameter (b = the goal through the same lot entry; with start == goal it is the code S2 ran before). The ids
are appended in the game's order. No lot under either point is answered before any routing read, as the game does.

Given back to the game's EditPath (today's wait): QL busy, dirty lot index, the consumer's lot not in the table (the game
would insert it), deferred flush, pin moved, invalid polygon cache, unknown footprint class, buffer limits, a tripwire, job
end, and any call with an exclude vector (not this form). The worker's own calls (a job's plan tail) pass through.

The answer is the game's own for every Sim; there is no scope gate and nothing can differ from the game's call on the same
data. What can differ in time is what differs today between a call made before and after the job's writers W1-W6: the clone
reads between them under QL, the game's call reads after the job.

### 8.4 Switches, counters, files

- `ts3native.ini` `[EditPathRO] Enabled=0` (install), `Mode=1` (start of the runtime switch: export `ts3native_editpathro`
  `+0x00`, `native_set editpathro`), `Shadow=0` (1: with no job running, the game's call then the clone, appended ids
  compared; `edit path RO: SHADOW MISMATCH ...`, 16 lines). Needs `[RouteGate] Enforce=1`, `[RouteAsync]`, the S2 writer set.
- One line a minute: `edit path RO: mode M shadow S calls N, with a job J: answered A (ids I, no lot L; T ms, max U us), the
  game's call G ms (max V us); given back: state, QL busy, lot, shape, polygon cache, tripwire, job end, may not; other form
  O; shadow checks C mismatch X`. New, log only: `steer RO: ... given back: state, QL busy, dirty lot index, lot not in table,
  deferred flush, polygon cache, shape, tripwire; job waits in given-back calls ... ms` (S2's counters, never logged before).
- Files: `native/ts3native/epro.cpp`, `epro-test.h`; `steerro.cpp` (`SrConsume` goal parameter, `SrLogCounters`); section EP
  in `steerro-test.cpp`; `ts3addr-ea169.h` (`0x85FFC2`, `0x85F5A0` unresolved: the EA 1.69 build logs SKIP; this branch has
  no `ts3addr-retail167.h`, add the same two rows where it exists); `tools/ini-sections.json`, `tools/ea-ini.ts`,
  `tools/harness/native-switches.ts`; repro `ip-editpathro-storm`.

### 8.5 Checks run (2026-11-20, no game)

- `steerro-test` section EP (clang-cl build under Wine, `SRT_ONLY=EP`, the Steam 1.67 code mapped at its addresses): the
  game's `0x85F5A0` in the route form (its whole path: `0x82F0B0`, `0x631E20`, `0x667400`, `0x663B20`, the collector,
  `0x62EF30`, the footprints' segment test) against `EpRun` on random worlds and segments (inside a lot, across lots, into the
  lot that is not in the table, off every lot, start == goal, differing levels, ids already present): **60,000 calls, 57,754
  compared equal** (6,533 appended ids, 9,118 ids, 21,420 footprints gathered; no-lot branch 8,176, start-lot swap 5,530),
  2,246 given back (lot not in the table), **0 differ**, references balanced; 12 call edges, the visitor load and the far-end
  helper match the image. The whole `steerro-test` (section A for the changed `SrConsume` included) ends `steerro-test ok`.
  Its fake footprints always have a valid polygon cache and its trees are shallow.
- Selftest case `TestEditPathRO` (stub on fakes: pass-through rules, the no-lot and empty-gather answers, every fallback,
  shadow compare and skew), with `TestPlaceCore`, `TestPlaceTestRO`, `TestGoalValidRO`, built with clang-cl and run alone
  under Wine. Pass. The full selftest and the asi need the MSVC CI build.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: 0 errors, no warning in
  the new or changed lines.
- Harness note: the repack exe (`SizeOfImage` 0x100C000, an extra `.addImp` section) and this Proton's Wine do not fit
  `steerro-test`'s reservation as built by `build.cmd` (the range at 0x400000 is taken at process start, err 487). The run
  above used an uncommitted build: `IMG_HI = 0x0140C000`, the test exe linked at `/BASE:0x400000` with a 0x100B000-byte array
  in `.text` from an object file linked first (so the exe's own image reserves the game's range), `VirtualProtect` in place of
  the commit, `SRT_CHILD=1`.

## 9. The other waiters: read, not built

### 9.1 `0x75A64C`, GoalPoint_Validate with footprints [verified decompile; s148-on counters] (built since: §11)

s148-on `goal validation RO:` over 4 min: 10,159 calls, 577 with a job: 350 answered, **all of them "no lot"**, 223 given back
as "footprints found", 4 QL busy. So in this storm every validated point that lies on a lot has a footprint within the query
radius (`[0x106E33C]`), and the empty-answer design never fires on a lot. The 223 calls are the 1,265 ms.

The hit loop (`0x75A672..0x75A92E`, inline in `0x759B20`) is not a pure filter: per footprint it reads the id (slot `+0x84`),
skips the Sim's own and the option lists (`0x81C970`), then by type: 1 (a Sim) looks the object up by id (`0x939560`, a
reference), tests slots `+0xC0`, `+0x5C`, `+8` / `0x620CA0`, a lot lookup, `0xAB88A0` and the footprint's point test `+0xD4`
with a radius, and writes the blocker id (`out+0x20`) and a cost (`*out2 = [0x1010960]`) or returns "invalid"; 9 / 0x14 record
the blocker id; 2, 0xC, 0xD, 0x11, 0x17 return "invalid" or clear the blocker. Three exits, two of them through destructors
of the validator's locals.

An exact answer needs the loop cloned and run inside the QL hold (the footprints are valid only there; `GvRun` already takes
such a visitor for `[IdleCheckRO] SiteCLoop`) and a way out of the game's inline loop, as `SiteCLoopJump` does for its site.
Open before it can be built: the targets of slots `+0x5C` and `+0xD4` for the nine footprint classes (not in S2's whitelist),
the object lookup `0x939560` beside a job, and the three exits' stack state. Not done here; about 2-4 h with an offline
section like EP. It is the next site by cost.

This is also the one spacing check among the costly takers: the loop asks whether another Sim's footprint covers the goal
point within a fixed radius. Skipping or shrinking it would let two Sims pick the same spot; nothing of that kind is built.

### 9.2 `0x62FB74`, gate 12 `LotTable_GetIfPresent` [caller not resolved]

The key is the gate inside `0x62FB60`, shared by every caller (footprint level rebuild F-A, portal writer ICs F-D, the F-F
readers, docs/195). `[IdleCheckRO]` (90 answers in s148-on, 0 given back) and `[FootstepRO]` cover two readers.
The 16 waits left (1.2 s) have `World_ArePointsConnectedByLand` as the last script call in 6 of 6 stall-watch lines, but the
stall watch prints raw stack words, not a call chain, and `[ProbeP1b]` (the second-frame key for this gate) was off. Run one
storm with `[ProbeP1b] Enabled=1` to get the caller; no clone can be designed before that.

### 9.3 `0xD882D7`, `0xD866E3`

Both are return addresses inside Mono icall trampolines (`call [esp+0x14]`, 4 arguments): the IC's own body calls
`Lock_Acquire` directly or through a tail jump, so the key names the trampoline, not the IC. Script stack in 2 of 2 lines:
`World_GetConnectedRoomsImpl`. 1.1 s in three storms. The IC's body is not read yet.

### 9.4 The rest

`0x85FE10` and the `0x631814` / `0x631F40` / `0x83156D` / `0x846D21` rows are given-back calls of existing clones; the new
`steer RO:` line and the existing counter lines of the others say why per storm. `0x760BFF` and `0x833697` are writers (a sync
plan's goal step; an object footprint update) and stay waits.

## 10. Next storm, on arm

```
[PlaceTestRO]
Enabled=1
Mode=1
Shadow=0
[EditPathRO]
Enabled=1
Mode=1
Shadow=1
[RouteAsync]
KindsUnseen=7
LogFallbackMs=40
[ProbeP1b]
Enabled=1
```

Read: `edit path RO:` (answered > 0, mismatch 0, which give-back reason leads), `steer RO:`, `route lock wait:` lines with key
`0085F6CB` (expected near 0) and where their time went (§7.1: read the `hitch totals` wait sum, not the key alone), the P1b
second-frame keys for `0x62FB74` (`node tools/harness/p1b-report.ts`). A cautious first pass runs `[EditPathRO] Mode=0
Shadow=1`. `[ProbeP1b]` is observe only; leave it out of a timing comparison.

## 11. `[GoalValidRO] Hits`: the validator's hit loop on the clone (ts3native, default off, NOT RUN)

2026-11-20, branch `feat/lock918-readers3`. Input: s151 (off / on = `[PlaceTestRO]` + `[EditPathRO] Shadow=1` + `KindsUnseen=7`).
`[EditPathRO]` held in the game: 23,543 job-time calls, all answered, 97,950 shadow checks, 0 mismatches, key `0x85F6CB` 12 lines
-> 0; sim waits on `+0x918` (>= 1 ms) 10,657 ms off -> 5,585 ms on, `lock918` hitch frames 20 -> 9. The largest key left is
`0x75A64C`: 23 lines, 1,841 ms, one 314 ms hitch; `goal validation RO:` shows 14,301 calls, 148 answered (all "no lot"), **381
given back as "footprints found"**.

### 11.1 What §9.1 left open, read [verified disassembly + decompile + vtable scan]

- **The loop runs without the lock.** `0x82EDB0` takes and releases `+0x918` itself; the loop `0x75A672..0x75A92E` then walks its
  vector with one plain reference per footprint. So every call the loop makes (footprint slots, the object lookup, the lot
  lookup) is one the game already makes on the sim thread with no routing lock held. The only thing the lock gave the loop is
  the vector; the clone has the same footprints inside the QL hold, without the references.
- **The loop** (frame = the validator's esp; arguments off ebp), per footprint, `obj` = vector element - 4:
  id = `obj` slot `+0x84`; skipped when it equals the caller's id (`[ebp+0x18]+0x20`), the validator's own (`[esp+0x80]+0x50`)
  or is in the options' id list (`0x81C970` on `[ebp+0x14]`). Type = interface slot `+8`, switch bytes `0x75AF5C`, targets
  `0x75AF4C`:

  | type | what the loop does |
  |---|---|
  | 1 (a Sim) | id 0, or not in the object service (`0x939560` on `[esp+0x9C]`): next. Slot `+0xC0` true: class = slot `+8` (the validator's record `esp+0xC0`, 0); class 0 / 1 / 0xD (`0x620CA0`): next; `World_LotLookup([esp+0x58], x, z)`, none: next; the point through `0xAB88A0` (the lot object's own transform), then slot `+0xD4` (level 0, u, v, `[0xFEC6DC]` = 0.123): **covered**: write the id to `out+0x20` when that is 0, then LEAVE; not covered: `*cost = [0x1010960]`, next. Slot `+0xC0` false: slot `+0x5C` false or class 0 / 1 / 0xD: `*cost`, next; else LEAVE |
  | 9, 0x14 | id != 0, `out+0x20` == 0 and the object exists: `out+0x20` = id. Then as the next row |
  | 2, 0xC, 0xD, 0x11, 0x17 | LEAVE unless `[esp+0x13]` != 0, `[esp+0x17]` == 0, slot `+0x5C` true and `obj+0x18 & obj+0x1C` != -1; then `out+0x20` = 0, next |
  | other | next |

  LEAVE = `0x75AABB`: drop the footprint reference (`0x55CFA0`), release the vector (`0x652470`), destroy the record
  (`0x643420`), `mov al, 1`, epilogue: the point is rejected. The loop's end joins the empty answer at `0x75A939` (esi and ebx
  are reloaded only because the loop used them; edi is dead there).
- **Slot targets**, all 9 footprint classes and the child class `0xFEBF90` (scan of the image for vtables with `+0x60` =
  `0x9C1B00` / `0x806F00`): `+0x5C` is `0x620D60` (`xor al, al`) everywhere but the child (`0x7EAEA0`, `mov al, 1`). `+0xD4` is
  `0x80F030` wherever `+0xC0` is true: level-0 polygon cache `[obj+0x38]`, `0x67B130` -> `0x67AE90` -> `0x676F70`, stack only once
  the cache is valid; its `this` (`0x1165D28`) is not read or written. The cache rebuild `0x80E5B0` is the given-back case, as in
  `[ObstructedRO]`, which calls the same slot. The class functions with a record that carries an options object read the
  footprint, the record and the options (`0x8294E0`, `0x81C880`, `0x81C970`, `0x946E90`): `[PlacementRO]`'s set.
- **The object's smart pointer** (`0x565970` lock +1, `0x7460F0` lock -1 and the destructor at 0) nets to nothing with a count
  >= 1; the clone skips it and gives the call back when the count is below 1.
- **The three exits are one**: `0x75AAA1` (blocker id), `0x75AAB2` (smart pointer) and `0x75AABB` fall into each other.

### 11.2 The clone

`gvhits.cpp`, wired into `GvStub` (`gvro.cpp`). With `Hits` or `HitsShadow` set, the call site `0x75A647` goes to `GvThunk` (`mov
edx, ebp; jmp GvStub`: edx is free at a thiscall site, ecx and the stack stay the stub's). `GvhFrame` finds the validator's frame
from the stub's return slot and checks it: return address `0x75A64C`, `(ebp & ~15) == esp + 0x100` (the prologue is `and esp,
-16; sub esp, 0xF4` and three pushes), the vector at `esp+0x84`, the point argument, the record at `esp+0xC0` (option word,
pointer 0, byte 1, the options object). On the sim (owner) thread, while a job runs, under `[GoalValidRO]`'s own rules,
`GvRun` runs S2's point query under QL shared and hands the sorted list to `GvhVisit` inside the hold: the loop above through
the game's own slot functions, in the game's order (its vector is sorted by the same pointer), **recording** the writes
instead of making them. When every check of the protocol has passed, `GvhFinish` writes the blocker id and the cost and the
stub returns al 1 with the vector still empty: the game continues at `0x75A939` exactly as after an empty answer. For LEAVE
the stub's return address becomes `0x75AAC4`, the exit's second step (the first would drop a footprint reference this path
never took); the vector's release finds it empty, the record's pointer is 0.

Given back to the game's `0x82EDB0` (today's wait), nothing written: the frame is not the one read; the loop's writes could be
read back through another pointer (`out+0x20` or the cost over the two ids, the point, the record, the options, the frame); the
loop's lot lookup would rebuild the lot index; a slot `+0x5C` / `+0xD4` target outside the list; an object count below 1; an
invalid polygon cache, or one that changed during the test; a full clone list; and every `[GoalValidRO]` doubt (QL busy, lot
table, deferred flush, tripwire, job end). The code is hashed at install (FNV-1a over `0x75A64C..0x75A950`, `0x75AAA1..0x75AACB`,
`0x75AAD0..0x75AAE6`, the tables `0x75AF4C..0x75AF73`, and `sub esp, 0xF4` at `0x759B26`): another hook inside = `SKIP goal hits
RO`, the plain stub is installed.

Exact, for every Sim: the same footprints, slot functions, order and writes as the game's loop on the same data; the spacing
rule (radius `[0xFEC6DC]`, the type table) is the game's. What differs in time is what differs for every clone here: it reads
between the job's writers under QL, the game's call reads after the job.

`HitsShadow=1` (no job running): the game's `0x82EDB0`, then the loop on the clone as a prediction (left / ended, blocker id,
cost); two observer call sites compare it with what the game's own loop did: `0x75A950` (`FootprintRefVec_ReleaseRange`, the
loop's end) and `0x75AACB` (the vector's release on LEAVE). Every path out of the loop passes one of them before anything is
released.

### 11.3 Switches, counters, files

- `ts3native.ini` `[GoalValidRO] Hits=0` (1: installed and answering; needs `Enabled=1`, `Mode=1`), `HitsShadow=0`. Export
  `ts3native_goalhitsro`, `+0x00` mode = the runtime switch (`native_set goalhitsro`; only when installed).
- One line a minute: `goal hits RO: mode M shadow S, loops run N: answered A (rejected L, blocker id B, cost C; footprints F, max
  G; T ms, max U us); given back: frame, alias, slot, object count, polygon cache, list full, lot index, other; shadow checks C
  (rejected R) mismatch X, unseen Y`. `goal validation RO:`'s last fallback number (footprints found) now counts only the calls
  the loop gave back or did not run on.
- Files: `native/ts3native/gvhits.cpp`, `gvhits-test.h`, `gvro.cpp` (thunk, stub, shadow, install); section GVH in
  `steerro-test.cpp`; `ts3addr-ea169.h` (7 rows, unresolved: the EA 1.69 build logs SKIP for Hits and keeps `[GoalValidRO]`;
  `tools/ea-ini.ts` keys them to `GoalValidRO.Hits` / `.HitsShadow`), `tools/ini-sections.json`,
  `tools/harness/native-switches.ts`; repro `ip-goalhitsro-storm`.

### 11.4 Checks run (2026-11-20, no game)

- `steerro-test` section GVH (clang-cl build under Wine, `SRT_ONLY=GVH`, the Steam 1.67 code mapped at its addresses, harness
  as §8.5): the game's own validator code from `0x75A5B7` (the record, the call, the loop, both ways out; a landing pad on
  `0x75A996`) on a frame built by the test, run twice per case on the same state: the call site as the game has it, and the
  site on `GvThunk` with a job "running". Fake worlds as PT / EP plus Sim footprints (type 1), an object service table, lot
  transforms, option lists, both frame flags, a preset blocker id. **60,000 runs: 20,809 answered by the loop clone, all equal**
  (how the validator left, blocker id, cost bits: 10,812 loop ends, 9,997 rejections; blocker id written 1,502, cost 2,902;
  54,313 footprints, up to 15 per loop), 39,191 empty / no lot (`[GoalValidRO]`'s own answer), 0 given back, **0 differ**;
  references and object counts balanced. HitsShadow on every fourth case: 5,115 predictions checked by the observers, 0
  mismatches. The code hash, 10 call edges, the switch tables and the classes' two slots match the image. Two deliberate
  errors in the clone (type 0x14 dropped; the list walked backwards) each stop the section within 200 cases. The whole
  `steerro-test` ends `steerro-test ok` (PT 4000 / 4000, EP 3839 of 4000, 0 differ).
  Not covered there: an object count below 1 and an invalid cache (the game's side would run a destructor / the rebuild on
  fakes); two worlds in three use a wider query radius than the game's 0.1 to get longer loops.
- Selftest case `TestGoalHitsRO` (stub on fakes: every branch, the three id skips, order and first LEAVE, writes only after a
  whole loop, the moved return address, each doubt, six frame checks, the runtime switch, prediction and observer), with
  `TestGoalValidRO`, `TestGvAttr*`, `TestIdleCheckC`, `TestEditPathRO`, `TestPlaceTestRO`, built with clang-cl and run alone
  under Wine. Pass. The full selftest and the asi need the MSVC CI build.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: 0 errors, no warning in
  the new or changed lines. `node tools/selftest-all.ts --ci` passes.

Unverified: everything in the game. In particular the frame check against a live validator (the offline frame is built from
the same disassembly), other mods' hooks inside the hashed ranges, and the share of the 381 calls the doubts give back.

## 12. The other keys of s151-on: read, not built

### 12.1 `0xD882D7`, `0xD866E3`: the two coarse IC gates [verified: stack words, trampolines, decompile]

The stall-watch stacks show nothing of the game between `Lock_Acquire` and the trampoline's return (`Lock_Acquire+0x45`, a
ts3native frame, `TS3W+0x9882D7`): the taker is **our own coarse gate on the IC's entry** (`kRgSites` 11 and 15, docs/18, docs/67
§13), not a lock call in the IC. `0xD882D7` returns into the 4-argument trampoline (`call [esp+0x14]`, int result):
`IC_World_GetConnectedRoomsImpl 0x7929F0`. `0xD866E3` is the 7-argument one (`call [esp+0x20]`, bool):
`IC_World_FindPlacesOnShoreline 0x78C440`. The "last script call" names beside these lines (FindGoodLocation, PlanFromPoint,
GetOption) are stale; do not use them for this key.

- **`0x7929F0` -> `RoomGraph_ConnectedRooms 0x63DCB0`**: `LotTable_Contains`, `GetOrInsert` (a hit after Contains), `0x63D2F0(entry,
  room)`, then the room's portal pointers (`room+4..+8`) and each portal's `+0x48`, `+0x4A`, `+0x58` into a sorted set, copied to
  the caller's vector. `0x63D2F0` builds the lot when `entry+0x8C` is 0 (`0x63CB90`, W4) and fills the room record when its
  first byte is 0 (room-to-room A* `0x6339F0` on the shared Q, `0x61E320` resets; the byte is set last). **A reader once the lot
  is built and the room's record is filled, a writer and a search on a room's first use.** The read set is `[IdleCheckRO]`'s
  (room portal vectors, portal `+0x48` / `+0x58`; I-178-8-2).
  Not cheap, so not built: the gate is at the IC's entry and three more gates (12, 0, 2) are nested in the body, so the
  answer needs a clone of `0x63DCB0` (the pre-checks, the set in the game's order, the caller's vector through the game's
  allocator) at its one call site **and** the entry gate skipped when the clone will answer, under S2's whole protocol (a
  tripwire writer of the room vectors must be able to make it drop the answer; the game's own body cannot be re-run). About
  1.5-2.5 h with an offline section. 12 lines / 599 ms in s151-on, 1.0 s over s146-s148.
- **`0x78C440`**: reaches `LotSearch_Entry 0x640E00` and the in-room A* on the planner's one A* object (docs/67 §13, crash K). A
  search: it needs the planner alone. Keeps the lock.

### 12.2 Gate 0 (`0x62EF7A`) and gate 12 (`0x62FB74`, `0x631814`, `0x62F782`) [verified docs/86, docs/90, docs/152; call paths]

A gate frame holds `+0x918` only for the gate's own call (`LotTable_Contains`, `GetOrInsert`); what the caller does with the
entry afterwards already runs unlocked. So the gate's wait is a guard only by accident: it makes the caller's following reads
start after the job that ran at entry. Letting the gate's read pass under QL shared is safe for the read itself (lot table: W5
under QL exclusive; a miss inserts and is given back), but it moves the caller's whole follow-up beside the running job,
every time instead of only when a queued job starts in the gap. That is provable per caller, not per gate.

**The one small path that exists is design SH** (`slotro.cpp`, `[SlotEnabledRO]`): the game's own body under a QL shared hold;
inside, `RgEnter` passes gate 12 and gate-0 hits, and any other gate, an insert, a lock, host or managed call drops the hold
and waits as today. It fits a caller whose only gate leaves are those two and whose other reads are in S2's read set.
`[Gate0RO]` is the same idea for one call. Callers seen in s146-s151 (script stack beside the wait line; the second frame
needs `[ProbeP1b]`):

| key | caller | class | answer |
|---|---|---|---|
| `0x62EF7A` | `IC_RouteManager_RoutingBlockerCheck2d 0x74E110` -> `RoutingWorld_BlockerCheck2d 0x82E340` (second frames `0x82E696` / `0x82E6C7`; 2 of the 4 named lines) | reader, but not gates only: after the two transforms comes the segment gather `0x631E20` (pin, flush, references) | SH does not fit. `rtoro.cpp` already clones this check as a step of `RouteToObjectCheck`; an IC-level stub on the call to `0x82E340` can reuse it. About 1-2 h, offline section as EP. The next build by cost: 8 lines / 624 ms and the 430 ms hitch in s151-on |
| `0x62EF7A` | `RouteManager_AddPrivateFootprint` (`0x831BA0`; 2 of the 4 named lines) | **writer**: its insert takes the lock at `0x831BF4` right after | keeps the lock (§12.3) |
| `0x62EF7A` | `RouteFollow_A / _B`, EditPath (G-a) | reader | `[Gate0RO]`, and `[EditPathRO]` / S2 / `[AvoidRO]` before it |
| `0x62FB74` | `World_ArePointsConnectedByLand 0x787240` -> `0x65DF90` -> `0x65BB90` -> `0x6595B0` -> `0x654790`, call `0x654BE0` [resolved statically: `CallPaths`, the only path] | reader: per candidate waypoint of a lot, the entry's waypoint map (`+0x124` / `+0x128`), `+0x10`, the outside room's portals; one gate leaf (12) | **SH fits**: a hold around `0x654790` (call `0x659787`). To prove first: its callees write nothing (`0x650030`, `0x654240`, `0xD38B00`, `0xB793F0`) and the hold's length (a loop over waypoints). About 1 h + an offline section. 1 named line of 8; confirm the share with `[ProbeP1b]` before building |
| `0x62FB74` | `Route_PlanFromPoint` (3 lines): a sync plan's first touch | **writer / search** | keeps the lock; `KindsUnseen` moves the plan off the sim |
| `0x62FB74` | `RouteManager_RemovePrivateFootprint` | **writer** | keeps the lock (deferred by `DeferPrivateFp` when on) |
| `0x62F782` | `RouteManager_SetPortalCost` -> `PortalsForObject` | **writer** (portal cost) | keeps the lock |
| `0x62F782` | slot position helper `0x76F0A0` | reader | SH (`[SlotEnabledRO]`) |
| `0x631814` | `IC_World_GetLotFrontDoorImpl` | reader | `[LotFrontDoorRO]`; what waits is its given-back calls |
| `0x760BFF` | gate 10, a sync plan's goal step | **writer / search** | keeps the lock |
| `0x833697`, `0x831BF4` | object footprint update, private footprint add | **writers** | keep the lock |

Also keeping the real lock whatever the caller: a gate-0 miss (`LotTable_Insert`), the lazy lot build and room fill (gates 2,
3), lot rebuilds (4, 13, 14), the Q resets and stamps (6-9).

### 12.3 `job wait: own job @disc` [verified source + s151-on log]

It is `DiscDrain(true)` -> `WaitAsyncDone`: the sim thread waits for the running route job because a queued (deferred) footprint
record of an object must be applied before the next operation on that object. 38 of the 40 such waits in s151-on (2,038 ms,
max 218 ms; the 390 ms frame is several of them) come from `Fp2FlushFor` at the private-footprint call sites: the add
(`RouteManager_AddPrivateFootprint`) is not deferred, so the object's queued remove is flushed first.
It is a writer's wait (the add would wait at `0x831BF4` for the same job), so no reader path applies. What would shorten it is
deferring the add like the remove (`PfAddSite`'s own note: `0x831BA0` is not audited: its reads of the caller's points, its
return value); not provable without that audit, not built.

## 13. Next storm, on arm

```
[PlaceTestRO]
Enabled=1
Mode=1
Shadow=0
[EditPathRO]
Enabled=1
Mode=1
Shadow=0
[GoalValidRO]
Hits=1
HitsShadow=1
[RouteAsync]
KindsUnseen=7
LogFallbackMs=40
```

A cautious first pass: `Hits=0 HitsShadow=1` (prediction only; the thunk and the observers are installed, no answer changes).
Read: the `goal hits RO:` line (shadow checks > 0, mismatch 0, unseen 0; answered > 0; which give-back column leads; `frame` and
`alias` must be 0), `goal validation RO:`'s last fallback number (381 in s151-on, expected near the give-backs), `route lock
wait:` lines with key `0075A64C` (23 in s151-on) and the `hitch totals` wait sum (§7.1). A `SKIP goal hits RO` line means the
code hash failed (another hook in the validator): report it. `[ProbeP1b] Enabled=1` in a separate observe-only run gives the
second frames for `0x62EF7A` and `0x62FB74` (§12.2) before either of those is built.

## 14. s160-s166 read; the `0x85F6CB` / `0x85FE10` residue (`[EditPathRO] StartGuard`, `Retry`; ts3native, default off, NOT RUN)

2026-11-20, branch `feat/lock918-readers4`. Input: the seven on arms s160-s166 (4 min Isla Paradiso route storms, every switch of
§13 on with `[SteerRO] Retry=0`, `[Discourage] DeferPrivateFp=0`), `ts3native-full.log` each; Ghidra headless (`DecompileTo`,
`ICSafety`, `CallPaths`), objdump on the Steam 1.67 code.

### 14.1 What the sim thread still waits for [measured, seven arms summed]

`route lock wait:` lines (>= 20 ms), and the `route jobs: sim waited ... for a job at ...` lines (>= 20 ms):

| what | ms | n | max | per arm (s160..s166) | class | answer |
|---|---|---|---|---|---|---|
| job wait `disc`, drain point `fp2 reader` | 15,655 | 235 | 192 | - | **writer**: the private-footprint ICs flush the object's queued records first (§16) | `DeferPrivateFp=1` + **`DeferPrivateFpAdd` (§16)** |
| key `0x760BFF` gate 10, a sync plan's goal step | 8,760 | 131 | 241 | 1373 1315 1503 1160 1295 1307 807 | writer / search | none (the plan itself) |
| job wait `rtwait` (`RtWaitJob`: destroy, drains, the lost-removal flush) | 5,788 | 104 | 211 | - | writers | not split by site in the log (§17) |
| key `0x62FB74` gate 12 `LotTable_GetIfPresent` | 4,934 | 75 | 285 | 410 762 450 846 826 1322 318 | mixed | callers not known (§17) |
| key `0xD882D7` coarse gate, `IC_World_GetConnectedRoomsImpl` | 3,677 | 55 | 202 | 401 190 476 784 375 885 566 | reader after a room's first use | **`[ConnRoomsRO]` (§15)** |
| key `0x85F6CB` EditPath gather | 3,249 | 55 | 248 | 707 358 470 534 450 481 249 | reader | **`StartGuard`, `Retry` (§14.2)** |
| key `0x62EF7A` gate 0 | 2,131 | 41 | 124 | 583 238 222 348 183 250 307 | mixed | private-footprint ICs named (§16); the rest unknown (§17) |
| key `0x85FE10` steering query | 2,035 | 30 | 205 | 238 327 87 398 412 307 266 | reader | S2's give-backs: `[SteerRO] Retry=1` (§14.3) |
| key `0x62F782` gate 12 `PortalsForObject` | 1,581 | 26 | 166 | 94 33 268 244 339 194 409 | writer (`SetPortalCost`) / reader | `[SlotEnabledRO]` for the reader |
| job wait `runplan` | 1,206 | 13 | 180 | - | the sim's own plan | none |
| job wait `disc` at READY / START | 1,125 | 17 | 112 | - | writer (deferred records older than 250 ms) | none |
| key `0x833697` object footprint update | 914 | 16 | 125 | - | writer | none |
| keys `0x75AA16`, `0x631814`, `0x83156D`, `0x75A64C`, `0x831BF4`, `0x631F40`, `0x62FB8B`, `0x85B50C`, `0x846D21` | 2,984 | 45 | 205 | - | readers' give-backs, two writers | - |

`hitch totals` per arm: `+0x918` waits >= 1 ms 3,701-5,082 ms (n 103-140), all behind the route worker; `lock918` hitch
frames 5-10, `job-wait` frames 1-11. **The largest single cost is not a lock key: it is the `fp2 reader` flush**, 2.2 s per
arm, which the wait-key tables of §7 and §11 do not show because it is booked as a job wait.

The 394 ms `World_ArePointsConnectedByLand` frame of s161 is **not** a lock frame: `cause sim-cpu ... lock918 0`, the samples
sit in `ClosestWaypoints_Find` (gather, sort) and `Query_CountObjectsOnLot`. No `+0x918` clone applies to it.

### 14.2 `0x85F6CB`: which call still waits [counters measured; the cause inferred]

`RouteFollow_EditPath 0x85F5A0` has two callers (`0x85FED7` in `Locomotion_ObstacleCheck`, `0x85FFC2` in the route form)
[verified: call scan of the image], so every `0x85F6CB` wait is behind `LocoHook` or `EpStub`. Per arm, `edit path RO:`
books **0, 2, 4, 10, 27, 35, 65 ms** in the game's call while a job ran (the given-back calls: QL busy 0-3, tripwire 0-2,
job end 2-14), and `steer RO:` books 0 ms at the EditPath gather in six arms (143 ms in s161). The lines sum to 249-707 ms
per arm. So most of the wait is in **calls that saw no job at the stub's entry** [inferred: the only path left in the
source]: `EpStub` reads `g_async.state`, sees `A_IDLE` and calls the game; `ServeAsync` takes `+0x918` before it publishes
`A_RUNNING`, so a job that starts between the stub's look and the game's `Lock_Acquire` makes that call wait for the whole
job. The start is correlated with the caller (the sim thread has just queued the job from the same script frame:
`Route_SetAGS` is the script stack of 4 of the 5 named lines), which would explain a microsecond window being hit about
8 times an arm. The same hole follows a job-end give-back: the game's call then races the next queued job (`job end 12`,
longest game call 31.8 ms in s166).

Built (`epro.cpp`):

- **`StartGuard=1`**: with no job at the entry the stub opens a `g_rgOffSimOpen` frame, reads the state again, and makes the
  game's call inside the frame; `ServeAsync` backs off while the count is > 0 (the Dekker pair every gate leaf and the shadow
  path already use). A job that had started in the gap sends the call to the clone path. The answer is the game's own call;
  the job's start moves by one EditPath call. No lock is skipped.
- **`Retry=1`**: a clone answer dropped for QL busy, a tripwire or the job's end is run again from the protocol's start (S2's
  `SrRetry` rule, docs/250 §8: sequence, epoch and generation are read again, so a rerun is as exact as a first run), at most
  4 passes within ~1 ms; QL busy and tripwire yield first; after a job end the loop's top decides (no job: the game's call,
  inside the guard frame when `StartGuard` is on; a new job: the clone).
- Counter line gains `start guard G: the game's call in a frame N, a job had started R; retry T: reruns N, answered after one
  K`. Export `ts3native_editpathro` `+0x5C..+0x70` (guard, retry, guard calls, guard race, reruns, answered after a rerun).

The next storm decides: with `StartGuard=1`, key `0x85F6CB` should fall to the with-a-job figure (`the game's call N ms`).
If it does not, the inference above is wrong and the waiting call must be found with a per-call entry-state counter.

### 14.3 `0x85FE10`: which call still waits [measured counters]

Nearly all of it is S2's given-back calls: `steer RO: ... job waits in given-back calls` is 261 / 212 / 97 / 283 / 336 / 256
/ 273 ms per arm against 238 / 327 / 87 / 398 / 412 / 307 / 266 ms of lines. The reasons per arm: `state` 2-6 (the job
ended before or during the clone, `s2JobEndDrop`; the stub's other state refusals hold the lock or serve a callback and do
not wait), `QL busy` 2-6 (a W1-W6 writer's exclusive hold), `tripwire` 2-5; no lot, cache or shape give-back at all.
`[SteerRO] Retry=1` (docs/250 §8, built, off in these arms) reruns the QL-busy and tripwire calls; the job-end ones call
the game with no job live, and `[LocoWindow] Mode=1` keeps a new job from starting during the locomotion pass. Nothing was
built for this key: **arm `[SteerRO] Retry=1`**. In four arms the lines exceed the given-back figure by 51-115 ms; that
rest is not attributed (a call outside the locomotion pass meeting §14.2's start race would look like this; S2 has no guard
for it).

### 14.4 Checks run (2026-11-20, no game)

- Selftest `TestEditPathRO` (clang-cl, alone under Wine): the guard frame is open during the game's call and closed after;
  switch off = no frame; a job-end drop rerun 3 times then given back once; the next job armed during the first pass = the
  second pass answers; the job gone = the game's call in the frame, no wait booked; QL busy with Retry counted once. Pass.
  The "job had started in the gap" branch needs a second thread and is not exercised.
- `steerro-test` section EP unchanged (`EpRun` is not touched): 3,839 of 4,000 equal, 0 differ, in the whole run.

## 15. `[ConnRoomsRO]`: `IC_World_GetConnectedRoomsImpl` on a clone (ts3native, default off, NOT RUN)

Key `0xD882D7` (§12.1): 55 lines / 3,677 ms in the seven arms, the largest reader key left. `World_GetConnectedRoomsImpl` is
the script stack of 5 of its 10 named lines.

### 15.1 The call [verified disassembly `0x7929F0..0x792AAF`, `0x63DCB0..0x63DF57`, `0x6286F0`, `0x63D2F0..0x63D364`]

- The IC (cdecl: lot id lo, hi, room, level) reads the script host `[0x11D4BBC]`, the routing world and the lot planner,
  calls `RoomGraph_ConnectedRooms 0x63DCB0` at `0x792A55` (thiscall P: lo, hi, room, level, vector*; `ret 0x14`) into an
  empty local vector tagged "Scripting/WorldHost", and on al != 0 asks the host for an array (slot `+0x8C`) and copies the
  vector into it (slot `+0xA4`). The frame up to the call is `sub esp, 0x14; push ebx; push edi` and five pushes: the call's
  return slot is the IC's return slot - 0x34.
- `0x63DCB0`: `LotTable_Contains` (gate 12), `GetOrInsert` (gate 0, a hit). Room index as 16-bit words: room > 0 (signed
  word) `room + 0x10`; room == 0 `level + 4`; room < 0 `0xFFFF`. Index >= the entry's room count (`+0x90` / `+0x94`, 0x44
  bytes each, unsigned compare): al 0. `RoomGraph_FirstUse 0x63D2F0` (gate 2): builds the lot when `entry+0x8C` is 0,
  fills the room record when its first byte is 0, **else returns at once** (`0x63D300`, `0x63D359`). Then each portal
  pointer of the room (`+4 .. +8`): skipped with bit 1 of `portal+0x58`; the words `+0x48` and `+0x4A` that are neither
  `0xFFFF` nor the index go, as `w <= 0x10 ? 0 : w - 0x10`, into a set ordered by **signed** 16-bit compare (`setl` at
  `0x628716`). The set is appended to the vector sign-extended (`movsx` at `0x63DEE0`); a vector that is too small gets one
  block of `4 * count` bytes from `0x4E3F90` with the vector's tag. al = the vector is not empty. A null portal pointer is
  read at `+0x48` (the null test at `0x63DDA8` only skips the bit test).

### 15.2 The clone (`connro.cpp`)

At the entry gate (`RgEnter`, site 11), for an outermost entry of the sim (owner) thread while a job runs and `AvMayRun`
holds, **before the gate publishes a frame or waits**: `CnGate` reads the answer under QL shared (try only) with S2's protocol
through `[IdleCheckRO]`'s `IkEnter` / `IkMoved` / `IkLeave` (sequence, tripwire epoch, generation before and after): the lot
entry by the Contains rules (`SrEntry`, no insert), the room index, `entry+0x8C`, the room's first byte, the portal pointers
and the three portal fields. This is `[IdleCheckRO]`'s read set (the room vectors and portal `+0x48` / `+0x58` its leaf
`0x625140` reads under the same hold, I-178-8-2) plus the neighbouring word `+0x4A`.

An answer is parked for this one IC call, keyed by the IC's return slot, the planner and the four arguments. The gate then
returns with **no frame and no lock**: the only part of the IC that touches routing state is the call at `0x792A55`, and the
stub there (`CnStub`) hands the parked answer out instead of calling `0x63DCB0`: one block from the game's allocator with
the vector's tag, the values in the set's order, al as the game's. The IC's own tail (the managed array) runs as after the
game's call. A parked answer that the stub cannot match (another slot, planner or argument, a vector that is not empty) is
dropped and the game's `0x63DCB0` runs, each nested gate on its own as for any unlocked entry (`not used` in the line).

Given back to the gate (today's wait, the game's whole body under `+0x918`), nothing parked: QL busy, a tripwire, the job's
end, a lot that is not built or a room that is not filled (`0x63D2F0`'s two writes: a lot build and a room-to-room search),
a null portal pointer, more than 128 rooms, no planner, a job callback.

Exact for every Sim: the same vectors, fields, set order and output as the game's code on the same data. What differs in
time is what differs for every clone here (it reads between the job's writers under QL; the game's call reads after the job).

Shadow (`Shadow=1`, no job; the gate's own frame holds job starts off): the game's `0x63DCB0`, then the clone, the vector and
al compared; `SHADOW MISMATCH` lines (16). The game's call has built and filled what it needed, so the shadow checks the
read, not the two doubts.

### 15.3 Switches, counters, files

- `ts3native.ini` `[ConnRoomsRO] Enabled=0` (install), `Mode=1` (start of the runtime switch: export `ts3native_connroomsro`
  `+0x00`, `native_set connroomsro`), `Shadow=0`. Needs `[RouteGate] Enforce=1` (gate stub 11), `[RouteAsync]`, the S2
  writer set. Install checks the IC's frame bytes (`0x7929F6..`, `0x792A26..0x792A4C`) and the call.
- One line a minute: `connected rooms RO: mode M shadow S, IC entries with a job J: answered A (no rooms E, rooms R; T ms,
  max U us), handed out H, not used X; given back: state, QL busy, tripwire, job end, lot not built, room not filled,
  shape, may not; shadow checks C mismatch X`.
- Files: `native/ts3native/connro.cpp`, `connro-test.h`; `routegate.cpp` (one line in `RgEnter`); section CN in
  `steerro-test.cpp`; `ts3addr-ea169.h` (`0x792A55`, `0x63DCB0` unresolved: the EA 1.69 build logs SKIP);
  `tools/ini-sections.json`, `tools/ea-ini.ts`, `tools/harness/native-switches.ts`; repro `ip-connroomsro-storm`.

### 15.4 Checks run (2026-11-20, no game)

- `steerro-test` section CN (clang-cl build under Wine, `SRT_ONLY=CN`, harness as §8.5): **the game's IC `0x7929F0` run
  from its entry** with a fake script host that records the array it is asked for, twice per case on the same world: with
  no job (the stub passes to the game's `0x63DCB0`; the shadow compare runs on the way), and with a job "running" and
  `CnGate` called on the IC's return slot as `RgEnter` calls it. Random lots (in the table, the active lot, not in the
  table, key 0, all-ones), rooms (0 by level, positive, negative, past the vector, garbage in the high word), up to 70
  rooms with up to 13 portals, portal words on both sides of the signed boundary. **60,000 calls: 57,242 compared equal**
  (array asked of the host, its contents, the IC's result; 12,437 with rooms, 71,697 room numbers, up to 19 a call, 14,778
  negative; 44,805 "no rooms"), 57,242 shadow checks 0 mismatch, **0 differ**; 2,336 doubts given back and not run on the
  game (lot not built 877, room not filled 894, null portal 565: the game's side would build, search or fault on fakes;
  the other 422 doubt cases changed a field the call does not read and were answered); a parked answer is refused by
  another IC frame. 11 call edges, the IC's frame, the set's signed compare, the sign-extended copy and `0x63D2F0`'s two
  tests match the image. Two deliberate errors in the clone (`+0x4A` dropped; the set ordered unsigned) each stop the
  section in its first cases. The whole `steerro-test` ends `steerro-test ok`.
- Selftest `TestConnRoomsRO` (clang-cl, alone under Wine): room index, set order and cap, the read's six outcomes, eight gate
  refusals, the parked answer through the allocator, the empty answer, three stale keys, shadow compare. Pass.

Unverified: everything in the game; in particular how many of the calls meet a lot or room on its first use (the two
give-backs that keep the wait), and the gate's pass with no frame against a live `RgEnter` (offline `CnGate` is called
directly; the one-line hook in `RgEnter` is read, not run).

## 16. `[Discourage] DeferPrivateFpAdd`: the private-footprint add as a record (ts3native, default off, NOT RUN)

### 16.1 The wait and the audit of `0x831BA0`

`route jobs: sim waited N ms for a job at disc (..., drain point fp2 reader)`: 235 waits / 15,655 ms in the seven arms.
It is `Fp2FlushFor` (§12.3) at the two private-footprint call sites: with design U phase 2 on, an object's `FromTool` /
`UpdateObjectLot` calls made during a job are queued; the script's next `RemovePrivateFootprint` or `AddPrivateFootprint`
for that object must run after them, so it drains the queue, which waits for the running job. The script caller is
`DynamicSimFootprint.Enabled` (large animals, boats with a turn-helper footprint: `Route.AddPrivateFootprint(id, vertices,
boat != null)`), and its result is discarded [verified in the decompiled script]. The same ICs are named on keys
`0x62EF7A`, `0x62FB74` and `0x62FB8B` (5 of the 40 named lock lines of the seven arms).

`DeferPrivateFp=1` (docs/89 §14, built, off) queues the remove. The add was "not audited". The audit [Ghidra decompile +
`ICSafety` closure (400 functions, depth 6) + `CallPaths`]:

- `RouteManager_AddPrivateFootprint 0x831BA0`, thiscall world (obj, points vector*, flag byte), `ret 0xC`, al. **Before its
  lock** it returns 0 for a closed world (`+0x974`), no object, fewer than 3 points (16 bytes each).
- `Lock_Acquire(+0x918)` at `0x831BF4`. Under it: no world manager -> 0. The object's lot (`0xB03750`); no lot or the
  active lot, **and flag 0** -> 0; the lot's record (`0xC62D40`); none **and flag 0** -> 0. Every other path ends in
  `return 1`. So with flag != 0 the result is 1 whatever the lot state.
- Then: a polygon from **the caller's points** (x, z copied as they are); when `0x749680(obj)` is not 0, a second polygon
  from the same points through the object's inverse transform (`0x958EE0`, `0x82A610`); the private key `0x7CFA30(2, 0x19,
  id)`; `0x635700` removes an older private footprint of the id; a new footprint object ("Routing/PrivateFootprint", vtable
  `0xFEBBC0`), its fields from the object's id, the lot key and two per-type flags; `0x80F6A0` (W6f); then the pending map
  (`+0x8BC` / `+0x8C4`) or the lot insert `0x6376D0`; `+0x994` = 0; release.
- Reads of caller state: the points vector (freed by the IC right after), nothing else. Reads of object state at the call:
  lot, transform, `0x749680`, type flags.
- No direct call path to `World_FireEvent 0x78F2B0` within 10 calls (none from `RemoveFootprintLocked 0x82A0C0` either,
  which docs/84 §7.1 treats as managed-free). The closure has 227 computed calls (10 in the body: the new footprint's own
  vtable slots); they were not followed. [inferred: managed-free, as the remove and the kind-1 insert records]

### 16.2 The record (`discourage.cpp`)

`PfAddSite` (the IC's call at `0x75DE1A`) with `DeferPrivateFpAdd=1`, on the sim thread, while a job runs or when the object
already has a queued record (order rule): the add becomes a record in D's FIFO (`pf` + `pfa`), with **its own copy of the
points** and the flag, answered 1. Pass-through as `PfRemoveDefer` (a `+0x918` hold, a job callback, a drain, another
world, closing). Not deferred, today's flush and call: **flag 0** (the result depends on the lot), fewer than 3 or more
than 64 points, no world manager, a full queue. The record is applied by the game's `0x831BA0` on a vector over the copy,
in FIFO order with every other record, at D's drain points (no job running, or the oldest record 250 ms old at a script IC
point) and before a destroy of the object (D-X). Gone or a reused id at the apply: nothing (vanilla's footprint would have
left with the object, `0x82B551`). A queued add does not count as a queued removal (`RemovalFirst`).

**Not identical to the game.** What differs, all of it:

1. The footprint reaches the routing world at the drain point instead of at the call. Until then sim-thread readers, and a
   route job that starts in that window, plan without it. The script's `AddDiscourageFootprint` of the same
   `DynamicSimFootprint` call is deferred the same way today (`[Discourage] Mode=1`).
2. `0x831BA0` reads the object's lot and transform at the apply. If the object moved in between, the lot key (and the second
   polygon, when the object has one) are the later ones. The counter `lot moved` counts it; the script's own follow-up
   (remove on `Enabled = false`, a new add) queues behind and is applied in the same drain under one lock hold, so no reader
   sees the intermediate footprint.
3. With today's wait the sim thread is blocked until the job ends, so nothing of the above can happen; that wait is the 2.2 s
   per arm this switch is meant to remove.

The result (1) is exact (flag != 0), the points are the call's, the order of routing-world operations per object is the
script's. Because of 1 and 2 it sits behind its own switch, default off; `DeferPrivateFpAdd=2` is a probe: today's flush
and call, then the object's lot is read again at the next drain point (`probe N moved M`), which measures 2 before the
switch is trusted.

- `ts3native.ini` `[Discourage] DeferPrivateFpAdd=0` (1 defer, 2 probe); needs `DeferPrivateFp=1` (which needs
  `DeferFootprint2=1` or `2`). Export `ts3native_pfadd` `+0x00` mode (`native_set pfadd`).
- One line a minute: `private footprint add: mode M calls N (in a job J): deferred D (no job, order rule O), applied A,
  object gone G, lot moved S, oldest T ms; not deferred: pass, flag 0, shape, queue full; flush wait before an add W ms;
  probe P moved Q (longest R ms)`.
- Files: `discourage.cpp` (`PfAddDefer`, `PfaProbe*`, the `pfa` branch of `DiscApplyOne`, `DiscRemovalQueued`),
  `selftest.cpp` (`TestPrivateFpAdd`), `tools/harness/native-switches.ts`; repro `ip-pfadd-storm`.

### 16.3 Checks run (2026-11-20, no game)

- Selftest `TestPrivateFpAdd` (clang-cl, alone under Wine, D's fakes): mode 0 and no job = today's call; a job = a record
  that keeps its own points after the caller's change, answered 1; remove / add / remove of one object applied in call order;
  the order rule with no job; flag 0, 2 points, no world manager and a `+0x918` hold not deferred; object gone, id reused
  and lot moved at the apply; destroy applies the add first; an add is no queued removal; probe mode. Pass. The case found
  one error in the build (a `pfa` record counted as D's own remove in `DiscRemovalQueued`), fixed.
- `TestPrivateFpDefer` and `TestDiscourage` stop with an access violation when run alone in this clang-cl build (exit 5,
  before and after this change's code paths are reached [not traced; clang-cl's `__try` does not catch faults, §8.5's
  limit, is the likely reason]); they need the MSVC CI run, as the full selftest does.
- No offline run of `0x831BA0` itself: its closure needs the object service, the world manager and the lot records.

Unverified: everything in the game; the indirect calls of `0x831BA0` (above); horses (flag 0) keep the flush; how much of
the 15.7 s is the add's flush and how much the remove's (with `DeferPrivateFp=0` both flush; the split needs one arm with
`DeferPrivateFp=1`: `pfAddFlush` and `flush wait before an add`).

## 17. Read, not built

- **`0x62EF7A` under `RoutingBlockerCheck2d` (§12.2).** Does not rank: no line of the s151, s156 and s160-s166 logs names
  `RouteManager_RoutingBlockerCheck2d` (0 hits in nine logs). The three named `0x62EF7A` lines of s160-s166 are
  `RemovePrivateFootprint`, `AddPrivateFootprint` (§16) and `Slots_AttachToSlotImpl`. Not built.
- **`0x62FB74` under `World_ArePointsConnectedByLand` (§12.2).** One named line in s158 (81 ms), none in s160-s166, where the
  8 named lines are `RemovePrivateFootprint` 2 (§16), `PlanFromPoint` 1 (a sync plan), `IsOkToIdleAtCurrentLocation` 1
  (285 ms; an `[IdleCheckRO]` give-back [inferred]), `CountObjectsOnLot` 2 and `GetOption` 1 (names of a later sample, not of the
  taker), `GetConnectedRooms` 1. The IC's own slow frames are CPU (§14.1). Not built. The purity check asked for in §12.2
  was run for the record: `ICSafety` on `0x654790` (92 functions): no global write; callees `0x650030`, `0x654240`,
  `0xD38B00`, `0x62FB60`, `0xB793F0`; locks reached: the allocator's and `World_LotLookup`'s own lock `0x10786C8` through
  `Lock_Acquire 0x4E16F0` (`0x650030 > 0xC68990 > 0xC688A0 > 0xC68680`), which an SH hold must be able to stay open across
  (`QlSharedTrip(SLD_LOCK)` in `LockAcquireHook`: check which locks trip it). **4.9 s of `0x62FB74` has no known caller:
  run `[ProbeP1b] Enabled=1` (observe only) once**; §10 and §13 asked for it and no arm has had it.
- **Job wait `rtwait`** (104 waits / 5.8 s): `RtWaitJob`'s callers (destroy, the lost-removal flush `UolFlushFor`, saves) are
  not named in the line. `lrsN` / `lrsUs` (counters v192) split the lost-removal part; read them in the next arm.
- **`0x85FE10` outside the pass** (§14.3): 51-115 ms per arm in four arms, not attributed.

## 18. Next storm, on arm

On top of §13's arm:

```
[EditPathRO]
StartGuard=1
Retry=1
[SteerRO]
Retry=1
[ConnRoomsRO]
Enabled=1
Mode=1
Shadow=1
[Discourage]
DeferPrivateFp=1
DeferPrivateFpAdd=2
```

First pass as written: `[ConnRoomsRO]` answers and shadow-checks (mismatch must be 0; `not used` 0), the private-footprint
remove is deferred and the add only probed. A more cautious first pass for `[ConnRoomsRO]`: `Mode=0 Shadow=1` (compare
only). Read: `connected rooms RO:` (answered against `lot not built` / `room not filled`), key `0x00D882D7`; `edit path RO:`
(`a job had started`, key `0x0085F6CB`); `steer RO:` and key `0x0085FE10`; `private footprint add:` (`flush wait before an
add`, `probe N moved M`), the `drain point fp2 reader` lines and `pfAddFlush`. If `moved` is 0 or rare: second pass with
`DeferPrivateFpAdd=1` (`applied`, `lot moved`, `object gone`; the `fp2 reader` waits should go; watch boats for a
turn-helper footprint that shows late). Separate observe-only arm: `[ProbeP1b] Enabled=1` for the callers of `0x62FB74`
and `0x62EF7A`. Read the hitch totals' wait sums, not one key (§7.1).

> **Port note (post/roclone-1123, 2026-11-23):** §21-§26 below come from the combo branch with the read-only clone set ([ObstructAtRO], [Blocker2dRO], [LandCheckRO], the §26 shadow fix, [SteerRO] RetryWait). §19, §20 and §24's `ForeignKindsUnseen` part are not in this PR (other PRs / not posted), which is why the numbering has gaps; the two §24 headings are kept as the code comments name them. **None of this has run in the game**, and docs/631 §5 suspects the set of the s229 / s241 third-floor stranding.

## 21. s189 read: every gate wait has its caller (`RouteLockCallers`) [verified: log + disassembly]

2026-11-20, branch `feat/lock918-readers6`. Input: s189 (3-min storm, Isla Paradiso, every fix on, `[HitchAttr] RouteLockMs=20
RouteLockCallers=1`), 63 `route lock wait:` lines, 3,369 ms. No s190 log existed when this was read. Functions found with
objdump on the Steam 1.67 code and `functions.csv`; no Ghidra run.

| ms | lines | max | key | first caller word | who | class |
|---|---|---|---|---|---|---|
| 508 | 13 | 76 | `0x62EF7A` | `0x655ABE` | `RouteObstructionAtPoint 0x655880` (§21.1) | reader: **built, `[ObstructAtRO]`** (§22) |
| 476 | 8 | 152 | `0x62FB74` | `0x654BE5` | `0x654790` under `World_ArePointsConnectedByLand` (§12.2's path) | reader; SH hold, not built (§23.1) |
| 442 | 7 | 109 | `0x85FE10` | `~7805F619` (ts3native) | S2 steering query given back | `steer RO:` line: 381 ms in given-back calls |
| 413 | 5 | 129 | `0x62EF7A` | `0x82E696` | `RoutingWorld_BlockerCheck2d 0x82E340` | reader; IC-level stub on `rtoro.cpp`, not built (§23.2) |
| 247 | 6 | 67 | `0x62FB74` | `0x834201` | `0x8341D0` (§21.2) | **writer path**: keeps the lock |
| 242 | 3 | 104 | `0x62F782` | `0x7581A6` | `0x758110` = `RouteManager_SetPortalCost` (§21.3) | **writer**: keeps the lock |
| 225 | 7 | 58 | `0x760BFF` | `0x76429D` | `0x7641D0`, the goal step of `IC_Route_Plan` / `IC_Route_Replan` (§23.3) | sync plan |
| 222 | 4 | 128 | `0x760BFF` | `0x764400` | `0x7642C0`, the goal step of `IC_Route_PlanFromPoint` / `IC_Route_ReplanFromPosition` (§23.3) | sync plan |
| 187 | 2 | 139 | `0x631814` | `77FF029C` (not image) | `[LotFrontDoorRO]` given back [inferred] | - |
| 114 | 2 | 76 | `0xD882D7` | scan only | `[ConnRoomsRO]` given back | - |
| 109 | 2 | 73 | `0x62FB74` | `77FA1526` (not image: stale) | unknown | - |
| 53 / 51 / 48 / 32 | 1 each | | `0x846D21`, `0x85F6CB`, `0x631F40`, `0x62EF7A` from `0x75167F` | | RouteFollow_A, EditPath given back, FGL footprints, `IsPointObstructedBySim` given back by `[ObstructedRO]` | - |

### 21.1 `0x655ABE`: `RouteObstructionAtPoint 0x655880` [verified disassembly 0x655880..0x655ED1]

thiscall(self; ctx, Vector3 point, bool classFlag), `ret 0x14`, result EDX:EAX. Two callers: `0x7519D9` (an IC body after
`IsPointObstructedBySim`; it builds ctx on its stack from the route: option word `route+0x3C`, own id `route+0x40/+0x44`,
ignore list `route+0x158`; the s170-s187 logs named `Route_GetObstructionAtWorldOrLotPoint` beside this key [inferred: the same
IC]) and `0x6437AF` (in planner function `0x6435B0`, classFlag 1).
Body: `World_LotLookup(self+0xC, x, z)`; no lot: the other path through `0x62D0D0` (not read). With a lot: the level
(`0xADD860`), the height (`0xB015B0`, `0x74A8F0`), **the lot transform `0x62EF30(self+8, lot id, ...)` = gate 0**, whose two
outputs are never read again, then `RoutingWorld_ObstacleQuery 0x82EDB0` (mode 0, radius `[0x106E8A8]`, option word = ctx's)
at `0x655AF6`, which takes `+0x918` itself. Then the loop over the returned footprints: classFlag 0: type (slot `+0xC`) 0x14
or 1, id (slot `+0x84`) not ctx's own and not in the ignore list (`0x81C970`) = the answer; classFlag 1: class (slot `+8`
with ctx) >= 2 and not 0xD = answer 1:0. Nothing found: 0.
**A reader of planner state** (lot table hit, quadtree footprints); the result is a value. It writes only footprint
refcounts (+1/-1) and a lot-table entry on a miss. So the gate wait buys nothing by itself: without it the function would
wait at `0x82EDB0` instead. Both touches must be answered together: §22.

### 21.2 `0x834201`: `0x8341D0` [verified disassembly, 0x831710 not read]

thiscall(RoutingWorld; object, level), called from `0x834C12` (`0x834B80`) and `0x835DED` (`0x835240`), the object
footprint update family (`0x833697` is the writer key next to it). It skips objects with `+0x408 == 1 && +0x40C == 0x50`,
finds the lot entry (`0x62FB60`, gate 12), tests bit `level - entry+0x40` of `entry+0x168`, and when the bit is clear calls
`0x831710(object, level, 0, 1)`. The read is two fields of the entry; what follows it is a routing-world change for that
object and level [inferred from the callers and the argument shape]. **Writer path: keeps the lock.** A read-only answer
would only cover the "bit already set" case and would move the callers' following writes beside the job; not provable per
caller in this pass.

### 21.3 `0x7581A6`: `0x758110` = `RouteManager_SetPortalCost` [verified disassembly]

`PortalsForObject 0x62F760` into a vector, then `portal+0x2C = cost` for each, then the references released. **Writer of
portal cost: keeps the lock** (§12.2 had it from the script name; now from the code).

## 22. `[ObstructAtRO]`: `RouteObstructionAtPoint` on the clone (ts3native, default off, NOT RUN)

### 22.1 What it does [built: `gvro.cpp` `OaHook`, `OaRun`, `OaLocked`, `OaScan`]

An entry detour on `0x655880` (6 bytes `55 8B EC 83 E4 F0`, the prologue `[ObstructedRO]` checks on its sibling). On the
sim thread while a route job runs, and only when the thread holds no `+0x918`, serves no job callback and has no gate frame
open (`OrHook`'s conditions):

- phase 1, no lock, the game's own calls in its order: `World_LotLookup(self+0xC)`, the level `0xADD860`, `0x82EDB0`'s own
  lookup on `world+0x964`. The height is not computed (the query reads x, z and the level; as `[ObstructedRO]`).
- phase 2, QL shared (try only), S2's protocol (tripwires armed, sequence, epoch, generation): the lot table hit for gate 0
  (`SrEntry`; a miss would insert: the game), the obstacle query as S2's point query (`g_orQuery` = `SrQuery`, record =
  ctx's option word, `+0x24 = 1`, radius `[0x106E8A8]`), then the function's loop for classFlag 0 on the clone's list in
  `0x82EDB0`'s order, through the game's slots `+0xC` and `+0x84`. The refcount +1/-1 is skipped (net 0).

Given back to the game (it waits as today), each with its own count: no lot at the point (the `0x62D0D0` path is not
cloned), classFlag 1 with footprints found (slot `+8` is not read for this call; `gvhits.cpp` has it for the validator),
an ignore list that lies inside the route the running job plans (or the job's route cannot be found), `self+8` not the
world's planner, a dirty lot index, and every S2 doubt (QL busy, lot not in the table, deferred flush, shape, tripwire, job
end). `Shadow=1`: with no job running (and none allowed to start: `g_rgOffSimOpen`), the game's call, then the clone,
compared; `SHADOW MISMATCH` lines (16 at most).

Keys: `[ObstructAtRO] Enabled=0`, `Mode=1`, `Shadow=0`. Export `ts3native_obstructat` (size 0x48, `+0x00` mode =
`native_set obstructat`, `+0x04` shadow; docs/registry.md). Line, once a minute when installed: `obstruction at point RO:
mode M shadow S calls C, answered in a job A (obstructed H) max U us; given back: state, QL busy, lot, shape, tripwire, no
lot, class flag with footprints, job's route; shadow runs R mismatch X`. Address row `0x00655880` (EA 1.69: unresolved, the
feature prints its SKIP line there). Repro `ip-obstructat-storm`.

### 22.2 Checks run, and what is not checked

- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: 0 errors, no warning in the new lines.
- `node tools/selftest-all.ts --ci`: 200 PASS, 0 FAIL.
- `steerro-test` section OA (2026-11-20, branch `fix/obstructat-check`; clang-cl build under Wine as §8.5, `SRT_ONLY=OA`): the
  game's own `0x655880` (its lookup, transform `0x62EF30`, query `0x82EDB0`, `0x8294E0` / `0x81C970` and loop; the level
  `0xADD860`, `0xB015B0` and the height `0x74A8F0` faked, the height returning a y far from the caller's) against `OaRun` on
  random worlds, points, contexts (none, own id, ignore list or none) and both class flags: **30,000 calls, 24,394 compared
  equal** (4,768 name an object; 4,453 class flag with nothing near), **0 differ**, 5,606 given back (no lot 2,091, class
  flag with footprints 2,123, lot not in the table 1,392: the game inserts it there). The fakes saw the same lot and the same
  point `{x, y, z, x}` from both sides; references balanced. Then the detour on the mapped function: 2,712 answers in a
  "job" equal to the game's, 689 given back for the job's own route, shadow 3,735 runs / 0 mismatches, QL busy 51 times -> the
  game's call. 12 call edges and the bytes of `0x8294E0`, `0x81C970`, the ctx reads and the flag test match the image. Eight
  wrong readings put into `OaRun` on purpose (own id dropped or read at `+4`, type 1 dropped, ignore list by low word only,
  option word 0, level 0, lookup `(z, x)`, point w 0) each stopped the section. The whole `steerro-test` ends `steerro-test
  ok` with the other sections' numbers unchanged. **So the reading of §21.1 holds**: lookup `(x, z)`, the point's layout, ctx
  `+0` / `+8` / `+0xC` / `+0x28`, the transform's outputs dead, the query blind to y. Nothing in `OaRun` had to change.
- Selftest case `TestObstructAtRO` (`gvro-test.h`; clone and hook on fakes, the gate's lot and the query's lot apart, every
  given-back reason, shadow compare), built with clang-cl and run under Wine before and after `TestGoalValidRO` /
  `TestObstructedRO`. Pass. The full selftest needs the MSVC CI build.
- Still read only, not run: the no-lot path (`0x62D0D0`; given back before any routing read), the class flag's loop with
  footprints (slot `+8`; given back), the real `0xADD860` / `0x74A8F0` / `0xB015B0` (lot objects and terrain; the clone calls
  the first as `[ObstructedRO]` does and skips the other two), a world where `self+0xC` and `world+0x964` are different
  managers (selftest fakes only). The section's worlds never give a deferred flush or an invalid polygon cache for this call.
  The job's-route test takes any ignore list within 0x1000 bytes of the job's route as that route's own (the route's size
  is not known): another route allocated right behind it would be given back too; the `job's route` count shows it.
  **`Mode=0 Shadow=1` first stays the order** (§23.4).
- Not known: how many job-time calls are classFlag 1 with footprints (given back), i.e. how much of the 508 ms it removes.
  The `class flag with footprints` count of the first arm says it.

## 23. Read, not built

### 23.1 `0x62FB74` from `0x654BE5` (476 ms, the second largest)

The prediction of §12.2 / §19.2 holds: all 8 lines with that word are the call at `0x654BE0` in `0x654790`. Design SH (a
QL shared hold around `0x654790` at call `0x659787`, gate 12 passing inside) is still the fit. Open before it can be
built: `0x654790` reaches `World_LotLookup`'s own lock `0x10786C8` (§17), so `LockAcquireHook`'s `QlSharedTrip(SLD_LOCK)`
must be shown to let that one lock pass without dropping the hold, or the hold drops on every call and nothing is gained;
and an offline section that runs the game's `0x654790` under the hold against the plain call. About 1-1.5 h. Not started
in this pass (the 45 minutes went to §22). **Built since: §24** (the lock question is settled in §24.1; the offline
section is still open).

### 23.2 `0x62EF7A` from `0x82E696` (413 ms)

`RoutingBlockerCheck2d` does rank now (§17 said it did not: the stall-watch names missed it; the caller word does not).
`rtoro.cpp` clones this check as a step of `RouteToObjectCheck`; the IC-level stub on the call to `0x82E340` (§12.2: the
segment gather `0x631E20` with its pin and references rules SH out) is the next build after §23.1, about 1-2 h with its
offline section. **Built since: §24.**

### 23.3 `0x760BFF`: which sync plans [verified: disassembly of the call sites + s189 log]

`0x7641D0` (return `0x76429D`) is called at `0x7651DC` (`IC_Route_Plan 0x765160`) and `0x7654C8` (`IC_Route_Replan
0x765490`); `0x7642C0` (return `0x764400`) at `0x76542C` (`IC_Route_PlanFromPoint 0x765240`) and `0x7655C3`
(`IC_Route_ReplanFromPosition 0x765550`). Both call the goal step `0x7606E0` (gate 10). So the caller word splits the key
by IC pair, not by reason.

s189's sync plans >= 40 ms (18 lines): **16 are `entry Route_PlanLocked (no IC)` with managed caller
`ScriptCore.Route.Route_PlanFromPoint`** (41-93 ms each, about 0.9 s), 1 `ReplanFromPosition / Skipped` (75 ms), 1 `Plan /
Los` (77 ms). `(no IC)` means no IC frame of ours was open: these plans never reached a START, the `NoStart` class of §19.3.
So for `0x764400` (4 lines / 222 ms) the reason is `NoStart` on PlanFromPoint; `SkipRetry` cannot help (nothing was queued
to be skipped) and neither can M1Retry (no START was refused). What would: give those PlanFromPoint calls a START (why the
A10 kind-2 hook does not arm for them: `KindsUnseen` scope, no point argument, or the watchdog-only hook; `arWdPass` and
the `pkAfSkip` counters say which) and then they park like any other. That is a read of `a10.cpp`'s arming rules, not the
small SkipRetry pattern; not built. `0x76429D` (7 lines / 225 ms, all under 60 ms) are Plan / Replan sync plans mostly
below the 40 ms line threshold, so the log does not give their reasons; `Los` is one of them and must stay synchronous.
`QueueFull`: 0 lines in s189 (3 in ten logs before): parking it is the same `J_PEND` + `FbRetry` pattern, but with nothing
to measure in this arm it was not built.

### 23.4 Next storm, on arm

On top of §20.4:

```
[ObstructAtRO]
Enabled=1
Mode=0
Shadow=1
[HitchAttr]
RouteLockMs=20
RouteLockCallers=1
```

First pass as written (compare only; `shadow runs` > 0, `mismatch` 0). Second pass `Mode=1`: `answered in a job` against
`class flag with footprints` and `no lot`; lines with key `0062EF7A` and first caller `00655ABE` against s189's 13 / 508 ms;
no new key `00655AFB`. Also read `arWdPass` / `pkAfSkip` for §23.3.

## 24. `[Blocker2dRO]`: the IC's `RoutingWorld_BlockerCheck2d` call on the clone (ts3native, default off, NOT RUN)

2026-11-20, branch `fix/blocker2d-ro` (from `work/combo-1117` a7ecd7ba). The build §23.2 asked for.

### 24.1 The call [verified disassembly `0x74E110..0x74E288`, `0x82E340..0x82ED9E`, `0x62EF30`, `0x814C30`, `0x81CB60`, `0x81C880`, `0x81C970`]

`IC_RouteManager_RoutingBlockerCheck2d 0x74E110` (cdecl: x0, z0, level, x1, z1, a goal word, four option words, the target object's id,
an ignore id, an option word): `World_LotLookup` on wm `[0x11ECBC4]` for the start (no lot, no world or no object manager:
returns 8), the height `0xC6A250`, the options object on its stack (`0x81CB60`: the four words at `+0x28..+0x34`, five empty
vectors), the target object by id (`0x939560`; may be none), then **one call, `0x74E261`**:
`0x82E340` (thiscall world; out, lot, start {x, y, z, x} with the level at `+0x24`, goal {x, z}, opts, target, &ignore id,
five zero bytes, the option word; `ret 0x34`). The IC returns `out[0]`.

`0x82E340` takes no lock of its own. Its two routing touches each wait behind a route job: the lot transform `0x62EF30`
x2 (`0x82E691`, `0x82E6C2`: gate 0, key `0x62EF7A`, caller words `0x82E696` / `0x82E6C7`) and the segment gather `0x631E20`
(`0x82E6EB`: Contains, the cell graph, pin, flush, references). So, as §21.1 found for its neighbour, answering only the
gate would move the wait to the gather: both are answered together. Everything else in the function is sim-owned or
physics: the option tests, the option copy `0x81D3C0` the gather's record points at, the ignore ids (`*ign` when not 0, the
target's, with option `0x80000` the target's parent, with `0x100000` its slot children), the per-footprint rules (id not
ignored, not in the options' list `0x81C970`, not in the lot's Sim list when T(0, 4), slot `+0xC0`, the level-0 segment
test `+0xD8` with radius `+0x44`, then the type rules and the merge `0x829250`), and, when no footprint answered and option
4 holds and `b34` is 0, five physics range samples along the segment (`0x686A90`, `0x682590`, `0x82A780`). The debug
segment draw `0x814C30` does nothing unless byte `[0x11D90B0]` is set.

`rtoro.cpp`'s `RtoBlock` already is this function from the two transforms to the merge (it is step 7 of
`RouteToObjectCheck`, `[RouteToObjRO]`, on by default since 2026-11-04), and `RtoTail` is its physics samples.

### 24.2 The stub (`rtoro.cpp` `B2Stub`, `B2Run`, `B2Phase1`)

One call-site retarget, `0x74E261` -> `B2Stub`. On the sim (owner) thread while a job runs, when the thread is not the
`+0x918` owner, serves no job callback and has no gate frame open (`AvMayRun`):

- phase 1, no lock: the option tests (`b24 |= T(2)`, `b28 |= T(1)`, `o4 = b2c || T(4)`, `T(0, 4)`, `T(0, 0x800)`), the
  ignore ids in the game's order, the option copy with bit 4 forced when `o4`.
- phase R, QL shared (try only), `RtoLocked` (tripwire state, QL sequence, epoch and job generation before and after):
  the lot table hit for gate 0 (`SrEntry`, GetOrInsert rules), then `RtoBlock`.
- phase P, QL released: `RtoTail` when the footprint pass left code 0 and the tail applies (`Tail=1`).

An answer writes `out[0]`, `out[2]`, `out[3]` and returns `out`, as the function does (`out[1]` is never written by it).
Given back to the game's call (today's wait), each counted: may not (the `AvMayRun` cases), state (not the routing world,
no planner), QL busy, lot (table miss: the game inserts; deferred flush; pin moved), shape (unknown footprint class or
slot), polygon cache, tripwire, job end, tail (`Tail=0` only), option (the debug-draw byte; `0x100000` with a slot
component on the target: the children iterator `0x952E40` is not cloned). `Shadow=1`: with no job running and none
allowed to start (`g_rgOffSimOpen`), the game's call, then the clone, the three words compared; `SHADOW MISMATCH` lines
(16 at most).

The answer is the game's own for every Sim (no scope gate, nothing relaxed). In time it differs only as §8.3 says: the
clone reads between the job's writers under QL, the game's call reads after the job.

Changed in the shared clone, for both users: `RtoBlock`'s tail gate reads `g_rtoTailOn` (set by `RtoRun` from
`rtoTailMode`, by `B2Run` from this block's `b2Tail`), and `RtoTail` takes optional counter pointers so `[Blocker2dRO]`
counts its tails in its own block. `[RouteToObjRO]`'s behaviour and counters are unchanged.

### 24.3 Switches, counters, files

- `ts3native.ini` `[Blocker2dRO] Enabled=0` (install), `Mode=1` (start of the runtime switch: export
  `ts3native_blocker2dro` `+0x00`, `native_set blocker2dro`), `Shadow=0`, `Tail=1`. Needs `[RouteGate] Enforce=1`,
  `[RouteAsync]`, the S2 writer set. Independent of `[RouteToObjRO]` (shared code, own site).
- One line a minute when installed: `blocker check 2d RO: mode M shadow S tail T calls C, with a job J: answered A (code 0
  .., 1 .., 3 .., 4 .., 6 .., 7 ..; physics tails N, hit H; T ms, max U us), the game's call G ms (max V us); given back:
  state, QL busy, lot, shape, polygon cache, tripwire, job end, may not, tail, option; shadow runs R mismatch X not
  compared Y`.
- Files: `native/ts3native/rtoro.cpp` (the section at its end), `blocker2dro-test.h` (selftest case `TestBlocker2dRO`),
  `steerro-test.cpp` section B2, `ts3native.cpp` (three lines), `ts3addr-ea169.h` (rows `0x0074E261`, `0x0082E340`,
  `0x011D90B0`, all unresolved: the EA 1.69 build logs SKIP), `ts3native.ini`, `tools/ini-sections.json`, `tools/ea-ini.ts`,
  `tools/harness/native-switches.ts`, `docs/registry.md`, `docs/68-hook-conflict-map.md`, repro `ip-blocker2dro-storm`.

### 24.4 Checks run (2026-11-20, no game), and what is not checked

- **`steerro-test` section B2** (clang-cl build under Wine with §8.5's recipe, `SRT_ONLY=B2`, the Steam 1.67 code mapped
  at its addresses): the game's `0x82E340` in the IC's form (its whole path: option tests and copy by the game's own
  `0x81CB60` object, both transforms, `0x631E20`, `0x667400`, the collector, the footprints' own slots, the merge) against
  `B2Run` on random worlds, segments, levels -1..2, option bits, option id lists, targets, ignore ids and, in a quarter of
  the calls, non-zero byte arguments: **60,000 calls, 56,324 compared equal** (code 0: 46,858, 1: 133, 3: 535, 4: 541, 6:
  422, 7: 7,835; 30,502 footprints gathered; 37,564 with a target; 14,675 reached the tail), 3,676 given back (lot not in
  the table: the game's insert is not run on the fake table), **0 differ**; `out[1]` untouched, references balanced; 15
  call edges, `ret 0x34` and the IC's result load match the image; the stub passes through off the owner thread and
  with no job; one shadow run on the game's code, 0 mismatch. This is also the first offline comparison of `RtoBlock`
  itself with the game's function (until now `steerro-test` checked `[RouteToObjRO]`'s three call edges only). The whole
  `steerro-test` (400 cases) ends `steerro-test ok`.
- Selftest case `TestBlocker2dRO` (the stub on fakes, no thread, no timer, its own preconditions): 42 hand-derived cases
  x (Mode 1 with a job, shadow), incl. each byte argument against its option bit, the ignore ids with and without a
  target, option `0x80000`'s parent, the merge, the physics tail with `Tail` 1 and 0, and the give-backs; then switches
  off, another thread, no job, may not, QL busy, tripwire, job end, shadow skew. Built with clang-cl and run alone under
  Wine together with `TestRouteToObjTail`: pass. `TestRouteToObjRO` does not run alone in that build (it stops silently,
  the same on the base commit), so **the unchanged behaviour of `[RouteToObjRO]` after the two small changes of §24.2 is
  read, not run: the MSVC CI selftest is the check.**
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: 0 errors, no warning
  in the new or changed lines. `node tools/selftest-all.ts --ci`: 200 PASS, 0 FAIL. No MSVC build here.
- Not covered offline: the physics samples (no physics world in the test process: both sides return before the first
  sample; `RtoTail` has its own selftest and runs in the game under `[RouteToObjRO] Tail=1`), the lot's Sim list
  (`lot+0x2C0`, T(0, 4)), option `0x80000` against the game's component lookup (selftest fakes only), a deferred flush
  and an invalid polygon cache (the test worlds have neither), the IC body above the call (read from the disassembly:
  argument order and the start point's layout; a wrong reading would show as a shadow mismatch).
- Not known: how many of the IC's job-time calls are given back (lot misses, `0x100000`), i.e. how much of the 413 ms
  goes; and §7.1 applies: the frame's next real taker may inherit the wait.

### 24.5 Next storm, on arm

```
[Blocker2dRO]
Enabled=1
```

## 24. `[LandCheckRO]`: the closest-waypoint gather under design SH (ts3native, default off, NOT RUN)

2026-11-20, branch `fix/land-check-ro`. Tools: capstone on the Steam 1.67 code (function bodies, a direct-call closure of
`0x654790` with every indirect call, absolute write and import listed, a rel32 scan for callers, a field-offset scan of
`0x61C000..0x690000`), `functions.csv`. No Ghidra run, no game run.

### 24.1 The open question of §23.1: `World_LotLookup`'s lock [verified disassembly + source]

- The path is `0x654790 -> 0x650030 -> 0xC68990 -> 0xC688A0`. `0xC688A0` starts with `cmp byte [wm+0x1CC], 0; je` and calls
  the lot index rebuild `0xC68680` only when that byte is set (`0xC688A6..0xC688AF`). The rebuild is the only function of
  the gather's closure that calls `Lock_Acquire` (`0xC6869C`). The lock is the object `wm+0x228` (`lea ecx, [ebx+0x228]`);
  **`0x10786C8` is the timeout word pushed for the call, not the lock** (the reading docs/62 gives for `0xFBA5CC`).
- So with a clean index the gather never meets that lock. With a dirty index: `LockAcquirePlain` sends every acquire of a
  thread with `t_slHold` set to `LockAcquireFull`, whose first statement is `QlSharedTrip(SLD_LOCK)` -> `SlDrop`: QL is
  released **before** the game's lock call. The hold is never open across the lock; what follows runs as today (the
  rebuild on the sim thread, then the gate's wait). Nothing of ours is held while it waits.
- §23.1's worry ("the hold drops on every call and nothing is gained") does not hold: the drop happens only for a dirty
  index. The build excludes that case up front anyway (`dirty lot index`: no hold is started, the game's call), and the
  drop stays as the answer when the byte is set by another thread after the check.
- The rest of the closure (108 functions by direct call): the imports reached are the allocator's `EnterCriticalSection` /
  `LeaveCriticalSection` (not `Lock_Acquire`, so no drop: the candidate vector may grow inside the hold), `VirtualAlloc` /
  `VirtualFree` / `VirtualQuery`, `GetSystemInfo`, `memmove`, `ceil`. No wait import, no write to an absolute address. The
  water point test `0xB793F0` (flag `0x800`) reaches no gate site and no `Lock_Acquire` by direct call; it has virtual
  calls (`0xB74D05`, `0x746119`, `0x958643..0x95895F`, `0x8A65EB`, `0x9EC452`) **[not followed]**: a lock or managed call
  behind one of them drops the hold like any other.

### 24.2 What the gather is, and a finding about its area [verified disassembly]

`0x654790` is thiscall(area), no stack argument, AL; one caller, `0x659787` in `ClosestWaypoints_Try 0x6595B0`, only for an
area whose flag `0x400` is set. It resets the area's candidate vector (`+0x34 = +0x30`), walks the chunks of the area,
appends one 32-byte record per accepted waypoint (`0x654240`, may reallocate) and clears `0x400`. For a waypoint of type 2
or `0x15`: `0x650030` (the lot under the waypoint), `LotTable_GetIfPresent 0x62FB60` = `LotTable_Contains` (gate 12, return
`0x62FB74`) then `GetOrInsert` (gate 0, return `0x62FB8B`), then the entry's waypoint map `+0x120` (`0xD38B00`, a pure
find), `+0x10`, `+0x16C`, the level table `+0x90` and the hub's position `+0x50 / +0x54`. The two gate frames are the only
places the function waits.

- **The follow-up reads of the entry** (§12.2's condition for SH): the writers of `+0x120` / `+0x16C` found in
  `0x61C000..0x690000` are the entry's constructor and copy in the insert path (`0x62AA60`, `0x62DE40`: W5),
  `0x6236D0 <- 0x63C8E0 <- RoutingWorld_LoadLotData 0x82CA10 <- 0xAB4920` (lot load, not a plan step) and `0x630CF0`
  (`+0x16C`; callers `0x829D50 <- 0xAB1EB0`, the coarse rebuild gate `0x835110`, `0x83575D`: lot-side calls, their
  threads not traced); the level table is
  W4's. Under the hold W4 / W5 are excluded; today these reads run after the gate returned with no lock at all.
- **The gate is not a guard the caller relies on**: it is reached only when an area must be gathered and has a lot
  waypoint. A call that finds its area in the cache, or gathers an area with no lot waypoint, runs the whole IC beside
  the job today.
- **Finding: the area is shared with the route worker and has no lock.** The area is one of the 10 blocks of the
  planner's FindClosest object `W+0xE0` (`W = [world+0x960]`: the IC takes it through `0x829C00`, and so does
  `RoutingJob_Execute`, p4clone.cpp / docs/94 §21.13). `0x652D80` relinks that list (move to front) and recycles the
  oldest block, and both threads append to block vectors: the worker inside plans under `+0x918`, the IC with no lock (it
  only meets `+0x918` at the two gates above). With plans on the sim thread (the game as shipped) the two cannot
  overlap; with `[RouteAsync]` they can, today, whenever the IC runs during a job **[inferred from the code paths; no
  crash is attributed to it, nothing measured]**. Not fixed here (a lock around `0x6595B0` would deadlock with the gate
  wait); listed in §24.5.

### 24.3 The build (`landro.cpp`, the hold in `slotro.cpp`)

- `slotro.cpp`: the hold has a kind (`t_slKind`: `SLK_SLOT`, `SLK_LAND`); `SlBegin(dry, kind)`, `SlGate`, `SlDrop` and `SlEnd`
  send the land kind to `LcGateWhy` / `LcOnDrop` / `LcRelease`. `[SlotEnabledRO]`'s own behaviour and counters are
  unchanged. One hold per thread: a gather inside a slot hold (or the reverse) runs the game's code.
- An entry detour on `0x654790` (6 bytes `55 8B EC 83 E4 F8`; no branch in `.text` lands in them). On the sim (owner)
  thread while a job runs, and only when the thread holds no `+0x918`, serves no job callback and has no gate frame
  open: QL shared (try only), then the game's own gather. Inside, `RgEnter` passes **exactly** the gate-12 entry that
  returns to `0x62FB74` and a gate-0 **hit** that returns to `0x62FB8B`. Everything else drops the hold (QL released,
  the game goes on and waits at its next gate as today), each with its own count: another gate site, another caller of
  either gate, a gate-0 miss (an insert), an odd lot table, a `Lock_Acquire`, a host or managed call.
- Given back before a hold starts (the game's call, today's wait): state (lock owner, job callback, open gate frame),
  nested hold, a broken clone path, QL busy, a dirty lot index, the worker on the same area.
- **The area hand-over** (so the hold never puts both threads into one block's vector where today's gate wait kept them
  apart): the hook runs on every thread. The sim publishes the area of its hold, any other thread the area it gathers
  (each publishes, then reads the other's). The sim starts no hold on an area the worker is gathering, and drops its
  hold at the next gate when the worker arrives (`worker`: it then waits at that gate, which is today's order of
  events). The worker waits at the gather's entry while the sim's hold is on that area; the hold contains no wait
  (§24.1), so this ends with the sim's own work. A cap (4000 pause loops, then 250 sleeps of 1 ms; `gave up`, must stay
  0) sets Mode to 0 and lets the worker go on. After a drop for another reason the sim runs on to its next gate with
  the area free, as today.
- `Shadow=1` (changed 2026-11-21, §26): with no job running, the game's gather as with the switch off, then (job starts
  held off, no job since) the gather again under a dry hold **on a private copy of the area**; AL, the area's flags, the
  candidate count and an FNV hash of the candidate records compared (`land check RO: SHADOW MISMATCH ...`, 16 lines at
  most). It checks that the gates passed without `+0x918` change nothing; it costs a second gather per call.
- The answer is the game's own code for every Sim: no scope gate, nothing cached, nothing skipped.

Keys: `[LandCheckRO] Enabled=0`, `Mode=1`, `Shadow=0`. Needs `[RouteGate] Enforce=1` (gate stubs 0 and 12), `[RouteAsync]`,
the QL writer stubs W4 / W5 / W6g. Export `ts3native_landcheckro` (size 0x88; `+0x00` mode = `native_set landcheckro`,
`+0x04` shadow; docs/registry.md). Line, once a minute when installed: `land check RO: mode M shadow S gathers N, in a
job J: held to the end T (gate 12 passed, gate 0 hits, candidates; ms, max us); dropped: gate, miss, lock, managed, table,
caller, worker; not held: state, nested, broken, QL busy, dirty lot index, worker on the area; worker gathers G waited W
(max us) gave up X; shadow runs R mismatch M dropped D`. Address row `0x00654790` (EA 1.69: unresolved, SKIP line there).
Repro `ip-landcheckro-storm`.

### 24.4 Checks run (2026-11-20, no game)

- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: 0 errors, no
  diagnostic in `landro.cpp`, `landro-test.h` or `slotro.cpp`.
- Selftest case `TestLandCheckRO` (clang-cl build, run under Wine three times in one process between `TestSlotRo`,
  `TestEditPathRO`, `TestConnRoomsRO`, `TestPlaceTestRO`): the gate rule (pass, other caller, miss, active lot, table
  shape, other site, lock, managed call, worker on the area), `LcBegin`'s refusals, the hook (off, shape, hold, drop
  inside, state fallbacks, QL busy, unwind), shadow (equal, skewed, dry drop), another thread's gather (free, waiting
  for the hold's end, handed over at the sim's gate, the cap). No step reads a timer; every sub-case asserts the state it
  starts from. Pass. `TestSlotRo` passes unchanged. The MSVC `/W4 /WX` build and the full selftest need CI.
- `node tools/selftest-all.ts --ci`: pass.

### 24.5 Not verified, not done

- **No offline section** runs the game's `0x654790` under the hold against the plain call (a waypoint chunk table, lot
  waypoints, lot entries with their map: about 1 h). Until then the in-game shadow pass is the equality check. Read from
  the disassembly only: the area layout (`+0x24` try, `+0x2C` flags, `+0x30 / +0x34` vector, `+0x40` FindClosest object),
  the FindClosest fields (`+0xC` P, `+0x10` W, `+0x14` world manager, `+0x18` graph) and `W+0xC` = the world manager.
  **Run `Mode=0 Shadow=1` first.**
- The entry fields' writer list (§24.2) is a scan of `0x61C000..0x690000` for two offsets, not a proof for `+0x10` and
  the hub records; they are read with no lock today too.
- The shared area cache (§24.2) outside the two cases the hand-over covers: both threads in `0x652D80`'s list at once,
  the sim recycling the block a plan is still scoring. Unchanged by this build; a candidate for its own fix (a private
  FindClosest object for the IC's calls would remove the sharing).
- How much of the 476 ms goes: unknown. The gather's time itself stays (docs/679); only its gate waits go, and §7.1's
  rule applies (the next taker in the frame inherits the wait).

### 24.6 Next storm, on arm

```
[LandCheckRO]
Enabled=1
Mode=0
Shadow=1
[HitchAttr]
RouteLockMs=20
RouteLockCallers=1
```

First pass as written (compare only): `shadow runs` > 0, `mismatch` 0. Second pass `Mode=1` (repro
`ip-blocker2dro-storm`): `answered` against the give-back counts; lines with key `0062EF7A` and first caller `0082E696` /
`0082E6C7` against s189's 5 / 413 ms; `the game's call` ms is what is left of this site's wait.
First pass as written (`shadow runs` > 0, `mismatch` 0). Second pass `Mode=1 Shadow=0`: `held to the end` against `in a
job`, the drop and not-held counts, `gave up 0`; lines with key `0062FB74` and first caller `00654BE5` against s189's
8 / 476 ms. The default storm kinds do not force the IC: use `kinds` with `taxi` and `stray` (repro
`ip-landcheckro-storm`).

## 25. s216-s225 read: which waits are left and why; `[SteerRO] RetryWait` (ts3native, default off, NOT RUN)

2026-11-20, branch `feat/lock918-rest-1120`. Input: the nine on arms s216, s217, s218, s219 (calm), s221 (calm), s222, s223,
s224 (calm 6 min), s225 (the others: 2-min Isla Paradiso storms, seed 152), ts3native 0.36.267; `route lock wait:` lines
(>= 20 ms) and `hitch:` frames with cause `lock918`. No game run in this pass. s226 was still running.

### 25.1 What the arms did not have on [verified: the `job yield mode` and ini lines of each log]

- **`[RouteAsync] JobYield=0` in all nine** (`route jobs sim waits: ... job yield mode 0 asks 0 busy 0`). The repo ini has
  `JobYield=1` since 0.36.253 (docs/183 §6: on by default, household included, measured); the test rig's `Bin\ts3native.ini`
  still carries `JobYield=0` and no run script sets it. ts3fixes `kJobYield` is on, so `StepAside` asks and the native side
  answers "not busy" every time.
- `[ObstructAtRO]`, `[Blocker2dRO]`, `[LandCheckRO]` are not in any of the nine arms (only in s204-s213).
- `[RouteAsync] SkipRetry` and `ForeignKindsUnseen` are off in all nine. s205 / s213 set `ForeignKindsUnseen=1`, which is
  the Replan bit; the `Foreign` class of §24 is kind 2 (PlanFromPoint) and needs `ForeignKindsUnseen=2`.
- `[HitchAttr] RouteLockCallers` is off in all nine, so the gate keys (`0x62FB74`, `0x62EF7A`, `0x62F782`) have no caller
  word; their split below is s189's (§21) [inferred for these arms].

### 25.2 The table (nine arms summed: 385 lines, 21,229 ms; 41 `lock918` frames)

| key | lines / ms / max | `lock918` frames (frame's largest key) / lock ms | who | why not answered in these arms |
|---|---|---|---|---|
| `0x62FB74` gate 12 | 99 / 4,860 / 202 | 8 / 1,205 | s189: `ArePointsConnectedByLand` gather `0x654BE5`, the writer path `0x834201` | `[LandCheckRO]` off (s213 with it serving: 338 ms against 540 a run here); `0x834201` is a writer path and keeps the lock (§21.2) |
| `0x760BFF` gate 10, a sync plan | 90 / 4,795 / 261 | 9 / 1,407 | sync plans behind a running job. By the `route phases` line beside the wait: `Plan / Los` 17 / 1,187 (`ViewObjects.RouteToObject`), kind-2 pass-through `(no IC)` 12 / 650 (§24's `Foreign`; tally lines of four arms: `Foreign` 29-113, `NoStartMsg` 3-58 a run), `Plan / NoStart` 6 / 435, `ReplanFromPosition / NoStart` 1 / 156, `Skipped` 3 / 132, no line (plan under 40 ms) 51 / 2,235 | **`JobYield=0`** (§25.1): every refused START and the poll cap run `StepAside` before the sync plan [verified `RouteBackoff.cs`]. Not reached by it: `Skipped` (READY then the plan, no `StepAside`: `SkipRetry`, off) and a task that may not yield |
| `0x62EF7A` gate 0 | 59 / 3,148 / 161 | 5 / 734 | s189: `RouteObstructionAtPoint` `0x655ABE`, `BlockerCheck2d` `0x82E696` | `[ObstructAtRO]`, `[Blocker2dRO]` off (s213 with both serving: 81 ms against 350 a run here) |
| `0x85FE10` steering query | 41 / 2,739 / 267 | 7 / 1,193 | S2's given-back calls: the `steer RO` line books 110-619 ms a run in them, 2,485 ms of the 2,739 | **nothing answers it**: `[SteerRO] Retry=1` is on and still gives back `QL busy` 2-11, `tripwire` 1-5, `state` 1-7 a run. Built: §25.3 |
| `0x62F782` gate 12 `PortalsForObject` | 27 / 1,538 / 172 | 4 / 700 | `SetPortalCost` (writer), `Slots_GetPositionOfSlotImpl` | writer keeps the lock; `[SlotEnabledRO]` on |
| `0xD882D7` coarse gate, `GetConnectedRooms` | 21 / 1,361 / 232 | 3 / 440 | `[ConnRoomsRO]` give-backs | `lot not built` (6 of 36 in s225): a room list not built yet is a write |
| `0x631814`, `0x631F40` | 13 / 991 / 224 | 3 / 509 | `[LotFrontDoorRO]` give-back, FGL footprints | - |
| `0x833697`, `0x83156D` | 14 / 859 / 208 | 1 / 208 | object footprint update | writers |
| `0x846D21`, `0x85F6CB`, `0x75AA16`, `0x63D89B`, `0x82F4AB`, `0x62FB8B`, `0x75A64C`, `0x62EB55`, `0xD866E3` | 21 / 938 / 77 | 1 / 142 | readers' give-backs | - |

The holder is the route worker in every line. Jobs that ended in the wait: own kind 0 the most (2,875 ms at `0x62FB74`,
2,691 at `0x760BFF`), A10 foreign kind 3 next (1,377 at `0x62EF7A`); most `0x85FE10` lines end without a job end (the sim
got the lock in a TailUnlock window).

So with the switches that exist turned on (`JobYield=1`, the three clones of §22 / §24, `SkipRetry=1`,
`ForeignKindsUnseen=2`) the largest key left without an answer is `0x85FE10`, then the writer paths.

### 25.3 `[SteerRO] RetryWait` (`steerro.cpp` `SrWait`, `steerwait-core.h` `SwLoop`)

`Retry=1` reruns a dropped clone at most 4 times within ~1 ms. A W1-W6 writer's exclusive QL hold or a tripwire writer
that lasts longer is given back, and the game's `Locomotion_ObstacleCheck` then waits at `+0x918` for the rest of the job
(or its TailUnlock window): 2-11 + 1-5 such calls a run carry 110-619 ms.

With `RetryWait=1` such a call waits for the writer instead of the job: `LocoHook` opens a `g_rgOffSimOpen` frame (no job
starts until the call is over, as the `+0x918` wait pump does), then loops: one pump step (up to 1 ms on the job's
managed-call event or its end, the call served with `HC_PUMP918`), the clone again from its protocol start (sequence,
epoch, generation read again, as `SrRetry`). The loop ends when the clone answers, the job ends (the game's call then runs
with no job, inside the frame), a rerun meets a reason waiting does not cure, or `RetryWaitMs` (1-250, default 50) is used
(the game's call waits as before). The answer is S2's clone answer or the game's call: no check is skipped, household
included as S2 itself. `RetryWait=2` only counts the calls that would wait.

Worst case [inferred]: a call waits `RetryWaitMs` longer than today when the writer's hold lasts to the job's end and a
TailUnlock window would have let the game's call in earlier; `budget used` and `the game's call after a wait` measure it.

Not covered: `state` give-backs (the job ended in the clone: the game's call runs with no job while `[LocoWindow] Mode=1`
holds starts), the 0x85F6CB gather (`[EditPathRO]` has its own loop).

Counters: exported block `ts3native_steerwait` (docs/registry.md; runtime switch `native_set steerwait`). Lines: minute
`steer RO wait: mode M budget B ms, waits N (QL busy a, tripwire b): answered .., job ended .., other reason .., budget used
..; T ms in the waits (max ..; .. of it answered), R reruns, S managed calls served; the game's call after a wait G ms;
count only: would wait W`; one `steer RO wait: N ms for a writer's hold (...)` line per wait >= 20 ms (64 a session). With
`[HitchAttr]` on a wait is booked as a `+0x918` wait behind the worker under key `0x85FD10`, so it stays in `lock918` and
splits from `0x85FE10`.

No compare mode: the clone is S2's, compared by `[SteerRO] Shadow=1` (no job running); a rerun has nothing to compare
with while the job runs.

### 25.4 Checks run (2026-11-20, no game)

- `steerwait-host-test.cpp` (clang++ -m32, freestanding): answered after three steps, tripwire / QL busy mixed, job end in
  a step, no job, a reason not curable (in the loop and at the entry), budget, budget 0: pass.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`) 0 errors, 170 warnings as before; `selftest.cpp` 0 errors,
  217 as before.
- Selftest block in `TestSteerRO` (`SrWait`: answered on the third rerun, a state reason, budget, no job, no frame leaked;
  the block's layout): written, **not run** (MSVC CI).
- `node tools/native-install-once.ts`, `node tools/selftest-all.ts --ci`: pass.

### 25.5 Next storm, on arm

First the config: `RouteAsync.JobYield=1` in every arm (the release value). Then, on top of s223's keys:

- count arm: `SteerRO.RetryWait=2` (read `would wait` against `QL busy` + `tripwire` of the `steer RO` line);
- serve arm: `SteerRO.RetryWait=1 SteerRO.RetryWaitMs=50`. Read `answered` against `waits`, `budget used` small, `the
  game's call after a wait` against s216-s225's `job waits in given-back calls` (110-619 ms), keys `0085FE10` + `0085FD10`
  together against 304 ms a run, no new stuck Sim, `steer RO: ... shadow` unchanged.

## 26. Shadow mode of `[ObstructAtRO]` / `[Blocker2dRO]` / `[LandCheckRO]` made free of side effects (2026-11-21, `fix/ro-shadow-side-1121`, NOT RUN, no MSVC build)

### 26.1 Why: repro `upstairs-landing-jam`, arm c

Two loads (landing-11202338, landing-11210005). Arms 0, a, b, d: the four household Sims reach the ground floor (crowd
4/4, 10-20 s). Arm c (= b with the three switches at `Mode=0 Shadow=1`): crowd 1/4 in both loads, alone and top pass.

What the logs show **[verified, counter lines and `gohere` lines of both logs]**:

- The plans succeed in every arm. In arm c three or four Sims stand within 0.3 m of each other at the stair top
  (927.2-927.7, 61.0, 1203.4-1203.8), each Go Here ends with `MidRoutePushRequested`, and the push routes (1.0-2.2 m) are
  replanned 15-18 times and end `route false`; the Sims are still on the landing or the stair when the 50 s wait ends.
  Arms a and d show the same push (1-3 Sims) and it ends `route ok` after one more plan.
- `[Blocker2dRO]`: its stub was not reached in arm c in either load (`calls 4` before and after).
- `[ObstructAtRO]`: load 1: no call in the failing segment (`calls 5` before it and after the last `route false`);
  load 2: 2 calls, both ended at `no lot` (the clone had read the lot index only).
- `[LandCheckRO]`: the only shadow path that ran in both failing segments (6 and 2 shadow gathers, mismatch 0).

So of the three, only the land check's shadow can be tied to the failing segments, and it was the only one whose shadow
wrote data the game reads: its second gather ran **on the live area** (`0x654790` resets the candidate vector at `+0x34`,
refills it and clears flag `0x400`; the function's only two writes through the area pointer, plus the append `0x654240`
on `area+0x30`, **[verified disassembly `0x654790..0x654CB0`, `0x654240`, `0x6534A0`]**), with the gates of
`LotTable_GetIfPresent` passed without `+0x918`. All three ran the game's own call inside a job-start hold-off
(`g_rgOffSimOpen`) that a call with the switch off does not have.

**Not shown:** how a doubled gather (equal result, 33 of 33 compares) changes route following on a stair. No per-call
line exists and the mechanism was not found in the code; the tie to arm c is the two loads (arm c is also always the
fourth crowd segment at the same game time, so position in the load is not excluded). The fix below removes every
difference a shadow run had from a switch-off run; the rerun decides whether arm c was the shadow's doing.

### 26.2 The rule and the build (`roshadow-core.h`, `landro.cpp`, `gvro.cpp`, `rtoro.cpp`)

With `Shadow=1` the game's call runs exactly as with the switch off; the clone works on copies; nothing the game reads
is written.

- `RoShadow(env, game, clone)` (`roshadow-core.h`): the game's call first, with no frame of ours open. Then job starts
  are held off (`g_rgOffSimOpen`, the worker's Dekker order) and the clone runs only when no job runs and none ran since
  before the game's call (`g_asyncRuns`, counted by the worker with `A_RUNNING` published); else the call is not
  compared. All three hooks use it.
- `[LandCheckRO]`: the dry gather runs on `g_lcScratch`, a private copy of the area holding the inputs as they were
  before the game's call (bytes `+0x00..+0x2F`, `+0x3C..+0x43`) and a candidate vector of its own, empty at the start. The
  game's vector code grows it through `EA_AllocTagged 0x4E3F90` (`0x6534A0`: an empty vector frees nothing) and the hook
  gives the storage back with `EA_Free 0x4E3F70` (cdecl, one argument, as the call at `0x6535B3`) after the compare.
  Nothing is published for the copy (`g_lcSim` stays NULL; `t_lcArea` = the copy, which no other thread gathers). The
  live area and its storage are only read (hash, count, flags). The record is written whole before the append
  (`0x654C66..0x654C9A`: all 32 bytes), so the hash does not see stack residue.
- Line: `land check RO: ... shadow runs R mismatch M dropped D not compared N` (`lcFb[6]`, `+0x60`: a job ran or runs
  since the game's call). The other two count such a call in neither `shadow runs` nor a give-back.

### 26.3 Checks (2026-11-21, no game)

- `roshadow-host-test.cpp` (clang++ `-m32`, freestanding, exit 0): the order (the game's call sees no hold-off, the clone
  one; a live job, a job that started, a whole job in between, a backing-off worker: no clone), the area copy (0-4
  candidates: the live area and its storage byte-equal across the clone, the copy's storage from the allocator and handed
  back, inputs from before the game's call, a skewed gather = mismatch, an empty vector hashes equal with and without
  storage), and the order used before this change run through the same checks, which it fails. Writing the test found
  one error of the first draft (an empty gather on the copy hashed differently from the live one).
- `TestLandCheckRO` section 4 and `TestBlocker2dRO` (MSVC selftest, **CI only, not run here**): the game's call with
  `g_rgOffSimOpen == 0` and no hold, the dry gather on `g_lcScratch`, the live fake area unchanged, one free of the
  copy's storage, a job in between not compared.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `steerro-test.cpp`: no error, no
  diagnostic in the changed files. `node tools/selftest-all.ts --ci`: pass.

### 26.4 Next run

Repro `upstairs-landing-jam` with the CI asi of this branch, twice. Pass: arm c crowd 4/4 in both loads, `land check RO`
with `shadow runs` > 0 and `mismatch 0`. If arm c still fails, the shadow switches are not the cause (26.1's open
point): swap arms c and d in the repro, and run arm c with one shadow word at a time.

