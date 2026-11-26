# Queue rebaseline-1116c-smoke 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rebaseline-1116c-smoke-2026-11-16.jsonl`.

## 2026-11-16T15:42 UTC, build 68447d379, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 15:42 (23:42) | 12 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->254, stuckcheckasync_run 0->254, etspread_passes 0->1, stuckcheck_noplan 0->200, stuckcheck_notstuck 0->28, stuckcheck_rearmed 0->28, stuckcheck_reset 0->5<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1758.4/1634.9/1813.3 -> 1997.9/1917.6/1588.9 | - | timing: invalid (noise medium); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: medium (other apps 35.0% of game CPUs); others on game CPUs 35.0%; clock avg/min 4124/3984 MHz; Measurement smoke (AGENTS.md): BP 10 <br>sim ms/s 206.1; p99 94 ms, p99.9 160, max 283, >=300 0, >=1000 0 (6.2 min) |
