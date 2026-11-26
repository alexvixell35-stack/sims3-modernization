# Queue rewind-pilot-1116-rerun2-1117 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rewind-pilot-1116-rerun2-1117-2026-11-17.jsonl`.

## 2026-11-17T00:16 UTC, build d25dcc4aa, jobs bp-rewind-pilot-a, bp-rewind-pilot-b, bp-rewind-pilot-c

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rewind-pilot-a | 00:16 (08:16) | 11 | **CRASH** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 00:26:16 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void<br>FAIL: UNKNOWN: [ | 1 | 1701.4/1623.1/1689.6 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise high; heavy tool samplers); build: asi 2b633b6935a7 ts3native.asi.rwud1116 ts3native 0.36.268-rwud1116 (log ok); ts3fixes tuning c64820e723fc ts3fixes.package.master-1116b/ts3fixes.package.master-1116c (expected game-copy/ts3fixes.package.master<br>sim ms/s 134.4; p99 116 ms, p99.9 183, max 243, >=300 0, >=1000 0 (0.8 min) |

rewind: 0 of 2 planned jobs started from a rewind, 0 refused, 0 failed (fresh load instead); ~0 min saved vs fresh starts (175 s each: mean of this run's 1 fresh starts)

Stopped: bp-rewind-pilot-a failed (stopQueueOnFail): FAIL: profile 5 min, seed 12345 (rng-compare.ts) | FAIL: game died at 00:26:16 (TS3W.exe process gone) | FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void | FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void | FAIL: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void | FAIL: UNKNOWN: [Route.
