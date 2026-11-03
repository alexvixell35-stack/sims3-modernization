# Queue p6fix-ab-rerun 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/p6fix-ab-rerun-2026-10-25.jsonl`.

## 2026-10-25T07:06 UTC, build 7e73a5ae, jobs ip-p6fix-ab2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6fix-ab2 | 07:06 (15:06) | 23.9 | PASS | PASS: profile loop window end reached: 30 events after 20.9 min, rule not met (off 15, on 15; 31 firings, 7 cycles; rule 16 events per arm; 26 steps capped), cap 22 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2251/2023.6/1413.2 -> 2377.4/2121.6/1249.4 | asyncStarted +13108, asyncRefused +2353, asyncCollected +12785, asyncDropped +15, asyncUs +154312577, asyncMaxUs +493767, asyncPumpUs +93413221, asyncPumpWaits +16304, asyncRefusedM1Pending +131, asyncSkipped +308, asyncBackoffs +106, asyncQueueFull +2108, asyncQueueMax +8 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 308b6d2d92f6 ts3native.asi.p6int/ts3native.asi.p6fixlog ts3native 0.36.65 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 15.0% of game CPUs); others on game CPUs 15.0%; clock avg/min 4182/4133 MHz; Rerun of p6fix-verify job 3 (ip-p6fix-ab) after crash M (docs/67 §1 |

### ip-p6fix-ab2: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 22 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
