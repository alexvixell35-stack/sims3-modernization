# Queue hh-exempt-audit-correct-1118-rerun2-1118 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hh-exempt-audit-correct-1118-rerun2-1118-2026-11-18.jsonl`.

## 2026-11-18T14:37 UTC, build a0054a3ce, jobs bp-hh-exempt-audit

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hh-exempt-audit | 14:37 (22:37) | 3.6 | **CRASH** | FAIL: woohootywait-check: profile 10 min, first window after load<br>FAIL: game died at 14:41:10 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1744.5/1632.5/1792.1 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; woohootywait-check: job VOID: runInvalid: profiler cut short (64 of 600 s); crash or abort (no "done; game alive"); noise high); build: asi 7b3fd0598b79 ts3native.asi.nohdr1-1118 ts3native 0.36.286-nohdr1 (log ok) [cut: full row in the jsonl]<br>woohootywait-check: sim ms/s 173.7; p99 <50 ms, p99.9 <50, max 0, >=300 0, >=1000 0 (0.0 min) |
