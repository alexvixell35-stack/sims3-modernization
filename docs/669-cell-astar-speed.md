# 669: Cell A* speed: the s140 perf ranking, three hot functions read, and an exact side index for the cell table

> **Code status (2026-11-23):** `[RouteSpeed] CellIndex`, `ClassMemo` and `OpenHint` (the `cellindex*` / `cellspeed*` files named below) are not on main; they are posted separately, default off (docs/680: CellIndex correct, no gain; ClassMemo / OpenHint not run).

2026-11-19. Sources: the two s140 profiling runs (docs/666 §3: Linux `perf` on the game's Simulator thread, 499 Hz,
`<runs>/phase-20261119-2238` Isla Paradiso and `phase-20261119-2310` Bridgeport), Ghidra decompiles and `objdump`
disassembly of TS3W 1.67, CallerTree. Branch `feat/cell-astar-speed`. Built, default off, **NOT RUN in the game**.

Goal: less CPU per in-room cell search with every result identical to the game's. Pure function-result caches, so by the
household rule they may serve every router; served answers still go to NPC routers only until a shadow run shows 0
mismatches (as docs/663).

## 1. Method and its limits

`perf script -F comm,tid,time,ip,dso` over `sim.perf.data`; TS3W addresses named from the local Ghidra function export
and `tools/ghidra/symbols/*.csv`, as `route-stutter-report.ts` does. Perf time is the logs' `at` ms (docs/666).

- **The call chains are not usable.** The routing code is built without frame pointers, so perf's frame-pointer walk
  ends after one or two frames: `PathPlanner_PlanHierarchical` is on 54 of 463,671 sim stacks and the cell A* `0x61E500`
  on 17 [measured]. So "inside routing" is by address: the leaf is in `0x618000-0x680000`. All numbers below are **self
  time**; groups follow the static call graph. An inclusive ranking needs LBR or DWARF unwinding, which this run has not.
- That range also holds the terrain ray code (`RayTriangle_Int16 0x67E2B0`, `TerrainRay_*`), which line-of-sight and
  height queries use too, and footprint updates, which are not searches.
- Only the sim thread (and main) were sampled. The route worker's plans are not in the data.
- Hitch windows: the 11 game hitches of the Isla Paradiso log (4.9 s), each `at - ms - 20 .. at + 10`. Bridgeport had 0
  game hitches in the profiled window, so it gives whole-run numbers only.

## 2. Ranking [measured]

**Share of sim-thread CPU spent in routing code**

| run | sim samples | on CPU | routing code | share |
|---|---|---|---|---|
| Isla Paradiso, whole run | 463,671 | 929 s | 21,787 | 4.7% |
| Isla Paradiso, the 11 hitch windows | 1,826 | 3.7 s | 1,111 | 60.8% |
| Bridgeport, whole run | 602,354 | 1,207 s | 19,231 | 3.2% |

Over a whole run the sim thread is the script interpreter (`ves_exec_method_with_context` 37-38%), the GC and the
allocator; routing is small. Inside hitches it is most of the CPU time.

**Routing self time by group (% of routing samples)**

| group | IP whole | IP hitches | BP whole |
|---|---|---|---|
| footprint room ids / polygon clip (`0x67C070` family, §3.1) | 24.0 | 22.6 | 21.7 |
| terrain rays | 23.0 | 1.1 | 20.0 |
| cell A*: neighbour expansion (`0x66BFD0`, `0x66B9C0`, climb / mirror / collect, cell positions) | 14.4 | 20.8 | 16.7 |
| cell A*: QuadLeaf class tests (`0x667DB0`, `0x6681E0`, `0x66B090`, footprint slots) | 4.9 | 13.7 | 4.3 |
| cell A*: open list, heap, node state | 4.1 | 12.5 | 3.7 |
| cell A*: table lookup (`0x664370`, wrappers, create, clear) | 3.9 | 11.6 | 4.6 |
| cell A*: edge cost (`0x66C0C0`, `0x66BCE0`) | 2.5 | 9.7 | 2.9 |
| world A* / water | 8.6 | 0.8 | 5.3 |
| other | 14.6 | 7.2 | 20.9 |
| **cell A\* together** | 29.8 | **68.3** | 32.2 |

The helpers `QuadTreeCellPos_B 0x66A040` and `0x61B7C0` serve both the neighbour walk and the polygon walk; they are
counted under neighbour expansion, which overstates it in the whole-run columns.

**The cell A\* itself (hitch windows, 759 samples = 41.6% of all sim CPU in hitches)**

| part | share of cell A* |
|---|---|
| neighbour expansion | 30.4% |
| QuadLeaf class tests | 20.0% |
| open list / heap | 18.3% |
| table lookup (`AStarCellTable_Find` alone: 14.4%) | 17.0% |
| edge cost | 14.2% |

**Top self functions, Isla Paradiso** (% of routing samples; whole run / hitch windows):
`RayTriangle_Int16` 17.9 / 0.5, `0x67C070` 7.0 / 11.0, `0x61B7C0` 5.6 / 3.5, `QuadTreeCellPos_B` 4.7 / 3.6,
`LotQuadTree_CollectMasked 0x669230` 4.2 / 4.1, `0x66A760` 4.0 / -, `QuadLeaf_ClassFromMaskedEntries` 2.6 / 8.3,
`AStarCellTable_Find` 1.9 / 9.8, `AStarBucket_InsertSorted 0x61B880` 1.9 / 6.9, `AStarNodeTable_Clear 0x61C020` 1.7 / 1.4,
`QuadCell_ExpandNeighbours` 0.3 / 4.0, `QuadCell_EdgeCost` 0.9 / 3.8.

Per hitch: the 1.0 s hitch is `0x67C070` (122 of 486 samples) with `LotQuadTree_CollectMasked`, `0x628F80` and the
lot footprint code (`Lot_GetObjectFootprintCutoutBounds`, `FootprintQuadtree_*`); the five 0.3-0.5 s hitches at
254,030-254,037 s are cell A* (80-89% routing; `AStarCellTable_Find` 12-18%, `QuadLeaf_ClassFromMaskedEntries` 8-15%,
`AStarBucket_InsertSorted` 10-12%); two later ones are script work (`ves_exec_method_with_context`,
`Query_GetObjectsOnLot_impl`), not routing.

## 3. The three functions

### 3.1 `0x67C070`: polygon clip against a quadtree cell [verified, decompile]

`thiscall` on a scratch object of two point vectors (the global `0x1158580`, docs/101 §2), arguments (polygon, min corner,
max corner, optional out vector). It is a Sutherland-Hodgman clip of a 2D polygon against an axis-aligned rectangle: four
passes (x >= min.x, x <= max.x, z >= min.z, z <= max.z), each copying the kept points and the edge crossings from one
vector to the other, then `0x67BF10` (a clean-up pass) until nothing changes. It returns whether at least 3 points remain,
i.e. **whether the polygon overlaps the rectangle**.

Callers: `0x67C6A0` (out = 0) from `QuadTreeCell_ClipScratch 0x66AB70`, which builds the rectangle of a quadtree cell;
that from the quadtree polygon walk `0x6641B0` (query wrapper `0x667560`), from `0x634930` and `0x6351A0`, from
`LotRouting_FootprintRoomIds 0x635440`: **the rooms an object footprint touches**. That runs for every footprint that
`LotRouting_RemoveObjectFootprints 0x635700`, `PathPlanner_AddFootprint 0x6376D0` or `LotGraph_Build 0x63CB90` handles
[verified CallerTree]. (A second user, `0x67C6C0`, is called from `0x67CBF0` and `0x67D920`.)

**Why 1 s:** each footprint polygon is clipped against every quadtree cell its walk visits, and a lot rebuild or a batch
of deferred footprint updates (docs/101 H13: applied when a plan unpins the lot quadtrees) does that for every object of
the lot in one go. The hitch's other leaves are that same pass (`LotQuadTree_CollectMasked`, the lot's footprint
quadtree) [inferred from the leaf set: the perf stacks do not reach the caller]. It is a write path, not a search, so no
result cache applies here; a cut needs the caller named first (stall watch `StackKB=256 Names=1`, docs/666 §5). The family
is 22-24% of routing self time in both worlds, so it is the next target after the cell A*.

### 3.2 `AStarCellTable_Find 0x664370`: the cell table [verified, decompile + disassembly]

- **Structure.** A chained hash table inside Q: 10007 bucket heads at `Q+0x28` (fixed size, never resized), an end slot
  `Q+0x9C84` (`0xFFFFFFFF`), the element count `Q+0x9C88`. A cell object (0x3C bytes) carries the chain link at `+0x2C`
  (to the next cell's `+0x2C`) and its two key words at `+0x30 / +0x34`.
- **Lookup.** `bucket = (k0 xor k1) mod 10007` (one `div`), then a walk comparing both key words. O(chain length), one
  cache line per cell visited. Result: `{cell, &bucket}` or `{end value, &end slot}`.
- **Key** (`QuadCell_Key 0x669FF0`): `k0 = ix | depth << 12 | iz << 16`, `k1 = shape | level << 8 | tree id << 12`.
- **The weak spot.** The xor puts the shape byte (the sub-cell of a leaf, docs/98 §4) and the level
  nibble on the same bits as the x index. Two sub-cells collide whenever `ix xor shape` is equal, so the sub-cells of
  neighbouring leaves in a row pile into the same few buckets however empty the table is [verified from the key
  layout; how many shapes a real lot uses is not measured]. Example: with x indices below 64 and shapes below 128 a
  whole row has 128 buckets to share.
- **Load.** The table grows from the plan's Q reset to the next one (cells leave only in the clear `0x61C020`; the count
  word is written by the insert `0x61BC66`, the constructor and the clear only). The real count and chain length are
  **not measured yet**. Evidence for long chains [inferred]: 352 of the function's 417 samples sit on the compare after
  the cell's key load (`0x664399`), against 4 on the instruction after the `div` and 19 after the bucket load. One key
  load a call could not outweigh a 32-bit divide 88 to 1; several loads of cold cell objects a call can.
- So: **not O(n) in the table size, but a multi-miss chain walk caused by the hash**, which a better-hashed side lookup
  with inline keys removes. §4 builds it and its counters measure the chain (`ciSteps / ciWalks`, `ciCountMax`).

### 3.3 `QuadLeaf_ClassFromMaskedEntries 0x667DB0`: the class of a leaf sub-cell [verified, decompile; docs/98 §5]

`thiscall` on a quadtree leaf, arguments (tree, context X, shape, out class, out multiplier). It scans the leaf's
footprint entries whose 64-bit mask has the shape's bit, and for each one makes virtual calls on the footprint object
(type `+0xC`, disabled `+0x28`, blocks-agent `+0x60`, class / multiplier `+0x8 / +0x4` for the agent, object id `+0x84`),
checks the agent's ignore list and options, and folds the results.

**Is it recomputed for the same leaf and mask?** Yes, in three ways:
1. Its only cache is one result per **cell object** (`QuadCell_Class 0x66B600` / `QuadCell_Multiplier 0x66B670`: class
   byte `+0x20`, multiplier `+0x10`, keyed by the 24-bit context flags `X+0x50` stored in `+0x1C`). Cell objects die at
   every plan's Q reset, so every plan recomputes every cell it touches.
2. A call with other flags overwrites the key: a point check (`LotPlan_PointCellCheck`, flags `0x10000`) or a retry
   search (`0x80000`) between two searches makes the next search recompute.
3. **Class 1 cells clear their key** (`0x66B59C`), so a cell blocked by a Sim or a dynamic footprint is recomputed on
   every use, inside one search too. In a crowd that is the common blocked cell, which fits its 8-15% in the hitches.

**Not memoised here.** A memo per (leaf, shape, tree, agent, flags) is exact only if every footprint virtual it calls is
a pure function of state that cannot change inside the memo's life. EA itself refuses to cache class 1, the footprint
classes are many (docs/98 §5.1), and portal lock refresh
(`InRoom_RefreshPortalLocks 0x632460`) runs inside plans. That proof is not done, so no serve path was built. Next step:
a shadow-only probe on `0x667DB0` (ret 0x14) that counts repeats and compares, then the audit of the footprint vtables
(docs/98 §5.1 lists the class / multiplier slots).

## 4. `[RouteSpeed] CellIndex` (built)

### 4.1 What it does

An entry detour on `0x664370` (8 prologue bytes `83 EC 08 56 8B 74 24 14`, no relative operand; `__fastcall` hook with the
same stack clean-up, returns `out` in `eax` as the game does). Per planning thread it keeps a 2-way set cache
`(table, k0, k1) -> (cell, bucket address)`, 8,192 sets, with its own mixed hash over both key words and the table.

- A lookup whose key is in the index and whose kept cell still carries both key words is **served**: `out = {cell,
  bucket}`. One set (48 bytes) and the cell itself are touched; no divide, no chain.
- Anything else runs the game's function. A found answer is then kept; a full set drops one entry.
- **"Not found" is never kept**: the caller creates the cell next, so a negative answer is stale at once. Such lookups
  cost what they cost today.

### 4.2 Why it is identical

- Only the game's own answers are kept, so the first answer for every key is the game's.
- **A kept answer cannot change before the next clear.** The table only grows between clears (§3.2: no code but the
  insert, the constructor and the clear writes the count or the buckets), keys are unique (both creators, `0x66B819` and
  `0x664613`, follow a failed find of the same key), so the chain holds the same single cell for that key, and its bucket
  address is a function of the key.
- **Invalidation:** the clear sequence `g_nmClearSeq`, bumped by the stub on `0x61C020` that NeighbourMemo and FloodMemo
  already share (`RsInstallClear`). Both callers of the clear (the Q reset `0x61E320`, the destructor `0x61E420`) go
  through it. Any thread's clear resets every thread's index. Quadtree writes do not matter: the answer depends on the
  table only, so `[RouteSnap]` is not needed.
- **Belt and braces:** the kept cell's key words are compared before a serve; a difference counts `ciStale` (must stay
  0), drops the entry and runs the game's function.
- **What the caller sees:** the same two out words and the same `eax`. The game's function leaves other values in
  `ecx / edx`; both callers (`0x664590`, `0x6645D0`) reload from their stack and read neither [verified disassembly].
- Search order, tie-breaking, heuristics, the open list and reference counts are untouched: Find takes no reference.
- **Threads:** one index per thread (`__declspec(thread)`, at most 4; a fifth thread runs the game's function). The
  table itself is only used under the game's own routing lock.

### 4.3 Switches, counters, logs

**ini** `[RouteSpeed]`: `CellIndex` 0 off (default), 1 serve, 2 shadow (the game's function always runs; a kept answer
that differs counts `ciMismatch` and logs `CELL INDEX MISMATCH`, 10 lines). `CellIndexAll` (default 0): 1 = every router;
0 = unseen and seen-NPC routers only (`PccScopeNow` 2 or 3). `[Experimental] HouseholdToo=1` also lifts it.

**Runtime switch:** export `ts3native_cellindex` +0x00 (`native_set cellindex`).

**Counters** (the export; no ts3fixes slot): +0x00 mode, +0x04 ready bits (1 find hook, 2 clear hook), +0x08 lookups,
+0x0C hits, +0x10 stores, +0x14 resets, +0x18 not found, +0x1C stale (must stay 0), +0x20 scope, +0x24 evicted,
+0x28 shadow checks, +0x2C mismatch (must stay 0), +0x30 no index, +0x34 / +0x38 / +0x3C sampled chain walks / cells
visited / longest, +0x40 most cells in one table, +0x44 / +0x48 sampled time of the game's find (us, n), +0x4C log lines.

**The measurement of §3.2:** every 64th call of the game's function is timed and its chain walked once more. The 60 s
line `route speed: cell index ...` prints `the game's chain N.N cells a walk (max M)`, `most cells in a table C (10007
buckets)` and `saved ~N ms` (hits times the sampled find time: an upper estimate, it ignores the index's own cost).

**Memory:** 393 KB per planning thread. **EA 1.69:** `0x664370` has no 1.69 address yet (a 0 row); the feature logs SKIP.

### 4.4 Expected saving [inferred, to be measured]

`AStarCellTable_Find` is 14.4% of cell A* self time in the hitch windows, and the cell A* is 41.6% of sim CPU there.
Most lookups are repeats (each cell is looked up once per neighbour that expands, in every search of the plan). If the
measured chain is several cells a walk, a served lookup costs about one cell miss instead of the chain: **about 10% of
cell A* time, 4% of hitch CPU**. If the chain turns out near 1, the lookup's cost is the one miss of the right cell,
which the caller pays anyway, and the index gives nothing: the shadow line decides before any serve run.
With NeighbourMemo on, repeat expansions skip Find altogether; the index then only helps first expansions.

## 5. Tests and build

- **Selftest `cellindex-test.h` (`TestCellIndex`):** the sets (keys, table, eviction, drop, generation wrap, the clear
  sequence, 4,096 keys of the game's colliding shape); `CiFindHook` on fake tables and cells with a fake game function
  written from the layout: a served answer equals the game's cell, bucket address and return value; three cells in one
  bucket; the same key in two tables; "not found" runs the game's function every time and the later insert is found; a
  changed key is not served; a clear drops the index; household scope runs the game's function; shadow counts a mismatch;
  the sampled walk and count; `CiChainSteps`.
- **Linux host test `cellindex-host-test.cpp`** (clang `-m32`, freestanding, no libc): the same selftest header as it
  is, the NPC / household scope rule, and a model check (240,000 random find / insert / clear steps on two tables built
  as the game's: every answer the index would serve equals the model's). **Passes (exit 0)**; four mutants of the hook
  (wrong bucket, no clear sync, no key check, no scope check) each fail it.
- **clang-cl `/Zs /W4`:** `ts3native.cpp` for Steam and `/DTS3A_TABLE`, and `selftest.cpp`: 0 errors, the same warning
  counts as before the change (174 / 176 / 221). The game build must come from MSVC CI (clang's SEH limit).
- `node tools/hook-sites.ts --check`, `fix-repro-check.ts`, `proven-fixes.ts --self-check`: pass.
- **Repro `ip-cellindex-crowd`** (Isla Paradiso, UNVERIFIED): job ini `CellIndex=2` and `CandidateSplit=1`; off / on / on
  / off by `native_set cellindex`. Run it in shadow first: `ciMismatch` 0, `ciStale` 0, and read the chain length.

## 6. Prior art (credited)

- Open addressing with inline keys against chained buckets: Knuth, TAOCP vol. 3 §6.4; set-associative caches with
  replacement as a lookaside in front of a slower exact structure: the TLB design (Hennessy and Patterson, Computer
  Architecture, appendix B). The index is that: a lookaside that may forget, never invent.
- Multiplicative mixing constants: the golden-ratio constant of Knuth's multiplicative hashing and the finalizer
  constants of MurmurHash3 (Austin Appleby, public domain), as `routespeed-core.h` already uses.
- Sutherland and Hodgman, "Reentrant Polygon Clipping", CACM 1974: the algorithm `0x67C070` implements.
- TS3 modding: no public project changes the native pathfinder's tables (docs/663 §6).

## 7. Files

- New: `native/ts3native/cellindex-core.h`, `cellindex.cpp`, `cellindex-test.h`, `cellindex-host-test.cpp`.
- `ts3native.cpp` (include, install after FloodMemo, the 60 s line), `selftest.cpp`, `ts3native.ini` (`[RouteSpeed]`),
  `ts3addr-ea169.h` (one unresolved row), `tools/ini-sections.json`, `tools/harness/native-switches.ts`,
  `tools/harness/repros.json`, `docs/68`, `docs/registry.md`, `docs/index.md`.
- Not touched: `routespeed.cpp`, `floodmemo.cpp`, `nopathmemo.cpp`, `hitchsum.cpp`.

## 8. `[RouteSpeed] ClassMemo` (2026-11-20, branch `speed/cell-astar2`, built, default off, NOT RUN)

Input: s159's storm line `route speed: flood exits: found 3018 (2818 ms, 1261162 pops)`: 418 pops and 0.93 ms a found
search, 2.2 us a pop; split totals `cell 3123 ms / 3011 calls` (s159), `4005 / 4670` (s172) [measured, logs].

### 8.1 The function [verified decompile + disassembly]

`QuadCell_Recompute 0x66B090`: thiscall on a cell position, one argument X, `ret 4`. Callers: only the two cached getters
`QuadCell_Class 0x66B600` and `QuadCell_Multiplier 0x66B670` (4 call sites), which call it when the cell's key
(`cell+0x1C >> 8`) is not `X+0x50`. It runs one of the three leaf scans (`0x667DB0` sub-cell, `0x6681E0` whole leaf,
`0x668C40` internal node), then the context adjustment of docs/98 §5.3. Its own stores are `cell+0x20` (class),
`cell+0x10` (multiplier), `cell+0x1C &= 0xFF` (key clear: class 1, or the agent's own object in the clearance test) and
`X+0x4C`; nothing else.

So §3.3's three cases are one thing: the getter's key misses. Per plan every cell is computed once (cells die at the
Q reset); a class 1 cell is computed on **every** getter call, and the edge cost `0x66C0C0` / `0x66BCE0` calls the getters
for both cells of every edge.

### 8.2 Purity audit [verified, capstone walk of every call target]

Every function reachable from the three leaf scans and from `0x669530` (the blocking-type count of a penalised cell) was
walked for stores to non-stack memory and for indirect calls:

- The scans store only through their two out pointers.
- Footprint virtuals they call, in all 11 footprint vtables (`0xFEBBC0`, `0xFEBA98`, `0xFEB848`, `0xFEB970`, `0xFEBE38`,
  `0xFEBD10`, `0xFEB5F8`, `0xFEB720`, `0xFEAE08`, bases `0x10205B8`, `0x1020AE8`): `+0x00` count, `+0x04` multiplier,
  `+0x08` class, `+0x0C` type, `+0x28` ignore, `+0x60` applies, `+0x7C` / `+0x84` object id: **0 stores** in each target
  and its callees (`0x8292D0`, `0x8294E0`, `0x81C880`, `0x81C970`, `0x81CEF0`, `0x946E70`, `0x946E90`). `+0x90` / `+0x94`
  (`0x915880` / `0x915890`) add and release a reference around `0x669530`'s temporary vector: net zero.
- The one impure slot is `+0x34` (rectangle overlap, `0x80F0D0`: 81 stores, the lazy polygon cache and the allocator). The
  recompute calls it only in the small-cell clearance test (flag `0x200000`, a sub-cell with a parent). **Those calls are
  never looked at by the memo.**
- What they read of the agent: the options object `O = X+0x40` (`+0x00` kind word, `+0x08 / +0x0C` own id) and, when
  the global `0x11D9128` is set, `D = [O+0x28]`: `+0x08 / +0x0C / +0x18` the sorted held-id vector, `+0x20 / +0x24`,
  the option words `+0x28..+0x34`, the ignore list `+0x38 / +0x3C`.

### 8.3 The memo

Per planning thread, a 2-way set cache (4,096 entries). Key: tree (`cell+0x04`), node (`cell+0x0C`), shape, `X+0x50`,
`X+0x4C` on entry, and the agent's record number. An agent record is O's first 0x38 bytes, D's first 0x40 bytes and
both id lists element by element (up to 16 ids each, 8 agents; anything larger runs the game's function). The record is
compared against memory on every call, so a changed word or id is another agent: no hash stands in for content.
Value: class byte, multiplier bits, `X+0x4C` after the call, whether the key was cleared. A served call writes those four.

Life: dropped as a whole when the Q table clear sequence, `[RouteSnap]`'s write sequence or the thread's managed-call
sequence moves (as FloodMemo, docs/667). A result is kept only if none moved while the function ran.
The write sequence stood at 427,061 at the end of s172's session and docs/676's probe found "no routing write since" for
1 of 183 repeat world searches, so **nothing lives across plans**: a cross-plan class memo was not built. What is left is
the repeat inside one stretch of one plan: class 1 cells and flag changes (the point check's `0x10000`, the retry's
`0x80000`).

Not looked at: no node (tree default, two loads), no context, key 0, the clearance case.

### 8.4 What is inferred

- That every writer of a footprint field these slots read is a `[RouteSnap]` writer row or IC [inferred; FloodMemo rests
  on the same]. The shadow mode tests it: every kept result is compared with the game's (`kmChecks`, `kmMismatch`).
- The gain. Class tests are 20% of cell A* self time in the s140 hitch windows (§2); the share of them that are repeats
  is **not measured**. The line's `uncached by the game` over `calls` gives it; `the game's recompute N us` times `hits`
  is the saving. If repeats are a small share, leave it off.

Keys: `ClassMemo` 0 / 1 serve / 2 shadow, `ClassMemoAll` (0 = NPC routers only). Runtime: `native_set classmemo`.
Needs `[RouteSnap]` counting and `[RouteAsync] Enabled=1`. Line: `route speed: class memo ...`. 170 KB a thread.

## 9. `[RouteSpeed] OpenHint` (built, default off, NOT RUN)

### 9.1 Why not a binary heap

- The pop is inline: in `0x61E500` (`0x61E689..0x61E6C3`: head of bucket `min`, unlink, `0x61BAF0` when the bucket
  empties) and `0x61BAF0` has four more callers (`0x61E8A0`, `0x626A50`, `0x63E340`, `0x63EC40`). Another structure
  means patching every one of them mid-function.
- The order could be kept: the game's order on ties is a total order, key `(f, s)` with `s` falling for every insert
  (`0x61B880`: before equals) and rising for every in-bucket move (`0x61B910`: behind equals) [derived from docs/98 §8.3,
  not built].
- The cost is the walk, not the structure: an insert walks from the bucket head to its place. Buckets are 1.0 wide and
  the open set of a room search spans a few units of f, so a bucket can hold many nodes. `0x61B880` is 6.9% of the
  routing samples in the hitch windows, about 10% of the cell A* [measured, §2]. The walk length itself is **not
  measured**.

### 9.2 What it does

Entry detour on `0x61B880` (9 bytes `8B 44 24 04 F3 0F 10 40 24`; thiscall bucket, one argument, `ret 4`; callers
`0x61DF99` insert and `0x61E01A` update into another bucket, both after writing `node+0x34`).

Per thread, 256 slots by bucket index, each with 32 sub-ranges of the bucket's f interval; a sub-range remembers the
lowest-f node inserted into it. For a new node the hook tries the remembered nodes of its sub-range and below (at most
3) and takes the first that passes the proof:

1. the node carries this search's stamp, is open and has this bucket's index: it is a member of this list (a node leaves
   a list by the pop, which closes it, or by a bucket change, which rewrites `+0x34`);
2. its links agree with the list (it is the head, or its predecessor's `next` is it);
3. it is the head, or its predecessor's f is **below** the new f.

The list is sorted, so with 3 every node before the start is below the new f; the game's walk from the head would pass
them all and arrive at the start. From there the hook runs the game's walk and links with the game's three cases. The
result is the same list. Ties: the walk stops at the first node with f >= new f, as the game's; a start whose predecessor
ties with the new f is refused.

The game's function runs for: buckets with fewer than 8 inserts in this search, no remembered node, no proof, an end mark
that is not 0, a household router (`OpenHintAll=0`).

Kept pointers: only cells' own graph nodes (vtable `0xFEA080`), which live until the Q table clear; a slot is emptied
when the bucket address, the generation word `0x11580C4` (written only by `0x61E360`, which both searches that reach
`0x61E030` call) or the clear sequence differs, before any kept pointer is read. The bucket vector can be reallocated
when it grows (`0x62BD60` -> `0x62A950`): the address differs then and the slot starts empty.

### 9.3 Shadow and counters

`OpenHint=2`: the game links every node; the hook compares the node's successor with the one it would have chosen
(`ohChecks`, `ohMismatch`, `OPEN HINT MISMATCH`) and counts **every insert's walk from the head** (`the game's walk N
nodes (n, max, 8 or more)`) against the hinted walk. That line is the measurement §9.1 lacks: under about 3 nodes a walk
the hint cannot pay (its proof touches two nodes) and should stay off.

Keys: `OpenHint` 0 / 1 / 2, `OpenHintAll`. Runtime: `native_set openhint`. Needs no other section. 70 KB a thread.

## 10. The heuristic and tie-breaking (candidate b): nothing exact to gain [verified docs/98 §7.4, §8, §9.1]

- h = straight-line distance to one target point T, weight 1. The wrapper sets T to the point of the best-scored walkable
  goal and the threshold to that score, and the search stops when a goal of that score is popped. So h aims at the goal
  that ends the search and is the tightest bound that needs no precompute. Unlike the world A*'s it is not weak by
  construction.
- It is far below the cost wherever multipliers are above 1: a class 1 cell costs 100,000 a metre, a penalised cell up
  to 400. A goal behind or on such cells is reached only after every cheaper cell of the room is closed. Those floods are
  the game's result (the cheapest path), not waste.
- A stronger h or other tie-breaking is not exact here: closed nodes are never reopened, an open node's f is updated with
  h at its old entry point, and entry points depend on the parent (docs/98 §7.1, §8.2). Any change of pop order can
  change entry points, costs and the chosen path. Not built.
- **Not measured:** pops against the straight-line bound. The logs hold only the mean (418 pops a found search); no line
  has a search's start-to-target distance or final cost. It needs two counters at the search's exit (h of the start
  node, g of the best goal), which belong in `floodmemo.cpp`'s `FmFoundNote`, not touched here.

## 11. Checks (2026-11-20, no game)

- Linux host test `cellspeed-host-test.cpp` (clang `-m32`, freestanding): the selftest header as it is plus the scope
  rule and 240 more open-list searches. **Exit 0.** The open-list model inserts 1,500 nodes a search with ties (f in
  steps of 1/64), pops, in-bucket moves (`0x61B910` rewritten from its disassembly) and bucket changes, and checks every
  insert against a reference walk from the head and the whole lists every 32 nodes; the class memo runs against a fake
  recompute with the four writes.
- 12 broken copies each fail it: proof without the tie / open / link test, link step short, key without flags or
  `X+0x4C`, no write sequence, agent without the ignore ids, serve without key clear or `X+0x4C`, either scope rule gone.
- clang-cl `/Zs /W4`: `ts3native.cpp` 170 warnings (as before the change), `/DTS3A_TABLE` 172, `selftest.cpp` 217; none
  in the new files, 0 errors. Not built with MSVC here.
- `tools/hook-sites.ts --check`, `node tools/selftest-all.ts --ci`: pass.
- Repro `ip-cellspeed-storm` (UNVERIFIED): shadow arm, then serve arm.

## 12. Files (§8-§11)

- New: `native/ts3native/cellspeed-core.h`, `cellspeed.cpp`, `cellspeed-test.h`, `cellspeed-host-test.cpp`.
- `ts3native.cpp` (include, install after CellIndex, the 60 s lines), `selftest.cpp`, `ts3native.ini`,
  `ts3addr-ea169.h` (3 unresolved rows: `0x61B880`, `0x66B090`, `0xFEA080`), `tools/ini-sections.json`,
  `tools/harness/native-switches.ts`, `tools/harness/repros.json`, `docs/68`, `docs/registry.md`, `docs/index.md`, `PLAN.md`.
- Not touched: `routespeed.cpp`, `floodmemo.cpp`, `plancand.cpp`, `cellindex.cpp`.
