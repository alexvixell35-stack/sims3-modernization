# Queue night-1024-g1dump 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-g1dump-2026-10-24.jsonl`.

## 2026-10-24T17:46 UTC, build 55cbaed9, jobs bp-g1-m2dump

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-g1-m2dump | 17:46 (01:46) | 13.6 | PASS | PASS: profile 20 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2262.3/2015.1/1413.1 -> 2413.6/2330.8/1024.9 | asyncStarted +13401, asyncRefused +246, asyncCollected +13081, asyncDropped +2, asyncUs +80285433, asyncMaxUs +174915, asyncPumpUs +59559735, asyncPumpWaits +12365, asyncRefusedM1Pending +139, asyncSkipped +318, asyncBackoffs +614, asyncQueueFull +3, asyncQueueMax +7 | timing: invalid (noise medium; heavy tool samplers; heavy tool RouteSnap.audits); build: asi 83773480bdd4 ts3native.asi.g1dump ts3native 0.36.50 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.9% of game CPUs); others on game CPUs 7.9%; clock avg/min 4257/4159 MHz; G1 BP compares as bp-p6-g1-compares (Mode=1, Kinds=1, ShadowEvery=1, L |

### bp-g1-m2dump: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 20 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
