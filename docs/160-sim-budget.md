# 160. Sim-thread budget: one table per world

Written 2026-10-22. Queue and report built 2026-10-22; **first run 2026-11-22 on master set m1122 (§"Results m1122" at the end)**. Goal: pick weekend targets by measured ms/s of the sim thread, not by guess (memory rule "measure before build"). It completes docs/28 §4 item 4 (per-stage split of `MonoScriptHost::Simulate`, SV missing, ~290 ms/s untimed).

## How to run and read

- Queue: `tools/harness/queues/sim-budget.json`, 3 jobs (`bp-`, `ip-`, `sv-sim-budget`), ~12 min each. Release defaults on `game-copy/ts3native.asi.0325`, plus `[HitchAttr] Enabled=1` (observe only, docs/96 §9). 10 min seeded profile (`profile-world.ts --minutes 10 --seed 12345`): stutter profiler, managed sampler (minutes 1-4), native sim-thread sampler (minutes 5-8). No `--ab`, no repro. Timing counts only from a clean night.
- Report: `node tools/harness/sim-budget-report.ts` (newest `sim-budget-*` run per world; or pass tags). From a worktree, set `TS3_REPO=<main checkout>`. `--self-check` runs a fixture, then checks every bucket on the newest real run that has managed samples.
- Inputs per tag: `probe-out/profile/<tag>-<secs>-seconds.csv` and `-hitches.jsonl` (stutter-profile.js), `<tag>-managed.jsonl`, `<tag>-simstack.jsonl`, `probe-out/queue/<tag>-native.json` (`/native` before and after: `blockKey/N/Us`).
- Rows: the first 60 s (load work, as in hitch-attr-report.ts) and paused seconds are dropped. ms/s = ms per wall second, so the buckets add up to 1000.

## Buckets

| Bucket | What it measures | Top 5 contributors from | Tag |
|---|---|---|---|
| Mono script | Scheduler stages (tick `0xD80BC0`, TaskObjects, OneShot, ThreadedObjects) minus route, the waits taken from script, and forced GC; multiplied by the share of managed samples that are not inside an internal call | owner category of the C# stack (`stall-split.ts category`: autonomy, interaction step, SimUpdate, NRaas, alarms...) | timers measured, split inferred |
| ICs (native under script) | The same remainder times the share of managed samples whose leaf is an IC from `docs/data/ic-safety.csv` (non-plan Route ICs included) | leaf IC name | split inferred |
| route sync plans | stutter-profile `route` (`Route_PlanLocked 0x82C6E0` on the sim thread) minus `rtPlanWait` | plan IC (`Route_Plan`, `PlanFromPoint`, `Replan`, `ReplanFromPosition`) and its first script caller | measured |
| +0x918 / job waits | `rtLock + rtOtherLock + rtJob + rtPlanWait` ([HitchAttr]) | `/native` `blockKey` deltas named with hitch-attr-report.ts `keyName` (docs/84-90 keys) | measured; key split covers the whole `/native` window |
| GC + finalizers | `gcMark` (step and forced) + `fin` | step mark, forced (allocation), finalizers | measured |
| anim + locomotion + publish | `animSacs + animPose + locomotion + publish` minus the steering-key share of the lock waits | the four stages | measured; wait split inferred |
| other (untimed, sync, idle) | row wall minus all of the above: catch-up overhead, the remote-call pump `0xD81FC3` (shared JobManager jobs, incl. render jobs, docs/41 S4), frame waits, untimed native work | leaf frame of simstack samples with no timed-stage or Mono-runtime frame | remainder measured, split approximate |

**Frames >= 300 ms it leads:** for every hitch record >= 300 ms (after the first 60 s), the same split on that frame's own fields; the bucket with the most ms leads. Mono and ICs are one bucket here (no per-frame managed samples).

## Known blind spots

1. **Mono vs IC split is a sample ratio** (5 Hz, minutes 1-4 only, ~1,000 samples per world), applied to the whole window. A wait inside an IC (e.g. `IsSlotUnobstructed`) is subtracted as wait but its samples still count as IC, so the IC share is a little high [inferred].
2. **Waits by key** come from the `/native` before/after pair, which spans the whole profile including the first 60 s. Only waits while a route job runs or is queued are keyed (`WaitNote`), and only the first 32 keys get a slot (`blockOther` counts the rest without time).
3. **Steering waits** are moved from the script stages to anim/locomotion by run-level key share (per second) or by `rtMaxKey` (per hitch). A wrong key list moves time between the two buckets, not out of the table.
4. **Route plans called outside the scheduler stages** (locomotion callbacks, docs/141 class c) are still subtracted from the script stages; the script bucket clamps at 0 per row.
5. **Samplers suspend the sim thread** (5 Hz) during minutes 1-8, which adds to whichever stage was running. This is the same for all three worlds, so worlds compare.
6. **Render/UI on the sim thread:** render runs on the main thread (docs/41). What reaches the sim thread is the publish sync (`publish`, waits for the main-thread consume), render jobs the unbudgeted pump picks up (in "other", not split by job), and UI ICs (in "ICs"). No main-thread sampler runs in this queue.
7. **Idle/sleep:** the sim loop has no idle state while unpaused; paused seconds are dropped. Waits in `ntdll`/`KERNELBASE` outside the timed stages show as an `ntdll.dll` contributor of "other" (no export names).
8. `publish` may nest inside another stage (stutter-profile.js note); the overlap is not removed.

## Results m1122 (2026-11-22): the whole sim thread, and who owns it

Queue `tools/harness/queues/sim-budget-m1122.json` (chain `probe-out/queue/chain-sim-budget-m1122.sh`), master set m1122
(ts3native 0.36.299-m1122, `ts3native.asi.m1122` sha256 876d7de8d625, ini / ts3fixes / ts3agent `*.m1122`), Bin state `apex`
(Apex latest + RTX Remix bridge, snapshot -> state -> restore). One arm: release defaults + `[HitchAttr] Enabled=1` +
`[UnseenProbe] Enabled=1 Bands=50,150,400,1000` + `[AnimPhase] Enabled=1 Split=1` + `[Diag] TaskSeam=1` (the band sites, docs/322)
in the same run, stutter profiler + both stack samplers, 10 min seeded profile (seed 12345), no forced actions. Jobs: smoke BP 3 min
PASS, BP / IP / SV PASS, `bp-sim-budget-light` (tool cost, below) FAIL only on one script error (blind spot 5; its counters are
usable). Machine noise medium on every 10-min job (other CPUs 70-89% busy, game CPUs swept): attribution shares, not a timing
verdict. Queue log: `docs/data/results/2026-11-22-queue-sim-budget-m1122.md` (main checkout).

Report: `TS3_REPO=<main checkout> node tools/harness/sim-budget-report.ts` (newest `sim-budget-*` run per world). New in the
report (no new counter: the band counters already give the household / NPC split): a **wait / idle** line (route-job waits +
publish sync + `IdleSimulationCycle 0x7694B0`) and a **Who** table from the `[UnseenProbe] Bands` counters of the same run
(disjoint sites loco + pose + Sim task + autonomy per row; near = bands with upper edge <= 150 m, far = lower edge >= 150 m;
non-Sim = 1000 minus the Sim rows). `--self-check` covers the row split.

### Totals [measured]

| | BP | IP | SV |
|---|---:|---:|---:|
| sim iterations per second | 44.3 | 45.4 | 51.6 |
| wait / idle ms/s (route-job waits + publish + idle cycle) | 21.8 | 51.5 | 16.3 |
| busy ms/s | 978.2 | 948.5 | 983.7 |
| frames >= 300 ms in ~530 s | 0 | 26 | 1 |

The sim thread has no idle time while unpaused: `IdleSimulationCycle` is 0.0 ms/s in all three worlds, the pacing sleeps
`0xD81AF7` / `0xD81B16` sit on the pause path (paused seconds dropped), and the publish sync is 2-4 ms/s [measured]. So the
thread runs flat out, and saved sim-thread time shows up as more iterations per second (sim speed headroom), not as idle
[inferred, I-580-1].

### Buckets (ms/s, share of the thread) [measured; Mono vs IC split inferred from samples, blind spot 1 above]

| Bucket | BP | IP | SV | Top contributors (BP / IP / SV) |
|---|---:|---:|---:|---|
| Mono script | 473.2 (47.3%) | 427.3 (42.7%) | 465.7 (46.6%) | Sim interaction step 123 / 77 / 65; SimUpdate (motives, buffs) 84 / 83 / 97; Lot.Simulate / reactions 61 / 85 / 69; autonomy search 53 / 49 / 82 |
| ICs (native under script) | 157.3 (15.7%) | 174.8 (17.5%) | 201.0 (20.1%) | `Query_GetObjects` 14 / 27 / 30; `Query_GetObjectsOnLot`; `Slots_GetParentIdImpl` (BP 12); `World_FindGoodLocation` (IP, SV 8) |
| route sync plans | 16.4 (1.6%) | 49.6 (5.0%) | 10.0 (1.0%) | `PlanFromPoint` / `ReplanFromPosition` / `Route_Plan` via RouteBackoffHook |
| +0x918 / job waits | 18.1 (1.8%) | 47.5 (4.8%) | 14.0 (1.4%) | `0x82F4AB` 5.8 / 12.4 / 3.1; gate 12 / gate 10; `0xD882D7` (SV 3.3) |
| GC + finalizers | 7.6 (0.8%) | 7.6 (0.8%) | 8.7 (0.9%) | step mark 6-7, finalizers 1-2, forced 0 |
| anim + locomotion + publish | 149.0 (14.9%) | 112.9 (11.3%) | 111.6 (11.2%) | animPose 87 / 63 / 61; locomotion 58 / 46 / 48; publish 2-4 |
| other (untimed, sync, idle) | 178.4 (17.8%) | 180.3 (18.0%) | 188.9 (18.9%) | ntdll 27 / 44 / 17; `0x4E5E40` 18 / 10 / 14; frida-agent 13 / 6 / 20 (tool); ts3native.asi ~11 (probes) |

"other" timers (overlap the buckets): `svcUpdate` 30-38, `preLoco` 36-37, `pump` 1-2, `other7694` 0.0 ms/s in all three
worlds. IP's 26 frames >= 300 ms are led by waits (69%) and Mono + ICs (27%).

### Who owns the thread (ms/s, share of 1000) [measured attribution, heavy tools on]

| Owner | BP | IP | SV |
|---|---:|---:|---:|
| household + selected + unknown scope | 47.8 (4.8%) | 61.7 (6.2%) | 102.6 (10.3%) |
| NPC near (< 150 m) | 53.2 (5.3%) | 35.8 (3.6%) | 37.2 (3.7%) |
| **NPC far (>= 150 m)** | **245.7 (24.6%)** | **257.6 (25.8%)** | **208.1 (20.8%)** |
| NPC no position | 1.1 (0.1%) | 0.7 (0.1%) | 0.5 (0.1%) |
| non-Sim + unattributed (1000 - the rows above) | 652.3 (65.2%) | 644.3 (64.4%) | 651.5 (65.2%) |

The far-NPC rows match the docs/322 run (BP 250, IP 251, SV 195 ms/s on 0.36.299-uband2 without samplers): stable across two
runs and two builds [measured]. The non-Sim row is not all non-Sim work: it holds everything outside the four band sites, i.e.
lot and object tasks (`Lot.Simulate` / reactions 61-85 ms/s), services (`svcUpdate`), the untimed "other" bucket, GC, the SACS
pass, and Sim-driven work that runs outside the Sim's own task run (ICs and plans from other objects' tasks, situation and
alarm callbacks) [inferred, I-580-2].

**What the far-Sim model would remove** [inferred from measured shares]: the docs/322 estimate (~200-260 ms/s of NPC cost
beyond 150 m) is **24.6% (BP), 25.8% (IP), 20.8% (SV) of the whole sim thread**. Far Sims made free is the ceiling; on a
saturated thread that is at most ~1.33x (BP / IP) and ~1.26x (SV) iterations per second, or the same headroom spent on more
Sims / larger worlds [inferred, I-580-1]. Household Sims (5-10%) stay untouched by rule and are not in that figure.

### Tool cost [measured, one pair, medium noise]

`bp-sim-budget-light` = same save, seed and build, release defaults + `[HitchAttr]` only, stutter profiler on, no samplers, no
bands / anim split / task seam: 44.6 it/s against 44.3 it/s for the full BP arm (~0.7%, inside the noise of a single pair).
The probes move time between buckets more than they add: anim + locomotion 130.6 -> 149.0 ms/s (+18: `[AnimPhase] Split` QPC
pairs per pose), Mono + ICs 646.3 -> 630.5, other 183.9 -> 178.4. Inside the full arm the tools show as frida-agent 6-20 ms/s
and ts3native.asi ~11 ms/s in "other" (sampler suspends, probe code); docs/322 measured the task seam's self cost at ~3.4 ms/s.
Read the band rows as shares of a thread that carries ~2-3% tool load; a timing verdict needs an ABBA pair on a quiet machine.

### Blind spots (this run)

1. Band window = the `/native` before / after pair (whole profile, first 60 s included); the buckets drop the first 60 s.
2. Band sites time QPC spans that include sampler suspends (5 Hz, minutes 1-8): a suspend inside a pose or task run is charged
   to that Sim's band [inferred].
3. Waits inside a Sim's update (site 1) are part of loco and also in the waits bucket: the Who table and the bucket table each
   sum to 1000 on their own and are not added together.
4. A `Sim` task run that drives another object (e.g. a script route call) counts for the Sim; an object task that acts on a Sim
   counts as non-Sim.
5. `bp-sim-budget-light` hit one script error (release defaults, no probes): an NPC (not the active household),
   `InteractionQueue.OnError` "Piano+Watch left an object in the Sim's hand with no carry state machine: Guitar", through
   `Ts3Fixes.Fixes+BookHandQueueHook.PutDownCarriedObjects` (calls the original). Same class as docs/251 (book in hand), new
   object (guitar); no measurement tool was on in that arm. Left for the docs/251 owner.
6. Bin restore after the chain reported 5 differing files, all run outputs (pccheck, bridge logs, taskseam summary), no binary.
