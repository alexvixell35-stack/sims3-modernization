# 624. GC cycle pacing and partial (sticky-mark) cycles: `[GC] CyclePace`, `[GC] PartialFreq`

Written 2026-11-17 (UTC). Built and selftested offline. **Not run in game.** Source: `native/ts3native/gcpace.cpp`,
selftest `native/ts3native/gcpace-test.h`. This doc follows docs/621 ideas G1 and G2 (survey, on a parallel branch) and
docs/13 TL;DR 1. Tags as in AGENTS.md: **[verified]** = read in the Ghidra decompile or the bytes of the Steam 1.67 exe;
**[inferred]** = reasoning, with the test that settles it.

## Summary

- **Every per-frame GC cycle is a full collection, and the GC never idles** [verified]. `GC_try_to_collect_inner`
  `0xE49FB0` first finishes any cycle in progress (`0xE49F10`). When that returns 1, it always starts a new full cycle and
  never calls `GC_should_collect` `0xE49950`.
- **`CyclePace=N`** (default 0) holds the next per-frame cycle until the allocation since the last cycle reaches
  scan / N. This is Boehm's own `GC_should_collect` rule with free-space divisor N. A cycle in progress keeps EA's
  per-frame steps exactly as now. GC.Collect and every forced path never come through the gate.
- **`PartialFreq=N`** (default 0) runs N-1 partial cycles between full ones. A partial cycle keeps the old mark bits and
  re-marks from the roots plus the dirty pages (GetWriteWatch). This is Boehm 6.x's own generational path, written into our
  copy of EA's driver. docs/621 named `GC_collect_a_little` `0xE4A3F0` as the path to use, but `0xE4A3F0` is
  `GC_apply_to_each_object` [verified]. EA's `GC_collect_a_little_inner` `0xE49DA0` only steps a cycle that is already in
  progress, and the exe has no `GC_maybe_gc` path that starts one. So the partial branch lives in our copy of `0xE49FB0`.
- EA's mark-bit edits (§3) do not break partial cycles, as far as static reading shows. The risks are floating garbage,
  delayed finalizers and weak links, and EA's frame-chunk mark procedure (§3.3). Run the in-game A/B with `[GcRootCheck]`.
- Both keys off: nothing is patched, so the game runs byte-identical to now. With either key on, one call site changes:
  `0xE4A089` (inside `GC_try_to_collect` `0xE4A050`, its call to `0xE49FB0`). `memexp.cpp`'s retarget of `0xD819AA` is not
  touched, so `SqrtHeapLimit` still works with these keys.
- Steam 1.67 only: the EA 1.69 table build logs SKIP. The install checks the bytes of `0xE49FB0` (0x92 bytes) and of
  `GC_initiate_gc` `0xE4C2A0`.

## 1. EA's driver (static, Steam 1.67)

| Address | Role (Boehm 6.x name) | Notes [verified] |
|---|---|---|
| `0xD819AA` | `MonoScriptHost_Simulate` -> `GC_try_to_collect(0xD717B0)` | the only caller of `0xE4A050`, and the only `push 0xD717B0` (EA's frame-budget stop function); the result is unused |
| `0xE4A050` | `GC_try_to_collect` | `GC_debugging_started` check, finalizer notify `0xE4B480`, `GC_init` if needed, **`call 0xE49FB0` at `0xE4A089`**, then the notify again on success |
| `0xE49FB0` | `GC_try_to_collect_inner` | `0xE49F10`; if that returns 1: (never_stop only: `[0x11F30A0]()`), `GC_promote_black_lists 0xE4A7C0`, `GC_invalidate_mark_state 0xE4C370` (mark state = 5), `GC_clear_marks 0xE4C270`, `[0x11947E4]=0`, `GC_is_full_gc [0x11F2FB8]=1`, `GC_stopped_mark 0xE49980`. Success: `GC_gc_no++`, `[0x11F2F94]=1`, `[0x11947E4]=-1`, `GC_finish_collection 0xE49A50`. Failure, non-incremental only: invalidate plus `GC_unpromote_black_lists 0xE4A450` |
| `0xE49F10` | finish the cycle in progress (EA) | returns 0 if `GC_dont_gc`. While `GC_collection_in_progress` (`[0x11F3078] != 0`), it calls `GC_collect_a_little_inner(1)` `0xE49DA0` until the stop function fires (then returns 0). If `[0x11F2F94]` is set, it runs the deferred `GC_finish_collection` (when `[0x11947E4] != -1`) and `GC_reclaim_all(stop, 0)` `0xE50380`, then returns 1 |
| `0xE49DA0` | `GC_collect_a_little_inner` | EA's version only steps a cycle in progress ("Bruce says: we shouldn't get here" otherwise). Stock Boehm's `else GC_maybe_gc()` is gone |
| `0xE49950` | `GC_should_collect` | `GC_adj_words_allocd() 0xE49900 >= min_words_allocd() 0xE498B0 \|\| GC_heapsize [0x12225A0] >= GC_collect_at_heapsize [0x1194818]`. Callers: `0xE493B7`, `GC_collect_or_expand 0xE4A16D` only |
| `0xE498B0` | `min_words_allocd` | `((large_free>>2) - large_free + heapsize + 20000 + GC_root_size [0x11F3088]) >> 2`, divided by `GC_free_space_divisor [0x1194810]` (5), or by 2x that when `GC_incremental [0x11F2F9C]` and `GC_time_limit [0x1194814] != 999999`. `large_free` = `[0x12225B4]` |
| `0xE49900` | `GC_adj_words_allocd` | words allocated since the last finish (`GC_words_allocd [0x12225C4]`), less explicit frees, plus the finalized and wasted words, as in Boehm |
| `0xE49A50` | `GC_finish_collection` | notify event 3, finalize, start the reclaim, `GC_need_full_gc [0x11F2FA4]`, `words_allocd_before_gc [0x12225C0] += words_allocd`, then the counters are reset |
| `0xE4C2A0` | `GC_initiate_gc` | `GC_read_dirty` when `GC_dirty_maintained [0x11F40F0]`; mark state `MS_NONE (0) -> MS_PUSH_RESCUERS (1)`; `MS_INVALID (5)` stays; any other state -> "unexpected state" |

Boehm reference: Mono's libgc in https://github.com/mono/mono/blob/mono-2-0/libgc/alloc.c (`GC_should_collect`,
`min_words_allocd`, `GC_maybe_gc`, `GC_full_freq = 19`, `GC_try_to_collect_inner`) and mark.c (`GC_initiate_gc`,
`MS_PUSH_RESCUERS`). bdwgc (https://github.com/ivmai/bdwgc, alloc.c) keeps the same scheme, counted in bytes.

## 2. What the keys change

`GpInner` replaces the call at `0xE4A089`. A call whose stop function is not `0xD717B0` goes straight to `0xE49FB0`. With
both keys at 0 at run time, every call does. Otherwise `GpRun` runs EA's `0xE49FB0` body call for call (the selftest checks
the order), with these changes:

1. **CyclePace=N**, applied after `0xE49F10` returned 1, so nothing is in progress and the last cycle has finished and been
   swept. A new cycle starts only when `GC_adj_words_allocd() >= scan_words / N` or `heapsize >= collect_at_heapsize`.
   Otherwise the call returns 0 and is counted as `skipped`. The steps, the finish and the reclaim of a running cycle are
   never skipped. N=10 equals stock incremental Boehm on this exe (divisor 5, halved). N=5 is the non-incremental rule.
2. **PartialFreq=N**: the cycle is full when N-1 partials have run since the last full cycle, when the heap has grown since
   then, when the mark state is not `MS_NONE`, when it is the first cycle, or when the key was off since the last call.
   Otherwise it is partial: no promote, invalidate or `clear_marks`, and `GC_is_full_gc = 0`. `GC_initiate_gc` then
   moves `MS_NONE -> MS_PUSH_RESCUERS`, so the marker rescans the marked objects on dirty pages, then the roots, and marks
   whatever new it reaches. `GC_need_full_gc` is not used: EA's `0xE49DA0` clears `GC_is_full_gc` before every incremental
   finish, so `GC_used_heap_size_after_full` is stale. The heap-growth rule replaces it.

Never paced: `GC_gcollect 0xE4A0B0` (GC.Collect, `mono_gc_collect`) calls `0xE49FB0` at `0xE4A0E8`.
`GC_collect_or_expand 0xE4A120` calls it at `0xE4A17B` / `0xE4A24E`, and through `0xE4A320` at `0xE4A32E`.
`GC_enable_incremental` calls it at `0xE4E425` / `0xE4E4A3`. None of these sites is patched.

## 3. EA's mark-bit edits and partial cycles

| Edit [verified] | Effect on a partial cycle [inferred] |
|---|---|
| `GC_clear_marks` per block (`0xE4C0F0`) stamps header `+0x18 = GC_gc_no` and clears `+0x1C` (not kinds 2/3) | Partial cycles skip it. The stamp only matters as `== GC_gc_no` in the reclaim (below). Stamps written at the cycle start are always below the post-mark `gc_no`, so skipping them changes nothing |
| alloc fast paths (`0xE4C1E0`) clear the object's free bit `+0x9C` and stamp `+0x18 = GC_gc_no` | unchanged: an object allocated between the mark and the finish keeps its block out of this reclaim, as now |
| reclaim (`0xE50070` / `0xE50160`) skips blocks with stamp `== GC_gc_no`. A small block first does `marks &= ~free_bits` | this keeps "marked => not on a free list" after every sweep, so sticky marks never keep a free-list object |
| `GC_free` sets the free bit; the marker skips free-bit objects | a `GC_free`'d object that was marked keeps its mark until its block is swept. If it is allocated again before that, the new object starts out marked. Its initialising writes dirty the page, so the next stopped mark rescans it as a rescuer. This is the same case as an old object written during incremental marking |

### 3.1 What Boehm's invariant needs here

Every marked object's children are marked, or the object sits on a page written since the last `GC_read_dirty`. Mint frames
and every managed store go through ordinary memory writes into the GC heap, and GetWriteWatch also sees writes from the
kernel. EA's incremental cycles already depend on the same dirty bits at their final stopped mark.

### 3.2 Delays (behaviour, not memory safety)

An object that dies after it was marked stays until the next full cycle. Its finalizer and its weak links wait up to N-1
cycles. Live-byte readers (`SqrtHeapLimit` live, TypeGrowth) overcount on partial cycles.

### 3.3 Open risk

EA's frame-chunk mark procedure `0xE69520` pushes `[chunk+8, top)`, and `top` lives in the task state, outside the chunk.
A chunk whose `top` grows over words that were not written since the last cycle would not be rescanned. This looks
impossible, because mint writes each frame it pushes, but it has not been proven. The in-game A/B should run with
`[GcRootCheck] Enabled=1` (frame audit at event 3, docs/67 §22), and `PartialFreq` stays off unless that audit shows no
new misses.

## 4. Counters and log

Export `ts3native_gcpace` (`GcPaceCounters`, not versioned, append only; docs/registry.md). It holds `pace`,
`partialFreq`, `ready`, `calls`, `started`, `full`, `partial`, `skipped`, `otherGcs` (`GC_gc_no` advances outside the
per-frame step, i.e. GC.Collect and forced collections), `lastCycleUs`, `maxCycleUs`, `lastAllocKB`, `heapKB` and
`thresholdKB`. A cycle runs from one start to the next and includes its steps, its finish and its sweep.

Every `[GC] SqrtLogSec` seconds (default 10):

```
gcpace: started=T (full F, partial P) skipped=S other=O | window Ns: started= full= partial= skipped= other= msPerCycle= maxMs= MBPerCycle= heap= threshold=
```

`msPerCycle` = per-frame GC ms per closed cycle in the window, `MBPerCycle` = allocation per cycle, `heap` and
`threshold` in MB.

## 5. Offline results

- clang-cl 23 compile of `ts3native.cpp` (/W4), Steam build and the `/DTS3A_TABLE` build: no warnings from the new files.
  MSVC `build.cmd` has not been run.
- `TestGcPace` (in `selftest.cpp`; also run on its own as a clang build under Wine, logic only, no SEH) passes. It checks:
  the pass-through; EA's call order with both keys off inside `GpRun` (immediate, incremental and non-incremental failure
  paths); the threshold formula and the collect-at escape; pacing (skip, start, steps kept, finish + sweep kept, other-GC
  count, KB per cycle); the partial schedule F p p p F p; full after heap growth, after a non-NONE mark state and after a
  run-time switch-off; and both keys together.
- **monohost could not run key 2 on Linux.** With a `__writefsdword` shim, clang-cl builds `native/monohost/monohost.cpp`.
  Under Wine the host's restart-with-reservation does not place TS3W.exe at `0x400000` (it maps at `0x016F0000`:
  "FAIL: not an image at 0x400000"). So there are no mark-time, heap or checksum numbers yet.
- **What a Windows run needs** (docs/119 §8 layout): a selftest variant in monohost that (a) calls
  `GC_enable_incremental` `0xE4E450` after `mono_interp_init` (the host skips it today, docs/119 §9.3), (b) drives the
  collector each tick through `GC_try_to_collect` `0xE4A050` with a time-budget stop function instead of `GC_gcollect`,
  (c) retargets `0xE4A089` to a copy of `GpRun`, with the stop-function check pointed at the host's stop function, and
  (d) runs the M1.4 graph stress (2,000-node graph, garbage per tick, finalizers) for 300 ticks with `PartialFreq=0` and
  then `PartialFreq=4`, logging mark µs per cycle, `GC_heapsize`, the graph checksum, finalizer counts and `GC_gc_no`. The
  monohost workflow only builds (EA files stay out of CI), so this runs on a Windows machine with a game copy.

## 6. Suggested first in-game A/B

One load, Bridgeport or the usual soak world, Apex baseline. Arms are toggled through `ts3native_gcpace.pace` /
`.partialFreq` (run-time switches), or one ini per run:

```
[GC]
CyclePace=10      ; arm B (arm A: 0)
PartialFreq=0
SqrtHeapLimit=2   ; observe: gcsize lines (live, alloc, gcMs) for both arms
SqrtLogSec=10
```

Compare GC ms/s (`gcMs` and `msPerCycle` × cycles), `skipped`, `other` (forced collections must not rise), heap MB,
and the sim ms/s baseline. `PartialFreq=4` goes in a second A/B only after pacing is clean, with `[GcRootCheck] Enabled=1`.
