# Queue stove-wall-player 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-wall-player-2026-11-07.jsonl`.

## 2026-11-07T03:13 UTC, build c10a3c09, jobs stove-wall-player

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-player | 03:13 (11:13) | 6 | PASS | PASS: profile stopped at 2 events after 2.9 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->36, stuckcheckasync_run 0->36, etspread_passes 0->1, stuckcheck_noplan 0->31, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->1<br>PASS: repro events fired | 0 | 2107.2/1790.9/1644.9 -> 2179.7/1830.5/1571.6 | asyncStarted +1098, asyncRefused +32, asyncCollected +1097, asyncUs +26012278, asyncMaxUs +1041675, asyncPumpUs +3705538, asyncPumpWaits +2681, asyncRefusedM1Pending +3, asyncBackoffs +3, asyncQueueMax +7 | timing: invalid (noise medium; A/B 1 off / 1 on iterations, < 4 per arm (repro firings)); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.9% of game CPUs); others on game CPUs 17.9%; clock avg/min 4155/4125 MHz |

## 2026-11-07T03:19 UTC, build 041a0a5e, jobs stove-wall-player

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-player | 03:19 (11:19) | 6 | PASS | PASS: profile stopped at 2 events after 2.9 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->50, stuckcheckasync_run 0->50, etspread_passes 0->1, stuckcheck_noplan 0->32, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->5<br>PASS: repro events fired | 0 | 2087.2/1785.2/1644.9 -> 2173.7/1970.8/1443.6 | asyncStarted +1162, asyncRefused +44, asyncCollected +1161, asyncUs +29273773, asyncMaxUs +703443, asyncPumpUs +4217496, asyncPumpWaits +2302, asyncRefusedM1Pending +2, asyncBackoffs +9, asyncQueueMax +6 | timing: invalid (job VOID: runInvalid: noisy machine (others 24.3% of game CPUs); noise medium; A/B 1 off / 1 on iterations, < 4 per arm); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 23.1% of game CPUs); others on game CPUs 23.1%; clock avg/min 4134/4057 MHz |

## 2026-11-07T03:28 UTC, build e01080f7, jobs stove-wall-player

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-player | 03:28 (11:28) | 6 | PASS | PASS: profile stopped at 2 events after 2.9 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->51, stuckcheckasync_run 0->51, etspread_passes 0->1, stuckcheck_noplan 0->34, stuckcheck_notstuck 0->8, stuckcheck_rearmed 0->8, stuckcheck_reset 0->1<br>PASS | 0 | 2105.9/1792.3/1656.9 -> 2161/1959.9/1447 | asyncStarted +1192, asyncRefused +39, asyncCollected +1191, asyncUs +38836247, asyncMaxUs +1701327, asyncPumpUs +4224546, asyncPumpWaits +2703, asyncRefusedM1Pending +2, asyncBackoffs +10, asyncQueueMax +7 | timing: invalid (noise medium; A/B 1 off / 1 on iterations, < 4 per arm); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.6% of game CPUs); others on game CPUs 17.6%; clock avg/min 4149/4119 MHz |

## 2026-11-07T03:35 UTC, build 7ea78a91, jobs stove-wall-player

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-player | 03:35 (11:35) | 6 | **FAIL** | PASS: profile stopped at 2 events after 2.9 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->50, stuckcheckasync_run 0->50, etspread_passes 0->1, stuckcheck_noplan 0->38, stuckcheck_notstuck 0->7, stuckcheck_rearmed 0->5<br>FAIL: events not fired: wat | 0 | 2119.2/1790.8/1644.9 -> 2157.4/1827/1579.6 | asyncStarted +1061, asyncRefused +32, asyncCollected +1060, asyncUs +29980601, asyncMaxUs +2176214, asyncPumpUs +5938792, asyncPumpWaits +2220, asyncRefusedM1Pending +2, asyncBackoffs +2, asyncQueueMax +7 | timing: invalid (job FAIL: FAIL: events not fired: watchdogs=1 (want <=0); noise medium; A/B 1 off / 1 on iterations, < 4 per arm); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.0% of game CPUs); others on game CPUs 17.0%; clock avg/min 4149/4105 MHz |

Stopped: stove-wall-player failed (stopQueueOnFail): FAIL: events not fired: watchdogs=1 (want <=0).

## 2026-11-07T03:43 UTC, build f762a519, jobs stove-wall-player

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-wall-player | 03:43 (11:43) | 6 | PASS | PASS: profile stopped at 2 events after 2.9 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->46, stuckcheckasync_run 0->46, etspread_passes 0->1, stuckcheck_noplan 0->29, stuckcheck_notstuck 0->9, stuckcheck_rearmed 0->7, stuckcheck_reset 0->2<br>PASS | 0 | 2104.8/1789.6/1644.9 -> 2173.9/1826.8/1579.6 | asyncStarted +1072, asyncRefused +42, asyncCollected +1068, asyncUs +32981163, asyncMaxUs +968803, asyncPumpUs +3518187, asyncPumpWaits +2456, asyncRefusedM1Pending +6, asyncBackoffs +4, asyncQueueMax +7 | timing: invalid (noise medium; A/B 1 off / 1 on iterations, < 4 per arm); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 3296fe74ed3e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.2% of game CPUs); others on game CPUs 17.2%; clock avg/min 4154/4120 MHz |
