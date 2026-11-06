# Queue v027-memdiag 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v027-memdiag-2026-11-06.jsonl`.

## 2026-11-06T01:05 UTC, build 2f9253a4, jobs bp-v027-memdiag

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-v027-memdiag | 01:05 (09:05) | 11.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->244, stuckcheckasync_run 0->244, etspread_passes 0->1, stuckcheck_noplan 0->197, stuckcheck_notstuck 0->23, stuckcheck_rearmed 0->28, stuckcheck_reset 0->6 | 0 | 2222.6/1850.6/1552.4 -> 2257.5/2095.7/1316.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 10.4% of game CPUs); noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 11.6% of game CPUs); others on game CPUs 11.6%; clock avg/min 4196/4159 MHz; BP v |
