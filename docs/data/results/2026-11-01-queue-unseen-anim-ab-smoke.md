# Queue unseen-anim-ab-smoke 2026-11-01

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-anim-ab-smoke-2026-11-01.jsonl`.

## 2026-11-01T23:03 UTC, build 508eb8c0, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 23:03 (07:03) | 5.7 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->48, stuckcheckasync_queued 0->69, idlememo_skipped 0->4, stuckcheckasync_run 0->69, idlememo_capped 0->127, stuckcheckasync_maxdepth 0->2, bouncerskip_skipped 0->2724, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->66, stuckcheck_notstuck 0->2, stuckch | 0 | 2131.5/1858/1548.9 -> 2237.6/1950.6/1467.3 | asyncStarted +6296, asyncRefused +97, asyncCollected +6295, asyncUs +36672067, asyncMaxUs +131999, asyncPumpUs +17034942, asyncPumpWaits +3892, asyncRefusedM1Pending +9, asyncBackoffs +2, asyncQueueMax +5 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi 1d19bad88bc3 not in DB ts3native 0.36.153 (log ok); ts3fixes tuning a0e1dc9b078c not in DB (expected game-copy/ts3fixes.package.kit5-wakehooks); noise: medium (other apps 11.0% of game CPUs); others on game CPUs 11.0%; clock avg/min 4246/4209 MHz; Measurement smoke (AGENTS.md): BP Spring 4 min, --ab unseenanim 1-m |
