# Queue float5-bisect 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/float5-bisect-2026-11-09.jsonl`.

## 2026-11-09T02:37 UTC, build a9855238, jobs bp-float5-rasync-off-1, bp-float5-rasync-off-2, bp-float5-destroypark-off-1, bp-float5-destroypark-off-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-rasync-off-1 | 02:37 (10:37) | 1.4 | **FAIL** | FAIL: events not fired: log /^cas trim: outfit string check \(0x5E7BC2, session\) at save #\d+: calls [1-9]/ 0 lines (want >= 1); log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8)<br>FAIL: forbidden log lines: log /^FAIL\b/ 1 lines (want <= 0)<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job VOID: cycle.ts --save exit 1; self check: ts3native hook failed, run void (FAIL route gate closures: sites 0x1FFFFFF of 0x1FFF; noise high; correctness-only tool ProbeSnap.CallSites); build: asi cca13cb54bc6 ts3native.asi.master1109c ts3native 0.36.226; ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ts3f |

## 2026-11-09T02:39 UTC, build a155b473, jobs bp-float5-rasync-off-1, bp-float5-rasync-off-2, bp-float5-destroypark-off-1, bp-float5-destroypark-off-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-rasync-off-1 | 02:40 (10:40) | 14.3 | **CRASH** | FAIL: game died at 02:54:12 (TS3W.exe process gone)<br>run: FAIL<br>summary: 1 cases, 0 passed, 1 failed (run)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>FAIL: 2 new script errors (max 0, 1 known EA ignored): ScriptError_<pc>__11-09-2026__10-50-32__15F7A368.xml, ScriptError_<pc>__11-09-2026__10-51-58__E037AC3D.xml | 1 | 2159.3/1886.8/1591.4 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-09 10.54.09.txt; FAIL: game died at 02:54:12 (TS3W.exe process gone); FAIL: 2 new script er; noise high; correctness-only tool ProbeSnap.CallSites); build: asi cca13cb54bc6 ts3native.asi.master1109c ts3native 0.36.226 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap- |

## 2026-11-09T02:55 UTC, build 2cbdff31, jobs bp-float5-destroypark-off-1, bp-float5-destroypark-off-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
