# 235. EA app 1.69.47: the managed side (assembly diff, hook targets, compile check)

Static work on the EA laptop, 2026-11-02 14:10-14:55 PHT. docs/232 §3 last bullet, §5 task 8. No game run, nothing written
under `C:\Program Files`, Mods untouched. Companion to docs/230 (edition), docs/231 (popup), docs/233 (Mods set).

## 1. Extraction [verified]
- 1.69 keeps the script assemblies where Steam does: `<EA>\Game\Bin\scripts.package` (3 S3SA), `gameplay.package` (4),
  `simcore.package` (3). `GameData\Shared\Packages\*` holds no S3SA; the EPnn folders have thumbnails only.
- `node tools/extract-assemblies.ts game-copy/assemblies-ea169 <those three>` (read-only; the extractor already takes any
  package paths, so no option was added). Output dir is under the ignored `game-copy/` (`git check-ignore` ok).
- S3SA headers (`tools/s3sa-stamp.py`): scripts/gameplay resources are header v2 with stamp `1.0.0.18` (Steam 1.67: v2,
  `0.0.0.32`); simcore (mscorlib, System, System.Xml) is header v1, no stamp, on both editions.

| assembly | bytes 1.67 / 1.69 | MVID 1.67 -> 1.69 | assembly version | methods | verdict |
|---|---|---|---|---|---|
| ScriptCore.dll | 430080 / same | ee57f83a... -> 5db130b8... | 1.0.5134.21446 -> 1.0.9048.31157 | 4625 | recompiled, 0 methods differ |
| SimIFace.dll | 618496 / same | c5300e3d... -> 1f69b4ba... | 1.0.0.50 | 5990 -> 5991 | +1 method: `Sims3Launcher.BusinessLogic+S3L_EntitledContentItem::.ctor()` |
| Sims3Metadata.dll | 143360 / same | 2eec397f... -> 1ae992d8... | 1.0.0.0 | 959 | recompiled, 0 differ |
| Sims3GameplayObjects.dll | 7626752 / same | b2cc44b0... -> ee64b579... | 1.0.0.0 | 55580 | 1 differs (`ChallengeDelegateFunctions::.cctor`) |
| Sims3GameplaySystems.dll | 13873152 / 13881344 | 36818893... -> 51ac3449... | 1.0.0.0 | 75877 | 8 differ (§2) |
| Sims3StoreObjects.dll | 1236992 / same | bd729220... -> dd745123... | 1.0.0.0 | 6732 | recompiled, 0 differ |
| UI.dll | 3014656 / same | 618382f8... -> c973f95b... | 1.0.0.0 | 14926 | 5 differ (§2) |
| mscorlib / System / System.Xml | same | same MVID, same sha256 | 2.0.0.0 | 8256 / 1340 / 5177 | byte-identical |

Full MVIDs and sha256 prefixes: the header lines of the `out/port/managed-ea169.csv` run (`tools/port-fingerprints.ts`
`assemblies()`). ScriptCore's build number 9048 = days after 2000-01-01 = 2024-10, the 1.69 recompile [inferred].

## 2. Method diff
Two tools, two answers; the token-normalized one is authoritative:
- `port-report`'s managed stage (`out/port/managed-ea169.csv`, 145715 rows): hash-changed 134144, size-changed 11458,
  removed 56, added 57, module-missing 0. Its hash covers raw IL with metadata tokens and its key is short type name +
  method + arity + order, so a recompile with new tokens and reordered overloads flags nearly everything; removed/added are
  all compiler names (`<>c__DisplayClassN`, `b__N`). Not usable for a 1.69 verdict [verified by the second tool].
- `tools/mod-method-diff.ts --asm game-copy/assemblies --pkg <EA scripts.package>=ea169 --pkg <EA gameplay.package>=ea169`
  (token-normalized IL, compiler names folded, docs/218): `out/port/managed-ea169-il.csv`, 14 methods differ + 1 added,
  164690 same. Differing: `ActiveCareer` `.cctor`/`.ctor()`/`.ctor(OccupationNames)`, `Sim::.ctor()`, `CASLogic::.cctor`,
  `Career::ShowUploadResumeTns` (+ its lambda, renamed), `TravelUtil::.cctor`, `ChallengeDelegateFunctions::.cctor`,
  `BuildController::.ctor(uint)`, `CASCharacter::.cctor`/`.ctor(uint)`, `OptionsDialog::.ctor()`, `SceneMgrWindow::.ctor(uint)`;
  every one "calls and stores unchanged: branches, constants or locals", same IL line count = constants or field order,
  not new logic [verified shape diff; the constants themselves not read].
- The 1.69 managed layer is 1.67 recompiled with constant changes in 14 constructors plus the launcher entitlement type.

## 3. Our hook targets [verified]
- ts3fixes: 156 `Patch(...)` calls, 126 distinct `typeof(T), "Method"` targets (the rest name NRaas/Overwatch types from
  variables, not game assemblies) + 64 `GetMethod("...")` reflection reads; ts3agent: reflection by name in its handlers.
- Intersection with the 14 differing / 1 added methods: none. Every targeted method has identical normalized IL in 1.69,
  none is missing. `mods/ts3fixes/src/AutShadowIlHashes.cs` is the exception: `AutShadowCore.IlHash` is not normalized,
  `node tools/autshadow-il-hashes.ts --set ea169=<7 DLLs> --check` reports 961 of 3570 G-M6 hashes new and 1 method
  missing (`InteractionInstance+<>c__DisplayClass1::.ctor()`, renamed), so on 1.69 `[AutShadow]` turns itself off every
  session until the file gets an `ea169` set.

## 4. Compile check [verified]
`TS3_REF_DIR=game-copy/assemblies-ea169 TS3_BUILD_DIR=build/ea169 node tools/setup/build-fixes.ts` and
`... node tools/harness/build-agent.ts` (new env overrides, default unchanged): both compile with csc without an error or
warning; `build/ea169/ts3fixes.package` (1284854 B) and `ts3agent.package` (454426 B), `build/` untouched, nothing installed.

## 5. What a 1.69 build needs
1. Nothing in the C# sources. The same source compiles against 1.67 and 1.69; the API surface we use is unchanged.
2. `AutShadowIlHashes.cs`: regenerate with `--set ea169=...` (adds 961 hashes) or `[AutShadow]` stays off on 1.69.
3. Release/CI: a second package pair built with `TS3_REF_DIR` = 1.69 is not needed for correctness (csc binds by name, not
   MVID); the 1.67-built packages should load on 1.69 as they are [inferred; a load on the EA laptop is the test].
4. Stamp: our packages are header v1 without a stamp (`tools/dbpf.ts` `s3saEncode` writes `head[0] = 1`), so they never
   trigger the docs/231 popup on either edition; no v2 stamp is needed. Open: whether 1.69's content scan treats a v1 core
   replacement differently from a v2 one stamped `1.0.0.18` (simler90 EAapp) [not tested].

## 6. Next steps
- EA laptop load with the current `build/ts3fixes.package` + `ts3agent.package` (verify run, docs/233 pass criteria), read
  the `ts3fixes:` world lines; then decide whether an `ea169` AutShadow hash set ships in v0.2.5 or after.
- Drop the `.managed.csv` stage from the 1.69 port checklist (docs/232 task 8): it over-reports on a recompile; use
  `mod-method-diff.ts --pkg` instead, or give port-fingerprints the ilShape hash (docs/23 idea, not planned).
