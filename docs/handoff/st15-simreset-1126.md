# ST-15 + Sim reset alarms handoff

### ST-15 shared far API and names; EA bug check I-614-1 (2026-11-26 PHT, sub-agent / Fable)
- State: done (two docs; nothing built, nothing run)
- Changed: `docs/351-far-shared-api-names.md` (new), `docs/352-sim-reset-alarms.md` (new), `docs/51-inference-ledger.md` §620, §621 (I-614-1 marked refined), `docs/index.md` (two lines), `docs/332-unbounded-work-packages.md` §9 (ST-15 answer), `docs/346-alarm-hold-hook-and-census.md` (Q1, Q5 point to the answers), `docs/data/registry-reservations.json`, this file
- Branch / commit: master, see `git log -2 -- docs/351-far-shared-api-names.md docs/352-sim-reset-alarms.md`
- Build: none
- Switches: none built. Named for builders: the far switch table of docs/351 §6; `simresetalarms` and `[Diag] SimResetAlarmProbe` (docs/352 §5), all default off
- Counters / slots: none reserved (reserve at build)
- Tests: none (doc only). Owed: FO-3 selftest of take mode with a pass-through timer; repro `sim-reset-alarms` (docs/352 §5)
- Household: `FarHold` keeps the docs/346 §3.6 exemptions; `simresetalarms` applies to all Sims as an EA bug fix whose result vanilla already produces on normal lots
- Facts found:
  - A reset asks only the current lot's alarm manager; Sim alarms are on the global one: kept on a normal lot, dropped (both reset-scoped types) on the world lot [verified code path, V-621-1 .. V-621-3; effect inferred, I-621-1]
  - Vampire sunset / sunrise alarms are armed only at load or occult set-up, so a street reset loses them until reload [V-621-4, effect I-621-4]
  - 198 Sim-owner census sites are reset-scoped (146 `DeleteOnReset`, 52 `NeverPersisted`) [V-621-5]
  - `serviceleave` restarts a live timeout after a reset on a normal lot [I-621-6]
  - No NRaas handling of this [V-621-6]
  - docs/353 (written in parallel) names the save hub `FarSave`; docs/351 places it and keeps `FarHub` for load, world quit, budget and pause
- Open facts / questions: docs/351 §9 (pause predicate, take-mode count, docs/350 not read, which package builds `FarSave`); docs/352 §7 (world-lot reset frequency, the other 29 long-lived sites, stalled situation states, EA 1.69, objects without a lot)
- Next: FO-3 builds `FarHold*` / `FarHub*` from docs/351; a lane builds `[Diag] SimResetAlarmProbe`; a bugfix branch for the `serviceleave` re-arm guard (docs/352 §6)
