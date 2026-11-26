# Queue s3pp-compat-1124-hostpatch-m1124d2-ea 2026-11-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/s3pp-compat-1124-hostpatch-m1124d2-ea-2026-11-24.jsonl`.

Machine: ea-laptop

## 2026-11-24T14:44 UTC, build d5d7fe9c, jobs bp-s3ppgc-smoke-hostpatch

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| bp-s3ppgc-smoke-hostpatch | 14:44 (22:44) | 6.8 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->75, stuckcheckasync_run 0->75, stuckcheck_noplan 0->63, stuckcheck_notstuck 0->7, stuckcheck_rearmed 0->4, stuckcheck_reset 0->1; ts3fixes errortrapspread: stood down (errortrapspread: ErrorTrap not loaded, off)<br>PASS: repro events fired [cut: full row in the jsonl] | 0 | 2042.5/1717.4/1785.6 -> 2104.6/1765.5/1732.8 | - | timing: invalid (noise medium; correctness-only tool ProbeSnap.CallSites); build: asi ef19c1b510a1 ts3native-ea169.asi.eam1124d2 ts3native 0.36.305-eam1124d2 (log ok); ts3fixes tuning b7018ac0bf65 ts3fixes.package.m1124b/ts3fixes.package.eam1124b/ts3fixes.package.m1124c/ts3fixes.package.m1124d/ts3fixes.package.eam1124d2 (expected game-copy/eam1124d2/ts3fixes.package) [cut: full row in the jsonl] | ea-laptop |
