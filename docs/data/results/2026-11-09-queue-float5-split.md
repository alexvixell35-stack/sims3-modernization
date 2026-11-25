# Queue float5-split 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/float5-split-2026-11-09.jsonl`.

## 2026-11-09T03:02 UTC, build 4e9e2175, jobs bp-float5-split-a-1, bp-float5-split-b-1, bp-float5-split-a-2, bp-float5-split-b-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-split-a-1 | 03:02 (11:02) | 1.1 | **CRASH** | FAIL: events not fired: log /^uaf check on \(/ 0 lines (want >= 1); log /^uaf: save end \(save #\d+, world load #\d+\): held \d+ block/ 0 lines (want >= 8); log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: no crash report of this run written since 2026-11-09T03:02:21.176Z; FAIL: events not fired: log /^uaf check on \(; noise medium); build: asi b15141a82298 ts3native.asi.float5uaf-1109b ts3native 0.36.229; ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ts3fixes.package.float5uaf-1109/ts3fixes. |
| bp-float5-split-b-1 | 03:03 (11:03) | 1.1 | **CRASH** | FAIL: events not fired: log /^uaf check on \(/ 0 lines (want >= 1); log /^uaf: save end \(save #\d+, world load #\d+\): held \d+ block/ 0 lines (want >= 8); log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: no crash report of this run written since 2026-11-09T03:03:35.024Z; FAIL: events not fired: log /^uaf check on \(; noise high; correctness-only tool ProbeSnap.CallSites); build: asi b15141a82298 ts3native.asi.float5uaf-1109b ts3native 0.36.229; ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ |

Stopped: two jobs in a row crashed.

## 2026-11-09T03:05 UTC, build 942b8bdd, jobs bp-float5-split-a-1, bp-float5-split-b-1, bp-float5-split-a-2, bp-float5-split-b-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-split-a-1 | 03:05 (11:05) | 5.9 | **CRASH** | FAIL: game died at 03:11:42 (TS3W.exe process gone)<br>run: FAIL<br>summary: 1 cases, 0 passed, 1 failed (run)<br>FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void<br>FAIL: events not fired: log /^diag: mem #\d+ save-start ev=travel-save / 2 lines (want >= 8)<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 1 | 2015.3/1666.4/1741.1 -> undefined/undefined/undefined | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native has no asyncStarted: run void; noise high); build: asi b15141a82298 ts3native.asi.float5uaf-1109b ts3native 0.36.229 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ts3fixes.package.float5uaf-1109/ts3fixes.package.float5uaf-1109b (expected ts3fixe |

Stopped: same job crashed twice.

## 2026-11-09T03:12 UTC, build 5734f2aa, jobs bp-float5-split-b-1, bp-float5-split-b-2, bp-float5-split-a-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T03:12 UTC, build 5734f2aa, jobs bp-float5-split-b-1, bp-float5-split-b-2, bp-float5-split-a-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-float5-split-b-1 | 03:12 (11:12) | 13.8 | **CRASH** | FAIL: game died at 03:26:11 (TS3W.exe process gone)<br>run: FAIL<br>summary: 1 cases, 0 passed, 1 failed (run)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>FAIL: events not fired: log /^diag: mem #\d+ save-start ev=travel-save / 0 lines (want >= 8)<br>PASS: no forbidden log lines<br>FAIL: 1 new script errors (max 0, 2 known EA ignored): ScriptError_<pc>__11-09-20 | 1 | 2224.9/1878.7/1521 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; noise high; correctness-only tool ProbeSnap.CallSites); build: asi b15141a82298 ts3native.asi.float5uaf-1109b ts3native 0.36.229 (log ok); ts3fixes tuning 718c92c6f84d ts3fixes.package.wdasync-1109/ts3fixes.package.master1109c/ts3fixes.package.float5trap-1109/ts3fixes.package.float5uaf-1109/ts3fixes.pac |

Stopped: same job crashed twice.
