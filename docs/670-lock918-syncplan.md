# 670. The +0x918 waits and the unseen sync plans left after s143

2026-11-20 (UTC). Inputs: the Isla Paradiso route storms s142 (profiling) and s143 (fixes off / on), 4 min each, their
`ts3native-full.log` and `route-stutter-report.ts` tables; Ghidra headless (DecompileTo, CallerTree, XrefsTo) and capstone
on the Steam 1.67 code; our source. Built: `[PlaceTestRO]` (ts3native, default off) and a log line for `KindsUnseen`.
**Nothing here has run in the game.** Branch `feat/lock918-syncplan`.

Added 2026-11-20, later the same day (§7-§10, branch `feat/lock918-rest`): the s146-s148 read, why the wait stays after
each site fix, the lock-level options, `[EditPathRO]` (built, NOT RUN) and the waiters left. §5 and §6 are the state before it.

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

### 9.1 `0x75A64C`, GoalPoint_Validate with footprints [verified decompile; s148-on counters]

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
