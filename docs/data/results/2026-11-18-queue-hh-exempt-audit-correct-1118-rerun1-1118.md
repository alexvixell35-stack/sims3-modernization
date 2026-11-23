# Queue hh-exempt-audit-correct-1118-rerun1-1118 2026-11-18

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hh-exempt-audit-correct-1118-rerun1-1118-2026-11-18.jsonl`.

## 2026-11-18T14:13 UTC, build c03cb8f88, jobs bp-hh-exempt-audit

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hh-exempt-audit | 14:13 (22:13) | 3.5 | **CRASH** | FAIL: woohootywait-check: profile 10 min, first window after load<br>FAIL: game died at 14:16:35 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1744.9/1637.7/1812.2 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; woohootywait-check: job VOID: runInvalid: profiler cut short (91 of 600 s); noisy machine (others 51.2% of game CPUs); crash or abort (no "done; game ; noise medium) [cut: full row in the jsonl]<br>woohootywait-check: sim ms/s 191.6; p99 75 ms, p99.9 140, max 140, >=300 0, >=1000 0 (0.3 min) |

## 2026-11-18T14:20 UTC, build c03cb8f88, jobs bp-hh-exempt-audit

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-hh-exempt-audit | 14:20 (22:20) | 3.7 | **CRASH** | FAIL: woohootywait-check: profile 10 min, first window after load<br>FAIL: game died at 14:24:01 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1738.4/1629.1/1715.7 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; woohootywait-check: job VOID: runInvalid: profiler cut short (78 of 600 s); crash or abort (no "done; game alive"); noise high); build: asi fa87ce688f06 ts3native.asi.gcmark-p4-1118 ts3native 0.36.285-g5p4 (log ok) [cut: full row in the jsonl]<br>woohootywait-check: sim ms/s 186.8; p99 104 ms, p99.9 131, max 131, >=300 0, >=1000 0 (0.2 min) |

Stopped: same job crashed twice.
