# Queue night-1024-p6-fix 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-p6-fix-2026-10-24.jsonl`.

## 2026-10-24T17:35 UTC, build d052e322, jobs bp-p6-verify2, ip-p6-verify2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-p6-verify2 | 17:35 (01:35) | 5.7 | PASS | PASS: profile stopped at 4 events after 2.6 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm; 1 steps capped), cap 10 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2223.9/2002.5/1425.3 -> 2380/2101.1/1299.6 | asyncStarted +5195, asyncRefused +202, asyncCollected +5152, asyncUs +64510881, asyncMaxUs +572062, asyncPumpUs +28464095, asyncPumpWaits +7829, asyncRefusedM1Pending +16, asyncSkipped +42, asyncBackoffs +30, asyncQueueFull +123, asyncQueueMax +27 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 19.3% of game CPUs); others on game CPUs 19.3%; clock avg/min 4201/4167 MHz; §24.6 step 1, BP: routesnap stack  |
| ip-p6-verify2 | 17:40 (01:40) | 5.6 | PASS | PASS: profile stopped at 4 events after 2.8 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm; 4 steps capped), cap 10 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2236.4/2006.6/1413.3 -> 2312.4/2063.5/1339.9 | asyncStarted +2616, asyncRefused +197, asyncCollected +2576, asyncUs +53944234, asyncMaxUs +613668, asyncPumpUs +27154454, asyncPumpWaits +4284, asyncRefusedM1Pending +8, asyncSkipped +39, asyncBackoffs +14, asyncQueueFull +160, asyncQueueMax +20 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.3% of game CPUs); others on game CPUs 7.3%; clock avg/min 4246/4211 MHz; §24.6 step 1, IP: same settings and  |

### bp-p6-verify2: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6-verify2: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
