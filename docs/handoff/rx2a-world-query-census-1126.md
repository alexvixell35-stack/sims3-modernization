# RX-2a handoff: world-wide query census (docs/356)

### RX-2a World-wide query census, EA scripts: every site classified, choke points named (2026-11-26 PHT, sub-agent / Fable)
- State: done (static only; nothing built, nothing run). The object half by type was docs/344 §3; this adds the
  per-site classification, the Sim / household / lot / description lists and the count choke point
- Changed: `docs/356-world-query-census.md` (new), `docs/data/world-query-census.csv` (new, 1,270 rows),
  `tools/world-query-census.ts` (new, self-check 9 PASS), `docs/index.md` (one line),
  `docs/51-inference-ledger.md` (§625), `docs/340-far-state-machine-rc-plan.md` (RX-2a row marked done), this file
- Branch / commit: master, see `git log -1 -- docs/356-world-query-census.md`
- Build: none
- Switches: none
- Counters / slots: none. Doc 356 and ledger §625 reserved (`doc-356`, `ledger-625`); the reservation lines sit in
  the uncommitted `docs/data/registry-reservations.json` beside other lanes' lines and were not committed
- Tests: `node tools/world-query-census.ts --self-check` 9 PASS; `tools/selftest-all.ts --list` picks it up.
  The full `selftest-all` run was not started (other lanes are building)
- Household: read-only
- 4x / streamed world: every site in the census is a loop over the whole world; `SimDescription.Find` is
  O(residents) per call (docs/356 §4.3, §8)
- Facts found:
  - Two native choke points: get `0xD798F0` (host vtable `0x108B690` slot `+0xC0`) and count `0xD74C00` (slot
    `+0xC4`), which walks the map itself [verified, docs/51 §625 V-625-1, V-625-2]
  - `WorldLot.GetObjects<T>` / `CountObjects<T>` ask for the global objects [verified, V-625-3]
  - `SimDescription.Find` is a linear scan plus a world-wide `Urnstone` query on a miss; 633 call sites
    [verified, V-625-4]
  - 329 object sites / 149 types: 6 task loops, 8 repeating alarms, 21 interaction tests, 137 load / cheat / ui,
    70 untraced (grouped by hand, docs/356 §3.4) [inferred, heuristic, I-625-1]
  - Materialise list for LZ-4: 88 types; 56 load / cheat / ui only and 5 count-only may be lazy [inferred, I-625-2]
  - 107 sites enumerate Sim objects; 9 run in play and write to each Sim [inferred, I-625-3]
- Open facts / questions (docs/356 §7, none for Rodney):
  - O1 class hierarchy for the 88 types (LZ-1 / LZ-4)
  - O2 second hop for the 70 untraced rows (RX-9 if a row decides a stage)
  - O3 `SocialComponent.FindSimsOfRelationshipType` picks from live Sims only: confirm the tie rule keeps this
    inside "household untouched" (FS-16 / RX-9)
  - O4 the second host map `+0x1678` (only for a native facade)
  - O5 real call rates per choke point (LZ-1 shadow)
  - O6 EA 1.69
- Next: RX-2b can run (`node tools/world-query-census.ts --out <csv> <mod source folders>`); RX-9 has its EA-side
  query input (it still waits for RX-2b). Candidates for the loop-cost list (docs/333): description index,
  spawner world walks, `Camera.GetClosestVisibleLot` (docs/356 §6); none built, none measured
