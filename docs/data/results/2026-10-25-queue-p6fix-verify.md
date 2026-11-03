# Queue p6fix-verify 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/p6fix-verify-2026-10-25.jsonl`.

## 2026-10-25T01:53 UTC, build df197022, jobs bp-p6fix-verify, ip-p6fix-verify, ip-p6fix-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-p6fix-verify | 01:53 (09:53) | 5.4 | PASS | PASS: profile stopped at 4 events after 2.4 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 10 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2239.8/2016.6/1413.1 -> 2321.1/2120.1/1281.2 | asyncStarted +4821, asyncRefused +141, asyncCollected +4771, asyncDropped +1, asyncUs +37278769, asyncMaxUs +537305, asyncPumpUs +20922927, asyncPumpWaits +6277, asyncRefusedM1Pending +23, asyncSkipped +49, asyncBackoffs +19, asyncQueueFull +79, asyncQueueMax +31 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8699d52ca6dd ts3native.asi.p6fix ts3native 0.36.57 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 13.5% of game CPUs); others on game CPUs 13.5%; clock avg/min 4225/4207 MHz; §24.14 correctness, BP: night-10 |
| ip-p6fix-verify | 01:59 (09:59) | 5.6 | PASS | PASS: profile stopped at 4 events after 2.8 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm; 4 steps capped), cap 10 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2246.6/2001.3/1413.3 -> 2325.6/2056.4/1327.9 | asyncStarted +2660, asyncRefused +198, asyncCollected +2617, asyncDropped +4, asyncUs +39914799, asyncMaxUs +487633, asyncPumpUs +23181945, asyncPumpWaits +4069, asyncRefusedM1Pending +13, asyncSkipped +39, asyncBackoffs +9, asyncQueueFull +162, asyncQueueMax +20 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8699d52ca6dd ts3native.asi.p6fix ts3native 0.36.57 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 14.4% of game CPUs); others on game CPUs 14.4%; clock avg/min 4181/4082 MHz; §24.14 correctness, IP: same set |
| ip-p6fix-ab | 02:04 (10:04) | 3.5 | **CRASH** | FAIL: profile 22 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step set_speed failed: TypeError: fetch failed<br>FAIL: events not fired: p6Taken_0 missing; p6eUsed missing; m2Off missing; m2Trips missing<br>PASS: no forbidden log lines | 1 | 2238.4/2001.5/1408.1 -> ? | - | timing: invalid (job VOID: runInvalid: profiler cut short (96 of 1320 s); crash or abort (no "done; game alive"); noise medium; heavy tool RouteSnap.audits; A/B 1 off / 0 on iterations, < 4 per arm (repro firings)); build: asi 8699d52ca6dd ts3native.asi.p6fix ts3native 0.36.57 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 21.8% of gam |

### bp-p6fix-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6fix-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
