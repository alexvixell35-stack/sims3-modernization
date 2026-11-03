# Queue verify-preplancb-order 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-preplancb-order-2026-10-25.jsonl`.

## 2026-10-25T01:39 UTC, build fe0f695c, jobs bp-preplancb-order-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-preplancb-order-verify | 01:39 (09:39) | 13.7 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>PASS: fixes counters preplancb_precalls>=1<br>FAIL: repro steps: sim_visit_lot.queued=5 (want >=6)<br>mono ledger INCOMPLETE: per-item numbers below the table | 1 | 2166.6/1835.9/1532.8 -> 2259.8/2058.7/1291.3 | - | timing: invalid (job FAIL: FAIL: profile 12 min, seed 12345 (rng-compare.ts); FAIL: repro steps: sim_visit_lot.queued=5 (want >=6); noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 18.0% of game CPUs); others on game CPUs 18.0%; clock avg/min 4188/4166 MHz; prepla |

### bp-preplancb-order-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T07:30 UTC, build 6cecb17f, jobs bp-preplancb-order-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-preplancb-order-verify | 07:30 (15:30) | 13.9 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>PASS: fixes counters preplancb_precalls>=1,preplancb_doors_restored>=1<br>FAIL: repro steps: sim_visit_lot.queued=3 (want >=4)<br>mono ledger INCOMPLETE: per-item numbers below the table | 1 | 2191.2/1847.3/1500.9 -> 2302.2/2115.3/1243.4 | - | timing: invalid (job FAIL: FAIL: profile 12 min, seed 12345 (rng-compare.ts); FAIL: repro steps: sim_visit_lot.queued=3 (want >=4); noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 10.4% of game CPUs); others on game CPUs 10.4%; clock avg/min 4205/4155 MHz; prepla |

### bp-preplancb-order-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T13:47 UTC, build 6a94a715, jobs bp-preplancb-order-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-preplancb-order-verify | 13:47 (21:47) | 13.8 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2193.3/1853/1532.8 -> 2377.2/2098.9/1259.6 | - | timing: invalid (noise medium; heavy tool CrashForensics.Guards); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 12.6% of game CPUs); others on game CPUs 12.6%; clock avg/min 4180/4125 MHz; preplancb (docs/185 §5, §6.1): AboutToPlanCallback before the async START, locked-door  |

### bp-preplancb-order-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
