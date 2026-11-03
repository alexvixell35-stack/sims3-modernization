# Queue cellmemo-probe-1101 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/cellmemo-probe-1101-2026-11-01.jsonl`.

## 2026-11-01T08:36 UTC, build 4de49338, jobs ip-cellmemo-1101

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-cellmemo-1101 | 08:36 (16:36) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->363, stuckcheckasync_run 0->363, etspread_passes 1->1, stuckcheck_noplan 0->259, stuckcheck_notstuck 0->8, stuckcheck_rearmed 0->68, stuckcheck_reset 0->28<br>PASS: repro events fired | 0 | 2147.7/1872.5/1564.9 -> 2168/1887.9/1531.3 | - | timing: invalid (noise medium; correctness-only tool CellMemoProbe); build: asi df8ca55c8e5a rel025b-asi ts3native 0.36.152 (log ok); ts3fixes tuning c564d8781e18 rel025b-fixes (expected game-copy/ts3fixes.package.rel025b); noise: medium (other apps 13.2% of game CPUs); others on game CPUs 13.2%; clock avg/min 4227/4150 MHz; Isla Paradiso, 10 min, rel025b release defaults + RouteJobPhases + CellMe |
