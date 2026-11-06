# Queue v025-mintcall-ab 2026-11-04

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v025-mintcall-ab-2026-11-04.jsonl`.

## 2026-11-04T04:54 UTC, build f5738cf3, jobs bp-ph-mintcall-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-ph-mintcall-ab | 04:54 (12:54) | 20.2 | PASS | PASS: profile 18 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->7, owspread_types_run 0->140, stuckcheckasync_queued 0->423, stuckcheckasync_run 0->423, etspread_passes 0->1, stuckcheck_noplan 0->339, stuckcheck_notstuck 0->36, stuckcheck_rearmed 0->39, stuckcheck_reset 0->9 | 0 | 2201.5/1855/1532.8 -> 2290/2106.7/1313 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 21.6% of game CPUs); noise high); build: asi 95f85108e6e2 rel025d-asi ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes (expected game-copy/ts3fixes.package.rel025d-ph); noise: high (other apps 22.2% of game CPUs); others on game CPUs 22.2%; clock avg/min 4122/3772 MHz; In-load A/B of [MonoFast] Cal |
