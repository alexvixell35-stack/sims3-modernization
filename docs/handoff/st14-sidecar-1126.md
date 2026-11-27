# ST-14 handoff: far sidecar format

### ST-14 One sidecar format for far-Sim, far-object and lazy-instantiation data (2026-11-26 PHT, sub-agent / Fable)
- State: done (design doc; nothing built, nothing run)
- Changed: `docs/343-far-sidecar-format.md` (new), `docs/index.md` (one line), `docs/340-far-state-machine-rc-plan.md` §3.7 (location wording), this file
- Branch / commit: master, see `git log -1 -- docs/343-far-sidecar-format.md`
- Build: none
- Switches: none (the doc asks for one ini key per load-time cleanup step, named at build)
- Counters / slots: none reserved; names proposed in docs/343 §10, slots to be reserved by FO-5 with `tools/registry.ts`
- Tests: none (doc only); the selftests the builders owe are listed in docs/343 §12
- Household: no household Sim, selected Sim, unknown-scope Sim, household lot or pair with a household member is ever written; one writer filters with `Fixes.HouseholdExempt`, the reader filters again at load; `HouseholdToo=1` does not lift it (docs/343 §8)
- 4x / streamed world: only the S and D lines grow with far Sims and both are capped with a safe drop, 1 MB per world block (docs/343 §10); load-critical paged-out lot data (docs/320 §7.6 `<Save>.lw/`) is out of this format (docs/343 §11)
- Facts found:
  - EA's save deletes `Saves\<name>.sims3` and copies the working copy over it, so a file of ours inside the save folder would not survive [verified, docs/15 step 9]
  - A travel save does not update `Saves\` [verified, docs/15 step 7]; the sidecar is therefore written only at normal saves
  - The text channel answers one bool per call, so read-back over it costs one call per bit [verified, `UserDiag.cs` `DiagSend`]
  - An existing per-save sidecar store (key, folder, atomic write, Save As) is `native/ts3native/played.cpp` + `PlayedHouseholds.cs` [verified]; docs/343 reuses its key, folder and write pattern
- Open facts / questions (docs/343 §13):
  - Stamp: sim clock and `SimDescription` count equal across save and load on both editions? (FO-5, first run log)
  - Transport: text channel or rooted array; cost per KB; rooted array on EA 1.69 (FO-5)
  - Do mod save paths reach `OptionsModel.SaveGame`, or must the flush hook `LoadSaveManager.SaveGame`? (SD-4)
  - L2: repair file enough, or write-ahead per record before destroy? (SD-4, RX-9)
  - World file name API for custom worlds (FO-5)
  - Closed: location. Rodney 2026-11-26: outside the save folder, `ts3native\far\<key>.far.jsonl`; docs/343 §13 #6 closed and docs/340 §3.7 updated
  - Field lists of `P.f` and `S.c` (SD-4, SD-1); object id stability across save and load (RX-4)
- Next: FO-5 and FS-18 can cite docs/343; ST-15 is unblocked on its ST-14 edge; SD-4 should read docs/343 §7 and §13 before writing the save-flush spec
