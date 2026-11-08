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

## 9. Build order and repro plan (not built)

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
