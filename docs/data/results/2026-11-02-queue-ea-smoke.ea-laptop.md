# Queue ea-smoke 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ea-smoke-2026-11-02.jsonl`.

Machine: ea-laptop

## 2026-11-02T04:00 UTC, build aa1fc47, jobs ea-sv-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ea-sv-smoke | 04:02 (12:02) | 0.1 | **FAIL** | PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: exit code ?; noise unknown; correctness-only tool ProbeSnap.CallSites); EA laptop harness smoke (docs/226, EA app 1.69.47, TS3_LAUNCH=ea: no game args, the save is loaded by the in-game agent). Baseline-style arm: the only asi on this laptop is the CI build b99f370, which SKIPs all hooks on 1.69, so the job needs no ts3native counters; every section the release ini enabl | ea-laptop |

## 2026-11-02T04:03 UTC, build aa1fc47, jobs ea-sv-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ea-sv-smoke | 04:03 (12:03) | 1.1 | **CRASH** | PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: no crash report written since 2026-11-02T04:03:03.442Z; noise medium; correctness-only tool ProbeSnap.CallSites); build: asi c035abe5a3a6 not in DB ts3native 0.36.152; ts3fixes tuning 0d19ef086257 not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.0% of game CPUs, GPU 3D others 15.0%); others on game CPUs 7.0%; clock avg/min 2595/2595 MHz; EA | ea-laptop |

## 2026-11-02T04:05 UTC, build 3091200, jobs ea-sv-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ea-sv-smoke | 04:05 (12:05) | 2.9 | **FAIL** | FAIL: watchdog:     at handleMessage (/frida/runtime/message-dispatcher.js:25:23)<br>PASS: 0 new script errors (max 0) | 1 | 1830/1482.6/1852.8 -> 1861.9/1533.3/1852.8 | - | timing: invalid (job FAIL: FAIL: watchdog:     at handleMessage (/frida/runtime/message-dispatcher.js:25:23); noise medium; correctness-only tool ProbeSnap.CallSites); build: asi c035abe5a3a6 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 0d19ef086257 not in DB (expected build/ts3fixes.package); noise: medium (other apps 19.6% of game CPUs); others on game CPUs 19.6%; clock avg/min 3112/27 | ea-laptop |

## 2026-11-02T04:09 UTC, build 8f00a27, jobs ea-sv-smoke

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note | machine |
|---|---|---|---|---|---|---|---|---|---|
| ea-sv-smoke | 04:09 (12:09) | 6.6 | **FAIL** | PASS: soak 5 min, watchdog quiet<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, stuckcheckasync_queued 0->107, stuckcheckasync_run 0->107, stuckcheck_noplan 0->6, stuckcheck_notstuck 0->65, stuckcheck_rearmed 0->33, stuckcheck_reset 0->3<br>FAIL: 1 new script errors (max 0): ScriptError_<pc2>__11-02-2026__12-11-24__F328BD0E.xml | 0 | 1822.3/1430.1/1980.9 -> 1847.8/1525.4/1885.3 | - | timing: invalid (job FAIL: FAIL: 1 new script errors (max 0): ScriptError_<pc2>__11-02-2026__12-11-24__F328BD0E.xml; noise medium; correctness-only tool ProbeSnap.CallSites); build: asi c035abe5a3a6 not in DB ts3native 0.36.152 (log ok); ts3fixes tuning 0d19ef086257 not in DB (expected build/ts3fixes.package); noise: medium (other apps 7.3% of game CPUs); others on game CPUs 7.3%; clock av | ea-laptop |
