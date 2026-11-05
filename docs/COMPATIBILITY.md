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

## The 25 overlap rows, by status

"Same code" means a mod and one of our features reach the same game code. It is not a problem report: nobody has found a fault in any of these pairs. `status` is the CSV column for this. The first group is the only label based on game runs; the other two come from the static scan alone. Feature names are our `ts3fixes` features; switch names are in the CSV `overlap` column. We do not call any pair "works" or "compatible": the game tests ran with these mods installed, but nobody checked each feature pair.

### In our test setup (5 rows)

The exact file is in our test install's `Mods\Packages` folder, so our game tests ran with it loaded.

- **NRaas ErrorTrap**: replaces `ScriptCore` (patched `ReferenceHashtable` members and `Persist::GetPersistedTypeCode`). Overlaps with OverwatchNotice, OverwatchSpread, ErrorTrapSpread, OverwatchStuck, CachedReflection, RefTable, RouteBackoff.
- **NRaas Overwatch**: our features look up its types by name. Overlaps with OverwatchNotice, OverwatchSpread, OverwatchProbe, ErrorTrapSpread, OverwatchStuck.
- **simler90 Gameplay Core Mod** (UPDATE242, Steam): replaces game modules (`Sims3GameplaySystems`, with patched `Sim` methods such as `FindRoutablePointInsideNearFrontDoor`). Overlaps with EaSpread, CandidateLoops, DisposeSpread, DoorMirror, FewerPlans, Fixes, MemSuspects, NameCache, PlayTrace, PortalAdaptorLeak, ReplanDefer, RouteFixes, RouteSources, ScriptHotSpots, SeasonMarkerCache, UnseenFallbackFinish, UnseenThrottle. Our off-by-default Mono offload needs a guard for this mod (`docs/128`).
- **Sims 3 Store Updates 021-040 and 041-051 (v1.4)**, 2 rows: store objects override `ScriptObject::Destroy` or derive from patched `GameObject`/`ScriptObject` members. Overlaps with MemSuspects.

### Same code, not tested together (19 rows)

Not in our test install, so no game test of ours had them loaded.

- **simler90 Gameplay Core Mod**, 2 rows: the MTS 659969 download and the player's copy with fixes. Same overlaps as above; the player's copy adds Compaction, DeadRefs, EaFixes, EaVacation, PosturePop, VenueMemo, WakePark.
- **NRaas MasterController**, **NRaas Register**, **Library_7** (NRaas common code in the player's folder): our features look up `NRaas.Common` types by name. Overlaps with OverwatchNotice, OverwatchSpread, ErrorTrapSpread, OverwatchStuck (Library_7 also OverwatchProbe).
- **Library_5, Library_10, Library_14, Library_16, icarusallsorts EatOutsideRestaurant (fixed)**: objects override `ScriptObject::Destroy` or derive from patched `GameObject`/`ScriptObject` members. Overlaps with MemSuspects (Library_14 also derives from `RabbitHole`, patched door animation).
- **Library_15**: same MemSuspects overlap, plus `Turnstile` overrides `CommonDoor::ImportContent`, overlapping DoorMirror.
- **Moar Interactions (Buzz)**, 2 rows (MTS 437344 download, the player's copy): overrides `VisitCommunityLot::Run` and `VisitLot::Run`. Overlaps with Fixes.
- **Overrides.package** (the player's folder): overrides `VisitCommunityLot::Run` and derives from patched `GameObject`/`ScriptObject` members. Overlaps with Fixes.
- **Seasons Expanded (wexxamsims)**: its Gardener service overrides `Service::FindSimForAssignment` and derives from `Service` (patched `RemovePreferredSim`). Overlaps with EaFixes (preferredservice, socialworkeronlot).
- **XCAS (cmar)**, 4 files (basics and full, Steam 1.67 and 1.69.43; MTS 434270): replaces the `UI` module (CAS grids, `UIManager`, hud motive and buff panels). Overlaps with CasGrid, PlayTrace, ScriptHotSpots, UiCaches.

### Known limit (1 row)

- **Tiny UI Fix**: replaces `Sims3GameplayObjects` and `Sims3GameplaySystems`. Overlaps with 29 features (see the CSV row). One speed-up of ours (AutShadow) may turn itself off with this mod.

## Check your own Mods folder

```
node tools/compat-scan.ts "<your Mods folder>" --matrix my-compat.csv
```

It needs Node 22.18 or newer and your game folder (set `TS3_GAME` if it is not in the default Steam path). It only reads files. Run `node tools/compat-scan.ts --self-check` to see that it works.

## Report

Found something? Send your `my-compat.csv` (file names are relative to the folder you scanned; CC-only packages without overlap are left out) or the row you doubt through [an issue](https://github.com/rodneypantonial/sims3-modernization/issues/new). Say what happened in the game; a scan row alone does not prove a problem.
