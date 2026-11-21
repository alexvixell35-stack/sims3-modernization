# 640: the sim thread waits out a foreign boat job at a forced footprint drain (DiscYieldForeign)

2026-11-17, static plus log analysis, built, selftest `TestDiscYield` (`native/ts3native/discyield-test.h`), NOT RUN in game.
Switch `[RouteAsync] DiscYieldForeign` (ts3native.ini, default 0). No ts3fixes change, no counter slot, no new hook and no new
game address (log lines only). Lever "J1" of docs/247 §13.4 and docs/639.

## 1. Symptom [measured]

Isla Paradiso, `route jobs: sim waited 303-382 ms for a job at disc (route X, foreign job, task could yield yes)`: 5 lines in
`phase-20261117-1940`, 9 in `phase-20261117-2211` (`unseenboatland` on). Each is a sim-thread hitch of that length. The job is
the A10 foreign `ReplanFromPosition` (kind 3, follower = a boat; release `ForeignKinds=4`): the preceding line is
`route async: long job 344 ms, A10 foreign START, IC kind 3, route <same>, scope unseen`. Bridgeport, Appaloosa and Starlight
have none (no boat replans).

## 2. Root cause [verified, source]

- `SwNote` (ts3native.cpp) prints the line for every sim-thread job wait >= 300 ms; site `disc` = `DiscDrain` (discourage.cpp).
- `DiscDrain(force)` applies the deferred discourage footprint records (docs/84 §2.4, docs/89). It needs world `+0x918`, which
  the worker holds for the whole job (H6 S3b), so with a job running it calls `WaitAsyncDone(SW_DISC)` and blocks until the job
  ends (2 s watchdog loop, no cap).
- It is forced by `DiscPoll` when the oldest record is >= `kDiscMaxAgeMs` (250 ms). `DiscPoll` runs at our script-IC points in
  `GetOptionHook` (detour of EA `Route.GetOption` IC `0x74D040`): Route.Plan START / READY / CANCEL and the A10-channel START
  (kinds 1-3, foreign kind 0), plus about 12 D1/D2/reader/world/save sites inside EA native calls.
- `task could yield yes` is only `CanYield()` sampled for the counters (`swYieldable`, `dsYield`): no code path used it. The
  drain had no way to give up its turn, so even a caller whose task sits at its root interpreter state (it could sleep a tick)
  froze the whole sim thread for the rest of the foreign job.

The drain is a correctness step only for what plans next: a plan must not run past footprint records older than 250 ms
(the bound the deferral design accepts). It is not needed for the caller's own result when (a) its job is the one running or
already done (planned on the footprints of its START), or (b) its START is not queued yet. So at START / READY the wait can be
replaced by a park, provided the parked job is queued only after the records are applied. CANCEL, a queued job (the drain is
what stops the worker from claiming it past the records) and the native IC sites keep the wait.

## 3. The switch (ts3native, `[RouteAsync] DiscYieldForeign=1`)

`DiscYieldOk` (discourage.cpp) skips the forced drain at START / A10 START / READY only when all hold:
- switch on, records queued, oldest >= 250 ms (DiscPoll would wait), sim thread;
- a job runs and it is a foreign one (`g_jobOwn == 0`: fb or af, the boat replan class);
- the caller's route is known unseen (ts3fixes scope 2 or an UnseenFallback mark, `DyKnownUnseen`); household, selected and
  unknown scope keep the wait (`HhExempt`; `[Experimental] HouseholdToo=1` lifts it, the plan is the same, only later);
- `CanYield()` (the caller can sleep);
- the oldest record is < `kDyMaxAgeMs` (1000 ms): the hard ceiling, past it everyone waits as before.

What happens instead:
- **START** (kind 0 or A10): `AsyncStartBody` parks it as `J_PEND` with `dy = 1` (no M1 test now: a job runs, it could only say
  busy) and answers yes; the task sleeps and polls READY. If `AsyncStartBody` refuses for any other reason (Active, LOS, NoYield,
  queue full, ...), `DyStartEnd` runs the skipped drain before returning, so the sync plan that follows sees the same footprints
  as without the switch.
- **READY** (`DyReadyMay`): yields only if the caller's job is the one running, is done, or is a `dy` park with ticks left. For a
  `dy` park, `FbRetry` keeps it parked while a forced drain is still due (`DiscYieldWait`); once READY's `DiscPoll` has applied
  the records (the first poll with no job running drains without a wait) it runs the normal M1 test and queues the job, which then
  plans on the same footprints as after the old wait. Cap: `kDyTicks` (30) READY ticks, then that READY's `DiscPoll` drains with
  the wait as before and the job queues; `pend` restarts so the M1 test gets its own 30 ticks.
- CANCEL keeps the wait. Nothing changes with the switch off (selftest step 7).

Same plan inputs, same IC call on the worker, so the same route as the wait path [inferred: no game run yet]; the Sim starts its
route some ticks later. Moved waits (docs/247 §13.4): the records still have to be applied; a household START, a native IC site
or the age ceiling may now wait instead.

## 4. Logs

- Per wait >= 300 ms, now with the drain point: `route jobs: sim waited 326 ms for a job at disc (route 257E8CC4, foreign job,
  task could yield yes, drain point A10 START)` (names: START, A10 START, READY, CANCEL, plan, D1 add, D1 remove, usf, fp2,
  fp2 reader, private fp, ghost census, ghost op, world, save). Always on: tells whether the IP lines are at a point this switch
  reaches.
- At startup: `disc yield: DiscYieldForeign=<0|1> (docs/640 ...)`.
- Per yield (10 a minute): `disc yield: READY skipped a forced drain (oldest record 312 ms, 4 queued) while foreign job 257E8CC4 runs`.
- Every 60 s with the counter lines (also UserDiag op 34): `disc yield: mode 1 yields START a A10 START b READY c, kept not
  unseen d no yield e age cap f, parked capped g max ticks h, max age i ms`.

## 5. Test

- Selftest `TestDiscYield`, run inside `TestFallback`: the decision (off, young record, own job, no job, household, nested call,
  age ceiling), `DyReadyMay` per job state, START park + READY yield without a wait then queue and collect, the tick cap drain at
  READY, a household START that waits, a refused START that drains before its sync plan, CANCEL, switch off.
- In game (IP, night, `unseenboatland` on as in phase-20261117-2211): first one run with the switch off and this build to read the
  drain points of the `sim waited ... at disc` lines; then off / on. Pass: on arm has no `sim waited >= 300 ms at disc ... drain
  point START|A10 START|READY` lines, `disc yield` totals > 0, `parked capped` near 0, no rise of `swMaxUs` / other drain points.

## In-game result (2026-11-18, s101 off / s102 on, Isla Paradiso, asi 4ff453a1)

Not proven; keep off. Off: 3 game hitches up to Sun 11:24 PM, 3 disc waits. On: 12 hitches to Mon 1:23 AM (8 by 11:24 PM), 2 disc waits; yields START 1, A10 START 3, READY 13, parked capped 0. Every remaining disc wait names `drain point fp2 reader`, which this switch does not cover, and most IP night hitches have no disc wait at all: they sit next to kind-3 boat jobs, so the sim thread blocks elsewhere while the job holds the world lock. Next: stall-watch stacks (StallMs=250) on an IP night run (s104).

## Own jobs (2026-11-21)

`[RouteAsync] DiscYieldOwn` (default 0) is the same skip behind an own job, a switch of its own: docs/670 §27.5.
