# Queue unseen-anim-cap-smoke 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/unseen-anim-cap-smoke-2026-11-02.jsonl`.

## 2026-11-02T03:22 UTC, build 7e874cf8, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 03:22 (11:22) | 5.7 | PASS | PASS: profile 4 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->113, stuckcheckasync_queued 0->76, idlememo_skipped 0->4, stuckcheckasync_run 0->76, idlememo_capped 0->92, stuckcheckasync_maxdepth 0->3, bouncerskip_skipped 0->3049, owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1, stuckcheck_noplan 0->58, stuckcheck_notstuck 0->5, stuckch | 0 | 2122/1831.1/1628.9 -> 2178.6/1906.7/1557.3 | asyncStarted +5510, asyncRefused +78, asyncCollected +5510, asyncUs +32389775, asyncMaxUs +176798, asyncPumpUs +15779062, asyncPumpWaits +3754, asyncRefusedM1Pending +13, asyncBackoffs +3, asyncQueueMax +4 | timing: invalid (noise medium; A/B 1 off / 2 on iterations, < 4 per arm); build: asi f26c963efa65 not in DB ts3native 0.36.156 (log ok); ts3fixes tuning 3619dc2713cf not in DB (expected game-copy/ts3fixes.package.unseentable1); noise: medium (other apps 6.3% of game CPUs); others on game CPUs 6.3%; clock avg/min 4235/4195 MHz; Measurement smoke: BP Spring 4 min, --ab unseenanim 1-min slices, CapPa |
