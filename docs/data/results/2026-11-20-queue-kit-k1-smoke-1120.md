# Queue kit-k1-smoke-1120 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kit-k1-smoke-1120-2026-11-20.jsonl`.

## 2026-11-20T16:11 UTC, build 58f85dfe3, jobs bp-kit-k1-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-kit-k1-smoke | 16:11 (00:11) | 12.2 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->222, stuckcheckasync_run 0->222, etspread_passes 0->1, stuckcheck_noplan 0->178, stuckcheck_notstuck 0->20, stuckcheck_rearmed 0->24, stuckcheck_reset 0->4<br>FAIL: events not fired: log /^jit: refusals by reason \(.* [cut: full row in the jsonl] | 0 | 1743.5/1681/1808.6 -> 1986.2/1934.6/1609.1 | - | timing: invalid (job VOID: runInvalid: noisy machine (clock fell to 120% from 166% (2 samples under 133%)); noise high); build: asi 6d1e44d19a25 not in DB ts3native 0.36.298-mkit1 (log ok); ts3fixes tuning 96f496b7d197 ts3fixes.package.m1120n/ts3fixes.package.m1120t/ts3fixes.package.m1120u (expected game-copy/ts3fixes.package.m1120u); noise: high (NOT comparable (load or clock)) [cut: full row in the jsonl]<br>sim ms/s 167.7; p99 99 ms, p99.9 159, max 286, >=300 0, >=1000 0 (6.1 min) |
