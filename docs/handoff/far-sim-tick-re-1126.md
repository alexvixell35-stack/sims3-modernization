# Handoff: far-Sim tick RE (docs/341, phase R lane 2 of docs/340)

Written 2026-11-26. Static only, docs only, on master. Nothing built, nothing run. Ledger: docs/51 §611.
For: SD-1 (far-Sim state machine design), RX-6 (readers that need a Sim object), RX-9 (verdict), and whoever builds
FS packages of docs/332.

Path note: the task asked for `docs/handoff/`; the repo's existing folder is `docs/handoff/`, so the file is here.

## What is answered

| docs/340 item | Answer | Where |
|---|---|---|
| RX-1, Sim part: loop entry, tasks one Sim owns, add / remove | Two scheduler entries per Sim (the Sim, Threaded; `SimUpdate`, Task). `AutonomyManager` is one global task. All Sim alarms sit in `AlarmManager.Global`, pumped by the world lot's task | docs/341 §1 |
| RX-1: "`0x76CA60` native task fetch, confirm" | It is `Simulator_GetTaskImpl`, a lookup icall, not the scheduler's fetch. The scheduler walk for Threaded tasks is `0xD7F8B0`, run through `ScriptHost_RunTask` `0xD7F000` | docs/341 §1.1 |
| RX-1: EA entry that safely sends an NPC home | `Sim.AttemptToPutInSafeLocation(sendHome: true)` teleports through EA placement; `Sim.MakeSimGoHome` only queues an interaction | docs/341 §4 |
| RX-5: non-determinism sources, allowed inputs | 11 hazards (H1-H11) and the allowed-inputs list | docs/341 §5 |
| RX-6: partial | `CreatedSim` is read on about 2,300 lines of Sims3GameplaySystems; counts per namespace only. Sites are NOT classified | docs/341 §3 |
| State set for SD-1 | 10 states, inputs, seeded RNG, sidecar record | docs/341 §6 |

## The three findings SD-1 should start from

1. EA already computes needs of low-LOD Sims in closed form (auto-satisfy curve of the hour plus one random phase per
   motive). Use the same form; hand `MotiveTimeDelta`, `AutoUpdated` and the value to EA at go-live.
2. EA's fresh-Sim contract (`Sim.OnCreation`) carries no buffs (travel buffs aside), no queue, no posture. A machine
   that hands over "standing, empty queue, needs from the curve" hands over a state EA reaches by itself.
3. The live EA run is timing dependent (real-time deadline in the Threaded walk, 1 ms stopwatch yields in autonomy).
   Determinism tests compare the machine against its own replay (DT-1 / DT-2), never against a live EA run.

## Not done (pick up here)

Round 2 (docs/341 §9, ledger §619) closed Q1-Q5, Q7, the Sims3GameplayObjects sweep and a heuristic RX-6
bucket count. Still open: Q6 (EA 1.69, needs the decrypted image), the motive tuning XML span, the `Simulator.Wake`
icall body, a byte-level read of the `ScriptTick` count-down loop. The list below is the round-1 state.

- Q1: native sleep list and wake order (disassembly of the path behind `Simulator_SleepImpl` `0x76CB00` and of
  `0xD80620`).
- Q2: inventory of a Sim without an object (stage 2b blocker check).
- Q3: timed buffs with side effects at timeout (go-far eligibility).
- Q5: `Object.InternalGetHashCode` address based in this Mono build: managed side read, native side not.
- Q6: EA 1.69: scheduler addresses unresolved since docs/308; managed methods cited in docs/341 not diffed.
- Q7: the `0xD7F8B0` rows come from the Ghidra decompile under `game-copy/analysis/decomp/sched-seams.c`; do a
  byte-level read before any hook there, and run `node tools/hook-sites.ts --check` (docs/68: TaskSeam and TaskServe
  already retarget the four RunTask call sites).
- RX-6 site classification; Sims3GameplayObjects not swept for per-Sim tasks.
- docs/341 §7 candidate (`SimUpdate` of a low-LOD NPC runs every sim tick): not measured; check docs/143 / 144 for
  overlap before anyone builds it.

## How to re-check

- Managed rows: open the `file:line` given in docs/341 under `game-copy/decompiled` (local only, never committed).
- Native rows: docs/308 table (Steam and retail bytes), docs/04 F17, docs/27 §1; the walk in
  `game-copy/analysis/decomp/sched-seams.c` (function at `0xD7F8B0`).
