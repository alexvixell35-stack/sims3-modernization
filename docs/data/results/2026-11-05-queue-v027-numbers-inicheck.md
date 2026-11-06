# Queue v027-numbers-inicheck 2026-11-05

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v027-numbers-inicheck-2026-11-05.jsonl`.

## 2026-11-05T22:28 UTC, build 51eb1159, jobs bp-v027-inicheck

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v027-inicheck | 22:28 (06:28) | 4.7 | PASS | PASS: profile 3 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->61, stuckcheckasync_run 0->61, etspread_passes 0->1, stuckcheck_noplan 0->56, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->4 | 0 | 2148/1826.2/1612.9 -> 2220.1/2022.9/1423.5 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 13.6% of game CPUs); others on game CPUs 13.6%; clock avg/min 4218/4137 MHz; 3 min deploy check: release ini loaded (no [RouteToObjRO]); not a |
