# Queue minihomelot-1114 2026-11-14

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/minihomelot-1114-2026-11-14.jsonl`.

## 2026-11-14T12:05 UTC, build bf419742, jobs bp-minihomelot-check

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-minihomelot-check | 12:05 (20:05) | 7.3 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->135, stuckcheckasync_run 0->135, etspread_passes 0->1, stuckcheck_noplan 0->105, stuckcheck_notstuck 0->11, stuckcheck_rearmed 0->19, stuckcheck_reset 0->6 | 0 | 2179/1891.7/1575 -> 2239.8/2102.2/1313.4 | - | timing: invalid (noise medium); build: asi 1017eee08f92 ts3native.asi.master1112b ts3native 0.36.248 (log ok); ts3fixes tuning 891c44f273b7 minihomelot-1114 (expected game-copy/ts3fixes.package.minihomelot-1114); noise: medium (other apps 21.2% of game CPUs); others on game CPUs 21.2%; clock avg/min 4184/4040 MHz; [est 8 min] Correctness (5 min): repro minihomelot-movein-check. Pass: off HasSameHo |
