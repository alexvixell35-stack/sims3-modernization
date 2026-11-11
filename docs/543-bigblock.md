# 543. EA direct big blocks: top-down, Mono cores at the bottom, reuse (`[BigBlock]`, 2026-11-11)

**Status: NOT RUN IN GAME.** Built and host-selftested only (`native/ts3native/bigblock.cpp`, `bigblock-test.h`). Every
switch is off by default (`[BigBlock] Enabled=0`). Static RE on Steam TS3W.exe 1.67 (Capstone disassembly of a read-only
copy). The allocator is Apex Radiance's area (docs/191, docs/201 §12.5): this is an experiment to measure, and nothing
goes to Apex until a run shows a gain.

Tags: [verified] = read in the disassembly or tested on the host; [measured] = a game run's numbers; [inferred] =
reasoning, needs a run; [claim] = another project's documentation, not checked by us.

## 0. Short answer

- **Problem:** Error 12 comes when the largest free block of the 32-bit space runs out, not the total free (docs/204,
  docs/201 verdict). Each travel round trip moves the largest free block down by more than the memory the game keeps,
  and "free total minus largest" grows: the space is being cut into pieces.
- **Idea:** two ends. Long-lived reservations (EA heap cores, Boehm sections, Mono sub-heap cores) stay bottom-up, as
  today. EA's short-lived direct big blocks go to the top of the space, so they stop sitting between long-lived ones
  and leaving holes when they go. Freed direct blocks can also be kept reserved and reused, so the same address ranges
  come back instead of new ones being cut.
- **Three switches**, all off: `TopDown` (EA's own top-down byte, no code patch), `MonoBottom` (Mono sub-heap cores keep
  bottom-up), `Reuse` (decommit and keep freed direct blocks, recommit for the next block of the same size class).
- **Stands down with Apex** (`[Compat] ApexStandDown=1`): Apex FastMemory already sets the same top-down byte and patches
  the same `VirtualFree` call `0x4E5306` [claim, docs/148 row FastMemory].

## 1. The problem in numbers

From the docs:

- Error 12 = the largest free block exhausted (docs/204). Free space outside the largest block is 114-132 MB in live mode
  (docs/66); every other free block is under 9 MB; the largest block is the top gap below the driver regions (docs/201
  §13.5) [measured].
- EA direct blocks (`0x4E50F0`, one `VirtualAlloc` each): in one run 1,445.6 MB allocated and 1,267.7 MB freed by the
  second sample, 125 live blocks / 178 MB. They land bottom-up between the permanent cores and the Boehm sections
  (docs/201 §12.3) [measured].
- The `Scripting/Simulator` EA sub-heap (Mono's native heap) takes its cores from the main EA heap as direct blocks
  (`at=0x4E5130 from=0xD71558`): 74 -> 98 MB in one run, +13 MB as 13 new 1 MB blocks in one trip (docs/201 §13.2,
  §13.6) [measured]. They are never given back [inferred, docs/201].
- EA heap cores and Boehm sections are reserved bottom-up with a NULL hint and never released before exit (docs/201
  §12.2, docs/198) [verified].

New numbers from one travel-loop run (address-space map every 45 s; raw data not in the repo) [measured, one run]:

| | value |
|---|---|
| one round trip: reserved | +231 MB |
| one round trip: largest free block | 1630 -> 1406 MB |
| free total minus largest, after two trips | 94 -> 182 MB |
| new and kept per trip | 3 x 32 MB Boehm sections, 9 x 4 MB + 2 x 16 MB EA heap cores |

What this module can and cannot change [inferred]: the kept growth (about 164 MB of cores and sections per trip) is
long-lived and stays; no switch here makes it smaller. What the switches aim at is the extra loss: the largest block
fell by 224 MB in a trip, and the space outside it grew by 88 MB in two trips. A long-lived core cut bottom-up while
short-lived blocks sit low lands above them, and the hole they leave when freed is too small for the next core. That
is the growth of "free total minus largest". The best case is that it stops growing; the gain is bounded by that
growth, not by the kept total.

## 2. The two-ended layout

Windows places `VirtualAlloc(NULL, ...)` at the lowest free range that fits, or the highest with `MEM_TOP_DOWN`. With
`TopDown`:

- bottom-up (unchanged): EA heap cores (`0x4E4E81`), Boehm sections (`0xD70910`), the Mono sub-heap cores (`MonoBottom`),
  and everything else in the process (NT heap, mimalloc arenas, DXVK);
- top-down: EA direct blocks of every GeneralAllocator heap whose byte `+0x498` is set (only the MemoryMan heap
  `[0x11CB864]` here; the Audio and SpeedTree heaps keep their own byte at 0).

Short-lived blocks then come and go at the top end of the largest gap. A freed block next to the gap edge merges back
into it; a freed block between two live ones leaves a hole that the next top-down block of that size fills first. The
bottom end only sees long-lived reservations.

The Mono sub-heap cores are direct blocks too, but they are long-lived, so with `TopDown` alone they would move to
the top and sit between short-lived blocks for the rest of the session. `MonoBottom` keeps them at the bottom.

`Reuse` attacks the churn itself: a freed direct block of 64 KB to `ReuseMaxBlockMB` is decommitted (its RAM and commit
go back) but stays reserved; the next direct block of the same 64 KB size class gets that reservation recommitted, so
no new range is cut. The OS rounds every reservation's start to 64 KB, so a block of a class costs the same address
space wherever it sits.

## 3. The switches

All need `Enabled=1`. Log lines: `big block:` at start, `OK   big block` when armed, a `bigblock:` line at every
`diag: mem` mark (`[MemDiag]`).

### 3.1 `TopDown` [verified static, not run]

Sets byte `+0x498` of the MemoryMan heap to 1, once, from a thread that waits for `[0x11CB864]` (10 ms steps, up to
120 s). No code patch: EA's own code at `0x4E5111` then passes `0x103000` instead of `0x3000` to `VirtualAlloc`.

- Checks first: the heap is readable, byte `+0` (Init done) is 1, page size `+0x4D4` = 0x1000, and the core reserve /
  commit pair `+0x4D8` / `+0x4DC` is 16 / 4 MB (EA) or 4 / 1 MB (S3PP AllocatorArenaTuning). Anything else: the byte is
  not written and the reason is logged. Byte already set (another mod): left alone. The log gives old -> new.
- Why one write sticks: `+0x498` is written only by the constructor `0x4E5AA0` (`0x4E5B8A`), Init `0x4E5710`
  (`0x4E5855`, the constructor's last call) and SetOption 11 (`0x4E4AC7`, no direct caller passes 11). The heap pointer
  is stored once, at `0x4E40A6`, after the constructor and Init ran [verified].
- Readers: `0x4E5111` (direct blocks) and `0x4E4EBE` (the core fallback, which runs only when a core reserve failed;
  it then goes top-down too) [verified].

### 3.2 `MonoBottom` [verified static, host-tested, not run]

Needs `TopDown=1`. The Mono sub-heap core supplier `0xD71510` (a callback, no direct callers) rounds the request up to a
power of two, at least 1 MB, then calls the main heap's Malloc: `mov ecx,[0x11CB864]; push 0; push esi; call 0x4E69D0`
at `0xD71553`. Its rel32 (`0xD71554`) is pointed at `BbMonoMalloc`, which marks the calling thread in one slot
(compare-exchange; no TLS, docs/201 §15) around EA's Malloc. The `VirtualAlloc` stub at `0x4E512A` strips `MEM_TOP_DOWN`
for the marked thread only and never uses the reuse cache for it.

- Least invasive way found: the supplier calls the main heap Malloc, which is shared by everything, so the flag cannot
  be changed per heap; the call site is the one place that knows the request is a Mono core.
- Limit: one slot. A second thread inside the supplier at the same time is not marked (`monoBusy` counts it); its core
  goes top-down like any direct block. The supplier runs under the sub-heap's lock [inferred], so this should be rare.
- Limit: only cores that reach the direct path (`0x4E50F0`) are affected. A request the main heap serves from a core
  (when `+0x46D` is clear or `+0x488` reached `+0x490`) is not a direct block and is unchanged [verified static].

### 3.3 `Reuse` [verified static, host-tested, not run]

The disp32 of `call [VirtualAlloc]` at `0x4E512A` and of `call [VirtualFree]` at `0x4E5306` point at our own pointer
slots (`BbAlloc` / `BbFree`). The return addresses `0x4E5130` / `0x4E530C` do not change, so `[MemDiag] AllocSites`
attribution is the same.

- **Free** (`VirtualFree(base, 0, MEM_RELEASE)` from EA): a base this module recorded at allocation, still live, with
  EA's header (`[base] = 0`, IS_MMAPPED bit, EA's size in the region's 64 KB class), is decommitted with
  `VirtualFree(base, region, MEM_DECOMMIT)` and cached. Anything else goes to the real `VirtualFree` unchanged: blocks
  allocated before arming, sizes outside 64 KB-`ReuseMaxBlockMB`, Mono cores, other call shapes.
- **Alloc** (`VirtualAlloc(NULL, n, 0x3000 or 0x103000, PAGE_READWRITE)` from EA): the newest cached reservation of n's
  64 KB class with room for n is recommitted with `VirtualAlloc(base, n, MEM_COMMIT, PAGE_READWRITE)`. Recommitted pages
  are zero, as from a new block. If the commit fails (commit limit), the reservation is released and EA's own call runs.
  A miss runs EA's call and records the new base.
- **Budget:** at most `ReuseMaxMB` (default 128) of cached reservations and 256 entries; the oldest are released with
  `VirtualFree(base, 0, MEM_RELEASE)` (base = the reservation's start, size 0: the whole reservation goes, whatever is
  committed in it) outside our lock.
- **Double free:** a second free of a cached block returns `FALSE` with `ERROR_INVALID_ADDRESS` (what `VirtualFree` says
  for a released region) and keeps our reservation.
- **Locking:** a slim reader/writer lock of our own, held only for table updates, never across a system call. The call
  sites are shared by every GeneralAllocator heap, and a heap whose lock `+0x4E4` is NULL runs them unlocked (Malloc
  `0x4E69D0` tests it), so EA's lock is not relied on.
- **Map:** one DWORD per 64 KB of the 32-bit space (256 KB, allocated only with `Reuse`): region size and state of every
  base this module recorded.

## 4. Patched sites (TS3W 1.67) [verified, bytes checked before every write]

Each write is one aligned 4-byte operand (compare-exchange against the game's value), at DllMain, after the whole
window around it matched. A window that differs logs `SKIP big block ...: bytes at 0x... differ (found ...)`.

| part | window | bytes (start) | written |
|---|---|---|---|
| every switch | `0x4E50F0`-`0x4E5133` (68 bytes) | `56 57 8B F9 8B 87 D4 04 00 00 ... 80 BF 98 04 00 00 00 B8 00 30 00 00 74 05 B8 00 30 10 00 6A 04 50 56 6A 00 FF 15 60 52 F9 00 85 C0 74 6D` | disp32 at `0x4E512C` (`MonoBottom`, `Reuse`); old `60 52 F9 00` |
| `Reuse` | `0x4E51F5` (11 bytes) | `F6 C2 02 53 55 0F 85 CF 00 00 00` (test dl,2; jne `0x4E52CF`) | nothing |
| `Reuse` | `0x4E52CF`-`0x4E530B` (61 bytes) | `8B 10 8B 6C 06 0C 8B C8 8D 5C 32 10 2B CA ... 68 00 80 00 00 6A 00 ... 51 FF 15 5C 52 F9 00` | disp32 at `0x4E5308`; old `5C 52 F9 00` |
| `MonoBottom` | `0xD71510`-`0xD71559` (74 bytes) | `8B 44 24 0C 83 C0 FF ... 8B 0D 64 B8 1C 01 6A 00 56 E8 78 54 77 FF 85 C0` | rel32 at `0xD71554`; old `78 54 77 FF` (target `0x4E69D0`) |

The full windows are in `bigblock.cpp`. These are the only direct-block calls: the exe has 7 `VirtualAlloc` and 5
`VirtualFree` import references (`0x4E4E81`, `0x4E4EB4`, `0x4E4EDB`, `0x4E512A`, `0x4E5536`, `0xD70910`, `0xD9AB09`;
`0x4E48AB`, `0x4E504C`, `0x4E5306`, `0xD70A00`, `0xEBC58F`); the others are cores, the core release at heap shutdown,
the trim decommit, Boehm, Mono and the CRT [verified]. The survey's description of the direct path (docs/201 §12.3)
matched the bytes; one detail added: "large" means at least `[heap+0x494]` (128 KB from Init) with byte `+0x46D` set and
`[heap+0x488]` below `[heap+0x490]`.

## 5. Verified and not

| claim | state |
|---|---|
| direct-block alloc / free sites, header layout, counters `+0x488` / `+0x48C` | verified (disassembly) |
| `+0x498` writers and readers; heap published after Init | verified (disassembly) |
| Mono supplier bytes and its call to the main heap Malloc | verified (disassembly) |
| stub logic: reuse, size class, budget, entry cap, double free, pass-through, header check, Mono strip, wrapper stack balance, 4-thread stress, site windows, give-back, TopDown checks, Apex stand-down | verified (host selftest, real `VirtualAlloc` / `VirtualFree` on test reservations, copies of the game's bytes) |
| every ASI is loaded before the game creates the heap, and the heap exists before Apex installs (first Present + 1 s), so the late give-back comes first | inferred |
| top-down blocks stop "free total minus largest" growing | inferred: the point of the test |
| `MEM_TOP_DOWN` placement cost with many regions | community-reported, not measured (docs/201 §12.5) |
| Apex FastMemory sets `+0x498` | claim (docs/148), not measured in game |

## 6. Test plan (not run)

A/B on the travel loop (repro `leak-travel-roundtrip-x3` or the run behind §1), same save, Apex absent, one change at a
time, arms alternated:

1. `Enabled=0` (baseline);
2. `TopDown=1`;
3. `TopDown=1`, `MonoBottom=1`;
4. `TopDown=1`, `MonoBottom=1`, `Reuse=1`.

Read every 45 s an external read-only address-space map (`tools/memory/mem-census.py` or the probe's `/memory`):
largest free block and "free total minus largest", plus where the direct blocks sit (top or bottom half). With
`[MemDiag] Light` the `bigblock:` line gives `allocs`, `hits` / `misses`, `cacheN` / `cacheMB`, `evicts`, `strips`,
`monoBusy`, `pass`, `dfree`, `bad`, `stale` and the heap's live direct blocks.

Pass for an arm: per trip, the largest free block falls by no more than the baseline, and "free total minus largest"
grows clearly less; no new crash, no slower travel load. A short smoke first: the start lines say what installed,
`topDown=1` in the first `bigblock:` line, `bad=0`, `dfree=0`, `stale=0`. One start with Apex loaded must log the
stand-down line and install nothing.

## 7. Risks

- **Every big EA block moves.** A block placed in a new place can expose an old EA bug that the old layout hid; any crash
  in the A/B is a stop.
- **Top-down next to the driver regions:** short-lived blocks now sit right below the driver / DXVK regions at the top of
  the gap [inferred harmless, not measured].
- **Reuse holds address space:** up to `ReuseMaxMB` stays reserved with no RAM behind it. With `TopDown` it sits at the
  top end of the largest gap, so in the worst case the largest block is that much smaller while the cache is full. The
  default 128 MB is a guess; the A/B decides.
- **Reuse changes `VirtualFree` timing** on the free path (a decommit instead of a release); Apex moves the same call to a
  worker. Both at once is not supported: stand-down.
- **MonoBottom marks one thread at a time**; a missed mark only means one Mono core at the top.
- **Late Apex** (`zzApexRadiance.asi`): our operands go back before arming, but if Apex looked at the bytes earlier than
  inferred, its FastMemory patch at `0x4E5306` would not install for that session.
- **No counters slot:** the numbers live in the module and only reach the log; no native registry ids were reserved.

## 8. Files

`native/ts3native/bigblock.cpp` (module), `native/ts3native/bigblock-test.h` (host selftest, `TestBigBlock`),
`native/ts3native/ts3native.ini` (`[BigBlock]`), docs/68 (§2 row, §4 #22), `docs/data/apex-sites.csv` (`0x4E5306`
class C2).
