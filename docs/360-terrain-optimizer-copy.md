# 360. Terrain optimizer height copy: lifetime, readers, release (2026-11-26)

Static only (Steam 1.67 `game-copy/bin/TS3W.exe`, Ghidra headless decompile + Capstone call scans, decompiled C#
for the managed callers). Nothing was run. Question from Atlas docs/015 §2.3: can the optimizer's second height
copy (4 bytes per sample) be freed after use, on EA worlds too?

## 1. Verdict

- **Freeing the optimizer copy alone, after use: not a build.** Its readers have no null check and run on every
  kd-tree chunk build; a recreate before each read is a full-map copy plus an error pass on the main thread (§3).
- **The game already frees it, together with the height map, through its own unload** (`0xC638E0`, the
  `World.UnloadHeightMapRelatedData` internal call). In the game the world loads in on-demand mode: neither the
  height map nor the copy is allocated at world load (§2). Atlas I-015-3 ("stays from world init to the next
  reload") is wrong for the game; it describes load mode 0, which no call site in the exe passes.
- **What is left is a narrower EA leak:** after a Build mode session with terrain edits, both blocks stay
  allocated in live mode until the next Buy mode / blueprint exit or Edit Town exit, because Build mode's exit
  has no unload call (§4). Candidate fix `[TerrainOpt] UnloadOnLive` (default off, §6): call the game's own
  unload from managed code once a native predicate says the terrain is settled. **Probe first** (§7): two open
  points decide it.

| Freed by the game's unload | 2048 m | 4096 m | 8192 m |
|---|---|---|---|
| Optimizer copy, N x N x 4 (N = 2^k + 1) | 16.8 MB | 67.1 MB | 268.5 MB |
| Height map, N x N x 2 | 8.4 MB | 33.6 MB | 134.3 MB |
| Both | 25.2 MB | 100.7 MB | 402.8 MB |

On EA worlds the gain exists only in sessions that used Build mode terrain tools and did not pass through Buy
mode or Edit Town afterwards [inferred from §4; size per session unmeasured]. It is not a flat 17 MB on every
2048 m world.

## 2. When the copy is built

Objects: `world+0x54` terrain data holder (`+0x08` height map, `+0x1C` "height map not loaded", `+0x1D`
"loaded on demand"), `world+0x58` terrain object (`+0x64` the same holder, stored at `0xC80561`; `+0x68`
builder), `builder+0x68` optimizer (`+0x00` row pointer table, `+0x08` N, `+0x0C` k) [verified].

- Allocation and copy: `0xC883E0` allocates N row pointers and one N x N x 4 block when N differs from the
  stored N (the free helper zeroes N, so a freed copy is allocated again), then `0xC88330` copies each 16-bit
  height into the low half of a 4-byte slot and calls `0xC87C60`, which fills the high half with a per-vertex
  error over the whole grid [verified]. Only callers: `0xC24220` (`0xC2424A`) and `0xC24F90` (`0xC24FBA`).
- `0xC24220` has three callers, `0xC24F90` one [verified, rel32 scan]:
  - `0xC85323` world init (`0xC85270`): only when `holder+0x1C` is 0.
  - `0xC84C04` in the terrain update `0xC845C0`: only when `holder+0x1C` is 0, the world mode `world+0x1B4` is 0
    (live), no dirty rect arrived this frame, the 1000 ms deadline set after the last dirty rect has passed
    (`+0x170`, QPC), and the accumulated dirty rect `+0x148..+0x154` is not empty. So: once, about 1 s after
    terrain edits stop and the game is back in live mode.
  - `0xC62BAF` in the load job `0xC62AC0` (posted by `0xC63840`): right after the height map is loaded.
  - `0xC27D65` in `0xC27CF0`, from the full rebuild `0xC605F0` (no direct caller; two table references).
- **World load mode.** The terrain data loader `0xC2ECA0` writes its third argument to `holder+0x1C`
  (`0xC2ECBB`); with 1 it never calls the height map loader `0xC2A290` and installs stored per-cell kd-tree
  shapes instead (`0xC2EECB`; shape kind inferred from the non-null tree argument, not read); with 0 it loads the height map (`0xC2EFA2`). The world load step `0xC622D0`
  picks `0xC2EFE0` (mode 1) or `0xC2F000` (mode 0) from the flag that `0xC6CF80` forwards, and both callers of
  `0xC6CF80` push 1 (`0xAABE2E`, `0xC6D9E3`) [verified]. So the game always loads in mode 1 and world init
  skips the copy.
- Height map loads in mode 1 [verified call sites, conditions partly inferred]:
  - `0xC6D61B` in `WorldManager_Update`: `world+0x1B4` not 0 (an edit mode) plus one of two pending
    predicates; calls `0xC2A290` with the on-demand flag. This loads the height map only; the copy follows at
    the `0xC84C04` gate.
  - `0xC63840`: internal call `World_LoadHeightMapRelatedDataImpl` (`0x787040`; managed: Edit Town entry,
    `EditTownModel.cs:719`, keep-loaded), bulldoze `0xB04B70`, and `0xC6BD08` in `0xC6BCC0`.
- **Save.** `0xC6BCC0` (called by `WorldSave` `0xC6D460` and by `World_BuildRoutingDataImpl` `0x78AE40`) acts
  only when the terrain dirty byte `world+0x46` is set: if the height map is not loaded it posts the load job
  (height map + copy), does its work, clears the byte and posts the unload again (`0xC6C0A8`). A save with
  dirty terrain is therefore a transient peak of the "Both" row; a save with clean terrain builds nothing. If
  the height map was already loaded, the save leaves it loaded [verified].

## 3. Who reads the copy

| Reader | Site | When |
|---|---|---|
| Triangle recursion `0xC88590` (error word of each midpoint), via `0xC888F0` | from `0xC889C0` and `0xC88AB0` | below |
| `0xC889C0`: triangle list of one chunk | `0xC2698B` in the kd-tree mesh builder `0xC26810` | every chunk built by `0xC84090` (update `0xC84E19`, `0xC84EF5`; init `0xC8534C`) and the rebuild job `0xC274A0` |
| `0xC88690`: vertex heights from the low half | end of `0xC889C0` | same |
| `0xC88B30`: threshold search for a triangle budget | `0xC24260`, `0xC24330`, `0xC24F00` | after each copy, update `0xC85182` / `0xC851D2`, rebuild `0xC605F0` |

- `0xC26810` reads `optimizer+8` and the rows with no null check; `0xC24330` and `0xC24260` test only the
  optimizer pointer, not the rows [verified]. A freed copy with the height map still loaded is a null read at
  the next kd-tree chunk build.
- These readers run only while the height map is loaded: with `holder+0x1C` set the update takes the stored-data
  path `0xC83D00` for changed chunks and never reaches `0xC84090` [verified branch at the top of the second
  half of `0xC845C0`; `0xC2A540` sends a chunk to `0xC83D00` when the holder is unloaded].
- Nothing on the camera, routing or save paths reads the copy directly; they reach it only through the chunk
  builds above [verified for the direct callers listed; indirect calls not excluded].

## 4. Free sites and the leak

- `0xC87C30` frees block and row table and zeroes N. Callers: `0xC24190` (from the unload job `0xC62BF0` at
  `0xC62C42`) and the builder teardown `0xC25850`. Plus the resize inside `0xC883E0` [verified].
- The unload job also releases the height map (`0xC29BC0`: deletes `holder+8`, sets `+0x1C`). It is posted only
  by `0xC638E0`, which does nothing while the keep-loaded byte `world+0x152` is set unless its argument forces.
  Callers [verified, rel32 scan]: internal call `World_UnloadHeightMapRelatedDataImpl` `0x787060`,
  `0xACE8BC` (lot operation `0xACE4A0`), `0xC6C0A8` (save, §2).
- Managed callers of the unload: `BuyController.cs:1619` and `BlueprintController.cs:732` (dispose, not
  forced), `EditTownModel.cs:741` (forced). `BuildController.Dispose` (`BuildController.cs:4838`) has none
  [verified, grep of the decompiled UI assembly].
- So: Build mode with terrain edits loads the height map (`0xC6D61B`), live mode builds the copy 1 s later
  (`0xC84C04`), and nothing unloads either until Buy mode, a blueprint, Edit Town, a lot operation through
  `0xACE4A0`, or a save that had to load the map itself [inferred from the caller lists; not seen live].

## 5. Water grid second height map (`0xC2A370`)

`0xC2A370` (one caller, `0x672B19` in the water grid build `0x6729D0`) loads a private height map object with
reference count 1. The build releases it at `0x673222` on its only path past the load, before it returns
[verified]. Transient: 8.4 / 33.6 / 134.3 MB for the length of one water grid build. It exists because the main
height map is normally not loaded (§2). If the load fails after the object was allocated, `0xC2A370` returns 0
without releasing the 100-byte object: a small EA leak on a failure path only.

## 6. Design: `[TerrainOpt] UnloadOnLive` (not built)

- Switch in `ts3native.ini`, default 0. Household rules do not apply (no Sim state). No detour, no byte patch.
- Native export `ts3native_terrainopt_ready`: true when all hold for 5 s: `world+0x1B4` is 0, `holder+0x1C` is
  0 and `holder+0x1D` is 1 (loaded on demand), `world+0x152` is 0, `world+0x46` is 0, the accumulated dirty
  rect at terrain `+0x148..+0x154` is empty and bit `0x20` of `+0x140` is clear. Read-only.
- Managed (`ts3fixes`): in live mode, not paused / CAS / build / buy / Edit Town, poll the export at a low rate
  and call `World.UnloadHeightMapRelatedData(false)`: the same call, with the same argument and from the same
  thread, as the game's own Buy mode exit.
- Recreate path: the game's own (`0xC6D61B` on the next edit mode, `0xC63840` for Edit Town, bulldoze, save).
  Nothing of ours to leak or reset; counters `tuUnloads` / `tuNotReady` (slots to be reserved at build).
- `world+0x46` in the predicate keeps the unload behind the terrain commit, so the gain appears after the first
  save or routing rebuild that follows the edit [inferred meaning of the byte, §2].
- Hook conflicts: none. No site of this doc is in docs/68 (ours, S3SS, S3PP, Apex); `node tools/hook-sites.ts
  --check` unchanged. `[HeightCache]` / `[TerrainGrave]` detour `0xC2A570` and `0xC2A840`; the unload job calls
  neither, heights do not change, so cached heights stay valid [inferred]. See open point O1 for shapes.
- Effort: probe 20-30 min build + one live job (~10 min); the fix itself 30-45 min build + one correctness job.

## 7. Open before any build

- **O1. Height-field shapes after an unload.** `0xC83200` (`0xC8334A` -> `0xC2A840`) builds cell shapes that
  point into the height array without holding a reference. The unload job frees the array and swaps no shape.
  Whether such cells still exist when the game's own Buy-exit unload runs was not read: if they do, this is an
  EA use-after-free that an extra unload would hit more often, and `[TerrainGrave]` does not cover it.
- **O2. Live state.** One probe line (`holder+0x08`, `+0x1C`, `+0x1D`, optimizer row table, `world+0x152`,
  `world+0x46`) after load, after a Build mode terrain edit back in live mode, and after a Buy mode exit.
  Expected from §2 and §4: 0 / loaded with copy / 0.
- O3. Thread of the load and unload jobs (posted through the scheduler `0x599160`, invoke `0x59A350`, then
  `0x59A500`): not read.
- EA 1.69 addresses: not mapped.

## 8. For Atlas (docs/012, docs/015)

- The 134 MB height array and the 268 MB copy at 8192 m are not resident in live mode; they appear in edit
  modes, Edit Town, bulldoze and saves with dirty terrain, and can sit there after Build mode (§4).
- The single 268 MB request still happens at the `0xC84C04` gate and in the load job; for large worlds the
  save-time load (§2) is the peak to plan for.
- Height reads in play go to stored kd-tree shapes, not to the height array, unless the map was loaded on
  demand [inferred, §2]; this changes H1 and §2.4 of docs/015.
