# Queue master-smoke 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master-smoke-2026-11-07.jsonl`.

## 2026-11-07T14:16 UTC, build 01453f07, jobs bp-master0189-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-master0189-smoke | 14:16 (22:16) | 6.8 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->130, stuckcheckasync_run 0->130, etspread_passes 0->1, stuckcheck_noplan 0->102, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->18, stuckcheck_reset 0->2<br>PASS: repro events fired | 0 | 2130.2/1842.1/1592.9 -> 2236.9/2046.3/1374.8 | - | timing: invalid (noise medium); build: asi e6b48f5aa4e9 master0189 ts3native 0.36.189 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189 (expected game-copy/ts3fixes.package.master0189); noise: medium (other apps 22.3% of game CPUs); others on game CPUs 22.3%; clock avg/min 4169/4152 MHz; [natural play, no cheats] master head 0. |
