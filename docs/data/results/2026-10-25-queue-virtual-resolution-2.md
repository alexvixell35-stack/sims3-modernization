# Queue virtual-resolution-2 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/virtual-resolution-2-2026-10-25.jsonl`.

## 2026-10-25T03:42 UTC, build 0df75ce3, jobs bp-vres2-routing

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-vres2-routing | 03:42 (11:42) | 34.1 | PASS | PASS: rare-route-bits: profile 4 min, first window after load<br>PASS: rare-owned-vehicles: profile 6 min, reused session (#1 since load)<br>PASS: bp-door-lock: profile 3 min, first window after load<br>PASS: bp-upstairs-route: profile 3 min, first window after load<br>PASS: replan-buildbuy-crowd: profile 5 min, first window after load<br>PASS: stuck-check-3am: profile 5 min, first window after lo | 0 | 2199/1854/1516.9 -> 2339.4/2112.4/1229.1 | - | timing: invalid (rare-route-bits: noise medium; correctness-only tool ProbeSnap.CallSites; rare-owned-vehicles: noise medium; correctness-only tool ProbeSnap.CallSites; bp-door-lock: noise medium; correctness-only tool ProbeSnap.CallSites; bp-upstairs-route: noise medium; correctness-only tool ProbeSnap.CallSites; replan-buildbuy-crowd: noise medium; correctness-only tool ProbeSnap.CallSites; stuc |

### bp-vres2-routing: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 5 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
