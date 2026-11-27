# SD-4 handoff: far save flush (docs/353)

### SD-4 (save part) Save / flush design shared by every far feature (2026-11-26 PHT, sub-agent / Fable)
- State: done for the save flush (static analysis + design; nothing built, nothing run). The facade rows, id rule
  and stage ladder of SD-4 are referenced from docs/344, not rewritten
- Changed: `docs/353-far-save-flush.md` (new), `docs/index.md` (one line), `docs/51-inference-ledger.md` (§622),
  `docs/340-far-state-machine-rc-plan.md` (SD-4 row), this file
- Branch / commit: master, see `git log -1 -- docs/353-far-save-flush.md`
- Build: none
- Switches: none added. Named for the builders: hub `FarSave` in ts3fixes (installed when any far switch is on),
  latches `FarSave.Quiesce` and `FarSave.InSave`, rule FarMemGuard
- Counters / slots: none reserved; names in docs/353 §11 (`farFlushFailed`, `farSaveUnflushed`, `farSaveRefused`,
  `farFlushMs`, `farFlushItems`). Doc 353 and ledger §622 reserved through `tools/registry.ts` (ids `doc-353`,
  `ledger-622`); the reservation lines sit in the uncommitted `docs/data/registry-reservations.json` with other
  lanes' lines and were not staged
- Tests: none (doc only). Owed by the builders: docs/353 §11 (hub selftest, M-save trace hash, save round trip with
  the mod removed, save matrix incl. NRaas Saver / Traveler and a library export)
- Household: the flush touches only items in the owners' tables, which cannot hold household Sims, the selected
  Sim, household lots or unknown scope; the hub re-checks each id with the shared predicate and promotes on a hit;
  `HouseholdToo` is not read (save change)
- 4x / streamed world: L0 flush cost is linear in far items at about 0.1 ms each; T4 and L2 flush grow with the
  world and are the reason they stay opt-in (docs/353 §10)
- Facts found:
  - `0xAB1030` has one caller, `0x742A95` (in `0x742A50`); normal and travel saves both pass it [verified, V-622-1]
  - Managed entry is the static `Sims3.SimIFace.LoadSaveManager.SaveGame` / `SaveTravel` pair; travel saves
    (GameStates.cs:1643, :1678, :1785 and NRaas Traveler's copies) never pass `OptionsModel.SaveGame`
    [verified, V-622-2, -4, -6]
  - `OptionsModel.SaveGame` is async for the options menu and has dialogs and a yield before the native call
    [verified, V-622-3, -5]
  - `SaveTravel`'s result is not read by EA, so a travel save cannot be refused [verified, V-622-4]
  - `LoadSaveManager_SaveWorld` `0x743680` is the world-tool path only [verified, V-622-7]
  - The NRaas tree has a `LoadSaveManagerEx` that re-implements the instance `SaveGame`; do not hook the instance
    method [verified source, install inferred, I-622-1]
- Decisions in the doc: hub on the static forwarders (docs/343 Q3); repair file plus refuse rule, no write-ahead
  (docs/343 Q4); keep the `AlarmManager.Write` bracket (docs/346 Q3); hard flush never yields; L2 forbidden during
  travel
- Next: FS-7 / FS-10 and FO-5 build against docs/353 §3-§5; FS-16 answers Q2 and Q3; LZ-6 answers Q4
- Open questions: docs/353 §13 (7 open; Q1 closed by Rodney 2026-11-26: refuse a normal save, travel saves and counts)
