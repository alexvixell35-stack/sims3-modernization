# Queue master-smoke-m1122 2026-11-22

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master-smoke-m1122-2026-11-22.jsonl`.

## 2026-11-22T07:00 UTC, build e555c9a9c, jobs bp-m1122-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-m1122-smoke | 07:00 (15:00) | 7.2 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->142, stuckcheckasync_run 0->142, etspread_passes 0->1, stuckcheck_noplan 0->126, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->8<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1784/1690.1/1770.4 -> 2008.7/1911.5/1614.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 24.7% of game CPUs); noise high); build: asi 876d7de8d625 ts3native.asi.m1122 ts3native 0.36.299-m1122 (log ok); ts3fixes tuning c7b6325019fc ts3fixes.package.m1122 (expected game-copy/ts3fixes.package.m1122); noise: high (other apps 25.0% of game CPUs); others on game CPUs 25.0%; clock avg/min 3662/3389 MHz; [est 10 min] [natural play [cut: full row in the jsonl]<br>sim ms/s 162.9; p99 76 ms, p99.9 138, max 277, >=300 0, >=1000 0 (2.8 min) |
