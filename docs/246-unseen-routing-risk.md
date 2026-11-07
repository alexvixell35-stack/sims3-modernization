# 246. Can the unseen work cause routing issues?

Written 2026-11-06. Static analysis over existing run data and source, base master `d1556442`, plus the uncommitted `unseenpark` code in the `feature/unseenpark` worktree. No build, no game run. Rodney asked whether today's unseen work could repeat the routing trouble of the work parked earlier. Ledger: docs/51 §435.

Tags: **[measured]** from the run files named; **[verified]** read in source or a doc's own disassembly; **[inferred]** reasoning, still to confirm. All arm numbers are in-load `--ab`, warm-up slice and the 15 s after each switch left out, at ultra speed. Every run is `timing: invalid` by noise class, so only counts and shares count here, not fine ms/s.

## Verdict

- **No active unseen feature shows a routing regression in existing data.** Plans per Sim-hour, route fails, cut routes and routes >= 300 ms stay within noise for `autunseen`, `[UnseenAnim]`, `[UnseenTable]`, root-motion Mode 1 and the fallback [measured].
- **The parked result is weaker than remembered.** "Routes/s up 10-14%" is the `routes` column, which counts sim-thread route calls only. It rose on BP only (+14, +10), IP was +7 and 0. Real script plans on the same runs moved +2.9% BP and -5.1% IP. No single switch of the bundle reproduced it, and no mechanism was ever proven. The throttles were parked for no ms/s gain, not for broken routing.
- **The routing mechanisms that are proven or close to it are different:** fake steering answers and skipped avoidance. Both stayed off. Nothing active has that mechanism.
- **Real risks left:** `unseenpark` (never run, and its stop rule reads the wrong column), pace 50 and 35 (the untested direction; unpaced searches rose +19% / +10%), and route length (no counter exists, only a job-time proxy).
- **Open, not a finding yet:** BP hitches >= 100 ms rise with `autunseen` in the one balanced pair (177 -> 218 per 10 min, §4). Most of the wider gap is the slice-order artefact of §1.

## 1. What the counters really mean

| Metric | What it counts | Use |
|---|---|---|
| `routes`, `routeFails` (seconds CSV) | `routeOuter` completions **on the sim thread only**, including `Route_IsPointRoutable`; other threads go to `routeOtherThreads` [verified, `tools/frida/stutter-profile.js`] | not a plan count. With `[RouteAsync]` about 2 of 3 plans run elsewhere |
| `backoff_plans` (ts3fixes [24]) | script `Route.Plan` calls through `PlanBackoff` [verified, `RouteBackoff.cs`]; within 2% of `asyncStarted` [measured] | **the plan count to use** |
| `asyncUs` / `asyncStarted` | mean job time on the worker [verified, `ts3native.cpp`] | proxy for route length, reads cost and not distance |
| `asyncUs` per Sim-hour | total route work | most stable reading, §3 |
| `ufMarks`, `ufLong`, `ufJobCut`, `ufSyncCut` | plans marked unseen, marked plans or jobs >= `DeadlineMs` (300), cuts [verified, `unseenfb.cpp`] | unseen share and the 300 ms rule |
| `g8_simhours_x100` | Sim-hours, the right denominator; 0 without `kAutLive` | not in the throttle runs |

**Noise [measured].** Slice-to-slice sd of `routes` per second is 8.2% BP, 12.5% IP (166 + 202 slices), so one in-load arm difference has sd ~7% BP, ~10% IP. Null switches (`unseentable`, `wakepoll`): per-run sd 4.2% BP, 10.5% IP. Script plans swing more on BP: -17..+16% per run, for any switch.

**Slice-order artefact [measured control].** Root-motion Mode 1 is EA-identical (docs/228), so it is a null test. Its BP `route + rtLock` ms/s, off -> on: off-first 100.8 -> 122.9, 98.8 -> 114.2; on-first 124.2 -> 98.9, 112.1 -> 93.6. That is ±20 ms/s by order alone, the slow middle slices of docs/143 §8.15. **Any one-order BP comparison of route time is void.** The `gate2-pace` jobs are single order.

**Same control on the `routes` column:** Mode 1 shows +6.7% on BP (10, 3, 4, 10) with script plans +0.3% and worker time -0.8%. A feature with no behaviour change moves the column by about what the bundle did, so the column cannot judge behaviour at ±10% [measured]. Why it moves is not known.

## 2. What was parked or rolled back, and why

| Item | State | Routing symptom | Numbers | Mechanism |
|---|---|---|---|---|
| Script bundle `unseenbroadcast` + `unseenmotives` + `wakepark` | parked 11-02 | `routes` up | BP +14, +10; IP +7, 0. Script plans BP +2.9% (-2, +7), IP -5.1% (-2, -8). Worker time BP -6.5%, IP -5.1% [measured] | **Unproven.** Members alone are flat, §3. Parked for ~0 ms/s (+8.5 / -9.9 by order) |
| `unseenmotives`, `unseenbroadcast`, `unseenenv` | parked 11-02 | one extra >= 300 ms event on BP for `unseenmotives`, two for `unseenbroadcast`; `unseenenv` BP route ms/s 36.4 -> 43.4 | CI overlaps, timing invalid (`throttle-gains-2026-11-02.md`) [measured] | none shown |
| All three on, SV | 10-22 | frames >= 300 ms 1 -> 13 over a pair: 5 sync plans, 4 `+0x918` waits, 3 locomotion with waits [measured, docs/144 §8] | | catch-up burst **not supported**; "picks happen at other times" [inferred]. Never split by switch |
| `wakepark` | parked 11-02 | route ms/s +11% BP in one pair | -2.0 / -5.0 ms/s, "route -14 / locomotion +11 swing = noise"; script plans BP -0.4%, IP +6.1% (-3, 11, 6) [measured] | none shown |
| `wakepoll` | parked 11-02 | IP max hitch up in both orders | script plans BP +5.9%, IP -5.2% [measured] | none shown |
| `[UnseenFallback]` C1 / C2 fake answers | `Sites=0`, off | unseen walkers hit objects, game replans: sync `Route_Replan`, 200-300 ms hitches | docs/202 §3.4.2 states it; no counter separates it | **Stated as the design cause** of the FinishWalk redesign, no tag there |
| `[UnseenThrottle]` avoidance skip | off | more locomotion and lock waits | ~-10% it/s, both orders negative on IP (docs/143 §8.15) [measured] | late avoidance, Sims run into each other, replans [inferred] |
| Ambient car route skip | dropped | more stuck Sims in BP | ~1% gain (docs/143, docs/04 F-table) | not recorded |
| Mode 2 coarse root motion | parked 11-03 | none: `rm2Elig` 0, never acted | smoke FAIL (docs/228 §11.8) | n/a |
| Root-motion Mode 1 | off in release ini since v0.2.5 | none | -13% it/s in `v025-speed-ab`, not routing; R5 retest after Mono | n/a |
| D / D-arrive / D-leave, trip holds | built, never acted naturally | none | docs/202 §3.6.3 | n/a |
| `autunseen` first builds | fixed | unseen searches +19% BP, +10% IP unpaced; IP pushes -13.5% until rabbit-hole pairs went to the game | docs/222 §8-§10 [measured] | cadence: direct search does not yield on vanilla's schedule [verified] |

So the proven list is short: **cadence moves with pacing**, and **lying to the steering costs replans**. "Skipping raises routes" (docs/243 option B) rests on the `routes` column only and should be read as unproven.

## 3. Route data per feature

Pooled over runs, on vs off, per second. Per-run list in brackets. Out because the slice record is missing or was overwritten: `bp-gug-pace100-ab`, `bp-gug-t-ab`, `thr-unseenmotives-ab`, `thr-unseenenv-ab`.

| Feature | World, runs | `routes` col | Script plans | Worker job time | Sim-thread fail % |
|---|---|---|---|---|---|
| bundle | BP 2 | +12.0 (14, 10) | +2.9 (-2, 7) | -8.7 | 4.2 -> 4.7 |
| | IP 2 | +3.6 (7, 0) | -5.1 (-2, -8) | -0.1 | 16.6 -> 13.1 |
| `unseenmotives` | BP 2 | -3.0 (3, -9) | -5.9 (-17, 7) | +7.0 | 2.7 -> 3.1 |
| `unseenbroadcast` | BP 3 | -1.1 (0, -2, 0) | -1.2 (-8, -7, 7) | -1.4 | 3.2 -> 3.5 |
| | IP 1 | -1.9 | -15.0 | +13.0 | 16.9 -> 21.2 |
| `wakepark` | BP 3 | -0.8 (-13, 2, 5) | -0.4 (-11, -9, 16) | -2.2 | 3.9 -> 3.6 |
| | IP 3 | +4.3 (-5, 11, 4) | +6.1 (-3, 11, 6) | -2.1 | 10.8 -> 11.1 |
| `[UnseenAnim]` | BP 5 | -0.3 (-4, 1, -2, 10, -5) | -2.9 (-16, 5, -4, 0, 2) | +5.0 | 2.9 -> 2.9 |
| | IP 2 | -1.3 (-7, 4) | +0.6 (4, -2) | -9.5 | 9.4 -> 10.3 |
| `[UnseenTable]` | BP 3 | -1.6 (-2, 3, -6) | -2.3 (-8, 16, -13) | -2.1 | 2.9 -> 2.5 |
| | IP 2 | -6.2 (-18, 6) | -1.8 (0, -4) | -12.2 | 8.1 -> 8.7 |
| root-motion Mode 1 | BP 4 | +6.7 (10, 3, 4, 10) | +0.3 (-6, 10, 16, -16) | -0.7 | 2.8 -> 3.2 |
| | IP 4 | +2.7 (1, 9, 7, -5) | -3.2 (-2, -4, -2, -5) | +1.6 | 9.0 -> 9.6 |
| `unseenroute` | BP 2 | +2.5 (0, 5) | -15.6 (-22, -9) | +23.8 | 3.2 -> 3.2 |
| | IP 5 | -1.7 | +1.1 (3, -5, 2, 14, 6) | -1.4 | 9.2 -> 8.9 |
| | SV 1 | +18.6 | -13.4 | -6.9 | 4.1 -> 3.3 |

`autunseen` has Sim-hours, so per Sim-hour, off -> on. BP 4 jobs (3 off-first, 1 on-first), IP 5 jobs [measured]:

| | BP | IP |
|---|---|---|
| Searches | 0.740 -> 0.501 (-32.4%) | 0.887 -> 0.718 (-19.0%) |
| Pushes | 0.183 -> 0.153 (-16.6%) | 0.187 -> 0.176 (-5.9%) |
| Script plans | 2.042 -> 1.860 (-8.9%) | 0.797 -> 0.792 (-0.6%) |
| Unseen-marked plans | 1.557 -> 1.329 (-14.6%) | 0.642 -> 0.656 (+2.1%) |
| Worker job mean, us | 4,642 -> 5,095 (+9.8%) | 10,875 -> 10,733 (-1.3%) |
| Worker ms per Sim-hour | 9.48 -> 9.48 (0.0%) | 8.67 -> 8.50 (-1.9%) |
| Script route fails | -2.9% (79 -> 76 events) | +7.6% (67 -> 74) |
| Sim-thread `routes` | +5.2% | +6.2% |
| `ufLong`, whole set | 0 -> 0 | 3 -> 2 |
| `ufJobCut` | 0 -> 1 | 65 -> 61 |
| Seconds with a route >= 300 ms | 0 -> 0 | 5 -> 2 (~1,550 s each arm) |
| Total routes in a second, p99.9 | 72 -> 75 | 39 -> 38 |

The balanced BP pair alone (`t-ab-r2` + `t-ab-onfirst`): plans -5%, job mean +4.5%. Worker ms per Sim-hour by job runs -11.8..+8.2%, so 0.0 is partly luck, but the sign is mixed.

`autunseenrh`, one pair per world, on vs off: plans per Sim-hour +15.4% BP, +17.6% IP; job mean -14.8%, -13.4%; worker ms per Sim-hour -1.7%, +2.2%; unseen pushes per unseen Sim-hour +12.2%, +14.2%; `visitlot_runs` 8 -> 19, 5 -> 13 [measured].

## 4. Per-feature verdict

The question for each: more picks that route, farther picks, bursts, stale needs, retries, snap-back routes, wake-ups, lot-edge entry, `RouteAsync` interplay, the 300 ms rule.

| Feature | State | Verdict | Why |
|---|---|---|---|
| `autunseen` | opt-in, default ON if gate 2 passes | **Safe on volume, unknown on length** | Fewer searches, flat or fewer plans, fails flat, no 300 ms change, no bunching in the p99.9 [measured]. Top-1 equal in Dry (docs/222) so picks match. Job mean +9.8% BP could mean farther trips or fewer short ones; no length counter |
| Snap-back of a direct pick | with `autunseen` | **Safe** | Per on arm: 2-59 picks tagged, `autunseen_flip_cancel` 0 and `_flip_kept` 0 in all 9 jobs, `snap_*` 0-8, against 2,000-13,000 plans [measured]. A kept running head is vanilla's route. Cannot cause a route rise |
| `autunseenpace` 78 / 100 | with `autunseen` | **Safe** | The table above is these |
| `autunseenpace` 50 / 35 | gate 2 pace queue, not run | **Risk** | Less charge moves toward the unpaced state: unseen searches +19% / +10% per Sim-hour with per-search outcomes flat (docs/222 §8), so more picks. **No route data survives from that build** (the g8 tags were overwritten). Single-order jobs, so only plans per Sim-hour read, not route ms |
| `autunseenpacecredit` | test switch, not run | **Unknown, bounded** | Aims at vanilla's schedule, so the ceiling is the off arm's cadence, if I-432-5 holds. A wrong exchange rate looks like the row above |
| `autunseenrh` | stays on | **Safe; off is the risky state** | On arm matches the exact arm: 2.08 vs 2.04 BP, 0.80 vs 0.80 IP. Off under-routes. Worker time flat. One pair per world |
| `autseengame` | test switch, not run | **Safe by construction** [inferred] | A seen actor runs the game's own search. Only open point: if the exact path really picks more often (I-432-4), seen pushes fall a little |
| `unseenpark` | built, uncommitted, never run | **Risk** | §5 |
| `[UnseenAnim]` Mode 1 | shipped | **Safe** | Delays clip events and the footprint refresh up to 3 passes for unseen Sims, held within 10 m of a visible Sim and 80 m of the household (docs/144 §3.4) [verified]. Plans, fails, cuts flat in 7 runs [measured]. Not the fake-steering mechanism: a Sim's path following is untouched |
| `[UnseenTable]`, `unseenposcache` | shipped | **Safe** | A read cache for the same classifier, `RefreshTicks=1`. IP's -6.2% is one -18 run; plans -1.8% |
| `[UnseenRootMotion]` Mode 1 | off, R5 retest | **Safe** | EA-identical. Use it as the null control |
| `[UnseenFallback]` phase A, teleport, stuck, D family | ts3fixes defaults True in `build-fixes.ts`, `Enabled=1`, `Sites=0` | **Safe with the stack** | `ufSyncCut` 0, `ufJobCut` 65 -> 61 with `autunseen` [measured]. A cut route is a no-path failure with a 1.5 s no-retry window and a 4-in-10-s cap, so a cut cannot loop [verified, docs/202 §2]. D never acts naturally |
| `[UnseenFallback]` `Sites=1` | off | **Risk if enabled** | The C1 / C2 replan mechanism. Keep off |
| `[UnseenRoute]` | off, not in gate 2 | **Unknown, not active** | Plans -15.6% BP, -13.4% SV is the intended skip. `sv-ur-car-loop` route ms/s 13.5 -> 31.3 in a forced loop. On IP `ufLong` 2 -> 4 and seconds with a route >= 300 ms 7 -> 10, within noise. Needs its own bars before any default change |

### What the checks found across the stack

- **Retries.** `autunseen` removes no-pick retries, it does not add them: no-pick per Sim-hour -37.6% BP, -22.5% IP.
- **Stale needs.** Low-LOD motives come from `AutoSatisfyMotive`, a time-of-day curve (docs/245 §3) [verified], so a longer gap does not build a need up. No stale-need pick for low-LOD Sims.
- **Sims at the lot edge.** A direct pick needs the Sim unseen at post and resume. Its route is then a normal planned route. `V5` covers arrivals. No data separates routes that end in view.
- **`RouteAsync` interplay.** `asyncRefused` 627 -> 568 BP, 285 -> 273 IP; `asyncDropped` under 40 per arm [measured]. No sign of the gate backing up.
- **The chain is weak.** Searches -32% gives plans -9% on BP; -19% gives -0.6% on IP. There are ~11 plans per push on BP and ~4 on IP, so autonomy pushes are a minority source of plans [inferred]. This cuts both ways: search work is unlikely to raise routing much, and I-243-2 (fewer searches cut the chain) looks weak.
- **Route work looks conserved.** In both `autunseen` and `autunseenrh`, plans and job mean move in opposite directions and the product stays within ±2% pooled. Plan count alone would have read `autunseenrh` as +17%. Cause unknown [inferred]: picks that add short plans.
- **Side finding.** `autunseenrh` raises unseen pushes +12-14% with cadence flat (searches +2.9%, -2.9%). docs/222 §10.1 put the IP push drop on cadence. One pair per world, so a question for that section, not a correction.

**BP hitches >= 100 ms, open.** Off -> on per 10 min: off-first 131 -> 230, 164 -> 247, 148 -> 266; on-first 206 -> 170. Route-led share stays ~45%. The sign follows slice order like the control's `route + rtLock`, and Mode 1 pooled balanced is flat (175 -> 169). But the balanced `t-ab` pair still reads 177 -> 218. None reach 300 ms. `*-gu2-t-ab` and `-onfirst` settle it: hitch-ab pair mean, both orders.

## 5. `unseenpark`

Read from the uncommitted worktree code, docs/243 §9 there. Never run. Hold = `AutonomyManager.Add` not called; `mLastTime` is the clock; hold while gap < 30 sim-minutes.

| Question | Finding |
|---|---|
| Parked Sims waking together | No sleeping task, so no wake storm like `wakepark`'s view-change wake-all. Each Sim polls on EA's idle path [verified]. Release is per Sim and one search per tick serializes it |
| Burst on a camera jump | Held Sims on the new lot all pass at their next poll (`unseen_park_snap`) and become searches within seconds, **in view**. They were idle anyway, so they are a queue, not new work, but those picks and routes land together on the lot the player just opened [inferred]. No counter times this |
| Stale needs | None for low LOD (§4). `RunAutonomy`'s refresh is untouched |
| More routing | Unlikely by itself: no-pick searches go first. But `MakeSimGoHome` follows no-picks (docs/222 §11.4), so a held Sim leaves a venue later, and venue demand is a controller: `ScoreMetaObject` returns 0 once a venue is full [verified there]. Fewer Sims moving can raise the pick rate of the rest. Under `autunseen`, pushes per search already rose |
| Gain | The weak chain above predicts search time saved and little routing saved. Stop rule "only autonomy ms falls" is the likely outcome [inferred] |
| With `autunseen` | Effects overlap on the same no-pick searches. The A/B jobs run at `autunseen` off; default ON would change the base, and docs/243 §8 says it must match |
| With `unseenrefresh` | Both read or gate `mLastTime`-based refresh. A skipped refresh leaves `mLastTime` old, so park releases sooner: a quiet loss of effect, not a fault [inferred]. Do not combine untested |

Two fixes to its plan before it runs: docs/243 §7 "routes/s rises" must read bar R1 below, not the `routes` column, or Mode 1's own +6.7% would stop it. And `g8_simhours_x100` must be > 0 in the smoke job, since it needs `kAutLive`.

## 6. Proposed checks

For `gate2-report.ts` and the queues' expectations. **Implemented 2026-11-06 (branch `feature/unseen-routing-bars`):** R1-R6 are rows in `tools/harness/gate2-report.ts` (`routingBars`, thresholds in `BAR.r*`, all judgement), `routingFailFast` `{ ms: 300, scope: "unseen" }` is on the BP A/B and whole-run jobs of `golive-unseen-gate2`, the BP pace jobs and `bp-upark-ab*`, and the `unseen-park.json` stop rule names R1-R6 (docs/243 §7). A bar FAILs only when pooled and in both orders; one order is a log row. Counters 2 (park release burst, `unseen_park_burst_max` [824]) and the stagger deferral [825] are built; 1, 3, 4 are not (below). Pooled per world over both orders. Sim-hours from `g8_simhours_x100`.

| Bar | Metric | FAIL when | Baseline (off) |
|---|---|---|---|
| R1 plans | `backoff_plans` per Sim-hour | up > 10% pooled **and** up in both orders | BP 2.04, IP 0.80 |
| R2 route work | `asyncUs` per Sim-hour | same rule | BP 9.5, IP 8.7 ms |
| R3 length proxy | `asyncUs` / `asyncStarted` | up > 15% pooled and both orders, with R2 up too | BP 4.6, IP 10.9 ms |
| R4 fails | `backoff_failures` / `backoff_plans` | > 1.5 x off + 5 events | 0.18% BP, 0.37% IP |
| R5 300 ms | `ufLong`, route-led hitch >= 300 per 10 min | on > off + 2 per pair; hitch-ab clear-regression rule | BP 0, IP 3 per ~25 min |
| R6 bursts | p99.9 of `routes + routeOtherThreads` per second | > 1.25 x off | BP 72, IP 39 |
| log | `routes` per Sim-hour, unseen-marked share, `asyncRefused`, `gohome_runs`, `visitlot_runs` | never | |

The thresholds are judgement from one data set: adjust after the first pair. The both-orders rule is what guards against §1's artefact.

| Queue | Add |
|---|---|
| `golive-unseen-gate2.json` `*-gu2-t-ab`, `-onfirst` | R1-R6 as bars for default ON |
| `*-gu2-fastspeed-ab`, `*-gu2-speed1-ab` | Cut 2026-11-06 (docs/222 §11.9): no bar read them. `*-gu2p-speed1-ab` in the pace queue is the only look left at player speed, if V6 fails |
| `*-gu2-out-off` / `-on` | R1, R2, R4 whole-run, same seed |
| `golive-unseen-gate2-pace.json` pace 50 / 35, `credit` | R1 and R4 only, `log` (single order). A pace that restores seen cadence but raises plans > 10% does not go into `gate2`. Add `-onfirst` for the chosen pace before relying on it |
| `unseen-park.json` | R1-R6 replace the `routes/s` stop line. Add a pair with `autunseen` on. Read `unseen_park_snap` against camera jumps in `unseen-park-loop` |
| `routingFailFast` | `{ ms: 300, scope: "unseen" }` on BP jobs of `gate2` and `unseen-park`. **Not on IP:** IP has 2-3 marked plans >= 300 ms per arm set in both arms, so the job would stop on baseline; use R5 there. None of these queues has the field today |

**Missing counters** (reserve slots at build time; none built here):

1. **Route length by class.** Sum and count of `Route.GetDistanceRemaining()` at a successful plan, seen / unseen, plus a >= 40 m bucket. Site: `RouteSrcEnd`. The one thing that closes "farther picks".
2. **Park release burst.** Built as `unseen_park_burst_max` (most snap releases in one tick) with the stagger of docs/243 §9; slots 824-825.
3. **Picks per arm.** `{"t":"picks"}` is cumulative; per-slice snapshots would show whether trip definitions gain share. Not read here.
4. **Plans ending in view.** Routes that start unseen and end seen, for the lot-edge question.

## 7. Not verified

- Route length: job time is a proxy, and `asyncUs` also holds lock and contention time.
- No route data from the unpaced `autunseen` build.
- Everything is ultra speed on BP and IP, 10-18 min. No SV `autunseen` pair, no long-history save, no mods, no CC.
- `[UnseenFallback]` numbers come from jobs with the fallback at test settings; `v020-natural` was not re-read.
- The cause of `routes` moving with Mode 1 is unknown.
- `unseenpark` read as uncommitted code; it may change. `unseenrefresh` interplay read, not traced.
- Bundle "mechanism unproven" means existing counters do not show one. A real effect under ~5% on BP plans cannot be excluded with 2 runs.
- `kUnseenFallback*` True defaults are read from `build-fixes.ts`, not from a release package.
