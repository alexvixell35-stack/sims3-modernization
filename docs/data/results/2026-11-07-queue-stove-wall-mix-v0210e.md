# Queue stove-wall-mix-v0210e 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-wall-mix-v0210e-2026-11-07.jsonl`.

## 2026-11-07T14:24 UTC, build 947506b1, jobs stove-wall-mix-v0210e

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-mix-v0210e | 14:24 (22:24) | 17.8 | PASS | PASS: profile stopped at 1 events after 14.3 min (off 0, on 1; 1 firings, 1 cycles; rule 1 events), cap 150 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->21, owspread_types_run 0->420, stuckcheckasync_queued 0->1915, stuckcheckasync_run 0->1777, etspread_passes 0->1, stuckcheck_noplan 0->1736, stuckcheck_notstuck 0->75, stuckcheck_rearmed 0->50, stuckcheck_reset 0->2<br> | 0 | 2129.5/1842.5/1598.1 -> 2410.9/2266.6/1176.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 33.6% of game CPUs); noise high; A/B 0 off / 1 on iterations, < 4 per arm (repro firings)); build: asi 32fe95a0a467 v0210e/ts3native.asi.rel0210 ts3native 0.36.168e (log ok); ts3fixes tuning 5716accd026a imp1105-fixes/imp1105b-fixes/ts3fixes.package.routefail1/ts3fixes.package.routefail2/ts3fixes.package.v029b/ts3fixes.0.2.9 (expected ga |
