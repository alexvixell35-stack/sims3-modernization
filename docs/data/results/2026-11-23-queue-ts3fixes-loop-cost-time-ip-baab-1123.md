# Queue ts3fixes-loop-cost-time-ip-baab-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ts3fixes-loop-cost-time-ip-baab-1123-2026-11-23.jsonl`.

## 2026-11-23T04:26 UTC, build cef177609, jobs bp-loopcost-ab-smoke, ip-loopcost-baab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-loopcost-ab-smoke | 04:26 (12:26) | 8.2 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->3, deadrefcadence_snapshots 0->19, deadrefcadence_desc_skipped 0->15, deadrefcadence_snapshot_objects 0->11029, owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->77, stuckcheckasync_run 0->77, etspread_passes 0->1, stuckcheck_noplan 0->63, stuckcheck_notstuck 0->7 [cut: full row in the jsonl] | 0 | 1789/1696.9/1716.3 -> 1965.1/1749.5/1581.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 19.7% >= 10%); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok) [cut: full row in the jsonl]<br>sim ms/s off 170.5 / on 163.7 (on - off -6.8, -4.0%); off: p99 161 ms, p99.9 250, max 318, >=300 1, >=1000 0 (0.9 min); on: p99 115 ms, p99.9 186, max 361, >=300 1, >=1000 0 (1.2 min) |
