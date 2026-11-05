# Three optional memory fixes: 30- and 60-minute runs, old save (2026-11-04 / 05)

Hardware: AMD Ryzen 9 7945HX 16C/32T, 32 GB, RTX 4070 Laptop 8 GB (driver 32.0.16.2101) + Radeon 610M, Windows 11
Game: Steam TS3W.exe (sha256 b78a3d535535...), LAA on, DXVK, S3SS, S3PP, Apex Radiance 2.18.3
Build: ts3native 0.36.168 (origin/main cac2729 + the decompress repeat census), release defaults + `[EaCrashFixes] GeomBoneIndex=1`
Save: the contributor's old save ("1111 kit5"), autopilot profile, `-disablestoryprogression`, seed 12345
Fixes (all optional, off by default): `cache-overflow-budget` (GraphicsRules.sgr CacheOverflowBudget 64 MB), `dxvk-texture-memory`
(dxvk.conf `d3d9.textureMemory = 64`), `[ThreadStack] Enabled=1 ReserveKB=512`. The 8-minute A/Bs are in the fixes.json notes.

## Runs

| Job | Fixes | Content | Result | Free min (MB) | Largest free min (MB) | Frame median / p95 (ms) | Frames > 50 ms |
|---|---|---|---|---|---|---|---|
| old-base-30 | none | as-is | PASS 35.4 min | 655 | 529 | 21.47 / 65.17 | 14.92% |
| old-all3-30 | all three | as-is | PASS 35.4 min | 767 | 638 | 21.01 / 64.14 | 12.50% |
| old-base-30-nr | none | no Randomizer | CRASH 18.8 min (crash W, below) | 800 (15 min) | 638 | 19.61 / 60.03 | 8.88% |
| old-all3-30-nr | all three | no Randomizer | PASS 35.5 min | 610 | 476 | 23.05 / 66.04 | 13.03% |
| old-all3-60-s128 | all three, ReserveKB 128 | no Randomizer | CRASH 1.1 min (stack overflow) | | | | |
| old-all3-60 | all three | no Randomizer | PASS 65.6 min | 344 | 208 | 22.41 / 64.56 | 12.39% |

- The only same-content pair is the first two rows: the fixes gave +112 MB free address space at the low point and fewer slow frames.
- The no-Randomizer base crashed (crash W, not related to the fixes), so the no-Randomizer rows have no pair. The Randomizer's event
  dialogs stopped two autopilot runs; `content: "no-randomizer"` moves its packages out for a job.
- ThreadStack 512: 64 exe threads capped (63 file watchers), stack reservations 212-220 -> 164-173 MB in every run.
- ThreadStack 128: `STACK_OVERFLOW at 0x00f7ee87` (chkstk) 1.1 min in. A file-watcher path uses ~66 KB and then a 64 KB stack
  buffer. 512 stays the documented value; the ini comment says so.

## Growth over 60 minutes (old-all3-60, `diag: mem`, t = 300 s -> 3900 s)

| Field | 300 s | 3900 s | Change |
|---|---|---|---|
| priv | 2192 | 2949 | +757 |
| rest (private not in another field) | 585 | 908 | +323 |
| eaRes (EA heap cores) | 796 | 1048 | +252 |
| gcHeap | 368 | 448 | +80 |
| miCom | 153 | 212 | +59 |
| eaDir (EA direct blocks) | 278 | 320 | +42 |
| rcN (ResourceCache entries) | 120,758 | 347,781 | +227,023 |

- Free space stays mostly one block (largest 224 of 344 MB at 60 min), so the loss is growth, not scattered holes.
- Hitches do not grow: per third of the 60 min, 2225 / 2647 / 2280 hitches; AutonomyManager is ~45% of hitch time all along.
- `rcN` is not a missing erase [verified disassembly]: trim `0x732E90`, the predicate sweep `0x732B60` and remove (`0x732A00`
  with flag 0) all erase the map node (`0x732130`). So the growing entries are still referenced or never made cold [inferred].
- `rest` and Apex Radiance [measured, 2026-11-05, 4 x 30 min, same save, no fixes, no Randomizer, order off / on / on /
  off; MB per 10 min]: 5-35 min: off +19 / +26, on +53 / +62; 15-35 min: off +9 / +20, on +14 / +15. So Apex adds about
  90 MB once, in minutes 5-15, and no steady growth after. Free address space at the end: off 1001 / 948 MB, on 735 / 729.
  An ApexRadiance.toml switch bisect (20 min each) could not separate switches; FastMemory off crashed once at 7.1 min
  (null read `0x68EEF0`, after access violations inside ApexRadiance.asi).
- `eaRes` follows `rcN`, with or without Apex [measured, the same 4 runs]: `rcN` 120k -> 231k in all four; from 15 to 35 min
  +62k entries and `eaRes` +51 to +79 MB, about 1 KB per entry, while `rc` (the entries' own sizes) stays flat.
- What the growing entries are [measured, 3 x 35 min, same save, a census of the cache by type and refcount under the
  cache lock at each 5-min mem line, 7-15 ms per walk; `[MemDiag] RcCensus`, not in this PR]: about 90% of the growth is
  two types, TXTC `0x033A1435` and `0x0341ACC9`. In one run they went 39k -> 94k and 34k -> 89k in 35 min. Each is
  ~0.1 KB, in group 0, with a random-looking instance. None is cold. Most have exactly 2 references (the cache + one
  holder): 18.7k -> 50.5k and 15.5k -> 66.2k. No other type grows by more than 100.
- They are made in memory [verified]: the 351 installed packages hold 76k TXTC and 48k `0x0341ACC9` keys, and the save's
  ExportDB holds 6k / 5.7k. The sampled keys are in neither.
- Who makes them [verified disassembly]: the CAS SimBuilder part compositor `0x5E2C10`, allocation tag
  `CAS/SimBuilder/SetPartCompositor`.
  - It gives each one a random 64-bit instance (`0x5DFDB0`).
  - It keeps it in a hash map per part (the part list at SimBuilder `+0x1E0`, tag `CAS/SimBuilder/PartList`).
  - It registers it in the ResourceCache (`0x4B00C0`).
  - Removing a part releases that map (`0x5E2ED0` -> `0x515510`, which calls Release on each).
- The SimBuilders are not the holder [measured, one 35-min run, an address-space scan for SimBuilder objects at each
  census]: 4-10 live SimBuilders, holding 0-40 compositors in their parts. So the second reference is held somewhere
  else [inferred]. Next: count AddRef / Release on these two types by caller.

## Crash W dump (docs/67 §27): an out-of-bounds read, not a released pose

Apex Radiance wrote a minidump for the old-base-30-nr crash (`C0000005 at 0054E79A`, reading `C4131AE0`; same `ebx 0x1AB760` as issue #99).

- Branch (a) [verified]: the RefPtr local `[0x1AB378]` = `0xC3853C90`, and that object's `+0xC` = `0xC412F140` = `ecx`, the source pose.
- That object is alive after the release [verified]: vtable `0xFC2D30`, refcount `+4` = 1, `+8` = 42, `+0xC` = the pose.
  So `[EaCrashFixes] ThumbPose` (keep the reference until the evaluation) would not change this crash.
- The object is a pose holder [verified disassembly]: constructor `0x574750` stores the bone count at `+8` and a `count x 48`-byte
  buffer at `+0xC`. `0x5E0FD0` caches it on the model (`+0x214`) and returns it only when the count equals the model's own rig.
- The modifier `0x54E630` takes its bone indices from the destination rig `[ctx+0x40]` by name (`rootWorld`, `L_footWorld`,
  `R_footWorld`; indices 222, 223, 224 here) and reads the source pose at those indices with no size check. 222 x 48 = `0x29A0`
  is far past a 42-bone buffer (2016 bytes); it faults only when the read crosses into an unmapped page.
- Fix idea (not built): where the three branches store the source pose (`0xD5B708`, `0xD5B76C`, `0xD5B7A1`), store 0 when the
  holder's vtable is `0xFC2D30` and its `+8` differs from the destination rig's bone count (`([rig+0x30] - [rig+0x2C]) / 112`,
  as `0x575650`). The modifier already skips a null source pose (`0x54E754`), and branch (c) already stores 0 sometimes.

## Checks

- `node tools/harness/queue.ts --self-check`: ok (the `no-randomizer` set and the `match` filter are covered).
- install-fixes: `dxvk-texture-memory` install, reinstall (no change) and uninstall (restored) checked on a scratch copy of Bin.
