# 343. Far sidecar format (ST-14)

2026-11-26. Design only: nothing built, nothing run, no REA. Package ST-14 of docs/332 §2.2 and docs/340 §3.7.
Gate (docs/332): one schema, versioned, load-time cleanup rule. Builders: FO-5 (first writer), FS-18, LZ-2 / LZ-3 / LZ-9.

Claims are marked [verified] (read in our source or an earlier verified doc), [inferred] (to confirm at build) or
[community-reported].

## 0. Short answer

- One file per save, `<Documents>\Electronic Arts\The Sims 3\ts3native\far\<key>.far.jsonl`, kept by ts3native, in the
  folder and with the key of the existing played-household store (`native/ts3native/played.cpp`, docs/14 §8.6). It is
  JSON Lines: one header line, one block per world of the save, one end line with a line count and CRC-32.
- It holds only data that can be thrown away: world seed, model versions, per-world keys, far-Sim carry-over, drift
  ledgers, a world-history ring and a capped event log. Every save flushes far state to EA state first
  (docs/340 §3.5), so the `.sims3` never needs the sidecar.
- A second, small file in the same format, `<key>.far.repair.jsonl`, exists only when a save flush could not restore
  something (an L2 object, an alarm taken out of EA's list). It is written before EA's save and is the input of the
  load-time cleanup.
- Missing, damaged, too big, other version, stale or from another save: the file is ignored, the world gets a new
  seed, far state is derived again from EA state. The game save is never touched by any of these paths.
- This replaces the three formats that were about to be designed apart: far-Sim event log (docs/324 §2.5), far-object
  event log (docs/331 §6.4) and region state (docs/320 §7.6, see §11 for what stays out).

## 1. What already exists (build on this)

| Piece | Where | Reused as |
|---|---|---|
| Sidecar folder `ts3native\` under the game's user folder, promised to players as readable and deletable | docs/FAQ.md "Nothing of ours is stored in your saves" [verified] | parent folder |
| User folder lookup `UserSims3Dir` (shell-folder registry key, OneDrive-safe, empty string instead of a drive-root path) | `native/ts3native/apex.cpp` [verified] | same function |
| Save key `SaveCleanupCore.PlayedKey`: save file name, folders cut, `[A-Za-z0-9._-]` only, at most 100, `unsaved_<world>` before the first save; native check `PlKeyOk` | `mods/ts3fixes/src/SaveCleanupCore.cs`, `played.cpp` [verified] | same key, same check |
| Save As handling: key read again after every save, data written under the new key, old save keeps its file | `PlayedSyncKey` in `PlayedHouseholds.cs`, called from `DisposeSpread.cs` and `SaveCleanupSpread.cs` [verified] | same call sites |
| Atomic write: `<file>.tmp`, then `MoveFileExW(MOVEFILE_REPLACE_EXISTING)`, delete the temp on failure | `PlSave` in `played.cpp` [verified] | same, plus a flush (§6) |
| Script code cannot write files; ts3fixes sends text lines to ts3native over the `[UserDiag]` channel (`Route.GetOption2`, top byte `0x87`); the answer is one bool | `userdiag.cpp`, `UserDiag.cs`, docs/registry.md "UserDiag ops" [verified] | transport option a (§9) |
| Native reads and writes rooted ts3fixes statics and Mono arrays | `native/ts3native/autshadow-run.cpp` [verified that the pattern exists] | transport option b (§9) |

The played store stays as it is (one id per line, `played\<key>.txt`). It is not moved into this file: it has shipped,
it is read by another feature, and its content is household data, which this format never stores (§8).

## 2. Location and naming

```
<Documents>\Electronic Arts\The Sims 3\ts3native\far\<key>.far.jsonl          main file, one per save
<Documents>\Electronic Arts\The Sims 3\ts3native\far\<key>.far.repair.jsonl   only after an incomplete flush (§7)
<Documents>\Electronic Arts\The Sims 3\ts3native\far\<key>.far.jsonl.tmp      during a write only
```

docs/340 §3.7 first said "`<Save>.far.json` next to the save". The file is in the `ts3native\far\` folder instead
(Rodney 2026-11-26: outside the save folder; docs/340 §3.7 now says the same), for three reasons:

1. EA's save deletes the whole `Saves\<name>.sims3` folder and copies the working copy `CurrentGame.sims3` over it
   (docs/15 "Where a save goes", step 9) [verified]. A file of ours inside the save folder would be deleted on every
   save.
2. A file beside the save folders in `Saves\` is read by code we have not checked (the game's save list, the
   launcher) [inferred risk]. The `ts3native\` folder is ours.
3. docs/FAQ.md already tells players where our per-save files are.

One file per save, not one per world: a save folder holds one `<world>.nhd` per visited world, and a normal save
replaces all of them at once from the working copy (docs/15 step 9). One sidecar with one block per world is replaced
in the same step, so the blocks can never be newer or older than the worlds they describe (§5.2).

`<key>` is `PlayedKey` unchanged. The world name in a block is the world file name without folder and extension,
sanitized the same way [inferred: `GameUtils.GetCurrentWorld()` is `UserCreated` for custom worlds, so the file name
is needed; FO-5 confirms the API and logs both on the first run].

## 3. File format

UTF-8 without BOM, ASCII only (everything else as `\uXXXX`), `\n` line ends, one JSON object per line, at most 4096
bytes per line. No pretty printing. Keys inside an object are written in the order this doc lists them.

### 3.1 Value rules (determinism)

- Ids (`SimDescriptionId`, object id, lot id, household id never) are unsigned 64-bit, written as decimal strings.
- Sim time is ticks as a decimal string. No wall-clock time anywhere except `written` in the header.
- Integers are JSON numbers and must fit 32 bits. Floats are never written as decimals: a float is the 8 hex digits
  of its IEEE-754 bits (`"41200000"`), so it round-trips exactly and no locale can change it.
- No addresses, no hash codes, no enum numbers that differ between editions: enums are written by name.
- No Sim names, lot names or household names. The only names are the save name and the world name in the header and
  world lines.

### 3.2 Lines

| `t` | Line | Fields, in order | Notes |
|---|---|---|---|
| (none) | header, line 1 | `far` (container version, 1), `min` (lowest reader version that may read it, 1), `writer` (our build version string), `edition` (`steam167` / `ea169`), `kind` (`main` / `repair`), `save` (save name as the game gives it), `key`, `written` (UTC, `YYYY-MM-DDThh:mmZ`), `worlds` (number of W blocks) | the only line with wall-clock time |
| `W` | world block start | `w` (world name), `ticks`, `sims`, `seed` (16 hex digits), `mv` (`{"fs":n,"fo":n,"lz":n}` model versions), `n` (lines in this block after the W line) | `ticks` + `sims` are the stamp (§5.2) |
| `K` | per-world key | `k` (name), `v` (integer or hex float), `src` (package or measurement that produced it) | calibrated values such as the T3 edge (docs/340 §3.2, ME-4); never ini switches |
| `S` | far-Sim carry-over | `id`, `st` (state name), `ti` (next transition index), `c` (object of named integer credits) | only what EA state does not hold and the machine wants back: PRNG position and partial-effect credits |
| `D` | drift ledger | `id`, optional `to` (second Sim id), `k` (ledger name), `v` | accumulators behind the "bounded drift" rule; a pair with a household member is never written |
| `H` | world-history ring entry | `ticks`, `k` (weather, season, holiday, ...), `v` | recorded machine inputs (docs/331, docs/340 §3.4) |
| `E` | event log entry | `ticks`, `id`, `ti`, `from`, `to`, optional `lot` | the trace of docs/340 §3.4; written for DT-1 replays and diagnosis, never read back in game |
| `A` | held alarm (repair file only) | `id` (owner), `lot`, `cb` (callback type and method by name), `left` (ticks remaining), `rep` (repeat ticks or 0) | §7 |
| `P` | lot record of a non-instantiated object (repair file only) | `lot`, `id`, `cls` (full type name), `pos` (3 hex floats), `rot` (hex float), `f` (object of persisted fields, defined by SD-4) | the same record shape LZ-6 keeps in memory |
| `Z` | end, last line | `lines` (all lines before this one), `crc` (CRC-32 of all bytes before this line, 8 hex digits) | integrity (§5.1) |

A world block is the `W` line followed by its lines in the fixed section order K, S, D, H, E. The repair file uses the
same header, `W` and `Z` lines and the section order A, P.

### 3.3 Ordering

- World blocks: by world name, ordinal byte order.
- K by `k` ordinal; S by `id` numeric; D by (`id`, `to`, `k`); H and E by (`ticks`, `id`, `ti`); A by (`id`, `lot`,
  `cb`, `left`); P by (`lot`, `id`).
- Writers sort before writing and never write in dictionary or hash order.
- Result: two saves of the same state with the same build give the same bytes from line 2 to the line before `Z`.
  The selftest of the writer checks exactly that (§12).

### 3.4 Example

```
{"far":1,"min":1,"writer":"0.40.0","edition":"steam167","kind":"main","save":"My Town","key":"My_Town","written":"2026-12-02T11:05Z","worlds":1}
{"t":"W","w":"Sunset_Valley","ticks":"1834200","sims":212,"seed":"9c41d2e07b53a611","mv":{"fs":1,"fo":1,"lz":0},"n":4}
{"t":"K","k":"t3EdgeM","v":300,"src":"ME-4"}
{"t":"S","id":"7215113988226514","st":"HomeIdle","ti":41,"c":{"hunger":12}}
{"t":"D","id":"7215113988226514","k":"skillDrift","v":3}
{"t":"E","ticks":"1834100","id":"7215113988226514","ti":40,"from":"Work","to":"HomeIdle"}
{"t":"Z","lines":6,"crc":"5f0a91c3"}
```

## 4. Versioning and migration

- `far` is the container version. A reader accepts a file when `min` <= its own version. Unknown `t` values and
  unknown fields are skipped and counted, so a newer build can add line types without breaking an older one.
- A change that an older reader would misread raises `min`. An older build then ignores the file (status `version`)
  and overwrites it at its next save. A downgrade therefore starts fresh; nothing is lost that the save needs.
- Container migrations are native functions "lines of version n to lines of version n + 1", chained, each with a
  selftest case. Version 1 has none.
- Model versions (`mv`) are per owner: `fs` far-Sim machine, `fo` far-object machine, `lz` lazy instantiation. A
  mismatch discards that owner's lines of the block only (S, D and E of Sims for `fs`; object E lines and H for `fo`).
  There is no migration of model state: it is derived again from EA state, as docs/340 §3.4 says. The world seed and
  the K lines survive, because the seed formula already mixes the model version in.

## 5. Reading: integrity and binding to the save

### 5.1 Integrity

Checked by ts3native before any line reaches ts3fixes: file size at most the cap (§10); header parses and `kind`
fits the file name; `min` fits; last line is `Z`; `lines` and `crc` match; every `W` block has exactly `n` lines;
every line is at most 4096 bytes. Any failure: status `bad`, the file is left on disk and ignored. Lines that parse
as JSON but have a wrong field type are skipped and counted, the rest of the file is used.

### 5.2 Stamp: is this sidecar the one written with this save?

Nothing of ours can be put in the `.sims3` to bind the two, so the binding is a stamp of EA state:

- `ticks`: the world's sim clock at the save.
- `sims`: the number of `SimDescription`s in the world at the save.

At world load ts3fixes sends both values of the loaded world with the key. The block is used only when both are
equal. [inferred: the sim clock after a load equals the clock at the save, since the save runs paused (docs/15
step 1); FO-5 logs both numbers on its first run and the queue checks they match.]

| Case | What the reader sees | Result |
|---|---|---|
| Normal load of the last save | stamp equal | block used |
| Older save restored (EA's `.sims3.backup`, a manual copy of an old folder) | `ticks` differs | block ignored, status `stale` |
| Crash after EA's save finished and before our file was written | old file, `ticks` differs | `stale` |
| Save renamed or copied outside the game | no file under the new key | `missing` |
| Save deleted and a new game saved under the same name | `ticks` / `sims` / world differ | `stale`; overwritten at the first save |
| Save copied to another machine | no file | `missing` |
| File of another save copied over ours by hand, stamp equal by accident | block used | harmless: the content is derived data and is checked against EA state line by line (§5.3) |
| Two saves at the same tick while paused (build mode in between), second write fails | the failed write deletes the old file (§6) | `missing` |

`missing`, `bad`, `version`, `stale` and `toobig` all end the same way: empty far state, a new world seed drawn once
and kept in memory until the next save, one log line `far: <key> <world>: <status>` and one counter per status. No
dialog, no retry, no change to anything the game saved.

### 5.3 Per-line checks against EA state

A line is applied only when its subject exists and is allowed:

- S and D: the Sim id is a `SimDescription` of this world, alive, and passes the household rule (§8) now.
- P and A (repair): §7.
- Everything else for an unknown id is dropped and counted (`farSidecarOrphanLines`). A dropped line never causes an
  object or Sim to be created; only the repair pass creates anything.

## 6. Writing: order and atomic write

Save order, all on the sim thread:

1. Before EA's save: every machine flushes to EA state (docs/340 §3.5, docs/331 §6.4). Owners report what they could
   not restore. If that list is not empty, the repair file is written now (§7).
2. EA's save runs. We write nothing during it.
3. After the save returned success, at the call sites that call `PlayedSyncKey` today: read the key again (Save As
   and the first save give a new one), send the current world's block, and commit.
4. If EA's save failed: nothing is written. The old sidecar still matches the old save.

A crash at any point leaves either the old file or the new file, never a mix:

- ts3native writes `<file>.tmp`, calls `FlushFileBuffers`, then `MoveFileExW(MOVEFILE_REPLACE_EXISTING |
  MOVEFILE_WRITE_THROUGH)`. This is `PlSave` plus the flush.
- If the write or the rename fails (disk full, antivirus holding the `.tmp`): delete the temp, delete the old main
  file, log once, count `farSidecarWriteFail`. The next load then sees `missing`, not a stale file with an equal stamp.
  The game save is not affected and the player sees nothing.
- Blocks of other worlds are carried over byte for byte from the old file. Exception: a world left by travel in this
  session. Its block was captured in memory at the travel flush and replaces the old one, because the first normal
  save after the trip also replaces that world's `.nhd` (§5.2 stays true).

| Event | Sidecar action |
|---|---|
| Save | steps 1-4 |
| Save As, first save of a new game | key changes; the file is written under the new key, the old save keeps its file (played-store behaviour) |
| Rename, copy, delete outside the game | nothing at that time; next load is `missing` (rename, copy) or the file becomes an orphan (delete) |
| Travel (vacation, University, the future) | flush as for a save; the home world's block is kept in ts3native memory; no file write, because a travel save does not update `Saves\` (docs/15 step 7) [verified]; written at the next normal save |
| Return from travel | same in the other direction |
| World change through the main menu, quit to menu, exit | in-memory blocks and seeds are dropped (`far: close`); nothing is written |
| Edit Town, moving the household, reset | no sidecar action; owners flush first (docs/320 §7.6 rule) |
| Mod save paths that skip `OptionsModel.SaveGame` | no sidecar write, so the next load is `stale`; whether the flush hook has to sit lower (`LoadSaveManager.SaveGame`) is SD-4's question (§13) |

Orphans: at game start ts3native deletes `far\*.far.jsonl` files not opened for 180 days when the folder is over
64 MB, oldest first, and never a repair file. Each delete is logged.

## 7. Load-time cleanup of our damage

Rule (memory note "clean up our damage on load"): a feature that can leave bad state in a save ships with the cleanup.
For the far models the save is meant to hold plain EA state, so damage means the flush of §6 step 1 was incomplete.

**Repair file.** Written before EA's save, with the same atomic write, only when an owner reports items it could not
restore: `A` lines for alarms that are not in EA's alarm list at that moment, `P` lines for L2 objects that could not
be created again. Its `W` stamp is the stamp of the save that is about to run. A save with a complete flush deletes
the repair file.

**Repair pass.** At world load, before the first sim tick, own ini key (off = no repair, old behaviour), behind the
loading screen and unthrottled:

1. Repair file missing: nothing to do.
2. Stamp differs: the file belongs to a save that never completed or to another save. Ignore and delete it, log once.
3. Stamp equal: for each `P` line, create the object through the owner's normal go-live path only when no object
   with that id exists in the world and the lot exists; for each `A` line, re-arm through EA's alarm call only when
   the owner object exists and has no equal alarm. Both checks make the pass safe to run twice.
4. One log line and one counter per repaired item, per skipped item and per failed item. The file is deleted at the
   next save with a complete flush.

**Owner scans that do not need a file.** Each owner package brings one `CleanOnLoad` step for state that EA does
save and that a crash or a skipped flush could leave behind (a Sim still hidden or inert from T3, a lot still marked
parked, an L1 object without its native parts). Contract: runs after the repair pass, touches only entries that are
provably ours and dead, count-only mode first, own ini key, per-item log line and counter. Owners: FS-10 (Sims),
FO-5 (lots, alarms held in place), LZ-3 (L1), LZ-9 (L2).

**Limit, stated plainly.** If a flush was incomplete and the repair file is then lost, the missing object or alarm
cannot be brought back, because nothing of ours is in the save. Two rules keep that case out of a release:
`farFlushFailed` must be 0 in the save matrix for a feature to go ON, and L0 holds alarms in place (they stay EA
alarms, docs/331 §6.4) so only L2 can produce `P` lines at all.

## 8. Household data is never stored

- One writer in ts3fixes takes every line. It refuses a Sim, lot or object that fails the shared predicate
  (`Fixes.HouseholdExempt`): household Sims, the selected Sim, anything of unknown scope, the household's home lot,
  any lot a household Sim is on, and any D pair with a household member. Refusals count in
  `farSidecarHouseholdRefused`, which must stay 0; a non-zero value is a caller bug and fails the queue.
- `[Experimental] HouseholdToo=1` does not lift this. The switch never applies to save changes (CLAUDE.md), and the
  sidecar is treated as one.
- The reader applies the same test again at load (§5.3): a Sim that joined the household since the save loses its
  lines.
- The played-household store is a different file and stays out of this format.

## 9. Transport between ts3fixes and ts3native

The format is line based so that both existing transports can carry it. FO-5 picks one and measures it:

- **a. Text channel.** Lines `far: open <key> <world> <ticks> <sims>`, `far: put <line>`, `far: commit`,
  `far: repair-commit`, `far: close`, answers through one new UserDiag op (registry row at build). Works on both
  editions today [verified for the played store]. A write costs one call per 4 bytes and a read one call per bit
  [verified in `UserDiag.cs` and `userdiag.cpp`: the answer is a bool], so read-back has to stay small (§10). The
  `far:` prefix needs its own line cap, outside the shared 400 lines per prefix.
- **b. Rooted array.** ts3native fills or reads a rooted ts3fixes `byte[]` as `autshadow-run.cpp` does for its
  arrays. Bulk both ways. Whether the same addresses exist for EA 1.69 is not checked [inferred].

Either way ts3native owns the file, the integrity check, the carry-over of other worlds' blocks and the atomic write;
ts3fixes owns the content of the lines and the household filter. With ts3native missing, old, or `[UserDiag]` off,
`open` answers 0: far features that need a seed kept across saves run with a per-session seed, and nothing is
written.

## 10. Size budget, leaks and counters

| Part | Cap | Over the cap |
|---|---|---|
| Line | 4096 bytes | writer splits `f` / `c` over more lines of the same `t` and id, or drops the line and counts it |
| K per world | 64 lines | drop and count (a bug) |
| S + D per world (read back at load) | 512 KB with transport b, 16 KB with transport a | drop S first (derived again), then D entries with value 0, then the rest of D |
| H ring per world | 512 lines | oldest dropped |
| E ring per world | 2000 lines | oldest dropped (compaction) |
| World block | 1 MB | compaction order E, H, S, D; K and the seed are never dropped |
| Main file | 4 MB | blocks of worlds not visited for the longest time are dropped; a reader refuses a bigger file (`toobig`) |
| Repair file | 1 MB | a flush that needs more is a failed gate, not a bigger file |

4x and streamed worlds (AGENTS.md rule): only S and D grow with the number of far Sims, and both are capped with a
safe drop, so the file does not grow with the world past 1 MB per block.

Write cost target: under 50 ms for 1 MB behind the save dialog [inferred; FO-5 measures it and reports ms per save].

No leaks (memory note "no leaks in new builds"): ts3native frees the carried blocks and the travel capture on
`far: close`, on quit to menu and on a new `open`; ts3fixes clears its pending lines and the seed on the same quit
handler the played store uses. Counters (slots reserved at build with `tools/registry.ts`): `farSidecarBytes`,
`farSidecarLines`, `farSidecarNativeHeld` (bytes held in ts3native), `farSidecarStatus` per status of §5.2,
`farSidecarCompactions`, `farSidecarWriteFail`, `farSidecarOrphanLines`, `farSidecarHouseholdRefused`,
`farFlushFailed`, `farRepairDone` / `farRepairSkipped` / `farRepairFailed`. Queue checks: `HouseholdRefused` 0,
`FlushFailed` 0, `NativeHeld` 0 after quit to menu.

## 11. Editions and paths

| Point | Steam 1.67 (`TS3W.exe`) | EA app 1.69 (`TS3.exe`) | Effect on this format |
|---|---|---|---|
| User folder | `Documents\Electronic Arts\The Sims 3` | same [verified: one `UserSims3Dir` serves both since v0.2.8] | none |
| Localized user folder (for example a German install) | folder name is translated [community-reported] | same | `UserSims3Dir` always builds `The Sims 3`, so on such installs `ts3native\` is not in the folder that holds `Saves\`. Our files still find each other. Nothing in this format reads `Saves\`, on purpose |
| OneDrive-moved Documents | handled by the registry lookup [verified] | same | sync tools may lock the `.tmp`: write failure path of §6 |
| Save folder layout, `.nhd` per world, `.sims3.backup` | docs/15 [verified on Steam] | same layout [community-reported]; native save addresses differ | none: the stamp comes from script state, not from files or native addresses |
| Transport | a and b | a works; b not checked | §9 |

A sidecar written by one edition is valid for the other (`edition` is information only): a save moved between the
editions on one machine keeps its key and stamp.

Out of this format: docs/320 §7.6 planned `<Save>.lw/` with per-lot packages of paged-out lots that the save does not
contain. That data would be needed to load the world correctly, which breaks the rule this format is built on (the
sidecar is never needed). It belongs to the L3 / streamed-world work in Boundless Atlas and needs its own design with
a commit that is tied to EA's save. This RC materialises everything before the save, so it has no such data.

## 12. What the builders owe (selftests)

Native (`played.cpp` style pure functions with selftests) and ts3fixes writer:

1. Round trip: lines in, file out, file in, same lines.
2. Determinism: the same input in two different insertion orders gives the same bytes from line 2 to the line
   before `Z`.
3. Reader refuses: truncated file, wrong CRC, wrong `lines`, wrong `n`, line over 4096 bytes, file over the cap,
   `min` above the reader, `kind` not matching the name. Each gives its status and leaves the file alone.
4. Stamp cases of §5.2, including travel carry-over and Save As.
5. Write failure deletes the old main file and never touches another key's file.
6. Household filter: a household Sim, the selected Sim, an unknown-scope Sim, a household lot and a D pair with a
   household member are all refused and counted.
7. Repair pass is idempotent: run twice, second run creates nothing.
8. In game (docs/340 §7): the save matrix with the sidecar missing, stale and of another version; `.sims3` equal to
   the vanilla save in each case.

## 13. Open questions

| # | Question | Who closes it |
|---|---|---|
| 1 | Does the sim clock after a load equal the clock at the save, and is the `SimDescription` count stable across save and load, on both editions? If not, the stamp needs another EA value | FO-5, first run log |
| 2 | Transport a or b: cost per KB written and read, and b on EA 1.69 | FO-5 |
| 3 | Do mod save paths (NRaas Saver and others) reach `OptionsModel.SaveGame`, or must the flush and the sidecar commit hook `LoadSaveManager.SaveGame`? | SD-4, compat row in docs/328 |
| 4 | L2: is the repair file enough, or must each L2 record be on disk before its object is destroyed (write-ahead)? Decides whether L2 can go ON | SD-4, RX-9 |
| 5 | World file name API for custom worlds | FO-5 |
| 6 | Closed. Rodney 2026-11-26: outside the save folder, `ts3native\far\<key>.far.jsonl`; docs/340 §3.7 updated to match | - |
| 7 | Field list of `P.f` and of `S.c` | SD-4, SD-1 |
| 8 | Object ids stable across save and load (the P and E lines assume it) | RX-4 |
