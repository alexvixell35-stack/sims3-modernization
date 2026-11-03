# Queue night-1024-new 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-new-2026-10-24.jsonl`.

## 2026-10-24T15:12 UTC, build 287c1bff, jobs sv-owprobeplans-ab, bp-owstucknotice-summary, bp-preplancb-order, sv-deferfp2-alone-ab, sv-crowd-t1-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sv-owprobeplans-ab | 15:12 (23:12) | 2.3 | **FAIL** | FAIL: profile 15 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step crowd_sea_lot failed: no lot with sea inside its area in this world (community or residential)<br>mono ledger INCOMPLETE: per-item numbers below the table | 1 | 2141.1/1799.1/1548.9 -> 2124.8/1812.1/1548.9 | - | timing: invalid (job VOID: runInvalid: profiler cut short (37 of 900 s); crash or abort (no "done; game alive"); noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 33.2% of game CPUs); others on game CPUs 33.2%; clock avg/min 4172/4145 MHz; owprobeplans (docs/163 §8 |
| bp-owstucknotice-summary | 15:14 (23:14) | 9.7 | PASS | PASS: profile 8 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2182.5/1848.8/1500.9 -> 2228.5/2057.4/1307.4 | - | timing: invalid (noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 24.1% of game CPUs); others on game CPUs 24.1%; clock avg/min 4180/4156 MHz; overwatchstucknotice (docs/145 §13): one summary notice instead of per-Sim Overwatch stuck notices. Pass: owstucknotice_h |
| bp-preplancb-order | 15:24 (23:24) | 13.7 | PASS | PASS: profile 12 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2184.9/1852/1500.9 -> 2264.9/2086.5/1259.4 | - | timing: invalid (noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 22.3% of game CPUs); others on game CPUs 22.3%; clock avg/min 4197/4163 MHz; preplancb (docs/185 §5): AboutToPlanCallback before the async START, locked-door list kept. Arms: A routeasync 0 (vanilla |
| sv-deferfp2-alone-ab | 15:38 (23:38) | 14.7 | PASS | PASS: profile stopped at 8 events after 11.5 min (off 4, on 4; 8 firings, 2 cycles; rule 4 events per arm), cap 32 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2142/1791.9/1564.9 -> 2257.6/1994.6/1355.8 | - | timing: invalid (noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 12.5% of game CPUs); others on game CPUs 12.5%; clock avg/min 4230/4132 MHz; DeferFootprint2 alone (docs/89 §11): fixed-window ABBA, 4 firings per arm, timing A/B (timing verdict per docs/168 §10).  |
| sv-crowd-t1-ab | 15:53 (23:53) | 17.8 | **FAIL** | PASS: profile stopped at 20 events after 14.8 min (off 10, on 10; 20 firings, 5 cycles; rule 10 events per arm, >= 10 firings per arm), cap 47 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>FAIL: events not fired: fbjdSkip_0=0 (want >=1) | 0 | 2153.1/1808.8/1548.9 -> 2277.1/2018.3/1312.9 | asyncStarted +16757, asyncRefused +257, asyncCollected +16732, asyncDropped +5, asyncUs +148229650, asyncMaxUs +1110914, asyncPumpUs +95977202, asyncPumpWaits +15614, asyncRefusedM1Pending +57, asyncSkipped +16, asyncBackoffs +15, asyncQueueFull +1, asyncQueueMax +32 | timing: invalid (job FAIL: FAIL: events not fired: fbjdSkip_0=0 (want >=1); noise medium); build: asi 53d1ea75f030 ts3native.asi.0338 ts3native 0.36.33 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.9% of game CPUs); others on game CPUs 8.9%; clock avg/min 4226/4167 MHz; Forced repro A/B: sv-crowd-foreign-loop ABBA until 10 firings p |

### sv-owprobeplans-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 15 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-owstucknotice-summary: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 8 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### bp-preplancb-order: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-deferfp2-alone-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 32 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

### sv-crowd-t1-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 47 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
