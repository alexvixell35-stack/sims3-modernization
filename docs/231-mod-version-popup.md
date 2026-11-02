# 231. Start-up popup "unofficial game modification that does not match your current version"

Seen on the EA laptop (EA app 1.69.47, docs/230) with the Mods set restored from the Steam 1.67 machine. The popup is
native (Win32), appears before the script VM runs and blocks start-up until Quit or Continue is pressed. Status
(2026-11-02): cause found statically, harness answer built and self-checked, not yet run against the game.

Claim marks: **[verified]** = read in the files named; **[inferred]** = consistent with everything read, native code not
decompiled on this laptop (no Ghidra install here).

## 1. Trigger and condition
- The text, "Quit" and "Continue" live in the exe's own localized string table (`TS3W.exe` 1.67 file offset 0xDB4F6E,
  `TS3.exe` 1.69.47 0xE0EF9A, both UTF-16), next to the fatal start-up messages; not in any script assembly or STBL [verified].
- The dialog is resource template 104 (`DLGTEMPLATE`, class `#32770`): button 1001 "Quit", button 1004 "Continue", static
  1003 for the text; identical in both exes [verified].
- Script resources (S3SA, type 0x073FAA07) come in two header versions. Version 1 (what `tools/dbpf.ts` writes, NRaas
  Overwatch, MonoPatcher, SmoothPatch) has no version stamp. Version 2 carries a UTF-16 game-build string: every S3SA in
  the 1.67 game's `scripts.package`/`gameplay.package` says `0.0.0.32`, and so do NRaas ErrorTrap's ScriptCore
  replacement and all three simler90 UPDATE242-Steam assemblies [verified]. The 1.67 Steam exe holds `0.0.0.32` three
  times, the 1.69.47 EA exe holds `1.0.0.18` instead [verified].
- Condition [inferred]: the start-up content scan compares a version-2 S3SA stamp with the exe's own build string; any
  mismatch calls a warning callback (S3SS's "scanner callback", Steam 1.67 `0xECAC30`, pattern `83 EC 44 56 8B 74 24 4C`)
  that shows template 104. Continue = return 0 and keep loading (S3SS "Hide Dialogue" stubs the callback to `xor eax,eax;
  ret`); Quit exits. It fires with 1.67 core mods on 1.69 and with Steam builds on EA app, which is what the community
  reports (Steam discussion 1781640738625280760; EA forum thread 8322754).
- Our packages (`ts3agent`, `ts3fixes`) are version 1 and never trigger it [verified header].

## 2. Fix
1. Mod set (the real fix, no code): on a 1.69 install use the 1.69 builds (NRaas ErrorTrap 1.69, simler90 UPDATE242
   EAapp build); the Steam/1.67 ones also run 1.67 code on 1.69 (docs/07 §2.1).
2. Harness guard (built, `tools/probe/ts3probe.py` `answer_startup_dialog`): for 5 min after start the probe polls
   once a second for a visible `#32770` window of the game process with controls 1001, 1004 and 1003 and posts
   `WM_COMMAND(1004)` to it (the Continue click; `PostMessageW`, no focus, no key press). Nothing changes for players:
   the probe is harness-only. `python tools/probe/ts3probe.py self-check` asserts the matcher.
Not chosen: an in-game answer (the agent is not running yet) and a tuning override (native check). S3SS's
StartupWarningDialogFix also hides it, but S3SS is not part of the EA-laptop stack and hides the real mismatch.

## 3. Verify
Start a job on the EA laptop with the 1.67 set still in place. The probe log must show one
`[ts3probe] startup-dialog: Continue posted: It looks like you have installed ...` line and `events.jsonl` one
`{"kind": "startup-dialog", ...}` entry, then `cycle.ts` reaches "agent answering" without the 300 s stall. With the
1.69 mod builds installed the line must not appear. `posted` false or a second line = the click did not close it; then
read the dialog's thread (owner window) before changing the guard.

## 4. Open
Decompile the scanner (Steam `0xECAC30` and its registration at `0xECBAA9`) to confirm the compare and whether a
version-2 stamp on a non-core assembly (simler90Extras) alone fires it.

## Verify 2026-11-02
EA laptop, 1.67 Mods set in place, screen locked (LogonUI running the whole time), `python tools/probe/ts3probe.py serve --launch --port 47891` with `TS3_LAUNCH=ea`, 11:48:56 PHT. [verified]
- Probe log: `launching [EALauncher.exe, origin2://...]`, `attached; serving` (frida attach + agent.js), then one `startup-dialog: Continue posted: It looks like you have installed an unofficial game modification that does not match your current version of The Sims 3. ...`; `events.jsonl` holds one `{"kind": "startup-dialog", ...}` entry. No second line, no `failed`.
- The dialog closed: at 11:52 the game pid owned only `Canvas` "The Sims 3" (visible), `EAClipboardClass`, `IME`, `MSCTFIME UI`; no `#32770`.
- ts3agent answered on 1.69.47 at 11:49:39 (43 s after launch): `agent?cmd=ping` -> `{"ok": true, "result": {"pong": true, "plugin": "Ts3AgentPlugin", "mod_version": "0.1.0"}}`, `agentstatus` heartbeat rising 595 -> 3937 over 2 min, `worldLoaded: false` (main menu; `--world` is ignored under `TS3_LAUNCH=ea`). No new `ScriptError*.xml`; `ts3native.log` still `SKIP all: host is not TS3W.exe at 0x400000` (expected until the 1.69 port).
- Runs while locked: CPU seconds 27.6 -> 54.8 over 10 consecutive 15 s samples with LogonUI present (about 2.5 s per 15 s, main menu); working set 825 -> 836 MB and flat (no world load, so the "past 1 GB" mark does not apply here).
- Stopped at 11:52 (`Stop-Process -Force`): probe logged `detached: process-terminated`, `stopped`.
Not verified: the no-dialog case with 1.69 mod builds (line must not appear), and a world load after the Continue (needs a save started from the main menu; this laptop gives no timing data).
