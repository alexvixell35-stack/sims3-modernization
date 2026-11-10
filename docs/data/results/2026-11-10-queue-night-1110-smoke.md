# Queue night-1110-smoke 2026-11-10

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1110-smoke-2026-11-10.jsonl`.

## 2026-11-10T07:00 UTC, build 9a28acf9, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-10T07:02 UTC, build 9a28acf9, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 07:02 (15:02) | 2.4 | **FAIL** | PASS: repro events fired | - | ? -> ? | - | noisy: does not count, rerun as smoke-rerun; timing: invalid (job FAIL: exit code ?; noise high; A/B iterations unknown); build: asi baaa4cfc174c ts3native.asi.rc1111 ts3native 0.36.239; ts3fixes tuning 4b4eba259fc1 ts3fixes.package.rc1111 (expected game-copy/ts3fixes.package.rc1111); noise: high (other apps 28.3% of game CPUs); others on game CPUs 28.3%; clock avg/min 4136/4095 MHz; hang capture: |
| smoke-rerun | 07:04 (15:04) | 12 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: watchdogasync_ticks 0->9765, watchdog_route_fail_sims 0->82, watchdog_route_fails 0->17, watchdogasync_sent 0->277, watchdog_idle_stationary 0->7, watchdogasync_refused 0->1, watchdog_second 0->2, watchdog_home_house 0->1, watchdogasync_started 0->277, watchdog_idle_verdicts 0->740, watchdog_shortcuts 0->1, watchdogasync_stale | 0 | 2241.3/1888.3/1516.4 -> 2195.8/1916.6/1508.4 | - | rerun of smoke: noisy again, neither counts; timing: invalid (job VOID: runInvalid: noisy machine (others 34.9% of game CPUs); noise high); build: asi baaa4cfc174c ts3native.asi.rc1111 ts3native 0.36.239 (log ok); ts3fixes tuning 4b4eba259fc1 ts3fixes.package.rc1111 (expected game-copy/ts3fixes.package.rc1111); noise: high (other apps 33.3% of game CPUs); others on game CPUs 33.3%; clock avg/min 4 |
