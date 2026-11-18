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
| 2 | sweep behaviour under sticky marks with EA's stamp skip and free bits (blocks never swept, objects born old) | read `0xE4FF00`-`0xE50380` fully; toy-heap selftest |
| 3 | finalization (`GC_finalize 0xE4AE60`) with sticky marks: no object finalized while reachable | follows from the mark invariant; cover in verify mode |
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
