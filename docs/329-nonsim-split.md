# 329. Non-Sim sim-thread split: who owns the other 65%, and what a far-lot model could take (2026-11-22)

docs/160 §"Results m1122" left **652 / 644 / 652 ms/s (BP / IP / SV)** of the sim thread as "non-Sim + unattributed": everything
outside the four docs/322 band sites (Sim loco, Sim pose, the Sim's own task, AutonomyManager by actor). The far-NPC rows are
208-258 ms/s and are the far-Sim model's (docs/324). This doc splits the rest per owner, per distance band and per far-lot
class, so a deterministic far-lot model (Minecraft-style catch-up on approach or lot load) can be scoped by measured ms/s.

Branch `feature/nonsim-split-1122` (off master e555c9a9c = m1122). ts3native **0.36.299-nsplit1**, ts3fixes nonsim-split-1122.
Ledger §582. Tags: [verified] = our source, decompiled code or disassembly; [measured] = a run; [inferred] = to confirm (docs/51 §582).

**Status: built + selftests, queue RUN 2026-11-22, 5/5 PASS.** §3 holds the measured per-owner x band table.

## 1. What was built

`[Diag] NonSimSplit` (ts3native.ini, **default 0**; read at game start; needs `[Diag] TaskSeam=1`, bands from `[UnseenProbe]
Enabled=1 Bands=...`). File `native/ts3native/nonsim.cpp`, hooks in `taskseam.cpp` (after each run, outside its timed span).
Measurement only: every run calls EA exactly as before; the code reads ids, positions and fields and adds sums.

- **Owner per task run**, resolved once per TaskSeam node slot (again when the slot gets a new task) [verified, our source]:
  the ScriptProxy target's class (objects, lots, managers); a target holding a Sim in `mSim` or `mActor` (e.g. `SimUpdate.mSim`
  [verified, decompiled field name]) = kind **simdrv**, put in **that Sim's band**; the delegate's method for
  `OneShotFunctionTask` / `FunctionTask` / our `LoopTask` (`mFunction`); the invoked object's class for OneShot / ScriptTick
  invokes; else the task object's class. Kinds: object, lot, simdrv, sim, autonomy (these two are already the docs/322 rows),
  delegate, task, invoke. Up to 2048 owner rows (3/4 fill), overflow in one row, counted.
- **Band per node**, refreshed every 10 s (simdrv every 1 s): docs/322 `UbRow` (nearest of camera target / position and
  household Sims) on the object's own position; a **lot** uses its centre from the lot table (below); rows: bands 0..n, hh,
  none (no position), glob (delegates and managers: no position by design; the world lot).
- **Household exemption (in code, `nonsim.cpp` header):** a run is household (row hh) when its object is a household actor
  (fresh held table), is a household lot or lies inside one (lot table flag 1, circle of the half diagonal), or the held table
  is stale (unknown scope = household). The report always puts hh rows in class (a): household lots and household-owned objects
  stay live; no model may take them.
- **Lot table** (ts3fixes `UnseenViewers.cs` `PushLots`, viewer channel codes 29-37, docs/registry.md): every lot's ObjectId,
  centre (`Lot.GetCenterPosition`), half diagonal (`GetClosestCornerToPoint` from the centre, squared distance, so sqrt
  [verified, decompiled]), flags (1 active household's home lot, 2 world lot). At load and every 200 viewer pushes (~1000 ticks).
  Heap-grown table, no cap tied to the lot count. Sent only when ts3native answers code 29 (split on); refused = no more sends.
- **Sim work outside the Sim's own task:** simdrv rows (by the Sim's band), plus the docs/322 footprint and plan sites of Sims
  that ran inside a non-Sim run (`nonsim outside footprint|plans`, per band; part of that run's owner row, shown apart).
- **Self cost:** every 64th `NsRun` timed (QPC pair), printed as `self N ns per run` in the dump line.
- **Output:** a dump in ts3native.log every 60 s on TaskSeam's writer thread (`nonsim split:` head, two `nonsim outside` lines,
  top 60 `nonsim class` lines, one rest line; rows in us). Report `node tools/harness/nonsim-split-report.ts [<tag> ...]`
  (`--provisional` without a dump, `--self-check`), §2.

**4x / streamed worlds:** bands are metres from the viewers (ini edges), the lot table grows with the lot count, owner rows are
by class (not by object), per-run work is one cache check; a locate is one id lookup + transform read per node every 10 s.

## 2. How the report splits the thread (ms per wall second, sums to 1000)

| Row | Source | Tag |
|---|---|---|
| Sim rows (household, NPC near < 150 m, NPC far >= 150 m, no position) | docs/322 sites loco + pose + Sim task + autonomy | measured |
| owner rows by band | NonSimSplit rows of kind object / lot / simdrv / delegate / task / invoke | measured |
| lot sub-owners | a lot row split by the managed samples under `Lot.Simulate` (reactions, alarms by callback, detail priority, rest) | split inferred (I-582-5) |
| scheduler outside task runs | the four scheduler stages minus every seam run | measured remainder |
| non-Sim anim + locomotion + publish | `animSacs + animPose + locomotion + publish` minus the Sim loco / pose sites | measured |
| GC step + finalizers, svcUpdate, preLoco, pump | stutter-profile timers (docs/28 §0.3) | measured |
| other | 1000 minus all of the above | remainder |

Classes: **(a)** live (visible, global, household); **(b)** per-lot / per-object state a far-lot model can compute lazily on
approach or lot load (deterministic from elapsed time, keyed by lot, sidecar only if needed, nothing in the .sims3); **(c)**
throttle; **(d)** engine. Rules (`nonsim-split-report.ts` `GLOBAL_C`, `LOT_PARTS`): near and household rows of (b) owners count
as (a); far object / invoke / lot-state rows are (b); simdrv far rows are (b) but belong to the far-Sim model (docs/324), listed
apart; UI, camera, seasons, clock and situations are (a); services, population, role managers, our own loops are (c); every
engine row is (d).

## 3. Measured split, run 2026-11-22 (nonsim-split-1122, 5/5 PASS) [measured]

`node tools/harness/nonsim-split-report.ts` (from this worktree) on `nonsim-split-1122-2026-11-22-{bp,ip,sv}-nonsim-split`;
results `docs/data/results/2026-11-22-queue-nonsim-split-1122.md`. Dumps over 698-699 s, seed 12345, `-disablestoryprogression`;
self cost 330 / 328 / 282 ns per run; owner table never full; lots 83 / 120 / 93 (1 household lot each). Rows: ms/s total, then
near (< 150 m) / far (>= 150 m) / household / no position. The provisional table of the first version (managed-sample shares, no
bands) is superseded.

Sim rows (docs/322 sites), ms/s: household 42.2 / 65.0 / 108.6, NPC near 52.5 / 36.2 / 32.6, NPC far 230.5 / 245.0 / 204.4.

| Owner (class) | BP total: near / far / hh / none | IP total: near / far / hh / none | SV total: near / far / hh / none |
|---|---|---|---|
| `SimUpdate` (b, simdrv) | 101.4: 13.3 / 28.0 / 60.2 / 0 | 97.4: 3.5 / 26.5 / 67.4 / 0 | 108.8: 8.1 / 34.6 / 66.1 / 0 |
| `StateMachine` (c, task) | 34.4: global | 35.4: global | 34.3: global |
| Lot: reaction pulses (b) | 26.1: 2.5 / 19.4 / 4.2 / 0 | 22.4: 0.8 / 18.7 / 2.9 / 0 | 7.6: 1.0 / 5.9 / 0.8 / 0 |
| `WorldLot` (b, object) | 20.0: global (hh 0.2) | 27.5: global (hh 0.4) | 24.1: global (hh 0.3) |
| `DeadRefSweepRun` (c, ours) | 17.7 | 21.4 | 22.9 |
| Lot: alarms, other callbacks (c) | 11.9: 1.1 / 8.9 / 1.9 / 0 | 12.0: 0.4 / 10.0 / 1.6 / 0 | 13.7: 1.7 / 10.5 / 1.4 / 0 |
| `OneShotFunction` (b) | 10.9: global | 2.2: global | 3.1: global |
| `Services` (b) | 10.8: global | 13.7: global | 10.7: global |
| `UpdateTrafficTask` (b) | 10.7: global | below top rows | below top rows |
| Lot: detail priority (c) | 9.7: 0.9 / 7.2 / 1.5 / 0 | 11.2: 0.4 / 9.3 / 1.5 / 0 | 6.1: 0.8 / 4.7 / 0.6 / 0 |
| Lot: zombies / door privacy / rest (b) | 8.9: 0.8 / 6.7 / 1.4 / 0 | 15.7: 0.6 / 13.1 / 2.1 / 0 | 6.1: 0.8 / 4.7 / 0.6 / 0 |
| viewer push delegate (c, ours) | 8.1 | 7.1 | 8.0 |
| `AlarmOneShot` (b) | 7.8: global | 9.7: global | 12.5: global |
| `SimBuilderTask` (c) | 6.7 | 16.7 | 15.0 |
| `SeasonsManager` (b) | 5.8 | 9.4 | 7.9 |
| UI / camera (a: tooltips, camera, map tags, notifications) | 22.6 | 22.8 | 24.3 |
| Lot: alarms, object state (b) | < top rows | 2.2: 0.1 / 1.9 / 0.3 / 0 | < top rows |
| engine (d): other 93.3 / 78.5 / 112.9, scheduler 57.2 / 62.4 / 61.3, non-Sim anim + loco + publish 75.0 / 62.0 / 46.6, preLoco 35.5 / 35.9 / 36.0, svcUpdate 31.8 / 39.9 / 30.4, GC 9.0 / 8.6 / 10.7, pump 2.9 / 0.9 / 2.4 | 304.7 | 288.1 | 300.3 |

| Class (ms/s, share) | BP | IP | SV |
|---|---:|---:|---:|
| (a) live: visible / global / household | 213.0 (21.3%) | 211.0 (21.1%) | 255.7 (25.6%) |
| (b) lazy per-lot / per-object | 127.9 (12.8%) | 123.4 (12.3%) | 106.9 (10.7%) |
| (c) throttle | 123.8 (12.4%) | 132.4 (13.2%) | 132.6 (13.3%) |
| (d) engine | 304.7 (30.5%) | 288.1 (28.8%) | 300.3 (30.0%) |
| far NPC Sim rows (docs/324) | 230.5 (23.1%) | 245.0 (24.5%) | 204.4 (20.4%) |

Readings:
- **Far-lot model removable (class b, >= 150 m, household excluded): 27.7 / 33.9 / 11.8 ms/s** (2.8 / 3.4 / 1.2%); plus far Sims'
  Sim-driven work outside their own task (`SimUpdate` far) 28.0 / 26.5 / 34.6 ms/s, which is the far-Sim model's (docs/324).
  The no-position rows (`WorldLot`, `AlarmOneShot`, `OneShotFunction`, ~40 ms/s per world) are not banded; docs/331 §8 designs
  the probe that places them.
- **`SimUpdate` household share is the largest part (60-67 ms/s)**; far is 26-35 ms/s, much less than the provisional
  70-95 estimate.
- Our own loops (`DeadRefSweepRun` 18-23, viewer push 7-8, `StuckWatchdog` 5-7, `OdSweep` 2-4) are 36-41 ms/s: docs/330.
- Engine (d) 288-305 ms/s cannot be modelled.

## 4. Blind spots

1. Window: the dump and the ub counters span the whole profile (first 60 s included); the csv rows drop the first 60 s.
2. An owner row's time includes everything its run calls (ICs, route plans, forced GC). A Lot alarm that runs a Sim's logic
   (e.g. `Sim.RunIdleLogic`) stays on the lot row; only `mSim` / `mActor` holders are moved to a Sim (I-582-2).
3. Lot sub-owners come from managed samples, applied with one mix to every band (I-582-5).
4. Object band = the object's own position, not its lot's centre (error up to half a lot diagonal, I-582-4). Household lot test
   is a circle around the lot centre (counts a few metres around the lot as household: errs toward live).
5. Household-owned objects off the household lot (a parked car, an inventory item with a task) are only household when the
   object is a household actor; otherwise by position. The held table covers Sims, pets and horses.
6. Delegates and managers have no position (row glob); a delegate that acts on one far lot stays global.
7. Code outside script tasks (inline event listeners, the native UI services) stays in the engine rows.
8. Samplers (5 Hz, minutes 1-8) suspend the thread inside whichever run is active (docs/160 blind spot 2).
9. `-disablestoryprogression` is on in these queues (as sim-budget-m1122): story progression is absent from every table.

## 5. Queues (run 2026-11-22, 5/5 PASS)

`tools/harness/queues/nonsim-split-1122.json`, Bin state `apex` (Apex latest + RTX Remix bridge, snapshot -> state -> restore),
asi / ini / ts3fixes `game-copy/*.nsplit1` (builds.json `*.nonsim-split-1122`, asi sha256 c66bc9a06fc9), agent m1122. Runs
`queue.ts` from the lane worktree (its builds.json names the builds; outputs in the worktree's `probe-out`).

| Job | What | Pass |
|---|---|---|
| `bp-nonsim-split-smoke` | BP 3 min, full arm, stops the queue on fail | version `0.36.299-nsplit1`, HitchAttr / bands edges / task seam / anim split / nonsim OK lines (bands), a dump with runs, owners, lots with a household lot and simdrv nodes, Apex + bridge lines, 0 script errors, ub counters moved |
| `bp-` / `ip-` / `sv-nonsim-split` | 10 min seeded natural play (seed 12345), release defaults + `[HitchAttr]` + `[UnseenProbe] Bands=50,150,400,1000` + `[AnimPhase] Split` + `[Diag] TaskSeam` + `[Diag] NonSimSplit` + both samplers | same reads; attribution shares, not timing |
| `bp-nonsim-split-light` | tool cost: BP 10 min, bands + TaskSeam + NonSimSplit, no samplers, no anim split | version + OK + dump lines, 0 script errors; pair with sim-budget-m1122 `bp-sim-budget-light` (HitchAttr only, same save / seed); dump `self N ns per run` and the task seam summary give the per-run cost. One pair: **needs quiet machine** for a timing number |

Household untouched: read-only by construction (no switch changes a Sim, lot or save; seed 12345 as m1122; save guard hashes).
Order (chains): inst-probe -> unbounded-caps -> overflow-fixes -> **this** -> sp-instantiate-guard. Waiter
`probe-out/nsplit-wait-1122.sh` (no `chain-` prefix, outside `probe-out/queue`, so track.ts does not list it and the caps
chain's "no other chain" wait is not blocked) starts `probe-out/queue/chain-nonsim-split-1122.sh` once no chain is listed and
`chain-overflow-fixes-1122.log` has CHAIN END or CHAIN STOP. Est ~80 min. Read: `node tools/harness/nonsim-split-report.ts`
(from the worktree).

## 6. Next

Done (§3, 2026-11-23). The far-lot model is scoped in docs/331 (branch `feature/far-object-model-1123`, compute everything); `DeadRefSweepRun` / viewer push cost went to docs/330. Open: place the no-position rows (docs/331 §8 probe).
