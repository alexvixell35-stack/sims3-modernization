# Queue null-service 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/null-service-2026-11-01.jsonl`.

## 2026-11-01T09:10 UTC, build a03b824, jobs bp-null-service-check, lr-null-service-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-null-service-check | 09:10 (17:10) | 0 | **FAIL** | cycle.ts --restart exit 1 | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown); Correctness (5 min, Bridgeport test save): repro null-service-check. Pass: every step ok (off: the EA reader threw NullReferenceException on the null; on: it returned), nullservice_dropped 1 (fixesRequire). |

Stopped: bp-null-service-check failed (stopQueueOnFail): cycle.ts --restart exit 1.

## 2026-11-01T09:11 UTC, build a03b824, jobs bp-null-service-check, lr-null-service-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-null-service-check | 09:11 (17:11) | 3 | **FAIL** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: nullservice broken: wrong outcome: the reader returned 27 services; our null still in the list after the reader: False; switch False, 31 entries, 0 null, dropped so far 0<br>note: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle (forced repro: not the  | 1 | 2255.1/2148.1/1347.5 -> 2380.6/2323.4/1165.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (1 of 300 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise medium); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 20cfbc00a2b2 not in DB (expected build/ts3fixes.package); noise: medium (GPU 3D others 27.2%); others on game CPUs 0.9%; clock avg/min 4672/4635 M |

Stopped: bp-null-service-check failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts).

## 2026-11-01T09:16 UTC, build a03b824, jobs bp-null-service-check, lr-null-service-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-null-service-check | 09:16 (17:16) | 8.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->129, stuckcheckasync_run 0->129, etspread_passes 0->1, stuckcheck_noplan 0->121, stuckcheck_notstuck 0->7, stuckcheck_rearmed 0->2 | 0 | 2263.8/2103.1/1388.6 -> 2522.6/2415.8/998.2 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 67% from 165%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 20cfbc00a2b2 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock)); others on game CPUs 1.6%; clock avg/min 4187/1932 MHz; Correctness (5 min, Bridgeport test save): repro null- |
| lr-null-service-check | 09:24 (17:24) | 8.4 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->140, stuckcheckasync_run 0->140, etspread_passes 0->1, stuckcheck_noplan 0->129, stuckcheck_notstuck 0->6, stuckcheck_rearmed 0->5 | 0 | 2289.1/2159.9/1361.7 -> 2668.5/2522.8/879.6 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 100% from 164%); noise high); build: asi 076fd2d4655e ts3native.asi.rel024 ts3native 0.36.105 (log ok); ts3fixes tuning 20cfbc00a2b2 not in DB (expected build/ts3fixes.package); noise: high (NOT comparable (load or clock), GPU 3D others 38.7%); others on game CPUs 1.5%; clock avg/min 4204/2511 MHz; Correctness (5 min, day-406 save |
