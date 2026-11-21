# 673. Where the route worker's `+0x918` hold goes, why FloodMemo is idle, and what a yield could give

2026-11-20 (UTC). Inputs: the on-arm `ts3native-full.log` of the 2-minute Isla Paradiso storms s151, s153 and s155
(`route candidate split` lines and totals, `route lock wait` lines, `hitch totals`, the flood memo line), objdump on the Steam
1.67 code (`QuadTreeCellAStar 0x61E500`, the goal-lot loop of `PathPlanner_PlanHierarchical 0x65E0D0`), our source. Background:
docs/670 §7 (the lock and its hand-over points), docs/667 (FloodMemo), docs/669, docs/672 (shortlist items 1, 4, 6).
Built: `[RouteJobPhases] HoldProbe` and the `route speed: flood exits:` counters, both observe only, default off.
**Nothing here has run in the game.** Branch `feat/worker-hold`. §9 (branch `feat/hold-outside`) reads the first storm with
the probe on (s159) and adds the job-part line, `[RouteAsync] PlanEndLend` and `LendWake`.

Short version: the two changes the shortlist asked for (a reachability memo for failed floods, a lock yield between candidate
goals) cannot shorten the hold in these storms, and the logs show why. Failed floods are 0.3-1.1% of the cell searches, and
plans with more than one candidate are 0.4-1.4% of the job time. The hold is single-candidate plans that succeed: water plan,
world A\* and post pass. Neither change was built; what was built measures the two things the logs cannot answer.

## 1. Where the hold time goes [measured, three on arms]

| | s151 on | s153 on | s155 on |
|---|---|---|---|
| worker jobs / job time (= hold time) | 1548 / 36.0 s | 1971 / 49.7 s | 2239 / 51.7 s |
| mean job, longest job (last counter line) | 23 ms, 339 ms | 25 ms, 306 ms | 23 ms, 238 ms |
| jobs >= 200 ms (`long job` lines) | 6 | 8 | 9 |
| `PlanHierarchical` calls (worker + sync) / their time | 1773 / 25.4 s | 2219 / 36.7 s | 2633 / 38.9 s |
| water plan | 6.5 s (25%) | 10.7 s (29%) | 11.0 s (28%) |
| world A\* | 5.7 s (22%) | 8.0 s (21%) | 8.5 s (21%) |
| post pass | 5.6 s (21%) | 8.4 s (22%) | 9.0 s (23%) |
| cell A\* | 3.3 s (12%) | 3.4 s (9%) | 4.8 s (12%) |
| host calls (door ask) | 2.2 s (8%) | 3.2 s (8%) | 2.3 s (5%) |
| everything else in the planner (lot search, lot plan self, shore, gather, insert, rest) | 2.2 s | 3.0 s | 3.3 s |
| job time not inside `PlanHierarchical` (upper part: the sync plans' share is in the row above) | >= 10.6 s | >= 12.9 s | >= 12.8 s |
| plans / failed (`route plans:`) | 1732 / 104 (6.0%) | 2174 / 116 (5.3%) | 2533 / 197 (7.8%) |
| cell searches per plan, ms per search | 1.94, 0.95 | 1.95, 0.79 | 1.88, 0.98 |
| `LotEdgePortals_Choose` calls per plan | 0.43 | 0.46 | 0.45 |
| worker plans >= 100 ms, succeeded: n / ms | 44 / 5982 | 62 / 8621 | 63 / 8133 |
| ... their time by phase | world 1946, water 1680, post 1039, cell 602, host 234 | water 2484, world 2357, post 1427, cell 929, host 859 | world 2547, water 2346, post 1731, cell 685, host 366 |
| ... cell searches in them, median / max | 3 / 13 | 3 / 15 | 3 / 12 |
| worker plans >= 100 ms, failed (code 5): n / ms | 2 / 517 | 1 / 229 | 1 / 231 |
| ... shape | 87 cell searches | 103 searches, 52 candidates, cell 211 ms | 103 searches, 52 candidates, cell 212 ms |
| failed plans >= 100 ms as a share of the hold time | 1.4% | 0.5% | 0.4% |
| routes with more than one plan >= 100 ms | 8 of 40 | 13 of 53 | 10 of 56 |
| `ufplan:` lines (managed plan time), same Sim + kind again within 5 s | 28 of 207 (8.2 of 71.4 s) | 42 of 336 (17.2 of 161.6 s) | 33 of 311 (15.8 of 166.6 s) |
| sim `+0x918` waits >= 1 ms (`hitch totals`) | 167 / 5.6 s | 200 / 6.3 s | 244 / 7.0 s |
| `route lock wait` lines (>= 20 ms) that ended with the job: n / ms | 45 / 2946 | 65 / 4016 | 57 / 3190 |
| ... wait p50 / p90, the job's length p50 / p90 | 54 / 108, 79 / 172 ms | 54 / 115, 73 / 150 ms | 46 / 109, 56 / 137 ms |
| ... job age when the wait began, p50 / p90 | 8 / 106 ms | 8 / 39 ms | 3 / 37 ms |
| ... began within 5 ms of the job's start: n / ms | 19 / 1346 | 30 / 2004 | 33 / 1753 |
| lines >= 20 ms that ended while the job still ran (a lend: door-ask borrow, `TailUnlock`): n / ms | 22 / 1796 | 37 / 2055 | 44 / 3515 |

What the logs cannot give: the job length distribution below 200 ms and the phase split of plans below 100 ms (the split
line has a 100 ms floor; only totals exist), what the 10-13 s of job time outside `PlanHierarchical` is (goal step, the plan's
EditPath tail, the worker's own lock and host waits), and where in a job the sim starts to wait in terms of phases. §4's
probe prints all three.

Reading:

- **The hold is ordinary successful plans.** 68-74% of planner time is water + world + post of plans with one candidate
  (0.43-0.46 `Choose` calls a plan). There is no fat tail left: 6-9 jobs >= 200 ms a storm.
- **Failed plans that repeat searches are 0.4-1.4% of the hold** (1-2 plans >= 100 ms a storm, 229-517 ms). The 104-197
  failed plans a storm are mostly short. docs/667's 300-550 ms failed sync plans (95-136 searches) are this shape, but in
  these storms they are rare and run on the worker.
- **A sim wait is, half the time, the whole job.** The wait begins a median 3-8 ms into the job and lasts until its end
  (p50 46-54 ms). 42-58% of the waits >= 20 ms begin within 5 ms of the job's start. The hand-overs that exist already end
  34-52% of the logged wait ms before the job does.
- **Repeats** (`ufplan:` lines, same Sim and kind within 5 s) are 9-12% of the managed plan time. They are a Sim's next
  plan after a result, in sequence; not two queued jobs for one answer (§5).

## 2. Why FloodMemo does nothing [verified: counters + disassembly]

| flood memo line, shadow mode | s151 | s153 | s155 |
|---|---|---|---|
| searches looked at | 3445 | 4346 | 4972 |
| failed with an empty open list: kept + not kept + predicted and checked | 5 + 2 + 2 = 9 | 19 + 10 + 18 = 47 | 18 + 2 + 12 = 32 |
| share of searches | 0.3% | 1.1% | 0.6% |
| mean kept flood | 0.66 ms | 0.89 ms | 0.99 ms |
| shape / scope / command / no-memo passes | 0 | 0 | 0 |
| resets (of them a managed call) | 5 (2) | 19 (11) | 15 (9) |
| mismatches | 0 | 0 | 0 |

- **It has almost nothing to answer.** Every search is looked at (the count equals the split's cell calls), none is passed
  by for shape or scope. Of 3445-4972 searches, 9-47 end as a failure. The rest find a goal.
- **The searches of a failed plan are not failed floods.** s153 has a code-5 plan with 103 cell searches in 211 ms; the
  whole storm has 47 failed searches. So those 103 searches returned 1. In `0x61E500` the answer is 1 whenever a popped
  node passed the goal test and became the best node (`ebx`, `0x61E6E0..0x61E711`); the failure exit `0x61E59C` is taken
  only with no best node (`0x61E7D9: test ebx, ebx`). The plan fails after its lot legs succeed: the same line shows
  `world 0/102`, two world searches a candidate that cost nothing. docs/667 §3's "each search fails only after it has
  expanded everything" does not hold for these plans; it was inferred from the plan's code, not from the search's answer.
- **The memo is already the reachability memo of docs/672 item 1.** A record is the closed cell set of a failed flood, and
  `FmFind` answers any later search whose start cell is in a record with the same context, whatever its goal (the goal only
  must not hang on a cell of the set). Its key and invalidation are exact: `Q`, lot, options object and hash, `X+0x50`,
  `X+0x4C`; dropped by the Q table clear (`g_nmClearSeq`), any routing write (`g_snWriteSeq`: every writer row but the two
  polygon-cache ensure entries, `Footprint_SetEnabled`, every routing IC but the four plan starts) and a managed call on the
  planning thread (`t_fmHostSeq`). Widening the key cannot help: there is no second failed flood to serve. Where one comes it
  is predicted (2-18 checks, 0 mismatches).
- **Every record is dropped once** (resets = stores), about half by a managed call. That costs nothing here, because the
  repeats inside one plan come before the drop (the checks), and it is what keeps the answer exact.

Ceiling of any failed-flood memo in these storms: 9-47 searches of about 1 ms, under 50 ms of 36-52 s. **Not redesigned, no
relaxed switch added:** a wider or staler key would buy nothing and could only cost exactness.

What could pay instead is the opposite class: searches that *find* a goal from the same start under unchanged data (the 103
searches of a failed plan, 2 ms each, and the 3-15 of a slow successful plan). Whether they repeat a start, and with the same
goal, is not in any log. docs/150 §12's `[CellMemoProbe]` keyed on start and goal and found none. The new counters (§3) say
how much of the 3.3-4.8 s of cell time starts from a cell that an earlier found search started from. Only if that share is
large is a kept search tree (one flood answering many goals, docs/672 §3 "multi-goal") worth designing; it would have to
replay the success state the caller reads from `Q`, which a failure memo never needs.

## 3. Built: `route speed: flood exits:` (floodmemo.cpp, counting only)

With `[RouteSpeed] FloodMemo=1` or `2`, `FmCellLeave` now classes every search that ran, on a second 60 s line:

`route speed: flood exits: found N (T ms, P pops), failed N (T ms; open list not empty N), cut N; found again from a start
cell of an earlier found search, nothing written since: N (T ms, P pops), same goal index N`

- "found again": same start cell and same context key as an earlier found search on this thread, with no sequence moved
  since (the list of 64 start cells is emptied whenever the memo is: clear, write or managed call). "Same goal index" also
  compares `Q+0x1C` with that start's last one.
- Fields `+0x50..+0x78` appended to `ts3native_floodmemo`. Nothing is served from this; modes and answers are unchanged.

## 4. Lock yield between candidate goals: not provable, and worth under 1.4% here

### 4.1 Ceiling [measured]

A candidate boundary exists only in plans with two or more candidates. A storm has 0.43-0.46 `Choose` calls a plan; the
many-candidate plans are the failed ones of §1: 229-517 ms a storm, 0.4-1.4% of the hold. A yield there cannot move the sim's
5.6-7.0 s. The boundaries with time behind them are inside one candidate: before the world A\*, the water plan and the post
pass.

### 4.2 What survives a candidate boundary [verified, disassembly `0x65F2E0..0x65F851`]

The goal-lot loop pops one 0x1C-byte candidate record from the back of a vector a turn (`0x65F842: add [edi+4], -0x1C`,
`jne 0x65F2E0`). Across a turn its frame keeps:

| local | what | state |
|---|---|---|
| `ebx` | the planner P (`P+0x8` lot table owner, `P+0x120`, `P+0x128`, flags `P+0x174`) | lives as long as the world |
| `esi` (= `[esp+0x58]`) | the plan's goal object (ids `+0x48 / +0x4C`, byte `+0x4B0`) | the caller's |
| `edi` | the candidate vector, 0x1C-byte records | **contents not read**: plain values or portal / footprint pointers |
| `[esp+0x148]`, `[esp+0x178]` | two vectors filled before the loop and passed to `Choose 0x65BC20` every turn (the start lot's exits) | **contents not read** |
| `[esp+0x24]` | a refcounted object, released through its vtable `+4` when replaced (`0x65F53A`) | **class not read** |
| `[esp+0x38]`, `[esp+0x40]`, `[esp+0x4C]` | a vector and two cursors of the lot leg | plain within a turn |
| `[ebp+0x8..0x34]` | the caller's arguments (path, points, options) | the caller's |

Fresh every turn: the goal lot's row, looked up by id (`LotTable_GetIfPresent 0x62FB60` at `0x65F463` and `0x65F66F`).

Outside this frame and also live across the boundary [verified docs/159 §3.1, docs/663 §4.2, docs/670 §7.3]: the frames above
(`Route_PlanLocked`, `Route_PlanCommon`: start and goal lot rows, the route's footprint references), the A\* object `Q` (its
cell table keeps quadtree leaf pointers until its clear `0x61C020`), `[RouteSpeed] NeighbourMemo`'s kept neighbour lists,
and the global clip scratch `0x1158580`.

### 4.3 What blocks it

1. **A full release (any taker gets the lock).** A writer may free or move what the three unread locals, the caller
   frames and `Q`'s cell table point at (lot row, footprint, portal, quadtree node). `g_snWriteSeq` shows the write after the
   retake, but there is no safe reaction: restarting the candidate does not refresh the caller frames, and leaving the plan
   early runs the game's cleanup over the changed data. This is docs/670 §7.4 option C; reading the loop frame did not
   change it. Proof needed: the contents of the 0x1C records and of the two exit vectors, and every pointer local of the
   two frames above, each shown to be a value, an id, or re-read after the boundary.
2. **A restricted lend (`TailUnlock`'s rule: only the sim's steering and read-only gate leaves, worker parked).** The
   admitted calls are the game's own readers. Parked, the worker does not race their counters, but a reader that is the
   outermost query flushes a deferred quadtree (frees nodes that `Q`'s cell table and the neighbour memo still point at), and
   one that finds an invalid polygon cache rebuilds it through `0x80E5B0`, which can insert a lot table row (rows held by the
   frames above). `TailUnlock` is proven because passes 16-19 touch no guarded state at all; that proof does not carry to a
   point between two lot legs. Admitting a reader only when no flush is pending and the cache is valid is exactly the
   pre-check of the read-only clones, and those answer without the lock already. So the takers that still wait are the ones
   this lend would have to refuse.

Not built. The same two blockers apply to the phase boundaries inside a candidate, with more live state (the world path
under construction).

### 4.4 Built instead: `[RouteJobPhases] HoldProbe` (workerhold.cpp, observe only, default off)

docs/672 item 4 asks for "the yield counted but not taken" before any yield. This is that count, for every boundary kind,
plus the distributions §1 could not give. It patches nothing: plancand.cpp's frames call it, `ServeAsync` brackets the job,
`LockAcquireAsync`'s pump loop keeps a count of the sim's `+0x918` waits.

- `ts3native.ini` `[RouteJobPhases] HoldProbe=0`, `HoldProbeDupMs=2000`. Needs `[RouteAsync] Enabled=1`; the plan and
  boundary parts need `CandidateSplit=1`. Runtime switch: export `ts3native_workerhold +0x00` (`native_set workerhold`).
- Three lines a minute:
  - `worker hold: ... jobs by ms (n/ms), every plan ok: <5 .. >=250; a plan failed: ...; outside PlanHierarchical N ms;
    repeats (same route and IC kind within W ms of its last end) N, T ms`
  - `worker hold plans by candidates (n/ms): worker ok: 0-1, 2, 3-9, >=10 (cell T ms); worker failed: ...; sync ok: ...;
    sync failed: ...` (candidates = `LotEdgePortals_Choose` calls of the plan; the first of them is the start lot's exits)
  - `worker hold boundaries (reached / with the sim waiting on +0x918 / first such of its job, ms from it to the job's end):
    plan, candidate, lot leg, world, water, post; jobs that ended with the sim waiting and no boundary in the wait N`
- How to read the third line: "first such of its job" for kind K with T ms means that in those jobs K was the first
  boundary reached while the sim already waited, and T is the time from there to the job's end. T is the most a yield at
  boundaries of that kind (and later ones) could cut from the sim's wait: the taker's own time and the worker's retake are
  not subtracted. If `candidate` carries little and `world` / `water` / `post` carry most, §4.1 is confirmed in the game.

## 5. Queue: priority and coalescing (docs/672 item 6) [verified source, measured §1]

- **Visible / player first exists.** The A10 queue picks by `PfBefore` (`[RouteAsync] PlayerFirst`: a marked job before an
  unmarked one, else oldest START first).
- **Two queued jobs for one answer cannot exist.** A job belongs to a route handle (`JobFor`), one job a route. A Sim that
  plans again does so after its last result.
- **Sequential repeats are what §1 measures** (9-12% of managed plan time, same Sim and kind within 5 s). The exact form
  is built: `[RouteAsync] ReplanDedupe` answers a repeat of a failed plan within 500 ms (same follower, IC, goal bytes, start
  within 10 cm, unchanged world version), on in these storms. A repeat with a moved start or after a success is a new
  question; answering it from the old result is not exact.
- Design left, not built: order unseen jobs behind seen-NPC jobs in `PfBefore` (a third class from the job's `kdScope`,
  known at START). It moves no hold time, only whose plan is late; worth it only if the probe's `worker hold:` line shows
  the sim's long waits sit behind unseen jobs while a seen job is queued. About 30 minutes of code plus one storm.
  `HoldProbe`'s repeat count (same route handle and IC kind within 2 s) gives the native-side repeat rate first.

## 6. Checks run (2026-11-20, no game)

- `workerhold-host-test.cpp` (Linux, 32-bit, freestanding; includes `workerhold-test.h` as is): buckets, the first boundary
  in a sim wait, the duplicate ring (window edge, tick wrap, oldest replaced), the hooks on counters (a job with a wait at
  water, a failed 52-candidate plan that repeats a route, a sync plan, mode 0). Pass.
- `floodmemo-host-test.cpp` with the new exit-class cases (found, found again, same goal, another start, a write empties
  the list, cap exit, cut). Pass.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors, the
  same warning set as before the change (170 / 172 / 217, none in the new or changed lines). No new address, so no
  `ts3addr-ea169.h` row.
- `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL.
- Not run: the MSVC build, the full Windows selftest (`TestWorkerHold`, `TestFloodMemo`), anything in the game.

## 7. Next storm

One arm is enough (nothing changes a plan). On top of the s155 on-arm ini:

```
[RouteJobPhases]
HoldProbe=1
CandidateSplit=1
CandidateSplitLogMs=100
[RouteSpeed]
FloodMemo=2
```

Read: `worker hold:` (is the hold many 20-100 ms jobs, and how much is outside the planner), `worker hold boundaries`
(which boundary kind carries the sim's wait), `route speed: flood exits:` (how much found-search time repeats a start).
Repro `ip-workerhold-storm`.

## 8. Files

`native/ts3native/workerhold.cpp`, `workerhold-core.h`, `workerhold-test.h`, `workerhold-host-test.cpp`; `floodmemo.cpp`,
`floodmemo-test.h`; `plancand.cpp` (two calls), `ts3native.cpp` (include, install, counter line, job bracket, wait count),
`selftest.cpp`; `ts3native.ini`, `tools/ini-sections.json`, `tools/harness/native-switches.ts`, `tools/harness/repros.json`,
`docs/registry.md`, `docs/68-hook-conflict-map.md`.

## 9. s159: the probe ran. What the 12.4 s outside `PlanHierarchical` is, and a lend at the plan's end (branch `feat/hold-outside`)

2026-11-20. Inputs: `ts3native-full.log` of s159-on (2-min storm, HoldProbe, CandidateSplit, FloodMemo=2, CellIndex=2), Ghidra
headless `DecompileTo` of `Route_PlanCommon 0x7606E0` on a private copy of the project, objdump of `0x65E0D0`, our source.
Built here: HoldProbe's fourth line, `[RouteAsync] PlanEndLend` and `LendWake` (default off). **Nothing here has run in the game.**

### 9.1 A reading to correct first [verified source]

The `plan` column of `worker hold boundaries` is the **start** of a `PlanHierarchical` call (`WhEnter(PCC_PH)`, §4.4: "at the
start of every plan ..."), not its end. `plan 1513/101/99 2333 ms` therefore says: in 99 jobs the sim thread was already
waiting on `+0x918` when the plan began, and 2,333 ms of job time followed, the plan itself included. The wait began in the
part of the job before the plan. It is not a measure of what a hand-over after the plan could give; no s159 number is. The
new line (§9.5) measures that.

### 9.2 The 12.4 s by component

A worker job is one route IC [verified docs/87 §2-3, decompile]: start points, `Route_GenerateDestinationCandidates 0x751FF0`,
the goal step `0x75C2D0`, then `Route_PlanLocked 0x82C6E0` once (`0x760DE1`; pending updates, `RoutingJob_Execute`, in it
`PlanHierarchical`), then `Route_MapStatusToResult 0x752620`, `RouteFollow_EditPathForRoute 0x85FF30` per path, a few setters
on the last path, and our job end in `ServeAsync`. s159: 1,535 jobs, 33,712 ms; `PlanHierarchical` on the worker 21,323 ms.

| component of the 12,387 ms | ms | how known |
|---|---|---|
| the worker waiting for the sim thread to run a managed call (door asks, event 59), outside `PlanHierarchical` | **6,100 - 9,500** | measured lower bound: `route task serve` picked up 13,070 calls after 8,109 ms of queue (0.62 ms each, max 44 ms); `route plans:` counts 17,580 host calls; the split's host frames inside `PlanHierarchical` are 4,115 calls / 1,961 ms. Upper bound: all 17,580 at the same mean, plus service |
| ... of it in the goal step (before the plan) | most | 103 `routefail job` lines with `plan 0` (the IC never reached `Route_PlanLocked`): 765 ms of job, **580 ms host**, 154 ms other. 5.6 ms of host wait a job before any plan; x 1,535 jobs = 8.6 s if every job is like them |
| goal generation and the goal step's own code | about 2,300 | the same 103 jobs: 1.5 ms a job that is neither plan nor host; docs/177 §1.1 measured 1.85-2.45 ms "rest" a job on Isla Paradiso |
| `Route_PlanLocked` outside `PlanHierarchical`: pending updates, `RoutingJob_Execute`, and **our wrappers** (`DiscDrainForPlan` applying deferred footprints, `PlanCached`, NoPathMemo, plan-repeat keys) | not separable | no counter in s159; in the 11 `routefail job` lines with a plan, job minus plan minus host sums to -66 ms (host waits nest in the plan) |
| the tail: result mapping, `EditPathForRoute`, path setters | small, not measured | docs/178 §1.1: `EditPathForRoute` max 3.1 ms a call in 4,736 jobs; sync IC plans in s159 show `other` 0-8 ms for the whole IC outside the phases (19 lines, 43 ms) |
| our job end between the IC's return and the release (`KdJobEnd`, `RjpJobEnd`, `WhJobEnd`, `HsJobEnd`, their log lines) | small, not measured | thread-local sums and at most one `Log` (an unbuffered `WriteFile`) a job; about 190 such lines in the storm |

So more than half of the time outside the planner is the worker **waiting**, not working: the goal step asks the script whether
a door is passable, one round trip to the sim thread per ask, about 0.6 ms each when the sim is not itself waiting on the lock
(a sim that waits on `+0x918` answers at once from its wait pump). `DoorMemoX` answered 10,672 asks and missed 18,081 (15,942
with no row: 1,387 generation bumps a storm clear the table about 11 times a second). The sim is not blocked during such a
wait, but the job is longer by it, and a longer job meets more sim frames (docs/670 §7.1).

### 9.3 Is our own code part of it?

| ours, under the job's hold | size in s159 | verdict |
|---|---|---|
| the host-call proxy (the managed call must run on the sim thread) | 6.1-9.5 s outside the planner, 2.0 s inside | the largest item, and ours as a mechanism. Not movable: the lock must stay held (docs/159). Fewer asks is the lever: the goal step's asks are where `DoorMemoX` misses |
| `[RouteSpeed] CellIndex=2` (shadow) | 12.8 M calls, 6.2 M compares, 0 hits, "saved ~0 ms" | pure cost inside the cell A\* (3.1 s self time in the storm). Estimate 0.4-0.8 s at 30-60 ns a call [inferred]. A timing arm should run it at 0 or 1 |
| `[RouteSpeed] NeighbourMemo=1` | 2.87 M calls, 17,374 hits (0.6%), 1.17 M stores, "saved ~8 ms" | the stores are paid on every miss; at 50-100 ns each they cost more than the 8 ms saved [inferred]. Worth an off arm |
| candidate split frames (13 sites, two clock reads each) | about 155,000 frames | < 30 ms |
| log lines written on the worker inside a job (split lines, `routefail job`, `long job`) | about 190 | < 10 ms |
| our `Route_PlanLocked` wrappers, our job end | unknown | the new line prints both (§9.5) |
| P5 / P6 leftovers | none on the worker: the 583 `P5c park` lines are written by the sim thread | - |

Nothing of ours was found that both runs under the hold and can simply move out. The two shadow / memo costs are settings of
the storm arm, not code to fix.

### 9.4 The tail after the plan: what needs the lock [verified decompile of `0x7606E0`, `0x760DE6..0x7615C9`]

After `Route_PlanLocked` returns, `Route_PlanCommon` uses: the route object (`param_1+0xE0` path list, `+0xEC`, `+0x100..+0x141`
orientation, `+0x3C`, `+0x158`), its own goal vectors by the two indexes the planner returned, the status block it copies out,
atomic reference counts on the route's path objects, and per path `0x545170` + `EditPathForRoute`. No lot row, quadtree node,
footprint or A\* table of the planner is read again. `EditPathForRoute` is the exception, and it is most of the tail: per path
node it runs `RouteFollow_EditPath 0x85F5A0`, whose gather `0x82F0B0` takes `+0x918` itself, and whose consumer loop then runs
**outside that acquire** on lot rows (`0x62EF30`, find-or-insert), polygon caches (`0x80E5B0` rebuild) and plain footprint
reference counts (docs/670 §8.2, docs/177 §4). On the sim thread that is safe because it is the sim thread; on the worker only
the job's hold makes it safe (`RefcountGuard` orders the sim's count writes against a worker that holds the lock).

So the tail cannot run without the hold, and there is no split that leaves a useful unlocked part: what is not `EditPath` is
arithmetic on the route. Releasing before the tail and letting `EditPath` take the lock for its gather would run the consumer
loop unlocked: a data race. **Not built.**

What is provable is a **lend with the worker parked** at the point where the call returns: the search is over, the planner's
frames are gone, the game itself holds no `+0x918` level there, and everything the tail needs it reads again under the hold
(`EditPathForRoute` is also what the game calls later on any planned path, `IC_Route_SetAGS`, after any number of world
changes). This is TailUnlock's window (docs/150 §11) at a later point of the same job with less live state, and with a worker
that runs nothing while it is open.

### 9.5 Built

**HoldProbe, fourth line** (`workerhold.cpp`, `workerhold-core.h`; observe only): `worker hold parts: before or between
Route_PlanLocked N (host H / n), jobs that never reached it n / ms; inside our Route_PlanLocked detour N (host, the game's
Route_PlanLocked G with PlanHierarchical P of it, our wrappers at most W); after the last one N (host); our job end before the
release N (max us); the last plan ended with the sim waiting on +0x918 in n of m jobs, T ms from there to the release`.
Hooks: `PlanLockedHook` (the detour's whole body), `Proxy` (a host wait goes to the part it was posted in), `ServeAsync`
(the IC's return, the release). The three parts sum to the IC's time. T is the ceiling of any hand-over at the plan's end.

**`[RouteAsync] PlanEndLend`** (`tailunlock.cpp`, default 0; 1 = lend, 2 = count only; `PlanEndLendWaitUs=300`). At the end of
our `Route_PlanLocked` detour, on the worker inside a job, when the caller is `Route_PlanCommon` (return `0x760DE6`), no host
call is open and the sim thread is waiting on `+0x918`: open TailUnlock's window (`TuOpenWhy`: same refusals, same `P5bLock`
admission, state stays `A_RUNNING`), set `g_asyncDone` so the sim's wait pump tries now, and **wait** until the sim's taker has
the lock or its wait ended, or `P5bLock` held the waiter out (not an admitted class), or the time is up; then `TuClose` takes
every level back (it waits for the taker's release; an admitted taker waits for no job). The tail then runs under the hold as
today. Patches no byte. EA 1.69: the address row is unresolved, so it installs as off.

- Line `plan end lend:` plan ends, with the sim waiting, windows (taken / waiter held out / nobody came / refused by reason),
  worker parked ms, **job time after a taken window** (the most the sim's waits ended earlier), `closed while parked` (the
  assertion), TailUnlock windows that woke the sim.
- The assertion asked for ("no `+0x918`-protected access in the unlocked tail") is met by construction: no part of the job
  runs while the window is open. `closed while parked` counts a window that something closed under the parked worker (it
  must stay 0), and TailUnlock's own tripwires (`TuTrip`: a worker acquire, gate or routing site inside an open window) stay
  armed.
- Admission is TailUnlock's: the sim's steering calls and, with `[RouteSnap] ReleaseGates`, its read-only gate leaves
  (`g_p5cJob` is 0 here: P5c's wider admission ends with the clone run, before the plan returns). Writers, gates, destroys
  and sync plans keep waiting for the job's end. Admitting them would need two more proofs: that no such taker waits for the
  job while it holds the lent lock, and that no site reads `A_RUNNING` as "no sim writer runs". Not attempted.

**`[RouteAsync] LendWake`** (default 0). The sim's `+0x918` wait pump (`LockAcquireAsync`) sleeps up to 1 ms between tries
unless `g_invReady` or `g_asyncDone` is set. A TailUnlock window does not set either, so a window shorter than the rest of
that millisecond passes unseen. With `LendWake=1`, `TuOpen` sets `g_asyncDone` when the sim is waiting. Every waiter on that
event re-reads its condition in a loop, so the extra set costs one lock try.

Expectation, stated plainly: the plan-end lend can give at most the tail of the jobs whose plan ends with a waiting, admitted
taker. The old numbers put the tail well under a millisecond for most jobs. It is built because it was asked for and is
cheap; the storm's `worker hold parts` T decides whether it stays. `LendWake` may matter more: it makes the existing
TailUnlock windows usable.

### 9.6 Why plans with two candidates cost more [verified call sites, measured s159]

"Candidates" in the probe are calls of `LotEdgePortals_Choose 0x65BC20`. It has two call sites in `PlanHierarchical`:

| site | where | what it chooses | what follows |
|---|---|---|---|
| `0x65EF72` | before the goal-lot loop | the exit of the **start** lot (the Sim starts on a lot) | row lookup `0x65EFBE`, the start lot leg `LotPlan` at `0x65F11C` |
| `0x65F41B` | in the goal-lot loop `0x65F2E0..0x65F84B`, once a turn | the entry of the **goal** lot | row lookup `0x65F463`, the goal lot leg at `0x65F582`, the world A\* at `0x65F809` |

So a plan with two calls is not two alternatives searched twice. It is a lot-to-lot route: leave the start lot, cross the
world, enter the goal lot. One call is a route with one end on a lot; none is a route inside one lot (`0x65E3C2`) or between
two points off any lot. The 6x is mostly the mix: 1,726 plans made 549 `Choose` calls, so about 1,350 of the 1,558 plans in
the "0-1" bucket made none, and those are the short in-lot plans. Among plans >= 100 ms (`route candidate split` lines) the
two classes cost the same: 27 worker plans with one call, mean 140 ms; 21 with two, mean 124 ms.

| per plan >= 100 ms | one `Choose` (27) | two (21) |
|---|---|---|
| world A\* ms / calls | 60 / 1.2 | 18 / 1.9 |
| water ms | 36 | 44 |
| post ms | 20 | 24 |
| cell A\* ms / calls | 9 / 2.3 | 14 / 5.2 |
| lot search ms / calls | 1 / 1 | 5 / 2 |
| host ms / calls | 7 / 6.5 | 11 / 12.6 |

Shared work: none between the two lot legs (two different lots, each its own cell searches and door asks). The second world
search of the two-call class (1.9 a plan with one loop turn) comes from one of the other two world A\* call sites of the
function (`0x65FB99`, `0x65FDA5`); which, and whether it repeats the first search's start, was not read. The flood counters
say repeats are rare: 13 of 3,018 found searches start from a cell an earlier one started from. Nothing to keep was found.

### 9.7 Checks run (no game)

- `workerhold-host-test.cpp` with the part cases (nested plan, a host wait between two plans, a job with no plan, the hooks
  on counters, the release): pass.
- clang-cl `/Zs /W4` with the xwin headers: `ts3native.cpp` (Steam and `/DTS3A_TABLE`), `selftest.cpp`: 0 errors, no warning
  in the new lines of the main unit.
- `node tools/selftest-all.ts --ci`: 199 PASS, 0 FAIL. No new address (`0x760DE6` is an existing row), `ts3addr_count` unchanged.
- Not run: MSVC, the Windows selftest (`TestWorkerHold`; `TestTailUnlock` gained a single-thread PlanEndLend case: switch
  off, another caller, no sim wait, count only, a window nobody takes, no job), anything in the game. The "taken" path of the
  lend has no test: it needs the second thread.

### 9.8 Next storm

Off arm = the s159 on-arm ini plus:

```
[RouteJobPhases]
HoldProbe=1
CandidateSplit=1
HostSplit=1
[RouteAsync]
PlanEndLend=2
[RouteSpeed]
CellIndex=0
```

On arm adds `[RouteAsync] PlanEndLend=1` and `LendWake=1`. Read: `worker hold parts` (is the time before the plan host
waits; what our wrappers and job end cost; T), `route host calls` (which serve site carries the queue), `plan end lend:`
(windows taken, job time after them, `closed while parked 0`), and the sim's `+0x918` waits in `hitch totals`. Repro
`ip-holdoutside-storm`. If the goal step's door asks are confirmed as the bulk, the next build is there (a `DoorMemoX` that
survives a generation bump for doors the bump did not touch, or one ask per lot for all its doors), not at the tail.
