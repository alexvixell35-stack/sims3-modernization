# 139. Mint interpreter fast paths: dispatch, superinstructions, calls, `callvirt` cache

Script-stutter lever from docs/130 §7.1: the mint interpreter's own code is ~25.5% of sim-thread samples, call/return machinery ~43% of that, and the hottest single instruction is the receiver vtable load in `callvirt`. This note studies the three fast-path families named there (G-I10 dispatch, G-I5/G-I6 superinstructions and call/return, the `callvirt` cache), measures what can be measured offline, and ranks them.

Sources: capstone over the Steam 1.67 `TS3W.exe` (copy under `game-copy/`), docs/130 (§1.2-§1.9 live-chain shares, §9.1 superop rules), `docs/data/mint-opcodes.csv`, and new monohost runs (docs/119 offline host, `native/monohost`): instrumented dispatch over real script code plus a timed workload. No game run, no queue. Conversion as in docs/130: 1% of sim-thread samples ~ 10 ms/s [inferred]. Tags: **verified** (disassembly or host run), **measured offline** (monohost, not the game), **inferred**.

## Summary

| Rank | Option | Expected gain in game | Hours (Claude + agents) | Game runs | State |
|---|---|---|---|---|---|
| 1 | **Sub-run fusion** of the existing 22 superops (§4): fuse a descriptor wherever it occurs inside a run, not only when the whole run equals it | **~2-8 ms/s** [inferred]; offline: -0.7% to -11.8% dispatches, **-7.1%** time on the bench | 1-2 h to port to ts3native (switch, default off) | 2 loads (transform-time: no in-load A/B), clean night | **built in monohost**, 37/37 selftest and same checksum in 59 of 60 runs (54 in a row); one unexplained failure (§4.4) |
| 2 | **Call/return fast path** (§5, §8): own handlers for `call`/`callvirt`/`ret*` through the jump table, falling back to EA's handler before any state change | design said ~15-30 ms/s; **as built ~5-10 ms/s** [inferred from host benches, §8.5]: a bit-exact frame keeps nearly all of EA's stores (209-233 -> 191-201 instructions) | done | verify queue + A/B **in one load** (`--ab mintcall`) | **built** (ts3native 0.36.127 `[MonoFast] CallPath`, off), host-verified 0 mismatches over 19-21 M calls, not run in game |
| 3 | New superinstructions for frequent pairs (§3) on top of rank 1 | ~1-4 ms/s [inferred] | 4-8 h, after G-I1 picks the pairs | 2 loads | design only |
| 4 | Threaded (replicated) dispatch, G-I10 (§2) | **≤ ~2-3 ms/s** of a 16 ms/s ceiling; offline -1.7%, at the noise floor | done offline; port 1 h | not worth one | **built in monohost**, not recommended |
| 5 | Drop the opcode range check in the dispatch head (§2) | ~0 (no gain measured) | - | - | built, rejected (also removes the "Unimplemented opcode" guard) |
| 6 | `callvirt` two-entry cache or receiver prefetch, G-I5 (§6) | < 1 ms/s [inferred] | - | - | rejected: offline miss rate 0.15-0.17%; the receiver stall is a data-cache miss that a prefetch only moves |

Main findings:

- **EA's peephole fuses whole runs only** [verified]: the pass `0xE5C660` collects one straight-line run (cut at branch targets and before 22 "end" opcodes: every call kind, `ret*`, `newobj`, `leave*`, `throw`, `endfinally`, `sim_sleep`, ...) and the matcher `0xD89930` hashes the **entire** run and compares its length: a superop is written only when the whole run equals a descriptor. So `ldthis+ldfld.i4` (`0x1AD`) fires only for a run that is exactly those two instructions; inside longer runs it never fires. That is why the 22 superops take only 3.2% of interpreter self (docs/130 §1.9). Fusing them inside runs placed **~10x more superops** in the script code the host ran (1,643 inner vs 168 whole-run sites in the `.cctor` sweep).
- **Threaded dispatch does not pay on this CPU class** [measured offline]: 417 handler tails each got their own byte copy of the dispatch head; the bench moved -1.7% (spread ~1%), and the unpinned runs showed nothing. Modern indirect predictors already predict the single shared `jmp [edx*4+0xE59880]` well.
- **The `callvirt` cache hits** [measured offline]: 0.15% misses over the `.cctor` sweep (1.35 M `callvirt`), 0.17% over the rest of the selftest. The hot spot at `0xE54AEC` is the `obj->vtable` load itself; no cache shape avoids it.
- **One mint instruction costs ~1.9 ns** on the bench (161.6 M dispatches in ~314 ms, pinned core) [measured offline]; each dispatch removed by fusion saved ~1.15 ns (~60% of an average instruction).

## 1. Method

monohost (docs/119) maps a copy of `TS3W.exe` and runs the game's own Mono with the game's assemblies. New in this note (`native/monohost/monohost.cpp`, `HostTest.cs`; all off unless an environment variable is set):

| Switch | What it does |
|---|---|
| `MONOHOST_MINT=count` | every entry of the jump table `0xE59880` points to a counter stub (opcode and opcode-pair counts, all registers and flags kept, then `jmp` to the original handler); `call 0xE51FC0` at `0xE54B0B` (the `callvirt` miss) goes through a miss counter. Writes `out/mint-pairs.csv` per phase |
| `MONOHOST_MINT=threaded` | each of the 417 `jmp 0xE544D5` handler tails (capstone sweep of `0xE53EB0`-`0xE59880` and a raw byte scan agree: 417 `E9` sites, plus 2 short `jmp` and 2 `je` left alone) is retargeted to its own 25-byte copy of the head (same instructions, `ja` re-based) |
| `MONOHOST_MINT=nocheck` | head becomes `movzx; movzx; jmp [edx*4+0xE59880]` (range check skipped) |
| `MONOHOST_MINTFUSE=sub` | sub-run fusion (§4): `call 0xD89930` at `0xE5C8CA` -> our matcher |
| `MONOHOST_MINTBENCH=<n>` | after the selftest, `HostTest.MintBench(n)` 7 times on one pinned core at high priority, best time and checksum logged |

Phases: **m13** = the M1.3 load and `.cctor` sweep of all 10 script assemblies (EA script code and the mscorlib it reaches; the only phase that is EA code throughout), **selftest-rest** = M1.4-M5 (our HostTest code over the game's mscorlib, two interpreting threads in M5), **bench** = `MintBench` (our code: two receiver types behind one abstract class, plus the game mscorlib's `List`/`Dictionary` generics, struct and interface enumerators, boxed `Enum.Equals`, `GetType`). Every variant must return the same bench checksum and pass the 37 selftest checks.

Limits: none of these phases is play. The `.cctor` sweep is straight-line init code (`newobj`, `stsfld`, `call`), so its pair mix differs from the sim's hot methods; the weighting the game needs comes from G-I1 (managed sampler, docs/130 §7.1). The machine was in use (noisy-machine rule): timing claims rest on the pinned bench only, with the spread stated.

## 2. G-I10: dispatch

Head `0xE544D5` [verified]: `movzx eax, word [edi]` / `movzx edx, ax` / `cmp edx, 0x1C0` / `ja 0xE596C2` / `jmp [edx*4 + 0xE59880]`; every handler ends with `jmp 0xE544D5`. The table address appears once in the binary (`0xE544EA`), so the head is the only reader.

| Variant (bench, n = 3000, best of 7, pinned) | Runs (ms) | Median | vs stock |
|---|---|---|---|
| stock | 308.6, 318.8, 309.7, 315.5, 314.3 | 314.3 | - |
| threaded | 311.7, 308.4, 308.8, 308.5, 311.6 | 308.8 | **-1.7%** |
| sub-run fusion (§4) | 282.0, 290.6, 297.4, 292.1, 295.4 | 292.1 | **-7.1%** |

Unpinned runs (same build, before pinning): stock 324-394 ms, threaded 329-359, nocheck 336-357: no difference above the ~±8% process-to-process noise. Correctness: same checksum (-466393015 at n = 3000) and 37/37 checks in every threaded and nocheck run [verified].

Verdict: the dispatch region is 6.3% of interpreter self (~16 ms/s ceiling, docs/130), and replication buys at most ~1.7% of interpreter time, i.e. **≤ ~2-3 ms/s** in game [inferred]. A real threaded-code rewrite (handler addresses in the code stream instead of opcodes) would also drop the `movzx`/table load, but needs a transform rewrite, breaks every reader of mint code (yield save as opcode-word index, superop decoding, docs/133's cache walker) and gains under the same ceiling: not worth it. Removing the range check: no gain measured and the guard against corrupt code would turn into a wild jump. **G-I10 closes as "do not build".**

## 3. G-I6: opcode pairs

Counts from `MONOHOST_MINT=count` (stock fusion). "Fusable" = the first opcode is not an end opcode (calls, `ret*`, `newobj`, branches) and neither is a superop. % of all dispatches in the phase:

| Phase | Dispatches | Superops | Top fusable pairs | Top 10 / 20 / 50 cover |
|---|---|---|---|---|
| m13 (EA `.cctor` code) | 45.8 M | 1.3% | `ldthis+ldarg.i4` 3.7, `ldc.i4.0+call` 3.7, `ldthis+ldc.i4.0` 3.7, `ldarg.i4+call` 2.5, `stinarg.i4+ldthis` 2.5, `ldloca.s+ldfld.o` 2.5, `ldarg.o+ldloca.s` 2.5, `ldarg.o+brtrue.i4.s` 1.6 | 27.8 / 40.6 / 54.9% |
| selftest-rest | 731.7 M | 1.8% | `ldloc.o+ldfld.o` 3.1, `ldarg.r4+ldarg.r4` 2.7, `stinarg.r4+ldarg.r4` 2.3, `ldloc.r4+ldloc.r4` 1.8, `conv.r4.i4+stloc.r4` 1.7 | 19.7 / 29.8 / 49.0% |
| bench | 75.4 M | 1.4% | `ldthis+ldfld.i4` 5.7, `ldthis+ldfld.o` 4.6, `ldloc.o+callvirt` 1.9, `ldfld.o+ldfld.i4` 1.9, `ldfld.i4+sub1.i4` 1.9 | 23.1 / 32.6 / 55.0% |

Findings:

- **No pair dominates**, and the top pairs change with the workload (the m13 top five come from one loop: equal shares). The three phases share only field-access pairs (`ldthis+ldfld.*`, `ldloc.o+ldfld.o`, `ldfld.o+ldlen`, `ldfld.o+ldloc.i4`) and argument-setup pairs before calls. The game's own per-handler self samples (docs/130 csv: `callvirt` 25.7%, `call` 8.7%, `ldfld.i4` 6.5%, `stinarg.i4` 5.2%, `isinst` 4.3%, `ldthis` 3.3%) point the same way: field loads and call setup.
- **The two most frequent field pairs already have superops** (`0x1AB` `ldthis+ldfld.o`, `0x1AD` `ldthis+ldfld.i4`), and still show up unfused at 5.7% and 4.6% of the bench: the whole-run rule (§4) keeps them out. Fixing the rule comes before adding any new superop.
- New superops after that: candidates `ldloc.o+ldfld.o`, `ldfld.o+ldlen`, `ldfld.o+ldfld.i4`, `ldarg.o+ldfld.*`, `stinarg.*+stinarg.*` (call setup). Each fused pair saves one dispatch plus one eval-stack store/load. Pick them from G-I1's hot methods, not from these phases. Building them needs new handlers in our DLL, a bigger jump table (patch `cmp 0x1C0` at `0xE544DB` and the table disp32 at `0xE544EA` to our own copy; table entries `0x1C1`+ point to our handlers) and our own descriptor list in the §4 matcher. Gain on top of §4: ~1-4 ms/s [inferred], 4-8 h.

## 4. Sub-run fusion (built in monohost)

### 4.1 How EA's peephole works [verified, capstone]

`0xE5C660(ctx, stats)` runs once per method at the end of generate-code, under the transform lock `0x11F4174`:

1. Pass 1 marks every branch target (operand kinds 7, 8 and switch 9 from the class table `0x1195238`) in a byte map.
2. Pass 2 walks the code (`[ctx+0x38]`, lengths from `0x1195088`) and appends each opcode to a window of at most 32. The window is flushed (a) at a marked branch target, (b) before one of 22 **end** opcodes (`break`, `ret`, `ret.void`, `ret.vt`, `leave`, `leave.s`, `throw`, `rethrow`, `endfinally`, `call`, `con.call`, `callvirt`, `con.callvirt`, `con.boxcallvirt`, `calli`, `calli.nat`, `jmp`, `callint`, `callrun`, `newobj`, `mono_retobj`, `sim_sleep`; the end opcode itself starts no window), or (c) at 32 entries. `nop` is skipped (not appended). All other 402 base opcodes, including conditional and unconditional branches, are appended (classes from the two small switch tables at `0xE5C914`/`0xE5C920` and `0xE5C9BC`/`0xE5C9C4`).
3. A flushed window of 2+ opcodes goes to the matcher `0xD89930(window, count, &out)`: FNV-1a over all `2*count` bytes, then for each of the 22 descriptors compare hash, **count** and bytes. Only an exact whole-window match writes `0x1AA + index` to `out`; the caller writes it over the window's first opcode word (`0xE5C8F0`).

So a superop exists only where a whole run between two boundaries is exactly one of EA's 22 sequences: profile-shaped fusion of a few hot blocks, not general pair fusion.

### 4.2 The change

`call 0xD89930` at `0xE5C8CA` (5 bytes) -> `SubFuseShim`, which passes the window, its length, `out` and the code address of the run start (`[ctx+0x38] + edi*2`; `ctx` is the pass's first argument) to `SubFuse`:

- walk the run's code with the operand-length table to get each instruction's address; an instruction that is not in the window (`nop`) is a barrier; a switch (length 0 in the table) leaves the run alone;
- left to right, take the **longest** of the 22 descriptors that matches at this position without crossing a barrier, write its opcode over that instruction's first word, skip its length; no match: next instruction;
- a whole-run match is returned through `out` as before (EA's own case, same result), so the caller's write is unchanged.

Why it is safe [verified rules from docs/130 §9.1, applied to a new position]: the encoding is EA's (first word replaced, operands and later opcode words in place; the handler advances `ip` by the summed lengths); no run contains a branch target, so no jump lands inside a fused sequence that EA's whole-run fusion would not also have produced; every fault inside a superop sets `MonoInvocation+0x1C` to the base instruction that would have faulted, so catch-clause ranges and stack traces see the same `ip` even when a try region starts in the middle of a run; calls end runs, so a yield or a return never resumes inside a superop. The only new fact is the position, and the handlers do not depend on it [inferred from the handler reads; checked by the host runs below].

### 4.3 Results [measured offline]

| Phase | Dispatches stock | Dispatches sub-run fusion | Change | Fused sites (cumulative): whole-run / inner |
|---|---|---|---|---|
| m13 (EA `.cctor` code) | 45,771,028 | 45,468,398 | **-0.66%** | 168 / 1,643 |
| selftest-rest | 731,728,459 | 708,916,496 | **-3.12%** | 226 / 1,809 |
| bench | 75,394,435 | 66,501,096 | **-11.80%** | 231 / 1,853 |

Bench time -7.1% (table in §2). `callvirt` and miss counts are unchanged (bench identical; the other phases within ±0.03%, which is the M5 worker's run-to-run variation), as expected since calls end runs. Same checksum in every run.

### 4.4 Correctness record and the open failure

- 37/37 selftest checks and the same bench checksum in **59 of 60** sub-run-fusion runs (4 unpinned with bench, 1 with counters, 5 pinned with bench, 6 + 14 without bench, 30 pinned with bench); **54 clean in a row** after the one failure. 0 failures in ~50 runs without it (stock, threaded, nocheck, count) in the same period.
- **One run failed one check** (`SELFTEST FAILED: 36 passed, 1 failed`, pinned bench, first run of its batch). The log was overwritten before the check id was read, and it never came back (the later loops kept any failing log). The 50-clean-runs bar is met, but the cause is unknown; the selftest has timing-dependent checks on two interpreting threads (M5), which is a candidate, not a cause [inferred]. Keep the switch off by default and keep the failing-log capture in any further host loop.

### 4.5 Expected gain in game

The offline phases bracket the dispatch reduction from 0.7% (init code, many calls) to 11.8% (field-heavy code). The sim's hot interpreted code is field and call heavy (§3), so **~2-6% fewer dispatches** is the working guess [inferred]. With interpreter self 25.5% of sim-thread samples and ~60% of an average instruction saved per removed dispatch: 25.5% x (2-6%) x 0.6 x ~1.0-1.7 (the field superops also drop eval-stack traffic) = **~0.3-0.8% of samples, ~2-8 ms/s** [inferred]. Confirming it needs either G-I1 (hot methods; then count inner sites per hot method offline) or a two-load A/B: the change acts at transform time, so methods transformed before a switch flip keep their form, and an in-load A/B would be biased.

Port to ts3native: the same 5-byte call patch at `0xE5C8CA` (check the bytes `E8` -> `0xD89930` first), installed at DLL load before `MonoScriptHost_Init` (`0xD7E560`) transforms anything; switch `[MintFuse] Sub=1`, default off; counters (runs, whole, inner) in a new counter version (reserve in `docs/registry.md`). No S3SS/S3PP hook site is listed near `0xE5C660`, `0xD89930` or the interpreter handlers (docs/68 table, grep).

## 5. Call/return fast path (design)

Hot path today [verified, capstone; instruction counts approximate]: `callvirt` `0xE549EB`-`0xE54B4B` on a cache hit ~48 instructions (it also tests the `0xED`/`0xEE` variants, spills ip and sp, and reloads the data items), common push `0xE56287`-`0xE5642E` ~85 (record save, depth and max-depth update, slot `MonoInvocation` reuse, transform-flag test, arena bump, native-body test, eval-stack and area pointers, init-locals test), `ret` `0xE591FF` + frame return `0xE543C7`-`0xE544D5` ~53 (depth, restore, arena pop, pending exception, newobj flag, value copy, yield flag). About **190 instructions and ~55 stores per interpreted call**, compiled C with most state spilled to `[ebp-x]`.

Live-chain shares (docs/130 §1.9, of all sim-thread samples): `callvirt` 4.95% (of which ~2.6% is the receiver stall at `0xE54AEC`/`0xE54AFD`, untouchable), call common 1.98%, `call` 1.67%, frame return 1.35%, `ret` 0.42%: **~7.8% non-stall, ~78 ms/s**.

Design: own handlers for `call` `0xEA`, `callvirt` `0xEC` and the `ret` family, installed by writing their jump-table entries (aligned 4-byte stores: atomic, and switchable at run time). Each checks every slow condition first and jumps to EA's handler unchanged if any holds, so nothing is half-done:

- `call`/`callvirt`: cache hit (`callvirt`), reference-type receiver, callee transformed (`RuntimeMethod+0x44 & 0x100`), no EA native body (`+0x24 == 0`), depth < `0x3F` and < the deepest slot already holding a `MonoInvocation` (`[ebp-0x30]`, so no `alloca` is needed), arena chunk has room (`+0x38`), no init-locals (or an inline clear for small sizes);
- then write exactly what EA writes: `MonoInvocation+0x1C` (throw ip) before anything can fault, record `+0x00`..`+0x14` (newobj flag 0), `MonoInvocation` `+0x04`/`+0x08`/`+0x0C`/`+0x10`/`+0x14`/`+0x18`/`+0x1C..+0x24`, `ThreadContext+8`, depth and max depth, `[ebp-4]`/`[ebp-8]`/`[ebp-0xC]`/`[ebp-0x10]`/`[ebp-0x14]`/`[ebp-0x2C]`, `esi`/`edi`;
- `ret`: depth > 0, no pending exception, newobj flag 0, yield flag `state+0x149` clear, arena pop on the inline path; else EA's path.

Expected: ~75 instructions instead of ~190; cycles fall less than instructions (spills to a hot stack are cheap): **30-40% of the ~78 ms/s, ~15-30 ms/s** [inferred]. Correctness risks, in order: the yield save (`0xE53620`) and resume (`0xE53F18`) read the records and `MonoInvocation` fields, so the fast path must leave them bit-equal to EA's (host differential test: run every selftest phase with and without, compare a dump of records and `MonoInvocation` at each return); exception ip (`+0x1C`) and depth-overflow order; GC: the conservative scan sees the same stack words. Patch: jump-table entries (data writes, no code detour), handlers in ts3native as naked asm; in-load A/B possible by swapping the entries back. 8-14 h including the host differential harness; 1 BP A/B night.

Complementary, not a replacement: G-I2's transform-time inliner removes trivial calls entirely; this fast path cuts every remaining call. G-I1 decides which comes first (how many calls are trivial getters).

## 6. G-I5: the `callvirt` cache and the receiver stall

- Cache: one (class, method) pair per call site [verified, docs/133 §1.1]. Offline miss rate 0.15% (m13, 1.35 M `callvirt`), 0.17% (selftest-rest); the bench's deliberate two-type site gives 19.75% [measured offline]. The miss path `0xE51FC0` is 0.50% live in game including callers other than `callvirt` (docs/29: mostly `0xE59FD0`). A two-entry cache recovers part of the `callvirt` share of that: **< 1 ms/s** [inferred]. Not worth a handler rewrite on its own; if §5 is built, a second entry costs a few instructions there.
- Stall: 1,518 of 3,794 `callvirt` samples sit right after `mov ecx, [eax]` (receiver -> vtable) and 449 after the class compare [verified PCs, docs/130]. The receiver object is cold in the data cache. A prefetch at the `ldloc.o`/`ldfld.o` that pushed the receiver has a few dozen cycles of lead (one or two dispatches) against a ~300-cycle miss, and the callee's first field access would touch the same line anyway, so the miss moves rather than vanishes [inferred]. Nothing in the interpreter fixes it; fewer cold objects per tick (script-level changes, docs/74) does.

## 7. Next

1. Sub-run fusion: port to ts3native as `[MintFuse] Sub=1` (default off) and a two-load BP A/B on a clean night.
2. G-I1 managed sampler (1 BP job) to weight pairs and calls by hot methods; then decide §5 vs G-I2 inliner order.
3. §5 call/return fast path with a host differential harness: **built, §8**.

Ledger: docs/51 §194.

## 8. Call/return fast path as built (`[MonoFast] CallPath`, ts3native 0.36.127, 2026-10-30)

Rodney approved §5 after docs/207 §7 showed that compiled bodies stay call-bound. Code: `native/ts3native/mintcall.cpp` (shared: ts3native includes it before `autshadow.cpp`, monohost inside `namespace mt`), host harness in `native/monohost/monohost.cpp` (`McTests`, `McBench`) and `HostTest.cs` (`CallTest`), selftest `mintcall-test.h`, counters v143 (`docs/registry.md`). Built, proven offline, **not run in game**.

### 8.1 What it does

Own handlers for `ret` 0x06, `ret.void` 0x07, `call` 0xEA and `callvirt` 0xEC, installed as four jump-table entries (aligned data writes, no code byte changed). Each handler tests every case in which EA's path does something else, **before any write**, and on any of them jumps to EA's handler with the entry state unchanged (eax = edx = opcode, edi = ip, esi = sp):

| Path | Goes to EA's handler when |
|---|---|
| `call` | switch off; remoting check would run (instance, not a value-type method: null `this` or null vtable (EA faults there), class equal to `[0x1220EC0]`); MonoPatcher ForceJIT active (byte `0xE5637D` is not `0x75`); callee not transformed (`+0x44 & 0x100`); EA native body (`+0x24`); the next record slot has no `MonoInvocation` yet (depth + 1 >= high-water mark `[ebp-0x30]`, i.e. EA would `alloca`); no arena chunk or not enough room |
| `callvirt` | as `call`, plus null receiver, inline-cache miss (EA looks up and writes the cache), value-type receiver (EA's unbox adjust); `con.callvirt` / `con.boxcallvirt` keep EA's handler |
| `ret`, `ret.void` | depth 0 (exit A), yield flag `state+0x149`, pending exception `inv+0x20`, newobj return (record `+0x14`), values left on the eval stack (EA logs), arena base 0 or outside the current chunk (EA's slow pop) |

Otherwise it writes the same live words as EA (record of the caller and the callee, `MonoInvocation` fields, depth and max depth, `ThreadContext+8`, arena bump / pop, frame words `[ebp-4]`, `-8`, `-0xC`, `-0x10`, `-0x14`, `-0x2C`, the return copy including the dead high dword of a 4-byte value, init-locals clear) and dispatches the next instruction with its own copy of the dispatch head. `ret` and frame return run as one block.

### 8.2 Facts found on the way [verified]

- **EA's hit paths are longer than §5 counted:** `call` + push 128 instructions, `callvirt` (cache hit) + push 152, `ret` + frame return 71, each plus the 5-instruction dispatch head: **209 per static call + return, 233 per virtual call + return** (capstone path walk over the Steam exe).
- **Scratch frame slots:** every read of `[ebp-0x18]`, `-0x1C`, `-0x24`, `-0x28`, `-0x34`, `-0x48`, `-0x50`, `-0x54`, `-0x58` in `ves_exec` (`0xE53EB0`-`0xE59880`) is preceded by a write in the same handler (call / callvirt / calli / newobj before the common push, frame return, exception search; address-order first-access scan of every `[ebp-x]` operand). They are dead across a dispatch, so the fast path does not write them and verify does not compare them.
- **MonoPatcher 0.3.0** patches two bytes inside the modeled code (docs/68 §1.3): `0xE59218` (`90 E9`, drops the "more values on stack" print; accepted by a second code hash, behaviour equal since our `ret` bails on that case) and `0xE5637D` on demand (ForceJIT: transform on every call; tested per call). `[GcRootCheck] TailWatch=1` patches `0xE563B2` (push): CallPath refuses (code hash).
- **mint's transform already inlines trivial getters**: a `callvirt` to a non-virtual `return V;` costs ~1 ns in the host (CallBench kind 4), an empty static method ~0 ns (kind 2).

### 8.3 Correctness [verified: host runs 2026-10-30]

Verify mode (`MONOHOST_MINTCALL=verify`, in game `[MonoFast] CallPath=2`): every jump-table entry goes through a compare stub; an eligible call snapshots everything both paths may write (frame words `[ebp-0x80, ebp)`, both records, the callee's and the caller's `MonoInvocation`, `state+0x13C..0x14C`, the chunk header, `ThreadContext`, the return slot, the callee frame), runs the fast writes, records them, restores the snapshot and runs EA's handler from the same entry state; at the next dispatch every byte (scratch slots excepted) and edi / esi / esp must be equal. One call in flight over all threads.

| Run (monohost selftest + `CallTest` + 6-deep yield task) | Result |
|---|---|
| `MINTCALL=off` / `fast` / `verify` | 39/39 each, same `CallTest.Run(300)` checksum 2096027, deep yield task 6 runs, result 775 |
| verify, every eligible call (`MINTCALLEVERY=1`), before the scratch trim | 21,230,211 compared, **0 mismatches** |
| verify after the scratch trim | 19,303,133 compared, **0 mismatches** |
| negative control (`MONOHOST_MCNEG=1`: the fast pass flips one frame word EA does not write) | 892,600 of 892,600 flagged |
| fast + `MONOHOST_SATB=1` (Satb act: worker scores the cut while the main thread mutates) | **44/44**, `AS-satb-exact` A0 6000 of 6000, A1-A3 0, G-A2 10,000 of 10,000 |
| fast + P0 compiler verify (`MONOHOST_JIT=verify`), with and without inlining (docs/207 §8) | 2.0 M verified compiled calls each, 0 mismatches, 23/23 bench checksums |

Covered by the tests: static, instance, virtual, interface and value-type calls, wide / struct returns (`ret.vt` stays EA's), cache misses, throw through `finally` with the stack trace text in the checksum, null receivers, the 63-frame limit (`StackOverflowException` caught), a task yielding 6 frames deep (save / resume of every frame) with collections between resumes, two interpreting threads (M5), and the conservative GC's view (every live word compared at each call). Not covered offline: the game's own script mix (the in-game verify queue below).

### 8.4 Cost [measured offline; quiet machine 2026-10-30 07:08-07:13 UTC under a hold, plus earlier noisy runs for the spread]

| | EA | fast path |
|---|---|---|
| static call + return (instructions on the hit path, incl. dispatch) | 209 | ~191 (-9%) |
| virtual call (cache hit) + return | 233 | ~201 (-14%) |
| `CallBench` static call, ns per call (best of 41, pinned, stock and fast interleaved in one process); quiet | 14.34 | 14.63 (+2%; noisy runs -1% to +9%) |
| `CallBench` virtual call (cache hit); quiet | 14.17 | 13.14 (-7.3%; noisy runs 0 to -10%) |
| `MintBench(1000)`, same interleaving; quiet | 95.5 ms | 91.0 ms (-4.7%; noisy runs +2% to -7%) |
| `JitBench` per op, interpreted (docs/207 cases): `Dictionary<int,Shape>.TryGetValue` / `Dictionary<ObjectGuid,int>.TryGetValue` / `ContainsKey + set_Item`; quiet, separate processes | 174 / 260 / 376 ns | 149 / 237 / 337 ns (-14% / -9% / -10%) |
| `MintBench(3000)` interpreted (JitBench harness, separate processes: ±5% process to process) | 286.1 ms | 272.4 / 285.4 ms (two processes: -5% / 0%) |

Per-call increments of a counter cost ~1-2 ns in this path, so fast calls are counted in verify mode only (bails always). **The gain is far below §5's estimate:** a bit-exact frame keeps nearly every store EA makes (the protocol is store-bound: ~60 cycles for ~210 instructions, IPC ~3.5), so cutting the spills and reloads buys ~10-15% of the call side and nothing on the return side.

### 8.5 Expected gain in game [inferred]

Call machinery outside the receiver stall ~78 ms/s (§5). Call side (callvirt non-stall + call + push, ~60 ms/s) x ~10-15% + return side (~18 ms/s) x ~0 = **~5-10 ms/s, realistic ~8 ms/s**; low ~3 (bare-call microbench: no change), optimistic ~15 (if the game's calls look like the dictionary cases). The in-game A/B decides it.

### 8.6 ts3native port, switches, queues (built, not run)

- `[MonoFast] CallPath=0|1|2`, `VerifyEvery=16` (ts3native.ini, default off), installed right before `[AutShadow]` (after every code patch; refuses on other bytes in the five modeled ranges or another owner of the four entries). Runtime switch `mcSwitch` (+0x54E0, harness `--ab mintcall`): 0 = every call goes to EA's handler through ours (one extra indirect jump), 1 = fast path. Counters v143 `mc*`; `agent.js`, `native-switches.ts`, docs/68 row.
- Satb (docs/206 §7) accepts our callvirt entry as the stock one (`McOwns`) and chains to it on the sim thread; its worker side never reaches it (`SatbWCallvirtTail`).
- asi `game-copy/ts3native.asi.mcall1`.
- `tools/harness/queues/mintcall-verify.json` (correctness, `--no-smoke`): BP / IP / SV 10 min with `CallPath=2 VerifyEvery=16` (pass: `mcReady` 7, `mcVerified` > 100,000, `mcMismatch` 0), plus BP `CallPath=1` under `[AutShadow]` Mode R with the mono-cost13 keys (pass: mono-cost13's gates). Run: `node tools/harness/queue.ts tools/harness/queues/mintcall-verify.json --no-smoke`.
- `tools/harness/queues/mintcall-ab.json` (timing, clean machine, after mintcall-verify passed): BP / IP / SV off-first and on-first plus a second IP job, 10 min, `--ab mintcall` 2-min slices, declared 4-min smoke job first. Read with `hitch-ab.ts` and `sim-budget-report.ts`; gain = Mono ms/s off - on. Run: `node tools/harness/queue.ts tools/harness/queues/mintcall-ab.json`.
