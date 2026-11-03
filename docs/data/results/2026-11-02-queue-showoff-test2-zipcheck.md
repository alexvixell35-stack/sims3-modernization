# Queue showoff-test2-zipcheck 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/showoff-test2-zipcheck-2026-11-02.jsonl`.

## 2026-11-02T23:55 UTC, build fe7d0451, jobs bp-showoff-jig-race

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-showoff-jig-race | 23:55 (07:55) | 1.9 | **FAIL** | FAIL: profile 25 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call failed: no overload of Ts3Fixes.Fixes.ShowOffBadgesCheck takes 1 convertible argument(s)<br>ts3fixes activity: owchain_alarms 0->1, owspread_types_run 0->20, etspread_passes 0->1 | 1 | 2141.1/1858/1532.9 -> 2130.7/1899.8/1515.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (16 of 1500 s); noisy machine (machine load: no samples); crash or abort (no "done; game ; noise high); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning edec78dad898 not in DB (expected game-copy/ts3fixes.package.showoff-test2); noise: high (other apps 15.8% of game CPUs); others on game CPUs 15 |

## 2026-11-02T23:59 UTC, build 76677ac4, jobs bp-showoff-jig-race

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| bp-showoff-jig-race | 23:59 (07:59) | 9.1 | **FAIL** | FAIL: profile 25 min, seed 12345 (rng-compare.ts)<br>FAIL: repro step reflect_call: reflect_call until [{"path":"done","equals":true}] not met in 60 s: "NullReferenceException: A null value was found where an object instance was required.\n#0: 0x00113 throw      in Ts3Agent.Ts3Agent.Handlers:ReflectCall (Ts3Agent.Args) ([8097BBB0] )\n#1: 0x00000<br>ts3fixes activity: owchain_alarms 0->1, owspread_ | 1 | 2133.6/1837.3/1548.9 -> 2120.5/1836.2/1531.3 | - | timing: invalid (job VOID: runInvalid: profiler cut short (447 of 1500 s); noisy machine (others 15.6% of game CPUs); noise high); build: asi ac313272ebb6 ts3native.0.36.105.rel020 ts3native 0.36.105 (log ok); ts3fixes tuning edec78dad898 not in DB (expected game-copy/ts3fixes.package.showoff-test2); noise: high (other apps 15.5% of game CPUs); others on game CPUs 15.5%; clock avg/min 4244/4180 MH |
