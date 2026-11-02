# 232. EA app 1.69.47 port of ts3native: design (2026-11-02 PHT)

Question: ship EA app 1.69.47 support with the least new machinery and no risk to the Steam build. Inputs: docs/230
(coverage of the first EA image), docs/68 §7-§7.5 (tools), docs/231 (mod-version popup), `out/port/ts3-ea169-file-fec39efa/`
(EA-derived, never committed). [verified] = read in the CSVs/sources this session, [inferred] = reading of them.

## 1. What the map says [verified]
- `map.csv` 1,123 rows: exact 573, masked 268, ref 248, near 17, ambiguous 9, offset-changed 5, missing 3. `problems.csv`
  284 rows, 186 `ai=yes`: by section .text 19, .rdata 50, .data 117; spread over 42 source files, worst routesnap.cpp 40,
  replay.cpp 14, crashfx.cpp 9, memdiag.cpp 9, locowin.cpp 8, memexp.cpp 8. 44 of the 186 are `ported (by-value)` IC tables
  (`kSnIcDef[0..38]`, `kHxIc[0..6]`): patch-bytes-check already holds their 1.69 address, `gen` just has no rule for them.
  `ghidra-names.csv` places 0 of the 186 (code names only). Patch bytes: 244 ok, 120 ported, 5 mismatch (4 addresses,
  eafix/watchpt/crashfx/gcroots), 4 unmapped. Vtables: 27 named by our sources, 17 unshifted (vt_Portal 0xFED048 ->
  0x1038590, 3 votes), 10 shifted or resized (0xFEB848 221 -> 73 slots). `map.offsets.csv` 9 groups, 5 sites (steerro, watchpt,
  rtoro, memexp).
- Not in any source file with `ai=yes`: decompress.cpp, optsinit.cpp, waterjps*.cpp, alloc.cpp, cellmemo.cpp, movedwait.cpp
  (sigmap: 24 features fully mapped, docs/230). These are the first-smoke pool.
- `Matches` = `Readable(addr, n) && memcmp` (ts3native.cpp:47); `InstallDetour`/`PatchImm32` go through it. Plain data reads
  (`*(DWORD*)0x0116...`) have no check. Host gate today: name `TS3W.exe` + base 0x400000, else `SKIP all` (ts3native.cpp:4688). <!-- docs-code: ok -->

## 2. Options
**A. Codemod + compile-time table, second asi (`ts3native-ea169.asi`).** `addr-codemod --apply` wraps 1,393 literals as
`TS3A(0x...)`; default `TS3A(x)` = `(x)` (preprocessor: Steam object code cannot change). `/DTS3A_TABLE` build takes the pairs
from a generated header; constexpr lookup, so static initializers (130 `static const DWORD ... = 0x00...` lines [verified
grep]) stay constants, and 12 case labels (apex.cpp) + 5 inline asm (mintcall.cpp) stay literal `manual` rows [verified
docs/68 §7.1]. Unresolved rows: `gen` writes `0` (new flag `--zero-unresolved`), so a code patch at 0 fails `Readable` and
logs SKIP; a data read at 0 would crash, so every file with a zero row is OFF in the EA ini and CI asserts it. Players
install the asi for their edition; the host check selects by PE timestamp + SizeOfImage (6707155C / 0xEB5000 vs 52DEC247 /
0xE5A000). New code: ~40 lines gen, ~10 lines host check, ~15 lines build.yml. [inferred: counts from the §7.1 scan]
**B. One asi, runtime table.** `TS3A` as a runtime lookup breaks every constant context: the 130 static-initializer lines
(several hundred literals), 12 case labels, 5 inline asm, template/constexpr uses, array initializers (`kSnIcDef` 39 dwords,
`kHxIc` 7), and the 5 generated `vcallsites*.h` headers (5,917 rows, probe-only). Each becomes lazy init or an accessor: a
non-mechanical refactor of ~74 files that changes Steam code paths and adds a lookup to hot reads. Most machinery, and it
removes the Steam byte-identity guarantee. Rejected. [inferred]
**C. Apex-style runtime signature scan (`sigmap-ea169.json`).** 740 of 875 code sites unique, but sigmap has no data
addresses at all (code sites only, tools/sigmap.ts header) and no ref-following; the 248 `ref` + 167 unmapped data rows
would need exe-addr-map's ref logic rewritten in C++ inside the asi, plus signature storage and ambiguity handling at run
time with no offline review. Also [inferred] the EA `.text` is decrypted after the DRM wrapper runs; a scan in DllMain may
see ciphertext. Largest new machinery; its one advantage (future EA patches without a rebuild) is not asked for. Rejected.
**Recommendation: A.** It reuses port-run/gen/patch-bytes-check as built, the table is addresses only (committable like
`docs/data/sigmap-ea169.json`), and Steam stays provably byte-identical.

## 3. Handling the problem classes under A
- 186 `ai=yes`: 44 by-value IC rows -> `gen` takes patch-bytes `ported (by-value)` as table rows (port-run already has them).
  142 left -> `0` in the table + their 42 files OFF on EA. Resolve only the ones a wanted feature needs, in Ghidra on the main
  laptop (`probe-out/ts3-ea169-file.exe` + `ghidra-names.csv`), recorded in `docs/data/ea169-overrides.csv`
  (steam,ea,label,evidence; addresses only) that `gen` reads. The 6 ts3native.cpp rows (0x676340.., 0x74CFC0, 0xE4E168) and
  3 `ts3native.cpp` data rows come first: they sit outside any feature switch. [inferred: check at task 5]
- 5 mismatches, 9 ambiguous, 3 missing, 17 near: `near` passes `--allow-inferred` and is protected by `Matches`; the others
  stay 0 -> their files (eafix, watchpt, crashfx, gcroots, routephases, allocsites, steerro, p4clone, avoidro, memexp, rtoro)
  OFF until hand-verified.
- 120 `ported` byte expectations: expected bytes embed Steam rel32/pointers, so `Matches` SKIPs on EA (safe, feature inert).
  Not in this milestone; later a per-build byte table from the same `gen`. First smoke uses only files whose expectations are
  all `ok`.
- 407 slot-shifted vtables, 5 offset-changed sites: slot numbers and struct displacements are not addresses, the codemod does
  not touch them. steerro, watchpt, rtoro, memexp, lotfd (vt_Portal) OFF on EA; re-derive by hand later (docs/230 reading).
- Gating rule: EA ini = Steam ini with every section `Enabled=0` except an allowlist in build.yml (same awk style as the
  release check); CI fails if an allowlisted section's module file appears in the header's `// unresolved` list.
- Steam identity proof: the `--apply` commit is a separate PR; CI artifact of its parent (main) vs the PR artifact: sha256 of
  the `.text`/`.rdata` sections (parsePe from tools/exe-addr-map.ts, `node -e`) and `ts3native.map` minus the timestamp line
  must be equal. Build.cmd already writes the .map.
- Table review/regeneration: `node tools/port-run.ts --exe probe-out/ts3-ea169-file.exe --allow-inferred` writes
  `native/ts3native/ts3addr-ea169.h` (sorted pairs + `// unresolved: 0x... file:line` comments, no EA bytes); review = git diff.
  Rerun after any merge that adds literals.
- Managed lane (separate, after native smoke): extract the 1.69 assemblies on the EA laptop (`tools/extract-assemblies.ts`,
  into a gitignored dir), `port-report` managed stage -> `.managed.csv` (ts3fixes methods changed), rebuild ts3fixes with
  `REF_DIR` pointed at 1.69 -> `ts3fixes-ea169.package`. Version-1 S3SA header, so no mod-version popup from ours (docs/231);
  the popup itself comes from 1.67 core mods and is answered by the harness guard in docs/231 §2.

## 4. Branch and merge plan around v0.2.5 (PHT)
- Mon 11-02 .. Thu 11-05: branch `ea169-port` from main. Only tooling + ts3addr.h + host check + build.yml there; no
  `--apply` (79 files rewritten would conflict with every main commit until the release). Freeze Tue 11-03, ship Thu 11-05 from main.
- Fri 11-06: merge main into the branch, run `--apply` as one commit (idempotent; a second scan must show 0 `wrap` rows),
  identity proof (task 4), PR to main. After that every merge from main is `--apply` again, never a conflict resolution.
- First EA smoke can run before 11-06 from a branch CI artifact (PR builds run `native-ea169`), with `--apply` done on the
  branch only and dropped before the merge.

## 5. Tasks (effort per CLAUDE.md: minutes/hours wall-clock; game runs on the EA laptop, correctness only)
| # | task | files | agent | time | EA runs | verify |
|---|---|---|---|---|---|---|
| 1 | `gen --zero-unresolved`, by-value rows, committed output `native/ts3native/ts3addr-ea169.h` with `TS3A_HOST_PE/SIZE` defines and `// unresolved` list; ts3addr.h includes it | tools/addr-codemod.ts, tools/port-run.ts, native/ts3native/ts3addr.h | sonnet | 45 min | 0 | port-run prints gen 1,031 addresses, 142 zero rows; `node tools/selftest-all.ts --ci` green |
| 2 | host check: under `TS3A_TABLE` accept any host name whose PE timestamp+SizeOfImage match the table; Steam build unchanged | native/ts3native/ts3native.cpp | sonnet | 20 min | 0 | EA log `host Sims3Launcher.exe ... pe 6707155C size 0xEB5000`, no `SKIP all` |
| 3 | build.yml job `native-ea169`: one `cl ... /DTS3A_TABLE` line (no selftest), EA ini from the Steam ini via awk allowlist, assert allowlist vs unresolved list, artifact `ts3native-ea169` | .github/workflows/build.yml | sonnet | 40 min | 0 | PR artifact has asi + ini; a deliberately wrong allowlist entry fails the job |
| 4 | `--apply` on the branch, identity proof vs main artifact (`.text`/`.rdata` sha256, .map diff) | native/ts3native/*.cpp (79 files), docs/68 §7.1 note | sonnet | 30 min | 0 | both hashes equal; second scan 0 `wrap` rows |
| 5 | resolve the 9 ts3native.cpp rows + any zero row in the smoke pool in Ghidra (main laptop), `docs/data/ea169-overrides.csv` | docs/data/ea169-overrides.csv, tools/addr-codemod.ts (read overrides) | reasoning | 2 h | 0 | port-run: those rows leave the unresolved list with evidence text |
| 6 | first smoke (§6) **done 2026-11-02: PASS on the second run (ini fix 80b215b, Verify=1; lot reached, 5 min soak, see end)** | ts3native-ea169.ini on the EA laptop | reasoning | 1 h | 1 (+1 rerun) | log: host line, no `SKIP all`, every on-feature logs its install line, no `FAIL`, reach the main menu and a lot |
| 7 | widen: features with only `ok` expectations and no zero rows, one per run | allowlist in build.yml | sonnet | 30 min each | 1 each | same as 6 plus the feature's own first log line |
| 8 | managed lane (§3 last bullet) | tools/setup/build-fixes.ts (REF_DIR arg), docs/231 | reasoning | 3 h | 1 | `.managed.csv` reviewed; game starts with ts3fixes-ea169 and no popup |
| 9 | post-release: `--apply` on main, PR, docs/68 §7.1 "applied" | as task 4 | sonnet | 30 min | 0 | identity proof repeated on main |

## 6. First smoke on the EA laptop
ON: `[Decompress]`, `[OptsInit]` only (both sigmap fully mapped, shipped on Steam, every site byte-checked, their files have no
`ai=yes` row [verified]). Add `[WaterJps]` on the rerun if its files show no zero row. OFF: everything else, in particular
`[GC]`/gcroots (mismatch kMxNotifySite), `[UnseenFallback]` (unseen.cpp 5 rows), `[RouteAsync]`/`[RouteGate]`/`[Discourage]`
(routing modules with offset-changed and ambiguous sites), `[SaveWrite]` (savecompress.cpp 2 rows), `[LotRemoveGuard]`
(lotfd vt_Portal), `[UserDiag]`, `[CrashReport]`, `[MemDiag]`, `[Verify]`, steerro, watchpt, mintcall, apex. If every patch
logs `bytes differ`, suspect `.text` still encrypted at DllMain [inferred]: then defer installs to the first hooked frame.

## 7. Open
- Decryption timing at asi load (first smoke answers it). - 120 `ported` byte expectations need per-build bytes before most
features can go on. - steerro/watchpt struct layouts: rewrite, not port (docs/230).
- Not checked by the design: the ini-section-to-source-file map the task 3 allowlist assert needs (by hand), and whether
  selftest.exe compiles under `/DTS3A_TABLE` (task 3 skips it). Risk: a feature left ON whose data read hits a zero table
  row crashes at load; the ini allowlist + CI assert is the only guard.

## First smoke 2026-11-02 (task 6)
Run 1 12:27 PHT: CI artifact `ts3native-ea169.asi` (run 41387870205, 1,135,104 B, sha256 `bfef6925...cf040f`) as `Bin\ts3native.asi` with the
EA ini (Decompress and OptsInit `Enabled=1`, 64 sections `Enabled=0`), launched through `ts3probe.py serve --launch`, Bin pair backed
up as `.pre-ea169`. Log head [verified]: `ts3native 0.36.152`, `host Sims3Launcher.exe base 0x00400000 pe 6707155C size 0xEB5000`,
`host build: EA app 1.69.47`, no `SKIP all`. Installed: `OK   decompress: call 0x004EC1A5 -> RefpackHook, mode 1 verify 0 (0x004EB4F0
starts 83 EC 10 8B 4C = game code at load time)`, `OK   detour RoutePlanOptions_ctor (I-94-322 hub refs) at 0x00654240`, `OK   OptsInit:
search options hub references +4 / +0x34 start at 0 (I-94-322)`. No `FAIL`, no `bytes differ`: `.text` is decrypted when the asi loads
(§7 open point closed; the one `SKIP detour Route_GetOption at 0x0074DF80: prologue differs` is the unconditional NOYIELD detour in
ts3native.cpp, nothing written). `GC patches disabled (Enabled=0)` (not `SKIP GC`: the ini gate runs first). Decompress worked through
the menu and the save load: `calls 21135 fails 0 mismatches 0 in 54 MB out 131 MB time 162 ms` (Verify=0, so output bytes unchecked).
- Not in the §6 ON list but installed: `OK   arena tail pad: 6 sites` and `OK   initobj size: transform call 0xE63427` ([ArenaGuard]
  `TailPad=1` / `InitobjFix=1` are independent of `Enabled`, the awk allowlist keeps them). TailPad also read the zero row 0x011EE508
  (`arenatail.cpp:233`, `// unresolved`), seen as `exception C0000005 at 0x5D40CC02` inside the asi on the main thread, caught by its own
  `__except` [inferred from the log order and the unresolved list]. Task 7 rerun: set both to 0 or resolve the row first.
- Run stable: no. `load_save Baseline-SV-Spring` ended in `A serious error has occurred while loading 'Baseline-SV-Spring'` after 10 s (no
  new `ScriptError*.xml`, no crash, game alive at the menu). Rerun 12:43 with the `.pre-ea169` pair (`SKIP all`, c035abe5a3a6) and the
  same Mods and save: identical failure at 9 s. So the load failure is not the ea169 asi. Cause (isolated 13:04, docs/233): the smoke
  driver passed `file: "Baseline-SV-Spring"` without `.sims3`; cycle.ts passes `Baseline-SV-Spring.sims3` and the same save then loads
  in ~12 s. Not the cause: ts3agent/ts3fixes (sha256 `e9b98bf37d90`/`b067efaa9047`, identical to the 12:09 change set `344a2752d415`),
  the 260 B `scriptCache.package` (the game writes it at every start). The restamped ErrorTrap P169 is a separate failure: it crashes
  the load (`0xc0000005`), so it is disabled (docs/233). Task 7 reruns must load `Baseline-SV-Spring.sims3`.
- Verdict: host check PASS, decryption timing PASS, Decompress + OptsInit installed PASS, lot reached FAIL (environment). CPU/WS: only
  at the menu (300 MB WS at attach), no soak. Both runs' logs: `probe-out/ea-smoke6-1102*.log`, `probe-out/ea-smoke6-1102.ts3native.log`.
- Task 7 candidates (files with 0 unresolved rows and 0 problems.csv rows, own ini gate): `[WaterJps]` (waterjps.cpp + wastar.cpp, §6
  names it), `[SlotEnabledRO]` (slotro.cpp), `[CellMemoProbe]` (cellmemo.cpp, probe only). replandedupe/plandeadline/movedwait are clean
  too but need `[RouteAsync]` (unseen.cpp 5 rows, routing sites offset-changed).

### Second run 2026-11-02 (ini fix 80b215b, Verify=1)
13:22 PHT, CI artifact of 80b215b (asi sha256 `ff4f6ef2...f87366`, ini `730b6afa...944f0` with `[ArenaGuard]` `TailPad=0` / `InitobjFix=0`),
installed as the Bin pair with the 12:43 pair backed up as `.pre-ea169b` (`c035abe5a3a6` / `8aa37522782a`, restored after the run, sha-checked);
`[Decompress] Verify=1` set in the installed ini for this run only. Driver: `ts3probe.py serve --launch` + `cycle.ts --save Baseline-SV-Spring.sims3`
(agent after 14 s, "Mod Scripts Found" accepted, world loaded 14 s, `LiveModeState`, households 32, Keaton). **Verdict: PASS** [verified]:
- Log: `host Sims3Launcher.exe base 0x00400000 pe 6707155C size 0xEB5000`, `host build: EA app 1.69.47`, no `SKIP all`, no `FAIL`, no `bytes differ`.
  `OK   decompress: call 0x004EC1A5 -> RefpackHook, mode 1 verify 1`; `OK   detour RoutePlanOptions_ctor (I-94-322 hub refs) at 0x00654240`;
  `OK   OptsInit: search options hub references +4 / +0x34 start at 0 (I-94-322)`. ArenaGuard off as intended: `arena guard disabled (Enabled=0)`,
  `arena tail pad disabled (TailPad=0)`, `initobj size fix disabled (InitobjFix=0)`; no exception inside the asi (run 1's `0x5D40CC02` is gone).
- Decompress with verification over menu, load, 5 min live, one in-world reload and a clean quit (`quit_game`, process exit writes the
  end line): `decompress: mode 1 verify 1 calls 45625 fails 0 mismatches 0 in 215 MB out 619 MB time 450 ms max 14502 us`: the RefpackFast
  output equals the game decoder's on 1.69.47 too.
- Soak 13:24-13:29 (every 30 s): WS 1580 -> 1541 MB flat (1542 at the end), commit 1415 MB flat, game CPU 3.4-3.7 % of 16 logical CPUs
  (about half a core), 67 threads; no new `ScriptError*.xml` (only the 12:11 one from before the run). Logs: `probe-out/ea-smoke6b-1102.log`,
  `probe-out/ea-smoke6b-1102.drive.log`, `probe-out/ea-smoke6b-1102.ts3native.log`, events `probe-out/20261102-132245`.
- Not ours, logged by the asi's first-chance exception logger: 3x `exception C0000005 at 0x04697CC2` on thread 27868 (page-aligned read faults at
  `0x42841000`, `0x38DB1000`, `0x41485000`, eip in a JIT/heap region, outside the asi module 0x5D400000-0x5DB74000, ebp 0, same esp each time),
  during the lot load; the game did not notice. Steam logs show no such lines [inferred: Mono/GC first-chance faults on 1.69; check against a
  `SKIP all` run before reading anything into it].
- Also in this run: the ts3agent `LoadSave` fix (bare name gets `.sims3`, package `3eb7bb6fc994`) checked live: `load_save {"file":"Baseline-SV-Spring","reload":true}`
  answered `{"loading":"Baseline-SV-Spring.sims3"}` and reached `LiveModeState` again in ~40 s. No EA app relaunch followed the clean quit (150 s watch).
