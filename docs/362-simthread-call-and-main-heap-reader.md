# 362. `IsSimThread` internal call, the main-heap core reader, PortalFlagRefs on large graphs

2026-11-26, branch `feature/native-simthread-heapcores-1126`. Built, host selftests pass, queue written and dry-run,
**not run in game**. Steam 1.67 only; EA 1.69 not read. Ledger: docs/51 §632. Counters: v227 `mhc*`
(docs/registry.md).

Three small pieces of ts3native 0.36.306-simhc1 that other lanes asked for (the third is §6):

1. `IsSimThread`: managed code can ask whether it runs on the game's sim thread (docs/359 §8 item 7).
2. A reader for the main EA heap: cores, committed bytes, free bytes inside the cores and the largest free run. For
   `[MemDiag]`, and for the Atlas P1 probe's "p1 mem" lines.

Nothing here changes a Sim, a lot or a save. No code path looks at a Sim, so there is no household exemption to make.

## 1. `IsSimThread`

### 1.1 What the game knows [verified, disassembly]

- `0x7D99F0` returns `[0x11D8A8C]`; `0x7D9A00` stores its argument there. Two callers, both pass
  `GetCurrentThreadId()` (import slot `0xF95158`): `0x76B479`, at the top of the function at `0x76B470`, and
  `0x76B728`.
- ts3native already reads this slot as "the sim thread" in `routegate.cpp` (`g_rtSimTidSlot`), the crash report and
  `cullrace.cpp`.
- [inferred] Both callers run on the script (sim) thread itself. The slot is 0 before the first of them ran.

### 1.2 The call

Native: `Ts3Fixes.AutShadowNative::IsSimThread`, registered with the other `AutShadowNative` calls at the first
Simulate tick (`AsRegisterIcs`, `autshadow-run.cpp`). It returns 1 when the slot is not 0 and equals the calling
thread's id, else 0. Two reads and a compare, no lock.

Managed (`mods/ts3fixes/src/AutShadow.cs`):

<!-- docs-code: ok -->
```cs
[MethodImpl(MethodImplOptions.InternalCall)] public static extern bool IsSimThread();   // AutShadowNative
public static bool OnSimThread()                                                        // Fixes
```

`Fixes.OnSimThread()` is the one to call. It answers **false** in three cases, and in each the caller must take the
game's own path:

- another thread (the AutShadow worker, a route job, any future script thread);
- the game has not set its sim thread id yet;
- the installed ts3native has no such call (older asi): the first call throws `MissingMethodException`, which is
  caught once and remembered.

The contract version of the `AutShadowNative` calls stays 11: the new calls are additions, and an older ts3fixes
never calls them.

**Warm-up.** A call first made on the worker is a cold transform there (docs/206 §15). `Ready()` in `AutShadow.cs`
calls `OnSimThread()` on the sim thread at every world load, right after `ColdWarm()`, and writes the read-back line

```
ts3fixes: simthread: IsSimThread 1 on the sim thread, call there (docs/362)
```

The line needs `[UserDiag]` (release default). `Ready()` runs only with AutShadow in use; without it there is no
worker, so no cold call either.

### 1.3 Handoff for `feature/simdesc-index-1126` (the merger applies it; that branch was not edited)

Today the index decides "may I use and update the index here" with the native "is the AutShadow worker" test without
the run-in-progress condition (docs/359 §8 item 7, I-628-8). That is "not the one worker we know", which is wrong for
any other thread that runs script code.

Change, after this branch is merged before or with simdesc-index:

1. `mods/ts3fixes/src/SimDescIndex.cs` line 112 on that branch (tip cf472d671):
   `static bool SdiSimThread() { try { return !AutShadowNative.IsWorker(); } catch (Exception) { return true; } }`
   becomes `static bool SdiSimThread() { return OnSimThread(); }`, and the comment above it (lines 109-111) says
   "the game's sim thread, by ts3native (docs/362)". "Not the sim thread, or not known" then sends the lookup to
   the game's own code and leaves the index untouched. `OnWorker()` itself is not changed: other code uses it
   with its `sInWork` condition.
2. Keep the rule that a rebuild is thrown away when a writer was noted while it ran. It covers a different case.
3. No extra warm-up is needed: `Ready()` warms the call at every world load. If the index can be asked before
   `Ready()` ran, the first call happens on the sim thread anyway (the world load runs there) [inferred].
4. With an older asi `OnSimThread()` is always false, so the index is never used: the switch then does nothing. The
   simdesc-index queues must pin an asi from this branch or later, and should require the read-back line above.
5. docs/359 §8 item 7 and docs/51 I-628-8 can then be closed as "sim-thread test from docs/362".

## 2. Main-heap core reader

### 2.1 Layout used [verified unless marked: disassembly of Steam 1.67, Capstone]

The main heap is the `GeneralAllocator` at `[0x11CB864]` (docs/re/structs.md §9, docs/201 §12.2, docs/344 §9).

| What | Where | Read at |
|---|---|---|
| Lock | `[heap+0x4E4]` = pointer to a `CRITICAL_SECTION` followed by a depth word at `+0x18`; `Malloc 0x4E69D0` calls `EnterCriticalSection` (import `0xF9512C`), adds 1 to the depth, and undoes both after the call; a NULL pointer means no lock | `0x4E69D4`-`0x4E6A05` |
| Core ring | head node `heap+0x448`, first core `[heap+0x460]`, next `[core+0x18]`, previous `[core+0x1C]` | `0x4E4F08`-`0x4E4F38`, `0x4E54DD` |
| Core header | `+0` first chunk (`(core+0x2F) & ~0xF`), `+4` committed bytes, `+8` reserved bytes | `0x4E4EF0`-`0x4E4F3D` |
| Fencepost | each core ends in a 16-byte fencepost at `core + committed - 0x10`: `{prev_size, 8, 8, 9}`; bit 0 of its size word says the chunk before it is in use; when clear, `prev_size` is that free chunk's size | `0x4E4F5F`-`0x4E4F75`, `0x4E555B`-`0x4E5576` |
| Uncommitted tail | before adding a core the heap looks for a core with `reserved - committed >= need`, commits there and grows that core's last free chunk (or starts one) | `0x4E54F4`-`0x4E55BB` |
| Bins | 127 doubly linked lists, head of bin `i` at `heap+0x28+8*i`, links at chunk `+8` (forward) / `+0xC` (back); bin 1 is the unsorted bin; bin map at `heap+0x430` | `0x4E5EA6`, `0x4E6032`, `0x4E6082` |
| Top | `[heap+0x440]`, linked to itself; `heap+0x30` means "no top"; a replaced top goes to the unsorted bin | `0x4E547F`-`0x4E54AD` |
| Fastbins | head for chunk size `s` at `heap + (s >> 3) * 4`, used when `s <= [heap+4]`; singly linked through chunk `+0xC` (not `+8`) | `0x4E5E80`-`0x4E5E94`, pop `0x4E62A7`, push in Free `0x4E51CB`-`0x4E51EF` |
| Chunk size | `[chunk+4] & 0x7FFFFFF8` (flags `0x80000007`) | `0x4E56A6`, `0x4E4F4C` |

Free indexes the fastbins by `size >> 3` only and sizes are multiples of 16, so with `max_fast` 0x40 the heads in use are
those for 16, 32, 48 and 64 bytes; the reader walks those four.

### 2.2 What is read

Under the heap lock, in this order:

- **cores, committed, reserved:** one pass over the ring.
- **free:** the sizes of all chunks in bins 1-127, the top chunk and the four fastbins. These are free bytes in
  committed core memory.
- **tail:** reserved minus committed, summed over the cores. The heap uses it before it adds a core.
- **largest free run:** the larger of (a) the largest free chunk and (b) for each core, its last chunk if free plus
  its uncommitted tail. (b) is one run because the heap commits the tail in place and merges it with that chunk.

Not counted: direct-mapped blocks (`heap+0x488` / `+0x48C`, the `eaDir` field of `diag: mem`), other heaps (the
`eaCom` / `eaCoreN` fields of `diag: mem` add the script heap when it is a separate object), and chunks held by a
thread cache (`[Alloc]`, Apex FastMemory): the heap sees those as in use.

Reading note: per docs/51 I-629-6 a request of 128 KB or more that misses the top goes to a direct block [inferred
there], so "largest free run" bounds what the heap can serve from its cores, not the largest allocation that can
succeed.

The walk refuses and gives no answer when a list does not hold: more than 4096 cores, committed above reserved, a
fencepost size above the core, a NULL link in a bin, more than 4,000,000 free chunks, free above committed, or an
access fault (SEH). The lock is released in every case. Sanity that always holds on an answer: `cores >= 1`,
`free <= committed`, `committed <= reserved`.

### 2.3 Cost and rate

One read holds the main heap lock for the walk, so every thread that allocates or frees on that heap waits for it. The
walk follows one pointer per free chunk; its time is returned (`us`) and not yet measured in game. Rules built in:

- nothing runs unless a caller asks or `[MemDiag] MainHeap=1`;
- a second read within 1 s returns the previous answer (flag bit 2), with no walk and no lock;
- a read while another thread is reading returns "no answer" at once.

Call it on demand or at a low rate (a line per load, per region, per minute). Never per frame.

The lock is taken the way EA takes it (enter, depth + 1; depth - 1, leave), so the hold looks like an EA allocation to
any code that inspects the lock (the crash H unlock in `p4.cpp`). It is the only write the reader makes.

### 2.4 API

**Native export** (for `atlas.asi` and any other module in the process):

<!-- docs-code: ok -->
```c
struct Ts3nMainHeap {   // 9 DWORD, fixed; KB = 1024 bytes
    DWORD flags;        // bit 0 values valid, bit 1 read under the heap lock, bit 2 previous answer (asked again within 1 s)
    DWORD cores;
    DWORD comKB, resKB; // committed / reserved
    DWORD freeKB;       // free chunks in committed core memory
    DWORD tailKB;       // reserved, not committed
    DWORD bigKB;        // largest free run
    DWORD chunks;       // free chunks walked
    DWORD us;           // walk time = lock hold
};
int __cdecl ts3native_mainheap(Ts3nMainHeap* out);   // 1 = answer, 0 = no answer (out zeroed); out may be NULL
```

<!-- docs-code: ok -->
```c
typedef int (__cdecl* MainHeapFn)(Ts3nMainHeap*);
HMODULE m = GetModuleHandleW(L"ts3native.asi");
MainHeapFn fn = m ? (MainHeapFn)GetProcAddress(m, "ts3native_mainheap") : NULL;   // NULL: older ts3native
```

The struct is fixed. New fields would get a new export name.

**Internal call** (managed): `Ts3Fixes.AutShadowNative::MainHeap(int field)`. Field 0 reads now (1 = answer, 0 = no
answer). Fields 1-9 return the previous answer: 1 flags, 2 cores, 3 committed KB, 4 reserved KB, 5 free KB, 6 tail KB,
7 largest run KB, 8 free chunks, 9 walk us. Other values return -1. A managed assembly other than ts3fixes cannot bind
this name; it goes through ts3fixes or uses the counters.

**Counters** v227 (`ts3native_counters` +0x6B90, 10 int32, docs/registry.md): `mhcReads` (walks), `mhcFails` (walks
without an answer), then the last answer: `mhcCores`, `mhcComKB`, `mhcResKB`, `mhcFreeKB`, `mhcTailKB`, `mhcBigKB`,
`mhcChunks`, `mhcUs`.

**Log** with `[MemDiag] MainHeap=1` (default 0; needs `Light=1` or `Enabled=1`), one line after every `diag: mem`
line, same number and tag:

```
diag: mheap #2 periodic cores=238 comKB=611200 resKB=1265664 freeKB=48211 tailKB=654464 bigKB=15872 chunks=18233 us=2140 lock=1
diag: mheap #3 save-start no answer
```

### 2.5 For the Atlas P1 probe ("p1 mem" lines)

Suggested use: call `ts3native_mainheap` once at each point where a "p1 mem" line is written (after a region load,
after an unload, after a settle), and print `cores`, `comKB`, `freeKB`, `tailKB`, `bigKB` next to the process numbers.

- Core count rising while `freeKB` is large means fragmentation: free bytes exist but not as runs the request fits.
- `bigKB` falling across load / unload cycles of the same region is the number to watch for paged lots.
- `flags` bit 2 set means the values are from an earlier call within the same second; a line written then should say
  so or wait.
- With ts3native absent or older, `GetProcAddress` returns NULL: print `-`.

## 3. Switches

| Switch | Default | Effect |
|---|---|---|
| `[MemDiag] MainHeap` (ts3native.ini) | 0 | `diag: mheap` line after each `diag: mem` line |
| `ts3native_mainheap` export, `MainHeap` internal call | on demand | run only when called |
| `IsSimThread` internal call | on demand | read-back line once per world load when AutShadow is in use |

## 4. Build and queue

- ts3native 0.36.306-simhc1 = master set m1126b + this; ts3fixes = master + the two declarations, `OnSimThread()` and
  the read-back line. Builds `ts3native.asi.simhc-1126`, `ts3native.ini.simhc-1126`, `ts3fixes.package.simhc-1126` in
  `docs/data/builds.json`.
- Host selftest: `memdiag-test.h` (a fake heap with two cores: sums, largest run through the uncommitted tail, the
  1 s repeat, the lock taken and given back, three refusals, the log line) and `autshadow-test.h` (38 calls
  registered; `IsSimThread` on an empty slot, this thread, another id).
- `tools/harness/queues/simhc-correct-1126.json` (correctness, BP 10 min, release defaults + `MainHeap=1`, Bin state
  apex): version read-back, the "main heap on" line, two or more `diag: mheap` lines with cores >= 1 under the lock,
  no "no answer" line, `mhc*` counters, `IsSimThread 1` on the sim thread, 0 script errors. Dry-run only so far.
- No timing queue: nothing here is a gain. The lock hold per line (`us=`) is read from the correctness job.
- Harness file changed on this branch: `tools/probe/agent.js` (the ten `mhc*` names). It merges between game runs.

## 5. Open

1. Walk time in game (`us=`), and how it grows with a fragmented heap. Decide the Atlas probe's call rate from it.
2. The worker side of `IsSimThread` (0 on the AutShadow worker) is proven in the host selftest only; simdesc-index's
   own queue is the first in-game user.
3. EA 1.69: addresses not read. `ts3native_mainheap` reads through `TS3A(0x011CB864)`; the struct offsets are
   assumed equal there [inferred, not checked].
4. Apex FastMemory and our `[Alloc]` cache hold freed chunks outside the heap's lists, so `freeKB` reads lower than
   the bytes those caches could give back.

## 6. `[EaCrashFixes] PortalFlagRefs`: full chunk on graphs above 16,384 chunks

Asked by the Atlas lane (Atlas docs/026, commit 0a71401). Same asi, separate commit.

**The gap.** A lot portal keeps its world-waypoint reference at portal `+0x4C`: chunk in bits 0-13, waypoint in bits
14-23, flags above. 14 bits hold 16,384 chunks, a 4096 m world. Atlas's `[World8192] Routing` hooks the seven readers
that mask the chunk and rebuilds the full chunk there. Its seventh reader (R7, the `and edx, 0x3FFF` at `0x82A1D4`)
lies inside the 11 bytes at `0x82A1CF` that our PortalFlagRefs replaces with a jump, and our stub decoded the chunk
as `id & 0x3FFF`. With both mods on, Atlas had to leave R7 alone and writer 1 would have used the wrong chunk on an
8192 m world.

**The site** [verified, disassembly `0x82A190`-`0x82A20E`]: `esi` = the routing world (`this`; graph width / height
in chunks at `+0x54` / `+0x58`, chunk offsets at `+0x5C`, record base at `+0x60`), `edi` walks the lot's portal
pointers, `0x82A1C0` loads the portal from `[edi]`, `0x82A1CC` loads `ecx = [portal+0x4C]`. After the replaced bytes
the game needs `eax = [esi+0x5C]`, `edx` = chunk and `ecx` = the id (waypoint and flag bits are taken from it at
`0x82A1E0`-`0x82A1F5`). The skip target `0x82A204` reloads everything from `edi`.

**The change.** The stub now passes the world and the portal (`[edi]`) to `PfIdChunk`, which returns the chunk or -1
(skip, as before, when the pair is not a waypoint of the graph). The chunk comes from `PfPortalChunk`:

- width x height <= 16,384: `id & 0x3FFF`. Two reads and a multiply before the old check; no float work, no other
  read. **Every EA world and everything up to 4096 m gets exactly the value the old stub and the game use.**
- above that: `PfFullChunk`. `p` = the chunk under the portal's own position (`floor((x - originX) / 32)` as column,
  the same for z as row, `row * width + column`); the result is the chunk within -8192..+8191 of `p` whose low 14
  bits are the stored ones. It falls back to the stored bits (counted, first 8 logged) when the position was never
  set, lies outside the graph or is NaN, when the result would leave the graph, when there is no world manager, or
  when the graph has more than 65,536 chunks.

The formula gives the same answers as Atlas's `FullChunk` on purpose: the readers Atlas hooks and this writer must
agree on a portal's chunk. It was written for this site's registers, not copied.

[inferred, from Atlas docs/010 and docs/026, not re-read here] portal position x / z at portal `+0x50` / `+0x54`;
world origin x / z at `[0x11ECBC4] + 0xB4 / +0xB8`; chunk size 32 m; chunk index = row x width + column. These are
read only on a graph above 16,384 chunks.

**Selftest** (`wastar-test.h`), mirroring Atlas's cases: all 65,536 chunks of a 256 x 256 graph with the portal in
the chunk and in each of its 8 neighbours rebuild exactly; first and last chunk past 14 bits; a waypoint 31 rows from
the portal; world origin subtracted; a 256 x 128 graph; 128 x 128 and 64 x 64 graphs return the stored bits for any
position (plus a sweep of ids); the seven fallbacks; and the stub itself through the register harness on a 256 x 256
graph (chunk 51,277 in `edx`, id kept in `ecx`, `edi` kept), on 128 x 128 (the stored bits) and with no world
manager (stored bits, one fallback).

**For Atlas.** With ts3native 0.36.306-simhc1 or later and `PortalFlagRefs=1`, R7 is covered: Atlas keeps leaving
`0x82A1CF`..`0x82A1D9` alone (our jump is there) and no longer has a wrong chunk at that reader. Its stand-down text
"its stub keeps the 14-bit decode" is out of date from this build on. With an older ts3native the gap stays, so
Atlas should check the ts3native version before it calls R7 covered.

No switch added: the behaviour is part of `PortalFlagRefs` (release default 1) and cannot differ from before on a
graph of 16,384 chunks or fewer. No counter slot: two log lines (`... lot portal chunks are rebuilt ...` once,
`... chunk not rebuilt ...` first 8). Not run in game: no world above 4096 m exists to run it on yet.
