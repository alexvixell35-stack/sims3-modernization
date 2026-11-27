# Queue atlas-p0-load-rerun2-1127 2026-11-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/atlas-p0-load-rerun2-1127-2026-11-26.jsonl`.

## 2026-11-26T23:14 UTC, build 8fdd8bd8f, jobs atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-4096 | 23:14 (07:14) | 1.5 | **CRASH** | FAIL: game died at 23:15:58 (TS3W.exe process gone)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0)<br>FAIL: atlas.log log rules: extraAsi log /^scene quadtree: root 4096 x \d+ x 4096 m, depth 7$/ 0 lines (want >= 1) | 1 | 1322.7/1228.9/1891.2 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-27 07.15.50.txt; FAIL: game died at 23:15:58 (TS3W.exe process gone); FAIL: atlas.log log r; noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b) [cut: full row in the jsonl] |
| atlas-p0-8192 | 23:16 (07:16) | 1.3 | **CRASH** | FAIL: game died at 23:17:20 (TS3W.exe process gone)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0)<br>FAIL: atlas.log log rules: extraAsi log /^scene quadtree: root 8192 x \d+ x 8192 m, depth 8$/ 0 lines (want >= 1) | 1 | 1320.5/1229.6/1894.4 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-27 07.17.20.txt; FAIL: game died at 23:17:20 (TS3W.exe process gone); FAIL: atlas.log log r; noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b) [cut: full row in the jsonl] |

Stopped: two jobs in a row crashed.

## 2026-11-26T23:19 UTC, build 8fdd8bd8f, jobs atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-4096 | 23:19 (07:19) | 1.3 | **CRASH** | FAIL: game died at 23:21:13 (TS3W.exe process gone)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0)<br>FAIL: atlas.log log rules: extraAsi log /^scene quadtree: root 4096 x \d+ x 4096 m, depth 7$/ 0 lines (want >= 1) | 1 | 1324.5/1228.9/1891.2 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-27 07.21.12.txt; FAIL: game died at 23:21:13 (TS3W.exe process gone); FAIL: atlas.log log r; noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b) [cut: full row in the jsonl] |

Stopped: same job crashed twice.
