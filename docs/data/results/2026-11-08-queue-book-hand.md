# Queue book-hand 2026-11-08

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/book-hand-2026-11-08.jsonl`.

## 2026-11-08T05:24 UTC, build 0484057e, jobs bp-book-hand-repro-off, bp-book-hand-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-book-hand-repro-off | 05:24 (13:24) | 9.8 | PASS | PASS: profile 8 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->196, stuckcheckasync_run 0->196, etspread_passes 0->1, stuckcheck_noplan 0->177, stuckcheck_notstuck 0->13, stuckcheck_rearmed 0->9, stuckcheck_reset 0->1 | 0 | 2134.6/1842.7/1604.1 -> 2278.2/2090.2/1343.9 | - | timing: invalid (noise medium); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning ea12bdd98af0 ts3fixes.package.bookhand1108 (expected game-copy/ts3fixes.package.bookhand1108); noise: medium (other apps 25.6% of game CPUs); others on game CPUs 25.6%; clock avg/min 4185/4060 MHz; Correctness (8 min), off arm: repro book-hand-check-off. Records whethe |
| bp-book-hand-repro | 05:33 (13:33) | 2.1 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: bookhand read: no check armed on Tiara Angelista (begin first)<br>ts3fixes activity: etspread_passes 0->1<br>FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-08-2026__13-35-59__BA7A5CBD.xml | 1 | 2148/1845.7/1594.3 -> 2154.5/1877.8/1571.5 | - | timing: invalid (job VOID: runInvalid: profiler cut short (8 of 480 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise medium); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning ea12bdd98af0 ts3fixes.package.bookhand1108 (expected game-copy/ts3fixes.package.bookhand1108); noise: medium (other apps 40.6% of game CP |

## 2026-11-08T05:41 UTC, build ae6b81da, jobs bp-book-hand-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-book-hand-repro | 05:41 (13:41) | 1.9 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: bookhand read: no check armed on Tiara Angelista (begin first); armed: Tiara Angelista, destroyed True, same id False<br>ts3fixes activity: etspread_passes 0->1<br>FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-08-2026__13-43-15__CA88433B.xml | 1 | 2167.6/1846.7/1588.9 -> 2173.8/1892.3/1549.4 | - | timing: invalid (job VOID: runInvalid: profiler cut short (8 of 480 s); noisy machine (machine load: no samples); noise medium); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning ea12bdd98af0 ts3fixes.package.bookhand1108/ts3fixes.package.castrimrel1108/ts3fixes.package.bookhand1108b (expected game-copy/ts3fixes.package.bookhand1108b); noise: medium |

## 2026-11-08T05:44 UTC, build b4276229, jobs bp-book-hand-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-book-hand-repro | 05:44 (13:44) | 6.5 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call: reflect_call until [{"path":"ready","equals":true}] not met in 180 s: {"reading":false,"stops":3,"$type":"Ts3Fixes.Fixes+BhState","seen":0,"stuck":false,"$str":"Ts3Fixes.Fixes+BhState","fixedN":0,"ready":false,"carry":false}<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued  | 1 | 2162.4/1862.7/1584.8 -> 2251.2/2062.7/1379.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (286 of 480 s); noise medium); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning ea12bdd98af0 ts3fixes.package.bookhand1108/ts3fixes.package.castrimrel1108/ts3fixes.package.bookhand1108b/ts3fixes.package.bookhand1108c (expected game-copy/ts3fixes.package.bookhand1108c); noise: medium (other a |

## 2026-11-08T05:51 UTC, build b4276229, jobs bp-book-hand-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-book-hand-repro | 05:51 (13:51) | 1.8 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: bookhand read: no check armed on Tiara Angelista (begin first); armed: Tiara Angelista, destroyed True, same id False<br>ts3fixes activity: etspread_passes 0->1<br>FAIL: 1 new script errors (max 0): ScriptError_<pc>__11-08-2026__13-53-36__ECA160A2.xml | 1 | 2166.6/1862.8/1576.9 -> 2165.9/1897/1549.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (8 of 480 s); noisy machine (machine load: no samples); noise medium); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning ea12bdd98af0 ts3fixes.package.bookhand1108/ts3fixes.package.castrimrel1108/ts3fixes.package.bookhand1108b/ts3fixes.package.bookhand1108c (expected game-copy/ts3fixes.packag |

## 2026-11-08T06:14 UTC, build 52b0bba3, jobs bp-book-hand-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-book-hand-repro | 06:15 (14:15) | 8 | **FAIL** | FAIL: profile 8 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call: reflect_call until [{"path":"ready","equals":true}] not met in 180 s: {"reading":false,"stops":5,"$type":"Ts3Fixes.Fixes+BhState","seen":0,"stuck":false,"$str":"Ts3Fixes.Fixes+BhState","fixedN":0,"ready":false,"carry":false}<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued  | 1 | 2158.3/1846.7/1593.4 -> 2302.7/2091.7/1350.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (359 of 480 s); noise high); build: asi 638805d344d4 ts3native.asi.castrimdiag1108 ts3native 0.36.194 (log ok); ts3fixes tuning 24e0fad3a692 ts3fixes.package.bookhand1108d (expected game-copy/ts3fixes.package.bookhand1108d); noise: high (other apps 32.5% of game CPUs); others on game CPUs 32.5%; clock avg/min 4128/3841 MHz; Correctness (8 m |
