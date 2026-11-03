# Queue night-1024-plandeadline 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-plandeadline-2026-10-24.jsonl`.

## 2026-10-24T17:22 UTC, build d052e322, jobs bp-plandeadline-verify, bp-plandeadline-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-plandeadline-verify | 17:22 (01:22) | 4.3 | PASS | PASS: profile stopped at 4 events after 1.4 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 8 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2167.5/1845.9/1500.9 -> 2138.6/1853.2/1483.3 | asyncStarted +419, asyncRefused +12, asyncCollected +419, asyncUs +6146761, asyncMaxUs +81426, asyncPumpUs +2106418, asyncPumpWaits +235, asyncRefusedM1Pending +2, asyncQueueMax +3 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.0% of game CPUs); others on game CPUs 8.0%; clock avg/min 4239/4166 MHz; CORRECTNESS (noise OK, fail fast). route-caronly-deadline-loop, s |
| bp-plandeadline-ab | 17:26 (01:26) | 8.6 | PASS | PASS: profile stopped at 16 events after 5.4 min (off 8, on 8; 16 firings, 4 cycles; rule 8 events per arm), cap 22 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2196.3/1857.6/1500.9 -> 2164.3/1875.6/1451.6 | asyncStarted +928, asyncRefused +18, asyncCollected +928, asyncUs +13387609, asyncMaxUs +105202, asyncPumpUs +4818900, asyncPumpWaits +558, asyncRefusedM1Pending +4, asyncQueueMax +3 | timing: invalid (noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.6% of game CPUs); others on game CPUs 8.6%; clock avg/min 4236/4173 MHz; TIMING, clean machine (docs/168 §10: noise low, >= 4 firings per arm). In-load ABBA on route-caronly-deadline-loop, fixed r |

### bp-plandeadline-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 8 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-plandeadline-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 22 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
