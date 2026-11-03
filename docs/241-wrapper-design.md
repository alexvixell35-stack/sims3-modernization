# 241. Wrapper: host window + auto-restart (initial design, draft)

Status (2026-11-03): initial design, not sent. Planned owner: Dalexlol (whole wrapper, not only the restart part), target v0.2.6. Rodney: "we don't go halfway with it".

## Why

Error 12 is the 32-bit address space running out, mostly through fragmentation. Dalexlol's #208 measurement (docs/204 §2.1) shows an in-process reload gives nothing back (largest free block -56 to -94 MB at the reload mark), while a fresh start + load of the same file has ~600 MB more largest free at load (BP 636, LR 609 MB) and 365-380 MB more ten minutes later [verified, contributor measurement]. A restart is the only measured lever of that size short of a 64-bit rebuild.

## What it is

A host process that owns the game window for the whole session. TS3W runs inside it as a child window. When address space runs low, the game saves, quits, relaunches and loads the same save, while the wrapper keeps the screen up with a TS3-style "Saving... / Loading..." view. To the player it looks like a long save, not a restart.

## Scope

- **Host window:** borderless fullscreen and windowed; owns the window for the session; TS3W is reparented into it (SetParent / window style). Handles resize, resolution and monitor changes, DPI, alt-tab, minimize, focus and input (mouse capture, cursor clip, keyboard, IME); the close button is a normal game exit.
- **Launch:** the player starts the wrapper (Steam launch option or shortcut); it starts TS3W with the same arguments. Check Steam DRM and overlay behaviour with a direct TS3W start [unverified].
- **Trigger:** largest-free-block threshold (ts3native already reads it) plus an optional "restart now" hotkey. Ask the player once per session, or a setting: always / ask / never. Never in CAS, edit town, build/buy with an item held, travel, or while a save runs.
- **Save, quit, relaunch, load:** normal EA save first; a failed save means no restart and the game keeps running. Clean exit, relaunch, ts3fixes reads the save name from a sidecar file (never inside the .sims3, see the no-mod-data-in-saves rule) at the main menu, loads it and deletes the file. Camera and active Sim restored if cheap.
- **Transition UI:** the wrapper draws the saving/loading view over the child the whole time (no desktop or main-menu flash). Progress comes from ts3native log or pipe events.
- **Fail-safe:** relaunch or load fails: clear message, game left at the menu, never a lost save. A crash during the restart still leaves the save from the first step.
- **Switches and counters:** off by default; ini switch and threshold; counters for restarts, refusals (by reason), failed saves and restart time; a log line per step.
- **Compat:** DXVK, S3SS, S3PP, Apex Radiance, NRaas, borderless tools (and which of them to switch off).

## Not in this phase

- **64-bit render process** (D3D9 proxy over shared memory; textures, driver and DXVK, ~430-640 MB, in 64-bit). The step after this, Apex Radiance's area (docs: apex coordination). The wrapper must leave room for it: window and swapchain owned by the wrapper side.
- **Exclusive fullscreen.** D3D9 exclusive mode needs a top-level window owned by the device's process; the game here is a child window of another process, so only windowed (borderless) works. A display mode switch on every restart would also show the desktop. The later render process owns device and window, so it can go exclusive and survives game restarts. Loss is small: borderless flip-model presentation gets independent flip on Windows 10/11 (same latency as exclusive), and DXVK presents through Vulkan, where D3D9 exclusive is emulated anyway [community-reported].

## How

1. Design doc by the owner first: process and window ownership, input and focus path, trigger states, failure paths (save fails, relaunch fails, Steam not running), sidecar location, launch path with Steam.
2. Harness repros: (a) forced low threshold on Baseline-BP: save, relaunch, same save loaded, largest free back up, Sims in place; (b) save fails: no restart; (c) relaunch fails: message and menu; (d) alt-tab and resolution change mid-session. Results files as usual.
3. Self-test, then one realistic play session on a long-history save with a natural restart.

The owner decides the transition look and input handling, as long as a restart never looks like a crash and never risks the save.
