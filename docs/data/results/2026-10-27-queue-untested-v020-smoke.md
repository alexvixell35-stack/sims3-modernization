# Queue untested-v020-smoke 2026-10-27

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/untested-v020-smoke-2026-10-27.jsonl`.

## 2026-10-27T04:39 UTC, build 715ba46c, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 04:39 (12:39) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->407, stuckcheckasync_run 0->407, etspread_passes 0->1, stuckcheck_noplan 0->49, stuckcheck_notstuck 0->154, stuckcheck_rearmed 0->162, stuckcheck_reset 0->42<br>PASS: repro events fired | 0 | 2169.5/1860.8/1516.9 -> 2186.5/1887.6/1481.6 | asyncStarted +7586, asyncRefused +89, asyncCollected +7576, asyncDropped +10, asyncUs +79404006, asyncMaxUs +356494, asyncPumpUs +55697719, asyncPumpWaits +6249, asyncRefusedM1Pending +32, asyncBackoffs +7, asyncQueueMax +9 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.1% of game CPUs); others on game CPUs 8.1%; clock avg/min 4198/4151 MHz; Measurement smoke: IP 10 min on the kinddl asi with job 7's switch set and light tools (checks the per-arm-free natural measuremen |

## 2026-10-27T14:32 UTC, build a12bad58, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 14:32 (22:32) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->404, stuckcheckasync_run 0->404, etspread_passes 0->1, stuckcheck_noplan 0->45, stuckcheck_notstuck 0->154, stuckcheck_rearmed 0->157, stuckcheck_reset 0->48<br>PASS: repro events fired | 0 | 2196.6/1854.6/1500.9 -> 2199.4/1897.5/1467.4 | asyncStarted +7694, asyncRefused +105, asyncCollected +7684, asyncDropped +8, asyncUs +76958020, asyncMaxUs +357258, asyncPumpUs +52827911, asyncPumpWaits +5688, asyncRefusedM1Pending +41, asyncSkipped +1, asyncBackoffs +2, asyncQueueMax +6 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: medium (other apps 4.2% of game CPUs); others on game CPUs 4.2%; clock avg/min 4214/4173 MHz; Measurement smoke: IP 10 min on the kinddl asi with job 7's switch set and light tools (checks the per-arm-free natural measuremen |
