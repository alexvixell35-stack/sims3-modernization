# Queue wdasync-fix3-1110 2026-11-09

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/wdasync-fix3-1110-2026-11-09.jsonl`.

## 2026-11-09T22:58 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T22:58 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T22:59 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T22:59 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T22:59 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T23:00 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T23:00 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T23:01 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-09T23:02 UTC, build c28bd38b, jobs bp-wdasync-stuck-repro-wdfix3, bp-wdasync-shadow-wdfix3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-wdasync-stuck-repro-wdfix3 | 23:02 (07:02) | 2.3 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_strand failed: no unroutable spot in 40 samples on Community Lot (try another lot or more tries)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 03:10 +23 min, no 03:00); ts3fixes stuckcheckasync: not reached (sim 03:10 +23 min, no 03:00)<br>FAIL: events not fired: arStartedPlanFromPoint | 1 | 2194.1/1880.8/1572.7 -> 2198.1/1924.9/1519.4 | asyncStarted +48, asyncCollected +46, asyncDropped +1, asyncUs +1838720, asyncMaxUs +826225, asyncPumpUs +223204, asyncPumpWaits +213, asyncRefusedM1Pending +1, asyncQueueMax +3 | timing: invalid (job VOID: runInvalid: profiler cut short (4 of 600 s); noisy machine (machine load: no samples); crash or abort (no "done; game al; noise medium); build: asi 107ed6b41297 ts3native.asi.wdfix2-1110 ts3native 0.36.239 (log ok); ts3fixes tuning 67ef7c4d6aa0 ts3fixes.package.wdfix3-1110 (expected game-copy/ts3fixes.package.wdfix3-1110); noise: medium (other apps 38.1% of game CPUs); o |
| bp-wdasync-shadow-wdfix3 | 23:04 (07:04) | 17.1 | PASS | PASS: profile 15 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: watchdogasync_ticks 0->23610, watchdog_route_fail_sims 0->183, watchdog_route_fails 0->2, watchdog_idle_hidden_room 0->1, watchdogasync_sent 0->800, watchdog_idle_stationary 0->5, watchdogasync_refused 0->444, watchdog_second 0->5, watchdog_home_house 0->8, wdLockSkip 0->438, watchdogasync_started 0->800, watchdog_idle_verdict | 0 | 2191.5/1884.8/1570.6 -> 2357.6/2150.8/1309.4 | asyncStarted +24393, asyncRefused +328, asyncCollected +24351, asyncDropped +5, asyncUs +292538892, asyncMaxUs +573696, asyncPumpUs +42937175, asyncPumpWaits +32875, asyncRefusedM1Pending +152, asyncSkipped +34, asyncBackoffs +178, asyncQueueMax +14 | timing: invalid (noise medium); build: asi 107ed6b41297 ts3native.asi.wdfix2-1110 ts3native 0.36.239 (log ok); ts3fixes tuning 67ef7c4d6aa0 ts3fixes.package.wdfix3-1110 (expected game-copy/ts3fixes.package.wdfix3-1110); noise: medium (other apps 31.8% of game CPUs); others on game CPUs 31.8%; clock avg/min 4169/4122 MHz; [wdasync-fix3-1110: copy of wdasync-fix2-1110 bp-wdasync-shadow on pair wdfix |
