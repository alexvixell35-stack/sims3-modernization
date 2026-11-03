# Queue crashn-rerun 2026-10-25

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/crashn-rerun-2026-10-25.jsonl`.

## 2026-10-25T09:32 UTC, build 51fd0453, jobs ip-p6int-p6-ab-crashn

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-p6-ab-crashn | 09:32 (17:32) | 23.9 | PASS | PASS: profile stopped at 32 events after 21.0 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 19 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2222.7/2011.4/1423.1 -> 2373.6/2266.6/1110.6 | asyncStarted +22101, asyncRefused +175, asyncCollected +21787, asyncDropped +16, asyncUs +288377463, asyncMaxUs +591939, asyncPumpUs +195935186, asyncPumpWaits +21566, asyncRefusedM1Pending +160, asyncSkipped +297, asyncBackoffs +194, asyncQueueMax +32 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 75c4ad1afa6c not in DB ts3native 0.36.68 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 8.4% of game CPUs); others on game CPUs 8.4%; clock avg/min 4213/4163 MHz; Rerun of p6-integration rung 1 (ip-p6int-p6-ab) after crash N (docs/67 §16, 2026-10-25 04:49:26 UTC, s |

### ip-p6int-p6-ab-crashn: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)

## 2026-10-25T09:56 UTC, build 45e8d92e, jobs ip-p6int-p6-ab-crashn

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-p6int-p6-ab-crashn | 09:56 (17:56) | 23.8 | PASS | PASS: profile stopped at 32 events after 20.8 min (off 16, on 16; 32 firings, 8 cycles; rule 16 events per arm; 14 steps capped), cap 24 min, seed 12345 (rng-compare.ts)<br>mono ledger INCOMPLETE: per-item numbers below the table<br>PASS: repro events fired<br>PASS: no forbidden log lines | 0 | 2240.6/2012.2/1423.2 -> 2380.5/2128.7/1257.2 | asyncStarted +20714, asyncRefused +199, asyncCollected +20422, asyncDropped +7, asyncUs +278543230, asyncMaxUs +564945, asyncPumpUs +189186176, asyncPumpWaits +20734, asyncRefusedM1Pending +187, asyncSkipped +285, asyncBackoffs +151, asyncQueueMax +31 | timing: invalid (noise medium; heavy tool RouteSnap.audits); build: asi 75c4ad1afa6c not in DB ts3native 0.36.68 (log ok); ts3fixes tuning 58cdf1e3a38e not in DB (expected build/ts3fixes.package); noise: medium (other apps 10.8% of game CPUs); others on game CPUs 10.8%; clock avg/min 4197/4094 MHz; Rerun of p6-integration rung 1 (ip-p6int-p6-ab) after crash N (docs/67 §16, 2026-10-25 04:49:26 UTC, |

### ip-p6int-p6-ab-crashn: mono ledger probe (docs/51)

    mono ledger: on 0x0 (ledger OFF, ticks NO, scope NO, frames no, task no); 0 meta scopes over 24 min, 0 ms inside (wall), full/unmatched 0, records dropped 0
    I-121-5 RNG: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; caller chains 0, outside the two expected paths 0 (0 calls)
    I-121-7 Query_GetObjects: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -
    I-121-12 Query_GetObjectsInRange: in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 classes: -; not Sim: 0
    I-121-9 Slots_GetPositionOfSlotImpl (slot helper 0x76F0A0): in meta scoring 0 (0.0/min, -/scope), elsewhere 0; 0 distinct objects, 0 caller chains
    I-120-4 [0x11DC18C] at each Simulate: 0 ticks, 0 non-zero -> no data
    I-119-44 Sleep(0) resume ticks 0:0 1:0 2:0 3+:0; Sleep(1) 0:0 1:0 2:0 3+:0 -> no data (probe task did not run)
