# Queue v025-final-smoke 2026-11-05

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v025-final-smoke-2026-11-05.jsonl`.

## 2026-11-05T06:16 UTC, build 0d9ef38c, jobs sv-v025-final-smoke, ip-v025-final-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-v025-final-smoke | 06:16 (14:16) | 7.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->153, stuckcheckasync_run 0->153, etspread_passes 0->1, stuckcheck_noplan 0->144, stuckcheck_notstuck 0->9, stuckcheck_rearmed 0->2<br>PASS: repro events fired | 0 | 2047.9/1796.1/1676.9 -> 2121.8/1966.4/1482.9 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes (expected game-copy/ts3fixes.package.rel025e); noise: medium (other apps 11.7% of game CPUs, other CPUs 52.7% busy); others on game CPUs 11.7%; clock avg/min 4149/4122 MHz |
| ip-v025-final-smoke | 06:23 (14:23) | 6.9 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->151, stuckcheckasync_run 0->151, etspread_passes 0->1, stuckcheck_noplan 0->104, stuckcheck_rearmed 0->32, stuckcheck_reset 0->15<br>PASS: repro events fired | 0 | 2097.3/1852.8/1623.7 -> 2131/1872.8/1584.3 | - | timing: invalid (noise medium); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes (expected game-copy/ts3fixes.package.rel025e); noise: medium (other apps 11.8% of game CPUs); others on game CPUs 11.8%; clock avg/min 4161/4130 MHz |
