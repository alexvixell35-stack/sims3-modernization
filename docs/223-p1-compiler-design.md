# 223. P1 Mono method compiler: design

Design only (2026-10-31): no build, no game run, no new measurement. Follows docs/207 §8.5 ("Conditional GO for P1"). Inputs: docs/207 §7-§9 (P0 compiler, inliner, projections, accessors), docs/139 §5 and §8 (call/return fast path), docs/213 (opcode spec), docs/221 (float templates), docs/130 (runtime map), docs/29/30/56/57 (thread state, GC), docs/206 (Satb), docs/214 (cold paths), docs/128 (mod guards), docs/220 (binder bodies), `native/monohost/monohost-jit.cpp`, `docs/data/compile-candidates.csv`.

Tags: **[verified]** = disassembly, host run or code read, with the source named; **[measured]** = a run's number; **[inferred]** = reasoning, to be confirmed by the step that names it.

## 0. Gate status

- `mintcall-verify` 2026-10-31: passed (the queue's result file `docs/data/results/2026-10-31-queue-mintcall-verify.md`).
- `mintcall-ab` 2026-10-31 (`docs/data/results/2026-10-31-queue-mintcall-ab.md`): pair means of both slice orders show the fast path saving **BP ~7.7, IP ~17.7 ms/s** of Mono stage time [measured; noisy: per run BP -12.6 / -2.7, IP -6.8 / -28.6; the queue tags every job "timing: invalid (noise medium)", 11.6-13.5% other load]. SV was still running when this was written. The go line was ~5 ms/s and the projection 5-10 (docs/139 §8.5), so the gate is met.
- What the ratio says: measured / projected midpoint = **~1.0x (BP) to ~2.4x (IP)**. §8 below scales the P1 numbers with the BP-like 1.0x and gives the IP-like case as the upper end. Treat the ratio as unproven until SV and a quiet rerun agree.

## 1. Scope and order

**Compile the never-yielding set (class N of docs/207 §1) by ms/s, top down.** About 72% of interpreter self time, ~245-270 ms/s, is in N methods in all three worlds [measured, docs/207 §2]. Order:

1. The BCL generics at the top of `compile-candidates.csv`: `Dictionary.GetPrev` / `TryGetValue` / `ContainsKey` / `set_Item`, the `List` and `Dictionary` enumerators, `List.get_Item` / `Add`, `Array.IndexOf` (docs/207 §3, P0 list `kJitList`, `monohost-jit.cpp:19`). P0 already compiles these and verifies them (docs/207 §7.2, §8.2).
2. The 19 never-yielding `Sims3Gameplay*` accessors (26.1 ms/s, docs/207 §9): `GameObject.GetComponent`, `InteractionObjectPair.Equals`, `GetObject`, `SimClock.ElapsedTimeInMinutes`, `get_Parent`, `get_ObjectId`, `get_LotCurrent`, ... once their offline fixtures exist (effort S 8 / M 11, no world image needed [inferred, docs/207 §9]).
3. Then the rest of N by ms/s, until the code cache cap (§6) or until a method's measured speed-up is < 1.05x.

**Stays interpreted** (the compiler never patches it):

- Class Y and D methods (yielding or on a yield path) and every method on EA's yield list (`sim_sleep` / `sim_chkyld*` `0x1A6`-`0x1A8` inside). Their self time is small (~20%, docs/207 §2) and the yield save/resume (docs/130 §1.6) must keep seeing EA frames.
- Methods with an EA native body (`RuntimeMethod+0x24 != 0`, 1,085 bodies, docs/130 §1.4, docs/220), with exception clauses (P1 first cut; §2.4 opens them later), with filter clauses (EA aborts on them, docs/130 §1.5), `.ctor` / `.cctor` (the may-yield test treats them specially, docs/130 §10.5), and methods that boxing / calls dominate with < 1.0x in P0 (`ObjectGuid !=`, boxed `Enum.Equals`, `DefaultComparer`, `Dictionary` foreach: 0.88-0.98x, docs/207 §7.3).
- Any method whose IL hash is not in the known set (§5): mod assemblies and mod-rewritten EA methods.
- Everything on a thread other than the sim thread (§4.3).

## 2. Compiled-to-compiled direct calls with lazy frames

### 2.1 What a call costs today

EA's call + return is 209 instructions (static) or 233 (virtual, cache hit), plus the dispatch head [verified, docs/139 §8.2]; the fast path cuts it to ~191-201, and the remaining cost is the frame protocol itself: ~55 stores per call into the record, the `MonoInvocation`, the depth and max depth, `ThreadContext+8` and the arena (store-bound, IPC ~3.5) [measured, docs/139 §8.4]. A compiler that keeps EA's frame cannot make the call much cheaper (docs/207 §8.1 (a)). P1's lever is to **not write the frame** while nothing can look at it.

### 2.2 The lazy call

At a compiled `call` / `callvirt` site whose target is a compiled method (§2.5 for `callvirt`):

1. Arguments are copied into the callee's arena exactly as EA does (arena bump inline, same widths, same dead high dwords; docs/139 §8.1). Args, locals and the eval stack stay in arena memory, as in P0 (`monohost-jit.cpp:1-15` header: no managed reference lives only in a register across anything that can collect) [verified for P0, docs/207 §7.1].
2. **Depth stays exact:** `state+0x140` += 1 and the max depth `+0x144` updated, so the 63-frame `StackOverflowException` fires at the same call as in EA (docs/130 §1.4, `0xE56290`). A lazy call is allowed only when the next record slot already holds a `MonoInvocation` (depth + 1 < the high-water mark `[ebp-0x30]`), the same rule the fast path uses (docs/139 §8.1), so materializing never needs EA's `alloca`.
3. One 16-byte entry is pushed on a per-thread **shadow stack** (callee `RuntimeMethod*`, caller ip as a word pointer, caller `esi`, arena base). The record, the `MonoInvocation` fields and `ThreadContext+8` are **not written**.
4. Control moves with a `jmp` to the callee's native entry; the callee returns with a `jmp` to the caller's return-site address (kept in the entry). **No native frames:** `esp` stays at `ves_exec_method_with_context`'s frame (`0xE53EB0`), so every EA handler reached through an exit, EA's `alloca`, and the native-raise `longjmp` to `0xE59FD0` (docs/130 §1.5) see the C stack they see today [inferred design rule].

### 2.3 When frames must exist, and materialization

A frame must be real (records + `MonoInvocation` as EA writes them) whenever anything reads the chain. Candidates [inferred list; step B1 closes it]:

| Reader | Path | P1 rule |
|---|---|---|
| Exception throw / search / finally | `throw` `0xE59449`, trace record `0xE53910`, search `0xE540AB`, finally pass `0xE54198` (docs/130 §1.5) | materialize before the throw exit |
| Yield save / may-yield test | `0xE53620`, `0xE53550` (docs/130 §1.6) | only interpreted code yields; any exit into interpreted code materializes first |
| Native transitions | IC call `0xE52630`, EA native bodies `0xE525C0`, P/Invoke, `callrun` delegates (docs/130 §1.8) | materialize: ICs can re-enter managed code (`0xD8A8E0`, 862 bodies, docs/220) and walk or raise |
| Stack traces | `StackTrace` / `Environment.StackTrace` icalls, ErrorTrap logging (docs/184), `TaskControl_GetTaskContext` `0xD83DD0` (docs/130 §9.4) | reached only through an IC: covered by the row above |
| Our managed sampler (`[ProbeSnap]` / profile) | reads the `MonoInvocation` chain from another thread | sampler also reads the shadow stack (our code); otherwise lazy frames are booked to the last real frame |
| GC | conservative scan of the collecting thread's stack + roots (docs/57 §4) | no materialization: the GC finds objects through arena words, not records [inferred: B1 must confirm that no object is reachable only through a `MonoInvocation` field, e.g. `+0x0C this`, which EA also keeps in the arena args] |
| Debugger hook `[0x11F4154]` | init-time only (docs/130 §1.5) | none; refuse to install if set |

**Rule that keeps this simple:** *every exit materializes, except the exits B1 proves frame-free.* An exit is frame-free when its EA handler reads none of the record, `MonoInvocation`, depth or `ThreadContext` fields, cannot throw, call, allocate into a collection that runs finalizers, or yield (e.g. `ldstr` from the cache, `ldsfld` on an initialized class, `box` of a primitive, if B1 shows them clean). Exception checks inside templates (null, bounds, overflow, divide) branch to a slow stub that materializes, then jumps to EA's exception builder with EA's `edi` / `esi` / `[ebp-4]` (docs/221 §1 lists the exits: null `0xE5947E`, index `0xE5949D`, overflow `0xE57AA5`, not finite `0xE5952A`).

**Materialize** = walk the shadow stack from its oldest pending entry and, per entry, write what EA's common push `0xE56287` and the fast path write (docs/139 §8.1: caller record, callee `MonoInvocation` `+0x00`-`+0x18`, `ThreadContext+8`, frame words `[ebp-4]`/`-8`/`-0xC`/`-0x10`/`-0x14`/`-0x2C`), then mark the entry real. Cost ~ one fast-path push per pending frame, paid only on the slow path.

**Return:** a compiled `ret` pops its shadow entry. If the entry is still lazy: restore `edi`/`esi`, depth - 1, arena pop, value copy as EA, `jmp` to the return site. If it was materialized: take the fast path's return (docs/139 §8.1 `ret` rules) and dispatch the caller's return-site word, which P0 already patches to `0x1A9` with an ip-table entry (every exit continuation is an entry, docs/207 §7.1), so control comes back to compiled code.

### 2.4 Unwinding

All unwinding is EA's: after materialization the chain is a normal EA chain, the search walks `MonoInvocation+0` parents, and a handler resumes at its mint ip; a handler ip inside a compiled method gets an ip-table entry so it re-enters compiled code. The shadow stack is truncated to the depth EA unwound to (search end and `leave` / `endfinally` exits reset it). P1 first cut compiles no method with clauses, so a handler is always in interpreted code; clause support is a later step (§7 S5).

### 2.5 Fast path and the `callvirt` inline cache

- Compiled code reads the **same** cache pair EA keeps in the method's data items (`[class, RuntimeMethod]`, `0xE549EB`, docs/130 §1.4). Hit and target compiled: lazy direct call; the native entry comes from a side table keyed by `RuntimeMethod*` (our memory, so EA's data items stay bit-equal). Miss, value-type receiver, null receiver, `con.callvirt` / `con.boxcallvirt`: materialize, exit to the live table entry, which is the fast path when `[MonoFast] CallPath=1` (docs/139 §8.6) or EA's handler; that handler writes the cache as today.
- Exits read the **live** jump-table entry at exit time (one load), not the copy cached at install (`g_opHandler`, `monohost-jit.cpp:123`, `:246`), so `CallPath`, Satb's handler swaps and `MintDivGuard` stay in effect without a recompile (docs/207 §7.8).
- Calls to interpreted callees are ordinary exits (materialize, then fast path). P1 needs the fast path on; with `CallPath=0` it still works, slower.

## 3. Exactness

The bar is EA's behaviour, bit for bit, including EA's bugs, unless a separate switch fixes them.

| Area | P1 rule | Source |
|---|---|---|
| Integer ops | P0 templates (150 base opcodes) as verified | docs/207 §7.1-§7.2 |
| `div.i4` / `rem.i4` INT_MIN / -1 | same unguarded `idiv`; the template tests `divisor == -1` and then jumps to the **live** table entry, so with `[EaCrashFixes] MintDivGuard` on the guard's handler decides, off EA's crash stays (no silent fix) | docs/213 §4, §8 |
| `mul.ovf.i4/i8` | reproduce EA's off-by-one at MIN (`0xE57B49`) exactly; docs/213 cases are the test | docs/213 §4 |
| Floats | docs/221 templates: x87 for arithmetic, `neg`, r4 stores, `conv.r4.r8`; `fucomip` compares without EA's `_isnan` calls; `conv.u4/i8/u8.r8` stay exits until the real-CPU `fisttp` check | docs/221 Summary, §5 |
| x87 control word | never touched; compiled code runs only on the sim thread, whose control word EA's handlers already use; `conv.r4.i4/i8` keep the unrounded double; `_ftol2` paths keep the CRT flag `[0x1234374]` choice by calling the same helper | docs/213 limits (1)-(2), §3 #7 |
| `brfalse.r8` long form | exit (EA's mis-decode stays EA's) | docs/213 §4 |
| EA superinstructions | expanded to their base sequences (as P0); the 22 were proven equal to the bases | docs/130 §9.1, docs/213 |
| Statics / class init | `ldsfld` / `stsfld` template only behind a per-site "class initialized" test (one load + branch); cold = exit to EA's helper `0xD89CD0`, which runs the `.cctor` at the same point as EA | docs/130 §9.2, docs/214 |
| Cold callees | a direct call needs the callee transformed (`+0x44 & 0x100`) and compiled; otherwise exit, so EA's transform, lazy icall binding and class init happen at the first call as today (docs/214: transforms continue for 30+ min, so this is a steady path, not a load-time one) | docs/214 §3-§4 |
| Boxing, `newobj`, strings, casts | exits in P1 (allocation through EA's code keeps GC and finalizer behaviour); allocation templates only after B1 marks them frame-free (S5) | docs/207 §7.1 |
| Exceptions | thrown at the same instruction with the same ip `+0x1C`, after materialization (§2.3) | docs/130 §1.5 |

**Gates.** (1) Verify mode as built in P0: every compiled call run twice from a restored reachable-graph snapshot, against a pure-interpreter reference (docs/207 §7.2), plus a **materialize check**: every Nth lazy call forces materialization and compares records, `MonoInvocation` and frame words with what the fast path writes for the same call (the docs/139 §8.3 compare stub). (2) The **differential corpus** (contributor task B4): the compiled set's methods with recorded and synthetic inputs (edge values from docs/213 and docs/221, the B5 accessor fixtures), run interpreted vs compiled in monohost; a new template or method ships only with corpus rows. (3) In game: `[MonoJit] Mode=2` (verify every Nth call), 0 mismatches on BP / IP / SV, seeded `rng-compare.ts` equal on/off. Not covered by verify: static fields of classes the graph walk does not reach, and the worker thread (docs/207 §7.2): B4 adds static snapshots of the compiled set's declaring classes.

## 4. GC and threads

1. **Conservative scan.** Compiled code adds no native frames (§2.2) and keeps references in arena memory, so the GC sees the same words as today; no root registration changes [inferred from the P0 rule, verified only offline: docs/139 §8.3 compared every live word at each call]. Shadow-stack entries hold `RuntimeMethod*` and arena pointers, no object references.
2. **Write barriers.** Boehm here has no generational or card barrier [inferred from docs/57's map, which lists none; B1 confirms]. The only barrier is **Satb** (docs/206 §7): every compiled heap store (`stfld.*`, `stind.*`, `stelem.*`, `stobj`, `initobj` once templated; the 35 store opcodes of docs/206 §3.3) gets `cmp byte [satbActive],0 / jne slow`; the slow path calls Satb's own log routine (first write only), then stores. Stores to the arena need nothing (the worker never reads another thread's frame). Cost while no job runs ~0.3-0.5 ns per heap store [inferred, docs/207 §7.8].
3. **The [AutShadow] worker runs interpreted code.** The `0x1A9` handler checks the thread (one TLS read, as `monohost-jit.cpp:829` does in verify); on the worker it dispatches the original word from the shadow, so the worker runs EA's handlers, which Satb swaps for its load checks. The worker pays one hash probe per patched word (entries and exit continuations only). Compiled code on the worker (with Satb load templates) is a later step, not P1.
4. **A/B accounting.** Compiling the sim thread shrinks what the worker can move off it, and the worker's re-score at the cut gets cheaper (docs/207 §7.8): every A/B states whether `[AutShadow]` was on.

## 5. Mod compatibility

- **IL-hash guard (reuse G-M6).** A method compiles only if its IL hash (`AutShadowCore.MethodIlHash`: FNV-1a 64 over key, IL with tokens as names, EH clauses) is in the known set (`tools/autshadow-il-hashes.ts`: EA 1.67 688 + simler90 94 hashes, docs/128 G-M6) [verified: built, offline only]. P1 extends the generator from the [AutShadow] closure to the compiled set. ts3fixes computes the hashes at load (it has reflection); ts3native compiles only `RuntimeMethod*`s ts3fixes marks (GetOption2 channel or a shared table). EA has its own IL-hash guard for its native bodies (`0xD89D40`, docs/220), which shows the same idea works on this runtime.
- **Mod assemblies stay interpreted** (NRaas, store content, our own ts3fixes hooks): they are the `?` 4-6% of self time (docs/207 §2), and their overrides reach the compiled set only through `callvirt`, whose cache target is then an interpreted method: an exit.
- **Fall back to the interpreter** on: hash miss, assembly MVID not in the known list, a mod that patches mint code or the jump table (MonoPatcher ForceJIT byte `0xE5637D`, `[GcRootCheck] TailWatch` at `0xE563B2`: refuse as the fast path does, docs/139 §8.2), a mod override that yields (it is interpreted, and the exit materialized the compiled frames below it: the yield save then sees a normal chain, and resume dispatches the caller's patched return word; test: the docs/139 6-deep yield task with a compiled middle frame).

## 6. Where code lives, memory

- **In-game JIT in ts3native**, compiling at EA's transform (`JIT_XFORM` `0xE643B0` hook, `monohost-jit.cpp:90-120`), one method at a time. **Ahead-of-time per assembly hash is rejected for P1:** mint code, data items and `RuntimeMethod*`s are heap pointers produced per run by EA's transform, so an AOT image would need a relocation scheme for every embedded pointer; compile cost is a few µs per method at its first call [inferred from P0], not worth the scheme.
- **Memory.** P0: ~650 bytes of code per method, ~150 KB for 234 instantiations [measured, docs/207 §7.7]. P1 in game: a few hundred to ~2,000 instantiations [inferred] = 0.2-1.3 MB. Reserve one fixed region at startup (2 MB code + 128 KB ip table of 8,192 entries + 64 KB side tables), so play never fragments the heap; when full, stop compiling (counter). Next to docs/66's 1.2-1.5 GB largest free block in play this is < 0.2%, and it is address space, not commit growth. W^X: emit into RW pages, `VirtualProtect` to RX and `FlushInstructionCache` before publishing the ip-table entry and patching the opcode word (docs/207 §7.7).
- **Runtime off** (for `--ab`): restore the original opcode words at the top of a `MonoScriptHost_Simulate` tick; compiled code stays in the cache. A yielded task whose saved ip is a continuation word then resumes at the original opcode, so the flip is safe both ways [inferred; verify in S1].

## 7. Build plan

Hours = wall clock with agents (docs: an agent build ~10-60 min, a live job ~8-15 min). Every step has its own switch, off by default; counters and ini keys reserved with `tools/registry.ts` when built.

| Step | What | Hours | Offline gate (monohost) | In game | Switch | Contributor? |
|---|---|---|---|---|---|---|
| **B1** | Exit and frame-reader census: per exit handler, what it reads/writes of the frame, can it throw / call / allocate / yield; every reader of records and `MonoInvocation` (ICs, sampler, icalls); GC roots through `MonoInvocation` | 3-5 | none (static, docs/213 harness can confirm per handler) | none | - | **yes** |
| **S1** | Port P0 to ts3native `[MonoJit]`: templates + docs/221 floats, live-table exits, Satb barrier, div-guard test, sim thread only, IL-hash guard, reserved code region, W^X, verify (`Mode=2`, `VerifyEvery`) | 8-12 | verify 0 mismatches, `MintBench` -15% or better with fast path, 39/39 selftests | `jit-verify` BP/IP/SV 10 min (~40 min); `jit-ab` 7 jobs (~85 min) | `[MonoJit] Mode=0/1/2`, runtime `jitSwitch` | no |
| **B4** | Differential corpus + monohost CI | 4-8 | corpus runs clean on stock vs compiled | none | - | **yes** |
| **S2** | **B3**: lazy-frame direct calls in monohost (shadow stack, materialize, exception stubs, callvirt side table, sampler reads the shadow stack, materialize check) | 12-20 | verify + materialize check 0 mismatches; throw-through-compiled, 63-deep, yield-through-compiled cases; `MintBench` | - | `[MonoJit] Direct=0/1/2` (2 = materialize check) | **yes** (monohost part) |
| **S3** | Port S2 to ts3native | 4-6 | same as S2 | `jit-verify` + `jit-ab` again; 1 clean night with `Direct=1` (crash check) | as S2 | no |
| **S4** | Inlining tier 2 (null-checked `ldfld` on any base, exit before any write; value-type `this`) + B5 accessor fixtures | 6-10 | verify, corpus rows for the 19 accessors | `jit-ab` (~85 min) | `[MonoJit] Inline=0/1/2` | fixtures: **yes** |
| **S5** | Frame-free exit templates (B1 list: statics behind the init test, primitive `box`, `ldstr`) and methods with clauses | 6-10 | verify + corpus | `jit-ab` | `[MonoJit] Wide=0/1` | no |

Total ~43-71 h plus ~4 verify/ab rounds (~2 h of game time each) and 1-2 clean nights. B1 and B4 can run now in parallel with S1; S2 needs B1. **P2 / P3** (register allocation, wider templates, compiled code on the worker) only after S3's in-game number.

## 8. Gain per step [inferred] and risks

Base numbers: Mono bucket BP 334 / IP 336 / SV 358 ms/s (docs/160); N self time ~250 ms/s; call path ~78 ms/s non-stall before the fast path (docs/139 §5), ~60-70 after it [measured fast-path saving 7.7-17.7]; the `callvirt` receiver stall (~26 ms/s, docs/139 §6) no compiler removes.

| Step | Gain at BP-like scaling (1.0x) | IP-like (2.4x of projection) | Basis |
|---|---|---|---|
| S1 body compiler, EA frames | ~10-20 | ~20-30 | docs/207 §8.4 "compiler on never-yielding code" realistic 24-33, minus the fast path's share on that code (~72% of 7.7-17.7) |
| S2/S3 lazy direct calls | ~10-25 | ~15-30 | ~35-50% of calls are compiled-to-compiled (offline 78% of compiled calls sit inside a compiled caller, docs/207 §7.2, cut by exits and the compiled-set limit) x ~60-70 ms/s x ~75% of the per-call stores removed |
| S4 inlining tier 2 + accessors | ~3-8 | ~5-10 | 26.1 ms/s of accessors (docs/207 §9), getters and compares lose the call entirely |
| S5 wider templates | ~3-8 | ~5-10 | fewer exits = fewer materializations, longer lazy windows |
| **P1 total** | **~26-61, realistic ~30-45 ms/s** | upper ~45-80 | on top of the fast path's 7.7-17.7 |

Stop rules: S1 under ~5 ms/s in game: stop before S2 (the body compiler then does not carry the lazy frames' risk). S3 under ~8 ms/s on top of S1: keep S1, drop S2/S3.

**Risks, top 3:**

1. **A frame reader we did not find** (EA host code, a mod's icall, our own tools) sees a lazy chain: wrong stack traces at best, a crash in EA's search or yield save at worst. Mitigation: B1 census, "every exit materializes unless proven frame-free", materialize-check mode in game, `Direct` switch separate from the body compiler. Added 2026-11-03 (Rodney), in order:
   - **Reader trap (gate for S2/S3):** a verify build poisons the unwritten fields of lazy entries and traps reads of the chain fields (guard page or hardware breakpoint on `ThreadContext+8` / the `MonoInvocation` words); each hit logs caller + stack. Plus a static Ghidra xref list of every access to those fields. No S2/S3 build until a correctness run with all frame readers active shows 0 unknown readers.
   - **Valid chain head as fallback:** lazy entries still keep the chain head and method pointer valid, so a reader we missed sees a correct chain with missing contents instead of garbage.
   - **Narrow scope first:** start with hot leaf methods from the exit census that cannot yield, throw out or call back into script; widen the allowlist step by step, one verify run per step.
   - **Recovery:** per-method kill switch; SEH catch around compiled code that marks the method bad, falls back to the interpreter and counts it; IL-hash guard; shadow mode in monohost (both paths, compare results) before game runs.
   - **Reader-heavy correctness runs:** sampler, NRaas ErrorTrap, ScriptError dumps and saves during Sim waits on, crash logging, long soaks, save matrix.
2. **Exactness drift in the real script mix** that the offline verify does not reach: statics outside the graph, x87 control word on a thread D3D changed, the `_ftol2` CRT flag, mod overrides behind a known hash. Effect: behaviour change or desync of seeded runs. Mitigation: verify mode in game, `rng-compare.ts` on/off, IL-hash + MVID guards, corpus rows per template.
3. **Gain below projection**: the in-game ratio is noisy (BP 1.0x vs IP 2.4x), the call protocol is store-bound and the receiver stall stays; the compiled sim thread also lowers [AutShadow]'s measured offload gain. Mitigation: per-step `jit-ab` with stop rules; A/Bs state the [AutShadow] setting.

## 9. Open questions

1. SV `mintcall-ab` and a quiet rerun: does the BP 1.0x or the IP 2.4x ratio hold?
2. Can any object be reachable only through a `MonoInvocation` field during a lazy window (B1)? If yes, lazy frames must keep that field written.
3. Monohost CI: monohost needs the user's own `TS3W.exe` and script assemblies, which CI must never hold; CI can build monohost and run the template encoder self-check, but the corpus needs a self-hosted runner or stays a local pre-PR check.
4. Hash source for ts3native: ts3fixes marks methods at load (reflection) vs ts3native hashing mint/IL itself (EA's `0xD83680` style, docs/220).
5. How many instantiations the game compiles in a session (cache sizing) and whether compile cost at first call shows up as a hitch (count it in S1's verify jobs).
