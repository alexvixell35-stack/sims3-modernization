# Queue release-verify-rc1119-rest-m1124d2-ea 2026-11-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/release-verify-rc1119-rest-m1124d2-ea-2026-11-25.jsonl`.

Machine: ea-laptop

## 2026-11-25T04:00 UTC, build 8df446a2, jobs bp-outfitwait-dead-rc1119

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| bp-outfitwait-dead-rc1119 | 04:01 (12:01) | 10.2 | PASS | PASS: profile 8 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->141, stuckcheckasync_run 0->141, stuckcheck_noplan 0->111, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->15, stuckcheck_reset 0->5; ts3fixes errortrapspread: stood down (errortrapspread: ErrorTrap not loaded, off)<br>PASS: repro events fired [cut: full row in the jsonl] | 0 | 2048.8/1706.5/1788.7 -> 2121.8/1809.7/1500.5 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi ef19c1b510a1 ts3native-ea169.asi.eam1124d2 ts3native 0.36.305-eam1124d2 (log ok); ts3fixes tuning b7018ac0bf65 ts3fixes.package.m1124b/ts3fixes.package.eam1124b/ts3fixes.package.m1124c/ts3fixes.package.m1124d/ts3fixes.package.eam1124d2 (expected game-copy/eam1124d2/ts3fixes.package) [cut: full row in the jsonl] | ea-laptop |
