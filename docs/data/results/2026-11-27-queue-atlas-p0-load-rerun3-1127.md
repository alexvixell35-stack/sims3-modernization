# Queue atlas-p0-load-rerun3-1127 2026-11-27

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/atlas-p0-load-rerun3-1127-2026-11-27.jsonl`.

## 2026-11-27T00:05 UTC, build 1cbcefad3, jobs atlas-p0-4096

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-4096 | 00:05 (08:05) | 4.2 | **FAIL** | atlas-p0-load-thing-4096: PASS: steps done (no watch) (expected: in_world within 10 min (the step time is the load time); 2 min of camera poses over the origin quadrant and the three far corners with the game still alive; no script error)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) [cut: full row in the jsonl] | 0 | 1304.3/1210.5/1904.5 -> 1748.4/1652.4/1737.7 | - | timing: invalid (job FAIL: FAIL: atlas.log log rules: extraAsi log /^scene quadtree: root 4096 x \d+ x 4096 m, depth 7$/ 0 lines (want >= 1); noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b) [cut: full row in the jsonl] |

## 2026-11-27T00:12 UTC, build 1cbcefad3, jobs atlas-p0-4096

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-4096 | 00:12 (08:12) | 4 | **FAIL** | atlas-p0-load-thing-4096: PASS: steps done (no watch) (expected: in_world within 10 min (the step time is the load time); 2 min of camera poses over the origin quadrant and the three far corners with the game still alive; no script error)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) [cut: full row in the jsonl] | 0 | 1302.5/1211.3/1903.5 -> 1753.1/1656/1723.8 | - | timing: invalid (job FAIL: FAIL: atlas.log log rules: extraAsi log /^scene quadtree: root 4096 x \d+ x 4096 m, depth 7$/ 0 lines (want >= 1); noise high; correctness-only tool ProbeSnap.CallSites); build: asi a2ee78e8685b ts3native.asi.m1126b ts3native 0.36.306-m1126b (log ok); ts3fixes tuning 5f21313c91ab ts3fixes.package.m1126b (expected game-copy/ts3fixes.package.m1126b) [cut: full row in the jsonl] |
