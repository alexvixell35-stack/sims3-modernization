# 676. Routing adapted to the world: what world A\*, the water plan and the post pass compute, and a scan table for the water search

2026-11-20 (UTC). Inputs: the on-arm `ts3native-full.log` of the 2-minute Isla Paradiso storms s160-s168 (`route candidate
split` totals and lines), Ghidra headless decompiles on a private copy of the project (`0x676260`, `0x674250`, `0x673600`,
`0x671930`, `0x64E380` and three of its passes, `0x65DF90`, `0x650D80`, `0x6595B0`), objdump for call sites, the Isla Paradiso and
Sunset Valley waypoint graphs decoded with `tools/rtdata-decode.ts`, an offline search model on those graphs, our source.
Background, not repeated here: docs/99 (waypoint graph R, the world A\*), docs/100 (water grid G), docs/142 (failing world
searches), docs/663 §2 (the split and "not cacheable"), docs/669, docs/672, docs/673 (the hold). Tags as in AGENTS.md.
Built, default off: `[WorldPrecompute] WaterScanTable` and `Probe`. **Nothing here has run in the game.** Branch
`feat/world-precompute`.

## 0. Answer

The owner's question: would per-world precomputation remove much of the 45-50% of planner time in world A\*, water plan and
post pass?

- **Partly, and not in the form asked.** The time is not spread over all plans. It sits in the plans that cross water: about
  260 of 1,800 plans a storm (15%) run a water search, and those plans carry nearly all of the world A\*, all of the water
  plan and nearly all of the post pass (§1). A cross-water plan costs about 20-40 ms of world A\*, 22-30 ms of water search
  and 20-27 ms of post pass.
- **Connectivity classes already exist in the game** (§3): land components as one byte per waypoint, built at load; water
  bodies as one byte per grid cell. `ArePointsConnectedByLand` and the planner's own checks read them in O(1). Nothing to
  build. What costs time in those calls is the snap to the closest waypoint, which tests live obstacles.
- **The water search is the one phase that recomputes static data** (§2.2): it walks straight rays over two byte grids that
  change only when a footprint or the water grid is written. Built: a per-grid-pair table of every ray's run, checked
  against the live grids at every search, identical results (§5.1). Offline the jump is 5-20 times faster with it.
- **The world A\* cannot be memoised exactly, but its heuristic can be precomputed** (§2.1, §6.1). Its heuristic is the
  straight line at 1 per metre against edge costs of 10 per metre, and a water crossing costs 2 x 10 x (cells of the water
  body) as a flat fee. So a search to another island floods the whole start island before it tries the sea. Landmark
  distances (ALT) on a lower-bound copy of the graph give the same path cost with far fewer expansions. Designed and
  modelled offline, not built: it needs three cost-function hooks and a proof run, and the probe first has to confirm the
  share it targets.
- **The post pass is not static data** (§2.3): it is path smoothing against live obstacles and height and boat passes over
  the path's own nodes. The one static leaf (terrain height) is already memoised by `[HeightCache]`, which is on in these
  storms. Built: a split by its 19 passes, so the next storm says which pass carries the time.
- **Repetition is not in any log** (§4). The probe counts it for water and world searches with exact "unchanged" tests.

## 1. Where the time is [measured, nine on arms]

`route candidate split totals`, last line of each run (self ms / calls):

| run | `PlanHierarchical` calls | world A\* | water plan | post pass | cell A\* | share of planner time: world / water / post / cell |
|---|---|---|---|---|---|---|
| s160 | 1826 | 5894 / 1130 | 7220 / 281 | 5662 / 1701 | 2687 / 3477 | 24 / 29 / 23 / 11% |
| s161 | 1717 | 4021 / 1065 | 5262 / 218 | 4924 / 1588 | 2867 / 3141 | 19 / 25 / 23 / 13% |
| s162 | 1929 | 4958 / 1203 | 7420 / 267 | 5914 / 1758 | 3033 / 3438 | 19 / 29 / 23 / 12% |
| s163 | 1698 | 6417 / 1144 | 7022 / 301 | 6037 / 1594 | 3378 / 3279 | 23 / 26 / 22 / 12% |
| s164 | 1812 | 5400 / 1045 | 5780 / 258 | 5397 / 1703 | 3028 / 3242 | 23 / 25 / 23 / 13% |
| s165 | 1773 | 5319 / 1081 | 5632 / 255 | 5279 / 1619 | 2551 / 3369 | 23 / 25 / 23 / 11% |
| s166 | 1837 | 4890 / 1138 | 5799 / 247 | 4750 / 1727 | 2747 / 3306 | 23 / 27 / 22 / 13% |
| s167 | 1776 | 6952 / 1157 | 8109 / 316 | 5967 / 1682 | 2574 / 3237 | 26 / 30 / 22 / 10% |
| s168 | 1819 | 5395 / 1001 | 6540 / 256 | 5423 / 1735 | 2741 / 3236 | 23 / 28 / 23 / 12% |

Per call: world 3.8-6.0 ms, water 22-28 ms, post 2.8-3.8 ms.

The 479 split lines (plans >= 100 ms, 65.3 s over the nine runs), by whether the plan ran a water search:

| plans >= 100 ms | n | plan ms | world ms / calls | water ms | post ms | cell ms |
|---|---|---|---|---|---|---|
| with a water search | 427 | 56,043 | 17,770 / 643 | 18,102 | 11,551 | 3,474 |
| without | 52 | 9,214 | 241 / 1,420 | 0 | 223 | 5,935 |
| of the latter: failed (code 5) | 20 | 4,789 | 207 / 1,379 | 45 | 0 | 3,586 |

- A slow plan with a water search: world 42 ms, water 42 ms, post 27 ms on average. Per slow plan: world p50 26 / p90 96 /
  max 156 ms; water p50 45 / p90 56 / max 71 ms; post p50 26 / p90 40 / max 58 ms.
- 98.7% of the world A\* time of slow plans, and 98% of their post pass time, is in plans that also ran a water search.
- The slow plans without one are the many-candidate failures of docs/673 §1 (78-106 world searches that cost nothing, cell
  A\* time).
- Scaling up [inferred]: a storm has 218-316 water searches at 22-28 ms. If each of those plans also carries 20 ms of world
  A\* and 20 ms of post pass, that is the storm's whole world and post total (5-7 s each). So the 68-74% of planner time in
  the three phases is, to a first approximation, the cost of about 260 cross-water plans a storm. The probe's `world kinds`
  line (§5.2) measures this directly.
- 380 distinct routes among the 479 lines; 73 routes have more than one slow plan (one has 23).

## 2. What the three phases compute

### 2.1 World A\* `0x676260` [verified: decompile; docs/99 §2 for the parts not re-read]

- **Graph:** the waypoint graph R (`world+0x54`), a packed blob loaded from the `.world` file. Isla Paradiso: 43,942
  waypoints, 179,444 directed edges, 115 land components (largest 17,542 and 5,789 waypoints), 2,865 type-`0x13` and 2,423
  type-`0x15` shoreline waypoints. Sunset Valley: 39,265 / 159,662 / 77. Plus one virtual node per water body, linked to the
  shoreline waypoints through G's two maps (`G+0x30` waypoint to body, `G+0x4C` body to waypoints).
- **Search:** A\* on a binary heap keyed by `f`, closed bits per waypoint, duplicates instead of decrease-key. A first pass is
  limited to a circle around the midpoint (radius `0.5 d + 48` m, only below 600 m); if it fails the search runs again
  without it.
- **Cost** (per cost mode: 1 pedestrian `0x674E40`, 2 car, 3 pedestrian + car `0x674250`, 4 no restrictions `0x675800`):
  segment length times a factor from the edge class byte and the two waypoint types: 10 per metre walking, `10 / sidewalk`
  (5.9-6.7), `10 / street` (1.7-3.3, mode 3), trespass 13, lot hub rules (30, a flat 5,000,000 into the start or goal lot's
  hub, walls).
- **Heuristic:** straight-line distance times 1. It is admissible and 6-10 times below the real cost per metre, so the
  search is close to Dijkstra: the model (§6.1) expands 1,300-4,500 waypoints for a 100-600 m land route.
- **Water links** (only with planner flag `0x80`) [verified, `0x674250` virtual branches]: waypoint to body and body to
  waypoint each cost `10 x cells(body)` (the body's cell count, `G+0x3A0 + 8 x id`, at least 1). For a sea of 150,000 cells
  that is 1.5 million each way, the cost of 300 km on foot. Exceptions [branches verified; which waypoint the reference
  test `0x64FCE0` names is inferred]: a shoreline waypoint of the start or goal lot when that lot is a water lot
  (`Lot+0x4B0`) and the point is below sea level costs the small water-lot factor; a link at the start or goal waypoint
  itself costs 10 x its distance to the start or goal point.
  **Consequence [inferred from the cost, matches §1]:** a search to another land component pops every waypoint whose
  `g + h` is below about 2 x 10 x cells, which is the whole start component, before the sea's node is popped. Then every
  shoreline waypoint of that sea is pushed, and the search works inwards to the goal with the same weak heuristic. That is
  the 26-96 ms.
- **Static per world file:** R's records (positions, neighbour lists, edge class bytes, the type bits `0x01`, `0x07`, `0x0D`,
  `0x0E`), the cost factor tuning.
- **Static per water grid build** (load, build / buy exit, lot or terrain edits; docs/100 §3.1): the type bits `0x13` /
  `0x15` of shoreline waypoints, G's two shoreline maps, the body cell counts.
- **Dynamic inputs** [verified in `0x674250` and `0x676260`]:

| input | where read | changes when |
|---|---|---|
| blocked bit of a lot-edge waypoint (record flag `0x80`) | every edge into the waypoint | a lot's routing is built or updated (docs/99 §1.7) |
| the job's polygon set (`opts+0x8C`), tested against every segment of edge class 2 and 7 (`0x627780`) | most edges | per route: the footprints the job collected |
| `blockedNodeMap` (`G+0x35C`) through `0x66F890`, for a water link of a type-`0x13` waypoint | water links | a non-Sim footprint is added or removed |
| start and goal lot hub, their "edge component" sets, flags `0x10` / `0x20` / `0x40` / `0x80` / `0x100` / `0x200` | hub edges, lot-edge edges, water links | per plan |
| exact start and goal point | only when the start or goal waypoint is its lot's hub (flags 1, 2) and for the water-lot test | per plan |
| route option word (`& 0xCF00`: boat kinds) | water links | per Sim and vehicle |
| cost mode, short or long cost set (first heuristic value against the threshold 80) | every edge | per plan |

- **Why docs/663 said "not cacheable", exactly.** It gave the circle centre and the heuristic's use of the exact points.
  That is only half right: the circle and the heuristic use the two waypoint positions unless a waypoint is its lot's hub
  [verified, `0x673600`: flags 1 and 2 pick the exact point]. The real obstacles to an exact memo are the per-job polygon
  set (a segment test on most edges, against data that belongs to the route and is not versioned) and the lot component
  sets. A key that covers them is the whole search context; `[RouteSnap]`'s write sequence covers the world side but not
  the polygon set. So an exact memo of legs is not provable from what we have read; the probe's key B (§5.2) measures what
  it could hit if it were.
- **Start and goal snapping** `ClosestWaypoints_Find 0x65BB90` -> `0x6595B0` [verified]: a cached candidate area per point
  (`0x6583E0`), candidates sorted by squared distance to both ends (and rescored by direction for the first six), then a
  routability test per candidate against live obstacles (`0x659390`) until enough pass; up to three widening tries. In the
  planner it is 15 ms in 739 calls in s168. The 390 ms sim-thread frame under `World_ArePointsConnectedByLand` was
  one call of this on the sim thread; the storms do not show it as a recurring cost.

### 2.2 Water plan `WaterPath_Plan 0x671930` [verified, decompile]

- Rejects at once unless both cells carry the same non-zero water body id (`G+0x1C` cells, low byte).
- Picks a passability map from the route option word (0 swimmers, 1 boats, 2-4 houseboats; steps down while the start cell
  is closed in the map) and a high-cost map (`0x66D6E0`, per agent class), builds the callback with the start and goal
  cell, and calls the jump-point search `0x620560` (one caller, `0x671AFD`) on the two byte grids. The result is a list of
  jump points, turned into cell indices.
- **Data:** two byte grids of `w x h` cells of 4 m (Isla Paradiso 513 x 512 = 262,656 cells).
  - Passability maps (`G+0x7B0`): written only by the water grid build [verified docs/100 §4].
  - High-cost maps (`G+0x7C0`): lot outlines at build, plus the outline of every object footprint polygon, set at footprint
    insert and cleared at remove (docs/100 §5). This is the part that moves in play (boats, objects placed near water).
- **Per query:** start cell, goal cell, the two map indices, a constant cost scale. Nothing of the Sim beyond the map
  choice.
- **Where its time goes:** `[WaterJps]` already replaced the game's recursive jump with a loop (on in these storms). Nine
  sim-thread stall samples lie under the water plan: six inside our jump, three in the search's own code (open list).
  The jump walks rays cell by cell; a diagonal jump walks two straight rays from every cell it passes.
- **Static share:** everything a straight ray does, except "is this the goal cell" and "is this cell within 3 cells of the
  start or goal" (the callback `0x66DC50`). That is what §5.1 precomputes.

### 2.3 Post pass `0x64E380` [verified: call list, three passes decompiled]

19 passes over the plan's 64-byte path nodes, some only with option flags (boat `0x8000`, `0x1000000`, `0x800`). The seven
sim-thread stall samples under it fall in three:

| pass | function | what it does | static? |
|---|---|---|---|
| 3 | `0x64DD40` | retypes nodes, then pulls the path straight: advances along it while the direct segment is clear (`0x644560`, `0x6443E0`), re-inserting nodes | no: segment tests against live obstacles, per path |
| 6 | `0x645310` | lot lookups per node, segment-clear tests (`0x6443A0`), node inserts (`0x629140`) | no |
| 16 | `0x64C7C0` | "TempPathListTH": samples `Terrain_GetHeight 0xC6A540` and `World_GetHeightAt 0xC6A250` along every segment against the sea height and inserts nodes | terrain is static; the lookups end in the raycast `[HeightCache]` memoises |

- Slow plans have a median of 147 node inserts; the post pass costs 0.17 ms per insert call there.
- With flag `0x8000` (boat or water route) passes 10 and 11 (`0x64B430`, `0x64BBD0`) also run; not read.
- **So (d) has nothing to precompute per world** beyond the heights, which are cached. Which pass carries the 21-23% is not
  in the logs (seven samples); the probe prints all 19 (§5.2). A fix would be per pass (for example the height pass
  re-sampling a water leg whose height is the sea level by definition), after the numbers.

## 3. Connectivity classes: already there [verified]

- **Land:** `RTData_BuildComponents 0x66CE10` floods R once and stores one signed byte per waypoint (`R+0x28`, docs/99 §1.8).
  `World_ArePointsConnectedByLand 0x787240` -> `0x65DF90` is: closest waypoint of each point (`0x65BB90`), then compare the two
  bytes. The planner's own component check (docs/99 §2.7) reads the same bytes.
- **Water:** the body id in every grid cell (`G+0x1C`), from the build's flood fill. `0x650D80` ("same water") is two cell
  lookups and a compare; `WaterPath_Plan` starts with the same compare.
- **What they do not cover:** the passability class (a boat's map 1 can split a body that map 0 connects), blocked lot
  edges and footprints. Those make a search fail after the O(1) check passed; failing world searches are docs/142's
  ground (`[RouteEarlyOut]`), failing floods docs/667's.
- In the storms `sameLand` is 30-60 calls and 0 ms, `sameWater` 7-30 calls and 0 ms. **Nothing built for (a).**

## 4. Repetition [measured as far as the logs go]

- The logs carry no waypoint or cell of a search, so "the same query again" cannot be read from them. What they give:
  73 of 380 routes with a slow plan have a second slow plan in the same run (replans of one route), and docs/673 §1's
  `ufplan:` repeats (9-12% of managed plan time, same Sim and kind within 5 s).
- `[WorldPrecompute] Probe=1` (§5.2) counts, per water search and per outermost world search: seen before in this session,
  and seen before with nothing changed since, each with its ms. That decides memo against precompute:
  - water "seen before, grids unchanged" high: a result memo keyed (start cell, goal cell, two maps) with the grid compare
    of §5.1 as its invalidation would be exact (§6.3);
  - world key B "seen before, no routing write since" high: a leg memo is worth the proof work of §2.1;
  - both low: only the heuristic (§6.1) and the scan table help.

## 5. Built (default off)

### 5.1 `[WorldPrecompute] WaterScanTable` (waterjps-core.h, worldpre-core.h, worldpre.cpp)

**What is kept.** Per (passability map, high-cost map) pair that a search used: for every cell and each of the four
straight directions, one 16-bit entry: how many cells a ray from there runs before it stops, and whether it stops at a
jump point (a cell forced by its neighbours, or marked in the high-cost map) or at a cell that is not water or outside the
grid. Entry 0 means "longer than 32,766 cells: walk it".

**How a ray is answered.** One read gives the static stop at cell `k`. The goal and the two 7 x 7 boxes of the callback can
only stop the ray earlier, and whether they lie on it before `k` is arithmetic on indices. The ray is the game's index walk
(an east or west ray runs on into the next row exactly as the game's step does). Diagonal jumps still step cell by cell,
but each step's two straight scans are now one read each.

**Exact or relaxed: exact.** The table path returns what the cell walk returns for every input it accepts (no callback, or
the game's own `0x66DC50` with box centres inside 0..2^30); any other call walks. The walk is `[WaterJps]`'s `WaterJump`,
itself verified against the game's code (43.9 M jumps offline, 5.4 M in the game). Nothing can differ in a route.

**Invalidation: a compare, no writer hook.** The search's one call site (`0x671AFD`) is retargeted. At the start of every
search both live grids are compared with the table's own copies (256-byte blocks, then bytes). No difference: the table is
used as is. Up to 64 changed cells: each updates the entries whose next cell is in its 3 x 3 block, then the entries behind
on the same ray while they change (north and south: the three columns). More: the table is built again. So every writer is
covered, including ones we have not found, and a world reload or a grid rebuilt at the same address is just "many changed
cells". The grids cannot change during a search any more than they can for the game's own code.

**Where a jump may use it.** Only inside a search that went through the retargeted call, on the same thread, for the same
two grid pointers and size. One SRW lock guards the tables and is held for the search; a second thread's search that finds
it taken walks (`busy`). A search left by an unwind keeps the lock on its thread; that thread's next search continues with
it and frees it.

**Household rule.** The answer is identical, so it may serve every router. Until a shadow run shows 0 mismatches it serves
unseen and seen-NPC routers only (`WaterScanAll=0`; `[Experimental] HouseholdToo=1` lifts it, as for NeighbourMemo).

**Bounds.** 10 bytes per cell and table (two copies and four 16-bit entries): 2.6 MB each in Isla Paradiso, `WaterScanSlots`
tables (default 2, 1-4; the least recently used pair is replaced). Grids above 1,048,576 cells get no table. Nothing is
written to a save or a file; a table is built on the first search of its pair.

**Switches, counters, logs.**
- `ts3native.ini` `[WorldPrecompute]` `WaterScanTable` (0 off, 1 use, 2 shadow: the jump runs both ways, the walk's result
  is used), `WaterScanSlots=2`, `WaterScanAll=0`. Needs `[WaterJps] Enabled=1`.
- Runtime switch: export `ts3native_worldpre +0x00` (`native_set waterscantable`).
- One line a minute: `world precompute: water scan table mode M ready 0xR: searches N (with a table T; not: busy, no table,
  scope); grids at a search: unchanged N, cells updated C in R refreshes, built B; compare + update T ms; jumps with the table
  J (walked instead W), straight rays from it S, walked (run too long) L; shadow checks K mismatch M; N KB`.
- `WATER SCAN TABLE MISMATCH`, at most 10 lines.
- EA 1.69: no new address row. The site is `TS3A(0x671930) + 0x1CD` and is accepted only if it is a call whose target holds
  `[WaterJps]`'s jump call; else it logs SKIP.

**Offline numbers** [measured, a synthetic 513 x 512 sea with 12-40 islands, clang -O2 32-bit, not the game's grid]:
table build 5.3-7.1 ms; a compare with nothing changed 0.04 ms; a refresh after 8 changed cells 0.3-0.4 ms; straight jumps
8-20 times faster, diagonal jumps 5-14 times (16-59 cells per jump).

**Expected gain [inferred].** Two thirds of the water plan's stall samples are in the jump (six of nine). If the jump gets
5-10 times faster, the water plan drops by about half: 3-4 s of a storm's 5.3-8.1 s, 12-15% of planner time. The split's
water ms per call (22-28 ms today) is the measure.

### 5.2 `[WorldPrecompute] Probe` (worldpre.cpp, observe only)

- **Water searches** (the same retargeted call): `world precompute probe water: searches N (ms), found F; same start cell,
  goal cell and maps as an earlier search R (ms), of them nothing changed since U (ms); distinct D`. "Unchanged" is the grid
  compare itself when the table is on, else `[RouteSnap]`'s write sequence.
- **World searches** (plancand.cpp's world frame, so it needs `[RouteJobPhases] CandidateSplit=1`; the circle pass and its
  rerun count as one search):
  - `world precompute probe world kinds: water links allowed N (ms); start and goal waypoint in different land components N
    (ms)`: the claim of §1 and §2.1, measured.
  - `world precompute probe world: searches N (ms), failed F (ms); key A ... seen before R (ms), no routing write since U
    (ms), distinct D; key B ...`. Key A = start waypoint, goal waypoint, cost mode, planner flags, route option class. Key B
    = A plus the two lot hubs and the exact start and goal points. Neither holds the job's polygon set, so key B's count is
    an upper bound of what an exact memo could serve.
- **Post pass by node pass:** the 19 call sites inside `0x64E380` are retargeted to a stub that keeps every register and the
  flags, notes the time, and lets the pass return through a second stub. `world precompute probe post passes (ms/calls,
  pass and its Steam address): 1 644FD0 x/y, ...`. The targets are read from the calls at install, so the four sites
  `[RouteAsync] TailUnlock` retargets first (passes 16-19) chain into its hooks. Installed only with `Probe=1`.
- Tables: 4,096 keys each for water, world A and world B; sightings that find no room are counted (`not kept`).
- `Probe` has no `native_set` name; export `+0x04` holds the ini value.

## 6. Designed, not built

### 6.1 Landmark distances (ALT) as the world A\*'s heuristic

- **Idea.** Keep the game's search, costs, walls and tie order. Replace only the value the cost function returns for a
  heuristic call (`isEdge = 0`) by `max(game's value, landmark bound)`. The bound is `|d(L, m) - d(L, goal)|` over a few
  landmarks L, with `d` the shortest distance on a lower-bound copy of the graph: every edge at the smallest factor any
  mode-and-flag case can give it (edge class and static type bits only; blocked bits, polygons and lot rules can only raise
  a cost or wall the edge).
- **Exact or relaxed.** The bound never exceeds the real remaining cost and obeys the triangle rule on the lower-bound
  graph, so the search still returns a cheapest path. What can differ: which of several equal-cost paths, and after a
  failed search the "nearest reached" waypoint the blocker query uses. Car-only mode 2 is left alone (its own heuristic is
  already above its costs).
- **Offline model** [measured: the game's costs for modes 1 and 3 without lot rules, polygons or water, 300 random lot-edge
  pairs in one land component, landmarks chosen farthest-first per component]:

| world, mode | waypoints popped, game heuristic | with 16 landmarks a component | path cost |
|---|---|---|---|
| Isla Paradiso, mode 3 | 887,036 | 474,481 (1.9x fewer) | equal in 294 of 294 |
| Isla Paradiso, mode 1 | 1,001,145 | 459,770 (2.2x) | equal in 294 of 294 |
| Sunset Valley, mode 3 | 1,214,956 | 485,638 (2.5x) | equal in 300 of 300 |

  By distance in Isla Paradiso mode 3: below 100 m 143 -> 117 pops, 100-300 m 1,326 -> 829, 300-600 m 4,488 -> 2,536, above
  600 m 9,781 -> 4,600. Eight landmarks give 1.7x. A perfect lower-bound heuristic gives 3.5-4.8x, so landmarks reach about
  half of what any heuristic can. 100 landmark trees over 43,942 waypoints take 67 ms in the model (JavaScript).
- **The case that matters is not in that table.** For a search to another land component the bound includes the water
  fee: a landmark on the start side sees the goal `2 x 10 x cells` further than any waypoint of the start component, so the
  search heads for the cheapest shoreline instead of popping the whole component (§2.1). The model has no water grid, so
  this is [inferred]; the probe's `world kinds` line says how much of the 4.0-7.0 s it is.
- **What it needs.** Detours on the three cost functions (`0x674E40`, `0x674250`, `0x675800`; the first heuristic call of a
  search must pass through untouched, it selects the cost set); a reader of R's records and of G's two shoreline maps and
  body counts; one lower-bound graph per (mode, water links) pair; a fingerprint of R and G to rebuild on; a rule that
  switches the bound off for a query whose start or goal lot is a water lot (its shoreline links cost the small water-lot
  factor, which a static bound cannot know). A shadow mode runs the search twice and compares path cost.
- **Risk.** A bound that is too high for some edge gives a longer route, never a missing one. That is why it needs the
  shadow run before it serves.
- Effort: about 2 hours to build with host tests of the graph reader and the bounds, then one shadow storm and one serve
  storm.

### 6.2 A memo or table of world legs

Not provable as exact today (§2.1: the polygon set). Not designed further until the probe's key B line shows repeats with
no routing write in between worth more than §6.1 would give.

### 6.3 A memo of water search results

Exact with the pieces built here: the search reads the two grids, the start and goal cell and a constant. A result kept
per (grids, start, goal) and dropped whenever §5.1's compare finds a change is the game's result. It has to replay the
search's output vector through the game's allocator, which is not read yet. Worth building only if the probe's "nothing
changed since" count is high; boats moving through the high-cost map may keep it low.

### 6.4 Not worth it

- A cache file beside the mod: every table here builds in milliseconds from live data, and a file would add a staleness
  problem the compare does not have.
- Hand-tuned per-world data: nothing above needs it; custom worlds get the same tables.

## 7. Checks run (2026-11-20, no game)

- `worldpre-host-test.cpp` (Linux, 32-bit, freestanding; includes `worldpre-test.h` as is, about 40 s): the table against
  the walk on 260 random grids (8 directions, both flag values, with and without boxes, goals on and off the ray, tiny
  grids, rows that run on, a 300 x 300 sea with runs longer than an entry holds): about 2.2 M jumps equal, including the
  count of cells passed; cell-by-cell updates after 1-24 byte changes against a fresh build; the slot manager (refresh
  classes, two slots and the least recently used one, busy lock, a search left by an unwind, scope gate, too large, no
  memory); the jump's gate; shadow counts; the search hook; the probe's repeat counts with both "unchanged" sources; the
  world probe's outermost frame and kinds; the post pass bookkeeping. Pass. Six deliberate faults (box edge, column
  update, ray update, index run, a skipped refresh, a wrong grid in the gate) each fail it.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `waterjps-test.cpp`:
  0 errors; the first three have the warning counts of the base (170 / 172 / 217), none in the new or changed lines.
- `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL. `tools/addr-codemod.ts scan`: the two addresses are wrapped rows
  that exist; no new row, `ts3addr_count` unchanged (1498).
- Not run: the MSVC build, the Windows selftest (`TestWorldPre`), `waterjps-test.exe` (the game's jump against
  `WaterJump`; its code moved to `waterjps-core.h` unchanged), anything in the game. The post pass stubs' assembly has no
  test: it needs the game.

## 8. Next storm

On top of the s168 on-arm ini, shadow first:

```
[WorldPrecompute]
WaterScanTable=2
WaterScanAll=1
Probe=1
[RouteJobPhases]
CandidateSplit=1
CandidateSplitLogMs=100
```

Read: `world precompute: water scan table` (searches with a table, shadow checks, **mismatch 0**, how often the grids changed
between searches, compare + update ms), the three probe lines (repeats; water links and cross-component world ms; the post
pass by pass). Then `native_set waterscantable 1` (or `WaterScanTable=1`) and read the split's water ms per call against
22-28 ms. Repro `ip-worldpre-storm` runs both arms. Shadow doubles the jump work, so its water time is not a timing.

## 9. Files

`native/ts3native/waterjps-core.h` (the pure jump, moved from waterjps.cpp, and the table), `worldpre-core.h`, `worldpre.cpp`,
`worldpre-test.h`, `worldpre-host-test.cpp` (new); `waterjps.cpp` (include, the gate in `WaterJumpHook`), `plancand.cpp` (the
world frame calls the probe), `ts3native.cpp` (include, install, counter lines), `selftest.cpp`; `ts3native.ini`,
`tools/ini-sections.json`, `tools/ea-ini.ts`, `tools/harness/native-switches.ts`, `tools/harness/repros.json`,
`docs/registry.md`, `docs/68-hook-conflict-map.md`, `docs/index.md`, `PLAN.md`.
