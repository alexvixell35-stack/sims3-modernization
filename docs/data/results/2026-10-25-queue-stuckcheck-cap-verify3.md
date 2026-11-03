# Queue stuckcheck-cap-verify3 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stuckcheck-cap-verify3-2026-10-25.jsonl`.

## 2026-10-25T07:58 UTC, build 302503a3, jobs sv-sccap3-nat-off, sv-sccap3-nat-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-sccap3-nat-off | 07:58 (15:58) | 47.1 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckcap_marked 0->0, stuckcheck_noplan 0->285, stuckcheck_notstuck 0->1473, stuckcheck_rearmed 0->650, stuckcheck_reset 0->52<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2122.1/1808.2/1548.9 -> 2272.9/2000.4/1371.4 | asyncStarted +30755, asyncRefused +841, asyncCollected +30718, asyncDropped +31, asyncUs +261460180, asyncMaxUs +1446822, asyncPumpUs +168911106, asyncPumpWaits +26327, asyncRefusedM1Pending +179, asyncSkipped +5, asyncBackoffs +19, asyncQueueFull +1, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 03cf4168be61 ts3native.asi.sccap2 ts3native 0.36.62 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.4% of game CPUs); others on game CPUs 8.4%; clock avg/min 4206/4107 MHz; SV natural run, stuckcheckown ON, stuckcheckcap OFF (45 min seeded 12345 from Baseline-SV-Spring),  |
| sv-sccap3-nat-on | 08:45 (16:45) | 46.9 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckcap_marked 0->6640, stuckcheck_noplan 0->262, stuckcheck_notstuck 0->1478, stuckcheck_rearmed 0->613, stuckcheck_reset 0->55<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2143.1/1803.6/1548.9 -> 2268.3/1999.4/1371.4 | asyncStarted +30164, asyncRefused +828, asyncCollected +30126, asyncDropped +30, asyncUs +261555243, asyncMaxUs +1734230, asyncPumpUs +169198096, asyncPumpWaits +25128, asyncRefusedM1Pending +148, asyncSkipped +8, asyncBackoffs +8, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 03cf4168be61 ts3native.asi.sccap2 ts3native 0.36.62 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 9.1% of game CPUs); others on game CPUs 9.1%; clock avg/min 4209/4150 MHz; Same as sv-sccap3-nat-off with stuckcheckcap ON. Expected: uncut waits >= 1 s behind own jobs only  |

### sv-sccap3-nat-off: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-sccap3-nat-on: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
