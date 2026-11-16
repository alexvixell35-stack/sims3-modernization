# 201. `[MemDiag] AllocSites`: native address-space growth by call site

Status 2026-10-26: 0.36.88 (asi `game-copy/ts3native.asi.allocsites6`) fixes a load deadlock in the hooks' reentrancy flag (TLS on an index >= 64, §15). Built (ts3native 0.36.85, `native/ts3native/allocsites.cpp`, asi `game-copy/ts3native.asi.allocsites3`), run in the game 2026-10-26 (queue `tools/harness/queues/alloc-sites.json`, results §13; ledger §363). v4 built, not run (0.36.86, asi `game-copy/ts3native.asi.allocsites4`, queue `alloc-sites4.json`: untracked allocations, Ex hooks, §14; ledger §364). TEST-RUN TOOL: off by default, never a release default. Ledger docs/51 §357.

## 1. Why

Error 12 is the 32-bit address space (CLAUDE.md, docs/66). The `diag: mem` lines (docs/190, docs/191) say how much each region type grows, not who holds it. Evidence to explain [measured]:

| Run | Largest free | Other |
|---|---|---|
| 45-min BP soak | 1,533 -> 1,211 MB | |
| one live stress cycle (place 400 CC objects + dress Sims, remove all, forced GC; docs/200) | -16 MB per cycle | free -28..-39 MB, `map` 112 -> 134 MB |
| one travel round trip (Bridgeport -> France -> Bridgeport) | -80 MB | docs/191 travel lead |

The design note of docs/191 step "not built" (commit 758a4e24: per-call-site tracking behind a switch, sampled, side table, dumped at each sample) is this tool, widened to every reservation and file view, because the largest free block shrinks by reservations and views, not by heap blocks inside existing cores.

Ownership: the native allocator is Apex Radiance's area (docs/191 Ownership, docs/187). This only observes; nothing goes to Apex from it. With Apex loaded and `[Compat] ApexStandDown=1` it installs nothing (`SKIP alloc sites: Apex Radiance loaded`).

## 2. Hooks

Each hook checks the bytes before it writes and chains an existing `jmp` (another tool's or `[Alloc]`'s detour) instead of refusing, so install order does not matter. Install runs after `[Alloc]` and `[CrashForensics]` (its heap check reads the Free prologue). docs/68 has the row.

| Entry | Address / where | Args, ret | Check | Tag |
|---|---|---|---|---|
| EA heap Malloc | `0x4E69D0` | thiscall (size, flags), `ret 8` | prologue `56 57 8B F9 8B B7 E4 04 00 00` | [verified disassembly 2026-10-26; alloc.cpp] |
| EA heap Memalign | `0x4E6A20` | (size, align, offset, flags), `ret 0x10` | same prologue | [verified] |
| EA heap Realloc | `0x4E6A70` -> core `0x4E6710` | (ptr, size, flags), `ret 0xC` | same prologue | [verified: args at `+0xC/+0x10/+0x14` after the two pushes, `ret 0xC` at `0x4E6AB4`] |
| EA heap Free | `0x4E5920` | (ptr), `ret 4` | same prologue | [verified] |
| `NtAllocateVirtualMemory` (MEM_RESERVE only) | ntdll | stdcall, 6 args | WoW64 stub starts `B8 imm32` (5 bytes, no relative operand) | [verified host selftest on Win11 x64 WoW64: 5 stubs hooked, round trips counted] |
| `NtFreeVirtualMemory` (MEM_RELEASE) | ntdll | 4 args | same | [verified] |
| `NtMapViewOfSection` (MEM_MAPPED only; images skipped) | ntdll | 10 args | same | [verified] |
| `NtUnmapViewOfSection`, `NtUnmapViewOfSectionEx` | ntdll | 2 / 3 args | same | [verified: both paths in the selftest] |
| `NtAllocateVirtualMemoryEx` (VirtualAlloc2), `NtMapViewOfSectionEx` (MapViewOfFile3), 0.36.86 | ntdll, Windows 10 1803+ | 7 / 9 args | same | [verified host selftest on Win11 WoW64: both hooked, round trips counted; §14] |

Coverage: every public EA heap entry. The only direct callers of the cores (`0x4E5E40`, `0x4E51B0`, `0x4E6710`, `0x4E63F0`) are the locked entries themselves, the realloc core and the EA debug layer (`0x4E7B9E`, `0x4E823F`, `0x4E82AF`, `0x4E83FF`, `0x4E843A`, `0x4E846A`) [verified: call scan of `.text`]. The debug layer runs only when `ICoreAllocator_Alloc 0x4E3E40` finds debug allocator id `0x8843221A` through `0x4E44F0` [inferred off in retail]; its blocks would be invisible (gap G1). The ntdll level sees every module: EA cores `0x4E48F0` / direct blocks `0x4E50F0`, Boehm `GC_win32_get_mem 0xD70900` (docs/198), S3SS mimalloc arenas (MSVCR80 `malloc`, so no separate MSVCR80 hook: its arenas show as `virtual` sites in the S3SS module), DXVK `MemoryFilePool` views, NT heap segments, driver memory. `CreateFileMapping` itself is not hooked: a section costs commit, not address space; the view does. The loader's image mappings are skipped (`MEM_IMAGE`).

Before 0.36.86 not hooked: `NtAllocateVirtualMemoryEx` / `NtMapViewOfSectionEx` (gap G2, closed in §14). Thread stacks are reserved by the kernel / WoW64 layer and never pass the 32-bit stub (they stay in `stkRes`, docs/190).

Overlap: S3SS hooks MSVCR80 and game code, S3PP patches immediates inside `0x4E48F0`/`0x4E5AA0` (not the entries), Apex FastMemory hooks `0x4E4F90` and `0x4E5306` (docs/187 `apex-hooks.csv`) [verified from those tables]; none of them touch the ntdll stubs [inferred: no ntdll site in docs/68 or apex-hooks.csv]. The Frida probe agent attaches to none of these (`tools/probe/agent.js`) [verified].

## 3. Sampling and tables

- Heap blocks with chunk size < 64 KB: 1 in N (`AllocSitesSample`, power of two, default 64) by a hash of the block address, weight N. Address-based sampling lets Free decide without a lookup. Blocks >= 64 KB (chunk size word at `p-0xC`, alloc.cpp), every reservation and every view: all, weight 1.
- Sites: 4,096 slots (16,384 from 0.36.86, §14) keyed by (kind, `at`, `from`), insert-only, row 0 = overflow. Per site: live bytes and count (estimated for sampled blocks), allocated and freed totals.
- Sampled heap blocks: 256 stripes x 1,024 entries (3 MB), linear probing with backward-shift delete, one try-lock per stripe. Regions: 16,384 entries keyed by base. All tables are reserved at install (~4.5 MB with the per-tag tables, only when on); nothing allocates inside a hook.
- Locks never block: a bounded spin (64 pauses, then up to ~4,000 `SwitchToThread`) and then the record is dropped (`busy`). A thread suspended by Boehm's stop-the-world while holding a stripe costs a lost record, never a hang.
- A block leaves the table before the game frees it (free, realloc), so a reused address cannot remove another thread's newer record. A per-thread TLS flag stops recursion.
- Shortcuts: a realloc that fails loses the old block's record; a site key filled by two threads at once can appear twice (the report merges by key).

## 4. Attribution

`at` = the first frame outside the allocator: for the heap, the caller of the hooked entry, or the next frame when that caller is an EA wrapper (`0x4E3D00`-`0x4E9000`: `ICoreAllocator_Alloc 0x4E3E40`, `EA_AllocTagged 0x4E3F90`, `EA_Free 0x4E3F70`, the cores, the debug layer); for reservations and views, the first frame outside ntdll / kernelbase / kernel32. `from` = the next frame (another module, or outside the EA allocator range when `at` is inside it: an EA core's `VirtualAlloc` gets the heap user as `from`).

Frames come from a stack scan for words that are return addresses (in a module image, preceded by a `call`: `E8`, `FF /2` with disp32 / disp8 / sib / register forms). EA code is frameless (docs/15 §8: "the sampler cannot name them"), so there is no ebp chain to walk. A stale return address in an uninitialised local can be picked up [inferred]; read `from` as a lead, `at` is reliable for the heap (it is the hook's own return address unless that is a wrapper).

Output names TS3W.exe addresses as VAs (`0x004E4931`); `tools/memory-report.ts` adds the nearest named function at or below from `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (3,819 names; a guide marked `?`, not proof). Other modules print as `name.dll+0xoff` (file name only, docs/188 privacy).

## 5. Cost (host selftest, `out-allocsites\selftest.exe`)

| Path | Unhooked | Hooked | Added |
|---|---|---|---|
| EA heap malloc + free pair (dummy entries, near-free body), 1 in 64 | 5-7 ns | 8.5-9 ns | **+2-3.5 ns per pair** (3 runs) |
| same, every block sampled (worst case, `AllocSitesSample=1`) | | 95-127 ns | +90-120 ns per sampled pair |
| `VirtualAlloc` reserve + release 64 KB | 1.6-2.3 us | 1.8-2.4 us | +0.2-0.7 us (noisy; one run -0.5) |

So the sampled path costs ~100 ns and the unsampled one ~3 ns. At an assumed 1M EA heap pairs per second that is ~3 + 1.6 ms/s [inferred rate; docs/15 §8 has the allocator at 6.9-16% of sim time, not a call count]. Test-run tool only; never a timing arm. The output costs one VirtualQuery walk (~ms, like `diag: mem`) plus `GetModuleFileNameW` per printed row, on the thread that takes the sample.

## 6. Output

At every `diag: mem` sample (each `mem_mark`, every 5 min of play, load, save, exit), on the same thread, right after the `diag: mem` line. Format (values made up):

```
allocsites: #14 mark ev=live-empty sum heapMB=612.4 heapN=1840000 virtMB=1702.3 virtN=861 mapMB=131.9 mapN=34 sites=2210 blocks=31800 regions=895 sampled=402113 drops=0 busy=0 nofree=12 rgnMiss=3 vCalls=5120 vUs=21400 vMaxUs=310 sample=64
allocsites: #14 mark ev=live-empty live r=1 k=virtual at=0x00D7090A from=0x00E4EF00 MB=384.00 n=9 dMB=+0.00 dN=+0 aMB=384.0 fMB=0.0
allocsites: #14 mark ev=live-empty grow r=1 k=map at=d3d9.dll+0x4A1C2 from=d3d9.dll+0x3B7E0 MB=96.00 n=6 dMB=+16.00 dN=+1 aMB=812.0 fMB=716.0
allocsites: #14 mark ev=live-empty cyc r=1 k=heap at=0x0073A1F2 from=0x00737E85 MB=41.20 n=60416 dMB=+2.75 dN=+4032 aMB=950.3 fMB=909.1 since=#9
allocsites: #14 mark ev=live-empty inbig r=1 k=virtual at=0x004E4931 from=0x00737E85 MB=8.00 n=2 win=0x9EFC0000+1240MB
allocsites: #14 mark ev=live-empty free r=1 base=0x9F800000 MB=1211.3 below={k=virtual at=0x004E4931 from=0x00737E85} above={image nvoglv32.dll+0x1000}
```

| Row | Meaning |
|---|---|
| `sum` | live MB / count per kind (heap estimated), tracked sites, sampled blocks, regions; `drops` (table full or lock given up), `busy` (lock give-ups), `nofree` (a sampled free not in the table: allocated before the hooks, through the debug layer, or dropped), `rgnMiss` (a release of a region made before the hooks), virtual hook calls / us / max us, N |
| `live r=` | top `AllocSitesTop` sites by live bytes |
| `grow r=` | top by growth since the previous output (>= 64 KB) |
| `cyc r=` | with a mark tag: top by growth since the previous sample with **the same tag** (`since=#M`); at `<prefix>-empty`, `gc-after`, `home` this is growth that survives removal |
| `inbig r=` | sites holding reservations / views whose base lies inside the previous output's largest free block: they split it |
| `free r=` | the three largest free blocks and the allocation just below and above each (a tracked site, `image <module>`, or `untracked private|map <base>`) |

## 7. How to read

`node tools/memory-report.ts <ts3native.log>`, section "native call sites": totals first -> last; top live; per repeated mark tag the per-cycle growth of each site, `SURVIVES` when every step grew > 0.25 MB at an `-empty` / `gc-after` / `home` tag; "held through `<prefix>` removal" = top live sites that kept >= 90 % from `<prefix>-full` to the next `<prefix>-empty`; `inbig` and the free-block neighbours of the last sample. Self-check: `node tools/memory-report.ts --self-check`.

Questions the queue answers: per live cycle, which heap / view / reserve sites grow from `live-empty` to `live-empty` (the -16 MB); whose views make `map` 112 -> 134 MB (DXVK `MemoryFilePool` [inferred] or the exe's own `CreateFileMappingA` at `0xEA4E9F`, docs/66 §3.2); per trip, which sites grow `home` to `home` (the -80 MB); which sites' regions land inside the big free block.

## 8. Settings (`[MemDiag]`)

`AllocSites=0` (1 = on; needs `Light=1` or `Enabled=1`), `AllocSitesSample=64` (1-4096, rounded down to a power of two), `AllocSitesTop=12` (4-32), `AllocSitesHeap=1`, `AllocSitesVirtual=1` (0 skips a hook group). A group installs whole or not at all (free before alloc, unmap before map, all four EA entries); with no complete group the partial hooks are removed.

## 9. Host selftest (probe-safety rule)

`allocsites-test.h`: four generated dummy entries with the game's exact prologue (then `pop edi; pop esi; jmp body`, same argument counts and `ret N`) over a fake heap object; the chained case on a `jmp` entry and a refused entry with other bytes; the real ntdll stubs of the selftest process with VirtualAlloc / VirtualFree / MapViewOfFile / UnmapViewOfFile(Ex) round trips, then the original bytes back. Checks: 20,000 x 200 B estimate within 25 % and exactly 0 after the frees; 256 KB exact; memalign / realloc move the record; reentrancy flag; 4 threads x 20,000 mixed alloc / free leave 0 records, 0 drops, 0 busy; output rows (`sum`, `live`, `grow`, `cyc since=`, `free`) under the 512-byte log line; Apex stand-down installs nothing.

## 10. Gaps

- G1: EA debug-layer allocations (`0x4E82E0` / `0x4E84A0` paths) are not seen [inferred inactive].
- G2: `NtAllocateVirtualMemoryEx` / `NtMapViewOfSectionEx` not hooked [inferred unused]. Closed in 0.36.86 (§14).
- G3: regions made before ts3native loads (exe image setup, early DLLs) are untracked (`untracked` neighbours, `rgnMiss`).
- G4: `from` comes from a stack scan (stale return addresses possible); no deeper chain.
- G5: heap live bytes are an estimate (address sampling; selftest ±25 % at 20,000 blocks, tighter with more); heap growth inside existing EA cores does not move the largest free block by itself: the `virtual` rows with `at` in `0x4E48F0` / `0x4E50F0` and their `from` are the heap's address-space cost.
- G6: game cost not measured in the game (selftest numbers only); the call rate per second is unknown.
- G7: when Apex Radiance is loaded the tool is off, so Apex setups need Apex's own memory analysis (docs/187). 0.36.260-memdiag3: `[MemDiag] AllocSitesWithApex=1` runs it next to Apex (no shared site in Apex 3.2.3; docs/190 §5.6), not run yet.

## 11. First run failed: hook frame scan faulted (0.36.82, fixed in 0.36.84)

Queue `alloc-sites` 2026-10-26 01:02-01:10 UTC, asi `game-copy/ts3native.asi.allocsites` (0.36.82), all 3 jobs failed; no WER dump, no EA xcpt file, TS3W.exe still running at each stop (no game crash):

| Job | Failure | Probe log |
|---|---|---|
| bp-cc-allocsites | probe server exit after ~60 s | `frida.attach('TS3W.exe')`: `frida.ProtocolError: timeout was reached` |
| bp-travel-allocsites | `agent answering: not reached within 300 s` | attached; then nothing: one `callsites min 1` line, no further log, GPU 3D 0 %, TS3W ~21 % CPU (hang before the main menu) |
| bp-bb-allocsites | probe server exit after ~60 s | same attach timeout |

Evidence [verified, the three `ts3native.log` files in `probe-out/queue/alloc-sites-2026-10-26-*`]: all 16 first-chance log slots filled with `C0000005` at `ts3native+0x117D5`, read of `base - 5`: twice on the main thread at start-up (`0x70E9FFFB` = ts3native's own image base - 5) and 14 times on a second thread (`0x6813FFFB` = image base `0x68140000` - 5; that thread starts while Frida attaches, `0x68140000` = Frida's agent DLL [inferred: not named in the log]). `+0x117D5` = `mov dl,[ecx-5]` in `AsCallBefore`, called from `AsCodeRetSafe` in `AsAnyFrames`: the reservation / view hooks scan the stack for return addresses, and a stack word equal to an image base (an HMODULE in a local, the loader's and Frida's injector's normal state) passed the "in an image" test and read the 6 bytes before it, an unmapped page. The `__except` caught every fault, so the code looked safe in the host selftest. Earlier asis on the same jobs that day: 3 first-chance AVs per run, none in this path.

Cause [inferred from the above; not stepped live]: a first-chance AV raised inside `NtAllocateVirtualMemory` / `NtMapViewOfSection` on Frida's injected thread, while it loads the agent (loader lock held) and Gum installs its exception dispatcher hook, goes through Frida's exceptor before our `__except`; the agent never comes up (attach timeout x2) or a thread stays stuck holding the loader lock (travel: the game hung before the menu). Any first-chance exception in a hook on ntdll is unsafe for the same reason (callers include the loader, heap code under its lock, and the exception dispatcher itself).

Fix (0.36.84, `allocsites.cpp`): the hooks never fault. `AsModOf` caches each image's executable-section range (from the section headers, read once per module); a non-exe stack word counts as a return address only when the 8 bytes before it lie in that range (`AsCodeRetSafe(v, cLo, cHi)`), so headers and image bases are never read past. The `__try` stays as a backstop. Host selftest `allocsites-test.h` "no first-chance AV": reservations from a function whose locals hold a DLL base, a header address, the exe base and an exe header address, with a vectored handler counting access violations on the thread: 0 on 0.36.84; the 0.36.82 code fails it (`g_astAvs == 0`, reproduced). New asi `game-copy/ts3native.asi.allocsites2` (the old one is kept); queues `alloc-sites`, `cc-build-items`, `cc-script`, `cc-makeup-presets`, `cc-lots` and `cc-build-stress` job `bp-cc-edittown` point at it.

If the rerun still fails at attach: fallback build = EA heap hooks only (`AllocSitesVirtual=0`, the ntdll hooks off; the `virtual` / `map` rows are then missing, the heap rows stay).

## 12. The two biggest `virtual` rows: EA heap cores and Boehm sections (static, 2026-10-26)

Data: `cc-build-stress` job `bp-cc-edittown` 2026-10-26 01:43 UTC, asi `ts3native.asi.allocsites2` (0.36.84, `native/ts3native/out-allocsites2`), `probe-out/queue/cc-build-stress-2026-10-26-bp-cc-edittown-0143-ts3native.log`. Loaded: S3PP (`Sims3Performance.asi`, `AllocatorArenaTuning=1`; its log says "changed TS3 arenas from 16/4 MB to 4/1 MB"), S3SS, DXVK; Apex not loaded; `[Alloc]` cache off ("alloc cache off (Enabled=0)"). Static checks with `dumpbin /disasm` on `game-copy/bin/TS3W.exe` (byte-identical to the Steam exe) and on the asi. Only Error 12 (address space) is in scope here.

### 12.1 `from=ts3native.asi+0x13FCF` is our own hook, not the caller [verified]

- `+0x13FCF` (VA `0x10013FCF`, image base `0x10000000`) is the return address after the call through `g_asMalloc` in `AsMallocHook` (`0x10013FC0`: 2 stack arguments, `ret 8`, chunk-size test against 64 KB after the call). `+0x14037` is the same spot in `AsMemalignHook` (`0x10014020`, 4 arguments). Both are the allocsites detours of `0x4E69D0` / `0x4E6A20` (§2). The `[Alloc]` cache was off; ArenaFix and S3PP patch no entry point.
- Why: `AsAnyFrames` takes the first non-system frame (`0x4E4E87`, inside the EA wrapper range), skips further frames inside that range and stops at the first frame in another module, which is our hook. The game's real caller is one word further up (the hook's own return address). The same applies to the `at=0x004E5130` rows (direct blocks). Fix built in 0.36.85 (asi `game-copy/ts3native.asi.allocsites3`, `native/ts3native/out-allocsites3`, not run): `AsAnyFrames` skips every return address inside ts3native's own image (range from `GetModuleHandleExW` at install; this also drops stale words such as `+0x1168A` below), and `AsHeapFrames` treats a caller inside our image like an allocator wrapper (the next exe frame becomes `at=`). The code-section check of §11 stays. Host selftest: game caller -> dummy hook (our range) -> dummy core (allocator range) -> `VirtualAlloc` records `at=` core, `from=` the caller (fails on 0.36.84 with `from=` the hook). Queues `alloc-sites`, `cc-build-items`, `steady-state`, `cc-script` and job `bp-cc-edittown` of `cc-build-stress` point at it (`cc-lots` left on allocsites2: running).
- `+0x1168A` (2 cores, 32 MB) follows a call to `SuspendThread` inside a ts3native function: a stale stack word (G4), not a real frame [inferred].

### 12.2 `at=0x004E4E87`: EA GeneralAllocator new core [verified]

- `0x4E4E30` = "new core" (thiscall, argument = bytes needed). Size = max(request rounded to the `+0x4D4` page size, heap field `+0x4D8`), rounded up to 64 KB. One `VirtualAlloc(NULL, size, MEM_RESERVE, PAGE_READWRITE)` (call at `0x4E4E81`, returns to `0x4E4E87`), then `MEM_COMMIT` of max(request, `+0x4DC`) capped at the core size. If the reserve fails: one `MEM_RESERVE|MEM_COMMIT` of exactly the request, with `MEM_TOP_DOWN` only when byte `+0x498` is set. Address hint: always NULL (the OS places it bottom-up). Callers: `0x4E5476` (heap growth) and `0x4E5660` only.
- `+0x4D8` / `+0x4DC` / `+0x498` come from the constructor `0x4E5AA0` (16 MB / 4 MB / 0, stores at `0x4E5BDA` / `0x4E5BE4` / `0x4E5B8A`) or from `SetOption` `0x4E48F0` options 4 / 5 / 11 (option 4 with value 0 restores 16 MB at `0x4E49E5`, option 5 with 0 restores 4 MB at `0x4E4A1C`). S3PP rewrites those four 16 / 4 MB immediates to 4 / 1 MB. Direct `SetOption` calls: `0x89F56A` (option 3 = 0), `0x89F579` (option `0x3E8`: out of range, no-op), `0xD7F319` (option 3 = 0, Mono heap) [verified, call scan]; indirect calls not scanned. So today the core size is EA code with S3PP's immediates; ts3native, S3SS and Apex set none of it (`tools/hook-sites.ts`, `docs/data/apex-hooks.csv`: nobody touches `0x4E4E30`).
- Sizes in this run: 33 Malloc-path cores = 492 MB (mean 14.9 MB), 4 Memalign-path cores = 64 MB, 2 more = 32 MB. The last two groups are exactly 16 MB each, so at least those heaps still use the 16 MB default or asked for >= 16 MB [inferred; per-core sizes are not in the log]. Open: read `+0x4D8` of each heap live (one probe read) to see whether S3PP's 4 MB reached every heap.
- Core life: the core header is in the core (`+4` committed, `+8` reserved, `+0x10` optional free callback, `+0x14` its context), on a circular list at heap `+0x448` (next at `+0x18`); `0x4E4840` finds a pointer's core by walking that list. Cores are released only at heap shutdown (`0x4E4870`, one caller `0x4E5DBF`: the core's callback if set, else `VirtualFree(MEM_RELEASE)`); trim `0x4E4F90` only decommits core tops (`MEM_DECOMMIT` at `0x4E504C`). So the 492 MB reserve stays for the whole session. Each core ends in a fencepost (`0x4E4BC0`), so neighbouring cores are never merged [inferred from the fencepost writes].

### 12.3 Short-lived big blocks: `at=0x004E5130` [verified]

`0x4E50F0` (flag bit 3 or large requests) makes one `MEM_RESERVE|MEM_COMMIT` per block, `MEM_TOP_DOWN` added when `+0x498` is set; release at `0x4E5306` (`MEM_RELEASE`). In this run: 1,445.6 MB allocated and 1,267.7 MB freed by the second sample (125 live, 178 MB). This is the churn row; the blocks land bottom-up between the permanent cores and the Boehm sections.

### 12.4 `at=0x00D70916`: Boehm heap sections [verified]

`GC_win32_get_mem` `0xE4EEF0` calls EA `0xD70900`: `VirtualAlloc(NULL, bytes, 0x203000, PAGE_EXECUTE_READWRITE)`. `0x203000` = `MEM_WRITE_WATCH|MEM_RESERVE|MEM_COMMIT`, not `MEM_TOP_DOWN` (`0x100000`); docs/08 row 4 is wrong there. Sizes: ts3native `[GC] InitialHeapMB=352` (first expansion) and `GrowthStepMB=32` (`0xE4A1D8`); header chunks come from `GC_scratch_alloc` through the same call (docs/198). This run: 81 reservations / 372 MB at load = the 352 MB block + 80 small ones (~20 MB), later +4 reservations / +7.5 MB; no 32 MB growth step ran [inferred from the totals]. Size: ours; flags and address hint: EA code; no other mod hooks `0xD70900`.

### 12.5 One arena per allocator, top-down for transient blocks: feasibility [design only]

| Idea | Hook point | What the allocator assumes | Risk / conflicts | Owner |
|---|---|---|---|---|
| EA cores from one pre-reserved region | retarget the `VirtualAlloc` call at `0x4E4E81` (6-byte indirect call) to a sub-allocator; the commits (`0x4E4EB4`, `0x4E5536`) and the trim decommit (`0x4E504C`) work on sub-ranges unchanged | cores are independent (fenceposts, list walk); released only at shutdown with `MEM_RELEASE` on the base, which fails on a sub-range (set the core's `+0x10` free callback or retarget `0x4E48AB`) | small gain: cores are already permanent and placed bottom-up; the region must be sized up front (~600 MB here) and needs a fallback to the game's call when full; S3PP's immediates keep working | Apex (allocator area, docs/191 Ownership) |
| Big direct blocks top-down | EA's own switch: byte `+0x498` (option 11) per heap, or the constructor store at `0x4E5B8A` | `0x4E50F0` and the core fallback already support it | Apex hooks `0x4E5306` (`AllocMmapFreeCall`, big-block free on a worker) and trim `0x4E4F90`: same blocks, must be agreed; `MEM_TOP_DOWN` search cost with many regions [community-reported, not measured]; blocks then sit next to DXVK / driver regions at the top | Apex |
| Boehm sections from one region | retarget `0xD70910` (the single EA call) to a sub-allocator over one region reserved with `MEM_WRITE_WATCH` (a reserve-time flag; `GetWriteWatch` on sub-ranges works) | sections are never returned (docs/66 §3.3), `GC_heap_bases` holds at most `0x300`, headers are separate | small gain: the 352 MB is already one block; only ~84 small header chunks would move; `[GC]` growth must stay inside the region | ours (Mono / GC) |

Verdict [inferred]: the address space in this run is not fragmented (largest free 1,548.8 MB at load, 1,451.3 MB at sample #2; the next free block is 16 MB), so this log does not justify either arena. The lever that matches the churn is top-down placement of EA direct blocks (one byte per heap, EA's own code path); that is Apex's area, so it goes to them as a proposal with this data, not a build of ours. First fix the attribution (§12.1) and rerun, so the real callers of the 33 cores and of the direct-block churn are known.

## 13. Results 2026-10-26 [measured]

Queue `alloc-sites` 2026-10-26 from 03:24 UTC, asi `game-copy/ts3native.asi.allocsites3` (ts3native 0.36.85, build e4a6f7f3), S3SS + S3PP + DXVK, Apex not loaded. Logs `probe-out/queue/alloc-sites-2026-10-26-<job>-<hhmm>-ts3native.log`; read with `tools/memory-report.ts` (incl. `--steady live|bb`) plus scripts over the `diag: mem` / `allocsites:` lines. Site names come from the EA allocation tag strings in the calling function (`dumpbin /disasm` on `game-copy/bin/TS3W.exe`) [verified static]; the `memory-report.ts` symbol guesses for these rows (`CopyFolderRecursive+0x1be7?` and similar) are far from their symbols and wrong.

| Job | Result | Marks | Largest free |
|---|---|---|---|
| `bp-cc-allocsites` (0324) | PASS | 3 x live-full / live-empty, 3 x inv-full / inv-empty, each with gc-before / gc-after | 1,597 -> 1,237 MB |
| `bp-travel-allocsites` (0346) | FAIL (1 of 3 trips: one away + one home mark) | base, away, home, gc-after x2 | 1,533 -> 1,464 MB (base 1,485 -> 1,464 after one trip) |
| `bp-bb-allocsites` (0351) | PASS | 5 x bb-full / bb-empty, 3 x bbp-full / bbp-empty | 1,509 -> 1,198 MB |

### 13.1 Attribution works [measured]

- On line in all three jobs: `EA heap hooks 4/4, ntdll hooks 5` (`NtAllocateVirtualMemory`, `NtFreeVirtualMemory`, `NtMapViewOfSection`, `NtUnmapViewOfSection`, `NtUnmapViewOfSectionEx`).
- `drops=0 busy=0 nofree=0` at every sample of every job.
- 0 rows with `at=` or `from=` inside ts3native (the §12.1 fix holds): heap `at=` are game call sites, and the EA core rows (`at=0x004E4E87`) name the heap user in `from=`.
- Limits seen: the site table is full by the end (CC 4,092 of 4,096, BB 4,096 at #53), so later sites go to the `overflow` row (7.5-9.4 MB); one virtual hook call took up to 51.5 ms (`vMaxUs`, CC load); the `inbig` row tests the region base only, so a region that starts below the window and overlaps it is missed (CC #24, the 128 MB `nvoglv32` reserve).

### 13.2 Names of the sites that matter [verified static unless marked]

| Site (`at` / `from`) | Name (EA tag string or code) | Kind |
|---|---|---|
| `0x4DEC17` / `0x4DEE8C` | `Services/EAIO/EAStreamMemory/data`, `.../ptr` (resource data read into memory streams) | heap user; `from=` of 96 MB of EA cores |
| `0x72D62B` / `0x72D6C9` | `ResourceLoad/PackedStream/DecompressedRecordData` (package record decompression) | `from=` of 80 + 16 MB of EA cores |
| `0x57A593` / `0x578F6D` | `Animation/ClipResource` (+ `ClipIkInfo`); EASTL vector in the clip code | `from=` of 64 + 48 MB of EA cores |
| `0xD71510` (call `0xD71553`, ret `0xD71558`) | core supplier of the EA sub-heap **`Scripting/Simulator`** (`[0x11EE51C]`, created in Mono init at `0xD7F306`, callback set at `0xD7F325`): rounds the request up to a power of two, at least 1 MB, and takes it from the main EA heap `[0x11CB864]` | its cores are the `virtual at=0x004E5130 from=0x00D71558` direct blocks (74 -> 98 MB, n=91) |
| `0xD70A10` (ret `0xD70A22`) via `0xE9A3B3` | Mono runtime malloc wrapper (heap `[0x11D4BC8]`, 102 call sites in `0xE4xxxx`) | heap, 100-107 MB, n ~13,000 |
| `0x5C7906` | `CAS/GeometryResource` | heap |
| `0x5A2FFF` | `CAS/BlendGeometryResource/BlendRegionData` | heap |
| `0x8B452D` / `0x8B6022` | `Audio/ResourceBinary/Data`, `Audio/ResourceBinarySampleData` | heap + direct blocks |
| `0x60D91C` | `Renderer/Texture` (`DDS_UploadLevels`) | `from=` of DXVK views |
| `0x00D70916` | Boehm sections (§12.4) | virtual |
| `nvoglv32.dll+0x8ACB7E` | NVIDIA's 32-bit Vulkan/OpenGL driver under DXVK, 128 MB reserves [community: `nvoglv32.dll` is the NVIDIA Vulkan ICD; inferred: a mapped DXVK memory chunk] | virtual |
| `d3d9.dll+0x28FEF2`, `ucrtbase.dll+0x2DF5F < d3d9.dll+0x58B80C`, `d3d9.dll+0x11F665` | DXVK views (texture data) and DXVK 16 MB reserves | map / virtual |

The `miRes` / `miCom` fields of `diag: mem` count every 64 / 128 MB private region as mimalloc (`userdiag.cpp`); the 128 MB regions are `nvoglv32` reserves [measured: the `miRes` 192 -> 320 step is the same sample as the `nvoglv32` +128 MB row, CC #24 and BB #53]. So the earlier "mimalloc commit +17 MB" lead is at least partly the driver.

### 13.3 Growth that survives removal and GC [measured]

CC, largest free at `live-empty`: 1,447 -> 1,399 -> 1,231 MB (-48, -168); at `inv-empty`: 1,431 -> 1,269 -> 1,237. Owners from the top-12 live rows (lower bounds), load -> end of CC (big -360 MB):

| Owner | Load -> end | Per live cycle (live-empty to live-empty) |
|---|---|---|
| EA heap cores (`0x4E4E87`, permanent, §12.2) | 192 -> 288 MB (+96) | +32, +16 (`from` EAStreamMemory); totals per mark 240 -> 272 -> 288 (steps shrink) |
| `nvoglv32` 128 MB reserves | 128 -> 256 (+128, once, cycle 2 inv-empty) | 0, +128 |
| Boehm sections | 374.5 -> 409.5 (+35) | 0, +16.75 |
| `Scripting/Simulator` heap cores (direct blocks from `0xD71558`) | 74 -> 98 (+24) | +3, +2 |
| DXVK `d3d9.dll+0x11F665` | +15.8 once (cycle 1) | +15.8, 0 |
| untracked private (4096 - free - img - map - tracked virtual) | 306 -> 370 (+64, all before the first mark) | 0, 0 |
| DXVK views (tracked) | 114.5 -> 89.5 (-25) | see §13.4 |

Heap sites (inside existing cores; they cost address space only when they push a heap to a new core) that grow at every `-empty` mark: `CAS/GeometryResource` `0x5C7906` +8.5, +2.5 (live-empty) and +7.0, +5.5 MB (inv-empty), live 30-34 MB; `EAStreamMemory` `0x4DEC17` +13.4, +5.7 (inv-empty), live ~19.5 MB (it also frees: 62 MB summed over 11 gc-after steps); `BlendRegionData` `0x5A2FFF` +10.7 then +0 (fills once); `Scripting/Simulator` +3, +2. Only `CAS/GeometryResource` grows every cycle without settling: ~5 MB per cycle, a leak-or-cache candidate [inferred: 3 cycles are too few to tell].

BB: the 5 Buy cycles lose nothing (big 1,374 MB flat from bb-full #1 to bb-empty #5; `--steady bb`: big and priv flat); the 3 Build cycles lose -32, -16, -16 MB, all to **untracked** private 16 MB blocks placed at the bottom of the largest free block (`0xA9EC0000`, `0xABEC0000`, `0xACEC0000`; two released again by #51). The end drop (-144 MB at #53, after the cycles) is the second `nvoglv32` 128 MB reserve plus EA cores (`ClipResource` +32, +16, `EAStreamMemory` +16).

### 13.4 Map views [measured]

The `map` 112 -> 134 MB per cycle of docs/200 does not repeat: CC `map` swings 78-152 MB with no trend (144 at load, 127 at the end; tracked views 114.5 -> 89.5 MB). All tracked views are DXVK's (`at=d3d9.dll+0x28FEF2`, `0x28FF30`), called from `Renderer/Texture` `0x60D91C`, S3SS `asi+0x322EF`, `SceneQuadtree` `0x7055F1` and unattributed threads. Views come and go: per site the gc-after cycle sums are 39-57 MB while live stays 14-44 MB. A bounded cache [inferred: DXVK's memory-mapped texture data in 32-bit]. Untracked views: ~30-38 MB constant in CC; in BB one ~88 MB untracked view (`0xA4800000`, from bb-full #1 to #51) sat right below the largest free block.

### 13.5 Who splits the largest free block [measured]

The largest free block is the top gap (below `OPENGL32.dll` / driver regions); every other free block is under 9 MB (CC: free 1,312 vs big 1,237 at the end; 113 MB of small holes at load, 75 at the end). So every new reservation over ~8 MB is cut from its bottom edge: bottom-up placement, not scattered holes.

| Run | Sample | Splitter |
|---|---|---|
| CC | #2, #15, #28 | EA cores `from=EAStreamMemory` 32, 32, 16 MB |
| CC | #5 | EA core `from=PackedStream` 16 MB |
| CC | #9 | DXVK `d3d9.dll+0x11F665` 15.8 MB |
| CC | #24 | `nvoglv32` 128 MB (missed by `inbig`, base below the window; seen as the new `below` neighbour) |
| CC | #28 | Boehm 16 MB |
| CC | #15, #29, #31, #32 | DXVK views 1.4-6.9 MB (given back by the next sample) |
| BB | #3 | untracked map ~88 MB |
| BB | #36, #42, #48 | untracked private 16-32 MB |
| BB | #53 | `nvoglv32` 128 MB |
| travel | #2, #17 | untracked private 16 MB |

### 13.6 Travel round trip [measured, 1 trip of 3]

Largest free: base 1,485, away 1,501, home 1,497, then -33 at the save after home (1,464, untracked private 16 MB at `0xA3520000` below it), gc-after 1,464. One trip = -21 MB, not the -80 MB of docs/191; the trip itself gave space back, the loss came at the next save. Sites that grew from gc-after #6 to gc-after #20: `Scripting/Simulator` cores +13.05 MB (13 new 1 MB blocks), Mono runtime malloc +10.5 MB (heap, inside those cores [inferred]), `Audio/ResourceBinary` direct blocks +7.7 MB, DXVK views from `Renderer/Texture` +9.1, Boehm +3.25. The Mono sub-heap growth per world switch is the one lead in our area; it needs the 3-trip rerun to show per-trip repetition.

### 13.7 Verdict

Not a per-object leak and not hole fragmentation: the address space goes to **permanent reservations that grow to each new peak**, all placed bottom-up into the one big gap: EA heap cores (EA code; peaks from resource streams, decompression and animation clips; +96 MB in CC), the NVIDIA/DXVK 128 MB driver reserve (+128 once per run, the largest single step), Boehm sections (+35), the `Scripting/Simulator` Mono sub-heap (+24 in CC, +13 per trip) and untracked 16 MB private blocks (Build mode, saves). Heap-level leak candidate: `CAS/GeometryResource` ~5 MB per live cycle [inferred]. Owners: EA cores = Apex area (allocator); `nvoglv32` = DXVK / driver (third party); Boehm + `Scripting/Simulator` = ours (Mono / GC); untracked = unknown.

Next (measurement before fixes):
1. Tool: name the untracked regions (at each sample, VirtualQuery the untracked private / map regions >= 1 MB: base, size, protect, `AllocationBase`) and hook `NtAllocateVirtualMemoryEx` / `NtMapViewOfSectionEx` (gap G2); `inbig` by overlap instead of base; site table 16,384; keep `nvoglv32` regions out of `miRes`.
2. Rerun the travel job (3 trips) for the per-trip Mono sub-heap growth.
3. Fix candidates to measure: DXVK chunk / mapped-memory settings in `dxvk.conf` for the 128 MB driver reserve [inferred knob, not checked]; Boehm `InitialHeapMB=160` (docs/199 §7, parked); EA core growth to Apex as data (cores are never released, §12.2).

## 14. v4 build: untracked allocations, Ex hooks (0.36.86, built, not run)

Goal: the next run names everything that cuts the largest free block, mainly the untracked 16-32 MB private blocks of §13.3 / §13.5 (Build mode, the save after travel) and the regions `inbig` missed. Code `native/ts3native/allocsites.cpp`, selftest `allocsites-test.h`; asi `game-copy/ts3native.asi.allocsites4` (`native/ts3native/out-allocsites4`); queue `tools/harness/queues/alloc-sites4.json`; ledger §364. Still a TEST-RUN TOOL, off by default; no new ini key, no counter slots (log lines only).

### 14.1 Changes

| # | Change | Why |
|---|---|---|
| 1 | **Untracked allocations.** The free-block walk now also groups regions by `AllocationBase` and keeps every allocation >= 1 MB with no record at its base. Per output: one `usum` row (count / MB by type, `new`, `more` = past the 1,024-entry list) and up to 64 `untr` rows | §13: 306 -> 370 MB untracked private, 16 MB blocks splitting the big block with no site |
| 2 | **`NtAllocateVirtualMemoryEx` / `NtMapViewOfSectionEx`** detoured like the other stubs (`B8` check, chained `jmp`, Ex alloc only after free + alloc are in, Ex map only after the unmaps); missing exports (before Windows 10 1803) are skipped. The on line says `ntdll hooks 7` on Windows 11 | gap G2 |
| 3 | **MEM_COMMIT at a NULL base** (no `MEM_RESERVE` flag) is recorded as a new reserve, in both alloc hooks. `VirtualAlloc(NULL, n, MEM_COMMIT, ...)` reserves implicitly, and the old hook kept only calls with `MEM_RESERVE` [inferred: a likely source of the untracked blocks; the run will tell]. `*BaseAddress` is read before the call only from this thread's stack or a committed readable page (a hook never faults, §11). A commit inside an existing reserve is not a new region (selftest) | §13 untracked blocks |
| 4 | **Site table 4,096 -> 16,384**, plus `siteFull=` in the `sum` row (records that went to the overflow row because the table or its 64-slot probe was full). Eviction was not built: a site index is held by every live block and region record, so evicting one would move their bytes. Cost: tables ~4.6 -> ~8 MB reserved and committed at install, statics +~0.3 MB, all only with `AllocSites=1` | CC filled 4,092 of 4,096, BB 4,096 (§13.1) |
| 5 | **`inbig` by overlap**: a record counts when `[base, base+size)` overlaps the previous window, not only when its base lies inside | the 128 MB `nvoglv32` reserve at CC #24 (§13.1) |
| 6 | **GPU driver regions labelled, out of `miRes`.** Rodney 2026-10-26: the `nvoglv32` 128 MB reserves (2 x 128 per run) are accepted cost (DXVK saves ~1.75 GB); no driver-specific investigation. A module whose export directory names `nvoglv32.dll`, `amdvlk32.dll` or `igvk32.dll` is a driver; a region whose first frame lies in one gets a flag in its record, and the `diag: mem` walk (`userdiag.cpp`) leaves a 64 / 128 MB private allocation with that flag out of `miRes` / `miCom` (only while AllocSites is on: without the hooks nobody knows the owner, and S3SS mimalloc also makes 128 MB arenas). `memory-report.ts` labels `nvoglv32.dll+...` (and the other two) `(driver)` in every row and neighbour | §13.2: the `miRes` 192 -> 320 jump was the driver |
| 7 | `tools/memory-report.ts`: parses `usum` / `untr`; prints the last totals, every untracked allocation that was new or at the window (first sighting, owner, `inside the previous largest free block` / `at the largest free block`), and the 8 largest others. TS3W.exe addresses are named only from the §13.2 table (EA tag strings, exact return addresses); the nearest-symbol guess (`ts3w-1.67.symbols.csv`) is gone, it was wrong for these rows | §13 |

### 14.2 Output

```
allocsites: #36 mark ev=bbp-empty usum private=41/322.5MB map=3/34.0MB image=118/402.1MB new=1 more=0
allocsites: #36 mark ev=bbp-empty untr r=7 type=private base=0xA9EC0000 MB=16.00 comMB=16.00 prot=0x4 aprot=0x4 owner=? first=#36 new=1 win=in
```

| Field | Meaning |
|---|---|
| `base`, `MB` | the whole allocation (`AllocationBase` and the summed size of its regions), so `base` = AllocationBase |
| `comMB`, `prot`, `aprot` | committed bytes; `Protect` of the first committed region (incl. `PAGE_GUARD` 0x100); `AllocationProtect` |
| `owner` | image / view: the mapped file name (file only, docs/188 privacy) or `pagefile-section`; private: `ts3native-allocsites` (our tables, made before the hooks), `ntheap` (an NT heap's first segment, `GetProcessHeaps`), `stack?` (has a guard page [inferred: a thread stack]), else `?` |
| `first`, `new` | the output that first listed this base + size; `new=1` = not in the previous output (never on the first output) |
| `win` | `in` = overlaps the previous output's largest free block (it split it); `edge` = touches the current largest free block; `-` |

Rows shown: `win` not `-`, or new, or private / map without a guard page. Images and stacks only when new or at the window (they are counted in `usum`). The walk is the one the `free` rows already did (one `VirtualQuery` pass) plus `K32GetMappedFileNameW` per printed image / view row; `GetProcessHeaps` once.

### 14.3 Host selftest (0.36.86, `out-allocsites4\selftest.exe`) [verified]

- `ntdll hooks 7` on Windows 11 x64 WoW64: `VirtualAlloc2` 8 MB reserve and `MapViewOfFile3` 4 MB view each recorded at their caller and gone after release / unmap.
- `VirtualAlloc(NULL, 16 MB, MEM_COMMIT)` recorded exactly; a 64 KB commit inside a recorded 8 MB reserve leaves the region count unchanged.
- `inbig`: a reserve whose base lies 4 MB below the window shows as `inbig r=1`.
- `untr`: a 4 MB reserve made with the reentrancy flag set (no record) after #10 shows at #11 as `type=private ... MB=4.00 comMB=0.00 ... owner=? first=#11 new=1 win=in`; the tool's own tables show as `owner=ts3native-allocsites first=#7 new=0`; `siteFull=0`; every line < 510 bytes.
- Driver flag: `version.dll` stands in for a driver (its export name added to the list in the test): its code range is a driver range, the selftest exe's is not; a 64 MB private reserve counts in `miRes` without the flag and not with it.
- Cost (two runs): EA heap pair +5.1 / +6.4 ns at 1 in 64, +95 / +118 ns every block sampled; reserve + release +0.28 / +0.73 us. Same range as §5.

### 14.4 Queue `alloc-sites4.json`

Copy of `alloc-sites.json` on `allocsites4`, plus `logRequire`: `ntdll hooks 7`, the two Ex `OK` lines, `usum` rows at 3 x `live-empty`, 3 x `home`, 3 x `bbp-empty`. **Dependency:** `bp-travel-allocsites` needs the `clear_recovery` change on branch `fix/memchain-ccb-travel` (commit `79376459`: `save-matrix.ts` passes `clear_recovery` for trips 2+ to a vacation world, ts3agent `TravelHandlers.cs` drops the WentToLocation buff); without it trip 2 fails as on 2026-10-26. That branch is not merged here. Dry run 2026-10-26: 3 of 3 jobs listed, asi version 0.36.86 read back; a real start would refuse at that moment for state outside this queue (a content stash manifest from the running queue, `cc-2k` stashed): run `queue.ts --restore-content` once the running queue ends.

### 14.5 Unverified

- Ex stubs in the game process: the game machine is the same Windows 11 as the selftest, so `B8` stubs are expected [inferred]; a `SKIP alloc sites NtAllocateVirtualMemoryEx` line fails the job (`logForbid`).
- Whether the untracked 16 MB blocks come from MEM_COMMIT-at-NULL, the Ex entries, or neither (kernel / WoW64-side allocations never pass the 32-bit stubs [inferred]); the `untr` rows at the Build cycles and the post-travel save say which: a block that now has a site was one of the hook gaps, one still listed as `untr` is outside the 32-bit stubs.
- Placeholders (`MEM_RESERVE_PLACEHOLDER` split / replaced): recorded as one reserve at their base; split pieces stay untracked (shortcut, no 32-bit user known).
- Owner guesses `stack?` and `ntheap` are heuristics (a fully committed stack has no guard page; NT heap segments after the first are `?`).
- Driver detection by export name: `nvoglv32.dll` exporting under that name is [inferred]; if not, its regions stay in `miRes` as before (the report still labels them from the module file name).
- Game cost of the larger tables and the extra walk work per sample: not measured in the game.

## 15. Load hang with AllocSites on: TLS flag deadlock (0.36.88)

Symptom [measured]: job `bp-cc-steady-fresh` 2026-10-26 ~04:24-04:28 UTC (0.36.85, `allocsites3`) hung at load: TS3W.exe at 0.2 % CPU, `load_save` never answered, no stack captured. A wait at ~0 % CPU is a lock wait, not a spin (every lock of our own is a bounded try-lock, §5).

### 15.1 Lock-order audit of `allocsites.cpp`

| Lock / call | Where | Held while | Verdict |
|---|---|---|---|
| stripe try-locks `g_asStripeLock[256]` | `AsBlockAdd` / `AsBlockRemove` | table probe only, no call | safe: bounded (~1 ms, then `busy` + dropped record) |
| `g_asRgnLock` try-lock | `AsRgnAdd` / `AsRgnRemove` / `AsIsDriverRegion` / output copy | table probe or `memcpy`; `AsInDrv` (pure read) | safe, bounded |
| `g_asOutLock` (SRW) | `AsAfterSample` only (sample thread) | `Log`, `VirtualQuery` walk, `GetModuleFileNameW`, `K32GetMappedFileNameW`, `GetProcessHeaps` | safe: no hook waits on it; the hooks on that thread skip (reentrancy flag) |
| `VirtualQuery`, `GetProcessId`, `QueryPerformanceCounter` | hooks (`AsNewRegion`, `AsModOf`, `AsLiveImage`, `AsSelf`, map hooks, `AsRgnAdd`) | | safe: system calls without user-mode locks |
| module cache | `AsModOf` / `AsDropMod` | lock-free rows, headers read under `__try` | safe: no loader API in a hook (`GetModuleHandleExW` only at install) |
| frame scan | `AsHeapFrames` / `AsAnyFrames` | reads the own stack + code pages checked by `VirtualQuery` (§11, docs/67 §21) | safe |
| **`TlsGetValue` / `TlsSetValue` (reentrancy flag)** | `AsEnter` / `AsLeave`, every recording hook | | **deadlock**, below |

The game's `TlsAlloc` indexes are >= 64 (docs/130 G-B1: 102 / 103), so ours is too. `TlsSetValue` on an index >= 64, on a thread's first use, allocates the thread's TLS expansion array from the **process heap** under the **PEB lock** [verified 2026-10-26, scratch probe on this Windows 11: `TEB+0xF94` 0 -> heap pointer after the call; the call blocks while another thread holds `RtlAcquirePebLock`, and blocks while another thread holds `HeapLock(GetProcessHeap())`]. Our hooks run inside `NtAllocateVirtualMemory`, which the NT heap calls with its heap lock held (segment growth, blocks above the virtual threshold). So:

- thread A: holds the process heap lock, grows the heap -> `NtAllocateVirtualMemory` hook -> first `TlsSetValue` -> waits for the PEB lock;
- thread B: any thread's first `TlsSetValue` on a high index (game, Mono, DXVK, our own) holds the PEB lock -> `RtlAllocateHeap(process heap)` -> waits for A's heap lock.

ABBA, both at 0 % CPU [inferred as the cause of the 04:24 hang: matches the symptom and the load-time burst of new threads; no stack was captured]. Same family: a first hook entry on any thread while a thread suspended by Boehm's stop-the-world holds the process heap or PEB lock waits forever. The bug existed since 0.36.82; 0.36.84-0.36.87 runs that loaded fine only had the timing on their side.

### 15.2 Fix (0.36.88, asi `game-copy/ts3native.asi.allocsites6`)

- The reentrancy flag is a thread id in a 256-slot lock-free table (`g_asIn`, home slot by a hash of `TEB+0x24`, 32 probes, `InterlockedCompareExchange` to claim): no TLS, no allocation, no lock, no system call. A thread holds at most one slot; a full probe run counts `busy` and drops the record (never waits). `TlsAlloc` is gone from `AsInit`.
- Nothing else changed: the audit found no other call in a hook that takes a user-mode lock or allocates.
- Queues `steady-state.json` and `cc-build-stress.json` point at `allocsites6`.

### 15.3 Host selftest [verified]

- **First entry under a held lock:** a thread whose first hook entry (a reserve) happens while the main thread holds the PEB lock, then the process heap lock, must finish within 3 s. Negative control: the same selftest with the 0.36.87 TLS flag on an index >= 64 fails with `allocsites FAIL: first hook entry blocked on the PEB lock held elsewhere`.
- **Lock-order stress, 1.5 s:** 7 threads (2 x EA heap + reserve + view + `HeapCreate` growth, 2 x fresh threads doing a first reserve + first high-index `TlsSetValue` + 1 MB process-heap block, `LoadLibrary` / `FreeLibrary` of `msimg32.dll` (images mapped and unmapped under the loader lock), PEB lock -> process heap allocation, process heap lock held) while the main thread runs `AsAfterSample` back to back. Hang = 20 s timeout = fail. Result: ~2.1 M operations + ~400 samples, no hang, busy 0, drops 0.

### 15.4 Harness: no hang capture on a load stall (report, not changed here)

`cycle.ts` guards the load with `stallGuard('world load')` (`game-alive.ts`). It runs `captureHang` (sim stacks, thread stacks, procdump minidump) only on the verdict `nothing moved`, which needs the probe to answer. In a heap / PEB deadlock the in-process probe stalls too, so the verdict is `probe not answering` and the job fails **without** a capture or dump (consistent with "no stack captured"). Commit `9e8c5089` added the capture before the heartbeat-freeze kill in `watchGame`, same condition (process and probe up). Suggested follow-up on master: also take the procdump minidump (needs no probe) when the verdict is `probe not answering` with TS3W.exe still running.

### 15.5 Unverified

- That the 04:24 hang was this deadlock (no dump). The next steady-state run on `allocsites6` loading cleanly twice is the check; a further load hang with AllocSites on would need the minidump of §15.4.
- CI flake 2026-10-26 (runs 40410736628, 40412681268): the §15.3 first-entry test timed out on the process heap lock even after the ready-event wait; the test is skipped on CI (`GITHUB_ACTIONS` / `CI` set), runs locally. Root-cause the first-entry heap-lock wait before AllocSites ships to players.
