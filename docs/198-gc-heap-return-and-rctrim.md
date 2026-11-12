# 198. Boehm heap return (static) and the ResourceCache trim experiment

Status: 2026-10-26. Part A static only (Steam 1.67 `TS3W.exe`, capstone disassembly of `game-copy` copies, no game run). Part B built in ts3native 0.36.80 (`[ResourceCache] TrimAfterCleanup`, off), selftest only; queue `tools/harness/queues/rctrim-exp.json` not run. Companion to docs/191 (memory plan, step 7), docs/66 §3.3 / §6.2 (Boehm heap budget, `ApplyGc`), docs/57 (GC and allocator), docs/39 (resource system), docs/193 (ResourceCache addresses). Ledger docs/51 §355.

**Ownership.** ResourceCache is Apex Radiance's area (docs/191 Ownership). Rodney 2026-10-26: "This is just an experiment and only propose them when we have a proven path forward." Nothing in this doc goes to Apex; the switch stays off by default and stands down when Apex is loaded.

## 1. Question

The Mono Boehm heap starts as one block of `InitialHeapMB` (352), grows in `GrowthStepMB` (32) steps and is never given back (docs/66 §3.3). F15 measured 151 MB of a 360 MB heap free at 6 min. Can the free part be given back: decommitted (RAM, commit charge) or released (address space, the Error 12 budget)?

## 2. Verdict

| Option | Feasible | Gain | Cost / risk |
|---|---|---|---|
| **Decommit** free hblks after a full GC (`MEM_DECOMMIT`) | yes, with a recommit detour [inferred design on verified structures] | RAM / commit charge only: private bytes drop by the free pages; **address space unchanged** | 2 detours (decommit pass after the collection, recommit where a free block is taken), write-watch check; ~3-5 h + 2 runs |
| **Release** whole empty growth sections (`MEM_RELEASE`) | mechanically yes, practically no | <= 32 MB per empty growth section; the 352 MB initial section is never empty | 10 globals to keep consistent, coalesced free blocks crossing sections, write-watch loop; yield likely 0 |
| Right-size `InitialHeapMB` (docs/66 candidate 1) | yes, built | up to ~96 MB address space | forced GCs if too small (stutter); restart A/B |

Address space is the 32-bit limit (CLAUDE.md); RAM is not on the target machine (32 GB). So decommit fixes the wrong resource, and release has no material yield. The address-space lever stays docs/66 candidate 1 (measure the peak heap with `diag: mem gcHeap/gcFree`, then lower `InitialHeapMB`). No build for Part A.

## 3. Findings [verified unless marked]

### 3.1 No unmap code

- `VirtualFree` (IAT `0xF9525C`) has five callers: EA allocator `0x4E4840`, `0x4E4F90`, `0x4E51B0`; `0xD70990`; Mono `0xEBC580`. None is in the Boehm range (`0xE49000`-`0xE56000`).
- `0xD70990` (`VirtualQuery` + EA tracker + `VirtualFree(MEM_RELEASE)`) is reached only from `GC_win32_free_heap 0xE4EF50`, which frees every heap base at shutdown (only caller `0xD80B5B`).
- No `GC_UNMAP_THRESHOLD` / `GC_FORCE_UNMAP_ON_GCOLLECT` strings (stock `USE_MUNMAP` reads them in `GC_init`); `GC_INITIAL_HEAP_SIZE`, `GC_MAXIMUM_HEAP_SIZE`, `GC_ENABLE_INCREMENTAL` are there.
- So the build has no `GC_unmap` / `GC_remap` / `GC_unmap_old`: the collector never decommits or releases a heap page.

### 3.2 How heap sections are obtained and recorded

| Item | Address | Notes |
|---|---|---|
| `GC_expand_hp_inner(n blocks)` | `0xE49C80` | min 64 blocks, rounds to page size `[0x1222520]`, honours `GC_max_heapsize [0x12225A4]`; callers `GC_collect_or_expand` `0xE4A20C`/`0xE4A219`, `GC_init` `0xE4E36E` |
| `GC_win32_get_mem(bytes)` (EA) | `0xE4EEF0` | one `VirtualAlloc` per call through EA `0xD70900`: `MEM_RESERVE|MEM_COMMIT|MEM_WRITE_WATCH` (`0x203000`), `PAGE_EXECUTE_READWRITE`, EA counter `[0x11EE520] += bytes`; base stored in `GC_heap_bases 0x122EF14[]`, count `[0x11F40EC]`, max `0x300` ("Too many heap sections"); page-alignment check |
| `GC_add_to_heap(p, bytes)` | `0xE49B10` | `GC_heap_sects 0x122D714` (8-byte `{start, bytes}`), `GC_n_heap_sects [0x11F2FC0]` (max `0x300`); `GC_install_header 0xE4B6E0`, `hb_sz = bytes >> 2` (words, 6.x layout), `hb_map = 1`, `hb_flags = 0`, then `GC_freehblk`; `GC_heapsize [0x12225A0] += bytes`; plausible bounds `[0x1194824]` / `[0x11F2FC4]` |
| after the add | `0xE49D55`.. | `GC_last_heap_addr [0x12225AC]`, `GC_prev_heap_addr [0x12225B0]`, `GC_collect_at_heapsize [0x1194818]`, optional hook `[0x1234364](GC_heapsize)` (Mono heap-resize callback [inferred]) |
| header memory | `GC_scratch_alloc 0xE4B520` | separate `GC_win32_get_mem` calls (`0xE4B552`/`0xE4B58C`/`0xE4B5B4`): **headers are not in the heap sections** |

So every section is its own `VirtualAlloc` allocation of exactly its size (the initial 352 MB one included). Sections can be adjacent in the address space.

### 3.3 Free blocks

- Headers: two-level table `top_index 0x1233318` (`addr >> 22`, then `(addr >> 12) & 0x3FF`); block header fields: `hb_sz +0` (words), `hb_next +4`, `hb_prev +8`, `hb_map +0x10`, `hb_flags +0x15`. HBLKSIZE = 4 KB = page size, so every free hblk is whole pages.
- `GC_hblkfreelist 0x11F2DA0[61]` (index: <= 32 blocks = n, < 256 = 32 + (n - 32) / 8, else 60), `GC_free_bytes 0x11F2E98[61]`, `GC_large_free_bytes [0x12225B4]`.
- `GC_freehblk 0xE49730`: rounds size, `GC_remove_counts 0xE4B810`, double-free check against `GC_invalid_map [0x11F30D8]`, `GC_invalidate_map 0xE4E890`, coalesces with the next block (`HDR(p + size)`) and the previous one (`GC_free_block_ending_at 0xE490D0`) via `GC_remove_from_fl 0xE49030` + `GC_remove_header 0xE4B7D0`, `GC_large_free_bytes += size`, `GC_add_to_fl 0xE49190`. **Every write goes to the header table or the list globals; none touches the block's own pages.** Coalescing looks only at the neighbour's header, not at section bounds, so a free block can span two adjacent sections.
- Free blocks leave the list in `GC_get_first_part` (`0xE4921E`) and `GC_allochblk_nth` (`0xE495CA`); `GC_allochblk 0xE49850` is called from `0xE4BAC0`, `0xE4BAF0` (large objects) and `0xE4E7D5` (`GC_new_hblk`). Those callers write into the block at once (free-list build, clears) [inferred from 6.x source shape, not disassembled per site].
- Incremental mode: EA's `GC_read_dirty 0xE4EFB0` calls `GetWriteWatch` over every `GC_heap_sects` entry.

### 3.4 Decommit design (not built)

1. After a full collection on the sim thread (allocation lock held; `GC_gcollect 0xE4A0B0` return or the `GC_try_to_collect 0xE4A050` path when it completed a cycle), walk the 61 free lists and `VirtualFree(MEM_DECOMMIT)` every free block of >= N pages that has not been decommitted yet; keep a side bitmap per page (no free header bit exists).
2. Detour the two take sites (`0xE4921E`, `0xE495CA` callers of `GC_remove_from_fl`) or `GC_allochblk_nth` entry: `VirtualAlloc(MEM_COMMIT)` the whole block before it is handed out (a commit on committed pages is a no-op). Coalescing in `GC_freehblk` needs nothing: a mixed block is recommitted whole when taken.
3. Risks: (R1) any path that reads or writes a free block's pages outside those two sites faults (conservative marking skips free blocks through the header check [inferred], the dirty scan only reads write-watch state); (R2) `GetWriteWatch` on a section with decommitted pages [inferred to work: tracking is per committed page]; (R3) recommit failure under commit pressure = an access violation instead of Boehm's out-of-memory path; (R4) EA counter `[0x11EE520]` and tracker see no change (cosmetic). Gain: private bytes and commit charge only.

### 3.5 Release design (rejected)

Only a growth section that is one free block (after splitting off any coalesced neighbour part) could go. It needs, under the allocation lock right after a full GC: unlink from `GC_hblkfreelist` + `GC_free_bytes[i]`, remove its headers (`GC_remove_header` per block start, counts), compact `GC_heap_sects`/`GC_n_heap_sects`, `GC_heap_bases`/`[0x11F40EC]`, `GC_heapsize -= bytes`, `GC_large_free_bytes -= bytes`, `GC_collect_at_heapsize`, the `[0x1234364]` callback, `[0x11EE520]`; leave the plausible bounds and black lists (address-hashed bitmaps: stale bits only make the GC more conservative [inferred]); remove the section before the next `GC_read_dirty` (else `GetWriteWatch` on released memory). Boehm 6.x has no code that removes a section, so every reader of these globals is an untested path. Yield: the initial section never empties (all early objects live there) and growth sections are rarely all free because `GC_allochblk` takes the first fitting block by size class, not by address [inferred]. Rejected.

## 4. Part B: `[ResourceCache] TrimAfterCleanup` (built, 0.36.80, off)

### 4.1 The game's trim [verified disassembly]

- `ResourceCache_Trim 0x732E90` (thiscall on a cache, `ret 0xC`, args `sub`, `filter`, `force`): takes `g_ResourceCacheLock 0xFFDD34`, walks the flush list (`+0x3C`, idle unreferenced entries, idle bytes `+0x98`) while `held (+0x94) + idle > budget (+0x8C) - sub`, unless `force = 0` and idle < overflow (`+0x90`); moves the dropped entries to a local vector, writes the new idle total, unlocks, then releases each entry (`0x731DD0`) outside the lock; returns the bytes dropped. Referenced entries are never on the flush list.
- `ResourceManager_ShrinkBoth 0x733E70` (manager primary vtable `0xFFE2F0` slot `+0x50`, data `0xFFE340`; thiscall, `ret`; an earlier version of this line said `0xFFE250` slot `+0xF0`, the same data word counted from the wrong vtable): `Trim(budget, 0, 1)` on the caches at manager `+0x1E0` (ResourceCache) and `+0x1E4` (AnimationCache), i.e. target 0: **every idle entry of both caches dropped**. No direct call in the exe; Apex MemoryGuard calls it under low address space. `0x733120` (cache vtable slot) does the same for one cache.
- `ResourceManager_Update 0x737560` (thiscall, `ret 4`): `Trim(0, 0, 0)` on both caches (down to budget, overflow kept) every call, then the manager's other upkeep. Called only from the ResourceSystem service update `0x7377F0` (`ecx = service + 0x18`), the `+0x1C` slot of vtable `0xFFEB10` (data `0xFFEB2C`), stored only by the service ctor `0x739120` (manager ctor `0x736DB0` at `+0x18`). That manager has two vtables: `0xFFE2F0` at `+0` (`C7 06 F0 E2 FF 00` at `0x736DD2`) and the ResourceManager interface `0xFFE250` at `+8` (`C7 07 50 E2 FF 00` at `0x736DD8`, `edi = this + 8`); `g_ResourceManager [0x11C78D8]` (memdiag, verify, castrim) points at `+8`, and `ResourceSystem_Init 0x738670` (slot `+4` of `0xFFE250`) stores the two caches through that `+8` pointer at `+0x1D8` / `+0x1DC`, i.e. manager `+0x1E0` / `+0x1E4` [verified exe bytes 2026-11-11].
- Engine service `+0x1C` slots run on the **main thread** from `ServiceList_Update 0x59ED20` (docs/36 §1, docs/167 §7.1).

### 4.2 Call site and thread safety

ts3native swaps the data pointer at `0xFFEB2C` (refuses when it does not hold `0x7377F0`; no code bytes) for a wrapper that calls `0x7377F0` and then, when a trigger is due, `0x733E70` on `service + 0x18` after checking the manager vtables (`0xFFE2F0` at `+0`, `0xFFE250` at `+8`) and the ResourceCache vtable `0xFFDD94`. **Fixed 2026-11-11 (next version):** the check compared `+0` with `0xFFE250`, so every trim was skipped; the first in-game run (old save, Apex not loaded) logged only `rctrim: skipped load` / `skipped world-switch: manager or ResourceCache vtable not as expected`. The skip line now prints the manager pointer, both vtable words and the cache vtable seen versus expected. That is the same thread, the same place and the same function family the game already runs every frame (`0x737560` -> `0x732E90`), with a lower target. The trim locks the cache itself and releases dropped entries outside the lock exactly as the per-frame call does, so no new lock order or thread is introduced [verified for the trim body; the resource release `0x731DD0` body is not read, it runs on this thread every frame already].

Triggers: the ts3fixes `world:` line (`g_udWorlds`, [UserDiag] on, release default) + 2 s settle: `load` (first), `world-switch` (new world name: travel arrival and return), `reload`; every `TrimEveryMin` minutes after the first load (`periodic`). **Overwatch nightly: not built** (ts3fixes sends no Overwatch signal to native; adding one is a ts3fixes change).

Apex Radiance loaded and `[Compat] ApexStandDown=1`: no trim, one `rctrim: stood down` line, counter `rtStood` (checked per trim, so a late Apex load is covered).

### 4.3 Output

`rctrim: #n <why> world=<name> rc=<held>/<idle> -> <held>/<idle> anim=... freed=<MB> priv=<MB> -> <MB> commit=<MB> -> <MB> big=<MB> -> <MB> ms=<shrink ms> walkMs=<two address-space walks>`. Counters v136 (`rtOn`, `rtTrims`, `rtFreedKB`, `rtLastFreedKB`, `rtMaxUs`, `rtStood`, `rtBad`). Selftest `TestRcTrim` (`rctrim-test.h`): fake manager and caches, fake shrink, counts, vtable refusals, Apex stand-down, triggers.

### 4.4 What the queue answers

`tools/harness/queues/rctrim-exp.json` (not run, ~1 h 40 min wall incl. loads): `bp-rctrim` (BP 20 min, shipped ini + `TrimAfterCleanup=1 TrimEveryMin=5`), `bp-rctrim-travel` (save-matrix travel round trip, trims at both arrivals), `bp-rctrim-cc2k` (same as the first with the 2k synthetic CC set). Questions: how many idle MB does a trim drop (docs/39 measured 341-460 MB held at a 200 MB budget, so up to ~100+ MB idle); does private bytes or largest free move at all (the EA heap returns core tops itself, docs/191 prior art: memory freed into dlmalloc may stay committed); does the shrink cost a main-thread hitch; do hitches follow (dropped idle resources reloaded). Gain path for an Apex proposal: largest free up after travel or under heavy CC with no hitch cost. Without it: drop the experiment.
