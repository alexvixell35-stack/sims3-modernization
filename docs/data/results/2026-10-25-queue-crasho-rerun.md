# Queue crasho-rerun 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/crasho-rerun-2026-10-25.jsonl`.

## 2026-10-25T12:58 UTC, build 9a11373b, jobs ip-p6int-hold2-ab-crasho

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-hold2-ab-crasho | 12:58 (20:58) | 23.8 | PASS | PASS: profile stopped at 32 events after 20.8 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 17 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2206.6/2008.1/1432.7 -> 2402.9/2282.2/1107.9 | asyncStarted +21730, asyncRefused +149, asyncCollected +21316, asyncDropped +11, asyncUs +299443276, asyncMaxUs +676182, asyncPumpUs +199226130, asyncPumpWaits +23313, asyncRefusedM1Pending +160, asyncSkipped +403, asyncBackoffs +237, asyncQueueFull +3, asyncQueueMax +30 | timing: invalid (noise medium; heavy tool CrashForensics.Guards; heavy tool CrashForensics.DbIndexLog; heavy tool CrashForensics.SceneTreeGuard); build: asi 5a77eaedb18a not in DB ts3native 0.36.71 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.0% of game CPUs); others on game CPUs 9.0%; clock avg/min 4196/4128 MHz; Rerun of p6-integ |

### ip-p6int-hold2-ab-crasho: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T13:22 UTC, build 9a11373b, jobs ip-p6int-hold2-ab-crasho

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-hold2-ab-crasho | 13:23 (21:23) | 23.8 | PASS | PASS: profile stopped at 32 events after 20.9 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 16 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2243.5/2020.8/1408.1 -> 2389.4/2159.4/1235.8 | asyncStarted +19687, asyncRefused +168, asyncCollected +19272, asyncDropped +13, asyncUs +277464294, asyncMaxUs +1451025, asyncPumpUs +176403272, asyncPumpWaits +20404, asyncRefusedM1Pending +166, asyncSkipped +401, asyncBackoffs +128, asyncQueueFull +2, asyncQueueMax +32 | timing: invalid (job VOID: runInvalid: noisy machine (others 16.0% of game CPUs); noise high; heavy tool CrashForensics.Guards; heavy tool CrashForensics.DbIndexLog; heavy tool CrashForensics.SceneTreeGuard); build: asi 5a77eaedb18a not in DB ts3native 0.36.71 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: high (other apps 15.9% of game CPUs); others on  |

### ip-p6int-hold2-ab-crasho: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
