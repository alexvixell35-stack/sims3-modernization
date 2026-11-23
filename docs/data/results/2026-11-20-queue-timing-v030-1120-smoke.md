# Queue timing-v030-1120-smoke 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/timing-v030-1120-smoke-2026-11-20.jsonl`.

## 2026-11-20T11:39 UTC, build fc606d175, jobs bp-v030-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-20T11:46 UTC, build fc606d175, jobs bp-v030-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v030-smoke | 11:47 (19:47) | 12.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->272, stuckcheckasync_run 0->270, etspread_passes 0->1, stuckcheck_noplan 0->220, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->24, stuckcheck_reset 0->7<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1763.5/1677.8/1758.5 -> 1957.5/1870.6/1619.8 | - | noisy: does not count, rerun as bp-v030-smoke-rerun; timing: invalid (noise high); build: asi 527949741d2b ts3native.asi.rc-1119b/ts3native.asi.rel030 ts3native 0.36.295-rc1119b (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b/ts3fixes.package.rel030 (expected game-copy/ts3fixes.package.rc-1119b); noise: high (NOT comparable (load or clock)); others on game CPUs 0.2% (apps [cut: full row in the jsonl]<br>sim ms/s 190.1; p99 83 ms, p99.9 139, max 337, >=300 1, >=1000 0 (5.8 min) |
| bp-v030-smoke-rerun | 11:59 (19:59) | 1.2 | **FAIL** | PASS: repro events fired<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | rerun of bp-v030-smoke: counts instead of it; timing: invalid (job FAIL: exit code ?; noise medium); build: asi 527949741d2b ts3native.asi.rc-1119b/ts3native.asi.rel030 ts3native 0.36.295-rc1119b; ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b/ts3fixes.package.rel030 (expected game-copy/ts3fixes.package.rc-1119b); noise: medium (other apps 4.8% of game CPUs, other CPUs 73.8% busy) [cut: full row in the jsonl] |

Stopped: bp-v030-smoke-rerun failed (stopQueueOnFail): cycle.ts --save exit 1.

## 2026-11-20T12:06 UTC, build 52f5c5368, jobs bp-v030-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v030-smoke | 12:06 (20:06) | 13.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->217, stuckcheckasync_run 0->217, etspread_passes 0->1, stuckcheck_noplan 0->188, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->16, stuckcheck_reset 0->5<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1765.2/1679.8/1711.3 -> 1951.7/1735.7/1617.8 | - | timing: invalid (noise medium); build: asi 527949741d2b ts3native.asi.rc-1119b/ts3native.asi.rel030 ts3native 0.36.295-rc1119b (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b/ts3fixes.package.rel030 (expected game-copy/ts3fixes.package.rc-1119b); noise: medium (other CPUs 87.3% busy); others on game CPUs 0.5% (apps; incl. kernel 22.5%); clock avg/min 3537/3193 MHz [cut: full row in the jsonl]<br>sim ms/s 194.8; p99 91 ms, p99.9 155, max 477, >=300 2, >=1000 0 (8.8 min) |
