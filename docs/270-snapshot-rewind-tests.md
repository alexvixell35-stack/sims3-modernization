# 270. Snapshot / rewind for faster, identical-start test games

2026-11-15. Design input: Dalexlol's rewind notes of 2026-11-14 (GitHub alexvixell35-stack, "Faster, steadier tests: snapshot /
rewind instead of reloading"). The idea, the option list, the phase timings and the rewind design sketch are his; this doc adds
the read-only churn census (§3), the decision (§4) and the fallback (§6). Test-only, never for players.

Status: census tool built and run on one DXVK job [verified]. Bridge-arm census queued, not run. Rewind tool built and guarded
(§8), every guard proven on a toy process with its success and its refuse path [verified]; nothing of it has run on the game
yet (smoke queue §10, not run). No game was started by this lane.

Bridge (2026-11-15, §12-§13): the primary target is now the RTX Remix bridge arm, the stack we ship on. The bridge protocol is
read from source [verified, file:line in §12]; a rewind mode for the bridge (server and client, patch 0015 on Apex Radiance's
own bridge source, off by default) is built and compiles, and the tool speaks its protocol [verified on the toy with a fake
bridge client]. Nothing of it has run on the game: queue `rewind-bridge-1115`, not run.
Second version of the patch (same day, §12.3-§12.4, §13): a rewind is exact or refused, the parked client stops every
other sender, memory is limited, errors end in a defined state, and two offline tests in Apex's tree pass on system d3d9
and DXVK [verified]. The pull request text is ready (`tools/harness/rewind/bridge-patch/PR-BODY.md`); in-game runs of
this version are still to do.

## 1. Why

- A world load is ~12% of a 12-min test job (median 27 s load, 29 s launch, 8 s menu settle, ~15 s close + swap)
  [community-reported: Dalexlol's harness timestamps, 131 games]. An instant load saves at most ~1.5 min per job.
- The bigger win is an identical start state for every A/B game: Isla Paradiso in-load CV is 0.23 [community-reported, same source].

## 2. Options (from the notes)

1. Full process checkpoint/restore on Windows: no tool exists (CRIU, DMTCP are Linux only; libTAS needs software rendering and
   restores only within one process lifetime) [community-reported].
2. VM with a virtual GPU (VMware Workstation): snapshots work with 3D acceleration; no Vulkan in the guest, so no DXVK, bridge
   or Apex: screening only [community-reported].
3. In-process rewind: restore memory and thread contexts inside the same running process, so threads and handles stay alive.
   Feasible only where TS3W holds no GPU device of its own, i.e. under the RTX Remix bridge [inferred, Dalexlol].

## 3. Feasibility census (the notes' step 1)

Tool: `node tools/harness/rewind-census.ts snap|diff|watch|--self-check` (+ `tools/harness/rewind/RewindCensus.cs`, Add-Type in
64-bit PowerShell). Read-only: `PROCESS_QUERY_INFORMATION | PROCESS_VM_READ`, `THREAD_QUERY_INFORMATION`, Toolhelp,
VirtualQueryEx, ReadProcessMemory, NtQuerySystemInformation. Records threads (tid, Win32 start address, creation time), handles
with a per-type breakdown (SystemExtendedHandleInformation + ObjectTypesInformation type names; the self-check asserts every
type resolves and the listed count equals GetProcessHandleCount, so unlike the notes' census the breakdown is checked), the
region map, private / image / mapped totals, and FNV hashes of every committed writable 4 KB page. One TS3W snapshot takes
2.8-4.2 s for ~460k pages [verified].

### 3.1 Run: DXVK job, 2026-11-15 [verified]

Job `bp-l8-dry-rerun` of chain-reruns-1115 (Baseline-BP-Spring, 10 min profile, master-merge-1115 set). Snapshots at 18:33:11
PHT (~2.5 min after ready) and 18:40:35 PHT (442 s apart), plus a mid one at +36 s. NvRemixBridge.exe was not running: this
chain uses DXVK, so TS3W holds its own GPU device (nvoglv32.dll 9 threads, d3d9.dll 12 threads). The bridge-arm census is §4's queue.

| TS3W.exe | start | end (+442 s) | churn |
|---|---|---|---|
| Threads | 163 | 156 | 1 created (ntdll thread pool), 8 exited: 5 frida-agent (harness probe detach), combase, mswsock, 1 private start |
| Handles | 1,614 | 1,601 | 6 opened (File), 19 closed (Event 7, Thread 6, File 4, Timer 1, Section 1) |
| Private committed | 1,721 MB | 1,765 MB | 33 allocations made (incl. mapped below: 79 MB), 44 freed; commit +19 / -22 MB inside kept ones; 0 re-protected |
| Image | 430 MB | 413 MB | frida-agent.dll unloaded (17.6 MB, harness) |
| Mapped | 70 MB | 68 MB | 133 views mapped, 89 unmapped: unnamed (pagefile-backed) PAGE_READWRITE sections, ~55 MB live |
| Writable pages | 1,794 MB | 1,836 MB | 690 MB of 1.75 GB compared changed (39%); 481 MB (27%) already after 36 s |

Handle types at start: Event 513, EtwRegistration 257, File 240, Thread 173, Key 86, Section 71, Semaphore 70,
WaitCompletionPacket 62, Mutant 54, IoCompletion 32, ALPC Port 20, rest < 20. Thread starts: msvcr80 98 (EA/Mono
`_beginthreadex`), frida-agent 14, d3d9 12, ntdll 10, nvoglv32 9, ts3native 4 [verified].

Reading [inferred unless marked]:
- Threads and handles: low churn. The game's own thread set is nearly fixed over 7 min; the moves are a thread-pool worker
  and harness (Frida) threads. Not the "threads created and exited every job" blocker.
- Unnamed read-write mapped views: the main open problem. ~18 views/min come and go; a view unmapped after the snapshot has
  its section handle closed, so a rewind cannot re-map it from outside without duplicating the section handle at freeze time.
  Owner unknown (candidates: DXVK, Frida, EA streaming); the bridge-arm census tells whether it is the renderer.
- Memory: rewind writes back the whole ~1.8 GB writable set anyway; 39% dirty pages means no win from dirty-only copies.
- The DXVK arm cannot be rewound in process at all (GPU device in TS3W) [verified: nvoglv32 + d3d9 threads present].

## 4. Decision and queue

Rodney 2026-11-15: in-process rewind first, VM only as fallback with the exact blocker. The census shows no hard blocker for
threads or handles; the open items are the mapped-view churn and the missing bridge-arm numbers. Next measurement, RUN FIRST
after chain-reruns-1115:

- Queue `tools/harness/queues/rewind-census-bridge-1115.json` (IP, 10 min, Apex + bridge Bin state, master-merge-1115 set;
  dry-run ok). Chain `probe-out/queue/chain-rewind-census-1115.sh` (turbo-compat snapshot / state apex / restore, starts
  `rewind-census.ts watch` before the job). Not started.
- Judge: TS3W and NvRemixBridge thread / handle / allocation churn; whether the unnamed RW views remain without DXVK.

Changes that would let one world load serve many A/B games (describe only; queue.ts is not changed during the chain):
- queue.ts: a job kind `run.rewindGames: { n, profile }` that keeps the game up after the first `ready`, and per game: snapshot
  once, then per arm rewind, re-read switches (ini reload where the switch is live, else arms must be load-time-equal), run the
  profile window, collect counters; row per game with the shared load.
- profile-world.ts: a `--no-load` mode that starts from "in world" (skip cycle.ts --save, keep seed_rng and test_env), and
  agent reconnect after each rewind (the agent socket state is rewound with memory; the notes list it as a risk).
- Sound off for these jobs (DirectSound + WASAPI threads hold device state the rewind does not restore) [inferred].

## 5. Rewind spike: first lane (history)

The first census lane did not build the spike. It was built later the same day (commit 87baa5e7) and is now §8. The design stays
Dalexlol's sketch (notes, "Rewind design sketch"); the census added what it must handle: thread-pool threads coming and going,
the unnamed RW view churn (duplicate section handles at freeze), Frida detach (run without the probe agent), and the WOW64
per-thread 64-bit state.

Prior art in this repo: `native/worldimage` (docs/123 §8) already suspends TS3W at a cut and dumps private memory + writable
sections for the Mono world image (capture only, into another process) [verified: worldimage.cpp header].

### 5.1 Bridge-server rewind mode (first sketch; built 2026-11-15, see §12)

The bridge is Apex Radiance's; this was the sketch of a patch to propose with proof, built off by default, never shipped by us:
while a snapshot exists, resource frees are tombstoned, the first Lock/Update of a resource after the snapshot keeps a copy of
the old contents (copy on write); on rewind the server revives tombstoned resources, drops resources created after the
snapshot and puts the old contents back, then acks the client [inferred, Dalexlol's sketch]. Proof needed first: the bridge-arm
census (§4) and a working client-side rewind on a toy.

## 6. Fallback: VM screening route

Only if the bridge census or the spike hits a hard blocker (name it here when it does). Facts (Rodney, 2026-11-15): VMware
Workstation 26.0.0 installed, `vmrun` works, the VM would live on D:, Hyper-V and VBS are active on the host (Workstation then
runs on the Windows hypervisor platform: slower, [inferred] 3D acceleration may be limited), no Windows guest yet.
Setup: Windows evaluation guest on D:, Steam + TS3 + our pair, plain D3D9 (no DXVK, bridge or Apex in the guest
[community-reported]), take a VM snapshot in world after load, `vmrun revertToSnapshot` per game. Screening only; final runs
stay on the bridge copy.

## 7. Decisions for Rodney (answered 2026-11-15 unless marked open)

1. Run order: `rewind-smoke-1115` (§10) after chain-mono-1115, via `probe-out/queue/chain-rewind-smoke-1115.sh`. Keep the experimental override job, run it LAST (queue order plan, plan-probe, strict, override). Done.
2. Sound: accept `--allow-audio` for the smoke (all four jobs pass it). Then find a real sound-off start: OPEN, see 2a.
3. Probe detach/attach (§9.2): build AFTER the smoke run, as the next task (PLAN.md). Not built now.
4. `--reap-workers`: allowed in all smoke jobs.
5. `--recreate-views`: allowed in all smoke jobs (risk: it cannot be shown from outside that nothing else shares that memory, §8.1).
6. Pinning files (§11): not asked; stays accepted for test windows without saving.
7. ts3native `[Rewind]` hook (§9.1): not now. Build only if the smoke's refusal reasons show it is needed.
8. Under the stock RTX Remix bridge the renderer process is not rewound; the tool refuses (shared views). The bridge rewind mode of §12 is built for it (patch 0015, not yet run in game).

Open after the bridge work (2026-11-15):

9. Restart `probe-out/queue/chain-rewind-smoke-1115.sh`: it was edited while a copy waited. Its first 14 lines are unchanged byte for byte, so a waiting bash should read the new tail, but that is not proven for this shell; a restart is certain.
10. Answered (Rodney 2026-11-15, replaces decision 1's override part): the `override` job is dropped. Every rewind job (census, smoke `plan` / `plan-probe` / `strict`, and `rewind-bridge-1115`) runs on the patched bridge copy, Bin state `apex-rewind`, not on the stock bridge; `bp-rewind-refuse-dxvk` stays on plain DXVK. The smoke and bridge jobs pass `--renderer bridge-rewind` and fail at once unless `NvRemixBridge.exe` runs and `Bin\bridge.conf` has `rewindMode = True`. The re-baseline on stock Apex 4.0.1 is another lane and is not touched. Open: the chain script still sets Bin state `apex` for its census and smoke steps and still names `bp-rewind-override` in `--only` (queue.ts then stops with "--only: no job" and that step runs nothing). Its owner has to change those two `arm` lines to `apex-rewind` and drop the id; this lane was told not to edit the script again.
11. PR to Apex (§13): commit subject area `tools:` (their list has no bridge area), the flag is a `bridge.conf` option `rewindMode` instead of an environment variable (their idiom), and the commit ends with `Assisted with AI` (their public text policy forbids naming tools; this names none). Change any of the three before the PR is opened.
12. The build needed meson and ninja, which were missing: they are installed from PyPI into a venv under `game-copy/rewind-bridge-1115/venv` only (nothing system-wide).
13. Render target contents are not restored by the server (§12.4). If the plan job shows this matters (frames differ after a rewind), the next step is a readback of the targets written since the snapshot.

Flags per job: all jobs `--allow-game --no-coop --allow-module frida-agent --allow-audio --reap-workers --recreate-views`; plan jobs add `--plan-only` (plan also `--no-view-probe`); override adds `--allow-shared-views --allow-gui`. strict therefore keeps only the shared-view and window guards.

### 2a. Sound-off research (open item, static only, 2026-11-15)
- Known: `ts3probe.py ensure_audio_off` sets voice/soundfx/ambient/music level 0 and mute 1 in Options.ini; devices and dsound/AudioSes/MMDevAPI stay loaded (§8.6). A new `lastdevice` resets Options.ini (docs/236 round 4).
- Web search (EA forums, Steam): no documented Options.ini key or launch argument that disables the audio device [community-reported: none found].
- Static string scan of `game-copy/bin/TS3W.exe` (ASCII): a bare string `nosound` exists, plus `OverrideAudio` and `XInputGetDSoundAudioDeviceGuids`. `nosound` is a candidate launch argument (`-nosound`) [inferred: string only, not proven an argument; xref it in Ghidra and find the reader]. No `-noaudio` or Options.ini device key found.
- Next (not done): xref `nosound` and `OverrideAudio` in Ghidra on a game copy; if a command-line reader, test `-nosound` in one short game job and check that dsound/AudioSes are not mapped (rewind-census module list). Fallbacks: disable the playback device for the test user (Windows, outside the game) or a stub dsound.dll in the game-copy Bin.

## 8. The rewind tool and its guards (2026-11-15) [verified on the toy unless marked]

Files: `tools/harness/rewind/rewind-spike.cpp` (x64 host), `rewind-selftest.inl` (its toy selftest), `rewind-toy.cpp` +
`rewind-toy.h` (x86 toy and the shared cooperation block), `build.cmd` (builds both, runs the selftest; `build.cmd <case>` runs
one case). `node tools/harness/rewind-smoke.ts --self-check` does the same from `tools/selftest-all.ts` and skips without MSVC.
Guard: only `rewind-toy.exe`, or `TS3W.exe` with `--allow-game`; 32-bit targets only. Test-only, never for players.

Commands: `selftest <toy> [case]`, `cycle <pid> <runMs> <n> [options]`, `serve <pid> [options]` (stdin: `snapshot`, `rewind`,
`plan`, `ticks <n>`, `quit`; one `result {json}` line each with code, reasons, ms per phase, counts).

Rule of the tool: a rewind is planned first with all threads suspended. Every reason to refuse is found in the plan, before
anything is changed: exit code 2, threads resumed, the target plays on, the harness reloads the world. After the first change a
failure cannot be undone, so the tool ends the target (code 3): a half-rewound process never runs on. After a rewind that went
through, the tool checks it before it resumes anything (§8.7).

Snapshot content: region map below 4 GB; every committed page (also read-only, no-access and guard pages, read with a
protection change that is put back), except the kernel's shared page at 0x7FFE0000 and views mapped without write permission;
kept in a delete-on-close temp file mapped into the host (not in host RAM). Thread contexts (WOW64, all parts), suspend counts,
priorities. All handles duplicated into the host. States of events, semaphores and mutexes. File positions.

### 8.1 Mapped views

- No read-only call links a view to its section. Measured on Windows 11 26300: `QueryWorkingSetEx` reports share count 7 for
  every section page, mapped once or three times, and no page priority, so it cannot serve as a proof [verified, experiment].
- So, with the target suspended: a file-backed view is matched to a section handle of the same file whose bytes are equal at
  exactly one 64 KB step. A pagefile-backed writable view gets its last byte flipped and put back; the one (section, offset)
  whose byte follows both times is the same memory. `--no-view-probe` skips this one write; such views then stay unpinned.
  Risk of the probe [inferred]: if another process writes that same byte within the few microseconds, its write is lost.
- Rewind: views made after the snapshot are unmapped (modules loaded after it too). A pinned view that is gone, or sits at the
  same base on other memory (probed again), is mapped again from the pinned section at the same base and offset, then its bytes
  are restored. Toy: `views` (2 views of one section incl. one at offset 64 KB unmapped; section handle closed too; another
  section mapped at the same base).
- Refused: a view (or module) unmapped after the snapshot that has no section handle. Toy: `views`, "no section handle is left".
  `--recreate-views` maps a fresh pagefile section with the snapshot bytes instead (toy: same case); this is right only when no
  one else maps that memory, which cannot be shown from outside [inferred]. Also refused: pages committed inside a kept view.
- Refused at the snapshot: a writable pinned view whose section has a name or more handles than the target's own (shared with
  another process: IPC, a renderer bridge; the other side is not rewound). `--allow-shared-views` overrides. Toy: `refusals`
  (named section; and the real sound stack, which shares a section with the audio service). Not seen by this check: a section
  another process mapped and then closed its handle to [inferred].
- A kept view without a pin is counted (`viewsUnverified`): its bytes are restored, its identity is not checked.

### 8.2 Threads in kernel waits

A thread is "waiting" when its 64-bit side stands just behind a `syscall` instruction; the 32-bit stub it came through gives the
system call by name (export tables of the 32-bit ntdll and win32u). Stub shape `mov eax / mov edx / call edx` with the saved eip
behind the call [verified on the toy, Windows 11 26300].

- Snapshot is taken only when every thread is running 32-bit code or sits in a call that only waits (NtWaitForSingleObject,
  NtWaitForMultipleObjects, NtDelayExecution, NtRemoveIoCompletion(Ex), NtWaitForWorkViaWorkerFactory, NtWaitForAlertByThreadId,
  NtWaitForKeyedEvent, NtUserGetMessage, NtUserWaitMessage, NtUserMsgWaitForMultipleObjectsEx), or is parked in a wait for
  another process (§14). The tool resumes, waits a few ms and tries again up to 40 times, then refuses. Live upper vector
  registers no longer refuse: they are saved and put back (§14.4).
- Rewind, per thread: same wait as at the snapshot (eip, esp, ebp, ebx, esi, edi and 11 stack words equal): left alone
  (`threadsLeft`). Running now: context written back; if it was waiting at the snapshot, eip is set to the start of the stub,
  so it calls the same wait again (`threadsRestarted`). In a Sleep of at most 1 s now: context written, it takes effect when
  the Sleep returns (`threadsSetLate`). Toy: `waits` (all three; the restarted waiter is signalled afterwards and wakes).
- Refused: a thread now in any other call (toy: a second infinite wait, "at the snapshot it was"); a thread at another
  user-callback depth than at the snapshot, read from WOW64 TLS slot 5 of the 64-bit TEB (toy: window thread inside a
  SendNotifyMessage callback) [slot meaning inferred from WOW64, behaviour verified on the toy].
- Suspend counts and priorities are put back. Kernel object states: events and semaphores are set back to the snapshot state;
  a mutex whose owner changed is refused (ownership cannot be given from outside). Toy: `memory`.
- Known gap [inferred]: a wait that had just completed but not yet returned at the snapshot instant looks like a pending wait;
  a restart then waits again and that one wake-up is lost. The quiesce hook (§9.1) makes the window very small.
- Restarted relative timeouts start over (a 5 s wait waits 5 s again).

### 8.3 Threads created or exited

- A thread that exited after the snapshot: refused (it cannot be made again). Toy: `threads`.
- A thread made after the snapshot: refused. Toy: `threads`.
- Pool workers made after the snapshot, idle in NtWaitForWorkViaWorkerFactory with a start address in ntdll and owning no
  mutex: refused by default; `--reap-workers` ends them. Why that is safe [inferred, toy-proven]: all their user-mode state
  (stack, TEB, pool bookkeeping) is thrown away by the rewind anyway, they hold no kernel object, and the kernel removes them
  from the worker factory at exit. Toy: 8 workers reaped, the pool runs new work after it. The kernel frees their stacks itself
  [verified: the tool's own free then found the range gone].
- The Frida probe: a snapshot or rewind is refused while `frida-agent*.dll` is loaded (its threads and its session with the
  host would be rewound under it). `--allow-module frida-agent` overrides for the smoke. Toy: `refusals` (module guard).

### 8.4 Handles

- Snapshot: every handle is duplicated into the host (the objects stay alive). Kinds that cannot be duplicated (ETW
  registrations: 38 of the toy's 135 handles) are recorded by value, type and access only.
- Rewind: handles opened after the snapshot are closed in the target. Handles closed after it are put back at the same value:
  the host duplicates the pinned object into the target until the wanted value comes up and closes the rest (works because the
  value is free again) [verified on the toy, also when the value was reused by another object]. Each kept handle is checked to
  be the same object (`CompareObjectHandles`).
- Refused: a closed handle of a kind that cannot be duplicated (toy: `files`, EventUnregister); a new handle that is protected
  from close (seen when a thread turns into a window-system thread).
- File positions: recorded for disk files and put back for handles without write access. Queries run on a helper thread with a
  300 ms limit and `CancelSynchronousIo`, because a query on a synchronous handle with a read pending in a suspended thread
  would wait forever. Toy: `files`.

### 8.5 Time

The cooperation block (§9.1) carries `skewQpc`. The host sets it so that the target's clock (real time minus skew) continues
from the snapshot instant after a snapshot freeze and after a rewind. Toy: with the fix 0 clock jumps over 300 ms across 3
rewinds; with `--no-time-fix` the toy sees the jump (`refusals`, last case). For the game this needs the ts3native clock hooks
(§9.1, design only). Until then: rewind while paused, unpause after; what a real-time jump of minutes does to the paused game
is not measured.

### 8.6 Sound, window system, other state outside the process memory

- Sound: refused when a thread starts in dsound, AudioSes, MMDevAPI, wdmaud, XAudio or OpenAL, or the cooperation block reports
  an open device. `--allow-audio` overrides. Toy: `refusals` (real `waveOutOpen`, and the flag). Game fact: `ts3probe.py`
  `ensure_audio_off` only mutes (levels 0, mute 1); dsound.dll, AudioSes.dll and MMDevAPI.dll were mapped in the 11-15 census
  snapshot [verified: read]. Whether sound threads exist then is what smoke job 1 reports.
- Window system: refused when the GDI or USER object count differs from the snapshot (`--allow-gui` overrides). Toy: `waits`.
- Write-watch allocations: since §15 kept, every page made dirty again after a rewind (a superset for a Boehm-style GC); an unknown importer of GetWriteWatch refuses. Toy: `writewatch`.

### 8.7 Verification after each rewind (before any thread is resumed)

Region map equal to the snapshot's (state, protection, type, allocation; a written copy-on-write page counts as read-write);
every kept byte read back and compared, plus a 64-bit hash equal to the snapshot's (`hashEqual`); handle set, object
identities, file positions, event / semaphore / mutex states; thread count and the contexts that were written. A mismatch ends
the target (code 3). Determinism: `ticks <n>` rewinds and lets the target run exactly n ticks through the cooperation block
(`parkAtTick`); the toy gives the same counter, RNG state, allocation count and block addresses in two runs (`basic`). For the
game the same check is: same seed (`seed_rng`), same tick count, equal profile rows; `rng-compare.ts` today compares run
metrics (pick overlap, variance), not state hashes, so an agent `state_hash` command would have to be added [inferred].

### 8.8 Hardening

- Refusals and normal ends resume the threads. A watchdog child process holds a handle to the host: if the host dies with the
  target only suspended it resumes exactly the threads the host had suspended; if the host dies mid-rewind it ends the target.
  Toy: `watchdog` (both).
- Timing per phase in every result line. Toy (18 MB, 9 threads): snapshot freeze 5 ms, copy 18-40 ms; rewind memory 7-9 ms,
  verify 11-16 ms. Game estimate [inferred]: about 2.3 GB to copy once and to read twice per rewind, a few seconds.
- Toy selftest: 8 cases, 87 checks, about 20 s; run 6 times in a row without a failure.

## 9. Design, not built

### 9.1 ts3native `[Rewind]` cooperation hook (default off)

Protocol, as the toy implements it (`rewind-toy.h` `RewindCoop`, named mapping `Local\rewind-coop-<pid>`): the host sets
`request`; the target parks at the next tick boundary and sets `parked`; the host refuses if that does not happen within
`--quiesce-ms` (toy: `refusals`, "did not park"), or if there is no block ("no cooperation block"). `parkAtTick` parks after
an exact tick count; `skewQpc` is the clock offset; `audioOn` reports an open sound device. For TS3W the tool requires the
block unless `--no-coop` is given (the smoke uses `--no-coop` and pauses through the agent).

ts3native side: `[Rewind] Enabled=0`, read with `GetPrivateProfileIntW` like `[StallWatch]`. Only with 1: create the mapping
and add one callback with `SimPumpAdd` (`native/ts3native/simloop.cpp`, once per frame on the sim thread before the frame
limiter; it is installed only by its first caller, so the default path is untouched) [verified: read]. The callback parks in
a `Sleep(1)` loop while `request` is set. Still to find before `parked` may mean "render and streaming idle": the render
thread's frame boundary and the streaming queue depth [inferred, not located]. Clock: ts3native has no hook on timeGetTime,
QueryPerformanceCounter or GetTickCount today [verified: read]; the hooks would subtract `skewQpc`. Estimate: park only about
1 h of agent work plus one game check; clock hooks and the idle checks need static work first. It needs a game run to prove,
so it was not built in this lane.

### 9.2 Harness changes (queue.ts is not touched while a chain runs)

- ts3probe.py: `detach` and `attach` endpoints (the session is dropped and made again; `agent.js` `findMailbox` already
  re-scans for the mailbox). The agent's mailbox is a managed byte[] in game memory, so a rewind puts it back to the snapshot
  state: the harness must have no command in flight at the snapshot and must re-read the mailbox after a rewind.
- queue.ts job kind `run.rewindGames: { n, profile }`: after `ready`: pause, detach the probe, `rewind-spike serve` `snapshot`;
  per game: attach, set the arm's switches, `seed_rng`, unpause, profile window, pause, collect counters, detach, `rewind` (or
  `ticks <n>` once the hook exists); on code 2 or 3 fall back to a normal restart and reload, and mark the row.
- profile-world.ts `--no-load` (start from "in world"), as in §4.
- ts3native.log and other files the game appends to are not rolled back: rows must be read per game before the next rewind.
- Built 2026-11-16: queue job kind `run.rewindGames: { n, profile, ab? }` (`tools/harness/queue.ts`, `rewindArms`). The probe's
  `/fridadetach` and `/fridaattach` were already there (§24), and profile-world.ts already starts from "in world", so no
  `--no-load` was needed. Each game is one profile-world.ts window with `--no-prof --no-samplers --no-frames` (no Frida
  session of its own across a rewind); `ab` switches go through `--fixes-on` / `--fixes-off` in ABBA order and are read back;
  counters (`<tag>-counters.json`) and the ts3native.log slice are read per game before the next rewind; a rewind with code 2
  or 3 reloads the world, takes a new snapshot and marks the game row `reloaded`. Smoke queue
  `tools/harness/queues/rewind-games-smoke-1116.json` (dry-run ok).
- First smoke run (2026-11-16, Dalexlol's PC, Apex 4.0.1 + the tip pair d3d9 926303ee / NvRemixBridge 749d75b8, Baseline-BP-Spring)
  [verified: job log, bridge64.log]: snapshots code 0 (copy 830-1129 ms, park 147-181 ms); game 1 (off) played 2 min; its rewind
  came back code 2 ("the bridge server refused op 3, result 80004005"), so the job reloaded as designed (~6 min), took a new
  snapshot and ran game 2. The bridge server gave that second snapshot up 10 s after it was taken: "a 4096x1 surface of format
  116 written after the snapshot could not be read first" (format 116 = A32B32G32R32F; a float lookup texture redrawn every
  few seconds). Stopped by hand before game 3. Open: which renderer pass writes that surface, and whether the server can copy
  such a surface (GetRenderTargetData / StretchRect) instead of giving the snapshot up.

## 10. Game smoke queue (not run)

Order and flags: see §7. Chain: `probe-out/queue/chain-rewind-smoke-1115.sh` (waits for chain-mono-1115, builds the tool, then runs census, smoke and `rewind-bridge-1115` with a Bin state per step, §12.6). Next task after the smoke: probe detach/attach (§9.2).

Renderer per job (2026-11-15). Before: `rewind-smoke-1115` ran on the Bin state the chain found, which is plain DXVK (`d3d9.dll` 7.9 MB in Bin, no `.trex`, no `ApexRadiance.asi`) [verified: read]; `rewind-census-bridge-1115` needed the Apex bridge state from its own chain, which was never started. After: jobs 1-4 of the smoke run on the bridge arm (Bin state `apex`: latest Apex Radiance release, `NvRemixBridge.exe` running; each job passes `--renderer bridge`, which fails the job at once on the wrong state, and reads the Apex compat line back). A fifth job `bp-rewind-refuse-dxvk` runs on plain DXVK (`--renderer inproc`) and passes only when the snapshot is refused with "the GPU device sits in this process" (new guard: a Vulkan loader or driver module in the target, `--allow-gpu` overrides; toy: `refusals`). The census runs first, on the same bridge state. Changed the same day (§7 item 10): the bridge state for all of them is the patched copy, `apex-rewind`, with `--renderer bridge-rewind`, and the `override` job is gone. `strict` keeps `--no-coop`, so on this arm it is still expected to refuse at the bridge's channel views; the rewind the server follows is `bp-rewind-bridge-apply`.

Removed 2026-11-15 (Rodney, §14.5): `bp-rewind-refuse-dxvk`; every game job runs on Apex + the RTX Remix bridge.

`tools/harness/queues/rewind-smoke-1115.json`, dry-run ok, RUN FIRST after chain-reruns-1115:
`node tools/harness/queue.ts tools/harness/queues/rewind-smoke-1115.json --no-smoke` (build first:
`tools\harness\rewind\build.cmd`). Four `run.script` jobs of `tools/harness/rewind-smoke.ts` on Baseline-BP-Spring, each
about 6 min, stop at the first FAIL: (1) `plan`: nothing written into the game, lists the work and every reason to refuse;
(2) `plan-probe`: plus the one-byte view probe, gives pinned vs unpinned views; (3) `strict`: real snapshot and rewind with
every guard on, a refusal is the expected result; (4) `override`: sound, shared-view, window, worker and unpinned-view guards
off, experimental. Rows: `probe-out/rewind/smoke-1115-<mode>.jsonl`. Results go here.

## 11. Still unguarded

- The renderer: GPU device state in TS3W (DXVK arm: refused by the GPU module guard, §10), or the bridge server process (stock bridge: refused through its shared views; patched bridge: §12, exact or refused since the second version, with the limits left in §12.4: cursor commands, DXVK's own state).
- Files on disk written after the snapshot (logs, caches) are not rolled back; positions of writable handles are left alone.
- Pinned handles keep files, sockets and pipes open in the host while the game thinks it closed them.
- Timers, I/O completion queues, pending APCs and pending overlapped I/O are not captured.
- Window-system objects are only counted, not compared.
- Sockets: the kernel side of a connection is not rewound.
- Thread affinity, and memory above 4 GB (the 64-bit side of WOW64) are left as they are.
- A wait that completed at the exact snapshot instant (§8.2), and a second process that mapped a section and closed its handle (§8.1).

## 12. Rewind under the bridge (2026-11-15)

Source read: Apex Radiance's bridge is public in its repo (`loinyx/Sims3-ApexRadiance`, `native/remix-bridge/`, release 4.0.1):
NVIDIA's `dxvk-remix` at commit f35b86a4, folder `bridge/` (MIT), plus Apex's patches 0001-0014. The older
`NVIDIAGameWorks/bridge-remix` repo is deprecated (its README points to `dxvk-remix/bridge`) and was used for orientation only.
File:line below is `bridge/src/...` of that tree with the 14 patches applied. No bridge code is copied here.

### 12.1 How client and server talk [verified]

- Two processes: the client is `d3d9.dll` in TS3W (32-bit), the server is `.trex\NvRemixBridge.exe` (64-bit) with the real
  D3D9 device (DXVK x64, `server.useVanillaDxvk`). The client starts the server with a GUID on the command line
  (`client/d3d9_lss.cpp:303-316`, `server/main.cpp:3703`); every shared object's name ends in that GUID (`util/util_guid.h:64-75`).
- Two bridges, "Device" and "Module" (`util/util_devicecommand.h:30-35`, `util/util_modulecommand.h:29-33`), each with a
  channel per direction, `Client2Server` and `Server2Client` (`util/util_bridgecommand.h:368-372`). The server runs device
  commands on its main thread and module commands on a second thread (`server/main.cpp:3802-3806`).
- One channel (`util/util_ipcchannel.h:47-73`) is one named read-write file mapping `<name>Channel_<guid>`
  (`util/util_sharedmemory.cpp:33-56`) that holds: two 64-bit counters for overwrite protection (`serverDataPos`,
  `clientDataExpectedPos`, lines 54-55 and 109-112), the command queue, and the data ring; plus a named semaphore and two
  named wake events. Defaults 96 MB client channel, 32 MB server channel (`util/config/global_options.h:311-316`); Apex sets
  the server channel to 72 MB.
- Command queue (`util/util_atomiccircularqueue.h`): a ring of 16-byte headers `{command, flags, dataOffset, pHandle}`
  (`util/util_commands.h:931-936`). Its two indices and two "sleeping" flags live in the shared memory at offsets 0, 128,
  256 and 384 (lines 66-70); empty means the indices are equal (line 185). So "both queues empty" can be read from outside.
- Data ring (`util/util_circularbuffer.h`, `util/util_circularqueue.h`): 32-bit words in the same mapping. **Its read and write
  positions are private members of each process** (`m_pos`, `m_absPos`, `util_circularqueue.h:51-54`), not shared. A
  command is: the client locks the channel, pushes a UID and the arguments into the data ring, then pushes the header, whose
  `dataOffset` is the client's ring position after the data (`util/util_bridgecommand.cpp:277-283`, 354-436). The server pops
  the header and pulls the same words at its own position (`server/main.cpp:325-355`); in release builds a mismatch with
  `dataOffset` only logs "Data not in sync" (`main.cpp:102`, 3379-3381). After each command the server publishes its read
  position (`main.cpp:3384-3385`).
- Answers: `Bridge_Response` with the UID in `pHandle`; the client waits for it (`util_bridgecommand.h:36-43`,
  `util_bridgecommand.cpp:163-266`) and pops it after reading the data (`consumeResponse`, lines 122-126).
- Timeouts: a side that gets no command for `commandTimeout` x `commandRetries` = 1 s x 300 gives up
  (`global_options.h:378-380`, `util_bridgecommand.cpp:173-174`, 262-264). A freeze of TS3W for a snapshot (seconds) is far below it.
- The shared heap (`useSharedHeap`) is off by default (`global_options.h:436`) and not set by Apex; not covered here.

### 12.2 Resource names across the boundary [verified]

- Every client object gets an id from one counter that starts at 1 (`client/base.h:305-310`, 363; `client/d3d9_lss.cpp:68`).
  The id is the header's `pHandle`. The counter is client memory: **after a rewind the client hands out the same ids again.**
- The server keeps one map per kind from id to the real object: devices, resources (textures, buffers, surfaces), volumes,
  vertex declarations, state blocks, vertex and pixel shaders, swap chains, queries (`server/main.cpp:157-165`). It keeps
  nothing else per id: no contents, no state. A destroy releases the object until its count is 0 and erases the id
  (`main.cpp:185-199`, e.g. 2069-2076); `Bridge_UnlinkResource` erases an id without a release (texture levels, `main.cpp:2957-2968`).
- Contents reach the server on Unlock: the server locks the real buffer or surface and copies the bytes in
  (`main.cpp:2448-2485` vertex buffer, 2656-2700 surface). The client keeps its own CPU copies (Apex patches 0003 / 0008 free
  idle ones and fetch them back from the server).
- What a client-only rewind therefore breaks [verified from the above]: the server's data ring positions (both directions,
  both bridges) no longer match the client's; ids made after the snapshot are reused for new objects; objects destroyed
  after the snapshot are gone on the server while the client uses them again; contents uploaded after the snapshot stay;
  the device state on the server is the late one while the client's record of what it sent is the old one.

### 12.3 Where both sides are quiet [verified unless marked]

- `Present` is not a quiet point by itself: the client sends the Present command and then only waits for a credit of the
  present semaphore, which lets it run `presentSemaphoreMaxFrames` (1) frames ahead (`client/d3d9_swapchain.cpp:153-198`,
  `client/d3d9_device.cpp:467-496`, `global_options.h:409-410`; server side `main.cpp:745-775`).
- A quiet point is made with one synchronous command at the end of a Present: when its answer has been popped, the server
  has run every earlier command (the queue is first in, first out) and both command queues are empty. The swap chain's
  Present holds the present mutex there (`d3d9_swapchain.cpp:158`).
- One sender thread is **not** given by the bridge [verified in the source, 2026-11-15; lines of the tree with patch 0015]:
  - Client: the only thing that serializes senders is the writer channel's mutex (`util/util_ipcchannel.h:119`), taken per
    command (`util/util_bridgecommand.cpp:377`) and per state setter (`:447`). The device lock exists only on a thread-safe
    device (`client/d3d9_module.cpp:639-643`, `util/util_scopedlock.h:106-113`), and `Present` does not hold it: the device's
    Present takes it only inside two short blocks and has left it when it calls the swap chain's
    (`client/d3d9_device.cpp:524-550`), which takes only the present mutex (`client/d3d9_swapchain.cpp:276`). Calls on
    `IDirect3D9` go over the module bridge with no device lock at all (`client/d3d9_module.cpp:114-382`).
  - The client DLL itself starts no thread that sends commands (no thread is created under `client/`; the server-exit
    callback, `util/util_process.cpp:94`, only sets state; window messages go over the message channel, not the rings).
  - Server: device commands run on the main thread, module commands on a second one (`server/main.cpp:3901-3905`,
    `server/module_processing.cpp:76-80`).
  - So whether one thread sends is a property of the game, which the bridge cannot prove. Patch 0015 no longer relies on
    it: the parked client holds both writer mutexes (`client/d3d9_swapchain.cpp:191-192`), checks that nothing else was
    sent (`:237`, `isQuiet` at `:159`), and the server holds its command loop after a rewind until the restored client
    answers (`server/main.cpp:224`, client `:254`) (12.4). Blocking is proven offline by `test_rewind_device` (a second
    thread's command waits for the whole park); the refusal after an interleaved send and the hold are not.

### 12.4 The coordinated freeze (built: patch 0015, off unless `rewindMode = True` in both `bridge.conf`)

Second version (2026-11-15, Apex branch commits 2 to 4): a rewind is exact or refused. The first version (commit 1, the build the
`rewind-bridge-1115` queue ran first) left render target contents alone and only counted what it could not undo.

Parts: `util/util_rewind.h` (shared: ops, answers, cooperation block, the "done" event, since §28 no other event), `server/rewind_mode.h` (the server
logic, a header so a test can run it), a park function at the end of the client's swap chain Present, one new command
`Bridge_Rewind` at the end of the command list (older numbers unchanged), `set_pos` on the data ring, two options in
`GlobalOptions` (`rewindMode`, `rewindMaxKeptMB`), and one-line hooks in `server/main.cpp`: the destroy helper, the
dispatch, the loop tail, the buffer and surface uploads, `UpdateSurface`, `UpdateTexture`, `StretchRect`, `ColorFill`,
`GetRenderTargetData`, `GetFrontBufferData`, `SetLOD`, `SetAutoGenFilterType`, the volume uploads and the query
`Issue` / `GetData`.

1. **Park.** The bridge client publishes our cooperation block (`Local\rewind-coop-<pid>`, §9.1; layout unchanged: `op`,
   `bridge`, `bridgeResult`, eight numbers). The tool sets `request`; the client stops at the end of the next Present and
   takes the writer mutex of the module bridge and of the device bridge, so every other sender blocks at its next command.
   For each `op` the tool writes (1 snapshot, 2 rewind, 3 plan, 4 drop) the client checks that the module bridge is quiet,
   lets go of the device mutex, sends `Bridge_Rewind`, waits for the answer and for the server's "done" event, takes the
   mutex again and checks that no other thread sent in between (the command counter is the op's own + 1, both command
   queues are empty, the server's published read position equals the client's write position). If not: the op is refused
   (`E_FAIL`), and after a snapshot the client sends op 4 so the server lets its snapshot go. Then it stores the result and
   clears `op`.
2. **Snapshot (op 1).** Server: copies its nine id maps and the set of live objects; per device a state block
   (`D3DSBT_ALL`), the bound render targets and depth surface, the gamma ramp and the software vertex processing flag;
   copies every render target and depth surface alive (standalone ones, the levels and faces of render target textures,
   back buffers, the bound ones) into a second surface of the same kind with `StretchRect`; waits up to 0.5 s for the
   results of the queries in flight and keeps them; and after the command records the data ring positions of both
   bridges. A scene left open is closed for the copies and opened again. Not copied, because D3D9 leaves both undefined
   after a Present: the back buffers of a swap chain with `D3DSWAPEFFECT_DISCARD`, and the depth surfaces made with
   Discard (2a). Then the tool freezes and snapshots TS3W as before.

   **2a. Depth surfaces made with Discard (commit 4, after the first run in game).** First in-game snapshot of commit
   3's pair (`bp-rewind-bridge-plan`, 2026-11-15 22:27 PHT, Apex 4.0.1): op 1 refused, `a 1024x768 depth surface of format
   75 could not be copied (0x8876086c)` = D24S8, `D3DERR_INVALIDCALL` [verified: `bridge64.log`]. Cause [verified]:
   - TS3 makes its depth surfaces with Discard = TRUE: `Surface_CreateRTorDS 0x617C50` pushes 0, ppSurface, **1**, 0, 0,
     format, height, width before the call through device vtable +0x74 (`0x617C5F`-`0x617C75`, disassembly of the Steam
     1.67 exe). The automatic depth surface has no discard flag (docs/37: present Flags = 0).
   - DXVK 3.1.1 `D3D9DeviceEx::StretchRect` returns `D3DERR_INVALIDCALL` for a depth source or destination when either
     was made with Discard, when the copy is not the whole surface, or inside a scene (`d3d9_device.cpp`, the
     `srcIsDS || dstIsDS` block). Depth to depth is otherwise supported, and our copy is whole and its target is made
     without Discard, so Discard was the reason; the game is not in a scene at Present (docs/37 §5: EndScene, then
     Present). System d3d9 returns the same code for the same call [verified: the test prints it on both].
   - D24S8 is not one of D3D9's lockable depth formats, so a lock is no way to read it either [inferred: not tried].
   Method: no copy. D3D9 defines a Discard depth surface as undefined after Present, which is where the snapshot is
   taken. To not rely on the contract alone, the server proves from the command stream that the game does not read it:
   per such surface it keeps the rectangle a `Clear` without rectangles has covered since the snapshot (viewport cut by
   the scissor rectangle), and whether the stencil was cleared whole. A draw with the depth test on outside that
   rectangle, or with the stencil test on before the stencil clear, gives the snapshot up ("the game drew with a depth
   surface made with Discard before clearing it"). No refusal at plan or rewind time therefore means: cleared before
   use in every frame since the snapshot. After a rewind the surfaces count as undefined again. The server learns
   Discard from the create commands (a set, filled only with the mode on, emptied at the destroy).
3. **Between snapshot and rewind.** Server: a destroy of an object from the snapshot is deferred (tombstone). Before the
   first write into a vertex buffer, index buffer or surface from the snapshot (a texture level counts through its
   texture; writes are uploads, `UpdateSurface`, `UpdateTexture`, `StretchRect`, `ColorFill`, `GetRenderTargetData`,
   `GetFrontBufferData`) the old contents are read and kept, and before the first `SetLOD` or `SetAutoGenFilterType`
   the old values. Kept contents
   plus the estimated size of the tombstones are limited by `rewindMaxKeptMB` (default 1024) and logged every 128 MB;
   past the limit the snapshot is given up: everything it kept is freed (tombstones are destroyed for real) and every
   later rewind or plan answers `E_FAIL` with the reason.
4. **Rewind.** Tool: park with op 3, the server answers what it would do or that it refuses, nothing is changed. Then the
   tool's own plan on the frozen TS3W; any refusal ends here with nothing changed on either side. Then the tool resumes
   TS3W for a moment (the render thread stays parked) and sends op 2. Server: refuses when a device is lost; else puts
   kept contents back (locks, or `StretchRect` from the copies), releases the objects made since the snapshot (one Release
   each; a texture level or volume is only forgotten), restores the id maps (this revives tombstoned objects), render
   targets, depth surface, device state, gamma and the texture settings, makes the queries issued since answer with their
   result at the snapshot, sets the data ring positions back, sets a mark word in the client's channel mapping and the
   "done" event, and then **reads no command** until the mark is 0 again or 60 s have passed (§28; until commit 6 it
   waited for a "resume" event that the client set after an "applied" event, which our tool reset, §27). The tool
   freezes again, plans again and restores TS3W, including the bytes of the bridge's channel mappings (`viewsBridge`),
   and that restore clears the mark. A command that
   another thread slipped in after op 2 is therefore never run and is wiped with the restore. If the second plan
   refuses, the server has already gone back: the tool ends the game (code 3).
5. **Resume.** The tool clears `request`; the client thread lets go of both mutexes and runs on.

Bridge views the tool has to cover (2026-11-15, after the first in-game snapshot that went through: bridgeKept 25493,
surfaces 339, about 448 MB; the tool then refused the view `QueryResults_<guid>`, +0x2C000):
- The four channel mappings `...Channel_<guid>` (12.1), as before.
- `QueryResults_<guid>` (`util/util_queryresults.cpp`, made at start by `util/util_devicecommand.h:51`): 4096 slots of
  44 bytes = 0x2C000, through which `GetData` of a query is answered without a wait. Not stateless [verified in the
  source]: the client sends one request per query with a sequence number and then only compares the slot's
  `answeredSeq` with it, without asking again (`client/d3d9_query_result.h:69-73`). A client put back to a snapshot
  where that answer was in the slot would wait forever once a later answer has taken the slot. So the bytes have to
  come back, and the tool's copy of the client's view does that (it copies every writable mapped view,
  `rewind-spike.cpp` `copies()`): it is the same section the server writes.
- Safe to save and restore at our two moments [verified in the source]: the server writes a slot only while it runs a
  `GetData` command on its device thread (`server/main.cpp:2963-2976`); at a park every command sent before has been
  run, so every request is answered and nothing writes; between op 2 and "resume" the server reads no command. The
  server keeps no state of its own about the block; the client's (slot per query, free list, sequence counter) is
  process memory and comes back with the process.
- So no bridge change is needed, only the tool's name test (`rewind-spike.cpp`, the shared-view guard): accept
  `\QueryResults_` next to `Channel_` when a bridge client answered. Not built here: the tool is another lane's.

Answers: `S_OK`; `E_FAIL` = refused, nothing changed (the tool's "refused before any change" for op 2, unchanged);
`E_NOTIMPL` = mode off on the server; `E_ABORT` = a rewind failed part-way (a write-back or a state call failed): the
server drops the snapshot, logs an error and answers `E_ABORT` to every later op, and the tool ends the game (its
"no clear answer" path, unchanged). The tool needs no change: the block layout and ops 1 to 3 are as before.

Refused by design (the snapshot is given up, reason in `bridge64.log`) [verified by `test_rewind_server` where marked T]:
- a multisampled render target or depth surface alive at the snapshot [T]: DXVK may resolve on a copy between
  multisampled surfaces [inferred from memory of DXVK's `StretchRect`, not measured], which loses the samples;
- a depth surface the D3D9 library does not copy, such as an automatic depth surface with the discard flag (not
  tracked; TS3 has none) [plain depth surfaces copy on system d3d9 and on DXVK, T];
- a draw that can read a depth surface made with Discard before the game cleared that part [T];
- a write into a volume texture from before the snapshot; a query whose result was not ready at the snapshot and that
  is issued again;
- a device reset, creation or destroy [T]; `SetDialogBoxMode`; Remix API or shared heap commands;
- for the moment only (the snapshot stays): an upload in progress, the shared heap on, a lost device [T for the path].

Limits left:
- Cursor commands (`SetCursorProperties`, `SetCursorPosition`, `ShowCursor`) are counted (`bridgeNotRestored`), not undone.
- An object made after the snapshot is released once at a rewind; if the game had added references to it, it leaks
  until the device goes (the other way would risk freeing an object the snapshot needs).
- The present semaphore count is restored by the tool from the client side (§8.2). DXVK's own state (shader and
  pipeline caches, frame counters) is not rewound [inferred: no effect on the picture with plain DXVK; not measured].
- A render target of the `NULL` format has no contents and is skipped. `GenerateMipSubLevels` and `GetDC` need nothing: the server ignores `GetDC`, and generated levels follow level 0.
- The query answers end when the snapshot is dropped or given up.
- Op 2 over the real channels (ring positions, the server's hold and its mark word) has no offline test: it needs the
  tool on a process that goes through the bridge. In game: reruns 7 and 8 applied it and came back hash-equal (§24, §27).

With the option off [verified by reading every hook; `test_rewind_device` mode off checks the client publishes nothing]:
the client's park function tests one static pointer per Present; the server tests one flag per device command before
the dispatch, one after it, and one in each destroy, upload and query hook. No allocation, lock, system call or extra
command; `rewindMode` defaults to off; the events and the cooperation block are only made when it is on.

Offline tests (in the patch, `bridge/test/rtx/unit/`, run by Apex's `native\remix-bridge\test-rewind.cmd`) [verified
2026-11-15, RTX 30 laptop GPU]:
- `test_rewind_server` (x64, a D3D9 device, no client): 50 checks, PASS on system `d3d9.dll` and on DXVK
  (`d3d9vk_x64.dll` of Apex 4.0.1): tables and a reused id, tombstone and revive, kept buffer / texture / surface
  contents, render target, render target texture, back buffer and depth contents, bound target and render states,
  one Release for a new object, query answer, a second rewind to the same snapshot, the memory limit for contents and
  for tombstones, reset, drop, multisample refusal, `E_ABORT` after a failed write-back and after it, no reference
  left on the device. Since commit 4, 63 checks: also a snapshot and rewind inside an open scene, and the game's case:
  a 1024x768 device as TS3 makes it with a D24S8 depth surface made with Discard (not copied; kept while each frame
  clears first; a part clear allows draws in that part; given up at a draw outside it or before any clear).
- `test_rewind_device` (x86 app through the built pair): mode on: park, plan without snapshot refused, snapshot, a
  second thread's command blocked for the whole park, plan numbers (1 revive, 1 drop), drop; mode off: no cooperation
  block. PASS, 12 runs each.
- Seen 6 times in the first runs and not since (0 in 30 runs of ours, 0 in 12 of upstream's): an access violation in
  the client at the first Present, in upstream's own `test_sent_state_device` too and with the pair of commit 1 as
  well, so not from this change [verified: same failure on the old pair]; cause not found.

### 12.5 Tool changes [verified on the toy]

`rewind-spike.cpp`: `bridgeOp` (writes an op, waits 20 s for the acknowledgement, turns a refusal into a reason and the
eight numbers into stats `bridgeKept`, `bridgeRevive`, `bridgeDrop`, `bridgeBuffers`, `bridgeSurfaces`, `bridgeSavedKB`,
`bridgeNotRestored`, `bridgeRefused`); snapshot sends op 1; rewind sends op 3, plans, sends op 2, plans again; the GPU
module guard (§10). Toy: `C_BRIDGE_ON` makes the toy answer ops like a bridge client, `C_BRIDGE_REFUSE` makes it refuse.
Selftest `refusals`: snapshot saw op 1, rewind saw op 3 then op 2 and came back hash-equal, a refusing server gives code 2
with the game running on, a GPU module gives the GPU refusal. 93 checks, PASS.
`rewind-smoke.ts`: modes `bridge-plan`, `bridge-apply` (no `--no-coop`, shared-view guard on), `refuse-gpu`, and `--renderer`.

### 12.6 Build, install copy and test arm

- Source and branch: a clone of Apex's repo at release 4.0.1 (their latest `main`, d563695) in
  `game-copy/rewind-bridge-1115/apex-repo` (git-ignored), local branch `feature/bridge-rewind`, eight commits:
  1. `tools: add rewind mode to the RTX Remix bridge` (675be38): the first version.
  2. `tools: make bridge rewinds exact or refused, with offline tests`: 12.4 as described above, the two tests,
     `native/remix-bridge/test-rewind.cmd`, and the feature, validation and history pages (78aa5da).
  3. `tools: put back texture filter settings in bridge rewinds` (8caad90): `SetAutoGenFilterType` kept and put back,
     `NULL` targets skipped. Pair d3d9.dll 93584e9f, NvRemixBridge.exe de81c980: the one of the first in-game run.
  4. `tools: leave Discard depth surfaces out of bridge rewind snapshots` (a358170): 12.4 step 2a. Pair f3716806 /
     04dc12c1, the one of reruns up to 8.
  5. `docs: name the shared views a bridge rewind host puts back` (bbfe752), docs only.
  6. `tools: read a rewound client's return from shared memory` (3eea11c): §28. Pair de394c51 / 706346bf.
  7. `tools: stop counting render target 0 as a device reference` (956d61c): §29, patch 0016. Pair
     9b834944 / 7b2d7a77.
  8. `tools: keep default pool textures written after a rewind snapshot` (cb2e62d, the tip): §30. Pair
     c2968f76 / 4b759c9b.
  Their text check (`tools/public_text_check.py`) passes for the files and the commit message. Not pushed. Our repo
  holds the same patch file and the steps: `tools/harness/rewind/bridge-patch/`.
- Two builds exist on disk [verified 2026-11-15]:
  - `apex-repo/native/remix-bridge/out`: the pair of commit 1. The install copy and the first `rewind-bridge-1115` run
    use it. Not rebuilt while that chain runs.
  - `pr/apex-repo/native/remix-bridge/out` (a second clone, same branch): the pair of the tip, from their
    `native\remix-bridge\build.cmd` on a fresh fetch (all 15 patches apply, x86 client and x64 server; VS 2022, meson
    and ninja from the venv in `game-copy/rewind-bridge-1115/venv`), followed by `test-rewind.cmd` (PASS).
  To put the tip on the test arm: copy that pair over `apex-repo/native/remix-bridge/out` (or rebuild there: delete
  `apex-repo/native/remix-bridge/src` first, it still holds commit 1's tree), then `install-copy` again.
- Install copy: `node tools/turbo-compat.ts install-copy` unpacks the latest Apex release into
  `game-copy/rewind-bridge-1115/install/apex-unz`, puts the patched `d3d9.dll` and `.trex\NvRemixBridge.exe` over that copy
  and appends `rewindMode = True` to both `bridge.conf`. It refuses when the latest release is not the one the pair was
  built on (rebuild first). Apex's zip and anything Apex installed are not touched.
- Test arm: Bin state `apex-rewind` of `tools/turbo-compat.ts` (snapshot, state, restore with a whole-Bin hash compare, as
  the other Apex arms). The `ts3-apex-run` skill cannot point at the copy by itself: `apex-run.ts` takes the game Bin from
  `TS3_BIN` and an `--apex-asi`, swaps only our ts3native pair and leaves the bridge files to whatever Bin holds
  [verified: read]. To run Apex's runner on the patched pair: set Bin state `apex-rewind` with turbo-compat first, then
  start `apex-run.ts`, then restore (not tried).
- Queue `tools/harness/queues/rewind-bridge-1115.json` (dry-run ok), RUN FIRST before any further rewind or bridge build:
  census on the arm (10 min, also proves the patched pair plays like the stock one), `bridge-plan`, then one `bridge-apply`.
  Order: census, smoke (3 jobs), the DXVK refuse check, then this queue; all but the DXVK check on `apex-rewind` (§7 item 10).

## 13. What Apex gets, and the pull request

Plan (Rodney, 2026-11-15): we build and test the bridge side ourselves and open the pull request to Apex's repo as a
draft; it leaves draft after our in-game runs pass. Files: `tools/harness/rewind/bridge-patch/PR-BODY.md` (the body,
in Apex's template, their text check passes), `PR-DRAFT.md` (title, command, what to fill in) and `TEST-GUIDE.md`
(plain steps for Loinyx, also folded into the body).

What it is for on their side:
- **Many checks from one load.** Their crash-triage and live-check runner needs about 6.3 min per load [community-reported:
  Rodney]. With a snapshot after the load, N checks cost one load plus N times (check + rewind) instead of N loads. Example
  [inferred, arithmetic]: 10 checks of 30 s each are about 6.3 + 10 x 0.6 = 12 min instead of 63 min, if a rewind takes
  about 5 s. Our numbers: launch, load, menu and close are about 79 s of every one of our 12-min jobs [community-reported:
  Dalexlol, 131 games, §1]; the toy rewinds in 7-9 ms plus 11-16 ms of checks for 18 MB [verified]; the game estimate of
  a few seconds for about 2.3 GB is [inferred] until `bridge-apply` has run.
- **Renderer regression checks from the same state.** Two builds of the renderer, or two settings, drawn from one snapshot
  and compared by screenshot. Today each side comes from its own load, and load-to-load variation is large (in-load CV 0.23
  on Isla Paradiso for our metrics [community-reported, §1]). Whether frames after a rewind are byte-identical is
  [inferred]: render target contents come back since commit 2 (12.4), DXVK's own state does not; it needs the
  screenshot compare, which is not built.
- **Shader and material A/B from identical states.** Same snapshot, switch the setting, look: differences come from the
  setting, not from Sims having moved [inferred].

What they would merge (one patch file, off by default): the server's rewind mode (tombstones, copy on write, restore by
id), the park at the end of Present, and the `Bridge_Rewind` command; `rewindMode` in `bridge.conf` turns all three on.
With it: the two offline tests inside the patch, `test-rewind.cmd`, and their feature, validation and history pages.
What we provide: the client tool (`tools/harness/rewind/`, snapshot and rewind of TS3W with its guards), the census
(`rewind-census.ts`), the toy tests including the fake bridge client, the queues and the test guide.

Status (2026-11-15):
- The tip builds from a fresh fetch; offline tests pass (12.4); the tool's side of the protocol is toy-tested (12.5).
- In game, commit 3's pair: the first snapshot was refused cleanly on a Discard depth surface (12.4 step 2a), fixed in
  commit 4, whose pair (d3d9.dll f3716806, NvRemixBridge.exe 04dc12c1) has not run in game.
- In game, earlier: the first `rewind-bridge-1115` run uses the pair of commit 1. Its results say whether the mode idles
  cleanly and what a plan counts in a loaded world, but they are not results for the tip: the queue has to run again
  on the pair of the tip (12.6) before the PR leaves draft. Expect refusals to read first: a multisampled target
  (edge smoothing on) or a depth surface made with Discard gives the snapshot up by design, with the reason in
  `bridge64.log`.
- Open: op 2 over the real channels has no offline test; the access violation seen in the bridge test apps' first runs
  (12.4) is unexplained.

## 14. Threads parked in a wait for another process (2026-11-15, branch `bugfix/rewind-ntfs-1115`)

### 14.1 What refused every game snapshot on 2026-11-15

Every in-game snapshot of `rewind-smoke-1115` (patched bridge arm and plain DXVK) was refused before any change with
`thread <tid> is inside NtFsControlFile` [verified: `probe-out/rewind/smoke-1115-*.jsonl`, 4 of 4 snapshots]. The other two
reasons (`inside NtGetWriteWatch`, `has live upper vector registers`) showed up once each, always next to it.

- Which thread [verified, static read of the Steam 1.67 `TS3W.exe`, no game run]: EA's own named-pipe server for the
  launcher (strings `NamedPipeListenerThread`, `SIGS/NamedPipes/mGameServerPipeName`, `mLauncherServerPipeName`,
  `NamedPipeServer::Shutdown`). Its only `CreateNamedPipeW` call (`0x60C766`) passes open mode 1 (`PIPE_ACCESS_INBOUND`, no
  `FILE_FLAG_OVERLAPPED`), pipe mode 6 (message, `PIPE_WAIT`), unlimited instances, 1 KB buffers; right after it
  `ConnectNamedPipe(pipe, NULL)` (`0x60C78A`, handle from `[this+0x34]`) is called in a loop. A synchronous listen blocks in
  `NtFsControlFile(FSCTL_PIPE_LISTEN 0x110008)` until a client connects, which in a test game never happens: the thread sits
  there for the whole session. `TSLHost.dll` has the same code (`0x1004EFC6`). That the refused tid is this thread is
  [inferred]: no other module the game loads has a pipe server we know of (the Steam overlay imports `ConnectNamedPipe` too),
  and the refusal stays on every try. The next game run prints it: refusals now carry the thread start (module+offset), the
  system call number and its 10 arguments, the FSCTL code, the handle's name, and the module addresses on its stack.
- Not ours [verified: no pipe code in the repo; ts3native, ts3probe and the agent use none], so there is no cooperation to
  build on our side (cancelling and re-issuing the listen around a snapshot would need a hook in EA's thread for no gain over
  §14.2).
- `NtGetWriteWatch` and the AVX reason were most likely seen only because the tool ran all 41 tries against the
  NtFsControlFile thread and printed what the last try saw [inferred]. Refusals now say in how many tries each thread was
  caught there, which tells a stuck thread from a passing one.

### 14.2 Design

- **Parked wait**: a thread inside `NtFsControlFile` with `FSCTL_PIPE_LISTEN` (ConnectNamedPipe) or `FSCTL_PIPE_WAIT`
  (WaitNamedPipe). It waits for another process and keeps no state of its own; it cannot be restarted from outside (a listen
  issued again is a new call, and after a connection it would answer "already connected"). Snapshot: allowed; its syscall
  number, arguments (11 stack words) and WOW64 context are kept (stat `threadsParked`).
- **Rewind**: a parked thread is only ever left alone. Allowed only when it is still in the same system call, with the same
  context (eip, esp, ebp, ebx, esi, edi, eax, ecx, edx, eflags, segments) and the same 11 stack words, and when its pipe handle
  is still the snapshot's object at the same value. Otherwise refused before any change (exit 2): "left the ... wait it was
  parked in", or "parked ... on handle ..., which is no longer the snapshot's object". It is never put into `set` (restart).
- Every other unknown system call keeps refusing (the existing 40 tries over about 1 s stay the transient-syscall retry).
- Module guards (probe module, GPU device) now run first, before the target is parked or frozen, so `refuse-gpu` gets its own
  reason instead of a thread reason. `--plan-only` still keeps such a refused snapshot to list plans.
- The exit-3 guard is unchanged: everything new refuses in the plan, before the first change. Two new failure points after
  the change (vector state set, vector state verify, §14.4) end the target like every other failure there.
- Accepted gap [inferred]: a pipe connection that came and went between snapshot and rewind leaves the server in an equal
  listen and is not detected; the client side (another process) is not rewound. TS3W's launcher pipe gets no client in tests.

### 14.3 Toy proof (selftest case `parked`, all cases PASS) [verified, Windows 11 26300, i7-11800H]

The toy now runs a pipe server on a synchronous pipe (`ConnectNamedPipe(h, NULL)`, then `ReadFile`, `DisconnectNamedPipe`,
listen again), next to the existing waiter in `WaitForSingleObject`.
- Snapshot with the server parked: OK, `threadsParked` 1, 10 threads waiting. Rewind: OK, 10 threads left, memory hash equal.
- Client connects (server now in `ReadFile`): rewind refused "left the NtFsControlFile wait", toy runs on. A snapshot then is
  refused with the new detail, e.g. `thread 55924 is inside NtReadFile (start rewind-toy.exe+1368, syscall 1a0006, args 1b4 0
  0 0 ..., handle 1b4 (name query timed out), stack kernelbase.dll+1616ee rewind-toy.exe+1368 ...; caught there in 4 of 4
  tries)`. The handle's name query on a synchronous handle with a pending read waits on the file lock: it runs on the helper
  thread with a 300 ms deadline and says so instead of hanging.
- Client writes and closes (server back in its listen): snapshot OK; one more full connection, then rewind: OK (same call,
  same context, same handle), the pipe still takes clients after it.
- GPU guard order: with the server in `ReadFile` (a thread reason present) and a fake GPU module, the snapshot gives only the
  GPU reason and freezes nothing.

### 14.4 Upper vector registers (AVX, AVX-512)

`Wow64GetThreadContext` / `Wow64SetThreadContext` carry only the FXSAVE area (lower 128 bits of xmm0-7). For a WOW64 thread
suspended in 32-bit code the native context with `CONTEXT_XSTATE` (`InitializeContext`, `SetXStateFeaturesMask`,
`LocateXStateFeature`) is that thread's real register file, upper halves included. Snapshot: for each thread running 32-bit
code the AVX and AVX-512 components in use are kept (stat `threadsXState`). Rewind: after the WOW64 context, a native
`SetThreadContext` with only `CONTEXT_XSTATE` writes them back (components not in use at the snapshot go back to zero); the
verify step reads them back and compares. A thread caught inside a system call needs none (vector registers are volatile
across the call); a thread that would get its context late (short Sleep) and had vector state is retried instead.
Toy [verified]: `avx` mode keeps lane 7 of a ymm register equal to a plain sum, rewound 3 times without the cooperation
block, no invariant break; with the XSTATE restore and check switched off the toy reports the break (negative control).
`NtGetWriteWatch`: a plain transient call, left to the retries. Note: a game heap allocated with `MEM_WRITE_WATCH` is still
refused by its own guard (§8.6), which the game snapshots never reached.

### 14.5 Next in game

After this branch is merged into the rewind lane and `build.cmd` is run there: `rewind-smoke-1115` jobs `bp-rewind-plan`,
`bp-rewind-plan-probe`, `bp-rewind-strict`, then `rewind-bridge-1115`, all on Bin state `apex-rewind` (patched bridge).
`bp-rewind-refuse-dxvk` is removed (Rodney 2026-11-15: no plain-DXVK game runs; the in-process GPU guard is proved on the
toy only, cases `refusals` and `parked`).

## 15. Write-watch heaps (2026-11-15, branch `bugfix/rewind-writewatch-1115`)

### 15.1 Finding

After §14 the next in-game snapshot (`bp-rewind-plan`, patched bridge arm) refused on 40 allocations with
`allocation ... uses write watch (its dirty bits cannot be put back)`, many of them 0x40000 (256 KB) apart [verified: job log].

Owner [verified, static read of the Steam 1.67 `TS3W.exe`, no game run]: Mono's Boehm GC, which is statically linked into
TS3W. Of the modules in `Game\Bin`, only `TS3W.exe` names GetWriteWatch. TS3W imports `GetWriteWatch` (IAT `0xF952D8`) and has
exactly one call to it, at `0xE4F00A`, with `WRITE_WATCH_FLAG_RESET` and a 0x400-entry buffer (Boehm's `GC_gww_read_dirty`
shape; the string "GC_gww_read_dirty unexpectedly failed" and the Boehm copyright are in the image). There are exactly two
`VirtualAlloc(..., MEM_COMMIT | MEM_RESERVE | MEM_WRITE_WATCH, PAGE_EXECUTE_READWRITE)` sites (`0xD70908`, `0xD9AB01`).
Their callers `0xE4EEFB` and `0xE51CEF` sit in the same code region as the GetWriteWatch call. That these callers are
Boehm's heap growth is [inferred] from that proximity. 256 KB matches Boehm's minimum heap increment of 64 blocks of 4 KB.
The thread seen once inside `NtGetWriteWatch` (§14.1) is this GC reading dirty pages, so it runs in incremental or
generational mode [inferred].

### 15.2 Why a superset is enough

A Boehm GC in GWW mode asks the kernel which heap pages were written since its last read (and resets the bits), then
rescans only those. An extra dirty page costs rescan time only. A missing one means a pointer stored in that page is not
seen, which can free a live object. After a rewind the memory is the snapshot's, but the bits are not: the GC may have
read and reset them after the snapshot. So every page of every write-watch heap is made dirty after the rewind. Other
consumers may need exact bits, so any module other than TS3W.exe or the toy (and kernel32, kernelbase, ntdll) that
imports GetWriteWatch or ResetWriteWatch is refused by name (`Opt.wwOwners`, no command-line switch). A user that calls
them through GetProcAddress is not seen [inferred gap].

### 15.3 Design (built)

- Snapshot: each private allocation with write watch is recorded (base and end, stat `writeWatchAllocs`). The old
  refusal is gone. The owner guard above runs when any exists.
- Rewind plan, before any change: every committed page of those heaps must be writable (else refused, exit 2). One page
  that is clean is written back with its own content, and the tool checks that its bit is now set (stat `writeWatchProbe`;
  "a write from this tool does not set the write-watch bit" refuses with exit 2). Not run with `--plan-only`.
- Rewind, after the memory restore: each committed page of each heap is written back with its own restored content
  (`WriteProcessMemory`, content-equal). Then `NtGetWriteWatch` without reset must report every committed page dirty
  (stat `writeWatchPages`). A heap that was released after the snapshot is reserved again with `MEM_WRITE_WATCH`.
- `WriteProcessMemory` from another process sets the write-watch bit [verified on the toy, Windows 11 26300: probe and
  16 of 16 pages]. No remote thread is needed.
- Exit-3 risk: the re-dirty step and its check run after the first change. If a page cannot be written or does not report
  dirty, the target is ended (exit 3) like every other failure there. The plan probe makes that unlikely: protection and
  the bit mechanism are checked before the change.

### 15.4 Toy proof (selftest case `writewatch`) [verified]

The toy runs a "GC" on a 64 KB write-watch heap: each run copies the pages GetWriteWatch reports (with reset) into a
shadow copy and checks that the shadow equals the heap (`B_GC` otherwise). Sequence: page 3 is written, snapshot, the
GC takes page 3 and resets the bits, rewind, GC again. With re-dirtying: 16 of 16 pages dirty, the GC's shadow matches.
Control with re-dirtying off: the restore writes no heap page (the heap is unchanged since the snapshot), the GC sees no
dirty page and its restored shadow keeps the old page 3, so `B_GC` is set. Owner guard: with the toy taken off the owner
list, the snapshot is refused with "rewind-toy.exe imports GetWriteWatch".

## 16. Waits taken up again, and threads made after the snapshot (2026-11-15)

### 16.1 Findings from the same game rerun [verified: job log]

- One rewind refused on about 7 threads: `thread N is in NtWaitForSingleObject; at the snapshot it was in
  NtWaitForSingleObject` (also NtWaitForMultipleObjects and NtWaitForWorkViaWorkerFactory). These threads woke and went
  back into the same kind of wait. The tool did not say what differed. Its comparison then covered eip, esp, ebp, ebx, esi,
  edi and 11 stack words. Since §14 that is 5 more words than before, which for a 3-argument wait reaches into the
  caller's frame.
- 7 threads made after the snapshot were refused, all in `NtWaitForSingleObject`: start `0x6935DBC0` (6 threads) and
  `0x723C29E1` (1). Mapped with the module bases of the 11-15 census of the same machine (`bridge-rewind-1115-TS3W-*.json`):
  `ApexRadiance.asi+0xADBC0` and `msdmo.dll+0x29E1` [inferred: the bases come from an earlier session on the same boot,
  not from the run itself]. The census shows Apex with 23 threads at a time, so Apex starts and ends workers during play.

### 16.2 Rule for a wait taken up again (built)

A thread waiting at the snapshot and waiting now is left alone when it is the same call, defined as:
- same stub, eip, esp and ebp;
- same callee-saved registers (ebx, esi, edi), which the caller sees after the return;
- same return address and arguments (the count comes from the stub's `ret n`);
- same 64-bit stack pointer;
- same inputs behind pointer arguments (handle array, timeout), compared with the snapshot's memory copy;
- each handle it waits on is still the snapshot's object (`planRest`; this also closes the gap for waits that never woke).

Stack words above the arguments may differ: they are memory, the rewind puts them back, and the kernel does not hold them.
Why that is safe: after the rewind, everything the thread can observe when its wait returns equals the snapshot. That is
its memory and stack, its callee-saved registers, and a call with the same arguments on the same object. The volatile
registers are overwritten by the return. The wait itself started later, the same as the existing restarted waits in §8.2.
Stat `threadsRearmed`. When the call differs, the refusal now lists what differs (for example `differs: esi 0->1`), so the
next game run shows which case the 7 threads are.

Toy [verified, case `rearm`]: a worker calls `NtWaitForSingleObject` itself and keeps a counter in the stack word just
above the arguments. After it wakes and waits again, the rewind leaves it (`threadsRearmed` 1). It then wakes with a
consistent state: the counter and the state block went back together. With the counter in ebx, the same worker is refused
(`differs: ebx`). The first toy build kept the counter in esi on its own: the compiler does that for loop counters. That
loop is not safe to leave and was refused (`differs: esi 0->1`).

### 16.3 Threads made after the snapshot (built, under `--reap-workers`)

Any thread made after the snapshot that sits in a wait-only call (the §8.2 list) is ended at the rewind under
`--reap-workers`. Every smoke job already passes that flag. Before, only idle ntdll pool workers were ended. Without the
flag, the refusal names the start module (`start ApexRadiance.asi+...`). Why that is safe [inferred, argued]: after the
rewind, memory says the thread never existed (its creator's records, its stack and TLS data are put back). Handles made
after the snapshot, including its own thread handle and anything its DLL attach opened, are closed by the rewind. A mutex
it owns, or window objects, still refuse. It is in a wait, so it holds no user-mode lock the restored memory does not
also show. Toy [verified]: a thread in `Sleep(INFINITE)` made after the snapshot is refused without the flag and ended
with it; the toy runs on and passes its audit.

### 16.4 What needs the `[Rewind]` hook (design, not built)

Outside rules stop where a worker keeps loop state in a callee-saved register across its wait (the toy's esi case), and
for a thread that exited after the snapshot (it cannot be made again from outside). If the next run's `differs:` lines
show registers on the 7 threads, the tool cannot make the rewind safe alone. The hook (§9.1) then has to:
1. Park the sim thread at a tick boundary (§9.1).
2. Before setting `parked`, wait until our own threads (ts3native watchers and lanes) sit in their idle waits.
3. Ask Apex through its own rewind or quiesce entry, which Apex would have to add, the same way the bridge server takes
   ops in §12, to keep its worker set fixed and its workers in their idle wait. The thread set then stays stable between
   snapshot and rewind, and every thread is either in the same wait or ended under 16.3.

The game's own Mono and msvcr80 workers wait at fixed loop sites; whether their registers stay equal is what the next run
shows.

### 16.5 Next in game

After merging into the rewind lane and building there, rerun `rewind-smoke-1115` `bp-rewind-plan` and then
`bp-rewind-strict` on Bin state `apex-rewind`. Read the `differs:` lines first: arguments or words alone are handled now,
registers mean 16.4.

## 17. Waits whose registers or depth differ (2026-11-15, branch `bugfix/rewind-waits-1115`)

### 17.1 Finding (rerun 2, tool 7b5a5709, patched bridge) [verified: job log]

The snapshot was taken; the rewind refused on 5 waiting threads:
- 6120, 54528 and 45760: `NtWaitForSingleObject`, only esi differs (`271->290`, `6636eb60->0`, `18b0d116->18b13f90`).
- 53900: `NtWaitForMultipleObjects`, esp, ebp and arg2 differ (another call depth).
- 28148: `NtWaitForAlertByThreadId`, esp, ebp, esi and both arguments differ (another depth).

The log names no module or return address for these threads, so their owners are not known yet. Refusals now print
`start <module+offset>, returns to <module+offset> (snapshot <module+offset>)`.

### 17.2 What the esi difference is [verified, static, Windows 11 26300 SysWOW64]

`KernelBase!WaitForSingleObjectEx` saves ebx, esi and edi in its SEH prolog and does not touch esi before the system
call. At the call, esi is still the caller's value, which is why it looks like a loop counter or pointer. The return site
(`+0x1600C8`) starts with `mov esi, eax`, and the epilogue restores the caller's esi with `pop esi` from the frame. The
frame is memory, so the rewind puts it back. `WaitForMultipleObjectsEx` has the same shape (`mov esi, eax` at `+0x160F3F`).
So when the return site is one of these, an esi that differs is harmless [inferred for the 3 game threads until the next
run prints their return sites].

### 17.3 Design (built)

- **(a) Dead registers at the return site.** When only ebx, esi or edi differ, the tool reads the code at the return
  address in the target. It decodes a small set of instructions (mov, xor-self, test, push, pop, mov imm) and stops at
  anything else. A register is allowed to differ only if it is written before it is read on that straight line. The
  return address, esp, ebp, arguments, inputs and handle objects must still be equal. Stat `threadsDeadRegs`. A
  different esp is another call depth, so (a) never applies there.
- **(b) Alert waits at another depth.** This applies to `NtWaitForAlertByThreadId`, where both return addresses are in
  the 32-bit ntdll and the 64-bit stack pointer is the same. Only ntdll's wait-on-address, SRW lock and condition
  variable code waits this way, and it re-checks its condition after any return, so a spurious wake-up is safe. Before
  any change, the tool gives the thread its snapshot context at the stub start (reversible). A helper thread is created
  in the target without DLL thread-attach calls. It calls the 32-bit `NtAlertThreadByThreadId(tid)` and exits at once,
  with a 2 s deadline and its exit status checked. The handle, view and allocation plan is then made again, so the
  helper's traces are put back with the rest. On any failure the contexts go back and the rewind is refused (exit 2);
  the thread then sees one spurious return, which its loop takes. The memory restore puts the snapshot's WOW64 state
  back, so the thread leaves its current wait and enters the snapshot's (stat `threadsRealerted`).
  - Tried first and rejected [verified on the toy]: `NtAlertThreadByThreadId` from the tool's process returns
    `STATUS_ACCESS_DENIED` (c0000022), and it ran after the change, so exit 3. A special user APC
    (`NtQueueApcThreadEx2`, flag 1) is queued but does not end the non-alertable wait.
- **Everything else is refused by name**: another depth on a counted object (events, semaphores, multiple objects),
  registers that are live or not proven dead, and any other call.
- **(c) The `[Rewind]` hook** (§9.1, §16.4) is still the way to a stable thread set, but it covers only threads we own
  or can park. These are the sim thread at a tick boundary, the ts3native watcher and lane threads, and the bridge
  client (it already parks, §12). It would not cover Apex workers (`ApexRadiance.asi`), the Windows thread pool
  (ntdll), `msdmo.dll` (DirectX Media Objects), Mono's GC and finalizer threads, msvcr80 worker threads, the Steam
  overlay or sound threads. Those stay with (a), (b), §16.3 and the retries.
- Exit-3 risk: none new. The helper runs and is checked before the change, and the context write is undone on a refusal.
  After the change only the existing context set and verify paths remain.

### 17.4 Toy proof (case `rearm`) [verified]

- **Dead register.** A worker keeps a counter in esi across `NtWaitForSingleObject` and overwrites esi with `mov esi, eax`
  right after the return. After it wakes and waits again, the rewind leaves it (`threadsDeadRegs` 1), and it wakes later
  with a consistent state.
- **Live register.** The same worker with the counter in ebx is still refused (`differs: ebx`, with
  `returns to rewind-toy.exe+...`).
- **Alert wait.** A condition-variable waiter (`SleepConditionVariableSRW`, so `NtWaitForAlertByThreadId` from ntdll) is
  woken on A and then waits on B one frame deeper. The rewind puts it back into its wait on A (`threadsRealerted` 1; a
  plan right after it shows the thread in the snapshot's wait), and the waker of A finds it there. B and A work again
  afterwards.

### 17.5 Prediction for the next game run (after merge and build, `bp-rewind-plan`, then `bp-rewind-strict`, Bin state `apex-rewind`)

- 6120, 54528 and 45760 (esi only): left alone if their return site is KernelBase's `WaitForSingleObjectEx`, which is
  likely for game, Apex and CRT code [inferred]. If not, the refusal now names the return site.
- 28148 (alert wait at another depth): handled by (b) if both return sites are in ntdll and the 64-bit stack pointer
  matches.
- 53900 (`NtWaitForMultipleObjects` at another depth): still refused, now with its start and return modules. If it
  persists, it needs the hook or an owner-specific rule.
- New waiting threads are ended under `--reap-workers` (§16.3).

## 18. Busy threads caught in their snapshot wait (2026-11-15, branch `bugfix/rewind-catch-1115`)

### 18.1 Finding (reruns 1 and 2) [verified: job logs]

One `NtWaitForMultipleObjects` thread refused in every run: 41368, 51412/58272, 53900 and 65176. Each time it was the
same shape: esp, ebp and arg2 (the handle array) differ, for example `esp 11bdfdac->11bdf270`. At the snapshot the thread
waited about 0x254 bytes below its stack top, so in a shallow main-loop wait. In the retries it was 0xB90 or 0x1600
bytes deeper, so in nested waits. Over 41 tries (1.5 to 14 s) it was never back at the shallow wait. The owner is not
known yet. Rerun 3 runs the §17 build, which prints the start and return modules.

### 18.2 Candidates and the rule for each

Re-entering a counted-object wait (events, semaphores, multiple objects) from outside could consume a signal, so §17 (b)
never applies here. The safe tools are the catch (§18.3), the `[Rewind]` hook (§9.1), or an owner-specific rule.

| Candidate | How it would wait | Rule |
|---|---|---|
| Message or COM STA loop (`MsgWaitForMultipleObjects`, `CoWaitForMultipleHandles` in an STA) | `NtUserMsgWaitForMultipleObjectsEx` in win32u, not this call [inferred: Windows docs] | not this thread; a GUI wait stays under the GUI guard |
| COM MTA `CoWaitForMultipleHandles`, CRT or game worker | `WaitForMultipleObjectsEx`, returns to `kernelbase.dll+160f3f` | catch |
| Apex or DXVK worker (`ApexRadiance.asi`, `d3d9.dll`) | own event loop | catch; a stable park point is Apex's to add (hook, docs/270 §12) |
| Bridge client | already parks through the cooperation block (§12) | not this thread |
| Audio mixer (dsound, XAudio2, FMOD, `msdmo.dll`) | timer or event loop with `WaitForMultipleObjects` | the sound-module guard sees it by start module (the game jobs pass `--allow-audio`); catch |
| Steam API (`steam_api.dll`, `steamclient.dll`, overlay) | IPC worker with `WaitForMultipleObjects`; talks to steam.exe through shared memory | catch only while the shared-view guard passes; else refused |

### 18.3 Design (built): catch and hold

- A plan attempt can fail only because snapshot threads that were in a wait are elsewhere now (`catchable`, the same
  list the retry refusals come from). When that happens, the tool polls each such thread alone: it suspends the thread,
  classifies it, and runs the same `sameWait` (and `sameParked`) checks as the plan, then resumes it. Polls are about
  50 us apart. Once a thread is in its snapshot wait, it stays suspended (held), and the next `freeze()` suspends the
  rest around it. The hold's suspension then becomes the freeze's own: the count is net 1, and `prev` is unchanged for
  the suspend-count restore.
- Nothing in the target changes. A thread suspended in a wait is what `freeze()` makes too, and the plan still decides
  with the same checks. Held threads are in the watchdog's list (phase 1), so a host crash resumes them. On a miss within
  the budget, every held thread is resumed and the plain retries go on.
- Budget `--catch-ms` (default 2000 per rewind, 0 = off). Stats: `catchPolls`, `catchMs`, `threadsCaught`.
- Exit-3 risk: none new. Everything happens before the change. The hold only delays the thread, like the freeze. A
  thread that never comes back gives the same exit 2, about 2 s later.
- Limit: this does not help a thread that left the snapshot's wait for good, or that comes back too rarely for 2 s.
  Those still need the hook or an owner rule.

### 18.4 Toy proof (case `catch`) [verified]

A busy worker waits in a shallow `WaitForMultipleObjects`. While busy, each pulse sends it into a frame 0x1600 bytes
deeper, and the next pulse lets it back to the shallow wait. It returns to `kernelbase.dll+160f3f` like the game thread.
- **No gap** (never back at the shallow wait): refused with `differs: esp ... ebp ... arg2`. 5802 polls in a 500 ms
  budget, nothing held, and the toy runs on consistently.
- **About 300 us at the shallow wait per ~20 ms pulse**: caught in 162 polls (14 ms), and the rewind is exact
  (`threadsCaught` 1, hash equal, audit clean). The worker's stack counter and the state block went back together, and
  it runs on after the rewind.
- All other cases still pass.

### 18.5 Prediction

- Rerun 3 (§17 build) still refuses this thread, now with `start <module+offset>, returns to <module+offset>`.
- With this build, if the thread returns to `kernelbase.dll+160f3f` and comes back to the shallow wait at least a few
  times per second, the catch takes it (expect `threadsCaught` 1 and a `catchMs` of a few ms to a few hundred). If it
  is a Steam thread, the shared-view guard still applies (§18.2). If it never comes back within 2 s, it is refused as
  before, and `catchPolls` shows that the catch tried.

## 19. Our route worker parks for the rewind (2026-11-15, branch `bugfix/rewind-catch-1115`)

### 19.1 Finding (rerun 3, tool c6d488bc) [verified: job log, map]

The deep-wait thread is ours. The log says `start ts3native.asi+d4d40, returns to kernelbase.dll+160f3f`. The rewind jobs
pin `ts3native.asi.master-merge-1115` (0.36.261-lanes1). In that build's map (`native/ts3native/out`, sha256 b75b706a,
the same as builds.json), rva 0xd4d40 is `WorkerMain`, the route worker (`ts3native.cpp`, `StartWorker`).
- **Shallow wait** (the snapshot's): `WorkerMain`'s idle `WaitForMultipleObjects(g_jobReady, g_asyncReady)`.
- **Deeper waits** (0xB3C and 0x15B8 bytes): `Proxy`'s `WaitForMultipleObjects(g_invDone, g_jobReady)`. A route job's
  managed call waits there for the sim thread, which serves it only at script IC points (READY polls). The two depths are
  the same wait reached from a job directly and from a nested plan [inferred from the code; the call depth matches].

While a job waits for the sim thread, the worker cannot come back. That is why the catch (§18) did not get it: 19152
polls in 2000 ms, `threadsCaught` 0. A plan right after it passed on the first freeze, once the job had ended.

### 19.2 Fix (built): `[Rewind] Park` (ts3native, default 0)

- With `Park=1`, ts3native creates `Local\rewind-ts3n-<pid>` (`RewindWorkerPark` in `rewind-toy.h`: request, idle, held).
- While `request` is 1, `AsyncStartBody` starts no job and returns 0, so the plan runs inline, as with async off
  (`held` counts these). Running and queued jobs finish as usual. `WorkerMain` sets `idle` around its wait.
- The tool parks the worker before the bridge client parks the sim thread: the sim thread must still serve a running job's
  calls. The tool waits for `idle` in 5 reads 2 ms apart (`--quiesce-ms`), else it refuses (exit 2). It unparks with the
  cooperation block. Stat `workerParked`.
- Behaviour with `Park=0`: nothing is created, no check runs. With `Park=1` and no tool: one flag write per worker wake.
  While parked, plans run inline as with `[RouteAsync]` off, which is a supported mode [inferred: same result, more sim-thread time].
- Toy proof (case `workerpark`): the no-gap busy worker of §18, which the catch cannot take, is parked and rewound
  exactly (`workerParked` 1, hash equal); after the unpark it works again. Native selftest: a START while parked starts no
  job.
- Build `ts3native.asi.rewind-park-1115` (builds.json) = master-merge-1115 + #408 (DiscDefer slot reset, already on
  master) + `[Rewind]` Park / Offline.

## 20. Writable views shared with other processes (2026-11-15)

### 20.1 Finding (rerun 3, `bp-rewind-bridge-plan`) [verified: job log]

The bridge's `Channel_` views passed (viewsBridge 4). Four views were still refused as shared:
- `QueryResults_<guid>`: the bridge's. The bridge agent's verdict is that it is quiet at the park and the client view
  restore covers it, so it is now classified with the bridge views.
- `UrlZonesSM_<user>` (+1000, 42 handles): urlmon's zone map, shared by every process that uses urlmon or wininet.
- An unnamed section (+1000, 12 handles) and an unnamed section (+3000, 4 handles): the owners are not known yet. The tool
  now names them (§20.3). For the 4-handle one, "3 ours" is the target's handle plus the tool's 2, so 1 handle is held
  somewhere else, perhaps in the target itself [inferred].

### 20.2 Who loads wininet and urlmon [verified, static, Steam 1.67 Bin]

- `Game\Bin\wininet.dll` (sha256 d5a059aa467a) is the **Ultimate ASI Loader** (`UltimateASILoader` and its build path in
  the binary). It loads the ASI plugins and forwards to the system wininet.
- `TS3W.exe` imports WININET statically: InternetOpenA, InternetConnectA, HttpOpenRequestA/SendRequestA/ExA,
  InternetReadFile/WriteFile, cookies, InternetGetConnectedState and more. This is EA online code (store, news, Exchange,
  login, patch check; docs/16). `TSLHost.dll` (launcher host) has the same imports. No ASI imports them.
- So the system wininet is always loaded. The zone map comes from urlmon/iertutil when wininet handles URLs or cookies
  [inferred: Windows behaviour, not traced].

Fix: `[Rewind] Offline` (ts3native, default 0; rewind test queues only). It rewrites TS3W.exe's wininet import slots so
that InternetOpenA fails, InternetGetConnectedState says offline and the two cookie calls fail. The game takes its own
no-connection path. TSLHost.dll is patched only if it is already loaded (a `shortcut:` note in the code). Player installs
keep `Offline=0`. Script-side offline behaviour (dialogs, gated interactions; docs/16 item 5) only matters in test runs.
Not proven in game yet: the next run must show no `UrlZonesSM` view.

Test isolation, the other half: `tools/harness/net-block.ts` turns an outbound firewall block for TS3W.exe on and off for a
chain. This session is not elevated (Medium integrity), so a firewall rule cannot be changed from here. Rodney runs
`node tools/harness/net-block.ts setup` once and pastes its 3 lines into an elevated PowerShell. They create a disabled
rule and two scheduled tasks that run elevated and only enable or disable it. Chains then call `net-block.ts on` and set
`trap 'node tools/harness/net-block.ts off' EXIT`. The rule matches the Steam install's TS3W.exe, so it also blocks
Rodney's own play while it is on.

### 20.3 Rule for the rest (built): left alone, checked unchanged

A writable view shared with another process that is neither ours nor the bridge's is no longer refused at the snapshot.
- The snapshot names it in `notes`, with the processes that also hold the section (handle scan of same-user processes,
  `also open in <exe> xN`). Stat `viewsForeign`.
- A rewind does not write it back. Before any change, it compares the view's bytes with the snapshot's and refuses
  (exit 2) if they changed, naming the view. Verify skips its bytes.
- Safe for read-mostly caches such as UrlZonesSM: the rewound target finds the view as it was at the snapshot, and nothing
  of another process is written. The old `--allow-shared-views` (restore it with the rest) is unchanged.
- Cost: the owner scan takes about 0.5 s per shared view at the snapshot.
- Toy proof (case `refusals`, extshare): the view the host also maps is left alone, and the rewind is exact
  (`viewsForeignSame`). After the host writes into the view, the rewind is refused before any change, naming the view;
  the host's byte stays. The audio case is now a snapshot with foreign views (the audio service).
- Exit-3 risk: none new; the check runs before the change, at the first plan pass only. Under the bridge, a later write
  by another process lands like one made after the rewind.

## 21. Rerun 4 fixes: park before the pause, private sections, pool workers (2026-11-16)

### 21.1 Findings (rerun 4, tool 5cc36ebb, `ts3native.asi.rewind-park-1115` with Park and Offline on) [verified: job logs]

- **Park not used in the `--no-coop` jobs.** `park()` returned before `parkWorker` under `--no-coop`, so plan, plan-probe
  and strict never parked the worker (no `workerParked`). The second plan-probe plan refused on `WorkerMain` at its
  `Proxy` depth again.
- **Park asked after the pause.** In `bp-rewind-bridge-plan` the plan refused: "the route worker did not come back to its
  idle wait within 5000 ms". `rewind-smoke.ts` pauses the game (agent `set_speed pause`) before each op. A route job's
  managed call is served only at script IC points, which a paused game does not run, so a job caught by the pause holds
  the worker in `Proxy` until the game runs again. The snapshot's park and the coop park were released correctly
  (`unpark` on every exit path).
- **Our Sleep thread**, `ts3native.asi+33720` = `CpMain` (rewind-park-1115 map): the `[ProbeSnap] CallSites` coverage
  thread. queue.ts turns it on for every job unless the job says `callSites: false`. At the snapshot it was in its
  start-up `Sleep(DelaySec)`; later it was in its 60 s loop sleep, with the int3 traps armed (code patches and a VEH).
  A "same Sleep, only the timeout differs" rule would have left it in the loop while memory went back to before the
  arming, so that rule is not safe here.
- **Unnamed +3000 section "also open in TS3W.exe x1"**: that holder is the target itself (the owner list includes the
  target). The section is the target's own memory with a second handle.
- **Pool worker** 32104 (ntdll `TppWorkerThread`) was inside a work item (sleeping) at the snapshot and idle at the plan.

### 21.2 Fixes (built)

- `park()` parks the worker first, also under `--no-coop`. A new serve command, `park`, parks the worker while the game
  still runs. `rewind-smoke.ts` sends it before every pause; the tool holds the park until the op after the pause ends.
- The rewind queues set `callSites: false` (no coverage traps in rewind runs).
- Shared-looking views: one handle scan per snapshot (this process first, then every process it can open). A view held
  only by the target and the tool is the target's own memory (`viewsSharedSelf`) and is restored like the rest. A handle
  count above twice the handles seen means a holder the tool cannot open (a protected process such as audiodg), so such
  a view is foreign. Foreign notes name the holders by exe and pid and the first module data word that points at the
  view ("pointed to from").
- Snapshot quiescence: a thread with the start of an idle pool worker (`NtWaitForWorkViaWorkerFactory`, starts kept
  across ops) that is doing anything else is inside a work item. The snapshot retries until every worker is idle, else
  refuses with its stack modules (the callback's owner).
- Cheap retries: once the catch has used its budget and only waits are in the way, the rewind stops retrying (it used to
  run 41 freezes, 5 to 12 s).
- Toy proof: `workerpark` under `--no-coop` (snapshot and plan, `workerParked` 1, no catch). `refusals` extshare: held
  only by the target, the view is restored (`viewsSharedSelf`); with a second process holding it, it is left alone and
  named with that process's pid. `threads`: a 300 ms work item, refused with `retries` 2, taken after it ends with
  `retries` 40. Full rewind selftest PASS, selftest-all 0 FAIL.
- Fix 2026-11-16 (extshare flake, 2 of 60 `refusals` runs): after the first shared-looking view, the owner scan holds a
  duplicate of every section handle in the system, so a later view of the target's own unnamed section counted more handles
  than the target holds and went through the scan too (`viewsSharedSelf` 2: still restored, never foreign). The tool's
  duplicates are no longer counted; the extshare toy maps an unnamed section above the named view, so the case fails every
  time without the fix (3 of 3) instead of by address layout.
- ts3native is unchanged: `ts3native.asi.rewind-park-1115` stays pinned.

## 22. Rerun 5: a wininet pool work item at every snapshot (2026-11-16)

- Finding: the snapshot refused 41 of 41 tries on a thread-pool worker inside a work item, stack ending in
  `wininet.dll+1d4807`. That is the system wininet (SysWOW64), not the ASI loader's `wininet.dll` in Bin [verified: the
  return address follows `push 0x7530; call [Sleep]` in the system DLL]. The callback at rva 0x1d4760 is a `PTP_WORK`
  callback that pins its module (`FreeLibraryWhenCallbackReturns`) and loops `while (flag) { flag = 0; Sleep(30000); }`;
  each new submission sets the flag again, so the worker sleeps in the pool for as long as wininet keeps being used
  [verified in the disassembly; what it does after the loop and who submits it are inferred]. `wininet.dll+206cbc` on
  the stack is stale data.
- Offline was on from the start (`[Rewind] Offline on: 4 of 4 ...` in the job's ts3native.log), but it turned away only
  4 of TS3W.exe's 19 wininet imports. The others (`InternetSetOptionA`, `InternetQueryOptionA`,
  `InternetSetStatusCallback` and the HTTP calls, some on NULL handles) still reached wininet, and that is the likely
  source of the work item [inferred]. Apex uses winhttp, not wininet.
- Fix: `[Rewind] Offline` now turns away 18 of the 19 imports. Each stub has the import's own stdcall argument count,
  checked against the `ret N` of each system wininet export. Stubs return 0/NULL/FALSE with
  `ERROR_INTERNET_DISCONNECTED` (12163). Exceptions: `InternetCloseHandle` returns TRUE, `InternetErrorDlg` returns
  `ERROR_CANCELLED`, and `InternetSetStatusCallback` returns NULL. `InternetCrackUrlA` stays real because it only parses
  a string. The log line now gives the number turned away and the number left (1 expected).
- Limit: only TS3W.exe's import table (and TSLHost.dll's if it is already loaded). If the work item still shows up,
  the next step is a rule that treats a pool worker in this known 30 s wininet sleep as idle. Prevention comes first.
- Build `ts3native.asi.rewind-park2-1115` (builds.json). The native selftest checks the stub table and return values.
  Both rewind queues are repinned to it. NOT run on the game yet.

## 23. Rerun 6: the audio engine's shared buffer (2026-11-16)

- Rerun 6 on `rewind-park2-1115`: the wininet work item is gone. The snapshot passes and all 4 plans return code 0.
  The only refusal left in `bp-rewind-strict` (apart from the bridge channels under `--no-coop`) is the same one
  `bp-rewind-bridge-apply` hit in rerun 4: an unnamed writable view of +3000. Its holders were the target and one
  process the tool cannot open; no module data word points at it.
- Who holds it:
  - The system handle table (`SystemExtendedHandleInformation`) would give the holder's pid without opening it. On
    this Windows build, without SeDebugPrivilege, it hides object addresses, so it cannot match the section. The tool
    still tries it and names the process when the table shows addresses.
  - Fallback: the tool scans the target's private memory for words equal to the view's base. Before each hit it looks
    for the nearest vtable, a word that points into an image module whose first entry points into the same module.
    The note then says "kept by objects of <module>".
  - Toy proof: `rewind-toy.exe audio` (one `waveOutOpen`) makes exactly the same view: +3000, unnamed, one holder the
    tool cannot open, pointed to from no module. Its note now reads "kept by objects of audioses.dll" [verified on the
    toy]. That is the WASAPI audio client's render buffer, which the audio engine (audiodg.exe) reads. For the game the
    same is [inferred] from the identical signature (3 sound threads, `--allow-audio` passed). The next run's note
    shows the module.
- Why it is not game state: the buffer holds samples and stream positions, written by the sound stack (audioses.dll,
  driven by the game's audio threads through dsound/XAudio-style APIs) and read by audiodg. The pointer to it sits in
  audioses.dll's own objects, not in TS3W.exe or ts3native data. The sim does not call the sound stack's buffer
  functions; it only queues sounds, which the audio threads mix. [inferred: from who keeps the pointer]
- Rule (only under `--allow-audio`): a foreign view whose other holder is audiodg.exe, or a process the tool cannot
  open while a sound module's object (`dsound`, `audioses`, `xaudio`, `openal`, `wdmaud`, `mmdevapi`) keeps the view,
  is an audio view (`viewsAudio`). At a rewind it is left as the engine left it and does not stop the rewind
  (`viewsAudioMoved`). At worst there is a click. Without `--allow-audio` the same change still refuses with exit 2
  before any change. Other foreign views are unchanged (§20).
- Toy proof (`refusals`): the audio toy's view is classed as audio. A byte changed in it after the snapshot rewinds
  with code 0 and `viewsAudioMoved` 1 under `--allow-audio`, and is refused without it. Full rewind selftest PASS.
  ts3native is unchanged.

## 24. Rerun 7: the first good rewind, then the probe stopped answering (2026-11-16)

- `bp-rewind-bridge-apply` (rerun 7): the first rewind returned code 0 and verify passed (memory 18454 ms, verify
  1166 ms, `bridgeRevive` 162, `viewsAudioMoved` 1). After it, every probe call timed out (`set_speed`, `game_state`),
  and the job failed with "game does not answer".
- The game kept playing [verified from the job's own files]:
  - The machine samples after the rewind (16:51:56 to 16:54:06 UTC) show TS3W.exe at 4-9 % CPU and the bridge GPU at
    23-28 %, the same as before.
  - ts3native.log goes on with sim lines (route jobs, `ufplan` ticks) and the periodic "memory at 5 min" line, which is
    written at 16:54 UTC, after the rewind.
  - The probe's own memory sampler, a Frida call every 10 s, wrote its last row at 16:51:02. The 16:51:12 call fell
    inside the rewind freeze and never returned.
- The AVs in the log are not the cause:
  - The summary line ("1486 more AVs ...") is printed before the world load, so all those AVs come before the rewind.
  - `0x0F6689D8` and `0x0F6689E4` are ApexRadiance.asi+0xF89D8 and +0xF89E4, on the sim thread during the load. The
    earlier job of the same queue, which never rewound, has the same two sites 3027 and 651 times.
  - Module 0x66300000-0x66DDA000 in log line 4 is ts3native.asi's own image range (the crash log names its own
    module). frida-agent.dll, the probe, sits right after it at 0x66DE0000 (`66E4BA69` x4 is frida-agent.dll+0x6BA69).
- Cause: a Frida call in flight across a rewind. Toy proof (Frida 17.23 attached to `rewind-toy.exe`, one
  `ping` RPC loop running while the tool rewinds):
  - In one of 5 runs, the in-flight call never returned, later calls timed out, and a new `frida.attach` timed out too.
    That matches the game.
  - In the other runs only the in-flight call was lost. The agent and its host session (pipe, message state) are
    put back to the snapshot while the host side moves on.
- Fix (test harness only):
  - `ts3probe.py` gets `/fridadetach` and `/fridaattach`. The HTTP server and the probe process stay up, so queue.ts
    does not see a gone probe; game calls fail between the two.
  - `rewind-smoke.ts` detaches the probe before every snapshot and rewind, waits until `frida-agent*.dll` is no longer
    loaded (`tasklist /M`), runs the op, then attaches again.
  - Ops that write into the game no longer pass `--allow-module frida-agent`, so the tool's `frida-agent` module guard proves
    the agent is out. Plan ops keep the agent in (they write nothing).
- Toy proof of the flow: detach, snapshot, attach, 200 pings, detach, rewind, attach, 200 pings, detach, a second
  rewind, attach, ping. 5 of 5 runs had no lost call and code 0 every time, with the module guard on.
- Still open from rerun 7: the second rewind was refused because 3 ntdll pool workers (start 77e34e70) and one thread
  with start 679fe00c exited after the snapshot (next item). The memory restore of 18.5 s writes 87301 pages (see the
  next item for a dirty-page-only restore).

## 25. Idle pool workers end between rewinds (2026-11-16)

- Rerun 7's second rewind was refused because 3 threads with start 77e34e70 (ntdll's pool worker) ended after the snapshot.
  The snapshot was at 16:50:47 and the second rewind at 16:52:56, about 130 s later.
- Measured on a 32-bit test process: 12 short work items grow the default pool to 8 threads. With no more work, they all
  end together after 130-135 s idle (8 threads, then 1).
- The kernel's worker factory counts its threads, and a rewind cannot bring an ended thread back. Raising the factory's
  minimum stops idle workers from ending. Setting it to the thread count from another process
  (`NtSetInformationWorkerFactory`, ThreadMinimum, on a duplicate of the target's `TpWorkerFactory` handle) kept all
  7 workers past 150 s [verified on the test process].
- Fix (in the rewind tool, not ts3native, so it works for any target and needs no game build): after a good snapshot,
  each worker factory whose thread count is above its minimum gets its count as the minimum (`poolFactoriesHeld`,
  `poolWorkersHeld`). The old minimum is put back when the snapshot is dropped (a new snapshot, or the tool ends).
  Workers that are created after the snapshot are reaped as before (`--reap-workers`).
- Toy proof (`threads`): after a snapshot the factory's minimum equals its count; after the snapshot is freed it is
  back to the old value.
- Thread start 679fe00c (rerun 7, also gone) is a Frida agent thread: frida-agent.dll+0xC1E00C. In the bridge census
  of 11-15 (same boot, frida-agent.dll at 0x66DE0000, as in rerun 7) five threads start there. With the probe out of
  the game during ops (§24) there are none at the snapshot.

## 26. Faster memory restore; the second park after a rewind (2026-11-16)

- The restore always wrote only pages that differ from the snapshot (read, compare, write changed runs). Its cost was
  the write call: `WriteProcessMemory` also queries the protection and flushes the target's instruction cache on every
  call. Toy measurement: 87040 one-page `WriteProcessMemory` calls into the toy took over 2 minutes. The game's
  86802 changed pages are scattered, so most are their own run.
- Fix: bulk writes go through `NtWriteVirtualMemory` alone, and executable regions that were written get one
  `FlushInstructionCache` each (`codeRegionsFlushed`). The write-watch pass (`wwTouch`) uses the same call. The
  `memory` lap now covers the page restore only; the write-watch pass is its own lap `writeWatch`.
- Toy proof (`build.cmd speed`, mode `big`: 900 MB private memory, 380 MB write-watch heap, one byte changed in every
  second page, 87040 runs): `memory` 412-495 ms, `writeWatch` 193-217 ms, `verify` 770-830 ms, hash equal. Then 2 more
  rewinds to the same snapshot, both exact. Rerun 8 had `memory` 11278 ms for 86802 pages; that number now includes
  the write-watch pass too. The game number is still to be measured.
- Rerun 8 (Frida detach in place): rewind 1 code 0, verify ok. Rewind 2 was refused with "the target did not park within
  5000 ms", and its stats show `workerParked` 1, so ts3native's route worker parked and the bridge client's park at a
  Present did not answer.
  - The machine samples of the job show why: before the snapshot NvRemixBridge used 7-9 % GPU, and after rewind 1 it
    used 0-2 % (17:21:58 and 17:22:31). The bridge stopped presenting.
  - The client parks only in `Present`, after `syncOnPresent` got the server's present semaphore. A Present that times
    out there returns before `parkForRewind`, so no park can answer [inferred from the patched source].
  - In rerun 7, rewind 1 was followed by 23-28 % GPU, so it does not happen every time.
  - The bridge logs (`Bin
tx-remix\logsridge32.log`, `bridge64.log`) were gone with the job's Bin restore.
- Changes:
  - The park refusal now says whether the target still reached its park point (`parkTicksSeen`, the block's tick:
    Presents for the bridge client): "it never reached its park point: a bridge client presented no frame", or "it
    passed its park point N times".
  - `rewind-smoke.ts` copies `bridge32.log` and `bridge64.log` to `probe-out/rewind/<tag>-<mode>-bridgeNN.log` at the
    end of every bridge job.
  - The next bridge-apply run tells whether the server stopped after the rewind (its `[Rewind]` lines) or the client
    waits on the present semaphore.

## 27. The second park: the tool reset the bridge's RewindApplied event (2026-11-16)

- The chain collects the bridge logs per step (`probe-out/rewind-rerun-1115-<sfx>/step2-apex-rewind/`). In rerun 8,
  bridge64.log has `Op 2` at 01:21:51, then at 01:22:51: "[Rewind] The client did not come back within 60 s of a
  rewind. Reading commands again." The second park (01:22:28 to 01:22:33) fell inside those 60 s, while the server
  read no commands: the client's Present waited for the present semaphore, and the GPU went to 0-2 % (§26).
- Cause [verified in the patch source and the tool source; that it happened in rerun 8 is inferred from the log
  order]:
  - The server (patch 0015) sets `<id>RewindApplied` before `<id>RewindDone`. The client acks the op only after
    `done`.
  - The tool then puts every event of the target back to its snapshot state. `RewindApplied` was unset at the
    snapshot, so the tool reset it.
  - The client sends `<id>RewindResume` only when it sees `RewindApplied`. It never saw it, so the server waited
    60 s.
  - Rerun 7's second park answered. Why that run was not hit is not known.
- Fix: the tool leaves events whose names end in `RewindDone`, `RewindApplied` or `RewindResume` alone at the rewind
  and in verify (`bridgeEventsKept`). They belong to the op the server just ran, not to the snapshot.
- Toy proof (`waits`): the toy makes an auto-reset event `...-RewindApplied`, and the host sets it after the snapshot.
  The rewind keeps it set (`bridgeEventsKept` 1), and the toy's other events are still put back.
- Rerun 9 (chain-rewind-rerun9-1115.sh) checks it in game: bridge-apply with 2 rewinds, and bridge32.log and bridge64.log
  for each step.

## 28. Bridge side of §27: the server's hold no longer uses kernel objects; park on every way out (2026-11-16)

Apex branch commit 6 (3eea11c), pair d3d9.dll de394c51, NvRemixBridge.exe 706346bf, in
`game-copy/rewind-bridge-1115/pr/apex-repo/native/remix-bridge/out`. Built from a fresh fetch, offline tests pass, not run
in game.

- **Why the server stopped after a rewind** [verified: rerun 8's bridge64.log, §27; patch source]: after op 2 the server
  set the event "applied" and waited up to 60 s for the event "resume", which only a restored client sets after it has
  seen "applied". Our tool sets events and semaphores back to their snapshot state (§8.2), "applied" included, so the
  client never saw it. Nothing about frame or semaphore counters: the present semaphore stands at the same count at
  every park (one credit taken per Present, one given back by the server per Present, `client/d3d9_device.cpp:467-495`,
  `server/main.cpp` Present case), and the tool puts it back anyway.
- **Fix in the bridge**: no state that has to outlast a rewind lives in a kernel object. After op 2 the server writes 1
  into a spare 64-bit word in the head of the device bridge's Client2Server mapping (word 2 of the 64 reserved bytes,
  `util/util_ipcchannel.h` `kReservedSpace`; words 0 and 1 are the ring's overwrite protection) and reads no command
  until the word is 0. It is 0 in every snapshot, and the tool's restore of that view (a bridge view, §12.4) puts the 0
  back, exactly when the client's memory is back. The "applied" and "resume" events and the client's part of the
  handshake are gone; "done" stays (set and used up within one op, so equal at every snapshot). The server logs
  `[Rewind] The client is back at the snapshot after N ms.`, or after 60 s an error, and reads on.
- **The tool's §27 change is no longer needed for this pair, and it did not match the old one** [verified in the bridge
  source]: the bridge names its objects `<name>_<guid>` (`util/util_guid.h:63-74`), so the events were
  `RewindApplied_<guid>`, while `bridgeHandshake()` tests whether the name ends in `RewindApplied`. For the tool's
  lane: either drop that exception or test for `\\Rewind` in the name. With the new pair only `RewindDone_<guid>` exists.
- **Park point** [verified in the patch source]: the client parked only at the end of a Present that got through its
  semaphore wait (the wait retries up to `commandRetries` x `commandTimeout` = 300 x 1 s and returned before the park
  when it gave up). Now: on every way out of `Present` (a scope exit: closing window, bridge off, semaphore given up,
  normal end), and inside the semaphore wait each time it times out (1 s), without counting a tick. A game that makes
  no Present call at all still cannot park; that stays the tool's "it never reached its park point" refusal. Under this
  client the game never sees a lost device (`TestCooperativeLevel` answers `D3D_OK` locally,
  `client/d3d9_device.cpp:174-180`), so it keeps presenting.
- Not covered offline: the hold and its release (they need a restore of the client's view). First thing to read in the
  next run's bridge64.log: the "client is back ... after N ms" line after each `Op 2`.

## 29. Tool side of §28: the bridge's event names are <base>_<guid> (2026-11-16)

- The bridge builds its names with `gUniqueIdentifier.toString(base)` (util_guid.h): `RewindDone_<guid>`, 36 characters of
  GUID after the underscore. §27 matched names that end with `RewindDone`, `RewindApplied` or `RewindResume`, so the real
  events never matched and rerun 9 (old bridge pair) still resets them (found by the bridge agent).
- Fix: `bridgeHandshakeName` takes the last path part and matches `RewindDone_`, `RewindApplied_` or `RewindResume_` plus a
  GUID (hex and dashes in place). Toy test: 3 real-form names match; 7 near misses (no GUID, other base, trailing character,
  bad dash, leading character, the old toy name, empty) do not. The toy's event is now `RewindDone_<pid as Data1>-0000-...`.
- With the new pair (§28) only `RewindDone_<guid>` is left; the tool keeps it.
- Rerun 10 (chain-rewind-rerun10-1115.sh) runs the new pair and this tool. Read bridge64.log for "client is back ... after
  N ms" after each Op 2.

## 29. Rerun 10 crash at device creation: Apex's patch 0010 miscounts the device's references (2026-11-16)

Not from the rewind code. Fixed as a separate patch 0016 on the Apex branch (commit 7, 956d61c); pair d3d9.dll 9b834944,
NvRemixBridge.exe 7b2d7a77 in `game-copy/rewind-bridge-1115/pr/apex-repo/native/remix-bridge/out`. Not run in game.

- Crash [verified: xcpt file of 03.03.00, bridge64.log of `rewind-rerun-1115-11160302/step1-apex-rewind`]: access violation
  reading 0 at `d3d9.dll` + 0x2D4B4 (base 0x6FA20000), called from ApexRadiance.asi, Sims3SettingsSetter.asi, TS3W
  `0x6112E2` (inside `Device_Create`, the return of the `CreateDevice` call). No Present had run: bridge32.log has no
  "[Rewind] Rewind mode is on" line, so the park code was never entered.
- RVA to source [verified: the same objects linked again with `/MAP`, the client DLL in Bin has the pair's hash]:
  0x2D4B4 = `Direct3DDevice9Ex_LSS<false>::GetBackBuffer` + 0x14 = `m_pSwapchain->GetBackBuffer(...)` with a null
  `m_pSwapchain` (`client/d3d9_device.cpp:554-557`).
- Why null [verified]: the server's command history ends `CreateDevice, LinkSwapchain, GetBackBuffer, LinkAutoDepthStencil,
  GetDeviceCaps, IDirect3DSurface9_Destroy, IDirect3DSwapChain9_Destroy, Bridge_UnlinkResource`: the client destroyed its
  implicit depth surface and swapchain right after the device's constructor. That is `destroyImplicitObjects`, reached
  from the device's `Release` when the count equals `m_implicitRefCnt` (`client/d3d9_device.cpp:153-164`).
- Cause [verified in the source, reproduced offline]: Apex's patch 0010 made render target 0 the swapchain's back buffer
  0, which references the swapchain, but kept `m_implicitRefCnt++` for it. A new device then counts 3 references (its
  creator's, the swapchain's, the depth surface's) against 3 "implicit" ones. Offline on the pair of commit 6: `AddRef`
  returns 1, the `Release` after it returns 0 and destroys the swapchain, and `GetBackBuffer` then crashes at the same
  place (d3d9 + 0x2D4xx). Any code that takes and returns a reference on the new device before it holds a second one
  triggers it; in the game that is one of the two mods' `CreateDevice` hooks. Why only some starts do so is [not
  established]: step 2 of the same rerun started on the same pair.
- This also explains the access violations of the bridge test apps' early runs (12.4): same command history
  (`IDirect3DSwapChain9_Destroy` right after `GetDeviceCaps`). What took the reference there is [not established].
- Fix: patch 0016 removes the render target from the count (two lines in `client/d3d9_device.cpp`). Side effect: the
  game's last `Release` now really destroys the device (before, the count never reached the implicit one from above
  and the device leaked at exit) [inferred from the count; the test app's exit runs that path and passes].
- Test: `test_rewind_device` takes and returns a reference on the new device and asks for the back buffer. FAIL on the
  pair without 0016, PASS with it, mode on and off.
- For Apex: this is their bug in a shipped patch (4.0.1 has 0010), independent of rewind mode. It rides on our branch
  because the test arm needs it; it can go to them as its own PR (drop the test hunk, which needs 0015).

## 30. Queue jobs start from a rewind (2026-11-16, branch `feature/queue-rewind-1116`, not run in game)

Rodney 2026-11-16: "Make sure we now use the tool whenever we can." A fresh start (cycle.ts --restart, launch, load) takes ~2 min on
BP and more on IP and big saves; a snapshot takes ~2.5 s and a verified rewind ~2-2.5 s (§26-§29, reruns 12-13).

### 30.1 Design (`tools/harness/queue-rewind.ts`, `tools/harness/queue.ts`)

- Queue key `"rewind": "auto" | "off"` (default `REWIND_DEFAULT` = off until the pilot passes, then auto; `TS3_REWIND` overrides;
  job `"rewind": false` opts out). `rewindGroups` puts runs of >= 2 consecutive jobs into one group when each may start from a
  rewind (`rewindBlock`) and all share the game start key (queue.ts `keyOf`: asi + sha, full ini, fixes, fixesPackage, save,
  content, ccSet/ccLots, gameArgs, Default.ini / S3PP / S3SS / dxvk.conf, d3d9Dll, pin).
- What may differ inside a group = what a job changes in the running game: `profile.ab`, `fixesOn` / `fixesOff`, `native_set`
  repro steps, the repro itself, profile minutes and seed. A different asi, ini value, package, script DLL, config or save is a
  different key and relaunches. Ini values are not mapped to runtime switches: an ini `Mode` key often decides whether a hook is
  installed at all (`0 = not installed`, `1/2 = installed, starts on/off`), so an ini difference always relaunches [inferred from
  the ini comments; a per-key map is the next step only if ini-only arm pairs turn out common, see 30.3].
- Blocked (fresh start, reason in the dry run): `run.script` (rewind-smoke itself, quit-cycle), `run.matrix`, `run.saveReload`,
  `run.age`, `run.soak`, `run.routelock`, `run.menuMemory`, `run.worldimage`, `profile.travel`, `profile.story`, `profile.repros`
  (keeps one frame-times Frida session per load in the game: the tool's frida-agent guard would refuse), save `menu`, vanilla /
  `ourMods: false` (no ts3native, no `[Rewind] Park`), `d3d9Dll`, `callSites: true` or ini `ProbeSnap.CallSites` on.
- Grouped jobs get ini `Rewind.Park=1`, `Rewind.Offline=1` and `callSites: false`, as the rewind queues ran (§19-§22).
- A real run needs Bin state `apex-rewind` (`rewindMode = True` in Bin\bridge.conf, `REWIND_ON`); without it the queue logs one line,
  drops every group and adds no ini (every job starts fresh as before). Chains ask
  `node tools/harness/queue-rewind.ts state <queue.json>` (prints `apex-rewind` for a rewind-auto queue when the tool is built, else
  `apex`) and set that state with turbo-compat.ts. Bridge only, no DXVK arms.
- Snapshot: after the load of a grouped job whose next job is in the same group (and after a fresh fallback load), when
  `rewindReady` holds (rewind-spike.exe built, Bin state apex-rewind, NvRemixBridge.exe running, ts3native.log has
  `[Rewind] Park on` and `[Rewind] Offline on`): one `rewind-spike serve` per load with the bridge-apply flags (rewind-smoke.ts
  `MODES`). Each op: read the game speed (agent `time`) and the autopilot's speed target (first op), `park` (the route worker, before the pause, §21), agent `set_speed pause`,
  probe `/fridadetach` and wait up to 30 s until `frida-agent*` is unloaded (§24), the op, `/fridaattach`, `set_speed` and the autopilot target back,
  `game_state` in world. A paused-refusal sends `plan` to end the park hold.
- Next job of the group: `rewind`. It counts only with code 0, `hashEqual` 1 (when reported) and the ts3fixes / ts3native switches
  read back equal to the read after the load (`switchDiff`, session-reuse.ts `readSwitches`). Code 2 (refused, nothing changed):
  close + the same game start loaded fresh (queue.ts `reloadGame`). Code 3, no answer, probe not back or switches differ: the same,
  and the row gets `rewindFailed: true`. Never a FAIL of the job. Two failures (snapshot or rewind) turn rewind off for the run.
- The game is kept for the next group job whatever the job's verdict (the rewind puts it back), but not after a crash, a timeout
  or a job error. The serve process holds the snapshot; it quits before the game closes (endGame, reloadGame), which also puts the
  pool-worker minimums back (§25).
- Counters: ts3native counters, ts3fixes `patches`, the sim clock and switch values are memory and come back to the snapshot. Every
  job already reads its `/native` (`c0`), patches (`fx0`) and memory baselines at its own start, now after the rewind, so
  `nativeRequire` deltas, `fixActivity` (stuckcheck / easpread) and earlyGates (absolute values) see the same numbers as after a
  fresh load. Files are not rolled back: the ts3native.log copy is the start-up part plus the job's own lines (`logCopy`, as in
  docs/170), and `ts3native-shadow.jsonl` / `autshadow.jsonl` copies of a rewound job start at the size they had when it started.
- Hitch reports: a rewound window gets no `--since-load` / `--frames-shared` (no kept frames session), so it is read as a first
  window after a load (first minute dropped), which is what it is.
- Rows: `row.start` = `fresh` | `rewind (N ms)` | `rewind refused -> fresh (why)` | `rewind failed -> fresh (why)`, `row.startMs`,
  `row.snapshot` (taken / skipped / refused / failed); the md note says `start: ...` on every row of a queue with groups. The queue
  ends with `rewind: N of M planned jobs started from a rewind (x s each), r refused, f failed; ~T min saved vs fresh starts (S s
  each: mean of this run's fresh starts | estimate 120 s)`. The dry run prints `rewind auto: N of M jobs start from a rewind (G
  groups); est. saving ~T min` and per job `0 start: ...` with the block reason.
- Timing jobs: A/B arms in one group differ only in runtime switches by construction; the row records the rewind start; the
  measurement smoke and the machine-load checks are unchanged (the smoke is a one-job child queue: always a fresh start).

### 30.2 Self-checks

`node tools/harness/queue-rewind.ts --self-check`: grouping (consecutive, equal key, opt-out, blocks), every block reason, mode
parsing and `TS3_REWIND`, `rewindReady` on the real `[Rewind] Park on` / `Offline on` lines (rewind-bridge-1115
bp-rewind-bridge-apply ts3native.log) and the real bridge.conf lines with non-matches (`FAIL [Rewind] Park: mapping ...`, a commented
`# rewindMode = True`), the result rows of 2026-11-15 20:30 (code 0 with hashEqual 1, refusal "thread ... exited after the
snapshot", an ended target), the op order on fakes (speed read, park, pause, detach, op, attach, speed back, game_state) and each
fallback (refused, ended, hash differs, probe not back, game not in world, pause refused: park released by `plan`, nothing
detached), `switchDiff`, the summary lines. `queue.ts --self-check` unchanged and ok.

### 30.3 Dry run of every queue (TS3_REWIND=auto, 2026-11-16)

- All 632 queue files: 630 dry-run (2 name a repro that no longer exists), 2918 jobs, **625 would start from a rewind** (405
  groups, in 218 queues).
- The 102 queues of 11-13 to 11-16: 414 jobs, **99 rewinds**. Examples: mono-park-timing-1115 9 of 12, satbdiag-stress-1114 15 of
  16, satbguard-stress-1114 7 of 8, offload-unseen-apply-ab-1113 6 of 12, sweep-packs-home-1115 5 of 15, turbo-apex-1114 4 of 5,
  mono-offload-timing-1115 3 of 5, lane-l3 / lane-l4 / mono-autshadow-seenexact 2 of 3.
- Why the rest start fresh (all queues): game start differs from the neighbour 1466 jobs, run.matrix 117, run.script 69, no
  ts3native 67, no save 67, profile.travel 32, profile.repros 23, run.soak 20, run.saveReload 13, profile.story 5, d3d9Dll 3.
  Of 1900 neighbour pairs of rewindable kinds, 773 differ in the save (queues alternate BP / IP / SV), 69 in the asi, 257 only in
  the ini (13 in the 11-13..16 queues), 223 in other start fields. So ordering jobs by save matters more than an ini-to-switch map.
- Expected saving: ~112 s per rewound job on BP (~3 min on IP and big saves); the 11-13..16 queues together ~3 h. Caveat: the
  current pinned sets (master-mono-1115, out/ tested build) have no `[Rewind]` code (merged to master 1aa9f375 on 11-16):
  those jobs log "snapshot skipped: no [Rewind] Park on line" and start fresh until the next set is built from master.

### 30.4 Pilot (first game run, after the chain)

`tools/harness/queues/rewind-pilot-1116.json` (`rewind: "auto"`): three 5-min BP profile jobs with one game start (asi
rewind-park2-1115, the set the rewind reruns used): `a` loads and the queue takes the snapshot, `b` and `c` start from rewinds.
Bin state apex-rewind. Pass: a, b, c PASS; b and c `start: rewind (N ms)`; end line 2 rewinds, 0 refused, 0 failed. Then
`REWIND_DEFAULT` goes to auto. Not run.
## 31. Pilot refusal: a default pool texture written after the snapshot could not be read (2026-11-16)

Apex branch commit 8 (cb2e62d), pair d3d9.dll c2968f76, NvRemixBridge.exe 4b759c9b in
`game-copy/rewind-bridge-1115/pr/apex-repo/native/remix-bridge/out`. Fresh-fetch build and offline tests pass, not run in
game. Pushed to the fork branch of draft PR 89.

- Refusal [verified: `probe-out/queue/rewind-pilot-1116-2026-11-16-bp-rewind-pilot-a-0454-bridge64.log`]: Op 1 fine at
  12:55:45 (25,468 objects, 339 surfaces, 448 MB); 14 s later "a 4096x1 surface of format 116 written after the snapshot
  could not be read first" (116 = `D3DFMT_A32B32G32R32F`), so op 3 answered `E_FAIL`.
- Which path [verified in the patch source and DXVK 3.1.1's source]: the message is the lock branch of `keepSurface`,
  which only non-target surfaces reach. DXVK refuses `LockRect` when the texture is in the default pool and not dynamic
  (`IsLockable`, `d3d9_device.cpp` texture creation; `LockImage` returns `D3DERR_INVALIDCALL`), as d3d9 does. Uploads
  lock on the client first, so a surface like that is written by `UpdateTexture` (the game's only such call,
  `0x60EC10`, docs/37) or by `UpdateSurface` / `StretchRect` from a mod. Who owns the 4096x1 float texture is [not
  established]: nothing in ApexRadiance_LOG.txt names it.
- Fix: when the lock fails on a default pool surface the server copies it with `StretchRect` into a render target of
  the same format (DXVK and d3d9 both allow a texture as the source when the destination is a render target), reads
  that with `GetRenderTargetData` into a system memory surface, and keeps that surface. A rewind puts it back with
  `UpdateSurface`. Formats without a render target (compressed ones) still give the snapshot up.
- Also: `UpdateTexture` copies what is marked dirty in its source and clears the marks, state the server cannot read or
  restore. A rewind now marks every `UpdateTexture` source used since the snapshot dirty as a whole (`AddDirtyRect`),
  so the game's next `UpdateTexture` copies all of it [exact as long as source and texture were in step outside the
  dirty parts, which is what `UpdateTexture` maintains; a source locked with `D3DLOCK_NO_DIRTY_UPDATE` would differ].
- Test [verified]: `test_rewind_server`, 75 checks, PASS on system d3d9 and DXVK 3.1.1: a 4096x1 A32B32G32R32F default
  pool texture written by `UpdateTexture` from a system memory texture; cannot be locked; written after the snapshot
  the snapshot stays; after a rewind it holds its old contents float for float (values outside 0..1); twice; then the
  game's next `UpdateTexture` finds both alike. Before the fix this case ends in the pilot's refusal.

## 32. Threads that end during a long job (2026-11-16, branch `feature/rewind-thread-exits-1116`)

- First in-queue rewind on the new bridge pair (queue `mono-autshadow-seenexact-1116-rerun1-1116-1116b`, job
  bp-seen-dry-crowd): refused. The snapshot was taken after bp-seen-smoke's load, 32 min (one job) earlier. Two threads
  had ended and one was in another NtWaitForSingleObject wait (its owner was cut off by the queue's 200-character line).
- Owners [verified: SysWOW64 module bases of this boot, then disassembly of the start routines]:
  - 76dd3bf0 = combase.dll+0x123bf0: COM RPC thread-cache worker entry (thread id into the cache object, worker loop,
    cleanup, FreeLibraryAndExitThread). The loop waits on its own event with a 30 s timeout (0x7530) and leaves the cache
    when idle [inferred from the code shape].
  - 74497710 = mswsock.dll+0x17710: Winsock async worker. Loops on NtRemoveIoCompletion; the last worker waits with a
    300 s relative timeout and on timeout swaps the global worker count 1 -> 0 and exits [verified: disassembly].
- Rule: a snapshot thread that ended still refuses, always. No class is allowed:
  - combase and mswsock keep a user-mode cache entry or worker count. The memory restore puts it back, so work would be
    handed to a thread that no longer exists (a hang).
  - Even an idle ntdll pool worker cannot be left out. The kernel decommits the dead thread's TEB pages in the TEB
    region, and `VirtualAllocEx(MEM_COMMIT)` from outside fails there with error 87 (toy: a rewind with the pool hold
    lifted failed dirty at the TEB allocation). Pool workers are already held by §25, so they do not end.
  - Threads are never made again.
- What changed: the refusal names the start module and the worker class (`thread N (start combase.dll+123bf0, COM RPC
  cache worker (ends after 30 s idle)) exited after the snapshot`). The queue logs the tool's full reason list
  (`rewind: why in full:`). Toy `threads`: a game thread ends -> refused naming rewind-toy.exe; a pool worker ends (hold
  lifted) -> refused before any change, toy runs pool work after it. All toy cases PASS.
- Workers made after the snapshot stay safe to reap (§16.3): the memory rollback removes their cache entry or count too.
- Keeping the workers alive (built; the names come from Microsoft's public symbols for this Windows build, found by
  disassembly) [verified: static + toy]:
  - mswsock: `SockAsyncThread` takes the 300 s timeout only while the global reference count is 1 (`cmovg`: above 1
    it waits with no timeout). `WSPAsyncSelect` -> `SockAsyncSelectHelper` -> `SockCheckAndReferenceAsyncThread` adds a
    reference. `SockAsyncSelectCompletion` re-arms the AFD poll (`SockProcessAsyncSelect`) and drops the reference
    only when the select ends. So one live `WSAAsyncSelect` registration keeps the thread in a wait with no timeout,
    with no traffic needed. ts3native `RewindKeepAsync` (only with `[Rewind] Park=1`, never in release defaults) does
    this: a message-only window and one `WSAAsyncSelect(FD_ACCEPT)` on a TCP socket listening on 127.0.0.1 at a free
    port. Nobody connects, so `[Rewind] Offline` holds. ws2_32 and user32 are loaded by name only in that mode. Log line:
    `[Rewind] keepalive on`. Toy `keepalive`: after the same calls, the mswsock thread at the snapshot is in
    `NtRemoveIoCompletion` with a NULL timeout, and the rewind is exact.
  - combase: cannot be kept alive cleanly. `CRpcThread::WorkerLoop` waits 30 s on its own event. Work goes to the head
    of `CRpcThreadCache::_pFreeList` (users: `OXIDEntry::PostCall` for cross-apartment calls into the MTA,
    `CROIDTable::EnsureWorkerThread`, GIT revoke, `CDllHost::GetApartmentActivators`). A 20 s call would keep only one
    cached thread alive, and the others would still end. Fallback: settle for this worker only. The tool has a new
    command `settle <s> [module]` (no freeze; it polls the live threads' start modules), and queue-rewind runs
    `settle 40` before each snapshot. That costs at most 40 s per group, and usually the remainder of the 30 s idle
    time. If combase threads are still there, the snapshot is taken anyway (note line); a rewind then refuses if one
    ends. Toy `keepalive`: settle returns at once when no thread matches, and waits its time and names them when they
    stay.
- combase in-memory patch (built after the settle wait, only with `[Rewind] Park=1`) [verified: static + ts3native
  selftest]: `RewindCombaseKeep` (ts3native, on the keepalive thread at start) scans combase's loaded `.text` for a
  wildcarded 55-byte block of `CRpcThread::WorkerLoop`. The block runs from the `WaitCoalesced` arguments
  (`lea eax,[esp+?]; mov ecx,7530h; push eax; xor eax,eax; inc eax; push eax; push ecx; push ecx; push esi; mov edx,eax;
  mov ecx,edi; call ?`) through the result handling (`test eax,eax; movzx ebx,bl; push 1; pop eax; cmovs ebx,eax; ...;
  je ?`) to the fallback `push eax; push 7530h; push [esi]; call [?]` (WaitForSingleObjectEx). Calls, stack offsets and
  jumps are wildcards, so the block is found wherever the function moves. Only the two imm32 values are written, both
  to 0xFFFFFFFF:
  - +5 is `mov ecx, 7530h`: WaitCoalesced's due time and tolerance. Its timer is then set about 49.7 days out. The
    multiply by -10000 is 64-bit, so there is no overflow.
  - +47 is `push 7530h`: the fallback's timeout. It then waits INFINITE.
  So a cache worker never times out, and the cache keeps its idle threads. Written with VirtualProtect, write, restore
  and FlushInstructionCache, in this process only; the file is never touched. Exactly one match is required. Otherwise
  it logs `[Rewind] combase keepalive: pattern not found, settle wait used (combase <version>, <n> matches)` and changes
  nothing. On success it logs `[Rewind] combase keepalive: patched (combase <version>, ...)`. queue.ts then passes
  `settleS: 0` (`COMBASE_KEPT`) and the snapshot skips the settle wait. ts3native selftest: synthetic 0/1/2-copy cases,
  then the system combase.dll read from disk, 10.0.26100.9818: **1 match**. Then the patch applied in the selftest's own
  process: the pattern no longer matches, and the patched form matches once. Limit: a cache worker already in its 30 s
  wait when the patch lands keeps that one wait. The patch runs at ts3native start, before the game's COM use.
- Stopping the workers at the source instead (checked first, not possible from static data alone):
  - mswsock: no game module imports `WSAAsyncSelect` (ws2_32 #101). Checked: TS3W.exe, TSLHost.dll and the ASI
    loader (`Bin\ddraw.dll`, Ultimate ASI Loader). The worker is also started by `SockDoConnectReal` (connect
    completion through `SockAsyncConnectCompletion`) [inferred: a connect in a non-blocking mode]. TS3W.exe (ordinals
    4 connect and 10 ioctlsocket, used by EA's own socket code, not wininet, so `[Rewind] Offline` does not block it),
    TSLHost.dll (wsock32 connect) and the ASI loader (connect + WSAEventSelect) can all do that. Which one does it in a
    rewind run is not known; it needs the creator's stack at run time. So the keepalive stays.
  - combase: the cache serves cross-apartment calls into the MTA (`OXIDEntry::PostCall`) and COM's own housekeeping.
    Likely users at load are audio (MMDevAPI/WASAPI), text input (msctf) or shell calls [inferred]. None of these can be
    turned off safely, so the settle wait stays.
- Fallback load: after a refused or failed rewind, `rwStart` -> `reloadGame` drops the old snapshot, and the
  `rwNext && !sess.rw` check right after the load takes a new one when the next job is in the same group (queue.ts
  game-start block). So the next job already rewinds to the newest snapshot. No change needed.
- Thread 71284 (the NtWaitForSingleObject wait with other arguments): not identified yet. The next refusal's
  `rewind: why in full:` line gives its start and return sites.

## 33. Refusal census and tally (2026-11-16, branch `feature/rewind-refusal-census-1116`, harness only, no game run)

Rodney: "Can we also check for other refusal like it? So we can do memory patches and guard against them?" §32 covers
combase and mswsock (branch `feature/rewind-thread-exits-1116`). This section lists every refusal seen so far, the other
in-process sources that change over time, and a guard for each.

### 33.1 Refusals seen so far [verified: chain logs, counted by `rewind-refusals.ts backfill`]

Source: `probe-out/queue/chain-*.out` (rewind-smoke `refuse:` lines of reruns 1-13 and the smoke chain; queue.ts
`rewind refused -> fresh` lines of the pilot and chain-correct-1116b). Toy build logs (deliberate refusals) are left out.
A reason counts once per chain (thread ids ignored). 231 reasons in 13 chains:

| Kind | Count | Owner(s) | Status |
|---|---|---|---|
| write-watch allocation | 116 | GC heap (1 chain, rerun 1) | fixed §15 |
| shared writable view changed | 70 | bridge `DeviceClient`/`DeviceServer` 20, `QueryResults` 14, `ModuleClient`/`ModuleServer` 8, `UrlZonesSM` 8, unnamed 20 | fixed §12, §20, §23 |
| wait with other registers or depth | 23 | ts3native 4, ntdll 1, not named 18 | fixed §17 |
| wait taken up again / other wait | 4 | not named | §16.2; one still open (below) |
| no snapshot (tool use) | 5 | harness | fixed in rewind-smoke |
| thread made after the snapshot | 2 | starts 723c29e1, 6935dbc0 | §16.3 `--reap-workers` |
| thread ended after the snapshot | 4 | ntdll pool worker, Frida agent, combase RPC cache worker, mswsock async thread | §25, §24, §32 |
| route worker did not park | 2 | ts3native | fixed §27 |
| bridge server refused op 3 | 2 | bridge (pilot b, c) | fixed §31 |
| pool worker inside a work item | 1 | ntdll (wininet item) | §18, §22 |
| thread inside NtFsControlFile | 1 | not named | §14 |
| live upper vector registers | 1 | not named | §14.4 |

In queue.ts all 4 rewinds so far were refused: pilot b and c (bridge op 3), and both in chain-correct-1116b (combase +
mswsock ended, plus one thread in a different `NtWaitForSingleObject` wait: 71284, then 57156; owner still unknown, the
queue line cut it off).

### 33.2 Census of what changes over time [measured: `probe-out/rewind/ts3w-bp-{start,mid,end}.json`, BP, 7 min, 11-15, DXVK arm]

Thread starts by module (start / end of the 442 s): msvcr80 `_beginthreadex` (the game's own threads) 98/98,
frida-agent 14/9, d3d9 (DXVK) 12/12, ntdll pool 10/11, nvoglv32 9/9, dsound 3/3, ts3native 4/4, graphics-hook32 (OBS game
capture) 2/2, one each for TS3W.exe, Sims3Performance, Sims3SettingsSetter, InputHost, netbios, ucrtbase, and three
private (non-image) starts. Ended: combase+123bf0, mswsock+17710, 5 Frida agent threads, and the private start at
0x41430000 (a thread that starts at an allocation base: an injected loader thread, Frida's [inferred]). Handles: Event
513 -> 506, Thread 173 -> 167, File 240 -> 242, Section 71 -> 70, Timer 1 -> 0; IoCompletion, WaitCompletionPacket,
TpWorkerFactory, IRTimer, ALPC Port, Mutant and Semaphore unchanged.

Imports [verified: import tables of the Steam 1.67 Bin files and this Windows build's SysWOW64]:

| Module | Threads / timers it can make | Idle exit or churn | Status |
|---|---|---|---|
| ntdll thread pool | workers on demand | end after ~130 s idle | verified, held by §25 |
| combase | RPC thread cache | 30 s idle wait, then ends | verified, §32 patch + settle |
| mswsock | async select thread | 300 s while its count is 1 | verified, §32 keepalive |
| rpcrt4 | LRPC calls on the ntdll pool | no own idle exit seen | inferred (no ended thread in the census) |
| wininet (Bin wrapper) | `QueueUserWorkItem`, `WSAEventSelect` | pool items | verified import; `[Rewind] Offline` §22 |
| winhttp | `CreateThreadpoolWait/Work`, `SetThreadpoolTimer` | pool items, connection cache timers | verified import; user: ApexRadiance.asi imports `WinHttpOpen` (what it fetches: not established) |
| dsound | `CreateThread`, `SetThreadpoolTimer`, `timeBeginPeriod` | 3 threads, stable 7 min | measured |
| MMDevAPI, AudioSes | `CreateThreadpoolWait/Work`, `SetThreadpoolTimer`, waitable timers | pool items | verified import; §23 for the shared buffer |
| winmm / winmmbase | `CreateThread`, `timeSetEvent` | timer thread on first `timeSetEvent` | verified import; no game module imports `timeSetEvent` (TS3W, ts3native, Apex, S3SS, S3PP checked) |
| XInput1_4, InputHost, msctf, dxgi | `SetThreadpoolTimer`, `QueueUserWorkItem` / `TrySubmitThreadpoolCallback` | pool items (device polling, text input) | verified import; InputHost 1 thread stable |
| netbios | `CreateThread` | 1 thread stable | measured |
| d3d9 (bridge client) | `RegisterWaitForSingleObject`, `GetRawInputData` | pool wait | verified import; bridge side §28 |
| Mono (inside TS3W.exe) | finalizer thread, Boehm GC (no GC threads) | no TS3W start ended in the census | measured; finalizer wait INFINITE [inferred] |
| ts3native | route worker, offload workers, `CreateWaitableTimer(Ex)W` | park §21 | verified |
| ApexRadiance.asi | `CreateThread`, `SetWaitableTimerEx`, `WinHttpOpen`, `CoInitializeEx` | not measured on Apex 4.0.1 | inferred |
| TS3W.exe, TSLHost.dll | `CreateNamedPipeW`, `FindFirstChangeNotificationW`, `RegisterDeviceNotificationA`, `connect`, `timeBeginPeriod` | kernel object state | verified import |
| graphics-hook32 (OBS), com_antivirus / product_info (antivirus, in the 11-16 logs) | injected, not ours | unknown | measured present |

### 33.3 State a rewind cannot restore

- Kernel objects: event, semaphore and mutex states are checked after a rewind (§8.7); a change notification handle
  (`FindFirstChangeNotificationW`) signalled by a file write, a pipe's buffered bytes, a socket's kernel side, pending
  overlapped I/O and APCs are not (§11). Profile jobs do not save (rewindBlock), so the change handle stays quiet [inferred].
- Timers: waitable and pool timers keep their kernel due time; after a rewind they fire as if no time had passed in
  between, or at once. The pool hold (§25) keeps workers to run them.
- Clock: QueryPerformanceCounter, `timeGetTime` and `GetTickCount64` jump by the time since the snapshot. The game is
  paused across the op, but the first frame's delta and any ts3native deadline taken from `GetTickCount64` see the jump.
  `skewQpc` (§8.5) needs the ts3native clock hooks (§9.1, design only).
- GPU: the bridge server (§12, §28, §31). Compressed default pool surfaces still give the snapshot up (§31 limit).
- Sound: buffers and the device are outside the process; §8.6 and §23.

### 33.4 Guards, ranked by likelihood x cost

1. **The unnamed different wait (71284 / 57156)**, seen in every in-queue rewind after the bridge fix. Guard: §32's
   `why in full` line names its start; if it is a timed worker loop, an in-memory signature patch of the timeout (wildcard
   pattern, single match only, `[Rewind] Park=1` only, never on disk), else add it to the §16.2 rule when its loop keeps
   no state between waits.
2. **Injected third-party modules** (OBS `graphics-hook32`, antivirus `com_antivirus` / `product_info`): threads and pipes
   to their own services can start or end at any time. Guard: the tally names their module; a pre-snapshot module check
   that logs every loaded module outside Windows and Bin; a machine rule (OBS closed during rewind runs).
3. **Clock jump after a rewind**: not a refusal but a first-frame hitch and early deadlines in timing data. Guard: drop the
   first seconds after a rewind start from profile rows (cheap, queue side), then the `skewQpc` hooks.
4. **Pool timer callbacks running at the snapshot** (dsound, MMDevAPI, AudioSes, XInput1_4, InputHost, msctf, dxgi,
   winhttp): caught by §18 catch-and-hold; a long callback refuses. Guard: the existing retry, then a short settle.
5. **Apex's `WinHttpOpen` user and other idle caches**: a winhttp session keeps sockets (mswsock) and pool timers.
   Guard: find what Apex fetches and switch it off in rewind runs (Apex setting, or a `[Rewind] Offline` extension);
   the mswsock keepalive (§32) covers the thread meanwhile.

Lower: combase patch missing on another Windows build (settle wait fallback, §32); bridge surfaces with no render-target
format (§31 limit, Apex side); named pipes and change notifications (no profile job writes saves).

### 33.5 Refusal tally (built)

- `tools/harness/rewind-refusals.ts`: `classify` sorts a reason into one of 22 kinds and an owner (`combase.dll` from
  §32's `start <module>+<off>` form, `addr:<hex>` from the older form, `section:<name>`, `bridge`, `ts3native.asi`). An
  unmatched reason is kind `new`.
- queue.ts writes every reason of a refused or failed snapshot and rewind to `probe-out/rewind-refusals.jsonl` (queue,
  job, op, code, kind, owner, doc reference, full reason) and logs `rewind: new refusal kind: <kind> <owner>: <reason>`
  when the kind and owner pair is not yet in the file.
- `tally` counts by kind and owner; `backfill [--write]` reads past chain logs. The file was seeded with the 231 reasons
  of §33.1, so only new pairs are reported from now on.
- Self-check on real reasons from the chain logs (match and non-match lines, the cut queue form, the new owner form),
  queue.ts self-check and selftest-all pass.

## Spike on the toy process, first version (2026-11-15, history)

The first version (commit 87baa5e7) suspended the threads, copied writable memory and the region map, and on rewind restored
the region map, memory and contexts; it refused when the thread set changed and only counted unmapped views. 3 rewinds on a
single-thread toy came back exact. §8 replaces it.
