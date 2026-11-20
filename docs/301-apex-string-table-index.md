# Apex string table index (StringTableIndex) PR

Written 2026-11-20 (Rodney 11-20: build the in-game string table index in our Apex Radiance fork and send it upstream;
it replaces the offline merge idea of the compat branch `feature/compat-smellymods-stringmerge-1119`, whose RE notes are §8). Ledger §547. Branch `feature/string-table-index` in the fork, from
upstream `main` at 4.0.11; harness and agent parts on `feature/stbl-index-1120` here.

Credit: the idea to cut the per-lookup table walk comes from SmellyBelly's offline tool SmellyMods CC String Merge
(MTS d/701426, `CCStringMerge_Strings.package`). We do not contact the author (Rodney 11-20).

## 1. What it is

Apex feature *Faster text lookups*, `[patches.StringTableIndex] enabled` in `ApexRadiance.toml`, **default off**,
Experimental, not in the Overview group or any profile. Steam 1.67 and retail 1.67 by signatures (address group
`StringTableIndex`, each signature unique in both exes [verified offline check]); EA 1.69 unverified (encrypted on
disk). Source: `features/string_table_index{.h,.cpp,_core.h}` in the fork; Apex docs page
`docs/features/performance/faster-text-lookups.md`. No Sim behaviour changes: text lookups return the same string
(household rule n/a).

## 2. Game side [verified, ledger V-547-1..6]

- `LocStrSvc_GetString_A` Steam `0x59C5B0` (retail `0x59CF50`) walks the STBL list at service `+0x48` from `0x59C5C2`
  and probes each table with `STBL_Find 0x59AF50` (7,919 inline buckets at `table+0x28`) until a text is non-null and
  non-empty. A missing key probes every table. No lock.
- The list changes only in clear `0x59AF00`, add one table `0x59B190` and rebuild `0x59BDE0` (start, locale change).
- A second walk with the same rule at `0x59CB00` is not hooked.

## 3. Design

- 6-byte jump at the walk start into a naked stub that redoes the original 16 bytes, asks the index for the start node
  and enters the walk's loop test (walk `+0x4C`): the winning node (one probe), the sentinel (EA's missing path) or the
  first node (EA's full walk, no answer). The real node is passed because EA's found path compares it with the first node.
- Index: key -> node, open addressing, 12 B a slot, load at most 3/4, cap 32 MB (over the cap: walk until the list
  changes). Built from the loaded tables in memory, in 2 ms steps on lookup threads: no disk reads, no file hashing.
- Staleness: Detours on the three list changers bump a generation; first/last node fingerprints; Try SRW locks (a
  lookup never waits); the table layout (bucket count, end offsets) is checked before each build step.
- Self-check: the first 256 answers after each build and 1 in 64 later ones are compared with an EA-rule walk; a
  difference turns the index off for the session (logged once).
- Threads: called on the sim thread and the UI thread [inferred, I-547-8]; no concurrent list change in practice
  [inferred, I-547-7], still guarded.
- Stand-downs (logged): GetString entry or walk bytes changed by another module; `Mods\Packages\CCStringMerge_Strings.package`
  present; S3PP `LocalizedStringCache` (entry detour) or `StblBucketLayoutTuning` (layout) on [V-547-9].
- Log lines: `[StringTableIndex] On at ...`, `Index built (#n): N strings from T tables, K KB, M ms in S steps` (first 8,
  then every 100th), `Off: hits, missing, game walks, checked (differed), builds, list changes, over cap, last build`.

## 4. Proof

| Check | Result |
|---|---|
| Host test `tools/string_table_index_test/run.ps1` (fork): first non-empty wins, empty-text skip, missing, rebuild on change, unhooked list change, verification catch, cap, layout change, 3,000 tables vs walk | all passed; 3,000 tables = 79,291 strings, 1,536 KB, built in 23-36 ms over 12-18 lookups; 0.04 us vs ~24 us per lookup on fake data |
| Signatures, Steam 1.67 and retail 1.67 exe | each of 5 unique in both [verified] |
| In game ON vs OFF (queue `stbl-index-correct-1120`) | see §5 |

## 5. In-game A/B

Queue `tools/harness/queues/stbl-index-correct-1120.json` (ABBA, Baseline-BP, user CC + `cc-2k-v2`, rc-1119b ts3native
pair, releaseDefaults, S3PP `StblBucketLayoutTuning` and `LocalizedStringCache` off in all arms, Bin state apex with the
fork asi). Repro `stbl-index-check`: ts3agent `label_check {bench: 5}` before and after a CAS + Buy browse. Pass: the
`label_check` hash and counts equal in all jobs; ON logs `On at` + `Index built` and never `differed` > 0; no ScriptError.
Timing: `bench_us` / `bench_miss_us` (light tools; a quiet machine is needed for a timing claim).

**PASS, 2026-11-20 10:32-11:31 PHT** (`docs/data/results/2026-11-20-queue-stbl-index-correct-1120.md`; the first attempt
failed at once: `label_check` with `bench: 20` took longer than the 8 s step timeout, so the repro now has `timeoutS: 600`
and `bench: 5`). Apex asi (public build of the fork branch, sha256 `c0634bc95150f9d0...`, tracked here because
builds.json has no Apex kind), ts3agent `ts3agent.package.stbl-index-1120`.

| Job | Switch | label_check hash (917 keys, both calls) | empty / placeholder / mismatch | us per lookup | us per missing key | Apex log |
|---|---|---|---|---|---|---|
| bp-stbl-off-a | off | `35980a9322b4a02d` | 14 / 0 / 0 | 75.9, 78.9 | 145.3, 140.0 | no index line |
| bp-stbl-on-b | on | `35980a9322b4a02d` | 14 / 0 / 0 | 2.5, 2.2 | 2.2, 2.2 | `Index built (#1): 115026 strings from 4257 tables, 3072 KB, 38 ms in 16 steps` |
| bp-stbl-on-b2 | on | `35980a9322b4a02d` | 14 / 0 / 0 | 2.0, 2.1 | 1.7, 1.4 | same, 33 ms in 14 steps |
| bp-stbl-off-a2 | off | `35980a9322b4a02d` | 14 / 0 / 0 | 71.1, 68.3 | 131.7, 130.5 | no index line |

[verified, measured] Same texts in all 8 checks; no `Index off` line; 0 ScriptErrors (`scriptErrorsMax: 0`); Bin
restored by sha256. Per lookup about 30x faster, per missing key about 60-90x (7,170 timed lookups per call; noisy
machine allowed, other CPU about 4%). Not measured: pie menu open time (a quiet-machine timing queue is still to write).
The ON job's Apex log rotated (`ApexRadiance_LOG.old.txt`, 1.4 MB): the per-job keep holds only the tail, so the On /
Index built lines were read from the rotated file.

Upstream PR: https://github.com/loinyx/Sims3-ApexRadiance/pull/140 (draft, fork branch `feature/string-table-index`,
commit `48e9aba8`).

## 6. Harness and agent (this branch)

- `tools/harness/queue.ts`: job key `apexToml` (any `ApexRadiance.toml` keys for a job, put back after it; tools/harness
  change, merges between game runs).
- `mods/ts3agent` `label_check`: optional `bench` (rounds) times `GetLocalizedString` over the checked keys plus as many
  missing keys (`bench_ms`, `bench_n`, `bench_us`, `bench_miss_us`). Build `ts3agent.package.stbl-index-1120`.
- `tools/harness/repros.json`: `stbl-index-check`.

## 7. Open

- EA 1.69 signatures (needs the decrypted exe on the EA laptop).
- Users with S3PP's string features on get no gain (stand-down). Coexisting with `StblBucketLayoutTuning` would need the
  bucket count read from the probe instead of the constant; not built.
- The second walk `0x59CB00`.

## 8. EA string tables: load, priority and lookup (static RE, Steam 1.67, ledger §543)

Kept from the compat branch `feature/compat-smellymods-stringmerge-1119` (docs/304 §5a there, renumbered from 297; not merged). That branch adds the same ledger §543: whichever merges second keeps one copy.


- **Which tables load:** the table-list rebuild `0x59BDE0` asks the resource manager for every STBL key in every database (`GetKeyList 0x4B1AE0`, slot `0x20`, flag 0). Databases come in priority order, highest first, with no de-duplication. A key is kept when the top byte of its instance equals the service locale `+0x1C`, so CCStringMerge's `lang << 56` instance is the right encoding. Alternate-locale keys (`+0x24`) are appended after them [verified].
- **Loading:** `0x59B190` loads each table by key through the manager's slot `0xC`, which returns one winner per TGI: the highest-priority copy. It fills the first list node with that key; duplicate nodes stay empty and are skipped [verified].
- **When:** the rebuild runs at service start (`0x59BFF0`) and on a locale change (`0x59C430`). The change listener `0x59BFC0` puts new keys at the front of the list; when it fires at run time is [inferred].
- **Lookup:** each STBL is its own hash table (7,919 buckets, `STBL_Find 0x59AF50`) in a list at service `+0x48`; there is no merged map. `GetString_A 0x59C5B0` returns the first table in list order with a non-empty text. Cost is one hash probe per table passed, and a missing key passes all of them [verified].
- **Duplicates inside one table:** `0x59F750` keeps the existing entry, and the parser `0x59FA20` ignores the inserted flag, so the first entry wins [verified]. CCStringMerge writes each key once, so this does not come up for its tables.
- **Open:** the real Resource.cfg priority values (Mods above the game is inferred). Registration order among packages of equal priority (inferred: the file order of each Resource.cfg pattern).
