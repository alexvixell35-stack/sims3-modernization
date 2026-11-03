# Queue v020-natural-smoke 2026-10-27

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v020-natural-smoke-2026-10-27.jsonl`.

## 2026-10-27T15:55 UTC, build a12bad58, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 15:55 (23:55) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbpre_rj_townie 0->11, unseenfbteleport_no_spot 0->23, unseenfbpre_rj_task 0->32, unseenfbpre_rj_vehicle 0->950, unseenfbpre_rj_short 0->3635, unseenfbsites_lists 0->1030, unseenfbpre_rj_sim 0->336, unseenfbsites_ids 0->13867, unseenfbteleport_shortcuts 0->3, owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasyn | 0 | 2200.9/1853/1500.9 -> 2196.9/1879.4/1483.3 | asyncStarted +6522, asyncRefused +111, asyncCollected +6514, asyncDropped +7, asyncUs +69479922, asyncMaxUs +360407, asyncPumpUs +48395519, asyncPumpWaits +5157, asyncRefusedM1Pending +27, asyncBackoffs +1, asyncQueueMax +6 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.1% of game CPUs); others on game CPUs 5.1%; clock avg/min 4216/4155 MHz; Measurement smoke: IP 10 min with the ON-arm config (all11 asi, full v0.2.0 routing set + hitch guard + spread fixes): every switc |
