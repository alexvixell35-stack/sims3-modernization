# metricskillmissing: a career skill metric whose skill is not registered (2026-11-05)

- Change: `fix/metric-skill-missing` 374b3bf (docs/532, issue #166 build 94), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: **simler90's UPDATE242 core without my patches** (my core carries build 94); the harness swapped it in for this job only.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `metric-skill-missing-check`, queue `tools/harness/queues/metric-skill-missing.json`.
- Machine load ok; crashes: none.

| job | result | off arm (`expect broken`) | on arm (`expect fixed`) | `metricskillmissing_names` |
|---|---|---|---|---|
| `bp-metric-skill-missing-check` (rerun) | PASS in 10.5 min | `MetricTitle` and `MetricDescription` threw `NullReferenceException`; value not called (Art Appraiser's first `MetricSkillX`) | both returned text; the value hook ran through `GetCurrentCalcMetric`, score 0 | 2 (gate >= 2 passed) |

The first run failed only in the check: its reflection call of the body-swapped `FindMetricValue` threw `TargetException`. The check now goes through the public `GetCurrentCalcMetric`.

Not tested: the real career panel with such a metric (needs a career XML naming an unregistered skill).
