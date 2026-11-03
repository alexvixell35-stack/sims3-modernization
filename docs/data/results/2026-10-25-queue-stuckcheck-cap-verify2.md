# Queue stuckcheck-cap-verify2 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stuckcheck-cap-verify2-2026-10-25.jsonl`.

## 2026-10-25T02:08 UTC, build ebad84a9, jobs sv-sccap2-nat-off, sv-sccap2-nat-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-sccap2-nat-off | 02:08 (10:08) | 46.9 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2142/1804.1/1548.9 -> 2269/2005.8/1339.8 | asyncStarted +30755, asyncRefused +884, asyncCollected +30720, asyncDropped +32, asyncUs +259930106, asyncMaxUs +1968957, asyncPumpUs +166893607, asyncPumpWaits +25395, asyncRefusedM1Pending +139, asyncSkipped +3, asyncBackoffs +10, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 03cf4168be61 ts3native.asi.sccap2 ts3native 0.36.62 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 12.4% of game CPUs); others on game CPUs 12.4%; clock avg/min 4213/4140 MHz; SV natural run, stuckcheckown ON, stuckcheckcap OFF (45 min seeded 12345 from Baseline-SV-Spring) |
| sv-sccap2-nat-on | 02:55 (10:55) | 46.9 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2146.4/1808.2/1548.9 -> 2242.4/2021.6/1339.4 | asyncStarted +30869, asyncRefused +799, asyncCollected +30846, asyncDropped +21, asyncUs +247876874, asyncMaxUs +410121, asyncPumpUs +155055618, asyncPumpWaits +23566, asyncRefusedM1Pending +169, asyncSkipped +2, asyncBackoffs +9, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 03cf4168be61 ts3native.asi.sccap2 ts3native 0.36.62 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 13.4% of game CPUs); others on game CPUs 13.4%; clock avg/min 4213/4154 MHz; Same as sv-sccap2-nat-off with stuckcheckcap ON. Expected: uncut waits >= 1 s behind own jobs onl |

### sv-sccap2-nat-off: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-sccap2-nat-on: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
