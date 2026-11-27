# 364. EA resource and package caches out of process: census and verdict (2026-11-26)

Static (Capstone disassembly of the Steam TS3W.exe 1.67 copy in `game-copy/`) plus numbers that existing runs already
logged; no game run, no build. Brief (Rodney, 2026-11-26): move EA's resource and package caches out of the 32-bit
process where possible (decoded models, package indexes, resource caches, anything sizeable EA keeps resident that is
not touched every frame), served by the 64-bit host over the Apex helper channel / Atlas OOP plugin host (Atlas
docs/025) with a small in-process LRU, exact by construction, behind `[ResCache] OutOfProcess=0`. Kill criterion: a
cache read every frame, or under a lock the main thread waits on.

Tags: [verified] = read in the disassembly or an earlier doc's verified claim; [measured] = a logged run;
[inferred] = reasoning; [community].

## 0. Verdict

- **Nothing new passes the kill criterion, so nothing new is built and `[ResCache] OutOfProcess` does not exist.**
  Every sizeable EA cache left in the process is either in use (live engine objects reached through raw pointers),
  hot (looked up more than a thousand times a second under the resource lock), or on disk already.
- **What can leave is already built and waits on one gate:** resource read buffers (docs/305, `PackedStream/Raw`,
  16-36 MB of load cores [measured lower bound]) and string tables (docs/306, ~20 MB with S3PP, 36 MB counted in the
  cache below) as services 1 and 2 of the helper channel in our Apex fork; the audio cache gets a cap, not a move
  (docs/305 §5.2). None of their queues has run: the stack has never run with Apex's restart host on (Atlas docs/025
  §0). That compat run, not a new service, is the next step for this whole area.
- **ADR 0002 holds, with a sharper reason.** The ResourceCache keeps almost nothing cold: its budget is 200 MB
  (`GraphicsRules.sgr`), the resources in use already count 320-410 MB, so EA evicts every unreferenced entry as soon
  as it lands on the flush list (census: 232 cold entries of 108,851). A host-side LRU behind it would hold what EA
  re-reads, and EA re-reads almost nothing in play (~0.25 package reads a second, 0.2 MB per 5 min [measured]).
- **The biggest counted line is textures, and they are not EA memory.** 197.5 MB of the 321.9 MB census is
  `0x00B2D882` (DDS), counted as 52 B + pixel bytes per texture (docs/40 §3 step 8 [verified]); the pixels live in
  the D3D9 layer: with the RTX Remix bridge the GPU objects are in the 64-bit server and the client's CPU copies are
  freed when idle by Apex patches 0003 / 0008 (docs/270 §bridge [verified source]). Apex owns that.
- **Package index parked memory (docs/23 #20):** nothing changed in its favour. The size is still 15-21 MB
  (docs/305 §5.1 [measured on disk]), and the probe it would serve runs on every resource miss under
  `g_ResourceLock` (0.67% of sim-thread samples in `DBPF_IndexCompressed_Find` + `GetPackageSource`, docs/39 §7
  [measured]): killed for out of process. The parked idea itself (a key -> package map, docs/39 R1) is an in-process
  CPU gain and stays parked; if reopened it belongs in Apex's ResourceLookupCache.

## 1. Census

Sources: rc census = `[MemDiag] RcCensus` in `castrim-live` 2026-11-08 OFF arm, BP, last sample #24 (no bridge, DXVK
era) [measured]; owners = `[MemDiag] AllocSitesOwners` `memdiag3-owners-1115` 2026-11-15, BP, sample #3, without /
with Apex 3.2.3 (heap = sampled blocks < 64 KB scaled + all larger ones; cores = EA allocator reservations)
[measured]; finds and reads = `[MemDiag] Access` `memdiag2-access-1115` 2026-11-14 rerun, BP, no Apex [measured].

### 1.1 Inside the ResourceCache (census #24: 108,851 entries, 57 types, 321.9 MB counted, 232 cold)

| Type | What | Entries | MB counted | Held by | Out of process? |
|---|---|---|---|---|---|
| `0x00B2D882` | DDS textures | 1,327 | 197.5 | ref2 631, ref3+ 684, cold 12 | pixels are already D3D9-layer memory (bridge + Apex); the EA object is 52 B |
| `0x220557DA` | STBL string tables | 3,057 | 36.1 | ref2 all | built: docs/306 (copy-out lookups) |
| `0x067CAA11` | BGEO blend geometry | 521 | 15.5 | ref2 all | no: CAS blending reads it through raw pointers [inferred, docs/215 row 12] |
| `0x319E4F1D` | OBJD catalog products | 9,505 | 14.8 | ref3+ all | no: products handed to script as native handles, filters walk them in memory (docs/08 §2.5 [verified]) |
| `0x033A1435` + `0x0341ACC9` | CAS compositors | 80,329 | 10.1 | registry leak | no: in-process fix `[CasTrim]` (docs/539) |
| `0x025ED6F4` | (not named here) | 1,822 | 4.9 | ref3+ 1,802 | in use |
| `0x015A1849` | GEOM | 54 | 1.9 | 32 cold | cold but tiny |
| `0x034AEECB` | CASP | 568 | 1.6 | 37 cold | cold but tiny |
| rest (49 types) | | | < 1.5 | | |

### 1.2 Caches outside the ResourceCache

| Cache | Size | Access | Verdict |
|---|---|---|---|
| Package indexes (16 B per entry, per package) | ~15.2 MB EA + ~3.2 MB this machine's CC [measured on disk, docs/305 §5.1] | every resource miss probes all packages in priority order under `g_ResourceLock` [verified, docs/39] | **killed** (lock + per-miss round trips) |
| Resource read buffers (`ea.res` cores) | 168-210 MB of cores; Raw 16-36 MB movable [measured, docs/305] | inside one read | **built** (docs/305), not run |
| String tables | ~20 MB with S3PP, 90-290 without [docs/215 row 10] | copied out per lookup [verified, docs/54] | **built** (docs/306), not run |
| Audio binary cache | 7-15 MB [measured, docs/215] | mixer reads sample buffers through raw pointers [inferred] | not movable; cap built in Apex fork (docs/305 §5.2) |
| AnimationCache | 15 MB fixed; clip cores 96-120 MB [measured] | players hold clip pointers | not movable |
| CAS part cache (`CASPartCacheService` `[0x11CE42C]`) | ~0.7 MB for 7,279 parts [contributor-measured, docs/190 Q2] | CAS and outfit builds | too small |
| Buy/build catalog `[0x11ED034]` | ~12.8 MB [contributor-measured] | product pointers, in-memory filter | not movable (docs/08) |
| Compositor caches (`compositorCache.package`, `simCompositorCache.package`, `CAS/CompositorCache`) | index of a writable package only | §2 | **already on disk** |
| Preloader `HoldOntoResources` | 0 entries held by it alone (census `ref1held=0`) [measured] | - | nothing to move |
| `cas` heap owner (code `0x5A2C00-0x60AA00`) | 85-108 MB [measured] | CAS geometry, blend data, compositors, outfits in use | owners below the top-12 cut: queue §5 job 2 |
| `gfx` heap owner (renderer code ranges) | 79-101 MB [measured] | renderer objects; not named | owners below the top-12 cut: queue §5 job 2 |

### 1.3 How hot the ResourceCache is [measured, BP, no Apex]

`diag: memaccess` periodic windows: `rc get` 326,944 in 221 s and 353,416 in 300 s = **1,180-1,480 finds a second**,
12-16 k distinct keys per 5 min; package reads `rd n` 56 and 79 per window, 0.2 MB each (all 19.6 MB of big reads
happen at load). Every find runs through `Find 0x7324E0` on the game's threads; that alone kills serving the cache from
another process (1,200-1,500 round trips of 6-17 us = 7-25 ms/s, more than the whole resource system costs today,
~1.2% of sim-thread samples, docs/39 §7).

## 2. New static finding: the compositor caches are packages on disk [verified]

`0x5BC6D0` (the `CAS/CompositorCache` open/trim function):
- allocates `0x398` = 920 B tagged `"CAS/CompositorCache/Cache"` (`0x5BC72C`) and constructs it with `0x4A8F70` on a
  path built from the user folder (`0x72F1C0` slot `+0x28`, `0x4FF380` / `0x4FF470`). 920 B is the size of one
  `DatabasePackedFile` object (docs/08 §2 "920 B per package object" [verified]).
- reads `"UnboundedTCC"` and `"ClearTCC"` debug switches (`0x58B100`), opens the package, then registers it with the
  resource manager (getter `0x4AFD20`, slot `+0x34`, `0x5BC84B`).
- trims: `[budget +0x38] + 0x100000` compared with the package's size (`0x4A6EB0`) at `0x5BC8B4`; over it, it lists
  keys into `"CAS/CompositorCache/TempKeyList"` and deletes until under.

So the compositor caches hold their bytes in a package file; in memory there is the package object and its index.
`docs/08 §2.5` marked this [inferred]; it is now [verified]. S3SS's "WorldCache 512 MB uncap" (Steam address commented
out at `0x5BC8B4`) patches exactly that size check: it raises a disk-file limit, not an in-memory cache (docs/08 had
this [inferred]; the site is now [verified], S3SS's intent [community]).

## 3. What the RTX Remix bridge already takes out, and what stays

| Leaves the process | Stays |
|---|---|
| D3D9 device, textures, buffers, shaders, VRAM bookkeeping, driver (~430-640 MB of renderer stack without the bridge, docs/215 row 2) | EA resource objects (texture objects are 52 B each), all non-texture decoded resources in §1.1 |
| CPU copies of idle managed textures and buffers (Apex patches 0003 / 0008: freed in the client, fetched back from the server) | CPU copies of textures and buffers in use, in the bridge client (Apex's area) |
| | read buffers, string tables, package indexes, audio, animation, catalog, CAS (§1.2) |

## 4. The two that came closest, and why they are not built

1. **A host LRU behind the ResourceCache** (lower `CacheBudget`, host keeps decoded records of read-only packages,
   misses served by the resread service of docs/305): exact by construction (same bytes, keyed by package and record
   offset), but there is nothing to serve: EA keeps no cold share (§0), re-reads ~0.25 a second in play, and the budget
   barely moves held bytes (docs/08 Phase 0: budget 0, 50 and 200 MB all hold 390-410 MB). Gain ~0 MB, so killed under
   the measure-before-build rule. Reopen if the §5 census on today's stack shows the cold share above ~30 MB or
   reads above ~10 a second in play.
2. **Package indexes as a lookup service** (host answers key -> package): killed by the lock rule. The lookup runs
   inside `GetPackageSource 0x4AFFC0` under `g_ResourceLock`, on the sim thread for synchronous script loads
   (docs/39 §2.1 [verified]), and is the slot Apex's ResourceLookupCache owns. 15-21 MB.

## 5. Measurement queue (dry-run only)

`tools/harness/queues/rescache-census-1126-m1126b.json`, master set m1126b, Bin state apex, ~30 min, sizes only:
- `bp-rescache-census`: `[MemDiag] RcCensus=1 Access=1`. Gives the census of §1.1 and the finds/reads of §1.3 next
  to Apex and the bridge (both older runs had neither). `SKIP mem access` in its log means Apex now owns slot 6 of the
  cache vtable; that is a finding, not a harness bug.
- `bp-rescache-owners`: `AllocSitesOwners=1 AllocSitesWithApex=1 AllocSitesTop=32`, to name the `gfx` and `cas` heap
  owners (§1.2) that the top 12 never listed.

Read: if a named owner over ~20 MB shows up that is (a) not reached through raw pointers and (b) read less than a few
times a second, it becomes the first `[ResCache]` candidate; otherwise this area stays closed until the restart-host
compat run lands the docs/305 / 306 services.

## 6. Sources

docs/08 §2.5 and Phase 0, docs/39 §5-8, docs/40 §3-4, docs/190 Q2, docs/215 rows 2, 3a, 10, 12-14, docs/270 (bridge),
docs/305, docs/306, docs/539, ADR 0002; Atlas docs/012 O3 and docs/025. Logs under `probe-out/queue/`:
`castrim-live-2026-11-08-bp-castrim-live-off-0414-ts3native.log`,
`memdiag3-owners-1115-2026-11-15-bp-memdiag3-owners-{0020,apex-0035}-ts3native.log`,
`memdiag2-access-1115-2026-11-14-bp-memdiag2-access-2337-ts3native.log`.
