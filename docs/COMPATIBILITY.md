# Compatibility with other mods

Date: 2026-11-22 (scan of our v0.3.1 mods with their release settings). Data: [`docs/data/compat-matrix.csv`](data/compat-matrix.csv) (717 rows).

**Every verdict here comes from a static code-overlap scan, not from playing the game with the mod. "No overlap" does not guarantee that a mod works with ours, and "overlap" does not mean it is broken.**

## Check your own Mods folder

To check your own Mods folder, use Apex Radiance's **mod conflict scan** (`ModConflicts`, by loinyx and the Apex Radiance project; it started as a port of our `compat-scan.ts`). It reads your `Mods` folder and `Game\Bin` and changes nothing there. It lists mods that clash with each other:

- the same resource in two mods, with the likely winner
- two core mods replacing the same game file
- old and new copies of one script mod
- two mods replacing the same interaction
- two plugins (`.asi`) patching the same game address. Where our `ts3native` shares an address with another plugin on purpose (one side steps aside), it shows INFO, not a conflict.

How to run it: on Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), download or clone [Apex Radiance](https://github.com/loinyx/Sims3-ApexRadiance). In that folder, run:

```
dotnet run --project tools/mod_conflicts
```

With no folder given, it scans the game it finds. Add a folder path to scan another folder. `--offline` skips its one download of the hook table. Exit code 1 means it found a conflict. A WARNING is a hint, not proof of a clash. Full description: [tools/mod_conflicts/README.md](https://github.com/loinyx/Sims3-ApexRadiance/blob/main/tools/mod_conflicts/README.md) in the Apex Radiance repository. Thanks to the Apex Radiance team for maintaining it.

The table below answers a different question: which popular mods reach the same game code as **our** mods.

## What the table means

The scan reads a mod file (it never runs it) and compares it with what our mods touch. On the `ts3fixes` side, that is the game methods it patches, each marked live or off with the release settings: 790 of 936 patched methods are live, and 630 of 847 switches are on by default. On the `ts3native` side, it is the hook addresses (1,333). Each address is marked by whether its module is on in the v0.3.1 Steam ini or the EA app ini. One row is one mod file. Plain custom content (CC) packages without overlap are left out.

| Column | Meaning |
|---|---|
| `mod`, `file` | Mod name and the file's place in a Mods folder or MTS download |
| `kind` | `script`, `tuning`, `script+tuning`, `cc`, `archive` (could not be read here) or `empty` |
| `overlap` | Which of our features (and their switches) touch the same code; `[off by default]` marks features whose switches are off in the release |
| `verdict` | `no overlap` (612 rows), `overlap` (104), `overlap, off by default` (0: the mod only touches features that are off), `unknown` (1) |
| `status` | Overlap rows only: `in our test setup` (7), `same code, not tested together` (96), `known limit` (1) |
| `note` | Our reading of the row, when there is one |
| `evidence` | What the scan saw (for example "overrides X::Y" or "replaces module Z"), tagged with the scan it came from |

An overlap means a mod and one of our features reach the same game code. It shows where to look and is not a bug report. Only `in our test setup` is based on game runs: those files are in the install that every one of our game tests uses.

## Coverage

- **All 100** of Mod The Sims' 100 most downloaded TS3 game mods (list of 2026-11-22). Every download was scanned: 49 mods from 2026-11-05 were rescanned and the other 51 were downloaded for this scan ([docs/24](24-community-re-projects.md)). 842 files in total, together with our test install.
- Our own test install: NRaas ErrorTrap and Overwatch, the Sims 3 Store packages, simler90's core mod and other fixes.
- A player's Mods folder of 108 files (Dalexlol, issue #165, scanned 2026-11-02). These rows were not rescanned and still reflect our mods as of 2026-11-02.

Counts for our own scan (609 rows): no overlap 519, overlap 90 (7 in our test setup, 83 same code, not tested together), unknown 0.

## What changed since 2026-11-05

- **Our mods touch much more code now.** v0.3.0 and v0.3.1 added about 440 EA bug fixes in `ts3fixes`, the script compiler and many `ts3native` hooks. The scan now reads its list of patched methods straight from the code (including the `Hook(...)` swaps it missed before) and uses the release default of each switch. A feature that is off by default no longer counts as an overlap.
- **Coverage went from 49 to 100** of the MTS top 100. Rows: 334 to 717. Verdicts: no overlap 308 to 612, overlap 25 to 104, unknown 1 to 1 (the only unknown is in the 2026-11-02 player-folder rows; our scan has none, because `.rar` / `.7z` downloads are unpacked now and zips nested in zips are read).
- **New overlaps with features that are on by default:**
  - Three EA fixes sit in base methods that many interaction mods override: `cleanupvfx` (`InteractionInstance.Cleanup`), `initstacktrace` (`InteractionInstance.Init` / `OnAddedToQueue`) and `serviceshowalarm` (`ServiceSituation.OnServiceStarting` / `CleanUp`). This is most of the new overlap rows.
  - A second core mod: Consort's 2x Weight and Fitness Integration.
  - Castable RabbitHole Replacements (Lyn), which subclasses the hover train station (`hovertraintest`).
  - XCAS now meets more of our UI fixes.
- **Known conflicts** found by game runs were added (below).

## The overlap rows (2026-11-22)

Nobody has found a fault in any of these pairs. We do not call any pair "works" or "compatible". In-game checks for the rows that are not in our test setup are written up as queue `compat-overlap-1122` but have not run yet. This page updates when they finish.

### Harmless overlap (read the code, not tested in game)

"Harmless" means we read the code and found that our hooks and the mod do not get in each other's way. It is not an in-game test.

- **Interaction mods (27 rows, 18 mods):** Weapons System and Death Note (Desecrate), Housecleaning For All Sims, Buy Takeout, Scribbling Pad (Buzz), Non-Core Global Online Banking, Study Skills Online, Toddler Interactions (Cherry), More Alchemy Potions, Cook With Any Ingredient, The Sims 3 Passion And Romance, Napping Mat, Children can care for their lil' siblings, More Baby / Toddler / Pregnancy / Romantic Interactions (TSS), Gardener Service. Their interactions override `InteractionInstance.Cleanup` / `Init` (and Gardener and Buy Takeout also override `ServiceSituation`). Our EA fixes in those base methods run when the mod's override calls the base method. When it does not, that interaction keeps EA's original behaviour. Either way the mod's own code runs as written.
- **XCAS (cmar), 4 files:** replaces the UI module. An IL diff of its Steam 1.67 builds (`tools/mod-method-diff.ts`) found 51 changed methods, and none of them is one of the 28 UI methods our default-on features patch.
- **Castable RabbitHole Replacements (Lyn):** its ZEPHYR station subclasses the hover train station. Our hover train memo turns itself off when a mod declares its own station start-up, so the game's own check answers.
- From the 2026-11-02 player folder (not rescanned): Seasons Expanded, Library_5/10/14/15/16, icarusallsorts EatOutsideRestaurant, NRaas MasterController and Register. Tiny UI Fix is a known limit: one speed-up of ours (AutShadow) may turn itself off with it.

### Real overlap, tested in every run of ours

These files are in our test install, so every game test of ours ran with them loaded.

- **simler90 Gameplay Core Mod** (UPDATE242, Steam): replaces game modules and overlaps most features (see its CSV row). The Mono offload, which is off by default, needs a guard for this mod (`docs/128`).
- **NRaas ErrorTrap**, **NRaas Overwatch**: our features overlap with them and ship with repros.
- **Sims 3 Store packages** (Store Fixes, Updates 001-020, 021-040, 041-051): the same base-method overlap as the interaction mods above.

### Real overlap, game test pending

- **Moar Interactions (Buzz):** its visit interactions override the `Run` methods we patch without calling the base method, so our visit hooks skip them (queue `compat-top`). It also has the interaction base-method overlap above.
- **Overrides.package** (Lyn, player folder): overrides `VisitCommunityLot::Run`, which our hook wraps.
- **2x Weight and Fitness Integration Core Mod (Consort), 47 files** (every version and edition): a core mod with its own `Sims3GameplaySystems`, like simler90's. Every `ts3fixes` patch on that module lands on its copy. It cannot be used together with another core mod, and we have not tested it.

### Real overlap, can't test here

- **simler90 + fixes** (a player's own build): we have no copy.
- **Sims3MonoModder (S3MM, ASI)** (source read, docs/291): it shares Mono call paths with our JIT, fast call path and AutShadow. `ts3native` turns those three off by itself when `Sims3MonoModder.asi` is installed (`[Compat] S3mmStandDown=1`, the default). We have not tested the two together in game.
  - **S3MM's profiler is not compatible with ts3native.** S3MM always installs the profiler at start, and it hooks Mono functions we also hook. Whether it crashes depends on which mod loads first. It also clashes with our `[Verify]` probe, and its numbers are wrong under our JIT. S3MM has no setting to turn the profiler off yet (we proposed one upstream).
  - The rest of S3MM works with the stand-down. We do not ship or install S3MM.

### Known conflicts (seen in game runs, not ours)

Two packages stopped the game on our setup (Steam 1.67, Apex Radiance + RTX Remix bridge, an Isla Paradiso test save) **with none of our mods installed**. We found them while bisecting a large Mods folder ([docs/314](314-kit-ab-mode.md)):

- **`EnhancedWitches.package`** (Enhanced Witches, MTS 608185; sha256 `684514e4...11bb`): the game hangs before the main menu, with this package alone, twice (2026-11-21).
- **`velocitygrass_AddAnyLotSize.package`** (Add Any Lot Size, MTS 471164; sha256 `316f12dc...2bad`): the main menu comes up, but loading the world never finishes, with this package alone, twice (2026-11-22). A tester on the EA app 1.69.47 loads fine with it, so this may depend on the game version or setup.

Seen on our setup, not caused by our mods, and not proven for every setup. If your game loads with one of them, keep it. If it hangs at the same point, try without it first. `compat-scan.ts` flags both by file name (`docs/data/known-bad-mods.json`, status `hang-seen`).

### Tools that rewrite your Mods folder

- **SmellyMods CC String Merge** (MTS, offline tool, added 2026-11-19, source read, docs/304): compatible, no overlap. It runs while the game is closed. It moves the string tables of your CC into one table per language, and with `--merge` it also packs the CC into a few big packages. It leaves script mods (ours included) alone. Nothing of ours hooks what it changes, and none of our caches depend on your CC files.
  - Text can change in two rare cases. A package that replaces one of the game's own string tables (some translations and text edits) still shows its text, but the keys it leaves out now show the game's text instead of nothing. When two CC packages give the same string different text, the tool picks by file name, which may not match the game's own pick for files in different subfolders.
  - `compat-scan.ts` names the tool's output files and flags packages that replace a game string table.
  - After merge mode, known-bad checks by file name no longer work; checks by resource key still do.

### Not scanned yet

- **GoHere** and **Story Progression**: they build on trip types we patch.

## Our scan, for developers

```
node tools/compat-scan.ts "<your Mods folder>" --matrix my-compat.csv
```

It needs Node 22.18 or newer and your game folder (set `TS3_GAME` if it is not in the default Steam path). It only reads files. With a checkout of this repository it uses our current sources and the release inis (`--ini`, `--ea-ini`). Run `node tools/compat-scan.ts --self-check` to check that it works. For players, Apex Radiance's conflict scan (above) is the easier tool.

## Is my PC throttled?

Our fixes move work to other CPU threads. Those gains get smaller on a PC that slows its CPU (heat, power limit, power-saver plan, battery), runs other heavy programs, or limits the game to a few cores.

Builds with `[PcCheck]` (docs/299) check this while you play and write `pccheck-summary.txt` in `Game\Bin`. It has one line that says either "CPU: no throttle seen" or "CPU: clock limited X% of the time", plus the power plan, battery, other programs' CPU use and the game's affinity, priority and efficiency mode. With `[PcCheck] Notify=1` (off by default), a notice appears in the game only when it finds a real problem, with a one-line fix.

The tester kit prints the same short check before it installs (warnings only). The check only reads Windows numbers and changes nothing.

## Report

Found something? Open [an issue](https://github.com/rodneypantonial/sims3-modernization/issues/new) and say what happened in the game. Attach the Apex conflict scan output, your `my-compat.csv`, or the row you doubt. File names in `my-compat.csv` are relative to the folder you scanned, and CC-only packages without overlap are left out. A scan row alone does not prove a problem.
