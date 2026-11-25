# Queue float5-catch 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/float5-catch-2026-11-09.jsonl`.

## 2026-11-09T06:05 UTC, build dcadb65f, jobs bp-float5-catch-1, bp-float5-catch-2, bp-float5-catch-3, bp-float5-catch-4, bp-float5-catch-5, bp-float5-catch-6, bp-float5-catch-7, bp-float5-catch-8, bp-float5-catch-9, bp-float5-catch-10

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-catch-1 | 06:05 (14:05) | 9.7 | **CRASH** | FAIL: game died at 06:14:49 (TS3W.exe process gone)<br>run: FAIL<br>summary: 1 cases, 0 passed, 1 failed (run)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>FAIL: 1 new script errors (max 0, 2 known EA ignored): ScriptError_<pc>__11-09-2026__14-13-28__B9CEDC4A.xml | 1 | 2311.8/1942.2/1468.8 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; noise high; correctness-only tool ProbeSnap.CallSites); build: asi 0f43e99e737b ts3native.asi.float5catch-1109 ts3native 0.36.234 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ts3fixes.package.float5uaf-1109/ts3fixes.pa |

## 2026-11-09T06:16 UTC, build e203a58b, jobs bp-float5-catch-2, bp-float5-catch-3, bp-float5-catch-4, bp-float5-catch-5, bp-float5-catch-6, bp-float5-catch-7, bp-float5-catch-8, bp-float5-catch-9, bp-float5-catch-10

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
