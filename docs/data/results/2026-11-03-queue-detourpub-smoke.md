# Queue detourpub-smoke 2026-11-03

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/detourpub-smoke-2026-11-03.jsonl`.

## 2026-11-03T02:05 UTC, build 54e5f01e, jobs bp-detourpub-allocsites, bp-detourpub-release

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-03T03:00 UTC, build 744bd976, jobs bp-detourpub-allocsites, bp-detourpub-release

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-detourpub-allocsites | 03:00 (11:00) | 5.1 | **FAIL** | PASS: profile 3 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->25, etspread_passes 0->1<br>PASS: repro events fired<br>FAIL: forbidden log lines: log /exception C0000005/ 3 lines (want <= 0) | 0 | 2204.5/1839.2/1563.4 -> 2205.2/1942/1509.6 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 24.0% of game CPUs); noise high); build: asi f44f8da0f74c not in DB ts3native 0.36.166 (log ok); ts3fixes tuning b779b0d14a03 rel025c-fixes (expected game-copy/ts3fixes.package.rel025c); noise: high (other apps 23.3% of game CPUs); others on game CPUs 23.3%; clock avg/min 4129/4041 MHz; [MemDiag] AllocSites=1 AllocSitesVirtual=1, world l |
| bp-detourpub-release | 03:05 (11:05) | 7.3 | **FAIL** | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->64, stuckcheckasync_run 0->64, etspread_passes 0->1, stuckcheck_noplan 0->55, stuckcheck_notstuck 0->5, stuckcheck_rearmed 0->3, stuckcheck_reset 0->1<br>PASS: repro events fired<br>FAIL: forbidden log lines: log /exception C0000005/ 3 lines (want <= 0) | 0 | 2111.5/1848.7/1628.9 -> 2215.8/2053.4/1393.8 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 24.0% of game CPUs); noise high); build: asi f44f8da0f74c not in DB ts3native 0.36.166 (log ok); ts3fixes tuning b779b0d14a03 rel025c-fixes (expected game-copy/ts3fixes.package.rel025c); noise: high (other apps 25.7% of game CPUs); others on game CPUs 25.7%; clock avg/min 4031/3360 MHz; v0.2.5 release defaults (repo ts3native.ini, AllocS |

**Verdict 2026-11-03 (manual): PASS on the job criteria** (allocsites: 11 alloc-site OK, 54 detour OK; release: 54 detour OK; 0 FAIL/SKIP). Both FAIL marks come from the `/exception C0000005/` forbid rule matching the known first-chance AV in Windows `spfileq.dll`/`setupapi.dll` at startup (in 194 logs since 10-31, also in rel025c PASS jobs without #233). Merged as c6daed6c.
