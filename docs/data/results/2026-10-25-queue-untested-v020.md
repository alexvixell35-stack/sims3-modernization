# Queue untested-v020 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/untested-v020-2026-10-25.jsonl`.

## 2026-10-25T18:43 UTC, build 1dcc36dc, jobs bp-stuckchecknear-smoke, sv-overwatchstuckslice-verify, ip-drivertask-boat-verify, sv-private-fp-loop, bp-snap2-cancel, sv-plandeadline-soak, ip-v020-routing-31

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-stuckchecknear-smoke | 18:43 (02:43) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->228, stuckcheckasync_run 0->228, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->71, stuckcheck_notstuck 0->89, stuckcheck_rearmed 0->62, stuckcheck_reset 0->6<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: rep | 0 | 2198.5/1839.3/1548.8 -> 2378.8/2106.1/1238.2 | asyncStarted +13604, asyncRefused +255, asyncCollected +13603, asyncDropped +1, asyncUs +69923185, asyncMaxUs +191730, asyncPumpUs +40662040, asyncPumpWaits +8588, asyncRefusedM1Pending +32, asyncBackoffs +18, asyncQueueFull +6, asyncQueueMax +5 | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.4% of game CPUs); others on game CPUs 6.4%; clock avg/min 4252/4199 MHz; Correctness smoke of ts3fixes stuckchecknear (docs/163 §3, counters 238-240, built 2026-10-22, never run), copied from nostart-fix- |
| sv-overwatchstuckslice-verify | 18:55 (02:55) | 3.7 | **FAIL** | FAIL: profile stopped at 8 events after 0.6 min (off 4, on 4; 8 firings, 2 cycles; rule 3 events per arm), cap 14 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: owstuck_slices=0 (want >=1)<br>ts3fixes activity: owchain_alarms 0->8, owspread_types_run 0->160, stuckcheckasync_queued 0->0, stuckcheckasync_run 0->0, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->0, st | 1 | 2105/1794.5/1564.9 -> 2108.7/1832.2/1531.3 | asyncStarted +126, asyncRefused +3, asyncCollected +126, asyncUs +2593357, asyncMaxUs +152987, asyncPumpUs +743019, asyncPumpWaits +92, asyncQueueMax +1 | timing: invalid (job FAIL: FAIL: profile stopped at 8 events after 0.6 min (off 4, on 4; 8 firings, 2 cycles; rule 3 events per arm), cap 14 min, s; noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.3% of game CPUs); others on game CPUs 7.3%; clock avg/min 4241/4162 MHz; |
| ip-drivertask-boat-verify | 18:59 (02:59) | 11 | PASS | PASS: profile stopped at 4 events after 8.2 min (off 2, on 2; 4 firings, 1 cycles; rule 2 events per arm), cap 13 min, seed 12345 (rng-compare.ts)<br>note: self check: [Discourage] DeferFootprint on but usfDeferred rose 0: feature idle (forced repro: not the job subject)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->90, stuckcheckasync_run 0->90, et | 0 | 2194.5/1871.2/1500.9 -> 2211/1865.8/1451.6 | asyncStarted +229, asyncRefused +5, asyncCollected +229, asyncUs +3245583, asyncMaxUs +70291, asyncPumpUs +868875, asyncPumpWaits +88, asyncRefusedM1Pending +2 | timing: invalid (noise medium; A/B 2 off / 2 on iterations, < 4 per arm (repro firings)); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.0% of game CPUs); others on game CPUs 7.0%; clock avg/min 4234/4162 MHz; Rerun of night-1024-verify ip-drivertask-boat-verify (drivertaskasync rid |
| sv-private-fp-loop | 19:10 (03:10) | 14.6 | PASS | PASS: profile stopped at 8 events after 11.5 min (off 4, on 4; 8 firings, 2 cycles; rule 4 events per arm), cap 24 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->6, owspread_types_run 0->120, stuckcheckasync_queued 0->884, stuckcheckasync_run 0->882, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->124, stuckcheck_notstuck 0->513, stuckcheck_rearmed 0 | 0 | 2140.4/1801.2/1548.9 -> 2291.4/2030.4/1323.8 | - | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.3% of game CPUs); others on game CPUs 6.3%; clock avg/min 4249/4209 MHz; First run of T7 [Discourage] DeferPrivateFp (docs/89 §14, counters v113 pf*, never run). Repro sv-private-fp-loop: 30 spawned Sims  |
| bp-snap2-cancel | 19:24 (03:24) | 4.7 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step pet_guard: pet_guard until [{"path":"area","equals":true}] not met in 120 s: {"area":false,"guarding":false,"pet":"Roxy Dog"}<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->121, stuckcheckasync_run 0->121, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->35, st | 1 | 2162.3/1842.6/1516.9 -> 2268.1/2040.8/1325.1 | asyncStarted +5106, asyncRefused +13, asyncCollected +5100, asyncDropped +2, asyncUs +25307292, asyncMaxUs +146876, asyncPumpUs +15528432, asyncPumpWaits +3420, asyncRefusedM1Pending +13, asyncSkipped +1, asyncBackoffs +5, asyncQueueMax +3 | timing: invalid (job FAIL: FAIL: profile 10 min, seed 12345 (rng-compare.ts); FAIL: repro step pet_guard: pet_guard until [{"path":"area","equals":; noise medium; heavy tool ProbeSnap); build: asi ed59c43e15c0 ts3native.asi.snapprobe2 ts3native 0.24.0 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.4% of game CPUs); others on game CPU |
| sv-plandeadline-soak | 19:29 (03:29) | 46.8 | **FAIL** | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->25, owspread_types_run 0->500, stuckcheckasync_queued 0->2500, stuckcheckasync_run 0->2500, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->265, stuckcheck_notstuck 0->1496, stuckcheck_rearmed 0->672, stuckcheck_reset 0->67<br>mono ledger INCOMPLETE: per-item numbers below the table<br>F | 0 | 2124/1803.3/1548.9 -> 2255.5/2030.3/1337.8 | asyncStarted +23892, asyncRefused +902, asyncCollected +23831, asyncDropped +51, asyncUs +230507845, asyncMaxUs +1732475, asyncPumpUs +147680405, asyncPumpWaits +19948, asyncRefusedM1Pending +160, asyncSkipped +10, asyncBackoffs +16, asyncQueueMax +6 | timing: invalid (job FAIL: FAIL: events not fired: pdHitOk=1 (want <=0); noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.0% of game CPUs); others on game CPUs 7.0%; clock avg/min 4229/4069 MHz; TIMING soak of [RouteAsync] PlanDeadlineMs=300 (docs/164 §7; bp-plandeadlin |
| ip-v020-routing-31 | 20:16 (04:16) | 32.8 | **FAIL** | PASS: profile 31 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->16, owspread_types_run 0->320, stuckcheckasync_queued 0->1418, stuckcheckasync_run 0->1418, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->131, stuckcheck_notstuck 0->614, stuckcheck_rearmed 0->529, stuckcheck_reset 0->144<br>mono ledger INCOMPLETE: per-item numbers below the table<br>F | 0 | 2161.8/1850.2/1500.9 -> 2205.1/1900.8/1467.3 | asyncStarted +21625, asyncRefused +264, asyncCollected +21601, asyncDropped +24, asyncUs +222615587, asyncMaxUs +352353, asyncPumpUs +146465852, asyncPumpWaits +16862, asyncRefusedM1Pending +73, asyncBackoffs +10, asyncQueueMax +6 | timing: invalid (job FAIL: FAIL: events not fired: pdHitOk=3 (want <=0); noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 6.4% of game CPUs); others on game CPUs 6.4%; clock avg/min 4247/4192 MHz; TIMING, combined v0.2.0 routing set, IP 31 min natural play (autopilot, cam |

### bp-stuckchecknear-smoke: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-overwatchstuckslice-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 14 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-drivertask-boat-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 13 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-private-fp-loop: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-plandeadline-soak: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-v020-routing-31: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 31 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T22:24 UTC, build e29604b9, jobs bp-snap2-cancel

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-snap2-cancel | 22:24 (06:24) | 4.7 | **FAIL** | FAIL: profile 10 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step pet_guard: pet_guard until [{"path":"area","equals":true}] not met in 120 s: {"area":false,"guarding":false,"pet":"Roxy Dog"}<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->120, stuckcheckasync_run 0->120, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->25, st | 1 | 2157.7/1821.9/1532.9 -> 2292.6/1941.5/1421.1 | asyncStarted +5062, asyncRefused +10, asyncCollected +5061, asyncUs +23197831, asyncMaxUs +127806, asyncPumpUs +14377555, asyncPumpWaits +3397, asyncRefusedM1Pending +10, asyncSkipped +1, asyncBackoffs +7, asyncQueueMax +4 | timing: invalid (job FAIL: FAIL: profile 10 min, seed 12345 (rng-compare.ts); FAIL: repro step pet_guard: pet_guard until [{"path":"area","equals":; noise medium; heavy tool ProbeSnap); build: asi ed59c43e15c0 ts3native.asi.snapprobe2 ts3native 0.24.0 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.1% of game CPUs); others on game CPU |
