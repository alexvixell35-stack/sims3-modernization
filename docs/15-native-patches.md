# 15. ts3native.asi: our native patches

Our own ASI plugin for patches the C# mod cannot do. Source: `native/ts3native/`. Build: `native\ts3native\build.cmd` (VS 2022 Professional, `vcvars32.bat`, `cl /LD /O2 /MT`, 32-bit, static CRT). It also builds and runs `selftest.exe`, which checks the patch and detour helpers on local memory. Output: `native/ts3native/out/ts3native.asi` + `ts3native.ini` (git-ignored).

Tags as in docs/13: **[code]** = checked in the exe bytes or decompile, **[inferred]** = to confirm by measurement.

Target: TS3W.exe Steam 1.67, SHA-256 `10b2423e2368e161774462b0ef1960231ad0af298d564a7bb6a6e78c5e880609`. PE header of `game-copy/bin/TS3W.exe`: ImageBase `0x400000`, DllCharacteristics `0x0` (no DYNAMIC_BASE, so no ASLR), Characteristics `0x123` (relocations stripped). Sections: `.text` 0x401000, `.rdata` 0xF95000, `.data` 0x1146000. **[code]**

## How it works

- `DllMain(DLL_PROCESS_ATTACH)` runs all patches once. It does nothing unless the host is `TS3W.exe` loaded at `0x400000`.
- Each patch lists its sites with the full original instruction bytes. All sites of a patch are compared first; if any byte differs, that patch is skipped and the found bytes are logged. Only then are the immediates written, with `VirtualProtect` around each write and `FlushInstructionCache`. Memory is checked with `VirtualQuery` before it is read, so a wrong exe cannot crash the plugin.
- Settings: `ts3native.ini` next to the `.asi`. Log: `ts3native.log` next to the `.asi`, rewritten each launch (version, host, settings, each patch `OK`/`SKIP`/`FAIL` with old and new values).

## Phase 1: GC patches (docs/13)

| Patch | Site | Original bytes | Instruction | New (defaults) | ini |
|---|---|---|---|---|---|
| Initial heap | `0xE4E168` (imm `0xE4E169`) in `GC_init` `0xE4E160` | `BD 00 28 00 00` | `mov ebp,0x2800` (40 MB in 4 KB blocks) | `BD 00 00 01 00` = 0x10000 blocks = 256 MB | `InitialHeapMB` (40-1024, default 256) |
| Growth step 1/3 | `0xE4A1D8` (imm `0xE4A1D9`) in `GC_collect_or_expand` `0xE4A120` | `3D 00 08 00 00 76 2C` | `cmp eax,0x800 ; jbe` | `3D 00 20 00 00` = 0x2000 = 32 MB | `GrowthStepMB` (8-256, default 32) |
| Growth step 2/3 | `0xE4A1FF` (imm `0xE4A200`) | `3D 00 08 00 00 77 05` | `cmp eax,0x800 ; ja` | `3D 00 20 00 00` | same |
| Growth step 3/3 | `0xE4A206` (imm `0xE4A207`) | `B8 00 08 00 00 50` | `mov eax,0x800 ; push eax` | `B8 00 20 00 00` | same |
| Budget ceiling (off by default) | `0x1192324` (`.data` dword) | `DC 05 00 00` | 1500, max of the per-frame GC budget `0x11922A4` | e.g. `C4 09 00 00` = 2500 | `BudgetCeiling` (0 = unchanged, else 500-20000) |

Only the trailing opcode bytes (`76 2C`, `77 05`, `50`) are in the comparison, not written. The three growth sites are patched together or not at all. Values equal to the game default (40 MB, 8 MB) write nothing. `GC_INITIAL_HEAP_SIZE`, if set in the environment, still overrides our default because `GC_init` only uses `ebp` when the env var is absent (decompile `gc13/core.c`); the plugin logs a note when it sees the variable. **[code]**

### Is DllMain early enough? Yes.

- `GC_init` (`0xE4E160`) begins `cmp dword [0x11F30D0],0 ; push ebp ; mov ebp,0x2800 ; jnz <return>`: it reads the immediate on its single real run, guarded by `GC_is_initialized` (`0x11F30D0`), which it sets to 1 at the end. **[code]**
- It has no static or CRT-initializer caller. The only direct calls come from lazy GC entry points: `0xE4A06D` and `0xE4A0CD` (`GC_try_to_collect` / `GC_gcollect`), `0xE4BAB0` and `0xE4BC07` (large-object alloc), `0xE4E47E` (`GC_enable_incremental`, called from MonoScriptHost init at `0xD7E602`). The only `jmp`s to it come from the public wrapper `0xE4E4C0` (init the allocation lock, then `jmp GC_init`), which is called from `0xE66242` and `0xE75C22`; walking callers up, both lead to `0xE5A680` (Mono runtime init), called only from `0xD7E60C` inside MonoScriptHost init (`0xD7E560`). No absolute pointer to either function exists in the file. So the first GC init happens when MonoScriptHost starts Mono, well after `WinMain`. **[code: E8/E9 and pointer scan of the whole file]**
- Ultimate ASI Loader, as `wininet.dll`, loads plugins during process start-up, before the exe's entry point (`0xF7F371`) runs. **[community: UAL design; S3SS relies on the same timing]**
- Runtime proof per launch: the plugin reads `0x11F30D0` before patching. If it is already 1, the initial-heap patch is skipped and the log says "plugin loaded too late". The growth-step and ceiling patches still apply, as both are read on every use.

## Phase 2: route negative cache (v0.2.0, `[RouteCache] Enabled=0` by default)

Built and self-tested; **not yet run in the game**. Recommendation 1 of docs/11: a repeat of a request that just failed with "no path" returns a plain failure at once, before the RoutingWorld lock, so none of the native retries, the obstacle replan, or the C# fail-path plans run.

### Patch site and calling convention [code: dumpbin disassembly of `0x82C6E0`–`0x82C7FB`, call sites `0x760DE1`, `0xC7C6E3`, `0xC7C9D5`]

`InstallDetour` writes `jmp hook` over the 6 bytes `81 EC D8 00 00 00` (`sub esp,0xD8`; checked first, skipped and logged if different). The trampoline is that instruction plus `jmp 0x82C6E6`.

`Route_PlanLocked` is `__thiscall`: `ecx` = RoutingWorld (`0x8292D0()` at the PlanCommon call site). It takes 10 stack dwords and ends with `ret 28h`. The result is a bool in `AL`. The hook is `__fastcall` with a dummy `edx`, which gives the same stack layout and cleanup.

| Stack arg | Value at the `0x760DE1` call (Route_PlanCommon, `esi` = native route) | Meaning |
|---|---|---|
| 1 | `esi+0xE0` | out path list (EASTL list head). PlanCommon empties it before the call (`0x7606E0` prologue) |
| 2 | PlanCommon arg 2 | start points vector `{begin,end}`, 64-byte records |
| 3 | local vector (accepted points) | goal points vector, 64-byte records, x,y,z floats at +0 (x/z bounds-checked in PlanCommon) |
| 4 | `[esi+0x3C]` | agent class: 0x800 flier, 0x900 walking dog, 0xA00 little dog, 0xB00 leading horse, 0x4400 shark (set in PlanCommon from the options) |
| 5 | `1` | do-plan flag (byte); 0 = take the lock, run `0x82C420`, return 0 |
| 6 | `esi+0x158` | RoutingOptions; RouteOption lo/hi at +0x28/+0x2C, RouteOption2 lo/hi at +0x30/+0x34 (`0x81C880` is called with `ecx = esi+0x158` at `0x7608AD`) |
| 7, 8 | `&local`, `&local` | out dwords, zeroed by the caller |
| 9, 10 | `[esi+0x40]`, `[esi+0x44]` | **follower ObjectGuid** (64-bit). docs/11 called these "lot id": `0x752620` compares each blocker id with route+0x40/+0x44 to skip the follower itself, so they are the follower's id [code for the comparison, meaning inferred] |

`0xC7BF70` (off-screen road placement) calls it directly with agent class 0, follower 0 and a local list.

Correction to docs/11 Q4: the lock is the object at `world+0x918` (`ecx` for `Lock_Acquire 0x4E16F0`). `0x1021CF0` is a constant pushed as its argument.

### How a failure is returned and recorded [code]

- After `RoutingJob` Execute, the function reads the first path in the out list, calls its vtable `+0x20` (status), and returns `status < 3`.
- PlanCommon then calls `0x752620` if the list is non-empty. That maps status 7/8 to `FailedBecauseNoPathToDestination` (16) plus the blocker id, type and location (vtable `+0x160/+0x164`).
- If the function returns 0 with the list still empty, PlanCommon jumps to `0x76156D` and returns its default result: `mType = 2` (**Failed**), all other fields 0, so **no blocker**. The original's own early-outs (no start, no goal, world flags `+0x97E`/`+0x976`) take this path.
- C# side (SimRoutingComponent.cs:1265–1300): with blocker id 0 and location zero, `GetRouteFailPath` returns null, so `DoRouteFailureBehavior` runs no fail-path plans. It still plays the route-fail reaction when `DoRouteFail` and the frequency check allow it, with no target object. The interaction's normal failure branch runs, the same as after the C# `RouteBackoff` fast fail.

### Hook logic (`PlanLockedHook` in `ts3native.cpp`)

1. Count the call. If do-plan is 0, or starts or goals are empty, call the original unchanged.
2. Build the key, a 64-bit FNV-1a hash of:
   - the agent class;
   - all four option words;
   - the follower ObjectGuid;
   - the start count and each start point's x,y,z, floored to **0.5 m** cells, in order;
   - the same for the goals.
   The key is strict on purpose: a miss costs one normal plan, while a false hit returns a wrong failure. No option bits are masked out.
3. If the key failed less than `WindowMs` ago (real time, `GetTickCount64`): **return 0** without the lock, the original, or touching the out list. Callers see a plain Failed with no blocker.
4. Otherwise call the original through the trampoline. If it returned 0 and the first path's status is 7 or 8, store the key with `until = now + WindowMs`. Successes are never stored, and neither are other failures (status 3–6, empty list).

The table is a fixed open-addressed array: `MaxEntries` rounded up to a power of two, 256–65536 slots, allocated once. A key probes 8 slots. A store takes the matching slot, else the first free or expired slot, else evicts the oldest of the 8, so the table never grows. Lookups hold an SRW lock shared and stores hold it exclusive, only around the table, never around the original call. The counters use `Interlocked*`.

### Settings and counters

`ts3native.ini` `[RouteCache]`: `Enabled=0` (no detour when 0, unless `[RouteWorker] Enabled=1`), `WindowMs=2000` (100–60000), `MaxEntries=4096` (256–65536). When the cache is on, `ts3native.log` stays open and gets `route cache: calls N hits N misses N stored N otherFails N` about every 60 s, written by whichever game thread calls the planner first after the minute is up.

Export for test tools: `ts3native_counters`, a data export of 16 int32 (8 in layout version 1, ts3native 0.2.0):

| Offset | Field |
|---|---|
| +0x00 | version (2 since 0.3.0) |
| +0x04 | enabled (1 when the detour is installed) |
| +0x08 | calls |
| +0x0C | hits |
| +0x10 | misses (keyed calls that ran the original) |
| +0x14 | stored |
| +0x18 | otherFails |
| +0x1C | windowMs (0 when the cache is off) |
| +0x20..+0x3C | route worker, see Phase 3 |

Frida: `const c = Module.findExportByName('ts3native.asi', 'ts3native_counters'); c.add(0xC).readS32()`.

### Relation to the C# RouteBackoff (`mods/ts3fixes/src/RouteBackoff.cs`)

The C# hook on `ScriptCore.Route.Plan` fast-fails unseen townies on foot for 30 sim-minutes, per Sim and destination 1 m cell. Those requests never reach native code, so they are not counted twice. The native cache covers what the C# hook leaves alone: selectable and on-screen Sims, pets, horses, non-Sim routers, short hops, native callers (`0xC7BF70`), and repeats within 2 s whose C# key differs.

One interaction: a native hit reaches the C# hook as `Failed` with no blocker, which the C# hook also records (it accepts `Failed` and `FailedBecauseNoPathToDestination`). That only happens when a real no-path failure of the same request occurred less than 2 s earlier. If strict separation is wanted, the C# hook can record only `FailedBecauseNoPathToDestination`, since a real no-path failure always comes back as 16 from `0x752620`.

### Risks

- **Stale failures** [inferred]: a door unlocked, an object moved or sold, a wall removed, or a Sim that was blocking walks away. A request that would now succeed still fails for up to `WindowMs`. Mitigations:
  - The window is short (2 s real time).
  - The key includes the exact start cell, so a Sim that has moved is a new request.
  - There is no routing-world epoch yet. The pending-update flag `[world+0x95C]+0x10E` from docs/11 is unverified, so it is not used.
- **Transient Sim blockers** are cached too. The native hook does not decode the blocker type, because the `+0x164` argument layout is not verified. Sims that re-plan within 2 s after being blocked by another Sim get a plain failure instead of waiting their turn. Watch for this in the test.
- **0.5 m cells**: two starts in the same cell on opposite sides of a thin wall share a key, for 2 s.
- **Hash collision**: 64-bit keys, at most 65536 live entries; negligible.
- Another plugin that hooks `0x82C6E0` first changes the prologue. Ours then skips, logs `SKIP detour Route_PlanLocked`, and leaves `enabled = 0`.

### Test plan

1. Set `Enabled=1`, start the game, and check `ts3native.log` for `OK detour Route_PlanLocked at 0x0082C6E0`. Check the Frida export: `enabled = 1`, `windowMs = 2000`.
2. **Repro**: on a quiet lot, use the agent action `sim_route_to` to an unreachable point, e.g. inside a closed room with no door, or on a roof. Repeat 10 times in quick succession.
   - Expect `stored` +1 on the first try, then `hits` rising for repeats within 2 s.
   - The Sim plays its route-fail reaction at most once, then stands idle and stays controllable. Queue a reachable walk right after: it must succeed at once (different key).
3. **Stale-failure check**: make a point unreachable (lock a door for that Sim) and fail a route to it. Unlock the door, wait more than 2 s, and route again: it must succeed. Within 2 s, a plain failure is expected.
4. **Soak**: 30 min in the stutter save (Isla Paradiso or Sunset Valley) with `Enabled=0`, then the same with `Enabled=1`. Compare against the baseline table:
   - frame-time spikes and route time per `Route.Plan` from the probe;
   - the hit ratio from the counters;
   - `stuck` Sims: any Sim standing still with an interaction queued for more than 1 sim-hour.
   Also log any Sim that keeps re-queuing the same failed interaction. A plain failure that loops would show here.
5. If stuck Sims or loops show up, try `WindowMs=500` before turning the cache off.

## Phase 3: route worker, offload-sync (v0.3.0, `[RouteWorker] Enabled=0` by default)

docs/18 first step: prove the route search runs safely on another thread, with no gameplay change. Built and self-tested 2026-10-13; not run in game yet.

- **Hook**: the same `Route_PlanLocked 0x82C6E0` detour as the cache (`PlanLockedHook`). One detour serves both; it is installed when either `[RouteCache] Enabled=1` or `[RouteWorker] Enabled=1`. Order per call: the original's early-outs run on the caller; a cache hit returns on the caller; everything else (cache miss, or cache off) goes to `RunPlan`.
- **Threading**: one persistent worker thread (`CreateThread`, 1 MB stack reserve), started in `DllMain` (it runs once the loader lock is released). With `workerMode = 1`, `RunPlan` copies the 10 arguments, `ecx` and the caller's x87 control word and MXCSR into one job slot, signals the worker and blocks on an event. The worker sets the same FPU state, calls the original through the trampoline and signals back. One job at a time (a critical section); a call made on the worker itself runs there directly (`fallbacks`). Same arguments, same result, only the thread changes.
- **Watchdog**: every 2 s of waiting logs `route worker: thread N waited S s for worker thread W ...` and counts `watchdogs`; the caller keeps waiting (no unsafe recovery). Any watchdog line is deadlock evidence (docs/18 hazard 2).
- **Settings**: `Enabled` (start the worker, install the detour), `Mode` (start value of the runtime switch, 0 = plan on the calling thread, 1 = on the worker). With `Enabled=1, Mode=0` the detour only passes through until the switch is flipped.
- **Runtime switch**: `workerMode` at `ts3native_counters+0x20` is writable. Probe: `GET /native?routeworker=1` (or `0`); `profile-world.ts --ab routeworker` alternates it per slice.
- **Log**: about every 60 s, `route worker: mode M calls N worker N ms max N us wait N ms fallbacks N watchdogs N`.

| Offset | Field |
|---|---|
| +0x20 | workerMode (writable runtime switch) |
| +0x24 | workerReady (1 when the worker runs and the detour is installed) |
| +0x28 | workerCalls |
| +0x2C | workerUs (total time in the original on the worker) |
| +0x30 | workerMaxUs |
| +0x34 | waitUs (total caller wait; `waitUs - workerUs` = handoff overhead) |
| +0x38 | fallbacks |
| +0x3C | watchdogs |
| +0x64 | invokeHook (v4: 1 runtime_invoke slot hooked, -1 refused, 0 not yet) |
| +0x68 | invokes (v4: managed calls from the worker run on the caller) |

Self-test (`selftest.cpp`, `TestRouteWorker`): mode 0 runs on the caller; mode 1 runs on the worker thread with the caller's x87 control word (24-bit precision set on purpose) and returns each call's own result; a plan started on the worker falls back to a direct call; `doPlan = 0` stays on the caller; with the cache on, a no-path failure found on the worker is stored and the repeat is a hit that never reaches the worker.

Probe for the same step: `GET /routelock?start=1` (opt-in, `stop=1` unhooks) hooks `Route_PlanLocked`, `Lock_Acquire 0x4E16F0` / `Lock_Release 0x4E17B0` filtered on `world+0x918`, and `0x83CED0` (iteration count). It reports acquisitions, wait and hold per thread, plan length percentiles, and the overlap **O**: for each plan, `min(plan length, gap until the sim thread next acquires the routing lock)`. `Lock_Acquire` is engine-wide, so every acquire enters the Frida script; `acquireHooksPerSec` shows that cost. Measure O with `workerMode = 0`.

### Crash 2026-10-13 and fix (v0.4.1, still `Enabled=0` by default)

First in-game use (Bridgeport, `workerMode` switched to 1, agent `sim_route_to`) crashed within about 1 s: `xcpt <pc> 26-10-13 01.11.31.txt`, ACCESS_VIOLATION reading 0x58 at 0x00EA2B03, `ecx = edx = 0`, stack at 0x03B6xxxx.

- **0x00EA2B03** is `mono_class_vtable(domain, class)` (0xEA2AD0, assert `object.c:962 "class"`), at `mov ecx,[edx+0x58]` = `domain->domain_id` with **domain = NULL** [verified, disassembly]. Its caller 0xEA36FF is `mono_object_new` (0xEA36F0 = `mono_object_new_specific(mono_class_vtable(d,k))`) [verified].
- **0x00E5A668** is **not** Boehm: it is the return address in the Mono interpreter's runtime_invoke 0xE5A600 (`interp.c`; `sub esp,150h`, builds a MonoInvocation and calls `ves_exec_method` 0xE59FD0) [verified]. 0xE59FD0 reads `mono_domain_get()` (0xE75260 = `TlsGetValue([0x11959DC])`) into the interpreter context, then 0xE52C70 boxes the non-void return value with `mono_object_new(context->domain, ...)` [verified, return addresses 0xE5A175, 0xE52D22, 0xEA36FF in the stack data]. 0xE5A600 is only reached through the runtime_invoke slot `[0x1195B60]` (set by `mono_install_runtime_invoke` 0xEA1470 from `mono_interp_init` 0xE5A680; 0xEA1080 "not supported" stub before) [verified].
- **Cause**: the route planner calls managed (script) code through `mono_runtime_invoke` during a plan. On the worker, which was never attached to Mono, the domain TLS is NULL, so the first managed call faults. docs/18 checked direct calls only; this path goes through at least one virtual call (no direct-call path from 0x82C6E0 to any runtime_invoke user within 30 levels) [verified]. Thread: the worker [inferred: only an unattached thread has a NULL domain; every game thread that plans is a script thread].
- **Why not attach the worker**: Boehm here is the 6.x win32 variant; its thread table (0x1221000, 256 x 0x14) gets one entry only, from `GC_thr_init` 0xE513D0 -> `GC_new_thread` 0xE51070 (single caller) [verified]. The game has no thread wrapper that registers other threads, and Mono state (thread statics, `MonoThread` TLS 0x1195B7C, the interpreter context TLS 0x11F4160) is per thread. Running script code on a second OS thread is a new, untested mode for this runtime.
- **Fix (0.4.1)**: managed calls go back to the waiting caller thread. On the first mode-1 plan, `EnsureInvokeHook` checks `[0x1195B60] == 0xE5A600` and the prologue `81 EC 50 01 00 00 68 50 01 00 00`, then swaps the slot (`InterlockedCompareExchange`) to `InvokeHook`. Any other value: `invokeHook = -1`, logged, and every plan stays on the calling thread. `InvokeHook` passes all threads but the worker straight to the original. On the worker it posts the call and waits; the caller, which waits in `RunPlan` on `{jobDone, invReady}`, runs the original runtime_invoke on its own thread and signals back. A plan started from inside that managed call goes to the worker the same way (the worker waits on `{invDone, jobReady}`), so exactly one of the two threads runs at a time, as on one thread before. The first 8 distinct MonoMethod pointers are logged (`route worker: planner made a managed call, MonoMethod ...`) to name the callbacks.
- **Counters (v4)**: `+0x64 invokeHook` (1 hooked, -1 refused, 0 not yet), `+0x68 invokes` (managed calls run for the worker). Periodic log adds `invokeHook N invokes N`.
- **Self-test**: refused slot (plans stay inline, slot untouched); hooked slot passes other threads through; a worker plan's managed call runs on the caller with its result returned; worker plan -> managed call on the caller -> nested plan back on the worker.
- **Still open** [inferred]: (1) a managed call made by another route than runtime_invoke (the game's direct interpreter wrappers 0xD7EED0/0xD7F000/0xD7F210, or native glue that allocates with `mono_domain_get()` before invoking, e.g. `mono_string_new`) would still fault on the worker; the crash shows this plan's glue did not. (2) A managed exception that propagates (exc = NULL) longjmps over `RunPlan` on the caller while the worker holds the routing lock: hang, not corruption; the original would unwind through the planner too. (3) A boxed return value lives only on the worker's stack until the plan reads it; a GC in a later managed call of the same plan could free it (Boehm does not scan the worker). The callback is now identified (docs/18 "Managed calls during a plan": `LotManager.PortalLockedCallback` via World event 59 from the portal-edge cost `0x634500`); it allocates, so a GC inside a plan is possible, but `0x78F2B0` copies the boxed result right after the call [inferred].

Unverified: the planner has no thread affinity (TLS, profiler scopes, asserts); the calling thread never already holds `world+0x918` when it calls `Route_PlanLocked` (if it did, the worker would deadlock on the lock, and the watchdog would show it); an access violation inside the search on the worker is not caught by any handler the sim thread may have.

## Phase 4: route search budget (v0.4.0, `[RouteBudget] Enabled=0` by default)

docs/11 Q3 / docs/18 rank 2: one route request can no longer search without limit. Built and self-tested 2026-10-13; not run in game yet.

**Hook** [verified in `astar-676260.asm` and the bytes of `game-copy/bin/TS3W.exe`]: the world A\* `0x676260` loads its cost function into `[esp+0x60]` with one of five `mov dword [esp+0x60], imm32` (`C7 44 24 60 imm32`) at `0x676340/4A/54/5E/68` (travel modes 1-4 → `0x674E40`, `0x673850`, `0x674250`, `0x675800`; default `0x6734E0`). It calls it as cdecl with 8 args, float in ST0. Arg 5 is 1 for an edge cost, which the caller compares against the 1e8 wall (`0x00FEF678`) at `0x676683`, `0x6768A6` and `0x676B24`; it is 0 for the heuristic, which runs once per generated node right before the node is allocated and pushed (plus once per search for the start). The patch swaps the five immediates for stubs (`push orig; jmp CostGate`). This needs no code cave at the back-edge and no open-list drain (the docs/11 Q3 plan).

**How the cap ends a search**:
- The budget is armed per `Route_PlanLocked` request (`CallPlan`, on the thread that runs the original: the caller's or the worker's). The state is thread-local (`__declspec(thread)`, 0.4.2), so a plan on another thread neither reads nor overwrites it; a plan nested on the same thread (from a managed callback inside the search) gets its own budget and the outer one resumes after. It counts heuristic calls (= nodes generated) across every A\* run in the request, its retries included.
- The `MaxUs` clock starts at the first node (0.4.2), after `Route_PlanLocked` has taken the routing lock `world+0x918`, so lock waits do not count. It still includes managed callbacks made during the search (the portal-lock query, docs/18). The counters (`budgetCapUs`, `budgetMaxUsOk`) keep the whole request time, lock wait included.
- Over `MaxNodes`, or over `MaxUs` (checked every 16 nodes), every further edge cost returns 1e9. Nothing new is pushed, the queued nodes are popped (the goal check on pop still runs) and the loop leaves through its own "open list empty" exit `0x676BF7` (frees the heap, returns 0). That is the same exit as any failed search, so nodes go back to the pool as usual.
- Retries in the same request (`0x661B50` per-goal-lot plans, `0x81C67A` obstacle replan) find the budget spent and fail after one pop each.
- The result is the game's ordinary no-path failure (status 7/8 → `FailedBecauseNoPathToDestination` with the best node found as the blocker hint) [inferred from docs/11 Q1: not traced which code the planner returns for a world-leg failure]. No new result code.
- Not capped: the lot/portal A\* `0x63E340` and in-room `0x632E40` (other code), and the C# fail-path plans (`DoRouteFailureBehavior`, 2-4 more requests), each of which is a new request with its own budget.

**Script reaction** [verified in code]: a no-path failure takes each caller's normal failure branch. For unseen townies on foot, ts3fixes `RouteBackoff` then fast-fails the same Sim and destination for 30 sim-minutes (it records only `FailedBecauseNoPathToDestination`, and not when the blocker is a Sim or vehicle). The native route cache stores the capped failure for `WindowMs`.

**"Unseen townies only" cannot be decided natively.** The request has the agent class, follower ObjectGuid and options, but not selectable/on-screen/household state. `OnlyFlagged=1` caps only routes with RouteOption `DebugPathPlan` (hi word bit `0x1`, opts+0x2C), for a later C# caller (RouteBackoff) that sets the bit for unseen townies. Nothing sets it yet, so the default is to cap **all** searches with a generous cap.

**Settings**: `Enabled`, `Mode` (start value of the switch; 1 = cap), `MaxNodes` (0 = off; no calibrated default), `MaxUs` (default 100000), `OnlyFlagged` (0).
- **Default cap, 100 ms per request** (derived, not measured): 4× the Isla Paradiso mean plan (25.4 ms, F7b) and 28× Bridgeport's (3.6 ms). Bridgeport's longest single search was 188 ms, and failed requests cost up to ~250 ms each (4 failed plans in one 1,010 ms iteration). So the cap cuts the failure tail but leaves almost every successful plan alone.
- A node cap is better (deterministic, the same on the worker and under load), but no nodes-per-ms figure exists. Run once with the time cap and `budgetMode = 0`, read `budgetMaxNodesOk`, then set `MaxNodes` above it.

**Runtime switch and counters** (layout v3; `GET /native?routebudget=0|1`; `profile-world.ts --ab routebudget`):

| Offset | Field |
|---|---|
| +0x40 | budgetMode (writable switch) |
| +0x44 | budgetReady (stubs patched and detour installed) |
| +0x48 | budgetCalls (requests run under a budget) |
| +0x4C | budgetCapped |
| +0x50 | budgetCapNodes (sum of nodes when the cap tripped) |
| +0x54 | budgetCapUs (sum of capped request time: search, drain and retries) |
| +0x58 | budgetMaxNodes (any request) |
| +0x5C | budgetMaxNodesOk (largest request that found a path) |
| +0x60 | budgetMaxUsOk (longest request that found a path) |

The log line every 60 s is `route budget: mode … calls … capped … capNodes … capMs … maxNodes … maxNodesOk … maxUsOk …`. `budgetCalls` and the maxima count only requests run with the switch on, so calibrate with the switch on and a high `MaxUs` (for example 2000000), not with mode 0.

**Fail safe**: all five sites are verified before any write (a mismatch logs `SKIP route budget` and nothing is patched). With no budget armed on the current thread, a stub passes straight through to the original (one call and a thread-local flag check per cost call). A search not started from `Route_PlanLocked` is never capped.

Self-test (`TestRouteBudget`): one changed site → nothing written; switch off → passes through; cap above the search → same result and counters; cap of 300 → the next edge is a wall and the fake A\* fails through its own exit with 300 expansions; a search outside a request is untouched; `OnlyFlagged`; time cap; 30 ms before the first node (lock wait) is not capped by a 5 ms `MaxUs` but shows in `budgetMaxUsOk`; a capped plan on a second thread in the middle of ours leaves our budget intact (202 nodes, not capped) and ours still caps at 300; through the route worker the worker's own budget caps the plan and the caller's stays off.

**Test plan** (serial, saves backed up):
1. `Enabled=1, Mode=1, MaxUs=2000000`. Check `OK route budget` in the log and `budgetReady = 1` in `/native`. Play 10 min on `Baseline-IP`, then read `budgetMaxNodesOk` / `budgetMaxUsOk` (calibration) and confirm `budgetCapped = 0`.
2. Repro `sim_route_to` to an unreachable point (closed room or roof) with `MaxNodes` set below the measured failure size: `budgetCapped` +1, the Sim fails as before (one route-fail reaction) and stays controllable; a reachable walk right after succeeds. Then a long legitimate trip (IP cross-water, `bp-route-cache` repro) with the default cap: it must still succeed (`budgetCapped` unchanged).
3. `profile-world.ts --ab routebudget` on `Baseline-IP` and `Baseline-BP`: route ms/s, hitches ≥ 50 ms, fail % and stuck Sims, on vs off.

**Risks** [inferred]: a reachable destination that needs more than the cap now fails, and RouteBackoff then blocks that unseen townie from it for 30 sim-minutes (a long trip for a played Sim fails instead of being slow). A time cap depends on load: a slow machine or a descheduled thread fails more routes. The capped search still pops every queued node, which is cheap but not free. Lot searches stay unbounded.

## Phase 5: async route plan (v0.5.0, `[RouteAsync] Enabled=0` by default)

Built and self-tested 2026-10-13, not run in game. The sim thread no longer waits: a plan from a Sim's own yielding task runs on the worker while the task sleeps (ts3fixes `PlanAsync`). Three more detours (`Lock_Acquire 0x4E16F0`, `IC_Route_Plan 0x765160`, `Route_GetOption 0x74D040`), counters v5 (`asyncMode` writable at +0x6C ... `asyncPumpWaits`). Design, protocol, risks and test plan: docs/18 "Async plan".

## Phase 6: faster resource decompression (v0.7.0, `[Decompress] Enabled=0` by default)

Built and self-tested 2026-10-13, not run in game. Offload candidate #1 from docs/08: resource decompression. Source `native/ts3native/decompress.cpp` (included by `ts3native.cpp`), offline test `decompress-test.cpp`.

### Where decompression happens

| What | Address | Evidence |
|---|---|---|
| RefPack codec object (vtable `0xFB9018`: dtor, compress `0x4EC200`, decompress `0x4EC010`, header check `0x4EC080`, name "Refpack") | `0xFB9018` | vtable dump + decompile [code] |
| Decompress slot: checks the `0x10FB` signature and header size <= buffer, then calls the core | `0x4EC010` (`__thiscall`, `ret 14h`; only reference is the vtable) | decompile + bytes [code] |
| Decode core: cdecl `(dst, dstCap, src, srcLen)`, byte-by-byte copies, bounds-checked, returns the header size after the stop code or 0 | `0x4EB3B0`-`0x4EB74C` | decompile [code] |
| The only call to the core | `0x4EC065` `E8 46 F3 FF FF`, then `add esp,10h` | byte scan of the whole exe for `E8`/`E9` rel32 and absolute refs [code] |
| Users of the codec: PackedFile `0x4A7360`/`0x4A7030` (`ResourceLoad/PackedFile/CompressionRefpack`), MemoryDB `0x72B0A0`, PackedStream `0x72C980`, `0x72AFB0` | | string xrefs + decompile [code] |
| zlib 1.1.3 inflate (`0x4331F0`, "incorrect header check") with callers `0x435384`, `0x43D816`, `0x43DD0F` next to libpng-style errors ("Extra compressed data", "Decompression error") | `0x431A80`-`0x43DDA1` | strings + call scan [code]; PNG use [inferred] |

- **Packages use RefPack only.** All 267 installed `.package` files: 602,169 RefPack resources (11.0 GB compressed, 33.4 GB decompressed), 314,217 uncompressed, **no zlib (`0x5A42`)** [code, index survey]. Half the RefPack bytes are resources of 0.5-2 MB (textures, meshes); the largest is 5.5 MB.
- zlib has no script-reachable caller within 10 calls (`CallPaths` from all 1,716 `IC_*`) and appears in at most a few sim-thread samples per run. Not a target.
- **Which thread:** whichever thread asks for the resource; the decoder has no thread of its own and runs synchronously inside the caller (docs/08: under the global resource lock `0xFB2CD0`). In the 15 native sim-thread profiles in `probe-out/profile/*-simstack.jsonl` (5 Hz, 10-min runs in all three worlds, 9,890 samples), the decoder (return address `0x4EC06A`, with S3SS's code on top) is on the stack in **24 samples = 0.24%** of sim-thread time in live mode (0-4 per run) [code, measured]. Those stacks run through CAS outfit building (`IC_CASUtils_SetOutfitInGameObjectDelayed`) and the TaskObjects stage `0xD7FC60`, so the sim thread does decode synchronously, but rarely. World load, CAS and Buy were not sampled; the counters below measure them.
- **S3SS already replaces the core.** `RefPackDecompressor` (on in our install, docs/05) writes a `jmp` at `0x4EB3B0` to an SSE2/AVX2 version (source `patches/refpack_decompressor_patch.cpp`, read 2026-10-13 at commit 5eb2c65). It copies 16/32 bytes only for literal runs and matches with offset >= 16; short literals (1-3 bytes) and matches with offset < 16 stay byte loops.
- **No splitting.** One RefPack stream is strictly sequential (each match copies earlier output). Parallel work is only possible across resources (docs/08 helper pool). The largest resource decodes in about 2-4 ms, so no single decompression is a hitch on its own [inferred from the offline MB/s].

### What we built

- `RefpackRef`: exact C port of `0x4EB3B0` (same checks in the same order). Reference for the tests.
- `RefpackFast`: same result and the same failure cases, but "wild" copies away from the buffer ends (LZ4 style): 4 bytes for every 1-3 byte literal, 16-byte blocks for literal runs and matches with offset >= 16, 8-byte blocks for offset 8-15, `memset` for offset 1, byte loop for offset 2-7. The overrun stays inside `[dst, dst + min(cap, header size))` and inside the source, and the next command overwrites it. Near the ends it copies exactly. SSE2 only (every CPU that runs Windows 11).
- Hook: the rel32 of the call at `0x4EC065` points to `RefpackHook` (5 bytes verified first, else `SKIP` in the log). We never touch `0x4EB3B0`, so S3SS's `jmp` there stays and is what mode 0 runs; the two patches do not overlap, and the load order of the two ASIs does not matter.
- Runtime switch `ts3native_counters.decompressMode` (counters v7, appended after the v6 save fields: `decompressReady, decompressMode, decompressVerify, decompressCalls, decompressFails, decompressMismatches, decompressMaxUs, decompressInMB, decompressOutMB, decompressMs, decompressMainCalls, decompressMainMs`; `decompressMode` at +0xC4, `decompressVerify` at +0xC8). 64-bit byte/time sums and the per-thread split (first 8 threads) stay internal and go to `ts3native.log`. Mode 0 = the code at `0x4EB3B0` (S3SS or the game), 1 = `RefpackFast`. `decompressVerify=1` also decodes with `0x4EB3B0` into a scratch buffer, compares, counts mismatches and keeps that result (slow, first runs only).
- Log every 60 s (`ts3native.log`): `decompress: mode .. calls .. fails .. in .. MB out .. MB time .. ms max .. us`, then one line per thread (first 8 threads that decode; `(main)` = the thread that loaded the ASI). The probe's sim thread id tells which line is the sim thread.

Settings (`ts3native.ini`):

```ini
[Decompress]
Enabled=0   ; 1 = retarget the call at 0x4EC065
Mode=1      ; start value of the runtime switch
Verify=0    ; 1 = compare every decode with 0x4EB3B0 (slow)
```

### Offline results (2026-10-13, i7-11800H, 32-bit /O2 build) [measured]

`out\decompress-test.exe ["<game dir>"] [MB]` reads a byte-proportional sample of every installed package (read-only), decodes each resource with `RefpackRef` and `RefpackFast`, asserts the same result, the same bytes and 64 untouched guard bytes past the buffer, then times three passes each (best of 3). A third decoder can be compiled in with `/DEXTRA_DECODER` (we used an extract of S3SS's AVX2 path; it is not in the repo).

| 321,378 resources, 502 MB compressed, 3,707 MB out | all sizes | resources < 16 KB (629 MB out) |
|---|---|---|
| Game decoder (C port of `0x4EB3B0`, modern compiler) | 1,277 MB/s | 796 MB/s |
| S3SS `RefPackDecompressor` (AVX2) | 1,502 MB/s | 793 MB/s |
| **`RefpackFast`** | **2,766 MB/s (2.2x game, 1.8x S3SS)** | **1,318 MB/s (1.7x)** |

Identity: 0 mismatches over 321,378 resources; none ends short of its header size; the game's decoder rejects none. A first run sampled per package (98,159 resources, 840 MB out) gave 1.78x vs the port and 1.75x vs S3SS. The selftest adds 3,000 random valid streams with every command type (incl. overlapping matches and the 0-3 byte stop tail) plus 12,000 corrupted ones (flipped bytes, cut input, smaller and larger buffers): same result as `RefpackRef`, same bytes wherever the game's decoder writes, nothing past the buffer. The game's own machine code was also copied out of the exe for a direct benchmark, but it crashed outside the game (not chased), so the "game" row is our C port; the real 2009-compiler code is likely slower, not faster [inferred].

### Expected gain in game [inferred]

- **Live mode:** decoding is 0.24% of sim-thread time; 1.8x faster saves about 0.1% of sim time. Not a stutter fix on its own.
- **Loads, CAS, Buy:** decoded bytes per world load are not measured yet; the ResourceCache holds 389-501 MB after a load (docs/17 §6), so assume 0.5-1.5 GB decoded. At S3SS's 1.5 GB/s that is 0.35-1.1 s of an 84-113 s load; `RefpackFast` saves 0.15-0.5 s (under 1%), on whatever thread loads. Shorter decodes also shorten holds of the global resource lock.
- So this is a small, safe win, and the counters give the first real numbers (bytes and ms per thread during load, CAS and Buy). If they show more than a few seconds of decoding per load, the next step is the docs/08 helper pool (decode across resources on other cores), not a faster single decoder.

### How to test in game

1. Build, then `install --only native-patches`; in `Game\Bin\ts3native.ini` set `[Decompress] Enabled=1`, `Mode=1`, `Verify=1` for the first run. Check the log for `OK   decompress: call 0x004EC065 -> RefpackHook`.
2. Load `Baseline-BP`, play 2 min, open CAS and Buy (`store-cas-browse`). Expect `mismatches 0`, `fails` the same as with mode 0. Then `Verify=0`.
3. Load split: `node tools/harness/offload-phase0.ts --only store-200` once with `Mode=0` and once with `Mode=1` (same cache state: warm both or cold both), compare menu/world load seconds and the `decompress:` log lines (MB and ms per thread; which thread is `(main)` and which is the sim thread from probe `/simstack`).
4. CAS/Buy: repro `store-cas-browse` with mode 0 and 1 (the switch can be flipped in one load through the export, offset +8).
5. Stop on any `MISMATCH` line or a crash in `ts3native.asi`; triage against the change ledger.

### Risks

- A bug in the wild-copy bounds would corrupt or overrun resource buffers. Mitigations: all bounds checks of the original kept, overrun limited to `min(cap, header size)`, selftest fuzzing with guard bytes, 3.7 GB real-data identity check, `Verify=1` for the first in-game runs.
- If a stream ever ends short of its header size (none in the shipped packages; possible in broken CC), the bytes between the real end and the header size may hold scratch instead of untouched heap garbage. The game reads that buffer as the resource either way.
- Another plugin that rewrites the call at `0x4EC065` (none known; S3MM maps `0x4EC010` but a detour at its entry still reaches our call) would make ours `SKIP`.
- The per-call `QueryPerformanceCounter` pair and interlocked counters cost roughly 50 ns per decode (about 1% of a 4 KB decode).

## Phase 7: save (v0.6.0, `[SaveWrite] Enabled=0` by default)

Offload candidate #2. Built and self-tested 2026-10-13, not run in game. Code: `native/ts3native/savewrite.cpp`. Static analysis: Ghidra decompile of the functions below plus `dumpbin` of `FUN_00AB1030` (0xAB1030-0xAB14AB) and `IC_LoadSaveManager_SaveGame_Impl` (0x742A50).

### Where a save goes (sim thread, start to end)

| # | Step | Code | Kind | Off the sim thread? |
|---|---|---|---|---|
| 1 | Pause, `ProgressDialog`, `ThumbnailHelper.UpdateDirtyLotThumbnails`, lot + household thumbnails | C# `OptionsModel.SaveGame` | rendering, live state | no |
| 2 | `LoadSaveManager.SaveGame` -> `LoadSaveManager_SaveGame_Impl` 0x742A50 -> `FUN_00AB1030` (thiscall, 3 args) | native | synchronous; C# waits for the error code | - |
| 3 | Save info and paths (`FUN_00AAE950` x2): working copy `<TS3 user folder>\CurrentGame.sims3`, target `Saves\<name>.sims3`; info+0x430 = `Saves\<name>.sims3\<world>.nhd` | 0xAB10DB, 0xAB10F6 | paths | - |
| 4 | Old single-file save renamed `.legacy` (`FUN_00AACE80`) | 0xAB112D | rare | - |
| 5 | Open the working-copy DBPF (`World/LoadSaveManager/DBPF`, vtable 0xFB2600, open = +0x18) and write the game into it: `FUN_00AAC110` writes the "S3" header, the save listeners (+0xC), the world (`FUN_00C6D460`: lots, objects, script state via the `ScriptCore.LoadSaveManager` callbacks `SaveObjectGroup` / `SaveObject` / `EndSaveObjects` into a native stream = OBJS), the routing world save (`FUN_00834FC0` under the routing lock), then post-save listeners (+0x14) | 0xAB12AF | **live game state + Mono** | **no** |
| 6 | Package commit and close: vtable +0x1C = `FUN_004A8500(1)` writes pending resources (`FUN_004A7910`, vtable +0x3C per record) and the index; +0x08 closes | 0xAB12C1, 0xAB12FD | DBPF write; compression per resource [inferred, compressor not located] | not without rebuilding the DBPF writer |
| 7 | Travel save: stop here (`cmp [esp+315Ch],0; jne`) | 0xAB1319 | - | - |
| 8 | If `<world>.nhd` exists in the target: **backup** `FUN_00AB0F80` -> `FUN_00AAFC50(Saves\<name>.sims3, Saves\<name>.sims3.backup)`: delete the old backup (up to 10 tries, `Sleep(500)` between), recursive `CopyFileW` of the old save (`FUN_004DD030`), `FlushFileBuffers` on every copied file (`FUN_00AAF4B0`) | 0xAB134E | **pure file I/O on the old save**, which nothing writes during a save | **yes (built)** |
| 9 | **Copy the new save**: `FUN_00AB0E80(name, 0)` -> `FUN_00AAFC50(CurrentGame.sims3, Saves\<name>.sims3)`: delete the old save folder (same retry loop), copy the working copy file by file, flush every file | 0xAB1363 | file I/O, needs step 6 done | only with the sim thread waiting (no gain) |
| 10 | Failure path: working copy to `<name>.sims3.bad`, `.legacy` restore | 0xAB1375-0xAB13FF | error handling | - |
| 11 | Write `1a5tf1l3.dat` in the save folder and flush every file in it again (`FUN_00AAF5F0`) | 0xAB1410 | file I/O | no gain |
| 12 | C#: `UIManager.SetSaveGameMetadata`, `DynamicChallengeManager.Save`, close the dialog, restore speed | C# | - | - |

[verified code] for the order, the calls and the copy/flush/retry behavior; [inferred] for the CurrentGame.sims3 location (same base as `Saves`, from the same `vtable+0x24` user-folder call; the folder is gone after the game exits) and for where resource compression happens.

Size of what is copied: `Baseline-BP` is 33.3 MB (31.9 MB `.nhd`, 0.5 MB ExportDB, 0.9 MB TravelDB; docs/09 §6). So every ordinary save copies about **33 MB twice** (old save to `.backup`, working copy to the save) and flushes about 66 MB, plus a second flush pass in step 11.

### Where the save time goes (measured 2026-10-14)

Bridgeport, `Baseline-BP` (35 MB save), three saves of 11.1-13.2 s (`[SaveWrite] Mode=1`). Save 2: native sim-thread stacks (`/simstack?frames=64`, 323 samples; stack-scan unwinder, so only leaf frames and named markers count). Save 3: C# stacks (`/managedstack`, 3,261 samples, time-weighted). Shares are rough (one save each). Native shares were counted per sample, but `/simstack` returns a blocked thread every 2-7 ms and a running one only every ~35 ms, so waits are overweighted ~10x; docs/25 re-weights by time (per-lot `Lot_Save` ~3.6 s of the ~4.1 s native window, footprint save ~1.2 s).

| Part | Time | Evidence |
|---|---|---|
| Script serialization (C#, `LoadSaveManager.SaveGame` -> persisted-object writer) | ~6.3 s (~48%) | [measured] C# sampler. Inside: `ScriptCore.ReferenceHashtable.TryGetValue/TryAdd` (object identity tracking) ~2.5 s, reflection (`Object.GetType`, `MonoType.GetFields`, `MonoField.GetValue`, `RuntimeTypeHandle`, `PersistedClass.InitFieldList`) ~1.3 s, `FastStreamWriter.Write` ~0.6 s, rest interpreter |
| Native world write (start of the save, ~4 s) | ~4-4.5 s | [measured] leaf frames: `FUN_006663c0` (called through `FUN_0063c690`, which also calls the per-lot routing object `FUN_0062eab0`; byte-swapping stream writer `FUN_004ddcc0`) [inferred: lot/routing world data], EA allocator `FUN_004e5e40` |
| Sim thread waiting on another thread | ~0.13-0.24 s (corrected; the count-based ~0.9 s overweighted waits ~10x, docs/25) | [measured] `FUN_004e2760` = `WaitForSingleObject` wait (S3SS hooks it), near `Lock_Release` / `FUN_004a94c0` / `FUN_004ece50` (package code) [verified: decompile of 0x4E2760; owner of the lock unknown] |
| Resource compression | ~0.6 s | [verified] `FUN_004ec0a0` (RefPack header 0x10FB, window 0x1FFFF) -> core `FUN_004ebb90` |
| Disk write, copy and flush (steps 6, 9, 11) | ~0.8 s | [measured] `FUN_00aaf4b0` flush, KERNELBASE at the end of the save; fast NVMe |

**What follows:** the parts a worker could take without the script engine (compression, package write, copy and flush) are only about 1.5 s of ~12 s, and need our own DBPF writer: poor value. Half the save is C# serialization on the sim thread (Mono, cannot move, dead-ends #17). Better levers: fewer persisted objects (save cleanup; the life-event trim removes ~18% of script objects in BP), a cheaper `ReferenceHashtable` (identity lookups ~2.5 s per save) and cached reflection, then the native world write (~4 s, owner functions to be named) and the footprint save scan (docs/25: ~1.2 s, quadratic). Measured 2026-10-14: trimming 26,161 empty life-event lists cut BP saves from 12.0 s to 10.7-11.6 s (two saves each, noisy).


**No save-time numbers exist yet** (test E has never been run). The new hooks measure it: one log line per save with total, package write (step 5, includes the script serialization), backup, copy and final flush.

### What is offloaded (Mode=1)

Step 8 copies the **old** save, so it does not depend on the package write. At step 3 (after `FUN_00AAE950` returns, non-travel, `<world>.nhd` exists) a worker thread copies `Saves\<name>.sims3` to `<TS3 user folder>\ts3native-backup.tmp` (plain Win32: `CopyFileW` per file, `FlushFileBuffers` per file, like the game), while the sim thread runs step 5. At step 8 the sim thread waits for the worker (expected to be done by then), deletes the old `.backup` exactly as the game does (file: `DeleteFileW`; folder: up to 10 tries 500 ms apart) and renames the temp copy to `<name>.sims3.backup` (`MoveFileExW`, `MOVEFILE_WRITE_THROUGH`). The game's copy is skipped and the hook returns 1, which is what the game's step returns after a good copy.

Data safety:
- Nothing is reported earlier than before. Steps 9 and 11 (the new save, its flush) still run on the sim thread before the C# call returns; only the backup of the old save changes thread, and it is complete and flushed before the rename that makes it the backup.
- The temp copy is used only if (a) every copy and flush succeeded, (b) the old save's file names, sizes and write times hash the same at job start, after the copy and again at step 8, (c) the copy hashes the same as the source (`CopyFileW` keeps write times), (d) the backup step's name is the same folder (`<name>` or `<name>.sims3`). Any miss: the temp copy is deleted and the game's own `FUN_00AB0F80` runs, so the outcome is the game's.
- The old `.backup` is deleted only after (a)-(d) passed. A failed rename after that leaves no backup for a moment and then the game's own copy makes it, which is the game's own order (it also deletes before copying).
- A save that never reaches step 8 (write error, travel, cancelled) waits for the worker at the end of `FUN_00AB1030` and deletes the temp copy, so the old `.backup` is untouched, as in the game.
- Paths come from the save info only when they look exactly right (info+0x430 readable, under 260 chars, ends in `.nhd`, the file exists, parent ends in `.sims3`, grandparent in `\Saves`); else no job.
- The temp folder sits next to `Saves` (same volume, so the rename is a metadata change) and outside it, so the game never lists it as a save.

Hook sites (`call rel32` rewritten; every site must be an `E8` to the expected function or nothing is written): 0x742A95 (`FUN_00AB1030`, total), 0xAB10F6 (`FUN_00AAE950`, job start), 0xAB12AF (`FUN_00AAC110`, write time), 0xAB134E (`FUN_00AB0F80`, backup), 0xAB1363 (`FUN_00AB0E80`, copy time), 0xAB1410 (`FUN_00AAF5F0`, final time). Calling conventions checked in the disassembly (`ret 0Ch`, `ret 0Ch`, `ret 8`, `ret 4`, `ret 8`, `ret`).

Settings: `[SaveWrite] Enabled=0` (1 = hooks + timing), `Mode=1` (start value of the runtime switch; 0 = time only, the game backs up). Counters v6 at the end of `ts3native_counters`: `saveReady`, `saveMode` (writable), `saveJobs`, `savePromoted`, `saveFallbacks`, `saveCount`, `saveLastUs`, `saveLastWriteUs`, `saveLastBackupUs`, `saveLastCopyUs`, `saveLastFinalUs`.

Self-test (`TestSaveWrite`, real temp folders): call-site patch refuses a wrong target; worker copy promoted (backup = old save, older backup gone, no temp left, game's backup not called); `<name>.sims3` name accepted; old save changed during the write -> fallback, older backup kept until the game's step; wrong folder name -> fallback; save that stops before the backup -> temp removed, backup untouched; travel save, `Mode=0` and a new save start no job.

### Expected gain [inferred]

The whole backup step leaves the sim thread when the package write takes longer than the copy, which it should (it serializes every script object; docs/09: 188k-303k OBJS records). Copying and flushing 33 MB on this laptop's NVMe should be about 0.1-0.5 s, more with Defender scanning the new `.nhd` or on an HDD. The delete of the old backup (and its retry loop, up to 5 s when a file in it is locked) still runs on the sim thread at step 8, as in the game. The new save's own copy (step 9) and the double flush stay. The numbers decide whether this is worth keeping: compare `backup` between Mode 0 and 1.

### Not offloaded, and why

- Step 5 (package write incl. script state): walks live Mono objects and game state; must stay on the sim thread.
- Step 6 (resource compression and index): runs inside the DBPF writer interleaved with its file offsets; offloading needs a new writer. Update: the compression alone can run ahead on workers without a new writer, see Phase 10 `[SaveCompress]`.
- Step 9 (copy the new save): needs the finished package and must be complete and flushed before success is reported; on a worker the sim thread would only wait. Update: the copy stays, its flush moves to a worker after the save (Phase 10 `[SaveFlush]`, with the failure cases).
- Step 11: small. Its flush pass moves with step 9's (Phase 10).

Next best options (not built): (1) make step 8 a rename of the old save instead of a copy (`<name>.sims3` -> `<name>.sims3.backup`; step 9 then finds no folder to delete). Same crash windows as the game (the game also deletes the save folder before copying the new one), no 33 MB copy at all, no worker. (2) Skip the second flush pass in step 11 for files step 9 just flushed. Both change the game's file operations, so they wait for the step timings.

### In-game test plan

Back up `Documents\Electronic Arts\The Sims 3\Saves` first (whole folder). Then, serially:
1. `[SaveWrite] Enabled=1, Mode=0`. Load `Baseline-BP`, `tests-be.ts` test E (save as `TestE-<tag>`): check `OK   save write: 6 call sites` and one `save:` line in `ts3native.log`; record total, write, backup, copy, final. Save the same save again once (now a backup exists): the backup number is the cost being moved.
2. `Mode=1` (ini or the runtime switch), same steps: `savePromoted` +1 per save, `saveFallbacks` 0, `backup` near 0 when `worker` < `write`; `TestE-<tag>.sims3.backup` exists and `save-stats.ts` on it gives the same counts as on the previous save; no `ts3native-backup.tmp` left in the TS3 user folder.
3. Reload the saved game: loads, household and Sims as before; `save-stats.ts` counts on the save identical to a Mode 0 save of the same state.
4. Load the `.backup` folder as a save (copy it to a new `.sims3` name): it loads.
5. Repeat 1-2 on `Baseline-IP` and `Baseline-SV`; autosave and Save-and-Quit once each in Mode 1.

## Proposed install entry for tools/setup/fixes.json (not applied)

```json
{
  "id": "ts3native",
  "name": "ts3native.asi (our native patches)",
  "author": "this project",
  "category": "engine",
  "version": "0.1.0",
  "source": { "kind": "local-build", "path": "native/ts3native/out", "build": "native\\ts3native\\build.cmd" },
  "install": { "target": "bin", "files": ["ts3native.asi", "ts3native.ini"] },
  "requires": ["asi-loader"],
  "default": false,
  "notes": "Phase 1: GC initial heap 256 MB, growth step 32 MB (docs/13, docs/15). Check Game\\Bin\\ts3native.log after the first start. Remove both files to roll back."
}
```

The `local-build` source kind does not exist in the installer yet; it needs a small branch that copies from `native/ts3native/out` instead of downloading.

## Risks

- **Address space.** The 256 MB initial heap is one contiguous reservation at Mono start, and Boehm never gives heap back. That is up to 216 MB less for DXVK, textures and the world cache (Error 12). Measure committed memory and the largest free block against the baseline. If `GC_expand_hp_inner` cannot get the block, the game exits at start-up with "Can't start up: not enough memory" (no save touched). **[code]**
- **32 MB growth steps** add up to 24 MB extra peak per expansion. **[inferred]**
- **Other plugins** patching the same bytes: whichever runs second sees different bytes and skips (ours logs it). S3SS 1.6.3 does not patch these sites as far as docs/03 §3 lists. **[inferred]**
- The budget unit is probably microseconds (docs/13 Q3). Raising the ceiling costs sim-loop time each frame. **[inferred]**

### Phase 4 in game (2026-10-13): route budget stubs strand Sims - keep OFF

- Functional check: 50 plans through the budget, game stable, unreachable plans fail fast (218 nodes, 2.5 ms) [verified].
- 10-min Bridgeport profile with `[RouteBudget] Enabled=1` (stubs + detour installed; budgetMode alternated 1/0 every 2 min): **41 stuck Sims** on all trip kinds (visits, work, school, Go Home), although only 1 search was capped (`capNodes 16`, `capUs 989189`) [verified, `probe-out/repro/Repro-bp-budget-1724.json`].
- Control, same save and mods, `Enabled=0` (no stubs, no detour), 5 min: **0 stuck** [verified, `probe-out/bp-nobudget.log`].
- So the stubs themselves break routing even with the switch off (suspects: cost/FPU state, stale per-request timer for A* calls outside Route_PlanLocked); code review in progress. `[RouteBudget] Enabled=0` stays the default.
- Phase 3 (route worker) crashed the game on first use: ACCESS_VIOLATION reading 0x58 at 0x00EA2B03 (`mono_class_vtable` with a NULL domain) from 0x00E5A668 (Mono interpreter runtime_invoke, not Boehm) on the worker thread: the planner calls script code (xcpt 26-10-13 01.11.31). Fixed in 0.4.1 by running those managed calls on the waiting caller thread (Phase 3, "Crash 2026-10-13 and fix"); not retested in game; `Enabled=0` stays the default.
- Code review (2026-10-13): the five cost stubs keep the cdecl contract, registers and x87 stack exactly (wall path pushes one ST value like the original; arg5 offsets right; sites are load-only) [verified], so they do not explain the stuck Sims. Real bugs to fix before any retest: (1) `CallPlan` starts the budget clock before `Route_PlanLocked` takes the routing lock, so lock waits count against MaxUs (explains the one cap at 16 nodes / 989 ms) - start the clock at the first node; (2) `g_budget` is one unprotected global - make it thread-local. Next suspect for the 41 stuck Sims: the `Route_PlanLocked` detour itself; test it alone (`[RouteWorker] Enabled=1, Mode=0`, nothing else) for 10 min.
- **Fixed in 0.4.2** (built, self-test ok, not run in game; defaults unchanged, `[RouteBudget] Enabled=0`, `[RouteWorker] Enabled=0`): (1) `BudgetStep` starts the clock at the first node (`if (b.nodes++ == 0) QueryPerformanceCounter(&b.t0)`); `CallPlan` keeps its own request start for the counters. (2) `g_budget` is now `t_budget`, `__declspec(thread)` (implicit TLS in a `LoadLibrary`'d DLL works on Vista+, threads that already exist included). On the worker, `ServePlan` -> `CallPlan` arms the worker's copy; `CostGate` runs on the worker and reads the same copy. The `tid == me` guard is gone: a nested plan on the same thread saves the outer budget, runs with a fresh one, and restores it. Neither fix explains 41 stuck Sims with the switch off; the detour test above is still next.

## Phase 8: per-thread cache in front of EA's arena allocator (v0.8.0, `[Alloc] Enabled=0` by default)

Built and self-tested 2026-10-13, not run in game. Item 2 of docs/20 (the biggest native cost that is not route search: `0x4E5E40` is 6.9% of pooled sim-thread samples, 9.9% in Bridgeport, up to 16% incl. lock waits in `bp-detour2`/`bp-worker1`). Source `native/ts3native/alloc.cpp` (included by `ts3native.cpp`). RE from Ghidra headless decompiles of `0x4E4800`-`0x4E7000`, xrefs and the default allocator's vtable.

### What the allocator is [verified: decompile]

- EA's GeneralAllocator, a close derivative of **dlmalloc 2.7** (Doug Lea): heap objects of 0x518 bytes (a debug variant adds a table up to +0x6A0). Fields: `+0` initialized, `+4` `max_fast` with bit 0 = "fastbins hold chunks", `+8..+0x24` fastbins, `+0x28` bins (unsorted bin = `+0x30`, fd `+0x38`, bk `+0x3C`), `+0x430` binmap, `+0x440` top, `+0x444` last remainder, `+0x448` list of VirtualAlloc'd cores, `+0x488/+0x48C` count/bytes of direct-mapped blocks, `+0x4AC/+0x4B0` out-of-memory callback, `+0x4E4` lock (pointer to a `CRITICAL_SECTION` + recursion count at +0x18, spin count 10, `0x4E48C0`).
- Chunks: 16-byte header `{prev_size, size|flags, fd, bk}`, user pointer = chunk + 0x10, sizes multiples of 16 (`request2size` `0x4E4770`: `(n + 0x1B) & ~15`, min 16). Flags in the size word: bit 0 = previous chunk in use, bit 1 = direct `VirtualAlloc` block, bit 2 and bit 31 = in a fastbin (bit 2 also tags debug chunks). A chunk is in use when the **next** chunk's bit 0 is set (dlmalloc). Free chunks link through fd/bk at +8/+0xC (inside the header), so user bytes are not touched by a free except the last 4 (next chunk's prev_size) when it coalesces.
- Entry points, each "lock, core, unlock": `0x4E69D0` Malloc(size, flags) -> `0x4E5E40`; `0x4E6A20` Memalign(size, align, offset, flags) -> `0x4E63F0` (which calls `0x4E5E40(size, flags)` unchanged when align <= 16 and offset 0); `0x4E6A70` Realloc -> `0x4E6710`; `0x4E5920` Free -> `0x4E51B0`; `0x4E5CB0` Shutdown (also through the jmp thunk `0x4E69C0`). Flag bit 3 sends a request straight to `VirtualAlloc` (`0x4E50F0`). No MSVCR80 import anywhere in `0x4E4800`-`0x4E63E6`, so S3SS's mimalloc (MSVCR80 `malloc`/`free`) never sees these heaps (docs/17 §8.3).
- Heaps: the default allocator (`0x931020`, ICoreAllocator vtable `0xFB8B38`: slot +4 `0x4E3E90` aligned Alloc -> `0x4E6A20`, slot +8 `0x4E3E40` Alloc -> `0x4E69D0`, slot +0xC `0x4E3EF0` Free -> `0x4E5920`) on the shared "MemoryMan" heap (`DAT_011CB864`, shared block id `0xEE9B7413`); `"Services/MemoryMan/MemoryManHeap"` (`0x4E40C0`), `"Audio/Allocator"` (`0x89F4E0`, own heap with an OOM callback), SpeedTree (`0x7AF190`-`0x7BC0D0`, the densest direct callers, own byte counter at `0x11D8628`). Direct call sites: 179 Malloc, 60 Memalign, 7 Realloc, 307 Free, plus everything behind the ICoreAllocator vtable. Threads: any; the callers are frameless, so the sampler cannot name them (docs/20).

### Why it is slow

- **One lock per heap, shared by every engine thread** [verified: code]; lock waits measured at 1.0% of sim-thread samples pooled and 3.4% in `bp-worker1` (docs/20) [measured]. The route worker and the sim thread now allocate from the same heap at the same time, which fits the higher wait with the worker on [inferred].
- **Fastbins stop at 64-byte chunks** (`max_fast = 0x40`, set in `0x4E5710`) [verified], i.e. requests up to 52 bytes. Everything from 53 bytes up goes through the general path: each Malloc first sorts **every** chunk in the unsorted bin into its bin (loop `LAB_004e5f41`, with a linear walk to keep large bins sorted), then searches bins and the binmap [verified: code]. Every non-fast Free coalesces and pushes onto the unsorted bin, so a burst of frees is paid for by the next Malloc [verified: code].
- **Any request >= 512 bytes while fastbins hold chunks runs `malloc_consolidate` `0x4E5960`** over all fastbins (coalesce each chunk, push to unsorted), and the next unsorted loop sorts them all again [verified: code]. Interleaved small frees and >= 512-byte allocations (strings, vectors, route buffers) pay this again and again [inferred: the profiles attribute samples to `0x4E5E40` itself, and we have no split between the loops].
- Frees that leave a free block >= 64 KB check whether the top of a core can be decommitted (`0x4E4B30` -> `0x4E4F90`, `VirtualFree(MEM_DECOMMIT)`), recommitted later [verified: code; cost in game not measured]. No page commits on the normal Malloc path.
- Not a linear free-list search in the fast case: small bins are exact-size and O(1); the cost is the sorting and consolidation work plus the lock [verified: code].

### What we built

A thread-local cache of freed chunks in front of the locked entry points (tcache-like): **Free** of an in-use, not direct-mapped, not fastbin/debug-tagged chunk of 16..1024 bytes pushes it onto this thread's bin for (heap, chunk size); **Malloc** and **Memalign with align <= 16, offset 0** of the same chunk size pop it. No lock, no `0x4E5E40`, no unsorted sort, and fewer chunks in the fastbins, so fewer consolidations.

Semantics kept [verified: code + selftest]:
- A cached chunk stays in use from the heap's point of view: its header, the next chunk's in-use bit and every user byte stay as Free got them. The link lives at `p-4` (the chunk's `bk`, the word the fastbin uses too). Realloc, memalign, the debug layer, heap walks and coalescing of neighbours all see a live allocation.
- Only exact chunk sizes are reused, and `0x4E5E40` itself returns exact sizes (remainders of 16+ bytes are always split), so the usable size the caller gets is the same as the game would give.
- Alignment: every user pointer is 16-aligned; memalign requests with a larger alignment or an offset (ZoneObject: align 4, offset 16) go to the game.
- Flag bit 3 (direct `VirtualAlloc`) and sizes above 1 KB go to the game. A Free that fails the in-use check (e.g. a double free) goes to the game exactly as before.
- Heap shutdown: the `0x4E5CB0` detour records the heap in a 16-entry ring and bumps a generation; each thread, on its next call, forgets chunks of a shut-down heap (never freed into freed memory) and hands the others back. More than 16 shutdowns between two calls of one thread: it forgets everything (bounded leak).
- Thread exit: an FLS callback (where the CRT frees its per-thread data) hands the thread's chunks back through the game's Free and releases its quota.
- Out of memory: if the game's Malloc/Memalign returns NULL, this thread's chunks of that heap go back and the call is retried once.
- **Address space:** nothing is reserved. Cached chunks are already-committed heap memory: at most `ThreadKB` (64 KB) per thread, `PerBin` (32) per size, and `MaxMB / ThreadKB` threads (4 MB / 64 KB = 64 threads) get a cache at all; the rest run uncached. Per-thread bookkeeping is 2.1 KB from the process heap. The engine has no allocator budget counter for these heaps on the Malloc path (only direct-mapped bytes at `+0x48C`) [verified], so no accounting changes; heap reports count cached chunks as used.

Settings `[Alloc]`: `Enabled=0`, `Mode=1` (start value of the runtime switch `ts3native_counters.allocMode`, offset +0x108), `ThreadKB=64`, `MaxMB=4`, `PerBin=32`. Install is all four detours or none (prologue bytes of `0x4E69D0`, `0x4E6A20`, `0x4E5920` are `56 57 8B F9 8B B7 E4 04 00 00`, `0x4E5CB0` `55 8B EC 83 EC 14`; no jumps into them). Counters v9 (+0x104): `allocReady, allocMode, allocHitsK, allocMissesK, allocCachedK, allocPassedK` (units of 1024, published every 1024 calls per thread), `allocThreads, allocFlushed, allocDropped, allocShutdowns, allocOomRetries`; one `alloc cache:` log line per minute; probe `nativeCounters` reads them.

### Offline results (2026-10-13, i7-11800H, 32-bit /O2 build) [measured]

Selftest against a fake heap that lays chunks out like the game's: hit/miss per size and heap, memalign rules, every pass-through case, per-bin and per-thread caps, mode 0, shutdown (drop vs hand back), OOM retry, thread exit, quota refusal. A cache hit costs **~16 ns per free+malloc pair** (incl. `FlsGetValue`), against **~50-57 ns for an uncontended `CRITICAL_SECTION` around a trivial bump allocator**, i.e. before any of `0x4E5E40`'s own work. The game's own code cannot run outside the game, so there is no offline number for the real path.

### Expected gain in game [inferred]

Hit rate decides it and is unknown: game code tends to free and reallocate the same few sizes, so 60-90% hits on the sim thread is plausible. At that rate most of the allocator's 6.9-16% of sim-thread time and its lock waits go away: roughly **4-10% of sim-thread time**, more on the busy BP worker runs. Realloc (7 sites), aligned/offset memalign, > 1 KB requests and threads without a quota keep the old cost.

### How to test in game

1. Build, `install --only native-patches`, `[Alloc] Enabled=1`. Log must show `OK   alloc cache: mode 1, ...` and four `OK   detour EA heap ...` lines.
2. Bundle run in Bridgeport (`bp-route-cache`, 5 min): no crash, stuck count as baseline, `alloc cache:` line with `hits` well above `misses`, `oom 0`, `threads` well under the max.
3. A/B in one load: flip `allocMode` (+0x108 of `ts3native_counters`) 0/1 with `/simstack` on, compare the `0x4E5E40` + `0x4E69D0`/`0x4E6A20` share and lock-wait frames.
4. Address space: M3 largest free block at the save vs the same run with `Enabled=0` (expect at most a few MB difference). `store-200` load to make sure the busy load phase is fine.
5. Stop on any crash in `ts3native.asi` or a heap corruption crash in `0x4E5E40`/`0x4E51B0`; triage against the change ledger.

### Risks

- **Game memory bugs change shape:** a double free of a cacheable chunk is no longer caught by anything (the game's own fastbin did not catch it either) and can hand one chunk to two owners; a use-after-free still reads the same bytes (we write only the header word). A pointer freed through the wrong heap was already corruption; now it is reused by the wrong heap's Malloc instead.
- A heap shut down while another thread is still inside Malloc/Free of it was already a game race; our ring only covers shutdowns seen before the thread's next call.
- Up to `MaxMB` of free memory sits in caches; an out-of-memory retry flushes only the calling thread, so Error 12 could come slightly earlier on a process that is already at the edge (bounded by 4 MB).
- Thread-exit flush takes the heap lock inside the FLS callback (as the CRT does); a deadlock needs a lock holder that waits on that thread's exit.
- More than 4 heaps on one thread: the 5th is not cached (`allocPassedK`). Only `0x4E69D0`, `0x4E6A20` and `0x4E5920` are covered; code that calls `0x4E5E40`/`0x4E51B0` directly (debug layer, realloc internals) is unaffected and stays correct because cached chunks look allocated.
- S3PP `AllocatorArenaTuning` patches immediates in `0x4E48F0`/`0x4E5AA0` (core sizes), not these entries: compatible [verified sites]. Another mod detouring these entry points makes ours `SKIP` all four.

## Phase 9: faster water-grid jump-point search (v0.9.0, `[WaterJps] Enabled=0` by default)

Built and tested offline against the game's own code 2026-10-13, not run in game. Item 1b of docs/20: the water JPS `0x620560`/`0x61FE10` is 7.8% of Isla Paradiso sim-thread samples (0% in BP/SV). Source `native/ts3native/waterjps.cpp` (included by `ts3native.cpp`), offline test `waterjps-test.cpp`.

### What the code does [verified: decompile + disassembly]

- Caller chain: `0x65E0D0` -> `0x671930` ("PathPlan/path", "Routing/waterPath") -> `0x620560(g1, g2, w, h, costScale, &start, &goal, &outPath, cb)`. `g1` and `g2` are EASTL `vector<u8>` grids picked from the routing world (`+0x7B0 + 16*i` by boat/swim type, `+0x7C0 + 16*j`), `w`/`h` at world `+0x7A0`/`+0x7A4`. `g1 != 0` = water the route may use; `g2 != 0` = a cost layer: such cells are always jump points, and an edge touching one costs `costScale` (`DAT_00FBB870` = 40.0) per cell instead of `DAT_0107A538` = 1.0.
- `0x620560` is a plain JPS A*: a binary-heap open list ("PathPlan/tOpenListContainer", EASTL push/pop heap `0x61F830`/`0x6EB480`), a hash map cell -> {g, parent} ("PathPlan/extraData"), a `w*h` byte closed set allocated and zeroed per call ("PathPlan/closed"), octile heuristic `0x620120`, Euclidean/Manhattan edge cost `0x61FFA0`. For each popped node it computes the parent direction (`0x61F7E0`), the natural plus forced neighbours (`0x61FA10`), and calls the jump `0x61FE10` once per direction.
- The jump `0x61FE10` (cdecl, 9 args, plain `ret`): step one cell (`0x61F750`, index arithmetic: `cell - w`, `+1`, `+w`, ... so a step east off the last column lands on column 0 of the next row), `n / w` and `n % w` (two `div`s), bounds check, then `n == goal`, forced test `0x61FA10`, `g2[n]` (`0x61F9E0`); a diagonal step first runs both straight jumps (recursive calls), and then **recurses for the next cell**. So one stack frame per cell scanned. A flag argument (start cell not water) makes it return the first step without scanning.
- The forced-neighbour test `0x61FA10` first calls the route callback's vtable slot 1 (`0x66DC50`: is the cell within 3 cells of the start or the goal, Chebyshev), then up to four `0x61F890` (neighbour coordinate, a switch) + `0x61F9A0` (open = `g1 && !g2`, coordinate bounds check, no row wrap) pairs and three `g2` neighbour reads.

### Why it is slow [measured: IP samples]

Of the 367 Isla Paradiso sim-thread samples with the JPS on the stack, the leaf is the jump `0x61FE10` 105, `0x61F9A0` 86, `0x61FA10` 85, `0x61F890` 34, `0x61F9E0` 11, `0x61F750` 8, callback `0x66DCxx` 7: **~92% is the per-cell chain** (about ten calls, two divisions and an indirect call per cell). Open list, hash map and closed-set allocation are a few samples. Not a missing pruning or a repeated grid scan: the algorithm is ordinary JPS; the per-cell constant is the cost.

### What we built

`WaterJump`: the same jump as one loop, every helper inlined, no division per cell (x, y tracked next to the index, with the same row wrap), interior cells tested with plain index offsets and no bounds checks, the game's callback `0x66DC50` inlined (any other callback is still called once per forced test through its vtable). Straight scans are a loop; a diagonal step runs the two straight scans and continues; the stack use is fixed. Results are identical by construction and by test, including the row-wrap quirk.

Hook: only the search's own call at `0x6207FB` (`E8` to `0x61FE10`) is retargeted to `WaterJumpHook`; the game's function is untouched and stays the mode-0 path. Install checks the call bytes and an FNV-1a of `0x61F750..0x61FF93` (every function the jump runs, `0x5A2C1E2B`) and of `0x66DC50` (`0x585FD97D`); a mismatch skips the patch (or, for the callback only, calls it through the vtable). Passed to the game's jump: mode 0, `dir` byte > 7, `w < 2` (a diagonal step is +0 there and the game recurses until the stack overflows), `w * (h + 2) >= 2^31`, `cell >= w*h`.

Not done: a result cache or JPS+ jump tables. A jump depends on the goal and on the callback's start/goal boxes, so (cell, dir) results do not carry over between searches, and precomputed jump distances per grid would need an invalidation point for `+0x7B0`/`+0x7C0` (houseboats, terrain edits) that we have not found [inferred].

Settings `[WaterJps]`: `Enabled=0`, `Mode=1` (runtime switch `ts3native_counters.waterJpsMode`, +0x134), `Verify=0` (+0x138: mode 1 also runs the game's jump, counts and logs mismatches, uses the game's result). Counters v10 at +0x130: `waterJpsReady`, `waterJpsMode`, `waterJpsVerify`, `waterJpsCallsK`, `waterJpsCellsK`, `waterJpsMismatches`, `waterJpsFallbacks`; `agent.js` reads them for `version >= 10`.

### Offline results (2026-10-13, i7-11800H, 32-bit /O2 build) [measured]

`out\waterjps-test.exe ["<path>\TS3W.exe"] [grids]` copies `0x61F000..0x621000` and `0x66D000..0x66E000` from the exe (read-only) to the same addresses in the test process (the code only calls itself; its switch tables are in those pages) and runs the game's jump and `WaterJump` on the same inputs: random grids 2..161 x 1..160 with islands, open sea, sparse and patchy `g2`, every start cell (a sample on big grids) x 8 directions x flag 0/1 with junk in the flag's upper bytes, random or no goal, and three callbacks (none, the game's `0x66DC50`, another one through the vtable whose calls are counted). **1000 grids: 43.9M jumps, identical results and identical callback call counts.** Timing on a 512 x 512 sea with 300 islands and 0.1% `g2`, 400k jumps from random water cells (138 cells per jump on average), best of 3: **game 2.21 s, WaterJump 0.56 s, 3.9x**.

Found on the way: on a grid 1 cell wide the game's jump recurses forever (stack overflow), and on large open grids its recursion depth is the number of cells scanned (a row-wrapping scan can go `w*h` deep); the test runs on a 256 MB stack for that reason. Whether the game's water grids are ever wide open to the border is not known [inferred].

`selftest.exe` adds hand-checked jumps on tiny grids (goal, cost-layer neighbour, flag, the row-wrap case) and the mode-0/fallback wiring.

### Expected gain in game [inferred]

~92% of the JPS time is the per-cell chain and it runs 3.9x faster offline, so the JPS share drops by about 0.92 x (1 - 1/3.9) = 68%: in Isla Paradiso about **5 points of the 7.8%, ~50 ms per second of sim-thread time** (40-55 ms/s if the real grids behave differently from the synthetic one). Long boat/swim searches get shorter in the same proportion, which also helps the async route plan later (a shorter job). BP and SV have no water JPS samples: no change there.

### How to test in game

1. Build, `install --only native-patches`, `[WaterJps] Enabled=1 Verify=1`. Log must show `OK   water jps: call 0x006207FB -> WaterJumpHook, mode 1 verify 1, callback 0x0066DC50 inlined`.
2. Isla Paradiso bundle run with boats and swimming in it (route repro actions for a boat trip and a swim): `waterJpsCallsK` grows, `waterJpsMismatches 0`, `waterJpsFallbacks 0`, no `MISMATCH` lines.
3. Then `Verify=0` and an A/B in one load: flip `waterJpsMode` (+0x134) 0/1 with `/simstack` on and compare the `0x61FE10`/`0x61FA10`/`0x61F9A0` family share (docs/20 method).
4. Stop on any crash in `ts3native.asi` or in `0x620560`; triage against the change ledger.

### Risks

- A wrong jump point would change boat/swim routes (not memory safety: the loop only reads the two grids inside `w*h` and the callback's 16 bytes). Mitigated by the identical-result test on the game's own code and by `Verify=1` for the first runs.
- Another mod patching the jump's code or the call at `0x6207FB` makes ours `SKIP`.
- The counters use two interlocked adds per jump (~10-20 ns against ~1.4 us per jump).

## Phase 10: save compression and save flushes off the sim thread (v0.10.0, `[SaveCompress]` and `[SaveFlush]` `Enabled=0` by default)

Built and tested offline 2026-10-14, not run in game. Targets two parts of "Where the save time goes" (Phase 7): resource compression (~0.6 s) and the disk flushes (part of the ~0.8 s disk time). Sources `native/ts3native/savecompress.cpp` (compression) and `savewrite.cpp` (flushes); offline test `savecompress-test.cpp`, shared test fake `savecompress-fake.h`.

### How the commit compresses [verified: decompile + disassembly]

| What | Address | Evidence |
|---|---|---|
| Package commit `FUN_004A8500(this, 1)` (vtable `0xFB2600` +0x1C = `0x4A8D20`, which calls it) calls the record loop | call at `0x4A850F` `E8 FC F3 FF FF` -> `0x4A7910` (`__fastcall`, ecx = package, returns al) | disassembly |
| Record loop `FUN_004A7910`: while the pending count `+0x2FC` is not 0, `vt+0x3C(*(leftmost+0x20))`; leftmost = `+0x2F0` | `0x4A7910` | disassembly |
| Pending map: EASTL rbtree, anchor `+0x2EC` (right, left = leftmost, parent = root), count `+0x2FC`; node `+0` right, `+4` left, `+8` parent, `+0x10` key, `+0x20` record; increment `FUN_004E0B20` | | decompile |
| `vt+0x3C` = `FUN_004A8E00`: takes `Lock_Acquire(pkg+0x270)`, finds the node, record class `+8 == 0x12E4A892` -> `FUN_004A8910`, then erases the node | `0x4A8E00` | decompile + disassembly |
| `FUN_004A8910` (per record): if `+0x20` (compression) != 0 and the size is `0x32..0xF42400`: stream `record+0x28` (`vt+0x1C` size, `vt+0x28` seek(0,0), `vt+0x30` read(buf, size)) into a `RecordData` buffer from the package allocator `+0x10`, then `FUN_004A82C0` -> `FUN_004A7030` | `0x4A89AE`-`0x4A8A55` | disassembly |
| `FUN_004A7030`: builds a RefPack codec (`0x4EBFD0`: vtable `0xFB9018`, allocator at +4), asks the bound `slot1(src, len, 0, 0, 1)` = `len*1.25+32`, allocates it ("PackedRecord"), compresses with `slot1(src, len, dst, bound, flags)`; flags: `+0x20 == 1` -> `0x10001` (1 on the codec-plugin path), `== 3` -> 2, else 2 (len <= 250,000) or 1. With codec plugins (`+0x390`) it first runs a size-only pass `slot1(src, len, 0, 0, 2)` | `0x4A70A8`-`0x4A727C` | disassembly |
| Codec slot 1 `0x4EC200` (thiscall, `ret 14h`): mode = flags bit 1 ? 1 : bit 16 ? 2 : 0; dst 0 with bit 0 -> the bound; else `0x4EC0A0(dst, cap, src, len, *(codec+4), mode)` | `0x4EC200`; vtable slot `0xFB901C` holds `0x4EC200` | disassembly + exe bytes |
| Compressor `0x4EC0A0` (cdecl): header `0x10FB` (`0x90FB` for len >= 16 MB), mode bit 0 clear -> `|0x4000` and window `0x3FFF`, else window `0x1FFFF`; scratch: hash `0x40000` bytes (len > 16 KB) and chain `window*4+4` from the allocator passed in (`vt+8`, freed with `vt+0xC`), a 1 KB `_alloca` table for len <= 16 KB; cores `0x4EBB90` (> 16 KB) and `0x4EB750`; `dst` 0 = count only; `cap` is never checked | `0x4EC0A0`-`0x4EC1F2` | disassembly |
| No global data in `0x4EB340`-`0x4EC282` (outside the decoder): only memcpy/memset through the MSVCR80 imports and the alloca probe | | disassembly scan |

So the compressor is re-entrant when it gets its own allocator: it reads only its arguments and writes only `dst` and its scratch.

The save package uses `+0x20 == 1`: of the 3,348 compressed records in `Baseline-BP`, the game's own code reproduces all 3,348 stored byte streams exactly (3,256 with mode 2 = `0x50FB`, 75 with mode 1 = `0x10FB`, 17 where mode 0 gives the same bytes); 4,402 of 4,402 in `Baseline-IP`, 3,496 of 3,496 in `Baseline-SV` [verified offline]. The 75 `0x10FB` records (type `0x01661233`) do not fit the flags rule above for this package; they may come from another writer [inferred]. If the game compresses them in the save, they show up as misses.

### What we built: `[SaveCompress]`

- Hook 1: the call at `0x4A850F` (verified `E8` to `0x4A7910`) -> `ScCommitHook`. With `compressMode` 1, compression on (`+0x20 != 0`), the package writable (`+0x14` bit 1) and records pending, it starts a batch: under the package lock (`Lock_Acquire 0x4E16F0(pkg+0x270)`) it walks the pending map from the leftmost (the order the loop commits in); for each record of class `0x12E4A892` with 4 KB to 15.26 MB of data it does the seek(0,0) and read the game is about to do, into a private copy, and queues a job with the predicted mode. Then it calls the game's loop.
- Workers (`Threads`, default 6, started at the first batch, 256 KB stacks) run the game's compressor `0x4EC0A0` on the copy into a private output buffer (`len*1.25+1056`), with an allocator object of our own (two cached `HeapAlloc` blocks per worker, made big enough before the call, since the game's code does not check its allocations). No game allocator, no game lock, no Mono.
- Hook 2: vtable slot `0xFB901C` (verified to hold `0x4EC200`) -> `ScSlotHook`. Outside a batch, on another thread, for bound calls and for records under 4 KB it is the game's slot. In a batch it looks for a job with the same length, the same mode (from the call's flags) and the same bytes (`memcmp` of the whole input), waits for it (or runs it itself if no worker took it yet), and copies the output to `dst` if it fits `cap`; a size-only call gets the length and keeps the job for the real call. No job, a failed job, another mode or output > cap: the game's slot runs (a miss). So the bytes written are always the game's compressor's bytes.
- Window: copies + outputs in flight are capped at `MaxMB` (32); each answered call frees its job and refills from the map (records already committed are skipped by count: the node `walked` places after the start's leftmost is `walked - committed` places after the current one). A record larger than the window still gets a job when nothing else is in flight.
- One batch at a time; a commit inside a commit or on another thread runs plain. At the end of the commit, queued jobs are cancelled, running ones waited for, and all buffers freed.
- Install checks: FNV-1a of the compressor code `0x4EB340-0x4EB3B0`, `0x4EB750-0x4EBFD0`, `0x4EC0A0-0x4EC282` = `0x86B12C94` (Steam 1.67; the decoder range that S3SS patches and phase 6's call site are left out), the call bytes at `0x4A850F`, the slot value. Any mismatch: `SKIP`, nothing written.

Settings `[SaveCompress]`: `Enabled=0`, `Mode=1` (runtime switch `compressMode`, +0x150), `Verify=0` (+0x154: an answered call also runs the game's slot into `dst`, compares, counts `compressMismatches` and returns the game's result), `Threads=6` (1-8), `MaxMB=32` (4-256). Log per commit with jobs: `save compress: N records, J jobs (MB), hits, misses, run here, mismatches, commit ms, wait ms, peak MB`.

### What we built: `[SaveFlush]`

The step 9 copy (`FUN_00AAFC50`: delete the old save folder, `CopyFileW` the working copy, then `FUN_00AAF4B0` = `FlushFileBuffers` on every file) and step 11 (`1a5tf1l3.dat`, then the same flush over the save folder) stay on the sim thread; only the two flush calls move:
- Call sites `0xAAFD8A` (in `FUN_00AAFC50`) and `0xAAF737` (in `FUN_00AAF5F0`), both `E8` to `0xAAF4B0` (cdecl, path) -> `SaveFlushHook`. While the `[SaveWrite]` hooks of step 9 (`0xAB1363`) and step 11 (`0xAB1410`) run on the saving thread and `flushMode` is 1, the hook records the path instead of flushing. The same function called anywhere else (the game's backup fallback, the load copy, the failure path) flushes as before.
- After `FUN_00AB1030` returns, one worker replays the recorded calls in order (same files; opened `GENERIC_WRITE` with read/write/delete sharing). If the thread cannot start, the game's flushes run there, as before.
- Waits: the next save (also autosave and the travel save, all through `SaveGameHook`) and the load copy (call `0xAB16ED`, `FUN_00AB0E80(name, 1)`, save -> working copy) wait for the worker first, so the game never deletes, renames or copies a save folder the worker still has open.

Not moved: the copy itself. Its source is the working copy `CurrentGame.sims3`, which the game keeps using after the save; we have not found every writer of it, so a background copy could take bytes from after the save. The copy from the file cache is the cheap part; the flushes write the 33 MB to the disk [decided; the split is measured by the `copy`/`final` numbers below].

Failure cases (the game's own order: step 8 flushes the `.backup`, step 9 deletes the old save and copies the new one):
- Game crash or quit (`ExitProcess` ends the worker) after the save returned: no loss. Every byte was written into the OS file cache before the game reported success, and Windows writes it out on its own.
- OS crash or power loss before the worker finished (well under a second to a few seconds after the save): files of the new save may be incomplete on disk, and `1a5tf1l3.dat` (written in step 11) may reach the disk before the `.nhd`. The `.backup` folder was flushed in step 8 and is intact. With the game's order the same event during the save itself loses the save folder too; the window is longer now.
- A reader that opens a save file without write sharing (for example the load menu reading `Meta.data`) at the moment the worker flushes that file gets a sharing error. Before any load, the load copy waits for the worker.

Settings `[SaveFlush]`: `Enabled=0` (needs `[SaveWrite] Enabled=1`, else `SKIP`), `Mode=1` (runtime switch `flushMode`, +0x180). The `save:` line ends with `flushes deferred N`; the worker logs `save flush: N calls, F files flushed on the worker, fails, ms`.

Counters v11 (+0x14C): `compressReady, compressMode, compressVerify, compressBatches, compressJobs, compressHits, compressMisses, compressMismatches, compressInMB, compressWaitMs, compressLastMs, compressPeakMB, flushReady, flushMode, flushDeferred, flushJobs, flushWaits, flushWaitMs, flushLastMs, flushFails`; `agent.js nativeCounters` reads them for `version >= 11`.

### Offline results (2026-10-14, i7-11800H, 32-bit /O2 build) [measured]

`out\savecompress-test.exe "<path>\TS3W.exe" "<Saves>\<name>.sims3\<world>.nhd" [threads] [MaxMB] [+0x20]` copies the game's compressor (`0x4EB000-0x4ED000`) from the exe to its own address, points its three CRT calls at this process's CRT, decodes every record of the save, then checks:
1. determinism: 507-653 records per save compressed with scratch tables full of junk and of zeros, plus the count-only pass: identical bytes and length;
2. stored bytes: all compressed records of `Baseline-BP`, `-IP` and `-SV` reproduced exactly (above);
3. pipeline: the whole package as one commit through a fake package with the game's layout (`savecompress-fake.h`), mode 0 (the game's slot, serial) vs mode 1: every record byte-identical, 0 misses.

| Save | records | MB in | serial commit | 6 workers, MaxMB 32 | peak in flight |
|---|---|---|---|---|---|
| Baseline-BP | 3,511 | 139.2 | 1,086 ms | 328 ms (3.3x) | 38 MB |
| Baseline-IP | 4,546 | 117.3 | 903 ms | 253 ms (3.6x) | 36 MB |
| Baseline-SV | 3,598 | 109.5 | 893 ms | 274 ms (3.3x) | 37 MB |

The serial number includes the test allocator filling scratch tables; the in-game compressor share is ~0.6 s (Phase 7 table). What stays on the committing thread in mode 1: reading the records (the game reads them again), the copy, the `memcmp` of each input and the output copy, about 0.1 s for 140 MB [inferred].

`selftest.exe`: fake compressor and fake package: identical output with and without workers (240 records, 1 MB window, many refills), misses when the commit uses another mode than predicted, size-only call plus real call served by one job, verify counts mismatches and keeps the game's bytes, no batch without compression, the hook outside a batch is the game's slot; flushes recorded and replayed (6 files), no flush by the game during the save, the next save and the load copy wait, a gone folder counts a fail, mode 0 and "not ready" flush as before.

### Expected gain [inferred]

Compression ~0.6 s -> ~0.15-0.2 s on the sim thread (copy, compare, waits on the largest records; the 9 MB record alone takes ~40 ms on one worker). Flushes: the step 9 and step 11 flush passes leave the sim thread; their share of the ~0.8 s disk time is not measured yet (`copy` and `final` in the `save:` line, mode 0 vs 1). Together roughly 0.5-1 s of a ~12 s Bridgeport save.

### How to test in game

Back up `Documents\Electronic Arts\The Sims 3\Saves` first.
1. Build, `install --only native-patches`; in `Game\Bin\ts3native.ini`: `[SaveWrite] Enabled=1`, `[SaveCompress] Enabled=1 Mode=1 Verify=1`, `[SaveFlush] Enabled=1 Mode=1`. Log: `OK   save compress: commit 0x004A850F, codec slot 0x00FB901C -> ScSlotHook, ...`, `OK   save flush: 3 call sites`, `save flush settings: Mode=1`.
2. Load `Baseline-BP`, save as `TestSC-on`. Expect a `save compress:` line with `mismatches 0` and `hits` close to `jobs`, a `save:` line ending `flushes deferred 2`, and `save flush: 2 calls, ... 0 fails`; counters `compressMismatches 0`, `flushFails 0`.
3. Proof of identical output: `Verify=1` with `mismatches 0` over whole saves (every answered record also compressed by the game and compared). Optional cross-check: pause, save as `TestSC-on`, set `compressMode` (+0x150) and `flushMode` (+0x180) to 0 through the probe, save as `TestSC-off` right after without unpausing, and compare the two `.nhd` files record by record (`save-stats.ts` counts equal; records with equal uncompressed bytes have equal compressed bytes).
4. `Verify=0`, save again: compare `write` (the commit is inside it), `compressLastMs` and `compressWaitMs` between mode 0 and 1, and `copy` / `final` between `flushMode` 0 and 1.
5. Reload `TestSC-on`: it loads, same household and Sims. Save twice in a row (the second may wait: `flushWaits`), and load another save right after saving (the load copy waits).
6. Stop on any `MISMATCH` line, a crash in `ts3native.asi` or a save that does not load; triage against the change ledger.

### Risks

- A wrong answer from a job would write wrong bytes into the save. Mitigations: a job is used only for the same length, mode and full input bytes; the code is the game's own compressor (hash-checked); `Verify=1` for the first runs; the offline test reproduces every stored record of three saves.
- The read-ahead calls the record stream's seek and read on the committing thread before the game does; the game seeks to 0 again before its own read [verified], so a memory stream gives the same bytes. A stream with side effects on read would break this [inferred: none seen; only class `0x12E4A892` records are read].
- Address space at save time: up to `MaxMB` (more only while one record alone is over the window, up to 2.25x its size) plus ~0.8 MB scratch per worker; 38 MB peak offline with the defaults.
- If the walk order were not the commit order, jobs would not be found: misses, the game compresses; slower, not wrong.
- Flushes: see the failure cases above (OS crash or power loss window after the save; a sharing error for a reader during the flush of that file).
- Another mod patching the compressor, the slot or the call sites makes ours `SKIP` (logged).

### First in-game run (2026-10-14): no batch, and why [verified]

- BP, two saves with `[SaveCompress] Enabled=1 Verify=1`: hooks installed, but `compressBatches 0`, no `save compress:` line. `[SaveFlush]` worked (2 flushes deferred per save, 6 files on the worker, 0 fails).
- Cause: a save does not leave its records pending until the package commit. The record class (`0x12E4A892`, vtables near `0x4ADA10`) commits itself when the writer closes it: `0x4AD9F0` (close) and the destructors `0x4AE080`/`0x4AE3C0` call `package->vt+0x3C(record)` = `FUN_004A8E00` whenever `record+0x24` (open) is set, so each record is compressed and written on close, one at a time; the second DBPF vtable `0x1048DA0` (`FUN_00996630`) has the same commit and `+0x3C`. The commit hook at `0x4A850F` then finds few or no pending records. Exe scan: the codec (`0x4EBFD0`) is built for compression only in `FUN_004A7030`; the other users (`0x727950`, `0x72A150`, ...) decompress.
- Consequence: there is no look-ahead at the commit. Compressing a record ahead of its close would need its bytes before the writer is done with it (reading the open stream moves its position) or deferring the record's commit to the package commit (different file layout, and a read-back of a deferred record would fail in `FUN_004A94C0`). Neither is done. `[SaveCompress]` stays off.
- 0.10.1 adds counters v12 (`compressCommits`, `compressCommitPending`, `compressDirectCalls`, `compressDirectMs`, `compressDirectMB`) and one log line per save: `save compress: this save N codec compress calls outside a batch (MB in, ms), C commit calls (pending at the last: P)`. They give the real compression time per save and how many records are still pending at the commit before any further design.

## Phase 11: the saved ExportDB without dead space and unneeded outfit copies (`[SaveSlim] Enabled=0` by default)

`saveslim.cpp`, built on 0.36.148, no counters (log lines only).

### Why

- Every Sim who leaves a lot is packed again (`PackOutfitToDisk` -> `SaveTravelSim`): the game writes a new outfit (`0x025ED6F4`) and its textures (`0x033A1435`, `0x0341ACC9`) into `<world>ExportDB.package` and never deletes the old copy [verified 2026-10-29: ~10-12k texture entries per session, ~300 outfits].
- The package writer appends and never reuses freed bytes; every save also leaves the old index behind as dead space [verified].
- Result on one 491-day save: ExportDB 101 MB, 297,629 entries (3,904 outfits, 155,273 + 138,452 textures), 16.6 MB dead; +~4 MB per 20-minute session [measured]. Every index entry also costs EA heap at load (~2 heap chunks per entry) [inferred from the 2026-10-29 heap walk: an offline-cleaned copy loaded with ~16 MB more EA heap free].
- An earlier script-side cleaner (delete the old outfit when it is replaced) found 2 of 273 "unneeded" outfits still named by another saved object, and the file grew anyway (no byte reuse). So the decision is made over the saved bytes, after the save.

### What it does

- The game plays from the working copy `CurrentGame.sims3`. It copies the saved folder `Saves\<name>.sims3` only at step 9 of the save and at the load copy (`0xAB16ED`) [verified code]. It holds none of that folder's files open in play [verified: share-mode-0 opens of every file of the loaded save succeed in a running session; only the working copy's `TravelDB.package` is held].
- `SaveInfoHook` notes the saved folder (info+0x430) for normal saves. After `FUN_00AB1030` returns 0 (every one of 100 logged saves returned 0), `SaveGameHook` starts one worker at background CPU and I/O priority (after `[SaveFlush]`'s worker, if any).
- The worker reads every file of the folder:
  - DBPF 2.0 with index type 0, no holes, compression 0 or `0xFFFF` (the only values in the saves checked);
  - plain files (`Meta.data`, `1a5tf1l3.dat`) as bytes.
- Mode 2: the candidates are the outfits and the group-0 textures of the ExportDB packages, one node per instance.
  - Roots: every 8-byte run, at any alignment, of every other body (decoded with `RefpackFast`) and of the plain files that equals a candidate instance. The instance of any other index entry also counts (123 outfits share their number with `.nhd` thumbnails `0x0580A2CD`/`CE`).
  - A candidate's own body names other candidates (outfit -> TXTC -> TXTF); those links are closed from the roots.
  - It drops outfits not reached, and group-0 textures not reached that a dropped outfit names, directly or through textures. Textures nothing names stay (unknown use).
  - Any byte match keeps an entry, so the test only errs towards keeping.
- Each ExportDB with something dropped or at least 1 MB dead is written to `<TS3 user folder>\ts3native-slim.tmp`: header as the game wrote it (count, index size and position updated), kept bodies byte for byte in source order, then the index in its old order with new offsets. The same layout as the offline cleaner whose copies loaded with the same outfit health (2026-10-29).
- Checks before the rename: written size, then header, index and every kept body read back and compared with the source. The source is held open without write sharing from the first read to the rename (`MoveFileExW` replace + write-through, same volume).
- Anything unexpected keeps the game's file: an unknown header field or compression, an unreadable or undecodable body, a subfolder, or a failed check. An unreadable file anywhere in the folder turns Mode 2 into Mode 1 for that save (references unknown).
- Waits: the next save (also autosave and travel) and the load copy wait for the worker (`[SaveFlush]`'s load-copy hook, or its own when `[SaveFlush]` is off). A game exit while it runs leaves at most the temp file; the save is untouched until the rename.
- Not touched: `TravelDB.package` (30-54% dead in some saves, but the working copy holds it open and it is not loaded into the EA index heap), the `.nhd` files (~1% dead).

Settings `[SaveSlim]`: `Enabled=0` (needs `[SaveWrite] Enabled=1`, else `SKIP`), `Mode=2` (0 = what Mode 2 would do, measured and logged, nothing written; 1 = rewrite without dead space; 2 = also drop). Log after each save:
- `save slim: <mode>, N files, N ExportDB, references read; outfits N, dropped N outfits + N textures (orphan textures kept N); scan N MB, N instances, N links, N ms; write N ms; total N ms`;
- then one `save slim: <world>ExportDB.package: entries a -> b, outfits, textures, MB before -> after (dead MB): rewritten | left (why)` per ExportDB;
- `save slim: waited N ms for the worker` when a save or load had to wait.

Tests: `selftest.exe` (`saveslim-test.h`): made-up save folders, Modes 0/1/2, outfit named only by another outfit, by an index key of another package or by `Meta.data`, texture chains TXTC -> TXTF, orphan textures kept, unknown compression and an undecodable body (Mode 2 -> Mode 1), unknown index type (file untouched), second run with nothing to gain, 64 KB read windows. `saveslim-tool.exe <save folder> [mode]` runs the job outside the game (Mode 0 reads only; Modes 1 and 2 only on a copy).

### How to test in game

1. `[SaveWrite] Enabled=1`, `[SaveSlim] Enabled=1 Mode=0`. Load an old save, play 10+ minutes, save as a new name: the `save slim:` lines give what Mode 2 would drop and the scan time.
2. `Mode=2`, same save, save as another new name. Check the `rewritten` lines, the new folder's ExportDB size, no `ts3native-slim.tmp` left.
3. Load the new save twice: no script errors, every Sim dressed and textured as before, travel visitors arriving dressed (LagScope outfit health at load: same counts, invalid 0).

## `[Verify]`: runtime checks of the mapping-doc inferences (v0.10.8, counters v20 + v21 + v22, `Enabled=0` by default)

Measurement only (`native/ts3native/verify.cpp`). The mapping waves (docs/34-48) left many claims marked [inferred]: which thread creates textures, whether two threads overlap on unlocked data, what a lookup costs. One probe build checks them all in game, so the fixes in PLAN.md "Next round" (items 1-28) start from numbers. 0.10.6 (27 probes, v20) ran once on 2026-10-15 (docs/data/results/2026-10-15-verify-findings.md). **0.10.7** (2026-10-15, selftest ok, **not run in game**) fixes four readouts of that run and adds 31 probes for the docs/51 rows that had no key; parked as `game-copy/ts3native.asi.verify` (SHA-256 starts `d4835064a8ec`), run 2026-10-16. **0.10.8** (2026-10-16, selftest ok, **not run in game**) adds 31 probes for the docs/52-62 ledger rows (docs/51 §3b, §6; counters v22) and puts the `0xD7F8B0` probe behind `ThreadedObjects` (default 0, docs/68 §4 #1); parked as `game-copy/ts3native.asi.verify` (SHA-256 starts `20af6f323fff`).

### 0.10.7: readouts fixed after the first run [verified asm on the exe copy unless marked]

| Readout (0.10.6) | What it read | Fix |
|---|---|---|
| `vfMemSpeedTreeBytes` -10,057 / -9,373 KB, flat from load to end | `[0x11D8628]` is SpeedTree's own net counter, but its adds and subtracts do not pair: one free path subtracts `0x13800` where the alloc adds `0x13810` (`0x7BE670` vs `0x7BDBA0`), array frees subtract a 4-byte header plus `count*0x54` (`0x7A3210`); 110 sites in 56 functions, no other total exists | dropped: `vfSpeedTreeKB` stays -1 (field kept for the layout); `verify-verdicts.ts` treats a negative memory value as no data. The SpeedTree heap size (I-34-6) needs an allocator-side count; open |
| `vfMemResCacheBudgetBytes` 15.7 MB | the first ResourceCache-class instance in the manager's cache array is the **AnimationCache**: `ResourceSystem_Init` creates it first (`0x738E2E`, budget forced `0xF00000` at `0x738E97`, priority `0x3E8`), then the ResourceCache (`0x738EDA`, budget from the config through `0x733140`, priority 0) | budget, held bytes and flush bytes now come from the **priority-0** instance only (0.10.6 summed both caches' bytes); none: budget -1 |
| UI lock `0xFBA5CC`: 0 calls | the probe was hooked (`vfReady` `0x17FFFFFF` = bits 0-26 and the readout bit 28; bit 27 is no probe). `0xFBA5CC` is **not the lock**: it is a static `-1` that the 10 UI call sites pass as `Lock_Acquire`'s timeout pointer `[esp+4]`; the lock itself is `ecx` (WindowManager `+0x38` in the draw `0x50B270`, `+0x678` in `PostMessage 0x50D660`). The filter compared `ecx` with `0xFBA5CC`, so it never matched. `0xFD9744` (JobManager, 23 sites) and `0xFE4DC4` (SimService request list, 3 sites) are the same kind of per-module timeout constant (docs/36, docs/42 call them locks) | `Lock_Acquire` calls are classified by the timeout pointer: `0xFBA5CC` keeps `vfUiLock`, `0xFD9744` and `0xFE4DC4` get thread slots 44/45 (role counts, wait us, max) |
| `vfMemVramBytes` 0 | the offset is right: `0x591010` creates the DeviceConfig singleton `[0x11CDD10]`, `DeviceConfig_Init 0x5928F0` calls the hw-info init `0x613CB0` with `this+0x174`, which stores the DxDiag VRAM at hw info `+0xC4` (`0x613EAE`); the DxDiag path `0x612640` returns `(((MB + 4) << 20) & 0xFF800000) >> 20`, so 8,192 MB wraps to 0 | none: 0 means the game stored 0 (docs/40 §5) |
| `vfCostECAA65Calls` 0 | `0xECAA65` is in the main loop `FUN_00ECA960` (loops back to `0xECA9B1` until the quit state), on the branch taken only when `ResourceSystem` vtable `+0x58` returned true (`0xECAA58 test bl`) | stub kept; new mid-function counter at `0xECA9ED` (`movss xmm0, [0x11CDD9C]`, 8 bytes, reached by both branches of the flag test every iteration) gives the loop count `vfCostECAA65Loops`. 0 gate hits from game start (load included) means the gate was never true in that run, so "loading" is not its meaning [inferred] |

### How it works

- One generic pass-through stub per probe (the routegate.cpp pattern): it saves all registers and flags, calls the counter code with the probe number and the caller's return slot, restores everything and jumps to the stolen prologue. No probe needs the function's signature or calling convention. Probes that time a call or count "threads inside" swap the return address for a leave thunk and keep the real one on a per-thread stack of 16 frames; a frame left without returning (exception unwind) is dropped at that thread's next entry. The counter code does integer work only, so a float return in ST0 (`0xC2A570`) passes through untouched.
- The 10 FPS gate `0xECAA65` and the loop counter `0xECA9ED` are in the middle of the main loop `FUN_00ECA960` with ST0 live: their stubs have no call, save the flags and do one locked add.
- Overlap groups (0.10.7, with `Races=1`): each timed probe can sit on side A or B of a group; an entry counts `vfOverlap[g]` when another thread is inside the other side (a probe on both sides counts two threads inside itself). Own nesting never counts; a dropped frame gives its sides back.
- Every prologue was disassembled from the Steam 1.67 exe copy (capstone, 2026-10-15) [verified]: whole instructions, no relative operand, first SSE use in each function is a write (no SSE argument passing), and no branch in `.text` targets the overwritten bytes (byte scan; the 3 hits were inside other instructions).
- At start each probe of an enabled group is checked against its bytes; a mismatch skips that probe only and logs `SKIP verify <name>: prologue at ... differs (found ...)`. Pairs are skipped together (shape swap + ray, key list + register, package probe + index find): one half alone gives numbers that mean nothing. `[Verify]` installs last, so a section that already hooked an address wins: `[HeightCache]` owns `0xC2A570`/`0xC2A840`, `[RouteAsync]`/`[RouteGate]` own `Lock_Acquire 0x4E16F0`.
- Roles: main = `[0x11D8A88]`, sim = `[0x11D8A8C]`, other = neither, off-device = not the device owner `[[0x11CECC8]+0x10]` (the test `Device_IsOwnerThread 0x60E820` makes [verified asm]).
- Event file `ts3native-verify.jsonl` (`Log=1`): `first` (first call of a thread probe per role, with tid and return address), `idOffSim` (each of the 8 recorded off-sim lookup callers), `swapInRay` / `registerInKeyList` / `overlap` (first 16 overlaps; `overlap` with the group number 0 Scene, 1 UI, 2 R1, 3 route ray), `mem` (readouts every 10 s with `Memory=1`).

### Switches (`[Verify]`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | 0 | master switch |
| `Mode` | 1 | start value of `vfMode` (+0x34C): 0 = every hook passes straight through, 1 = measure |
| `Log` | 1 | write `ts3native-verify.jsonl` |
| `Threads` | 0 | group 1 (12 probes, 0.10.7: + 30, 0.10.8: + 31) |
| `ThreadedObjects` | 0 | 0.10.8: also hook `ScriptHost_ThreadedObjects 0xD7F8B0` (I-36-1). Keep 0 while S3PP is installed: S3PP's SimulatorStageProfiler expects that entry's bytes, so our `jmp` disarms all four of its seams and its type-lookup speed-up (MonoTypeEHashProbe) without an error (docs/68 §4 #1) |
| `Races` | 0 | group 2 (4 probes + the id lookup; 0.10.7: the overlap groups of the thread probes) |
| `Costs` | 0 | group 3 (10 probes + the id lookup chain walk, SimService readout; 0.10.7: + the loop counter); hooks every engine `Lock_Acquire` (classified by the timeout pointer, three classes), a small cost on every lock |
| `Memory` | 0 | group 4, readouts only (no hooks), once per second |

### Counters (v20, offsets from the `ts3native_counters` export; `/native` shows them by these names)

`vfReady` (+0x348) bit i = probe i hooked (order below, 0-26), bit 28 = readout thread; `vfReady2` (+0x55C, v21) bit i = probe 27 + i. `vfMode` (+0x34C). `*MaxUs`/`*Max*` are maxima since the last reset; `*K` count in units of 1024. **Live window (0.10.7):** `/native?vfresetmax=1` zeroes every `vf*Max*` field after reading them; `queue.ts` sends it with the dump after the load for jobs with `Verify.Enabled` and marks the dump `maxReset: true`, and `verify-verdicts.ts` takes `after - before` for counts and `*Us` (modulo 2^32) and the `after` maxima, so the verdicts cover play only (the first run's dumps have no reset: their maxima include the load).

`/native` keys follow `vf<Group><Target><Suffix>` (Target = address without `0x`): struct field -> key: `vfThread[i][0..5]` -> `vfThr<addr>Main|Sim|Other|OffDevice|Us|MaxUs`; `vfSwaps`/`vfSwapsInRay` -> `vfRaceC2A840Calls`/`Race`; `vfRayCallsK` -> `vfRaceC2A570KiloCalls`; `vfRayMaxInFlight` -> `vfRaceC2A570MaxInFlight`; `vfKeyLists`/`vfRegDbs`/`vfKeyListOverlaps` -> `vfRace4B1AE0Calls`/`vfRace4B2D00Calls`/`vfRace4B1AE0Overlap`; `vfIdLookupsK`/`vfIdOffSim`/`vfIdOffSimRet[i]` -> `vfRace939100KiloCalls`/`OffSimRace`/`Ret<i>`; `vfPkgCalls`/`Us`/`MaxUs` -> `vfCost4AFFC0Calls`/`Us`/`MaxUs`; `vfPkgWalked`/`vfPkgMaxWalked` -> `vfCost4AA1B0Calls`/`MaxPerCall`; `vfIdChain[6]`/`vfIdMaxChain`/`vfIdObjects` -> `vfCost939100Chain0|1|2to3|4to7|8to15|16up`/`MaxChain`/`Objects`; `vfW2lCallsK` -> `vfCostAB8540KiloCalls`; `vfFindLotCalls` -> `vfCostC60D30Calls`; `vfTreeRebuilds`/`vfTreeMaxPerFrame` -> `vfCost712480Calls`/`MaxPerFrame`; `vfFdbHold*` -> `vfCost693780Calls|Us|MaxUs`; `vfFdbWait*` -> `vfCostFdbWaitCalls|Us|MaxUs`; `vfUiLock[role]` -> `vfCostFBA5CC<Main|Sim|Other>Calls|Us|MaxUs`; `vfFpsGate` -> `vfCostECAA65Calls`; `vfSimSlice` -> `vfCostSimServiceSlice`; `vfSpeedTreeKB`/`vfVramMB`/`vfRcBytesKB`/`vfRcFlushKB`/`vfRcBudgetMB` -> `vfMemSpeedTreeBytes`/`vfMemVramBytes`/`vfMemResCacheBytes`/`vfMemResCacheFlushBytes`/`vfMemResCacheBudgetBytes` (agent.js scales KB/MB to bytes; -1 stays -1); `vfDeviceTid` -> `vfThrDeviceTid`; `vfReady`, `vfMode`, `vfUntracked`, `vfLogDropped` unchanged. v21: `vfThread2[s-12][0..5]` -> `vfThr<addr>Main|Sim|Other|OffDevice|Us|MaxUs` with the slot addresses below; `vfOverlap[0..3]` -> `vfRaceSceneOverlap`, `vfRace50AB70Overlap`, `vfRaceR1Overlap`, `vfRace686CC0Overlap`; `vfFdbPublishes` -> `vfCostFrameDbPublished` (agent.js adds `vfCostFrameDbConsumed` = `vfCost693780Calls`); `vfFpsLoops` -> `vfCostECAA65Loops`; `vfReady2` unchanged.

**Threads** (`vfThread[12][6]` from +0x350, 24 bytes per probe: `main`, `sim`, `other`, `offDevice` calls, `us` total, `maxUs`):

| # | Probe | Address | Offset | Inference it tests |
|---|---|---|---|---|
| 0 | `thumbTex` | `ThumbnailManager_CreateThumbnailTexture 0xD5D910` | +0x350 | docs/43: creates and fills a MANAGED texture on the sim thread with no owner check (crash-on-save lead): `sim`/`offDevice` > 0 confirms |
| 1 | `texCreate` | `Texture_CreateD3D 0x619540` | +0x368 | docs/40 S1: the only CreateTexture site; is it ever off the device thread |
| 2 | `texUpload` | `DDS_UploadLevels 0x60D290` | +0x380 | docs/40 S1: same for the per-mip lock and copy |
| 3 | `renderReq` | `Renderer_RenderRequest 0x6F3DC0` | +0x398 | docs/46: reached from `IC_CASUtils_IsSceneLoadedImpl`; a sim call would wait for the frame jobs and draw |
| 4 | `execJob` | `JobManager_ExecuteJob 0x599720` | +0x3B0 | docs/46 RJ1, docs/36: all jobs per thread (the overall count) |
| 5-9 | `jobMainCull`, `jobShadowCull`, `jobReflCull`, `jobShadow2Cull`, `jobSortMain` | `0x6F1B00`, `0x6EBE30`, `0x6EA260`, `0x6EBF50`, `0x6F1D10` | +0x3C8, +0x3E0, +0x3F8, +0x410, +0x428 | docs/46 RJ2: does the sim pump ever steal a frame job, and its length (`sim`, `us`). The bodies are hooked directly instead of comparing `job+0x10` in `ExecuteJob`: same answer, no job layout needed |
| 10 | `texDrain` | PendingTextures drain `0x50A860` | +0x440 | docs/47: main thread only |
| 11 | `asyncComplete` | `AsyncLoader_Complete 0x7294F0` | +0x458 | docs/39 §1.4 step 5: "the thread that services the loader" [inferred] |

**Races:**

| # | Field (offset) | Probe | Inference it tests |
|---|---|---|---|
| 12, 13 | `vfSwaps` (+0x470), `vfSwapsInRay` (+0x474), `vfRayCallsK` (+0x478), `vfRayMaxInFlight` (+0x47C) | `WorldCellGrid_SetCellShapes 0xC2A840` counted; `WorldCellGrid_Raycast 0xC2A570` in-flight count | docs/38 C1: main frees `cell+0x38` while a sim ray reads it. `vfSwapsInRay` = swaps while another thread is inside a ray |
| 14, 15 | `vfKeyLists` (+0x480), `vfRegDbs` (+0x484), `vfKeyListOverlaps` (+0x488) | `GetKeyList 0x4B1AE0`, `RegisterDatabase 0x4B2D00` in flight | docs/39 R2: a registration during an unlocked key walk |
| 16 | `vfIdLookupsK` (+0x48C), `vfIdOffSim` (+0x490), `vfIdOffSimRet[8]` (+0x494) | `ObjectId_Lookup 0x939100` | docs/45 (and PLAN 24): off-sim readers of the unlocked id hash, with the first 8 distinct caller return addresses; counted once the sim thread id is known |

**Costs:**

| # | Field (offset) | Probe | Inference it tests |
|---|---|---|---|
| 17, 18 | `vfPkgCalls` (+0x4B4), `vfPkgUs`, `vfPkgMaxUs`, `vfPkgWalked` (+0x4C0), `vfPkgMaxWalked` | `GetPackageSource 0x4AFFC0` (outermost calls), package index searches `DBPF_IndexCompressed_Find 0x4AA1B0` inside it | docs/39 R1: ~6.7 ms/s in the linear probe; packages walked per miss |
| 16 | `vfIdChain[6]` (+0x4C8: 0, 1, 2-3, 4-7, 8-15, 16+ nodes compared), `vfIdMaxChain` (+0x4E0), `vfIdObjects` (+0x4E4) | `0x939100` on the sim thread: walks the chain as the lookup does (`ecx` = buckets, `[esp+8]` = id, `id_lo % 0x409`, next `+0x10`, ids `+8/+0xC`; count at buckets `+0x1028` = service `+0x1038`) [verified asm] | docs/45 O1/O2: chain length in big worlds |
| 19 | `vfW2lCallsK` (+0x4E8) | `Lot_WorldToLocal2D 0xAB8540` | docs/38 C2 |
| 20 | `vfFindLotCalls` (+0x4EC) | `WorldManager_FindLotById 0xC60D30` | docs/38 C3 |
| 21 | `vfTreeRebuilds` (+0x4F0), `vfTreeMaxPerFrame` (+0x4F4) | SpeedTree cluster rebuild `0x712480`, max between two FrameDb consumes | docs/48 S2. A frame tick at `Frame_RenderAndPresent 0xEC9F00` is not possible (relative call inside its first 5 bytes); the consume runs once per render frame |
| 22 | `vfFdbHolds` (+0x4F8), `vfFdbHoldUs`, `vfFdbHoldMaxUs` | `FrameDb_ConsumeAndInterpolate 0x693780` on main | docs/41 S1: hold of the `+0x48` lock. The consume's whole time is an upper bound (the early outs before `TryEnterCriticalSection` are short) |
| 23, 24 | `vfFdbWaits` (+0x504), `vfFdbWaitUs`, `vfFdbWaitMaxUs` | `SimRender_TripleBufferSync 0x593590`, `FrameDb_PublishFrame 0x593600` on the sim thread (both `EnterCriticalSection(+0x48)` first) | docs/41 S1: the sim waits for the consume (likely the 0.6% sim wait, docs/20) |
| 25 | `vfUiLock[3][3]` (+0x510: per role main/sim/other `acquires`, `us`, `maxUs`); slots 44/45 (v21) | `Lock_Acquire 0x4E16F0` with timeout pointer `[esp+4]` `0xFBA5CC` (UI lock; 0.10.6 tested `ecx`, which never matched), `0xFD9744` (JobManager), `0xFE4DC4` (SimService request list) | docs/47 UI-L1: sim waits while main draws the 3D scene under the UI lock; docs/36 (I-36-10), docs/42 (I-42-3) |
| 26 | `vfFpsGate` (+0x534) | `0xECAA65` (reached only while the ResourceSystem gate is set) | docs/41 P5: does the 10 FPS loading cap fire in live mode |
| 57 | `vfFpsLoops` (+0x8A4, v21) | `0xECA9ED`, every main-loop iteration | the gate's denominator |
| - | `vfSimSlice` (+0x538) | readout `[[0x11CE6A0]+0x139]` (-1 = no service) | docs/42 CAS2: is SimService time slicing on |

**Memory** (readouts, no hooks): `vfSpeedTreeKB` (+0x53C, -1 since 0.10.7: `[0x11D8628]` is not a live-bytes total, see the table above), `vfVramMB` (+0x540, `[[0x11CDD10]+0x238]`: hw info at DeviceConfig `+0x174` (`DeviceConfig_Init` passes it to `0x613CB0` at `0x592A30`), VRAM at hw info `+0xC4` (`0x613EAE`); DeviceConfig singleton from the getter `0x58E3F0` [verified asm; the getter's object = DeviceConfig inferred from the shared `+0x2A8` Mac flag]; docs/37 C1, docs/40 §5: a wrapped value reads 0 or 2048), `vfRcBytesKB` (+0x544) and `vfRcFlushKB` (+0x548: `+0x94`/`+0x98` of the priority-0 ResourceCache since 0.10.7; 0.10.6 summed it with the AnimationCache), `vfRcBudgetMB` (+0x54C, its `+0x8C`), `vfDeviceTid` (+0x550). Also `vfUntracked` (+0x554, timed entries with 16 frames open) and `vfLogDropped` (+0x558).

**v21 thread probes** (0.10.7; `vfThread2` from +0x560, 24 bytes per slot, slot s at +0x560 + (s - 12) * 24; probe i = 27..56 uses slot i - 15). Each prologue: whole instructions, no relative operand, the function's first SSE use writes its register, no branch into the patched bytes (byte scan of `.text`; one hit at `0x401BBC` is the `0x74` inside `mov esi, [esp+0x10]`) [verified, capstone on the exe copy]; all 58 table entries compared byte for byte with the exe copy. `[LotStream]` `SyncWait=1`/`ObjScene=1` own `0x7D98C0`/`0xABFAC0`: with those on, the Verify probe skips itself.

| Slot | Probe | Address | docs/51 row | Group |
|---|---|---|---|---|
| 12 | `rayEntry` | physics ray entry `0x6583E0` | I-33-3 | |
| 13 | `routeRay` | route worker ray `0x686CC0` | I-33-4 | overlap `686CC0` (self, both sides) |
| 14 | `stCtor` | `SpeedTreeRenderer_ctor 0x7140D0` (CRT static init: may run before the hook) | I-34-2 | |
| 15 | `stRender` | `SpeedTreeRenderer_RenderFrame 0x7135B0` | I-34-3 | |
| 16 | `stWind` | SpeedTree wind `0x712F00` | I-34-7 | |
| 17 | `audioCb` | sound-finished callback dispatch `0x863DC0` | I-35-5 | |
| 18 | `threadedObjs` | `ScriptHost_ThreadedObjects 0xD7F8B0` (MaxUs vs the 5000 deadline) | I-36-1 | |
| 19 | `loadLayout` | `LoadLayout` IC `0x782BC0` (window-IC marker: `GetMainWindow 0x779540` and the other small window ICs start with a relative call) | I-36-3, I-43-2 | overlap `50AB70` side B |
| 20 | `rwfsWorker` | RW file system worker start `0x401BE0` (calls = threads started) | I-36-4 | |
| 21 | `poolWorker` | thread pool `WorkerProc 0x4E1DF0` (calls = workers started) | I-36-6 | |
| 22 | `devTexCreate` | device wrapper texture create `0x619770` | I-37-3 | |
| 23 | `worldLoad` | front-end world load `0xC6D970` | I-38-2 | |
| 24 | `lotGrid` | lazy lot grid rebuild `0xC688A0` | I-38-6 | |
| 25 | `remoteSync` | synchronous remote call `0x7D98C0` (wait us) | I-38-7 | |
| 26 | `texFactory` | texture factory `0x722E00` | I-40-1 | |
| 27 | `buildModel` | `BuildModelMsg 0x5AD8F0` | I-42-1 | |
| 28 | `buildStep` | `BuildStep 0x5E2050` | I-42-2 | |
| 29 | `lotLighting` | `LotLightingManager 0xADD2C0` | I-44-1 | |
| 30 | `ao` | precomputed AO `0x6883A0` | I-44-2 | |
| 31 | `imposterStep` | `LotImposterJob_Step 0xADAE50` (a job step pointer, set at `0xADB062`) | I-44-3, I-44-4 | |
| 32 | `idInsert` | object id hash insert `0x939170` | I-45-1 | |
| 33 | `sceneMsg` | `SceneObjMgr_HandleMessage 0x703CB0` | I-46-1, I-48-3 | overlap `Scene` side A (side B: the cull job bodies, probes 5-8) |
| 34 | `adoptVisuals` | `WindowManager_AdoptVisualsLocked 0x50B300` | I-47-2 | |
| 35 | `uiInput` | `UIInput_ToWindowMessage 0x5989C0` | I-47-3 | |
| 36 | `inputCall` | `0x598F90` (camera/input callers) | I-47-7 | |
| 37 | `quadtree` | static quadtree build `0x6E4770` | I-48-1 | |
| 38 | `lotScene` | `Lot_UpdateObjectSceneNode 0xABFAC0` | I-50-2 | |
| 39 | `scriptMsg` | `ScriptMessageScope 0x7D2DB0` | I-50-3 | |
| 40 | `wmDraw` | `WindowManager_Draw 0x50B270` | I-43-2, I-47-5 | overlap `50AB70` side A, `R1` side B |
| 41 | `effectUpdate` | `UTFWinEffect_Update 0x5172E0` | I-47-5 | overlap `R1` side A |
| 42 | (`shapeSwap`, probe 12) | `0xC2A840` thread roles | I-38-3 | |
| 43 | (`treeRebuild`, probe 21) | `0x712480` thread roles | I-34-8 | |
| 44 | (`lockAcquire`, probe 25) | `Lock_Acquire` with `0xFD9744` (JobManager): acquires by role, wait us | I-36-10 | |
| 45 | (`lockAcquire`, probe 25) | `Lock_Acquire` with `0xFE4DC4` (SimService request list; posters `0x5EDA20` from `IC_CASUtils_SimOutfitFreeImpl`, swap `0x5F0DF0`/`0x5F0E50`) | I-42-3 | |

Also v21: `vfOverlap[4]` (+0x890), `vfFdbPublishes` (+0x8A0: `0x593600` on the sim thread, I-41-4), `vfFpsLoops` (+0x8A4).

**v22 probes** (0.10.8; `vfThread3` from +0x8AC, slot s at +0x8AC + (s - 46) * 24; `vfReady3` +0x8A8 bit i = probe 58 + i). Prologues read from the exe copy (capstone, 2026-10-16) [verified]: whole instructions, no relative operand, no direct branch into the copied bytes (byte scan of `.text`), the function's first SSE use is a write (`0xC6C290`, `0x49C5F0`, `0xD75FA0`) or none before the first `ret`; all 89 table entries compared byte for byte with the exe. None sits in a known signature window of S3SS or S3PP (docs/68 §6). `/native` keys: slot prefix + `Main|Sim|Other|OffDevice|Us|MaxUs` (count-only slots keep `Us`/`MaxUs` 0).

| Slot | Probe | Address | Kind | docs/51 row | Key prefix |
|---|---|---|---|---|---|
| 46 | `monoLoaderLock` | `mono_loader_lock 0xE85020` (skipped under `[IcLockCheck]`) | thread, timed | I-58-1 | `vfMonoE85020` |
| 47 | `monoVtable` | `mono_class_create_runtime_vtable 0xEA1E30` | thread | I-58-2 | `vfMonoEA1E30` |
| 48 | `monoCctorSlow` | `0xEA3382` in `mono_runtime_class_init`: mid-function, after the fast exit `jne 0xEA36C7`; the logged caller is `[esp+0x1C]` | count | I-58-3 | `vfMonoEA3382` |
| 49 | `monoThunk` | `tramp_create_thunk 0xE51400` | thread | I-58-4 | `vfMonoE51400` |
| 50 | `monoGcHandle` | GC handle alloc `0xE78780` | thread | I-58-5 | `vfMonoE78780` |
| 51 | `monoRtMethod` | `mono_interp_get_runtime_method 0xE53070` | thread | I-58-6 | `vfMonoE53070` |
| 52-55 | `rtLotContains`, `rtExitBuildBuy`, `rtRebuildLot`, `rtDestroy` | `0x628A90`, `0x835240`, `0x835110`, `ObjectService_DestroyById 0x939420` (docs/60 §2 gate sites 13-15, destroy drain) | thread | I-60-1..I-60-5 | `vfRt628A90`, `vfRt835240`, `vfRt835110`, `vfRt939420` |
| 56 | `rtShapeRelease` | `PhysShape_Release 0x682760`, counted only when the return address is `0xC2A896` (the terrain swap's release) | count | I-60-6, I-60-7 | `vfRt682760` |
| 57 | `evPump` | `JobManager_RunJobs 0x59A430` (opens a pass per thread; skipped under `[LotStream]`) | thread, timed | I-61-2 | `vfEv59A430` |
| 58, 59 | `evReady` + existing `execJob` | `ReadyJob 0x599FC0` inside a pass records the job (`[esp+4]`, newest 16 per thread); `ExecuteJob 0x599720` of a recorded job in the same outermost pass counts once | count | I-61-2 | `vfEvReadyInPump`, `vfEvPumpSamePass` |
| 60-63 | existing `remoteSync` / `execJob` / `lotAddExec` | `RemoteCall_Dispatch 0x7D98C0`: inline when the caller is the target (selector byte `[esp+4]`: 1 = sim `[0x11D8A8C]`, 0 = main `[0x11D8A88]`, the test at `0x7D98E2`) and inline inside a lot add; a not-inline call with async byte `[esp+8]` = 0 opens a wait in which `ExecuteJob` and lot adds count | count | I-61-1, I-61-8, I-61-3, I-61-4 | `vfEvRcInline`, `vfEvRcInLotAdd`, `vfEvJobInSyncWait`, `vfEvLotAddInSyncWait` |
| 64 | `evScriptDrain` | ScriptMessageServer service slot `+0x20` `0x76B1A0` | thread | I-61-9 | `vfEv76B1A0` |
| 65, 66 | `lotAddExec` + the four managed entries | `RemoteMethodCall_Execute 0xABD3C0` with fn `[ecx+0x10] == 0xAC1130` (lot add; skipped under `[LotStream]`); `0xE59FD0`, `0xE5A600`, `0xD8A8E0`, `0xD75FA0` (skipped under `[RouteWorker]`) entered inside it | thread, timed / count | I-61-5..I-61-7 | `vfThrAC1130`, `vfEvLotAddManaged` |
| 67 | `lotScore` | `WorldManager_ScoreAndStreamLots 0xC6C290` (skipped under `[LotStream]`) | thread | I-61-14, I-61-15 | `vfThrC6C290` |
| 68-70 | `glyphAddPage`, `glyphPageTex`, `glyphClearPage` | `0x452D30`, `0x6916F0`, `0x452630` | thread | I-54-4..I-54-6, I-52-4 | `vfThr452D30`, `vfThr6916F0`, `vfThr452630` |
| 71 | `stblRebuild` | `LocStrSvc_RebuildTableList 0x59BDE0` | thread | I-54-7 | `vfThr59BDE0` |
| 72 | `padPoll` | `InputMan_PollPad 0x49C5F0` | thread | I-55-1, I-55-2 | `vfThr49C5F0` |
| 73, 74 | `shaderVs`, `shaderPs` | `0x799780`, `0x799810`; creates before the first `0xC6D970` entry also count in `vfShaderStartup[2]` (+0xBE0) | thread | I-53-1..I-53-3 | `vfThr799780`, `vfThr799810`, `vfCost7997x0Startup` |
| 75 | `loadPump` | `App_LoadPumpCallback 0xECA350` | thread | I-55-5 | `vfThrECA350` |
| 76 | `cursorApply` | `CursorManager_Apply 0x505620`, self overlap group 4 -> `vfOverlapCur` (+0xBDC) | thread + overlap | I-55-4 | `vfThr505620`, `vfRace505620Overlap` |
| 77, 78 | `arenaGrow`, `gcFree` | `0xD71900`, `GC_free 0xE4BEB0` | count | I-57-1, I-57-2 | `vfMonoD71900`, `vfMonoE4BEB0` |
| 79 | the four managed entries | `0xE59FD0`, `0xE5A600`, `0xD8A8E0`, `0xD75FA0` (nested entries count again) | count | I-56-5 | `vfMonoInvoke` |

Also v22: `vfMonoIcNestMax` (+0xBE8) and `vfMonoIcNestNeg` (+0xBEC, key `vfMonoIcNestNegCalls`): the 1 s poll (Costs or Memory) reads `[0x11F2D84]` (docs/56 I-56-1). Not hookable at the entry: `Job_Submit 0x59A350` (`je` in its first 4 bytes) and `RunJobsLocked 0x599E60` (`0x599ED8` jumps to `0x599E65`). Left out by the hook rule (docs/68): `0x59C5B0`, `0xE4D9F0`, `0xD81FC3`-`0xD81FE0`, `0xAC1130` (S3SS detours it), no new probe on `0xABFAC0`/`0x7D2DB0`.

**Not built:** the texture share of the ResourceCache (docs/39/40): the cache keeps no per-type byte count, so there is no field to read; it would need a hook on `Texture_CacheSizeBytes 0x619DB0` or `ResourceCache_AdjustBytes 0x731F60`.

### Selftest

Every probe runs through its real stub, trampoline and leave thunk on a dummy function (nop prologue of the probe's length, `mov eax, id; ret 8`): mode 0 passes everything through and counts nothing; (0.10.7) every v21 thread probe in its slot, the four overlap groups both ways with own nesting excluded and a dropped frame giving its sides back, swap and tree thread roles, the lock classes by timeout pointer (an `ecx` equal to the old filter value is not counted), FrameDb publishes on sim only, the loop counter, the priority-0 cache pick; thread roles and off-device; timing; ray/swap and key-list/register overlaps across two threads (own nesting excluded); a dropped dead frame undoes its in-flight count; the 16-frame limit; the id chain histogram over a fake bucket array and the off-sim caller list; the package probe with nested calls; tree rebuilds per frame; FrameDb hold/wait by role; the UI lock filter; the gate stub; each stub's index and trampoline slot; readouts over fake objects and SEH on bad pointers; the event file; and install refusing every probe in a process without the game's bytes. `static_assert`s pin the v20 offsets and the v21 layout. 0.10.8: every v22 thread probe in its slot, the count-only kinds (no time, no frame), the shape release filter on `0xC2A896`, shader startup vs after the world load, pump same pass (once per job, forgotten by the next pass), inline remote calls by selector, a synchronous wait counting jobs and lot adds run inside it, the lot-add filter on fn `0xAC1130` with managed entries and inline re-posts inside it, a dropped lot-add frame giving its depth back, the cursor self overlap across two threads, the IC nesting readout; `static_assert`s pin the v22 layout (764 int32). The save-write test's info buffer moved to its own page (a static buffer ended at a section edge in this build, so `Readable()` over the 520-char path failed).

### How to test in game

One load per group first (`Threads=1`, then `Races=1`, then `Costs=1` with `Memory=1`), BP and SV, normal play plus a save and a CAS open; read `/native` and the jsonl. Overhead to watch: `Costs` hooks every `Lock_Acquire` and the hot lookups (`0x939100` with a chain walk, `0xAB8540`, `0xC2A570` in `Races`); compare the sim tick time with `Mode=0` in the same load before reading costs.

### Risks

- A mod that patches one of these prologues after we load would overwrite our jump (we only check at start).
- The return-address swap is invisible to the game, but a stack walker (Frida, crash dumps) shows the leave thunk in place of the caller for an open timed call.
- Counters written from several threads use interlocked adds; the id chain histogram is sim-thread only (plain adds).


## Native fix batch 2 (v0.10.9, counters v23, all `Enabled=0` by default)

PLAN "Next, in order" 2. Each fix has its own ini switch, off by default; counters are listed in docs/registry.md (v23).

### `[JobQueue]` SimSkipShared (#13; covers #21)

- **What:** the sim thread's job record (created with mask `0x80000004` at `0xD7E9F6`) loses bit 31 ("also take shared-queue jobs") before each sim pump pass, so mask -1 jobs (CAS part loads, store jobs, the five frame jobs of docs/46) run only on `JobThread0..n` and the main pump [verified: `RunJobsLocked 0x599E60` reads bit 31 of `rec+0x2C` once per pass].
- **Where:** JobManager vtable slot `0xFD97C8` (`+0x24`, `RunJobs 0x59A430`), not the sim pump call site `0xD81FDA` (S3SS signature window, docs/68 §4 #2). Only a record whose low bits are exactly 4 is switched; the merged main+sim record (`0x80000005`, single-thread mode) never is. Runtime switch `jqSimMode` (+0xBF4); `SimSkipShared=2` installs it switched off for `--ab simskipshared`.
- **Counters:** `jqSimPumps`, `jqSimSkipPasses`; `ExecuteJob 0x599720` detour (installed with `Enabled=1`; `[Verify]` execJob is then skipped): `jqSimSharedJobs`, `jqSimRenderJobs`, `jqRenderJobs`, `jqWorkerJobs`, `jqWorkerTids`. Repro `jobqueue-shared`.
- **#21 not built as a private mask:** docs/46 RJ2 would give the frame jobs a mask bit shared by JobThread0 and main, but `ReadyJob 0x599FC0` hands a job with a specific mask to the first matching record's own queue only, so all culls would run on one thread. With #13 on, frame jobs no longer run in the sim pump; `jqSimRenderJobs` confirms it live.
- **Risk:** a shared job that the sim thread used to pick up quickly now waits for a worker or the next main pump (load and CAS times; compare `cas_state` timings). Shared jobs already have up to three consumers, so no new concurrency class [inferred].
### `[JobQueue]` Workers (#14)

- **What:** `JobManager_ReadTimeslices 0x5992D0` calls `JobManager_Init` with `push 1`; Init starts one OS worker `JobThread%i` (mask `0x80000000`) per set bit [verified disassembly `0x5991A0`-`0x5991C4`]. `Workers=N` (1-4) writes `(1 << N) - 1` there, so `JobThread0..N-1` share the shared queue (indices stay below the resource worker's `JobThread5`). Start-up only; not switchable at run time, so its A/B needs two loads.
- **Counters:** `jqWorkers` (patched count), `jqReady` bit 2; live check `jqWorkerTids` = N (distinct non-main, non-sim threads that ran shared jobs) during repro `jobqueue-shared`.
- **Risk:** docs/46 RJ4 left open whether a shared job assumes a single worker. Today up to three threads (JobThread0, main, sim) already run shared jobs at the same time, so a second worker adds no new kind of overlap [inferred]; watch CAS/store and save results in the bundle run.
### `[D3DMultithreaded]` (#4)

- **What:** `Device_Create 0x611080` keeps the CreateDevice behaviour flags in `esi`: `0x40` at `0x6112A4`, replaced by `0x20` at `0x6112B9` when hardware vertex processing is available [verified disassembly]. `Enabled=1` makes them `0x44`/`0x24` (`D3DCREATE_MULTITHREADED`), so the device serialises every call and the sim-thread thumbnail texture create/lock/fill (`0xD5D910`, docs/43 §1, seen live twice) no longer races the main thread's draws. Start-up only.
- **Not built: deferral to the device thread.** The IC callers wait for the texture, so a deferral is a sim-thread wait on the main thread, and the main thread already makes the sim wait on the FrameDatabase lock (docs/41 S1); a design that cannot deadlock was not worked out [inferred].
- **Counters:** `d3dMtReady` = 1 when both sites were patched; repro `thumbs-d3d-mt`; rule docs/68 §6.7: the S3SS log must still show its CreateDevice hook.
- **Risk:** a lock per device call on the main thread (DXVK honours the flag [inferred]); measure frame time against a run without it. It only guards device calls: the game's own texture objects are still unlocked.
### `[Alloc]` Dedupe (docs/67)

- **What:** `EaFreeHook` walks the target bin (at most `count` links, so at most `PerBin`) before caching a chunk; a pointer already there is a same-thread double free. `Dedupe=1` drops that Free (the first Free still owns the chunk) and counts it in `allocDupFrees`; with 0 it is counted and pushed as before. `AcPop` treats `count == 0` as an empty bin and `AcEmpty` walks at most `count` links, so a bin that an earlier double free looped cannot hang a flush or hand out the same chunk forever.
- **Not covered:** a double free across two threads (each caches the chunk once). docs/67 §2.4 needs a marker in the chunk for that; it is left until `allocDupFrees` shows that the game double frees at all.
- **Counters/switch:** `allocDedupe` (+0xC1C, `--ab allocdedupe`), `allocDupFrees` (+0xC20); repro `alloc-dupfree`. Offline: 18.4 ns per free+malloc hit after the walk (16.4 ns before; same machine, noisy).

## Native fix batch 3 (v0.10.10, counters v24, all `Enabled=0` by default)

PLAN "Next, in order" 3, in-place sim savings. Each fix has its own ini switch, off by default, a runtime switch for `--ab`, and a `Verify=1` key that checks every cache hit against the game's own function; counters in docs/registry.md (v24).

### `[LotSinCos]` (#11, docs/38 C2)

- **What:** `Lot_WorldToLocal2D 0xAB8540` (thiscall: lot, x, z, outX, outZ; `ret 0x10`) computes `fsin` and `fcos` of the lot angle (`lot+0x1A0`, float) on every call, then a 2D rotation around the lot origin (`+0x190`/`+0x198`) in packed SSE [verified disassembly; tools/emu `lot_world_to_local.py`: 3,038 of 3,038 cases bit-exact against the per-step float32 model]. It is 0.60% of sim-thread samples, reached from route planning and C# world queries (docs/38 §7) [measured]. Lots keep their angle, so the replacement keys a 256-entry cache on the angle bits (sin and cos only) and repeats the rest.
- **Bit-exact by construction:** sin/cos use the same x87 sequence (`fld`, `fld st0`, `fsin`, `fstp` float, `fcos`, `fstp` float); the rotation is scalar SSE with the same operands in the same order as lanes 0 and 1 of the game's `subss`/`mulps`/`addps`, including `-0.0 - s` from a volatile constant, so the compiler cannot turn it into a sign flip and NaN payloads come out the same. x87 precision control does not affect `fsin`/`fcos`; MXCSR is the calling thread's in both versions [verified: Intel SDM, same thread].
- **Thread safety:** the function runs on any thread; entries carry a check word written last (the `[HeightCache]` pattern), so a torn entry is a miss.
- **Offline check:** selftest `TestLotSinCos`: 4,600 calls (cold, warm, evicted, +0.0 vs -0.0, torn entry, Verify with a lying original, mode 0) bit-identical to an independent C version of the game's formula.
- **Counters/switch:** `lscReady`, `lscMode` (+0xC28, `--ab lotsincos`), `lscCallsK`, `lscHitsK`, `lscMismatches` (Verify=1). Repro `lot-sincos`. `[Verify]` worldToLocal is skipped while this detour is installed.
- **Expected gain [inferred]:** most of the 0.60% (two transcendentals at ~40-100 cycles each against a few loads and a hash), ~3-5 ms/s of sim time while routing; to measure with `--ab lotsincos`.
- **Not built:** the id-to-lot hash for `WorldManager_FindLotById 0xC60D30` (docs/38 C3): the lot tree changes on lot add/remove and the invalidation points are not mapped.

### `[IdCache]` (#22, docs/45 O2)

- **What:** every script `ObjectGuid` resolves through `ObjectService_FindById 0x939560` (442 call sites) -> `ObjectId_Lookup 0x939100`: 1,033 fixed buckets (`id_lo % 0x409`), chain through `node+0x10`, no lock [verified, docs/45 §4]. Chains are 8-15 nodes in play (docs/62 §5) [measured]. The detour answers sim-thread lookups from a direct-mapped 4096-entry cache keyed on the full 64-bit id and walks the chain itself on a miss (same rule as the game; outputs checked against the exe by tools/emu `object_id_lookup.py`, 308 of 308). Other threads, other tables and mode 0 go to the game's code.
- **Invalidation:** `ObjectIdHash_EraseNode 0x938D00` is the only unlink [verified: its two `add [ecx+0x1028],-1` are the only count decrements in the object service code, one caller `DestroyById 0x939420`, `DestroyAll 0x939510` destroys through `DestroyById`; emu `object_id_erase.py` confirms arg 2 is the node, 204 of 204]. Its detour runs in every mode: it bumps a generation and drops the erased node's entry before the game unlinks it. A fill whose walk overlapped an erase on another thread (generation changed) is dropped. A hit also needs the node to still carry the id and its bucket to be non-empty (the service constructor `0x93A510` zeroes all buckets; one call site, so once per process [inferred]). Misses are never cached, so inserts need no hook.
- **Bigger table (O1) not built:** it needs `Insert 0x939170`, `DestroyAll 0x939510` and the 9 sentinel users moved together (docs/45 §7); the cache gets most of the walk savings without touching the table. If the counters show many fills and long chains, O1 is the next step.
- **Offline check:** selftest `TestIdCache`: 3,000 objects (~3 per bucket), every hit, fill and miss returns the same iterator as a C model of the game's lookup; erase (switch on and off), re-insert, emptied bucket, Verify catching an entry whose node left the chain behind our back, other thread, other table, mode 0.
- **Counters/switch:** `idcReady`, `idcMode` (+0xC3C, `--ab idcache`), `idcLookupsK`, `idcHitsK`, `idcFillsK`, `idcNotFoundK`, `idcStepsK` (chain length = steps / (fills + not found), the docs/45 "chain-length counter"), `idcErases`, `idcMismatches` (Verify=1), `idcObjects`. Repro `object-id-cache`. `[Verify]` idLookup is skipped while the detour is installed.
- **Expected gain [inferred]:** a hit costs one division, a hash and four compares instead of 4-8 node visits on average (cache misses on scattered nodes); up to ~1.5% sim self (docs/45 O1 estimate) if the hit rate is high.
- **Risk:** a node freed without `EraseNode` would leave a dangling entry; the scan above found no such path, and `Verify=1` checks every hit in the bundle run before the switch is used without it.

### Not built in batch 3

- **#9 package map (docs/39 R1):** `GetPackageSource 0x4AFFC0` is `thiscall(manager, key*, int* outPriority)`, `ret 8`, walking the `{database, priority}` array at manager `+0x30`/`+0x34` and asking each database through its vtable `+0x34` (record open in test mode), with `g_ResourceLock` released around each ask [verified disassembly, 2026-10-16]. R1 needs to fall back to the probe for writable databases (saves, caches, DCCache), but the docs do not say how to tell a writable database from a read-only one (docs/08 mentions a read-only flag passed at registration, its location is not mapped), which vtable calls add or remove keys in a writable package, or how to enumerate a DBPF index's keys for the map. A cache keyed on the resource key without those facts could return a lower-priority package after a save package gains the key. Needed first: a Ghidra pass on `DatabasePackedFile` (open mode field, record add/remove slots) and on `DBPF_IndexCompressed` (key enumeration).
- **Autonomy stage A (docs/59 §3.7):** the doc gives rules and gains, not a complete design. A1 (per-search IC memo, the large item) names no managed hook points or the begin/end of a search scope; A2 changes behaviour by design; A3 (lot Sim array per tick) and A5 (`CalculateNumSimsAtHomeOrGoingHome` per tick) do not define what invalidates the per-tick value when a Sim is created or destroyed inside the tick (a destroyed Sim in a reused array would be scored); A4's "lot-add generation" has no source. Gains for A3-A5 are < 1-3 ms/s each [inferred]. Needed first: the managed hook list for A1 (which `SimIFace` wrappers, which entry and exit of `ScoreInteractionsForLocalAutonomy`) and an invalidation rule for A3/A5.
- **#11 id-to-lot hash for `WorldManager_FindLotById 0xC60D30` (docs/38 C3):** lot tree add/remove points not mapped.

Build: ts3native 0.10.10 (counters v24, 792 int32), parked at `game-copy/ts3native.asi.batch3` (sha256 prefix `4b8c5bca1985`), all switches off; `native/ts3native/out/ts3native.asi` in the main checkout stays the matrix-tested `dd8c43a7a854`. Draft queue `tools/harness/queues/native-batch3.json` (BP + IP, one load each, `--ab lotsincos,idcache`, 3 min slices, `Verify=1` on both, `[Alloc]` off), not run.

## H6 S2b and S3a: routing closure sites, observe only, and the terrain graveyard (v0.10.11, counters v25, all `Enabled=0` / `Sites=0` by default)

docs/60 §2.3 steps S2b and S3a. Built 2026-10-16, not run in game. Counters in docs/registry.md (v25, +0xC60-0xD14).

### S2b: `[RouteGate]` gate sites 13-15 and `Sites=1` (docs/60 §2.2, observe only)

- **Gate sites 13-15:** `LotTable_Contains 0x628A90` (8 bytes), `RoutingSystem_OnExitBuildBuy 0x835240` and `RoutingSystem_RebuildLotRouting 0x835110` (10 bytes each, ending before the `jz rel32` at +10) join the 12 S0 sites in `kRgSites` (all 15 or nothing, as before). The v16 arrays `rgUnlocked[12]`/`rgJob[12]` cannot grow in place, so sites 13-15 count in `rgUnlocked2[3]`/`rgJob2[3]` (agent.js keys `rgUnlocked_lotTableContains`, `_exitBuildBuy`, `_rebuildLotRouting`). New: `vfRtGateOffSim` = outermost unlocked gate entries (all 15 sites) by a thread that is neither the sim thread (`[0x11D8A8C]`) nor the route worker. The main-thread routing debug display and world load on the main thread are expected sources; the first 48 site/caller pairs are logged with main, sim and worker thread ids.
- **`Sites=1` (`rtsites.cpp`, 18 detours, each installs on its own):** #1 the worker's quadtree readers `0x6583E0`/`0x83B3A0` push a reader frame on the gate frame stack (worker only, not a gate frame), so `vfRtQtBare` counts worker entries into the leaves `0x681820`/`0x681E00` outside a reader (the docs/18 S2b `0x681E00` check, now for both leaves; must be 0), `vfRtQtReads` the outermost reads; the mutators `0x6823E0`/`0x682080`/`0x6819B0` count in `vfRtQtWriteInJob` while a job runs. #4 `ObjectService_DestroyById 0x939420`: `vfRtDestroyInJob`, `vfRtDestroyOffSim`. #5 `vfRtLotGridWorker` (worker entries into `0xC688A0`), `vfRtPendingInJob` (worker `ApplyPendingUpdates 0x82C420` with `[[world+0x95C]+0x10E]` set; `ecx` = world), `vfRtLotIdxRebuildInJob[4]` (`0xC68680` during a job: main, sim, worker, other). #6 `vfRtDrainSave`/`Brd`/`LotSave` (`0xAAC110`, `0x78AE40`, `0x8361B0` entered during a job). #9 `vfRtDebugSkip` (`0x818EA0` during a job). #10 `vfRtTeardownInJob` (`0x829410`, `0x82D140`, `0x8299E0` during a job; must be 0). `*WaitUs` fields stay 0 until S3b. "Job" = `g_callerTid` set (Mode 1 sync) or an async job running, as in S2a.
- **Exact G11 (docs/18 (3)):** a sim-thread plan (the `Route_PlanLocked` detour, outermost; in Mode 1 its time includes the wait) opens a window at its return; the sim thread's next routing touch closes it: an outermost `+0x918` acquire, an unlocked gate entry (1-12 or 13-15), a destroy, a quadtree write, or the next plan. `g11Plans`, `g11PlanUs`, `g11GapUs` (each gap capped at 1 s), `g11MovableUs` = sum of min(plan, gap), `g11End[6]` by closer. Movable ms/s = delta `g11MovableUs` / run seconds / 1000; the docs/18 go rule (>= 15 ms/s BP, >= 30 ms/s IP) applies to the Mode 0 slices. Shortcut: the rest of `IC_Route_Plan` around `Route_PlanLocked` is not timed, so the plan length is a lower bound.
- **Verify inside the owner stub (docs/68 §6 rule 3):** `[Verify]` probes rayEntry `0x6583E0`, lotGrid `0xC688A0`, rtLotContains, rtExitBuildBuy, rtRebuildLot and rtDestroy no longer skip when these stubs own their address: `ApplyVerify` finds the owner (`VfOwnerSlot`) and the stub calls `VfEnter` first; the gate's caller log reads the real return address behind Verify's leave thunk (`VfRealRet`).
- **Mode 1 reading:** the sim thread waits in `RunPlan`, so sim-thread in-job counts must be 0; worker counts divided by `workerCalls` give per-job frequencies (destroys, `Contains` calls, mutator calls per job are the new G11 window closers).
- **Selftest** `TestRouteSites`: v25 layout, gate sites 13-15 and their job counts, G11 (gate, nested plans, `+0x918` acquire, next plan, destroy and quadtree write closers, other threads open nothing), off-sim gate entries, worker reader frames (nested, dead frame, gate entry inside a reader still outermost), bare leaves, pending flag through `ecx`, every in-job counter on and off a job, every stub's index and trampoline slot, and a Verify thread probe counted through `RtEnter` with the real return address.
- **Risk:** no behaviour change; cost is one stub per call on hot sites (the quadtree leaves and mutators run per object move and per query on every thread). Read the run's iterations/s against the baseline before reading timings from the same load.

### S3a: `[TerrainGrave]` (docs/60 §2.2 #2, docs/38 C1 graveyard variant)

- **What:** the terrain cell rebuild `0xC2A840` (main thread on terrain LOD changes, docs/38) releases the cell's old collision shape (`cell+0x38`) with `PhysShape_Release 0x682760` (call returns to `0xC2A896`), zeroes the slot and stores the new shape, with no lock. `0xC2A570` (sim thread, and the route worker in Mode 1) ray-casts through `cell+0x38` without taking a reference [inferred, docs/38], so a release that drops the count to 0 frees the shape under a running ray (today's main-vs-sim race, and the worker's in S3b). With `Enabled=1` and the switch on, that one release is parked in a 64-entry ring (count untouched, shape alive); the next rebuild's entry runs the original release for every parked shape once no ray is in flight. Every other release goes to the game's code.
- **Facts it rests on [verified]:** `PhysShape_Release` = `add [ecx+0x4C],-1`, at 0 the deleting destructor (vtable slot 0, argument 1), returns the new count (disassembly; tools/emu `phys_shape_release.py` 205 of 205 against that model, 3/3 blocks). The swap ignores the return value: after `0xC2A896` `eax` is rewritten before any read, and the flags are rewritten by a `cmp` (disassembly). So the parked path returns straight to the caller.
- **In flight:** `g_tgInFlight` counts threads inside the game's `0xC2A570`, kept by the `[HeightCache]` detour around every call it passes on (misses, Verify, mode 0, the out-of-grid constant); a cache hit reads no shape. The interlocked add orders it before the ray's load of `cell+0x38`; the drain reads it with an interlocked op after the swap's store, so 0 means every ray that could have seen a parked pointer has ended. Ring full: wait (spin) for 0, then drain (`vfRtGraveSpins`).
- **Detours:** `0x682760` (5 bytes) plus the two `[HeightCache]` detours, which `[TerrainGrave]` installs without a table when `[HeightCache]` is off (then `heightMode` stays 0, and a probe write of 1 still runs the game's ray). `[Verify]` rtShapeRelease is counted inside the stub; Verify ray/shapeSwap skip as before, so these stubs count `vfSwaps`/`vfSwapsInRay` themselves.
- **Counters/switch (v25):** `tgReady`, `tgMode` (+0xD00, `--ab terraingrave`, ini `Mode` = start value), `vfRtGraveParked`, `vfRtGraveFreed`, `vfRtGraveSpins`, `vfRtGraveHeld`, `vfRtGraveRaces` (switch off: swap releases that freed the shape while a ray was in flight, i.e. the race happening; 0 by construction with the switch on, where `vfRtGraveParked` counts instead), `vfSwaps`, `vfSwapsInRay`. Pass for the A/B (docs/60 §2.3): races only in off slices, parked shapes freed (`Freed` follows `Parked`, `Held` <= 64), `heightMismatches` unchanged with `[HeightCache] Verify=1`, memory flat.
- **Selftest** `TestTerrainGrave`: the exe's release bytes under our detour, called from a fake swap site (return address stands for `0xC2A896`): other callers and shared counts untouched, switch off frees at once and counts the race, switch on parks, no drain while a ray is in flight, drain at the rebuild entry, ring full (spin, drain 64, park the 65th), switch back off; the ray's in-flight count; `heightMode` 1 with no table; Verify owner slot.
- **Risk / shortcut:** at most 64 parked shapes stay until the next rebuild, also across a world unload, so their destructor runs later than the game's would (that a cell shape then has no realm links left is inferred). Upgrade if a crash points there: drain at world teardown (docs/60 §2.5 hooks). The stub costs one compare on every `PhysShape_Release` in the game.
