# Next release dev notes (week of 2026-11-09)

Rename to `vX.Y.Z-dev.md` when the version is set. Player notes: CHANGELOG.md `## Unreleased`.

## CasTrim Mode 3 ON with the crash detector (Rodney 2026-11-08)

Release `ts3native.ini` `[CasTrim]`: `Mode=3`, `Detect=1`, `Probe=0`, `Install=0`, `MinAgeSec=60`, `Named=1`, `Async=1`, `NeedDb=0` (the last five as before). Build: ts3native 0.36.196 or later (pair castrimrel1108; `Detect` is new in 0.36.196, older builds ignore it and only install the detector with `Probe=1`).

- `Mode=3`: the save sweep of docs/539 §6 (part compositors EA's save left out, keep list from ts3fixes: active household, selected Sim, unknown scope). Live set on castrimdiag1108 (0.36.194): Mode 3 x2 + off arm, no crash, 0 `inventory bad slot` in 6 checks each (docs/data/results/2026-11-08-queue-castrim-live.md).
- `Detect=1` (default 1 with `Mode>=1` or `Probe=1`): installs only the `0x5EFE11` call site (docs/539 §12.5 inventory check, `cas trim: inventory check save #N` per save, 0.7-3.9 ms in the live set, first 20 `inventory bad slot` lines). The erase ring and the `0x5E772C` / `0x5E76DA` fault decoder (`crash forensics: cas trim inventory fault`, first 2) need no install and are in every build since 0.36.194.
- `Probe=0`: the docs/539 P1, P3, P6 diagnostics stay out (CacheOutfit name counts, fallback and rebuild counts, geometry KB, the `diag: castrim` outfit map walk per [MemDiag] sample). Before 0.36.196 the detector came only with them.
- Open: the 11-08 travel-save AV at `0x5E772C` (docs/539 §12, 1 in 4 Mode 3 runs, cause unknown). Release notes ask for `ts3native.log` on any crash during save or travel.
- CI: the stable-list check in `.github/workflows/build.yml` reads only `Enabled=` keys, so `[CasTrim]` (`Mode=`) needs no entry there; docs/PLAYERS.md lists it anyway.
- Release check job: `bp-castrim-release-smoke` in `tools/harness/queues/castrim-live.json` (release ini, no CasTrim overrides). Not run yet.

## CasTrim CellGuard ON, outfit release logging (Rodney 2026-11-08)

Release and repo `ts3native.ini` `[CasTrim] CellGuard=1` (code default 1, installed in every `Mode`, also `Mode=0`, with no tables): ts3native 0.36.198 (pair castrimdiag1108b). docs/539 §13.7.

- Cause: the 11-08 release-smoke AV at `0xD4B156` (docs/539 §13): EA's SimOutfit clear `0x5E6E20` released `[outfit+0xBC]` entry 63 = `0x40A00000` (float 5.0) through `0x5AF640` -> loader `+0x3C` -> `0xD4B150`. Who writes 5.0 is not known (three crashes with that value since 10-15, the first before CasTrim existed).
- `CellGuard=1`: call sites `0x5E6E60` / `0x5E7633` -> `0x5AF640`. An untagged non-null cell that is `0x40A00000`, unreadable, or whose first dword is below `0x10000` or outside TS3W `.rdata` is set to 0 before EA runs (that entry leaks; EA releases the others as before). `cas trim: cell guard skipped` (first 40) names the outfit, entry / count, value, cells -8..+8, erase-ring lookup, the save of the last inventory check that saw the outfit, the caller chain, CasTrim mode and the save / travel phase. Counters v176 `ctCellSeen` / `ctCellSkip`.
- `Detect=1` adds: the `[outfit+0xBC]` cells in the save inventory (`cas trim: inventory cells save #N` with the result vtables seen), the float-5 watch (`cas trim: float-5 cell`, any mode), `cas trim: outfit cell bad`, the part record check at the release sites (`cas trim: outfit release check ...` per save), and the `0xD4B156` / `0x5AF67E` crash decoder (`crash forensics: cas trim outfit release fault`; `crash-ledger.ts triage` decodes the EA report too).
- Cost: selftest 2.3 us per 111-cell outfit (warm cache); in game the per-save and per-release totals are in the log lines.
- Not proven: the guard's only test is the selftest (no game repro writes 5.0 into a cell).

## Small fixes ON by default (Rodney 2026-11-08)

Release `ts3fixes.package`: 13 small localized fixes True (`[Tunable]` default in `Instantiator.cs` and the tuning XML in `tools/setup/build-fixes.ts`, which must match). Evidence: Dalexlol's harness repros and our earlier passing repros (docs/252). Dalexlol's five: `invnotyet` (kInvNotYet), `metricskillmissing` (kMetricSkillMissing), `fireworksfizzle` (kFireworksFizzle), `petpoolorphan` (kPetPoolOrphan), `reltablesapart` (kRelTablesApart). Ours: `jazzparam`, `carnoroute`, `ageupabort`, `rewardgiver`, `buffupdate`, `npcdriverpool`, `uniformbackup`, `ageupkeepoutfits`. Not in: `bookcarry` (pending a run), `bookhand` (pending).
