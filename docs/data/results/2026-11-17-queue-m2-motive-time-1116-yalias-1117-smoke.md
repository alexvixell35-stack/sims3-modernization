# Queue m2-motive-time-1116-yalias-1117-smoke 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/m2-motive-time-1116-yalias-1117-smoke-2026-11-17.jsonl`.

## 2026-11-17T23:06 UTC, build 6ea3f61b0, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 23:06 (07:06) | 11.9 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->233, stuckcheckasync_run 0->232, etspread_passes 0->1, stuckcheck_noplan 0->190, stuckcheck_notstuck 0->23, stuckcheck_rearmed 0->19, stuckcheck_reset 0->6<br>FAIL: events not fired: log /OK   mono jit \(docs/527\): Mode=1, .*Count=0 [cut: full row in the jsonl] | 0 | 1715.8/1625.9/1802.8 -> 1942.6/1888.1/1615 | - | timing: invalid (job FAIL: FAIL: events not fired: log /OK   mono jit \(docs/527\): Mode=1, .*Count=0, .*YRows=1$/ 0 lines (want >= 1); noise medium); build: asi 7b33ffaec4f5 ts3native.asi.yalias-1117 ts3native 0.36.274-yalias2 (log ok); ts3fixes tuning 26eb733447c2 ts3fixes.package.dalexlol-1117b (expected game-copy/ts3fixes.package.dalexlol-1117b); noise: medium (other apps 26.8% of game CPUs) [cut: full row in the jsonl]<br>sim ms/s 206.3; p99 89 ms, p99.9 143, max 238, >=300 0, >=1000 0 (6.3 min) |
