# 358. FarLight and the FarHold core: build notes (FO-3 part 1, FO-4 FarLight part) (2026-11-26)

Built, selftests pass, queues written and dry-run, NOT RUN in the game. Branch `feature/farlight-farhold-1126` (rebased on
master after the rule review). Specs: docs/350 (far-object machine, `FarLight`), docs/346 (hold hook), docs/351 (names),
docs/348 (lot hub gaps), docs/340 FO / MS rows. Ledger: docs/51 §627. This note only says what was built from them and
where the build differs.

**For the merger:** copy §7 into docs/351 §8 (a pointer is there), and regenerate the "next free" row of docs/registry.md
(`node tools/registry.ts fix-next docs/registry.md`): this branch's row says 1898+ and other lanes hold reservations.

## 0. Short answer

- **FarLight is built**: a lot past the far edge (200 m from the nearest viewer, docs/322 points) for 30 sim minutes,
  with no Sim on or near it and on no never-far row, skips `Lot.UpdateReactions`, `Lot.UpdateNumZombiesOnLot` and
  `Lot.UpdateDoorPrivacy`. Alarms, detail priority and display level run as EA wrote them. No model. Leaving FarLight
  is docs/350 L6: pulse timers reset, the skipped zombie time paid once.
- **FarHold core is built**: the owner table (parked lots, held Sims, one owner tag per id), Hook A on
  `AlarmManager.Simulate` for the parked-lot case, release on world quit, switch off, Edit Town and lot gone, the
  exemption re-read on every call. Nothing in play parks a lot yet (that needs the FO-4 model); a test entry parks one.
- **FarHub is built**: one registry of lanes, world load / quit order, the pause gate, a 2 ms frame budget.
- Six ts3fixes switches, all default off: `farhold`, `farholdshadow`, `farobj`, `farobjlight`, `farobjshadow`,
  `farobjpulseprobe`. With all off there is no task, no record and each hook is a generation compare and the original.
- Counters 1834-1897 (`fixes-1834`), agent names in camelCase as docs/351 §6. Selftests `tools/selftest/farhold`, `farhub`, `farobj`.
- Builds: docs/data/builds.json rows `ts3fixes.package.farlight-farhold-1126` and `ts3agent.package.farlight-farhold-1126`
  (the hashes are there; the copies in `game-copy/` carry the same names). No native change: asi and ini are master set m1126b.
- Queues: `farhold-farobj-correct-1126` (11 jobs, ~150 min), `farhold-farobj-time-1126` (ABBA BP / IP / SV, needs
  quiet machine, ~100 min). Both dry-run only.
- Not built: take mode for held Sims and pass-through timers, Hook B, `FarParked`, sidecar, `FarSave` (§6).

## 1. Files

| File | Lane | Content |
|---|---|---|
| `mods/ts3fixes/src/FarHoldCore.cs` | MS | owner table, conflict rule, pass-through set, Hook A decision. No EA types |
| `mods/ts3fixes/src/FarHold.cs` | MS | Hook A, `FarLotExempt`, the docs/351 §2 API, `FarHoldDiag` |
| `mods/ts3fixes/src/FarHubCore.cs` | MS | lane registry, event order, frame budget. No EA types |
| `mods/ts3fixes/src/FarHub.cs` | MS | world events, `FarHubPaused`, frame task, `FarViewerDist`, `FarHubDiag` |
| `mods/ts3fixes/src/FarObjModel.cs` | FO | lot record, states and transitions of the FarLight part. No EA types |
| `mods/ts3fixes/src/FarObj.cs` | FO | boundary pass, never-far rows, the three pulse hooks, pulse probe, `FarObjDiag` |
| `tools/harness/farobj-report.ts` | FO | per-arm pulse ms/s by distance band from `<tag>-ab-fixes.jsonl` |

## 2. FarHold and FarHub as built

Hook A (`FhAlarmHook.SimHook` on `AlarmManager.Simulate`), per call:

1. switch generation changed: re-read `farhold` / `farholdshadow` (every switch write and every world load bumps it);
2. parked set empty, or both switches off: the original;
3. the manager's lot id (`AlarmManager.mLotId`, reflection) is in the parked set: `FarHoldCore.Decide`:
   exempt lot = removed from the set and logged (docs/350 EP5), simulated; frozen = simulated; pass-through set not
   empty = simulated (take mode is not built); shadow = counted, simulated; else **not simulated** (docs/346 §3.2 step 2).

`FarLotExempt(lot)` is true for: no lot, a destroyed lot, the world lot, no active household, the active lot, the home
lot, no selected Sim, the selected Sim's lot, a lot a household Sim is on, a lot holding any Sim for which `IsPlayerSim`
is true (selectable or unknown scope), any exception. `[Experimental] HouseholdToo` is not read (docs/346 §3.6).

API (docs/351 §2), all on `Fixes`: `FarHoldSim` (always false here: take mode not built), `FarReleaseSim`,
`FarIsSimHeld`, `FarParkLot` (false when the switch is off, the hook is missing or the lot is exempt; in shadow the id
is tabled for counting and the answer stays false), `FarReleaseLot`, `FarIsLotParked` (false in shadow and for an
exempt lot), `FarReleaseAll(reason)`, `FarPassThrough`, `FarHoldDiag`. `FarHoldDiag("parkfar")` only takes a lot that is
not exempt, on no never-far row of the FarLight pass (§3) and empty. Without `AlarmManager.mLotId` Hook A is not patched
(logged) and FarHub still starts.

FarHub: lanes call `FarHubRegister(owner, load, flush, reset, step, lotGone)` once and `FarHubWant(owner, on)` when
their switch state changes. The frame task exists only while some lane wants it. Per frame: nothing while
`FarHubPaused()`; nothing in a frame over 25 ms (frame length from `SrcNowUs`), except that the 8th such frame in a row
steps with a 0.5 ms budget (the count restarts on a normal frame, on pause and on world quit; recorded in docs/331 §7
and docs/351 §4); else every lane's `step(more)` in the order FS, FO, LZ inside one 2 ms budget. World quit, travel, quit to menu: every flush, `FarReleaseAll(worldQuit)`, every reset. All switches off:
the same with reason `switchOff`, on the first running frame, then the task ends. Entering Edit Town: the same with
reason `standDown`.

## 3. FarLight as built

States kept: `GoFarPending` and `FarLight` have a record (lot id -> state, dwell start, lot circle); `Live` and
`Excluded` lots have none. Transitions of docs/350 §5.1 built: L1, L2, L4, L6, L11, L12, L13, L14.

The pass and its records run with `farobj` alone; `farobjlight` (or the shadow) only decides whether a lot at its dwell
takes FarLight. So the off arm of an A/B on `farobjlight` keeps every record and its dwell, and the on arm skips again
from its first pass.

**Boundary pass** (docs/350 EP1 + EP2 in one call), every 5 sim minutes, lots in id order, sliced by the hub budget
(at least 7 lots per frame). At the start of every slice the never-far rows and every Sim's position are read fresh;
nothing yields inside a slice, so a row is read on the tick of the transition it guards.

| Row (docs/350 §4.3) | Built as |
|---|---|
| X1 | `Household.ActiveHousehold.LotHome` |
| X2 | `LotManager.ActiveLot`, the selected Sim's `LotCurrent` |
| X3 | `LotCurrent` of every Sim in `LotManager.Actors` for which `IsPlayerSim` is true |
| X4 | for those Sims: the lot of every queued interaction's target, the work lot (`Sim.CareerLocation`), the school lot |
| X5 | only through X4 (interaction targets). Owned objects placed elsewhere, cars, reference lists: not checked |
| X6 | lots of the active household's `RealEstateManager.AllProperties` |
| X7 | the lot of every `Situation` in `Situation.sAllSituations` (any situation, household or not) |
| X8 | no active household, no selected Sim, or any row read that throws: every lot excluded for that slice |
| X9 | `Lot.IsWorldLot` |
| X10 | `Lot.FireManager.FireCount > 0` (any fire) |
| X11 | Edit Town drops everything (FarHub); Build / Buy and CAS freeze the pass; map-view or lot-info observation: not checked |
| X12 | not checked |
| X13 | covered by the dwell: a new lot needs 30 sim minutes of passes before it can take FarLight |
| ours | houseboat lots are never far (their position moves) |

G-sim: a lot with any Sim or animal on it (`GetAllActorsCount`) stays live. For a lot at or past its dwell, every Sim's
position is also tested against the lot's circle plus `R_pulse` (the largest `BroadcasterParams.PulseRadius` on the
lot, at least 10 m). Every Sim counts: no far-Sim model owns any yet.

**Per pulse** (docs/350 EP4), for a `FarLight` lot: frozen (`FarHubPaused`, read live on every call, before any cached
answer) = body runs. Else once per lot tick (the three hooks share the answer): a viewer within 150 m of the lot's
circle, scope not fully readable, the lot being the home / active / selected-Sim lot or a household or player Sim's
current lot, or any actor on the lot = body runs **and the record is dropped in that call**. Viewers are the camera
target and position, the active household's Sims, the selected Sim and every Sim outside the household for which
`IsPlayerSim` is true (listed by the pass). The scope counts as known only when every read worked: the flag is set
false first and true as the last statement, inside a try; a listed Sim that is gone makes it unknown until the next pass. Otherwise the body is skipped (shadow: counted and run). Every 16th skip is cross-checked with
`FarLotExempt`; a hit logs `farobj: HOUSEHOLD CHECK MISSED`, drops the record and counts 1874, which must stay 0. A lot
that is being destroyed is dropped there without counting.

**Leaving FarLight (docs/350 L6)**, only for a lot on which a pulse was really skipped (never in shadow):

- Pulse timers: `ReactionBroadcaster.mTimeSinceLastSimulate` of every broadcaster of the lot is set to 0 (counter 1897).
- Zombie body: every skipped `UpdateNumZombiesOnLot` call adds its `timePassed` to the record. At go-near the sum moves
  to a per-lot row and is added to the `timePassed` of the lot's next zombie call, once (counter 1896), in the lot's own
  task, where EA's body may yield. So the search interval and `ZombieDanceTimeOut` see the same total time as in vanilla.
  Rows of lots that are gone are pruned at the end of each pass; world quit drops them; 1879 counts the rows.
- Door privacy needs nothing: `UpdateDoorPrivacy` clears `mbNeedPrivacyUpdate` only when it runs, so a skipped update
  runs on the first live tick.
- `Lot.Simulate` computes `timePassed` from `mLastTime` before the call, so no accumulated time arrives on the first
  live tick [verified, decompiled `Lot.cs`].
- Callers [verified by grep of the three script assemblies]: `UpdateReactions` and `UpdateNumZombiesOnLot` are called
  only from `Lot.Simulate`; `UpdateDoorPrivacy` also from the `PortalPathPlan` remove accessor, which sets the flag first.

Not paid back [inferred, I-627-2, I-627-7]: zombie time still owed when the game is saved is not in the save (no
`FarSave` yet), so after a reload that lot's time-out is late by the owed minutes.

Hook order: the three FarLight hooks are swapped in at start-up after `doorprivacynull`; WakePark's and HitchCause's
hooks on the same methods are swapped later (tuning time) and so wrap ours. A skipped pulse skips `doorprivacynull`'s
copy too, and the outer probes still see the call.

## 4. Switches, counters, read-backs

| Switch (tuning) | Default | Meaning |
|---|---|---|
| `farhold` (`kFarHold`) | off | a parked, non-exempt lot's alarm manager is not simulated |
| `farholdshadow` (`kFarHoldShadow`) | off | diagnostic: the same decision is counted, the manager is simulated; wins over `farhold` |
| `farobj` (`kFarObj`) | off | far-object master; alone it runs the pass and keeps records, and changes nothing in the game |
| `farobjlight` (`kFarObjLight`) | off | FarLight pulse skip (needs `farobj`) |
| `farobjshadow` (`kFarObjShadow`) | off | diagnostic: pass and records run, pulses counted as would-skip; wins over `farobjlight`. Stands in for `[Diag] FarObjShadow` of docs/350 §14 until FO-5 |
| `farobjpulseprobe` (`kFarObjPulseProbe`) | off | diagnostic: every 16th `UpdateReactions` body timed by docs/322 band (needs `farobj`) |

| Slots | Agent names (camelCase, docs/351 §6) | Queue check |
|---|---|---|
| 1834-1847 | `farHoldLots`, `farHoldLotsHigh`, `farHoldSims`, `farHoldParks`, `farHoldReleases`, `farHoldReleaseAlls`, `ownerConflict` (shared), `farHoldSkipped`, `farHoldWould`, `farHoldEvicted`, `farHoldRefused`, `farHoldPassBlocked`, `farHoldErrors`, `farHoldGone` | `ownerConflict` 0, `farHoldErrors` 0, `farHoldLots` 0 at the end; shadow: `farHoldSkipped` 0 |
| 1848-1855 | `farHubLoads`, `farHubQuits`, `farHubSteps`, `farHubPausedFrames`, `farHubOverFrames`, `farHubErrors`, `farHubBudgetOut`, `farHubDrops` | `farHubErrors` 0 |
| 1856-1867 | `farobjRecords`, `farobjRecordsHigh`, `farobjByState0..5` (Live, GoFarPending, FarLight, FarParked, PendingLive, Excluded), `farobjPasses`, `farobjLight`, `farobjGoNear`, `farobjGone` | `farobjRecords` 0 after switch off |
| 1868-1873 | `farobjSkipReact` / `Zombie` / `Door`, `farobjWouldReact` / `Zombie` / `Door` | shadow: skips 0 |
| 1874 | `farobjHhPulseSkip` | **0, hard fail** |
| 1875-1884 | `farobjHhCaughtLight`, `farobjSimArrive`, `farobjNearCall`, `farobjBlockSim`, `farobjOwedLots`, `farobjExclHousehold`, `farobjExclOther`, `farobjErrors`, `farobjPassUsMax`, `farobjDrops` | `farobjErrors` 0 |
| 1885-1895 | `farobjProbeUsB0..B4`, `farobjProbeNB0..B4`, `farobjProbeYielded` | timing |
| 1896-1897 | `farobjCatchUp` (lots whose owed zombie time was paid), `farobjTimerResets` (broadcaster timers reset) | shadow: both 0 |

Leak rules: tables hold ids and numbers only; one drop function (`FarObjModel.Drop`) removes a record and releases the
lot from FarHold; records of lots the pass no longer finds are swept; world quit, Edit Town and switch off empty every
table; the player-Sim list (Sim references) is rebuilt at every pass slice and dropped on reset; size counters 1834,
1856 (with high-water marks) and 1879; selftests churn 10,000 lots and 100 load / quit cycles to 0.

Read-backs (reflect_call): `Fixes.FarHoldDiag`, `Fixes.FarHubDiag`, `Fixes.FarObjDiag` print one line each with the
switch values and counts (`ts3fixes: farhold: ...`, `farhub: ...`, `farobj: ...`). The queues' regex cases are built
from these format strings (allowed by the regex rule), not captured from a run [inferred, I-627-4]: replace them with
real lines after the first run. The version case is an instance of ts3native's version line format; its time is the
run's. Rewind: ts3fixes counters and switch values come back to the snapshot (docs/270), so every rewound job counts
from 0 and the shadow checks (`skipped 0/0/0`, `farHoldSkipped<=0`) hold after a live job.

Agent pin: `tools/harness/queue.ts` now checks the `agentPackage` file against docs/data/builds.json by its base name
(sha256 mismatch = the job refuses, not registered = warning). Harness change on this branch: merge between game runs.

## 5. Scaling (docs/320 rule)

Tables are dictionaries keyed by lot id; no size is tied to the world. Per frame the pass does at most 2 ms. One
shortcut: each slice reads every Sim, and a lot at its dwell is tested against every Sim (Sims x candidate lots per
pass). At 4x lots and Sims that is ~16x the tests, spread over more frames by the budget, so passes get slower, not
frames longer. A grid by world cell is the fix when a world has thousands of lots. A streamed world needs the lot hub
events (docs/348 §6.1) in place of the sweep.

## 6. Gaps against the specs

| Gap | Effect | Owner |
|---|---|---|
| Take mode (docs/346 §3.2 steps 3-4), Hook B on `AlarmManager.Write` | no Sim can be held; a parked lot with a pass-through timer is simulated | MS (FO-3 part 2), before FS-3 |
| `FarParked`, `PendingLive`, model, due queue, seed, sidecar, `FarSave` | `G-model` always fails: lots stop at FarLight. No flush before a save: a test-parked lot's queue is untouched, but zombie time owed by FarLight lots is not paid before the save (late time-out after a reload, I-627-7) | FO-4 .. FO-7, MS |
| Lot hub events (docs/348 §6.1) not used | a parked id of a gone lot is found by a sweep every 64 frames; a record by the next pass | CP / MS |
| X5 beyond interaction targets, X11 observation, X12 mod rows | not needed for exactness of FarLight (the per-pulse check covers household Sims), needed before a lot parks | FO-4 |
| Routes towards a FarLight lot are not a near trigger | the lot goes live when the Sim is on it or within 150 m (household) / at the next pass (NPC within `R_pulse`) | FO-4 |
| NPC within `R_pulse` but not on the lot, between passes | that NPC gets no reaction from the lot's broadcasters for up to 5 sim minutes; never a household Sim (150 m rule) | accepted, uncontrolled Sims |
| `farobjFlap` | not counted: with a dwell of 10 minutes or more a park within 10 minutes of a go-near cannot happen | - |
| Frames over 25 ms | docs/331 §7 says no step; here the 8th long frame in a row steps with a 0.5 ms budget, so a slow game is not starved (recorded in docs/331 §7 and docs/351 §4) | MS |
| docs/351 §8 names | listed in §7 below, to be copied into docs/351 §8 at merge (pointer left there) | merger |

## 7. Names added (for docs/351 §8)

| Name | File | Package | Date |
|---|---|---|---|
| switches `farholdshadow`, `farobjshadow`, `farobjpulseprobe` | `Instantiator.cs`, `Fixes.cs` | FO-3 / FO-4 | 2026-11-26 |
| `FarHubRegister` sixth argument `lotGone`, `FarHubWant(owner, on)` (owner 0 = farhold) | `FarHub.cs` | FO-3 | 2026-11-26 |
| `FarHubPaused()` wraps `SsState() != 1` of `LotHub.cs` (docs/351 Q2) | `FarHub.cs` | FO-3 | 2026-11-26 |
| `FarViewerDist`, `FarSwGen`, `FarHubDiag`, `FarObjDiag` | `FarHub.cs`, `FarHold.cs`, `FarObj.cs` | FO-3 / FO-4 | 2026-11-26 |
| drop reason `standDown` for Edit Town | `FarHub.cs` | FO-3 | 2026-11-26 |
| counter names `farHold*`, `farHub*`, `farobj*` and the shared `ownerConflict` (agent, camelCase as docs/351 §6) | `PatchHandlers.cs` | FO-3 / FO-4 | 2026-11-26 |
| `FoViewerDist`, `FoNeverFar` (never-far rows outside a pass, used by `FarHoldDiag`) | `FarObj.cs` | FO-4 | 2026-11-26 |
| repros `far-readback`, `farobj-light-visit`, `farobj-save-reload`, `farobj-zombie-return`, `farhold-park-testlot`; queues `farhold-farobj-correct-1126`, `-time-1126` | `tools/harness` | FO-3 / FO-4 | 2026-11-26 |

## 8. Queues

- `farhold-farobj-correct-1126` (run first, 11 jobs): smoke (every shadow on, 3 min, stops the queue), BP shadow 10
  min, BP live 10 min, visit repro in shadow and live (the selected Sim crosses town through FarLight lots), zombie
  return (I-627-2), FarHold test lot in shadow and live, IP and SV live 10 min, save and reload with `farobjlight` and
  `farhold` on (I-627-5; fresh start, no rewind). Every job has a `farobj: counts` read-back line with its switch
  values (natural jobs through repro `far-readback`). Pass: 0 script errors, `farobjHhPulseSkip` 0, no error counter,
  shadow jobs skip nothing and reset no timer, live jobs skip.
- `farhold-farobj-time-1126` (after correctness, quiet machine): 25 min ABBA `--ab farobjlight` on BP, IP, SV with the
  master switch and the probe on in both arms. `node tools/harness/farobj-report.ts <tag>-ab-fixes.jsonl` prints the
  switch values and FarLight lot count per slice and pulse ms/s per arm and band; it exits 1 when a slice ran with
  other switch values or an on slice ended with no FarLight lot.
  Estimate docs/350 §15 [inferred]: 16-23 / 19-29 / 6-10 ms/s (BP / IP / SV); see I-627-3 for why the part reachable
  before far Sims exist may be smaller.
- Not written: the save matrix. It is the gate for any ON decision (the hooks sit on yielding lot-task methods).
