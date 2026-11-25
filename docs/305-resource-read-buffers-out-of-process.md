# 305. Resource read buffers out of process (restart host helper)

2026-11-20, lane resread. Rodney 2026-11-20: build docs/215's first target (resource read buffers, 112-192 MB of EA heap
cores) in the 64-bit restart host we sent to Apex (docs/241). Later the same day: also the package indexes and the audio
binary cache as extra services (§5). Built in our Apex fork, default off, PR draft for Rodney's OK. Tags as in AGENTS.md.
Steam TS3W.exe 1.67, base `0x400000`. Ledger: docs/51 §549.

## 0. Verdict

- **Only one of the three read-buffer owners can leave the process: `ResourceLoad/PackedStream/Raw`** (`0x72D385`), the
  raw (compressed) copy of a packed-stream record, allocated, read, decoded and freed inside one function [verified].
  Built: the restart host (ts3wrap.exe, x64) reads and RefPack-decodes the record; the game copies the decoded record
  straight into its own buffer; the raw buffer never exists in the game.
- **`.../DecompressedRecordData` (`0x72D62B`) and `Services/EAIO/EAStreamMemory/data` (`0x4DEC17`) cannot move**: their
  pointers are handed out raw to factories, the audio cache and DDS streams, which keep them [verified, §1]. Their fix is
  in process (keep them out of the long-lived EA cores): Apex already has `stream_arena` (dev switch, 128 KB-8 MB stream
  buffers from one top-down reservation, unmeasured); an arena for `DecompressedRecordData` is the open decision (§6).
- **Gain path (measured, lower bounds)**: AllocSites 2026-11-15 (`memdiag3-owners-1115`, BP 10 min, top 12 rows per
  sample): without Apex the Raw cores were 36 MB at load (`from=0x0072D385`, 9 cores), Decompressed 60, EAStreamMemory
  40 -> 56 in play; with Apex 3.2.3 Decompressed 40 at load and EAStreamMemory 44 -> 68 in play, while Raw fell under the
  top 12 [measured]. So the movable part is **~16-36 MB of load-time cores** [measured without Apex; with Apex unknown,
  below the top-12 cut], not 112-192. The memory queue (§7.2) measures it with Apex 4.0.11 and the restart host.
- docs/215 row 3a was wrong on the path: the three sites are not on the DBPF read `0x4A8380` (the `memaccess` hook there
  never sees them); sites 1-2 belong to PackedStream (vtable `0xFFD790`, OpenRecord `0x72D470`), site 3 to
  `EA::IO::MemoryStream` [verified].
- ADR 0002 is not repeated: it dropped a resource **cache** in a helper (390-410 MB in use, ~20 MB cold). This moves no
  cached resource, only a buffer that dies inside the read.

## 1. Static map [verified unless marked]

| Site | Function / callers | Life | Who reads the bytes | Movable |
|---|---|---|---|---|
| `0x72D385` Raw | `0x72D360` thiscall(stream; u16 compression, offset, rawSize, memSize, dest, throttled), `ret 18h`, al; only caller `0x72D649` in OpenRecord `0x72D470` (vtable slot `0x34`), which holds the stream lock `+0x40` and charges the throttle `[+0x38]` first | allocated through `[this+0x10]` vtbl+8, read by `0x72D300` -> `0x72CF90` (RW file-system request on handle `+0x2C`, or the throttled handle `+0x30`, blocking), decoded by `0x72C980`, freed at `0x72D3C7` on success and failure | nobody outside the function | **yes** |
| `0x72D62B` DecompressedRecordData | OpenRecord compressed branch; buffer to `RecordRead` ctor `0x739F80` (MemoryStream at `+0x28`, `0x4DF120(buf, size, usePtr 1, freePtr 1, alloc 0)`) | SharedPointer; freed by `0x4DEA80` at the last release (normally RecordRead dtor `0x739E60`) | after `GetType == 0x347223D2` the raw pointer `[stream+4]->[+8]` is read directly: AsyncFactory `0x726F01`, audio ResourceBinary `0x8B44C1` (AddRef `0x8B44E1`, kept long), DDSStream `0x7E76FD`, `0x722C9D` | no |
| `0x4DEC17` EAStreamMemory | SharedPointer ctor `0x4DEBE0`, only caller `MemoryStream::Realloc 0x4DEE40` (SetSize `0x4DF1BE`, Write growth `0x4DF05C`, SetPosition growth `0x4DEF7A`); creators: AsyncLoader copy `0x727540` (whole resource, job thread), `StreamAdapter 0x7D83C6` | copied and released on each Realloc (growth `cap*1.5+incr`); final free `0x4DEA80` | same raw-pointer consumers | no |

PackedStream: every one is read-only (Open `0x72D790` refuses write access); path = UTF-16 range `+0x1C..+0x20`, open
flags `+0x14`, handle `+0x2C`. Creators: `World/WorldDB` (`0xC63605`, `0xC63675`, `0xC670F1`, `0xC6CE5C`),
`CAS/Compositor/Cache` (`0x6CC5E4`), `LoadSaveManager/MetadataDB` (`0xAAB8C4`, `0xAAD787`, `0xAAF33D`), `0x734306`,
`0x734C31`. Uncompressed records (compression 0) go to `0x72D6B4` and are read straight into the record buffer (no Raw).
Compression `0xFFFF` uses the built-in RefPack codec (`0x4EBFD0` -> `0x4EB3B0`); other values go to a codec provider
`[this+0xAC]` vtbl+0x6C (which ones: not determined). EA bug: `0x72CF90` returns 0/1, `0x72D300` tests `!= -1`, so a
failed file read counts as success [verified bytes; effect inferred: the decode header check catches most]. Hooks by
others: none on `0x72D360` / `0x72D470` / `0x4DEE40` / `0x4DEBE0` / `0x739F80` (`hook-sites.ts --check`,
`apex-hooks.csv`); Apex reads vtable `0xFFD790` (ResourceLookupCache), so the vtable is never swapped. Prologue
`53 32 DB 83 7C 24 0C 00` (3 instructions, nothing relative, no jump into bytes 1-7, no data references) [verified].
Open: which file each creator opens; main vs AsyncLoader share (needs a counter); the throttle setter `0x72CA70`.

## 2. Design

- **Host**: Apex's restart host `ts3wrap.exe` (x64 [verified `native/ts3wrap/ts3wrap.vcxproj`], started by Apex when
  `[ui] restart_host = true`) runs the services as threads (Rodney 11-20: no third process, no launch from the asi). One
  helper channel shared with the stbl lane (`probe-out/oop-helper-handoff-1120.md`; skeleton `a3494b7d` by that lane):
  section `Local\ApexOopHelper-<pid>` (8 slots x 256 KB), request / per-slot done events, two host workers, 2 s timeout,
  host death seen through its process handle.
- **Service resread** (id 1, `native/ts3wrap/svc_resread.cpp`, `resread_proto.h`): decode (path, game handle, offset,
  rawSize, memSize) -> the host opens the package (one handle per path + game handle, so a file the game opens again is
  opened again here), reads the raw record at the offset, RefPack-decodes with Apex's `RefPackCodec::Decompress`
  (a translation of `0x4EB3B0`), checks the size equals memSize. Records <= 256 KB come back whole; larger ones under a
  ticket in 256 KB chunks; tickets expire after 10 s, at most 32.
- **Game side** (`features/resource_offload.cpp`, patch `ResourceOffload`): Detours entry hook on `0x72D360` (signature
  in `GameAddr::PackedStreamReadRecord`, bytes checked). Served only when: compression `0xFFFF`, `offset != 0`, vtable
  `0xFFD790`, flags read-only, handle set, path <= 1024 chars, host ready. Answer copied straight into `dest` (no staging
  buffer in the game). Returns al = 1.
- **Fallbacks**: switch on but no restart host -> nothing hooked, one log line `[OopHelper] ResourceOffload is on but the
  restart host is off: staying in process. Turn on the restart host ([ui] restart_host) so ResourceOffload can move out
  of the game`, status text says the same (no menu row: the restart host has none either, so the UI hint is the status
  text). Any refused case, busy slots or an error answer: that call goes to the game's own code. Timeout or host death:
  every service stays in the game for the session (OopClient latch). A switch turned on mid-session waits for the next
  start (the host opens the section at its own start).
- **Exactness**: the record is the same bytes the game decodes (same file, same offset, same decoder semantics);
  `verifyEvery N` makes the game also read every Nth served record into `dest` and compare; on a difference the game's
  bytes stay, the log names the record. No Sim, save or script result changes (memory-only; household exemption n/a).
  Nothing is written into saves.
- **Cost** [measured host test]: echo round trip 14 us; per record = round trip + file read + decode in the host (the
  game's own path did the same read + decode on the calling thread while holding the stream lock).

## 3. What stays in the game and why

`DecompressedRecordData` and `EAStreamMemory/data` are reached through raw pointers by code that keeps them (§1), so
copy-on-access through the stream vtable is not possible: a stream object with another GetType makes the four consumers
take their copy paths (`0x726E03`, `0x8B4507`, `0x7E771A`, `0x72A486`), which put the bytes back into the game (the
`0x72A486` copy allocates at `0x4DEC17` again). Paging a window under live pointers is the docs/215 rule's dead end.

## 4. Switches and builds

- `[patches.ResourceOffload] enabled = false` (experimental, Steam only), `verifyEvery = 0` (test setting); needs
  `[ui] restart_host = true`.
- `[patches.AudioCacheCap] enabled = false`, `capMB = 16`, `idleSeconds = 20` (experimental, Steam only, §5.2).
- Fork branch `feature/resource-offload-1120` = upstream 4.0.11 `fe5c99eb` + skeleton `a3494b7d` + `4603636a` (ResourceOffload) + `87ed57a4` (AudioCacheCap).
  builds.json: `ApexRadiance.asi.resread-1120`, `ts3wrap.exe.resread-1120` (kind `apex`, hashes there); the asi also has `AudioCacheCap` (§5.2).
- Harness (this branch): Bin state `apex-oop` in `tools/turbo-compat.ts` (latest Apex release + bridge, asi and
  ts3wrap.exe from `game-copy/apex-oop-test`); `apexToml` job key copied from feature/stbl-index-1120 (same diff).
- Checks: `tools/oop_helper_test/run.ps1` (x86 client vs x64 host: resread round trip, 3-chunk record, wrong size,
  wrong offset, missing file, ticket dropped after the last chunk) pass; Apex `check.ps1 -Suite Smoke` pass;
  `public_text_check.py` pass.

## 5. Small owners: package indexes and audio binary cache

### 5.1 Package indexes: not moved

- **Size [measured on disk, 2026-11-20]**: index entry counts from the DBPF headers (`+0x24`), at the 16 B/entry of the
  in-memory compressed layout (docs/190 M5; type/group pairs and size overflows add a little): Steam install (all packs)
  281 packages, 993,144 entries = **~15.2 MB** (worlds 76,758 of them, loaded one at a time); this machine's Mods 171
  packages, 209,699 entries = ~3.2 MB; synthetic CC sets: cc-2k-v2 (2,200 files) 1.4 MB, many-mods-cc (4,750 files)
  2.6 MB. So ~18-21 MB even with heavy CC; ADR 0002's ~3.5 MB for 172 CC packages holds. No in-game count exists (no
  `idx` lines in any log).
- **Access [verified]**: index object (100 B, ctor `0x4AAF50`, vtable `0xFB2840`) at package `+0x70`, tables at
  `+0x08` (16 B entries), `+0x20` (size overflow), `+0x38` (type/group); Find `0x4AA1B0` has one caller, GetRecord
  `0x4AA470` (slot `+0x28`), which copies into a per-index scratch record `+0x54` and returns a pointer to it; key
  enumeration `0x4AC9C0` copies keys out. Freed at package close (`0x72C8F0` -> `0x4AB5A0`); an idle Resource.cfg package
  closes and keeps a `DatabaseShadow/DatabaseIndex` key set instead (`0x735320`, `0x72DB40`, probe `0x72DBD0`).
- **Why not**: readers get copies, so it could move, but the cost is wrong: `GetPackageSource 0x4AFFC0` asks every
  package in priority order on each resource miss (docs/39), one index find per package; out of process that is one
  round trip (~14 us) per package per miss, or a whole-lookup service on the provider slot, which is Apex
  ResourceLookupCache's slot (it re-probes the answering package in process). The tables are permanent load-time
  allocations inside EA cores, so moving them frees address space only if the cores are never made, ~15-21 MB. Not
  built; if wanted, it belongs inside Apex's lookup cache (the cache answers from the helper and re-probes there).

### 5.2 Audio binary cache: not movable, cap switch

- **Structure [verified]**: audio system `[0x11DA098]` (Init `0x8B3C20` sets it at `0x8B3C78`; vtable `0x10289E8`, base
  `0x103B018`), cache map `Audio/BinaryCacheMap` `+0x3330`, byte count `+0x2732C`, cap `+0x27348` (100 MB, ctor
  immediate at `0x8C0CF5`; config key `BinaryResourceCacheSizeMB` read by `0x8B68E0` overrides it), idle time
  `+0x27340` (double 20.0 s), trim slice `+0x27338` (100). Get `0x8B8D40` (slot `+0x1C8`) stamps last use and AddRefs
  into the caller; a miss reads the whole record into one shared buffer (`0x8B44A0`, `+0x18`/`+0x1C`/`+0x28`). Trim
  `0x8B8F30` (slot `+0x23C`, thiscall, no args, `ret`; one call site `0x8BFD00` on the audio thread [inferred]) erases,
  while bytes > cap, entries idle longer than the idle time; the erase (`0x5EE1E0`) only Releases the cache's
  reference (`0x924180`, dtor `0x8B4610` releases the SharedBuffer at refcount 0). Cap readers: ctor, config reader,
  Get (only `!= 0` = cache on) and trim; nothing else.
- **Why not movable**: sounds in this cache are fully resident and the mixer reads the sample buffer through a raw
  pointer for the life of a voice [inferred; voices hold a reference, verified via Get's AddRef].
- **Why "cap only" was right, and what was wrong**: the observed 7-15 MB (docs/215) never reaches the 100 MB cap, so
  EA's trim never runs and every sound played stays for the session [inferred]. Built: Apex patch `AudioCacheCap`
  (`[patches.AudioCacheCap] enabled=false capMB=16 idleSeconds=20`, Steam only, no hook): writes the cap (never 0: 0
  turns the cache off and stops the trim) and the idle time into the live audio system from the pump, re-applied when
  the game's configuration sets its own value, the game's values put back when off. Rule `NextCap` with compile-time
  checks. Gain [inferred, small]: large samples are allocator direct blocks returned to the OS when freed, small ones
  stay in heap cores; the direct-block threshold is not traced. Measured by the `bp-audiocap-mem-*` pair.
- **Not a game-result change**: an evicted sound is read from its package again at the next play (the same path as a
  first play); a playing sound keeps its own reference.

## 6. Decisions for Rodney

1. The movable gain is the Raw cores only (~16-36 MB at load, lower bound, to be measured with Apex 4.0.11). Ship it as
   the ON arm of v0.3.1 if §7.1-7.2 pass?
2. The other ~100-120 MB (Decompressed + EAStreamMemory cores) need an in-process change: measure Apex's `stream_arena`
   (dev switch; needs a Developer Apex build) and build a `DecompressedRecordData` arena (hook `0x72D621` + the allocator
   argument at `0x739FBC`), same fork, default off? Not built here: it is not "out of process" and changes Apex's
   allocator area beyond the brief.

## 7. Queues (chain `probe-out/queue/chain-resread-1120.sh`, Bin state apex-oop, restart host on)

1. **Correctness** `resread-correct-1120` (RUN FIRST): OFF, ON verifyEvery 1 (BP 10 min), ON verify + France round
   trip, ON verify IP 10 min. Pass: served > 0, checked > 0, differed 0, no `Host lost`, no ScriptError, no crash,
   ts3wrap.exe ended with the game.
2. **Memory** `resread-mem-1120`: ABBA, 4 BP save-reload cycles each, AllocSites + AllocSitesWithApex: largest free per
   load, EA cores by tag (Raw `0x72D385` gone in ON).
3. **Timing** `resread-time-1120` (needs quiet machine; `chain-resread-1120.sh time`): ABBA BP 10 min: load time,
   ms/s, hitch p99/p99.9/max, us per record.
4. AudioCacheCap: `bp-audiocap-on` in the correctness queue (capMB 8, idle 5 s), pair `bp-audiocap-mem-off-e` /
   `-on-f` in the memory queue.
5. Apex compat: every job runs the newest Apex release + bridge with the restart host on (the Bin state fetches the
   latest at snapshot).
