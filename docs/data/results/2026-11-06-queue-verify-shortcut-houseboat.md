# Queue verify-shortcut-houseboat 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/verify-shortcut-houseboat-2026-11-06.jsonl`.

## 2026-11-06T22:00 UTC, build 102a7854, jobs ip-ubstart-verify, ip-ubstart-verify-b, ip-ubstart-repro

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ip-ubstart-verify | 22:00 (06:00) | 16.7 | **FAIL** | FAIL: too few tries for unseenboatstart within the time limit (try another world or save); recent: Tyra Snow -> virtual home (faded) / Lincoln Porter => Costa Verde Corp. & Alyssa's Bookstore from Aquaholic - 1br, 1ba / Rajan Patel => Trade Winds Spa and Bistro from Spirit of the Sea - 2br, 1ba / Antoinette Hernandez => Cruzita's Groceries and Diner from Greenbeard's Fishing Spot / Harley Greenwoo | 1 | 2145.7/1846/1596.9 -> 2115.8/1864.3/1588.9 | asyncStarted +8444, asyncRefused +145, asyncCollected +8431, asyncDropped +12, asyncUs +216983808, asyncMaxUs +537541, asyncPumpUs +44651842, asyncPumpWaits +17781, asyncRefusedM1Pending +72, asyncBackoffs +41, asyncQueueMax +9 | timing: invalid (job FAIL: FAIL: too few tries for unseenboatstart within the time limit (try another world or save); recent: Tyra Snow -> virtual ; noise high; correctness-only tool ProbeSnap.CallSites); build: asi 189908e037d4 ts3native.asi.gcstreak1107 ts3native 0.36.181 (log ok); ts3fixes tuning cccb1da937b7 ts3fixes.package.hbstart1 (expected game-copy/ts3fixes.package.hbstart1); noise: high  |

Stopped: ip-ubstart-verify failed (stopQueueOnFail): FAIL: too few tries for unseenboatstart within the time limit (try another world or save); recent: Tyra Snow -> virtual home (faded) | Lincoln Porter => Costa Verde Corp. & Alyssa's Bookstore from Aqu.
