# 354. Far-Sim shadow build: machine core, shadow compare, drift and visits, calibration queue (FS-1 .. FS-4, 2026-11-26)

Built, selftests pass, queue dry-run clean, **not run in game**. Packages FS-1, FS-2, FS-3 and FS-4 of docs/340 §6.3
(goals in docs/332). Spec: docs/345 (SD-1). Branch `feature/farsim-shadow-1126` on `merge/cp0-1126`.

Markers: [verified] = read in the decompiled game code or counted by a selftest in this build; [inferred] = a choice or
assumption of this build, listed in docs/51 §623; "not built" = left to the package named.

## 0. Short answer

- `mods/ts3fixes/src/FarSimModel.cs` is the machine of docs/345: 11 states, the 24 transitions as a data table, seed and
  draws, `Plan(day)`, `F(t)`, the due queue with nominal ticks, catch-up without replay, the trace hash, one drop path,
  stand-down, the kill list. It has no game types and no hooks.
- `mods/ts3fixes/src/FarSimShadow.cs` runs that machine beside EA's live simulation and only compares. It writes
  nothing to the game and hooks no EA method: one frame task polls.
- Everything is off by default. Six ts3fixes switches; no native change, no ini key. Agent: the source name list only
  (`PatchHandlers.cs`), no agent build.
- The shadow answers four questions per world: how often the machine's state matches what EA's Sim is doing, how far
  the modelled place is from the Sim, how far the needs read is from EA's motive values, and how the modelled
  relationship drift and visit proposals compare with EA's.
- Queue `farsim-shadow-1126`: BP, IP, SV, 10 minutes each. Gate: 3 jobs pass, error tables read, every household
  counter 0, script errors 0.

## 1. What each package built

| Package | Built | Not built here |
|---|---|---|
| FS-1 machine core | everything docs/345 §18 lists; selftests D1, D2, D3, D4, D5, D6 (format), D8, D9, leak churn | sidecar read and write (the line formats and the version filter are there; the file is ST-14) |
| FS-2 shadow compare | boundary pass, safe-point poll, exclusion reasons, would-be go-far and go-live, state / place / needs tables, the §6.1 "no clamp" counters, scope poll, stand-down | observation hooks (camera jump, map tag, pickers), `eaPush` hook, `farsimGoLiveDistress`, the `[Diag] FarSimShadow` ini key (a ts3fixes switch instead) |
| FS-3 drift and visits | relationship drift per pair-day against EA's change of the same pairs; visit proposals per day against NPC arrivals on the household's home lot; skill share draw (model side only) | skill and career drift against EA; EA's visit cooldowns in `ea_ok` |
| FS-4 calibration queue | `tools/harness/queues/farsim-shadow-1126.json`, repro `farsim-shadow-session`, regex cases for every key | the run itself; ME-4 edges (working values 450 / 450 / 300 m) |

## 2. Switches (all default off)

| Switch (ts3fixes, set with `patches`) | Effect |
|---|---|
| `farsim` | master. Off: no machine, no task, no table |
| `farsimshadow` | with `farsim`: the shadow runs after `Ts3Fixes.Fixes.FarSimShadow("start")` or the next world load. Turning either off releases every record in one tick and resets the tables |
| `farsimwork` | the plan has work and school blocks (`FarWork`, `FarSchool`) |
| `farsimvenue` | the plan has venue blocks (`FarVenue`) |
| `farsimvisit` | the plan proposes visits (`VisitProposed`) |
| `farsimdrift` | the snapshot keeps relationship baselines and the drift table is filled |

In this build the four feature switches only add parts of the model to the comparison. Ownership (`farsimt3`, parking,
the alarm hold, object moves, needs phase write) is FS-6 .. FS-9; `farsim` without `farsimshadow` does nothing.

Harness API `Ts3Fixes.Fixes.FarSimShadow(cmd)`: `start`, `state`, `summary <why>`, `clear`,
`set <key> <n>` for `FarRadiusM`, `LiveRadiusM`, `HandoffRadiusM`, `FarDwellMin`, `ParkedDwellMin`, `BoundaryPassMin`,
`GoFarTimeoutMin`, `GoFarRetryMin`, `FlapWindowMin`, `DueBudget`, `VenueGoPct`, `StatesOff` (docs/345 §4.2, Q7).

## 3. Household, selected Sim, unknown scope

The test is `IsPlayerSim` / `IsPlayerDesc`, not `HouseholdExempt`, so `[Experimental] HouseholdToo` is never read
(docs/345 §10). Entry points in this build:

| docs/345 | Where | What happens |
|---|---|---|
| EP1 | `FsOne` (boundary pass) | reason `household`, `selected` or `unknownScope` goes to `Machine.Boundary`: T20, the Sim sits in `Excluded` |
| EP2 | `FsOne` on the tick of a would-be go-far | the reason is asked again on that tick; a scope reason gives T2 and `hhBlockedAtGoFar` |
| EP3 | `Machine.DueOne` | `IInputs.Excl` before every due edge: T15 reason `scope`, `hhCaughtInFar` |
| EP4, EP5 | - | no object move and no needs write exist in the shadow |
| EP6 | `FsScopePoll`, every frame before the drain | active household, its size or the selected Sim changed: every record of a Sim now in scope is promoted at once |
| EP7 | `FsSnapTake`, `FsDriftCompare` | a pair with a scope Sim on either side gets no baseline and is skipped again at the compare |
| EP8, EP9 | - | no alarm hold and no sidecar in the shadow |

A Sim the machine cannot resolve (destroyed, not in the last pass, no household table) is `unknownScope`. `Audit`
(V2) counts a scope Sim found in any far state as `householdInFar` and stands the machine down. [verified by the
selftest: household row, selected, unknown scope, scope change while pending, while far, caught in the drain]

Pause, CAS, Build / Buy, Edit Town, menus, travel: the frame task does nothing while `SimFrozenOrNotLive()` holds and
counts the frames (`skippedPaused`). World quit resets every table.

## 4. What the shadow reads and what it never does

Reads (getters only): `LotManager.Actors`, each Sim's position, lot, posture, queue count, running interaction,
buffs, situations, role and service fields, `Autonomy.Motives.AllMotives` (value, `AutoUpdated`, `MotiveTimeDelta`,
the tuning's auto-satisfy curve), occupation and school hours, relationships, camera position and target, the lot list.

Never: a Sim, motive, relationship, alarm, lot or save write; a hide, park, move or reset; an interaction push or
cancel; an EA method patch. EA keeps every Sim the whole time, so "go-far" and "go-live" are trace entries.
The needs phase of a Sim whose motive has none yet is drawn and kept in the snapshot only (the real build writes it,
docs/345 §6.1, Q6 still open).

## 5. Log lines (prefix `ts3fixes: ` in ts3native.log)

`FarSimShadow("summary end")` writes, for the would-be go-lives (`near`) and the hourly sample (`hour`):

- `farsim: shadow state end <near|hour> <state> ea home:N sleep:N transit:N work:N school:N venue:N visit:N other:N`
  and `... total n N agree A pct P`: model state against what EA's Sim was doing.
- `farsim: shadow err end <near|hour> placeM n N meanabs M bias B max X buckets le25:.. over:.. samelot S of N`:
  metres between the modelled place (lot centre) and the Sim.
- `farsim: shadow err end <near|hour> need:<motive> n N meanabs M bias B max X buckets le2:.. over:..`: the machine's
  needs read (EA's own curve object at hour + phase) against the motive value.
- `farsim: shadow err end near relDrift ... modelsum M easum E scopeskipped S`.
- `farsim: shadow totals`, `household`, `bystate`, `excl`, `needs`, `visits` lines (counts).

`household` carries the hard-fail counters: `householdInFar hhCaughtInFar driftHousehold ownerConflict fViolations
illegal stoodDown`, all 0 in a passing job. Two of them cannot rise in the shadow build and prove nothing there:
`ownerConflict` (EA owns every Sim; FS-6 raises it) and `driftHousehold` (a pair with a scope side gets no baseline
and is skipped before any delta is computed; the skips are `scopeskipped` on the `relDrift` line). NPC arrivals
(`eaArrivals`) are counted for every NPC, excluded or not; only scope Sims are left out.

## 6. Counters

ts3fixes slots 1780-1827 (`fixes-1780`). 1780 + n = machine counter n: records, recordsHigh, dueSize, planCache,
dwell, goFar, goLive, goLiveFallback, noSafePoint, flap, catchUp, gone, householdInFar, hhCaughtInFar,
driftHousehold, hhBlockedAtGoFar, ownerConflict, fViolations, illegal, scopePromoted, stoodDown, visitsProposed,
visitHandover, stateOff, transitions, edgeOther. 1806-1816 Sims per state. 1817 frames skipped while frozen, 1818
would-be go-lives with a need below -40, 1819 the lowest need at one, 1820 motives whose curve minimum is below -40,
1821 passes, 1822 shadow errors, 1823 comparisons, 1824 NPC arrivals on the household's home lot, 1825 drift
pair-days, 1826 snapshots held, 1827 model version while the shadow runs. Slot 1898 (`fixes-1898`): size of the
arrival list (NPC ids now on the household's home lot).

Leak rules: record, snapshot, plan cache and due entry leave through `Machine.Release` / `Drop` only; the snapshot
through its `OnDrop` sink. The arrival list loses an id when its Sim is gone, leaves the lot or comes into scope, and
is pruned against the pass's ids at every pass end. World quit and switch off clear every table, the comparison
tables included. Selftest: 10,000 records created and destroyed and 100 load / quit cycles end at size 0.

## 7. Where this build differs from docs/345 [inferred, docs/51 §623]

| Topic | docs/345 | This build |
|---|---|---|
| reason `due` | not in §5.2 | added for a plan edge reached on time (T5-T14, T16) |
| T4 into a day with a visit drawn | T4 lands in 2-7 | T4 to `FarHome`, then T14 on the same tick |
| edges the table has no row for (`FarSleep` to `FarTransit` before an early shift) | - | logged as T22 (still `F(t)`), counted `edgeOther` |
| `Live` Sims | no record, a small dwell entry | one table; a `Live` entry holds the dwell start, the retry time and the last go-live, and is dropped once nothing waits on it. `ep` restarts when it is dropped (the sidecar `S` line will carry it) |
| near triggers | hooks | polled: the Sim is on a household lot or a lot a household Sim is on, or a household Sim's running interaction targets it |
| safe point | six conditions | the ones a getter answers: empty queue, no running interaction, standing, no parent, not routing, not in a rabbit hole, not on screen, no near trigger. Pending Sims are polled 8 per frame between passes |
| exclusions | 17 reasons | not evaluated: `ageUp` within `PrebuildHours` (only "in progress"), `pendingEvent`, `heldClass`, `modOwned`, blood relatives in `tie`. `timedBuff` is the strict rule (any buff with a timeout). Pets and Sims without a household are excluded |
| work hours | EA's `IsWorkHour` answer per day | start hour, finish hour and work days of the current level; a shift is cut at midnight |
| venue | open-lot list, venue open (I10) | every community lot, opening hours not asked |
| travel time | car, bike or walk from the description | car speed for every Sim; speeds 15 / 6 / 1.6 m per sim minute are first values |
| visit weight and `ea_ok` | docs/324 §3.9 | per mille per day = 2 x the best liking to a household Sim; `ea_ok` = not before 05:00 |
| world seed | sidecar `W` line | per session, from the clock and the world (docs/345 §14 allows it without a sidecar) |
| trace file, hash per sim hour | `[Diag] FarTrace` | not built (DT-2); the machine keeps lines only when asked (selftest, DT-1) |
| ring | SD-3 / FS-11 | 0; the clamp `R_live + ring <= R_far - 50 m` is in `Cfg.NearCm` |

## 8. Selftest results [verified]

`node tools/selftest/farsim/run.ts`: docs/345 §8.2 vector and the §13.1 hash example reproduce; same seed twice, a
shuffled Sim order and `DueBudget` 1 / 8 / unlimited give one trace hash (120 Sims, 12 sim days); lazy `F(t)` equals
the stepped state for 1000 Sims over 30 sim days; catch-up after 1, 3 and 30 blocked days equals the stepped state;
every transition in every run is a table row; plans have at most 12 blocks. `F(t)` with a cold plan costs a few
microseconds per Sim on the build machine (printed by the selftest; the FS-11 spread budget takes it from there).

## 9. 4x and streamed worlds

No world-size constant: radii are metres in `Cfg` (per-world far radius read at start), the pass is sliced by count
(24 Sims per frame), tables are sorted lists keyed by `SimDescriptionId` that grow with the Sims present, and every
table resets on world quit. The pass walks `LotManager.Actors`, so its cost follows instantiated Sims; a streamed
world changes that list and the T24 poll drops records of Sims that leave it.

## 10. Queue

`tools/harness/queues/farsim-shadow-1126.json` (written, dry-run clean, not run): `bp-farsim-shadow`,
`ip-farsim-shadow`, `sv-farsim-shadow`; master set m1126b asi and ini with `ts3fixes.package.farsim-shadow-1126`;
story progression on; Bin state `apex`. Each job turns all six switches on, starts the shadow, plays about 9 sim
hours, sends the active Sim to the farthest community lot (far Sims there come near) and writes the summary.

Read after the run, per world: `agree pct` of the `hour` and `near` state tables, the `placeM` and `need:` tables,
`noSafePoint` against `goFar` (what waiting for a safe point costs), the `excl` counts by reason (how many Sims can
go far at all), `goLiveBelow40` against `eaBelow40`, `proposed` against `eaArrivals`.

## 11. Open questions

| # | Question | Who |
|---|---|---|
| 1 | A sleeping or busy Sim never has an empty queue, so go-far only happens between interactions. If `noSafePoint` is high, does the real build need the queue-empty hook, or a wider safe point (sleeping in the own bed)? | FS-7, after the run |
| 2 | `timedBuff` strict rule: how many Sims does it keep live (the `excl` line shows it)? | docs/341 Q3 |
| 3 | Is the hourly sample of the needs fair? EA updates a far Sim's motives on its own schedule, so the two reads can be up to one update apart | FS-4 report |
| 4 | Should the shadow get the `[Diag] FarSimShadow` ini key so it covers the world load, or is the switch enough? | orchestrator |
