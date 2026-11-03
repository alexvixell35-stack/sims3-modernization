# Queue untested-v020-smoke 2026-10-26

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/untested-v020-smoke-2026-10-26.jsonl`.

## 2026-10-26T16:37 UTC, build 0dc5fca8, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 16:37 (00:37) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->381, stuckcheckasync_run 0->380, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->41, stuckcheck_notstuck 0->154, stuckcheck_rearmed 0->145, stuckcheck_reset 0->40<br>PASS: repro events fired | 0 | 2195.2/1854.9/1516.9 -> 2196.6/1884.9/1499.3 | asyncStarted +7383, asyncRefused +96, asyncCollected +7368, asyncDropped +14, asyncUs +77574655, asyncMaxUs +293053, asyncPumpUs +51961549, asyncPumpWaits +6063, asyncRefusedM1Pending +33, asyncBackoffs +7, asyncQueueMax +6 | timing: invalid (noise medium); build: asi 9c0d99f4c8e7 not in DB ts3native 0.36.100 (log ok); ts3fixes tuning fa48a92ba45f not in DB (expected build/ts3fixes.package); noise: medium (other apps 4.0% of game CPUs); others on game CPUs 4.0%; clock avg/min 4233/4161 MHz; Measurement smoke: IP 10 min on the kinddl asi with job 7's switch set and light tools (checks the per-arm-free natural measuremen |
