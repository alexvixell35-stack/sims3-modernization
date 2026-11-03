# Queue kind-deadlines-smoke 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kind-deadlines-smoke-2026-10-25.jsonl`.

## 2026-10-25T16:37 UTC, build 1dcc36dc, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 16:37 (00:37) | 11.6 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->5, owspread_types_run 0->100, stuckcheckasync_queued 0->457, stuckcheckasync_run 0->457, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->36, stuckcheck_notstuck 0->179, stuckcheck_rearmed 0->193, stuckcheck_reset 0->49<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS:  | 0 | 2171.4/1852.2/1500.9 -> 2187.2/1897.9/1467.4 | asyncStarted +5865, asyncRefused +129, asyncCollected +5860, asyncDropped +5, asyncUs +63264955, asyncMaxUs +286541, asyncPumpUs +45769832, asyncPumpWaits +4406, asyncRefusedM1Pending +29, asyncBackoffs +3, asyncQueueFull +2, asyncQueueMax +7 | timing: invalid (noise medium); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 5.5% of game CPUs); others on game CPUs 5.5%; clock avg/min 4224/4185 MHz; Measurement smoke: IP 10 min on the kinddl asi with KindDeadlines=1 and the pair's light tools (checks the measurement path and tha |

### smoke: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
