# 24. Community reverse-engineering projects and how to reuse them

Researched 2026-10-13. Goal: find other people doing reverse engineering, engine research or deep performance and bug fixing of The Sims 3, so we can reuse their names, addresses, parsers and findings instead of rediscovering them, and know who to invite to the crowdsourced repo.

Builds on docs/01 (fix comparison), docs/03 (S3SS anchors, LazyDuchess repos, symbol situation), docs/06 (Sims3MonoModder, MonoPatcher, Sims3-MCP, ts3-template, Interaction-Injector) and docs/07 (installable fixes). Those are not repeated; this file only adds what is new or changes their value.

Tags: **[verified]** I opened the repo, README, file tree, commits or page (GitHub API, 2026-10-13). **[snippet]** from a search-result summary only. **[inferred]** our reasoning.

License note: "none" means the repo has no license file. Then the default is all rights reserved: we may read it and cite it, and re-derive facts (addresses, struct offsets, behaviour) ourselves, but must not copy code. GPL-3.0 code can be copied only into GPL-3.0 code of ours. MIT/zlib/Unlicense can be copied with the notice.

---

## Top 10 by value to us

| # | Project | Why it ranks here |
|---|---|---|
| 1 | **Apex Radiance** (loinyx) | MIT, Steam 1.67.2 addresses, a published TS3W engine reference (main loop, services, threads, Mono GC, lot streaming, timers) and 15 shipped hitch fixes, several of which are the same ideas as our docs/08 offload and lookup-index plans |
| 2 | **Sims3MonoModder** (sims3fiend) | Already used (docs/06, 08). New value: per-method Mono profiler and assembly diff for script-side stutter (docs/04a) |
| 3 | **Sims 3 Settings Setter issue tracker** (sims3fiend) | 80 issues of field reports on exactly our patches: GC patch stutter (#43), memory growth (#60, #63), save lag (#65), access violations (#57) |
| 4 | **TS3-64bit-Patch** (cmedina-dev) | The only other public attempt at the 4 GB limit; GPL-3.0; has a hitch detector and allocation-pattern analytics |
| 5 | **S3LotLoDThing** (sims3fiend) | Two Steam addresses for the "Throttle Lot LoD Transitions" debug variable and the JZ it bypasses; author says it "reduces stutter dramatically" |
| 6 | **sims3-package-interface / s3pi-modern** (marcos4503, Destrospean; original Peter L Jones) | Reference DBPF/resource parsers; we already ported the S3SA decrypt from s3pi-modern |
| 7 | **Sims3HookMods** (ChrisNonyminus) | MIT, Steam-only ASI that hooks Mono setup and adds native internal calls: the pattern for a native-to-script bridge we can copy |
| 8 | **NRaas source** (Chain-Reaction) | Already used for routing logic (docs/03 §5.3); source of truth for Overwatch/ErrorTrap stuck-Sim resets |
| 9 | **sims-3-bevy** (Eamo5) | Clean-room Rust readers for worlds, terrain, meshes and textures; useful to check our own format assumptions |
| 10 | **SimsWiki / MTS wiki TS3 modding reference** | Resource type IDs and DBPF/RCOL docs that `tools/dbpf.ts` should cite |

---

## 1. Apex Radiance (loinyx): highest value

- **URL:** https://github.com/loinyx/Sims3-ApexRadiance [verified]
- **Author:** loinyx; lot-streaming research credited to **idavidveiga** [verified README/docs]
- **Activity:** created 2026-09-28, last push 2026-10-12 (release 2.12.2), 18 stars, issues on [verified]
- **License:** MIT [verified repo metadata]
- **What it is:** an ASI for lighting and visuals that also ships a "Performance" group of 15 hitch fixes and a Frame Profiler. Its docs follow a strict standard: `docs/engine/` is a reverse-engineering reference for `TS3W.exe`, and "Game addresses are for Steam 1.67.2 (image base 0x00400000) unless stated otherwise", which is **our exact build**. [verified]
- **Engine reference pages** [verified, file tree and page heads]: `main-loop-and-services.md`, `lot-loading-and-streaming.md`, `mono-gc.md`, `timers-and-sleeps.md`, `terrain-and-light-bake.md`, `room-light-maps.md`, `light-objects-and-rigs.md`, `shaders.md`, `camera-and-map-view.md`, `game-versions.md` (Steam fixed addresses plus a signature table for EA app 1.69.47).
  Examples from `main-loop-and-services.md`: main loop `0x00ECA960`, called from `0x00ECBD00` ("Services/Sims3App"); simulation thread entry `0x00D81840` ("Simulator Frame") with an explicit GC call at `0x00D819AA`; ServiceManager object at `[0x011CE3F4]`, `ServiceManager::Update 0x00588E00`, `AddService 0x0059F500`, a node layout with per-service main-loop and sim-loop flags, and "58 functions that register services"; inactive-window sleep at `0x00EC9FBA` (the bytes S3SS's limiter replaces); RefPack at `0x004EC010` (matches our docs/08). [verified, page text]
- **Performance switches that overlap our plans** [verified, feature pages]:
  - *Faster Game File Lookups* (`ResourceLookupCache`): remembers which package answered each resource and re-checks it, instead of probing ~290 packages. Same idea as our docs/08 H3 native lookup index.
  - *Faster File Lists* (`FileListCache`): caches per-type key lists of read-only packages (CAS, Sim load). Overlaps docs/21 CAS offload.
  - *Faster Object Lookups* (`ObjectLookupIndex`): index by object ID next to the object service's map, and cached lot lookups instead of walking the world tree.
  - *Faster Memory Handling* (`FastMemory`): spin before sleep on the shared allocator lock, and return big blocks to Windows in the background. Overlaps our alloc cache result (PLAN: +19% it/s).
  - *Faster Scripts* (`ScriptMath`, experimental): removes a library call on every Mono decimal comparison and a locked search on type-info lookups. Directly relevant to the script-thread cost in docs/04a.
  - *Room to Save* (`MemoryGuard`, experimental): reserves address space and frees it right before a save; trims the file cache when memory is tight. Error-12-on-save.
  - Also: room-light queue, lot lighting while moving, wall shading while moving, scene-node budget, fast DXT and RefPack encoders, fast CAS sort, remember missing files, lot streaming switches (off by default).
  - Their rules are worth copying as policy: bit-identical results, the first 16/64 calls also run the game's function and compare, and every hook site is byte-checked against the studied Steam 1.67.2 bytes before writing. [verified]
- **How it speeds us up:**
  1. Import their address names into our Ghidra project (`tools/ghidra`) as a "loinyx/Apex" name source: main loop, services, sim thread, GC sites. Cross-check against docs/04 before trusting. [inferred]
  2. Before building docs/08 H3 or more of docs/20 and docs/21, read `features/resource_cache.cpp` and `features/object_index.cpp`. If they already do it, measure Apex in the baseline table instead of writing our own.
  3. Use `mono-gc.md` against docs/13 (GC tuning). Their open questions (what increments `[0x011F2F98]`, whether `[0x011EE528]` can be 0) are ones our probe can answer.
  4. Their Frame Profiler is a second opinion on our `profile-world` numbers.
- **Reuse:** MIT, so code can be copied with the notice. Note that `sigcheck.pl` and the Ghidra research tools are private (`game-versions.md`), and a PR (#17) mentions a private Ghidra analysis of Steam TS3W. [verified page; PR from snippet]
- **Overlap:** high. It also detours the resource manager and allocator, so it may conflict with our ts3native hooks and S3SS: check overlapping bytes before running both (as docs/08 already says for S3MM).
- **Compatibility map (2026-10-24, v2.15.3 = HEAD 2026-10-23):** docs/187 indexes every game address their feature code names (`docs/data/apex-hooks.csv`, 386 sites, 96% cited in their `docs/`), per switch what it defers/caches/reorders and on which thread, and where it meets ts3native; use their `docs/architecture.md` §4.6/§10 and `features/performance/*.md` as the field-level reference for their hooks. MIT; we cite, never copy code. [verified]

## 2. Sims3MonoModder (sims3fiend)

- **URL:** https://github.com/sims3fiend/Sims3MonoModder [verified]. GPL-3.0, last push 2026-09-29, one contributor (sims3fiend). [verified]
- **Already in docs/06 and docs/08.** New angles only:
  - The **Profiler** (per-method calls, self and total time, allocation, boxing, icall tables; CSV and Chrome-trace export) answers docs/04a's "which script methods cost the most per tick" without us writing a managed profiler. [verified README]
  - **Assembly Diff** shows exactly which methods a core mod (simler90, NRaas) changed versus vanilla. Useful to check whether simler90 already fixes a routing retry loop before we patch it.
  - A **vectored handler can recover from a known recurring crash**: ask which crash, since it may be one in our ledger.
- **Reuse:** GPL-3.0; reference or GPL-only reuse. Use as a tool at runtime, do not ship it.

## 3. Sims 3 Settings Setter issue tracker (sims3fiend)

- **URL:** https://github.com/sims3fiend/Sims3SettingsSetter/issues [verified, 80 issues listed]. No license file ("none"). Contributors: sims3fiend (91 commits), **just-harry** (25), **swiffyjk** (10), **LazyDuchess** (7). [verified]
- **Issues worth reading before our next patches** [verified titles]: #43 "Experimental GC_try_to_collect() patch causes stuttering" (docs/13), #60 and #63 memory growth since 1.4.0, #65 "Game takes too long to save, super fast mode is very laggy", #57 random `ACCESS_VIOLATION`, #44 crash with CPU Thread Optimization, #46 SpecialK frame pacing, #33 WorldCache size cap, #72 headless patch-only mode for translated D3D9 (DXVK).
- **How it helps:** field reports from many machines about the same patches we run. Triage our crash ledger against them. If #72 shipped, our DXVK setup can use the patch-only mode.
- **Reuse:** reference only (no license); docs/03 already uses its anchors.
- **Update 2026-10-18** [verified tree via GitHub API]: `crates/s3formats/src/lot.rs` parses the per-lot wall/room graphs (resource `0x312E7545`: vertices, edges with room ids on each side, levels) plus roofs and lot terrain/water; `crates/game/src/nav.rs` is its own grid pathfinder (obstacles, barriers, stairs/elevator links, locked doors), not the game's. Reference for the static half of a routing snapshot (docs/91 study, inputs rows 1-2); still no license.

## 4. TS3-64bit-Patch / TS3 Memory Server (cmedina-dev)

- **URL:** https://github.com/cmedina-dev/TS3-64bit-Patch [verified]. GPL-3.0; created 2025-10-09, last push 2026-03-30; 19 stars; 4 issues (#1 hook on launch causes arena fault, #3 StackWalker crashes during hitches). [verified]
- **What it does:** a 64-bit server process plus an injected 32-bit DLL. Hooked `HeapAlloc` calls of 4 MB or more are sent over a named pipe to the server, which allocates in its own 64-bit address space and returns a handle; the game gets a 4-byte "proxy" pointer. Also ships `AdvancedHitchDetector`, `AllocationPatternDetector`, an `AsyncCacheWriter` and cache-integrity code. [verified README and file tree]
- **Our read:** a proxy pointer to 4 bytes cannot be read or written by the game as if it were 10 MB, so as described this can only work for allocations the game never touches directly, or through extra mapping not shown in the README. Treat the approach as unproven until we read `src/ts3_patch`. [inferred] The hitch detector and allocation-pattern code are useful ideas for our probe regardless.
- **Overlap:** docs/08 and docs/20 (resource offload out of process) are the same goal with a different, safer mechanism (shared memory views). Compare notes; do not adopt.
- **Reuse:** GPL-3.0; reference, or GPL-only reuse.

## 5. S3LotLoDThing (sims3fiend)

- **URL:** https://github.com/sims3fiend/S3LotLoDThing [verified]. Steam only, no license, last push 2025-02-28.
- **Content:** `0x011ECBC0` = the "Throttle Lot LoD Transitions" bool in the Streaming debug-variable category; `0x00C63015` = a `JZ` after `CALL 0x00AB1860` that the patch turns into a `JMP`. The author guesses frustum culling and says "it reduces stutter dramatically". The full Streaming category is editable in S3SS. [verified README]
- **How it helps:** two named Steam addresses for lot streaming (stutter priority), and a lead to check against Apex's lot-streaming page and our docs/04. Label them in Ghidra.
- **Reuse:** facts only (no license).

## 6. s3pi family: sims3-package-interface, s3pi-modern, Sims3Tools

- https://github.com/marcos4503/sims3-package-interface: GPL-3.0, archived copy of s3pi plus docs and a SimsWiki mirror; last push 2024-09-10. LazyDuchess has a fork. [verified]
- https://github.com/Destrospean/s3pi-modern: s3pi on modern .NET, GPL-3.0, 2026-07. Already the source of our S3SA decrypt port (`tools/extract-assemblies.ts`). [verified]
- https://sourceforge.net/projects/sims3tools/ (Peter L Jones, "pljones"): original s3pi, s3pe, s3oc; development finished. [snippet]
- https://github.com/anonhostpi/s3piClone (commit 4ce04ec6, GPL-3.0 header in the files, no repo license field): `s3pi Wrappers/CatalogResource/CatalogResource.cs` (`Material`, `MaterialBlock`, `ComplateList` type codes 1-7, TGI-index values) and `ObjectCatalogResource.cs` (OBJD field order: version, TGI offset/size, material list, instance name, common block, ...). Used 2026-10-26 for the OBJD design-preset layout in `tools/cc-gen.ts` `objdMaterials` (docs/200 §9): format knowledge only, our own code, checked against all 1,614 base-game OBJDs. Same files as in marcos4503/sims3-package-interface. [verified]
- **How it helps:** the reference for every resource wrapper (RCOL, GEOM, CASP, OBJD, world data). When `tools/dbpf.ts` needs a new resource type, port the parser from s3pi rather than reverse engineer it. Our tools are TypeScript and not GPL: port the format knowledge (layout, field order) and write our own code, and cite the source file, as `extract-assemblies.ts` already does. [inferred]

## 7. Sims3HookMods (ChrisNonyminus)

- **URL:** https://github.com/ChrisNonyminus/Sims3HookMods [verified]. MIT; a single push on 2024-03-02 (inactive).
- **What it does:** Steam-only proof-of-concept ASI that hooks the game's Mono runtime setup and registers custom internal calls (`MonoAddInternalCall("Namespace.Class::Func", fn)`), called from C# via `[DllImport("Sims3Common")]`; example `IO.File::CReadAllText`. It also loads further plugin DLLs that add internal calls. Files: `GameFuncs.cpp`, `Hooking/Hooks.cpp`. [verified README and tree]
- **How it helps:** if our probe or ts3native needs a direct script-to-native call (for example to expose a native routing cache to a script mod), this is the smallest working MIT example on our exact build. Compare its Mono setup hook address with our `tools/internal-calls-map.ts`.
- **Reuse:** MIT, copy with notice.

## 8. NRaas (Chain-Reaction)

- **URL:** https://github.com/Chain-Reaction/NRaas [verified]. No license file; last push 2026-07-29; 60 stars.
- Already used in docs/03 §5.3 and docs/07. Nothing new, except that it is still maintained, so a question to the NRaas community (nraas.net forums) about Overwatch's stuck-Sim reset thresholds can still get an answer.
- **Source clone (2026-10-24):** commit `2f57186` (2026-07-29) kept read-only and git-ignored under `game-copy/nraas-src`. **No license:** read and cite (repo path, class, method), never copy code into our repo; its `Sims3/` folder holds EA's script projects and ErrorTrap's modified `ScriptCore`: never read for docs, never copied or committed. Used for docs/184 (ErrorTrap fix catalog vs our findings, route plan contexts in NRaas mods, community-reported names for ledger claims, tooling ideas).

## 9. sims-3-bevy (Eamo5)

- **URL:** https://github.com/Eamo5/sims-3-bevy [verified]. Created 2026-10-08, very active (push 2026-10-12), no license.
- **What it is:** a standalone Rust/Bevy reimplementation that reads an installed copy of the game. A baker converts all worlds (base, EPs, WA destinations) to a GPU-ready cache: terrain paint and shadow maps, roads, water, placed objects, SpeedTree billboards. [verified README]
- **How it helps:** its decoders are a second, independent reading of world and terrain formats. Use it to check our parsers and as a reference for which resources a world load touches (docs/19 travel worlds). Not a source of engine behaviour (it re-implements gameplay).
- **Reuse:** reference only (no license).
- **Update 2026-10-24** [verified repo tree + files]: 732 commits in 16 days (commit messages credit an AI), 291 files, still no license. Most useful for us: `docs/formats/world.md` (`.world` resources with VERIFIED / WIKI / GUESS marks: roads `0x9063660E` laid out and verified on Sunset Valley, world routing graph `0x05CD4BB3` from the wiki, lot location + routing `0x046A7235`, WBND `0x05DA8AF6`, heightmap `0x2AD195F2`), `crates/s3formats/src/lot.rs` and `lotdesign.rs` (lot files: walls, floors, footprints; a second opinion for P5 lot-data checks), `docs/formats/gameplay.md` (tuning tables, careers, wishes, recipes, socials: story fuzzer content), `objects_rslt.md`, `sims.md`, `models.md`. Its pathfinding (`crates/game/src/nav.rs`) is its own grid search, not EA's planner: no help for the routing internals we patch.

## 10. SimsWiki and MTS wiki technical pages

- SimsWiki "TS3 Advanced Modding Reference": https://simswiki.info/wiki.php?title=Tutorials:TS3_Advanced_Coding_Tutorials [verified, page fetched]
- SimsWiki "Sims 3:PackedFileTypes" (resource type IDs): https://simswiki.info/wiki.php?title=Sims_3:PackedFileTypes [verified, page fetched]
- MTS wiki "Sims 3:Modding": https://modthesims.info/wiki.php?title=Sims_3:Modding [verified, page fetched]
- MTS wiki DBPF page: https://modthesims.info/wiki.php?title=DBPF [snippet]
- Rust `refpack` crate lists TS3 under the "SimEA" RefPack variant: https://docs.rs/refpack [snippet]; license not checked.
- **How it helps:** cite type IDs in `tools/dbpf.ts`; the RefPack variant note is a quick test for our RefPack reader. 2026-10-26 (docs/200 §6, `tools/cc-gen.ts --kind build`): the PackedFileTypes table names the Build catalogue types cc-gen clones: 0x515CA4CD CWAL "Catalog Wall/Floor Pattern", 0x0418FE2A CFEN fence, 0x04ED4BB2 CTPT terrain paint brush, 0x04C58103 CRAL railing, 0x049CA4CD CSTR stairs [verified, page fetched]; our base-game census confirms each (wall vs floor by description text, thumbnail families by instance match).
- **License:** wiki content; cite, do not copy pages.

---

## Added 2026-10-24 (web and GitHub search)

| Project / resource | URL | What | Activity / license | Use for us |
|---|---|---|---|---|
| OpenTS3 status update | https://opents3.com/ , Discord https://discord.gg/CCMmUbRtYb | Engine mod, now Vulkan + .NET 10; runs EA's C# gameplay DLLs and replaces TS3W.exe, so its route planner is new code; its roadmap lists "path queries, route splitting, obstacle handling and crowded town routes" as incomplete | private pre-alpha, source private (open source promised), no release date [verified site] | No code; ask on Discord how the routing graph was decoded; its script speed comes from modern .NET instead of Mono 1.2 (compare with our Mono plan) |
| Sims3-MCP (Critical-Reynolds) | https://github.com/Critical-Reynolds/Sims3-MCP | MCP server + script mod that lets an AI play the game through a shared-memory mailbox (patch 1.67 / 1.69) | created 2026-10-07 [verified] | Same idea as our ts3agent mailbox; compare handlers if we extend ours |
| RadioLeveler (thesammy58) | https://github.com/thesammy58/RadioLeveler | ASI mod: per-song loudness leveling for the radio; README says research, RE and code were produced with Claude | created 2026-10-20 [verified] | Small AI-built ASI example |
| sims3-legacy-content-repair (murlock1000) | https://github.com/murlock1000/sims3-legacy-content-repair | Repairs old Store `.sims3pack` headers so legacy content installs again; documented evidence + synthetic tests | created 2026-10-17 [verified] | Sims3Pack header handling (next to our `tools/install-lot.ts`) |
| Android / Switch / DS ports | github Lucaslllll/the-sims-ambitions, OlhaGatito/The-Sims-3-Android-port, barbaruiva25/TS3NX, luispolis124/OpenTS3-DS | Ports of other editions (mobile, Xbox 360 recompiled for Switch, DS decompilation) | 2026-09/10 | Not the PC game; ignore |
| Game's own Sims3Pack installer | `.sims3pack` file association on a Steam install: `"...\Game\Bin\Sims3Launcher.exe" -file:"%1"` | EA's single-file install path (double-click) | [verified registry on this machine] | Launcher-free lot install candidate (untested); fallback `tools/install-lot.ts` (docs/181 §6) |
| Delphy's Sims3Pack Multi Installer, S3P Extractor | MTS / community | Unpack Sims3Packs into `.package` files for Mods; lots come out as loose packages, not installed lots | community tools [reported] | CC only, not a lot installer |
| ModTheSims lots for story runs | https://modthesims.info/d/354795/large-house-for-8-sims.html (in `Story-BP-Mansion`), https://modthesims.info/d/560390/ (Breezy Getaway: sloped lot, placement height problems) | CC-free furnished family homes | MTS uploads [verified pages] | Story run saves (docs/181) |
| Lot install threads | https://forums.ea.com/discussions/the-sims-3-en/changing-sims3packs-to-packages/369285 , https://modthesims.info/t/498648 | Lots need the launcher; worlds can go into `Worlds` as `.world` | forum [reported] | Background for `tools/install-lot.ts` |

## Added 2026-10-25 (memory leak prior art, docs/191)

| Project / resource | URL | What | Activity / license | Use for us |
|---|---|---|---|---|
| TS3VASManager (ZanderFrieze) | https://github.com/ZanderFrieze/TS3VASManager | Fork of TS3-64bit-Patch: Detours on `RtlAllocateHeap`/`NtAllocateVirtualMemory`/`NtMapViewOfSection`, large allocations into a 1 GB in-process arena, VAS heartbeat, hitch detector, ETW provider, managed companion `TS3VASMonoGc.cs` that runs `GC.Collect()` + `WaitForPendingFinalizers()` on native VAS pressure; README: large blocks "recycled cleanly, zero leaks inside the proxy" | GPL-3.0, created 2026-06-09, pushed 2026-06-12, 0 stars [verified API + README] | docs/191 steps 5-6: finalizer-backed native memory hypothesis; read, do not copy (GPL) |
| Apex Radiance memory docs | https://github.com/loinyx/Sims3-ApexRadiance/blob/HEAD/docs/features/performance/room-to-save.md , `docs/engine/native-allocation-and-script-memory.md`, `docs/engine/native-memory-cache.md`, `docs/features/memory-budget.md`, `docs/features/memory-analysis.md` | Room to Save: 128 MB reserve freed before saves, game's own ResourceSystem cache shrink (`0x732E90`, `0x733E70`, caches at `+0x1E0`/`+0x1E4`) under 320 MB largest free; allocator trim `0x4E4F90` returned 0 bytes; Mono heap counters `0x12225A0`/`0x12225B4`; memory analysis and external snapshot tool (game attribution unverified) | MIT, docs read 2026-10-25 [verified raw files] | docs/190 `rc`/`gc` buckets, docs/191 step 7; cache caps stay their area |
| Apex Radiance issues #6, #28 | https://github.com/loinyx/Sims3-ApexRadiance/issues/6 , /issues/28 | #6: ~200 MB more with Apex (lighting data; author points to save safeguards); #28: 2.14 memory work held, "RAM reduction ... Error 12 prevention ... not established" | MIT [verified] | Their memory gains are unmeasured; do not cite as fixes |
| S3SS memory issues #42, #60, #63, #36, #10 | https://github.com/sims3fiend/Sims3SettingsSetter/issues/63 (and /42, /60, /36, /10) | #63/#60: "+1 GB since 1.4.0" was the new accurate monitor (maintainer); #42 fixed by redoing GraphicsRules + DXVK; #36 background thread memory patch; #10 64 KB alloc per float | MIT repo [verified API] | No leak in S3SS; vmmap recommended to validate (same as our walk) |
| Catalogue Lag and Memory Leak Fix (Sonja92yu) | https://www.nexusmods.com/thesims3/mods/242 | ~80 hidden UI windows per catalogue (Build/Buy, CAS, Create-a-Style); reopening grows memory by hundreds of MB; LAYO-only fix, EP badges need MonoPatcher file | Nexus, Aug 2026 [snippet; page 403 to scripts] | docs/191 step 3 catalogue open/close repro (already in docs/07 §2.3) |
| S3PP review (simscommunity.info) | https://simscommunity.info/2026/07/16/sims-3-performance-patch-mod-review/ | Quotes the S3PP feature text (Error 12 = VAS, not Task Manager RAM); reviewer: slowdown after 30-40 min, Build mode toggle clears it for a while | review 2026-07-16 [verified page] | docs/191 step 3 Build-mode marker; S3PP STBL claim (~290 MB) is in its own ini (docs/07 §1.6) |
| EA forum Error 12 threads | https://forums.ea.com/discussions/the-sims-3-technical-issues-en/sims-3-error-code-12-help-/13404703 , https://forums.ea.com/discussions/the-sims-3-technical-issues-en/error-code-12-stopping-game-from-saving/8074014 , https://forums.ea.com/discussions/the-sims-3-technical-issues-en/sims-3-crashes-when-traveling/11172186 | Moderators: memory only rises in a session, travel keeps part of the old world, save spike, keep under ~3.4 GB, save + restart after travel | forum [snippet, anecdote] | docs/191 steps 3-4 test these claims |
| EA Answers, Mac 64-bit memory leak | https://answers.ea.com/t5/Technical-Issues-Mac/CURRENT-ISSUE-64-bit-TS3-64-bit-memory-leak/m-p/9679265 | 64-bit Mac client grows until system memory limits (routing loop example) | forum [snippet; 403 to scripts] | Growth exists without the 4 GB cap [inferred] |
| Memories / save bloat threads | https://forums.ea.com/discussions/the-sims-3-en/what-exactly-does-disable-memories-mean/571402 | Disabling memories stops new ones only; MasterController wipes them; memories bloat saves and slow play | forum [snippet, anecdote] | docs/191 step 6 class counts |

## Added 2026-10-26 (Boehm heap experiments, docs/199)

| Project / resource | URL | What | Activity / license | Use for us |
|---|---|---|---|---|
| Cork (Jump, McKinley), "Detecting Memory Leaks in Managed Languages with Cork", Software: Practice and Experience 2009 (extends POPL 2007 "Cork: Dynamic Memory Leak Detection for Garbage-Collected Languages") | https://www.cs.utexas.edu/~mckinley/papers/cork-spe-2009.pdf | Per-type live-heap summary at each full GC piggybacked on the collector, type points-from graph, ratio ranking (decay factor f = 15%, rank threshold 100, >= 2 growth phases), slices back to the data structure | paper (author copy); idea and algorithm only, no code | docs/199 `[MemDiag] TypeGrowth`: ratio ranking as published, census by a separate walk over the marked blocks instead of a mark-phase hook |
| MemBalancer (Kirisame, Shenoy, Panchekha), "Optimal Heap Limits for Reducing Browser Memory Use", OOPSLA 2022 | https://arxiv.org/pdf/2204.10455 | Square-root heap limit M = L + sqrt(L g / (c s)) (live, allocation rate, GC speed, tuning c); g smoothed 0.95 per 1 s heartbeat, s smoothed 0.5, numerator and denominator separately; 2 MB minimum extra, V8 nursery term | arXiv preprint (OOPSLA 2022, ACM); license not checked; formula only, no code | docs/199 `[GC] SqrtHeapLimit`: limit per completed Boehm cycle drives GC_collect_or_expand growth |

## Other projects found (lower value)

| Project | URL | What | Activity / license | Use |
|---|---|---|---|---|
| OpenTS3 (JJ, JJ's Game Lab) | https://opents3.com/ | 64-bit .NET 8 engine replacement that runs EA's 9 managed gameplay DLLs on a new native layer; reports a decoded routing graph (34,471 vertices, 135,657 edges, Sunset Valley) | private pre-alpha, repo private, GPLv3 announced, no release date [verified site 2026-10-18] | Ask (Discord) how the routing graph was decoded; no code to reuse yet |
| Gibbed.Sims3 (Rick "gibbed") | https://github.com/gibbed/Gibbed.Sims3 | 2009 tools: DBPF, scenegraph, rig, STBL readers; `ReadExecutable/ParseScriptFunctionTables.cs` walks the exe's native script function tables (`{native, name}` pairs, keyed by exe MD5) | Single push 2017-07; zlib-like license with a **non-commercial** clause [verified] | Confirms our internal-call map method; table addresses are for 2009 exes, not 1.67 |
| sims3-rs (kitlith) | https://github.com/kitlith/sims3-rs | Rust DBPF tools: `find_merged_cc`, `geom_tri_count`, `dump_package_pngs` | MIT, push 2025-10 [verified] | `geom_tri_count` is a ready check for heavy CC meshes (docs/17) |
| Tatsh/sims3-tools | https://github.com/Tatsh/sims3-tools | Python 3 utilities | MIT, 2013 [verified metadata] | Reference only |
| Regul Save Cleaner (Onebeld) | https://github.com/Onebeld/RegulSaveCleaner | Save cleaner on s3pi | MIT, push 2025-11 [verified metadata] | Already in docs/09; MIT means we can borrow its cleaning rules |
| Tiny UI Fix (just-harry) | https://github.com/just-harry/tiny-ui-fix-for-ts3 | UI scaling generator (PowerShell) | No license, push 2026-03 [verified] | Author is also the 2nd-largest S3SS contributor |
| LazyDuchess repos | https://github.com/LazyDuchess | New since docs/03: `LotPopulation` (populates community lots), `TS3PatchLauncher`, `TS3BurglarHate`; fork of S3SS | No licenses [verified] | Nothing stutter-related beyond docs/03 |
| SimsOnline (JurjenBouma) | https://github.com/JurjenBouma/SimsOnline | "Proof of concept sims 3 online multiplayer mod" | README only, 2021-01, no license [verified] | No content; no other TS3 multiplayer RE found [snippet searches] |
| Sims3ModLoader (JurjenBouma) | https://github.com/JurjenBouma/Sims3ModLoader | Launcher and package (un)packer | 2021-01 [verified metadata] | None |
| TS3-Revamp (VMPYRC) | https://github.com/VMPYRC/TS3-Revamp | Tuning/XML edits to `GameplayData.package` | 2026-03, no license [verified] | Shows which tuning files a "fixes" package touches |
| Kuree/Sims4Tools | https://github.com/Kuree/Sims4Tools | s4pi (s3pi fork for TS4) and DBPF wiki | Deprecated 2016 [verified metadata] | DBPF 2.x docs |
| OpenTS3-DS, TheSims3Mobile | github luispolis124/OpenTS3-DS, WanKerr/TheSims3Mobile | DS and Windows Phone decompilations | [snippet / verified metadata] | Different games; not useful |
| TheSims3WorldToolBuilder "leaked source" | github Triomonitor/TheSims3WorldToolBuilder | Claims leaked EA source | [verified title only] | **Do not use or open**: leaked EA code is a legal risk to the project |
| Sims 3 Performance Patch (Sonja92yu) | https://www.nexusmods.com/thesims3/mods/235 | Already in docs/01, 07, 20 | Nexus returned HTTP 403 to scripts today | No GitHub account found under that name [verified search] |

Not found: a public Ghidra/IDA database or symbol map for TS3W.exe (same as docs/03 §8), a Simlogical/Pescado routing write-up, and any public Discord with a technical archive (searches returned only generic SEO pages) [snippet searches].

---

## EA terms and mod legal history (checked 2026-10-25, not legal advice)

| Item | URL | What it says or what happened | Type |
|---|---|---|---|
| EA User Agreement §2 License | https://www.ea.com/legal/user-agreement | Personal, non-commercial, revocable licence; no copying, modifying, distributing or reverse engineering "unless expressly authorized by EA or permitted by law". No clause about "revival" games or server emulation [verified page text] | ToS clause |
| EA User Agreement §7 | same | Tampering with anti-piracy measures ends the licence; "Unauthorized Third-Party Programs" (mods that bypass or modify the game in ways EA did not authorize) can lead to enforcement, aimed at cheating in online games [verified] | ToS clause |
| The Sims 4 Policy on Mods | https://help.ea.com/en/articles/the-sims/the-sims-4/mods-policy/ | Mods allowed if free (time-limited early access, donations and ads on the creator's own site allowed); no EA/Maxis logos, Plumbob or key art; no implied endorsement; EA may act on mods that infringe, disrupt or break the User Agreement. No Sims 3 page found [verified] | Policy |
| FreeSO / Simitone (The Sims Online, The Sims 1 engines) | https://simscommunity.info/2017/08/07/the-sims-1-for-mobile-fanmade-project-has-been-cancelled/ | 2017: EA contacted the team; they changed the logo, added disclaimers, took no donations; EA then asked them to stop the mobile port. The PC engine (loads the player's own game files) was allowed to continue [verified, team's statement] | Letter, no lawsuit |
| Revive Network (Battlefield 2/2142/Heroes) | https://www.pcgamer.com/uk/ea-tells-battlefield-revive-team-to-stop-distributing-copies-of-our-game-clients | 2017: EA asked them to stop distributing copies of EA game clients and using EA trademarks/artwork; project closed [verified] | Letter, no lawsuit |
| OpenSC2K (SimCity 2000 remake) | https://github.com/github/dmca/blob/master/2018/2018-07-25-EA-2.md | 2018 GitHub DMCA: the repo used SimCity 2000 art assets [verified notice] | DMCA |
| The Sims 2 Ultimate Collection uploads | github/dmca 2025-02-10 notice (mirror seen, original path not confirmed) | 2025: repositories redistributing the full game files [snippet] | DMCA |
| Command & Conquer source release | https://www.videogameschronicle.com/news/ea-says-it-will-release-the-command-conquer-source-code-in-a-key-moment-for-the-industry | 2025: EA published C&C engine source under GPLv3 and added Steam Workshop support; assets stay EA's [snippet] | EA support |

Pattern [inferred from the cases above]: EA acted on redistribution of its game files or art, its trademarks and logos, ports to new platforms, and online service revivals; we found no lawsuit and no action against engine-hook or patch mods that ship no EA files (S3SS, Smooth Patch, NRaas, S3PP all public for years). Legal frame: US 17 U.S.C. 1201(f) (https://www.law.cornell.edu/uscode/text/17/1201) allows circumvention for interoperability only; EU Directive 2009/24/EC art. 5(1) (error correction by a lawful user) and art. 6 (decompilation for interoperability), with art. 8 voiding contract terms against art. 6 (https://eur-lex.europa.eu/eli/dir/2009/24/oj). US courts have enforced EULA reverse-engineering bans in some cases (Blizzard v. BnetD, server emulation, 2005).

---

## Outreach: who to invite to the crowdsourced repo, and what to ask

Use only each person's public channel (GitHub issue or discussion on their own repo, their MTS profile, or the channel their README names). One short, specific message each; offer something back (our verified addresses, measurements, crash triage). No mass messages, and nothing that asks them to share private research they chose to keep private.

| Who | Channel | Ask | Offer |
|---|---|---|---|
| **loinyx** (Apex Radiance) and **idavidveiga** | Issue on Sims3-ApexRadiance ("Reporting a problem" section names it) | Would they cross-link engine notes? Specifically: do ResourceLookupCache and FastMemory conflict with other resource-manager and allocator hooks, and which byte ranges they patch, so tools can coexist | Our verified routing and route-planner addresses (docs/11, 18), our measurement harness results for their switches |
| **sims3fiend** (S3SS, S3MM) | GitHub issue on S3SS or S3MM | Whether S3SS could expose a list of patched byte ranges for conflict checks; status of #72 patch-only mode; which recurring crash S3MM's vectored handler recovers from | Our crash ledger matches against their open issues (#57, #65) |
| **LazyDuchess** | Her GitHub repos (issues) | Whether MonoPatcher's Mono signatures have Steam 1.67 equivalents she already knows | Our Steam addresses for the same Mono functions |
| **cmedina-dev** | Issue on TS3-64bit-Patch | How the proxy pointer is made usable by the game (mapping, or only for untouched blocks?), and hitch-detector findings | Our docs/08 offload design and shared-memory approach |
| **just-harry**, **swiffyjk** (S3SS contributors) | GitHub | Interest in reviewing native patches | Credit in the repo |
| **NRaas community** | nraas.net forums | Overwatch stuck-Sim reset thresholds, and known bad routing spots per world | Our stuck-Sim repro list (repros.json) |
| **ChrisNonyminus** | GitHub | Permission is already given (MIT); just credit and link | Credit |

Before any outreach: Rodney decides who and sends the messages himself. Our repo is local-only (CLAUDE.md), so "invite to the crowdsourced repo" needs a publishable subset first (notes and our own patch source, never EA files).

---

## Sources

All GitHub facts come from the GitHub REST API and raw READMEs/docs read on 2026-10-13: repos listed above, `loinyx/Sims3-ApexRadiance` `docs/README.md`, `docs/engine/main-loop-and-services.md`, `docs/engine/mono-gc.md`, `docs/engine/game-versions.md`, `docs/features/performance/*.md`; `sims3fiend/Sims3SettingsSetter` issues and contributors; `cmedina-dev/TS3-64bit-Patch` README, tree, commits, issues; `sims3fiend/S3LotLoDThing` README; `ChrisNonyminus/Sims3HookMods` README and tree; `gibbed/Gibbed.Sims3` tree, license and `ParseScriptFunctionTables.cs`; `Eamo5/sims-3-bevy` README. Web pages fetched: SimsWiki and MTS wiki pages listed in section 10. Search summaries (marked [snippet]): GitHub topic and PR pages, sourceforge sims3tools, docs.rs refpack, Nexus 235 review.

## Added 2026-10-27 (Apex Radiance dev tools, re-check for the "most complete harness" claim)

| Project | URL | What it has | License / checked | Use here |
|---|---|---|---|---|
| Apex Radiance developer tools | https://github.com/loinyx/Sims3-ApexRadiance (`docs/features/frame-profiler.md`, `docs/features/dev-tools/tester.md`, `recorder.md`, `census.md`, `docs/features/memory-analysis.md`, `tools/memory_snapshot/`) | In-game developer mode: Frame Profiler (~40 timed game functions per frame, hitch composition, optional sampler, `ApexRadiance_Hitches.txt`), Tester tab (live on/off of lighting parts, same-scene compare), lighting Recorder (20 s timeline), shader Census, memory analysis CSV + address-space owner ledger (dev builds), external memory snapshot tool with self-tests, ~58 offline test programs (shader/lighting checks), per-feature validation docs | MIT, pushed 2026-10-26, 24 stars [verified tree + docs read] | Strongest public in-game dev tooling for render/lighting/memory. No unattended in-game test runner of its own (our runner PR #33 still open), no seeded A/B queues, no sim-thread routing / script / GC instrumentation. Claim we can make: only public automated in-game harness (seeded repros, on/off arms, real-play pairs, fail-fast) with sim-thread routing and Mono GC instrumentation; not "most complete overall" |

## Added 2026-10-29 (thread placement and busy-wait fixes in other games)

| Project | URL | What it has | License / checked | Use here |
|---|---|---|---|---|
| Mjolnir_IV (llamacomma) | https://github.com/llamacomma/Mjolnir_IV | GTA IV ASI: NOPs a `SetThreadAffinityMask(thread, 1)` main-thread pin and injects `SwitchToThread()` into an engine `cmp`/`je` busy-wait through a code cave; README reports core 0 load 72% -> 29% | MIT; binary release, no source for the patch code [verified repo + README, GitHub API 2026-10-29] | Idea only: docs/203 checked TS3W.exe for both patterns (no pin, no EA busy-wait; the spins on our install come from S3SS and our own route waits) |

## Added 2026-11-02 (hybrid-CPU start-up failure, EA laptop)

| Project | URL | What it has | License / checked | Use here |
|---|---|---|---|---|
| IntelFix + AlderLakePatcher (LazyDuchess) | https://github.com/LazyDuchess/IntelFix, https://github.com/LazyDuchess/AlderLakePatcher, https://modthesims.info/d/667734 | `dllmain.cpp`: at `DLL_PROCESS_ATTACH` `SetProcessAffinityMask(process, 1)` (one logical CPU), a thread restores the old mask after `Sleep(5000)`; the patcher adds `IntelFix.dll` to the exe's import table (TS3W.exe 1.67, TS3.exe 1.69, TSM.exe). Release 1.0.1 (2022-05-30) | No licence file (all rights reserved; read only, never copy) [verified GitHub API 2026-11-02] | The whole "Alder Lake fix" is a 5 s single-core window at start-up; S3SS CPU Thread Optimization does the same with P-cores (docs/03). Our pin.ts `ea-laptop` profile is a persistent affinity instead |
| EA Answers HQ thread "Sims 3 won't open (Alder Lake Intel 12th gen CPU)" | https://answers.ea.com/t5/Technical-Issues-PC/Sims-3-won-t-open-Alder-Lake-Intel-12th-gen-CPU/m-p/11061002 (Dec 2021), https://www.pcgamesn.com/the-sims-3/update-2025, https://www.techspot.com/community/topics/intel-reveals-the-games-that-dont-work-with-alder-lake-and-a-temporary-fix.272062/ | Symptom: no game window / crash at launch (`0xc0000094`); workaround: Task Manager "Set affinity" to 1 CPU for the first seconds, or a PowerShell script; Intel: the DRM layer misreads E-cores as a second system; EA's 2025-01-13 patch fixes the EA app build only, Steam and disc still need the workaround | Forum posts / news, read via search summaries [snippet, 2026-11-02; forums.ea.com returns 403 to fetchers] | Confirms the fault is the DRM start-up check on the thread/core enumeration, not the game's core count; the EA app 1.69.47 on the EA laptop started 3x with all 14 CPUs (docs/230) |

## Added 2026-11-02 (1.69 builds of the stamped core mods, EA laptop; docs/233)

| Project | URL | What it has | License / checked | Use here |
|---|---|---|---|---|
| simler90's Gameplay Core Mod UPDATE242, EA app build | https://modthesims.info/d/659969 (file https://modthesims.info/getfile.php?file=2320213, `simler90GameplayCoreMod-UPDATE242-EAapp.7z`, 9,087,348 B, sha256 `7a34c24c...94bfd`) | Core mod (GameplaySystems/GameplayObjects + one more assembly) in four platform builds (Retail, Steam, EAapp, Mac) plus modder source; the EAapp build's three S3SA stamps read `1.0.0.18` (Steam: `0.0.0.32`) | Author policy on the page: modify / alternative versions allowed on non-pay websites [verified page 2026-11-02] | Installed on the EA laptop in place of the Steam build (docs/233); stamps checked with the docs/231 method |
| NRaas ErrorTrap "EA App version (1.69)" | https://www.nraas.net/community/ErrorTrap (file `download/NRaas_ErrorTrap_P169_V100.zip`, 413,774 B, sha256 `a48dc4af...bdad`; nraas.net redirected it to https://www.dropbox.com/s/roh6ii1q4uhep64/NRaas_ErrorTrap_P169_V100.zip?dl=1 until 2022, Wayback CDX) | ScriptCore replacement stamped `0.2.0.32` (2016 build) + ErrorTrap v1 S3SA; also `P170_V100` ("64 bit Mac version (1.70)") and `P167_V100_Steam` | No licence text; nraas.net sits behind Cloudflare (403 to curl/WebFetch) and posted "A note regarding automatic downloads or the creation of mod updaters" (2026-03-01, topic15303, not readable to us): fetch by hand, never automate [verified page via Wayback 2026-08-12] | Not installed: stamp differs from the 1.69.47 exe's `1.0.0.18` (docs/233); re-check if a browser download of the current zip has another sha256 |

## Tooling installed on the EA laptop (2026-11-02)
- **Ghidra 12.1.4 PUBLIC** (https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.1.4_build, `ghidra_12.1.4_PUBLIC_20260921.zip`, sha256 `ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db`, matches the release page digest). Licence Apache-2.0. Use: headless static analysis of TS3W.exe via `tools/ghidra/run-headless.sh`, unpacked to `~/tools/ghidra_12.1.4_PUBLIC`. [verified: headless import smoke test passed]
- **Eclipse Temurin OpenJDK 21.0.12.1+1 LTS** (https://adoptium.net, already at `~/.jabba/jdk/openjdk@1.21`, not re-downloaded, no zip sha256). Licence GPLv2 with Classpath Exception. Use: JVM for Ghidra 12 (needs 21+).

## Added 2026-11-05 (D3D9On12 test, queue d3d9on12-ab)
- **ForceD3D9On12 (narzoul)**: https://github.com/narzoul/ForceD3D9On12, licence 0BSD, has x86 and x64 builds. DLL wrappers for DirectX 1-9 that disable the GPU vendor user-mode D3D9 driver so the runtime falls back to Microsoft D3D9On12. Not used: no log of the path taken and it changes the adapter query, not the API call. No Windows registry or system setting that forces D3D9On12 was found (web search 2026-11-05, standard mode, so unverified beyond that). Our own explicit proxy: tools/d3d9on12-test (Direct3DCreate9On12 with Enable9On12=TRUE; the x86 system d3d9.dll on this Windows build exports Direct3DCreate9On12 and Direct3DCreate9On12Ex, checked). Related: Microsoft D3D9On12 mirrors https://github.com/microsoft/D3D9On12 (custom builds need WinPixEventRuntime.dll and dxbcsigner.dll). docs/03 line 238 says no TS3 reports exist.

## Added 2026-11-05 (compat matrix: MTS top-100 Game Mods, issue #165 scans)
- **Verdicts in `docs/data/compat-matrix.csv` are static code overlap (compat-scan), not in-game tests.** Rows tagged `dalexlol-#165 scan 2026-11-02` come from Dalexlol's scans in issue #165 (his Mods folder, run with his compat-scan; reported, not re-run by us). Rows tagged `static scan 2026-11-05, MTS <id>` are ours.
- Source: https://modthesims.info/downloads/ts3/414/page/1/?csort=7 (Most Downloaded, pages 1-5), fetched through the MTS helper in Rodney's own Chrome. Files kept under `game-copy/compat-mods/` (ignored), analysed statically only. License: none stated on the listing; MTS uploads default to creator terms, so reference and static analysis only, no redistribution (reported, not verified per page). Version = the title or the listing date shown below.

- Compat job `queues/compat-top.json` uses Moar Interactions (row 13, MTS 437344, Buzzler, updated 2015-01-26, no license stated: behaviour read only, `Buzz_MoarInteractions.zip` sha256 e8d26cfc..., copied from `game-copy/compat-mods/` to the manual downloads folder). No new downloads for that job [verified 2026-11-05].

| rank | MTS id | URL | title / version | creator | listed date |
|---|---|---|---|---|---|
| 1 | 579548 | https://modthesims.info/d/579548/no-stretch-children-can-series.html | 'No Stretch' Children can Series | kapaer | 6th Jul 2016 at 8:01am |
| 2 | 438706 | https://modthesims.info/d/438706/pose-player-updated-8-22-13.html | Pose Player Updated 8-22-13 | cmomoney | updated 15th Jan 2015 at 3:05am |
| 3 | 444692 | https://modthesims.info/d/444692/no-mosaic-patch-1-22-to-1-67-edited-01-16-2014.html | No Mosaic Patch 1.22 to 1.67 edited 01/16/2014 | velocitygrass | updated 18th Oct 2013 at 6:48am |
| 4 | 402950 | https://modthesims.info/d/402950/weapons-system.html | Weapons System | desecrate | updated 11th Sep 2010 at 3:59am |
| 5 | 434270 | https://modthesims.info/d/434270/xcas-core-mod-more-tattoo-locations-edit-naked-outfit-slider-hack-body-hair-more-update-5-23-16-steam-version-added.html | XCAS core mod: more tattoo locations, edit naked outfit, slider hack, body hair, more - update 5/23/16 Steam version added. | CmarNYC | updated 23rd May 2016 at 11:42pm |
| 6 | 485285 | https://modthesims.info/d/485285/pregnancy-progress-controller-new-version-10-31-2013.html | Pregnancy Progress Controller - new version 10/31/2013 | CmarNYC | updated 31st Oct 2013 at 11:56pm |
| 7 | 480744 | https://modthesims.info/d/480744/cigarette-smoking-mod.html | Cigarette/Smoking Mod | cmomoney | 8th Jul 2012 at 4:20am |
| 8 | 342922 | https://modthesims.info/d/342922/no-intro.html | No Intro | Rick | 3rd Jun 2009 at 8:16am |
| 9 | 383662 | https://modthesims.info/d/383662/book-of-talent-for-ts3-compatible-with-1-67.html | Book of Talent for TS3 *Compatible with 1.67* | treeag | 26th Dec 2009 at 7:54pm |
| 10 | 343951 | https://modthesims.info/d/343951/no-mosaic.html | No Mosaic | Rick | 6th Jun 2009 at 7:37pm |
| 11 | 379117 | https://modthesims.info/d/379117/no-mosaic-updated-for-1-17-2-12-3-8-4-5-5-2-6-0.html | No Mosaic updated for 1.17/2.12/3.8/4.5/5.2/6.0 | Rothchild | updated 27th Oct 2010 at 7:59pm |
| 12 | 492102 | https://modthesims.info/d/492102/retuned-attraction-system-1-67-compatible.html | Retuned Attraction System (1.67 compatible) | Fentonparkninja | updated 17th Oct 2013 at 3:56pm |
| 13 | 437344 | https://modthesims.info/d/437344/moar-interactions.html | Moar Interactions | Buzzler | updated 26th Jan 2015 at 12:18am |
| 14 | 659969 | https://modthesims.info/d/659969/simler90-s-gameplay-core-mod.html | simler90's Gameplay Core Mod | simler90 | updated 20th Sep 2026 at 8:32pm |
| 15 | 441682 | https://modthesims.info/d/441682/animation-player.html | Animation Player | cmomoney | 4th May 2011 at 5:12am |
| 16 | 526207 | https://modthesims.info/d/526207/more-traits-for-all-ages.html | More Traits for All Ages | mikey | 4th Jun 2014 at 1:26pm |
| 17 | 486247 | https://modthesims.info/d/486247/faster-slower-and-or-hidden-skills-updated-for-into-the-future-1-67-update-19-7-14.html | Faster/Slower and/or Hidden Skills! (Updated for Into The Future/1.67) - UPDATE: 19/7/14 | gesimz | updated 24th Jun 2013 at 9:57pm |
| 18 | 343046 | https://modthesims.info/d/343046/hidden-traits-unlocked-updated-10-25-2013.html | Hidden Traits Unlocked *UPDATED 10/25/2013* | treeag | updated 15th Mar 2010 at 11:04am |
| 19 | 523924 | https://modthesims.info/d/523924/the-sims-3-violence-and-aggression.html | The Sims 3 Violence And Aggression | Dramatic-Gamer | 20th Apr 2014 at 8:21pm |
| 20 | 671632 | https://modthesims.info/d/671632/more-baby-interactions.html | More Baby Interactions | TheSweetSimmer | updated 21st Mar 2026 at 5:50am |
| 21 | 524468 | https://modthesims.info/d/524468/the-sims-3-passion-and-romance.html | The Sims 3 Passion And Romance | Dramatic-Gamer | 3rd May 2014 at 1:28pm |
| 22 | 372599 | https://modthesims.info/d/372599/dexter-the-bear-updated-for-patch-1-50-1-55-1-63-1-67.html | Dexter the bear - updated for patch 1.50 / 1.55 / 1.63 /1.67 | ani_ | updated 24th Feb 2013 at 3:58pm |
| 23 | 497903 | https://modthesims.info/d/497903/no-intro-with-maxis-logo.html | No Intro with Maxis logo | ellacharmed | 7th Mar 2013 at 12:17am |
| 24 | 471164 | https://modthesims.info/d/471164/add-any-lot-size-edited-01-16-2014.html | Add Any Lot Size (edited 01/16/2014) | velocitygrass | updated 7th Jul 2013 at 3:18pm |
| 25 | 616566 | https://modthesims.info/d/616566/children-can-care-for-their-lil-siblings.html | Children can care for their lil' siblings | TheSweetSimmer | 19th Aug 2018 at 9:20pm |
| 26 | 608185 | https://modthesims.info/d/608185/enhanced-witches-update-16-11-23.html | Enhanced Witches (Update 16/11/23) | PuddingFace | updated 16th Nov 2023 at 4:42pm |
| 27 | 677256 | https://modthesims.info/d/677256/more-toddler-interactions-v3.html | More Toddler Interactions V3 | TheSweetSimmer | updated 15th Mar 2024 at 6:15am |
| 28 | 427297 | https://modthesims.info/d/427297/05-12-jenesis-all-buyable-fish-books-fruits-mods-compatible-with-patch-1-63-1-67.html | [05/12] Jenesis' All Buyable Fish/Books/Fruits Mods (Compatible with Patch 1.63-1.67) | anak_ponti | updated 20th Oct 2011 at 3:40am |
| 29 | 455990 | https://modthesims.info/d/455990/sims-3-online-pharmacy-control-script-pills-and-cabinets.html | Sims 3 online pharmacy control script, pills, and cabinets | lenglel | updated 24th Feb 2013 at 3:54pm |
| 30 | 681572 | https://modthesims.info/d/681572/more-pregnancy-interactions-v2.html | More Pregnancy Interactions V2 | TheSweetSimmer | updated 9th Apr 2024 at 6:54am |
| 31 | 482543 | https://modthesims.info/d/482543/realistically-slower-cars-taxis-and-bikes.html | Realistically Slower Cars, Taxis, and Bikes | JSimmer | 6th Aug 2012 at 2:40am |
| 32 | 353351 | https://modthesims.info/d/353351/cooking-and-ingredients-overhaul-plants-only-version-13-12-11-updated.html | Cooking And Ingredients Overhaul!!! + Plants Only Version [13/12/11 - Updated] | BlackCat007 | updated 4th Dec 2010 at 8:40pm |
| 33 | 383093 | https://modthesims.info/d/383093/treasure-box-compatible-with-1-67.html | Treasure Box *Compatible with 1.67* | treeag | 24th Dec 2009 at 11:54pm |
| 34 | 529758 | https://modthesims.info/d/529758/taxi-charge-mod-updated-oct-16.html | Taxi Charge Mod (Updated Oct 16) | Sleepy-Genius | updated 16th Oct 2015 at 10:27pm |
| 35 | 670722 | https://modthesims.info/d/670722/random-sim-fixes-2-0.html | Random Sim Fixes 2.0 | LazyDuchess | updated 23rd Apr 2023 at 4:14am |
| 36 | 351416 | https://modthesims.info/d/351416/writing-painting-gardening-tinkering-more-fun.html | Writing, Painting, Gardening, Tinkering More Fun | joshiejack | updated 2nd Jul 2009 at 4:28am |
| 37 | 634199 | https://modthesims.info/d/634199/snow-but-better-snow-replacement-mod.html | Snow but better! - Snow replacement mod | Lyralei | 21st Oct 2019 at 12:42am |
| 38 | 594851 | https://modthesims.info/d/594851/baker-s-basket-plants-ingredients-and-recipes.html | Baker's Basket Plants, Ingredients and Recipes | icemunmun | 23rd May 2017 at 12:01am |
| 39 | 691427 | https://modthesims.info/d/691427/more-romantic-interactions-v2.html | More Romantic Interactions V2 | TheSweetSimmer | updated 23rd Oct 2025 at 10:46am |
| 40 | 548787 | https://modthesims.info/d/548787/cooking-and-ingredients-overhaul-more-nectar-ingredients.html | Cooking and Ingredients Overhaul + More Nectar Ingredients | Cinderellimouse | updated 15th Jul 2015 at 9:19pm |
| 41 | 470223 | https://modthesims.info/d/470223/new-update-yoga-rug.html | [New UPDATE] Yoga Rug | Kitabalibar | updated 1st Mar 2012 at 7:05pm |
| 42 | 490587 | https://modthesims.info/d/490587/now-compatible-1-67-minimum-wage-more-realistic-earnings-higher-tuition-higher-boarding-school-costs-higher-grant-money.html | [Now Compatible 1.67] Minimum Wage/More Realistic Earnings, Higher Tuition, Higher Boarding School Costs, Higher Grant Money | Madam Doofie | updated 24th Sep 2014 at 1:33pm |
| 43 | 490536 | https://modthesims.info/d/490536/grow.html | Grow | Consort | updated 17th Sep 2013 at 10:05am |
| 44 | 450059 | https://modthesims.info/d/450059/builder-stuff.html | Builder Stuff | Buzzler | updated 26th Jun 2016 at 3:25am |
| 45 | 500862 | https://modthesims.info/d/500862/longer-or-shorter-pregnancies-puddles-and-more-1-67.html | Longer or Shorter Pregnancies, Puddles and More! 1.67 | AgentTexas | updated 20th Sep 2013 at 12:22pm |
| 46 | 486695 | https://modthesims.info/d/486695/no-more-zombies-generated-at-full-moon-2-flavors.html | No More Zombies Generated At Full Moon - 2 Flavors | Personcalledjoy | 16th Sep 2012 at 5:43pm |
| 47 | 630525 | https://modthesims.info/d/630525/deep-conversations.html | Deep Conversations | QuackGames | 25th Jul 2019 at 7:08am |
| 48 | 459967 | https://modthesims.info/d/459967/gardener-service-v2-3-4th-may-2014.html | Gardener Service - v2.3 (4th May 2014) | douglasveiga | updated 5th May 2014 at 7:07am |
| 86 | 529671 | https://modthesims.info/d/529671/interaction-on-sloped-terrain-enabler.html | Interaction on sloped terrain enabler | nikel23 | 29th Jul 2014 at 10:47pm |

## Saved stacks through swapped methods (2026-11-06, docs/242)

Web search and the NRaas source found no mod that keeps a saved frame across a method swap: nothing online on the task-stack checksum; `game-copy/nraas-src` touches `TaskStateCollection` only in its compiler stub project. EA forum threads describe the symptom only (a Sim rebuilt by ResetSim, e.g. https://forums.ea.com/discussions/the-sims-3-technical-issues-en/sims-interaction-seemingly-disabled/8324681).

## Boat replan hitch on Isla Paradiso (2026-11-06, docs/248)

Looked for a fix of the sync replan after a get-in; none found. Workarounds only, nothing reused, no code taken:
- Houseboat NPC mod, https://modthesims.info/d/665907 (license: site terms): removes NPC houseboats, not the replan.
- Forum reports of the lag and port lot workarounds: https://forums.ea.com/discussions/the-sims-3-technical-issues-en/removing-ip-lag/2055350 , https://forums.thesims.com/en_us/discussion/692071/island-paradise-lag-issue-solution-no-mods-required (forum posts, read only).
- NRaas GoHere `mAllowBoatRouting` turns boat routing off for everybody; ours keeps boats for seen Sims.

## Sims leaving a houseboat (2026-11-07, docs/248 §10)

Looked for a fix or teleport of townies leaving a houseboat; none found, nothing reused, no code taken:
- https://modthesims.info/d/665907 (license: site terms): stops NPCs moving houseboats, another cause.
- https://forums.ea.com/discussions/the-sims-3-technical-issues-en/removing-ip-lag/2055350 , https://forums.ea.com/discussions/the-sims-3-en/isla-paradiso-fixes/187443 (forum posts, read only): remove the Scott household or the boats; ports with bad boat access.
- NRaas GoHere, StoryProgression (source read for behaviour): no teleport off a houseboat.

## Added 2026-11-08 (EA package-lock ABBA deadlock at the main menu; docs/539 §13.9)
Looked for a fix of the menu hang (package database lock pair `O+0x48` / `O+0xC0`); none found, nothing reused, no code taken:
- https://forums.ea.com/discussions/the-sims-3-technical-issues-en/sims-3-game-frozen-in-main-menu/8292874 , https://forums.ea.com/discussions/the-sims-3-technical-issues-en/help-frozen-loading-screen/12119308 (forum posts, read only): generic advice (windowed mode, delete caches, remove Mods), no lock cause.
- Apex Radiance source (`resource_cache.cpp`, read for overlap): no hook in `0x7343B0`-`0x7356xx`.

## Added 2026-11-12 (AI-assisted TS3 projects, GitHub + web search)
GitHub facts verified (repo pages, commit trailers); Nexus items are search snippets only; no Reddit/Tumblr reception checked.

| Project | URL | Overlap with us |
|---|---|---|
| ccstringmerge (SmellyBelly), v0.1.0 11-07, Claude co-authored | https://github.com/smellybellyy/ccstringmerge | Merges CC string tables per language; own-Sim pie menu 1.17 s -> 0.30 s with 6,282 CC packages. Closest overlap (CC-heavy UI/load); cross-link or collaboration candidate |
| Sims3FreezeCursor (thesammy58), v1.0.2 11-07, Claude-made | https://github.com/thesammy58/Sims3FreezeCursor | ASI: busy cursor when the sim thread stalls > 1 s (split from an S3MM freeze-rescue feature). Partial overlap with our watchdog/stall detection. Same author: LimbIkScaleFix, RadioLeveler, an S3SS fork |
| Sims3MP, sims-3-rust, sims-3-bevy (11 of 100 commits AI) | https://github.com/fsminecrafter/Sims3MP , https://github.com/mmeerrkkaa/sims-3-rust , https://github.com/Eamo5/sims-3-bevy | Multiplayer prototype; engine rewrites (long-term watch, like OpenTS3) |
| Nexus "AI Lighting ... POPULATION with DEEPSEEK" | [snippet] | Fork of LazyDuchess population mod; low |

Policy: Nexus Mods requires AI disclosure labels (AI Generated / AI Media / AI Assisted) since 2026-11-03, existing mods by 2027-01-02, no ban (GamingOnLinux, PC Gamer; secondary). Our Nexus page needs the "AI Assisted" label. No MTS AI rule found. The Sims Resource rejects AI-generated uploads.
