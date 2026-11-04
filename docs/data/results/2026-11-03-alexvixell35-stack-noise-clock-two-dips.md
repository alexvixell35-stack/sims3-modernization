# Harness: one clock dip no longer voids a run (2026-11-03, alexvixell35-stack)

`machine-load.ts` marked a run NOT comparable when any sample's `% Processor Performance` fell below 80% of the first
sample. The early verdict (`earlyNoise`, 3 samples after the profiler's first minute) applies the same rule, so a single
boost dip of an otherwise quiet machine stopped an A/B about 6 minutes in.

## Seen

Old-save A/B job, 2026-11-03 23:30 [measured, queue row]:
`machine load: other CPU mean 4.0% max 4.6% ..., clock 168% -> min 134% (3 samples): NOT comparable: clock fell to 134%
from 168%`. 134 < 0.8 x 168 = 134.4; the other two samples were above the line, and other CPU was 4%. The same night two
other jobs passed with dips to 85% and 89% of their first sample.

## Change

`PERF_DIPS = 2`: the clock part of the rule needs at least 2 samples under the line. One such sample is reported in the
line (`min 134% (1 dip, ignored)`) and does not void the run. The rule is shared by the early verdict and the end-of-run
summary, so the dip is not just moved to the end. A/B runs still get the per-side clock lines (`wait-ab.ts`), which show
whether a dip fell on one side. The other-CPU rule is unchanged.

## Checks

`node tools/harness/machine-load.ts --verdict-only`: PASS. New cases: one dip to 70% gives `''` early and
`(1 dip, ignored)` in the line; two dips (75%, 70%) still give `clock fell to 70% from 100% (2 samples under 80%)`.
