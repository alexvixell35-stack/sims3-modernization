# 542. GC and allocator long shots: what the binary allows (2026-11-10)

Answer to the long-shots plan (L1-L5, plus L6 for the 64-bit direction). Static RE on a copy of Steam TS3W.exe 1.67
(Capstone linear sweep, string and constant reads), read-only process reads during test runs (VirtualQueryEx, heap object
fields), and numbers from runs al372 and tonight's queue. No code change; no game state touched.

Tags: [verified] = read in the disassembly or the binary's strings; [measured] = a run's numbers; [inferred] = reasoning,
needs a probe; [doc] = established in an earlier doc, cited. EA 1.69 was not checked; every address is Steam 1.67.

## 0. Short answer

| # | Idea | Verdict | Blocking facts | Gain | Effort / risk |
|---|---|---|---|---|---|
| L1 | bdwgc 8.x in place of the game's Boehm | **not feasible as a swap**; possible only as a port of 8.x onto EA's edits | the GC is Mono's libgc **6.6** plus an EA backport and EA edits (§1); EA and our own code read Boehm internals (header `+0x18`, `+0x9C`, typed descriptor in a block's last word, mark proc pushing via an internal) (§2) | parallel mark shortens pauses: stress only (§7.1); unmapping gives RAM, not address space (§7.2) | weeks; high (every internal reader, finalization, mint's conservative frames) |
| L2 | features already compiled in | **partly feasible; mostly done** | parallel mark and USE_MUNMAP are compiled out [verified, §1]; incremental mode on GetWriteWatch is in and on [doc] | tuning only: heap size and divisor, already ts3native `[GC]` (docs/199) | low |
| L3 | precise / generational / moving GC | **not feasible** | mint keeps raw object pointers in frames and arena chunks, scanned conservatively; EA keeps managed refs in uncollectable cells and on the stack, never in handles; no write barrier anywhere (§3, §4) | n/a | n/a |
| L4 | modern native allocator everywhere | **report only (Apex's area)** | EA GeneralAllocator (dlmalloc 2.7) serves the main heap and one shared Mono/Scripting heap; S3SS mimalloc serves the CRT (§6) | pending M4 | Apex's call |
| L5 | rebase DLLs, reserve early | **no gain now** | no image borders the largest free block; private allocations fill the space between images (§5) | 0 MB measured | n/a |
| L6 | own the page layer (log, then move data out) | **feasible as measurement**; the step toward 64-bit | every VirtualAlloc / MapViewOfFile owner tagged first (§8) | enables moving the resource cache (150-200 MB, docs/08) | medium; low while it only logs |

The 64-bit view (user framing): an allocator swap cannot widen 32-bit pointers. What a 64-bit port needs from this layer is
the map of which allocations hold pointers and where. §2.2 is that map for the managed side.

## 1. S1: which Boehm, and how it is built [verified]

- TS3W.exe holds the string `Mono 1.2.3.1`: the runtime is **Mono 1.2.3.1** (closes docs/03 §8 "unconfirmed").
- Mono 1.2.3's bundled libgc (mono/mono tag `mono-1-2-3-1`, `libgc/version.h`) is **Boehm GC 6.6**. It stays 6.6 through
  mono-1.2.6.
- The binary's GC strings match the 6.x source:
  - `GC Warning: Out of Memory!  Returning NIL!` (7.x says `Returning NULL` with `Heap size:`).
  - Present: `GC_PAUSE_TIME_TARGET`, `GC_LARGE_ALLOC_WARN_INTERVAL`, `GC_DUMP_REGULARLY`, `GC_NO_BLACKLIST_WARNING`,
    `GC_MAXIMUM_HEAP_SIZE`, `GC_INITIAL_HEAP_SIZE`, `GC_ENABLE_INCREMENTAL`, `GC_PRINT_STATS`.
  - Absent: `GC_MARKERS`, `GC_NPROCS`, `GC_UNMAP_THRESHOLD`, `GC_FORCE_UNMAP`, `GC_FULL_FREQUENCY`, `GC_FREE_SPACE_DIVISOR`.
- Constants match 6.6's `gc_priv.h`: MAXHINCR 2048 (docs/13: 0x800), MAX_HEAP_SECTS 768 (docs/13: 0x300), `hb_sz` in words
  (docs/198 §3.2).
- **One EA backport:** the binary has `GC_gww_read_dirty` and GetWriteWatch
  (`GC_gww_read_dirty unexpectedly failed ... Falling back to marking all pages dirty`). Mono's libgc has no GetWriteWatch
  code in mono-1-2-3-1, 1.2.6, 2-0 or 2-4; that GWW_VDB code is bdwgc 7.x's `os_dep.c`. So EA added it, which explains the
  "7.1 by code shape" reading of docs/13 and docs/120.
- **Resolves the docs conflict:** base = Mono's libgc 6.6 (with Mono's patches) + EA's GetWriteWatch dirty bits from bdwgc
  7.x + EA's own edits (docs/13 Q2 collect-not-expand, docs/57 §1 header stamp and free bits, docs/67 §22.6). Not 7.1.
- Linked statically into TS3W.exe with Mono, no DLL [doc: docs/32, docs/66 §3.1].
- Compiled out [verified strings; doc: docs/13, docs/198 §3.1]: parallel mark, thread-local allocation, USE_MUNMAP (no
  VirtualFree caller in the GC range).
- Compiled in [doc: docs/13, docs/120 §2.1]: incremental mode on GetWriteWatch (enabled at `0xD7E602`), the Win32 thread
  table (256 x 0x14 at `0x1221000`, SuspendThread stop-the-world, only the GC_init thread registered), typed kind 4.
- `GC_time_limit` is `[0x1194814]`, initialized to **5** in the image; GC_init (`0xE4E24D`) overwrites it only from
  `GC_PAUSE_TIME_TARGET` (values >= 5). Mono 1.2.3.1's libgc has `#define TIME_LIMIT 50` (`gc_priv.h`). So 5 is an EA edit
  [verified: image value + source]. In this build it only picks divisor x2 or divisor in `min_bytes_allocd` (docs/13 Q1).

## 2. S2: who calls Boehm, and how

### 2.1 Range and census [verified, static]

- Method: linear Capstone sweep of the executable sections, 222,113 rel32 call/jmp decoded. Direct calls only; calls through
  function pointers and code the sweep misaligns are not counted, so every count is a lower bound.
- **Boehm code ends at `0xE51E80`**, not `0xE56000` (docs/198 §3.1):
  - `0xE51290` is win32_threads (GetCurrentThreadId, the 256-entry table).
  - `0xE51E80+` is Mono: `0xE520E0` has 1,036 callers from the metadata code `0xDC0000-0xE3FFFF`; `0xE544D5` is the mint opcode
    dispatch (`cmp 0x1c0; jmp [0xe59880 + op*4]`).
- 211 Boehm functions are reached by direct calls; 38 of them are called from outside the Boehm range (Mono and EA).
- Allocation entries [verified]: `GC_malloc` = `0xE4BE30` (kind 1 NORMAL, free list `0x12226E8`); `GC_malloc_atomic` =
  `0xE4BDC0` (kind 0 PTRFREE, free list `0x1222EEC`).

### 2.2 Allocation kinds by caller: the 64-bit port map

| Entry | Sites outside Boehm | Callers | What a 64-bit port needs |
|---|---|---|---|
| `GC_malloc` `0xE4BE30` (conservative) | 40 | EA host `0xD7xxxx-0xD8xxxx` 14 (ScriptProxy / ScriptCore glue at `0xD7EED0`, `0xD7FE80`); Mono reflection 7 (MonoCMethod, MonoField, MonoProperty, MonoEvent, MethodBody); object.c `0xEA2690`, `0xEA2770`, `0xEA2E70`, `0xEA7990`, `0xEA8190`; mono-ehash; mint/runtime `0xE5xxxx-0xE6xxxx` 7, `0xEBxxxx` 6 | layout unknown to the GC: every word is a candidate pointer, so the pointer words must be found per site |
| `GC_malloc_atomic` `0xE4BDC0` (pointer-free) | 5 | object.c only: `0xEA14B0`, `0xEA1510` (clone), arrays `0xEA2770`, `0xEA2E70`, `0xEA3EB3` | none: strings, primitive arrays, pointer-free objects |
| `GC_malloc_explicitly_typed` `0xE50DE0` (+ `0xE50F90`) | 7 + 2 | object.c `0xEA1490`, `0xEA2690`, `0xEA2770`, `0xEA2E70`; `mono_object_new_ptrfree` / `_box` / `_fast` `0xEA1CB0`; EA host `0xD718A0`, `0xD71900`, `0xE697D0` | the class bitmap descriptor: the exact map to widen |
| `GC_malloc_uncollectable` `0xE4BF90` (roots) | 9 | EA host only: `0xD70DA0` x2, `0xD714B0`, `0xD71800`, `0xD71900` x2, `0xD7E625` (MonoScriptHost_GetEnumSimple), `0xD8A85C` x2 | root cells holding pointers: per site |
| `GC_free` `0xE4BEB0` | 14 | EA host 10 (`0xD80750` "Scripting/Mono" x4), Mono `0xE52DD0` x3, `0xE53E80` | - |

- Mono's object.c picks one of three kinds per allocation: typed (the class has a descriptor), atomic (pointer-free) or
  `GC_malloc` (no descriptor). Reflection objects, mono-ehash tables and the EA glue use plain `GC_malloc`.
- So the typed and atomic paths carry their layout; the 49 plain and uncollectable sites do not.
- Against `docs/data/mono-glue-map.csv` (EA host sites): `GC_malloc` 14 = 14, typed 2 = 2; `GC_free` 10 here vs 9 there;
  uncollectable 9 here vs 7 there (the two at `0xD8A85C` are missing from the map; docs/118 lists `0xD8A830` as an
  uncollectable site). docs/67 §22.5's "GC_free 16 direct" = 14 outside + 2 inside the Boehm range here.

### 2.3 Internals read directly (the swap's other surface) [doc]

- EA arena chunks rely on the typed descriptor in the block's last word (`+0x7FC`); EA's mark proc `0xE69520` (via
  `GC_new_proc`) pushes through the internal `0xE508D0` (docs/67 §22.3, §22.5).
- EA hook slots `[0x1234360]` (notify event) and `[0x11949CC]` (other roots); EA hooks `0xD70900`, `0xD71820`, `0xD707E0`; 78
  empty lock-hook calls (docs/118 D2-D4).
- Block header: `+0x18` gc_no stamp, `+0x9C` free bits; the fast-path hook `0xE4C1E0` (docs/57 §1, docs/67 §22.6).
- Our own code: ts3native's black-list immediates `0xE4A856` / `0xE4A85D` (`[GC] BlackListCapKB`), `GC_gc_no` `0x11F2F90`
  ([MonoJit] verifier), the census and heap-peek tools (header and free-list layouts).
- No code pops a free list directly outside Boehm (docs/57 §1).

## 3. S3: managed pointers outside the managed heap [doc + gap]

- Roots [doc: docs/57 §4, docs/130 §2]: the `GC_add_roots` table `0x122FB14` (callers `0xE53070`, `0xE75BF0`, `0xE78DA0`),
  uncollectable blocks, the finalizer and disappearing-link tables, the collecting thread's registers and own stack.
- Not roots: the image `.data` (`GC_no_dls` = 1 at `0xE69598`, docs/130 I-130-6), TLS, other threads' stacks.
- EA keeps managed references in uncollectable cells and on the scanned stack, never in GC handles (docs/118 D9, D10). The
  task container is uncollectable (`0xD7E560`, host `+0xB84`). Host maps `+0x1664` / `+0xBE8` hold managed references
  (docs/121); how they are rooted is not stated.
- For L3 every one of these is "pinned forever" or "unknown": none can move. Count of EA C++ objects storing a MonoObject*:
  **gap** (§9).

## 4. S4: interpreter assumptions [doc + gap]

- mint keeps raw object pointers in eval-stack and local slots inside arena chunks, in `MonoInvocation+0x0C`, task state
  `+0x04` / `+0x20` and saved cells `+0x28`; the chunks are scanned conservatively (docs/130 §1.3, §1.6; docs/67 §22.6).
- No write barrier exists (docs/57 §6). The store helper `0xE69820` looks like one [inferred, D11].
- Store surface a barrier would need (from the Satb work, docs/212, docs/206 §7.1): 37 mint store opcodes, 45 runtime
  writers, `0xE69820` at about 80 sites, 180 byref / array ICs, 273 writer rows, plus every [MonoJit] compiled store
  (docs/507: compiled stores bypass the handlers).
- Gap: 222 of 233 EA native-body rows are not disassembled for stores (docs/212 gap 3).

## 5. S5: address-space map [measured, al372 at +39 min]

BinBase copy, DXVK x86 in-process, Apex off. Tool: a VirtualQueryEx walk (read-only).

- 4,695 regions; free 1,198 MB; **largest free 1,057 MB** at `0xBDC20000-0xFFCB0000` (the top of the 4 GB LAA space,
  between two private regions); the next are 25, 6 and 3 MB.
- Image 407 MB, mapped 111 MB, private committed 2,059 MB, private reserved 321 MB.
- 151 images: 5 below 256 MB (18 MB; TS3W.exe at `0x400000` and Windows.Devices.winmd have no relocations), 143 between 256 MB
  and 2 GB (337 MB, all relocatable), 3 above 2 GB (52 MB).
- **Moving any single image gains 0 MB of largest free block:** no image borders it, and private allocations fill the space
  between images. TS3W.exe (no `.reloc`, docs/15) sits at the bottom, where it splits nothing.
- Test-only: three frida-agent.dll copies (24.6 MB each, 74 MB) come from the harness, not a player's game.
- Series over the night (M3, §7.3): the largest free block fell from 1,057 to 839 MB during al372 [measured].

## 6. S6: which allocator serves what [doc + measured]

- CRT malloc family (MSVCR80): S3SS's mimalloc (docs/03).
- EA GeneralAllocator (dlmalloc 2.7) serves the main heap `[0x11CB864]`, Mono's g_malloc `[0x11D4BC8]` and
  Scripting/Simulator `[0x11EE51C]` (docs/15 §8, docs/201 §13.2).
- **Measured in al372:** `[0x11D4BC8]` and `[0x11EE51C]` hold the same object (`0x36D61970`): one shared heap, not two. Core
  sizes are 4 MB / 1 MB on both the main heap (`0x0151D630`, `+0x4D8` / `+0x4DC`) and the shared heap. So S3PP's 16/4 to 4/1 MB
  rewrite (docs/69) reaches both. The two exact-16 MB core groups of docs/201 §12.2 come from allocators outside these slots
  [inferred].
- `+0x498` is a byte flag, not a count [verified]: byte stores at `0x4E4AC7`, `0x4E5855`, `0x4E5B8A`, byte tests at
  `0x4E4EBE`, `0x4E5111` (docs/201: option 11, default 0). `+0x49C` / `+0x4A0` are a list head that init points at itself
  (`0x4E5B90`, `0x4E5B96`). An earlier dword read of `+0x498` gave "284.83 / 68.75 MB"; that read three more bytes and is
  not a size.
- Apex FastMemory is tuning, not a replacement: critical-section spin 10 to 2,000, big-block free on a worker, 60 s trim,
  top-down (docs/187, docs/148). The plan's L4 line overstates it.

## 7. Measurements

### 7.1 M1: GC cost on the sim thread

| Source | Collections | Pauses | GC time |
|---|---|---|---|
| natural, 352 MB heap (docs/04 F15) | 1 per 30 s | forced max 27 ms | mark 4.8 ms/s |
| stress, al372 (94 min, every leak trigger family, old save) [measured] | 31 per minute (2,523 in 81 min; `GC_gc_no` 21-33 per min) | 94% of >= 250 ms frames had a collection (1,823 of 1,932); worst 594-756 ms, each with one; none >= 1 s | about 130-180 ms/s [inferred: 31 x 0.25-0.35 s per minute] |
| stress (docs/199 §7) | - | 336-371 ms | ~580 ms per cycle |
| natural IP, current release (two 10-min jobs, release ini) [measured] | 25 and 29 per minute (251 in 10.2 min, 298 in 10.3 min), heap 232-302 MB | few hitches: a collection in 59 and 82 of about 1,600 frames >= 50 ms, 2 and 21 of about 400 >= 100 ms, 1 of 96 and 1 of 45 >= 250 ms | not in the log (it counts frames, not collection time) |
| natural BP / SV | pending (the 10-min BP job quit before its 10-min log; next BP / SV jobs) | pending | pending |

- Source of the al372 row: the WishVariety periodic log (frames with a collection, heap range 269-364 MB) and `GC_gc_no`
  read at the heap peeks (each peek adds 2).
- Reading: under stress, collections are the hitch. In natural play they are frequent but short: IP collects about every
  2-2.4 s, and only 1 in 4 collections even shows in a >= 50 ms frame (docs/161: GC is not a hitch leader; docs/96: 0.2% of
  >= 300 ms iterations). So L1's parallel marking pays in stress and in long sessions with a big
  live set, not in a typical frame [inferred].

### 7.2 M2: managed-heap fragmentation

- [doc] live 78-84 MB in a 353-360 MB heap, ~165 MB in free blocks (docs/199 §7.2); sections 374.5 to 409.5 MB, about 80
  small header chunks at load (docs/201 §12.4, §13.3); black-list runaway to 2,388 MB, mostly free (docs/67 §35).
- al372 census: 144.7 MiB live at +55 min under stress [measured].
- Header walk at minute 50 of a 60 min natural Sunset Valley run [measured, read-only, mark bits merged over two
  collections]: 11 sections, 400.0 MB. Live (marked) objects 128.4 MB. Unmarked slots beside live ones in used
  small-object blocks 112.5 MB. Used blocks with no live object, waiting for the sweep, 81.2 MB (50.2 MB of it large
  objects). Free blocks 73.7 MB in 3,025 blocks, the largest 0.9 MB: 1,815 of 4 KB, 639 of 8-16 KB, 304 of 20-64 KB,
  211 of 68-256 KB, 56 of 260 KB-1 MB, none above 1 MB. Large objects: 68.8 MB in 19,082 pages.
- Reading: about a third of the heap is live. No free block can take an allocation above 0.9 MB, so every larger one
  grows the heap by a new section, and the dead large objects (50 MB waiting for the sweep) are what cuts the free
  space into pieces. A census of the large objects by class is the cheap next step: if a few callers make most of
  them, reuse there helps without touching the collector [inferred].
- A free block's `hb_sz` is in bytes (equal to the measured extent for 3,022 of 3,025 blocks). A heap walk that reads
  it as words skips the used blocks behind each free block: 11,029 of 74,681 blocks on this run. Census numbers taken
  before 2026-11-10 with such a walk are about 15% low.
- Pending: the same walk before and after a travel; the large-object census by class.
- What L1/L2 unmapping could give: decommit returns RAM; Boehm keeps its section reservations, so the largest free block
  does not grow (docs/198) [inferred for 8.x's Windows unmap, which decommits].

### 7.3 M3: address-space fragmentation over time

- A VirtualQueryEx walk every 10 min through tonight's queue: totals, the 3 largest free blocks, owners next to them.

| Run (minutes since the run's start) | free | largest free | private committed |
|---|---|---|---|
| al372, kit5 old save, stress: +39 | 1,198 MB | 1,057 MB | 2,059 MB |
| al372 +72 | 1,118 MB | 967 MB | 2,148 MB |
| al372 +82 | 1,050 MB | 935 MB | 2,194 MB |
| al372 +92 (just before the end) | 947 MB | 839 MB | 2,283 MB |
| IP, release ini, about 8 min in | 1,491 MB | 1,414 MB | 1,876 MB |
| IP, second job, at load | 1,978 MB | 1,693 MB | 1,344 MB |
| IP, second job, end (16 min) | 1,463 MB | 1,355 MB | 1,896 MB |

  [measured]. On the stress run the largest free block shrank about 4 MB per minute (218 MB in 53 min), in step with
  private commit (+224 MB): the space goes to new commits, not to holes between them. Pending: the BP and SV rows.
- The plan's "load 7: 1,531 MB free, largest 1,389 MB" is not in any doc; docs/204 §2.1 has restart vs in-process reload
  (about 600 MB more largest free after a restart).

### 7.4 M4: native allocator waste

- Not measured. Needs: EA heap free bytes inside committed cores (dlmalloc bins) and mimalloc segment slack on the same
  save. docs/215 §4.1 has the proposal; miCom counts nvoglv32 regions (docs/201 §13.2).

## 8. L6: own the page layer

- Log every VirtualAlloc / NtAllocateVirtualMemory / MapViewOfFile by owner (docs/08 §2.5: 7 VirtualAlloc sites in TS3W.exe),
  then attach each private region in the S5 map to an owner.
- That turns "private 2,059 MB" into a per-owner list, which is what decides what can leave the 32-bit space first. The only
  movable block known today is the resource cache (150-200 MB, the docs/08 ts3res64 helper) [doc].
- Risk is low while it only logs; Apex owns the allocator hooks, so a shared hook point is their call.

## 9. Gaps and how to close each

| Gap | Close by |
|---|---|
| EA 1.69 the same build? | rerun the S1 strings and the S2 census on the EA exe |
| EA C++ objects holding MonoObject* (S3 count) | host-side census: scan EA heap blocks for words that point at marked objects, grouped by vtable (read-only peek) |
| EA native-body stores (S4, 222 of 233 rows) | docs/212 gap 3: disassemble the remaining rows |
| M1 natural BP / SV | a BP and an SV job of 10 min or more (WishVariety periodic log + `GC_gc_no`) |
| M2 free-block distribution | heap-peek pass over Boehm's free lists in a 60-min run and a travel |
| M3 owners between free ranges | the VirtualQueryEx series plus the L6 owner log |
| M4 | EA heap bins walk + mimalloc stats on one save |

## 10. Tools

Read-only, each with `--self-check` (kept outside the repo): the S2 census and caller tables (Capstone), the EA heap core
reader, the VirtualQueryEx map. No game run for this doc changed game state.
