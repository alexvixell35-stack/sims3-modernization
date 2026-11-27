# SD-3 handoff: far boundary, hysteresis and the go-far / go-live contract (docs/355)

### SD-3 Boundary, hysteresis and go-far / go-live contract with one repro per line (2026-11-26 PHT, sub-agent / Fable)
- State: done (design doc plus a small static check; nothing built, nothing run)
- Changed: `docs/355-far-boundary-and-golive-contract.md` (new), `docs/index.md` (one line),
  `docs/51-inference-ledger.md` (§624), `docs/340-far-state-machine-rc-plan.md` (SD-3 row marked done), this file
- Branch / commit: master, see `git log -1 -- docs/355-far-boundary-and-golive-contract.md`
- Build: none
- Switches: none added. Keys named for the builders, all under `[FarSim]`: `RingMinM` 25, `RingLeadS` 2.0,
  `NearStepM` 20, `GapM` 50, `JumpM` 150, `WakeTimeoutTicks` 30. Debug commands for test builds: `far_force_far`,
  `far_fail`, `far_state`, `far_hold_add`
- Counters / slots: none reserved; 21 names in docs/355 §6, slots to be reserved by FS-6 / FS-7 / FS-11 with
  `tools/registry.ts`. Doc 355 and ledger §624 reserved (`doc-355`, `ledger-624`); the reservation lines sit in the
  uncommitted `docs/data/registry-reservations.json` beside other lanes' lines and were not committed
- Tests: none (doc only). Owed by FS-10: 19 repro jobs of docs/355 §5 (C1-C8 contract lines, B1-B6 boundary,
  G1-G5 go-far), about 3 h of game time in one correctness queue [inferred]
- Household: the near scan only requests go-lives; go-far re-checks scope on the Sim's own task (GF1); every repro
  reads the four household counters of docs/345 §10; `[Experimental] HouseholdToo` is read nowhere
- 4x / streamed world: near detector cost follows machine-owned places inside the scan box (hash grid, no bounds);
  far detector is sliced over the resident list; a parked Sim costs one sleep-map visit per `ScriptTick`
- Facts found:
  - `Simulator.Wake` = script call `0x76CB30` -> host `0xD77BA0` (vtable slot `+0x24`); a sleeping task gets its
    sleep-map count zeroed directly [verified, docs/51 §624 V-624-1]. This closes the wake half docs/341 §9.1 left
    inferred (I-619-1)
  - EA calls `Simulator.Wake` at 12 sites in Sims3GameplaySystems, none in the interaction queue: an interaction
    added to a parked Sim does not wake it, the `eaPush` hook must [verified, V-624-2]
  - The idle branch of the Sim's pass runs EA's idle call, then the autonomy test, then a 2 / 10 / 20 tick sleep;
    `AutonomyManager.RemoveActor` exists [verified, V-624-3]
  - Design decisions: near side found by a movement-driven scan, not by the 5-minute pass; ring = lead segment;
    dwell clock restarts at every go-live; T4 only on the Sim's own task; parking mechanism c (docs/345 Q4)
- Open facts / questions (docs/355 §8, none for Rodney):
  - P1 method to patch at the park point, inline check (FS-7)
  - P2 is a task's sleep state saved; P3 is `SimUpdate` saved (FS-7, repro G5)
  - P4 EA reset of a task that sleeps forever (FS-7, repro G4)
  - P5 host `+0xBD8` / `+0x1764` named from shape only
  - P6 real camera speeds for `RingLeadS` (FS-4)
  - P7 EA 1.69 addresses (RX lane)
- Next: FS-7 is unblocked on the design side (it still waits for FS-6). FS-6, FS-10 and FS-11 briefs should cite
  docs/355 §7 for their parts. Until FS-11 lands, FS-7 runs the near test inside the boundary pass with
  `RingLeadS` 0 and `BoundaryPassMin` 1 (docs/355 §7)
