# Queue kind-deadlines 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kind-deadlines-2026-10-25.jsonl`.

## 2026-10-25T16:49 UTC, build 1dcc36dc, jobs sv-kd-loop, sv-kd-nat-verify, ip-kd-nat-off, ip-kd-nat-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-kd-loop | 16:49 (00:49) | 4.6 | **FAIL** | PASS: profile stopped at 132 events after 1.5 min (off 66, on 66; 4 firings, 1 cycles; rule 50 events per arm; 1 steps capped), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->160, stuckcheckasync_run 0->160, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->17, stuckcheck_notstuck 0->95, stu | 0 | 2117.3/1801.8/1548.9 -> 2142.6/1963.8/1387.3 | asyncStarted +2208, asyncRefused +409, asyncCollected +2207, asyncDropped +1, asyncUs +18127944, asyncMaxUs +255939, asyncPumpUs +11184688, asyncPumpWaits +2058, asyncRefusedM1Pending +10, asyncBackoffs +3, asyncQueueFull +322, asyncQueueMax +7 | timing: invalid (job FAIL: FAIL: events not fired: kdCut_0=0 (want >=1); noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.7% of game CPUs); others on game CPUs 5.7%; clock avg/min 4228/4182 MHz; Correctness, forc |
| sv-kd-nat-verify | 16:54 (00:54) | 31.7 | PASS | PASS: profile 30 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->17, owspread_types_run 0->340, stuckcheckasync_queued 0->1741, stuckcheckasync_run 0->1737, etspread_passes 0->1, stuckcheckcap_marked 0->4698, stuckcheck_noplan 0->180, stuckcheck_notstuck 0->1038, stuckcheck_rearmed 0->475, stuckcheck_reset 0->47<br>mono ledger INCOMPLETE: per-item numbers below the table<b | 0 | 2144.5/1798.1/1548.9 -> 2269.5/2015.4/1355.4 | asyncStarted +25608, asyncRefused +486, asyncCollected +25581, asyncDropped +23, asyncUs +204236503, asyncMaxUs +1484079, asyncPumpUs +122191163, asyncPumpWaits +21469, asyncRefusedM1Pending +101, asyncSkipped +4, asyncBackoffs +17, asyncQueueMax +6 | timing: invalid (noise medium; heavy tool samplers); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.5% of game CPUs); others on game CPUs 6.5%; clock avg/min 4230/4148 MHz; Correctness, SV natural 30 min seeded 12345 from Baseline-SV-Spring with every job kind live: FallbackAsync +  |
| ip-kd-nat-off | 17:26 (01:26) | 32.7 | PASS | PASS: profile 31 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->16, owspread_types_run 0->320, stuckcheckasync_queued 0->1526, stuckcheckasync_run 0->1526, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->153, stuckcheck_notstuck 0->612, stuckcheck_rearmed 0->598, stuckcheck_reset 0->163<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2148/1851.5/1500.9 -> 2211/1899/1467.4 | asyncStarted +16812, asyncRefused +327, asyncCollected +16786, asyncDropped +25, asyncUs +184825596, asyncMaxUs +366336, asyncPumpUs +129118598, asyncPumpWaits +13213, asyncRefusedM1Pending +74, asyncBackoffs +10, asyncQueueFull +3, asyncQueueMax +7 | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.5% of game CPUs); others on game CPUs 6.5%; clock avg/min 4239/4186 MHz; IP natural pair, off half: Isla Paradiso 31 min natural play (autopilot, camera untouched, no repro), kinddl asi with release defau |
| ip-kd-nat-on | 17:58 (01:58) | 32.8 | PASS | PASS: profile 31 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->15, owspread_types_run 0->300, stuckcheckasync_queued 0->1427, stuckcheckasync_run 0->1427, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->174, stuckcheck_notstuck 0->569, stuckcheck_rearmed 0->545, stuckcheck_reset 0->139<br>mono ledger INCOMPLETE: per-item numbers below the table<br>P | 0 | 2198/1858/1500.9 -> 2201.7/1891.7/1500.9 | asyncStarted +17774, asyncRefused +335, asyncCollected +17748, asyncDropped +26, asyncUs +189563492, asyncMaxUs +406346, asyncPumpUs +131959323, asyncPumpWaits +14367, asyncRefusedM1Pending +102, asyncBackoffs +11, asyncQueueFull +6, asyncQueueMax +7 | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.9% of game CPUs); others on game CPUs 6.9%; clock avg/min 4233/4167 MHz; IP natural pair, on half: as ip-kd-nat-off with KindDeadlines=1 (release defaults: only own Route.Plan jobs exist, cut at 250 ms; s |

### sv-kd-loop: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-kd-nat-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 30 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-kd-nat-off: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 31 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-kd-nat-on: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 31 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
