# SmellyMods CC String Merge: compatibility (static)

Written 2026-11-19. Static only: no game run, no build, the tool was not run. Update 2026-11-19 evening: F4 static RE done (§5a, ledger §543). F2 (compat-scan) and F3 (COMPATIBILITY.md) are done. F1 (`[UserDiag]` lines) is coded but not built. The F5 upstream text is drafted (§6a) and not posted. Rodney dropped the Phase 2 conversion and game queue: the tool is not run by us (§7).

Question (Rodney): "check this out and make it compatible": https://modthesims.info/d/701426/smellymods-cc-string-merge-faster-sim-menus-and-loading-with-lots-of-cc.html

**Short answer: compatible in strings mode, with no change needed on our side for correctness. It is an offline tool, not a mod.** It loads nothing into the game: no ASI, no DLL, no script, no hook. It rewrites `.package` files in `Mods\Packages` while the game is closed. Nothing of ours patches the same address, hooks the same managed method or uses the same resource keys. None of our caches is keyed on the package set across sessions. Static RE of EA's string tables (§5a) shows that two of its behaviours can change in-game text only in narrow cases, and neither is caused by us. In C1, a text-replacement mod's missing keys show EA text instead of blank. In C2, it depends on name order versus package priority (§5). Merge mode (`--merge`) also changes which file wins a duplicate resource (C3). The fixes on our side are small diagnostics and compat-scan changes (§6). Nothing of ours does the same job, so nothing needs to stand down (§4).

## 1. Sources

- **Page:** MTS d/701426, "SmellyMods CC String Merge", author **SmellyBelly**, uploaded 2026-11-19 (Programs and Utilities, Windows). Fetched 2026-11-19. The page text was read as data. It contains no text aimed at agents. It has an "AI disclosure" saying the tool was written with Claude's help; that is a statement about the tool, not an instruction.
- **Download:** `CCStringMerge-0.1.0.zip` (direct link, no login needed) holds `CCStringMerge.exe` (7,451,115 B), `Read me.txt` and `LICENSE.txt`. The exe's SHA-256 `5635fef9…861d67af` matches the hash on the page [verified]. The exe is a PyInstaller one-file build (`PyInstaller`, `_MEIPASS` and `python312.dll` strings inside) of the source below. The repo's `build.ps1` makes it with `PyInstaller --onefile` [verified strings + build script]. That the exe matches this exact source commit is inferred: the build is not reproducible byte for byte.
- **Source:** https://github.com/smellybellyy/ccstringmerge, MIT, `main` at `ebc3416` (2026-11-07). `ccstringmerge.py` (767 lines, standard library only), `tests/test_ccstringmerge.py`, `build.ps1`. Read in full.
- Local copies (git-ignored, never run): `game-copy/compat/smellymods-stringmerge/{src,dl}`.
- **Ours:** master at `e7a8faba5` (rc-1119b): `native/ts3native`, `mods/ts3fixes`, `mods/ts3agent`, `tools/compat-scan.ts`, `tools/harness`. Apex: `game-copy/apex-src` docs. S3PP: docs/17 §8.

## 2. What it does [verified source]

- **Default (strings mode):** it scans `Documents\…\Mods\Packages` (or a dropped folder), recursively. It reads every `.package` (DBPF 2.0 only) and takes out every STBL (`0x220557DA`). All strings go into **one STBL per language** in a new `Mods\Packages\CCStringMerge_Strings.package`: group 0, instance `(lang << 56) | random 56 bits`, written uncompressed. Each package is rewritten without its STBLs, and every other resource is copied byte for byte with the same index fields. A package that held only STBLs is moved out. File names and folders stay. A rerun pools the old strings package again and writes a new one with a new random instance.
- **Merge mode (`--merge`, opt-in):** it also concatenates all processed packages into `CCStringMerge_Merged_NNN.package` parts of 1 GB (`--part-size`; it refuses past 2 GB). A duplicate TGI keeps **the first package in name order** and drops the rest. The originals leave the folder.
- **Conflicts:** when two packages hold the same string key with different text, the first in name order wins, but a non-empty text always beats an empty one. The report counts conflicts and lists 20 examples.
- **Leaves alone:** packages that hold an S3SA (`0x073FAA07`): every script mod, including **our `ts3fixes.package` and `ts3agent.package`**. Also non-2.0 DBPF files, unreadable packages, `.sims3pack` files and DCCache.
- **Safety:** the originals move to `The Sims 3\CCStringMerge backups\<date>\files` (outside Mods) with a jsonl manifest. After writing, it checks every string and resource against the backup and rolls back by itself on any mismatch. To undo, drop the backup folder onto the exe or run `--undo`. It refuses to run while `ts3w.exe` or `ts3.exe` shows in `tasklist`.
- **Side effects:** no network, no registry access. Its only subprocess call is `tasklist`. It writes only to the target folder and the backup folder.

The author's numbers for 6,282 CC packages (~150,000 STBLs) are community-reported, not measured by us. Own-Sim pie menu: 949-1,200 ms before, 207-211 ms in strings mode, 142 ms in merge mode. Start to main menu: 106 s, 78 s, 29 s. Save load: ~220 s, 194 s, ~30 s.

**Why it helps** [verified, §5a]: EA keeps one hash table per STBL in a list. `GetString_A 0x59C5B0` walks the list and probes each table until one has the key with a non-empty text, and a key that no table has walks them all. So lookup cost grows with the number of tables, and merging thousands of CC tables into one per language shortens every walk. docs/17 also puts STBL layout at about 29 KB per table [community], so thousands of CC tables are also an address-space cost (§3, A1).

## 3. Overlap with our stack

| Area | Ours | Overlap | Status |
|---|---|---|---|
| Our packages | `ts3fixes.package` and `ts3agent.package` contain S3SA | Skipped by the tool (`any(e[0] == S3SA)`) | verified source |
| String lookup | `[namecache]` (`NameCache.cs`): caches Sim First/Last/FullName getters by stored string; cleared at every world load and on locale change | The tool changes tables only while the game is closed. Keys and texts are unchanged except in the C1/C2 cases, which the game shows the same way with or without the cache | verified code |
| String lookup | ts3agent `label_check` (docs/17 §8.3) | None. It is the right correctness probe for Phase 2: it hashes a sorted label sample, including `objects_*` catalog names | verified code |
| DBPF reads | `[MemAccess]` detour on ReadDecompress `0x4A8380` | The tool writes STBLs uncompressed (comp 0, size bit 31 set) and copies other resources raw. memaccess already masks bit 31 | verified code |
| Package DB locks | `[PkgLock]` close hook `0x7355A0` (docs/539 §13.9) | None. Merge mode means fewer packages and so fewer idle closes. Same lock order | inferred |
| Mods diagnostics | `[UserDiag]` Mods line (`userdiag.cpp`): counts packages with S3SA, CASP or OBJD; index read cap 2 MB | Merge parts with more than ~65k entries go past the cap and are counted as "other". The per-file CASP/OBJD counts become per-part. Diagnostics only | verified code; size effect inferred |
| Resource lookup | Apex `ResourceLookupCache` (FindProvider `0x4AFFC0`; emptied on any package-list change) | The tool runs offline, so the game sees the package list fresh at start. Merge mode shortens the list the cache is built over (complementary) | verified Apex doc |
| CAS | `CasTrim`, `CasTriSort 0x5D1960` / Apex `FastCasSort` | None. CASP resources are copied unchanged. In merge mode, `CASPartCache.package` is rebuilt over different files (a cache clear is advised; the harness `ccSet` already clears caches) | inferred |
| JIT / Mono caches | keyed on assemblies | Script packages are untouched by the tool | verified source |
| On-disk caches keyed on CC | none found (grep of `native/ts3native` for cache/index files) | n/a | verified grep |
| compat-scan | `tools/compat-scan.ts` reads package indexes, warns over 2 GB, matches known-bad names and keys (docs/23 §8) | 1 GB parts pass. Known-bad matches **by file name** are lost in merge mode; matches by key still work | verified code |
| Harness | `ccSet` moves a `tools/cc-gen.ts` set into `Mods\Packages\cc-synth` and back by file name; `mods-restore.ts` restores only our three packages | Strings mode keeps the file names, plus one new file. The set must be converted before it is moved in (§7) | verified code |
| S3SS | 51 byte patterns | None in the resource or string code (Apex doc) | community / Apex doc |
| S3PP | `StblBucketLayoutTuning` (STBL hash code `0x59AF50`-`0x5A0xxx`), `LocalizedStringCache` (`0x59C5B0`), `PackageIndexWarmup` | Same goal, different layer. S3PP works in game, the tool on disk. Both work together: fewer tables plus a memo. LSC's stale-miss risk (a key looked up before its table loads) does not grow, because the merged table loads with the other Mods tables | inferred |

No `docs/data/hook-table.csv`, `docs/data/compat-areas.json` or `tools/mod-conflicts.ts` exists on master yet; the mod-vs-mod scan is in progress as docs/295. The comparison above was done by hand.

## 4. Do we duplicate it?

No. Nothing of ours merges STBLs or indexes string lookups. `[namecache]` covers only Sim name getters, and the tool does not make it redundant: the getters still do the lookup and the `Ui/Global:FullName` format. **No stand-down needed.**

A possible in-game version of the same gain would be a native index from key to string over all loaded STBLs, hooked at `LocStrSvc_GetString_A` or the per-table Find `0x59AF50`. It would work without rewriting any file and would keep the game's own conflict order (C1/C2). S3PP's LSC is close to that. Per "prefer own fixes over S3PP" and "measure before build", this is idea **I1** for docs/23 (§6), measured first.

Prior art: S3PP `StblBucketLayoutTuning` and `LocalizedStringCache` (in-game, docs/17). Package mergers such as Delphy's Multi-Merger and S3PE merge whole packages but do not merge STBLs [community]. The STBL Duplicator (MTS d/371472) does the opposite and adds tables per language. NRaas: `NRaasPacker/STBL.cs` and the `STBL`/`unSTBL` tools build STBLs for NRaas's own packages; NRaas has no merging or lookup speedup [grep]. Web search found no earlier tool that merges CC STBLs to make the Sims 3 pie menu faster.

## 5. What can break

| # | Case | Effect | Ours? | Status |
|---|---|---|---|---|
| C1 | **Text-replacement or translation packages** without a script, whose STBL has the **same TGI as a game STBL** (overriding a whole EA table by key priority) | Before the tool, the mod's copy replaces EA's table outright: one copy loads per TGI, and keys the mod lacks show blank. After it renumbers them, both tables load. The merged strings package sits in Mods, which outranks the game, so the mod's text still wins for shared keys, and keys it lacks now show EA text instead of blank. Its text would stop showing only if Mods ranked below the game, which no normal Resource.cfg does | no (tool + EA) | verified (§5a), except the Mods-above-game priority [inferred] |
| C2 | Same string key, different text in two CC packages | **Game:** the first table in list order with a non-empty text wins: higher priority first, then registration order. All Mods packages share one priority, so registration order decides, and that is likely the file order of each Resource.cfg pattern. **Tool:** keeps the first in full relative path order, and a non-empty text beats an empty one, which matches the game's skip of empty texts. Same result when the two orders agree. They can differ between files in the Packages root and in subfolders, which are separate Resource.cfg patterns. Only for keys the tool reports as "Same key, different text" | no | verified game rule (§5a); registration order [inferred] |
| C3 | Merge mode with duplicate TGIs (CC overriding CC, e.g. defaults and replacements) | The first in name order is kept. Which one won in the game before is not verified | no | inferred |
| C4 | Merge mode with known-bad CC detection by file name (our compat-scan, `[UserDiag]`) | Names are gone; key matches remain | ours (diagnostics) | verified code |
| C5 | `[UserDiag]` Mods counts on merge parts | Undercounts CASP/OBJD in parts with indexes over 2 MB | ours (diagnostics) | verified code |
| C6 | Player reports | Without a log line we cannot tell that a Mods folder was processed | ours | n/a |
| C7 | Address space | **Positive:** ~29 KB of layout per table [community] times thousands of CC tables, down to 23 tables; and identical strings are stored once | n/a | inferred, measure in Phase 2 ([MemDiag]) |

Nothing found where our code crashes or misbehaves because of the tool.

A side effect that helps: when two CC packages hold the same STBL TGI, the game loads only the winner's table today, so the loser's keys show blank. The tool pools both tables, so those keys get their text back [verified game rule, §5a].

## 5a. EA string tables (F4, static RE, Steam 1.67, ledger §543)

- **Which tables load:** the table-list rebuild `0x59BDE0` asks the resource manager for every STBL key in every database (`GetKeyList 0x4B1AE0`, slot `0x20`, flag 0). Databases come in priority order, highest first, with no de-duplication. A key is kept when the top byte of its instance equals the service locale `+0x1C`, so the tool's `lang << 56` instance is the right encoding. Alternate-locale keys (`+0x24`) are appended after them [verified].
- **Loading:** `0x59B190` loads each table by key through the manager's slot `0xC`, which returns one winner per TGI: the highest-priority copy. It fills the first list node with that key; duplicate nodes stay empty and are skipped [verified].
- **When:** the rebuild runs at service start (`0x59BFF0`) and on a locale change (`0x59C430`). The change listener `0x59BFC0` puts new keys at the front of the list; when it fires at run time is [inferred].
- **Lookup:** each STBL is its own hash table (7,919 buckets, `STBL_Find 0x59AF50`) in a list at service `+0x48`; there is no merged map. `GetString_A 0x59C5B0` returns the first table in list order with a non-empty text. Cost is one hash probe per table passed, and a missing key passes all of them [verified].
- **Duplicates inside one table:** `0x59F750` keeps the existing entry, and the parser `0x59FA20` ignores the inserted flag, so the first entry wins [verified]. The tool writes each key once, so this does not come up for its tables.
- **Open:** the real Resource.cfg priority values (Mods above the game is inferred). Registration order among packages of equal priority (inferred: the file order of each Resource.cfg pattern).

## 6. Compat plan (our side)

| # | Change | Why | Size | Status |
|---|---|---|---|---|
| F1 | `[UserDiag]`: two extra lines, logged only when they apply; the `Mods\Packages` line keeps its format because `player-log-digest.ts` parses it. `diag: CC String Merge output in Mods\Packages: strings package yes/no, merged parts N` matches the names by `UdCcsmKind`. `diag: N packages with an index over 2048 KB: ...` covers merge parts the counts skip. Selftest has match and non-match cases | C5, C6: player reports | ~30 min + build | **coded, NOT BUILT** (build single-threaded after CHAIN-GCGATE-RR3 DONE) |
| F2 | `tools/compat-scan.ts`: `CCSM_RE` / `ccStringMergeNote` note the strings package and the merge parts (file-name known-bad matches are lost). `stblOverrides` / `stblOverrideNote` flag a non-script package whose STBL TGI equals a game STBL; the game index is read from the install's package indexes only when such a package exists. Self-check: regex match and non-match cases, synthetic STBL packages. Smoke on the real Steam install: 42 game STBLs (21 each in FullBuild0 and DeltaBuild0); a synthetic override package was flagged, and the two tool-named packages got their notes | C1, C4 | done | done |
| F3 | `docs/COMPATIBILITY.md` section "Tools that rewrite your Mods folder" | players | done | done |
| F4 | Static RE: EA STBL load and lookup model (§5a, ledger §543) | C1, C2 | done | done |
| F5 | Upstream note to `smellybellyy/ccstringmerge`: text drafted in §6a, **not posted** (needs Rodney's OK) | C2 order; C1 is cosmetic | draft | draft |
| I1 | Idea (docs/23): native STBL index by key at `0x59C5B0`, the in-game version of the tool's gain. Measure pie-menu build ms on `cc-2k` first | §4 | measure ~1 game run; build later | idea |

No change is needed in ts3fixes, ts3agent, the JIT, CasTrim, PkgLock or MemAccess.

### 6a. F5 draft (issue text for smellybellyy/ccstringmerge; not posted)

> **Conflict order and EA table overrides: notes from a static look at TS3W 1.67**
>
> Thanks for CC String Merge. We looked at how the game loads and searches string tables (Steam 1.67, static disassembly) to check it next to our mods (sims3-modernization). It looks sound; two notes on edge cases:
>
> 1. **Same key, different text.** The game walks its tables in database priority order and returns the first non-empty text. All Mods\Packages files share one priority, so the order in which the game registers them decides. We believe that is the file order of each Resource.cfg `PackedFile` pattern: root files and each subfolder depth are separate patterns. `scan()` sorts by the full relative path, so a file in the root and one in a subfolder can come out in a different order than the game's. Ordering the pool by Resource.cfg pattern order first, then by name within each pattern, would match the game more closely. Your empty-text rule already matches the game, which skips empty texts.
> 2. **Packages that replace an EA string table by key** (same TGI as a game STBL, e.g. translations or text edits). Today one copy loads per TGI, so the mod's table replaces EA's outright and keys it lacks show blank. After renumbering, both load. The mod still wins on shared keys because Mods outrank the game, and the missing keys now show EA text. That is probably fine, but it changes what players see. You might list such packages in the report, or leave their STBLs in place.
>
> Details: the table list is rebuilt from every database's STBL keys at service start and on a locale change. A key is kept when its instance's top byte equals the locale, which matches your `lang << 56`. Lookup is a linear walk of per-table hash maps, which is why merging helps.

## 7. Phase 2 plan: DROPPED (Rodney 2026-11-19: no need to run the tool)

The conversion and the `cc-stringmerge-correct` game queue below are not planned any more; the text stays for reference. What is left: build F1 after CHAIN-GCGATE-RR3 DONE (single-threaded), with the native selftest (`UdCcsmKind` cases), and merge with the next batch.

**Placement:** after the rc-1119b release-verify chain (until ~08:30 PHT Fri 11-20), then sp-rerun2, then gcgate. It is not part of the release gate.

**Fix branch** `feature/compat-smellymods-stringmerge-1119` (this branch): F1 + F2 + F3, then `selftest-all`. Build in a build window: ts3native for F1, no ts3fixes change. Register the build in `builds.json`. F4 runs as a static agent anytime. F5 after F4.

**Test sets (prep, game closed, no game run).** Running the tool's Python source needs Rodney's OK at Phase 2 start (third-party code). The source was reviewed: standard library only, no network.
1. Copy `game-copy/cc-synth/cc-2k` (cc-gen, has STBLs) to `game-copy/cc-synth/cc-2k-smstr` and `game-copy/cc-synth/cc-2k-smmrg`.
2. Run `python -I <scripts>/ccstringmerge.py --yes game-copy/cc-synth/cc-2k-smstr` (strings mode), and the same with `--merge --part-size 256` on `cc-2k-smmrg`. Use a copy of the script outside the download dir. The backups land in `game-copy/cc-synth/CCStringMerge backups/` (git-ignored).
3. The tool's report must show `Left alone: 0` and `Same key, different text: 0` (cc-gen keys are unique).

**Correctness queue** `cc-stringmerge-correct` (Apex + bridge, Bin state `apex`, release set rc-1119b or newer plus the F1 build, `releaseDefaults` ini, BP save, the same seed for every arm). Arms by `ccSet`: `cc-2k` (baseline), `cc-2k-smstr`, `cc-2k-smmrg`. Per arm:
- load to live mode (record the load time from the log);
- `label_check {dump: true}` (the `s3pp-label-check` repro);
- open the pie menu on the selected household Sim and on an NPC (agent action; teleport in if needed);
- `live-cc-cycle`, then `bb-cc-cycle` (buy CC placement and catalog names);
- `store-cas-browse` for CAS labels;
- save and reload once.

**Pass:** `label_check` `hash`, `empty`, `placeholder`, `mismatch` and `hash_empty` equal across all three arms. No new ScriptError XML. No crash or hang. The `[UserDiag]` Mods line shows `ccstringmerge=` in the two tool arms and not in the baseline. MemDiag commit and largest free block are logged (C7, no gate).
- The measurement smoke runs first (queue.ts does it): ccSet moved in and back, label_check read back, switch values read back.
- The queue must restore the plain `cc-2k` set and clear caches after every arm, which ccSet already does.
- Estimate: prep ~20 min, 3 game jobs ~10-15 min each.

**Optional (needs Rodney's OK, no gate):**
- One run on a real CC-heavy player save, e.g. Dalexlol's, with the tool in strings mode.
- A timing job for I1: pie-menu build ms on `cc-2k` vs `cc-2k-smstr`, 10 min A/B.
