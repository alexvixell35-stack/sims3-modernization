# Queue ts3fixes-loop-cost-time-1122-smoke 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ts3fixes-loop-cost-time-1122-smoke-2026-11-23.jsonl`.

## 2026-11-23T00:46 UTC, build 4eb50c9a7, jobs bp-loopcost-ab-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-loopcost-ab-smoke | 00:46 (08:46) | 7.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: deadrefcadence_sim_skipped 0->1, deadrefcadence_snapshots 0->32, deadrefcadence_desc_skipped 0->18, deadrefcadence_snapshot_objects 0->10957, owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->140, stuckcheckasync_run 0->140, etspread_passes 0->1, stuckcheck_noplan 0->125, stuckcheck_notstuck 0->9 [cut: full row in the jsonl] | 0 | 1794.5/1708.9/1801.2 -> 1982.2/1915.7/1557.8 | - | timing: invalid (noise medium); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 1467b8d4c707 ts3fixes.package.ts3fixes-loop-cost-1122/ts3fixes.package.ts3fixes-loop-cost-1122b/ts3fixes.package.ts3fixes-loop-cost-1122c/ts3fixes.package.ts3fixes-loop-cost-1122d (expected game-copy/ts3fixes.package.loopcost4) [cut: full row in the jsonl]<br>sim ms/s off 149.2 / on 155.4 (on - off +6.2, +4.2%); off: p99 59 ms, p99.9 122, max 187, >=300 0, >=1000 0 (0.9 min); on: p99 <50 ms, p99.9 112, max 280, >=300 0, >=1000 0 (1.0 min) |

A/B summary of this run (sim ms/s = script + route + animPose + locomotion, arm-ms.ts; hitches per 10 min over the same arms, hitch-ab.ts):

- ts3fixes-loop-cost-time-1122-smoke-bp-loopcost-ab-smoke: sim ms/s off 149.2 / on 155.4, on - off +6.2 (+4.2%); >=300 ms per 10 min off 0.0 / on 0.0, >=1000 off 0.0 / on 0.0 (1 off, 1 on run)
