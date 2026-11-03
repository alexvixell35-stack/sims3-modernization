# Queue p6-integration 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/p6-integration-2026-10-25.jsonl`.

## 2026-10-25T04:16 UTC, build ea30f4f3, jobs bp-p6int-verify, ip-p6int-verify, ip-p6int-p6-ab, ip-p6int-hold1-ab, ip-p6int-hold2-ab, ip-p6int-stalelot-ab, ip-p6int-stalelotfp-ab, ip-p6int-ask-ab, ip-p6int-kinds-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-p6int-verify | 04:16 (12:16) | 5.7 | PASS | PASS: profile stopped at 4 events after 2.6 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm; 2 steps capped), cap 12 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2235.3/2015/1421.3 -> 2404/2264.8/1117.1 | asyncStarted +6099, asyncRefused +186, asyncCollected +6049, asyncUs +79128162, asyncMaxUs +448002, asyncPumpUs +29003030, asyncPumpWaits +10958, asyncRefusedM1Pending +28, asyncSkipped +50, asyncBackoffs +54, asyncQueueFull +139, asyncQueueMax +27 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.7% of game CPUs); others on game CPUs 6.7%; clock avg/min 4254/4193 MHz; docs/189 in |
| ip-p6int-verify | 04:22 (12:22) | 5.6 | PASS | PASS: profile stopped at 4 events after 2.8 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm; 4 steps capped), cap 12 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2222.7/1995.5/1439.6 -> 2317.6/2072.8/1338.8 | asyncStarted +3156, asyncRefused +277, asyncCollected +3133, asyncDropped +4, asyncUs +76571127, asyncMaxUs +549178, asyncPumpUs +34192111, asyncPumpWaits +5810, asyncRefusedM1Pending +9, asyncSkipped +19, asyncBackoffs +44, asyncQueueFull +250, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.5% of game CPUs); others on game CPUs 8.5%; clock avg/min 4197/4143 MHz; docs/189 in |
| ip-p6int-p6-ab | 04:27 (12:27) | 21.6 | **CRASH** | FAIL: profile 24 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 04:49:30 (TS3W.exe process gone)<br>FAIL: events not fired: m2Off missing; m2Trips missing; hgTrip missing; hgLongHold missing; stlVer_1 missing; dmxMismatch missing; p5bWdEscalate missing<br>PASS: no forbidden log lines | 1 | 2244.3/2010.7/1431.8 -> ? | - | timing: invalid (job VOID: runInvalid: profiler cut short (1151 of 1440 s); crash or abort (no "done; game alive"); noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.8% of game CPUs); others on game CPUs 9.8%; |
| ip-p6int-hold1-ab | 04:49 (12:49) | 24.1 | PASS | PASS: profile stopped at 32 events after 21.0 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 20 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2242.7/1998.2/1413.3 -> 2367.7/2132.3/1256.3 | asyncStarted +22288, asyncRefused +207, asyncCollected +21839, asyncDropped +14, asyncUs +274312088, asyncMaxUs +658272, asyncPumpUs +184336575, asyncPumpWaits +23272, asyncRefusedM1Pending +218, asyncSkipped +434, asyncBackoffs +193, asyncQueueFull +16, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.0% of game CPUs); others on game CPUs 6.0%; clock avg/min 4224/4106 MHz; docs/189 integration (c) timing ladder rung 2/7, IP, one load: in-loa |
| ip-p6int-hold2-ab | 05:13 (13:13) | 24.1 | PASS | PASS: profile stopped at 32 events after 21.0 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 16 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2245.9/2021.7/1415.9 -> 2383.2/2122.8/1273.4 | asyncStarted +22465, asyncRefused +224, asyncCollected +22000, asyncDropped +14, asyncUs +285031008, asyncMaxUs +585822, asyncPumpUs +185995327, asyncPumpWaits +23279, asyncRefusedM1Pending +172, asyncSkipped +449, asyncBackoffs +189, asyncQueueFull +4, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.4% of game CPUs); others on game CPUs 5.4%; clock avg/min 4225/4159 MHz; docs/189 integration (c) timing ladder rung 3/7, IP, one load: in-loa |
| ip-p6int-stalelot-ab | 05:37 (13:37) | 16.8 | **FAIL** | FAIL: profile 24 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step set_speed failed: game speed is locked (a dialog is open or the game is saving)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 1 | 2239.3/1999.2/1408.1 -> 2355.1/2095.9/1286.7 | asyncStarted +14818, asyncRefused +108, asyncCollected +14521, asyncDropped +19, asyncUs +191305757, asyncMaxUs +541089, asyncPumpUs +129229882, asyncPumpWaits +14510, asyncRefusedM1Pending +115, asyncSkipped +277, asyncBackoffs +178, asyncQueueFull +3, asyncQueueMax +32 | timing: invalid (job VOID: runInvalid: profiler cut short (879 of 1440 s); crash or abort (no "done; game alive"); noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.6% of game CPUs); others on game CPUs 5.6%;  |
| ip-p6int-stalelotfp-ab | 05:54 (13:54) | 24.3 | PASS | PASS: profile stopped at 32 events after 21.3 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 19 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2242.5/2020.5/1415.9 -> 2387.9/2265.2/1095.3 | asyncStarted +21492, asyncRefused +194, asyncCollected +21046, asyncDropped +18, asyncUs +282542257, asyncMaxUs +6899187, asyncPumpUs +190889160, asyncPumpWaits +22533, asyncRefusedM1Pending +176, asyncSkipped +428, asyncBackoffs +185, asyncQueueFull +2, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.1% of game CPUs); others on game CPUs 6.1%; clock avg/min 4230/4140 MHz; docs/189 integration (c) timing ladder rung 5/7, IP, one load: in-loa |
| ip-p6int-ask-ab | 06:19 (14:19) | 22.8 | PASS | PASS: profile stopped at 32 events after 20.0 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 9 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2246.2/2013.7/1430.3 -> 2401.1/2274.9/1092.2 | asyncStarted +15668, asyncRefused +126, asyncCollected +15376, asyncDropped +12, asyncUs +206126462, asyncMaxUs +594459, asyncPumpUs +131249705, asyncPumpWaits +16034, asyncRefusedM1Pending +137, asyncSkipped +280, asyncBackoffs +187, asyncQueueMax +30 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.9% of game CPUs); others on game CPUs 5.9%; clock avg/min 4227/4155 MHz; docs/189 integration (c) timing ladder rung 6/7, IP, one load: in-loa |
| ip-p6int-kinds-ab | 06:41 (14:41) | 24.1 | PASS | PASS: profile stopped at 32 events after 21.2 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 19 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2245.3/2018.4/1421.3 -> 2366.6/2123.7/1273.8 | asyncStarted +21222, asyncRefused +199, asyncCollected +20793, asyncDropped +10, asyncUs +317303409, asyncMaxUs +645455, asyncPumpUs +187206390, asyncPumpWaits +23654, asyncRefusedM1Pending +176, asyncSkipped +418, asyncBackoffs +188, asyncQueueFull +14, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.4% of game CPUs); others on game CPUs 6.4%; clock avg/min 4225/4139 MHz; docs/189 integration (c) timing ladder rung 7/7, IP, one load: in-loa |

### bp-p6int-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-hold1-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-hold2-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-stalelot-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-stalelotfp-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-ask-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-kinds-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T10:20 UTC, build a92fbb78, jobs ip-p6int-p6-ab, ip-p6int-hold1-ab, ip-p6int-hold2-ab, ip-p6int-stalelot-ab, ip-p6int-stalelotfp-ab, ip-p6int-ask-ab, ip-p6int-kinds-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-p6-ab | 10:20 (18:20) | 23.7 | PASS | PASS: profile stopped at 32 events after 20.7 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 12 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2236.8/1994.3/1443.9 -> 2378.4/2102.6/1292.1 | asyncStarted +22773, asyncRefused +198, asyncCollected +22451, asyncDropped +14, asyncUs +278630732, asyncMaxUs +769597, asyncPumpUs +183248499, asyncPumpWaits +22720, asyncRefusedM1Pending +176, asyncSkipped +308, asyncBackoffs +217, asyncQueueMax +31 | timing: invalid (noise medium); build: asi 83816d930686 not in DB ts3native 0.36.67 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.3% of game CPUs); others on game CPUs 9.3%; clock avg/min 4189/4142 MHz; docs/189 integration (c) timing ladder rung 1/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-p6-loop (off = the rungs |
| ip-p6int-hold1-ab | 10:44 (18:44) | 24.1 | PASS | PASS: profile stopped at 32 events after 21.1 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 18 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2246.5/2021.2/1415.9 -> 2388.7/2138.3/1236.2 | asyncStarted +21888, asyncRefused +194, asyncCollected +21449, asyncDropped +16, asyncUs +281468504, asyncMaxUs +1503973, asyncPumpUs +187729347, asyncPumpWaits +22071, asyncRefusedM1Pending +165, asyncSkipped +423, asyncBackoffs +175, asyncQueueFull +1, asyncQueueMax +32 | timing: invalid (noise medium); build: asi 83816d930686 not in DB ts3native 0.36.67 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.5% of game CPUs); others on game CPUs 7.5%; clock avg/min 4200/4064 MHz; docs/189 integration (c) timing ladder rung 2/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-hold1-loop (off = the ru |
| ip-p6int-hold2-ab | 11:08 (19:08) | 6.7 | **CRASH** | FAIL: profile 24 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 11:15:08 (TS3W.exe process gone)<br>FAIL: events not fired: m2Off missing; m2Trips missing; hgTrip missing; hgLongHold missing; stlVer_1 missing; dmxMismatch missing; p5bWdEscalate missing<br>PASS: no forbidden log lines | 1 | 2242.2/2015/1429.3 -> ? | - | timing: invalid (job VOID: runInvalid: profiler cut short (283 of 1440 s); crash or abort (no "done; game alive"); noise medium; A/B 3 off / 4 on iterations, < 4 per arm (repro firings)); build: asi 83816d930686 not in DB ts3native 0.36.67 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.2% of game CPUs); others on game CPUs 8.2%; cloc |
| ip-p6int-stalelot-ab | 11:15 (19:15) | 24.4 | PASS | PASS: profile stopped at 32 events after 21.5 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 18 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2237.5/1998/1413.3 -> 2384.3/2119.8/1259.9 | asyncStarted +21338, asyncRefused +172, asyncCollected +20900, asyncDropped +10, asyncUs +287032759, asyncMaxUs +3769622, asyncPumpUs +191208240, asyncPumpWaits +21222, asyncRefusedM1Pending +209, asyncSkipped +428, asyncBackoffs +167, asyncQueueFull +5, asyncQueueMax +32 | timing: invalid (noise medium); build: asi 83816d930686 not in DB ts3native 0.36.67 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.6% of game CPUs); others on game CPUs 9.6%; clock avg/min 4189/4095 MHz; docs/189 integration (c) timing ladder rung 4/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-stalelot-loop (off = the |
| ip-p6int-stalelotfp-ab | 11:39 (19:39) | 24.2 | PASS | PASS: profile stopped at 32 events after 21.2 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 19 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2244.9/2018.5/1415.9 -> 2395.9/2163.8/1220.8 | asyncStarted +21103, asyncRefused +152, asyncCollected +20677, asyncDropped +15, asyncUs +302101178, asyncMaxUs +3403931, asyncPumpUs +195306265, asyncPumpWaits +23563, asyncRefusedM1Pending +178, asyncSkipped +411, asyncBackoffs +175, asyncQueueFull +4, asyncQueueMax +32 | timing: invalid (noise medium); build: asi 83816d930686 not in DB ts3native 0.36.67 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 11.3% of game CPUs); others on game CPUs 11.3%; clock avg/min 4188/4142 MHz; docs/189 integration (c) timing ladder rung 5/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-stalelotfp-loop (off = |

## 2026-10-25T12:09 UTC, build dbcdc8ed, jobs ip-p6int-ask-ab, ip-p6int-kinds-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-ask-ab | 12:09 (20:09) | 24.1 | PASS | PASS: profile stopped at 32 events after 21.1 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 14 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2208.4/2010/1458.5 -> 2335.7/2146.3/1264.8 | asyncStarted +21346, asyncRefused +174, asyncCollected +20983, asyncDropped +17, asyncUs +305791304, asyncMaxUs +3506839, asyncPumpUs +194834885, asyncPumpWaits +21199, asyncRefusedM1Pending +155, asyncSkipped +345, asyncBackoffs +242, asyncQueueFull +13, asyncQueueMax +32 | timing: invalid (noise medium); build: asi 4f13c458a50b not in DB ts3native 0.36.72 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.7% of game CPUs); others on game CPUs 8.7%; clock avg/min 4194/4146 MHz; docs/189 integration (c) timing ladder rung 6/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-ask-loop (off = the rung |
| ip-p6int-kinds-ab | 12:33 (20:33) | 24.6 | PASS | PASS: profile stopped at 32 events after 21.6 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 23 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2209.3/2011/1457.8 -> 2371.1/2277/1145.3 | asyncStarted +21549, asyncRefused +213, asyncCollected +21125, asyncDropped +14, asyncUs +332666612, asyncMaxUs +2990484, asyncPumpUs +198595801, asyncPumpWaits +24090, asyncRefusedM1Pending +161, asyncSkipped +408, asyncBackoffs +339, asyncQueueFull +4, asyncQueueMax +31 | timing: invalid (noise medium); build: asi 4f13c458a50b not in DB ts3native 0.36.72 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.2% of game CPUs); others on game CPUs 7.2%; clock avg/min 4205/4151 MHz; docs/189 integration (c) timing ladder rung 7/7, IP, one load: in-load ABBA on repro route-queuefull-p6int-kinds-loop (off = the ru |

### ip-p6int-ask-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-p6int-kinds-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
