# Queue verify-shortcut-boat 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-shortcut-boat-2026-11-06.jsonl`.

## 2026-11-06T11:17 UTC, build 5c323275, jobs ip-uboat-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-uboat-verify | 11:17 (19:17) | 21.8 | **FAIL** | FAIL: profile 20 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: unseenboat_shortcuts=0 (want >=5)<br>ts3fixes activity: unseenboat_candidates 0->646, unseenboat_seen 1->2, unseenboat_rj_unknown 0->288, unseenboat_gate 1->598, unseenboat_unseen 0->596, unseenboat_rj_route 0->646, owchain_alarms 0->9, owspread_types_run 0->180, stuckcheckasync_queued 0->672, stuckcheckasync_run 0->672, et | 1 | 2106.7/1842.5/1628.9 -> 2151.9/1879/1558.9 | asyncStarted +11391, asyncRefused +228, asyncCollected +11368, asyncDropped +15, asyncUs +299621047, asyncMaxUs +551984, asyncPumpUs +58256281, asyncPumpWaits +20327, asyncRefusedM1Pending +72, asyncSkipped +6, asyncBackoffs +101, asyncQueueMax +11 | timing: invalid (job FAIL: FAIL: profile 20 min, seed 12345 (rng-compare.ts); FAIL: fixes counters: unseenboat_shortcuts=0 (want >=5); noise medium); build: asi 0da27e11a81e ts3native.asi.mono1106 ts3native 0.36.173 (log ok); ts3fixes tuning cefa968babf7 ts3fixes.package.boattp2 (expected game-copy/ts3fixes.package.boattp2); noise: medium (other apps 22.2% of game CPUs); others on game CPUs 22.2%; |

Stopped: ip-uboat-verify failed (stopQueueOnFail): FAIL: profile 20 min, seed 12345 (rng-compare.ts).

## 2026-11-06T11:40 UTC, build 8fb5886d, jobs ip-uboat-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-uboat-repro | 11:40 (19:40) | 15.3 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenboat_candidates 0->419, unseenboat_seen 1->8, unseenboat_rj_unknown 0->148, unseenboat_gate 1->395, unseenboat_unseen 0->387, unseenboat_rj_route 0->419, owchain_alarms 0->6, owspread_types_run 0->117, stuckcheckasync_queued 0->423, stuckcheckasync_run 0->423, etspread_passes 0->1, stuckcheck_noplan 0->311, stuckcheck_no | 0 | 2111.6/1843.6/1612.9 -> 2169.1/1885.6/1533.7 | asyncStarted +8698, asyncRefused +163, asyncCollected +8685, asyncDropped +9, asyncUs +205712031, asyncMaxUs +614398, asyncPumpUs +43710421, asyncPumpWaits +19678, asyncRefusedM1Pending +53, asyncSkipped +4, asyncBackoffs +53, asyncQueueMax +10 | timing: invalid (noise medium); build: asi 0da27e11a81e ts3native.asi.mono1106 ts3native 0.36.173 (log ok); ts3fixes tuning cefa968babf7 ts3fixes.package.boattp2 (expected game-copy/ts3fixes.package.boattp2); noise: medium (other apps 20.7% of game CPUs); others on game CPUs 20.7%; clock avg/min 4181/4142 MHz; Forced repro ip-unseen-boat-loop: spawned townies cross the sea, none stranded. Read uns |

## 2026-11-06T12:57 UTC, build 585c3f25, jobs ip-uboat-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-uboat-repro | 12:57 (20:57) | 15.3 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: unseenboat_shortcuts 0->5, unseenboat_rj_goal 0->1, unseenboat_candidates 0->451, unseenboat_seen 1->16, unseenboat_rj_unknown 0->187, unseenboat_released_seen 0->4, unseenboat_max_hold_min 0->90, unseenboat_gate 1->473, unseenboat_rj_sim 0->7, unseenboat_no_spot 0->1, unseenboat_unseen 0->457, unseenboat_rj_route 0->433, owch | 0 | 2114.7/1848/1628.9 -> 2175.2/1885.1/1550.3 | asyncStarted +8456, asyncRefused +164, asyncCollected +8432, asyncDropped +17, asyncUs +201685792, asyncMaxUs +656556, asyncPumpUs +43801708, asyncPumpWaits +14183, asyncRefusedM1Pending +72, asyncSkipped +6, asyncBackoffs +60, asyncQueueMax +16 | timing: invalid (noise medium); build: asi 0da27e11a81e ts3native.asi.mono1106 ts3native 0.36.173 (log ok); ts3fixes tuning cefa968babf7 ts3fixes.package.boattp2/ts3fixes.package.boattp3 (expected game-copy/ts3fixes.package.boattp3); noise: medium (other apps 18.2% of game CPUs); others on game CPUs 18.2%; clock avg/min 4210/4166 MHz; Forced repro ip-unseen-boat-loop: spawned townies cross the sea |

## 2026-11-06T13:13 UTC, build 9d916513, jobs ip-uboat-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-uboat-verify | 13:13 (21:13) | 21.8 | **FAIL** | FAIL: profile 20 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: unseenboat_shortcuts=3 (want >=5)<br>ts3fixes activity: unseenboat_shortcuts 0->3, unseenboat_rj_goal 0->1, unseenboat_candidates 0->572, unseenboat_seen 1->6, unseenboat_rj_unknown 0->223, unseenboat_released_seen 0->9, unseenboat_max_hold_min 0->63, unseenboat_gate 1->591, unseenboat_rj_sim 0->9, unseenboat_unseen 0->585, | 1 | 2109.3/1844.1/1628.9 -> 2143.8/1873.5/1569.6 | asyncStarted +13610, asyncRefused +219, asyncCollected +13591, asyncDropped +13, asyncUs +300220393, asyncMaxUs +461877, asyncPumpUs +59529160, asyncPumpWaits +20788, asyncRefusedM1Pending +86, asyncSkipped +6, asyncBackoffs +92, asyncQueueMax +12 | timing: invalid (job FAIL: FAIL: profile 20 min, seed 12345 (rng-compare.ts); FAIL: fixes counters: unseenboat_shortcuts=3 (want >=5); noise medium); build: asi 0da27e11a81e ts3native.asi.mono1106 ts3native 0.36.173 (log ok); ts3fixes tuning cefa968babf7 ts3fixes.package.boattp2/ts3fixes.package.boattp3 (expected game-copy/ts3fixes.package.boattp3); noise: medium (other apps 12.6% of game CPUs); o |

Stopped: ip-uboat-verify failed (stopQueueOnFail): FAIL: profile 20 min, seed 12345 (rng-compare.ts).
