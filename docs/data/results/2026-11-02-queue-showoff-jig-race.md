# Queue showoff-jig-race 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/showoff-jig-race-2026-11-02.jsonl`.

## 2026-11-02T22:49 UTC, build 29900704, jobs bp-showoff-jig-race, bp-hug-jig-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-showoff-jig-race | 22:49 (06:49) | 10.9 | **FAIL** | FAIL: profile 25 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: showoff verdict ab: off arm 16/16 failed, other arms 2/16 failed: off try 1 outside 5m: route ok (plans 3, failed 0, async 3, jig 1, adult plan fails 1, adult DoRoute 0); off try 2 inside 6m: DoRoute false (balloon), plan Succeeded (plans 3, failed 0, async 3, jig 1, adult<br>ts3fi | 1 | 2156.9/1833.9/1612.9 -> 2140/1845.4/1579.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (544 of 1500 s); noisy machine (others 13.5% of game CPUs); crash or abort (no "done; gam; noise high); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning b779b0d14a03 not in DB (expected build/ts3fixes.package); noise: high (other apps 14.2% of game CPUs); others on game CPUs 14.2%; clock avg/min |
| bp-hug-jig-smoke | 23:00 (07:00) | 6.7 | PASS | PASS: profile 5 min, seed 12345 (rng-compare.ts)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1 | 0 | 2154.4/1846.7/1548.9 -> 2101/1840.2/1523.3 | - | timing: invalid (job VOID: runInvalid: noisy machine (others 14.6% of game CPUs); noise high); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning b779b0d14a03 not in DB (expected build/ts3fixes.package); noise: high (other apps 15.3% of game CPUs); others on game CPUs 15.3%; clock avg/min 4185/4117 MHz; smoke: 6 Friendly Hugs with asyncjigsync on (defaul |

## 2026-11-02T23:22 UTC, build fe7d0451, jobs bp-showoff-jig-race

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-showoff-jig-race | 23:22 (07:22) | 10.8 | **FAIL** | FAIL: profile 25 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: InvalidOperationException: showoff verdict ab: off arm 16/16 failed, other arms 2/16 failed: off try 1 outside 5m: DoRoute false (balloon), plan FailedBecauseNoPathToDestination (plans 4, failed 1, async 4, jig 1, adult plan fails 1, adult DoRoute 0); off try 2 inside 6m: DoRoute false (balloon), pl<br>ts3fi | 1 | 2140.2/1857.2/1532.9 -> 2147/1879.1/1515.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (552 of 1500 s); noisy machine (others 15.4% of game CPUs); noise high); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning b779b0d14a03 not in DB (expected build/ts3fixes.package); noise: high (other apps 15.5% of game CPUs); others on game CPUs 15.5%; clock avg/min 4258/4177 MHz; 16 tries per ar |
