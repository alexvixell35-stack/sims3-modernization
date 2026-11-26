# Resource system map (M-CORE3)

Static map of how TS3W.exe 1.67 (Steam) finds, reads, decodes and caches resources from `.package` / `.dbc` files, which thread does each step, which locks it holds and what it costs on the sim thread. It builds on [docs/08](08-resource-offload-design.md) (manager, index layout, caches, Resource.cfg, AsyncLoader settings, Phase 0 cache results); this doc adds the lock map, the asynchronous path with its threads, the factory registry, the memory owners, the measured sim-thread cost split and a ranked candidate list.

Method: Ghidra project copy `game-copy/ghidra-core3` with `tools/ghidra/symbols/ts3w-1.67.symbols.csv` imported; `XrefsTo.java` (new, references to globals/vtables), `DumpPtrs.java` (vtables), `RangeMap.java`, `StringXrefs.java` (allocator tags), `DecompileTo.java`; cost from the existing 16 `probe-out/profile/*simstack*.jsonl` files (10,251 sim-thread samples from BP, IP and SV live runs). No game runs. Names below are ours unless they are EA allocator tags.

## 0. Findings first

1. **Four locks, not one** [verified]. `g_ResourceLock 0xFB2CD0` (manager), `g_PackedFileLock 0xFB24F8` (every DBPF package, one global lock for all of them), `g_ResourceCacheLock 0xFFDD34` (both resource caches) and `g_AsyncLoaderLock 0xFFD2DC`. All are the EA recursive lock (`Lock_Acquire 0x4E16F0`, critical section). The 23 references to `0xFB2CD0` sit in 19 functions, all of them methods of the manager vtable `0xFB2DA0` (§3).
2. **A synchronous load holds two global locks for the whole read + RefPack decode** [verified]: `GetResource 0x4B0480` holds `g_ResourceLock` across lookup, open, read, decode and factory; inside it, `DBPF_PackedFile_OpenRecord 0x4A94C0` holds `g_PackedFileLock` across `ReadFile` and the decompression. So no two synchronous loads ever run at the same time, on any thread, and they also exclude the package code of the async path while they read.
3. **There is a real asynchronous path, and it decodes off the calling thread** [verified]. `AsyncLoader` (vtable `0xFFD388`) queues reads to the RenderWare file-system manager (`Services/rw/core/filesystem/Manager`, code `0x400F00-0x402600`), which runs its **own I/O thread** (started on first request by `0x401CA0` through the EA thread creator `0x4E2F30` / `_beginthreadex`) and orders its queue by priority, then by file offset. The read bytes are decompressed and turned into an object on the AsyncLoader's job thread (JobManager thread, mask 2, index 5, created in `AsyncLoader_Init 0x7274D0`) with a private RefPack codec, outside `g_ResourceLock` and `g_PackedFileLock`. `MemoryUsageLimit` (default 20 MB) is the cap on **bytes of reads in flight** (`+0x30` vs `+0x38`, gate in `0x727D20`) [verified].
4. **The biggest resource cost on the sim thread is not RefPack but the package probe** [measured]. Per cache miss, `GetPackageSource 0x4AFFC0` asks every registered database in priority order whether it has the key; each ask is a hash plus binary search in that package's index (`DBPF_IndexCompressed_Find 0x4AA1B0`). These two functions take **0.67%** of sim-thread samples (~6.7 ms/s; BP 0.74%, IP 0.58%, SV 0.75%), against **0.23%** for RefPack decode (N15). All resource-system code together is ~1.2% self (~12 ms/s).
5. **Lock contention is negligible in live mode** [measured]: 5 of 10,251 sim samples (0.05%) are in `Lock_Acquire` with resource code on the stack, and the sampler over-weights waits (docs/25). Stutter from this system comes from synchronous misses on the sim thread (39 script ICs load directly, §4), not from waiting on other threads.
6. **`GetKeyList` (manager slot `0x20`, `0x4B1AE0`) walks the database array without any lock** [verified], while `RegisterDatabase 0x4B2D00` changes it under `g_ResourceLock`. A registration on another thread during a key enumeration (CAS part search, catalog, preloader) is a use-after-free risk [inferred: no crash tied to it yet].
7. **Package priority**: the database array is kept sorted by priority, highest first; a new package goes before the first one with a lower priority, so among equal priorities the **first registered wins** [verified, `0x4B2D00`].

## 1. Function list

All addresses Steam 1.67. Evidence: [verified] = decompile or reference scan in this session unless marked otherwise.

### 1.1 Manager (`ResourceSystem/ResourceMan/*`, singleton `[0x11C78D8]`, vtable `0xFB2DA0`)

| Slot | Address | Name | What it does | Lock |
|---|---|---|---|---|
| 0x00 | `0x4B35A0` | dtor | | |
| 0x08 | `0x4B2770` | `ResourceMan_RegisterFactorySet` | Adds a factory set (tag `Manager/factorySet`); called by `0x737170` at resource-system init | yes |
| 0x0C | `0x4B05F0` | `ResourceMan_GetResourceEx` | Same flow as `GetResource` with caller-supplied database and factory; the catalog loader uses it (docs/08) | yes, whole call |
| 0x14 | `0x4B0750` | `ResourceKey_Read` | | |
| 0x18 | `0x4B0810` -> `0x4B0480` | `GetResource` | cache -> database -> factory -> cache | yes, whole call |
| 0x20 | `0x4B1AE0` | `GetKeyList` | Asks every database (slot `0x30`) or every cache for keys matching a filter | **none** |
| 0x28 | `0x4B29D0` | `ResourceMan_RegisterFactory` | Type id array + factory array (`TypeIdArray`, `Mgr/FArray`) | yes (2 refs) |
| 0x2C | `0x4B10E0` | `ResourceMan_FindFactory` | Hash map type -> factory list (`Mgr/TFMap`, `+0x7C`), first factory whose slot `0x30` accepts (type, group) | yes |
| 0x30 | `0x4B1180` | `ResourceMan_GetFactoryList` | | yes |
| 0x34 | `0x4B2D00` | `RegisterDatabase(add, db, priority)` | Sorted insert / remove; calls the database's attach hook (slot `0x48`) with the lock released | yes (3 refs) |
| 0x38 | `0x4AFF70` | `ResourceMan_IsDatabaseRegistered` | | yes |
| 0x3C | `0x4B2EC0` | `ResourceMan_SetDatabasePriority` [inferred name: erase + sorted re-insert] | | yes |
| 0x40 | `0x4AFFC0` | `GetPackageSource` | Linear probe of the database array; releases the lock around each probe (no effect inside `GetResource`, the lock is recursive) | yes |
| 0x44 | `0x4AFDA0` | `FindDatabase` | -> slot `0x40` | via 0x40 |
| 0x48 | `0x4B17E0` | `ResourceMan_GetDatabaseList` | Every database that has a key, probed under the lock | yes |
| 0x4C | `0x4B0960` | `ResourceMan_NotifyDatabaseChanged` [inferred name] | Calls change listeners for keys of one database, lock released around each callback | yes (2 refs) |
| 0x50 | `0x4AFDC0` | `ResourceMan_LockUnlock(bool)` | Takes or releases `g_ResourceLock` for a caller (virtual callers not traced) | yes |
| 0x58 / 0x5C | `0x4B0070` / `0x4B00C0` | `FindCache` / `CacheResource` | Walk the cache array (`+0xA0`/`+0xA4`); the caches lock themselves | cache lock |
| 0x84 / 0x88 / 0x8C | `0x4B18A0` / `0x4B2400` / `0x4B1CD0` | `ResourceMan_SetKeyName` / `GetKeyName` / `RemoveKeyName` [inferred names] | Key -> name map (`+0x11C`) | yes |
| 0x90 / 0x94 / 0x98 / 0x9C | `0x4B0D10` / `0x4B1380` / `0x4B24B0` / `0x4B1D50` | `ResourceMan_GetTypeIdByName` (case-insensitive compare) / `GetTypeName` / `GetTypeNameString` / `RemoveTypeName` [names of the last three inferred] | Type <-> name map (`+0xF4`) | yes |

Key name hashing: `ResourceKey_HashNameFNV64 0x4AEB90` (FNV-1 64-bit over the lower-cased wide string) [verified].

### 1.2 Packages (`DatabasePackedFile`, vtable `0xFB2600`, lock `0xFB24F8`)

| Address | Name | What it does |
|---|---|---|
| `0x4A8F70` | `DBPF_PackedFile_ctor` | 920-byte object; buffer allocator `+0x10` = `DefaultAllocator 0x931020` (EA GeneralAllocator) [verified] |
| `0x4A7AD0` | `DBPF_HeaderValidate` | docs/08 |
| `0x4AD020` / `0x4AAF50` | `DBPF_IndexCompressed_Load` / `_ctor` | docs/08 |
| `0x4AA1B0` | `DBPF_IndexCompressed_Find` | (type + group) mod bucket count, chain walk, then binary search on instance in that (type, group) run [verified] |
| `0x4AC9C0` | (symbols CSV: `CASPartSearch_Leaf`) | Really the index's **key enumeration** (tag `IndexCompressed/Matches`, index vtable slot `0xFB285C`): walks every entry, optional filter callback. Reached through `GetKeyList`, which is why it shows under CAS part search (docs/20) [verified] |
| `0x4A94C0` | `DBPF_PackedFile_OpenRecord` (slot `0x34`) | Takes `g_PackedFileLock`; index lookup; compressed: allocate decoded + raw buffers, read, decode, wrap; small uncompressed: heap copy; large uncompressed or "stream" flag: streaming record (slot `0x74`) |
| `0x4A8380` -> `0x4A7360` | read + decompress | `0x4A7360` allocates a RefPack codec object per call (`CompressionRefpack`, 8 bytes) for compression `0xFFFF`, else asks the package's codec provider (`+0x390`, slot `0x6C`) |
| `0x4A7DE0` | `DBPF_PackedFile_ReadAt` [inferred name] | seek + read under `g_PackedFileLock` |
| `0x4DB4C0` / `0x4DB850` | `FileStream_Open` / `FileStream_Read` | plain `ReadFile`, no `OVERLAPPED` [verified] |

### 1.3 RefPack and zlib

| Address | Name | Note |
|---|---|---|
| `0x4EC010` | `RefPack_DecompressWrapper` (codec vtable `0xFB9018` slot 2) | checks `0x10FB`; size-only call when the destination is null |
| `0x4EB2F0` | `RefPack_ReadSize` | header size field (3 or 4 bytes) |
| `0x4EB3B0` | `RefPack_DecompressCore` | one call site `0x4EC065`; pure function of (dst, cap, src, len), no globals [verified]: safe to run on any thread |
| `0x4EBFD0` | `RefPackCodec_ctor` | |
| `0x4EC0A0` -> `0x4EBB90` | compress | saves and caches (docs/25 E2) |
| `0x4331F0` | `zlib_inflate` (1.1.3) | three callers `0x4350A0`, `0x43D720`, `0x43DAA0`, all in the libpng block; **packages never use zlib** [verified callers] |

### 1.4 Asynchronous loading

| Address | Name | Thread | What it does |
|---|---|---|---|
| `0x728690` / `0x7274D0` | `AsyncLoader_ctor` / `_Init` | main (init) | Reads `MemoryUsageLimit` into `+0x38`; creates its JobManager thread (`+0x388`) |
| `0x729E10` | `AsyncLoader_RequestResource` [inferred name] | caller | Under `g_AsyncLoaderLock`: cache hit returns at once; else `0x7297E0` |
| `0x729CD0` | `AsyncLoader_RequestNoCallback` [inferred name] | caller | Same without a completion callback |
| `0x7297E0` | `AsyncLoader_CreateLoad` | caller | Factory (`0x2C`) and database (`0x44`) lookups (brief `g_ResourceLock`), test open, `LoadInfo` (0x118 bytes), counters `+0x74` (loads) and `+0x78` (bytes); for databases of kind `0x3C4B9A4` (database slot `0x0C`) it takes a separate path and calls the database's slot `0x50` [verified call; purpose not decoded] |
| `0x727D20` | `AsyncLoader_PumpIOQueue` | caller / job | Issues the next queued read only while bytes in flight `+0x30` <= `MemoryUsageLimit` `+0x38` (or <= the largest single request `+0x34`) |
| `0x727C50` | `AsyncLoader_IssueRead` | caller / job | `+0x30 +=` request size, hands a read request to the RW file system (`0x401400` -> `0x401D10`) |
| `0x401D10` / `0x401CA0` / `0x401BE0` | `RWFS_QueueRequest` / `RWFS_StartIOThread` / `RWFS_IOThreadProc` [inferred proc name] | I/O thread | Queue under lock `0xF97F84`, sorted by priority then file offset; one I/O thread created lazily |
| `0x72A150` | `AsyncLoader_JobDecompress` | AsyncLoader job thread | Opens the record in stream mode (no decode under `g_PackedFileLock`), copies into a `MemoryStream`, RefPack-decodes with its own codec, builds the record |
| `0x72A020` | `AsyncLoader_QueueDependencies` | job | Parses child keys (`0x726BA0`) and queues them as more loads |
| `0x7294F0` | `AsyncLoader_Complete` | pump caller [inferred: the frame that services the loader] | Factory create (slots `0x18`/`0x1C`/`0x24`), `FindCache` + `CacheResource`, client callbacks (`0x727DD0`) |
| `0x726480`, `0x726DB7` | async factory helpers (`ResourceLoad/AsyncFactory/*`) | job | Child records and streams for factories that build in pieces (`Renderer/AsyncModelFactory`) |

### 1.5 Caches, discovery, preloader

Unchanged from docs/08 §2.5-2.6 and verified again here: `ResourceCache_ctor 0x732910` (vtable `0xFFDD94`, `CacheBudget` `+0x8C`, overflow `+0x90`, held bytes `+0x94`), `ResourceCache_Find 0x7324E0`, `_Add 0x732A00`, `_Touch 0x732B60` (moves bytes between flush list and held, `CacheRefsToRelease`), all under `g_ResourceCacheLock 0xFFDD34`. Two instances from `ResourceSystem_Init 0x738670`: `ResourceCache` (priority 0, budget from `GraphicsRules.sgr`, see docs/08 Phase 0) and `AnimationCache` (priority 1000, forced 15 MB). `Resource.cfg`: `App_StartupFlags 0xECB600` -> `ResourceConfig_LoadResourceCfg 0x596450` -> `ResourceConfig_LoadOneCfg 0x5961C0` (three calls: game data, `Win32/Resource.cfg`, `Mods\Resource.cfg`) -> `ResourceConfig_ParseDirectives 0x4A1BD0` -> `RegisterDatabase` [verified callers]. Resource-system init also allocates a fixed 513 KB `RWFileSysAllocator` (0x80228 bytes) and the device drivers (`File System Device`, `ResourceManDeviceDriver`, `StreamableDeviceDriver`, debug `ThrottledDeviceDriver`) [verified tags].

### 1.6 Factories (who decodes a type and owns the object)

Each subsystem registers its factories with the manager (`RegisterFactory`, slot `0x28`); `FindFactory` picks the first factory for the type whose accept test passes. The factory's `CreateResource` (slot `0x1C` on the sync path) gets the open record and returns an engine object; the record and its buffer are released right after (`GetResource` releases the record via its slot `0x08`) unless the factory keeps a reference [verified for the sync path]. Factories found by their allocator tags [verified strings; registration sites not traced one by one]:

| Owner | Factories |
|---|---|
| Renderer | `ModelFactory`, `AsyncModelFactory`, `ModelLODFactory`, VB/IB/VF/Skin/MaterialDef/MaterialSet factories (`0x6D9800`), `TextureFactory` (`0x722C80`, `0xEC9760`), `RadTextureFactory`, `AnimatedTextureFactory`, `LightingDataFactory` |
| Animation | `Factory/Clip`, `Factory/Rig`, `Factory/TrackMask` (`0x5721A0`), `ObjectSlotResourceFactory` |
| CAS | `PartResourceFactory` (`0x5B39D0`), `CASPartDataResourceFactory` (`0x5AF070`), color-info factories (`0x5B5250`), `SimOutfitResourceFactory` (`0x5E4DB0`), `FacialBlend`, `BlendGeometry`, `CompoundBlend`, `SkinTone*`, `HairTone`, `SpeciesDefinition`, `TextureCompositor*`, `UserPresetList`, `ResourceLookupTable` |
| Objects / lots | `ObjectResourceFactory` (`0x937710`), `FootprintResourceFactory`, `NameGuidMap`, `SignatureMap`, `VisualProxy`, `CTCatalog/CatalogProductFactory` (`0xC8D590`), `ModularObjectResourceFactory`, `Terrain/MeshResourceFactory` |
| Script / data | `PrototypeResourceFactory`, `SignedAssemblyResourceFactory`, `AssemblyManifestResourceFactory`, `DataClassResourceFactory`, `String/TableFactory` (STBL, `0x59F650`) |
| UI | `LayoutFactory`, `UITextureFactory`, `HitMaskFactory`, `TriggerMapFactory`, `ThumbnailResourceFactory`, `SaveGameMetadata/Factory` |
| Other | `SACS/Factory/*` (state machines), `SpeedTree/Factory`, `Physics/KDTreeFactory`, Audio factories (`0x8BE390`), `DownloadContent/SimExportResourceFactory` |

## 2. Load paths, with the thread of each step

### 2.1 Synchronous (`GetResource`, used by most engine code and all 39 resource ICs)

| Step | Code | Thread | Locks held |
|---|---|---|---|
| 1 | `GetResource 0x4B0480` | caller (sim for ICs, main for render/world code) | `g_ResourceLock` from here to the end |
| 2 | Cache lookup (`FindCache` -> `ResourceCache_Find`) | caller | + `g_ResourceCacheLock` briefly |
| 3 | `GetPackageSource`: probe each database in priority order (`OpenRecord` test mode -> `IndexCompressed_Find`) | caller | + `g_PackedFileLock` per probe |
| 4 | `FindFactory` (type hash map) | caller | |
| 5 | `OpenRecord`: read (`ReadFile`, blocking) + RefPack decode | caller | + `g_PackedFileLock` for read and decode |
| 6 | Factory `CreateResource` | caller | `g_ResourceLock` still held |
| 7 | `CacheResource`, release record (frees raw/decoded buffers unless kept) | caller | + cache lock |

### 2.2 Asynchronous (`AsyncLoader`)

| Step | Code | Thread | Locks |
|---|---|---|---|
| 1 | Request (`0x729E10`), cache check, `CreateLoad` | caller | `g_AsyncLoaderLock`; brief `g_ResourceLock` for factory/database lookup |
| 2 | Queue read, gate on `MemoryUsageLimit` (`0x727D20`, `0x727C50`) | caller, later the job | `g_AsyncLoaderLock` |
| 3 | File read in priority + offset order | RW file-system I/O thread | RWFS queue lock `0xF97F84` |
| 4 | Decode (RefPack), build record, queue child keys (`0x72A150`, `0x72A020`) | AsyncLoader job thread | none of the four resource locks during decode [verified for the decode call; reads of the stream record inferred to take `g_PackedFileLock` per chunk] |
| 5 | Factory create, `CacheResource`, callbacks (`0x7294F0`) | the thread that services the loader [inferred] | cache lock, `g_AsyncLoaderLock` |

Lock order seen: `g_AsyncLoaderLock` -> `g_ResourceLock` -> `g_PackedFileLock` / `g_ResourceCacheLock`. No reverse order found in the decompiled functions [verified for those only].

## 3. The resource lock `0xFB2CD0`: all references

23 references in 19 functions (the brief named 22): `0x4AFF70`, `0x4AFDCA` (slot `0x50`, sets the lock as the argument and tail-jumps), `0x4B0480`, `0x4B05F0`, `0x4B0960` (2), `0x4B0D10`, `0x4B10E0`, `0x4B1180`, `0x4B1380`, `0x4B17E0`, `0x4B18A0`, `0x4B1CD0`, `0x4B1D50`, `0x4B2400`, `0x4B24B0`, `0x4B2770`, `0x4B29D0` (2), `0x4B2D00` (3), `0x4B2EC0` [verified, `XrefsTo.java`]. Every one is a manager vtable method; nothing outside the manager names the lock, so other code can only hold it through the manager's slot `0x50` or by being called from inside `GetResource` (factories, which then may load child resources recursively on the same thread). Hold times: long (read + decode + factory) only in `GetResource`/`GetResourceEx`; short map operations everywhere else; `GetDatabaseList` (slot `0x48`) probes every package while holding it.

## 4. Who calls the manager

`ResourceMan_GetManager 0x4AFD20` has 485 call sites in 431 functions [verified]. 39 are script internal calls, so they reach the manager on the **sim thread** (through `GetResource` they load synchronously on a miss): CAS (`CASUtils_*` color info, skin tone, presets, `FacialBlendLoad`, `BlendUnitLoad`, `CompoundBlendLoad`, `SimOutfitLoad`), UI (`LoadLayout*`, `LoadUIImage`, `LoadUIHitMask`, `PickPixel`, `GetColorPickerSwatches`), world (`World_ResourceExists`, `ObjectPlayVideo*`), catalog/user tools (`CTProductModularObject_Load`, `ProductGeneric`, `GetPresetIdFromProductAndString`), `SACS_AcquireDriver*`, `Animation_ClipExists`, `Simulator_GetAllSimulationObjectKeys`, `Simulator_CountResources`, `DownloadContent_*`, `CacheManager_*`, `Audio_SetEventModifiers`. Named native users: `CASPartCacheService_Init`/`OpenCachePackage`, `CASCompositorCache_Fn`, `Preloader_Init`, `WorldManager_LotRebuildCaller`, `KDTreeFactory_Create`, `ScriptHost_LoadImage`. The densest caller blocks are CAS (`0x5Axxxx`, `0x5Bxxxx`, `0x5Exxxx`), routing/objects (`0xB3xxxx`, `0xA6xxxx`), catalog (`0xC9xxxx`) and download content (`0x9Cxxxx`).

## 5. Caches

| Cache | Size | Eviction | Source |
|---|---|---|---|
| `ResourceCache` (generic decoded resources) | budget 200 MB from `GraphicsRules.sgr` (not `Default.ini`), overflow 10 MB; **measured 341-460 MB held** in BP | unreferenced objects go to a flush list and are dropped over budget; referenced ones are counted but cannot go | docs/08 §2.5, Phase 0 [measured] |
| `AnimationCache` | 15 MB fixed | same class | [verified] |
| CAS part cache | every CASP in every package, rebuilt into `CASPartCache.package` at startup | none | docs/08 [verified] |
| Buy/build catalog `[0x11ED034]` | ~8 MB fixed objects for 14,577 OBJDs plus per-product heap [to measure] | none until the catalog is cleared | docs/08 [verified] |
| Compositor caches | on-disk files 100 MB / 500 MB; in-memory MRU | MRU | docs/08 |
| Preloader (`PreloadedResources`) | the `KeyFilter` keys, kept when `HoldOntoResources` (default true) | never | docs/08 |
| Package indexes | ~17.6 B per resource (~3.5 MB CC, ~15 MB EA) | live while the package is registered | docs/08 [verified layout] |
| AsyncLoader in flight | `MemoryUsageLimit` 20 MB of reads in flight | n/a (gate, not cache) | §1.4 [verified] |

## 6. Memory owners

- Every resource buffer (raw, decoded, `RecordData`, async `MemoryStream`, `LoadInfo`) comes from the EA GeneralAllocator (dlmalloc derivative, `0x4E5E40`, one lock) through `DefaultAllocator 0x931020` / `EA_AllocTagged 0x4E3F90` [verified]. With S3SS's allocator hook these land on mimalloc instead (docs/08 §2.5).
- Sync compressed read: `fileSize + memSize` transient per resource, the raw buffer freed right after decode, the decoded one when the factory releases the record [verified]. Async read: raw read buffer, then a `MemoryStream` copy, then the decoded buffer: up to three copies transiently [inferred from the allocations in `0x72A150`/`0x727540`; whether the raw buffer is the stream's own is not checked].
- Fixed: `RWFileSysAllocator` 513 KB, 920 B per package object, the index tables.
- The real growth stays the decoded objects that live engine objects reference (meshes, textures without DXVK, CAS parts, string tables, catalog), docs/08 §3.

## 7. Cost on the sim thread [measured, existing samples only]

Pooled over the 16 simstack files (10,251 samples; 1% ~ 10 ms/s). Self time = the first TS3W frame of the sample.

| Block | Self | BP / IP / SV |
|---|---|---|
| Package probe: `DBPF_IndexCompressed_Find 0x4AA1B0` (48) + `GetPackageSource` loop (21) | **0.67%** | 0.74 / 0.58 / 0.75% |
| RefPack decode (`0x4EB3B0` via `0x4EC010`) | 0.23% | 0.17 / 0.22 / 0.41% (matches N15) |
| Index key enumeration `0x4AC9C0` (CAS part search, `GetKeyList`) | 0.17% | 0.07 / 0.29 / 0.06% |
| Resource caches | 0.07% | |
| AsyncLoader, MemoryDB, Resource.cfg, RW file system, zlib | ~0.03% | |
| **All resource-system code** | **~1.2%** | 1.05 / 1.33 / 1.68% |
| Lock waits with resource frames | 0.05% (5 samples) | |

Resource frames appear anywhere in the top 12 frames of 2.7% of samples. Inclusive numbers are a floor: the manager functions use no frame pointer, so `GetResource` itself never shows in the scanned stacks (0 samples) [measured]. Load-time cost (startup ~45 s to the main menu, world load 29-50 s in `probe-out/startup-timeline.log` and harness logs) has no per-function split in the existing data: there is no load-window sampler run yet [to measure].

## 8. Candidates

Ranked by help for the short-term goals and by ease. None is designed or built.

| # | Goal | Candidate | Gain | Effort / risk |
|---|---|---|---|---|
| R1 | stutter, loads | **Key -> database map** replacing the linear probe in `GetPackageSource` (slot `0x40`): build a hash map over all read-only `DBPF` indexes when packages register (`RegisterDatabase` hook), keep priority order, fall back to the probe for writable databases (saves, caches, DCCache) and on any registration change | ~0.6% sim thread (~6 ms/s) in live mode [measured cost, gain inferred]; larger with many CC packages and at load, where every miss probes ~440 packages (docs/08) | medium: one detour on a vtable slot, memory ~16 B per key (~4 MB) [inferred]; must match "first registered among equal priority" |
| R2 | crash | **Take `g_ResourceLock` in `GetKeyList`** (or snapshot the database array) | removes a use-after-free race on registration [inferred risk] | small; holds the lock for a full key walk, so only around the array walk, not the index walks |
| R3 | loads | **More AsyncLoader decode threads**: `AsyncLoader_Init` creates one job thread; RefPack core is pure, the async path decodes outside the global locks | parallel decode for async (streamed) loads [inferred]; factories that run in the job stage must be checked for thread safety first | medium-high |
| R4 | loads, stutter | **Raise `MemoryUsageLimit`** (`Sims3.ini [ResourceSystem]`, 20 MB): it caps reads in flight, not memory kept | faster streaming bursts at the cost of transient memory [inferred] | trivial, measure only (docs/08 §9) |
| R5 | loads | **Parallel package index load at startup**: `DBPF_IndexCompressed_Load 0x4AD020` runs per package, serially, during `Resource.cfg` processing on the main thread; reading and converting indexes on a pool before the serial `RegisterDatabase` calls | startup time with large CC sets [inferred; not measured] | medium |
| R6 | stutter | **Prefetch for sim-thread sync misses**: the 39 ICs load inline under both global locks; warming the cache through the AsyncLoader (lot streaming, CAS open) turns their misses into hits | hitch removal on first use [inferred]; live-mode average only ~0.2% (N15) | high (needs the right keys) |
| R7 | memory | `ResourceCache` budget: held bytes run at 2x budget because referenced objects are counted (docs/08 Phase 0); a lower budget only trims the unreferenced part | up to the flush-list size (to measure with `/rescache`) | config only |
| R8 | memory | Avoid the async extra copy (`MemoryStream` + decoded buffer) | transient MB per streamed resource [inferred] | medium; check first whether it really copies |

Not candidates: lock contention (0.05%, §7); zlib (not used by packages); index memory (small, docs/08).

## 9. Open questions

1. Which thread runs `AsyncLoader_Complete 0x7294F0` (the factory stage of async loads): main thread through `Timeslices/ResourceSystem`, or the job thread? Decides whether R3 needs thread-safe factories.
2. Who calls the manager's `LockUnlock` (slot `0x50`), and what the separate `AsyncLoader_CreateLoad` path for databases of kind `0x3C4B9A4` (database slot `0x50`) does.
3. Load-window cost split (startup, world load, travel): needs a sampler on the main and job threads during loads, before R1/R3/R5 can claim load-time gains. The routing part of a world load is measured by `[LoadProf]` (§10).
4. Per-package probe count per miss with the maintainer's CC set at runtime (one counter on `GetPackageSource`), to size R1.

## 10. World-load routing share: `[LoadProf]` (2026-10-19, built, not run)

**Question (Rodney, 2026-10-19):** how much of a world load is the game building routing data, versus everything else (resources, Mono/script work, layout, terrain)? If routing is a small share, caching routing data on disk next to the save is not worth it.

**Already known** [verified live, docs/112 §5.4]: the load's G build (water grid, `0x82A030` from `0x835390`) took 688 ms in BP and 505 ms in IP, once per load, on the sim thread. Nothing else of the routing load path has a time yet. Lot graphs are not built at load by default: `LotGraph_Build 0x63CB90` runs lazily at a lot's first route (`RoomGraph_FirstUse 0x63D2F0`), unless `[RouteSnap] Prebuild=1` builds all of them at world event 7 (docs/94 §16) [verified].

### 10.1 Where the routing data is built during a load [verified static, 2026-10-19]

Load order is docs/94 §9.1 (steps 1-10 of `WorldManager_LoadWorld 0xC6CF80`, sim thread; steps 4 and 6 run on the main thread while the sim waits). The routing builds on that path, with the thread from docs/38, docs/94 §9.1 and docs/100 §3:

| Step | Routing work | Entry timed | Thread |
|---|---|---|---|
| 4. World init (`World_LotStreaming 0xC622D0`) | routing world setup under `world+0x918`, including the R load | call `0xC626F8` -> `RoutingWorld_LockedOp_WorldInit 0x829410`; inside it `RTData_Load 0x66D110` (only caller `0x82942F`) | main (sim waits) |
| 5. Lots (`CreateLotsFromLayer 0xC65E00`, `LoadLots 0xC678D0` / `0xC64630`) | per lot: lot table insert and lot quadtrees, object footprints into the quadtrees and the footprint grid, polygon caches, the lot's R update | `LotTable_Insert 0x62E680`, `LotRouting_QuadTreeBuild 0x62F8A0`, `RoutingWorld_UpdateObjectFootprintCore 0x8321F0`, `LotQuadTree_ApplyInsert 0x665C50`, `LotQuadTree_FlushDeferred 0x666A40`, `Footprint_EnsureLevelPolygons 0x80E5B0`, `Footprint_EnsureAllPolygons 0x80E610`, `RoutingWorld_ApplyLotUpdate 0x82B590`, `RoutingWorld_RestoreLotBlockedBits 0x82A190`; `Lot_FinishLoad`'s call `0xACF3CF` -> `RoutingSystem_LotLoadFloorUpdate 0x834DC0` | sim |
| 7. G build | water grid, shorelines, R type bits | calls `0x8352F1`, `0x835390`, `0x8358B5` -> `WaterGrid_BuildFromWorld 0x82A030` | sim |
| (rare) world routing rebuild | `RoutingWorld_BuildWorldRoutingData 0x8349C0` | calls `0xC6BE53`, `0xC6CD02`, `0x835A27` | sim |
| 10 or later (lazy, or `Prebuild=1` at event 7) | lot graphs and room connectivity | `RoomGraph_FirstUse 0x63D2F0`, `LotGraph_Build 0x63CB90` | sim (route worker with `[RouteAsync]`) |

The other routing calls in `Lot_FinishLoad 0xACF170` (`0xACF3F9`-`0xACF471`: `0x631C20`, `0x637900`, `0x80B490`, `PathPlanner_AddFootprint 0x6376D0`) run only for lot files older than version `0x45` with `+0x408 == 1` and `+0x40C == 0x50` (the branch at `0xACF3D4`) [verified]; their footprint inserts still reach the timed leaves. `Lot_Load 0xACF520` itself calls no routing code directly [verified: no call into `0x600000-0x690000` or `0x800000-0x840000`].

Prologues and call edges: capstone and a raw branch scan on the Steam 1.67 exe (whole instructions, no relative operand, nothing jumps into the stolen bytes, every call site is `E8` to its target); `steerro-test` re-checks all 28 rows on the image ("loadprof: 28 sites match").

### 10.2 What `[LoadProf]` measures (ts3native 0.32.0, counters v59, `loadprof.cpp`)

- **Window:** a `WorldManager_LoadWorld` call (`0xAABE33` save load, `0xC6D9E8`) opens it and zeroes the rows; world event 7 (`ScriptHost_PostWorldReady`, its `World_FireEvent` call `0x769993`) closes it. `lpWindowMs` = LoadWorld entry to event 7. Event 7 waits for the script host's pending tasks (docs/94 §9.1), so the window ends after the managed post-load work.
- **Per site** (`lpSite_<site>_<row*3+k>`): calls, inclusive microseconds and the longest call; row 0 = calls started in the window, row 1 = after it (lazy builds in the first minute of play).
- **Routing total** `lpRtUs[0]`: only the outermost routing frame of each thread adds, so nested routing calls (FirstUse -> LotGraph_Build -> QuadTreeBuild -> ApplyInsert) count once; `lpRtThrUs` splits it by main / sim / other thread. Main-thread routing in steps 4 and 6 runs while the sim waits, so it is on the load's critical path too.
- **Phases** (not routing): `World_LotStreaming` and `World_PostLoadInit` (main), `CreateLotsFromLayer`, the two `LoadLots`, `LoadWorld` itself. Window minus `LoadWorld` = the script side after the native load (managed post-load, until event 7).
- At event 7 the log gets `loadprof window ...` and one `loadprof   routing|phase <name>: calls, ms, max` line per site.
- Measurement only: each stub times the call and runs the game's code unchanged. A site another section already hooked is skipped (`SKIP loadprof ...`): run `[LoadProf]` alone, as the queue does. Not timed: the harness part before `LoadWorld` (save file read, managed `LoadGame` setup); the report shows it as harness load minus window.

### 10.3 Run and read

1. Build: `native\ts3native\build.cmd` (selftest ok), then park `native\ts3native\out\ts3native.asi` as `game-copy/ts3native.asi.loadprof` (the tested `out/` build stays as it is).
2. Queue (6 jobs, fresh launch each, about 5-7 min per job, ~40 min in all): `node tools/harness/queue.ts tools/harness/queues/load-profile.json`. BP, IP and SV `-Spring` saves, two runs each so the second shows the warm file cache; each loads, plays 1 min at normal speed, quits.
3. Report: `node tools/harness/load-profile-report.ts probe-out/queue/load-profile-<date>.jsonl`. Per job: window seconds, harness load seconds (`world loaded after N s` from the job log), routing ms and its share of both, the thread split, the phases, routing by function (window | after), and the lazy routing in the first minute.

**How to decide:** a disk cache next to the save could save at most the routing total of the window (and the lazy lot graph builds of row 1, which happen in play, not in the load). Below ~10% of the harness load it is not worth a save-format change; the G build alone (~0.5-0.7 s) is already known. A first-run vs second-run gap much larger than the routing total means the load is I/O bound, and resource work (§8 R1/R3) is the lever instead.

Effort: build and docs ~2 h (done); the queue ~40 min of game time (Rodney); reading the report ~15 min.

### 10.1 Results 2026-10-19 (`load-profile.json`, `[LoadProf]` build, 2 fresh loads per world)

| Job | Harness load | Window (LoadWorld to event 7) | Routing in window | Share of harness load | Water grid build | Lazy routing, first minute |
|---|---|---|---|---|---|---|
| bp-load-1 | 26 s | 17.5 s | 1,086 ms | 4.2% | 1,070 ms | 129 ms |
| bp-load-2 | 25 s | 16.8 s | 1,081 ms | 4.3% | 1,066 ms | 86 ms |
| ip-load-1 | 24 s | 15.8 s | 1,109 ms | 4.6% | 921 ms | 26 ms |
| ip-load-2 | 24 s | 16.7 s | 1,134 ms | 4.7% | 945 ms | 27 ms |
| sv-load-1 | 24 s | 15.8 s | 989 ms | 4.1% | 972 ms | 16 ms |
| sv-load-2 | 25 s | 17.2 s | 990 ms | 4.0% | 971 ms | 110 ms |

**Verdict [measured]:** routing is 4.0-4.7% of the load, under the 10% rule, so a disk cache of routing data is not worth it. Nearly all of it (85-99%) is one call: `WaterGrid_BuildFromWorld` (G / water grid, from `0x835390`), 0.92-1.07 s on the sim thread per load. Everything else is small: lot quadtrees ~11-14 ms, R load ~4 ms, footprints 3 ms (IP 170 ms, max 23 ms per call). The rest of the load is LoadWorld itself (~11.6 s) and script-side work.

**If load time matters later:** the one routing target is the water grid build (~1 s per load). Options: cache only the water grid per world (static unless terrain or water changes), or build it on another core while the rest of the load runs. Neither is planned.

**Fix 2026-11-22 (docs/100 §13.2):** until then every LoadWorld entry with `[LoadProf] Enabled=1` zeroed all counters from `lpWindowMs` to the end of `Ts3nCounters` (every section after v59), not only the LoadProf rows. The 2026-10-19 rows above were taken with LoadProf alone, so they stand; runs that combined LoadProf with other sections lost those sections' counters at each load. Now only the LoadProf rows are zeroed. The water grid build is split by phase in docs/100 §13 (`[WaterGrid] Prof`).
