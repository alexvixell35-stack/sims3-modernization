# Queue stuckcheck-cap-verify 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/stuckcheck-cap-verify-2026-10-24.jsonl`.

## 2026-10-24T22:31 UTC, build 97155748, jobs bp-stuckcheckcap-loop, sv-sccap-nat-off, sv-sccap-nat-on

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-stuckcheckcap-loop | 22:31 (06:31) | 4.3 | PASS | PASS: profile stopped at 145 events after 1.3 min (off 73, on 72; 8 firings, 2 cycles; rule 40 events per arm, >= 4 firings per arm), cap 22 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: stuckcheckasync_queued 0->428, stuckcheckasync_run 0->418<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2173.8/1859.9/1516.9 -> 2225.6/1928.8/1467.3 | asyncStarted +6044, asyncRefused +76, asyncCollected +6040, asyncDropped +1, asyncUs +29168724, asyncMaxUs +210464, asyncPumpUs +13585946, asyncPumpWaits +4886, asyncRefusedM1Pending +20, asyncSkipped +3, asyncBackoffs +8, asyncQueueFull +14, asyncQueueMax +6 | timing: invalid (noise medium); build: asi 636929185593 ts3native.asi.sccap ts3native 0.36.52 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 18.5% of game CPUs); others on game CPUs 18.5%; clock avg/min 4200/4178 MHz; Forced ABBA on route-stuckcheck-cap-loop (stranded crowd, check armed at attempt 1, stuckcheckasync + stuckcheckown on  |
| sv-sccap-nat-off | 22:36 (06:36) | 46.9 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2137.7/1775/1564.9 -> 2225.9/1995.5/1355.3 | asyncStarted +29624, asyncRefused +874, asyncCollected +29578, asyncDropped +39, asyncUs +273059346, asyncMaxUs +9025072, asyncPumpUs +183082709, asyncPumpWaits +23212, asyncRefusedM1Pending +191, asyncSkipped +7, asyncBackoffs +14, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 636929185593 ts3native.asi.sccap ts3native 0.36.52 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 14.8% of game CPUs); others on game CPUs 14.8%; clock avg/min 4202/4125 MHz; SV natural run, stuckcheckown ON, stuckcheckcap OFF (45 min seeded 12345 from Baseline-SV-Spring,  |
| sv-sccap-nat-on | 23:23 (07:23) | 46.9 | PASS | PASS: profile 45 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2138.9/1790.3/1564.9 -> 2211.7/2000.3/1371.4 | asyncStarted +29638, asyncRefused +903, asyncCollected +29589, asyncDropped +40, asyncUs +256218806, asyncMaxUs +2350847, asyncPumpUs +167812283, asyncPumpWaits +23330, asyncRefusedM1Pending +156, asyncSkipped +9, asyncBackoffs +13, asyncQueueMax +5 | timing: invalid (noise medium; heavy tool samplers); build: asi 636929185593 ts3native.asi.sccap ts3native 0.36.52 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 22.7% of game CPUs); others on game CPUs 22.7%; clock avg/min 4189/4066 MHz; Same as sv-sccap-nat-off with stuckcheckcap ON. Expected: stuckcheckcap_marked rises at each 3 AM, |

### bp-stuckcheckcap-loop: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 22 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-sccap-nat-off: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-sccap-nat-on: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 45 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
