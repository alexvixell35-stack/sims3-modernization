# Queue v027-numbers-clean-smoke 2026-11-05

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v027-numbers-clean-smoke-2026-11-05.jsonl`.

## 2026-11-05T19:44 UTC, build 6671fca4, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 19:44 (03:44) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->288, stuckcheckasync_run 0->288, etspread_passes 0->1, stuckcheck_noplan 0->238, stuckcheck_notstuck 0->30, stuckcheck_rearmed 0->23, stuckcheck_reset 0->2 | 0 | 2148.8/1826.8/1619.5 -> 2253.1/2074.4/1328.5 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 8.3% of game CPUs); others on game CPUs 8.3%; clock avg/min 4247/4219 MHz; declared smoke: v0.2.7 BP, 10 min; the auto smoke copies the first  |
