# 250. Goal validation without the `+0x918` wait (H4), and the `0x85FE10` fallback (H7)

2026-11-08. Design only: no code, no build, no game run. Sources: docs/247 §10, §11.7-11.9; docs/178 §7 and §9 (T6 `[GoalValidRO]`); docs/177; docs/155; docs/134; docs/195 §8 (`[IdleCheckRO]` C2, the template); `gvro.cpp`, `steerro.cpp`, `idlero.cpp`; ts3fixes `RouteFixes.cs` (`StuckWatchdog.CheckIdle`), `FewerPlans.cs`, `RouteBackoff.cs`. The data is the `hitchlog3` runs (`probe-out/queue/hitch-cause-2026-11-06-{bp,ip,sv}-hitch-cause-native.json`, hitch records in `probe-out/profile/hitch-cause-2026-11-06-*-600-hitches.jsonl`). Ledger §453.

REA did not answer during this work (two lookups timed out at 149 s while a game chain was running), so the caller list of `GoalPoint_Validate 0x759B20` comes from docs/105 §6 and the hitch records, not from a fresh xref. The build agent runs `procedure_callers 0x759B20`, `0x75AFA0` and `0x751FF0` first (§9 step 0).

## 1. Result

- **What waits.** `GoalPoint_Validate 0x759B20` runs on the sim thread inside the goal generation of the sim's own sync plans (`Route_PlanCommon` -> `Route_GenerateDestinationCandidates 0x751FF0` -> radial destination slot `+0x10` `0x75BE60` -> `0x75AFA0` -> `0x759B20`, docs/105 §6 [verified there]). Its steering query `0x82EDB0` (call `0x75A647`, key `0x75A64C`) takes `+0x918`. An async route job holds `+0x918` on the worker for the whole job, so the sim waits for the rest of the job.
- **Why `[GoalValidRO]` still waits.** It answers only empty results. In all three runs every empty answer was a point off any lot (`gvTaken == gvNoLot`, IP 1,951 of 2,084). Every on-lot point found footprints, and those calls go to the game (`gvFb_6`): 59-63% of the calls made while a job ran [measured]. Handing the hits back would need footprint refs, which are plain refcount writes that race the job (docs/177 §7).
- **The main constraint: moved waits.** The validator is one step inside a sync plan. The same plan takes `+0x918` again later: in the goal footprint gather `0x82F0B0` (key `0x75AA16`), at gate 10 `0x760BFF`, and in `Route_PlanLocked` (key `0x05000000`). An exact read-only answer at `0x75A64C` alone moves the wait to the plan's next take. It does not remove it (docs/152 conservation, docs/155 §1 row "tail goes to the gate-10 wait"). The hitch records agree: the hitches keyed `0x75A64C` have `rtPlanWait` of only 0-106 ms in total per world, because the job ended during the first wait [measured]. **So the only ways to gain are: no sync plan while a job runs (Part A, the plan goes into a job), or no exact plan (Part B2, unseen Sims only).** Answering the hit loop read-only (B1) gains only for callers that take `+0x918` nowhere else.
- **Who waits** (hitches with `rtMaxKey` `0x75A64C`, by the hitch's longest task, §4): our own `StuckWatchdog.CheckIdle` 16-45%, Sims' route following and replans (`FollowRouteAction.PerformRoute`, `ObstacleEncountered…`, portals) 44-62%, interactions and state machine events 8-23%.
- **Recommendation: one feature in four switches, all default off, in this order** (§7):
  1. Part 0: attribution counters, so each part's share is measured and not inferred. ~30-45 min, rides on the next 10-min IP job.
  2. **Part A1: the watchdog's route check as a job** (ts3fixes, our task sleeps). Non-household Sims only (CheckIdle already skips them), same planner, so the verdict is exact. Expected **BP ~2.4, IP ~4.6, SV ~4.4 ms/s**, and the IP 458 ms hitch. ~45-60 min + one 10-min IP verify job. Low risk.
  3. **Part A2: own-task sync plans that are refused today go async** through the existing machinery (`M1Retry`, `asyncreplan`, `FallbackAsync` scoped). This is configuration plus Part 0 data. It covers part of the route following share (BP ~9.4, IP ~6.1, SV ~4.6 ms/s together with B2).
  4. **Part B2: unseen, non-household sync plans while a job holds `+0x918`** go to the existing unseen route fallback instead of waiting. ~1-1.5 h + a verify-shortcut run. Medium risk, unseen only.
  5. Part B1 (an exact read-only hit loop, IdleCheckRO C2 pattern) is built **only if** Part 0 shows that at least 25% of the `0x75A64C` wait has no later `+0x918` take in the same call. ~3-4 h, otherwise ~0 ms/s.
- **Household and selected Sims** stay exact: A1 never sees them, A2 is the same plan on the worker (an exact result, docs/134), B2 exempts them (unknown scope counts as household), and B1 is exact by construction (shadow-checked). Their sync plans that cannot yield keep today's wait. That is the residual, sized by Part 0.
- **H7** (`0x85FE10`, 1.2-2.9 ms/s, worst 201 ms) has the same lock and the same callee `0x82EDB0`, but not the same reason. `[SteerRO]` already answers hits read-only; its waits are the rare transient fallbacks (QL busy, tripwire, state: 25/27/51 against 22/20/48 waits). It shares only one helper with H4: **retry the clone for at most ~1 ms before calling the game** (`SrRetry`, §8). Expected 0.6-2 ms/s, ~30-45 min.

## 2. What `+0x918` guards, who holds it, for how long

- `+0x918` is the routing world's lock (`RoutingWorld + 0x918`, released through `Lock_Release 0x4E17B0`, docs/81, docs/177 §7). It guards the lot table, the quadtree of footprints and their refcounts (`[fp+4]`, plain read-modify-write), the cell graph and the lot transforms. These are the structures `0x82EDB0` reads and copies refs from [verified docs/177 §7, docs/178 §7.1].
- **Holder:** an async route job holds it on the worker for the whole job (H6 S3b; docs/134 §10: "the worker holds `+0x918` for the whole job"). TailUnlock lends it only in post-pass tails, DoorMemo only on host calls (docs/178 §5, docs/150, docs/159).
- **How long** (hitchlog3 runs, about 10 min each) [measured]:

| world | jobs | mean job | longest job | `0x75A64C` waits / s / longest | `0x05000000` (`RunPlan`, docs/87) waits / s |
|---|---|---|---|---|---|
| BP | 12,157 | 14.3 ms | 398 ms | 922 / 9.1 / 111 ms | 996 / 14.4 |
| IP | 5,814 | 27.6 ms | 402 ms | 475 / 8.3 / 221 ms | 51 / 0.9 |
| SV | 6,302 | 27.2 ms | 501 ms | 549 / 5.8 / 209 ms | not in the top 12 |

  The mean job is `asyncUs / asyncStarted`. The waits are the block table (`blockKey/N/Us/MaxUs`). The longest wait is the slot maximum; docs/247 §11.8 quotes 220 ms from the same data.
- **Inside the validator** the game holds `+0x918` only for the query and the copy, which adds `+1` per footprint into a stack vector. Then it releases it. The hit loop `0x75A672..0x75A92E` and the vector's release `0xAAC090` (`-1` each) run after the release [verified docs/178 §7.1, docs/177 Table §4]. In vanilla no job runs at the same time; with our async jobs, those plain writes would race the worker's own writes on the same footprints. That is why `gvro.cpp` hands no refs back.

## 3. `[GoalValidRO]` today (ts3native 0.36.47+, `gvro.cpp`, on in master and v0.2.8)

| | BP | IP | SV |
|---|---|---|---|
| `gvCalls` (sim, all) | 327,709 | 274,716 | 250,861 |
| answered, empty (`gvTaken`) | 8,167 | 2,084 | 2,235 |
| of those off any lot (`gvNoLot`) | 8,167 | 1,951 | 2,235 |
| footprints found, the game's call (`gvFb_6`) | 13,207 | 3,493 | 3,252 |
| other fallbacks (`gvFb_1` try, `_4` tripwire, `_5` job end) | 3 | 2 | 5 |
| hits share of calls made during a job | 61.8% | 62.6% | 59.3% |

- About 93% of `gvCalls` run with no job, use the game's call and wait for nothing [measured: `gvCalls` minus the job-time calls].
- `gvTaken == gvNoLot`: no on-lot point was ever empty. The radius at `0x106E33C` around a ring point next to an object or a wall always meets a footprint, and the Sim's own footprint may count too [inferred]. Mode 1 empty answers are worth no more than they are now.
- The fallback is the game's `0x82EDB0`, which waits on `+0x918` for the rest of the job. That is the 9.7-15 ms/s.

## 4. Who calls: the measured caller split

There are 238 / 109 / 79 hitches (BP / IP / SV) with `rtMaxKey` `0x75A64C` in the hitch records. They are grouped by the hitch's longest task (its `from`/`to` script frames). Which task waited is [inferred]: the longest task is usually the waiter, because the wait makes it long.

| caller group | BP rtLock | IP rtLock | SV rtLock | worst wall | async frames seen |
|---|---|---|---|---|---|
| `StuckWatchdog.CheckIdle` (ours: `SpotVerdict` -> `Sim.IsPointInLotSafelyRoutable`) | 1,216 ms, 16% | 2,366, 33% | 1,637, 45% | IP 458 | 0 |
| route following: `FollowRouteAction.PerformRoute` (sub-path, portal traversal), `ObstacleEncounteredRouteInterruptAction` | 2,975, 40% | 3,153, 44% | 1,733, 47% | SV 419 | 14 of 185 |
| portal start (`ElevatorPortalComponent.OnPortalStart`) | 1,604, 22% | — | — | 137 | 1 |
| interactions (`DoInteraction`, `WaitForExitReason`, socials) | 522, 7% | 889, 13% | 189, 5% | IP 418 | 14 |
| state machine events (`StateMachineClient.GetNextEvent`) | 742, 10% | 581, 8% | — | 288 | 5 |
| other | 374, 5% | 117, 2% | 115, 3% | 197 | 3 |

In ms/s the shares of the per-world totals (BP ~15.2, IP ~13.8, SV ~9.7 ms/s; docs/247 §11.8) are: watchdog **2.4 / 4.6 / 4.4**; route following and portals **9.4 / 6.1 / 4.6**; interactions, events and other **3.3 / 3.2 / 0.8** [measured shares, attribution inferred].

- **Watchdog.** `CheckIdle` queues only non-household Sims that are not selectable (`if (household || s.Parent != null || …) continue;`, `RouteFixes.cs` ~:253 [verified code]). It runs in its own yielding task (`MayYield()` -> `Simulator.Sleep(0)` after each full plan). Its check is a full sync plan because foreign-task plans are sync since `[RouteAsync] FallbackAsync=0` (dropped from v0.1.5, `RouteBackoff.cs` comment) and the watchdog task is not the Sim's task (`AsyncEligible` needs `Simulator.CurrentTask == sim.ObjectId`) [verified code]. The coordinator's 477 ms example (`rtLock` 312, `rtPlanWait` 157) is the two-take shape of §1: validation first, then the plan.
- **Route following, portals, events.** These are the Sims' own tasks, yet mostly with no `AsyncWait` frame, so they are sync [measured]. Why they are not async (refused `M1Pending` 142-364 per run, a non-yielding context inside a portal or state machine callback, or a `Replan*` IC kind the A10 channel does not take) is [inferred]. Part 0 logs it.

## 5. Can validation move into the job? (Rodney's question)

- **No caller validates first and submits a job after.** Goal validation is a step inside one plan call: the candidates go straight into the same `Route_PlanCommon`'s A\*. The async path validates inside the job, on the worker, under the job's own hold (`[RouteJobPhases] GoalSplit` times `0x759B20` only on the worker inside jobs, docs/177 §5 [verified]). That the async START on the sim does not call `0x759B20` is [inferred]; the counter `gvAtStart` (Part 0) proves it.
- **So "validation in the job" means "the whole sync plan in the job".** The caller needs the plan result before it goes on, so this works only where the calling task can sleep until READY (`AsyncWait`): the own-task path (docs/18), the foreign path (docs/134 `FallbackAsync`, docs/176 `ForeignKinds`), or our own tasks (`stuckcheckasync` already does this for EA's stuck check, `FewerPlans.cs` ~:205).
- **Exactness.** The plan on the worker is the same planner on the same world state, plus or minus the ticks it waits. docs/134 accepted this for own-task plans, and it is the shipped async path. For household and selected Sims, an async plan they would have got anyway is exact in the shipped sense. Moving a household plan out of a context that cannot yield is not possible without changing script flow, so those plans keep the wait.
- **Sync plans with no job running** do not wait: `GvStub` passes straight to the game when `g_async.state != A_RUNNING`, so nothing changes for them.

## 6. Options

Gains are ms/s of the 9.7-15 ms/s; worst is the worst hitch with this key (BP 378, IP 458, SV 419 ms).

| option | what | covers | risk | ms/s | worst | effort |
|---|---|---|---|---|---|---|
| O1 read-only hits (B1) | run the validator's hit loop on the clone's footprints inside `GvRun`'s QL hold (no refs), answer when every slot target is a verified read; the gather `0x82F0B0` at `0x75AA11` gets the same | every caller, every Sim, exact | medium: the loop has a 23-way type switch and virtual slots `+0x84`, `+8`, `+0xC0`, `+0x5C`, `+0xD4`, `0x939560` lookups, output writes `[ebp+0x20]+0x20` / `[ebp+0x24]`; 21 indirect sites unresolved (docs/177) | **~0 for plans** (moved to `0x75AA16`, `0x760BFF`, `0x05000000`); only the share with no later take | unchanged for plans | static map 1-2 h (REA, needs a free CPU) + build 1.5 h + shadow job 10 min |
| O2 deferred validation = plan in a job (A) | the sync plan becomes a job, the calling task sleeps | yielding callers: watchdog for sure, own-task refusals | low for our task; known for FallbackAsync (bundle worse once, `ip-locktaker-defer-ab`, ForeignKinds alone never measured) | watchdog 2.4-4.6; plus the A2 share | watchdog 458 -> job length on the worker | A1 ~45-60 min; A2 config ~15-30 min |
| O3 cached result | memo of the hit-loop result per (point cell, option word, level), dropped on any S2 writer generation change | repeated ring points | medium-high: the loop reads object state (`+0xC0`, `+0x5C`, flags `+0x2C`) that changes without footprint writes; ring points move with the target | unknown hit rate; still a moved wait for plans | unchanged | ~2 h + a shadow job; **rejected**: same moved-wait limit as O1 with more risk |
| O4 unseen-only skip (B2) | for unseen non-household Sims, while a job holds `+0x918`, the sync plan is not run: the existing unseen fallback answers (place after travel time, docs/202, `unseen-routing-relaxed`) | unseen share of route following, portals, interactions | medium: behaviour change, unseen only; snap back when seen; household, selected and unknown scope exempt | the unseen part of 9.4 / 6.1 / 4.6 (unseen share unknown, Part 0) | the unseen part | ~1-1.5 h + verify-shortcut run |
| O5 retry before wait | retry the clone for at most ~1 ms on QL busy or tripwire | GV `gvFb_1/_4` (1-4 per run), **H7** | low | H4 ~0; H7 0.6-2 | H7 201 -> a few ms on success | ~30-45 min shared helper |
| O6 job lends `+0x918` (P5) | the worker lends the lock at safe points | all | high, docs/155 territory | — | — | out of scope |

## 7. The feature: Part 0, A and B, each behind its own switch

All parts are default off and are runtime switches (`native_set` / ts3fixes shortcut), so `--ab` pairs work in one load. Counter slots are reserved at build time (`node tools/registry.ts reserve native <n>`); the names below are the design.

### 7.0 Part 0: attribution (`[GoalValidRO] Attr=1`, native switch `gvattr`)

- In `GvStub`, for every call during a job: the source class from the ts3fixes route-source code (`RouteSourcesCore`: watchdog, stuck check, own task, foreign, none) as the plan's START or `IcPlanHook` saw it; the scope (household or selected, unknown, unseen non-household) from the same scope bit `UbCore` / `UnseenViewers` push; and the native caller (the validator's frame: return address of `0x759B20` and one frame up, read through the frame pointer chain, which needs `/Oy-` on `gvro.cpp`).
- Counters: `gvSrcN[6]`, `gvSrcWaitUs[6]` (time of the game's call when it falls back), `gvScopeWaitUs[3]`, `gvAtStart` (calls from inside our START handler: expected 0), and `gvLaterTakeUs`: the `+0x918` wait the same sim-thread IC call has after the validator, summed until `IcPlanHook` returns. This is the moved-wait meter that decides B1.
- Log line `gvattr:` at most once a second: top 8 native caller pairs with n and ms.
- Cost: one TLS read and two interlocked adds per job-time call (~21k per 10 min). This is light, so it is allowed in timing runs.

### 7.1 Part A1: the watchdog's route check as a job (ts3fixes `watchdogasync`, native `[RouteAsync] ForeignKinds` gets one more kind bit)

- Where: `SpotVerdict(…, mayCheck: true, …)` from `CheckIdle`'s queue loop only (not `SafeSpot`, not the game's callers through `RoutableCacheHook`).
- How: when ts3native reports that a job holds `+0x918` (the existing `GetOption2` channel; the same flag watchdoglockskip reads), the check's plan is sent as a foreign START (bit 61, `kArStart | kArForeign`, as `AsyncKindStart` does) with the new kind "own check". The watchdog task sleeps in `AsyncWait` and takes the verdict from READY. With no job running, the check stays sync as today (no wait there).
- Exactness: only non-household, non-selectable Sims reach this loop [verified code]. The verdict comes from the same planner. The verdict cache key, the per-tick budget `kMaxSpotChecksPerTick` and `Act`'s reset rules are unchanged. The verdict may arrive 1+ ticks later; the loop already re-validates the Sim after every yield (`Still` and position checks at the top of `tries`).
- Refusal: queue full, `M1Pending` or the kind switched off means the START answers false, and then §7.4's order applies.
- Counters (ts3fixes `PatchCounters`, slots at build): `wdA1Sent`, `wdA1Started`, `wdA1Refused`, `wdA1Ms` (sleep ticks); native `gvSrcWaitUs[watchdog]` should fall to ~0.
- Effort ~45-60 min (ts3fixes + one native kind + selftest), one IP job of 10 min.

### 7.2 Part A2: own-task sync plans that are refused today (configuration first)

- Part 0 names why the route following, portal and interaction plans are sync. Then turn on the switch that exists for that reason: `M1Retry` (docs/134 §2.6) for `M1Pending`, the 32-deep queue for `QueueFull`, `asyncreplan` for `Replan*` kinds, and a `ForeignKinds` bit for foreign followers (docs/176).
- No new code unless Part 0 shows a reason with no switch. Plans in a non-yielding context (portal callbacks, state machine events) cannot sleep, so they go to Part B.
- Exactness: the shipped async-path argument (§5). Household plans may take this path, as they already do on the own-task path.
- Counters: the existing `asyncRefused*`, `fbRetry*`, `fbForeign*`, and `gvSrcWaitUs[own task]` before and after.

### 7.3 Part B: plans A cannot take

**B2 (`[GoalValidRO] UnseenDefer=1`, native switch `gvunseen`).**
- At `IcPlanHook`'s sync entry (`SyncIc`, `entry nojob`), before goal generation, when all of these hold: a job holds `+0x918` (`g_async.state == A_RUNNING`, not the owner); the plan's Sim scope is unseen non-household (the `UbCore.Unseen` bit; household, selected, unknown and no-Sim plans are exempt); the existing unseen fallback is on for that plan kind. Then the plan is not run: the unseen fallback answers it as it does for a refused unseen plan (docs/202 D walk-time hold or teleport after travel time, `UfPlan` frames). Everything else runs the game's plan.
- Why at plan entry and not at `0x75A647`: answering in the middle of the plan only moves the wait (§1).
- Snap back: the fallback's own rules (visible again -> exact plan).
- Counters: `gvUnseenDeferN`, `gvUnseenDeferRefused[4]` (scope, kind, switch, no fallback), and `gvScopeWaitUs[unseen]` -> ~0.
- Effort ~1-1.5 h; one verify-shortcut run (memory `shortcut-verification`), then the natural bundle.

**B1 (`[GoalValidRO] Hits=1`, native switch `gvhits`, and `GatherRO=1` for `0x82F0B0` at `0x75AA11`). Built only if `gvLaterTakeUs` is <= 75% of `gvSrcWaitUs` (Part 0).**
- Template: `[IdleCheckRO]` C2 / C2b (`idlero.cpp`). The loop runs on the clone's primary objects inside `GvRun`'s QL hold through the existing `visit` hook. Every slot target is checked against a verified table, and any unknown target is a doubt that calls the game. The answer goes to the validator's state as the loop's own exits write it, and the stub jumps to the matching exit (as C2b's `kIcExit`).
- First a static map of `0x75A672..0x75A92E` (and `0x75AA40..0x75AB5A`): the 23-way switch, the slot targets, and the writes. This is REA work when no timing job runs.
- Shadow (`gvhitsshadow`, no job): the loop on the game's vector and on the clone, compared; `gvHitsMismatch` must be 0.
- Exact for every Sim, so no scope rule is needed. It gains only where nothing else takes `+0x918` later.
- Counters: `gvHitsTaken`, `gvHitsDoubt[8]`, `gvHitsShadowRuns`, `gvHitsMismatch`.

### 7.4 Order for `StuckWatchdog.CheckIdle`, with watchdoglockskip (being built in `g2pair1108b`)

The order per queued Sim, in `CheckIdle`'s `tries` loop:

1. **Read-only answer first: the verdict cache and `idlememo`.** `sPlanOk.Covers` (c[233]/[236]) and `SpotVerdicts` (c[37]) answer with no plan and no lock. For the watchdog this is Part B's read-only answer. Native B1 cannot serve it, because its check is a whole plan and would wait at the plan's next take.
2. No job holds `+0x918`: the sync check as today (no wait; c[39]).
3. **A job holds it: Part A1.** The async check; the task sleeps (`wdA1Started`).
4. **A1 refused or off: watchdoglockskip.** Skip the Sim this pass (`wdLockSkip`); it stays queued with its `Still` record, so the next pass checks it.

- No-conflict rules: watchdoglockskip keys on "A1 not started", not on "lock held". With A1 on, it fires only on `wdA1Refused`. With A1 off, it fires whenever the lock is held, which is its current design.
- Both act only inside `CheckIdle`'s queue loop, where household Sims never get, so the household rule holds by construction.
- A skipped Sim is never reset in that pass. A false "unroutable" verdict cannot come from either layer (A1's verdict is the planner's; the skip gives no verdict).
- Share per layer, in one run: cache c[37] + c[236]; sync c[39] minus `wdA1Started`; A1 `wdA1Started`; skip `wdLockSkip`. Native `gvSrcWaitUs[watchdog]` is what is left; it should be ~0 with A1 + skip on.

### 7.5 Expected totals

| part | callers | ms/s BP / IP / SV | worst | risk | effort |
|---|---|---|---|---|---|
| 0 | all (counters only) | 0 | — | none (light tool) | 30-45 min, no own run |
| A1 | watchdog | 2.4 / 4.6 / 4.4 | IP 458 | low | 45-60 min + IP 10 min |
| A2 | own-task refused plans | part of 9.4 / 6.1 / 4.6, after Part 0 | SV 419 | known (FallbackAsync history) | 15-30 min config + one A/B pair |
| B2 | unseen non-household sync plans | the unseen part of what A2 leaves | same | medium, unseen only | 1-1.5 h + verify-shortcut |
| B1 | callers with no later take | conditional, likely < 2 | — | medium | 3-4 h + shadow job |
| left | household, selected and unknown sync plans that cannot yield | the rest, sized by `gvScopeWaitUs[household]` | — | — | — |

These shares are [inferred] from the hitch attribution. The moved-wait rule means each part's real gain is read from the sum of keyed sim waits per arm (`rtLock` total and `mwKey*`), not from `0x75A64C` alone.

## 8. H7: the `0x85FE10` fallback wait

- Same lock, same callee: `Locomotion_ObstacleCheck 0x85FD10` calls `0x82EDB0` twice, and `[SteerRO]` answers it read-only, hits included (its own consumer, no refs) [verified `steerro.cpp`]. The waits left are its fallbacks: `steerFbState` 3/7/3, `steerFbQlBusy` 12/14/8, `steerFbTrip` 10/6/40, against `0x85FE10` waits 22/20/48 (docs/247 §11.7) [measured, the counts match].
- So H7 does **not** share H4's cause. H4 is structural (hits need refs, and plans take the lock again). H7 is transient: a W1-W6 writer holds QL exclusive on the worker for a short write, or a tripwire fires. Then the fallback waits for the whole job.
- Fix (`[SteerRO] Retry=1`, native switch `steerretry`; a shared `SrRetry` in `steerro.cpp` that `gvro.cpp` and `idlero.cpp` can also call): on `SR_FB_TRY` or `SR_FB_TRIP`, `SwitchToThread` and rerun the clone, at most 4 times within ~1 ms of QPC. Then the game's call. Each rerun starts from S2's protocol again (`g_qlSeq`, `g_twEpoch`, `g_twGen`), so a rerun answer is as exact as a first answer. `State` fallbacks are not retried.
- Counters: `steerRetryN`, `steerRetryOk`, `steerRetryUs`, `steerRetryMaxUs`.
- Expected: the QL busy part for sure and most of the tripwire part, **0.6-2 ms/s**, worst 201 ms -> about 1 ms when a retry succeeds [inferred]. For H4 the same helper covers only `gvFb_1/_4` (1-4 calls per run), which is negligible.
- Effort ~30-45 min + selftest; measured inside the Part A1 job (both on in one load, `--ab steerretry`).

## 9. Build order and repro plan (built 2026-11-08, see §10)

0. REA, with no timing job running: `procedure_callers` for `0x759B20`, `0x75AFA0` and `0x751FF0`, to confirm the sim callers are plan paths only (I-453-2).
1. Part 0 + `SrRetry` (one native build).
2. Part A1 (ts3fixes + native kind).
3. Read Part 0; then A2 config, B2, and B1 only if its gate passes.

**repros.json idea: `ip-watchdog-lockwait-loop`** (based on `bp-idle-unroutable` and `ip-goalvalid-loop`):

- bug: the sim waits on `+0x918` at `0x75A64C` from `StuckWatchdog.CheckIdle`'s full route check while an async job runs (IP 458 ms, docs/250 §4).
- fix: `watchdogasync` + `gvattr` (and `watchdoglockskip` as the third arm).
- Firing:
  1. `spawn_sims` 12 townies on a community lot, unseen (camera on the household lot), each given a standing idle spot through the agent's existing idle-spot setup of `bp-idle-unroutable`.
  2. `time` forward past `kIdleMinutes`, so they enter CheckIdle's queue.
  3. During each watchdog pass, a long job: household `go_here` to the farthest lot, plus a `crowd_go` of 10 more townies, so a 100-400 ms job holds `+0x918` when the check runs.
  4. Unpause 60 s, pause (memory `clean-repro-sequence`).
- Arms per firing with `native_set` / patches: A off, A on, A off + lockskip.
- expect: no crash, no tripwire line; on arms `wdA1Started` >= 1 per firing and `gvSrcWaitUs[watchdog]` ~0, and household Sims never in `wdA1Sent` (assert from the `gvScopeWaitUs` split); off arms `0x75A64C` waits as today. Read with `hitch-ab.ts --scenario`, plus the sum of keyed sim waits per arm (moved waits).

**Queue sketch `tools/harness/queues/h4-goalvalid.json`** (not written):

| job | what | length | gate |
|---|---|---|---|
| `ip-h4-smoke` | the built pair, every new switch read back, `gvattr` lines present, per-arm split, heavy tools off (memory `measurement-smoke-before-timing`) | 5 min | all counters move |
| `ip-watchdog-lockwait-loop` | the repro above, 10 firings per arm | ~15 min | `wdA1Started` > 0, no household row |
| `ip-h4-attr-natural` | natural IP, Part 0 + A1 + `steerretry` on, `--ab watchdogasync` and `--ab steerretry` in one load (ABBA) | 10 min | the decision data for A2, B2 and B1 |
| `sv-h4-attr-natural` | the same on SV (watchdog share 45%) | 10 min | as above |

Then B2's verify-shortcut run (unseen route fallback, the world that fires it), and B1's shadow job only if its gate passes.

## 10. Built (2026-11-08, pair `g2pair1108c`, ts3native 0.36.195, branch `feature/h4-goalvalid-a1`; NOT RUN)

**Step 0, caller list (I-453-2) [verified, Ghidra `tools/ghidra/CallerTree` on a copy of TS3W.exe with no timing job running].** `0x759B20` has two callers: `0x75AFA0` (site `0x75B5AC`) and `0x75B610` (sites `0x75BD86`), and both are reached only from `0x75BE60` (sites `0x75C03D`, `0x75C246`, `0x75C2B8`), whose only reference is the vtable slot at `0x100424C`. `0x751FF0` is called only from `Route_PlanCommon 0x7606E0` (site `0x76099F`), which comes from `Route_StartPointsFromPosition` (`IC_Route_Plan`, `IC_Route_Replan`) and `Route_StartPointsBuild` (`IC_Route_PlanFromPoint`, `IC_Route_ReplanFromPosition`). So the sim callers are plan paths only. New: `0x75B610` is a second validator caller under `0x75BE60` that this doc did not list. The link `0x75BE60` <- `0x751FF0` goes through the vtable (docs/105 §6) [inferred from there, not a direct xref].

- **Part 0 `gvattr`** (native v174 0x5E4C-0x5E93, ini `[GoalValidRO] Attr=0`, default off): as §7.0. ts3fixes sends class + scope on UserDiag op 35 (`H4Core.SrcClass` / `Scope`), `GvaPlanEnd` at op 12 books the later take. Selftests `TestGvAttr`, `h4core`.
- **H7 `steerretry`** (native v175 0x5E94-0x5EBF, ini `[SteerRO] Retry=0`, default off): as §8, counters named `srrN/srrOk/srrFb/srrFbWaitUs[2]`, `srrUs`, `srrMaxUs` (not `steerRetry*`). Two sites per array: 0 steering `0x85FE10`, 1 the EditPath goal gather `0x85F6CB` (`0x82F0B0` from `RouteFollow_EditPath 0x85F5A0`), both under the one `LocoHook` clone; a retry is booked by the clone phase that failed, the fallback wait by the `+0x918` key (both arms). The script-side EditPath waits (`OnAboutToStartFollowingRoute` / `AsyncWait`, `EditPathForRoute 0x85FF30`, the ~64 hitches >= 200 ms of the 11-07 gate2 runs) are not under this hook, so `steerretry` does not reach them. Selftest `TestSteerRO`.
- **Part A1 `watchdogasync`** (ts3fixes slots 872-875 `wd_a1_sent`, `wd_a1_started`, `wd_a1_refused`, `wd_a1_ticks`, default off). Deviation from §7.1: no new native kind. The check's plan goes out as an **own START** from the watchdog task (`RouteBackoff.cs OwnerTaskSlot` 872, the `stuckcheckown` path); native `AsyncStart` takes an own START from a non-Sim task (refusals LOS / NoYield / M1 / Running / QueueFull only), and KindDeadlines sees the plan's scope as for today's sync check. Order `H4Core.WdRoute` (§7.4): cache or no job -> sync; job holds the lock -> A1 when on and ts3native would start it (`HcWould` q 1, 5, 7 and the task may yield), else watchdoglockskip if on, else sync. Household, selected and unknown-scope Sims never reach A1 (selftest `h4core`, all 32 combinations). A START refused after the pre-check (queue full) runs that check sync: `wd_a1_sent - wd_a1_started`.
- Repro `ip-watchdog-lockwait-loop` and queue `tools/harness/queues/h4-goalvalid.json` as §9 (natural jobs flip `watchdogasync,steerretry` together).

## 11. Step 0: callers of `0x759B20`, `0x75AFA0`, `0x751FF0`, their threads and the `+0x918` lock (2026-11-12, static)

Static only, Ghidra headless on a copy of the Steam 1.67 TS3W.exe (own project `game-copy/ghidra-h4`, `CallerTree`, `XrefsTo`, `StoreScan`, `DecompileTo`, a disassembly range dump), plus our own source. No game run. §10 (caller list, on branch `feature/h4-goalvalid-a1`) found the two validator callers; this section adds the virtual link, the threads and the lock. Ledger §478.

**Correction to the request.** `0x1021CF0` is not the routing lock. It is the pointer to the infinite timeout that every `Lock_Acquire(world+0x918)` call pushes (docs/18 Q2). `XrefsTo 0x1021CF0` lists 29 such pushes, all in `0x829370-0x834860` (`Route_PlanLocked 0x82C72C`, `RoutingWorld_ObstacleQuery 0x82EDC7`, `RoutingWorld_GoalFootprintGather 0x82F0C7`, ...). None is in the caller chain below [verified]. The lock is `[g_RoutingWorldPtr]+0x918`.

### 11.1 The complete static caller tree [verified]

- `GoalPoint_Validate 0x759B20` <- `RadialRangeDest_FirstValidGoal 0x75AFA0` (call `0x75B5AC`) and <- `RadialRangeDest_RingGoals 0x75B610` (call `0x75BD86`). There are no other direct calls and no data references.
- `0x75AFA0` <- `RadialRangeDest_GenerateGoals 0x75BE60` only (call `0x75C03D`, taken when the radius band is under 1 m). `0x75B610` <- `0x75BE60` only (calls `0x75C246`, `0x75C2B8`).
- `0x75BE60` has no direct caller. Its only reference is the vtable slot `0x100424C` = `vt_RadialRangeDestination 0x100423C` + `0x10`.
- The slot is called at `0x752042` in `Route_GenerateDestinationCandidates 0x751FF0`. That function walks the route's destination list (`[this+0x18]`, a ring list whose entries point 4 bytes into each destination object) and calls `[vt+0x10]` on each destination with seven arguments. It ORs the results and returns "any goal". So docs/105's "slot `+0x10`" link is now [verified] in disassembly (§10 had it as inferred).
- `0x751FF0` <- `Route_PlanCommon 0x7606E0` only (call `0x76099F`). `0x7606E0` <- `Route_StartPointsFromPosition 0x7641D0` (`0x764298`; from `IC_Route_Plan 0x765160` at `0x7651DC` and `IC_Route_Replan 0x765490` at `0x7654C8`) and <- `Route_StartPointsBuild 0x7642C0` (`0x7643FB`; from `IC_Route_PlanFromPoint 0x765240` at `0x76542C` and `IC_Route_ReplanFromPosition 0x765550` at `0x7655C3`). The four ICs are reached only through their binding slots (`0x11608E0`-`0x11608F8`).
- **What follows from the tree:** every `0x759B20` call is the goal generation of a **radial-range destination** (`IC_Route_AddRadialRangeDestination 0x756B20`, docs/92) inside one of the **four route plan ICs**. Point, lot and slot-array destinations have their own slot `+0x10` and never reach the validator. A non-plan caller on the sim thread would need some other code that calls slot `+0x10` on a radial-range destination. The direct-call search finds none, and the `[reg+0x10]` call-site scan of `0x740000-0x770000` lists only route handle-table and IC accessor calls besides `0x752042` [inferred: virtual call sites cannot be proven exhaustive statically]. Part 0's `gvaSrcN[0]` (no class) and `gvaAtStart` are the runtime check.

### 11.2 Threads and the lock

| path | thread | `+0x918` when `0x759B20` runs | takes inside the validator | evidence |
|---|---|---|---|---|
| sync plan: an IC with no async START (NoStart, refused, ts3native off), ts3native `IcCollect` -> `SyncIc` | sim | **not held.** None of the 4 ICs, `0x7641D0`, `0x7642C0`, `0x7606E0`, `0x751FF0`, `0x75BE60`, `0x75AFA0`, `0x75B610` calls `Lock_Acquire` (StoreScan `lockAcq 0` each). The plan takes the lock first in `Route_PlanLocked 0x82C6E0` (`0x82C72C`), which `0x7606E0` calls after goal generation | `ObstacleQuery 0x82EDB0` at `0x75A647` (wait key `0x75A64C`) for every validated point. `GoalFootprintGather 0x82F0B0` at `0x75AA11` (key `0x75AA16`) only for a point the hit loop did not reject and with RouteOption `0x10000` (`RoutingOptions_Test` at `0x75A99F`). Each callee acquires and releases inside itself | [verified] disassembly, StoreScan |
| async job (`ServeAsync`) | route worker | **held by the worker for the whole job** (`LockAcquireHook(j->lock)` before the IC, ts3native.cpp `ServeAsync`). The callee acquires nest (recursive lock) and never wait | the same two calls, as nested levels | [verified] our source, docs/134 §10 |
| async START on the sim (`AsyncStart`) | sim | — | none: START only fills a slot and queues it. No IC, no plan | [verified] our source (`AsyncStart` -> `JobQueue`); `gvaAtStart` should read 0 |
| sim plan while a job runs | sim | the sim **waits** at `0x75A647` for the rest of the job (and at `0x75AA11`, `0x82C72C` for any job that starts in between) | — | docs/250 §2 [measured] |

- **Only the sim thread ever waits here.** The worker owns the lock during its own validation. The sim waits only while `g_async.state == A_RUNNING`, or rarely behind another `+0x918` holder (lot streaming, docs/18 Q2).
- **No lock held during the hit loop on the sim.** `0x82EDB0` releases `+0x918` before it returns, so the loop `0x75A672..0x75A92E` and the vector release `FootprintRefVec_ReleaseRange 0xAAC090` run unlocked on the sim (docs/177 §7; §13 here). Inside a job they run under the worker's hold.
- **Several takes per plan.** `0x75AFA0` validates sample points until one is accepted. `0x75B610` validates every ring point (accepted -> candidate vector, rejected -> non-candidate list `0x74C650`). So one plan calls `0x82EDB0` once per point, plus `0x82F0B0` for accepted points with option `0x10000`, plus `Route_PlanLocked` unless the plan ends first (§13.3).

### 11.3 What this means for the H4 parts

- **Part 0 (`gvattr`).** The six source classes cover every sim caller, because all of them are inside one of the four ICs. `gvaAtStart` must be 0. `gvaSrcN[0]` (no script task) is the only bucket that could hold an unknown caller.
- **Part A (plan into a job).** This is confirmed as the only exact way to remove all three takes at once (validator, gather, `PlanLocked`): on the worker they nest under the job's own hold.
- **Part B2 (unseen defer at plan entry).** `IcPlanHook` covers only `IC_Route_Plan`. Route following replans arrive through `IC_Route_Replan`, `IC_Route_PlanFromPoint` and `IC_Route_ReplanFromPosition`. In the release ini (`Kinds=0 ForeignKinds=4`) only ReplanFromPosition has an A10 detour (`a10.cpp`). So B2 must hook the plan-entry of all four ICs (or their shared `Route_PlanCommon 0x7606E0`, which is after the start points but still before `0x751FF0`) to cover the route following share (§4) [verified hooks, coverage inferred].
- **Part B1 (read-only hit loop).** It can only help the validator's own take. §13 maps the loop and gives the go/no-go.

## 12. Part A2: switches that let refused own-task sync plans go on safely (2026-11-12, static, design only)

Sources: ts3native `AsyncStart` / `IcCollect` / `RtM1Why` (ts3native.cpp, rtsites.cpp, a10.cpp), ts3fixes `RouteBackoff.cs` (`PlanBackoffBody`, `AsyncEligible`, `AsyncKindStart`, `StepAside`), master ini `[RouteAsync]` (release: `FallbackAsync=1`, `M1Retry=0`, `Kinds=0`, `ForeignKinds=4`, `JobYield=0`, `KindDeadlines=1`, `KdExemptSeen=1`). All [verified code] unless marked. Ledger §478.

### 12.1 Why an own-task plan stays sync today, and which switch exists

An own-task plan is a plan from the route Sim's own yielding task (`Simulator.CurrentTask == sim.ObjectId`). §11 shows each such plan waits at `0x75A64C` while a job runs.

| reason (`kRfbName`, counter) | when | switch today (release value) | does it remove the H4 wait? |
|---|---|---|---|
| **NoStart, Replan-family IC** (`arRefused[k]`, not in the total) | `IC_Route_Replan` / `PlanFromPoint` / `ReplanFromPosition` from the Sim's own task: no own kind hooked (`Kinds=0`; kind 4 hooked for foreign STARTs only, `g_arOwnKinds = 0`) | **`[RouteAsync] Kinds`** bits 1/2/4 + ts3fixes **`asyncreplan`** (`KindsMode`, `--ab asyncreplan`). Release: off. Own A10 kinds "EXPERIMENTAL, never run in game" | yes: the whole plan goes into the job (all takes nest there) |
| **M1Pending** (`asyncRefusedM1[3]`) | pending lot updates; `FallbackAsync=1` applies them under one `+0x918` try. If **a job holds the lock** the try fails: this is the H4 case exactly | **`[RouteAsync] M1Retry`** (`fbRetryMode`, `--ab m1retry`): park, each READY poll tries again, give up after 30 ticks -> sync. Release: off | yes for parked plans that start; `fbRetryGaveUp` ones still wait |
| QueueFull (`asyncQueueFull`) | all slots busy | `FallbackAsync=1`: 32 slots (on) | already done |
| Foreign (`RFB_FOREIGN`) | not the route Sim's task | `FallbackAsync=1` (on), `ForeignKinds=4` (on) | already done (not own-task) |
| **Running** | this route's own job still runs (a replan during its job) | none | only by yielding (JobYield) |
| **Active** (`arActive[k]`) | an A10 kind or a foreign START on a **route being followed** (`Route_IsActive 0x74CFC0`, route+0x60): never planned on the worker (race not tested) | none, by design | only by yielding (JobYield) |
| **Los** (`arLosRefused`) | RouteOption `0x2000000` ForcePhysicsBasedRaycast: the validator calls `World_CanSeeObject` at `0x75AC22`, which is not safe on the worker (docs/94 §17.9) | none, by design | only by yielding (JobYield) |
| M1Busy / M1Closing / M1NoLock | a guarded routing call open, travel or quit, world unreadable | none, by design | M1Busy only by yielding |
| M1DirtyIndex (`asyncRefusedM1[4]`) | lot index dirty ("shortcut: refused, not cleaned first") | none | only by yielding; a "clean first" switch would be new code |
| M1FrameOpen (`asyncRefusedM1[5]`) | the plan is nested in a guarded routing frame on this thread | none, by design | no (nested: cannot yield) |
| **NoYield** (`afNoYield`, total) | nested native->managed call: portal callbacks, state machine events (`OnPortalStart`, `GetNextEvent`) | none: a yield here aborts the game (interp.c `yieldReturn`) | **no**: Part B2 (unseen) or the residual |
| C# keeps it sync | `asyncjigsync` (c[758]), not live or travelling, no yielding context | none, by design | `CheckYieldingContext` false = no yield |
| collect side: `fbRetryGaveUp`, Unstarted, Skipped, Uncollected, KindMismatch | the job did not deliver | none | the plan waits as today |

**The cross-cutting switch is JobYield** (`[RouteAsync] JobYield=1` + ts3fixes `jobyield`, docs/183; both off in release). When a START is refused and the task may yield, `StepAside` sleeps the task one tick at a time while ts3native answers JOBBUSY. Then it runs the same sync plan with no job left to wait for (cap 10 s, then today's wait, c[250]). This removes the `0x75A64C` wait, and the later takes of the same plan, for every reason above except NoYield and M1FrameOpen. It does not move the plan off the sim thread. The sim still does the plan's work; it only stops waiting for someone else's job. The result is the same sync plan, run a few ticks later [verified code; measured only in a too-short `sv-jobyield-ab`, PLAN night-1024].

### 12.2 Switch design for A2: three existing switches and one scope gate (default off)

1. **L1, plan into a job:** `m1retry` (M1Pending) and `asyncreplan` with own `Kinds` bits (Replan 1, ReplanFromPosition 4; PlanFromPoint 2 only if Part 0 shows own PlanFromPoint plans). Routes being followed stay refused (`Active`): no new race.
2. **L2, wait by sleeping:** `jobyield` for every refusal left whose task may yield.
3. **New: scope gate `[RouteAsync] A2Scope=1`** (runtime `a2scope`, ~45-60 min incl. selftest). With it on, L1 and L2 act only for routes whose ts3fixes scope message says **unseen (kdScope 2, op 11) or seen NPC (kdScope 3, op 13)**. Household and selected (op 10 -> kdScope 1), **no answer (kdScope 0: unknown counts as household)**, pets and vehicles keep today's path exactly.
   - Native, in `AsyncStart`: the `park` branch (M1Retry) and the own-kind check for kinds 1-3 refuse with a new reason `A2Scope` when the scope is 0 or 1. The scope has to be read before `KdScopeTake` consumes it.
   - ts3fixes: `StepAside` returns at once when `H4Core.Scope(ufWhy, seenNpc) == 0`. Counters: `a2Kept[3]` (by layer: m1retry, kinds, jobyield) for household or unknown routes that kept today's path; `a2Acted[3]`.
   - Why a gate, although own-task async is shipped for household Sims (exact, docs/134): parking (up to 30 ticks), own A10 kinds (never run in game) and step-asides (up to 10 s) all change **when** a controlled Sim's plan runs. CLAUDE.md "household Sims stay untouched" keeps them out until a separate household decision is backed by data. `A2Scope=0` (all Sims) exists for test runs only.
4. **No new code for a reason that has no switch.** NoYield and M1FrameOpen stay sync (Part B2 for unseen Sims). Active and Los stay off the worker and get only L2.

### 12.3 What one A/B must show (after Part 0, after the smoke)

- **Precondition from Part 0** (`gvattr` run): `gvaSrcWaitUs[3]` (own task) is at least ~2 ms/s on the world, and the reasons are known. `gvattr` has no reason split. So the Part 0 run also needs `LogFallbackMs=50` + `LogFallbackStack=4` (existing "sync plan" lines with reason and script caller), read with `nostart-report.ts`. The other option is a counter `gvaWhyWaitUs[19]`, keyed by `g_rfbWhy` at `SyncIc` (~15 min, light) [recommendation].
- **One natural ABBA on IP** (route following share 44%; SV next), 10 min per arm, Part 0 on in both arms. Arm OFF = release ini. Arm ON = `m1retry` + `asyncreplan` (Kinds 5) + `jobyield`, `a2scope=1`. Per-switch counters attribute the parts; an ABBA per switch only if the bundle regresses (memory `timing-trim-1106`).
- **Pass criteria:**
  1. Gain is read from the **sum of keyed sim waits** (`rtLock` total and `mwKey*`), not from `0x75A64C` alone (moved waits, I-453-3), and it is >= 3 ms/s (memory `clear-gain-on-by-default`). `gvaSrcWaitUs[3]` with scope unseen + NPC falls by >= 50%.
  2. **Household untouched:** `gvaScopeWaitUs[0]` (household, selected, unknown) is the same in both arms within noise, and `a2Acted` never counts a kdScope 0/1 route (assert).
  3. Hitches: p99, p99.9, max and counts >= 100 / 300 ms no worse (memory `hitch-metrics-target`).
  4. Correctness: `ArTrip` lines 0 (`arOrphan`, `arMismatch`, `afActiveTrip`); `fbRetryGaveUp / fbRetryParked` < 10%; c[250] step-aside cap 0; route failure rate and stuck-Sim count per arm unchanged; no new script errors.
  5. The refusal counters move as designed: `asyncRefusedM1[3]` down, own `arCollected[k]` up, `jyBusy` > 0.
- If L1 alone carries the gain, `jobyield` can stay off. If only `jobyield` gains, A2 is a pure wait remover (no sim-time gain from the plan's own work).

## 13. B1 prerequisite: static map of the hit loop `0x75A672..0x75A92E`, and go/no-go (2026-11-12, static)

Source: disassembly of `0x759B20..0x75AD40` and of the helpers, jump tables read from the exe, decompile of the callers (all in the session scratchpad, not committed). The meaning of names is marked [inferred] where only the use shows it. Ledger §478.

### 13.1 What the code does

The validator's arguments are `this` = the plan's follower record (its id at `+0x50/+0x54`) and 8 stack words (`ret 0x20`): `[ebp+8]` level/option word, `+0xC` and `+0x10` point copies, `+0x14` RoutingOptions, `+0x18` destination record (id at `+0x20/+0x24`), `+0x1C`, `+0x20` the plan's result record (obstruction id at `+0x20/+0x24`, docs/93 mType 16), `+0x24` the caller's cost factor (float, preset to the constant at `0x107A538`). Return 0 = point accepted, 1 = rejected; the callers then put the point into the candidate or the non-candidate list.

1. `0x75A647`: `RoutingWorld_ObstacleQuery 0x82EDB0` (point `[ebp+0x10]`, radius 0.1 m at `0x106E33C`, option word `[ebp+8]`, forced to `0xE00` on one branch at `0x75A5E0`) fills a stack vector with footprint entries, each with a **plain** `+1` on `[entry+4]`, under `+0x918`. The lock is released inside. Empty vector or false -> the loop is skipped (`0x75A939`).
2. **Per entry** (`0x75A672`; `obj` = entry - 4):
   - null entry: next. **Plain `+1` on `[entry+4]`** (`0x75A686`).
   - `id = obj->vt[0x84]()` (64-bit). Skip if it equals the destination record's id (`+0x18`->`+0x20/+0x24`) or the follower's id (`this+0x50/+0x54`) [inferred roles], or if the id is in the ignore list (`RoutingOptions_IgnoreListHas 0x81C970`).
   - `type = entry->vt[8]()`, 1..23, through the byte table `0x75AF5C` and the jump table `0x75AF4C`. **The "23-way switch" has only four targets** [verified, tables read from the exe]:

| class | footprint types | what happens |
|---|---|---|
| skip | 3-8, 10, 11, 14-16, 18, 19, 21, 22, and any value outside 1..23 | next entry (`0x75A901`) |
| reject | 2, 12, 13, 17, 23 | `0x75A8C0`: if the level-code flag is set, the "low point" flag is not, `obj->vt[0x5C]()` is true and the 64-bit field `[obj+0x18]` is not -1, the result record's obstruction id is cleared to 0. **Return 1** (`0x75AABB`) |
| record + reject | 9, 20 | `0x75A87E`: if the id is not 0 and the result record has no obstruction id yet, `ObjectService_FindById 0x939560`; found -> write the id to `[ebp+0x20]+0x20/+0x24`. Then as "reject" |
| object test | 1 | `0x75A6FD`: id 0 -> next. `FindById` into a ref (`RefPtr_Assign 0x565970`, **LOCK** `+1` on the object); not found -> next. Then `obj->vt[0xC0]()`: **true** -> `obj->vt[8](buf, 0)` kind; kind < 2 or 13 (`0x620CA0`) -> next; else `World_LotLookup 0xC68990` on `[0x11ECBC4]`, no lot -> next; lot transform `0xAB88A0`; `obj->vt[0xD4](xz, 0.123 m at 0xFEC6DC)` [inferred: point inside the footprint]. Hit -> write the obstruction id if empty, **return 1** (`0x75AAA1`). Miss -> `*[ebp+0x24] = 0.001` (`0x1010960`), next. **false** -> `obj->vt[0x5C]()`: true and kind not passable -> **return 1** with no id (`0x75AAB2`); otherwise `*[ebp+0x24] = 0.001`, next |

   - Next entry: **plain `-1` on `[entry+4]`**; at 0 the count is set to 1 and `entry->vt[0](1)` deletes it (`0x75A901`; the same shape as `FootprintRef_Release 0x55CFA0`).
3. Loop end: `FootprintRefVec_ReleaseRange 0xAAC090` (plain `-1` per entry) and free. If RouteOption `0x10000`: the **second take** `GoalFootprintGather 0x82F0B0` at `0x75AA11` and its loop `0x75AA40..0x75AB5A`. That loop has the same skip tests (its id comes from `entry->vt[4]`), and types 2, 4, 9, 12, 13, 17, 21, 23 (table `0x75AF7C` / `0x75AF74`) reject (return 1 at `0x75AC9C`, the same id-clear rule). Then the level/lot test `0x74CB80` and line of sight `0x75AC22` (option `0x2000000`).
4. Every reject exit releases the current entry, the vector and the object ref before it returns.

### 13.2 Data, thread and lock

- **Reads:** the footprint vector from `0x82EDB0`. Six virtual slots of each footprint (`obj +0x84`, `entry +8`, `obj +0xC0`, `+0x5C`, `+8(buf,0)`, `+0xD4`) and `[obj+0x18..+0x1F]`. `ObjectService_FindById` (global hash, no lock, docs/45). `World_LotLookup` and the lot transform. The ignore list. The flags computed earlier in the validator (level code `(opt & 0xCF00)` in a set via `0x55DA60`; a "point below the lot or terrain" flag from `0xC6A250` / `0xC61670` [inferred]).
- **Writes outside the stack:** `[entry+4]` plain `+1/-1`, with a delete at 0 (routing-world footprints, guarded by `+0x918` everywhere else). The object refcount `[obj+4]` (LOCK, delete at 0). `[ebp+0x20]+0x20/+0x24` and `*[ebp+0x24]` (the plan's own records on the calling thread). What the six virtual targets write is not resolved per class (docs/177: 21 indirect sites).
- **Thread and lock:** on the sim (sync plan) the loop runs with **no lock held** (§11.2), after the query released `+0x918`. In a job it runs on the worker under the job's whole-job hold. So the race with a running job is exactly the sim's plain `[entry+4]` writes, the delete at 0, and the virtual calls on live footprints that the worker may change (pending updates, portal flags). `gvro.cpp` hands no refs back for this reason.

### 13.3 Go/no-go for B1: **no-go now** [static]; Part 0 must answer a corrected question

1. **Where a read-only answer at `0x75A64C` can gain.** The same plan takes `+0x918` again (a) at `0x82EDB0` for every further sample point (B1 would answer those too), (b) at `0x75AA11` for any accepted point with option `0x10000` (needs `GatherRO` as well), and (c) in `Route_PlanLocked 0x82C72C`. `Route_PlanCommon` skips (c) only when goal generation fails with a result type other than "no goal" (mainly **mType 16, goal obstructed by an object**: the validator wrote an obstruction id and `0x751FF0` finds the object), or when it has neither candidates nor non-candidates, or on the option `0x200` and bounds exits (result 10). Points the hit loop rejects go to the non-candidate list, so **a plan that only had points rejected still runs `PlanLocked`** and waits there for the rest of the job [verified control flow, decompile of `0x7606E0` / `0x75AFA0` / `0x75B610`]. B1's ceiling is the job-time validator wait inside plans that end before `PlanLocked` (mostly obstructed-goal failures), plus the sim work between validator and `PlanLocked` (sub-ms against 14-28 ms mean jobs) [inferred share].
2. **The Part 0 meter as built cannot decide B1.** `gvaLaterTakeUs` adds the job waits after the plan's last validator fallback. With B1 off, the validator's wait lasts until the job ends, so the later takes find the lock free unless **another** job started in between. The sim's lock wait holds `g_rgOffSimOpen` only while it waits, so the worker may start the next queued job right after. `gvaLaterTakeUs` therefore reads waits for later jobs, not the wait B1 would move. The §7.3 gate "`gvLaterTakeUs` <= 75% of `gvSrcWaitUs`" would pass for the wrong reason [verified code `GvaOrig` / `GvaPlanEnd` on `feature/h4-goalvalid-a1`; effect inferred]. This matches the hitch records (`rtPlanWait` 0-106 ms in `0x75A64C` hitches): the first wait absorbs the job.
3. **Corrected gate (counter change, ~20-30 min, light, before the Part 0 run):** `gvaNoLaterUs` = job-time validator wait (as `gvaSrcWaitUs`) of plans whose end (op 12) saw **no** further sim-thread `+0x918` acquire after the last fallback, waited or not. Set a flag in the owner-thread path of `LockAcquireHook` for the routing lock. Also add `gvaNoLaterN` / `gvaPlansN`. **Build B1 only if** `gvaNoLaterUs` >= 25% of `gvaSrcWaitUs` **and** >= ~2 ms/s on one world. Part 0 must also show `gvaAtStart` = 0 and a small, explained `gvaSrcN[0]`.
4. **Exactness cost, if the gate passes anyway.** The loop is not a data-only pass. An exact read-only copy needs, per clone footprint and under S2's writer protocol: id (`+0x84`), type (`+8`), `+0xC0`, `+0x5C`, the `+8` kind, `[+0x18]`, and a per-class re-implementation of the `+0xD4` point test. It must also keep **entry order** (the first rejecting entry decides, and earlier type-1 entries may already have written the obstruction id or the cost factor), plus the gather loop for option `0x10000`. Every unverified slot target is a doubt that calls the game (C2 template). The estimate goes from 3-4 h (§7.3) to **~5-7 h + shadow job** [estimate]. A cheaper "reject-only" variant (answer only when the first non-skipped entry is type 2/12/13/17/23 and needs no virtual call) is exact only when flag 13 is clear, and still moves the wait to `PlanLocked` in most plans.
5. **Verdict:** do not build B1. Part A (A1 `watchdogasync` on master, default off; A2 §12) and B2 cover the same waits structurally. Revisit B1 only if the corrected Part 0 gate passes.

## 14. Built: B2 `gvunseen` and the Part 0 rows for §12.3 / §13.3 (2026-11-12, ts3native 0.36.245, branch `feature/h4-gvunseen`; built, NOT RUN)

Native build + selftest, ts3fixes build, `h4core` selftest, `hook-sites.ts --check`, `selftest-all`. No game run (Rodney: tests skipped). Counters v189 `0x6018-0x60A7` (after a hold `resv5F80[38]` for v185-v188, which other branches reserved). Ledger: none new; the scope rule is docs/202 §2's, the place in the plan is §11.3.

**Where B2 sits (a deviation from §7.3, for §11.3).** §7.3 put the decision at `IcPlanHook`'s sync entry, and §11.3 asks for all four plan ICs. A native answer at IC entry would have to write the `RoutePlanResult` without running the IC, and the route would keep its old paths and start points: only ts3fixes can clear those (`FailFast`: `ClearPaths`, start points `Vector3.Invalid`, `PlanResult`). So the decision is native and the answer is ts3fixes':

- ts3fixes asks **DEFER?** (`GetOption2`, `optHi 0x83000000`, `optLo 3 | kind << 8`) right before the sync original in the four sync branches: `PlanBackoffBody` (Plan, after a refused START and `StepAside`), `ReplanAsync`, `PlanFromPointAsync`, `ReplanFromPointAsync` (after `AsyncKindStart` returns false). It asks only inside `UfPlanBody`'s marked plan (`sGvuRoute == this`, set around `UfInner`): household, selected, pet, own-check and unknown-scope plans are never marked (docs/202 §2), so they are never asked. All four ICs are covered whether or not a native collect detour hooks them, because the ask is on the script side.
- ts3native (`unseenfb.cpp GvuMsg`) answers 1 only when all hold, else counts the first failed reason in `gvuNo[4]`: **0 scope**: this route carries the MARK (`g_ufH`) and its scope message for this plan said unseen (`g_kdScopeH == handle`, `g_kdScope == 2`, op 11); no message = unknown = household. **1 no fallback**: `UfOn()` (the unseen fallback must be there to answer). **2 lock free**: a job runs (`A_RUNNING`), it is another route's, `g_async.lock` is set, and this is the sim thread; else the plan does not wait, so it runs. **3 streak**: the route has been deferred for `gvuMaxMs` (default 2000 ms, `[GoalValidRO] UnseenDeferMaxMs`, runtime `gvunseenms`, clamped 100-10000 at use); then the plan runs as vanilla and the streak ends. A lock-free ask also ends the route's streak; a streak whose last deferral is `gvuMaxMs` old starts again. 32 route slots, the stalest evicted (`shortcut:` comment in code).
- On 1: the MARK is used up (no IC follows), `g_ufCutH = handle` so CUT? answers 1, Replan's own `add [route+0x1F4],1` is applied (as ReplanDedupe's served repeat), `gvuN` and `gvuKind[kind]` count, and the first 40 events log `gvunseen: route ... kind ... deferred (job route ... holds +0x918)` / `... runs as vanilla`. ts3fixes returns `FailFast((RoutePlanResultType)2)`: the planner's own "Failed, no blocker", the same type `UfLockGate`'s timed-out lock gives (§3.1.1 of docs/202). `UfPlanBody` then sees a cut and runs its recovery: the teleport after travel time for an own-task Plan (`unseenfbteleport`), else the 1.5 s no-retry window; `MayMark`'s 4 cuts in 10 s rule also bounds a Sim.
- Switch: `[GoalValidRO] UnseenDefer=0` (repo ini; release ini untouched), runtime `gvunseen` (`gvuMode +0x6018`, `native-switches.ts`). Read in `InstallGoalValidRO` before its `Enabled` check (no hook of its own). ts3fixes has no switch of its own: with `gvunseen` off native answers 0 before counting. Older ts3native and the game answer false.
- No new patch site: the channel is `GetOptionHook`'s, the Replan count write is a data write. Nothing to register in `hook-sites.ts`.

**Part 0 rows (§12.3, §13.3), on the `gvattr` path, counters only:**
- `gvaPlansN` / `gvaNoLaterN` / `gvaNoLaterUs`: per plan the validator's job-time waits (`g_gvaPlanUs`); `LockAcquireHook` counts owner-thread acquires of the routing lock while `g_gvaWatch` is set (one compare when off); a plan whose end (op 12) saw none after its last fallback adds its waits to `gvaNoLaterUs`. The B1 gate of §13.3: `gvaNoLaterUs >= 25%` of the summed `gvaSrcWaitUs` and >= ~2 ms/s on one world.
- `gvaWhyWaitUs[20]`: the same waits by the `SyncIc` reason in force (`kRfbName` 0-18; 19 = not under `SyncIc`, an IC with no collect detour). `SyncIc` is now a wrapper that sets the reason around `SyncIcBody` and restores the outer one. Second log line `gvattr: wait ms by sync reason: ...`.

**Selftests** (`gvro-test.h`): `TestGvAttrNoLater` (v189 layout, no-later plan with two waits, later acquire, no fallback, bad reason -> 19); `TestGvUnseen` (off counts nothing; unseen + another job's lock: deferred, CUT? once; household (scope 1), unknown (no scope message), seen NPC (3), another route's mark or scope, bad kind, no handle; fallback off; lock free / own job / other thread / no lock; each of the four kinds, Replan count +1 once; the 2 s streak and the vanilla plan after it; a stale streak restarts; `gvuMaxMs` 0 clamped to 100; 33 routes in 32 slots).

**Risks [inferred, for the repro to check].** (1) A deferred plan is a failed plan for the script caller: an unseen NPC's interaction takes its route-fail branch more often (no balloon, unseen); the streak cap and `MayMark` bound how often in a row. (2) FailFast does not run the plan's PostPlan callbacks (as `failrepeat` / `unseenfbpre`). (3) The teleport recovery plans once more from the goal; that plan is not asked (outside `UfInner`), so it waits as today if a job still runs.

**Queue (prepared, not run):** `tools/harness/queues/h4-gvunseen.json` with pair `h4gvu1112`: smoke (IP 5 min, `--ab gvunseen`), `ip-gvunseen-defer-loop` (repro in `repros.json`: 30 townies unseen at the far lot plan home while the household's Go Here jobs hold `+0x918`, `M1Retry=0`, ABBA 10 firings per arm), `ip-gvunseen-natural-ab` (IP 10 min ABBA `--ab gvunseen`, `unseenfbplan` + `unseenfbteleport` on in both arms). Pass criteria: §12.3's (sum of keyed sim waits, household scope wait unchanged, hitch metrics), plus `gvuN > 0` and no household route in a `gvunseen:` line.
