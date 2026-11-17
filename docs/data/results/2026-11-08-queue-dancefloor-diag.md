# Queue dancefloor-diag 2026-11-08

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/dancefloor-diag-2026-11-08.jsonl`.

## 2026-11-08T10:31 UTC, build 896c0e8b, jobs bp-dancediag-release, bp-dancediag-norouteasync, bp-dancediag-nogc, bp-dancediag-castrim

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-dancediag-release | 10:35 (18:35) | 2.1 | **FAIL** | FAIL: profile 50 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step dance_together_floor failed: dance_together_floor fire: the pair is not on Waylon's Haunt<br>ts3fixes activity: etspread_passes 0->1<br>FAIL: events not fired: log /^dancefloor list: call #\d+ / 0 lines (want >= 20)<br>PASS: no forbidden log lines<br>REPORT: 0 script errors matching Collection was modified[\s\S]*(FindAndRouteOnt | 1 | 2152.9/1839.6/1596.9 -> 2143.4/1848.8/1565.7 | - | timing: invalid (job VOID: runInvalid: profiler cut short (21 of 3000 s); noisy machine (machine load: no samples); crash or abort (no "done; game ; noise medium); build: asi 56edfb9a1b73 ts3native.asi.monojit1108/ts3native.asi.dancediag1108 ts3native 0.36.200 (log ok); ts3fixes tuning 24e0fad3a692 ts3fixes.package.bookhand1108d/ts3fixes.package.castrimdiag1108b/ts3fixes.package.tier2rows1108/ts3f |
