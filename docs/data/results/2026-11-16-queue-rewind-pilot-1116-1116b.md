# Queue rewind-pilot-1116-1116b 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rewind-pilot-1116-1116b-2026-11-16.jsonl`.

## 2026-11-16T12:41 UTC, build 7147daea2, jobs bp-rewind-pilot-a, bp-rewind-pilot-b, bp-rewind-pilot-c

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rewind-pilot-a | 12:41 (20:41) | 7.2 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->109, stuckcheckasync_run 0->109, etspread_passes 0->1, stuckcheck_noplan 0->83, stuckcheck_notstuck 0->14, stuckcheck_rearmed 0->16, stuckcheck_reset 0->3<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1787.9/1622.4/1719 -> 1984.6/1702.3/1547.5 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 17.8% >= 10%); noise high; heavy tool samplers); build: asi 011f124f4797 ts3native.asi.master-1116b ts3native 0.36.266-mb1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b (expected game-copy/ts3fixes.package.master-1116b); start: fresh (100 s); snapshot taken (5854 ms); noise: high (other apps 38.3% of game CPU<br>sim ms/s 201.4; p99 87 ms, p99.9 171, max 197, >=300 0, >=1000 0 (0.6 min) |
