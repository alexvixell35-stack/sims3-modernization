# Queue goal-household-exempt 2026-11-07

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/goal-household-exempt-2026-11-07.jsonl`.

## 2026-11-07T04:15 UTC, build b113f7e5, jobs sv-goal-household-exempt

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-goal-household-exempt | 04:15 (12:15) | 4 | **FAIL** | FAIL: profile 14 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_route_to failed: Regan Redmond is not currently in the world (not instantiated)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->26, stuckcheckasync_run 0->26, etspread_passes 0->1, stuckcheck_noplan 0->21, stuckcheck_notstuck 0->4, stuckcheck_rearmed 0->1<br>PASS: repro events f | 1 | 2108.1/1794.2/1654.9 -> 2125.1/1834.1/1605.9 | asyncStarted +987, asyncRefused +32, asyncCollected +987, asyncUs +24299506, asyncMaxUs +372307, asyncPumpUs +2799609, asyncPumpWaits +2701, asyncRefusedM1Pending +2, asyncBackoffs +3, asyncQueueMax +6 | timing: invalid (job VOID: runInvalid: profiler cut short (110 of 840 s); noisy machine (others 23.1% of game CPUs); crash or abort (no "done; game; noise high; A/B 3 off / 4 on iterations, < 4 per arm (repro firings)); build: asi 82390fbae451 not in DB ts3native 0.36.186 (log ok); ts3fixes tuning 8a7002f70c55 not in DB (expected build/ts3fixes.package); noise: high (other apps 35.5% of game CPUs) |

Stopped: sv-goal-household-exempt failed (stopQueueOnFail): FAIL: profile 14 min, seed 12345 (rng-compare.ts).

## 2026-11-07T04:20 UTC, build 03a67701, jobs sv-goal-household-exempt

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-goal-household-exempt | 04:20 (12:20) | 4 | **FAIL** | FAIL: profile 14 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step sim_route_to failed: Bradford Malcolm is not currently in the world (not instantiated)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->18, stuckcheckasync_run 0->18, etspread_passes 0->1, stuckcheck_noplan 0->14, stuckcheck_notstuck 0->4<br>PASS: repro events fired | 1 | 2097/1774.4/1644.9 -> 2104.1/1808.9/1611.3 | asyncStarted +931, asyncRefused +36, asyncCollected +931, asyncUs +27187856, asyncMaxUs +346942, asyncPumpUs +3038781, asyncPumpWaits +2738, asyncRefusedM1Pending +2, asyncBackoffs +6, asyncQueueMax +7 | timing: invalid (job VOID: runInvalid: profiler cut short (117 of 840 s); noisy machine (other CPU 10.8% >= 10%); noise high); build: asi 82390fbae451 not in DB ts3native 0.36.186 (log ok); ts3fixes tuning 8a7002f70c55 not in DB (expected build/ts3fixes.package); noise: high (other apps 38.5% of game CPUs, NOT comparable (load or clock)); others on game CPUs 38.5%; clock avg/min 3979/3798 MHz; Cor |

Stopped: sv-goal-household-exempt failed (stopQueueOnFail): FAIL: profile 14 min, seed 12345 (rng-compare.ts).

## 2026-11-07T04:24 UTC, build 11b9859d, jobs sv-goal-household-exempt

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-goal-household-exempt | 04:24 (12:24) | 5.9 | PASS | PASS: profile stopped at 20 events after 2.4 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->2, owspread_types_run 0->40, stuckcheckasync_queued 0->150, stuckcheckasync_run 0->150, etspread_passes 0->1, stuckcheck_noplan 0->138, stuckcheck_notstuck 0->10, stuckcheck_rearmed 0->2<br>PASS: repro events | 0 | 2099.8/1795.2/1642.9 -> 2123.8/1819.2/1609.2 | asyncStarted +2519, asyncRefused +74, asyncCollected +2517, asyncUs +68832769, asyncMaxUs +552790, asyncPumpUs +8254255, asyncPumpWaits +7403, asyncRefusedM1Pending +8, asyncBackoffs +30, asyncQueueMax +7 | timing: invalid (noise high); build: asi 82390fbae451 not in DB ts3native 0.36.186 (log ok); ts3fixes tuning 8a7002f70c55 not in DB (expected build/ts3fixes.package); noise: high (other apps 20.4% of game CPUs); others on game CPUs 20.4%; clock avg/min 4121/4001 MHz; Correctness, forced ABBA on goal-household-exempt: unseen townie routed to the household lot, the loop flips goalhhexempt. Pass: no  |
