# Queue diag-lifeevent-1114 2026-11-13

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/diag-lifeevent-1114-2026-11-13.jsonl`.

## 2026-11-13T21:56 UTC, build 810ea133, jobs ip-diag-lifeevent

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-diag-lifeevent | 21:56 (05:56) | 1.8 | **FAIL** | FAIL: profile 5 min, seed 12345 (rng-compare.ts)<br>FAIL: exit 3221226505 with no verdict line (child stopped from outside or died before its summary); last output: Assertion failed: !(handle->flags & UV_HANDLE_CLOSING), file src\win\async.c, line 94<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (n | 3221226505 | 2187.5/1890.7/1568.7 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; FAIL: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium; heavy tool samplers); build: asi 432ab1b2a191 ts3native.asi.rc1111g/ts3native.asi.rel0212 ts3native 0.36.247 (log ok); ts3fixes tuning 4108298b71f0 not in DB (expected game-copy/ts3fixes.package.diaglife-twopointjj); noise: medium (other |

Stopped: ip-diag-lifeevent failed (stopQueueOnFail): child profile-world exited (code 3221226505) without a result.
