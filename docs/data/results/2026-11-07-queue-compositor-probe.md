# Queue compositor-probe 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/compositor-probe-2026-11-07.jsonl`.

## 2026-11-07T09:05 UTC, build f7d3b426, jobs bp-compositor-probe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-07T09:29 UTC, build 30bc74c3, jobs bp-compositor-probe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-07T09:37 UTC, build fd191851, jobs bp-compositor-probe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-compositor-probe | 09:37 (17:37) | 17.3 | **FAIL** | FAIL: profile 24 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step time failed: no reply to 'time' (game paused, loading or blocked?)<br>PASS: repro events fired<br>PASS: no forbidden log lines | 1 | 2158.5/1845.2/1596.9 -> 2185.6/1869.9/1498.6 | - | timing: invalid (job VOID: runInvalid: profiler cut short (883 of 1440 s); noise medium); build: asi 0e378f0f0acf ts3native.asi.castrim3 ts3native 0.36.187 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3 (expected game-copy/asi-castrim3/ts3fixes.package); noise: medium (other apps 18.0% of game CPUs); others on game CPUs 18.0%; clock avg/min 4155/4098 M |

Stopped: bp-compositor-probe failed (stopQueueOnFail): FAIL: profile 24 min, seed 12345 (rng-compare.ts).

## 2026-11-07T09:57 UTC, build 4bc4d733, jobs bp-compositor-probe

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
