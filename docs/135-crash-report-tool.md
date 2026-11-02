# 135. Crash report tool: opt-in `[CrashReport]` + `tools/report-crash.ts`

Written 2026-10-21. **Offline build only:** built and self-checked in an agent worktree (ts3native 0.36.7); no game run, no crash injected into the game. Asked for in PLAN.md ("SECOND tomorrow", Rodney 2026-10-20): testers on other PCs (AMD, 4 cores, other mod sets) need a way to send a usable crash report without the maintainer on their machine.

## Summary

- **In the mod:** `[CrashReport]` (`native/ts3native/crashreport.cpp`), shipped `Enabled=0`. An unhandled-exception filter writes one scrubbed text report per crash to `<Documents>\Electronic Arts\The Sims 3\ts3native-crashes\crash-<UTC>-<pid>.txt`. A running marker turns a session that ended without a clean exit and without a report into a `-hard.txt` report on the next start.
- **Signature:** fault module+offset plus the top 3 stack frames, e.g. `TS3W+0x23456 TS3W+0x1000 d3d9+0x42 TS3W+0x2000`; its FNV-1a hash is the **signature id** `crashsig-xxxxxxxx`, which issues are searched and deduplicated by. Issue title: `[crash] TS3W+0x23456 (ts3native 0.36.7)`.
- **Sending:** `node tools/report-crash.ts` lists new reports, prints exactly what would be sent, and only after the player types `y` either uses the player's own logged-in `gh` (search by signature id, comment on the match or create an issue) or opens a prefilled `issues/new?template=crash.yml` page that the player submits. No token in the mod or the tool; nothing is sent automatically.
- **GitHub side:** `.github/ISSUE_TEMPLATE/crash.yml` (fields `signature`, `version`, `what`, `report`) and `.github/workflows/crash-issues.yml` (labels `crash`, comments with earlier issues of the same signature id; never closes).

## 1. Design as built

### 1.1 Start-up (`Run`, TS3W.exe only)

1. `CrPreLog` (before `ts3native.log` is truncated): with `Enabled=1`, keeps the last 50 lines of the previous session's log in memory.
2. `InstallCrashReport` (right after `[Crash] Log`):
   - Documents folder from `HKCU\...\Explorer\User Shell Folders\Personal` (what `SHGetFolderPath(CSIDL_PERSONAL)` returns, so OneDrive-moved folders work), expanded by `RegGetValueW`. `advapi32` is already an import of TS3W.exe [verified, import table], so no new DLL loads in `DllMain`. If `<Documents>\Electronic Arts\The Sims 3` does not exist (localized folder names), reports go to `Game\Bin\ts3native-crashes\`.
   - Scrub list (§2), enabled ini sections (every section with `Enabled` > 0), CPU brand (CPUID), logical processors, RAM, OS build (`RtlGetVersion`), Large Address Aware flag of the exe.
   - `running.txt` from the previous session still there = it ended without `DLL_PROCESS_DETACH` and without a filter report: write a **hard** report (§1.4), then write a new marker (`ts3native <version>`, pid).
   - `SetUnhandledExceptionFilter(CrFilter)`, keeping the old filter as the next one, then swap the **exe's IAT slot** of `SetUnhandledExceptionFilter` for `CrSetFilter`.
3. `DLL_PROCESS_DETACH` (normal quit through `exit`/`ExitProcess`): delete the marker.

### 1.2 Why the IAT slot [verified static, inferred runtime]

TS3W.exe imports `SetUnhandledExceptionFilter` at IAT `0x00F9517C` and calls it after our `DllMain`: `0x4D71BA` installs `0x4D7140` (EA's handler, stores the old filter at `+0x44`), `0x4D7260` / `0x4D73C7` put the stored one back, `0x4D7039` loads the pointer into a register, and the CRT calls it at `0xF7F5F2` (its C++ filter) and `0xF7F926` (sets NULL before a fast fail) [verified: import table and call sites in the Steam 1.67 exe]. Without the swap, EA's later call would replace ours. With it, `CrSetFilter` behaves like the API for the caller (returns the previous "next" filter) but keeps `CrFilter` on top and makes the caller's filter the one `CrFilter` calls next. That `0x4D7140` writes EA's `xcpt*.txt` and then calls `TerminateProcess` (`0x4D7122` is just before it) is [inferred] (ledger I-190-2). Filters that other DLLs (DXVK, S3SS) set through their own IATs still replace ours; their order is not handled (shortcut, see §5).

### 1.3 The filter (`CrFilter`)

- One report per process (`g_crBusy`); the report code runs inside `__try` so a fault in it never stops the chain.
- **Never swallows:** returns the next filter's verdict, or `EXCEPTION_CONTINUE_SEARCH` when there is none.
- No heap: every buffer is static (~520 KB of .bss), module names come from `VirtualQuery` + `K32GetMappedFileNameW` (no loader lock).
- Written in parts, each scrubbed and flushed: first the header, fault, registers and stack (what matters most), then exe SHA-256 (own SHA-256, reads the exe), system lines, package names, log tail. A second fault in the later parts still leaves the first part on disk.
- Deletes `running.txt` (the crash is captured, no hard report next start).

Report format (`ts3native crash report 1`, `key: value` lines, then `---` sections):

```
ts3native crash report 1
kind: exception
time: 2026-10-21T01:02:03Z
title: [crash] TS3W+0x23456 (ts3native 0.36.7)
signature id: crashsig-0a1b2c3d
signature: TS3W+0x23456 TS3W+0x1000 d3d9+0x42 TS3W+0x2000
ts3native: 0.36.7
code: C0000005 (access violation, read 0x00000010)
fault: TS3W+0x23456
thread: 1234 (sim)
registers: eax=... efl=...
address space used: 3012 of 4095 MB
enabled sections: [GC] [WaterJps] ...
stack (return addresses found on the stack, top first):
  TS3W+0x1000
  ...
exe sha256: 10b2423e...
cpu: ..., 16 logical processors
ram: 32512 MB
os: Windows 10.0.26300
large address aware: yes
--- package files (names only) ---
  ts3fixes.package
packages: 1
--- ts3native.log (last 50 lines) ---
...
--- end ---
```

**Stack:** a scan, not an unwind (the game has no frame pointers everywhere): from `esp` up to 4096 words, every word that points into executable image code right after a call instruction (`E8 rel32`, or `FF /2` of 2-7 bytes). Up to 32 frames. Stale return addresses can show up; the signature takes only the top 3 (ledger I-190-4 checks that the same crash gives the same id).

### 1.4 Hard report (`-hard.txt`)

`kind: hard`, the previous session's version (from the marker), the previous log's last 50 lines, the current enabled sections, system lines and packages. If an EA `xcpt*.txt` newer than the marker exists, only its `type:` and `address:` lines are copied (the rest of that file names the user and the machine) and, for an address in TS3W.exe (fixed base `0x400000`), the signature becomes `TS3W+0x...`; otherwise it is `hard exit`.

## 2. Scrub list

Applied by the mod to every byte written (`CrScrub`), and again by `report-crash.ts` before anything is shown or sent:

| What | Replaced by | Notes |
|---|---|---|
| `<Documents>\Electronic Arts\The Sims 3` | `<TS3User>` | longest match first |
| the game's `Game\Bin` folder | `<Bin>` | the Steam library path can hold a user name |
| `%USERPROFILE%` | `<home>` | |
| `%USERNAME%` (3+ chars) | `<user>` | case-insensitive (ASCII); shorter names are skipped (they would eat the text) but still go through the next rule |
| computer name (3+ chars) | `<machine>` | `GetComputerNameW` |
| `%USERDOMAIN%` (3+ chars) | `<domain>` | |
| any `\Users\<x>` or `/Users/<x>` segment | `\Users\<user>` | other drives, other profiles |
| package files | file names only | never the folder |
| EA `xcpt*.txt` | `type:` and `address:` lines only | its `[System info]` names the user and machine |

The tool also replaces this PC's `os.userInfo().username`, `os.hostname()` and `os.homedir()` (longest first). Not scrubbed: register values and addresses (no personal data), package file names themselves (a player who names a file after themselves sees it in the preview and can say no).

## 3. Sending (`tools/report-crash.ts`)

- Folder: same registry value (`reg query`), else `~/Documents`; `--dir` for the `Game\Bin` fallback. `reported.json` in that folder remembers sent (`url`), skipped and browser-opened reports.
- For each new report: prints the destination, the title and **exactly** the text that would be sent, then asks `y` / `n` (not now) / `s` (never ask again).
- `y` with `gh auth status` OK and no `--browser`: `gh issue list --search "<id> in:body" --state all`; a match gets a comment (`gh issue comment`), else `gh issue create` with the title and the body (signature id, signature, version, the report in a `text` block, cut at 60,000 characters). No labels from the client (players cannot set them); the Action does.
- Otherwise: opens `https://github.com/<repo>/issues/new?template=crash.yml&title=...&signature=...&version=...&report=...` with the report cut by lines to keep the URL under 7,500 characters, and asks the player to attach the full file if it was cut.
- `--self-check` (CI, `build.yml` node job): header parse, both scrub rules, body cut, URL cut and length, and that `crash.yml` still has the `signature`, `version` and `report` field ids.
- **Error 12 report (`--memory [--log <ts3native.log>] [--names]`, docs/201 `[MemDiag] AllocSites` opt-in, docs/PLAYERS.md):** keeps the lines `tools/memory-report.ts` reads (run header, `diag:`, `allocsites:`, `typegrowth:`, `gcsize:`, `ts3fixes: world:` / `script assemblies`, `save: memory`) plus the last 20 log lines; the §3 scrub, then emails -> `<email>`, `\\host\` -> `\\<machine>\`, and every `*.package` name -> `pkg-<first 8 hex of sha256(lower-case name)>` (same name = same id across reports; a known name can be confirmed by hashing it) unless `--names`. Writes `ts3native-memory-report.txt` in the current folder, prints exactly what would be sent, asks `y`; gh = always a new issue (body starts with the marker `ts3native memory report`, cut in the middle: first 80 lines + the end), else the prefilled `memory.yml` page (cut from the start: the samples before Error 12 stay). `crash-issues.yml` job `label-memory` labels issues with the marker `memory`.

## 4. Selftests (`native/ts3native/crashreport-test.h`, run by `build.cmd`)

- SHA-256 known answers (`abc`, one million `a`).
- Scrubber on a fake profile `alice` / `ALICE-PC` with an OneDrive Documents folder and a Steam library on `D:`: every needle, case-insensitive matches, the `\Users\` rule on other drives and with `/`, the 3-character minimum, truncation never overruns.
- Signature string and id: fault + 3 frames, absolute hex for addresses outside any image, same input = same id.
- IAT chaining on `selftest.exe`'s own slot: later `SetUnhandledExceptionFilter` calls return the previous "next" filter and queue behind ours; the real top filter stays `CrFilter`.
- **Forced fault in a test-only dummy** (`CrtFault`, reads address `0x10`; never a game path): the report exists, `fault:` lies inside `CrtFault`, the code line says `read 0x00000010`, the stack holds a `selftest+0x` frame, the next filter ran once and its verdict was used, the marker was deleted, package names only (`.package` files, folder name gone, user name scrubbed in a file name), log tail = exactly the last 50 lines, no `alice` anywhere.
- No next filter: `EXCEPTION_CONTINUE_SEARCH`; second crash in one process: no second report.
- Hard report from a marker: `kind: hard`, `[crash] hard exit (ts3native <previous version>)`, previous log tail scrubbed.

## 5. Not built (on purpose, or later)

- No minidump (PLAN mentioned one kept locally): it needs `dbghelp` in a crashing process and would be too big and too personal to send.
- No in-game prompt (`Prompt=1` in the PLAN entry): the player runs the tool; a `DllMain` message box is not safe.
- `crash-ledger.ts` does not read GitHub issues yet.
- Localized Documents folder names (`Die Sims 3`, ...) are not searched; reports then land in `Game\Bin\ts3native-crashes\`.
- Filters set later by other DLLs through their own IATs replace ours (shortcut: hook their slots too if a tester's setup shows `no exception reached the filter` hard reports with an xcpt file).

## 6. Before `Enabled=1` in a release

Ledger §190: I-190-2 (ours stays on top in game: log line `exe IAT SetUnhandledExceptionFilter hooked at 0x00F9517C`), I-190-3 (a normal quit leaves no `running.txt`), I-190-4/5 (one crash-injection job on a dummy path, run twice: same signature id, report written, EA's xcpt still written after ours). Then add `[CrashReport]` to the release check list in `build.yml` and to `docs/PLAYERS.md` "What's switched on".

## 7. Symbolizing a crash (`tools/crash-symbolize.ts`)

`node tools/crash-symbolize.ts <ts3native.log | crash report> [--build <dir>] [--map <file>] [--all] [--json]` prints one line per frame: for a report the fault and the stack (`module+0xoffset`), for a log every distinct first-chance `exception` line (eip, then its stack code refs; repeats counted) with the first 8 frames (`--all`: every frame). Last line `needs review: ...` (faults inside ts3native, ts3native frames without a map).

- **Ranges:** TS3W.exe at its fixed base (`host TS3W.exe base`, image size `0xE5A000` from the 1.67 PE header), ts3native from the `crash log: ... (module 0x..-0x..)` line, other modules by the name+offset the line carries (docs/172 §7 builds) or "no range in the log".
- **TS3W.exe names:** nearest named function at or below the address from `tools/ghidra/symbols/ts3w-1.67.symbols.csv`. The CSV has no function ends, so past 1 KB the line says it may be another function. With `game-copy/analysis/functions.csv` (local: entry, end, name for every function) the containing function is exact. Exact address hits in `docs/data/*.csv` with an address and a name column (IC tables, Apex hook sites, Mono glue, native writers) are added in brackets.
- **ts3native names:** from the linker map of the same build. `build.cmd` writes `out	s3native.map` (`/MAP`, the asi bytes do not change) and the CI `native` job builds it too; uploading it as its own artifact `ts3native-map` (not part of the release) is one `upload-artifact` step in `build.yml`, proposed with this tool (a workflow edit, so a maintainer adds it). Until then the map comes from a local `build.cmd`. `--build <dir>` takes `ts3native.asi` + `ts3native.map` from one folder (the two artifacts unpacked together, or `native/ts3native/out`), stops when the asi's version is not the log's or the map's link timestamp is not the asi's; `--map` alone is used unchecked.
- **Stack junk:** a stack scan also finds UTF-16 text (`0x00XX00YY`, both characters printable); such frames are labelled as text, not code.
