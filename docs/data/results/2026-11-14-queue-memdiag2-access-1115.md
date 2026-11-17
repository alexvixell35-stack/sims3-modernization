# Queue memdiag2-access-1115 2026-11-14

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/memdiag2-access-1115-2026-11-14.jsonl`.

## 2026-11-14T23:11 UTC, build 5e853290, jobs bp-memdiag2-access

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-memdiag2-access | 23:11 (07:11) | 13.9 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->262, stuckcheckasync_run 0->262, etspread_passes 0->1, stuckcheck_noplan 0->199, stuckcheck_notstuck 0->28, stuckcheck_rearmed 0->35, stuckcheck_reset 0->10<br>FAIL: events not fired: log /^diag: memaccess #\d+ load win=\d+ rc get=\d+ hit=[\d.]+ miss=\d+  | 0 | 2202.8/1878.6/1552.6 -> 2328.9/2084/1312 | - | timing: invalid (job FAIL: FAIL: events not fired: log /^diag: memaccess #\d+ load win=\d+ rc get=\d+ hit=[\d.]+ miss=\d+ keys=\d+ anim get=\d+ .* ; noise medium; heavy tool samplers); build: asi c0cb6d2bb70e ts3native.asi.memdiag2-1115 ts3native 0.36.257-memdiag2 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b/ts3fixes.package.replanlog-1114 (expected gam |

Stopped: bp-memdiag2-access failed (stopQueueOnFail): FAIL: events not fired: log /^diag: memaccess #\d+ load win=\d+ rc get=\d+ hit=[\d.]+ miss=\d+ keys=\d+ anim get=\d+ .* rd n=\d+ MB=[\d.]+ rawMB=[\d.]+ maxMB=[\d.]+ flyMaxMB=[\d.]+ big=\d+ ms=\d+ main.

## 2026-11-14T23:37 UTC, build 1e7285dd, jobs bp-memdiag2-access

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-memdiag2-access | 23:37 (07:37) | 12.2 | **FAIL** | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->3, owspread_types_run 0->60, stuckcheckasync_queued 0->172, stuckcheckasync_run 0->172, etspread_passes 0->1, stuckcheck_noplan 0->133, stuckcheck_notstuck 0->16, stuckcheck_rearmed 0->18, stuckcheck_reset 0->5<br>FAIL: events not fired: maRdMB=0 (want >=1)<br>PASS: no forbidden log lines<br>PASS: 0 new scrip | 0 | 2182.2/1877.6/1572.6 -> 2316/2147.9/1249.7 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 56.4% of game CPUs); noise high; heavy tool samplers); build: asi b17826d1a3ae ts3native.asi.memdiag2b-1115 ts3native 0.36.257-memacc2 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b/ts3fixes.package.replanlog-1114 (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 51.3% of g |

Stopped: bp-memdiag2-access failed (stopQueueOnFail): FAIL: events not fired: maRdMB=0 (want >=1).

## Verdict, memdiag2b rerun (re-judged by hand, 2026-11-15)

Raw FAIL "maRdMB=0 (want >=1)" is a queue check bug: package reads during play are ~0.2 MB per 5-min window (all big reads happen at load, 19.6 MB), so the profile-window delta of the integer MB counter is 0. Check changed to `@maRdMB>=1` (after value). Everything else held: no crash, the scan split works (rc get=177239 keys=77689 scan=489129 vs anim get=636 keys=230 scan=489765 at load; separate key counts in every line), rc printed first, reads n=189 at load + 135 in play, fail=0, ourMs ~0.07-0.15 ms/s. Re-judged PASS (correctness, diagnostics only).
