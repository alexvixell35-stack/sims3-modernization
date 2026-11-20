# 670. The +0x918 waits and the unseen sync plans left after s143

2026-11-20 (UTC). Inputs: the Isla Paradiso route storms s142 (profiling) and s143 (fixes off / on), 4 min each, their
`ts3native-full.log` and `route-stutter-report.ts` tables; Ghidra headless (DecompileTo, CallerTree, XrefsTo) and capstone
on the Steam 1.67 code; our source. Built: `[PlaceTestRO]` (ts3native, default off) and a log line for `KindsUnseen`.
**Nothing here has run in the game.** Branch `feat/lock918-syncplan`.

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
