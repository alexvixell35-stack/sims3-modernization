# 663: Route speed: an exact neighbour memo for the cell A*, and a finer split of "rest"

2026-11-19. Static work (Ghidra decompile, capstone disassembly, CallerTree) plus the `route candidate split` logs of 11
runs today. Built, default off. Branch `feat/route-speed`.

**Upstream carve-out (2026-11-20, PR branch `post/route-storm-1120`):** only `[RouteSpeed] NeighbourMemo` (§4) comes over.
The `[RouteJobPhases] CandidateSplit` split of §1-§2 (plancand.cpp, docs/644) is not in this tree, so the four new split
sites (§2) are left out; §1-§2 stay as the measurement that motivated the memo. The router scope NeighbourMemo needs (§4.4,
household rule) is read from the plan-deadline session (`PdSess.scope`, set at its open) or the worker job's `t_hcScope`
(`NmScopeNow` in routespeed.cpp; the combo read it through plancand.cpp's `PccScopeNow`, same rule).

**In game (2026-11-20, docs/668):** only as part of the route storm "on" bundle, never alone. s143 shadow (`NeighbourMemo=2`):
105,102 shadow checks, **0 mismatches**, 0 `NEIGHBOUR MEMO MISMATCH` lines; s146 serve (`=1`): 2,681,038 calls, 16,761 hits
(73,766 cells served), 1,912 not served for scope (household / unknown routers); serve mode does not compare.
How much of the bundle's hitch gain is this memo's is not measured.

Goal: less CPU per route search, with every result identical to the game's. This is a pure speedup with no gameplay
change, so by the household rule (CLAUDE.md) it may apply to every Sim. Until a shadow run shows 0 mismatches, it serves
NPC routers only.

## 1. Where the time goes (2026-11-19, `[RouteJobPhases] CandidateSplit=1`)

Source: the `route candidate split` lines (docs/644) of the 11 runs `phase-20261119-*` that had the split on. The 60 s
totals lines are cumulative, so each run's last line was used. Measured (verified, log data).

All `PathPlanner_PlanHierarchical 0x65E0D0` calls: 186,625 calls, 1,151,595 ms in total.

| phase | ms | share | calls | per call |
|---|---|---|---|---|
| rest (own code, world A*, water, post pass, insert, allocator) | 681,930 | 59.2% | | |
| cell (in-room cell A* `0x61E500`) | 216,685 | 18.8% | 287,458 | 754 us |
| lotPlan `0x6410B0` (self) | 143,642 | 12.5% | 164,547 | 873 us |
| lotSearch `0x640E00` | 87,364 | 7.6% | 49,292 | 1,772 us |
| shore `0x6443A0` | 12,153 | 1.1% | 1,192,677 | 10 us |
| gather / closestWps / choose | 5,515 / 3,741 / 565 | 0.9% | | |
| sameLand / sameWater | 0 | 0% | | |

Slow plans (lines >= 100 ms, n = 1,283, total 204,232 ms, p95 282 ms):

| phase | total ms | p95 ms |
|---|---|---|
| rest | 139,921 | 253 |
| cell | 45,295 (9,322 searches, in 813 plans) | 166 |
| lotSearch | 9,835 | 49 |
| lotPlan | 5,647 | 25 |
| shore | 1,292 | 3 |

**By scope (slow lines):**

| scope | n | total ms | p95 | cell ms | rest ms |
|---|---|---|---|---|---|
| unseen | 1,018 | 161,129 | 280 | 29,212 | 117,371 |
| seen NPC | 170 | 26,084 | 516 | 9,213 | 14,356 |
| household or seen | 92 | 16,574 | 440 | 6,759 | 7,872 |

**By kind:**
- Boat or water plans (sameWater calls > 0): n = 78, 13,234 ms, p95 281, almost all of it rest (12,796).
- Land plans: n = 1,205, 190,998 ms, p95 284, of which cell is 45,167.

**Other findings:**
- Failed plans (code 5, no path): n = 94, 27,760 ms, p95 603. Their cell time is 17,477 ms (p95 585), from about 46
  lot legs per plan. Failing floods re-expand whole rooms.
- Lot legs by call site (inclusive ms): `65F582` 19,709, `65E3C2` 15,262, `660D83` 11,330, `65F11C` 3,669.
- 145 of 1,074 route hashes repeat (max 53), but whole-plan memo work belongs to NoPathMemo (docs/643, another branch).

The coordinator's sample near hitches (124 plans, 31.2 s) is: rest 48.6%, cell 44.4%, lotPlan 3.0%, lotSearch 2.8%,
shore 0.4%. So the two targets are the cell searches and "rest".

## 2. What "rest" is, and the split added here

"Rest" is PlanHierarchical's time outside the timed helpers. Before this change it held all of these:
- world A* `0x676260`;
- WaterPath_Plan `0x671930`;
- the path-node post pass `0x64E380` (its per-node height raycasts);
- PathNodes_Insert `0x629140`;
- EA's allocator;
- the planner's own code.

docs/150 §2 (sampled) put the shares at: post pass 19.3%, PathNodes_Insert 12.9%, world A* 10.5%, allocator 27.7%.

`CandidateSplit=1` now also times four sites as self time: `world`, `water`, `post` and `insert`. The bytes and `ret`
sizes were checked: `0x676260` ret 0x1C, `0x671930` ret 0x24, `0x64E380` ret 0x14, `0x629140` ret 8.

How each site is chained:
- **world:** inside `[RouteEarlyOut]`'s pointer (`g_eoAsOrig`) when that is on, else an own detour.
- **water:** inside the plan-deadline stub (`g_pdOrig[1]`), or after the water goal check (`g_wgcNext`), else an own
  detour. It is installed last.
- **post and insert:** own detours.
- `[RouteJobPhases]` (routephases.cpp `RjpOwner`) chains through these owners as well.

After this change, "rest" is only the own code, the allocator and the unhooked helpers. The next CandidateSplit run
gives the real split; it has not been measured yet.

**Which of world A* and water can be cached or sped up while staying identical** (asked by the coordinator):

- **World A\* `0x676260`: not cacheable across calls.**
  - Its search context depends on the exact start and goal points through the circle centre and the heuristic
    (docs/99 §2.3-2.4), so two Sims never share an answer.
  - Repeat failing searches of one request are already proven and answered by `[RouteEarlyOut]` Mode=2 (docs/142 §9).
  - Within one call, an ALT heuristic or JPS would change the expansion order, so equal-cost ties could give a different
    path. Not identical.
- **Water `0x671930`: same as world A\*.**
  - Point-exact inputs.
  - Its one provable shortcut is the "unreachable goal tier" reject, which is already built (`WaterGoalCheck`, docs/644).
- **The post pass `0x64E380`: the exact win in "rest" is already built.**
  - Its height raycasts are what `[HeightCache]` (`0xC2A570`) memoises. It was verified at 0 mismatches over 4.41M
    calls, but it is `Enabled=0` in today's runs.
  - **Recommendation:** turn `[HeightCache] Enabled=1` on in the next timing ini. That is an ini change only, with no new
    code.
- **PathNodes_Insert and the allocator:** no exact cache. They are writes, not lookups.

## 3. What is already cached or cut (so not redone here)

| existing | what it saves | doc |
|---|---|---|
| `[CellMemoProbe]` | probe only: design M (a within-plan leg memo) would hit 0.003 ms/s. Dropped. | docs/150 §12 |
| `CandidateCut` / `WaterGoalCheck` | NPC hopeless candidates, unreachable water goals | docs/644 |
| `[RouteEarlyOut]` | failing world A* searches answered by proof | docs/142 |
| `NoPathMemo` | whole failed plans, per world version | docs/643 |
| `[HeightCache]` | the post pass's height raycasts (exact, but off) | heightcache.cpp |

None of these touches the work inside one cell A*. docs/150 §8.3 proposed design **M2**, a neighbour-list memo, but it
was never built. This doc builds it.

## 4. `[RouteSpeed] NeighbourMemo` (built)

### 4.1 The repeated work (verified, decompile + disassembly)

The cell A* `0x61E500` expands each popped cell through `0x66BFD0`, which calls `QuadCell_Neighbours 0x66B9C0`.

Call shape: thiscall, ecx = cell object; stack args (Q, out vector, path vector, records vector); `ret 0x10`; prologue
`83 EC 24 56 57`.

What it does:
1. Releases `out`'s elements with `0x61BCE0` (begin, end; `ret 8`; `--` at +0x28 and a vtable free at 0).
2. Empties `out`.
3. For each of the four sides: climbs the quadtree (`0x66B8D0`), mirrors down (`0x66AC60`) and collects the final cells
   (`0x66B6E0`).
4. Finds each cell in Q's cell table (`0x664590`), or creates it there (`0x61C2C0`).
5. Pushes it into `out`, taking one reference (+0x28).

What it reads:
- the cell's position record (+0x08 parent, +0x0C node, +0x1C shape, +0x20 quadrant bits);
- the quadtree nodes' kind, child, parent and final-mask words;
- Q's table.

It never reads the agent, costs, the goal or the open list.

Each plan re-runs the cell A* many times over the same room (lot legs, candidates, failing floods), so the same cell's
list is rebuilt again and again.

### 4.2 Why a memo is exact

- **Cells live in Q's table until it is cleared.** The table holds a reference on each cell (`0x61C270` +1). Cells are
  dropped only by the table clear `0x61C020`, which has two verified callers: the Q reset `0x61E320` (called by
  PlanHierarchical at `0x65E345`, every plan) and Q's init path `0x61E420`.
- **So within one clear-to-clear window and one quadtree state, `(cell, Q)` always has the same list**, made of the same
  cell objects in the same order. A second call creates nothing, because the first call created every cell on the list.
- **The key** is (Q, cell pointer, the cell's key words +0x30/+0x34). A cell freed and reused at the same address with
  another key misses.
- **Invalidation:**
  - a clear sequence, bumped by an own stub on `0x61C020` (chained after `[RouteGate]`'s site-9 stub when that owns the
    entry);
  - a tree sequence `g_snTreeSeq`, bumped at the top of `[RouteSnap]` `SnEnter` for its writer rows W1-W5, RemoveLot and
    ApplyLotUpdate. It is bumped on any thread, before SnEnter's replay and clone early returns, so those writes count
    too.
  - The memo refuses to install unless RouteSnap counting is on with no skipped row. The NeighbourMemo key now turns
    RouteSnap counting on.
- **What a served answer does:** it releases `out` with the game's own `0x61BCE0`, writes the kept list and adds one
  reference per cell, exactly as the game's push does.
- **Capacity:** it serves only if `out` already has the capacity. Otherwise the game's function runs, because the push
  could reallocate through EA's allocator.
- **The two scratch vectors** keep the last real call's contents. Only `0x66B8D0`, `0x66AC60` and `0x66B6E0` use them,
  and each clears them on entry (verified).

### 4.3 Bounds

**Memory:**
- One memo per planning thread, at most 4; a fifth thread runs the game's function.
- Each memo is 246 KB: 4,096 slots with 8 probes, and a pool of 32,768 words.
- A list is kept only if it has at most 32 cells and no null cell.
- A full pool or a full probe run stores nothing; it is counted, not evicted.

**Time to live:** entries last until the next plan's Q reset, or until a quadtree write. The whole memo is cleared in
O(1) by a generation bump.

### 4.4 Switches, counters, logs

**ini:** `[RouteSpeed]`
- `NeighbourMemo`: 0 off (default), 1 serve, 2 shadow.
- `NeighbourMemoAll`: default 0, so only unseen and seen-NPC routers (`NmScopeNow` 2 or 3) take a served answer.
  `[Experimental] HouseholdToo=1` also lifts this, since a served answer is a cached, realistic one.

**Runtime switch:** export `ts3native_routespeed` +0x00 (`native_set neighbourmemo`, registered in
`tools/harness/native-switches.ts`).

**Counters:** native, in the export, with no ts3fixes slot:

| offset | counter |
|---|---|
| +0x00 | mode |
| +0x04 | ready bits |
| +0x08 | calls |
| +0x0C | hits |
| +0x10 | cells served |
| +0x14 | stores |
| +0x18 | resets |
| +0x1C | no room |
| +0x20 | scope |
| +0x24 | not kept |
| +0x28 | shadow checks |
| +0x2C | mismatch (must be 0) |
| +0x30 | no memo |
| +0x34 / +0x38 | sampled uncached call time and its count (every 64th miss) |
| +0x3C | log lines |

**Logs:**
- one line a minute: `route speed: neighbour memo ...`, including the estimate `saved ~N ms`, which is hits times the
  sampled cost;
- `NEIGHBOUR MEMO MISMATCH`, at most 10 lines.

**EA 1.69:** `0x66B9C0` and `0x61BCE0` have no 1.69 address yet (0 rows). The feature logs SKIP there.

### 4.5 Expected saving (inferred, to be measured)

docs/98 §6 and docs/150 §2 put neighbour generation at about a quarter to a third of the cell A*'s self time. The quad
climb and table finds run per side, and the open list and costs are the rest.

A hit costs one hash probe and a copy of at most 32 words, against four climb, mirror and collect walks plus table
finds. Within a plan, most expansions after the first search of a room are repeats (lot legs at the 4 call sites
re-enter the same rooms).

The estimate is **15-35% of cell time**: about 30-75 s of today's 216,685 ms, and 7-16% of a hitch plan's time near the
coordinator's 44.4% cell share. The memo's own `saved ~ms` and the split's cell per-call ms give the real number.

## 5. Tests and build

- **Selftest `routespeed-test.h` (`TestRouteSpeed`):**
  - the core table: keys, sequences, generation wrap, pool, probe, null and long lists, the serve rule;
  - `NmNbHook` on fake cells with a fake game function and a fake release: a served answer gives the same cells, order
    and references; a clear or a tree write drops the memo; another Q, a changed key, no room and a household router
    each run the game's function; shadow counts a mismatch.
- **Freestanding host test `routespeed-host-test.cpp`:** clang, no libc. Passes (exit 0) on Linux.
- **clang-cl `/Zs /W4`:** ts3native.cpp passes for both Steam and `/DTS3A_TABLE`; selftest.cpp passes for Steam (the
  selftest's EA-table errors are unrelated, in monojit-test.h, and were there before this change). The game build must
  come from MSVC CI, because clang builds hit the SEH limit.
- **Repro `ip-neighbourmemo-crowd`** (Isla Paradiso): off/on/on/off by `native_set neighbourmemo`, with job ini
  `NeighbourMemo=2`. Run it in shadow first. Read the `route speed: neighbour memo` line per arm.

## 6. Prior art (credited)

- **TS3 modding:** no public project speeds up TS3's native pathfinder. Searched on GitHub: "sims3 routing",
  "ts3 pathfinding", "sims 3 route fix" and code searches; none of the results was TS3 routing work.
  - LazyDuchess Smooth Patch (https://github.com/LazyDuchess/TS3PatchLauncher) raises the simulation tick rate, not A*
    cost.
  - OhRudi "Sims need less Space" / "Pets need less space" and TSS ToddlerRoutingFix (in the Mods folder; no readme)
    change routing footprints and toddler routing, which fixes blocked paths, not CPU cost. Inferred from their names.
  - NRaas (Overwatch `StuckCheck.cs` on `Route.PostPlanCallback`, GoHere door filters, `SimRoutingComponentEx`) treats
    stuck Sims and adds filters; it caches nothing.
- **Memoisation:** Michie, "Memo functions and machine learning", Nature 1968. A successor memo is exact when the key
  covers every input and the memo is cleared on every change: the method used here.
- **Techniques considered and rejected for exactness:**
  - HPA* (Botea, Müller, Schaeffer, "Near Optimal Hierarchical Path-Finding", JOGD 2004): near-optimal, with a different
    search graph.
  - LPA* / D* Lite (Koenig and Likhachev, AIJ 2004 / AAAI 2002): needs to own the open list, which a hook cannot do, and
    equal-cost ties can produce different paths.
  - ALT (Goldberg and Harrelson, SODA 2005): a different heuristic changes the tie order.
  - JPS (Harabor and Grastien, AAAI 2011): prunes symmetric paths, so it returns a different equal-cost path.
  - Sub-path splicing changes results; only whole-query caching with an exact key is exact, which is NoPathMemo's ground.

## 7. Files

- `native/ts3native/routespeed-core.h`, `routespeed.cpp`, `routespeed-test.h`, `routespeed-host-test.cpp` (new).
- `routesnap.cpp`: `g_snTreeSeq` and `SnTreeRow`; NeighbourMemo added to ApplyRouteSnap's enable condition.
- `plandeadline.cpp`: `PdSess.scope` (the router scope at the session's open; from docs/644, which is not in this tree).
- `ts3native.cpp`: include, install, and the 60 s log line.
- `ts3native.ini`: `[RouteSpeed]`.
- `ts3addr-ea169.h`: two unresolved rows.
- `tools/ini-sections.json`, `tools/harness/native-switches.ts`, `tools/harness/repros.json`.
- Not touched: `nopathmemo.cpp`, `earlyout.cpp`.
