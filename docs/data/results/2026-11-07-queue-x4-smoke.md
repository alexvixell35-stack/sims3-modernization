# Queue x4-smoke 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/x4-smoke-2026-11-07.jsonl`.

## 2026-11-07T15:38 UTC, build d5cf5898, jobs bp-x4-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-x4-smoke | 15:38 (23:38) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->261, stuckcheckasync_run 0->261, etspread_passes 0->1, stuckcheck_noplan 0->200, stuckcheck_notstuck 0->22, stuckcheck_rearmed 0->30, stuckcheck_reset 0->12 | 0 | 2135.5/1842.6/1607.6 -> 2267.3/2088.8/1348.8 | - | timing: invalid (noise medium); build: asi e6b48f5aa4e9 master0189/ts3native.asi.g2pair1107b ts3native 0.36.189 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189/ts3fixes.package.g2pair1107b (expected game-copy/ts3fixes.package.g2pair1107b); noise: medium (other apps 16.4% of game CPUs); others on game CPUs 16.4%; clock avg/min |
