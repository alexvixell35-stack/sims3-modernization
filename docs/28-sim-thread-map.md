# 28. Sim-thread map: who owns the work, what blocks moving it

PLAN lane H item H2, first step of [docs/23 #17](23-ideas-and-roadmap.md) (sim-thread offload architecture). Written 2026-10-15 from what the repo already has; the game was not run for this note. Steam TS3W.exe 1.67, image base `0x400000`.

Tags: **[verified]** = read in a decompile, disassembly, decompiled C# or the exe's strings; **[measured]** = from sample or timing files named below; **[community-reported]** = from other projects; **[inferred]** = reasoning, still to confirm. "[to measure]" = no number exists yet.

## 0. Refresh 2026-10-25 (existing data only, no game run)

Sections 1-5 below are the 2026-10-15 snapshot; this section replaces their ranking. Effort = wall clock for Claude with agents (CLAUDE.md working style); game runs and clean nights listed apart.

### 0.1 Sources

- **Budget buckets** = docs/160 `split()` (`tools/harness/sim-budget-report.ts`, imported as is) over existing runs. The `sim-budget.json` queue itself was **never run**; the earlier "Mono ~400 / ICs ~200-235 / other ~205 / anim ~141 / route ~47" figures are its self-check on one run (`tailunlock-2026-10-22-bp-tailunlock-verify`).
- **Natural set** (used for the table): every `*-(bp|ip|sv)-(rvb|soak)-*` profile from 2026-10-23..25 (release-validate and soak, seeded natural play). Our builds (v0.1.4, candidate, v0.1.5): BP 3,655 s, IP 2,115 s, SV 8,880 s; vanilla: one 10 min run per world. `[HitchAttr]` was off in all of them, so route-job waits are **not** a separate bucket here: they sit inside Mono/ICs (script waits), anim (steering waits) or other.
- **Wide set** (95 runs, 2026-10-20..25, repros and A/B arms included) for the "other" method checks and the simstack counts.
- Autonomy, Mono-leaf and hitch causes from docs/118 §8 and docs/161 §3; route-job waits from docs/189 §1 (M1-M3); unseen agents from docs/143; scripts from docs/144/129; Apex ownership from docs/187.

### 0.2 Budget per world (natural set) [measured timers; Mono/IC split inferred from managed samples]

| Bucket (ms/s) | BP ours | BP vanilla | IP ours | IP vanilla | SV ours | SV vanilla |
|---|---|---|---|---|---|---|
| Mono script (incl. waits taken from script) | 375 | 396 | 363 | 351 | 387 | 392 |
| ICs (native under script) | 279 | 192 | 225 | 134 | 223 | 170 |
| route sync plans on the sim thread | 41 | 105 | 79 | 269 | 19 | 83 |
| GC + finalizers (timed) | 7 | 8 | 10 | 8 | 13 | 11 |
| anim + locomotion + publish | 116 | 109 | 116 | 75 | 104 | 88 |
| other (untimed) | 181 | 190 | 207 | 164 | 253 | 255 |

Route plans left the sim thread (vanilla 105 / 269 / 83 -> 41 / 79 / 19), but part of that time came back as waits behind the job inside ICs and anim (ICs +87 / +91 / +53 vs vanilla). Single-run vanilla columns, so read the direction, not the digits.

### 0.3 The "other" bucket (~181 / 207 / 253 ms/s)

- **It is real time outside every timed stage, not tool cost** [measured]: wide set by profile window, BP 170 / 173 / 170 ms/s (managed sampler on / native sampler on / none), IP 173 / 182 / 179, SV 250 / 255 / 260. Vanilla has the same size (190 / 164 / 255), so it is EA's loop, not our hooks.
- **It scales with iterations** [measured fit, natural set rows of ~1 s]: BP 102 + 1.78 x it/s, IP 82 + 2.00 x it/s, SV 103 + 1.71 x it/s. So ~80-105 ms/s per wall second plus ~1.7-2.0 ms per sim iteration; SV is largest because it runs ~89 it/s (BP 47, IP 62).
- **What runs there** [verified call order, docs/36 §2 and docs/82 §"Sim iteration order"]: `ServiceList_SimUpdate 0x59ED70` (two to four calls per iteration: UTFWin `WindowManager_UpdateVisuals 0x50DE70`, audio service `0x869000`, camera, the other services; ~14 ms/s incl. in docs/36), the pre-locomotion step `0x7FD520`, `0x76C5E0`, `0x769420`, `0xD7FE80`, `RunCatchUpSteps`, the publish wrapper `0x76C8A0` (only the inner `0x593590` is timed), the `[host+0xB78]` callback, the remote-call pump at `0xD81FC3` (JobManager jobs, 0.29% incl. in docs/36 §7), and **pacing**: `IdleSimulationCycle 0x7694B0` plus the sleeps at `0xD81AF7`/`0xD81B16` (idle, not cost).
- **The simstack sampler cannot split it** [measured, this refresh]: 73-83% of sim-thread samples hit the 24-frame cap, only 5.0-5.9% carry any timed-stage frame and 2-5% reach `Simulate` at all. sim-budget-report's "other" contributor list (leaf of samples with neither a timed nor a Mono-range frame) is therefore a sample of truncated deep stacks (allocator `0x4E5E40`, height ray `0x67E2B0`, quadtree `0x681CF0`, `Query_GetObjectsOnLot 0x7499B0`, object hash `0x939100`), mostly work from inside the script stages; do not read it as the remainder's content. The few untimed samples that do reach `Simulate` (20 / 29 / 43 in the rel015 runs) sit at `Simulate+0x40A` (the `0x7FD520` chain) and `+0x453` (services: UTFWin visuals, audio), then the per-frame GC chain `0xE49FB0 -> 0xE49F10 -> 0xE49DA0` (no `0xE4A050` frame ever appears: the Frida hook trampoline hides it, so whether GC time leaks into "other" is open [inferred]), publish and the pump.
- Short-stack OS waits are 11-17% of untimed samples: part under the arena lock wrapper `0x4E69D0` (~2-4 ms/s) and `Semaphore_Wait 0x4E2760` (< 1 ms/s) [measured share, ms inferred]; the rest has no caller frame.
- **Hitch relevance** [measured frames]: "other" leads 10 of 262 frames >= 300 ms in the natural set (4%). The 6 frames >= 1 s it leads are one SV soak episode (`overnight-1023-...-sv-soak-cand`, 1.2-2.3 s each, script stages <= 15 ms), which the night note attributes to own-START route jobs with the sim in `WaitAsyncDone`/`WaitJobDone` (`docs/data/results/2026-10-23-queue-overnight-1023.md`), i.e. route waits taken at an untimed point, not loop overhead.
- **Verdict: LEAVE** as a target (EA loop overhead, pacing, UI services = Apex's UTFWin/publish area). To split it for real, add four `stutter-profile.js` timers (`0x59ED70`, `0x7FD520`, the pump call `0xD81FC3`, `0x7694B0`, ~1 h) next time that script is touched, and run `sim-budget.json` with `[HitchAttr]` so route waits get their own bucket.

### 0.4 Who leads the hitches (natural set, 286 profiled minutes) [measured frames, cause split per docs/160]

Frames >= 300 ms: 262 (BP 19, IP 142, SV 101). Route sync plans lead 118 (45%; IP 90 of 142), script 107 (41%), anim/locomotion 26 (10%; SV 19), other 10 (4%), GC 1. Frames >= 1 s: 17 (BP 0, IP 1, SV 16): route plans 8, route-job waits at untimed points 6 (above), script 3. This agrees with docs/161 §3 (sync plan fallbacks lead 12 of 15 >= 1 s) and docs/118 §8.3 (31 of 64 >= 300 ms script-led, 7 of 7 >= 1 s sync `Route.Plan` fallbacks). In the wide set, where `[HitchAttr]` was on in many runs, route-job waits lead 25% (IP) and 43% (SV) of frames >= 300 ms, so part of this table's "script" and "anim" share is waiting on a route job.

### 0.5 Refreshed table

Owner: **us** = routing, script, Mono; **Apex** = engine, visuals, render, lookups, allocator, memory (docs/187); **EA-only** = no realistic patch. ms/s are BP / IP / SV.

| # | Subsystem | ms/s now | Leads >= 300 ms / >= 1 s | Status | Owner | Verdict |
|---|---|---|---|---|---|---|
| T1 | Route sync plans on the sim thread (fallbacks `NoStart`/`Foreign`/`Los`, own-task sync plans, docs/141 class-c callers) | 41 / 79 / 19 [measured] | 45% / 8 of 17 (+6 job-wait frames) | in progress: `[RouteAsync]` shipped; `FallbackAsync`, `PlanDeadlineMs`, `FbJobDeadline`, `JobYield`, `stuckcheckcap` built, partly run | us | **OFFLOAD** (finish: no multi-second plan on the sim) |
| T2 | Sim waits behind route jobs (`+0x918`, `A_RUNNING` gates, drains, result waits) | keyed BP 68-92, IP 73-99; IP `asyncPumpUs` 74 [measured, docs/189 M1]; SV not split (steering ~48% of keyed) | wide set: IP 25%, SV 43% of >= 300 ms | in progress: P5/P5c, RO clones (S2, AvoidRO, Gate0RO, SlotEnabledRO, RouteToObjRO, PlacementRO), P6 phases 1 and 3 built, not run | us | **MAKE CHEAPER IN PLACE** (P6 `A_HOLD` + take rate; lock-taker bundle) |
| T3 | Mono interpreter + runtime (all C#) | managed leaf 327 / 317 / 377 [measured share, docs/118 §8.1]; budget bucket 375 / 363 / 387 incl. waits | script 41% (with ICs) / 3 of 17 | must stay (M); fast paths designed, sub-run fusion built in monohost | us | **MAKE CHEAPER IN PLACE** (docs/139) |
| T4 | Script one-tick batches (Overwatch nightly, ErrorTrap load scan, EA 3 AM / pet pool / decay, stuck checks, dispose) | small per second; 0.3-0.9 s per event [measured, docs/145-147] | most of the script-led >= 300 ms (ThreadedObjects / TaskObjects bursts) | built, off; disposespread verify PASS, the rest not run | us (+ NRaas upstream patches) | **MAKE CHEAPER IN PLACE** (spread) |
| T5 | Locomotion (agent update, steering, avoidance, footprints) | locomotion stage 72-79 of the anim bucket [measured, rel015]; unseen Sims 94% of agent updates [measured, docs/143] | anim 10% (SV 19 of 101) / 0 | phase 1 + 2 throttles built, not run | us (routing) | **MAKE CHEAPER IN PLACE** (unseen throttle) |
| T6 | Autonomy scoring | 65 / 55 / 88 [measured share, docs/118] | 0 named | candidate: `[AutShadow]` pilot designed, M5 open | us | **OFFLOAD** (steady ms/s, not a hitch fix) |
| T7 | Unseen script work (broadcasters, environment score, motives) and the docs/129 five | broadcasters ~4-8 BP; docs/129 five ~37 total [inferred from samples] | 0 | docs/144 built, off, not run; docs/129 design | us | **MAKE CHEAPER IN PLACE** now (docs/144), **OFFLOAD** later (docs/129 with `[MonoThread]`) |
| T8 | ICs (queries, SACS requests, slots, `FindGoodLocation`, non-plan route ICs) | 279 / 225 / 223 incl. waits inside ICs [measured remainder x sample share] | inside the script row | route ICs covered by RO clones; `kSpatialCache` | us (route ICs), Apex (generic lookups) | **MAKE CHEAPER IN PLACE** (route ICs via T2) / **APEX** (lookups) |
| T9 | Animation (SACS pass, pose pass) | `animPose` 42-55, `animSacs` ~0.2 [measured] | inside anim row | must stay (synchronous clip events) | Apex / EA | **APEX** |
| T10 | Untimed loop ("other": services incl. UTFWin visuals, pre-locomotion step, publish wrapper, pump, pacing) | 181 / 207 / 253 [measured remainder] | 4% / 0 (the 6 are route waits) | EA loop | EA + Apex (UI, publish) | **LEAVE** (add timers, §0.3) |
| T11 | GC + finalizers | 7 / 10 / 13 timed [measured] | 1 / 0 | `[GC]` heap tuning in place | us (Mono runtime) | **LEAVE** |
| T12 | EA arena allocator | 6.9% of samples (N6) | - | `[Alloc]` cache exists | Apex | **APEX** |
| T13 | Lookups: object-id hash, lot/world queries, height ray, package probe (N9, N10, N20, N21) | ~2.4 + 1.9 + 0.7 + 4.0% of samples (2026-10-15) | - | designs H5, C2, C3, R1 | Apex | **APEX** |
| T14 | Audio update | ~4 (N19) | - | mixing already off-thread | EA | **LEAVE** |
| T15 | Saves (episodic) | 11-12 s per save | save stall | `[SaveWrite]`, `kFastRefTable`, `[FootprintIndex]` shipped | us | **LEAVE** (done) |
| T16 | Lot streaming through the pump | [to measure] (N16) | - | §5.5 probe built, not run | Apex (scene) | **APEX** (measure first) |

### 0.6 Top 10: estimated gain and effort

| Rank | Item | Gain | Effort |
|---|---|---|---|
| 1 | T1 own stuck-check job cap + `PlanDeadlineMs` + `JobYield` ship-on decision | SV/BP freezes up to ~9 s from own probe jobs: 10 -> 2 waits >= 1 s in the sccap pair [measured, PLAN night-1024] | ~1-2 h + 1 night |
| 2 | T1 fallback finish: `FallbackAsync`/`FbJobDeadline` defaults, `LogFallbackStack=16`, then docs/141 class-c callers | the rest of the >= 1 s freezes; IP route bucket 79 -> ~40-60 ms/s [inferred] | 2-4 h + 1 night; class-c fixes 4-8 h + 2 runs |
| 3 | T2 lock-taker bundle A/B (SteerRO, AvoidRO, Gate0RO, DeferFootprint2, RouteToObjRO, PlacementRO, ForeignKinds; built 0.36.29) | IP/SV 300 ms-2 s waits; F-G placement ~110 ms each [measured, docs/174] | ~1 h + 1-2 nights |
| 4 | T2 P6 phases 0-3 integration + IP ABBA | IP wait behind jobs ~74 -> ~15-25 ms/s, -6..-8 frames >= 300 ms per 10 min [inferred, docs/189 §3.6] | ~11-17 h + ~6 runs |
| 5 | T4 script batch spreads (Overwatch, ErrorTrap, EA maintenance, `stuckchecknear`, `failrepeat`) | 0.3-0.9 s one-tick hitches per event -> spread [measured cost, gain inferred] | ~1-2 h + 1-2 nights |
| 6 | T1 route early-out acting Mode 2 (docs/142) | failing multi-second plans -> ms (provable A* time counted by the probe) [inferred] | ~6-10 h + 2 runs |
| 7 | T5 unseen agent throttle phase 1 + 2 | BP rate ~15-35, footprint ~4-12, cars ~3-10 ms/s, not additive [inferred, docs/143 §8.7] | ~1 h + 2 nights (19 jobs) |
| 8 | T7 unseen script throttle (docs/144) | broadcasters ~4-8 ms/s BP + env score / motives on the unseen high-LOD share [inferred] | ~1 h + 1 night |
| 9 | T3 Mono fast paths (sub-run fusion port + call/return) | +17..+38 ms/s steady, no hitch effect expected [inferred, docs/139] | ~11-20 h + 2 runs |
| 10 | T6 + T7 autonomy pilot / tier 2 + docs/129 five on a second Mono thread | net +47..+135 ms/s steady [inferred, docs/118 §8.2] | ~80-130 h, many runs |

### 0.7 Next after v0.2.0, ranked by player-visible hitch impact per hour

1. Stuck-check cap / `PlanDeadlineMs` / `JobYield` ship-on (rank 1): built, biggest freezes, ~1-2 h.
2. Lock-taker bundle A/B (rank 3): built, IP/SV 0.3-2 s waits, ~1 h + nights.
3. Script batch spreads (rank 5): built, the script-led >= 300 ms, ~1-2 h + nights.
4. Fallback finish + class-c callers (rank 2): the remaining >= 1 s plans, ~6-12 h.
5. P6 phases 0-3 (rank 4): the most IP frames >= 300 ms, but ~11-17 h.
6. Route early-out Mode 2 (rank 6): ~6-10 h.
7. Unseen agent and script throttles (ranks 7-8): steady ms/s, few hitches, ~2 h + nights.
8. Measurement, before picking beyond this: `sim-budget.json` with `[HitchAttr]` (1 night) + the four "other" timers (~1 h).
9. Mono fast paths (rank 9), then the second-thread offloads (rank 10): steady throughput, no hitch effect in the data.

## Sources and units

- **Native share:** docs/20 §2, 15 pooled `/simstack` profiles (9,890 samples, BP/SV/IP), nearest owner. Self time is reliable, inclusive time approximate (24-frame cap, stack scan) [measured].
- **Managed share:** pooled for this note from all 22 `probe-out/profile/*managed*.jsonl` files (16,745 samples: BP 8,667, SV 2,402, IP 5,676). A sample counts for a row if a matching method is anywhere on its C# stack, so rows overlap (inclusive). 27.0% of samples had no script running [measured]. The script for this count stayed in the session scratchpad.
- **Timers:** docs/04 F5/F7b/F13 (`stutter-profile.js`: route ms/s, GC ms/s), docs/15 and docs/25 (save phases), `docs/data/results/2026-10-14-save-matrix.md`, docs/21 (CAS).
- Both samplers take wall-clock samples at 5 Hz, so **1% of samples is about 10 ms per second of sim-thread time** (waits included) [inferred from the sampling method].

## How the sim thread is organised (what every row inherits)

- One loop, `MonoScriptHost::Simulate` `0xD81840`: script tick `0xD80BC0` (every script task), GC step `GC_try_to_collect` `0xE4A050` (call `0xD819AA`, ~1 ms adaptive budget), finalizers `mono_gc_invoke_finalizers` `0xE697F0`, two animation passes (`0x571AD0` at `0xD81A70`, `0x571B90` at `0xD81C73`), locomotion `0x83CED0`, render publish `0x593590`; up to 10 catch-up steps per frame (docs/04 F2, docs/22 §1) [verified]. Scheduler details in [docs/36](36-scheduler-jobs.md): only ThreadedObjects has a time slice (`5000 x max(1, time scale)`, round-robin, checked between tasks); the remote-call pump at `0xD81FC3` has none and also drains the JobManager's shared "any thread" queue; engine services run a sim-thread slot (`+0x20`, ~1.4% incl.) [verified, measured].
- **Scripts are cooperative microthreads on this one OS thread.** Every `ITask` yields only at `Simulator.Sleep` or yielding native calls; no script assembly uses `System.Threading`, `lock` or `Monitor`, so script state has no locking at all (docs/04a §1) [verified-in-code].
- **Mono is pinned to this thread in practice.** The interpreter keeps its `ThreadContext` in one TLS slot (`[0x11f4160]`) and the current task in a global (`[0x11f4170]`); the host keeps a global task id at host`+0xBD0` that a worker call zeroed and so crashed (docs/04 F6a, docs/18 0.7.1) [verified]. Boehm's Win32 thread table is compiled in (the H2 string check missed the lowercase "too many threads", "SuspendThread failed"), but only the thread that runs `GC_init` is ever registered (`GC_new_thread` `0xE51070` has one caller), EA's `GC_push_other_roots` `0xD71820` scans only the collecting thread's own stack, and the `GC_malloc` fast path `0xE4BE30` takes no lock [verified, docs/29 §3]. The TLS slot is per thread and method compile is locked, so those two are not blockers [verified, docs/29]. **Any C# that runs off the sim thread today is unsafe; that is why the route worker proxies every managed call back** (docs/18).
- So the blockers below fall into five kinds: **(M)** Mono/script affinity, **(S)** shared unlocked state, **(L)** the routing lock `RoutingWorld+0x918` taken every tick, **(E)** synchronous events/callbacks into other systems, **(O)** ordering (results needed in the same tick).

## 1. Per-second work (live mode)

Costs are pooled over the three worlds unless a world is named. Rows within a family overlap.

### 1a. Native

| # | Work | Owner | Cost | Blocker | Verdict |
|---|---|---|---|---|---|
| N1 | Mono interpreter (all C#) | `0xE53EB0` (`ves_exec_method_with_context`) | 28.5% self (~285 ms/s) [measured, docs/20] | M | **Not movable.** Make cheaper: hot-method fixes (done: walk style, broadcaster, go-home), JIT study (H4) |
| N2 | Mono runtime (type compare, IC trampoline, metadata) | `0xEA8720` 2.2%, `0xDA5970` + ntdll locks ~1.5%; ranges `0xE20000-0xEF0000`, `0xD82000-0xDC0000` | 14.8% (~148 ms/s) [measured] | M | **Not movable** (rides N1) |
| N3 | Route search (land + water) | Planner `0x65E0D0`, world A* `0x676260`, lot portal A* `0x63E340`, cell A* `0x61E500`, water JPS `0x620560`/`0x61FE10`; ICs `Route_Plan`, `ReplanFromPosition`, `PlanFromPoint`, `Replan` | 14.5% native (IP water JPS alone 7.8%); 16.8% of managed samples under a route IC (IP 23.3%); timers 95 / 82 / 246 ms/s BP/SV/IP base, 108 / 77 / 184 with all fixes [measured, docs/04 F7b, F13] | L (taken every tick by locomotion); S (goal step `0x7606E0 -> 0x75C2D0 -> 0x63D2F0 -> 0x61E320` runs outside the lock on the per-lot search object `0x62EAB0`; more unlocked lot state `0x6255B0`, `0x62E420`/`0x63D7B0` [inferred]); M (`mono_runtime_invoke` callbacks during a plan, host call `0xD75FA0`); O (all 278 callers need the result at once) [verified, docs/18, PLAN] | **Movable with a routing gate** (worker owns `+0x918` for the whole job, every unlocked entry takes it too). Worker mode 1 (sync) is stable, 11,523 plans, 0 crashes, no gain; async is paused after 3 crashes. Overlap left = only non-routing work, realistic 15-70 ms/s BP, 35-125 IP (docs/18) [inferred]. Meanwhile cheaper in place: `[WaterJps]` 3.9x per jump (shipped) |
| N4 | GC incremental mark + forced collections | `GC_try_to_collect` `0xE4A050`, mark `0xE4C3E0`, `0xE4B8D0`; stop fn `0xD717B0` | 9.9% of samples (SV 17%); timers mark 7-14 ms/s, forced 2.8-3.6 ms/s with fixes, forced ~0 with ts3native `[GC]` heap 352 MB [measured, docs/04 F13, F15] | M (stop-the-world, sim-thread stack only, no parallel marker in this build [verified, docs/13]) | **Not movable.** Would need a rebuilt GC with thread support and parallel marking; out of reach. Keep in-place tuning (`[GC]`) |
| N5 | Finalizers | `mono_gc_invoke_finalizers` `0xE697F0` | ~1.8 ms/s, plus a whole-queue drain after 200 frames with >= 50 pending [measured, docs/04 F5; verified F2] | M (runs managed finalizers) | **Not movable**; spread the drain instead [inferred, idea] |
| N6 | EA arena allocator | `0x4E5E40`, lock wrapper `0x4E69D0` | 6.9% (BP 9.9%, up to 16% with the route worker on) [measured] | Called by everyone (not a unit of work) | **Not an offload target.** Thread cache `[Alloc]` in place (+19% it/s A/B, crash suspect, PLAN) |
| N7 | Animation update (two passes) | `0x571AD0` (SACS driver `0x94E9C0`), `0x571B90` -> time step `0x942C00` -> rig pose `0x940430` | 3.3% (~33 ms/s) [measured] | E (clip events fire synchronously: parent/unparent, sound, VFX, destroy prop; list mutates during the walk); S (non-atomic refcounts, global controller id counter); L (calls into locomotion/routing) [verified, docs/22] | **Not movable as a parallel-for.** Only the blend slice `0x5833C0` is parallel, needs a C++ rewrite of `0x940430`, gain <= 10-20 ms/s [inferred]. H3 done (docs/22 §7): the eval slice has no events, Mono or routing, and its shared refcounts are atomic; cross-actor controllers (`0xFC2068`) need a serial tail. Realistic 5-15 ms/s. Parked until the §6 split is measured |
| N8 | Locomotion / obstacle avoidance | `0x83CED0` -> `0x957210` -> `0x847390`/`0x85D980` | 2.4% [measured] | L (holds `+0x918`), S (writes agent state), O (same tick) [verified, docs/18] | **Not movable** |
| N9 | Object-id hash lookup | `0x939100` (`% 0x409`), `0xD726F0` | 2.4% [measured] | Called ~300 ICs, too fine-grained | **Not an offload target**; bigger table or last-hit cache [inferred] |
| N10 | Height raycast | `0xC2A570` -> `0x67EDE0`/`0x67EA80`/`0x67E740` -> `0x67E2B0` | 1.9% (IP 2.5%) [measured] | Called from route planning (N3): A* edge test `0x627780`, path-node pass `0x64C7C0`, through the height queries `0xC6A250`/`0xC6A540` [verified]. Pure: reads only terrain kd-trees, which only `0xC2A840` rebuilds [verified]. **But** `0xC2A840` runs on the main thread in live mode (`WorldTerrain_Update` `0xC845C0` on terrain chunk LOD changes) and frees the old cell shape with no lock while sim-thread rays may hold it: a use-after-free window ([docs/38](38-world-terrain-lots.md) §6, C1) [verified code, overlap inferred] | **Moves with N3**; in place: exact-key height cache at `0xC2A570`, invalidated at `0xC2A840` (H5 GO, docs/20 §4b): covers 0.71/0.35/1.21% BP/SV/IP, ~3.5-5.5/2-3/6-10 ms/s [inferred]; not built |
| N11 | Footprint quadtree | `0x681E00` -> `0x681CF0` | 1.3% [measured] | Called from route planning, locomotion and C# range queries; holds physics items of moving Sims and objects ("Physics/RangeShapeResult") [verified] | **Moves with N3** for its routing share; no cache (H5 NO-GO, docs/20 §4b: results change every tick and centers differ per caller [inferred]); keep the F12 throttle |
| N12 | Lot object query | `Query_GetObjectsOnLot` impl `0x7499B0` | 1.1% native, 1.8% managed [measured] | O (callers use the result at once) | **Not an offload target**; per-tick cache `kSpatialCache` built (docs/20 §4a) |
| N13 | Sim-to-render publish | triple buffer `0x593590` | 0.6% (wait) [measured] | Sync point by design | **Not movable** |
| N14 | Line of sight | `0x6814B0` | 0.4% [measured] | Called from `CanSeeObject` in C# | Throttle with broadcasters (F12) |
| N15 | Resource decode (RefPack) in live mode | core `0x4EB3B0`, only call `0x4EC065`, under global resource lock `0xFB2CD0` and the package lock `0xFB24F8` on the sync path; async loads decode on the AsyncLoader job thread ([docs/39](39-resource-system.md)) | 0.24% [measured, docs/15 Phase 6; 0.23% in the docs/39 pooled split] | S (resource lock); O (caller waits) | **Movable with prefetch** (decode ahead on a worker, docs/08 §"use all cores") but worth ~2 ms/s in live mode; `[Decompress]` faster in place (shipped) |
| N16 | Lot streaming (objects into the scene) | `WorldManager::Update` `0xC6D570`, `Lot::AddLotObjectsToScene` `0xAC1130` | [to measure]. Threads now known (§5, G13): lot scoring on the **main thread**, object add/remove on the **sim thread** through the remote-call pump at `0xD81FC3`, which has no time budget [verified] | E (per-object scene events run synchronously, script message scope) [verified call shape]; O (whole lot in one pump) | Measure ms per activation with the §5.5 probe first. Existing samples: 0 lot add/remove samples, JobManager 0.29% incl., jobs run in the pump 0.10% (no pans recorded) [measured, docs/36 §7]; a pump deadline is a small patch because `RunJobs` supports one (docs/36 C1). Full lot lifecycle and threads: [docs/38](38-world-terrain-lots.md) §5 |
| N18 | Physics/collision module (total; contains N10, N11, N14) | `0x676F70`-`0x687440`: kd-tree height ray, loose quadtree range and ray (`0x681E00`, `0x681820` -> `0x6814B0`), 2D polygon tests ([docs/33](33-physics-map.md)) | 3.2% self (BP 3.1%, IP 3.8%, SV 1.9%; kd-tree ray 1.8%, quadtree 1.0%) [measured, docs/33 §6] | S (unlocked quadtree, global free lists, non-atomic shape refcounts; writers marshal to the sim thread, readers have no check), O [verified]. N14's ray recursion is also reached from the route planner (`0x6583E0` -> `0x686CC0`), not only `CanSeeObject` [verified] | **Moves with N3** (no work of its own). Route worker: proxy `0x686CC0` like `0x83B3A0` before async (docs/33 §5); in place: `[HeightCache]` for the kd-tree ray |
| N17 | OS waits, MSVCR80 memcpy, unattributed | ntdll 5.3%, MSVCR80 2.2%, no family 3.0% | ~10% [measured] | n/a | n/a (part is route-worker waits in `bp-worker1`) |
| N19 | Audio: per-frame `Update` (listener, ambience, emitter positions), command building from ICs and animation sound events | `Update` `0x865E10` -> `0x8B4730`, ambience `0x862C20`, emitters `0x9436A0`, anim sound `0x583050` -> `0x9442D0`, event writer slots on vtable `0x10289E8` | 0.41% self (~4 ms/s; BP 3.7, IP 3.1, SV 7.5) [measured, docs/35 §3] | S, O (object, lot and room reads for positional sounds) | **Already offloaded by design**: mixing, decoding and DirectSound run on the `RWAudioCore Dac` and EA audio threads behind a lock-protected triple buffer; handles are caller-side; nothing waits [verified]. Rest movable gain ~1-2 ms/s [inferred]: not worth it ([docs/35](35-audio-offload.md)) |
| N20 | Package probe per resource cache miss (linear walk of all registered packages in priority order) | `GetPackageSource` `0x4AFFC0` -> per-package `DBPF_IndexCompressed_Find` `0x4AA1B0`; plus index key enumeration `0x4AC9C0` for `GetKeyList` (CAS part search) | 0.67% self (~7 ms/s; BP 0.74, IP 0.58, SV 0.75) + 0.17% enumeration [measured, docs/39 §7] | S (`g_ResourceLock` + `g_PackedFileLock`), O (caller needs the resource now) | **Not an offload target; make it cheaper in place**: a key -> database map built at package registration (docs/39 R1) [inferred gain]. Grows with package count (CC) |
| N21 | World/lot queries (lot transform, lot lookup by id and by point, room and tile lookups, lot managers) | `Lot_WorldToLocal2D` `0xAB8540` (fsin+fcos per call), `WorldManager_FindLotById` `0xC60D30` -> `SceneNode_FindRecursive` `0xC5FA60` (linear tree walk), `WorldManager_LotGridLookup` `0xC688A0`, `0xA8DF80`/`0xAB7ED0` ([docs/38](38-world-terrain-lots.md)) | ~4.0% self in all world/lot/terrain code; named: 0.60% (IP 0.85%), 0.38%, 0.22%, 0.15% [measured, docs/38 §7, 16 simstack files] | S (lot grid rebuilt lazily without a lock), O (callers use the result at once) [verified] | **Moves with N3** (route cost and height queries are the main callers); in place: sin/cos cache (C2) and id-to-lot hash (C3), ~7-13 ms/s together [inferred] |

### 1b. Managed (script tasks on the sim thread)

All of these share blocker **M**: C# cannot run off the sim thread today (previous section). Shares are % of all managed samples [measured, this note]; BP / SV / IP in brackets.

| # | Work | Owner (C#) | Cost | Other blockers | Verdict |
|---|---|---|---|---|---|
| S1 | Interaction execution | `Sim.Simulate` -> `Sim.DoInteraction` -> `InteractionQueue.ProcessOneInteraction` | 29.3% incl. (28 / 22 / 34), includes most routing and animation calls | S (writes Sim, object, lot state), yields mid-method | **Not movable** |
| S2 | Autonomy scoring | `AutonomyManager.Simulate` (one search per tick), `Autonomy.ScoreInteractionsForLocalAutonomy` (never yields), `ScoreInteractionsForMetaAutonomy` | 11.1% (11 / 13 / 10); task itself 5.5% | S (reads every object, Sim and interaction on the lot while other tasks mutate them between yields); E (interaction `Test()` is arbitrary script, mods included); O (the pick runs in the next queued step) [verified-in-code, docs/04a §4] | **Movable only with a second Mono thread plus a world snapshot.** Not now. Biggest managed prize; in place: fewer or cached scorings |
| S3 | Lot update, reaction broadcasters | `Lot.Simulate` -> `Lot.UpdateReactions` -> `ReactionBroadcaster.PulseSims` / `GetInRadiusSims` | 10.2% (11 / 7 / 10); broadcasters 4.5% (6 / 1 / 4) after the F12 throttle | S, E (reactions push interactions) | **Not movable**; throttled (F12) |
| S4 | Sim update (motives, buffs) | `SimUpdate.Simulate`, `Sims3.Gameplay.Autonomy.Motive*` | 8.0% (8 / 10 / 7); motives 4.2% | S (writes motive state); self-throttled to 2 high-LOD Sims per tick [verified-in-code, docs/04a] | **Not movable** (pure math part is small); batching is an in-place idea [inferred] |
| S5 | Go-home and lot trips | `GoHome.Run`, `GoToLot`, `VisitLot` | 5.8% (6 / 3 / 7), mostly their route calls | as N3 | Covered by N3 and the off-screen shortcuts |
| S6 | Animation requests from script | `StateMachine*` / `Animation*` ICs | 4.6% | E, O (yielding native calls) | **Not movable** |
| S7 | Object queries | `Sims3.SimIFace.Queries.*` | 3.9% | O | Cache (N12) |
| S8 | Outfit building in live mode | `SimBuilder*`, `CASUtils.*`, `SimOutfit.*` | 2.7% | S (global `ObjectDesignerService` lock `0x0106B874`, UI and resource manager) [verified, docs/21] | **Not movable** natively; cheaper per call |
| S9 | Walk-style rules | `SimWalkStyleRules.*` | 2.4% (was 6% before F11) | O | Fixed in place (F11) |
| S10 | One-shots, alarms, services | `OneShotFunction` 2.3%, `AlarmManager` 1.9%, `Services` 0.8% | ~5% | S, E (callbacks are arbitrary script) | **Not movable** |
| S11 | Story progression, events, situations | `StoryProgression*` 0.7%, `EventTracker.SendEvent` 0.9%, situations 0.5% | ~2% | S | Small today; story progression is a snapshot-friendly candidate if a second Mono thread ever exists [inferred] |

## 2. Episodic work (saves, menus, loads)

| # | Work | Owner | Cost | Blocker | Verdict |
|---|---|---|---|---|---|
| E1 | Save: backup copy and flush | native save `FUN_00AB1030` (CopyFileW + FlushFileBuffers, ~33 MB per file) | ~0.8 s of a ~11-12 s save [measured, docs/15] | none for the backup copy (old file, game done with it) | **Movable now:** `[SaveWrite] Mode=1` copies on a worker (built, shipped in v0.1.1) |
| E2 | Save: RefPack compression of each resource at close | `0x4EC0A0` -> core `0x4EBB90` | ~0.56 s [measured, docs/25] | O (stream writer waits for the bytes at close); the input buffer is owned by the writer [inferred] | **Movable with a deferred writer** (compress closed resources on a pool, write in order at the end) [inferred]; not designed yet |
| E3 | Save: native world write | `World_Save` `0xC6D460`, `Lot_Save` `0xACB8A0`, footprints `0x6663C0` (~1.2 s, quadratic), object groups `0xC6F520` (~0.76 s) | ~4 s [measured, docs/25] | S (reads the live routing world and lots) | **Not movable**; cheaper in place (`[FootprintIndex]`) |
| E4 | Save: script serialization | `ScriptCore.PersistWriter`, `ReferenceHashtable` (~2.5 s), reflection (~1.3 s) | ~6.3 s [measured, docs/15/26] | M (walks the live managed heap) | **Not movable**; cheaper in place (`kFastRefTable` BP 12.7 -> 10.3 s) |
| E5 | Save: lot imposter jobs | `LotImposter_Build` `0xADAF60`, wait `JobManager_WaitLocked` `0x59A220` | ~0.13-0.24 s wait [measured, docs/25] | Already a JobManager job; the sim thread waits for it | Already off the thread; only the wait remains |
| E6 | Create-a-Sim category fill | `CASHair.PopulateTypesGrid` (per tile: `LoadLayout`, `ObjectDesigner.SetCASPart`, preset XML) | hair 3.2 s = ~1 s tab switch + ~2.2 s tile loop [measured/inferred, docs/21] | M, S (UI window system, `ObjectDesignerService` lock) | **Not movable**; incremental fill + per-part memo built in ts3fixes (docs/21 §6). Thumbnails already async |
| E7 | Catalog (OBJD) decode | loader `0xC922C0` into singleton `[0x11ED034]` | once per catalog load, ~8 MB for 14,577 products [inferred, docs/08 §2.5]; time [to measure] | O (the in-memory filter needs every product) | Possibly movable as a startup prefetch [inferred]; measure first (lane G item 2) |
| E8 | Lot load streaming | N16 | [to measure] | N16 | [to measure] |

## 3. Ranking: cost x feasibility

Feasibility: **now** = built or no blocker; **with X** = one named blocker to solve; **no** = blocked by M on every path.

| Rank | Candidate | Cost on the sim thread | Feasibility | Main blocker | Next step |
|---|---|---|---|---|---|
| 1 | N3 route search (incl. water JPS, N10/N11 routing share) | 95-250 ms/s [measured] | with a routing gate | L + unlocked per-lot state; Mono callbacks proxied | Measure how often `+0x918` is free during a tick (the overlap); only then revive async with the gate (docs/18) |
| 2 | E1 save backup copy | ~0.8 s per save | now (built) | none | Save matrix with `Mode=1` (PLAN next task 1) |
| 3 | E2 save compression | ~0.56 s per save | with a deferred, ordered writer | ordering of the package write | Design note: where the compressed bytes are consumed after close |
| 4 | S2 autonomy scoring (and S11 story progression) | ~110 ms/s (+~7) | no today; with a second Mono thread + snapshot | M (GC registers and scans one thread, unlocked allocation, global `[0x11F4170]`, host task id [verified, docs/29]), S, E | Static check done (docs/29 §3): not safe today; a native GC patch (register workers, multi-stack `push_other_roots`, allocation lock, per-thread `[0x11F4170]`) is ~1-2 weeks [inferred]; the world snapshot stays the larger cost |
| 5 | N7 animation eval slice | <= 10-20 ms/s of 33 [inferred] | with a C++ reimplementation of `0x940430` | E (clip events), S (refcounts, id counter) | H3 done (docs/22 §7); measure the split (docs/22 §6) |
| 6 | N15/E7/E8 resource decode, catalog, lot streaming | ~2 ms/s live; load-time [to measure] | with prefetch | resource lock `0xFB2CD0`, script `Init` on object creation | Measure lot-load streaming cost and thread first |

**Not offload targets, but the biggest sim-thread savings stay in place:** interpreter + Mono runtime (N1+N2, ~430 ms/s; hot-method fixes, H4), GC (N4, `[GC]`), allocator (N6, `[Alloc]`), query caches (N9-N12, H5), save write-path fixes (E3, E4).

### Per-stage ms/s of `MonoScriptHost::Simulate` (G12, 2026-10-15)

**S3PP's stage profiler gives no ms/s table.** `SimulatorStageProfiler` logs only single stage calls over `SimulatorSlowStageMs` (`[SIMSTALL] stage=... duration=` / `active=... still-running`) and keeps no per-stage totals [verified, S3PP 1.1 source `simulator_stage_profiler.cpp`]. The installed `Game\Bin\Sims3Performance.log` holds 53 such lines and no totals; `probe-out/*s3pp*.log` hold none. Our own `tools/frida/stutter-profile.js` does time the stages every second (`*-600-seconds.csv` columns `script`, `taskObjects`, `oneShot`, `threaded`). 20 files have those columns, 13 of them with rows. Pooled with `node tools/harness/sample-split.ts --stages` (ms per wall second, paused seconds dropped) [measured]:

| World (runs) | Seconds | it/s | tick `0xD80BC0` | TaskObjects | OneShot | ThreadedObjects | route (nested) | route search | GC mark | GC forced | finalizers |
|---|---|---|---|---|---|---|---|---|---|---|---|
| BP (7: `heightcache-ab`, `nobudget`, `wab-alloc2`, `wab-routablecache`, `wab-spatialcache`, `wab-worker`, `worker1`) | 3,504 | 30.4 | 11.4 | 74.2 | 0.2 | **617.7** | 109.8 | 85.8 | 3.6 | 0.1 | 2.3 |
| IP (2: `jps-verify`, `wab-waterjps`) | 861 | 35.3 | 11.0 | 82.8 | 0.1 | **640.5** | 197.3 | 196.6 | 4.4 | 0.0 | 1.7 |
| t14 (4, world not recorded in the files) | 1,757 | 32.1 | 10.6 | 71.7 | 0.2 | 607.7 | 110.7 | 108.0 | 3.9 | 0.3 | 1.9 |

- ThreadedObjects is **~62-64% of wall time**, so most script work (N1/N2, autonomy, routing ICs) runs in that stage. TaskObjects is 72-83 ms/s [measured]. Route time is called from scripts inside these stages, so it overlaps them and is not additive [inferred].
- The timed stages add up to ~710 ms/s (BP). That leaves ~290 ms/s that is not timed: the two animation passes (`0x571AD0`, `0x571B90`), locomotion `0x83CED0`, render publish `0x593590`, catch-up overhead and frame waits [inferred: not timed by any hook].
- Many of these are A/B runs, so the rows mix fix states. SV has no stage data.

**Run needed to complete the table:** one SV run plus one clean BP/IP run without `--ab`: `node tools/harness/profile-world.ts --tag sv-stages --minutes 10` after `cycle.ts --save Baseline-SV` (then the same for `bp-stages`, `ip-stages`). S3PP's stage profiler can stay on, because `stutter-profile.js` times its wrapper. `stutter-profile.js` now times animation (`animSacs` = `0x571AD0` at call `0xD81A70`, `animPose` = `0x571B90` at `0xD81C73`), `locomotion` `0x83CED0` and `publish` `0x593590` (new `*-seconds.csv` columns, sim thread only; profile-compare.mjs and `sample-split.ts --stages` print them), so these runs split the untimed ~290 ms/s as well [not yet run in game].

## 4. Biggest unknowns

1. ~~**Can any C# run on a second thread?**~~ Answered in docs/29 §3 (H4): **not today** [verified: one GC-registered thread, only the collecting thread's stack scanned, unlocked `GC_malloc`, global `[0x11F4170]` and host`+0xBD0`]; the TLS slot `[0x11f4160]` is per thread and compile is locked. Fixable with a native GC patch (docs/29 §3.3); the S blockers remain.
2. **Routing lock idle time.** The real gain of route offload is the share of a tick in which `RoutingWorld+0x918` is free; never measured (docs/18 step 1 probe).
3. **Lot streaming:** threads answered statically in §5 (G13); cost per lot load (N16, E8) is still not measured. Probe design in §5.5.
4. **Per-stage split of `MonoScriptHost::Simulate`**: partly answered in "Per-stage ms/s" above (G12: the scheduler stages, GC and finalizers are timed for BP/IP). Still missing: SV, plus the animation, locomotion and publish stages (~290 ms/s is not timed). The run needed is given there; queue `sim-budget.json` + `sim-budget-report.ts` now do it per world with contributors (docs/160, not run).
5. **Inclusive native costs** are approximate (24-frame cap, scanned frames not marked, docs/20 §6); route work hidden under "no family" is likely [inferred].
6. **CAS and Buy** have timings but no native or managed stacks (docs/20 §5).

## 5. Lot streaming path (G13, static, 2026-10-15)

Ghidra headless on a private copy of the H1 project (`game-copy/ghidra-ls`), read-only; the game was not run. Steam TS3W.exe 1.67. All addresses are absolute (image base `0x400000`).

### 5.1 What "lot streaming" is in this build

- **Every lot's objects stay loaded; streaming only moves them in or out of the scene.** `Lot::Load` `0xACF520` (via the vtable thunk `0xACF9D0`) builds the lot's object layer, floor manager and pending scene-node manager and ends with `AddLotObjectsToScene` called directly (`0xACE0D0` -> `0xAC1130` at `0xACE1E2`) [verified]. In live mode, the camera-driven path only calls `Lot::SetActive` `0xAC20E0`, which adds or removes the lot's scene objects [verified]. This fits M10 (panning the town costs +22 MB at most and gives it back) [measured, PLAN].
- **Lot activation = LOD switch, not a load.** Inactive lots are drawn through the lot renderer at `lot+0x364` (`Lot/LotRenderer`, created by `0xAB4A20`) [verified], which holds the imposter (`LotImposter_Build` `0xADAF60`, docs/25) [inferred: imposter shown while the lot is inactive; the swap itself is not traced].

### 5.2 The path, with the thread of each part

| Step | Function | Thread | What it does |
|---|---|---|---|
| 1. Service update | main loop `0xECA960` -> `0x588E00` -> service list `0x59ED20` -> `World/Service` vtable `0x107A358` slot `+0x1C` = `0xC7E3C0` -> `WorldManager::Update` `0xC6D570` | **Main (render) thread** [verified: the chain comes from `___tmainCRTStartup` `0xF7F0B1`; `World/Service` is registered at app init in `0xECB600`, the function that stores the main thread id] | Camera position, then step 2 unless `WM+0x258` (map view) is set |
| 2. Lot scoring | `0xC6C290` (called at `0xC6D6E4`) | Main | Returns early when the camera's mean speed over the last 10 updates is above `Camera speed threshold` (`WM+0xEC`). Otherwise scores every lot (`0xC62D80`: distance; an active lot gets `Active Lot Bias` `WM+0xE0` taken off, the JZ at `0xC63015`), sorts them (`0xC64050`), keeps the best `Max Active Lots` (`WM+0xE4`) and calls `Lot::SetActive(1/0)` on each (`0xC6C7C3`, `0xC6C7CE`). With `Throttle Lot LoD Transitions` (`[0x11ECBC0]`, default 0) and `WM+0xE4` <= `[0x1184328]` (default 12), at most one lot is newly activated per update, and only once `0xC69FF0` reports no active lot still waiting on render resources [verified code; meaning of `0xC69FF0`'s checks inferred] |
| 3. Post to the sim thread | `Lot::SetActive` `0xAC20E0` -> `PostRemoteMethodCall` `0xABE9C0` (add, fn `0xAC1130`) or `0xABEA50` (remove, fn `0xAB4C00`), selector 1, async | Main posts; nothing waits [verified] | Sets `lot+0xC1` (active) at once, then sends lot message `0x421FEFD` |
| 4. Dispatch | `0x7D98C0`: if the current thread is the target (`[0x11D8A8C]` = sim thread for selector 1, `[0x11D8A88]` = main thread for selector 0), run **inline**; otherwise make a JobManager job (`[0x11CE2CC]` vtable `+0x10` = `0x599620`) of kind 4 (selector 1) or 1 (selector 0), submit `0x59A350`, and wait in `0x59A500` only when the call is synchronous [verified] | caller | In single-threaded mode (`Multithreaded=0`, sim id = main id) everything below runs inline inside `WorldManager::Update` [verified] |
| 5. Pump | `MonoScriptHost::Simulate` `0xD81840` at `0xD81FC3`: `JobManager->RunJobs(host+0xAC0, deadline 0, until empty)` = `0x59A430` -> `0x599E60` | **Sim thread** [verified: `host+0xAC0` is the sim thread's own JobThread record, mask `0x80000004`, registered without a new OS thread at `0xD7E9E6`-`0xD7E9FD`, only when main != sim] | Runs **every** queued remote call until the queue is empty, then every job in the shared "any thread" queue (docs/36 §2.1). With deadline 0 there is no time budget [verified], so a whole lot's add runs in one Simulate pass, after the script tick, animation and locomotion and before `IdleSimulationCycle` `0x7694B0` |
| 6. Add / remove objects | `AddLotObjectsToScene` `0xAC1130`: opens a script message scope (`0x7D2DB0`, ids `0x4C55E8C`/`0x4C55EFA`, through the script host `0x7692C0` vtable `+0x18`), then `UpdateObjectSceneNode` `0xABFAC0` for each object in `lot+0x14..+0x18`. Remove `0xAB4C00` sends object event `0xC` for each object | Sim | Per object: either a deferred LOD record in a hash map (`0xABEFE0` -> `0xABE900`), or object event `0x14` through the listener chain `[0x11DC2A8 + 4*id]` (`0x936C90`) with the LOD from the lot renderer (`0xAB1730`) [verified]. The listeners run synchronously; which systems they are (scene graph, model build, resource requests) is not traced [to find] |
| 7. Resource loads | ResourceSystem's own JobThread: `0x7274D0` makes a **new** thread, mask 2, index 5 (`JobThread%i`) [verified] | Resource worker | Async model and texture loads [inferred: model builds for newly visible objects queue here]. RefPack decode stays under the global resource lock `0xFB2CD0` (N15) whoever calls it [verified, docs/15] |

### 5.3 What the sim thread waits on

1. **The pump itself (step 5)** is the main sim-thread cost: one Simulate pass runs every pending add or remove to completion, with no deadline [verified]. When the camera passes several lots, all their adds can land in one pass [inferred]; that is the stutter S3SS's throttle targets.
2. **Synchronous calls to the main thread during `Lot::Load`.** `0xABE970` posts `0xAB4A20` (create the lot renderer) with selector 0 and waits (`0x7D98C0(..., 0)` -> `0x59A500`) [verified]. If `Lot::Load` runs on the sim thread (world load, new lot placement) [inferred], the sim thread blocks until the main thread pumps kind-1 jobs; `0x59A220` helps run other jobs while it waits [verified]. Not part of camera streaming.
3. **Resource lock contention.** Synchronous resource reads from the per-object listeners (step 6) would wait on `0xFB2CD0` while the resource worker holds it [inferred; the listeners are not traced].
4. **Script work inside the scope.** The message scope batches script notifications around the object loop [inferred from the ids]; where they are delivered, and whether C# callbacks run inside the pump, is not traced [to find].
5. Not a wait: main-thread scoring (step 2) is already off the sim thread, and `Lot::SetActive` never waits [verified].

### 5.4 S3SS options that touch it

| S3SS option | Site | Effect, from our reading |
|---|---|---|
| Lot Streaming Optimizations: object throttle | Detours `0xAC1130` (entry `83 EC 08 57 8B F9 80 BF C9 00 00 00`) [community-reported, docs/03; bytes verified] | Adds N objects per call and re-posts the rest with `PostRemoteMethodCall`. **Risk [inferred]:** a re-post made on the sim thread with selector 1 runs **inline at once** (step 4 same-thread branch), so the work only spreads across passes if S3SS defers some other way. Check the S3SS source before relying on it |
| Map-view blocker | Sets `WM+0x258` in its `WorldManager::Update` detour | Skips step 2 while map view is on [verified gate in `0xC6D570`] |
| Visibility override | JZ -> JMP at `0xC63015` | Drops the `Active Lot Bias` hysteresis: active lots no longer get the bonus, so lots near the boundary swap more readily [verified meaning of the JZ] |
| Live settings | `Throttle Lot LoD Transitions` `[0x11ECBC0]`, `... Max Active Lot Threshold` `[0x1184328]` (12), `Camera speed threshold` `WM+0xEC`, `Max Active Lots` `WM+0xE4`, `Lot LOD dist.` `WM+0xDC`, `Active Lot Bias` `WM+0xE0`, `Terrain Height Thresh` `WM+0xE8`, `Force LoD Min Priority` `[0x118432C]` (1) | Registered by `0xC671E0` under "Streaming" [verified: string and address pairs] |
| RefPack decompressor | `0x4EB3B0` | Shortens the decode part of step 7 (N15) |

### 5.5 Measurement (built 2026-10-15, not run in game)

Goal: ms of sim-thread time per lot activation, how many activations land in one pass, and the main-thread scoring cost. The hooks are not on route or GC hot paths; call rates are lots per second, except the optional per-object hook.

| Counter | Hook site | Prologue bytes (verified) | Records |
|---|---|---|---|
| `lotAdd` | `0xABD3C0` (remote-call execute for the add type, vtable `0x10650C4` slot `+0x10`) | `56 8B F1 0F B6 4E 19` (7) | Filter on fn pointer `[obj+0x10]` == `0xAC1130`. Lot ptr `[obj+0x0C]`, object count `([lot+0x18]-[lot+0x14])/4`, lot id `[lot+0x48]`/`[lot+0x4C]`, QPC ms enter to exit, thread id. Hooks the caller, so it works with or without S3SS's `0xAC1130` detour |
| `lotRemove` | `0xABD3E0` (remove type, vtable `0x10650D8` slot `+0x10`) | `56 8B F1 0F B6 4E 18` (7) | Same, fn `0xAB4C00` |
| `pumpMs` | `0x59A430` (RunJobs), only when `GetCurrentThreadId() == [0x11D8A8C]` | `53 56 8B F1 57 8D 7E 08` (8) | ms per call, `lotAdd`/`lotRemove` run inside it, max per second: the per-pass spike, not only the total |
| `lotScore` | `0xC6C290` (main thread) | `55 8B EC 83 E4 F0` (6) | ms per call, early returns (camera speed), calls that changed a lot |
| `lotSetActive` | `0xAC20E0` | `53 8A 5C 24 08` (5) | On/off changes per second (`[lot+0xC1] != arg` and `[lot+0xC9] == 0`): shows whether the throttle limits activations per update |
| `syncPostWait` (optional) | `0x7D98C0` | `51 53 56 57 8B F1` (6) | Count and wait ms when the second stack argument is 0 (synchronous) and the caller is the sim thread: covers 5.3 item 2 |
| `objScene` (optional, off by default) | `0xABFAC0` | `83 EC 0C 83 B9 64 03 00 00 00` (10) | Per-object ms histogram, to find heavy objects (store content). High call rate during an add: short runs only |
| `thumb` (optional `Thumb=1`, off by default; 2026-11-18, counters v212) | `0xD5D910` `ThumbnailManager_CreateThumbnailTexture` (thiscall, 5 args, `ret 0x14` [verified asm]) | `55 8B EC 83 E4 F8` (6) | `thumbCalls` / `thumbUs` / `thumbMaxUs` on the sim thread, `thumbOffSim` = calls on any other thread (not timed). D3D CreateTexture + per-level lock/fill/unlock on the calling thread (docs/43 §1). Offload inventory 1118 candidate 5: decides whether the fill is worth moving; no offload built because the texture is created on the device and handed to the request queue under lock `0x1088330` in the same call, so a deferral changes when the texture exists for the IC's caller [inferred, docs/43 §4]; diag first (queue `offload-a-diag-1118`) |

Per-second CSV columns (like `stutter-profile.js`): `lotAdds`, `lotAddMs`, `lotAddMaxMs`, `lotAddObjs`, `lotRemoves`, `lotRemoveMs`, `pumpMaxMs`, `lotScoreMs`, `lotSetActive`. Per event (JSONL): `{t, kind, lot, lotId, objs, ms, passMs}`. Reserve the counter slots in `docs/registry.md` before building.

**Repro:** the M10 pan (`camera-zoom-memory`) plus a pan at street zoom across a dense neighbourhood, with S3SS Lot Streaming Optimizations off and on, in one load if a runtime switch exists (bundle rule). Decision: if `lotAddMaxMs` is a few ms, lot streaming is not a stutter source and N16/E8 close; if it is tens of ms, a time-budgeted pump (stop `0x599E60` after N ms and leave the rest queued) or an object-chunked add becomes a candidate, after S3SS's own throttle is checked with the same counters.

**Status 2026-10-15: built, not run in game.** ts3native 0.10.5 `[LotStream]` (`native/ts3native/lotstream.cpp`, default `Enabled=0`), counters v19 at `0x2C4`-`0x344` (docs/registry.md), build parked as `game-copy/ts3native.asi.lotstream`; `native/ts3native/out/` stays the matrix-tested build.

- **Hooks and calling conventions** [verified, disassembly of the Steam 1.67 exe, 2026-10-15]: `0xABD3C0` / `0xABD3E0` are thiscall with no stack arguments and a plain `ret`; they call `[call+0x10]` with `ecx = [call+0x0C] + [call+0x14]` (so the lot is that sum, not `[call+0x0C]` alone). `0x59A430` is thiscall with 3 arguments (`ret 0xC`), `0xC6C290` 4 (`ret 0x10`, the first is a float), `0xAC20E0` 1 byte argument (`ret 4`), `0x7D98C0` 2 (`ret 8`: selector, async; async 0 waits in `0x59A500`), `0xABFAC0` 3 (`ret 0xC`, ecx = the lot: it reads the renderer at `+0x364`). All seven prologues match the table above; the five required ones are checked together and nothing is hooked if one differs.
- **What is counted:** lot add/remove ms (QPC), objects per added lot, outermost sim-thread `RunJobs` passes (ms, passes with lot calls, most lot calls in one pass), scoring ms and calls that switched a lot, `SetActive` on/off switches (the game's own `[lot+0xC1] != arg && [lot+0xC9] == 0` test). `SyncWait=1` counts synchronous posts whose target thread is not the caller (the inline same-thread case is excluded); `ObjScene=1` gives a 4-bucket per-object histogram and logs objects slower than `ObjLogMs`. Execute calls with another fn pointer are only counted in `lsOtherFns`, the first 8 logged to `ts3native.log`: that shows whether S3SS re-posts through another function [to find in game].
- **Outputs:** per event a line in `Game\Bin\ts3native-lotstream.jsonl` `{t, kind, lot, lotId, objs, ms, passMs}` (`t` UTC ms; events inside a pass are buffered and written when it ends, outside the timed span). Per second, `tools/frida/stutter-profile.js` adds `lotAdds, lotAddMs, lotAddMaxMs, lotAddObjs, lotRemoves, lotRemoveMs, pumpMaxMs, lotScoreMs, lotSetActive` to `*-seconds.csv` (it zeroes the plugin's `lotAddMaxUs` / `pumpMaxUs` after each read); `/native` shows all v19 fields. Runtime switch `lsMode` (+0x2C8) turns the timing off in place.
- **Repro:** agent `camera_pan` (in-game camera only, never the window; by wall-clock time, path through the 8 lots around the densest lot unless `from`/`to` are given) and `tools/harness/repros.json` `lotstream-pan` (steady pan, then the same path with 3 s dwell per lot in case the `Camera speed threshold` blocks activations while moving [inferred risk]). `profile-world.ts --repro lotstream-pan` runs it inside a profile window without the stack samplers.
- **S3SS on/off:** S3SS has no runtime switch we know of; it reads `<Documents>\Electronic Arts\The Sims 3\S3SS\S3SS.toml` at game start (`[patches.LotStreamingOptimizations] enabled`) [verified: file and S3SS_LOG install lines]. Queue jobs set it with the new `s3ssToml` field (put back after the job), so on/off is two loads, not one.
- **Queue:** `tools/harness/queues/lotstream.json`: `bp-lotstream-s3ss-on`, `bp-lotstream-s3ss-off`, `ip-lotstream-s3ss-on` (6 min profile each). Self-test: `selftest.exe` drives every hook on fakes (mode 0 pass-through, other fn pointers, nested and other-thread passes, the per-second reset, SetActive's change test, sync-wait selector/async bytes, object histogram, the JSONL lines, refusal when the prologues are absent) [verified, build 2026-10-15].
- **Not verified yet:** the thread of every call in a real load (only the static reading of §5.2), the hook overhead in game, and whether `[0x11D8A8C]` is already set when the first pump runs (before that, no pass is timed) [inferred].
