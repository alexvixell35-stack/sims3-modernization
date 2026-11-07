# 538. The main thread, function by function (Steam 1.67)

Status (2026-11-06): static call graph plus sampled stacks, every graph function named, the top layers read from a
Ghidra decompile. **9,820 functions** reachable from the main thread's 27 entry points. They are named in
`tools/ghidra/symbols/ts3w-1.67.mainthread.csv`, and **1,935** of them have a name and behavior read from the decompile. This
document gives the behavior: the startup, the frame, the window procedure and what each of the 24 engine services does
each frame. Names, addresses and behavior only. No decompiled code is in the repository. Related: docs/41 (frame
loop), docs/71 (main-thread profile), docs/167 §7 (services stage timers).

There is **no separate render thread** (docs/41, checked again here). Rendering is the second half of the main thread's
frame: the main thread draws, presents and waits on its own frame jobs. The JobManager workers run those jobs, and the
main thread runs ready jobs itself while it waits (§4.3).

## 1. Method and files

- **Static graph.** The graph starts from 27 roots: `App_Run 0xECBE30`, `MainLoop 0xECA960`, `Canvas_WndProc 0x4118C0`,
  and the 24 service updates (vtable `+0x1C`, docs/167 §7.1). Each function is decoded by recursive descent. Edges come
  from direct calls, tail jumps, switch tables, function pointers and vtables stored in the body. 48 utilities with more
  than 300 callers are not expanded. Result: 40,038 edges.
- **Sampled stacks.** 4,234 main-thread stack samples (`mainstack` sampler, al262 and earlier runs, BP and IP; SV did
  not run) added 500 edges
  through indirect calls the static pass cannot see. These added 1,132 functions, mostly behind virtual calls. A sampled
  edge can skip frames (FPO), so it shows "reached from", not always "called directly".
- **Each function is listed under the root that reaches it first** (BFS). The per-root counts in §8 are exclusive.
- **Decompile.** Ghidra 12.2 headless, no auto-analysis: the graph's functions are created and named first, then
  decompiled per root. 8,731 functions were decompiled in the first pass. A second pass decompiled the 1,132 functions
  found by sampling (§6.4): 1,131 bodies, and Ghidra finds no function at `0x615F6E`.
- **Names, in priority order:**
  1. Read from the decompile (1,935 functions, 1,264 verified and 671 inferred: depth 0-2 of each root, depth 3 below
     `MainLoop` and `RenderSystem`, and
     every function found by sampling except `0x615F6E`, where Ghidra finds no function).
  2. The project CSV and docs names (440).
  3. An EA heap or config tag in the body (735).
  4. A string the body uses (161).
  5. A body pattern (75): getter, constant return, thunk.
  6. A placeholder `<parent>__hN` (6,474): the Nth callee of the first parent that names it.

  The CSV comment says which: `[verified: read from the decompile; ...]` (1,264), `[unverified: inferred ...]` (671), or
  `[unverified: <source>]`.
- **Import:** `-postScript ImportSymbols.java tools/ghidra/symbols/ts3w-1.67.mainthread.csv keep` after the project CSV.
  With `keep`, names you set yourself stay.

## 2. Startup and shutdown

1. `App_Run 0xECBE30` builds the command line object (`EACommandLine_Ctor 0x4EB270`, tokenizer `0x4EAC00`), runs
   `App_RunLoop`, then destroys it (`0x4EA380`).
2. `App_RunLoop 0xECBD00` stores its argument (`0x11FFB18`, `0x11D3FEC`). It returns 0 at once when `0x11FFBB4` or
   `0x11FFBBC` is `0xB7`. Otherwise it allocates the `Services/Main` object (0x74 bytes, `Services_Main_Ctor 0xEC9FE0`).
3. `App_StartupFlags 0xECB600` (1,778 bytes) does the startup checks. These cover the uninstall flags, `-fpuexceptions`
   and `ead_gl_remove`. It records the main thread id, checks the launcher exe and sets up config and the crash reporter.
   A return of 0 aborts. Otherwise `MainLoop` runs.
4. After `MainLoop`: when `0x11CDBC8` is set, `App_BuildStartupErrorMessage 0xECA870` and `0xECA5A0` (string ids 4/5)
   show a message box. `App_Shutdown 0xEC9B40` tears down the renderer, D3D9 and the profiler. `0xECA300` frees
   `Services/Main`.

## 3. The frame: `MainLoop 0xECA960`

Before the loop: config key `NoInactiveIdle` is read into a flag and a stopwatch is started (`Stopwatch_Init(4, 1)`).
The loop ends when the load state's id (`[0x11FF7AC]+0x28`) is `0x28D869F` (quit). Each iteration:

1. **Load state.** `LoadState_Update 0xEC6C30`: the lowest set bit of the wanted mask picks the state.
   `LoadState_CreateState` builds it, `LoadState_SwitchState` swaps it in, then its vtable `+0xC` runs. The sub-state
   ctors are `0xEC55F0..0xEC5C50` (ids 2, 8, 4, 0x10, 0x20, 0x40) and the warnings state `0xEC65D0` (id 1).
2. **Idle.** ResourceSystem vtable `+0x58` answers "busy loading". When the resource system is not busy and
   `NoInactiveIdle` is unset, `App_IdleIfNotForeground` sleeps 10 ms while the window is not in front.
3. **Lost device pauses the clock.** The device-lost byte `GraphicsDevice+0x8D` sets game-clock pause flag 4
   (`GameClock_SetPauseFlags`), and clearing the byte clears the flag. Both take the clock lock (`0xFD7C84`).
4. **Services.** `ServiceManager_Update 0x588E00` calls `ServiceList_Update 0x59ED20`, which calls each enabled
   service's `+0x1C` update in list order (§5).
5. **Render, every iteration, or while loading only every 100 stopwatch units** (`Stopwatch_GetElapsedScaled`,
   restarted each time; unit inferred as ms, so loading screens draw at about 10 fps):
   - `VideoCapture_RenderFrame` when the capture queue is not empty.
   - `Renderer_BeginFrame 0x6EBB70` (§4.1).
   - `Frame_RenderAndPresent 0xEC9F00` with argument 1, when `0x11D1530` is set (§4.2).
   - `Renderer_EndFrame 0x6E8810`.
   - `Canvas_ApplyPendingMode`: a pending display mode change and device reset.
6. **Clock.** `GameClock_FrameTick`: QPC delta, scaled by the clock mode (`Stopwatch_SetClockMode 0x4F30E0` sets mode
   and scale), frame counter, then `GameClock_Advance`. That takes the lock, clamps, and posts message `0x2CB180C0` when
   the time changes.

The sim thread does not run here. The main thread meets it in two places: the `ScriptThread` service (§5) and the
FrameDatabase consume in `RenderSystem` (§4.1).

## 4. Render path

### 4.1 Consume and frame jobs

- `RenderSystemService_Update 0x6E3620` (a service, so it runs in step 4, before the render part):
  1. Device lost: `RenderSystem_MarkDeviceLost`.
  2. `FrameDb_ConsumeAndInterpolate` takes the FrameDatabase lock with `TryEnterCriticalSection`, so on contention
     there is no wait and no consume that frame. It reads a 3-slot ring (`FrameDb_GetReadPair` / `AdvanceReadSlot`).
     It drains the event queue (`FrameDb_DrainEvents`, swapped under its own lock). The interpolation fraction comes
     from `FrameDb_AdvanceRenderClock` and is stored by `Renderer_SetRenderTime`. Per-type handlers come from the
     table at `0x11CDD34`, including `FrameDb_InterpolateObjectState`.
  3. Then the scene object manager update (blend timers), the CAS probe toggle, camera sync,
     `Renderer_ProcessRenderRequests` and `RenderSystem_PruneUnusedRefs`.
- `Renderer_BeginFrame 0x6EBB70` runs the following:
  - `LightManager_FrameUpdate`: time-of-day queue, day/night fade, probes (`0x6AE580..0x6B5DA0`).
  - It drains `RenderingJobs_Pop` and runs deferred callbacks.
  - The pick readback (`RenderPickManager_*`).
  - `Renderer_ChooseFrameJobs`, then it submits up to five frame jobs. Their handles are at `Renderer+0x310..+0x320`
    (`Job_Submit`, `Job_AddDependency`).
- `Renderer_WaitFrameJobsAndDraw 0x6F3C90`:
  - It waits on the cull and sort jobs (flag `+0x28F`) and runs the deferred callbacks.
  - Then `Renderer_CullMainView 0x6F1B00`, with the frustum from `CullFrustum_FromCamera 0x68D9E0`.
  - The clear constants `0xE3CA8205` / `0xEFEE8205` are chosen by view flags.
  - `SceneCullFilter_Render 0x6DF8E0` and `0x6E7620` are per-view variants.
- `Renderer_EndFrame 0x6E8810` waits on the frame jobs, then `RenderingJobs_Advance`.

### 4.2 Draw and present

- `Frame_RenderAndPresent 0xEC9F00` runs the following in order:
  1. `Device_BeginScene`, then the viewport from the back buffer.
  2. The UI: WindowManager vtable `+0x30`, `UTFWin_DrawDisplayLists 0x50EB20` (2D ortho setup and draw).
  3. The capture overlay (vtable `+0x48`), then `Device_EndSceneAndPresent`.

  With the device lost it sleeps 30 ms (`SleepEx`).
- `Device_Present 0x611680` calls `IDirect3DDevice9::Present` (vtable `0x44`). `D3DERR_DEVICELOST` sets `+0x8D`.
  Any failure goes to `Device_TryReset 0x610AB0`. That resets only when this process owns the foreground window (unless
  forced): it releases the default pool and resets.
- Gamma: `0x6C2290` builds the identity ramp, `0x6C29E0` the brightness curve. Both go through `Device_ApplyGammaRamp`,
  called from the load-state tick.

### 4.3 Waiting on jobs

`JobManager_WaitLocked 0x59A220` is how the main thread waits for a frame job. It finds or makes a per-thread wait node
(`Services/JobManager/WaitNode`). While the job is not done, it runs other ready jobs inline. Then it releases the lock
and blocks on the semaphore `0xFD9744`. So "main thread waiting on render jobs" in a profile can be main-thread time
spent running other jobs.

## 5. Window procedure: `Canvas_WndProc 0x4118C0`

- The Canvas comes from `GWL_USERDATA`; with none, `DefWindowProc`.
- A listener at `Canvas+0x5C` sees the raw message first (event `0x1EE100C`) and can swallow it.
- Events go through `Canvas_DispatchEvent 0x410570`. Keyboard and mouse events (`0x1EE1001`) go to the handler at
  `+0x60`. Other events go to the listener (`+0x5C`) or to the C callback (`+0x64`).
- Handled messages:
  - Window: `WM_ERASEBKGND`, `WM_CLOSE` (hide, `PostQuitMessage`), `WM_PAINT`, `WM_SIZE`, `WM_MOVE`, `WM_ACTIVATE`,
    `WM_ACTIVATEAPP`, `WM_SETTINGCHANGE`, `WM_GETMINMAXINFO`, `WM_INPUTLANGCHANGE`, `WM_DISPLAYCHANGE`, `WM_SIZING`
    (keeps the aspect ratio), and the end of a size/move.
  - Keyboard: keys `0x100-0x105`.
  - Mouse: `0x200-0x209`, wheel, `WM_MOUSELEAVE`. Button ids: left 1000, middle `0x3E9`, right `0x3EA`.
- Mouse trails, vanish and sonar are saved on `WM_SETTINGCHANGE`, turned off while the mouse is tracked inside the
  window (`0x410290`), and restored on leave (`0x410E10`).
- `0x410350` raises the screen saver timeout; its message (probably `WM_SYSCOMMAND`) is not confirmed.

## 6. The services, each frame

In `ServiceList_Update` order of the vtable table in docs/167 §7.1 (the run order is the list order at run time).

| Service | `+0x1C` | Per frame on the main thread |
|---|---|---|
| MessageServer | `0x588890` | Drains the EA message queue under its lock (`0x44D170`). |
| Canvas | `0x588A00` | Unless `+0x12C` is set, calls the canvas's virtual `+0x8C`; when that returns false, sets `0x11CDBC4` to 1 (`0x588770`). docs/167 and docs/55 call this service the `PeekMessage` pump; whether `+0x8C` is that loop was not reread here. |
| Input | `0x598660` | `InputMan_Update 0x49D040`: polls 4 XInput pads; probes one disconnected pad every 250 ms in turn; `0x49C5F0` diffs the state. |
| JobManager | `0x599A10` | Main-thread job pump under a timeslice stopwatch; `0x599E60` runs jobs or waits on the semaphore. |
| CAS/SimService | `0x5F0E50` | Sim model builds, §6.1. |
| CAS/TextureCompositor | `0x608630` | Sim texture compositing, §6.1. |
| NPMessageServer | `0x60C5A0` | Launcher named pipe: flushes the send queue (`0x60C400`, `0x60C2D0`: `CreateFileW` + `WriteFile`), handles received messages (`0x60BC60`). |
| AnimatedTextureManager | `0x687C80` | Advances animated textures (5 functions). |
| RenderSystem | `0x6E3620` | Lost-device flag, FrameDatabase consume, scene update, §4.1. |
| SwarmSystem | `0x71E640` | Effects, §6.3. No lock. |
| UTFWin | `0x723EE0` | Clipboard: pending text to `CF_UNICODETEXT` (`0x4E93E0`). |
| ResourceChangeMonitor | `0x733D20` | Polls for changed resource keys on a QPC timer. |
| ResourceSystem | `0x7377F0` | `0x737560`: callbacks under a time budget, cache trims (`0x732E90`), timed releases. |
| ScriptThread | `0x76B330` | When the script thread asks for a pause (world vtable `+0x30`): sets game-clock pause flag 1, idles (`App_IdleIfNotForeground`) until the request clears, clears the flag. The main thread stalls here for as long as the script thread holds the request. |
| ShaderSystem | `0x7A08C0` | `ShaderWatcher_Poll`: every 3 s reads the shader package's file time; 1 s after the last change reloads it (`ShaderSystem_LoadPackage(1)`). |
| UI/IME | `0x7E6840` | Every 4th frame: turns the IME off when the focused UI element is not a text field. |
| Audio | `0x868FF0` | Nothing: calls the shared empty body `Nop_RetStub 0xC0FD60`. |
| OnlineInformation | `0x883080` | 600 s entitlement check timer (`0x8925C0`). |
| Camera | `0x96DBD0` | `Camera_UpdateAspectRatio`: config `AspectRatio` 1-4 or the back buffer ratio; posts a sim-thread message on change. |
| VideoManager | `0x9D96D0` | Movie player: walks the videos (LOD fixup, distance, start/stop messages); state at `+0x22C` (1 playing, 2 paused). `VideoManager_HandleMessage` dispatches several message ids. |
| OnlineEventListener | `0xA37E60` | 10, 180 and 30 s timers sending Blaze `getMessages` requests. |
| ObjectDesigner | `0xB3A960` | §6.2. All under lock `0x106B874`. |
| World/Service | `0xC7E3C0` | §6.3. |
| UI/ThumbnailManager | `0xD61670` | §6.2. |

### 6.1 CAS: Sim builds and textures

`SimService_Update 0x5F0E50`:

1. It frees the finished node list (`+0x70`) under a global lock (`0x4E16F0`).
2. On the first frame only, it registers with the service host (`0xAAB640`).
3. Budget: unlimited when byte `+0x139` is 0 (`or esi,-1`, read unsigned), else the unsigned integer at `+0x2C`, on a
   QPC deadline.
4. `CasSim_BuildSortedRequestList 0x5F0820` ages request priorities by class (+0.25 to 1.0) and builds the static
   sort list.
5. The main request list (`+0x58`): when the active slot is idle, it ranks requests by camera distance and view angle
   (`(1.5 - cos) * distance`) and sorts them. Then it runs the head request through a state machine:

   | State | Step |
   |---|---|
   | 0/1 | outfit (`0x5EEFB0`) |
   | 2 | texture LODs (`0x5EC550`) |
   | 3 | skin textures (`0x5EBAE0`) |
   | 4 | skin and blend geometry (`0x5EC6D0`) |
   | 5 | `BuildSkinJob` (`0x5DE400`) |
   | 6 | material definitions (`0x5D2750`) |
   | 7 | LOD build, then model record and overlay textures (`0x5F06B0`) |
   | 8 | done, or `CasSim_DestroyRequest 0x5EE860` |

   A step that is not ready ends the frame's work. The deadline is checked after every step.
6. The scene-object path (`+0x68`, nearest to the camera, `0x5EDB30`) shares states 6 to 8. It adds:
   - 9: part texture cache check (`0x5EE6D0`).
   - 0xA: base skin texture ops (`0x5DC800`).
   - 0xB: compositor request (`CasSim_StepProcessCompositorRequest 0x5EBC50`).
7. `CasService_UpdateBusyState 0x5BDF10` (category 2) posts script message `0x30290F0` when work starts and when all
   three categories are idle.

`TextureCompositorQueue_Update 0x608630`:

1. The material compositor `0x6CE0D0` runs first. It has its own 5-unit stopwatch (`0x6CD380`) and refreshes the
   composited textures (`0x6CDD70`). Then it rebuilds its start, refcount and release arrays.
2. `0x608270` runs the job queue under the budget `+0x18` (unlimited when byte `+0x15` is set). `0x5FCAD0` starts a
   job and `0x5FDEF0` steps one through 8 states: setup, per-region composite, bounce buffer, finish. A job is 0x360
   bytes.
3. It reports busy in category 0.

### 6.2 Thumbnails

`ObjectDesignerService_Update 0xB3A960`, all under lock `0x106B874`:

1. It drops handles whose objects are gone (`0xB39000`).
2. The request at `+0x64` goes to `0xB36DD0`. Its project name is `DestroyObjectRequest`, but the code builds a
   `Lot_MessageData`, so the role is open.
3. It applies a pending object change (`0xB33BF0`, `+0x78`/`+0x7C`).
4. It drains four thumbnail queues: presets `0xB34A50`, requests `0xB373A0`, hair swatches `0xB34750` and large
   patterns `0xB37DA0`. Each builds a TextureBuilder texture.
5. The active render request gets its view transform (`0x6FAF40`) and a scene and light refresh
   (`0x6F5400`). It is released when the renderer's queue is empty.
6. Then key lists (`0xB34CB0`), fabric loads (`0xB36260`) and queued textures (`0xB37750`, id -1 = all).

`ThumbnailManager_Update 0xD61670` runs only when `+0x5B` is set and `+0x5C` is clear:

1. Pregeneration (`0xD60800`) builds key lists from the product vectors.
2. `0xD5CBD0` takes incoming requests under lock `0x1088330`. It builds their key lists (`0xD5F710`, `0xD5F980`) and
   the scene model.
3. `0xD5C6D0` renders the queue: it makes a 512x512 renderer (`0xD4B390`) and renders each request (`0xD59C10`).
   It tears the renderer down (`0xAE6290`) when the queue is empty.
4. It handles resource loads (`0xD547A0`, list `+0x164`), starts one cache-load job each (`0xD548C0`, `+0x170`) and
   polls them (`0xD54B10`, `+0x17C`).
5. When all are idle and `+0x5D` is set, it posts render message `0x2618EB3A` (`0x692F70`) and notifies the listener.

### 6.3 World and effects

`WorldService_Update 0xC7E3C0`, only when the world object `0x11ECBC4` is ready:

1. `WorldManager_Update 0xC6D570`:
   - **World clock.** When not paused (`+0x1B4`), `+0x124 += rate * dt`, wrapping at 86,400.
   - **Terrain** (`+0x58`). It loads the height map when the buffers are empty (`0xC2A290`), then runs
     `WorldTerrain_Update 0xC845C0` to a stopwatch deadline.
   - **Lots.** `WorldManager_ScoreAndStreamLots 0xC6C290`: the camera-driven lot streaming of docs/28 §5.
   - **Roads, water and sky.** Road chunks `0xB890C0`. Water (`+0x60`): `0xC0B550` and `0xC0DD60`. Ponds `0xB5E070`.
     Weather `0xD6E350` (`+0x74`). Sky `0xC15D80` (`+0x64`). Layer scroll `0xA50F40` (`+0x68`).
   - **INI hot reload, last.** It dispatches on the pending id `+0x98`, cleared atomically:

     | Id | Reloads |
     |---|---|
     | 1 | Sea |
     | 2 | world lighting |
     | 3 | Pond |
     | 4 | SkyCommon |
     | 5 | anim callback |
     | 6 | CustomLightRigging |
     | 7/8 | CASCameras |
     | 9 | AO, then posts `0x486519D` |
     | 10 | ThumbnailCameras |
     | 11 | CameraFade |
     | 12 | ThumbnailSizes |
     | 13 | LotSettings |
     | 14 | reset |
     | 15 | FutureOutcomes |

     The handlers for 2, 5 and 14 are inferred.
2. When `0x11ECE58` is set: `LotRenderer_UpdateAllLots 0xC7CEA0` calls `0xAEB2E0` per lot. That picks imposter or
   full draw, fades the fog of exploration and sets the tile boundary rects.
3. Debug draws:
   - `0x83ABC0` flushes the locked debug list. It is the only blocking `EnterCriticalSection` here and runs only with a
     debug flag on.
   - `RoutingDebug_FrameUpdate 0x818EA0` draws the routing overlays. It uses `TryEnterCriticalSection`, and posts
     script messages `0x5273C29` / `0x5273C32` at most every 0.1 s.
   - `0x740100` flushes the line lists, with `TryEnterCriticalSection` (it skips the flush when busy).
4. `World_UpdateSpeedTreeWind 0x712F00`:
   - camera (`0x712D40`);
   - the wind clock, which wraps at 50,000 (`CSpeedTreeRT_SetTime 0x7BA0C0`), with a matrix reset on wrap (`0x7C2750`);
   - gusts (`0x7C2FA0`, `rand`);
   - the sway globals `0x11D28B0` / `0x11D28B4`.

`0xD6E350` has the heap-tag name `ArgScript_ScreenFilterDepthBlend`. It is named `Weather_UpdateFrame` here
[inferred]. It does snow floors, SpeedTree settings, frosted-glass LOD and the lookup-texture locks under a critical
section.

`SwarmSystem_Update 0x71E640` takes no lock. When enabled (`+0x18`, `+0x2C`), it does the following:

1. It stores the cursor position into the render state (`+0x38B8`).
2. It copies the camera and view block (`0x6E9AC0`).
3. It calls the effect manager (vtable `+0x48` of `+0x1C`, with `0x11CDDAC` and dt), then vtable `+0x1C` of `+0x48`.

The per-effect work, `SwarmManager_UpdateFrame 0x9160B0`, is reached only through that virtual call. It runs a timer,
clears the stats and updates each effect group (`0x91A600`, `0x91BA10`, and `SwarmManager_UpdateEffectGroup 0x91BAB0`:
settings copy, re-apply on changed quality bits, then per effect a camera-distance LOD band and the effect's own
update, with no frustum test). The first pass named `0x91BAB0` `ApplyQualityChanges` after only its first part.

### 6.4 Found by sampling: the runtime libraries

The second pass read 1,131 of the 1,132 functions that only the sampled stacks reach (1,122 not read before). 1,011 of
the 1,132 are listed under
VideoManager, but almost none is video code. A sampled edge can skip frames, and code that script calls reach is listed
under the first root whose stacks pass through it. What they are:

| Range or group | What it is |
|---|---|
| `0xE64BD0`-`0xEBBDE0` and around | The embedded Mono runtime: assembly, image, class and method loaders, metadata tables, custom attributes, reflection, the marshalling IL emitters for wrapper methods, mempool. |
| `0xE48E80`-`0xE513D0`, `0xD70900` | The Boehm GC: heap sections, mark state machine, sweep and reclaim, typed malloc, thread registry, stop and start world. |
| `0xE450C0`-`0xE48AB0` | A glib subset: hash table, list, string, path, UTF-8 to UTF-16, an XML parser, log handlers. |
| `0xD9D2D0`-`0xE3A530` | EA's binder native bodies (docs/220, `docs/data/binder-bodies.csv`): managed methods (mscorlib, ScriptCore persistence) with a native body that the interpreter enters through `RuntimeMethod+0x24`. Not Mono AOT: the exe has no AOT code. The 43 on the main thread are named `Body_<Class>_<Method>` from that CSV. |
| mostly `0x44DB50`-`0x465B50` | EAText layout and shaping: bidi, line breaking, Thai, Hebrew and Arabic clusters, OpenType GSUB. |
| smaller groups | zlib and libpng write code, eastl vector helpers, `Lot` wall and placement tests, the thumbnail scene objects and renderer (`ThumbSceneObj_*`, `ThumbRenderer_*`), and a speech-style audio decoder at `0xF09C50`-`0xF14080` (12-tap LPC, 0x1B0-sample frames; codec not identified). |

Bug candidates from this pass. None is fixed or tested:

- `GC_write 0xE4DD50`: once the log file failed to open (handle -1), a later call returns -1 without leaving the
  critical section `0x1222580`, which `GC_stop_world` also takes. Not reachable in a normal run: a failed open goes
  to the abort sink `0xD707E0` (docs/118 D2) first, and GC warnings never get here, because `mono_gc_base_init`
  sets the warn proc to `mono_gc_warning 0xE69550` at `0xE695AC` (docs/120), which logs through `mono_trace`. What
  is left on `GC_write` is `GC_PRINT_STATS` output and paths that abort anyway.
- `ThumbSceneObjMap_InsertUnique 0x7007B0`: after a null-tested allocation, two stores still run, so a failed
  `EA_AllocTagged` would fault. Only under address-space exhaustion. (The first read also flagged
  `ThumbSceneObj_FindAndDestroy 0x7033A0`; a second read found the node is the link inside a live object and
  cannot be null there.)
- The rest are stock Mono or Boehm patterns: a fatal-error print that returns into a full table (`0xE49B10`,
  `0xE51070`), an assert that falls through (`0xE8A520`, `0xE9BE70`), allocations used without a null check.

## 7. Project names changed

Four project-CSV names did not match the code read here. The new CSV uses the new names and keeps the old one in its
comment. `ts3w-1.67.symbols.csv` is not changed.

| Address | Project CSV name | New name | Why |
|---|---|---|---|
| `0x4F30E0` | `Timer_Get` | `Stopwatch_SetClockMode` | Stores the clock mode at `+0x10` and a scale at `+0x14` picked per mode (`ret 4`); a setter, not a read. |
| `0x5EBC50` | `SimService_Process` | `CasSim_StepProcessCompositorRequest` | State 0xB of the scene-object path in `SimService_Update`. The OOM crash site `0x5EBE31` (docs/04, 05) is inside it. |
| `0x5F0820` | `SimService_ReleaseRequestRefs` | `CasSim_BuildSortedRequestList` | First step each frame: clears a static list (guarded init, `atexit`), walks the request hash table, ages priorities, builds the sort list. |
| `0xC0FD60` | `IC_Animation_SetMissingAnimationCallback` | `Nop_RetStub` | One `ret`. Several script icalls bind to it in the binding table (the project name is one of them), and 29 native sites call it (Audio's update is one). |

Docs-only names written as `svc.X` or `A::B` are renamed to `XService_Update` and `A_B`.

The second pass kept the project name at all 112 project-named addresses it read. At 49 the reader proposed another
name, kept in the CSV comment as `Suggested name:`. Most only restyle a Mono or Boehm name (`mono_class_init` as
`Mono_ClassInitLocked`). These would change the meaning and need a second look before the project CSV takes them:

| Address | Project CSV name | Suggested | Why |
|---|---|---|---|
| `0xD78760` | `ScriptHost_LoadImage` | `ScriptHost_IsImageLoaded` | Calls a ResourceMan slot and returns 0 either way. |
| `0xE49B10` | `GC_heap_section_limit_a` | `GC_AddHeapSection` | Appends to the 768-entry heap section tables. |
| `0xE4EEF0` | `GC_heap_section_limit_b` | `GC_win32_get_mem_recorded` | Gets memory and records the section. |
| `0xE4B8D0` | `GC_hot_2` | `GC_next_used_block` | Walks to the next block in use. |
| `0xE4D270` | `GC_per_frame_path_a` | `GC_mark_some` | One step of the mark state machine (states 0-5), 1 when marking is done. |
| `0xE4D460` | `GC_per_frame_path_b` | `GC_mark_some_protected` | The same step under an exception guard. |
| `0xEBBDE0` | `g_hash_table_lookup` | `MonoGLib_HashTableLookupExtended` | Returns 1 with key and value out pointers, like `g_hash_table_lookup_extended`. |

## 8. Coverage

| Root | Functions | Read from the decompile |
|---|---:|---:|
| World/Service | 3,094 | 63 |
| VideoManager (mostly runtime libraries, §6.4) | 1,279 | 1,052 |
| CAS/SimService | 1,098 | 142 |
| `MainLoop` | 984 | 277 |
| `App_Run` | 974 | 15 |
| UI/ThumbnailManager | 933 | 92 |
| ObjectDesigner | 576 | 86 |
| SwarmSystem | 349 | 19 |
| ShaderSystem | 138 | 5 |
| RenderSystem | 112 | 55 |
| CAS/TextureCompositor | 92 | 15 |
| `Canvas_WndProc` | 34 | 32 |
| 15 other services | 157 | 82 |
| **all** | **9,820** | **1,935** |

Deeper functions carry a summary built from the graph: callees, strings, imports and virtual call slots.

## 9. Open questions and limits

- The role of `0x11D1530` (the gate for `Frame_RenderAndPresent`) and of the frame-job flags `Renderer+0x28E`,
  `+0x28F`, `+0x294` and `+0x29C` is inferred.
- The second pass cut each body at 60 lines, so 237 of the 1,132 were read in part. Rows built mostly from callees are
  marked inferred. The bug search did not cover the cut parts.
- Several large functions were read only in part (the extract stops at 70 lines; the agents read the full body for
  `MainLoop`, `SimService_Update` and `WorldManager_Update`): `App_StartupFlags`, `VideoCapture_RenderFrame`,
  `0x9160B0`, `0x91BAB0`, `0xB2D650`, `0xD4CC00`. Their behavior lines say so where it matters.
- `MainLoop__h5..h7` are listed under a 156-byte stopwatch read because of sampled edges that skip frames. They are not
  frame-loop steps.
- **The stack sampler is not safe to rerun as is.** In al262, the BP run hung: a main-thread sample got stuck, and the
  next one said "busy: previous stack sample still running". In IP the game died 8 s after sampling started
  (`0xC0000005`), and the queue stopped before SV. No compiler or test switch was loaded, so the sampler is the likely
  cause. The 4,234 samples used here are from before those stops.
