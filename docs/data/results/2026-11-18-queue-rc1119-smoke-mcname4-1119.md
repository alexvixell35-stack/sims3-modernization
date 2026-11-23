# Queue rc1119-smoke-mcname4-1119 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rc1119-smoke-mcname4-1119-2026-11-18.jsonl`.

## 2026-11-18T17:00 UTC, build 8c2a98848, jobs bp-rc1119-smoke-mcname4

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rc1119-smoke-mcname4 | 17:00 (01:00) | 2 | **CRASH** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 17:02:42 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1754.9/1633.5/1445.9 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise high); build: asi 5fc05eba3446 ts3native.asi.mcname4-1119 ts3native 0.36.289-mcname4 (log ok); ts3fixes tuning f0dd9bca27b3 ts3fixes.package.rc-1118n (expected game-copy/ts3fixes.package.rc-1118n); noise: high (other apps 29.5% of game CPUs) [cut: full row in the jsonl] |

Stopped: bp-rc1119-smoke-mcname4 failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts) | FAIL: game died at 17:02:42 (TS3W.exe process gone) | FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void | FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void | FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void | FAIL: UNKNOWN: [Route.
