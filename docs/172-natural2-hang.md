# 172. bp-natural-2 hang (2026-10-22, heartbeat frozen)

Read-only triage of queue job `bp-natural-2` (`tools/harness/queues/faster-tests-example.json`, chain138). No game run, build or selftest. Ledger rows: docs/51 §280.

Sources: `probe-out/queue/chain138.log` (lines 686-723), `probe-out/queue/faster-tests-example-2026-10-22-bp-natural-2-1921-ts3native.log` (the hung process's ts3native.log, copied by the queue), `probe-out/profile/faster-tests-example-2026-10-22-bp-natural-2-*` (`600-hitches.jsonl`, `600-seconds.csv`, `600-freezes.jsonl`, `load5-192229-frames.csv`, `frames.log`, `calls.jsonl`, `upkeep.log`, `machine.jsonl`, `nativelog.jsonl`). `samplers.jsonl` and `simstack.jsonl` are empty (noSamplers). No new EA `xcpt` file: the newest one is from 2026-10-22 20:15 PHT, which is from an earlier run.

Times are UTC (harness logs). The QPC-to-epoch offset comes from `frames.csv` (`epochMs - qpcMs` = 1792591547196.4 ms, drift 0.13 ms from the first row to the last), the same alignment `worst-hitches.ts` uses.

## 1. Corrections to the event report

- **Build: asi.0329 (ts3native 0.36.24), not asi.0332.** The change set for every load of this queue is `ed988ac3c157`, and its asi hash is `19466ae1b74f`, which is `game-copy/ts3native.asi.0329` (sha256 prefix). The queue JSON pins `.0329` for all three jobs, and the archived log header reads `ts3native 0.36.24 2026-10-23 03:21:02`. The chain line "installed the source build" does not say which build that was. [verified]
- **This build has no [HitchAttr] Holders, no F1 lock fast path and no PlanDeadline/FailMemo.** Holders arrived in 0.36.25 (hitchattr.cpp), F1 in asi.0330 (commit ca61d83a), and the archived log has no PlanDeadline line. The stutter profiler printed `ts3native lock holders not active (holders stay empty)`. All four are therefore ruled out as causes. [verified]
- **The `ts3native.log` in `Game\Bin` belongs to the next job's process.** Its header is `0.36.27 2026-10-23 03:32:16` (19:32:16 UTC, from `bp-overwatch-ab-loop2`, which started at 19:32:15), after this game was killed at about 19:32:11. The exception lines quoted in the event (thread 27892) come from that later process. The hung process's own log has the same five lines on its main thread 42328 (lines 189-193). [verified]
- **Session reuse did not happen because of the carry-over check, not a fault.** At the end of bp-natural-1 the queue logged `carry-over check for bp-natural-2: drift: lot objects 110 -> 116 (limit +-5); sim time +96.1 h since load (limit 48 h)`, so it started a fresh game. bp-natural-1 had restarted the game in the same way. [verified]

## 2. Who owns 0x710E672C

**Not ts3native, and not the hang.** [verified for both points]

- In the hung process, ts3native's image is `0x6EB30000-0x6EE18000` (log header). 0x710E672C is outside that range.
- The pair `0x...672C` / `0x...666C`, both reading `0xCCCCCD04` (= `0xCCCCCCCC + 0x138`), appears on the main thread right after ts3native's `done` line, before the world loads:
  - in 550 of the 621 archived `probe-out/queue/*ts3native.log` files;
  - at bases `0x7065xxxx`, `0x706Dxxxx`, `0x70EAxxxx` and `0x710Exxxx`, so it is one DLL that the loader places at a different base on each boot, at offsets 0x672C/0x666C;
  - in the passing bp-natural-1 log (`...-bp-natural-1-1909-ts3native.log`) as well.
- The exceptions are first-chance exceptions that something catches, followed by a C++ `E06D7363` throw.
- Stack refs are UTF-16 path text (`\\?\Program F...`), so the code is handling a file path at startup.

Which DLL it is cannot be told from these logs:
- `frames.log` prints only the CModule base (`0x564c0000`), not the module list.
- ts3native's first-chance logger prints raw eip without a module name.

The `0xCCCCCCCC` pointer fits a module built with MSVC `/RTC` (debug stack fill) or an uninitialised stack slot. That is [inferred] only.

**To capture it next time:** have ts3native's first-chance logger resolve `GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | ..._UNCHANGED_REFCOUNT)` plus `GetModuleFileNameW`, and print `module+rva`. Alternatively, dump `Process.enumerateModules()` once from `frame-times.js` into `frames.log`. This is low priority: it is boot noise seen on 89% of runs.

## 3. Timeline

| UTC | event | source |
|---|---|---|
| 19:21:00 | job start; carry-over check failed, so the game restarts | chain138 682-687 |
| 19:22:22 | world ready (Baseline-BP-Spring, LiveMode) | chain138 |
| 19:22:30 | window start; legreplanskip off, bouncerskip/idlememo/stuckcheckasync/errortrapspread on, seed 12345 | chain138 |
| 19:24:24 | last nativelog line: 158 ms sync plan `NoStart`, stack through `Ts3Fixes.Fixes.StuckCheckRun` | nativelog.jsonl |
| 19:31:05.38 | last agent call batch answered normally (`patches`, 50 ms) | calls.jsonl |
| 19:31:09.40 | last hitch: 60.8 ms (QPC 105922206.5), `Sims3.Gameplay.Actors.Sim` in FollowRouteAction, 35 ms `rtLock` at gate 0 `0x0062EF7A`, holders empty | 600-hitches.jsonl |
| 19:31:09.63 | last complete per-second row: 37 iterations, max 60.8 ms | 600-seconds.csv |
| ~19:31:10 | **sim thread stops.** After the 19:31:09.63 row, 14 more iterations finish (about 0.4 s at the run's ~35/s). The next iteration never finishes. Sim-to-main messages per frame drop from about 21,000/s to 0 from the 19:31:10 5-second bucket on. | seconds.csv final row, frames.csv `msgs` |
| 19:31:10-19:32:11 | **main thread keeps running**: 300 frames per 5 s, frames 16-19 ms (one 33 ms), every frame `consumes` 1 and `drains` 1 with 0 messages | frames.csv |
| 19:31:32 | `test_env` RPC takes 8.0 s; the next calls return in 2-3 ms; at 19:31:40 the upkeep loop logs `RPCException: previous request still pending` (the mailbox request is never answered by the sim tick) | calls.jsonl, upkeep.log |
| 19:31:39 | TS3W uses 9.4% of the machine (12.0-13.6% before), about 1.5 logical CPUs; other CPU 1.7% | machine.jsonl |
| 19:32:10 | `game-alive.ts`: `FAIL: game died at 19:31:10 (agent not answering for 60 s (heartbeat frozen))`; `profile-world` exits; the profiler unloads and writes the final seconds row (14 iterations, max 33.56 ms) | chain138, seconds.csv |
| 19:32:11.36 | last frame row; the frames session prints `Process terminated` (queue `taskkill`) | frames.csv, frames.log |

Findings from the timeline:

- **Thread:** the sim thread stopped. The main thread did not stop. [verified]
- **Main-thread exceptions:** all five came about 9 minutes **before** the freeze, at process start, and are the routine boot pair described in §2. Nothing was logged in the 60 s window. [verified]
- **No crash:** no access violation, no xcpt file. The process was killed while hung. [verified]
- **No RPC in flight when it stopped:** the last batch ended at 19:31:05.38 and the next began at 19:31:32. The probe mailbox did not trigger the hang. [verified]
- **The sim got stuck inside one iteration, not between iterations at a measured boundary.** The final row's `maxIterMs` is 33.56 and an iteration is recorded only on leave. [inferred: depends on how stutter-profile.js measures an iteration]

## 4. Causes, most likely first

Every candidate below except rank 4 ran in the same configuration for about 45 minutes of clean play just before this job:
- change set `ed988ac3c157` = asi.0329 + the same ini;
- frames session with the stage and service CModule probes (the `bp-natural-1` frames.log shows the same `svc_start`/`svc_end`/`stage_*` symbols);
- stutter profiler with the task CModule;
- jobs bp-scen-multi (3 loads, 33 min, PASS) and bp-natural-1 (11.7 min, PASS).

The only per-job difference is **legreplanskip off**, which brings back the game's own leg replans, so the sim makes more synchronous replans. `tool-check` used asi.0331 (`380f90054a65`), so its configuration is not comparable.

1. **ts3native 0.36.24 route path on the sim thread: a deadlock or an endless wait or loop.** The candidates are:
   - the `Lock_Acquire`/`Lock_Release` route-gate detours;
   - the lot-table find-or-insert detour (the log has a `route gate: unlocked entry` line for the sim thread);
   - RouteAsync with FallbackAsync, M1Retry, TailUnlock, ReplanDedupe and H2;
   - SyncCapMs;
   - RouteEarlyOut Act;
   - SlotEnabledRO Mode 1;
   - UnseenThrottle.

   The last hitch was a gate-0 lock wait. Turning legreplanskip off sends more sync plans through these paths. The 9.4% CPU reading sits between "sim blocked" (main about 0.9 core, roughly 6%) and "sim spinning" (roughly 12%), so it fits a lock wait with a worker partly busy, or a sim/worker livelock. [inferred]
2. **The stutter profiler's JS hooks on the sim thread.** The `timed()` stage hooks, route-entry hooks, gc/publish hooks and the per-iteration ring reads all run JS on the sim thread, so every call takes the Frida JS lock, and the profiler's `setInterval` writer holds that lock too. A stall in Frida's runtime would look exactly like this run: the sim stuck inside an iteration while the main thread, which has only the frames CModule (no JS), keeps going. The task CModule needs no JS lock, but its `fieldFromName` NativeFunction is called from JS. It is the same profiler that passed about 45 minutes earlier, so this would be a rare race. [inferred]
3. **A managed endless loop on the sim thread.** Candidates are the ts3fixes `stuckcheckasync` / RouteBackoffHook path seen in the nativelog stack, or EA script code with legreplanskip off. Any script task that never yields freezes the ts3agent heartbeat. The final row's 33.56 ms max points against a loop that keeps running within completed iterations, but a loop inside one task fits it. [inferred]
4. **S3SS, S3PP or DXVK.** There is no evidence for or against. S3PP's tick or GC patches run on the sim thread. [unknown]
5. **The frames stage and service probes inside the main loop (incl. `0x59ED20`).** Unlikely. The main thread ran every hooked stage at 60 fps for the whole 61 s, and the sim cannot be waiting on a main thread that drains every frame. Not ruled out only in the sense that a corrupted shared structure could appear later. [verified that the main thread kept running; the "unlikely" verdict is inferred]
6. **Holders, F1, PlanDeadline, FailMemo.** Ruled out: none of them are in this build (§1). [verified]

## 5. Cheapest next step

1. **Read the sim thread's stack before the kill.** This one change separates causes 1, 2 and 3. `game-alive.ts` `watchGame` currently prints FAIL and exits. On `heartbeat frozen` it should first call ts3probe `/simstack?frames=24` (sim thread only, safe: `watchdog.ts` already does this on a stall) and `/threadstack?which=<route worker tid>`, then append both to the job log. If tooling allows, also write a full minidump (`procdump -ma`) to `probe-out/` before `taskkill`, which needs no game change. What each result means:
   - frida-agent frames on top: the profiler (cause 2);
   - ts3native.asi frames on top: our route path (cause 1);
   - interpreter frames from mono / TS3W with a managed loop: cause 3.
2. **Rerun bp-natural-2 exactly, twice** (same build and ini, legreplanskip off, fresh load). This is the "2 clean runs" rule. If it hangs again, the stack from step 1 names the owner.
3. **Only if it repeats without a stack:**
   - run the same job with `--no-prof` (no stutter profiler; frames session kept) to test cause 2;
   - then a run with `legreplanskip` on but the fresh-load ordering of bp-natural-2, to test whether the trigger is the switch or the restart.

   Turning the frames stage and service probes off is not the first test, because the main thread was healthy.
4. Optional: add the module name to ts3native's first-chance exception lines (§2), so the next boot-time exception names its DLL. Done for access violations in another module's code (§7).

## 6. Hang capture before the kill (built 2026-10-23, self-check only, not run)

§5 step 1 is now in the harness. When the game is hung but alive, `tools/harness/game-alive.ts` `captureHang` reads the probe **before** the FAIL line, so before the queue's `taskkill`. A hang means one of two things:
- `watchGame`: heartbeat frozen for 60 s, with TS3W.exe running and the probe answering. This covers profile-world, repro, tests-be, stuck-scan, verify-shortcut and save-matrix.
- `stallGuard`: "nothing moved" for 60 s, with the game and the probe up. This covers load, menu, save and travel waits.

When the process is gone or the probe is refused, nothing is captured.

What it writes to `TS3_HANG_DIR`, as `hang-HHMMSS-*`. `queue.ts` sets `TS3_HANG_DIR` per job to `probe-out/queue/<queue>-<date>-<job>-hang/`, which is git-ignored; outside the queue it is `probe-out/hang/`.

| file | content |
|---|---|
| `simstack.json` | `/simstack?frames=32` five times, 1 s apart, symbolized frames (`TS3W.exe!Name+0x..` or `module+0x..`). It stops after two failures in a row (dead probe). |
| `threadstack.json` | `/threadstack?which=all&frames=24` once: every non-sim game thread (route worker, job threads, main), one at a time |
| `native.json` | `/native`, all counters |
| `lockholders.json` | the `ts3native_lockholders` ring (decoded `HoRec`: waiter, lock, wait us, holder tid/role/site, waiter site), read through `/nativeexport` (which now returns the export address) plus one `/read`. Holders off: `enabled 0`. asi without Holders: no file, summary says `no export`. |
| `managedstack.json` | `/managedstack?frames=64`. The probe reads the interpreter frame chain from memory with the sim thread suspended, so it needs no sim tick. It is skipped when a `/simstack` call timed out, because that sample may still hold the sim thread, and a second sampler on it deadlocked the probe on 2026-10-13. |
| `.dmp` | Sysinternals `procdump -accepteula [-mp|-ma] TS3W.exe`. `TS3_HANG_DUMP=off|mini|mp|ma`, default `mini`: thread stacks and modules, a few MB. `mp`/`ma` can reach GBs. `TS3_PROCDUMP` = exe path. `TS3_HANG_DUMP_DIR` puts the dump elsewhere. It is skipped with a note when procdump is not installed (it is not in the repo or the probe), and it only runs when more than 2 s of the cap is left. |
| `summary.txt` | the one-line summary |

Limits:
- Each call has its own timeout (3-8 s), raced in the harness too.
- Everything sits under a 30 s total cap (`HANG_CAP_MS`), so a dead or hung probe delays the kill by at most ~30 s.

Summary line, printed by the child just before `FAIL: game died ...`:

`hang capture: 5 sim stacks, top frame ts3native.asi+0x11234 (5/5 same); 7 other thread stacks; native v99; holders on, 3 records; managed top ...; dump mini 4 MB; <dir>\hang-193110-*`

`queue.ts` copies the last such line from the job log, up to the dump part, into `row.hangCapture` and the md row's note.

Reading it against §4: `5/5 same` with a top frame in `ts3native.asi` means cause 1, and the holders ring plus the route worker's stack in `threadstack.json` name the other side of the lock. `frida-agent` on top means cause 2. Interpreter frames, plus a managed top that is the same across samples, mean cause 3.

Self-check (`node tools/harness/game-alive.ts --self-check`, with fake probes) covers:
- healthy fixtures, with all files and the summary;
- a refused probe, which stops after 2 sim samples;
- a probe that never answers, where the 400 ms cap holds and managed and dump are skipped;
- the ring decode, including a record mid-write.

The `queue.ts --self-check` covers `hangCaptureLine`. Ledger §281.

## 7. First-chance log: other modules once per address, with their name (2026-10-31)

**Problem [verified on a contributor's install with Apex Radiance: 95 archived session logs, 2026-10-29 to 10-31].** In 94 of the 95 sessions, all 16 slots were used before the world loaded: 14 handled access violations plus the two C++ throws (`0xE06D7363`) the logger allows. Every one of those AVs was inside `ApexRadiance.asi`. The process module list put it at `0x67810000`, and the AVs were at offsets `0xE0280`, `0xE02D8`, `0xE02E4`, `0xE99B7` and `0x3BE357`, one to three of them per session. The `0xE02xx` ones were reached from script host code (`0xD7D56D` on the stack). The remaining session had no AVs at all. The count of 14 is the cap at work, not Apex's real count.

The cost: the slots are gone before the world loads, so a later TS3W fault gets no line. In one session that crashed during world load (the issue #80 fault at `0x5DDFBD`), `ts3native.log` had no `exception` line for it. That also means no registers line, no `mono fault:` line (the script stack, which exists only in this logger) and no `P5cRelDump`. The `[CrashReport]` file still had the fault, registers and stack, so only the logger's extra lines were lost.

**Change (`[Crash] Log`, on by default, no switch of its own).**
- An access violation whose `eip` is in a loaded image other than TS3W.exe and ts3native is logged with `(name+0xoffset)` after the address, for example `exception C0000005 at 0x678F0280 (ApexRadiance.asi+0xE0280) ...`. The name comes from `VirtualQuery` and `K32GetMappedFileNameW`, with no loader lock.
- Up to 8 such addresses are remembered. A repeat at a remembered address takes no slot and is only counted. The minute counters line prints the total and the count per address when the total has moved, for example `crash log: 100294 more AVs at logged addresses in other modules (not logged): 678F02E4 x… 678F0280 x…`.
- TS3W faults, faults with `eip` outside any image (a wild call into the heap) and every other code are logged as before, up to the same 16.

Selftest: `TestCrashReport` checks the name for a kernel32 address, that a heap address is not named, that the second AV at an address takes no slot and is counted, and that a new address gets its line.

**In game [verified, 3 sessions on the same install, Apex Radiance 2.17.1, CI builds of this change, 15-20 minutes each].** Each named address got one line, and the remaining slots stayed free. The minute line showed what the old cap had hidden:

| Apex offset | Thread | Repeats | Read |
|---|---|---|---|
| `+0x14209B` | main | 20,000-25,000 per minute in one session, 52,000-63,000 in the next (589,312 in ~11 minutes), steady within a session | `[entry+0x2C]` with entry `0x1` or `0xB`: a 4×4 table of map-set pointers at `+0x16C` of a lighting object, one slot holding a small integer |
| `+0x3BE357` | main | ~300 per minute | CRT `memcpy` with source `0x8` |
| `+0xE0280`, `+0xE02D8`, `+0xE02E4`, `+0xE99B7` | sim | ~650 in all, during world load only | Night Lights' lot lighting lookups (`LotStoryManager`, `ReadCutExtras` in Apex's `features/level_light_share.cpp`) |

So the first lines of a session pointed at the sim thread, but the hot one is on the main thread. A handled AV costs about 2.7 µs in a 64-bit process with one handler (measured). Under WOW64 with several vectored handlers it costs more, so 350-1,000 per second is roughly 1-10 ms per second of main-thread time [inferred]. A report to Apex Radiance is drafted, not sent: their 2.18.x releases reworked the lamp code, so it needs a check on their current version first.

A script-stack line for the first AV on the sim thread was tried and dropped: at these load-time AVs there were no script frames to show.
