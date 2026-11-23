# Queue mono-followups-rerun1-1117 2026-11-17

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/mono-followups-rerun1-1117-2026-11-17.jsonl`.

## 2026-11-17T21:53 UTC, build 1840874de, jobs sv-m1-gcnat-rerun1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-m1-gcnat-rerun1 | 21:53 (05:53) | 13.9 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->468, stuckcheckasync_run 0->468, etspread_passes 0->1, stuckcheck_noplan 0->431, stuckcheck_notstuck 0->29, stuckcheck_rearmed 0->8<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1698.7/1590.7/1837.3 -> 1811.8/1727.8/1759.1 | - | timing: invalid (noise medium); build: asi dd4e4c13902e ts3native.asi.master-1117b ts3native 0.36.269-mb1117 (log ok); ts3fixes tuning 8ae32451d8bc ts3fixes.package.master-1117b (expected game-copy/ts3fixes.package.master-1117b); noise: medium (other apps 26.4% of game CPUs); others on game CPUs 26.4%; clock avg/min 4133/3949 MHz; [est 16 min] DATA (correctness-grade [cut: full row in the jsonl]<br>sim ms/s 125.6; p99 59 ms, p99.9 130, max 696, >=300 6, >=1000 0 (8.4 min) |
