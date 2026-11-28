# 639. Function coverage map of EA's gameplay scripts, and a sweep of the uncovered top (2026-11-15)

Static only (decompiled scripts, no game run). Goal: find which EA gameplay types no one has reviewed yet, rank them by scanner hits and gameplay reach, and hand-read the top of that list.

## 1. Coverage map

`docs/data/function-coverage-1115.csv`, one row per top-level type (one decompiled file) in Sims3GameplaySystems, Sims3GameplayObjects and Sims3StoreObjects (EP and SP code ships inside these DLLs). Rerun:

```
python3 -I tools/static/function-coverage.py <repo> <repo>/game-copy/analysis docs/data/function-coverage-1115.csv \
  --docs docs [--docs <other worktree>/docs ...] --fixsrc mods/ts3fixes/src [--fixsrc ...]
```

Columns:
- `methods`: methods per type from the call graph, nested types included.
- `swept-by`: scanners with hits on the type. Every scanner ran on every type, so `none` means scanned with no hit. The scanners are semgrep and grep (gameplay-scan.sh), ReSharper InspectCode (bug rules only), InferSharp, the managed-lifecycle scan, foreach-mutate and random-empty.
- `hits`: weighted bug-signal hits.
- `hand-read`: `yes` when a doc names the type as code, or ts3fixes patches it.
- `read-in`: which docs or fix sources name the type.
- `reach`: 0 to 3. 3 covers social, needs and autonomy, family, pets and supernaturals; 2 covers events, objects in daily use, careers and school.
- `rank`: hits × (1 + reach), for uncovered types only.

`hand-read` is a mention test. It overcounts types that a doc only names in passing, so treat `no` as the reliable signal.

| | types | methods |
|---|---|---|
| all gameplay types | 8,131 | 81,704 |
| hand-read before this pass (docs/04-585, ts3fixes) | 1,255 | |
| hand-read after this pass | 1,279 | |
| with at least one bug-signal scanner hit | 1,898 | |

The doc corpus was the docs folders of every local worktree, including docs/583 and docs/584 from their analysis branches, plus every ts3fixes source tree.

## 2. Method

1. Ranked the uncovered types (`rank` column). Top: `OccultImaginaryFriend`, `SubjectScoringFunctions`, `BedDreamPod`, `BorrowHorseSituation`, `Secrets`.
2. Filtered the scanner hits on uncovered types with reach ≥ 2 down to precise shapes: copy-paste, inverted or same-branch conditions, loops with no exit, listener and broadcaster overwrites, wait loops, always-null expressions, reference comparisons and random picks from an empty list. That left 81 sites, and all of them were read.
3. New scan, `tools/static/unset-field-push-scan.py`, for the shape behind finding 1: an interaction whose `Run()` bails out when a public field is null, and a create site that never sets that field. It produced 6 hits, 1 of them real; the other 5 set the field inside `Run()`.
4. Every finding was checked against `mods/ts3fixes/src` in all worktrees, `proven-fixes.txt` and `docs/data/external-bug-leads-1115.md`. It was also checked against simler90 Gameplay Core Mod UPDATE242: its `Secrets`, `CatJungleGym`, `MidlifeCrisisManager`, `InnerBeauty` and `BuffImminentNemesis` were decompiled with ilspycmd. None of the findings is fixed in any of them.

Read in full: `OccultImaginaryFriend`, `BorrowHorseSituation`, `Secrets`, `MidlifeCrisisManager`, `BuffImminentNemesis`, `BuffImminentRomance`, `BuffSickAndTired`, `BuffTragicClown`, `InnerBeauty`, `Charity`, `SlumberParty`, `PoolParty`, `UniversityMascot`, `WatchCatJungleGym` (`CatJungleGym`), `Arboretum` (competition code).

Read at the hit sites only (not counted as hand-read): AcademicSituationBase, AfterschoolActivity, BedDreamPod, PetWatchableFoodInteractionHelper, DaycareToddlerDropoffSituation, DaycareWorkdaySituation, IceCreamTruck, VaccinationSessionSituation, Grocery, SneakOntoLotSituation, GossipManager, DoggySwimHere, OccultMummy, ChewToy, CatCondo, LearnToDriveSkill, TattooChair, MedicalEmergencySituation, OpportunitiesOnCompletionFunctions, GroupInteractionSituation, LaserHarp, OccultFrankenstein, InteriorDesigner, SingerCareer, BoxOfMystery, Hypnotizer.

## 3. Findings, ranked (handed to the fix agent; nothing built here)

| # | Type.Method | Failure | Household-visible | Tag |
|---|---|---|---|---|
| 1 | `CatJungleGym.OnEnterWatchPetBroadcaster` | The broadcaster pushes `WatchCatJungleGym` without `JungleGymRef`, and `Run()` returns false on null. So the watch/laugh reaction never plays, and bystanders get a UserDirected-priority queue entry that dies at once. The user-directed path sets the field. | yes | [verified] |
| 2 | `MidlifeCrisisManager.ExportContent` / `ImportContent` | `mWishesFulfilled` and `mLastUpdatedDate` are not exported. Travel and world moves go through `Bin.ExportHouseholdForTravel` → `HouseholdContents` → `SimDescription.ExportContent`, so the wish count resets to 0 and the crisis ends "Unfulfilled" with no reward. | yes | [verified] omission, [inferred] travel round trip |
| 3 | `Secrets.GetBestBadSecretTextFor` (Relationship case) | The key is `"Gameplay/Socializing/Secrets" + ltr` with no `:`, while the guard and the good-secret path use `Secrets:`. The relationship bad secret never shows (it falls back to gossip or ErrorSecretBad), and the entry is consumed anyway. | yes (player-directed only) | [verified] |
| 4 | `InnerBeauty.ApplyEffects` | Each buff instance adds an unfiltered `kDidMeanSocial` listener that acts on `e.Actor`. With N Inner Beauty Sims, each mean social shows N duplicate notices and resets the timer N times. | yes (rare: needs 2 or more such Sims) | [verified] code, [inferred] frequency |
| 5 | `BuffImminentNemesis.OnSocialization` | Gated on `CanHaveRomanceWith`, copied from `BuffImminentRomance`, so the Imminent Nemesis spell never fires on relatives or on pairs romance rules exclude. It also adds a global unfiltered `kSocialInteraction` listener per buff instance. | yes | [inferred] design intent |
| 6 | `OccultImaginaryFriend(OccultImaginaryFriend)` copy ctor | Re-adding the occult copies the live listener and broadcaster references, whose delegates are bound to the old instance. `SetupSimHandlers` skips re-adding them, so the callbacks run on the stale instance. | yes (rare) | [inferred] |
| 7 | `MoodLamp.LocalizeString(bool, ...)` | Key with no `:`. No callers found. | no (latent) | [verified] |

Global scan for localization keys concatenated without a separator (`"Gameplay/...X" + var`): 6 sites. Live ones: #3. Latent: #7. The two LaserHarp sites discard their result, so they are dead code.

## 4. Ruled out (false positives in the top rank)

- InferSharp `OccultImaginaryFriend` hits: all 19 come from the `TryGetOccultFromSim` out-param pattern.
- AcademicSituationBase: the foreach-remove loops all `break` after removing.
- BorrowHorseSituation: listeners are removed before being re-added and in CleanUp.
- MidlifeCrisisManager same-branches: the branches differ.
- PetWatchableFood: the trait weights are correct.
- IceCreamTruck and AfterschoolActivity: else-if and decompiled switch artifacts.
- BuffSickAndTired: the start/stop logic is consistent.
- TragicClown and InnerBeauty broadcasters: `EndBroadcast` marks the broadcaster for disposal, and the manager's `Dispose` runs the exit callbacks.
- Arboretum: fake contestants are scored null-safe.
- The Tattoo, Daycare, GroupInteraction and RainbowCloud wait loops all have exits.
- The unset-field scan's other hits set the field in `Run()`.

## 5. Next

- Continue down the `rank` column (rows 6 onward, reach ≥ 2). Big uncovered social types with no scanner hits (`ShortTermContext`, `ActiveTopicTestFunctions`, `SocialRuleLHS`/`RHS`) need hand reading, because the scanners do not see their shapes.
- Rerun `function-coverage.py` after each sweep so the CSV tracks what has been read.
- Native RE (REA) was not needed for these findings: all 7 are pure script logic with no native call in the failing path.
