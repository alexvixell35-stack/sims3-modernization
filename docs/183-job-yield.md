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

## 7. Household stuck upstairs in JobYield storms: what the logs show, and the state a sleep left behind (2026-11-21)

Branch `fix/jobyield-strand-1121`. Built (ts3fixes), host tests pass, native selftest written for the MSVC CI build, NOT RUN in the game.

### 7.1 The finding and how far the data goes

- Isla Paradiso route storms, same seed, Collins household (Colonial Casa). In three runs (s229, s241, s265) every household Sim that goes to the third floor (y 61, room 11) gets `NoPathToDestination`, blocker None, for every goal on the two floors below: async jobs (7-60 ms, `nodes 0 in 1 plans`, uncut) and the stuck check's own synchronous `PlanFromPoint` pair alike [verified, `routefail:` lines].
- All three had `[RouteAsync] JobYield=1`. Storm runs with waves on this build line: JobYield 1: 3 of 24; JobYield 0: 0 of about 90. But only 7 of the JobYield 0 runs had the same other keys (TaskServe 1, SmoothCut 1: s245-s248, s253, s258, s263), and at 3 / 24 seven clean runs happen 39% of the time. So JobYield is **associated, not proven** [verified counts; cause inferred]. The run plan in §7.5 settles it.
- The block is **one-way and starts before the crowd**: each Sim arrives on the third floor after the first failure there (s229: Rosemarie line 1772, Cristobel 1912, Omar 2011, Christopher 2243; s265: the children 2155 / 2161, Rosemarie 2914, Christopher 3020), so the way up worked while the way down failed [verified]. One Sim got down and came back (s265 Omar: y 61 at line 2569, ground floor at 2749, y 61 again at 3005) [verified]. The Sim at the stair top with `NoDestinationSpecified` appears after the first failure in s229 and s265: a victim, not the start [verified].
- It does not end in the run and it outlasts the storm: s265 fails on after `route_storm: stop` (line 3367; fails to 3532 of 3635), s229 after its stop (3554; fails to 3756 of 3855), about 9 sim-hours in s265 [verified]. No calm phase longer than the tail of those logs was recorded.
- The repro `upstairs-landing-jam` (user-directed Go Here from the same spots) passes with JobYield on [verified, landing1-3]. The storm fails are autonomous plans. A one-way block that passes player commands fits a portal answer given at plan time (the game asks the lot's portal handlers per Sim and interaction; a privacy situation locks the portals into the room of its Sim for autonomous plans only) better than a damaged routing table [inferred, not shown by a log line].
- **Not found in the logs:** the event before the first failure. The step-asides are few (s265: 116 asks, 32 busy answers in the whole run) and were not logged one by one, so no line ties a step-aside to the first failure. §7.4 adds that line.
- Not the cause [verified in the logs]: the no-path memo (served 0), a cut (every failing job `uncut (exempt)`), a deadline (10 s), the three RO clones of s229 (s265 ran without them), PlayerFirst (s265 had it off).

### 7.2 What a step-aside did that the same plan without it does not [verified in the code]

`StepAside` sleeps between a refused START and the synchronous plan. Before JobYield nothing ran between the two, and three things were built on that:

1. **ts3native's records "for the IC right after".** A refused START leaves in single slots: the refusal reason (`g_rfbHandle` / `g_rfbWhy`, which picks the plan's deadline class in `PdIcUs`: `NoStart` / `Foreign` take `PlanDeadlineMs`, every other reason `PlanDeadlineOwnMs`), the stuck check's last-round mark (`g_scSyncH`: that plan is never cut), ts3fixes' scope answer (`g_kdScopeH` / `g_kdScope`) and the UnseenFallback mark (`g_ufH`). During the sleep (a) the next route's marks and START overwrite them, so the plan after the sleep runs as `NoStart` with no scope and no mark, and (b) a plan entered with no route handle (`PdPlanBegin`, `KdSyncCeilUs(0)`, `PdScopeAt(0)`: "the plan ts3fixes is running") takes the sleeping route's scope as its own. A sleeping unseen NPC so lends "unseen" to whatever plans natively in those ticks, a household Sim included: the 300 ms deadline instead of the household ceiling, and every shortcut keyed on the session's scope.
2. **asyncjigsync (docs/240).** A plan made while the Sim's interaction holds an unplaced social jig is kept synchronous so the partner cannot run before `AddToWorld`. `PlanBackoffBody` then reached `StepAside`, which slept when a job ran: the docs/226 race again, only under a busy worker.
3. **A route that is being followed.** ts3native refuses a kind START for it, so that plan (a Replan in the middle of a walk) was always synchronous; with JobYield the Sim walked on along the old path while its task slept.

None of the three is shown to be the cause of §7.1. They are what the yield path left behind or changed, and each is closed in §7.3.

### 7.3 Fix

- ts3native (`jobyield.cpp`, `jobyield-core.h`): JOBBUSY is asked on the plan's own route. The first busy answer takes that route's four records out of the slots into a small table (8 routes); the answer that ends the sleep puts them back right before the plan. A record leaves the table exactly once: back, dropped (op 2: the sleep ended by an exception, or the route sent a new START) or expired (20 s: the task never came back). A route that is being followed answers "not busy" (`[RouteAsync] JobYieldFollowed=1` restores the old behaviour for an A/B). Counts on the minute line: `records parked / back / dropped / lost, followed routes not yielded`. No counter layout change.
- ts3fixes (`RouteBackoff.cs` `StepAside`, `StepAsideCore.cs`): polls on its own route; no step-aside while the task's Sim or the route's Sim holds an unplaced jig; every sleep ends with exactly one closing message (the "not busy" answer, op 1 at the cap, op 2 from the `finally` when a reset leaves the sleep).
- Old pairs: a new ts3fixes on an older ts3native behaves as before (the handle and the low word are ignored); an older ts3fixes asks on a route with handle 0, for which nothing is parked.
- JobYield's reach is unchanged for every other plan: the task still sleeps instead of the sim thread waiting. What no longer steps aside: plans with a pending jig and plans on a followed route (both wait for the job as before JobYield); their share shows in the two counts.

### 7.4 Tests and logging

- `native/ts3native/jobyield-host-test.cpp` (Linux, no libc): part 1 reproduces the leak on a model of the slots (a household plan with no handle reads the sleeping NPC's scope; the NPC's plan comes back as `NoStart` with no scope and no stuck-check mark), parts 2-5 the same steps with the table (nothing readable during the sleep, the plan it would have been after it, two routes at once, drop, expiry, clock wrap, full table, no handle).
- `tools/selftest/stepaside` (C#): the loop's closing message for job over / cap / reset in the first or a later sleep, the jig rule.
- `jobyield-test.h` step 5 (MSVC selftest): the real slots through `GetOptionHook`: park, another route's marks meanwhile, back once, op 1, op 2, a new START's drop, switched off mid-sleep, followed route with and without `JobYieldFollowed`, an old ts3fixes' handle 0.
- New log line per step-aside (30 a minute): `ts3fixes: jobyield: <IC>, <ticks> ticks <ms> ms, job over|cap|reset, <Sim> in <task>, <interaction>; session: asides N capped N, kept sync for a jig N, resets N`. The next run with a stuck household names what stepped aside before the first failure.

### 7.5 Run plan (repro `ip-jobyield-strand-storm`)

Twelve 2-min storms, seed 152, the s265 key set (PlayerFirst 0, no new RO clones), interleaved: fix, fix, control, three more times; fix = this build with `JobYield=1`, control = the same build with `JobYield=0` (ts3fixes `jobyield` on in both). Pass: 0 runs with two or more household Sims failing from y 61 (scan `strand=`), `records lost 0`, `back + dropped = parked` at the end of each run, no new script error. Then read: 0 / 8 with the fix against 3 / 24 before is still only p = 0.34 for "fixed", so a pass means "not seen", and the claim needs about 24 clean fix runs (p < 0.05) or a run of the old build that strands with the new `jobyield:` line just before the first failure. If a fix run strands: its `jobyield:` lines before the first `routefail:` at y 61 name the plan; next arm `JobYield=0` + TaskServe / SmoothCut one at a time (the other two keys every stuck run shared).
## 8. The strand again after the fix (s321): what the log rules out, and the lines that name the answer next time (2026-11-21)

Branch `fix/strand-1121`. Logging only; ts3fixes built, host self-check passes, native part compiled in CI only, NOT RUN in the game. **No cause named.**

### 8.1 What s321 shows

s321 (2-min Isla Paradiso storm, seed 152, asi of `27a222c8`, ts3fixes with the §7 fix, `LandCheckRO` / `ObstructAtRO` / `Blocker2dRO` serving, `DeferObjInsert`, `DiscYield*`): the same picture as s229 / s241 / s265.

- **Start [verified].** The first fail from y 61 is job seq 988, Cristobel, Use Toilet, about sim hour 16.9; a successful household plan of hers is logged at hour 15.17. The fails before it on this lot are ground-floor ones from room 14 to 934.4 / 55 / 1204.5, which runs without a strand have too (s279, s329, s343).
- **Shape [verified].** 51 `NoPathToDestination` plans from room 11 to y 58 / y 55, four Sims, to the end of the run. 50 are jobs: `plan` 5-39 ms, `host` above 0 in 46 of them, so the planner got as far as asking script about portals (event 59 is its only managed exit, docs/92 §4). One Sim at 934.1 / 61 / 1204.1 (room 0) gets `NoDestinationSpecified` with `0 plans`, is moved to the kitchen by EA's stuck check and fails from room 11 again 840 log lines later.
- **Not the cause [verified in the log].** The no-path memo (`recorded 0 served 0`, fuzzy `served 0`). A cut or deadline (every job `uncut (exempt)`, 10 s). A step-aside of a household Sim: 14 in the run, all NPCs, the last before the first fail 278 log lines earlier (line 2004 against 2282) (`jobyield:` lines of §7.4), so the §7 leak is not what happened here. A memo answer for a door ask: `DoorShare` and `DoorMemoX` only ever answer "unlocked" (hostcalls.cpp), so they cannot close a stair.
- **Not shown to be the cause.** The three RO clones: s265 stranded without them, and 1 of 13 (serving) against 0 of 12 (shadow or off) is no difference (Fisher p = 1.0). JobYield: 4 of about 73 runs with it against 0 of about 90 without is p = 0.04, but §7's fix did not end it and no step-aside is near the start.

### 8.2 What can close a stair for a plan [verified in the code, docs/92 §3]

1. A script lock. For a stair the only vanilla one is `PrivacySituation.OnPortalPathPlan`: a portal into the room **its Sim stands in at that moment** (`SyncTarget.RoomId`, read live, not the room the situation started in) is locked for every Sim with an autonomous interaction up to priority Privacy, or a pushed one below UserDirected, unless the situation lets it ignore privacy (`GenericPrivacySituation`: toddlers only). `Sim.OnPortalPathPlan` handles doors, poles, stalls, jumps, elevators, diving boards and ladders, not stairs. A lock like this fits the log (autonomous plans only, script asked in the failing jobs, lasts as long as the situation's Sim stays in the room below) but nothing in the log names a situation.
2. Native state: the portal's cost, the Sim's age / species mask, the route's forbidden list (all per plan, none set for stairs by household code), the endpoint cell test of `PortalEdgeCost 0x634500` (something standing on a lane end, or a footprint left there), a missing portal, the room graph.

### 8.3 The lines

- **ts3native, every `routefail job:` text** (also the tail of ts3fixes' `routefail:` lines): `door asks N live L locked K discouraged D native I[, first locked portal <owner id> rooms <from>><to>[ native]]`, before the scope word. Counted in the door stub per job (`JobDoor`, worker only): asks answered by the stub, of those the live script calls, answers "locked" and "discouraged", asks that came in already locked by native rules. Not seen: asks of a synchronous plan, and asks with every door switch off (the stub passes them through).
- **ts3fixes (`StrandLog.cs`, part of `routefaillog`)**, for a failed plan (`NoPathToDestination` / `NoDestinationSpecified`) of an active-household Sim to another level of the lot it stands on; per Sim the first 3, then every 25th:
  - `routefail strand:` cached level / room against the live lot location of its position, the goal's, current and transition interaction with autonomous flag and priority, the rooms connected to its room (`World.GetConnectedRooms`);
  - `routefail strand privacy:` each privacy situation subscribed to the lot's portal event: type, its Sim, where that Sim is (cached and live room), what it is doing, and `portals into room R LOCKED|open for this plan` by EA's rule;
  - `routefail strand link:` each object with a portal component whose lane ends lie on two levels of the way: class, both ends (position, level, room), per lane EA's slot test at both ends without and with Sims, and what stands within 0.75 m of each end. Then always `routefail strand links:` N on the way, the portal objects on the lot, and every level link on the lot by class and levels.
  - `routefail strand rooms:` (a Sim's first line) the chain of rooms from its room to the goal's and back, walked over `World.GetConnectedRooms`, and `World.ArePointsConnectedByLand` for the two points.

**Changed 2026-11-21 (branch `fix/strand2-1121`, built, NOT RUN).** The first version listed `Stairs` objects only. s372 showed that is not enough: a household plan from level 1 (room 9) to level 2 (room 11) of Colonial Casa got `no stair between level 1 and 2 (1 on the lot)`, the one `Stairs` being level 0 room 2 to level 1 room 9, while the room graph lists 11 next to 9. So the link the third-floor strands lose is not a `Stairs`. The classes that add a level-to-level portal [verified, every `PortalComponent` subclass and `Route.Add*Portal*` call in the decompile]: `Stairs` (`AddStairPortals`), `StaircaseSpiral` (one `Route.AddPortal` from `RoutingSlot_0` to `RoutingSlot_1`, AnimateThrough, human age mask, cost 2, plus pet variations; removed and re-added by `ValidatePortals` on reset and on leaving build mode), `Ladder`, `FirefighterPole`, `ElevatorDoors` / `ElevatorInterior`, `TubeElevator`. Which of them stands on this lot is **not known yet**: the new lines find links by component, not by class, and name it. EA has no script call for "is there a path between two rooms"; `World.GetConnectedRooms` (neighbours) is the only room-graph call, so the line walks it.

### 8.4 Reading the next strand

| The lines say | Then |
|---|---|
| `locked` > 0 with `rooms 11>R`, and a privacy line `portals into room R LOCKED` | EA's privacy lock; the line names the situation's Sim and what it is doing for hours. Next: why that situation does not end (its Sim's interaction) |
| `locked` > 0, no privacy line locks | another `Lot.PortalPathPlan` handler (a mod) or the rule read wrongly: the first locked portal's owner id names the object |
| `native` > 0 for the stair's owner id | cost / mask / forbidden list: read the route's options |
| `door asks` > 0, `locked 0`, a link's lane end not `free` | the endpoint test: the `near` list names the object, or nothing stands there (a footprint left behind: then `[Discourage]` deferral is the suspect) |
| `door asks 0`, `host 0` | the search never costed a portal: room graph or missing portal; `strand rooms` says whether the graph still has the chain, `strand links` whether the object is still there |
| cached and live level / room differ | the Sim's room was not updated after its last move |

Checks: `tools/selftest/strandlog` (which plans, how often, the privacy rule, the stair filter), `asklent-test.h` (the stub's counts over live, memo and locked answers), `plandeadline-test.h` (the text, the scope word still last for `unseen-scorecard.ts`).

## 9. The strand's answer: Sims standing on the ladder's ends (`ladderfootshoo`, 2026-11-21, built, default off, forced case run in game)

§7 (household stuck upstairs in JobYield storms, PR #632) and §8 (the `routefail strand:` log lines that named this cause) live on the
contributor's combo branch; §8's logging is not part of this repo. This section stands on its own.

### 9.1 What the strand log lines showed [verified, log lines of the strand2 storms, 2 of 12 runs]

Colonial Casa's third floor (level 2, room 11, y 61) is reached only by a `Ladder`: end A 927.3 / 61.0 / 1205.2 (level 2),
end B 927.6 / 58.0 / 1204.5 (level 1, room 9).

- `routefail strand:` Omar, `Replan FailedBecauseNoPathToDestination`, at 927.6 / 58.0 / 1204.8 (level 1, room 9), goal
  930.4 / 61.0 / 1201.5 (level 2, room 11), doing `SocialInteractionB`.
- `routefail strand link: Ladder`, `lanes 2 [0: A free, B free (with Sims: sim)] [1: A free, B free (with Sims: sim)]`,
  `near A: nothing`, `near B:` two household Sims 0.2-0.3 m from the end.
- `routefail strand rooms:` `9>11`, back `11>9`, points connected by land: yes. No privacy line locks.

So the room graph has the link, no script lock, and both lanes' lower ends are free of objects and taken by
Sims. The earlier strands (s229-s365: up to four Sims on level 2 with no path to anything below, for hours) have the
same shape from the other end; they were logged before the link line existed, so for them this cause is **inferred**.

### 9.2 Why a Sim on a lane end closes the ladder [verified: native decompile of `0x634500` / `0x6338A0`, decompiled script]

- **The search.** `PortalEdgeCost 0x634500` costs the traverse edge of a portal: one-way test, lock test, then
  `LotPlan_PortalPointCheck 0x6338A0` on the entry end and (unless portal flag bit 1 is set) on the exit end. The point
  check asks the routing cell at the point for its class (`QuadCell_Class 0x66B600`, context flags `0x10000`) and
  passes only class 0, 1 or 13. When either fails, the edge returns `1e6`: the same value as a locked portal, a wall.
  A standing Sim's footprint is in the lot's routing cells (docs/92 §7), so a Sim that stops on a lane end closes that
  lane in both directions for every plan. No script is asked and nothing is reported: with every lane closed the plan
  ends `FailedBecauseNoPathToDestination`, blocker none.
- **The ladder's script side.** `Ladder.LadderPortalComponent.IsLaneUnobstructed` passes `checkForSims: false` whatever
  the caller asked, and the class has no `DoAdditionalLaneChecks`. `Ladder.PortalClimb.PositionSim` puts every climber
  exactly on its lane's end slot, the two lanes lie close together (the two Sims of §9.1 stood 0.3 m apart and took both; inferred from positions), and the ladder never asks anyone to move on.
  Two Sims that climb and then stand, talk or play where they arrived close it.
- **What EA does elsewhere.** `Door.DoorPortalComponent`, `NonAnimatedDoor` and `StaircaseSpiral` override
  `DoAdditionalLaneChecks`: when the lane's slots are taken by Sims (`IsRoutingSlotObstructed(... checkForSims: true)`,
  for doors exactly `BlockedBySim`), every Sim within 0.5 m of either slot that is not routing gets
  `RoutingComponent.PushSimsStatic(s, list, PushSimsAwayDistanceMin, PushSimsAwayDistanceMax, allowPushOutOfConversation:
  true, waitForPushes)` (doors wait, the spiral stair does not). `Stairs` pushes only when its cover closes
  (`PushSimsFromStairCover`). All of these run when a Sim **arrives at** the portal, after a plan that succeeded. A plan
  that fails on the endpoint test never gets there, and for a ladder nothing would push even then.
- **`PushSimsStatic` [verified].** Leaves out Sims the game calls unpushable (`IsSimPushable`: below toddler, push
  immunity, already in a push, pets at a run), queues `SimRoutingComponent.BePushed` (a walk of 0.6-2.5 m inside the
  Sim's own room, `PlanRouteForPush`) for a Sim with no running or transition interaction, and for a busy Sim only with
  `bForceAllowPushOutOfInteraction` (EA: fire, police), then as `AllowForHigherPriority`: `AddNextIfPossible`, and
  `InteractionQueue.RemoveLowerPriorityInteractions` cancels what the Sim does only when the push outranks it
  (strictly); at equal priority the walk waits behind the running interaction.

So the hypothesis holds, with one addition: the "no path" is the search's own endpoint test, not a script answer, and
the reason nobody is shooed is that EA's shoo sits at lane-lock time and the ladder class lacks even that.

### 9.3 Fix (`LadderFoot.cs`, rules in `LadderFootCore.cs`, shared scan in `LevelLink.cs`)

After each of the four plan hooks (`RouteBackoff.cs`), for a plan of a Sim that failed with
`FailedBecauseNoPathToDestination` (or `FailedBecauseDestinationPortalLocked`, §9.6) to another level of the lot it stands on, at most once per Sim per
`kLadderFootCooldownMinutes` (2):

1. **Detect.** The lot's level links on the way (`LevelLink.cs`, found by portal component; lane ends on two
   levels) get EA's slot test per lane end, without and with Sims. A lane is *taken by Sims* when both ends are free
   without Sims and an end answers `BlockedBySim` with them; a link is *closed by Sims* when no lane is free and at
   least one is taken by Sims. It counts only when no other link between the same two levels has a free lane.
2. **Ask.** The Sims within 0.5 m of the taken ends that are not routing and are not the planner (EA's door rule) get
   EA's push: `PushSimsStatic(planner, list, PushSimsAwayDistanceMin, PushSimsAwayDistanceMax, true, false,
   planner.InheritedPriority(), true)`. An idle or chatting Sim walks off at once; a busy one is interrupted only if
   the plan outranks what it does (a player's order against an autonomous pillow fight), else it walks off when it is
   done. EA's refusals hold. A Sim that already has the push queued is not asked again.
3. **Wait and plan once more.** In the planning Sim's own task (and only where a sleep is allowed, no unplaced jig), the
   hook polls every 10 ticks, as EA's own wait for pushed Sims does, until none of the asked Sims has the push queued or
   running, at most `kLadderFootWaitMinutes` (5, never above EA's `kTotalSimMinutesToWaitForSimsToBePushed`), then runs
   the same plan hook once more and returns its result. Timeout, a cancelled planner, nobody askable or a second
   failure return the first failure unchanged: EA's route-fail as before. A plan made by another task (stuck check
   probes, routability tests) asks and does not wait.

No teleport, no position write, no portal state touched, no interaction cancelled outside EA's priority rule.

**Scope.** `ladderfootshoo`: Sims the player does not control, as planner and as the Sim asked. `ladderfootshoohh` in
addition: household and selected Sims in either role (`Fixes.HouseholdExempt`). With only `ladderfootshoo` on, a
household planner is counted exempt and household Sims on the ends are left standing. The strands seen so far are all
household Sims, so the storm arm needs both.

**Off = compare mode** for active-household plans while `routefaillog` is on: counted, nothing asked.

**Line** (120 a session): `ladderfoot: <Sim>, <Plan kind> <no path | portal locked> from level N [to level M], <interaction>, closed by
Sims: <Type> 0x<id> level a>b lanes <Sims|blocked ...>, standing there: <Sim>@<pos> (<interaction>) ...; <fix off: would
ask N | exempt (household) | asked N (M stepping away, K refused), waited X min: retry ok | gave up (timeout | canceled
| second plan <result>) | not waited for>`.

**Counters 4352-4363** (`docs/registry.md`): scans / plans whose needed link is closed by Sims / would ask (off) /
exempt planner / Sims asked / Sims not asked / waits / **retry ok** / **gave up** / asked but not waited for / a later
plan of such a Sim succeeded within 60 sim-minutes / errors.

### 9.4 Limits [inferred unless marked]

- The trigger is EA's slot test with Sims counted. A footprint that reaches the endpoint's routing cell from more than
  the slot test's reach would close the lane without the test saying so; the two logged cases did say `sim`.
- `BePushed` walks 0.6-2.5 m within the Sim's room. A Sim that lands on the other lane's end keeps the ladder closed:
  the second plan fails, counted `gave up`, and the next failure (after the cooldown) asks again.
- Equal priority (autonomous planner, autonomous long interaction on the end): the wait can run out before the Sim is
  done; its walk stays queued, so a later plan passes (counter 4362 measures this against 4360).
- A Sim standing on an end because it is itself waiting for the ladder (`PortalClimb` line) has an interaction; it is
  asked by the same priority rule.
- Stairs are covered by the same rule (their lanes are wide apart, so all lanes taken by standing Sims is rare).

### 9.5 Checks (2026-11-21, no game) and run plan

- `node tools/selftest/ladderfoot/run.ts`: which plans, lanes and links from the slot test (every 3-lane combination),
  who is asked, the decision by switch and scope, a wait model (500 random waits: never past the cap, second plan
  exactly when the Sims left in time), cooldown.
- `LadderFootCheck run | result | lot` (repro `ladderfootshoo-check`): off `BROKEN`, on `FIXED`, with controls; `lot`
  lists the selected Sim's lot's level links with their lanes (read-only). The forced case is §9.6.
- Storms: repro `ip-jobyield-strand-storm` (PR #632), `routefaillog` on. Off arm: `ladderfoot:` lines `fix off: would ask
  N`; counter 4354 > 0 in a run that strands. On arm
  (`MF_EXTRA="... ladderfootshoo ladderfootshoohh"`): lines end `retry ok`, or `gave up` followed by 4362 rising; no
  household Sim failing from y 61 for longer than the wait cap plus one cooldown; 4363 = 0; no new script errors, no
  Sim pushed back and forth (the same Sim in 3 or more `standing there` lists within a few minutes would be a loop to
  look at). About 2 strands in 12 off runs, so "not seen" needs some 18 clean on runs for p < 0.05; a line with
  `retry ok` on a run that would have stranded is the direct proof.

### 9.6 Forced repro and first live run (2026-11-21, contributor's rig)

The forced repros (`ladderfoot-force`, `ladderfoot-force-down`) and the agent's `go_here` `exact` option they use stay on
the contributor's combo branch: they need its stand-and-wait switches and a storm load that are not in this repo.

Setup (harness, not gameplay): queues cancelled and autonomy off, Omar stood on the ladder's lower end (one Sim there
closes both lanes: `LadderFootCheck lot` says `[Sims Sims]`), Cristobel 0.7 m from it, Rosemarie on the ground floor with
Go Here to room 11 (with the agent's routable-point swap off: it would move a goal behind the closed ladder).

What the game does there [verified, Deck runs]:

- The first plan, from the ground floor, **succeeds**: a standing Sim on the end does not fail a plan made without
  "blocked by people". The Sim walks to the ladder, EA's stand-and-wait starts, and its replan (with "blocked by people")
  answers **`FailedBecauseDestinationPortalLocked`**, not "no path". The storms have both answers at this ladder (s422,
  s424: `Replan ... DestinationPortalLocked` from level 1 to a level 2 goal and from level 2 down, next to the "no path"
  lines of §9.1).
- **Bug found in the fix:** it looked only at "no path", so in the forced case it never scanned (`scans 0`, fix on).
  Now both answers start the scan (`LfsKind`); the line says `portal locked from level N` for the second. A door that
  is really locked gives no case: the scan still wants a level link closed by Sims.
- An idle Sim on the end is not a strand by itself: in the off arms the walker crossed too (the blocker was pushed by
  the combo branch's stand-and-wait fixes or had been pushed off the end by its neighbour before the walker arrived; no failed plan at
  all in 2 of 3 off arms). The long waits seen by hand (13 blocked cycles, 20 sim-min, route failed; twice) had a blocker
  left in `SocializingPosture` by a cancelled pillow fight; that state is not forced yet (a pillow fight started and
  cancelled by steps walks on after the cancel and leaves the end).

Run lf-r2 (one load, s420 keys and switches, package `ladderfoot3`; arms switch `ladderfootshoo` + `ladderfootshoohh`;
`ea*` = the combo branch's stand-and-wait fixes off as well; crossing read every 5 s):

| arm | failed plans | `ladderfoot:` line | crossed |
|---|---|---|---|
| off1 | Replan DestinationPortalLocked x1 | fix off: would ask 1 | within 25 s |
| on1 | Replan DestinationPortalLocked x1 | asked 1 (1 stepping away, 0 refused), waited 3.0 min: retry ok | within 25 s |
| off2 | none | none | within 20 s |
| on2 | Replan DestinationPortalLocked x1 | asked 1 (1 stepping away, 0 refused), waited 2.4 min: retry ok | within 25 s |
| eaoff | none | none | within 20 s |
| eaon | Replan DestinationPortalLocked x1 | asked 1 (1 stepping away, 0 refused), waited 2.3 min: retry ok | within 25 s |

Run lf-r3 (same, second load): off1 and off2 one failed replan each, `fix off: would ask 1`, crossed within 25 s; on2
`asked 1 (1 stepping away, 0 refused), waited 2.6 min: retry ok`, crossed within 25 s; on1, eaoff, eaon no failed plan
(Omar already off the end when the walker arrived). Counters after lf-r2 / lf-r3: scans 4 / 4, closed by Sims 4 / 3,
asked 3 / 1, waits 3 / 1, retry ok 3 / 1, gave up 0, would ask 1 / 2, errors 0.

`ladderfoot-force-down` (lf-r4) is not a repro yet: one Sim on the upper end takes one lane (`[Sims free]`), the walker
climbs down the other; no failed plan in either arm.

So the fix's own path is proven live four times (detect, EA's push accepted by a household Sim, the wait ends when the
push is done, the second plan succeeds, the Sim climbs), errors 0. Not shown: a gain over the off arm for an idle
blocker (there is none to show), the "no path" entry, a busy blocker (equal priority, §9.4).

### 9.7 Import 2026-11-22 (maintainer)

PR #637 (alexvixell35-stack). Counters 4352-4363 and `kCounterSlots` 4364 kept: nothing on master uses 4200-4363. `ladderfootshoo` is **ON by default** at import (forced case in game: 4 fix-on arms `retry ok`, errors 0; his off arms crossed too, so no gain over off is shown, only the path working). `ladderfootshoohh` stays **off**: no run is documented with household Sims in both roles, and the household rule keeps household and selected Sims untouched until it is proven. The pure check repro `ladderfootshoo-check` (off BROKEN / on FIXED) is in `tools/harness/repros.json`; the forced repros stayed on his combo branch. Not a gain claim: a clear strand reduction is still unmeasured.
