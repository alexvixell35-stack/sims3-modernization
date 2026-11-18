# 287. GC step split and mark helpers (G5) (2026-11-18)

Lane B of the sim-thread offload work (offload inventory 2026-11-18, candidate 4). Static RE plus builds; no game run.
Follows docs/281 (per-tick GC, idle gate G1, finish budget G2). Ledger §516.

Tags: [verified] = read in the bytes or disassembly of `game-copy/Bin/TS3W.exe` (Steam 1.67) or in our sources;
[inferred] = reasoning, ledger entry; [measured] = from a run named here.

## 0. Short answer

| Question | Answer |
|---|---|
| What is built | §2: `[GC] SubTimers` (default 0), counters v211 `gct*`, build `ts3native.asi.gcsub-1118` (0.36.279-gcsub1). Splits the GC step into incremental mark, stop-the-world finish mark, finalization marking, sweep, mark clear and finalizer notify. |
| What it costs | Two clock reads per timed call. The hot one is `GC_mark_some` (25 per `GC_collect_a_little_inner`), each call far longer than a clock read [inferred, I-516-1]. Off by default anyway: it retargets ten Boehm call sites. |
| Gate for the helpers | Mark (incremental + stop-the-world) >= 60% of the per-tick GC time `gcgMs` on BP and SV. Not measured yet: queue `gc-subtimers-measure-1118` is RUN FIRST. Rodney 2026-11-18: build the helpers anyway, default off, time later. |
| Household rule | Not applicable. The GC decides nothing about a Sim; marking the same objects on more threads changes no script-visible result. Timers change nothing at all. |

## 1. Where the GC step spends its calls [verified]

Call tree of EA's per-tick call `0xD819AA` (docs/281 §1-2) with the sites the timers use. Every site is an `E8 rel32`
call with cdecl stack arguments; no caller reads `ecx` / `edx` after the call (checked on the instructions after each
site). The same leaves are reached by the forced full collection `0xE4A320` and by `GC_gcollect`.

| Bucket | Site | In | Target | Notes |
|---|---|---|---|---|
| mark | `0xE49DF2` | `GC_collect_a_little_inner 0xE49DA0` | `GC_mark_some 0xE4D460` | the incremental steps, 25 per call; `GC_mark_some` is an SEH wrapper around `GC_mark_some_inner 0xE4D270` |
| stop | `0xE49E32`, `0xE49E4C` | `0xE49DA0` | `GC_stopped_mark 0xE49980` | forced finish (`GC_never_stop_func 0x5BFB90`, after 40 aborted attempts) and the abortable finish attempt |
| stop | `0xE49FF9` | `GC_try_to_collect_inner 0xE49FB0` | `0xE49980` | full (non-incremental) collection |
| finalize | `0xE49A63` | `GC_finish_collection 0xE49A50` | `GC_finalize 0xE4AE60` | includes three finalizer-reachability mark loops (`GC_mark_some` at `0xE4B002`, `0xE4B192`, `0xE4B2B2`) |
| sweep | `0xE49A69` | `0xE49A50` | `GC_start_reclaim 0xE502F0` | walks every block |
| sweep | `0xE49F81` | `0xE49F10` | `GC_reclaim_all 0xE50380` | lazy and abortable (stop function per block) |
| clear | `0xE49FDF` | `0xE49FB0` | `GC_clear_marks 0xE4C270` | cycle start, walks every block |
| run | `0xE4A05F`, `0xE4A0A6` | `GC_try_to_collect 0xE4A050` | `GC_notify_or_invoke_finalizers 0xE4B480` | only signals when finalize-on-demand is set [inferred, I-516-2] |

The set is disjoint: `GC_mark_some` at `0xE499D5` runs inside `GC_stopped_mark` and is not hooked, nor are the parents
(`0xE49F49`, `0xE49F79`, `0xE4A036`).

Outside the GC step, so not in `gcgMs` and not timed here:

- Allocation-time sweep: `GC_allocobj 0xE4A2B0` calls `GC_continue_reclaim 0xE50310` at `0xE4A2D9`. Part of the sweep
  cost is paid by script allocations. EA removed the incremental collect step from `GC_allocobj` (docs/281 §8).
- EA's finalizer drain in `MonoScriptHost_Simulate`: `mono_gc_invoke_finalizers 0xE697F0` at `0xD81A09` and `0xD81A30`.
  The second call sits next to S3SS's finalizer-throttle patch bytes (`0xD81A1D`, `0xD81A37`, docs/68), so we leave it.

Conflicts [verified]: none of the ten sites is patched by ts3native, S3SS (docs/68) or Apex. `[MonoThread] GcLock`
detours function entries (`0xE4A050` and others), not these interior call bytes. `monojit.cpp` calls `0xE50380` and
`0xE49A50` directly; those calls are not seen by the timers.

Other builds: retail 1.67 maps all sites (`tools/port-run.ts`, rows in `ts3addr-retail167.h`; `0xE49FDF` by the
block's +0x2F0 shift). EA 1.69 has no addresses (the EA exe copy is encrypted on disk); its rows are 0 and the feature
logs SKIP there with the idle gate, as before.

## 2. `[GC] SubTimers` (built, default 0, NOT RUN)

### 2.1 Switch

`[GC] SubTimers=1` (`native/ts3native/gcgate.cpp`). It needs the per-tick wrapper `GcgStep` for the whole-step time and
the thread tag, so with `IdleGate=0` it installs the idle gate open (as `IdleGate=2`, `gcgMode 0`): no GC decision
changes. Log: `OK   GC sub-timers: 10 call sites`. All ten sites are checked first; one mismatch writes nothing.

### 2.2 Counters v211 (`0x6850`-`0x688F`, reader names in `tools/probe/agent.js`)

Index `_0` = the thread is inside the per-tick call that `GcgStep` passed on; `_1` = any other caller (forced
collection, explicit `GC.Collect`, a worker). Times in us, carried without per-call rounding loss (`UsTk`).

| Counter | Meaning |
|---|---|
| `gctReady` | 1 = all ten sites retargeted |
| `gctMarkN`, `gctMarkUs_0/_1` | incremental `GC_mark_some` calls |
| `gctStopN`, `gctStopUs_0/_1` | `GC_stopped_mark` (finish attempts and full collections, with their own mark loop) |
| `gctFinN`, `gctFinUs_0/_1` | `GC_finalize` |
| `gctSweepN`, `gctSweepUs_0/_1` | `GC_start_reclaim` + `GC_reclaim_all` |
| `gctRunN`, `gctRunUs` | `GC_notify_or_invoke_finalizers` in the per-tick call |
| `gctClearUs` | `GC_clear_marks` (once per cycle) |

Per-tick shares: mark = (`gctMarkUs_0` + `gctStopUs_0`) / (`gcgMs` x 1000); finalize = `gctFinUs_0`; sweep =
`gctSweepUs_0`; other = `gcgMs` minus those (it contains `gctRunUs`, `gctClearUs`, the budget checks and cycle-start
bookkeeping).

### 2.3 Gate

Build order was fixed by Rodney on 2026-11-18 (build all offload items now, default off, time later). The measured
gate still decides whether the helpers get a timing queue: mark share >= 60% of `gcgMs` on BP and SV. Below that the
helper work stops at its correctness state and sweep or finalization is the next target instead.

### 2.4 Results

Not run. Queue `tools/harness/queues/gc-subtimers-measure-1118.json` (RUN FIRST; BP smoke 5 min, BP / SV / IP 10 min;
measurement and correctness in one: script errors 0, version, gate-open and site lines, `gcgSkipped` 0).

| World | gcgMs /s | mark | finalize | sweep | other |
|---|---|---|---|---|---|
| BP | - | - | - | - | - |
| SV | - | - | - | - | - |
| IP | - | - | - | - | - |

## 3. The mark phase in this binary [verified unless tagged]

Full local write-up (not committed): `probe-out/offload-laneB-mark-static-1118.md`.

| Item | Finding |
|---|---|
| Boehm build | libgc 6.x shape with EA changes (GetWriteWatch dirty bits, a free-bit array per block, a per-block cycle stamp). No version constant in the image. |
| Parallel mark | Compiled out. The 203 functions in `0xE48E80`-`0xE51E80` hold no `lock`, `xchg`, `cmpxchg` or `xadd`. |
| Mark bits | One bit per word at block header `+0x1C` (0x80 bytes), set by a plain load / `or` / store: 14 bytes `8B 4C 97 1C 85 C1 75 xx 0B C1 89 44 97 1C`, identical at exactly 8 sites: `0xE4C5F9`, `0xE4C797`, `0xE4C8E0` (`GC_mark_from`), `0xE4CD21`, `0xE506B9`, `0xE5085D`, `0xE509C9`, `0xE50B43` (push and mark-proc helpers). |
| Mark stack | One global stack: base `[0x1234338]`, top `[0x1234340]` (empty = base - 8), limit `[0x1234344]`, size `[0x11F3074]` entries (4096 at start, doubled only when empty). Overflow `0xE4C390`: state 5, `[0x11F307C]` = 1, drop the 512 newest entries. `GC_push_all` aborts on overflow. |
| Mark state | `[0x11F3078]`: 0 none, 1 push rescuers, 2 push uncollectable, 3 roots pushed, 4 / 5 (partially) invalid. |
| `GC_mark_from 0xE4C3E0` | cdecl `(top, base, limit)` -> new top; the caller stores `[0x1234340]`. Called from `GC_mark_some_inner 0xE4D270` at `0xE4D2B7` (states 1, 3, 4, 5) and `0xE4D33B` (state 2), and three times in `GC_finalize` (`0xE4AFD6`, `0xE4B166`, `0xE4B286`). One call scans about 4 KB. |
| Shared writes in `GC_mark_from` and its callees | The mark word (read-modify-write). `[0x11F3080]` = 1, the overflow stores, the array proc's `[0x11F307C]` = 1 (repeated stores of one value). Black-list `or`s (a lost bit only weakens a heuristic). Everything else is stack arguments, a stack-local header cache or read-only tables. |
| World stop | `GC_stop_world 0xE511A0` has one caller, `GC_stopped_mark`. The incremental slices run with other threads running; dirty bits and the stop-the-world finish cover their writes. |
| Mark procs | Six, indices from 8: `0xE50A30`, `0xE50D50`, `0xE50EB0`, `0xE69400`, `0xE694D0`, `0xE69520`; table `GC_mark_procs 0x12225E8`. |
| Fault handling | `GC_mark_some 0xE4D460` is an SEH wrapper; on an access violation its handler (`0xE4D4D4`) sets state 5, empties the stack and resets the scan pointer. It covers the calling thread only. |
| Marks final | At the top of `GC_finish_collection 0xE49A50` (notify event 3, slot `[0x1234360]`) for reachability; after `GC_finalize` for finalizer reachability. Valid until `GC_clear_marks` of the next cycle. |
| Block list | `GC_apply_to_all_blocks 0xE4B850(fn(h, data), data)`. Header (0x11C bytes): `+0` size in words, `+0xC` descriptor, `+0x10` map, `+0x14` kind, `+0x15` flags, `+0x18` cycle stamp, `+0x1C` mark bits, `+0x9C` EA free bits. |

Correction to ledger I-57-4: `0xE4C1E0` clears the EA free bit (`+0x9C`) and stamps `+0x18`; it does not clear a mark bit.

## 4. Design: mark helpers inside the step (`[GC] MarkHelpers`, default 0, not built)

### 4.1 What helpers can and cannot gain

The per-tick step is budget driven and never idles (docs/281 §2): the sim thread spends EA's budget every tick no
matter how fast marking goes. Helpers alone only finish each cycle in fewer ticks and start the next one sooner: same
sim-thread ms/s, more collections. **The gain exists only together with the idle gate** (`[GC] IdleGate=1`): a cycle
that closes in fewer ticks leaves more skipped ticks before the allocation threshold starts the next one. Second gain:
fewer aborted stop-the-world finish attempts (40-55 per cycle, docs/281 §8.7), each of which re-walks every used block
[inferred, I-516-8]. So the timing arms are `IdleGate=1` with helpers off vs on, judged by `gcgMs` + `gcgForcedMs`
per second and per cycle.

### 4.2 Options

| # | Option | Share of mark time | Risk | Verdict |
|---|---|---|---|---|
| A | The game's own `GC_mark_from` on N helper threads with private mark stacks; atomic mark-bit set patched in at the 8 sites | >= 70% [inferred, I-516-6] | high: 8 code patches in the marker, exact if the patch is right | build (P2-P4) |
| C | Helpers only pre-walk blocks (dirty-and-marked block discovery `0xE4D120`, uncollectable walk `0xE4D1F0`, `GC_clear_marks`); the sim thread pushes | 10-25% [inferred] | low: helpers only read | after A, if the timers show block walks matter |
| B | Our own mark loop | as A | highest: every interior-pointer, black-list and free-bit rule must be copied exactly, and the mark procs still need the 8 patches | rejected |

### 4.3 Option A

Patches (installed only when `MarkHelpers` > 0; each verified against the expected bytes first, one mismatch = feature
off with a SKIP line):

1. **Atomic mark bit** at the 8 sites: `F0 0F AB 77 1C` (`lock bts [edi+1Ch], esi`), `72 rr` (`jc` to the old
   already-marked target, rr = old rel8 + 1), 7-byte nop. `esi` is the word index at all 8 sites [verified]. `eax` /
   `ecx` are dead afterwards at the three `GC_mark_from` sites [verified]; at the other five this must be read before the
   build [inferred, I-516-5].
2. **Mark loop entry**: retarget `0xE4D2B7` and `0xE4D33B` to `ParMarkFrom(top, base, limit)`.
3. **Active window**: wrap the single call to `GC_collect_a_little_inner` (`0xE49F49`): wake the helpers on entry, park
   and join them on exit. Helpers therefore run only while the sim thread is inside the incremental mark loop or the
   stop-the-world finish of that call. Waking per `GC_mark_from` call is not an option: one call is ~4 KB of scanning,
   a wake-up costs more [inferred].
4. `GC_clear_marks` site `0xE49FDF` (already a timer site): drop any private entries (a cycle restart).

Run-time shape:

- N helper threads (`MarkHelpers` = 1..4), plain Win32 threads, never registered with Boehm or Mono, never calling into
  either except `GC_mark_from` on their own stack. Parked on an event outside the window; inside it they spin briefly
  for work, then yield.
- Each helper owns a private mark stack and loops `GC_mark_from(privTop, privBase, privLimit)` inside its own
  `__try / __except` for access violations. Above half full it donates its bottom half to a shared chunk pool; when
  empty it takes a chunk from the pool.
- `ParMarkFrom` on the sim thread: with helpers off, or outside the window, it is the original call. Inside the window
  it donates the bottom part of the global stack to the pool when the stack is deeper than `MarkHelpersMinSplit`
  entries, runs the original call once on what is left, and takes a chunk back when the global stack ran empty.
- **Invariant I1**: `ParMarkFrom` never returns an empty global stack while the pool or any private stack holds an
  entry or a helper is inside `GC_mark_from`. With an empty stack it waits (asks the helpers to donate, spins) until it
  gets a chunk or every helper is idle and empty. So "mark stack empty" keeps EA's meaning: closure reached. That is
  the condition `GC_mark_some_inner` uses to leave state 3 and `GC_stopped_mark` to finish.
- Leftovers at the end of a window stay in the pool and private stacks; I1 hands them back in a later call. Every path
  that continues a mark goes through `0xE49F49` [to verify, S3].
- Private stack overflow is handled by the game's own overflow routine (state 5 = rescan from the marked objects): slow
  but exact. Sizes are chosen to make it rare (64 K entries = 512 KB per helper).
- A helper access violation sets a flag; the sim thread then does what the SEH handler `0xE4D4D4` does (state 5,
  empty stack, scan pointer 0) and clears that helper's stack.

### 4.4 Why the result is exact

- No new mutator concurrency. Helpers run only while the sim thread is inside `GC_collect_a_little_inner`, the same
  window in which the serial marker runs today. What other threads may do in that window (the `[MonoThread]` worker in
  Mode 1) is unchanged, and the dirty bits plus the stop-the-world finish cover it as before [inferred, I-516-9].
- Marking is a monotone closure: with an atomic test-and-set, an object is pushed by exactly the thread that set its
  bit, so every marked object is scanned once, as in the serial order. Scan order differs; the marked set for one heap
  snapshot does not [inferred, I-516-7].
- I1 keeps the termination test exact.
- The risk that remains is a non-atomic writer of a mark word running on the sim thread while helpers are active (a
  plain `or` can undo a helper's bit and free a live object). All such writers inside the window must be among the 8
  patched sites [to verify, S1, I-516-10].

### 4.5 Self-check (`[GC] MarkHelpersVerify=N`, debug, default 0)

Every Nth cycle at notify event 3, with the world stopped: save all mark arrays, the black lists and the mark state
(`M_par`); clear, mark serially (`M_ser`); clear, mark with helpers (`M_par2`); require `M_par2 == M_ser` and
`M_ser` contained in `M_par` (floating garbage from the incremental cycle is allowed, a missing bit is not); restore.
A mismatch logs the first differing blocks, counts `g5VerifyBad` and latches the helpers off. Two extra full marks per
checked cycle (a visible hitch): correctness queues only.

### 4.6 Fallback and kill switch

- `MarkHelpers=0` (default): nothing installed, byte-identical marker.
- Run-time counter switch `g5Mode` (`--ab gcmarkhelpers`): 0 = `ParMarkFrom` calls the original (after handing back
  pool and private entries under I1), helpers parked. The atomic mark-bit patch stays in while installed, so the
  in-load off arm is "helpers off, atomic bits"; the vanilla arm is a separate load with `MarkHelpers=0`.
- Automatic fallback (counted in `g5Fallback`): helper thread creation fails, a verify mismatch, a helper access
  violation count above a small limit, a byte mismatch at install.
- Counters (reserve with `tools/registry.ts`, v212+): `g5Mode`, `g5Ready`, helper mark us, windows, chunks donated /
  taken / handed back, sim waits under I1, private overflows, helper access violations, fallbacks, verify cycles / bad.

### 4.7 Costs and neighbours

- Address space (32-bit): per helper a 512 KB private stack plus a 256 KB thread stack reservation, one 512 KB pool:
  about 2 MB for two helpers, about 3.5 MB for four. Committed on first use.
- CPU: helpers add work on other cores; this is the offload trade (accepted at up to ~2% loss if stable). They park
  outside the window.
- Apex and the bridge: Apex patches nothing in the Boehm range (docs/68); its developer Frame Profiler only reads
  `0xD819AA` / `0xE4A050`. The bridge moves rendering out of process and does not touch the script heap. Helpers
  compete with Apex's in-process threads for cores only.
- S3SS: no patch in the Boehm range except Chunky at `0xD819AA` (already refused by the idle gate).
- `[MonoThread]`: the worker is registered with Boehm and is suspended by the stop-the-world finish; the helpers are
  not registered and keep running there, which is the point. `[MonoJit]` calls `0xE50380` / `0xE49A50` directly:
  outside the window.
- Household rule: not applicable (§0).
- EA 1.69: no addresses (the data globals in `0x1220000`-`0x1234FFF` are unknown); the feature logs SKIP there until
  ported. Retail 1.67: all 8 mark-bit sites and both call sites are byte-identical at +0x2F0.

## 5. Build pieces

| Piece | Content | State |
|---|---|---|
| P1 | `[GC] SubTimers`, counters v211, queue `gc-subtimers-measure-1118` | built, NOT RUN |
| S1-S3 | Static checks before P3: every writer of a mark word in the Boehm range and whether it can run inside the window (S1); register liveness after the 5 non-`GC_mark_from` patch sites (S2); every path that continues or restarts a mark goes through `0xE49F49` / `0xE49FDF` (S3) | open |
| P2 | `gcmark.cpp`: helper pool, private stacks, chunk pool, `ParMarkFrom`, I1, selftest on a toy heap with a toy `GC_mark_from` (many threads, random graphs, result equals the serial closure). Nothing patched | open |
| P3 | The patches of §4.3 behind `MarkHelpers`, `g5Mode`, counters, fallbacks, byte checks, emulated-site selftest of the `lock bts` drop-in | open, after S1-S3 |
| P4 | `MarkHelpersVerify` (§4.5) | open |
| P5 | Queues: correctness (BP 10 min, `MarkHelpersVerify=4`, switch read-back, script errors 0, `g5VerifyBad` 0, save-hash guard) and timing (`IdleGate=1`, in-load ABBA `--ab gcmarkhelpers`, plus a vanilla-marker load; needs quiet machine). Retail port; EA SKIP | open |

Timer notes: the `gct*` counts and times are plain adds; a collection on another thread at the same moment can lose
an update (measurement only). `SubTimers=1` with `IdleGate=0` runs the idle-gate wrapper with the gate open; compare
`gcgCycles` per minute of the smoke with the 11-17 `IdleGate=2` runs once to confirm the pacing is unchanged.
