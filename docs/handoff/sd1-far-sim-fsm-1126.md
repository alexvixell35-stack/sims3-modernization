# SD-1 handoff: far-Sim state machine spec (docs/345)

Path note: docs/340 §2 names `docs/handoff/`; the task asked for `docs/handoff/` (the older folder, where the
docs/341 handoff also sits), so this file is here.

### SD-1 Far-Sim deterministic state machine spec (2026-11-26 PHT, sub-agent / Fable)
- State: done (design doc; nothing built, nothing run, no new REA)
- Changed: `docs/345-far-sim-state-machine.md` (new), `docs/index.md` (one line), `docs/340-far-state-machine-rc-plan.md`
  (SD-1 row marked done), this file
- Branch / commit: master, see `git log -1 -- docs/345-far-sim-state-machine.md`
- Build: none
- Switches: none added. Named for the builders (all default off): ts3fixes `farsim` (master), `farsimt3`,
  `farsimvenue`, `farsimwork`, `farsimvisit`, `farsimdrift`, `farsimt4` (SD-4), shared `farhold` (docs/346),
  `[Diag] FarSimShadow`, `[Diag] FarTrace`, native `[FarSim] Enabled` plus `[FarSim]` radii, dwell, budgets and
  `StatesOff=<list>`
- Counters / slots: none reserved; names in docs/345 §13.3, slots to be reserved by FS-1 / FS-2 with
  `tools/registry.ts`. Doc number 345 reserved through `tools/registry.ts` (id `doc-345`); the reservation line sits
  in the uncommitted `docs/data/registry-reservations.json` with other lanes' lines and was not staged
- Tests: none (doc only). Owed by the builders: D1-D9 of docs/345 §13.2 (PRNG test vector, same-seed hash,
  shuffled order and budget, lazy against stepped, catch-up, save round trip, in-game replay, version bump, static
  ban list) and the M1-M19 matrix of §17
- Household: household Sims, the selected Sim and Sims of unknown scope are only ever `Excluded`; the check is
  repeated at 9 entry points (EP1-EP9, docs/345 §10); `[Experimental] HouseholdToo` is read at none of them; four
  household counters are hard queue fails and trigger an automatic stand-down
- 4x / streamed world: no per-tick work per far Sim; cost follows transitions due now (about 4-8 per far Sim per sim
  day [inferred]); catch-up is one evaluation of `F(t)` whatever the far time; radii in metres from the ini or the
  sidecar `K` line, no world-size constant
- Facts found (design decisions; RE facts are cited from docs/341, 342, 346, 347):
  - Far state = pure function `F(seed, snapshot, recorded inputs, t)`; the due queue only delivers side effects, and
    logs nominal due ticks, so budget and evaluation order cannot change the trace
  - PRNG keyed by (episode, day, plan revision, slot), not by a running transition index: docs/340 §3.4 wording is
    refined, because a running index makes results depend on how many transitions were replayed
  - Needs: EA's auto-satisfy curve plus one phase per motive; one write at go-far (the phase), none at go-live. This
    replaces the decay integration and the meal / hygiene / bladder events of docs/324 §2.2
  - Alarms: nothing of ours; `FarHold` add at go-far, `FarHold.Release` at go-live (docs/346). `farsimt3` needs
    `farhold` on
  - Go-live starts from idle: no interaction is pushed (docs/324 §3.3 allowed one); the only push is EA's `VisitLot`
    after a visit go-live
  - The machine never cancels an interaction to reach a safe point (docs/324 §3.2 allowed it)
  - A parked Sim stays `InWorld` (answers docs/347 open question 5): Overwatch `StuckCheck` / `ResetCheck` will
    reset it, so `farsimt3` stands down with Overwatch loaded until FS-17
  - docs/343 open question 7, `S.c` part: `ep` and `rev` only in model version 1
- Open facts / questions (docs/345 §16):
  - Q1 closed (Rodney 2026-11-26): no -40 clamp at go-live (replaces docs/324 decision 2); the difference is
    counted in shadow tests (`farsimCurveBelowClamp`, `farsimGoLiveBelow40`, `farsimGoLiveNeedMin`,
    `farsimGoLiveDistress`; docs/345 §6.1, M22). Q12 (docs/347 I-616-2) is measured by the same counters
  - Q2 closed (Rodney 2026-11-26): no go-live while paused, no exception; far Sims at a venue the camera moved to
    appear at unpause (docs/345 §11, counter `farsimLateAtUnpause`, M21). Also answers docs/342 Q2 for the pickers
  - Q3 closed (Rodney 2026-11-26): EA runs work shifts in the first build so their load is measured; `FarWork` is
    built behind its own switch `farsimwork` (default off); A/B row M20 in docs/345 §17 (ms/s by band, household
    exempt in both arms). docs/340 rows FS-8 (builds it) and FS-13 (times it) say so. No question is left open for Rodney
  - Q4 parking mechanism (native park of both scheduler entries, or a parked interaction); needs docs/341 Q1 / Q7
  - Q5 does `SimFrozenOrNotLive()` cover CAS, Build / Buy and Edit Town on both editions
  - Q6 write access to `MotiveTimeDelta` / `AutoUpdated`, EA's phase range, which hide flags EA persists (not in
    docs/342's scope)
  - Q7 first values of the timing keys (tuned by FS-4)
  - Q8 direct writers that reach a far Sim without `InteractionQueue.Add` (no-gaps check)
  - Q9 EA 1.69 not diffed
  - Q10 the 45 "shows UI" alarm callbacks on Sim owners (docs/346 Q7) are unread; until read, their owners stay live
  - Q11 buff writes onto a far Sim (docs/342 Q4)
- Not read: docs/348 (ST-8 / ST-12) was still being written; nothing in the Sim table depends on it. docs/344 was
  read through its handoff only
- Next: FS-1 builds `FarSimModel.cs` from docs/345 §18 (after CP-0); DT-1 after FS-1; SD-3 writes the boundary and
  contract repros against §4.2, §5.4 and §9; FS-2's brief should start with Q6; the FS-8 brief with Q8, Q10, Q11.
  docs/343 owner: optional `why` field on `E` lines proposed (docs/345 §12), and its §3.4 example uses the old state
  names
