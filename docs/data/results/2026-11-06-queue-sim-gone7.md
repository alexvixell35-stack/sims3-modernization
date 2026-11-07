# Queue sim-gone7 2026-11-06

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/sim-gone7-2026-11-06.jsonl`.

## 2026-11-06T01:49 UTC, build e14122a0, jobs sg7-pair-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-pair-1 | 01:49 (09:49) | 1 | **CRASH** | PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-06 09.50.35.txt; noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168; ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 22.0% of game CPUs); others on game CPUs 22.0%; clock avg/min 4162/4162 MHz; v0.2.7 pair, full test |

## 2026-11-06T01:52 UTC, build e14122a0, jobs sg7-pair-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-pair-1 | 01:52 (09:52) | 1 | **CRASH** | PASS: 0 new script errors (max 0) | - | ? -> ? | - | timing: invalid (job FAIL: crash: xcpt <pc> 26-11-06 09.53.27.txt; noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168; ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 20.5% of game CPUs); others on game CPUs 20.5%; clock avg/min 4181/4181 MHz; v0.2.7 pair, full test |

Stopped: same job crashed twice.

## 2026-11-06T01:54 UTC, build e14122a0, jobs sg7-pair-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-pair-1 | 01:54 (09:54) | 2 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":16078,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2174.9/1820.6/1556.7 -> 2226.3/1835.2/1556.7 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 30.7% of game  |

## 2026-11-06T01:57 UTC, build e14122a0, jobs sg7-noours-1, sg7-pair-2, sg7-noours-2, sg7-pair-3, sg7-noours-3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-noours-1 | 01:57 (09:57) | 2.5 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines  | 0 | 1958.1/1632.3/1772.9 -> 2028/1664.4/1751.5 | - | timing: invalid (noise high); noise: high (other apps 23.9% of game CPUs); others on game CPUs 23.9%; clock avg/min 4161/4149 MHz; ourMods false: no ts3native, no ts3fixes; sim_gone_load armed |
| sg7-pair-2 | 01:59 (09:59) | 2 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":15547,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2186.5/1845.6/1540.8 -> 2212.5/1868/1540.8 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 25.7% of game  |
| sg7-noours-2 | 02:01 (10:01) | 2.6 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines  | 0 | 1915.2/1583.6/1788.9 -> 1947.4/1603.4/1788.9 | - | timing: invalid (noise high); noise: high (other apps 31.1% of game CPUs); others on game CPUs 31.1%; clock avg/min 4111/3985 MHz; ourMods false: no ts3native, no ts3fixes; sim_gone_load armed |
| sg7-pair-3 | 02:04 (10:04) | 2.4 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: 0 new script errors (max 0) | 0 | 2185.6/1844/1544.8 -> 2218.4/1859/1540.4 | - | timing: invalid (noise high); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: high (other apps 20.9% of game CPUs); others on game CPUs 20.9%; clock avg/min 4102/3998 MHz; v0.2.7 pair, full test install, sim_gone_load armed |
| sg7-noours-3 | 02:06 (10:06) | 2.5 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines  | 0 | 1893.9/1561/1828.9 -> 1896.8/1582.2/1828.9 | - | timing: invalid (noise high); noise: high (other apps 12.2% of game CPUs); others on game CPUs 12.2%; clock avg/min 4166/4133 MHz; ourMods false: no ts3native, no ts3fixes; sim_gone_load armed |

## 2026-11-06T02:10 UTC, build e14122a0, jobs sg7-noours-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-noours-1 | 02:11 (10:11) | 2.5 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines  | 0 | 1818.4/1559.3/1876.9 -> 1813.9/1576.6/1876.9 | - | timing: invalid (noise medium); noise: medium (other apps 22.7% of game CPUs); others on game CPUs 22.7%; clock avg/min 4159/4118 MHz; ourMods false: no ts3native, no ts3fixes; sim_gone_load armed |

## 2026-11-06T02:21 UTC, build e14122a0, jobs sg7-noours-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg7-noours-1 | 02:21 (10:21) | 2.5 | PASS | sim-gone-load: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL lines  | 0 | 1932.9/1606.4/1788.9 -> 1974/1618.2/1788.9 | - | timing: invalid (noise medium); noise: medium (other apps 23.3% of game CPUs); others on game CPUs 23.3%; clock avg/min 4203/4180 MHz; ourMods false: no ts3native, no ts3fixes; sim_gone_load armed |
