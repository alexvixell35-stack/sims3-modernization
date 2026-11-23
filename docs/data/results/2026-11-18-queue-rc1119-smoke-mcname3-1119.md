# Queue rc1119-smoke-mcname3-1119 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rc1119-smoke-mcname3-1119-2026-11-18.jsonl`.

## 2026-11-18T16:53 UTC, build fdee66cb7, jobs bp-rc1119-smoke-mcname3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rc1119-smoke-mcname3 | 16:53 (00:53) | 1.9 | **CRASH** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 16:55:44 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1774.2/1648.6/1451.6 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium); build: asi 94cc71ea1094 ts3native.asi.mcname3-1119 ts3native 0.36.289-mcname3 (log ok); ts3fixes tuning f0dd9bca27b3 ts3fixes.package.rc-1118n (expected game-copy/ts3fixes.package.rc-1118n) [cut: full row in the jsonl] |

Stopped: bp-rc1119-smoke-mcname3 failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts) | FAIL: game died at 16:55:44 (TS3W.exe process gone) | FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void | FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void | FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void | FAIL: UNKNOWN: [Route.
