# 530. Wrapper: host window and restart, owner's design (v0.2.6)

Status (2026-11-04): design, nothing built. Owner: alexvixell35-stack. Follows the draft in docs/241, which gives the
why (a restart gives back ~600 MB of largest free block at load, BP 636 and LR 609 MB; an in-process reload gives
nothing; docs/204 §2.1 in PR #208). This document settles process and window ownership, the input and focus path, the
trigger states, the failure paths, the sidecar and the launch path. **§9 lists what needs a decision before the
build.**

Tags: [verified] = checked on this machine or in the binaries; [unverified] = to check in the spike (§8 step 0);
[inferred] = reasoning only.

## 1. Pieces

| Piece | Where | Job |
|---|---|---|
| `ts3wrap.exe` | new, `native/ts3wrap/` (C++ Win32, static CRT, x86) | The host: owns the top-level window, starts TS3W, runs the restart state machine, draws the transition view, writes `ts3wrap.log`. Holds no game state. |
| `[Wrapper]` in ts3native | new `wrapper.cpp` | Inside TS3W: finds the host from the environment, creates the game window as the host's child, keeps the focus and size calls consistent, talks to the host through shared memory, counters, `wrap:` log lines. |
| ts3fixes `Wrapper.cs` | new, beside `Compaction.cs` | The game side: state checks, the ask dialog, the save, the quit, and the load from the sidecar at the main menu. |

The host is x86 because nothing in it needs address space, and the build is the same toolchain as the asi
(`build.cmd`). The later 64-bit render process (docs/241 "Not in this phase") is a separate process either way. Room
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
