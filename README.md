# Sims 3 modernization

Make The Sims 3 run well on modern PCs: less in-game stutter, fewer Error 12 (out of address space) crashes, and room for heavy store content and custom content (CC). First we collect and measure what the community fixes already do, then we reverse engineer the game and build our own fixes where those fall short: a C# script mod, a native ASI plugin, and the test tools to prove each change.

Target: The Sims 3 **Steam** edition, patch 1.67, all expansion and stuff packs, Windows 10/11. Since v0.2.5 also the **EA app** edition, patch 1.69.47: take `ts3native-ea169-<version>.zip` instead of the Steam zip, put it in `C:\Program Files\EA Games\The Sims 3\Game\Bin` and start the game from the EA app ([EA app install](docs/PLAYERS.md#ea-app-version-16947)). Origin, disc and other patches are not supported.

**Just want to play with the fixes?** Download the latest [release](https://github.com/rodneypantonial/sims3-modernization/releases) and follow [docs/PLAYERS.md](docs/PLAYERS.md): what's switched on, and how to install it in a few steps.

**Status:** research project. Latest release: **v0.2.9** (2026-11-07), Steam and the EA app 1.69.47: Sims you play or see no longer fail routes on a time limit, your household stays out of route resets and animation skips, and a crash after running out of script memory is fixed; checked for crashes, not timed. Before it, **v0.2.8** (2026-11-06): one release for Steam and the EA app 1.69.47, with crash fixes (Error 12 on every save of an Isla Paradiso world, Sim thumbnail render, a Sim texture that cannot be created) and the newer routing changes switched on; checked for crashes, not timed. Before it, **v0.2.7** (2026-11-05): EA app 1.69.47 version, Steam files unchanged. Before it, **v0.2.6** (2026-11-05): hotfix for the freeze and crash on the fifth Power Cell and for skipped object cleanup, not run in the game yet. Before it, **v0.2.5** (2026-11-05): cheaper animation for Sims you cannot see, a cheaper held-action check, a fix for a rare memory runaway at game start, crash fixes for custom-content meshes without a material and for Isla Paradiso routing, fixes for paired socials, rabbit hole doors and saves with a removed service, and EA app 1.69.47 support. Results are measured on one laptop (i7-11800H, 32 GB, RTX 3070 Laptop GPU). They are not yet confirmed on other machines; that is one of the things contributors can help with.

`PLAN.md` is the source of truth for what is done, what is open and what comes next.

## Results so far

Measured with the harness (`tools/harness/profile-world.ts`), 10 minutes of play at speed 3, on aged 14-day test saves. **Baseline** = the community fixes layer (DXVK, S3SS, NRaas, Smooth Patch, routing fixes, ...; see `tools/setup/fixes.json`) without our mods. The unmodified game cannot reliably load a save on this machine at all: it runs out of address space while loading (docs/05).

**Baseline vs v0.2.7 (Steam), 10 minutes of play, 2 runs per side.** Cells: Baseline -> **v0.2.7**.

| | Bridgeport | Isla Paradiso | Sunset Valley |
|---|---|---|---|
| Sim steps per second | 48.4 -> **51.2** | 63.9 -> **70.0** | 91.3 -> **89.5** |
| p99 sim step (99% of steps are shorter), ms | 100.9 -> **79.5** | 201.2 -> **110.7** | 58.1 -> **~51** |
| p99.9 sim step, ms | 201.4 -> **139.2** | 351.2 -> **245.0** | 141.6 -> **116.2** |
| Worst sim step, ms | 789 -> **739** | 1954 -> **420** | 1226 -> **506** |
| Freezes of 300 ms or longer, per run | 8.5 -> **2** | 72.5 -> **15** | 10.5 -> **3** |
| Freezes of 1 s or longer, per run | 0 -> **0** | 1 -> **0** | 1 -> **0** |
| Largest free address space after 10 minutes, MB | 1600 -> **1351** | 1827 -> **1575** | 1685 -> **1471** |

Spring baseline saves as used, same machine, the Baselines ran hours earlier, the harness tooling was loaded on both sides, speed varies by town. v0.2.7 uses about 215-250 MB more address space; cause not confirmed: at 10 minutes the v0.2.7 memory log shows EA heaps about 730 MB and the Mono heap 360 MB, and Baseline has no owner log to compare with (inferred).

**Other results:**

- **Address space (Error 12):** DXVK alone frees about 1.75 GB on a fresh save (vanilla: 176 MB largest free block after load and an out-of-memory crash; DXVK: 1,853 MB) [measured, docs/05]. The Sims 3 Performance Patch (S3PP 2.1) frees another ~70 MB with heavy store content (committed -71 MB, largest free block +64 MB); its string cache broke no labels in our check [measured, docs/17 §8-9].
- **Stuck Sims:** treated as solved (2026-10-13): many 10-minute runs with 0 real stuck Sims. New cases are only logged (`docs/data/stuck-log.md`).
- **Save cleanup (opt-in):** on a Bridgeport test save, -266 weak townie relationships and -9 stray other-world Sims, households and Sims unchanged, a second run removes nothing, the save reloads clean (docs/14).
- **Heavy store content:** CC + store repacks add ~29 s to a cold load and ~100 MB address space; load time is the main cost, not stutter (docs/17 §6).
- **Online features:** with our settings the game makes no network calls while playing (docs/16).
- **Routing off the sim thread (on by default since v0.1.4):** route planning on a worker thread; details in [docs/84](docs/84-async-wait-sites.md) and `PLAN.md`.

All numbers come from one machine and one set of saves. Treat percentages as "this is what we saw", not as a promise.

## What is in the repo

| Layer | Path | What it is |
|---|---|---|
| Community fixes installer | `tools/setup/install-fixes.ts`, `fixes.json` | Installs the known community fixes from their official sources (never re-hosted), checks SHA-256, backs up every file it replaces, `uninstall` restores. Also `check-conflicts.ts` (clashing mods) and `content-health.ts` (read-only CC check). |
| `ts3fixes` player mod | `mods/ts3fixes` | C# script mod (one `.package`). Replaces a few game methods at startup: off-screen trip shortcuts, route fixes, stuck watchdog, broadcaster throttle, optional save cleanup and caches. One tuning switch per fix. See `mods/ts3fixes/README.md`. |
| `ts3native` ASI plugin | `native/ts3native` | 32-bit native plugin loaded by Ultimate ASI Loader. GC heap patches and faster water route search, plus opt-in experiments (route cache, route worker, allocator cache, faster decompression, save write). Every patch checks the original bytes first and skips itself on a different exe. Settings in `ts3native.ini`. |
| `ts3agent` test agent | `mods/ts3agent` | In-game script mod that lets our tools read and drive the game (load saves, place Sims, trigger routes, switch fixes, count things). Test-only, never for players. Forked from Sims3-MCP (MIT). |
| Probe and harness | `tools/probe`, `tools/frida`, `tools/harness` | Frida-based probe server (memory, stacks, native counters), stutter profiler, frame-time hook, repro library, profiling and A/B runs, crash and change ledgers. |
| Research | `docs/01`-`docs/85` | Community fixes compared, reverse-engineering findings (native and C#), designs, measurements. Raw data in `docs/data`. |

### What we ship vs what is only for setup

- **We ship only our own fixes:** `ts3fixes.package` and `ts3native`, as GitHub releases with player notes in [CHANGELOG.md](CHANGELOG.md).
- **Community fixes are not part of the product.** The installer (`tools/setup/install-fixes.ts`) sets them up only so that developers and testers get the same game setup we measure against, with one command and as little manual work as possible. They are downloaded from their authors' own pages, never re-hosted or copied into this repo, and stay their authors' work (credits below).

## Status of each fix

| Fix | Where | Status |
|---|---|---|
| Community fixes layer (DXVK, S3SS, NRaas ErrorTrap/Overwatch, Smooth Patch, MonoPatcher, routing fixes, ...) | installer, default set | **on by default**, measured |
| Sims 3 Performance Patch 2.1 (S3PP) | installer `s3pp` | **on by default** (manual download), label check passed |
| Trip shortcuts, route fixes for unseen townies, stuck watchdog, broadcaster throttle, walk-style comparer, portal adaptor leak | `ts3fixes` | **on by default** (`stutter-fixes`) |
| Save cleanup (`kSaveCleanup`) | `ts3fixes` | **opt-in**, verified on one save each (relationships and other-world Sims; empty life-event lists: -26,161, save OK) |
| Lot object query cache (`kSpatialCache`) | `ts3fixes` | **opt-in**, +5% then -6% in two A/Bs: no gain |
| `kRoutableCache`, `kStuckCheckBusy` | `ts3fixes` | **opt-in**, no measurable gain |
| Create-a-Sim hair/makeup grid (`kCasGrid`) | `ts3fixes` | **experimental**, failed in game 2026-10-14: hair still freezes 4.1 s (4.8 s off) before the first tile |
| GC heap and growth step (`[GC]`) | `ts3native` | **on by default** (stable build, release v0.1.0), measured: forced GCs 128 -> 1 in 6 min in Bridgeport |
| Water route search (`[WaterJps]`) | `ts3native` | **on by default** (stable build, release v0.1.0), verified in game: 5.4M searches compared with the game's own code in Isla Paradiso, 0 mismatches |
| Allocator cache (`[Alloc]`) | `ts3native` | **experimental**: +19% and +9% in two A/Bs, but worse worst-case iterations and route fails; crash suspect, needs a long stability run |
| Route worker, synchronous (`[RouteWorker]`) | `ts3native` | **experimental**: stable in game, no speed gain on its own |
| Async route plan (`[RouteAsync]`, job queue, `[RouteGate]`) | `ts3native` | **on by default since release v0.1.4**: route plans asked by a Sim's own task run on a worker thread; plans for another Sim's task still run on the sim thread (the remaining 1 s+ freezes in Sunset Valley, next release's target) ([docs/84](docs/84-async-wait-sites.md)) |
| Waits left while routing runs off-thread: discourage footprints (`[Discourage]`, design D), read-only steering (`[SteerRO]`, S2), footprint rebuild race fix (`[FpRebuildLock]`, C-A), read-only lot front door (`[LotFrontDoorRO]`, L1) | `ts3native` | `[Discourage]` on by default (v0.1.4; footprint deferral `DeferFootprint=1` since v0.1.5); the others **built, off by default** (0.13.0-0.14.0): proven offline against the game's own code; live shadow and on runs queued ([docs/84](docs/84-async-wait-sites.md)) |
| Route search budget (`[RouteBudget]`) | `ts3native` | **paused**: stranded Sims (41 stuck vs 0) |
| Faster RefPack decoder (`[Decompress]`) | `ts3native` | **on by default** (release v0.1.1), verified in game: 0 mismatches over a full load, ~144k decodes in 4 sessions |
| Save write (`[SaveWrite]`) | `ts3native` | **on by default** (release v0.1.1), verified in game: old-save backup copied by a worker thread, saves reload |
| Route negative cache (`[RouteCache]`) | `ts3native` | **off**: tested in game 2026-10-14, 0.2% hits, no gain |
| Faster save object table (`kFastRefTable`) | `ts3fixes` | **on by default** (release v0.1.3): saves 14-24% faster, same save file, save tests PASS |
| Lighter Sim panel (`kMotiveBarImageCache`, `kBuffGridInPlace`, `kSimNameCache`) | `ts3fixes` | **on by default** (release v0.1.3): UI only, nothing in the save changes |
| Save footprint index, delayed flush, compression timing (`[FootprintIndex]`, `[SaveFlush]`, `[SaveCompress]`) | `ts3native` | **off, opt-in** (release v0.1.2): no gain above save-to-save variation on an NVMe SSD |
| Crash fix: a Sim looking for a spot on the shore while routes plan in the background (`[RouteGate]`) | `ts3native` | **on by default** (release v0.1.5): Isla Paradiso boat-exit scenario, 2 runs of 20 firings each, no crash |
| Moving Sims and objects no longer wait for background route plans (`[Discourage]` `DeferFootprint=1`) | `ts3native` | **on by default** (release v0.1.5): Isla Paradiso boat exit, one 8.3 s freeze -> none |
| Fewer short freezes in crowds (`[RouteAsync]` `RefcountGuard=1`) | `ts3native` | **on by default** (release v0.1.5): crowd scenario: fewer freezes of 300 ms or longer (7 -> 2 and 6 -> 5 in two tests) |
| NRaas Overwatch nightly clean-up in steps (`kOverwatchChain`) | `ts3fixes` | **on by default** (release v0.1.5): worst freeze 0.3 -> 0.1 s |
| 3 AM stuck-Sim check in its own task (`kStuckCheckAsync`) | `ts3fixes` | **on by default** (release v0.1.5): worst freeze 1.2 -> 0.14 s |
| NRaas ErrorTrap after-load check spread out (`kErrorTrapSpread`) | `ts3fixes` | **on by default** (release v0.1.5): worst freeze 0.28 -> 0.06 s |

## Quick start: players

Just want our fixes? Download them from the [releases](https://github.com/rodneypantonial/sims3-modernization/releases) page and follow **[docs/PLAYERS.md](docs/PLAYERS.md)** (copy the script mod into `Mods\Packages`, the native plugin into `Game\Bin`; about five minutes). Every release lists everything that is switched on.

Using other mods? See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) for the static compatibility scan of 49 popular mods.

## Quick start: full test install

The steps below set up the full test install (community fixes layer plus `ts3fixes`), the one contributors and testers use.

1. **Back up first:** copy `<Documents>\Electronic Arts\The Sims 3\` (saves, Mods, settings) somewhere safe. Keep a copy of `TS3W.exe` as `TS3W.exe.orig` if you ever patch the exe yourself (our tools never do).
2. Install Node.js 22.18 or newer, then clone this repo.
3. See what will be installed and which files you must download by hand (NRaas, ModTheSims and Nexus need a browser):

   ```
   node tools/setup/install-fixes.ts plan
   ```

   Put the manual downloads in `%USERPROFILE%\Downloads\ts3-fixes` (or pass `--downloads <dir>`). If the game is not in the default Steam folder, set `TS3_BIN=<SteamLibrary>\steamapps\common\The Sims 3\Game\Bin`.
4. Install the default set and check it:

   ```
   node tools/setup/install-fixes.ts install
   node tools/setup/install-fixes.ts status
   node tools/setup/check-conflicts.ts
   ```

   `stutter-fixes` (our `ts3fixes`) is part of the default set; building it needs the game's script DLLs, see [docs/BUILD.md](docs/BUILD.md).
5. To undo: `node tools/setup/install-fixes.ts uninstall` (or `--only <id>`). Backups live in `%LOCALAPPDATA%\ts3-fixes`.

Opt-in entries (`native-patches`, `overwatch-no-stuckcheck`, world fixes, ...) are listed by `plan --set all`; install one with `install --only <id>`.

## Quick start: contributors

- [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md): hardware, software, AI model.
- [docs/BUILD.md](docs/BUILD.md): build the mods and the native plugin, installer usage, `ts3native.ini` switches.
- [docs/TESTING.md](docs/TESTING.md): how we measure, repro library, A/B runs, crash triage, how to report results.
- [docs/TESTING-AND-BUG-HUNTING.md](docs/TESTING-AND-BUG-HUNTING.md): the test harness in one place and how we chase a crash or hitch down to its cause, with a worked example.
- [docs/MEMORY-RESEARCH.md](docs/MEMORY-RESEARCH.md): start here for memory / Error 12 work: what we measured, the tools, how to run them, open tasks.
- [AGENTS.md](AGENTS.md): rules for AI coding agents (and a good summary for humans).
- `PLAN.md`: current state, open items and the next tasks in order. Pick from there or from the GitHub issues.

## Project rules

- **No EA files in git.** No game binaries, packages, extracted or decompiled EA code, saves, dumps or captures. Each contributor works on their own copy of the game. `.gitignore` covers the usual paths; `game-copy/` is for local analysis only.
- **Measure everything.** A fix counts when the harness shows it against the baseline, with build hash and settings. One change at a time; compare in one load with runtime switches where possible.
- **Verified vs inferred.** Every claim in the docs is marked `[verified]` (disassembly, decompiled code or measurement), `[community]` (reported by others) or `[inferred]`. Inferred claims are confirmed before code relies on them.
- **No harm to saves.** Fixes use the game's own functions and end states. Anything that changes save data is opt-in.
- **Respect other authors.** Community mods without a license are studied for behaviour only and never copied or ported; we write our own implementation from a written spec and credit the author.

## License

Code: MIT (`LICENSE`). Documentation (`docs/`, `*.md`): CC BY 4.0 (`LICENSE-docs`). `mods/ts3agent` keeps its upstream MIT notice (`mods/ts3agent/LICENSE`). Third-party fixes installed by the installer keep their own licenses and are downloaded from their authors' pages, never redistributed here.

## Notice

The Sims and The Sims 3 are trademarks of Electronic Arts Inc. This project is not affiliated with, endorsed by or sponsored by Electronic Arts or Maxis. It contains no EA code or assets; you need your own legal copy of the game. Use at your own risk and keep backups of your saves.

## Credits

This work stands on community projects. Thank you to their authors.

| Project | Author | Used for |
|---|---|---|
| [Sims 3 Settings Setter (S3SS)](https://github.com/sims3fiend/Sims3SettingsSetter) | sims3fiend | Engine patches; reference for our native hooks |
| [NRaas mods](https://www.nraas.net/) (ErrorTrap, Overwatch, ...) | Twallan, Chain-Reaction and the NRaas team | Error repair, nightly cleanups, stuck check |
| [Smooth Patch](https://modthesims.info/d/658759/smooth-patch-2-1.html), [MonoPatcher](https://github.com/LazyDuchess/MonoPatcher), [Banding Fix](https://github.com/LazyDuchess/TS3-Banding-Fix) | LazyDuchess | Frame pacing, the method-patching approach our patch host follows |
| [Sims 3 Performance Patch (S3PP)](https://www.nexusmods.com/thesims3/mods/235), [Catalogue Lag Fix](https://www.nexusmods.com/thesims3/mods/242) | Sonja92yu | Address space, load and catalogue fixes |
| [simler90's Gameplay Core Mod](https://modthesims.info/d/659969/simler90-s-gameplay-systems-core-mod.html) | simler90 | Gameplay bug fixes |
| [House Boat Fix](https://github.com/FoulPlay/houseboatfix) | FoulPlay | Houseboat fixes |
| Routing fixes on ModTheSims: [sloped terrain](https://modthesims.info/d/529671/interaction-on-sloped-terrain-enabler.html), [Sims need less space](https://modthesims.info/d/683612/fix-sims-need-less-space.html), [toddler routing](https://modthesims.info/d/685924/toddler-routing-fix.html), [pets need less space](https://modthesims.info/d/683307/fix-pets-need-less-space.html), [ocean play](https://modthesims.info/d/686121/autonomous-play-in-the-ocean-fix.html) | their authors | Routing fixes in the default set |
| World routing fixes | ellacharmed | Optional `.world` fixes (links in `fixes.json`) |
| [DXVK](https://github.com/doitsujin/dxvk) | doitsujin and contributors | D3D9 on Vulkan: the biggest address-space win |
| [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader) | ThirteenAG | Loads S3SS and `ts3native` |
| [Tiny UI Fix](https://github.com/just-harry/tiny-ui-fix-for-ts3) | just-harry | Optional UI scaling |
| [Regul Save Cleaner](https://modthesims.info/d/652440/regul-save-cleaner.html) | Regul | Recommended save cleanup tool |
| [Sims3-MCP](https://github.com/Critical-Reynolds/Sims3-MCP) | Critical-Reynolds | Base of our test agent `ts3agent` |
| [Frida](https://frida.re/) | Ole André Vadla Ravnås and contributors | Live probe and profilers |
| [Ghidra](https://github.com/NationalSecurityAgency/ghidra) | NSA and contributors | Static analysis of TS3W.exe |
| [REA (Reverse Engineer Anything)](https://github.com/morluto/rea) | morluto | MCP server over Ghidra and .NET analysis |
| [ILSpy](https://github.com/icsharpcode/ILSpy) (`ilspycmd`) | ICSharpCode | Reading the game's C# scripts locally |

Full list with versions and notes: `tools/setup/fixes.json` and `docs/07-known-fixes-catalog.md`.
