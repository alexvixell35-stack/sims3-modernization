# 621. Mono modernization survey: what the repo did, what S3SS does, what newer runtime techniques still offer

Written 2026-11-17 (UTC). Research only: docs, capstone over the Steam 1.67 `TS3W.exe` (two functions), Mono's own libgc
source and web sources. No build, no game run, no code changed. Tags as in AGENTS.md: **[verified]** = read in the
disassembly, a source file or a doc's measured section; **[measured]** = a number from a run, with its doc;
**[inferred]** = reasoning, with the test that settles it. Rule: no ideas ported from other games' mods or engine bridges;
only general runtime techniques, each with a source.

## Summary

- **The repo has already built most of what "a newer Mono" offers.** A real JIT is out (no `mini` in the exe, docs/29), but
  the method compiler (`[MonoJit]`, templates, lazy frames, inlining) has been ON since 2026-11-15. Superinstruction
  sub-run fusion (`[MintFuse] Sub=1`) and the call/return fast path (`[MonoFast] CallPath=1`) are ON as well. A 352 MB
  initial heap and a 32 MB growth step are ON (`[GC]`). `[EqualsFast]` and the MemBalancer heap limit are built but off.
  Threaded dispatch, a two-entry `callvirt` cache, a bdwgc 8 swap, parallel mark, a precise or moving GC and heap
  decommit were each studied and rejected.
- **S3SS** (sims3fiend/Sims3SettingsSetter) does GC pacing (Finalizer Throttle, Chunky, a `GC_stop_world` early exit),
  tick pacing (Smooth Patch Original or Precise, 480 TPS by default), a 1 ms timer, mimalloc for the MSVCR80 heap, lot
  streaming and I/O patches. It has **no Mono interpreter or JIT patch** [verified: docs/03 §1.5, README 2026-11-17].
- **Three levers are still open.** All three are in the GC driver or in allocation, not in the interpreter:
  1. **GC cycle pacing.** EA's per-frame driver starts a new full cycle as soon as the last one ends. Stock Boehm
     starts one only after heap/divisor bytes of allocation.
  2. **Partial ("sticky-mark") collections.** Boehm's generational mode uses the dirty bits that are already on, but EA's
     driver clears all marks at the start of every cycle.
  3. **Non-boxing comparers** for the hot enum- and `ObjectGuid`-keyed collections, through the IL patcher.

## 1. What the repo already did

| Area | Doc | State | Measured / expected | Why stopped, if rejected |
|---|---|---|---|---|
| Swap in a period Mono JIT (`mini`) | 29 §1, §3.3 | rejected | n/a | no JIT in the exe; EA's yieldable tasks keep mint frames in the GC heap and resume with `setjmp`; hundreds of host sites read interpreter internals [verified] |
| Mono AOT (`--aot`) | 29 §1.1 | not possible | n/a | AOT needs `mini`; no AOT strings or loader [verified] |
| Own method compiler (mint opcodes to x86 templates) | 207, 223, 507, 524, 527, 537 | **ON since 2026-11-15** (`[MonoJit] Enabled=1 Direct=1 Inline=2 Wide=1`) | upper bound 119-172 ms/s at 2-3x on never-yielding methods (207 §2) [inferred]; jitMismatch 0 in verify [measured]; no ms/s A/B number yet | n/a |
| Superinstruction sub-run fusion | 139 §4 | **ON** (`[MintFuse] Sub=1`, 0.36.254) | offline -7.1% bench time; game ~2-8 ms/s [inferred] | n/a |
| Call/return fast path | 139 §5, §8 | **ON** (`[MonoFast] CallPath=1`) | 0.4-9 ns per call offline; game ~2-3 ms/s [inferred]; the older "-8 ms/s" A/B was not confirmed | n/a |
| New superinstructions for frequent pairs | 139 §3 | design only | ~1-4 ms/s [inferred] | waits for in-game pair counts (`PairCount=1`) |
| Threaded / replicated dispatch | 139 §2 | rejected | -1.7% offline, at the noise floor | modern indirect predictors already handle the single `jmp` |
| `callvirt` two-entry cache, receiver prefetch | 139 §6, 133 | rejected | offline miss rate 0.15-0.17% | the stall is the `obj->vtable` data-cache miss itself |
| `ValueType.InternalEquals` plan cache (loader lock per field) | 258 | built, **off** (`[EqualsFast]`) | ~410k lock pairs/s, ~8.7 ms/s [measured counts]; gain 6-9 ms/s [inferred]; exact on 61,159 pairs | no A/B run yet |
| Second Mono thread (`[AutShadow]`, offline host) | 118-131, 120 | built; Unseen=1 ON | autonomy ~50-110 ms/s movable (118, 129) | n/a (separate lane) |
| IL patcher at the transform | 506, 521, 526 | built (`[IlPatch]`) | correctness tool | n/a |
| Larger Boehm heap and growth step | 13, `[GC]` | **ON** (352 MB / 32 MB) | forced GCs 128 -> 1 in 6 min (BP) [measured] | n/a |
| Black-list spacing cap | 232, `[GC] BlackListCapKB` | ON | prevents an address-space runaway at start-up | n/a |
| MemBalancer square-root heap limit | 199 | built, off | address-space effect only; no Error 12 gain shown | off pending more runs |
| Heap decommit / release | 198 | rejected | RAM only; the address space does not change | wrong resource on a 32 GB machine |
| bdwgc 8.x swap, parallel mark | 542 L1/L2 | rejected | n/a | the game has libgc 6.6 plus EA edits; parallel mark and munmap are compiled out; EA code reads Boehm internals |
| Precise, generational-with-write-barrier or moving GC | 542 L3 | rejected | n/a | conservative mint frames; no write barrier anywhere |
| S3SS Chunky (no per-frame GC) | 13 Q4 | rejected | n/a | every collection would become a forced, unbounded full GC |
| S3SS Finalizer Throttle | 13 #5 | use S3SS's patch | removes the rare 200-frame drain spike | n/a |
| Per-thread cache in front of EA's allocator | 15 phase 8, `[Alloc]` | built, off | EA arena 6.9% of sim samples (docs/20) | not verified in game |

## 2. What S3SS does (confirmed)

Source: https://github.com/sims3fiend/Sims3SettingsSetter (README read 2026-11-17; every patch was read in docs/03 §1 at
commit `5eb2c65`).

- **Performance:** Mimalloc (detours the MSVCR80 `malloc` family; the Boehm heap and EA's GeneralAllocator are not
  touched, docs/13 Q4, docs/542 L4). Smooth Patch Original (shortens the `SleepEx(20)` in the sim pacing wrapper
  `0x4E1320`) or Precise (a QPC limiter that replaces `IdleSimulationCycle`; **480 TPS** by default, 60 FPS cap). Timer
  Optimization (1 ms timer resolution).
- **Experimental GC:** Finalizer Throttle, Chunky (NOPs the per-frame `GC_try_to_collect` at `0xD819AA`) and a
  `GC_stop_world` early exit.
- **Other:** lot streaming throttle, a SIMD RefPack decompressor, a 64 KB thread stack, the WorldCache uncap,
  `FILE_FLAG_RANDOM_ACCESS`, and visual patches.
- **No Mono interpreter, JIT or class-loader work** [verified]. Note: a higher TPS cap means more script ticks per
  second, so it raises interpreter and GC ms/s per wall second. It is a pacing choice, not a Mono speed-up.

## 3. New static facts behind the open GC ideas

- **Every per-frame cycle is a full collection** [verified, capstone, this note]. The per-frame `GC_try_to_collect`
  `0xE4A050` calls `GC_try_to_collect_inner` `0xE49FB0`. That function first finishes any cycle in progress (`0xE49F10`),
  then always runs `GC_promote_black_lists` `0xE4A7C0`, `GC_reclaim_all` `0xE4C370` and `GC_clear_marks` `0xE4C270`, and
  then starts `GC_stopped_mark` `0xE49980` with EA's stop function. It never calls `GC_should_collect` `0xE49950`, which
  matches docs/13 TL;DR 1: the GC is never idle.
- **Stock Boehm schedules it differently** [verified, Mono's libgc
  https://github.com/mono/mono/blob/mono-2-0/libgc/alloc.c]:
  - `GC_collect_a_little_inner` starts a cycle only through `GC_maybe_gc`, which first checks `GC_should_collect()`,
    i.e. allocation since the last GC >= `min_bytes_allocd` (scan size / divisor, halved in incremental mode).
  - In incremental mode, `GC_maybe_gc` runs **`GC_full_freq = 19` partial collections per full one**. A partial
    collection does not call `GC_clear_marks`: objects marked in an earlier cycle stay marked, and marking restarts
    from the roots and the dirty pages (here GetWriteWatch, which is already on).
  - This is the generational part of the Boehm-Demers-Shenker design ("Mostly Parallel Garbage Collection", PLDI 1991,
    https://dblp.uni-trier.de/rec/conf/pldi/BoehmDS91.html). It needs no compiler write barrier, so docs/542 L3 (which
    rejected a barrier-based generational GC) does not cover it.
- The exe does not honour `GC_FULL_FREQUENCY` or `GC_FREE_SPACE_DIVISOR`; the divisor is hard-coded to 5 at `0x1194810`
  (docs/13 Q1, docs/542 §1). Both would therefore need a patch, not an environment variable.
- **Callers of `mono_class_get_field_from_name` `0xE6F720`** [verified, direct `call` scan of the exe; the function names
  are nearest symbols, so treat them as approximate]: `ScriptHost_FindClass` region (2), the host init (2), the
  `ValueType.InternalEquals` icall region (2), `mono_object_isinst` region (1), `mono_get_method_from_token` region (2)
  and an unnamed function after `mono_metadata_type_equal` (14 sites). **`Interp_GetVirtualMethod` `0xE51FC0`** has
  exactly two call sites: the `callvirt` inline-cache miss `0xE54B0B` and `interp_jit_cache_check_1`.

## 4. Survey table

Gains are sim-thread ms/s. 1% of sim-thread samples is about 10 ms/s (docs/130). GC is 9.9% of pooled sim samples
(docs/20); `gcMark` was 2.4-6.0 ms/s in the 2026-11-04 BP runs (`docs/data/results/2026-11-04-queue-v025-slow-diag.md`).

| Idea | Already done in repo? (doc) | Expected gain | Risk | Effort | First cheap test |
|---|---|---|---|---|---|
| **G1. GC cycle pacing:** start a new per-frame cycle only when allocation since the last GC >= heap/divisor (stock Boehm `GC_should_collect`), not as soon as the last cycle ends | **No.** The problem is noted in docs/13 TL;DR 1, but no gate was built; `memexp.cpp` already retargets the call site `0xD819AA` | part of the 9.9% GC share; **~10-40 ms/s** if most cycles start before they are needed [inferred] | medium: heap exhaustion goes to EA's forced full GC; the 352 MB heap cushions it | low: one check in the existing `MxStep` retarget (skip when `!collection_in_progress && words_allocd < N`) | **zero build:** one load with `[GC] SqrtHeapLimit=2` (observe) logs `alloc` MB/s, `live`, `speed` and `gcMs` per cycle. Compare the allocation per cycle with heap/5: a ratio far below 1 means most cycles are premature |
| **G2. Partial (sticky-mark) collections:** use Boehm's own `GC_maybe_gc` path with `GC_full_freq` (19 partial per full) instead of `try_to_collect_inner`, which clears marks every cycle | **No.** docs/542 L3 rejected only barrier-based generational GC | mark work scales with new and dirty pages instead of the whole live heap: **~20-60 ms/s** upper range [inferred]; also shorter forced GCs | high: floating garbage until the next full GC (address space); EA's mark-bit edits (`clear_marks_for_block` stamps `+0x18`, docs/199) must be checked; finalizer and weak-link order | medium-high | **offline:** in `monohost` (docs/119), drive the GC through `GC_collect_a_little` (`0xE4A3F0`, via `GC_maybe_gc`) instead of `0xE4A050` during the selftest. Compare mark time per cycle, cycle count, heap growth and checksums. No game run |
| **A1. Non-boxing comparers** for hot enum- and `ObjectGuid`-keyed `Dictionary` / `List.Contains` (the modern .NET `EqualityComparer<T>.Default` specialisation, done by hand) | **No.** Partly overlaps `[EqualsFast]` (which removes the lock, not the box) | ~1% of managed samples across ~10 dictionaries (docs/74 row 105) plus `Enum.Equals` under scoring; **~5-12 ms/s**, plus fewer allocations, which makes G1 better [inferred] | low: the comparer gives the same answer as the default for integer enums and `ObjectGuid` | low-medium: `IEqualityComparer<T>` classes in ts3fixes plus `[IlPatch]` specs that pass them to the hot constructors / `Contains` sites | monohost bench row: `Dictionary<ObjectGuid,int>.TryGetValue` costs 302 ns with `DefaultComparer` (docs/207 §8). Add the same row with a custom comparer |
| `[EqualsFast]` A/B | built, off (258) | 6-9 ms/s [inferred] | low (exact on 61,159 pairs) | none: run it | `--ab equalsfast` in one load |
| Polymorphic inline cache (2-4 entries) for `callvirt` in the game | rejected on an **offline** miss rate (139 §6). docs/133 counts 2,952 multi-target edges in game | under 1 ms/s if the game miss rate stays ~0.2%; more only if it is several % | low | low-medium | count calls to `0xE51FC0` from `0xE54B0B` per second in game (one counter, existing probe) and divide by `callvirt`/s |
| Field-lookup cache: `(class, name) -> field` hash in front of `mono_class_get_field_from_name` | No | unknown; probably small unless one caller is hot | low | low | return-address histogram on `0xE6F720` from the live profiler's stacks, to find which of the ~25 sites is hot first |
| Concurrent marking on a helper thread (mostly-concurrent: mark while the sim runs, then finish with a stop-the-world rescan of dirty pages, BDS 1991) | No; docs/120 covers only a second *mutator* thread | most of the per-frame step leaves the sim thread: up to ~50-80 ms/s [inferred] | high: libgc 6.6's mark stack and allocator are not thread-safe; races with allocation | high | none cheap; do it only after G1 and G2 show how much mark time is left |
| Allocation-site reduction (fewer boxed enumerators, string concats, list copies in hot EA methods; the hand-written form of escape analysis) | partly: per-method fixes in ts3fixes (docs/74, 617) | indirect: fewer cycles once G1 paces by allocation | low | per site | GC_malloc caller histogram by managed method in one profile |
| bdwgc 8 / parallel mark / mimalloc for the Boehm heap | rejected (542 L1, L2, L4) | n/a | n/a | n/a | n/a |
| Newer Mono interpreter (2017+ `mini/interp`: tiering, inlining, cprop) or the JIT | rejected (29); our compiler covers the same ground (207/537) | n/a | n/a | n/a | n/a |

## 5. Sources

- S3SS: https://github.com/sims3fiend/Sims3SettingsSetter (README; docs/03 §1).
- Boehm GC as in Mono's tree: https://github.com/mono/mono/blob/mono-2-0/libgc/alloc.c (`GC_maybe_gc`,
  `GC_collect_a_little_inner`, `GC_full_freq`, `min_bytes_allocd`); environment variables (`GC_FULL_FREQUENCY`,
  `GC_FREE_SPACE_DIVISOR`): https://ftp.riken.jp/CTAN/graphics/asymptote/gc/doc/README.environment.
- Boehm, Demers, Shenker, "Mostly Parallel Garbage Collection", PLDI 1991:
  https://dblp.uni-trier.de/rec/conf/pldi/BoehmDS91.html (PDF: https://bitsavers.mirrorservice.org/bits/Xerox/PARC/PCR/parGC.pdf).
- MemBalancer (already used in docs/199): Kirisame et al., OOPSLA 2022.
- `EqualityComparer<T>.Default` devirtualization and boxing removal: S. Toub, "Performance Improvements in .NET Core 2.1",
  https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-core-2-1.
- Mono interpreter optimization tracking (tiering, inlining, superinstructions):
  https://github.com/dotnet/runtime/issues/47520.
