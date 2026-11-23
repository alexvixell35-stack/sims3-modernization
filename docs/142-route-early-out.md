# 142. Route early-out: why failing plans cost seconds, and a no-path pre-check (`[RouteEarlyOut]`)

Written 2026-10-21. **Static study plus an offline build:** no game run, no queue started. ts3native stays `0.36.9` on its branch (version string unchanged; merged into 0.36.12), counters **v78**, selftest `TestEarlyOut` (`selftest ok`). Question (Rodney, top priority): the worst freezes left after docs/134 are single long sync plans: 7,361 ms and 7,350 ms (SV big crowd), 4,060 ms (BP rare-route-bits), 1,015 ms and 1,313 ms (SV). Does the world search exhaust the whole graph before it reports no path, and is there a cheap pre-check that proves "no path" early with the answer the full search would give? Ledger rows: docs/51 §203. Tags as in AGENTS.md.

## Summary

- **A failing world search visits everything it can reach.** The world A\* `WorldAStar_Search 0x676260` has exactly two exits: the goal popped, or the open list empty; no node, time or cost cap [verified, docs/11 Q1, re-read at `0x676BE9..0x676C11`]. A no-path answer therefore costs a flood of the start's whole reachable part of the waypoint graph R (Sunset Valley: up to ~27,000 walkable waypoints and ~110,000 edge costs, each byte-2/7 edge with a polygon segment test), plus the circle pre-pass that runs first [verified `0x67628A..0x6762C4`].
- **The planner multiplies it.** For a goal inside a lot, `PathPlanner_PlanHierarchical 0x65E0D0` tries the goal lot's entry portals one by one: per candidate one colour check, then one full world A\*; on failure it drops the candidate and takes the next [verified, decompile of the loop at `0x65F697..0x65F842`]. All candidates share the start, so when the goal is cut off by walls the colours do not see (blocked lot edges, polygon walls, lot-hub rules, mode rules), every one of K candidates repeats the same flood. For open-ground goals with several goal records it runs one A\* per distinct goal waypoint, **without** the colour check [verified, `0x65FB99`, `0x65FDA5` paths]. Above that, `PathPlan_WithRetries 0x661B50` and the RoutingJob's obstacle replan add whole plans (docs/11 Q1 table) [verified there].
- **Data:** the BP 4,060 ms plan is the forced car-only plan of `rare-route-bits` (`EnablePlanningAsCar` + `DisablePlanningAsPedestrian` + `BeginAsCar` + `EndAsCar` to the farthest community lot, result `FailedBecauseNoPathToDestination`): its step took 8 s where every other step took 2.5-3 s [verified timestamps in `probe-out/queue/freeze-data-2026-10-20-bp-frz-rare-route-bits.log`; the match to the 4,060 ms line is inferred from it being the only multi-second line of that run]. Car-only = cost mode 2, which admits only edge byte 3 (street waypoints `0xD`); lot entry waypoints (type 2) have no byte-3 edge, so **every** goal-lot candidate of a car-only plan into a lot fails by construction [verified edge rules docs/99 §10.1, edge pairing §10.4]. The SV 7.3 s and 1.0-1.3 s lines have no script stack in that build and stay unattributed.
- **The pre-check (built as a probe, `[RouteEarlyOut]`, off by default, observe only):** before each world A\* call three proofs are tried; each implies "this call returns no path": (1) **car**: mode 2 and the goal is not in the start's byte-3 component of R (our own union-find, exact for mode 2); (2) **colour**: modes 1-3 without water/boat context flags, the game's own flood colours of start and goal differ (the test the game already makes for goal lots, extended to open-ground goals); (3) **memo**: an earlier unrestricted search of the same request with the same start and the same wall context failed and the goal is not in its closed set (the closed bits of a failed search are exactly the start's reachable set). Mode 1 still runs every search, counts the time a real early-out would save, and checks each proof against the real result (`eoWrong`, expected 0).
- **Why not act yet:** the proofs keep the yes/no answer by construction, but a skipped search also skips the "best node" it writes for the blocker hint (`*best` is reset to the start at every call and updated during the search [verified `0x67641E..0x67642A`]), and the memo depends on two inferred facts (no wall opens between two searches of one request; wall-ness does not depend on the SHORT/LONG cost set). Both are checked live by the probe before an acting mode exists (§5).
- **Not covered by any pre-check:** time spent outside the world A\* (lot legs `LotPlan 0x6410B0` per candidate, portal A\* `0x63E340`, closest-waypoint searches, post passes, managed callbacks). The probe's per-request line splits a slow plan into world-A\* time and the rest, which is the first thing the next run must answer (§6).

## 1. The slow plans

| Run (freeze-data 2026-10-20) | Line | Plan | Result | Tag |
|---|---|---|---|---|
| BP rare-route-bits | 4,060 ms, NoStart | `sim_route_to far` with car-only options (step id 51) | `FailedBecauseNoPathToDestination`, blocker location set | verified result; link to the line inferred (§0) |
| SV rare-route-bits | 1,313 ms, NoStart | not attributable: SV steps all took 5 s and most ended `FailedBecauseNoDestinationSpecified` | - | open |
| SV big crowd | 1,015 + 7,361 + 7,350 ms back to back, NoStart | crowd plans (social group routing per docs/134 §1.1 shapes) | not logged | open |

Other forced plans of the same run were cheap: the horse plan (`FailedBecauseNoPathToDestination`), the mounted plan (no path), plain car (`EnablePlanningAsCar`, mode 3, no path) and UFO (no path) added no visible time to their steps (gaps equal the steps' waits at the log's 1 s resolution) and no other sync plan line of that run exceeds 123 ms [verified], so "no path" alone is not slow; a multi-second no-path plan is one whose search has a large reachable part and many tries.

## 2. Termination and amplifiers

### 2.1 The world A\* (`0x676260`)

- Entry: thiscall on the A\* container `W+0x18`, seven stack arguments (R, G, &start ref, &goal ref, cost mode, search context, &best), `ret 1Ch`, result in EAX (non-zero = path) [verified, argument loads `0x676264..0x676282`, returns `0x676C11` / `0x676C9C`].
- Circle pre-pass: if the context's circle radius² (`+0xA0`) equals 0.0 at entry and `0x673600` accepts the pair, the A\* sets the circle and calls itself; on failure it clears the radius and runs again unrestricted [verified `0x676268..0x6762C4`, constant `0xF98964` = 0.0 read from the exe]. On success the radius stays set (a later call with the same context runs once, restricted) [verified: the success branch jumps to the epilogue].
- Closed set: the bit array at container `+0x10` (`+0x08` bytes) is cleared at the start of each search; the bit of ref (chunk, wp) is `table[(chunk + 1) & 0xFFFF] * 8 + wp` with the per-chunk byte table at `+0x0C` [verified `0x6762ED..0x676300`, `0x6764FA..0x67650B`, `0x676557..0x676588`].
- Exits: goal popped (`0x676C14`) or open list empty (`0x676BF7`: frees the heap array, returns 0). **The closed bits are not touched on the way out** [verified], so after a failure they hold every node the search closed = every node reachable from the start under that search's walls.
- `*best` is set to the start at entry [verified `0x67641E..0x67642A`] and moved to the generated node with the smallest heuristic during the search (docs/99 §2.4).

### 2.2 Multiplication above the A\*

| Where | What repeats | Tag |
|---|---|---|
| A\* circle pre-pass | a restricted flood before the full one (failures only) | verified |
| Goal-lot entry loop (`0x65E0D0`) | one colour check and one full A\* per entry candidate, same start, until one succeeds or the list is empty; a colour mismatch ends the loop at once | verified (decompile) |
| Same loop | a start-side lot leg (`LotPlan`, "Routing/lotStart") inside the loop body | inferred: the branch's condition was not resolved |
| Open-ground goals | one A\* per distinct goal waypoint (`W+0x100` tried set), no colour check | verified (decompile) |
| `PathPlan_WithRetries 0x661B50` | whole planner again with the retry flag; one planner run per distinct goal lot | verified (docs/11 Q1) |
| RoutingJob `0x81C67A` | a second PlanPath with relaxed flags when ReplanToFindObstacleWhenPathPlanFails is set (every Sim route) | verified (docs/11 Q1) |
| C# route-fail behaviour | further `Route.Plan` calls (separate IC calls, separate log lines) | verified call sites (docs/11) |

### 2.3 Existing caps

None in the game [verified docs/11 Q1/Q3]. Ours: `[RouteBudget]` (node/time cap through the A\*'s five cost-function pointers, off; in-game test 2026-10-13 stranded Sims, docs/15 phase 4) and docs/134 `SyncCapMs` (the same stubs, sim-thread sync plans only, off by default). Both **change answers** (a findable path becomes "no path" over the cap). The early-out is the opposite kind: it may only skip searches whose answer is already known to be "no path".

## 3. The pre-check

All three proofs run at the A\* entry, outside any lock of ours, in the same thread and request as the search. Virtual (water) ends and start = goal are never proven.

### 3.1 Car components (mode 2)

- **Condition:** cost mode 2 and `lab(start) != lab(goal)`, where `lab` is a union-find label over every R record reference whose edge byte is 3 (records with the edge-bytes flag `0x40` only).
- **Why it never turns a path into a failure:** the mode-2 cost function walls every edge whose byte is not 3, walls every virtual end, and has no other test that could admit an edge (no blocked-flag test, no polygon test; the circle only removes edges) [verified, docs/99 §10.1]. So every path the mode-2 search can find uses byte-3 edges only and stays inside one label. The labels are built over undirected edges, a superset of the forward references the search follows. A reference the search could follow outside the chunk table (out-of-range chunk or waypoint index) makes the labels unusable for that R (`ok = false`), so the proof never covers an edge it did not model.
- **Exactness:** for mode 2 the label is also sufficient (same label = reachable when the circle is off), so the proof misses nothing.

### 3.2 The game's colours (modes 1-3)

- **Condition:** cost mode 1, 2 or 3, context flags without `0x80` (water planning) and `0x100` (boat), both colours `>= 0`, and different.
- **Why:** the game's flood `RTData_BuildComponents 0x66CE10` follows every record reference regardless of edge byte, blocked flag or mode (docs/99 §1.8) [verified there], so a search without virtual edges stays inside one colour. Virtual edges are walls in modes 1 and 2 without flag `0x80` [verified, docs/99 §10.1]; mode 3 follows mode 1's virtual rules plus a water-lot rule [verified diff, docs/99 §10.1; that the `0x80` wall comes first in mode 3 is inferred]. Mode 4 is left out: its virtual-edge rule has no flag wall in docs/99 §10.1. The flood's colour classes are reach-closed only because R is symmetric (every edge has its reverse; all 14 shipped worlds, docs/99 §10.4) [verified data]; the game's own check at `0x65F781` relies on the same.
- **What it adds:** the game already makes this test for goal lots; the probe counts it where the game does not (open-ground goals, a second goal-lot candidate in another colour cannot happen because the loop breaks).

### 3.3 Same-request memo

- **Store:** after an outer A\* call (not the circle rerun) that returned no path, entered with radius² 0 and left with radius² 0, not capped by `[RouteBudget]`/`SyncCapMs`, and with both cost sets (container `+0x28` SHORT, `+0x4C` LONG) made only of finite positive factors: copy the closed bits and the call's wall context.
- **Wall context (key):** container, R, G, cost mode, start ref, context `+0x00..+0x63` (flags, start/goal lot hubs, start/goal lot points, edge-group sets), `+0x88` (W), `+0x8C` (job block with the polygon set), `+0xA8..+0xB3` (start point). Not in the key: the goal ref `+0xB4`, the goal point `+0xB8`, the cost-set slots `+0x64..+0x87` and the circle `+0x90..+0xA0` (written by the A\* itself).
- **Condition:** same key, same request (one `Route_PlanLocked` on this thread), goal's closed bit clear in the copy.
- **Why:** with the circle off, a failed search closes exactly the set of nodes reachable from the start through non-wall edges. Whether an edge is a wall depends on its two ends, its edge byte and the context, not on the goal or the visiting order: the only parent-dependent rule (edge byte 1 out of a hub, docs/99 §10.1 case d) gives finite factors without the retry flag and infinite or NaN (both walls) with it; the hub-entry 5,000,000 is not a wall; heuristic results are never tested against the wall [verified, docs/99 §10.1, §2.4]. The goal enters only through the heuristic (order, not reach), the circle (off) and the SHORT/LONG choice (equal walls when every factor is finite and positive). So a later search with the same key reaches the same set, and a goal outside it fails. A restricted later search (radius set at entry) reaches a subset and fails too.
- **Assumptions (inferred, checked live by `eoWrong`):** no wall changes inside one request between the two searches. The candidates: lazy lot routing builds inside the plan (`LotGraph_Build 0x63CB90` -> `RoutingWorld_RestoreLotBlockedBits 0x82A190` rewrites lot-edge blocked bits, docs/99 §1.7), the world-manager point test `0xC6BAB0` on type-2 targets, and managed callbacks between two searches.

### 3.4 Keeping the components right when the world changes

| Change | Car labels | Game colours | Memo |
|---|---|---|---|
| World load / new R | rebuilt when R's record pointer, size, w or h differ (~ms once) | the game rebuilds them at load | per request: gone |
| Lot load, lot streaming, build/buy, lot-edge blocked bits | edges of mode 2 never test blocked bits; records do not change after load (`WorldRoutingData_LoadChunks` runs only for BuildRoutingData / world save, docs/99 §1.9) | colours ignore walls: blocked bits only remove edges, so the old colours stay a superset | per request; inside a request see §3.3 |
| Portals and doors | lot-side (lot graph, portal A\*); not in R | same | same |
| Water links (G maps) | mode 2 walls them | flags `0x80`/`0x100` excluded | part of the key (G, flags) |

## 4. What was built (observe only)

- `native/ts3native/earlyout.cpp` (included by `ts3native.cpp`): detour on `0x676260` (prologue `83 EC 3C 53 8B 5C 24 58`, the bytes `[ProbeValues]` also checks; ours installs first, ProbeValues then logs SKIP for that one entry), request scope in `CallPlan` (every `Route_PlanLocked` on the caller or the worker; the old function is now `CallPlanBudget`), car labels, colour read, memo, counters, log lines.
- Ini `[RouteEarlyOut]` `Enabled=0` (installs the A\* detour and the `Route_PlanLocked` detour), `Mode=1` (start value of `eoMode`, runtime switch, `--ab earlyout`), `LogMs=200`.
- Per request `>= LogMs` (20 lines/min): `route early-out: plan N ms (caller|worker, path|no path): world A* a calls f fails x ms, provable p (car c colour k memo m) y ms, wrong w`. With `[RouteAsync] LogFallbackMs` set, it sits right before the matching `route async: sync plan` line (same thread, inner scope).
- Every 60 s: `route early-out: mode ... plans ... A* ... provable ... wrong ... slow N (ms, A* ms, provable ms, saved S), reach max R, labels L (us)`.
- Counters v78 (`docs/registry.md`): `eoMode` `+0x3260` ... `eoSlowSaved` `+0x32B0` (after v77 AvoidRO's last field; built at `+0x3174` on its branch, moved at the 0.36.12 merge); `eoSlowSaved` = slow requests whose provable time alone would bring them under `LogMs`.
- Selftest `TestEarlyOut` (`native/ts3native/earlyout-test.h`): fake R (2 chunks, byte-3 and byte-7 edges, an isolated waypoint), fake container and a fake search that floods like the game's (circle rerun through the detoured entry): car proof and no proof in one component, labels kept per R and refused for an out-of-range reference, colour proof and none with the water flag, memo store / proof / no proof inside the reached set / key changes / goal ref and goal point outside the key, a wall that opens behind the memo counted as `eoWrong`, no store after a restricted, capped or zero-factor search, a restricted search proven from an unrestricted memo, nested request scope, slow-request counters, Mode 0 pass-through, `CallPlan` scope on and off. Plus the v78 layout asserts.

## 5. Acting mode (design; built 2026-10-22 as §9, which replaces step 2's hooks with a content print and adds tie refusal)

`Mode=2`, after the probe shows `eoWrong` 0 over the A/B and a useful `eoSlowProvMs`:

1. On a proof, return 0 from the A\* hook without calling the game, after writing `*best` as the search would: the start ref, then the reachable node with the smallest heuristic. Car and memo proofs know the reachable set (the label class; the memo bits), so `best` is computed by one pass over it with the game's own heuristic call (cost function with the edge flag 0), first node in chunk/record order on exact float ties (the game: first in push order; the two differ only on exactly equal distances). Colour proofs do not know the set: act on them only where `best` is unused, or not at all.
2. Memo epoch: drop the memo when a blocked-bit writer (`0x82B590`, `0x82A190`) or a lot graph build (`0x63CB90`) runs inside the request (the counting stubs of `[RouteSnap]`/`[ProbeValues]` already sit there; docs/68), so §3.3's assumption is enforced instead of assumed.
3. Keep the closed bits consistent for the planner: the game does not read them after the A\* returns [inferred: no reader outside `0x676260` and its clear `0x673500`/`0x6735C0` was seen in docs/99]; checked by the shadow run of Mode 2 (both answers compared on the first N proofs).
4. Same answer, same blocker, fewer searches; the result line in the log and `RoutePlanResult` stay the game's.

## 6. Expected effect on the freeze lines

Upper bound = the provable share of world-A\* time in a slow plan; the probe measures both numbers per plan.

- **Car-only plans into a lot (the BP 4,060 ms line):** every mode-2 A\* call is provable (car proof). The world A\* share of that plan is unknown: a mode-2 flood is small (Bridgeport has 2,325 street waypoints), so if the plan is mostly A\*, it is mostly many candidates (K x 2 floods); if `eoSlowAsMs` is small, the time is in the per-candidate lot legs, and the fix moves up a level: one proof per goal lot before the entry loop (mode 2 and a goal in a lot = no world path at all), which needs a hook on the planner, not the A\*.
- **Pedestrian plans cut off by walls the colours do not see:** the memo saves K-1 of K floods in the goal-lot loop and the open-ground goal loop; the first flood is always paid (~100-200 ms in SV/BP; the longest single search measured in Bridgeport was 188 ms, `[RouteBudget] MaxUs` note in `native/ts3native/ts3native.ini`) [inferred scale].
- **Not reached:** plan-level retries with other flags (other key), the obstacle replan (relaxed flags), C# fail-path plans (separate requests), lot-side searches. If the 7 s lines are mostly those, the early-out is the wrong tool and the line split says so.

## 7. A/B queue (not run)

`tools/harness/queues/early-out-ab.json`, 8 jobs: BP and SV `rare-route-bits` (6 min) and `rare-big-crowd` (8 min), each OFF (`[RouteEarlyOut] Enabled=0`) then ON (`Enabled=1 Mode=1 LogMs=50`), seed 12345, the freeze-data saves and repros, both arms `LogFallbackMs=20` and `LogFallbackStack=8`. Before the run: build this commit (`native/ts3native/build.cmd`), copy `native/ts3native/out/ts3native.asi` to `game-copy/ts3native.asi.eo142`; `minCounters 78`. Pass: no crash, `eoReady` 1, `eoWrong` 0, watchdogs 0. Read per slow sync plan: the early-out line before it (A\* ms of the plan, provable ms, which proof), the script stack line (which managed caller). Decision rules: `eoWrong > 0` = the argument broke, stop and find which assumption (§3.3 list) before anything else; `eoSlowAsMs` well below `eoSlowMs` = the freezes are lot-side or retries, design the next pre-check there; `eoSlowProvMs` close to `eoSlowAsMs` = build Mode 2 (§5).

## 8. Effort

Static study and probe build: ~3 h (agent, offline). Game: the A/B queue, 8 jobs (~1.5 h of game time; correctness and the line split are valid on a noisy machine, timings only on a clean night). Mode 2 if the probe says so: ~2-4 h build + one A/B + 2 clean runs before turning it on.

## 9. Acting early-out (`[RouteEarlyOut] Mode=2 Act=1`, ts3native 0.36.22, counters v86; built 2026-10-22, offline, not run)

**Offline build, selftest ok, steerro-test K6E against the game's own A\* (seeds 1-3 x 3000) ok, no game run.** Ledger rows: docs/51 §227. Counters v86 sit after v85 (H2) at `+0x3664` (built at `+0x364C` on the branch, moved at the merge).

### 9.1 What a failed search leaves, re-read from the code

The acting path must leave exactly what `0x676260` leaves when it fails. Re-read from the decompile and the disassembly of `0x676260..0x67642A` (`game-copy/analysis/decomp/astar-676260.asm`) [verified]:

| Step (game order) | Address | What it writes | Acting path |
|---|---|---|---|
| circle prepass when `r²` (`opts+0xA0`) is 0.0 at entry | `0x676292` (stdcall, 4 args: R, opts, &start, &goal; returns AL) | centre and `r²`; on a failed inner run the A\* clears `r²` again (`0x6762C4`) | calls `0x673600` itself, clears `r²` when it accepted (the inner restricted run fails whenever the full one does) |
| start / goal refs | `0x6762D2`, `0x6762DA` | `opts+0xA4` = start, `opts+0xB4` = goal | same |
| LONG set | `0x6762E0..0x6762EB` | `opts+0x64..+0x87` = container `+0x4C` (9 dwords) | same |
| closed bits cleared | `0x6762FB` (`memset`) | container `+0x10`, `+0x08` bytes | rewritten with the reachable set (below) |
| h0 | `0x676388` (cdecl, 8 args, `add esp,0x20`; float in ST0) | - | the same cost function by address (`kCostFns`), edge 0, edge byte 0, parent null |
| SHORT set when threshold >= h0 | `0x676393..0x6763AA` (`fcomip`, `jc`: NaN keeps LONG) | `opts+0x64..+0x87` = container `+0x28` | same, compared as double |
| 1/x slots | `0x6763B0..0x676404` (divide when the factor is non-zero or NaN) | `opts+0x7C/+0x80/+0x84` = `[0x107A538]` / factor | same, float division |
| flag `0x40` | `0x67640C` | `opts+0x68` = `[0x11587D0]` | same |
| `*best` = start | `0x67642A` | `*best` | same |
| loop: per generated node, heuristic h; `h < min` (strict, float) moves `*best` | `0x67672A`, `0x67694D`, `0x676B8A` (float `comiss`/`fcomip` of stored floats, `jbe` skips: NaN and equal keep the old one) | `*best` | one pass over the reachable set minus the start, heuristic by the same call |
| open list empty | `0x676BF7` | frees the heap array; node pool back to its free list | nothing (pool contents are not an output) |

- **Generated nodes = closed set minus the start** [verified, code structure + K6E]: a node is pushed only when its edge passes the 1e8 wall and it is not closed; every pushed node is popped and closed before the open list empties; the start is closed first and never pushed again. So the final `*best` is the first node in push order with the smallest heuristic below h0.
- **Ties**: the game breaks equal minima by push order, which only the search knows. The acting path counts the nodes at the final minimum and refuses when two or more share it (`eoActTie`, the game answers). K6E found such ties on the byte grid (13 in ~5,200 answers); the first K6E run without the check failed on one (memo, mode 3: same closed set and context, best `0x0009000A` vs `0x0002000A`) [verified].
- **Closed bits are now written as the game leaves them**, so §5 step 3 (no reader after the return, inferred) no longer matters.

### 9.2 The proofs, and when their data is stale

| Proof | Acts? | Why it is exact | Stale data and what happens [tag] |
|---|---|---|---|
| car (mode 2) | yes | Mode 2 walls every non-byte-3 edge and every virtual end, with no blocked, point, polygon or circle test on byte-3 edges (docs/99 §10.1) [verified]. The acting path does not trust the labels for the set: a BFS over the **live** R's forward byte-3 references writes the closed bits; if it reaches the goal the labels were stale and nothing acts (`eoStale_0`, no proof counted) | R's records change only through `WorldRoutingData_LoadChunks` (BuildRoutingData / world save, docs/99 §1.9); blocked bits, lot loads, build/buy, doors and portals do not touch mode-2 walls [verified docs/99 §10.1, §3.4]. The BFS reads R at call time, so nothing can be stale |
| colour (modes 1-3) | **no** | the game's colours bound the reach but do not give the reachable set, so `*best` is unknown | observe only, as in Mode 1 (`eoActRefused`) |
| memo (same request) | yes | a failed unrestricted search's closed set is the start's reachable set under its wall context (I-203-8, docs/99 §10.1) [verified rules] | **wall print** (below) at the memo's search and at each use; a different print = no proof (`eoStale_1`), the game answers [verified, selftest + K6E] |

- **Cost sets (bug in v78)**: `EoCostSetsPlain` required all 18 floats of SHORT and LONG to be positive, but slots 6-8 of each set are the 1/x values the A\* computes into the context; the container holds 0 there (the compiled tuning, K6Ctr, docs/108 §7 F-1). **The v78 probe could never store a memo in game** [verified, code + K6 container]. Fixed: factors 0-5 of both sets finite and positive, and the largest edge factor of docs/99 §10.1 (`keep x max(1000 trespass, 10/sidewalk, 10/riding, 10/street, 30)`) times the longest edge (182 m) below the 1e8 wall, so no factor product makes a wall in one set and not the other. The Mode 1 probe now stores memos too.
- **Wall print (replaces §5 step 2's writer hooks)**: the entries `0x82B590`, `0x82A190` and `0x63CB90` already carry stubs of `[RouteGate]` (on by default), `[RouteSnap]`, `[ProbeValues]` and `[LoadProf]` (docs/68 rule 3: one owner per entry), so a fourth detour there would SKIP. Instead the memo keeps an FNV print of what those writers change, compared at every use: R's record pointer, size, per-chunk counts and every record's flags byte (blocked bit and type bits: `ApplyLotUpdate`, `RestoreLotBlockedBits` from lazy lot builds, shoreline rewrites), the lot-edge group hash head `W+0xA0..+0xAF` (`ApplyLotUpdate`, `LoadLotData`, p4clone.cpp [verified xrefs there]) and the contents of the start/goal group sets `opts+0x18`/`+0x48` (EASTL trees walked, order-free hash). Cost: one walk of R's records per memo store and use (SV ~27,000 records, tens of µs [inferred]); a memo is kept only behind a failed search of at least `MemoMinUs` (1 ms).
- **Not covered by the print** [inferred]: the world-manager point test `0xC6BAB0` (type-2 / `0x15` targets) and the segment test `0x627780` (edge bytes 2 and 7, footprints and the job's polygon set). Inside one request the plan holds `+0x918`; footprint writers take it (docs/89) [verified there]; lot routing writers on another thread wait at the gate sites with `[RouteGate] Enforce=1` (docs/60) [verified there]; world-manager lot state changing between two searches of one request is the open case. Sampled live: the first 32 proofs and every 16th after are shadowed (both answers compared, `eoActDiff`; the game's answer returned), and the first difference turns acting off for the session (`eoActOff`).
- **Build/buy, doors/portals, lot loads**: doors and portals are lot-side (lot graph, portal A\*) and never part of R's edges [verified §3.4]; build/buy exit and lot loads reach R only through `ApplyLotUpdate`/`LoadLotData`/`RestoreLotBlockedBits` (print). A memo never outlives its request.
- **Not acted**: restricted entry (`r²` set at entry: one restricted search over an unknown subset), a capped `[RouteBudget]`/`SyncCapMs`/`FbJobCapMs` budget (the game's own run ends at once with `*best` = start), modes outside 1-4, a container table shape the pass does not know (`eoActRefused`). The act path calls the cost functions by address, so `[RouteBudget]`'s stubs at the A\*'s `mov [esp+0x60]` sites do not count those heuristic calls.

### 9.3 What was built

- `native/ts3native/earlyout.cpp`: `EoCarFill` (BFS into the closed bits), `EoActPrologue` (the table of §9.1), `EoActBest` (heuristic pass, tie refusal), `EoWallPrint`/`EoSetHash`, `EoAct`; `EoAsHook` acts on car and memo proofs, shadows on schedule (`ShadowFirst`, `ShadowEvery`), latches off on a difference. The per-request line adds `acted a (car x memo y) ~z ms saved`; a second dump line `route early-out act: ...`.
- Ini `[RouteEarlyOut]`: `Mode` 0-2, `Act` (Mode 2: 0 = shadow every proof, 1 = act), `ShadowFirst=32`, `ShadowEvery=16`, `MemoMinUs=1000`. `--ab earlyout` still toggles `eoMode` 0/1; with Mode 2 in the ini, on = acting.
- Counters v86 (`+0x3664..+0x36AB`, 18 int32, total 3499): `eoAct` (0 probe, 1 shadow, 2 act), `eoActed`, `eoActedWhy[2]`, `eoActUs`, `eoActMaxUs`, `eoActSavedMs` (nodes x the shadowed game µs per node - act µs, per kind), `eoShadow`, `eoShadowUs[2]`, `eoShadowNodes[2]`, `eoActDiff`, `eoActOff`, `eoStale[2]`, `eoActRefused`, `eoActTie`.
- Selftest `TestEarlyOut` (`earlyout-test.h`): the fake search now does what §9.1 lists (circle rerun, context writes, h0, SHORT/LONG, 1/x, `*best`); acting cases with and without the circle pass: car unreachable answered identical to the fake game (result, best, context `+0x64..+0xBB`, closed bits) without calling it; car and memo reachable never cut; memo answered identical; stale print (a flags byte flipped) falls back, and with a wall opened the game finds the path (no `eoWrong`); colour, restricted and capped refused; stale car labels (an edge byte changed in place) overruled by the BFS; shadow every proof with no difference; a forced difference latches acting off; the shadow schedule; ini levels.
- steerro-test **K6E** (`steerro-test.cpp`): on the K6 fake world, the game's own `0x676260` (real circle prepass, cost functions, closed list `0x6735C0`) without the hook vs the same call through `EoAsHook` in act mode (no shadows), modes 1-4 with K6Sweep's flags, after blocked-bit writes too; a second goal on the same opts for memo answers; an R flags byte flipped after each memo answer. Seeds 1-3 x 3000: 5,235 answers identical, 810 car and 476 memo answers acted, 2,652 reachable goals never acted, 476 stale memos answered by the game, 13 heuristic ties left to the game, 21 car-only failures not acted (ties or start = goal), `eoWrong` 0.

### 9.4 Queue `tools/harness/queues/earlyout-ab.json` (not run)

asi `game-copy/ts3native.asi.0322` (0.36.22 merge build with H2, sha256 `ab2f6a7945793a11f2800510e02546c4591a1f06c2082c6f5722cb20b1ee3184`), the freeze-fix settings of fbfix-ab in every job, `Mode=2 Act=1`. Job 1 **`bp-earlyout-verify`** (day, 5 min BP `rare-route-bits`, noisy OK): pass = 0.36.22 / v86 lines, `eoActed` > 0, `eoWrong` 0, `eoActDiff` 0 after >= 32 shadows, `eoActSavedMs` > 0, no crash. Jobs 2-7 (clean night, 10 min, `--ab earlyout`, off-first + on-first): BP route-bits, SV `rare-big-crowd`, IP play. Read the pair means of the worst hitches and the `long job` / sync plan lines against the early-out lines.

### 9.5 Effort

Build, tests and docs: ~3 h (agent, offline). Game: 1 day job (~10 min) + 6 night jobs (~1.5 h), then 2 clean runs before it ships on.

## 10. Live result of the acting mode (queue `earlyout-ab`, 2026-10-22, 7 jobs; read 2026-10-24)

Read from the ts3native logs of the 7 jobs (`probe-out/queue/earlyout-ab-2026-10-22-*-ts3native.log`, last `route early-out` dump of each run; asi `0322`, `Mode=2 Act=1`, `eoMode` toggled by `--ab earlyout` in the pairs) [measured]:

| job | acted (car / memo) | shadows | `eoActDiff` | `eoWrong` | world A\* fails, ms | slow plans (>= 200 ms): ms, of which world A\* ms |
|---|---|---|---|---|---|---|
| bp-earlyout-verify (5 min) | 262 / 0 | 48 | 0 | 0 | 254 | 3: 9,119, A\* 0 |
| bp routebits off-first | 84 / 0 | 36 | 0 | 0 | 121 | 2: 4,066, A\* 0 |
| bp routebits on-first | 92 / 0 | 37 | 0 | 0 | 121 | 2: 3,807, A\* 0 |
| sv crowd off / on-first | 0 / 0 | 2 / 1 | 0 | 0 | 2 / 1 | 4: 3,685, A\* 15 / 2: 498, A\* 12 |
| ip play off / on-first | 0 / 0 | 4 / 4 | 0 | 0 | 4 / 4 | 13: 3,319, A\* 1,419 / 10: 2,306, A\* 513 |

- **Correct:** 132 shadowed proofs, no difference; `eoWrong` 0 and no `act OFF` line in any job. I-227-6 holds live; I-227-7 has no counter-example (memo proofs never fired, so it is not exercised).
- **Gain ~0:** `eoActSavedMs` 0 in every job. The BP 4,088 / 4,192 ms car-only plan made 237 / 252 world A\* calls that **all fail in 0 ms together**: the shadowed car floods average 1 node (36 nodes in 36 shadows), i.e. the start has no byte-3 street edge and the game's own search ends at once. The seconds are not in the world A\*; I-203-10 is refuted. In SV and IP the slow plans succeed or fail elsewhere (A\* 0-43% of the slow-plan time, none of it provable).
- **Verdict:** the acting mode is exact but has nothing to cut. No new build (an "eo2" asi or a night A/B of this switch) is worth game time. The multi-second plans belong to docs/164's per-phase split (cell A\*, lot legs, candidate loops, goal step) and its wall-deadline design (§4 there); the per-candidate loop cost of the car-only plan is the candidate loop itself, not the flood. Keep `[RouteEarlyOut]` off (default).

## 11. Lane check 2026-11-22 (world-scale lane, docs/320 §3 item 1): nothing to build

- docs/320 listed the `[RouteEarlyOut]` apply mode as "probe never run". Wrong: the acting mode (§9) was built and run on 2026-10-22 (§10, 7 jobs): exact (132 shadowed proofs, `eoActDiff` 0, `eoWrong` 0) and **~0 saved** [measured]. The BP 4 s car-only plan's 237-252 world A\* calls fail in 0 ms together; SV crowd and IP slow plans are 0-43% world A\*, none provable. So a failed world A\* flood is not where the multi-second plans spend their time today, and no new early-out build is worth game time. docs/320 is corrected.
- The time sits in the planner around the A\* (candidate loops, lot legs, cell A\*, goal step): docs/164 `[RouteJobPhases] SyncPlans=1` splits it by phase and `[RouteAsync] PlanDeadlineMs` / `FailMemo` (docs/164 §7) are the caps built for it. That is the follow-up for large worlds, not a new A\* pre-check.
- **On a 4x or streamed world:** the early-out code already sizes everything from R at use (car labels and the closed-bit copy are heap blocks sized from R's chunk and record counts, rebuilt when R's pointer, size, w or h change; no fixed world size), so it stays correct on a larger R; the only fixed bound is the group-set walk (`EoSetHash`, 1,024 entries: a larger set refuses the memo, it never acts on it). A 4x world makes a failed flood ~4x longer [inferred, docs/320 §2 row 1], so if the phase split ever shows world-A\* floods leading on a large world, the switch is ready (`Mode=2 Act=1`, off by default; car and memo proofs only; household plans included because the answer equals the game's by construction, shadow-checked).
