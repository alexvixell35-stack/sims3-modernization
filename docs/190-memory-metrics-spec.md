# 190. Memory metrics spec (movable caches)

Goal (Rodney, 2026-10-25): player logs should show the real RAM and address-space cost of every cache we could move out of the process (64-bit helper plus shared-memory windows, docs/08 §5) or cap, so we can pick the first offload from data instead of guesses. Rule: only **movable** data gets sub-categories. Unmovable buckets (GPU-side and DXVK textures, the Mono managed heap, thread stacks, allocator slack, module images, live engine object graphs) get one total line each.

Static research and spec only: no game run, no code. Sources: docs/08, 15, 34, 35, 38, 39, 40, 42, 44, 45, 47, 48, 50, 52, 53, 54, 57, 58, 66, 119, 130, 187, 188, plus Ghidra headless decompiles for this doc (§8). Steam TS3W.exe 1.67, image base `0x400000`. Tags as in AGENTS.md: `[verified]` = disassembly, decompile or measurement; `[inferred]`; `[community]`.

## 0. Summary

- **37 allocation sites or cache owners** found in the engine, the runtime and our own code (§2). **14 are movable or cappable**, 23 are unmovable.
- **Expected size order of the movable ones** (Rodney's setup, BP; most of these are not measured yet): string-table bucket layouts (~90 MB stock with the store set, up to ~290 MB on big setups [community]; S3PP shrinks it) > routesnap clone stack when P5/P6 are on (~50 MB committed at load, ~200 MB of largest free block after 10-16 min, docs/66 §7) > CAS part cache (unknown, built for every CASP) > CAS/world compositor MRU (unknown) > package indexes (~18.5 MB, 17.6 B per resource) > shader bytecode kept (~12.5 MB) > ResourceCache cold part (~10-20 MB) > catalog (~8 MB fixed + per-product heap) > AnimationCache (3-10 MB, capped at 15) > AsyncLoader reads in flight (cap 20 MB) > transient read/decompress and save buffers (peaks of several MB) > audio binary cache, preloaded resources (unknown).
- **The biggest buckets are unmovable:** ResourceCache in-use resources (~390 MB, live objects hold them, docs/08 Phase 0), the Boehm GC heap (352-360 MB), thread stacks (~140 MB reserved), module images (~426 MB), DXVK mapped texture window (<= 100 MB + other mappings), and ~1 GB of EA-heap engine objects and driver memory with no owner yet (docs/66 §3.3).
- **A key finding for measurement** [verified, §8]: in this release build `EA_AllocTagged 0x4E3F90` drops the allocation tag string and passes only size and flags to `Malloc 0x4E69D0`, and EA's own memory tracker `0x9336F0` is reachable from one hash-table allocation wrapper `0x624590`, only in tracking mode `[0x11DC18C] != 0`. So the engine keeps **no per-owner byte counts**. Per-owner numbers must come from (a) the few owner counters that exist (ResourceCache, AnimationCache, SpeedTree, GC, ours), (b) structure walks, or (c) our own caller-sampled allocation profile (§5.3, opt-in).
- **Logging build:** phase 1 (reads and walks only, no hooks) ~2-4 h + one verify run; phase 2 (access counters with hit/miss and distinct-key sketches) ~3-5 h; phase 3 (opt-in caller-sampled EA-heap profile) ~6-10 h + 2 runs; RE first for 6 items (§7) ~4-8 h static.

## 1. Movable or not: the test

An item is **MOVABLE** when one of these holds:

1. **Narrow key -> bytes access with copy-out:** the game asks by key and copies the answer into its own buffer or into a new object (string lookups, resource reads into a caller buffer). A helper process can hold the data and fill a shared window per request (docs/08 §5 transport).
2. **Decode on access:** the game keeps decoded data only to save a re-decode; the source bytes can be re-read (window) or re-decoded on a miss.
3. **Cappable with eviction:** a budget or an eviction path exists or can be added without changing what the game sees (only time).

**UNMOVABLE**: a live object graph where other code keeps raw pointers into the data (scene, objects, routing world, CAS parts in use), GPU or driver memory, OS memory (images, stacks, file mappings owned by DXVK), the managed heap (Boehm scans it in place, docs/57), allocator metadata and slack.

**Pointer-lifetime verdict** (column in §3): `copies out` = callers keep no pointer into the cache after the call, so a window that is reused per request works; `keeps pointers` = callers hold pointers, so only a cap or a compaction (or a copy-out shim in front of every reader) works.

## 2. Inventory (37 sites)

Abbreviations: RC = ResourceCache, EA heap = EA GeneralAllocator (dlmalloc 2.7 derivative, docs/15), MemoryMan heap object `[0x11CB864]`.

| # | Item | Owner code / evidence | Allocator | Size (known) | Verdict | Reason |
|---|---|---|---|---|---|---|
| 1 | Package objects (DBPF) | `DBPF_PackedFile_ctor 0x4A8F70`, 920-byte object, buffer allocator `+0x10` = `DefaultAllocator 0x931020` [verified docs/39] | EA heap | ~1-2 KB per package, ~0.3 MB for 172 CC [inferred docs/08] | UNMOVABLE (tiny) | live file handles, codec, lock |
| 2 | Package indexes (compressed layout) | `DBPF_IndexCompressed_Load 0x4AD020`, find `0x4AA1B0`, enumerate `0x4AC9C0` [verified docs/08, 39] | EA heap | 16 B/entry + 8 B per size overflow + 16 B per type/group pair = ~17.6 B/resource: EA ~15 MB, CC ~3.5 MB [verified layout, computed] | **MOVABLE** | key -> (offset, sizes); copies out a record; helper key -> database map (docs/39 R1) |
| 3 | Writable package index maps (saves, caches) | `0x4ABD70`, `0x4AC240`, nodes 0x24 B [verified docs/08] | EA heap | small, per open save/cache | UNMOVABLE | written in place during saves |
| 4 | DCCache index (`.sims3pack` DatabaseCache, `dcc.ent`, `missingdeps.idx`) | `0x984B60`, `0x98B5E0`, `0x98BD50`, `0x98E580` [verified strings docs/08] | EA heap | unknown, scales with installed `.sims3pack` | MOVABLE (with #2) | index, key -> location [inferred] |
| 5 | Transient compressed + decoded read buffers (sync) | `OpenRecord 0x4A94C0` -> `0x4A8380` -> `0x4A7360` [verified docs/39] | EA heap (`EA_AllocTagged 0x4E3F90`) | `fileSize + memSize` per read; several MB for big textures/meshes [inferred] | **MOVABLE** (raw half) | helper decodes into the engine's decoded buffer (docs/08 H1); raw buffer and RefPack work leave the process |
| 6 | Async read copies (raw, `MemoryStream`, decoded; `LoadInfo` 0x118 B) | `AsyncLoader_CreateLoad 0x7297E0`, `0x72A150`, `0x727540` [verified allocations; third copy inferred docs/39] | EA heap | up to 3 copies per streamed resource | **MOVABLE** (cap) | same as #5; plus the in-flight cap below |
| 7 | AsyncLoader bytes in flight | `AsyncLoader_PumpIOQueue 0x727D20`: `+0x30` in flight, `+0x34` largest request, `+0x38` `MemoryUsageLimit` (default 20 MB); counters `+0x74` loads, `+0x78` bytes [verified docs/39] | EA heap | <= 20 MB | **CAP** | ini gate `[ResourceSystem] MemoryUsageLimit` |
| 8 | RC in-use resources | `ResourceCache_ctor 0x732910` (vtable `0xFFDD94`), held `+0x94`, budget `+0x8C` (from `GraphicsRules.sgr`, 200 MB), overflow `+0x90`; `_Find 0x7324E0`, `_Add 0x732A00`, `_Touch 0x732B60`, lock `0xFFDD34` [verified] | EA heap (resource objects + decoded buffers) | 341-460 MB held in BP; ~390-410 MB is in use even at budget 0 [measured docs/08 Phase 0] | UNMOVABLE | live objects reference them (meshes, textures, CAS data) |
| 9 | RC cold part (flush list) | `+0x98` bytes unreferenced; dropped when over budget [inferred from `0x732B60`, docs/08] | EA heap | ~10-20 MB, even at budget 0 [measured] | **MOVABLE** (cap) | evictable already; helper L2 would serve misses |
| 10 | AnimationCache | same class, priority 1000, budget forced 15 MB, held `+0x94` [verified] | EA heap | 3-10 MB, median 4 [measured docs/66] | **CAP** | already capped |
| 11 | Preloaded resources | `[Preloader] HoldOntoResources` read at `0x731AE0` (default true; `Sims3.ini` misspells the key, so it stays true) [verified docs/08]; list `ResourceSystem/PreloadedResources` | EA heap | unknown | **CAP** (candidate) | held only to skip reloads [inferred]; RE §7 |
| 12 | Texture objects + DXVK system copies (MANAGED pool) | `TextureResource_Load 0x723270`, `Texture_CacheSizeBytes 0x619DB0`, `Resource_SetCacheSize 0x731CC0` [verified docs/40] | DXVK `MemoryFilePool` (16 MiB file mappings, <= `d3d9.textureMemory` 100 MB mapped) | <= 100 MB mapped window; full copies paged by DXVK [verified DXVK source] | UNMOVABLE (by us) | DXVK owns the mapping; cap = `dxvk.conf` (docs/66 #4) |
| 13 | Render targets, shadow maps, RT pool, imposters, AO, wall texel pool, thumbnails, CAS texture builder | `0x6173E0`, `0x70BEB0`, `0xADDEA0`/`0xADE030`, `0x68A7F0`, `0x689400`, `0xD62200`, `0x5FDA00` [verified docs/40] | D3D9 DEFAULT pool (VRAM; DXVK) | GPU side | UNMOVABLE | GPU |
| 14 | Vertex/index buffers (MANAGED static buffers) | `0x61A1C0`, `0x616C40` [verified docs/40] | DXVK mapping / VRAM | inside #12 window | UNMOVABLE | GPU / DXVK |
| 15 | Shader bytecode + DBUF defaults kept | `ShaderSystem_LoadPackage 0x79B870`, `Shader_ReadBytecode 0x799910` (malloc + read, create now or deferred `+0x11`), `ShaderDB_ReadLinkTable 0x79A930` [verified docs/53] | CRT malloc (mimalloc under S3SS) | ~8.7 MB bytecode + 3.8 MB DBUF for the kept subset [measured file scan; kept subset inferred] | **CAP** (free after create) | D3D9 copies bytecode at `Create*Shader`; kept bytes only needed for deferred creation and reload [inferred] |
| 16 | Material binding cache, global param table | `Renderer/MaterialShaderBindingCache` (`RenderSystem_ReadConfig 0x6E05A0`), param table `[0x11D7530]` [verified docs/53] | EA heap | small [inferred] | UNMOVABLE | per-draw pointers |
| 17 | String tables (STBL) per locale | `LocStrSvc_RebuildTableList 0x59BDE0` (list at service `+0x48`, one node per STBL), lookup `LocStrSvc_GetString_A 0x59C5B0` (linear walk, then a tagged heap copy + new `MonoString`) [verified docs/54] | EA heap | 3,187 English tables, 1.5 MB text; stock bucket layout ~29 KB/table = ~90 MB [community figure applied]; S3PP `StblBucketLayoutTuning` 7,919 -> 509 buckets (docs/17 §9: -71 MB commit) | **MOVABLE** (cap first) | copies out [verified]; the waste is layout, not text |
| 18 | EAText layout buffers, Uniscribe items | `EAText_UniscribeItemizeBreak 0x450C70` [verified docs/54] | EA heap | transient [inferred] | UNMOVABLE (tiny) | per layout |
| 19 | UI glyph cache (atlas, 4 pages 512x512x1 B) | ctor `0x452950`, `AddPage 0x452D30`, page texture `0x6916F0`; UI cache `[0x11CC9A8]` [verified docs/52, 54] | texture | <= 1 MB [verified bound] | UNMOVABLE (tiny) | texture, already bounded |
| 20 | UTFWin window tree, display lists, Renderable2D pool, RenderableList pool | pools `0xFBA244`, `0xFBA474`, display lists `window+0x1F4`, `Renderable2D_Alloc 0x5065B0` [verified docs/47, 52] | EA heap | unknown | UNMOVABLE | live UI graph |
| 21 | CAS part cache | `CASPartCacheService::Init 0x5AEDF0` (every CASP key, fills `CASPartCache.package` via `0x5AE170`), lookup `CASCache_GetCachedPart 0x5A44A0` (scan by age/gender/body flags), `CASCache_FindPart 0x5AEF40`; service object at `[0x11CE42C]` (getter `0x5AE140`) [verified exe bytes]; `[0x11CE404]` in `0x5A44A0` is a one-time init guard bit (`test`/`or` of bit 0, the exe's only 2 references), not the cache (docs/209 #95) [verified exe bytes] | EA heap | ~0.7 MB (7,279 parts, ~100 B each) [contributor-measured, one install, docs/209 #95]; 5,662 EA + 2,016 CC CASPs on this setup [verified count docs/08] | MOVABLE in principle; compaction **not worth it** (<= ~1 MB) | built once, never evicted [inferred docs/42]; readers get part pointers [inferred] |
| 22 | CAS part data / blend geometry resources | `CASPartDataResource_Parse 0x5B2290`, `CAS/CacheMap` block `0x5A2C00-0x5A4C00` [verified docs/42] | EA heap (counted in RC #8) | inside #8 | UNMOVABLE | in use by built Sims |
| 23 | CAS compositor cache (MRU), sim texture cache, renderer compositor | `CAS/CompositorCache` `0x5BB6A0-0x5BC6D0`, `SimService`/`SimTextureCache` `0x5EB9C0-0x5F16F0`, `Renderer/MaterialTextureCompositor` `0x6CBF90-0x6CE300` [verified docs/42]; `[CAS] CompositorCacheSize` 100 MB, `*WorldCompositorCacheSize` 500 MB (on-disk limits [inferred]) | EA heap + disk packages (99 MB, 251 MB files) | in-memory share unknown | **MOVABLE** (cap or window) | composited textures by key; disk-backed already |
| 24 | Outfit resources, Sim builder jobs | `SimOutfitResource_Parse 0x5E9AA0`, `IC_CASUtils_SimBuilderBuildModelImpl 0x5A80A0` [verified docs/42] | EA heap; the first CC outfit build adds a 128 MiB mimalloc arena reserve [measured +128 MB private, +19 commit, PLAN M9] | transient + arena reserve | UNMOVABLE (arena = allocator slack, #35) | live Sim models |
| 25 | Buy/build catalog | singleton `[0x11ED034]`, `Catalog::Init 0xC93000`, OBJD loader `0xC922C0` [verified docs/08] | EA heap | 576 B fixed per OBJD x 14,577 = ~8 MB + per-product presets, key tables, thumb info, strings [unknown] | **MOVABLE** (compact record) | key -> product; catalog UI holds product pointers [inferred] |
| 26 | Compiled-in thumbnails | thumbnail textures `0xD62200` [verified docs/40]; catalog `World/Catalog/ThumbInfo/*` tags [verified strings] | texture + EA heap | unknown | UNMOVABLE (texture) | GPU side; page cost measured by docs/08 M9 step 2 |
| 27 | SpeedTree models + cluster buffers | RT `0x7A0F40-0x7C1D70`, glue `0x70D990-0x718620`; byte counter `g_SpeedTreeAllocBytes [0x11D8628]` (110 plain add/sub sites) [verified docs/34]; cluster VB < 8,000,000 B per batch `0x718290` | EA heap (MemoryMan) | unknown (`vfMemSpeedTreeBytes` reads -1, but `[0x11D8628]` is a live counter) | UNMOVABLE | live render data; one total line, free to read |
| 28 | World: lots, terrain chunks, kd-trees, height fields, world tree list | `WorldManager_CreateLotsFromLayer 0xC65E00` (`World/Lot` 0x4C0 B), `TerrainChunk_BuildHeightFieldShape 0xC83200`, `_BuildKDTreeShape 0xC84090`, tree records 0x50 B `0x95BA60-0x95DC60` [verified docs/38, 34] | EA heap | unknown | UNMOVABLE | live graph, physics shapes |
| 29 | Routing world (game): lot table, lot quadtrees, footprints, world graph R, water grid G | `LotTable_Insert 0x62E680`, `LotRouting_QuadTreeBuild 0x62F8A0`, `WaterGrid_BuildFromWorld` (from `0x835390`) [verified docs/39 §5] | EA heap | unknown | UNMOVABLE | live, `+0x918`-guarded pointers |
| 30 | Routesnap (ours): replay clone heap, private pools, cut images, replay ring | counters `rpKB`/`rpKBMax`, `plKB` (96 MB contiguous reserve), `spKB`/`spKBMax`, ring 65,536 x 24 B [verified code + measured docs/66 §7] | own `HeapCreate` heap, own `VirtualAlloc` region | at load clone 20-22, pools 20-22, images 7, ring 1.5 MB; peak clone 42-46 (113 once) | **CAP** | ours; S1-S4 in docs/66 §7 |
| 31 | Object model: objects + components, ObjectGuid maps, script handle maps | `Object_Allocate 0x9361D0`, `ObjectService_CreateObject 0x93A1E0`, service `0x11DC350` allocator `+0x1040`; handle nodes 0x48 B `0x76A850` [verified docs/45] | service allocator / EA heap | unknown | UNMOVABLE | live graph |
| 32 | Scene graph, scene quadtree, render message buffer | `Scene_BuildQuadtree 0x6E4770`, `RenderMessageBuffer_Post 0x593E60` (0x50 B per message) [verified docs/48] | EA heap | unknown | UNMOVABLE | live render graph |
| 33 | Audio: EA audio heap, binary resource cache, event buffers | `"Audio/Allocator"` own heap `0x89F4E0` (with OOM callback) [verified docs/15]; `Audio/ResourceCacheMap 0x8C0720`, `BinaryResourceCacheSizeMB` string at `0x8B68E0` (reader not traced) [verified strings docs/08] | own EA heap | unknown | **MOVABLE** (cap) for the binary cache; rest UNMOVABLE | sample data by key; voices hold pointers while playing [inferred] |
| 34 | Mono runtime: images (S3SA raw data), metadata caches, vtables, interned strings, GC handle table | `mono_image_open_full 0xE84140` -> EA `0xD6FCB0`, `mono_image_open_from_data` per S3SA (docs/119); caches behind `loader_mutex 0x11F43A8` (docs/58); eglib `g_malloc 0xD70A10` -> EA `0x4E69D0` [verified docs/119] | EA heap (MemoryMan) | unknown; scales with script mods (81 assemblies here, docs/128) | UNMOVABLE | metadata is read through pointers into the image everywhere |
| 35 | Allocator slack: EA heap free chunks, core tails, mimalloc arena reserves, Boehm free blocks | EA heap object 0x518 B: bins `+0x28`, top `+0x440`, core list `+0x448`, direct blocks `+0x488`/`+0x48C` [verified docs/15]; S3SS mimalloc 64/128 MiB arenas [verified source]; S3PP cores 4 MB reserve / 1 MB commit | n/a | 54-55 MB reserved-only blocks seen [measured docs/66 §3.4] | UNMOVABLE | allocator metadata |
| 36 | Boehm GC heap (Mono managed objects, incl. NRaas and every script mod's data) | `GC_heapsize [0x12225A0]`, `GC_large_free_bytes [0x12225B4]`, `GC_gc_no [0x11F2F90]`; arena chunks `[0x11EE508]` [verified docs/66 §6.1, docs/57] | Boehm sections (`MEM_WRITE_WATCH`) | 352-360 MB heap, ~209 MB used at 6 min BP [measured] | UNMOVABLE | scanned in place, pointers everywhere |
| 37 | Thread stacks | 7 thread starts through IAT `0xF95244`/`0xF955D0`; ~140 threads x 1 MB reserve [verified docs/66 §6.3] | OS | ~140 MB reserved, mostly uncommitted | UNMOVABLE | OS |

Not separate rows: module images (OS, `MEM_IMAGE` total already in the `memory at` line), DXVK/NVIDIA driver private memory (no owner hook; remainder), EAWebkit (store UI, own allocations unknown; in the remainder), save write buffers (`TempDBPF_` via `0x4A9150`, compress `0x4EC0A0`; transient during saves, counted as a peak in §3 M11), Mono interpreter frame arena (2 KB chunks, inside #36).

## 3. Movable sub-categories: how to measure

Cost classes: **R** = plain reads of a few globals/fields on the userdiag memory worker (µs); **W** = structure walk (ms, once per sample or per world load, on the stated thread); **H** = hook with a counter (ns per call); **P** = sampled allocation profile (opt-in, §5.3).

| Id | Sub-category (log key) | How to measure cheaply | Size at load / growth per hour / per CC package (expected) | Access metric to log | Pointer lifetime | Offload / cap path | Owner | Cost |
|---|---|---|---|---|---|---|---|---|
| M1 | String-table layout (`stbl`) | **W** at world load on the sim thread: walk the list at LocStrSvc `+0x48`, per node read bucket count and entry count of the STBL object, sum `buckets x bucket size + entries x entry size` (layout RE §7 Q1). Also log whether S3PP layout tuning is active (bucket count 509 vs 7,919) | ~90 MB stock (store set) [inferred], ~20 MB with S3PP [inferred from -71 MB]; flat in play (rebuilt only on locale change); ~29 KB stock per STBL, so +29 KB per CC STBL (per package: STBL count x 29 KB) | **H** on the script-side caller of `0x59C5B0` (S3PP `LocalizedStringCache` detours `0x59C5B0` itself: never hook the same bytes; count at the IC or at `ScriptHost +0x58`): calls/min; distinct keys per 10 min (HyperLogLog, §5.2) | copies out [verified docs/54] | cap first (own layout fix or S3PP); helper only if text itself grows (1.5 MB today: no) | us (S3PP compatible, docs/69) | W ~1-5 ms once per load [inferred]; H ~5 ns/call |
| M2 | Routesnap clone stack (`rs`) | **R**: existing native counters `rpKB`, `rpKBMax`, `plKB`, `spKB`, `spKBMax`, ring constant; plus `HeapSummary` of our replay/image heaps (committed vs reserved, closes docs/66 §7 "heap slack") | 48 MB committed at load, +106 MB reserved (96 MB pool reserve); +10-25 MB per 10-16 min [measured]; scales with clone map entries (~330-375 B each), not CC | rebuilds per hour (`rpKBMax` events), pool high-water | ours | cap: S1 pool reserve 96 -> 48 MB, S2 one build at load + heap per epoch, S3 per-lot clone | us | R |
| M3 | CAS part cache (`casp`) | **W** once after `CASPartCacheService::Init` and at world load: count parts in the cache map and sum per-part size (layout RE §7 Q2). Fallback **P**: bytes allocated from the `0x5A2C00-0x5AEF40` caller range | unknown; built at startup, flat in play [inferred: no removal path]; linear in CASP count (EA 5,662 + CC) | **H** count `0x5A44A0` / `0x5AEF40` calls per min; distinct parts per 10 min | keeps pointers [inferred] | compaction (fixed-size records, strings and presets out) or helper with a copy-out shim at `0x5A44A0` (docs/08 H5, Phase 3) | us | W once; H ~5 ns |
| M4 | Compositor MRU (`comp`) | **W** walk of the CAS/CompositorCache MRU list (RE §7 Q3), entries x texture bytes; **R** the configured limits | unknown; grows with Sims shown, capped by MRU; CC outfits with 2K textures dominate [inferred] | hits/misses per min at the MRU lookup (RE), disk cache reads | copies out to a texture on a miss [inferred] | cap the in-memory MRU; disk files already serve misses | joint (CAS = us, texture side = Apex) | W ~1 ms per sample [inferred] |
| M5 | Package indexes (`idx`) | **zero game cost**: computed in the `[UserDiag]` header worker, which already reads every package index (docs/188 §1): `16 x entries + 8 x size overflows + 16 x type/group pairs`, EA packs from their resource counts the same way | EA ~15 MB + CC ~3.5 MB here; flat; 17.6 B x resources per package | **H** `GetPackageSource 0x4AFFC0` probes per min is Apex's (ResourceLookupCache owns that slot): read their counter if exposed, else skip | copies out (record lookup) [verified docs/08] | helper key -> database map; only worth it above ~100 MB (~5.7 M resources) | joint (Apex owns the lookup cache) | 0 (header worker) |
| M6 | Shader bytecode kept (`shd`) | **W** once at world load: walk ShaderDB link table entries (`0x79A930`), sum bytecode sizes of shaders already created (RE §7 Q4: where the size is kept) | ~12.5 MB; flat; no CC scaling | none needed | copies out (D3D9 copies at create) [inferred] | free bytecode after create when not deferred | Apex (visuals/shaders) | W once |
| M7 | RC cold (`rcCold`) | **R** `+0x98` of the RC; held `+0x94`, budget `+0x8C`, entry count (hash table `+0x20`/`+0x24`, §8) via the manager `[0x11C78D8]` cache array `+0xA0`/`+0xA4`, vtable check `0xFFDD94` (docs/08 `/rescache`) | ~10-20 MB, flat [measured] | **H** RC lookups/hits (`ResourceCache_Find 0x7324E0` return), misses that read a package (`0x4A8380` calls, bytes, max µs) | in use: keeps pointers; cold: evictable | cap (budget via `GraphicsRules.sgr`); helper L2 not worth (~20 MB) [measured docs/08] | us (reads) / Apex (lookup caches) | R; H ~5 ns |
| M8 | Catalog (`cat`) | **R** count at the catalog singleton `[0x11ED034]` x 576 B; per-product heap via **P** (caller range `0xC7E270-0xC93000`) | ~8 MB + per-product heap; flat after init; 576 B per CC OBJD (+ presets) | catalog page opens per hour (UI), not needed for players | keeps pointers [inferred] | compact record (docs/08 H1) | us | R |
| M9 | AnimationCache (`anim`) | **R** AnimationCache `+0x94` (priority 1000 entry of the cache array) | 3-10 MB, capped 15 MB [measured] | none | evictable | already capped | us | R |
| M10 | AsyncLoader in flight (`async`) | **R** AsyncLoader `+0x30` (in flight), `+0x34`, `+0x38` (limit), `+0x74`/`+0x78` (loads, bytes); the instance pointer needs RE (§7 Q5) | 0-20 MB transient; bytes/hour = streaming volume | loads and MB per min (deltas of `+0x74`/`+0x78`) | copies out (handed to the factory) | cap = `MemoryUsageLimit` | us | R |
| M11 | Transient read and save buffers (`xfer`) | **H** at `DBPF ReadDecompress 0x4A8380` (sizes are arguments: `fileSize + memSize`): bytes in flight, max single, count >= 4 MB; during a save, the `Save_WritePackage` span (docs/188 §3) gives the save window | peak only; several MB per big texture/mesh [inferred]; more with 2K CC textures | reads/min, MB/min, max single MB, peak in-flight MB | copies out [verified mechanism docs/39] | helper decode into the engine's buffer (docs/08 H1) removes the raw half; save compress is Apex's (FastCacheCompression slot `0xFB901C`) | joint | H ~10 ns + 2 adds |
| M12 | Audio binary cache (`aud`) | **W** core list walk of the `Audio/Allocator` heap object (`+0x448` cores, `+0x48C` direct bytes; heap pointer RE §7 Q6) = whole audio heap; split of the binary cache needs `Audio/ResourceCacheMap` RE | unknown | none for phase 1 | voices keep pointers while playing [inferred] | cap via `BinaryResourceCacheSizeMB` if the game reads it (reader not traced) | us | W ~0.1 ms |
| M13 | Preloaded resources (`pre`) | **W** walk of `ResourceSystem/PreloadedResources` (RE §7 Q5), sum resource sizes (`+0x20` of each resource, as the RC uses [verified §8]) | unknown; flat | none | referenced (they are kept on purpose) | cap: `HoldOntoResources=0` (correct spelling) A/B | us | W once per load |
| M14 | DCCache index (`dcc`) | with M5: size of `dcc.ent` / `missingdeps.idx` files in the header worker (numbers only) | unknown; scales with `.sims3pack` installs | none | copies out [inferred] | with M5 | us | 0 |

Hot-set estimate for each H metric: distinct keys seen in the 10-minute window (sketch) x mean bytes per entry of that cache = hot bytes. A cache whose hot bytes are far below its size is a good window candidate (the window only needs the hot set); one whose hot set is close to its size gains nothing from a window and needs a cap or compaction instead.

## 4. Unmovable totals (one line each)

| Key | Total | How to measure | Cost |
|---|---|---|---|
| `img` | Module images | existing `memory at` field `modules` (`MEM_IMAGE`) [built 0.36.66] | 0 (same walk) |
| `map` | Mapped files (mostly DXVK texture window) | existing `mapped files` field (`MEM_MAPPED`) | 0 |
| `gc` | Boehm heap / free / collections | **R** `[0x12225A0]`, `[0x12225B4]`, `[0x11F2F90]` [verified docs/66 §6.1] | R |
| `stk` | Thread stacks reserved / committed | per-thread TEB `StackBase - DeallocationStack` (as `[ThreadStack]`), or count of allocation bases whose regions carry `PAGE_GUARD` (in the existing walk) [inferred method] | W on the memory worker, ~140 threads |
| `rc` | RC in-use bytes | **R** RC `+0x94` minus cold `+0x98` | R |
| `st` | SpeedTree bytes | **R** `[0x11D8628]` (plain counter; a torn read is a few KB off) [verified docs/34] | R |
| `ea` | EA heap committed / free-in-bins / direct-mapped, per heap (MemoryMan, Audio, others) | **W** core list `+0x448` (committed per core), `+0x488`/`+0x48C` direct blocks; free = top `+0x440` size + bins (walk `+0x28` bins under the heap CS `+0x4E4`; or skip bins and log only top + cores, which needs no lock) [verified layout docs/15] | W: cores ~µs; bins walk ~0.1-1 ms under the allocator lock: phase 3 only |
| `mi` | mimalloc arenas (S3SS) reserved / committed | allocation bases of 64/128 MiB `MEM_PRIVATE` reserves in the existing walk (group regions by `AllocationBase`) [inferred signature] | 0 (same walk) |
| `ours` | Our own heaps other than routesnap | `GetProcessHeaps` + `HeapSummary` (Windows 8+) for heaps we created; process heap separately | W ~µs per heap |
| `rest` | Unattributed private (driver, DXVK private, EAWebkit, unknown EA owners) | `private - (gc + ea + mi + rs + ours + stk)` | 0 |

## 5. Log line format and builder plan

### 5.1 Lines

One `diag: mem` line right after each existing `memory at` line (world load, save start, save end, every 10 min, exit), numbers only: no names, keys, paths or package names. `-` = not readable in this build (RE missing, Apex owns it, or feature off). MB with one decimal for values under 100, integers above. `mov` holds the movable sub-categories, `fix` the unmovable totals.

```
<!-- docs-code: ok -->
diag: mem #3 save-start mov stbl=88.4/3187/7919 rs=48/106/7/1.5 casp=-/7678 comp=- idx=18.5 shd=12.5 rcCold=14.2 cat=8.4 anim=4.1 async=0.0/20 xfer=6.2 aud=- pre=- dcc=0.3 fix img=426 map=145 gc=360/151/812 stk=140/9 rc=388/200/41k st=11.2 ea=1210/96/22 mi=320/210 ours=6 rest=402
```

Field forms: `stbl=MB/tables/buckets-per-table` (bucket count shows S3PP on or off), `rs=committedMB/reservedMB/imagesMB/ringMB`, `casp=MB/parts`, `async=inflightMB/limitMB`, `gc=heapMB/freeMB/collections`, `stk=reservedMB/committedMB`, `rc=inUseMB/budgetMB/entries`, `ea=committedMB/topFreeMB/directMB`, `mi=reservedMB/committedMB`.

Access line every 10 min of play (not at saves), deltas over the window:

```
<!-- docs-code: ok -->
diag: memaccess 10min rc get=48k hit=97.1 miss=1.4k rdMB=212 rdMaxMB=5.3 inflightMaxMB=11.0 stbl get=31k keys=2.9k casp get=4.1k keys=610 comp hit=- miss=-
```

`keys` = distinct keys in the window (sketch estimate, ±3%). Length: ~300-420 characters per line; one line per sample, so a 3-hour session adds ~20 `mem` + ~18 `memaccess` lines.

### 5.2 Builder plan

| Phase | What | Where | Effort | Game runs |
|---|---|---|---|---|
| 1 | Reads and walks without hooks: `gc`, `rc` (+ cold, entries), `anim`, `st`, `rs` counters + `HeapSummary`, `ea` cores and direct blocks (no bins), `mi` and `stk` from the existing region walk grouped by allocation base, `idx`/`dcc` computed by the header worker, `rest`. Emits `diag: mem` | userdiag memory worker (`[UserDiag]`, docs/188 §6); RC fields read without the lock (aligned dwords); entry count only under `g_ResourceCacheLock 0xFFDD34` for one read, never a table walk off the sim thread | 2-4 h incl. selftest (line format, sums) | 1 verify (`verify-userdiag.json` + `logRequire` on `diag: mem`) |
| 2 | Access counters: H on `ReadDecompress 0x4A8380` (count, bytes, max, in flight), RC lookups at `0x7324E0` (or its callers, if Apex's ResourceLookupCache shares bytes; check `node tools/hook-sites.ts --check`), `CASCache_GetCachedPart 0x5A44A0`, the script-side string lookup caller; one 1 KB HyperLogLog per category (key hash: RC/resource key 16 B, STBL key 64-bit, CAS instance); `async` once the instance is found. Emits `diag: memaccess` | new `memdiag.cpp` (counters in a new counter version: reserve in docs/registry.md first) | 3-5 h | 1 verify + 1 BP 10 min to measure the counters' own cost |
| RE | §7 Q1-Q6 (STBL layout, CAS part cache, compositor MRU, shader sizes, AsyncLoader/Preloader instances, audio heap pointer) | Ghidra headless, static | 4-8 h | 0 |
| 3 | Opt-in caller-sampled EA heap profile `[MemProfile] Enabled=0` (§5.3): splits `ea` and `rest` by subsystem, sizes the owners no walk reaches (CAS cache, catalog heap, Mono eglib, world, UI) | detour of EA `Malloc 0x4E69D0` / `Free 0x4E5920` / `Realloc 0x4E6A70` | 6-10 h | 2 (BP soak 30 min + crash-free save matrix sample) |

### 5.3 Phase 3: caller-sampled profile (opt-in)

- Sampling by address: on Malloc, keep the block only when `hash(ptr) & 63 == 0`; store `{ptr, bucket, size}` in a fixed 64K-entry open-addressing table (~1 MB, own `VirtualAlloc`). On Free, the same hash test decides the lookup, so 63 of 64 frees cost one multiply and a compare. Live bytes per bucket = sampled bytes x 64 (good above ~1 MB per bucket; error ~1/sqrt(samples)) [inferred statistics].
- Bucket = the first return address outside the allocator wrappers (`0x4E3F90`, `0x931020` vtable slots, eglib `0xD70A10`/`0xD70A30`, EASTL helpers) mapped to an owner range table (resource `0x4A0000-0x4B3000` + `0x727000-0x739000`, CAS `0x5A2C00-0x5F1700`, renderer `0x5F0000-0x730000`, SpeedTree, audio `0x860000-0x8C5000`, Mono `0xD70000-0xEC0000`, world `0xC20000-0xCA0000`, objects `0x930000-0x93B000`, UI `0x500000-0x520000`, ...; ranges from docs/32 and the docs above, finalised in the build). `RtlCaptureStackBackTrace` depth 4 only for the sampled 1/64.
- Cost [inferred]: the allocator runs ~6.9% of sim-thread samples (docs/15); one hash test per call is ~1-3 ns, the sampled path ~0.5-1 µs, so ~1-3 ms/s summed over threads. Opt-in, for heavy-CC bug reports and our own soaks, never default.
- Conflicts: Apex FastMemory changes timing around `0x4E5306`, `0x4E4E81`/`0x4E504C` and the trim `0x4E4F90`, and our own `[Alloc]` uses the same four entries (docs/187 row 12, not in ApexStandDown). Rule: `[MemProfile]` stands down when another module already patched the entry bytes (same check as `[ThreadStack]`), and never runs with `[Alloc] Enabled=1`.

### 5.4 Risks

- **Torn or stale reads**: phase 1 reads dwords the sim thread writes; sums can be a few KB off; no pointer chasing off the sim thread except the RC cache array (stable after init [inferred]). Walks of mutable lists (STBL, CAS cache, catalog) run on the sim thread at world load only.
- **Lock order**: taking `g_ResourceCacheLock` from the memory worker is safe only for a single read with nothing else held; never take an EA heap CS from our worker in phase 1-2.
- **Hook overlap**: S3PP detours `0x59C5B0`, Apex owns `0x4AFFC0`/`0x4B2D00`/`0x4B1AE0` families and the compression slot; S3SS replaces the RefPack core `0x4EB3B0` (we hook above it at `0x4A8380`, docs/08 H1). Run `tools/hook-sites.ts --check` before phase 2.
- **Privacy**: counts and sizes only; key hashes stay in memory inside the sketch and are never written.
- **Cost creep**: the 10-min cadence and the worker thread keep phase 1-2 off the sim thread except the world-load walks (~ms once per load).

## 6. Decision table: what player data picks the first offload

Collect phase 1-2 lines from at least 10 player logs with heavy setups (>= 2 GB in `Mods\Packages` per the header, or a session low under 600 MB largest free), plus our own BP/IP/SV soaks. Regress each `mov` value against the header's package count, CASP/STBL counts and play time.

| If the logs show | Then build first | Why |
|---|---|---|
| `stbl` > 60 MB and bucket count 7,919 (S3PP layout off) | own STBL layout cap (or recommend S3PP `StblBucketLayoutTuning`) | biggest known movable, copy-out access, zero helper |
| `rs` drop in largest free block > 100 MB with P5/P6 on and session low < 600 MB | routesnap S1 + S2 (docs/66 §7) | ours, low risk, ~70-90 MB |
| `casp` > 40 MB and its slope per 1,000 CC CASPs > 5 MB | CAS part cache compaction, then helper with copy-out shim | grows with CC, built once, never evicted |
| `comp` > 50 MB and `memaccess` compositor misses low | cap the in-memory compositor MRU | misses already served from disk |
| `rest` > 600 MB or `ea` growing > 100 MB/hour | phase 3 profile on that setup before any offload | unknown owner; sizing first |
| `xfer` peak > 32 MB or largest free at save start < 300 MB with high `rdMaxMB` | docs/08 H1 read helper (raw buffer out of process); save buffers with Apex | transient peaks hit fragmented space first |
| `idx` > 100 MB | key -> database helper with Apex | only then worth the transport |
| `gc` free > 100 MB at every sample | `InitialHeapMB` down (docs/66 #1) | not an offload, but the cheapest MB |
| everything movable < 100 MB summed and Error 12 still reported | caps are not the answer: unmovable owners (stacks, mimalloc arenas, DXVK window) | docs/66 #2-#4 |

## 7. Open RE questions (needed before the item can be measured)

| Q | Question | Blocks | Static effort |
|---|---|---|---|
| Q1 | STBL resource object layout: bucket array, bucket and entry sizes (and S3PP's rewritten layout) | M1 | ~1 h |
| Q2 | **Closed** (docs/209 #95): container = `CASPartCacheService` at `[0x11CE42C]`, built in `0x5AEDF0`; `[0x11CE404]` is an init guard bit [verified exe bytes]; ~0.7 MB for 7,279 parts, catalog products ~12.8 MB, STBL ~9.4 MB with S3PP's bucket layout [contributor-measured]; CAS/catalog/STBL compaction not worth it (<= ~6 MB) | M3 | done |
| Q3 | CAS/CompositorCache MRU (`0x5BB6A0`-`0x5BC6D0`): list head, entry layout, byte counter if any; lookup site for hit/miss | M4 | ~1-2 h |
| Q4 | Where ShaderDB keeps bytecode size per shader after create | M6 | ~0.5 h |
| Q5 | AsyncLoader and Preloader instance pointers (`0x7274D0` init, `0x731AE0`); PreloadedResources list layout | M10, M13 | ~1 h |
| Q6 | Every GeneralAllocator heap object (callers of the heap init, `Audio/Allocator` `0x89F4E0`), so `ea` is per heap; who reads `BinaryResourceCacheSizeMB` | M12, `ea` | ~1 h |
| Q7 | Whether `mono_image_open_from_data` at `0xD6FCB0` copies the S3SA bytes (need_copy) and frees the resource buffer | sizing Mono images inside `rest` | ~0.5 h |
| Q8 | RC hash node layout (bucket array `+0x20`, count `+0x24`, node `+0x10` = resource [verified §8]; next-pointer offset) for an optional per-type split on the sim thread | optional rc split | ~0.5 h |

## 8. Verification done for this doc (Ghidra headless, 2026-10-25)

- `EA_AllocTagged 0x4E3F90` forwards size and flags to `Malloc 0x4E69D0` and does not keep or pass the tag string [verified decompile]. `0x4E69D0` = enter heap CS `+0x4E4`, core `0x4E5E40`, leave [verified, matches docs/15].
- The EA tracker `0x9336F0` (takes lock `0x1042634`, behaviour by mode `[0x11DC18C]` 1 or 2) is called from `0x624590` only when the mode is non-zero; the mode is written only by `IC_GameUtils_SetMemoryTrackingMode 0xEC1520` (values 0-2) [verified decompile; docs/120 found the same]. Turning it on is a debug path with a lock on every tracked allocation: not a player metric.
- `ResourceCache_Add 0x732A00` / `_Find 0x7324E0`: hash table with bucket array `+0x20` and count `+0x24`, node `+0x10` = resource, resource key at `+0x08`, resource size at `+0x20`, held bytes `+0x94` reduced by that size [verified decompile].
- `[0x11CE404]` is a one-time init guard bit in `0x5A44A0` (`test byte` bit 0 + `jnz` at `0x5A4A89`, `or` bit 0 at `0x5A4A92`; the exe's only 2 references), not the cache container; the cache is the service at `[0x11CE42C]` (getter `0x5AE140`) [verified exe bytes, docs/209 #95; earlier "container, inferred" refuted].

## 9. Related

docs/66 (address-space budget, §7 routesnap), docs/188 (`[UserDiag]` lines this extends), docs/08 (helper transport and hook points H1-H5), docs/187 (Apex ownership: engine, visuals, lookups, allocator), docs/69 (S3PP features), docs/registry.md (counter versions for phase 2).
