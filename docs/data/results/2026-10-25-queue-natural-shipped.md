# Queue natural-shipped 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/natural-shipped-2026-10-25.jsonl`.

## 2026-10-25T14:51 UTC, build 87e66371, jobs bp-shipped-31, ip-shipped-31

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-shipped-31 | 14:51 (22:51) | 32.9 | PASS | PASS: profile 31 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->12, owspread_types_run 0->240, stuckcheckasync_queued 0->1391, stuckcheckasync_run 0->1381, etspread_passes 0->1<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2159.9/1833.4/1516.9 -> 2328.6/2125.4/1227.4 | asyncStarted +41210, asyncRefused +667, asyncCollected +41192, asyncDropped +14, asyncUs +239288251, asyncMaxUs +208065, asyncPumpUs +143911925, asyncPumpWaits +25437, asyncRefusedM1Pending +138, asyncSkipped +1, asyncBackoffs +48, asyncQueueFull +11, asyncQueueMax +6 | timing: invalid (noise medium); build: asi e04063deb364 not in DB ts3native 0.36.37a (log ok); ts3fixes tuning d5e68fbf8941 ts3fixes.v0.1.5 (expected game-copy/ts3fixes.package.rel016); noise: medium (other apps 8.2% of game CPUs); others on game CPUs 8.2%; clock avg/min 4197/4144 MHz; Bridgeport, 31 min natural play (autopilot, camera untouched, no repro), shipped v0.1.6 asi + ts3fixes + release  |
| ip-shipped-31 | 15:24 (23:24) | 32.9 | PASS | PASS: profile 31 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->15, owspread_types_run 0->300, stuckcheckasync_queued 0->1414, stuckcheckasync_run 0->1405, etspread_passes 0->1<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2160.5/1833.5/1500.9 -> 2206.8/2015.3/1323.3 | asyncStarted +16729, asyncRefused +293, asyncCollected +16701, asyncDropped +27, asyncUs +181631030, asyncMaxUs +425777, asyncPumpUs +124437693, asyncPumpWaits +12956, asyncRefusedM1Pending +73, asyncSkipped +1, asyncBackoffs +15, asyncQueueMax +6 | timing: invalid (noise medium); build: asi e04063deb364 not in DB ts3native 0.36.37a (log ok); ts3fixes tuning d5e68fbf8941 ts3fixes.v0.1.5 (expected game-copy/ts3fixes.package.rel016); noise: medium (other apps 9.0% of game CPUs); others on game CPUs 9.0%; clock avg/min 4190/4131 MHz; Isla Paradiso, 31 min natural play (autopilot, camera untouched, no repro), shipped v0.1.6 asi + ts3fixes + relea |

### bp-shipped-31: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 31 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### ip-shipped-31: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 31 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
