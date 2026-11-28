# Queue hh-exempt-audit-correct-1118-rerun3-1118 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hh-exempt-audit-correct-1118-rerun3-1118-2026-11-18.jsonl`.

## 2026-11-18T14:43 UTC, build 64d54649b, jobs bp-hh-exempt-audit

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hh-exempt-audit | 14:43 (22:43) | 24.7 | PASS | PASS: woohootywait-check: profile 10 min, first window after load<br>PASS: serviceshowalarm-check: profile 10 min, first window after load<br>ts3fixes activity: exitcbsweep_longest 0->2, owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->195, stuckcheckasync_run 0->192, etspread_passes 0->1, stuckcheck_noplan 0->161, stuckcheck_notstuck 0->18, stuckcheck_rearmed 0->16 [cut: full row in the jsonl] | 0 | 1750.3/1633/1361.8 -> 1947.6/1868.6/1549.9 | - | timing: invalid (woohootywait-check: job VOID: runInvalid: noisy machine (others 46.3% of game CPUs); noise high; serviceshowalarm-check: job VOID: runInvalid: noisy machine (others 45.2% of game CPUs); noise high); build: asi fa87ce688f06 ts3native.asi.gcmark-p4-1118 ts3native 0.36.285-g5p4 (log ok) [cut: full row in the jsonl]<br>woohootywait-check: sim ms/s 190.4; p99 97 ms, p99.9 154, max 532, >=300 4, >=1000 0 (6.0 min)<br>serviceshowalarm-check: sim ms/s 195.0; p99 97 ms, p99.9 153, max 327, >=300 1, >=1000 0 (6.0 min) |
