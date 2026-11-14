# Queue kd-uncut-ceiling-1114 2026-11-14

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kd-uncut-ceiling-1114-2026-11-14.jsonl`.

## 2026-11-14T03:10 UTC, build c3265be8, jobs sv-kduncut-smoke, sv-kd-uncut-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kduncut-smoke | 03:10 (11:10) | 8.1 | **FAIL** | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->145, stuckcheckasync_run 0->145, etspread_passes 0->1, stuckcheck_noplan 0->134, stuckcheck_notstuck 0->8, stuckcheck_rearmed 0->3<br>FAIL: events not fired: log /[RouteAsync] KdUncutCeilMs=30000/ 0 lines (want >= 1)<br>PASS: 0 new script errors (max 0) | 0 | 2092.4/1809.7/1662.6 -> 2152.4/1853.1/1595.2 | asyncStarted +2578, asyncRefused +96, asyncCollected +2577, asyncUs +97918016, asyncMaxUs +483361, asyncPumpUs +10315943, asyncPumpWaits +8948, asyncRefusedM1Pending +9, asyncBackoffs +30, asyncQueueMax +7 | timing: invalid (job VOID: runInvalid: noisy machine (other CPU 11.3% >= 10%); noise high; heavy tool samplers); build: asi ad3e8ad4273f ts3native.asi.uncutceil-1114 ts3native 0.36.253 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 53.8% of game CPUs, NOT comparable (load or clock));  |

Stopped: sv-kduncut-smoke failed (stopQueueOnFail): FAIL: events not fired: log /[RouteAsync] KdUncutCeilMs=30000/ 0 lines (want >= 1).

## 2026-11-14T07:28 UTC, build da739355, jobs sv-kduncut-smoke, sv-kd-uncut-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kduncut-smoke | 07:29 (15:29) | 6.9 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->146, stuckcheckasync_run 0->146, etspread_passes 0->1, stuckcheck_noplan 0->139, stuckcheck_notstuck 0->7<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 2129/1826.6/1624.8 -> 2145.3/2000.7/1448.8 | asyncStarted +2926, asyncRefused +97, asyncCollected +2924, asyncDropped +1, asyncUs +87657526, asyncMaxUs +405888, asyncPumpUs +11127383, asyncPumpWaits +8775, asyncRefusedM1Pending +2, asyncBackoffs +22, asyncQueueMax +6 | timing: invalid (job VOID: runInvalid: noisy machine (others 45.0% of game CPUs); noise high; heavy tool samplers); build: asi ad3e8ad4273f ts3native.asi.uncutceil-1114 ts3native 0.36.253 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rel0211b); noise: high (other apps 41.5% of game CPUs); others on game CPUs 41.5%; cl |
| sv-kd-uncut-ceiling | 07:35 (15:35) | 11.9 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->385, stuckcheckasync_run 0->385, etspread_passes 0->1, stuckcheck_noplan 0->355, stuckcheck_notstuck 0->21, stuckcheck_rearmed 0->11<br>PASS: repro events fired<br>PASS: 0 new script errors (max 0) | 0 | 2141.5/1836.2/1608.8 -> 2207.9/2042.9/1421.9 | asyncStarted +6462, asyncRefused +213, asyncCollected +6456, asyncDropped +2, asyncUs +188643502, asyncMaxUs +611256, asyncPumpUs +17403143, asyncPumpWaits +19336, asyncRefusedM1Pending +48, asyncSkipped +2, asyncBackoffs +60, asyncQueueMax +7 | timing: invalid (noise medium); build: asi ad3e8ad4273f ts3native.asi.uncutceil-1114 ts3native 0.36.253 (log ok); ts3fixes tuning 0373884c6b0f ts3fixes.package.rc1111e/ts3fixes.package.rel0211b (expected game-copy/ts3fixes.package.rel0211b); noise: medium (other apps 15.8% of game CPUs); others on game CPUs 15.8%; clock avg/min 4223/4161 MHz; SV 10 min natural with the repro sv-kd-uncut-ceiling (n |

## Reading (docs/264 §7)

Build da739355 (ts3native 0.36.253, asi ad3e8ad4273f). The 11:19 PHT smoke FAIL was the unescaped brackets in the start-line regex (da739355 fixes it).

| job | result | KdUncutCeilMs / ceiling | kdUncutHit | longest (kdUncutMaxUs) |
|---|---|---|---|---|
| sv-kduncut-smoke (15:29 PHT, 6.9 min) | PASS | start line `KdUncutCeilMs=30000`, switch off/on read back | 0 (kdUncutOk 0) | 0 |
| sv-kd-uncut-ceiling (15:35 PHT, 11.9 min, ceiling forced to 0.3 s) | PASS | `kduncutceilus 300000` | 8 (kdUncutOk 7) | 573,899 us |

- Cut jobs (8 `uncut ceiling hit` lines, all own START, IC kind 2, kd kind 2, 311-574 ms): scope **unseen** 8, household 0, selected 0. No household or selected Sim was cut in this run (at the 30 s default they could be: crash guard, all scopes).
- Seen-Sim jobs ran as `uncut (exempt)` with the 10 s deadline (e.g. GoHere plans), untouched.
- After the cut: the 6 hits with follow-up lines show `routefail job ... result 16, cut (KindDeadlines, mask 2)` (the 300 ms kind deadline fired at the same moment as the forced 0.3 s ceiling), then a `routefail retry ... after cut ... request changed, backoff level 1, succeeded` 5.2-7.5 s later for the same Sim. Tail past the cut 4-86 ms. 2 hits (253C6A48, 25D0B8D1) have no further line for that route. No busy-for-N-s line, no crash, 0 new script errors, every hit under 3 s.
- Limit: the forced 0.3 s ceiling overlaps the KindDeadlines 300 ms cut, so this run proves the ceiling path fires, logs and recovers, not that a 30 s ceiling shortens a real freeze. Timing invalid (noisy machine); correctness only.
