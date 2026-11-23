# Queue commodmap-fast-1121-correct-rerun2 2026-11-21

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/commodmap-fast-1121-correct-rerun2-2026-11-21.jsonl`.

## 2026-11-21T02:52 UTC, build 14aed570a, jobs bp-commodmap-check, ip-mm-commodmap-shadow

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-commodmap-check | 02:53 (10:53) | 14.5 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1; ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0, 1 known EA ignored) | 0 | 1744/1683.6/1819.1 -> 1848.8/1782/1786.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 11.4% >= 10%); noise high); build: asi 66f4d0305688 ts3native.asi.mkit2-1121 ts3native 0.36.298-mkit2 (log ok); ts3fixes tuning e290a6386bec ts3fixes.package.cmf-1121 (expected game-copy/ts3fixes.package.cmf-1121); noise: high (other apps 45.0% of game CPUs, NOT comparable (load or clock)); others on game CPUs 45.0% [cut: full row in the jsonl]<br>sim ms/s 62.3; p99 <50 ms, p99.9 <50, max 115, >=300 0, >=1000 0 (4.7 min) |
| ip-mm-commodmap-shadow | 03:07 (11:07) | 0 | **N/A** | N/A: save ManyMods-IP-aged missing in Saves/ (copy it back, then rerun) | - | - | - | - |
