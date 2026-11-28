# Queue rebaseline-apex401-1115-smoke 2026-11-16

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/rebaseline-apex401-1115-smoke-2026-11-16.jsonl`.

## 2026-11-16T04:28 UTC, build dafc8ae3, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 04:28 (12:28) | 12 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->277, stuckcheckasync_run 0->274, etspread_passes 0->1, stuckcheck_noplan 0->222, stuckcheck_notstuck 0->24, stuckcheck_rearmed 0->26, stuckcheck_reset 0->7<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 1719.3/1630.5/1749.3 -> 1954.3/1865.5/1641.6 | - | timing: invalid (noise medium); build: asi 7860ed4bde30 ts3native.asi.master-mono-1115 ts3native 0.36.264-mono1 (log ok); ts3fixes tuning f8853892e12c ts3fixes.package.master-dalexlol-417-1115/ts3fixes.package.l3-convgone-1115/ts3fixes.package.mono-autshadow-1115/ts3fixes.mono-offload-1115/ts3fixes.package.master-mono-1115 (expected game-copy/ts3fixes.package.master-mono-1115); noise: medium (othe |
