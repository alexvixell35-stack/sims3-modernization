# 338. Scene index-list drain vs. the main-view cull job (suspected EA race)

2026-11-23, branch `bugfix/scene-cull-race-1123`, ts3native `0.36.299-cullrace2` (cullrace1 was ae04b156…; cullrace2 = crRace counts state 4 only, see §7), static + build + native selftest, **no game run**. Ledger docs/51 §605. Counters v225 (`cr*`, `cdw*`).

## 1. Why

The released v0.3.1 crashed on EA 1.69 (Lenovo triage 2026-11-23) at EA `0x6E54F5` = Steam `0x6E4735`, inside the scene index-list drain, on the normal main-thread draw (`Renderer_DrawScene 0x6F3F90` → SceneWindow draw → UTFWin display lists → WindowManager draw → `Frame_RenderAndPresent`). The index read from the list was a heap pointer (`0x2D0B2B40 + 4*0x274E3380`), so the list buffer had been freed and reused. Rule: crash cause before fix, ours until proven.

## 2. Static facts (Steam 1.67, `game-copy/bin/TS3W.exe`, capstone disassembly)

| # | Claim | Status |
|---|---|---|
| 1 | `0x6E4720` (thiscall scene) walks `[scene+0x40, scene+0x44)`: `idx = [esi]`, `obj = [[scene+4] + idx*4]` (`0x6E4735`, the fault), `obj->vfunc+0x74()` if non-null, then `+0x44 = +0x40` | [verified] |
| 2 | Writers: inlined `push_back` on that vector (`+0x40` begin, `+0x44` end, `+0x48` cap, grow `call 0x8621C0`) in `Scene_CullQuadtree 0x6E4F90` at `0x6E53CD` and `0x6E567B`, and in `Scene_CullLinear 0x6E5880` at `0x6E5B6B` (grow `0x6E5B8A`). No lock, no interlocked op (docs/48 §1) | [verified] |
| 3 | Growth `0x8621C0` (vector insert, EA `0xC217E0`): new buffer from `0x4E3F90`, memmove, **old buffer freed with `0x4E3F70` at `0x8622A9`**, then begin/end/cap rewritten | [verified] |
| 4 | `Renderer_DrawScene 0x6F3F90`: if `+0x28F` (multithreaded cull) it waits `+0x310` only if `+0x28E`, `+0x314` only if `+0x294`, `+0x320` only if `+0x29C` (each `JobManager_Wait 0x59A500`, fastcall handle), else culls inline (`0x6EBE30`, `0x6F1B00`, `0x6EA260`, `0x6EBF50`, `0x6F1D10`); then `mov ecx,[esi+8]; call 0x6E4720` at `0x6F401B`. **It never waits for `+0x318`** | [verified] |
| 5 | The other drain callers wait for 318 first: DrawMainPass `0x6F1F18` (`+0x28F` → wait 318 → drain `0x6F1F26`), SceneManager_Draw `0x6F3CD8` (waits 320, 318, 31C, then `0x6F3D09`). `0x6F230E` drains after an inline cull; `0x9DE391` not read | [verified] |
| 6 | Job 318 = `RenderJob_MainCullFn 0x6F3C30` (stored at `0x6F45FD`), which calls `0x6F1B00` at `0x6F3C3B`; 310/314/320 depend on 318 (docs/46), so a wait on any of them covers it | [verified call; dependency from docs/46] |
| 7 | Job state: handle = job+8, state at job+0x24 (`[handle+0x1C]`); `ExecuteJob 0x599720` sets 4 while the body runs; EA's wait returns at 0/1/6/7 (docs/36) | [verified] |
| 8 | Cull jobs are mask -1: JobThread0, the main pump, or the sim pump (no budget) can run them (docs/36, docs/46) | [verified, docs/36] |
| 9 | With lightingquality 1 and generalreflections 0, `+0x28E`, `+0x294`, `+0x29C` are all 0, so DrawScene drains while 318 may still be appending. **`+0x28F` (multithreaded cull) is 1 on EA at lighting 1 / reflections off** (our guess was 0) | [verified on EA, §7] for `+0x28F`; the other three [inferred] |
| 10 | The race (drain on main while 318 grows the list on another thread) is the crash cause | [inferred] — the detector and the queue below decide it |

## 3. Is ours involved?

- No ts3native hook in the cull, draw or drain code before this branch: grep of `native/ts3native` for `0x6E4xxx` / `0x6F1xxx` / `0x6F3xxx` / `0x8621C0` finds only crashfx draw guards in the model draw (`0x6F6B5B`, `0x6FA2DB`, default off) and comments [verified].
- `[RouteAsync]` does **not** run route jobs on JobThread0: our route worker is our own thread (`StartWorker` → `CreateThread(WorkerMain)`, `g_workerTid`) [verified]. The brief's amplifier mechanism (route jobs crowding JobThread0) does not exist; an indirect effect (CPU contention slowing JobThread0, so 318 runs later or on the sim pump) is possible [inferred, unproven]. The A/B arms in the queue measure it.
- Ours that touches the job system: `[JobQueue]` (`jobqueue.cpp`): #13 `jqSimMode` clears bit 31 of the sim record so the sim pump skips shared jobs (render jobs included), #14 more workers; RunJobs `0x59A430` hooks of `[LotStream]` / `[Verify]` time the pump only [verified]. #14 would add consumers for 318 (more chances to overlap the drain); #13 takes the sim thread out [inferred].

## 4. Build

`cullrace.cpp` (included after `savewrite.cpp`), two call-site retargets, byte-checked by `PatchCalls`:

- `0x6F401B` call `0x6E4720` → `CrDrainStub`: `CrBefore(scene = ecx, renderer = esi)`, then the game's drain.
- `0x6F3C3B` call `0x6F1B00` → `CrCullHook` (watch only): keeps the id of the thread inside the main cull, so a hit names it.

Switches:

| Switch | Default | What |
|---|---|---|
| `[CrashForensics] CullRaceWatch` | **1** (logging only, brief) | at the drain: `crRace` when `+0x28F` and job 318's state is 4 (running; cullrace1 counted every state not 0/1/6/7, which wrongly included 3 = queued, not started); `crBadIdx` when an index >= object count (`([scene+8]-[scene+4])/4`) or the list is unreadable. `cullrace: RACE` / `cullrace: BADIDX` lines (draw tid, cull tid, sim tid, job state, list / object counts) on the first hit and every `CullRaceLogEvery` (64). One `cullrace: first DrawScene drain` line says whether `+0x28F` is on at all. Read even with `Enabled=0` / `Light=0` |
| `[EaCrashFixes] CullDrainWait` | 0 | `JobManager_Wait(+0x318)` before the drain when `+0x28F` is set (EA's own wait; its prologue is byte-checked, else SKIP). EA's draw passes wait for 318 right after, so the expected cost is ~0; `cdwWaits` / `cdwWaitUs` show it |

Counters v225 at `0x6B74` (after the v223/v224 hold `resV223_224[11]` at `0x6B48`, block 6883 int32): `crOn` (bit 0 watch, 1 cull thread recorded, 2 CullDrainWait, 3 drain hooked: 11 = defaults, 15 = fix on), `crDrains`, `crRace`, `crBadIdx`, `cdwWaits`, `cdwWaitUs`. Renderer only: no Sim state is read or written, so the household rules do not apply.

No leaks: no allocation; the cull tid slot is cleared after every cull. Native selftest `TestCullRace` (fake renderer / job / scene): race only with `+0x28F` and state 4 (state 3 queued = no race), bad index incl. the crash value and an unreadable list, the wait only with the fix and `+0x28F`, watch off counts nothing, cull tid kept only during the cull.

Harness (branch-only, merges between game runs): `queue.ts` job key `optionsIni` (`options.<key>` merged into `<user>/Options.ini`, put back and read back after the job), self-check case; repro `ip-cullrace-pan` (30 Sims, far Go Here without the household, 2 × 245 s camera pans).

## 5. Queue `cullrace-correct-1124` (NOT RUN, Apex + bridge: Bin state `apex`)

1. `bp-cullrace-smoke`: BP 10 min, defaults; read-back `crOn==11`, `crDrains>=1000`, `crBadIdx==0`, the version / hook / first-drain (`+0x28F` 1) lines, `scriptErrorsMax 0`. If the first-drain line says `+0x28F` 0, multithreaded cull is off in this setup and the race cannot happen here: stop and report.
2. `ip-cullrace-async-on` / 3. `ip-cullrace-async-off`: IP, lighting 1 / reflections 0, `[RouteAsync] Enabled` 1 / 0, repro `ip-cullrace-pan`; early gates `crRace==0`, `crBadIdx==0` stop the job at the first hit (that FAIL is the repro: read the `cullrace:` line).
4. `ip-cullrace-fix`: as 2 with `CullDrainWait=1`: no crash, `crBadIdx 0`, `cdwWaits >= 1000`; `cdwWaitUs / cdwWaits` = cost per frame.

Ship rule: CullDrainWait ON only after the repro shows hits off and none on (rare-crash rule: seen + repro + fix works = ON).

## 6. Addresses (Steam 1.67 → EA 1.69 candidates for the Lenovo session)

| Steam | Use | EA 1.69 candidate | Retail 1.67 (verified) |
|---|---|---|---|
| `0x6F401B` | call site hooked (drain call in DrawScene) | `0x6F4D4B` (Lenovo) | `0x6F3EAB` |
| `0x6E4720` | drain, original target, called through | `0x6E54E0` (Lenovo) | `0x6E4390` |
| `0x6F3C3B` | call site hooked (main cull in job 318) | `0x6F496B` (offset of the `0x6F3C30` row only) | `0x6F3ACB` |
| `0x6F1B00` | main cull, original target, called through | `0x6F2830` (table) | table row |
| `0x59A500` | JobManager_Wait, called by the fix | `0x59ACF0` (Lenovo; = `0x59AC20` row + 0xD0) | `0x59AEC0` |
| `0x011D8A8C` | sim thread id, read for the log | `0x01232B3C` (table) | table row |

Renderer offsets read: `+8` scene, `+0x28E`, `+0x28F`, `+0x294`, `+0x29C`, `+0x318`; scene `+4`, `+8`, `+0x40`, `+0x44`; job handle `+0x1C`. Apex coordination list: renderer area, built by us, off by default (the detector is log-only), proposed with proof once the queue has run.

## 7. EA laptop evidence 2026-11-23 and the state fix (cullrace2)

- EA verified `JobManager_ReadyJob` stores state 3 = queued, not started [verified on EA]. A queued job has not touched the list, so it is no overlap; only state 4 (running) is. cullrace1 counted every state outside 0/1/6/7, so it also counted queued. cullrace2 `crRace` counts state 4 only; no separate state-3 counter (it needs a new counter slot, not worth it).
- `+0x28F` (multithreaded cull) is 1 on EA with lighting 1 and reflections off [verified on EA].
- cullrace1 (fix off), 4 EA jobs: ~590 state-4 lines (every 64th hit logged), 9 of them with a non-empty list (sizes 7-219), `crBadIdx` 0, no crash [verified, EA log read]. So the overlap happens, but in these jobs it did not corrupt the index list. The state-3 lines were not separated, so the exact split is unknown; cullrace2 gives it.
- Queue `cullrace-correct-1124b` is `cullrace-correct-1124` pinned to cullrace2 (smoke, IP RouteAsync on / off, `CullDrainWait=1` arm); the 1124 queue stays on cullrace1.
