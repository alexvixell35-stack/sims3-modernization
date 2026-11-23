# Queue kd-cut-camera-recheck 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kd-cut-camera-recheck-2026-11-07.jsonl`.

## 2026-11-07T04:02 UTC, build f762a519, jobs sv-kd-cut-camera-recheck

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kd-cut-camera-recheck | 04:02 (12:02) | 3.5 | **FAIL** | FAIL: profile 14 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step camera_to_tagged failed: Cornelius Nelson is not currently in the world (not instantiated)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1<br>FAIL: events not fired: kdCamWould=0 (want >=1); kdCamHeld=0 (want >=1); log /kd cam recheck: / 0 lines (want >= 1) | 1 | 2100.1/1790.3/1644.9 -> 2148.5/1828.5/1563.1 | asyncStarted +872, asyncRefused +31, asyncCollected +872, asyncUs +21775715, asyncMaxUs +287658, asyncPumpUs +3190938, asyncPumpWaits +2509, asyncRefusedM1Pending +3, asyncBackoffs +3, asyncQueueMax +6 | timing: invalid (job VOID: runInvalid: profiler cut short (96 of 840 s); crash or abort (no "done; game alive"); noise high; A/B 3 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 9014b3727e88 ts3native.asi.kdcam ts3native 0.36.186 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: high (other apps 29.3% of game CPUs); others on game CPUs 29.3 |

Stopped: sv-kd-cut-camera-recheck failed (stopQueueOnFail): FAIL: profile 14 min, seed 12345 (rng-compare.ts).

## 2026-11-07T04:06 UTC, build b113f7e5, jobs sv-kd-cut-camera-recheck

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kd-cut-camera-recheck | 04:06 (12:06) | 6 | PASS | PASS: profile stopped at 20 events after 2.5 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->117, stuckcheckasync_run 0->117, etspread_passes 0->1, stuckcheck_noplan 0->102, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->4<br>PASS: repro events | 0 | 2092.3/1776.5/1660.9 -> 2147/1947.1/1459.6 | asyncStarted +2251, asyncRefused +74, asyncCollected +2250, asyncUs +63297370, asyncMaxUs +537989, asyncPumpUs +8685848, asyncPumpWaits +6562, asyncRefusedM1Pending +5, asyncBackoffs +20, asyncQueueMax +7 | timing: invalid (job VOID: runInvalid: noisy machine (others 36.5% of game CPUs); noise high); build: asi 9014b3727e88 ts3native.asi.kdcam ts3native 0.36.186 (log ok); ts3fixes tuning 8a7002f70c55 not in DB (expected build/ts3fixes.package); noise: high (other apps 35.4% of game CPUs); others on game CPUs 35.4%; clock avg/min 4090/3990 MHz; Correctness, forced ABBA on kd-cut-camera-recheck: camera |
