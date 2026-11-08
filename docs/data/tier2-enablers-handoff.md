# Tier-2 enablers handoff (docs/126 §8 refused rows)

Branch `feature/tier2-enablers`. Status file for the agents building the shared-path pieces that unblock the docs/126 §8 refused rows. Static only; nothing run in game yet.

## Pieces

| Piece | Owner | What | State |
|---|---|---|---|
| E1 write mask | this lane (2026-11-08) | `AsDesc.of` flags + `outMask`: write outs only when the stored bool is true (`AS_OF_TRUE`), a static byte mask of the out bytes the game may write (`outMask`), a per-entry written mask from two fills (`AS_OF_DIFF`), an out that is a ref to a fresh managed array (`AS_OF_ARR`) | built, selftest `TestAutShadowTier2IcE` |
| E2 `ulong[]` array copy | this lane (2026-11-08) | `AK_ARRAY` element size = `AsDesc.sz` (4 or 8 B) in the pool copy and the worker allocator | built, same selftest |
| E3 level-bit gate | E3 lane (2026-11-08, branch `feature/tier2-e3`) | `LotTable_GetIfPresent` + entry `+0x168` check at the cut before filling #2, #4 (#18 retro not done) (docs/126 §4.1 correction) | built (0.36.208, pair `tier2e3-1108`), selftest `TestAutShadowTier2IcE3`, not run (ledger §463) |

## Rows

| # | IC | Piece | State |
|---|---|---|---|
| 7 | `World_GetTilesWithFloorFeaturesImpl` 0x796260 | E2 (`AK_ARRAY`, sz 8, AO_CUT) | built, `Tier2IcNew` bit 0 |
| 14 | `World_GetPondInfo` 0x78BC70 | E1 | still refused (124 B out, lot path not read) |
| 15 | `World_FindPondRepresentativePositions` 0x792630 | E1 (`AS_OF_TRUE`, `AS_OF_ARR`, AO_WORLD) | built, `Tier2IcNew` bit 1 |
| 16 | `World_GetLotLocationAtFloorImpl` 0x7850F0 | E1 (`AS_OF_DIFF`, mask 0xDFF, AO_WORLD) | built, `Tier2IcNew` bit 2 |
| 21 | `World_GetWorldToHouseboatDisplayPositionImpl` 0x788920 | E1 (`AS_OF_TRUE`, AO_CUT) | built, `Tier2IcNew` bit 3 |
| - | `World_GetHouseboatDisplayToWorldPositionImpl` 0x788A20 | E1 (`AS_OF_TRUE`, AO_CUT) | built, `Tier2HouseboatRow`; household-owned houseboat lots abort before the call |
| 2, 4 (+18 retro) | surface types | E3 (`AS_OF_LVL1` / `AS_OF_LVLA`) | built, `Tier2IcNew` bit 4 (#18 retro not done) |
| 20 | `World_LotGetDisplayLevelInfoImpl` | struct mask | not in this lane |

## Facts (disassembly, Steam 1.67 `TS3W.exe`, capstone)

- #21 0x788920 and the houseboat row 0x788A20 are the same body except the transform call (`0xAB33F0` vs `0xAB35A0`): world manager null, lot miss (`0xC62D40`) or lot byte `+0x444` clear -> `xor al, al`, out untouched; else 12 B written to `[ebp+0x1C]` (arg 5) through `0x76C040`, `mov al, 1`. cdecl. [verified]
- #16 0x7850F0: out (arg 3) bytes 0-3 and 4-7 (int x, z) and byte 8 (level) are written on every path (from the sentinel start value when there is no world manager or no lot); bytes 10-11 (room short) only when the lot is found and `lot+0x298` is set; byte 9 never. The body never reads the out. Returns the lot id (lot `+0x48`/`+0x4C`) or 0. [verified]
- #15 0x792630: lot 0, lot miss or no vertices (`0xAB5FA0` vector empty) -> 0, ref untouched; else host `+0x90` allocates a `float[]` (count = vector bytes >> 2) into the ref (arg 2), host `+0xA8` copies the elements, returns 1. [verified]
- #7 0x796260: element size 8 B (`add edi, 8`, count `sar edi, 3`); host `+0x88` (`0xD710A0`: `mono_array_new 0xEA30F0` with domain `[host+0xAC4]` and the class from `0xE75740`) allocates into a local, host `+0xA0` (`0xD71170`) copies 8 B per element to `+0x10`; returns the array or null (no WM, no lot, no `+0x298`, no tile). The game's allocation happens inside the body, which only runs on the sim thread at the cut (copy-never-call); the worker's answer is a fresh array from our `AsNewArrayGc` (GC_malloc under the P3 lock), the same path as the existing `int[]` rows (docs/122 §3.2 `GetDoorRooms`). [verified]
- #14 0x78BC70: out `PondInfo` is 124 B; the world path writes `+0xC..+0x3B` (4 Vector3 through `0x76BEB0`), `+0x6C` and `+0x70`; the lot path goes to `0xB64E00` (not read). [verified world path]

## Where things are

- Native: `AsDesc.of` / `outMask`, `AsEnt.om` (shares the union with `lp` / `lastRead`: never on object, list or memo rows, asserted), `AsRefresh` two-fill, `AsPoolArray`, `AsOutWrite` (hit), `AsSame` compares `om` and array-out payloads, `AsNewArrayGc(vtable, n, elemBytes)`. Rows 15-19 of the docs/126 §8 block (`AS_NT2IC` = 20, `AS_NT2E` = 5), gated per row by `AsDescOn`.
- Managed: `autofft2ic` slots 15-19 (`IcG5`, `kIcV` = 20); `LotPick` aborts on the worker at a household-owned houseboat lot.
- Queue: `bp-t2e-dry`, `ip-t2e-houseboat-dry` in `tools/harness/queues/tier2-ic-rows.json` (pair `tier2mask1108`). Not run.
- Ledger §460.

## Open questions

- #14: the 124 B out does not fit the 24 B `AsEnt.out`; needs an out stored in the pool and the lot path `0xB64E00` read. Still refused.
- Houseboat row: hit rate with a moving camera (ledger §459 I-459-5) and the sailing-boat answer at cut time (I-459-4) only show in the dry compare.

## Status (2026-11-08 08:05 UTC)

- Branch `feature/tier2-enablers` final, not merged: the merge agent merges it after a5b, in one held window. `git merge-tree` against master `e8a6377e` was clean; conflicts can come only from `docs/data/builds.json` / `PLAN.md` once a5b lands (`tools/agent-merge.cjs`).
- Pair `tier2mask1108` = `ts3native.asi.tier2mask1108` (0.36.202, sha 0009b5324e8e) + `ts3fixes.package.tier2mask1108` (sha 2c927c487c1c) in `game-copy/`, registered in `docs/data/builds.json` on this branch.
- Checks: native `build.cmd` selftest ok (incl. `TestAutShadowTier2IcE`), `tools/selftest-all.ts` 212 PASS / 0 FAIL / 5 SKIP, `tools/hook-sites.ts --check` pass, ts3fixes builds.
- Not run in game: next step is `tier2-ic-rows.json` jobs `bp-t2e-dry`, `ip-t2e-houseboat-dry` (correctness, gate 0 mismatches).
- Next piece: E3 level-bit gate (#2, #4, #18 retro); #14 needs a pool-backed out.

## Status E3 (2026-11-08 ~09:20 UTC, branch `feature/tier2-e3`, not merged, source only)

- Done (source, not compiled): `AS_OF_LVL1` / `AS_OF_LVLA`, `AsLevelGate` and the skip in `AsRefresh` (`native/ts3native/autshadow.cpp`), rows #2 and #4 last in `kAsDesc` (`AS_NT2IC` 22, `AS_NT2E` 7, `Tier2IcNew` bit 4 -> E bits 5-6), `g_asJobState = &g_async.state` before `ApplyAutShadow` (`ts3native.cpp`), selftest `TestAutShadowTier2IcE3` plus the `TestAutShadowTier2IcE` asserts moved to 7 E rows (`autshadow-test.h`). Disassembly facts and open claims: ledger §463.
- Not built: paused by the coordinator before the 17:30-17:45 PHT window. No version taken, no pair registered.
- Next steps, in order:
  1. Native `build.cmd` + selftest (fix compile errors first; monohost also includes `autshadow.cpp`: `g_asJobState` stays NULL there). Take the next free version across all branches right before registering (0.36.204 is taken; check `git grep` of `docs/data/builds.json` on every branch), pair `tier2e3-1108`.
  2. Managed: `autofft2ic` slots 20-21 (`kIcV` 22, new `IcG6` in `mods/ts3fixes/src/AutOffload.cs`, same household-lot skip as `IcG5`): `World.GetTerrainSurfaceType(p)` + 1, and `World.GetFloorSurfaceType(lot.LotId, p)` (null = 2, else a content hash).
  3. `tools/autonomy-tier2.ts` `TIER2_BUILT` += `World_GetTerrainSurfaceTypeImpl`, `World_GetFloorSurfaceTypeImpl`; regenerate `docs/data/autonomy-tier2.csv` (docs/126 Reproduce, `--asm`/`--index` from the main checkout's `game-copy/`).
  4. Queue job `bp-t2e3-dry` in `tools/harness/queues/tier2-ic-rows.json` (copy `bp-t2e-dry`, add `Tier2IcNew=16`, gate 0 mismatches); do not run.
  5. docs/126 §8 line, `tools/selftest-all.ts`, `tools/hook-sites.ts --check`, PLAN.md line, then commit.

## Status E3 (2026-11-08, branch `feature/tier2-e3`, built, not merged, not run)

- Built: ts3native 0.36.208 (asi sha256 19c7aeb4863a), pair `tier2e3-1108` = `game-copy/ts3native.asi.tier2e3-1108` + `game-copy/ts3fixes.package.tier2e3-1108` (sha 6ac1663013a1, tuning 24e0fad3a692), registered in `docs/data/builds.json` on this branch (commit a32db0ba).
- Done: native `build.cmd` selftest ok (incl. `TestAutShadowTier2IcE3`); managed `IcG6` slots 20-21 (`kIcV` 22, household-owned lots skipped like `IcG5`); `TIER2_BUILT` + `docs/data/autonomy-tier2.csv`; queue job `bp-t2e3-dry` (`Tier2IcNew=16`); docs/126 §8 and PLAN.md lines; `tools/selftest-all.ts` 215 PASS / 0 FAIL / 5 SKIP; `tools/hook-sites.ts --check` pass.
- Household: the native rows only answer worker calls (the worker's own household rules apply unchanged); the dry reads `IcG5` / `IcG6` skip lots owned by the active household; the gate itself changes no Sim state (skip = no answer, the worker aborts).
- Next step: merge `feature/tier2-e3` into master (after `feature/tier2-enablers`; expect `docs/data/builds.json` / `PLAN.md` / `tier2-ic-rows.json` conflicts, `tools/agent-merge.cjs`), then run `tier2-ic-rows.json` job `bp-t2e3-dry` (pair `tier2e3-1108`; gate 0 `autoff_t2ic_mis` over slots 0-21, read `autshadow level gate` skip/fill lines for I-463-2 and `[RouteGate]` waits for I-463-1). Optional later: an IP arm with `Tier2IcNew=31` once `bp-t2e-dry` passed.
- Open: #18 retro, I-463-1..3.
