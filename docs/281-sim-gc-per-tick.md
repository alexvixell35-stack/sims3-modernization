# 281. Script GC on every sim tick: caller, cost, what is left (2026-11-17)

Static RE plus existing run data; no game run. Trigger: Apex Radiance's camera-pan profile (Steam 1.67.2, Apex 4.0.x,
S3SS Smooth Patch, WinterRoadsFix, Tiny UI Fix, ts3native; SV save, about 7 min) timed the sim thread's
`GC_try_to_collect 0xE4A050` at 41,476 ms over 25,188 calls (1.65 ms a call, max 62.7 ms), plus two services at
2.3 s (`0x7F1530`) and 0.96 s (`0x869000`) over the same 25,188 calls. Ledger §499.

Tags: [verified] = read in the bytes or decompile of `game-copy/Bin/TS3W.exe` or our sources; [measured] = from a run
log named here; [inferred] = reasoning, ledger entry.

## 0. Short answer

| Question | Answer |
|---|---|
| Who calls it every tick | **EA.** One direct call site, `0xD819AA` in `MonoScriptHost_Simulate 0xD81840`, once per sim iteration; no other call, jump or absolute pointer to `0xE4A050` in the exe [verified: byte scan of `.text` for `E8`/`E9` rel32 targets and of the whole image for the dword]. |
| S3SS | Its only patch on this path is *Chunky* (`GCTryToCollect`, NOPs `0xD819AA`), not enabled in our setup (docs/68) and not in Apex's run (the call still ran 25,188 times). Smooth Patch is a frame limiter. Its finalizer throttle edits `0xD81A1D`/`0xD81A37`, not this call [verified docs/68, docs/03]. |
| ts3native | `[GC]` changes only the initial heap (352 MB), the growth step (32 MB) and, if set, the budget ceiling (`BudgetCeiling=0` = unchanged). `[MonoThread] GcLock=1` (on in release) detours `0xE4A050`; the lock is taken only while our worker runs. `[GC] SqrtHeapLimit` / `[MemDiag] TypeGrowth` retarget `0xD819AA` for timing only (off). None of them adds or removes a per-tick call [verified: `ts3native.cpp ApplyGc`, `memexp.cpp MxStep`, ini]. |
| Work or early return | **Real work, every tick.** The game's GC never idles: each call marks, sweeps or starts a new cycle until EA's stop function says the budget (500-1500, about µs) is spent (§2). 1.65 ms a call = the budget plus the stop-check granularity and cycle-start costs [inferred]. |
| Cost | Apex: ~99 ms/s at ~60 calls/s (if the run was ~420 s; the profile gives no exact length). Ours, BP 11-16: one completed cycle every ~8.4 s, ~1.0 s of GC per cycle, so ~120 ms/s [measured, §3]. Both about 10-12% of the sim thread, in line with the 9.9% (SV 17%) sample share in docs/28 N4. |
| Services | `0x7F1530` = `UIService_SimUpdate` (UTFWin message pump, layout, paint on the sim thread), ~5.5 ms/s. `0x869000` = audio service sim slot, ~2.3 ms/s (docs/35: ~4 ms/s, not worth an offload). §4. |
| Best next step | **Idle gate** (G1): skip the per-tick call while no cycle is running, no sweep is pending and little has been allocated since the last cycle. Estimated **30-80 ms/s** on BP [inferred], ~1 h build + smoke + two 10-min A/B jobs. §6. |

## 1. The call site [verified]

`0xD819A0`: `push 0xD717B0` (EA stop function), `mov [0x11EE524], eax` (budget start time), `call 0xE4A050`, then
`mov eax, [0x11F2F98]` / `add esp, 4` / `cmp eax, [0x11947E8]` (EA's budget adaptation). **The return value is not
used.** `0xE4A050` itself: optional hook `[0x11F309C]`, `GC_notify_or_invoke_finalizers 0xE4B480`, lazy `GC_init`,
the compiled-out lock call `0x5BFB90`, then `GC_try_to_collect_inner 0xE49FB0`.

Direct callers inside Boehm: `0xE49FB0` from `0xE4A089`, `0xE4A0E8`, `0xE4A17B`, `0xE4A24E`, `0xE4A32E`, `0xE4E425`,
`0xE4E4A3` (full/forced paths, init, `GC_enable_incremental`); `0xE49F10` from `0xE49FB6` and `0xE4A33B`.

## 2. What one call does [verified decompile, docs/13 Q3]

`0xE49F10`: while a mark is in progress (`GC_collection_in_progress 0xE4C060` = `[0x11F3078] != 0`), check the stop
function, then `GC_collect_a_little_inner 0xE49DA0` (25 `GC_mark_some` steps, then a stop-the-world finish attempt that
can abort, 40 tries at most before a forced finish). Then, if a finish is pending (`[0x11F2F94]`), `GC_finish_collection
0xE49A50` and the abortable lazy sweep `0xE50380`. If everything is done, `0xE49FB0` **starts the next cycle at once**:
black lists, reclaim, clear all marks (`0xE4C270`, unbounded), `GC_stopped_mark 0xE49980` with the stop function. There
is no `GC_should_collect` test (`0xE49950` exists but this path does not call it). So the budget is spent on every tick
whatever the allocation rate; heap size changes only how often an allocation forces a full collection.

## 3. Cost, and why our older numbers looked small

| Source | Per cycle | Cycles | GC on sim |
|---|---|---|---|
| Apex profile, SV | - | - | 41.5 s / 25,188 calls = 1.65 ms a call; ~99 ms/s at ~60 calls/s |
| `bp-m1-gcnat` 11-16 (0.36.264-mono1, `SqrtHeapLimit=2` observe), 12-min window | 1.0 s avg over 51 logged cycles (max 1.54 s); live 90-100 MB, heap 356 MB, GC speed ~109 MB/s | 86 (`sqGcs` 268 -> 354): one per ~8.4 s | ~120 ms/s [measured: `gcsize:` lines, `-native.json` before/after] |
| `bp-cc-sqrtheap-observe` 10-26 | 0.61 s avg (19 cycles) | - | - |
| docs/04 F15, docs/542 §7.1 M1 row "mark 4.8 ms/s" | - | - | **undercount**: `tools/frida/stutter-profile.js` (`gcStoppedMark`) times only `GC_stopped_mark`, not the incremental `GC_mark_some` slices or the sweep |

Allocation in the BP run: 6-10 MB/s (`sqAllocKBs` 9598 -> 6192); forced collections 0. During one ~8 s cycle the heap
takes 50-80 MB of its ~260 MB free space. Most of the headroom the 352 MB heap bought is unused by the per-tick GC: it
starts the next cycle long before it needs to.

## 4. The two services [verified vtable bytes]

Sim slot = vtable `+0x20` (docs/36 §3.3).

| Service | Vtable | `+0x20` | What | Apex cost | Our data |
|---|---|---|---|---|---|
| UI service `0x7F14E0` | `0x101E758` | `0x7F1530` `UIService_SimUpdate` | UI clock, window manager `[0x11CD080]` `+0x24` / `+0x28`: message pump, hit tests, managed window callbacks, layout and paint into display lists, all on the sim thread | 91 µs a call, ~5.5 ms/s | docs/43, docs/47, docs/55; docs/36 C7 lists it as a main-slot candidate |
| Audio `0x868EE0` | `0x1029060` | `0x869000` | per-frame audio `Update`, command records into the triple buffer, emitter pass | 38 µs a call, ~2.3 ms/s | docs/35: ~4 ms/s, movable part 1-2 ms/s, no offload |

## 5. What we already did for GC

| Item | State | Result |
|---|---|---|
| `[GC] InitialHeapMB=352`, `GrowthStepMB=32` | on since 0.1.0 | forced collections 128 -> 1 per 6 min on BP, forced GC 13.9 -> 0.1 ms/s (docs/04 F15) |
| `[GC] BudgetCeiling` | off (0) | not tested; shifts work between ticks, does not remove it |
| `[GC] BlackListCapKB=1024` | on | start-up heap runaway fix (docs/232) |
| `[GC] SqrtHeapLimit` | off; observe runs only | sizes the heap; does not touch the per-tick work (§2) |
| `[MonoThread] GcLock` | on | thread safety for our worker, no cost change |
| Heap return (docs/198) | dropped | RAM only |
| bdwgc swap, parallel mark, precise GC (docs/542 L1-L3) | not feasible | parallel mark compiled out; mint frames conservative |

## 6. Options left

| # | Option | Gain | Risk | Effort |
|---|---|---|---|---|
| G1 | **Idle gate at `0xD819AA`**: retarget the call (the `MxStep` call-site pattern in `memexp.cpp`) to a gate that returns 0 without calling when `[0x11F3078] == 0` (no mark), `[0x11F2F94] == 0` (no finish or sweep pending) and `GC_words_allocd [0x12225C4] * 4` is below a threshold `T`. `T` from the free space after the last cycle (heap minus last live, `sqLiveKB` when the event-3 hook runs, else heap / 4) minus twice the allocation of one cycle, capped by an ini `GateMB`; `T <= 0` = vanilla. Runtime counter switch for in-load A/B | cycles every ~20-30 s instead of ~8 s at the same ~1 s per cycle: **30-80 ms/s on BP** [inferred, ledger I-499-3] | an allocation burst past the free space hits EA's forced full collection (27 ms max at 352 MB, docs/04 F15): count `forced`; finalizers and weak refs clear later (I-499-4); more garbage per sweep | ~1 h build + selftest, smoke, 2 x 10-min BP/SV A/B. **Built 2026-11-17, §8** |
| G2 | Finish-attempt waste: each aborted stop-the-world finish rescans roots and dirty pages. Read `[0x11F2FB4]` (attempts) per cycle first; if cycles often need 20-40, give the finish attempt a longer budget | unknown until measured | timing only | counter read: minutes |
| G3 | `BudgetCeiling` up | none on ms/s (same work, fewer ticks), more per-tick latency | - | - |
| G4 | Mark on a second thread while the sim runs (GetWriteWatch catches the writes, the final stop-the-world rescans dirty pages) | in theory most of the ~100 ms/s | mark stack, free lists and EA's free bits (`+0x9C`) are single-threaded; allocation during a concurrent mark has no allocate-black path [inferred]: a port, not a patch (docs/542 L1) | weeks |

**Household rule.** The GC serves every Sim alike and changes no Sim decision; G1 and G2 change only when collection
work runs and how long the pauses are. No exemption applies; `HouseholdToo` is not involved [inferred: no managed code
reads GC timing except finalizer order, I-499-4].

## 7. Open questions

- Exact length of Apex's run (calls/s): the profile gives the call count, not the time.
- Finish attempts per cycle on BP/SV/IP (G2).
- Whether skipping a call also delays `GC_notify_or_invoke_finalizers 0xE4B480` enough to matter; Simulate's own
  `mono_gc_invoke_finalizers 0xE697F0` call still runs every tick (I-499-4).

## 8. Built: `[GC] IdleGate` (2026-11-17, branch `feature/gc-idle-gate-1117`, NOT RUN)

Build `ts3native.asi.gcgate-1117` (0.36.268-gcgate1 = master-1116c + this; `gcgate.cpp`, counters v205 `gcg*` at
0x6538, `docs/registry.md`). Default **off** (`IdleGate=0`). Steam 1.67 only: the byte checks embed Steam data
addresses, so the EA build logs SKIP until ported.

### 8.1 Fields checked before use [verified, `game-copy/Bin/TS3W.exe` Steam 1.67 bytes]

| Field | Address | Evidence |
|---|---|---|
| mark state (`GC_collection_in_progress`) | `[0x11F3078]` | `0xE4C060` = `xor eax,eax; cmp [0x11F3078],eax; setne al; ret` |
| finish / sweep pending | `[0x11F2F94]` | set 1 at `0xE4A022` right after `GC_gc_no++` when a mark completes (then `GC_finish_collection 0xE49A50`); cleared at `0xE49F91` when the lazy sweep `0xE50380` returns done; `0xE49F10` returns 1 (start the next cycle) only after that |
| bytes since the last collection | `GC_words_allocd [0x12225C4]` (x4) | zeroed in `GC_finish_collection` at `0xE49AD9` after `add [0x12225C0], eax` (`words_allocd_before_gc`) |
| incremental mode | `GC_incremental [0x11F2F9C]` | `0xE4A120` / `0xE49F1C` test it; the per-tick path needs it on |
| others | `GC_heapsize [0x12225A0]`, `GC_gc_no [0x11F2F90]`, `GC_dont_gc [0x11F30A8]`, `GC_is_initialized [0x11F30D0]`, start hook `[0x11F3094]` / `[0x11F309C]` | `0xE4A1C6` (heap / free-space divisor), `0xE4A01B`, `0xE49F10`, `0xE4A064`, `0xE4A050` |
| allocation never drives the GC | `GC_collect_a_little_inner 0xE49DA0` | one caller only, `0xE49F49` inside `0xE49F10` (call scan of `.text`) |
| forced collection | `0xE4A120` -> `0xE4A1B4 call 0xD70800` = `jmp 0xE4A320` | in incremental mode an allocation that finds no free block with words allocated > 0 runs a full collection with `GC_never_stop_func 0x5BFB90` (`xor eax,eax; ret`); `0xD70800` has that one caller |
| caller after the call | `0xD819AF` | return value unused; `[0x11F2F98]` (finish attempts) drives EA's budget +/-100 and is reset there, so a skipped tick leaves it unchanged |

### 8.2 Design

- `0xD819AA` retargeted (call-site pattern, `PatchCalls`) to `GcgStep`; it chains to whatever was there (EA's `0xE4A050`,
  or `MxStep` when `[GC] SqrtHeapLimit` timing is on). `[MonoThread] GcLock`'s detour on `0xE4A050` stays in the path.
- Skip only when **all** hold: mark state 0, pending 0, GC initialized, incremental, not `dont_gc`, and
  `words_allocd * 4 < T`, `T = min(IdleGateMB, IdleGateHeapPct % of GC_heapsize) >> backoff` (defaults 64 MB, 15%: 52.8
  MB at 352 MB). A skip still runs what `0xE4A050` runs before any GC work (start hook if set,
  `GC_notify_or_invoke_finalizers 0xE4B480`), so finalizer notification per tick is vanilla (I-499-6).
- The jmp at `0xD70800` is retargeted to `GcgForced` (counts and times the forced collection, then calls on). A forced
  collection right after a skipped tick halves `T` (max 8 halvings, log line) so the gate cannot keep causing them.
- `IdleGate=1` gate on; `IdleGate=2` installed with the gate open (counters only: the off arm). Run-time switch counter
  `gcgMode` (`--ab gcidlegate`, `tools/harness/native-switches.ts`).
- Household: GC timing changes no Sim decision (§6); no Sim-scope code path exists here.
- Shortcut: `T` is a fixed share of the heap, not "free space after the last cycle minus two cycles of allocation" (§6
  G1); live bytes are not known without a heap walk. The backoff covers the case where that matters (big live set).

### 8.3 Counters (v205, 0x6538-0x6577)

`gcgMode`, `gcgReady` (bit 0 call site, bit 1 forced thunk), `gcgCalls`, `gcgSkipped`, `gcgRan`, `gcgCycles`
(collections completed inside passed calls), `gcgOutside` (completed between per-tick calls: forced, explicit
`GC.Collect`), `gcgForced`, `gcgForcedMs`, `gcgMs` (ms in passed calls), `gcgMaxUs`, `gcgThreshKB`, `gcgHeapMaxKB`
(heap high-water), `gcgBackoff`, `gcgSpare[2]`. GC ms/s = (`gcgMs` + `gcgForcedMs`) per second.

### 8.4 Queues (dry-run only)

- `tools/harness/queues/gc-idle-gate-correct-1117.json`: smoke (BP 5 min, gate on, stops the queue on fail), then BP and
  IP pairs of 10 min, gate on (`IdleGate=1`) vs off arm (`IdleGate=2`). Pass: no crash, script errors 0, version and gate
  lines, `gcgSkipped > 0` (on) / `0` (off), forced on <= off + 1, `gcgBackoff` 0, heap high-water on <= 1.10 x off.
- `tools/harness/queues/gc-idle-gate-time-1117.json` (**needs quiet machine**): BP and SV, 25 min in one load,
  `--ab gcidlegate`, 5-min slices = 10 min per arm ABBA. Read: `node tools/harness/wait-ab.ts <tag> --counters
  gcgMs,gcgForcedMs,gcgForced,gcgCycles,gcgSkipped,gcgCalls` (option added for this) and `profile-compare.mjs` for hitch
  p99 / p99.9 / max.
- Both need this branch merged first (`tools/probe/agent.js` names the v205 counters).

### 8.5 Expected gain [inferred, I-499-5]

BP 11-16: ~1.0 s of GC per cycle, one cycle per ~8.4 s, 6-10 MB/s allocated. With `T` = 52.8 MB the GC idles 5-9 s
between cycles: ~1.0 s per 13.7-17.2 s = **58-73 ms/s instead of ~120, about 45-60 ms/s less** on BP, if the per-cycle
cost stays set by the live set (more garbage per cycle adds sweep work, not mark work). Heap headroom: `T` plus the
50-80 MB allocated during one cycle is ~105-135 MB of ~260 MB free on BP. A bigger `IdleGateMB` / `IdleGateHeapPct`
gains more and leaves less headroom; tune only after the first A/B.
