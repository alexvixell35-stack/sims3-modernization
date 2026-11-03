# Queue v020-natural-smoke 2026-10-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v020-natural-smoke-2026-10-26.jsonl`.

## 2026-10-26T16:25 UTC, build 0dc5fca8, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 16:25 (00:25) | 11.7 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->2618, stuckcheckasync_run 0->2618, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->211, stuckcheck_notstuck 0->0, stuckcheck_rearmed 0->52, stuckcheck_reset 0->24<br>FAIL: events not fired: ufcReady=0 (want >=63) | 0 | 2167.2/1854.4/1532.9 -> 2194.8/1888/1515.3 | asyncStarted +6505, asyncRefused +95, asyncCollected +6491, asyncDropped +13, asyncUs +78037574, asyncMaxUs +278633, asyncPumpUs +56323323, asyncPumpWaits +4810, asyncRefusedM1Pending +27, asyncBackoffs +4, asyncQueueMax +7 | timing: invalid (job FAIL: FAIL: events not fired: ufcReady=0 (want >=63); noise medium); build: asi 6ed3aa1ee696 not in DB ts3native 0.36.101 (log ok); ts3fixes tuning fa48a92ba45f not in DB (expected build/ts3fixes.package); noise: medium (other apps 3.7% of game CPUs); others on game CPUs 3.7%; clock avg/min 4224/4147 MHz; Measurement smoke: IP 10 min with the ON-arm config (all11 asi, full v0. |

## 2026-10-26T21:03 UTC, build 26737426, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 21:03 (05:03) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->2452, stuckcheckasync_run 0->2452, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->202, stuckcheck_notstuck 0->2, stuckcheck_rearmed 0->60, stuckcheck_reset 0->29<br>PASS: repro events fired | 0 | 2173.6/1853.7/1532.9 -> 2237.9/1886.4/1483.6 | asyncStarted +7375, asyncRefused +101, asyncCollected +7370, asyncDropped +5, asyncUs +81161514, asyncMaxUs +252310, asyncPumpUs +48872385, asyncPumpWaits +6823, asyncRefusedM1Pending +26, asyncBackoffs +9, asyncQueueMax +5 | timing: invalid (noise medium); build: asi a1ac14d39f73 not in DB ts3native 0.36.103 (log ok); ts3fixes tuning fa48a92ba45f not in DB (expected build/ts3fixes.package); noise: medium (other apps 11.4% of game CPUs); others on game CPUs 11.4%; clock avg/min 4262/4225 MHz; Measurement smoke: IP 10 min with the ON-arm config (all11 asi, full v0.2.0 routing set + hitch guard + spread fixes): every swi |

## 2026-10-26T22:34 UTC, build 5bfb3b7c, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 22:34 (06:34) | 11.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbpre_rj_townie 0->12, unseenfbteleport_no_spot 0->12, unseenfbpre_rj_task 0->38, unseenfbpre_rj_vehicle 0->868, unseenfbpre_rj_short 0->3399, unseenfbsites_lists 0->984, unseenfbpre_rj_sim 0->421, unseenfbsites_ids 0->15563, unseenfbteleport_shortcuts 0->2, owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_ | 0 | 2196.6/1852.8/1532.9 -> 2206.4/1886.6/1515.3 | asyncStarted +6260, asyncRefused +106, asyncCollected +6244, asyncDropped +13, asyncUs +71169761, asyncMaxUs +406011, asyncPumpUs +48865255, asyncPumpWaits +5129, asyncRefusedM1Pending +30, asyncSkipped +2, asyncBackoffs +4, asyncQueueMax +5 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 4b7db9a52d7f not in DB (expected build/ts3fixes.package); noise: medium (other apps 25.6% of game CPUs); others on game CPUs 25.6%; clock avg/min 4181/4159 MHz; Measurement smoke: IP 10 min with the ON-arm config (all11 asi, full v0.2.0 routing set + hitch guard + spread fixes): every swi |
