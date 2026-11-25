# Queue rc1111d-smoke 2026-11-11

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rc1111d-smoke-2026-11-11.jsonl`.

## 2026-11-11T03:35 UTC, build a1123d06, jobs bp-rc1111d-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rc1111d-smoke | 03:35 (11:35) | 4.1 | PASS | PASS: profile 2 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->38, stuckcheckasync_run 0->38, etspread_passes 0->1, stuckcheck_noplan 0->33, stuckcheck_notstuck 0->2, stuckcheck_rearmed 0->2, stuckcheck_reset 0->1<br>PASS: repro events fired | 0 | 2168/1886.3/1604.8 -> 2212.4/1939.1/1517.4 | - | timing: invalid (noise medium); build: asi f05a9fd6a71c ts3native.asi.rc1111d ts3native 0.36.240 (log ok); ts3fixes tuning 3bee114eaf9b ts3fixes.package.rc1111c (expected game-copy/ts3fixes.package.rc1111c); noise: medium (other apps 22.4% of game CPUs); others on game CPUs 22.4%; clock avg/min 4133/4115 MHz; [natural play, no cheats] rc1111d release pair smoke, Baseline-BP-Spring, 2 min: ts3nativ |
