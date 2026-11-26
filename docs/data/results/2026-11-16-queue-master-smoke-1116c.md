# Queue master-smoke-1116c 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master-smoke-1116c-2026-11-16.jsonl`.

## 2026-11-16T14:12 UTC, build 68447d379, jobs bp-master-1116c-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-master-1116c-smoke | 14:12 (22:12) | 9.3 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->78, stuckcheckasync_run 0->78, etspread_passes 0->1, stuckcheck_noplan 0->66, stuckcheck_notstuck 0->5, stuckcheck_rearmed 0->7<br>PASS: repro events fired | 0 | 1718.7/1629.7/1715 -> 2006.8/1762.7/1595.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 11.7% >= 10%); noise high); build: asi 17a2fcf3b5a3 ts3native.asi.master-1116c ts3native 0.36.267-mc1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master-1116c); noise: high (other apps 81.9% of game CPUs, NOT comparable (load or clock)); oth<br>sim ms/s 203.7; p99 124 ms, p99.9 211, max 293, >=300 0, >=1000 0 (2.8 min) |
