# Queue unbounded-caps-correct-1122c 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unbounded-caps-correct-1122c-2026-11-23.jsonl`.

## 2026-11-23T03:23 UTC, build b7cb38e63, jobs ip-capsc

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-capsc | 03:23 (11:23) | 12.1 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->218, stuckcheckasync_run 0->218, etspread_passes 0->1, stuckcheck_noplan 0->218, stuckcheck_notstuck 0->1<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1705.2/1625.8/1816.1 -> 1797.4/1701.4/1802.4 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 24.9% of game CPUs); noise high); build: asi eb590e1ce5ea ts3native.asi.unbounded-caps-1122c ts3native 0.36.299-ucap2 (log ok); ts3fixes tuning 7474070e74fe ts3fixes.package.unbounded-caps-1122 (expected game-copy/ts3fixes.package.unbounded-caps-1122); noise: high (other apps 23.1% of game CPUs); others on game CPUs 23.1% [cut: full row in the jsonl]<br>sim ms/s 159.1; p99 96 ms, p99.9 255, max 432, >=300 14, >=1000 0 (7.4 min) |
