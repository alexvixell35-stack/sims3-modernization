# Queue jit-bailcensus 2026-11-13

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/jit-bailcensus-2026-11-13.jsonl`.

## 2026-11-13T10:53 UTC, build 0ed40427, jobs bp-jit-bailcensus

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-jit-bailcensus | 10:53 (18:53) | 14.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: idlememo_skipped 0->2, stuckcheckasync_run 0->95, stuckcheckasync_queued 0->95, bouncerskip_skipped 0->2883, stuckcheckasync_maxdepth 0->2, idlememo_capped 0->162, stuckcheckasync_maxwait_simsec 0->178, owchain_alarms 0->2, owspread_types_run 0->40, etspread_passes 0->1, stuckcheck_noplan 0->73, stuckcheck_notstuck 0->9, stuck | 0 | 2411.1/2134.6/1370.8 -> 2440.8/2329.3/1123.9 | asyncStarted +4784, asyncRefused +86, asyncCollected +4784, asyncUs +134632953, asyncMaxUs +739411, asyncPumpUs +6619109, asyncPumpWaits +2958, asyncRefusedM1Pending +27, asyncBackoffs +68, asyncQueueMax +8 | timing: invalid (noise medium); build: asi 8a7286741b13 not in DB ts3native 0.36.251 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e); noise: medium (other apps 11.6% of game CPUs); others on game CPUs 11.6%; clock avg/min 4196/4090 MHz; BP 10 min count run: Mode=2 verify, Direct=1 DirectLeaf=0, CodeKB=4096, Cou |
