# 332. Unbounded worlds: work packages, handoffs and lanes (2026-11-23)

Plan only: no feature code in this doc's branch (`feature/unbounded-packages-1123`). It cuts the unbounded-world plans into
packages of one agent each (~10-60 min of agent time), lists what is already built and measured, and queues the tests that need
no agent (§6). Sources: docs/320 §7-8 (large worlds, region streaming), docs/321 (unbounded sweep, caps lanes 0/A/B/C/D/E),
docs/322 (distance bands), docs/323 (overflow audit), docs/324 (far-Sim model, branch `feature/far-sim-model-1122`), docs/325
(instantiation cost), docs/326 (big-world research), docs/329 (non-Sim split), docs/330 (our loop cost), docs/331 (far-object
model, branch `feature/far-object-model-1123` at b188cb1b4: compute everything, E1-E6), AGENTS.md "Build for unbounded worlds",
"Main agent orchestrates". Scope (Rodney 2026-11-22): this repo does the research and the performance work that makes larger or
streamed worlds possible; cap removal and streaming implementation go to a separate future project, so the WS packages below stop
at prerequisites and first proofs.

Effort is agent wall-clock time (CLAUDE.md rule), game runs listed separately. Every behaviour switch named below defaults OFF,
with the household, selected-Sim and unknown-scope exemption in code (unknown counts as household), and a repro per behaviour.

## 0. Short answer

- **Built and measured (§1):** 14 pieces. Merged on master: docs/320 research + `tools/world-gen.ts`, docs/321 sweep, docs/322
  band probe (4/4 PASS), world-scale `[WaterGrid] Prof`. On open branches: caps lanes 0+B (4/4 PASS) and C (3 PASS + 1 VOID),
  docs/323 overflow fixes (4/4 PASS), docs/325 instantiation probe (PASS), docs/329 non-Sim split (5/5 PASS), docs/330 loop-cost
  fixes (13/14, conversation check FAIL twice, rerun 3 armed), docs/327 SP instantiate guard (FAIL, rerun running), docs/324 and
  docs/331 designs (docs only).
- **Packages (§2): 88** = FS 20, FO 16, WS 10, CP 13, ME 11, ST 18. 21 of them are new research or measurement packages that close
  facts the split found missing (§2.7).
- **Critical path (§4):** to the first far-Sim timing verdict (docs/324 P5): 13 packages, **~10.5 h agent + ~6 h game**. The whole
  program is bound by the one game machine: **~40 h of serial game time** over all packages; agent time ~60 h spread over lanes.
- **Lanes (§5):** at most **4 build agents + 3 static agents = 7 at once**, 0 agents during a timing run.
- **Queued now, no agent (§6):** ME-0 ip-capsc rerun, ME-1 band rerun at the model edges `Bands=150,200,300,450,1000`. Waiter
  `probe-out/unbounded-notests-wait-1123.sh`, FIFO after compat-resolve-1122f; ETA ME-0 ~11:10, ME-1 ~12:10 PHT 2026-11-23
  (earliest ~09:00 / ~10:00 if the loop-cost timing gate stops).
- **Large-world game tests are deferred** (Rodney 2026-11-23) until WS-0, a purpose-built test world (oceans, houseboats, dense
  cities, rabbit holes, community lots) with the scenarios each test needs: ME-2, ME-5, ME-9, WS-1, WS-2, WS-4b, WS-5, FS-20 and
  the 4x proof of FO-16. WS-0 is not started and waits on Rodney.

## 1. Built inventory

State as of 2026-11-23 07:45 PHT. "Merged" = ancestor of master.

| # | Piece | Doc | Branch (head) | Build tag (builds.json, sha256 prefix) | Switch (default) | Test state | Merge |
|---|---|---|---|---|---|---|---|
| 1 | Large-worlds research, cap inventory, region streaming design, world generator | 320 | `feature/large-worlds-1122` (4aeea7f0e) | none (tool `tools/world-gen.ts`) | none | queue `large-worlds-load-1122` NOT RUN (now ME-2) | merged |
| 2 | Unbounded-world sweep (H1-H4, M1-M16, L1-L15, O1-O23) | 321 §0-6 | `docs/unbounded-sweep-1122` | none | none | static | merged |
| 3 | Caps lane 0 (fill counters `ts3native_caps`, ts3fixes slots 1518-1529) + lane B (H1-H4) | 321 §7.1-7.3 | `feature/unbounded-caps-1122` (b7cb38e63) | `ts3native.asi.unbounded-caps-1122` 672d3c2002ec (0.36.299-ucap1), `ts3fixes.package.unbounded-caps-1122` 748847418157, ini 5937bbb1266f, agent ceeb0c2e7623 | none new (H1-H4 already ON; counters always on) | `unbounded-caps-correct-1122` 4/4 PASS | open |
| 4 | Caps lane C (M2 object-lot map grows, M11 DoorMemoX keeps live rows, L4 UnseenAnim probing) | 321 §7.4 | same (b7cb38e63) | `ts3native.asi.unbounded-caps-1122c` eb590e1ce5ea (0.36.299-ucap2) | none new (default-ON items) | `unbounded-caps-correct-1122c`: smoke, BP, SV PASS; **ip-capsc VOID** (apex-401-present-lock-cycle); results file uncommitted in the lane worktree | open |
| 5 | Distance-band probe `[UnseenProbe] Bands`, TaskSeam 65536/4096 | 322 | `feature/unseen-distance-probe-1122` (04089d2dc) | in master set m1122 (`ts3native.asi.m1122` 876d7de8d625) | `[UnseenProbe] Bands=` (empty = off) | `unseen-bands-1122` 4/4 PASS (run 2) | merged |
| 6 | Water grid G build split `[WaterGrid] Prof`, RouteEarlyOut verdict | 100 §13 | `feature/world-scale-perf-1122` (5266ed8f8) | `ts3native.asi.world-scale-perf-1122` d31b5b1cc7c5 (0.36.299-wgprof1), in m1122 | `[WaterGrid] Prof=0` | watergrid queues repinned on m1122 (separate track) | merged |
| 7 | Overflow audit + fixes (L2 JIT room, L6 world object, PathFmt) | 323 | `bugfix/overflow-fixes-1122` (b446c3d60) | `ts3native.asi.overflow-fixes-1122b` 8e8b3eb2a36e (0.36.299-ovf1122b), `ts3fixes.package.overflow-fixes-1122` 4a7b0b02b5c2 | fixes to ON code, no new switch | `overflow-fixes-correct-1122` 4/4 PASS (04:42 PHT); open: L8 `JgPurge`, Linux host test | open |
| 8 | Sim instantiation cost by part | 325 | `feature/inst-probe-1122` (ccc445d1d) | `ts3native.asi.inst-probe-1122` 7634ac2fb8d5 (0.36.299-iprobe1), `ts3fixes.package.inst-probe-1122` eaa316a09c74 | probe (off) | `inst-probe-1122` PASS (ip N/A, retry PASS) | open |
| 9 | Big-world research (other games, tier ladder) | 326 | `feature/unbounded-sim-research-1122` (471d48409) | none | none | research | open |
| 10 | SP instantiate guard (`Sim.OnCreation` covers every creation route; NRaas-created Sims) | 327 | `bugfix/sp-instantiate-guard-1122` (a33fdd7a7) | `ts3fixes.package.sp-instantiate-guard-1122c` 3dfd9f88a26f | guard (off) | first run FAIL (`simcreated_other_armed=27`, want < 3); rerun1 chain running 07:39 PHT | open |
| 11 | Non-Sim split per owner, band, far-lot class `[Diag] NonSimSplit` | 329 | `feature/nonsim-split-1122` (d8134f1ca) | `ts3native.asi.nonsim-split-1122` c66bc9a06fc9 (0.36.299-nsplit1), `ts3fixes.package.nonsim-split-1122` be282ae8acb3, ini 4344be9d20ec | `[Diag] NonSimSplit=0` | `nonsim-split-1122` 5/5 PASS | open |
| 12 | Our loop cost: `deadrefcadence`, `viewerpushdelta` | 330 | `feature/ts3fixes-loop-cost-1122` (707ebb1c2) | `ts3fixes.package.ts3fixes-loop-cost-1122c` 4fd939842e0e (+ b, base) | both ts3fixes, default **ON** (fixes to ON loops; off = the pass as before) | correctness 13/14; `bp-conversation-gone-check-cadence` FAIL (2026-11-22, and 2026-11-23 07:29 with 1 new script error); rerun 3 + timing armed (`probe-out/loopcost-conv3-wait-1123.sh`) | open |
| 13 | Far-Sim model design (tiers H/T1-T4, P0-P8, decisions 1-9, R1-R3) | 324 | `feature/far-sim-model-1122` (ce62dc906) | none | planned `farsim` / `farsimt2` / `farsimt3` / `farsimt4` (all off) | docs only | open |
| 14 | Far-object model design (lots L1-L3, compute everything, E1-E6, P0-P8) | 331 | `feature/far-object-model-1123` (b188cb1b4) | none | planned `farobjlot` / `farobjfire` / `farobjtraffic` / `farobjworld`, `[Diag] FarObjProbe` (all off) | docs only | open |
| 15 | CP-12: L8 `JgPurge` drops destroyed Sims from `sJgPending`; L6 I-581-2 static look (stays open, docs/51) | 323 §5 | `bugfix/cp12-jgpurge-1123` | `ts3fixes.package.cp12-jgpurge-1123` 341cdf9d7b65 | inside `kJungleGymExitQueue` (ON) | queue `cp12-jgpurge-correct-1123` NOT RUN | open |

Related, not unbounded work but on the path: docs/328 compat rules (`CompatResolve.cs` stand-down pattern, used by FO-9 and the
decision on mods that tick held classes, docs/331 §12 #4); NRaas full audit (`feature/nraas-full-audit-1122`, facts already folded
into docs/324 §4 / §5.6).

## 2. Work packages

Columns: **Dep** = depends on, **Par** = can run in parallel with (no shared files), **Kind** static / build / game-run / doc,
**Est** = agent minutes + game runs. **HH** = household note (H = needs the explicit household, selected-Sim and unknown-scope
exemption in code; R = read-only, household untouched by construction; - = no Sim or lot behaviour). Every build package ends with
`node tools/selftest-all.ts` 0 FAIL, the native selftest when native code changed, a registered build in builds.json
(`<kind>.<lane>-<mmdd>` with sha256) and a dry-run queue. Repros are `tools/harness/repros.json` entries with off / on arms.

### 2.1 Measurement (ME)

| ID | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|
| ME-0 | Rerun the VOID ip-capsc job (caps lane C on IP) | queue `unbounded-caps-correct-1122c.json` (lane worktree), set unbounded-caps-1122 + asi 1122c | `ip-capsc` result, last `caps (minute` line | ip-capsc PASS; `asOl` / `dmx` / `aa` full 0 | - | ME-1/2 (serial on machine) | game-run | 0 + 1 job ~20 min (**queued**) | R |
| ME-1 | NPC and non-Sim ms/s at the model edges 150/200/300/450/1000 m (docs/324 P1 band rerun, decision 1; docs/331 L3 edge) | queue `farmodel-bands-1123.json` (this branch), nsplit1 set | band tables per world | 4/4 PASS, bands line `edges 150,200,300,450,1000 m (6 bands` read back | - | - | game-run | 0 + smoke + 3 x 10 min ~60 min (**queued**) | R |
| ME-2 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). First 4096 m load (docs/320 §8 a1) | queue `large-worlds-load-1122.json`, `tools/world-gen.ts`, set e1122 | load time, bounds / G read-back, largest free, fault site if any | control PASS; 4096 jobs PASS or a fault mapped to a docs/320 §1.2 site | WS-0 | - | game-run | 0 + prep + 3 jobs ~45 min (removed from the queue 2026-11-23) | - (menu only, no household) |
| ME-3 | Digest ME-0 into docs/321 §7.4 (`asOl` hw vs 49,152, `dmxLive`, `aaDisp`), settle the [inferred] address-bit note | ME-0 log, lane worktree results file | docs/321 §7.4 results row, commit on `feature/unbounded-caps-1122` | table filled, inference marked verified or refuted | ME-0 | ME-4, ME-5 | doc | 15 | - |
| ME-4 | Digest ME-1: Sims and ms/s per tier band, choose the T3 edge per world (300 or 450 m), non-Sim ms/s past 200 m | ME-1 jobs, `unseen-band-report.ts`, `nonsim-split-report.ts` | docs/324 §1.3 band check table, docs/331 §2.1 row at 200 m | per-world T3 edge written with its numbers | ME-1 | ME-3, ME-5 | doc | 25 | - |
| ME-5 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). Digest ME-2: load time, memory, each fault -> docs/320 §1.2 site | ME-2 logs, crash reports | docs/320 §1.6 results, WS-1 input list | every job verdict explained | ME-2 | ME-3, ME-4 | doc | 25 | - |
| ME-6 | Day-run queue (25 min, speed 3, across 05:00, story progression ON) on the nsplit1 set: daily plant bursts, spawner rolls, SP object side (docs/331 §2.2) | ST-9 answer, `nonsim-split-1122.json` | queue `farmodel-day-<mmdd>.json` | dry-run clean; 2 jobs PASS | ST-9 | FS/FO builds | doc + game-run | 20 + 2 x 25 min | R |
| ME-7 | Native-create split of docs/325 (engine object vs rig vs model vs resource reads) and `Sim.OnCreation` timing, for FS-18 warm-up | docs/325 §1, inst-probe branch | probe extension, counters, queue | 3 worlds PASS, split sums to `native` row | CP-0 | FS-1..3 | build + game-run | 45 + smoke + 3 jobs | R |
| ME-8 | Per-lot memory line (docs/320 §5.4 / b0): bytes per lot and per NPC instance | docs/320 §4-5, MemDiag | probe (off), `memory` line per lot | smoke + 3 jobs PASS | CP-0 | ME-7 (other files) | build + game-run | 45 + smoke + 3 jobs | R |
| ME-9 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). Caps fill on the generated 4096 m world once it loads with lots (re-run of the caps queue there) | ME-5, WS-4b | queue variant on the LwSV4096 save | caps lines read, no `full` | WS-4b, CP-0 | - | game-run | 15 + 2 jobs | R |
| ME-10 | Visit-rate baseline: EA's live visits per friend-day by distance (decay calibration for docs/324 §3.9) | ST-13 answer | counter or log tally on the nsplit1 / m1122 set, 3-day run | rate table by distance band | ST-13 | any build | build (small diag) + game-run | 30 + 1 x 25 min | R |

### 2.2 Static research (ST)

| ID | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|
| ST-1 | Facade census (docs/324 §5.4): exact call sites per read for phone, invite, story progression, relationship panel | decompiled Sims3GameplaySystems (local only), docs/324 §5.4 | docs/324 §5.4 table with addresses / methods [verified] | every read row has a site | - | all ST | static | 45 | - |
| ST-2 | NRaas StoryProgression / Overwatch reads, writes, instantiate sites on model-owned Sims (rest after the full audit) | docs/324 §4, NRaas source (read / cite only) | docs/324 §4 site table | each site: read / write / build, hook choice | - | all ST | static | 40 | - |
| ST-3 | EA setters for seeded drift (LTR, skill points, career performance) and their side effects | docs/324 §2.7 | setter list with events they fire | each drift write has one EA setter, no UI side effect unlisted | - | all ST | static | 30 | H (never a household relationship) |
| ST-4 | Alarm owner and dispatch site: `AlarmManager.Simulate`, Sim alarms on `AlarmManager.Global`, lot `AlarmManager` (docs/324 §5.2, docs/331 §3) | docs/331 §3, §4.0 | one dispatch-hook design note (shared by FS and FO) | hook site, read-only due-timer read, `Timer.ObjectRef` path [verified] | - | all ST | static | 45 | - |
| ST-5 | UI hook points for "looking" (decision 8: Sim info, relationship panel, phone contacts, map selection) | docs/324 §3.7 | hook list with methods | each of the 4 UI paths has a promote hook | - | all ST | static | 30 | - |
| ST-6 | Placeholder static check for adults (docs/324 §5.6, decision 6) | docs/324 §5.6 | proof or blocker list | written verdict per risk | - | all ST | static | 45 | H |
| ST-7a | FO callback census, shipped packs: every `AddAlarm*` callback into docs/331 §4.0 rows | docs/331 §4.0, §8 | census table (doc) | every callback has a row or "generic" | - | ST-7b | static | 60 | - |
| ST-7b | FO callback census, top-100 mods (docs/328 compat set) | compat set list, ST-7a format | census table | as ST-7a | - | ST-7a | static | 60 | - |
| ST-8 | Own-thread object classes (docs/331 §4.9) + mod object-tick census; which need a pause hook (E4) | docs/331 §4.9, §5, docs/328 | class list with loop shape | each class: pause hook or E4 | - | all ST | static | 45 | - |
| ST-9 | Harness check: can a queue job set game speed 3, start before 05:00 and run with story progression on | `tools/harness/queue.ts`, `profile-world.ts`, repro schema | answer + the job keys to use (or the harness change needed) | keys named with a code line | - | all | static | 20 | - |
| ST-10 | Lot page-out / page-in RE (docs/320 §7.5, b2): lot object list, serialize path, id stability | docs/320 §7.5, TS3W.exe (REA / Ghidra on a copy) | RE note with addresses [verified / inferred] | load and unload path named end to end | - | all ST | static | 60 | - |
| ST-11 | Story progression and services over parked / paged lots (docs/320 b5): who reads a lot that is not live | docs/320 §8 b5, ST-2 | census table | every SP / service reader classified | ST-2 | ST-7a/b | static | 60 | - |
| ST-12 | docs/321 lane A hub design check: `lotstream.cpp` `0xABD3C0` / `0xABD3E0` are called for every lot add / remove (travel, Edit Town, world change) | docs/321 §5 lane A, native lotstream.cpp | hook coverage note [verified] | each lifecycle event mapped | - | all ST | static | 30 | - |
| ST-13 | EA visit logic: where `VisitSituation` starts, conditions, rate (docs/324 §3.9 decay input) | docs/324 §3.9 | rate formula note | conditions and timers named | - | all ST | static | 40 | - |
| ST-14 | One sidecar format for the far-Sim event log (docs/324 §2.5), the far-object event log (docs/331 §6.4) and region state (docs/320 §7.6) | the three sections, docs/FAQ sidecar folder | spec section (new docs/NNN or docs/331 §6.4 extension) | one schema, versioned, load-time cleanup rule | - | all ST | doc | 45 | H (nothing in .sims3) |
| ST-15 | Shared hold-hook API between FS and FO (lot skip, Sim alarm hold, E1 pass-through, one owner flag), file ownership | ST-4, docs/324 §5.2, §6.5, docs/331 §6.5 | API note: file `mods/ts3fixes/src/FarHold.cs`, owner = M-shared lane | FS-8 and FO-4 briefs can cite it without editing each other's files | ST-4 | ST-14 | doc | 30 | H |
| ST-16 | `SceneQuadTree` config key (docs/320 §1.2, `0x58B160`): where it is read, can a queue set `4096,512,7` without a build | docs/320 §1.2 | answer + the file / key to set | read site [verified] | - | all ST | static | 30 | - |
| ST-17 | WTXT per-cell paint layers decode (docs/320 a3) | docs/320 §1.5, three world files | decode note + world-gen self-check case | decoded fields round-trip on SV | - | all ST | static | 60 | - |

### 2.3 Caps leftovers (CP)

| ID | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|
| CP-0 | Merge wave: `feature/unbounded-caps-1122`, `feature/nonsim-split-1122`, `feature/inst-probe-1122`, `bugfix/overflow-fixes-1122`, docs branches 324 / 326 / 331 / 332 (ts3-merger; between game runs) | branches in §1, `tools/agent-merge.cjs` | master set with the lanes, repinned queues | merger selftests, dry-runs | ME-3 (caps results in) | ST-* | doc (merge) | 45 | - |
| CP-1 | Lane A native: lot generation counter + world-change event in `lotstream.cpp` (always-on counter, default-off consumers). **Built 2026-11-23** (`feature/cp1-lotgen-1123`, 0.36.299-cp1, API docs/321 §8.2; queue `cp1-lothub-correct-1123` not run; ST-12 still open, docs/51 §591) | docs/321 §5 lane A, ST-12 | ts3native build, export | native selftest, smoke | CP-0, ST-12 | CP-6 (other files), FS-1..3 | build | 60 | - |
| CP-2 | Lane A managed: `OnLotLoad` / `OnWorldChange` hook in ts3fixes. **Built 2026-11-23** (`feature/cp2-lothook-1123`, 0.36.299-cp2, API docs/321 §9.2; queue `cp2-lothook-correct-1123` not run; I-593-1 open) | CP-1 | ts3fixes build, counter | managed selftest, smoke | CP-1 | FS-1..3 | build | 45 | - |
| CP-3 | Lane A native consumers: M1, M3, M4, M9, M11, M15, L1, L3, L15 clear or drop rows on the event | CP-1, docs/321 §1 | ts3native build | under-event equality self-tests | CP-1 | CP-4 | build | 60 | H (unchanged paths; rows only cleared) |
| CP-4 | Lane A managed consumers: O11, O12, O18, M8 validity, DoorGen and GhostSweep per lot | CP-2 | ts3fixes build | self-tests | CP-2 | CP-3 | build | 60 | H |
| CP-5 | Lane A correctness queue: travel + save matrix | CP-3, CP-4 | queue `caps-lanea-correct-<mmdd>` | queue PASS, `scriptErrorsMax 0` | CP-3, CP-4 | - | doc + game-run | 30 + 1 + save matrix (~90 min) | H |
| CP-6 | Lane C rest: M6, M7, M10 (`CapMB` scale + log), L2, L5, L14 sized from live counts or grown on pressure | docs/321 §5 lane C | ts3native / ts3fixes build | under-cap equality self-tests | CP-0 | CP-1 | build | 60 | - |
| CP-7 | Lane C timing A/B (needs quiet machine) | CP-6 | queue `caps-lanec-time-<mmdd>` (ABBA, smoke first) | per-arm ms/s, switch read-back | CP-6 | - | doc + game-run | 20 + ~130 min | - |
| CP-8 | Lane D nearby-only, part 1: M5, M6 walk, M10 walk | docs/321 §5 lane D | build behind default-off switches | self-tests | CP-5 | FS / FO lanes | build | 60 | H |
| CP-9 | Lane D part 2: M12, M13, M16, L10-L13 (cached per-type lot lists, lot-scoped DeadSweep, dirty-lot P3 cuts) | CP-8 | build | self-tests | CP-8 | FS / FO lanes | build | 60 | H |
| CP-10 | Lane D timing A/Bs (needs quiet machine) | CP-8, CP-9 | queue | per-arm ms/s | CP-9 | - | doc + game-run | 20 + 2-3 x ~130 min | H |
| CP-11 | Lane E: OFF items O2 and O3 first (they fail outright) | docs/321 §2 | fix in the OFF switches' code | their own repros | CP-0 | any | build | 30 | H |
| CP-12 | docs/323 open: L8 `JgPurge` drop of destroyed Sims; L6 rests on I-581-2 (verify) | docs/323 §5.1 | fix + self-test | selftest, ovf queue rerun job | CP-0 | any | build | 30 | - |

### 2.4 Far-Sim model (FS, docs/324)

P0 is the prerequisite set: CP-1..CP-4 (lane A), CP-6 (M6), CP-8 (M5), ST-1..ST-6, ME-7. P2 (FS-5) needs docs/327 merged.

| ID | Phase | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|---|
| FS-1 | P1a | Shadow core: pure functions for needs, day plan, position / activity at t (docs/324 §2.1-2.4), seed + version | docs/324 §2 | `FarSimModel.cs` (no hooks), self-test | managed selftest with fixed seeds | CP-0 | FO-1, CP-1 | build | 60 | R |
| FS-2 | P1b | Shadow compare hooks: model vs EA when a far Sim comes near and per sim-hour sample; eligibility / exclusion counts by reason | FS-1, ST-1 | `[Diag] FarSimShadow` (off), counters | selftest, smoke | FS-1, ST-1 | FO-2 | build | 60 | R |
| FS-3 | P1c | Seeded drift (§2.7) and visit proposals (§3.9) computed in shadow | FS-2, ST-3, ST-13 | shadow counters per pair-day, visits per friend-day | selftest | FS-2, ST-3, ST-13 | FO-2 | build | 45 | R |
| FS-4 | P1d | Shadow calibration queue BP / IP / SV with the ME-1 edges | FS-3, ME-4 | queue `farsim-shadow-<mmdd>`; per-world T3 edge | 3 jobs PASS; error tables read | FS-3, ME-4 | - | doc + game-run | 20 + smoke + 3 x 10 min | R |
| FS-5 | P2 | One-instance guard per SimDescription + age-up ordering (§5.5, R2) | docs/327 guard (merged), docs/324 §5.5 | guard in the choke point, self-tests, repros `farsim-dup-build`, `farsim-ageup-build` | 2 repros PASS | CP-0 + docs/327 merged | FS-1..4 | build + game-run | 60 + 2 jobs | H (guard covers all Sims, no behaviour change for one build) |
| FS-6 | P3a | Ownership table + T2 cadence tier (`farsimt2`, hand-off tier, R3) | FS-4, FS-5 | ownership table, T2 cadence | selftest, smoke | FS-4, FS-5 | FO-6 | build | 60 | H |
| FS-7 | P3b | T3 demote / promote (`farsimt3`): parked interaction, frozen motives, hide + inert, physical facade, lazy motive reads, flush before save | FS-6, ST-1 | T3 path | selftest, smoke | FS-6 | FO-6 | build | 60 | H |
| FS-8 | P3c | Held alarms through the shared hook (decision 7), on-demand guard (`InteractionQueue.Add` promote), always-live hooks | FS-7, FO-3 | Sim half of the hold hook | selftest | FS-7, FO-3, CP-2 | FO-7 | build | 60 | H |
| FS-9 | P3d | Observation hooks (decision 8) + drift applied live | FS-8, ST-5 | promote-on-look, drift writes | selftest | FS-8, ST-5 | FO-7 | build | 45 | H (never a household relationship: `driftHousehold` = 0) |
| FS-10 | P3e | Repros 1-10, 12 + correctness queue | FS-9 | repros.json entries, queue `farsim-correct-<mmdd>` | queue PASS (BP 10 min, read-back, `scriptErrorsMax 0`, `demotedHousehold` 0) | FS-9 | FO-8 (doc) | doc + game-run | 45 + 11 jobs (~130 min) | H |
| FS-11 | P4a | Spread queue: TTV priority, hysteresis pass, pre-expand ring (§6.1-6.3) | FS-10 | queue code | selftest | FS-10 | FO-9 | build | 60 | H |
| FS-12 | P4b | Prebuild, loading-screen batching, visit proposals live (§3.9, §6.4) + 3 jobs (camera sweep, travel, visit rate) | FS-11, ME-10 | build + queue | 3 jobs PASS | FS-11, ME-10 | FO-9 | build + game-run | 45 + 3 jobs | H |
| FS-13 | P5 | Timing: T3 in-load A/B BP / IP / SV, T2 on vs off pair (needs quiet machine) | FS-12 | queue `farsim-time-<mmdd>` (ABBA, smoke first) | per-arm ms/s by band, hitch p99 / p99.9 / max | FS-12 | none (timing) | doc + game-run | 30 + ~130 min | H |
| FS-14 | P6a | NRaas StoryProgression override (decision 4): hooks on its instantiate helpers and repair sites, our precedence | FS-10, ST-2 | hooks, stand-down rule | selftest | FS-10, ST-2 | FS-11 | build | 60 | H |
| FS-15 | P6b | EA SP night + NRaas core SP night + NRaas scenario on a T3 Sim (repros 7, 13) | FS-14 | queue | 3 jobs PASS, `ownerConflict` 0 | FS-14 | - | doc + game-run | 30 + 3 jobs | H |
| FS-16 | P7a | T4 placeholder variant (`farsimt4`, off even with the model on), placeholder on the home lot | FS-13, ST-6 | T4 path | selftest | FS-13, ST-6 | FO-11 | build | 60 | H |
| FS-17 | P7b | Overwatch / ErrorTrap / every NRaas rebuild copy overridden (R1), installer `mRecoverMissingSims` only with `farsimt4` on | FS-16, ST-2 | overrides | selftest, repro 15 | FS-16 | FO-11 | build | 60 | H |
| FS-18 | P7c | Inventory eligibility, `SeedNPCInventory` determinism, build spacing, sidecar event log (ST-14 format), career drift | FS-17, ST-14, ME-7 | build | selftest | FS-17, ST-14 | FO-12 | build | 60 | H (nothing in .sims3) |
| FS-19 | P7d | Save matrix + repros 14-15 + 3 timing | FS-18 | queue | save matrix PASS | FS-18 | - | doc + game-run | 30 + save matrix + 4 jobs | H |
| FS-20 | P8 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). 4x proof on the WS-0 test world | FS-19, WS-4b | queue | 3 jobs PASS | FS-19, WS-0, WS-4b | - | doc + game-run | 45 + 3 jobs | H |

### 2.5 Far-object model (FO, docs/331)

| ID | Phase | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|---|
| FO-1 | P0a | `[Diag] FarObjProbe` (ts3fixes, off): read-only hook around `AlarmManager.Simulate`, due timers by callback / `ObjectRef` kind / band, `AlarmOneShot` creations, would-park census per lot, `StateMachine` creator, traffic spawns by band | docs/331 §8, ST-4 | ts3fixes build, `farobj:` lines | selftest, smoke | CP-0, ST-4 | FS-1 | build | 60 | R |
| FO-2 | P0b | `nonsim-split-report.ts --farobj` + queue `far-object-split-<mmdd>` (smoke, 3 x 10 min with `Bands=150,200,450,1000`, 2 day runs per ME-6 keys) | FO-1, ST-9 | report + queue | report self-test; queue PASS | FO-1, ST-9 | FS-2 | build (tool) + game-run | 45 + smoke + 3 x 10 + 2 x 25 min | R |
| FO-3 | P1a | Shared hold hook core (`FarHold.cs`, per ST-15): lot skip, E1 own dispatch, world-lot holds by owner; no consumer switched on | ST-15, CP-2 | ts3fixes build | selftest | ST-15, CP-2, FO-2 | FS-6 | build | 60 | H (E1 pass-through) |
| FO-4 | P1b | Park / promote / owner flag (`farobjlot`), periodic pass, eligibility (§6.1), timer census | FO-3 | build | selftest | FO-3 | FS-7 | build | 60 | H (household lots, Sims' routes, owned objects never park) |
| FO-5 | P1c | World history ring, sidecar (ST-14), save flush, **shadow mode** (census + would-park only) + 1 correctness + 3 x 10 min shadow | FO-4, ST-14 | build + queue | queue PASS, `parkedHousehold` 0 | FO-4, ST-14 | FS-8 | build + game-run | 60 + 4 jobs | H |
| FO-6 | P2a | Closed-form / seeded models: plants, food spoil, dishes, trash / litter, fishing spots, fish tanks, puddles, wildflowers (pure functions) | FO-5, ST-7a | model code + self-tests | selftest per class | FO-5, ST-7a | FS-9 | build | 60 | R (pure) |
| FO-7 | P2b | Generic recorded fires, event log + compaction, ordered materialisation, spread task, seen-in-view guard, `UpdateAlarmTime` reset | FO-6 | build | selftest | FO-6 | FS-10 | build | 60 | H |
| FO-8 | P2c | Repros 1-7, 13 + queue | FO-7 | repros + queue | queue PASS | FO-7 | FS-11 | doc + game-run | 30 + 8 jobs | H |
| FO-9 | P3a | Spawners / harvests (seeded, EA spawn at materialisation) + exposure tables for pulses, commercial included | FO-8, ST-7b | build | selftest | FO-8 | FS-12 | build | 60 | H |
| FO-10 | P3b | Door privacy, zombies, own-thread pause hooks (Tree of Prosperity first, ST-8 list) + repros 14, 16, 18, 19 | FO-9, ST-8 | build + repros | 4 repros PASS | FO-9, ST-8 | FS-14 | build + game-run | 60 + 4 jobs | H |
| FO-11 | P4a | Fire model (`farobjfire`) with E2 promotion | FO-10 | build | selftest | FO-10 | FS-16 | build | 60 | H (`fireNearHousehold` 0) |
| FO-12 | P4b | Traffic model (`farobjtraffic`), seasons lot effects + repros 8, 15, 17, 20 | FO-11 | build + repros | 4 repros PASS | FO-11 | FS-17 | build + game-run | 60 + 4 jobs | H |
| FO-13 | P5 | Shadow calibration 2 sim-days per class (BP, IP, SV 25 min) | FO-12 | queue | per-class error tables | FO-12 | - | doc + game-run | 30 + 3 x 25 min | R |
| FO-14 | P6 | Timing: `farobjlot` A/B with docs/324 off, then with T3 on (needs quiet machine) | FO-13, FS-13 | queue | per-arm ms/s, hitch | FO-13, FS-13 | none (timing) | doc + game-run | 30 + ~220 min | H |
| FO-15 | P7 | `farobjworld`: lot-less objects on `AlarmManager.Global` by `ObjectRef` band; world-lot pulses + 3 repros + timing pair | FO-14 | build + queue | repros PASS | FO-14 | - | build + game-run | 60 + ~60 min | H |
| FO-16 | P8 | Streamed-world hand-off (park as page-out precondition) + 4x proof (the 4x proof part **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world).) | FO-15, WS-8 | build + queue | 3 jobs PASS | FO-15, WS-8 | - | build + game-run | 60 + 3 jobs | H |

### 2.6 World and streaming prerequisites (WS, docs/320 §8; implementation beyond these goes to the separate project)

| ID | Goal | Inputs | Outputs | Done check | Dep | Par | Kind | Est | HH |
|---|---|---|---|---|---|---|---|---|---|
| WS-0 | **Test world build (planning, NOT STARTED, waiting on Rodney):** design a large test world on `tools/world-gen.ts`: lots of ocean, houseboats and boat routes (the IP crash and SlaveUnlink cases), dense cities, rabbit holes, community and commercial lots, far NPC homes at the docs/324 / 331 band distances; plus a scenario sheet per test (ME-2 load, ME-9 caps fill, WS-2 / WS-5, FS-20, FO-16 4x proof: which lots, households, Sims and camera tour each needs) | docs/320 §1.5, §8 a3-a5, docs/321 §7.3 fill table, docs/324 §1.3, docs/331 §1.3, Rodney's scenario list | design doc (layout, lot list, scenarios, the world-gen features it needs = WS-3 / WS-4a / WS-4b scope) | Rodney approves the design | Rodney | all | doc | 45 (after Rodney's input) | H (test households in a test save only) |
| WS-1 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). a2 triage: cause per fault ME-2 found, fix proposal per site (generator data or a hook in the separate project) | ME-5 | docs/320 §1.2 rows | each fault has a cause [verified] | ME-5 | ST-* | static | 30-60 | - |
| WS-2 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). `SceneQuadTree=4096,512,7` key test as a queue variant of ME-2 | ST-16, ME-5 | queue | 1 job | ST-16, ME-5, WS-0 | - | doc + game-run | 20 + 1 job | - |
| WS-3 | a3 grow WTXT paint data in world-gen (from ST-17), scope per WS-0 | ST-17, WS-0 | `tools/world-gen.ts` change + self-check | world-gen self-check | ST-17, WS-0 | WS-4a | build (tool) | 60 | - |
| WS-4a | a4 part 1: lots across the area (copy / offset template lots), layout per WS-0 | WS-0 | world-gen change | self-check | WS-0 | WS-3 | build (tool) | 60 | - |
| WS-4b | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). (game part) a4 part 2: R edges across quadrants, lot footprints into G; a playable 4096 m save | WS-4a | world-gen change + save | 1 load job with a household | WS-4a | - | build (tool) + game-run | 60 + 2 jobs | H (new household in a test save only) |
| WS-5 | **DEFERRED until WS-0** (Rodney 2026-11-23: no large-world tests before a purpose-built test world). a5 save / reload matrix, memory and timing vs SV on the 4096 m world | WS-4b | queue | save matrix + 3 jobs | WS-4b | - | doc + game-run | 30 + ~90 min | H |
| WS-6 | b2 step 1: write one far lot's object list to a sidecar package (no clear) | ST-10, ST-14 | build (off) | object list round-trips offline | ST-10, ST-14 | FS / FO | build | 60 | H (never a household lot) |
| WS-7 | b2 step 2: clear + restore one far empty lot, compare ids before / after | WS-6 | build + repro | repro PASS, 0 script errors | WS-6 | - | build + game-run | 60 + 3 jobs | H |
| WS-8 | b2 step 3: page-out precondition API for FO-16 (lot parked + materialised -> eligible) | WS-7, FO-15 | API note + build | selftest | WS-7 | - | build | 45 | H |

(T4 = docs/320 b1, covered by FS-16..19. b3 tile manager, b4 sidecar travel, b6 paged G and a6 wider formats belong to the
separate project and get no package here.)

### 2.7 Packages added during the split (missing facts)

Planning: WS-0 (Rodney 2026-11-23: large-world tests need a purpose-built test world first). Static: ST-7b (mods were not in the docs/331 census scope), ST-9 (no proof the harness can run a speed-3 day job), ST-10, ST-11,
ST-12 (lane A hub coverage unproven), ST-13 (no EA visit-rate source for the decay constant), ST-14 (three sidecar formats were
about to be designed apart), ST-15 (FS and FO both planned the same alarm hook), ST-16 (whether `SceneQuadTree` is settable
without a build), ST-17. Measurement: ME-0, ME-1 (docs/322 edges 50/150/400/1000 do not include the model's 200, 300 and 450 m
edges), ME-2, ME-3..ME-5 (digests), ME-6 (no day run yet: daily bursts unmeasured, docs/331 §2.2), ME-7 (docs/325 has no native
split), ME-8 (no per-lot memory number), ME-9 (caps fill only measured on 2048 m worlds), ME-10 (EA visit rate unmeasured).

## 3. Handoff format

Every package ends by appending this block to `docs/data/unbounded-packages-handoff.md` (the repo's handoff convention is
`docs/data/<topic>-handoff.md`; there is no `docs/handoffs/` folder) and updating its row in that file's status table. A fresh
agent for the next package reads that file first.

```
### <ID> <short goal> (<date> <time> PHT, <agent / model>)
- State: done | partial (what is left) | blocked (by what)
- Changed: <files, one line each>
- Branch / commit: <branch> <short sha>
- Build: <builds.json name> <sha256 prefix> <version string> | none
- Switches: <name> (default off) | none
- Counters / slots: <registry ids> | none
- Tests: selftest-all <n PASS / 0 FAIL>; native selftest <ok | n/a>; queue <name> <dry-run ok | PASS / FAIL / VOID per job>
- Household: <how the exemption is coded, or "read-only">
- 4x / streamed world: <one line, AGENTS.md rule>
- Facts found: <claim> [verified | inferred, docs/51 §n]
- Open facts / questions: <list, each with the package ID that should close it>
- Next: <package IDs now unblocked>
```

## 4. Dependency graph and critical path

```
ME-0 -> ME-3 -> CP-0 ---------------------------------------------+
ME-1 -> ME-4 ---------------------------+                         |
ST-1 --+                                |                         v
ST-3 --+-> FS-1 -> FS-2 -> FS-3 ----> FS-4 -> FS-6 -> FS-7 -> FS-8 -> FS-9 -> FS-10 -> FS-11 -> FS-12 -> FS-13
ST-13 -+               docs/327 + FS-5 --^                  ^                     |                 ^
ST-4 -> ST-15 -> FO-3 <- CP-2 <- CP-1 <- ST-12              |                     +-> FS-14 -> FS-15 |
         FO-1 -> FO-2 --^   +-------------------------------+                         ME-10 --------+
FS-13 -> FS-16 -> FS-17 -> FS-18 -> FS-19 -> FS-20 <- WS-4b <- WS-4a <- WS-0 (Rodney)   [ME-2 -> ME-5 -> WS-1 deferred behind WS-0]
FO-3 -> FO-4 -> FO-5 -> FO-6 -> FO-7 -> FO-8 -> FO-9 -> FO-10 -> FO-11 -> FO-12 -> FO-13 -> FO-14 (needs FS-13) -> FO-15 -> FO-16 <- WS-8
CP-1 -> CP-3 --+-> CP-5 -> CP-8 -> CP-9 -> CP-10      CP-0 -> CP-6 -> CP-7      ST-10 + ST-14 -> WS-6 -> WS-7 -> WS-8
CP-2 -> CP-4 --+
```

**Critical path to the first far-Sim gain verdict (FS-13), unchanged by the 2026-11-23 large-world deferral (no large-world package is on it):** CP-0 -> FS-1 -> FS-2 -> FS-3 -> FS-4 -> FS-6 -> FS-7 -> FS-8 ->
FS-9 -> FS-10 -> FS-11 -> FS-12 -> FS-13 (13 packages; ME-3, ME-4, FO-3 and FS-5 run beside it). Agent time 45 + 60 + 60 + 45 +
20 + 60 + 60 + 60 + 45 + 45 + 60 + 45 + 30 = **635 min (~10.5 h)**; game time FS-4 50 + FS-10 130 + FS-12 45 + FS-13 130 =
**~355 min (~6 h)**, plus the queued ME-1 (~60 min) that FS-4 reads. FS-8 waits for FO-3 (shared hook): start FO-3 right after
CP-2 so it is never the long pole.

**Whole program:** FO is the longest chain (16 packages, ~14 h agent, ~13 h game, FO-14 after FS-13). All game runs share one
machine and timing runs block agents, so the program is bound by **~35 h of serial game time** now (FS ~10 h, FO ~12 h, CP ~8 h,
WS ~1 h, ME ~2 h) plus ~5.5 h of large-world runs deferred behind WS-0 (ME-2, ME-9, WS-2, WS-4b, WS-5, FS-20, FO-16 proof), not by agent time (~60 h over 4-7 lanes).

## 5. Lanes

File ownership decides the lanes, so two agents never edit the same file:

| Lane | Owns | Packages (in order) | CPU |
|---|---|---|---|
| N (native) | `native/ts3native/lotstream.cpp`, `caps.h`, the caps tables | CP-1, CP-3, CP-6, CP-8, CP-9, CP-12, ME-7, ME-8 | build |
| MS (managed shared) | `mods/ts3fixes/src/FarHold.cs`, the managed lot hub | CP-2, CP-4, FO-3, CP-11 | build |
| FS (far-Sim) | `mods/ts3fixes/src/FarSim*.cs` | FS-1 .. FS-20 | build |
| FO (far-object) | `mods/ts3fixes/src/FarObj*.cs`, `nonsim-split-report.ts --farobj` | FO-1, FO-2, FO-4 .. FO-16 | build |
| T (tools / worlds) | `tools/world-gen.ts`, WS queues | WS-0 (design), WS-3, WS-4a, WS-4b, WS-6, WS-7, WS-8, ME-10 | build (light) |
| S (static / doc) | docs only, REA on game-copy copies | ST-1 .. ST-17, ME-3 .. ME-6, WS-1, WS-2, CP-0 (merger) | static |

- **At most 4 build agents at once** (agent-lanes 2+2 rule, 2026-11-17: 4 CPU-heavy lanes) from N, MS, FS, FO, T, plus up to **3
  static agents** (S) on top: **7 at once**. Recommended pairs now: N + MS (lane A, the shared prerequisite), FS + FO (FS-1..3 and
  FO-1..2 need only CP-0), S x 3 (ST-1, ST-4, ST-12 first: they feed the next build packages).
- `tools/harness/repros.json` and `docs/data/builds.json` are append-only shared files: each lane appends, the merger reconciles.
- **No agent runs during a timing run** (CP-7, CP-10, FS-13, FO-14, FS-19 timing jobs: "needs quiet machine"). Correctness runs
  allow parallel agents. Agents never wait idle for a run: commit, write the handoff, stop.
- Game runs are serial: every game-run package queues FIFO behind the chains in `node tools/harness/track.ts status`, waiter
  without the `chain-` prefix, never preempting.

## 6. Tests with no agent (queued 2026-11-23 07:42 PHT, ME-2 removed and waiter re-armed 07:56 PHT)

Only existing builds and queues; no new build. One waiter runs the two chains in order, each whatever the previous verdict:

| Chain | Queue | Set / build | Jobs | Est | Read into |
|---|---|---|---|---|---|
| `probe-out/queue/chain-unbounded-capsc-rerun-1123.sh` (ME-0) | `unbounded-caps-correct-1122c.json --only ip-capsc` (lane worktree) | unbounded-caps-1122 + asi 1122c (0.36.299-ucap2) | ip-capsc 10 min | ~20 min | docs/321 §7.4 (ME-3) |
| `probe-out/queue/chain-farmodel-bands-1123.sh` (ME-1) | `tools/harness/queues/farmodel-bands-1123.json` (this branch; = nonsim-split-1122 with `Bands=150,200,300,450,1000`, light job dropped) | nsplit1 (0.36.299-nsplit1), agent m1122 | smoke 3 min, BP / IP / SV 10 min | ~60 min | docs/324 §1.3, docs/331 §2.1 (ME-4) |

- **Waiter** `probe-out/unbounded-notests-wait-1123.sh` (armed 07:42 PHT, log `probe-out/queue/unbounded-notests-wait-1123.log`).
  FIFO: chain-sp-instantiate-guard-rerun1-1123 (running) -> `loopcost-conv3-wait-1123.sh` (conv rerun 3 + loop-cost timing, quiet
  machine) -> compat-resolve-1122f -> this. It starts once `loopcost-conv3-wait-1123.log` shows "timing chain exited" (or that
  waiter is gone), a `chain-compat-resolve-1122f*.log` shows CHAIN END / STOP (or `probe-out/queue/compat-resolve-1122f.skip`
  exists: touch it if that chain is dropped), and no real `bash .../chain-*.sh`, TS3W.exe, job or queue is seen twice 90 s
  apart. It reads `ps` for chains, because track.ts also lists other agents' `bash -c` monitors that name chain files.
- ME-2 (`large-worlds-load-1122`, 4096 m load, world install into InstalledWorlds) is out of the waiter (Rodney 2026-11-23);
  its chain script is parked as `probe-out/queue/chain-large-worlds-load-1123.sh.deferred` and nothing in the waiter or the two
  chains calls `world-gen.ts` or touches InstalledWorlds (grep and both dry-runs, 07:56 PHT).
- Dry-runs: both clean (ip-capsc as `-r2`; farmodel-bands from the nonsim worktree with the new regex case). `bash -n` clean. The band edge list is valid: up to 6 ascending edges (`unseenband.cpp` `UbParse`),
  household row fixed at 7, both reports read the edge count from the export [verified: code].
- **ETA (PHT, 2026-11-23):** spguard rerun ended 07:50 (smoke FAIL, its own lane); conv rerun 3 started 07:53, ~08:10; loop-cost
  timing (~100 min, only if the gate passes) ~09:50; compat-resolve-1122f (not armed yet, assumed ~60 min) ~10:50; then ME-0
  ~11:10, ME-1 ~12:10. Earliest (gate stops, compat ~30 min): ME-0 ~09:00, ME-1 ~10:00.
- Not queued (need a build or an agent): day runs (ME-6 needs ST-9), docs/331 far-object split (FO-1 build), every large-world test (deferred behind WS-0), the docs/330 timing (already armed by its own lane).
- Risks: ME-0 can VOID again on the same Apex present-lock ABBA (known cause, not ours).
  `loopcost-conv3-wait-1123.sh` checks the track.ts "chains:" line, which a stale `bash -c` monitor (pid 61232) keeps non-empty; if
  that waiter never starts, this one waits too (FIFO holds; the conv3 owner should switch it to the `ps` check).

## 7. 4x and streamed worlds

Every package keeps the AGENTS.md rule: radii in metres from the viewers, tables grown at use, caches keyed by world / region /
lot with the lane A hub (CP-1..4) as the invalidation point, work proportional to nearby Sims and lots, sidecar state only. The
critical prerequisites for a streamed world are lane A (CP-1..4), T4 (FS-16..19) and the lot round trip (WS-6..8c).

## 8. docs/51

No new inferred claims: the plan restates docs/324 / 331 / 320 / 321 inferences with their existing ledger numbers (§575, §576,
§582, §584, §585). The facts this doc adds (band edge parser and report generality, chain order) are [verified] from code and
logs. Packages that find new inferences reserve a § with `node tools/registry.ts reserve ledger`.
