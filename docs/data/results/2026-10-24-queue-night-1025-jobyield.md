# Queue night-1025-jobyield 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1025-jobyield-2026-10-24.jsonl`.

## 2026-10-24T21:39 UTC, build 9c8af861, jobs sv-jobyield-verify, ip-owprobeplans-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-jobyield-verify | 21:39 (05:39) | 4.4 | PASS | PASS: profile stopped at 132 events after 1.4 min (off 66, on 66; 4 firings, 1 cycles; rule 50 events per arm), cap 10 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2139.9/1803.4/1564.9 -> 2177.3/1839.2/1520.6 | asyncStarted +1943, asyncRefused +377, asyncCollected +1941, asyncDropped +1, asyncUs +17545639, asyncMaxUs +215944, asyncPumpUs +10814837, asyncPumpWaits +1978, asyncRefusedM1Pending +10, asyncQueueFull +304, asyncQueueMax +5 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 21.0% of game CPUs); others on game CPUs 21.0%; clock avg/min 4184/4155 MHz; job-yield-ab.json job 1 (docs/183) on a |
| ip-owprobeplans-ab | 21:44 (05:44) | 16.7 | PASS | PASS: profile 15 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2201.9/1875.4/1484.9 -> 2228.3/1884.9/1435.6 | - | timing: invalid (noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 25.3% of game CPUs); others on game CPUs 25.3%; clock avg/min 4172/4147 MHz; Rerun of sv-owprobeplans-ab on IP (SV has no sea lot). owprobeplans (docs/163 §8-9): EA stuck-check probe plans hidden fr |

### sv-jobyield-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-owprobeplans-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 15 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
