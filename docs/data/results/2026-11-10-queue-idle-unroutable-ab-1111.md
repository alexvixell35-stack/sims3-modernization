# Queue idle-unroutable-ab-1111 2026-11-10

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/idle-unroutable-ab-1111-2026-11-10.jsonl`.

## 2026-11-10T08:11 UTC, build 15f06c19, jobs bp-wdasync-stuck-v3-noapex

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-wdasync-stuck-v3-noapex | 08:11 (16:11) | 7.8 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_where failed: sim_where: Wogan Hemlock is 560.200012207031 m from the strand spot (max 1), interaction none<br>ts3fixes activity: watchdogasync_ticks 0->8041, watchdog_route_fail_sims 0->79, watchdog_idle_hidden_room 0->1, watchdogasync_sent 0->317, watchdog_idle_stationary 0->6, watchdogasync_refused 0->336, watchdog_home_h | 1 | 2238.5/1887.2/1521.1 -> 2292.9/2141/1282.1 | - | rerun of bp-wdasync-stuck-repro-1111: noisy again, neither counts; timing: invalid (job VOID: runInvalid: profiler cut short (337 of 600 s); noisy machine (others 29.8% of game CPUs); crash or abort (no "done; game; noise high); build: asi baaa4cfc174c ts3native.asi.rc1111 ts3native 0.36.239 (log ok); ts3fixes tuning 4b4eba259fc1 ts3fixes.package.rc1111 (expected game-copy/ts3fixes.package.rc1111) |

## 2026-11-10T08:21 UTC, build 15f06c19, jobs bp-apex300-wdasync-stuck-v3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-apex300-wdasync-stuck-v3 | 08:21 (16:21) | 7.6 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_where failed: sim_where: Wogan Hemlock is 548.200012207031 m from the strand spot (max 1), interaction Meet Up<br>ts3fixes activity: watchdogasync_ticks 0->9685, watchdog_route_fail_sims 0->90, watchdogasync_sent 0->307, watchdog_idle_stationary 0->5, watchdogasync_refused 0->453, wdLockSkip 0->453, watchdogasync_started 0-> | 1 | 2437.1/2162.9/1181.4 -> 2655.2/2493.8/877.9 | - | timing: invalid (job VOID: runInvalid: profiler cut short (337 of 600 s); noisy machine (others 30.9% of game CPUs); crash or abort (no "done; game; noise high); build: asi baaa4cfc174c ts3native.asi.rc1111 ts3native 0.36.239 (log ok); ts3fixes tuning 4b4eba259fc1 ts3fixes.package.rc1111 (expected game-copy/ts3fixes.package.rc1111); noise: high (other apps 30.7% of game CPUs); others on game CPUs  |

## 2026-11-10T08:49 UTC, build 69a4109a, jobs bp-wdasync-stuck-v3-noapex

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-wdasync-stuck-v3-noapex | 08:49 (16:49) | 12.1 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: watchdogasync_ticks 0->14804, watchdog_route_fail_sims 0->138, watchdog_route_fails 0->1, watchdogasync_sent 0->510, watchdog_idle_stationary 0->5, watchdogasync_refused 0->477, watchdog_home_house 0->1, wdLockSkip 0->475, watchdogasync_started 0->510, watchdog_idle_verdicts 0->766, watchdogasync_shadow_runs 0->507, watchdog_s | 0 | 2188.8/1887.7/1571.6 -> 2319.8/2126.8/1287.8 | - | rerun of bp-wdasync-stuck-repro-1111: noisy again, neither counts; timing: invalid (job VOID: runInvalid: noisy machine (others 27.9% of game CPUs); noise high); build: asi baaa4cfc174c ts3native.asi.rc1111 ts3native 0.36.239 (log ok); ts3fixes tuning 4b4eba259fc1 ts3fixes.package.rc1111 (expected game-copy/ts3fixes.package.rc1111); noise: high (other apps 28.7% of game CPUs); others on game CPUs  |
