# 547. Memory stream buffers: the allocation site `0x4DEC17`, and `[StreamBuf]` (2026-11-11)

Static RE on TS3W.exe 1.67 (Capstone disassembly of the retail copy, sha256 `b78a3d53...`, 14,510,400 bytes; every byte
window quoted here was read from that copy), `native/ts3native/streambuf.cpp` with its selftest and host bench. The
measurements in §1 come from `[BigBlock]` Stats (docs/543, one 14 min run). **v1 (separate blocks) ran in the game: §14.
v2's arena, compressor tables and package buffers (§11-§13) ran in the game: §19. §15 Grow, §16 Timing and §17 Assemble
ran once (run E of §19). §21 PreZero and SkipClear and §23 Shared have not: selftest only.** §6 describes
the sites of v1, which v2 keeps unchanged; where §6 and §8 speak of the pool's blocks, §11 replaces them.

Tags: [verified] = read in the disassembly, or exercised by the selftest; [measured] = a run's numbers; [inferred] =
reasoning that a game run has to confirm.

## 0. Short answer

- **What allocates at `0x4DEC17`:** the constructor of EA's refcounted stream buffer (`EA::IO::SharedPointer`, vtable
  `0xFB84D0`), called only from `MemoryStream::Realloc 0x4DEE40`. Every `EA::IO::MemoryStream` (vtable `0xFB5BCC`) that owns
  its bytes gets them here [verified].
- **Why 0.7 and 1.4 MB, 20-30 times per second:** a stream never grows in place: each size change is a new buffer, a copy
  and a free [verified]. The async resource loader gives every streamed record one stream sized once to the record's raw
  size, and for a compressed record a second one sized to the decoded size [verified code]. A 1024x1024 DXT5 texture with
  mips is 1,398,256 bytes, DXT1 699,192: with the header these are exactly the 1408 KB and 704 KB classes [inferred from
  the sizes]. EA's heap gives a request of 128 KB or more its own `VirtualAlloc` (docs/543), so each such stream costs one
  reserve + commit, ~340 first-touch page faults and one release.
- **Fix:** `[StreamBuf] Pool=1` serves these buffers from memory of the module's own and takes them back when the game
  frees the stream's data. Three byte-checked sites in the stream code; the general allocator is not touched. Default
  off. v1 kept separate blocks; v2 keeps **one arena**: a single reservation made once, placed at the highest address
  that fits, cut into 64 KB slices that are committed on use and decommitted when idle (§11). Its address-space cost is
  the reservation and nothing else; v1's 48 MB of scattered blocks cost about 100 MB of the largest free range (§14).
- **Two more consumers on the same arena (v2):** the RefPack compressor's work tables (`Scratch=1`, §12) and the package
  writer's two record buffers (`Package=1`, §13).
- **Why it is safe:** the buffer's lifetime is the SharedPointer's, and the game frees stream data in exactly two places
  [verified, whole `.text` searched]; both are hooked, and a pointer that is not the pool's goes to the game's free
  unchanged. The pool hands a reused block out zero-filled (default), as the OS hands out a fresh one.
- **Host bench** (§8): 1365 KB buffer filled once: game path 342 us, arena 52 us (zero-filled) or 34 us (not filled).
- **What it adds beside `[BigBlock]`:** this caller (41% of all direct-block traffic by count, 58% by bytes) stops
  reaching the direct path at all, with at most `PoolMB` held and nothing held 5 s after streaming stops.

## 1. The measured target [measured, docs/543 run]

14 min, two world-travel round trips, large save: 59,588 direct blocks = 50.8 GB. One caller, `at=0x004DEC17
via=0x004DEE8C`: n=24,161, 29,507 MB, frees 24,158, live 3 blocks / 3.3 MB at the mark, peak concurrent 27.4 MB, average
lifetime 18 ms, 100% freed within 1 s. Classes: 1408 KB and 704 KB.

## 2. The classes [verified]

**MemoryStream** (0x28 bytes + header; vtable `0xFB5BCC`; constructors `0x4DF0C0` (empty; 66 callers), `0x4DF120` (wraps
given data through SetData `0x4DEC90`; 57 callers), copy constructor `0x4DEC30`):

| Offset | Field | Evidence |
|---|---|---|
| +0x04 | `SharedPointer*` (the bytes) | `0x4DEE44` `8B 43 04`, released in the destructor `0x4CDC40` |
| +0x08 | allocation name (`const char*`) | `0x4DF0E0`; passed to the constructor at `0x4DEE7F` |
| +0x0C | refcount of the stream | Release `0x455A00` |
| +0x10 | size | Read `0x4DEFAF`, SetSize `0x4DF1C7` |
| +0x14 | capacity | Realloc `0x4DEEE9` `89 6B 14` |
| +0x18 | position | Write `0x4DF093` |
| +0x1C | byte: resize enabled | SetOption `0x4DEAC0` option 1; tested by Write `0x4DF014`, SetSize `0x4DF1A1` |
| +0x20 | float: resize factor (default from `[0x1020A7C]`) | option 2; Write `0x4DF02C` `D8 4E 20` |
| +0x24 | resize increment | option 3; Write `0x4DF03A` `DA 46 24` |

Vtable `0xFB5BCC`: +0x1C GetSize, +0x20 SetSize `0x4DF190`, +0x24 GetPosition `0x4DEB70`, +0x28 SetPosition `0x4DEF30`,
+0x30 Read `0x4DEFA0`, +0x38 Write `0x4DEFF0`.

**SharedPointer** (0x14 bytes, allocated by `0x4E0510` with a 0x10-byte header that holds the allocator; vtable `0xFB84D0`,
deleting destructor `0x6D8F20`):

| Offset | Field |
|---|---|
| +0x04 | allocator (`ICoreAllocator*`; vtable +4 aligned Alloc, +8 `Alloc(size, name, flags)`, +0xC `Free(ptr, size)`) |
| +0x08 | data |
| +0x0C | refcount |
| +0x10 | byte: free the data when the count ends |

Constructors: `0x4DEBA0` (wraps a given pointer; one caller) and `0x4DEBE0` (allocates `size` bytes; **one caller**,
`0x4DEE87` in Realloc). The allocating one, bytes `0x4DEC06`-`0x4DEC2A`:

```
8B 10          mov edx,[eax]        ; allocator vtable
8B 52 08       mov edx,[edx+8]      ; Alloc(size, name, flags)
6A 00          push 0
51             push ecx             ; name: the stream's, else "Services/EAIO/EAStreamMemory/data" (0xFB84D4)
8B 4C 24 10    mov ecx,[esp+10h]    ; size
51             push ecx
8B C8          mov ecx,eax
FF D2          call edx             ; return address 0x4DEC17
89 46 08       mov [esi+8],eax
C7 46 0C 00..  mov [esi+0Ch],0
C6 46 10 01    mov byte [esi+10h],1 ; free the data
```

## 3. Growth: allocate, copy, free [verified]

`MemoryStream::Realloc 0x4DEE40` (thiscall, new capacity, `ret 4`):

1. allocator = the old SharedPointer's, else the default (`0x4D9AD0` -> `[0x11C7984]`, first use `0x931020`);
2. new capacity not 0: a new SharedPointer object (`0x4E0510`, name `.../EAStreamMemory/ptr`) built by `0x4DEBE0` with
   the new capacity; refcount 1; no data = fail;
3. old buffer present: `memcpy(new, old, min(new capacity, old capacity))` (`0x4DEEB9`-`0x4DEECB`: it copies the old
   **capacity**, not the old size);
4. old SharedPointer released inline (`0x4DEED3`-`0x4DEF11`); `[this+4]` = new, `[this+0x14]` = new capacity.

Callers of Realloc (three, all found by a rel32 search):

| Caller | Return | New capacity |
|---|---|---|
| SetSize `0x4DF190` | `0x4DF1C3` | exactly the new size (only when it differs from the size, and only with resize enabled) |
| Write `0x4DEFF0` | `0x4DF061` | `max(capacity * factor + increment, position + bytes)` (x87, truncated) |
| SetPosition `0x4DEF30` | `0x4DEF7F` | the position, when past the size with resize enabled |

So the pool needs no growth logic of its own: a grow is a new request, the game's own copy, and a free of the old block.

## 4. Where the data is freed [verified]

Two places, both guarded by `cmp byte [esi+10h],0` after the refcount ended, both `allocator->Free(data, 0)`:

- `SharedPointer::Release 0x4DEA80` (65 callers), bytes `0x4DEA93`: `80 7E 10 00 74 10 | 8B 4E 04 8B 01 8B 56 08 8B 40 0C
  6A 00 52 FF D0 | 8B 16 8B 02 6A 01 8B CE FF D0` (then the object's deleting destructor);
- Realloc's inline copy, bytes `0x4DEEF3`: `80 7E 10 00 74 10 | 8B 4E 04 8B 11 8B 46 08 8B 52 0C 6A 00 50 FF D2 | ...`.

Search of the whole `.text` for other copies: (a) `cmp byte [reg+10h],0` + `je` + `mov ecx,[reg+4]` with a `push 0` in the
next bytes: these two only; (b) the refcount idiom `mov eax,[reg+0Ch]; cmp eax,1; jle`: these two, the stream's own
Release `0x455A00` and one unrelated class at `0x4708C3`. The SharedPointer vtable is stored in 29 places (inlined
constructors of the wrapping kind); none of them frees. No code detaches the data from a SharedPointer (the class has
only the constructors, the accessors and Release) [inferred for the inlined users: they were not all read].

Pointers into the buffer do escape (a record object wraps the stream, factories read from it), but only together with a
reference on the stream or its SharedPointer: the copy constructor `0x4DEC30` and SetData share the SharedPointer and
count (`add [ecx+0Ch],1`). The buffer therefore lives exactly as long as the SharedPointer, with the pool or without.

## 5. The consumer: the async resource loader [verified code; hot path inferred]

`AsyncLoader_JobDecompress 0x72A150` (docs/39 §1.4; runs on the loader's job thread) per streamed record:

- `0x727540` (called at `0x72A220` / `0x72A487`): `new MemoryStream` (name `ResourceLoad/AsyncLoader/MemoryStream`),
  resize on, `SetSize(record stream GetSize)` (`0x7275B3`), resize off, then `record stream->Read(data, size)`
  (`0x7275F0`, vtable +0x30) and a `RecordGeneric` object around the stream (`0x4B3650`). The check after the read is
  `test eax,eax / je fail`: **a short read is accepted**.
- compressed record: a second `MemoryStream` (`0x72A288`), `SetSize(decoded size)` (`0x72A2B2`, return `0x72A2B4`),
  then the RefPack decoder writes into its data (`0x72A3CD`) and the result must equal the expected size
  (`cmp esi,eax` at `0x72A3CF`, else the load fails); the raw stream's record is closed and released (`0x72A3F7`).

So a streamed record costs one buffer of its raw size, plus one of its decoded size when compressed; each is sized once
(no growth), filled once from end to end, read by the factory and freed with the record: the 18 ms. Other SetSize / Write
users of memory streams (save writing through `MemStream_Write`, CAS, downloads, script streams) reach the same site
with mostly small sizes [inferred; the `who` lines of the Stats run list every sizer with its sizes].

**Zero-fill:** a fresh direct block is zero pages. The decoded stream is checked to be completely written, but the raw
stream of `0x727540` is not: after a short read its tail is whatever the buffer held. With the game's allocator that is
zeros for 128 KB and more (and heap garbage below, so no code can rely on zeros in general); from a pool it would be an
older resource's bytes. That cannot be proven harmless from the code, so `Zero=1` is the default: a reused block is
zero-filled over the requested bytes (measured cost §8). `Verify=2` measures in a game run whether any consumer leaves
bytes unwritten; only a run with `unwrittenN=0` justifies `Zero=0`.

**Threads and locks:** the stream code takes no lock [verified: its only calls in `0x4DEA80`-`0x4DF1D3` are the allocator, memcpy `0xF7E8A6` and its own functions]; EA's heap lock
(`[heap+0x4E4]`) is held only inside the allocator's Malloc / Free (docs/543). Buffers are allocated on the loader's job
thread and freed where the record's last reference goes (the frame that completes loads, `0x7294F0`) [inferred]; many
are alive at once (27.4 MB peak = about 20 buffers). So the pool is global with one SRW lock, held for table updates
only (never across a system call, a fill, or the game's allocator), and nests inside nothing.

## 6. `[StreamBuf]` (streambuf.cpp)

Patched sites (at DllMain, before the exe's entry point; the three windows `0x4DEBE0`-`0x4DEC2A`, `0x4DEA80`-`0x4DEAB6`,
`0x4DEE40`-`0x4DEF21` must match byte for byte, else `SKIP stream buffer: bytes differ ...` and nothing is written):

| Site | Original bytes | New bytes | Effect |
|---|---|---|---|
| `0x4DEC0B` (12) | `6A 00 51 8B 4C 24 10 51 8B C8 FF D2` | `51 50 53 FF 74 24 14 E8 rel32` | push name, allocator, ebx (the stream: Realloc's `this`), size; `call StbAlloc`. Return address stays `0x4DEC17` |
| `0x4DEA99` (16) | `8B 4E 04 8B 01 8B 56 08 8B 40 0C 6A 00 52 FF D0` | `56 E8 rel32 EB 08 CC*8` | `push esi; call StbFree` (the SharedPointer); jump to `0x4DEAA9` |
| `0x4DEEF9` (16) | `8B 4E 04 8B 11 8B 46 08 8B 52 0C 6A 00 50 FF D2` | `56 E8 rel32 EB 08 CC*8` | the same in Realloc; jump to `0x4DEF09` |

Read, not patched: the heads of SetSize / Write / SetPosition up to their Realloc call (byte-checked; they fix where on
the stack the sizer's return address lies: `how` and `at` of the `who` lines). If they differ, the module still installs
and the `who` rows stay empty.

- **StbAlloc:** not armed = the game's call. Else counts the request; a request of `MinKB` to `MaxBlockMB` with `Pool=1`
  takes slices of the arena (§11), else the game's own call (`fallback`). Everything else is the game's call with the
  same arguments.
- **StbFree:** a pointer inside the arena's range (a compare of two addresses, read without a lock; no memory is
  touched) is the arena's and its slices become free; every other pointer goes to the game's `Free` as before. A block
  handed out while the pool was on comes home even after the pool is switched off.
- **Upkeep** (own thread, 500 ms): free slices nobody took for `IdleSec` are decommitted, all free ones when nothing
  uses the arena any more. Every 5 s: `Enabled`, `Stats`, `Pool`, `Scratch`, `Package`, `Zero`, `Verify` are read again
  from the ini (run-time kill switch), and every written site is compared with our bytes: a foreign change stops the
  module for the session.
- **The `who` rows (fixed in v2):** in the game every v1 row read `at=0x00000000 how=?` [measured]. The stub gave the
  caller walk the address of its first parameter. The v1 build of that function carried a `/GS` cookie, and with it
  the compiler's copies of the parameters among the locals; `&size` was the address of the copy, so the walk read the
  stub's own frame and never found the constructor's return address [verified: disassembly of a v1 build, `mov
  eax,[esp+158h]` / `mov [esp+4],eax` at the head and `lea eax,[esp+1Ch]` as the walk's argument]. v2 starts the walk
  at the return address slot (`_AddressOfReturnAddress() + 1`), which no compiler option moves, and a selftest runs
  the patched constructor and checks the frame the walk receives.
- **Process exit:** the stubs pass through and take no lock.
- **Apex Radiance:** no Apex site or hook lies in `0x4DE000`-`0x4DFFFF` (docs/data/apex-sites.csv, apex-hooks.csv), so
  there is no stand-down. `[BigBlock]`'s two call operands (`0x4E512C`, `0x4E5308`) are untouched.

Log lines at every `diag: mem` mark: `streambuf: #n` (requests, big = 128 KB+, served = from the arena without a system
call, fresh = from the arena with one commit call, fallback, tooBig, frees, xthreadFrees, grows, growCopyMB, liveN /
peakLiveN, zeroMs / zeroMB, errors: dfree, vaFail, uafw, unwrittenN / unwrittenKB, lost, untracked), `streambuf: arena`
(§11), `streambuf: sizes`, one `streambuf: who` per requester (at, via, how = SetSize / Write / SetPosition / Scratch /
Package, n, MB, big, maxKB, grows, served, fresh, fallback, frees, xthreadFrees, live, peakMB, avgLifeMs, under1s) and
`streambuf: threads`.

**Verify** (debug): 1 = a freed block is filled with `0xB5` and checked when reused (`uafw`). 2 = also every pooled
block is handed out filled with `0xA7` instead of zeros, and at the free the aligned 8-byte groups that still hold it are
counted (`unwrittenN`, first 16 logged with the sizer). Limits: a read after free is invisible; so is a read before a
write that is overwritten later; a consumer that itself writes eight `0xA7` in a row is miscounted; with 2 an unwritten
byte reads `0xA7`, not 0, so a consumer that does depend on zeros misbehaves in that run (which is the test).

## 7. The other short-lived callers of docs/543 [verified tags; not built]

| Caller (BigBlock `at`) | What it is | Same fix? |
|---|---|---|
| `0x4A7216` (367 x 1.1 MB, 7 ms) | `ResourceLoad/PackedFile/PackedRecord`: the packed output buffer of a record write, allocated in `0x4A7030` (allocator `[this+0x10]` vtable +8) | built in v2: `Package=1`, §13 (the v1 text called it a read buffer: wrong, it is the write side) |
| `0x4A89F7` (260, 8 ms) | `ResourceLoad/PackedFile/RecordData`: the record's bytes (`size + 0x10`) read back for that write, in the commit `0x4A8910` | built in v2: `Package=1`, §13 |
| `0x7E9010` via `0xB709E8` (58 x 7.9 MB, 42 ms) | the real caller is `0xB709E8`: an EASTL vector growing by doubling (`lea ecx,[eax+eax]`, `EASTL/allocator.h`); `0x7E9010` follows a plain call, not an allocation, so it is a stale stack word | no: container growth; a `reserve` at its owner would be the fix |
| `0x6169AD` (183, 0 ms) | `Memalign(16)` scratch in `0x6165A0` | no (aligned scratch, own site) |
| `0x4EC140` (384, 0 ms), `0x4EC122` | `Services/UTFFoundation/CompressionRefPack`: the compressor's two work tables | built in v2: `Scratch=1`, §12 |
| `0x4445B9` (45, 84 ms) | `UI/EAGimex/Buffer`: an image decode raster | no (rare) |

## 8. Selftest and host bench [verified]

`streambuf-test.h` (in `selftest.exe`), v2:

- size policy, the range test, the Verify helpers; the sizer read from a stack laid out as the checked code leaves it;
- **placement** (`StaPlace`) against made-up free-range lists and the two measured in the game (start and end of a
  travel run): the top of the large range, a smaller range above it preferred, ends off the 64 KB grid, a range that
  is long enough but not from a boundary, the last user address without overflow, the `MinFreeMB` floor; then the
  scan of the test process's own map, and the reservation made from it checked with `VirtualQuery` (one region, nothing
  committed); no arena = every request to the game;
- the arena step by step: commit on first use, the same slices again without a system call and zero-filled, best fit
  (the smaller run first), a block split between two later requests, small / too big / full arena to the game,
  `stranded` (enough free slices, no run), double free, a pointer into a block, idle decommit with a neighbour's own
  clock, recommit as zeros without a fill, kill switch, process exit, Verify 1 (also across a split) and 2, `Zero=0`,
  Stats alone;
- a **randomized run of 8000 steps** (fixed seed, the measured sizes plus random ones, a 12 MB arena driven to full)
  against the arena written the plain way: every slice's state compared after every step, every block filled with a
  signature and checked at its free, and every 250 steps the OS asked whether the range is still one reservation with
  exactly the model's slices committed (1506 served without a system call, 559 with a commit, 1301 to the game of
  which 429 stranded, 476 decommits);
- **four threads** freeing each other's blocks while a fifth runs the decommit tick with a 3 ms idle time (12,000
  requests, nothing lost, the OS's committed bytes equal the module's count at the end);
- the patches: **patched copies of the game's constructor and Release bytes are executed** against a fake allocator
  (fields, stack balance, refcount path, small request, "free the data" clear, and the frame the caller walk is
  handed); Realloc's copy is patched and compared; **each of the ten patched windows of the compressor and the package
  writer is executed on its own** with the registers and frame slots the game has there (pointer and size reach the
  stub, stack and callee-saved registers come back unchanged, the allocation calls end at the game's return
  addresses); a foreign byte in any function blocks that part's install; a changed site stops the module; Scratch off
  for Apex, parts whose sites are not written cannot be switched on.

Bench, us per buffer, 300 each (low priority, 4 cores, a game running beside it); "filled once / not touched":

| | 1365 KB | 682 KB | 512 KB (a compressor table) |
|---|---|---|---|
| game path (VirtualAlloc + release) | 341.7 / 6.6 | 161.3 / 4.8 | 128.7 / 3.8 |
| arena, `Zero=1` | 51.9 / 18.6 | 21.5 / 6.6 | 14.3 / 4.8 |
| arena, `Zero=0` | 34.3 / 0.5 | 16.3 / 0.3 | 10.6 / 0.3 |
| arena, cold (every block committed and decommitted) | 331.6 / 10.1 | 156.5 / 6.6 | 118.1 / 5.3 |

The best-fit search itself: 0.50 us per request + free in a 48 MB arena with 17 blocks live and 17 holes.

The game path's cost is the first touch of fresh pages, not the two system calls; a cold arena block costs the same,
which is why slices stay committed for `IdleSec`. At 25 buffers per second the saving is about 6 ms/s: small. The value
is the address space: tens of thousands of reserve / release cycles become one reservation.

## 9. Test plan (written for v1; (a)-(c) were run with v1, §14; the v2 runs are in §11.6)

All with `[MemDiag]` on (the `diag: mem` marks) on the 2-trip travel loop. `[BigBlock] Enabled=1 Stats=1` beside it
gives the `who` line of `0x4DEC17`.

**(a) Stats** (confirms §5 before anything changes):

```
[StreamBuf]
Enabled=1
Stats=1
```

Read: `OK   stream buffer` at start (not `SKIP`); in the last `streambuf: #n`: `big` about 24,000 and `bigMB` about
29,500 (the BigBlock figures), `grows` small beside `big`; the top `who` rows should be `how=SetSize` with `at=0x007275B5`
and `at=0x0072A2B4` (the loader), `avgLifeMs` about 18, `under1s` 100%, `peakMB` about 27; `threads`: which thread
allocates, and `xthreadFrees`. Decides: PoolMB (peak + margin), whether other sizers matter. `untracked=0`, `lost=0`.

**(b) Pool + Verify**:

```
[StreamBuf]
Enabled=1
Stats=1
Pool=1
Verify=2
```

Pass: no crash, no texture corruption on screen, `dfree=0 vaFail=0 uafw=0 lost=0`; `served / (served + fresh +
fallback)` at least 90%; `fallback` under 1% of `big`; `peakPoolMB` at most 48; `poolMB=0` at a mark 10 s after
streaming stops. `unwrittenN`: 0 = `Zero=0` is safe for these consumers; above 0 = keep `Zero=1` (the logged lines name
the sizer). Then one run with `Verify=1`, `Zero=1`: same pass lines.

**(c) Pool beside BigBlock** (`Pool=1`, `Verify=0`, `[BigBlock] Enabled=1 Stats=1`): BigBlock's `who` line
`at=0x004DEC17` must drop from about 24,000 requests to this module's `fallback + tooBig` count (near zero; the row may
be missing), and BigBlock's total `allocs` by about the same 24,000; `priv` and the largest free block (`big`) of the
`diag: mem` lines not worse than in (a).

**(d) Timing ABBA** (clean night, `Pool=1 Zero=1 Stats=0 Verify=0` against `Enabled=0`): frame time and hitch counts
during travel must not be worse; the expected gain is within noise (§8). The deciding number for shipping is (c)'s
largest free block over a long session, not (d).

## 10. Risks

- A free path of stream data that the search of §4 missed would hand a pool block to the game's `Free` (heap
  corruption). Mitigation: two independent pattern searches, `Verify`, the `lost` counter, default off.
- Up to `PoolMB` (48) stays committed while streaming; a 32-bit process near its limit loses that much headroom for up to
  `IdleSec`. A failed `VirtualAlloc` falls back to the game's path (`vaFail`).
- `Zero=0` is unproven until run (b) says `unwrittenN=0`.
- The sizer attribution reads fixed stack slots; it is used for the log only.
- v2: the arena's `PoolMB` of address space is held for the whole session, also while nothing streams (v1 gave its
  blocks back). It is never released, so a session that runs out of address space elsewhere cannot get it back.
- v2: a block that the game never frees (a load that throws between allocation and free) keeps its slices for the
  session; with the game's allocator the same event leaks the same bytes.
- v2: freed arena memory stays readable and is reused within milliseconds, where the game's released block faulted on
  a late access. A use after free in a consumer turns from a crash into silent reuse; `Verify=1` exists to look for it.

## 11. The arena (v2) [verified by the selftest; game runs: §19]

### 11.1 Why

v1 made one `VirtualAlloc` reservation per pool block. In the game its 48 MB pool showed as 385 extra private regions
of 1 MB or less (+76 MB) spread through the address space, and the largest free range was about 100 MB smaller than
without the module at all three home windows of a travel run [measured, §14]: twice the pool's size, because every
scattered block also cuts a free range in two. `[BigBlock]`'s reuse cache, which keeps the game's own reservations
where they were, cost nothing in the same run.

### 11.2 Design and where it comes from

One reservation of `PoolMB` (default 48), made once, cut into 64 KB slices (the unit Windows reserves in). One state
byte per slice, outside the arena: in a block, first slice of a block, committed, poisoned, being decommitted.

| Decision | Taken from | Changed for this case |
|---|---|---|
| One reserved range, fixed slices, a freed range reusable at once, decommit ("purge") later and only for ranges that stayed free, which the purge first claims so no request can take them half-way | mimalloc's arena (`src/arena`, MIT). The design only; no code was copied | 64 KB slices instead of 32 KB; one idle clock per slice instead of one expiry per arena (a few hundred slices: the cost is nothing, and a hot slice is never decommitted because a cold neighbour expired) |
| All bookkeeping outside the managed memory; "is this pointer mine" = a range compare on the one reservation | the large-block heap of the New Vegas Heap Replacer (a 32-bit game's allocator replacement; GPL-2.0: idea only) | nothing is ever read from or written to arena memory to manage it, so free slices can be decommitted and a foreign pointer is never touched |
| Placement at the highest address that fits | Chromium PartitionAlloc searches its pool from the end for transient reservations (`FindChunkFromEnd`; BSD) | computed from a `VirtualQuery` scan of the live map, so the log can say which free range was taken and what it cost (§11.3) |
| A recommitted page is zero, a reset one is not | Microsoft's `VirtualAlloc` documentation | used: only slices that kept an older block's bytes are zero-filled; newly committed ones are not touched (mimalloc does not use this on Windows) |

Looked at and not taken: TLSF (O(1) and low fragmentation, but it keeps block headers and free-list links inside the
managed memory, so a free block's first and last page could never be decommitted); Scudo's secondary cache and
rpmalloc's span cache (caches of separate mappings: what v1 and `[BigBlock]` already are); jemalloc's decay curve (its
middle state needs `MADV_FREE`, which Windows lacks; a plain idle time gives the same result here); PartitionAlloc's
own 32-bit mode (it has no pool on 32-bit: one reservation per request); size classes inside the arena (§11.4).

### 11.3 Placement

Rule (`StaPlace`): among the free ranges of the address space, the highest 64 KB boundary at which `PoolMB` fits.
`StaReserve` scans the map with `VirtualQuery`, reserves at that address, scans once more if another thread took the
place in between, and as a last resort asks the OS with `MEM_TOP_DOWN` (the same rule, applied by the OS). No
reservation, or one that would cut the largest free range below `MinFreeMB` (512): no arena, and every request takes
the game's own path. The arena is reserved at DllMain when a part that uses it is on in the ini, else by the module's
own thread when such a part is first switched on; never on a game thread.

Evidence [measured: address-space maps of two travel runs, in-process renderer, 16 snapshots each]:

| Run | 45 s after the start: free ranges of 32 MB and more | End of the run |
|---|---|---|
| nothing on | `0x6D473000` + 96 MB, `0x772D9000` + 141 MB, `0x95FF0000` + 1692 MB | `0xB5AC0000` + 1185 MB only |
| `[BigBlock]` with top-down blocks | `0x7D260000` + 45 MB, `0x7FFE3000` + 1793 MB | `0xAC740000` + 1035 MB, `0xEE506000` + 34 MB |

- After the first minute there is one large free range and nothing else of 32 MB or more: every middle-sized range is
  filled by the game's bottom-up allocations. A 48 MB reservation therefore always costs 48 MB of the large range or of
  something the game would have used instead of it; the aim is that it never costs more.
- The large range's lower edge rose by about 500 MB during the run (`0x95FF0000` to `0xB5AC0000`): that is where the
  game grows, and where blocks of mixed lifetimes pile up. Its upper edge did not move (`0xFFBF0000` in the first run
  from the first snapshot to the last).
- So the upper edge is the one place where a permanent block can never end up between two ranges that would otherwise
  join when the game frees memory, and where it is out of the way of the growth. A block at the lower edge costs the
  same on the day it is made, but everything allocated before it lies below it, and when that is freed the hole
  cannot join the large range.
- When a smaller range above the large one fits (the 34 MB one in the second run would fit a 32 MB arena), the rule
  takes it and the large range is not touched.
- Pointers above `0x80000000` are nothing new for the consumers: in both runs the game's own blocks were already being
  placed above `0x95000000` 45 s after the start.

The start line names the choice: `streambuf: arena reserved: 0x...-0x..., 48 MB, one reservation, nothing committed; at
the top of the free range 0x... + N MB (rule: the highest address that fits; K free ranges could hold it); largest free
range before: 0x... + N MB, after: N MB`.

### 11.4 Layout inside the arena

A request of n bytes takes ceil(n / 64 KB) whole slices: the smallest free run that holds them, the lowest on a tie
(best fit). No size classes: the dominant sizes (576, 704, 1408, 2752, 3456, 4096, 5504 KB = 9, 11, 22, 43, 54, 64, 86
slices) are not multiples of each other, so classes would strand space, while runs of slices split and join for free
(a freed block's slices are simply free; neighbours need no merge step). Committed and decommitted slices count the
same for the search.

An offline comparison on that size mix (weights assumed, lifetimes 18 ms, arrival rate swinging between 0.3 and 2.2
times the mean; 200,000 requests per cell; the load is far above the game's so that requests are refused at all)
counted the requests no run could serve:

| Arena, mean bytes live | first fit (lowest address) | best fit | two-ended (under 2 MB low, larger high) |
|---|---|---|---|
| 48 MB, 8 MB | 46 | 36 | 42 |
| 48 MB, 12 MB | 744 | 572 | 618 |
| 48 MB, 16 MB | 2561 | 2312 | 2528 |
| 64 MB, 12 MB | 36 | 21 | 39 |

Best fit refused the fewest in every cell, so it is what the module does. The search is a walk over the state bytes
(768 for 48 MB) under the module's lock: 0.5 us per request and free on the host (§8).

Size: the measured peak of stream buffers in use is 27.4 MB; v1's 48 MB pool reused a block for 94% of the requests
and a 16 MB one for 78% [measured]. In an arena a request is refused only when no run of free slices holds it, so 48 MB
leaves 1.75 times the measured peak. The run-time counter `stranded` (requests refused although enough slices were
free) says whether fragmentation ever costs a request in the game.

### 11.5 Commit policy

- Slices are committed when a block first needs them: one `VirtualAlloc(MEM_COMMIT)` from the first to the last
  uncommitted slice of the block (zero pages; a committed slice in between keeps its bytes). Counted as `fresh`.
- A block whose slices are all committed costs no system call (`served`). With `Zero=1` the slices that held an older
  block are zero-filled over the requested bytes.
- A freed block's slices are free at once and stay committed. The upkeep thread decommits free slices nobody took for
  `IdleSec` (5 s), one run of neighbours per system call, outside the lock. The run is marked first, so a request
  cannot take a slice that is being decommitted. The reservation itself is never released.
- RAM cost: committed and free bytes (`idleMB`), at most the arena, zero `IdleSec` after the last use.
- A failed commit puts the slices back as they were and the game's own path answers (`vaFail`).

`streambuf: arena #n` at every mark: `base`, `arenaMB`, `oneReservation` (1 when a `VirtualQuery` walk finds every
region from the base to the end naming the base as its allocation base), `osCommitMB` (what that walk found committed),
`commitMB` / `inUseMB` / `idleMB` (the module's own counts), `peakInUseMB`, `peakCommitMB`, `freeMB`,
`largestFreeRunMB`, `freeRuns`, `commits`, `commitMB_total`, `decommits`, `stranded`, `noArena`.

### 11.6 Game runs for v2 (runs 1 and 2 done: §19; 3 and the new parts open)

Same travel loop as §14, `[BigBlock]` reuse and `[MemDiag]` on.

1. `Enabled=1 Stats=1 Pool=1` (48 MB). Pass: the start line places the arena near the top; `oneReservation=1` and the
   same `base` at every mark; `osCommitMB` = `commitMB`; error counters 0; `fallback` under 1% of `big`; `stranded` 0 or
   a handful; the `who` rows name `0x007275B5` and `0x0072A2B4` with `how=SetSize`; the largest free range at the three
   home windows about 48 MB under the `[BigBlock]`-only run (1666 / 1373 / 1192), not 100; in the map one 48 MB private
   region and none of v1's small ones.
2. The same with `Scratch=1 Package=1`: `[BigBlock]`'s rows `0x4EC140`, `0x4EC122`, `0x4A7216`, `0x4A89F7` drop to
   this module's `fallback + tooBig` for them; saves made in the run load again; the `who` rows `how=Scratch` /
   `how=Package` show `frees` = `n` and `liveN=0` at rest.
3. One run of 2 with `Verify=2`, then one with `Verify=1`: `uafw=0`; `unwrittenN` by requester.
4. The same on the configuration with the renderer in its own process, where two large shared views are mapped into
   the game: the start line must still place the arena, and the views must still map.

## 12. The compressor's work tables: `Scratch` (v2) [verified statically and by the selftest; one game run: §19]

**What it is.** `[BigBlock]`'s caller `0x4EC140` (18,000-40,000 blocks = 9-20 GB per travel run, one at a time, lifetime
0 ms) is in the RefPack compressor `0x4EC0A0` (cdecl: destination, capacity, source, length, allocator, mode;
`0x4EC0A0`-`0x4EC1FB`), which saves and caches go through record by record. Per call it allocates from the allocator
it is given, name `Services/UTFFoundation/CompressionRefPack` (`0xFB9030`):

| Table | Size | Allocation | Free |
|---|---|---|---|
| hash heads | `0x40000` (256 KB); 0x400 bytes of stack instead when the source is 16 KB or less | call `0x4EC120`, return `0x4EC122` | `0x4EC1D8`, skipped for the stack one |
| chain links | (window mask + 1) x 4 = `0x80000` (512 KB), or `0x10000` in the short-window mode | call `0x4EC13E`, return `0x4EC140` | `0x4EC1C3` |

EA's heap turns the 512 KB one into a direct block of 516 KB (the "504-576 KB" of the table) and the 256 KB one too
(caller `0x4EC122`).

**Who allocates and frees.** The compressor itself, both in the same call, on the calling thread, before it returns.
The pointers are held in its frame (`[ebp+18h]`, `[ebp+1Ch]`) and passed by value to the two cores `0x4EB750` /
`0x4EBB90`, whose only calls are `memset` and `memcpy` (`0xF7E8B8`, `0xF7E8A6`) [verified: every call instruction in
`0x4EB750`-`0x4EBFC1` listed]. Nothing stores them. The function has two callers, both in the codec's slot function
`0x4EC200` (`0x4EC23D`, `0x4EC276`); no other code calls it or refers to it [verified: rel32 and raw-address search of
the whole image].

**The two `via` values are one consumer.** `via=0x004EC242` is the return into `0x4EC200`. `via=0x004A62D2` follows a
lock call in the package writer `0x4A62B0`, which is further up the same call chain: it is a stale stack word that the
walk meets first when the compressor's frame holds the 0x400 bytes of stack table (a source of 16 KB or less), whose
bytes are whatever earlier calls left there. Same function, same two sites.

**One at a time?** Each call holds at most one table of each kind and frees them before returning, so one thread has
at most two blocks out. Several threads may compress at once (`[SaveCompress]`'s workers call this function too); the
arena does not depend on it either way.

**Read before written?** Both cores start with `memset(heads, 0xFF, whole table)` (`0x4EB763`: 0x400; `0x4EBBA3`:
`0x40000`), so the heads are never read uninitialised [verified]. The links are not initialised by the code; a link
is written when its position is entered and read when a chain reaches that position [inferred from the algorithm, not
proven instruction by instruction]. In the short-window mode the same code runs on a 64 KB table from the heap, which
is not zeroed, so the algorithm cannot depend on zeros [inferred]. `Zero=1` applies here as everywhere, so nothing
rests on this.

**Sites** (`Scratch=1` at the start; the whole function `0x4EC0A0`-`0x4EC1FB` must match byte for byte, else `SKIP`
and none is written; no branch of the function or from outside lands inside a window [verified by decoding it]):

| Site | Original bytes | New bytes |
|---|---|---|
| `0x4EC112` (16) | `6A 00 68 30 90 FB 00 68 00 00 04 00 8B CF FF D2` | `57 68 00 00 04 00 0F 1F 44 00 00 E8 rel32`: push allocator, push 40000h, `call StbScratchAlloc`; returns to `0x4EC122` |
| `0x4EC12D` (19) | `6A 00 8D 14 8D 04 00 00 00 68 30 90 FB 00 52 8B CF FF D0` | `8D 14 8D 04 00 00 00 57 52 0F 1F 44 00 00 E8 rel32`: the size as before, push allocator, push size, call; returns to `0x4EC140` |
| `0x4EC1B9` (12) | `8B 07 8B 50 0C 6A 00 51 8B CF FF D2` | `57 51 E8 rel32 0F 1F 44 00 00`: push allocator, push pointer, `call StbSiteFree` |
| `0x4EC1CE` (12) | the same bytes | the same |

The frees are written first. `StbSiteFree` takes any pointer: one inside the arena goes back to it, every other one
(also NULL) goes to the allocator's `Free` as before, so a free site is safe whatever it is handed. With `Scratch`
off at run time the allocation stubs call the game's allocator with the game's arguments.

**Other modules.** `[SaveCompress]` checks a hash of `0x4EC0A0`-`0x4EC282` before it installs, so Scratch is not
written when `[SaveCompress] Enabled=1`. Apex Radiance's FastCacheCompression replaces the codec slot `0xFB901C` in
front of this function: when Apex is loaded at the start the sites are not written, and when it turns up later
Scratch is switched off (the function is then no longer called).

**Limit.** A compressor call that never returns (an exception in a core) leaves its tables in use for the session.

## 13. The package writer's record buffers: `Package` (v2) [verified statically and by the selftest; one game run: §19]

**What they are.** `[BigBlock]`'s callers `0x4A7216` (484-1,124 blocks, 0.6-2.0 GB per run, life 7-10 ms) and
`0x4A89F7` (323-924, 0.4-1.6 GB, 9-13 ms) are the two buffers of one operation: the package writer committing a
record with compression (`0x4A8910`, thiscall on the package, `ret 8`; one caller, `0x4A8EAE`).

1. `0x4A89E3`-`0x4A89F5`: a buffer of the record's size + 0x10 from the package's allocator (`[package+10h]`), name
   `ResourceLoad/PackedFile/RecordData` (`0xFB26A4`); kept in the frame at `[esp+1Ch]`; two zero bytes are written
   behind the data, then the record's stream reads its bytes into it (`0x4A8A34`).
2. `0x4A8A55` calls `0x4A82C0` (one caller), which calls `0x4A7030` (one caller): it allocates the output buffer of the
   codec's bound (`0x4A7204`-`0x4A7214`, name `.../PackedRecord`, `0xFB257C`), stores it through an out pointer into
   the commit's frame (`[esp+18h]`), and has the codec compress into it (`0x4A727C`).
3. The packed bytes are written to the file (`0x4A8A89` -> `0x4A7E70`) and both buffers are freed.

**Every free** [verified: the three functions read from the first byte to the last; each has exactly one caller and
no reference from data, so the out pointer cannot reach other code]:

| Buffer | Freed at | When |
|---|---|---|
| packed | `0x4A72B0` (in `0x4A7030`) | the codec returned -1; the out pointer is cleared |
| packed | `0x4A8359` (in `0x4A82C0`) | the record did not shrink below the package's ratio; the out pointer is cleared |
| packed | `0x4A8AEF` (commit) | after the write, and when the space request or the write failed |
| record | `0x4A8AFF` (commit) | on every path that follows the allocation (`0x4A8A20`, `0x4A8A38`, `0x4A8A5C` jump to it; the rest fall into it) |

`0x4A7030` has a second allocation with the same name (`0x4A7307`, for a size of 0 or above 40 MB); `0x4A82C0`
refuses those sizes before the call (`0x4A82E4`, `0x4A82F6`), so it is unreachable; it is not hooked, and its block
would be the game's and be passed on by the free stubs.

**Sites** (`Package=1` at the start; the three functions `0x4A7030`-`0x4A7355`, `0x4A82C0`-`0x4A8370`,
`0x4A8910`-`0x4A8BD9` must match byte for byte, else `SKIP` and none is written; no branch lands inside a window):

| Site | Original bytes | New bytes |
|---|---|---|
| `0x4A72A5` (13) | `8B 4B 10 8B 11 6A 00 50 8B 42 0C FF D0` | `8B 4B 10 51 50 E8 rel32 0F 1F 00`: allocator, push it, push pointer, `call StbSiteFree` |
| `0x4A834E` (13) | `8B 4E 10 8B 11 6A 00 50 8B 42 0C FF D0` | `8B 4E 10 51 50 E8 rel32 0F 1F 00` |
| `0x4A8AE1` (16) | `8B 4E 10 8B 11 8B 44 24 18 8B 52 0C 53 50 FF D2` | `8B 4E 10 8B 44 24 18 51 50 E8 rel32 66 90` |
| `0x4A8AF1` (16) | `8B 4E 10 8B 01 8B 54 24 1C 8B 40 0C 53 52 FF D0` | `8B 4E 10 8B 54 24 1C 51 52 E8 rel32 66 90` |
| `0x4A7204` (18) | `8B 4B 10 8B 11 6A 00 68 7C 25 FB 00 50 8B 42 08 FF D0` | `8B 4B 10 68 7C 25 FB 00 51 50 0F 1F 00 E8 rel32`: push name, allocator, size, `call StbPkgAlloc`; returns to `0x4A7216` |
| `0x4A89E3` (20) | `8B 4E 10 8B 11 8B 52 08 53 68 A4 26 FB 00 8D 47 10 50 FF D2` | `8B 4E 10 8D 47 10 68 A4 26 FB 00 51 50 66 90 E8 rel32`; returns to `0x4A89F7` |

**Threads, one at a time, read before written.** The buffers live inside one call on the thread that commits the
record; the arena is global and locked, so it does not matter which thread that is or how many commit at once. The
record buffer is filled by the stream's read and the packed one by the codec; the write takes the packed length the
codec returned [verified]. `Zero=1` covers whatever is not written (the 14 bytes behind the two zero bytes).

**Sizes.** Requests under `MinKB` stay on the game's heap as before; a record above `MaxBlockMB` (8 MB; the table's
12.7 MB peak is one such record [inferred]) takes the game's path (`tooBig`).

**Other modules.** No Apex Radiance site lies in the three functions (its `DpfWriteDirect` entry `0x4A7FC0` and
`[SaveCompress]`'s call site `0x4A850F` are outside the windows). The codec the packed buffer is handed to may be
Apex's or `[SaveCompress]`'s replacement: it gets the same two plain pointers as before.

**Limit.** If `0x4A7030` returns failure with a buffer allocated (only when the package has no codec object), the game
never frees it [verified: `0x4A8A5C` skips the free]; with the arena its slices stay in use for the session.

## 14. v1 in the game [measured: two travel round trips on a large save, about 12 min each, in-process renderer]

- 31,275-32,704 stream buffers of 128 KB and more per run (28.9-31.2 GB); 93.5-94.1% served from an idle pool block;
  `fallback` 0; `dfree`, `lost`, `untracked`, `uafw`, `vaFail` all 0, with `Verify=2` and with `Verify=0`.
- 1,087 and more frees came from another thread than the allocation.
- Zero-fill: 1.33 s per 26.7 GB.
- `Verify=2`: 2,289 buffers were freed with an unwritten tail, so `Zero=1` stays the default.
- Largest free address range (MB) at the three home windows: nothing on 1656 / 1391 / 1186; `[BigBlock]` reuse only
  1666 / 1373 / 1192; with `[StreamBuf]` `PoolMB=48`, `MinKB=128` 1565 / 1259 / 1096 (with or without Verify). With
  `PoolMB=16`, `MinKB=512` and a 16 MB `[BigBlock]` cache: 1634 at the first window, but only 78% of the requests found
  a block (8,342 of 10,647; 1,924 new blocks, 379 to the game).
- A map taken at the same moment in both configurations: +76 MB in 385 more private regions of 1 MB or less, and one
  more private region of 32 MB. This is the cost §11 removes.
- With the stream buffers gone from `[BigBlock]`, what it still served is the list §12 and §13 start from.

## 15. Growing streams: `Grow` [verified statically and by the selftest; not run in the game]

### 15.1 What grows, and how often [measured]

Two runs with the renderer in its own process (2 travel round trips, about 14 min each, `Pool=1`): 1,457,969 and
1,452,249 stream buffer requests, of which **1,171,835 and 1,171,565 were grows** (80%), copying 2,457 and 2,311 MB.
The 23 minute run of §19 had 1,608,415 grows and 4,858 MB. A grow is always allocate + copy + free (§3). By requester:

| `at` (Write's return address) | What it is [verified] | Requests | 128 KB+ | Grows |
|---|---|---|---|---|
| `0x4B590C` | the text encoder `0x4B58A0` (22 callers, `0x4B8A68`-`0x4B9333`): it converts a string piece by piece into a 256-byte stack buffer (`0x4B5470`) and writes each piece to the stream | 506,140 | 0 | 477,375 |
| `0x4DDC15` | `WriteUint32` `0x4DDBD0` (754 callers; the `via` `0x565745` is one of them, a binary writer object's method `0x565730`): four bytes per call, byte-swapped when the stream is big-endian | 378,353 | 1,997 | 311,346 |
| `0x4DDCAC` | the array form `0x4DDC20` (21 callers): `count * 4` bytes per call | 45,966 | 668 | 45,966 |

So these are the game's general serialisers, not one consumer: every caller that builds a document, a property stream
or a save record in a memory stream goes through them. Streams per row = requests minus grows: 28,765 text streams
with 16.6 grows each on average, 67,007 `WriteUint32` streams with 4.6.

### 15.2 The game's growth is geometric already, from a start of a few bytes [verified]

`Write` `0x4DEFF0`, when the bytes do not fit and resizing is on (`0x4DF01A`-`0x4DF05C`): new capacity = max(capacity x
`[stream+20h]` + `[stream+24h]`, position + bytes), computed in x87 and truncated. Both constructors (`0x4DF0C0`,
`0x4DF120`) set the factor from the constant at `0x1020A7C` = **1.5** and the increment to 0. So growth is 1.5x, but
from the size of the first write: a stream fed four bytes at a time runs 4, 8, 12, 18, 27, 40, 60, 90, 135, 202, 303,
... : ten reallocations before 256 bytes, seventeen before 4 KB, thirty before 1 MB. The counts above follow from that:
1.5^16.6 is about 840, so a text stream ends near 840 times its first piece (a few KB); 1.5^4.6 is about 6.5, so most
`WriteUint32` streams end at a few dozen bytes. The size table of the same run agrees: 1,320,264 of 1,457,969
requests were under 4 KB.

What is wrong is therefore not the factor but the start. Other growable buffers avoid exactly this with a least
capacity for the first real growth: EASTL's `vector` (in this binary: the growth at `0xB709E8`) doubles, `max(1, 2 *
size)`; `std::string` implementations start from a 15 or 22 byte inline buffer; .NET's `MemoryStream` grows to at least
256 bytes and then doubles; Go's `append` doubles small slices and slows to 1.25x for large ones (the last three from
memory, not re-read). The rule below is
that family: a floor, a steeper factor while the buffer is small, a gentler one when it is large.

### 15.3 What "capacity" is to the rest of the game [verified for the class; inferred beyond it]

- Inside the stream class (`0x4DEA80`-`0x4DF1D3`, every instruction with a `+14h` operand listed) capacity is **read
  in two places only**: `Write` `0x4DF006` (does the write fit) and `Realloc` `0x4DEEB9` (how many bytes to copy). It
  is written by the constructors, the copy constructor (`0x4DEC58`), SetData (`0x4DEDB6`, `0x4DEDC4`), `0x4DEE12` /
  `0x4DEE1E` and `Realloc`.
- No virtual method returns it. The vtable `0xFB5BCC` has GetSize (+1Ch, `[this+10h]`), GetAvailable (+2Ch, size minus
  position), SetSize, Get/SetPosition, Read, Write; there is no capacity getter.
- The buffer object (SharedPointer, §2) has no size or capacity field at all, so whoever is handed the buffer must
  take the length from the stream's size. A record written from a stream is written at its size.
- Capacity already differs from size in every stream that Write has grown: the game's own rule leaves up to half of
  the capacity unused. A consumer that assumed capacity = size would already be wrong for all of these streams. `Grow`
  changes how much is unused, not whether [inferred: code outside the class that reads `[stream+14h]` directly cannot
  be excluded by a search, but it would have to cope with the game's own 1.5x].
- SetSize and SetPosition are not touched: their capacity stays exact. Only the capacity argument of the `Realloc`
  call inside `Write` goes through the rule.
- `Realloc` copies the old capacity, not the old size, so a larger capacity makes the next copy longer by the unused
  bytes. The rule's factors make copies rarer by more than that.

### 15.4 The rule (`StbGrowCap`)

The capacity the game asks for is raised, never lowered:

- a stream that already has a buffer: 4 times the old capacity while that is under 4 KB, twice from there on, and at
  least `GrowFloor` bytes (64);
- at least `GrowTextFloor` (4096) instead when the writer is the text encoder (Write's return address `0x4B590C`, read
  from Write's byte-checked frame): its streams are documents, 16.6 grows each today;
- a stream's first buffer: at least `GrowFirst` bytes; 0 by default, so **a stream that never grows is not touched**
  (most small streams);
- nothing from `MinKB` (128 KB) on: there the game's 1.5x stays, because headroom would be arena or address space,
  and §15.5 removes the copy instead.

Expected, from the sequences: a `WriteUint32` stream that ends under 64 bytes grows once instead of 4-6 times; a text
stream of a few KB grows once or twice instead of 17 times; a stream that reaches 64 KB takes 4 K, 8 K, ... : 5 grows
instead of 24. On the three rows of §15.1 that should be about a tenth of the text row's grows and about a quarter of
the `WriteUint32` row's (it cannot fall below one grow per growing stream unless `GrowFirst` is set; with
`GrowFirst=64` the small ones do not grow at all). These are predictions; the run's `who` rows are the measure.

Cost: unused bytes in small buffers while they live. A stream that grew at all holds at least 64 bytes (4096 for the
text encoder's) and up to 4 times its data under 4 KB, twice above. With `Stats=1` the module follows the headroom it
handed out per buffer (up to 49,152 buffers at once) and prints what is held now and the peak (`headroomHeldKB`,
`peakHeldKB`). That number decides whether the floors are right: if the text encoder's streams turned out to be kept
by the thousand, `GrowTextFloor` has to come down. For the 1.3 million requests under 4 KB the EA heap rounds every
block to 8 bytes with an 8 byte header anyway, so a 64 byte floor is about the size of the smallest useful chunk.

### 15.5 Growing in place in the arena (`StbGrowInPlace`)

A stream of 128 KB or more has its buffer in the arena. When `Realloc` asks for the larger buffer and

- the old SharedPointer will certainly be freed by this `Realloc` (reference count 1, "free the data" set, the same
  allocator: then `0x4DEED3`-`0x4DEF11` releases it right after the copy, on this thread), and
- the slices behind the block are free,

the block takes those slices and its own address is returned for the new SharedPointer. `Realloc` then copies the
block onto itself: its `call memcpy` at `0x4DEECB` goes to `StbGrowCopy`, which does nothing when source and
destination are equal. `Realloc`'s release of the old SharedPointer reaches `StbFree` from the Realloc site (return
address `0x4DEEFF`) and finds the block marked: the mark is cleared and nothing is freed. If the old SharedPointer is
shared (count above 1) `Realloc` would not free it, so the ordinary path is taken. This is what `realloc` does when the
chunk behind a block is free (dlmalloc) and what mimalloc offers as `mi_expand`.

New bytes behind the old capacity are zero (or the Verify pattern), as in a fresh block: the rest of the old last
slice and reused slices are filled, newly committed ones are zero from the OS.

### 15.6 Sites (`Grow=1` or `Timing=1` at the start)

| Site | Original | New |
|---|---|---|
| `0x4DF05C` in `Write` | `E8 DF FD FF FF` (`call Realloc 0x4DEE40`) | `E8 rel32` to `StbWriteGrow`: passes the capacity argument through `StbGrowCap` and jumps to `Realloc`, which returns to `0x4DF061` as before, so the frame the `who` walk reads is unchanged |
| `0x4DEECB` in `Realloc` | `E8 D6 F9 A9 00` (`call memcpy 0xF7E8A6`) | `E8 rel32` to `StbGrowCopy` (cdecl, same arguments) |

Write's head (`0x4DEFF0`-`0x4DF060`) and `Realloc`'s window are byte-checked as in §6; no branch lands inside either
call. With `Grow` off at run time `StbGrowCap` returns the game's value and `StbGrowCopy` is `memcpy`.

**Apex Radiance:** no Apex site or hook lies in `0x4DE000`-`0x4DFFFF`, `0x4B5000`-`0x4B5FFF` or `0x4DD000`-`0x4DDFFF`
(apex-sites.csv, apex-hooks.csv; docs/148 and docs/254 name none). FastCacheCompression replaces the codec slot
`0xFB901C`, ResourceLookupCache and FileListCache hook the resource manager (`0x4A7FC0` and others): none touches the
stream class. `Grow` therefore does not stand down for Apex and keeps working beside it, as `Pool` does.

Counters (`streambuf: grow`): `headroomGrants`, `stepsSkippedUpTo` (the 1.5x reallocations the game would have needed
to reach the granted capacities: an upper bound of the grows avoided, since a stream may never use its headroom),
`headroomMB_total`, `headroomHeldKB` / `peakHeldKB` / `headUntracked`, `inPlace`, `notCopiedMB`, `inPlaceBlocked`,
`growCopyMB`.

### 15.7 What it can be worth

Not much per second, and only a run with `Timing=1` can say how much. An estimate from the counts: 1.17 million grows
in 14 minutes are 1,400 per second; each is two small heap allocations and two frees (the buffer and the 20-byte
SharedPointer object) plus a copy of 2 KB on average. At 0.1-0.3 us per heap call and a few GB/s for the copies that is
about 1-2 ms per second of play, concentrated where the game serialises (saving, exporting). `Timing=1` prints the
copy time (`growCopyMs`) and the game allocator's time on the requests this module passes on (the small stream
buffers among them), so the first run with it replaces this estimate.

## 16. Timing: what the module costs and what the game's path would have cost (`Timing=1`) [selftest only]

All in QueryPerformanceCounter ticks, added up under the module's lock, printed as `streambuf: time` at every mark:

- `arenaMs` by family (SetSize / Write / SetPosition / Scratch / Package / Assemble) and `frees`: this module's own
  code on requests the arena served and on their frees. The zero-fill is in it. This is what the module spends.
- `passOverheadMs`: this module's own code on requests and frees it only passed on to the game's allocator (every
  stream buffer under `MinKB`): pure overhead added. The game allocator's own time on those is printed beside it.
- `growCopyMs` / `growCopyMB`: `Realloc`'s grow copies (needs the grow sites, which `Timing=1` writes).
- `sample`: for every 64th request the arena served, the game's own path is run for the same size with the same
  allocator: allocate, write one byte to every page, free. `gamePathEstMs` = the mean sample time x the number of
  served requests, with two standard errors from the samples' spread; `perSecond` divides by the seconds since Timing
  was switched on.

How good the estimate is:

- It is a systematic 1-in-64 sample across all served requests, so sizes and requesters are represented in
  proportion; with 30,000 served requests per run that is about 500 samples.
- It measures the allocation, the first touch of every page and the release: the three things the arena removes. It
  does not measure the consumer's own writes (the same in both paths) or cache effects of a fresh block against a
  reused one.
- It is the game's path **as configured**: with `[BigBlock] Reuse` on, the sample goes through its cache and measures
  that. For the plain game number, switch `[BigBlock]` off in the measuring run.
- Each sample is one real allocation of the game's, so one in 64 served blocks still makes a reserve / release cycle
  while Timing is on.
- The clock reads themselves cost about four per request and one more lock; they are inside the own-time numbers of
  small requests, which makes `passOverheadMs` an upper bound.

The comparison to make is `gamePathEstMs` against `arenaMs` (all families) + `frees` + `passOverheadMs`. It is a
measuring mode, not for timing A/B runs.

## 17. The block assembler's scratch buffer: `Assemble` [verified statically and by the selftest; not run in the game]

`[BigBlock]`'s caller `0x6169AD` (1,308 blocks, 3.2 GB in the 23 minute run, lifetime 0 ms, peak 5.3 MB) and the stream
requester `0x616A83` with the same `via` `0x6187EB` are one function: `0x6165A0` (cdecl: stream, count, descriptors;
`0x6165A0`-`0x616AB2`; ten callers). It adds up the sizes of `count` pieces (`0x616920`-`0x616995`), takes one buffer
for all of them directly from the MemoryMan heap, `[0x11CB864]->Memalign(size, 16, 0, 0)` (`0x6169A8`, call to
`0x4E6A20`), clears it (`0x6169CB`), copies a 0x80 byte header and the pieces into it, writes the whole buffer to the
stream with one `Write` (`0x616A81`: this is the stream row, already served by `Pool`), and frees it with
`heap->Free` (`0x616A93`, call to `0x4E5920`).

- **Owner and lifetime:** the function; the pointer is in `ebx` and `[esp+ACh]`; allocated and freed in the same
  call on the calling thread. The only exit between the two is the NULL check behind the allocation (`0x6169B8`).
  The buffer is passed to `memset`, `memcpy` and the stream's `Write`, which copies from it.
- **Read before written:** no; the function clears the whole buffer itself.
- **Sites** (`Assemble=1` at the start; the whole function must match): the two calls keep their place and length
  and get a new target: `0x6169A8` `E8 73 00 ED FF` -> `StbAsmAlloc` (thiscall on the heap, four arguments, as
  Memalign; the return address `0x6169AD` stays), `0x616A93` `E8 88 EE EC FF` -> `StbAsmFree`. A request the arena
  does not serve, and any call that is not `(size, 16, 0, 0)`, goes to the heap's Memalign with the original
  arguments; a pointer that is not the arena's goes to the heap's Free. `0x4E6A20` and `0x4E5920` themselves are not
  patched, and an arena pointer never reaches them, so other hooks on the allocator do not see these blocks.
- **Apex Radiance:** no site in the function; no stand-down.

**`0x72D6C9` via `0x734651`** (267 blocks, 251 MB, peak 18.8 MB, 9 ms) was looked at and not built: the function
`0x72D470` allocates the buffer (`0x72D6B4`-`0x72D6C7`), fills it (`0x72D300`) and hands it to a 0x58 byte object built
by `0x739F80` with an "owns the buffer" argument of 1; on that path the function itself frees it only when the object
cannot be made (`0x72D698`, `0x72D6AD`). The free that ends its 9 ms is in that object's class, which was not reverse
engineered; without every release path of that class the allocation cannot be hooked. 267 blocks per run do not
justify it. The two long-lived callers (`0x57A593`, `0x8B452D`) stay with `[BigBlock]`.

## 18. Arena size [measured: the runs of §19]

| Run | Parts | Peak in use | Peak committed | Fallback | Stranded |
|---|---|---|---|---|---|
| in-process renderer, 12 min | Pool | 28.8 MB | 47.9 | 0 of 32,375 | 0 |
| renderer in its own process, 14 min | Pool | 34.3 | - | - | 0 |
| the same with Apex Radiance | Pool | 30.2 | - | - | 0 |
| in-process renderer, 23 min | Pool, Scratch, Package | 45.4 | 48.0 | 39 of 140,650 | 39 |

- **The default stays 48 MB.** In three of four runs the peak was 29-34 MB. The one run that reached the limit lost
  39 requests of 140,650 (0.03%) to the game's own path, where each cost what every request cost before the module:
  one reserve and release. Sixteen more MB of address space held for the whole session to save 39 system calls in 23
  minutes is a bad trade.
- **What filled it:** one requester, `0x72A2B4` (the decoded buffer of a streamed record), peaked at 47.7 MB by itself
  with 36 ms average life and every one of its 2,701 frees on another thread. Its buffers wait for the thread that
  finishes the load; when that thread is busy they pile up. That is a burst of the game's own making, and it had the
  same size without the module (it then sat in 40 separate reservations).
- **A per-requester cap was considered and not built.** It would turn more of that requester's requests into
  fallbacks earlier in order to keep room for the others; the others lost 13 requests in that run. A cap would trade
  a few dozen fallbacks of one row for a few dozen of another, with a new rule to get wrong. Letting a burst overflow
  to the game's path is the cap: it costs nothing in address space and is exactly what happened.
- All 39 fallbacks were also counted as stranded: enough slices were free in total (2.6 MB at the peak) but not in one
  run. With 48 MB nearly full that is the expected end state of any layout, not a sign that best fit fails; at the
  29-34 MB peaks of the other runs it was 0.
- `Grow`'s in-place growth needs free slices behind a block, so it competes for the same space only when the arena is
  that full, and then simply does not happen (`inPlaceBlocked`).

## 19. Summary of the game runs (2026-11-11) and what may be claimed

All on one large save, two world-travel round trips per run, the build of commit `db8deae` merged with the project's
other modules.

| Run | Configuration | Stream buffers of 128 KB+ | Served without a system call | Fallback | Errors |
|---|---|---|---|---|---|
| A, 12 min, in-process renderer | `Pool` | 32,375 (30.3 GB) | 31,914 (98.6%); 457 with one commit | 0 | all 0 |
| B, 23 min, in-process renderer | `Pool`, `Scratch`, `Package` | 140,650 requests (111.9 GB) | 139,863 (99.4%); 737 with one commit | 39 | all 0 |
| C, 14 min, renderer in its own process | `Pool` | - | 98.6% | - | all 0 |
| D, 14 min, the same with Apex Radiance 3.1 loaded | `Pool` | - | 98.3% | - | all 0 |
| E, 15 min, in-process renderer, one travel round trip: the first run of `Grow`, `Timing`, `Assemble` | `Pool`, `Scratch`, `Package`, `Assemble`, `Grow`, `Timing`, `Stats` | 97,262 of 703,127 requests | 94,676 (97.3%) | 0 | all 0 |

- **Address space.** The arena was reserved at `0xFCEA0000`-`0xFFEA0000` in every run, at the top of the largest free
  range (2046 MB before, 1998 after), `oneReservation=1` and the same base at every mark. Largest free range at the
  three home windows of run A: 1624.5 / 1393.2 / 1186.1 MB, against 1656 / 1391 / 1186 with nothing installed and
  1565 / 1259 / 1096 with v1's separate blocks. So the arena costs its 48 MB at the start and nothing measurable
  once the game has grown past that; v1 cost about 100 MB throughout.
- **System calls.** Run A: 457 commits and 118 decommits in place of 32,375 reserve / release pairs. Run B: the
  compressor's tables (63,176 of 128 KB+, 29.5 GB) and the package writer's buffers (5,729, 8.2 GB) joined; `[BigBlock]`
  beside it fell from 26,454 to 5,178 allocations; 6.2 MB committed at the end.
- **Correctness.** `dfree`, `vaFail`, `uafw`, `lost`, `untracked` 0 in all four runs; 8,134 frees from another thread
  than the allocation in run B; saves written with the pooled compressor and package buffers loaded again on each
  return. The `who` rows name their requesters.
- **Run E (the build of commit `cbd8f5c`).** All sites installed; `oneReservation=1`, peak in use 32.5 MB, stranded 0.
  The second trip did not start (household Sims failed to route to the car, before any save; unrelated).
  - `Grow`: 211,071 grows and 378.4 MB copied in 900 s, against about 1.17 million and 2.3-2.5 GB per 14 minutes
    before: about -83% and -86%. 195,781 capacity raises; headroom held 891 KB at the end, 972 KB at most, none
    untracked: the floors cost nothing measurable. 2,194 grows in place (2,073.6 MB not copied), 39 blocked. The
    text encoder's row left the top Write rows; `WriteUint32` (`0x4DDC15`): 108,601 requests, 69,071 grows.
  - `Timing` at 600 s: own time on served requests SetSize 934.8 ms, Write 48.2, Scratch 154.9, Package 52.5, Assemble
    25.4, frees 15.9 (68,177 requests); pass-through overhead 178.7 ms on 445,557 requests, on which the game's
    allocator spent 516.8 ms; grow copies 18.9 ms; 1,038 samples of the game's path, mean 57.8 us, estimate
    3,845 +- 369 ms. Per second: own 2.35 ms, the game's path 6.41 ms. At 900 s: estimate 7,610 +- 583 ms, own 2.36
    ms per second, the game's path 8.45. So the module spends about a third of what it saves, most of it filling
    zeros (§21), and the saving is 4-6 ms per second of play, with statistics and timing running.
- **`[BigBlock]` beside this module:** with its newer demand rule (`ReuseDemand=2`) its cache hit 58% of the
  requests left to it, against 85.9% for the older rule in the same run's shadow cache. The setting to use beside
  `[StreamBuf]` is `ReuseMaxMB=16` with the older demand rule.
- **Arena size:** peaks in use over the five runs 28.8 / 45.4 / 34.3 / 30.2 / 32.5 MB; the default stays 48 (§18).
- **With the renderer in its own process** the two shared views sit low (96 MB near `0x04200000`, 72 MB near
  `0x0A200000`); nothing at the top is disturbed and both mapped.
- **With Apex Radiance** the stream sites share nothing with it and `Pool` ran unchanged, while `[BigBlock]` stood
  down. `Scratch` stands down for Apex; `Package`, `Assemble`, `Grow` and `Timing` share no site with it either.
- **Time.** Zero-fill 1.18 s per 12 min (run A) and 2.8 s per 23 min for 110.8 GB (run B). The save step of travel was
  not measurably shorter with `Scratch` and `Package` (1, 45, 18, 20 s against 1-26 s without, with statistics and
  other samplers running). A first ABBA set with all measuring off (two jobs per arm: load + one travel round trip)
  gave 169 and 142 s per round trip with both modules off against 139 and 139 s with `[StreamBuf]` `Pool` + `Scratch` +
  `Package` and `[BigBlock]` reuse on; outbound travel load 81 / 72 s against 66 / 67 s; save load unchanged (28-31 s).
  Two jobs per arm, with the two off jobs 27 s apart: weak evidence, to be repeated, and not separable between the
  two modules. A second set of six jobs (off, on, on + Grow, on + Grow, on, off; fresh start, load, one round trip
  each) gave 147 / 149 / 147 / 150 / 149 / 152 s, save load 28-30 s in every job. Over the ten jobs there is no load,
  save or travel time difference a stopwatch can see; the `Timing` line of run E is the statement of the gain.

**A pull request for this module may claim:** that the stream buffers, the compressor's tables and the package
writer's buffers no longer cut up the address space (the numbers above); that the pool costs one 48 MB reservation
and no more; that it ran clean in four game runs including the two-process renderer and beside Apex Radiance; that
every part is off by default and byte-checked.

**It must not claim yet:** any gain in frame time, hitching, travel or save time (one weak ABBA set points to shorter
travel loads and has to be repeated; the host bench and §15.7 say the CPU part is small: run E estimates 4-6 ms per second of play saved, measured with a sampling estimate, not a
stopwatch); that `Zero=0` is safe (it is not: unwritten tails were found); more for `Assemble`, `Grow` and `Timing` than
one 15 minute run shows; anything about `PreZero` and `SkipClear` beyond the selftest; that the arena helps the largest free range late in a session (it is neutral there:
the gain is against v1 and in system calls, and the baseline's late numbers are already equal); long sessions (the
longest run is 23 minutes).

## 20. Selftest for §15-§17 [verified]

- `StbGrowCap`: first buffer untouched (and raised with `GrowFirst`), the floor, 4x under 4 KB, 2x above, the text
  encoder's floor, a write that needs more than the rule gives, nothing from `MinKB` on, switched off.
- **The patched copy of `Write` is executed** up to its `Realloc` call against a stand-in `Realloc`: a stream with
  capacity 64 asking for 4 more bytes makes the game compute 96; the stand-in receives 256, sees Write's own return
  address above it and Write's caller where the rule reads the writer; with that caller set as the text encoder it
  receives 4096; with `Grow` off 96. The headroom is followed to the buffer and released with it.
- In-place growth, with and without Verify 2: a 704 KB block grows to 1408 KB at the same address, old bytes kept, new
  bytes zero (or the pattern); `Realloc`'s release, run through the patched free site, keeps the block and clears the
  mark; a shared old buffer and occupied slices behind the block take the ordinary path; a block that was not grown
  is freed by the same site as before.
- The headroom table: 49,200 entries offered, 49,152 kept, all removed in two passes, the table empty afterwards.
- `Timing`: 130 served requests give 2 samples through the stand-in allocator; own time by family, pass-through
  count, copy time; the `time` line is formatted.
- `Assemble`: a foreign byte blocks the install; the two calls change only their target; the stubs against a stand-in
  heap (served from the arena and zero, small to Memalign with `(size, 16, 0, 0)`, another alignment passed through,
  switched off).

## 21. The module's own cost: `PreZero` and `SkipClear` [verified by the selftest; not run in the game]

### 21.1 Where the time goes [measured, run E of §19]

`Timing=1`, 600 s: the module's own code on served requests took SetSize 934.8 ms, Scratch 154.9, Package 52.5,
Write 48.2, Assemble 25.4, frees 15.9: about 1,230 ms, 2.05 ms per second, against an estimated 3,845 +- 369 ms for the
game's own path on the same 68,177 requests. Nearly all of the 1,230 ms is the zero-fill of reused slices: the earlier
runs logged 1.18 s of fill per 12 min and 2.8 s per 23 min (`zeroMs`), the bookkeeping per request is one lock and a
walk over at most 768 state bytes (0.5 us on the host, §8), and the SetSize family is the one that fills 0.7-5.5 MB
per request [inferred from those numbers; the `time` line now prints the fill's share per family,
`ofWhichZeroFillMs`, so the next run states it].

### 21.2 `PreZero=1`: the fill moves to the module's own thread

The idea is the operating system's zero-page thread, and what mimalloc (`is_zero` on its arena memory) and Scudo
(zeroed secondary pages) keep track of: memory that is free and known to hold zeros needs no fill when it is handed
out. Here:

- **State.** A sixth slice bit, `ZERO`: free, committed, known zero. A freed slice is committed and dirty (no bit); a
  decommitted slice is zero by the OS's contract, as before.
- **Who clears.** The upkeep thread. A free that leaves dirty slices signals it (one `SetEvent`, and only when no
  signal is pending). It takes the lowest run of dirty free slices, at most 16 (1 MB, about 0.1 ms), marks it `PURGE`
  under the lock, fills it outside the lock, marks it `ZERO`, yields (`SwitchToThread`) and looks again until nothing
  is dirty.
- **Race rules.** A slice marked `PURGE` is neither free nor used to everyone else: requests, in-place growth and the
  idle decommit skip it (the same mark and the same rule the decommit already used). A slice is `ZERO` only after its
  fill has finished. A block that is handed out loses the bit; its slices come back dirty.
- **The request.** Slices with `ZERO` are not filled; dirty ones are filled on the caller's thread as before
  (`callerZeroMB`); uncommitted ones are committed. A request that needed neither fill nor commit counts as
  `servedClean`.
- **Preferring clean runs without hurting the layout.** Best fit still chooses by length alone. Only among runs of
  that same smallest length does the one win whose first slices need the least work (zeroed 0, dirty 1, uncommitted
  4 per slice: a commit and its page faults cost more than a fill, §8). Equal-length runs are the common case here,
  because the traffic is a handful of sizes. In-place growth is unaffected: it takes the slices behind its block in
  whatever state they are.
- **Pacing.** No affinity is set. While `PreZero` is on the thread runs at below-normal priority, so it gets a core
  only when one is idle; it holds at most 1 MB out of circulation at a time, for the length of one fill. If it is
  starved, slices simply stay dirty and callers fill them as today. It fills at most what is freed, about 40 MB per
  second at the measured traffic: a few milliseconds per second of its own time.
- **Verify.** With `Verify=1` a freed block is poisoned as before; the upkeep thread checks the poison before it
  overwrites it (`uafw` if someone wrote in between), and when a `ZERO` slice is handed out it is checked to be all
  zero (a write after the fill: `uafw`, and the slice is filled again). So a slice handed out as clean is proven
  clean in a Verify run. `Verify=2` hands out the pattern, so nothing is pre-zeroed.
- **Idle.** Zeroed slices are decommitted after `IdleSec` like dirty ones.

Expected: the SetSize family's own time falls by most of its fill share, as far as the thread keeps up; a block
that is freed and asked for again within the same fraction of a millisecond is still filled by its caller.
`bgZeroMB` against `callerZeroMB` on the arena line is the measure, with `ofWhichZeroFillMs` on the time line.

What it does not do: it does not make the fill cheaper, it moves it to an idle core. On a machine with no idle core
it gains nothing.

### 21.3 `SkipClear=1`: consumers that initialise their buffer

A request from a site whose consumer provably writes every byte it later reads is not zero-filled:

- **The compressor's hash heads** (256 KB): both cores start with `memset(heads, 0xFF, whole table)` (`0x4EB763` /
  `0x4EB777` for 0x400 bytes, `0x4EBBA3` / `0x4EBBB7` for `0x40000`) [verified].
- **The compressor's links** (512 KB), now proven instruction by instruction in both cores (`0x4EB750`, `0x4EBB90`:
  the same code, 0x440 bytes apart) [verified]:
  - the links table (`[esp+54h]`) is read in **one** place, `mov ecx,[eax+ecx*4]` at `0x4EBD4B` (`0x4EB90B`), with the
    index `c & mask`, where `c` is the candidate position in `[esp+1Ch]`;
  - `c` is either the head entry for the current hash (`0x4EBC64`) or a value read from the links table; both are
    accepted only by a signed compare against the lower bound: `cmp ecx,eax / jl` out at `0x4EBC89`-`0x4EBC8F` for the
    first, `cmp ecx,[esp+34h] / jge` back into the loop at `0x4EBD4E`-`0x4EBD56` for the following ones;
  - the lower bound (`[esp+34h]`, built at `0x4EBC6D`-`0x4EBC8B`) is max(0, current position minus the window mask);
    the heads start as -1, so an empty head is below the bound and no link is read for it;
  - every value stored in a head or a link is a position that was inserted, and the three places that insert a
    position (`0x4EBEB9` / `0x4EBEC5`, `0x4EBF14` / `0x4EBF1C`, `0x4EBF50` / `0x4EBF63`) each write `links[pos & mask] =
    heads[hash]` immediately before `heads[hash] = pos`. There is no other write to either table;
  - so a link is read only for an inserted position `c` with current - mask <= c < current, and its slot `c & mask` was
    written when `c` was inserted. A later position that would reuse the slot is `c + mask + 1` or more, which is
    beyond the current position. Written before read.
- **The block assembler's buffer**: `0x6169C7`-`0x6169CB` is `memset(buffer, 0, size)` over the whole allocation
  before anything else touches it [verified].

Everything else (stream buffers, the package writer's two buffers) is filled as before; `Zero=1` remains the rule.
`skipClearN` / `skipClearMB` on the arena line count the requests and the dirty bytes not filled. In a game run the
proof shows as saves and caches that load again; with `Verify=2` the tables are handed out as the 0xA7 pattern (the
skip is off then), which exercises the same independence of the content: the saves of such a run must load too.
`unwrittenN` cannot show it (the compressor does not write every slot; it only never reads the ones it did not
write).

### 21.4 Requests the module only passes on

Run E: 445,557 requests under `MinKB` in 600 s cost 178.7 ms of this module's own code (0.40 us per request and its
free) with `Stats` and `Timing` on. Of that, by reading the code [inferred, not measured separately]: `Timing` is
eight clock reads and two more lock acquisitions per pair, roughly 0.2-0.25 us; `Stats` is the caller walk, one lock
and the counters at the request and a table look-up under the lock at the free, most of the rest. With both off the
request still took the module's lock, a clock read and the thread id only to count itself. That is now gone: a
request under `MinKB` with `Stats` and `Timing` off goes straight to the game's allocator (no lock, no clock), and
its free was already a range compare. The price is that `requests=` counts small requests only with `Stats=1`.

## 22. A front for the small stream buffers: measured, designed, not built

**The numbers** [measured, run E]: 445,557 requests under `MinKB` in 600 s, on which the game's allocator spent
516.8 ms for the allocation and the free together: 1.16 us per pair, 0.86 ms per second of play. That is the ceiling.
By size (an earlier 14 minute run, before `Grow`): 1,320,264 under 4 KB, 44,119 under 8 KB, 18,943 under 16 KB, 14,072
under 32 KB, 16,530 under 64 KB, 9,759 under 128 KB: 93% under 4 KB. `Grow` has since removed more than half of these
requests. How many small buffers are freed on another thread is not known; with `Stats=1` the module now follows
every 64th of them to its free and prints the share and the average lifetime (`streambuf: small`).

**The design that would fit** (if it were built):

- size classes of 16 bytes to 4 KB in four steps per doubling (about 33 classes); larger requests stay with the game;
- memory from one more small reservation (8 MB, placed by the same rule, below the arena), cut into 64 KB slabs, a
  slab belonging to one class; ownership again a range compare, the class from a byte per slab;
- one free list per class shared by all threads, lock-free (Windows' interlocked singly linked lists, the mechanism
  of the kernel's lookaside lists), so that a free on another thread costs the same as one on the allocating thread.
  Per-thread caches (mimalloc, tcmalloc) are faster on the same-thread path but need a thread-exit hook to give
  memory back and a second path for cross-thread frees; this module has neither a reliable thread-exit moment nor
  the traffic to pay for them;
- an empty class takes a new slab; no slab free or a full reservation falls back to the game's allocator; slabs
  whose blocks are all free are decommitted on idle;
- the same two stream sites; `Zero` does not apply (the game's heap does not zero small blocks either).

**The honest gain.** A shared lock-free list costs about 0.1-0.2 us per pair on the host class of machine, so the
front would save about 1 us on each pair it serves: at most 0.7 ms per second of play with every small request
served, less after `Grow`, spread evenly over time (these are not hitches). Against that: a third allocator with its
own reservation, memory held in up to 33 partly used slab lists, and new code on the path every serialiser call
goes through, where a mistake corrupts saves.

**Recommendation: do not build it.** The gain is under a tenth of a percent of one core and not on a path anyone
feels. It would be worth a second look only if a run showed the game's heap lock contended at these requests (time
waiting, which the 516.8 ms of CPU time does not show), and that measurement belongs to the allocator work, not here.

## 23. The arena as shared memory: `Shared=1` [verified by the selftest; not run in the game]

**Why.** The project's 64-bit host (`ts3wrap.exe`, the wrapper service of another branch) can do work for the game, but its first service
measured 2.5-7 times slower per call than the same work in the game, because every call copied the input to the host
and the result back. If the game's big transient buffers already lie in memory both processes map,
the host can read and fill them where they are. The arena holds exactly those buffers.

**What changes.** With `Shared=1` at the start the arena's reservation is a view of a pagefile-backed section instead
of private memory. Slices, best fit, in-place growth, Verify, PreZero and the statistics run on top of it unchanged.
With `Shared=0` nothing differs from before (the private path is the same code as it was).

| Question | Answer |
|---|---|
| Section | `CreateFileMapping(INVALID_HANDLE_VALUE, sa, PAGE_READWRITE \| SEC_RESERVE, PoolMB)`: reserved, nothing committed |
| Placement | the same rule (§11.3): the chosen address is passed to `MapViewOfFileEx`. A view has no top-down mode of its own, so if the place cannot be had (twice), the section is closed and the arena is private memory, with a `WARN` line |
| Commit | `VirtualAlloc(MEM_COMMIT)` on the view, as for private memory: zero pages. Pages committed through one view are there for every view of the section [verified: the selftest reads and writes them through a second view] |
| Giving memory back | pages of a view **cannot be decommitted**. An idle run is handed back with `VirtualAlloc(MEM_RESET)`: the OS may drop the pages without writing them to the page file. It works on such a view [verified on the build machine; a failure is counted, `resetFail`]. The commit charge (up to `PoolMB`) stays until the process ends: that is the cost of sharing |
| "Known zero" | a reset page's contents are undefined (the documentation says so; decommit-and-recommit is the only way to get zeros, and it is not available here). So a reset slice is **dirty**: a slice state of its own (`RESET`) that the idle pass does not reset again and PreZero does not fill (touching it would bring the pages back); the next request fills it on the caller's thread. PreZero itself works as on private memory: its `memset` makes the zeros |
| Name | `Local\ts3sb-<process id>-arena`. The process id keeps two game instances apart, and the host already knows the game's process id (it is on its command line). The wrapper service's channel names its objects the same way |
| Who can open it | the security descriptor the wrapper service's channel uses: `D:P(A;;GA;;;SY)(A;;GA;;;<user SID>)S:(ML;;NW;;;ME)`: the same user and SYSTEM, medium integrity, no write-up. A name that already exists is refused (the arena is then private), as the channel's host refuses a name it did not create |
| Protection | read / write; the section is not executable and an execute view of it cannot be mapped [verified] |

**What another process of the same user could do:** read and write the game's stream buffers, as it could with
`WriteProcessMemory`; the section adds no right such a process did not have. What the game must not do is trust
anything that comes back through it: lengths and offsets a helper returns are checked against the block the game
itself owns before they are used (the rule for the host call, on the wrapper branch).

**If the helper dies:** its view and handle go with it; the game's view and the section stay; the arena works on
exactly as before [verified: the second view is closed in the selftest and the arena is used afterwards].

**Apex Radiance and the two-process renderer.** The renderer's client already maps a 96 MB and a 72 MB view low in the
game's address space; the arena as a view at the top is the same kind of object. docs/148 and docs/254 name
no code in Apex or in the renderer's client that enumerates or unmaps views it did not create [searched; inferred
from their absence, not from reading their sources].

Log: after the `arena reserved` line, `streambuf: arena is SHARED memory: a view of the section ...`; the OK line says
`Shared on` (or `asked, not in use` after a fallback); the arena line ends with `shared=1 resets= resetMB= resetFail=`.
In a game run as a plain arena it must show the numbers of the private arena, except that `commitMB` no longer falls
when streaming stops (`resetMB` grows instead).

Selftest: the section is created, placed by the rule and is a mapped region with nothing committed; a second view
opened by name sees a block's bytes at the same offset and its writes are seen by the game side; an execute view is
refused; free, PreZero, idle reset (state, counters, still committed), no second reset, no background fill of a reset
slice, the next request zero-filled; the second view closed; a squatted name falls back to a private arena that
decommits as before.

The host side (mapping the section in the 64-bit process and the first call that works in place) lives on the wrapper
service's branch, not here.
