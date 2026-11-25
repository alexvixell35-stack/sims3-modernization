# Queue master-smoke-1115 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master-smoke-1115-2026-11-15.jsonl`.

## 2026-11-15T08:45 UTC, build 70529098, jobs bp-master-merge-1115-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-master-merge-1115-smoke | 08:45 (16:45) | 7.1 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->128, stuckcheckasync_run 0->128, etspread_passes 0->1, stuckcheck_noplan 0->106, stuckcheck_notstuck 0->15, stuckcheck_rearmed 0->11, stuckcheck_reset 0->3<br>PASS: repro events fired | 0 | 2240.7/1899.4/1513.8 -> 2315.2/2147.8/1303.3 | - | timing: invalid (noise medium); build: asi b75b706ac7c9 ts3native.asi.master-merge-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package.master-dalexlol-405d-1115/ts3fixes.package.master-merge-1115 (expected game-copy/ts3fixes.package.master-merge-1115); noise: medium (other apps 15.4% of game CPUs); others on game CPUs 15 |
