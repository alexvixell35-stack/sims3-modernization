# Reverse-engineering findings: stutter

Written 2026-10-11 against the Steam build of TS3W.exe (SHA-256 `10b2423e…0609`, PE timestamp 2014-01-21, LAA set, no ASLR, image base `0x400000`).

The focus is **in-game stutter**, not load times. See `03-prior-art-re.md` for community work and `04a-script-layer-stutter.md` for the C# side.

Confidence tags: **[verified]** means read in the decompiled code or the bytes. **[inferred]** means reasoned from code but not yet confirmed by measurement. **[to measure]** means it needs a run of the game.

---

## How this was done (reproducible)

| Step | Tool / script | Output (all under the git-ignored `game-copy/`) |
|---|---|---|
| Copy game files | `cp` from `D:\SteamLibrary\…\Game\Bin` | `bin/`, `packages/` |
| Extract script DLLs from `.package` files (DBPF, RefPack, S3SA decryption) | `node tools/extract-assemblies.ts` | `assemblies/` (10 DLLs) |
| Decompile C# | `ilspycmd -p` | `decompiled/` (11,071 files) |
| Ghidra import + analysis (16 GB heap; X86Analyzer and StackVariableAnalyzer still hit `OutOfMemoryError`, so some functions are missing) | `analyzeHeadless` | `ghidra/TS3.gpr` |
| Name the 1,757 native functions bound to script `DllImport`s | `node tools/internal-calls-map.ts` → `analysis/internal-calls-map.csv`, then `tools/ghidra/ApplyInternalCallNames.java` | functions named `IC_<Class>_<Method>` |
| Decompile, string xrefs, call paths | `tools/ghidra/run-headless.sh DecompileTo.java / StringXrefs.java / CallPaths.java …` | `analysis/decomp/*.c`, `analysis/*.txt` |

**How script calls reach the engine.** Script methods are `[DllImport("Sims3Common.dll")]` stubs; no such DLL ships. TS3W.exe resolves them by name through `.data` tables of `{void* fn, char* name}` pairs, with each run ended by a null pair (for example `"ScriptCore.Route::Route_Plan"` → `0x765160`). Proof: in 41 of 42 runs a code pointer sits before the first name, and no run has one after its last name. [verified]

> **Correction (2026-10-11):** the first version of this document read the pairs as `{name, fn}`, which gave every name the *next* entry's function (spotted by the harness research in `06`). The names and addresses below are corrected, and F1's conclusion was re-checked under the correct names: it still holds.

---

## F1. Route planning is synchronous on the sim thread, under a global lock [verified chain, inferred thread]

Call chain from script to the A* search, read in Ghidra:

```
IC_Route_Plan 0x765160 / IC_Route_Replan 0x765490            -> 0x7641d0
IC_Route_PlanFromPoint 0x765240 / IC_Route_ReplanFromPosition 0x765550 -> 0x7642c0 ("Routing/RoutingHost/startPoints")
  -> 0x7606e0
  -> 0x82c6e0: lock(0x1021cf0) ; setup 0x819d50 ; SEARCH 0x81b2a0 ; 0x819ae0 ; read result ; unlock
                (lock = 0x4e16f0: EnterCriticalSection / WaitForSingleObject wrapper; unlock = 0x4e17b0)
  -> 0x81b2a0 path search (uses "Routing/RoutingJob/startPts|goalPts", "Routing/RoutingPathImp")
```

- No `JobManager` / `JobThread%i` call appears in this chain. The planner runs to completion inside the script call and returns the result. [verified]
- Scripts are single-threaded cooperative tasks (see 04a), so every `Route.Plan` stalls **all** script simulation until the search ends. [inferred]
- `IC_World_FindPlaceOnRoadOffScreen` reaches the same search. `Route_IsPointRoutable` (`0x7538c0`) does not. [verified]
- Open question: which other threads take lock `0x1021cf0` (contention)? To check with `StringXrefs`/xrefs on the lock.

**Why this is the stuck-sim stutter.** Several script paths retry routing without real backoff (04a): NPCs that fail autonomy re-queue immediately (`Sim.CanRunAutonomyImmediately` returns `!IsSelectable` after a failure, `Sim.cs:56248`) [verified]. `GoHome` makes up to 3 world-scale plans per attempt. A failed route target is blocked for only one sim minute. Waiting sims replan every 2–3 cycles for up to 50 sim minutes. On a world with bad routing data, failed searches are also the most expensive ones, because A* explores the whole reachable area before giving up. [inferred]

## F2. The simulation loop: GC runs every frame, with an adaptive ~1 ms budget [verified]

`MonoScriptHost::Simulate` is at `0xD81840` (Ghidra's analysis missed it; it is created on demand). Each loop iteration:

1. Runs the script tick (`0xD80BC0`).
2. Calls `GC_try_to_collect(stop_fn = 0xD717B0)` (`0xE4A050`, call site `0xD819AA`). The stop function aborts the collection when the elapsed timer exceeds `DAT_011922a4`.
3. Adjusts the budget `DAT_011922a4` by ±100 per frame, within the range `[DAT_011923a4 = 500, DAT_01192324 = 1500]`, starting at 1000. The units are probably µs.
4. Runs `mono_gc_invoke_finalizers` (`0xE697F0`). If 50 or more finalizers are pending (`DAT_01194848`) for 200 frames in a row, it **drains the whole queue in one frame**.
5. Catch-up: when the sim falls behind, it runs up to **10 sim steps in one frame**. A backlog above 19 is reset (`param_1+0xc14`).

**Why it matters [inferred, to measure].** Boehm's `GC_try_to_collect` is not incremental here. An aborted collection throws its mark work away, so a 1.5 ms cap wastes up to 1.5 ms per frame without ever finishing on a big heap. The heap then grows until an allocation forces a full, non-abortable, stop-the-world collection. On a heavily modded save with a large heap, that shows up as a periodic big hitch. The finalizer drain and the 10-steps catch-up are two more sources of single-frame spikes.

## F3. Boehm can log its own pauses [verified strings, to measure]

The exe contains `GC_PRINT_STATS`, `gc.log`, `GC_INITIAL_HEAP_SIZE`, `GC_MAXIMUM_HEAP_SIZE`, `GC_ENABLE_INCREMENTAL` and `GC_PAUSE_TIME_TARGET`. With `GC_PRINT_STATS=1` set for TS3W.exe, Boehm should write every collection and its duration to `gc.log` (likely in `Game\Bin`). This is the cheapest first measurement: it needs no patch.

## F4. The engine's own budget and profiling hooks [verified names, meaning inferred]

- `Sims3.ini` `[Timeslices]`: `SimService`, `JobManager`, `ResourceSystem` and `TextureCompositor` all = `10,10,25,50`. The `JobManager` and `SimService` code references these names (`0x5992d0`, `0x5ebc50`).
- Script-visible profiler hooks exist: `ProfilerUtils_BeginEvent/EndEvent/Marker/Counter`, `Simulator_EnableYieldAnalysis`, and `StopWatch_*`. Whether release builds strip these is still to check. If they are live, they are free instrumentation points.
- `GameUtils_IsSingleThreadedMode` (`0xEC1500`) compares two thread-related values; its meaning is not decoded yet. `Sims3.ini` has `[Script] Multithreaded=1`, which suggests the sim already runs on its own thread, separate from rendering. [inferred]

---

## Big wins, ranked (for stutter)

Rank = expected stutter gain × confidence ÷ effort. "Not done by" lists S3SS, the Performance Patch and NRaas.

| # | Win | Mechanism | Not done by | How | Effort | Confidence |
|---|---|---|---|---|---|---|
| 1 | **Route-failure storm control** | Stop repeated synchronous A* searches that are bound to fail (F1) | S3SS (none), NRaas (only resets after >10 fails / 30 min) | Native hook on `0x82c6e0` or the `IC_Route_*` entries: a negative cache keyed by (router object, goal set, world routing epoch) that returns "fail" at once for N sim-minutes. Invalidate on build/buy, `RouteManager_MarkRoomForUpdate`, portal or footprint changes. No core-mod conflicts with NRaas. | M | High that it removes repeat cost; size of the gain is [to measure] |
| 2 | **GC pause shape** | Avoid aborted-mark churn and the later forced full collection (F2) | S3SS Chunky (skips the GC entirely, so memory grows), Finalizer Throttle | First measure with `gc.log` (F3). Then try, one at a time: `GC_INITIAL_HEAP_SIZE` (fewer collections), a different budget window (`0x11923a4`/`0x1192324`), and collecting only when the heap grew by X since the last full collection. | S to M | Medium |
| 3 | **Per-search time budget and yield** | Bound the worst case of one A* search | none | Hook the search: past a node or time budget, return "fail, retry later" (the script already handles route failure). Combine with #1. | M | Medium |
| 4 | **Spike smoothing in the loop** | Finalizer drain-all after 200 frames; up to 10 catch-up sim steps per frame (F2) | S3SS Finalizer Throttle (partly) | Spread the finalizer drain over several frames; cap catch-up at 2–3 steps. Byte patches in `0xD81840`. | S | Medium [to measure] |
| 5 | **Script-side retry backoff** | NPC "can't go home" and autonomy immediate-retry loops (04a) | NRaas Overwatch (reset only) | A script core mod changes `Sims3GameplaySystems`, which conflicts with other core mods. Prefer #1 (native) and keep this as plan B. | M | High on the mechanism |
| 6 | **Bad-routing worlds** | World routing data (`0x05CD4BB3`), terrain paint, intersections | ellacharmed's per-world fixes (new games only) | Data fixes per world; a tool to detect unroutable areas from repeated failures logged by #1. | L | Community |
| 7 | **Rendering stutter** | Shader compile hitches, D3D9 driver overhead | DXVK | DXVK (baseline layer 3), with its state cache warmed. Already planned. | S | High |

## Measurements that will decide the order

1. **`gc.log`**: set the environment variable `GC_PRINT_STATS=1` for one session. Count collections per minute and the longest pause, on the heavy save.
2. **Route profiler (Frida)**: time every call to `0x82c6e0` and count successes and failures per sim. Write a per-second histogram. Correlate with frame-time spikes.
3. **Frame times**: PresentMon or CapFrameX for 5 minutes at speed 3 on the heavy save. Count frames over 50 ms. This is what our patches must reduce.
4. **Tick-loop timer (Frida)**: time the script tick, `GC_try_to_collect`, the finalizers and the number of catch-up steps per frame inside `0xD81840`.

## Open questions

- Who else takes the routing lock `0x1021cf0`? Do the car/steering jobs on `JobThread`s block the sim thread through it?
- Are the `ProfilerUtils_*` and `Simulator_DoCPPProfile` hooks live in the release build?
- Is `GC_ENABLE_INCREMENTAL` usable with this Mono 1.2.3.1 build on Windows (write-watch dirty bits), or does it crash?
- What is the units/timer mode of `FUN_004f30e0(2)` (µs vs ticks)? This sets the real GC budget.
- Do the remaining Ghidra `OutOfMemoryError`s hide important functions? Rerun with `GHIDRA_HEADLESS_MAXMEM=24G`, or analyse in the GUI.

## F5. Measured: where sim-loop time goes on Bridgeport [measured 2026-10-12]

`tools/frida/stutter-profile.js`, 10 min on `Baseline-BP` with all fixes, speed 3, autopilot on (raw data `probe-out/profile/bp-600-*`, not committed):

- 31 loop iterations/s; **4.0 hitches/s** (iterations of 50 ms or more), 2,354 in total, worst 1,014 ms.
- **Route search: 95 ms per second** (26.5 plans/s, 15,456 plans, single searches up to 188 ms). Only **1.6% fail**, but failures cluster (47–58 per minute in minutes 1, 4 and 7, which also had the highest route time).
- GC: mark 16.8 ms/s plus forced collections 15.9 ms/s (176 forced, up to 149 ms); finalizers 1.8 ms/s.
- Dominant cost per hitch: route 270, GC 89, **unaccounted 1,995**. The profiler's script hook (`0xD80BC0`) sees only 12 ms/s, less than the route time that runs inside scripts, so it does not cover most script execution; the unaccounted part is script and native work inside the iteration that no hook times.

Consequences: (1) a route-*failure* cache alone targets ~2% of plans; the cost is mostly successful searches, so the routing fix should move searches off the sim thread or make fewer of them; (2) the largest bucket is still unidentified: next, sample the sim thread's stack (`/simstack`) during hitches to rank the unaccounted time.

## F6. Scripts run in the Mono *interpreter*; it is the largest sim-thread cost [verified 2026-10-12]

- TS3W.exe embeds only Mono's interpreter ("mint"): strings `..\..\mono\mono\interpreter\interp.c`, `mintops.c`, `transform.c` ("transform.c: Unimplemented opcode ..."), `MINT_JMP ...`; no `mini`/JIT strings at all. All C# game and mod code is interpreted.
- `FUN_00e53eb0` (4,400 lines decompiled, `game-copy/analysis/decomp/gc-hot.c`, references `interp.c`) is the interpreter's main loop (`ves_exec_method_with_context`). Called via `0xe59fd0` and `0xd7f000`.
- Sampling the sim thread's top frame through `/simstack` (676 samples over 3 min, Bridgeport, x3; `probe-out/profile/simstack-bp.jsonl`): **29.7% in `FUN_00e53eb0`**, 6.1% in `FUN_00e4c3e0` (GC area, called from the per-frame GC path `0xe4d270`/`0xe4d460`), 5.0% in `FUN_004e5e40`, 7.5% in ntdll (waits/locks), the rest spread thin. The interpreter's callees (internal calls, allocation) come on top of the 29.7%.
- This explains F5's "unaccounted" hitch time: it is mostly interpreted script work that the profiler's hooks do not time.

Consequences: the biggest stutter lever is script execution cost. Options, roughly by effort: (1) find the hot C# methods (managed sampler: read the interpreter's `ThreadContext.current_frame` chain on the sim thread and name each frame's method) and slim those down through the patch host or move them to native internal calls; (2) cut how often the hot paths run (autonomy, routing callbacks); (3) speed up the interpreter itself (dispatch, hot opcode paths) or compile hot methods natively, a large project. A full JIT swap (newer Mono) is not realistic for a 32-bit exe with EA's embedding.

### F6a. Toward a managed sampler (partial, 2026-10-12)

- Verified: the interpreter's ThreadContext is in the sim thread's TLS slot whose index is at `[0x11f4160]` (96 on this run); `ThreadContext[2]` is the current call frame and `frame[0]` its parent (decompile of `0xe53eb0`, where `param_2[2] = frame` and `frame[0] = previous`). Probe endpoint `/interp` reads the chain with the thread suspended.
- Verified: `[0x11f4170]` holds the current script *task* (saved and restored around each interpreter run); its `+0` is the task's base `MonoMethod` (e.g. `Sims3.Gameplay.ActorSystems.SimBuilderTask.Simulate`, `ScriptCore.ScriptProxy.Simulate`), `+0x140` its call depth.
- MonoMethod layout here: klass `+8`, name `+0x18`; MonoClass name/namespace `+0x34`/`+0x38` [verified live]. The interpreter fetches a callee's runtime info from `MonoMethod+0x10` (decompile line ~228) [inferred].
- Not yet working: mapping a frame's `frame[1]` to its MonoMethod (`/rmname` scans memory for `MonoMethod+0x10 == frame[1]` and found none, ~35 s per scan). Either `frame[1]` is not that pointer or the layout differs. Next: decompile the call opcode path (where `frame[1]` is assigned from the callee) or hook the method-entry point to count entries per MonoMethod.
- Task-level sample (885 samples, 3 min, Bridgeport x3; reads of `[0x11f4170]` while the thread runs, so roughly 5% unresolved): **67% `ScriptCore.ScriptProxy.Simulate`** (the wrapper that runs every game object's and Sim's script task), 2.1% `SimBuilderTask`, 1.0% `StoryProgressionTask`, 0.9% `ScriptProxy.CreateLogic`, ~20% no script task running. Splitting the 67% by `ScriptProxy.mTarget` failed with unsuspended reads (the proxy pointer races); it needs the reads done inside the suspended window.

## F7. Managed (C#) profile of the sim thread [measured 2026-10-12]

Tool: probe `/managedstack` (interpreter frame chain read with the thread suspended: `frame[1]` = interpreter info, `info+8` = MonoMethod, back pointer at `MonoMethod+0x14`; script thread = the one whose TLS slot `[0x11f4160]` is set) and `tools/probe/managed-sample.mjs` (5 Hz). 885 samples over 3 min, `Baseline-BP`, all fixes, x3; 18.5% of samples had no script running. Raw: `probe-out/profile/managed-bp.*`.

Inclusive (anywhere on the C# stack): `ScriptProxy.Simulate` 66.8%; `Sim.Simulate`/`Sim.DoInteraction` 34.8% (`InteractionQueue.ProcessOneInteraction` 33.2%); **routing `SimRoutingComponent.DoRoute` 16.6%** (`Route.Plan` 11.5%, native `Route_Plan` 9.9% self); `Lot.Simulate` 12.9% → `Lot.UpdateReactions` 8.9% → **`ReactionBroadcaster.PulseSims` 7.1%, `GetInRadiusSims` 4.2%**; `SimUpdate.Simulate` 10.2%; **`GoHome.Run` 7.2%** and `GoToVirtualHomeInternal.Run` 4.4%; **walk-style rules** `SimWalkStyleRules.GetAdjustedWalkStyleForRoute` 6.0%, `PostProcessWalkStyle` 5.4%, `WalkStyleComparer.Compare` 4.7%, `SortedList`2.Find` 4.9%; `Queries.GetObjects` 6.0%; `EventTracker.SendEvent` 4.6%; `Autonomy.Update` 5.4%.

Self (innermost): native `Route_Plan` 9.9%, **`System.Enum.Equals` 5.1%** (boxed enum compare through `DefaultComparer`, very slow interpreted), `Query_GetObjectsInRange` 2.6%, `Route_ReplanFromPosition` 2.0%, `Object.GetType` 1.9%, `Query_GetObjectsOnLot` 1.8%.

Fix candidates, by expected gain per effort [inferred, to verify one at a time]:
1. Walk-style rules: a `SortedList<WalkStyle,…>`/`WalkStyleComparer` lookup per route that ends in `Enum.Equals` via the default comparer. An int-based comparer or a cached result per Sim and route type could remove most of ~6%.
2. NPC go-home loops (7–11%): per-Sim backoff after failures, or send NPCs home off-screen without world-scale planning (docs/04a).
3. Reaction broadcasters (7%): `PulseSims`/`GetInRadiusSims` scan Sims around each broadcaster on every lot update; throttle the pulse rate or cache the Sim list per lot per tick.
4. Route search itself (~10% self): fewer plans (1, 2) first, then searches off the sim thread.

### F7b. Same profile across worlds [measured 2026-10-12]

`tools/harness/profile-world.ts --tag <world>` runs the same 10 min for every world (x3 with autopilot, keep_alive and declines every 30 s, the native stutter profiler throughout, the managed sampler in minutes 1–4, the native sampler in minutes 5–8); `tools/probe/profile-compare.mjs bp sv …` prints the table. Sampling does not inflate hitches: on Sunset Valley the rate was 1.5/s before sampling, 1.7/s and 2.0/s during the two samplers, 1.8/s after.

| | Bridgeport (`Baseline-BP`) | Sunset Valley (`Baseline-SV`) | Isla Paradiso (`Baseline-IP`) |
|---|---|---|---|
| iterations/s | 31.2 | 63.3 | 31.7 |
| hitches/s (≥50 ms) / worst | 4.0 / 1,014 ms | 1.9 / 2,210 ms | 3.5 / 1,170 ms |
| route ms/s, plans/s, fail % | 94.9, 26.5, 1.6% | 82.2, 10.1, **7.0%** | **246.4**, 9.7, **12.0%** |
| ms per plan | 3.6 | **8.1** | **25.4** |
| GC mark / forced ms/s, forced max | 16.8 / 15.9, 149 ms | 11.2 / 6.6, 72 ms | 14.1 / 13.2, 162 ms |
| interpreter top frame (native samples) | 29.7% | 28.5% | 23.0% |
| no script running (managed samples) | 18.5% | 31.6% | 18.2% |
| `Sim.Simulate` (incl.) | 34.8% | 26.2% | 43.5% |
| `RoutingComponent.DoRoute` / native `Route_Plan` self | 16.5% / 9.9% | 7.3% / **11.5%** | 21.4% / **13.3%** (+ `Route_ReplanFromPosition` **10.9%**, `Route_PlanFromPoint` 2.8%) |
| walk-style rules / `Enum.Equals` self | 6.0% / 5.1% | 2.4% / 2.5% | 2.7% / 2.3% |
| `Lot.UpdateReactions` / `PulseSims` | 8.9% / 7.1% | 1.9% / 1.4% | 6.6% / 5.7% |
| `GoHome.Run` / `GoToVirtualHomeInternal` | 7.2% / 4.4% | 5.2% / 1.2% | **11.1%** / 1.1% |
| `Queries.GetObjects` / `EventTracker.SendEvent` | 6.0% / 4.6% | 1.1% / 0.8% | 3.1% / 0.6% |

Reading [measured, interpretation inferred]:
- Sunset Valley is lighter overall (twice the iterations, half the hitches) but its route searches are slower and fail more: fewer plans, each 2.2× longer, 7% failing. Failed searches are the expensive ones: one iteration with 4 failed `Route_Plan` calls took 1,010 ms. On this world a **route-failure cache or per-Sim backoff** pays more than on Bridgeport.
- Bridgeport's extra cost is population-driven script work (reaction broadcasters, object queries, events, walk-style rules), consistent with a dense downtown.
- Common to both: native `Route_Plan` is the largest single self cost (10–12%); NPC go-home is 6–12%; the largest hitches (0.5–2.2 s) carry little route or GC time, so they are interpreted script work no hook times (F5).

Isla Paradiso (`Baseline-IP`: new save from `IslaParadiso.world`, Dregg household on the "Atlantis" houseboat, aged 14 sim days at x3 in 36 min, no crash) [measured 2026-10-12]:
- **Routing dominates: 246 ms of route search per second (2.6× Bridgeport), 25 ms per plan, 12% failing.** Native route calls are ~29% of all script samples (`Route_Plan` 13.3%, `Route_ReplanFromPosition` 10.9%, `Route_PlanFromPoint` 2.8%, `Route_Replan` 1.7%). In hitches alone: 1,555 `Route_Plan` (21% failed, 57 s), 613 `ReplanFromPosition` (50 s), 273 `PlanFromPoint` (22 s) over 570 s.
- Who routes (task frame above the route call, % of samples): `GoHome.Run` 8.0%, rabbit-hole trips 6.4%, `VisitCommunityLot`/`VisitLot`/`GoToCommunityLot`/`GoToLot` 7.2%, `StandAndWaitController` 1.5% (blocked Sims waiting and replanning), group walks 2.4%. `GoHome.Run` inclusive is 11.1%, the highest of the three worlds.
- **NPC houseboats move on their own:** 3 of the 6 occupied houseboats (The Romantic, The Angler, Scott) changed position in 10 min (`houseboats` agent command, start/end snapshot). Scott is the household players name as the worst lot.
- Reading [inferred]: cross-water trips make every plan long and failure-prone, and Isla Paradiso sends more Sims on them (go-home, lot visits). The off-screen go-home (PLAN fix b) and a route-failure backoff should pay most here; houseboat placement off-screen (fix b2) is now worth building, after measuring how much of the replan time is boat-related.

## F8. Patch host: replacing C# methods at runtime [verified 2026-10-12]

Mechanism (the idea MonoPatcher's `ReplaceMethod` uses, reimplemented in `mods/ts3agent/src/plugin/Handlers/PatchHandlers.cs`): copy the replacement's 40-byte `MonoMethod` header over the original's, keeping the name pointer at +0x18. Measured with `patch_test`, `patch_test_cold` (agent commands):
- A swap reaches only callers the interpreter compiles **after** it (and reflection). Callers already compiled, direct or virtual, keep the old code: EA's interpreter (custom, not stock mint; `FUN_00e53eb0`) binds call targets when it compiles a caller. So patches are applied **once at startup** (tuning time, from the plugin's first load, before any world), never on a hot reload; each patch checks that the target's interpreter info (`MonoMethod+0x14`) is still 0.
- Calling the original: an instance stub on the hook class receives the original's header first (`SwapMethod(stub, target)`), then the target receives the replacement's; the replacement calls the stub. Verified with a virtual method (41 → 141). A **static** stub breaks (callers compiled before the swap pass arguments in the wrong layout: null `this`), and only the replacement may call the stub.
- Live test: `GoHome.Run` wrapped by `GoHomeHook.RunHook` (counts, then calls the original). In `Baseline-IP` at x3: 42 runs in ~2.5 min, no script errors; sampled stacks show `GoHomeHook.Run` → `GoHome.RunOrig` → `Lot.PlanRouteIntoLot`/`DoRouteWithFollowers` → `Route_Plan`, so the original body and routing run unchanged. Note: patched methods appear in samples under the swapped names (`GoHomeHook.Run`, `<Target>.RunOrig`).

## F9. Off-screen go-home shortcut, measured [2026-10-12]

`GoHomeHook` (docs/04 F8 patch host; PLAN fix b): unseen townies are placed outdoors on their home lot on a spot that passes `Sim.IsPointInLotSafelyRoutable`, then `GoHome.Run`'s own lead-in and success bookkeeping run. `Baseline-IP`, 10 min x3, `profile-world.ts` (`ip` = no patch, `ip-on` = shortcut on):

| | ip | ip-on |
|---|---|---|
| iterations/s | 31.7 | **38.1** (+20%) |
| hitches/s (≥50 ms) | 3.49 | **2.63** (−25%) |
| route ms/s | 246 | 254 |
| `GoHome.Run` (incl.) | 11.1% | **5.6%** |
| `Route_Plan` self | 13.3% | **8.9%** |
| `Route_PlanFromPoint` self | 2.8% | 6.4% (the safe-spot check) |
| GC mark / forced ms/s | 14.1 / 13.2 | 9.8 / 8.1 |
| stuck Sims | – | **0** |

329 of 404 go-home runs were shortcut. Route time per second did not fall: townies home sooner head out sooner, and outbound trips still route (fix b3). The first build placed Sims on the sidewalk (`Service.GetPositionInRandomLot` returns a footpath point near the lot), so the game kept sending them home: 5× the go-home rate until the spot had to be on the lot. Shortcut townies were seen continuing normally (work shifts, meet-ups, visits, eating out).

## F10. Trip shortcuts verified per type [2026-10-12]

`tools/harness/verify-shortcut.ts` (x3, up to 5 min, stuck-Sim check; Rodney's rule: each type must be seen working, no A/B). Shared rules: the Sim and its destination are unseen (off screen, beyond max(80 m, 2× camera height) from the camera and 80 m from the active household), the Sim is a townie on its own and standing; the spot is on the destination lot and passes `Sim.IsPointInLotSafelyRoutable`, its verdict cached per lot and 0.5 m cell for 1 sim-day (F18); the game's own method then runs the last metres and the arrival.

| Shortcut | Hook | Result |
|---|---|---|
| go-home | `GoHome.Run` | F9: 329/404 in 10 min, 0 stuck |
| trips into buildings | `RabbitHole.RouteNearEntranceAndEnterRabbitHole` | Isla Paradiso: 25+ in 2 min, 0 stuck |
| community lot visits | `VisitCommunityLot.Run` (follower list preset empty) | IP: 70 in 5 min |
| home visits / go to lot / go to community lot | `VisitLot.Run`, `GoToLot.Run`, `GoToCommunityLot.Run` | IP: 10 / 6 / 9 in 5 min, 0 stuck |
| ambient traffic cars | `Route.PlanToPoint` for `CarNpcWithNoDriver` (LiteDriver) | **dropped**: Bridgeport, ~24 stuck Sims in 2.5 min vs 7 with it off (eating out, meet-ups, shows, visits); gain ~1% |

Houseboats (F7 data): boat moves cost 0.2–0.3%; boarding and leaving through gangways costs 5–8% (`SimRoutingComponent.CheckAndUpdateForHouseboats`, House Boat Fix's `EnterHouseboatThroughGangway`). So fix b2 lets go-home and lot shortcuts target houseboat lots while the boat is docked (`Houseboat.IsDocked`). Verified on `Baseline-IP`: 12 Sims placed on docked houseboats (Fantasea, Aquaholic, ...) in 2 min, 0 stuck.

Bridgeport baseline (all shortcuts but traffic): the same townies (Ti Bellino, Wogan Hemlock, Mars Champion, Regina Choi, ...) sit stuck in Go Home on community lots, routing, in every run of `Baseline-BP`: a pre-existing problem of that save, not caused by the shortcuts (to investigate separately).

## F11. Walk-style comparer fix [measured 2026-10-12]

`SimRoutingComponent.WalkStyleComparer.Compare` (private) replaced by `WalkStyleCompareHook.CompareHook`: an array indexed by the `Sim.WalkStyle` value (sequential from 0) instead of two `Dictionary<WalkStyle, int>` lookups that box the enum and call `Enum.Equals`/`GetHashCode` in the interpreter. Same priorities, same exception for a style not in the table. `Baseline-BP`, 3 min managed samples at x3 (885 before, 887 after): `GetAdjustedWalkStyleForRoute` 6.0% -> 1.7%, `WalkStyleComparer.Compare` 4.7% -> 0.1%, `SortedList.Find` 4.9% -> 0.2%, `System.Enum.Equals` self 5.1% -> 1.0%. First full replacement (no stub) and first hook on a private nested class (found by reflection).

## F12. Reaction broadcaster throttle [measured 2026-10-12]

`ReactionBroadcaster.PulseSims` hook (`BroadcasterPulseHook`): a broadcaster far from the camera and the active household pulses one call in four (staggered by a shared counter); near the player nothing changes. `Baseline-BP`, 3 min managed samples, against the walk-style build: `PulseSims` 7.1% -> 4.3%, `GetInRadiusSims` 3.4% -> 1.5%, `Lot.UpdateReactions` 8.6% -> 5.5%; 99,148 of 161,636 pulses skipped. The first build saved under 1%: the hook itself (~860 calls/s) read AppDomain data and rebuilt the household Sim list per call. Counters and camera/household positions are now cached (positions once per sim tick, `RefreshViewers`), which also cheapens every shortcut; all hooks together are 0.9% of script time.

## F13. All fixes together, three worlds [measured 2026-10-12]

Build 62c39f6 (patch host with go-home, building trips, lot visits, docked houseboats, walk-style comparer, broadcaster throttle), `profile-world.ts`, 10 min x3 each, against the morning baselines (no patches):

| | BP base | BP all | SV base | SV all | IP base | IP all |
|---|---|---|---|---|---|---|
| sim iterations/s | 31.2 | **50.1** (+61%) | 63.3 | **116.8** (+85%) | 31.7 | **73.8** (+133%) |
| hitches/s (>=50 ms) | 4.03 | **2.46** (-39%) | 1.90 | **1.00** (-47%) | 3.49 | **1.70** (-51%) |
| worst iteration | 1,014 ms | 468 ms | 2,210 ms | 2,885 ms | 1,170 ms | 676 ms |
| route ms/s | 94.9 | 107.8 | 82.2 | 77.3 | 246.4 | 183.5 |
| route plans/s | 26.5 | 42.9 | 10.1 | 12.8 | 9.7 | 14.0 |
| GC mark / forced ms/s | 16.8 / 15.9 | 7.0 / 3.6 | 11.2 / 6.6 | 14.3 / 2.8 | 14.1 / 13.2 | 10.2 / 3.6 |
| stuck Sims (3 min rule) | - | 0 | - | 6 | - | 5 |

Plans per real second rise because the sim runs faster (plans per iteration unchanged: BP 0.85 -> 0.86); cost per plan fell (BP 3.6 -> 2.5 ms). Open: the stuck Sims in SV (Meet Up / Visit at homes, routing without moving) and IP (houseboat households Arias and Scott going out) were not attributable with the 40-line shortcut log; rerun with the full moved-Sim record and a shortcuts-off baseline (in progress).

## F14. Correction to F2 and GC tuning options [code reading 2026-10-12, docs/13]

Correction to F2 (from `docs/13-gc-tuning.md`, agent, verified in disassembly): the game always enables incremental GC at startup (`GC_enable_incremental` call at 0xD7E602), and aborted mark work is kept between frames, not discarded. The per-frame `GC_try_to_collect` starts a new cycle whenever none is running, so the ~1 ms budget is spent every frame. Forced (stop-the-world) collections come from heap exhaustion: EA's allocation path collects instead of growing the heap in incremental mode, then grows by at most 8 MB, which keeps the heap tight and causes the 100-240 ms hitches. The exe reads `GC_INITIAL_HEAP_SIZE` (default only 40 MB, 0xE4E169), `GC_MAXIMUM_HEAP_SIZE`, `GC_PRINT_STATS`, `GC_DONT_GC`; there is no free-space-divisor variable (fixed at 5) and no parallel marking. Script-side `GC.Collect()` is not a cause (only on world quit and in debug code). Options, to measure one at a time: a larger initial heap (env var), a larger growth step (8 -> 32 MB, immediates near 0xE4A1D8), a higher budget ceiling (0x1192324). Expected gains are inferred, not measured.

## F15. Bigger GC initial heap, measured [2026-10-12]

`Baseline-BP`, full mod, 6 min x3 each, same build: default heap vs `GC_INITIAL_HEAP_SIZE` = 352 MB (env var via `ts3probe.py serve --launch --env`). Forced collections 128 -> **1** (max 160 -> 27 ms), forced GC 13.9 -> **0.1** ms/s, GC mark 15.4 -> 4.8 ms/s, hitches 3.25 -> **2.80** /s (-14%), iterations/s 40.1 vs 38.3 (noise), stuck Sims 0 and 0. Heap 120 MB (default, 6 MB free) vs 360 MB (151 MB free), one collection per 30 s instead of seven. Address space with the big heap: 2.25 GB committed, largest free block 1.29 GB. Shipped through `ts3native.asi` (`InitialHeapMB=352`, growth step 32 MB) so players need no environment variable; first in-game check of the plugin in progress.

## F16. Route fixes and ts3native in game [measured 2026-10-12]

In-load A/B (`profile-world.ts --ab`, `Baseline-BP`, 12 min, slices of 3 min; tag `bp-nab`) toggling the four route fixes for unseen townies (route-fail skip, backoff, station-detour skip, virtual-home fade), with `ts3native.asi` loaded: on vs off: iterations/s 40.8 vs 34.9, hitches 2.57 vs 3.36 /s, worst iteration 402 vs 857 ms, route search 97 vs 127 ms/s, 0 stuck Sims. `ts3native.asi` 0.1.0 log: all GC patches applied (heap 0x2800 -> 0x16000 pages; growth step 0x800 -> 0x2000 at three sites); heap 356-360 MB, forced GC 0.0 ms/s, one collection per 30 s after the load (47 in the first 30 s while loading). Open: full-mod runs sit at 38-41 iterations/s vs 50 in one run of build 62c39f6; not GC, not these fixes A/B of watchdog and spot spreading (`bp-wab`): on 37.8 vs off 37.8 iterations/s, hitches 2.81 vs 3.26; not the cause either, so the single 50 run is treated as an outlier.

## F17. S3PP `[SIMSTALL]` TaskObjects stalls: what the stage is and how to attribute it [code reading + log analysis 2026-10-13]

Source: `Game\Bin\Sims3Performance.log` of the `store-200-s3pp` phase-0 run (launch 00:53:25 local, live mode 00:55:15, game closed 00:58:29; `probe-out/phase0-s3pp.log`), S3PP 1.1 source `game-copy/s3pp/src-1.1/.../patches/simulator_stage_profiler.cpp`, Ghidra output `game-copy/analysis/decomp/{sched-seams,script-tick,host-ctor}.c`, `game-copy/analysis/xrefs-schedmaps.txt`.

**1. What the stage is.**
- The four seams are siblings inside `MonoScriptHost::Simulate` (`0xD81840`, `simulate.c`), not nested. Every loop iteration calls `0xD80BC0(0)` ("ProcessTaskState" in S3PP, our `ScriptHost_ScriptTick`) and then `0xD7FC60` (TaskObjects), unconditionally, before the GC step. The sim-step branch calls `0xD80BC0(n)`, then `0xD74800` (OneShotObjects) and `0xD7F8B0` (ThreadedObjects). S3PP's header comment that ProcessTaskState runs inside TaskObjects does not match this [verified].
- `0xD7FC60` walks the host's `Scripting/MonoScriptHost/TaskObjects` map. The host ctor `0xD7E010` names the maps at `+0x16A0` (OneShotObjects), `+0x16C0` (ThreadedObjects) and `+0x16E0` (TaskObjects), and each seam reads its own map's offsets [verified]. For each task (8-byte ObjectGuid key) without a context it resolves the task's virtual method (`0xEA0FB0` = `mono_object_get_virtual_method`, `object.c` assert) and allocates a 0x150-byte interpreter context (`0xE52D70`: `[0]` method, `[1]` object). It then resumes the task through `0xD7F000`, which runs the task's interpreted code until it yields or ends. While a task runs, `host+0xBC8` points at its context slot and `host+0xBD0/+0xBD4` hold its ObjectGuid. Ended tasks are removed after the walk [verified].
- Which tasks live in that map: the `Task` execute type, by the map names and the `ScriptExecuteType` enum [inferred; the native AddObject dispatch is a virtual call, not decoded yet]. `ScriptExecuteType.Task` objects in the scripts [verified-in-code]: `ImmediateInteractionTask` (autonomy immediate actions), `OneShotFunctionTask` (deferred work by the game and mods such as NRaas), `StateMachine`, `FinalizerTask`, `AgingQueue`, `StoryProgressionTask`, `ReferenceWatchdog`, `AutoSave`, `Gameflow`, `LoadSaveManagerTask`, and CAS/UI tasks (`SimBuilderTask`, `Camera`, `TooltipManager`, `MapTagController`, ...). `Sim`, `Lot`, `AutonomyManager`, `Services` and alarm one-shots (`AlarmOneShot : OneShotFunction`) are `Threaded` (04a), so interaction-driven `Route.Plan` should land in ThreadedObjects, not TaskObjects [inferred].
- Meaning of the log lines [verified in source]: S3PP's worker thread polls about every 250 ms (`Sleep(250)` + `Update()`). `active=N ... still-running` is printed the first time a stage is seen past the 250 ms threshold (so N is always about 250-500 ms), then every 5 s. It is **not** the stall length. The next `[SIMSTALL] ... duration=N` line (no `[WARN]`, written by the batch flush) is the full length of that one call. All events are on thread 5788, the sim thread, so each one is a single sim-loop iteration blocked for `duration` ms [inferred].
- This run: 15 TaskObjects stages over 250 ms, with durations 268, 420, 453, 517, 533, 553, 555, 624, 774, 1,314, 1,505, 2,355, **5,156, 5,658 and 6,873 ms** (27.6 s in total, median 624 ms), plus one ThreadedObjects stage of 294 ms. No OneShotObjects or ProcessTaskState call reached 250 ms. Correction to docs/17 §8: the stalls are 0.27-6.9 s, not "0.3-0.5 s"; the 0.3-0.5 s figures are the first poll [verified].

**2. Load or in play.** The log has no timestamps. Lines are in time order, all after `Entering message loop` and the DBPF warmup, so the file alone cannot split load from play [verified]. Hints:
- The log's last write is 00:57:45, 44 s before the game closed. S3PP therefore writes as it goes, and the last stall (453 ms) ended about 150 s into live mode, in play [inferred].
- In live mode the phase-0 sampler saw 94-140 sim minutes per 10 s window (00:55:25-00:58:19) with no gap. A 5-7 s stall exceeds the 19-step catch-up limit (F2) and should remove about 50 sim minutes from one window. The three 5-7 s stages (the 2nd, 7th and 9th of 16 events) were therefore most likely during world load or the first ticks, before 00:55:25 [inferred].
- No save or travel happened in this run [verified, run log].
- NRaas ErrorTrap wrote its post-load "Correction Logs" scan at 00:56:43, in play. That scan is a candidate for one of the 1.3-2.4 s stages [inferred, unmeasured].

To place future events, have the harness tail `Sims3Performance.log` every 250 ms and stamp each new line with wall-clock time (±0.5 s), then join it by time with the `profile-world` hitches and `/simstack` samples.

**3. What S3PP adds over our tools.** `tools/frida/stutter-profile.js` (used by `tools/harness/profile-world.ts`) times whole `Simulate` iterations (hitch >= 50 ms), `0xD80BC0`, GC, finalizers, catch-up and routing. Its "script" bucket is `0xD80BC0` only (about 0.1 ms per hitch), which is why F5 had a large "unaccounted" share. S3PP's split shows that this share is TaskObjects/ThreadedObjects/OneShotObjects. S3PP gives no task attribution (`arg` is always 0). `/simstack` and `interpStack` (agent.js) give the managed stack at random times, not during a stall.

Proposed measurement (design, to implement next):
- `stutter-profile.js`: add `taskObjects` (`0xD7FC60`), `oneShot` (`0xD74800`) and `threaded` (`0xD7F8B0`) to the timed hooks, per second and per hitch, so every hitch is split by stage. Each runs about once per frame, so the hooks are cheap.
- `agent.js` endpoint `taskstall?ms=250`: `Interceptor.attach` on the three seams records `start` and the host (`this.context.ecx`). A 50 ms `setInterval` watchdog on the probe thread looks for a stage open longer than `ms`. It then reads `host+0xBD0/+0xBD4` (the current task's ObjectGuid) and `*(host+0xBC8)` (its context; `ctx+4` is the managed task object, class name via vtable, klass, name `+0x34`/namespace `+0x38`, the layout `methodNameOfInfo` already uses), and calls the existing `interpStack()` for the method chain (it suspends the sim thread for microseconds). It repeats every 250 ms while the stage stays open (cap 40 samples). On leave it writes `{t, stage, totalMs, taskId, taskClass, samples:[{atMs, methods}]}` to `<run>-taskstall.jsonl`. For `ScriptProxy` tasks the method chain names the real object. `profile-world.ts` starts it next to the hitch profiler.
- Safety: S3PP detours the same four prologues. Disable its `SimulatorStageProfiler` key in `Sims3Performance.ini` while ours runs, and test the hooks on a dummy run first (probe-safety rule).

Built (2026-10-13, not yet run in game):
- `tools/frida/stutter-profile.js` times all four seams per iteration: `script` (ScriptTick `0xD80BC0`, as before), `taskObjects`, `oneShot`, `threaded`, in each hitch line and as new last columns of `-seconds.csv` (plus `maxTaskObjectsMs`). `profile-compare.mjs` prints "stage ms/s" rows (`-` for older runs). It now checks every prologue before the first attach and names an S3PP detour (`e9`) when one is found.
- Probe `/taskstall?start=1&ms=250[&sample=250]` (`stop=1` unhooks, `n=` last N stalls): hooks TaskObjects, OneShotObjects and ThreadedObjects as designed above; each record is `{time, end, stage, ms, task:{guid, class, method}, samples:[{atMs, task, methods}]}`, appended to `probe-out/<session>-taskstall.jsonl`. `task` is the one seen in most samples; a stall that ends within 50 ms of crossing `ms` has no samples. `profile-world.ts --taskstall 250` starts and stops it around a run.
- Turn S3PP's `SimulatorStageProfiler` off while either runs; both refuse to hook a detoured prologue.
- Unverified: the context layout (`*(host+0xBC8)` = context, `[0]` MonoMethod, `[1]` task object, object `+0` MonoVTable whose `+0` is the klass) comes from the decompile, not a live read; hook cost (~100 seam calls/s, two QPC reads and two Frida callbacks each) is estimated, not measured.

**4. Likely culprits [inferred, to confirm with the measurement above].**
- 0.3-2.4 s in play: Task-type work that does not yield. Candidates: mod `OneShotFunctionTask`s doing whole-world scans (ErrorTrap/Overwatch cleanups), `ImmediateInteractionTask`s (autonomy immediate actions and any route plan they make), bursts of `StoryProgressionTask`/`AgingQueue`/`ReferenceWatchdog`, and `FinalizerTask` draining script finalizers. A forced (non-abortable) GC triggered by an allocation inside a task also counts toward the stage (up to 149 ms in F5; near 0 with ts3native's heap patch, F16). Interaction routing, autonomy scoring and broadcasters run in Sim/Lot/AutonomyManager (Threaded), and ThreadedObjects stalled only once (294 ms), so they are not the main suspects for these TaskObjects stalls.
- 5-7 s: world load and the first ticks (`Gameflow`, `LoadSaveManagerTask`, the `OnWorldLoadFinished` handlers of the game and mods). A save (`AutoSave`/`LoadSaveManagerTask`; `0xD7FE80` references `ScriptCore.LoadSaveManager`) or travel would land in the same stage, but neither happened in this run.

## F18. First /taskstall run names the long stages (2026-10-13, Bridgeport, 10 min, `profile-world --tag bp-budget --taskstall 250`)

Data: `probe-out/20261013-011301-taskstall.jsonl` [verified]. Five stalls over 250 ms in 10 minutes (S3PP off):

| ms | stage | task and managed stack (innermost first) |
|---|---|---|
| 287, 311, 326 | TaskObjects | `FunctionTask` -> **NRaas Overwatch `Alarms.StuckCheck.PrivatePerformAction`** -> `SimEx.IsPointInLotSafelyRoutable` -> `Route.PlanFromPoint` (a route plan per Sim, all in one task tick) |
| 362 | Threaded | `ScriptProxy` -> `GoHomeHook.Run` -> **our `Ts3Fixes.Fixes.SafeSpot`** -> `Sim.IsPointInLotSafelyRoutable` -> `Route.PlanFromPoint` |
| 1,069 | Threaded | `ScriptProxy` -> `Lot.PlanToPointRadialRange` -> `RouteBackoffHook.Plan` -> `Route.Plan` (an ordinary lot route) |

- Overwatch's stuck check duplicates our watchdog and costs ~300 ms each time it runs: candidate fix = turn off Overwatch's "Stuck Check" in our installed Overwatch settings (keep its other cleanups), our watchdog covers stuck Sims.
  - Follow-up (V123 decompiled, docs/07 §2.2): the stack is `FunctionTask` -> `Overwatch.OnTimer` -> `AlarmOption.PerformAlarm` -> `StuckCheck.PrivatePerformAction` [verified, full sampled stack]. `OnTimer` is Overwatch's **nightly** alarm (`AddAlarmDay` at Alarm Hour) running every nightly cleanup in one task, so this is one hitch per sim day, not a periodic check [verified code]; three in 7.3 real minutes means about three sim nights passed at ultra speed [inferred, sim clock not logged]. Each stall has one sample (at 267-282 ms) in StuckCheck, so the other nightly cleanups may share the time [inferred]. Our watchdog does not cover all of it (idle Sims on unroutable spots, hidden rooms, active-household route-fail resets). Kept on; optional `overwatch-no-stuckcheck` installer entry (default off, tuning `kStuckCheck` = False for new saves) pending a taskstall A/B.
  - **Built (2026-10-13, not yet measured):** `mods/ts3fixes/src/OverwatchStuck.cs` hooks Overwatch's static `NRaas.CommonSpace.Helpers.SimEx.IsPointInLotSafelyRoutable(Sim, Lot, Vector3)` through the patch host (static hook + static stub). Resolved by assembly name `NRaasOverwatch` among the AppDomain assemblies, with an exact signature check; tried in `Fixes.Apply` (startup) and again in `OnWorldLoadFinished` (Overwatch may load after us; its only caller is `StuckCheck.PrivatePerformAction`, reached from the nightly alarm that `OnWorldLoadFinished` arms, so the method is still uncompiled then) [verified code, V123]. Not found or different signature = nothing patched, a line in `ts3fixes.applied`. The hook calls NRaas's original unchanged, then `Simulator.Sleep(0)` when `Simulator.CheckYieldingContext(false)` (the alarm's `FunctionTask` and the menu interaction are script tasks): one route plan per sim tick. Yielding inside the callee instead of re-implementing the loop keeps NRaas's own loop, order and decisions; each Sim's plan and reset choice still happen in one tick, only the position bookkeeping after the call and later Sims run a tick later. Not cached (step 1 dropped): NRaas plans with `SimRoutingComponentEx.CreateRouteAsAdult`, whose `SetRouteOptions` reads species flags, posture, ghost buff, boat parent, valid rooms of `LotCurrent`, pets/horses and more, so a verdict is per Sim and per moment, and within one run no two calls share Sim and exact spot [verified decompiled V123]. (The game's own check has the same dependence: see the SafeSpot key fix below.) Switch `overwatchstuck` (default on), counters [44] checks / [45] yields (`patches`: `overwatch_checks`, `overwatch_yields`). Test: repro `bp-overwatch-stuckcheck` (calls `NRaas.Overwatch.OnTimer` twice via `reflect_call`; A/B `--ab overwatchstuck` under `profile-world --taskstall 250`): no StuckCheck stall over 100 ms with it on, same resets either way.
  - **Yield guard (2026-10-13, ts3native 0.5.1):** `CheckYieldingContext(false)` only proves a current task; a yield from code running inside a native->managed call during that task aborts the game (`interp.c:3595` `pState->yieldReturn`, seen with the async route plan, docs/18 "Crash: yield in a nested call") [verified mechanism, disassembly]. The hook now yields only when `MayYield()` (RouteBackoff.cs) also gets "may yield" from ts3native (current interpreter state == the task's root state); otherwise it returns without yielding, as NRaas's own loop would. Without ts3native the old rule applies. Overwatch's own path (`FunctionTask` -> `OnTimer` -> ... -> `SimEx`) is all managed, so the guard should never fire there [inferred].
- Our own `SafeSpot` safety check is a full route plan (~360 ms here): cache the result per lot/spot (spots are reused by spot spreading) or test fewer candidate spots per call.
  - **Built (2026-10-13, not yet measured):** `Sim.IsPointInLotSafelyRoutable` plans with `CreateRouteAsAdult()` from the spot to the lot's 4 corners and 4 edge midpoints (radius 1 m, 2 m on houseboats); ~~the only Sim input is `IsHuman`~~ (wrong, corrected the same day: see "Cache key fix" below). So `SafeSpot` now caches the verdict per lot and per 0.5 m floor-aligned cell (y in 1 m steps), good and bad alike, for 1 sim-day; a lot's cache is dropped when its household changes or the clock goes back (another save loaded). Each call first tries spots next to the lot's verified ones (up to 8, rotated so arrivals spread; `spotspread` off = always the last one), accepting only cells already verified good, then up to 3 fresh spots from the entrance, which may run a full check only within a budget of 1 per sim tick. No verified spot = the game's own trip, never an unverified spot. The old "within 3 m of the last verified spot, re-verify every 8th use" trust is gone. Counters [37] hits, [38] misses, [39] full checks (`patches`: `safespot_hits/misses/checks`). Test: repros `bp-hogans-pileup`, `bp-hogans-gohome` (no stuck or piled Sims), then `profile-world --taskstall 250` in Bridgeport (no `SafeSpot` stall; checks well below shortcuts). A single check can still take ~360 ms when it runs; the cache only makes it rare.
  - **Cache key fix (2026-10-13, built, not yet measured):** the `IsHuman`-only key was wrong [verified, decompiled `SimRoutingComponent.cs`]. `CreateRouteAsAdult()` keeps the Sim's species and gender flags (`SimFlags & 0xFFFFFF80 | 0x20`) and runs `SetRouteOptions`, which sets options from the Sim: real age (`ChildOrAbove`: car, water and boat planning), posture (carrying, scuba, riding, leading a horse, stroller, frozen-pond planning), `HasGhostBuff` (`RouteAsGhost`), mermaid, lifeguard level (`RouteAsLifeguard`), an owned UFO (`EnableUFOPlanning`), a repo man or social worker service (`PassThroughWalls/Objects`), pets, horses, deer, wild animals, a boat parent (the boat becomes the follower), and no options at all when the current interaction is `IHasCustomRouteOptions`. The valid-room limit is skipped (adult flags). So a ghost townie's "routable" verdict could have been reused for a living Sim, and the other way round. The key is now cell + route profile: `Fixes.ProfileOf` builds the route exactly as the game's check does (`CreateRouteAsAdult`, then water planning = `IsHuman`) and reads back every `RouteOption`/`RouteOption2` bit plus the age/gender/species flags; a verdict is reused only for an identical profile. Sims with a parent (boat, vehicle) are never cached, because the boat follower and ignore-object do not show in the bits. A lot's cache is also dropped when the lot moved (a houseboat at another dock). Cost per lookup: one route object and ~73 option reads, no plan.
- **Our own stuck check (2026-10-13, built, not yet measured):** `StuckWatchdog` (`RouteFixes.cs`) now also covers Overwatch's Stuck Check cases (docs/07 §2.2) behind switch `watchdogidle` (tuning `kStuckWatchdogIdle`, default on; also needs `watchdog`). Every 10 sim-minutes:
  1. A non-selectable Sim still (within 1 m) in one or no interaction, on a non-world lot, standing: 60 sim-minutes for an unseen townie, one sim-day for anyone else. Then the game's `IsPointInLotSafelyRoutable` through the profile-keyed cache. A cached "not routable" is re-checked before acting; a good verdict is trusted for a sim-day. Not routable -> `AttemptToPutInSafeLocation(false)` when nothing is queued (nothing cut), else `SetObjectToReset` (the game's reset runs the same placement). Counter [46].
  2. Same stillness in a room the lot marks hidden (`Lot.IsRoomHidden`) -> `SetToResetAndSendHome`, or a plain reset for a homeless Sim [47].
  3. Any non-selectable Sim a whole sim-day in one spot and interaction (not in a rabbit hole, bed or vehicle) -> reset [48].
  4. Any Sim, active household included (as Overwatch's route-fail callback), with at least 10 failed plans over more than 30 sim-minutes within 1 m -> reset [49]. Failures are counted in `RouteBackoffHook.PlanHook`; a successful plan clears the count; our own and Overwatch's routability checks are not counted. Overwatch's exclusion of EA's `RunStuckCheck` is not copied: that is a debug-only interaction.
  - Budget: at most one full routability plan per sim tick (shared with SafeSpot), a yield after each, at most 16 verdicts per pass; the rest is the existing one-tick scan. Shared daily limit (once per Sim per sim-day, a second time only more than 10 m away). Counters [50] verdicts asked, [51] Sims with failing plans tracked (`patches`: `watchdog_idle_unroutable/hidden_room/stationary`, `watchdog_route_fails`, `watchdog_idle_verdicts`, `watchdog_route_fail_sims`).
  - On-screen Sims are acted on only where Overwatch would (the one-day rules and route failures), never by the fast 60-minute rule.
  - Test: repros `bp-idle-unroutable` and `bp-route-fail-active` (new agent command `sim_strand`: stands a Sim on a spot of a lot that fails the game's check), A/B with `--set watchdogidle=false`.
- The 1 s plan is the long-search case docs/18 targets (route budget / off-thread).

## F19. The per-frame function 0xEC9F00 (frame-time hook for test B) [verified in Ghidra 2026-10-13]

`FUN_00ec9f00(bool present)`, `__thiscall`, 0xEC9F00-0xEC9FDE, prologue `83 ec 20 56 e8 b7 43 74` (`sub esp,20h; push esi; call 0x60E2C0`), single exit `ret 4`, no tail calls, no jump targets in the first 9 bytes (what Frida rewrites).
- Body: graphics device = `0x60E2C0()`; `0x611620` BeginScene (device vtable +0xA4; returns false when device +0x8D "not foreground or lost" is set, which skips rendering); scene render; `IC_StreamHost_Flush`; then, only when `present != 0`, `0x611760` -> `0x611680`: EndScene (+0xA8) and Present (+0x44); then the inactive-window ~30 FPS sleep at 0xEC9FBA (the S3SS Smooth Patch anchor, docs/03; S3SS jumps out there and back, the prologue stays intact).
- Callers, all on the main thread (entry -> `__tmainCRTStartup` -> 0xECBE30 -> 0xECBD00): the main loop `0xECA960` (runs until state `0x28D869F`) calls it with `present = 1` once per iteration at 0xECAAFC; `0xECA2A0` (window-message path via 0xECA350) with 1; the callback `0xECA030`, run by `0x9DE140` earlier in the same loop iteration, with `present = 0` (renders without presenting). So frames = calls with `present = 1`; counting every call would double-count frames whenever the callback path runs.
- Not the sim thread: the Mono sim loop (`MonoScriptHost::Simulate` 0xD81840) runs on its own thread with `[Script] Multithreaded` on.
- Nothing else hooks the range: ts3native detours none of 0xEC9F00-0xEC9FDE (address list in `native/ts3native/*.cpp`), S3SS patches only 0xEC9FBA, DXVK replaces d3d9.dll and patches nothing in the exe, S3PP's stage profiler hooks the sim loop.
- Presents this hook does not see: loading screens and startup (`0xEC9CD0`, `0xEC6370` call 0x611760 directly). In live mode the main loop is the only presenter, so B needs nothing more. A d3d9 vtable `Present` hook would count those too but needs the device pointer from 0x60E2C0 at run time.
- `tools/harness/frame-times.js` hooks it with a CModule (no JS lock on the render thread, no thread suspended) and records only `present = 1` calls. Dummy-tested on a 32-bit process: the CModule compiles on ia32, `present = 0` calls are skipped, a 125 ms stall shows as one 125 ms frame. Two Frida host processes = two frida-agents in the game: they must detach in reverse load order (first-loaded agent unloading first crashes the target at the later unload; dummy, even with no-op scripts, 2026-10-13), so tests-be.ts B hooks frames first and lets the profiler detach first. First in-game run still to do.

## F20. ScriptHost_InvokeDelegate reads past a zero-parameter delegate signature [verified disassembly 2026-11-09, docs/539 §20]
- 0xD70F90 (only caller 0xD75FA0 at 0xD75FD3; eax = delegate handle, bool in al) reads params[0] (`mov edi,[esi+0x10]`) and calls mono_type_get_type 0xF4AC60 on it before it checks `param_count == 1` (`cmp word [esi+4],1`). For a zero-parameter Invoke (GameUtils.SwapLoadScreen's `SwapLoadScreen()` delegate, via 0x7F1760 / global 0x11D8C4C / 0x7F1480) that reads the dword after the 0x10-byte signature; two travel-screen crashes read `[1+6]` at 0xF4AC64 [verified: float5-round3 all-off logs].
- Every param_count != 1 answers false anyway (0xD70FEB), so checking the count first changes no result. `[MonoSigGuard]` (monosig.cpp, 0.36.232) does that at 0xD70FB2.
