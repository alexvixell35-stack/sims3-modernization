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

## 20. `[RouteAsync] SkipRetry`: a skipped job is parked and retried (ts3native, default off, NOT RUN); the `rtwait` site

### 20.1 What it does [built: `ts3native.cpp` `SkPark`, `FbRetry`, `GetOptionHook` READY, `IcCollect`]

§19.3's largest class: the worker takes a queued job, finds the world not quiet (`k.rtBusy`, pending lot updates, a dirty lot
index) and marks it `J_SKIPPED`; the task's next READY poll then frees the slot and the whole plan runs on the sim thread
(`reason Skipped`), usually behind the job that runs by then. With `SkipRetry=1` that READY parks the START again instead
(`J_PEND`, the state M1Retry uses for a START refused for the same reason) and `FbRetry` runs in the same poll and on each
later one: it applies the pending lot updates under one `+0x918` try (`FbApplyPending`, as M1Retry) and queues the job; a
busy lock or queued discourage records wait a tick. Nothing else changes: the same IC on the worker, the same result, later
by READY polls of the Sim's own task (the Sim waits on its own timeline; the sim thread does not block).

Falls back to today's synchronous plan when:

- the route is not known unseen at START (`uf` mark or scope 2), i.e. household, selected or unknown scope, unless
  `[Experimental] HouseholdToo=1` lifts it (`HhExempt`); a PlayerFirst-marked route (the player's command) never parks,
  with the flag on too (`not parked: scope`);
- the START was parked `kSkMax` = 3 times already, or its 30 READY ticks (`kFbRetryTicks`, one count for all parks of the
  START) are used (`not parked: cap`, or `gave up` at the tick);
- `FbRetry` meets a refusal that waiting does not cure (closing, dirty lot index, `rtBusy`, `Los`, an active route): the
  plan runs at once with that reason, so a dirty index is still cleaned by the sync plan as today;
- the switch or `FallbackAsync` goes off while parked, or the plan is collected without a READY (`IcCollect`: `gave up`).

A parked slot is sim-thread only (the worker never sees `J_PEND`); the worker is finished with a `J_SKIPPED` slot before
READY reads it, so no new shared state. CANCEL and the stale-slot reclaim treat it as any parked START.

Counters: exported block `ts3native_skipretry` (no layout change): `+0x00` mode (`native_set skipretry`), parked, queued,
gave up, max ticks, not parked: scope, not parked: cap. Line: `route skip retry: mode M parked P queued Q gave up G max
ticks T, not parked: scope S cap C (skipped N)`.

Limit [inferred]: the seen-NPC share of the `Skipped` plans is not covered (scope unknown to the native side counts as
household). `not parked: scope` in the first arm says how large that share is.

### 20.2 The `rtwait` line names its site [built: `rtsites.cpp` `RtSiteName`, `SwNote`]

`route jobs: sim waited N ms for a job at rtwait (route H, own job, task could yield yes, site destroy)`: the kind of the
gate site that waited (`quadtree read / write / leaf`, `destroy`, `lot grid`, `pending updates`, `index rebuild`, `save`,
`build routing data`, `lot save`, `teardown`, `closing`, `steer`, `lost-removal flush`). Appended inside the bracket; the
report tools match the line's head only.

### 20.3 Checks run (2026-11-20, no game)

- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: 0 errors, the same warning counts as before
  the change (170 / 172 / 217, none in the new lines).
- Selftest case 2c in `TestFallback` (switch off; parked and queued in one READY with the worker's answer collected; scope;
  PlayerFirst; `kSkMax`; 30 busy ticks then the sync plan; switch off while parked; M1Retry's counters untouched; no timing
  assert): written, compiles, **not run**: the clang-cl build under Wine stops at the first worker START of `TestRouteAsync`
  (`selftest.cpp` line 1954, before the block; clang's SEH limit also needs `0x11D40C4` mapped for `AsyncLosRoute`). Needs
  the MSVC CI build.
- §19.2's `HsChain` block in `TestHitchSum`: run under Wine in the same build, passed (the run then stopped at a later,
  untouched assert of that test that expects earlier cases' state).
- `node tools/selftest-all.ts --ci`: pass.

### 20.4 Next storm, on arm

On top of §19.6: `[RouteAsync] SkipRetry=1` (with `FallbackAsync=1 M1Retry=1 LogFallbackMs=40`), repro `ip-skipretry-storm`
(`native_set skipretry`). Read `route skip retry:` (queued near parked, gave up small, max ticks far under 30, `scope`
against parked), the `sync plan ... reason Skipped` lines against the off arm, and the `site` word of the `rtwait` lines.

## 24. The `NoStart` PlanFromPoint plans are foreign STARTs: `[RouteAsync] ForeignKindsUnseen` (ts3native `a10.cpp`, default off, NOT RUN)

> **Port note (PR post/stutter-1123, on main 56acf732).** `[RouteAsync] KindsUnseen` (docs/662 §4), which this section's runs
> had on (`KindsUnseen=7`), is not in main and is not ported: main's `[RouteAsync] Kinds` carries the docs/288 §4 own-START
> gate (`ArOwnGateOk`: scope 2 from ts3fixes right before the START, HhExempt, plus the H2Ic / RefcountGuard prerequisites with
> KindsMode 1), which is the same rule, so `Kinds=7` on main is what `KindsUnseen=7` was on the combo branch. The foreign gate
> has no such scope rule on main; `ForeignKindsUnseen` adds it with the same scope test (`KdScopePeek == 2`). On main kind 2 is
> hooked for `WatchdogKind2` (release ON) and for this switch, so the pass-through and its reason tally (§24.3) apply there.

2026-11-20, branch `fix/nostart-plans`. Answers §23.3. No game run; s170 and s189 logs and the code.

### 24.1 Cause

**The kind-2 hook does arm; the START is refused one step earlier, at the foreign gate, and the pass-through hides the
reason.**

- [verified: s189 log] The own kind gate refuses nothing: with `KindsUnseen=7` every `KindsUnseen:` line ends `not unseen 0 0
  0` (own kind-2 passes 72 by the last line), and no `KindsUnseen=N leaves ...` line exists. So "kind not in the mask" and
  "scope not unseen" are both out for these plans.
- [verified: code] ts3fixes `AsyncKindStart` sends the **foreign** START (bit 61) when the caller's task is not the route's
  Sim (`AsyncEligible` false, `ForeignEligible` true): a boat or other vehicle planned in its rider's task, a route planned
  by another Sim's or a one-shot task. `A10StartBody` starts a foreign kind only with its `ForeignKinds` bit; the release ini
  has `ForeignKinds=4` (ReplanFromPosition only), so a foreign PlanFromPoint is refused `Foreign`.
- [verified: code] Kind 2 is hooked only for `WatchdogKind2` / `KindsUnseen` (in neither `Kinds` nor `ForeignKinds`), so
  `ArPass` sends every call without a job to the game's IC directly: no `SyncIc`, so no `route async: sync plan ... reason`
  line; `routephases.cpp` logs it as `entry Route_PlanLocked (no IC)`, route 0. The reason the START recorded (`g_rfbWhy`)
  is never printed. That is why §19.3 / §23.3 could only call the class `NoStart`. `arWdPass` counts these calls but is in no
  log line (the counter block is not dumped in a phase log).
- [verified: logs] The sync kind-2 plans slow enough for a `ufplansplit` line (>= 150 ms) are all boats: s189 `kind 2 173 ms,
  sync 46 ms (START refused), why -1, src BoatWaterScooter`; s170 `BoatSailBoat` 181 ms and `BoatSpeedFishingBoat` 183 ms. 3
  of 3; no Sim-follower kind-2 line says `START refused` in either log. The two stall-watch stacks beside a `0x760BFF` /
  `0x764400` wait (s189) are `Route_PlanFromPoint < PlanFromPointOrig < PlanFromPointAsyncBody < ... <
  Route.PlanToPointFromPoint`: the refused-START branch of ts3fixes under EA's `PlanToPointFromPoint` (the houseboat
  gangway leg of `SimRoutingComponent.EnterHouseboatThroughGangway`, run from the PostPlan callback of the boat's own plan,
  is the Isla Paradiso caller with a boat follower; docs/642 §7).
- [inferred] So the `(no IC)` PlanFromPoint lines (s189: the 16 of §23.3; s170: 23 `route phases` lines) are foreign kind-2
  STARTs refused `Foreign`. The
  shorter lines (41-93 ms) carry no follower name, so a share with another reason (a task that may not yield: no START
  message at all; `Active`; `NoYield`) cannot be excluded from these logs. The new tally line (§24.3) settles it on the
  next arm.

`ForeignKinds=6` would start them with no code, but for every scope (household boats too); docs/584 named it as an A/B
that was never run.

### 24.2 `[RouteAsync] ForeignKindsUnseen` (bits as `Kinds`; default 0)

`AfUnseenPass` (`a10.cpp`), asked by `A10StartBody`'s foreign branch for a kind without its `ForeignKinds` bit: the START
passes only when ts3fixes' scope answer for **this route** is unseen (`KdScopeUnseen`: op 11 right before the START; for a
vehicle the scope is its driver's, a driverless one by `UfDriverlessWhy`'s household rule). No answer, household, selected
(the player's Go Here), seen NPC, unknown scope and another route's answer stay refused `Foreign` and plan synchronously
exactly as today. `HhExempt` is the gate, so `[Experimental] HouseholdToo=1` lifts it (worker-computed answer, docs/271).
`ForeignKindsMode` (`afMode`, `--ab a10foreign`) switches it with `ForeignKinds`. A kind in it is hooked, and `ArPass`
treats it like `KindsUnseen`: every call without a job goes to the game's IC unhooked. After the gate the job is an
ordinary A10 foreign job (flag `af`: `KdA10ForeignMs` 1000 ms deadline, `Active` / `NoYield` / `Los` / M1 / queue refusals
and parking as for a `ForeignKinds` job, no fb cap; the worker runs the whole original IC). No new counter: `afSeen[]` /
`afStarted[]` / `afCollected[]` count them; log `route async: ForeignKindsUnseen: foreign kind K START of unseen route ...
passes the foreign gate (passes a b c, not unseen d e f)` at the 1st, 2nd, 4th ... pass per kind (24 lines at most).

**Known differences from the sync plan [inferred].** (1) The caller's task sleeps a tick at a time during the job, as for
every foreign kind-3 job since `ForeignKinds=4`. The gate is the **route's** scope: when a household Sim's task plans an
unseen NPC's route (a social's check, for one), that task sleeps for the job instead of blocking the frame; the result is
the game's own. Native code does not know the calling task's Sim; if that must stay synchronous too, ts3fixes has to hold
the foreign START back for a household caller. (2) A job past `KdA10ForeignMs` is cut (unseen routes only). (3) The moved
wait: the job holds `+0x918` on the worker.

### 24.3 The pass-through tally (log only, always on with a pass-through hook)

`ArPassNote` in the three A10 hooks: a call that `ArPass` sends to the game takes the refusal its START recorded for this
route and logs, at the 1st, 2nd, 4th ... such call per kind (30 lines at most): `route async: kind K call of route H runs
the game's IC with no job (reason); such calls of this kind by reason: Foreign n NoStart n NoStartMsg n ...`. `NoStart` =
refused at the own kind gate (or no point); `NoStartMsg` = no START was recorded for the route (ts3fixes sent none, or
another route's START came between). It takes the note as `SyncIc` does, so a later plan of the route does not inherit it.

### 24.4 Checks (2026-11-20, no game)

- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: 0 errors, no warning in the new lines.
- Selftest `TestA10` case 13c (switch off = refused `Foreign`, tallied; no START = `NoStartMsg`; no answer / household /
  seen NPC / other route refused; unseen starts as an `af` job and is collected; kind outside the mask and the own START
  refused; `afMode` 0; `Active`; HouseholdToo lift; `ArPass` both ways; every mark it reads set or cleared at its start):
  written, **not run** (needs the MSVC CI build).
- `node tools/selftest-all.ts --ci`: 200 PASS, 0 FAIL.

### 24.5 Next storm, on arm

`[RouteAsync] ForeignKindsUnseen=2` on top of §23.4 (repro `ip-foreignkindsunseen-storm`, two loads: 0 vs 2). Off arm: read
the `runs the game's IC with no job` lines; `Foreign` should carry most kind-2 calls. On arm: `ForeignKindsUnseen: foreign
kind 2 START` lines, fewer `entry Route_PlanLocked (no IC)` PlanFromPoint lines and fewer `0x760BFF` waits with caller
`0x764400`; `not unseen` counts the household / seen boats that stay sync; no new stuck boat rider.

## 27. s294-s309 read: the 142 frames by cause; `[Discourage] DeferObjInsert`, `[RouteAsync] DiscYieldOwn` (ts3native, default off, NOT RUN)

> **Port note (PR post/stutter-1123, on main 56acf732).** Only `[RouteAsync] DiscYieldOwn` (§27.5) is in this PR.
> `[Discourage] DeferObjInsert` (§27.4) is **not ported**: main already has `[Discourage] UofDefer` (docs/852, PR #491,
> default 1 since 2026-11-19), which queues the same insert `0x833680` at the same two sites behind the object's records.
> The s294-s309 arms ran on a branch without UofDefer, so their `lost-removal flush` / key `0x833697` waits are the waits
> UofDefer already answers on main. §27.4's extra rules (no deferral at the bracketed site `0x9462D4` or in the world's batch
> mode; one queued insert per object, a later insert replaces it) are kept below as a reading for UofDefer, not as code.
> The §27.8 arms C and D do not apply to main.

2026-11-21, branch `fix/lockstall-1121` (off `work/combo-1117` at `d067fe20`). Input: the play windows of the 16 on arms
s294-s309 (2-minute Isla Paradiso route storms; `ts3native-full.log` between `mem: p-base` and `mem: p-end`, GC brackets
left out), `[HitchAttr] RouteLockCallers=1` on in all of them; Ghidra headless `DecompileTo` and `llvm-objdump` on the
Steam 1.67 code. **No game run in this pass.**

### 27.1 What the arms had off [verified: the startup lines of each log]

All 16: `land check RO off`, `obstruction at point RO off`, `blocker check 2d RO off`, `[SteerRO] RetryWait=0`,
`ForeignKindsUnseen=0`, `DiscYieldForeign=0`, no `route skip retry:` line (`SkipRetry=0`), `waypoint snap off`.
`job yield mode 1` in all of them. So §25.2's list of built answers was again not in the test.

### 27.2 The frames [measured]

142 frames >= 150 ms: `lock918` 64, `job-wait` 33, `sim-cpu` 29, `mixed` 11, `sync-plan` 5. Logged waits (>= 20 ms):
`route lock wait:` 578 lines / 32.7 s, `route jobs: sim waited` 465 lines / 26.3 s. s294, between its two minute lines in
play: 891 jobs, 16.0 s of job time (27 % of the wall time), 717 pump waits on `+0x918`, 1.8 s.

**`+0x918` waits by key and caller** (the `callers` word; 16 arms summed; "frames" = `lock918` / `mixed` frames whose
largest key it is):

| key < caller | lines / ms / max | frames / lock ms | who | answer that exists | on in these arms |
|---|---|---|---|---|---|
| `0x62FB74` < `0x654BE5` (+ `0x62FB8B`) | 145 / 7,062 / 226 | 17 / 2,250 | `ArePointsConnectedByLand`'s closest-waypoint gather | `[LandCheckRO]` (§24) | no |
| `0x62EF7A` < `0x655ABE` | 98 / 5,088 / 224 | 7 / 991 | `RouteObstructionAtPoint` | `[ObstructAtRO]` (§22) | no |
| `0x85FE10` | 45 / 3,344 / 176 | 13 / 1,946 | S2's given-back steering queries | `[SteerRO] RetryWait` (§25) | no |
| `0x760BFF` < `0x76429D` (Plan / Replan), `0x764400` (PlanFromPoint) | 43 / 3,238 / 1,307 | 6 / 1,795 | a sync plan's goal step | §27.3 | no |
| `0x631814` < our L1 hook | 35 / 2,336 / 183 | 7 / 884 | `[LotFrontDoorRO]` give-backs | none; the reasons had no log line (§27.6) | - |
| `0x62FB74` < `0x834201` | 42 / 2,059 / 214 | 3 / 423 | writer path `0x8341D0` (§21.2) | none (writer) | - |
| `0xD882D7` | 31 / 1,463 / 106 | 2 / 207 | `[ConnRoomsRO]` give-backs (`lot not built`) | none | - |
| `0x62F782` < `0x76F15D`, `0x7581A6` | 38 / 2,162 / 159 | 4 / 600 | `PortalsForObject`; `0x7581A6` = `SetPortalCost` (writer), `0x76F15D` not read | none | - |
| `0x62EF7A` < `0x75167F` | 15 / 1,013 / 192 | 3 / 475 | gate 0 from `0x75167F` (inside the `IsPointObstructedBySim` IC body `0x751480`; `[ObstructedRO]`'s give-back [inferred]) | none | - |
| `0x85F6CB` | 18 / 1,032 | 2 / 228 | `[EditPathRO]` give-backs | - | - |
| `0x833697` | 19 / 801 / 88 | 1 / 59 | event 9's footprint insert `0x833680` | **`DeferObjInsert` (§27.4)** | new |
| `0x62EF7A` < `0x82E696` | 9 / 560 / 206 | 1 / 206 | `BlockerCheck2d` | `[Blocker2dRO]` (§24) | no |
| `0x631F40`, `0x83156D`, `0x846D21`, `0x62FB74` < a non-image caller, others | 40 / 2,498 | 9 / 1,156 | readers' give-backs | - | - |

So 16.1 of the 32.7 s (the first three rows and `0x82E696`) and 38 of the 75 frames have an answer that was built and
was off.

**Job waits by site** ("frames" = `job-wait` frames, by the frame's last wait line):

| site | lines / ms / max | frames / wait ms | what blocks | answer |
|---|---|---|---|---|
| `rtwait`, `lost-removal flush` | 283 / 15,933 / 458 | 18 / 3,092 | `UolFlushFor` before event 9's insert `0x833680` (§27.4) | **`DeferObjInsert`** (new) |
| `disc`, drain point `READY` | 81 / 4,531 / 423 | 5 / 1,084 | a forced drain (oldest record >= 250 ms) at a READY poll; 67 lines / 3,602 ms behind an own job | `DiscYieldForeign` (off) for the foreign 14; **`DiscYieldOwn`** (new, §27.5) |
| `runplan` | 44 / 3,294 / 340 | 7 / 1,284 | a sync plan waits for the running job before it plans | §27.3 |
| `rtwait`, `destroy` | 23 / 924 / 130 | 1 / 130 | `ObjectService_DestroyById` while a job runs, task cannot yield | none (D-X; `DestroyPark` is on) |
| `disc`, `fp2 reader` | 20 / 918 / 134 | 1 / 134 | the private-footprint ICs flush an object's queued record | none here |
| `disc`, `A10 START`, `START`, `CANCEL` | 14 / 679 / 227 | 1 / 227 | forced drains at the other poll points | `DiscYieldOwn` / `DiscYieldForeign` for START and A10 START; CANCEL keeps the wait |

### 27.3 Question 1: the sync plans [verified: logs + source]

`route phases: sync plan` lines (plans >= 40 ms), 16 arms:

| entry | lines / plan ms / lock ms | what it is | answer |
|---|---|---|---|
| `Route_PlanLocked (no IC)`, managed caller `Route_PlanFromPoint` 149, `Route_Replan` 7 | 157 / 8,662 / 46 | kind 2 / kind 1 calls from a task that is not the route's own: ts3fixes sends a foreign START (`RouteBackoff.cs`, the `kArForeign` bit), ts3native refuses it as `Foreign` with `ForeignKinds=4` (`route async: kind 2 call ... runs the game's IC with no job (Foreign)`, 45 such lines). The whole plan then runs on the sim thread: this is plan time, not lock wait | `[RouteAsync] ForeignKindsUnseen=2` (§24; kind 2 = PlanFromPoint) moves the unseen ones to the worker. Off in every arm |
| `IC Plan/Skipped`, `IC ReplanFromPosition/Skipped` | 25 / 2,013 / 492 | the job was queued; when the worker claimed it the world was not quiet (`ServeAsync`: `rtBusy`, pending lot updates, dirty lot index), `J_SKIPPED`; the task's READY poll then plans on the sim thread with no step-aside, usually behind the job that runs by then. Callers: `Lot.PlanToLotEx` 7, `PlanToPointRadialRange` 3, `RoutingComponent.DoRoute` 2, `ExitHouseboatThroughGangway` 2, boat, door and mailbox routes one each | `[RouteAsync] SkipRetry=1` (§20): parked again and retried on READY polls, unseen routes only. Off in every arm, never run |
| `IC Plan/NoStart`, `IC ReplanFromPosition/NoStart` | 12 / 691 / 634 | no START was sent: the route is not async-eligible and the task is not one a foreign START may come from (`PlanRouteTurnToFace` 4, `Sim.Wander` 2 of the lines with a script stack). Nearly all of its time is the lock wait | none: the plan needs the lock, and the step-aside did not act (the task may not yield, or a job started after the "not busy" answer) [inferred] |
| `IC Plan/Los` | 1 / 77 / 49 | `ForcePhysicsBasedRaycast` (`ViewObjects`) | none (§19.3) |

The largest single frame of the 16 arms is this class twice over (s309, 1,361 ms): a foreign kind 0 job ran 1,367 ms,
1,288 ms of it in 92 closest-waypoint searches (`route candidate split: ... closestWps 1288/92`); `KindDeadlines` cut it
at 535 ms and the cut took effect 831 ms later (`post-cut tail 831 ms`: the closest-waypoint search has no deadline
check); behind it a refused kind 2 START (`ufplansplit: kind 2 1314 ms, sync 1314 ms (START refused)`) waited 1,307 ms
at `0x760BFF` < `0x764400`. `ForeignKindsUnseen=2` takes the waiter off the sim thread; the search itself is docs/679's
subject (`[WaypointSnap]`, off in these arms).

### 27.4 Question 3, the largest job wait: `[Discourage] DeferObjInsert` (`discourage.cpp` `UofCall`, `UoiDefer`, `UoiVoidFor`)

**The wait [verified: source + decompile `0x833680`, `0x8321F0`, `0x829A60`, `0x82A720`, `0x946160`, `0x624620`, `0x829BC0`,
`0x945E40`; disassembly `0x946160..0x946330`].** An object that crosses a lot line fires event `0xA` (`0x946F70` ->
`UpdateObjectLot 0x82D280`, remove-only) and then event 9 (`0x946160` -> `0x833680`, the insert). With
`DeferFootprint2=1` and a job running the remove becomes a record (`fp2` 2). The insert that follows was never deferred:
`UofSite` flushes the object's records first, which is a wait for the running job (`RtWaitJob`, the `lost-removal flush`
site), and without a queued removal the game's call waits at its own `Lock_Acquire` (key `0x833697`). So the deferral of
the remove moved the wait by one call and did not remove it. The task's `could yield` word does not help: the call is
inside the game's native object code.

`0x833680` (thiscall world; obj; `ret 4`; void): `Lock_Acquire(world+0x918)`, the object's lot from its lot component
(`0x935770`) or its `+0x48 / +0x4C`, then the footprint core `0x8321F0(obj, 1, lot, 0, 0)`, release. The core with "remove
first" 0 only inserts: after its early outs (`world+0x974`, `0x829A60(obj, 1, 0)`) it builds one `Routing/ObjectFootprint`
per level offset and inserts it (`0x6376D0`), and for an object with a footprint polygon replaces its private footprint
key (`0x635700`, a new `Routing/PrivateFootprint`). Its two callers are in `0x946160`, which ignores it and returns 1:

- `0x94630A`: the plain call (component flag 2 clear, or `world+0x97E` 0, or `0x945E40` false);
- `0x9462D4`: only with flag 2, `world+0x97E` and `0x945E40` (`+0x974`, `+0x975`, `+0x976`: the world's batch mode), for an
  object whose key is not `StairsHelper` / `PoolHelper` / `PlatformHelper`, inside a bracket: `world+0x978 = 1`, planner
  `+0x10D = 1` (`0x624620` on `0x829BC0`'s planner), `0x82D280` at `0x9462C9`, then the insert, then both bytes restored.

**The switch.** `DeferObjInsert=1`: on the sim thread, at the plain site, while a job runs, the insert becomes a record
(`fp2` 3) at the tail of D's FIFO and the call returns. The record is applied by the game's own `0x833680` at the next
drain point (the first poll with no job, or the forced drain once the oldest record is 250 ms old), under `+0x918`, with
the object looked up by id as every other record (gone or a reused id: no footprint, vanilla's footprint would have left
with the object).

Rules:

- **Not deferred, today's call:** no job running; `+0x918` owned by this thread; inside a job callback; during a drain;
  another world; closing; an object with id 0; the bracketed site; the world's batch mode (`+0x97E` and `+0x976`, kind
  1's rule: there `+0x978` decides the early outs); queue full.
- **One insert an object.** A new insert of the object, queued or run at once, marks its queued inserts replaced
  (`allow = 1`, counted, not applied). Without this a replay could insert twice: insert (lot B), remove (lot B), insert
  (lot C) applied at one time would put the first insert on lot C too, since the call reads the object's lot at the
  apply.
- **Order.** The record sits behind the object's queued removals, so vanilla's remove-then-insert order holds without the
  flush. Later calls for the object queue behind it (the order rule of the design U and phase 2 hooks) or drain first
  (the private footprint sites through `Fp2FlushFor`, D's own Add / Remove); the destroy gate (`DiscApplyFor`, D-X)
  applies the object's removals and drops the insert.
- `DeferObjInsert=2` counts only (`would defer`, and the time of today's flush and call for calls made in a job).

**Not identical to the game.** The object's routing footprint appears where the object is at the drain, up to a job (at
most 250 ms plus the forced drain) later. The job that runs meanwhile is not affected: today the sim thread waits for its
end before it inserts. Sim-thread readers in that window (placement tests, obstacle queries, the read-only clones) and a
job that starts before the drain see the object's old footprint state: with a queued removal that is the footprint on
the old lot, which is already the case today for the removal. An insert with no queued removal (the `0x833697` waits)
leaves the object without a routing footprint on the new position for that window.

**Not verified:** what fires event 9 at the plain site in a storm (lot crossings of Sims, boats and cars are the
expected source [inferred]; `calls` against `in a job` in the first arm says how often); whether `0x829A60`'s early out
could answer most calls before the lock; batch mode on at the apply (`applied in batch mode`, expected 0: the game's
call may then skip the insert).

Counters: exported block `ts3native_objins` (docs/registry.md; runtime switch `native_set objins`). Line, once a
minute: `object footprint insert: mode M calls N (in a job J): deferred D, same insert queued Q, applied A, replaced by
a later insert S, object gone G, dropped at its destroy X, oldest T ms, applied in batch mode B; not deferred: pass P,
batch mode or bracketed site C, queue full F; count only: would defer W; not-deferred calls in a job: K, U ms in the
flush and the game's call`.

### 27.5 `[RouteAsync] DiscYieldOwn` (`discourage.cpp` `DiscYieldOk`)

docs/640's `DiscYieldForeign` skips a forced drain at START / A10 START / READY when the job that would be waited out
is a foreign one. In these arms 67 of the 81 READY disc waits (3,602 of 4,531 ms) are behind an own job, which that
switch refuses (`g_jobOwn`). docs/640 was written for the 300 ms boat replans and names no safety reason for the limit: the park at
START (`J_PEND`, `dy`), `DyReadyMay` at READY and the 1 s ceiling do not read the kind of the running job.
`DiscYieldOwn=1` lets the same skip through for an own job; every other rule is `DiscYieldForeign`'s (caller known unseen,
its task at its root, the oldest record under 1 s, at READY only for the caller's running or done job or a START this
switch parked). The two switches are independent.

What it does not reach: a READY poll whose own job is still queued behind the running one keeps the wait (the drain is
what stops the worker from claiming that job past the old records). The totals line counts those polls: `disc yield:
... ; own mode M: N of the yields behind an own job, READY kept for a queued job K`. If K carries most of the READY
waits, the next step is a hold on the worker's next claim while a drain is owed, not a wider skip.

### 27.6 Question 2: the worker's hold

The answer of §7.4 and docs/673 §4 stands, and these arms do not change it: the job holds `+0x918` for the whole of the
game's `Route_PlanLocked` (s294: 27 % of the wall time in jobs of 18 ms on average), and a release between candidates
or phases is not provable (parked frames keep lot rows, footprint and quadtree pointers; an admitted reader may flush or
rebuild). What the data adds is that most of what waits there is already answered without the lock by a clone that was
off (§27.2: 16.1 of 32.7 s), and that the sync plans at `0x760BFF` are refusals with a built answer (§27.3). A sync
plan cannot be served "without the lock": it is the same `Route_PlanLocked`.

Two log lines added, both always on, no switch:

- `lost removal flush: flushes N, at the bracketed site A (.. ms), at the plain site B (.. ms), task could yield Y (..
  ms), order misses M` (the v192 `lrs*` counters, which had no line);
- `lot front door RO: mode M shadow S calls N answered in a job T (max .. us); given back: state, nested QL, no world
  manager / active lot, QL busy, shape, QL frames lost` (`ltFb`, which had no line): which reason carries key
  `0x631814` (35 lines / 2.3 s here) decides whether a retry as `[SteerRO] Retry` applies.

### 27.7 Checks run (2026-11-21, no game)

- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: no error, the
  warning counts of the base commit (174 / 176 / 221).
- Selftest, written, **run only by the MSVC CI build**: a block in `TestFp2LostRemoval` (off; a lot move in a job with
  no wait and vanilla's map after the drain; the same insert twice; two lot moves in one job; an insert that runs at
  once replaces the queued one; object gone; the destroy gate; the bracketed site, batch mode, `+0x918` held, count
  only; the block's layout) and one in `TestDiscYield` (own job off / on, the household rule, independence from
  `DiscYieldForeign`, the queued-job count).
- `node tools/selftest-all.ts --ci`: pass.

### 27.8 Next storm, on arm

First the keys that exist (§27.1), then the two new ones, one at a time against the same seed:

| arm | `ts3native.ini` | read |
|---|---|---|
| A | `[LandCheckRO] Enabled=1`, `[ObstructAtRO] Enabled=1`, `[Blocker2dRO] Enabled=1` (each `Mode=1 Shadow=0`), `[SteerRO] RetryWait=1` | keys `0062FB74` < `00654BE5`, `0062EF7A` < `00655ABE` / `0082E696`, `0085FE10` against 7.1 / 5.1 / 0.6 / 3.3 s per 16 arms |
| B | A + `[RouteAsync] ForeignKindsUnseen=2`, `SkipRetry=1` | `route phases: sync plan ... Route_PlanLocked (no IC)` and `.../Skipped` lines, key `00760BFF`, `runplan` waits; `route skip retry:` (queued near parked, gave up small) |
| C | B + `[Discourage] DeferObjInsert=2` | `object footprint insert:` `would defer` against `calls`, and its `ms in the flush and the game's call` against the `lost removal flush:` line |
| D | B + `[Discourage] DeferObjInsert=1` | no `rtwait ... site lost-removal flush` line, key `00833697` gone, `deferred` = `applied` + `replaced` + `gone` + `dropped at its destroy` at a quiet point, `applied in batch mode 0`; `disc` waits must not rise by what `rtwait` fell (moved waits); no Sim walking through a parked car or boat, no stuck Sim |
| E | D + `[RouteAsync] DiscYieldOwn=1`, `DiscYieldForeign=1` | `disc ... drain point READY` lines, `disc yield:` totals (`parked capped` near 0, `READY kept for a queued job`) |
