# Queue float5-trap 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/float5-trap-2026-11-09.jsonl`.

## 2026-11-09T01:53 UTC, build eccf5815, jobs bp-float5-features-off, bp-float5-writetrap

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-features-off | 01:53 (09:53) | 27.8 | **FAIL** | travel: FAIL<br>summary: 1 cases, 0 passed, 1 failed (travel)<br>FAIL: events not fired: log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 1 | 1956.3/1666.8/1802 -> 2865.2/2576.6/851.9 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8); noise medium); build: asi 4b36985a7964 ts3native.asi.float5trap-1109 ts3native 0.36.227 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109 (expected ts3fixes.package.float5trap-1109); noise: medium ( |
| bp-float5-writetrap | 02:21 (10:21) | 14.9 | **CRASH** | FAIL: game died at 02:36:25 (TS3W.exe process gone)<br>run: FAIL<br>summary: 1 cases, 0 passed, 1 failed (run)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 1 | 2185.1/1890/1565.2 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; noise high; correctness-only tool ProbeSnap.CallSites); build: asi 4b36985a7964 ts3native.asi.float5trap-1109 ts3native 0.36.227 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109 (expected ts3fixes.package.float5trap-1109); |
