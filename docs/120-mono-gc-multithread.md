# 120. Second Mono thread (M5): what the GC and runtime need, verified statically

Static check of what a second OS thread running managed code (Mono plan M5, docs/118; offline host M1, docs/119) needs from the Boehm GC and the embedded Mono 1.2.3 runtime. It takes the design assumptions of docs/30 (`[MonoThread]` P1-P4 and the worker entry, §5) and the maps in docs/29, docs/56, docs/57 and docs/58, re-checks the ones the design depends on, and adds what they missed. Ghidra headless on a copy project (`game-copy/ghidra-m120`, copied from `ghidra-monob`, symbols already imported), scratch scripts (import census, string scan, instruction dumps, reference lists), plus a PE parse of `game-copy/bin/TS3W.exe` for the TLS directory and the internal-call tables. Steam `TS3W.exe` 1.67, image base `0x400000`. The game was not run. No decompiled code is kept here.

Tags: **[verified]** = read in the disassembly, the reference manager, the PE file or the decompiled class library for this note; **[verified, docs/N]** = verified there and not re-read; **[community-reported]** = stock Boehm 7.x / Mono 1.2 behaviour, not checked in this exe; **[inferred]** = reasoning, with the check that would settle it.

## Summary

- **Collector:** Boehm GC 7.x (7.1 by the code shape, docs/57), Win32 build with the thread table and stop-the-world kept, `LOCK()` compiled out, incremental mode on `GetWriteWatch`, no parallel marker, finalizers on demand. No version string is in the exe; the env-var set in `GC_init` (`GC_PAUSE_TIME_TARGET`, `GC_ENABLE_INCREMENTAL`, `GC_LARGE_ALLOC_WARN_INTERVAL`, no `GC_NPROCS`/`GC_MARKERS`) fits 7.0/7.1 without `PARALLEL_MARK` [verified strings, version inferred].
- **Roots (verified, three sources only):** (1) static data = every committed, writable (`PAGE_READWRITE` or `PAGE_EXECUTE_READWRITE`) **`MEM_IMAGE`** region of every loaded module, re-registered at the start of each root push (`GC_register_dynamic_libraries` `0xE4A8E0` from `GC_push_roots` `0xE4D91E`); (2) the collector's own seven registers (`0xE4BA10`) and its own stack (`0xD71820` through the hook `[0x11949CC]`); (3) the finalizer and disappearing-link tables, the free-list mark bits and the explicit roots of `GC_add_roots`. **`MEM_PRIVATE` memory is never a root**, so another thread's stack is not scanned by accident: docs/29's "GC scans one stack" holds. A thread table exists (`GC_new_thread` `0xE51070`) but has one caller, `GC_thr_init`; no `GC_register_my_thread`, `DllMain` attach or `mono_thread_attach` [verified].
- **Stop-the-world is real** (`SuspendThread`/`ResumeThread` on every registered thread except the caller, under `GC_write_cs` `0x1222580`), and the duplicated thread handle has full access (`DUPLICATE_SAME_ACCESS`), so P2's `GetThreadContext` on it works [verified]. **The allocation lock is a no-op** (`GC_allocate_ml` `0x1234320` initialised, never entered) [verified, docs/57].
- **Interpreter per-thread state:** the two frame globals `[0x11F4170]`/`[0x11F416C]` are still the only interpreter globals (5 references each, the docs/30 P4 table) [re-verified]. Everything else is already per thread: six Mono TLS keys, the managed exception path (`setjmp3` in `0xE5A3CE`, `longjmp` from `0xE537B2` and `0xE5A52C` through the thread's `ThreadContext` in TLS `[0x11F4160]`), and no `__declspec(thread)` data in use (`.tls` holds 8 zero bytes; its index variable `[0x1220AD0]` has no code reference) [verified].
- **Two new findings that change the design:**
  1. **`GC_add_current_malloc_heap` `0xE4EC40` calls CRT `malloc`/`free` from inside the root push**, once per GC number, and the root push runs with the world stopped (docs/30 §12.6). With a second registered thread suspended while it holds the CRT heap lock, the collector deadlocks. Its only caller is `GC_is_heap_base` `0xE4EDA0`, whose answer cannot change the outcome here (only `MEM_IMAGE` regions pass the filter, and a malloc heap is never an image) [verified filter order; dead-filter claim inferred]. **Fix: patch `0xE4EC40` to `ret`** (one byte, P3 install). **Update 2026-10-20 (§2.2.1):** the function never runs on Windows NT in this build (both `GC_is_heap_base` callers are switched off), so the hazard is not real and the patch is a no-op guard [verified static; I-120-2 refuted, I-120-3 verified].
  2. **`Monitor` is a no-op in this build** [verified]: the `System.Threading.Monitor` internal calls point at three EA stubs (`ret`, `mov eax,1; ret`, `xor eax,eax; ret`), and EA's `mscorlib` `Monitor.Enter`/`Exit` only check for null. So C# `lock` gives **no** exclusion on any thread, and `Thread.Start`'s `Thread_internal` is a `return 0` stub. Managed code cannot synchronise the worker with the sim thread; the hand-off must be native (docs/30 §7 already is) or `Interlocked.*`, which is real.
- **Open point resolved (docs/57):** the EA memory tracker `0x9336F0` that heap growth may call takes an EA lock (`Lock_Acquire` `0x4E16F0`). It is called only when the tracking mode `[0x11DC18C]` (set by the script IC `GameUtils.SetMemoryTrackingMode` `0xEC1520`) is non-zero, so the rule is: worker off unless the mode is 0.
- **docs/30 verdicts:** P1 holds; P2 holds; P3 needs change (17 lock detours confirmed, plus the `0xE4B3F0` replacement, **plus the `0xE4EC40` patch**: 19 sites); P4 holds; worker entry §5 holds with two new rules (no managed locking, tracking mode 0). **No blocker found.** Still open and needed before a live run: which thread is in Boehm's table today (docs/29 §4.3; one load with the built v18 counters answers it).
- **Go/no-go:** **go** for the first offline proof (docs/119 host, then docs/30 Mode=1/2 in an emulated or offline harness) and for building P3/P4. About 9-14 h of agent build and selftest, then 1 load for the registration check and 3 clean-night runs for Mode 0/1/2 (§6).

## 1. The collector and how it finds roots

### 1.1 Build and configuration [verified unless tagged]

| Item | Finding |
|---|---|
| Family | Boehm GC, Win32 (`os_dep.c`/`win32_threads.c` code and strings: `"Weird VirtualQuery result"`, `"Failed to find stack base in GC_new_thread"`, `"too many threads"`, `"GC_stop_world() called before GC_thr_init()"`) |
| Version | 7.x; no version string. `GC_init` reads `GC_PRINT_STATS`, `GC_DUMP_REGULARLY`, `GC_FIND_LEAK`, `GC_ALL_INTERIOR_POINTERS`, `GC_DONT_GC`, `GC_PRINT_BACK_HEIGHT`, `GC_NO_BLACKLIST_WARNING`, `GC_PAUSE_TIME_TARGET`, `GC_LARGE_ALLOC_WARN_INTERVAL`, `GC_INITIAL_HEAP_SIZE`, `GC_MAXIMUM_HEAP_SIZE`, `GC_ENABLE_INCREMENTAL` (strings `0x108DCB4`-`0x108DEFC`). 7.1 [inferred from this set and the code shape, as docs/57] |
| Threads | `GC_WIN32_THREADS`-style explicit table (256 x 0x14 at `0x1221000`), not `GC_DLL`/`DllMain` registration; only `GC_thr_init` `0xE513D0` registers, and it registers the `GC_init` thread [verified, docs/29 §3.1, re-read: `GC_new_thread` has one caller] |
| Parallel mark / thread-local alloc | absent: no marker threads (the exe's `CreateThread`/`_beginthreadex` callers are EA and CRT code outside the Boehm block), no per-thread free lists [verified import census] |
| Allocation lock | compiled out; `GC_allocate_ml` never entered [verified, docs/57 §2] |
| Incremental | `GetWriteWatch` dirty bits (`GC_gww_dirty_init` `0xE4EFB0` is the one `GetWriteWatch` caller); no write barrier [verified here and docs/57 §6] |
| Finalizers | on demand: `GC_finalize_on_demand` = 1, notifier NULL; run only from `mono_gc_invoke_finalizers` `0xE697F0` on the sim thread [verified, docs/30 §12.3] |
| `GC_no_win32_dlls` `[0x11F40E4]` | set only by `GC_check_win32` `0xE4EB70` (two `or` sites); read by `GC_init`, `GC_enable_incremental` and the two root-registration functions below. 0 on NT [inferred from Boehm: it is set for Win32s/Win9x], so the dynamic-library path runs (to confirm live: I-120-5) |

### 1.2 Root sources [verified]

`GC_push_roots` `0xE4D910` (docs/57 §4) does, in order:

1. **`GC_register_dynamic_libraries` `0xE4A8E0`** (call at `0xE4D91E`). When `[0x11F40E4]` is 0 it walks the address space from `[0x1222548]` to `[0x122254C]` with `VirtualQuery` and registers a region when `State == MEM_COMMIT` (`0x1000`), `Protect` is `PAGE_EXECUTE_READWRITE` (`0x40`) or `PAGE_READWRITE` (`0x4`), `GC_is_heap_base` `0xE4EDA0` says no, and **`Type == MEM_IMAGE` (`0x1000000`, compare at `0xE4A956`)**. Registration goes through `GC_cond_add_roots` `0xE4A870`, which cuts out registered thread stacks (`GC_get_next_stack` `0xE51360`) before `GC_add_roots_inner` `0xE4D5F0`. The other branch, `GC_register_root_section` `0xE4EE10` (only when `[0x11F40E4]` is set, from `GC_init` via `0xE4EEE0`), registers the exe image's writable sections and accepts `PAGE_WRITECOPY` variants too. Either way the static roots are **writable image sections only**: the exe's `.data`/`.tls`, every DLL's data, and every ASI's data (ts3native included).
2. Free-list mark bits, finalizer and disappearing-link tables (`0xE4A9A0`), explicit roots (`GC_add_roots`: Mono domain, `mono_gc_init`, the interpreter runtime-method hash; docs/57 §3.3). Host roots: `GC_malloc_uncollectable` blocks (call records, arena list nodes; docs/57 §1) are roots by kind.
3. The collector's registers: `0xE4BA10` pushes EAX, EBX, ECX, EDX, EBP, ESI, EDI with `GC_push_one` `0xE4D520` (only caller `GC_push_roots`).
4. The tail jump through `[0x11949CC]` -> `0xE4EF90` -> `0xE51350` -> `0xD71820`: the collector's own stack only (docs/30 §13).

Consequences for M5:

- **No accidental scan of another stack** [verified]: thread stacks are `MEM_PRIVATE`, so step 1 never adds them. Without P2, a worker's stack and registers are invisible to a sim-thread collection, as docs/29 said.
- **Where a hand-off may keep managed references** [inferred from the filter]: in a managed static (docs/30 §7 does this), in a ts3native global (`.data` is `MEM_IMAGE` and writable, so it is a root once the dynamic-library pass runs), on a registered thread's stack (P2), or in a `GC_malloc_uncollectable` block. **Not** in `malloc`/`new`/`VirtualAlloc` memory or a TLS slot (TLS is not a root, docs/57 §4).
- Root registration happens again at each root push, including stopped marks, which is where finding 1 of the summary comes from (§2.2).

## 2. Stop-the-world, suspension and what the collector calls while threads are stopped

### 2.1 Suspension [verified]

- `GC_stop_world` `0xE511A0` (only caller `GC_stopped_mark` `0xE49980`): `EnterCriticalSection(GC_write_cs)`, then for every in-use entry whose thread id differs from `GetCurrentThreadId()`: `GetExitCodeThread` (dead thread: free the entry) or `SuspendThread` (`[0xF95154]`, only caller `0xE511E1`) and `+0x10 = 1`. `GC_start_world` `0xE51290` resumes with `ResumeThread` (`[0xF95150]`, `0xE512AF`). Both imports have no other caller in the exe.
- `GC_new_thread` duplicates `GetCurrentThread()` with `DuplicateHandle(..., access 0, inherit 0, DUPLICATE_SAME_ACCESS)` (`push 2` at `0xE510F3`, call `0xE51111`), so the table handle carries the thread's full access, `THREAD_GET_CONTEXT` and `THREAD_SUSPEND_RESUME` included. P2's `GetThreadContext` on it is legal. The exe does not import `GetThreadContext`; ts3native imports it (built in step 1).
- So docs/29's "assumes single-threaded" is wrong for suspension and right for everything else: the collector stops registered threads but scans only its own stack and takes no allocation lock.

### 2.2 Calls made while the world is stopped [verified sites; deadlock reading inferred]

A stopped mark runs root pushes (docs/30 §12.6) and may grow the mark stack. While it runs, every other registered thread is suspended wherever it was. Any lock the collector takes that a suspended thread may hold is a deadlock. Calls found on these paths:

| Call | Site | Lock | When | M5 action |
|---|---|---|---|---|
| CRT `malloc(8)`, `malloc(10000..500000)` loop, `free` | `GC_add_current_malloc_heap` `0xE4EC40` (`0xE4EC47`, `0xE4ECB1`, `0xE4ECE9`, `0xE4ED1D`, `0xE4ED4F`), from `GC_is_heap_base` `0xE4EDA0` (`0xE4EDAD`, only caller), from `0xE4A8E0`/`0xE4EE10` | `MSVCR80` heap lock (or the allocator S3SS/mimalloc substitutes) | the first root push of each GC number (`[0x11949C8] != GC_gc_no`) | **patch `0xE4EC40` to `ret`** (§4 P3) |
| `GC_win32_get_mem` `0xD70900` -> EA tracker `0x9336F0` (`Lock_Acquire` `0x4E16F0` at `0x93376F`, `Lock_Release` at `0x933A47`) | `0xD70928`-`0xD7097D`, only when `[0x11DC18C] != 0` and `0x931080` agrees | EA tracker lock, also taken by `IndexBuffer_CreateD3D` `0x616C40` (render) | heap growth, including mark-stack growth `0xE4C950` from `GC_mark_some` (`0xE4D3A3`, `0xE4D3FF`) | **rule: the worker runs only while `[0x11DC18C] == 0`.** Its only writer is `IC_GameUtils_SetMemoryTrackingMode` `0xEC152E` (a debug IC) [verified] |
| `VirtualAlloc`, `VirtualQuery`, `GetWriteWatch`/`ResetWriteWatch` | heap growth, root registration, dirty bits | none in user mode [community-reported] | each cycle | none |
| Warn proc `[0x11949C0]` (set by Mono init via `GC_set_warn_proc` `0xE4DFC0`) | `GC_finalize` `0xE4B021` ("Finalization cycle"), `GC_collect_or_expand` (out of memory), large-block warnings | Mono logging, may allocate through the EA arena `0x4E69D0` [inferred] | rare error paths | accept; count in Verify |
| `GC_write_cs` | `GC_stop_world`, `GC_write` `0xE4DD50` | Boehm's own | log output with `GC_PRINT_STATS` only | none |

`0xE4EC40` is the one that matters: it runs every collection, and the CRT heap lock is the lock a suspended game thread is most likely to hold. Why `ret` is safe: `GC_is_heap_base` exists to keep malloc heaps out of the root set, but EA's dynamic-library filter only accepts `MEM_IMAGE` regions, and `GC_register_root_section` only walks the exe image, so a "yes, malloc heap" answer can never change which region is registered [verified 2026-10-20, §2.2.1; was inferred, I-120-3]. The list it maintains, `[0x11F40E8]`, is used by `0xE4EC10`, `0xE4EC40` and `0xE4EDA0` (raw byte scan of `.text` for the address; one more match at `0xA241AB` is outside the GC block and was not decoded).

Live check of the `[0x11DC18C]` rule (I-120-4): queue `mono-ledger-probe.json` reads the word at every `MonoScriptHost_Simulate` entry (`[AutShadow] Ledger=1`, autledger.cpp; counters `lgTicks`, `lgTrackTicks`, `lgTrackLast` in `ts3native_autshadow` v3) and `mono-ledger-report.ts` gives the verdict; built 2026-10-20, not run yet.

Today's game has the same hazard if the main thread is the registered `GC_init` thread (docs/29 §4.3): every sim-thread stopped mark would then suspend the main thread and may `malloc` while it holds the heap lock. That this does not visibly freeze the game hints that only one thread is registered today, or that the timing is rare [inferred; I-120-1 settles which thread is registered].

#### 2.2.1 Re-read of the filter and of the callers (2026-10-20, static)

Source: raw disassembly of the Steam 1.67 exe (capstone over the PE file), no Ghidra project needed.

| Step | Site | What it does | Tag |
|---|---|---|---|
| Filter order in `GC_register_dynamic_libraries` `0xE4A8E0` | `0xE4A926` state = `MEM_COMMIT`; `0xE4A93B`/`0xE4A940` protect = `PAGE_EXECUTE_READWRITE` or `PAGE_READWRITE`; `0xE4A94A` `GC_is_heap_base(AllocationBase)` != 0 rejects; `0xE4A956` type = `MEM_IMAGE` | the heap-base test sits before the image test, and a CRT heap is `MEM_PRIVATE`, so a region it would reject is rejected by the type test anyway | verified |
| What `GC_is_heap_base` `0xE4EDA0` answers without the malloc list | `0xE4EDEB`-`0xE4EE05`: the collector's own heap sections (`0x122EF14`, count `[0x11F40EC]`) | the `ret` only empties the malloc list `[0x11F40E8]`; collector sections still answer yes | verified |
| Caller 1 `0xE4A8E0` | called only from `GC_push_roots` `0xE4D91E`, skipped while `GC_no_dls` `[0x11949A8]` != 0 (`0xE4D915`); `mono_gc_base_init` sets it to 1 at `0xE69598` (I-119-37) | does not run | verified |
| Caller 2 `0xE4EE10` (registers every committed RW region that is not a heap base, no type test) | called only from `0xE4EEE0` (`GC_init`, `0xE4E2D7`); returns at once unless `GC_no_win32_dlls` `[0x11F40E4]` != 0. Its one writer, `0xE4EB70`, sets it only when `GetVersion` has the high bit set and major version <= 3 (Win32s) | does not run on NT | verified |

So on Windows 10/11 `GC_add_current_malloc_heap` is never called: the stopped-world `malloc` hazard of this section does not exist, and the P3 `ret` is a guard against a future `GC_no_dls = 0`, not a fix. The `ret` stays (one byte, already built). I-120-2 refuted, I-120-3 verified.

## 3. Interpreter and runtime state for a second thread

### 3.1 Per-thread already [verified]

| State | Mechanism | Evidence |
|---|---|---|
| Interpreter `ThreadContext` | TLS `[0x11F4160]` (`mono_interp_init` `0xE5A6A3`) | get/set sites in docs/56 §1; `0xE5A600` keeps it on the stack and clears the slot on exit (docs/57 §4) |
| Managed exceptions | the throw path `0xE53740` reads the context from TLS `[0x11F4160]` (`0xE53748`), stores the exception through `ctx+0xC` and `longjmp`s to the buffer at `ctx+0x10` (`0xE537B2`); `interp_mono_invoke_with_state` sets that buffer with `setjmp3` (`0xE5A3CE`) and has its own `longjmp` at `0xE5A52C` | so an exception on the worker unwinds the worker's own stack only; `longjmp` has no other Mono caller |
| Current domain, loader error, marshal type-info marker, P/Invoke last error, current `MonoThread` (always NULL) | TLS keys `[0x11959DC]`, `[0x1220F38]`, `[0x11F43C4]`, `[0x11F43E0]`, `[0x1195B7C]` | `TlsAlloc` callers `0xE5A680`, `0xE75BF0`, `0xE84880`, `0xE99CB0` (two keys), `0xEBB180`: six keys, matches docs/56 §1 |
| SEH | `fs:[0]` frames in the mark code | per thread by definition (docs/56) |
| Static TLS (`__declspec(thread)`) | `.tls` section 8 bytes, all zero; TLS directory index variable `[0x1220AD0]` has no reference in code | unused, nothing to move [verified PE + references] |

### 3.2 Global, must become per thread [verified]

Only P4's two globals. References re-read: `[0x11F4170]` at `0xE53EBD` (read), `0xE53ED7`, `0xE597BA`, `0xE5986C` (writes), `0xE534EA` (read in `0xE534E0`); `[0x11F416C]` at `0xE53EC2` (read), `0xE53EDD`, `0xE597BF`, `0xE59866` (writes), `0xE534E0` (read). Same nine sites as the docs/30 §4 table, nothing new. docs/56's other per-call globals (`host+0xBC8/+0xBD0/+0xBD4`, the call-record list `[0x11EE5A4]`, the IC nesting counter `[0x11F2D84]`) are host state that a worker avoids by entering only through `0xE5A600` and calling no EA internal call (docs/30 §5, docs/56 §2); they are rules, not patches.

### 3.3 Shared runtime state a worker would race

| Area | State in this build | M5 treatment |
|---|---|---|
| Class, method, name, generic, array caches; vtables; class init; transform; interned strings (`ldstr_section` `0x11F45A0`; `String.Intern`/`IsInterned` icalls `0xEB93B0`/`0xEB93C0`) | locked (loader lock `0x11F43A8`, per-domain lock, `0x11F4574`, `0x11F4174`) [verified, docs/58] | warm-up on the sim thread (docs/30 §5 step 6); the worker then reads published pointers |
| GCHandle table `0x11959E0`; thunk hash `0x11F4128`; reflection hash `0x11F46F0` | no lock [verified, docs/58] | rules: no GCHandle/weak refs, warm every internal call once, no reflection |
| Native-call trampolines (no JIT) | `tramp_create_thunk` `0xE51400` fills `[0x11F4128]` on the first call of a signature and caches it at `RuntimeMethod+0x10` [verified, docs/58] | warm-up |
| **`Monitor` (C# `lock`, `Monitor.Wait/Pulse`)** | **no-op.** Internal-call table: `Monitor_exit`, `Monitor_pulse`, `Monitor_pulse_all` -> `0xC0FD60` (`ret`); `Monitor_test_owner`, `Monitor_wait` -> `0x619EF0` (`mov eax,1; ret`); `Monitor_test_synchronised`, `Monitor_try_enter` -> `0x5BFB90` (`xor eax,eax; ret`). EA's `mscorlib` `Monitor.Enter`/`Exit` bodies are a null check only [verified: PE tables, anchored on `SuppressFinalize` -> `0xE786F0` (docs/57) and cross-checked on `Interlocked.Increment(int&)` -> `0xE66E10`, an `InterlockedIncrement` caller; decompiled `mscorlib` `System.Threading/Monitor.cs`] | **new rule:** no managed synchronisation between threads. Everything the worker shares is handed over natively (docs/30 §7 already does) or with `Interlocked.*` |
| `Interlocked.*` | real: `CompareExchange` `0xEBA2B0` (calls `InterlockedCompareExchange`), `Exchange` `0xEBA1F0`, `Increment(int&)` `0xE66E10`, `Decrement(int&)` `0xF4F560` (`InterlockedDecrement`), 64-bit variants `0xEBA340`/`0xEBA190`/`0xEBA1C0` [verified table; bodies of the 64-bit ones not read] | allowed on the worker |
| Managed threads | `Thread_internal` (thread start) -> `0x5BFB90` (`return 0`); `CurrentThread_internal` -> `0xEB9F40` (`TlsGetValue([0x1195B7C])`, NULL) [verified] | managed code cannot start a thread; the worker is a native thread (docs/30 §5) |
| `mscorlib` lazy statics guarded by `lock` (comparers, encodings, culture data) | the `lock` does nothing (row above), so a first use on two threads at once races [inferred from the Monitor finding] | warm-up must touch every `mscorlib` type the worker uses (`EqualityComparer<T>.Default`, `Dictionary`, `List`, string concat) on the sim thread first |
| `mono_stats` counters | unlocked `add` [verified, docs/56] | accept (lost counts only) |

#### 3.3.1 Who uses `Monitor` (2026-10-20, static, I-120-6)

Source: the call index (`game-copy/analysis/callgraph/index.json`, all edge kinds) and the `MethodDef` impl flags of the ten extracted assemblies. Nothing was run.

| Assembly group | Callers of `System.Threading.Monitor` | `Wait`/`Pulse`/`PulseAll`/`TryEnter` | `Synchronized` methods |
|---|---|---|---|
| Game scripts (ScriptCore, SimIFace, Sims3GameplayObjects, Sims3GameplaySystems, Sims3Metadata, Sims3StoreObjects, UI) | **0** | 0 | 1,174, all event `add_`/`remove_` accessors (UI 584, Sims3GameplaySystems 448, SimIFace 92, ScriptCore 28, Sims3GameplayObjects 20, Sims3Metadata 2) |
| `mscorlib` | 182 (`lock` = `Enter`/`Exit`) | only `Monitor`'s own overloads calling each other | 36, all event accessors |
| `System`, `System.Xml` | 3, 4 | 0 | 0, 24 (event accessors) |

Verdict [verified for the shipped scripts]: no game script calls `Monitor` or uses `lock`, and nothing anywhere waits or pulses. The only implicit locking is the compiler-made `Synchronized` flag on field-like event accessors, which guards a delegate field during subscribe/unsubscribe; with `Monitor` a no-op it already gives no exclusion today, and the worker subscribes to nothing (docs/30 §5 rules). The `mscorlib` `lock` users are the lazy statics of the row above: warm-up, as already planned. Mods were not scanned; a mod that relies on `lock` across threads is already broken in this build, and the docs/122 allow-list (by assembly MVID) keeps mod code off the worker.

## 4. docs/30 design elements: verdicts and site list

| Element | Verdict | Why |
|---|---|---|
| **P1** register the worker (and the sim thread if missing) with `GC_new_thread` `0xE51070` under the GC lock | **holds** | one caller today, no lock inside, table entry layout and the `[0x11F4120]` wait as docs/30 §1; the duplicated handle has full access (§2.1). Open: which thread is registered today (I-120-1) |
| **P2** replace `[0x11949CC]` with a push of every suspended thread's registers and stack | **holds** | roots are image data + collector stack only, no `MEM_PRIVATE` (§1.2); the hook is the last step of every root push, including stopped marks; built in ts3native 0.10.4 (docs/30 §13) |
| **P3** gated, re-entrant GC lock | **needs change (one site)** | the 17 detours of docs/57 §7 stand (no new allocation or free-list writer found: the four entries, `GC_free`, the arena trio, the collection entries, finalizer/link registration, `GC_add_roots`); **add the `0xE4EC40` `ret` patch** (§2.2), applied at install with the first 4 bytes checked (`83 EC 20 53`, `sub esp,0x20` / `push ebx`) [verified bytes] |
| **P4** frame globals to TLS | **holds** | same nine sites, no other reference (§3.2) |
| **§5** worker entry through `0xE5A600` with domain TLS, no thread attach | **holds, two rules added** | exceptions unwind per thread (§3.1); add "no managed locking" (§3.3) and "worker only while `[0x11DC18C] == 0`" (§2.2) |
| Finalizers on the sim thread, `0xE4B3F0` replaced | **holds** | unchanged (docs/30 §12.3, docs/57 §5) |

**Site count for P3:** 17 lock detours (`0xE4BE30`, `0xE4BDC0`, `0xE4BF90`, `0xE50DE0`, `0xE4BCE0`, `0xE4A050`, `0xE4A0B0`, `0xE4E450`, `0xE4AAC0`, `0xE4ABE0`, `0xE4AE30`, `0xE4A3F0`, `0xE4BEB0`, `0xD71900`, `0xD71A20`, `0xD71B70`, `0xE4D9F0`) + the `0xE4B3F0` replacement + the `0xE4EC40` patch = **19 sites**. Optional as before: `GC_disable`/`GC_enable` `0xE4DFE0`/`0xE4DFD0`, `GC_make_descriptor` `0xE50F90` (docs/56; or warm-up), a lock around Mono's GC handle functions (docs/58). docs/57's "17 detours" stays correct for the lock; the total patch list grows by one.

## 5. Risks

1. **An unlisted lock taken while the world is stopped** (§2.2 lists what was found by direct calls; indirect calls from the mark code, such as the mark procs `0x12225E8`, were not followed). Mitigation: Mode=2 stress with Verify, a watchdog in ts3native that logs if a stopped world lasts more than ~1 s, crash triage per docs/30 §9.
2. **mscorlib first-use races** with a no-op `lock` (§3.3). Mitigation: warm-up list per worker method, Verify counters on the loader lock and vtable creation (docs/58 §4) by thread.
3. **The registered-thread set today** (I-120-1). If the main thread is in the table, P1 adds the sim thread and every stopped mark then suspends two threads, and the `0xE4EC40` patch also removes a hazard that exists today.
4. **Incremental steps with two mutators**: dirty bits are process-wide (write watch), and every cycle ends in a stopped mark that rescans all roots (docs/30 §12.6); not tested with two threads in this game [community-reported for stock Boehm].
5. **Value**: the first proof moves no game work off the sim thread; it proves the mechanism (docs/30 §10.6).

## 6. Effort and go/no-go

| Step | Work | Effort |
|---|---|---|
| a | P3: 17 detours, `0xE4B3F0` replacement, `0xE4EC40` patch, lock flag in `MtTick`, worker registration/unregister, counters, selftest (two threads on fake free lists and arena) | 3-5 h |
| b | P4: three block stubs and the `0xE534E0` replacement, selftest on copied bytes | 2-3 h |
| c | Worker entry (§5), `MtProbe` in ts3fixes with warm-up, `mtprobe` switch, Verify, rules check (`[0x11DC18C]`, Monitor-free probe code), registry row | 3-4 h |
| d | Offline proof in the docs/119 host or the emulator harness (docs/63): run `MtProbe` on two threads with a forced collection while the other thread is inside the interpreter | 1-2 h |
| | **Agent build total** | **~9-14 h** |
| live | One load with `[MonoThread] Enabled=1 Mode=0` (registration check, I-120-1); then clean nights: Mode=0 cost A/B, Mode=1 Verify, Mode=2 Verify on BP/SV/IP | 1 load + 3 clean-night runs |

**Go** for a), b), c) and the offline proof d): every static question docs/30 left has an answer, and none is a blocker. **Before any live Mode=1 run:** I-120-1 (one load) and the `0xE4EC40` patch in the build. The Mono plan is parked until routing is done (memory: Mono parked), so this is the ready-to-build state, not a request to start.

### 6.1 M5 build (2026-10-20)

Steps a), b) and the worker part of c) are built behind `[MonoThread] GcLock=1`, `FrameTls=1`, `Mode=1` (all off by default): the 19 P3 sites, P4, and an idle registered worker; selftest and byte checks pass; no game run, no monohost run yet. Details, tests and risks: docs/30 §14; ledger docs/51 §148 (I-120-7 to I-120-12).

## 7. Ledger

Open claims are in docs/51 §141 (I-120-1 to I-120-6). Closed statically 2026-10-20: I-120-2 (refuted) and I-120-3 (§2.2.1), I-120-6 (§3.3.1).

## 8. M5 offline proof in monohost (2026-10-20, G-A2 groundwork)

**Setup.** `native/monohost` links `native/ts3native/monothread.cpp` (which includes `monothread-m5.cpp`) unchanged, inside a namespace that supplies the four ts3native helpers and the counter fields it uses. After the M1.4/M1.5 steps the selftest hands `[0x11949CC]` back to Boehm's push, writes a `[MonoThread] Enabled=1 Mode=1 GcLock=1 FrameTls=1` ini, calls `ApplyMonoThread` and then `MtTick` from the host's main thread (as the sim thread). So the code under test is the M5 build itself: P1 registration, P2 root push, the 19 P3 sites, the P4 frame TLS blocks and the worker entry (`MtSubmit`, `0xE5A600`). Host-side additions: the exe's static TLS block for threads started later (host TLS callback; the exe now uses TLS index 63 and the loader's vector is kept and restored at thread exit, after a 2-entry vector crashed dbghelp/gdi32full/ntdll at worker exit), and a counting detour at `0xD8A8E0`. Managed jobs are in `HostTest.cs` (`WorkerGraph`, `WorkerMin`, results in statics). Runs: selftest x3 (28/28), plus 4 runs with the concurrent jobs (one `selftest ... m5concurrent`, three of a build that ran them by default). No game, no Steam. Claim ids I-120-13 to I-120-17 in docs/51 §152.

| Check | Result | Tag |
|---|---|---|
| Install | `ApplyMonoThread` accepts the mapped exe (all byte checks pass); first tick: the host thread is already in Boehm's table (index 0), P3 19 sites, P4 `0xF` (TLS slots 20/21), worker state 2 within the wait | verified (run x6) |
| **Worker alone** (`M5-worker-alone`) | main thread idle; the worker builds a 400-node graph held only by its own frame, 1,000 rounds of 300-node churn plus a sum check, `GC.Collect()` every 64 rounds: **1,000/1,000 clean, 15 collections run on the worker, and P2 pushed the suspended main thread's stack at each one**; ~220-330 ms | verified (run x6) |
| Stop (`M5-stop`) | `mtMode` 0 + `MtTick`: worker unregisters and exits (state 4), gate 0, no stop failure, no worker exception; the M1.4-style handle graph (1,000 nodes) intact after all jobs | verified (run x3) |
| **Both threads interpreting** (`m5concurrent` job A, `MinLoop`: float compares, no allocation in the loop) | **fails**: 3 of 4 runs fault on the worker at `0xE5444D` (`cmp eax,[edx]`, edx = state `+0x13C` = 0: the frame-exit arena check of `0xE53EB0` finds no arena chain on the worker's outermost state although the frame came from arena memory); 1 run hung (worker never finished in 60 s). An earlier build passed this job once (62 ms) | verified (fault, hang); cause open |
| Both threads allocating (job B, worker graph + main churn and a collection per pass) | **fails** within 16-200 ms: `IndexOutOfRangeException` at `stelem.ref` in `Build` (an array's length changed under the worker), `NullReferenceException` right after a field store in `Churn`, or one corrupt sum. With 215 collections in one long run, still corrupt | verified (run) |
| `0xD8A8E0` (F1 question) | **0 entries on either thread** in every job: in the host `Math.Min(float)` is plain corlib IL, because EA's binder bodies (`0xE3E570`, docs/122 §12.1) are not registered; the cursor `[0x11EE5A4]` is 0 (never reset to the sentinel, `0xD9A520` not run) | verified (counter) |

**Verdicts.**

1. **GC side of M5 holds where it was tested:** a worker registered by P1 runs managed code under the P3 lock, its own collections stop the main thread, and P2 pushes that thread's registers and stack; nothing was lost over 15 worker-run collections (I-120-13, I-120-14). Collections run by the main thread while the worker is inside managed code were only seen together with the interpreter fault below, so "the worker's stack is scanned by a main-thread collection" is **not yet proven** (I-120-15).
2. **The interpreter is not yet safe on two threads, with or without allocation** (I-120-16). The fault is in frame bookkeeping, not in the collector: a frame's arena memory and its state's chain (`+0x13C`) disagree. P4 moves only `[0x11F4170]`/`[0x11F416C]`; the remaining shared candidates are docs/56 §2's list (EA pair `[0x11F4158]`/`[0x11F415C]`, thunk hash `[0x11F4128]`, `0xE50F90` typed descriptors) and the arena trio `0xD71900`/`0xD71A20`/`0xD71B70`, whose chunk list `[0x11EE508]` is locked by P3 but whose use by the interpreter's inline bump (`0xE563B2`-`0xE563D1`) is not. **M5 Mode=1 must not run a job while the sim thread interprets** until this is found; the in-game Mode=1 idle worker (no job) is unaffected.
3. **F1 (docs/122 §12.5): confirmed statically, not reproducible in this host.** Bytes of `0xD8A830`/`0xD8A8E0`: `[0x11EE5A4]` is a cursor into one chain of preallocated call records (node: `+0` next, `+4` prev, `+8` record `0x150`, `+0xC` buffer `0x28`; sentinel `0x11944C8`, tail `[0x11944CC]`, nodes from the pool at `0x11944D0`). Entry takes the record at the cursor and moves it forward; exit moves it back one (`0xD8A9A5`). Interleaved on two threads (A in, B in, A out, A in) the cursor hands B's live record to A: **two interpreters on one record** = crash, as docs/122 said (I-120-17, mechanism verified from bytes; live proof needs EA's binder table in the host). **F1 design (unchanged in intent, now concrete):** a detour at `0xD8A8E0` (5 bytes `8B 44 24 18 53`, no relative operand) that sends only the worker to a host copy of the two functions on a worker-private chain (own sentinel and cursor, nodes from the CRT, the two record buffers from `GC_malloc_uncollectable` so they stay roots and go through the P3 lock), same record fill, `0xE85040`, `0xE53EB0`, clear and step back; the sim thread keeps the game's code. A host prototype of this path was written and removed again because nothing in the host calls `0xD8A8E0`; it belongs in ts3native with `[Verify]`'s shared stub (docs/122 §12.4) and a two-thread selftest on a fake chain.

**Next (G-A2):** find the shared state behind the `0xE5444D` fault: run job A with a watch on the worker's state `+0x13C` and on `[0x11EE508]`, and with P3's arena trio counters per thread; then repeat jobs A and B (`selftest ... m5concurrent`) until both pass three times. Then register EA's binder table in the host (or call `0xD8A8E0` with a recorded argument set) for a live F1 proof.


## 9. The `0xE5444D` cause, the arena pool fix and F1 live (2026-10-20, G-A2)

Host runs only (`native/monohost` selftest on the `game-copy/monohost` exe copy), capstone reads of the exe bytes, no game, no Steam. Ledger docs/51 §157 (I-120-15 verified, I-120-16 closed, I-120-17 live, I-120-1 answered live, new I-120-18 to I-120-22).

### 9.1 Cause: one chunk in two states, from a double release in EA's frame arena

The interpreter's frame memory (locals, arguments, eval stack of each frame) is bump-allocated from a chain of 0x7FC-byte chunks per interpreter state (state `+0x13C` = current chunk; chunk `+0` top, `+4` end, `+8` previous chunk, data from `+0xC`). Chunks come from one process-wide pool `[0x11EE508]` (circular list of nodes: `+0` prev, `+4` next, `+8` chunk) and go back to it when a frame exit pops a chunk to its data start (`0xD71A20`) or when a state is freed (`0xD71B70`, release all) [verified bytes]. The inline bump (`0xE563B2`-`0xE563D1`) and the inline pop (`0xE54437`-`0xE54451`, `0xE59800`-`0xE59811`) touch only the state's own chunk; the pool is only touched by `0xD71900`/`0xD71A20`/`0xD71B70` (P3-locked) and the shutdown path `0xD80AF3` [verified, operand scan of the whole `.text`].

- **Bug [verified bytes]:** `0xD71B70` pushes every chunk of the chain back to the pool but never clears `+0x13C`. A finished task state is freed twice by EA's own flow: the interpreter frees it when the task ends (`0xE59FD0` -> `0xE52DD0`, sites `0xE5A4FD`/`0xE5A53E`/`0xE5A589`/`0xE5A5A4`) and `ScriptHost_RunTask` frees it again (`0xD7F166` -> `0xE53E80` -> `0xE52DD0`) [verified callers]. Normally the chain is already empty by then (every frame popped), so the second call does nothing. A task that ends with a managed exception leaves frames on the chain; the first free pools the chunk, the second pools it again.
- **Seen in the host [verified, arena call trace]:** M1.5's `SleepInCctor` task (yield refused in a `.cctor`, `TypeInitializationException` out of the task) took chunk C, then two release-all calls on the same state both found C on the chain: the pool held C twice from then on (pool walk: 65 nodes, 1 duplicate, after M1.5; 0 before). In job A the main thread's invocation took C and the worker's took the next node, C again; both bumped frames into the same chunk. The worker's frame exit then found its chain emptied by the other state's pop (fault at `0xE5444D`, `+0x13C` = 0), or its locals were overwritten (the "job ends in 8-19 us with result 0" runs). Job B's corrupt sums and exceptions in docs/120 §8 are the same shared chunk.
- **Not a missing TLS move [verified]:** operand census of the interpreter core `0xE52000`-`0xE5A700`: the only written absolute globals are the TLS key `[0x11F4160]` (init), `[0x11F4164]` (init/shutdown), the runtime-method hash `[0x11F4168]` (created under `0x11F4130`) and P4's `[0x11F4170]`/`[0x11F416C]`; reads are init-time flags (`[0x11F4148]`, `[0x11F414C]`, `[0x11F4154]`) and `mono_defaults` class pointers (`0x1220E68`-`0x1220EE4`). No other `0x11F4170`/`0x11F416C` reference exists in the exe. This matches docs/56 §2; the arena pool is the only shared mutable state the frame path uses, and P3 already locks its three functions. docs/56's other shared items stay as they were (thunk hash `[0x11F4128]` covered by the warm-up rule, `0xE50F90` typed descriptors, call-record cursor = F1).
- **In the game [inferred]:** the same double release happens whenever a script task ends with an exception while frames are on its chain. With one thread the duplicate bites only when two states are live at once (a native-to-managed call nested inside a script call takes the second copy); with the M5 worker two live states are the normal case. How often game tasks end that way is unmeasured (I-120-22).

### 9.2 Fix (`[MonoThread] ArenaFix=1`, default 1, active only with `GcLock=1`)

In `monothread-m5.cpp`, chained into P3's trampoline slots, so it runs on both gate paths (with or without the lock):

| Part | Where | What |
|---|---|---|
| clear after release-all | `0xD71B70` slot | run the original, then `state+0x13C = 0`; counts calls that found a non-empty chain (`arenaRelNonEmpty`) |
| dedupe at install | P3 install (sim thread, tick boundary) | walk the pool once, unlink later nodes whose chunk is already listed |
| heal before grow | `0xD71900` slot | while the pool head's chunk is in use (top != data start; every release path resets top [verified bytes]), unlink that node; counts `arenaDropped` |

The heal covers a duplicate created before install whose other copy is live in a suspended task. Counters: `ts3native_mtprobe` v2 (`arenaRelNonEmpty` +0x6C, `arenaDropped` +0x70, docs/registry.md). Selftest `TestMonoThreadM5`: fake pool A B A C B -> dedupe drops 2; A and B in use -> heal drops both; release-all wrapper clears `+0x13C` and a second free is harmless.

### 9.3 Host results (monohost selftest, 33 checks, default run)

| Check | Result | Tag |
|---|---|---|
| `M5-arena-dedupe` | 1 duplicate before install (M1.5's task), 1 node dropped, 0 after | verified (run x10) |
| `M5-arena-release` | a second exception task after install (`SleepInCctor2`): release-all of a non-empty chain 1, 0 duplicates after | verified (run x10) |
| `M5-concurrent-noalloc` (job A) | 5 rounds of `MinLoop(400000)` on the worker while the main thread loops `MinLoop(5000)`: 0 wrong results either side, ~410-460 main passes, shortest worker run 55-72 ms (was 8-19 us or a fault) | verified (run x10) |
| `M5-concurrent-alloc` (job B, I-120-15) | 400-node graph only on the worker's frame intact over 3,000 rounds while the main thread allocates and collects: ~800 collections, ~755 of them run by the main thread, P2 stacks pushed = collections (each main-run collection pushes the worker's suspended stack) | verified (run x10) |
| `M5-n2m-private` (F1, §9.4) | 5 rounds through `0xD8A8E0` on both threads: 0 wrong results, ~430 main entries on the game's chain, 5 worker entries on private records, cursor balanced | verified (run x4) |
| `M5-stop` | worker stops, 0 pool duplicates, 12 jobs | verified (run x10) |
| negative control `MONOHOST_ARENAFIX=0` | dedupe/release FAIL; job A: worker runs of 8 us (1 run) or the `0xE5444D` fault (1 run) | verified (run x2) |

Runs: 6 before the F1 job was added (32/32) and 4 of the final build (33/33), exit 0, ~10 s each; 3 more F1 runs passed every check except a wrong cursor expectation in the test itself (fixed: the idle cursor is the first free node, not the sentinel). `selftest <hostdata> m5concurrent` is no longer needed: the concurrent jobs are in the default run.

### 9.4 F1 live: the hazard and the private records (`[MonoThread] N2mPrivate=1`, default 1, with `GcLock=1`)

**Host setup:** a host internal call `MonoHostTest.HostTest::N2mMinLoop` enters the interpreter the way EA's binder bodies do: `0xD8A8E0(RuntimeMethod of MinLoop, this, stackval args, stackval ret, &exc, this thread's ThreadContext)` (signature read from the bytes: args at `+4`..`+0x18`, cdecl, no return value [verified]). The cursor `[0x11EE5A4]` starts at the sentinel `0x11944C8` as `0xD9A520` sets it in the game (sentinel and node allocator `0x11944D0` are initialised in the image's data [verified bytes]).

- **Hazard [verified, run x4]:** with `MONOHOST_F1=0` (game code on both threads) the first round faults (`0xE53F4D`, `0xE584B1`, `0xD8A95B`, `0xE54406`) and the main thread's calls return corrupt exceptions (`System.AppDomainSetup` thrown). Both threads ran on records handed out by one cursor.
- **Fix [verified, run x7]:** a 5-byte detour at `0xD8A8E0` (`8B 44 24 18 53`, no relative operand) compares the thread id with the worker's (`fs:[0x24]`); the worker runs the same body on a private stack of records (32 levels, 0x150 record + 0x28 buffer from `GC_malloc_uncollectable`, so they are roots and their allocation goes through the P3 lock): same fill, `0xE85040`, `0xE53EB0`, exception out, buffer and record cleared, buffer link restored. A full private stack or a failed allocation returns to the game's path and counts `n2mOverflow` (tripwire). The sim thread runs the game's code unchanged. Counters `n2mReady`, `n2mWorker`, `n2mMaxDepth`, `n2mOverflow` (v2, +0x74..+0x80). Selftest: fake exec nests 2 deep (distinct records, field fill, exception out, cleared after), overflow refused.
- **Install order:** `[Verify]`'s `nativeToManaged` stub owns `0xD8A8E0` when it is on; F1 installs at the first Simulate tick and finds changed bytes, so it skips with a log line. Running both needs F1 to chain into `[Verify]`'s stub (docs/122 §12.4). **Built 2026-11-15 (lane L9, offline, not run in game):** `verify.cpp` hands its nativeToManaged trampoline slot to `[MonoThread]` after install; F1 at the first tick swaps that slot for its entry, so Verify counts first and F1 decides worker vs game path (the game path runs the same 5 prologue bytes and jumps to `+5`, as the trampoline did). No game bytes written; `n2mReady` = 2 for the chained form. Selftest: slot taken, `n2mReady` 2.

### 9.5 Verdicts and next

1. **I-120-16 closed:** the shared state behind `0xE5444D` is EA's frame arena pool, poisoned by a double release; with `ArenaFix=1` two threads interpret at once with and without allocation, 10 of 10 runs clean.
2. **I-120-15 verified:** collections run by the main thread while the worker is inside managed code push the worker's stack, and a graph held only there survives ~755 of them per run.
3. **I-120-1 answered live** (job `bp-mt-p1`, ts3native 0.35.20, `[MonoThread] Enabled=1 Mode=0`): first-tick log "sim thread 32704 already registered (main 18852)", counters `mtReady` 7, `mtSimTid` 32704, `mtThreads` 1, `mtSimRegistered` 0. Boehm's table holds the **sim thread** (`GC_init` ran there), not the main thread, and it is the only registered thread: P1 has nothing to add for the sim thread, and P2/stop-the-world see exactly the sim thread plus the worker in Mode=1 [verified live]. This matches the host setup, where the host's main thread plays the sim thread and is table index 0.
4. **I-120-17 live:** the F1 hazard crashes in the host; `N2mPrivate=1` fixes it for the worker.
5. **Next (M5 -> M6):** the ts3fixes `MtProbe` job + Verify mode in game; F1 chained with `[Verify]`; measure how often game tasks end with a non-empty chain (`arenaRelNonEmpty` per hour, I-120-22) — if often, `ArenaFix` is worth having without the worker too (a separate single-thread switch).

## 10. I-120-8 static audit: does the P3 return-address swap fit all 17 entries? (2026-10-20)

Static only (no game run, no build). P3 (docs/30 §14.1, `monothread-m5.cpp` `MtLockEnter` / `MtLockExitThunk`) replaces each hooked call's return address with a thunk and keeps the real one on a global shadow stack (`g_mtRet[g_mtDepth]`) that the recursive `g_mtLock` guards. That is correct only if the hooked function (1) never reads its own return slot, (2) leaves only through a return that reaches the thunk (a tail jump is fine: the target returns to the thunk), and (3) is never left by `longjmp` or an SEH unwind while the lock is held. `tools/p3-entry-audit.py` disassembles each entry (capstone 5, Steam 1.67 `TS3W.exe`, Ghidra function bounds), tracks the stack offset of the return slot, lists exits, tail jumps, `fs:[0]` frames and indirect calls, and walks the direct-call closure to depth 6. The callback slots it finds were then followed by hand.

### 10.1 Per entry

| Entry | Function | Exits | Tail jump (target exit) | Own SEH | Return-slot reads | Reaches `GC_oom_fn` (longjmp) | Verdict |
|---|---|---|---|---|---|---|---|
| `0xE4BE30` | `GC_malloc` | 1 `ret` | `0xE4DBE0` x2 (plain `ret`, no slot read) | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xE4BDC0` | `GC_malloc_atomic` | 1 `ret` | `0xE4DBE0` x2 (as above) | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xE4BF90` | `GC_malloc_uncollectable` | 3 `ret` | - | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xE50DE0` | `GC_malloc_explicitly_typed` | 4 `ret` | - | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xE4BCE0` | `GC_generic_malloc` | 2 `ret` | - | no | none | **yes, own call** `[0x11949BC]` at `0xE4BDAD` | fits **except OOM** |
| `0xE4A050` | `GC_try_to_collect` | 1 `ret` | - | no | none | no | **fits** |
| `0xE4A0B0` | `GC_gcollect` | none of its own | `0xE4F2E0` (plain `ret`, no slot read) | no | none | no | **fits** |
| `0xE4E450` | `GC_enable_incremental` | 1 `ret` | - | no | none | no | **fits** |
| `0xE4AAC0` | register disappearing link | 3 `ret` | - | no | none | **yes, own call** at `0xE4AB81` | fits **except OOM** |
| `0xE4ABE0` | unregister disappearing link | 2 `ret` | - | no | none | no (20 functions, no indirect call) | **fits** |
| `0xE4AE30` | register finalizer | 1 `ret` | - | no | none | yes, via `0xE4AC60` (call at `0xE4AD67`) | fits **except OOM** |
| `0xE4A3F0` | `GC_apply_to_each_object` | 1 `ret` | - | no | none | no; its one callback (from `0xD7047A`) is `0xD70280` -> `0xD70110`: 48 functions, no hooked entry, no unwind import | **fits** |
| `0xE4BEB0` | `GC_free` | 2 `ret` | - | no | none | no | **fits** |
| `0xD71900` | EA frame arena grow | 1 `ret` | - | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xD71A20` | EA frame arena pop | 4 `ret` | - | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xD71B70` | EA frame arena release all | 1 `ret` | - | no | none | yes, via `GC_generic_malloc` | fits **except OOM** |
| `0xE4D9F0` | `GC_add_roots` | 1 `ret` | - | no | none | no (5 functions) | **fits** |

All [verified: tool output + hand reads of the named sites]. No entry has an `fs:[0]` frame, none reads its return slot (no `[esp+off]` at the slot offset, no `[ebp+4]`, no copy of `esp` used as a pointer), and the three tail jumps land in functions that end in a plain `ret` and do not read the slot either.

### 10.2 The one unwind out of a locked entry: `GC_oom_fn` -> managed `OutOfMemoryException` -> `longjmp`

- `mono_gc_base_init` (`0xE69570`) stores `0xE78610` into `GC_oom_fn` `[0x11949BC]` at `0xE695A2` (the static value `0x5BFB90` is a `return 0`) [verified].
- `0xE78610` reads the domain's preallocated out-of-memory exception (domain `+0x2C`) and calls `mono_raise_exception` `0xEA18F0`, which calls the handler in `[0x1195B64]` [verified].
- The interpreter sets that handler to `0xE53740` at `0xE5A70F` (the static default `0xEA18A0` prints "Exception %s.%s raised in C code" and exits) [verified].
- `0xE53740` takes the thread's interpreter context from TLS `[0x11F4160]`; with a context and a saved `jmp_buf` it calls MSVCR80 **`longjmp`** (`0xE537B2`, import `0xF95704`) to the interpreter frame's `setjmp3` (docs/120 summary) [verified]. Without a context it returns, `0xE78610` returns 0 and the allocation returns NULL.
- `GC_generic_malloc` calls `GC_oom_fn` only after the inner allocation and the collection/expansion retries failed, so this is the **heap-exhausted path** [verified for the call site; the retry order is stock Boehm, community-reported].

Effect with the gate on: the `longjmp` skips every thunk between the interpreter frame and the GC frame (two when `GC_malloc` reached `GC_generic_malloc` through its own hook). `g_mtLock` stays entered once per skipped thunk and `g_mtRet` keeps those entries. The next locked exit on that thread pops a stale return address and jumps into a dead frame (crash), and the other thread blocks on the lock for good (hang). This is the address-space-exhaustion case the project exists to make rarer (Error 12), so it is not a theoretical path; without P3 the same OOM is an ordinary managed exception that script code may survive.

Other unwind sources, all ruled out [verified]:

- The only SEH frame in any closure is `GC_mark_some` `0xE4D460` (`__try`/`__except` on `0xC0000005`, string "Caught ACCESS_VIOLATION in marker"): its handler at `0xE4D4AF` restores `esp` inside the same function and carries on, so an unwind stops below every hooked frame, and its marker closure (`0xE4D270`, 4 functions by direct call) reaches no hooked entry.
- No closure imports `RtlUnwind`, `RaiseException`, `_CxxThrowException`, `longjmp` or `setjmp`; the CRT `exit` import (GC abort paths) ends the process, which no lock state survives anyway.
- The other callback slots: warn proc `[0x11949C0]` (Mono sets `0xE69550`, a log call, at `0xE695AC`); `[0x11F309C]` and `[0x11F3098]` are only called when `[0x11F3094]` is non-zero, and no instruction in `.text` writes any of the three; `[0x1194830]` is `0xE4A410`, a heap-object print used by leak/smash reports. None unwinds.

### 10.3 Verdict and fix

- **I-120-8: refuted for one path, holds otherwise.** 7 of 17 entries fit outright; the other 10 fit on every path except the heap-exhausted `GC_oom_fn` path, which `longjmp`s out of the locked frame.
- **Fix [design, not built]:** store the return-slot address with each shadow entry (`g_mtSlot[d] = retSlot`). Stack frames nest, so a live outer hooked frame always has a higher slot address than any inner one. In `MtLockEnter`, drop (and `LeaveCriticalSection` once for) every top entry whose slot address is at or below the new `retSlot`; in `MtLockLeave`, drop every top entry whose slot is below the returning frame's slot (`esp - 4` at thunk entry) before popping. A frame abandoned by any unwind is then cleaned up at the thread's next hooked entry or exit. Add a `lockStale` counter. Cheaper alternative: wrap `GC_oom_fn` (write our function into `[0x11949BC]` while the gate is on) to release this thread's lock depth and clear its shadow entries before calling `0xE78610`. That covers only the known path; the slot rule covers any unwind. Selftest: a fake hooked entry that `longjmp`s to its caller twice, then a normal locked call; expect `lockStale` 2, depth 0, the right return address.
- Until the fix is in, `lockOverflow`/`lockMaxDepth` (the I-120-8 live test) would not show this leak, because no OOM happens in a normal 10-minute load. The live check should add an OOM case or wait for the fix.

### 10.4 Fix built (ts3native 0.36.6 (branch build 0.36.4), offline, not run in game)

The slot rule of §10.3, behind `GcLock=1` like the rest of P3 (no new key; default off):

- `MtLockEnter` stores `g_mtSlot[d] = retSlot` (caller's ESP at entry) next to `g_mtRet[d]`. After taking the lock it drops every top entry whose slot is **at or below** the new `retSlot` (a live outer hooked frame always has a higher slot; equal means the same caller frame calling again after an unwind). Each dropped entry does one `LeaveCriticalSection` (the recursion the skipped thunk never released) and counts `lockStale`. Any entry present then belongs to the caller: it holds the lock, and entries are only pushed by the owner.
- `MtLockExitThunk` now passes its entry ESP (returning slot + 4 + any `ret N`) to `MtLockLeave`, which drops top entries while the entry below also has a slot under that ESP (the lowest such entry is the returning frame, the ones above it were unwound), then pops as before.
- Counter `ts3native_mtprobe` v3: `lockStale` at `+0x84` (docs/registry.md; `tools/probe/agent.js` reads it).
- Selftest (`TestMonoThreadM5`, `selftest ok`): a fake hooked entry `longjmp`s to its caller with the gate on: depth 1 and lock recursion 1 remain (the leak as audited). The next locked call on the same thread returns its own value to the right place, depth 0, recursion 0, no owner, `lockStale` 1, and a second thread then takes the lock through a hook within 5 s. Exit path: a hooked outer entry `setjmp`s, calls the hooked jumper and returns normally through its thunk: value correct, depth 0, `lockStale` 2.
- Limit [inferred]: a stale entry is only dropped when its thread makes another hooked call or exit from a shallower frame. After an OOM `longjmp` the interpreter allocates again almost at once (exception object handling, frame arena), so this is expected to be immediate; until then the other thread waits on the lock. The `GC_oom_fn` wrapper of §10.3 would close that gap for the known path; built in §10.5.

Ledger: docs/51 §181.

### 10.5 `GC_oom_fn` wrapper (ts3native 0.36.6 (branch build 0.36.5), offline, not run in game)

Closes the §10.4 gap (I-120-28) for the known path: the out-of-memory thread gives the lock up **before** Mono raises, so the other thread does not wait for the OOM thread's next hooked call.

**Static check of the slot and its callers** (capstone on the Steam 1.67 `TS3W.exe` game copy, Ghidra bounds; every 4-byte `BC 49 19 01` in the image) [verified]:

| Site | Function | Use | Locks held at the call |
|---|---|---|---|
| `0xE695A2` | `mono_gc_base_init` `0xE69570` | `mov [0x11949BC], 0xE78610` (the only write) | - |
| `0xE4BDAD` | `GC_generic_malloc` `0xE4BCE0` (hooked) | `push [esp+0xC]` (size); `call [0x11949BC]`; `add esp,4`; result returned | none of Boehm's: `LOCK()`/`UNLOCK()` compile to nothing (allocation lock compiled out, §1.1); the function has no `EnterCriticalSection` or lock call, only P3's |
| `0xE4AB81` | register disappearing link `0xE4AAC0` (hooked) | `push 0xC`; `call [0x11949BC]`; `add esp,4`; NULL -> `[0x11F302C]++` (`GC_finalization_failures`), return 2 | none of Boehm's (same) |
| `0xE4AD66` | `GC_register_finalizer_inner` `0xE4AC60` (from register finalizer `0xE4AE30`, hooked) | `push 0x18`; `call [0x11949BC]`; `add esp,4`; NULL -> `[0x11F302C]++`, return | none of Boehm's (same) |

So the slot is `void* __cdecl (size_t)`, caller pops, result in EAX, and every caller treats NULL as failure. `0xE78610` ignores its argument: `mono_domain_get` `0xE75260`, push domain `+0x2C` (preallocated OOM exception), `mono_raise_exception` `0xEA18F0`, then `xor eax,eax; ret` (§10.2). The only lock any thread holds at the call is P3's `g_mtLock` (one recursion per locked hook frame on the shadow stack, plus any C-side hold), so releasing this thread's hook entries never releases a lock Boehm expects held. Static premise for releasing **all** of them: no hooked entry calls into the interpreter (§10.1 closures: the one callback reaches no managed code, finalizers run outside the lock), so every locked hook frame on the thread sits below the interpreter's `setjmp3` frame and the `longjmp` skips them all [verified, §10.1].

**Wrapper** (`MtOomWrap` in `monothread-m5.cpp`, installed by `MtInstallP3`, i.e. only with `[MonoThread] GcLock=1`; default off):

- Install: if `[0x11949BC]` is still `0xE78610`, keep it as the original and write `MtOomWrap` (`p3Ready` bit 5). Any other value (another mod's handler) is left alone and logged.
- Call, on the thread that ran out: if it owns `g_mtLock`, copy its `g_mtDepth` shadow entries (return address + slot) to the wrapper's stack, set depth 0, `LeaveCriticalSection` once per entry, count them in `lockOomReleased` (`ts3native_mtprobe` v4, `+0x88`). C-side holds (`MtLockC`) are not entries and stay. A thread that does not own the lock (gate off, or no hooked frame) releases nothing.
- Then **call** (not tail-jump) `0xE78610`. With an interpreter context it `longjmp`s past the wrapper and every hooked frame, exactly as without the wrapper (same argument, no SEH frame or destructor in the wrapper). Without one (native caller, worker before its context) it returns NULL and the hooked frames return normally through their thunks, so a tail jump would leave them with no entries (wrong return address). The wrapper therefore takes the lock again (one recursion per entry), puts the saved entries back on top and returns the NULL. The other thread may have used the lock meanwhile; it holds no entries once it has released it, so the stack is ours again.
- The §10.4 slot rule stays for any other unwind; after an OOM `longjmp` through the wrapper nothing is stale (`lockStale` unchanged).

**Selftest** (`TestMonoThreadM5`, `selftest ok`): fake hooked outer -> fake hooked inner -> `call [fake slot]` (as `GC_malloc` -> `GC_generic_malloc` -> `GC_oom_fn`), gate on. `MtWrapOom` refuses a slot that is not the expected handler. Not owner: pass-through, nothing released. Longjmp path: the fake Mono handler starts a second thread that takes the lock through a hook and waits for it (5 s limit) **before** the OOM thread makes any further call, then `longjmp`s: the other thread got the lock, `lockOomReleased` 2, `lockStale` unchanged, depth 0, recursion 0, no owner. Return path (no context): same, then both frames return through their thunks: NULL returned, outer frame resumed, `lockOomReleased` 4, depth 0, lock free; a following locked call returns right. The I-120-8 tests before it still pass.
- Test note: the fake outer must not tail-call the inner hook. A hooked entry that tail-jumps into another hooked entry shares its return slot, which the §10.4 rule reads as an unwound frame (the outer entry is dropped as stale). No game entry does that: the three tail jumps of §10.1 land in unhooked functions [verified].

Ledger: docs/51 §183.

Reproduce: `python tools/p3-entry-audit.py <repo>/game-copy/bin/TS3W.exe <repo>/game-copy/analysis/functions.csv` (17 blocks; the `GC_oom_fn` line names the function that calls the slot), then capstone at `0xE695A2`, `0xE78610`, `0xEA18F0`, `0xE5A70A`, `0xE53740`, `0xE4D460`. Ledger: docs/51 §173.
