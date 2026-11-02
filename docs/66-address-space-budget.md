# 66. Address-space budget (4 GB, LAA)

What fills TS3W.exe's 4 GB address space at the main menu and in a typical Bridgeport (BP) live session, by region type and by owner, with the top shrink candidates. Data and static work only: no game run for this doc. Every number comes from existing files (`probe-out/queue/*.jsonl`, `probe-out/*/memory.csv`, `probe-out/phase0/*`, installed ini files) or from earlier docs, and is tagged.

Rebuild the measured tables from the files on disk:

```
node tools/harness/addr-budget.ts                      # markdown tables
node tools/harness/addr-budget.ts --json               # raw medians
node tools/harness/addr-budget.ts --probe-out <dir>    # e.g. the main checkout's probe-out from a worktree
```

Setup behind the data: Steam 1.67, DXVK 3.1.1 x32, S3SS 1.6.3 (Mimalloc, OversizedThreadStackFix on, WorldCacheSizePatch off), S3PP 2.1 (StblBucketLayoutTuning and AllocatorArenaTuning on, ThreadStackDiet off), ts3native `[GC] InitialHeapMB=352`, `GrowthStepMB=32`, Rodney's CC, Frida probe attached [verified: `probe-out/queue/bin-before/*.ini`, `S3SS.toml`].

## 1. Summary

- At the main menu 1.81 GB is committed and the largest free block is 1.69 GB. In BP live mode it starts at 2.18 GB committed / 1.53 GB largest free and after 7-30 min at speed 3 it is 2.28 GB / 1.34 GB [measured, medians]. The 30-min soak (M3) went from 1,485 to 1,229 MB largest free [measured, PLAN M3]. Error 12 is not close in these sessions; for scale, vanilla without DXVK was 3,490 MB committed and 176 MB largest free after a load (docs/05) [measured].
- Image (426 MB) is the same at the menu and in live mode and cannot be shrunk by us. Mapped memory (96-215 MB, p10-p90) is mostly DXVK's texture window.
- Private memory is the budget that moves: ~1.49 GB at the menu, ~1.84 GB at live start, ~2.04 GB after a session. Of that, ~900 MB has a known owner (Boehm GC heap 352-360, ResourceCache held ~360, stacks ~140, plus small fixed tables). About **940-1,135 MB has no owner yet**: the rest of EA's GeneralAllocator heaps, S3SS mimalloc arenas, string tables, the CAS part cache, SpeedTree and the DXVK/NVIDIA driver's private memory [computed remainder]. That remainder is also where the session growth (+195 MB start to end) lands.
- Not worth shrinking (already measured): ResourceCache budget (Phase 0: `CacheBudget` 0, 50 or 200 MB all hold ~400 MB), Sims (M11: ~0.15 MB each), camera pan/lot streaming (M10: +22 MB, returned), AnimationCache (3-10 MB), glyph cache (<= 1 MB), catalog (~8 MB).

## 2. Region types (measured)

Medians over the queue jobs that have `/memory` before and after: 1 menu job (`lane-g menu-memory`), 9 BP jobs (verify, lotstream, lane-G soak/save/travel; "start" = right after the load, "after" = 7-30 min at speed 3). The probe walks the whole range with `VirtualQuery` (`tools/probe/agent.js` `addressSpace()`); image/mapped/private include reserved pages, so `commit + reserved-only = image + mapped + private` and `that + free = 4,096 MB`.

| Region (MB) | Main menu | BP live, start | BP live, after 7-30 min | Tag |
|---|---|---|---|---|
| Committed | 1,808 | 2,184 | 2,284 | [measured] |
| Reserved, not committed | 326 | 264 | 313 | [measured] |
| Image (exe + DLLs + asi) | 426 | 426 | 426 | [measured] |
| Mapped (file mappings) | 219 | 178 | 145 | [measured] |
| Private (heaps, stacks, GC, driver) | 1,489 | 1,844 | 2,039 | [measured] |
| Free | 1,962 | 1,646 | 1,472 | [measured] |
| **Largest free block** | **1,693** | **1,533** | **1,340** | [measured] |
| Fragmentation (free - largest free) | 269 | 114 | 132 | [measured] |
| Threads (Frida-visible) | ~137 | ~140 | ~140 | [measured, memory.csv] |

Spread over all 147 `memory.csv` runs (9,197 samples, 5,017 in live mode, mixed worlds and configs, DXVK in all): live largest free p10 1,243 / median 1,462 MB; mapped p10-p90 96-215 MB, max 256; image 383-461 MB (older builds, other mod sets); one run (2026-10-11 18:01 PHT) is the docs/05 vanilla run at 3,507 MB commit and 176 MB largest free [measured]. "Menu-like" samples (commit 1.6-1.95 GB, private < 1.6 GB) have median largest free 1,821 MB, so the single menu job is on the low side [measured].

## 3. Owners

### 3.1 Image, 426 MB

| Owner | MB | Tag |
|---|---|---|
| `TS3W.exe` (SizeOfImage; Mono and Boehm are linked in) | 14.4 | [verified PE header] |
| Mods and wrappers in `Game\Bin`: DXVK `d3d9.dll` 7.5, `Sims3SettingsSetter.asi` 1.6, `Sims3Performance.asi` 0.6, `ts3native.asi` 0.2, `MonoPatcher.asi` 0.1, `wininet.dll` proxy 2.9 | ~13 | [verified headers; loaded inferred] |
| `EAWebkit.dll` | 3.7 | [verified header; loaded inferred] |
| NVIDIA 32-bit Vulkan driver: `nvoglv32.dll` 25.8, `nvgpucomp32.dll` 85.3 | ~111 | [verified file sizes; loaded inferred] |
| `d3dx9_31.dll`, Windows system DLLs, Frida agent (probe only) | ~285 | [computed remainder] |

No module list is in the existing data, so the split is from file headers. The image total does not change between menu and live mode and none of it is ours to shrink. A `/modules` probe readout (name, base, size) would turn the remainder into numbers and show how much the Frida agent adds to every measurement.

### 3.2 Mapped, 145-219 MB

| Owner | MB | Tag |
|---|---|---|
| DXVK `MemoryFilePool`: system copies of `MANAGED` textures and static buffers in 16 MiB file-mapping chunks, at most `d3d9.textureMemory` = 100 MB mapped at a time | <= 100 | [verified DXVK source, docs/40, docs/08] |
| Other DXVK mappings, the exe's own `CreateFileMappingA` at `0xEA4E9F`, NLS/locale and font files | rest | [inferred, no per-owner data] |

Mapped is highest at the menu (219) and lower later in live mode (145), so the menu UI textures are the likely peak [inferred].

### 3.3 Private, BP live (~1,844 MB at start, ~2,039 MB after)

| Owner | MB | Tag |
|---|---|---|
| Boehm GC heap (Mono). One contiguous block at Mono start from ts3native `InitialHeapMB=352`, grows in 32 MB steps, never returned | 352 at the menu, ~360 in BP (151 MB of it free at 6 min) | [measured config; heap size measured docs/04 F15] |
| ResourceCache held bytes (`vfMemResCacheBytes`, Verify.Memory) | median 359 (305-421 over the verify jobs); phase0 388-500 | [measured] |
| AnimationCache (budget forced 15 MB) | 3-10, median 4 | [measured, phase0 `animBytesMB`] |
| Thread stacks: ~140 threads x 1 MB reserve (PE `SizeOfStackReserve` 1 MB, commit 4 KB); S3SS gives file-watcher threads 64 KB, ts3native workers 256 KB | ~140, mostly reserved-only | [threads measured, sizes verified; total inferred] |
| Package indexes (EA ~15, CC ~3.5) | ~18.5 | [computed, docs/08] |
| Catalog: every OBJD decoded once at catalog load, 576 B each x 14,577 | ~8 | [inferred, docs/08 H1, docs/39] |
| Shaders kept: SM3 + shadow mode 1 subset, 8.7 MB bytecode + 3.8 MB DBUF; DXVK SPIR-V per created shader on top | ~12.5 + DXVK | [measured file scan, docs/53; kept subset inferred] |
| Glyph cache (UI font atlas, 4 pages x 512 x 512 x 1 B) | <= 1 | [verified bound, docs/54] |
| String tables (STBL bucket arrays) | ~100 stock with the store set; S3PP `StblBucketLayoutTuning` (on here) cuts most of it (S3PP on: -71 MB commit, +64 MB largest free) | [inferred docs/54; S3PP effect measured docs/17 §9] |
| S3SS mimalloc arenas (all MSVCR80 `malloc` users) | 64 MiB reserve per arena, 128 MiB from the 9th (the one-time 128 MB block on the first CC outfit build) | [arena sizes verified in source; count and owner of the 128 MB block inferred, PLAN M9] |
| EA GeneralAllocator (dlmalloc 2.7 cores via `VirtualAlloc`, S3PP cores 4 MB reserve / 1 MB commit) | holds the ResourceCache contents and most engine objects | [verified code, docs/15 §8; size not measured] |
| CAS part cache (built for every CASP at startup, 5,662 EA + 2,016 CC) | not measured | - |
| SpeedTree (`vfMemSpeedTreeBytes` reads -1: counter not wired) | small per model, up to 8 MB per cluster batch buffer | [inferred, docs/34] |
| DXVK and NVIDIA driver private memory | not measured | - |
| **Unattributed remainder** (private - GC - ResourceCache - AnimationCache - stacks - indexes - catalog - shaders - glyphs) | **~940 at start, ~1,135 after** | [computed] |

Caveat on the ResourceCache row: a texture reports its pixel bytes to the cache only when it has a system copy (`0x619DB0`, docs/40), and under DXVK that copy lives in the mapped `MemoryFilePool`, not in private memory. So part of the 359 MB is mapped, and the remainder above is a lower bound [inferred]. **`rc` is not 32-bit address space under DXVK:** a contributor pair (docs/209 #96, one heavy-CC save) saw 168 MB of textures leave `rc` while mapped memory fell by only 22 MB and free space rose by 40 MB [contributor-measured]; read `big`/`free` for Error 12 room, `rc` for cache pressure, VRAM and upload stutter. Since ts3native 0.36.135 the `diag: mem` line also carries the EA heap cores (`eaCom`/`eaRes`/`eaCoreN`), which used to fall into `rest`.

### 3.4 Reserved but unused, 264-326 MB

Thread stack reserves (~130 MB of the ~140), the uncommitted tails of mimalloc arenas and EA cores, and Boehm's reserve. The biggest reserved-only blocks in the verify runs are 54-55 MB, 29.6, 18.7, 15.6 and 8 MB with no image owner [measured `biggestReservedOnly`]; the 54 MB one sits low in the address space and fits a partly committed 64 MiB mimalloc arena [inferred].

### 3.5 Fragmentation

The largest free block is one high region (base `0x9EFC0000`-`0xA4E51000` in the verify runs) [measured]. Free outside it is 114-132 MB in live mode and 269 MB at the menu. In the M3 soak the largest block fell 256 MB in 30 min while private rose 302 MB: growth lands in the big block, it does not scatter [measured, PLAN M3]. Allocations that need a fresh contiguous region are the ones Error 12 kills first: Boehm growth (32 MB steps), mimalloc arenas (64/128 MiB), DXVK chunks (16 MiB) [verified sizes].

## 4. Top 5 shrink candidates

| # | Candidate | Expected saving | Risk | How to measure |
|---|---|---|---|---|
| 1 | **Right-size the Boehm initial heap** (`InitialHeapMB` 352 -> peak heap + margin, e.g. 256) | up to ~96 MB committed and largest free block (F15: 151 MB of the 360 MB heap free at 6 min) [inferred] | **Medium:** forced GCs come back if the heap is too small; F15 measured 128 -> 1 forced GCs with 352, so this trades against stutter (the priority). Only worth it if Error 12 shows up | First read the real peak: add `GC_heapsize` (`0x12225A0`) and free bytes (`0x12225B4`) to Verify.Memory and log them over a 30-min BP soak. Then a restart A/B (the heap size is fixed at start): BP profile 10 min x 2 per setting, forced GC count, hitches/s and `/memory` largest free |
| 2 | **Smaller thread stacks** (~140 threads at 1 MB reserve): S3PP `ThreadStackDiet` 512 KB, or our own `_beginthreadex`/`CreateThread` stack-size cap for the engine's worker threads | ~70 MB reserved-only, mostly largest free block [community figure, docs/07] | **Low-medium:** a deep-recursion thread overflows its stack = crash. Today off because S3SS's fix covers the file-watcher threads | Add a per-thread stack readout (TEB stack base vs `AllocationBase`) to the probe to get the real reserve per thread. Then the `menu-memory` job with `ThreadStackDiet=1` vs 0 (reserved-only, largest free), plus a 30-min soak and the save matrix for crashes |
| 3 | **mimalloc `arena_reserve` 64 -> 32 MiB** (S3SS hard-codes 64 MiB; the 9th arena becomes 128 MiB) | <= 64 MB of largest free block early in a session (the 128 MB block on the first CC outfit build) [inferred, PLAN M9] | **Low** (more arenas, tiny bookkeeping), but it needs an S3SS option: ask upstream, we do not patch their binary | `/mallochook` once plus `/allocations?minMB=32` diff in the `store-items-memory` repro (already prints the count): the 128 MB block should become 64 MB |
| 4 | **DXVK `d3d9.textureMemory` 100 -> 64 MB** (`dxvk.conf`) | <= ~36 MB mapped, and a lower mapped peak (max 256 MB seen) [inferred] | **Low crash risk, possible stutter:** more chunk map/unmap when the texture working set is larger than the window | Restart A/B per world (dxvk.conf is read at start): `mappedMB` and largest free from `/memory`, hitches/s from the profile. Stop if hitches rise |
| 5 | **Find the session grower** inside the unattributed ~1 GB (+195 MB private start to end; M3 +302 MB in 30 min) | unknown, up to ~200 MB if one owner leaks or over-caches [inferred] | None for the measurement; the fix depends on the owner | Periodic `/allocations?minMB=4` snapshots during `bp-m3-soak30` (every 5 min), grouped by allocation base and type, to split Boehm growth steps vs new mimalloc arenas vs EA cores vs driver blocks. Wire `vfMemSpeedTreeBytes` (reads -1) and add a CAS part cache byte counter while at it |

Ranked by expected MB times confidence. Candidates 1 and 2 are the only ones with a known size above 50 MB; 5 is the largest unknown.

## 5. Open measurements

- Module list (`/modules`) to split the 285 MB image remainder and size the Frida agent's own cost.
- Boehm `GC_heapsize` readout (candidate 1) and per-thread stack reserve (candidate 2): both built 2026-10-20, not run yet (§6).
- Menu-side owners: ResourceCache held and the CAS part cache at the menu (the menu job has no native counters).
- Largest free block during a save (M3 open item).

## 6. Quick-win A/B prep (2026-10-20, no game run)

Candidates 1, 2 and 4 made testable with memory metrics, which CPU noise does not move. Queue: `tools/harness/queues/addr-quickwins.json`, four BP jobs of 15 min at speed 3 (watchdog for correctness, `/memory` before and after) plus a 3 h filler-night soak. Run the four with `node tools/harness/queue.ts tools/harness/queues/addr-quickwins.json --only addr-base,addr-heap256,addr-stack512,addr-tex64` (~2 h with loads and restarts); the soak alone with `--only filler-night-soak3h`. One run per arm is enough for MB deltas; rerun an arm only if its `largestFreeMB` at start differs from the baseline's by more than the arm's expected saving for no visible reason.

### 6.1 New readouts

- `/memory` (probe `agent.js` `addressSpace()`) now also returns `gcHeapMB` (`GC_heapsize` `[0x12225A0]`), `gcFreeMB` (`GC_large_free_bytes` `[0x12225B4]`) and `gcCount` (`GC_gc_no` `[0x11F2F90]`); `memory.csv` (every ~10 s) gets `gcHeapMB`/`gcFreeMB` columns. [verified: `mono_gc_get_used_size` `0xE69670` = `call 0xE4DD30` (`mov eax,[0x12225A0]`) minus `call 0xE4DD40` (`mov eax,[0x12225B4]`), Steam 1.67 disassembly; `GC_gc_no` from docs/119.] Used heap = `gcHeapMB - gcFreeMB` (upper bound: free objects inside partly used blocks count as used).
- `[ThreadStack]` logs every exe thread start with its real reservation read from the new thread's TEB (`StackBase` - `DeallocationStack`).

### 6.2 Candidate 1: `[GC] InitialHeapMB`

How it works today [verified, `ts3native.cpp` `ApplyGc`]: ts3native patches the immediate of `GC_init`'s first expansion to `InitialHeapMB` (repo ini 352, code default 256, clamp 40-1024) in `DllMain`, before `GC_init` runs; the env var `GC_INITIAL_HEAP_SIZE` still overrides it. `GrowthStepMB` (32, clamp 8-256) patches the growth step; the heap never shrinks. Fixed at start, so every arm is a restart.

Data: no `/memory` row has a heap readout yet. The only peak is F15 (docs/04): BP after 6 min, 360 MB heap with 151 MB free = **~209 MB used**; with the 40 MB default the heap sat at 120 MB with 6 MB free (live set after a collection ~114 MB) and paid 128 forced collections in 6 min [measured]. The 352 heap reached ~360 in BP, so it grew once [measured, §3.3].

**Proposal: 256 MB** (47 MB over the 6-min peak, ~140 MB over the live set), fallback **288**. Saving up to 96 MB committed and largest free block [inferred]. Decision rule after `addr-heap256`: keep 256 if `gcHeapMB` after is still 256 (no growth) and `gcCount` after-before is within ~1.5x of `addr-base`; growth or many more collections = forced GCs are back (stutter is the priority): try 288, else keep 352. A 10-min BP profile pair (forced GC count, hitches/s) confirms before it ships.

### 6.3 Candidate 2: thread stacks, ts3native `[ThreadStack]` vs S3PP `ThreadStackDiet`

All 7 thread starts in TS3W.exe go through two IAT slots [verified, disassembly 2026-10-20]:

| IAT slot | Call sites | Stack size passed |
|---|---|---|
| `KERNEL32!CreateThread` `0xF95244` | `0xF49820`, `0xF6F207`, `0xF6F6C7`, `0xF6F969` (third-party block; starts `0xF49730`, `0xF6F070`, `0xF6DC50`, `0xF6F720`) | 0 |
| `MSVCR80!_beginthreadex` `0xF955D0` | `0x4DB355` (file watchers; S3SS rewrites this call) | 0 |
| same | `0x4E2FCE` `EAThread_Begin`, `0x4E323E` `EAThread_BeginRunnable` (sim thread, JobManager, audio, pool, ...) | params `+4`, 0 without params; never with `STACK_SIZE_PARAM_IS_A_RESERVATION` |

A size without the reservation flag is a commit size, so Windows reserves the PE default (1 MB) for every one of them [verified: PE `SizeOfStackReserve` 0x100000; Windows API semantics]. S3PP `ThreadStackDiet` (size 0 only, kernel32 detour) therefore misses EA starts that pass a commit size, and it also changes every DLL's threads (DXVK, driver, Frida) [inferred from docs/69 row 8].

Built: `native/ts3native/threadstack.cpp` (selftest `threadstack-test.h`), off by default, log only (no counters, no `TS3NATIVE_VERSION` change). It swaps the exe's two IAT slots only while they still hold the real exports (another hook first = skip with a log line). Rule: reservation = max(asked, `ReserveKB`) with the flag set, only when that is below the PE default and the caller did not set the flag. `Skip` (hex, default `0x76B470` `SimulatorThread_Proc`, where the Mono interpreter recurses natively) keeps the game's stack; for the EAThread entries `0x4E2E30`/`0x4E30A0` the user function at arglist `+0x10` (runnable: its vtable `+4`) is matched. A capped start that fails is retried with the game's own arguments. `ReserveKB=0` = observe only. It composes with S3PP (their detour sees our explicit size and leaves it alone) and with S3SS's watcher fix. Selftest: real threads through both wrappers read 1024 KB uncapped and 256 KB capped [verified, selftest under WOW64]; MSVCR80 passing `initflag` on to `CreateThread` is inferred from the VC8 CRT source, the in-game log line checks it.

Expected: only the exe's own threads of the ~140 (the rest belong to DLLs); saving 512 KB per capped thread, summed in the log (`saved N KB`). Risk: a capped thread with deep recursion overflows its stack (crash); watchdog and crash triage catch it, then that thread's `fn` goes into `Skip`.

### 6.4 Candidate 4: DXVK `textureMemory`

`queue.ts` now takes `"dxvkConf": { "d3d9.textureMemory": 64 }` per job: merged into `Bin/dxvk.conf` (plain `key = value` lines, no sections) for the job and put back after it; `bin-before/` keeps a copy. DXVK reads it at start.

### 6.5 Filler-night soak

`filler-night-soak3h` (PLAN "Filler night job"): 3 h BP at speed 3 with the baseline config; `memory.csv` gives commit/private/largest free and now the GC heap every ~10 s. Switch its `ini`/`dxvkConf` to the winners of 6.2-6.4 once they pass. Levels off = memory solved for this setup; keeps growing = candidate 5.

## 7. Clone world memory cost 2026-10-25

Cost of the routesnap stack (P3 cut images, P4/P5 replay clone of the lot quadtrees and map, private pools, replay ring; P6 on top) against release defaults (`[RouteAsync]` only). Existing data only, no game run: `probe-out/queue/*.jsonl` `/memory` before (right after the load) and after, plus each job's `*-native.json` counters (absolute before/after). Routesnap jobs = native `rpKB` or `plKB` > 0 (2026-10-20 to 10-25: `snap-p3b`/`p4-long`/`p5*`, wait-reducer, `p5-verify-0338`, `p5c-recs-0339`, `night-1024-p6`, `-p6-fix`, `g1dump` 1-3, `p6fix-verify`); release = the other jobs of the same days with a native dump (release-check, v0.1.5 revalidation, soaks, fix verifies). Medians, Rodney's CC set, same machine. All [measured] unless tagged.

| MB (medians) | BP release | BP routesnap | delta | IP release | IP routesnap | delta |
|---|---|---|---|---|---|---|
| jobs | 262 | 43 | | 75 | 15 | |
| Private at load (incl. reserved) | 1,851 | 2,016 | **+165** | 1,853 | 2,007 | **+154** |
| Committed at load | 2,167 | 2,228 | +61 | 2,181 | 2,238 | +57 |
| Reserved-only at load | 279 | 385 | +106 | 276 | 382 | +106 |
| Free at load | 1,646 | 1,480 | **-166** | 1,639 | 1,482 | **-157** |
| Largest free at load | 1,525 | 1,413 | **-112** | 1,517 | 1,415 | **-102** |
| Largest free after 10-16 min (n 111 / 31; IP n 40 / 8) | 1,301 | 1,097 (min 997) | **-204** | 1,501 | 1,333 | **-168** |
| Free after 10-16 min | 1,464 | 1,233 | -231 | 1,687 | 1,479 | -208 |
| Growth in play 10-16 min: commit / largest free | +112 / -225 | +185 / -314 | +73 / -89 | -13 / -16 | +33 / -82 | +46 / -66 |

Own counters (native dump, KB -> MB; BP 82 lots / 126.7k clone map entries, IP 119 lots / 122.6k):

| Part | At load BP / IP | After 6-22 min | Peak seen | Notes |
|---|---|---|---|---|
| Replay clone heap `rpKB` (lots, map, footprint copies, payloads) | 19.9 / 21.9 | 24 / 29-33 | `rpKBMax` 42 / 46 every run (two clone builds at load, `replay.cpp:1939` and `:1945`, old + new alive together); 64-79 on long runs; 113 once (IP 0.35.24, `rpKB` 98.7 after) | own `HeapCreate` heap, cap `ReplayCapMB=128` |
| Private pools `plKB` (committed of a **96 MB contiguous reserve**, bump, never freed) | 20.4 / 21.9 | 22-30 / 23-31 | 45.8 (IP 0.35.24) | `pools.cpp` `kPlRegionMB` |
| Cut images `spKB` | 6.6 / 6.8 | 9-13 | `spKBMax` 13-25 | cap `CapMB=64`, `ShareCuts=1` |
| Replay ring | 1.5 fixed | | | 65,536 x 24 B (`kRpRing`) [verified code] |
| Prebuilt game lot graphs `rpPrebuildKB` | 0-0.05 | | | most graphs already built by the game |

Where the load delta goes: committed +61 ~= own 48 (clone 20 + pools 20 + images 7 + ring 1.5) + ~13 heap slack [inferred]; reserved-only +106 ~= the pool region's uncommitted 76 + the replay heap's segments kept from the 42 MB double-build peak [inferred]. The largest block loses ~110 MB at load because the 96 MB pool reserve and the heap segments land in it [inferred: §3.5, growth lands in the big block]. In play routesnap adds ~45-75 MB commit and ~65-90 MB of largest-free loss over release; the counters explain ~10-25 MB of it (clone +4-11, pools +2-10, images +3-6); the rest is heap high-water from rebases and the transient clone/image peaks [inferred, no per-heap readout].

Scaling: clone + pools ~330-375 B per clone map entry (clone ~165-185 B, pools ~170-185 B; ~380-490 KB per lot; BP has fewer lots but more entries), so cost follows footprint count, not lot count [computed]. Each whole rebuild doubles the clone transiently (`rpKBMax` ~2.1x `rpKB`). The ring is fixed and small. Cuts cost 1-1.2 MB worst lot copy (`snLotCopyMaxKB` 600 BP / 1,217 IP) and stay under 25 MB with sharing.

**Verdict: notable, risky for heavy-mod setups.** Total ~200 MB of largest free block after 10-16 min (BP 1,301 -> 1,097 MB, ~15% of the headroom), ~110 MB of it fixed at load. On this setup Error 12 stays far away (> 1 GB left), but a heavy-CC/store setup that sits within ~200 MB of the cap today would cross it with routesnap on. Release defaults are unaffected (routesnap off); this matters for v0.2.0 if P5/P6 ship on.

Slimming options (savings in largest free block, all [inferred], none built):

| # | Option | Saving | Risk / check |
|---|---|---|---|
| S1 | Pool reserve 96 -> 48 MB (peak `plKB` 31, max 46); or grow in 16 MB regions | ~48 | low: `plFull` counter already makes the clone work fail safe; watch `plFull` = 0 |
| S2 | One clone build at load (drop the second build at `:1945`), and a fresh heap per epoch with `HeapDestroy` of the old one so rebuild peaks give their reservation back | ~20-40 (the 2x peak), plus part of the in-play high-water | low-medium: build order at world load |
| S3 | On-demand per-lot clone + free far/idle lots (clone only lots with route activity since N sim-hours) | ~25-35 if a third of BP's 82 lots are active | medium: miss = rebase on first use (a hold); needs a per-lot use counter first |
| S4 | Image cap `CapMB` 64 -> 24 | peak only, ~0-10 | low |
| S5 | Ring cap | ~0 (1.5 MB) | not worth it |

S1 + S2 recover roughly half the cost (~70-90 MB) for little work; S3 only after a lot-use readout. Readout gap: no per-heap committed/reserved line for the replay and image heaps (`HeapSummary`/`HeapWalk` in Verify.Memory would close the "heap slack" rows).


### 7.1 Slimming ranked and built 2026-10-25 (ts3native 0.36.73, counters v132, not run)

**Parked 2026-10-25: P6 stopped, see docs/189 §17.** All P5/P6 switches ship off; P6 no longer gates v0.2.0.

Rodney's call: slim the clone only where the gain is big (>= ~30 MB of largest free block or clearly dominant), then move it out of process (docs/194). Ranking rule (2026-10-25): first the big items that also help the helper (docs/194 §9: one build + heap per epoch, per-lot on demand); items that matter only while the clone lives in the game get built only if cheap (< ~2 h) and big, else they are the fallback if docs/194 G0 fails. A-numbers re-cut the S-rows above; MB = largest free block regained, from the §7 measurements, all [inferred] until `p6-slim-verify` runs. Load-time evidence: every routesnap log builds the clone twice at load ("asked by replay.cpp:1939" after world event 9 / 0x0A, then ":1945" at event 7 after the load's G build, 190-280 ms each), both alive until the consumer applies the second EPOCH, so `rpKBMax` ~2x `rpKB` [measured: crashn-rerun, g1dump4, p6fix-ab2, p6int-kinds logs].

| # | Option | MB regained | Effort / risk | Out-of-process move | Built |
|---|---|---|---|---|---|
| A3 | One clone build per load + one heap per build, destroyed with its last block (`EpochHeap`) | **~20** at load (no double build, no heap kept at the 2x peak), plus rebuild peaks in play (`rpKBMax` 64-79 on long runs, 113 once) | ~1.5 h; low-medium: the cut builds nothing from event 9 / 0x0A until event 7 (60 s bound, `rpLoadWaitTimeouts`); `HeapDestroy` runs in the consumer's grave slice under `+0x918` (`rpGenDestroyMaxUs`); long-lived blocks made during a build would pin its heap (`rpGenLive`) | **easier** (docs/194 §9 S2): one epoch = one self-contained heap, the helper's rebuild unit | **yes** |
| A1 | Pool reserve on demand: 16 MB regions as the pools fill, up to 96 (`PoolChunkMB`) | **~48-64** at load and after (peak `plKB` 22-31 -> 32 MB reserved; IP max 46 -> 48) | ~1 h; low: push stubs check the first region as before plus the later ones (lo published first, hi only grows); `plFull` still fails clone work safe | neutral (moot once the pools move; docs/194 §9 says build it for the interim release) | **yes** (cheap and big) |
| A4 | Per-lot on-demand clone, idle lots freed | ~25-35 (if a third of BP's 82 lots are active) | 8-12 h + a per-lot use readout first; medium: miss = rebase hold on first use | **easier, highest value** (docs/194 §9 S3: helper restart = per-lot rebases, the ring the only way lots arrive) | **readout built** (`LotUseReadout`, below); the clone change waits for its numbers |
| A2 | The unattributed ~50 MB in play (heap high-water from rebases and transient peaks) | part of A3's in-play gain; rest unknown | readout: `rpGenRelKB` / `rpGenLive` now; `HeapSummary` per heap still open | neutral | readout via A3 counters |
| A5 | Auto stand-down: replay off once the session-low largest free block is under a threshold | 0 on this setup (> 1 GB free); ~25-45 when it fires (clone + heaps; pools and images stay) | ~20 min; low (the `ReplayCapMB` switch-off path) | moot out of process | **no: fallback if docs/194 G0 fails** (not big on this setup) |
| S4 | Image cap 64 -> 24 | ~0-10 (peak only) | low | neutral | no |
| S5 | Ring | ~0 (1.5 MB fixed) | - | - | no |
| S6/S7 | Portal refs as values, position-independent payloads (docs/194 §9) | ~0 MB; S6 also drops the grave's portal step under `+0x918` | medium | prerequisite for the move | no (docs/194 scope) |

Both switches default off (release ini unchanged): `[RouteSnap] PoolChunkMB` (0 = one 96 MB region; 16-48), `EpochHeap` (0/1). Counters v132: `plChunkMB`, `plChunks`, `plResKB`, `plSavedKB` (96 MB minus the reserve), `rpGenOn`, `rpGenLive`, `rpGenFreed`, `rpGenRelKB` (peak block KB of the destroyed heaps, a lower bound of the reservation returned), `rpGenDestroyMaxUs`, `rpLoadWaitCuts`, `rpLoadWaitTimeouts`. Selftest: TestPools (first region full -> a 16 MB region, stubs push its nodes privately, counters), TestSnapReplay seeds 3-4 with EpochHeap (a load-wait cut builds nothing; every build heap destroyed by teardown). Predicted with A1 + A3: largest free +50 MB at load and +60 MB at 10 min over the §7 routesnap medians, about half of the ~110 / ~200 MB cost (ledger §350, I-66-7-1..2). Verify: `tools/harness/queues/p6-slim-verify.json` (BP + IP, P6 on, VerifyEvery=1, [MemDiag] + [UserDiag]), asi `game-copy/ts3native.asi.slim`.

**A4 readout (Rodney 2026-10-25, built in the same 0.36.73, counters v133, not run):** `[RouteSnap] LotUseReadout=1` (default off; readout only, the clone is unchanged). Jobs: each P5/P6 clone run's read set (StaleLot's `LotTable_Find` detour, installed for the readout when StaleLot is off) adds one run and the agent to every lot it read. Bytes: at each [UserDiag] memory sample the replay consumer (the clone's only writer) walks the clone between rounds: per lot footprints (map nodes, FMEs, footprint copy blocks without their game-allocated polygon caches), quadtree (Q objects, inner and leaf nodes, leaf lists), portals (the lot graph's and rooms' portal pointer vectors), other (node copy, other graph vectors, room records); the world graph R is not in the clone, so its share is 0 here. Log: `lot use (sample #n)` summary with the share of lot bytes on lots no job read, the top 20 lots (bytes by structure, jobs, agents), and `lot use (exit ...)` at a clean exit; report `node tools/harness/lot-use-report.ts`. Limits: read sets miss lots reached without a key lookup (docs/189 phase 2, the StaleLot caveat) and count over 32 lots as `luOver`, so "untouched" is an upper bound. **What it decides:** untouched share >= 40% of lot bytes over a 10-12 min P6 run on both worlds = build the simple variant first (never copy lots no job has read; first read = a rebase, as a stale lot today), worth ~0.4 x 20 MB = 8+ MB in-process plus the helper's start/restart cost; < 40% = the simple variant is small, so only the full A4 (idle lots freed after N sim-hours, using the per-lot use history this readout starts) or nothing. In process this stays a small gain (~5-15 MB); its main value is the helper restart (docs/194 §6.3).
