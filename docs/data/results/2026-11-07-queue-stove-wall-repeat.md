# Queue stove-wall-repeat 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-wall-repeat-2026-11-07.jsonl`.

## 2026-11-07T09:59 UTC, build 50a3e4a0, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 09:59 (17:59) | 4 | PASS | PASS: profile stopped at 1 events after 0.5 min (off 0, on 1; 1 firings, 1 cycles; rule 1 events), cap 150 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->83, stuckcheckasync_run 0->83, etspread_passes 0->1, stuckcheck_noplan 0->81, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->1<br>PASS: repro events fired | 0 | 2131.2/1843.9/1614.1 -> 2205.5/1914.5/1520.7 | - | timing: invalid (noise medium; A/B 0 off / 1 on iterations, < 4 per arm (repro firings)); build: asi 1d4b92e4b303 not in DB ts3native 0.36.168c-diag1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3fixes.package.diag1); noise: medium (other apps 23.5% of game CPU |

## 2026-11-07T10:12 UTC, build 5d068a02, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 10:12 (18:12) | 3.2 | **FAIL** | FAIL: profile 150 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step stove_repeat_setup failed: layout blocks the slot: FridgeCheap slot FailedBecauseNoDestinationSpecified, path 0.0 m, candidates 0/0/0, blocker None;<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->69, stuckcheckasync_run 0->69, etspread_passes 0->1, stuckcheck_noplan 0->68, stuckch | 1 | 2159.3/1845.5/1602.1 -> 2191/1885.3/1541.1 | - | timing: invalid (job VOID: runInvalid: profiler cut short (74 of 9000 s); noise medium); build: asi 1d4b92e4b303 not in DB ts3native 0.36.168c-diag1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3fixes.package.diag1); noise: medium (other apps 26.3% of game CPUs |

## 2026-11-07T10:26 UTC, build 2727981e, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 10:26 (18:26) | 3.1 | **FAIL** | FAIL: profile 150 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step stove_repeat_setup failed: layout blocks the slot: FridgeCheap slot FailedBecauseNoDestinationSpecified, path 0.0 m, candidates 0/0/0, blocker None;<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->65, stuckcheckasync_run 0->65, etspread_passes 0->1, stuckcheck_noplan 0->62, stuckch | 1 | 2130.4/1841.5/1612.9 -> 2204.6/1882.5/1520.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (74 of 9000 s); noise medium); build: asi c71dd118821f not in DB ts3native 0.36.168c-diag2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3fixes.package.diag2); noise: medium (other apps 30.5% of game CPUs |

## 2026-11-07T10:40 UTC, build c0fd9615, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 10:40 (18:40) | 2.4 | **FAIL** | FAIL: events not fired: watchdogs missing; log /stove-repeat: layout ok/ 0 lines (want >= 1); log /stove-repeat: end/ 0 lines (want >= 1) | - | ? -> ? | - | timing: invalid (job FAIL: FAIL: events not fired: watchdogs missing; log /stove-repeat: layout ok/ 0 lines (want >= 1); log /stove-repeat: end/ 0 ; noise medium); build: asi 50cd3b664a12 not in DB ts3native 0.36.168c-diag2; ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3 |

## 2026-11-07T10:46 UTC, build c0fd9615, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 10:46 (18:46) | 3.2 | **FAIL** | FAIL: profile 150 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step stove_repeat_setup failed: layout blocks the slot: CounterMissionSuite front FailedBecauseNoDestinationSpecified, path 0.0 m, candidates 0/0/0, blocker None;<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->46, stuckcheckasync_run 0->46, etspread_passes 0->1, stuckcheck_noplan 0->44 | 1 | 2125.2/1838.7/1618.1 -> 2170.8/1869.2/1539.2 | - | timing: invalid (job VOID: runInvalid: profiler cut short (72 of 9000 s); noise medium); build: asi 50cd3b664a12 not in DB ts3native 0.36.168c-diag2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/ts3fixes.package.diag2); noise: medium (other apps 35.5% of game CPUs |

## 2026-11-07T10:52 UTC, build 917a13fc, jobs stove-wall-repeat

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-repeat | 10:52 (18:52) | 4.2 | **CRASH** | FAIL: profile 150 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 10:56:48 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>note: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void<br>note: UNKNOWN: | 1 | 2126.8/1844.5/1608.7 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium); build: asi 50cd3b664a12 not in DB ts3native 0.36.168c-diag2 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game |
