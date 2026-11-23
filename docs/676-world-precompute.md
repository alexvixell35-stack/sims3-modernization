# 676. Routing adapted to the world: what world A\*, the water plan and the post pass compute, and a scan table for the water search

2026-11-20 (UTC). Inputs: the on-arm `ts3native-full.log` of the 2-minute Isla Paradiso storms s160-s168 (`route candidate
split` totals and lines), Ghidra headless decompiles on a private copy of the project (`0x676260`, `0x674250`, `0x673600`,
`0x671930`, `0x64E380` and three of its passes, `0x65DF90`, `0x650D80`, `0x6595B0`), objdump for call sites, the Isla Paradiso and
Sunset Valley waypoint graphs decoded with `tools/rtdata-decode.ts`, an offline search model on those graphs, our source.
Background, not repeated here: docs/99 (waypoint graph R, the world A\*), docs/100 (water grid G), docs/142 (failing world
searches), docs/663 §2 (the split and "not cacheable"), docs/669, docs/672, docs/673 (the hold). Tags as in AGENTS.md.
Built, default off: `[WorldPrecompute] WaterScanTable` and `Probe`. In-game results since: §10 (s172 shadow, s173 / s189
upkeep) and the docs/680 ledger. Branch `feat/world-precompute`.

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
used as is. Up to 128 changed cells (`WS_INCMAX`): each updates the entries whose next cell is in its 3 x 3 block, then the entries behind
on the same ray while they change (north and south: the three columns). More: the table is built again. So every writer is
covered, including ones we have not found, and a world reload or a grid rebuilt at the same address is just "many changed
cells". The grids cannot change during a search any more than they can for the game's own code. The table is computed from
the copies, not from the live grids (fix/review-b): a byte written between the copy and the table's read of it would
otherwise leave entries the next compare cannot see.

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

> **2026-11-20: built as `[WorldPrecompute] WorldAlt`, §14** (default off, not run). §14.1 corrects the water-link rules
> below and in §2.1; §14.8 has the model on the real graphs with the built tables.

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

## 10. s172 / s173: the probe ran. Table upkeep, and what the numbers say about the world A\* (branch `feat/worker-time2`)

2026-11-20. Inputs: `ts3native-full.log` of s172 (3-minute Isla Paradiso storm, `WaterScanTable=2`, `Probe=1`) and s173
(`WaterScanTable=1`). **Nothing in this section has run in the game.**

### 10.1 What the runs showed [measured]

- **The table is exact in the game:** s172, 8,906,043 jumps computed both ways, mismatch 0. s173 served 412 of 412 searches.
- **Post pass by pass (s172, ms / calls):** pass 3 `0x64DD40` 6,901 / 2,420 (2.85 ms a call); pass 6 `0x645310` 1,358; pass 16
  heights 689; pass 1 418; pass 10 236; the other 14 together 330. Pass 3 is 70% of the post pass. §11.
- **World A\*:** 911 searches, 9,387 ms. With water links allowed: 761 searches, 8,999 ms (11.8 ms each); the other 150
  cost 388 ms (2.6 ms each). Start and goal waypoint in different land components: 212 searches, 3,230 ms (15.2 ms each).
  So the water-link searches inside one land component are 549 searches and about 5,770 ms (10.5 ms each): **two thirds
  of the world A\* time is not island crossing.** §2.1's "pops the whole start island" explains the 212, not the 549. §12.
- **Repeats:** world key A seen before with no routing write since: 1 of 911. Water: 8 of 460. A result memo of either is
  useless in a storm (§6.2 and §6.3 are closed).
- **Table upkeep is too dear:** s173: 412 searches, grids unchanged at 115, updated in place at 147 (1,775 cells, 12 a
  refresh), **built 150 times**; compare + update + build 1,025 ms (2.5 ms a search, against a compare of 0.04 ms).

### 10.2 Why 150 builds [inferred from the counters; the new counters decide it]

A build happens for two reasons only (`WsRefresh`, `WxSlotFor`): the slot holds no table for the pair of grids, or more
than `WS_INCMAX` cells differ.

- In-place refreshes average 12 cells, which is one boat's outline on the 4 m grid. For 150 of 412 searches to see more
  than 64 changed cells, five or more boats would have to move between two searches of the same pair, a third of the time.
- A table is kept per (passability map, high-cost map) pair, and s173 kept two (`at most 2 tables`, 5,120 KB). The water
  plan picks the passability map from the route option word (swimmers 0, boats 1, houseboats 2-4) and the high-cost map
  per agent class (§2.2). Three pairs in use in turn through two slots replace the least recently used one at every
  change of pair: every such search is a build. 150 of 412 is what a third pair used a third of the time gives.
- So the likely cause is slot thrash, not footprint size. The log could not say which: the line had one `built` count.

### 10.3 Built

- **`WaterScanSlots` default 2 -> 4** (the maximum). A slot allocates at its pair's first search, so a world that uses two
  pairs still holds two tables (2.6 MB each in Isla Paradiso).
- **In-place limit 64 -> 128 cells, and a batch update** (`WsUpdateCells`, waterjps-core.h). A changed cell's north and
  south entries are its three columns, whole. Cell by cell that was `3 x 2 x h` entries per cell even when the cells of one
  outline share columns. The batch does the rows per cell as before and every touched column once. A whole-column pass
  reads only the grid and that column's entries in ray order, so one pass after all bytes are in place gives exactly what
  the last of several passes gave. Worst case 128 scattered cells: 384 columns, 393,000 entries, under 40% of a build
  (1,050,000 in Isla Paradiso); a boat outline of 12 cells touches about 6 columns instead of 36 column passes.
- **Builds split by cause** in the line: `built N (pair replaced E, too many changes O)`; first builds are `N - E - O`.
  `(most M)` is the largest in-place refresh. Exported after the block's old end (docs/registry.md).
- Exact as before: the invalidation is still the byte compare of both grids at every search; only what is done with a
  difference changed.
- **Not built: hooking the footprint add / remove.** The compare of two 262,656-byte grids with nothing changed is 0.04 ms
  [measured offline, §5.1], 16 ms over 412 searches. A writer hook would save that and lose "every writer is covered".

### 10.4 Expected [inferred]

If the builds were slot thrash: 150 builds x about 6 ms = 0.9 s of the 1.0 s goes; upkeep drops to about 0.1 s a storm.
If `too many changes` stays high instead, the next step is a per-pair change journal from the footprint insert / remove
(docs/100 §5 has the two writers), not a larger limit.

### 10.5 Checks (no game)

- `worldpre-host-test.cpp` (38 s): odd random grids now update through the batch, with up to 128 changed cells in one
  round, against a fresh build; the slot test asserts the two causes (`wxFullOver`, `wxFullEvict`) and the largest refresh.
  Pass. Deliberate fault (the batch marks one column too few): fails at the fresh-build compare.
- clang-cl `/Zs /W4`: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`, `waterjps-test.cpp`: 0 errors, no warning
  in `worldpre*` / `waterjps-core.h`. `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL.
- Not run: MSVC build, Windows selftest, the game.

## 11. Pass 3 `0x64DD40`: what the smoothing does and where 2.85 ms a call can go (read, not built)

Ghidra headless on a private copy of the project (`0x64DD40`, `0x644560`, `0x644080`, `0x643E60`, `0x6418B0`, `0x641A10`,
`0x6443E0`, `0x663B20`, `0x658870`, `0xAB8540`) and the disassembly of the main loop `0x64E0F3..0x64E359`. The stall watch
has no sample under it (it samples the sim thread; the pass runs on the route worker), so the time split below the pass is
not measured.

### 11.1 The loop [verified: disassembly]

- **Before the loop** (O(n), few obstacle tests): retypes interior nodes (types 0 / 8 / 9 to 10 by leg class and
  neighbours, isolated 10s back to 0); trims tail nodes within 0.3125 of the last node; `0x642420` nudges type-10 pairs
  (world segment tests `0x658A00` per try); `0x641FC0` retypes corners; `0x644630` cuts sharp corners (up to two segment
  tests each).
- **Cursors, not nodes.** `0x6418B0` splits every segment longer than 5 units into `ceil(0.2 x length)` steps. An anchor
  cursor A and a probe cursor P move over these samples.
- **Greedy with one lookahead.** From A the probe advances while the node-type gate `0x641B40` passes and the segment A-P
  is clear (`0x644560`). At the first blocked sample it tries the next one once. Then the shortcut A to the last good
  sample is committed (`0x6443E0`: moves or inserts the end nodes, erases the nodes between), and **the anchor advances by
  one sample**, not to the shortcut's end. The probe restarts behind the anchor.
- **Tests per path:** with S the number of samples (nodes plus length / 5), at most about 3 S; a stretch that cannot be
  shortened costs two failing tests per anchor sample [inferred from the verified loop]. It is **linear, not quadratic,
  and no segment is tested twice**. The funnel algorithm of Recast / Detour (docs/672) does not apply as is: it needs the
  corridor of portals the path came from, and this pass has only the node list and the live obstacle trees.

### 11.2 One segment test [verified unless tagged]

`0x644560` (thiscall, `ECX` = anchor cursor, three stack arguments, `ret 0xC`): a dot-product reject, then `0x644080`
(thiscall, `ECX` = smoother, x0, z0, level0, x1, z1, level1, `ret 0x18`), which walks the lots the segment crosses:

- `0xC69790` every iteration: the lot at the start point through the world cell's lot list (`0xC68990`, a hash lookup; a
  dirty lot index rebuilds under the world manager's lock), although the cursor already holds its lot;
- `0xAB8540` world to lot coordinates: `fsin` and `fcos` of the lot's angle on every call, three or more per test;
- off lot: `0x658A00`, which ends in `0x658870`: recursive bisection of the segment with a point test per midpoint;
- on lot: `0x643E60`: false at once when the two levels differ; lot table lookup `0x62FB10` for the same (lot, level) every
  test; then by the smoother's byte `+0x72`:
  - 0: `0x6675E0` = two point queries (start, end) and one segment query on the lot's obstacle quadtree;
  - not 0: a "Routing/TemporaryFootprintSet" is built and freed **per test** (heap), `0x667400` (flushes deferred edits
    when the tree is not pinned), then three virtual calls per hit and stairs checks with an object lookup.
- The quadtree walk `0x663B20` starts at the root for every query and constructs and destroys 13 stack frames (26 calls)
  per query.
- Each probe step (`0x641A10`) also runs `0xB87D30` (a point test over up to four lists) and a lot lookup on a lot change.

### 11.3 Where the waste is

| waste | status |
|---|---|
| same segment tested twice | none [verified loop] |
| the start point's lot, lot graph and lot-local coordinates recomputed for every test of one anchor | [verified]: constant per anchor stop, at least two tests each |
| the start point's quadtree point query repeated per test (mode `+0x72` = 0) | [inferred: assumes `0x663900` is a pure point query]; with the end points that recur between neighbouring anchors, about two of three descents are repeats |
| a temporary footprint set allocated per test (mode `+0x72` != 0) | [verified] |
| full root descent and 26 constructor calls per query | [verified] |
| the anchor advancing one sample after a committed shortcut | by design; changing it changes the path |

### 11.4 Why nothing is built

An exact replacement is not provable from this reading:

1. There is no time split below the pass: the 2.85 ms may sit in the loop's tests, in `0x642420` / `0x644630`, or in
   `0xB87D30` per sample.
2. The value of `+0x72` in the storm is unknown, and the two modes cost very differently.
3. A per-path obstacle set needs `0x667350`, `0x663900` and `0x66B670` reproduced or memoised, and a proof that the tree
   cannot change inside one pass (`0x667400` may flush deferred edits at the first query; `0x62FB10` may insert into the lot
   table). Not read.

### 11.5 Next step (about 45 minutes to build, one storm)

Extend `Probe` with calls and time for `0x644560`, `0x644080`, `0x643E60`, `0x658A00`, `0xB87D30`, `0x642420` and `0x644630`
inside pass 3, plus the count of tests with `+0x72` set and the tests per call. Then, by what carries the time:

- **start-side memo per anchor** (lot, lot graph, lot-local start, start point query): retarget `0x644618` (`call 0x644080`
  inside `0x644560`); when both cursors hold the same non-zero lot call `0x643E60` directly. Needs the proof that the
  cursor's lot equals `0xC68990` of its point. Shadow: run both, compare the byte result.
- **a per-pass obstacle set** only if the quadtree descents dominate, after item 3 above is read.
- Hook facts for either: `0x64E166` and `0x64E27F` are the two `call 0x644560` sites; the result is in `AL` only.

## 12. World A\* with water links: what s172 changes in §6.1 (design, not built)

> **Built: §14.** The "start or goal waypoint's own link" rule below is wrong as written (§14.1: it is the virtual start or
> goal node), and mode 1's type-`0x15` links cost the water-lot value without a lot test.

- **Measured (s172):** 761 of 911 searches allow water links and carry 8,999 of 9,387 ms. 212 of them cross land
  components (3,230 ms, 15.2 ms each). The other 549 start and end in one component and cost about 5,770 ms (10.5 ms
  each), four times a search without the flag (2.6 ms).
- **So "the start island is flooded before the sea" is a third of the cost.** For the 549 the heuristic is the same weak
  one (1 per metre against 6-10), and with the flag every shoreline waypoint popped pushes its water body's node, and a
  small body (cost `10 x cells`, at least 10) is cheap enough to be popped, which pushes all of its shoreline waypoints
  [inferred from §2.1's costs; the probe has no pop counts]. The storm's routes are also long (forced across the map).
- **Design that covers both, same path cost: ALT (§6.1) with the water links in the lower-bound graph.**
  - Lower-bound graph per cost mode: R's edges at their smallest factor, plus per water body a node with links
    `min(10 x cells, small water-lot factor)` each way. Using the water-lot factor as the lower bound removes §6.1's
    "switch off for water lots" rule at the cost of a weaker bound there.
  - Landmarks farthest-first per land component (16), plus the largest bodies' nodes. The bound to a goal on another
    island then holds the fee of the cheapest sea link, so interior waypoints of the start island get
    `f >= cost through the best shoreline exit` and are pruned once the exit is found: the multi-source bound, obtained
    from the same tables.
  - The start or goal waypoint's own link costs 10 x its distance to the point (it can be below the fee): skip the bound
    for searches whose start or goal waypoint is a shoreline waypoint. Simple and exact.
  - Exactness: unchanged from §6.1 (admissible and consistent on a lower-bound graph, so the cheapest path cost; an equal
    cost path may differ). Shadow compares the path cost of both searches.
- **Before building:** pops per search by kind in the probe, to confirm that the 549 searches pop thousands of waypoints
  and how many of the pops are water-body fan-out. Model gain inside one component was 1.9-2.5 times fewer pops (§6.1);
  for the 212 crossing searches the model has no number.
- Effort unchanged: about 2 hours with host tests, one shadow storm, one serve storm.

## 13. Built: pass 3 by callee in the probe; the `0x644618` shortcut is not provable as named

### 13.1 `Probe`: pass 3 callees (worldpre.cpp, observe only, default off, NOT RUN)

- **Sites** (12 `E8` calls, Steam 1.67 offsets; objdump of the repack copy, whose post pass sites are the ones `Probe`
  accepted and timed in the game in s172):

| callee | what | sites |
|---|---|---|
| `0x642420` | nudge of type-10 pairs | `0x64DFDE` |
| `0x644630` | corner cutting | `0x64E018` |
| `0x644560` | segment test of the main loop | `0x64E166`, `0x64E27F` |
| `0x644080` | its walk over the lots on the segment | `0x644618` |
| `0xC69790` | next lot on the segment | `0x6440F2` |
| `0x658A00` | world (off-lot) segment test | `0x644168`, `0x644388` |
| `0x643E60` | lot segment test (quadtree) | `0x644209`, `0x6442EC`, `0x644327` |
| `0xB87D30` | point predicate per cursor step | `0x641A93` (in `0x641A10`) |

- **No address rows.** Pass 3's address is the target read from the post pass's third call; the segment test, the lot walk
  and the cursor advance are targets read from calls at fixed offsets under it; the sites are offsets from those. Install
  writes only if every site is a call and two sites have the same target exactly when the table says they are the same
  callee. Else `SKIP world precompute pass 3 split`. `ts3addr_count` unchanged.
- **Same stubs as the 19 passes** (every register and the flags kept, the return address swapped for the exit stub, so
  callee-cleans conventions with any `ret n` work; the exit passes its stack pointer, so an entry an unwind left inside
  the returning call is dropped instead of returned through). A site inside `0x644080` or `0x641A10` is also reached from other
  passes (pass 6 through `0x6443A0`): an entry is timed only while a pass 3 entry of the thread is open.
- **Smoother `+0x72`:** at the lot walk's site `ECX` is the smoother (thiscall); the stub's saved `ECX` is read and the byte
  counted. The game reads `[ecx + 0x71]` in the same call.
- Line: `world precompute probe pass 3 callees (ms/calls, each includes the ones it calls; ...): nudge 642420 a/b, corners
  644630 ..., segment test 644560 ..., lot walk 644080 ..., next lot C69790 ..., world test 658A00 ..., lot test 643E60 ...,
  sample predicate B87D30 ...; lot walks with smoother +0x72 set N`. Ready bit 16.
- **Reading it.** Times are inclusive: segment test > lot walk > (next lot + world test + lot test). Lot walk minus its
  three callees is the walk's own code. The stubs cost two timer reads per call, so pass 3's own total rises in a probe
  run; compare shares. Nesting is four deep at most (pass 3, segment test, lot walk, lot or world test); deeper is untimed.
- Host test: entries outside pass 3 or under another pass are not timed; the four-deep chain returns each entry's own
  address; the `+0x72` read from the saved `ECX`; the fifth level untimed.

### 13.2 The shortcut at `0x644618` [verified decompile `0x644080`, `0xC69790`]

Asked: call `0x643E60` directly when both cursors hold the same lot. Read again for the proof, it does not hold as named:

- **The cursors are not at that site.** `0x644080` gets (x0, z0, level0, x1, z1, level1) and the smoother. The cursors'
  lots live in `0x644560`'s caller. A hook that uses them has to sit at `0x64E166` / `0x64E27F` and reproduce `0x644560`'s
  dot-product reject too.
- **What the game does in the one-lot case.** `0xC69790`: lot L = `0xC68990(start)`; if L, transform the end point
  (`0xAB8540`) and test it against L's size (`L+0x174`, `L+0x178`); inside: both flags stay 0 and L is returned. `0x644080`
  then calls `0xAB1F00` (result unused on this path) and returns `0x643E60(x0, z0, level0, x1, z1, level1, L, 0)`.
- **So the exact condition is** "`0xC68990(start)` is L and the end point is inside L's rectangle", not "both cursors
  hold the same lot". The cursor's lot is set by `0x6418B0` from `0xC68990` at its node, and `0x641A10` refreshes it by its
  own rule on a step (`0x641AF8`); that it equals `0xC68990` of every sample point is not shown. Lots that overlap or a
  sample on a lot edge would break it.
- **Side effects of the skipped calls:** `0xC68990` can rebuild the world's lot index when it is dirty (a lock, a write).
  Skipping it moves that rebuild to the next reader; the pass itself reads the index again at the next cursor step. Not
  harmful, but it is a write the game does here. `0xAB8540` and `0xAB1F00` write only their out parameters [`0xAB8540`
  verified; `0xAB1F00` from the agent's read, not re-checked].
- **What it would save:** one hash lookup, one transform and four compares per test, against two transforms, a lot table
  lookup and three quadtree descents inside `0x643E60`. Probably under a tenth of a test [inferred]. The probe's `next
  lot` against `lot test` time is the measure.
- **Decision:** not built. An exact variant exists (memo of `0xC68990(start)` per anchor stop at the `0x6440F2` site, valid
  while the lot index is clean), but it is worth building only if `next lot C69790` carries a real share. If `lot test
  643E60` carries the time, the work is inside it (the per-test temporary set when `+0x72` is set, or the start point's
  repeated quadtree query), which §11.4 item 3 has to read first.

### 13.3 Not done

Pop counts per world A\* search kind (§12): the search's closed set and heap are not read in our source; it needs the
pop site inside `0x676260`. Open.

### 13.4 Checks (no game)

`worldpre-host-test.cpp` pass; clang-cl `/Zs /W4` on `ts3native.cpp` (Steam, `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors, no
warning in `worldpre*`; `node tools/selftest-all.ts --ci` pass. Not run: MSVC, the Windows selftest, the game. The stubs'
assembly is the s172-proven macro with new indices; the 12 sites' bytes were read from the repack copy only (no stock
exe on this machine), and the install check is what guards another layout.

### 13.5 Next storm

`[WorldPrecompute] Probe=1` (with `WaterScanTable=1`, `WaterScanAll=1`, `[RouteJobPhases] CandidateSplit=1`). Read `pass 3
callees` and `+0x72 set`, and the scan table's `built N (pair replaced E, too many changes O)`.


> **This repo (2026-11-23):** §1-§13 come with this PR (WaterScanTable, Probe and its pass 3 split). §14 of the
> contributor branch (`SmoothMemo`: shadow 4,348,062 / 0, no gain; `WorldAlt`: built, not run) is not part of it; §15
> (`SmoothCut`) was imported earlier and follows unchanged.

## 14. Built: `SmoothMemo`, a memo of quadtree cell evaluations inside one pass 3 (default off; s212 shadow 0 mismatches, s213 no gain)

Branch `speed/pass3-smooth`. Ghidra headless on a private copy (`0x663900`, `0x66B670`, `0x66B600`, `0x66B090`, `0x667350`,
`0x662D30`, `0x6681E0`, `0x668C40`, `0x667DB0`, `0x669530`, `0x669850`, `0x669B90`, `0x669D80`, `0x66A380`, `0x66A040`,
`0x61B7C0`, `0x64E380`) plus capstone on the sites. The repack copy only, as in §13.

### 14.1 What a lot test really costs [verified: decompile + disassembly]

§11.2 listed "two point queries and one segment query" for the mode with smoother byte `+0x72` clear. Read further:

- `0x6675E0` (thiscall, `ECX` = the level's tree from `0x62FB10`, stack: context = smoother `+0x10`, x0, z0, x1, z1,
  radius; `ret 0x18`): `0x663900` walks a stack cursor down to the start point and one to the end point, then
  `QuadCell_Multiplier 0x66B670` on each. Either multiplier equal to the constant at `0x11584C8`: blocked. Else the segment
  query `0x667350`, whose callback `0x662D30` asks `0x66B670` and, when the query's flag byte is 0, the class reader
  `0x66B600` for **every final cell the segment crosses**, and keeps count, minimum and maximum. Blocked when the maximum
  does not exceed one of the two end multipliers.
- Both readers return the cursor's cached value only when the cursor's key (`+0x1C >> 8`) equals the context's flags
  (`+0x50`). The cursors of a query are stack objects built for that query, so the key never matches and every ask runs
  the evaluator `QuadTreeCellPos_C 0x66B090`: the leaf's footprint entries with three or more virtual calls each
  (docs/98 §5.2), and for small cells a temporary footprint set. With a flag above bit 23 the 24-bit key cannot match
  at all, so the class ask evaluates the same cell a second time (docs/98 §5.4).
- So a lot test is N + 2 cell evaluations for a segment crossing N cells, and the neighbouring tests of a pass (same
  anchor and the next sample; the next anchor and the same sample) cross mostly the same cells. The pass tests no segment
  twice (§11.1), but it evaluates the same cells many times. That is the repeat.
- `+0x72` is bit 10 of the route's flag word (`0x64E380`: `byte [smoother + 0x72] = (flags >> 10) & 1`). With it set the
  lot test collects footprints into a set and asks no multiplier: this memo does nothing there.

### 14.2 Why the memo is exact

| claim | status |
|---|---|
| The evaluator reads of the cursor: tree `+0x04`, parent `+0x08`, node `+0x0C`, size `+0x14`, position `+0x18`, shape (low byte of `+0x1C`), quadrant (bits 8-9 of `+0x20`, in `0x66A380`). It writes `+0x10` and the class byte before reading them; the reader writes the key bits of `+0x1C` before it calls | [verified: decompile of `0x66B090` and the callees above; disassembly of both readers] |
| It reads of the context: options `+0x40`, class word `+0x4C`, flags `+0x50`; it writes `+0x4C` in one branch (docs/98 §5.3 step 1) | [verified: every `param + offset` on the context argument in those functions]; the same list FloodMemo rests on (docs/667) |
| Beyond that: the node's footprint entries and the footprints' own fields, the agent's ignore list behind the options, one object lookup by the agent (`0x8294E0`) | [verified for the functions read; the footprints' virtual slots are docs/98 §5.1's table, not re-read] |
| The game reuses the same answer itself: a cell of the in-room search keeps multiplier and class for the whole hierarchical plan while the flags match | [verified, docs/98 §5.4] |
| A class 1 cell clears its key (`0x66B090`'s last lines), so the game evaluates it at every use | [verified]. Why: not read. The memo never keeps such a cell |
| The trees do not change inside a pass 3 call | [inferred]: the pass runs inside a plan, the plan holds the routing lock from start to end, tree writers run under it (docs/81, docs/156). Not re-proved here. Three checks stand next to it, below |

Record: key = the seven cursor words above, the options pointer, a hash of the options' first `0x38` bytes, the context's
class word, flags and lot (`+0x54`); value = the cursor's `+0x10`, `+0x1C`, class byte and the context's class word after
the game's function ran. A hit writes those four back and returns what the game's reader returns (`fld [cursor + 0x10]`
or `movsx eax, byte [cursor + 0x20]`). A miss runs the game's function on the game's cursor and copies what it left.

Checks that do not depend on the lock claim:

1. A tree whose pending-edits byte (`+0x4C`) is set is neither read from nor written to the memo. `0x6675E0` asks for its
   two end cells before `0x667350` flushes deferred edits (`0x666A40`), so those two asks could see a tree the segment
   query then changes.
2. `[RouteSnap]`'s write sequence (when its counting is on) and the thread's managed-call sequence: a move drops every
   record, and an evaluation during which either moved is not kept.
3. Every pass 3 call starts with an empty table and closes the memo in a `__finally`; an ask that is not below the open
   pass's stack frame closes it too.

Shadow (`SmoothMemo=2`) runs every evaluation and compares the four words.

### 14.3 Sites and install

Five `E8` retargets, no address row (`ts3addr_count` stays 1507). Found from the post pass's row `0x64E380`: its call of
pass 3 (`+0x48`), then pass 3 `+0x426` -> segment test `+0xB8` -> lot walk `+0x189` -> lot test `+0x1D9` -> `0x6675E0`,
whose calls at `+0x62` and `+0x70` must name one function (the multiplier reader) and whose call at `+0xEE` is the
segment query; in it `+0x3A` must be a `push imm32` (the callback) and `+0x7C` a call of the tree walk `0x663B20` (a
row: the anchor); the callback's calls at `+0x0E` (must be the multiplier reader) and `+0x24` (the class reader); both
readers must start with the same nine bytes and call one evaluator at `+0x2A`. Anything else: `SKIP ... nothing
written`. With `Probe` on, the probe's stubs sit on the first four calls; the install takes the game's functions from the
probe's saved targets and chains the pass hook into the probe's stub, so it runs after `InstallWorldPre`.

| site | was | now |
|---|---|---|
| `0x64E3C8` | `call 0x64DD40` | `SmPassHook` (thiscall shape, `ret 4`, `eax` passed back whole) |
| `0x667642`, `0x667650` | `call 0x66B670` | `SmMultHook` |
| `0x662D3E` | `call 0x66B670` | `SmMultHook` |
| `0x662D54` | `call 0x66B600` | `SmClassHook` |

Outside a pass 3 call of the thread both reader hooks call the game's function at once (one thread-local read). The
callback and `0x6675E0` are shared with other callers of the segment query; they get that path.

### 14.4 Keys, scope, line

- `[WorldPrecompute] SmoothMemo` 0 off (default), 1 serve, 2 shadow. Runtime switch `native_set smoothmemo` (export
  `ts3native_smoothmemo` `+0x00`); the hooks install only with a non-zero ini value.
- `SmoothMemoAll` 0 (default): records answer only for unseen and seen-NPC routers; a household, selected or unknown
  router's pass runs as shadow. 1 (or `[Experimental] HouseholdToo=1`): every router except a plan that may be a player
  command (FloodMemo's rule).
- Line, once a minute: `world precompute: smooth memo mode M ready 0xR: pass 3 calls with the memo N (ms; shadow by
  scope), without N (ms); cell evaluations asked N: from a record, the game's (kept; not kept: class 1, slots taken,
  other), tree with pending edits; records dropped in a pass; shadow checks N mismatch N (the game's evaluations a
  record would have replaced N ms)`.
- Memory: 280 KB per planning thread, at most 4.

### 14.5 What is not known

- **The gain.** No run has split pass 3 (§13 probe, NOT RUN). The memo touches only lot tests with `+0x72` clear. If the
  storm's passes are mostly off-lot (`0x658A00`) or run with `+0x72` set, it saves next to nothing. The shadow arm
  measures it directly: "the game's evaluations a record would have replaced N ms" against "pass 3 calls with the memo
  (ms)". Read that before turning mode 1 on.
- The tree walk `0x663B20`, its box tests and the two descents per test still run on a hit.
- Candidates (a) (lot of the start point per anchor) and (b) (sin / cos per lot) were not built: (b) exists as
  `[LotSinCos]`; (a) saves one cell-list lookup per test (§13.2). (c) as a per-pass obstacle set and (d) were not built:
  this memo is the part of (c) that could be proved (the per-cell answers, not the tree walk); (d)'s set differs per
  segment.
- Why the game re-evaluates class 1 cells.

### 14.6 Checks (no game)

- `smoothmemo-host-test.cpp` (Linux, 32-bit, freestanding; `smoothmemo-test.h` as is): the table; a pass of 300-400 asks
  through the hooks against the same asks through fake readers alone, every cursor word, context word and return value
  folded into one sum (equal), with fewer evaluator runs; class 1; the class word's replay; flags above bit 23; a new
  pass after a write with no sequence; a write and a managed call inside a pass; every context word, an option word,
  another tree; pending edits; no context; the cursor's own cached answer; a pass left open; shadow and a forced
  mismatch; scope; the command rule; a thread with no block. Thirteen deliberate bugs (a key word dropped, no replay of
  the class word or byte, class 1 kept, each guard off) each fail it.
- clang-cl `/Zs /W4` on `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors, no warning in
  `smoothmemo*`. `node tools/selftest-all.ts --ci` pass.
- Not run: MSVC, the Windows selftest, the game. The five sites' bytes were read from the repack copy only; the install
  check guards another layout.

### 14.7 Next storm

`ip-smoothmemo-storm` (repros.json): ini `SmoothMemo=2`, `[RouteSnap] Enabled=1`; arms off, shadow, serve by
`native_set smoothmemo`. Pass: mismatch 0, no `SMOOTH MEMO MISMATCH`; then compare pass 3 ms per call with and without.

## 15. Pass 3: where the 2.5 ms go, and `[WorldPrecompute] SmoothCut` (default off; shadow and serve ran, §15.10)

Branch `feat/post-pass-1120`, 2026-11-20. Inputs: the `Probe` lines of s195 / s196 / s198 / s200 (the §13 probe ran),
SmoothMemo's lines of s212 (shadow) and s213 (serve), Ghidra headless on a private copy of the project (`0x64DD40`,
`0x644560`, `0x644080`, `0x643E60`, `0x6418B0`, `0x641A10`, `0x6443E0`, `0x641B40`, `0x6675E0`, `0x667350`, `0x663B20`,
`0x662D30`, `0x66A040`, `0x66A760`, `0x669CB0`, `0x669C30`, `0x669BE0`, `0x66B090`, `0x658A00`, `0x658870`) and objdump of
the sites. Bytes read from the contributor's game copy only. In-game results: §15.10.

### 15.1 The numbers [measured, s195: 2-minute storm, `Probe=1`, times include the probe's stubs]

| | ms | calls | per pass 3 call | each |
|---|---|---|---|---|
| pass 3 `0x64DD40` | 6,179 | 1,779 | | 3.47 ms |
| segment test `0x644560` | 5,647 | 215,942 | 121 | 26 us |
| lot test `0x643E60` | 3,143 | 135,799 | 76 | 23 us |
| world (off-lot) test `0x658A00` | 2,213 | 149,557 | 84 | 15 us |
| nudge `0x642420` | 236 | 1,779 | | |
| next lot, corners, sample predicate | 84 | | | |

- The segment tests are 91% of the pass; the lot test is half of it and the world test a third. The dot-product reject in
  `0x644560` fired 27 times in 215,942. `+0x72` was set in 9,640 of 215,915 lot walks (4%): the mode of §11.2 that builds a
  footprint set per test is rare.
- s196 / s198 / s200 give the same shares (lot test 55-58% of the segment tests, 16-19 us each).
- SmoothMemo's s213 line: 9,476,096 cell evaluations in 1,648 passes, 5,750 a pass. With 76 lot tests a pass that is
  about 75 cells per lot test [inferred: the two figures are from different storms].

### 15.2 Why SmoothMemo gave nothing

- s212 shadow: 4,348,062 records compared, mismatch 0: the memo is exact. s213 serve: 3.59 M of 9.48 M evaluations came
  from a record and the post pass was 5,659 ms / 1,648 calls against 5,634 / 1,672 in s202. No gain.
- Its "would have replaced 10 ms" figure was a timer bug on the contributor's branch (whole microseconds summed per
  evaluation); what s213 measured is the end to end time, and that did not move. SmoothMemo is not in this repo.
- So an evaluation costs about what the memo's own key (12 words, a hash of the options' 0x38 bytes) and probe cost
  [inferred from the end to end result]. Most cells a segment crosses hold no footprint entry, and such a cell is the tree's
  default (docs/98 §5.2 step 8). The time of a lot test is the walk itself: per final cell about one and a third inner
  nodes, four child steps `0x66A040` and four box tests `0x66A760` each, plus the callback.
- "Slots taken 4,487,401" is the table of 4,096 filling in long passes (846 records kept a pass on average; the long
  passes carry the count). A larger table would raise the hits, not the gain per hit. Not pursued.

### 15.3 What the loop does [verified: decompile and disassembly `0x64E0F3..0x64E359`, re-read; it corrects §11.1 in one place]

Cursor = (node index, x, z, level, lot, next lot, step, steps, dx, dz) (`0x6418B0`); a path segment longer than 5 units
has `ceil(0.2 x length)` steps. Anchor A at `[esp+0x68]`, probe P at `[esp+0x28]`, the last good probe position G.

1. P = the node after A, G = P (not tested: it is A's own path segment), P steps on.
2. While the node-type gate passes and (G is at the path's start or the segment A-P is clear): G = P; P steps on. At the
   last node: commit and return.
3. At the first blocked P: step P once more and test A-P again (the one lookahead). Clear: G = P, back to 2.
4. Stop. If G is still the node after A: **A steps one sample** (`0x641A10` on A). Else commit A-G (`0x6443E0`: rewrites
   the nodes), which leaves A at the shortcut's start and steps it **one sample** too.
5. P = the node after A again (`0x6418B0` on P with A's index + 1: §11.1 said "behind the anchor"), G = P, back to 2.

So every anchor stop ends in **two failing tests** (one when the gate or the last node ends it), and A then moves at most 5
units and asks again for nearly the same segments: from A's next sample to the same one or two samples past the next
node. 121 tests a pass is what "two failing tests per anchor sample, plus one per sample the probe gains" gives for a
path of some tens of samples [inferred: the probe counts calls, not results; `SmoothCut`'s line will show the split].
No segment is tested twice, but **the failing fan from the moving anchor to the same target is the pass**.

### 15.4 What a failing lot test pays [verified]

- `0x6675E0` (one caller: `0x643E60` at `0x644039`): the two end cells' multipliers m0, m1 (`fstp [esp+0x58]`, `[esp+0x08]`);
  either is `FLT_MAX` (the float at `0x11584C8`): blocked, no query. Else the segment query `0x667350` (one caller:
  `0x6676CE`), and the answer is **clear exactly when the query saw a cell and `m0 >= largest` or `m1 >= largest`**
  (`comiss` + `jae` at `0x6676E5`, `0x6676F0`). The least value comes back too and is not read.
- `0x667350`: flushes deferred edits when the tree is not pinned (`+0x48 < 1`), pins, runs the walk `0x663B20` with the
  callback `0x662D30` and a block (count, least, largest, flag byte), unpins.
- The callback takes the cell's multiplier, makes it `FLT_MAX` when the flag byte is 0 and the class is above 1, updates
  count, least and largest, and **returns 1 on every path** (`mov al, 1` at both exits). The walk would stop on 0
  (`0x663DBF`), but this callback never asks for it. A segment that the first cell blocks is walked to its end.
- The walk: 13 stack cursors, depth-first, children 0..3; a child is entered when its box test passes; a final cell
  (`0x669CB0`: no node, or shape 0x15 and up) goes to the callback. A cell that is not final at level 13 ends the whole
  walk (`cmp esi, 0xD` at `0x663DC5`).

### 15.5 `SmoothCut`: the design, and why the answers are the game's

`native/ts3native/smoothcut-core.h` is the walk's loop again over the game's own functions (constructor `0x669BE0`, root
set-up `0x669C30`, final test `0x669CB0`, child step `0x66A040`, box test `0x66A760`, callback `0x662D30`), in the game's
order, with two early ends:

- **cut:** after a callback, when the largest value so far is above both m0 and m1, the walk ends.
- **hint:** each thread keeps the last four cells that ended a walk this way, as (tree, child numbers from the root, two
  bits a level). Before walking, a query of the same tree goes down each hint's child numbers with the game's child step
  and box test; if every box passes and a final cell is reached, the game's callback runs on it; a value above both end
  multipliers ends the query without a walk. In the fan of §15.3 the cell that blocked A's test is nearly always the cell
  that blocks the next sample's.

| claim | status |
|---|---|
| The largest value never falls during a walk (`comiss v, largest; jbe`), and "blocked" is "largest above both end multipliers" or "no cell": once it is above both, the rest of the walk cannot change `0x6675E0`'s answer | [verified, §15.4]. A NaN never passes our test (ordered compares), so NaN cases walk whole |
| A hint's cell is evaluated only when every box from the root down passes: the condition under which the game's walk enters it | [verified: the walk's only tests are final / box, `0x663C95..0x663DE3`] |
| ... unless the game's walk ended before it at the depth stop | [inferred not to happen]: a cell not final at level 12 makes the game step into a 14th cursor, which lies on the walk's own return address (13 cursors of 0x24 bytes from `esp+0x7C`, frame 0x250); the game would crash one level before the stop. Our walk keeps the stop all the same; shadow mode would show a difference |
| The callback and the evaluator under it leave no lasting write except the context's class word `+0x4C`, written only when it is 2 or more, not 13, and not below the cell's class | [verified here for the class word: decompile of `0x66B090`; the rest is §14.2's list, backed by s212's 4.3 M equal replays]. **A query whose class word is 2 or more and not 13 is passed to the game untouched** (`scNoCut`), so skipping or reordering evaluations changes nothing |
| A hint holds nothing of the tree but child numbers; a stale hint (tree changed, another tree at the same address) fails a box test, ends on a non-final cell, or finds a cell that does not block | [by construction; host test with trees that change between queries] |
| Flush and pin are done as in `0x667350`, before anything is read | [verified against `0x667357..0x667367`] |
| m0 and m1 are read from `0x6675E0`'s frame relative to the "largest" out pointer (`-0x10`, `-0x60`) | [verified: `0x667647`, `0x667655`, `0x66768C..0x667698`, `0x6676D9..0x6676F3`; those bytes are compared at install] |
| The least value on an early end is the least so far, not the walk's | does not matter: [verified] `0x6675E0` reads only the result and the largest |

Different paths: none. Household Sims: the same answers, so no scope rule; the switch is one for all.

Site and install: one `E8` retarget at `0x6676CE`, no address row. Found from the post pass's row `0x64E380` (its call of
pass 3 at `+0x48`, pass 3's segment test at `+0x426`, the lot walk at `+0xB8`, the lot test at `+0x189`) down to the lot test, then `+0x1D9` -> `0x6675E0`, `+0xEE` -> `0x667350`, whose `+0x7C` must
call the row `0x663B20` and whose `+0x3A` pushes the callback. Checked byte for byte: the two `fstp`, the out-pointer
pushes and the compare in `0x6675E0`; the head and `ret 0x24` of `0x667350`; in the walk the root arguments, the depth
compare, `ret 0x24`, and that its three final-test calls and two box-test calls agree; the final test's 24 bytes; both
`ret 0xC` of the callback. Anything else: `SKIP ... nothing written`. Calling conventions read from the disassembly:
`0x669C30` `ret 0x28`, `0x66A040` `ret 8`, `0x66A760` `ret 0x14` (result in `al` only), `0x669CB0` and `0x669BE0` `ret`,
`0x666A40` `ret` with `ecx`, the callback `ret 0xC` with `ecx` = tree.

Beside the others: no other hook of this repo sits on `0x6676CE` or on the chain above it (`[RouteAsync] TailUnlock` owns
passes 16-19 of the post pass, `[RouteJobPhases]` times its entry). On the contributor's branch it also runs beside
`[WorldPrecompute] SmoothMemo` and `Probe` (not in this repo).

Keys: `[WorldPrecompute] SmoothCut` 0 off (default), 1 serve, 2 shadow; `SmoothCutHints` 0-4 (default 4). Runtime switch
`native_set smoothcut` (export `ts3native_smoothcut` `+0x00`, docs/registry.md); the hook installs only with a non-zero ini
value. Shadow: ours runs, then the game's query; the game's result is returned; a whole walk must match in "saw a cell",
least and largest bit for bit, an early end must be a game "blocked"; else `scMismatch` and `SMOOTH CUT MISMATCH` (10
lines). Line, once a minute: `world precompute: smooth cut mode M ready 0xR: lot segment queries by our walk N (ms):
blocked by a remembered cell N (tries, cells reached), blocked at a cell in the walk N, walked whole N blocked + N clear
(cells evaluated in walks N); passed to the game N, with mode 0 N; shadow checks N mismatch N (the game's queries ms)`.
The two times are tick sums, converted once.

### 15.6 Expected [inferred; the shadow arm measures it]

- Touches the lot tests with `+0x72` clear: 3.1 s of 6.2 s of pass 3 in s195, so about half of pass 3 and 0.4 of the
  post pass (5.6 s a storm in s202): about 2.2 s a storm in reach.
- If two tests in three fail (§15.3) and the walk is 0.85 of a lot test: a cut alone saves half of a failing walk on
  average (the blocking cell comes at a random place in the walk's order): 0.67 x 0.85 x 0.5 = 0.28 of the lot test time,
  about 0.6 s a storm. With a hint hit on 70% of the failing tests (a hit costs 1-2 us against 23): 0.67 x 0.7 x 0.9 +
  the cut on the rest = about 0.5 of the lot test time, about 1.1 s a storm, a fifth of the post pass.
- Lower bound: tests that fail at an end cell or at different levels cost nothing today and gain nothing; clear tests
  pay up to four hint tries (about 1 us of 23). If most lot tests are clear, the gain is near zero and
  `SmoothCutHints=0` removes the tries.
- The split is in the line: `blocked by a remembered cell` + `blocked at a cell in the walk` against `walked whole`.
- Not touched: the world test `0x658A00` (2.2 s in s195). It ends at its first failing point already; its points are the
  segment's own midpoints, so nothing carries from one anchor sample to the next.

### 15.7 Designed, not built: fewer tests (other paths, NPC only)

The fan itself (two failing tests per 5-unit anchor step) is by the loop's design; an exact change is not possible
because every test is a new segment. For Sims the player does not control, behind its own switch:

- **Skip anchor samples that cannot help:** after a stop with G still the next node, step A to the node instead of one
  sample when the two failing tests were blocked by a cell that also blocks from the node's side (the hint cell is
  inside the triangle A, node, target). Paths differ only where the game would have found a shortcut from a mid-segment
  sample: they keep the corner at the node. Every committed segment is still tested clear by the game's test.
- **Gallop the probe:** test A to samples 1, 2, 4, 8, ... and bisect, instead of one by one: the clear tests of a long
  straightening drop from n to about 2 log n. The shortcut found is valid (tested) but can be longer or shorter than the
  greedy one where visibility along the path is not monotone.

Both need the loop of `0x64DD40` replaced (its cursors and `0x6443E0`), not a call-site hook. Decide after the `SmoothCut`
line shows clear against blocked tests: the first idea pays if blocked tests dominate after the cut, the second if clear
ones do. About 2-3 hours to build either with a host test, one storm each.

### 15.8 Checks (no game)

- `smoothcut-host-test.cpp` (Linux, 32-bit, freestanding; `smoothcut-test.h` as is): a fake quadtree with the game's
  layout and fake cell functions; the game's query written as a recursion, not as the loop under test; a fake `0x6675E0`
  with the frame slots. 4,000 random segments and anchor fans in serve mode against the game's query (same answer every
  time, clearly fewer evaluations, hints and cuts both used, flush and pin as the game's); shadow (the game's outputs
  bit for bit, no mismatch); a forced mismatch; the class word rule (12, 13, 1, 0); no context; other flags; a tree that
  changes between queries with the hints kept; two trees in turn; no hints; a pinned tree; a root that is the only cell;
  the depth stop; NaN and `FLT_MAX` multipliers. Twelve deliberate bugs (cut on one end only, cut at equal, a hint
  without its box test, a hint cell not checked, no depth stop, three children, no class word rule, no flush, pin not
  released, the wrong frame slot, shadow returning ours, the flag byte lost) each fail it; a hint for another tree
  passes, as it must.
- clang-cl `/Zs /W4` with the xwin headers on `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors,
  no warning in `smoothcut*`. `node tools/selftest-all.ts --ci` pass. The MSVC build and the Windows selftest are the CI
  `native` job.

### 15.9 Repro

`ip-smoothcut-storm` (repros.json): ini `SmoothCut=2`; arms off, shadow, serve by `native_set smoothcut`. Shadow arm passes
with mismatch 0 and no `SMOOTH CUT MISMATCH`; its line gives our time against the game's on the same queries. Serve arm:
the post pass time (`[RouteJobPhases]`) against the off arm.

### 15.10 Results [measured: Isla Paradiso, 2-minute route storms, every proven fix on; Steam Deck, Linux Wine + DXVK]

| run | mode | result |
|---|---|---|
| s216 | shadow | 218,969 lot segment queries compared, **mismatch 0**; our walk 1,810 ms against the game's 2,218 ms on the same queries |
| s217 | serve | post pass 5,248 ms against 5,634 ms in the baseline s202; 0 hitch frames (>= 300 ms); failed plans 95 a minute |
| s222 | serve (rerun) | post pass **4,887 ms (-13% against s202)**; 0 hitch frames; failed plans 66 a minute |

- The failed-plan rate of s217 was run-to-run noise: s216 (shadow, the game's answers) had 62 a minute and the s222 rerun
  66 [measured]. SmoothCut gives the game's answer for every query (§15.5), so it cannot change which plans fail.
- One run per arm; the post pass saving (0.4-0.7 s a storm) is near the low end of §15.6's estimate [measured vs inferred].
- Not measured here: the share of hint hits against cuts per storm beyond the minute lines; other worlds.

Files: `native/ts3native/smoothcut-core.h`, `smoothcut.cpp`, `smoothcut-test.h`, `smoothcut-host-test.cpp` (new);
`ts3native.cpp` (include, install call, counter line), `selftest.cpp`; `ts3native.ini`, `tools/ini-sections.json`,
`tools/harness/native-switches.ts`, `tools/harness/repros.json`, `docs/registry.md`, `docs/68-hook-conflict-map.md`,
`docs/index.md`.
