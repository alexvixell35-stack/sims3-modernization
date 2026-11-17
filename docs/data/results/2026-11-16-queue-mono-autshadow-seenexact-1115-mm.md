# Queue mono-autshadow-seenexact-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-autshadow-seenexact-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T01:43 UTC, build e82388bb, jobs bp-seen-smoke, bp-seen-dry-crowd, bp-seen-act-crowd

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-seen-smoke | 01:43 (09:43) | 12.8 | **CRASH** | FAIL: profile 4 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 01:56:12 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>note: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void<br>note: UNKNOWN: [ | 1 | 1738.2/1617.6/1791.5 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3f |

Stopped: bp-seen-smoke failed (stopQueueOnFail): FAIL: profile 4 min, seed 12345 (rng-compare.ts).
