# Queue untested-v020-smoke 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/untested-v020-smoke-2026-10-25.jsonl`.

## 2026-10-25T18:31 UTC, build 1dcc36dc, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 18:31 (02:31) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->379, stuckcheckasync_run 0->379, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->49, stuckcheck_notstuck 0->169, stuckcheck_rearmed 0->131, stuckcheck_reset 0->30<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: r | 0 | 2193.2/1851.2/1500.9 -> 2191.7/1870.6/1483.3 | asyncStarted +6999, asyncRefused +120, asyncCollected +6987, asyncDropped +12, asyncUs +77075207, asyncMaxUs +302342, asyncPumpUs +51569853, asyncPumpWaits +5646, asyncRefusedM1Pending +30, asyncBackoffs +4, asyncQueueMax +7 | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.8% of game CPUs); others on game CPUs 7.8%; clock avg/min 4220/4163 MHz; Measurement smoke: IP 10 min on the kinddl asi with job 7's switch set and light tools (checks the per-arm-free natural measurement |

### smoke: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
