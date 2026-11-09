# 222. Go-live for unseen Sims: worker ranking used directly

Design 2026-10-31, built the same day (ts3fixes only, switch `autunseen`, off; not run). Decision by Rodney (2026-10-31): redesign `[AutShadow]` go-live (docs/124, Mode 3) as a **non-sharing path only for searches whose actor is unseen**. The worker already gives exact answers (golive-final G7: top-1 equal 100% over ~38k Dry searches), so for an unseen actor the sim uses the worker's ranking directly: no band-1 C1 re-run, no late fallback, one live `Test` on the pick. The only relaxation is staleness between the cut and the push. Visible actors keep today's exact path (Mode 3 or vanilla).

Inputs: `node tools/harness/mono-cost-report.ts --tag golive-final` (`docs/data/results/2026-10-31-queue-golive-final.md`), docs/59, docs/124, docs/125, docs/143, docs/144, docs/202, docs/211, docs/217, the decompiled `Autonomy` / `BestScores` (`game-copy/decompiled`, read only, not quoted), `mods/ts3fixes/src/AutLive.cs` (line numbers below are master `7395b531`, before this change). Inference ids I-222-n are local to this doc (no docs/51 § yet).

## Summary

- **Why [measured, golive-final]:** acting Mode 3 nets ~0 ms/s (BP -0.3, IP +0.6, crowd -1.8 in `bp-gf-act`, `ip-gf-act`, `bp-gf-act-crowd`): the consume side re-runs band-1 C1 (~1.15 ms a search) against ~0.7 ms saved; 60% of B14 calls in used searches are "late" (`AutLive.cs:522` `late = L.Late || f != L.ResumeFrame`, docs/217); cut 0.35-0.7 ms a request; guard thunks 1.5-2.1 ms/s always on; the cost guard skipped 86-96% of eligible searches.
- **Direct path (built):** the game's B13 still runs (its own side effects stay: `CalculateNumSimsAtHomeOrGoingHome`, bring toddlers/pets inside, the GoHome / StayHome / GoOutToYard pairs [verified, decompiled B13]), but the B14 hook consumes **every** valid object of every B13 slice: the worker's passing allow-listed ads go into `BestScores` with the worker's score (D1 `Add`, no C1/C2), off-list ads through the game's C1. After B13 the game's D2 draws; a hook on D2 runs **one live `Test`** on a pick from an inserted ad; a fail draws the next pick without it; a second fail scores the remaining inserted ads live (C2) and lets the game draw (vanilla).
- **Scope = actor only:** `UfWhy(actor) == 0` (`UnseenFallback.cs:54`, docs/202 §2) at the post and again at the resume. Active household and selectable Sims are always exact. A visible Sim's search is exact even for unseen objects.
- **Snap-back:** the first seen answer sends the search (at the resume) or the rest of it (at a later B13 slice) to the exact path; a queued direct pick of a Sim that turns seen keeps only if a live `Test` passes.
- **Gain [inferred]:** G(u) = u x S x d - guard; d = 0.85-1.8 ms a search. At u = 0.8: BP ~5.9, IP ~12.1, SV ~10.4, BP crowd ~6.3 ms/s (§6). u for searches is not measured; docs/202's unseen shares (route plans >= 300 ms 70-96%, locomotion 88-99%) pass the >= 50% go rule as a proxy, so the build went ahead and the search-share probe runs inside the first queue (Dry jobs count searches by actor class).
- **Built:** ts3fixes `AutLive.cs` / `AutLiveCore.cs` / `AutShadow.cs` (D2 hook), counters 611-640, per-path own cost guard, `mono-cost-report.ts` lines, ts3agent `camera_to_tagged` + counter names, repro `aut-unseen-snapback`, queue `tools/harness/queues/golive-unseen.json` (not run).

## 1. Scope rule

| Check (reused, no new classifier) | Source | Result |
|---|---|---|
| live mode, not travelling or shutting down | `UfWhy`, `UnseenFallback.cs:56` | exact |
| selectable or active household | `UfWhy` `kHousehold`, `UnseenFallback.cs:58` | always exact (also off screen) |
| actor on a seen lot (camera lot, active household's lot, lot reaching into the view) | `UfLotSeen`, docs/202 §2.1 | exact |
| EA on-screen test | `World.IsObjectOnScreen` (native equivalent: agent byte `+0xC8`, docs/143 §1) | exact |
| camera frustum / reach `max(80 m, 2 |cam y - y|)`, 80 m from household Sims | `UfPosUnseen` -> ts3native `UfCamMsg` (GetOption2 `0x83`); household positions over GetOption2 `0x82` (`UnseenViewers.cs:17`) | exact |
| held by UnseenFallback, or social target seen | `UfHeld`, `kSocialSeen` | exact |

`UfWhy` returns 0 only when every test says unseen [verified]. Without the ts3native camera query it answers seen, so the direct path never runs [verified, `UnseenFallback.cs:69-71`]; the smoke job fails then (`autunseen_posted` gate). Errors in the classifier count as seen (`SafeUfWhy` = -1, counter 638). **Hysteresis:** the actor must be unseen at the post and at the resume (>= 2 ticks apart, the worker wait); any seen answer leaves at once. **Mixed case decided:** objects never decide; a visible actor's search is exact.

## 2. Invariants on the direct path

1. **Only valid interactions are pushed.** A pick from an inserted ad gets the game's own `InteractionInstance.Test()` (live state, after `mUseCachedValues` is off) before D4/D5. Off-list ads, game-scored objects and vanilla-rescored ads are already live. D3-D5 run unchanged (D4's interrupt rule and draw included) [verified, decompiled D2/D4].
2. **No stale object refs.** The worker runs in-process on the same heap (docs/206 2b): pairs are real managed references. Result level: `LiveCheck` (docs/124 §3.1) at the resume; object level: `ObjStale` (object, then lot; docs/76/79 events) at the resume and again for every object of a later slice, `HasBeenDestroyed`, `SameAds`. Any reason = the game's B14 for that object.
3. **No crash paths.** `BestScores` changes only through the game's `Add` / `Clear` (no `Remove` exists; a failed pick is dropped by re-adding the other entries); dedupe of inserted ads by our own per-search set (the game's `InteractionCheckTable` is a private class; meta ads are distinct pair objects from the local pass [inferred, I-222-1]). D2 hook exceptions fall back to the game's D2; counters 638.
4. **RNG [relaxed, documented].** D2/D4 draws stay on the sim stream; a redraw adds one draw. C2 draws of inserted ads (pet branch, `FillInTokens`, docs/124 §5.1) are skipped; objects the worker aborted on `rng` run the game's B14 (`ObjWhy` invalid). Worker streams keep docs/125 contract 3. Seeded A/Bs compare distributions, not paths.
5. **Mod guard unchanged.** G-M1..G-M6 (docs/128) and the consumer-path L3 check (`LiveGuard`) decide as today; the D2 swap is ours (`OwnSwap`). Off-list ads stay live C1.
6. **Visible searches never use relaxed data.** Inserted ads live only between one B13 and the next D2 of the same autonomy (`sDirectAut`, cleared at every direct post); the exact path never reads them.

## 3. Snap-back

- **Search level:** seen at the resume = `L.Direct` off, the search runs today's exact Mode 3 consume (counter 613). Seen at a later B13 slice = the rest of its objects run the game's B14 (614); ads already inserted stay and D2 still tests a pick from them.
- **Queued pick:** the push hook tags a direct pick that was pushed (instance -> Sim, max 64). Once per frame at a meta search (`FlipCheck`, ~every 0.1 s [inferred]) each tag is checked: Sim gone or pick no longer queued = drop; pick is the running head = keep (vanilla could have started it); still unseen = wait; seen = live `Test()`: pass keeps (625), fail cancels through `InteractionQueue.CancelInteraction` (626).
- **Outcomes do not snap back:** a pushed pick passed a live `Test`; the gates judge behaviour over the run (§5).

## 4. What was built (ts3fixes only; worker and ts3native unchanged)

| Part | Where |
|---|---|
| Switch `autunseen` (default false, `Fixes.ShortcutOn`), acts only with `autlive` acting and the D2 hook patched | `Fixes.cs`, `AutLive.cs` `LiveMeta` |
| Direct consume per object | `AutLive.cs` `DirectB14` |
| D2 hook (`Autonomy.AssignProbabilitiesAndChooseInteraction`, kAutLive packages) + redraw / vanilla rule | `AutChooseHook`, `DirectChoose`, `Rebuild`; patch in `AutShadow.ApplyAutLiveB14` |
| Snap-back of queued picks | `FlipCheck`, `DirectTag` (push hook) |
| Pure rules + selftest | `AutLiveCore.Unseen` (`AtPost`, `Stay`, `Flip`, `AfterFail`), `AutLiveCore.B14Rate`; `tools/selftest/autlive` |
| Per-path own cost guard (§6.3) | `GuardAllow(direct)`, `GuardAdd`, `CostGuard.Own` |
| Counters 611-640 (`fixes-611`, `int[641]`) | posted / seen at post / snap resume / snap slice / dropped / direct / ads inserted / off-list C1 / objects to game B14 / Test pass / fail / vanilla / no pick / tagged / flip kept / cancelled / guard windows, skip windows, skipped, pm / own cost us / saved us / B14 ads / B14 us / Dry unseen runs, top-10, top-1 / exceptions / Dry seen runs / Test us |
| Report | `mono-cost-report.ts` `unseenLines` ("unseen-direct", "unseen-direct cost", "unseen-direct Dry by actor class"); estimate `moved` = vanilla allow-listed B14 x consumed share |
| Repro + agent | `aut-unseen-snapback` (camera far -> `camera_to_tagged` x 8); ts3agent `camera_to_tagged` reads AppDomain `ts3fixes.autunseen.tagged`; counter names `autunseen_*`, `autlive_b14_*` |

## 5. Gates (queue `golive-unseen.json`, `stopQueueOnFail`)

| Gate | Job(s) | Pass when |
|---|---|---|
| smoke | `bp-gu-smoke` (4 min acting) | direct searches posted (early gate 200 s), exceptions 0, applied log "unseen-direct D2 hook" |
| Dry by class (replaces G7 for this path; also the u probe) | `bp/ip/sv-gu-dry` (Dry=1 + `autunseen`) | unseen-actor searches: top-10 equal >= 99%, top 1 >= 99.5%; unseen share of compared searches reported (go rule >= 50%) |
| snap-back | `bp-gu-snapback` (repro) | flip kept + cancelled > 0, a snap-back counted, exceptions 0, no script error |
| acting | `bp/ip-gu-act` | live Test fail share < 40%, vanilla fallbacks < 10% of direct, own net > 0 ms/s |
| behaviour (G8u) | `bp/ip-gu-g8-off` vs `-on`, same seed, 30 min | pushes per Sim-hour +-5%, no-pick share +-5 points, MakeSimGoHome +-10%, stuck Sims not up, per-definition pick shares chi-square p >= 0.001 |
| cost (G9u) | later: clean night ABBA `--ab autunseen` BP/IP | sim it/s not down > 2%; 0 crashes |

Per-Sim drift over hours (motives, relationships, skills, funds, location) is not logged yet: G8u judges pushes / no-picks / GoHome / picks; a drift logger is the next step if G8u passes (open).

## 6. Gain and cost guard

### 6.1 Estimate [inferred from golive-final Dry G7 rows]

G(u) = u x S x d - guard; d = m x a - c_cut - c_d (m = worker-scored meta time a request, a = answered share, c_cut = cut a request, c_d = one live Test + inserts ~0.3 ms [I-222-2]); guard = thunks, fixed while Mode 3 is on. Off-list C1 is work vanilla does too, so it is neither cost nor saving.

| World (job) | S /s | m ms | a | c_cut ms | d ms | guard ms/s | G at u = 0.4 / 0.6 / 0.8 / 1.0 |
|---|---|---|---|---|---|---|---|
| BP (`bp-gf-g7`) | 9.0 | 2.1 | 0.92 | 0.55 | 1.08 | 1.9 | 2.0 / 4.0 / 5.9 / 7.8 |
| IP (`ip-gf-g7`) | 9.6 | 2.6 | 0.95 | 0.35 | 1.81 | 1.8 | 5.2 / 8.6 / 12.1 / 15.6 |
| SV (`sv-gf-g7`) | 14.1 | 1.9 | 0.94 | 0.40 | 1.09 | 1.9 | 4.3 / 7.3 / 10.4 / 13.5 |
| BP crowd (`bp-gf-g7-crowd`) | 15.0 | 2.0 | 0.93 | 0.70 | 0.85 | 3.9 | 1.2 / 3.8 / 6.3 / 8.9 |

Rebound: the manager runs one search per iteration (docs/59 §1); cheaper searches can mean more searches per second when a backlog exists [I-222-3].

### 6.2 Cheaper alternative: throttle unseen repeat searches (no worker)

73% of BP eligible searches end with no pick (3970 of 5424, `bp-gf-g7`), many of them NPC re-searches after a failed autonomous action (`CanRunAutonomyImmediately`, docs/59 A2). A sim-thread throttle of unseen Sims' repeat searches (docs/144 `unseenmotives` / docs/211 `wakepark` family) saves the whole search (~2.4-3.3 ms) with no cut and no guard: T = u_r x S x f x c_s, e.g. BP u_r 0.4, f 0.75 -> ~6.5-8 ms/s [inferred, u_r unmeasured]. **The worker path beats it only when** the throttle fails G8u (it changes cadence) at the same ms/s, or the searches left after it still give G >= 3 ms/s on BP and IP. They compose: throttle repeats first, direct path on the rest. Measuring u_r needs a per-Sim "last search had no pick" count (not built).

### 6.3 Cost guard v2 (built): count only real sim cost, one guard per path

- **Cost** per search = consumer us (check, select, band-1 C1 on the exact path; off-list C1 no longer counted, was `AutLive.cs:546`) + its own cut + the sim's GC lock waits while it was in flight (native words since the post; one request in flight). The inferred lock-entry ns and guard thunks are left out.
- **Saved** = vanilla B14 us per meta ad (game B14 timed: 1 in 8 pass-through calls while Mode 3 is on, plus every object a go-live search hands to the game) x the consumed allow-listed ads. No rate yet (< 200 ads timed) = no decision.
- Exact path keeps counters 538-541, the direct path 627-630; same OffPm / OnPm / window / hold settings.

## 7. Order, hours, open

Build ~3.5 h wall-clock (done). Queue: 11 jobs, ~3.5 h game time (smoke 4 min, 3 Dry x 10, snap-back 12, 2 acting x 10, 4 behaviour x 30), then one clean night (G9u). Before the run: `node tools/harness/build-agent.ts` in the main checkout (counter names, `camera_to_tagged`).

Open questions for Rodney:
1. Dry jobs answer u (unseen share of searches). If u < ~0.35 on every world, stop and build the repeat-search throttle (§6.2) instead?
2. Plausibility bar: G8u per-definition chi-square at p >= 0.001 (looser than docs/124 G8's 0.01) acceptable for the relaxed path?
3. Snap-back keeps a queued pick that passes its live `Test`; cancel all queued direct picks instead (stricter, more visible re-searches)?
4. Per-Sim drift logger (motives, relationships, skills, funds, location over hours) before shipping, or only if G8u is borderline?

## 8. Behaviour drift = cadence: pacing and G8 by actor class (build unseen2)

**Triage of the G8u pairs (2026-11-01):** the drift is a cadence change, not a per-search one. The direct path skips the allow-listed ads' C1/C2 (`AutLive.cs` `DirectB14`), while the game's B13 yields only when its 1 ms `Autonomy.sUpdateTimer` runs out (docs/59 Y2, docs/217 §1.1). Direct searches therefore finish in fewer ticks and unseen Sims search +19% (BP) / +10% (IP) more often per Sim-hour; per-search outcomes are about flat on BP; IP showed +3.3 points no-pick share that the old counters could not split by actor class [measured, triage of the G8u pair runs].

**Fix 1, pacing (switch `autunseenpace`, tuning `kAutUnseenPace` = on; acts only with `autunseen`):** after each consumed object of a direct search, its skipped allow-listed ads (the same count the guard's saved side uses, `SavedPairs`) x the vanilla B14 us per ad (`B14Rate`, the guard's rate; 0 = no rate yet = no charge) are added to `sUpdateTimer`, so B13 yields on vanilla's schedule. The native set writes only the stored part of the watch and keeps the start tick, so the hook stops the running watch (stored += running), sets stored + charge and starts it again [verified: disassembly of the set `0x4F3460`, stop `0x4F3220`, start `0x408700`]. Snapped searches and game-scored objects charge nothing (their time is real). Counters 659 (us charged) and 660 (yields brought forward: at a slice's last charge the real time was still within 1 ms and the charge took the timer over; approximate, game-scored work after that charge is not seen). Logic in `AutLiveCore.Pace` (selftest `tools/selftest/autlive`).

**Fix 2, measurement:** G8 split by actor class at the push / no-pick (`UfWhy` 0 = unseen, else seen; snap-backs make this the class at the end of the search): searches (every `ConsiderPerformingAutonomousAction`), pushes, no-pick, GoHome for unseen (661-664) and seen (665-668), and unseen Sim-hours x100 (669, from the hourly Sim sample). `{"t":"picks"}` keys are now `DeclaringType.Name + "." + Name` (most definitions are a nested `Definition`). `mono-cost-report.ts` prints per-search rates per class (no-pick share, GoHome per no-pick, pushes per search) and a separate cadence line (searches per Sim-hour, all / unseen / seen).

Build `game-copy/ts3fixes.package.unseen2` (`--on kAutLive`); queue `golive-unseen.json` g8 jobs (BP/IP off + on) point at it. PASS for the pairs: per-search rates per class within the §5 G8u bars, and the unseen cadence back within +-5% of the off arm.
## 9. Per-definition Dry compare (build unseen3, measurement only)

**Why:** triage of the IP G8 behaviour pair (golive-unseen): unseen actors push 13.5% fewer interactions with the direct path, and `AutonomouslyShopInRabbitHole.Definition` holds ~2/3 of the drop in the picks lines [measured]. Two suspects [inferred]: (1) `DirectB14` drops every pair the worker marked `kTestFailed` with no live check, and ShopInRabbitHole's Test is time of day (shop open vs `SimClock`), so a verdict from the cut can be stale; (2) the G7 Dry compare never looked at late slices (72% of B14 calls in Dry went to the game's B14 uncompared), while the direct path consumes every slice from cut-time data; the score also reads live RabbitHole reference counts.

**Measurement (switch `autunseencmp`, tuning `kAutUnseenCmp` = off; only with `[AutShadow] Dry=1` + `autlive` + `autunseen`; the direct path is unchanged):** for a Dry search whose actor is unseen at the post and at the resume, on every B13 slice (late ones too), the game's own C2 score of each request pair is compared with the worker's verdict for the same pair. Scope mirrors the direct path: a slice where the actor is seen stops the compare (the direct path snaps back there); per object only one the direct path would consume (`ObjWhy` 0, `ObjStale` re-asked after the resume slice, not destroyed, same ads); per pair only allow-listed pairs with a worker verdict (pass or Test failed). Game pass = C2 score != 0 (C2 runs the Test and scores only on pass, docs/59 C2 [verified]). Counters 670-679: compared searches, pairs, worker-fail/game-pass, worker-pass/game-fail, both pass, both fail, late-slice pairs, late-slice mismatches, objects the direct path would hand to the game, slices with the actor seen. Per definition (`DeclaringType.Name + "." + Name`): `{"t":"defcmp"}` lines in autshadow.jsonl, cumulative snapshots of the top 15 by mismatches every 1000 compared pairs or 300 ticks: pairs, both mismatch directions, both pass / both fail, worker/game score ratio of both-pass pairs (median, p90; first 4096 ratios per definition), pair age in ticks since the cut (bins <= 1, 2, 3, 4-7, 8+) for all pairs and for mismatches. Logic in `AutLiveCore.DefCmp` (selftest `tools/selftest/autlive`); `mono-cost-report.ts` prints the totals line "unseen-direct per-pair compare" and the per-definition table from the newest snapshot.

Build `game-copy/ts3fixes.package.unseen3` (`--on kAutLive`, = unseen2 + this measurement); queue `golive-unseen.json` job `ip-gu-dry-defcmp` (IP 10 min, Dry). Needs the ts3agent with the `autunseencmp` switch and counter names (rebuilt from master by `cycle.ts` at the next game restart). Read: if ShopInRabbitHole's worker-fail/game-pass rises with age or on late slices, fix 1 = a live Test for Test-failed pairs of time-dependent definitions (or the whole definition to the game's C1); a both-pass ratio far from 1 = score drift, fix 2 = live score for that definition.

## 10. Rabbit-hole pairs to the game, pacing at 78% (build unseen4)

**Measurement (job `golive-unseen-2026-11-01-ip-gu-dry-defcmp`, unseen3, IP 10 min Dry):** 121,831 compared pairs of unseen actors; worker-fail/game-pass 0, worker-pass/game-fail 8,814 (7.2%), almost all on rabbit-hole meta definitions: `AutonomouslyGoForMealInRestaurant` 2692 of 4293, `AutonomouslyShopInRabbitHole` 1953 of 4287, `AutonomouslyGoToCityHall` 1939 of 2087 (0 both pass), `AutonomouslyVisitHospital` 1899 of 2094; `VisitLot` 247 of 103,821, the rest ~0. Mismatches sit at pair age <= 1 tick (2618 of 2692 restaurant, 1924 of 1953 shop), and both-pass pairs of these definitions have a worker/game score ratio of 1.00 (median and p90) [measured]. So not staleness and not score drift on passing pairs; suspect 1 of §9 (stale Test-failed verdicts) is refuted (worker-fail/game-pass 0).

**Fix (switch `autunseenrh`, tuning `kAutUnseenRabbitHoleGame` = on; acts only with `autunseen`):** `DirectB14` hands every allow-listed pair whose target is a `RabbitHole` (the implementor whose `HowMuchItWantsSimToCome` scores these definitions) to the game's own C1 (`CalculateScoreAndAddToCandidates`), as it does for off-list pairs. These pairs are real sim time: not in `SavedPairs`, so no pacing charge and no saved side in the guard. Counter 685 (`autunseen_rh_game`). The Dry compare (`autunseencmp`) mirrors it: rabbit-hole pairs are counted in 685, not compared. Counter 686 (`autunseen_cmp_wpgf_w0`) splits worker-pass/game-fail by the worker's score being 0.

**Pacing at 78% (tuning `kAutUnseenPacePct` = 78):** `PaceCharge` charges 78% of the measured vanilla B14 us per skipped ad instead of 100%; full charge overshot (BP unseen searches per Sim-hour 0.541 with vs 0.429 without, §8 pair) [measured]. 78 is the ratio of the two rates; [inferred] that the overshoot scales linearly with the charge.

**Root cause, design only.** Two readings; the data so far cannot separate them:

1. **Mostly a compare artefact [inferred, likely].** The compare's "worker pass" is the Test verdict: `AutShadow.ScorePair` returns `kPass` once `Test` passed, whatever `CalculateScore` returned (`mods/ts3fixes/src/AutShadow.cs` `ScorePair`). "Game pass" is C2 score != 0, and a passing pair can score 0 (already marked at `AutShadow.cs` `AutScoreHook`). Rabbit-hole meta scores are often exactly 0 by design: `MetaInteractionScoredByVenue.CalculateScore` returns `IMetaObject.HowMuchItWantsSimToCome`; `RabbitHole.HowMuchItWantsSimToCome` returns 0 when the actor is on the rabbit hole's lot, when the venue type has no `MetaAutonomyTuning` (base `GetMetaAutonomyVenueType` = None), and `Autonomy.ScoreMetaObject` returns 0 when `GetNumUniqueReferences() >= desired` or when desired - current < 1 and not above the once-a-day random [verified: decompiled `Sims3GameplaySystems` `RabbitHole.HowMuchItWantsSimToCome`, `Autonomy.ScoreMetaObject`, `Autonomy.GetDesiredNumSimsForMetaObject`, `MetaAutonomyTuning.DesiredNumSims`]. Desired = the venue curve by hot-spot status x hour of day, capped by `DeviceConfig` processor level, x `min(1, LotManager.Actors.Count / GetTotalSimsRequired(hours))`; outside the venue's hours it is 0. `GoToCityHall` with 0 both-pass pairs of 2087 fits "desired 0 all run". With the same inputs at age 0 and ratio 1.00 on passing pairs, the worker most likely scores these pairs 0 too; then `BestScores.Add` (score > 0 unless `mAllowZeroScore`) drops them in the direct path exactly as vanilla does [verified: decompiled `BestScores.Add`], and routing them to the game buys no behaviour, only C1 cost.
2. **Real input gap [inferred, less likely].** If the worker's score is nonzero, the inputs that can differ between the cut snapshot and the sim are the live reference list behind `GetNumUniqueReferences` (Sims routing to / entering the rabbit hole add references on the sim thread between cut and score), `LotManager.Actors.Count`, the hot-spot status and `SimClock.HoursPassedOfDay`; `mRandomChanceThatChangesOnceADay` and the tuning are per-day constants.

**How to settle:** counter 686 on a Dry compare with `autunseenrh` off (rabbit-hole pairs compared): 686 close to 673 = reading 1, then turn `kAutUnseenRabbitHoleGame` off again and look for the IP `ShopInRabbitHole` push drop elsewhere (cadence, §8; picks among the remaining candidates). 686 well below 673 = reading 2: keep the routing, or snapshot `GetNumUniqueReferences` per rabbit hole at the cut and re-read it live in `DirectB14` (fix design: one live call per rabbit-hole object per slice, cheaper than C1). `ip-gu-dry-defcmp-u4` (routing on) only confirms the consumed side is clean and gives 686 for `VisitLot`.

Build `game-copy/ts3fixes.package.unseen4` (`--on kAutLive`, = unseen3 + this section); queue `golive-unseen.json` jobs `ip-gu-dry-defcmp-u4` (Dry compare, routing on) and `ip-gu-g8-ab-u4` (in-load `--ab autunseen` behaviour, `autunseencmp` forced off). Needs the ts3agent with `autunseenrh` and the 685-686 names (rebuilt from master at the next game restart).

### 10.1 Results unseen4 (chain mono27, 2026-11-01, `golive-unseen-2026-11-01-*`, all PASS, timing invalid: noise medium 13.6-14.6%)

- **rh on/off: not settled.** `ip-gu-dry-defcmp-u4-rhoff` ran with routing still on: the job sets `fixesOff` at job level, but queue.ts reads only `run.profile.fixesOff`, so no `--fixes-off` was passed; `autunseen_rh_game` 15,984 (u4: 15,658) and the `kAutUnseenRabbitHoleGame` setting is on in its ts3native.log [measured]. Both runs: 673 = 686 (u4 141 = 141, rhoff 136 = 136), so every remaining non-rabbit-hole worker-pass/game-fail pair (VisitLot etc.) has worker score 0, which `BestScores` drops on both sides [measured]. Rabbit-hole pairs were never compared: reading 1 vs 2 is still open. Rerun with `"run":{"profile":{"fixesOff":"autunseenrh"}}`.
- **G8 in-load A/B (`ip-gu-g8-ab-u4`, IP 15 min, 3 off / 4 on slices, warm-up out) [measured]:** unseen pushes per Sim-hour 0.1424 off / 0.1397 on = **-1.9%** (unseen2: -13.5%), inside the ±5% bar. No-pick share 84.5% / 80.6%; pushes per search 0.155 / 0.194. Unseen cadence 0.921 / 0.722 searches per Sim-hour = **-21.6%** (bar ±5%: FAIL); GoHome per unseen Sim-hour -27.7% (bar ±10%: FAIL), while GoHome per no-pick is flat (0.439 / 0.424). Stuck Sims 0, `autunseen_exc` 0, 0 crashes.
- Cause split [inferred]: rh routing and pacing 78% changed together, so the push recovery cannot be pinned on either. The lower cadence fits more real C1 time per search (rabbit-hole pairs now pay C1, 11,296 in the on arm) and not a pacing undercharge.
- Mono gain on arm: saved 11.9 - cost 1.8 = **net 10.1 ms/s** (guard counters over 427 s on-arm time; the saved side uses the guard's vanilla-rate model) [inferred from measured counters]. Whole-load go-live estimate: net 13.4 ms/s, worker adds 3.2 ms/s to the sim.
- Hitches off / on (timing invalid, no verdict): >=300 ms 11.5 / 25.3 per 10 min, >=1000 ms 0 / 0, max 683 / 613 ms, p99 113 / 126 ms, 54.3 / 51.2 it/s. On-arm causes: locomotion, steering obstacle query, ScriptProxy WorldLot/Sim, sync Route_PlanFromPoint, gate 12. None is named as autonomy [measured].
- Monday: ship opt-in as planned with `autunseenrh` on (the tested state) [measured: push gap closed, 0 exc/crash]. The cadence and GoHome bars are open known issues for an opt-in feature, not a blocker [inferred]. Before the default-on decision: a corrected rhoff Dry run, a pace-100 + rh-on G8 pair, and a valid-noise timing pair.
- **rh Dry reading (2026-11-06, `golive-unseen-gate-2026-11-03-ip-gug-dry-rhoff`, IP 10 min, `autunseenrh` off verified in the job log, ts3native build c5a27832) [measured]:** 99,924 pairs compared; counter 686 (worker pass, game fail, worker score 0) = 7,175. The last per-definition `defcmp` rows (99,047 pairs) sum worker-pass/game-fail to 7,135, so counter 673 is about 7,2xx at the end (not logged as a counter row; rescaled, 7,198): 686 is within 1% of 673. By definition the rabbit-hole pairs are 6,926 of the 7,135 (restaurant 2,228, city hall 1,573, hospital 1,555, shop 1,565, show 5), `wfgp` 0 on every definition. **Reading 1 (compare artefact): the worker's score is 0 on the pairs the game fails, `BestScores` drops both.** Timing for the job is void (noise high, others 19%) but the compare does not depend on timing. Consequence per "How to settle": `autunseenrh` is not needed for correctness; the IP `ShopInRabbitHole` push drop sits elsewhere (cadence, §8). Rodney 2026-11-06: `kAutUnseenRabbitHoleGame` stays on unless off shows a real benefit or gain (rh on vs off cadence was -1.8% pooled in the gate, so keeping it costs nothing measurable).

## 11. Gate 2: default-ON gate by what a player can see (Rodney 2026-11-06, R1 + R3; queues built, not run)

**Decisions.** R1: behaviour bars for unseen Sims are relaxed: a change is fine if it does not show when the player focuses on that Sim (snap-back) or is not visible at all. The §5 bars (pushes and cadence ±5%, GoHome ±10%) no longer gate. R3: `autunseen` goes default ON if it shows better performance and this gate passes. Replaces the 11-03 verdict (`docs/data/results/2026-11-03-queue-golive-unseen-gate.md`: FAIL on those bars). The "docs/222 §11 (a)-(e)" notes in the old `golive-unseen-gate.json` (unmerged, `feature/golive-unseen-default-on`) named a section that was never written; this is not it.

**Rodney's answers 2026-11-06.** These override anything below that reads softer:

- **G1:** modelled gains count (the saved side of `autunseen`'s net may be the guard's model). G2 and G3 must still agree.
- **V6 is hard:** visible Sims' behaviour stays the same; only invisible Sims may change. ±10% is the measurement band, not permission to change what a visible Sim does.
- **V1:** the idle limit is fixed at 240 sim minutes.
- **`Audit=50` stays on for players,** but its cost must be measured and shown light. Not measured yet: the gate queues run it on in both arms, so they cannot show it (needs its own arm, a gate 2 addition).
- **`autunseenrh` stays on** unless off shows a real benefit or gain. The Dry reading in §10.1 shows no benefit, so it stays on.
- **`autunseen` goes default ON if gate 2 passes.**

Files: `tools/harness/queues/golive-unseen-gate2-pace.json` (only if V6 fails, §11.9), `tools/harness/queues/golive-unseen-gate2.json`, `tools/harness/gate2-report.ts` (bars from run data, `--self-check`), ts3agent `focus_watch` / `focus_result` / `focus_summary` (`FocusHandlers.cs`, rules in `FocusRules.cs`, selftest `tools/selftest/focusrules`), repros `aut-unseen-focus`, `aut-unseen-arrive`, `aut-unseen-player-speed`. All UNVERIFIED: compiled and self-tested, never run in game.

### 11.1 Stays hard (any miss = FAIL, no trade against gain)

| Check | How |
|---|---|
| 0 crash, 0 `autunseen_exc`, 0 script error from the go-live path, 0 new stuck Sim | every job; `earlyGates`, queue verdict, stuck log |
| Snap-back accuracy | §11.2 V2 + V3 thrown by `focus_result`; `autunseen_flip_*` and `autunseen_snap_*` move |
| Save correctness | G9b with `autunseen` acting, SV / BP / IP x 2 (`*-gu2-save-*`): save completes, reload reaches live mode, 0 crash in 6 runs. No mod data in saves (unchanged) |
| Safety guards | unchanged and on: one live `Test` on every direct pick (§2.1), `ObjStale` / `LiveCheck`, G-M1..G-M6 mod guards (`autshadow_gm6_faults`, `_mismatch` 0), L3 `LiveGuard`, cost guard, Mode R audit A1-A3 0, exe and hash gates |

### 11.2 What a player can see

| # | Player sees | Check | Throws when |
|---|---|---|---|
| V1 | Focused or followed Sim does something plausible | `aut-unseen-focus` BP + IP: camera away 60 s, `focus_watch` puts the camera on the Sim of the last direct pick and follows it for 30 s at normal speed (a player's map click and pan), 6 firings; state before / after in `-repro.jsonl`; `focus_summary` needs >= 4 Sims judged | no action, not routing, for > 240 sim minutes (fixed, Rodney 2026-11-06); needs: hunger or energy <= -95; routing or held without moving for 60 s |
| V2 | Snap-back in time | same | 3 s after first on screen: still in a relaxed hold, or a queued autonomous pick fails a live `Test` (FlipCheck must have cancelled it; it runs ~every 0.1 s, §3) |
| V3 | No teleport or pop | same, position every tick | one on-screen step > 12 m, not hidden and not parented (cars, rabbit holes, boats do not count) |
| V4 | Camera really got there | same | never on screen: nothing checked |
| V5 | Sims arriving on the active lot behave | `aut-unseen-arrive` BP + IP: camera on the home lot, 8 spawned Sims sent back onto the active lot x 3, every on-screen non-household Sim watched for 180 s (STRESS labelled, stands in for guests); `arrivalsMin` 3; needs are compared by `gu2-arrive-rerun` (autunseen on, off), no throw: crowd Sims stay on a foreign lot at high LOD with real decay | V1-V3 for each |
| V6 | Visible Sims unchanged (hard: only invisible Sims may change; ±10% is the measurement band, not permission) | `gate2-report.ts`: **pushes** per **seen** Sim-hour, on vs off, within ±10% pooled per world (Rodney 11-06: a push shows on screen, a no-pick retry does not; searches stay a log row, not a bar); `bp-gu2-crowd` (crowd in view, `offload-goal.md`) 0 crash / exception / stuck | **Fails on 11-03 data as a searches bar (§11.4); as a pushes bar it passes the static read (§11.8: BP -3.8%, IP +1.4% pooled)** |

`focus_watch` samples in a game task and only moves the game camera; the window is never touched. `Test` runs twice a second, so these jobs are correctness only, never timing.

### 11.3 Long-run outcomes: visible or not

`*-gu2-out-off` / `-on`: 30 min whole-arm run per world, same seed, ~14 sim days at ultra (10 min ran 6,701 sim minutes [measured]). `sims_activity` now also gives at-home, community-lot and lowest-need per Sim; `activity.jsonl` keeps them every 30 s.

| Outcome | Visible? | Decision |
|---|---|---|
| Sims stranded at places | **Yes**: the player walks onto the lot and sees them standing there | Bar: same foreign lot, no action, >= 8 samples (~45 sim hours at ultra): on <= off + 1 |
| Needs starving | **Yes** on focus (V1); a starved Sim can die, which the player reads as a mod bug | Bar: Sims ever at <= -95: on <= off |
| Sim never at home | Barely: only by visiting that home; a home empty for 14 days is odd | Bar: Sims never at home in the run: on <= off + 2 |
| Venues empty | **Yes**: the world feels dead. GoHome falling does not empty venues, fewer trips would | Bar: mean Sims on community lots: on >= 0.75 x off |
| At-home share, GoHome count, pushes, cadence | No, not by themselves | Logged only (R1) |
| Missing work | No for townies, and the household is always exact. Work starts on career alarms, not autonomy [inferred] | Logged only (`Work Until End of Shift` in activity) |
| Relationships | Unseen with unseen: no panel shows it. With the household: socials happen on screen, exact | Not measured |

The limits (+1, +2, 0.75) are my judgement, not measured: adjust after the first pair, before reading the verdict on later runs.

### 11.4 What the -39% GoHome is, and what it hid (11-03 `gug-t-ab`, both orders; `-pace*-ab` where named)

- **The counter is not trips home [verified].** `g8_*_gohome` (`AutLive.cs` `G8Note`) counts no-pick searches meeting the fallback's condition, whether or not the Sim is already home. So it follows cadence: fallback per no-pick stayed flat or rose (BP 0.56 -> 0.58, IP 0.45 -> 0.51) [measured].
- **Real GoHome runs fell far less** (`gohome_runs`, all Sims): BP 545 -> 446, -18%, and the orders disagree (-38%, +4%); IP 346 -> 339, -2% [measured]. BP pace78 18 min: -28% [measured]. So -38.5 / -14.7 overstates it.
- **Sims are not visibly stuck out, as far as the data goes.** Samples with "Go Home" running: BP 7.5 -> 5.9%, IP 4.4 -> 5.0%. Trips (Visit, Eat Out, Meet Up): BP 10.5 -> 8.8%, IP 10.2 -> 7.5%. No interaction: BP 47.6 -> 52.8%, IP 68.9 -> 68.5% [measured, 16 samples per arm per world]. Both directions fall together: on BP the world moves a bit less. Stuck Sims 0.
- **"Staying out" is not proven either way:** 11-03 recorded no location per Sim. The gap between an unseen Sim's searches grows from 1.35 to 2.09 sim hours on BP and 1.15 to 1.50 on IP, so a Sim done at a venue leaves ~0.4-0.7 sim hours later [inferred]. Unlikely to be noticed [inferred]; §11.3 measures it.
- **Finding: seen Sims lost cadence too [measured].** Searches per seen Sim-hour, on vs off: t-ab BP -10%, IP -18%; 18 min pairs BP -38% (pace 78), -33% (pace 100); IP -28%, -20%. Seen Sims stay on the exact path (on arm only: the off arm is mostly guard-skipped game searches, §11.8), so §1's scope does not explain it. Unseen cadence falls with pacing (BP -23 / -40%, IP -11 / -22%); seen cadence does not clearly. Cause not known. Suspects [inferred]: searches share one manager, one search per iteration (docs/59 §1), so paced or waiting direct searches hold visible Sims back; or the classifier (`UfWhy` at the end of the search). This is the one 11-03 result R1 does not cover: on-lot Sims may react later.
- **All behaviour numbers so far are at ultra** (~11 sim minutes per real second). If the manager is the limit, cadence per Sim-hour is an ultra artefact and players at speed 1-2 never see it [inferred]. The main queue no longer reads that (§11.9); `*-gu2p-speed1-ab` in the pace queue does, if V6 fails.

`golive-unseen-gate2-pace.json` (audit O1 rescoped) is conditional since 2026-11-06 (Rodney): run it only if gate 2's V6 fails at pace 78, as the static read says it should pass (§11.8). Pace 50 and 35, BP + IP, judged on seen pushes per Sim-hour (V6, 11-06), not unseen. The pace that brings seen within ±10% goes into `ts3fixes.package.gate2`. Neither does: triage the cause before the main queue [one round, `throttles-park-rule.md`].

### 11.5 Performance bar (`clear-gain-on-by-default.md`, `throttles-judged-by-ms.md`)

The measure stays ms/s per arm, not hitches or it/s; hitches are a no-regression guard.

| Bar | Pass when | 11-03 |
|---|---|---|
| G1 net | `autunseen` saved - cost >= 3 ms/s in **every** run, both orders, BP and IP (`*-gu2-t-ab`, `-onfirst`, 18 min) | BP 6.0 / 5.5, IP 10.3 / 10.2 [measured counters; saved side is the guard's model, which counts (Rodney 2026-11-06)] |
| G2 no contradiction | pooled Threaded ms/iter not up > 2% per world; with X3, TaskObjects and script read beside it | BP -7.0%, IP -5.5% |
| G3 whole stack | full stack beats the release state by >= 3 ms/s, both worlds (`*-gu2-x-*`, cross-run ABBA). Read by hand with X3 + X4 | Never measured |
| Hitches | not worse (`hitch-ab.ts`, AGENTS.md clear-regression rule) | IP pass, BP borderline |

**Why not the summed stage columns.** The +2.2 / +14.8 figures are `arm-ms.ts` sums. Per order they are BP +10.4 / -14.6 and IP -31.3 / +1.4 ms/s (on - off), so **IP disagrees between orders too**, not only BP as the verdict said. The swing is route and locomotion, which `autunseen` does not touch: workload mix [inferred]. TaskObjects, where searches run, went +5.4, +4.8, +10.4, -17.2 [measured].

**Fixed-work note (audit §1.5).** Threaded stage is ~590 ms/s in every run, the sim runs flat out, so a saving shows as more iterations, not fewer ms/s. 5 ms/s is ~0.1 ms/iter against a slice sd of ~3 ms/iter: no stage A/B of practical length resolves a 3-5 ms/s bar. So G1 uses the counters, G2 only checks they are not contradicted, and G3 needs X4 (fixed managed workload per arm) and X3 (TaskObjects, threaded columns). Without X4: G3 = G1's net minus measured stack cost.

**The off arm already carries the stack.** `--ab autunseen` compares against exact go-live, which holds the `kAutLive` hook, guard thunks (1.5-2.1 ms/s, §Summary) and the worker's 3.2-4.9 ms/s on the sim thread (§10.1, audit §3). Exact go-live nets ~0 against release. From G1's net: BP ~0-2, IP ~3-5 ms/s against release [inferred]. **BP may miss the 3 ms/s bar on G3**, IP likely passes. Part of any gain is also the on arm doing less; under R1 that is allowed.

Default ON needs every row of §11.1, V1-V6, the §11.3 bars, G1, G2, G3. Off-thread lanes normally take a stable move at <= ~2% loss (`offload-goal.md`), but R3 says "better performance", so this bar is a gain, not "no loss".

### 11.6 What must ship with it, and the release change list

`autunseen` does nothing alone: it acts only with `autlive` acting, `[AutShadow] Mode=3`, `[MonoThread]` worker, the `kAutLive` hooks, and `[RouteSnap]` (cut tracking). All of it is default OFF today [verified]. The gate ini (from the 11-03 smoke job) is the state to ship; **any value changed below must be retested**.

| Where | Today | Default-ON release |
|---|---|---|
| `tools/setup/build-fixes.ts` | `kAutLive` True (O6, merged 2026-11-06), `kAutShadow` False | True, True. `kAutLive` adds one interpreter frame under every meta B14 (docs/130 §11): counted by G3 |
| `tools/setup/build-fixes.ts:71` | `kAutUnseenPacePct` 78 | the pace chosen in §11.4 |
| `mods/ts3fixes/src/Fixes.cs` `ShortcutOn` | `autlive`, `autunseen` on only by `[AutShadow] Unseen=1` (O6) or a test run | read tunables; add `kAutUnseen` to `Instantiator.cs` and build-fixes; every new switch default stays off in source |
| `mods/ts3fixes/src/Instantiator.cs` | `kAutLive` comment "test packages only" | correct the comment |
| `native/ts3native/ts3native.ini` `[RouteSnap]` | Enabled=0 Count=0 AuditEvery=20 | Enabled=1 Count=1 AuditEvery=0 |
| `[MonoThread]` | Enabled=0 Mode=0 GcLock=0 FrameTls=0 | Enabled=1 Mode=1 GcLock=1 FrameTls=1 ArenaFix=1 N2mPrivate=1 LazyGate=1 LazyEarly=1 |
| `[AutShadow]` | Enabled=0 Mode=0 Sample=4 | Enabled=1 Mode=3 Sample=1 Satb=2 ColdIc=1 CutLean=2 TierMs=1500 RowHot=200 RunSkip=1 KeepPartial=1 TierReqLots=1 FreshEvery=0 PreLearn=0 WindowWaitUs=0, TopN=10 Bands=3 Margin=0 MaxWait=3 VerifyEvery=50 Dry=0 |
| `[AutShadow]` test values | gate runs Audit=50 DumpOnMismatch=1 | Rodney: Audit=50 stays on for players, its cost measured and shown light; DumpOnMismatch=0 for players (dump only, verify with a load) |
| `.github/workflows/build.yml:64` | refuses Enabled=1 outside its list | add `[RouteSnap]`, `[MonoThread]`, `[AutShadow]`, else the release build fails |
| `docs/PLAYERS.md:132, :153`, `CHANGELOG.md` | "stay off", Tiny UI line | how to turn it off, what it changes, known issues |
| EA 1.69 | Mono addresses are Steam 1.67 | exe gate must leave all of it off (`steam-only.md`); check once on the laptop |
| Gate ini only, do not ship | `HitchAttr.Enabled`, `RouteSnap.Count` audit runs | `HitchAttr` stays as release |

Guards ship on: `kAutLiveGuard`, `kAutUnseenRabbitHoleGame` (already True; stays on unless off shows a real benefit or gain), `kAutUnseenPace`, G-M1..G-M6 and exe gates (always on with the stack). `[UnseenRoute]` / `unseenroute` is not part of this gate.

Open risk for default ON, not covered here: mods with `[AutShadow]`, issue #155 (heavily modded old save), O10 Tiny UI, O12 big save (last by rule). Default ON reaches every player; Dalexlol's save (`big-saves-first.md`) should run the release candidate first.

### 11.7 Order and time

Before any run: pair built 2026-11-06 (§11.8), `gate2` holds pace 78 until the pace queue picks one; ts3agent from `feature/gate2-additions` merged; X3 + X4 for G3. ~1 agent build round, ~30 min.

Game (2026-11-06, §11.9): main queue smoke + 27 jobs ~8.0 h: focus + arrive 4 (~0.9 h), timing 4 (~1.3 h, quiet machine), cross-run 8 (~1.6 h, quiet), outcomes 4 (~2.1 h), crowd 1, save 6 (~1.6 h, audit O5: last, once). Pace queue 10 jobs ~3.8 h, only if V6 fails. `stopQueueOnFail`: the cheap hard checks run first, so a failure ends it early. Read: `node tools/harness/gate2-report.ts golive-unseen-gate2-<date>`.

### 11.8 V6 cause, queue counters, Audit cost, speed 1 (2026-11-06, static, no game run)

**Pooled 11-03 data, 8 `--ab autunseen` jobs, rh pairs out, per seen Sim-hour, off -> on [measured]:**

| World | Searches | Pushes | No-pick searches |
|---|---|---|---|
| BP | 0.671 -> 0.486 (-27.6%) | 0.310 -> 0.298 (-3.8%) | 0.359 -> 0.186 (-48.2%) |
| IP | 0.827 -> 0.640 (-22.6%) | 0.356 -> 0.361 (+1.4%) | 0.471 -> 0.279 (-40.8%) |

**So what fell is no-pick searches, not actions.** Seen Sims do the same number of things per hour; they re-ask less often after finding nothing. The old V6 bar counted searches, so it read a retry rate. **Decided by Rodney 2026-11-06:** V6 judges pushes per seen Sim-hour (±10% pooled per world); no-pick retries do not show on screen, a push does. `gate2-report.ts` prints searches and no-pick beside it as log rows, not bars; a build without the push counters gives "no data", not a pass. Per job the push change scatters (BP -12..+20%, IP -3..+12%), so pushes are flat pooled, not per run.

**Disproof attempts on "shared manager throughput":**

| Suspect | Result | Tag |
|---|---|---|
| Static `sUpdateTimer` couples searches | Refuted. `Restart()` at each meta phase start, one search in the meta phase at a time (one manager, docs/59 §1.1), so an unseen search's charge cannot reach the next search | verified, `Autonomy.cs` |
| Worker busy with direct searches, seen posts refused | Refuted. `autshadow_busy` 0 in all 8 jobs | measured |
| Classifier or denominator moved | Refuted as the main cause. Seen Sim-hours differ by -15..+17% per job with no sign; pushes per seen Sim-hour stay flat on the same denominator | measured |
| A seen search's own worker wait | Refuted as a direct cause. +2-3 ticks is ~0.06 s against a ~7 s gap between searches | inferred from measured rates |
| Manager throughput | **Survives, not proven.** Total searches per second fall in every job: BP 10.9 -> 6.6, 11.0 -> 8.1, 10.6 -> 7.4, 9.4 -> 6.4; IP 10.9 -> 8.5, 11.8 -> 10.3, 10.2 -> 8.3, 11.6 -> 8.3 [measured]. No-pick per Sim-hour falls about the same in both classes (BP pace 100: seen -55%, unseen -45%) [measured]. `AutonomyManager` is the only shared piece left [verified]. But ticks per search (3.3 -> 4.2-6.2) is just it/s over searches/s, so it cannot show the queue is full: there was no independent search length in the 11-03 data | inferred |
| **New: the arms differ for seen Sims** | The exact cost guard skips ~85-90% of searches in the off arm (BP pace 100: 4,517 skipped, 448 requests), so off-arm seen Sims mostly run the game's search. On arm: 515 of 539 seen searches post and wait. §11.4's "seen Sims stay on the exact path" holds only for the on arm. Seen no-pick share is 45-63% where the guard skips and 33-46% on the on arm; `gug-rh-ab`, both arms acting, 35-39%. Either retries thinning, or the exact path picks more often than the game | measured; which of the two is open |

**Why searches run longer [inferred from measured counters + code]:** a direct search pays the worker wait (`LiveMeta` loop, Satb=2, up to `MaxWait` 3 ticks) and is then paced to vanilla's schedule, so it costs vanilla's slices plus the wait. Pacing was tuned against that total by percent. Forced yields per direct search 3.6-3.8 at pace 100, 1.5-2.3 at pace 78 [measured].

**Added (ts3fixes counters 785-817, `AutonomyManager.Add` hook under `kAutLive`, all counters Mode 3 only):**

- Enqueue-to-start wait by class in ticks: count, sum, bins <= 1 / 2-9 / 10-49 / 50-199 / 200+. Start = B13 entry, or the push for a search with no meta phase.
- Search length by class, so `(lengths + searches) / iters` is the manager's busy share: the independent number the 11-03 data lacked. Queue depth (high + low) at each search end and the share with both queues empty.
- Worker wait by class, seen searches by path with their no-picks, household searches / pushes / Sim-hours.

`gate2-report.ts` prints all of it as `log` rows. Read: busy share near 100% and queues rarely empty on both arms = manager saturated, the hypothesis holds. Busy share well under 100% = refuted, look at the seen path.

**Candidate fixes, switches off, test runs only, no tunable:**

- `autseengame`: with `autunseen` on, a seen actor runs the game's own search: no post, no worker wait. Most literal reading of V6; exact go-live nets ~0 (§11.5), so it gives up little [inferred].
- `autunseenpacecredit`: ticks a direct search waited count as vanilla slices (1 ms each, >= 1 ms by Y2) and pay the pacing charge first, so waited + paced equals vanilla's schedule without a tuned percent. Built for pace 100 (`gate2-gu100`). Logic in `AutLiveCore.Pace` (selftest `tools/selftest/autlive`).

Pace queue additions, read each by naming it (`gate2-report.ts <tag>-bp-gu2p-seengame`): `*-gu2p-seengame-ab` (`--ab autseengame`, `autunseen` on in both arms: exact vs game for seen Sims, nothing else changes) and `*-gu2p-credit-ab` (`--ab autunseen` with both switches). Prediction if the hypothesis holds: total searches per second and seen no-pick come back in the credit pair; `seengame` alone moves little. The pace 50 / 35 jobs are also a test of it: seen cadence must follow unseen's.

**Speed 1:** repro `aut-unseen-normal-speed`, `*-gu2p-speed1-ab` (pace queue), 30 min, slice 3 min; the main queue's speed pairs were cut, §11.9. All earlier data is ultra. At normal speed the same Sim-hours take ~11x longer, so few seen searches per arm: pool per world, expect wide bands [inferred].

**`Audit=50` cost [measured, 11-03]:** no arm needed. ts3native already times the audit slice of the cut (`cutAuditUs`, sim thread): **0.04-0.07 ms/s** on the on arm over all 8 jobs (0.004-0.024 on the off arm, where cuts are rarer). Bounded by design at 1,024 rows every 50th cut. `gate2-report.ts` now has a bar, <= 0.5 ms/s (my judgement) per world. Not covered: cache effects of the refreshes, too small to resolve in any A/B [inferred]. `ListAudit` and `VerifyEvery` are separate knobs, not part of this number.

**Pair (`feature/gate2-additions` e87c9498, ts3native 0.36.169):** `ts3native.asi.gate2`; packages `gate2` (default tuning, pace 78 until the pace queue picks), `gate2-gu50`, `gate2-gu35`, `gate2-gu100`, `gate2-rel`; `game-copy/ts3agent.package.gate2`. `gate2-rel` is **`kAutLive=False`**: since O6 the default package carries the hooks, and the G3 jobs' note says release state = no hook, no worker. The next release built from master would carry them unless the default changes. Before any run: cycle.ts builds ts3agent and ts3fixes from the checkout it runs in, so merge this branch first: without it the agent has no `fixed_work` and no 785-817 names [inferred from the queue.ts header, cycle.ts not read].

### 11.9 Timing trim: what each job feeds (Rodney 2026-11-06)

`golive-unseen-gate2.json` 31 jobs ~9.8 h -> 27 jobs ~8.0 h. The ~6 h target is not reachable without weakening a bar; ~8.0 h is the floor. Times use 1.9 min per job over its run minutes, the overhead that gives the 9.7 h estimate.

| Jobs | Feeds | Decision |
|---|---|---|
| `*-gu2-focus` x2 | V1-V4 (BP + IP), §11.1 snap-back. V1 limit 240 sim minutes is in the repro | keep |
| `*-gu2-arrive` x2 | V5 (BP + IP) | keep |
| `*-gu2-t-ab`, `-onfirst` x4 | G1 (every run, both orders), G2, V6 (the only pool), R1-R6 (both orders), `Audit=50` bar (`cutAuditUs` per arm), hitches | keep, 18 min: V6 is ±10% pooled from jobs that scatter -12..+20% |
| `*-gu2-x-*` x8 | G3, cross-run ABBA, both worlds. `kAutLive` cost: `-rel` has no hook, so G3 carries it | keep; gain the X4 bench below |
| `*-gu2-out-off`, `-on` x4 | §11.3 outcome bars per world over ~14 sim days. docs/246's R1, R2, R4 whole-run rows are not read: `gate2-report.ts` reads routing from `--ab` runs only, so t-ab feeds them | keep, 30 min: a shorter run moves never-home and stranded in both arms |
| `bp-gu2-crowd` | V6 crowd in view: 0 crash, exception, stuck | keep |
| `*-gu2-save-*` x6 | §11.1 save correctness, "0 crash in 6 runs" | keep: fewer weakens the bar |
| `*-gu2-fastspeed-ab` x2, `*-gu2-speed1-ab` x2 | No bar. `gate2-report.ts` reads them only by naming them, never pools them, R1-R4 are log rows | **cut, ~1.8 h** |

What the cut loses: the routing log at player speed (docs/246 §6). If V6 fails the pace queue's `*-gu2p-speed1-ab` still reads speed 1; speed 2 has no job left.

**X4 added to the `-x-` jobs.** §11.5 says G3 needs X4, but they had no bench. They now run `--ab fwnull --fixedwork 8 --slice 2`, as `f1-callpath` did; `fwnull` flips nothing and `gate2-report.ts` skips those runs. No added minutes. Run `x4-smoke.json` first.

**`kAutLive` alone is not split out.** G3 counts the hook inside the stack, which is enough for this gate. A hook-only arm would add 4 jobs (~0.8 h) and could not resolve it: one frame under B14 is far below the ~3 ms/iter slice sd of §11.5 [inferred], and X4's loop does not go through B14. It matters only if gate 2 fails and master still ships `kAutLive` True.

**V6 is not an automatic stop.** A failed bar in `gate2-report.ts` does not fail a job. Read it after the 4 t-ab jobs and stop by hand: the later jobs would be at a pace about to change.

To reach ~6 h a bar has to give: saves x1 (-0.8 h, 0 crash in 3 runs), outcomes 20 min (-0.7 h, ~9 sim days), or one cross-run pair per world (-0.8 h, G3 loses order consistency). Not taken; Rodney's call.

**Also on 2026-11-06:** `final-bundle.json` replaces `rootmotion-retest`, `offload3-timing-ab` and `saved-stacks-abba` in the chain (all-on vs all-off cross-run ABBA, 12 jobs, ~2.5 h). Root motion and the offload3 apply share arms, under the offload worker's ini. **Saved stacks cannot:** the hook removal and ErrorTrap's task have no switch, and `savedstacks` is read at load, before the profile sets switches. A clean off arm needs a no-fix package from the same master; `swapfix/ts3fixes-before.package` predates the Mono merge. The fix is in both arms, so the bundle does not measure it.

### 11.10 V3 pop in `bp-gu2-arrive-off`: who moved Matthew Hamming (2026-11-08, static triage, no build, no game run)

**FAIL.** Queue `gu2-arrive-rerun`, job `bp-gu2-arrive-off`, pair g2pair1108 (ts3native 0.36.190), 00:36-00:46 UTC. Round 2 of `aut-unseen-arrive`: `focus_watch` at 00:43:36, then `focus_result` at 00:46:39 threw `focus: Matthew Hamming: pop: moved 15.2 m in one step on screen at 15.3 s`. Off arm: `fixes off: autunseencmp,autunseen (verified)`, `fixes on: autshadow,autlive,autunseenpace,autunseenrh (verified)`. Ledger: docs/51 §454.

**Verdict: not a household rule break. The mover is not identified, because the data that would name it was not written. Ours stays a suspect until a rerun clears it** (`crash-cause-before-fix.md`).

- **Scope [verified]:** Hamming is a townie of Cliff Cottage (`activity.jsonl`: `"Cliff Cottage - 1br, 2 ba",true` = at home). The active household is Angelista (Tiara, Lala, Roxy Dog), and `focus_watch` skips the active household (`FocusHandlers.cs:192`). He was neither the household nor selected, so this is no severity-1 break. He is also not a crowd Sim: he is not in `spawn_sims` or either `crowd_go` `sent` list.
- **What he did (`activity.jsonl`, every 30 s; seconds after the watch start, wall clock joined to the ts3native stamps through `frames.csv` `epochMs`/`qpcMs`, check: gohere cmd 6 starts 1.5 s after `crowd_go` round 2):** at -2 s `Go for Walk to...` at home; +28 s routing on the street; +58 s on the street, not routing; +64.1 s `ufplan: kind 0 266 ms why 0 marked Matthew Hamming own task` (our scope says unseen; marked, not cut); +89 s `Work Until End of Shift (7:00 PM)`, routing on the street; +106.4 s `ufplan: kind 0 344 ms why 4 Matthew Hamming own task` (why 4 = kActiveLot: he stands on a lot in view); +119 s on `Plumbob Pictures Backlot`, community lot, not routing, still at work. The 15.3 s counts from his first on-screen sample, not from the watch start. That sample was not saved, so the pop time is not known.
- **The step is a placement, not walking or a stall [verified]:** samples are taken every task tick (`Simulator.Sleep(0)`). 77 Sims judged in the earlier gate 2 and rerun jobs had a largest step of 1.02 m. The worst frame in watch 2 was 233 ms (`hitches.jsonl`, +117 s). The step is measured in world X/Z (`FocusRules.cs:60`), so the camera moving cannot make it. Both samples were on screen by `World.IsObjectOnScreen`, with no parent and no hidden flags.

| Candidate | Result | Basis |
|---|---|---|
| unseen-direct, pace, `autunseenrh` | out | `autunseen` is off; pace and rh act only with it (`Fixes.cs:396-397`) |
| `crowd_go`, `crowd_visit_lot` (713e44a8, 2767c50c) | out | he is in neither list. `crowd_visit_lot` returned `"queued":0` in both rounds, so the new bet did nothing in this run |
| unseenfb teleport (cut plan, `UfTeleport`) | out | no cut plan for him in watch 2. Cuts take >= 150 ms and are logged; the 120-a-minute `ufplan` cap filled at +41.9 s of a minute that ends at +42.1 s |
| D pre / arrive / leave (holds, then places) | out [inferred] | the hold runs inside the timed plan call. His +64 s plan took the marked path (D returned 0), and the +106 s plan (344 ms) is too short to contain a D-leave walk |
| trip shortcuts (rabbit hole, lot trips) | out | each needs a trip longer than 80 m (`Fixes.cs:926`, `:964`), and the move is 15.2 m |
| GoHome shortcut | out [inferred] | he did not go home in watch 2 (walk, then work) |
| unseen boat | out | `kUnseenBoat` is not in the `settings on:` line |
| frame stall, camera move, read across a reset | out | see above. A reset read at the origin would be about 1.7 km |
| **StuckWatchdog reset** | **open, ours** | `Act` (`RouteFixes.cs:323-337`) calls `SetObjectToReset` or `AttemptToPutInSafeLocation`. The route-plans-failing reset (`:247-252`) and the stationary-a-sim-day reset (`:259`) have no unseen check, so a seen NPC can be reset. Only the household gets a log line (`watchdog: household Sim ...`, twice for Roxy Dog). For a townie it writes only the in-memory `recent`/`moved` lists, and `watchdog_*` is not in the job's `fixesReport` |
| **`rabbitholedoor`** (on since v0.2.5) | **open, ours** | a door request that throws `SacsErrorException` is swallowed and the entry goes on (`RabbitHoleDoor.cs:23-40`). The count `rabbitholedoor_skipped` [2015] was not reported |
| **EA rabbit-hole entry or exit at Plumbob Pictures Backlot** | **leading hypothesis [inferred]** | he went to work there: on the lot by +106 s, inside by +119 s. A move of about 15 m from the door into the building, made before the hidden flags are set, fits. Not checked in the decompile |
| NRaas Overwatch or ErrorTrap reset (third-party) | open | no ScriptError or reset line in the job logs. Overwatch's own resets are not logged |

**Why the data is missing.** (1) `focus_result` throws only the reason (`FocusHandlers.cs:226`): no positions, interaction, posture or lot of the two samples. (2) `profile-world.ts:601` reads the `patches` `moved` map (every Sim a shortcut or the watchdog moved, with its spot and sim time) every 30 s and keeps it only for stuck reports. (3) The queue's `fixesReport` is `g8_,autunseen_,gohome_`, so no watchdog, unseenfb, rabbit hole or door counters were recorded.

**Repro defect, not the cause [verified log, mover unknown].** The camera is set once (`repros.json` `aut-unseen-arrive`, "stays there for all three rounds"). In round 2 `focus_watch` reported `camera_lot: null`, target `1240.8, 60.1, 1705.8`, about 34 m from round 1's `Air Spray - 2br, 2ba` target. No repro step moved it.

**Check fix (proposed, ts3agent and harness only, no game-code change):**

1. `FocusRules.cs:64`: the pop reason also gives both X/Z points and their seconds. `FocusRules.Sample` (`:19-24`) gains the interaction name and `SimInRabbitHolePosture`, filled at `FocusHandlers.cs:82-95`. `FocusHandlers.cs:226`: on a pop, append the Sim's `ts3fixes.moved` entry and the last 10 `ts3fixes.recent` lines. Add a selftest row in `tools/selftest/focusrules`.
2. `profile-world.ts:601`: append every `moved` entry that changed since the last check to `<tag>-moved.jsonl`.
3. `gu2-arrive-rerun.json` (every `fixesReport`, from line 98): add `watchdog_,unseenfb,rabbithole,unseenboat`.
4. `repros.json` `aut-unseen-arrive`: add `camera {lot: home}` before each round's `crowd_go`.

**Rerun (Rodney's OK first):** `bp-gu2-arrive-off` once (about 11 min) on the same pair with the agent and harness changes. It passes if there is no pop, or if the pop names its mover.

- **Ours (a `moved` entry `watchdog ...`):** gate both watchdog resets on `TownieUnseen(s, null)` at `RouteFixes.cs:251` and `:259`, and defer them while the Sim is seen. Proof: `aut-unseen-arrive` twice clean (2-clean-runs rule) plus the stuck-watchdog repro.
- **Ours (`rabbitholedoor_skipped` rises at that time):** study docs/505 before changing the switch.
- **EA (`in_rh` changes on the pop sample):** V3 counts the rabbit-hole posture as hidden, and that becomes the rule fix.

**Rerun verdict (2026-11-08, `bp-gu2-arrive-off` rerun with the new pop logging): EA rabbit-hole entry [verified by the in_rh flip in the logged sample].** `focus: Kai Leiko: pop: moved 15.2 m in one step on screen at 15.8 s (479.4,1232.1 @173.61 s -> 480.5,1217.0 @173.63 s; interaction Work Until End of Shift (7:00 PM) -> Work Until End of Shift (7:00 PM); in_rh False -> True)`. He entered his workplace rabbit hole in that step, and EA places the Sim at the rabbit hole's slot (a game teleport). The interaction did not change, and our `moved` entry for him (`home Still Water ... Mon. at 2:55 PM`) is a different move to his home lot, so it is not ours. Same 15.2 m as the first pop (Matthew Hamming, also going to work), which fits the same EA placement. **Fix:** `FocusRules.Judge` treats a step with `in_rh` True at the end sample (entry) or True at the start and False at the end (exit) as `rh_snap`: counted in `Stats.RhSnaps`, logged in the `focus_result` row (`rh_snap`, `rh_snap_note`), no failure. Every other pop rule stays; selftest rows: entry, exit, a normal 15 m step still pops. Branch `bugfix/focus-pop-rh`.

**Rerun 4 verdict (2026-11-08 02:11, `bp-gu2-arrive-off`, autunseen off): check false positive [verified].** `focus: Deidre Littler: not exact 3.0 s after focus: 1 queued pick(s) fail a live Test`. Our worker made no direct pick in this arm: `autunseen` was off (`fixes off: autunseencmp,autunseen (verified)`), so `LiveMeta` never takes the direct path (`AutLive.cs` `unseenOn = sChooseOk && ShortcutOn("autunseen")`), and the job's counters show `autunseen_tagged` 0 and `autunseen_posted` 0 (`gu2-arrive-rerun-2026-11-08.jsonl`). The failing pick was the game's own queued pick. A vanilla queue can hold autonomous picks that fail `Test` until they reach the head, where EA drops them. Deidre is not in the active household (Angelista), and `scope: screen` skips `Household.ActiveHousehold`. The check did not log which interaction it was. **Fix (ts3agent only):** `FocusRules.SnapQueue` counts failing queued picks only once counter 624 `autunseen_tagged` is above 0. The reason now names the failing interactions. A selftest row covers it. **Open [inferred]:** in the on arm, a game pick (not tagged) of a seen Sim can still trip the rule. To make it exact, ts3fixes would publish `sDirectTags` (for example as AppDomain `ts3fixes.autunseen.tags`), and `FocusBadQueue` would count only the instances in that list.

### 11.11 `bp-gu2-arrive-on` "only 2 Sims arrived" (2026-11-08, static triage, no build, no game run)

**FAIL.** Queue `gu2-arrive-rerun`, job `bp-gu2-arrive-on` (pair g2pair1108, ts3agent with 57cde9a7), 03:06-03:21 UTC: `focus_summary: only 2 Sims arrived on the camera's lot`, `arrivalsMin` 3. The off arm `bp-gu2-arrive-off` r5 (02:50) passed with `arrivals` 3. Rodney used the PC: noise high on, medium off. Sim minutes were about the same (on 2406, off 2447), so the load did not shorten the window.

**Verdict: check and repro defect, not our feature [verified from logs].** In neither arm did a crowd Sim arrive, so the count only measured passers-by:
- `probe-out/profile/gu2-arrive-rerun-2026-11-08-bp-gu2-arrive-{off-r5,on}-activity.jsonl`: across all 26-28 snapshots, none of the 8 `spawn_sims` Sims stands on `Air Spray - 2br, 2ba` and none has Go Here at the head. Only household Sims (Tiara, Lala, Roxy) are there.
- `crowd_visit_lot` returned `not queued` for all 8 Sims in every round of both arms.
- `focus_result` rows (`-repro.jsonl`): the judged Sims are passers-by walking home or to a meet-up (Littlers, Odine Perry, Sugar Bijou, ...), plus 1-3 spawned Sims who were also only passing by. No row ends on Air Spray.
- Round 2 ran on the wrong lot in both arms: `camera {lot: home}` returned the home target, but `focus_watch` one second later read `camera_lot: Plumbob Pictures Backlot` (480.5, 1217). There it counted rabbit-hole workers arriving. Who moves the camera is unknown [inferred: the game follows the selected Sim. Tiara works at Plumbob Pictures, and `crowd_go household: true` queues her a Go Here].
- So 3 (off) against 2 (on) is EA variance among Sims that happen to pass by, not something autunseen causes. `FocusWatchCmd` counted every non-household Sim that stepped onto the camera lot (`FocusHandlers.cs` arrivals line, before this fix).

**Mover counters, on / off:** `autunseen_posted` 2462 / 0, `autunseen_direct` 2457 / 0, `autunseen_tagged` 66 / 0, `autunseen_dropped` 2 / 0, `unseenfb_cut` 194 / 149, `unseenfbteleport_shortcuts` 103 / 68, `unseenfbarrive_shortcuts` 0 / 0 (the arrive placement never fired), `unseenfb_refused` 0 / 0, `gohome_runs` 301 / 343, `watchdog_idle_stationary` 2 / 4. Household slots 867-870: `g8_hh_q_n` 137 / 220, `g8_hh_q_ticks` 1878 / 1892, `g8_hh_w_n` 44 / 1, `g8_hh_w_ticks` 82 / 1.

**Household untouched [verified]:** neither arm's `-moved.jsonl` (307 / 265 entries) has an Angelista Sim or Roxy in it, and none has a placement onto Air Spray.

**Check fix (ts3agent, this commit):** only `spawn_sims` crowd Sims count toward `arrivalsMin` (`FocusRules.CrowdArrival`, which has a selftest row). Other Sims are reported as `arrivals_other`, and `arrival_names` lists the crowd Sims that arrived. With the repro as it is, both arms will now fail honestly with 0 arrivals.

**Repro fix proposal (`repros.json` `aut-unseen-arrive`; applied in §11.12, smoke not run yet):**
1. `crowd_go {target: home, cancel: true}` and drop `household: true`. Without cancel, the user-directed Go Here waits behind the crowd's current visit and never reaches the head. `household: true` puts Go Here into the household's queues, which the test does not need, and it is the likely camera mover [inferred].
2. Why `crowd_visit_lot` gives `not queued` [inferred]: `VisitLot` onto a foreign household's home lot fails its Test for Sims the household does not know. Try greeting them first (or `how: goto`), or use a community lot as the target with the camera on it.
3. Smoke: one round, then read `arrival_names` and see whether the crowd reached the lot. If it did, rerun `bp-gu2-arrive-off` / `-on`.

### 11.12 `aut-unseen-arrive` repro fix: why `crowd_visit_lot` was `not queued` (2026-11-08, static + build, no game run)

**Where it fails [verified, decompiled Sims3GameplaySystems].** `InteractionQueue.Add` -> `InsertInteraction` returns false only when `InteractionInstance.Test()` fails. For `VisitLot.Definition.Test` that is `a.IsGreetedOnLot(target)` (greeted Sims may not "visit") or `Lot.CommonVisitTest`. With `autonomous: false` the user-directed gates left in `CommonVisitTest` are: community lot, own lot, no household and no callbox, private celebrity lot, `VisitSituation.IsTooLateToVisit()` (0:00-5:00 by the code default `kStartUnacceptableVisitTime` 24 / `kEndUnacceptableVisitTime` 5; tuning XML not checked), banned from an NPC party, and "nobody home" (no Sim on the lot, or none of them `IsAtHome`), plus `LotCurrent == target` indoors and `LotHome == target`.

**Which gate per round [inferred from the run logs; the Test result was not logged then].** Game clock at each round's `crowd_visit_lot` (`-repro.jsonl` `set_speed` rows): on arm 23:25, 13:55, 01:28; off arm r5 00:20, 14:56, 01:08.
- Rounds at 00:20, 01:08, 01:28: too late to visit (inside 0-5 h).
- Rounds at 13:55 and 14:56: Tiara and Lala are at work (`-activity.jsonl`: Plumbob Pictures Backlot, Steve's Business Complex), only Roxy the dog is home, so "nobody home" is the likely gate (whether a pet counts for `IsAtHome` not checked).
- On round 1 at 23:25: no gate above fits; `IsGreetedOnLot` for the crowd's households, or a tuning or special-case test, stays open. The new log line below names it on the next run.
- Earlier runs: `sim_visit_lot {lot: home, autonomous: false}` queued `VisitLot` onto the same home for save Sims (Wogan Hemlock, Johnny Jones), so the gate is time and household state, not "unknown visitor" as such.

**Fix (ts3agent, this commit):**
- The repro uses `crowd_visit_lot {lot: home, how: "goto"}`: `GoToLot` (the plain lot trip NPCs use; on a residential lot it walks to the front door or mailbox, outside, via `RouteToFrontDoorOrMailbox`). Its Test only refuses a Sim already on the lot with local autonomy, or an autonomous selectable Sim [verified, `GoToLot.cs`]. No time, greeting or nobody-home gate, so the arrive case no longer depends on the clock or on the household being home.
- `crowd_visit_lot` now writes the reason into each refused entry: `"<name>: not queued (<InteractionTestResult>: <greyed-out tooltip>)"` (shared helper `VisitTestResult`, also used by `sim_visit_lot`). New `min` arg: fewer queued throws (smoke gate).
- `crowd_go {target: home, cancel: true}` without `household: true` in all three rounds: no household Sim is commanded, and the crowd's last trip is cancelled so Go Here reaches the head.
- `focus_watch {scope: screen, lot: home}`: throws when the camera is on another lot, so a round like §11.11 round 2 (Plumbob Pictures Backlot) fails instead of counting rabbit-hole workers. Who moved the camera there is still not proven [inferred: the selected Sim's Go Here]; if it still drifts with no household command, this check shows it.

- Camera drift cause and fix (2026-11-09, static, no game run): the camera follows an object via the game's `CameraController.EnableObjectFollow` (`Camera.FollowSim`; e.g. `RabbitHole.cs:3905-3911` re-enables it for the selected Sim, Tiara at work at Plumbob Pictures), and a follow overrides `SetPositionAndTarget` every frame, so `camera {lot: home}` returned the home pose and the camera snapped back onto the followed Sim [inferred from decompiled `Camera.cs:1492-1504`; the next run's `followed` field confirms it]. `camera` with `lot`/`pos`/`target` now calls `DisableObjectFollow` first and returns `followed_id`/`followed` (camera-only; no Sim is commanded).

**Smoke job (not run):** `aut-unseen-arrive-smoke`, first job of `tools/harness/queues/gu2-arrive-rerun.json` (BP, autunseen off, profile 6 min, repro ~4.5 min), repro `aut-unseen-arrive-smoke` in `repros.json`: one round, gates `crowd_visit_lot` `min: 6` (of 8) and `focus_summary {min: 1, arrivalsMin: 1}`. Needs a ts3agent build with this commit in Bin. Pass: rerun `bp-gu2-arrive-off` / `-on`. Fail at `crowd_visit_lot`: the entries now carry the Test result.

Compile check: `TS3_BUILD_DIR=<scratch> node tools/harness/build-agent.ts` built against the game DLLs; build/ and Bin untouched.

**`ip-gu2-arrive-off` 2026-11-08 FAIL `focus_watch: camera on Picturesque Port, not Atlantis - 1br, 2ba`: check bug on a houseboat home, not a camera move [verified in the logs].** The IP home Atlantis is a houseboat docked in Picturesque Port (`probe-out/profile/ip-jps-verify-houseboats.json`: boat at 1423.4,979.2, port Picturesque Port). Its lot centre, which `camera {lot: home}` and `crowd_go {target: home}` used, is an off-world slot (230,2064.5). The camera step reply shows the position applied (230,2034.5) and the target kept at the save's view on the boat (1426,977.9, 3 m from it), so the camera never left the home boat; `LotManager.GetLotAtPoint` under the boat returns the port lot. The 11-07 IP runs had the same camera target on all rounds (`camera_lot` Picturesque Port) and their `crowd_go` sent the crowd to the off-world point. Fix (ts3agent): `LotViewPoint` (houseboat lot = its `Houseboat` object's position, else the lot centre) for `camera {lot}` and lot targets of `crowd_go` and the other `WalkGoal` users; `focus_watch {lot}` passes a houseboat when the camera target is within 30 m of the boat (`FocusRules.CameraOnLot`, self-check row in `tools/selftest/focusrules`) and then counts arrivals on the houseboat lot. Go Here onto the boat deck is not proven yet: the next IP run shows whether crowd Sims board (arrivals on Atlantis). Compile check into a scratch `TS3_BUILD_DIR`; no game run.
