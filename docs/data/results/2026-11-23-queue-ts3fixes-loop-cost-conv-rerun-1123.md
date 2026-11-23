# Queue ts3fixes-loop-cost-conv-rerun-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ts3fixes-loop-cost-conv-rerun-1123-2026-11-23.jsonl`.

## 2026-11-23T00:30 UTC, build 4eb50c9a7, jobs bp-conversation-gone-check-cadence

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-conversation-gone-check-cadence | 00:30 (08:30) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->16, deadrefcadence_snapshots 0->83, deadrefcadence_desc_skipped 0->60, deadrefcadence_snapshot_objects 0->10705, owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->251, stuckcheckasync_run 0->251, etspread_passes 0->1, stuckcheck_noplan 0->196, stuckcheck_notstuck 0->24 [cut: full row in the jsonl] | 0 | 1790.8/1711.1/1803.9 -> 2048.6/1971/1516.5 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 40.5% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok) [cut: full row in the jsonl]<br>sim ms/s 165.3; p99 72 ms, p99.9 149, max 327, >=300 2, >=1000 0 (6.1 min) |
