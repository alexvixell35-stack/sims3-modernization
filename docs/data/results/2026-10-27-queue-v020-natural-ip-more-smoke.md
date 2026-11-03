# Queue v020-natural-ip-more-smoke 2026-10-27

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/v020-natural-ip-more-smoke-2026-10-27.jsonl`.

## 2026-10-27T23:01 UTC, build 5b430d84, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 23:01 (07:01) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenfbpre_rj_townie 0->14, unseenfbteleport_no_spot 0->15, unseenfbpre_rj_task 0->43, unseenfbpre_rj_vehicle 0->890, unseenfbpre_rj_short 0->3467, unseenfbsites_lists 0->1009, unseenfbpre_rj_sim 0->393, unseenfbsites_ids 0->14050, unseenfbteleport_shortcuts 0->2, owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasyn | 0 | 2150.6/1853.5/1500.9 -> 2188.3/1879.5/1483.3 | asyncStarted +6398, asyncRefused +109, asyncCollected +6385, asyncDropped +12, asyncUs +71866373, asyncMaxUs +356009, asyncPumpUs +49702706, asyncPumpWaits +5075, asyncRefusedM1Pending +25, asyncBackoffs +3, asyncQueueMax +4 | timing: invalid (noise medium); build: asi df445a71d641 not in DB ts3native 0.36.104 (log ok); ts3fixes tuning 8b324168a202 not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.7% of game CPUs); others on game CPUs 5.7%; clock avg/min 4190/4107 MHz; Measurement smoke: IP 10 min with the ON-arm config (all11 asi, full v0.2.0 routing set + hitch guard + spread fixes): every switc |
