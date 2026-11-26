# 315. Kit benchmark mode: unattended A/B runs on the tester's PC

2026-11-21, branch `feature/kit-benchmark-1121`. Built, offline self-check green, **NOT RUN against the game yet** (§7).
Ledger §570.

Rodney: an opt-in, repeatable, scripted benchmark that testers start from the kit while away from the PC, for A/B
measurement on their own hardware and mod sets. This is not the story fuzzer (stopped); it is one fixed scenario.

## 1. What the tester sees

1. `Benchmark.cmd` (kit root). It lists the newest saves and asks for one, then prints the plan (runs, ABBA order, minutes,
   free disk) and asks the tester to type `YES`.
2. The game starts by itself. After the world loads, a notice appears: "TS3 test kit: automated benchmark running, please
   don't touch (about N min). Run i of n." At the end of each run: "benchmark run i of n done, closing the game." The runner
   then goes to the main menu and quits.
3. `Game\Bin\adaptdiag-bench.txt` (newest summary, one line per run) and `Game\Bin\adaptdiag-bench\bench-<stamp>-summary.txt`,
   `-report.jsonl` (everything, one JSON line), `-log.txt`; TS3Kit.bat option 8 (Make report) zips them (docs/317 §3-§4). Example line:
   `1/4 A: ok; fps median 59.5 min 30; hitches >=250ms 3 >=1s 1; longest pause 1200 ms; script errors 0; noise quiet`.

## 2. Scenario (per arm run, ~13 min with the defaults)

The arm is applied (hook, §5), a fresh copy of the save is made, and then:

| Step | Agent commands | Notes |
|---|---|---|
| start | (Start-Process `Game\Bin\TS3W.exe`, or `TS3.exe` for EA) | the native mod-version warning (docs/231) gets Continue posted (`WM_COMMAND`, no focus) |
| connect | mailbox scan, `session_lock {token}` | 5 s scan interval, 300 s cap |
| menu | `auto_dialog` every 2 s | "Mod Scripts Found" cleared, then 5 s quiet (a load sent earlier is lost, as in cycle.ts); 300 s cap |
| load | `autopilot {dialogs, speed none}`, `load_save TS3Kit-Bench.sims3`, `dialog_expect` | waits for the mailbox world flag, 900 s cap |
| settle | `quiet_ui`, `decline_requests`, `enter_live_mode` if needed, `seed_rng {seed: 1121}`, `notify`, `tick_stats {start}` | |
| live | `autopilot {speed: ultra}`, 180 s | phase tag `live` |
| buy | 5 x (`buy_open`, wait `cas_state.buy_catalog`, 5 s, `buy_close`, wait LiveModeState, 3 s) | `buy` |
| cas | `cas_open`, wait `cas_ready`, 10 s, `cas_close`, wait LiveModeState | `cas` |
| lots | `camera_jumps {order: far, lots: 5, dwellS: 8, loops: 1}` | `lots`: lot streaming through camera jumps, no Sim moved (instead of travel: shorter and no Sim state change) |
| save | `save_game {name: "TS3Kit-Bench"}`, wait `save_in_progress` false | `save`: reported, left out of the hitch totals |
| end | `notify`, `main_menu`, `quit_game` | the process is stopped by PID if it is still there after 120 s |

Each phase is a `tick_stats {reset}` window (the sim-iteration interval histogram). Order: ABBA (`A B B A`, rounds
repeat it; three arms = `A B C C B A`). `seed_rng` swaps the script RNG after the load, so later draws repeat [inferred
I-570-3]; the world load itself is not seeded.

## 3. Metrics

- **Hitches**: `tick_stats` gains `over_250` / `over_1000` (ticks >= 250 ms / >= 1 s, from its 0.1 ms bins + overflow bin)
  [verified: code]. Totals = live + buy + cas + lots. **Longest pause** = largest `max_ms` of those phases.
- **fps**: the runner reads the game's frame counter once a second with ReadProcessMemory: Steam / retail 1.67 u32 at
  `0x11CDD94` = GameClock `0x11CDD48` + `0x4C` (`mov ecx,0x11CDD48` at `0xECAB15`, `call 0x5943F0`; `add dword [esi+0x4C],1`
  at `0x594453`; image base fixed, no DYNAMIC_BASE; only reset at init `0x5944C0` <- `0xECBBF7`) [verified, static]. It counts
  main-loop iterations, which in live play is one per presented frame [inferred I-570-1]. EA 1.69: `0x1227E14` by the .data
  delta of `ts3addr-ea169.h` [inferred I-570-2]. Samples outside 0..2000/s or unreadable make fps `n/a` (more than a quarter
  bad, or fewer than 5). fps median / min over the measured phases. Seconds with the game window not in front are counted
  (`unfocusedS`: the game caps itself at ~30 fps unfocused, docs/41).
- **Script errors**: `ScriptError_*.xml` in the user folder written since the run started.
- **Noise grade**: CPU seconds of every other process during the run / (wall x logical CPUs): < 5% `quiet`, < 10% `ok`,
  else `noisy (rerun)` (memory: external load >= 10% = rerun).
- **Activity tags**: the phase list with seconds, tick stats and fps per phase (`bench-report.json` `runs[].phases`).
- `perArm`: medians across that arm's ok runs.

## 4. Safety

- **Save**: the tester's save is never loaded. Per run its folder is copied to `Saves\TS3Kit-Bench.sims3` (old copy and
  `.backup` removed first) and only that copy is loaded; the save step passes `name` so EA's `forceUseSaveName` path writes
  the copy's name (ControlHandlers.SaveGame) [verified: code]. The original folder and its `.backup` are hashed (sha256 over
  every relative path + file hash) before and after; a change prints `NO: HASH CHANGED` and the run exits 1. Names that match
  `^TS3Kit-Bench` or contain path characters are refused. Free disk must be >= max(5 GB, 3 x save size + 2 GB).
- **Refusals before anything changes**: the game already running, another `ts3agent*.package` in Mods (dev machines), low disk,
  missing save or agent package.
- **Cleanup**: always in `finally` (also on Ctrl+C): the copy, its `.backup`, the agent package and the state file
  (`<user>\TS3Kit-Bench\bench-state.json`, written before the first change). A leftover state file is cleaned at the next start;
  `TS3Bench.ps1 -Cleanup` does the same for the uninstaller (§5).
- **Processes**: only the PID this runner started is ever stopped. The game window is never focused or moved.
- **Command channel**: there is no network listener at all. The channel is the existing ts3agent mailbox in game memory,
  reached with ReadProcessMemory / WriteProcessMemory on the one PID (same user only). The agent package is in Mods only
  during the benchmark (`Mods\Packages\TS3KitBench-ts3agent.package`, removed after); the normal kit install never ships it.
  New `session_lock {token}` (loader Dispatcher): the runner claims the channel with a random 48-hex token per run; every later
  request without it is refused, a second lock is refused, tokens < 16 chars are refused, `release` frees it. Honest limit:
  a process with VM_WRITE on the game can already do anything to it, so the token is exclusivity, not a security boundary
  against the same user.

## 5. Hook points (interface for the kit lanes)

Built against the adaptive diag kit's contract, docs/317 (branch `feature/adaptive-diag-kit-1121`, 0ef0e7291).

- **Entry**: `tools\TS3Bench.ps1` (one script; `TS3Bench.cs` beside it is its Add-Type source). `Benchmark.cmd` starts it.
- **Session state** (docs/317 §1): before each arm's launch `[Session] Benchmark=1`, `BenchmarkName=ts3bench`,
  `BenchmarkRun=<stamp>-<i>`, `Arm=<arm name>` (ASCII, capped 40 / 40 / 16); after the arm's game exits `Benchmark=0`,
  `Arm=` (also in `-Cleanup` and after a crash). Written only through the kit's own `SetAdSession`: TS3Bench loads it with
  `ParseToml` / `MergeToml` / `$Rx` / `$AdState` from `TS3Kit.ps1` by AST into a private module (TS3Kit.ps1's menu never runs).
  A kit without `SetAdSession` (pre-1121.a1) runs the benchmark without session labels. `-KitScript` points elsewhere (tests).
  With `Benchmark=1` the rule engine turns nothing on mid-run (docs/317 §1), so every arm runs with identical diagnostics.
- **Results** (docs/317 §3): `Game\Bin\adaptdiag-bench.txt` + `Game\Bin\adaptdiag-bench\bench-<stamp>-*` (`.txt`, `.jsonl`,
  ASCII); the kit's Make report zips and masks them. Report schema: `kind: ts3kit-benchmark`, `version: 1`. ts3native's own
  `## benchmark` section (sim fps p50, hitches per clean minute, from `adaptdiag.csv` rows with `bench=1`) sits beside it in the
  same zip. `-OutDir` / `-ReportHook` of the stub are gone.
- **Arms** (kit A/B lane, `feature/kit-ab-1121`; docs/317 does not cover arm switching): `-ArmsFile <json>` =
  `{"arms":[{"name":"A","apply":"<ps1>","restore":"<ps1>"}, ...]}`; `apply` runs before each arm's launch and `restore` after
  its game exits, each with `-Arm <name> -Game <dir> -User <dir>`. Without `-ArmsFile` the arms come from the kit's A/B arms
  file (`payloadbrms.json`, docs/314; merge 2026-11-21): arm A and B are set by `TS3Kit.ps1 -Mode ArmApply -Arm X` (the
  A/B kit's own `AbApply`: package by sha, or the off arm's asi moved to `ab\off`), no session is counted in the A/B state;
  the next normal A/B session (menu 6) sets its own arm again. A kit without `arms.json`: two no-change arms A and B (noise check).
- **Payload**: the kit zip must carry `payload\bench\ts3agent.package` (or pass `-AgentPackage`): the ts3agent build with
  `session_lock` + `notify` (builds.json `ts3agent.kit-benchmark-1121`).
- **Uninstall** (in TS3Kit.ps1 since the 2026-11-21 merge; test-kit.ps1 case "benchmark leftovers removed"): `DoUninstall`,
  right after its `SetAdSession $bin 'Benchmark' '0'` line:
  `$bp = Join-Path $here 'TS3Bench.ps1'; if ($user -and (Test-Path -LiteralPath $bp)) { & $bp -Cleanup -Game $game -User $user | Out-Null }`
  (removes a leftover `TS3Kit-Bench.sims3` copy, its `.backup`, the agent package and the benchmark state file).

## 6. Runner choice

PowerShell (5.1 + 7) with a small C# block through Add-Type (C# 5, compiled by the .NET Framework csc that every Windows has),
not a bundled node or python. The whole client is ~170 lines of C#: OpenProcess, VirtualQueryEx scan for the mailbox magic in
committed RW regions >= 1 MB, the same header check and seq protocol as `tools/probe/agent.js`, a u32 read for the frame
counter, and EnumWindows + PostMessage for the start-up warning. A portable node is ~30 MB plus Frida (flagged by AV), and
python + frida has the same AV problem; Add-Type is already what the kit uses elsewhere and is easy to read for a tester.

Fail-fast: every 30 s the runner checks progress (mailbox heartbeat, frame counter or game CPU time moved); two misses in a
row end the run with "game stalled", the game PID is stopped, the arm restored and the benchmark aborted (cleanup runs).
Every wait also has its own cap (menu 300 s, load 900 s, CAS 180 s, save 600 s).

## 7. Tests and what is not verified

- `node tools/selftest/kitbench/run.ts` (in selftest-all, Windows only): builds a fake game from the **real** loader
  (`Mailbox.cs`, `Dispatcher.cs`, `Json.cs`, x86) and runs `TS3Bench.ps1 -SelfCheck -FakeTarget` under Windows PowerShell and
  pwsh: ABBA order, median, fps stats and n/a rule, noise grade, save name / ScriptError / dialog regexes (real match + non-match),
  request shape, token, phase sums, summary line, directory hash (copy equal, change seen), cleanup scope, mod-version dialog
  match, and the mailbox scan + round trip + every session_lock rule end to end.
- Dry run against the real install (`-DryRun`): plan, paths and refusals print, nothing changes.
- **NOT verified**: a real game run (scenario timings, notifications, load flag, buy/CAS waits, save-copy hash in practice),
  fps on EA 1.69, the agent on EA 1.69 (built against 1.67 assemblies), the mailbox scan time on a big save (the probe saw
  ~18 s per full pass, docs comment in Instantiator.cs).
- Local run when the game is free (track.ts status empty, kit A/B lane done), Steam, throwaway save, both arms. On a dev
  machine the harness's `Mods\Packages\ts3agent.package` must be moved out for the run (the runner refuses a second agent):
  `powershell -File tools\player\ts3-kit\tools\TS3Bench.ps1 -Save <throwaway> -AgentPackage build\ts3agent.package -LiveS 60 -BuyLoops 2`
  then check: both notices seen in the screenshot/log, the summary says "original save untouched: yes", no `TS3Kit-Bench*`
  folder left in Saves, no `TS3KitBench-ts3agent.package` left in Mods, Bin unchanged except `adaptdiag-bench*`, `[Session]
  Benchmark=0` afterwards, and **record whether Kaspersky reacts** (§8): its notification / quarantine list / the summary's
  "AV log lines" count, and `OpenProcess ... failed: 5` or "no mailbox" in the log.

## 8. Antivirus

The runner does what game trainers do: `OpenProcess` with VM_READ / VM_WRITE / VM_OPERATION on TS3W.exe, a VirtualQueryEx
scan and Read/WriteProcessMemory, from a PowerShell host that compiled C# with Add-Type. Behaviour blockers (Kaspersky on the
dev PC; Defender ASR / AMSI on others) may flag or block this: quarantine of `TS3Bench.ps1` / the Add-Type assembly, a denied
`OpenProcess` (error 5), or silently failed reads (mailbox never found).

What the runner records: the installed AV products (`root/SecurityCenter2 AntiVirusProduct`), per arm every line of an event
log named `*Kaspersky*` since the arm started whose text names TS3Bench / TS3W.exe / TS3.exe / powershell / pwsh (count + first
line in the summary, full list in the report), and the OpenProcess / scan failures with an antivirus hint in the error.
Not detected: blocks that leave no log line (the summary then shows a FAIL "no mailbox" with 0 AV lines).

If an AV reacts, the proposed alternative (not built) keeps cross-process memory access out of the runner entirely:
ts3native, already in the game process, opens a named pipe `\\.\pipe\ts3bench-<token>` only when `[Session] Benchmark=1`
(current-user ACL, one client, `PIPE_REJECT_REMOTE_CLIENTS`) and relays each request into the agent mailbox in-process (it can
find the buffer with the same magic scan from inside, or the agent can hand ts3native its address through a small icall);
the runner talks to the pipe with `System.IO.Pipes`. Fallback without native changes: a request / reply file pair in
`Game\Bin\adaptdiag-bench\` polled by ts3native (slower, ~100 ms per command). Either needs a ts3native build and a switch,
default off.
