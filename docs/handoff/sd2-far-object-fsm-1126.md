# SD-2 handoff: far-object state machine spec (docs/350)

Path note: `docs/handoff/` as the task asked (same folder as the SD-1 handoff); docs/340 §2 names `docs/handoff/`.

### SD-2 Far-object deterministic state machine spec (2026-11-26 PHT, sub-agent / Fable)
- State: done (design doc; nothing built, nothing run)
- Changed: `docs/350-far-object-state-machine.md` (new), `docs/data/far-object-class-machines.csv` (new, 691 rows:
  every census class with route, machine and release action), `docs/index.md` (one line),
  `docs/340-far-state-machine-rc-plan.md` (SD-2 row marked done), this file
- Branch / commit: master, see `git log -1 -- docs/350-far-object-state-machine.md`
- Build: none
- Switches: none added. Named for the builders (all default off): ts3fixes `farobj` (master), `farobjlight` (new:
  pulse skip only), `farobjlot`, `farobjtarget` (new), `farobjwalkers` (new), `farobjfire`, `farobjtraffic`,
  `farobjworld`, `farobjpet` / `farobjcage` / `farobjtree` / `farobjcrowd` / `farobjholo`, shared `farhold`,
  `[Diag] FarObjShadow`, `[Diag] FarObjProbe`, shared `[Diag] FarTrace`, `[FarObj] ClassesOff=<list>`,
  `[FarObj] FlushAtSave` (1; 0 is reserved and refused), `[FarObj]` radii, dwell, caps and budgets
- Counters / slots: none reserved; names in docs/350 §13.3, slots to be reserved by FO-4 / FO-5 with
  `tools/registry.ts`. Doc number 350 reserved through `tools/registry.ts` (id `doc-350`); the reservation line sits
  in the uncommitted `docs/data/registry-reservations.json` with other lanes' lines and was not staged
- Tests: none (doc only). Owed by the builders: D1-D12 of docs/350 §13.2 and the M1-M23 matrix of §18
- Household: §4.3 never-far list X1-X13 (home lot, active lot, any lot a household, selected or unknown-scope Sim is
  on, routes to or references through an object, household-owned venues, world lot); the check is repeated at 10
  entry points (EP1-EP10, §10); `[Experimental] HouseholdToo` is read at none of them; five household counters are
  hard queue fails and trigger an automatic stand-down
- 4x / streamed world: a parked lot without a due edge costs nothing per tick; go-near is one evaluation whatever
  the far time. The save flush is the one cost that still grows with the number of parked lots (§9.4, Q1)
- Facts found:
  - The 93 global no-owner rows of docs/346 Q4: 59 static, 34 instance callbacks [verified by scan of the callback
    declarations]. Split: 65 world / household singletons (stay EA's), 14 global walkers (need a filter), 11 instance
    callbacks (could be held by the lot of their delegate target), 1 lot-manager row, 2 interaction-scoped
  - `Lot.RunTriggerVolumeCallbacks` (0.1 sim-minute repeat) is registered on the lot's own alarm manager
    [verified, `Lot.cs:9668`], not on the global one as the census CSV said. The census row was corrected on the coordinator's instruction (now "lot manager (explicit)"; docs/346 §4.2 / §4.3 printed totals not touched)
  - `Wildflower.OnGrowWildflowersOnLot` pops one lot id per hour from a static list and creates a flower on that
    lot; the active household's home lot is put first [verified, `Wildflower.cs:434-460`]
  - Of the 588 lot rows only 327 need a generic machine and only 75 of those are repeating rows that need a replay;
    120 are interaction- or situation-scoped and cannot be armed on a lot that passes the safe-point guard
  - 77% of global-manager census sites have a Sim owner, so the far share of the no-position alarm time that the
    object model can remove is nearer 10-20% than docs/331 §10's 30-80% [inferred from site counts, not time]
- Design decisions (docs/350 §1): `FarLight` state; no E1 own dispatch (a lot with an armed E1 timer does not
  park); operation-count budget instead of 2 ms; PRNG keyed by nominal due tick, so no sidecar carry-over; rabbit
  holes `FarLight` only in model version 1; any object entering or leaving a parked lot from outside the model
  promotes it (`extWrite`)
- Open questions (docs/350 §17):
  - Q1 closed (Rodney 2026-11-26): full flush stays on EA worlds; the no-flush mode is Boundless Atlas
    `docs/007-paged-lot-save.md` (Atlas commits 6b4c6f3, ca1ec5c): pages outside `Saves` under `ts3native\lw\<key>\`,
    unfurnished far lots accepted when the mod is missing. The `L` sidecar line proposal is dropped
  - Q2 closed (Rodney 2026-11-26): replay of long-tail repeating timers up to the counted cap of 32 is accepted
  - Q3 closed (Rodney 2026-11-26): lots with one of the six unmapped repeating Sim hand-off sites armed stay at
    `FarLight` (`G-model`, `farobjBlock.handoff`); no re-arm with no effect. No question is left for Rodney
  - Q4 target-owner rule (ST-15 / FO-3; docs/351 does not have it); Q5 pulses that act on non-Sims; Q6 rabbit-hole
    schedules; Q7 read the 29 "shows UI" lot rows; Q8 EA setter table per named class (docs/347 is Sim-side only);
    Q9 which world singletons write onto arbitrary lots; Q10 registers; Q11 catch-up day cap; Q12 mods that walk all
    objects; Q13 trigger-volume cost; Q14 first key values
- Next: FO-4 can start from docs/350 §19 once FO-3 (`FarHold`, docs/351 names) exists; `farobjlight` needs only the
  three pulse hooks and Q5, and is the earliest item that can be timed
