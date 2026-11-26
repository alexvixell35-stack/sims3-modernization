# Queue gc-idle-gate-correct-1117-master-1117b 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/gc-idle-gate-correct-1117-master-1117b-2026-11-17.jsonl`.

## 2026-11-17T01:20 UTC, build 848b002d4, jobs bp-gcgate-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-gcgate-smoke | 01:20 (09:20) | 8.3 | **FAIL** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: early gate: the gate never skipped (threshold or state fields wrong) (gcgSkipped=0) [want >0 from 240 s]; child stopped<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1745.6/1631/1675.1 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise high); build: asi dd4e4c13902e ts3native.asi.master-1117b ts3native 0.36.269-mb1117 (log ok); ts3fixes tuning 8ae32451d8bc ts3fixes.package.master-1117b (expected game-copy/ts3fixes.package.master-1117b) [cut: full row in the jsonl]<br>sim ms/s 122.9; p99 70 ms, p99.9 137, max 283, >=300 0, >=1000 0 (2.1 min) |

Stopped: bp-gcgate-smoke failed (stopQueueOnFail): early gate: the gate never skipped (threshold or state fields wrong) (gcgSkipped=0) [want >0 from 240 s].
