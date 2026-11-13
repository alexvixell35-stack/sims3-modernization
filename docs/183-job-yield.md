# 183. The last two 1 s+ routing freeze classes: caller ranking and JobYield (v0.1.6 groundwork)

PLAN.md "After the release (Rodney 2026-10-24)" item 4: own-job wait fix and main-thread plan callers one by one. §1-§5: static work and a build only. §6 (2026-11-13): the first in-game A/B, measured; proposes JobYield on from the next version.

## 1. Caller ranking from the existing logs (Part A)

Sources: `node tools/harness/nostart-report.ts --filter 'sv-soak-rel015|sv-soak-v014-2356|sv-soak-cand-1721|release-check-2026-10-24|rvb-rel015' --jobs` (29 logs, 371 sync plans, 50.9 s) plus a parse of the `route async: long job` lines in the same logs. A sync plan of N ms is a sim iteration of at least N ms.

### 1.1 Sync fallback plans (the sim thread plans itself)

| Caller (script stack) | Plans | Sum s | >= 300 ms | >= 1000 ms | Max ms | Reasons | Worlds |
|---|---|---|---|---|---|---|---|
| **(no stack)** | 24 | 16.0 | 10 | 8 | 3600 | Foreign 11 (v0.1.5 SV soak), NoStart 10 (v0.1.4 SV soak), M1Pending 3 | sv, ip |
| test agent `Ts3Agent.Handlers.*` (repro commands, not play) | 16 | 6.1 | 4 | 2 | 2217 | Foreign 16 (rc-on-routegate) | bp |
| `LotManager.RunStuckCheckHelper` (stuck check alarm) | 51 | 4.8 | 1 | 0 | 423 | Foreign 51 | all |
| `LiteDriver.Simulate` (traffic car) | 73 | 4.6 | 0 | 0 | 105 | Foreign 73 | bp |
| `Boat.RouteBoatNearHouseboat` < `GetOutOfBoat.Run` | 18 | 2.7 | 0 | 0 | 243 | Foreign 18 | ip |
| `GoHere.RouteActorToDestination` | 25 | 2.6 | 0 | 0 | 229 | QueueFull 25 | repro |
| `Lot.PlanToLot` < `Boat.RouteBoatToLandOnWayToPosition` | 24 | 2.6 | 0 | 0 | 169 | Foreign 24 | ip |
| `GoHome.Run` -> `Lot.PlanRouteIntoLot` / `PlanToLot` | 21 | 1.5 | 0 | 0 | 117 | mixed | bp, ip |
| `ServingContainerGroup.RunBodyForGrabAServing` | 1 | 0.4 | 1 | 0 | 397 | M1Pending | ip |
| every other caller (30 rows) | 118 | 7.6 | 0 | 0 | < 250 | | |

Per run, plans >= 300 ms:

| Run | Build / settings | Sync plans >= 300 ms (ms / reason) | Stacks |
|---|---|---|---|
| `release-015-extra-...-sv-soak-rel015-0454` | v0.1.5, FallbackAsync 0 | 1091, 823, 1056, 1037, **3600** / all `IC Plan, Foreign, nojob` | no (LogFallbackStack=0) |
| `overnight-1023-...-sv-soak-v014-2356` | v0.1.4 | 1025, 332, **1654**, 1000, 1294 / all `IC Plan, NoStart, nojob` | no (build before the key) |
| `overnight-1023-...-sv-soak-cand-1721` | asi.0338, FallbackAsync 1, FbJobCapMs 300, FbJobDeadline 1 | none (max 164 ms) | yes (16) |
| `release-check-2026-10-24-rc-on-routegate-0135` | release check | 384, 2217, 2033, 495 / Foreign | yes: test agent |
| `release-check-...-rc-ab-pet-guard-loop`, `-rc-ab-ip-refcount-ring-loop` | release check | 423 Foreign, 397 M1Pending | yes |

Notes:
- The `caller` field of the sync plan line is one value per run (`408F2827`, `40732227`, `41112827`): it is the managed call stub, not the script caller [verified: the same value on every line of a run]. Only the stack line names a caller.
- **Stacks were logged** (`LogFallbackStack=16`) in `sv-soak-cand-1721` and every `release-check-2026-10-24-rc-*` / `*-rc-smoke` job. **Not logged** in the four `release-015-extra` jobs (`LogFallbackStack=0`, the release ini) and in `sv-soak-v014-2356` (v0.1.4 build). So the two SV soak classes that matter, v0.1.5 `Foreign` (worst 3,600 ms) and v0.1.4 `NoStart` (worst 1,654 ms), are both unattributed.
- In the candidate soak the same class ran as foreign jobs (FallbackAsync 1): 11 foreign jobs >= 300 ms, max 356 ms, cut by FbJobDeadline (`cut jobs cell 11`); none >= 1 s. What was left >= 1 s there is §1.2.

**Cause run that names the unattributed ones:** SV soak, `Baseline-SV-Spring`, seed 12345, 45 min, v0.1.5 release ini (FallbackAsync 0 keeps the Foreign class synchronous and visible) plus `RouteAsync.LogFallbackStack=16`, `RouteAsync.LogFallbackMs=20`, `RouteAsync.OwnJobWait=1`, `HitchAttr.Enabled=1`, `HitchAttr.Holders=1`, on a v109 build (the `route jobs: sim waited ... at <site>` lines of §2.3). Read with `nostart-report.ts --jobs` and `freeze-report.ts --min 1000`. Without the stack key the run cannot name a caller; LogFallbackStack 8 shows only 2 game frames (docs/141), use 16.

### 1.2 Own-START jobs (the Sim's own task planned async; the sim thread still froze)

| Run | Own jobs >= 300 ms | >= 1000 ms | Longest (ms) | Shape |
|---|---|---|---|---|
| `sv-soak-cand-1721` | 8 | 6 | 2281, 1504, 1229, 1182, 1179, 1170 | every one `route early-out: plan N ms (worker, no path)`: a failing plan flooding the reachable SV world (docs/142) |
| `sv-soak-rel015-0454` | 2 | 0 | 310 | |
| `rc-ab-bp-overwatch-ab-loop2` | 4 | 0 | 757 | |
| `rc-on-routegate` / `rc-on-ts3fixes-v014` | 1 / 3 | 0 | 852 / 565 | |
| `sv-rvb-rel015` | 4 | 0 | 474 | |

In cand-1721 the five freezes >= 1 s (1517, 1178, 1188, 1238, 2273 ms, seconds 1653-1664 of the run, one Sim re-planning a no-path route) match the long own jobs one to one by length. `freeze-report.ts --min 1000` classes all five as **other**: no route wait key, no sync plan, script runs <= 5 ms in those iterations, every `rt*` field 0 [verified, hitches.jsonl]. So the sim thread was blocked **outside every timed stage and outside every route wait we note** (RunPlan, RtWaitJob, the +0x918 pump, DiscDrain all note theirs). The one sim wait for a job we did not note was `WaitJobDone` (IcCollect, DropJob); a wait there would sit inside a task run, which the task timer would show. PLAN.md's "sim in WaitAsyncDone/WaitJobDone" is therefore **inferred, not verified**: open question O1 (§4). The only watchdog line of the run (`thread 23508 waited 2 s for the job of route 256D049F`) is the 2,281 ms job, from one of those two functions, but the thread is not named as the sim.

## 2. JobYield (Part B): design and build

### 2.1 Design in five lines

1. The game can only yield in managed code at a task's root interpreter state (docs/18; `CanYield`), so the step-aside lives in ts3fixes, before the IC that would wait.
2. New query JOBBUSY (GetOption2, optHi `0x85000000`, unnamed bit 63: the game and older builds answer false): ts3native answers true when a route job is running, this is the job owner thread outside a job callback, and `CanYield()`; it serves one pending managed call of the job as READY does.
3. ts3fixes `StepAside()` (switch `jobyield`, tunable `kJobYield`, default off) runs right before every plan that is about to run synchronously: a refused START in `PlanAsync`, a refused A10 kind START (`AsyncKindStart`), and the READY poll cap in `AsyncWait`; it sleeps the task a tick at a time while JOBBUSY, capped at 10 s real time.
4. The plan then runs with no job to wait for; the old path (native wait in RunPlan / IcCollect) stays as the fallback for everything JOBBUSY refuses (nested call, no task, switch off, the cap).
5. Every sim-thread job wait is now counted by site (`swN/swUs/swYieldable[6]`, line >= 300 ms with site, own/foreign and "task could yield"), so one run says how much of the remaining freeze time a C# step-aside can reach and names the site of the cand-1721 freezes.

### 2.2 What it changes

- ts3native 0.36.38, counters **v109** at +0x45CC (docs/registry.md): `jyMode` (switch, `[RouteAsync] JobYield`, `native_set jobyield`, harness `native-switches.ts`), `jyAsks`, `jyBusy`, `swN[6]`, `swUs[6]`, `swMaxUs`, `swYieldable[6]`, `swLines`. Sites: 0 RunPlan (sim sync plan), 1 RtWaitJob (gate sites), 2 DiscDrain, 3 IcCollect, 4 DropJob (CANCEL, same route again, mode off), 5 other (P5c). `WaitAsyncDone(site)` / `WaitJobDone(j, site)` take the site; the wait loops themselves are unchanged (a wait is timed only when it found a job). Minute line `route jobs sim waits: runplan n/ms/yieldable ... job yield mode m asks a busy b`.
- ts3fixes `RouteBackoff.cs` `StepAside`, counters [248] step-asides, [249] ticks slept, [250] capped (array `int[251]`); ts3agent `patches` names `jobyield_steps`, `jobyield_ticks`, `jobyield_capped`.
- Not changed: the own task's own wait (it already sleeps on READY), the gate-site waits inside ICs (RtWaitJob: deep in native code, no C# point before them), the +0x918 pump, DiscDrain, CANCEL from a reset (an exception path cannot sleep, and the route must outlive its job).

### 2.3 Safety

- Same yield rule as the READY poll that ships today (MayYield = `CheckYieldingContext` + ts3native NOYIELD), and JOBBUSY re-checks `CanYield()` natively. A sleeping task is what an async Route.Plan already does every tick.
- No deadlock: while the task sleeps the sim thread keeps serving the job's managed calls at its usual points, and each JOBBUSY poll serves one more.
- Starvation: back-to-back jobs keep JOBBUSY true; the 10 s cap then falls back to today's native wait (counted in [250]).
- Ordering: the plan now runs a few ticks later than before (the Sim waits in place, as with an async START). The plan's inputs are read when it runs, as for any async plan.

### 2.4 Build and self-check

`native/ts3native/build.cmd out-jy`: `selftest ok`; new `TestJobYield` (jobyield-test.h, on TestRouteAsync's real worker): JOBBUSY off / on / nested call / no job, the step-aside loop ends with the job, collect and drop waits counted with the yieldable flag, no wait counted without a job, >= 300 ms line and its rate limit. The 11 `k.version` asserts and the 19 `sizeof(Ts3nCounters) == 4490 * 4` asserts moved to v109. ts3fixes compiles (csc against the main checkout's `game-copy/assemblies`; `build-fixes.ts` itself needs the worktree's game-copy, not touched here).

### 2.5 Repro and queue (drafts, not run)

- `tools/harness/repros.json` `route-ownjob-yield-loop`: SV, 30 spawned Sims + household, `crowd_unreachable lot home` (own-task Go Here to an enclosed spot: every own-START job floods and fails, the cand-1721 shape), 8 slots fill and the rest plan sync behind a running long job; ABBA on `native_set jobyield` + `patches jobyield`, 50 plans per arm.
- `tools/harness/queues/job-yield-ab.json`: job 1 day correctness (both on, cause logging: sw lines, OwnJobWait, LogFallbackStack 16, HitchAttr Holders, HostSplit), job 2 clean-night ABBA judged with `hitch-ab.ts --scenario`.

## 3. Estimated reach [inferred]

JobYield reaches only the waits a C# caller can see coming: sync fallback plans behind a running job (site 0) and the poll-cap collect (site 3). In cand-1721 the >= 1 s freezes had no route wait noted at all, so the fix's share of those is unknown until job 1; for the v0.1.5 SV `Foreign` sync plans (FallbackAsync 0) the plan's own search time is the freeze, which JobYield does not shorten (FallbackAsync 1 + FbJobDeadline did, §1.1).

## 4. Open questions

- **O1** Where does the sim thread wait during a long own-START no-path job in SV (cand-1721 "other" freezes)? Candidates: an unnoted native wait outside the four scheduler seams (our sim-loop callbacks at `0xD81FF6`, a GC stop waiting on the worker, a lock taken without `Lock_Acquire`). Job 1 answers it: a >= 1 s hitch with a `route jobs: sim waited ... at <site>` line names the site; without one the stall is outside our job waits and the next step is a stack sample during the freeze.
- **O2** Who are the v0.1.4 `NoStart` / v0.1.5 `Foreign` multi-second SV callers? The cause run of §1.1.
- **O3** Does sleeping before a sync plan change behaviour (a Sim waiting a few ticks longer before a fallback plan)? Job 1 checks stuck Sims and route-fail reactions.

## 5. Ledger

I-183-1 (inferred): the cand-1721 >= 1 s freezes are sim waits on the long own-START job (lengths match one to one). I-183-2 (inferred): that wait is not in RunPlan / RtWaitJob / pump / DiscDrain (all noted, all 0 in those iterations). Both verified or refuted by job 1.

## 6. In-game A/B (2026-11-13) [measured]

Setup: the bridge test copy (Apex aside), ts3native main 2f50e37f plus #378, release ini, in-load A/B `--ab jobyield` with `--fixes-on jobyield` (the native switch and the ts3fixes step-aside together), 2-min slices, 10 min, both start sides. Isla Paradiso is where the sim thread waits most: in the same night's IP profiles, 97% of the `threaded` >= 300 ms time with no route search was waiting on the route worker (`rtLock` 250 s + `rtJob` 243 s of 509 s).

| Run | Game | Hitches per 10 min, off -> on | Note |
|---|---|---|---|
| jy1 | IP seed 12345, both orders (pair mean) | >= 300 ms 227.6 -> 121.4 (0.53 [0.39, 0.73]); >= 500 ms 140.7 -> 75.0 (0.53 [0.35, 0.79]); >= 1 s 134.5 -> 55.4 (0.41 [0.26, 0.64]); >= 2 s 2.1 -> 5.4 (4 events, too few) | p99 670 -> 130 ms, p99.9 861 -> 723 ms, max 2,312 -> 1,558 ms |
| jy1 | IP seed 2024, on first | >= 300 ms 358.8 -> 161.0; >= 1 s 61.8 -> 14.6; >= 2 s 11.8 -> 7.3 | |
| jy1 | IP seed 2024, off first | >= 200 ms 49.1 -> 32.1; >= 300 ms 7.5 -> 3.2 | a quiet game |
| jy2 | BP, on first | >= 100 ms 212.7 -> 166.9; >= 200 ms 7.6 -> 0 | no >= 300 ms in either arm |
| jy2 | SV, off first | >= 100 ms 76.0 -> 42.2; >= 300 ms 0 -> 4.7 (1 event, 374 ms) | |

- Step-asides: 326-499 per game, 2,459-6,590 ticks slept, `jobyield_capped` 0 in all 6 games.
- Stuck Sims: in jy1 the on arms ran more stuck checks (597 against 418: a smoother game simulates more Sim time in the same minutes). Resets per check 6.0% on, 5.3% off. jy2: 0 resets in either arm.
- Household Sims are included by design: a household Sim's task that would wait for a running job sleeps a few ticks first, as an async plan does, instead of the whole game freezing. Plan results are not changed. **Rodney 2026-11-13: accepted as exact for household Sims** ("same plan result, task sleeps a few ticks instead of the whole game freezing"), so JobYield has no household exemption. Merged as Dalexlol PR #397, ON from ts3native 0.36.253 (`JobYield=1`, ts3fixes `kJobYield=True`); results: docs/data/results/2026-11-13-jobyield-ab.md.
- Not checked: the >= 2 s class (too few events in both arms), and a soak longer than 10 min.
- Rough spots noted at merge, not blocking: the >= 2 s class went 2.1 -> 5.4 per 10 min in the IP seed 12345 pair (4 events); SV >= 300 ms 0 -> 4.7 (1 event, 374 ms). Watch both in the release soaks.
- O1-O3 (§4): O3 is answered for stuck Sims (no rise per check); route-fail reactions were not compared, because the routefail lines share ts3native's 400-line text cap.
