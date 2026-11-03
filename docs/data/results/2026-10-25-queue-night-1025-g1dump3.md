# Queue night-1025-g1dump3 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1025-g1dump3-2026-10-25.jsonl`.

## 2026-10-25T01:25 UTC, build fe0f695c, jobs bp-g1-m2dump3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-g1-m2dump3 | 01:25 (09:25) | 14.2 | PASS | PASS: profile 20 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2228.3/2002.4/1425.3 -> 2412.8/2304.2/1074.5 | asyncStarted +12970, asyncRefused +259, asyncCollected +12674, asyncDropped +2, asyncUs +80866961, asyncMaxUs +208990, asyncPumpUs +63202693, asyncPumpWaits +12263, asyncRefusedM1Pending +140, asyncSkipped +294, asyncBackoffs +657, asyncQueueFull +5, asyncQueueMax +7 | timing: invalid (noise medium; heavy tool samplers; heavy tool RouteSnap.audits); build: asi 0892b109b8d9 ts3native.asi.g1dump3 ts3native 0.36.55 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.4% of game CPUs); others on game CPUs 17.4%; clock avg/min 4208/4170 MHz; G1 BP compares as bp-p6-g1-compares (Mode=1, Kinds=1, ShadowEvery=1 |

### bp-g1-m2dump3: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 20 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
