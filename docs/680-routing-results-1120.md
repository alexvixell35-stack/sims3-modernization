# 680. Routing switches of 2026-11-19/20: the results ledger

> **Code status (2026-11-23):** A ledger as of combo `60efd9e1` (2026-11-20). Several switches it names are on main now (#605, #613, #617, #622); the rest are posted separately (2026-11-23 PRs) or not posted. §6 "Upstream state" is as of 2026-11-20.

One table of what was tested and what it showed, for every routing switch built or changed this week. Several older docs,
ini comments and repro notes still say "NOT RUN" or "UNVERIFIED in the game" for switches that have run since; **this page
wins where they disagree**. Every number below was read from the run logs (`sNNN-table.txt`, `proof1-5.txt`,
`sNNN.dirs` -> `phase-*/ts3native-full.log`, last counter line of the phase) on 2026-11-20. Code base: combo
`work/combo-1117` at `60efd9e1`.

## 0. How to read it

- **Test runs.** `route_storm` on Isla Paradiso (IP) or Bridgeport (BP), 2-4 minutes per arm; s188 is 10 minutes of natural
  play on IP. "Off" and "on" arms load the same save with the same seed. Hitch = a frame of 300 ms or more outside loading.
  Lock wait = the sim thread's waits of 1 ms or more on the routing lock `world+0x918`.
- **The off/on gain is for the whole on set, not for one switch.** The on arms of s174-s189 turn on, together (from the
  `diag: ini` lines of s174-on against s174-off): `[RouteAsync]` M1Retry, KindsUnseen=7, LendWake, TaskServe, DoorMemo,
  NoPathMemoFuzzy, NoPathMemoHousehold; `[RouteSnap]` DoorMemoX=3, DoorShare=1 (check 1 in 32); `[Discourage]`
  DeferPrivateFp, DeferPrivateFpAdd; `[SteerRO]` Retry; `[GoalValidRO]` Hits; `[PlaceTestRO]`; `[EditPathRO]` (StartGuard,
  Retry); `[ConnRoomsRO]`; `[FglFacing]`; `[FglMemo]`; `[HeightCache]`; `[WorldPrecompute]` WaterScanTable=1; ts3fixes
  npmfuzzy, npmhousehold, stucktaskflag, watchdogbusydefer, doorgen, doorshare. **Not in the on set**, although the
  `ab-arm.txt` label says so: `[RouteSpeed]` NeighbourMemo, FloodMemo and CellIndex (the on logs print
  `route speed off ([RouteSpeed] NeighbourMemo=0)` and the same for the other two).
- **Status words.** PROVEN = shadow 0 mismatches and part of an off/on gain (bundle, unless a run isolates it). CORRECT BUT
  NO GAIN = shadow 0 or exact by construction, no measurable saving. RAN WITHOUT ACTING = installed and on, but its counters
  show nothing served. NOT EXERCISED = on, the code path never ran. NOT RUN = never on in a game run. PARKED = ran, held
  back by a decision. BROKEN/FIXED = caused a crash, fixed and re-run.
- **Exact** = the game's own answer by construction (or shadow-checked equal); **not exact** = a relaxation or a
  timing change.

## 1. The off/on results

| run | world, phase | hitches off -> on (ms) | lock wait off -> on | frames >= 150 ms | other |
|---|---|---|---|---|---|
| s174 | IP storm 2 min, seed 201 | 5 (1,812) -> 0 | 12.8 s (n 409) -> 1.9 s (n 73) | 71 -> 2 | |
| s175 | IP storm, seed 202 | 7 (3,127) -> 0 | 12.2 s -> 2.5 s | 62 -> 11 | |
| s176 | IP storm, seed 203 | 8 (2,664) -> 1 (329) | 8.9 s -> 2.4 s | 58 -> 10 | the one on hitch is sim-cpu `System.UInt64.Equals` |
| s178 | BP storm, seed 205 | 0 -> 0 | 7.7 s (n 716) -> 1.3 s (n 104) | 14 -> 6 | |
| s183-s187 | BP storm, on only, seeds 204/206/207 | 0 each | 1.2-1.6 s (1,228-1,623 ms) | 4-8 | after the unseenservice fix (§4) |
| s188 | IP natural play 10 min | 12 (6,478) -> 0 | 13.4 s (n 643) -> 2.0 s (n 88) | 60 -> 6 | slow plans >= 300 ms 348 -> 20; slow plans 932 -> 123 |
| s189 | IP storm 3 min, on only, seed 152 | 1 (322) | 3.4 s (n 105) | - | the hitch is `World_ArePointsConnectedByLand` (docs/677) |

s177 (BP, seed 204) off: 0 hitches, 9.0 s lock wait; its on arm crashed after 1 minute (§4). Single runs per seed; no
repeat of the same seed off and on except s170/s171.

## 2. The ledger

| switch | what it does | default (ini at 60efd9e1) | exact | runs and key numbers | status |
|---|---|---|---|---|---|
| `[PlaceTestRO]` | answers the placement test the sim thread would wait for from a clone while a route job holds the lock | `Enabled=0` (Mode=1, Shadow=0 under it) | yes | shadow s148 10,223 + s149 37,643 = **47,866 checks, 0 mismatches**; s148 answered 40,200 in jobs; on set s174-s189 | PROVEN (bundle) |
| `[EditPathRO]` | same for the EditPath IC | `Enabled=0` | yes | shadow s151 **97,950 / 0**; on set: s174 answered 34,520 | PROVEN (bundle) |
| `[EditPathRO] StartGuard`, `Retry` | no job may start between the look and the game's lock; a dropped clone answer is retried | 0, 0 | yes (game's own call) | on set only; s170: retry reruns 11, answered after one 6 | PROVEN (bundle), no separate A/B |
| `[SteerRO] Retry` | as EditPathRO Retry, for the steering query | 0 | yes | on set only | PROVEN (bundle), no separate A/B |
| `[GoalValidRO] Hits` | the goal-hits loop answered from a clone while a job runs | `Hits=0` | yes | shadow s156 **11,227 / 0** (4,095 rejected); on set: s174 742 loops answered | PROVEN (bundle) |
| `[ConnRoomsRO]` | `GetConnectedRooms` answered from a clone while a job runs | `Enabled=0` | yes | shadow s167 97 + s168 84 = **181 / 0** (small sample); on set: s174 27 answered | PROVEN (bundle), small shadow |
| `[RouteSnap] DoorShare` (+ ts3fixes `doorshare`, `doorgen`) | an "unlocked" door answer is shared between Sims of one class | `DoorShare=0` | yes | s165 shadow **15,710 checks / 0**; s166 answer: live asks **19,987 (10.4 s) -> 4,887 (4.5 s)**, 724 checks / 0; v1 s163/s164 3,952 / 0 | PROVEN (own gain + bundle) |
| `[WorldPrecompute] WaterScanTable` | the water jump search reads ray runs from a table instead of walking cells | 0 | yes | s172 shadow **8,906,043 / 0**; s173 builds 150 in 412 searches (2 slots) -> s189 **10 in 458 (4 slots)**, upkeep 1,025 -> 276 ms | PROVEN (own + bundle) |
| `WaterScanSlots` | tables kept | 4 | - | see above | (setting of the row above) |
| `[FglFacing]` v2 | further facings of a tile whose footprint test failed are answered "no" | `Enabled=0` | yes | s157 shadow **21,752 tests / 0**; on set: s174 14,907, s189 47,181 answered | PROVEN (bundle) |
| `[FglMemo]` | failed placement tests carried to the next search of the same caller run | `Enabled=0` | **no** (doc says so) | s157 shadow **141,409 / 0**; on set: s174 78,076, s188 147,783 answered | PROVEN (bundle), not exact; see §5 item 9 |
| `[FglProf]` | per-search profile of `FindGoodLocation` | `Enabled=0` | observe only | s150 crashed (v1 stubs), v2 ran s152-s157, s189 (`Frames=16`) | BROKEN/FIXED |
| `[RouteAsync] M1Retry` | an M1Pending START whose lock try failed parks and retries instead of planning on the sim thread | 0 | yes (same plan, later) | same-seed pair **s171 off / s170 on**: M1Pending sync plans **9 -> 0**, lock wait **5.4 s -> 4.3 s**; hitches 2 (674 ms) -> 3 (1,262 ms), none M1Pending | PROVEN (own + bundle) |
| `[Discourage] DeferPrivateFp` | the private footprint removal is queued while a job runs | 0 | timing only | in every on arm s167-s189 (73-78 deferred and applied a run, tripwires 0) | PROVEN (bundle) |
| `[Discourage] DeferPrivateFpAdd` | the boat / large-animal footprint add is queued instead of flushing and waiting for the job | 0 | **no** (applied at the next drain point) | s167 (probe, mode 2) -> s168 (mode 1): flush wait before an add **2,710 -> 176 ms**; deferred 81, **lot moved 4 of 81** | PROVEN (own + bundle), not exact |
| `[RouteAsync] KindsUnseen=7` | own-task replans of unseen NPCs (kinds 1-3) start as worker jobs | 0 | yes (same plan, worker) | s148: unseen kinds 1, 2 and 3 all pass the gate (16 / 40 / 128); every on arm since | PROVEN (bundle) |
| `[RouteAsync] PlanEndLend` | the worker lends the lock at the plan's end while the sim waits | 0 | yes | s160 count only; s161 **13 of 136 windows taken** by the sim, 3 ms job time after; s162 8 of 117 | CORRECT BUT NO GAIN |
| `[RouteAsync] LendWake` | a TailUnlock window wakes the waiting sim at once instead of at its next 1 ms poll | 0 | yes | s161 147 wakes; in every on arm (s174 72, s188 90, s189 102); no isolated A/B | PROVEN (bundle), hint only |
| `[RouteJobPhases] HoldProbe` | counts where the worker's lock hold goes | 0 | observe only | s159 (docs/673 §9) and every on arm (`worker hold parts:`) | measurement, ran |
| `[RouteSpeed] NeighbourMemo` | the cell A*'s neighbour lists kept per cell until the table clear | 0 | yes | s143 shadow **105,102 / 0**; s144 served 241,287 hits, saved ~120 ms in 4 min; s146 ~8 ms, s148 ~24 ms | CORRECT BUT NO GAIN |
| `[RouteSpeed] CellIndex` | side index for the cell table lookup | 0 | yes | s144 shadow 9,543,906 / 0, s146 5,785,988 / 0, s148 6,498,600 / 0; **2.0 cells a walk**, saved 0; costs time in shadow | CORRECT BUT NO GAIN |
| `[RouteSpeed] FloodMemo` | a failed flood's region answers later searches of the same plan "no" | 0 | yes | s143-s148 mode 2: **hits 0** in every run; 0.3-1.1% of searches fail (docs/673 §2) | RAN WITHOUT ACTING |
| `[RouteAsync] NoPathMemoFuzzy` + ts3fixes `npmfuzzy` | no-path answer for a goal within 1 m of a recorded one | 0 / off | no (fuzzy) | on set: served 0 every run; s188 recorded 4, served 0 | RAN WITHOUT ACTING |
| `[RouteAsync] NoPathMemoHousehold` + ts3fixes `npmhousehold` | household scope records and is answered too | 0 / off | yes | on set: household scope let in 167-209 a run, **served 0** | RAN WITHOUT ACTING |
| ts3fixes `stucktaskflag` | the stuck check's flag no longer leaks to every task (docs/662 §2) | off | yes | on set s174-s189; no counter line is ever written to the log | RAN, NOT MEASURED (counters never dumped) |
| ts3fixes `watchdogbusydefer` | a refused watchdog check no longer plans on the sim thread (docs/662 §3) | off | yes | as above | RAN, NOT MEASURED (counters never dumped) |
| ts3fixes `SafeSpot` one-failed-search (9f20444a) | one failed fresh `FindGoodLocation` ends SafeSpot's run, remembered 30 sim minutes | always on (no switch) | yes | s152 -> s153: repeats within 10 s **133 (966 ms) -> 123 (409 ms)**; `SafeSpotBody` stack lines 31 -> 7 | PROVEN (own) |
| ts3fixes `nearestlotlazy` | `GetNearestLot` tests parking only for lots that can win (docs/677 §2) | off | yes | s189 on: **0 calls** (`nearestlot: same 0 DIFFERENT 0`) | NOT EXERCISED |
| ts3fixes `fglnonescan` | one scan instead of EA's staged whole-lot searches | off | no (relaxation) | s169 only: **3 fires** | PARKED (owner: no per-rule relaxations) |
| ts3fixes `fglnnearspot` | near-spot relaxation (docs/675) | off | no | s169 on, 0 log lines | PARKED |
| ts3fixes `followerlasttry` | last-try relaxation for followers (docs/675) | off | no | s169 on, 0 log lines | PARKED |
| ts3fixes `unseenservice` | unseen service Sims' bar order | off | yes | regression in Bridgeport (§4), fixed 7a3d4bbf | BROKEN/FIXED |
| `[HitchAttr] RouteLockCallers` | each lock wait line names its callers | 0 | log only | **s189 ran it** (`RouteLockCallers=1`): 82 lines by key and first caller, see §5 item 4 | ran (docs/670 §19 still says NOT RUN) |
| `[WorldPrecompute] Probe` (19 post passes) | times the post pass by node pass | 0 | observe only | s172, s189: pass 3 smoothing **6,901 / 7,119 ms** of the post pass | ran |
| `[WorldPrecompute] Probe` pass 3 by callee (docs/676 §13) | times pass 3's 12 callees | 0 | observe only | s195 (also s196, s198, s200): pass 3 6,179 ms / 1,779 calls; segment test 5,647 ms / 215,942 (121 a pass, 26 us each); lot test `0x643E60` 3,143 ms / 135,799; world test `0x658A00` 2,213 ms / 149,557; nudge 236 ms; `+0x72` set in 9,640 of 215,915 lot walks (docs/676 §15.1) | ran |
| `[WorldPrecompute] SmoothMemo` (docs/676 §14) | memo of quadtree cell evaluations inside one pass 3 | 0 | yes (exact) | s212 shadow: 4,348,062 checks, mismatch 0. s213 serve: 3.59 M of 9.48 M evaluations from a record, post 5,659 ms / 1,648 against s202's 5,634 / 1,672: **no gain** (the evaluator is not where the time is; the table of 4,096 fills; docs/676 §15.2) | ran, no gain |
| `[WorldPrecompute] SmoothCut` (docs/676 §15) | a blocked lot segment test ends at the blocking cell; remembered blocking cells tried first | 0 | yes (exact) | no `smooth cut` line in any run | NOT RUN |
| `[RouteAsync] SkipRetry` (docs/670 §20) | a skipped job parks and retries | 0 | yes | no `route skip retry:` line in any run | NOT RUN |
| `[SwarmGuard]` (docs/678) | guard at the Swarm effect update | `Enabled=0` | yes (skip) / repair | no `swarm guard:` line in any run | NOT RUN |

Count by status (the `WaterScanSlots` setting row not counted): PROVEN 16 (11 on the bundle gain only; 5 with a gain
of their own: DoorShare, WaterScanTable, M1Retry, DeferPrivateFpAdd, SafeSpot), CORRECT BUT NO GAIN 3, RAN WITHOUT
ACTING 3, NOT EXERCISED 1, PARKED 3, BROKEN/FIXED 2, NOT RUN 3; measurement-only switches that ran 3 (HoldProbe,
Probe's 19 passes, RouteLockCallers); ran but counters never dumped 2 (stucktaskflag, watchdogbusydefer).

## 3. What the logs did not confirm

- **DeferPrivateFpAdd "flush waits 36 -> 2".** The logs give the time (2,710 -> 176 ms, `flush wait before an add`) and
  `addFlush 73 -> 3` (s167 -> s168, the defer line), not 36 -> 2. No line of either run has 36.
- **KindsUnseen "kinds 2/3 pass".** s148's line shows all three kinds passing (16 / 40 / 128), kind 1 too.
- **Off arms vs the label.** `ab-arm.txt` of s174-s189 lists NeighbourMemo(2) and FloodMemo=2 in the on set; they were off
  (§0).
- **"0 of 5 after" the unseenservice fix.** No crash of the type before (`C0000005 at 0x00E6463F`) in s183-s187. Each of
  s184-s187 logs another `C0000005` at `0xF6xx..04F` (info `FFFFFFFF`) after the table was written; the same line is in
  s178's off arm, which nobody counted as a crash. It looks like the shutdown at the end of a run; not proven.
- **Regression commit.** The brief names c8d594a7 (2026-11-19); that commit's subject is "ci: register new switches ...".
  The ledger keeps the name; the switch change itself was not traced here.

## 4. Crashes this week

| crash | whose | runs | state |
|---|---|---|---|
| Swarm effect update `0x92E0C0`, the word at `+4` of a live `cVisualEffect` overwritten | the game's (writer unknown) | two samples (docs/678 §3) | `[SwarmGuard]` built, default off, NOT RUN |
| ts3fixes `unseenservice`: a delegate on the abstract method crashed the interpreter (`C0000005 at 0x00E6463F`) | ours, regression of c8d594a7 (2026-11-19) | Bridgeport: s177, s180, s181, s182 crashed (4 of 6, s177-s182) | fixed 7a3d4bbf (call goes to the instance's override); s183-s187 0 of 5 |
| early `[FglProf]` stubs | ours | s150 | fixed (v2 stubs, docs/671 §3.1); s152-s157, s189 ran |

## 5. Corrections: older docs that still mislead

1. **docs/667 ("Each search fails only after ... 95-136 times", line 122)** (and the FloodMemo ini comment, docs/672 items on
   "95-136 floods per failed plan"): failed plans that repeat floods are rare in practice: 0.4-1.4% of the worker's
   hold, 1-2 plans >= 100 ms a storm, 0.3-1.1% of searches fail (docs/673 §1, §2). FloodMemo never engages.
2. **docs/669 §3 ("Evidence for long chains [inferred]")**: wrong. The measured chain is **2.0 cells a walk** (s144; 1.8-1.9
   in s146/s148); CellIndex saves nothing.
3. **docs/671 §11.3 "the Sim stood outdoors"**: it was indoors. EA's indoors stage runs only when the start itself is in a
   room (docs/675 §3, stage 1).
4. **`worker hold boundaries:` plan column** (docs/673 §1 readings): it is the plan's START, not its end (docs/673 §9.1).
5. **`ArePointsConnectedByLand` and `Query_CountObjectsOnLot` freezes**: callers' loops (`GetNearestLot` over every lot,
   houseboat gangway check), not slow native calls (docs/677 §1).
6. **The ~340 ms `FindGoodLocation` frames** with FglFacing and FglMemo on: native search time (tile scan and placement
   tests), not a managed callback; the cause is open (docs/677 §3).
7. **docs/670 §19 title "RouteLockCallers ... NOT RUN"**: it ran in s189.
8. **ini comments "Built 2026-11-20, NOT RUN"** for PlaceTestRO, EditPathRO, ConnRoomsRO, GoalValidRO Hits, FglFacing,
   FglMemo, DeferPrivateFpAdd, and "OFF until an A/B" for M1Retry: replaced by the results in this ledger (ini comments
   only).
9. **FglMemo answers without `[Experimental] HouseholdToo=1`?** Its ini comment says it "answers only with
   HouseholdToo=1 and otherwise only compares"; s174-on answered 78,076 tests from the memo, and no `HouseholdToo` key is
   in the on arm's `diag: ini` lines. Either the dump leaves the key out or the comment is stale. Open; read `fglmemo.cpp`.

## 6. Upstream state

- **PR #605** (open): `[RouteSpeed] NeighbourMemo`, the route-hitch profiler, `route_storm`. A comment is posted that
  NeighbourMemo is correct (105,102 / 0) but shows no gain; recommend it stays off; the profiler and harness are the value.
- **PR #613** (open): `[PlaceTestRO]`, `[EditPathRO]`, `[GoalValidRO] Hits`, `[ConnRoomsRO]`, `[FglProf]` / `[FglFacing]` /
  `[FglMemo]`, all default off. The shadow counts of §2 are its evidence.
- **Proven, not upstream yet**, and what blocks each:

| item | blocking dependency |
|---|---|
| DoorShare (+ ts3fixes doorshare) | hostcalls / userdiag / DoorGen / UnseenFallback and the job struct are not upstream |
| DeferPrivateFpAdd | the `discourage.cpp` separation |
| LendWake, PlanEndLend, HoldProbe, WaterScanTable | the `plancand.cpp` PCC split is not upstream |
| SafeSpot one-failed-search | upstream has its own dedupe; reconcile first |
| M1Retry | already upstream, default off; recommend on with the s170/s171 evidence |
| KindsUnseen | upstream has its own gate; the equivalent is its `Kinds` bit |

## 7. Open items (ranked)

1. The ~340 ms `FindGoodLocation` search with FglFacing and FglMemo on: native time, cause unexplained (docs/677 §3).
2. Waypoint snap cost (closest-waypoint searches under `ArePointsConnectedByLand`, docs/676 §3).
3. Path smoothing, post pass 3 `0x64DD40`: ~7 s a storm (6,901 ms s172, 7,119 ms s189); the by-callee probe is NOT RUN.
4. Lot-table lock callers `0x655ABE` (key `0x62EF7A`, 13 lines in s189) and `0x834201` (key `0x62FB74`, 6 lines).
5. Remaining sync plans: `Los` (`ViewObjects.CreateRouteToObject`, s188 off), `ufplan` kinds 1-3.
6. Swarm writer unknown; `[SwarmGuard]` not run.
7. EA 1.69 / retail address rows unresolved for every new switch of this week (all rows are Steam 1.67).
8. Single-run evidence: one run per seed and arm; only s170/s171 is a same-seed pair for one switch.
9. Other worlds untested: Sunset Valley, Appaloosa Plains, Twinbrook (only IP and BP ran).
10. FglMemo answering without HouseholdToo (§5 item 9).

## 8. Sources

The run logs: `proof1.txt` (s174-s178), `proof2.txt` (s179-s182), `proof3.txt` (s183-s187), `proof4.txt` (s188),
`proof5.txt` (s189); `s143-s189` tables and phase logs. Docs 643, 662, 663, 665-678 (no 679 exists).
