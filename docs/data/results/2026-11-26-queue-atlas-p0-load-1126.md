# Queue atlas-p0-load-1126 2026-11-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/atlas-p0-load-1126-2026-11-26.jsonl`.

## 2026-11-26T08:07 UTC, build 03cf5b56e, jobs atlas-p0-2048, atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-2048 | 08:07 (16:07) | 11.7 | **FAIL** | FAIL: load_world until [{"path":"in_world","equals":true}] not met in 600 s: {"in_world":false,"load_file":"AtlasP0_2048.world","households":0,"save_in_progress":false,"save_name":null,"active_household":null,"sub_state":"unknown: A null value was found where an object instance was required.","modal":false,"shutting_down":false}<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) [cut: full row in the jsonl] | 1 | 1280.6/1220.7/1888.5 -> 1299.4/1239.4/1875.4 | - | timing: invalid (job FAIL: FAIL: load_world until [{"path":"in_world","equals":true}] not met in 600 s: {"in_world":false,"load_file":"AtlasP0_2048; noise high; correctness-only tool ProbeSnap.CallSites); build: asi 6c0a0087d744 ts3native.asi.m1124d ts3native 0.36.305-m1124d (log ok); ts3fixes tuning d12eca902d23 ts3fixes.package.m1124f (expected game-copy/ts3fixes.package.m1124f) [cut: full row in the jsonl] |

## 2026-11-26T08:50 UTC, build 03cf5b56e, jobs atlas-p0-2048, atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-2048 | 08:50 (16:50) | 11.4 | **FAIL** | FAIL: load_world until [{"path":"in_world","equals":true}] not met in 600 s: {"in_world":false,"load_file":"AtlasP0thin_2048.world","households":45,"save_in_progress":false,"save_name":null,"active_household":null,"sub_state":"unknown: A null value was found where an object instance was required.","modal":false,"shutting_down":false}<br>PASS: repro events fired [cut: full row in the jsonl] | 1 | 1382.1/1236.4/1891.2 -> 1499.6/1456/1822.9 | - | timing: invalid (job FAIL: FAIL: load_world until [{"path":"in_world","equals":true}] not met in 600 s: {"in_world":false,"load_file":"AtlasP0thin_; noise high; correctness-only tool ProbeSnap.CallSites); build: asi 6c0a0087d744 ts3native.asi.m1124d ts3native 0.36.305-m1124d (log ok); ts3fixes tuning d12eca902d23 ts3fixes.package.m1124f (expected game-copy/ts3fixes.package.m1124f) [cut: full row in the jsonl] |

## 2026-11-26T09:02 UTC, build 03cf5b56e, jobs atlas-p0-2048, atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-26T09:09 UTC, build 03cf5b56e, jobs atlas-p0-2048, atlas-p0-4096, atlas-p0-8192

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| atlas-p0-2048 | 09:09 (17:09) | 4.7 | PASS | atlas-p0-load-2048: PASS: steps done (no watch) (expected: in_world within 10 min (the step time is the load time); 2 min of camera poses over the origin quadrant and the three far corners with the game still alive; no script error)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored)<br>PASS: atlas.log lines present (atlas 0.1.0-p0 built Nov 26 2026 15:59:35 [cut: full row in the jsonl] | 0 | 1375.9/1226.5/1540.2 -> 1646.5/1580/1441.8 | - | timing: invalid (noise high; correctness-only tool ProbeSnap.CallSites); build: asi 6c0a0087d744 ts3native.asi.m1124d ts3native 0.36.305-m1124d (log ok); ts3fixes tuning d12eca902d23 ts3fixes.package.m1124f (expected game-copy/ts3fixes.package.m1124f); noise: high (other apps 68.4% of game CPUs, NOT comparable (load or clock)); others on game CPUs 68.4%; clock avg/min 3497/3217 MHz [cut: full row in the jsonl] |
| atlas-p0-4096 | 09:14 (17:14) | 1.5 | **CRASH** | FAIL: game died at 09:15:33 (TS3W.exe process gone)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0)<br>PASS: atlas.log lines present (atlas 0.1.0-p0 built Nov 26 2026 15:59:35, TS3W.exe link stamp 0x52DEC247 / physics root: off (PhysicsRootM=0), 0x00FEFBCC holds 4096) | 1 | 1375.7/1226.8/1892.2 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-26 17.15.26.txt; FAIL: game died at 09:15:33 (TS3W.exe process gone); noise high; correctness-only tool ProbeSnap.CallSites); build: asi 6c0a0087d744 ts3native.asi.m1124d ts3native 0.36.305-m1124d (log ok); ts3fixes tuning d12eca902d23 ts3fixes.package.m1124f (expected game-copy/ts3fixes.package.m1124f) [cut: full row in the jsonl] |
| atlas-p0-8192 | 09:15 (17:15) | 1.5 | **CRASH** | FAIL: game died at 09:17:04 (TS3W.exe process gone)<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0)<br>PASS: atlas.log lines present (atlas 0.1.0-p0 built Nov 26 2026 15:59:35, TS3W.exe link stamp 0x52DEC247 / physics root: 4096 -> 8192 m at 0x00FEFBCC, read back 0x46000000) | 1 | 1456.6/1306.6/1545.5 -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-26 17.17.01.txt; FAIL: game died at 09:17:04 (TS3W.exe process gone); noise high; correctness-only tool ProbeSnap.CallSites); build: asi 6c0a0087d744 ts3native.asi.m1124d ts3native 0.36.305-m1124d (log ok); ts3fixes tuning d12eca902d23 ts3fixes.package.m1124f (expected game-copy/ts3fixes.package.m1124f) [cut: full row in the jsonl] |

Stopped: two jobs in a row crashed.
