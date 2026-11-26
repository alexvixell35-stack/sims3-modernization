# 273. Retail (disc / non-Steam) TS3W.exe 1.67 port of v0.2.12 (2026-11-16 PHT)

Question: ship v0.2.12 for players on the retail (disc, non-Steam, non-EA-app) 1.67 game, with the same features and
defaults as v0.2.12 Steam. Input: one retail `TS3W.exe` from Rodney (kept in the ignored `game-copy/retail/`, never
committed). Static work only: a game test chain was running, no game run was allowed. [verified] = read in the exe bytes
or counted by a tool this session; [inferred] = reading of them (docs/51 §489).

## 1. The exe [verified]

| | Steam 1.67 (`game-copy/bin`) | retail 1.67 |
|---|---|---|
| size | 14,510,400 | 14,514,496 |
| PE timestamp | 52DEC247 (2014-01-21) | 52D872DA (2014-01-17) |
| SizeOfImage | 0xE5A000 | 0xE5B000 |
| version resource (File/ProductVersion) | 0.0.0.32 | 0.2.0.209 |
| OriginalFilename | Game_Win32.exe | Game_Win32.exe |
| `.text` virtual size | 0xB93971 | 0xB94351 (+0x9E0) |
| `.rdata` / `.data` / `.tls` / `.rsrc` start | 0xB95000 / 0xD46000 / ... | each +0x1000 |
| `.data` size | 0xEE384 | 0xEE384 |
| imports | 24 DLLs | the same 24 DLLs; KERNEL32 179 functions vs 178 |
| registry strings | `Software\Sims(Steam)\The Sims 3` ... | no `Sims(Steam)` paths |

- **Version:** neither exe holds a `1.67.x.y` string; the game's version number (Steam 1.67.2.024037) is not in the exe,
  so the community figure 1.67.2.024002 for retail is not verified here. The build string (the version resource, also what
  S3SA version-2 stamps are compared with, docs/231) is `0.2.0.209` on retail and `0.0.0.32` on Steam.
- **DRM:** no SecuROM section, no `Core/Activation.dll` import, no packed or encrypted section (the `.text` maps
  instruction by instruction). The text `SecuROM` occurs in both exes. LAA is set (given).
- **Same code, other layout:** 11.7 M of the 12.1 M `.text` bytes differ at the same RVA: the linker placed functions in a
  slightly different order (the address delta changes every few functions, mostly within ±0x2000, some functions moved
  far), and some identical small functions were folded onto other copies. `.data` moved as a whole by +0x1000 (5,224 clean
  pointer votes, no other delta). So the port is a full address table, not a version check.

## 2. Port (docs/232 option A)

- **Build:** the v0.2.12 EA release tree (`refs/public/rc-1111g-ea169` 8363ecec = v0.2.12 code + the `TS3A` codemod + EA
  blocks; the Steam v0.2.12 asi is `release/rc-1111g`, same native code) with `/DTS3A_TABLE /DTS3A_RETAIL`:
  `native/ts3native/build-retail.cmd` -> `out-retail/ts3native-retail167.asi`, CI job `native-retail167`.
- **Addresses:** `ts3addr-retail167.h` (1,298 rows) and `ts3bytes-retail167.h` (488 hook-site byte rows) from
  `node tools/port-run.ts --exe <retail exe> --ref game-copy/bin/TS3W.exe --allow-inferred --zero-unresolved --table native/ts3native/ts3addr-retail167.h --host-name "retail 1.67" --overrides docs/data/retail167-overrides.csv`.
- **Host check:** the asi runs only on PE timestamp 52D872DA + SizeOfImage 0xE5B000 (as EA); any other exe logs
  `SKIP all: host is not retail 1.67` and patches nothing. The log says `release: v0.2.12 retail`.
- **Steam code paths, not EA's:** `ts3addr.h` picks the table; `TS3N_EA_PORT` stays EA-only, so [ImageGuard] and
  [EaCrashFixes] SimNoTexture / SimNoTextureAtlas are not in the retail asi (not in v0.2.12 Steam either); [EaDiag] and the
  EA icall-table guard in autshadow.cpp are `TS3A_TABLE && !TS3A_RETAIL`.
- **Per-build constants:** `TS3A_PICK(steam, ea, retail)` (ts3addr.h) for the code hashes of [FootprintIndex],
  [SaveCompress], [WaterJps], [MonoJit] FastCopy, the five `[MonoFast]` CallPath hashes (+ the MonoPatcher variant) and two
  operand bytes of each pure-icall copy in monojit.cpp; mintcall's three asm literals have a retail branch tied to `TS3A`
  by the existing static_assert. On Steam every pick is the old literal.
- **Ini:** the shipped v0.2.12 ini (sha256 91b007e7) unchanged. Its one address, `[ThreadStack] Skip=0x76B470`
  (SimulatorThread_Proc), is translated to 0x76B530 in the retail build (threadstack.cpp), so a Steam ini copied over still
  exempts the sim thread.
- **Left as on EA:** [S3SSCompat] stale write is skipped, not redirected to WorldManager+0xEC (not verified on retail
  [inferred: same 1.67 struct]); [SaveWrite] keeps its timing call (Apex's early save is gated on the Steam stamp).
- **New guard (EA too):** [ProbeSnap] / CallSites refuse to arm under any table: `vcallsites*.h` are raw Steam sites with
  2-7 byte checks only.

## 3. Verification [verified]

`node tools/retail-port-check.ts --other game-copy/retail/TS3W.exe` (exit 0; `--self-check` runs in selftest-all). It does
not trust exe-addr-map: anchors are the code positions (pointers / call rel32, 28-byte masked window unique in both exes,
400,616 pairs); evidence per row:

| evidence | rows |
|---|---|
| vote: call sites / pointer users of the address name one retail address | 437 |
| code: anchors around it move by one delta and 24 masked bytes agree, or a unique 48-byte signature | 561 |
| slots: the vtable / function-table dwords holding it map to dwords holding one retail value | 211 |
| content: the pointers stored at and around a data address map to one retail place | 71 |
| string / shift (.data +0x1000) / near / region (back+forward >= 48 equal bytes, unique) | 5 / 4 / 1 / 2 |
| manual (`docs/data/retail167-overrides.csv`, written by hand) | 2 |
| unresolved, probe builds only (`vcallsites3.h`, `/DTS3N_VCSITES3`) | 4 |

**1,298 / 1,298 rows** pass (0 FAIL, 0 unresolved in the release build). 50 rows come from the overrides file (48 with
this tool's evidence, e.g. 0x405410: exe-addr-map said 0x620AD0 "exact", 55/0 call votes say 0x412E20, which has 74
callers like Steam's 75; 2 by hand: the RefPack codec slot 0xFB901C -> 0xFBA05C, the only retail dword holding the moved
slot function, and the selftest-only sentinel 0xC0FFEE). Per-build constants: 11 hashes over ranges that are the same code
(masked bytes equal, every moved operand maps), plus the three pure-icall copies and the Skip address: all ok, and each
retail hash is the third `TS3A_PICK` argument in its source file.

`patch-bytes-check` (via port-run): 492 expectations, 331 ok, 157 ported (rel32 / pointer operands moved), 4 mismatch:
0x571C4C ([UnseenRootMotion]) and 0x6FA2DB ([CrashForensics] Guards) are `PatchCalls` sites whose call target is checked at
run time against the translated target (retail calls 0x4B40C0 = TS3A(0xC0FD60) and 0x60FC70 = TS3A(0x6C93D0), the folded
copies, so they install); 0xD7E62A ([GCRoots], [MemExp], both off in the shipped ini) fails its byte check and SKIPs.

Native selftest (Steam build) ok (one timing assert in unseenfbc-test.h flaked under the running chain, rerun ok).
selftest-all: 3 FAIL (autshadow, boatgetoutdiag, h4core selftest runners) are the same on the untouched base tree.

## 4. Managed side (open, needs Rodney)

`ts3fixes.package` (v0.2.12 rel0211b) is the same file for every edition, but [AutShadow] only takes script builds it
knows (MVID pairs / IL hashes of Sims3GameplaySystems, Sims3GameplayObjects, ScriptCore; `AutShadowIlHashes.cs`). The
retail build string 0.2.0.209 suggests separately built script assemblies [inferred], in which case townies' actions stay
on the main thread on retail (safe, the v0.2.11-EA behaviour) until the hashes are added. Needed: copies of the retail
`Game\Bin\gameplay.package` and `scripts.package` into `game-copy/retail/`; then `tools/autshadow-il-hashes.ts` adds the
build and a ts3fixes rebuild follows (or the packages hash the same as Steam and nothing changes).

## 5. Test plan (one retail install, Rodney or the harness later)

1. Back up `Documents\Electronic Arts\The Sims 3\`; ASI loader as for Steam; `ts3native-retail167.asi` renamed
   `ts3native.asi` + the v0.2.12 `ts3native.ini` into `Game\Bin`; v0.2.12 `ts3fixes.package` in Mods\Packages.
2. Start: `ts3native.log` has `release: v0.2.12 retail`, `host build: retail 1.67`, no `SKIP all`, no `FAIL`; count `SKIP`
   lines against a Steam v0.2.12 log (expected extra: [ProbeSnap] off anyway, nothing else).
3. Load a save, play 10 min at speed 3 with an unseen town, save, quit. ScriptError xml: none; `ts3native.log` no crash
   or `FAIL` line; `[AutShadow]` lines say which script build was taken (§4).
4. Harness later: `TS3_EXE` pointing at a retail install, the master smoke queue with this asi (bridge state `apex` only
   if Apex supports the retail exe; it gates features on the Steam stamp).

## 6. Release proposal

Add `ts3native-retail167-v0.2.12.zip` (asi + the unchanged v0.2.12 ini) to the existing v0.2.12 release, as the EA app
zip was: same version, same ini, same `ts3fixes.package`; players pick the zip for their exe. A v0.2.13 would mix in
unreleased master work. Notes draft: `docs/release-notes/v0.2.12-retail-dev.md`. Publish only after §5 passes.

## 7. Risks

- The Steam v0.2.12 asi on a retail exe passes its host check (name TS3W.exe at 0x400000): byte checks protect code
  patches, plain data reads at Steam addresses do not. A PE-stamp check in the Steam asi (as EA / retail have) belongs on
  master (decision for Rodney).
- This branch sits on the public release tree, not on master: it is a release branch, not a merge candidate. Master needs
  its own `port-run` + `retail-port-check` pass on master's literals (the tool and the overrides carry over).

## 8. Master port (2026-11-18 PHT, branch `feature/retail-167-port-1118`, static only, NOT RUN)

Trigger: issue #555 (a retail 1.67 tester, same PE 52D872DA / SizeOfImage 0xE5B000 / build string 0.2.0.209); his
port-probe report put 1,079 of 1,093 of his rows on our table's addresses. The 11-16 port sat on the v0.2.12 release tree;
this pass puts it on master (ts3native 0.36.276-retail1), so the retail asi carries every master feature.

- **Table:** `node tools/port-run.ts --exe game-copy/retail/TS3W.exe --ref game-copy/bin/TS3W.exe --allow-inferred --zero-unresolved --table native/ts3native/ts3addr-retail167.h --host-name "retail 1.67" --overrides docs/data/retail167-overrides.csv`
  -> 1,499 rows (was 1,298), `ts3bytes-retail167.h` 527 byte rows. `node tools/retail-port-check.ts --other game-copy/retail/TS3W.exe`
  exit 0: 1,483 rows with evidence, 0 FAIL, 0 unresolved, per-build constants ok [verified]. 16 rows are `unused`: raw
  literals port-run also maps (mintcall's per-build asm literals, the [MemDiag] AllocSitesOwners range bounds, the
  `#ifndef TS3A_TABLE` vtable list in crashfx.cpp, EA-only autshadow.cpp bounds, 127.0.0.1) that no `TS3A` / `TS3A_R` reads
  in the retail build; the tool now names them instead of failing.
- **New overrides (5, evidence in the csv)** [verified]: 0x56FA98 -> 0x56D048 ([AnimPhase] Split clip site: the third E8
  caller of the mapped target), 0x5BFB90 -> 0x80D470 ([GC] IdleGate stop function: 98/0 call votes; the old 0x5C03F0 has no
  caller), 0xD82110 -> 0xD825A0 ([StallWatch] range end, same delta as its start), 0x10652D4 -> 0x106630C and
  0x1088F48 -> 0x1089F88 ([EaCrashFixes] LotLevelPastEnd / [ThumbStall] vtable slots: the retail dword holds the mapped
  function).
- **Features EA 1.69 stands down for missing rows run on retail** (`ts3a_noaddr = false` under `TS3A_RETAIL`, ts3addr.h):
  [EqualsFast], [Binder] TypeHandleDirect, [Crash2Diag], [Startup] DxDiagCache, [GC] IdleGate, [StallWatch], [AnimPhase],
  [EaCrashFixes] LotLevelPastEnd / HeightsInfLoop / BridgeHeightNaN / FglStaleObject, [ThumbStall] / [ThumbCallers]
  (retail asm literals tied to the table by the static_assert), Satb cold park, [AutShadow] Races bits 0-1,
  [MonoThread] QuitStop, [ResourceCache] trims, [RouteAsync] FbApplyLog, [MonoJit] BoundCall; [Discourage] LostRemovalFix
  gets the EA build's call check (UolCalls).
- **`TS3A_R(x)`** (ts3addr.h): raw Steam literals that the EA table has no row for (audiodup.cpp, memaccess.cpp,
  pkgprobe.cpp, plandeadline.cpp CutOnWait detour). Raw on Steam and EA as before; translated on retail.
  `tools/ts3addr-check.ts` now checks both tables (retail: `TS3A` + `TS3A_R`).
- **Stand-down on retail (log line):** [MemDiag] AllocSitesOwners (`SKIP allocsites owners`: Steam owner ranges, the retail
  linker ordered functions differently). [PkgProbe] (off by default) keeps its Steam code hash, so it SKIPs on retail.
  Patch sites that fail their byte check as in §3: 0xD7E62A ([GCRoots], [MemExp], both off).
- **Left as on EA** (§2 list unchanged): [S3SSCompat] redirect, [SaveWrite] Apex yield, Apex FastMemory bit.
- **EA note (not changed here):** audiodup / memaccess / pkgprobe / CutOnWait use raw Steam addresses on EA too (off by
  default there; their byte checks fail closed for code, data reads do not) [inferred]; an EA table pass should add their rows.
- **Harness:** `tools/harness/game-exe.ts installedProducts()` also counts retail packs installed as sibling folders of the
  game root (`The Sims 3 World Adventures` next to `The Sims 3`), named EPnn / SPnn by pack name (self-check case; the name
  keys are not checked on a real disc install, ledger I-489-5).
- **Managed side:** `ts3fixes.package` is the same file on every exe; nothing in it reads a TS3W address. §4 still holds:
  [AutShadow] offload takes only script builds it knows, so on unknown retail scripts it stays on the main thread (safe).
