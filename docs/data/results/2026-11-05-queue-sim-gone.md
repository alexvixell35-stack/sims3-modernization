# Queue sim-gone 2026-11-05

Tool: `tools/harness/queue.ts` (docs/TESTING.md). Times UTC (PHT in brackets). Rows: `probe-out/queue/sim-gone-2026-11-05.jsonl`.

## 2026-11-05T18:54 UTC, build 2a2b3863, jobs sg-pair-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-pair-1 | 18:54 (02:54) | 1.9 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: 0 new script errors (max 0) | 0 | 2119.4/1831.2/1580.9 -> 2135.5/1852.5/1580.9 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 25.4% of game CPUs); others on game CPUs 25.4%; clock avg/min 4147/4126 MHz; v0.2.7 pair, full test install |

## 2026-11-05T18:56 UTC, build 2a2b3863, jobs sg-noours-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-noours-1 | 18:56 (02:56) | 2 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL | 0 | 1857.6/1588.4/1820.9 -> 1855.9/1609.3/1820.9 | - | timing: invalid (noise medium); noise: medium (other apps 25.2% of game CPUs); others on game CPUs 25.2%; clock avg/min 4198/4173 MHz; ourMods false: no ts3native, no ts3fixes |

## 2026-11-05T18:58 UTC, build 2a2b3863, jobs sg-nooverwatch-1

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-nooverwatch-1 | 18:58 (02:58) | 1.8 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":13093,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2142.9/1840.2/1612.9 -> 2158.7/1859.9/1600.8 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 14.6% of g |

## 2026-11-05T19:00 UTC, build 2a2b3863, jobs sg-pair-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-pair-2 | 19:00 (03:00) | 1.8 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":15187,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2125.5/1825.3/1620.8 -> 2193.7/1858.4/1603.8 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 10.2% of g |

## 2026-11-05T19:02 UTC, build 2a2b3863, jobs sg-noours-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-noours-2 | 19:02 (03:02) | 1.9 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL | 0 | 1849.7/1574.5/1868.8 -> 1868.1/1609/1852.9 | - | timing: invalid (noise medium); noise: medium (other apps 10.6% of game CPUs); others on game CPUs 10.6%; clock avg/min 4254/4204 MHz; ourMods false: no ts3native, no ts3fixes |

## 2026-11-05T19:04 UTC, build 2a2b3863, jobs sg-nooverwatch-2

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-nooverwatch-2 | 19:04 (03:04) | 1.8 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: 0 new script errors (max 0) | 0 | 2114.9/1820.8/1612.9 -> 2151.7/1846/1610.3 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 13.2% of game CPUs); others on game CPUs 13.2%; clock avg/min 4253/4219 MHz; v0.2.7 pair, NRaas Overwatch removed for the job |

## 2026-11-05T19:05 UTC, build 2a2b3863, jobs sg-pair-3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-pair-3 | 19:05 (03:05) | 1.7 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":12047,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2119/1826.7/1612.9 -> 2151.1/1846/1612.9 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 12.3% of g |

## 2026-11-05T19:07 UTC, build 2a2b3863, jobs sg-noours-3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-noours-3 | 19:07 (03:07) | 1.8 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL | 0 | 1854.2/1572.5/1860.7 -> 1882.3/1604.4/1844.9 | - | timing: invalid (noise medium); noise: medium (other apps 11.4% of game CPUs); others on game CPUs 11.4%; clock avg/min 4238/4210 MHz; ourMods false: no ts3native, no ts3fixes |

## 2026-11-05T19:09 UTC, build 2a2b3863, jobs sg-nooverwatch-3

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-nooverwatch-3 | 19:09 (03:09) | 1.8 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":14171,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2116.3/1824.3/1612.9 -> 2154.9/1843.1/1612.9 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 11.2% of g |

## 2026-11-05T19:11 UTC, build 2a2b3863, jobs sg-pair-4

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-pair-4 | 19:11 (03:11) | 1.8 | **FAIL** | FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqualsCurrent":true,"watchdog_shortcuts":0,"owstuck_runs":0,"destroyed":true,"simIndex":1,"atHome":true,"otherSimsInteraction":["0: Sims3.Gameplay.Core.VisitCommunityLotWith"],"elapsedMs":15563,"currentAtLot":"Air Spray - 2br, 2ba","watchdog_second":0,"leftGroupReason":"not available | 1 | 2122.3/1827.8/1626.3 -> 2156/1845/1608.6 | - | timing: invalid (job FAIL: FAIL: compat_visit_status failed: {"owstuck_sims":0,"name":"Lala Angelista","createdSimIsNewObject":true,"createdSimEqua; noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 11.6% of g |

## 2026-11-05T19:13 UTC, build 2a2b3863, jobs sg-noours-4

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-noours-4 | 19:13 (03:13) | 1.9 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: clean baseline: clean: no Bin/ts3native.log, /native null, ts3fixes.applied null, no counter above 0<br>N/A (ourMods false): asi build check, ts3fixes build check, ts3native.log version gate, ts3native.ini features, hook FAIL | 0 | 1854.6/1566.9/1852.8 -> 1870.5/1604.5/1836.9 | - | timing: invalid (noise medium); noise: medium (other apps 11.5% of game CPUs); others on game CPUs 11.5%; clock avg/min 4245/4200 MHz; ourMods false: no ts3native, no ts3fixes |

## 2026-11-05T19:15 UTC, build 2a2b3863, jobs sg-nooverwatch-4

| job | start | min | result | verdict | exit | commit / private / largest free MB (before -> after) | counters changed | note |
|---|---|---|---|---|---|---|---|---|
| sg-nooverwatch-4 | 19:15 (03:15) | 1.8 | PASS | compat-visit-control: PASS: steps done (no watch) (expected: EA's Run runs; active Sim and the picked Sim arrive on the lot; no Sim object lost; no new script error)<br>PASS: 0 new script errors (max 0) | 0 | 2117.2/1839.5/1618.1 -> 2156.4/1858.5/1610.8 | - | timing: invalid (noise medium); build: asi 92b1341024f7 not in DB ts3native 0.36.168 (log ok); ts3fixes tuning 10b027b92d63 rel025d-fixes/rel025d-ph-fixes/rel025e-fixes/ts3fixes.0.2.6 (expected game-copy/asi-v027/ts3fixes.package); noise: medium (other apps 12.1% of game CPUs); others on game CPUs 12.1%; clock avg/min 4235/4217 MHz; v0.2.7 pair, NRaas Overwatch removed for the job |
