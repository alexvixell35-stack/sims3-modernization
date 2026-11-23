# 667: Lot phases of the route planner: the time is the door ask, not a search; and an exact memo for failed floods

> **Code status (2026-11-23):** `[RouteSpeed] FloodMemo` (the `floodmemo*` files named below) is not on main; it is posted separately, default off (docs/680: ran, never acted). `tools/harness/lot-route-report.ts` comes with this doc.

2026-11-19. Sources: the s140 profiling run (docs/666 §3) of Isla Paradiso, `<runs>/phase-20261119-2238/ts3native-full.log`
(CI build 44626443641, `[RouteJobPhases] CandidateSplit=1` with the world / water / post / insert sites, 23 min), the earlier
Isla Paradiso run `phase-20261119-2149`, ts3native source at combo 87921903, and the Steam 1.67 exe (Ghidra decompile and
objdump disassembly, local only). Built, default off, **NOT RUN**. Branch `feat/lot-route-speed`.

The question was: Isla Paradiso's slow plans spend 37% in lotSearch and 22% in lotPlan, so which lots and call sites carry
that, why does one LotPlan take 100-500 ms, and what exact cache removes it. The answer changes the question:

- **The lot phases' time is not a search.** On the route worker it is the wait for the sim thread to run the managed door
  ask (§2). On the sim thread the lot phases cost about 1% of a slow plan. No lot cache would save it; three switches that
  already exist do (§5).
- **The real CPU cost in failed plans is the cell A\*, repeated per candidate** (§3). That part gets an exact memo here:
  `[RouteSpeed] FloodMemo` (§4).
- The split gets a `host` column so the next run shows the wait as what it is (§2).

## 1. Ranking [measured, `tools/harness/lot-route-report.ts`]

`node tools/harness/lot-route-report.ts <log or run dir>` ranks the split lines by phase, scope, result, thread, lot, Sim,
interaction and LotPlan call site, joins each worker plan with its job line, and lists repeats.

**Every PlanHierarchical call** (the cumulative totals line: 15,524 calls, 229,430 ms):

| phase | self ms | share | calls |
|---|---|---|---|
| lotPlan `0x6410B0` | 54,981 | 24.0% | 16,302 |
| lotSearch `0x640E00` | 46,718 | 20.4% | 10,437 |
| world A\* | 36,138 | 15.8% | 7,733 |
| post pass | 30,871 | 13.5% | 13,792 |
| cell A\* | 27,960 | 12.2% | 27,370 |
| water | 22,821 | 9.9% | 1,021 |
| rest (own code, allocator) | 5,670 | 2.5% | |

"Rest" is 2.5% with the docs/663 sites, so the split is now complete.

**Slow plans** (split lines >= 100 ms, at most 20 a minute, so a sample: 503 plans, 84,899 ms):

| scope, result, thread | plans | ms | share |
|---|---|---|---|
| unseen, ok, worker | 226 | 36,158 | 42.6% |
| unseen, failed (code 5), worker | 135 | 27,073 | 31.9% |
| seen NPC, ok, worker | 74 | 11,519 | 13.6% |
| unseen, ok, sync | 20 | 2,643 | 3.1% |
| unseen, code 4, worker | 10 | 1,707 | 2.0% |
| **household or seen, ok, worker** | 11 | 1,665 | **2.0%** |
| seen NPC, ok, sync | 15 | 1,569 | 1.8% |
| unseen, failed, sync | 5 | 1,559 | 1.8% |

- **Household share:** 11 plans, 1,665 ms, 2.0% of the slow-plan time. All succeeded, all on the worker, lotPlan the largest
  phase in each (the door-ask wait of §2).
- **Largest phase:** lotSearch in 183 plans (86 of them failed unseen worker plans, 18,789 ms), world in 104, lotPlan in 111,
  cell in 51, water in 43.
- **LotPlan call sites** (inclusive ms): `65E3C2` 13,927 ms in 98 calls (5,404 ms in failed plans), `65F582` 6,557 ms in 546
  calls, `65F11C` 3,352 ms in 303 calls, `660D83` 63 ms, `660794` 1 ms. `65E3C2` is one call per plan at 142 ms a call.
- **lotSearch calls per plan:** 1 call in 76 plans (9,082 ms), 2-4 calls in 182 plans (22,083 ms), more than 20 calls in 9
  plans (1,018 ms). The time does not grow with the call count.
- **Lots and lot types:** the split line has no lot id. The Sim's lot comes from ts3fixes' `ufplansplit` line and is known for
  75 of the 503 plans. Among those, lotSearch time is led by ordinary land homes: Luxury Villa 3,345 ms in 13 plans (three
  Sims of one family), The Red Palace 1,319 ms, Colonial Casa 630 ms, Hobart's Hideaway 553 ms. No houseboat,
  resort, dive lot or port leads. By interaction: VisitCommunityLot, FollowerMeetUpAtPoint, VisitLot, WorkInRabbitHole,
  GoToLot. The pattern is "a townie leaves a lot with doors", not a water or houseboat search [measured on the 75; the other
  428 plans have no lot in the logs].
- **Repeats:** 53 of 438 route handles have more than one slow plan (108 plans, 19,079 ms, 25 of them failed, at most 3 on
  one route). The same (lot, start, goal) search cannot be read from the logs; the failed sync plans repeat exactly (§3).
- **Fail share:** 140 of 503 slow plans end in code 5 (33.7% of the time). Of the 92 failed worker plans with a job line, **92
  were cut at their job deadline** (`cut (UnseenFallback ...)`, deadline 220 ms). They are not searches that found no path.

**Bridgeport, preliminary** (the s140 Bridgeport session was still running; a snapshot of its live log after about 15 min,
22,887 calls, 136,061 ms) [measured]: lotPlan 60,608 ms (44.5%), post 26,681 ms (19.6%), lotSearch 16,355 ms (12.0%), cell
14,669 ms (10.8%), world 6,392 ms, water 5 ms. Slow plans: 116, all on the worker; household or seen 7 plans, 923 ms (5.4%).
22 of the 23 failed worker plans with a job line were deadline cuts. LotPlan site `65E3C2`: 7,250 ms in 58 calls. No slow
sync plan and no failed plan of the §3 shape in the snapshot. So the lot phases are the same wait there, larger in share,
and the failed-flood pattern of §3 is an Isla Paradiso (boat and houseboat) pattern so far. Rerun the tool on the full
Bridgeport log when the run has copied it.

## 2. Root cause of the lot phases: the managed door ask [verified]

**The call chain** [verified, decompile of `0x640E00`, `0x63EC40`, `0x6410B0`, `0x632460`; docs/92, docs/159]:

- `LotSearch_Entry 0x640E00` finds the room of the start point (`0x633FA0`), then runs `LotPortalSearch 0x63EC40`: a Dijkstra
  over the lot's room / portal graph that collects which lot-edge groups the start room reaches. It has no early exit; it
  runs until its open list is empty. Each expanded edge calls `PortalEdgeCost 0x634500`.
- `LotPlan 0x6410B0` -> `LotPlan_Inner 0x63F6E0` -> portal A\* `0x63E340` also calls `PortalEdgeCost` per edge, and every
  in-room wrapper refreshes the start room's portal locks first (`0x632460`).
- `PortalEdgeCost` and the refresh ask `Portal_LockState 0x64F2C0`, which fires the managed query at `0x64F4D3` (event 59,
  "may this Sim pass this door"). docs/92 found this to be the only managed entry under `Route_PlanLocked`.

**What the timer measured.** The split's lotSearch and lotPlan are self times, and the door ask was inside them:

- On the route worker the ask is proxied to the sim thread (`Proxy`, ts3native.cpp) and the worker waits until the sim
  thread reaches a serve site. The job line carries that wait as `host`.
- For the 153 worker plans with a job line: lotSearch + lotPlan self = 25,421 ms, host wait = 35,254 ms; in 95 of them the
  two agree within 15% [measured]. Example: `PlanHierarchical 294 ms ... lotSearch 293/2` with `plan 294 host 286`.
- On the sim thread there is no proxy. The 40 slow sync plans spent **71 ms of 5,771 ms** in lotSearch + lotPlan [measured].
- 296,422 host calls in 15,106 plans: about 20 managed calls a plan.

So "one LotPlan call takes 182 ms and then fails" is a worker waiting for the sim thread, past its 220 ms job deadline, and
then being cut. The answers to the brief's three questions: no unbounded flood on water or houseboat lots; the missing
early-out is in `0x63EC40`, but its cost is microseconds without the ask; the search is not restarted per candidate.

**What it costs.** The wait is not sim-thread CPU. The cost is late and cut NPC routes: 92 plans in 23 min reported as
failed that were cut while waiting, not proven to have no path [measured].

**The split's new `host` column (built).** A managed call under the planner is now its own timed frame, opened in the
host-call hook (`HostCallHook`, both branches: the worker's proxy wait, and the script itself on the sim thread). Its time
leaves the caller's self time. The line gains `, host N/M` after `insert`; `route-stutter-report.ts` and
`lot-route-report.ts` read it without a change.

## 3. The real search cost: one failing flood per candidate [measured + verified]

The failed **sync** plans are a different shape. Five in this run, identical:

`PlanHierarchical 304-322 ms code 5, sync, scope unseen: lotSearch 0/1, cell 286-298/95, lotPlan 8-11/48, world 0/94 ...
lot legs by call 65F582 296-312/48`

and in `phase-20261119-2149` one Sim on a houseboat lot failed 54 sync plans of about 550 ms each, of this shape:
`cell 498/136, lotPlan 38/68, closestWps 0/71, choose 4/68 ... lot legs by call 65F582 536/68`.

- The planner tries 48 or 68 candidates. Each calls LotPlan from `0x65F582`, which runs the in-room cell A\* `0x61E500` about
  twice (95 searches for 48 calls, 136 for 68) [measured; the second one is LotPlan's retry per goal room, docs/11, inferred].
- Each search fails only after it has expanded everything its start can reach: about 3 ms a search, 95-136 times.
- docs/663 §1 measured the same across 11 runs: failed plans' cell time 17,477 ms of 27,760 ms, about 46 lot legs a plan.
- `[CellMemoProbe]` (docs/150 §12) found no repeats because it keyed on the whole leg (start and goal); here the goal differs
  per candidate and only the failing flood repeats.

`[RouteAsync] NoPathMemo` (docs/643, off in both runs) answers the second and later plans of such a route for NPC routers.
Nothing removed the cost inside the first plan, and nothing exact existed for household routers. §4 does both.

## 4. `[RouteSpeed] FloodMemo` (built)

### 4.1 The rule

A failed cell A\* that ended with an empty open list has popped every node it could open. Those cells are the closure C of
its start. A later search with the same context and unchanged routing data whose start cell is in C can open only cells of
C. If no cell of C carries a goal, it fails too. The memo answers that search with the game's own failure exit at its first
pop.

### 4.2 Why it is exact [verified, docs/98 §5-§8 and disassembly]

- **What decides whether an edge opens a node.** `AStarSearch_NodeState 0x61E030` rejects a node only when the edge cost or
  the sum is >= INF. The cost is infinite when a cell's multiplier is not finite or the type gate fires (docs/98 §7.1). Both
  are functions of the cell's footprint entries, the context flags `X+0x50`, `X+0x4C` and the agent. The target, the
  threshold, the goals and the path so far change only finite magnitudes. Multipliers are at most 100,000 and lengths are
  lot-sized, so no sum reaches INF (1.7e38).
- **Failure with an empty open list = closure.** The loop `0x61E640..0x61E7D3` pops until the list is empty, the best goal is
  good enough, or the node cap is reached. A search that popped no goal and emptied the list has closed its whole closure.
  The memo keeps a flood only when `[[Q+0x18]+0x18] < 0` at the exit (the loop's own "list empty" test), so a cap exit or a
  deadline cut is never kept.
- **Goals.** A goal is a separate node linked from the node of its cell (explicit neighbours, node `+0x08..+0x0C`, relaxed
  by `0x61AF40` after the quadtree neighbours). The memo serves only when no node of C has an explicit link other than the
  new start node. Any goal on a cell of C, disabled or not, runs the search.
- **The start.** Exactly one start node (`[startSet+0x14] == 1`, the two wrappers' shape) whose only link is its cell node.
- **The served exit is the game's.** The search starts as always (`0x61E360`: generation + 1, target, start node opened) and
  pops the start node. At the node-cap test `0x61E76E` the stub jumps to `0x61E59C`, the function's own "open list empty"
  exit: it frees its two local vectors, releases the popped node, sets `[Q+0x1C] = -1`, returns AL 0, `ret 0x18`. This is
  the exit `[RouteAsync] CandidateCut` already uses (docs/644 §8). With one start node the open list is empty there, as
  after the full flood.
- **What differs after a served search** is state nothing reads:
  - `Q+0x24`, the inserted-node count (1 instead of the flood's size). `0x61E539` zeroes it at the next search's entry; the
    two wrappers do not read it [verified decompile].
  - The stamps, parents and entry points of the flood's nodes. Every search resets a node through its generation stamp
    before reading it (`0x61E030` step 1). A stale stamp could only alias after 65,536 searches inside one Q table
    generation, which the unpatched game is exposed to in the same way [inferred: no plan runs that many].
  - Cells the flood would have created lazily in Q's table. A later search creates them the same way.

### 4.3 What a record depends on, and what drops it

| input | how it is covered |
|---|---|
| the cells and nodes of C | they live in Q's cell table until its clear `0x61C020` (docs/663 §4.2); a cell gets its node when it is made (`0x61BEC0`). The clear sequence drops every record, and each serve re-checks `cell+0x38 == node`. |
| footprints, quadtree, lot structure, portals | `g_snWriteSeq` (new, routesnap.cpp): bumped on every `[RouteSnap]` writer row except the two polygon cache "ensure" entries (W6a `0x80E5B0`, W6b `0x80E610`: called on every read; a real fill passes the other W6 rows, which count), on `Footprint_SetEnabled`, and on every routing IC except the four plan starts, on any thread. The wrappers' portal lock refresh `0x632460` rewrites through `0x666C50` -> W1 `0x665C50` [verified], so it counts. |
| a script changing anything else | a managed call on the planning thread bumps `t_fmHostSeq` (host-call and runtime-invoke hooks) and drops the records. `FloodMemoHostKeep=1` turns this rule off, to measure what it costs. |
| the search context | the key: Q, the lot object `X+0x54`, the options object `X+0x40` and a hash of its first 0x38 bytes (ids `+0x08/+0x0C`, option words `+0x28..+0x34`), `X+0x50`, `X+0x4C`. A flood during which any of these or any sequence moved is not kept. |

**Open point [inferred].** The footprint class also reads the agent record the routing world keeps for the options object
(`0x8294E0`: ignore list, lock keys). It has no cheap hash. It is covered by the two sequences on the assumption that only
script changes it. The shadow mode measures this: `fmMismatch` must stay 0.

### 4.4 Scope

- The served answer is the game's own failure, so the rule holds for every router.
- Default: only routers of known NPC scope (unseen, seen NPC) take a served answer. Others run every search; their floods
  are still recorded and checked (`fmChecks`).
- `FloodMemoAll=1` (or `[Experimental] HouseholdToo=1`) adds household, selected and unknown routers, **except a plan that
  may be a player command**. Such a plan is served only while ts3fixes marks commands (NoPathMemo's op 39, switch
  `npmhousehold`, docs/643 §7) and the plan's route handle is known and not marked. A player-commanded Go Here always runs
  every search.

### 4.5 Bounds

- One block per planning thread, 0.6 MB, at most 4 threads. A fifth thread runs every search.
- 8 records, 16,384 cells in all, a flood of at most 8,192 cells. A flood that does not fit is not kept (counted).
- A search that is not looked at costs one thread-local read and one compare. A recorded search costs one store per pop.
- A hit costs one hash probe plus one pass over the record's cells (two reads a cell) against a flood of the same cells.

### 4.6 Switches, counters, logs

**ini** `[RouteSpeed]`: `FloodMemo` 0 off (default), 1 serve, 2 shadow; `FloodMemoAll` (default 0); `FloodMemoHostKeep`
(default 0). Needs `[RouteAsync] Enabled=1` (the host call detour) and turns `[RouteSnap]` counting on. It installs the cell
A\* entry hook and the loop stub of plancand.cpp even with CandidateCut and CandidateSplit off.

**Runtime switch:** export `ts3native_floodmemo` +0x00 (`native_set floodmemo`).

**Counters** (native export, no ts3fixes slot): +0x00 mode, +0x04 ready bits, +0x08 searches, +0x0C hits, +0x10 stores,
+0x14 resets, +0x18 resets by a managed call, +0x1C goal link, +0x20 scope, +0x24 command, +0x28 not kept, +0x2C checks,
+0x30 mismatch (must stay 0), +0x34 no memo, +0x38 / +0x3C kept flood us / n, +0x40 saved us (estimate), +0x44 log lines,
+0x48 shape, +0x4C cells kept.

**Logs:** one line a minute `route speed: flood memo ...`; `FLOOD MEMO MISMATCH`, at most 10 lines.

**EA 1.69:** plancand.cpp's sites have no 1.69 address; the feature logs SKIP there.

### 4.7 Expected saving [inferred, to be measured]

- A failed sync plan of the §3 shape: 95 searches become 1 or 2 floods plus 93 checks, about 300 ms -> under 20 ms; the
  houseboat shape 550 ms -> under 30 ms. In `phase-20261119-2149` that is 54 sim-thread stalls of 0.55 s (30 s) in one run.
- Over the 11 runs of docs/663: most of the 17,477 ms of failed plans' cell time.
- The hit rate depends on door asks between candidates, because each one drops the records. `fmHostResets` shows it; if it
  is high, `FloodMemoHostKeep=1` in a shadow run tells whether the stricter rule is needed.

## 5. What to turn on for the lot phases (no new code)

The wait of §2 is already addressed by three default-off switches that were off in s140 (`host-call split and door memo off`,
`TaskServe=0` in the log):

| switch | what it does | state |
|---|---|---|
| `[RouteAsync] TaskServe` (docs/637) | the sim thread serves the worker's managed call between two script task runs, not only at its own wait sites | A/B run 2026-11-17 (PR #540 notes): slow plans 955 -> 90 [reported, not re-read here] |
| `[RouteAsync] DoorMemo` (docs/159 H2) | within one job a repeated door key returns the first answer | built, exact, not in this run |
| `[RouteSnap] DoorMemoX=3` + ts3fixes `doorgen` (docs/585) | "unlocked" answers reused across jobs for 250 ms under a door generation | one arm run: 0 mismatches in 2,475 re-asks, host calls a job 9.4 -> 5.3 |

**Recommendation:** rerun s140's Isla Paradiso session with `TaskServe=1` and `DoorMemo=1`, and read the new `host` column.
An exact memo of whole lotSearch / LotPlan results was considered and rejected: its inputs include the door answers (managed
state), a "locked" answer has a per-plan managed side effect (`LockedDoorsDuringPlan.Add`, docs/159 §4.1) that a skipped
search would lose, and it would save a wait, not CPU.

## 6. Tests and build

- **Linux host test** `floodmemo-host-test.cpp` (clang `-m32`, freestanding, no libc): compiles floodmemo.cpp's search-side
  code and runs `floodmemo-test.h` on fake 32-bit nodes and cells, plus the player-command rule. **Passes (exit 0).**
- **Selftest** `floodmemo-test.h` (`TestFloodMemo`, the Windows selftest): the table (keys, sequences, generation wrap, caps);
  serve at the first pop; a goal on a record's cell, another context, a routing write, a table clear and a managed call each
  run the search; cap and cut exits are not kept; household and possible-command plans run the search; shadow counts a
  mismatch. `plancand-test.h`: the host frame takes its time out of lotSearch.
- **clang-cl `/Zs /W4`:** ts3native.cpp passes for both the Steam table and `/DTS3A_TABLE`; selftest.cpp passes for Steam (its
  EA-table errors in monojit-test.h are older). The game build must come from MSVC CI (clang's SEH limit).
- `node tools/harness/lot-route-report.ts --self-check`, `tools/hook-sites.ts --check`, `tools/selftest-all.ts`: see the
  commit message for the state at commit time.
- **Repro `ip-floodmemo-crowd`** (Isla Paradiso, UNVERIFIED): one off pass (`floodmemo` 0), then shadow / serve arms in one
  load. Shadow first: `fmChecks > 0`, `fmMismatch 0`.

## 7. Files

- New: `native/ts3native/floodmemo-core.h`, `floodmemo.cpp`, `floodmemo-test.h`, `floodmemo-host-test.cpp`;
  `tools/harness/lot-route-report.ts`.
- `plancand.cpp`: the `host` column; the cell A\* hook and the loop stub call the memo; both install for `FloodMemo` alone.
- `routesnap.cpp`: `g_snWriteSeq`; FloodMemo turns the counting on. `routespeed.cpp`: the Q clear hook is shared.
- `nopathmemo.cpp`: `NpmCmdHandle` (is this route a marked player command). `ts3native.cpp`: the host-call hooks, include,
  install, the 60 s line. `ts3native.ini`, `tools/ini-sections.json`, `tools/harness/native-switches.ts`,
  `tools/harness/repros.json`.
- Not touched: `routephases.cpp`, hitchwatch, StallWatch, hitchsum.
