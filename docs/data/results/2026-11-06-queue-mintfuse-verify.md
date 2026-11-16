# Queue mintfuse-verify 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mintfuse-verify-2026-11-06.jsonl`.

## 2026-11-06T17:14 UTC, build cd5d0ccf, jobs bp-mfuse-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-mfuse-verify | 17:14 (01:14) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->240, stuckcheckasync_queued 0->295, idlememo_skipped 0->5, stuckcheckasync_run 0->295, idlememo_capped 0->141, stuckcheckasync_maxdepth 0->4, bouncerskip_skipped 0->6557, owchain_alarms 0->4, owspread_types_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->247, stuckcheck_notstuck 0->22, s | 0 | 2156/1827.4/1608.9 -> 2253.5/2074.2/1347.1 | asyncStarted +12775, asyncRefused +209, asyncCollected +12762, asyncDropped +1, asyncUs +172432294, asyncMaxUs +473579, asyncPumpUs +30057128, asyncPumpWaits +23716, asyncRefusedM1Pending +194, asyncSkipped +10, asyncBackoffs +46, asyncQueueMax +10 | timing: invalid (noise medium; heavy tool samplers); build: asi e36bc1e47aa7 ts3native.asi.mono1106c ts3native 0.36.175 (log ok); ts3fixes tuning 6fd5814bc342 ts3fixes.package.mono1106/ts3fixes.package.mono1106-upark-live/ts3fixes.package.mono1106b/ts3fixes.package.mono1106b-upark-live/ts3fixes.package.mono1106c/ts3fixes.package.mono1106c-upark-live (expected game-copy/ts3fixes.package.mono1106c); |
