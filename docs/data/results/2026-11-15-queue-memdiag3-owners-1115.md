# Queue memdiag3-owners-1115 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/memdiag3-owners-1115-2026-11-15.jsonl`.

## 2026-11-15T00:20 UTC, build e444ac96, jobs bp-memdiag3-owners

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-memdiag3-owners | 00:20 (08:20) | 12.4 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->209, stuckcheckasync_run 0->207, etspread_passes 0->1, stuckcheck_noplan 0->182, stuckcheck_notstuck 0->12, stuckcheck_rearmed 0->14, stuckcheck_reset 0->5<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 2196.2/1886.6/1577.2 -> 2292.6/2123.6/1300.7 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 33.7% of game CPUs); noise high; heavy tool samplers); build: asi 7b1ebca13f15 ts3native.asi.memdiag3-1115 ts3native 0.36.260-memdiag3 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b/ts3fixes.package.replanlog-1114 (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 33.3% of g |

## 2026-11-15T00:35 UTC, build e444ac96, jobs bp-memdiag3-owners-apex

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-memdiag3-owners-apex | 00:35 (08:35) | 12.2 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->219, stuckcheckasync_run 0->219, etspread_passes 0->1, stuckcheck_noplan 0->179, stuckcheck_notstuck 0->13, stuckcheck_rearmed 0->25, stuckcheck_reset 0->7<br>PASS: repro events fired<br>PASS: no forbidden log lines<br>PASS: 0 new script errors (max 0) | 0 | 1790.3/1673.6/1805.5 -> 1907/1834.1/1583.9 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 32.9% of game CPUs); noise high; heavy tool samplers); build: asi 7b1ebca13f15 ts3native.asi.memdiag3-1115 ts3native 0.36.260-memdiag3 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b/ts3fixes.package.replanlog-1114 (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 33.2% of g |

## Verdict

PASS 2/2 (bp-memdiag3-owners plain Bin, bp-memdiag3-owners-apex with the latest Apex Radiance + RTX Remix bridge): queue checks pass, 0 new script errors, no crash; Bin restored by sha256 after the Apex job (423 files equal the snapshot). Timing VOID on both jobs (noisy machine, heavy tool samplers): correctness and data run only, no ms/s claim.
