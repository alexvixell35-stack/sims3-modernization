# Queue ts3fixes-loop-cost-conv-rerun-1123 2026-11-22

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ts3fixes-loop-cost-conv-rerun-1123-2026-11-22.jsonl`.

## 2026-11-22T22:11 UTC, build 26d13f504, jobs bp-conversation-gone-check-cadence

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-conversation-gone-check-cadence | 22:11 (06:11) | 12.3 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->14, deadrefcadence_snapshots 0->70, deadrefcadence_desc_skipped 0->51, deadrefcadence_snapshot_objects 0->10942, owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->212, stuckcheckasync_run 0->212, etspread_passes 0->1, stuckcheck_noplan 0->173, stuckcheck_notstuck 0->12 [cut: full row in the jsonl] | 0 | 1780/1680.7/1778 -> 2059.1/1965.8/1493.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 16.4% >= 10%); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 1467b8d4c707 ts3fixes.package.ts3fixes-loop-cost-1122/ts3fixes.package.ts3fixes-loop-cost-1122b (expected game-copy/ts3fixes.package.loopcost2); noise: high (other apps 47.6% of game CPUs [cut: full row in the jsonl]<br>sim ms/s 129.8; p99 65 ms, p99.9 125, max 421, >=300 1, >=1000 0 (5.5 min) |

## 2026-11-22T23:15 UTC, build 458acc87a, jobs bp-conversation-gone-check-cadence

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-conversation-gone-check-cadence | 23:15 (07:15) | 12.2 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->20, deadrefcadence_snapshots 0->84, deadrefcadence_desc_skipped 0->63, deadrefcadence_snapshot_objects 0->10699, owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->246, stuckcheckasync_run 0->244, etspread_passes 0->1, stuckcheck_noplan 0->199, stuckcheck_notstuck 0->23 [cut: full row in the jsonl] | 0 | 1787.7/1693.1/1805.1 -> 2031.2/1937.1/1551.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 33.8% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 1467b8d4c707 ts3fixes.package.ts3fixes-loop-cost-1122/ts3fixes.package.ts3fixes-loop-cost-1122b/ts3fixes.package.ts3fixes-loop-cost-1122c (expected game-copy/ts3fixes.package.loopcost3) [cut: full row in the jsonl]<br>sim ms/s 159.2; p99 70 ms, p99.9 125, max 431, >=300 2, >=1000 0 (8.8 min) |

## 2026-11-22T23:53 UTC, build 707ebb1c2, jobs bp-conversation-gone-check-cadence

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-conversation-gone-check-cadence | 23:53 (07:53) | 12.2 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->21, deadrefcadence_snapshots 0->85, deadrefcadence_desc_skipped 0->64, deadrefcadence_snapshot_objects 0->10866, owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->278, stuckcheckasync_run 0->278, etspread_passes 0->1, stuckcheck_noplan 0->233, stuckcheck_notstuck 0->26 [cut: full row in the jsonl] | 0 | 1791.5/1693.7/1802.3 -> 2041.2/1949.6/1537.1 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 32.0% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 1467b8d4c707 ts3fixes.package.ts3fixes-loop-cost-1122/ts3fixes.package.ts3fixes-loop-cost-1122b/ts3fixes.package.ts3fixes-loop-cost-1122c (expected game-copy/ts3fixes.package.loopcost3) [cut: full row in the jsonl]<br>sim ms/s 159.5; p99 67 ms, p99.9 127, max 267, >=300 0, >=1000 0 (8.8 min) |
