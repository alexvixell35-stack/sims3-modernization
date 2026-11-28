# Queue kd-uncut-ceiling-1115b 2026-11-15

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/kd-uncut-ceiling-1115b-2026-11-15.jsonl`.

## 2026-11-15T11:18 UTC, build d6a8dbd6, jobs ip-kd-uncut-ceiling

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-kd-uncut-ceiling | 11:18 (19:18) | 3.4 | **FAIL** | FAIL: profile 12 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step native_check failed: native_check arm: kdBackoff_1=0 (want >=1)<br>ts3fixes activity: etspread_passes 0->1; ts3fixes overwatchchain: not reached (sim 03:32 +725 min, no 03:00); ts3fixes stuckcheckasync: not reached (easpread_lots_passes rose 0: the 3 AM venue cleanup had not armed the stuck checks yet)<br>FAIL: events not fired: | 1 | 2156.7/1900/1603.3 -> 2205.1/1939.3/1550.9 | asyncStarted +769, asyncRefused +10, asyncCollected +759, asyncDropped +1, asyncUs +36533902, asyncMaxUs +476424, asyncPumpUs +3539086, asyncPumpWaits +1961, asyncRefusedM1Pending +8, asyncSkipped +4, asyncBackoffs +17, asyncQueueMax +13 | timing: invalid (job VOID: runInvalid: profiler cut short (81 of 720 s); noisy machine (others 73.0% of game CPUs); crash or abort (no "done; game ; noise high; A/B 1 off / 0 on iterations, < 4 per arm (repro firings)); build: asi b75b706ac7c9 ts3native.asi.master-merge-1115 ts3native 0.36.261-lanes1 (log ok); ts3fixes tuning abdce0a6fef1 ts3fixes.package.master-dalexlol-405c-1115/ts3fixes.package |
