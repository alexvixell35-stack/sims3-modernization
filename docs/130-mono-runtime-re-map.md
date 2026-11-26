# 130. Embedded Mono 1.2.3 runtime: consolidated RE map and gap register

The Mono counterpart of docs/116 (routing go/no-go). It consolidates docs/29, docs/30, docs/56, docs/57, docs/58, docs/118-124, docs/126 and `docs/data/mono-glue-map.csv` into one map of the runtime as it exists in the Steam `TS3W.exe` 1.67 (image base `0x400000`). It adds the static closes made for this note (§8, ledger docs/51 §175). It ends with a ranked gap register for (a) script stutter (interpreter fast paths, docs/29 §2 options b/c) and (b) managed code on a second thread (docs/30, docs/120, M5/M6).

Static only. Raw disassembly of `game-copy/bin/TS3W.exe` (capstone over the PE file, Ghidra function bounds from `game-copy/analysis/functions.csv`), plus the existing sim-thread samples in `probe-out/profile/*simstack*.jsonl` (206 files, 77,426 samples, collected before 2026-10-20). No game run, no build.

Tags in the tables: **verified** = read in the disassembly, a decompile, a host run or a live counter (the source doc is named); **inferred** = Mono 1.2.3 source knowledge or reasoning, not confirmed instruction by instruction; **unknown** = not known, with a gap id in §7. Each component table has a Tag column; §6 counts them.

## Summary

- **The interpreter is now mapped down to the opcode.** `ves_exec_method_with_context` `0xE53EB0` dispatches a 16-bit mint opcode through one jump table at `0xE59880` (449 entries, opcodes `0x000`-`0x1C0`, 394 distinct handlers). The opcode **name table** is in the exe at `0x11949E0` (425 names, `nop` .. `sim_chkyld2`) and the operand-length table at `0x1195088`. The full table (opcode, name, handler address, measured self samples) is `docs/data/mint-opcodes.csv` [verified].
- **EA added 25 opcodes to mint:** the three task intrinsics `sim_sleep` `0x1A6`, `sim_chkyld` `0x1A7`, `sim_chkyld2` `0x1A8` (docs/119), and **22 superinstructions `0x1AB`-`0x1C0`**. These are fixed sequences of 2 to 15 base opcodes (for example `ldthis+ldfld.o`, `ldloc.i4+add1.i4+stloc.i4`, and a 15-opcode `Equals`-shaped body). They are fused at the end of every transform by a peephole pass `0xE5C660` -> matcher `0xD89930` (FNV-1a hash of the opcode window against a 72-byte-per-entry table at `0x108FCD8`). This is EA host code, not upstream Mono [verified]. So docs/29's "superinstructions" idea already exists in a fixed, profile-shaped form.
- **Calls do not recurse in C.** One C activation of `0xE53EB0` holds up to 63 nested interpreted frames in a 63 x `0x130` record array on its own stack (frame `0x4C74` bytes). Depth `state+0x140` past `0x3F` throws `StackOverflowException` ("The maximum stack depth for the interpreter has been exceeded."). `callvirt` has a **per-call-site monomorphic inline cache** (class, runtime method) in the method's data items; a miss calls `0xE51FC0` [verified].
- **Exceptions:** a two-pass search over the frame chain. Typed catch uses `mono_object_isinst`, and finally blocks run from a 64-entry stack per frame record. **Filter clauses abort** (`g_assert_not_reached`). Unhandled exceptions return through `state+0xC`. Native-raised ones `longjmp` to the `setjmp3` in `0xE59FD0` through the thread's `ThreadContext` [verified].
- **Where interpreter time goes** [measured, existing samples]: interpreter self is 25.5% of sim-thread samples. Within it, call/return machinery (callvirt 19.5%, call-common frame push 7.8%, call 6.6%, frame return 5.3%, ret 1.7%, newobj 1.9%) is **~43%**, dispatch 6.3%, field and argument moves most of the rest. Superinstructions take 3.2%. The hottest instruction (`0xE54AEC`, 7.7% of interpreter self) sits right after `callvirt` loads the receiver's vtable: the cost is a **memory stall on the receiver object, not the method lookup**. Lazy compile (`0xE643B0`) never appears in play samples: transform is a load-time cost only.
- **GC, loader and threading:** unchanged from docs/57, docs/58, docs/120 with three corrections folded in: the static image sections are **not** roots (`GC_no_dls` is set unconditionally at `0xE69598`, docs/119 I-119-37, now verified statically for the game too); `0xD790A0` (EA interface-offset layout) always runs **inside** the loader lock (I-118-4 closed); EA's `GeneralAllocator` (eglib `g_malloc`) takes its own critical section, so Mono's `g_malloc` is thread-safe when that lock is configured (the lock is always configured: §9.4, G-B7 closed).
- **Second static pass (§9, ledger §179):** all 22 EA superinstructions do exactly what their base sequences do (no mismatch). The static-field helper `0xD89CD0` is **not** a 1.8% cost: that share came from stale return addresses in the stack scan; on the live chain it is ~0.04-0.07% of sim-thread samples, so a per-field cache (designed in §9.2) is not worth building. Only 3 `[ThreadStatic]` fields exist in the script assemblies (all in `System.AppDomain`) and no allow-listed closure reaches them (G-B4 closed, no fix needed).
- **Third static pass (§10, ledger §184):** the four dormant critical sections are `monitor_mutex`, `mono_delegate_section`, `allocator_section` and `finalizer_mutex`, never entered (G-B5 closed). `mono_defaults+0x80` is `transparent_proxy_class` and this build **never initialises it** (with nine other remoting/thread fields), so every proxy check compares with NULL and is dead (D21). `ThreadContext` is upstream mint's (`+0x0C` env frame, `+0x14` search-for-handler, `+0x15` write-only managed-code flag); frame record `+0x14`-`+0x24` is `newobj` state. The yield save/resume is fully mapped and costs ~0.04% on the live chain (G-I9 closed). A call-consistency filter over the whole stack scan replaces every "first 12 frames" share in §1.9: IC call path 7.7% -> 1.7%, native bodies 4.7% -> 0.7%, MTEH path ~6 ms/s instead of 20-30 (G-I7 drops below G-I10).
- **Counts** (§6, verified / inferred / unknown): interpreter 63 rows (54 / 5 / 4); GC 25 (19 / 5 / 1); loader, metadata and caches 20 (15 / 5 / 0); threading 14 (13 / 1 / 0); EA modifications 21 (16 / 5 / 0).
- **Top gaps** (§7): (a) which **managed methods** carry the call overhead. The frame to method chain is now known statically (`MonoInvocation+4` = `RuntimeMethod*`, `+8` = `MonoMethod*`), so a managed sampler is one probe build. Then: getter inlining or new superinstructions for the top call sites and the `[IcLockCheck]` run (built, never run). (b) For M5: P4 `FrameTls` fails in game because `TlsAlloc` returns indexes >= 64 (fix with TEB expansion slots); then the `arenaRelNonEmpty` rate in game and F1 `0xD8A8E0` private records live.

## 1. Mint interpreter

### 1.1 Entry points

| Address | Name | Role | Tag |
|---|---|---|---|
| `0xE53EB0` | `ves_exec_method_with_context` | main loop; args `(state, ThreadContext)`; C frame `0x4C74` bytes (`_chkstk` `0xF7EE60`); entry `0xE53EB0`-`0xE540AA`, resume path at `0xE53F18` when `state+0x140 != -1` | verified |
| `0xE59FD0` | `interp_mono_invoke_with_state` | run or resume one task state; makes the `ThreadContext` (`GC_malloc(0x18)`, or `alloca` when `state+0x14B`), stores it in TLS `[0x11F4160]`, `setjmp3` at `0xE5A3CE`, frees the state on task end (`0xE52DD0`) | verified (docs/29, docs/57, docs/120) |
| `0xE5A600` | `interp_runtime_invoke` | stack-built state (`+0x14A = +0x14B = 1`), no yield, clears the TLS slot on exit; target of the `mono_runtime_invoke` slot `[0x1195B60]` (`0xEA1090` = `jmp [slot]`) | verified (docs/57, docs/118 D12) |
| `0xE52B60`, `0xE52B90` | runtime-invoke entries | `0xE52B60` <- `0xD83680`; both transform first (`0xE643B0`) and use TLS `[0x11F4160]` | verified (docs/29) |
| `0xE52D70` / `0xE53E80` / `0xE52DD0` | task state alloc / release / free | `GC_malloc(0x150)`; `[0]` method, `[1]` object, `[2]` args, `[4]` exception slot, `+0x140`/`+0x144` = -1, `+0x148` yieldable, `+0x14A` first run | verified (docs/119 §9.5) |
| `0xD7EED0`, `0xD7F000`, `0xD7F210` | `ScriptHost_InvokeDirect_A`, `ScriptHost_RunTask`, `ScriptHost_InvokeDirect_B` | host wrappers; write `host+0xBC8/+0xBD0/+0xBD4` | verified (docs/56 §2) |
| `0xD8A8E0` | `ScriptHost_NativeToManaged` | native body -> managed callee; global call-record list `[0x11EE5A4]`; calls `0xE53EB0` directly | verified (docs/56, docs/122 §12.1) |
| `0xD75FA0` | `ScriptHost_InvokeDelegate` | host delegate call; boxes with `mono_value_box`; writes `host+0xBC8` | verified (docs/56) |
| `0xEA1400` | `mono_runtime_delegate_invoke` | finds `Invoke`, calls through `[0x1195B60]` | verified (docs/118 D12) |
| `0xE5A680` | `mono_interp_init` | TLS key, runtime-method section `0x11F4130`, 4 interpreter ICs, called at `0xD7E60C` in `MonoScriptHost_Init` `0xD7E560` | verified (docs/119 §1.2) |
| `0xE643B0` / `0xE5CA90` | transform / generate code | IL -> mint under `0x11F4174`; result at `MonoMethod+0x14` (RuntimeMethod) | verified (docs/29); 0 play samples inside it (§1.9) |

### 1.2 Dispatch, opcodes, superinstructions

| Item | Finding | Tag |
|---|---|---|
| Register model in `0xE53EB0` | `edi` = mint ip (mirrored at `[ebp-4]`), `esi` = eval stack pointer in 8-byte slots, `[ebp-8]` = current `MonoInvocation`, `[ebp-0x10]` = current `RuntimeMethod`, `[ebp+8]` = task state, `[ebp+0xC]` = `ThreadContext` | verified |
| Dispatch | head `0xE544D5`: `movzx` of the 16-bit opcode at `[edi]`, `cmp 0x1C0`, `ja` -> `0xE596C2`, `jmp [op*4 + 0xE59880]`. One indirect jump per instruction, no threaded code; every handler ends in `jmp 0xE544D5` | verified |
| Jump table | `0xE59880`, `0x1C1` entries, 394 distinct targets (shared handlers for type variants, for example the `mono_icall_*` family `0x199`-`0x1A1` -> `0xE57F6F`) | verified |
| Name table | `0x11949E0`: 425 `char*` (`0x000` `nop` .. `0x1A8` `sim_chkyld2`, then an empty string); used by the dumper `0xE5A7C0` (only caller: transform debug output at `0xE63BC8`) | verified |
| Operand lengths | byte table `0x1195088` (`mono_interp_oplen` shape: 1 for `nop`, 3 for `dup.vt`, ...) | verified (table read; full semantics inferred from mint) |
| Unimplemented | `0x1A9`, `0x1AA` and out-of-range -> `0xE596C2` ("Unimplemented opcode: %04x %s at 0x%x") | verified |
| EA task opcodes | `0x1A6` `sim_sleep` `0xE5939C`, `0x1A7` `sim_chkyld` `0xE544EE`, `0x1A8` `sim_chkyld2` `0xE5451A`; emitted by transform `0xE5DD1C` for `Simulator.Sleep`/`CheckYieldingContext` when the class cached at `[0x1220F34]` matches | verified (docs/119 §11.2) |
| **EA superinstructions** | `0x1AB`-`0x1C0` (22). Descriptor table `0x108FCD8`, 72 bytes per entry (`u32` hash, `u16` count, `u16` base opcodes), entry 0 empty. Matcher `0xD89930(window, count, &out)`: FNV-1a (`0x811C9DC5`, through `0x4FA560`) over the window bytes, compare hash, count and bytes, write `0x1AA + index`. Called only from the peephole pass `0xE5C660` (`0xE5C8CA`), itself called once per method at the end of generate-code (`0xE63C2F`). Sequences in `docs/data/mint-opcodes.csv`. All 22 handlers checked against their base handlers: same results, branch targets, stack effect and exception positions (§9.1) | verified |
| Second-level jump tables | `0xE59F84` (from `0xE57400`): element-type switch of the shared `ldelem.*` handler `0xE573D7`, 11 entries keyed by `op - 0x105` (`ldelem.u4` and `ldelem.ref` share a target); `0xE59FB0` (from `0xE57506`): same for the shared `stelem.*` handler `0xE574DA`, 8 entries keyed by `op - 0x113` | verified (§9.4, closes G-I8) |

### 1.3 Frame layout

| Structure | Layout | Tag |
|---|---|---|
| Frame record array | `ebp-0x4C74`, record stride `0x130`, index = `state+0x140` (depth), limit `0x3F` (`0xE56290`) | verified |
| Frame record (`0x130`) | `+0x00` -> `MonoInvocation` (one `alloca(0x28)` per slot, reused, `0xE56311`), `+0x04` saved ip, `+0x08` saved sp, `+0x0C` saved `[ebp-0xC]`, `+0x10` saved `[ebp-0x14]`, `+0x14` **newobj-return flag** (copied from `[ebp-0x28]` at push: 0 from `calli`/`call`/`callvirt` `0xE5487E`/`0xE549DF`/`0xE54B44`, 1 from `newobj` `0xE56280`, the only four entries into the push; non-zero sends the return to `0xE561B0`, which pushes the constructed value), `+0x18` class under construction (`newobj` `0xE56174`), `+0x20`/`+0x24` 8-byte result slot of that `newobj` (object reference, or the zeroed value-type buffer passed as `this`; `0xE561AB`, `0xE5624D`, `0xE56217`); `+0x18`-`+0x24` live in the record of the frame that runs `newobj`, the flag in the callee's record. `+0x28`..`+0x127` finally-target stack (64 entries), `+0x128` its depth, `+0x12C` pending clause | verified (§10.4) |
| `MonoInvocation` (`0x28`) | `+0x00` parent, `+0x04` `RuntimeMethod*`, `+0x08` return-value slot, `+0x0C` `this`, `+0x10` arena base (args, locals, eval stack), `+0x14` stack args, `+0x18` eval stack base, `+0x1C` saved ip (throw site), `+0x20` pending exception, `+0x24` current clause | verified (`0xE56330`-`0xE56409`, `0xE59449`, `0xE540AB`) |
| Task state (`0x150`) | `+0x00` method, `+0x04` object, `+0x08` args, `+0x0C` unhandled exception out, `+0x10` exception slot (non-zero = caller catches), `+0x20` boxed return value, `+0x30` outer `MonoInvocation` template (10 dwords copied at entry), `+0x13C` arena chain, `+0x140` depth / resume point (-1 = fresh), `+0x144` max depth, `+0x148` yieldable, `+0x149` yield return, `+0x14A` first run, `+0x14B` stack-allocated | verified (docs/18, docs/57, docs/119 §9.5, this note) |
| `ThreadContext` (`0x18`) | upstream mint layout: `+0x00` domain (`mono_domain_get` at `0xE5A3E9`), `+0x08` current frame, `+0x0C` **env frame** (the outer `MonoInvocation` template `state+0x30`; a native raise stores the exception at `env_frame+0x20`, not at `ctx+0xC`), `+0x10` `jmp_buf*`, `+0x14` **search-for-handler** (set to 1 by the native raise `0xE53740` before `longjmp`; on the exception path `0xE592EC`/`0xE59302` it is cleared and the handler search `0xE540AB` runs, else the finally pass `0xE54198`), `+0x15` **managed-code** flag (1 while `0xE53EB0` runs, 0 around transform `0xE56380`/`0xE56395`, around `mono_object_new` in `newobj` `0xE56237`/`0xE56250`, and after exit; written only, no reader in the binary's interpreter code) | verified (§10.3) |
| `RuntimeMethod` (`0x48`) | `+0x00` mint code start, `+0x08` `MonoMethod*`, `+0x10` native thunk (`tramp.c`), `+0x14` data items, `+0x18` clause array (`0x18` bytes each), `+0x24`/`+0x28`/`+0x2C` EA native body (init, body, one-shot; binder `0xD9A7B0`), `+0x38` arena bytes per frame, `+0x3E` / `+0x34` / `+0x36` sizes of three consecutive areas in it (the eval stack starts at base + `+0x3E`; `[ebp-0xC]` and `[ebp-0x14]` point after the next two), `+0x3C` bytes cleared when `+0x44 & 0x800`, `+0x40` param count, `+0x41` clause count, `+0x44` flags (`0x100` no transform, `0x200` has this, `0x800` init locals) | verified offsets (this note, docs/104, docs/119 §9.4); area roles inferred |
| Arena | chunks of `0x7FC` from pool `[0x11EE508]`; chunk `+0` top, `+4` end, `+8` previous; inline bump `0xE563B2`, inline pop `0xE54437`/`0xE59800`, slow paths `0xD71900`/`0xD71A20`/`0xD71B70` | verified (docs/120 §9.1) |
| Frame to method (sampler) | `MonoInvocation+4` -> `RuntimeMethod`, `+8` -> `MonoMethod`. Closes docs/04's open `/rmname` item: `frame[1]` is the runtime method, not a `MonoMethod+0x10` match | verified (code); live use unknown (G-I1) |

### 1.4 Call and return

| Path | Finding | Tag |
|---|---|---|
| `call` `0xEA` / `con.call` | `0xE5493E`: callee `RuntimeMethod` from data item, pops args (`+0x40`), `this` when `+0x44 & 0x200`, transparent-proxy check against `mono_defaults+0x80` (`0x1220EC0` = `transparent_proxy_class`, which this build **never initialises**: the check compares with NULL and is never taken), then the common path | verified (§10.2) |
| `callvirt` `0xEC` | `0xE549EB`: data item pair `[class, RuntimeMethod]` = **monomorphic inline cache**; hit when `obj->vtable->klass == cached` (`0xE54AF9`), miss -> `0xE51FC0` (virtual lookup incl. proxy) and overwrite the pair (`0xE54B1D`); value-type receiver test `klass+0x14 & 8` | verified |
| Common push `0xE56287` | save caller into the record, depth++, `alloca` `MonoInvocation` on first use of the slot, transform on first call (`0xE643B0`, exception -> `+0x20`), arena bump or `0xD71900`, init locals (`+0x44 & 0x800`) | verified |
| EA native bodies | `RuntimeMethod+0x24 != 0` -> `0xE5925F` -> `0xE525C0` (calls the bound body); 1,085 bodies (docs/122 §12.1) | verified |
| ICs and P/Invoke | `callint` `0xF2` -> `0xD70AC0` (`jmp 0xE52630`): stackval marshal `0xE521A0`, thunk `RuntimeMethod+0x10`, interruption checkpoint `0xEBB480`; `calli.nat` `0xF0` `0xE5488A`; `mono_icall_*` -> `0xE52E70` | verified |
| Return | `ret`/`ret.void`/`ret.vt` -> `0xE543C7`: depth--, restore caller, arena pop (inline or `0xD71A20`), exception propagate (`+0x20` -> `0xE592E3`), copy return value, yield check `state+0x149` -> `0xE53620` (frame save) -> exit B `0xE59860`; depth 0 -> exit A `0xE597DE` | verified |
| Depth overflow | `0xE59414`: `StackOverflowException` (`0xE777E0`) with the text "The maximum stack depth for the interpreter has been exceeded." | verified |
| Why 63 frames per activation | upstream mint recursed in C per call [inferred]; a flat record array lets a yield save all frames of a task at once (`0xE53620`) | inferred |

### 1.5 Exceptions

| Step | Finding | Tag |
|---|---|---|
| Throw | `throw` `0xE59449`: exception -> `MonoInvocation+0x20`, ip -> `+0x1C`, `0xE53910` (stack-trace record; calls `0xE529A0`, which knows the string `superop`), then search `0xE540AB`. Runtime faults use `exception.c` builders (`0xE770E0` `NullReferenceException`, `0xE772A0`, `0xE77300`, `0xE777E0`) | verified |
| Pass 1 | walk `MonoInvocation+0` parents; skip methods with `flags & 0x2000` or `iflags & 0x1003` (pinvoke, internal call, runtime); clauses at `RuntimeMethod+0x18`, count `+0x41`, `0x18` bytes (`+0` kind, `+4` try offset, `+8` try length, `+0x14` class); typed catch by `mono_object_isinst` `0xEA3820` | verified |
| Filter clauses | kind 1 hits `g_assert_not_reached` (`interp.c` line `0x1C13`): a filter aborts the game. C# 2.0 never emits filters; a VB or IL-patched mod would | verified (code); mod exposure unknown |
| Pass 2 | `0xE54198`: per frame, finally/fault targets onto the record's 64-entry stack (assert `finallyIPdepth < FINALLYSTACKSIZE`), `leave` `0xE59315` and `endfinally` `0xE57E9E` drain it | verified |
| Debugger hook | `[0x11F4154]` non-zero skips the search | verified (flag); writer unknown, init-time |
| Unhandled | no handler and `state+0x10 == 0` -> `0xE597D2`: exception -> `state+0xC`, every frame's arena released, exit A | verified |
| Native raise | `0xE53740` (from `mono_raise_exception` `0xEA18F0`): TLS context -> exception at `env_frame+0x20` (`ctx+0xC` is the env frame, §10.3), `ctx+0x14 = 1`, `longjmp(ctx+0x10)`; no `jmp_buf` or an `ExecutionEngineException` prints "Nothing can catch this exception: " instead; target `setjmp3` in `0xE59FD0` (`0xE5A3CE`), second `longjmp` `0xE5A52C` | verified (docs/120 §3.1) |
| Arena after an exception | a task that ends by exception leaves frames on its chain; EA frees the state twice (`0xE52DD0` twice) -> pool duplicate | verified (docs/120 §9.1) |

### 1.6 Tasks and yields

| Item | Finding | Tag |
|---|---|---|
| May-yield test | `0xE53550(state, frames)`: `+0x148` set, and for every record up to the current depth: finally stack empty (`+0x128 == 0`) and method name not `.ctor`/`.cctor`. These are exactly the record states the save does not keep (§10.5) | verified (docs/119; finally rule §10.5) |
| Yield | `sim_sleep` -> `0xD70820(ticks)` -> `[0x11EE514]` vtable `+0x20` = `0xD77B20` (sleep map `host+0x1728`) -> `0xD70F60` sets `+0x149` on the running state | verified (docs/119 §9.5) |
| Frame save | `0xE53620(state, records, method)` on the return path when `+0x149` is set: one `GC_malloc_uncollectable(0x2C)` cell (`0xD714B0`) + `memset` per depth `0..state+0x140`, pointer stored at `state+0x3C + 4*depth`; `method` arg -> `state+0x14C`. Cell: `+0x00` `RuntimeMethod*`, `+0x04` `MonoMethod*`, `+0x08` ip as opcode-word index, `+0x0C` eval-stack depth in 8-byte slots, `+0x10`/`+0x14` the two area pointers (`[ebp-0xC]`/`[ebp-0x14]`) as offsets from the eval base, `+0x18`/`+0x1C` return-value slot and stack args as offsets into the parent's arena (0 at depth 0), `+0x20` arena base pointer (the arena stays allocated on the state's chain, it is not copied), `+0x24` `RuntimeMethod+0x38` arena size, `+0x28` `this`. Not saved: record `+0x14`-`+0x24` (newobj) and the finally stack | verified (§10.5) |
| Resume | `state+0x140 != -1` -> `0xE53F18`: per depth `alloca` a `MonoInvocation`, rebuild parent, args, return slot, arena base (cell `+0x20`, then cleared), eval base (`+0x3E`), ip, sp and the two area pointers from the cell; record `+0x14`, `+0x128`, `+0x12C` and `MonoInvocation+0x24` set to 0; the top frame gets the pending exception from `state+0xC`; then `0xE535E0` frees every cell (`GC_free` via `0xD71810`). Cost per yield = depth x (alloc + memset + free) | verified (§10.5) |
| Assert `pState->yieldReturn` | yield inside a native-to-managed call aborts (`interp.c:3595`, docs/18) | verified |

### 1.7 Per-thread and shared interpreter state

| State | Per thread? | Tag |
|---|---|---|
| TLS `[0x11F4160]` `ThreadContext` | yes (TLS) | verified (docs/56) |
| `[0x11F4170]` current state, `[0x11F416C]` record-array base | **global**; 9 reference sites; P4 moves them to TLS | verified (docs/30 §4, docs/120 §3.2) |
| `host+0xBC8/+0xBD0/+0xBD4`, call records `[0x11EE5A4]`/`[0x11944C8]`/`[0x11944CC]`, IC nesting `[0x11F2D84]`, pair `[0x11F4158]` | global host state; worker rules or F1 (`N2mPrivate`) | verified (docs/56, docs/120 §9.4) |
| Arena pool `[0x11EE508]`/`[0x11EE504]` | global, unlocked (P3-locked in M5) | verified (docs/57 §3.2) |
| Thunk hash `[0x11F4128]` | global, unlocked; filled once per signature | verified (docs/58) |
| Runtime-method hash `[0x11F4168]` | locked `0x11F4130` | verified (docs/58) |
| Init flags `[0x11F4148]`, `[0x11F414C]` (yield disabled), `[0x11F4154]` (debugger) | read-only in play | verified reads; writers inferred init-time |

### 1.8 Interpreter <-> native transitions

| Transition | Path | Tag |
|---|---|---|
| EA ICs | provider `{function, name}` tables -> `mono_add_internal_call` `0xE82550` -> `icall_hash` `[0x11F436C]`; managed side is `DllImport`, resolved by `mono_lookup_pinvoke_call` `0xE84D60`; 1,758 bindings | verified (docs/119 §1.2, I-119-13) |
| Native-call thunks | `tramp_create_thunk` `0xE51400` (only runtime code generation), cached at `RuntimeMethod+0x10` | verified (docs/58) |
| EA native method bodies | binder `0xD9A7B0` (FNV-1 of `<ret> <Class>:<Method>(<args>)`), table from `0xE3E570` (1,085) | verified (docs/119 §9.4, docs/122 §12.1) |
| IC transition with callback | `0xDA7FB0` -> `0xDA5970` -> callback `[0x11EE6CC]` (bound at `0xDA594E`) -> loader lock inside | verified (docs/29 §5.1); callback identity unknown (G-I3) |
| Native -> managed | `0xD8A8E0` (direct `0xE53EB0`), runtime invoke `0xE5A600`, task run `0xE59FD0`, delegate `0xD75FA0`/`0xEA1400` | verified (docs/56 I-56-5: sim thread only today) |
| Delegates in managed code | `Delegate::CreateDelegate_internal` is an interpreter IC (`mono_interp_init`); invoke of a delegate is a runtime-implemented method (`callrun` `0xF3` `0xE591ED`) | verified (registration, handler); invoke internals inferred |
| Exceptions out of ICs | `0xE52630` checks for a pending exception after the native call; raise from inside an IC is the `longjmp` of §1.5 | verified (docs/119 I-119-27) |

### 1.9 Measured cost split (existing samples)

Method: leaf-most TS3W frame of each sample (as docs/29 §5); an interpreter PC is attributed to the handler whose start is the nearest one below it, with six shared blocks split out (exception search, frame return, dispatch, call common, exit A/B). Handler regions are contiguous in the exe, so out-of-line code inside a region counts for that region. 206 files, 77,426 samples; 19,712 (25.5%) have their leaf in `0xE53EB0` [measured].

| Region | Share of interpreter self | Share of all samples |
|---|---|---|
| `callvirt` `0xE549EB` | 19.5% | 4.95% |
| call common (push, arena, transform) `0xE56287` | 7.8% | 1.98% |
| `call` `0xE5493E` | 6.6% | 1.67% |
| dispatch `0xE544D5` | 6.3% | 1.60% |
| frame return `0xE543C7` | 5.3% | 1.35% |
| `ldfld.i4/.o/.p` `0xE5672C` | 4.9% | 1.25% |
| `stinarg.i4/.o/.p` `0xE586CF` | 3.9% | 1.00% |
| `isinst` `0xE56502` | 3.2% | 0.82% |
| `ldthis` `0xE583A5` | 2.5% | 0.63% |
| `ldsfld.o` `0xE56E66` | 2.4% | 0.60% |
| `newobj` `0xE56152` | 1.9% | 0.48% |
| `ret` `0xE591FF` | 1.7% | 0.42% |
| all 22 superinstructions | 3.2% | ~0.8% |

Inside `callvirt`, 1,518 of 3,794 samples sit at `0xE54AEC`, the instruction after the receiver's vtable load, and 449 at `0xE54AFD`, after the cache compare. So the cost is a cache miss on the receiver object, not the lookup [inferred from PC placement]; the miss path `0xE51FC0` is 0.51% self, mostly from `0xE59FD0`, not `callvirt` (docs/29 §5). Inclusive shares of helpers (first 12 TS3W frames, 74,363 samples with a TS3W frame): native IC call `0xE52630` 8.1%, EA native bodies `0xE525C0` 4.9%, static-field helper `0xD89CD0` 1.8%, yield frame save `0xE53620` 1.4%, `0xD8A8E0` 1.5%, IC transition `0xDA5970` 1.2%, transform `0xE643B0` 0.00%.

**Correction (§9.2, I-179-5):** these inclusive shares are upper bounds. The stack scan reports stale return addresses left in dead stack slots, so a helper "in the first 12 frames" is often not on the live call chain. For `0xD89CD0`, 1,341 of its 1,371 samples have the return address after its last call (`0xD89D35`) at frame depth 1-11 with an interpreter leaf, which that tiny leaf copy cannot produce; only 26 samples (0.035%) are consistent with a live chain. Frames 0-1 only give 0.50% for `0xE52630`, 0.30% for `0xE525C0`, 0.39% for `0xD8A8E0`, 0.25% for `0xDA5970`, 0.07% for `0xD89CD0` and 0.07% for `0xE53620` (lower bounds for helpers with deep callees, close to exact for leaf-ish ones) [measured, existing samples].

**Live-chain shares (§10.6, I-184-9):** every §1.9 inclusive share recomputed with a call-consistency filter over the whole scanned stack (frame 0 = PC; a later entry counts only if the instruction before it is a call into the current function, or an indirect call while the current function is address-taken). 206 sim-thread files (worker files excluded), 75,742 samples; % of all samples:

| Helper | First 12 frames (old rule) | Frames 0-1 | Live chain | Change |
|---|---|---|---|---|
| native IC call `0xE52630` (incl. IC bodies) | 7.74% | 0.47% | **1.71%** | ~4.5x lower |
| EA native bodies `0xE525C0` | 4.73% | 0.28% | **0.72%** | ~6.5x lower |
| `0xD8A8E0` native -> managed | 1.38% | 0.37% | **0.43%** | ~3x lower |
| static-field helper `0xD89CD0` | 1.76% | 0.07% | **0.04%** | as §9.2 |
| yield frame save `0xE53620` | 1.30% | 0.07% | **0.04%** | ~30x lower (G-I9) |
| IC transition `0xDA5970` | 1.15% | 0.24% | **0.99%** | real |
| IC lock callback path `0xDA7FB0` | 1.15% | - | **0.72%** | mostly real |
| loader lock `0xE85020` + unlock `0xE85030` | 0.18% + 1.39% | - | **0.14% + 0.73%** | ~9 ms/s, not 14 |
| type-object path `0xEBBDE0` (incl. `0xEA8720`) / `0xEA8A00` | 0.81% / 1.09% | - | **0.62% / 0.36%** | MTEH ceiling ~6 ms/s (G-I7) |
| `callvirt` miss `0xE51FC0` | 1.27% | - | **0.50%** | self 0.47% |
| transform `0xE643B0` | 0.00% | 0.00% | 0.00% | - |
| interpreter `0xE53EB0` / task run `0xE59FD0` | 34.3% / 30.8% | 27.2% / 26.8% | 26.6% / 24.4% | - |

The live chain is still an upper bound where an indirect call is accepted without knowing its target (IC bodies, native bodies), and a lower bound where the scan missed a real frame.

## 2. GC (Boehm 7.x, EA build)

| Item | Finding | Tag |
|---|---|---|
| Version | Boehm 7.x (7.1 by env-var set and shape), Win32 thread table kept, `LOCK()` compiled out (`GC_allocate_ml` `0x1234320` never entered) | verified table and lock; version inferred (docs/57, docs/120) |
| Allocation entries | `GC_malloc` `0xE4BE30`, `GC_malloc_atomic` `0xE4BDC0`, `GC_malloc_uncollectable` `0xE4BF90`, `GC_malloc_explicitly_typed` `0xE50DE0`, all lock-free fast paths, slow path `GC_generic_malloc` `0xE4BCE0` -> `GC_allocobj` `0xE4A2B0` / `GC_alloc_large` `0xE4BA90` | verified (docs/57 §1) |
| EA fast-path edit | `0xE4C1E0` after each pop: clear mark bit, store `GC_gc_no` at block `+0x18` | verified bytes; purpose inferred |
| `GC_free` | `0xE4BEB0`, used by task teardown and host | verified |
| Mono wrappers | `mono_object_new` `0xEA36F0`, `mono_string_new` `0xEA40B0`, `mono_array_new` `0xEA30F0`, `0xEA3140` (49 callers) | verified (docs/29 §4.1) |
| `MonoObject` header | vtable pointer only, no sync word (boxed value at `+4`) | verified (I-119-10) |
| Roots | explicit `GC_add_roots` table `0x122FB14` (Mono domain, `mono_gc_init`, runtime-method hash), uncollectable blocks, finalizer/link tables, free-list mark bits, collector registers `0xE4BA10`, collector stack via `[0x11949CC]` -> `0xD71820` | verified (docs/57 §4, docs/120 §1.2) |
| Image sections not roots | `mono_gc_base_init` `0xE69570` stores `GC_no_dls [0x11949A8] = 1` unconditionally at `0xE69598`; its only other references are the two reads in `GC_push_roots` and the env setup in `GC_init` (`0xE4E2A8`). So `GC_register_dynamic_libraries` never runs in the game either | **verified (this note, I-130-6)** |
| TLS | not a root | verified (docs/57 §4) |
| Thread table | 256 x `0x14` at `0x1221000`; only `GC_thr_init` registers (the `GC_init` thread = sim thread, live) | verified (docs/29, docs/120 I-120-1) |
| Stop-the-world | `SuspendThread`/`ResumeThread` on registered threads except the caller, under `GC_write_cs` `0x1222580` | verified (docs/120 §2.1) |
| Calls with the world stopped | `GC_add_current_malloc_heap` `0xE4EC40` never runs on NT; heap growth may reach EA tracker lock `0x9336F0` only when `[0x11DC18C] != 0` | verified (docs/120 §2.2.1); tracking mode 0 in play inferred (I-120-4) |
| Incremental | `GetWriteWatch` on `MEM_WRITE_WATCH` sections; no write barriers | verified (docs/57 §6) |
| Collection driver | `GC_try_to_collect` `0xE4A050` from `MonoScriptHost_Simulate` (per-frame step), `GC_gcollect` `0xE4A0B0` from host `0xD702A0`; budget counter `[0x11F2F98]` | verified (docs/57 §7, glue map) |
| Heap source | `GC_win32_get_mem` = EA `0xD70900` (`VirtualAlloc`, `MEM_WRITE_WATCH`) | verified (docs/118 D4) |
| Heap size defaults | 40 MB initial / 8 MB growth patched by ts3native | verified (docs/15) |
| Finalizers | on demand; only `mono_gc_invoke_finalizers` `0xE697F0` on the sim thread; queue `[0x11F303C]` | verified (docs/57 §5) |
| GC handles | table `0x11959E0`, no lock (`handle_section` `0x11F42F0` never entered); `GC_dont_gc` inc/dec only from handle growth | verified (docs/58) |
| Weak links | `0xE4AAC0`/`0xE4ABE0` via `0xE696C0`/`0xE696E0` | verified |
| Typed descriptors | `GC_make_descriptor` `0xE50F90` at vtable creation, unlocked against a collection | verified (docs/56 §4) |
| Abort sink | `0xD707E0` writes to address 0 after formatting | verified bytes; all 30 callers being error paths inferred (I-118-2) |
| Empty lock hooks | 36 calls to `0x5BFB90`, 42 to `0xC0FD60` from Boehm | verified bytes; role inferred (I-118-3) |
| EA "never stop" expand | `GC_collect_or_expand` -> `0xD70800` -> `0xE4A320` | verified (docs/118 D4) |
| How the game roots task states | states are collectable `GC_malloc` blocks; held by host structures | unknown (G-B11, I-119-41) |
| P3 (M5 lock) | 17 detours + `0xE4B3F0` replacement + `0xE4EC40` ret = 19 sites; built, in-game smoke clean | verified (docs/30 §14, PLAN 2026-10-20) |

## 3. Loader, metadata, class init, caches

| Item | Finding | Tag |
|---|---|---|
| Image source | all assemblies from S3SA resources through EA `0xD6FCB0` on an image-cache miss in `mono_image_open_full` `0xE84140` | verified (docs/119 I-119-2) |
| Assembly hooks | stock only, one caller each (`0xE66240`) | verified (I-119-3) |
| `loader_mutex` | `0x11F43A8`, wrappers `0xE85020`/`0xE85030` (65/89 sites), inline in 5 loader.c functions | verified (docs/29 §5.1, docs/58 §2) |
| Class/name/method/array/generic caches | loader lock for lookup and insert; some hit paths still lock (`0xE6FA90`, `0xE85810`, `0xE6E050`, `0xE706A0`, `0xE6DD30`) | verified (docs/58 §1) |
| Class init | `mono_class_init` `0xE6EE20`, fast exit `class+0x14` bit 0 | verified |
| EA interface-offset layout | `0xD790A0` (EASTL hash map, writes `class+0x46/+0x48/+0x4C`); **every call site holds the loader lock**: `0xE6EE20` (`0xE6F443`, `0xE6F537` between lock `0xE6EE57` and unlock `0xE6F555`), `0xE6E050` (`0xE6E266` before unlock `0xE6E26E`), `0xE6A480` (after lock), `0xE6CCD0` (callers `0xE6EBA0`, `0xEABA00`, `0xEABD90` all lock first) | **verified (this note, I-130-5; closes I-118-4)** |
| Vtables | fast path `0xEA2AD0`, slow `0xEA1E30` under domain lock then loader lock | verified (docs/58) |
| Class constructors | `0xEA3370`, fast exit `vtable+0x18` bit 1, `type_initialization_section` `0x11F4574` + per-class lock | verified (docs/58) |
| Interned strings | `ldstr_section` `0x11F45A0`; literals interned at transform | verified (docs/58) |
| Internal-call table | sorted static tables `0x10B4C68`/`0x10B4D28`, `icall_hash` `[0x11F436C]`, loader lock | verified (docs/58) |
| Marshal wrappers | `marshal_mutex` `0x11F43E4` | verified (docs/58) |
| Reflection hash | `[0x11F46F0]`, created after unlock | verified (docs/58) |
| Type-equal / type-object path | `0xEA8A00` -> `0xEBBDE0` -> `0xEA8720` (S3PP MTEH caches it) | verified chain; `0xEA8A00` = `mono_type_get_object` inferred (I-118-7) |
| `mono_defaults` | `0x1220E40`; filled by `mono_init_internal`-shaped code `0xE75D4C`-`0xE7686A` with one absolute store per field after a `(namespace, name)` class lookup: `+0x00` corlib .. `+0x74` `Exception`, `+0x8C` `AppDomain` .. `+0xC8` `Monitor`, then `+0xD8`.. (generic `IList`, `__ComObject`, `SafeHandle`, ...) in 1.2.3 order. **Never written:** `+0x58`, `+0x5C`, `+0x78`, `+0x7C`, `+0x80`, `+0x84`, `+0x88`, `+0xCC`, `+0xD0`, `+0xD4` (by 1.2.3 position: async result, wait handle, thread-abort exception, `Thread`, `TransparentProxy`, `RealProxy`, `MonoMethodMessage` and three remoting/security classes); no store and no address taken, so they stay NULL (zero-filled tail of `.data`, past its raw data); every one of the 26 reads of `+0x80` is a compare or a load for one | verified (§10.2; closes I-118-5); names of the unwritten fields inferred by position |
| `MonoClass` fields used | `+0x14` init bits, `+0x46`/`+0x48`/`+0x4C` interface layout, `+0x94` methods, `+0x98` `byval_arg`, `+0xB8` runtime info, `+0xBC` vtable slots | verified (docs/58, docs/104, I-119-32) |
| `mono_class_get_full` | `0xE6F840` | verified (I-119-31) |
| `mono_class_is_assignable_from` | `0xE6FD00` (calls `mono_class_init` and `Class_GetInterfaceOffset` `0xD70870`; 49% from `isinst` `0xEA3820`) | inferred name (calls verified) |
| Static-field helper | `0xD89CD0` (EA, `get value(field, out)`; siblings `0xD89BF0` set value, `0xD89C60` get address): per call `mono_domain_get` (one `TlsGetValue`), `mono_class_vtable` fast path (`class+0xB8` runtime info indexed by `domain+0x58`, no lock), init bit `vtable+0x18 & 2` (else `0xEA3370`), special-static lookup in `domain+0x88` (`g_hash_table_lookup` `0xE45F00`: two indirect calls and a `div`), address `vtable+0xC` data + `field+8` offset, then the typed copy `0xE521A0`. 166 call sites in 90 EA native bodies, 160 distinct fields, each from a global the per-class binder fills once (for example `0xD9D2B0` via `0xD89EB0`). Live cost ~0.04-0.07%, not 1.8% (§1.9 correction); cache designed, not worth building (§9.2) | verified code; special-static hash non-NULL in game inferred (G-I4 closed) |
| Dormant sections | `0x11F44EC`, `0x1220FE0`, `0x11F42D4`, `0x11F4314`: each is referenced only by its `InitializeCriticalSection` (and, for the two GC ones, `DeleteCriticalSection` in the cleanup); no other code or data reference, so they are initialised and never entered. Roles by init site and 1.2.3 order: `0x11F44EC` `monitor_mutex` (`0xE9F810`, called at the top of `0xE66240`; its cleanup sibling `0xE9F820` frees the never-filled `monitor_allocated` list `[0x11F4504]`; `Monitor` is stubbed, D19); `0x1220FE0` `mono_delegate_section` (`0xE66310`, right after `domain->setup` and before `mono_context_init` `0xEA07D0`); `0x11F42D4` `allocator_section` and `0x11F4314` `finalizer_mutex` (`mono_gc_init` `0xE78DA0`: `handle_section` `0x11F42F0`, these two, then two `GC_add_roots`; cleanup `0xE78DE0`). The stock users (fixed-allocator, finalizer thread and domain-finalize list, delegate-to-function-pointer table) are not in this build or never run | verified (never entered, §10.1); names inferred from 1.2.3 (G-B5 closed) |
| Transform at run time | 0 play samples in `0xE643B0`: warm after load | verified (measured, §1.9) |

## 4. Threading primitives

| Item | Finding | Tag |
|---|---|---|
| TLS keys | six: `[0x11F4160]` interp context, `[0x11959DC]` domain, `[0x1195B7C]` `MonoThread` (always NULL), `[0x1220F38]` loader error, `[0x11F43C4]` marshal marker, `[0x11F43E0]` last error | verified (docs/56 §1, docs/120 §3.1) |
| `__declspec(thread)` | `.tls` 8 zero bytes, unused | verified (docs/120) |
| `Monitor` | `Enter`/`Exit` bodies are null checks; `Monitor_exit/pulse/pulse_all` -> `ret`, `test_owner/wait` -> `return 1`, `test_synchronised/try_enter` -> `return 0` | verified (docs/120 §3.3) |
| C# `lock` users | 0 in game scripts; 182 in mscorlib (lazy statics) | verified (docs/120 §3.3.1) |
| `Synchronized` event accessors | 1,174 in game scripts; no exclusion (Monitor no-op) | verified |
| `Interlocked.*` | 32-bit `CompareExchange`/`Exchange`/`Increment`/`Decrement` (int, intptr, object, `T`, single) call the Win32 interlocked APIs (`0xEBA2B0`, `0xEBA1F0`, `0xEBA210`, `0xEBA2D0`, `0xE66E10`, `0xF4F560`); every 64-bit (long, double) body **and `Add(int&,int)`** is a plain read-modify-write under one global critical section `0x11F476C` (initialised in `mono_thread_init` `0xEBB180`): `0xEBA390` Add int, `0xEBA3C0` Add long, `0xEBA340`/`0xEBA300` CompareExchange long/double, `0xEBA240`/`0xEBA280` Exchange long/double, `0xEBA190`/`0xEBA1C0` Increment/Decrement long, `0xEBA400` Read long. Atomic against each other, not against plain stores or against the lock-free 32-bit ops on the same int | verified (§9.4) |
| Managed thread start | `Thread_internal` -> `return 0` | verified |
| `mono_thread_attach` | absent; no EA call into the thread API | verified (docs/30 §12.5, docs/118 D9) |
| Thread-static fields | `0xEBAB30` dereferences the NULL thread object. The script assemblies declare 3 (`System.AppDomain::type_resolve_in_progress`, `::assembly_resolve_in_progress`, `::_principal`, all `[ThreadStatic]`, no `[ContextStatic]`); no allow-listed closure reaches their 3 accessors (§9.3) | verified (docs/30 §5, §9.3) |
| Interruption checkpoint | `0xEBB480` returns on NULL thread | verified |
| `[ScriptMultithreaded]` / who runs the sim | `GC_init` ran on the sim thread; main is not registered | verified live (I-120-1) |
| EA `GeneralAllocator` (eglib `g_malloc` `0xD70A10` -> `0x4E69D0`) | takes `EnterCriticalSection` on the allocator's lock pointer `+0x4E4` when it is non-NULL (`0x4E69D4`-`0x4E6A05`); same in `0x4E5920` (free) and `0x4E6A70` (realloc). The lock pointer is always set: the constructor `0x4E5AA0` calls `Init` `0x4E5710`, which creates the critical section at `+0x4E8` (`0x4E48C0`, spin count 10) when `+0x4E4` is NULL; only option 1 of `SetOption` `0x4E48F0` clears it, and its 3 callers pass options 3 and `0x3E8`; `Shutdown` `0x4E5CB0` clears it at exit. `[0x11D4BC8]` is the allocator passed to the script host's init `0xD77600` (vtable slot `0x108B694`) by `0xD7F2B0`: a dedicated heap from config `Script`/`HeapSize` (default `0xA000`, built with `0x4E40C0` -> `0x4E5AA0`) or the game's main allocator `[0x11CB864]` | **verified (this note I-130-7; §9.4 closes G-B7)** |
| P/Invoke last error | TLS `[0x11F43E0]` | inferred (docs/56) |
| Lock order | domain -> loader; `0x11F4130` -> domain; loader -> marshal; cctor section -> per-class; GC (P3) innermost | verified sequences (docs/58 §2) |

## 5. EA modifications vs upstream Mono 1.2.3

D1-D12 are docs/118 §4.3; D13-D20 are added here.

| # | Change | Tag |
|---|---|---|
| D1 | eglib instead of GLib; allocator thunks into EA `GeneralAllocator` | verified |
| D2 | Boehm abort sink `0xD707E0` | verified |
| D3 | Boehm lock hooks folded to empty functions | verified (bytes); role inferred |
| D4 | GC hooks: heap source `0xD70900`, root hook `0xD71820`, never-stop expand, `[0x11EE528]` | verified |
| D5 | interpreter calls into EA: sleep, frame arena, context cells, interface offsets, binder, trampoline memory | verified |
| D6 | `mono_class_init` -> `0xD790A0` (interface-offset layout), inside the loader lock (I-130-5) | verified |
| D7 | image open -> EA `0xD6FCB0` | verified |
| D8 | task states, invoke with state, yield flag, frame lookup by depth `0xE534E0`, bind helper `0xE520E0` | verified |
| D9 | no thread API use; domain and context in TLS | verified |
| D10 | managed references kept in uncollectable cells, no GC handles | verified |
| D11 | store helper `0xE69820` (write-barrier shaped) | inferred (I-118-6) |
| D12 | symbol fix `0xEA1400` / `0xEA1090` | verified |
| D13 | **22 superinstructions** `0x1AB`-`0x1C0`, table `0x108FCD8`, matcher `0xD89930`, peephole `0xE5C660` | **verified (this note)** |
| D14 | **task opcodes** `sim_sleep`/`sim_chkyld`/`sim_chkyld2` `0x1A6`-`0x1A8`, named in the opcode table | verified |
| D15 | **flat frame records** (63 per C activation, finally stack per record) and the frame arena | verified layout; "not upstream" inferred |
| D16 | **`callvirt` inline cache** in data items | verified code; upstream 1.2.3 mint had none [inferred] |
| D17 | **native method bodies** (`RuntimeMethod+0x24..+0x2C`, binder `0xD9A7B0`, 1,085 methods) | verified |
| D18 | `MonoObject` without sync word | verified (I-119-10) |
| D19 | `Monitor` and `Thread_internal` stubbed | verified |
| D20 | filter clauses abort (`g_assert_not_reached`) | verified code; whether upstream mint also lacked them inferred |
| D21 | **remoting and thread classes left out of `mono_defaults`**: 10 fields never written (`TransparentProxy`, `RealProxy`, `MonoMethodMessage`, `Thread`, thread-abort exception, ...), so every proxy test in the interpreter and runtime (26 reads of `+0x80`) compares with NULL and the proxy branches are dead (§10.2) | verified |

## 6. Counts by component

Counted from the Tag columns of §1-§5 (a row with a verified part and an inferred or unknown part counts once, under its weakest tag):

| Component | Rows | Verified | Inferred | Unknown |
|---|---|---|---|---|
| Interpreter (§1.1-§1.8) | 63 | 54 | 5 | 4 |
| GC (§2) | 25 | 19 | 5 | 1 |
| Loader, metadata, class init, caches (§3) | 20 | 15 | 5 | 0 |
| Threading (§4) | 14 | 13 | 1 | 0 |
| EA modifications (§5) | 21 | 16 | 5 | 0 |

Before this note the interpreter rows of §1.2-§1.5 (dispatch, opcode table, superinstructions, frame records, exception search) were unknown or only named in docs/29 §1.2; the rows there without a doc reference rest on this note's reads. The §9 pass moved four rows: second-level jump tables (unknown -> verified), static-field helper (unknown -> inferred, one runtime fact left), `Interlocked` (unknown -> verified), `GeneralAllocator` lock (inferred -> verified). The §10 pass moved six more and added one: frame record, frame save and resume (unknown -> verified), `ThreadContext` and the `call` proxy check (inferred -> verified), dormant sections (unknown -> inferred: never entered verified, names from 1.2.3), and D21 (new, verified).

## 7. Gap register

Hours are wall-clock for Claude with agents (CLAUDE.md). A static step is ~20-60 min, an emulator test (docs/63) ~1-2 h, a monohost test (docs/119) ~1-3 h, and a live probe is a build (~30 min) plus one game job (8-15 min). Game jobs are listed separately. While Mono game runs are parked (memory: Mono static only) the live items wait for Rodney's go.

### 7.1 (a) Script stutter: interpreter fast paths

| # | Rank | Unknown | Why it matters | Cheapest closing job | Hours | Game jobs |
|---|---|---|---|---|---|---|
| G-I1 | 1 | Which managed methods and call sites carry the ~43% call/return share (callee histogram, top callers per callee) | picks the targets for getter inlining, new superinstructions or option-b native replacements; today we know the opcode, not the method | live probe: sampler reads `[0x11F4170]` -> record array -> `MonoInvocation+4` -> `RuntimeMethod+8` -> `MonoMethod` per sample (chain verified here); name with `0xE72010` offline | 1-2 h | 1 (BP, 10 min) |
| G-I2 | 2 | Gain of a transform-time inliner for trivial callees (`ldthis+ldfld+ret` getters, constant returns) | call overhead is ~43% of interpreter self (~110 ms/s of ~25% sim-thread share [inferred]); each inlined call skips push, arena and return | monohost test: transform the G-I1 top methods in the host, count inlinable call sites, then an emulated A/B of one method | 3-6 h after G-I1 | 1 A/B night |
| G-I3 | 3 | Which IC is behind the loader-lock callback `[0x11EE6CC]`, and contention vs call rate | docs/29 §5.1 said ~14 ms/s of `0xE85020`/`0xE85030` time; live chain ~0.9% (~9 ms/s: lock 0.14%, unlock 0.73%; callback path `0xDA7FB0` 0.72%, overlapping, §10.6); smaller than thought, rank unchanged | live probe: `[IcLockCheck]` is built (counters v15); one BP job | 0.5 h | 1 |
| G-I4 | - | Whether `0xD89CD0` (static-field address, hash lookup + vtable + class-init check per call) can be cached per field | was 1.8% inclusive; live chain only ~0.04-0.07% (stale return addresses, §1.9 correction) | **closed 2026-10-20 (§9.2): cacheable per field (rules + emu test plan written), gain ceiling < 1 ms/s, do not build** | - | 0 |
| G-I5 | 4 | Receiver-load stall in `callvirt` (`0xE54AEC`, 7.7% of interpreter self): which objects are cold | if a few hot types dominate, prefetching or a managed-side change (cache the field) helps; not fixable in the dispatch | needs G-I1 (call site -> receiver type); then static | 1 h after G-I1 | 0 |
| G-I6 | 5 | Pair frequencies of base opcodes in hot methods (candidates for new superinstructions) | the 22 EA superops take only 3.2%; new ones need new handlers and a jump-table extension (`cmp 0x1C0` at `0xE544DB`) | monohost: dump mint code of the G-I1 methods (dumper `0xE5A7C0` or own walker with the oplen table) and count n-grams weighted by samples | 2-3 h after G-I1 | 0 |
| G-I7 | 7 | MTEH (S3PP) effect on `0xEA8720`/`0xEBBDE0` without the route-worker confound | docs/29 §5 put it at ~20-30 ms/s; on the live chain the whole type-object path is 0.62% inclusive (`0xEBBDE0`, incl. `0xEA8720`; `0xEA8A00` 0.36%), so the ceiling is **~6 ms/s** on these samples (§10.6); moved below G-I10 | live A/B, S3PP ini on/off | 0.3 h | 2 (one load each) |
| G-I8 | - | Roles of the second-level jump tables `0xE59F84`, `0xE59FB0` | completes the handler map; low value | **closed 2026-10-20: `ldelem.*` / `stelem.*` element-type switches (§1.2, §9.4)** | - | 0 |
| G-I9 | - | Yield frame save `0xE53620` and resume `0xE53F18`: cell layout and cost per yield | was 1.4% inclusive; live chain 0.04% (save incl. cell alloc), resume block 0.38% self including ordinary entries | **closed 2026-10-20 (§10.5): cell layout and resume read; cost ~0.4 ms/s, reusing cells is not worth building** | - | 0 |
| G-I10 | 6 | Dispatch rewrite (threaded code) gain | dispatch is 6.3% of interpreter self (~1.6% of all samples): ~16 ms/s ceiling [inferred] | not before G-I1/G-I2; emu harness can time a handler loop | 4-8 h | 1 A/B |

### 7.2 (b) Managed code on a second thread (M5/M6)

| # | Rank | Unknown | Why it matters | Cheapest closing job | Hours | Game jobs |
|---|---|---|---|---|---|---|
| G-B1 | 1 | P4 `FrameTls` in game: `TlsAlloc` returned 102/103, outside the 64 inline TEB slots (PLAN 2026-10-20) | without P4 the worker must not interpret in game: blocks Mode R jobs | build: rebuilt blocks read `fs:[0xF94]` (TLS expansion array) + `(idx-64)*4`, or take the two keys in `DllMain` before the game; selftest with forced high indexes | **built 2026-10-20 (`2f4c595`, merged `a92111c`): `FrameTls=1` reserves the pair in `ApplyMonoThread` at DLL load; selftest ok** | 1 smoke left |
| G-B2 | 2 | How often game tasks end with frames on their arena chain (`arenaRelNonEmpty`, I-120-22) | decides whether `ArenaFix` must also ship single-threaded (pool duplicates are latent corruption) | live: counter exists (`ts3native_mtprobe` v2); any BP job with `[MonoThread] GcLock=1` | 0 h build | 1 |
| G-B3 | 3 | F1 (`N2mPrivate=1`, worker-private call records at `0xD8A8E0`) in game | 59 of 78 binder bodies in the closure reach `0xD8A8E0` (docs/122 §12.1) | live Verify job with the worker idle, then Mode 1 | 0 h build | 1-2 |
| G-B4 | - | `[ThreadStatic]` fields in allow-listed code (`0xEBAB30` faults on the NULL thread object) | a worker hit is a crash, not an abort | **closed 2026-10-20 (§9.3): 3 fields, all `System.AppDomain`; 0 reached by allow-list v0 or any docs/126 tier-2 group, even without cuts, with delegates, at depth 40; no fix needed** | - | 0 |
| G-B5 | - | Roles of the dormant sections `0x11F44EC`, `0x1220FE0`, `0x11F42D4`, `0x11F4314` | code that a stock build would guard and this build does not | **closed 2026-10-20 (§10.1): `monitor_mutex`, `mono_delegate_section`, `allocator_section`, `finalizer_mutex` by init site; none is entered anywhere in this build; their stock users are monitor records (stubbed `Monitor`), the delegate-to-function-pointer table, the fixed GC allocator and the finalizer thread, none on an allow-listed path [inferred from 1.2.3]** | - | 0 |
| G-B6 | - | Host code `0xD83E22`-`0xD848D6` that calls the arena functions (no Ghidra function) | a sim-thread arena user the P3 lock must cover; confirm none can run during a worker job | **closed 2026-10-20 (§9.4): the bodies of ICs `TaskControl_GetTaskContext` `0xD83DD0` and `TaskControl_SetTaskContext` `0xD84360`; reached only from task save/restore and `GetObjectStackTrace`, never from an allow-listed closure; sim thread only** | - | 0 |
| G-B7 | - | Lock pointer `+0x4E4` of the Mono `GeneralAllocator` `[0x11D4BC8]` is set | if NULL, eglib `g_malloc` from a worker races the sim thread (transform, hash growth) | **closed 2026-10-20 (§4 row, §9.4): always set by `Init`; never cleared before shutdown** | - | 0 |
| G-B8 | 4 | How the game roots collectable task states (I-119-41) and what EA does after a task ends by exception with `[0x11F4170]` dead (I-119-43) | matters once P4 makes these per thread | static RE of `ScriptHost_RunTask` `0xD7F000` and the task list | 1 h | 0 |
| G-B9 | 5 | Residual indirect allocator path from the route planner and other native workers (docs/29 §4.4) | a foreign allocation corrupts free lists with or without M5 | live: `gcForeignAllocs` design (docs/29 §4.5), slow-path detour | 1 h | 1 |
| G-B10 | 6 | Warm-up completeness for `mscorlib` lazy statics guarded by no-op `lock` | first use on two threads at once races | monohost Verify: run the worker closure cold and count slow-path entries (`0xEA1E30`, `0xEA3370` past the fast exit, `0xE51400`) | 1-2 h | 0 |

Other open items, lower value: transparent-proxy identity, `ThreadContext+0x14/+0x15` and frame record `+0x18`-`+0x24` are closed (§10.2-§10.4); `MonoClass+0x98` vs `+0xA0` as `byval_arg` (§10.2 note; static, 0.3 h); filter clauses in mods (scan mod DLLs for clause kind 1 when a user reports an abort at `interp.c` line `0x1C13`).

## 8. Static closes made for this note

All in docs/51 §175.

1. Opcode table, names, operand lengths and handler addresses: `docs/data/mint-opcodes.csv` (I-130-1).
2. EA superinstructions: table, matcher, peephole pass, sequences (I-130-2).
3. Frame record array, depth limit, `MonoInvocation`, task-state, `ThreadContext` and `RuntimeMethod` offsets; frame -> method chain for a sampler (I-130-3).
4. Exception search: two passes, clause layout, filter clauses abort (I-130-4).
5. `0xD790A0` always inside the loader lock (I-130-5, closes I-118-4).
6. `GC_no_dls` set unconditionally: image sections are not roots in the game either (I-130-6, raises I-119-37 for the game to verified static).
7. EA `GeneralAllocator` locks per call when configured (I-130-7).
8. Opcode-level split of interpreter self time from existing samples (I-130-8, measured, attribution approximate).
9. `callvirt` monomorphic inline cache; its hot spot is the receiver load (I-130-9).

## 9. Second static pass (2026-10-20, ledger docs/51 §179)

Static only: capstone over `game-copy/bin/TS3W.exe`, the call index of `tools/managed-callgraph.ts`, the metadata of the 10 script assemblies, and the existing samples. No build, no game run.

### 9.1 The 22 EA superinstructions against their base sequences (I-179-1)

Each handler was read next to the handlers of its base opcodes. Rules that hold for all 22 [verified]:

- **Encoding.** The peephole pass `0xE5C660` overwrites only the first opcode word of a matched window (`0xE5C8F0`); the operands and the opcode words of the 2nd..nth instruction stay in place. A superop handler reads each operand at its original offset and advances `ip` by the summed length of the base instructions, so a jump into the middle of a fused sequence still runs the base opcodes.
- **Block boundaries.** Pass 1 marks every branch target (branch operand kinds 7 and 8, switch kind 9) in a byte map; pass 2 flushes the window at a marked position, so no window spans a branch target.
- **Branches.** Every fused branch adds its offset to the address of the branch instruction itself, as the base handler does; a not-taken branch continues with the next base instruction's work.
- **Faults.** Each null, bounds or divide check jumps to a stub that sets `MonoInvocation+0x1C` to the offset of the base instruction that would have faulted (stubs `0xE595AB` +2, `0xE595CA` +4, `0xE595E9` +6, `0xE59608` +8, `0xE59646` +0xA, `0xE59665` +0xE, `0xE59684` +0x10 for `NullReferenceException` `0xE770E0`; `0xE59627` +8 and `0xE596A3` +0x10 for the index exception `0xE772A0`, the builder base `ldelem` uses; `0xE593F8` for `DivideByZeroException` `0xE76F00` at `rem.un`), so catch-clause ranges and stack traces see the same `ip`. No superop skips a check its base sequence performs; the only checks it leaves out are on `this` after it was already checked in the same handler, which base `ldthis` never checks either.
- **isinst.** `0x1B9` and `0x1C0` inline the four paths of base `isinst` `0xE56502` (proxy `0xEA3220`, interface `0xD70870` with the max-interface-id test, array/rank `0xEA3820`, supertype table at `class+0x3C` by depth `+0x40`) with the same register roles.

Differences with no semantic effect [verified]: (a) dead eval-stack slots above the final top are not written (base handlers leave the pushed-then-popped values there), which only changes what the conservative GC scan sees in dead slots; (b) some field or local loads are hoisted before a later check (only of locals or of an object already proven non-NULL, so no new fault); (c) `0x1C0` keeps one value in the handler scratch `[ebp-0x1C]`, which 30+ base handlers (conversions, `ldsfld`, `callvirt`) also use as scratch with no state carried between instructions.

| Op | Base sequence | Bytes | Faults (at base instruction) | Verdict |
|---|---|---|---|---|
| `0x1AB` | ldthis, ldfld.o | 6 | NRE at ldfld | equal |
| `0x1AC` | brtrue.i4.s, ldc.i4.0 | 6 | - | equal |
| `0x1AD` | ldthis, ldfld.i4 | 6 | NRE at ldfld | equal |
| `0x1AE` | brfalse.i4.s, ldloc.i4 | 8 | - | equal |
| `0x1AF` | ldfld.o, ldfld.o | 8 | NRE at either ldfld | equal |
| `0x1B0` | brfalse.i4.s, ldloc.o, ldfld.o | 12 | NRE at ldfld | equal |
| `0x1B1` | ldarga, ldarg.o | 8 | - | equal |
| `0x1B2` | ldloc.i4, add1.i4, stloc.i4 | 10 | - | equal |
| `0x1B3` | ldthis, ldfld.o, ldfld.o | 10 | NRE at either ldfld | equal |
| `0x1B4` | ldloc.o, ldthis, ldfld.o | 10 | NRE at ldfld | equal |
| `0x1B5` | ldloc.o, brtrue.i4.s, ldc.i4.0 | 10 | - | equal |
| `0x1B6` | ldthis, ldfld.o, brtrue.i4.s, ldnull | 10 | NRE at ldfld | equal (tail shared with `0x1B5`) |
| `0x1B7` | stloc.np.o, brfalse.i4.s, ldloc.o, ldfld.o | 16 | NRE at ldfld | equal (branch tail shared with `0x1B5`) |
| `0x1B8` | ldloc.i4, ldloc.i4, blt.i4.s, ldc.i4.m1 | 14 | - | equal (signed compare as base) |
| `0x1B9` | ldthis, isinst, stloc.np.o, brfalse.i4.s, ldloc.o | 18 | - | equal |
| `0x1BA` | stinarg.o x2, ldarg.o x2, ceq.i4 | 18 | - | equal |
| `0x1BB` | ldloc.o, ldarg.o, ldarg.o, ldloc.i4, ldelem.ref | 18 | NRE / index at ldelem | equal (unsigned bounds compare as base) |
| `0x1BC` | ldloc.o, ldfld.o, stloc.o, ldloc.i4, add1.i4, stloc.i4 | 22 | NRE at ldfld | equal |
| `0x1BD` | stinarg.o x2, ldarg.o, brtrue.i4.s, ldarg.o, ldnull, ceq.i4 | 24 | - | equal |
| `0x1BE` | ldthis, ldfld.o, brtrue.i4.s, ldthis, ldfld.i4, ldthis, ldfld.o, ldfld.o, ldlen, blt.i4.s | 32 | NRE at ldfld (this), the 2nd object ldfld, ldlen | equal |
| `0x1BF` | ldc.i4, and.i4, ldloc.i4, rem.un.i4, stloc.i4, ldthis, ldfld.o, ldloc.i4, ldelem.ref, stloc.np.o, brfalse.i4.s, ldnull, stloc.o | 44 | divide at rem.un; NRE at ldfld; NRE / index at ldelem | equal (a hash-bucket lookup shape) |
| `0x1C0` | stinarg.o, ldarg.o, isinst, stloc.np.o, brfalse.i4.s, ldthis, ldfld.o, ldloc.o, ldfld.o, bne.un.i4.s, ldthis, ldfld.o, ldloc.o, ldfld.o, ceq.i4 | 54 | NRE at the first ldfld on this and at both ldfld on the local | equal (an `Equals` override shape) |

No row differs, so `docs/data/mint-opcodes.csv` needs no change: its `superop_sequence` column is confirmed for all 22.

### 9.2 Static-field helper `0xD89CD0` and a per-field cache (G-I4, I-179-2 .. I-179-5)

**What one call does** [verified, §3 row]: `mono_domain_get` (`TlsGetValue([0x11959DC])`); `mono_class_vtable` `0xEA2AD0` fast path (`class+0xB8` runtime info: `u16` max domain id, then one vtable per domain id `domain+0x58`; miss -> `0xEA1E30` under the domain and loader locks, once per class); class-init test `vtable+0x18 & 2`, else `mono_runtime_class_init` `0xEA3370` (cctor lock, may run the cctor); if `domain+0x88` (the special-static hash) is non-NULL, `g_hash_table_lookup` `0xE45F00` -> `0xE459F0` (hash callback, `div` by the bucket count, equal callback per chain entry) and on a hit `0xEBAB30` (thread or context static address); else address = `vtable+0xC` (static data) + `field+8`; then the typed copy `0xE521A0` (switch on `type+6`, a 4- or 8-byte or `float`->`double` move). No lock on the hit path.

**Who calls it** [verified]: 166 call sites in 90 EA native bodies (`0xD9D2D0`-`0xE3A530`), 160 distinct `MonoClassField*` globals in `0x11EE650`-`0x11F2D60`, each written once by a per-class binder (example: `0xD9D2B0` stores the result of the field lookup `0xD89EB0` into `[0x11EEB38]`). Set-value `0xD89BF0`: 17 sites, 15 fields; get-address `0xD89C60`: 3 sites, 2 fields.

**Special statics:** `0xEA1E30` registers a static field in `domain+0x88` (created on first use with `g_hash_table_new(NULL, NULL)` at `0xEA2088`) when `0xEA0C90` classifies it as thread or context static, and allocates its slot with `0xEBAA80` [verified]. The only such fields are the 3 `AppDomain` ones (§9.3), so the hash exists as soon as the `AppDomain` vtable exists, which Mono creates at runtime init with the domain object [inferred: 1.2.3 `mono_runtime_init`; a live read of `[domain+0x88]` confirms]. Every helper call therefore pays one hash lookup that always misses.

**Cost** [measured, existing samples]: 1,371 samples (1.84%) list an address inside the helper in their first 12 frames, but 1,341 of them are the return address after the last call (`0xD89D35`) at depth 1-11 above an interpreter leaf, which a call to the leaf copy `0xE521A0` cannot produce: stale stack slots. Samples consistent with a live chain (leaf in the helper, or frame 1 = a helper return address and frame 0 inside the matching callee): 26 = **0.035%**; frames 0-1 alone: 0.07%. With 1% ~ 10 ms/s [inferred], the helper costs ~0.4-0.7 ms/s in total.

**Cache design** (not built, and not worth building at that ceiling):

- Detour at the helper entry (and the two siblings, same prologue: three pushes and a `call rel32` to relocate). Table of 512 entries, open addressing on `field >> 2`; entry = {field, domain, address, type-kind}.
- Hit: read the domain (keep the `TlsGetValue`: one call, and it keeps the key exact), match {field, domain}, copy through `0xE521A0` with the cached address. Skips the vtable lookup, the init test and the hash lookup.
- Fill only after the original path returns, and only if: the vtable init bit is set at that point (a class whose cctor is still running, on this or another thread, is never cached, so cctor ordering and the cctor lock keep their stock behaviour); the special-static lookup missed (thread/context statics are per thread and never cached); the caller is the sim thread (until the M5 worker rules, docs/30, allow worker fills). Publish order: address, then domain, then field, as aligned 4-byte stores (x86 stores are not reordered with each other), so a reader on another thread sees a whole entry or a miss.
- Invalidation: static data (`vtable+0xC`) is allocated once per vtable and vtables live as long as the domain; this build never unloads classes or creates a second domain (docs/118 D9) [inferred]. Flush the table in the shutdown path `0xD80750`, and key by domain so a second domain could never hit an entry of the first.
- Not cached: get-address `0xD89C60` callers that keep the pointer (they already do their own caching).

**Emulator test plan (docs/63, spec not written):** map a fake domain (`+0x58` id, `+0x88` NULL or a hash built by the real `0xE45E50`/`0xE458E0`), class (`+0xB8` runtime info), vtable (`+0xC` data, `+0x18` flags), field (`+0` type, `+8` offset, `+0x14` class); stub `TlsGetValue` (returns the domain), `0xEA3370` (sets the init bit, counts calls) and `0xEBAB30` (returns a per-"thread" address). Run the stock helper and a Python model of the cached helper on generated cases and compare the out bytes and the stub call counts: (1) initialised class, repeated calls: same bytes, model hits from the 2nd call; (2) uninitialised class: one init call, no fill until the bit is set; (3) init bit set during the call: fill on the next call only; (4) special-static hit: never filled, `0xEBAB30` taken every call; (5) two domains alternating: no cross hits; (6) every copy kind of `0xE521A0` (i1, u1, i2, u2, i4, i8, r4->r8, r8, reference, value type): identical bytes; (7) flush: next call misses. Coverage target: every block of `0xD89CD0` and the copy cases used. A game A/B only if someone builds it.

### 9.3 `[ThreadStatic]` and thread-static-like state in the allow-listed code (G-B4, I-179-6)

- **Fields** [verified]: a scan of the `CustomAttribute` table of all 10 script assemblies (`ScriptCore`, `SimIFace`, `Sims3GameplayObjects`, `Sims3GameplaySystems`, `Sims3Metadata`, `Sims3StoreObjects`, `System.Xml`, `System`, `UI`, `mscorlib`) finds 3 fields with `System.ThreadStaticAttribute` and none with `ContextStaticAttribute`: `System.AppDomain::type_resolve_in_progress`, `::assembly_resolve_in_progress`, `::_principal` (all `static`). Their IL accessors: `AppDomain::get_DefaultPrincipal`, `AppDomain::SetThreadPrincipal`, `AppDomain::DoAssemblyResolve` (the last one is called by the runtime on a failed assembly load); none has a managed caller in the index.
- **Reach** [verified, tool output]: BFS over the call index from allow-list v0 (12 definitions, the 7 implementors' `HowMuchItWantsSimToCome`, the 5 pilot base paths) and from each docs/126 tier-2 group (meta-rest, base-local, local-sampled, scoring-function, row5-candidate): 0 accessors reached with the pilot cuts at depth 12, and 0 without cuts, with delegate edges, at depth 40 (closures of 12,114-13,023 methods).
- **Thread-static-like state** [verified]: this `mscorlib` has no `System.Threading.Thread` members at all, and `CultureInfo::get_CurrentCulture` only calls `get_InvariantCulture` (a static), so the `String.Compare`/`IndexOf` paths that the closures do reach (via `Tutorialette::AreTutorialTipsEnabled`, `InteractionDefinitionUtilities::GetInteractionLocalizationKey`) read no per-thread state. The remaining per-thread state is native TLS (domain, interpreter context, loader error, marshal marker, last error; §4), already in docs/30 §5 and docs/56.
- **Fix:** none needed. If a later allow-list reaches one of the 3 accessors, the fix is an abort, not a warm-up (the value is per thread) and not a registered thread object (`mono_thread_attach` is absent, and a fake `MonoThread` would need the special-static slot arrays at `+0x40`). The sim thread has no thread object either, so the game itself crashes on these paths; they are dead in play.

### 9.4 Small closes (I-179-7 .. I-179-10)

- **G-I8:** §1.2 row. `0xE59F84` targets: `ldelem.i` and `.i4`, `.i1`, `.u1`, `.i2`, `.u2`, `.u4`/`.ref` (shared), `.i8`, `.r4`, `.r8`; `0xE59FB0`: `stelem.i` .. `stelem.ref` (`0xE5750D`-`0xE5759A`).
- **G-B6:** the code without a Ghidra function is two IC bodies, registered through the provider table at `0x108C608` ({body, name} pairs; registration at `0xD84A56`): `ScriptCore.TaskControl::TaskControl_GetTaskContext` `0xD83DD0`-`0xD83F14` (arena call `0xD75E80` at `0xD83E22`) and `TaskControl_SetTaskContext` `0xD84360`-`0xD8495D` (arena calls `0xD71B70`, `0xD714B0`, `0xD71900`, `0xD714C0`, `0xD71800`, `0xD75E80`; switch table `0xD84A28`). Managed callers: `TaskControl::GetTaskContext` <- `TaskStateCollection::WriteTask`, `Simulator::GetObjectStackTrace`, `UTFConnectorSimulator::GetObjectStackTrace`; `TaskControl::SetTaskContext` <- `SavedTaskContext::Restore`. Not reached from any allow-listed closure (same BFS as §9.3, depth 40, no cuts). Both run on the sim thread from managed code. A worker job only runs in the quiescent window between `MonoScriptHost_Simulate` return and the next entry, where the sim thread runs no managed code (docs/122 §2 steps 3-5; a host invoke into managed code in the window trips the stop flag, A5), and saves and loads stop the worker first (docs/59 worker rules). So these arena users cannot overlap a worker job and need no extra P3 site [inferred from the docs/122 design].
- **G-B7:** §4 row.
- **`Interlocked` 64-bit:** §4 row. For M5: a worker and the sim thread mixing `Interlocked.Add(int)` with `Interlocked.Increment(int)` on one field would not be atomic; no allow-listed closure uses `Interlocked` on shared fields today [not checked; add to the docs/122 write-set scan if one appears].

## 10. Third static pass (2026-10-20, ledger docs/51 §184)

Static only: capstone over `game-copy/bin/TS3W.exe` with the Ghidra bounds, import names from the PE import table, and the existing `probe-out/profile/*simstack*.jsonl` samples. No build, no game run.

### 10.1 Dormant critical sections (G-B5, I-184-1)

All four are referenced only by their `InitializeCriticalSection` (the two GC ones also by `DeleteCriticalSection` in `0xE78DE0`); a byte scan of the whole file finds no other occurrence of the four addresses [verified]. Init sites and roles:

| Section | Init site | Role (1.2.3 name) | Why nothing enters it |
|---|---|---|---|
| `0x11F44EC` | `0xE9F810`, third call of `0xE66240` (after `GC_init` `0xE4E4C0` and `mono_gc_base_init` `0xE69570`) | `monitor_mutex` (monitor.c); the next global `[0x11F4504]` is the record list that the uncalled cleanup `0xE9F820` frees | `Monitor` icalls are stubs (D19), so no monitor record is ever allocated [verified stubs; name inferred] |
| `0x1220FE0` | `0xE66310`, after `AppDomainSetup`/`AppDomain` objects are created and stored, before `mono_context_init` `0xEA07D0` | `mono_delegate_section` (appdomain.c) | its stock users (delegate to function-pointer marshalling) are absent or use another lock [inferred] |
| `0x11F42D4` | `mono_gc_init` `0xE78DA0` (called at `0xE6639D`), between `handle_section` `0x11F42F0` and `0x11F4314`, followed by two `GC_add_roots` | `allocator_section` (gc.c) | no fixed-allocator user in the build [inferred] |
| `0x11F4314` | same | `finalizer_mutex` (gc.c) | no finalizer thread; finalizers run on demand on the sim thread (§2) [verified no thread; name inferred] |

`0xE66240` is therefore `mono_runtime_init`-shaped: `GC_init`, `mono_gc_base_init`, monitor init, assembly hooks (`0xE67380`-`0xE67140`), `mono_thread_init` `0xEBB180`, `AppDomain` setup, delegate section, context init, the preallocated `OutOfMemory`/`NullReference`/`StackOverflow` exceptions (`domain+0x2C/+0x30/+0x34`), `mono_gc_init`; it is called from `mono_interp_init` `0xE5A680` (`0xE5A7A6`) [verified calls; names inferred].

### 10.2 `mono_defaults` and the transparent-proxy check (I-184-2, I-184-3)

`mono_defaults` is filled at `0xE75D4C`-`0xE7686A` by one absolute store per field after a `(namespace, name)` lookup, so every written field is named by its strings [verified]. Ten fields get no store anywhere in the binary and their addresses are never taken: `+0x58`, `+0x5C`, `+0x78`, `+0x7C`, `+0x80`, `+0x84`, `+0x88`, `+0xCC`, `+0xD0`, `+0xD4` [verified]. Their neighbours fix the positions: `+0x74` `Exception` and `+0x8C` `AppDomain` are written, and 1.2.3 puts thread-abort exception, `Thread`, `TransparentProxy`, `RealProxy`, `MonoMethodMessage` between them, so `+0x80` is `transparent_proxy_class` [inferred by position, matches the docs/130 §1.4 guess]. An icall body at `0xE7A870` (function-table entry `0x10B5C54`) has the 1.2.3 `Object.GetType` shape: if `obj->vtable->klass == [+0x80]` it reads `obj+8` -> `+8` (transparent proxy `remote_class` -> `proxy_class`), else it uses the object's class; that is the TransparentProxy layout [verified shape].

Consequence [verified]: `[0x1220EC0]` is NULL for the whole run, the 26 reads compare an object's class with NULL, and every proxy branch is dead: in the interpreter `calli` `0xE54848`, `call` `0xE549BE` and the remote-field opcodes `ldrmfld`, `ldrmfld.vt`, `vtldrmfld`, `strmfld`, `strmfld.vt` (`0xE567F9`-`0xE56CCB`); outside it the `callvirt` miss lookup `0xE52096`, the `isinst` proxy path `0xEA3220` (2 reads) and 16 other runtime sites (7 of them in code Ghidra has no function for, such as `0xE7A870`). A new fast path (G-I2, G-I6) may drop the check. Note: the same body passes `klass+0xA0` where 1.2.3 passes `&klass->byval_arg`; §3 lists `+0x98` as `byval_arg`. Both `MonoType`s carry the same class pointer, so earlier reads stay valid, but which offset is `byval_arg` is open (small).

### 10.3 `ThreadContext` (I-184-4)

The layout is upstream mint's [verified]: `interp_mono_invoke_with_state` `0xE59FD0` fills `+0x00` (`mono_domain_get`), `+0x08` current frame, `+0x0C` env frame (`state+0x30`, the outer `MonoInvocation` template), `+0x10` `jmp_buf` (`ebp-0x60`), sets `+0x15 = 1` before `0xE53EB0` and 0 after, and restores `+0x08..+0x10` after a `longjmp`. The native raise `0xE53740` writes the exception into `env_frame+0x20`, sets `+0x14 = 1` and `longjmp`s; when the frame that took the exception returns with it pending (`0xE592E3`), `+0x14` set means "search for a handler" (cleared, then `0xE540AB`), clear means "only run finally blocks" (`0xE54198`). `+0x15` is written only (no read in `0xE53EB0`, `0xE59FD0`, `0xE52B60`/`0xE52B90`, `0xE53740`, `0xE53910`), so it is a vestigial "in managed code" flag that new handlers need not maintain [verified no read in these; inferred for the rest of the binary]. `+0x04` is not written by these paths.

### 10.4 Frame record `+0x14`-`+0x24` (I-184-5)

The four entries into the common push `0xE56287` are `calli` (`0xE54885`), `call`/`con.call` (`0xE549E6`) and the `callvirt` family (`0xE54B4B`), each after storing 0 into `[ebp-0x28]`, and `newobj`, which falls through after storing 1 (`0xE56280`) [verified]. The push copies that value to the callee record's `+0x14`. `newobj` itself writes its own record's `+0x18` (class) and `+0x20`/`+0x24` (the new object, or a zeroed 8-byte value-type buffer passed as `this`), and a non-zero `+0x14` on return sends control to `0xE561B0`, which pushes `+0x20` (and `+0x24` for value types and strings) onto the caller's eval stack [verified]. No other code in `0xE53EB0`, the save or the resume touches `+0x18`-`+0x24`.

### 10.5 Yield save and resume (G-I9, I-184-6, I-184-7)

Cell layout and resume: §1.6 rows [verified]. Two links that explain the may-yield rules: the save does not keep record `+0x14`-`+0x24` (an open `newobj`) or the finally stack, and the resume zeroes them, so the may-yield test `0xE53550` refuses a yield while any frame is a `.ctor`/`.cctor` or has a finally block running (`+0x128 != 0`) [verified checks; the reason is inferred]. Cost: per yield, depth x (`GC_malloc_uncollectable(0x2C)` + `memset` + later `GC_free`); live-chain share of the save 0.04% (~0.4 ms/s), cell alloc `0xD714B0` 0.00%, so G-I9 is closed without a build.

### 10.6 Live-chain re-check of the §1.9 inclusive shares (I-184-8, I-184-9)

The sampler (`tools/probe/agent.js`) scans the thread's stack for any word that points into a loaded module, so the frame list mixes live return addresses with stale ones and with pointers to data. Filter used here, per sample, over all scanned entries:

1. Frame 0 is the PC and starts the chain; its function is the "current" one (Ghidra bounds, end inclusive).
2. A later TS3W `.text` entry `R` joins the chain only if the instruction ending at `R` is a `call` and either: a direct call whose target (through one `jmp` thunk) is the current function's start; or `call [IAT]` while the current frame is in a system DLL; or an indirect call through a register or memory while the current function is address-taken (its start appears as a dword anywhere in the image or as an immediate; 9,651 of 40,263 functions). Otherwise `R` is skipped as stale.
3. System-DLL entries are kept only while the chain is still inside system code.

Result: the §1.9 table under "Live-chain shares". Calibration: the filter gives 0.04% for `0xD89CD0`, matching §9.2's hand check (0.035%). Shares that change a priority: G-I9 (1.30% -> 0.04%, closed), G-I7 (docs/29's 20-30 ms/s -> ~6 ms/s live, now below G-I10), G-I3 (14 -> ~9 ms/s, rank kept). The IC path (`0xE52630` 1.71% live incl. IC bodies) and the EA native bodies (0.72%) are much smaller than their first-12 shares, which caps any "cheaper IC transition" idea at ~17 ms/s total including the IC work itself; no gap row depended on the old values.

### 10.7 Second-level jump tables (re-check)

The interpreter body `0xE53EB0`-`0xE5987E` has exactly three table jumps: the dispatch `0xE544E7` (`0xE59880`) and the two element switches `0xE57400` (`0xE59F84`) and `0xE57506` (`0xE59FB0`) already closed as G-I8 (§9.4) [verified]. No other second-level table exists in the interpreter.

## 11. Frame budget of ts3fixes hooks (issue #92, ledger docs/51 §386)

Real play reaches the 63-frame limit of §1 (issue #92: three incidents, stacks 62-64 frames deep from `ScriptProxy.Simulate`, ErrorTrap's handler needing ~11 more). Each interpreted call is one frame record (§1; mint 1.2.3 has no call inlining [inferred, docs/29]), so every ts3fixes hook adds frames on every path through it. A ts3fixes hook copies its own method header over the target (`Fixes.SwapMethod`) and reaches the original through a stub that received the original's header, so a hook costs at least one frame: the hook body (shown under the target's name) plus the stub (the original body). Counts below are frames held **while the original body runs** (what deeper callbacks see), minus the one frame the original costs without us; static count from the source (2026-10-29) [verified: source].

| Target (hook) | On which reported path | Added before | Added now | How |
|---|---|---|---|---|
| `ScriptCore.Route.Plan` (`PlanHook`) | every Sim route plan (#89 path 2 family) | +7 (+8 with `failrepeat`) | +4 (+5) | `UfPlanBody` returns "not ours" and `UfPlan` runs the plan itself (`UfPlanBody` and `UfInner` off the stack); `PlanAsync` inlined into its only caller `PlanBackoffBody` |
| same, plan marked by `unseenfbplan` (unseen Sim) | | +7 (+8) | +6 (+7) | `PlanAsync` inlined; the mark's try/finally keeps `UfPlanBody` + `UfInner` |
| `Route.Replan` / `PlanFromPoint` / `ReplanFromPoint` (`*Hook`) | #89 path 2 (`DoRoute` -> `ReplanFromPoint`) | +5 | +3 (marked: +5) | as `Plan` |
| `GoHome.Run` (`GoHomeHook`) | #89 path 2 | +1 | +1 | floor (stub) |
| `RabbitHole.RouteNearEntranceAndEnterRabbitHole` (`RabbitHoleHook`) | #92 incident 1 | +1 | +1 | floor |
| `Sim.IsAllowedInRoom(ulong, int)` (`AutRoomHook`) | #92 incident 2 | +1 | +1 | floor; installed whenever `ApplyAutShadow` finds its members, also with `autshadow` off (open: install only when on at load, costs the runtime `--ab` flip) |
| `FollowSubPathRouteAction.PerformAction` (`unseenfbfinish` hook) | any route follow (#92 incident 1 if the spin ran inside it [inferred]) | +1 | +1 | floor |
| `Sim.set_Posture` (`PosturePopHook`, new, issue #89) | every posture change | - | +1 | floor; the hook calls nothing but the stub on the common path |
| `UfHeld` (inside `UfWhy`, transient) | #89 path 2 | +1 + chain depth (`Posture.Satisfies` recursion; unbounded on a posture loop = the overflow) | +2, bounded at 16 postures | walks `PreviousPosture` itself with `Satisfaction` (same answer, `Satisfies` is not virtual) |

Helpers a hook calls **before or after** the original (`SafeSpot`, `RouteSrcBegin`, `DoorMirror.SyncRoute`, `UfWhy`) return before it runs and add nothing to the depth the original's callbacks see; they only need their own depth at the call. Saved on the reported stacks: 2 frames on #89 path 2 (`ReplanFromPoint`), 3 on any plan from `Route.Plan`; incidents 1 and 2 of #92 had only floor-cost hooks of ours. Not ours to cut: EA's forwarding overloads (`SwitchToOutfitWithSpin` 6 frames, `RouteToSlot` 5; the reporter IL-patches those) and the error handler margin (ErrorTrap ~11 frames). Raising the limit itself is native work (`state+0x140` check at `0xE59414` against the 63-record array on the C stack, §1): not studied.

2026-11-24 incident (chain-m1124b, lc7-travel-m1124b bp-lc7-save-reload, ScriptError D0CC3B59, NPC, release-default hooks): 64 frames from `ScriptProxy.Simulate` (Eat Out -> `RouteNearEntranceAndEnterRabbitHole` -> `RouteOutside` -> `DoRoute` -> portal -> `SwitchToOutfitWithSpin` -> `LaundryManager.OnClothesChanged` one-shot -> `SetLogic` error -> ErrorTrap), 8 of them ts3fixes hooks installed whatever the switches: `RheEnterHook` + `RabbitHoleHook` (two layers on one method), `RabbitHoleHook.RouteOutside`, `PrxRouteHook` + `BuRouteHook` (two layers on `DoRoute`), `LaneSnapHook`, `NsSimHook`, `ErrorTrapLoopHook` (handler). Open: fold the two-layer pairs into one hook each (-2 frames; `PrxRouteHook` has a SavedStacks stub remap, docs/data/swap-stack-audit.csv, so a fold needs the saved-frame check) [verified: stack; cause of the first exception inside `SetLogic` not logged].

2026-11-24 incident 1 (bundle-day-1124-vb bp-vbowner-correct-3, ScriptError 357B987D, StackOverflowException, NPC on Eat Out, failed plan): 64 frames, 14 ours: `BuRouteHook` x8 (`RouteHook`, `RouteBlocked`, `RouteReach`, `RouteView`, `RouteSocial`, `Keep`, `RouteStuck`, `KeepEa`, then EA's `DoRoute` body running `DoRouteFailureBehavior` -> outfit spin), `PrxRouteHook`, `RheEnterHook` + `RabbitHoleHook`, `UfFinishActionHook`, `NsSimHook`, `ErrorTrapLoopHook` [verified: stack]. The 8 `BuRouteHook` frames are not recursion: each failed-plan rule called the next rule, and the last one called the game's body (no method of the class calls `DoRoute` through the hooked entry again) [verified: source].

**Fold (ts3fixes m1124c, `feature/hookflat-1124`, no switch, behaviour unchanged by construction):**

- `SimRoutingComponent.DoRoute`: one hook, `BuRouteHook.RouteHook`. It runs petrelax's `PrxPlanFailed` first (what the outer `PrxRouteHook` did), then the rules. `RouteBlocked` calls each rule in turn (`RouteUse`, `RouteReach`, `RouteView`, `RouteSocial`, `RouteStuck`) and each answers with a step code: yes, no, next rule, unstuck, the game's answer (before a walk the body, after one false), the body again, the body with the balloon off. `RouteHook` itself runs the game's body for the last three. Every rule keeps its statements, catches and counters; only `return X(...)` became `return code`. `PrxRouteHook` is gone (`ApplyPetRelax` reads `sBuMissing` for its "not found" line).
- `RabbitHole.RouteNearEntranceAndEnterRabbitHole`: one hook, `RheEnterHook`, which now derives from `RabbitHoleHook` and calls `RabbitHoleHook.EnterPre` (the old `EnterHook` body: counters [4]-[6], rabbitholedirect, the rabbithole shortcut) before EA's body, with the same delegate (wrapped when rhentryrelax is on) as before. `ApplyRhEntryRelax` patches always; the old early return when `AnimateEnterRabbitHole` is missing only logs now (the hook then runs `EnterPre` + EA, as `RabbitHoleHook` alone did).
- Household / selected / unknown-scope exemptions are in the rules and helpers, which did not change.

Frames of ours on the stack while EA's body runs (static count from the source [verified: source]; the two incident stacks confirm the "before" rows [verified: stack]):

| Path | Before | After | Saved |
|---|---|---|---|
| `DoRoute`, plan ok (every walk) | 2 | 1 | 1 |
| `DoRoute`, failed plan, the game's route-fail answer (incident 1) | 9 | 1 | 8 |
| `DoRoute`, walk then failed replan, unstuck plans again (body again) | 9 | 1 | 8 |
| `DoRoute`, viewlenient's quiet answer (balloon off) | 5 | 1 | 4 |
| `DoRoute`, a rule's own walk: unstuck walk-out / socialreach / viewlenient / reachuse near walk | 8 / 6 / 5 / 4 | 3 | 5 / 3 / 2 / 1 |
| `DoRoute`, blockeduse near or clip walk, woohoosnap near walk | 3 / 4 | 3 / 4 | 0 |
| `RouteNearEntranceAndEnterRabbitHole` (every entry) | 2 | 1 | 1 |
| rabbitholedirect / shortcut steps | 3 | 3 | 0 |

Incident 1 would have been 55 frames (-9), incident 2 62 (-2: one `DoRoute` layer on the walk path, one rabbit hole layer). Incident 2's own throw (the laundry one-shot `SetLogic`) is EA's and stays; with 62 frames ErrorTrap's report sits one frame under the limit, so more cuts there (floor hooks `RouteOutside`, `LaneSnapHook`, `NsSimHook`) are the next step if it recurs.

Read-back: `Fixes.StackedHooks()` ("stacked hooks: N methods: A+B, ..."), called live in world (the tuning-gated patches, `ApplyBlockedUse` among them, go in after `Apply`): every method that carries more than one of our hooks; repro `hookflat-readback`.

Saved frames (docs/242 §2, docs/293): both targets keep one stub that takes EA's header, so `savedstacks` still points an EA frame (a save without ts3fixes, or v0.3.0, which had no `DoRoute` hook) at that stub. A frame inside the old hook bodies (`PrxRouteHook` / `BuRouteHook` of m1124b and earlier test builds, `RheEnterHook` + `RabbitHoleHook` of any build) is refused once at the first load after the update, as for any hook IL change (that Sim restarts). For release saves this adds nothing: v0.3.0's `RabbitHoleHook.EnterHook` already differs from m1124b's (rabbitholedirect), so its frames were refused by the RC without the fold too [verified: source at tag v0.3.0; inferred that the checksum differs, docs/51 §609]. Queue `hookflat-correct-1124-m1124c` (arms A own save, C m1124b save, R v0.3.0 RC save, D uninstall, E vanilla save; R and C each against an m1124b reference load).

## Reproduce

- PE reader plus capstone 5 (`pip install capstone`), Ghidra bounds from `game-copy/analysis/functions.csv`. Jump table: 449 dwords at `0xE59880`; names: `char*` at `0x11949E0 + 4*op`; superop entry `k`: 72 bytes at `0x108FCD8 + 72*k` (`u32` hash, `u16` count, `u16[count]` opcodes), opcode `0x1AA + k`.
- Sample split: for each `probe-out/profile/*simstack*.jsonl` line, take the first `TS3W.exe` frame among the first 4. If it is in `0xE53EB0`-`0xE59880`, bucket it by the greatest handler start <= PC, with the six shared blocks of §1.9 added as starts. The CSV column `handler_self_samples` is that count for the first opcode of each handler.
- Lock checks: for each caller of `0xD790A0`/`0xE6CCD0`, the order of its calls to `0xE85020`/`0xE85030` and the target.
- §9.1: disassemble each superop handler from its start to the next handler start and the handlers of its base opcodes; operand offset of base instruction `i` = sum of the lengths before it (`0x1195088`, `ldc.i4` = 3 words).
- §9.2 live-chain check: per sample, the first of the first 12 frames inside `0xD89CD0`-`0xD89D3B`; count it only if it is frame 0, or frame 1 with frame 0 inside the callee that its return address belongs to (`0xD89CD8` -> `0xE75260`/system DLL, `0xD89CE8` -> `0xEA2AD0`/`0xEA1E30`, `0xD89CF9` -> `0xEA3370`, `0xD89D0D` -> `0xE45F00`/`0xE459F0`, `0xD89D1A` -> `0xEBAB30`, `0xD89D35` -> `0xE521A0`). Field globals: the register pushed as the first argument, loaded from an absolute address in the preceding 10 instructions.
- §9.3: `CustomAttribute` rows (table `0x0C`) whose parent is a `Field` and whose constructor's type is `ThreadStaticAttribute`/`ContextStaticAttribute`; accessors from the index's static read/write lists (`sr`/`sw`); roots as in `tools/autonomy-tier2.ts` (pilot roots and each candidate group), BFS over the index edges with and without the pilot `CUT` list and `dlg` edges, depth 12 and 40. Done with a scratch copy of the two tools, not committed.
- §10.1/§10.2: every code operand and every byte offset of the file equal to the section or field address (absolute stores, `push`/`mov`/`lea` of the address, data-table dwords); `mono_defaults` field names from the two string pushes before each store in `0xE75D00`-`0xE76880`.
- §10.3/§10.4: all `[reg+0x14]`/`[reg+0x15]` byte accesses in the functions that load the TLS context (`[0x11F4160]` users) plus `0xE53740`/`0xE53910`; all writes of `[ebp-0x28]` and every jump into `0xE56287`; uses of `[ebp-0x2C]` + `0x18`..`0x24`.
- §10.6: the three-rule filter of §10.6 over every `probe-out/profile/*simstack*.jsonl` line except `*worker*` files, window = all scanned entries; a helper counts once per sample if any chain entry lies in its Ghidra bounds (end inclusive). Address-taken set: dwords equal to a function start at all four byte alignments of the file, plus `push`/`mov`/`lea` immediates. Scratch script, not committed; the file set grows while game runs write new samples (206 sim-thread files, 75,742 samples at the time of this pass).
