# 532. A career skill metric whose skill is not registered throws in the career panel (`metricskillmissing`)

Issue #166 build 94. A career level's "skill X" performance metric (`MetricSkillX`) can name a skill this game does not
have; EA then throws each time the career panel fills and scores the metric as if the Sim had the skill at level 0.
This doc describes the bug, the ts3fixes hooks (switch `metricskillmissing`, off by default; counters 2059-2060,
registry `fixes-2059`; ledger §936) and the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `MetricSkillX`'s constructor sets `SkillGuid = SkillManager.sSkillEnumValues.ParseEnumValue(row.GetString("Args" + n))`
  **[verified]**. The parser looks skill names up; the player's career XML (SimsMx Career Metrics, merged with
  RealisticWages) gives Video Game Developer levels 1-3 and Engineer 4-7 the id `0xC7DB6873` there, which no skill in
  that game has (`GetStaticSkill(3353045107)` returned null live) **[verified live, 2026-10-30, run 129 of the player's
  core builds]**.
- `MetricTitle` and `MetricDescription` are `SkillManager.GetStaticSkill(SkillGuid).Name` and a `LocalizeString`
  **[verified]**: with no static skill they throw `NullReferenceException`. The career panel fills these for every
  metric of the Sim's level (`Career.get_PerfFactors` <- `CareerPanel.FillCareerInfo`); in the player's run, NRaas
  Overwatch's day callback refreshed the panel through `HudModel.OnSimAgeChanged`, so one script error a sim day
  **[verified live]**.
- `FindMetricValue` is `career.OwnerDescription.SkillManager.GetElement(SkillGuid)?.SkillLevel ?? 0` **[verified]**: 0
  for a skill nobody can learn. `PerfMetric.CalcMetric` scores a value below `mMap0` as negative (0 only at `mMap0`)
  **[verified]**, so on levels whose `Map0` is above 0 the job loses performance for good.

## 2. The fix

`mods/ts3fixes/src/MetricSkillMissing.cs`, hooks swapped over `MetricSkillX.FindMetricValue`, `MetricTitle` and
`MetricDescription` (`EaFixes.cs` patch list). With the switch on and no static skill for `SkillGuid`: the title and the
description are EA's strings with an empty skill name, and the value is `mMap0`, which `CalcMetric` scores as neutral
(0). A registered skill, or the switch off, runs the originals. Counters [2059] `metricskillmissing_names` (titles and
descriptions), [2060] `metricskillmissing_values`.

The metric stays on the level: the panel shows it with no skill name and it neither helps nor hurts. Dropping it from
the level would change what the panel lists, which a hook should not do.

## 3. Test

Repro `metric-skill-missing-check`, queue `tools/harness/queues/metric-skill-missing.json`, save Baseline-BP, **simler90's
UPDATE242 core without the player's patches** (the player's core carries build 94). The agent check
`Ts3Fixes.Fixes.MetricSkillMissingCheck` takes the first `MetricSkillX` of any career level and gives it `0xC7DB6873`
for the calls, restored in a `finally`. It refuses to run if that id is a registered skill. Off arm (`expect broken`):
EA's `NullReferenceException` from `MetricTitle` and from `MetricDescription` (EA's `FindMetricValue` is not called: the
prototype career has no owner, which would throw for another reason). On arm (`expect fixed`): both return text and
`GetCurrentCalcMetric` (public; it calls the protected `FindMetricValue`) scores 0 with the value hook run once; `metricskillmissing_names` reaches 2. Early gate: that counter at 3 min.

Result, first run (2026-11-05, pure UPDATE242 core, Art Appraiser's first `MetricSkillX`): the off arm passed (EA's
`NullReferenceException` from `MetricTitle` and `MetricDescription`). On the on arm both returned text, but the check's
reflection call of `FindMetricValue` threw `TargetException` (the call, not the hook). The check now goes through the public
`GetCurrentCalcMetric`. Rerun (2026-11-05, same core): **PASS** in 10.5 min. Off: title and description threw
`NullReferenceException`. On: both returned text, the value hook ran and the score was 0 (`metricskillmissing_names` 2).

## 4. What is left

- The real career panel with such a metric (the check calls the methods directly). Needs a career XML that names an
  unregistered skill, as the player's does; the harness has no step to install one.
