# 306. String tables out of process (Apex `StringTableOffload`, restart-host service)

Written 2026-11-20 (Rodney 11-20: move the string tables out of the game into the 64-bit helper, PR to Apex). Ledger
§551. Builds on the in-process index of docs/301 (Apex PR #140). Shared helper with the resource read-buffer lane:
`probe-out/oop-helper-handoff-1120.md` (local only). Fork branches: `feature/oop-helper-1120` (helper skeleton, owned by
this lane) and `feature/stbl-oop-1120` (#140 + skeleton + this feature); harness parts on `feature/stbl-oop-1120` here.

## 0. P0 verdict: how much leaves the game

Counted offline from the packages (`STBL` resources, English, one table per TGI; scratch script over the DBPF indexes,
RefPack decoded) [verified, counted]; the per-table formula is from the table code (§1) [verified static].

Bytes per loaded table = 31,736 (the table object: 7,919 inline bucket heads) + 24 per entry + 2 x (chars + 1) per entry,
plus three allocator headers.

| Setup | Tables | Entries | Chars | Fixed (bucket objects) | Entries | Text | Total |
|---|---|---|---|---|---|---|---|
| Base game + all packs (Steam install, no CC) | 20 | 57,223 | 3.74 M | 0.6 MB | 1.3 MB | 7.2 MB | **9.2 MB** |
| Rodney's Mods folder (CC only, 171 packages) | 3,037 | 7,575 | 0.64 M | 91.9 MB | 0.2 MB | 1.2 MB | **93.3 MB** |
| Harness BP (user CC + `cc-2k-v2`, docs/301 §5 log) | 4,257 | 115,026 | ~4.5 M [inferred: base + CC chars] | 128.8 MB | 2.6 MB | ~9 MB | **~140 MB** |
| Same with S3PP `StblBucketLayoutTuning` | | | | right-sized | | | ~20 MB [community] |

- The cost is the **fixed table object of every CC/store package's STBL**, not the text: a CC package carries a table
  with a median of 2 strings and pays 31.7 KB for it. The text of the whole base game is 7 MB.
- Existing AllocSites rows agree on the text side: `0x59FB02` (the parse's pool allocation) 12.0-12.6 MB live in
  BP runs (n = 1,994-2,954 tables) [verified, existing logs]. The 0x7BF8 table objects come from the tagged allocator
  `0x4E3F90`, which AllocSites does not sample, so no existing run shows them.
- **The cache holds them too** [verified, §1]: the resource manager offers every loaded table to the ResourceCache, which
  keeps its own reference. Dropping the string service's reference does not free a table, so the move also takes each
  table out of the cache (§2).

**Verdict:** worth building. On Rodney's setup (user CC + `cc-2k-v2`, 4,257 tables) about 140 MB of string tables live
in the game; on his CC alone about 93 MB; on a no-CC install about 9 MB. The move frees all of it except a ~2 MB shared
section and small per-thread buffers. S3PP's bucket fix leaves ~20 MB [community]; ours leaves ~2 MB, with no layout
rewrite. What we cannot claim before a run: how much of it shows up as **largest free block** (the tables are freed
into the EA heap: ledger I-551-11). Queue `stbl-oop-mem-1120` measures it.

## 1. Game side [verified static, ledger §551; Steam 1.67]

- Table object 0x7BF8 bytes, tag "String/TableResource" (`0x4E3F90`): refcount +4, key +8, 7,919 buckets +0x28,
  missing value -1 at +0x7BE4; entries (24 B) and text pool from parse `0x59FA20`. A duplicate key is not chained.
- Load: Refresh `0x59B190` (thiscall(service; key*)) gets the table from the resource manager (slot 0xC), which also puts
  it in the ResourceCache; the cache keeps its own reference (cache filter `0x731E50` admits STBL). Clearing the node
  alone frees nothing.
- Free: Clear `0x59AF00` (rebuild, shutdown) releases every node's table; Refresh replaces one. Manager slot 0x5C with
  (resource, 0) takes a resource out of the cache that holds it [verified path; reference drop inferred, I-551-5].
- Readers: only GetString `0x59C5B0` and the native lookup `0x59CB00`; both copy out. No other run-time consumer of
  STBL resources (only the DLC installer code) [verified scan].
- Threads: GetString on the sim thread [verified via icalls]; the native lookup on the main thread [inferred].

## 2. Design

- **Helper:** the out-of-process service lives in Apex's restart host `ts3wrap.exe` (Rodney 11-20): no third process,
  no launch from the asi. The shared channel and skeleton (`oop_channel.h`, `oop_host.cpp`, `oop_client.cpp`) were
  built by this lane on fork branch `feature/oop-helper-1120`; the resource read-buffer lane adds its service there
  (`probe-out/oop-helper-handoff-1120.md`). Service id 2 (`svc_stbl.h`): ops reset, table (chunked), drop, order,
  lookup, stats. The host answers by the walk's rule: the first table in list order whose entry (first copy) is not empty.
- **Move** (Apex feature `StringTableOffload`, "Text tables outside the game", default **off**, experimental, Steam only):
  1. Tables loaded before the feature started (the boot rebuild, I-551-10) move once the host answers: every table's
     chains are sent; the host gets the list order; **every key is compared** with the game's own walk (equal text or
     the tables stay in the game, logged); then the walks switch to the host, the nodes are cleared, and after 1 s (a
     walk that started before the switch) each table is taken out of the cache and released.
  2. Every later load (Refresh hook: world packages, DLC, locale rebuild) moves at once: send, clear the node, uncache,
     release. Peak = one table.
  3. Lookups: both walks start at a node Apex hands them (6-byte jump at `0x59C5C2`, 5-byte jump at `0x59CB04`): a
     per-thread node whose one-entry table carries the host's answer (the game probes it, copies and formats as before)
     or the list end (missing). One IPC round trip per lookup.
- **Fallbacks:** no restart host: stays in the game, logs `[OopHelper] Text tables outside the game is on but the
  restart host is off: staying in process. Turn on the restart host ([ui] restart_host) ...`, and the switch row shows
  the same reason (the feature's start error). Host lost (2 s timeout, process ended, a failed upload): every node's
  table is loaded again through the game's own Refresh (same package priority, same cache) on a worker thread, lookups
  wait for it (at most 10 s), and the feature stays in the game for the session. Switching it off at run time does the
  same reload before unhooking.
- **Stand-downs:** ja-jp locale (its lookups treat the list's first table apart, and our node is never first);
  `CCStringMerge_Strings.package`; another mod changed the lookups or the layout (S3PP `LocalizedStringCache` /
  `StblBucketLayoutTuning`). `StringTableIndex` (#140) is stopped while this is on (both start the same walk) and
  stands down if started after it.
- **Self-check:** the all-keys compare at the move (identical text for every key of every loaded table, against the
  game's own walk); `freed` vs `still held` (refcount 1 before the release = the game frees it) in the `Moved:` line.
- **Household rule:** n/a for Sims (memory only, every text identical; the compare enforces it). No mod data in saves:
  the host keeps texts in memory only.

## 3. S3PP coexistence (decision)

S3PP (on by default in its own ini) has `StblBucketLayoutTuning` (right-sizes the 7,919 buckets: ~20 MB left) and
`LocalizedStringCache` (a cache on GetString's entry, known blank-label issues, docs/17). Both change exactly what this
feature hooks and reads. Per prefer-own-fixes-over-s3pp: **ours replaces S3PP's string features; with either on, ours
stands down and says which S3PP switch to turn off** (no combining: the layout rewrite moves the offsets the move reads,
and the cache answers before our walk). Ours frees more (~2 MB left vs ~20 MB) without rewriting the hash code. The PR
text and the Apex page tell S3PP users to set `StblBucketLayoutTuning=0` and `LocalizedStringCache=0` to use it.

## 4. Lookup cost

Round trip of a small request x86 -> x64 host: 6-11 us (`tools/oop_helper_test`, 20,000 calls) [measured, offline].
The game's walk with 4,257 tables: ~70 us a found key, ~135 us a missing key; #140's index: ~2 us (docs/301 §5). So the
move is still ~7-20x faster than the game's own walk on a CC-heavy install, and ~3-5x slower than #140. Not added: an
in-game answer cache (it would keep text in the game; add only if the timing queue shows a cost).

## 5. Tests

| Queue | What | Status |
|---|---|---|
| Offline `tools/oop_helper_test/run.ps1` (fork) | channel + stbl service through the channel, x86 client vs x64 host, host death, oversize answers | pass |
| Offline `tools/string_table_offload_test/store_check.cpp` (fork) | first table wins, empty passes on, first copy, order change, chunks, drop, reset, malformed input | pass |
| `stbl-oop-smoke-1120` | **RUN FIRST**: one ON load, read-back lines | not run |
| `stbl-oop-correct-1120` | ABBA OFF/ON: label_check hash equal, all-keys compare `differed 0`, bench us | not run |
| `stbl-oop-mem-1120` | ABBA + S3PP arm: free, largest free, priv at gc-after | not run |
| `stbl-oop-time-1120` | **needs quiet machine**: ABBA x2 ms/s, bench us, load time | not run |

All on Bin state apex with the fork asi + `ts3wrap.exe` of build `apex.stbl-oop-1120` copied into Bin, `[ui]
restart_host = true` in every arm (the Apex compat run on the newest Apex + bridge is these queues). `tools/turbo-compat.ts`
now lets restore remove `ts3wrap.exe` (EXTRA_OK).

## 6. Upstream

Follow-up PR on top of #140 (not an extension of it): #140 is a small speed change already in review; this one adds the
helper channel (shared with the resource read-buffer move) and a memory feature that replaces #140 while on. Draft
text: `probe-out/stbl-oop-pr-draft-1120.md` (Rodney's OK before posting).

## 7. Open

- Early install (hook Refresh before the boot rebuild, start the host earlier) so the boot tables never enter the
  cache: only if the memory queue shows the late move leaves the largest free block short.
- EA 1.69 signatures; the native lookup's thread.
- Host restart after a lost host (today: back in the game for the session).
