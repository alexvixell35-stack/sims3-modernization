# Queue master-smoke-1115-mm 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/master-smoke-1115-mm-2026-11-15.jsonl`.

## 2026-11-15T12:07 UTC, build 4cd10dd9, jobs bp-master-mono-1115-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-master-mono-1115-smoke | 12:07 (20:07) | 7.3 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->116, stuckcheckasync_run 0->116, etspread_passes 0->1, stuckcheck_noplan 0->98, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->8<br>PASS: repro events fired | 0 | 2233.7/1897.2/1519.7 -> 2280.8/2094.4/1278.9 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 25.8% of game CPUs); noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.master-mono-1115 (expected  |
