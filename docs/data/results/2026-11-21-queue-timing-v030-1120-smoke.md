# Queue timing-v030-1120-smoke 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/timing-v030-1120-smoke-2026-11-21.jsonl`.

## 2026-11-21T10:23 UTC, build 3e33627e7, jobs bp-v030-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v030-smoke | 10:23 (18:23) | 12.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->264, stuckcheckasync_run 0->264, etspread_passes 0->1, stuckcheck_noplan 0->212, stuckcheck_notstuck 0->29, stuckcheck_rearmed 0->29, stuckcheck_reset 0->5<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1764.3/1679/1708.5 -> 1999.9/1921.9/1570.7 | - | timing: valid-single; build: asi ba8c7211b7b9 ts3native.asi.hf031/ts3native.asi.rel031 ts3native 0.36.295a (log ok); ts3fixes tuning 710a355f3c57 ts3fixes.package.rc-1119b/ts3fixes.package.rel030 (expected game-copy/ts3fixes.package.rc-1119b); noise: low; others on game CPUs 0.3% (apps; incl. kernel 23.1%); clock avg/min 3728/3409 MHz [cut: full row in the jsonl]<br>sim ms/s 191.7; p99 83 ms, p99.9 151, max 297, >=300 0, >=1000 0 (5.8 min) |
