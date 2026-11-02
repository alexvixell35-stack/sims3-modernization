# 30. Design: a second managed (Mono) thread (`[MonoThread]`, docs/23 #17)

Design, with step 1 (P1 + P2) built (§13). It follows up docs/29 §3 (H4), which found that no C# may run off the sim thread today, and it is the step that docs/28 rank 4 (autonomy, story progression) waits on. Static analysis only: Ghidra headless on a copy project (`game-copy/ghidra-mt`, copied from `ghidra-h1`; step 0 in §12 on `game-copy/ghidra-s0`, copied from `ghidra-mt`), Steam `TS3W.exe` 1.67, image base `0x400000`. The game was not run for this note.

Tags: **[verified]** = read in the disassembly or decompile for this note (or in the doc named); **[community-reported]** = stock Boehm GC / Mono design, not checked in this exe; **[inferred]** = reasoning, to confirm in the step named; **[to measure]** = no number yet.

## Summary

- Four patches make a worker thread GC-safe and interpreter-safe: **P1** register the worker (and the sim thread, if it is missing) in Boehm's thread table; **P2** replace the root-scan hook pointer `[0x11949CC]` with one that also pushes the registers and stacks of the other suspended threads; **P3** a GC lock around the allocation and collection entry points, taken only while a worker is active; **P4** move the interpreter's two frame globals `[0x11F4170]` **and** `[0x11F416C]` (docs/29 named only the first) to a TLS slot.
- A worker then enters managed code through the interpreter's runtime-invoke `0xE5A600` directly (not through the `[0x1195B60]` slot that `[RouteWorker]` hooks), after setting the Mono domain TLS. No Mono thread object is attached: this exe never stores one for any thread, the sim thread included (§12.5).
- Only pure managed code on **copied input** is safe on the worker. Game script state (Sims, lots, objects, interactions, alarms) and nearly every game internal call stay sim-thread-only (docs/28 blockers S, E, O).
- First proof: a ts3fixes static method that sorts and hashes a copied `int[]` while allocating and holding temporary objects, run in a loop on a worker while the game plays; `Verify=1` recomputes the result on the sim thread and compares.
- Sim-thread cost: about 0 when `Enabled=0`; a gate test per allocation plus two short jumps per interpreted method call when installed; a lock per allocation only while a worker is active. Estimated 0.2-2% of sim iterations/s, all [to measure]; the acceptance rule is <= ~2% (PLAN Goal).
- Effort: ~2.5-3.5 weeks to a stable first proof, larger than the 1-2 weeks docs/29 §3.3 estimated, because P3 covers more entry points than `GC_malloc` and P4 has two globals and four code sites. Step 0 is done (§12): P3 gains four detours and one function replacement, the finalizer rule and the thread attach drop out, so the total stays the same. **Correction (docs/57 §7):** P3 needs **17 detours** (19 with the optional `GC_disable`/`GC_enable` pair) plus the `0xE4B3F0` replacement, not 12, and the lock must be re-entrant (§3). Autonomy offload still needs a world snapshot on top of this (docs/28 rank 4).

## 1. Blocker 1: only one thread is registered

### What the code does [verified]

- Thread table at `0x1221000`: 256 entries of 0x14 bytes: `+0x0` in use, `+0x4` thread id, `+0x8` duplicated handle, `+0xC` stack base, `+0x10` suspended. Highest used index is `[0x11F4124]`.
- `GC_new_thread` `0xE51070` registers the **calling** thread: it claims a free entry with an interlocked exchange on `+0x0` (import `[0xF95234]`), raises `[0x11F4124]` with an interlocked loop, duplicates the current thread handle into `+0x8`, stores the stack base from `GC_get_stack_base` `0xE4EAF0` (VirtualQuery of the current stack page) into `+0xC` and the thread id into `+0x4`. At the end it waits (`Sleep(20)` loop, `[0xF95110]`) while `[0x11F4120]` is set. `[0x11F4120]` is the "world stopped" flag: `GC_stop_world` `0xE511A0` sets it, `GC_start_world` `0xE51290` clears it. It returns the entry address. No lock is taken.
- Its only caller is `GC_thr_init` `0xE513D0` (once, flag `[0x11F4118]`), called from `GC_init` `0xE4E160`. There is no unregister function.
- `GC_stop_world` takes critical section `0x1222580`. For every in-use entry whose thread id differs from the caller's, it checks `GetExitCodeThread`: a thread that has exited gets its entry freed (`+0x0` and `+0xC` zeroed, handle closed); a live one gets `SuspendThread` and `+0x10 = 1`. `GC_start_world` resumes every entry with `+0x10` set except the caller's and clears `+0x10`. A failed `SuspendThread` or `ResumeThread` aborts through `0xD707E0` (strings `0x108E284`, `0x108E2C8`).
- Which thread runs `GC_init` is open (docs/29 §4.3). If it is the main thread, the sim thread is not in the table, and a collection that a worker starts would not suspend the sim thread.

### Change P1

1. **Worker start:** the worker calls `0xE51070` itself (it registers the caller), **while holding the GC lock (P3)**. The lock matters: the function writes the handle and stack base after it claims the entry, and a `GC_stop_world` running at the same time on the sim thread would call `SuspendThread` on handle 0 and abort the game [inferred from the order of the writes]. With the lock held, no collection runs, so the `[0x11F4120]` wait loop never spins.
2. **Sim thread:** when the patch turns on, at the top of a `MonoScriptHost_Simulate` tick, the sim thread scans the table for its own thread id and calls `0xE51070` if it is missing (under the GC lock). The main thread stays registered if it was the `GC_init` thread; it is suspended in every stopped mark as it is today.
3. **Worker exit:** under the GC lock, close the entry's handle and zero `+0x0`/`+0xC`. Without this, the next `GC_stop_world` frees the entry anyway (exit-code check), so a missed unregister is not a crash [verified from the code above].
4. Log the table at enable time (index, thread id, which thread is the sim thread); that also answers docs/29 open point "which thread runs `GC_init`".

Hook sites: none in game code. P1 only calls `0xE51070` and writes table entries. Check before use: the first 16 bytes of `0xE51070` (`56 57 8B 3D 34 52 F9 00 6A 01 68 00 10 22 01 33`) [verified].

## 2. Blocker 2: the root scan covers only the collecting thread

### What the code does [verified]

- `GC_push_roots` `0xE4D910` (one caller, `0xE4D270`, the mark state machine) pushes static roots, then the collector's registers (`0xE4BA10`: `GC_push_one` `0xE4D520` for EAX, EBX, ECX, EDX, EBP, ESI, EDI), then tail-jumps to the hook:
  `0xE4D9DC  A1 CC 49 19 01   mov eax,[0x011949CC]` / `85 C0 74 02 FF E0` (`test; jz ret; jmp eax`).
- `[0x11949CC]` holds `0xD71820` (EA's `GC_push_other_roots`): `VirtualQuery` of the current stack page, then `GC_push_all_stack` `0xE4CEE0(lo = own esp area, hi = page + RegionSize + 1)`. `0xE4CEE0` has no other caller. The table's stack bases (`+0xC`) are read only by `GC_new_thread`, `GC_stop_world` and `GC_get_next_stack` `0xE51360`; nothing pushes another thread's stack, and the exe does not import `GetThreadContext`.
- The GC is incremental with `GetWriteWatch` dirty bits (docs/13); a cycle ends with a stopped mark that rescans roots.

### Change P2

Write a new function pointer into `[0x11949CC]` (a data write; check the old value is `0xD71820` first, else refuse). The new function:

1. Calls `0xD71820` (unchanged behaviour for the collector's own stack).
2. If `[0x11F4120] == 1` (world stopped), for each entry `0..min([0x11F4124], 255)` with in use, `+0x10 == 1` and thread id != own id: `GetThreadContext(handle, CONTEXT_INTEGER | CONTEXT_CONTROL)` into a preallocated static, 16-byte aligned `CONTEXT`; `GC_push_one` `0xE4D520` for EAX, EBX, ECX, EDX, ESI, EDI, EBP; `GC_push_all_stack` `0xE4CEE0(ctx.Esp, entry+0xC)`. This is stock Boehm's `GC_push_all_stacks` for Win32 [community-reported]. `GetThreadContext` also completes the asynchronous `SuspendThread` [community-reported, Win32 behaviour].
3. If the world is not stopped (an incremental step while all threads run), push nothing extra. The final stopped mark rescans all stacks, and heap writes by any thread are caught by `GetWriteWatch`, which is process-wide [inferred]. Root pushes with the world running do happen (incremental steps and `GC_finalize` overflow recovery), and every cycle ends in a stopped mark that pushes roots again [verified, §12.6].
4. A failed `GetThreadContext` must not be ignored (an unscanned stack frees live objects): count it, and fall back to pushing the whole committed stack range `[stack limit, base)` from `VirtualQuery` (conservative, slower, safe).

Constraints: no allocation, no `Log`, no lock in this function (it runs with other threads suspended, possibly holding the CRT heap or EA arena lock). `GetThreadContext` and `VirtualQuery` take no user-mode lock [inferred]. The sim thread is pushed by this path whenever a worker collects, so the same code covers both directions.

## 3. Blocker 3: allocation and collection have no lock

### What the code does [verified]

- `GC_malloc` fast path `0xE4BE30`: size class from `0x12246F8`, pops `GC_objfreelist` `0x12226E8`, adds to `GC_words_allocd` `0x12225C4`; no lock. Its slow path and large objects go to `GC_generic_malloc` `0xE4BCE0`.
- Boehm's `LOCK()` is compiled out: `GC_try_to_collect` `0xE4A050` goes straight from `GC_notify_or_invoke_finalizers` `0xE4B480` to the collection, with no lock call in between. The one critical section, `0x1222580`, is taken only by `GC_stop_world` and the `gc.log` writer `0xE4DD50`.
- Every writer of `GC_words_allocd` (the full allocation entry set): `0xE4BE30` (`GC_malloc`), `0xE4BDC0` (fast path on the atomic list, 6 Mono callers: `0xEA2770`, `0xE50460`, `0xEA14B0`, `0xEA3F00`, `0xEA2E70`, `0xEA1510`), `0xE4BF90` (`GC_malloc_uncollectable`, corrected by docs/57 §7; host callers `0xD71800`, `0xD71900`, `0xD714B0`, `0xD8A830`, plus `0xD7E634`, `0xD70DA5`, `0xD70DB5` in code Ghidra has no function for), `0xE50DE0` (typed/explicit allocation, callers in `0xEA2770`, `0xEA2690`, `0xE697D0`, `0xD71900`, `0xD718A0`), `0xE4BCE0`, `0xE4BBB0` (inner small allocation, also called from `0xE4DFF0`, `0xE4AC60`, `0xE4AAC0`, `0xE4BCA0`), `0xE4BCA0`, and the collector's own `0xE49A50`, `0xE49900`, `0xE49C80`, `0xE4A120`, `0xE4E450` [verified by operand scan].
- Collection entries: `GC_try_to_collect` `0xE4A050` (only caller `0xD819AA` in `MonoScriptHost_Simulate`), `0xE4A0B0` (`GC_gcollect` [inferred]; callers host `0xD702A0` and the thunk `0xE69650`, which is `mono_gc_collect`, so managed `GC.Collect` reaches it [inferred]), `GC_enable_incremental` `0xE4E450` (one host caller `0xD7E602`), and the forced collection inside the allocation slow path (docs/13).
- `0xE4B480` runs finalizers inline (`jmp 0xE4B3F0`) unless `GC_finalize_on_demand` `[0x11F3028]` is set; otherwise it calls the notifier `[0x11F3044]` once per collection [verified]. It is called at the start of `0xE4BCE0`. In this exe it never runs finalizers: Mono's GC init `0xE69570` sets `[0x11F3028] = 1`, no code writes `[0x11F3044]` (it stays NULL), and the only other writer, `0xE78E20`, saves and restores the flag around a run of one finalizer [verified, §12.3].

### Change P3: a gated GC lock

- One `CRITICAL_SECTION` (or an SRW lock in exclusive mode) `g_gcLock`, plus a flag `g_gcLocking` read by every hook.
- Detours (ts3native `InstallDetour`, 5-byte `jmp`, prologues copied verbatim) on the public entry points `0xE4BE30`, `0xE4BCE0`, `0xE4A050`, `0xE4BDC0`, `0xE4BF90`, `0xE50DE0`, `0xE4A0B0`, `0xE4E450`, and the finalizer and weak-link entries `0xE4AAC0`, `0xE4ABE0`, `0xE4AE30`, `0xE4A3F0` (prologue bytes and lengths in §12.1). Each hook: `if (!g_gcLocking) jmp trampoline;` else take the lock (recursive: a `CRITICAL_SECTION` allows the nested `0xE4BE30 -> 0xE4BCE0` call), call the original, release.
- **Five more detours (docs/57 §7, prologues there), 17 in all:** `GC_free` `0xE4BEB0` (pushes onto the same free lists `GC_malloc` pops, no lock; missed above because it does not write `GC_words_allocd`), EA's interpreter frame arena `0xD71900` (grow), `0xD71A20` (pop), `0xD71B70` (release all), which keep 2 KB frame chunks on a global unlocked list, and `GC_add_roots` `0xE4D9F0` (lazy one-time root registrations). Optional: `GC_disable`/`GC_enable` `0xE4DFE0`/`0xE4DFD0`. The lock must stay **re-entrant**: `0xD71900 -> 0xD718A0 -> GC_malloc` and `0xD71A20 -> GC_free` nest inside one another [verified, docs/57].
- Also inside the lock: Boehm's finalizer and disappearing-link registration (Mono `GCHandle`, weak references, `mono_object_register_finalizer`), `GC_apply_to_each_object` `0xE4A3F0` (S3MM uses it), and the finalizer queue pop in `GC_invoke_finalizers` `0xE4B3F0`. Step 0 found the entry points (§12.2): register and unregister disappearing link `0xE4AAC0`/`0xE4ABE0`, register finalizer `0xE4AE30`, and `0xE4B3F0`, which is replaced whole (pop one entry under the lock, run the finalizer outside it) so a worker never waits for a finalizer to finish. The first proof still keeps GC handles and objects of classes with finalizers off the worker: Mono's own `GCHandle` table (`0xE78780`, `0xE78AE0`) is not locked at all; its section `0x11F42F0` is initialised but never entered (docs/58).
- **Flag changes only on the sim thread, at the top of a Simulate tick** (it is then outside every GC function). On: set `g_gcLocking = 1`, then release the worker. Off: the worker reports it is idle and outside managed code, then the sim thread clears the flag. This keeps the sim thread from being inside an unlocked allocation when locking starts.
- **Finalizers stay on the sim thread** with no extra code: `GC_finalize_on_demand` is already 1 and the notifier is NULL, so `0xE4B480` in a worker's allocation does nothing (§12.3). Finalizers run only from `mono_gc_invoke_finalizers` `0xE697F0` (callers `0xD81A09`, `0xD81A30` in `MonoScriptHost_Simulate`, and `0xE790E0`) [verified]. A worker collection can append to the finalizer queue `[0x11F303C]` (in `GC_finalize` `0xE4AE60`) while the sim thread pops it, hence the `0xE4B3F0` replacement above.
- Collections stay legal on the worker (its slow path may force one). It then holds the lock, stops the world (the sim thread is suspended wherever it is) and pushes the sim thread's stack through P2.
- Not chosen for the first version: per-thread free lists (Boehm `thread_local_alloc`). They remove the lock cost but change the allocator itself; consider them only if the lock costs more than ~1% (§8).

## 4. Blocker 4: interpreter frame state in globals

### What the code does [verified]

`ves_exec_method_with_context` `0xE53EB0` keeps the current frame in `[0x11F4170]` and the base of its local frame-info array in `[0x11F416C]`, saves the old values on entry and restores them on both exits. The only reader outside it is `0xE534E0` (frame lookup by depth, stride 0x130, via `+0x140`), whose only caller is the host function `0xD71CC0`. These are all references to either address in the exe (operand scan of every instruction; an access through a computed pointer would not show) [verified]. `0xD71CC0` is the script call-stack frame reporter (§12.7):

| Site | Bytes | Instruction |
|---|---|---|
| `0xE53EBD` | `A1 70 41 1F 01` | `mov eax,[0x11F4170]` (save old frame) |
| `0xE53EC2` | `8B 0D 6C 41 1F 01` | `mov ecx,[0x11F416C]` (save old array) |
| `0xE53ED7` | `89 1D 70 41 1F 01` | `mov [0x11F4170],ebx` (set frame = arg) |
| `0xE53EDD` | `A3 6C 41 1F 01` | `mov [0x11F416C],eax` (set array = `ebp-0x4C74`) |
| `0xE597BA` | `A3 70 41 1F 01` | `mov [0x11F4170],eax` (restore, exit A) |
| `0xE597BF` | `89 0D 6C 41 1F 01` | `mov [0x11F416C],ecx` (restore, exit A) |
| `0xE59866` | `89 15 6C 41 1F 01` | `mov [0x11F416C],edx` (restore, exit B) |
| `0xE5986C` | `89 0D 70 41 1F 01` | `mov [0x11F4170],ecx` (restore, exit B) |
| `0xE534E0` | `8B 15 6C 41 1F 01` | reader (`0xE534EA`: `A1 70 41 1F 01`) |

Exit A re-read in step 0 from an instruction boundary: `0xE597BA A3 70 41 1F 01`, `0xE597BF 89 0D 6C 41 1F 01`, next instruction `0xE597C5 8D A5 80 B3 FF FF` (`lea esp,[ebp-0x4C80]`); the loads `0xE597B1 8B 45 A0`, `0xE597B4 8B 4D 9C` and `0xE597B7 83 C4 0C` come before it [verified]. Exit B: `0xE59860 8B 55 9C`, `0xE59863 8B 4D A0`, then the two stores, next `0xE59872` [verified]. No code reference targets an instruction inside the entry block or either exit block (`0xE53EC2`, `0xE53ED7`, `0xE53EDD`, `0xE597BF`, `0xE5986C` have none) [verified, Ghidra reference manager; a computed jump would not show].

Two threads in the interpreter overwrite each other's values, and the non-nested restores can leave a pointer into a dead stack frame that `0xE534E0` then reads [inferred].

### Change P4

- `TlsAlloc` one slot pair (or one slot holding a two-word struct). Require an index < 64, so the value sits at `fs:[0xE10 + 4*index]` in the TEB; otherwise refuse (log, stay off).
- Rewrite the entry block `0xE53EBD`-`0xE53EE1` (37 bytes, straight-line, no relative operands) as a `jmp` to a stub that does the same moves with `fs:` operands and jumps back to `0xE53EE2`. Same for exit A (`0xE597BA`-`0xE597C4`) and exit B (`0xE59866`-`0xE59871`). Each block starts and ends on an instruction boundary, and the bytes are checked before writing (all or nothing, as `PatchImm32` does).
- Replace `0xE534E0` with a C function that reads the two values from the TLS slot (98 bytes, simple logic, see the table).
- The TLS slot `[0x11F4160]` (interpreter `ThreadContext`) is already per thread (docs/29) and needs nothing.
- Patch time: at enable, on the sim thread at a tick boundary, before any worker exists. No thread is inside these blocks then, except possibly a thread preempted inside one; the blocks are short and only the sim thread runs managed code at that point [inferred]. The patch stays in until exit.
- **Our own readers of `[0x11F4170]` follow the slot (0.36.106, docs/51 §377).** After P4 the game never writes the global again, so it keeps the value from install time. ts3native reads it in `CanYield` (`[RouteAsync]` START and `JobYield`) and in `gcroots.cpp` (Crash S task line, `[GcRootCheck]`). With the stale value `CanYield` was false for every START: bp-mt-gates-off (2026-10-29, asi rel020) refused all plans as NoYield (0 jobs started, `invokeHook 0`, `usfDeferred 0`). `MtInstallP4` now points `g_curState` at the sim thread's own TEB slot (`fs:[0x18] + 0xE10 + 4*slot`) once the entry block is in. Switches off: nothing changes (P4 never installs).

## 5. How a worker enters managed code

1. **Start:** a ts3native thread with a 256 KB stack. Under the GC lock: register (P1).
2. **Domain:** set the Mono domain TLS `[0x11959DC]` with `0xE75270` (`TlsSetValue` wrapper) to the root domain that `mono_domain_get` `0xE75260` returns on the sim thread [verified for both functions]. Without it, `mono_object_new` faults on a NULL domain (docs/18, route worker crash).
3. **Mono thread object: none.** `threads.c` keeps the current `MonoThread` in TLS key `[0x1195B7C]` (allocated in `mono_thread_init` `0xEBB180`). The only `TlsSetValue` on that key in the whole exe stores NULL (`0xEBAE0D`, in `mono_thread_exit` `0xEBADF0`), so `mono_thread_attach` is not in this build and the sim thread has no thread object either (§12.5) [verified]. The worker therefore matches the sim thread with no attach call. `mono_thread_interruption_checkpoint` `0xEBB480` returns early on NULL [verified]. Thread-static fields do not: `0xEBAB30` (special static data, 5 call sites in the interpreter) dereferences the thread object without a NULL check, so worker code, like sim code, must not touch `[ThreadStatic]` fields.
4. **Interpreter context:** none to create; `0xE59FD0` makes one when `[0x11F4160]` is NULL for this thread [verified, docs/29].
5. **Invoke:** call the runtime-invoke function `0xE5A600` **directly** with `(method, obj, params, &exc)`. Do not call through the slot `[0x1195B60]`: with `[RouteWorker]` on, ts3native hooks it and proxies calls back to the waiting thread (docs/18). Never call the host wrappers `0xD7EED0`, `0xD7F000`, `0xD7F210`: they write the host's running-task fields `host+0xBC8`/`+0xBD0` (docs/18).
6. **Methods:** resolve the `MonoMethod*` on the sim thread (`mono_class_from_name` `0xE6FA90`, `mono_class_get_method_from_name_flags` `0xE70330`, docs/06) and **call it once there first**. That runs class constructors, creates vtables and transforms the method (`0xE643B0`, locked by `0x11F4174`) on the sim thread, so the worker does not hit class init or the loader.
7. **Exceptions:** a managed exception comes back in `exc` (caught by the `setjmp` in `0xE59FD0` on the worker's own stack) [inferred]. Count it, drop the result, keep running; never print it through the game's script error path (host code).
8. **Worker rules (docs/56, docs/57, docs/58):** no `GCHandle` and no weak references (the handle table bitmap is updated with no lock, docs/58); no first-time class or vtable setup on the worker (class init, `mono_class_create_runtime_vtable`, typed-descriptor creation `0xE50F90`/`0xE50460`: run it on the sim thread first, step 6); invoke only through `0xE5A600` and never through `ScriptHost_NativeToManaged` `0xD8A8E0` (its call-record list `[0x11EE5A4]` is global and unlocked, docs/56); warm the native-call thunk hash on the sim thread by calling every internal-call / P/Invoke method once there first, because `tramp_create_thunk` `0xE51400` fills `[0x11F4128]` with no lock on first use (docs/56, docs/58).

### What the worker may call [inferred, from docs/28 and docs/04a]

| Safe | Not safe |
|---|---|
| Interpreted code of our own methods, arithmetic, locals, arrays, `List<T>`/`Dictionary<K,V>` it owns | Any `Sims3.SimIFace` / `Sims3.Gameplay` internal call or object (world, routing, animation, UI, resources): docs/28 blockers S, E, L |
| Allocation of plain objects, arrays, strings (with P3) | `Simulator.*` (task system, sleeps, yields): host task fields, task model |
| Pure `mscorlib` internal calls: `Math.*`, `Buffer.BlockCopy`, `Array.Copy`, `String` concat/compare | Static constructors, reflection, `Type.GetType`, assembly loading (loader locks; class init runs arbitrary script) |
| Reading arrays copied for it by the sim thread | `GCHandle`, weak references, objects of classes with finalizers (Boehm tables are under the P3 lock; Mono's handle table has no lock in this build, docs/58) |
| | `[ThreadStatic]` fields (NULL thread object, §5 step 3) |
| | Any game object reference, even read-only: other tasks mutate it between yields (docs/28 S2) |

The first proof keeps to the left column. A runtime guard for the right column is a Verify-mode extra: hook the internal-call transition (docs/28 N2 `0xDA5970`, to confirm) and count calls from the worker thread.

## 6. Game script state that stays unsafe

Unchanged by this patch (docs/28 §1b, docs/04a §1): scripts have no locks at all; every `ITask` assumes the one sim thread; interaction `Test()` and callbacks are arbitrary script, mods included (S2, S10); the routing lock `RoutingWorld+0x918` (L); synchronous events into animation and UI (E); results needed in the same tick (O). So the usable pattern is **snapshot -> pure compute on the worker -> apply on the sim thread**: the sim thread copies the inputs into plain arrays, the worker computes, and the sim thread checks and applies the result at a later tick. Autonomy scoring (S2) needs a world snapshot design of its own; story progression (S11) and motive math (S4) are smaller snapshot candidates.

## 7. First proof: `[MonoThread]` Mode=1

- **ts3fixes side:** a static class `MtProbe` with `static int Run(int[] input, int[] scratch)`: copies `input`, sorts it, builds a `List<int>` and a `Dictionary<int,int>` from it, boxes a few values, concatenates a short string, and returns a hash. It allocates on purpose, so the GC paths (P1-P3) are exercised. It references no game type. A runtime switch `mtprobe` (ts3fixes switch convention) lets the sim thread copy a fresh input each tick: the motive values of the active household's Sims, as plain ints (copied on the sim thread, so the worker never sees a game object).
- **ts3native side:** at a tick boundary the sim thread hands the input array (rooted by a static field in `MtProbe`, so it outlives the call) to the worker; the worker calls `Run` in a loop and publishes `(hash, sequence)`; the sim thread reads it the next tick.
- **GC stress (Mode=2):** the worker also keeps a linked list of 10,000 small objects that only its stack and registers reference, rebuilds part of it each loop and verifies every node's payload. A missed root (P2 bug) shows as a payload mismatch when a freed node is reused. This is the test docs/29 asked for (probe-safety: a dummy that hits the path).
- **Verify=1:** the sim thread runs `Run` on the same input and compares the hash (`mismatches` counter), and checks the stress list at random ticks.
- ini section, default off, as the other sections:

```ini
[MonoThread]
; Second managed thread (docs/30): GC thread registration, multi-stack root scan, gated GC lock,
; per-thread interpreter frame globals, and one worker running ts3fixes MtProbe.Run on copied input.
; OFF until verified in game. 1 = install the patches at the first Simulate tick.
Enabled=0
; 0 = patches only, no worker (measures the sim-thread cost); 1 = worker runs MtProbe.Run; 2 = 1 + GC stress list.
; Runtime switch: monothread.
Mode=0
; 1 = the sim thread recomputes each result and checks the stress list.
Verify=0
```

- **Counters:** a new ts3native counters version (the next free one in `docs/registry.md` when the code lands; v13 at the time of writing): `mtReady`, `mtMode` (switch), `mtVerify` (switch), `mtThreads` (registered entries), `mtSimRegistered` (1 if P1 had to add the sim thread), `mtRuns`, `mtRunUs`, `mtMaxRunUs`, `mtMismatches`, `mtExceptions`, `mtStacksPushed`, `mtCtxFails`, `mtLockedAllocsK`, `mtLockWaitUs`, `mtWorkerGcs`, `mtFinalizerSkips`. Reserve the version in the issue first (registry rule).

## 8. Sim-thread cost

| Part | When | Cost per event | Estimate |
|---|---|---|---|
| P1 registration | once per worker start | ~10 µs | 0 |
| P2 root scan | each stopped mark with a worker | `GetThreadContext` + scan of the worker's used stack (<= 64 KB) ~20-50 µs | < 0.1 ms/s [inferred] |
| P3 gate | each allocation, patch installed, no worker | detour `jmp` + one compare, ~1 ns | allocations/s [to measure: `mtLockedAllocsK` with Mode=0 counts them]; likely 0.1-0.5% [inferred] |
| P3 lock | each allocation while a worker runs | uncontended critical section ~10-20 ns, plus waits | ~0.5-1.5% at 0.5-1 M allocations/s [inferred]; waits in `mtLockWaitUs` |
| P4 TLS stubs | each interpreted method call | two extra jumps at entry and two at exit, `fs:` moves | method calls/s [to measure]; likely 0.1-0.5% [inferred] |
| Worker collections | when the worker's allocation forces one | the sim thread is suspended for the stopped mark, as with its own forced GCs | same as today's forced GCs; visible in `mtWorkerGcs` |

Measure with Mode=0 against Enabled=0 in one load (`--ab`, runtime switch), then Mode=1. Over ~2% needs a plan: per-thread free lists for P3 first.

## 9. Test plan

1. **Selftest** (`native/ts3native/selftest.cpp`, no game): a fake thread table with two real threads; the P2 push function against fake `GC_push_one`/`GC_push_all_stack` that record ranges (the suspended thread's ESP..base must be pushed, registers included); P3 hooks against a fake free list hammered by two threads with the lock on (no lost or duplicated objects) and off (gate path); P4 stubs run on copies of the original bytes against a fake frame (same values in the TLS slot as the original writes to the globals).
2. **Static checks in step 0**: done (§12).
3. **In game, one bundle run per world (BP, SV, IP), with `--ab` on `monothread`:** Mode=0 vs off (cost), then Mode=1 Verify=1 for 30+ sim-minutes, then Mode=2 Verify=1. Pass: 0 mismatches, 0 crashes, 0 `mtCtxFails`, no new stuck Sims, sim iterations/s within ~2%. One save in each run (the save path allocates heavily).
4. **Crash triage** against the change ledger: EIP in the mark code (`0xE4C3E0`, `0xE4B8D0`) or a fault on a reused object -> missed root (P2); EIP in `0xE4BE30`/`0xE4BBB0` or a corrupt free list -> lock gap (P3, an unhooked entry); fault in `0xE534E0`/`0xD71CC0` or in the interpreter exit -> P4; fault under `0xEA36F0` with a NULL domain -> worker attach (§5); "SuspendThread failed" abort -> registration race (P1). Stop the run at the first one and fix (stop-tests rule).

## 10. Risks

1. **An unlisted allocation or GC-table entry** (finalizer, disappearing link, a Mono path that pops a free list itself): heap corruption that shows up much later. Mitigation: the operand scan of `GC_words_allocd` and the free lists (§3) plus the step 0 scan of `finalize.c`; first-proof code avoids handles and finalizers; Mode=2 stress.
2. **Suspension while the other thread holds a lock the collector needs** (CRT heap, EA arena `0x4E69D0`, Mono locks): deadlock. The collector path (P2) is allocation- and lock-free, but finalizer or notifier code run by a collection is not; keep it on the sim thread (§3).
3. **Mono runtime state outside the GC**: class init, vtable creation, method transform, string interning, `threads.c` state. Locks exist (critical sections `0x11F4130`, `0x11F4174`, `0x11F4290` are initialised) [verified], but EA may have dropped others as it dropped Boehm's `LOCK()`. Mitigation: warm-up on the sim thread (§5 step 6) and the allowed-call list.
4. **Incremental GC with concurrent mutators**: stock Boehm supports it with write-watch dirty bits [community-reported], but EA's changes to the incremental path (docs/13) were only tested single-threaded. Confirm the root-push states in step 1; the Mode=2 stress runs across many incremental cycles.
5. **Interaction with other ts3native patches**: `[RouteWorker]` hooks the runtime-invoke slot (bypassed by calling `0xE5A600`); `[GC]` heap settings and `[Alloc]` are independent [inferred]; the gcthreads measurement detours `0xE4A2B0` and `0xE4BA90` sit below the P3 hooks and share no bytes with them [verified]. S3SS: no overlap with any P1-P4 site (§12.8).
6. **Value**: the first proof moves almost nothing off the sim thread (a hash of a few hundred ints). It proves the mechanism; gains need snapshot designs per workload (docs/28 rank 4).

## 11. Steps and effort

| Step | Work | Effort [inferred] |
|---|---|---|
| 0 | Static: prologues, exit-A bytes, finalizer/disappearing-link entry points, finalizer flags, `mono_thread_attach`, root pushes with the world running, role of `0xD71CC0` | **done** (§12) |
| 1 | P1 + P2 with selftest; enable-time table log | **built, selftest ok, not run in game** (§13) |
| 2 | P3 gated lock on 12 entries plus the `0xE4B3F0` replacement, selftest | 2.5-3.5 days |
| 3 | P4 stubs and `0xE534E0` replacement, selftest | 2 days |
| 4 | Worker domain TLS and invoke (§5, no thread attach), ts3fixes `MtProbe` + switch, Verify, counters, registry row | 2.5-3.5 days |
| 5 | In-game: Mode=0 cost A/B, Mode=1/2 Verify runs on three worlds, fixes | 3-5 days |
| | **Total to a stable first proof** | **~2.5-3.5 weeks** |
| 6 | First real workload (snapshot -> worker -> apply), e.g. story progression or motive math; autonomy later | separate design |

Steps 1-3 can be built and selftested without the game; nothing is turned on in the stable build until step 5 passes.

## 12. Step 0 results (static)

Ghidra headless on `game-copy/ghidra-s0` with a scratch script (instruction bytes, references, operand scans), plus one raw byte scan of the exe for `[0x1195B7C]`. No decompiled code is kept in the repo.

### 12.1 Hook prologues [verified]

None of these has a relative operand, and no code reference targets an instruction inside the copied range (Ghidra reference manager; computed jumps would not show). Each hook checks the bytes and the address before writing; `0xE4A050` and `0xE4A0B0` have identical first instructions.

| Entry | Role | Copied bytes (instruction boundaries) | Length | Resume at |
|---|---|---|---|---|
| `0xE4BE30` | `GC_malloc` | `8B 44 24 04` / `B9 00 08 00 00` | 9 | `0xE4BE39` |
| `0xE4BDC0` | atomic fast path | `8B 44 24 04` / `B9 00 08 00 00` | 9 | `0xE4BDC9` |
| `0xE4BF90` | `GC_malloc_uncollectable` (docs/57; was `mono_gc_malloc`) | `8B 0D B4 49 19 01` | 6 | `0xE4BF96` |
| `0xE50DE0` | typed allocation | `A1 B4 49 19 01` | 5 | `0xE50DE5` |
| `0xE4BCE0` | `GC_generic_malloc` | `83 3D 04 41 1F 01 00` | 7 | `0xE4BCE7` |
| `0xE4A050` | `GC_try_to_collect` | `83 3D 94 30 1F 01 00` | 7 | `0xE4A057` |
| `0xE4A0B0` | `GC_gcollect` | `83 3D 94 30 1F 01 00` | 7 | `0xE4A0B7` |
| `0xE4E450` | `GC_enable_incremental` | `83 3D C0 30 1F 01 00` | 7 | `0xE4E457` |
| `0xE4AAC0` | register disappearing link | `53` / `8B 5C 24 08` | 5 | `0xE4AAC5` |
| `0xE4ABE0` | unregister disappearing link [role inferred] | `A1 40 48 19 01` | 5 | `0xE4ABE5` |
| `0xE4AE30` | register finalizer (wrapper of `0xE4AC60`) | `8B 44 24 14` / `8B 4C 24 10` | 8 | `0xE4AE38` |
| `0xE4A3F0` | `GC_apply_to_each_object` | `8B 44 24 04` / `50` | 5 | `0xE4A3F5` |
| `0xE4B3F0` | `GC_invoke_finalizers` | `51` / `53` / `56` / `8B 35 3C 30 1F 01` | 9 | replaced whole, no trampoline |

`mono_gc_invoke_finalizers` `0xE697F0` starts with a relative `call 0xE4B3E0` (`E8 EB 1B FE FF`), so it is not detoured; hooking `0xE4B3F0` covers it.

### 12.2 Finalizer and weak-reference tables [verified]

- Disappearing links: hash table head `[0x11F3030]`, entry count `[0x11F3034]`, log size `[0x1194840]`. Users: register `0xE4AAC0` (string `"Bad arg to GC_general_register_disappearing_link"`, allocates entries with `0xE4BBB0`), whose only caller is the Mono wrapper `0xE696C0` (called from `0xE78780` twice and `0xE78AE0`, Mono's weak `GCHandle` paths [inferred]); unregister `0xE4ABE0` via `0xE696E0`; `GC_finalize` `0xE4AE60`; the root push of both tables `0xE4A9A0` (called from `GC_push_roots`).
- Finalizers: head `[0x11F3038]`, count `[0x11F3040]`, log size `[0x1194844]`. Users: `0xE4AC60` (register inner), only via `0xE4AE30`, which `gc.c` calls from `0xE78630` (`object_register_finalizer`, via `0xE78F60`, called by the object allocation paths `0xEA2690`, `0xEA2710`, `0xEA3140` and by `0xE8E6E0`), `0xE786F0` (an internal call, table `0x10B5A2C`; `SuppressFinalize` [inferred]) and `0xE78E20` (runs one finalizer); `GC_finalize` `0xE4AE60`; `0xE4A9A0`.
- Ready queue `[0x11F303C]`: filled by `GC_finalize` (called from finish-collection `0xE49A50`), popped without a lock by `GC_invoke_finalizers` `0xE4B3F0` (callers: the inline path in `0xE4B480`, unused in this exe, and `mono_gc_invoke_finalizers` `0xE697F0`).
- So allocating an object of a class with a finalizer registers it in the finalizer table from inside the allocation path. With the P3 hook on `0xE4AE30` that is under the lock; the first proof still avoids it.

### 12.3 Finalizer flags [verified]

`[0x11F3028]` (`GC_finalize_on_demand`): read by `0xE4B480`; written `= 1` by `0xE69570` (Mono `boehm-gc.c` init, called from `0xE66240` and `0xE75BF0`, guarded by a once flag), and saved, set to 1 and restored by `0xE78E20`. `[0x11F3044]` (the notifier): one read in `0xE4B480`, no writer. Result: `0xE4B480` never runs finalizers or a notifier on any thread.

### 12.4 Exit-A bytes [verified]

See §4: the table rows for `0xE597BA` and `0xE597BF` are right; the block `0xE597BA`-`0xE597C4` is 11 bytes, straight-line, resume at `0xE597C5`.

### 12.5 `mono_thread_attach` [verified]

Not present. Every reference to the current-thread TLS key `[0x1195B7C]` (operand scan in Ghidra, and a raw byte scan of the exe for `7C 5B 19 01`: 21 sites, all in `0xEB9F40`-`0xEBB9C1`) is a `TlsGetValue`, the `TlsAlloc` store in `0xEBB180`, the `TlsFree` in `0xEBA750`, or the one `TlsSetValue(key, NULL)` at `0xEBAE0A`-`0xEBAE0D` in `mono_thread_exit` `0xEBADF0`. A new TLS slot reads NULL on every thread (Win32), so the sim thread runs with no `MonoThread` [inferred from the above]. EA's script tasks do not use Mono threads. Effect on the plan: §5 step 3 needs no code, and `[ThreadStatic]` access faults on any thread (`0xEBAB30`).

### 12.6 Root pushes with the world running [verified]

- `GC_stop_world` `0xE511A0` has one caller, `GC_stopped_mark` `0xE49980`. `GC_mark_some` `0xE4D270` (one caller, the wrapper `0xE4D460`) calls `GC_push_roots` `0xE4D910` in states 1, 2 and 4/5 once the mark stack is empty.
- Callers of `0xE4D460`: `GC_stopped_mark` (world stopped); `0xE49DA0`, the incremental step (only caller `0xE49F10`), which runs `0xE4D460` in a loop **with the world running** and calls `GC_stopped_mark` once marking is done; and `GC_finalize` `0xE4AE60` at three sites (mark-stack overflow recovery, after the stopped mark has restarted the world [order inferred from Boehm]).
- So `[0x11949CC]` is called with the world running, and P2 must test `[0x11F4120]` as designed. `GC_stopped_mark` always calls `GC_initiate_gc` `0xE4C2A0` after stopping the world (state 0 becomes 1, 5 stays, anything else aborts with `"unexpected state"`), so every cycle ends with a root push, P2 stacks included, while the world is stopped. That is the condition §2 step 3 needed.

### 12.7 `0xD71CC0` [code verified, use inferred]

A virtual method in a host vtable (slot `0x108B7E4`, among `0xD7xxxx` host functions; no direct caller). Given a frame depth it calls `0xE534E0` to get the interpreted method and position at that depth, stores the position through its second argument, formats the method with `0xE72010(method, 1)` (`mono_method_full_name` [inferred]), copies the text into the caller's string (`0x4FF320`) and frees the temporary (`0x4E5920`). It returns 1 if a frame exists at that depth. This is the script call-stack reporter for error and exception logs [inferred]. P4 keeps it correct: the `0xE534E0` replacement reads the calling thread's TLS slot, so the sim thread sees its own frames.

### 12.8 S3SS overlap [verified against the docs/03 anchors]

| S3SS patch | Site | Touches a docs/30 site? |
|---|---|---|
| Chunky (skip `GC_try_to_collect`) | `call` at `0xD819AA` in `Simulate` | No. P3 detours the callee `0xE4A050`; with the call removed the hook just never runs from there. |
| GC Finalizer Throttle | `0xD81A1D`, `0xD81A37` in `Simulate` | No. It changes how often `0xE697F0` runs; the `0xE4B3F0` replacement is reached the same way. |
| `GC_stop_world` optimisation | `0xE511F5` (`cmp eax,0x100; jl` becomes `test eax,eax; jz`) | No hook there. With it on and a worker registered (`[0x11F4124] >= 1`), the loop bound becomes 255, so all 256 entries are scanned and unused ones skipped: suspension stays correct, slightly slower [verified from the bytes at `0xE511F0`-`0xE51203`]. |
| mimalloc | `MSVCR80` `malloc` family | No. The Boehm heap is separate. |

S3SS hooks nothing in the interpreter (docs/03), so P4 has no overlap either.

## 13. Step 1 status (P1 + P2, `native/ts3native/monothread.cpp`, ts3native 0.10.4)

Built and selftested; **not run in game**. `[MonoThread] Enabled=0` by default; Mode 0 only (no worker).

- **Correction to §2 [verified, Ghidra on `game-copy/ghidra-m1`]:** the static value of `[0x11949CC]` is `0xE4EF90`, not `0xD71820`: `0xE4EF90` is `jmp 0xE51350` (`E9 BB 23 00 00`) and `0xE51350` is `jmp 0xD71820` (`E9 CB 04 F2 FF`). The only reference to `[0x11949CC]` in the exe is the read at `0xE4D9DC`, so nothing rewrites it at run time. The install accepts `0xE4EF90` with both thunk bytes checked, or `0xD71820` directly, and calls whatever it found.
- **Install at load (DllMain, before any GC):** checks `[0x11949CC]` and the thunks, `0xD71820` (`83 EC 20 56 6A 1C`), the tail at `0xE4D9DC` (`A1 CC 49 19 01 85 C0 74 02 FF E0`), `GC_push_one` `0xE4D520` (`8B 44 24 04 3B 05 24 48 19 01`, cdecl `(p)`), `GC_push_all_stack` `0xE4CEE0` (`83 3D B4 49 19 01 00`, cdecl `(lo, hi)`, hi exclusive: the eager path scans `[lo rounded up, (hi & ~3) - 4]`), `GC_new_thread` `0xE51070` (16 bytes of §1), and the vtable slot `[0x108B6B0] == 0xD81840`. Any mismatch writes nothing. Then `[0x11949CC] -> MtPushOtherRoots` and the vtable slot -> a stub that runs `MtTick` with all registers and flags kept, then `0xD81840`.
- **P2 `MtPushOtherRoots`:** calls the old hook; returns if `[0x11F4120] != 1`; else for entries `0..min([0x11F4124],255)` with stack base `+0xC` != 0, `+0x10 == 1` and thread id != caller (the same tests `GC_stop_world`/`GC_start_world` use [verified]): `GetThreadContext` (`CONTEXT_INTEGER | CONTEXT_CONTROL`, static 16-byte aligned `CONTEXT`), `GC_push_one` for EAX, EBX, ECX, EDX, ESI, EDI, EBP, `GC_push_all_stack(Esp, base)`. On failure, or an ESP outside `(base - 64 MB, base)`, `mtCtxFails++` and it pushes the whole committed region `VirtualQuery(base - 4)` (guard page excluded). No allocation, `Log`, lock or CRT call.
- **P1:** `MtRegisterCurrentThread` calls `0xE51070` and checks the table. **There is no GC lock yet (P3), so it only registers the sim thread, and only from the first `MonoScriptHost_Simulate` tick** (outside every GC function; only the sim thread collects, docs/29 §4.5 measured 0 foreign allocations). Other threads get `false`. The first tick also logs the table (index, thread id, sim/main marks, base, suspended) and answers docs/29 §4.3.
- **Why Mode 0 is the game:** with only the collecting thread registered, the P2 loop finds no entry and the scan is the old hook alone. If the main thread is the registered `GC_init` thread and the sim thread is not, the first tick adds the sim thread (`mtSimRegistered = 1`); from then on the main thread, which `GC_stop_world` already suspends today, also gets its registers and stack pushed. That only adds conservative roots (objects can live longer, none are freed earlier). The cost is one compare per Simulate tick, plus one `GetThreadContext` and one stack scan per registered other thread per stopped mark.
- **Counters v18** (+0x2A4..0x2C0): `mtReady` (bit 0 root hook, bit 1 Simulate slot, bit 2 first tick done), `mtMode`, `mtSimTid`, `mtThreads`, `mtSimRegistered`, `mtPushCalls`, `mtStacksPushed`, `mtCtxFails`.
- **Selftest** (`TestMonoThread`): a fake table of 256 entries (the collector itself marked suspended, a real suspended thread, one entry not suspended, one freed), fake old hook, `GC_push_one` and `GC_push_all_stack`. It checks that the world running pushes nothing extra; that a stopped world pushes the 7 registers equal to `GetThreadContext` and `[Esp, StackBase)` holding a marker local of that thread; that a bad handle falls back to the page-aligned committed range that still covers the marker; that a max index of 1000 is clamped; and that registration from a non-sim thread is refused while the sim thread registers once and only once. It caught a real bug: `base - 64 MB` wrapped for stacks below 64 MB.
- **First in-game check (step 5 or earlier, Enabled=1 Mode=0):** the log shows the table at the first tick; `mtPushCalls` > 0 after a few collections; `mtStacksPushed` = 0 if the sim thread was the only one registered; `mtCtxFails` = 0.
- **Step 2 (P3) needs:** the gated lock on the 12 entries and the `0xE4B3F0` replacement (§3, §12.1); registration of non-sim threads inside that lock (lift the sim-only guard in `MtRegisterCurrentThread`); worker unregister under the lock (close the handle, zero `+0x0`/`+0xC`); flag changes in `MtTick`, which already runs at the top of every tick; counters `mtLockedAllocsK`, `mtLockWaitUs`, `mtWorkerGcs`, `mtFinalizerSkips` as the next free version.

## 14. M5 build: P3, P4 and the worker entry (`native/ts3native/monothread-m5.cpp`, 2026-10-20)

Built and selftested offline; **not run in game**. Everything is off by default and installs nothing unless `[MonoThread] Enabled=1` plus its own key. `TS3NATIVE_VERSION` is not changed on the branch (set at merge). Counters are a separate export, `ts3native_mtprobe` v1 (docs/registry.md), so the routing lanes' `Ts3nCounters` versions are untouched.

### 14.1 What is built

| Part | Switch | Sites | How |
|---|---|---|---|
| **P3** gated GC lock | `GcLock=1` (`LockAddRoots=0` leaves `GC_add_roots` out, docs/68 conflict 4) | the 17 detours of docs/120 §4 (`0xE4BE30`, `0xE4BDC0`, `0xE4BF90`, `0xE50DE0`, `0xE4BCE0`, `0xE4A050`, `0xE4A0B0`, `0xE4E450`, `0xE4AAC0`, `0xE4ABE0`, `0xE4AE30`, `0xE4A3F0`, `0xE4BEB0`, `0xD71900`, `0xD71A20`, `0xD71B70`, `0xE4D9F0`), `0xE4B3F0` replaced, `0xE4EC40` -> `ret`: **19 sites** | Each detour jumps to a generated 43-byte stub: gate `ts3native_mtprobe.locking` 0 -> `jmp [trampoline]` (one compare). Gate 1 -> `pushad`, `MtLockEnter(site, &return address)`: takes a recursive `CRITICAL_SECTION` (spin 4000), pushes the caller's return address on a shadow stack that the lock itself guards (only its owner is inside a locked hook) and puts `MtLockExitThunk` in its place; the original runs, returns into the thunk, which pops the real address, leaves the lock and returns there with EAX/EDX/ECX/flags kept. This works for every calling convention (`ret N` already dropped the arguments) and for the nesting `0xD71900 -> 0xD718A0 -> GC_malloc` (depth 64, past it `lockOverflow` counts and the call runs unlocked). All 17 prologues are checked before anything is written, and every trampoline and stub exists before the first `jmp` goes in. `0xE4B3F0` is a C copy of the game's loop [verified bytes]: per-call limit `[0x1194848]`, pop one entry under the lock, run `fn(base, data)` outside it, `GC_mem_freed` delta into `[0x12225D8]`; a naked wrapper keeps ECX as the original does. `0xE4EC40` is checked (`83 EC 20 53`) and set to `C3`. |
| **P4** frame globals to TLS | `FrameTls=1` | blocks `0xE53EBD` (37 bytes), `0xE597BA` (11), `0xE59866` (12); `0xE534E0` replaced | Two `TlsAlloc` slots (both < 64, else nothing is written). Each block is rebuilt in a stub with the same instructions; the four instructions that name `[0x11F4170]`/`[0x11F416C]` (always the last 4 bytes) get an `fs:` prefix and `0xE10 + 4*slot`, then `jmp` back. The block is overwritten with `jmp stub` + NOPs. At install the sim thread's slots get the current global values. `0xE534E0` -> `MtFrameAt` (same logic on the calling thread's slots; the only caller, `0xD71CC0`, tests the method out-value, not EAX [verified]). |
| **Worker entry** | `Mode=1` (needs `GcLock=1` and `FrameTls=1`; `Mode=2` runs as 1) | calls `0xE5A600`, `0xE75260`, `0xE75270`, `0xE51070` | At a tick where the runtime switch `mtMode` >= 1 and P3 + P4 are fully in, the sim thread reads the domain (`0xE75260`), sets the gate, and starts a 256 KB thread (created suspended so the lock hooks know its id first). The worker registers with `GC_new_thread` under the lock (P1, the sim-only guard is lifted while the gate is on), sets the domain TLS (`0xE75270`), checks that the interpreter context TLS `[0x11F4160]` is NULL (`workerCtxSet` tripwire), then waits for jobs. A job (`MtSubmit(method, obj, params)`, sim thread; arguments rooted by the caller) runs through `0xE5A600` directly; a managed exception is counted and dropped; with `[0x11DC18C] != 0` the job is refused (`workerTrackSkips`, docs/120 §2.2). `mtMode` -> 0: the worker unregisters under the lock (handle closed, `+0x0`/`+0xC` zeroed), exits, and only then the gate goes off; a worker that does not stop within 1 s keeps the gate on (`workerStopFails`). **No managed job exists yet**: `Mode=1` runs an idle registered worker; the ts3fixes `MtProbe` class and the `Verify`/stress parts of §7 are the next step. |

Install time: P3 and P4 go in at the first `MonoScriptHost_Simulate` tick, after P1 (sim thread, outside every GC and interpreter function; only the sim thread allocates, docs/29 §4.5). `MtTick` now runs every tick (one compare when no worker is wanted).

### 14.2 Offline proof

- **Bytes:** every expected byte string in `monothread-m5.cpp` (17 prologues, 3 blocks, `0xE4B3F0`, `0xE4EC40`, `0xE534E0`) matches the Steam 1.67 `game-copy/bin/TS3W.exe` (scratch check, 23 of 23) [verified].
- **Selftest `TestMonoThreadM5`** (`monothread-test.h`, runs in `selftest.exe`): (1) P3 on three fake GC entries (cdecl alloc, cdecl free, stdcall grow that calls both through their hooks): one bad prologue writes nothing; gate 0 runs the originals with no lock; gate 1 keeps return values, reaches depth 2, leaves depth 0; two threads x 20,000 alloc/free on a free list with a race gap: 0 nodes handed out twice, 64 of 64 back (about 30-40k contended entries). Control with the gate off on the same list: about 6,000 double hand-outs and a corrupt list, so the test does see the race. (2) `0xE4B3F0` copy on a fake queue: limit 2 then 0, order, `fn(base, data)`, next and data cleared, bytes freed meanwhile added, ECX kept. (3) P4: the three game blocks copied byte for byte, patched and run on fake frames: entry saves the old pair (frame to `[ebp-0x60]`, array in ECX) and sets frame `[ebp+8]` / array `ebp-0x4C74`; exits A and B restore from EAX/ECX and EDX/ECX; a second thread starts with NULL slots and keeps its own values; `MtFrameAt` method and position, depth past the top, empty cell, NULL slots. (4) Worker against the fake GC table: no start without P3/P4 or with `mtMode` 0; start, registration in the table under the lock, domain TLS seen inside the invoke, a job that allocates 2 x 20,000 through the hooks while the sim thread does the same (0 duplicates, list intact), busy refusal, managed exception, tracking-mode refusal, stop (entry zeroed, gate off), restart and stop again.
- **Conflicts:** `node tools/hook-sites.ts --check`: 476 addresses, 0 missing from docs/68 (rows updated: P3/P4 built, worker calls, conflict 4 note).
- **Not done:** a run inside `native/monohost` (two threads in the real interpreter with a forced collection, docs/120 §6 d). The host is being extended for M1.2-M1.3 by another lane; a harness there needs the real `GC_new_thread`/stop-the-world on a mapped image and is the next offline step.

**Update 2026-10-20 (docs/120 §9):** two more keys, both default 1 and active only with `GcLock=1`: `ArenaFix` (EA frame arena pool: clear `+0x13C` after release-all `0xD71B70`, dedupe the pool at install, drop in-use chunks before a grow `0xD71900`; the cause of the `0xE5444D` fault) and `N2mPrivate` (F1: worker-private call records at `0xD8A8E0`). Counters `ts3native_mtprobe` v2. monohost now runs both threads interpreting at once in the default selftest (33/33).

### 14.3 Risks (in-game checks in docs/51 §148)

1. **Return-address swap:** a `longjmp` or SEH unwind out of a locked GC function would leave the lock held and the shadow stack off by one. The 17 entries call no managed code except the `GC_apply_to_each_object` callback (S3MM) and the arena functions' GC calls [inferred]; `lockOverflow`/`lockMaxDepth` show a leak (I-120-8). **Update 2026-10-20 (docs/120 §10):** 10 entries can be left by the out-of-memory `longjmp` (`GC_oom_fn` -> `0xE53740`) [verified static]. Fixed in ts3native 0.36.4: each shadow entry keeps its return-slot address and the next hooked enter/exit on that thread drops entries below the current stack, releasing their lock recursion (`lockStale`, `ts3native_mtprobe` v3); selftested offline (docs/120 §10.4). ts3native 0.36.5 adds the `GC_oom_fn` wrapper (with `GcLock=1`): the OOM thread releases its entries before Mono raises, so the other thread gets the lock at once (`lockOomReleased`, v4; docs/120 §10.5).
2. **Install at the first tick with other threads running:** the `jmp`s are written non-atomically; safe only if no other thread is inside these functions then (docs/29 §4.5 measured 0 foreign allocations; P4 blocks run only on the sim thread) [measured for allocations, inferred for the interpreter].
3. **Gate cost with no worker** (two jumps and a compare per allocation and per arena chunk crossing) and P4's two extra jumps per interpreted call: to measure, GcLock/FrameTls on vs off (I-120-7, I-120-10).
4. **S3PP LocalizedStringCache** if it scans for `GC_add_roots` after the first tick (I-120-9): `LockAddRoots=0` avoids it.
5. The worker rules of §5 and docs/120 §3.3 (no GC handles, no managed locking, warm-up on the sim thread) are not enforced by code; the first job must follow them.

## 15. GC lock entry cost and the lazy gate (2026-10-29, ts3native 0.36.109, mtprobe v5, docs/51 §380)

**Uncontended entry cost [measured, selftest].** `TestMonoThreadM5` times 2,000,000 alloc + free pairs through two P3 stubs on one thread, gate on vs gate off, best of 3, with a worker id set so `MtLockEnter` reads the thread id as in game. Three runs on the dev machine (i7-11800H, Windows 11, a game queue running at the same time): **+68.4, +54.9, +69.8 ns per locked entry** (gate on 94-111 ns per hooked call, off ~40 ns). The extra cost is the stub's `pushad`/`pushfd`, `TryEnterCriticalSection` + `LeaveCriticalSection`, the shadow-stack push and the return through `MtLockExitThunk` (a swapped return address defeats the CPU's return-stack predictor). Fakes are hot in cache; in game the entries run between interpreter work, so the per-entry cost there is [inferred] the same order, not measured.

**What that means in game [inferred from measured counts].** mono-cost4 `ip-autshadow-r` counted 341.7 M locked entries over its 10 min play window (mtprobe `lockedCalls` delta), about 570k entries/s with the gate on. At ~68 ns that is **~39 ms/s of sim-thread time** (55-70 ns: 31-40 ms/s; at 580k/s: 32-41 ms/s), far above the PLAN.md stop rule (5 ms/s) and about 10x the measured contended waits (`lockWaitUs` 34 us in the same run). So with a worker present, the gate itself is the cost, not contention. `tools/harness/mono-cost-report.ts` now adds `entries x 68 ns` to the stop-rule line, labelled [inferred].

**Lazy gate (`[MonoThread] LazyGate=1`, default 0).** The gate (`ts3native_mtprobe.locking`) is on only while the worker runs a job:

- **On:** `MtSubmit`, on the sim thread, sets the gate before `SetEvent` hands the job over. Its callers run at a Simulate tick boundary outside every GC and interpreter function (`[AutShadow]`'s post-pump / loop-back dispatch; monohost's driver), so no unlocked GC frame of the sim is open when the hooks start locking, and every later GC/alloc entry of the sim takes the lock (same thread, program order). `SetEvent` is a full barrier: the worker can only start the job after the store, so every entry the worker makes in the job sees the gate on.
- **Off:** `MtLazyOff` from `MtM5Tick` (top of the next Simulate iteration, the same tick boundary that starts the worker), only when `workerState == 2` and `g_mtBusy == 0`. The worker clears `g_mtBusy` after `0xE5A600` returned, i.e. after every locked hook frame of the job left through its thunk and released the lock (or was dropped by the stale-entry rule, §10 I-120-8); between jobs it only waits on its event and makes no GC entry. The next dispatch turns the gate on again before the worker can run.
- **GC while off:** a sim collection still suspends and scans the idle worker (P2, `MtPushOtherRoots`); its stack holds no live job references (job arguments are rooted statics, docs/120 §1.2).
- **Start and stop:** `MtStartWorker` turns the gate on as before (registration under the lock); `MtStopWorker` turns it on before the stop request so the worker unregisters under the lock, then off. The first tick after the worker is idle turns it off.
- **Counters (mtprobe v5):** `lazyGate` (switch), `lazyOn` (dispatches that turned it on), `lazyOff` (ticks that turned it off). Expected in game: `lazyOn` = `lazyOff` (+-1) = jobs; `lockedCalls` only inside job windows.
- **Race argument in one line:** both transitions are made by the sim thread at a point where the sim has no GC frame open, the on-transition happens-before the worker's first job instruction (`SetEvent`), and the off-transition happens-after the worker's last GC exit (`g_mtBusy` written last by an interlocked exchange), so no GC entry on either thread can run unlocked while the other thread may be inside the GC.
- **Selftest:** `TestMonoThreadM5` (gate off while idle and the sim's entries unlocked, on at dispatch with sim + worker hammering the fake allocator without a duplicate or lost node, off at the next tick, stop and restart with the switch on). Not run in game; queue `tools/harness/queues/mono-cost5.json` jobs `bp-mt-idle-worker` (LazyGate off) and `bp-mt-idle-worker-lazy` (LazyGate=1) give the in-game difference.

## 16. Satb act and the lazy gate: early off, the sim's own waits (2026-10-30, ts3native 0.36.141, mtprobe v6, not run)

satb-act on satb5 (bp, 0.36.140) [measured, `probe-out/queue/mono-loop-report.txt`]: the stop-rule line read 8.1 ms/s, of which GC lock waits 2.4 ms/s (143,712 waits, max 3,949 us) and uncontended entries 4.1 [inferred: 19.1 M entries x 68 ns, worker 1.95 M of them]. Two problems with that line and one with the gate:

1. **The waits were every thread's.** `lockWaits` counts the worker too; `workerCollects` was 0 (no collection on the worker), so the 3.9 ms maximum is most likely the worker waiting while the sim collected [inferred]. mtprobe **v6** adds `simLockWaits`, `simLockWaitUs`, `simLockMaxWaitUs` (any thread that is not the worker); `mono-cost-report.ts` now counts only those, and only the sim's entries (`lockedCalls - lockedWorker`) in the stop rule (v5 runs keep the conservative all-thread numbers).
2. **The gate stayed on for a whole tick.** With `[AutShadow] Satb=2` the job is submitted at the end of frame t and ends somewhere in tick t+1, but `MtLazyOff` runs at the top of t+2, so every GC and arena entry of the sim for the rest of t+1 took the lock (~17.2 M sim entries in 530 s).
3. **`[MonoThread] LazyEarly=1`** (with LazyGate=1, default 0): in `MtLockEnter`, on the sim, after taking the lock: no locked frame of the sim open (`g_mtDepth` 0: the entry is not nested in a hooked GC frame), the worker idle (`workerState` 2, `g_mtBusy` 0, no stop request): the gate goes off, the lock is released and this entry runs unlocked; counted in `lazyEarlyOff`. Race argument as §15: the worker cleared `g_mtBusy` after its last locked frame left, it starts nothing until the sim's next `MtSubmit`, and the sim holds no locked frame, so no entry of either thread is inside the GC unlocked while the other is locked [inferred; selftest].
4. **Selftest** `TestMonoThreadM5`: v6 layout; LazyEarly x2: sim and worker hammer the fake allocator while the job runs (locked, no duplicate or lost node), then the first sim entry after the job (or the hammer's last ones) turns the gate off with no locked call counted.

Not done (design only): a worker-private allocator (thread-local free lists, `GC_malloc_many`-style batches). docs/57: the four allocation entries pop Boehm's global free lists unlocked; a private list would have to be a GC root node by node (a collection clears the global lists and would hand the same objects out again), and typed objects (`GC_malloc_explicitly_typed`, most managed objects with references) keep their descriptor in the object, so a free typed object cannot be marked safely. First read the v6 split: if the sim's own waits are small, the cost is the gate (item 3), not contention.
