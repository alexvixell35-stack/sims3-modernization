# Queue s3pp-compat-1124 2026-11-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/s3pp-compat-1124-2026-11-24.jsonl`.

## 2026-11-24T00:17 UTC, build cd648f975, jobs bp-s3ppgc-smoke, bp-s3ppgc-savereload, ip-s3ppgc-play, sv-s3ppgc-gcsoak, sv-s3ppgc-gcsoak-bypassoff

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-s3ppgc-smoke | 00:17 (08:17) | 7.2 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->144, stuckcheckasync_run 0->144, etspread_passes 0->1, stuckcheck_noplan 0->124, stuckcheck_notstuck 0->9, stuckcheck_rearmed 0->14, stuckcheck_reset 0->3<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0 [cut: full row in the jsonl] | 0 | 1781.8/1689.1/1791.5 -> 1996.2/1898.2/1687.8 | - | timing: invalid (noise medium); build: asi a1e2646ee07e ts3native.asi.m1123c ts3native 0.36.299-m1123c (log ok); ts3fixes tuning bee9aef384f0 ts3fixes.package.m1123c (expected game-copy/ts3fixes.package.m1123c); noise: medium (other apps 15.7% of game CPUs); others on game CPUs 15.7%; clock avg/min 4126/4085 MHz; [est 12 min] SMOKE: BP 5 min speed 3, m1123c release defaults [cut: full row in the jsonl]<br>sim ms/s 154.8; p99 76 ms, p99.9 144, max 289, >=300 0, >=1000 0 (2.6 min) |
