# Queue kd-uncut-ceiling-ip-rerun2-1115-mm 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kd-uncut-ceiling-ip-rerun2-1115-mm-2026-11-16.jsonl`.

## 2026-11-16T04:06 UTC, build dafc8ae3, jobs ip-kd-uncut-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-kd-uncut-ceiling | 04:06 (12:06) | 6.8 | PASS | PASS: profile stopped at 12 events after 3.4 min (off 6, on 6; 12 firings, 3 cycles; rule 5 events per arm), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->94, stuckcheckasync_run 0->94, etspread_passes 0->1, stuckcheck_noplan 0->68, stuckcheck_notstuck 0->7, stuckcheck_rearmed 0->16, stuckcheck_reset 0->4<br>P | 0 | 1668.5/1587/1821.4 -> 1769.8/1688.8/1803.2 | asyncStarted +3496, asyncRefused +33, asyncCollected +3489, asyncDropped +4, asyncUs +134914690, asyncMaxUs +530643, asyncPumpUs +13182621, asyncPumpWaits +1790, asyncRefusedM1Pending +11, asyncSkipped +3, asyncBackoffs +64, asyncQueueMax +11 | timing: invalid (job VOID: runInvalid: noisy machine (others 35.8% of game CPUs); noise high); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.master-mono-1115 (expected ga |
