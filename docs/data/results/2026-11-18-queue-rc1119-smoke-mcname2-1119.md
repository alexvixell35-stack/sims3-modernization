# Queue rc1119-smoke-mcname2-1119 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rc1119-smoke-mcname2-1119-2026-11-18.jsonl`.

## 2026-11-18T16:28 UTC, build 261561c44, jobs bp-rc1119-smoke-mcname2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rc1119-smoke-mcname2 | 16:28 (00:28) | 1.9 | **CRASH** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 16:30:37 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1762.4/1641.1/1457.9 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium); build: asi ead7de2c0e6f ts3native.asi.mcname2-1119 ts3native 0.36.289-mcname2 (log ok); ts3fixes tuning f0dd9bca27b3 ts3fixes.package.rc-1118n (expected game-copy/ts3fixes.package.rc-1118n) [cut: full row in the jsonl] |

Stopped: bp-rc1119-smoke-mcname2 failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts) | FAIL: game died at 16:30:37 (TS3W.exe process gone) | FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void | FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void | FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void | FAIL: UNKNOWN: [Route.
