# Queue f1-callpath-m1110-j1 2026-11-10

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/f1-callpath-m1110-j1-2026-11-10.jsonl`.

## 2026-11-10T05:00 UTC, build c2bb5ed, jobs ip-f1-mintcall-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-f1-mintcall-ab | 05:00 (13:00) | 16.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->385, stuckcheckasync_run 0->378, etspread_passes 0->1, stuckcheck_noplan 0->292, stuckcheck_notstuck 0->21, stuckcheck_rearmed 0->51, stuckcheck_reset 0->15 | 0 | 2227.6/2000.7/1535.1 -> 2298.7/2202/1309.3 | - | timing: valid-half; build: asi bf3e9d8c9fc8 not in DB ts3native 0.36.239 (log ok); ts3fixes tuning 7c3921b1573a not in DB (expected build/ts3fixes.package); noise: low; others on game CPUs 0.3%; clock avg/min 4712/4638 MHz; F1 in-load A/B, IP 10 min, seed 12345, off first: --ab mintcall flips mcSwitch per 2-min slice, ini CallPath=1 so the hook is installed (the repo ini default 0 installs none, c |
