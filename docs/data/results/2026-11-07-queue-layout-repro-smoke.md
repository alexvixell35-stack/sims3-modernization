# Queue layout-repro-smoke 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/layout-repro-smoke-2026-11-07.jsonl`.

## 2026-11-07T04:32 UTC, build ad136c83, jobs layout-repro-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| layout-repro-smoke | 04:33 (12:33) | 2.5 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step build_layout failed: no free area of 5x6 tiles on : give origin or use an emptier lot<br>ts3fixes activity: etspread_passes 0->1<br>FAIL: events not fired: log /stove-wall: layout passed/ 0 lines (want >= 1) | 1 | 2128.3/1839.1/1614.7 -> 2101.3/1865.8/1589 | - | timing: invalid (job VOID: runInvalid: profiler cut short (36 of 480 s); crash or abort (no "done; game alive"); noise high); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 not in DB (expected build/ts3fixes.package); noise: high (other apps 18.9% of game CPUs); others on game CPUs 18.9%; clock avg/min 4161/4150 MHz |

## 2026-11-07T04:40 UTC, build 49d8c121, jobs layout-repro-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| layout-repro-smoke | 04:40 (12:40) | 2.6 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step build_layout: build_layout error: {"done":true,"calib":{},"rotation":0,"error":"McpException: live calibration failed before any change: only 0 of 0 floor picks hit the plane at y=102.629997253418; point the camera at the lot / stack: #0: 0x000cb throw      in Ts3Agent.Ts3Agent.Handlers:LayCalibLive (Ts3Agent.Handlers/Lay,System. | 1 | 2130.5/1827.4/1612.9 -> 2120.6/1860.9/1581.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (40 of 480 s); noise medium); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.4% of game CPUs); others on game CPUs 17.4%; clock avg/min 4166/4153 MHz |

## 2026-11-07T04:49 UTC, build 2c3d0ecc, jobs layout-repro-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| layout-repro-smoke | 04:49 (12:49) | 2.8 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step build_layout: build_layout error: {"done":true,"calib":{"live":{"picks_error":"only 0 of 0 floor picks hit the plane at y=102.629997253418; 285 points tried on the 1024x768 window, 285 found no floor tile (bare ground is no floor): point the camera at a floored lot","centre_px":[512,420],"window":"1024x768","target":{"x":1392.199 | 1 | 2147.6/1840.4/1606.3 -> 2162.5/1895.8/1491 | - | timing: invalid (job VOID: runInvalid: profiler cut short (54 of 480 s); noise medium); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.4% of game CPUs); others on game CPUs 17.4%; clock avg/min 4164/4134 MHz |

## 2026-11-07T04:58 UTC, build 03febfcb, jobs layout-repro-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| layout-repro-smoke | 04:58 (12:58) | 10.5 | PASS | PASS: profile 8 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->216, stuckcheckasync_run 0->216, etspread_passes 0->1, stuckcheck_noplan 0->190, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->16, stuckcheck_reset 0->7<br>PASS: repro events fired | 0 | 2128/1842.9/1588.8 -> 2237.4/1943.8/1496.6 | - | timing: invalid (noise medium); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 not in DB (expected build/ts3fixes.package); noise: medium (other apps 16.8% of game CPUs); others on game CPUs 16.8%; clock avg/min 4154/4136 MHz |
