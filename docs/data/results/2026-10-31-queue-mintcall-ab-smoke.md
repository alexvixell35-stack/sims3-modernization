# Queue mintcall-ab-smoke 2026-10-31

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mintcall-ab-smoke-2026-10-31.jsonl`.

## 2026-10-31T13:07 UTC, build 7395b531, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 13:07 (21:07) | 5.7 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->48, stuckcheckasync_queued 0->80, idlememo_skipped 0->1, stuckcheckasync_run 0->80, idlememo_capped 0->126, stuckcheckasync_maxdepth 0->2, bouncerskip_skipped 0->2991, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->66, stuckcheck_notstuck 0->4, stuckch | 0 | 2164/1845.4/1564.9 -> 2182.6/2041.8/1347.3 | asyncStarted +5452, asyncRefused +89, asyncCollected +5451, asyncDropped +1, asyncUs +37724217, asyncMaxUs +167709, asyncPumpUs +18533959, asyncPumpWaits +4371, asyncRefusedM1Pending +22, asyncBackoffs +3, asyncQueueMax +6 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi 43861c859373 not in DB ts3native 0.36.127 (log ok); ts3fixes tuning 2113f393f5d2 rel025a-fixes (expected build/ts3fixes.package); noise: medium (other apps 13.2% of game CPUs); others on game CPUs 13.2%; clock avg/min 4230/4169 MHz; Measurement smoke (AGENTS.md): BP 4 min, --ab mintcall 1-min slices. Pass: both ar |
