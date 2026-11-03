# Queue owstuckslice-verify2 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/owstuckslice-verify2-2026-10-25.jsonl`.

## 2026-10-25T21:00 UTC, build 0e32d7a8, jobs sv-owstuckslice-soak-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-owstuckslice-soak-verify | 21:00 (05:00) | 47 | **FAIL** | FAIL: profile 45 min, seed 12345 (rng-compare.ts)<br>FAIL: fixes counters: owstuck_slices=0 (want >=1)<br>ts3fixes activity: owchain_alarms 0->25, owspread_types_run 0->500, stuckcheckasync_queued 0->2412, stuckcheckasync_run 0->2409, etspread_passes 0->1, stuckcheckcap_marked 0->0, stuckcheck_noplan 0->276, stuckcheck_notstuck 0->1474, stuckcheck_rearmed 0->616, stuckcheck_reset 0->49<br>mono led | 1 | 2142.1/1791.7/1548.9 -> 2251.2/1994.2/1371.4 | asyncStarted +23030, asyncRefused +709, asyncCollected +23003, asyncDropped +24, asyncUs +208541533, asyncMaxUs +565512, asyncPumpUs +133199170, asyncPumpWaits +19606, asyncRefusedM1Pending +90, asyncSkipped +3, asyncBackoffs +12, asyncQueueMax +6 | timing: invalid (job FAIL: FAIL: profile 45 min, seed 12345 (rng-compare.ts); FAIL: fixes counters: owstuck_slices=0 (want >=1); noise medium; heavy tool samplers); build: asi 6cf20ff33272 not in DB ts3native 0.36.77 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.9% of game CPUs); others on game CPUs 8.9%; clock avg/min 4221/4158 MHz |

### sv-owstuckslice-soak-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
