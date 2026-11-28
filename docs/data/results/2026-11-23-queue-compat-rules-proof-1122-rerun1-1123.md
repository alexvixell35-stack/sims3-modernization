# Queue compat-rules-proof-1122-rerun1-1123 2026-11-23

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compat-rules-proof-1122-rerun1-1123-2026-11-23.jsonl`.

## 2026-11-23T02:28 UTC, build e710e1091, jobs ip-rules-r-resolve-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-rules-r-resolve-on | 02:28 (10:28) | 12.5 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->238, stuckcheckasync_run 0->238, etspread_passes 1->1, stuckcheck_noplan 0->237, stuckcheck_notstuck 0->1, stuckcheck_rearmed 0->1<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1746.8/1658.9/1803.7 -> 1819.3/1728.1/1797.9 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 33.8% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning 8b2859c3b588 ts3fixes.package.compat-resolve-1122h (expected game-copy/ts3fixes.package.compat-resolve-1122h); noise: high (other apps 31.7% of game CPUs); others on game CPUs 31.7% [cut: full row in the jsonl]<br>sim ms/s 160.4; p99 100 ms, p99.9 247, max 517, >=300 9, >=1000 0 (7.1 min) |
