# Queue mintfuse-verify 2026-11-08

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mintfuse-verify-2026-11-08.jsonl`.

## 2026-11-08T02:12 UTC, build 9b13b565, jobs bp-mfuse-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-mfuse-verify | 02:12 (10:12) | 12 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_maxwait_simsec 0->176, stuckcheckasync_queued 0->264, idlememo_skipped 0->3, stuckcheckasync_run 0->262, stuckcheckasync_dropped 0->2, idlememo_capped 0->116, stuckcheckasync_maxdepth 0->2, bouncerskip_skipped 0->5799, owchain_alarms 0->4, owspread_types_run 0->80, etspread_passes 0->1, stuckcheck_noplan 0->200 | 0 | 2157.8/1844.9/1600.1 -> 2267.6/2084.2/1365.9 | asyncStarted +11476, asyncRefused +167, asyncCollected +11465, asyncDropped +1, asyncUs +165267265, asyncMaxUs +337345, asyncPumpUs +26139813, asyncPumpWaits +19840, asyncRefusedM1Pending +206, asyncSkipped +10, asyncBackoffs +51, asyncQueueMax +9 | timing: invalid (noise medium; heavy tool samplers); build: asi efc5de17539d ts3native.asi.g2pair1108 ts3native 0.36.190 (log ok); ts3fixes tuning 1265927843a6 ts3fixes.package.g2pair1107/ts3fixes.package.castrim3/ts3fixes.package.master0189/ts3fixes.package.g2pair1107b/ts3fixes.package.g2pair1108/ts3fixes.package.g2pair1108b (expected game-copy/ts3fixes.package.g2pair1108); noise: medium (other a |
