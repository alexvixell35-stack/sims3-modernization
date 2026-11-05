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
| `evidence` | What the scan saw, for example "overrides X::Y" or "replaces module Z" |

An overlap means both the mod and one of our features reach the same game code. It says where to look, nothing more.

## Coverage

- 49 of the top 100 Mod The Sims mods, scanned from their downloads.
- A player's Mods folder of 110 files (credit: Dalexlol, GitHub `alexvixell35-stack`, issue #165).
- Our own test install (the common NRaas, Store and simler90 packages).

## The 25 overlap rows, by mod

Feature names are our `ts3fixes` features. Switch names are in the CSV `overlap` column.

- **simler90 Gameplay Core Mod** (3 rows: MTS 659969 download, our test install, the player's copy with fixes). Replaces game modules (`Sims3GameplaySystems`, with patched `Sim` methods such as `FindRoutablePointInsideNearFrontDoor`). Overlaps with EaSpread, CandidateLoops, DisposeSpread, DoorMirror, FewerPlans, Fixes, MemSuspects, NameCache, PlayTrace, PortalAdaptorLeak, ReplanDefer, RouteFixes, RouteSources, ScriptHotSpots, SeasonMarkerCache, UnseenFallbackFinish, UnseenThrottle. The player's copy adds Compaction, DeadRefs, EaFixes, EaVacation, PosturePop, VenueMemo, WakePark.
- **NRaas ErrorTrap**: replaces `ScriptCore` (patched `ReferenceHashtable` members and `Persist::GetPersistedTypeCode`). Overlaps with OverwatchNotice, OverwatchSpread, ErrorTrapSpread, OverwatchStuck, CachedReflection, RefTable, RouteBackoff.
- **NRaas Overwatch**: our features look up its types by name. Overlaps with OverwatchNotice, OverwatchSpread, OverwatchProbe, ErrorTrapSpread, OverwatchStuck.
- **NRaas MasterController**, **NRaas Register**, **Library_7** (NRaas common code in the player's folder): our features look up `NRaas.Common` types by name. Overlaps with OverwatchNotice, OverwatchSpread, ErrorTrapSpread, OverwatchStuck (Library_7 also OverwatchProbe).
- **Sims 3 Store Updates 021-040 and 041-051 (v1.4)**: store objects override `ScriptObject::Destroy` or derive from patched `GameObject`/`ScriptObject` members. Overlaps with MemSuspects.
- **Library_5, Library_10, Library_14, Library_16, icarusallsorts EatOutsideRestaurant (fixed)**: objects override `ScriptObject::Destroy` or derive from patched `GameObject`/`ScriptObject` members. Overlaps with MemSuspects (Library_14 also derives from `RabbitHole`, patched door animation).
- **Library_15**: same MemSuspects overlap, plus `Turnstile` overrides `CommonDoor::ImportContent`, overlapping DoorMirror.
- **Moar Interactions (Buzz)** (2 rows: MTS 437344 download, the player's copy): overrides `VisitCommunityLot::Run` and `VisitLot::Run`. Overlaps with Fixes.
- **Overrides.package** (the player's folder): overrides `VisitCommunityLot::Run` and derives from patched `GameObject`/`ScriptObject` members. Overlaps with Fixes.
- **Seasons Expanded (wexxamsims)**: its Gardener service overrides `Service::FindSimForAssignment` and derives from `Service` (patched `RemovePreferredSim`). Overlaps with EaFixes (preferredservice, socialworkeronlot).
- **Tiny UI Fix**: replaces `Sims3GameplayObjects` and `Sims3GameplaySystems`. Overlaps with 29 features (see the CSV row).
- **XCAS (cmar), 4 files** (basics and full, Steam 1.67 and 1.69.43; MTS 434270): replaces the `UI` module (CAS grids, `UIManager`, hud motive and buff panels). Overlaps with CasGrid, PlayTrace, ScriptHotSpots, UiCaches.

## Check your own Mods folder

```
node tools/compat-scan.ts "<your Mods folder>" --matrix my-compat.csv
```

It needs Node 22.18 or newer and your game folder (set `TS3_GAME` if it is not in the default Steam path). It only reads files. Run `node tools/compat-scan.ts --self-check` to see that it works.

## Report

Found something? Send your `my-compat.csv` (file names are relative to the folder you scanned; CC-only packages without overlap are left out) or the row you doubt through [an issue](https://github.com/rodneypantonial/sims3-modernization/issues/new). Say what happened in the game; a scan row alone does not prove a problem.
