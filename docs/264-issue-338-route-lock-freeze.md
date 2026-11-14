# 264. Issue #338: uncut route jobs freeze the sim thread on the route lock

2026-11-14. Design only: static analysis and existing logs. No build, no game run. Issue #338 (opened 2026-11-10, "RouteAsync: backoff-uncut foreign jobs plan 14-38 s and freeze the sim thread on the route lock"). Sources: `native/ts3native/plandeadline.cpp`, `ts3native.cpp`, `docs/197` §4 and §7, `docs/134` §10-11, `docs/142`, `docs/164` §7, `docs/250` §2 and §7.1, `docs/data/results/2026-11-13-kd-unseen-foreign-cap.md` (PR #378), and the 165 `probe-out/queue/*2026-11-1[1-4]*-ts3native.log` files with their `-native.json` counters.

The native logs of the five runs in the issue (`ip-f1-mintcall-ab` 11-10, `ip-jit-ab-long` 11-06, `ip-save-reload`, `ip-e12-plain-wa1-play0`, `ip-compaction-optin` 11-02) are no longer in `probe-out/`. For those runs, only the lines quoted in the issue are available.

## 1. Result

- **What "uncut" means.** `KdJobBegin` (`plandeadline.cpp:268`) returns 0 on four branches [verified, code]:
  1. backoff level 2: `t_kdUncut` is set at `:312`;
  2. an own job with any `j->sc` tag: `kdExemptSc`, `:287`;
  3. a kind whose deadline is set to 0 ms: `:291`;
  4. a job that is not exempt and not cut, before `KdExempt`. This branch is not reached in release.

  The worker then sets `t_fbCapUs = kdUs >= 0 ? kdUs : ...` (`ts3native.cpp:4089`). A return of 0 therefore also turns off `FbJobCapMs` (300 ms). The job gets no world A* deadline and no cell or water deadline. Only an `UnseenFallback`-MARKed job (`j->uf`) still gets `ufUs` (`:4094`). The `KdSeenCeilMs` / `KdSeenNpcCeilMs` ceiling applies only on the `KdExempt` branches (`:282`, `:295`).
- **Why the sim thread freezes.** The worker holds `+0x918` (RoutingWorld lock, `Lock_Release 0x4E17B0`) for the whole job (docs/134 §10, docs/250 §2). When the sim thread next needs that lock, `LockAcquireAsync` (`ts3native.cpp:5284`) loops until the job ends. That happens for steering, destroy, and `GoalPoint_Validate 0x759B20`, which waits at `0x75A64C` on the steering query `0x82EDB0`. The loop only serves the job's host calls (`:5315-5324`). The game's own deadline is infinite. The `busy for N s` line comes from `:5334`. A failing world A* (`0x676260`) floods the start's whole reachable area before it answers "no path" (docs/142). On IP and SV that takes seconds.
- **Cause of the runs in the issue (11-02 to 11-10)** [verified code, inferred per run because the logs are gone]: the long jobs were branch 1. A foreign caller kept retrying an unreachable point. After two cuts within 10 s, the follower's row reaches level 2. Every later job then runs uncut, and an uncut failure does not reset the row (`:351`, `:357`; docs/197 §7). Two scope changes matter:
  - Before 0.36.237 (09 Nov), jobs with unknown and household scope were also cut. They could reach level 2, which covers the 11-02 and 11-06 runs.
  - From 0.36.237 on, only scope unseen or `uf` can reach level 2, because `KdExempt` returns before the backoff lookup.

  Dalexlol's 118 logs from 11-11 and 11-12 show 85 foreign jobs ≥ 1 s with `capAt -1`, all scope unseen, longest 21.4 s [measured, PR #378].
- **Already fixed for foreign kinds:** `KdUnseenForeignCap=1` (0.36.253, ini ON since 11-13) treats level 2 as level 1 for `KD_FGN` / `KD_A10F` of unseen followers. My scan of the 0.36.253+ logs in `probe-out/queue` (11-13 and 11-14) finds 0 uncut foreign jobs ≥ 1 s [measured].
- **Still open (this doc).**
  - **(a) The watchdog idle check runs uncut** [verified code, measured]. Since 0.36.223 / 0.36.239, the `watchdogasync` route check sets `j->sc` bit `0x400` (`ts3native.cpp:4903`, also for IC kind 2). `KdJobBegin` tests only `j->sc` (`:286`), so it treats that check as a stuck check's last round: uncut, no ceiling. The `ScProbeMark` comment at `ts3native.cpp:5080` says the tag is "a label only … no kd mark". The code does not do that.
    - `wdJobMaxUs` in the 11-11 to 11-14 counters: 9,785 ms in SV (`jit-bundle-verify-1113 sv-jb-verify-1854`, 0.36.256), 1,677 ms in IP, 1,439 ms in BP, 1,033 ms in `freeze-stress-1112`. `wdJobLong` (≥ 200 ms) is 39-87 per run.
    - The ≥ 1 s lines all read `own START, IC kind 2, … watchdog check, scope unseen`: 9 of them, including 9,784 ms and 9,229 ms in the same SV run.
    - The watchdog only checks NPCs that are neither household nor selectable (docs/250 §7.1 [verified there]). This is a pure NPC freeze. The 11-10 run in the issue used 0.36.239, the first version that tags kind 2. Its 18 s wait fits this path or branch 1 [inferred].
  - **(b) Own kinds at backoff level 2 of unseen followers run uncut.** These are the route the Sim must walk; #378 left them out on purpose. In 0.36.253+ logs there are 2 such jobs ≥ 1 s (`no stuck-check mark`, 1.1 and 1.3 s, IP) [measured]. They are small now, with no ceiling.
  - **(c) The real stuck-check last round has no ceiling.** For household Sims too, because `kdExemptSc` comes before `KdExempt`. Vanilla plans it uncut too.
  - **(d) A cut is not a hard end.** Work after the cut still holds `+0x918`:
    - `sv-jb-verify-1854`: a UF-capped foreign job hit its cap at 1,088 ms and then ran another 4,420 ms (`tail 4420 ms`, `plan 4421 ms`, a plan that started after the cap).
    - A seen-NPC job ran 1,123 ms past its 2 s ceiling.
    - docs/164 §7: a cell A* search already running runs 1-2.3 s past a cut.

    Which plan ignores the expired deadline is **not proven**; logging comes first (§4).
- **Which Sims.** Every measured long uncut job is an NPC (scope unseen). Household and unknown scope jobs are exempt with the 10 s `KdSeenCeilMs` ceiling. In the 11-11 to 11-14 logs they peak at 2.2 s, under the ceiling. Seen NPCs have the 2 s `KdSeenNpcCeilMs` ceiling.

## 1.1 What JobYield (PR #397, ON since 11-13) does and does not cover

Rodney's question: doesn't JobYield already keep routing from stopping the main thread?

**What it covers** [verified, code]:
- One kind of waiter: a script route plan that ts3fixes is about to run synchronously because START was refused or the READY poll hit its cap (`RouteBackoff.cs:133`, `:261`, `:424`).
- In that case `StepAside` (`RouteBackoff.cs:434`) sleeps the task one tick at a time while ts3native answers JOBBUSY (option2 `0x85…`, counters `jyAsks` / `jyBusy`, `ts3native.cpp:1897`). It gives up after 10 s (`kStepAsideMaxMs`).
- It only does this when `MayYield()` is true (`:499`): `Simulator.CheckYieldingContext` and not `kNoYield`. That means the task must be at its root state, not inside a nested native-to-managed call.

**What it does not cover** [verified, code]:
- **The sim thread's native acquires of `+0x918` in `LockAcquireAsync` (`ts3native.cpp:5284`).** These are steering `RouteFollow_EditPath 0x85F5A0` / `0x82EDB0` / `0x82F0B0`, `GoalPoint_Validate 0x759B20` at `0x75A64C`, destroy and LOS. They are C++ on the sim thread with no task to put to sleep. These are the waits in the issue's `busy for N s` lines.
- **NoYield contexts:** plan callbacks (docs/185), and `Route.Replan` calls from `FollowRouteAction.PerformRoute` that ran synchronously (docs/261).
- **Game plans that do not go through ts3fixes' RouteBackoff hooks.**

So JobYield shortens the script-plan waits (`swN` sites runplan / rtwait). It cannot shorten a lock wait.

**What the 11-13 and 11-14 runs show** [measured: counters and targeted logs, no new greps over big files]:
- **The SV run with the 9.8 s and 9.2 s uncut watchdog jobs did not freeze on the lock** (`jit-bundle-verify-1113 sv-jb-verify-1854`, 0.36.256, `jyMode` 1):
  - `jyAsks` and `jyBusy` are both 0, so JobYield never acted. This was not JobYield's doing.
  - No `busy for` line in the log.
  - `asyncPumpUs` (all `LockAcquireAsync` waits, whole run) is 2.1 s in total.
  - `swMaxUs` is 360 ms.
  - The hitch frames during both jobs (`t` 256,778,000-256,800,500, the same clock as the long-job `at`) are 100-830 ms, almost all in the `threaded` stage, with `rtLock` 0 and `rtJob` 0.

  The sim thread simply did not need `+0x918` for long while those jobs ran. The read-only answers (`[GoalValidRO]`, `[AvoidRO]`, `[SteerRO]` / `steerReady`, docs/137, docs/250) cover most of its lock queries during a job [inferred]. This was luck of the workload, not a guarantee.
- **All JobYield-on runs on 11-13** (counter files `*-<hhmm>-native.json` with `jyMode` 1):
  - `swMaxUs` is at most 463 ms.
  - No `busy for N s` line (which means ≥ 2 s) in any 0.36.253+ log.
- **Last lock freeze seen:** 0.36.248 with JobYield off (`master1112b-verify ip-m1112b-gvunseen-loop-0633`, 11-13):
  - waits of 2 s and 6 s;
  - `asyncPumpUs` 100 s in total;
  - one of the long jobs was an A10 foreign job of a household-or-seen Sim at 6.2 s, exempt with the 10 s ceiling.

  Fix A does not change that case; household keeps 10 s.

**Remaining risk, sized:**
- **Not observed.** No sim-thread lock freeze ≥ 2 s has been seen since 0.36.253.
- **Possible.**
  - NPC jobs with no deadline still run up to 9.8 s (watchdog check). Any sim-thread acquire that the read-only paths do not answer during such a job freezes the game for the rest of it, and JobYield cannot help.
  - Household / seen jobs up to the 10 s ceiling can do the same, at the vanilla cost.
- **So fix A is a cheap worst-case bound for the non-yieldable waiters, not an urgent fix.** Do this first:
  1. Ship the §4 logging. Item 2, the running job's fields in the `busy` line, decides which job froze the game the next time it happens.
  2. Build A only if such a line names an NPC uncut job, or bundle it with the next ts3native build because it is ~20 lines.

## 2. Fix options, ranked

Common rule (AGENTS.md household rule): a household, selected or unknown-scope job is never cut below `KdSeenCeilMs` (10 s), which is the existing long hard ceiling for crash and freeze guards. Waiting on `+0x918` behind a household plan costs the same as vanilla, which plans that route synchronously on the sim thread. RouteAsync adds no time to it, so household results stay as they are. Only the NPC branches below change.

| # | option | what | household | NPC result | effort |
|---|---|---|---|---|---|
| **A (recommended)** | **Ceiling on every uncut return** | In `KdJobBegin`, replace the three `return 0` paths with `return KdUncutCeilUs(scope)`. Scope unseen (2) or seen NPC (3) gets `kdNpcCeilUs` (2 s). Household, unknown and seen get `kdCeilUs` (10 s). The watchdog tag (`sc & 0x400`) leaves the `kdExemptSc` branch: it gets its scope's uncut ceiling, never the 250 ms kind deadline, because a cut check reads "unroutable" and could make the watchdog act on an idle NPC [inferred]. Runtime switch `kduncutceil` (ini `[RouteAsync] KdUncutCeil=1`) for the A/B. Same build: issue items 2 and 3 (§4). | stuck-check last round: uncut → 10 s ceiling. Only a plan > 10 s changes, which is within the accepted crash-guard rule. Say so in the PR. Everything else is unchanged. | A plan the NPC would have finished after 2 s now fails instead. The caller retries or the NPC gives up. `kdUncutOk` counts how often. | 1 agent build (~45 min including selftest `TestPlanDeadline` steps); runs: smoke + repro IP + repro BP (3 × ~15 min) |
| B | Find and end the post-cut tail | Logging first (§4.3). Then make a nested plan that starts after `t_jobCapAt` exit at once: the cut is already decided. | not affected (household jobs get the cut only at 10 s) | the 1-4 s tail after a cut goes away | 1 logging build plus 1 IP and 1 SV run, then 1 fix build plus repro runs: ~2-3 h total |
| C | Cut when the sim thread waits | `LockAcquireAsync` stamps a "sim waits since" time. `BudgetStep` and `PdWorkerSkip` cut a job of scope 2 or 3 once the sim has waited longer than `KdSimWaitCutMs` (e.g. 500 ms). This catches every branch, including ones nobody has found yet. | household jobs never cut (scope check) | NPC plans cut only while the sim thread is actually frozen | 2 agent builds (worker and sim thread share a new field, plus the camera recheck path); 3-4 runs |
| D | Release `+0x918` during long plans | Rejected. The lock guards footprint refcounts, the quadtree and lot transforms, and the A* holds references into them (docs/250 §2, docs/177 §7). Lending it mid-search is the race class TailUnlock and DoorMemo exist to avoid. | - | - | days, crash risk |
| E | Refuse foreign kinds while the sim waits | Covered by #378 for foreign kinds. It does not reach (a) or (b), which are own jobs. | - | - | - |

Order (revised after §1.1): **§4 logging first**. Then **A**, as a low-priority guard bundled into the next ts3native build. It closes (a), (b) and (c) with a bounded ceiling. (a) is the measured 9.8 s job, but that job froze nothing in its run. **B's logging rides in A's build.** C only if A plus B still leave waits ≥ 2 s in the release runs.

## 3. Repro: `kd-uncut-ceiling` (forcing, harness)

The goal is an off arm that freezes the sim thread for ≥ 1 s every time, using the branch 1 path (own kind of an unseen follower, which #378 does not cover):

1. `native_set kinddeadlines 1, kdexemptseen 1, kduncutceil $on` (setup).
2. `native_set kdownplanus 1000` (setup, **test-lowered**: 1 ms for own Plan, so the first two plans are always cut). This needs `kdownplanus` added to `tools/harness/native-switches.ts` `nativeValues` at `kdUs[0]`, `+0x5224`. No native change.
3. `pick_sims count 1 townies noPets`. `camera_to_tagged` on a household Sim at the far side of the world, so the townie answers scope unseen (`UfWhy`).
4. `sim_route_to picked:1 to: "sealed"` three times, 1 s apart, all within the 10 s window. `"sealed"` is a **new agent target**: the enclosed tile that `sim_strand` already finds (`bp-idle-unroutable`). A goal nobody can reach makes the world A* flood the start's whole area (docs/142). Plans 1 and 2 are cut at 1 ms and 2 ms. Plan 3 is at backoff level 2.
5. Same step: `sim_route_to` the household Sim `to: "far"`, so steering takes `+0x918` every tick during plan 3.
6. `native_check`: `kdBackoff_1>=1`. Arms `off,on,on,off`, `perArm 5`, `maxMinutes 10`.

Pass checks:
- **Off arm:** a hitch record ≥ 1,000 ms with `rtLock`, and a `long job ≥ 1000 ms … uncut: backoff` line. If plan 3 is under 1 s, the result is N/A: the world is too small, so use IP.
- **On arm, `kdnpcceilus` test-lowered to 300,000:** no hitch ≥ 1 s from that job, a `KD UNCUT CEILING CUT` line, and `kdUncutCut >= 1`.
- **On arm, default 2 s:** max sim wait ≤ 2.5 s. This is checked in the natural job.

Queue `kd-uncut-ceiling.json`:
- `ip-kd-uncut-ceiling` (forced repro, IP, `noiseCheck false`).
- `bp-kd-uncut-ceiling` (same on BP, where the flood is smaller and the job may be N/A).
- `sv-kd-uncut-natural`: SV 10 min natural, `--ab kduncutceil`, seed 12345, the `jit-bundle-verify` setup that produced the 9.8 s watchdog job.
  - Pass: `wdJobMaxUs <= 2.5 s` in on slices.
  - Off slices are expected to show the uncut watchdog lines.
  - `kd-arm-cuts.ts --expect-on-zero` stays green: no household cut below 10 s.

Smoke first (queue.ts does this automatically). Household check: in the same IP job, a `kd-exempt-seen-gohere` step must show no new household cut.

## 4. Logging (first, in A's build)

1. **Uncut reason** (issue item 2): the long-job lines (`JobLongNote`, `ts3native.cpp:3946`/`:3950`) get `uncut: backoff | sc-last | watchdog | kind-0 | kd-off`, plus the deadline actually used (`dl N ms`). New native int32s `kdUncut[4]` by reason and `kdUncutCut`; reserve them with `node tools/registry.ts reserve native 5`.
2. **The running job in the `busy for N s` line** (`:5334`): append its `who` (own / foreign / A10), IC kind, scope, `sc` tag text and uncut reason. These fields come from `AsyncJob` and are logged when the line prints, so a freeze names its branch without the long-job line (which only appears after the job ends, and never if the queue kills the game).
3. **Post-cut tail** (option B): after `t_jobCapAt`, count nested plans that start (`CallPlanBudget0`) and whether their `Budget.dl` was already expired, plus the host time after the cap. Log one `route async: post-cut tail` line per job when the tail is ≥ 300 ms (20 a minute).
4. **Harness** (issue item 3): when an agent call times out, `profile-world.ts` prints the last `route async: lock … busy` line from the native log instead of "agent package without the command?".

## 5. Claims

| claim | status |
|---|---|
| return 0 = no deadline at all (FbJobCapMs off too), except `uf` + `ufMode` | verified, code `plandeadline.cpp:268-315`, `ts3native.cpp:4087-4095` |
| sim thread waits for the whole job, serving host calls only | verified, code `ts3native.cpp:5284-5340` |
| the watchdog tag (`sc & 0x400`) goes to `kdExemptSc`, so it runs uncut | verified, code `ts3native.cpp:4903`, `plandeadline.cpp:286`; measured `wdJobMaxUs` 9.8 s |
| the issue's 14-38 s jobs were backoff level 2 | inferred (counters in the issue; logs gone) |
| the tail after a cut comes from a nested plan ignoring an expired deadline | inferred; §4.3 decides |
| a cut watchdog check could make the watchdog act on an NPC | inferred; why A gives it the ceiling, not the kind deadline |

## 6. Fix A as built (Rodney 2026-11-14): one 30 s ceiling, all Sims

Rodney's decision replaces the 2 s / 10 s split in §2. There is now **one long hard ceiling for every route job that has no other deadline**, for all scopes, household included. It is a never-freeze-forever crash guard (CLAUDE.md: "crash guards keep a long hard ceiling"), not a cut rule. Branch `bugfix/338-uncut-ceiling`. Code and selftest only: **not built, not run**. Build and verify steps are in `docs/data/issue-338-handoff.md`.

**Setting** `[RouteAsync] KdUncutCeilMs=30000`:
- Read like the other Kd values (`ReadKindDeadlines`, `plandeadline.cpp`).
- 0 = off. Other values are clamped to 5,000-300,000 ms (`UcIniUs`).
- Runtime switch `kduncutceil` (`kdUncutMode` +0x62F4, `--ab` / `native_set`). Value `kduncutceilus` (`kdUncutCeilUs` +0x62F8, in us).
- Counters v198: `kdUncutHit` (+0x62FC) and `kdUncutMaxUs` (+0x6300). They sit after a 10-int32 placeholder for v196 / v197, which are reserved on MonoJit branches.

**What gets the ceiling.** In the worker (`ts3native.cpp`, after the UnseenFallback cap), `UcCapUs(t_fbCapUs)` gives the ceiling only when `t_fbCapUs` is 0, that is when no other rule set a deadline. That covers:
- KindDeadlines backoff level 2;
- the `kdExemptSc` branch, which includes the watchdog idle check tag `0x400` (§1 (a));
- a kind set to 0 ms;
- an own job with KindDeadlines off.

A job with any other deadline (kind deadline, `KdSeenCeilMs`, `KdSeenNpcCeilMs`, `FbJobCapMs`, `UnseenFallback`) keeps that deadline unchanged. `t_ucJob` marks the job. `g_pdWkTid` is armed for it too, so the cell A* and water exits answer as well as the world A* exit.

**How it ends.** Through the existing deadline exits (`BudgetStep` / `CallPlanBudget` for world A*, `PdWorkerSkip` for cell and water). The job's IC then runs the game's own failure handling, returns a failed `RoutePlanResult`, and `LockReleaseHook` releases `+0x918` as for any cut job [verified code path: the same exits and the same release as KindDeadlines cuts]. The §1 (d) post-cut tail can still add 1-4 s after the ceiling [measured on other cuts]. The ceiling is not an exact end.

**What the player sees** when it fires: the same as for any route that finds no path. The script caller gets an ordinary failed plan (docs/197 §1):
- The Sim's own route runs `SimRoutingComponent.DoRouteFailureBehavior`, which plays the route-fail thought balloon (`Sim.PlayRouteFailure`) unless the Sim is set to `NeverPlayRouteFail`, and then makes a few more short plans (`GetRouteFailPath`, `PlanToLot`) [call sites verified, counts inferred, docs/11; docs/04a].
- The `RouteTo*` call returns false. The interaction that needed the route then stops, or its own code tries another spot [inferred per interaction: each caller decides].
- A foreign caller (another task planning for this Sim or object) gets a failed plan and goes on to its next option.

**Log lines** (same wording in both):
- At start: `[RouteAsync] KdUncutCeilMs=30000: a route plan with no other deadline is cancelled after 30.0 s and treated as a failed route (route plan cancelled after 30.0 s (KdUncutCeilMs))`.
- With 0: `KdUncutCeilMs=0: no ceiling ...`.
- At each hit: `[RouteAsync] uncut ceiling hit job=<route> us=<job us> scope=<scope>, <own|foreign|A10 foreign> START, IC kind <n>, kd kind <n>: route plan cancelled after 30.0 s (KdUncutCeilMs)`.
- The long-job lines say `capped (KdUncutCeilMs)`.
- A ceiling hit is not counted in `kdCut*` / `fbjcCapped`, so the KindDeadlines statistics keep their meaning. The follower's backoff row still sees a failed, capped job.

**Household.** A household or unknown-scope job never reaches this today with `KdExemptSeen=1`, because it has the 10 s `KdSeenCeilMs`. It can be hit only on:
- the stuck-check last round, uncut until now;
- a 0 ms kind;
- KindDeadlines off.

Each of these needs a plan longer than 30 s, which is above the longest single job in the issue's logs (25.5 s, `ip-jit-ab-long`, a failed foreign plan). A household result can change only in that case, and the CLAUDE.md crash-guard rule accepts it.

**Where it is documented:** the `ts3native.ini` comment (player wording), `docs/PLAYERS.md` (ts3native list), `docs/registry.md` v198, `tools/harness/native-switches.ts`, `tools/probe/agent.js`. Selftest `TestUncutCeil` (`plandeadline-test.h`, called from `selftest.cpp`) checks:
- the ini range;
- that only a job with no deadline gets the ceiling;
- that the switch off or ini 0 means no ceiling;
- that the hit counter and max count only a capped job under the ceiling.

**§4 logging, implemented in the same branch** (2026-11-14, not built). All of it is log-only: no new counters and no behaviour change.

- **§4.1 Uncut reason.** Reasons are `UC_BACKOFF`, `UC_SCLAST`, `UC_WATCHDOG`, `UC_KIND0` and `UC_KDOFF`. `KdJobBegin` sets `t_kdWhy`; the worker turns it into `t_ucWhy` when the job has no deadline of its own. Long-job lines end in `, uncut: <why> (KdUncutCeilMs|no ceiling)`.
- **§4.2 Who froze the sim thread.** The worker publishes the running job (`g_runJob`, `g_runWhy`). The `busy for N s` line appends `; job: <who> START, IC kind, scope, tag, uncut: <why>`.
- **New line for 1-2 s waits.** Waits under 2 s used to print nothing. Now any wait of 1 s or more in `LockAcquireAsync` on `+0x918` prints `route async: sim waited N ms on lock … last job seen route …: …` (20 a minute). The job fields are snapshotted during the wait, because the job is gone by the time the wait ends.
- **§4.3 Post-cut tail.** `CallPlanBudget0` counts plans that start after `t_jobCapAt` (`t_pcPlans`, `t_pcCapped` = capped at once, `t_pcPlanUs`). `Proxy` counts host time after the cut (`t_pcHostUs`). A job ≥ 300 ms past its cut prints `route async: post-cut tail …` with that split and the rest (20 a minute).
- **§4.4 Harness.** `tools/harness/lock-wait-line.ts` (self-check on real match and non-match lines) is used by `profile-world.ts`: the "fixed_work failed twice" FAIL names the last route-lock wait line, if there is one.

The §3 repro (agent target `sealed`, `kdownplanus`) stays open, and so do B and C. The handoff's natural SV job is the first check.
