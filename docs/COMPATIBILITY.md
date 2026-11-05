# Compatibility with other mods

Date: 2026-11-05. Data: [`docs/data/compat-matrix.csv`](data/compat-matrix.csv) (334 rows).

**Every verdict here is a static code-overlap scan, not an in-game test. "No overlap" does not guarantee that a mod works with ours, and "overlap" does not mean it is broken.**

## What the table means

The scan reads a mod file (never runs it) and compares it with what our mods touch: the game methods `ts3fixes` patches and the addresses `ts3native` hooks. One row = one mod file. Plain custom-content packages (CC) without overlap are left out of the table.

| Column | Meaning |
|---|---|
| `mod`, `file` | Mod name and the file's place inside a Mods folder or MTS download |
| `kind` | `script`, `tuning`, `script+tuning`, `archive` (not readable here) or `empty` |
| `overlap` | Which of our features (and their switches) touch the same code |
| `verdict` | `no overlap` (308 rows), `overlap` (25 rows), `unknown` (1 row: an archive we could not read) |
| `status` | Overlap rows only: `in our test setup` (5), `same code, not tested together` (19), `known limit` (1) |
| `note` | Extra remark for a row, when there is one |
| `evidence` | What the scan saw, for example "overrides X::Y" or "replaces module Z" |

An overlap means both the mod and one of our features reach the same game code. It says where to look, nothing more, and it is not a bug report. `in our test setup` is the only label based on game runs.

## Coverage

- 49 of the top 100 Mod The Sims mods, scanned from their downloads.
- A player's Mods folder of 110 files (credit: Dalexlol, GitHub `alexvixell35-stack`, issue #165).
- Our own test install (NRaas ErrorTrap and Overwatch, the Store packages, simler90 and other fixes).

## The 25 overlap rows: initial results (2026-11-05)

"Same code" means a mod and one of our features reach the same game code. Nobody has found a fault in any of these pairs. We do not call any pair "works" or "compatible". These are initial results: this list updates when the game tests finish.

### Harmless overlap (read the code, not tested in game)

"Harmless" means we read the code and found that our hooks and the mod do not get in each other's way. It is not an in-game test.

- **XCAS (cmar)**, 4 files: replaces the UI module, but none of its changed methods is one of the 8 UI methods we patch.
- **Tiny UI Fix**: the hooks overlap harmlessly for our shipped features. Known limit: one speed-up of ours (AutShadow) may turn itself off with this mod. We did not fully trace the mod's generic hooks.
- **Seasons Expanded**, **Library_15** (Turnstile), **Library 5, 10, 14, 16**, **Sims 3 Store Updates** (2 rows), **icarusallsorts EatOutsideRestaurant**: the game methods they touch are no longer patched by us, or our hook only reads after the original runs.
- **NRaas MasterController**, **NRaas Register**: our features only look up NRaas types in the Overwatch and ErrorTrap files.

### Real overlap, tested in every run of ours

The file is in our test install, so every game test of ours ran with it loaded.

- **simler90 Gameplay Core Mod** (UPDATE242, Steam): replaces game modules and overlaps many features (see the CSV row). Our off-by-default Mono offload needs a guard for this mod (`docs/128`).
- **NRaas ErrorTrap**, **NRaas Overwatch**: our features overlap with them and ship with repros.
- **Library_7**: carries the same Overwatch build as our test install.

### Real overlap, game test pending

- **Moar Interactions (Buzz)**, 2 rows: its visit interactions override the `Run` methods we patch, so our visit hooks skip them.
- **Overrides.package** (Lyn): overrides `VisitCommunityLot::Run`, which our hook wraps.

### Real overlap, can't test here

- **simler90 + fixes** (a player's own build): we have no copy.

### Not scanned yet

- **GoHere** and **Story Progression**: they build on trip types we patch.

## Check your own Mods folder

```
node tools/compat-scan.ts "<your Mods folder>" --matrix my-compat.csv
```

It needs Node 22.18 or newer and your game folder (set `TS3_GAME` if it is not in the default Steam path). It only reads files. Run `node tools/compat-scan.ts --self-check` to see that it works.

## Report

Found something? Send your `my-compat.csv` (file names are relative to the folder you scanned; CC-only packages without overlap are left out) or the row you doubt through [an issue](https://github.com/rodneypantonial/sims3-modernization/issues/new). Say what happened in the game; a scan row alone does not prove a problem.
