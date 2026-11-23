# Queue watergrid-prof-correct-1122-m1122 2026-11-22

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/watergrid-prof-correct-1122-m1122-2026-11-22.jsonl`.

## 2026-11-22T07:34 UTC, build e555c9a9c, jobs bp-wgprof-correct

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-wgprof-correct | 07:34 (15:34) | 12.1 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->250, stuckcheckasync_run 0->250, etspread_passes 0->1, stuckcheck_noplan 0->194, stuckcheck_notstuck 0->27, stuckcheck_rearmed 0->32, stuckcheck_reset 0->8<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1741.8/1669.4/1812.8 -> 1962.8/1904.8/1539.6 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 25.7% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning c7b6325019fc ts3fixes.package.m1122 (expected game-copy/ts3fixes.package.m1122); noise: high (other apps 26.2% of game CPUs); others on game CPUs 26.2%; clock avg/min 3546/3284 MHz [cut: full row in the jsonl]<br>sim ms/s 154.1; p99 69 ms, p99.9 139, max 336, >=300 1, >=1000 0 (6.1 min) |
