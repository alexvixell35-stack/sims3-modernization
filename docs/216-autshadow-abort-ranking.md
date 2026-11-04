# 216. `[AutShadow]` worker aborts ranked by scoring time lost, and the shared-cut verdict

Static and data analysis only (2026-10-31): no build, no game run. Inputs: the per-job native counter deltas (`probe-out/queue/<job>-native.json`, `autshadow` block), the ts3fixes A4-by-reason arrays in the queue records (`autshadow_reasons`, `<queue>.jsonl`), the `ts3native.log` copies and `node tools/harness/mono-cost-report.ts --tag golive-g7` / `--tag satb-act`. Code read: `native/ts3native/autshadow.cpp` (`AsAbort`, `AsWorkerAnswer`), `native/ts3native/autshadow-run.cpp` (`metaCalls`/`metaTicks`), `mods/ts3fixes/src/AutShadow.cs` (`ScoreMeta`, `ScorePair`, worker hooks H5/H11). Data: `docs/data/autshadow-abort-ranking.csv`.

## Summary

- **Aborts are a small lever.** All worker aborts together cost the sim thread **~1.0-1.5 ms/s on BP, ~1.0-2.6 on IP and ~3.9-8.0 on the crowd save** [inferred: measured pairs x measured reference cost per pair, §2]. For comparison, the whole allow-listed reference scoring is 7.1 ms/s BP / 8.0 IP (Mode R), and in G7 the "late" bucket (B14 calls after B13 yielded) is 63-72% of B14 calls in used searches, against 5-9% for abort/off-list objects [measured, report "go-live objects"]. Coverage work on aborts pays most on crowded lots.
- **Ranking** (crowd, high end): `row` 2.1 ms/s, `desirefn` 2.0, `rain` 1.8 (weather-dependent), `stop` 1.4, `simstale` 0.5; everything else < 0.1 (§3).
- **Cheapest real fix: `desirefn`.** The H5 abort fires mostly inside the driver's per-object `HowMuchItWantsSimToCome`, which vanilla calls only inside pairs that pass `Test` (docs/124 §13.2) and which go-live never reads (`Meta[]` is used only by Mode R's `Classify`) [verified in the code]. Skipping that call in Mode 3 removes most `desirefn` aborts and shortens worker jobs by ~45% (docs/122 §14.23), which also cuts `stop` aborts. ~30-60 min + 1 job.
- **Not logged:** which definition or implementor an abort belongs to, and which IC a `row` abort came from after the first 64 log lines (§5 designs the minimal record).
- **Shared cuts: no gain.** In all 14 jobs (G7 BP/IP/crowd, satb-act BP/IP) every sim tick with a B13 call had exactly one: `metaCalls == metaTicks` [measured]. One cut per tick serving all requests saves **0 ms/s**; the only sharing left is reusing a cut across ticks (§4).

## 1. What each reason means

Abort mechanics [verified in the code]: `AsAbort` counts every aborting read (`abortTotal`, `abort[]`) and keeps the first reason per object; `ScoreMeta` reads `Aborted()` after each meta object and marks **all** allow-listed pairs of that object aborted with that reason. Scoring of the object continues after the first abort, so one object can log several abort events. In go-live (docs/124 §17.2 selection rule) an aborted object is never consumed: the game's own B14 (C1) scores it on the sim thread, so the time lost is that object's vanilla scoring.

| Reason | Set where | Meaning |
|---|---|---|
| `row` | native `AsWorkerAnswer` | answer-table miss: key never read before (learned now, answered from the next cut), aged out after 100 unused cuts (`AS_AGE`), cold (`ColdIc`) or distrusted list row of the IC whose reason is `row`. The capped log lines (first 48 misses, first 64 aborts per session, biased to the first minutes) name `Slots_GetParentIdImpl` (506 of 531 `row` lines, 1,002 of 1,296 miss lines), `Simulator_GetTaskImpl` (202 misses) and `Objects_GetPosition` |
| `desirefn` | ts3fixes `AutDesireHook` (H5) on `MetaAutonomyTuning.GetSimCountForVenueWithCustomDesireFunction` | the venue tuning names a desire function by reflection (`Time` column = `Type:Method`, read by `MetaAutonomyManager`); the closure cannot see the target. Reached from `DesiredNumSims` inside `Lot.HowMuchItWantsSimToCome` (and `BakeSaleTable`), so from the driver's per-object meta score and from the venue score of passing pairs |
| `rain` | ts3fixes `ScorePair`, H11 (docs/127 §4) | Seasons on, rain or hail, autonomy search, outdoor non-Sim target: the shelter branch would create an `InteractionInstance` (`sNextId++`) on the worker. Weather-dependent: 6-7% of BP aborts, 12% IP, 19% crowd. Shelter answer built 2026-10-31 (docs/127 §4.1, 0.36.148, Satb=2 only; queue `rain-h11.json` not run) |
| `stop` | native `g_asStop`, ts3fixes `ScoreMeta` | the sim resumed (window closed) before the object finished; KeepPartial keeps the finished objects |
| `simstale` | native `AE_STALE` | a Sim row not refreshed at this cut (TierMs tier 2, docs/122 §14.16) read by the worker; the Sim is promoted for later cuts |
| `list`, `pos`, `privacy`, `ic`, `roomcache` | native / ts3fixes | small (< 0.1 ms/s each); `ic` = UI ICs (`UIManager_*`) seen only in the crowd log |

The desire functions' target methods are not in the repo (the tuning XML is not extracted) and no run logged them [unknown].

## 2. Cost model

- **Pairs per reason** [measured]: ts3fixes A4 by first reason (`autshadow_reasons`) in Mode R, averaged over satb-act BP (`bp-satb-act-1`, `-2`, `-tick`, `-ww`, the runs whose counters match the native files) and IP (`ip-satb-act-1`, `ip-satb-act-2` of 10-31), 530 s kept each [inferred: the report's 10-minute keep]. G7 is Mode 3, which leaves `autshadow_reasons` at 0, so the **crowd** pairs are its native abort events (824 s) times the BP pairs-per-event ratio of each reason (row 0.55, desirefn 0.60, rain 0.80, stop 1.52, simstale 0.62) [inferred].
- **Reference cost per pair** R [measured]: Mode R `refUs / classPairs` = 92.1 us BP, 81.0 IP (crowd uses BP).
- **Low estimate** = pairs/s x 0.65 R. R includes the driver's per-object `HowMuchItWantsSimToCome` (~47% of R) that vanilla does not run; vanilla meta B14 is ~0.6-0.7 x R (docs/124 §15.5).
- **High estimate** = low x (0.7 + 0.3 q/p): aborted objects hold passing pairs more often (reference passes among worker-A4 pairs q = 32.5% BP, 29.6% IP, 40.0% crowd vs answered pass share p = 11.9%, 4.9%, 8.8%), and only passing pairs pay the score share (`Test` ~70%, docs/124 §15.3). Factors 1.52 BP, 2.51 IP, 2.06 crowd [inferred].

Crowd counter check: the native delta for the crowd job is `abortTotal` 76,567 (row 32.6%, desirefn 29.6%, rain 19.1%, stop 8.3%, simstale 7.5%); the planning note quoted 67,872 with a different split (desirefn 34.8%, row 34.6%); that figure does not come from this native file and is not used here.

## 3. Ranked table

| # | Reason / sub-cause | pairs/s BP / IP / crowd | ms/s lost BP | IP | crowd | Known gap | Fix | Effort | Risk |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `row`: first or aged-out reads, mostly `Slots_GetParentIdImpl`, `Simulator_GetTaskImpl` | 8.5 / 9.4 / 16.8 | 0.51-0.77 | 0.49-1.23 | 1.00-2.06 | docs/122 §14.15, docs/124 §14.1 | per-IC counter first (§5); pre-learn parent/task keys for the objects of the request's scored lots at the cut and/or age learned rows 1,000 cuts (table 8,192 -> 16,384 rows, +~4 MB) | ~1-2 h + 1 job | low-medium: more body calls per cut |
| 2 | `desirefn`: H5, mostly from the driver's per-object meta score | 3.5 / 3.0 / 16.4 | 0.21-0.32 | 0.16-0.40 | 0.98-2.02 | docs/122 §12.5 H5, §14.15; docs/122 §14.23 + docs/124 §13.2 | **A:** skip the driver's `HowMuchItWantsSimToCome` in Mode 3 (Mode R keeps it); **B** later: answer the desire function per venue (sim-side bool per venue at the cut, or closure-checked targets) | A ~30-60 min + 1 job; B ~2-4 h | A low; B medium |
| 3 | `rain`: H11 shelter branch | 0.9 / 1.8 / 14.2 | 0.05-0.08 | 0.09-0.23 | 0.85-1.75 | docs/127 §4 (shelter-bit variant = "later option") | per-target shelter bit snapshotted at the cut, worker answers the branch without an instance | ~1-2 h + 1 rain-save job | medium: must never reach `CreateInstance` on the worker |
| 4 | `stop`: window closed mid-object | 1.9 / 4.8 / 11.7 | 0.11-0.17 | 0.25-0.63 | 0.70-1.44 | docs/124 §15.5, docs/206 | no own fix: shorter jobs (row 2 A) and the docs/206 window | follows 2A | low |
| 5 | `simstale`: tier-2 Sim row | 1.3 / 0.7 / 4.4 | 0.08-0.12 | 0.04-0.10 | 0.26-0.54 | docs/122 §14.16 | refresh tier-2 Sims on the request's scored lots at the cut | ~30-60 min + 1 job | low |
| 6 | `list`: distrusted tier-2 list row | 0.2 / 0.2 / 0.8 | 0.01-0.02 | 0.01-0.03 | 0.05-0.10 | docs/122 §14.16 | none | - | - |
| 7 | `ic`: UI ICs on the worker | 0 / 0 / 0.8 | 0 | 0 | 0.05-0.10 | docs/122 guard | find the definition with the §5 record first | ~15 min | low |
| | **All** | 16 / 20 / 66 | **0.97-1.47** | **1.04-2.61** | **3.89-8.03** | | | | |

`desirefn` fix A is ranked first for the next build despite `row` costing more: it is one switch in ts3fixes, needs no native change, and also shortens the worker job (fewer `stop`). How much of `desirefn` it removes depends on how many venue objects have a passing pair; the abort bias above (q 30-40%) means a share stays [unknown: the §5 split answers it].

## 4. Shared cuts (goal 2)

`metaCalls` counts every B13 call ts3fixes sees and `metaTicks` the Simulate ticks with at least one (`autshadow-run.cpp`: `metaTicks` moves once per frame) [verified]. Deltas per job [measured]:

| Job | B13 calls | ticks with a call | calls per such tick | ticks with a call / all ticks | cut cost (avg us, ms/s) |
|---|---|---|---|---|---|
| G7 crowd (BP) | 12,357 | 12,357 | 1.000 | 17.4% | 1,103 us, 10.3 ms/s |
| G7 BP satb / satb-2 / nosatb | 4,494 / 4,928 / 4,634 | same | 1.000 | 18.1-18.3% | 686-751 us, 3.6 ms/s |
| G7 IP satb / satb-2 / nosatb | 5,198 / 5,702 / 4,943 | same | 1.000 | 16.6-17.3% | 454-478 us, 2.3 ms/s |
| satb-act BP (4 jobs) / IP (3 jobs) | 6,510-7,617 / 6,702-6,773 | same | 1.000 | 23-27% | - |

The distribution is degenerate: never two B13 calls in one tick (the game's autonomy runs one Sim's search per tick at most in these saves) [measured; the cause is inferred]. One cut per tick serving all requests therefore saves nothing; `batchMax` 1 and "shared cuts 0" in the report agree. What remains is reusing one cut across ticks: B13 ticks are ~1 in 6, and the per-cut fixed part (prep 84-112 us, publish 28-38 us, audit 9-13 us) is ~120-160 us of the 454-1,103 us cut; the refresh part already follows dirty rows. Upper bound for cross-tick reuse ~0.4-1.3 ms/s if every second cut found nothing dirty [inferred]; whether that happens is not in the counters. Minimal counter if wanted: per cut, ticks since the previous cut and dirty marks + epoch moves since it (histogram 0 / 1-4 / 5+ marks) in the native block.

## 5. Minimal abort record (not built)

What is missing to target fixes by definition: per abort, which IC, which implementor and definition, and for `desirefn` which caller.

- **Native:** `abortIc[]` per IC index (`AsAbort` already gets `idx`; one `LONG` per bound IC, ~128 slots) exported in the native block; replaces the 64-line log sample for `row`.
- **ts3fixes:** in `ScoreMeta` after `Aborted()`: count aborted pairs per (reason, definition index `Def[i]`) and per (reason, implementor `ObjImpl[o]`) into one `int[]` in AppDomain data (e.g. `ts3fixes.autshadow.abortdef`), read by the agent next to `autshadow_reasons`; a `desirefn` split (driver meta score vs pair score) with one static flag set around the driver's `HowMuchItWantsSimToCome`; the venue type and desire function name (`DesiredSimFunction` declaring type + name) logged once per venue type.
- Counter slots: reserve with `node tools/registry.ts reserve fixes|native` at build time (docs/registry.md), not here.
- Cost: a few array increments per aborted object, worker side only; fits a timing run.

## 6. Next build

**Built 2026-10-31 (ts3native 0.36.145 = `game-copy/ts3native.asi.satb9`, ts3fixes `--on kAutLive` = `game-copy/ts3fixes.package.golive6`; queue `tools/harness/queues/golive-dfn.json`):** item 1 without the venue-type log. Mode 3 (`q.Mode == 3`) skips the driver's `HowMuchItWantsSimToCome` on both sides (worker and VerifyEvery reference: `Meta[]` stays 0 on both, so the compare is unchanged); Mode R unchanged. Records: ts3fixes `{"t":"abortdef"}` lines in `autshadow.jsonl` every 1,000 used worker results (cumulative: `desire` = [objects aborted `desirefn` in the driver meta score, `desirefn` pairs], `def` / `impl` = [reason index, short type name, aborted pairs], most first, capped at 1,250 chars); native `autshadow abortic @N cuts (ic=aborts/row)` lines in `ts3native.log` every 2,048 cuts (top 24 ICs, `stop` excluded); 0.36.146 logs every 256 cuts / 250 results (a 10-min BP Mode R job has ~900 cuts).

**Result (queue golive-dfn, `docs/data/results/2026-10-31-queue-golive-dfn.md`) [measured]:** BP crowd Mode 3 Dry vs G7 crowd: worker 57.7 -> 23.5 ms/s, 6.9 -> 2.9 ms per job, aborts 76,567 -> 23,576 (desirefn 22,700 -> 6,877, stop 6,320 -> 928), verify cuts A1-A3 0, G7 top-10/top-1 100%. `desirefn` is not ~0: no driver aborts are left, the rest come from the venue score of passing pairs, ~90% `ShowVenue+AutonomouslyAttendShow+Definition` on `RabbitHole` (fix B). BP Mode R stays exact (A0 27,075).

1. ts3fixes: Mode 3 skips the driver's per-object `HowMuchItWantsSimToCome` (Mode R unchanged) + the §5 record (`abortdef`, `desirefn` caller split, venue-type log) + native `abortIc[]`. One build, one G7-style BP + crowd job.
**`desirefn` fix B parked (2026-10-31, Rodney): < 1 ms/s** (remaining ~3.5 aborts/s in the crowd, ~0.3-0.6 ms/s; the `ShowVenue+AutonomouslyAttendShow` venue scores stay on the sim thread through the existing abort fallback).

2. Then by the record: `row` pre-learn / longer age, the `rain` shelter bit (needs a rain save), tier-2 refresh on scored lots.
3. Bigger than all of these: the "late" B14 bucket (63-72% of B14 calls in used searches) is a consume-timing question, not abort coverage; it belongs to the docs/124 hand-off design.
