# 502. A career uniform backup that is uncached again and again (#101, `uniformbackup`)

Issue #101 found two household Sims, in two player saves, whose Career outfit did not load after a reload. Each lost
key was the key of the name that `Occupation.ApplyOccupationUniform` caches each new career outfit under. This doc
explains why that happens and describes the ts3fixes switch that fixes it. The switch is off by default. Status
(2026-10-31): built and self-tested, not run in game in this form. The same sequence proved the bug live, and a patch
of the player's own script build with the same effect ran live (issue #101). Counter 2002 (registry `fixes-2002`).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live in #101;
**[inferred]** = our reading, not yet checked in game.

## 1. Why the Career outfit loses its resource

All methods are in `Sims3GameplaySystems` unless named otherwise.

- `Occupation.ApplyOccupationUniform(SimOutfit)` runs at every promotion or demotion (`SetOccupationOutfitForCurrentLevel`),
  the daily uniform in `LawEnforcement`, and `Film` [verified, #101].
- It returns early when clothes changes are not allowed, when building the new outfit fails, or when the new outfit has
  the old Career outfit's key [verified].
- It builds the new outfit with `OutfitUtils.TryApplyUniformToOutfit`, which caches it under one name per Sim. The outfit
  store gives that name's key K while K is free, and a new key while K is held [verified live, #101].
- Then, before it touches the Sim's Career outfits, it uncaches the static `Occupation.backupOutfit` (shared by all
  Sims), unless that is null or is the outfit the Sim wears. It never clears the field [verified].
- Then it replaces Career[0]. If the Sim wears the old Career outfit, the old one is kept in `backupOutfit` instead of
  being released; otherwise it is released [verified].
- The sequence that loses the outfit [verified live, #101]:
  1. A backup holding K is uncached once, so K is free again, but the field still holds it.
  2. The Sim's next uniform gets K.
  3. The same call caches the new outfit before the backup step, then uncaches the stale backup, which is K.
- The new Career outfit then has no resource. Its `SimOutfit` object loaded its handle first, so it works until a save.
  The save does not contain K, and after a reload the outfit is not valid.

## 2. The fix (`uniformbackup`, `mods/ts3fixes/src/UniformBackup.cs`)

- **Switch off:** EA's method runs.
- **Switch on:** EA's method still runs unchanged. Before it, the hook notes the backup, the outfit the Sim wears (EA's own
  worn-key call) and Career[0]. After it, the hook clears `backupOutfit` when EA uncached that backup and left it in the
  field: the field still holds the same backup, the Sim was not wearing it, and Career[0] was replaced (a call that
  returns early replaces nothing) [inferred from the method's order, §1].
- So each backup is uncached once, and step 3 cannot happen.
- A call that stores a new backup leaves a different object in the field, and the hook keeps it.
- Not repaired: a backup left stale while the switch was off. The repro's `pick` step clears the field so each arm starts
  from the state after load.
- The decision is in `UniformBackupCore.cs` (self-test `tools/selftest/uniformbackup`).

| switch | tunable | default | counters |
|---|---|---|---|
| `uniformbackup` | `kUniformBackup` | off | 2002 `uniformbackup_cleared` (backups that EA uncached and left in the field, cleared) |

## 3. Checks

- **Self-test** (`node tools/selftest/uniformbackup/run.ts`): every input of the decision, plus the cases in §2.
- **Repro** `career-uniform-backup-check` (Baseline-BP, `Fixes.UniformBackupCheck`): the #101 live sequence with EA's own
  calls on a Sim whose occupation hands out a uniform for its level (active household first, then any Sim in the world): wear Career, re-uniform, wear Career, re-uniform, wear Everyday,
  re-uniform, re-uniform. `state` prints the Career keys and whether each loads, the worn key, whether K loads, the
  backup and the counter. Off: after the last step Career[0] is K and does not load. On: every Career outfit loads.
  The off arm leaves that Sim with a broken Career outfit for the session; the harness does not save.
- **Which Sim** [verified IL]: `Occupation.SetOccupationOutfitForCurrentLevel` calls `ApplyOccupationUniform` only when
  `TryGetUniformForCurrentLevel` returns a uniform. The Film career does not (it dresses Sims in work costumes through its
  own methods), so the first run (2026-10-31, a Film Sim) never reached EA's code and its early gate stopped it. `pick` now
  takes the first Sim with a Career outfit whose occupation returns a level uniform.
