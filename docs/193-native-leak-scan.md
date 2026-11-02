# 193. Native cache and leak scan (docs/191 step 2)

Status: static only (2026-10-25), no game run. Data: `docs/data/native-leak-candidates.csv` (L1-L10 plus the refcount scan rows R1-Rn). Inputs: docs/190 inventory (37 sites), docs/191 prior art, docs/39, docs/40, docs/47, docs/54, `docs/data/apex-hooks.csv`. Pass 2 (same day) closed the §8 open items: L1 refuted, L3/L9/L10 verdicts, engine-wide refcount error-path scan (§3.6).

## 0. Summary

- **The catalogue pin (L1) is refuted** [verified static, one inferred link]. Every catalogue cell has a `CustomContentIcon` child (window id 35) whose script wrapper sits in the static `UIManager.mCustomControlInstanceDict`, and that dictionary only drops an entry when the icon itself gets a detach with `DisposeOnDetach` set. Pass 1 assumed the grid clear never detaches the icon. It does: the native cell clear removes the cell through the base `Window` remove-child, which sends the detach message `0x11` to the cell **and every descendant** while the grid is attached; the managed `Detach` event is that message (event 17); every custom control registers `OnCustomDetach` on load; and every native custom-control class sets `CustomControlFlags` bit 0 (`DisposeOnDetach`) to 1 in its constructor, which no layout of `CustomContentIcon` overrides. So Build/CAS/Edit Town icons are removed and disposed on every catalogue clear. `BuyController` sets the flag to 0 only because it recycles cells, and then disposes by hand. What remains of L1 is bounded (Buy recycle lists, two CAS drag thumbnails, one filter grid).
- **The per-cell transient is the real catalogue cost** (L2 + L3, GC-bound, not a session leak): each Build/CAS cell creates a `UI/LayoutInstance` + window tree (L2) and a **fresh thumbnail texture per call** (L3: `GetThumbnailImage` has no native cache on this path; it allocates a new `ThumbnailUITexture` + `Image` every call and renders into it). Both come back only when the Boehm GC finalizes the dropped `Layout` / `UIImage` wrappers.
- **Audio cache (L9):** the binary sample cache is capped at 100 MiB (soft: only entries idle > 20 s are evicted, per audio update, time-boxed); the audio `ResourceCacheMap` beside it has **no eviction** until audio reset/shutdown and holds a reference to every distinct audio resource the session touched (new row L11, growth bounded by distinct sounds; size per entry unknown).
- **UTFWin pools (L10):** free-list pools that never return memory to the heap (peak-retaining) [verified]; bounded by the peak UI display-list content, small.
- **Refcount error-path scan (§3.6):** new Ghidra script `tools/ghidra/RefPathScan.java` over the whole exe (39,949 functions, 10,749 with register-indirect calls decompiled): 997 rows in 685 functions, 28 with the error-path signature. Ranked by sampled call frequency and read by hand: **one real error-path leak, R1** (`0x54BA10`, UTFWin layout object creation: an AddRef'd object is dropped when its property block fails to parse; only on bad layout data, so not a normal-play growth source); the other reviewed rows are heuristic false positives. **No refcount error path explains session growth in normal play** [verified for the reviewed rows; the unreviewed tail is inferred low-value, §3.6].
- **Tooling:** pass 1 used a capstone index; pass 2 used Ghidra headless (read-only, low priority) for decompiles, caller trees and the refcount scan, capstone for vtable slots and immediates, and the UI layouts from `Game\Bin\UI\UI.package` (type `0x025C95B6`, 654 layouts) for property defaults.

## 1. Method

1. docs/190 rows with a container or cache owner were taken first: for each, the global or vtable was looked up in the index, insert and erase/evict/clear sites listed (direct callers, data pointers for virtual methods), and the trigger named.
2. The catalogue claim was traced from the script side down: decompiled `UI.dll` (`BuyController`, `BuildController`, `ItemGrid`, `Grid`, `Layout`, `WindowBase`, `UIManager`, `CustomContentIcon`) for create, clear, dispose and registry calls, then the native ICs they reach (`LoadLayout 0x782BC0`, `DestroyLayout 0x77A570`, `DestroyWindow`/`DestroyUIImage 0x779430`, `GridGeneric 0x77C030`, `GetThumbnailImage 0x782D00`) by instruction reads. Pass 2 followed `GridGeneric` op `kGridClearAllCells` down to the window remove and the detach message, and read the custom-control constructors and property tables.
3. Apex overlap from `docs/data/apex-hooks.csv` by address and by range.
4. Pass 2 refcount scan: §3.6.

Limits: indirect calls are not resolved beyond the vtable slots read by hand; the refcount scan is a heuristic over decompiler p-code (§3.6 lists what it cannot see).

## 2. Ranked candidates

| Rank | Id | What | Kind | Evidence | Owner |
|---|---|---|---|---|---|
| 1 | L3 | Per-cell thumbnail: fresh `ThumbnailUITexture` + `Image` per `GetThumbnailImage` call, freed only by the `UIImage` finalizer | collectable but uncollected (no native cache) | §3.3 | script (ours); texture memory = Apex context |
| 2 | L2 | Per-cell `UI/LayoutInstance` + window trees freed only by finalizers | collectable but uncollected | §3.2 | script / GC (ours) |
| 3 | L11 | Audio `ResourceCacheMap` (audio `+0x3310`): no eviction until audio reset | cache without eviction, bounded by distinct audio resources | §3.5 | us (docs/190 M12) |
| 4 | L9 | Audio `BinaryCacheMap` (audio `+0x3330`): 100 MiB soft cap, idle > 20 s eviction | bounded cache (large) | §3.5 | us |
| 5 | R1 | UTFWin layout object creation `0x54BA10`: AddRef'd object lost when the property parse fails | error-path leak, bad layout data only | §3.6 | us (UTFWin); not Apex |
| - | L10 | UTFWin display-list pools `0xFBA244` / `0xFBA474` | peak-retaining free lists, small | §3.5 | us |
| - | L1 | `CustomContentIcon` wrappers in `mCustomControlInstanceDict` | **refuted**: disposed on detach | §3.1 | - |
| - | L4-L8 | Buy cell recycle lists, CAS part cache, ResourceCache, Preloader, STBL | bounded cache or fixed cost | CSV | various |

## 3. Evidence

### 3.1 L1: custom controls and the grid clear (refuted)

- Add: `UIManager.WrapperFromHandle` looks up `mCustomControlInstanceDict` (static `Dictionary<uint, WindowBase>`); a miss constructs the registered control and adds it [verified managed].
- Remove: `UIManager.OnCustomDetach` removes and disposes when the detached window is a custom control, `DisposeOnDetach` is set and the global `DisableDisposeOnDetatch` is off (set only inside `UIManager.Reparent`); `UIManager.ClearEventRegistry` from `UIManager.Shutdown` clears the rest [verified managed].
- Registration: `UIManager.OnCustomControlLoad` (the `CustomControlLoad` handler every custom control gets) calls `PreInit`, hooks `Attach`, and adds `Detach += OnCustomDetach`; `WindowBase.Detach`'s add accessor is `UIManager.RegisterEvent(this, 17, ...)`, and 17 = `kEventWindowBaseDetach` = message `0x11` [verified managed].
- Default flag: `WindowBase.DisposeOnDetach` reads flag property `0x03AA2001` bit 0 (`kMaskCustomControlFlagsDisposeOnDetach`). The three native custom-control classes map `0x03AA2001` to a field in their property tables (`0x100E2D8` -> `+0x258`, `0x100E5A0` -> `+0x2E8`, `0x100E874` -> `+0x388`), and their constructors store 1 there (custom window `0x77E9E0` at `0x77EA13`, custom button `0x77EB20` at `0x77EB5D`, custom dialog `0x77ED20` at `0x77ED5D`) [verified instructions]. In `UI.package` 29 layouts contain a `CustomContentIcon` object and none sets `CustomControlFlags` on it (the property appears 280 times on other objects, 276 of them set to 1) [verified layouts]. So the icon is created with `DisposeOnDetach` on.
- Grid clear, native: `ItemGrid.Clear()` -> `Grid.ClearAllCells` -> `GridGeneric 0x77C030` op 13 -> grid interface slot `+0x1A0` (`0x5253C0`, vtable `0xFBD078`): used range (slot `+0xF8`), then `0x5252C0` calls slot `+0x198` (`GridClearCell 0x52ABB0`) per cell -> `0x528560` -> for window cells `0x528240`, which drops the cell from the grid's window map and calls `RemoveWindow` (IWindow slot `+0xE0`) on the grid's inner container (`+0x4E4`, a plain `Window` built by `0x515C80` in the grid constructor `0x52B2D0`) [verified instructions].
- `Window_RemoveWindow 0x514030` (base IWindow vtable `0x101E8F8` slot `+0xE0`): unlinks the child, and when the parent has a window manager (`+0x2C` of the interface) calls `0x50C7E0`, which sends message `0x11` to the window, recurses over its child list (`+0x210`), clears the manager's deferred slots and queued messages for it; then Releases the child (vtable `+4`) [verified instructions]. `DestroyAllChildren` (`0x77ABA0` -> slot `+0xE8` `0x5140C0` -> `+0xE4` `0x514070`) goes through the same remove [verified]. Only `0x5145A0` (batch re-parent) and the manager's root swap `0x50DBD0` also call `0x50C7E0`.
- So every icon in a cleared, attached catalogue grid receives message `0x11` and is disposed by `OnCustomDetach` [verified static for each step; the native message-to-managed-event dispatch is the one inferred link, and EA relies on the same path: `InventoryPanel` clears its grid with `Clear(true)` to dispose custom cells on detach].
- Why `BuyController` sets `DisposeOnDetach = false`: it keeps cells in two recycle lists across `Clear` and re-attaches them, so it must stop the dispose-on-detach and disposes the icons itself in `BuyController.Dispose` (L4, bounded). `CASFamilyScreen` does the same for its two drag-cursor thumbnails; `CatalogProductFilter` for its one item grid.
- Left over: an icon that is wrapped but never attached and detached (a cell built into a grid that is not in the window tree and destroyed before it is attached) stays keyed. No catalogue path was found that does this; the memory tool count (§4) will show it if it happens.

### 3.2 L2: layouts freed only by finalizers

- `LoadLayout 0x782BC0` allocates a 0xC0-byte object with tag `UI/LayoutInstance` (`EA_AllocTagged 0x4E3F90`), increments `+0xA4`, loads the layout and registers a script handle (type `0x30`) [verified instructions]. `DestroyLayout 0x77A570` resolves the handle and decrements `+0xA4`, deleting through vtable slot 0 at zero [verified instructions].
- Managed: `Layout.Dispose` or `~Layout` is the only path to `DestroyLayout`. Catalogue builders call `LoadLayout(itemKey).GetWindowByExportID(1)` and drop the `Layout` (BuildController 3 sites, BuyController when its cache is empty, CAS grids, `CustomContentIcon.PreInit` for its badge sub-layout) [verified managed].
- The grid clear Releases the grid's reference to the cell (§3.1), but the `LayoutInstance` and the managed wrappers keep the tree until the next Boehm collection plus finalizer pass. Not a leak if GC runs; a large transient if catalogues are clicked through quickly (docs/191 step 6 GC probe measures this directly).

### 3.3 L3: thumbnails (no native cache)

- Each cell sets its image drawable to `UIManager.GetThumbnailImage(product, Medium)`; the old image is never disposed, so its handle is released by the `UIImage` finalizer through the same Release body `0x779430` [verified managed + shared IC].
- `GetThumbnailImage 0x782D00` only tests the ThumbnailManager pointer `[0x11EDC30]` for null; it does not look anything up under it. It fills a request (`0xD4C2A0`: object and catalogue lookups, no container) and calls `0xD5C830`, which allocates a new `UI/ThumbnailManager/ThumbnailUITexture` (0xC B) and a new `UI/ThumbnailManager/Image` (0x2C B, `0x50A5F0`) with power-of-two dimensions from the request, queues the render (`Services/FrameDatabase/EventList` via `0xD4E950`, `0xD59A20`, submit `0x692F70`) and returns the image as a script handle (type `0x30`) [verified instructions].
- So there is no insert, lookup or eviction on this path: every call is a new texture, and its lifetime is the managed `UIImage`. Pass 1's question (does the native thumbnail cache evict?) does not arise for catalogue thumbnails; the cost is one texture per cell per populate until the finalizer runs. The source thumbnail resources come through the resource system (ResourceCache, Apex's area) [inferred].
- Size: power-of-two of the Medium thumbnail size, 32-bit [inferred]; at 128x128 that is 64 KB per cell, so a few hundred cells per category click put tens of MB on the GC's schedule. The `UIImage` census (§4) measures it.

### 3.4 docs/190 caches closed by the scan

- **CAS part cache** (#21): owned by `CASPartCacheService` at `[0x11CE42C]` (getter `0x5AE140`), built once in `CASPartCacheService_Init 0x5AEDF0` [verified getter; build path inferred]: fixed cost, never freed. `[0x11CE404]`, read and written only in `0x5A44A0` (its 2 references), is a one-time init guard bit, not the cache (docs/209 #95) [verified exe bytes].
- **ResourceCache** (#8-#10): `Find`/`Add`/`Touch` are virtual (data pointers `0xFFDDAC`/`0xFFDDB0`/`0xFFDDBC` in vtable `0xFFDD94`); trim `0x732E90` is called from shrink-both `0x733E70` (data pointer `0xFFE340`) and `0x737560` [verified]. Cache with eviction; Apex MemoryGuard hooks both. Not a leak.
- **Preloader** (#11): `0x731AE0` has one caller `0x731CA0` [verified]; held by design.
- **String tables** (#17): `LocStrSvc_RebuildTableList 0x59BDE0` has one caller `0x59C430` [verified]; fixed after load.
- **EA allocator trim** `0x4E4F90`: one caller `0x4E51B0` (free path, 8 callers) [verified]: consistent with Apex's note that core tops return on free.

### 3.5 Audio caches (L9, L11) and UTFWin pools (L10)

- **Audio object:** constructor `0x8C0720` (base) from `0x868D70` from `0x868380`, which publishes `obj + 0x27BD0` in `[0x11D8C40]`; so `audio = [0x11D8C40] - 0x27BD0` [verified instructions]. Three EASTL hash maps and a list are built there: `Audio/ResourceCacheMap` (`+0x3310` buckets, `+0x3314` bucket count, `+0x3318` element count), `Audio/BinaryCacheMap` (`+0x3330`, `+0x3334`, `+0x3338`), `Audio/PrirorityMap` (sic, `+0x3350`).
- **L9 BinaryCacheMap:** get-or-load `0x8B8D40` (vtable `0x103B018` slot `+0x1C8`) counts misses `+0x27330` and hits `+0x27334`, stamps each entry with the current time (entry `+0x20`, double) and adds the entry size (entry `+0x1C`) to the byte total `+0x2732C`. Trim `0x8B8F30` (slot `+0x23C`, called from the audio update `0x8BFAE0` at `0x8BFD08`) runs while the byte total exceeds the cap `+0x27348`, walks a round-robin cursor (`+0x27324`/`+0x27328`) and erases entries idle longer than `+0x27340`, within a time budget `+0x27338`. The constructor sets cap = `0x6400000` (100 MiB), idle = 20.0 and budget = 100 (`0x8C0CE5`-`0x8C0CEF`; the double comes from `0x103B3F0`) [verified instructions; seconds and milliseconds as units are inferred]. Cap 0 disables the cache. Verdict: bounded, soft cap 100 MiB of decoded sample data; it can exceed the cap only while every entry was used in the last 20 s. 100 MiB is a large share of a 4 GB address space: worth reading in the memory tool before any change.
- **L11 ResourceCacheMap:** lookup-or-load `0x8B7FD0` (slot `+0x1C0`): on a miss it loads the resource (type `0x2B9F662` through slot `+0x1C4`, others through the resource manager `0x4AFD20` slot `+0xC`) and inserts it with a reference held at node `+0x10`. The only other functions that touch the map are the clear-all `0x8BF250` (slot `+0x7C`, reached from the derived class's teardown `0x868320`) and the destructor `0x8BFEB0` [verified: displacement scan over `0x860000`-`0x8E0000` plus caller tree]. So every distinct audio resource the session touches stays referenced until audio reset or exit; growth is bounded by the number of distinct sounds, not by time. What the held object weighs (handle only or decoded data) is not read [inferred unknown]; if these references keep resource-system entries pinned, ResourceCache cannot evict them (Apex's area, report only).
- **L10 UTFWin pools:** `Renderable2D` pool (lock `0xFBA244`): get `0x5065B0` pops a free vector or allocates a new 0x50-byte `UI/UTFWin/Renderable2D`; put-back `0x5099C0` pushes onto the vector (growing it). `RenderableList` (0x10 B) and `DisplayList` (0xF0 B) free lists at `0x114E490` / `0x114E488` (lock `0xFBA474`): pop `0x509D80` / `0x509F10` (allocate when empty), splice back `0x509DF0` / `0x509E40` / `0x509E90`. Those five functions are the only references to the list heads [verified XrefsTo]; nothing frees pooled nodes to the heap. Verdict: peak-retaining, bounded by the peak UI display-list content (KB to low MB) [size inferred]. Not a growth source.

### 3.6 Refcount error-path scan (engine-wide)

- **Tool:** `tools/ghidra/RefPathScan.java` (new, read-only, run headless at low priority, one run for the whole exe). Per function with a register-indirect call it decompiles to p-code and marks AddRef = indirect call through vtable slot 0 with no other argument, Release = slot `+4` (the EA refcount convention seen in docs/39/40/47). A forward may-dataflow over the decompiler's basic blocks keeps an AddRef "owned" until a Release of the same object (object identity by pointer expression, so a field re-read matches), a store of it, a copy into a stack struct, a call argument or a return; the null edge of `if (p)` owns nothing. A return reached while owned is a leak path. Kinds: `asym` (the same AddRef also reaches a clean return: the error-path signature), `handoff` (no clean return, but the object escapes somewhere: ownership transfer the heuristic cannot order), `noRelease` (neither).
- **Run (whole exe):** 39,949 functions, 10,749 decompiled, 997 rows in 685 functions: 28 `asym`, 351 `handoff`, 618 `noRelease`. Frequency proxy: inclusive sample share per function from every `probe-out/profile/*stack*.jsonl` (145,241 sim-thread and 10,381 main-thread samples); 22 of the 28 `asym` rows and 365 of the 618 `noRelease` rows have samples. Raw output stays in `game-copy/analysis/leak193/` (not committed).
- **Reviewed rows** (top `asym` by share, plus the two hottest `noRelease`; each read in the decompile):

| Id | Function | Share (sim / main) | Verdict |
|---|---|---|---|
| R1 | `0x54BA10` UTFWin layout reader: create a window object by class id (factory slot `+0x20`), register it in the id map, AddRef at `0x54BB30`, then parse its property block (`0x54B890`) | 0.188% / 0 | **leak path** [verified decompile read]: when the parse fails it returns null without the Release, so the object keeps a reference and is never freed. Needs bad or unsupported layout data (broken UI mods); stock layouts parse. Not a normal-play growth source. |
| R2 | `0x6DC150` (main thread) | 0 / 0.511% | false positive: the decompiler reuses one variable for two objects (alias); every real path releases |
| R3 | `0x92BD10` | 0 / 0.019% | false positive: this class family has AddRef at `+4` and Release at slot 0 (inverted convention, smart-pointer assign pattern) |
| R4 | `0x847830` (steering, near `ObstacleAvoidance 0x847390`) | 0.084% / 0.010% | open: the decompile has unaffected-register artefacts, so the flagged path cannot be judged from it; needs an instruction-level read (~30 min) |
| R5 | `Locomotion_UpdateAgent 0x957210` (`noRelease`) | 1.861% / 0 | false positive: inline atomic refcount, slot 0 is the deleter called at zero, not AddRef |
| R6 | `0x6C9E20` (`noRelease`) | 0 / 4.836% | false positive: factory result and its AddRef/Release pair on two variables the heuristic does not join |

- **What the scan cannot see:** objects with an inline atomic count (no vtable call), classes with another slot convention, ownership passed through stack structs built field by field, and functions Ghidra cannot decompile cleanly. The 24 unreviewed `asym` rows are all at or below 0.01% sample share (cold paths); the `noRelease`/`handoff` tail is dominated by the false-positive shapes above [inferred from the reviewed sample]. A session leak from a refcount error path would need a hot function with a frequently taken error branch; none of the reviewed hot rows has one.

## 4. What the docs/191 memory tool should read

| Id | Read | How to count | Cost |
|---|---|---|---|
| L1 (check) | `Sims3.UI.UIManager.mCustomControlInstanceDict` (private static) from ts3fixes by reflection on the sim thread | `Count` and `CustomContentIcon` share: expected flat after a catalogue loop (refutation check); values with `WinHandle == 0` = disposed but keyed | ~0.1-1 ms per sample [inferred]; at catalogue markers only |
| L2 | managed `Sims3.UI.Layout` instance count | Mono class census (docs/191 step 6) or a finalizer counter; native: count live `UI/LayoutInstance` objects through the script host handle table (type `0x30`), table layout behind `[0x11D4BBC]` needs RE (~1 h) | census: soak only |
| L3 | managed `Sims3.UI.UIImage` instance count; native `UI/ThumbnailManager/ThumbnailUITexture` allocations if the allocator tags are countable | class census; drop after forced GC = L3 share | soak only |
| L9 | audio `+0x2732C` bytes, `+0x27348` cap, `+0x3338` entries, `+0x27334`/`+0x27330` hits/misses (`audio = [0x11D8C40] - 0x27BD0`) | plain reads, any thread [inferred: torn reads harmless for a counter] | ~0 |
| L11 | audio `+0x3318` element count of `ResourceCacheMap` | plain read; plateau = bounded by distinct sounds | ~0 |
| L10 | node count of the free lists at `0x114E488` / `0x114E490` (circular, walk under no lock: read-only, may be torn) | walk length per sample, capped at 100k | ~0.1 ms |
| all | `diag: mem` private, Boehm heap/free (`[0x12225A0]`/`[0x12225B4]`) before and after a test-agent `GC.Collect()` + `WaitForPendingFinalizers()` | drop = L2/L3 share | probe job |

## 5. Repro per candidate

All in live mode on the active household's lot (realistic preconditions, docs/TESTING), with the test agent driving the UI and `diag: mem` + the counts in §4 logged at each marker.

- **L2 / L3 (Build catalogue loop), L1 check:** enter Build mode, open the wall-paint catalogue, click through 10 categories, repeat 5 rounds, leave Build mode. Markers: before, after each round, after leaving, after a forced `GC.Collect` + finalizers. Expect the L1 count flat (refutation check), private bytes up per round and down after GC (L2 + L3). Same loop in CAS (hair, then clothing rows) and Buy mode as the bounded control arm (L4 should plateau).
- **L9 / L11:** play 10 min with music + TV + a party; log the audio counters every minute. L9 bytes should stay under 100 MiB; L11 entries should plateau.
- **L10:** open and close the large catalogue 20 times; free-list lengths plateau.
- **R1:** static only; a repro needs a deliberately broken layout package (out of scope for player-like tests). Count instead: none (no counter exists on this path).

Repro entries go into `tools/harness/repros.json` when the memory tool's catalogue markers are built (docs/191 goal: repro-driven); none were added here (static task).

## 6. Fix directions (not built)

- **L1:** none needed.
- **L2/L3 (ours, ts3fixes, opt-in):** dispose the old cell `Layout` and the old thumbnail `UIImage` when a Build/CAS cell is replaced or the grid is cleared (`BuyController` already disposes its icons; the thumbnail is the larger part), or let the docs/191 step 6 GC probe show whether a timed full collection after leaving Build mode/CAS is enough.
- **L9:** none unless the memory tool shows the 100 MiB cap reached in normal play; a lower cap is one constructor constant (`0x8C0CEF`), but it trades address space for audio reloads (measure first).
- **L11:** none until the per-entry weight is known.
- **R1:** a one-site patch (Release before the null return) would be correct but only matters with broken layouts; not worth a hook unless crash or memory reports point at UI mods.

## 7. Report draft for Apex Radiance (not sent)

> UI catalogue and audio memory, static findings (Sims 3 modernization, docs/193). (1) Correction to our earlier note: catalogue `CustomContentIcon` wrappers are not pinned; the native grid clear sends the detach message to every descendant and the icons are disposed on detach (custom controls default `DisposeOnDetach` on). (2) What catalogues do cost: every Build/CAS cell gets a new `ThumbnailUITexture` + `Image` from `GetThumbnailImage` (IC `0x782D00` -> `0xD5C830`, no native cache on this path) and a new `UI/LayoutInstance`; both are released only by Mono finalizers, so they ride on GC timing. We plan an opt-in script-side dispose. (3) Audio: the binary sample cache is capped at 100 MiB (`audio+0x27348`, idle > 20 s eviction), and the audio `ResourceCacheMap` (`audio+0x3310`, `audio = [0x11D8C40] - 0x27BD0`) keeps a reference to every distinct audio resource until audio reset; if those references pin ResourceCache entries, MemoryGuard trims cannot reclaim them. (4) An engine-wide AddRef/Release error-path scan found no hot leak path; the one real one (UTFWin layout object creation `0x54BA10`, bad layout data only) is in our area. Tell us if MemoryGuard or your snapshot tool sees texture growth after catalogue use, or audio resources that never leave ResourceCache.

## 8. Open items

1. ~~Native detach propagation from `kGridClearAllCells`~~: closed, L1 refuted (§3.1).
2. ~~Thumbnail cache eviction~~: closed, no cache on the catalogue path (§3.3).
3. ~~Audio cache cap~~: closed (§3.5, L9 + new L11); L11 per-entry weight still unknown (read the held object's type and size, ~30 min).
4. ~~Engine-wide AddRef/Release error-path scan~~: done as a heuristic (§3.6); left: R4 instruction-level read (~30 min), and the scan's blind spots (inline atomic counts, inverted slot conventions) if a memory run ever shows native growth the L rows do not explain.
5. Script handle table layout behind `[0x11D4BBC]` for a native live-layout/window count (L2), ~1 h.
