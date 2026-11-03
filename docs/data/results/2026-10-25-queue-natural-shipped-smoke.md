# Queue natural-shipped-smoke 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/natural-shipped-smoke-2026-10-25.jsonl`.

## 2026-10-25T14:39 UTC, build 2892a890, jobs smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| smoke | 14:39 (22:39) | 11.7 | PASS | PASS: profile 10 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->4, owspread_types_run 0->80, stuckcheckasync_queued 0->455, stuckcheckasync_run 0->455, etspread_passes 0->1<br>mono ledger INCOMPLETE: per-item numbers below the table | 0 | 2157.6/1837.1/1516.9 -> 2332.1/2091.8/1283.4 | asyncStarted +13448, asyncRefused +192, asyncCollected +13440, asyncDropped +3, asyncUs +74867515, asyncMaxUs +182259, asyncPumpUs +44263672, asyncPumpWaits +9256, asyncRefusedM1Pending +36, asyncSkipped +4, asyncBackoffs +16, asyncQueueFull +4, asyncQueueMax +7 | timing: invalid (noise medium); build: asi e04063deb364 not in DB ts3native 0.36.37a (log ok); ts3fixes tuning d5e68fbf8941 ts3fixes.v0.1.5 (expected game-copy/ts3fixes.package.rel016); noise: medium (other apps 10.5% of game CPUs); others on game CPUs 10.5%; clock avg/min 4180/4121 MHz; Measurement smoke: BP 10 min on the same shipped files and install path (fixesPackage), no owchain_alarms requi |

### smoke: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 10 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
