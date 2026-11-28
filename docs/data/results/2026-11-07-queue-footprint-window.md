# Queue footprint-window 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/footprint-window-2026-11-07.jsonl`.

## 2026-11-07T12:42 UTC, build 500ca5d3, jobs footprint-window

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| footprint-window | 12:42 (20:42) | 5.6 | PASS | PASS: profile stopped at 20 events after 2.4 min (off 10, on 10; 20 firings, 10 cycles; rule 10 events per arm), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->159, stuckcheckasync_run 0->159, etspread_passes 0->1, stuckcheck_noplan 0->149, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->6<br>PASS: repro events | 0 | 2131.1/1839.3/1600.8 -> 2163.4/1879.5/1535 | - | timing: invalid (noise medium); build: asi 79c017303b54 v0210d ts3native 0.36.168e-rftest1 (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected game-copy/asi-v029/ts3fixes.package); noise: medium (other apps 17.0% of game CPUs); others on game CPUs 17.0%; clock avg/min 4156/4116 M |
