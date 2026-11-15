# 581. EA performance code left off, unfinished or unused

2026-11-15, static survey only (decompiled 1.67 managed code in `game-copy/decompiled`, the managed call graph
`game-copy/analysis/callgraph`, Ghidra on TS3W 1.67, the shipped `GraphicsRules.sgr`). Nothing built, no game run.
EA code is described in our own words; only type/member names and addresses are quoted.

Question: which performance features did EA build into The Sims 3 and then leave off, unwired or unused, and could
any of them make the game run better under the project rule (controlled Sims' results equal vanilla; only
proven-exact speedups and EA bug fixes stay)?

Method:
- Managed: grep of `[Tunable]` / static bool gates by name (cache, throttle, LOD, batch, pool, fast, lite, enable,
  disable, debug, use, skip, force); call-graph in-degree 0 scan over 179 k methods for perf-named methods
  (`Fast|Cache|Batch|Lite|Lazy|LOD|Cull|Throttle|...`) and for SimIFace bridge wrappers with no game caller;
  EA console command list (`Commands.cs`, 138 registrations) for cleanup / reset routines.
- Native: string xrefs and decompiles for config keys and the script cache (`tools/ghidra/run-headless-linux.sh`
  with `StringXrefs`, `XrefsTo`, `DecompileTo`; `-Xmx3g`, niced).
- Ini: every `prop $ConfigGroup` key in `GraphicsRules.sgr` and how each branch sets it.
- Every candidate was grepped against `docs/`, `tools/`, `mods/` and `native/ts3native/src` before calling it new.

## 1. Ranked top 10 (gain / risk)

| # | Candidate | Kind | Gain (inferred) | Result risk | In project already? |
|---|---|---|---|---|---|
| 1 | Script tuning cache (`ScriptCore.CacheManager`, `scriptCache.package`) never fills on the first start after a delete, and is turned off for the whole session on any signature or ownership miss | startup time | seconds per game start (reflection scan of every `[Tunable]`, D&P trees, autonomy tuning) | none when only the fill timing changes; forcing it on against EA's own ownership check is **not** exact | no: docs/07, docs/06, docs/236 only delete the file |
| 2 | `SharedFridgeInventory.CleanupFridgeRecipeCaches()` never called | object count, save size | fewer orphan `Ingredient` objects (count unknown) | low for the orphan part (objects at origin, no inventory, not in use); the spoilage re-derive part needs a check | no |
| 3 | `AllowImpostorUnload` config key, true only for the lowest draw distance | address space | lot impostors unloaded when far (MB unknown) | visual only (distant lots may reload or pop) | symbol only (`tools/ghidra/symbols/ts3w-1.67.mainthread.csv`) |
| 4 | `CullUndergroundBuildBuyStructures` config key, 0 in both `AdvancedRendering` branches | render CPU | fewer draws for underground build/buy parts | visual only | no |
| 5 | `OutfitUtils.ClearCachedParts()` never called | memory | caps `sCachedParts` growth | none (pure cache) | listed: `docs/data/script-leak-candidates.csv` row `sCachedParts` |
| 6 | `Simulator.ClearReflectionCache()` never called | memory | frees the native reflection cache (size unknown) | low, needs a check that nothing holds its entries | IC list only (docs/62) |
| 7 | `GameUtils.FreezeLotLODs(bool)` never called | streaming hitches | stops lot LOD swaps (e.g. during fast camera moves) | visual (lots keep the wrong LOD) | IC list only |
| 8 | EA console cleanups never run on their own (`removeallpuddles`, `fixupPetPools`) | world state | small | low to medium (each changes world data) | NRaas / docs/147 cover the daily EA cleanups |
| 9 | `DeviceConfig.ProcessorLevel` tiers (marine life caps, holiday house lights) | sim load | fewer spawned objects on a "Medium" tier | **changes results** (spawn counts): opt-in only | no |
| 10 | `Sim.DecideWhetherToEnableFacialIdles()` never called (`sEnableFacialIdles` dead switch) | animation | ~0 (facial idles already need high LOD) | visual | no |

None of the ten is a large in-game stutter win; the clearest gains are start-up time (#1) and memory or address
space (#3, #5, #6). EA's in-game throttles (autonomy LOD, `SimUpdate` adaptive sleep, role and service spreading,
daily genealogy purge) are already switched on and already covered by docs/144, docs/09, docs/147.

## 2. Details

### 2.1 Script tuning cache (`scriptCache.package`) [verified code, inferred gain]

What EA built [verified, decompiled ScriptCore + Ghidra]:
- `ScriptCore.CacheManager` (implements `Sims3.SimIFace.ICacheManager`) stores serialized results of expensive
  start-up work in `scriptCache.package` ("Scripting/CachePath", DB "Scripting/CacheDB", native open at `0x73CF20`).
  Users: `TuningControl.GameplayInitialize` (skips `FindTunables`, the reflection scan for every `[Tunable]` field
  plus their XML), `PersistStatic.GameplayInitialize` (the list of persisted statics), `AutonomyTuning`,
  `DreamsAndPromisesManager` (trees and primitives), `PaintingSkill` data.
- `CacheManager.VerifyCache` builds a `CacheSignature` (count of loaded assemblies, sum of their serializer hashes,
  checksum, size and record count of the `GameplayData` database) and compares it with the stored one. On a match
  caching is on. On a miss it clears the cache, writes the new signature, and **leaves caching off for the rest of
  that session**: nothing is saved until the next start.
- Native gate `CacheManager_IsCachingAllowed` (`0x73C6C0`): returns false when byte `0x115DBF0` is clear (cleared only
  by `0x73C660`, called from the command-line handler `0xEC9A40` when a `-gameplay` override path is given), then
  checks that the `GameplayData` keys resolve to one database [verified structure; that a Mods package overriding
  tuning XML makes this fail is inferred from the loop shape].

What we see [verified]: this install's `scriptCache.package` is 260 bytes (signature only) and is rewritten at each
start; a backup taken 2026-11-10 holds a 3 MB populated one. The harness `restore` path deletes the file (docs/236),
and docs/07 tells players to delete it after any mod change.

Why it is effectively off [inferred]: every delete costs two starts (the first only writes the signature), and the
signature misses whenever any loaded assembly changes (each ts3fixes / ts3agent rebuild), so harness and dev starts
almost never use it. Whether a typical modded player install reaches the populated state is unknown.

Options, smallest first:
1. Probe: log `CacheManager.IsCachingEnabled`, the cache file size and the time spent in
   `TuningControl.GameplayInitialize` per start (ts3fixes, off by default).
2. Exact fix: after `VerifyCache` writes a fresh signature, turn caching on for that session so the first start
   fills the cache (the data written is the same parse the game just did; the signature already guards reuse).
3. Docs: the signature already covers script assembly changes, so deleting `scriptCache.package` after a pure script
   mod change is not needed [inferred from `CacheSignature`; confirm with the probe]; tuning-XML mods are guarded by
   the native ownership check.
Do not force `IsCachingAllowed` true: that would reuse stale tuning when a mod overrides tuning XML.

### 2.2 `SharedFridgeInventory.CleanupFridgeRecipeCaches()` [verified unused]

Static method with no caller anywhere in the game assemblies (call graph in-degree 0, grep). It (a) re-derives the
fridge spoilage multiplier on every shared fridge inventory whose multiplier is exactly 0 or 1, and (b) destroys every
`Ingredient` that is not in use, has an item component, has no inventory parent and sits at `Vector3.Zero`, i.e.
ingredients leaked out of an inventory and never placed. The name suggests EA meant it as a load-time fix-up and never
hooked it in [inferred].

Use: count first (agent query: ingredients with no inventory parent at the origin, per save). If the count is real,
run part (b) once after world load for non-household-owned objects; it is an EA-written bug fix. Part (a) can change
spoilage for a fridge whose multiplier is legitimately 1, so keep it out unless proven a no-op.

### 2.3 `AllowImpostorUnload` [verified key, inferred effect]

Read by `0xAE3870` (world/lot manager config refresh) into `+0x396`, default 0. `GraphicsRules.sgr` sets it true only
in the `DrawDistance` `$Low` branch, false in Medium and High. Name and placement suggest distant-lot impostor meshes
may be unloaded when not needed [inferred]. Value for us: address space (the 32-bit limit). Risk: render only (lots
popping back or a reload hitch when the camera swings). Test: one in-load A/B of address-space headroom with the key
forced on (S3SS-style config override or SGR edit), plus a camera-sweep hitch repro.

### 2.4 `CullUndergroundBuildBuyStructures` [verified key, inferred effect]

Read in four places: `0xC0B4C0` and `0xC0E140` (copy into scene part `+0x352`), the constructor `0xC0EDB0` (same
field, next to the sea scene parts) and `0xAE3870`, which on a change calls `0xB25D10`; that function moves two lists
of scene parts to another render layer mask. Default 0, and `GraphicsRules.sgr` writes 0 in both the `$Off` and `$On`
branch of `AdvancedRendering`, so it can never be on through the shipped rules: a feature EA wired and then disabled
[verified]. Likely reason: visual errors when the camera is below ground or in basements [inferred]. Gain: draw calls
for foundations and basements not in view (render thread / main thread, size unknown). No sim effect.

### 2.5 `OutfitUtils.ClearCachedParts()` [verified unused]

Never called; `OutfitUtils.sCachedParts` (CAS part query results by key) only grows. Already in
`docs/data/script-leak-candidates.csv`. Wiring a clear at world load / travel would be exact (it is a pure memo).

### 2.6 `Simulator.ClearReflectionCache()` [verified unused, inferred size]

SimIFace wrapper over the native `Simulator_ClearReflectionCache`; no game caller. Could free reflection data after
start-up. Size unknown; docs/62 lists the internal call only for thread safety.

### 2.7 `GameUtils.FreezeLotLODs(bool)` [verified unused]

Internal call `0xEC0E80` sets byte `+0xD9` on the world manager (`0x11ECBC4`) through `0xC5FB20`; no game caller.
Probably an EA tool / screenshot aid that keeps lots at their current LOD [inferred]. Could be tested as a short
freeze during fast camera moves to avoid lot LOD swap hitches; visual risk only.

### 2.8 EA console cleanups [verified code]

`Commands` registers debug-only routines that never run on their own: `removeallpuddles` (removes every puddle on every
lot), `fixupPetPools` (re-syncs `PetPoolManager`'s type map, EP5 world), `cleanhouseholds` and `nukecruftysims`
(dispose non-instantiated Sims / households: **destructive, rejected**), `resetEveryone`, `stressreset`.
`purgegenealogy` already runs daily from `Autonomy.OnceADayCallback` (docs/09). Small gains; each changes world data,
so only as opt-in tools.

### 2.9 `DeviceConfig.ProcessorLevel` tiers [verified code]

`MarineLifeSpawner` (lot and world caps), `HolidayHouseLights` and the unused facial-idle switch read the CPU tier.
On any modern CPU the tier is High or Uber, so the caps are at their largest. Forcing a lower tier would cut spawned
objects but changes results; at most an opt-in "lite" profile.

### 2.10 `Sim.DecideWhetherToEnableFacialIdles()` [verified unused]

Would turn facial idles off on a Low CPU tier; never called, so `sEnableFacialIdles` is always true. `PlayFacialIdle`
already requires `IsSufficientlyOnScreenForHighLODSimulation`, so unseen Sims do not pay for it: gain about zero.

## 3. Checked and not useful

| Item | Finding |
|---|---|
| `Commands.sShowDebugInteractionInfo` (true by default) | no reader besides its own toggle: dead, costs nothing [verified] |
| `ObjectSpawner.EnableSpawnerWarnings`, `Route.sRouteLogEnabled`, `InteractionQueue.sDebugShowInteractions` | off by default [verified] |
| `GameStates.IsPerfTestRunning` | only skips a camera setup in `LiveModeState` [verified] |
| `[Script] Multithreaded` | on for `cpuCount >= 2` in the SGR, so already on (docs/203, docs/18) |
| `ObjectHiding` | player option in `OptionsDialog`, on for weaker hardware only (docs/48) |
| `EnableInteractiveLoading` | loading-screen UI, no perf effect |
| Autonomy LOD (`Autonomy.IsSufficientlyOnScreenForHighLODSimulation`, 60-tick cache), `SimUpdate` adaptive sleep (`kSimUpdateSleepTicks` scaled by high-LOD count) | built and on; docs/144, docs/211 |
| `RoleManagerTask` / `Services` loops | already spread with sleeps between roles and services |
| `ResourceCache` budget (`MemCacheBudgetValue` 200 MB) | docs/39, docs/66 |
| Daily EA cleanups (genealogy purge, life events, venue cleanup) | on; docs/09, docs/147 |
| EA profiler wrappers (`GameUtils.DumpProfilerReport`, `AddOrConfigureProfiler`) | release stubs (docs/06) |

## 4. Next steps

1. Script cache probe (2.1 option 1), then the first-start fill fix if the probe shows seconds per start.
2. Agent census of orphan `Ingredient` objects (2.2) on the baseline saves.
3. One address-space A/B with `AllowImpostorUnload` forced on (2.3).
