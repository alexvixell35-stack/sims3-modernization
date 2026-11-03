# Queue save-cleanup-aggressive 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/save-cleanup-aggressive-2026-10-30.jsonl`.

## 2026-10-30T07:25 UTC, build f6ddcc27, jobs bp-multigen-build, bp-multigen-aggr, bp-multigen-rotation, bp-multigen-ab, ip-multigen-aggr

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-multigen-build | 07:25 (15:25) | 6.2 | **FAIL** | FAIL: profile 24 min, seed 12345 (rng-compare.ts)<br>FAIL: sim clock stuck at day 16 14:08 for ~60 s (speed Pause, sub_state LiveModeState) (fail-fast guard)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->64, stuckcheckasync_run 0->64, etspread_passes 0->1, stuckcheck_noplan 0->52, stuckcheck_notstuck 0->5, stuckcheck_rearmed 0->8, stuckcheck_reset 0 | 1 | 2198.6/1829.5/1548.9 -> 2197.6/1900.2/1492.9 | - | timing: invalid (job VOID: runInvalid: profiler cut short (266 of 1440 s); crash or abort (no "done; game alive"); noise medium); build: asi 70ab1d502451 not in DB ts3native 0.36.126 (log ok); ts3fixes tuning 94c60769ef8f not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.7% of game CPUs); others on game CPUs 17.7%; clock avg/min 4188/4109 MHz; Builds MultiGen-Test-BP (repro |
