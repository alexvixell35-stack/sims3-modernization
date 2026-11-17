# 276. Mono offload leftovers: why they failed or stalled, redesigns, verdicts (2026-11-16)

Static read plus existing data only: no build, no game run, no queue run. Base: master 3d737d65. Sources: docs/129, docs/208 §9, docs/219, docs/126 §8, docs/124 §17, docs/222 §11, docs/270 §30, docs/271, docs/272, docs/524 §6, `docs/data/races-handoff.md` §6, `docs/data/tier2-local-phase-handoff.md`, `docs/handoff/mono-offthread-next-1113.md`, result files in `docs/data/results/`, and the queue rows in `probe-out/queue/*.jsonl` (named per claim).

Tags: **[verified]** = code or doc line read for this note; **[measured]** = counter or result file named; **[inferred]** = arithmetic or reasoning, not confirmed in the game.

## Summary

| # | Item | Real cause of failure or stall | Verdict | Next step |
|---|---|---|---|---|
| 1 | Closest visible lot `autofflot` | Mode R works (IP A0 518 of 1,064, A2 0, exc 0). There is no apply because the camera lot is by definition what the player is looking at, and the residual left after `kHoursMemo` is ~1 ms/s. `kHoursMemo` itself has never run | **Drop the apply**, keep Mode R code. **Save `kHoursMemo`** instead | hoursmemo exactness stamp + correctness/timing |
| 2 | Tier-2 rows `autofft2` / `Tier2IcRows` / local driver | Not the wrong place for unseen Sims: there is **no unseen local-phase work**. EA's B8 gate already keeps unseen Sims out (0 unseen non-household local searches on BP/IP/SV). The local-phase cost is household (SV 70 ms/s), seen NPCs and side entries | **Drop as an unseen offload.** **Open a new lead**: the SV household local-search rate | SV search-rate probe; HouseholdToo local exact path only after that |
| 3 | D10 IC pass-through | Never built. Its proof runs (`races-r1`, `races-r2c`) never ran. With `Satb=2` only ICs whose inputs are load-constant stay exact, and the upper bound is 0.5-0.6 ms/s | **Drop** as a performance item. Keep the race runs as a proof gate | none |
| 4 | Seen-crowd go-live `autliveseen` (B11) | Exact (dry: 540 seen searches, 0 misses). In acting runs it loses: cost guard ratio 3,438 per mille, all 8 windows skipped. The fixed per-search cost (cut, lock waits, band re-score) is larger than the saved B14 scoring | **Dropped 2026-11-17** (§4.3): cut-riding has nothing to ride, go-live cuts never carry 2 requests | none |
| 5 | G3 whole-stack cost | Every attempt was noise-void. The structure also cannot resolve it: the sim runs flat out, so 5 ms/s ≈ 0.1 ms/iter against a slice sd of ~3 ms/iter. Separate loads start from different states. The prepared G3 queue has an `ourMods:false` arm, which the rewind cannot reuse | **Save** with a two-part design: G3a same-start rewind segments, G3b installed-only price | fix the rewind refusals, then the G3a queue |
| 6 | D7 K_LIFT | ≤ 0.004 ms/s; would need a runtime relocator for EA handler bytes | **Drop (confirmed)** | none |

Ranked next work: §7.

## 1. Closest visible lot (`autofflot`, docs/129 rank 4, docs/208 §9.2, §9.5, §9.6)

### 1.1 What the data says

- **Cost** [measured, docs/129 §2.4]: `Camera.Simulate` costs 6.0 ms/s on BP and 7.5 ms/s on IP. 31 of its 36 samples are in `GetClosestVisibleLot`: 20 in `Lot.IsOpenVenue` (the opening-hours lookup that `kHoursMemo` memoises) and 5 in the quadrant/distance math.
- **Mode R works on both worlds.**
  - BP 11-08: PASS [measured, `2026-11-08-queue-offload-ranks.md`].
  - IP 11-08: FAILed on houseboat `ic` aborts [verified cause, docs/208 §9.5].
  - IP 11-12, after HouseboatToWorld (§9.6), `ip-m1112b-moder-offranks` [measured, `probe-out/queue/master1112b-verify-2026-11-12.jsonl`]:

    | items | A0 | A5 | A2 | A4 | exc | dry |
    |---|---|---|---|---|---|---|
    | 1,064 | 518 (48.7%) | 546 (51.3%, stopped or overrun) | 0 | 0 | 0 | 4 checks, 4 equal |
- **Correction:** `docs/handoff/mono-offthread-next-1113.md` row 6 says `autoff_lot_a0` "is not among changed counters". That is wrong. The md note column was cut off, and the jsonl row has `autoff_lot_a0` 0->518 [measured]. The houseboat fix works.
- **`kHoursMemo` has never run** [verified]:
  - `Instantiator.cs:79` has `kHoursMemo = false` ("opt-in until measured").
  - Its only queue, `tools/harness/queues/ts3fixes-hotspots.json`, still says "DRAFT, not run".
  - No result file in `docs/data/results/` mentions `hoursmemo`.

  Every gain figure for rank 4 is "after `kHoursMemo`", and that switch was never turned on in a measured run.

### 1.2 Why no apply was built, and why one is not worth building

1. **The camera's pick is visible by design** [inferred, handoff row 6]. The closest visible lot is the lot under the player's camera. It feeds `sCurrentFocusedGreetedLot` / `LotFocusChanged` (greetings, lot focus for the active household). An "unseen only" gate cannot apply. Household relevance is direct, so a stale pick (up to 15 ticks in the dry check, docs/208 §9.2) would change a visible event's timing. Only a tick-exact apply would be allowed, and a worker answer is ≥ 1 tick old.
2. **What is left after `kHoursMemo` is small** [inferred from the measured shares]:
   - Remainder: 11/31 × 6.0 ≈ 2.1 ms/s on BP, 2.7 on IP.
   - Of that, the worker could move only the pick math: 5/31 × 6.0-7.5 ≈ 1.0-1.2 ms/s.
   - The sim-thread request (lot lists, ≤ 32 Sims with their `IsOutside` reads, houseboat answers) redoes part of the walk.
   - Half the items stop or overrun (A5 51%), so they fall back to the game.
   - Net ≤ ~0.5 ms/s, against the ~2% / 3 ms/s bars.
3. **`kHoursMemo` takes the big share in place, with no worker** [inferred]:
   - It covers 20/31 = 65% of the camera cost: ~3.9 ms/s on BP, ~4.8 on IP.
   - It also covers its other callers (`Autonomy.CheckAvailabilityOnLot`, `AboutToPlanRouteCallback`, `DoorSingleGender.IsAllowedThrough`, venue tests). docs/74 §6.3 estimates 5-8 ms/s in total.

### 1.3 Redesign: make `kHoursMemo` exact, then measure it

- **Exactness gap** [verified, docs/74 §6.3]:
  - The memo keeps (result, open, close) per lot per sim tick.
  - The only change within a tick is a `BachelorParty` starting on a bar lot, which moves the closing hour; the memo shows it one tick late.
  - `DoorSingleGender.IsAllowedThrough` and route callbacks reach this for household Sims too, so under CLAUDE.md the household result must equal vanilla.
- **Fix design** [inferred, not built]: a generation word, bumped by a hook where a `BachelorParty` situation starts (its constructor or first state) and when the lot's venue type changes (edit mode already clears the memo, per the pause rule). A memo entry stamped with an older generation is a miss. That makes the memo exact for every scope, so it needs no household exemption and no HouseholdToo. `hoursmemoverify` (1 in N calls checked against the game) stays the tripwire.
- **Expected:** ~3.9 BP / ~4.8 IP ms/s from the camera path alone, up to 5-8 ms/s with the other callers [inferred]. That clears the 3-5 ms/s default-ON rule if it shows.
- **Effort:** stamp hook ~1 h (one agent build). Repin `ts3fixes-hotspots.json` (or a hoursmemo-only queue) ~20 min. Game runs: 2 correctness jobs (~30 min with loads), then one bundle timing slot on a clean night.
- **Stop rule:**
  - `hoursmemoverify` mismatches > 0 once the stamp exists: fix once, then drop.
  - No clean ms/s gain in the bundle after one fix round: park (throttles park rule).
- **Risk:** low. Read-only memo with a verify switch, and the stamp only adds misses.
- **`autofflot` verdict:** drop the apply. Keep the Mode R code and counters, and give it no further queue slot.

## 2. Tier-2 rows and the local-phase driver (`autofft2`, `Tier2IcRows`, docs/208 §9.1, docs/126 §8, docs/219)

### 2.1 Why the measured unseen gain is 0

There is no unseen work, so the place was not wrong for unseen Sims [measured, L0, `2026-11-10-queue-tier2-local-l0-m1110.md`, `-ip.md`, handoff log in `docs/data/tier2-local-phase-handoff.md` l.177]:

- BP, IP and SV each had **0 unseen non-household local searches** (`aut_l0_n_unseen` 0).
- EA's B8 gate (`ShouldRunLocalAutonomy`: local phase only for high-LOD Sims, which are mostly seen) refused the local phase for 12,227 / 5,952 / 16,139 searches.
- The rows and the driver target a class EA has already emptied.
- The rows also had not passed their own Mode R:
  - IP 11-12, `autoff_t2`: items 16,872, A0 2,191 (13%), A4 8,439 (50%), A5 6,242 (37%) [measured, same jsonl as §1.1].
  - `bp-l8-dry` FAILed twice on `autoff_t2ic_eq=0`. Fix 31988a92 is queued (PLAN, lane L8).

### 2.2 Where the local-phase cost actually is

ms/s, C0+B8 floor subtracted [measured, same L0 files]:

| Class | BP | IP | SV |
|---|---|---|---|
| household | 7.76 | 4.52 | **69.96** |
| seen non-household | 2.65 | 0.10 | 0.12 |
| side entries (raw) | 11.66 | 3.63 | 7.19 |

**The SV household figure is a new lead.** Raw counters [measured]:

| | Searches | Window | Rate | Time per search | Pairs per search |
|---|---|---|---|---|---|
| SV household | 3,163 | 605 s | 5.2/s | 13.4 ms | 727 |
| BP household | 397 | 605 s | 0.66/s | 11.8 ms | 684 |

- The cost per search is the same; SV runs **8x more** household local searches per second.
- Two possible causes, both [inferred], neither checked:
  - a bigger active household;
  - repeated failing searches (autonomy finds nothing and retries).
- No other doc follows this up (grep for the figure finds only the L0 log line).

### 2.3 Redesign options

| Option | What | Household rule | Expected | Effort | Risk |
|---|---|---|---|---|---|
| **A. SV search-rate probe** (no offload) | One SV job with the existing `autt2l0` probe plus a per-Sim search count and B8 reason (or the g8 counters `g8_hh_searches` per household Sim-hour, which exist on master; BP reads 0.86 searches per household Sim-hour [measured, `golive-checks-1115`]). Decide: real play load, or a retry loop | measurement only; a loop found = EA bug fix behind a switch (fix-EA-bugs rule), exact by construction or household-exempt | 0 directly; if it is a loop, a fix could take a large part of SV's 70 ms/s on saves like it [inferred upper bound] | ~1 h build (counter add) + one 12-min SV job | none (read-only) |
| **B. HouseholdToo local exact path** (docs/271 Y3 + Y4 applied to B9) | Household local phase on the worker. The exact consumer pushes every score live through C1, as the seen path does. Needs L2-L7 of the local-phase plan | realistic-only site (worker answers + exact path, docs/271 §1). Only with `[Experimental] HouseholdToo=1`, never default; unknown/selected count as household | the cut (0.7-1.1 ms) is 6-9% of a 12-13 ms household local search, under the G-C 25% bar. Upper bound SV ~30-50, BP ~4-5 ms/s for opt-in players [inferred: ready share 0.6-0.8 × (1 − band share)] | 11-18 h agent time (handoff §8 total) + 6-12 game jobs + 1 clean night | high: the tier-2 rows' Mode R is not green; the meta exact path loses in crowds (§4) |
| C. Side entries (`FindBestActionForCommodity*`, `AutoSolveCommodity`, socials) | 11.66 ms/s raw on BP, not split by class | unknown scope | unknown | class split first (~1 h counter add) | - |

- **Verdict:**
  - Drop the rows and the local driver as an *unseen* offload, because the measured class is empty.
  - Keep the rows' code and the L8 rerun only as the base for option B.
  - **Save via A first.** Build B only if A shows the SV cost is real play (not a loop) and Rodney wants an opt-in-only gain.
- **Stop rule for B:**
  - The L8 rerun must give `autoff_t2ic_eq` ≥ 99% and t2 A0 ≥ 50% of items, else park.
  - After L7, net < 3 ms/s on the SV-type save: park.

## 3. D10 IC pass-through (`docs/data/races-handoff.md` §6)

- **State** [verified]:
  - Design only, never built.
  - Its preconditions have not run: `races-r1.json` and `races-r2c.json` have no result file in `docs/data/results/`.
  - The two drift rows (`0x11EADA0`, `[0x11ECBC4]+0x1B4`) have no writer read.
- **Cause of the stall:** the payoff is too small.
  - Bound [measured inputs, inferred bound]: object bodies at the cut cost 47-58 µs a cut × ~10 cuts/s = **0.5-0.6 ms/s** (mono-cost17).
  - Under `Satb=2` the job overlaps the next tick. A pass-through body reads the live world, so only ICs whose inputs are constant after load stay exact. That is a subset of an already sub-ms bound.
  - Each call also pays the gate checks (R1 bits, R2c, epoch, `PassCheck`).
- **Redesign:** none worth building. If option 2.B goes ahead and the tier-2 rows make the cut more expensive (docs/219: 7.4% tier-2 share of cut rows), re-measure the per-IC body split first (a small counter add).
- **Verdict:** drop as a performance item. `races-r1` / `races-r2c` stay as a correctness proof gate for worker rows in general.
- **Reopen rule:** load-constant ICs ≥ 1 ms/s in a per-IC split.

## 4. Seen-crowd go-live (`autliveseen` = B11, docs/124 §17.10, §17.12)

### 4.1 What the 11-16 runs show

Source: `mono-autshadow-seenexact-1116-rerun1-1116-1116b`, master-1116b; jsonl rows in `probe-out/queue/` [measured]. Every job's timing is VOID on noise (12.5% other CPU, 47.9% other CPU, clock fell to 140%), so no ms/s comes from these runs.

| Job | Seen posts | Dry top-10 / top-1 / sim mis | Guard windows / skip windows | Eligible skipped | Ratio (per mille) |
|---|---|---|---|---|---|
| bp-seen-smoke (dry) | 346 | 0 / 0 / 0 | 0 / 0 | 0 | - |
| bp-seen-dry-crowd | 540 | 0 / 0 / 0 | 0 / 0 (dry skips the guard) | 0 | - |
| bp-seen-act-crowd | 214 | - | **8 / 8** | **1,128** | **3,438** |

- **Exactness holds** [measured]: 0 misses over 886 dry seen searches, and `autliveseen_hh_post` is 0.
- **The economics fail** [measured ratio, attribution inferred]:
  - Every closed guard window of the act crowd came out above the 1,000 per mille break-even. The last one cost 3.4x what it saved, so the guard held go-live off for most of the job (1,128 searches skipped, 214 posted).
  - The exact guard counts the search's consumer µs + cut words + GC lock-wait words since its post, against saved = vanilla B14 µs/pair × consumed pairs [verified `AutLive.cs:619-626`].
  - The cut and wait words are cumulative native words, so a seen search in flight also gets charged for cuts that concurrent unseen-direct requests took (6,129 unseen posts in the same load) [inferred over-attribution]. The ratio is therefore pessimistic.
  - The 10-30 G7 crowd still had cut 36% of the moved scoring against the G-C 25% bar [measured, docs/124 §17.10].
- **Cause:**
  - Fixed per-search cost: cut 0.7-1.1 ms, lock waits, and the band-1 / off-list re-score through C1, which the exact path must pay to stay exact.
  - The meta B14 scoring saved per seen search is small.
  - Seen NPC searches are rare even in the crowd (~540 per session, dry).
  - Positive net needs a near-free cut.

### 4.2 Redesign options

| Option | What | Household rule | Expected | Effort |
|---|---|---|---|---|
| **a. Cut-riding** | Post a seen-NPC search only in a frame where an unseen-direct request already takes a cut (shared cuts carry up to 4 requests, docs/124 §17.2 step 1). The marginal cost is then the request copy and the consumer, not a cut | unchanged: seen NPCs only, household/selected/unknown keep the game's search | +0.5-1.5 ms/s in crowds, ~0 in natural play [inferred]; also makes the guard's attribution honest (no own cut) | ~1-2 h build + dry and act crowd jobs (~25 min each) + one timing pair |
| b. HouseholdToo Y4 | Already built (docs/271 Y4): household actors on the exact path | realistic-only, opt-in | household searches (849 in the act crowd session) have the same cost structure, so also negative without (a) [inferred] | - |
| c. Smaller `TopN` / `Bands` | Not allowed: seen Sims must get the exact pick (go-live unseen-only rule) | - | - | - |

- **Verdict:** park `autliveseen`; it stays default off. Even if it were on, the guard switches it off where it loses.
- **Optional save:** option (a).
- **Stop rule for (a):** the exact guard ratio must stay < 1,000 per mille in ≥ 8 windows of the BP crowd and the timing pair must show ≥ 0, else drop B11 for good.

### 4.3 Option (a) built, smoked twice, dropped (2026-11-17)

Built on `feature/autliveseen-cutride-1116` (switch `autliveseenride`, default off; slots fixes-1085..1090; household, selected and unknown scope never ride, HouseholdToo-lifted household Sims never ride): a seen NPC posts only when `sPosted` (an earlier B13 this frame opened a batch whose cut has not run) and the guard charges a rider no cut words.

- **Smokes** [measured]: `bp-ride-smoke` failed twice on its own gate `autliveseenride_rode > 0 by 200 s` (chain-1116d; rerun1 with a mixed seen/unseen crowd, chain-1117c). Rerun1 at 170 s: rode 0, skipped 8, refused 0, `autunseen_posted` 263.
- **Why** [verified + measured]: a go-live batch is open only from its first request to the end-of-frame cut (`AutShadow.cs` TryRequest `join = sPosted`, AutFinish clears it); every posting search then waits in `Simulator.Sleep(0)` for the answer (`AutLive.cs` LiveMeta wait loop), so a rider must be a different Sim's B13 later in the same frame. That never happens in practice: the 11-16 BP act crowd (`bp-seen-act-crowd`, master-1116b) took **6,343 cuts for 6,343 requests, `batchMax` 1, `batchCuts` 0** (native.json `autshadow`). Not one cut in a full crowd job served two requests, so the shared cut that §4.2 (a) assumed does not occur.
- **Upper bound** [inferred, ledger §506]: even if every seen search rode, ~540 seen searches per 10 min x ~1 ms cut = ~0.9 ms/s; with the measured same-frame rate (0 of 6,343) the realistic gain is ~0, under the 0.5 ms/s stop bar. Forcing co-occurrence (the rerun2 `crowd_cancel` stress on `bugfix/ride-mixed-crowd-1117`) would only prove the mechanism under stress, not a gain in play.
- **Verdict: B11 dropped for good** (stop rule above). `feature/autliveseen-cutride-1116` and `bugfix/ride-mixed-crowd-1117` are not merged; their rerun2 queue is not run. `autliveseen` stays parked and default off. Salvage, if anyone reopens it: a seen search would have to *open* a batch that unseen requests join, which puts the cut on the seen search again (the §4.1 cost); no cheaper shape is known.

## 5. G3 whole-stack cost (docs/222 §11.5)

### 5.1 Why every timing was invalid

- **Noise** [measured]:
  - `golive-unseen-gate2` 11-07: 10 jobs, 7 "noise medium", 3 VOID ("profiler cut short").
  - The 11-16 seenexact rerun: other CPU 12.5% / 47.9%, clock drop.
  - `rewind-pilot-1116-1116b`: VOID, noisy machine.
- **Structure** [verified, docs/222 §11.5 fixed-work note]:
  - The threaded stage is ~590 ms/s in every run and the sim runs flat out, so a saving shows as more iterations, not fewer ms/s.
  - 5 ms/s ≈ 0.1 ms/iter, against a slice sd of ~3 ms/iter.
  - Separate loads also start from different Sim and lot states.
  - No stage A/B of practical length resolves a 3-5 ms/s bar.
- **The prepared queue cannot be fixed by rewind** [verified]:
  - `mono-measure-g3stack-1115.json` (prepared, never run) has off arm `ourMods: false`.
  - `queue-rewind` blocks vanilla arms (no ts3native, no `[Rewind] Park`; docs/270 §30.1).
  - Ini differences relaunch.
- **Rewind in queues has not yet reused a session in a profile job** [measured, 11-16 rows]. Every attempt was refused:
  - "thread … exited after the snapshot" ×2 (seenexact rerun1);
  - "bridge server refused op" ×2 (rewind-pilot-1116).

### 5.2 Design: split G3 into a runtime part and an installed part

**G3a, runtime part, same-start rewind segments** (switchable at runtime: posts, worker, consume, guard decisions, unseen direct):

- One rewind group per world: N consecutive jobs with the same start key (asi, ini, package, save). They differ only in `fixesOff` (arm A: `autshadow,autlive,autunseen` off at runtime = no requests; arm B: as shipped).
  - queue-rewind already groups exactly this (docs/270 §30.1: `fixesOn` / `fixesOff` may differ inside a group), so no new harness feature is needed.
- The snapshot comes after the first load and warm-up. Every job starts from the same memory image with seed 12345, so the work is identical until the arms diverge.
  - This makes each segment fixed work by construction, and X4's synthetic bench is not needed [inferred].
- Order ABBA ABBA… with 3-min profile segments. ≥ 6 pairs per world: 12 jobs × (3 min + ~5 s pause/rewind) ≈ 40 min per world.
- **Read:**
  - per-arm ms/s and it/s (docs/272 §4 lines, `arm-ms.ts`);
  - **paired** on−off difference per pair (same start), as mean ± sd over pairs;
  - iterations per wall second as the primary metric (flat-out sim), ms/s secondary.
- **Noise:** the machine check runs per segment. A noisy segment voids only its pair and adds one rerun pair from the snapshot (cheap). This replaces "void the night".

**G3b, installed-only part** (not switchable at runtime: the `kAutLive` B14 frame, guard thunks, hook prologues with the switches off):

- Priced as call count × unit cost.
- Call counts come from the counters the stack already keeps (B14 calls `autlive_b14_ads`, guard thunk calls).
- Unit cost comes from a monohost microbench of the same frame/thunk (docs/119 host). docs/222's summary already puts guard thunks at 1.5-2.1 ms/s [inferred there].
- One cross-check if wanted: a separate-load pair (`kAutLive` True vs False package, same ini) on a clean night, judged only by it/s.

**G3 = G3a + G3b.** The bar stays docs/222 §11.5: the stack beats release by ≥ 3 ms/s on both worlds, or G3 decides the stack's price for the release-number table.

- **Prerequisites:**
  - the two rewind refusals fixed (thread exits after the snapshot: the `--reap-workers` path of docs/270 §7, or let exited worker threads pass the check; the bridge's op refusal: read its `[Rewind]` log line);
  - the `rewind: auto` pilot ≥ 80% accepted rewinds on BP.
- **Effort:**
  - queue prep ~30 min;
  - pairing in `arm-ms.ts` (pair = consecutive A/B jobs of one rewind group) ~1 h;
  - G3b microbench ~1 h;
  - game: ~40 min per world on a quiet machine;
  - the rewind fixes are their own lane.
- **Stop rule:** if the pilot cannot reach 80% accepted rewinds, fall back to `mono-measure-g3stack-1115.json` (separate loads) pooled over ≥ 2 clean nights, judged by it/s.
- **Verdict:** save. This is a measurement, with no behaviour change and no household effect.

## 6. D7 K_LIFT (docs/524 §6)

Drop confirmed [verified, docs/524 §6]:

- 63 of the 132 lifted opcodes have no hand template.
- In the compile list they occur at 3 exit sites in 3 already-compiled methods, with 0 newly compilable methods, so at most **~0.004 ms/s**.
- Shipping it would mean a runtime relocator for EA handler bytes, with an edition check per handler, plus a lazy vt-cursor form.
- **Reopen rule:** a JIT game census shows one of the 63 opcodes as an exit in a method costing ≥ 0.5 ms/s. In that case write hand templates, not K_LIFT.

## 7. Ranked next builds and queues

| Rank | What | Kind | Expected | Effort (wall-clock) | Depends on |
|---|---|---|---|---|---|
| 1 | `kHoursMemo` exactness stamp (BachelorParty / venue generation) + repin `ts3fixes-hotspots.json` (or a hoursmemo-only queue): correctness (verify mismatches 0, counters > 0), then one bundle timing slot | build + queue | 3.9-4.8 ms/s camera share, up to 5-8 ms/s [inferred] | build ~1 h; queue ~20 min; 2 jobs ~30 min; 1 clean-night slot | none |
| 2 | Rewind refusal fixes (thread exit after snapshot, bridge op refusal), then the G3a rewind queue (12 jobs per world) + G3b microbench | harness + queue | prices the shipped stack; enables same-start A/B for every later Mono item | rewind fixes: own lane; G3 prep ~2.5 h; game ~40 min per world | rewind pilot |
| 3 | SV household local-search rate probe (§2.3 A) | small counter add + 1 SV job | lead to an EA retry loop or a real 70 ms/s household load | ~1 h + 12 min | none |
| 4 | ~~`autliveseen` cut-riding (§4.2 a)~~ dropped 2026-11-17 (§4.3) | - | ~0 (no shared cuts) | - | - |
| 5 | HouseholdToo local exact path (§2.3 B) | lane L2-L7 | opt-in only; SV-type saves up to ~30-50 ms/s [inferred, wide] | 11-18 h + 6-12 jobs + 1 night | rank 3 result, L8 rerun green, Rodney's go |

Dropped: the `autofflot` apply, D10, D7 (and docs/208 §9.3 zombies, as before). Rodney's decisions needed: rank 5 (opt-in-only gain worth 11-18 h?) B11 was tried and dropped (§4.3).

## 8. Household statement

Ranks 1 and 3 are built (§9); no other household path changed. Every redesign above keeps the household rule:

- Rank 1 makes the memo exact for every scope.
- Rank 2 is measurement only.
- Rank 3 is read-only.
- Rank 4 is seen NPCs only.
- Rank 5 is gated on `[Experimental] HouseholdToo=1` at docs/271 realistic-only sites (worker answers, exact path), never on teleports, skips or cuts.

## 9. Built 2026-11-16: ranks 1 and 3 (branch `feature/hoursmemo-svprobe-1116`, NOT RUN)

### 9.1 Rank 1: `kHoursMemo` exact

- **Inputs of `Lot.GetHoursOfOperation`** [verified, decompiled `Lot.GetHoursOfOperation`, `Bartending.TryGetHoursOfOperation(Extended)`, `TryGetBarData`, `MetaAutonomyTuning.TryGetHoursOfOperation`]:
  - the lot's `mLotType` and `mMetaAutonomyType` (venue type, bar data, tuning are looked up from them);
  - static tuning tables;
  - on a bar only: is a `BachelorParty` with `Lot == this` and `Started` in `Situation.sAllSituations`.
- **Why no generation hook** [verified + inferred]:
  - `Started` is the field `HostedSituation.IsStarted`, set only in `HouseParty.Start.Init` [verified: the only `IsStarted = true` in the HouseParty family].
  - A party leaves the list in the private `Situation.Dispose(bool)`, which the finalizer thread also calls [verified].
  - Hooking those means a field write site and a ~18-byte IL method that Mono may have inlined into `Dispose()` before our patch [inferred, ledger I-493-2].
  - The live check is cheaper to get right: it reads the same inputs the game reads, at the same time.
- **Design as built** (`ScriptHotSpots.cs` `HoursMemoHook`, `TickMemo.cs` `HoursMemo`):
  - each entry keeps the venue key `(LotType << 16) ^ MetaAutonomyType`; another key is a miss (`hoursmemo_keymiss`);
  - a bar entry (`Bartending.TryGetHoursOfOperation` true at the miss) keeps the bar's own hours; every bar hit runs the party check on the live list (index loop, no list copy) and puts in `Bartending.TimeOfPartyEnd` when it holds (`hoursmemo_barhits`);
  - an answer that is not the bar formula on a bar (another mod rewrote the method) is not kept (`hoursmemo_nostore`): always a miss, still exact.
- **Result:** the memo answers as the game for every caller and Sim, household included, so it needs no household exemption and no HouseholdToo.
- **Verify:** `hoursmemoverify` now compares **every** hit with the game's method (`hoursmemo_checked`), returns the game's answer, counts `hoursmemo_mismatches` and logs the first 8 in `ts3fixes.applied`.
- **Offline:** `tools/selftest/hotspots` models venue changes and parties that start or end mid-tick, checks every call against the model (50,000 steps, > 1,000 bar hits), and checks the foreign-answer refusal.
- **Counters:** fixes-1069..1072 (`hoursmemo_checked`, `_barhits`, `_keymiss`, `_nostore`).
- **Gap:** no agent action starts a bachelor party, so the party path is proven offline only; the game jobs show whether bar answers occur (`hoursmemo_barhits`).
- **Queues:**
  - correctness `tools/harness/queues/ts3fixes-hotspots.json` (promoted from the DRAFT): BP + IP, 10 min, repro `hours-memo`, verify on, Bin state apex. PASS = 0 mismatches, hits and checked >= 1000, 0 script errors.
  - timing `tools/harness/queues/hoursmemo-timing-1116.json`: smoke + in-load ABBA `--ab hoursmemo` (5-min slices, both orders, BP + IP, rewind groups), verify off, needs a quiet machine.

### 9.2 Rank 3: SV household search-rate probe (`hhsearchprobe`)

- **Where:** `AutPushHook` (`Autonomy.ConsiderPerformingAutonomousAction`, patched when `kAutLive` is on, the default). It reads the queue head before the push and notes the search after the game's push.
- **Scope:** household class as the L0 probe (`AutLocalProbeCore.Class`: household or unknown scope). Records are keyed by object id; no Sim reference is kept.
- **Read-only:** it changes no result for anyone.
- **Counters:** fixes-1073..1084:
  - `hhsearch_n`, `_local` (`IsRunningLocalAutonomy`);
  - outcome: `_push`, `_nopick`, `_busyuser` (picked, not pushed, user-directed head), `_busyauto` (autonomous head not beaten);
  - loop signs: `_samedef` (same pick as the Sim's previous pick), `_shortgap` (< 2 sim-minutes since the Sim's previous search), `_nopushrun` (no push after a no push);
  - `_sims_n`, `_maxpersim`, `_exc`.
- **Per-Sim lines:** `hhsearch_sims` (counts, searches per sim-hour, top pick), rebuilt every 64 household searches.
- **Switch:** `hhsearchprobe` is a test-only switch: `Fixes.ShortcutOn` returns false unless the harness turns it on (default off, as `autt2l0`).
- **Offline:** `tools/selftest/autlocalprobe` (`HhSearchRec`).
- **Queue:** `tools/harness/queues/hhsearch-sv-1116.json` (SV + BP) and a copy for Dalexlol's save. Extended the same day to the docs/277 §5 item list (docs/277 §7.2: scope, why enqueued, gap bins, lowest motive, lot, pairs). The decision rule is in the queue note.
- **Existing fixes checked:** no NRaas mod memoises lot hours or limits household search repeats (grep of the NRaas source: only `Bartending.TryGetHoursOfOperation` callers in Career/Traffic); a web search found no Sims 3 fix at this level.

## 10. Rank 5 started 2026-11-17: piece 1 (branch `feature/hhtoo-local-1117`, NOT RUN)

Rodney's go: "HouseholdToo build it". Gate state [measured]: rank 3 (`hhsearch-sv`) voided twice on the DoorMemoX self-check, no values; the L8 rerun passed but its tier-2 A0 share is 5-13% of items (BP 1,362 / 10,642 and 1,647 / 15,520, IP 703 / 14,122), under the §2.3 stop rule's 50%. Building anyway on Rodney's go; the stop rule stays the main risk (decision for Rodney after the coverage run).

- **Piece 1 (built):** local allow-list L3 (`tools/autonomy-allow-local.ts` writes `mods/ts3fixes/src/AutShadowLocalList.cs` from `docs/data/autonomy-tier2.csv`: 238 ready local definitions) and the coverage probe `autt2lcov` (default off, needs `autt2l0`, read-only for every Sim): each counted local search's C2 calls timed and filed on/off that list per actor class, counters fixes-1145..1157 (agent `aut_lcov_*`). Name-only match, so the on-list share is an upper bound [inferred].
- **Queue (RUN FIRST, not run):** `tools/harness/queues/hhtoo-local-cov-1117.json`, SV + BP 12 min with `hhsearchprobe` + `autt2l0` + `autt2lcov`, `[RouteSnap] DoorMemoX=0` (the rank 3 rerun as well). Gate for piece 3 on: household coverage x household local ms/s >= 3 ms/s on SV.
- **Piece 2 (built 2026-11-17, branch `feature/hhtoo-local2-1117`, no game behaviour change):** `mods/ts3fixes/src/AutShadowLocalCore.cs`, pure rules only, nothing calls them yet. Post gate `Post`: switch `autt2local` (new, default off, `Fixes.ShortcutOn`) **and** `[Experimental] HouseholdToo=1`, else `kOff` for every actor; manager search only; `mForceRunLocalAutonomy`, an overridden lot or `mForceScoreSoloInteractions` = game; household scope (`UfWhy` kHousehold) only with `HouseholdLift`; not live, gone and unknown scope (-1) never; unseen and seen NPCs post (the exact path). B9 source order `Sources` and B11 commodity order `Commodities` as read in `Autonomy.ScoreInteractionsForLocalAutonomy` / `ScoreInteractions` (Steam 1.67) [verified, read]: the inventory test reads the actor's own lot (`IsNPC && LotCurrent.IsWorldLot`), not B9's picked lot; solo pairs run on `mForceScoreSoloInteractions`, not on the force-local flag. H8 local rule `LocalCacheNoop` (no-op only for the request's own Autonomy, cache on at the request, C0 unchanged; else abort `cache`) with `LocalC0`: C0 is **9** fields, not 10 (`CacheValuesToOptimizeTestFunctions(mActor)` at B7) [verified, read]. Dedupe `LocalPairSet`: the game's key is value equality (`InteractionObjectPair.Equals` = definition Equals + same Target; `GetHashCode` likewise) [verified, read], not the pair reference the handoff design said; cap `kMaxLocalPairs` 8192 -> `kCap` (`lpcap`). Offline: `tools/selftest/autshadowlocal`. No counters yet (piece 3 reserves them). Build `ts3fixes.package.hhtoo-local2-1117` (not run; needs no run).
- **Piece plan, household rule, next steps:** `probe-out/householdtoo-handoff-1117.md` (local handoff) mirrors §2.3 B: L1+L2 core, L2 driver, L4 native rows, L5/L6 Mode R, L7 exact consumer, gates. Every later piece posts household / selected / unknown actors only through `HouseholdLift` (HouseholdToo=1); with 0 they never post.
