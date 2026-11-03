# Queue ip-error12-vanilla 2026-11-02

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/ip-error12-vanilla-2026-11-02.jsonl`.

## 2026-11-02T23:38 UTC, build fe7d0451, jobs ipv-spring, ipv-plain

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|

## 2026-11-02T23:40 UTC, build fe7d0451, jobs ipv-spring, ipv-plain

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| ipv-spring | 23:40 (07:40) | 2.3 | PASS | overwrite: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>N/A (vanilla): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines and route hook state, counter gate and /native counters, ts3fixes activity | 0 | 1837.4/1603.9/1876.7 -> 1842.1/1614.9/1860.7 | - | timing: invalid (noise high; correctness-only tool ProbeSnap.CallSites); noise: high (other apps 13.8% of game CPUs); others on game CPUs 13.8%; clock avg/min 4234/4211 MHz; IP Error 12 check (docs/67 s31): load, 3 saves via matrix overwrite case; check .sims3.bad and result code |
| ipv-plain | 23:42 (07:42) | 2.3 | PASS | overwrite: PASS<br>summary: 1 cases, 1 passed, 0 failed<br>N/A (vanilla): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines and route hook state, counter gate and /native counters, ts3fixes activity | 0 | 1885.9/1604.2/1876.8 -> 1884.6/1642/1830.5 | - | timing: invalid (noise high; correctness-only tool ProbeSnap.CallSites); noise: high (other apps 13.3% of game CPUs); others on game CPUs 13.3%; clock avg/min 4257/4225 MHz; IP Error 12 check (docs/67 s31): load, 3 saves via matrix overwrite case; check .sims3.bad and result code |
