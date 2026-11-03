# Queue verify-preplancb-order 2026-10-24

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-preplancb-order-2026-10-24.jsonl`.

## 2026-10-24T22:17 UTC, build 5509c60a, jobs bp-preplancb-order-verify

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-preplancb-order-verify | 22:17 (06:17) | 13.7 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>PASS: fixes counters preplancb_precalls>=1<br>FAIL: repro steps: sim_visit_lot.queued=0 (want >=6)<br>mono ledger INCOMPLETE: per-item numbers below the table | 1 | 2171.5/1840.5/1516.9 -> 2291.8/2073.3/1307.4 | - | timing: invalid (job FAIL: FAIL: profile 12 min, seed 12345 (rng-compare.ts); FAIL: repro steps: sim_visit_lot.queued=0 (want >=6); noise medium); build: asi 8c0e67e2b1b6 ts3native.asi.p6f ts3native 0.36.43 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 21.0% of game CPUs); others on game CPUs 21.0%; clock avg/min 4184/4139 MHz; prepla |

### bp-preplancb-order-verify: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 12 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
