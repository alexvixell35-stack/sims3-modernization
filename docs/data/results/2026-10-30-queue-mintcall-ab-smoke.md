# Queue mintcall-ab-smoke 2026-10-30

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mintcall-ab-smoke-2026-10-30.jsonl`.

## 2026-10-30T09:09 UTC, build 384512a5, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 09:09 (17:09) | 5.6 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->63, stuckcheckasync_queued 0->76, idlememo_skipped 0->3, stuckcheckasync_run 0->76, idlememo_capped 0->95, stuckcheckasync_maxdepth 0->3, bouncerskip_skipped 0->2974, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->65, stuckcheck_notstuck 0->5, stuckche | 0 | 2207.5/1860.1/1516.9 -> 2219.9/1934.3/1476.9 | asyncStarted +5382, asyncRefused +75, asyncCollected +5381, asyncUs +33878067, asyncMaxUs +198858, asyncPumpUs +16477248, asyncPumpWaits +4092, asyncRefusedM1Pending +16, asyncSkipped +1, asyncBackoffs +3, asyncQueueMax +5 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi 43861c859373 not in DB ts3native 0.36.127 (log ok); ts3fixes tuning 94c60769ef8f not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.3% of game CPUs); others on game CPUs 5.3%; clock avg/min 4250/4214 MHz; Measurement smoke (AGENTS.md): BP 4 min, --ab mintcall 1-min slices. Pass: both arms pre |
