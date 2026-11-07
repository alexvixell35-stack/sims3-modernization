# Household Sims audit (2026-11-07)

Rule (Rodney, 2026-11-07): our changes may only affect Sims the player doesn't control. Household Sims never count as unseen, even off screen. An unknown Sim counts as household. Results for them must equal vanilla; proven-exact speedups are allowed; crash guards keep a long hard ceiling.

Audited: master `5e873fa3` and v0.2.8 (`5d2a7807`). Code only, no game runs. Switch defaults come from the `TUNING_XML` in `tools/setup/build-fixes.ts` (the Instantiator values in source are not what ships) and from `native/ts3native/ts3native.ini`. Full table: `docs/data/household-audit.csv` (38 rows).

## Key case: off-screen household Sims in `UfWhy`

**Exempt [verified].** `UfWhyBody` (`mods/ts3fixes/src/UnseenFallback.cs:71`, same line in v0.2.8) returns `kHousehold` before any camera test: `s.IsSelectable || s.Household == Household.ActiveHousehold`. An off-screen household Sim, a pet (the second clause) or a Sim at another lot never gets 0. Unknown states also return non-zero (`kNotLive`, `kGone`).

The same test sits first in the other three script scopes: `TownieUnseenBody` (`Fixes.cs:871`), `SimUnseenBody` (`UnseenThrottle.cs:74`) and `DisposeSpread.cs:64`. D-leave excludes `kHousehold` (`UnseenFallbackCore.cs:25`). So **no script path gives a household Sim `UfWhy == 0`, and no change to that check is needed.**

## What bypasses it

The problem is what never asks `UfWhy`:

| Bypass | Why it never asks |
|---|---|
| KindDeadlines, PlanDeadlineMs, SyncCapMs, FbJobCapMs | Native; the job only has a scope if ts3fixes sent one |
| Scope send itself (`UnseenFallback.cs:772`) | Sits after the skips at `:768`: pets, stuck-check plans and non-Sim followers (a household Sim's car or boat) send nothing |
| Watchdog case 49 (`RouteFixes.cs:238-243`) | Runs before the household test at `:244`, on purpose |
| `[UnseenAnim]` | Native `UnObjClass` has no household test (`unseen.cpp:21`); only the 80 m viewer table holds the Sim |

## One central fix

Yes for the route cuts: one rule, **cut only a route known to be unseen.**

1. ts3fixes: move the scope send from `UnseenFallback.cs:772` to the top of `UfPlan` (`:722`), for every follower. A vehicle takes its rider's `UfWhy`; a vehicle with no rider sends unseen, so traffic cars and NPC boats keep their deadlines.
2. Native: `KdExempt` (`plandeadline.cpp:98-99`) cuts only `kdScope == 2`. Unknown then counts as household.
3. Read the same scope in `PdIcUs` (`:432`), `PdPlanBegin` (`:446`), `SyncIc0` (`ts3native.cpp:4311`, ceiling 30 s instead of none) and the job cap (`ts3native.cpp:3748`).
4. `KdExemptSeen=1` by default.

Step 1 means more plans pay for one `UfWhy` (pets, vehicles, stuck checks); it already runs per plan for other Sims. The exemption also covers seen non-household Sims, as `KdExemptSeen` does now, so those hitches come back; `docs/197` §7's repro `kd-exempt-seen-gohere` has not been run.

Not covered by that fix: rows 4, 7 and 8 below.

## Exposed, by player impact

| # | Item | Effect on a household Sim | Smallest fix |
|---|---|---|---|
| 1 | KindDeadlines own jobs | Go Here and any own plan fail at 250 ms, then 500 ms (the player report) | Central fix. v0.2.8 stopgap, ini only: `KdOwn*Ms=0` |
| 2 | KindDeadlines foreign jobs | Plans for pets, the Sim's car or boat and stuck checks cut at 200 ms, even with `KdExemptSeen=1` | Central fix, step 1 |
| 3 | PlanDeadlineMs=300 | Sync replans and NoStart or Foreign plans fail at 300 ms | Central fix, step 3 |
| 4 | Watchdog case 49 | The game's reset after 10 failed plans in 30 sim-minutes; cuts from 1-3 feed the count | Move the `:244` test above it |
| 5 | SyncCapMs=1000 | Sync world A* fails at 1 s, also in plans documented as never cut | Central fix, step 3 |
| 6 | FbJobCapMs=300 (dormant) | Returns if KindDeadlines is turned off, so that is not a fix | Central fix, step 3 |
| 7 | UnseenAnim window | Up to 3 pose passes skipped, time summed: only after a jump over 80 m between pushes (5 ticks), or past 32 actors | Household ids in `PushHeld` (`UnseenViewers.cs:72`), tested first in `AaHoldWhy` (`unseenanim.cpp:103`) |
| 8 | FootstepRO MissNear | A wrong footstep sound for one step while a job runs | `MissNear=0`, or accept as a logged exception |

Unknown: **ReplanDedupe** (a failed replan repeated within 500 ms gets the old failure). Exactness is not proven when object footprints changed; fix with the same scope or prove it.

## Counts

38 rows, several grouping many switches: 8 exposed, 1 unknown, 18 exempt, 9 exact-only, 2 crash guard. Items 1-8 apply to both v0.2.8 and master; master only adds the `KdExemptSeen` code, off by default. `wildflowertask` is on in master only and touches no Sim.

## Decisions for Rodney

- EA bug fixes (row 36) differ from vanilla for household Sims where vanilla throws, hangs or blocks. Filed as crash guards under `fix-ea-bugs-too`; the new rule could be read as stricter.
- Row 8 is cosmetic and costs lock waits to fix.

## Limits

Static only: how often each cut hits a household Sim is not measured [inferred]. "No pulse over 80 m" for the broadcaster and the `errortrapspread` row are [inferred]. Fix sketches are untested; nothing here was changed in code.
