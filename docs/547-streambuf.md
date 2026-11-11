# 547. Memory stream buffers: the allocation site `0x4DEC17`, and `[StreamBuf]` (2026-11-11)

Static RE on TS3W.exe 1.67 (Capstone disassembly of the retail copy, sha256 `b78a3d53...`, 14,510,400 bytes; every byte
window quoted here was read from that copy), `native/ts3native/streambuf.cpp` with its selftest and host bench. **Nothing
here has run in the game.** The measurements in §1 come from `[BigBlock]` Stats (docs/543, one 14 min run).

Tags: [verified] = read in the disassembly, or exercised by the selftest; [measured] = a run's numbers; [inferred] =
reasoning that the Stats run of §9 (a) has to confirm.

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
- **Fix:** `[StreamBuf] Pool=1` serves these buffers from a small pool of the module's own blocks and takes them back when
  the game frees the stream's data. Three byte-checked sites in the stream code; the general allocator is not touched.
  Default off.
- **Why it is safe:** the buffer's lifetime is the SharedPointer's, and the game frees stream data in exactly two places
  [verified, whole `.text` searched]; both are hooked, and a pointer that is not the pool's goes to the game's free
  unchanged. The pool hands a reused block out zero-filled (default), as the OS hands out a fresh one.
- **Host bench** (§8): 1365 KB buffer filled once: game path 288 us, pool 35 us (zero-filled) or 24 us (not filled).
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
  takes the smallest idle block that fits with at most a quarter to spare (the most recently freed of that size), else a
  new block (`VirtualAlloc`, size rounded up to 64 KB) while the pool owns less than `PoolMB` (idle blocks are released
  oldest first to make room), else the game's own call (`fallback`). Everything else is the game's call with the same
  arguments.
- **StbFree:** a pointer whose 64 KB-aligned base has its bit in the owner bitmap (read without a lock) is the pool's
  and becomes idle; every other pointer goes to the game's `Free` as before. A block handed out while the pool was on
  comes home even after the pool is switched off.
- **Upkeep** (own thread, 500 ms): idle blocks older than `IdleSec` go back to the OS, all of them when the pool is off.
  Every 5 s: `Enabled`, `Stats`, `Pool`, `Zero`, `Verify` are read again from the ini (run-time kill switch), and the
  three sites are compared with our bytes: a foreign change stops the module for the session.
- **Process exit:** the stubs pass through and take no lock.
- **Apex Radiance:** no Apex site or hook lies in `0x4DE000`-`0x4DFFFF` (docs/data/apex-sites.csv, apex-hooks.csv), so
  there is no stand-down. `[BigBlock]`'s two call operands (`0x4E512C`, `0x4E5308`) are untouched.

Log lines at every `diag: mem` mark: `streambuf: #n` (requests, big = 128 KB+, served = reused block, fresh = new pool
block, fallback, tooBig, frees, xthreadFrees, grows, growCopyMB, poolMB, idleMB, inUseMB, peakInUseMB, peakPoolMB,
liveN / peakLiveN, evicts, trims, zeroMs / zeroMB, errors: dfree, vaFail, uafw, unwrittenN / unwrittenKB, lost,
untracked), `streambuf: sizes`, one `streambuf: who` per sizer (at, via, how, n, MB, big, maxKB, grows, served, fresh,
fallback, frees, xthreadFrees, live, peakMB, avgLifeMs, under1s) and `streambuf: threads`.

**Verify** (debug): 1 = a freed block is filled with `0xB5` and checked when reused (`uafw`). 2 = also every pooled
block is handed out filled with `0xA7` instead of zeros, and at the free the aligned 8-byte groups that still hold it are
counted (`unwrittenN`, first 16 logged with the sizer). Limits: a read after free is invisible; so is a read before a
write that is overwritten later; a consumer that itself writes eight `0xA7` in a row is miscounted; with 2 an unwritten
byte reads `0xA7`, not 0, so a consumer that does depend on zeros misbehaves in that run (which is the test).

## 7. The other short-lived callers of docs/543 [verified tags; not built]

| Caller (BigBlock `at`) | What it is | Same fix? |
|---|---|---|
| `0x4A7216` (367 x 1.1 MB, 7 ms) | `ResourceLoad/PackedFile/PackedRecord`: the raw record read buffer in `0x4A7030` (allocator `[this+0x10]` vtable +8) | same pattern (transient buffer through `ICoreAllocator`), different site: needs its own alloc hook and its free site(s) found |
| `0x4A89F7` (260, 8 ms) | `ResourceLoad/PackedFile/RecordData`: the decoded record (`size + 0x10`) of the sync path | as above |
| `0x7E9010` via `0xB709E8` (58 x 7.9 MB, 42 ms) | the real caller is `0xB709E8`: an EASTL vector growing by doubling (`lea ecx,[eax+eax]`, `EASTL/allocator.h`); `0x7E9010` follows a plain call, not an allocation, so it is a stale stack word | no: container growth; a `reserve` at its owner would be the fix |
| `0x6169AD` (183, 0 ms) | `Memalign(16)` scratch in `0x6165A0` | no (aligned scratch, own site) |
| `0x4EC140` (384, 0 ms) | `Services/UTFFoundation/CompressionRefPack`: the compressor's hash tables | no (`[SaveCompress]` territory) |
| `0x4445B9` (45, 84 ms) | `UI/EAGimex/Buffer`: an image decode raster | no (rare) |

The pool functions take any size and pointer, so the two PackedFile sites could be added as further hooks later.

## 8. Selftest and host bench [verified]

`streambuf-test.h` (in `selftest.exe`): size policy; the sizer read from a stack laid out as the checked code leaves it;
the pool step by step (reuse, zero fill, no cross-size reuse, small / too big / pool full to the game, eviction, double
free, idle release, kill switch, process exit, Verify 1 and 2, Stats alone); a randomized run of 6000 steps (fixed seed)
against the policy written the plain way, with every block filled with a signature and checked at its free (716 reused,
840 new, 200 to the game, 613 idle releases, 219 evictions); four threads freeing each other's blocks; and the patches:
**patched copies of the game's constructor and Release bytes are executed** against a fake allocator (fields, stack
balance, refcount path, small request, "free the data" clear), Realloc's copy is patched and compared, a foreign byte
blocks the install, a changed site stops the module.

Bench, per buffer, 300 each (low priority, 4 cores, a game running beside it):

| | 1365 KB filled once | not touched | 682 KB filled once | not touched |
|---|---|---|---|---|
| game path (VirtualAlloc + release) | 287.9 us | 4.7 | 151.1 us | 3.6 |
| pool, `Zero=1` | 35.3 us | 13.2 | 19.0 us | 7.2 |
| pool, `Zero=0` | 23.9 us | 0.1 | 12.8 us | 0.1 |

The game path's cost is the first touch of fresh pages, not the two system calls. At 25 buffers per second the saving is
about 6 ms/s: small. The value is the address space: 24,000 reserve / release cycles become a handful of blocks.

## 9. Test plan (game runs; none done)

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
