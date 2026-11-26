# Queue lane-L9-1115-f1verify-rerun-1115b 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/lane-L9-1115-f1verify-rerun-1115b-2026-11-15.jsonl`.

## 2026-11-15T11:48 UTC, build d6a8dbd6, jobs bp-l9-f1verify-rerun

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-l9-f1verify-rerun | 11:48 (19:48) | 12.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->214, stuckcheckasync_run 0->214, etspread_passes 0->1, stuckcheck_noplan 0->185, stuckcheck_notstuck 0->17, stuckcheck_rearmed 0->17, stuckcheck_reset 0->2<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 2142.8/1888.6/1602.1 -> 2410.7/2210.2/1161.6 | vfThrD5D910Main +720, vfThrD5D910Sim +18, vfThrD5D910OffDevice +18, vfThrD5D910Us +144968, vfThrD5D910MaxUs +6065, vfThr619540Main +24577, vfThr619540Us +6323562, vfThr619540MaxUs +597, vfThr60D290Main +21199, vfThr60D290Us +4015487, vfThr60D290MaxUs +727, vfThr6F3DC0Main +440, vfThr6F3DC0Us +362080, vfThr6F3DC0MaxUs +12032, vfThr599720Main +233157, vfThr599720Sim +4646, vfThr599720Other +162570,  | rerun of bp-l9-f1verify: counts instead of it; timing: invalid (noise medium; heavy tool Verify); build: asi d6a252a78252 ts3native.asi.l9-f1verify-fix-1115 ts3native 0.36.262-vfchain1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package.master-dalexlol-405d-1115/ts3fixes.package.master-merge-1115 (expected game-copy/ts3fixes.package.master-merge-1115) |
