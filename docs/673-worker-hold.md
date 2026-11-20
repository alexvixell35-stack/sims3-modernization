# 673. Where the route worker's `+0x918` hold goes, why FloodMemo is idle, and what a yield could give

2026-11-20 (UTC). Inputs: the on-arm `ts3native-full.log` of the 2-minute Isla Paradiso storms s151, s153 and s155
(`route candidate split` lines and totals, `route lock wait` lines, `hitch totals`, the flood memo line), objdump on the Steam
1.67 code (`QuadTreeCellAStar 0x61E500`, the goal-lot loop of `PathPlanner_PlanHierarchical 0x65E0D0`), our source. Background:
docs/670 §7 (the lock and its hand-over points), docs/667 (FloodMemo), docs/669, docs/672 (shortlist items 1, 4, 6).
Built: `[RouteJobPhases] HoldProbe` and the `route speed: flood exits:` counters, both observe only, default off.
**Nothing here has run in the game.** Branch `feat/worker-hold`.

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
