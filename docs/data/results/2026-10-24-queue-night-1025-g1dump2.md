# Queue night-1025-g1dump2 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1025-g1dump2-2026-10-24.jsonl`.

## 2026-10-24T22:01 UTC, build 5509c60a, jobs bp-g1-m2dump2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-g1-m2dump2 | 22:01 (06:01) | 15.7 | PASS | PASS: profile 20 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2247.4/2023.1/1411.8 -> 2453.9/2356.8/1021.9 | asyncStarted +13690, asyncRefused +287, asyncCollected +13263, asyncDropped +7, asyncUs +91085898, asyncMaxUs +226681, asyncPumpUs +69385896, asyncPumpWaits +11792, asyncRefusedM1Pending +170, asyncSkipped +419, asyncBackoffs +703, asyncQueueFull +8, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers; heavy tool RouteSnap.audits); build: asi d6d11c1cb457 ts3native.asi.g1dump2 ts3native 0.36.51 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 19.0% of game CPUs); others on game CPUs 19.0%; clock avg/min 4206/4142 MHz; G1 BP compares as bp-p6-g1-compares (Mode=1, Kinds=1, ShadowEvery=1 |

### bp-g1-m2dump2: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 20 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
