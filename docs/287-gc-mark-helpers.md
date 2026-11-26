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
   `ecx` are dead afterwards on both paths at all 8 sites [verified, S2, I-516-5]. S1 found two more in-window
   mark-word writers, made atomic too: `0xE4CE3D` (`GC_mark_and_push_stack`, `lock or`) and `0xE4C17E`
   (`GC_set_mark_bit`, `lock or`), see §7.
2. **Mark loop entry**: retarget `0xE4D2B7` and `0xE4D33B` to `ParMarkFrom(top, base, limit)`.
3. **Active window**: wrap the single call to `GC_collect_a_little_inner` (`0xE49F49`): wake the helpers on entry, park
   and join them on exit. Helpers therefore run only while the sim thread is inside the incremental mark loop or the
   stop-the-world finish of that call. Waking per `GC_mark_from` call is not an option: one call is ~4 KB of scanning,
   a wake-up costs more [inferred].
4. `GC_clear_marks` site `0xE49FDF` (already a timer site): drop any private entries (a cycle restart).
5. (S1 fix 3, S3 b) Overflow deferral at `GC_signal_mark_stack_overflow 0xE4C390` and a quiesce before
   `alloc_mark_stack` at `0xE4D3A3` / `0xE4D3FF` (§7).

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
  that continues a mark goes through `0xE49F49` [verified, S3, I-516-11; the new cycle's stop-the-world mark at
  `0xE49FF9` runs right after the `0xE49FDF` drop and stays serial].
- Private stack overflow: the game's routine drops 512 entries; on a helper the `0xE4C390` detour only flags it, and the
  sim thread writes state 5 + too_small (rescan from the marked objects): slow but exact. Helpers never write the mark
  state (S1: the sim thread's check-then-store at `0xE4D2F2` / `0xE4D30A` / `0xE4D404` / `0xE4D421` could overwrite it).
  Sizes make it rare (64 K entries = 512 KB per helper).
- A helper access violation drops that helper's stack and sets a flag; the sim thread writes state 5 the same way (§7:
  no global stack reset).

### 4.4 Why the result is exact

- No new mutator concurrency. Helpers run only while the sim thread is inside `GC_collect_a_little_inner`, the same
  window in which the serial marker runs today. What other threads may do in that window (the `[MonoThread]` worker in
  Mode 1) is unchanged, and the dirty bits plus the stop-the-world finish cover it as before [inferred, I-516-9].
- Marking is a monotone closure: with an atomic test-and-set, an object is pushed by exactly the thread that set its
  bit, so every marked object is scanned once, as in the serial order. Scan order differs; the marked set for one heap
  snapshot does not [inferred, I-516-7].
- I1 keeps the termination test exact.
- The risk that remains is a non-atomic writer of a mark word running on the sim thread while helpers are active (a
  plain `or` can undo a helper's bit and free a live object). S1 refuted "the 8 sites are all" (I-516-10): two more
  writers and the overflow state race; P3 fixes all three. The scan is heuristic (a pointer built through registers can
  be missed): `MarkHelpersVerify` (P4) is the backstop and runs before any timing.
- Mono's descriptor table `[0x11F4248]` can be reallocated by `0xE69720` (class init, not under the GC lock) while proc
  `0xE694D0` reads it during a mark. The race exists today with the `[MonoThread]` worker; helpers add readers, not the
  race [verified writers, race inferred, I-516-15].
- `MarkHelpers` refuses to install with `[MonoThread] Mode=1` and `GcLock=0` (I-516-14).

### 4.5 Self-check (`[GC] MarkHelpersVerify=N`, debug, default 0)

Every Nth cycle at notify event 3, with the world stopped: save all mark arrays, the black lists and the mark state
(`M_par`); clear, mark serially (`M_ser`); clear, mark with helpers (`M_par2`); require `M_par2 == M_ser` and
`M_ser` contained in `M_par` (floating garbage from the incremental cycle is allowed, a missing bit is not); restore.
A mismatch logs the first differing blocks, counts `g5VerifyBad` and latches the helpers off. Two extra full marks per
checked cycle (a visible hitch): correctness queues only. Built as §8; the check point moved from event 3 to the end of
the stopped mark (§8.1).

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
| S1-S3 | Static checks before P3: every writer of a mark word in the Boehm range and whether it can run inside the window (S1); register liveness after the 5 non-`GC_mark_from` patch sites (S2); every path that continues or restarts a mark goes through `0xE49F49` / `0xE49FDF` (S3) | done 11-18: S2 go, S1 three fixes, S3 go with conditions (§7) |
| P2 | `gcmark.cpp`: helper pool, private stacks, chunk pool, `G5ParMarkFrom`, I1, selftest on a toy heap with a toy `GC_mark_from` (many threads, random graphs, result equals the serial closure). Nothing patched | built (0.36.280-g5p2, counters v214 `g5*`, §6); library only, no caller |
| P3 | The patches of §4.3 behind `MarkHelpers`, `g5Mode`, counters, fallbacks, byte checks, emulated-site selftest of the `lock bts` drop-in | built (0.36.283-g5p3, §7), NOT RUN; queues `gc-markhelpers-correct-1118` / `gc-markhelpers-time-1118` written, not launched |

| P4 | `MarkHelpersVerify` (§4.5) | built (0.36.285-g5p4, §8), NOT RUN; queue `gc-markhelpers-verify-1118` written, not launched |
| P5 | Queues: correctness (BP 10 min, `MarkHelpersVerify=4`, switch read-back, script errors 0, `g5VerifyBad` 0, save-hash guard) and timing (`IdleGate=1`, in-load ABBA `--ab gcmarkhelpers`, plus a vanilla-marker load; needs quiet machine). Retail port; EA SKIP | open |

Timer notes: the `gct*` counts and times are plain adds; a collection on another thread at the same moment can lose
an update (measurement only). `SubTimers=1` with `IdleGate=0` runs the idle-gate wrapper with the gate open; compare
`gcgCycles` per minute of the smoke with the 11-17 `IdleGate=2` runs once to confirm the pacing is unchanged.

## 6. P2 implementation notes (`native/ts3native/gcmark.cpp`, `gcmark-test.h`)

Built 2026-11-18 as ts3native 0.36.280-g5p2 (build `ts3native.asi.gcmark-p2-1118`). Library only: no ini switch, no
caller, no thread at DLL load; nothing exists until a later piece calls `G5Init`. The asi behaves exactly as 0.36.279
in game. No game queue for this piece (nothing changes in the game). Household rule: not applicable; the GC marks
objects, it decides nothing about Sims, and the marked set must equal the serial one bit for bit (§4.4).

API (coordinator calls from one thread only, the sim thread in P3):

| Function | Does |
|---|---|
| `G5Init(n, fn, ovf, privCap = 65536, poolCap = 65536, minSplit = 64)` | `n` = 1..4 helper threads (256 KB stack reservation, `STACK_SIZE_PARAM_IS_A_RESERVATION`), parked on a manual-reset event; `fn` = the marker (`G5MarkFromFn`, cdecl `(top, base, limit)` -> new top, 8-byte entries `G5E {start, descr}`, empty = top < base); `ovf` = the game's overflow flag (`[0x11F307C]` in P3) for `g5PrivOverflow`. Pool 512 KB at init; each private stack 512 KB, allocated by its helper on its first window. Sets `g5Mode` = 1, `g5Ready` = 1. Returns the helpers started (0 = serial) |
| `G5WindowEnter()` / `G5WindowLeave()` | open: `active` = 1, wake. Close: `active` = 0, reset the event, wait until no helper is inside its loop: after Leave no helper is inside `fn` or touches a private stack. Enter does nothing with `g5Mode` = 0 |
| `G5ParMarkFrom(top, base, limit)` | in place of `GC_mark_from`. Window open and mode on: donates the bottom half of a stack deeper than `minSplit` while the pool is low, then one `fn` call. Otherwise first hands back pool entries and (window closed) parked private entries. Never lets the stack exceed `limit - 512` entries on hand-back; the rest stays outside. Empty after `fn`: I1 loop (take from the pool, else wait with `wantDonate` set until a chunk arrives or the pool is empty and no helper is busy) |
| `G5Reset()` | joins the helpers if a window is open, drops every pool and private entry and a pending fault, reopens |
| `G5TakeAv()` | returns and clears "a helper faulted"; the caller replays the SEH handler (state 5) |
| `G5OutsideN()` | pool + private entries + busy helpers (I1 instrumentation, exact when helpers are idle or joined) |
| `G5Shutdown()` | stops and frees everything (selftest; never from `DllMain`) |

Helper loop: `fn(privTop, privBase, privLimit)` inside `__try / __except (EXCEPTION_ACCESS_VIOLATION)`; donate the bottom
half above half full or when the coordinator waits; take `max(minSplit, pool / 2)` entries (at most half a private stack)
when empty; spin 64 pauses, then `SwitchToThread`. With `g5Mode` = 0 in an open window a helper hands its whole stack
back through the pool. A helper fault drops that helper's stack, sets the flag for `G5TakeAv` and counts `g5HelperAv`.
The I1 proof: a helper sets `busy` only under the pool lock, in the same step that takes entries, and clears it only with
an empty stack, so "pool empty and no helper busy" read under the lock means no entry exists outside the coordinator.

Counters v214 (`0x68A8`, after v213 `arScopeRefused` / `arPrereqRefused` at `0x68A0`, which filled the placeholder
`resV213[2]` in place, docs/288): `g5Mode`, `g5Ready`, `g5Threads`,
`g5Windows`, `g5WindowUs`, `g5HelperUs`, `g5Donated`, `g5Taken`, `g5HandBack` (chunks), `g5SimWaits`, `g5SimWaitUs`,
`g5PrivOverflow`, `g5HelperAv`, `g5Fallback`, `g5VerifyN`, `g5VerifyBad` (P4), `g5PoolHigh`, `g5Resets`, `g5Spare[6]`.

Selftest `TestGcMarkHelpers` (~2 s): toy heaps of 1e5 / 2e5 / 1e6 nodes (random out-degree 0-5, 1 in 64 nodes with
40-99 edges, scanned 16 edges per entry with the rest pushed back), toy marker with `_interlockedbittestandset`, credit
128 edges per call and the game's overflow rule (flag, drop 512 newest); the driver emulates `GC_mark_some_inner`
(roots in batches, windows of 1-20 calls, a call outside the window every third boundary, state-5 rescan after an
overflow or a fault). For N = 1..4 and three seeds it asserts: marked set equal to the serial closure; I1 at every
empty return; every reachable node scanned exactly once and pushes = pops (no entry lost or duplicated); helpers really
took work; `G5Reset` mid-mark plus a full re-mark exact; a mode-flipping thread (every ~0.5 ms) exact; a helper access
violation contained, counted and repaired by the rescan; no helper calls the marker after `G5WindowLeave` (poisoned
interval with a 1 ms sleep); tiny private stacks (16 entries) report `g5PrivOverflow` and stay exact; `G5Init(0)` creates
nothing. Mutation checks during the build: dropping the I1 loop, dropping the join, and a plain (non-atomic) mark-bit
set each fail the test.

Left for P3: the 8 `lock bts` patches, the call retargets at `0xE4D2B7` / `0xE4D33B`, the window wrapper at `0xE49F49`,
the reset at `0xE49FDF`, the SEH replay after `G5TakeAv`, `[GC] MarkHelpers` / `MarkHelpersMinSplit`, the
`--ab gcmarkhelpers` switch. The real `GC_mark_from` overflow drops the 512 newest entries of a private stack too and
sets state 5 itself; P3 must check `privCap` > 512 (default 65536).

## 7. P3 live wiring (`[GC] MarkHelpers`, `gcmark.cpp` `ApplyGcMarkHelpers`; built, NOT RUN)

Built 2026-11-18 as ts3native 0.36.283-g5p3 (build `ts3native.asi.gcmark-p3-1118`). Experimental, not released, default
`MarkHelpers=0`: no thread, no byte written, the marker is EA's. Household rule: not applicable (the GC decides nothing
about Sims; the marked set must equal the serial one, §4.4); the correctness queue keeps the standard save-hash guard.

Switches (`[GC]`): `MarkHelpers=N` (0 default, 1..4 helper threads), `MarkHelpersMinSplit` (64 entries, 8..4096: the
smallest sim-thread stack it shares). Run-time switch `g5Mode` (`--ab gcmarkhelpers`, counter `0x68A8`): 0 = serial
marker through the same (atomic) sites, no window opens. Refused (SKIP, `g5Fallback`) on EA 1.69 (no addresses), and with
`[MonoThread] Enabled=1 Mode>=1 GcLock=0` (I-516-14).

Install (`ApplyGcMarkHelpers`, after `ApplyGcIdleGate`, at DLL load before the exe entry, so no mark runs; `[0x11F3078]`
must read 0). Every check first, then threads (`G5Init`), then the mark-word writes, then the call retargets with the
window last. One mismatch: nothing written, `SKIP GC mark helpers: <what> differs at 0x...`. A failed write after that:
`FAIL ...`, `g5Mode` 0 (what was written is the serial-equivalent atomic form; no window without the last retarget).

| Site | Bytes checked | Written |
|---|---|---|
| 8 mark-bit sites (S2 table in §4.3: `0xE4C5F9` 3B, `0xE4C797` 39, `0xE4C8E0` 39, `0xE4CD21` C4, `0xE506B9` / `0xE5085D` / `0xE509C9` / `0xE50B43` 30) | `8B 4C 97 1C 85 C1 75 rr 0B C1 89 44 97 1C` with each site's rr | `F0 0F AB 77 1C 72 rr+1` + 7 x `90` |
| `0xE4CE3D` `GC_mark_and_push_stack` | `B8 01 00 00 00 D3 E0 09 44 96 1C` | `31 C0 40 D3 E0 F0 09 44 96 1C 90` |
| `0xE4C17E` `GC_set_mark_bit` | `5E 09 10 C3 CC` | `5E F0 09 10 C3` |
| `0xE4C390` `GC_signal_mark_stack_overflow` | first 29 bytes (`cmp [0x11F30B4],0`, `mov [0x11F3078],5`, `mov [0x11F307C],1`, `je`) built with this build's data addresses (retail differs), and the tail `0xE4C3CA` `8B 44 24 04 05 00 F0 FF FF C3` | `E9 G5OvfStub` + 2 x `90` |
| `0xE4D2B7`, `0xE4D33B` -> `GC_mark_from 0xE4C3E0` | `E8` to the target | `G5ParMarkFrom` |
| `0xE4D3A3` (mark done), `0xE4D3FF` (state 5) -> `alloc_mark_stack 0xE4C950` (size in `edi`) | same | naked `G5GrowDone` / `G5GrowInvalid`: `pushad`, quiesce, `popad`, `jmp` original |
| `0xE49FDF` -> `GC_clear_marks 0xE4C270` or `[GC] SubTimers`' `GctClear` | same (either target) | `G5Clear`: `G5Reset`, then the previous target |
| `0xE49F49` -> `GC_collect_a_little_inner 0xE49DA0` (cdecl `n`, `stop`) | same | `G5Little`: window open while a mark runs (`[0x11F3078]` != 0) |

Run time:
- `G5OvfStub` (overflow routine): on a helper thread (`G5HelperOverflow` matches its thread id) it sets that helper's
  flag and returns `[esp+4] - 0x1000` like EA's code, writing no global; on any other thread it replays `cmp
  [GC_print_stats],0` and jumps to `0xE4C397` (EA's code, flags intact).
- `G5ApplyLk` (sim thread): at every `G5ParMarkFrom` return (after its last idle check) and after each window join, any
  helper overflow flag or fault becomes `state = 5` (+ `too_small = 1` for an overflow), `g5StateApply`++. So a mark can
  end only after every flag raised before it was applied. A flag found with the state already 0 is never applied (it
  would fake a running mark): `g5Anomaly`++ and a FAIL line; it must stay 0 (I1 says it cannot happen).
- Helper access violation: as the brief, the helper's stack is dropped and counted (`g5HelperAv`), but the sim thread
  writes state 5 only, without the SEH handler's global stack reset and scan pointer 0. Differs from the brief: state 5
  already rescans every marked object and the roots (`0xE4D3B0` path, the same as an overflow), and the replay after the
  join could hit a mark that finished inside the window [inferred, I-516-16]. After 3 faults in a session `g5Mode`
  latches 0 (logged).
- Growth wrappers: join, drop pool + private entries + pending flags, reopen. At `0xE4D3FF` (state 5, before the
  rescan starts) this is exact and counted in `g5GrowDrop`; at `0xE4D3A3` (mark done) I1 says nothing is outside, a drop
  there counts `g5Anomaly` too. Either anomaly (also a flag found after the mark ended) latches `g5Mode` 0 for the
  session. The `0xE4D3FF` call runs only when too_small is set (`je` at `0xE4D3F5`); after a fault-only state 5 nothing
  quiesces, and the pool entries left are re-pushed marked objects (duplicates, harmless).
- A `FAIL GC mark helpers: write failed` run keeps whatever atomic sites and the overflow detour were written, with
  `g5Mode` 0 and no window: it is not a clean vanilla run.

- Helpers are plain `CreateThread` threads from the asi: `DllMain` calls `DisableThreadLibraryCalls`, ts3native has no
  `DLL_THREAD_ATTACH` path, and `[MonoThread]` registers only its worker (`g_mtTable`), so helpers are never registered
  with Boehm or Mono [verified in our source; that Boehm in the exe sees no thread attach for them inferred: it is linked
  into the exe, not a DLL].

P2 defects fixed: `G5Init` keeps `fn` set even with 0 helpers and `G5Shutdown` keeps it (a retargeted site never calls
NULL; the install retargets only after `G5Init` started helpers); a `G5Init` after one with 0 helpers tries again; the
pool / event failure path frees what it made. API changes: `G5Init(n, fn, state, tooSmall, privCap, poolCap, minSplit)`
(the game's mark state and too_small instead of the overflow flag), `G5TakeAv` removed (`G5ApplyLk` applies faults),
`G5HelperOverflow()`, `G5Drop()`.

Counters (v214 spares, no layout change): `g5StateApply` (0x68F0), `g5GrowDrop` (0x68F4), `g5Anomaly` (0x68F8),
`g5Spare[3]` (0x68FC). Also used: `g5Ready` (1 = all written), `g5Threads`, `g5Windows`, `g5WindowUs`, `g5HelperUs`,
`g5Donated`, `g5Taken`, `g5HandBack`, `g5SimWaits`, `g5SimWaitUs`, `g5PrivOverflow`, `g5HelperAv`, `g5Fallback`,
`g5Resets`, `g5PoolHigh`.

Selftest `TestGcMarkHelpers` (~2 s): (1) emulated sites: each of the 8 sites' original and patched 14 bytes in an
executable buffer behind the game's pre-code (ecx, eax, edx from the bit index), on a fake header for every bit index
0..1023 with a random header, the bit already set and the bit clear: same header bytes and the same branch (taken =
already marked) for both; the patched site from 4 threads on 64 shared headers loses no bit and takes no skip; the
`0xE4CE3D` pair (header, eax and CF / PF / ZF / SF / OF equal) and the `0xE4C17E` pair (word, popped esi) the same way.
(2) the naked stubs with fake targets: the growth stubs keep `edi` / `esi` and drop (counted, anomaly only at the done
site); the overflow stub returns EA's value with `GC_print_stats` 0 and 1 on the coordinator and `arg - 0x1000` plus
the flag on a helper. (3) the P2 toy heaps for N = 1..4 now run through `G5Little` (fake `GC_collect_a_little_inner`
driving `G5ParMarkFrom`), `G5Clear` (fake `GC_clear_marks`) and the overflow rule of the detour (helpers flag, the
coordinator writes state 5): exact closure, I1, reset, mode flips, a helper fault repaired by the coordinator's state 5,
the 3-fault latch, tiny private stacks, and `g5Anomaly` 0 throughout.

Queues (written, dry-run, NOT launched): `gc-markhelpers-correct-1118` (BP smoke 5 min, BP / SV / IP 10 min,
`MarkHelpers=2 IdleGate=1 SubTimers=1`) and `gc-markhelpers-time-1118` (needs quiet machine, smoke first; in-load ABBA
`--ab gcmarkhelpers` plus a `MarkHelpers=0` vanilla-marker load). Run P4 (`MarkHelpersVerify`) on the correctness worlds
before any timing: the S1 writer scan is heuristic.

Not verified: that the GC lock is held for the whole of `0xE4A050` (S1); the helper-fault path against a real fault in
game; that nothing outside the S1 scan writes mark words (P4, §8).

## 8. P4 exactness check (`[GC] MarkHelpersVerify`, `gcmark.cpp` `G5Verify`; built, NOT RUN)

Build 0.36.285-g5p4 (`ts3native.asi.gcmark-p4-1118`). Debug switch for correctness queues, never for timing or release.

### 8.1 Switch and check point

`[GC] MarkHelpersVerify=N` (default 0; needs `MarkHelpers > 0`): every Nth finished incremental mark is checked.
With 0 no new code runs and no byte differs from P3: the check adds **no patch site**. It runs in `G5Little` (the
`0xE49F49` retarget P3 installed) right after the `GC_collect_a_little_inner` call during which "finish pending"
`[0x11F2F94]` went 0 -> 1 (store at `0xE49EEC`, reached only when `GC_stopped_mark` succeeded) [verified].

Why not notify event 3 as §4.5 planned: after the stopped mark succeeds, `0xE49F10` asks the stop function once more
(`0xE49F5A`); when the budget is spent it returns and `GC_finish_collection` (event 3) runs on a later tick
(`0xE49F60`-`0xE49F79`) [verified]. The game runs managed code and allocates in between, so at event 3 a serial mark
would find objects the cycle never had to mark. At the end of the stopped mark the marks are final and nothing ran
since. Cycles that finish in the stop-the-world mark at a cycle start (`0xE49FF9` -> `0xE4A036`) are not checked: no
helper window is open there (§7.3).

Install checks (all before the helpers install; a mismatch drops only the check, logs `SKIP GC mark verify`, counts
`g5Fallback`): the calls `0xE499D5` -> `GC_mark_some 0xE4D460`, `0xE49981` -> `GC_stop_world 0xE511A0`, `0xE49A3E` ->
`GC_start_world 0xE51290`, and the bytes of the store at `0xE49EEC`.

Builds: Steam 1.67 and retail 1.67 (14 new rows in `ts3addr-retail167.h`: code +0x2F0, data +0x1000, each read in the
retail exe [verified]; the retail asi compiles). EA 1.69: rows with 0, refused together with `MarkHelpers`.

### 8.2 What one check does

1. Skip (`g5VerifySkip`) unless the mark state is 0, the mark stack is empty and nothing is in the pool or a private
   stack.
2. Count the used blocks, allocate the copy buffer (260 bytes per block + 16 KB; freed after the check).
3. `GC_stop_world`, `GC_world_stopped [0x11F2FA8]` = 1 (as `GC_stopped_mark` does; no notify events).
4. Walk the block index as `GC_apply_to_all_blocks 0xE4B850` does (`[0x11F3054]`, 1024 entries, next at `+0x1000`, key
   at `+0x1008`, entries <= 0xFFF and free blocks skipped) [verified]; save each header's 0x80 mark bytes (`M_par`),
   both incomplete black lists (`[0x1234350]`, `[0x1234358]`, 0x2000 bytes each) and `scan_ptr`,
   `GC_objects_are_marked`, `GC_n_rescuing_pages`, `GC_mark_stack_too_small`.
5. Fresh marks: clear every block except kinds 2 / 3 (the `0xE4C0F0` rule; the `+0x18` stamp is not touched), state 5,
   `scan_ptr` 0, `GC_objects_are_marked` 0 (what `0xE4C370` + `0xE4C270` leave), zero 32 KB of stack below the frame,
   open a helper window, loop `GC_mark_some(&local)` until it returns 1, close the window -> `M_par2`.
6. The same without a window -> `M_ser` (no window: `G5ParMarkFrom` is the serial marker).
7. Compare, restore marks, black lists and the four globals, state 0, `GC_world_stopped` 0, `GC_start_world`.
8. Log after the world runs again (nothing is logged or allocated from a heap while threads are suspended).

A mark that does not finish within 60 s, or a fault in our code, gives up: stacks and pool emptied, marks restored,
`g5VerifySkip`. The mark stack may grow during a check (the game's own growth path; not undone).
Known gap: the failure paths of P3 (`g5Anomaly`, the 3-fault latch) and the game's own "ACCESS_VIOLATION in marker"
print still log inside the stopped world; with a `[MonoThread]` worker suspended inside a log call that could hang the
check [inferred]. The verify queue runs without the worker.

### 8.3 Results

| Result | Counter | Effect |
|---|---|---|
| check completed | `g5VerifyN` | log line `OK   GC mark verify n: blocks, ms, g5Mode; ... 0 words differ; ... 0 words` |
| `M_par2 != M_ser` (same snapshot, helpers vs serial) | `g5VerifyBad` | `FAIL` line + up to 8 `differ:` lines (block, header, kind, size, mark word, both values); `g5Mode` latched 0 |
| `M_ser` has a bit `M_par` lacks (kinds 2 / 3 left out) | `g5VerifySub` | `WARN` line + up to 8 `outside:` lines; no latch |
| not done | `g5VerifySkip` | log line with the reason |

`g5VerifySub` (0x68FC) and `g5VerifySkip` (0x6900) take two v214 spares (`g5Spare` at 0x6904 is the last); no layout
change. Floating garbage (a bit in `M_par` that `M_ser` lacks) is normal for an incremental cycle and not counted.

### 8.4 What each result proves

- `g5VerifyBad` 0 over many checks: marking with helpers from a stopped snapshot gives the serial marker's result
  (the atomic mark-word sites, pool, private stacks, overflow deferral). It does not cover the race between helpers and
  a running `[MonoThread]` worker, because the world is stopped.
- `g5VerifySub` 0: the incremental cycle with helper windows missed no object that is reachable right after its
  stopped finish. This is the check for the S1 scan (a mark-word writer outside the 10 atomic sites) and for the
  overflow / fault paths in real cycles [inferred, I-516-19].
- A `g5VerifySub` hit is not proof of a helper bug: the conservative scan of the sim thread's stack below `G5Little`
  and its registers differs from the stopped mark's (the stack below the check is zeroed first to keep this small), and
  with `[MonoThread]` on, the worker's stack moved between the two stops. Triage: the `outside:` lines; then the same
  job with `g5Mode` 0 from the start (the checks still run, the cycle is serial) gives the noise floor.
- With `g5Mode` 0 (`--ab` arm, a latch) checks still run; both marks are then serial.

Cost per check: one world stop, two full marks, about 260 bytes of address space per used heap block for the copies
(about 32 MB at a 500 MB heap) for the length of the check.

### 8.5 Selftest and queue

Selftest (`TestG5Verify` inside `TestGcMarkHelpers`): toy block index (two bottom indices, forwarding entry, free
block, kinds 0 / 1 / 2 / 3) and a toy `GC_mark_some` that dirties black lists and globals: block walk and block
addresses, clean check, a helper mark that loses a bit (bad + latch), the same with `g5Mode` 0 (nothing found), a cycle
that missed an object (sub, no latch), the time limit, the "mark running" skip, and the 0 -> 1 pending rule with
`MarkHelpersVerify=2` through `G5Little`; marks, black lists, globals and the world state restored bit for bit each
time.

Queue (written, dry-run, NOT launched): `gc-markhelpers-verify-1118` (correctness; BP smoke 5 min with
`MarkHelpersVerify=1`, BP / SV / IP 10 min with `MarkHelpersVerify=4`, `MarkHelpers=2 IdleGate=1 SubTimers=1`; gates
`g5VerifyN > 0`, `g5VerifyBad` 0 (early gate), `g5VerifySub` 0, `g5VerifySkip` 0, an `OK   GC mark verify` line, no
FAIL / WARN / skip line, plus the P3 gates). Order: `gc-subtimers-measure-1118`, `gc-markhelpers-correct-1118`, this
queue, then `gc-markhelpers-time-1118`.

Not built: the in-game injection of a helper fault and of tiny private stacks (I-516-13 / -16 are exercised by the toy
selftest only). Not verified: how EA keeps objects allocated between the stopped mark and the deferred finish alive
(not needed here: the check runs before that gap); nothing ran in game.
