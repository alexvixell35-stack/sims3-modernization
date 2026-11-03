# Queue night-1024-owprobe-ip 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/night-1024-owprobe-ip-2026-10-24.jsonl`.

## 2026-10-24T17:18 UTC, build b4e83692, jobs ip-owprobeplans-ab

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-owprobeplans-ab | 17:18 (01:18) | 3.8 | **FAIL** | FAIL: profile 15 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step stuck_check_fire failed: no spawn_sims Sim off the active household's lot on Pretty Port (strand_crowd or crowd_sea_lot first)<br>mono ledger INCOMPLETE: per-item numbers below the table | 1 | 2185.2/1843/1516.8 -> 2157.1/1860.3/1516.8 | - | timing: invalid (job VOID: runInvalid: profiler cut short (136 of 900 s); crash or abort (no "done; game alive"); noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning d6c5e8711bdb not in DB (expected build/ts3fixes.package); noise: medium (other apps 10.5% of game CPUs); others on game CPUs 10.5%; clock avg/min 4233/4200 MHz; Rerun of sv-owprobeplans |

### ip-owprobeplans-ab: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 15 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
