# 247. What `EventQueue.GetNextEvent` hitch time is, and what the longest hitches are (v0.2.7)

Static triage, no game run, no build. Data: `v027-numbers-final-2026-11-05` (v0.2.7, ts3native 0.36.168, 2 runs of 10 min per world, 22:33-23:42 UTC), `hitches.jsonl`, `frames.csv`, `seconds.csv`, `freezes.jsonl`, ts3native logs. Baseline comparison: `v027-numbers-clean-2026-11-05-*-baseline-clean-*`. Run offsets (`+119 s`) count from the first `seconds.csv` row. Tags: **[verified]** code or disassembly read, **[measured]** from these runs, **[inferred]** reasoning, not proven. Ledger: docs/51 §437.

## 1. Result

Cause logging results of 2026-11-06 replace the open rows below: §11. Fix verify and the wildflower save proof: §12.

- **`EventQueue.GetNextEvent` costs nothing.** It is where a Sim task goes to sleep. The time is the run before that sleep. On IP 87% of it (49.2 of 56.5 s) is one run: after `GetInBoatRouteAction` fades the Sim in, `RoutingComponent.DoRoute` calls `Route.ReplanFromPoint` **synchronously on the sim thread** [measured, verified]. It is in-game stutter from boats, not an event backlog.
- **It is EA's path and it is not new.** Baseline has the same share (IP 35-37%). The async route job already built for it (`[RouteAsync] ForeignKinds=4`, docs/176) is off in the release ini, and `Kinds=0`, so all replans stay sync.
- **The longest hitches have different causes from the largest total.** Worst case by cause (§4): world lot alarm pass 956 ms, locomotion waits 390 ms, Sim runs ending at `AsyncWait` 383 ms, Overwatch Sim reset 439 ms, boat replans 334 ms. Only the boat replan is fully named by this data.
- **The 200 ms frames are not a timer, Sleep or present wait.** They are one service call: `CAS/SimService` takes 182-185 ms on the main thread [measured]. They do not line up with sim hitches, and they are in the Baseline too. Which build state takes the time is not decided (§5).

## 2. What the `to` and `from` fields mean [verified]

`tools/frida/stutter-profile.js` (CModule, docs/166): a run is timed from entry to exit of the task resume `0xD7F000`. `from` = the saved frames at resume, read on entry. `to` = the frames the yield save `0xE53620` just wrote, read at its return. So `ms` is the whole run **between** the two. Neither frame is where the time went: `from` is where the task slept, `to` is where it slept next. The field is 3 frames deep. The prior aggregate grouped by `to`, which is why a sleep point looked like a cost.

## 3. Q1: `EventQueue.GetNextEvent`

### 3.1 What it is [verified, decompiled scripts]

`ScriptCore.EventQueue.GetNextEvent` < `StateMachineClient.GetNextEvent` < `StateMachineClient.WaitNextEvent`. This is the per-state-machine (SACS animation) event queue, not `EventTracker`. `StateMachineClient.RequestState` (yield form) loops on `WaitNextEvent`; with an empty queue `GetNextEvent(wait)` stores `mSleepingTask` and calls `Simulator.Sleep(uint.MaxValue)`, and the native callback `OnNativeEvent` wakes it. The loop only reads event ids and updates filters. It runs no listeners: opportunity and achievement listeners (`EventTracker`) are not on this path. So no event type or listener runs under it, and the candidates in the brief (EventTracker backlog, listeners) are out. ts3fixes has no hook on `EventQueue` or `GetNextEvent`; its `StateMachineClient` hooks are unrelated (`BookCarry.cs`).

### 3.2 What the time really is [measured]

Iterations >= 100 ms, both runs, whole 10 min (all ms sums of the kept top 5 runs per iteration):

| world | iterations | wall ms | `to` = GetNextEvent ms | share of wall | `from` = GetNextEvent ms |
|---|---|---|---|---|---|
| BP | 281 | 37,256 | 1,709 | 4.6% | 1,234 |
| IP | 1,081 | 165,049 | 56,537 | 34.3% | 7,110 |
| SV | 166 | 25,384 | 1,185 | 4.7% | 536 |

IP runs that end in `GetNextEvent`, by where they resumed:

| `from` -> `to` | runs | ms | max ms |
|---|---|---|---|
| `FadeIn` < `GetInBoatRouteAction.PerformAction` < `DoRoute` -> GetNextEvent | 375 | 49,163 | 334 |
| `AsyncWait` -> GetNextEvent | 34 | 1,893 | 178 |
| GetNextEvent -> GetNextEvent | 48 | 2,352 | 190 |

All runs from `GetInBoatRouteAction` to GetNextEvent over any length (503, 57.1 s, also under 100 ms iterations): `route` 52.8 s of them, correlation run ms to `route` 0.98, the rest p50 8 ms, p90 13 ms. All route time is on the sim thread, `rtPlanWait` 0 because `[HitchAttr]` was off. `byEntry`: `Route_ReplanFromPosition` 609 calls 43.7 s, `Route_PlanFromPoint` 114 calls 9.0 s. Per run p50 99 ms, p90 189 ms, max 334 ms. About 25-28 such runs per minute.

### 3.3 Why this run, and why IP [verified code, measured counts]

`GetInBoatRouteAction.PerformAction` places the boat, fades the Sim out, puts it in the boat, `FadeIn(yield: true)`, sets `Route.Follower = boat.Proxy` and `BeginAsBoat`, and always returns `ContinueAndPopPathAndReplan`. `DoRoute` then calls `ReplanFromPoint(RoutingParent.PositionOnFloor)` and pushes the new actions. The task sleeps again at the first animation wait of the new route, which is the `to` frame.

The plan is sync because ts3fixes sees follower = boat, not the task's Sim, so `AsyncEligible` is false, and ts3native has `route async A10 off (Kinds=0, ForeignKinds=0)` (log line in every run). `route kind deadlines` shows `own ReplanFromPosition 0` and `A10 foreign 0` jobs.

IP only because only IP Sims ride boats in these saves: runs from `GetInBoatRouteAction` BP 8, SV 0, IP 552. World route time `seconds.csv`: IP 83-93 ms/s, BP 38-52, SV 13-17. Community reports point the same way (water taxi and houseboat routing in Isla Paradiso, player threads only, no fix that touches this call).

### 3.4 Existing coverage

- **Ours:** docs/151 ranked this source (S1, IP 62 ms/s) and inferred get-**out** (I-151-2); §3.2 shows get-**in** is the large part in this run. D1 `legreplanskip` hooks only `BoatFollowSubPathRouteAction` and `VehicleFollowSubPathRouteAction`, and requires the follower to stay the Sim, so it does not apply here. docs/176 built the foreign async job for exactly this shape. I-176-3 said "most likely the boat case"; this data confirms that task and call site.
- **NRaas:** GoHere's `RoutingComponentEx.DoRoute` copy keeps the same `ReplanFromPoint` call; the frames here show EA's `DoRoute`. No NRaas fix.
- **Web:** one search, player threads about houseboats and water taxis only. Nothing to log in docs/24.

## 4. Q2: longest sim-iteration hitches [measured]

Times are UTC. Stage ms from the hitch line; `route` is nested inside `threaded`. `loco` = locomotion. All were visible: the publish gap in `freezes.jsonl` equals the wall time within ~10 ms.

Nothing else was seen in those seconds: `gcForced` 0, longest GC mark 17 ms, `steps` 10, no save or harness step in the queue log. The lot columns of `seconds.csv` are 0 in every row of every run of this job, so they say nothing about lot loads.

### BP (max 967)

| # | run | UTC | +s | wall | stages | task (ms) `from` -> `to` |
|---|---|---|---|---|---|---|
| 1 | 1 | 22:36:50 | 119 | 967 | threaded 957 | `WorldLot` 956 `Lot.Simulate` -> `Lot.Simulate` |
| 2 | 2 | 22:47:32 | 61 | 511 | threaded 500 | `WorldLot` 494, same |
| 3 | 2 | 22:47:09 | 38 | 491 | threaded 478 | `WorldLot` 478, same |
| 4 | 2 | 22:48:33 | 122 | 396 | threaded 387 | `WorldLot` 381, same |
| 5 | 1 | 22:41:55 | 424 | 250 | loco 187, threaded 58, route 32 | `UpdateTrafficTask` 34 |
| 6 | 1 | 22:44:19 | 568 | 229 | loco 176, threaded 44 | Sim `DoInteraction` -> `AsyncWait` 30 |
| 7 | 1 | 22:40:03 | 312 | 197 | threaded 179, route 13 | `ScriptProxy` no frames 91, 52; one-shot -> `AgingState.Pre` 28 |
| 8 | 1 | 22:44:37 | 586 | 191 | threaded 117, loco 67 | Sim `DoInteraction` -> `AsyncWait` 48, 23 |
| 9 | 2 | 22:55:21 | 530 | 189 | loco 117, threaded 66 | Sim `WaitForExitReason` (`AcquireLane`) 46 |
| 10 | 2 | 22:53:12 | 401 | 186 | loco 157, threaded 19 | none over 4 |

### IP (max 427)

| # | run | UTC | +s | wall | stages | task (ms) `from` -> `to` |
|---|---|---|---|---|---|---|
| 1 | 1 | 23:06:57 | 528 | 427 | threaded 226, loco 196 | Sim `AsyncWait` -> `OnAboutToStartFollowingRoute` 222 |
| 2 | 1 | 23:00:33 | 144 | 418 | threaded 390, loco 24 | Sim `DoInteraction` -> `AsyncWait` 383 |
| 3 | 2 | 23:15:01 | 314 | 413 | loco 390, threaded 17 | none over 5 |
| 4 | 1 | 23:00:32 | 143 | 409 | threaded 295, loco 109 | Sim `DoInteraction` -> `AsyncWait` 209, 70 |
| 5 | 2 | 23:10:09 | 22 | 396 | loco 217, threaded 172 | Sim GetNextEvent -> `AsyncWait` 170 |
| 6 | 1 | 23:02:35 | 266 | 385 | threaded 273, loco 107, route 142 | Sim `AsyncWait` -> `OnAboutToStartFollowingRoute` 265 |
| 7 | 2 | 23:12:05 | 137 | 382 | threaded 355, loco 20 | Sim `DoInteraction` -> `AsyncWait` 292, 60 |
| 8 | 2 | 23:10:07 | 20 | 380 | threaded 349, loco 25 | Sim `DoInteraction` -> `AsyncWait` 258, 82 |
| 9 | 1 | 23:00:32 | 143 | 377 | threaded 355, loco 14 | Sim `DoInteraction` -> `AsyncWait` 303, 42 |
| 10 | 2 | 23:12:27 | 159 | 364 | threaded 358 | `ScriptProxy` no frames 346 |

### SV (max 557)

| # | run | UTC | +s | wall | stages | task (ms) `from` -> `to` |
|---|---|---|---|---|---|---|
| 1 | 1 | 23:26:16 | 285 | 557 | threaded 285, loco 267 | `SimUpdate.Simulate` 280 |
| 2 | 1 | 23:27:36 | 366 | 468 | taskObjects 440 | `OwResetHook.OnPerform` 439, resumed in NRaas `ResetSimTask`, ended |
| 3 | 2 | 23:33:24 | 16 | 454 | taskObjects 430 | same 427 |
| 4 | 1 | 23:24:00 | 150 | 336 | loco 323 | none over 3 |
| 5 | 2 | 23:38:14 | 305 | 315 | threaded 307 | one-shot (fresh) -> `AsyncWait` 303 |
| 6 | 1 | 23:26:07 | 277 | 312 | threaded 304 | `WorldLot` 298 |
| 7 | 2 | 23:36:47 | 218 | 288 | threaded 281 | Sim `FollowRouteAction.PerformRoute` -> `AsyncWait` 279 |
| 8 | 1 | 23:25:34 | 244 | 286 | threaded 160, loco 117 | Sim `AsyncWait` -> `OnAboutToStartFollowingRoute` 152 |
| 9 | 2 | 23:38:56 | 347 | 271 | threaded 139, loco 126 | same 133 |
| 10 | 2 | 23:37:38 | 270 | 264 | loco 245, threaded 13 | none over 3 |

**Same site between runs:** BP `WorldLot` at +119 and +122 (956, 381), and Baseline at +126 (895): a scheduled event at the same sim time [measured time, inferred alarm]. IP `DoInteraction` -> `AsyncWait` in both runs, in bursts of 2-3 back-to-back iterations (23:00:32-33, 23:10:07-09). SV `OwResetHook` once per run at different times.

### Ranked by worst-case ms

| rank | cause | worst | where | how many | status |
|---|---|---|---|---|---|
| 1 | **World lot task pass** (`Lot.Simulate`: world `AlarmManager.Simulate`, then door, detail and display updates) in one run, no route | 956 run | BP 967; IP 316, SV 298; IP +5..+12 s after load | >= 100 ms: BP 7, IP 23, SV 6 | site verified, **which alarm unknown**. Baseline BP 895. Not ours |
| 2 | **Locomotion stage** with no route call | 390 | IP 413, SV 336 and half of 557 | >= 200 ms: IP 13, SV 5; >= 100 ms: BP 9, IP 120, SV 37 | **named, §10.1:** `+0x918` wait at `0x85FE10`, `[SteerRO]` off in the release ini |
| 3 | **Overwatch stuck-Sim reset** resuming in `ResetSimTask` | 439 | SV 468, 454 | 2 of 2 runs | site measured, body unknown, candidates in §10.2. Baseline max 44 ms in 5 runs is not a fair compare (§10.2) |
| 4 | **Sim run ending at `AsyncWait`** with `route` ~0 (`DoInteraction`, `FollowRoute`, one-shot) | 383 | IP 418, SV 315 | `DoInteraction` >= 100 ms: IP 24, SV 6, BP 4 | **unnamed**, see below |
| 5 | **`ScriptProxy` run, no frames** (script invoke, not a resumed task) | 346 | IP 364; `: Dispose` form 279 | IP 5 | unnamed |
| 6 | **Boat get-in sync replan** | 334 | IP 341 | >= 100 ms: 245 runs, 37.4 s | named, §3 |
| 7 | **Run after a plan** (`AsyncWait` -> `OnAboutToStartFollowingRoute`) with `route` ~0 | 280 | IP 427 | IP 71 runs, 10.2 s | unnamed |
| 8 | **`SimUpdate.Simulate`** | 280 | SV 557 | 1 | see below |

**Locomotion.** The native stage rose with async routing: maximum per run Baseline BP 6, SV 5-30, IP 52-112; v0.2.7 BP 157-187, SV 245-323, IP 235-390 [measured]. That is the steering wait for `+0x918` while a job holds it (docs/96 §4.4, measured there with stacks). `[HitchAttr]` was off, so every `rt*` field is 0, but the per-key wait table proves it (§10.1). Longest own job per run 173-344 ms, 5-14 own jobs >= 200 ms in IP and SV. So this is the tail v0.2.7 trades for removing 1.5-2.3 s plans (Baseline max 781-2,321 ms against 427-967).

**`SimUpdate` 280 + loco 267 in one iteration (SV top).** The run's `route jobs sim waits` maximum is 278 ms, close to the 280 ms run, so a job wait is possible [inferred, one match]. The two parts add to one hitch, so it could be the same long job seen twice.

**Runs ending at `AsyncWait` with no route time.** The async plan's job is not in `route`, so the time is before START: script work, a lock wait, or both. IP sim waits max 182-198 ms, below the 383 ms run, so a job wait alone does not explain it [measured]. PLAN's open "1.1-1.3 s Sim stall ending at `AsyncWait`" looks like the same family [inferred]. Three frames are not enough to name it.

**Share by largest part,** iterations >= 200 ms: BP 6 (world lot 4, loco 2); IP 186 (route 62, loco 33, world lot 10, mixed 81); SV 27 (loco 9, world lot 2, mixed 16).

## 5. Longest frames [measured unless tagged]

| world | frames >= 200 ms per run | run p99.9 frame ms |
|---|---|---|
| BP | 1, 0 | 75, 70 |
| IP | 24, 31 | 56, 200 |
| SV | 73, 65 | 206, 204 |

- **Where the time is.** In every >= 150 ms frame on IP and SV, `svcMs` is the whole excess (median 189): the `svc1` slot is `CAS/SimService`, median 184 ms. Idle ~10.7, draw ~2.3, present 0.12, begin 0.5 ms are normal. BP's five long frames are mixed (`World/Service` 85-140, one `CAS/SimService` 202).
- **Very narrow.** In SV 182-185 ms holds most samples (run 1: 62 of 76 stalls in 181-185 ms). Nothing sits between ~90 and ~165 ms. Frame total = 184 + the normal frame = the observed 200-205 ms. A few sit at 195-214 ms.
- **Not sim hitches.** Overlap with a sim iteration >= 50 ms: stalls 7-29%, random frames 8-26%. Same for a `SimBuilderTask`/`SetOutfit` run in the 3 s before: stalls 7-21%, random 18-25%. Never two in a row. Gap median 4-17 s; steady per minute. `freezes.jsonl` overlaps them because the main thread's stall delays publishes.
- **Not new, not ours.** Baseline: SV 79-103, IP 10-26, BP 0-4 per run. `baseline-true` SV 86. Seen back to 2026-10-22 profiles. ts3native has no hook in SimService update (the only near code is the crash guard at `0x5EE1E0`). The D3D9On12 runs were BP only (0-3 per run), so they say nothing on DXVK.
- **No fixed wait found.** `SimService_Update 0x5F0E50` [verified]: no Sleep and no timed wait in the body; a QPC deadline that only ends the loop between steps, and `[+0x139]` 0 means no deadline. In the direct-call closure (depth 6, 1,057 functions) the only blocking primitive is `SimOutfit_LoadParts 0x5E84D0` -> `JobManager_Wait` -> `EASemaphore_Wait 0x4E2760`. Vtable calls are **not** in that closure, so D3D calls are not excluded. ts3native's only `Sleep(200)` outside tests is `saveslim.cpp` save retries, not in play.
- **Which state (weak).** Old main-thread stacks (5 Hz, 2026-10-15/16/22, BP and IP, not aligned to these stalls, 643 samples inside the update): 77% below state 4 `0x5EC6D0` (loader step: `0x5D9DC0`, `0x5DA0C0`, in the model builder range), 8% state 7 `0x5F06B0`; 71% game code, 24% OS wait leaf, 5% driver [measured, but not of the 184 ms stall itself].

**Verdict:** a fixed-size step of one outfit or model build on the main thread, or a wait inside it [inferred]. The tightness argues for fixed work or a fixed wait, and this data cannot tell which. It is EA code path, present without our build, and a separate problem from the sim hitches: a 200 ms camera and render stall roughly every 5-15 s on SV.

## 6. Fix design (not built): boat replan

1. **Turn on what exists:** `[RouteAsync] ForeignKinds=4`. The Sim's task sleeps in `AsyncWait` while the worker plans; the Sim is in the boat and faded in, the action is between route actions, so nothing moves it [inferred, I-247-4]. ts3native refuses if the route is followed (`afActive`) and falls back to sync.
2. **Gain if the wait is not replaced:** subtracting `ReplanFromPosition` time from each hitch, IP iterations >= 100 ms 1,081 -> 666, >= 200 ms 186 -> 125, about 36 ms/s off the sim thread [measured subtraction, gain inferred]. No effect on BP or SV.
3. **Risk:** the bundle with ForeignKinds was worse in `ip-locktaker-defer-ab` (10 -> 16 freezes, max 929 -> 3,549, docs/release-notes/v0.1.5-dev.md), but ForeignKinds alone was never measured there. A 100-330 ms job holds `+0x918`, so §4 rank 2 would grow: this is the moved-waits case. Read `locomotion` and `rtLock` with it.
4. **Not valid:** extending `legreplanskip` to get-in. The follower changes to the boat here, so D1's equivalence does not hold.
5. **Order:** verify first with `ip-foreign-replan-verify` (`tools/harness/queues/foreign-replan.json`, exists), then a natural IP pair with the job off and on. Judge by hitch counts, p99.9 and max.

## 7. Logging to decide the rest

Built: §9. The `alarmtime` threshold is 20 ms, not 50.

No build is needed for the first three.

| open question | what to run | reads |
|---|---|---|
| Locomotion, `AsyncWait` runs: job wait or work | `[HitchAttr] Enabled=1` (off in the numbers job; `rt*`, `rtMaxKey`), 1 run per world | `hitch-attr-report.ts` |
| World lot, `AsyncWait` start, `ScriptProxy` runs: which script | `profile-world.ts` without `--no-samplers` (the numbers job logged `managed sampler 0 s`, `simstack` empty): correctness run only, samplers pause the sim thread. BP is enough for world lot: the event is at ~+120 s and the managed window starts ~57 s after the first row (`worst-hitches.ts`) | `worst-hitches.ts` |
| 184 ms stall | SV, `--no-samplers --mainstack` (main-thread stacks for the whole window; the main thread is suspended per sample, so frame cause only) | stacks in frames >= 180 ms |
| World lot, exact alarm | new ts3fixes switch `alarmtime` (off, observe): one line for each alarm callback >= 50 ms, method name, ms, sim time, in `AlarmManager.Simulate`. Only if samplers do not catch it | ts3fixes log |
| 184 ms stall, exact state | new `frame-times.js` option `svcsplit`: time `0x5EEFB0`, `0x5EC550`, `0x5EBAE0`, `0x5EC6D0`, `0x5DE400`, `0x5D2750`, `0x5F06B0`, `0x5DC800`, `0x5EBC50`, `0x5FE1A0` and `EASemaphore_Wait 0x4E2760` inside `SimService_Update`, write the top one per frame. Only if main stacks do not decide | frames.csv |
| Longer task stacks | `stutter-profile.js` keeps 3 frames for `from`/`to`; `frames()` could keep 6 at the cost of more names per hitch line | hitches.jsonl |

## 8. Not verified

- Why `ReplanFromPoint` from a boat costs ~100-300 ms here (water or world A\*, distance): not split in this data.
- Whether `afActive` refuses the boat replan (I-176-5). The static read says the route is not followed at that point; no run has shown it.
- All ms sums are the kept top 5 runs per iteration (`taskDropped` not added back), so shares are lower bounds.
- `overwatchstucknotice` wraps the reset, but what took 430 ms inside it was not seen. Stuck Sims stay log-only (no research).

## 9. Cause logging (built, not run)

Branch `feature/hitch-cause-logging`, pair `*.hitchlog2` (ts3native 0.36.175), queue `tools/harness/queues/hitch-cause.json`. All off by default: the hooks need the tuning `kHitchCauseHooks` (False in every release package; True only in `ts3fixes.package.hitchlog2`), then the switches `alarmtime`, `boatreplanlog`, `owresettime`. Read a run with `node tools/harness/hitch-cause-report.ts <ts3native.log> [--frames <frames.csv>]`. Lines go through `DiagLine` to `ts3native.log`, 120 per minute (`hitchcause_capped` counts the rest). Counters 826-835, `patches` names `alarmtime_*`, `boatreplan_*`, `owreset_*`, `hitchcause_capped`.

| target | switch or option | line or column | how |
|---|---|---|---|
| World lot pass | `alarmtime` | `alarmtime:` per alarm >= 20 ms: method, target, task, ms, sim time, `ended=run\|yield`. `lotpass:` per pass: alarms, top alarm, door, detail, `rest<=` | no swap of `AlarmManager.Simulate`. `AlarmManager.Simulate` reads `Timer.YieldRequired` once on each due alarm just before the callback, so `Timer.get_YieldRequired` is the stamp (timers not due yet, read by the reset paths, are skipped): the next stamp closes the alarm before. The first stamp, `PriorityQueue.RemoveAt`, timed 0 alarms on `bp-hitch-cause` (§9.1). `Lot.UpdateDoorPrivacy` and `Lot.UpdateDetailPriority` stamp the rest of the pass |
| Boat replan | `boatreplanlog` | `boatreplan:` ms, sync or async, result, caller, previous route action, Sim + `UfWhy`, boat seen, class seen or unseen, job could or no, why, route length, paths, distance, lot, goal lot. The sync plan line before it holds phases, planlocked retries, loops and water grid | leaves `RouteSrcBegin` and `RflPlan` of the existing plan hooks; `GetOption2` query `kWouldHi` asks ts3native what a foreign job would do, with no side effect |
| Overwatch reset | `owresettime` | `owreset: OnPerform` whole ms, `after` = time after `ResetSimTask.Perform` (the notice), `mSuffix`, species, age, `reset no` with `safe` > 0 = `AttemptToPutInSafeLocation` took the path with no reset. `owreset: Perform` ms, tail, `create` (`CreateObjectWithOverrides` calls, ms, null results), `retry` (`FixInvisibleTask.Perform`, reached only after a null first result), `safe`, `restore` (`CreationProtection.Dispose`). `owreset: InstantiateAtHome` ms, species, age, tries | hooks with stubs. `OnPerform`: `HcOnPerformHook` replaces `OwResetHook` only with `kHitchCauseHooks`, so the release hook's IL is unchanged (docs/242); no stuck notice sorting under it |
| Locomotion with no route | none new | `[HitchAttr] Enabled=1 Holders=1`, `rt*`; `blockMaxUs_i` in `*-native.json` | existing switches cover `+0x918`; export `ts3native_blockmax[32]` holds the longest wait per `blockKey` slot, read by `agent.js` |
| `AsyncWait`, `ScriptProxy` runs | none new | hitches.jsonl: 8 tasks per iteration, `fromMore`, `toMore` frames 4-6, `entry` for runs with no frames | `stutter-profile.js` |
| 184 ms frames | `--svcsplit`, job `profile.svcSplit` | frames.csv `svcTop,svcTopMs` | 11 calls of `SimService_Update`, §7 list |

Swap audit: `Timer.get_YieldRequired`, `Lot.UpdateDoorPrivacy`, `Lot.UpdateDetailPriority` do not yield [verified: `swap-stack-audit.ts --recompute`]. `AlarmManager.Simulate` is not swapped. `GlobalFunctions.CreateObjectWithOverrides` does not yield [verified: same]; patched at startup, because the first world load would compile it. `ResetSimTask.Perform`, `FixInvisibleTask.InstantiateAtHome` and `Perform`, `Instantiation.AttemptToPutInSafeLocation`, `CreationProtection.Dispose` and `ResetTask.OnPerform` are mod targets with stubs. `Lot.UpdateNumZombiesOnLot` was dropped as a stamp: it reaches `Simulator.Sleep`. So nothing stamps the display level and `UpdateReactions` (which yields): `rest<=` runs to the next stamp of any lot, an upper bound that can hold other tasks' runs.

### 9.1 Why the first `alarmtime` stamp timed nothing (2026-11-06)

`bp-hitch-cause` (pair hitchlog2) stopped at the 120 s gate with `alarmtime_timed` 0 and no `alarmtime:` or `lotpass:` line. The `tuned:` and refused-patch lines were lost: they live in AppDomain data `ts3fixes.applied`, on no file, and the job row only kept them on autshadow jobs (queue.ts now keeps the read after load in `row.fixesApplied`).

| Finding | Tag |
|---|---|
| The IL path is right: `AlarmManager.Simulate` calls `Sims3.Collections.PriorityQueue.RemoveAt(int)`, no overload, not generic, and the only other callers are `InternalRemoveAlarm` and `InternalFixLoadAlarms`. `runtime_invoke` is not on this path: `Lot.Simulate` to `AlarmManager.Simulate` to `RemoveAt` are plain mint calls | verified (decompiled) |
| `RemoveAt` is one of EA's 1,085 binder bodies (`0xE0C640`, `docs/data/binder-bodies.csv` row 239): the game runs native code, not this IL, unless the swap changes the binder key. The key is built from the method's class, signature and name when the `RuntimeMethod` is first used; `Fixes.SwapMethod` swaps class and signature, so a swap before that misses the table | verified (disassembly `0xD9A7B0`, `0xD9A530`, `Fixes.cs`) |
| `RemoveAt` returns `object`, `HcQueueHook.RemoveAtHook` returned `void`: a header swap gives callers the hook's signature, so the caller's `pop` had nothing to pop. A hook that ran would have left the eval stack one short | verified (decompiled, `Fixes.cs`); effect in game not observed |
| `Fixes.Compiled` refuses a target whose `MonoMethod+0x14` is set; `0xE51F20` sets it when the first caller is transformed, not only the target. One run of `InternalRemoveAlarm` before the patch would refuse `RemoveAt` | verified (`0xE51F20`) |
| A hook that was in would have counted alarms, so the patch did not take effect; no `lotpass:` line fits the old `&&` chain stopping at its first link, which left the door and detail stamps out too. Which of refused or bound applied is not known | inferred, the log is gone |
| Mint inlining and `[MonoFast]` / `[MintFuse]` are not the cause: neither section is on in this job, and inlining reads the swapped body | verified (job ini line, docs/139) |

Fix: stamp `Timer.get_YieldRequired`: IL only (not in the binder table), `bool` hook for a `bool` target, no yield, called only from `AlarmManager.Simulate` and two reset paths. The three lot and alarm patches now go in independently. Other swaps on binder methods: `ClassDBHook` and `TypeCodeHook` (`cachedreflection`, off by default).

An alarm that sleeps is closed by the next stamp of another task (`ended=yield`): its ms run to that stamp and work after the sleep is not in it. Delegates are not followed by the audit, so "does not yield" means no direct path.

**Which path sees the boat replan [verified: code, `managed-callgraph.ts`]:** `DoRoute` itself is not hooked. After `ContinueAndPopPathAndReplan` it calls `Route.ReplanFromPoint`, so the plan reaches `RouteBackoffHook.ReplanFromPointHook`, `RouteSrcBegin`, then `UfPlan(3, p)`. `UfPlanBody` takes kind 0 in `unseenfbpre`, and `proxy.Target as Sim` is null because `Route.Follower` is the boat by then, so `[UnseenFallback]` returns false before `UfWhy`. `UfCarOrBoat` and `UfTeleport` (kind 0, own task) refuse it as well. Next comes `ReplanFromPointAsync`: `AsyncEligible` is false, the foreign path runs only with `ForeignKinds=4`.

**Where a teleport could be gated [inferred, not built]:** `GetInBoatRouteAction.PerformAction`, `Sim.GetBoatForGetInBoatRouteAction`, `RoutingComponent.PushActionsForRoute` and `GetRouteActionForSubPath` all reach a yield: no swap there. `GetInBoatRouteAction..ctor` does not yield, but it can only mark the route. No new swap is needed: the plan hook path already holds the Sim, the route and the classifier, so a branch for kind 3 with a boat follower at the top of `UfPlanBody`, or in the leaf `RouteSrcBegin`, sees exactly this replan after the Sim is in the boat. Changing the IL of a hook that can sit on a saved stack refuses that frame once on the first load after the update (docs/242), so a leaf is the safer place. The boat ride itself still has to be skipped; that is a design question for the teleport work.

- What took 430 ms inside the reset was not seen (§10.2). `overwatchstucknotice` is off in this build, so `OwResetHook` is a pass-through and only names the task. Stuck Sims stay log-only (no research).

## 10. The two regressions (static, existing runs, no game run)

Rule: our build slower than Baseline is ours until proven. Tags as above. Ledger: docs/51 §439.

### 10.1 Locomotion stage: the release ini has `[SteerRO]` off

**Cause [measured].** `v027-numbers-final` is the real release ini, and that ini has the whole read-only stack off: `[SteerRO]`, `[AvoidRO]`, `[Gate0RO]`, `[LotFrontDoorRO]`, `[ObstructedRO]`, `[RouteToObjRO]`, `[PlacementRO]`, `[GoalValidRO]`, `[IdleCheckRO]`, `[SlotEnabledRO]`, H2 / LocoWindow, TailUnlock, ReplanDedupe, DeferFootprint2, DestroyPark. So async routing runs, and `Locomotion_ObstacleCheck` (wait key `0x85FE10`) waits on `+0x918` for the job. The same asi 0.36.168 with the master ini (`v027-numbers-clean`, `-clean-rerun`, and the `v025-rel` arms, which ran that asi too) has them on, and the wait is gone. `cbc92bb0` (#272) turned them on after v0.2.7 was cut.

Natural pairs, same asi and seed, 10 min:

| world | ini | locomotion max ms | >= 50 ms | >= 100 ms | >= 200 ms | sum of >= 50 ms | key `0x85FE10` waits / ms |
|---|---|---|---|---|---|---|---|
| BP | release | 187, 157 | 39, 45 | 6, 3 | 0, 0 | 2,854, 2,968 | 1,007 / 10,959, 1,172 / 11,705 |
| BP | master | 44, 121, 54 | 0, 2, 1 | 0, 1, 0 | 0 | 179 (middle run) | 9 / 251, 17 / 187 |
| IP | release | 235, 390 | 162, 141 | 57, 63 | 4, 9 | 16,232, 15,558 | 1,111 / 22,214, 1,031 / 18,167 |
| IP | master | 232 | 10 | 8 | 3 | 1,472 | 19 / 1,500 |
| SV | release | 323, 245 | 87, 67 | 20, 17 | 4, 1 | 7,878, 5,958 | 1,589 / 18,732, 1,372 / 15,569 |
| SV | master | 59, 57 | 1, 1 | 0 | 0 | small | 31 / 273, 36 / 412 |
| all | Baseline | 5-121 | 0-2 | | | | no job |

The wait table does not need `[HitchAttr]`: it is the `blockKey_i` / `blockN_i` / `blockUs_i` slots in `*-native.json`, cumulative per run, with no per-key maximum.

**This is a moved wait only in part [measured].** Total lock pump time falls on the master ini: BP 43.7, 43.2 -> 35.3, 31.3 s, IP 48.1, 47.0 -> 33.1 s, SV 34.7, 33.9 -> 23.8, 24.6 s. Some keys rise: `0x83156D` 40-790 ms -> 3.1-11.4 s, `0x62FB74` 2.5 -> 5-6.2 s, `0x2000003` 1.6 -> 3.2-4.2 s, disc 0 -> 3.1-4.4 s. Those are other sites (mostly script task time). Wall hitches moved little, so the gain is in the locomotion stage, not yet a clear win in every wall count: >= 100 ms release BP 167, 114, IP 554, 527, SV 84, 82; master BP 150, 149, IP 540, SV 69, 70. >= 200 ms release 3, 3 / 95, 91 / 11, 16; master 4, 4 / 83 / 9, 16; Baseline IP ~352-419, SV 22-29.

**Left on the master ini:** IP locomotion 232-248 ms, 3 hitches >= 200 ms. Candidate `0x85F6CB`, the second `[SteerRO]` site (IP 120 waits / 2,240 ms) [inferred].

**Fix: no code.** Master already has the ini; the next release from master carries it. v0.2.8 does not: it was cut from the v0.2.7 line (`6fcfccfe`) and has these switches off, so it keeps the regression.

**Do not turn `[RouteToObjRO]` on in the v0.2.7 or v0.2.8 asi.** `ip-v027-clean-2`, the one crash on the master ini, is that clone: `ACCESS_VIOLATION reading 0x2C0` at asi+0x77D42, `mov eax,[eax+0x2C0]` with `eax` 0 [measured registers]. The same bytes resolve to `RtoPhase1` in the newer maps, `in.simList = sim + 0x2C0` with no Sim [verified]. PR #257 (`d4705ecc`) fixed this; neither 0.36.168 nor v0.2.8's commit contains it, HEAD does. One in 132 logs since 11-04. `SteerRO` alone is not touched by this bug, so a hotfix ini could take that switch alone [inferred, not run].

**Dropped 2026-11-06 (Rodney, timing trim): `[SteerRO]` is on in master and v0.2.8, `v028-smoke` proved waits 10-20, so the jobs are out of `hitch-cause.json`. The run as planned (`ip-steerro-*`, `sv-steerro-*`, ABBA, never run):** current master, one natural pair per IP and SV with `[SteerRO] Enabled=1` against `0` (the rest of the master ini on both arms). Read the locomotion column, `0x85FE10`, and wall hitch counts. Only the key is proven, not which switch alone removes the stage.

`SimUpdate.Simulate` 280 ms in the §4 SV top hitch fits an `RtWaitJob` drain: `final-1` sim wait `rtwait` max 278 ms [inferred, one match].

### 10.2 Overwatch reset: fixed step of 400-550 ms, SV, not explained

**The comparison [measured].** Task name `OwResetHook.OnPerform` comes from our hook on `StuckCheck.ResetTask.OnPerform`, but `overwatchstucknotice` defaults off, so `PerformHook` only calls the original (`OverwatchNotice.cs` line 77). The name is an artefact: the same run is `ResetTask.OnPerform` without our hook.

The run measured is the tail after `Common.Sleep()` in NRaas `ResetSimTask.Perform`, up to the end of the task.

| arm | runs | >= 100 ms | max ms |
|---|---|---|---|
| Baseline, reset tail as `ResetTask.OnPerform` | 203 | 0 | 49 |
| Baseline, `ResetSimTask.OnPerform` | 8 | 0 | 50 |
| Baseline, `NRaas.Overwatch.OnTimer` -> end from `ResetSimTask` (other cleanups in the same run, docs/145 §10) | 13 | 13 | 602 |
| v0.2.7 release ini | 71 | 2 | 439 |
| master ini, `OwResetHook` | 145 | 0 | - |
| master ini, `ResetSimTask.OnPerform` | 7 | 4 | 507 |
| all our history, `OwResetHook` | 7,572 | 27 | 629 |
| all our history, `ResetTask` | 2,160 | 3 | 1,025 |
| all our history, `ResetSimTask` | 423 | 28 | 512 |

So the plain Baseline reset tail has never reached 100 ms in these runs, but the same frames do appear in a larger Baseline task, where the time cannot be split. Whether it is ours is open; by the rule it stays ours until shown otherwise. It is not new in v0.2.7: it is seen since 10-23, before `[UnseenFallback]`, and with the master ini too (`sv-imp1105-allon` 538).

**Shape [measured].** Reset runs >= 70 ms are bimodal: 50-100 ms 30, 100-150 15, 150-200 14, 200+ 3, then a gap, 400-450 14, 450-500 11, 500-550 5, 600 1, 1,000 1. The >= 300 ms runs are SV-specific: SV 18 of 1,969, BP 2 of 5,614, IP 1 of 2,071. In the big runs: 1 route of 1.2-1.6 ms, GC ~0, no long main-thread frame in the same time (`final-2`), no route job long enough (`final-2` longest wait 110 ms, job 279 ms). So it is neither route planning nor the 184 ms `SimService` stall. The only stack we have of a reset run is the small mode (133 ms, in `Lock_Acquire`).

**Correlate [measured, weak].** 17 of 19 SV big resets are followed within ~1 s by a `UI/ThumbnailManager` service spike of 15-25 ms, against 26 of 346 small SV resets. But a thumbnail spike alone is not it: 15 of 85 such resets in our SV runs are big, 0 of 16 in Baseline SV (p ~4%). A notice portrait is not the simple link either: `HudModel.GetThumbnailForGameObject` gives an invalid key for the destroyed Sim [verified].

Managed samples under `InstantiateAtHome` (145 in all history, none in a big run): 107 in `Route.PlanFromPoint` by way of `IsPointInLotSafelyRoutable`, 16 `CreateObject`, 7 `FindGoodLocation`, 0 in `FixInvisibleTask.Perform` or `SimBuilder`. No sample hits a big run, so that says only what the small mode is. `AgingState.PreInstantiateSim` waits with `Simulator.Sleep(0)`, so a deferred age-up outfit build would yield and end the run: not this [verified decompile].

No ts3fixes hook found on `Instantiate`, `StyledNotification` or `kSimInstantiated` [verified grep].

**Ranked candidates, none proven:**

| # | candidate | what proves it |
|---|---|---|
| 1 | Fixed ~450 ms step for one kind of SV Sim after the sleep: `CreateObjectWithOverrides` for a heavy Sim or species, or the `CreationProtection` restore | per-step ms with name and species in `InstantiateAtHome` |
| 2 | `Common.Notify` after `ResetSimTask.Perform`: `Localize`, `StyledNotification`, thumbnail | whole `OnPerform` ms with the `Perform` ms and suffix: the difference is the notice |
| 3 | `InstantiateAtHome` second try through `FixInvisibleTask.Perform` (outfit rebuild) | called twice, or a null first result |
| 4 | Two jobs back to back on `+0x918`: only for the 100-200 ms mode. With release-ini `DeferFootprint2` and `DestroyPark` off, more likely there | `rtLock`, `rtMaxKey` with `[HitchAttr]` on |

**Were missing from the `feature/hitch-cause-logging` fields (`owresettime`); all built, §9, not run:**

1. `owreset: notice sort` runs only inside `OwNoticeSort`, so never with `overwatchstucknotice` off. Time of the notice after `ResetSimTask.Perform` is not logged. It needs the whole `OnPerform` ms, logged before line 77's early return.
2. `mSuffix`, species and age, and whether `AttemptToPutInSafeLocation` took the path with no reset.
3. Inside `InstantiateAtHome`: `CreateObject`, the safe-location plan, the retry, the `CreationProtection` dispose.
4. `[HitchAttr] Enabled=1` in `hitch-cause.json` (not checked), so that `rt*` and `rtMaxKey` exist.
5. A per-key maximum beside `blockUs_i`, so a single wait can be told from many.

**The game run that decides it:** `hitch-cause` on SV with those fields. Two runs caught a big reset in 2 of 2 on this save. No fix built: nothing is proven to fix.

## 11. Results of the cause logging (2026-11-06, pair `hitchlog3`, ts3native 0.36.176)

Runs: `hitch-cause-2026-11-06` `bp-hitch-cause-r3` (samplers on), `ip-hitch-cause`, `sv-hitch-cause` (`--mainstack --svcsplit`), `ip-foreign-replan-verify`, all PASS; `verify-shortcut-boat-2026-11-06-ip-uboat-verify-r2` (20 min, FAIL on `unseenboat_shortcuts` 3 < 5). Attribution jobs, no quiet window: ms/s are for ranking, not release numbers. Static read, no game run, no build.

### 11.1 What limits this data

- **Most lines are lost [verified].** `userdiag.cpp` writes only the first 400 `ts3fixes:` lines of a run, and `ufplan:` (149-200) and `play:` (150) use them up. `hitchcause_capped` stays 0, because it only counts the 120-per-minute cap. So counters are whole-run, lines are the start of the run:

| lines | BP written / in log | IP | SV |
|---|---|---|---|
| `alarmtime:` | 32 / 14 | 74 / 13 | 44 / 8 |
| `boatreplan:` | 0 | 300 / 58 | 1 / 0 |
| `owreset:` | 35 / 6 | 26 / 0 | 32 / 2 |

- **`class seen` on every `boatreplan:` line is a logging bug [verified].** A Sim in a boat gets `UfWhy` = 7 (`kHeld`), which is returned only after every camera test passed. `HcBoatBegin` tests `SimWhy == 0`; `UbCore.Unseen` accepts `kHeld`, the log does not. So `boatreplan_seen_ms` is the total, not the seen part. `townieUnseen=0` on all lines is also not a view test: the Sim is not in `Standing`.

### 11.2 World lot pass: EA's spring wildflower alarm

| world | hitch wall | `lotpass:` | top alarm |
|---|---|---|---|
| BP | 937 | 923 ms, alarms 8, door 0, detail 0, rest<= 11 | `Wildflower.OnGrowWildflowersOnLot` 923 |
| BP | 563 | 535 ms, alarms 13 | same 519 |
| BP | 556 | 527 ms, alarms 8 | same 527 |
| IP | 295 | 256 ms, alarms 1 | same 256 |
| verify job | - | 204, 183 ms | same |
| SV | - | 176 ms, alarms 9 | `StrayPets.StrayPetHelper` 171 |

- **Named [measured].** The 956 ms of §4 is this alarm: one `Lot.Simulate` pass, `ended=run`, no route, no lock. Every wildflower line sums to 2,152 ms on BP, 3.6 ms/s, only while the spring drain runs.
- **EA's, not ours and not a mod's [verified].** The line says `Ts3Fixes.Fixes+WildflowerTaskHook` because the hook's header is swapped in; with `wildflowertask` and `wildflowerprobe` off it only calls the original (`ScriptHotSpots.cs`). Baseline has it too (895 ms).
- **`AlarmOneShot` limit not hit [measured].** The alarm runs inline in `WorldLot` (`task WorldLot`, `ended=run`). `AlarmOneShot` is only the task of a yielding alarm, it has no limit, and `AlarmManager.Simulate` has no count limit either (docs/04a).
- **Fix exists:** `kWildflowerTask` (docs/110 W1), on since §11.10, one try per sim tick, slices up to ~65 ms. Its only live run is `bp-ab-wildflowertask` (2026-10-18, PASS), which measured nothing (docs/110 §13), and CHANGELOG still says not tested live.
- **Next alarms:** `ObjectSpawner.TriggerSpawn` (`SeaLifeSpawner` 133, 117, 103 on IP, `InsectSpawner` 99 on BP), `Plant.EvaluatePlantGrowth` 124. Later `WorldLot` hitches (BP 161, SV 219, 202) are past the line cut, unnamed.

### 11.3 Boats

| | value | tag |
|---|---|---|
| Boat replans, IP, 10 min | 300, 27,323 ms = **45.5 ms/s**, mean 91 ms | measured, counters |
| Get-in runs (top 5 per iteration) | 297 runs, 36.1 s, 158 >= 100 ms, max 382 | measured |
| Unseen share | 585 of 591 get-in replans (`unseenboat_unseen` / `gate`, 20 min) | measured |
| Boat out of view in the 58 lines | 58 of 58; goal unseen too 51 (4,678 of 4,692 ms) | measured |
| Caller, 58 lines | `DoRoute` 48, 4,672 ms, max 302; `fp.houseboatfix ... EnterHouseboatThroughGangway` (a mod) 10, 20 ms | measured |
| Boat kind, 58 lines (ms) | Sail 1,119, Speed 1,009, SpeedFishing 905, WaterScooter 793, Taxi 504, Paddle 362 | measured |

Natural `verify-shortcut-boat` run: 572 candidates = shortcuts 3 + `rj_route` 544 + `rj_sim` 9 + `released_seen` 9 + `rj_owned` 6 + `rj_goal` 1. `rj_unknown` 223 are trips that stayed exact before classification.

`unseenboat_log` keeps 20 lines per reason and no ms, so the split of 544 is not known and ms per reason cannot be read. Time follows count: 95% of candidates are in `rj_route` [inferred]. In the first 57 log lines, before any reason filled up: `PlanRouteIntoLot` 19, colours unknown (start unknown) 18, `RouteToDoor` 9, `PlanToLotEx` 7, `PlanToMailbox` 2, swimming or raised 1. By interaction over all samples: Go Home first, then lot visits, Eat Out, Meet Up.

**Cause of `rj_route`: `UbCore.Follows` does not see through `*Orig` frames [verified code and stacks].** `UbFollows` skips pass-through frames only before the first frame. A trip that plans through a Lot helper gives `[Lot.PlanRouteIntoLot, GoHome.RunOrig, Fixes+GoHomeHook.Run]`, and `Follows` tests only the frame right after the helpers: `GoHome.RunOrig`, not in `kFollowers`. Live stacks show that shape: `Sims3.Gameplay.Core.GoHome.RunOrig < GoHomeHook.Run`, `...VisitCommunityLotLabLecture.RunOrig < VclSubHook`. The selftest passes because its input leaves out the `Orig` frame. So hooked `GoHome`, `VisitCommunityLot`, `GoToLot` and `GoToCommunityLot` are refused; `RabbitHole.RouteOutside` has no hook and passes, which fits the samples [inferred: the 3 shortcuts were of that kind]. `VclSubHook*` here is LynMods `VisitCommunityLotLabLecture` (I-439-8).

**Would widening be safe, what it catches:**

| change | safe for unseen Sims | catches |
|---|---|---|
| Skip `*Orig` in `Follows` (bug fix, no new caller) | yes, the bodies were read for docs/248 | Go Home, lot visits, most of the Lot helper samples |
| Drop the start height test for a real swim or posture test | yes: start height is not used, the Sim is placed elsewhere [inferred]. Samples repeat dry spots, room 0: Hobart's Hideaway, Accolades, Seaside Stuff | 20+ samples, mostly Go Home |
| `VisitLot` (add `VisitLotHook.Run`) | yes with private `mFollowers` read for `UbAlone` and `UfVerdict` on the goal; `Run` plans with `doRoute: false`, then `DoRouteWithFollowers`, and checks `LotCurrent` after [verified] | residential visits, 6+ samples |
| Plain Go Here | yes for a townie by the 10-27 rule, but `UfPreSim` denies "GoHere" and no natural sample has it; `crowd_go` repro legs need it exact. Leave | ~0 natural |
| `GoHereWithHoldingInteraction` (Meet Up) | no: runs under a `GoHereWithSituation` with participants | 9 samples, stay exact |
| `RouteToDoor` as an allowed caller | no: `doRoute` false only hands the route back; the frame above must decide. The `Orig` fix gives that | - |
| Start on a houseboat (colours unknown) | Rodney's call: docs/248 §4 keeps it exact | 18 of the first 57 lines, `rj_unknown` 223 |

**Gain [inferred].** Of the 28 candidates that passed the caller gate, 3 made it (`released_seen` 9, `rj_sim` 9, `rj_owned` 6, `rj_goal` 1). Those were rabbit-hole trips, so 11% is a floor. At 11-40% of candidates, about 5-18 ms/s on IP, worst 382 gone for those trips. `rj_owned` sits after the caller gate, so its true share only shows after this fix. NRaas: GoHere `mAllowBoatRouting` turns boats off, its `DoRoute` copies keep the replan; no teleport for boats. Nothing covers this.

### 11.4 Overwatch reset: step split not read

- **No big reset is in the log [measured].** Written 35 / 26 / 32 lines, kept 6 / 0 / 2. Kept lines are the small mode: `Perform` 63-110 ms with sleeps, tail 2-4 ms, create 8-27 ms, safe 4-7, restore 0-3, `tries 1`, result True.
- **Big runs tonight:** IP +180 s 479 ms, IP 150, BP 174, SV none (max 33). All resume at `Common.Sleep` in `ResetSimTask.PerformOrig`, under `HcResetHook.Perform`; IP 479: `rtLock` 0, GC 1.5, route 1. Not route, lock or GC, as §10.2.
- **New:** the big mode is not SV only; one in IP, none in SV.
- **`Baseline 44` is not a fair compare [inferred].** Besides the task names of §10.2, big runs are 21 of 9,654 resets (0.22%), so 203 Baseline resets expect 0.44, and none has a 64% chance. It says nothing either way.

### 11.5 `CAS/SimService` 182-185 ms: named, it is EA's triangle sort, Apex has the fix

- **`--svcsplit` names it [measured].** 72 of 74 frames >= 150 ms on SV have `svcTop` `s5F06B0`, 181-208 ms, p50 185. Whole run 13.9 s = 23 ms/s on the main thread. The other two: `UI/ThumbnailManager`, `World/Service`.
- **Inside it [measured].** Of 74 main-thread samples in those frames, 63 sit under `0x5F06B0` > `0x5DC000`, 61 in `0x5D1960` at `+0x261`..`+0x2E1`. Only 4 are an OS wait leaf. It is CPU work, not a wait: this replaces the weak "state 4" and "fixed wait" reading of §5.
- **`0x5D1960` is `CasTriSort`** ("CAS/ModelBuilder/TriangleSortDataList", docs/187): triangles x vertices of `divps`/`rsqrtps` [Apex docs, stacks agree]. The narrow time fits the same large part being sorted again and again [inferred].
- **Owner: Apex Radiance.** `FastCasSort` replaces this function, on by default since 2.5.5; these runs have no Apex (`compat: Apex Radiance not loaded at start`). Nothing for us to build. Open: why SV rebuilds that part about 7 times a minute.

### 11.6 `ip-foreign-replan-verify`

| arm (slices) | sync `ReplanFromPosition` | jobs | rows |
|---|---|---|---|
| off, 37.9 s | 29, 864 ms = 22.8 ms/s | 1 | 12 Succeeded |
| on, 18.5 s | 2, 47 ms = 2.5 ms/s | 25 started, 24 collected, 967 ms | 12 Succeeded, 13 paths |

Moved off the sim thread [measured, forced repro]. Route signature is the same across arms in each cycle; one off row differs. `watchdogs` 0, no crash, 0 stuck. Task wall per plan rises: mean 211 ms on, 114 off (first plan of an arm 594, 469). The one ErrorTrap file (22:12:53) is a load-time correction log, before the first arm, and `ip-hitch-cause` has one too.

**The real boat path has one sample, and it failed [measured].** Natural get-in replans: 3. Two ran with the arm off. One took the job: Guillermo Ichtaca, `BoatSpeedBoat`, 848 m, job 671 ms, `capped (KindDeadlines)`, result `FailedBecauseNoPathToDestination`, route 0. `KdA10ForeignMs=200` turns a long boat replan into a failed route with the Sim in the boat. 4 of 58 sync replans ran >= 200 ms (7%), so this is not rare [inferred]. What the Sim did next is not in the log.

**Not ready for the 4 natural jobs:**

1. They pin `hitchlog2` (0.36.175, the broken `RemoveAt` stamp with `alarmtime` on); repoint to `hitchlog3`. Queue file edit: between jobs.
2. Raise `KdA10ForeignMs` to `SyncCapMs` (1000) on these jobs, or the on arm fails routes.
3. First a 5 min natural correctness job with `ForeignKindsMode=1`: count async and failed `boatreplan:` lines, Sims stuck in boats.
4. Newest design first: the `Follows` fix may remove most of what this job would move.

### 11.7 Locomotion no-route waits on the master ini

| world | max | >= 50 | >= 100 | >= 200 | `0x85FE10` waits / ms / longest |
|---|---|---|---|---|---|
| BP | 128 | 4 | 1 | 0 | 22 / 750 / 126 |
| IP | 201 | 10 | 8 | 1 | 20 / 1,723 / 200 |
| SV | 87 | 6 | 0 | 0 | 48 / 881 / 81 |

- **Those waits are `[SteerRO]` fallbacks [inferred, counts match].** `steerFbState + QlBusy + Trip`: BP 25, IP 27, SV 51, against 22, 20, 48 waits. 0.02-0.03% of 141-150k calls. Each one waits up to a whole job. 1.2-2.9 ms/s.
- **§10.1's candidate `0x85F6CB` is not locomotion [measured].** Every top locomotion hitch has `rtMaxKey` `0x85FE10` (one SV `0x846D21`). `0x85F6CB` is script time: IP 21 hitches 4.3 s, it names part of §4 rank 7 (`AsyncWait` -> `OnAboutToStartFollowingRoute`, IP 311 ms, key max 136).

### 11.8 New: `AsyncWait` runs are `+0x918` waits at goal validation

§4 rank 4 is named. Hitches with `rtLock` >= 40% of wall, by `rtMaxKey`, `0x75A64C`: BP 22 (2.9 s), IP 34 (5.9 s), SV 13 (2.2 s). IP `DoInteraction` -> `AsyncWait` 405 ms: `rtLock` 388, key max 201. `StuckWatchdog.CheckIdle` 437 ms on IP has the same key [measured]. Block table: BP 9.1 s, IP 8.3 s, SV 5.8 s = **9.7-15 ms/s, all worlds**, longest wait 220 ms.

`[GoalValidRO]` is on and answers only empty results: `gvFb_6` (footprints near the point) BP 13,207, IP 3,493, SV 3,252 against `gvTaken` 8,167, 2,084, 2,235 [measured]. A hit goes to the game's call and waits [verified `gvro.cpp`]. The vector's refcount writes race the job, so a fix needs a design, not a switch.

SV's worst hitch is new and single: 791 ms, `rtJob` 775, 5 waits, longest 333, key `0x06000000`, Sim plan through `Lot.PlanToLotEx`. Not named.

### 11.9 Ranked fixes

Effort: edit and build, game runs listed apart.

| rank | fix | ms/s | worst | effort | tag |
|---|---|---|---|---|---|
| 1 | `UbCore.Follows` skips `*Orig`, real frames in the selftest; start swim test; `VisitLot` | IP up to 45.5, likely 5-18 | 382 | ~45 min + verify run 20 min | cause verified, gain inferred |
| 2 | `kWildflowerTask` on | BP 3.6, spring only | **923** | ~15 min + BP verify 12 min | fix built, one live run |
| 3 | Goal validation hit answered without `+0x918` | 9.7-15, all worlds | 405 | ~2-4 h design and build + runs | cause measured, design open |
| 4 | ForeignKinds, §11.6 list | IP ~20-36 moved, not removed | 382 | ~15 min + 5 min job + 4 timing jobs ~1 h | one failed natural sample |
| 5 | `0x85FE10` fallback wait | 1.2-2.9 | 201 | ~1-2 h | inferred |
| - | `CasTriSort` (frames, main thread) | SV 23 | 208 frame | none: Apex; 15 min SV run with Apex to confirm | owner Apex |
| - | Overwatch reset | ~1 | 479 | needs the logging fix | not named |

Logging fix before any rerun (~30 min + IP and SV runs 25 min): `hitchcause` lines get their own cap in `userdiag.cpp` as `memsus` and `savecleanup` do; `HcBoatBegin` uses `UbCore.Unseen`; `unseenboat_log` gets per-reason counters.

### 11.10 Fixes built (2026-11-06, pair `hitchfix1`, ts3native 0.36.177, not run)

Branch `bugfix/hitch-fixes-1106`. Each change sits behind its switch, counters kept. Queue `hitch-fix-verify.json`.

**Rank 1, boats (`unseenboat` stays off).** `UbCore.Follows` skips frames that end in `Orig`; the frame above still decides. The selftest now has the real shapes: `[Lot.PlanRouteIntoLot, GoHome.RunOrig, Fixes+GoHomeHook.Run]`, `VisitCommunityLotLabLecture.RunOrig < VclSubHook0.Run`. `RouteToDoor` with nothing listed above it stays refused, plain Go Here and Meet Up stay exact, start on a houseboat (`rj_unknown`) is untouched: Rodney's call.
- Start test: the height test is gone. Refused only in a water posture (`SwimmingInPool`, `ScubaDiving`, `PondAndOceanRoutingPosture`, `UbCore.WaterPosture`). The start lot rule still applies only with the Sim in a room, as before.
- `VisitLot` (`Run` and `VisitLotHook.Run`) is a follower. `UbAlone` reads its private `mFollowers` by reflection, no field means exact. `UfVerdict` stays: a residential goal with no verdict is refused.

**Rank 2, wildflowers: `kWildflowerTask` default on.** Checked against the brief:

| check | result | tag |
|---|---|---|
| Flower results | same lots, order, try limit, placement calls and result handling as EA's `PlaceObjectOnLot`; RNG draws per try the same, they mix with other tasks' draws in time. A flower can show up to 20 ticks later | verified decompiled, `WildflowerModel` 5,000 stub fires |
| Off-tick | own `LoopTask`, no alarm or event frame; EA has the same yield path (`Yield` in `PlaceObjectOnLot`). Overlap guard: a fire during a live task runs in the alarm | verified code; live: `bp-wildflower-verify` |
| Save data | writes nothing of ours. `LoopTask` is the class of our 32 other tasks, which are live at every save, and the docs/242 save matrix with the mod removed passes, so its frame is no new case [inferred: which tasks the native save writes is not decoded]. The only other thing that could be stored is an EA `Wildflower` out of world | verified code, gap below |
| Swap-stack audit | `GrowHook` has stub `GrowOrig`, its IL is unchanged and it does not yield; the yield is in `WfPlaceSliced` in our task | `swap-stack-audit` |

The known gap was the `shortcut:` of docs/110 §7.3: "a save that starts and ends inside one yield still stores the out-of-world flower". **It does not happen on a normal save [verified decompiled]:** `OptionsModel.SaveGame` sets `mSaveInProgress`, and `SaveGameSynchronous` runs `Simulator.Sleep(0)` before the native save, so a task on `Sleep(0)` wakes with the flag up and destroys the flower. Travel saves did not set that flag: the guard now also reads `GameStates.IsTravelling`, set before the `Sleep(0)` in front of each `SaveTravel` (`WildflowerFire.MustStop`). That each task runs once per script pass is [inferred] from docs/04's TaskObjects walk, so `bp-wildflower-save-abort` is in the queue. The fix has no live proof yet: `bp-ab-wildflowertask` measured nothing (docs/110 §13), §11.2's "one live run" is that job.

**Logging.** `userdiag.cpp`: `boatreplan:`, `owreset:`, `alarmtime:`, `lotpass:` have their own cap of 6,000 lines, one line when it is reached, on top of `HcLine`'s 120 a minute; test package only. `boatreplan:` class uses `UbCore.ReplanUnseen`, so `kHeld` with the boat out of view is `unseen` and `boatreplan_seen_ms` is the seen part. New list `unseenboat_ms`: boat replan ms per the Sim's last refusal reason (`UbReasonMs`, 64 rows, with `unseenboat` and `boatreplanlog` on). A replan with no refusal on record is `no refusal`.

**`[RouteAsync]` foreign cap (§11.6): 1000 ms on the natural jobs, default unchanged.** The cap must clear the worst natural job, not the mean. Worst sync replan 382 ms, the on arm's task wall is 1.85x sync (211 / 114 ms, forced repro), so ~710 ms [inferred]; the one natural job ran 671 ms. `KdA10ForeignMs=1000` is `SyncCapMs`: a job is then never cut where the sync plan would run on. `ForeignKinds` stays off in the ini. The 4 `ip-foreign-natural-*` now pin `hitchfix1` with that cap, after `ip-foreign-natural-correct` (5 min, `ForeignKindsMode=1`). Rank 1 may shrink what they move (§11.6 item 4).

## 12. `hitch-fix-verify` results and the wildflower save proof (2026-11-06, pair `hitchfix1`)

Queue `hitch-fix-verify-2026-11-06`, 5 jobs, static read of the rows, no new game run. `ip-uboat-verify`, `bp-wildflower-verify`, `sv-hitchfix-smoke`, `ip-foreign-natural-correct` PASS with 0 crashes and 0 stuck Sims; `bp-wildflower-save-abort` FAIL (§12.5). All are correctness runs; `run-verdict.ts` calls two of them VOID on machine noise (other apps 24-30% of the game CPUs, mostly `monohost`), so no number here is a timing result.

### 12.1 Boats, `ip-uboat-verify` (20 min, IP) [measured]

| | `hitchlog3` run | this run |
|---|---|---|
| `unseenboat_shortcuts` | 3 | **67** |
| `rj_route` of candidates | 544 of 572 | 59 of 586 |
| boat replans, total | 300, 27.3 s, 10 min | 491, 44.4 s, 20 min |
| per wall second | 45.5 ms/s | 37.0 ms/s |
| per replan | 91 ms | 90 ms |

Run lengths differ (10 vs 20 min) and the rates come from separate runs, so they are correctness data, not an A/B. A replan costs the same; there are fewer of them per minute (24.6 vs 30). **True split:** seen 6 replans, 731 ms; unseen 485, 43,660 ms (98%).

`unseenboat_ms` (replans, ms): no refusal 113, 14,102 · owned boat 82, 9,079 · goal indoors, raised or in the water 68, 8,031 · `start:resort/diving` 34, 2,308 · `goal:resort/diving` 23, 2,086 · Sim group 23, 2,030 · caller `GoHereWithHoldingInteraction.Run` 17, 1,202 · Sim held 13, 1,111 · goal verdict 9, 1,055 · Sim age 14, 641 · seen 60, 555 · followers 6, 535, then 8 rows under 500. `no refusal` is the biggest row and is not explained: the replan has no refusal on record, and `rj_unknown` is 270 trips that stayed exact before classification [inferred link]. Next lever: that row, then owned boats and goals.

Correctness: stuck 0, houseboats 10, moved none, `unseenboat_no_spot` 0. One ScriptError file: an ErrorTrap correction log at load (`PlateServing`, Margarita Manzano), the known one of docs/242; `ip-foreign-natural-correct` has a like one (destroyed plates and a `CarUFO`), also at load. Refused for view: `unseenboat_seen` 7, `released_seen` 113. No counter or line says a moved Sim was on screen, so a visible snap is not excluded, only not reported. `verify-shortcut.ts --name unseenboat` needs a live game and did not run.

### 12.2 Wildflower, `bp-wildflower-verify` (12 min, BP) [measured]

The task path ran live for the first time. `kWildflowerTask` in `settings on`, 12 fires in the natural drain, **9 placed, 3 failed** (20 tries, 577, 625, 943 ms in total, split into one try per tick), 69 tries, `wildflower_saveaborts` 0. Same count as the alarm in docs/110 §12 (12 fires, 9 placed, 3 failed).

Longest slice 84 ms (`wildflower_maxms`; `hitches.jsonl`: 26 task runs >= 40 ms in `LoopTask` / `WfPlaceSliced`, max 84.6). No `Wildflower` alarm line >= 20 ms, against 923 ms. The 943 ms fire is the same size as that hitch, so the run had the case in it. Worst hitch of the run 328 ms, `rtLock` 253 in a Sim's task, not a flower. Flowers on lot, 0.6-26 m from the try's start.

### 12.3 Foreign job, `ip-foreign-natural-correct` (5 min, IP, `KdA10ForeignMs=1000`) [measured]

96 boat replans: **95 async, 1 sync of 2 ms** (`noTaskYield`, `fp.houseboatfix`), 0 failed, 0 `NoPath`. Sync boat time ~0 ms/s on 300 s. The async lines sum to 13.9 s; that is wall time with the Sim's task asleep, not sim-thread time. A10 foreign `long job` lines: 30, max 461 ms, **0 capped**. `a10foreign_started` 151, `sent` 465. `routefail:` 39, 38 `Plan async FailedBecauseNoPathToDestination`, none in a boat. Stuck 0, houseboats moved none.

**Ready for the 4 `ip-foreign-natural-*` timing jobs.** Not read: the same 5 min with the arm off, so `routefail:` 38 has no comparison, and rank 1 changes what they move (§11.6 item 4). Worst hitch 563 ms ends in `AsyncWait`, `rtLock` 365 on `0x85F6CB`: the job moved the replan, the sim thread still waits on `+0x918` at goal validation (§11.8).

### 12.4 Overwatch big run and SV 791 ms [measured, cause inferred]

The cap fix works: `owreset:` lines 42 of 42 on SV (BP 43, IP 13); no `owreset: OnPerform` line on any job, so `after` was not logged.

| line | ms |
|---|---|
| `Perform` Justine Keaton, SV, with sleeps | 570 |
| `InstantiateAtHome` (`create` 57, `restore` 28, `safe` 5 before it) | 63 |
| **`tail`, after `InstantiateAtHome`, inside `Perform`** | **456** |
| hitch: `OwResetHook.OnPerform` run from `Common.Sleep` in `ResetSimTask.Perform`, 545.7 ms frame | 520 |

63 + 456 = 519 of the 520 ms run, so the run ends about when `Perform` ends. That rules out candidates 1, 3 (§10.2: `create`, second try) and 2 (the notice) for this reset. The step is NRaas's code after `InstantiateAtHome`: `DoSelectActor` when she was `Sim.ActiveActor`, `ClaimOwnership` of the bed, `ResetSkillModifiers`, `ResetRole`, `UpdateInterface`. She is in the active household; whether she was the selected Sim is not in the line. `DoSelectActor` fits the `ThumbnailManager` spike after big resets (§10.2) [inferred]. One sample: the 22 other resets have `tail` <= 11 ms. Proof needs one stamp around those calls.

**SV 791 ms (`rtJob` 775): not seen.** Max 546 is the reset above; largest `rtJob` 267, key `0x06000000`. One 10 min run, so it is not gone. Second worst: `StuckWatchdog.CheckIdle` 477 ms (`rtLock` 312, `rtPlanWait` 157), our own task's route check, new in this table and not named further.

### 12.5 `wildflower_drain` could not find the method: cause and fix

`bp-wildflower-save-abort` failed like the 2026-10-19 A/B (docs/110 §13). The error named one member, `OnGrowWildflowersOnLot`. The decompiled method is `private static` on `Wildflower` itself, not nested and not renamed [verified]. `ReproHandlers.cs` asks for `Static | NonPublic`.

**Cause.** ts3fixes' `SwapMethod` copies the hook's 40-byte header over the target, all but the name pointer [verified `Fixes.cs`]. The flags at `+0` are in it. `WildflowerTaskHook.GrowHook` is `public static`, so after the swap the method reads as public under its old name, and a `NonPublic` search skips it. That fits both failures: only the swapped method is missing, the two fields and `mCollected` are found, ts3fixes' own lookup has all flags and hooks it fine [inferred from the code; the agent has not run since]. The agent's other lookups were scanned by a script: `ReproHandlers.cs` was the only one that asks for a `Patch` target without `Public`, apart from `PatchHandlers.cs` `Run`, which asks with all flags.

**Fix.** `st | BindingFlags.Public` on that lookup. A static target has no invoke check against the swapped class (`ReflectCall`'s issue is instance-only) [inferred].

### 12.6 Does the repro prove the save claim?

As written it did not:

- **Hit by chance.** 9 of 12 natural fires place on the first try, and only a failing fire stays pending for its 20 ticks. A save at a fixed 20 s misses most runs; the entry called that inconclusive and the job could PASS without the guard running.
- **Save read by hand.** The `expect` line said to reload and look; no job did.

Now:

| step | how | proves |
|---|---|---|
| Force the state | `wildflower_drain {save_pending: name}`: a task waits for a flower out of world on 2 ticks running, then saves as a new save at that moment | a save starts with a fire between tries, every run |
| Guard ran | `until patches wildflower_saveaborts >= 1`, 240 s, else FAIL | the flag is read in time, the flower destroyed, I-247-18's order live |
| Live state | new reply field `out_of_world` (uncollected, not in world, not in inventory) = 0 after the save | no flower left behind |
| File | new repro `wildflower-save-check`, job `save: Wildflower-Scratch`: loads, `out_of_world` 0 at load and after 2 min, `scriptErrorsMax` 0 | the save holds no such flower |
| Removed | same repro, `ourMods: false` | the save loads without ts3fixes |

`save_pending` only fires on a flower the same count sees, so `out_of_world` cannot be blind: if `GetObjects` missed out-of-world flowers, the step would time out, not pass. Not proven: a flower reloaded by another route than `GetObjects`.

### 12.7 Queue `wildflower-save.json` (not run)

7 jobs, ~1 h 5 min game time [inferred from docs/170 and bundle-1105 rows], correctness only, `--no-smoke`, `--dry-run` parses 7 of 7:

| job | what |
|---|---|
| `bp-wf-save-pending` | forced save, §12.6 |
| `bp-wf-save-reload` | that save with our mods |
| `bp-wf-save-removed` | that save, `ourMods: false` |
| `bp-wf-matrix`, `ip-wf-matrix`, `sv-wf-matrix` | `save-matrix.ts --fixes shipped`, 9 cases + reload, as `bp-rel-matrix` |
| `bp-wf-travel-france` | travel case, as `bp-rel-travel-france`: the guard reads `IsTravelling` |

The switch-on proof is `logRequire` `settings on:.*kWildflowerTask`. ts3fixes comes from source, because `fixesPackage` is refused on matrix jobs and `hitchfix1` carries the test-only `kHitchCauseHooks`; the asi is `hitchfix1`. The matrix compares fixes on vs off, it does not switch `kWildflowerTask`: it shows saves stay good with it on and a natural drain in the spring save, not that a fire was pending in a matrix save. The other 5 trips stay in `release-validate.json`.

**Default.** Stays ON until `wildflower-save` runs. The static case is verified code plus one inferred task order, and the natural run is clean. If `bp-wf-save-pending` ends with no `(save)` line or `bp-wf-save-reload` shows a flower, set `kWildflowerTask` default off. Worst case if the guard failed: one flower stored out of world, counting toward the 20 cap until its lifespan alarm.

### 12.8 `wildflower-save` read: the 2 ScriptErrors are not the wildflower task (2026-11-08, static, ledger §452)

Rows `probe-out/queue/wildflower-save-2026-11-06.jsonl`, reports in `probe-out/20261107-021550/events.jsonl` (reload) and `probe-out/20261107-033111/events.jsonl` (removed). No game run.

**Flower bar met [measured].** `bp-wf-save-pending`: `wildflower_saveaborts` 1 with a `(save)` line, save written during the pending fire, `out_of_world` 0. `bp-wf-save-reload` (our mods) and `bp-wf-save-removed` (`ourMods: false`): `out_of_world` 0 at load and after 2 min, the world loads, `uncollected` 0 at load. The repro steps PASS in both; only the script-error check failed. `bp-wf-matrix`, `sv-wf-matrix`, `bp-wf-travel-france` PASS, `ip-wf-matrix` PASS on its rerun.

**The two files [verified, report text].** Both are NRaas ErrorTrap correction logs, no exception and no stack frame, no `Wildflower` line:

| job | correction lines | Ts3Fixes loaded |
|---|---|---|
| `bp-wf-save-reload` | `Out of World Object Found 2: SocialJigOnePerson`, `Destroyed Object Found: NewspaperShreds`, `Destroyed: SocialJigOnePerson`; dereference chain `RolePaparazzi+FollowCelebrity.mJig` on Lenny Shutter | yes |
| `bp-wf-save-removed` | `Destroyed Object Found: NewspaperShreds` only | no |

**Owner per line.**

- **Paparazzi jig: EA [verified for the reference chain, inferred for the cause].** 20 runs since 2026-10-17 have a `SocialJigOnePerson` report; the same `RolePaparazzi+FollowCelebrity mJig` chain shows in `ip-baseline-clean-1` (2026-11-05, `probe-out/20261106-043810`), a run without Ts3Fixes. The paparazzi role keeps a jig that is not in the world, and ErrorTrap clears it at load. It is in the reload file only because ErrorTrap logs the jig when that Sim is loaded with the jig out of world; the removed load did not hit it.
- **NewspaperShreds: in the save file, not from loading with our mods [verified]; not ours [inferred].** The line is the same with and without our mods, so it is written in `Wildflower-Scratch.sims3`. No other run of 1,949 reports has it, so the object was made and destroyed in the 2-min pending session (our mods on). `NewspaperShreds` is an EA `TrashPile`, made by resource key, so no caller is greppable. Neither ts3fixes nor ts3agent names it; the agent's `fresh_world` destroys loose `TrashPile`s, but no step of these jobs calls it (no `trashPiles` line). An EA trash pile disposed while something still lists it is the likely cause. Not separable without a save written in a vanilla session, and no such job exists.

**Verdict.** Neither file comes from `kWildflowerTask` or the `hitchfix1` save guard. The jig is EA (seen without our mods). The shreds line is in the file and loads the same without ts3fixes. "0 script errors beyond vanilla": met for the jig, met in substance for the shreds (no wildflower line, same with mods removed) but without a vanilla-session comparison. 41cfcd23 already lets these three exact lines pass on the two jobs (`scriptErrorsAllow`), and a `Wildflower` line or a stack frame still fails (`queue.ts --self-check`).

**Default.** `kWildflowerTask` default ON: no stored flower, guard ran live, matrix and travel PASS. Optional, not needed for the default: rerun the first 3 jobs of `wildflower-save.json` (`bp-wf-save-pending`, `-reload`, `-removed`, ~11 min with the allow list) to see whether `NewspaperShreds` comes back. It would not decide ours vs EA; only a pending session with our mods off would, and that job is not built.

### 12.9 Boat `no refusal` row and `hbstart1` household check (2026-11-08, static, ledger §452)

**What `no refusal` is [verified code, split inferred].** `UbBookReplan` books a boat replan under the Sim's entry in `sUbLastWhy`; only `UbSample` (a gate refusal or a colour-unknown exit) writes that entry. `UbTrip` removes it at every boat-enabled plan (`UnseenBoat.cs` line 80) and then exits with no entry when: the goal is invalid or under 40 m (`FarEnough`), the plan is nested (`sUfPlanDepth > 1`), or `UbCore.Trip` is `kExact` with a known colour, i.e. a same-lot trip or a trip inside one land component that EA plans with a boat. Replans themselves keep the entry: in the boat, `UfCarOrBoat(true)` returns before the removal. So `no refusal` is trips that were never candidates (exact by the §2 rules), plus candidates whose entry a later short or same-lot boat plan of the same Sim wiped. It is not a hidden refusal and not a shortcut that failed.

**Data [measured, `hitch-fix-verify-2026-11-06-ip-uboat-verify-1453-ts3native.log`, 491 `boatreplan:` lines].** 376 of the 387 `DoRoute` replans (42.9 of 44.1 s) belong to Sims with at least one sampled refusal, 11 (1.2 s) to Sims never sampled. So most `no refusal` replans are Sims with a refusal at some other time whose entry was cleared, not Sims that never reached `UbTrip`. Same-lot replans: 16, 1.3 s. 104 `EnterHouseboatThroughGangway` replans cost 273 ms in all. The split of the 113 replans / 14.1 s across the three exits is not in the data: the exits write nothing.

**Next lever (not built, test package only).** One `sUbLastWhy` entry per silent exit (`trip: same lot`, `trip: one component`, `trip: short`, `trip: nested`), no behaviour change; then one `ip-uboat-verify` (20 min, `hitch-fix-verify.json`, with `unseenboat` and `boatreplanlog` on). If `trip: one component` holds most of the 14.1 s, that is EA picking a boat on a land-connected trip, which the shortcut leaves exact by design; a change there would be a new design question for Rodney, not a fix.

**`hbstart1` household check [verified in the logs for the logged moves; inferred for the rest from code].** Queue `verify-shortcut-houseboat-2026-11-07` (5 jobs, IP, active household Dregg: Cara and Edward Dregg). `ip-ubstart-repro`: `unseenboatstart_shortcuts` 11, `left_dirty` 0, 8 sampled `houseboat start: placed, Docked` rows (Akoni Kahale, Lincoln Porter, Rubisel Ichtaca), none a Dregg; one `start:household lot` refusal (Raylen Richardson, a visitor on Atlantis): the household-lot rule refuses any Sim on that lot, not only members. No `=>`/`->` move line in any of the 5 job logs names a Dregg. Code: `UbGate` refuses `UfWhy != 0`, and `UfWhyBody` returns `kHousehold` for `IsSelectable` or the active household; `UfDriverlessWhy` counts no-Sim and ownable vehicles as household. The sample list was full (240) and the verify-b rows carry no list, so unlogged moves rest on that code. The `ufplan: ... why 3 cut Edward Dregg` lines in these logs are the shared-field print bug PR #306 fixed (`UnseenFallback.cs` `UfLast`), not a cut of a household plan. `hbstart1` runs had `boatreplanlog` off, so `unseenboat_ms` cannot be re-read there.
