# Build

How to build our components from source and install them. Everything runs on Windows; the game is Windows-only.

Paths below use placeholders: `<SteamLibrary>` is your Steam library (default `C:\Program Files (x86)\Steam`), `<Documents>` your Documents folder, `<repo>` this checkout.

## Prerequisites

| What | Version | Needed for |
|---|---|---|
| Windows 10 or 11, x64 | | everything |
| The Sims 3, Steam edition | patch 1.67 (`TS3W.exe` SHA-256 starts `10b2423e`, ends `0609`) | everything. Our native patches check the exe bytes and skip themselves on any other build. Results so far use all expansion and stuff packs; the fixes should work with fewer, but that is untested. |
| Node.js | 22.18 or newer (runs `.ts` files directly, no build step, no npm packages) | installer, build scripts, harness |
| .NET Framework 4.x C# compiler | `C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe` (ships with Windows) | `ts3fixes`, `ts3agent` (C# 5, compiled against the game's own DLLs) |
| MSVC x86 build tools | Visual Studio 2022 (Build Tools, Community or Professional) with "Desktop development with C++" | `ts3native` (`build.cmd` calls `vcvars32.bat` and `cl`) |
| Python 3 + `frida` 17 + `frida-tools` | `pip install "frida>=17,<18" frida-tools` | probe server and profilers (testing only) |
| Python 3.10+ + `unicorn` 2 + `capstone` 5 | `pip install unicorn capstone` | offline function tests on your exe copy: `python tools/emu/run.py --self-check` (docs/63) |
| Git Bash | | the few `.sh` helpers (`tools/harness/restart-*.sh`, `tools/ghidra/run-headless.sh`) |
| Optional: Ghidra 12 + JDK 21, `ilspycmd`, REA | | reverse engineering only (see docs/04 "How this was done") |

Other C# compiler: `build-fixes.ts`, `build-agent.ts` and `layout-map.ts` take the compiler from `TS3_CSC` when it is set (for example a wrapper around Roslyn `csc.dll` from the .NET SDK on Linux). Releases are still built with the Windows `csc.exe`; a Roslyn build is not yet verified to load in the game's Mono.

`build.cmd` has the Visual Studio path hard-coded (`...\2022\Professional\VC\Auxiliary\Build\vcvars32.bat`). With another edition, edit that line locally (or run `build.cmd` from an "x86 Native Tools Command Prompt" after removing the `call` line). Do not commit the change unless you make it detect the edition.

## Game script DLLs (one-time, from your own game)

`ts3fixes` and `ts3agent` compile against the game's script assemblies. They are EA files: extract them from **your own** install and never commit them. `game-copy/` is git-ignored.

```
node tools/extract-assemblies.ts game-copy/assemblies ^
  "<SteamLibrary>\steamapps\common\The Sims 3\Game\Bin\gameplay.package" ^
  "<SteamLibrary>\steamapps\common\The Sims 3\Game\Bin\scripts.package" ^
  "<SteamLibrary>\steamapps\common\The Sims 3\Game\Bin\simcore.package"
```

The build scripts need these 10 files in `game-copy/assemblies/`: `mscorlib`, `System`, `System.Xml`, `ScriptCore`, `SimIFace`, `Sims3Metadata`, `Sims3GameplaySystems`, `Sims3GameplayObjects`, `Sims3StoreObjects`, `UI` (`.dll`). If one is missing, add `Game\Bin\UI\UI.package` to the command. The build stops with a list of missing DLLs.

`game-copy/` belongs to one checkout. A git worktree needs its own copy (or a directory link to the main one).

## Components

| Component | Build | Output | Install |
|---|---|---|---|
| `ts3fixes` player mod | `node tools/setup/build-fixes.ts` | `build/ts3fixes.package` | `node tools/setup/install-fixes.ts install --only stutter-fixes` (builds too) -> `<Documents>\Electronic Arts\The Sims 3\Mods\Packages\ts3fixes.package` |
| `ts3native` ASI plugin | `native\ts3native\build.cmd` (also runs `selftest.exe`; builds `decompress-test.exe`, `waterjps-test.exe`) | `native/ts3native/out/ts3native.asi`, `ts3native.ini` | `install --only native-patches` (builds too) -> `Game\Bin\`. Needs `asi-loader` (Ultimate ASI Loader as `wininet.dll`). The game must be closed: the installed `.asi` is locked while it runs. |
| `ts3agent` test agent | `node tools/harness/build-agent.ts` (full, needs a game restart) or `--hot` (plugin only) | `build/ts3agent.package` | `node tools/harness/cycle.ts --restart` installs it; `node tools/harness/reload-agent.ts` hot-reloads the plugin into a running game. Test-only: never ship it to players. |
| No-intro package | `node tools/harness/nointro.ts` | `build/ts3-nointro.package` | installer entry `nointro` |
| Overwatch Stuck Check off | `node tools/setup/build-overwatch-tuning.ts` | `build/ts3-overwatch-nostuckcheck.package` | installer entry `overwatch-no-stuckcheck` (opt-in, new saves only) |
| Overwatch inventory cleanup off | `node tools/setup/build-overwatch-tuning.ts inventory` | `build/ts3-overwatch-noinvcleanup.package` | installer entry `overwatch-no-invcleanup` (opt-in, new saves only; conflicts with the Stuck Check one) |

Changing a setting of `ts3fixes`: edit the tuning XML in `tools/setup/build-fixes.ts` (`TUNING_XML`) and rebuild, or edit the tuning resource `Ts3Fixes.Instantiator` in the installed package with s3pe. During tests the agent switches fixes at runtime (`patches {name:false}`, see docs/TESTING.md).

The game caches compiled script mods in `<Documents>\Electronic Arts\The Sims 3\scriptCache.package`. `cycle.ts --restart` clears it when our mods changed; clear it yourself (or use installer entry `cache-clear`) if you install a rebuilt mod by hand.

## Installer

`tools/setup/install-fixes.ts` reads `tools/setup/fixes.json`. It downloads GitHub releases itself, takes manual downloads (NRaas, ModTheSims, Nexus) from a folder, verifies SHA-256 where the manifest has one, backs up every file it overwrites and records what it did.

```
node tools/setup/install-fixes.ts plan      [--set default|all] [--only id,id] [--skip id,id]
node tools/setup/install-fixes.ts install   [same options]
node tools/setup/install-fixes.ts uninstall [--only id,id]
node tools/setup/install-fixes.ts status
```

- `plan` lists what would be installed, which manual downloads are missing and where to get them, and runs the read-only `content-health.ts` on your user folder.
- `--set default` (default) is every entry with `"default": true`; `--set all` adds the opt-in ones.
- `--downloads <dir>`: folder with manual downloads (default `%USERPROFILE%\Downloads\ts3-fixes`).
- Environment: `TS3_BIN` (default `C:\Program Files (x86)\Steam\steamapps\common\The Sims 3\Game\Bin`), `TS3_USER` (default `<Documents>\Electronic Arts\The Sims 3`).
- Backups and state: `%LOCALAPPDATA%\ts3-fixes`. `uninstall` restores the exact previous files.
- After installing: `node tools/setup/check-conflicts.ts [modsDir]` (CLASH = two core mods or two copies of one EA assembly; only one can win).

Entries for test loads only (`user-cc`, `store-repacks-test`, `cache-budget`) read content from the maintainer's own backup and are not for general use.

## `ts3native.ini` switches

The plugin reads `Game\Bin\ts3native.ini` at game start. Every section except `[GC]` is off until verified in game. Details, addresses and test plans: `docs/15-native-patches.md`. `Mode` is the start value of a runtime switch; the probe flips five of them (`/native?routeworker|routebudget|routeasync|alloc|waterjps=0|1`), so one load can A/B them (`profile-world.ts --ab`).

| Section | Key: default | What it does | Status |
|---|---|---|---|
| `[GC]` | `Enabled=1`, `InitialHeapMB=352` (game 40, range 40-1024), `GrowthStepMB=32` (game 8, range 8-256), `BudgetCeiling=0` (unchanged) | Boehm GC initial heap and growth step: far fewer forced (stop-the-world) collections. The env var `GC_INITIAL_HEAP_SIZE` overrides the heap size. | verified (docs/04 F15) |
| `[RouteCache]` | `Enabled=0`, `WindowMs=2000`, `MaxEntries=4096` | A route request that just failed with no path fails again at once for `WindowMs` instead of searching. | built, not run in game |
| `[RouteWorker]` | `Enabled=0`, `Mode=0` (runtime `routeworker`) | Each route search runs on a worker thread while the sim thread waits. | works in game, no gain alone |
| `[RouteAsync]` | `Enabled=0`, `Mode=1` (runtime `routeasync`) | The Sim's task sleeps while its route is planned on the worker. Needs `ts3fixes`. | **paused**, see docs/18 |
| `[RouteBudget]` | `Enabled=0`, `Mode=1` (runtime `routebudget`), `MaxNodes=0`, `MaxUs=100000`, `OnlyFlagged=0` | Caps one route search by nodes or time; over budget it fails the game's own way. | **paused**: stranded Sims |
| `[SaveWrite]` | `Enabled=0`, `Mode=1` | Times each save step; mode 1 copies the old save to its `.backup` on a worker during the package write. | built, not run in game |
| `[Decompress]` | `Enabled=0`, `Mode=1`, `Verify=0` | Faster RefPack decoder (identical output on 3.7 GB of resources offline). `Verify=1` compares with the game's decoder. | built, not run in game |
| `[Alloc]` | `Enabled=0`, `Mode=1` (runtime `alloc`), `ThreadKB=64`, `MaxMB=4`, `PerBin=32` | Per-thread cache of freed small chunks in front of EA's allocator (no heap lock on a hit). | experimental, +19% in one A/B |
| `[WaterJps]` | `Enabled=0`, `Mode=1` (runtime `waterjps`), `Verify=0` | Water route jump search as one inlined loop (3.9x offline, identical results). `Verify=1` compares with the game's code. | built, not run in game |

Install the plugin with the GC section only first. Turn on one experimental section at a time, on a test save, and keep backups.
