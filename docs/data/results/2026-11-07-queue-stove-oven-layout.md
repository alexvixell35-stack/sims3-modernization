# Queue stove-oven-layout 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stove-oven-layout-2026-11-07.jsonl`.

## 2026-11-07T07:48 UTC, build 7a97baab, jobs stove-oven-layout

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-oven-layout | 07:48 (15:48) | 5.2 | **FAIL** | PASS: profile stopped at 2 events after 2.0 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 18 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->82, stuckcheckasync_run 0->82, etspread_passes 0->1, stuckcheck_noplan 0->73, stuckcheck_notstuck 0->3, stuckcheck_rearmed 0->4, stuckcheck_reset 0->2<br>FAIL | 0 | 2130.3/1847.2/1592.9 -> 2203.4/1895.9/1505.8 | asyncStarted +3516, asyncRefused +43, asyncCollected +3510, asyncDropped +1, asyncUs +41061023, asyncMaxUs +1117621, asyncPumpUs +6236970, asyncPumpWaits +3382, asyncRefusedM1Pending +14, asyncSkipped +2, asyncBackoffs +20, asyncQueueMax +18 | timing: invalid (job FAIL: FAIL: events not fired: log /stove-oven: arm 0 CookInOven seen/ 0 lines (want >= 1); log /stove-oven: arm 1 CookInOven s; noise medium; A/B 1 off / 1 on iterations, < 4 per arm (repro firings)); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107 (expected build/ts3fixes.package); noise: m |

## 2026-11-07T08:23 UTC, build a84a5dd4, jobs stove-oven-layout

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| stove-oven-layout | 08:23 (16:23) | 6.9 | PASS | PASS: profile stopped at 2 events after 3.4 min (off 1, on 1; 2 firings, 1 cycles; rule 1 events per arm), cap 18 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->117, stuckcheckasync_run 0->117, etspread_passes 0->1, stuckcheck_noplan 0->104, stuckcheck_notstuck 0->2, stuckcheck_rearmed 0->8, stuckcheck_reset 0->3<br>P | 0 | 2132.3/1852.3/1596.9 -> 2161.4/1886.6/1524.9 | asyncStarted +3814, asyncRefused +44, asyncCollected +3805, asyncDropped +3, asyncUs +45571990, asyncMaxUs +1230714, asyncPumpUs +6387818, asyncPumpWaits +3320, asyncRefusedM1Pending +19, asyncSkipped +6, asyncBackoffs +18, asyncQueueMax +12 | timing: invalid (noise medium; A/B 1 off / 1 on iterations, < 4 per arm); build: asi b3e008f42a81 ts3native.asi.household3 ts3native 0.36.184 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107 (expected build/ts3fixes.package); noise: medium (other apps 20.6% of game CPUs); others on game CPUs 20.6%; clock avg/min 4159/4139 MHz |
