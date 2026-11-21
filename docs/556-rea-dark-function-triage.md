# 556. REA + Ghidra triage of un-reverse-engineered TS3W.exe code (2026-11-14)

Static only: no game run. Goal: stand up **REA** (Reverse Engineer Anything, the CLAUDE.md static-RE tool),
point it at our local Ghidra, find the parts of TS3W.exe 1.67 nobody has reverse engineered yet, scan their
decompiled pseudo-C for EA-bug shapes, and reverse engineer what ranks highest. Addresses are from our repack
copy of TS3W.exe 1.67 (byte-identical to Steam at these addresses, docs/549-550); nothing from EA is copied,
logic is in our words. Tags: **[verified]** = read in the decompiled bytes via Ghidra/REA; **[inferred]** =
reasoning (reachability / in-game effect), not checked in the game.

## 1. REA status: installed and working [verified]

- Installed `rea-agents` (CLI package `rea-agents` v6.9.0, server SDK 2.3.1) under `~/.local/opt/rea` (npm
  global prefix, no sudo). Node 24, our local toolchain.
- Wired to the **local Ghidra 12.1.4 + JDK 21** (no IDA/Hopper). `rea doctor --provider ghidra` reports
  `healthy:true` with every `ghidra-*` check green (version 12.1.4, native decompiler `linux_x86_64`, JDK 21
  accepted). The other engines (Hopper/IDA/JADX/Kuna) are unconfigured and unused.
- **Gotcha, important for reuse:** a from-scratch `rea decompile <exe>` re-imports and re-analyses the whole
  16 MB PE on every call and times out at REA's 330 s startup deadline (`REA_GHIDRA_STARTUP_TIMEOUT_MS`), and
  an 8 G heap concurrent with a cargo build gets OOM/`SIGKILL`ed. The fix is REA's own advice: analyse **once**
  into a persistent Ghidra project and reuse it via `REA_GHIDRA_PROJECT_PATH`. With the project pre-built,
  `rea decompile`/`xrefs`/`function` run in seconds (`import_mode: existing-project-private-copy-v1`), read-only.
- Reusable env + wrappers: `tools/rea/rea-env.sh` (paths, heap, snapshot), `tools/rea/scan-pseudoc.py` (pattern
  scanner), `tools/rea/coverage-diff.py`, `tools/ghidra/run-headless-linux.sh` and `tools/ghidra/DecompileExport.java`
  (the Windows `run-headless.sh` is cygwin-pathed; these are the Linux equivalents).

**Persistent project build (RAM note):** `analyzeHeadless` import+analyse of the exe into `game-copy/ghidra/TS3`
needs ~8 G heap one-time (the project's old note, docs/04, used 16 G and still OOM'd some analyzers); it ran
`nice -n 10`, solo, with no game test active (~9 min). All later decompiles reuse it read-only at `-Xmx3G`,
decompiler parallelism 1 (one JVM, sequential), within the steady RAM cap.

## 2. Coverage: what has no notes [verified]

Function inventory = union of `tools/ghidra/symbols/ts3w-1.67.{symbols,mainthread}.csv` = **12,977 entries**.
"Covered" = a doc/ or native/ mention within 48 bytes of the entry (21,363 distinct in-range addresses grepped
from `docs/` + `native/`). `tools/rea/coverage-diff.py` output:

| 1 MB region | funcs | dark (no note) | dark % | character |
|---|---|---|---|---|
| 0x400000 | 1234 | 740 | 59% | CRT, text/codec, early engine |
| 0x500000 | 1607 | 907 | 56% | UI/EAText, containers |
| 0x600000 | 2501 | 1413 | 56% | math/collision/containers |
| 0x700000 | 2179 | 504 | **23%** | routing/sim (heavily RE'd) |
| 0x800000 | 384 | 150 | 39% | routing tail, scene |
| 0x900000 | 679 | 349 | 51% | SACS/CAS |
| 0xA00000 | 1054 | 811 | **76%** | online/Exchange, web, net |
| 0xB00000 | 598 | 489 | **81%** | online/serialization |
| 0xC00000 | 1161 | 832 | 71% | threads, store, misc |
| 0xD00000 | 308 | 159 | 51% | DBPF/resource tail |
| 0xE00000 | 976 | 116 | **11%** | IC/mono glue (heavily RE'd) |
| ≥0xF00000 | 196 | 196 | 100% | repack/overlay, non-original |
| **total** | **12,977** | **6,766** | **52%** | |

So **~52% of known function entries have no notes**. The dark-dense, never-touched subsystems are the online
stack (0xA00000-0xB00000, Exchange/Simport/web, 76-81% dark) and the 0x600000 math/container band; routing
(0x700000) and mono glue (0xE00000) are the well-covered islands. The ≥0xF00000 band is repack overlay, not
original EA code (skip).

## 3. Automated scan of the dark code [verified method]

Decompiled the **800 largest dark main-thread functions** (seed = dark ∩ main-thread, by size) to pseudo-C
with `DecompileExport.java`, then ran `tools/rea/scan-pseudoc.py` (patterns adapted to Ghidra output:
unsigned-subtraction loop bound, null-after-alloc, file-data array index, lock/unlock imbalance, endless loop,
free-then-use, double free, size-overflow, float-equality loop; weighted by crash/corruption prior, dark funcs
ranked first). 16 functions scored ≥ 6. **Every high-rank hit was then read by hand** — decompiler idioms make
these patterns FP-heavy (Ghidra renders ordinary loops as `do{}while(true)` with an internal `break`).

## 4. Findings (top hits reverse-engineered) [verified]

| addr | what it is (RE'd) | scanner flag | verdict |
|---|---|---|---|
| **0x612BE0** | Hoare partition of 16-byte records on a 48-bit key; only caller **0x612E30** is a median-of-three, depth-limited **introsort** (`0x6123A0` m3 pivot, `0x612CA0` insertion sort at ≤28 elems). | LOOPSUB+ENDLESS | **correct.** Pivot is always one of three in-range elements, so the unbounded scans terminate in-bounds; the depth limit caps worst-case recursion. FP. |
| **0x4FDF70** | UTF-8 → UTF-16 decoder (8 callers); length table at `0x114E338`, overlong/range tables `0xFB9898/98AC/98C0`. | LOOPSUB | **correct.** Lead-byte length checked `< remaining`, continuation bytes range-checked `0x80-0xBF`, output write guarded by capacity. FP (`uVar6≥2`). |
| **0x497030** | Glyph coverage-bitmap **rank/popcount** (2-bits-per-nibble LUT `0xE994`); font char→index. | IDXFILE | **correct.** Row index ≤ 0x1F within a 256-char block. FP. |
| **0xA84EB0** | Merges 4 two-bit influence fields of a 0x28-byte grid cell (terrain/tile blend) into ≤4 output structs. | ENDLESS+IDXFILE | **correct.** `local_10[4]` stack array; distinct-count maxes at 4 with the last write `local_10[3]`. Bounded. FP. |
| **0xB79F90** | Bounded container iterator (advance to first non-empty, limit `param_1[8]`). | IDXFILE | **correct.** FP. |
| **0xA34A00 / 0xA334D0** | Online **Simport** "CrossroadsPostPassportPostThread": builds the Exchange passport URL. `0xA34A00` copies a passport/Sim-name string (`obj+0x18`) into a **1020-byte stack buffer** (`local_400`), replacing `'|'`→`'&'` (URL-param sanitize), **with no length bound**. | IDXFILE | **real unbounded stack copy [verified]**; overflow [inferred] needs a source string > 1019 bytes on the online Simport "host/send Sim" path — a worker thread, **not** the sim thread, and EA's Exchange servers are largely defunct. **Low player impact; no fix built.** |

The 0xB26440 / 0x6D3720 / 0x5FFF90 "endless" hits and the 0x9228A0 / 0x922250 float-loop hits all have internal
`break`/`return` and signed, in-bounds bounds — FPs.

**Net:** the scanned slice of dark code is almost entirely correct EA library code (introsort, UTF-8 decode,
font rank, container iterators, blend merge). The only genuine defect found is the **0xA34A00 unbounded stack
copy on the dead online Simport path** — real but low-impact, off the sim thread, so it stays a candidate (list,
no native hook): a safe fix would be a bounded copy (`local_400` is 1020 bytes) but it is not worth a ts3native
hook given reachability. Household rule not engaged (online NPC data, no sim behaviour change).

## 5. cwe_checker (fkie-cad) add-on

Tried to build `cwe_checker` (Ghidra-based CWE scanner) from source with our Ghidra 12.1.4. It builds with cargo
but this machine has **no gcc runtime** (the `gcc-16` dir ships no `crtbegin`/`libgcc`), so the default Rust
link fails, and a clang + compiler-rt/libunwind link still fails on the proc-macro build scripts (they need full
host linking). **cwe_checker does not build in this gcc-less environment** — a host toolchain gap, not a PE
problem. The exact clone/build/run recipe is saved in `tools/rea/cwe-checker-run.sh` (not vendored: GPL-3.0,
heavy Rust build) to rerun once a C toolchain is present. REA + Ghidra + our scanner carried the triage instead.

## 6. Scripts (our code, saved)

- `tools/rea/rea-env.sh` — env + wrapper for `rea` against the persistent project.
- `tools/rea/scan-pseudoc.py` — pseudo-C pattern scanner (this doc's §3).
- `tools/rea/coverage-diff.py` — function-inventory vs covered-address diff (§2).
- `tools/rea/cwe-checker-run.sh` — cwe_checker build/run recipe (§5).
- `tools/ghidra/DecompileExport.java` — bulk/seeded pseudo-C export from the project.
- `tools/ghidra/run-headless-linux.sh` — Linux `analyzeHeadless` runner (project build + read-only scripts).

Decompiled C, the Ghidra project and the REA snapshot stay under `game-copy/` (gitignored); no EA bytes committed.
