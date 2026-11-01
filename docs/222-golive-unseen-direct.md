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
