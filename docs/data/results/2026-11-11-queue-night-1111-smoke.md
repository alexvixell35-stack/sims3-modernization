# Queue night-1111-smoke 2026-11-11

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1111-smoke-2026-11-11.jsonl`.

## 2026-11-11T03:55 UTC, build 3698341d, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 03:55 (11:55) | 11.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: watchdogasync_ticks 0->12703, watchdog_route_fail_sims 0->94, watchdog_route_fails 0->18, watchdogasync_sent 0->290, watchdog_idle_stationary 0->6, watchdogasync_refused 0->3, watchdogasync_started 0->290, watchdog_idle_verdicts 0->812, watchdogasync_stale 0->6, watchdog_idle_unroutable 0->2, owchain_alarms 0->5, owspread_type | 0 | 2118.7/1882.7/1588.8 -> 2173.2/1907.1/1520.3 | - | timing: invalid (noise medium); build: asi f05a9fd6a71c ts3native.asi.rc1111d ts3native 0.36.240 (log ok); ts3fixes tuning 3bee114eaf9b ts3fixes.package.rc1111c (expected game-copy/ts3fixes.package.rc1111c); noise: medium (other apps 9.2% of game CPUs); others on game CPUs 9.2%; clock avg/min 4246/4178 MHz; measurement smoke: rc1111 on IP, --ab watchdogasync 2-min slices, 10 min: version, Trim (Se |
