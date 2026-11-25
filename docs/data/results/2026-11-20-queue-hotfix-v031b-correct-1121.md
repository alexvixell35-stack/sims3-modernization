# Queue hotfix-v031b-correct-1121 2026-11-20

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/hotfix-v031b-correct-1121-2026-11-20.jsonl`.

## 2026-11-20T23:10 UTC, build 58f85dfe3, jobs bp-rc1119-smoke-hf031b, bp-speedlock-modeswitch-hf031b

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-rc1119-smoke-hf031b | 23:10 (07:10) | 6.5 | PASS | PASS: profile 3 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->78, stuckcheckasync_run 0->78, etspread_passes 0->1, stuckcheck_noplan 0->64, stuckcheck_notstuck 0->7, stuckcheck_rearmed 0->6, stuckcheck_reset 0->1<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1766.3/1689.6/1686.1 -> 2007.3/1787.3/1585.7 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 39.7% of game CPUs); noise high); build: asi ba8c7211b7b9 ts3native.asi.hf031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b (expected game-copy/ts3fixes.package.hf031b); noise: high (other apps 39.8% of game CPUs); others on game CPUs 39.8%; clock avg/min 3734/3119 MHz; [hotfix v0.3.1 [cut: full row in the jsonl]<br>sim ms/s 179.6; p99 85 ms, p99.9 185, max 239, >=300 0, >=1000 0 (1.3 min) |
| bp-speedlock-modeswitch-hf031b | 23:17 (07:17) | 3.2 | **CRASH** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 23:20:26 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>note: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1761.5/1676/1713.6 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise high); build: asi ba8c7211b7b9 ts3native.asi.hf031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b (expected game-copy/ts3fixes.package.hf031b); noise: high (other apps 57.1% of game CPUs) [cut: full row in the jsonl] |

Stopped: bp-speedlock-modeswitch-hf031b failed (stopQueueOnFail): FAIL: profile 3 min, seed 12345 (rng-compare.ts) | FAIL: game died at 23:20:26 (TS3W.exe process gone) | FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void | note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void | note: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void | note: UNKNOWN: [Route.

## 2026-11-20T23:23 UTC, build 58f85dfe3, jobs bp-speedlock-modeswitch-hf031b

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-speedlock-modeswitch-hf031b | 23:23 (07:23) | 2.2 | **CRASH** | FAIL: profile 3 min, seed 12345 (rng-compare.ts)<br>FAIL: game died at 23:26:02 (TS3W.exe process gone)<br>FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void<br>note: UNKNOWN: [RouteGate] Enforce on but rgEntries missing from /native (no answer): run void<br>note: UNKNOWN: [Discourage] DeferFootprint on but usfDeferred missing from /native (no answer): run void [cut: full row in the jsonl] | 1 | 1719.6/1660.7/1815.3 -> ? | - | timing: invalid (job VOID: FAIL: UNKNOWN: [RouteAsync] on but /native did not answer: run void; note: UNKNOWN: [RouteGate] Enforce on but rgEntries; noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b (expected game-copy/ts3fixes.package.hf031b); noise: medium (other apps 30.3% of game CPUs) [cut: full row in the jsonl] |

Stopped: same job crashed twice.

## 2026-11-20T23:33 UTC, build 58f85dfe3, jobs bp-speedlock-modeswitch-hf031b

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-20T23:38 UTC, build 58f85dfe3, jobs bp-speedlock-modeswitch-hf031b

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-speedlock-modeswitch-hf031b | 23:38 (07:38) | 5.2 | PASS | PASS: profile 3 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->37, stuckcheckasync_run 0->36, etspread_passes 0->1, stuckcheck_noplan 0->23, stuckcheck_notstuck 0->5, stuckcheck_rearmed 0->8, stuckcheck_reset 0->4<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1725.2/1665.2/1816.4 -> 2112/2050.6/1548.6 | - | timing: invalid (noise medium); build: asi ba8c7211b7b9 ts3native.asi.hf031 ts3native 0.36.295a (log ok); ts3fixes tuning 4c12a1eb4e6b ts3fixes.package.hf031b (expected game-copy/ts3fixes.package.hf031b); noise: medium (other apps 26.6% of game CPUs); others on game CPUs 26.6%; clock avg/min 4079/3928 MHz; [hotfix v0.3.1b [cut: full row in the jsonl]<br>sim ms/s 180.7; p99 66 ms, p99.9 113, max 130, >=300 0, >=1000 0 (1.9 min) |
