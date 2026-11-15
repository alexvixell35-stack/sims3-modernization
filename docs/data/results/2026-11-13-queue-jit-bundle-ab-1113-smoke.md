# Queue jit-bundle-ab-1113-smoke 2026-11-13

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/jit-bundle-ab-1113-smoke-2026-11-13.jsonl`.

## 2026-11-13T19:13 UTC, build 0544ff28, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 19:14 (03:14) | 11.8 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: idlememo_skipped 0->3, stuckcheckasync_run 0->280, stuckcheckasync_queued 0->280, bouncerskip_skipped 0->6495, stuckcheckasync_maxdepth 0->8, idlememo_capped 0->186, stuckcheckasync_maxwait_simsec 0->434, owchain_alarms 0->4, owspread_types_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->228, stuckcheck_notstuck 0->22, s | 0 | 2184.2/1895.8/1584.5 -> 2286.9/2146.1/1298.1 | asyncStarted +13229, asyncRefused +230, asyncCollected +13213, asyncDropped +6, asyncUs +193514576, asyncMaxUs +333762, asyncPumpUs +28857853, asyncPumpWaits +25051, asyncRefusedM1Pending +304, asyncSkipped +9, asyncBackoffs +74, asyncQueueMax +10 | timing: invalid (noise medium); build: asi 1c968ed16e2a not in DB ts3native 0.36.256 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rc1111e); noise: medium (other apps 9.7% of game CPUs); others on game CPUs 9.7%; clock avg/min 4237/4142 MHz; measurement smoke (AGENTS.md): BP 10 min with the ON set, Mode=1: every switc |
