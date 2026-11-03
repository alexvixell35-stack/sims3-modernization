# Queue plandeadline-verify2 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/plandeadline-verify2-2026-10-25.jsonl`.

## 2026-10-25T21:47 UTC, build e3c26b65, jobs sv-plandeadline-soak2, bp-plandeadline-lastround

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-plandeadline-soak2 | 21:47 (05:47) | 31.9 | PASS | PASS: profile 30 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->17, owspread_types_run 0->340, stuckcheckasync_queued 0->1754, stuckcheckasync_run 0->1752, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->185, stuckcheck_notstuck 0->1057, stuckcheck_rearmed 0->469, stuckcheck_reset 0->51<br>mono ledger INCOMPLETE: per-item numbers below the table<br>P | 0 | 2142.4/1807.3/1564.9 -> 2203.9/1999/1403.4 | asyncStarted +16091, asyncRefused +552, asyncCollected +16077, asyncDropped +12, asyncUs +155794579, asyncMaxUs +365966, asyncPumpUs +98706506, asyncPumpWaits +13267, asyncRefusedM1Pending +91, asyncSkipped +2, asyncBackoffs +9, asyncQueueMax +5 | timing: invalid (noise medium); build: asi d90ea828efd5 not in DB ts3native 0.36.78 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.3% of game CPUs); others on game CPUs 8.3%; clock avg/min 4217/4153 MHz; TIMING soak, SV 30 min natural play (autopilot, camera untouched, no repro), pd2 asi at release defaults plus PlanDeadlineMs only ( |
| bp-plandeadline-lastround | 22:19 (06:19) | 4.3 | PASS | PASS: profile stopped at 121 events after 1.3 min (off 57, on 64; 8 firings, 2 cycles; rule 40 events per arm, >= 2 firings per arm), cap 12 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->262, stuckcheckasync_run 0->262, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->51, stuckcheck_notstuck 0->5 | 0 | 2175.3/1855.6/1500.9 -> 2248.5/1935.9/1435.3 | - | timing: invalid (noise medium); build: asi d90ea828efd5 not in DB ts3native 0.36.78 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 12.3% of game CPUs); others on game CPUs 12.3%; clock avg/min 4192/4171 MHz; CORRECTNESS (noise OK). route-stuckcheck-own-loop (20 stranded Sims, the game's stuck check armed at attempt 3 = last round, stuc |

### sv-plandeadline-soak2: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 30 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-plandeadline-lastround: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
