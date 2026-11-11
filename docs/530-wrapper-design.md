# 530. Wrapper: host window and restart, owner's design (v0.2.6)

Status (2026-11-11): design, and the step-0 spike (§8, results in §10) is built and has run on a test copy of the game:
runs wsp2, wsp3 and wsp4 on 2026-11-10 (the first try, wsp, failed in the harness before the game started). It is off
by default (`[Wrapper] Enabled`, `native/ts3native/wrapper.cpp`, x64 host `native/ts3wrap/`, queue `wrapper-spike.json`).
Two changes against the text below, both from the spike: the game starts first and ts3native starts the host when the
game creates its window (Steam and harness start unchanged), and the host is a 64-bit process. EA 1.67 never sets a
parent window itself (field +0x74 is only read). Not yet checked: input, focus, alt-tab, minimize, IME, Steam
overlay, Steam start (all need a person at the screen). §11 is the staged plan from here; step 1 (§12: host-owned
lifecycle and restart into the same save) is built, off by default, and not yet run in the game.
Owner: alexvixell35-stack. Follows the draft in docs/241, which gives the
why (a restart gives back ~600 MB of largest free block at load, BP 636 and LR 609 MB; an in-process reload gives
nothing; docs/204 §2.1 in PR #208). This document settles process and window ownership, the input and focus path, the
trigger states, the failure paths, the sidecar and the launch path. **§9 lists what needs a decision before the
build.**

Tags: [verified] = checked on this machine or in the binaries; [unverified] = to check in the spike (§8 step 0);
[inferred] = reasoning only.

## 1. Pieces

| Piece | Where | Job |
|---|---|---|
| `ts3wrap.exe` | new, `native/ts3wrap/` (C++ Win32, static CRT, x64 in the spike, §10) | The host: owns the top-level window, starts TS3W, runs the restart state machine, draws the transition view, writes `ts3wrap.log`. Holds no game state. |
| `[Wrapper]` in ts3native | new `wrapper.cpp` | Inside TS3W: finds the host from the environment, creates the game window as the host's child, keeps the focus and size calls consistent, talks to the host through shared memory, counters, `wrap:` log lines. |
| ts3fixes `Wrapper.cs` | new, beside `Compaction.cs` | The game side: state checks, the ask dialog, the save, the quit, and the load from the sidecar at the main menu. |

The host was drawn as x86 here (nothing in it needs address space); the spike builds it x64 (`native/ts3wrap/build.cmd`,
vcvars64), which is the owner's goal (a 64-bit process that holds the 32-bit game) and costs nothing against a window host. The later 64-bit render process (docs/241 "Not in this phase") is a separate process either way. Room
left for it: the host owns the top-level window and its placement. The game window is a child that a renderer
window can replace.

## 2. Window ownership

**Creation, not reparenting.** The ASI loader is `wininet.dll`, which TS3W imports, so ts3native runs before WinMain
and before the game window exists [verified: imports; README]. When `TS3WRAP_HOST=<hwnd>` is set, ts3native hooks
`CreateWindowExW`/`CreateWindowExA` (both imported by TS3W [verified]) for the game's main window class. It creates that
window with `WS_CHILD | WS_CLIPSIBLINGS`, the host's client area as parent, at `(0,0)` and the host's client size. The
D3D9 device is then made on a child from the start. No `SetParent` after the device exists, which is where most
reparenting tools break [inferred].

**Host window modes** (`[Wrapper] Window=`):
- `0` borderless: `WS_POPUP` at the monitor's full rectangle. This is the default, and it replaces exclusive fullscreen.
- `1` windowed: `WS_OVERLAPPEDWINDOW`, client size = the game resolution.

The game is always windowed inside. ts3native forces windowed mode on the device (`Windowed = TRUE` at
`CreateDevice`/`Reset`) and logs it once if the player's Options.ini asked for fullscreen.

**Size and resolution.**
- The game sets its own window size through `SetWindowPos`/`MoveWindow`/`AdjustWindowRectEx` on its window
  [verified: imports].
- With the game as a child, ts3native turns those calls into a request to the host (shared memory + event). The host
  resizes its frame (windowed) or keeps the monitor size (borderless), then sizes the child with
  `SetWindowPos(..., SWP_ASYNCWINDOWPOS)`.
- In borderless mode the game's resolution becomes its back buffer size. Present stretches it to the child when they
  differ (the D3D9 windowed present does that), so a lower resolution still fills the screen [inferred].
- A monitor or DPI change (`WM_DPICHANGED`, `WM_DISPLAYCHANGE`) moves and sizes the host, and the child follows the same
  way.

**DPI.**
- TS3W calls no DPI API [verified: no `SetProcessDPIAware`/`SetProcessDpiAwareness` strings].
- Windows does not scale a child window of another process with a different DPI awareness [inferred from the
  mixed-mode DPI docs: that hosting works only in-process].
- So both processes are per-monitor v2: the host by its manifest, and TS3W by ts3native calling
  `SetProcessDpiAwarenessContext` at load, only when hosted.
- TS3 then renders at physical pixels. That is what the common "override high DPI scaling: Application" setting does
  today.

## 3. Input and focus

TS3W reads input from window messages only: no DirectInput, no raw input [verified: no `dinput`,
`DirectInput8Create`, `RegisterRawInputDevices` or `GetRawInputData` in the binary]. XInput reads the pad without a
window. IME goes through IMM32 on the game's own window [verified: imports]. A child window gets keyboard and mouse
messages directly while it has the focus, so most of this works as is.

| Issue | What happens with a child | Handling |
|---|---|---|
| Foreground checks | TS3W imports `GetForegroundWindow` and `SetForegroundWindow` [verified]. With the game as a child, the foreground window is the host, so a check `GetForegroundWindow() == game` fails. The game may then act as if it were in the background [unverified: what it does then]. | ts3native hooks both: `GetForegroundWindow` answers the game window while the host is foreground. `SetForegroundWindow(game)` becomes `SetForegroundWindow(host)` plus `SetFocus(game)`. |
| Activation | Child windows get no `WM_ACTIVATEAPP`, and no `WM_ACTIVATE` for the app [inferred: Win32 rules]. | The host posts `WM_ACTIVATEAPP` and `WM_ACTIVATE` to the child on its own activation changes (`PostMessage`, never `SendMessage`). |
| Alt-tab, minimize | The host is the one in the task switcher. | Host minimize: the game gets `WM_ACTIVATEAPP(FALSE)` as above. Restore: focus back to the child. |
| Mouse capture, cursor clip | `SetCapture` and `ClipCursor` work for a child (screen coordinates) [inferred]. | ts3native clips to the child's screen rectangle and releases it on deactivation. A clip left in place after a crash is released by the host when TS3W exits. |
| Close | The host's close button. | The host posts `WM_CLOSE` to the child, which is the game's normal exit path (its quit dialog). The host exits when TS3W exits. |
| Input queues | A cross-process parent and child attach their threads' input queues (Raymond Chen, "Is it legal to have a cross-process parent/child or owner/owned window relationship?"). If one thread hangs, focus and activation calls from the other can hang too. TS3's main thread does block for seconds in loads and saves. | The host never makes a synchronous call to the child. Window work uses `PostMessage` and `SWP_ASYNCWINDOWPOS`. The transition view (§5) is a separate owned top-level window on its own thread, so it keeps painting while TS3W is busy or gone. **The spike has to measure this (§9 R1).** |

## 4. Trigger

The trigger is #208's Compaction, which already decides when, refuses unsafe states and asks the player. Only its
last step changes: a process restart instead of the in-process reload. `[Compaction] Route=2` means restart, and it is
refused with "not hosted" when the game was not started by `ts3wrap.exe`.
- **When:** `compaction.cpp`'s verdict. Largest free block under `ThresholdMB` (raised to 320 under Apex
  MemoryGuard), the re-arm hysteresis and the minimum minutes. It is read after each normal save that returned 0
  (`CpSaveOk`), and also from the memory worker's periodic sample, which can arm a restart for the next save.
- **Where never:** `Compaction.cs` `CpStateWhy` refuses CAS, edit town, build/buy (and so an item held), travel and
  university, an emergency, a modal dialog and a running save. The refusal reason is counted and logged.
- **Ask:** `Enabled` gives the mode: 1 = always, 2 = ask, 0 = never.
  - Ask shows "Restart now / Not now / Always". "Always" writes `Mode=always` to the host's settings file, not to
    ts3native.ini, so the ini stays the player's.
  - "Not now" uses up the arm, as in Compaction.
- **Hotkey:** `[Wrapper] Hotkey=1` (default off; `Ctrl+Shift+F12`). It goes through the same state check, then a normal
  save, then the restart.
- **Threshold crossed with no save coming:** an armed sample shows the ask dialog at the next safe state. "Restart now"
  then saves (EA's normal Save to the same file) and restarts only if that save returns 0.

## 5. The restart sequence

| Step | Who | What | Fails -> |
|---|---|---|---|
| 1 save | game | The player's save, or the one from §4. It must return 0 (`CpSaveOk`). | Not 0: no restart, the game keeps running, "save failed, not restarting" notice, `wrapSaveFails` counted. |
| 2 sidecar | ts3fixes | Writes `<TS3 user folder>\ts3wrap\resume.txt`: save file name, world, the save's write time and size, session id, attempt count, and the restore state Compaction keeps in memory today (game speed, HUD panel, selected Sim, camera). Written to a temp file, then renamed. Never inside the `.sims3`. | Can't write: no restart. |
| 3 cover | host | Shows the transition view over the game: an owned, top-level layered window on the host's monitor. A plain dark screen with "Saving..." / "Loading..." and a progress line from the shared-memory step field. No EA art or fonts (not ours to ship). | - |
| 4 quit | ts3fixes | `GameStates.TransitionToGameStateQuitNoCheck` (the agent's `quit_game` [verified]). | TS3W still running 60 s after the quit (Apex's quit hang, memory: apex infinite save): the host kills it. Safe: the save ended with result 0 in step 1. |
| 5 relaunch | host | The same executable and arguments, plus `TS3WRAP_HOST` and `TS3WRAP_RESUME`. | `CreateProcess` fails, or TS3W exits before the main menu twice: message "Your game was saved as <name>. It could not be restarted: <reason>. Start the game again to continue." The cover goes and the host exits. The save is untouched. |
| 6 load | ts3fixes | At the main menu: reads the sidecar and checks that the file exists with the same write time. Renames it to `resume.used`, then `GetSaveFileDetails` and `TransitionToGameStateToInWorld`, the agent's `load_save` path [verified]. Compaction Route=1 already loads from the menu this way. | File missing or changed, details refused, or the world load fails: the game stays at the main menu with a notice, the sidecar becomes `resume.failed`, and the host drops the cover. |
| 7 restore | ts3fixes | After the world load (ts3native's world-load hook, as `lpReady`), puts back the speed, HUD panel, selected Sim and camera from the sidecar. Then the host fades the cover. | A restore step that throws is logged and skipped. |

A crash anywhere after step 1 leaves the step 1 save. The cover times out at 10 min and shows the step 5 message.
`TS3WRAP_RESUME` keeps a crash loop from auto-loading forever: attempt count 2 = no auto-load.

## 6. Launch

- **Steam:** launch option `"<path>\ts3wrap.exe" %command%`.
  - The host takes the rest of its command line as the game command. If that names `TS3.exe` or a launcher, the host
    starts `TS3W.exe` from the same folder with the same arguments.
  - This machine has `TS3.exe`, `TS3W.exe`, `Sims3Launcher.exe` and `Sims3LauncherW.exe` in `Game\Bin` [verified].
  - Which one Steam starts [unverified].
  - The harness starts `TS3W.exe` directly while Steam runs, and the game plays [verified: every queue run].
  - The Steam overlay injects into the processes Steam starts. Whether it reaches the host's child process
    [unverified].
- **EA app / disc:** a shortcut to `ts3wrap.exe`, which looks for `TS3W.exe` beside itself or in the registry install
  path.
- **Relaunch:** the host starts TS3W itself, not through Steam. If that start fails where the first one worked (DRM,
  Steam not running), the step 5 message covers it.

## 7. Switches, counters, logs

- `ts3native.ini` `[Wrapper]`: `Enabled=0` (off by default), `Window=0`, `Hotkey=0`, `QuitTimeoutS=60`,
  `CoverTimeoutMin=10`. The trigger settings stay in `[Compaction]` (`Route=2`).
- Without `TS3WRAP_HOST`: one `wrap: not hosted` line, and nothing hooked.
- Counters (a new block from the contributor range): hosted, restarts, refusals by reason (8),
  save fails, relaunch fails, load fails, last and max restart ms (save end to world loaded), largest free before and
  after.
- Logs: `wrap: <step>` lines in ts3native.log. The host's own `ts3wrap.log` gets one line per state change with times,
  so a failed restart can be read from either side.

## 8. Plan

| Step | What | Gate |
|---|---|---|
| 0 spike | Host plus the `CreateWindowEx` hook only, no restart. One session each: borderless, windowed, alt-tab, minimize, resolution change in Options, monitor move, IME (Japanese input in a Sim name), DXVK on and off. Steam start through `%command%`. Note what TS3 does when it thinks it is in the background. | Every row of §3 works, or the fallback (§9 R2) is chosen. |
| 1 restart | Steps 1-7 of §5 on Compaction Route=2. Host state machine with a selftest (pure transitions, timeouts, failure paths, fake child process). ts3native: hook install checks, shared-memory protocol, sidecar parse. ts3fixes: sidecar validation as pure functions (CompactionCore style). | Selftests. |
| 2 repros | (a) forced restart on Baseline-BP (`ThresholdMB=4000`): save, restart, the same save loaded, largest free back up, Sims in place. (b) save fails (a test switch, as Compaction's `compactionfail`): no restart. (c) relaunch fails (a test switch makes the host's second start fail): message, save intact. (d) alt-tab and resolution change mid-session. Results files as usual. | All PASS. |
| 3 compat | DXVK, S3SS, S3PP, Apex Radiance, NRaas: one forced restart each. Sets are built from the CC Magic library, which keeps each package as its own file (NRaas Career, Hybrid, Overwatch, Tempest, Traveler among them), so one mod at a time is cheap. Borderless tools and S3SS's own window options are detected and logged, and they must be off. | No crash, restart time logged. |
| 4 session | One real play session on the old save (`1111 kit5`, 2+ years, NRaas) to a natural restart. | Docs + results. |

Dependency: #208 (Compaction) merged first, since the trigger, state checks and restore state are its code.

## 9. Decisions before building

- **R1, cross-process child window (the scoped design).** It works on paper, but three things could make it fragile.
  - The attached input queues: a hung TS3 main thread can stall the host's focus and activation.
  - The game's own foreground checks.
  - DXVK and S3SS window handling on a child.
  - All three have the mitigations in §2-§3 and get measured in spike step 0.
- **R2, fallback: cover window, no child.** If the spike shows R1 breaking something the player would notice, TS3W
  stays a normal top-level window and the host only shows the cover window (topmost, the game's monitor) from step 3
  to step 7.
  - The player sees the same thing ("a long save").
  - The input, focus, DPI, alt-tab and tool compatibility questions all go away, because they stay as they are today.
  - The cost: the host does not own the game window. The 64-bit render process would then bring its own window,
    which it has to own anyway.
  - Proposal: build the spike first and choose A (child) or B (cover) on its results. Steps 1-4 are the same for
    both.
- **R3, Steam start.** Which executable Steam runs, and the overlay in the child process. The spike checks both. If
  `%command%` cannot be used, the fallback is a shortcut, with the Steam overlay possibly lost.

## 10. Step 0 results (spike built and run, 2026-11-10)

Tags as above. Run files: the queue results table `docs/data/results/2026-11-10-queue-wrapper-spike.md`, the queue
`tools/harness/queues/wrapper-spike.json`, and the per-run window notes kept outside the repo.

**What was built (not committed yet; branch `feat/wrapper-spike`, base `4e98172`).**

| Part | File | What it does |
|---|---|---|
| Native side | `native/ts3native/wrapper.cpp` (about 10 KB), 2 lines in `ts3native.cpp`, `[Wrapper]` in `ts3native.ini` (`Enabled=0`, `Back=0`), `TestWrapper` in `selftest.cpp` | Swaps 8 import slots of `TS3W.exe`, all or none. Each hook keeps and chains the old target. Writes no code bytes. Checks the six bytes of the call at `0x4121CF` first and skips with one log line on a mismatch. |
| Host | `native/ts3wrap/ts3wrap.cpp` (about 4.4 KB), `build.cmd` | x64, static CRT, no console. Opens an overlapped window of class `ts3wrap-<game pid>`, shows it (behind other windows and never activated with `back`), exits when the game process exits. Sizes the child only through two registered messages (`ts3wrap-fit` in, `ts3wrap-size` out), by `PostMessage` only. Goes borderless (popup at the monitor rectangle) when the game asks for the whole monitor. Calls `SetProcessDPIAware()` first. |
| Test | `tools/harness/queues/wrapper-spike.json` | One job: BP Spring 6 min, seed 12345, `[Wrapper] Enabled=1 Back=1`. Needs `ts3wrap.exe` copied next to `ts3native.asi`. |
| Tracking | `docs/68-hook-conflict-map.md` (one paragraph, both code sites), `docs/data/change-ledger.jsonl` (3 harness rows) | `node tools/hook-sites.ts --check` passes. |

**The 8 hooked imports** (import slot address in `TS3W.exe`, game code that reaches it):

| Slot | Import | Hook |
|---|---|---|
| `0xF95864` | CreateWindowExW | Acts only for the call at `0x4121CF` in the create function `0x4120C0` (return address `0x4121D5`): starts the host, waits up to 5 s for its window, creates the game window as a child (`WS_CHILD`, host client size). Other callers pass through. No host: one `FAIL` line, the game runs unwrapped. |
| `0xF9587C` | SetWindowLongA | On the game window, frame styles are removed from style writes (`WS_CHILD` kept) and the window-edge extended styles are removed. |
| `0xF958A0` | MoveWindow | On the game window, the size is posted to the host; the child is moved and sized only by the host's answer. The x and y the game passes are dropped. |
| `0xF9589C` | AdjustWindowRectEx | Identity once the game window exists (a child has no frame). |
| `0xF958F8` | GetForegroundWindow | Answers the game window while the host is the foreground window. |
| `0xF958F4` | IsIconic | On the game window, asks the host. |
| `0xF958F0` | IsZoomed | On the game window, asks the host. |
| `0xF958FC` | ShowWindow | Minimize, maximize and restore of the game window go to the host (`ShowWindowAsync`). |

The game window is also subclassed (stripping frame styles on every style change and turning any size request into a
host request), because other modules size and style it through user32 directly, past the import slots (seen in wsp2).

**Evidence.** All runs on a test copy of the game, Baseline-BP-Spring save, 55 households, release ini defaults plus
`[Wrapper] Enabled=1 Back=1`, ts3native 0.36.240 test build. Result lines are from the results table and the per-run
log lines.

| Run | Start (UTC) | Result | What it showed |
|---|---|---|---|
| wsp | 12:43 | FAIL, 0 min | The harness failed before the game started (`cycle.ts --restart` exit 1 at `build-agent.ts`). No wrapper evidence. Not a wrapper fault. |
| wsp2 | 13:34 | PASS, 14.4 min | Log: `8 import slots hooked`, then `game window 0x002A0AFA is a child of host 0x000B0BCC (style 0x00CE0000 -> 0x40000000, 1002x712)`. Window query at +5 and +9 min: host rectangle 1721x1056, client 1706x1018; child 1707x1067 with style `0xD0000000`, which is a popup, not a child, so another module had restyled it past the hooks. That finding led to the subclass. The screenshot script failed (a script error, no image). |
| wsp3 | 14:55 | PASS, 14.5 min | Subclass in. Two lines `size 2560x1600 asked of the game window (it has 1002x712), passed to the host`. Host became a popup (`0x96000000`) and the child was a child (`0x50000000`), both rectangle 1707x1067. `PrintWindow` of the host at +5 and +9 min: 1825 of 1825 sampled points non-black. The display is 2560x1600 at 150 %: the host was DPI unaware, so the game window was a virtualized 1707x1067 "monitor". |
| wsp4 | 20:32 | PASS, 12.5 min | Host calls `SetProcessDPIAware()`. Host and child both 2560x1600 (client 2560x1600), both reported dpi 144 and awareness 2 by the window query tool (the call is the system-aware one; the printed value was not reconciled with it). `PrintWindow` at +5 and +9 min: 4109 of 4109 non-black. |

Across the three passing runs: no crash, the job's own checks passed (the Sims route and act as usual), the results table records no counter
changes. A leak and stress rider ran beside wsp3 (and an armed rider beside wsp4); neither compares wrapped against
unwrapped. The timing columns are marked invalid by the harness (heavy samplers, medium noise), and there was no
unwrapped control run. **So no statement about frame time, memory or latency can be made from these runs.**

**Known limits of the spike** (from the code):

- Windowed child only. Exclusive fullscreen is not handled.
- No restart, no sidecar, no shared memory, no host log file. The host holds no state.
- The window position is not carried (the host keeps its own place).
- The host is system DPI aware, not per-monitor v2 as §2 asks. A monitor change with another DPI is not handled.
- The host waits at most 5 s for its window; a slow start means an unwrapped game.
- Another mod that patches the call at `0x4121CF` itself, or replaces the same import slots without chaining, would
  conflict; none of the installed ones does [inferred from docs/68, not from their binaries].
- `TestWrapper` (pure style functions and the slot hook) is in `selftest.cpp`; it was not run in the pass that wrote
  this section.

**Not verified (needs a person at the screen, or a run that was not made):**

| Item | Why it is open |
|---|---|
| Keyboard and mouse input, focus, click-to-focus, text entry | Every run's harness note is "game not in front; window NOT focused", and the host ran with `Back=1` (never activated). The agent acts in-process, so play in these runs says nothing about input. |
| Alt-tab, minimize and restore, host close button, WM_ACTIVATEAPP forwarding, cursor clip | No run did any of it. |
| User resize of the host window, resolution change in Options, monitor move, DPI change | Only the sizes the game itself asked for were seen. |
| Borderless against windowed default | The host went borderless only because the game asked for the full monitor. The windowed case at the player's own size was not run on its own. |
| IME (Japanese input in a Sim name), Steam overlay and other overlays | Not run. The run diagnostics note 2 modules outside Windows and Bin (overlays or injectors) but do not name them. |
| DXVK on and off | The diagnostics line says a `d3d9.dll` sits in `Bin` (a wrapper such as DXVK); whether it was DXVK in each run, and the off case, were not recorded. |
| Steam start through `%command%`, EA app start, elevated game | The harness starts `TS3W.exe` directly. |
| Shutdown order | The host is written to exit when the game process ends, and the runs ended with the job closing the game, but no run file records the host's exit or that no `ts3wrap.exe` was left over. |
| Cost in ms per frame of the child path | No control run, no frame timing. |
| Hung main thread (§9 R1) | Not provoked. This is the main risk of a cross-process child. |

The verdict on R1 versus R2 is therefore **not yet made**. What step 0 shows is that creation as a child works and renders
(the one thing §2 could not claim), not that the player's day-to-day window behaviour is right.

## 11. Next steps (staged plan)

The goal is the owner's: own the process architecture so that a restart on demand is possible now and a later move of
services out of the 32-bit process, and maybe a 64-bit migration, has a place to land. No migration is planned. Each
step has a gate that must pass before the next is built, and the maintainer is asked before any step that touches the
launch path or the game's save path. The 32-bit side keeps everything that holds a game pointer.

Standing rules for every step: the wrapper is off by default; any host failure leaves the game running as it is today
(unwrapped or in-process); the host makes no synchronous call to the game window; every step ships with a way to turn it
off from the ini.

### 11.1 Step 0b: close step 0 (before anything is built)

| | |
|---|---|
| Goal | Run the person-at-the-screen checklist of §8 step 0 and the R1 hang test, then pick A (child) or B (cover window only, §9 R2). |
| 32-bit side keeps | Everything: the spike code as is. |
| What to run | Input and focus, alt-tab, minimize, close, resolution change, monitor move, IME, DXVK on and off, Steam start through `%command%`, game exe elevated or with a compatibility flag while the host is not, and a forced 20-second main-thread block (a long load) while clicking the host. Two timed arms (wrapped and unwrapped) for ms per frame, with PresentMon, on one save. |
| Risks | Input path and queue attach (§3). Elevation: a window message from a lower-integrity host to a higher-integrity game window is dropped by Windows [inferred]. Starting the host from the game process (as the spike does) gives both the same token, which is a reason to keep game-first start. A compatibility layer set on `TS3W.exe` (DPI awareness, run as administrator) does not apply to `ts3wrap.exe` by itself. |
| Gate | Every row of §3 works, with a frame cost that is within the harness noise, or R2 is chosen and written down. |

### 11.2 Step 1: host-owned lifecycle

| | |
|---|---|
| Goal | The host launches the game (from a Steam launch option or a shortcut), notices exit or crash, and restarts it on demand. Ties to the restart idea in §4-§5: `[Compaction] Route=2` (restart through the wrapper) does not exist in `ts3native.ini` yet, which has `Route=0` (direct) and `Route=1` (menu) only (docs/204). |
| 32-bit side keeps | The trigger and the save (compaction verdict, state refusal, `CpSaveOk`), the sidecar write and the load at the main menu (ts3fixes). The host only quits and relaunches. |
| Risks | Launcher and Steam: `TS3.exe` is EA's starter that runs `TS3W.exe` and waits (docs/255, docs/256 record launcher hangs); a host-first start changes the process tree Steam watches, and the Steam overlay may not reach a process Steam did not start [unverified]. Relaunching outside Steam may fail where the first start worked. A crash loop (§5 attempt count). The value of the restart trigger itself: Compaction work was stopped on 2026-11-07 (PLAN.md): the in-process reload gave no address space back, and the full-restart control run was cancelled, so the restart gain rests on the docs/204 and docs/241 figures (about 600 MB of largest free block at load) and has not been re-measured on the current build. |
| Gate | (1) First, one restart control run (save, quit, relaunch, load) on the current build, with largest free block before and after: if it gives back less than about 300 MB the trigger is not worth building. (2) Selftests of the host state machine (pure transitions, timeouts, a fake child process). (3) Repros of §8 step 2: forced restart on a baseline save, save fails and no restart, relaunch fails and the save is intact. |

### 11.3 Step 2: host-side services that need no game memory

| | |
|---|---|
| Goal | Things the host can do from outside, so they work when the game is wedged or gone: a host log with a line per state change and exit codes; crash dump capture (the host writes a minidump of the game process on an abnormal exit or a fault the in-process `[CrashReport]` filter, docs/135, cannot write because the process is damaged); hang detection (the game window stops pumping, checked by `IsHungAppWindow` and process times, never by sending it a message); PresentMon-style frame timing from ETW present events. |
| 32-bit side keeps | The in-process crash filter and `[CrashReport]` scrub rules, the frame stage timers (docs/167), the counters. The host adds data; it replaces nothing. |
| Risks | Privacy: a dump holds the player's data, so it stays local and the existing scrub-and-ask rules of docs/135 apply to anything sent. A watchdog that kills a slow but alive game (a long save, a travel load; docs/255 and docs/256 show launcher and start hangs are long) would lose play, so it only reports at first. ETW real-time sessions need administrator or a log-users group, so frame timing may be unavailable to a normal player. Anti-cheat: none known for this single-player game [inferred]; an injected or overlay tool is still the player's own choice. |
| Gate | A forced crash produces a dump that opens with the right module. A forced 30-second hang is reported, and a 6-hour soak shows no false hang report. Frame timing with the host on and off agrees within the noise of the harness (no new cost). |

### 11.4 Step 3: shared-memory channel between host and game DLL

| | |
|---|---|
| Goal | A versioned, pointer-free block (named file mapping plus events) with a heartbeat, the counters ts3native already keeps, and a small command ring (host to game: "save and quit", "show cover"; game to host: step, state). It is the transport that §5 and any later subsystem need. |
| 32-bit side keeps | The writer side of counters and state, command execution on the game's own thread, and the decision to act. |
| Risks | A protocol that breaks across versions (header with a version and size, host refuses a mismatch); a host crash must never stall the game (the game never waits on the host, only polls); the same-integrity rule of 11.1 also applies to the mapping's security descriptor. |
| Gate | Selftest of the protocol (torn writes, version mismatch, full ring). Kill the host mid-run: the game goes on. Kill the game mid-run: the host reports it. Measured cost of a message in microseconds on the game thread, inside the 70-100 ms/s budget of docs/215 with a large margin. |

### 11.5 Step 4: first out-of-process subsystem candidates

The repo's own numbers on what is worth moving (docs/215, docs/08). Raw pointers cannot be wrapped, so only memory
reached by key, handle or a narrow call surface qualifies. Most of the address space does not qualify (the Boehm GC
sections, about 376-408 MB, the Mono heap, about 100 MB, and animation resources inside the EA heap cores).

| Candidate | Size and traffic (repo numbers) | Position |
|---|---|---|
| Resource read buffers (`ResourceLoad/PackedStream/Raw`, decompressed record data, `EAStreamMemory`) | 112-192 MB of permanent EA heap cores; about 300-800 requests per second [inferred], 0.3-24 ms/s of IPC against a 70-100 ms/s budget (docs/215 §4) | Recommended first. docs/215 itself says to build the in-process fixed window first and move the reader out after measuring. Go when peak concurrent read-buffer bytes at load are at least about 80 MB and play-time calls are at most about 1,000 per second. |
| Renderer (a Direct3D 9 proxy streaming to 64-bit DXVK) | About 430-640 MB, but hundreds of thousands of one-way calls per second; presenting into another process's window is unproven (docs/215 §4.2) | Biggest prize, biggest risk. See the bridge below. |
| Resource cache (docs/08) | The engine's cache budget is 200 MB; package indexes are about 3.5 MB for the whole CC set, so index offload is not worth it (docs/08 §0) | Tune the in-process budget first (docs/08 phase 0). |
| String tables, routing clone, save write buffers | String tables about 20 MB (with S3PP) to about 90 MB (stock); routing clone designed in docs/194; the save blocks are not yet named (docs/215) | Later, after the first helper exists. |

| | |
|---|---|
| Goal | One helper service (the resource reader) running in a 64-bit process the host owns or starts, with the in-process path kept as the fallback on any helper failure. |
| 32-bit side keeps | The cache of decoded objects, every pointer, the fallback read. |
| Risks | A moved reader that is slower under load costs hitches where today there are none; the address-space gain may come mostly from the in-process window (docs/215 says so), so the helper must be justified by the off-thread I/O and decompression, not by memory. |
| Gate | The AllocSites measurement of docs/215 §4.1 (peak concurrent bytes, free bytes inside the EA cores, calls and microseconds per second at `0x4A8380`), then an A/B on the old save with ms/s and largest free block as the two numbers; the helper stays off if either is flat. |

**The NVIDIA bridge.** Apex Radiance ships an optional RTX Remix bridge that runs DXVK in its own 64-bit process (about
600 MB more free address space, docs/254), and another contributor owns it. The wrapper does not replace or compete
with it. It must coexist: the bridge presents into the game's window, and with the wrapper that window is a child of
the host, a cross-process parent that neither project has tried with the other. docs/254 (R8) lists the bridge as never
run with ts3native. So before any renderer claim the plan has one run of the wrapper with the bridge installed
(does the bridge find and present into the child window, does input still reach it), reported to the bridge's owner
first. The wrapper's job on the renderer track is only to keep the child-window and fallback paths working; the
renderer move stays with the bridge.

### 11.6 Questions for the maintainer

1. Is this staged plan acceptable, in particular step 0b (A or B decision) before step 1?
2. Which step may be built next? Proposed: 0b, then the restart control run of 11.2 (no build), then the host state
   machine.
3. Any objection to shipping the host as a separate executable (`ts3wrap.exe`, 134 KB, x64) next to `ts3native.asi`, off by
   default, instead of inside the asi?

## 12. Step 1 as built: restart into the same save (2026-11-11, not run in game)

Tags as above. Branch `feat/wrapper-restart`, base `475fe07`. Everything is off by default and needs `[Wrapper]
Enabled=1` first (step 0). Nothing here has run in the game yet: §12.8 lists what the selftests cover, §12.9 the first
game runs. Two changes against §4-§7: the save loads through EA's own `-worldFile` switch at the relaunch, not through
a ts3fixes main-menu load (§12.5), and the host never kills the game (a quit that hangs gives the restart up, §12.2).

### 12.1 Pieces

| Part | File | What it does |
|---|---|---|
| Channel | `native/ts3wrap/restartchan.h` | One named file mapping (4,960 bytes, one layout for the x86 game and the x64 host, `static_assert`) and two auto-reset events. Names: `Local\ts3wrap-<first game pid>-<32 hex session id>`, plus `-h` (game to host: a new message) and `-g` (host to game: the cover is up). A relaunched game finds them through `TS3WRAP_CHANNEL=<pid>-<id>`, which the host sets in its own environment. Magic `T3WC`, version 1 and size are checked on both sides; a mismatch means no channel. Separate from the service channel of step 3. |
| Host state machine | `native/ts3wrap/hoststate.h` | Pure: `HsStep(state, event, now)` returns actions. No Win32 calls; the selftest drives it. |
| Host | `native/ts3wrap/ts3wrap.cpp` | Window, cover, relaunch, `ts3wrap.log`. Usage `ts3wrap.exe <game pid> [<session id>] [back]`. |
| Game side, native | `native/ts3native/restart.cpp` (included before `wrapper.cpp`), small hooks in `wrapper.cpp`, `userdiag.cpp`, `ts3native.cpp` | The `[Wrapper]` keys, the channel, the verdict (Compaction's rules), the handoff file, text channel op 40, the summary line. |
| Game side, script | `mods/ts3fixes/src/Restart.cs`, `RestartCore.cs` (game-free), small changes in `Compaction.cs`, `Fixes.cs` | The state gates, the prompt, the record (Compaction's `CpRecordNow`), the quit, the restore (Compaction's restore body), the hotkey. Shares Compaction's two hooks (`OptionsModel.SaveGameTask`, `MainMenuState.Startup`): a method is patched once. |
| Tests | `native/ts3native/restart-test.h` (`TestRestart` in `selftest.exe`), `tools/selftest/restart/` | §12.8. |

### 12.2 Host states

`Idle -> GameRunning -> RestartRequested -> WaitingForExit -> Relaunching -> WaitingForWindow -> Loading -> GameRunning`,
plus `GameExited`, `GameCrashed` (the game ended outside a restart) and `Failed` (a restart could not finish and no
game is left). The host ends only in those three. Every give-up while a game is alive goes back to `GameRunning`.

| State | Event | Next | Actions |
|---|---|---|---|
| Idle | attach (host start) | GameRunning | - |
| GameRunning | restart requested | RestartRequested | cover |
| GameRunning | close (player) | GameRunning | `PostMessage(WM_CLOSE)` to the game window: the game asks its own question |
| GameRunning | exit / crash | GameExited / GameCrashed | host ends |
| RestartRequested | covered | WaitingForExit | set `-g`: the game quits now |
| RestartRequested, WaitingForExit | game failed (gave up before its quit) | GameRunning | uncover |
| RestartRequested, WaitingForExit | close | same | no relaunch later (`closing`) |
| RestartRequested, WaitingForExit | exit or crash | Relaunching (closing: GameExited / GameCrashed) | relaunch (closing: host ends) |
| WaitingForExit | `quitMs` (QuitTimeoutS, 60 s) passed | GameRunning | message "did not finish closing"; the game is not killed |
| Relaunching | launched / launch failed | WaitingForWindow / Failed | - / message box, host ends |
| WaitingForWindow | window (a child of the host from the new pid) | Loading (closing: GameRunning) | - (closing: uncover, close to the game) |
| WaitingForWindow | exit or crash | Failed | message box "closed while starting again", host ends |
| WaitingForWindow | 120 s passed | Failed | message box, host ends: the game creates its window later as an ordinary top-level window (§12.6) |
| Loading | ready | GameRunning | uncover, restarts +1, summary line |
| Loading | game failed (boot fell back) | GameRunning | message on the cover for 10 s or a click |
| Loading | close | GameRunning | uncover, close to the game |
| Loading | exit or crash | GameExited / GameCrashed | message box, host ends |
| Loading | `readyMs` (CoverTimeoutS, 600 s) passed | GameRunning | message "loading takes long", the game is shown as it is |

Exit or crash: an exit code under `0xC0000000` and the channel's `bye` flag (written at the game's
`DLL_PROCESS_DETACH`) is an exit; anything else is a crash. A crash during the restart's quit still relaunches (the save
ended with result 0 before anything else happened). The heartbeat (`beat`, written by every message the game window
gets) is logged only: "game window silent for N s" after 30 s, and the time since the last one at every exit. A long
load also stops it, so it decides nothing.

Single instance: mutex `Local\ts3wrap-host` per Windows session. A second game's host exits at once, and that game runs
unwrapped after its own `FAIL wrapper` line.

### 12.3 One restart, step by step

1. **Save.** Any save that goes through `OptionsModel.SaveGameTask` (Save, Save As, autosave, the hotkey's save).
   `TaskHook` sends op 40/1 before the save, then after it Compaction's guard calls `WrapAfterSave` first.
2. **Gates.** `CpWaitStateWhy` (Compaction's `CpStateWhy` plus its wait for EA's own save dialog): world, live mode
   (so not Build/Buy/CAS or Edit Town), not travelling, vacation, another town, a move, university or future world, no
   modal window, no emergency. A refusal logs "wrapper: save #N not considered: <why>" and lets Compaction look.
3. **Verdict** (native op 40/2). `CpSaveOk` (this task's save, result 0, within a minute), then Compaction's
   `CpVerdict` on `RestartThresholdMB` (raised to 320 under Apex MemoryGuard), re-arm at +256 MB, no minimum minutes.
   A pending hotkey (op 40/13, at most 2 minutes old) is a yes at any free block. One "wrapper: verdict" line.
4. **Ask** (only `RestartAsk=1`): "Refresh / Not now". Not now uses the arm up. The gates are checked again after the
   answer.
5. **Record.** `CpRecordNow` (speed, HUD panel, selected Sim, camera, queues of the Sims in scope, Compaction's rules),
   as `RestartCore` lines, sent as "wraprec " text lines (not logged).
6. **Commit** (op 40/6, native). Waits for the save's flush worker and SaveSlim worker, reads the save folder's
   identity (one level of `<Saves>\<name>.sims3`: total bytes, file count, newest write time), writes the handoff
   file (temp file, flush, rename), fills the channel (save name, reason, largest free block), sets `-h` and waits at
   most 1.5 s for `-g`. The host covers its client area and sets `-g`. Any step failing: §12.4.
7. **Quit.** ts3fixes calls `GameStates.TransitionToGameStateQuitNoCheck` (the agent's `quit_game`) from its own task.
   It skips EA's "save first?" question because the save of step 3 returned 0. No process is killed.
8. **Relaunch.** The host sets `hostCmd = restart boot` and starts the first game's executable with its command line
   plus `-worldFile:"<name>.sims3"`, in the first game's start folder, with the environment the host inherited from
   it plus `TS3WRAP_CHANNEL`.
9. **Boot.** The new process copies the previous `ts3native.log` to `ts3native.before-restart.log` before its own log
   starts, opens the channel at `DllMain` and reads `hostCmd`. With `Restart=1` it checks the handoff file (§12.4);
   without it the save just loads and native says "ready" at the first world load. Its window becomes the child of
   the channel's host window; it never starts a second host.
10. **Load.** EA's `MainMenuState.Startup` takes `-worldFile` at the first menu entry: `GetSaveFileDetails` and
    `TransitionToGameStateToInWorld` (docs/06 §3, decompiled code) [verified code; the switch syntax is inferred].
11. **Restore.** At the world load ts3fixes asks op 40/14 (the file passed the checks), reads the save name and the
    record back bit by bit (ops 0x10-0x12), checks the loaded save is the named one, and one tick later restores with
    Compaction's restore body: camera, selected Sim, queues (EA's Test, then `InteractionQueue.Add`), panel, then the
    speed. Op 40/9 with what came back. Native writes the summary line and tells the host "ready"; the cover goes.

### 12.4 Handoff file and failures

`<Documents>\Electronic Arts\The Sims 3\ts3native\restart.bin` (the user folder of `played.cpp`), never inside the save.

| Field | Meaning |
|---|---|
| magic `T3WR`, schema 1, header size | another build's file is refused |
| session id (16 bytes) | the channel's id: a file from another host session is refused |
| written (UTC FILETIME) | older than 15 minutes, or in the future: refused |
| save name (`<name>.sims3`), save bytes, file count, newest write time | the save on disk must be the one just saved |
| largest free block, reason | for the summary line |
| payload length, FNV-1a 32 | truncated, too long or changed: refused |
| payload | the RestartCore lines (ASCII): `R1|speed|panel|selected|camera flag|6 floats`, `S|sim id`, `I|kind|id|level|value|autonomous|cancellable|x|y|z|definition type|field|object type` |

Checked in this order at the boot, before anything is used: present, size, read, session, save folder there, then
`WrHandoffWhy` (too short, not a handoff file, another schema, truncated or too long, checksum, another session,
another save, the save changed since, too old). A refused file is deleted and the save just loads without a restore;
native then tells the host "ready" at the first world load. ts3fixes refuses the record too when one line does not
parse (strict: nothing restored) or the loaded save is not the named one. The file is deleted after use and at every
normal start with `Restart=1`.

| Fails | Result |
|---|---|
| A gate refuses, the verdict says no | Nothing happens; Compaction may still look at the save. |
| No save name, record over 64 KB or not sent, file not written, host window gone, no cover in 1.5 s | One `wrapper: restart not started: <why>` line, "Memory refresh skipped" notice, Restart off for the session, the host drops its cover. The game plays on. |
| The quit throws | Op 40/7, the same. |
| The quit hangs past QuitTimeoutS | The host gives the restart up and shows a message; the game finishes its quit when it does (the host then ends with it, the save is the one of step 3). |
| Relaunch fails, the new game exits before its window, or no window in 120 s | Message box "your game was saved: start The Sims 3 as usual", the host ends. |
| `-worldFile` not taken (the game stays at the menu 15 s) | ts3fixes sends op 40/10: native summary line, the host shows "load it from the menu" for 10 s. |
| The world loads but nothing can be restored | Op 40/10 (another save, record did not parse), or 40/9 with what came back; the save is loaded either way. |
| A low-memory restart that did not get back to the threshold + 256 MB | "Restart off for this session" in the summary line (as Compaction's "did it help"). |

### 12.5 Boot to save for a normal player

The player starts the game as always (Steam, the EA app or a shortcut); the game starts the host when it creates its
window (step 0). At a restart the host starts `TS3W.exe` itself with the first game's exact command line and
environment plus `-worldFile:"<name>.sims3"`. EA's own main menu then loads that save directly, as a double-click on a
save would. The player sees the host window stay up with "Refreshing memory... your game is saved" over the last frame
(or black) and a step line ("Closing the game", "Starting the game", "Loading your save", seconds), then the game in
the same place with its speed, selection, camera, panel and the visible Sims' queues back, and one notice.

Why not a ts3fixes main-menu load (§5 step 6): a load sent before the menu is ready is lost (the harness waits for the
menu), and the "Mod Scripts Found" dialog sits on the menu. `-worldFile` is taken inside `MainMenuState.Startup`,
which ts3fixes also hooks (Compaction's hook) to watch for a boot that stays at the menu.

### 12.6 Never a headless game

- The host never ends while a game it hosts is alive, except after "no window in 120 s". Then the game has no window
  yet: `WrCreateWindowExW` checks `IsWindow` on the channel's host window and creates an ordinary top-level window when
  it is gone. If the child create fails anyway (the host went away between the two calls), it creates the top-level
  window too.
- Closing the host posts `WM_CLOSE` to the game window: the game asks its own quit question. During a restart, closing
  stops the relaunch and the host ends with the game.
- The game's window closing outside a restart (its own quit) hides the host until the process ends, as in step 0.
- The host crashing destroys the child window (Windows rule for child windows). Step 0 has the same risk; nothing here
  makes it worse.

### 12.7 Switches, logs, counters

`ts3native.ini` `[Wrapper]`, each needing the one before it: `Channel=0`, `Restart=0` (also needs `[UserDiag]
Enabled=1` and `[SaveWrite] Enabled=1`), `RestartThresholdMB=320`, `RestartAsk=0`, `RestartHotkey=0`
(Ctrl+Shift+F12: an EA save, then the restart if it returned 0), `RestartFrame=1` (the cover shows a screen copy of the
host's client area, taken only while the host is in front; 0 = black), `QuitTimeoutS=60`, `CoverTimeoutS=600`.

Logs: `ts3wrap.log` next to the host (one timestamped line per state change, every game message, each exit with code,
`bye` and heartbeat age, one `restart summary #N` line with the largest free block before and after, the seconds to
quit, to the window and to ready, and the restored bits). `wrapper:` lines in `ts3native.log`: channel, verdict per
save, the commit, the boot check, the restore, and one `restart summary` line. The game before a restart keeps its log
as `ts3native.before-restart.log`.

ts3fixes counters `[2150-2155]`: saves checked, offers, declined, restarts started, given up before the quit, boot
restores done (agent names `wraprestart_*`). The queue entries share Compaction's `[434-438]`.

### 12.8 Selftested and not

| Selftested | Needs a game run |
|---|---|
| Channel names (fits, bad id, too small a buffer), layout and offsets, the version, size and magic checks | That the x64 host and the x86 game open the same block and events (both builds compile against one header) |
| Handoff file: build, every refusal (another session, another save, the save changed, too old, in the future, truncated, too long, checksum, another schema, not ours, too short), name and size limits | The save folder identity being stable between the commit and the next start (flush and SaveSlim workers are waited for) |
| The verdict: failed save, hotkey, threshold, arm, re-arm at +256 MB, Apex 320, threshold 4000 | `CpStateWhy` and the prompt in a live game |
| The bit reads ts3fixes makes (payload, UTF-8 name, both lengths, out of range) and every op answering no while off | The text channel round trip of a whole record |
| The host state machine: a whole restart, crash in the quit, launch failure, quit stuck, close in each restart state, death before the window, no window, death while loading, boot fallback, ready timeout, tick wrap | The cover window, the screen copy, focus and input after the restart, the relaunch's Steam behaviour |
| RestartCore lines: round trip under a comma-decimal culture, 13 refusals, line and total limits, non-ASCII, save-name match | `Type.GetType` finding the definition types again in the new process |

### 12.9 Test plan

Copy `native/ts3wrap/out/ts3wrap.exe` next to `ts3native.asi`. Common ini: `[Wrapper] Enabled=1 Channel=1`, `[UserDiag]
Enabled=1`, `[SaveWrite] Enabled=1`; ts3fixes and the agent installed.

| Run | Ini | Steps | Pass |
|---|---|---|---|
| (a) off | `Channel=0` (and once `Enabled=0`) | Load, save, quit. | No `wrapper: channel` line; with `Enabled=1` the host works as step 0; `ts3wrap.log` shows `no channel`. No `restart.bin`. |
| (b) forced | `Restart=1 RestartThresholdMB=4000` | Load a baseline save, wait 2 minutes, save once (agent `save_game`). | `wrapper: verdict RESTART`, `restart #1 ... quitting`; host `GameExited`/`Relaunching`/`WaitingForWindow`/`Loading`/`GameRunning`; the new log loads the same save without the menu; `restart summary` on both sides with before and after MB; `ts3native.before-restart.log` exists; `restart.bin` gone. First check: `-worldFile:"<name>.sims3"` taken (else the 15 s menu fallback line). The new process then says "Restart off" (4000 is never reached after a restart). |
| (c) threshold | `Restart=1 RestartThresholdMB=320` on the old long save (the one that gets low) | Play with saves until the largest free block drops under 320 MB. | One restart at the first save under 320 MB; after it at least 576 MB, or "Restart off" in the summary. |
| (d) host closed | as (b) | Save; while the cover shows "Closing the game", close the host window. | No relaunch; host `GameExited` (or `GameCrashed`) and ends; the save of step 3 loads by hand. |
| (e) crash during restart | as (b) | Save; while the cover shows "Starting the game" or "Loading your save", end `TS3W.exe` from Task Manager. | Host "closed while starting again" box (or `GameCrashed` while loading), host ends; the save loads by hand; the next normal start deletes `restart.bin`. |

Read: `ts3wrap.log`, `ts3native.log` and `ts3native.before-restart.log` (all next to the asi), the agent's counters
(`wraprestart_*`), and `Documents\Electronic Arts\The Sims 3\ts3native\` (no `restart.bin` left).

### 12.10 Unknowns

1. **`-worldFile` syntax and Steam.** The colon and quotes are inferred from EA's launcher strings (docs/06 §3). The
   relaunch does not go through Steam: whether `TS3W.exe` started by the host restarts itself through Steam (and so
   loses `TS3WRAP_CHANNEL` and the switch) is unknown; the harness starts `TS3W.exe` directly while Steam runs and that
   works [verified], which is the same case. A first command line that already has `-worldFile` gets a second one.
2. **Elevation and compatibility flags.** The host is started by the game and so has its token; the relaunch inherits
   that. A compatibility layer set on `TS3W.exe` applies to the relaunch only if Windows applies it to `CreateProcess`
   from another process [inferred: it does for the registry layers]. `ERROR_ELEVATION_REQUIRED` gives the launch-failed box.
3. **Input focus.** The cover is a never-activated owned popup; the new child gets the focus only when the host is in
   front. Keyboard input during the cover goes to the quitting or loading game.
4. **DXVK and device loss.** The relaunched game creates a fresh device in an existing parent window. Not run with DXVK
   or the Remix bridge; the screen copy of a DXVK window through `GetDC(NULL)` is expected to work with the desktop
   compositor [inferred].
5. **"Mod Scripts Found".** If that dialog blocks the `-worldFile` transition, the cover hides it: after 15 s the menu
   fallback drops the cover so the player can click it, and the load then runs without a restore.
6. **Quit time.** A quit longer than 60 s (Apex's quit hang, other mods) gives the restart up; the player's save is
   safe but the game then ends with nothing started again.
