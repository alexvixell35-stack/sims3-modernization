# Queue verify-userdiag 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-userdiag-2026-10-25.jsonl`.

## 2026-10-25T01:10 UTC, build fe0f695c, jobs bp-verify-userdiag

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-verify-userdiag | 01:10 (09:10) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2168.6/1853.2/1516.8 -> 2307.2/1964.8/1387.6 | - | timing: invalid (noise medium); build: asi a3014c9f4358 ts3native.asi.userdiag ts3native 0.36.53 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 17.0% of game CPUs); others on game CPUs 17.0%; clock avg/min 4208/4157 MHz; docs/188 [UserDiag] verify (correctness, logging only): one Bridgeport load + one agent save (repro userdiag-save, s |

### bp-verify-userdiag: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 5 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
