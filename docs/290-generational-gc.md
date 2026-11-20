# 290. Generational GC for the embedded Boehm collector (2026-11-18)

P0a of the generational GC work: what the collector in Steam TS3W.exe 1.67 does today, what a collection costs, which
type and root information exists, and which parts of docs/542 L3 ("precise / generational / moving: not feasible")
hold. Static RE only (Capstone disassembly of `game-copy/Bin/TS3W.exe`); no game run, no code change. Ledger §520.

Tags: [verified] = read in the disassembly or image bytes; [inferred] = reasoning, ledger entry; [measured] = from a
named run; [doc] = established in an earlier doc. EA 1.69 not checked.

## 0. Short answer

| Question | Answer |
|---|---|
| Does EA's build run partial (generational) collections? | **No.** Every cycle is a full one: `GC_clear_marks 0xE4C270` has one caller, `0xE49FDF` in `GC_try_to_collect_inner 0xE49FB0`, and it runs unconditionally at every cycle start. `GC_maybe_gc` (the only place Boehm 6.6 chooses partial vs full) is not in the binary; `GC_full_freq` and the partial counter went with it [verified, §1.2]. |
| What are the dirty bits used for then? | Only to make the incremental full mark correct: each stop-the-world finish attempt re-reads GetWriteWatch and rescans marked objects on dirty pages [verified, §1.3]. |
| Is the partial-collection machinery still compiled in? | **Yes.** Mark state 1 (push marked objects on dirty pages, then roots with `all = 0`) is intact and runs today on every finish attempt: `GC_push_next_marked_dirty 0xE4D120`, `GC_page_was_dirty 0xE4F170`, `GC_push_conditional 0xE4CC00` [verified, §1.3]. |
| Does a generational collector need a software write barrier, precise scanning or moving? | No. Boehm's own design (sticky mark bits: marked = old, unmarked = allocated since the last collection) needs none of them. The page-level barrier already exists and sees every writer (mint, JIT code, EA native code, workers) [verified mechanism; completeness inferred, I-520-3]. |
| What blocks it? | Nothing structural. The work: skip three calls at cycle start for a minor cycle, a full/minor policy, and a check of EA's edits (free bits, block stamp) under sticky marks (§5). The gain depends on one unmeasured number: dirty pages per cycle (§2.3). |
| docs/542 L3 | "Generational: not feasible" is **wrong**. "Moving" stays blocked in practice. "Precise" is partly there and not needed (§4). |

## 1. Current state [verified unless tagged]

### 1.1 Names and globals

| Item | Address | Notes |
|---|---|---|
| `GC_enable_incremental` | `0xE4E450` | called by the host at `0xD7E602`; sets `GC_dirty_maintained` through `0xE4EFA0`, runs a full collection if anything was allocated, reads the dirty bits once, then sets `GC_incremental` |
| `GC_incremental` | `[0x11F2F9C]` | 0 in the image, 1 after `0xE4E4B0` |
| `GC_dirty_maintained` | `[0x11F40F0]` | readers: `GC_initiate_gc 0xE4C2A3`, `GC_push_conditional 0xE4CC07`, `GC_push_next_marked_dirty 0xE4D120` |
| `GC_dont_gc` | `[0x11F30A8]` | |
| `GC_gc_no` | `[0x11F2F90]` | +1 when a mark completes (`0xE49EE5`, `0xE4A01B`) |
| mark state | `[0x11F3078]` | 0 none, 1 push rescuers, 2 push uncollectable, 3 roots pushed, 4 / 5 invalid (docs/287 §3) |
| `GC_objects_are_marked` | `[0x11F3080]` | cleared by `GC_clear_marks` |
| `GC_is_full_gc` | `[0x11F2FB8]` | set to 1 at every cycle start (`0xE49FEF`); cleared in `GC_finish_collection` |
| `GC_need_full_gc` | `[0x11F2FA4]` | written in `GC_finish_collection` (`0xE49A86`, `0xE49AB3`); **no reader** (two references in the image) |
| `GC_used_heap_size_after_full` | `[0x11F2FAC]` | |
| `GC_n_attempts` | `[0x11F2FB4]` | aborted finish attempts of the running cycle |
| step count, attempt cap | `[0x119481C]` = 25, `[0x1194820]` = 40 | variables in EA's build; one reader each besides the cap's three |
| `GC_time_limit` | `[0x1194814]` = 5 | two references: `GC_init` and `min_words_allocd 0xE498B0` (compare with 999999). It times nothing (§2.2) |
| EA budget level | `[0x11F2F98]`, range `[0x11947EC]` = -20 .. `[0x11947E8]` = 20 | raised / lowered in `0xE49DA0` by how many attempts a cycle needed; read by the host at `0xD819B0` |
| `GC_grungy_pages` | `0x1227714`, 0x2000 bytes | 65,536 page bits, index = (address >> 12) & 0xFFFF: one bit per page up to 256 MB, pages 256 MB apart share a bit |
| `GC_written_pages` | `0x122B714` | or-ed from the grungy bits after each read (`0xE4F124`) |
| heap sections | `0x122D714` (start, bytes), count `[0x11F2FC0]` | |

### 1.2 Full versus partial

- Cycle start is only `GC_try_to_collect_inner 0xE49FB0`. After the running collection is finished (`0xE49F10`), it
  always calls: `GC_promote_black_lists 0xE4A7C0` (`0xE49FD5`), `GC_invalidate_mark_state 0xE4C370` (`0xE49FDA`),
  `GC_clear_marks 0xE4C270` (`0xE49FDF`), sets `GC_is_full_gc`, then `GC_stopped_mark 0xE49980` (`0xE49FF9`).
- `GC_clear_marks` walks every block with `0xE4C0F0`: stamp the block (`+0x18 = GC_gc_no`) and zero its 0x80 bytes of
  mark bits, except for uncollectable kinds, which keep their marks. Then mark state 5, `GC_objects_are_marked` 0.
- So no mark bit of a collectable object survives a cycle start: **not generational**.
- `GC_collect_a_little_inner 0xE49DA0` has one caller (`0xE49F49`). Its "no collection in progress" branch is an abort
  message, not `GC_maybe_gc`. `GC_should_collect 0xE49950` has two callers (`0xE493B7` block allocation,
  `0xE4A16D` `GC_collect_or_expand`); neither chooses a collection type.
- No `GC_full_freq` or partial counter: the only code that used them is gone, and `GC_need_full_gc` has no reader.
- Who starts cycles: EA's per-tick call (`0xD819AA`, docs/281), which starts the next cycle as soon as the last one
  is swept; `GC_collect_or_expand 0xE4A120` in incremental mode calls the host thunk `0xD70800` -> `0xE4A320` (start
  if none runs, then finish without a stop function); `GC_gcollect`.

### 1.3 What one cycle does with the dirty bits

1. Start: clear all marks, state 5. `GC_stopped_mark`: stop the world (`0xE511A0`), `GC_initiate_gc 0xE4C2A0` reads the
   dirty bits (`GC_gww_read_dirty 0xE4EFB0`), then `GC_mark_some` until done or until EA's stop function
   (`0xD717B0`) reports the budget spent. With state 5 and no marked objects the mark goes to state 2 (push
   uncollectable objects) and then pushes the roots with `all = 1`.
2. Budget spent: the world restarts with the mark state kept. Later ticks run `GC_collect_a_little_inner`: 25
   `GC_mark_some` steps between stop-function polls, with the world running.
3. Mark stack empty: `GC_stopped_mark` again. The mark state is 0 now, so `GC_initiate_gc` reads the dirty bits and
   selects **state 1**: `GC_push_next_marked_dirty 0xE4D120` pushes every marked object in blocks with a dirty page
   (test `0xE4F170`: no header = dirty, else the grungy bit), then `GC_push_roots 0xE4D910` with `all = 0`
   (`GC_push_conditional 0xE4CC00`: static roots page by page through the same test; non-heap pages always count as
   dirty). If the stop function fires, the attempt is aborted and counted; after more than 40 attempts the finish runs
   with `GC_never_stop_func 0x5BFB90`.
4. `GC_finish_collection 0xE49A50`: finalization, sweep start; the abortable sweep `0xE50380` follows.

Step 3 is exactly what a Boehm partial collection does at its start. The only difference between today's finish
attempt and a minor collection is that today the marks were cleared a few ticks earlier.

### 1.4 `GC_gww_read_dirty 0xE4EFB0`

- Zeroes the grungy bitmap, then for each heap section calls GetWriteWatch (import slot `0xF952D8`) with the reset
  flag and a 1,024-entry address buffer (`0x11F30E0`), repeating while the buffer comes back full. Each returned page
  sets one bit. On failure it warns (five times at most) and marks the whole section dirty.
- Sections come from `GC_win32_get_mem` (host `0xD70900`) with `MEM_WRITE_WATCH` (docs/57 §6).
- One call per `GC_initiate_gc`, so per `GC_stopped_mark`: 1 at cycle start plus 1 per finish attempt (up to 41).
- It runs after the world stop and before the first stop-function poll, so EA's budget cannot interrupt it.

### 1.5 EA edits that matter for sticky marks

| Edit | Where | Effect |
|---|---|---|
| free bits, block header `+0x9C` | set by `GC_free 0xE4BF52` and by the sweep when it builds free lists (`0xE4F440`, `0xE4F947`); cleared per allocation by `0xE4C1E0` (docs/67 §22.6) | "this slot is on a free list" |
| sweep drops marks of free slots | `0xE500D6`-`0xE500E6`: mark word &= ~free word, for each block the sweep visits | an explicitly freed object loses its mark at the next sweep, so its slot is "young" again when reused |
| block stamp `+0x18` | written by allocation (`0xE4C1E0`), new blocks, `GC_clear_marks` (`0xE4C11A`); the sweep skips a block whose stamp equals `GC_gc_no` (`0xE500B6`) | blocks allocated into after the mark ended are not swept in that cycle |
| `GC_free` does not touch the mark bit | `0xE4BEB0` | correction of docs/57 §1 (it sets the free bit; docs/287 §3 already corrected `0xE4C1E0`) |

Writers of mark words in the Boehm range `0xE48E80`-`0xE51E80` [verified scan for stores to header `+0x1C`]: the 8
mark sites and `0xE4CE44` (docs/287, I-516-10), the clear in `0xE4C0F0`, the sweep's free-bit mask `0xE500DA`, and
`GC_set_mark_bit` / `GC_clear_mark_bit` helpers. Nothing else clears marks per cycle [inferred from this scan, I-520-1].

Full read of the sweep, the free-bit and stamp rules, every mark-bit reader and writer and `GC_finalize`: §8.

## 2. Cost model

### 2.1 Where the time goes today

- Call-site map of the GC step: docs/287 §1. Per-tick GC on BP: about 120 ms/s, one cycle per ~8.4 s, ~1.0 s of GC
  per cycle, live 90-100 MB in a 356 MB heap [measured, docs/281 §3].
- Sub-timer smoke of 2026-11-18 (`gc-subtimers-measure-1118`, BP): mark + stop-the-world finish = 83% of `gcgMs`
  [measured, reported by the orchestrator; the result table in docs/287 §2.4 is not filled yet].
- So the cost is the full mark of the live heap, every cycle, plus up to 41 dirty-page rescans and root pushes.

### 2.2 The time limit

- `GC_time_limit` (5) is not a pause limit in this build: its only use is doubling the divisor in `min_words_allocd`.
  `GC_timeout_stop_func` is not used; the limit is EA's stop function `0xD717B0`: elapsed µs since `[0x11EE524]`
  against `[0x11922A4]`, gated by `[0x11EE528]`.
- Not covered by the budget: `GC_gww_read_dirty`, `GC_clear_marks`, the world stop, `GC_finish_collection`.

### 2.3 Dirty-page rescans

- Per finish attempt: one GetWriteWatch pass over all sections (about 100,000 pages for 400 MB, at least one call per
  1,024 dirty pages), a walk over every block header (state 1 visits all blocks and tests their pages), a rescan of
  every marked object in a dirty block, and all roots.
- Granularity is the block, not the object: for a small-object block one dirty page rescans all marked objects of
  that 4 KB block; for a large object any dirty page rescans the whole object.
- **Not measured: dirty pages per read.** This number decides the gain of a minor collection. Allocation itself
  dirties pages (free-list links, object init), and new objects go into free slots between old ones (112 MB of
  unmarked slots beside live ones, docs/542 §7.2), so the dirty share may be large [inferred, I-520-2]. Counter to
  add first: bits set in `0x1227714` after each `0xE4EFB0`, and marked objects pushed by `0xE4D120`.

### 2.4 What a minor cycle would cost

Mark work = marked objects in dirty blocks + roots + objects allocated since the last cycle that are reachable.
Unchanged: the sweep (walks every block), finalization, GetWriteWatch. Old garbage is not freed until the next full
cycle, so the heap grows between full cycles; the policy (§5.3) bounds that.

## 3. Type information and roots

### 3.1 Object layout known to the collector [doc: docs/542 §2.2, docs/57 §1; addresses verified there]

| Kind | Entry | Who | Scan |
|---|---|---|---|
| 0 pointer-free | `GC_malloc_atomic 0xE4BDC0` | Mono object.c only: strings, primitive arrays, classes without reference fields (`0xEA14B0`, `0xEA1510`, `0xEA2770`, `0xEA2E70`, `0xEA3EB3`) | never scanned |
| typed | `GC_malloc_explicitly_typed 0xE50DE0`, descriptor in the object's last word | Mono objects whose class has a bitmap descriptor (`0xEA1490`, `0xEA2690`, `0xEA2770`, `0xEA2E70`, `0xEA1CB0`), EA arena chunks (`0xD71900`, `0xD718A0`), `0xE697D0` | only the bitmap's words; each word still passes the conservative "is this an object start" test |
| 1 normal | `GC_malloc 0xE4BE30` | 40 sites: reference arrays and objects without a descriptor, reflection objects, mono-ehash, mint task state, EA glue | every word |
| 2 uncollectable | `GC_malloc_uncollectable 0xE4BF90` | EA host only, 9 sites | every word; always marked |

- Which classes get a bitmap (size limit of the descriptor, arrays of references) follows Mono 1.2.3's
  `mono_class_compute_gc_descriptor` [community-reported: source; not re-read in the binary, I-520-5].
- Six mark procs, table `0x12225E8` (docs/287 §3); EA's `0xE69520` scans arena chunks up to the frame top.
- So the heap is partly precise in layout and conservative in pointer identity. That is enough for a non-moving
  generational collector (it never needs to know that a word is a pointer for certain). It is not enough to move
  objects (§4).

### 3.2 Roots [doc: docs/57 §4, docs/130; order verified in `GC_push_roots 0xE4D910`]

| Root | Size | Conservative? | In a minor cycle (`all = 0`) |
|---|---|---|---|
| static root ranges `0x122FB14` (12-byte entries, count `[0x11F3084]`; callers of `GC_add_roots 0xE4D9F0`: `0xE53070`, `0xE75BF0`, `0xE78DA0`) | Mono static-field blocks and runtime tables; total bytes not measured | every word | pushed in full: the pages are outside the heap, so the dirty test says dirty |
| free-list heads of each kind | small | - | as today |
| finalizer and disappearing-link tables `0xE4A9A0` | per registered object | exact | as today |
| collector's registers and its own stack (`0xE4BA10`; EA `GC_push_other_roots` `[0x11949CC]` -> `0xD71820`) | sim-thread stack depth | every word | as today |
| uncollectable objects (state 2 in a full cycle) | EA cells: task container, arena list nodes, 0x28 / 0x2C-byte cells | every word | only those in dirty blocks (they stay marked, so state 1 covers them) |
| mint frames and eval stacks | 2 KB typed chunks **inside the GC heap**, reached from task state | conservative up to the frame top | no root entry of their own: an old (marked) chunk that was written is in a dirty block and is rescanned; a new chunk is reached from its task state |
| not roots | image `.data`, TLS, other threads' stacks (docs/542 §3) | | |

## 4. docs/542 L3, claim by claim

| Claim in docs/542 | Verdict | Evidence |
|---|---|---|
| "mint keeps raw object pointers in frames and arena chunks, scanned conservatively" | true; blocks **moving** and full precision only | §3.2. A non-moving collector scans them as today |
| "EA keeps managed refs in uncollectable cells and on the stack, never in handles" | true; blocks **moving** only | cells stay marked and are rescanned when dirty (§1.3 state 1) |
| "no write barrier anywhere" | **false as a blocker** | the GetWriteWatch page barrier is in the binary, on, and already feeds a marked-and-dirty rescan (§1.3, §1.4). It needs no store instrumentation |
| "precise: not feasible" | partly present, rest needs work, **not needed** | typed and pointer-free kinds carry layout; kind 1 (40 sites) and all roots do not (§3.1) |
| "generational: not feasible" | **wrong** | sticky mark bits + the existing state 1 path; three calls to skip at cycle start (§5.1) |
| "moving: not feasible" | holds in practice | an ambiguous word cannot be updated, so every object reachable from a conservatively scanned place must stay: stacks, mint chunks, uncollectable cells, every kind 1 object (all reference arrays). Mostly-copying with pinning would pin most of the heap until reference arrays and mint frames get exact maps [inferred, I-520-6] |

## 5. How a minor collection fits (input for the design, P1)

### 5.1 The collector side

- Minor cycle = cycle start without `GC_promote_black_lists` (`0xE49FD5`), `GC_invalidate_mark_state` (`0xE49FDA`)
  and `GC_clear_marks` (`0xE49FDF`). The mark state is 0 there (the previous collection is finished), so
  `GC_initiate_gc` selects state 1 on its own. Everything after that is EA's unchanged code.
- Hook form: retarget the three `E8` call sites to wrappers that return at once when the cycle is minor. `0xE49FDF`
  is already retargeted by `[GC] SubTimers` (docs/287 §1): read the current target at install and chain to it.
- A mark-stack overflow in a minor cycle sets state 5 with marked objects present, and EA's code then rescans every
  marked object (`GC_push_next_marked`): correct, costs one full mark.
- `GC_is_full_gc` stays 1 at `0xE49FEF`; it only drives EA's attempt counter and heap-size bookkeeping.

### 5.2 Remembered set: who consumes what

| Source | Consumer | Complete? |
|---|---|---|
| GetWriteWatch pages (today) | `GC_page_was_dirty 0xE4F170` in state 1 | every user-mode write by any thread to a heap section; no store census needed [inferred, I-520-3] |
| software barrier (`[AutShadow] Satb`, docs/206; JIT stores, docs/507) | could replace the answer of `0xE4F170` (three references: `0xE4CC4E`, `0xE4D175`, `0xE4D1B7`) with a finer card or object set | only if every store path is covered (P0b census; EA native stores are the gap, docs/542 §4). Until then it can only be checked against the page bits, never replace them |

Minor root set: §3.2, last column. No new root code is needed.

### 5.3 Policy

Boehm's own rule: full when `GC_need_full_gc` (still computed at `0xE49A91`-`0xE49AB1`: heap in use grew by more
than the allocation threshold since the last full cycle) or after N minor cycles. Forced collections (`0xE4A320`,
`GC_gcollect`, heap exhaustion) stay full.

### 5.4 Allocation touch points and Apex

The design touches no allocation path: `GC_malloc*`, `GC_free`, free lists, `GC_allocobj`, heap sections and
`GC_win32_get_mem` stay as they are. Touched: three call sites in `0xE49FB0`, optionally the dirty test `0xE4F170`.

- Apex (standalone) ships no GC patch; its only GC hook is the Frame Profiler entry detour on `0xE4A050`
  (developer mode) [doc: docs/187 row 16; Apex engine notes on the Mono GC, local copy]. S3SS: `0xD819AA`
  (Chunky), `0xE511F5` (stop-world), finalizer throttle (docs/68). None is at our sites.
- To check while building: Apex's VirtualAlloc layer must leave `MEM_WRITE_WATCH` on GC sections (a section without
  it makes GetWriteWatch fail, and EA's fallback marks the section dirty: correct, no gain) [inferred, I-520-4].

### 5.5 Exactness

A minor cycle frees a subset of what a full cycle would free, and nothing reachable. Script-visible differences are
limited to when finalizers and weak references of old dead objects fire (later, at the next full cycle); their timing
is not deterministic today either. The household rule is not touched: no Sim decision reads the collector.
Verify mode (P2): after a minor mark completes, run a full mark into a shadow bitmap and require that no object the
full mark reaches is unmarked.

## 6. Open, verify while building

| # | Question | How |
|---|---|---|
| 1 | dirty pages and rescued objects per read (decides the gain) | counter on `0xE4EFB0` / `0xE4D120`, one 10-min BP job |
| 2 | sweep behaviour under sticky marks with EA's stamp skip and free bits (blocks never swept, objects born old) | **answered, §8.1-§8.3, §8.8**: no change to the sweep; born-old objects exist and are safe |
| 3 | finalization (`GC_finalize 0xE4AE60`) with sticky marks: no object finalized while reachable | **answered, §8.4, §8.8**: no change; old objects are finalized in full cycles only. Two EA weak-reference hazards found, §8.6 |
| 4 | Apex VirtualAlloc hooks keep `MEM_WRITE_WATCH` | log GetWriteWatch failures (EA already warns) |
| 5 | Mono descriptor rules (which classes are typed) | read `0xEA1490` callers' class setup; only needed for moving |
| 6 | EA 1.69 / retail addresses | `tools/port-run.ts` as for docs/287 |

## 7. Write barrier census (P0b, 2026-11-18, static, ledger docs/51 §521)

Question: where does anything store a managed object reference into memory the collector treats as heap, so that a minor collection needs to hear about the store? Steam 1.67 addresses; EA 1.69 goes through `TS3A` as usual.

### 7.1 Summary

- The census does not start from zero. `[AutShadow] Satb` (docs/206 §7, docs/212, docs/220, docs/507 c) already puts a software barrier on every interpreter store, on the compiled stores, on the runtime's store helper and on 45 runtime icalls. Its coverage list is the baseline here. Satb logs every store; a GC barrier needs only **reference** stores, which is a subset, so every Satb hook point is a valid barrier hook point.
- What Satb cannot give a collector: Satb may *stop the worker* when it meets a writer of unknown size. A collector has no such exit. A store it misses frees a live object.
- **A software-only barrier cannot be proven complete in this binary** [verified facts below, conclusion inferred]. The runtime has exactly one barrier-shaped helper (`0xE69820`, 80 call sites); every other native reference store is a plain `mov` or a block copy: 154 `memcpy` call sites in the Mono runtime range, 296 EA binder bodies that write fields of managed objects, the GC handle table, `Hashtable.Rehash`, the StringBuilder bodies.
- **The OS write watch is complete by construction** [verified that it is on: docs/57 §6, heap sections are allocated with `MEM_WRITE_WATCH` at `0xD70906`, read at `GC_gww_dirty_init` `0xE4EFB0`]. It sees every user-mode store from every thread, whoever made it.
- Recommendation: the remembered set is **write watch, always** (page granularity). Software card marks on the hot paths (interpreter, compiled code, the runtime helper, array copy) are built as a second source and at first used only to *measure* and to *verify* (§7.8). Card marks replace the page scan only for pages that a later phase can prove no unknown writer touched; until then the card table is the union of both sources, which is as safe as write watch alone.

### 7.2 What needs a barrier and what does not

A minor collection in a non-moving, sticky-mark-bit design (design B of the handoff; §5) traces from the roots plus every *marked* (old) object that was written since the last collection. Consequences for the census:

| Store target | Barrier needed | Why | Tag |
|---|---|---|---|
| field / element / static slot of a marked (old) heap object, value is a reference | **yes** | the only place an old-to-young pointer can appear | design |
| any slot of an object allocated since the last collection (unmarked) | no | young objects are traced in full when reached | design |
| scalar store (int, float, char) | no | cannot create a reference; note that write watch cannot tell it from a reference store | design |
| null store, `initobj`, `initblk`, `Array.Clear` | no | removes a reference; never creates one | design |
| interpreter frame arena chunks (locals, arguments, evaluation stack) | no stub; write watch covers them. A cards-first mode must **scan every live chunk at each minor** | chunks are typed GC objects in the heap (`0xD718A0`, 2 KB), written by every `stloc`; today a marked chunk is rescanned only when its block is dirty (§3) | [verified] docs/57 §1, §3; cards-first treatment [design] |
| the value-type stack and native stacks | no | already scanned conservatively as stacks | docs/542 §3 |
| `GC_malloc_uncollectable` cells (EA host: call records, arena list nodes, task container `0xD7E560`, 9 allocation sites) | no stub; write watch covers them. A cards-first mode must push them in full at each minor | they keep their mark and are rescanned only when their block is dirty (§1.3 state 1, §3), and EA native code writes them with plain stores | [verified] §3; cards-first treatment [design] |
| `GC_add_roots` ranges, ts3native / exe `.data` | no | static roots, always scanned | docs/120 §2 |
| static fields | **yes** | static data of a class comes from `0xE697D0` (typed or plain `GC_malloc`, collectable) and is stored in the vtable `+0xC` at `0xEA1FB2`, then listed by `0xE75600`; it is a heap object, so it is rescanned only when dirty. This differs from the §3 row that lists static-field blocks under the root ranges. Pointer-free statics sit in the domain mempool | [verified] `0xE697D0`, `0xEA1FA4`-`0xEA1FB5`; docs/206 §7.1 |
| thread / context statics (`0xEBAB30`) | **yes** (same allocation helper, callers `0xEBA988`, `0xEBA9AF`) | [verified] callers |
| strong and pinned GC handle slots | **yes** | the slot array is `0xE697D0(size, 0)` = collectable `GC_malloc` memory (`0xE7879D`, grown at `0xE788EF` with a block copy at `0xE78903`) | [verified] |
| weak GC handle slots, disappearing links | no | hidden pointers, not traced; cleared by the collector | [inferred], P0a |

The danger case for any "this object is fresh, skip the barrier" argument: native code that allocates a container, then in a loop allocates an element and stores it. A collection inside the loop marks the container; every later store is an old-to-young store through a plain `mov`. The runtime's reflection and string-split icalls have this shape. Write watch catches it. A card-only barrier needs a hook in each such loop.

### 7.3 Group 1: the interpreter (mint), dispatch table `0xE59880`

All 37 store opcodes (26 handlers) are in `docs/data/satb-handlers.csv` with operands and sizes [verified there]. The reference-capable subset:

| Opcode(s) | Handler | Stores | Heat | Barrier | Risk |
|---|---|---|---|---|---|
| `stfld.o` / `stfld.p` 0x61 / 0x62 | `0xE56BE7` | one reference into `obj + offset` | hot | table swap stub: mark the card of the destination, then the game's handler | low: same mechanism as Satb |
| `stind.i4` / `stind.ref` 0x93 / 0x98 | `0xE559A7` (shared) | 4 bytes at an address; target may be heap, static, arena or native | hot | stub marks whenever the address is in a heap section; an `int` store marks too (harmless) | low |
| `stind.i` 0x95 | `0xE559EB` | pointer-sized store | warm | as above | low |
| `stelem.ref` 0x11A, `stelem.i` 0x113 | `0xE574DA`, cases `0xE5759A` / `0xE5750D` | array element | hot | stub: card of `arr + 0x10 + idx * 4` (or of the array header for a coarse mark) | low |
| `stsfld` 0x6A | `0xE56F52` | static slot, typed through `0xE52350` | warm | stub resolves the address as Satb's `SatbStaticAddr` does, or hook `0xE52350` (below) | medium: class init can run inside |
| `stfld.vt` 0x63, `stsfld.vt` 0x6B, `stobj.vt` 0x100, `stelem.any.vt` 0x112, `strmfld.vt` 0x65 | `0xE56C0A`, `0xE56FEC`, `0xE57089`, `0xE575CF`, `0xE56CA9` | a value type copied into the heap (block copy `0xF7E8A6`); may contain references | warm | stub marks every card of the destination range; no need to know the layout | low |
| `stobj` 0xFF, `strmfld` 0x64 | `0xE570C8`, `0xE56C43` | typed store through `0xE52350` | cold | range mark | low |
| `cpblk` 0x101 | `0xE58A17` | raw block | cold (unsafe code) | range mark | low |
| `initobj` 0xF5, `initblk` 0x102 | `0xE589F7`, `0xE58A49` | zeros or bytes | - | none needed | - |
| `ldelema` 0x110, `ldflda`, `ldsflda`, `ldloca` | `0xE57340` and others | no store; the address is written later by `stind` / `stobj` / `cpblk` or handed to native code | - | covered at the store; an address handed to a native body is §7.6 | - |
| `stloc*`, `starg*`, `stinarg*`, the four store superinstructions 0x1B7, 0x1BC, 0x1BF, 0x1C0 | - | frame arena only | very hot | no stub: write watch; scanned in full in a cards-first mode (§7.2) | - |
| `localloc` | - | memory from the frame arena / value-type stack | - | no stub, as the arena [inferred, not read] | - |

Typed stores inside the interpreter that are not opcode handlers [verified call sites of `stackval_to_data` `0xE52350`, 9 callers]: array `Set` of the runtime-implemented array methods (`0xE53950`, called from `0xE53DF7`; **not** in Satb's opcode list), the result copy of the invoke entry `0xE59FD0` (`0xE5A583`), `stsfld` (`0xE56FC2`, `0xE56FDF`, `0xE57070`), `strmfld`, `stobj`, one site at `0xE5725E`, and EA's static setter (`0xD89C50`). One detour on `0xE52350` (destination is its second argument) covers all nine [inferred: argument order from the call sites read].

The interpreter also has 20 direct block-copy call sites (`0xE5228D` ... `0xE59252`). Those inside `0xE521A0` / `0xE52350` are the typed load / store helpers; the rest are loads onto the value-type stack and the store handlers above [inferred from Satb's handler table; the load-side sites were not re-read one by one].

How a stub goes in: Satb's `SatbSwap` / `kSatbOps` (satb.cpp) already swaps exactly these table entries and proves at install that each entry is the game's handler. A GC barrier is on for the whole session, not for a span, so its stubs must be the bottom layer and Satb, `mintcall`, `MonoJit` and `MintDivGuard` must accept them as "the game's handler" (they compare table entries at install today; docs/68).

### 7.4 Group 2: compiled code (`[MonoJit]`, monojit.cpp)

[verified, monojit.cpp + docs/507 c] The compiler emits heap stores only for `stfld.*` and `stind.*` plus the value-type copies and its inlined `Array.FastCopy`; every one of them already calls the emitter helper `satb(width, base, disp, bytes)` (11 sites, monojit.cpp lines 1515-1806, and `JitSatbStore` at line 825 for the array copy). Every other store opcode exits through the **live** jump table (line 712), so a table stub of §7.3 runs for it. `stloc` / `starg` / `stinarg` write the frame arena.

Barrier: extend the same emitter with an inline card mark (address in a scratch register, shift, one byte store; about 12 bytes, no call) at the sites whose value is a reference or a value type. Heat: hot. Risk: low; the sites are enumerated by one helper. The fused and paired handlers (`mintfuse.cpp`, `mintpairs.cpp`, `mintcall.cpp`) must be checked for stores that bypass the table [not read in this phase].

### 7.5 Group 3: Mono runtime C code

| Site | What it stores | Heat | Barrier | Risk | Tag |
|---|---|---|---|---|---|
| `0xE69820`, the field / array-element reference store helper (stores its third argument through its second) | one reference; 80 direct callers: 78 in the runtime (`0xE650FF` ... `0xEB9D80`: reflection objects, custom attributes, delegates, exceptions, string arrays) and EA `0xD760E1`, `0xD79BDA` | warm | one entry detour (Satb has one: prologue check + 8 bytes); mark the card of argument 2 | low | [verified] body and callers |
| no other barrier helper exists: the neighbours `0xE69810`, `0xE69830` are not stores; there is no generic-store or value-copy barrier function | - | - | - | - | [verified] `0xE69780`-`0xE69870` |
| `Array.FastCopy` `0xE7A070` (the body under `Array.Copy`) | element range, block move; reference arrays included | hot (List growth, CopyTo) | icall table `0x10B5838` wrapper (Satb's `SatbRtFastCopy`): mark the destination range when the element class holds references | low | docs/206 §7.2 |
| `Array.SetValueImpl` `0xE793D0`; `Array.SetValue(object, int[])` `0xE79CE0` calls it directly at `0xE79D77`, `0xE79E05` (wrapper bypass) | one element | cold | wrapper + detour the body instead of the table entry | low | docs/212 |
| `Interlocked.CompareExchange` / `Exchange` family `0xEBA190`-`0xEBA3C0` | one reference | cold (not declared by the game's mscorlib, docs/212) | wrapper | low | docs/206 §7.2 |
| `MonoField.SetValueInternal` `0xE7B320` -> `set_value` `0xEA10A0` | field by type, static or instance | cold | wrapper, destination from `SatbFieldDest` | low | docs/206 §10 |
| `MonoMethod.InternalInvoke` `0xE7CDC0`, `Marshal.PtrToStructure` `0xE99A50`, `get_property_info` `0xE7B4D0`, `construct_internal_locale_from_lcid` `0xE874B0` | fields of an existing object or caller array | cold | wrappers exist in Satb (sized) | low | docs/206 §10, §15 |
| GC handle allocation `0xE78780` (callers `0xE79015`-`0xE79051`) | a reference into the handle slot array | cold | detour: mark the slot array | low | [verified] |
| value-type copy into the heap by runtime code (`mono_value_copy`-shaped block copies; part of the 154 `memcpy` sites in `0xE5A800`-`0xEBC000`) | value types with references | unknown | **no single hook point**; write watch | high for card-only | [verified] count, sites not classified |
| boxing `0xEA3140` (76 callers), object clone `0xEA2710`, `Array.Clone` `0xEA3800`, `mono_object_new*`, array and string allocation, delegate construction, class init static stores, string intern | a fresh object | hot | none while the object is unmarked; the container-then-loop case of §7.2 applies to callers that keep filling after another allocation | medium for card-only | [inferred] |
| remoting, P/Invoke marshal wrappers | - | not reachable in this game (docs/212, I-119-13) | none | - | [inferred] |
| finalizer queue, disappearing links | collector-internal tables | - | none; the collector owns them (P0a) | - | [inferred] |
| `String.InternalStrcpy` x4, `Buffer.BlockCopy`, `Marshal.Write*`, `Buffer.SetByte` | characters and bytes | hot | none: pointer-free objects (atomic kind) are never scanned | - | design |

### 7.6 Group 4: EA native code

| Site | What it stores | Heat | Barrier | Risk | Tag |
|---|---|---|---|---|---|
| binder native bodies, 1,085 registrations, one dispatch `0xE525C0` (`docs/data/binder-bodies.csv`): 296 write fields of `this` or of objects reached from arguments, 25 write byref / array arguments, 10 unknown | plain stores. Reference stores among them [inferred from docs/212, docs/220]: `Hashtable.Rehash` `0xDCA870` (new bucket array into an old table), StringBuilder bodies (`_str`, `_cached_str`), `PriorityQueue.Add` / `RemoveAt` / `SetCapacity` `0xE0D450` / `0xE0C640` / `0xE0D290` (`mItems`, 16-byte items), `String.Format` object arrays, XML reader state | hot (the dispatch runs for every bound call) | one detour on `0xE525C0` (Satb row 12 has it): after the body returns, mark `this` (whole object) and every array / byref argument that points into the heap. Coarse but one hook | medium: a body that writes an object reached two loads deep is not marked; 471 bodies have opaque handoffs (docs/220) |
| EA ICs with byref / array parameters, 180 (`satb-ics.h`) | caller memory of unknown size | warm | Satb's guard thunk: mark the argument objects | medium | docs/206 §7.2 |
| EA static helpers: set `0xD89BF0` (through `0xE52350`), address-of `0xD89C60` | a static slot; the address-of result is written later by native code | warm | detour (Satb replaces all three); an address-of marks the slot's card and it stays suspect until the next minor | medium | docs/206 §15 |
| host static setters `0xD79EF0` / `0xD79F50` | 4-byte int / float statics | caller unknown | none: scalars | - | docs/212 row 9 |
| per-tick stores `0xD81D39` / `0xD81D49` (`sSimTickCnt`), `SimClock.ElapsedTime*` `0xE0A6D0` / `0xE0A790`, `Curve.Fx` `0xE0A950` | scalars | hot | none: scalars. They do dirty their pages under write watch every tick | - | docs/212 rows 1, 2, 6 |
| EA uncollectable cells and host maps `+0x1664` / `+0xBE8` | references held by native code | - | no stub: write watch; pushed in full in a cards-first mode (§7.2) | - | docs/542 §3 |
| native callbacks that fill managed arrays or structs outside the binder and IC paths | unknown | unknown | **not enumerable statically** (indirect calls); write watch | high for card-only | docs/212 limits |

### 7.7 Group 5: block copies of managed memory

`memcpy` `0xF7E8A6` has 768 direct callers in the exe: 20 in the interpreter, 154 in the Mono runtime range, 6 in the EA Mono glue (`0xD70000`-`0xD9B000`), none in the binder-body range (bodies reach it through helpers and cached icall pointers). `memmove` is the import `0xF95684`. `memset` `0xF7E8B8` (630 binder-body sites) never creates a reference. A block copy matters only when the destination is a marked object that is scanned (not atomic) and the bytes contain a young reference: `Array.FastCopy`, the value-type store opcodes, handle table growth and runtime value copies are the known cases; the 154 runtime sites are not classified one by one. Kernel writes (file reads into a `byte[]`) land in atomic objects only [inferred].

### 7.8 Write watch as the remembered set, and the hybrid

| | Write watch (today's dirty bits) | Software card marks |
|---|---|---|
| Completeness | every user-mode store, any thread, any writer; nothing to enumerate | only the hooked paths; §7.5-§7.7 list writers with no hook point |
| Granularity | 4 KB page; any store dirties it, scalars included (tick counters, motive floats, frame arena) | 512 bytes or finer; reference stores only |
| Mutator cost | none in user mode (the kernel takes one soft fault per page per reset) | a few instructions per reference store in a table stub; inline in compiled code |
| Collector cost | one `GetWriteWatch` + reset per collection over all heap sections, then a scan of every marked object on each dirty page | scan of the marked cards only |
| Address space | none | a byte table: 8 MB reserve for 4 GB at 512-byte cards, committed only where the heap is (about 1/512 of the heap) |
| Unknowns | how many pages are dirty per minor in a live game, and what share only scalar stores dirtied: **not measured** | whether any writer is missed: cannot be shown statically |

Hybrid, in steps that never lose a store:

1. **Mode 1, write watch only.** The remembered set is the dirty-page set. This is the correctness baseline and it needs no census.
2. **Mode 2, union + measure.** Card stubs on, and at each minor the card table is OR-ed with the dirty pages (a dirty page marks all its cards). Same safety as mode 1. Counters: dirty pages, card-marked pages, pages dirty with no card (scalar-only or unknown writer). This is the measurement that says whether cards can gain anything.
3. **Verify mode.** For every page that is dirty with no card mark, the scan records whether it found a marked object pointing at an unmarked one that nothing else kept alive. Each hit is a missed barrier, logged with the object's class and field offset. This turns the open rows of §7.5-§7.7 into a measured list.
4. **Mode 3, cards first.** Only after verify mode shows no miss over long runs: pages with no card mark are skipped. Any correctness doubt falls back to mode 2 (handoff rule: exact).

A mix that would *not* be sound: trusting cards on pages where a hooked writer wrote and scanning the rest. Write watch cannot say who wrote, so an unknown native writer on the same page would be lost.

### 7.9 Dependencies and conflicts

- The GC heap's page layer: sections must keep `MEM_WRITE_WATCH`. The allocator around it is Apex Radiance's area since 2026-11-12; a generational mode must not replace it and must check at install that a heap section still answers `GetWriteWatch` (refuse the switch otherwise). `gcgrow.cpp` and Apex's page hooks need the same check [not read here].
- Table stubs share entries with Satb, `mintcall`, `MonoJit` exits and `MintDivGuard`; the detours on `0xE69820`, `0xE525C0`, `0xD89BF0` / `0xD89C60` and the icall table are Satb's today. One owner module with clients is needed, as docs/507 did for the transform entry `0xE643B0`.
- Compiled code runs only on the sim thread; a worker thread that stores references (`[AutShadow]`, P2 threads) needs the same stubs. The card byte store is idempotent, so no lock is needed.

### 7.10 Open questions

1. Answered by §3: uncollectable blocks and arena chunks are rescanned only when dirty. A cards-first mode has to scan both in full; their size per minor is not measured.
2. Dirty pages per minor in a live game, and the scalar-only share (mode 2 counters; needs a game slot).
3. Classification of the 154 runtime `memcpy` sites and of the binder bodies' reference stores from their own code (`binder-bodies.py` knows the store sites; it does not yet say reference or scalar).
4. Stores in `mintfuse` / `mintpairs` / `mintcall` templates that bypass the jump table.
5. Whether `0xE5725E` and `0xE5A583` can target a marked heap object or only stack / fresh memory.

## 8. Sweep, free bits, stamp and finalization under sticky marks (lane GB, brief B4, 2026-11-18)

Static read (Capstone) of the sweep `0xE4FF00`-`0xE50460`, `GC_free 0xE4BEB0`, the header setup, `GC_finalize
0xE4AE60`, the cycle finish `0xE49F10` / `0xE49DA0` and every reader and writer of a mark bit. Everything in this
section is [verified] in the disassembly unless tagged. Ledger §523. Answers §6 #2 and #3: **EA's sweep and
finalization need no change for a minor cycle**; the rules the minor cycle must follow are in §8.5.

### 8.1 Function map

| Function | Address | What EA's build does |
|---|---|---|
| finish the previous collection | `0xE49F10` | called first by `GC_try_to_collect_inner`: runs `GC_collect_a_little_inner` until the mark is done (aborts when the stop function fires), then, if the sweep flag `[0x11F2F94]` is set: `GC_finish_collection` once (guard `[0x11947E4]` != -1) and `GC_reclaim_all(stop_func, 0)`; returns 1 only when the sweep is complete |
| mark end inside the incremental step | `0xE49EDF`-`0xE49EF2` | `GC_gc_no` + 1, `[0x11F2F94]` = 1, return. **No `GC_finish_collection` here**: it runs on the next entry of `0xE49F10`, which may be a later tick (§8.6) |
| mark end inside the cycle start | `0xE4A01B` | `GC_gc_no` + 1, flag, `GC_finish_collection` at once |
| `GC_finish_collection` | `0xE49A50` | notify 3, `GC_finalize`, `GC_start_reclaim(0)`, heap bookkeeping (`GC_is_full_gc` branch `0xE49A71`), notify 4. No free-list or reclaim-list clear, no `GC_clear_fl_marks` (EA's free bits replace them) |
| `GC_start_reclaim` | `0xE502F0` | only `GC_apply_to_all_blocks(GC_reclaim_block)`. Free lists and reclaim lists are **not** cleared (Boehm 6.6 clears both): free lists live across cycles |
| `GC_reclaim_block` | `0xE50160` | stamp `+0x18` == `GC_gc_no`: return (`0xE5017F`). Large (> 0x200 words): marked -> keep, else `GC_freehblk 0xE49730`. Small: push on the kind's reclaim list (`+4` link). The Boehm "block empty -> free now" test is gone |
| `GC_reclaim_small_nonempty_block` | `0xE50070` | stamp == `GC_gc_no`: return (`0xE500B6`). Else mark word &= ~free word for the whole block (`0xE500D6`-`0xE500E6`). No mark left and the size's free list not empty: unlink this block's free-list entries (`0xE4FF00`, count from `0xE4FEB0`), `GC_freehblk`. Otherwise `hb_last_reclaimed` (`+0x16`) and the reclaim proper (`0xE4FF90`) |
| reclaim variants | `0xE4F3D0` (clear), `0xE4F8E0` (uninit), size-specialised `0xE4F4A0`, `0xE4F620`, `0xE4F970`, `0xE4FAE0`, `0xE4FD40` | a slot goes on the free list only when its mark bit **and** its free bit are 0; its free bit is then set (`0xE4F440`, `0xE4F947`). Mark bits are never written. Uncollectable kinds: all marks set again afterwards (`0xE50059` -> `0xE4C0B0`). Read in full: `0xE4F3D0`, `0xE4F8E0` and the first step of `0xE4F4A0`; the other size-specialised ones by their free-bit references and stores only [inferred same rule, I-523-3] |
| `GC_continue_reclaim` | `0xE50310` (caller `0xE4A2D9`, allocation) | sweeps blocks of one size from the reclaim list until the free list is non-empty |
| `GC_reclaim_all` | `0xE50380` (one caller, `0xE49F81`, `ignore_old` = 0) | sweeps every listed block, polls the stop function per block |
| `GC_free` | `0xE4BEB0`, 16 call sites (host `0xD70DC5`, `0xD714C5`, `0xD71810`, `0xD71AAB`, `0xD71B47`, `0xD71BC6`, `0xD80AD3`, `0xD80B0A`, `0xD80B3A`, `0xD80B4D`; collector `0xE4AC4C` (unregister link), `0xE4F35F`; Mono `0xE52E34`, `0xE52E47`, `0xE52E5A`, `0xE53EA5`) | small: clear the body, push on the free list, set the free bit. Large: `GC_freehblk`. **Mark bit and stamp untouched** |
| allocation touch | `0xE4C1E0` (5 callers: `0xE4BC7E`, `0xE4BDFD`, `0xE4BE6E`, `0xE4BFDA`, `0xE50E53`) | clear the free bit, stamp = `GC_gc_no`. **Mark bit untouched** |
| header setup (new or reused block) | `0xE48FFE`, `0xE49004`, `0xE4901D` | marks zeroed (`0xE4C070`), free bits zeroed (`0xE4C090`), `+0x16` and stamp = `GC_gc_no` |
| `GC_new_hblk` | `0xE4E80D`, `0xE4E860` | uncollectable: all marks set; every slot's free bit set while the free list is built |

### 8.2 What the stamp and the free bits mean

- Stamp == `GC_gc_no` means "allocated into (or created, or marks cleared) since the last mark end". `GC_gc_no` moves
  only at a mark end, so such a block holds objects the last mark never saw. The sweep skips it (both entry points)
  and `GC_finalize` treats its objects as live (§8.4). The block is swept in the next cycle unless allocated into again.
- `GC_clear_marks` stamps every block too (`0xE4C11A`). No reader runs between a cycle start and its mark end (reclaim
  lists are empty then, §8.1 row 1), so that stamp changes nothing; a minor cycle that skips it loses nothing.
  Readers of the stamp: `0xE4AEF3`, `0xE4AF9F`, `0xE4B0D3`, `0xE500B6`, `0xE5017F`; no other compare of `+0x18` in
  `0xE48E80`-`0xE51E80` [verified scan, same heuristic limit as I-520-1].
- Free bit = "this slot is on a free list". The marker never marks such a slot and never rescues one: free-bit tests
  at the mark sites (`0xE4C5F0`, `0xE4C78E`, `0xE4C8D7`, `0xE4CD18`, `0xE4CE30`, typed `0xE506B0`, `0xE50854`,
  `0xE509C0`, `0xE50B3A`) and in the push-marked loops (`0xE4CF21`, `0xE4D056`). So a pointer to a freed slot keeps
  nothing alive and a free list is never traced.
- A mark bit can outlive its object in one way only: `GC_free` of a marked object. The bit goes away when the sweep
  next visits the block (the mask), unless the slot is reused first: then the new object is **born marked**.

### 8.3 Mark-bit writers and readers (`0xE48E80`-`0xE51E80` and the host callers)

| Site | Kind | Under sticky marks |
|---|---|---|
| 8 mark sites + `0xE4CE44` (docs/287 §3) | set | unchanged |
| `GC_set_mark_bit 0xE4C140`: `GC_finalize` (`0xE4AFF6`, `0xE4B0C7`, `0xE4B186`, `0xE4B19F`, `0xE4B2A6`, `0xE4B2BF`), uncollectable allocation `0xE4C02A`, `0xE4D980`, tail call `0xE4F2CB` | set | unchanged |
| `GC_set_hdr_marks 0xE4C0B0` (`0xE4E80D`, `0xE50059`) | set all, uncollectable blocks | unchanged |
| `GC_clear_marks` walk `0xE4C0F0` | clear all but uncollectable | full cycles only: this is what ends an old object's life |
| sweep mask `0xE500DA` | clear, free slots only | unchanged |
| `GC_clear_mark_bit 0xE4C190` (`0xE4AF1E`, `0xE4B3A2`) | clear, the collector's own link-table entry after a weak link is dropped | works as in Boehm's own partial mode: the entry is swept in this cycle |
| header setup `0xE4C070` | clear, new or reused block | unchanged |
| `GC_is_marked 0xE4C230`: `GC_finalize` (`0xE4AEE7`, `0xE4AF8F`, `0xE4B00F`, `0xE4B0AE`, `0xE4B1E9`, `0xE4B309`) | read | §8.4 |
| reclaim variants, `0xE50160` (large) | read | old garbage is kept: intended |
| push-marked loops, state 1 / state 5 | read | this is the rescue |
| EA heap walk `0xE4A3F0` -> `0xE4A350` (marked and not free), host callback `0xD70280` -> `0xD70110` (`GC_is_marked` at `0xD70132`); one caller `0xD7047A` | read | the caller runs `GC_gcollect 0xE4A0B0` (stop function `0x5BFB90`) at `0xD70470` first. With forced collections kept full the walk sees exact marks |

### 8.4 Finalization and weak links

`GC_finalize 0xE4AE60`, tables: links `[0x11F3030]` (count `[0x11F3034]`, log size `[0x1194840]`), finalizable
`[0x11F3038]` (count `[0x11F3040]`), queue `GC_finalize_now [0x11F303C]`. `GC_java_finalization [0x119483C]` is 1 in
the image and has no writer. The only finalizer registration with callers is `GC_register_finalizer_no_order
0xE4AE30` (Mono, 3 sites); its mark proc is the empty function `0xC0FD60`. Weak links: Mono wrapper `0xE696C0` ->
`GC_general_register_disappearing_link 0xE4AAC0`, removal `0xE696E0` -> `0xE4ABE0`.

| Step | Code | Rule |
|---|---|---|
| 1 clear weak links | `0xE4AEB0`-`0xE4AF49` | target not marked **and** its block's stamp != `GC_gc_no` (EA's addition, `0xE4AEF3`): zero the link, drop the entry |
| 2 mark from finalizable objects | `0xE4AF60`-`0xE4B03C` | same two tests; calls the entry's mark proc, which is empty for every registered finalizer, so nothing is marked |
| 3 queue | `0xE4B060`-`0xE4B1C5`, `0xE4B32E` | not marked and stamp != `GC_gc_no`: move to the queue. Not marked but stamped: mark the object and what it reaches, keep it registered (`0xE4B0EF`-`0xE4B19F`) |
| 4 keep queued objects | `0xE4B1D8`-`0xE4B2CC` | every queued object and everything it reaches is marked |
| 5 drop links whose holder died | `0xE4B2E0`.. | holder = `GC_base` of the link address; not marked: drop the entry. No stamp test here |

Under sticky marks:

- An object marked in an earlier cycle stays marked through minor cycles, so its finalizer is not queued and weak
  links to it are not cleared until the next **full** cycle. There it is unmarked like any dead object and steps 1 / 3
  run as today. Nothing else can end its life: §8.3 lists every clear.
- A queued object is marked by step 4, so it is old from then on. After its finalizer ran it is freed by the next
  full cycle (today: by the next cycle). Resurrection needs nothing special: the store that resurrects it dirties a
  page or a root.
- Young objects (never marked) are finalized and their weak links cleared in the minor cycle in which they die,
  exactly as in a full cycle.
- The collector's own table entries and tables are heap objects reached from roots that are pushed in every cycle
  (`0xE4A9A0`); a new entry is linked by a write into the table or a neighbour entry, which dirties that page.

So no weak link is cleared and no finalizer queued for an old object in a minor cycle, and nothing is lost: the full
cycle does it. The cost is latency (bounded by the full-cycle policy) and that `GC.Collect` must stay a full cycle.
Script-visible difference: a weak reference to a dead old object keeps returning that (intact) object until the next
full cycle instead of the next cycle, and its finalizer runs that much later. Whether any script uses a weak
reference as a liveness signal is not checked [inferred harmless, I-523-5].

### 8.5 Rules for the minor cycle (lane GA)

| # | Rule | Why |
|---|---|---|
| 1 | Minor only when `GC_objects_are_marked [0x11F3080]` != 0; else full | after a mark-stack overflow the state-5 handler (`0xE4D3B0`) rescans all marked objects only if this flag is set; with 0 it would push uncollectable objects and roots only, and young objects reachable only through old ones would be freed. Writers: 0 in `GC_clear_marks` (`0xE4C27F`), 1 in `GC_mark_from` (`0xE4C40A`) and after the root push (`0xE4D2F9`), `0xE4D008`. It stays 1 across minors; assert it |
| 2 | Never read or reset the write watch outside `GC_initiate_gc`: no extra `0xE4EFB0` call, no `ResetWriteWatch`, no GetWriteWatch with the reset flag on a GC section; a verify pass must leave `0x1227714` as it found it | each read resets the watch, and only a state-1 rescue consumes the bits. A read whose bits are dropped loses old -> young stores (toy test, §8.8). An aborted finish attempt is fine: the rescue continues on the same bitmap and the next attempt reads again |
| 3 | The full-cycle bound (`GenFullEvery`) is a correctness bound: finalizers and weak links of old objects fire only in a full cycle. Forced collections (stop function `0x5BFB90`: `GC_gcollect`, heap exhaustion, the heap walk) stay full, and a full cycle runs the real `GC_clear_marks` | §8.4, §8.3 last row |
| 4 | Do not use `GC_is_full_gc [0x11F2FB8]` or `GC_need_full_gc [0x11F2FA4]` as policy inputs; keep your own "this cycle is minor" flag and your own heap-size baseline, written at notify 4 of each full cycle (`[0x12225A0]` - `[0x12225B4]`, the value `0xE49A79` uses) | EA uses `[0x11F2FB8]` as a "first mark end of this cycle" flag: `0xE49FEF` sets it to 1 at every cycle start, and `0xE49E03`-`0xE49E16` clear it (and `GC_n_attempts [0x11F2FB4]`) the first time the incremental mark stack runs empty. So `GC_finish_collection` sees 1 only when the cycle's first stopped mark completed (`0xE4A01B` path) and takes the "full" branch (`GC_need_full_gc` = 0, baseline = now) for a minor cycle too, while a full cycle that ran incrementally takes the other branch and never moves the baseline: `GC_need_full_gc` would then stay 1 and every cycle would be full. Leave the flag itself alone (the attempt counter needs it) |
| 5 | Do not stamp blocks and do not touch free bits in a minor cycle | §8.2 |
| 6 | Sweep and `GC_finalize`: no hook, no change | §8.1, §8.4 |
| 7 | Born-marked objects are legal | §8.2: a marked slot freed with `GC_free` and reused before a sweep visit. The new object's page is dirty (the allocation or `GC_free` wrote it), so state 1 rescans it; it lives until the next full cycle |
| 8 | Verify mode (full mark after a minor): one class of miss is not a collector bug: a live old object on a clean page that still holds the address of a slot which was `GC_free`d and reused. The full mark follows the stale address, the minor does not; the program holds no valid reference there. Log for each miss the block kind and whether the holder's page was dirty; a miss is FAIL until shown to be this class | toy test config "stale", §8.8 |
| 9 | With `[GC] MarkHelpers` or `SubTimers` on, chain; the three skipped calls must be skipped as a set | §5.1 |

### 8.6 Two EA hazards found on the way (present with full cycles, not caused by minors)

Both need a weak reference whose target the last mark found dead. The code paths are [verified], including the read:
`mono_gchandle_get_target 0xE78A70` jumps to `0xE69700` for weak handles, which returns the complement of the slot
(0 when cleared) with no lock, no mark and no test of the collector state. How often the game does this is
[inferred, I-523-1].

1. **Weak read between mark end and `GC_finalize`.** When the mark ends inside the per-tick step (`0xE49EDF`), the
   stop function is polled next (`0xE49F5A`); if the budget is spent, `GC_finish_collection` waits for a later
   call. Scripts run in between. A script that reads a weak reference to a dying object gets the object (the link is
   still set), stores it, and the deferred `GC_finalize` then clears the link and the sweep frees the object while it
   is referenced. EA protected allocations in this window (the stamp) but not weak reads.
2. **Weak link kept, referents swept.** Step 1 keeps the link when the dead target's block is stamped (someone
   allocated into that block after the mark end). The target survives this cycle, but the objects it points to are
   swept if their blocks are not stamped. A later read of the weak reference returns an object with dangling fields.
   Step 3 handles the same case for finalizable objects by marking from the object; step 1 does not.

Not exposed [inferred, I-523-2]: step 5 has no stamp test, so a link stored inside a heap
object allocated in the window would lose its registration; Mono's weak handle slots come from the host allocator
`0xD70A10` (table growth at `0xE7892C`), not from a `GC_malloc` entry.

Minor cycles do not add exposure: both hazards need an unmarked target, and old targets stay marked. Possible fix,
not built (own default-off switch, EA bug fix): in step 1 mark from an unmarked target in a stamped block instead of
only skipping it (as `0xE4B0EF` does), and run `GC_finish_collection` in the same step as the mark end at `0xE49EDF`
(cost: `GC_finalize` and the block walk of `GC_start_reclaim` land in that step). Effort: ~1-2 h build plus one
correctness job. Built 2026-11-18: §10 (`[GC] WeakHandleDiag` / `WeakHandleFix`, default 0); the stamp half is done at the
read instead of moving `GC_finish_collection`.

### 8.7 Notes for the gain gate (I-520-2)

- The sweep writes into every block where it reclaims a slot (free-list link, body clear), and allocation and
  `GC_free` write too. Those pages are dirty at the next read, so the next minor cycle rescans all marked objects of
  every block that lost or gained an object since the last cycle. The dirty share is therefore at least the share of
  blocks with churn, not only blocks with pointer stores [inferred from the code; number open, I-520-2].
- A block is returned to the block allocator only when it has no mark left. Old garbage keeps its block until a full
  cycle, so block reuse across sizes and kinds drops between full cycles.
- Born-marked objects and objects kept by dead old holders on dirty pages are extra floating garbage until the next
  full cycle.

### 8.8 Toy-heap selftest (`native/ts3native/gcgen-toy-test.h`, `TestGenGcToyHeap`)

Host-side model, no game code: blocks of 16 slots with stamp, mark bits, free bits and a kind (pointer-free, normal,
uncollectable); free lists that persist; the rules of §8.1-§8.4 for allocation, `GC_free`, the marker (free-bit test,
state-1 rescue of marked objects on dirty pages, overflow -> rescan of all marked objects), 1-3 stopped-mark attempts
per cycle with the mutator running between them, the window between mark end and `GC_finalize`, weak links, no-order
finalizers with resurrection, lazy sweep with stamp skip, free-bit mask and empty-block release. A full cycle clears
marks and stamps; a minor cycle does neither. Ground truth uses generation-tagged pointers, so a pointer to a
freed-and-reused slot does not count as a reference.

Checks: C1 every reachable object is marked at mark end (and, without stale pointers, the minor mark is a superset
of a conservative full mark); C2 the sweep never frees a reachable object; C3 no weak link is cleared while its
target is reachable, none is left pointing at a swept slot; C4 no finalizer is queued for a reachable object and no
finalizable object is swept unqueued; C5 after a full cycle no dead object still waits for its finalizer or weak
link.

Scenarios (deterministic): old -> young store on a clean page with and without the dirty bit; young garbage freed by
a minor, old garbage only by the full; `GC_free` of an old object with reuse (born marked) and without (mask); an
allocation after the mark end with and without the stamp skip; mark-stack overflow in a minor; finalizer and weak
link of an old and of a young object.

Randomized (seeded, about 2 s in total) [measured, standalone run 2026-11-18]: all-full baseline, 40 seeds
(validates the model: 0 errors); sticky marks with a full every 4 cycles, 150 seeds: 9,600 minor and 2,700 full
cycles, 501,935 slots swept, 21,197 born-marked objects, 39,478 finalizers, 54,573 links cleared, 4,180 blocks
released, 0 errors; mark stack of 6 entries, 60 seeds; stale `GC_free`, 100 seeds (1 stale-pointer miss, rule 8, 0
real ones); no window with a full every 16, 60 seeds.

Faults that must be caught, and are: a store that fails to dirty its page (2% of stores, 40 seeds: 11 errors, first
at C1), an extra dirty read (rule 2: 99 errors), a sweep without the stamp skip (243), never a full cycle (dead old
objects keep waiting, rule 3), and EA's weak-read hazards of §8.6 (reproduced with full cycles only: 78 errors; the
default mutator skips reads of dying targets so that every remaining error belongs to the sticky-mark change).

Not modelled: large objects (same rules: mark bit 0, stamp skip at `0xE5017F`), typed descriptors, the collector's
own table entries as heap objects, black lists, the 64K-bit dirty bitmap aliasing (only adds dirty pages), threads.
The model is a reading of the disassembly, not the game's code: it proves the rules are consistent, the live verify
mode (B3) proves the game follows them.
## 9. Build: minor cycles, counters, verify (lane GA, 2026-11-18, `native/ts3native/gcgen.cpp`; built, NOT RUN)

Build 0.36.286-gengc1, counters v215 (`gen*`, `0x6908`-`0x6987`), ledger §522. Steam 1.67 and retail 1.67 (retail addresses
read in the retail exe: code +0x2F0, data +0x1000 for every new site [verified]); EA 1.69: SKIP (no addresses).
Household rule: not applicable, the collector decides nothing about a Sim; the rule here is exactness (§9.3).

### 9.1 Switches (all default 0, `[GC]` in ts3native.ini)

| Key | Default | Effect |
| `Generational` | 0 | 1 = minor cycles allowed (`genMode` 1). Run-time switch: counter `genMode` (`--ab gcgen`) |
| `GenFullEvery` | 8 | full cycle after this many minors (1..64; an upper bound is needed: finalizers and weak links of once-marked objects fire only in a full cycle, §8 / lane GB) |
| `GenStats` | 0 | 1 = install with `genMode` 0 (every cycle full) and log one line per cycle; the counters of §9.4 run with either switch |
| `GenVerify` | 0 | N = check every Nth minor mark (§9.3) |

Refused (log line, `genFallback`): EA 1.69 table build; `[MonoThread]` Mode >= 1 without `GcLock=1`; any call site or byte
check that differs (nothing written). `[GC] MarkHelpers` stays out while `Generational` or `GenStats` is 1 (it shares
`0xE49FDF` and reads `0xE49A3E`); first version, a chained form is possible later.

### 9.2 Sites and the decision [verified in the disassembly]

| Site | Was | Wrapper |
| `0xE49FD5` | call `GC_promote_black_lists 0xE4A7C0` | `GenStartStub` (naked: `esi` = stop function, set at `0xE49FB1`, compared at `0xE49FC2`) -> `GenCycleStart`: decides, full = call the original |
| `0xE49FDA` | call `GC_invalidate_mark_state 0xE4C370` | `GenInvalidate`: skipped in a minor cycle |
| `0xE49FDF` | call `GC_clear_marks 0xE4C270` (or `[GC] SubTimers`' `GctClear`, chained) | `GenClear`: skipped in a minor cycle |
| `0xE49A3E` | call `GC_start_world 0xE51290`, success path of `GC_stopped_mark` only (abort path: `0xE499FA`) | `GenStartWorld`: full cycle = record heap in use; minor = GenVerify; then the original |
| `0xE4C2AB` | call `GC_gww_read_dirty 0xE4EFB0` in `GC_initiate_gc` | `GenReadDirty`: original, then count the bits of `0x1227714` |
| `0xE4D2CF` | call `GC_push_next_marked_dirty 0xE4D120` | `GenRescue`: count, original |

A cycle is minor only if all hold (`GenDecide`, selftest table): `genMode` 1, `GC_incremental`, `GC_dirty_maintained`,
mark state 0, `GC_objects_are_marked` != 0 (lane GB rule 1: else a mark-stack overflow restarts without the old
objects), mark stack empty, the last cycle's mark was seen to complete at `0xE49A3E`, every heap section answers GetWriteWatch, stop function != `0x5BFB90`, fewer than
`GenFullEvery` minors since the last full, and heap in use (`[0x12225A0]` - `[0x12225B4]`) grew by at most
`min_words_allocd 0xE498B0` words since the last full mark ended. The first cycle after install is full.

Findings while building [verified]:
- **`GC_is_full_gc` is not "this cycle is full".** `GC_collect_a_little_inner` clears it (and `GC_n_attempts`) at
  `0xE49E03`-`0xE49E16` the first time the incremental mark completes. So `GC_finish_collection` takes the non-full
  branch for every cycle that went through incremental steps, and `GC_used_heap_size_after_full [0x11F2FAC]` is only
  refreshed by cycles whose first stopped mark completed. `GC_need_full_gc` is therefore not a usable policy input
  (§1.1 / §5.3 corrected); the build keeps its own baseline, taken at `0xE49A3E` of each full cycle.
- **A section without write watch fails safe in EA's code.** `0xE4F010`: GetWriteWatch != 0 -> `0xE4F018`-`0xE4F09C` sets
  the bit of every page of that section (one warning per section start, five at most). Dirty-page bits alias every
  256 MB (index `(addr >> 12) & 0xFFFF`): superset, safe. The build still probes each section's first and last page
  (flags 0: no reset, selftest) at every cycle start and runs full cycles while one fails (`genUnwatched`).
  Apex Radiance 4.0.4 (`ApexRadiance.asi`, byte scan of the release file): no immediate of the host allocator
  (`0xD70900`, `0xD70906`, `0xD70800`), no `GetWriteWatch` / `ResetWriteWatch` import, two references to the section count
  `0x11F2FC0` (a reader), one to `0xE4A050` (the known profiler detour) [verified scan; a hook on `VirtualAlloc` itself
  would not show this way, I-520-4 stays open until the queue reports `genUnwatched` 0].
- Static-field blocks, mint arena chunks and uncollectable cells are heap objects (P0b; §3.2 row 1 corrected): old and on
  a clean page they hold no pointer to a young object, as any old object. Pages outside the heap count as dirty
  (`0xE4F18E`: no block header = dirty), so every registered root range is pushed in full in a minor cycle.
- An aborted minor stopped mark leaves the mark state in progress; later ticks continue it (`0xE49F49`) on the same
  dirty bitmap and the next attempt reads the write watch again: the path today's finish attempts take.

### 9.3 GenVerify

At `0xE49A3E` of every Nth minor cycle (mark complete, world stopped, nothing swept): copy all mark words (M_minor) and
both incomplete black lists, clear the marks of collectable blocks, run one full mark with EA's own `GC_mark_some
0xE4D460` from state 5 (M_full), compare. A bit in M_full that M_minor lacks = an object the minor cycle would free and a
full cycle keeps: `genVerifyMiss`, first 8 logged (block, kind, size, mark words, first page dirty or clean), the union of
both marks stays (nothing live is freed), `genMode` latched 0 (`genLatched`). No miss: M_minor is restored, so the cycle
equals a plain minor. The world-stopped flag `[0x11F2FA8]` (cleared at `0xE49A34`, just before the replaced call) is 1 during the check. Black lists and mark globals are restored; the dirty bitmap is not touched and no write watch is
read. `genKeptMinor` - `genKeptFull` of the last check = old garbage a minor cycle carries.
Time limit 60 s or a fault: M_minor restored, `genVerifySkip`. Log lines after the world restarts.
Expected false-positive class (lane GB rule 8, I-522-3): an old object on a clean page with a stale pointer to a slot
that was `GC_free`d and reused. A miss is FAIL first; then check that class ("clean" page, kind of the missed block).

### 9.4 Counters v215

`genMinor` / `genFull` / `genFullReason[4]` (not eligible, forced, every N, heap grew); `genReads`, `genDirtyPages`,
`genDirtyMax`, `genReadUs` (all cycles), `genReadsMinor`, `genDirtyMinor`; `genRescue`; `genMinorMarkUs` /
`genFullMarkUs` (mark + stop time from `[GC] SubTimers`, closed at the next cycle start; 0 without SubTimers);
`genVerifyN`, `genVerifyMiss`, `genVerifySkip`, `genVerifyMs`, `genLatched`, `genKeptMinor`, `genKeptFull`;
`genUnwatched`, `genFallback`. `0xE4E4AB` (the one read in `GC_enable_incremental`) is not wrapped.
Gain gate (I-520-2): `genDirtyPages / genReads` against the heap's page count, and `genMinorMarkUs / genMinor` against
`genFullMarkUs / genFull`, both from the correctness queue (`GenStats=1`, `SubTimers=1`).

### 9.5 Selftest and queue

- `gcgen-test.h` (`TestGcGen`): decision table; the three cycle-start wrappers on a fake collector (all skipped or all
  run, reasons, baseline, latch arm); write-watch probe on real `MEM_WRITE_WATCH` and plain regions (and that the probe
  does not reset the watch); bit count; verify comparison. The stopped-world full mark itself has no host test.
- `tools/harness/queues/gengc-verify-correct-1118.json` (written, NOT LAUNCHED): smoke BP 5 min `GenVerify=1`, then
  BP / IP / SV 10 min `GenVerify=2`, all `Generational=1 GenStats=1 SubTimers=1`. Pass: script errors 0,
  `genMinor` >= 1, `genVerifyN` >= 1, `genVerifyMiss` 0, `genVerifySkip` 0, `genUnwatched` 0, no FAIL / WARN / SKIP line.
  Each check is a visible hitch; frame times of this queue mean nothing. Timing queue: only after this one is clean.

### 9.6 Open

| # | Question | How |
| 1 | does verify stay at 0 misses on BP / IP / SV, and on a long-history save | the queue; then a long save |
| 2 | gain (I-520-2) | §9.4 ratios from the same jobs |
| 3 | other readers of mark bits see old garbage between full cycles (EA heap walk `0xE4A3F0`, host `0xD70132`; lane GB rule 10) | trace both callers |
| 4 | heap growth between full cycles on a big save (old garbage waits up to `GenFullEvery` cycles) | `gcgHeapMaxKB` on vs off in the timing queue |
| 5 | `[GC] MarkHelpers` together with minor cycles | chain `G5Reset` after `GenClear`'s decision; not built |
| 6 | GenVerify memory: 136 bytes per heap block for the mark copies (about 14 MB at a 400 MB heap), allocated with the world stopped | debug switch only; on a full address space the check is skipped (`genVerifySkip`), never run short |

## 10. `[GC] WeakHandleDiag` / `WeakHandleFix`: EA's weak-reference hazards (lane GB, 2026-11-18, built, NOT RUN)

Counter and fix for the two hazards of §8.6. `native/ts3native/gcweak.cpp`, both switches default 0, nothing written
at 0. An EA bug fix inside the collector: the same for every Sim, no household exemption applies (the collector
decides nothing about a Sim); with the fix off or no hit the game is unchanged.

### 10.1 Hooks [verified, Steam 1.67]

| Site | Was | Now |
|---|---|---|
| `0xE78AC7` in `mono_gchandle_get_target 0xE78A70` (weak handle types) | `jmp 0xE69700` (`mono_gc_weak_link_get`: returns the complement of the slot, 0 when cleared) | `jmp` to `WkGet`, which calls `0xE69700` and then classifies the target |
| `0xE49A63` in `GC_finish_collection` | `call GC_finalize 0xE4AE60` (or SubTimers' timer) | `call WkFinalize`: pre-pass over the weak-link table, then the previous target |

Not hooked: the second caller of `0xE69700`, `0xE7898D` (handle table growth, collector disabled around it). Byte
checks before anything is written: `0xE69700`, `0xE4AEC3` (entry layout), `0xE4AEF3` (stamp test), `0xE4C140`,
`0xE4C2F0`. Other builds: SKIP (the checks embed Steam data addresses; retail 1.67 / EA 1.69 not ported).

### 10.2 What it does

Read hook, for a target that is in the heap, not on a free list and not marked:

| State | Counter | Fix |
|---|---|---|
| finish pending (`[0x11F2F94]` != 0 and `[0x11947E4]` != -1), block not stamped | `windowHit` (hazard 1) | stamp the block (`+0x18` = `GC_gc_no`, the store every allocation into the block does): `GC_finalize` keeps the link, the sweep skips the block |
| finish pending, block stamped | `windowStamped` | none needed, the pre-pass covers it |
| mark running (`[0x11F3078]` != 0) | none | none: unmarked is normal, roots are rescanned at the mark end |
| no finish pending, block stamped | `lateStamped` | none: with the fix off this is the exposure to hazard 2 plus reads of young objects; with the fix on only young objects remain |
| no finish pending, block not stamped | `anomaly` | none: `GC_finalize` should have cleared such a link; must stay 0 |

Pre-pass before `GC_finalize` (collector thread, mark state 0, mark stack empty): for every weak link whose target is
unmarked in a stamped block (`kept`), the fix marks the target and everything it reaches (`markedFrom`) with the
game's own routines in EA's order for finalizable objects (`0xE4B0FC`-`0xE4B19F`): push (object, descriptor), run
`GC_mark_from 0xE4C3E0` until `0xE4C2F0` says empty, on overflow set the bit and run `GC_mark_some 0xE4D460` until
done, then `GC_set_mark_bit 0xE4C140`.

Effect of the fix: a weak target that already kept its link for a cycle now also keeps its referents, and a target
read in the window survives that cycle. Nothing is freed earlier; an object that nobody reads is freed one cycle
later at most (with `[GC] Generational=1`: at the next full cycle, because the fix marks it).

Log (ts3native.log), at the first cycle, every 16th cycle and whenever `windowHit` or `anomaly` moves:
`gcweak: cycles= reads= unmarked= windowHit= windowStamped= lateStamped= anomaly= offThread= kept= markedFrom= fix=`.
Counters are log lines, not `ts3native_counters` fields (no layout change, no run-time `--ab` switch).

### 10.3 Exposure [verified grep, I-523-5 closed for these sources]

- EA's script DLLs (decompiled ScriptCore, SimIFace, Sims3GameplaySystems, Sims3GameplayObjects, Sims3Metadata,
  Sims3StoreObjects, UI, System, System.Xml): one user of `WeakReference`, `ReferenceWatchdog`; its `RegisterObject`
  is compiled out of the release build (`[Conditional("EA_DEBUG")]`) and it calls `GC.Collect()` before it tests
  `IsAlive`. mscorlib has only the class itself.
- NRaas source (local copy): no `WeakReference`, no weak `GCHandle`.
- So no script logic in the vanilla game or NRaas uses a weak reference as a liveness signal (the generational
  switch's later clearing, §8.4, changes nothing there), and the two hazards are reachable only through other mods
  or a native user we did not find. Expected in a vanilla run: `reads=0`. The counter is the proof; the fix is cheap
  insurance and stays off until a run shows reads [inferred until run, I-523-1].

### 10.4 Selftest

- `TestGcWeakHandle` (`gcweak-test.h`): the classification, header bit math, block-index lookup, the read hook
  (counts, stamp only with the fix and only in the window) and the pre-pass on a fake weak-link table and mark stack,
  including the overflow path and "no table".
- `TestGenGcToyHeap` (§8.8) models the fix: the mutator reads dying weak targets at any time (the hazard
  configuration, 78 errors without the fix); with the fix 300 seeds with full cycles and with sticky minors plus 40
  seeds with a 6-entry mark stack: 4,042 dying reads, 3,349 blocks stamped, 26,582 targets marked, 0 errors
  [measured on the model].

## 11. `[GC] GenCards`: card table, write-watch union, verify scan (lanes W + V, 2026-11-18, built, NOT RUN)

Build 0.36.288-gccard1 (on 0.36.287-gcweak1), counters v216 (`gcc*`, `0x6988`-`0x69C7`), ledger §525. Steam 1.67 and
retail 1.67 (no address of its own: everything goes through `gcgen.cpp`'s tables); EA 1.69: off with `gcgen.cpp`.
Household rule: not applicable (the collector decides nothing about a Sim). In this step the collector's input is
unchanged: with `GenCards=1` it still rescues by EA's dirty-page bits, so the result equals `GenCards=0`.

### 11.1 Contract for the barrier lanes (`native/ts3native/gccard.h`)

- Table: one byte per 512 bytes of address space, index `addr >> 9`, 8 MB for the whole 4 GB, so a mark needs no bounds
  check. Reserved and committed in one call (demand-zero pages: memory only where marks land). Not committed on demand
  as §7 first said: an inline `mov byte ptr [edx + cards], 1` in compiled code cannot branch to a commit path. Never
  freed or moved; the address may be baked into stubs.
- `GcCardIniMode(ini)`: a lane's install decision (>= 1: install stubs; allocates the table). 0 unless `GenCards` is 1 or
  2 and `Generational=1` or `GenStats=1`.
- `GcCardMark(slot)`, `GcCardRange(addr, bytes)`, `GcCardObj(obj)` (whole object; size from the block header through
  the module's own copy of the block index; no header = one card). Idempotent byte stores, no lock, any thread.
  A mark without a store is allowed. Mark the slot written, not the value.
- Switch: `[GC] GenCards` 0 off / 1 union + counters (mode 2) / 2 cards first (mode 3): **not built**, read as 1 with a
  WARN line.

### 11.2 The read (`gccard.cpp`, no hook site of its own)

`GcCardOnRead` runs at the end of `gcgen.cpp`'s wrapper of `GC_gww_read_dirty` (`0xE4C2AB`), so after EA's own read and
never as an extra write-watch read (§8.5 rule 2). Per heap section (`0x122D714`), per 4 KB page: dirty bit
(`0x1227714`, index `page & 0xFFFF`) against the page's 8 card bytes; counters; snapshot byte `snap[addr >> 12]` = card
bits, or `0xFF` for a dirty page (the union: mode 3's input later, 1 MB); cards cleared. The module keeps its own copy of
the block index (`[addr >> 22]` = bottom index, from the list at `[0x11F3054]`), refreshed at every read.

Stand-down (install check): at the first read, and whenever the section count changes, every section's first and last
page must answer `GetWriteWatch` (flags 0, `GenWatched`). If not: `g_gcCardsOn` 0 for the session, `gccRefused`, one
WARN line. Apex Radiance owns the allocator and the page hooks; `gcgrow.cpp` holds no section flag (grep: no
`MEM_WRITE_WATCH` there), the flag is the host's at `0xD70906` [verified §1.4], so the probe at run time is the check.

[verified, Steam 1.67 disassembly, read for this build]
- `GC_install_counts` (`0xE4B775`-`0xE4B7AC`): the blocks of a large object after the first get `min(blocks back,
  0xFFF)` as their index entry, written through the top index `0x1233318[addr >> 22]` (a direct 1,024-entry array) at
  `[(addr >> 12) & 0x3FF]`. `GccHdr` walks these counts back to the header.
- `GC_reclaim_clear 0xE4F3D0`: objects of a small block start at word 0 and repeat every `size` words while
  `p <= block + 0x1000 - size * 4`; mark bit `[hdr + 0x1C]` and free bit `[hdr + 0x9C]` are indexed by the word number.
  `GccScanPage` / `GccYoung` use the same rule.

Dirty bits alias every 256 MB (§9.2), so `gccDirtyPages` counts real pages whose bit is set and can exceed
`genDirtyPages`; an aliased clean page counts as dirty without a card.

### 11.3 Counters v216

`gccMode` (1 = reading; 0 after a stand-down), `gccReady`, `gccRefused`, `gccReads`, `gccDirtyPages`, `gccBothPages`
(dirty with a card), `gccNoCardPages` (dirty without: scalar-only store, aliased bit, or a writer no lane hooks),
`gccCardOnlyPages` (card without dirty bit: a mark without a store, or a mark that raced the read), `gccCards`,
`gccMergeUs`, `gccVerifyPages`, `gccVerifyMiss`, `gccVerifySites`, `gccVerifyUs`, `gccVerifyFault`.
Gain question for mode 3 (I-521-8): `gccNoCardPages / gccDirtyPages` once every lane's stubs are in. With lanes W + V
alone no store is hooked, so every dirty page is "without a card".

### 11.4 Verify (lane V)

1. **Dirty-without-card scan** (`GenVerify` != 0 and `GenCards` >= 1): at each read whose mark state was 0 before it,
   every marked, not-free object on a dirty page without a card is scanned word by word (pointer-free blocks, kind 0,
   skipped) for the first byte of an allocated, unmarked object. A hit = a reference stored into an old object since
   the last read that no card recorded: `gccVerifyMiss`, and per (first word of the holder = its vtable, field offset)
   one line at count 1, 10 and 100: `gengc verify miss: class <Ns.Name> field +0x.. count N (block kind, object words,
   last target)`. Lines are written outside the stopped world (next cycle start or after `GC_start_world`).
   Why state 0: the read is the first thing `GC_initiate_gc` does; state 0 means the previous mark ran to the end, so
   every reference a marked object held at the previous read was followed [inferred, I-525-1]. State 5 (full cycle
   start: nothing marked) and a mark in progress are not scanned.
   Not a miss by construction: target marked, target slot free (stale pointer, §8.5 rule 8), target not an object
   start, holder unmarked (young objects are found through the roots or an old holder).
   Known false hit: a scalar word equal to a young object's address (the scan is conservative like the collector; typed
   objects are not read through their bitmap) [I-525-4].
2. **Cross-check**: `gcgen.cpp`'s `GenVerify=N` full mark after every Nth minor mark (§9.3), unchanged: `genVerifyMiss`
   must stay 0. It checks the write watch (the remembered set in mode 2); the scan checks the cards.

### 11.5 Open

| # | Question | How |
|---|---|---|
| 1 | misses per class once lanes M / J / R / E are in | queue `gengc-verify-1119` on the combined build |
| 2 | mark-before-store or store-before-mark for mode 3, and the clear racing a mark from another thread (the clear is one atomic exchange per page, so no mark is lost; but a card set between EA's read and the exchange is consumed one read before its dirty bit, which shows as one card-only page now and one dirty page without a card at the next read: a possible false miss line, other threads only; harmless in mode 2) | decide with mode 3; today sim-thread stores cannot interleave with a read on the sim thread [inferred, I-525-2] |
| 3 | size of the alias inflation in `gccDirtyPages` | compare with `genDirtyPages` in the first run |
| 4 | cost of the page walk per read (`gccMergeUs / gccReads`; about 100,000 pages at a 400 MB heap, up to 42 reads per cycle) | first run; a walk over the set dirty bits and the touched card pages is the cheaper form if it shows |

### 11.6 Selftest and queue

- `gccard-test.h` (`TestGcCard`): marks and ranges; a real `MEM_WRITE_WATCH` section with stores, cards, the union
  snapshot, counters and the clear; `GcCardObj` sizes; the stand-down on an unwatched section; the scan on fake
  headers (hit, card present, state not 0, target marked / free / interior / free block, large object as holder and as
  target, holder unmarked / free, pointer-free block, log thresholds 1 / 10 / 100).
- `tools/harness/queues/gengc-verify-1119.json` (written, NOT LAUNCHED): `bp-gccard-union` (this build: counters and
  the cross-check; miss lines are the measured list, not a FAIL), then `bp-gccard-verify` and `ip-gccard-verify`
  (strict: any `gengc verify miss:` line or `genVerifyMiss` > 0 fails). The strict jobs fail by design on a build
  without the stub lanes: repin them to the combined build.
## 12. Card marks: mint stubs and compiled stores (lanes M + J, 2026-11-18, `gcbarrier.cpp`, `monojit.cpp`; built, native selftest ok, monohost compiles, NOT RUN)

Second source of the remembered set next to the write watch (§7.8 mode 2). Nothing here decides what is collected:
lane W (§11) reads the table. No new switch: both parts install when `GcCardIniMode()` (gccard.h) says cards are on
(`[GC] GenCards >= 1` next to `Generational=1` or `GenStats=1`, all default 0). Household rule: not applicable, a card
byte decides nothing about a Sim.

### 12.1 Mint stubs (`gcbarrier.cpp`) [verified: handler disassembly, Steam 1.67]

Jump-table swap (table `0xE59880`), all entries checked first, one mismatch = nothing written.

| Opcode | Handler | Stub | Slot marked |
|---|---|---|---|
| `stfld.o` 0x61, `stfld.p` 0x62 | `0xE56BE7` | fast, 6 instructions, `ecx` only | obj `[esi-16]` + u16 `[edi+2]` |
| `stind.i4` 0x93, `stind.ref` 0x98, `stind.i` 0x95 | `0xE559A7`, `0xE559EB` | fast | address `[esi-16]` |
| `stelem.i` 0x113, `stelem.ref` 0x11A | `0xE574DA` | fast | array `[esi-24]` + 0x10 + index * 4 |
| `stfld.vt` 0x63, `strmfld.vt` 0x65, `stobj.vt` 0x100, `stelem.any.vt` 0x112, `cpblk` 0x101 | `0xE56C0A`, `0xE56CA9`, `0xE57089`, `0xE575CF`, `0xE58A17` | `pushad` + C, range from Satb's decode | every card of the range |

- The decode is Satb's, moved unchanged from `satb.cpp` to `satb-decode.h` (one copy, two includers).
- Differences from the brief, with reasons:
  - `stelem.i1` .. `stelem.r8` (0x114-0x119) share handler `0xE574DA`, but the table has one entry per opcode, so
    only the two 4-byte pointer-capable forms are swapped. Marking scalar element stores would only add false cards
    to the mode 2 counters [inferred: scalar arrays are pointer-free objects, never scanned].
  - `stsfld` 0x6A, `stsfld.vt` 0x6B, `strmfld` 0x64 and `stobj` 0xFF get no table stub: each stores only through
    `stackval_to_data` [verified: `0xE56FC2`, `0xE56FDF`, `0xE57070`, `0xE56C99`, `0xE570EB`; the remoting branch of
    `strmfld` never runs in this build].
- **I-521-4 closed, and the census had it wrong**: `stackval_to_data 0xE52350(type, val, data, pinvoke)`: `[esp+4]` is
  the MonoType (byref bit of `+4`, type byte `+6`), `[esp+8]` is read, `[esp+0xC]` is written in every case. The
  destination is argument **3**. Entry detour (11 bytes, two whole instructions, no branch into them): one card at
  `data`. The value-type cases copy with `call memcpy` at `0xE52423` (both paths, `0xE523F8` and `0xE5244B`, join at
  `0xE52413`): that call is retargeted to mark the exact range, then copy. Covers the four opcodes above, the runtime
  array `Set` (`0xE53A7E`), the invoke result (`0xE5A583`), `0xE5725E` and EA's static setter (`0xD89C50`: lane E must
  not hook it again).
- A size the decode cannot give (`stobj.vt` of a class without a layout): the whole object is marked (`GcCardObj`) and
  counted in a `gcbarrier:` log line at 1, 10, 100, 1000. Not expected in play.

### 12.2 Layering [verified in each module's install code]

The stubs are the bottom layer for the whole session: `ApplyGcBarrier` runs before `ApplyMintFuse` / `ApplyMintCall` /
`ApplyMonoJit` / `ApplyAutShadow`.

| Module | Table entries | Change |
| `[AutShadow] Satb` (`kSatbOps`) | the same store opcodes | `SatbInstall` accepts `GcbOwns()` as the game's handler and chains (`g_satbOrig` = our stub). The worker's emulated store never runs the entry: `SatbWorkerStore` marks the range itself |
| `[MonoFast] CallPath` | 0x06, 0x07, 0xEA, 0xEC | none (other opcodes). Verify mode copies the table as found, so it wraps our stubs |
| `MintDivGuard` | 0x127, 0x143 | none |
| `[MonoJit]` | 0x1A9 | none at install: exits and `JitOrig` read the live entry, so an uncompiled store opcode meets our stub. monohost's `g_opHandler` snapshot is taken after the stubs are in |
| `[MintFuse]`, `[MonoFast] PairCount` | none | none: fusion only rewrites into EA's superops, the census only counts at the dispatch head |

I-521-9: `mintfuse` and `mintpairs` have no store of their own [verified, whole files]. `mintcall`'s four handlers
write the frame record, the arena and the caller's eval stack [inferred from its header and its verify mode, which
compares exactly those words; the asm was not re-read store by store]: arena chunks are write-watch material (§7.2).

### 12.3 Compiled stores (`monojit.cpp` `Em::card`, same code in `monohost-jit.cpp`)

`Em::satb()` is the one place every compiled heap store passes (11 sites), so the mark is emitted there, after the
Satb block: `lea edx,[base+disp]; shr edx,9; mov byte ptr [edx+cards],1` (15 bytes, no call, no branch; `edx` and the
flags are scratch at every site). Decided at compile time: `g_jitCards` is set in `ApplyMonoJit` before any compile.

- Marked: 4-byte integer / pointer stores (`stfld.o`, `stfld.p`, `stind.i`, `stind.ref`, `stelem.i`, `stelem.ref`,
  and `stsfld` / `stobj` when the field type is 4 bytes wide) and the value-type copies (`stfld.vt`, `stelem.any.vt`,
  `stobj.vt`: first and last byte's card up to 512 bytes, a `JitCardRange` call above that).
- Not marked: the `.i4` forms (by opcode name), 1 / 2 / 8-byte and float stores, `initobj` (zeros), and the inlined
  `Array.FastCopy` (`JitFastCopy` only copies value types without references, any other input exits to EA's icall,
  which is lane R's).
- Code size: +15 bytes per marked store (+30 for a value type), inside the 256 bytes per position bound.

### 12.4 Selftests

- `TestGcBarrier` (`gcbarrier-test.h`, native selftest): fake table, fake eval stack and frame; every stub marks
  exactly the cards of its slot and reaches the handler with `eax` / `edx` / `edi` / `esi` unchanged; null object and
  index out of range mark nothing; a foreign table entry refuses the whole install; the `stackval_to_data` hook body
  and the copy wrapper. Not covered there: `strmfld.vt` (its decode reads the game's proxy class), the two real sites.
- `TestMonoJitCards` (`monojit-test.h`): compiled `stfld.o` marks one card, `stfld.i4` and a null exit none,
  `stfld.vt` the first and last byte's card.
- monohost: `MONOHOST_GCCARD=1` installs the table stubs under MintCall / Satb / the JIT (no `stackval_to_data`
  detour: the host's `InstallDetour` is a stub) and turns the compiled marks on; check `GC-card-stubs` (cards set),
  log line `jit: card marks: N`. Every existing check (AS-satb-exact, the JIT corpus) must still pass with it on.

### 12.5 Open

1. Host run with `MONOHOST_GCCARD=1` together with `MONOHOST_SATB` and `MONOHOST_JIT` (not run: no host tests tonight).
2. Game run: lane V's verify queue (§11) is the check; no queue of its own here.
3. Retail 1.67 / EA 1.69: no table row was added. `0xE52423` is taken as `TS3A(0xE52350) + 0xD3` and byte-checked,
   so a build where the function differs refuses the whole install.
4. Satb's overlay path (a worker store the sim side logged first) is marked when the worker stores, not when the
   overlay is applied; in mode 2 the write watch covers the gap. Recheck before mode 3.
5. `stind.i4` 0x93 is stubbed with `stind.ref` (same handler, brief); if mode 2 shows it as the main false-card
   source, drop its row.
## 13. Card marks for native reference stores: lanes R + E (2026-11-18, `native/ts3native/gccardrt.cpp` + clients in `satb.cpp`; built, selftest only, NOT RUN)

Branch `feature/gccard-re-1118` (on `feature/gc-weakfix-1118` + lane W's contract commit `bdb3af444`). Ledger docs/51 §527.
No switch of its own: on when `GcCardIniMode(ini) >= 1` (gccard.h: `[GC] Generational=1` or `GenStats=1`, and `GenCards>=1`). Default: nothing hooked, nothing written.
Household rule: not applicable. A mark is a byte store in our own table; the collector decides nothing about a Sim.
Counters are one log line (`gccard rt: field=... bind=...`, every 2^20 binder calls), not `ts3native_counters` fields: no layout change.

### 13.1 Lane R: Mono runtime writers

One dispatcher per hooked address, shared with `[AutShadow] Satb`: the Satb wrappers are the dispatchers (they pass through while no Satb span is on, `g_satbOn == 0`) and each reference-capable one got a card client. `SatbInstallRt` now runs once, whoever asks first.

| Site | Hook | Mark | Tag |
| `0xE69820` store helper (80 callers) | Satb's whole-function detour `SatbFieldStore` | the slot's card, after the store | [verified] body `mov eax,[esp+0xC]; mov ecx,[esp+8]; mov [ecx],eax; ret` |
| `Array.FastCopy` `0xE7A070` | icall table wrapper `SatbRtFastCopy` (table index 37) | destination range, when the copy succeeded and the element size is 4 or more | element-size filter instead of "class has references" [design: an element under 4 bytes holds no reference; `int[]` copies are marked too, superset] |
| `Array.SetValueImpl` `0xE793D0` | **body detour** (6 prologue bytes `83 EC 18 D9 EE 53`) to the same wrapper `SatbRtSetValue`; the table entry (index 45) is put back to the body, so there is one path | one element | [verified] prologue; `0xE79CE0` calls the body directly at `0xE79D77`, `0xE79E05`. With cards on, Satb sees those direct calls too (it missed them before) |
| `Interlocked.Exchange` `0xEBA1F0`, `CompareExchange` `0xEBA2B0` | table wrappers `SatbRtXchg`, `SatbRtCmpx` | the slot's card | [verified] each body sits at four table entries (`0xEBA1F0`: 490, 492, 493, 495; `0xEBA2B0`: 481, 483, 484, 486): the int, object, IntPtr and generic forms share one body. The float, double and 64-bit forms store no reference |
| `MonoField.SetValueInternal` `0xE7B320` | `SatbRtFieldSet` | the field (`SatbFieldDest`), computed after the call; an unsized kind marks the slot's first card and the whole object | docs/206 §10 |
| `MonoMethod.InternalInvoke` `0xE7CDC0` | `SatbRtInvoke` | the `params` array's elements (out / ref write-back) | docs/206 §15 |
| `Marshal.PtrToStructure` `0xE99A50`, `construct_internal_locale_from_lcid` `0xE874B0` | `SatbRtPtrToStruct`, `SatbRtLcid` | the whole object | docs/206 §10, §15 |
| `get_property_info` `0xE7B4D0` | `SatbRtPropInfo` | the 20-byte struct | docs/206 §15 |
| `Array.GetGenericValueImpl` `0xE7A300` | `SatbRtGetGeneric` | the `out` location (element size 4 or more) | added: the element is copied to a byref that may be a field [inferred: rare] |
| GC handle allocation `0xE78780` | call-site retarget of the two strong callers `0xE7903D` (normal, table `0x1195A10`) and `0xE79051` (pinned, `0x1195A28`) to a naked wrapper | the slot's card `[table+4] + (handle >> 3) * 4`, after the store | [verified] `esi` = table, one stack argument, store at `0xE78A38`, return `slot << 3 \| type + 1` at `0xE78A54`-`0xE78A64`, type byte `+0xC`. Byte checks at `0xE78A38`, `0xE78A54`. Steam 1.67 only (four zero rows in the other tables) |

Not hooked, as the brief says (write watch + verify decide): boxing `0xEA3140`, clone `0xEA2710` / `0xEA3800`, allocation, the 154 runtime `memcpy` sites, handle table growth (`0xE78903`: the new slot array is fresh). `Array.Clear`, `Buffer.BlockCopy`, the string copies and `Marshal.Write*` wrappers got no client: they cannot create a reference.

### 13.2 Lane E: EA native writers

| Site | Hook | Mark |
| binder dispatch `0xE525C0(frame, ctx)` | Satb's detour; `SatbBindCall` is now the dispatcher: card client around `SatbBindInner` (Satb rows 2, 3, 5, 6, 12). `[MonoJit] BoundCall` calls `0xE525C0` itself (`g_jitBoundEntry`), so it passes here | `this` (frame `+0xC`) and every argument dword (frame `+0x14`, 8-byte slots, count = RuntimeMethod `+0x40`) that points into a heap block: whole-object marks, **before and after** the body; `this` and the first 12 arguments stay on a per-thread in-flight list in between (§13.3) |
| `PriorityQueue.Add` `0xE0D450`, `RemoveAt` `0xE0C640`, `SetCapacity` `0xE0D290` | matched by body address as Satb row 3 | also `this.mItems` (`+4`), one load deep |
| `Hashtable.Rehash` `0xDCA870`, StringBuilder `InternalEnsureCapacity` `0xDD4ED0` | no special case | `this` covers them (the new bucket array / string is fresh, the store is into `this`) |
| 180 EA ICs with byref / array parameters (`satb-ics.h`) | the guard thunk's callee is now `SatbIcDisp` (Satb's `SatbIcSim` while a span is on, the card client always) | each byref / array argument that points into a heap block; a byref array also the array it holds. **Before the body only** (the thunk tail-jumps). Needs the guard thunks: `[AutShadow] Enabled=1` (the install line says so when they are missing). Worker-thread IC calls skip the thunk's hook (unchanged) |
| static address-of `0xD89C60` | Satb's whole-function replacement `SatbStaticAddrOf` (installed alone when Satb is off) | the slot's cards (type size, 4 when unknown) at the call |
| static set `0xD89BF0` | none here | lane M's `0xE52350` detour covers it (`SatbStaticSet` calls `0xE52350` by address) |

A value is "in the heap" when gccard.h's copy of the block index has a header for its page (`GcRtHdr`); a page with a forwarding count (large-object tail) marks one card; a free block, a stack or static address or a region the index has not seen marks nothing. So scalars that look like addresses cost nothing unless they hit a live block (then they mark it: superset).

Coverage limit [inferred, I-527-2]: a body that stores into an object reached through a load from `this` or an argument (other than the three PriorityQueue bodies) is not marked. `docs/data/binder-bodies.csv` column `ref_stores` (§13.4) lists the candidates.

### 13.3 Mark order: what a cards-first mode needs [design, from the hook shapes; not measured]

gccard.h leaves the order open for mode 3. What the native hooks show:

1. One store instruction, mark **before** only (the mint stubs of lane M): safe at a stopped read (the value is still on the evaluation stack or in a register, so the mark that follows finds it and it is old from then on). Open only when a dirty read on another thread, not stopped, falls between the mark and the store: the card is consumed, the store follows, and nothing marks it again before the final stopped read.
2. One store, mark **after** only: open when the world stops between the store and the mark and the value is in no root any more.
3. Mark before **and** after closes both for a single store, except a stop exactly between store and post-mark with the value dropped from every register (conservative stack scan makes this unlikely, not impossible).
4. A long native body (binder body, IC, `InternalInvoke`) can allocate, so a dirty read can happen **inside** it, after one of its stores and before the next. Pre + post marks do not cover a store that follows an in-body read. The in-flight list does: gccard.cpp calls `GcRtFlyMark()` before it reads and clears the cards, and every listed `this` / argument is marked again.

Built here: pre + post + in-flight for binder bodies; post for the icall wrappers and the store helper; pre only for ICs and the static address-of. So for mode 3 the open items are: ICs (need a return hook: a shadow return stack in the guard thunk, entries dropped by stack address after an unwind, as the in-flight list does), the static address-of (the store comes later in the caller), `InternalInvoke`'s write-back, and the single-store cases 1-2 for every lane. Mode 2 depends on none of it.

`GcRtFlyMark` wiring: gccard.cpp (lane W) declares `void GcRtFlyMark();` and calls it at the top of its dirty-read handler. Not wired on this branch (gccard.cpp is not here). Limits of the list: 64 threads, depth 32, 12 arguments; past that `flyLost` counts and the call has only its pre and post marks. A cards-first mode must refuse while `flyLost > 0`.

### 13.4 `docs/data/binder-bodies.csv` column `ref_stores`

`tools/binder-bodies.py` now keeps the value of each store and tags it by provenance (4-byte stores only): `ref` = an allocation result (a fresh object: the old-to-young case), `word` = a dword loaded from an object, an argument slot or static data (a reference or a copied scalar), `unknown` = unresolved, pointer arithmetic, a pointer loaded from native memory, a block copy or a store of unknown size, `scalar` = constants and every store that is not 4 bytes. The column lists each managed target (the entries of `writes_args` and `writes_other`) as `target=tag`, strongest tag per target. The raw JSON (`--json`) carries the tag as a fourth element of each `w` entry; a file without it reads `unknown`. Self-check extended.

Regenerated CSV (1,085 rows; the older columns are kept as committed, classes unchanged: 708 reader, 296 writes-other, 25 writes-args, 46 native-only, 10 unknown) [counted]:

| | Count |
|---|---|
| bodies with no managed store target | 763 |
| bodies whose strongest tag is `ref` / `word` / `unknown` / `scalar` | 157 / 53 / 86 / 26 |
| targets tagged `ref` / `word` / `unknown` / `scalar` | 281 / 123 / 464 / 306 |
| reference-capable targets (`ref`, `word`, `unknown`) of writes-other bodies: a field of `this` | 731 |
| ... `this` deeper than one field or at an unknown offset (incl. `this.mItems` elements) | 70 |
| ... an argument object, direct / deeper | 25 / 1 |
| ... static field data | 12 |

So 26 bodies with managed stores can never store a reference (skip the mark), and the coarse mark's blind spot is at most the 70 + 1 deeper targets plus what the 464 `unknown` values hide. Examples: `PriorityQueue.Add` `this.mItems=ref` + `this.mItems (QueueItem[], unknown offset)=word`; `StringBuilder.InternalEnsureCapacity` `this._str=ref`; `Hashtable.Rehash` only `this.modificationCount=unknown` (its bucket store is not resolved by the tool: I-527-7).

### 13.5 Selftest

`TestGcCardRt` (`gccardrt-test.h`): fake heap (two blocks in gccard.h's block index copy), fake icall originals, a fake binder body and a fake `alloc_handle`. Checks: nothing marks with the lane off; `GcRtAny` (scalars, stack, free block, byref near a card edge, large-object tail); the store helper; FastCopy (range, refused copy, 2-byte elements), SetValue, GetGeneric (heap `out` and a local), Exchange / CompareExchange (store happens, then the mark), InternalInvoke, get_property_info, field set; the handle wrapper (strong marks, weak does not, `eax` and `esi` pass through); the binder dispatch for `Hashtable.Rehash`, the three PriorityQueue bodies and an unlisted body (in-flight marks seen from inside the body after a simulated card clear, post marks after it, `mItems` only for PriorityQueue, scalars mark nothing), unwound-frame drop, depth overflow, value-type `this`, null `this`, lane off; the IC dispatcher (row hit, no row, Satb's client not run without a span); the icall table swap read-back on a fake table incl. folded entries and the second-client no-op.

### 13.6 Open

1. NOT RUN. The hooks are live with `GenCards=1` even before lane W consumes the table: `tools/harness/queues/gccard-re-correct-1118.json` (hook safety, BP).
2. Lane W: wire `GcRtFlyMark()` (§13.3).
3. Mode 3 prerequisites of §13.3 (IC return hook, single-store order, address-of).
4. `ref_stores` -> a per-body table that replaces the coarse `this` + arguments mark (skip bodies with no `ref` / `word` / `unknown` store; add one-load-deep marks for the bodies that need them).
5. EA 1.69 / retail 1.67: the lane stands down on EA 1.69 (`ts3a_noaddr`); the handle hook is Steam only; the rest follows Satb's rows on retail (not checked with `tools/retail-port-check.ts`).
6. A strong handle slot written by anything but `alloc_handle` (I-527-3).

## 14. ON test build for v0.3.1 (2026-11-20, `integration/gengc-all-1118` on master 4cdc19ff1 (v0.3.0 + Dalexlol imports), 0.36.297-gengcon2; built, NOT RUN)

Rodney 2026-11-20: "switch it on and we test it". The generational GC and the sim-thread offload switches get an ON arm
in a test build; each goes ON in the v0.3.1 RC only if smoke, correctness, save matrix and Apex compat pass and the ms/s
loss stays within ~2% (offload goal rule). Plan and status: probe-out/release-next-plan.md ("Decisions").

- **Install-time read-back of §6 #4 (Apex keeps `MEM_WRITE_WATCH`):** `GenCycleStart` now logs once per session, at the
  first collection after install, in any genMode: `gen watch probe (first cycle): N heap sections, all write-watched |
  NOT all write-watched (minor cycles stand down); Apex Radiance loaded | not loaded`. Same probe as the per-cycle
  `GenWatched` stand-down (first and last page of every section, `GetWriteWatch` without reset, so rule 2 of §8.5 holds).
  The smoke requires `all write-watched` with `Apex Radiance loaded`. [verified in code; not yet run]
- **Build set `gengc-on-1120`** (builds.json): asi 0.36.297-gengcon2, ini = the branch ini with
  `[GC] Generational=1 WeakHandleFix=1`, `[JobQueue] Enabled=1 SimSkipShared=1 Workers=2`, `[RouteAsync] Kinds=7`
  (own-task STARTs gated to unseen routes in code, docs/288 §4). Left off on purpose: `[GC] MarkHelpers` (refuses with
  Generational, §9), `GenCards` (union mode only adds barrier cost until a cards-first mode exists; the verify queue
  turns it on), `SubTimers` (it also turns the idle gate on as 2; queues set it in both arms), `GenStats` / `GenVerify`
  (diagnostics), `[LotStream] Thumb` (timer only). ts3fixes offload switches (`autoffenv`, `autoffbcast`,
  `autoffenvapply`, `autoffbcastapply`) go ON per job through `fixesOn`.
- **Household rule fix in ts3fixes (docs/208 §14):** `autoffenvapply` / `autoffbcastapply` applied answers for household
  Sims unless `autoffunseen` was also on. They now always take the unseen non-household scope.
