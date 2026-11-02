# Queue events-batch-1-rest 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/events-batch-1-rest-2026-11-01.jsonl`.

## 2026-11-01T13:01 UTC, build 23a5283, jobs bp-events-batch-1-rest

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-events-batch-1-rest | 13:01 (21:01) | 3.3 | **FAIL** | FAIL: egg-spot-check: profile 5 min, first window after load<br>FAIL: repro step reflect_call failed: TargetException: Unable to invoke an invalid target.<br>STOP: egg-spot-check failed: 1 repros not run (daytime rule; TS3_NIGHT=1 or reuseSession.onFail "restart" reloads and goes on)<br>note: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle (forced repro: not the job | 1 | 2312.8/2139.3/1354.3 -> 2405/2195.2/1290.1 | - | timing: invalid (job FAIL: FAIL: egg-spot-check: profile 5 min, first window after load; FAIL: repro step reflect_call failed: TargetException: Una; egg-spot-check: job VOID: runInvalid: profiler cut short (1 of 300 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning  |

Stopped: repro egg-spot-check of bp-events-batch-1-rest failed (daytime rule).

## 2026-11-01T13:20 UTC, build 75a7f3a, jobs bp-events-batch-1-rest

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-events-batch-1-rest | 13:20 (21:20) | 11.8 | **FAIL** | PASS: egg-spot-check: profile 5 min, first window after load<br>FAIL: graduation-table-check: profile 5 min, first window after load<br>FAIL: repro step reflect_call failed: InvalidOperationException: graduationtable fixed: wrong outcome: GraduateInCityHall threw NullReferenceException; AttendGraduation threw NullReferenceException; actor Lenny Shutter StageComplete False, ticks +0; switch True, t | 1 | 2298.7/2157.3/1339.6 -> 2364.9/2166.3/1294.3 | - | timing: invalid (job FAIL: FAIL: graduation-table-check: profile 5 min, first window after load; FAIL: repro step reflect_call failed: InvalidOpera; egg-spot-check: job VOID: runInvalid: noisy machine (clock fell to 114% from 149%); noise high; graduation-table-check: job VOID: runInvalid: profiler cut short (2 of 300 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; |

Stopped: bp-events-batch-1-rest failed (stopQueueOnFail): FAIL: graduation-table-check: profile 5 min, first window after load.

## 2026-11-01T13:33 UTC, build 75a7f3a, jobs bp-events-batch-1-rest

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-events-batch-1-rest | 13:34 (21:34) | 8.4 | PASS | PASS: graduation-table-check: profile 5 min, first window after load<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->104, stuckcheckasync_run 0->104, etspread_passes 0->1, stuckcheck_noplan 0->99, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->6 | 0 | 2361.1/2158.1/1300.6 -> 2596.2/2519.1/888.9 | - | timing: invalid (graduation-table-check: job VOID: runInvalid: noisy machine (clock fell to 124% from 159%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning daeecb3b4962 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock), GPU 3D others 41.5%); others on game CPUs 1.9%; clock avg/min 3486/2635 MHz; Correctn |
