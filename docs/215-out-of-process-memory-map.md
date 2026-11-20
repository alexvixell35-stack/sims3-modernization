# 215. Out-of-process memory map: which address-space owners could live in a helper process

Static research, 2026-10-31. No game run, no build. Question (Rodney): which memory in TS3W.exe's 32-bit address space (Error 12 = the largest free block runs out) could live in one or more helper processes, each with its own address space (64-bit when it runs no game code), with a sim/main-thread budget of 70-100 ms/s for the IPC?

Rule carried over (established before this doc): raw pointers cannot be wrapped. Paging data back in on access needs the address range reserved anyway (no saving), swapping windows under live pointers is unsafe, and binary translation of every access is too slow. So only memory the game reaches **by key, ID or handle through a narrow call surface**, or **caches it can rebuild on a miss**, can move.

Tags as in AGENTS.md: `[verified]` (disassembly, exe bytes, measurement), `[measured]`, `[inferred]`, `[community]`. Steam TS3W.exe 1.67, image base `0x400000`. Machine-readable table: `docs/data/oop-memory-owners.csv`.

## 0. Summary

- **Most of the address space is not movable.** The three largest owners that are known (Boehm GC sections 376-408 MB, the Mono native heap ~100 MB, animation clips and other in-use resource objects inside the EA heap cores) are live object graphs that the game and the Mono interpreter reach through raw pointers. Thread stacks, module images and allocator reserves are OS or allocator memory.
- **Two owners are movable through a narrow API and are large:**
  1. **The renderer stack (DXVK + the NVIDIA Vulkan driver), ~430-640 MB** [measured parts, sum inferred]. The game reaches it only through D3D9 COM interfaces; pointers it gets back (`Lock`/`LockRect`) are valid only until `Unlock` by the D3D9 contract. A proxy `d3d9.dll` that streams the calls to a 64-bit process running DXVK x64 would take the driver's 128-256 MB reserves, DXVK's private memory and mapped texture window and ~118 MB of driver and DXVK images out of the game. The cost is per-call volume (hundreds of thousands of one-way calls per second on the main thread), not round trips, and presenting into the game's window from another process is unproven. Biggest prize, biggest risk.
  2. **Resource read buffers (`ResourceLoad/PackedStream/Raw`, `.../DecompressedRecordData`, `Services/EAIO/EAStreamMemory/data`), 112-192 MB of permanent EA heap cores** created at world load by transient buffers, plus ~16-20 MB/s of stream churn in play [measured]. Access is key -> bytes -> parse -> free [verified docs/08, measured churn]. A helper that reads and decompresses into a reused shared window removes them; ~300-800 requests/s [inferred], 0.3-24 ms/s.
- **Smaller movable owners:** string tables (copy-out lookup [verified docs/54]; ~20 MB left with S3PP, ~90 MB stock, up to ~290 MB on big setups [community]), the routing clone (docs/194, already designed), the audio binary cache (cap only), package indexes (~18.5 MB).
- **Unknown owners that block a full answer:** ~270-283 MB of untracked private allocations (16 MB blocks, several first seen at saves), an 86.8 MB pagefile-backed view at a save, 42-125 MB of reservations with no game frame, and EA cores from unnamed sites (`0x5C32E2`, EASTL allocator callers). If the save-time blocks are save write buffers, a save writer in a helper would be a third movable owner [inferred].
- **Recommended first target: the resource read buffers** (§4). It is the largest movable owner whose call surface is already mapped, whose helper needs no game code (64-bit), and whose IPC cost sits well inside the budget. The renderer proxy is the second track, gated on two no-build checks (§4.2).

## 1. Method

1. **Owners and sizes** from the AllocSites runs (docs/201 §13-14): queue `alloc-sites4` 2026-10-26 (ts3native 0.36.88, S3SS + S3PP + DXVK, Apex not loaded), jobs `bp-travel-allocsites` (1 travel round trip + saves, 10 min), `bp-cc-allocsites` (3 live CC cycles + inventory cycles), `bp-bb-allocsites` (Buy/Build cycles). Read with `tools/memory-report.ts` and a script over the `allocsites: ... live` rows (top ~40 rows per sample, so per-owner sums are **lower bounds**). Cross-checked with docs/66 §2-3 (region totals) and docs/190 §2 (37-site inventory with movable verdicts).
2. **Site names** from the EA allocator tag string pushed before the allocation call, read from the exe bytes (`dumpbin /disasm /range` on `game-copy/bin/TS3W.exe` and a PE-section read of the pushed string) [verified]: `0x72D385` pushes `ResourceLoad/PackedStream/Raw` (new, this doc); `0x72D62B` `.../DecompressedRecordData`, `0x57A593` `Animation/ClipResource`, `0x4DEC17` `EAStreamMemory/data` (docs/201 §13.2). `0x626C9A` and `0x663802` push the EASTL allocator's source-path string only (no owner tag); `0x5C32E2` calls `Malloc 0x4E69D0` with no tag, its sampled caller `0xAE00C5` sits in the lot imposter / `HolidayLights` block [verified strings; owner inferred].
3. **Access check** per owner from the existing maps (docs/08, 35, 37, 39, 40, 41, 42, 54, 57, 58, 119, 190, 194) plus the allocate/free totals per site (`aMB`/`fMB`) in the logs: a site whose bytes are freed within a sample interval is transient; a site whose cores stay is permanent.
4. **IPC cost** = boundary crossings per second x per-crossing cost, at ~1-5 us (shared-memory ring, spinning consumer) and ~10-30 us (event wake), the same two models as docs/194 §6.2. Copy cost ~0.1 ms per MB (docs/08 §6.4). Crossings are [inferred] where no counter exists (§5 lists them).
5. **Data caveat:** the runs are stress runs (CC dress/undress cycles, 400 synthetic objects, Buy/Build cycles, one travel trip). Owners that grow with those actions (CAS geometry, clip cores, EA cores from resource reads, the second 128 MB driver reserve) can be smaller in normal play; the load-time owners (Boehm, PackedStream cores, Mono heap, driver first reserve) are the same in normal play. Each row says which.

## 2. Ranked owners (address space, MB)

Sizes: travel job first -> last sample, then the CC / BB end values. "Survives removal" = still held after the stress objects were removed and a GC ran (docs/201 §13.3).

| # | Owner | MB | Growth / survives removal | Normal play differs? | Access (how consumers reach it) | Verdict |
|---|---|---|---|---|---|---|
| 1 | **Boehm GC sections** (`0xD70916` from `GC_win32_get_mem 0xE4EEF0`) | 376 -> 408 (CC 393, BB 388) | +16-35 per run, never returned (docs/198) | no (load-time 352 + growth steps) | managed objects; the interpreter, ICs and native glue hold raw pointers; Boehm scans in place [verified docs/57, 198] | **not movable** |
| 2 | **Renderer stack: NVIDIA Vulkan ICD + DXVK** | driver reserve `nvoglv32.dll+0x8ACB7E` 128 (BB 256); DXVK private (`ucrtbase < d3d9`, `d3d9+0x237EF3`, `d3d9+0x11F665`) ~63-127; DXVK views 55-118; images `nvoglv32` 26 + `nvgpucomp32` 85 + `d3d9.dll` 7.5 | driver +128 once per run (the largest single step); views bounded (swing, no trend) | the second 128 MB reserve may not come in short sessions | D3D9 COM interfaces only; almost all calls go through the wrapper block `0x60E000-0x61B000` [verified docs/37]; `Lock` pointers live until `Unlock` (D3D9 contract) | **movable via API** (whole renderer process); per-call volume is the risk |
| 3 | **EA heap cores (all users)** (`0x4E4E87`, new core `0x4E4E30`) | 240 -> 272 (CC 272, BB 224; top rows only) | +32-96 per run; cores released only at heap shutdown [verified docs/201 §12.2] | partly (see 3a-3c) | split below | split |
| 3a | ... from **resource reads**: `PackedStream/Raw` `0x72D385` 48-64, `PackedStream/DecompressedRecordData` `0x72D62B` 64-96, `EAStreamMemory/data` `0x4DEC17` 0-48 | 112-192 | all PackedStream cores exist at the first sample (world load) and never grow; `EAStreamMemory` cores +16-32 in play; heap churn at `0x4DEC17` 10.5 GB alloc / 10.5 GB free in 530 s travel (~20 MB/s), 20.1 GB in the CC job; live 1-20 MB [measured] | no: load-time peak (concurrent async reads at world load) | `OpenRecord 0x4A94C0` -> read + decompress `0x4A8380` -> `0x4A7360`, by resource key; the factory parses the stream, the buffers are freed [verified docs/08 §0, docs/39]; some streams stay with their resource (the 1-20 MB live) | **movable via API** (transient buffers); the kept streams stay |
| 3b | ... from **animation clips** `Animation/ClipResource` `0x57A593` | 48-112 | +16-48 per run; clip heap churn 135 MB alloc / 132 freed (travel) | yes: more Sims animating = more | animation players hold clip pointers while playing [inferred docs/22]; `AnimationCache` capped at 15 MB already [verified docs/08] | **not movable** (cap exists) |
| 3c | ... from **unnamed sites**: `0x5C32E2` (caller in the lot imposter block) 48-80, EASTL allocator `0x626C9A` 32 | 80-112 | grows with lots shown / Buy cycles [measured BB] | unknown | unknown | **unknown** (gap G3) |
| 4 | **Untracked private** (no allocation record at the base, `usum`/`untr` rows) | 271-283 in 73-84 allocations | 16 MB blocks cut the largest free block at saves (`save-start traveling` #2, #17) and Build cycles | yes: saves and Build mode | not seen by the 32-bit ntdll stubs [measured docs/201 §14]: the kernel graphics driver (`dxgkrnl`) creates them for the Vulkan drivers' `dxcore` calls [measured §5.1] | **renderer** (joins row 2); on hybrid laptops the second GPU's driver part is avoidable (§5.1) |
| 5 | **Mono native heap**: `Scripting/Simulator` sub-heap cores (`0xD71558`, direct blocks 73 -> 98) with the Mono runtime `g_malloc` (`0xD70A22`, 77 -> 97 inside them) | ~100 (BB ~106) | +12-25 per run, +13 on the first world switch [measured] | no | metadata, classes, vtables, transformed method code, all pointer-linked; interpreter frames point into them [verified docs/58, 119, 130] | **not movable** |
| 6 | **Thread stacks** | ~99-100 reserved (`stkRes`), ~10 committed | flat | no | OS | **not movable** (cap: docs/66 §6.3) |
| 7 | **Reservations with no game frame** (`virtual - < -`) | 42-125 | flat after load | unknown | the NVIDIA Vulkan driver and Apex's `d3d9` heap segments have their own rows (§5.1); the true `at=- from=-` row is 48 MB [measured §5.1] | **renderer** for the named part, rest unknown (gap G2) |
| 8 | **S3SS mimalloc arena** (`Sims3SettingsSetter.asi+0xDE986`) | 64 | flat | no | every MSVCR80 `malloc` user; raw pointers | **not movable** (reserve size: docs/66 #3) |
| 9 | **Pagefile-backed view at save** | 86.8 (travel save) | appears at `save-start`, first seen #2 | yes: saves only | unknown section owner (Apex not loaded) | **unknown** (gap G1) |
| 10 | **String tables (STBL)** | ~20 with S3PP's bucket fix (~90 stock, up to ~290 big setups [community]) | flat after load | grows with installed packs | `LocStrSvc_GetString_A 0x59C5B0`: linear walk, then a tagged heap copy and a new `MonoString`: **copies out** [verified docs/54] | **movable via API** |
| 11 | **Routing clone P5/P6** (ours, when routesnap is on) | -112 at load, -204 after 10-16 min of largest free [measured docs/66 §7] | grows in play | n/a (our switch) | job protocol + record stream (docs/194) | **movable via API** (designed) |
| 12 | **CAS geometry** `CAS/GeometryResource 0x5C7906` + `BlendRegionData 0x5A2FFF` | 26-35 + 15 heap (inside cores) | ~5 MB per live CC cycle, does not settle [inferred leak-or-cache, docs/201 §13.3] | yes: CC cycles | parts in use by built Sims; pointers [inferred docs/42] | **not movable** (fix the growth instead) |
| 13 | **Audio** `Audio/ResourceBinary` `0x8B452D` / `0x8B6022` | 7-15 | churn, bounded | yes: more sounds | sample data by key; voices hold pointers while playing [inferred docs/35, 190 #33] | **movable as rebuildable cache** (idle part only) |
| 14 | **Package indexes** (EA ~15 + CC ~3.5) | ~18.5 | flat | grows with CC count | `DBPF_IndexCompressed` find `0x4AA1B0` by key, copies out a record [verified docs/190 #2] | **movable via API** (small) |

Not ranked: ResourceCache `rc` (173 -> 390 in the travel job): under DXVK a texture's pixel bytes live in DXVK's mapped pool, so `rc` is mostly not 32-bit address space (docs/66 §3.3 caveat); its in-use resource objects sit in row 3, its cold part is ~10-20 MB (docs/08 Phase 0) and a helper L2 for it was already rejected there.

## 3. Movable owners: boundary, cost, helper, Apex

| Owner | Boundary (narrow surface) | Crossings/s | ms/s at 1-5 us | ms/s at 10-30 us | Helper bits | Address space saved | Apex overlap | Effort |
|---|---|---|---|---|---|---|---|---|
| **Renderer stack** (#2) | proxy `d3d9.dll`: every `IDirect3DDevice9`/resource method, encoded one-way into a command ring; round trips only for `Lock` of readable data, `GetRenderTargetData` (CAS compositor `0x5FBCE0`, lights `0x6B4B10`, docs/37), queries, `Present` pacing | one-way 0.3-2.4 M/s [inferred: 1-3k draws/frame x 5-15 state calls x 30-60 fps]; round trips 100-300/s [inferred] | round trips 0.1-1.5; one-way encode ~6-120 at 20-50 ns each [inferred], which replaces DXVK's own front-end work on the same thread | round trips 1-9 | **64** (DXVK x64 + 64-bit driver, no game code) | ~430-640 MB minus a staging ring for locks (~32-64 MB in game) = **~370-600 MB** | **medium:** Apex hooks renderer code (Night Lights, `FastCasSort` compositor readback at `0x5FBCE0`/`0x5FDBF0`, draw dispatch `0x6EE5E0`, docs/187); none of it calls D3D9 behind the device, but their readback timing changes under a proxy | **~60-120 h + 15-25 game runs** [inferred]; gate first (§4.2) |
| **Resource read buffers** (#3a) | hook the read + decompress path (`0x4A8380`; `OpenRecord 0x4A94C0` callers stay): send (package id, offset, sizes) or the resource key; helper reads the file, RefPack-decompresses into a **reused shared window** mapped in the game; the factory's stream reads the window; streams a resource keeps get a heap copy at hand-off | ~300-800/s in play [inferred from ~16-20 MB/s churn at an assumed 25-55 KB mean]; thousands/s during a world load (loading screen, not in the hitch budget) | 0.3-4 | 3-24 (+ ~2 ms/s copy for kept streams) | **64** (DBPF read + RefPack from the format spec; no game code) | the 112-192 MB of load-peak cores, minus the window (16-32 MB) = **~80-170 MB** [inferred: assumes the freed core space would otherwise be refilled; measure first, §4.1] | **high:** Apex `ResourceLookupCache` hooks `DpfWriteDirect 0x4A7FC0`, `ResFindProvider 0x4AFFC0`, reads `PackedStreamVtable 0xFFD790`; `FastMemory` hooks the EA allocator (`0x4E4F90`, `0x4E5306`) | **~12-20 h + 3-5 runs** after the measurement |
| **String tables** (#10) | `LocStrSvc_GetString_A 0x59C5B0` (and its siblings): key -> copied string | 10-1,000/s [inferred: UI, tooltips, notifications] | 0.01-5 | 0.1-30 | **64** (STBL parse from the format, no game code) | ~20 MB with S3PP; ~90 stock, up to ~290 on big setups without S3PP | low (S3PP owns the bucket fix; Apex none found) | ~6-10 h + 2 runs |
| **Routing clone** (#11) | docs/194 job protocol + P5 record stream | ~10 jobs/s + record stream | +0.15-0.7 total (docs/194 §6.2) | same | **32** (`routehost` runs TS3W.exe code) | ~100-200 MB of largest free when P5/P6 are on (docs/194 §6.1) | low (`[RouteGate]` destroy path vs Apex ObjectIdMap, docs/187) | ~60-95 h + 11-17 runs (docs/194) |
| **Audio binary cache** (#13, idle part) | sample fetch by key at sound start | 10-50/s [inferred] | 0.01-0.25 | 0.1-1.5 | 64 | < 10 MB | none found | ~8-12 h; **not worth it** |
| **Package indexes** (#14) | index find by key | thousands/s at load, tens-hundreds/s in play [inferred] | <= 1 in play | <= 6 in play | 64 | ~18.5 MB | **high** (Apex `ResourceLookupCache` caches the same lookups) | ~6-10 h; **not worth it alone** (docs/08 §0) |
| **Save writer** (#4/#9, if G1 confirms) | package write calls during a save (`TempDBPF_` `0x4A9150`, compress `0x4EC0A0`, docs/190) | 0 in play; per save only | 0 | 0 (save time +) | 64 if it only writes bytes | up to the 16 MB blocks + 86.8 MB view seen at saves [inferred] | **high:** Apex `MemoryGuard` (Room to Save) reorders the save path (`0x732E90`, `0x737560`) | unknown until G1 |

RAM-only flags: none of the movable rows is RAM-only; each removes reservations from the 32-bit range. The non-movable caps already in other docs (stack size, mimalloc reserve, Boehm initial heap) are also address space, not only RAM.

## 4. Recommended first target: resource read buffers

**Update 2026-11-20 (docs/305):** built in Apex's restart host. Static pass corrected row 3a: the three sites are not on `0x4A8380`; `PackedStream/Raw` (local to `0x72D360`) is the only movable one, `DecompressedRecordData` and `EAStreamMemory/data` are read through raw pointers and stay. Movable gain ~16-36 MB, not 112-192.

Why this one: largest movable owner whose call surface is mapped and narrow (by key, copy-out, then freed), a 64-bit helper with no game code, IPC well inside 70-100 ms/s even at the event model, and failure is local (fall back to the game's own read). The renderer proxy saves more but has two unknowns that can each kill it.

Honest limit: the address-space gain comes from **not growing EA cores for transient buffers**, and an in-process fixed window for the three tags would give most of the same gain without a helper. The helper adds off-thread file I/O and RefPack (no main-thread decompression, no package handles in the game) and keeps a second copy of hot data out of the game. Build the in-process window as the first step of the same design (it is the game-side half anyway), measure, then move the reader out.

### 4.1 The one live measurement that confirms it

One AllocSites job (`bp-travel-allocsites` with 3 trips, plus one world load from the main menu), with three additions to the existing `allocsites:` output:

1. **Peak concurrent live bytes** per site for `0x72D385`, `0x72D62B`, `0x4DEC17` between samples (today only live at the sample and alloc/free totals exist).
2. **EA main heap free bytes inside its cores** (`[0x11CB864]`, sum of bins + top per core) at each mark: if the PackedStream cores' space is mostly free after load, the 112-192 MB is peak slack that the window would remove; if other users have filled it, the saving is smaller.
3. **Calls, bytes, thread and microseconds per second at `0x4A8380`** (read + decompress), split load vs play.

Go when: peak concurrent read-buffer bytes at load >= ~80 MB and play-time calls <= ~1,000/s. Then the expected gain is >= ~60 MB of largest free block at load at <= ~5 ms/s (spin) of IPC.

### 4.2 Renderer track: two checks before any design

- **Call volume:** DXVK's HUD (`DXVK_HUD=drawcalls,submissions` in `dxvk.conf` or the environment) in BP, IP and CAS for one minute each gives draws/frame; state calls per draw from a call counter on the D3D wrapper block `0x60E000-0x61B000` (docs/37) in a probe build. Above ~2 M one-way calls/s the main-thread encode cost alone eats the budget.
- **Cross-process present:** a 64-bit test program that renders with Vulkan to a window owned by another (32-bit) process, on this NVIDIA driver. If the surface cannot be created or presents stall, the fallback is a helper-owned borderless window over the game window with input forwarded, a much bigger change.

## 5. Gaps

| # | Gap | Why it matters | Closing step |
|---|---|---|---|
| G1 | 271-283 MB untracked private (16 MB blocks at saves and Build cycles) and the 86.8 MB pagefile view at a save have no owner | could be the save writer (movable) or kernel/WoW64-side allocations (not) | **mostly closed (§5.1, 2026-11-20):** the private blocks are kernel-made Vulkan driver memory (renderer), not the save writer; still open: the 86.8 MB view at a travel save (not reproduced) |
| G2 | 42-125 MB of reservations with no game frame | likely DXVK/driver workers; would join row 2 | **mostly closed (§5.1):** NVIDIA driver and Apex `d3d9` reserves are named; the remaining `at=- from=-` row is 48 MB, with candidates only [inferred] |
| G3 | EA cores from `0x5C32E2` (48-80 MB) and EASTL callers (`0x626C9A`, `0x663802`) | the lot imposter block (`0xAE00C5`) might be a rebuildable cache | one `from=` depth of 2 in AllocSites, or a static read of `0x5C32E2`'s callers |
| G4 | crossing rates are inferred for every row except routing | the ms/s columns are ranges, not measurements | §4.1 item 3; DXVK HUD; a hit counter on `0x59C5B0` |
| G5 | owner sums use the top ~40 live rows per sample | every row is a lower bound | AllocSites sum by `at`/`from` over the whole site table at a mark |
| G6 | stress runs only | clip cores, CAS geometry, the second driver reserve may be smaller in normal play | the same sums from one natural 30-min BP soak with AllocSites |
| G7 | whether the D3D9 wrapper ever keeps a `Lock` pointer past `Unlock` (persistent dynamic buffers) | breaks the renderer proxy's API rule | static read of the VB/IB lock users in `0x616B00-0x617600` / `0x61A010-0x61A400` (docs/37) |

### 5.1 G1 / G2 owners from kernel stacks (2026-11-20)

**Setup.** One machine: a hybrid laptop, RTX 4070 Laptop GPU plus an AMD Radeon 610M iGPU, with the display on the iGPU. The game-folder `d3d9.dll` (Apex Radiance) renders in process through `vulkan-1.dll` on the NVIDIA GPU in these runs [measured: `d3d9 > vulkan-1 > nvoglv32` stacks]. Also loaded: S3SS, ts3native 0.36.x, a big CC set and an old save. Each job runs 30 min with 3 saves and 2 Build cycles (harness repro: 45 s lead-in, then 3 × [240 s play, `save_game`, `mem_mark` before/after], Build mode on/off after saves 1 and 2).

**Method.**
- A WPR kernel trace records `VirtualAlloc` / `VirtualFree` and map / unmap view events with stacks, plus image loads (`tools/etw/untr.wprp`).
- `tools/etw/etlread` (TraceEvent) decodes it into one line per event with module+offset frames.
- `untr_match.py` matches the AllocSites `untr` rows to their events by base and size. Bases are reused, so size must match too.
- Two runs:
  - **untr1** has AllocSites on, which provides the `untr` rows.
  - **untr2** has AllocSites off. It is needed because the ntdll hook stub has no frame pointer, so with AllocSites on the kernel's 32-bit walk stops at `ts3native+0x2D154`.
- The process exit frees everything, so "live" means live 10 s before exit.
- Both runs passed. Stacks below are innermost first, with the OS layers left out.

**G1: untracked private.** At untr1's last sample (`usum private=78/330 MB`), 224 MB of the live untracked blocks were made by `dxgkrnl` for `dxcore` callers [measured]. AllocSites cannot see these: the kernel creates them for the user-mode graphics driver.

| Owner | MB | Stack (untr2) | Notes |
|---|---|---|---|
| NVIDIA Vulkan driver (the render GPU) | 96 live in 16 MB chunks; 29 commits = 452 MB and 9 reserves = 144 MB made after load (30 min) | `d3d9+0x1AD620` (also `+0x214BA2`) > `nvoglv32+0x8C48F7` > `dxcore+0x2513C` | These are the "16 MB blocks at saves and Build cycles": one chunk arrives at save 2 [measured]. Vulkan memory the renderer asks for, so it moves with row 2 [inferred]. |
| AMD Vulkan driver (the iGPU; the game never renders on it) | 96 (3 × 32 MB at ~7.8 s) + 2 × 4; plus the 85.1 MB `amdvlk32.dll` image all session | `amdvlk32+0x3CD0C4` > `dxcore+0x2513C` | The loader loads every Vulkan driver at `vkCreateInstance` (`sims3settingssetter+0x30EFC > d3d9+0x199027 > vulkan-1`). The AMD one is also an implicit layer (`VK_LAYER_AMD_switchable_graphics` in `amdvlk32.dll`). |
| EA's startup DxDiag probe | 32 live (write-combined), left after the AMD D3D12 / D3D9 user-mode driver unloads | untr1: `ddraw < amdxn32`; untr2: `d3d12core+0x858E6 < amdxc32+0x2466B9` | `TS3W 0x0061270A` calls `dxdiagn` from 8.0 to 12.0 s after start. It creates devices on every adapter: `amdxc32.dll` (69.8 MB) is loaded 9 times, plus `nvwgf2um`, `amdxx32` and the system `d3d9` twice [measured]. About 4 s of startup [measured span]. Whether the game needs this data at every launch: unknown. |
| ts3native (ours) | 13.5 + 13.4 committed at startup | `ts3native+0x1450B` = `castrim.cpp` `CtAlloc` (0xD80000); `ts3native+0x6798D` = `monojit.cpp` `JitAlloc` (`data + g_jitCodeBytes`, 0xD63000 here) | [verified: frame offset -> disassembly of that build -> source]. Both are on in the release ini (`[CasTrim] Mode=3`, `[MonoJit] Enabled=1`). They count as untracked because they are made before AllocSites' tables exist [inferred]. |
| user32 / win32k section views | 15.63 at save 1 (`gdi32full+0x8070C < gdi32+0x433C`); 20 at load (`gdi32full+0x46C38 < user32+0x322AC`) | | 15.63 MB = 2560 × 1600 × 4, a screen-size bitmap [inferred from the size]. |
| NVIDIA shader compiler image | 88.8 (`nvgpucomp32.dll`, mapped by `nvoglv32` at 7.7 s) | `nvoglv32+0xD5176D` | Counts as image, not private; listed because it shows up as a map event. |

**G2: reservations.** In untr1's `save-end` live rows, most of the reservations without a game frame have a named first frame. The owners come from untr2 [measured].

| AllocSites row (`k=virtual`) | live MB (n) | Owner |
|---|---|---|
| `at=nvoglv32.dll+0x8DFD8E` | 256.1 (3) | NVIDIA driver reserve. In untr2: 128 MB at device creation (`vulkan-1+0x6C365 < d3d9+0x199027`) and 128 MB at 129 s (`d3d9+0x1786B0`). |
| `at=ucrtbase.dll+0x2DF5F from=d3d9.dll+0x58B80C` | 79.1 (5), 3600 MB allocated / 3521 MB freed | Heap segments of Apex's `d3d9`, 15.8 MB each, on 6 threads. |
| `at=d3d9.dll+0x237EF3` | 47.4 (3) | Apex `d3d9` |
| `at=- from=-` (the actual "no game frame" row) | 48.2 (11), 776 MB allocated / 728 MB freed | Not matched block by block. Live user-mode blocks in untr2 with no game frame add up to about this: `coremessaging < xinput1_4` 8 MB, `d3d9` 13 × 1 MB, `frida-agent` 13 MB (harness only), `amdvlk32+0x265322B` 2 × 4 MB, about 6 MB with no user frame [inferred]. |

**Hybrid-GPU A/B (address space, MB).** A 32-bit test program (`tools/etw/vkva.c`) makes the same Vulkan calls as Apex's `d3d9`: instance, enumerate, one device on the NVIDIA GPU. It prints the used address space after each step. It ran at Medium integrity [measured]:

| Environment | after `vkCreateInstance` | after the NVIDIA device |
|---|---|---|
| none | 536.2 | 544.0 |
| `VK_LOADER_DRIVERS_DISABLE=*amd*` (or `VK_LOADER_DRIVERS_SELECT=*nv*`, or `VK_DRIVER_FILES=<nv-vk32.json>`) | 460.4 | 500.2: `amdvlk32.dll` still loads, as the implicit layer |
| `DISABLE_LAYER_AMD_SWITCHABLE_GRAPHICS_1=1` only | 536.2 | 544.0 |
| both | 372.0 | **411.8**: `amdvlk32.dll` not loaded, **132 MB less** |

Caveats:
- **Elevated processes ignore these loader variables.** From an elevated shell they had no effect; at Medium integrity they worked [measured]. The harness runs the game elevated, so an in-game A/B with the variable set (untr3) still loaded `amdvlk32`. In-game confirmation needs a non-elevated launch.
- **`*amd*` is only right when another GPU renders.** On an AMD-only machine it removes the only GPU. A fix has to disable only the drivers of adapters that will not render, for example the integrated one when a discrete one exists [inferred].
- **A probe instance is not free.** After `vkDestroyInstance`, the test still holds 207-241 MB of private reserves. Picking the GPU through a probe instance would cost that much, so it should come from the DXGI adapter list instead [measured].
- **Where it could go:** the Apex launcher's environment for TS3W, or ts3native at asi load. ts3native would call `SetEnvironmentVariable` before `d3d9` creates the instance: the asi loads at ~5.8 s and `vulkan-1` at ~5.9 s [measured times; not built].

**Not reproduced:** the 86.8 MB pagefile view at a travel save. These runs had no travel.

**Tools** (`tools/etw/`; needs an elevated shell for WPR):
1. Record: `wpr -start tools\etw\untr.wprp!Untr -filemode`, run the job, then `wpr -stop out.etl` (~0.6 GB for 30 min).
2. Decode: `dotnet run --project tools/etw/etlread -- out.etl TS3W 1048576 > ev.txt`. Add ` mods` at the end for the image list.
3. Analyse:
   - `python tools/etw/untr_match.py ev.txt ts3native.log [cut ms]` matches the `untr` rows and sums live blocks by owner.
   - `untr_g2.py ev.txt <cut ms> [MB]` lists live blocks with no TS3W frame.
   - `untr_groups.py ev.txt <after ms> [MB]` groups big blocks by caller.
4. Hybrid A/B: build `vkva.c` with `cl /O1 vkva.c /link /LARGEADDRESSAWARE` from an x86 VS prompt, and run it non-elevated.
