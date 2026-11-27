# 325. Sim instantiation cost by part (2026-11-22)

Input for the far-Sim model (docs/324, feature/far-sim-model-1122): far Sims get removed and re-instantiated through a spread
queue with a per-frame ms budget. This probe measures what one instantiation and one removal cost on the sim thread, split by
part, so the budget can be set from numbers and the biggest part can be judged for off-thread work. Measurement only.

Branch `feature/inst-probe-1122`; ts3native `0.36.299-iprobe1` (master m1122 + `[InstProbe] Enabled`); ts3fixes `instprobe`
(`InstProbe.cs`, pure math in `InstProbeCore.cs`); counters 1530-1535; ledger §615.

## 1. What was built

- **Switches.** ts3native `[InstProbe] Enabled=0` (default off). 1 = ts3fixes `instprobe` is on from game start, so the world
  load's own instantiations are timed (asked once through GetOption2 q 15, the HouseholdToo channel; an older ts3native answers
  0). The ts3fixes switch `instprobe` (patches, default off) turns it on later in a session instead. Read-back lines:
  `OK   inst probe (docs/325): [InstProbe] Enabled=1, ...` (native) and `ts3fixes: instprobe: on (docs/325), native [InstProbe]
  Enabled 1, switch instprobe ...` (managed, from the frame task).
- **What it times** [verified, decompiled]: every `SimDescription` instantiate overload ends in
  `Instantiate(Vector3, ResourceKey, bool, bool)`; `Instantiate(Lot, ResourceKey)` (shell / elevator door spot, lot corners) and
  `InstantiateOffScreen(Lot, ResourceKey)` (random lot spot) pick a position first. Wrapper hooks, each calling EA with the same
  arguments and returning its result:

  | part | hook | meaning |
  |---|---|---|
  | total | outermost of the three entries | wall time, includes the age-up wait's sleeps |
  | busy | total - ageup | sim-thread work of the call |
  | spot | Lot entry - Instantiate(Vector3) | spot search (Lot paths only) |
  | ageup | `AgingState.PreInstantiateSim` | deferred age-up outfit build; sleeps until the SimBuilderTask is done |
  | create | `GlobalFunctions.CreateObjectWithOverrides` (the record's first) | object creation as a whole |
  | oncreation | `Sim.OnCreation` | managers, autonomy, motives, interaction queue, buffs, traits |
  | onstartup | `Sim.OnStartup` | components, interactions, alarms, SimUpdate task, outfit check |
  | native | create - oncreation - onstartup | engine object, rig and model setup [inferred I-615-1] |
  | footprint | `SimRoutingComponent.EnableDynamicFootprint` + `ForceUpdateDynamicFootprint` | routing registration |
  | initobj | `Sim.AddInitialObjects` | starting inventory |
  | rest | Instantiate(Vector3) - ageup - create - footprint - initobj | skills, occult, kSimInstantiated listeners, health |
  | f0 / f1 / f1x | frame task | the sim frame the call ended in, the next frame, f1 over the session median frame |
  | destroy | `Sim.Destroy` | removal |
  | startuponly | `Sim.OnStartup` outside any instantiation | Sims loaded from the save |

  Records are per sim-thread task (the age-up wait sleeps, so another task's instantiation can run inside it). A part hook does
  nothing unless its own task has an open record (one int test otherwise). PieMenuSimHead / CAS preview Sims (subclasses of
  Sim) are skipped.
- **Output** (ts3native.log via the UserDiag channel, all with prefix `ts3fixes: instprobe:`, written only by the frame task,
  never inside a timed call): one `inst #N <phase> scope <household|selected|npc|unknown> age .. species .. outfits N lot
  <kind>:<id> path <pos|lot|offscreen> t <ms> total .. busy .. spot .. ageup .. create .. (native .. oncreation .. onstartup ..)
  footprint .. initobj .. rest .. f0 .. f1 .. base .. yielded <0|1> ms` line per instantiation (cap 4000), `destroy` and
  `startuponly` lines, and `summary <why> <phase> <part> n N p50 X p95 Y max Z ms` lines every 5 min and on
  `Ts3Fixes.Fixes.InstProbeSummary(why)` (reflect_call), ending with a `summary <why> totals:` line (events, destroys,
  startuponly, lines dropped, errors, samples dropped, native ini, switch). Percentiles are nearest-rank over every sample
  (20,000 per phase and part).
- **Phases.** `load` before and up to 2 min after a world load, `steady` after that, or a label the harness sets with
  `Ts3Fixes.Fixes.InstProbeMark(label)` (`visitor`, `travel`; "" = automatic).
- **Household.** Measurement only: no Sim, lot or save change, so household, selected and unknown-scope Sims are untouched by
  construction; scope is only a column. Results for controlled Sims equal vanilla.
- **Unbounded worlds.** No world-size constant; samples are per event, not per lot or cell. On a streamed world the same lines
  give the per-instantiation cost of a region load.
- **Self-test.** `node tools/selftest/instprobe/run.ts` (nearest-rank p50 / p95 / max, frame median, the part split never
  below 0, phase naming with TickCount wrap, the summary line format the queue reads, the sample cap). Native selftest asserts
  q 15 follows the ini value.

## 2. Queue `inst-probe-1122` (run 2026-11-22, 4/4 PASS)

Run 2026-11-22 11:40-12:35 UTC by `probe-out/queue/chain-inst-probe-1122.sh` (after chain-sim-budget-m1122, before
chain-unbounded-caps-1122): smoke, BP, IP (first try N/A: EA package-lock deadlock at load, docs/539 §13.8; the retry
passed), SV all PASS; result file `docs/data/results/2026-11-22-queue-inst-probe-1122.md`. Bin state apex (Apex + RTX Remix bridge via
`tools/turbo-compat.ts` snapshot -> state apex -> restore), release defaults + `[InstProbe] Enabled=1`,
`[UserDiag] LinesPerPrefix=8000` (the default 400 per prefix would cut the load's lines), no samplers, no shadow, rewind off.

| job | world | min | steps |
|---|---|---|---|
| bp-instprobe-smoke | BP | 3 | repro `inst-probe-smoke`: spawn_sims 3, remove_sims, summary (stops the queue on fail) |
| bp-instprobe | BP | 10 | repro `inst-probe-session`: load (from game start), ~2 min natural play (steady), visitor: spawn_sims 10 + remove_sims (destroy rows), travel: the active Sim visits the farthest community lot, natural play, summary |
| ip-instprobe | IP | 10 | same |
| sv-instprobe | SV | 10 | same |

Both repros first set the switch `instprobe` on through `patches` (cleanup turns it off), so they also work with the ini at 0
(no load rows then). Pass per job: version + both read-back lines, >= 1 full inst line and destroy line, the summary totals line with events > 0,
destroys > 0, errors 0 (also the last repro step's mustMatch), Apex + bridge lines, 0 script errors. Dry-run clean.

Run (from the worktree, between chains): `node tools/harness/queue.ts tools/harness/queues/inst-probe-1122.json --no-smoke`.

## 3. Results (2026-11-22, Steam 1.67, Apex + bridge, release defaults + probe)

106 timed instantiations (BP 48, IP 16, SV 42 at the summary), 96 destroys, 0 probe errors, 0 lines dropped, every row
`yielded 0` (I-615-3 holds: nothing but the age-up wait sleeps, and no deferred age-up happened, so busy = total). Every
instantiated Sim was scope `npc`: no household, selected or unknown-scope Sim went through the probe. All times ms on the sim
thread, nearest-rank p50 / p95 / max (n small per phase: read p95 = max where n < 20).

**Instantiation, busy (sim-thread work of the call)**

| world | load | steady | visitor (10 at once) | travel |
|---|---|---|---|---|
| BP | 34.3 / 133.0 / 133.0 (n 14) | 10.8 / 58.4 / 58.4 (n 8) | 37.9 / 51.5 / 51.5 (n 12) | 28.5 / 100.5 / 100.5 (n 14) |
| IP | 69.2 (n 1) | 15.1 / 20.5 / 20.5 (n 4) | 18.4 / 32.9 / 32.9 (n 10) | 13.1 (n 1) |
| SV | 21.5 / 61.7 / 61.7 (n 10) | 21.5 / 35.4 / 35.4 (n 10) | 35.9 / 52.3 / 52.3 (n 13) | 22.7 / 44.2 / 44.2 (n 9) |

**By part, steady (p50 / p95)**

| part | BP | IP | SV |
|---|---|---|---|
| native (engine object, rig, model) | 10.6 / 23.7 | 8.3 / 14.0 | 15.7 / 21.3 |
| initobj (AddInitialObjects) | 0.0 / 19.7 | 4.5 / 7.8 | 8.2 / 14.2 |
| spot (Lot entries only) | 24.5 (n 1) | - | 0.06 / 0.29 |
| footprint | 0.03 / 0.13 | 0.00 / 0.06 | 0.00 / 0.06 |
| rest (skills, occult, listeners, health) | 0.13 / 0.54 | 0.13 / 0.16 | 0.18 / 0.38 |
| ageup | 0 / 0.06 | 0 / 0.06 | 0 / 0.03 |
| oncreation, onstartup inside the create | 0 | 0 | 0 |
| **startuponly (deferred Sim.OnStartup)** | 6.1 / 17.9 | 4.2 / 10.3 | 8.7 / 15.9 |
| f1x (next frame over the median) | 11.2 / 15.5 | 2.0 / 9.3 | 11.1 / 26.6 |
| destroy (Sim.Destroy) | 0.74 / 1.34 | 0.70 / 2.30 | 0.90 / 1.70 |

Visitor burst native p50 / p95: BP 24.5 / 34.0, IP 13.5 / 18.7, SV 20.2 / 31.1; initobj BP 11.2 / 21.6, IP 3.7 / 14.0, SV
10.4 / 20.9. Load spot (BP, Lot path from the world load's own spawns): 14.5 / 92.5 ms. Load `startuponly` = the save's own
Sims (BP n 108: 5.6 / 14.8 / 70.5, IP n 72: 4.5 / 32.8 / 53.8, SV n 95: 7.9 / 35.8 / 68.9).

**What the numbers say**
- I-615-1 refuted [verified, measured]: `Sim.OnStartup` does not run inside `CreateObjectWithOverrides`. Per phase, the
  `startuponly` count equals the instantiation count (BP steady 8 / 8, visitor 12 / 12, travel 14 / 14; same on IP and SV), so
  each new Sim's OnStartup runs later, outside the call (inferred: at the engine's object startup on a later sim step).
  `oncreation` reads 0 too: OnCreation is not inside the create either, and the probe does not time it outside a record, so its
  cost is not in these numbers (open, docs/51 §615). The `create` time is all engine work plus whatever native callbacks run
  inside it.
- One instantiation costs two sim-thread chunks: the call (steady p50 11-22 ms, p95 20-58 ms) and the deferred OnStartup
  (p50 4-9 ms, p95 10-18 ms), which matches f1x (the next frame is 2-11 ms longer at p50).
- Removal is cheap: `Sim.Destroy` p50 0.7-0.9 ms, p95 <= 2.3 ms on all worlds and phases.
- The Lot entry's spot search can cost more than the instantiation itself (BP load p95 92.5 ms, travel p95 77 ms); the
  Vector3 path has none.

**Proposed per-frame budget for the docs/324 spread queue**
- One instantiation never fits a 16 ms frame at p50 on SV and only just on BP / IP, and it cannot be split by the queue. So the
  queue's unit is one Sim, and the budget is a spacing rule rather than a ms cap per frame: **at most one far-Sim
  instantiation per 3 sim frames** (call + deferred OnStartup + one quiet frame), and none in a frame that already ran
  longer than 25 ms. That keeps the added cost near p50 25-30 ms per Sim spread over ~50 ms of frames, about one tenth of
  the 10-Sim visitor burst's worst frame.
- Instantiate through `Instantiate(Vector3, ...)` with a spot the model already holds (the Sim's last position), never the
  Lot entries: that removes the up to 92 ms spot search.
- Removals need no spreading below ~10 per frame (p95 <= 2.3 ms each); spread them only so they do not share a frame with an
  instantiation.

**Off-thread candidate**
- **native** (engine object, rig and model setup inside `CreateObjectWithOverrides`) is the biggest part: 50-75% of busy at
  p50 on every world and phase (steady 8-16 ms, burst 13-25 ms). It is engine code; the cheapest offload path is to warm what
  it loads (the outfit's model and texture resources) on a worker before the instantiation, not to move the create.
  [inferred: which resource reads dominate needs a native split of the create, next probe step]
- Second: **initobj** (AddInitialObjects, script, p50 4-11 ms): for a re-instantiated far Sim whose inventory already exists,
  the far-Sim model can pass `addInitialObjects: false` (decide in docs/324; must keep the result equal to vanilla for the
  Sim's inventory).
- Third: the deferred **OnStartup** (script, 4-9 ms p50): stays on the sim thread (alarms, interactions, SimUpdate task), but
  the spacing rule above gives it its own frame.

