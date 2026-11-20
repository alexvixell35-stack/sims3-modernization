# 36. Scheduler and job system of TS3W.exe (M-CORE4)

PLAN item 7, row M-CORE4. Static only: Ghidra headless on a private copy of the M-PHY project (`game-copy/ghidra-core4`, symbols CSV imported first); the game was not run. Cost numbers come from the existing `probe-out/profile/*simstack*.jsonl` files only. Steam TS3W.exe 1.67, image base `0x400000`, all addresses absolute.

Tags: **[verified]** = read in the decompile or disassembly; **[measured]** = from the sample files named in §7; **[inferred]** = reasoning, still to confirm; **[community]** = other projects.

## 1. Summary

- **One job system, one lock.** `JobManager` (interface at `[0x11CE2CC]`, vtable `0xFD97A4`) keeps every queue and list under one lock, released only while a job body runs [verified]. Its 23 `Lock_Acquire` call sites pass `0xFD9744` as the timeout pointer (a static `-1`); the lock object itself is `ecx` at each site, not `0xFD9744` [verified asm; correction from docs/15 [Verify] follow-up and docs/data/results/2026-10-15-verify-findings.md]. Below, "`0xFD9744`" names that lock by its call-site constant. Jobs carry a **thread mask** (`job+0x28`): `-1` = any thread, `1` = main, `2` = resource worker, `4` = sim thread [verified].
- **Threads that run jobs are "job thread records"**, not always OS threads: the main thread (mask `0x80000001`), the sim thread (`0x80000004`, registered at `0xD7E9E6` only when main != sim), one generic worker **`JobThread0`** (`0x80000000`) and the ResourceSystem worker **`JobThread5`** (mask 2) [verified]. Bit 31 means "also takes jobs from the shared queue"; the resource worker does not [verified].
- **Pumps:** `JobThread0` blocks in `RunJobs` forever; the main thread pumps once per frame with a timeslice (`JobManagerService_Update 0x599A10`, `[JM+0xCC]`, default 5, config `Timeslicing/JobManager` 1-100) [verified code; ms unit inferred]; the **sim thread pumps at `0xD81FC3` with no deadline and keeps going until its own queue and the shared queue are both empty** [verified]. So the sim thread also runs "any thread" jobs (CAS outfit and part loads, store/link jobs, some render jobs) whenever they are pending.
- **ThreadedObjects has a time budget; the other stages do not.** `ScriptHost_ThreadedObjects 0xD7F8B0` sets a deadline of `5000 x max(1, game time scale)` (constant `0x1020494` = 5000.0, scale `[0x11CDDB0]`; 5 ms at speed 1 if the unit is µs [inferred]) and resumes round-robin at `host+0x1700` next call [verified]. It checks only between tasks, so a task that does not yield overruns it (measured 607-640 ms/s in this stage, docs/28 G12). ScriptTick `0xD80BC0`, TaskObjects `0xD7FC60` and OneShotObjects `0xD74800` call no timer [verified].
- **Engine services have two update slots:** vtable `+0x1C` runs on the main thread (`ServiceList_Update 0x59ED20`, from the main loop) and `+0x20` on the sim thread (`ServiceList_SimUpdate 0x59ED70`, called four times in `Simulate`) [verified]. 60 services are constructed through `Service_ctor 0x59ECC0`; 12 have a non-stub sim slot (§3.3).
- **Threads:** 2 core threads (main, `Simulator`), 2 JobManager workers, 3 audio, 1 RenderWare file-system worker, N file watchers, an EA thread pool for the online "SIGS" jobs (3 at start, at most 16), and single threads for timers, the launcher pipe, IGA and movie decode (§4). The "~70 Blaze job threads" of docs/32 are **job bodies** (43 `BlazeWal*`/`Crossroads*` names), not 70 OS threads [verified: they sit in 3-slot job vtables at `0x1058AF4`...; the pool that runs them is inferred].
- **Sim-thread cost is small** in the existing samples: JobManager code 0.29% inclusive, jobs run in the pump 0.10% (~1 ms/s), remote-call dispatch 0.01%, lot add/remove 0 samples (no camera pans in these runs), sim-slot services 1.37% inclusive (~14 ms/s) [measured, §7].
- **Best candidate:** a deadline for the sim pump is a one-call patch, because `RunJobs` already supports deadlines (§8 C1). It only helps when lot streaming (or another burst) queues many jobs; measure with `[LotStream]` first (docs/28 §5.5).

## 2. Functions

Names added to `tools/ghidra/symbols/ts3w-1.67.symbols.csv` (tag `<docs/36>`), except where noted as existing.

### 2.1 JobManager (`0x599000`-`0x59B000`)

| Address | Name | What it does |
|---|---|---|
| `0x599160` | `JobManager_Get` | returns `[0x11CE2CC]` (the interface, `JM+0x18` of the service) [verified] |
| `0x599170` | `JobManager_Init` (vtable `+0x04`) | record for the calling thread at `+0x90` (mask `0x80000000`, thread id 0), then one OS `JobThread%i` per set bit of its argument [verified] |
| `0x5995D0` | `JobManager_Shutdown` (`+0x08`) | destroys every job thread, then drains with `RunJobs(rec+0x90, 0, nonblock)` until empty [verified] |
| `0x599620` | `JobManager_CreateJob` (`+0x10`) | 0x50-byte job (`Services/JobManager/Job`, free list `+0x48`, live list `+0x50`), state 1, mask -1, priority 0, refcount `+0x30`; returns `job+8` [verified] |
| `0x599CF0` | `JobManager_JobThread` (existing name; = **CreateJobThread**, `+0x14`) | `(mask, createOsThread, index)`: 0x44-byte record (`Services/JobManager/JobThread`, list `JM+0x88`), own queue `+0x30`, semaphore `+0x08`; with `createOsThread` names it `JobThread%i` and starts `JobThread_Proc` through `EAThread_Begin`; else binds the calling thread id [verified] |
| `0x59A8B0` | `JobManager_DestroyJobThread` (`+0x18`) | cancels its queue, joins the OS thread [verified] |
| `0x5991D0` | `JobManager_BindThread` (`+0x1C`) | sets a record's thread id to the caller [verified] |
| `0x599E00` | `JobManager_FindThreadByMask` (`+0x20`) | first record with `mask & arg` [verified] |
| `0x59A430` | `JobManager_RunJobs` (`+0x24`) | lock, `0x599E60(record, deadline, nonblock, 0)`, unlock [verified] |
| `0x599E60` | `JobManager_RunJobsLocked` | loop: if the record has bit 31 and the shared queue head has a higher priority than its own head, move it over; run own head; return when the deadline (QPC at `deadline+0x18`) passed, when `single` is set, or (nonblock) when both queues are empty; else park on the idle list `JM+0x38` and wait on the record semaphore [verified] |
| `0x599720` | `JobManager_ExecuteJob` | state 4, **drops the lock**, calls `fn(job+8, arg, 4)` (`4` = the "execute" opcode, always passed here; not a thread mask, docs/61), re-locks; then completion: dependents released, waiters woken, state 6 [verified] |
| `0x599FC0` | `JobManager_ReadyJob` | no fn: completes at once; mask -1: shared queue `JM+0x60` (priority order, `0x599920`) and wakes one idle thread or waiter; else the **first** record whose mask matches gets it in its own queue and its semaphore is posted; no match: cancel [verified] |
| `0x599920` | `JobQueue_InsertByPriority` | sorted insert on `job+0x48` [verified] |
| `0x59A190` / `0x59A350` | `JobManager_Submit` / `Job_Submit` | state 1 -> ready (`0x599FC0`) or waiting for dependencies (list `JM+0x58`, state 2) [verified] |
| `0x59A220` | `JobManager_WaitLocked` | until the job's state is 0/1/6/7: if the caller is a job thread, run its own jobs one at a time (`0x599E60(rec,0,1,1)`); else queue a `Services/JobManager/WaitNode` and sleep on its semaphore [verified, docs/25] |
| `0x59A500` | `JobManager_Wait` | lock wrapper of `0x59A220` (26 caller functions) [verified] |
| `0x599220`, `0x59A990`, `0x59A540`, `0x59A9F0` | `Job_GetState`, `Job_Release`, `Job_Cancel`, `Job_CancelTree` | state read under the lock; refcount drop and destroy; cancel; cancel with `JobManager/DepsToCancel` [verified code; names of the last two inferred] |
| `0x599200` | `JobThread_Proc` | `RunJobs(record, 0, 0)`: blocks for ever [verified] |
| `0x599970` | `JobManagerService_ctor` | `Service_ctor(0x2A8D18B)`, vtable `0xFD9810`, `+0xD0` = single-threaded flag, `+0xCC` = 5 [verified] |
| `0x5992D0` | `JobManager_ReadTimeslices` (existing name; = **JobManagerService_Init**, slot `+0x10`) | `JobManager_Init(1)` (one generic worker), main record mask `0x80000001` (`0x80000005` when `+0xD0`, so the main thread also takes sim jobs), sets `[0x11CE2CC]`, registers the `Timeslicing` / `JobManager` value `+0xCC` (1-100) [verified] |
| `0x599A10` | `JobManagerService_Update` (slot `+0x1C`, main thread) | deadline = timeslice `[+0xCC]` (stopwatch mode 4), `RunJobs(main record, deadline, nonblock)` [verified] |

### 2.2 Threads, ids and remote calls

| Address | Name | What it does |
|---|---|---|
| `0x4E2F30` / `0x4E2E30` | `EAThread_Begin` / `EAThread_Entry` | `_beginthreadex` suspended with the stack size from the parameters (`+0x04`), priority (`+0x08`, `0x4E2AF0`), ideal processor (`+0x0C`, `0x4E2C50`; -1 = round-robin counter `[0x11CB538]`), name (`+0x14`, set through exception `0x406D1388`), then `ResumeThread` [verified]. 10 call sites |
| `0x4E31A0` / `0x4E30A0` | `EAThread_BeginRunnable` / `EAThread_EntryRunnable` | same for an object with `Run` at vtable `+4` [verified]. 2 call sites |
| `0x4E2C50`, `0x4E2AF0` | `EAThread_SetIdealProcessor`, `EAThread_SetPriority` | `SetThreadIdealProcessor` modulo the CPU count; `SetThreadPriority` with fallback steps [verified] |
| `0x4E2760`, `0x4E2810` | `EASemaphore_Wait`, `EASemaphore_Post` | `WaitForSingleObject` with a `GetTickCount` deadline; release [verified wait; post inferred] |
| `0x7D99F0` / `0x7D99D0` | `GetSimThreadId` / `GetMainThreadId` | `[0x11D8A8C]` / `[0x11D8A88]` [verified]. Callers: physics writers (docs/33), `0x7D98C0`, `MonoScriptHost_Init`, `IC_GameUtils_IsSingleThreadedMode`, `0x76AF90`, `0xEC02E0`; main id also `0xADAF60`, `0xC6B780`, `0x607970`, `0x6E3BF0`, `0x723220`, `0xECA350` |
| `0x7D9A00` / `0x7D99E0` / `0x7D9A10` | `SetSimThreadId` / `SetMainThreadId` / `IsSingleThreaded` | setters; `main == sim` [verified] |
| `0x7D98C0` | `RemoteCall_Dispatch` | `(selector, async)`: if the caller is the target thread, run inline; else job with mask 4 (selector 1) or 1 (selector 0), fn `0x7D9840`, submit, wait unless async [verified, docs/28 §5.2] |
| `0x7D9840` | `RemoteCall_JobFn` | runs the call's `+0x10`; on the execute opcode `4`, when bit 0 of the job's mask (`job+0x28`, i.e. a main-thread call) is set and `[0x11D1530]` is set, first offers it to `0x6E33F0` [verified shape; meaning of the offer inferred]. Correction (docs/61): the `4` is the opcode, not the sim-thread mask |
| `0xABE9C0`, `0xABEA50`, `0xC649A0`, `0xC64AC0`, `0xB35DB0` | `PostRemoteMethodCall_*` | async posts (heap `Services/RemoteMethodCall`); `0xABE9C0`/`0xABEA50` = lot add/remove (docs/28 §5) [verified] |
| `0x82AA70` / `0x82ACF0` | `RunOnSimThreadSync` / `RunOnMainThreadSync` | job with mask 4 / 1, submit and wait; only caller `0x835110` (routing system) [verified code, caller role inferred] |
| `0x76B1B0` / `0x76B610` / `0x76B470` | `ScriptThreadService_ctor` / `_Init` / `SimulatorThread_Proc` | service `Scripting/Service/Thread`; Init either binds the sim id to the caller (single-threaded) or starts **`Simulator`** (512 KB stack, ideal processor 1) and waits up to 60 s for it to start the script host [verified] |
| `0xEC9C20` | `App_ReadMultithreaded` | `[Script] Multithreaded`; command-line `MT` forces 1, `ST` forces 0, `SP` pins the process to its lowest CPU [verified strings at `0x10F210C`-`0x10F2114`; that they are command-line switches is inferred] |
| `0xEC6D90` | `App_ReadTimeslices` | `[Timeslices]` `SimService`, `JobManager`, `ResourceSystem`, `TextureCompositor`, four values each, clamped to [1, `[0xF98970]`] [verified] |
| `0x4F35B0` / `0x4F34F0` | `Stopwatch_Init` / `Stopwatch_SetDeadline` | stopwatch with a unit mode; deadline = QPC + count / units-per-tick [verified; mode 3 = µs, 4 = ms inferred from EAStdC's `Stopwatch` unit order] |

### 2.3 Services

| Address | Name | What it does |
|---|---|---|
| `0x59ECC0` | `Service_ctor` | `(id, x)`, base vtable `0xFDDE44` (stub `0x861C50` in `+0x1C`/`+0x20`) [verified]. 60 constructors |
| `0x59ED20` | `ServiceList_Update` | each enabled service with flag bit 0: vtable `+0x1C(dt, x)`; caller `0x588E00` (main loop) and `0xB36D10` [verified] |
| `0x59ED70` | `ServiceList_SimUpdate` | flag bit 1: vtable `+0x20(dt, x)`; 4 calls in `MonoScriptHost_Simulate` (`0xD81AA2`, `0xD81C8E`, `0xD81E73`, `0xD81F55`) [verified] |
| `0x59EFE0` | `ServiceList_Find` | lookup by id (58 callers) [verified] |
| `0x59F0C0`, `0x59EDC0` | `ServiceList_CountDeps`, `ServiceList_InitOne` | dependency counts from vtable `+0x2C`/`+0x30`; init when ready [verified] |

## 3. The sim-thread loop

### 3.1 `MonoScriptHost_Simulate 0xD81840` [verified, extends docs/04 F2/F17, docs/28]

Per iteration: frame marker, `ScriptHost_ScriptTick(0)` (walks the host's lists at `host+0xBD8`/`+0xC04` and the handle map, no timer; that these are pending task-state changes is inferred), `ScriptHost_TaskObjects` (every Task-type script task, no timer), GC step (`GC_try_to_collect` with the ~1 ms adaptive budget), finalizers (whole-queue drain after 200 busy frames), SACS animation pass `0x571AD0`, then either a pause path (services `SimUpdate` with dt 0, sleep up to 10 ms) or the step path: up to 10 steps (`[host+0xC14] x [0x10049E4]`), for each `0x7FD520`, `Locomotion_Update 0x83CED0`, `Animation_UpdateActors 0x571B90`, `ServiceList_SimUpdate`, then `ScriptTick(n)`, `OneShotObjects` and `ThreadedObjects` (only when `host+0xA2C < 1`), catch-up steps, publish. After either path: `[host+0xB78]` callback, **the remote-call pump** (`JobManager->RunJobs(host+0xAC0, 0, 1)` at `0xD81FC3`, only when `host+0xAC0` is set, i.e. main != sim), `IdleSimulationCycle 0x7694B0` (pacing) [verified].

### 3.2 Stage budgets and deadlines

| Stage / pump | Thread | Budget | Source |
|---|---|---|---|
| ScriptTick `0xD80BC0` | sim | none | no timer call [verified] |
| TaskObjects `0xD7FC60` | sim | none (all tasks) | no timer call [verified]; 72-83 ms/s [measured, docs/28 G12] |
| OneShotObjects `0xD74800` | sim | none | [verified]; 0.1-0.2 ms/s |
| ThreadedObjects `0xD7F8B0` | sim | `5000 x max(1, [0x11CDDB0])` stopwatch mode 3 (5 ms at speed 1 if µs [inferred]), round-robin cursor `host+0x1700`, checked between tasks | [verified]; 607-640 ms/s at ~30 it/s, i.e. ~20 ms per call, so tasks overrun the budget [measured docs/28, inference here] |
| GC step | sim | ~1 ms adaptive (`EA_gc_frame_budget`, +-100 per step) | [verified, docs/13] |
| Finalizers | sim | none (count `GC_finalizers_per_call`, full drain after 200) | [verified, docs/04 F2] |
| Catch-up | sim | at most 10 steps per iteration | [verified] |
| Remote-call pump `0xD81FC3` | sim | **none**; runs own queue, then the shared queue, until both are empty | [verified] |
| JobManager main pump `0x599A10` | main | timeslice `[JM+0xCC]`, default 5, config 1-100 (`[Timeslices] JobManager=10,10,25,50` in Sims3.ini, link inferred) | [verified code] |
| `JobThread0`, `JobThread5` | workers | none (block on the record semaphore) | [verified] |
| `ScriptThreadService_Init` start-up wait | main | 60 s for the `Simulator` thread | [verified] |

### 3.3 Services with a sim-thread slot (`+0x20` not the stub)

`ResourceChangeMonitor` (`0x733D30`), `Services/DebugDrawHostService` (`0x740A70`), service `0x868EE0` (`0x869000`, audio range [inferred]), `CameraService` (`0x96C2F0`), `InGameAdService` (`0x9E52C0`), `0xA23840` (`0xA23B50`), `UI/ThumbnailManager` (`0xD4D6D0`, takes lock `0x1088330`), `0x7F14E0` (`0x7F1530` -> `0x516AC0`, UI range [inferred]), `World/Service` (`0xC7E300`), `Misc/GameState/StateController` (`0xEC5340`), the ScriptMessageServer service (`0x76B1A0`, slot `+0x20` of vtable `0x1007B74`; docs/61 corrects the earlier `Services/Script` label: `ScriptServices_Register 0x76B540` creates both services) [verified slots; whether each service's flag bit 1 is set is not read, so "runs" is inferred]. Main-thread slots (`+0x1C`) include `JobManager`, `World/Service` (`WorldManager::Update`, docs/28 §5), `RenderSystem`, `UTFWin`, `UI/IME`, `ObjectDesignerService`, `SimService`, `TextureCompositor`, `MessageServer`, `NPMessageServerService` [verified slots].

## 4. Threads

| Thread (name) | Entry | Creator | Count | Role | Runs C#? |
|---|---|---|---|---|---|
| Main | CRT `___tmainCRTStartup 0xF7F0B1` -> main loop `0xECA960` | OS | 1 | Window, render, UI, services `+0x1C`, JobManager main pump (kind 1 and shared jobs), lot scoring (docs/28 §5) | UI ICs only through the sim thread [inferred] |
| `Simulator` | `SimulatorThread_Proc 0x76B470` (host vtable `+0x14` = run loop -> `Simulate`) | `ScriptThreadService_Init 0x76B610` via `EAThread_Begin`, only with `[Script] Multithreaded=1` (default) | 1 | All script, GC, locomotion, animation, services `+0x20`, remote-call pump (kind 4 and shared jobs) | **yes, the only one** |
| `JobThread0` | `JobThread_Proc 0x599200` | `JobManager_Init(1)` from `0x5992D0` | 1 | Generic JobManager worker: shared-queue jobs (mask -1) | no |
| `JobThread5` | `0x599200` | `AsyncLoader_Init 0x7274D0` (`CreateJobThread(2,1,5)`) | 1 | ResourceSystem async loads (mask 2) | no |
| EA audio system | `0x8B2A00` | `AudioSystem::Init 0x8B3C20` | 1 | EA audio command processing (docs/35) | no |
| `RWAudioCore Dac` | `0xF00170` | `0xF00D40` | 1 | Mixer and DirectSound output (docs/35) | no |
| `RwAudioCore Device WatchDog` | `0xEDB5E0` | `0xEDC5C0` | 1 | Audio device watchdog (docs/35) | no |
| RenderWare file system worker | `0x401BE0` | `0x401CA0` (callers `0x401D10`, `0x401E70`, `0x4024F0`) | 1 per device [inferred] | Queue under lock `0xF97F84`, runs request `+0x44` (`Services/rw/core/filesystem/Manager` tags) [verified shape, role inferred] | no |
| File watchers | `0x4DB220` | `0x4DB290` (`_beginthreadex` directly, `0x4DB355`) | one per watched folder; "several dozen" [community, docs/03] | `EAIO` directory change notification; S3SS stack fix site | no |
| `CallbackProcessor` | runnable via `EAThread_BeginRunnable` | `0x4E9100` (lazy, first timer callback; lock `0xFB8E30`) | 1 | Timer callbacks [inferred from the name and deadline fields] | no |
| EA thread pool (SIGS `ThreadJobManager`) | `EAThreadPool_WorkerProc 0x4E1DF0` -> job `+4` -> `0x8942F0` -> `JobRunner_Loop 0x893FB0` [inferred link] | `EAThreadPool_Init 0x4E2210` from `0x893660` (max 16, start 3; every idle worker ends after 500 ms, no minimum: `0x4E1EDF`/`0x4E1EFD` [verified, docs/270 §40]) | 3, up to 16 | Online jobs: Blaze WAL/Crossroads (43 job names), upload, `ServiceStatusJobThread` `0x88B6C0`; servers are dead | no |
| `NamedPipeListenerThread` | `0x60CB00` | `NPMessageServerService` (`0x60BFB0`, site `0x60CCC0`) | 1 | Launcher pipe | no |
| `IGACacheRefresherThread` | runnable | `0x9E6350` | 1 | In-game ad cache | no |
| IGA Lua thread | `0xF49730` (`iga_lua_threadfunc`) | `CreateThread` at `0xF49820` | 1 | Ad SDK script | no |
| Network library threads | `0xF6F070`, `0xF6F720`, `0xF6DC50` | `CreateThread` in `0xF6F0F0`, `0xF6F900`, `0xF6F570` | up to 3 | Third-party online code (HMAC pads, `recv-event-crit`) [inferred] | no |
| `MoviePlayer2 Decode Thread` | `0xF1C800` | `0xF1CF50` | while a movie plays | VP6 decode (docs/35) | no |

Not EA threads: DXVK and driver threads, S3SS/S3PP/ts3native workers (our route worker, save copy). Mono and Boehm start no threads (docs/29) [verified]. The full list was found from every caller of `EAThread_Begin`, `EAThread_BeginRunnable`, `_beginthreadex` and `CreateThread` [verified: no other import or wrapper creates threads; `_beginthread` is not imported].

## 5. Queues: who posts, who drains

| Queue | Posted by (thread) | Drained by (thread) | Lock |
|---|---|---|---|
| JobManager shared queue `JM+0x60` (mask -1) | any: CAS outfit load `0x5E9AA0`, CAS part data `0x5B2290`, store and link jobs `0x9ED1B0`/`0x9ED870`/`0x9EB390`, `0x88E300`, `0xA48BE0`/`0xA48C50`, `0xAE2170`, render `0x6EEC60`/`0x6E8810`/`0x6EBB70` (mask from the caller) [verified posts; masks not written in the decompile are the default -1, inferred] | `JobThread0` (blocking), main pump (timeslice), **sim pump (no budget)** | `0xFD9744` |
| Main record queue (mask 1) | `RemoteCall_Dispatch(0, ...)` (lot renderer create `0xABE970`, lot rebuild `0xC6BCC0`, `0xAC01E0`), imposter `0xADAF60`/`0xADA250`/`0xADA400`, `0xB0B970`, `0x983970`, `0xC638E0`, `0xC63840`, `0xC27CF0`, `0x9EEB50`, wallpaper tool `0xA61AE0`, houseboat `0xB9ECE0`, `RunOnMainThreadSync` | main pump | `0xFD9744` |
| Sim record queue (mask 4) | `RemoteCall_Dispatch(1, ...)`: lot add/remove (main, async), `0xC649A0`/`0xC64AC0`/`0xB35DB0` (async), `0x8690D0`, `0xC65AD0` (sync); LotRenderer `0xAEA680` -> `0xAA65B0`/`0xAA6650`; ThumbnailManager `0xD503EF`-`0xD5C200` (11 sites); lot thumbnail helper `0x743470`; named-pipe messages `0x60B210`; `RunOnSimThreadSync` [verified masks] | sim pump `0xD81FC3` | `0xFD9744` |
| Resource record queue (mask 2) | AsyncLoader `0x7297E0` [inferred mask] | `JobThread5` | `0xFD9744`, then resource lock `0xFB2CD0` (N15) |
| Dependency wait list `JM+0x58` | `Job_Submit` with unmet dependencies | moved to ready by `ExecuteJob` | `0xFD9744` |
| Script host lists `host+0xBD8`, `+0xC04` | script host internals | `ScriptTick` (sim) | none (sim thread only) [inferred] |
| Physics sim messages | physics writers off the sim thread (docs/33) | sim thread | message refcounts atomic |
| `Misc/Camera/SimThreadCameraMessage` (`0x96C0D0`) | camera (main) | sim thread [inferred] | n/a |
| SIGS `ThreadJobManager` container (`0x893D90`, `0x893EC0`) | online code | pool threads | critical sections at `+0x20`, `+0x48` |
| CallbackProcessor timer table | any | `CallbackProcessor` | `0xFB8E30` |
| rw file system request list | resource code | file system worker | `0xF97F84` |

**Synchronous waits** (`JobManager_Wait` callers, 26 functions): render device work (`0x6E8810`, `0x6EBB70`, `0x6ED0A0`, `0x6F0DB0`, `0x6F1390`, `0x6F1D90`, `0x6F3C90`, `0x6F3D30`, `0x6F3F90`), CAS (`0x5AF0D0`, `0x5E84D0`), imposter pump `0xAD97E0` (docs/25), `RemoteCall_Dispatch` sync posts, `RunOn*ThreadSync`, `0x9508A0`, `0x983970`, `0xA48BE0`, `0xA48C50`, `0xA60AA0`, `0xA61190`, `0xA61AE0`, `0xB0B970`, `0xC27CF0`, `0xC63840`, `0xC638E0` [verified]. A job thread that waits runs its own queue meanwhile (help-while-wait), a non-job thread sleeps [verified].

## 6. Locks

| Lock | Protects | Notes |
|---|---|---|
| JobManager lock (`ecx`; call sites pass `0xFD9744` as the timeout pointer, docs/15 [Verify] follow-up) | every JobManager list, job state, records | One lock for all; dropped while a job body runs and while a thread waits [verified]. Hot only if jobs are tiny [inferred] |
| Record semaphores (`record+0x08`), WaitNode semaphores | idle workers, waiters | `EASemaphore_Wait` [verified] |
| `0xFB8960` | EA thread pool | [verified] |
| `0xFB8E30` | CallbackProcessor | [verified] |
| `0xF97F84` | rw file system queue | [verified] |
| `0x1088330` | ThumbnailManager (sim slot) | [verified] |
| `0xFB2CD0` | global resource lock (docs/15, N15) | not part of the job system |
| `RoutingWorld+0x918` | routing (docs/18, L) | not part of the job system |

## 7. Sim-thread cost (existing samples)

Source: all 35 `probe-out/profile/*simstack*.jsonl` (31 non-empty, 10,238 samples: BP 4,076, IP 4,439, SV 1,723; base and fix configs mixed). Method of `tools/harness/sample-split.ts`: leaf-most TS3W frame = self, any of the first 24 TS3W frames = inclusive (approximate, stacks are scanned), nearest function from `game-copy/analysis/functions.csv`. 1% is about 10 ms/s (docs/28). The script stayed in the session scratch folder [measured].

| Block | Self | Inclusive | BP / IP / SV incl. |
|---|---|---|---|
| JobManager code `0x599000`-`0x59B000` | 0.07% | 0.29% | 0.20 / 0.34 / 0.41% |
| `RunJobs` (`0x59A430`, `0x599E60`) | 0.01% | 0.09% | 0.07 / 0.07 / 0.17% |
| Jobs run on the sim thread (`ExecuteJob 0x599720`) | 0.00% | 0.10% (~1 ms/s) | 0.10 / 0.07 / 0.17% |
| `JobManager_WaitLocked` | 0.00% | 0.06% | 0.05 / 0.09 / 0.00% |
| `EASemaphore_Wait 0x4E2760` | 0.35% | 0.41% | 0.27 / 0.59 / 0.29% (callers mostly not on the stack) |
| `RemoteCall_Dispatch` | 0.00% | 0.01% | |
| Lot add/remove (`0xABD3C0`, `0xAC1130`, `0xAB4C00`, `0xABFAC0`) | 0 | 0 | no camera pans in these runs |
| `ServiceList_SimUpdate` | 0.04% | 1.37% (~14 ms/s) | 1.37 / 1.26 / 1.63%; below it mostly `0x516AC0` (UI range) and the animation pose path `0x566010`/`0x582830`/`0x571B90` [stack-scan overlap likely, inferred] |
| ScriptTick / TaskObjects / ThreadedObjects / OneShot | 0.11 / 0.08 / 0.26 / 0.00% | 0.34 / 0.71 / 3.05 / 0.01% | the stage frames are often not on the 24-frame scan; use the docs/28 G12 timers instead |

So the job system itself costs the sim thread ~1-4 ms/s in normal play; the open question is the burst case (lot streaming, many queued jobs), which these files do not contain. docs/28 rows unchanged except the notes in N16 and §5 (this doc).

## 8. Candidates

| # | Candidate | Feasibility | Blockers (docs/28 letters) | Gain |
|---|---|---|---|---|
| C1 | **Deadline for the sim pump at `0xD81FC3`.** Build a stopwatch deadline (as `0x599A10` does) and pass it instead of 0; jobs left over stay queued in order for the next pass | **easy**: `RunJobs` already supports deadlines; ~20 bytes of patch or a detour of the call | O: FIFO kept, but a job posted synchronously from the main thread now waits longer (main-thread hitch instead of sim hitch); one job is still atomic, so a single big lot add still runs whole (needs object chunking, S3SS throttle, docs/28 §5.4) | only in bursts; measure `pumpMaxMs` with `[LotStream]` first |
| C2 | **Sim pump skips the shared queue**: clear bit 31 of the sim record mask (`0x80000004` -> `0x4`, set at `0xD7E9E6`) | easy (one immediate) | none for correctness if "any" jobs are really thread-agnostic [inferred]; they then wait for `JobThread0` or the main pump | measured ~1 ms/s average; removes CAS/store job spikes from the sim thread |
| C3 | **More generic workers**: `JobManager_Init` argument 1 -> e.g. `0x7` (3 workers) in `0x5992D0` | easy (one immediate) | none known; jobs already share one priority queue, so no work stealing is needed | load/CAS throughput, not sim-thread time [inferred]; combine with C2 |
| C4 | Work stealing between thread-specific queues | **not useful** | affinity is the point of masks 1/2/4 (M, S) | n/a |
| C5 | Second resource worker (mask 2) | possible | global resource lock `0xFB2CD0` serialises decode (N15) | small (docs/08) |
| C6 | **Move posted sim-thread work off the sim thread** (ThumbnailManager mask-4 jobs, LotRenderer `0xAA65B0`/`0xAA6650`, named-pipe messages, lot add/remove) | per job only | S (scene, lot and object state owned by the sim thread), E (scene events), M where a job calls script | unknown; audit each kind with a per-kind counter on `ExecuteJob` before any change |
| C7 | Services' sim slots (`+0x20`) that do non-sim work (UI `0x7F1530`, camera `0x96C2F0`, thumbnails `0xD4D6D0`) could move to the main slot | needs per-service reading | S, E [to find] | ≤ 14 ms/s in total (1.37% incl.) |
| C8 | ThreadedObjects budget: tasks that ignore the 5 ms slice are the cost (docs/04 F17) | the budget already exists | M (cooperative tasks) | in place only: fix the slow tasks (hot-method work) |
| C9 | Dead online threads (SIGS pool, IGA, Lua, named pipe, network block) | stub or S3SS Online Nuke | none for gameplay | address space and wake-ups, not sim time |

**Next step:** run the built `[LotStream]` probe (docs/28 §5.5); it already times the outermost sim-thread `RunJobs` (`pumpMs`, `pumpMaxMs`). Add a per-job-kind count in `ExecuteJob 0x599720` (fn pointer `job+0x10` histogram, sim thread only) to the same probe; if `pumpMaxMs` reaches tens of ms, build C1 (+C2) behind a default-off switch.

## 9. Method notes

- The docs/33 §8 recipe worked: the allocator tags (`Services/JobManager/*`, `Threads/ThreadPool/*`, `SIGS/*`) named the structures; vtable dumps (`0xFD97A4`, `0xFD9810`) gave the interface slots; callers of `0x599160` with the vtable offset after them classified create/run/destroy.
- New scratch scripts (not committed): a caller scan that prints the pushes before and the instructions after each call (vtable offsets on the returned object), a service scan (vtable after `Service_ctor`, slots `+0x10`/`+0x1C`/`+0x20`, ctor strings), a pointer finder for functions only reached through vtables Ghidra did not type.
- Masks set through stack variables are lost in the decompile; read the disassembly before relying on a "-1" job.

## 10. Open items

- Flag bits of each service (main/sim slot enabled) are set outside the ctor; not read.
- Which jobs land in the shared queue in play, and how often the sim pump runs them (counter above).
- `0x6E33F0` hand-off in `RemoteCall_JobFn` (execute opcode 4, main-mask jobs; docs/61).
- Units of stopwatch modes 3/4 (µs/ms) and the link from `[Timeslices]` to `[JM+0xCC]`.
- The pool that runs the Blaze job objects (vtables `0x1058AF4`...).
