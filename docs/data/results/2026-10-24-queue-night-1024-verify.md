# Queue night-1024-verify 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-verify-2026-10-24.jsonl`.

## 2026-10-24T16:11 UTC, build 2bfa0b32, jobs bp-stuckcheckown-verify, sv-jobyield-verify, ip-seasonmarkercache-verify, bp-overwatchstuckslice-verify, bp-drivertask-car-verify, ip-drivertask-boat-verify, bp-disposespread-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-stuckcheckown-verify | 16:11 (00:11) | 4.1 | PASS | PASS: profile stopped at 137 events after 1.0 min (off 67, on 70; 8 firings, 2 cycles; rule 40 events per arm), cap 11 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_queued 0->273, stuckcheckasync_run 0->268<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2213/1876.6/1500.9 -> 2253.7/1917.5/1467.3 | asyncStarted +4891, asyncRefused +62, asyncCollected +4886, asyncDropped +3, asyncUs +24143999, asyncMaxUs +139766, asyncPumpUs +12693449, asyncPumpWaits +3474, asyncRefusedM1Pending +9, asyncBackoffs +7, asyncQueueFull +9, asyncQueueMax +5 | timing: invalid (noise medium); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.0% of game CPUs); others on game CPUs 7.0%; clock avg/min 4251/4218 MHz; Verify stuckcheckown (docs/163 §7, RouteBackoff.cs OwnerTaskStart): route-stuckcheck-own-loop on it |
| sv-jobyield-verify | 16:15 (00:15) | 1.5 | **CRASH** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: exit 3221226505 with no verdict line (child stopped from outside or died before its summary); last output: Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 94<br>FAIL: events not fired: jyBusy missing; watchdogs missing | 3221226505 | 2147.3/1807.8/1548.9 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-10-24 21.57.52.txt; FAIL: profile 10 min, seed 12345 (rng-compare.ts); FAIL: exit 3221226505 w; noise medium); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.7% of game CPUs); others on game CPUs 6. |
| ip-seasonmarkercache-verify | 16:16 (00:16) | 5.5 | PASS | PASS: profile stopped at 8 events after 2.6 min (off 4, on 4; 8 firings, 2 cycles; rule 3 events per arm; 2 steps capped), cap 9 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2199.6/1868.8/1500.9 -> 2180.5/2016/1355.3 | asyncStarted +2467, asyncRefused +43, asyncCollected +2464, asyncDropped +3, asyncUs +20620229, asyncMaxUs +86008, asyncPumpUs +12214866, asyncPumpWaits +2428, asyncRefusedM1Pending +13, asyncBackoffs +1, asyncQueueMax +3 | timing: invalid (noise medium); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.1% of game CPUs); others on game CPUs 7.1%; clock avg/min 4240/4208 MHz; Verify seasonmarkercache (docs/74 §11 a): ip-world-dispose-loop, ABBA, 3 firings per arm, cap 8 min |
| bp-overwatchstuckslice-verify | 16:22 (00:22) | 3.2 | **FAIL** | FAIL: profile stopped at 4 events after 0.3 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 11 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: owstuck_slices=0 (want >=1)<br>FAIL: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle, run void<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80<br>mono ledger INCOMPLETE: per-it | 1 | 2194.4/1834.5/1532.9 -> 2168.6/1838.6/1499.3 | asyncStarted +120, asyncRefused +8, asyncCollected +120, asyncUs +1526559, asyncMaxUs +80613, asyncPumpUs +395008, asyncPumpWaits +46, asyncRefusedM1Pending +7, asyncQueueMax +1 | timing: invalid (job VOID: FAIL: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle, run void; noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.9% of g |
| bp-drivertask-car-verify | 16:25 (00:25) | 7 | PASS | PASS: profile stopped at 4 events after 4.2 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 9 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2163.6/1849.1/1500.9 -> 2297.9/2109.8/1275.4 | asyncStarted +8575, asyncRefused +126, asyncCollected +8575, asyncUs +41705563, asyncMaxUs +196244, asyncPumpUs +22536782, asyncPumpWaits +5015, asyncRefusedM1Pending +11, asyncBackoffs +3, asyncQueueFull +3, asyncQueueMax +6 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.8% of game CPUs); others on game CPUs 8.8%; clock avg/min 4239/4182 MHz; Verify drivertaskasync, car side (docs/16 |
| ip-drivertask-boat-verify | 16:32 (00:32) | 11.1 | **FAIL** | PASS: profile stopped at 4 events after 8.2 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 13 min, seed 12345 (rng-compare.ts)<br>FAIL: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle, run void<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2212.6/1872.9/1500.9 -> 2190.9/1868.6/1451.6 | asyncStarted +267, asyncRefused +4, asyncCollected +267, asyncUs +4618662, asyncMaxUs +106926, asyncPumpUs +1312008, asyncPumpWaits +135, asyncRefusedM1Pending +2, asyncQueueMax +1 | timing: invalid (job VOID: FAIL: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle, run void; noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.7% of g |
| bp-disposespread-verify | 16:43 (00:43) | 6.3 | PASS | PASS: profile stopped at 4 events after 3.4 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 9 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired | 0 | 2189.9/1839.5/1516.9 -> 2317.7/2115.3/1227.4 | asyncStarted +5915, asyncRefused +135, asyncCollected +5913, asyncDropped +2, asyncUs +33231126, asyncMaxUs +153213, asyncPumpUs +18750143, asyncPumpWaits +4750, asyncRefusedM1Pending +40, asyncBackoffs +23, asyncQueueFull +2, asyncQueueMax +4 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 8baf3af01c14 ts3native.asi.0344/ts3native.asi.jobyield ts3native 0.36.38 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.1% of game CPUs); others on game CPUs 7.1%; clock avg/min 4237/4191 MHz; Verify disposespread (docs/74 §12): bp-di |

### bp-stuckcheckown-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 11 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-seasonmarkercache-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 9 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-overwatchstuckslice-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 11 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-drivertask-car-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 9 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-drivertask-boat-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 13 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-disposespread-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 9 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
