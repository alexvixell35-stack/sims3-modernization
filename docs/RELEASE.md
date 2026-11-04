# Release checklist

How a release goes out. Current: **v0.1.4** (background routing on by default), published 2026-10-20 (PR #55, tag v0.1.4).

## v0.1.4 release build

| File | Version | sha256 |
|---|---|---|
| `native/ts3native/out/ts3native.asi` | ts3native 0.36.0 | `fa25a2c8420e5fb6546c13474b2e0ccfc6b03ac3b5b6ca9e28fc2196f525f007` |
| `native/ts3native/out/ts3native.ini` | release defaults | `68b2cfc27572b7679a1902c12c868efb6150f05c1e06a837ff068567114d1af0` |
| `build/ts3fixes.package` | defaults as v0.1.3 (17 tunables True; shortcuts kGoHome, kBuildingTrip, kLotTrip, kVirtualHome True) | `c09fb14d283f6261cc780c42ab5207763d2b379a235e2b5669d40f60f17414ee` |

Release `ts3native.ini` defaults: on = `[GC]`, `[WaterJps]`, `[Decompress]`, `[SaveWrite]`, `[LotRemoveGuard]`, `[OptsInit]`, and new in v0.1.4 `[RouteAsync]` (`Mode=1`, `Kinds=0`), `[RouteGate]` (`Sites=1`, `Enforce=1`, `G10Common=0`), `[Discourage]` (`Mode=1`, `DestroyApply=1`, `DeadSweep=1`). Everything else off, including `[RouteSnap]` (P3-P5c code ships, `Enabled=0`). The installer (`install-fixes.ts`) and CI (`.github/workflows/build.yml`) both ship `out/ts3native.ini`, which `build.cmd` copies from `native/ts3native/ts3native.ini`.

Rebuild check: a rebuild from the release commit must give the same ini hash; the asi hash can differ only if the toolchain changed. Selftest note: one build of 0.36.0 failed once at `selftest.cpp:8441` (`W6b lots: nDirty -1`, threaded replay test), then passed 3 times in a row and in the next full build; a flaky threaded test, unrelated to the ini change, not yet investigated.

## v0.1.5 (released 2026-10-24)

The routing release. Each candidate ships on only if its A/B passes (`tools/harness/queues/clean-night-0.2.0.json`, ~7 h, plus the existing per-feature queues for the ones not in it: `defer-footprint-ab`, `destroy-park-ab`, `candidate-loop-ab`, `replan-defer-ab`, `avoid` jobs). Mono features are not in this release.

### Decisions

**Decided 2026-10-24** (ship rule: positive forced-repro A/B and no crash; docs/release-notes/v0.1.5-dev.md): `node tools/release-features.ts overwatchchain,refcountguard,deferfootprint,stuckcheckasync,errortrapspread --write`, plus the crash K gate site 16 (code). Every other candidate in the table below ships off; reasons per candidate in the dev notes ("Not shipped, and why"). The table is the 2026-10-22 suggestion record.

Suggestions 2026-10-22 from the clean-night pair means (off-first + on-first, both orders and all numbers in `docs/data/results/2026-10-22-v015-decisions.md`). Every entry marked "suggested" is open until Rodney decides. Suggested set: `node tools/release-features.ts slotenabledro,unseenthrottle,stuckcheckasync,errortrapspread` (dry run only; `--write --docs` after his OK, on the release branch).

| Feature id | What | A/B (clean night) | Decision | Edit when on |
|---|---|---|---|---|
| `fallbackasync` | `[RouteAsync]` foreign-task plans async + 1 s cap (docs/134) | `*-fb-*` | suggested: ship off. Pair means BP/SV/IP it/s +1.3/-0.9/+9.1%, route -21/-3/-15 ms/s, keyed wait +14..+18 ms/s; multi-second freezes only with it on (SV 1,027 ms; 7.9 s twice in fbjobcap-ab; 8-16 s in candidate-loop SV) and `fbjobcap` does not bound them. `SyncCapMs=1000` alone is not expressible in `release-features.ts` (open) | `FallbackAsync=1`, `SyncCapMs=1000` |
| `m1retry` | `[RouteAsync]` parked M1Pending STARTs (needs `fallbackasync`) | `*-fb-*` | suggested: ship off (needs `fallbackasync`) | `M1Retry=1` |
| `fbjobcap` | `[RouteAsync]` worker job cap for foreign plans (docs/134 §10, needs `fallbackasync`) | `fbjobcap-ab` `*-fjc-*` | suggested: ship off. `fbjcCapped` 0 in every on slice (the 7,881/7,767/1,457 ms off-side frames vs 547/358 on are where long jobs fell, not the cap); BP route-bits: the one capped job still ran 1,948 ms (cap seen at 1,942 ms) | `FbJobCapMs=300` |
| `slotenabledro` | `[SlotEnabledRO]` (docs/90, docs/136) | `*-slotro-*` | suggested: ship on (decided on BP earlier). Pair means IP +4.9%, SV -1.9%, BP -5.4% (both BP orders negative on a clean pair: Rodney to weigh), mean -0.8%, no crash | `Enabled=1` |
| `avoidro` | `[AvoidRO]` (docs/137) | `clean-night-routing-extra` `bp-avoidro-*` | suggested: ship off. `avTakenHit` 0 in every job (never answers); pair -0.4% it/s, wait +6.0 ms/s | `Enabled=1` |
| `deferfootprint` | `[Discourage] DeferFootprint` (design U) | `defer-footprint-ab.json`, routing-extra off-first only | suggested: ship off. Pairs -0.6% / +0.7% it/s, wait +5.7 / +8.4 ms/s; clean on-first not run | `DeferFootprint=1` |
| `destroypark` | `[Discourage] DestroyPark` (design G) | `destroy-park-ab.json`, routing-extra, `destroypark-fix-verify` | suggested: ship off. Crash I on 0.36.14 in the clean night; fix 0.36.19 has 1 clean job of the 2 nights required; pairs +0.5% / +3.5% | `DestroyPark=1` |
| `unseenthrottle` | `[UnseenThrottle]` (docs/143; phase 2 if it lands) | `*-ut-*` | suggested: ship on, List only (docs/143 §8.15). Pair means +0.6 / +2.8 / +5.6 / +6.1% it/s (BP, IP, BP crowd, BP list) | `Enabled=1` |
| `stuckcheckasync` | ts3fixes 3 AM stuck check in a task | `stuckcheck-async` (two loads, daytime) | suggested: ship on (weak: no pair). +6.5% it/s, >= 300 ms frames 7 -> 4, `stuckcheckasync_run` 240, no crash; release-validate repro is the gate | `kStuckCheckAsync` True |
| `replandefer`, `replanspread` | ts3fixes door / build-buy replans | `replan-defer-ab.json` | suggested: ship off. Only run fails the load rule (18.5% / 16.3% other CPU); +0.4% it/s, >= 300 ms 11 -> 12 | `kReplanDefer`, `kReplanSpread` True |
| `safespotcap`, `farthestsorted`, `indoorcap` | ts3fixes candidate loops | `candidate-loop-ab.json` | suggested: ship off. Single loads, 9.7-15.2% other CPU; BP -1.0%, SV +6.8% with 7,985 ms frames on (FallbackAsync pattern) | `kSafeSpotCap`, `kFarthestLotSorted`, `kIndoorPositionCap` True |
| `overwatchspread` | ts3fixes NRaas Overwatch nightly spread | `*-spreads-*` | suggested: ship off. No fewer TaskObjects bursts in the night windows (BP off 4 / on 3, SV off 5 / on 3, on max higher); SV -7.4% it/s in both orders; alarm firing in counted slices not shown | `kOverwatchSpread` True |
| `easpreadlots`, `easpreadpets`, `easpreaddecay` | ts3fixes EA nightly alarms spread | `*-spreads-*` | **ship on (Rodney 2026-10-28)**: repros 10-27 PASS (same lots/pets/relationships, 0 threw, longest tick 2-7 ms), own A/B `bp-ab-easpread` in noise (>= 300 off 1 / on 0, not an EA alarm); gain only on old/large saves, not measurable on BP. Earlier ship-off came from the combined run with `overwatchspread` | `kEaSpreadLots`, `kEaSpreadPets`, `kEaSpreadDecay` True |
| `errortrapspread` | ts3fixes NRaas ErrorTrap load scan spread | `bp-et-*` | suggested: ship on. Both two-load pairs: TaskObjects `/taskstall` rows 4 -> 3, 1,347 / 1,389 -> 888 / 1,047 ms, max 420 -> 354 ms | `kErrorTrapSpread` True |
| `unseenenv`, `unseenmotives`, `unseenbroadcast` | ts3fixes unseen script throttle (docs/144) | `*-unseen-*` | suggested: ship off. BP +5.5%, IP +7.2%, crowd +3.4%, but SV >= 300 ms frames 1 -> 13 over the pair (both orders) and route +7.8 ms/s BP/SV; switches never measured apart | `kUnseenEnvThrottle`, `kUnseenMotiveThrottle`, `kUnseenBroadcastThrottle` True |

### Release defaults in one step

`node tools/release-features.ts <ids from the table, comma-separated> --write --docs` sets every candidate's `ts3native.ini` keys and `ts3fixes` tunables (`tools/setup/build-fixes.ts`) to on or off, refuses a feature without what it needs (`m1retry` needs `fallbackasync`), warns when CI's stable list in `.github/workflows/build.yml` lacks an enabled section, and resolves the feature blocks in `docs/PLAYERS.md` and the CHANGELOG `## v0.1.5` section (on text for chosen ones, the "shipped but off" line for the rest). Without `--write` it only prints the edits; `--list` shows the table, `--status` what the repo has on now; `none` is the v0.1.4 state. `--docs` is one way (the markers go), so run it once, on the release branch. Then: build (`native/ts3native/build.cmd`, `node tools/setup/build-fixes.ts`), fill the hashes below, copy the asi to `game-copy/ts3native.asi.rel015`, run `release-validate.json`.

CI's stable list now also accepts `[SlotEnabledRO]`, `[AvoidRO]` and `[UnseenThrottle]` as `Enabled=1`; drop the ones not shipped from the list before tagging.

### v0.1.5 release build

| File | Version | sha256 |
|---|---|---|
| `native/ts3native/out/ts3native.asi` | ts3native 0.36.37 (commit f5054207, `out-rel015`, selftest ok; parked as `game-copy/ts3native.asi.rel015`) | `7582da7881727292cc74622e62cb3dc1d4819a5aa378db584146f0f90fbe730d` |
| `native/ts3native/out/ts3native.ini` | release defaults from `release-features.ts overwatchchain,refcountguard,deferfootprint,stuckcheckasync,errortrapspread --write` (= repo ini) | `eacb06112367fdfa826b3c7af2aab1e4da5069fa569e515c1f4c7a905ac58446` |
| `build/ts3fixes.package` | v0.1.4 defaults + kStuckCheckAsync, kOverwatchChain, kErrorTrapSpread True, kOverwatchSpread False (shortcuts kGoHome, kBuildingTrip, kLotTrip, kVirtualHome True; built 2026-10-24 from f5054207 by the release queue, tunables checked) | `949dac596eda91470baa4b31e6697ad73288cf1563b684f83944b53c4e131bc6` |

Release `ts3native.ini` defaults: as v0.1.4 (`[GC]`, `[WaterJps]`, `[Decompress]`, `[SaveWrite]`, `[LotRemoveGuard]`, `[OptsInit]`, `[RouteAsync]` `Mode=1` `Kinds=0`, `[RouteGate]`, `[Discourage]`) plus `[RouteAsync] RefcountGuard=1` and `[Discourage] DeferFootprint=1`; crash K gate site 16 is code under `[RouteGate] Sites=1`. Validation: the generated release check (below; replaces the fixed 46-job `release-validate.json`, which stays as the source of the old jobs and the fallback), results `docs/data/results/2026-10-24-queue-release-check.md`; pass rules as below plus `fbCapped` per job and route fails not above v0.1.4's.

Result 2026-10-24: **22 of 22 PASS, 0 crashes** (a smoke 3, b BP save matrix 9 of 9 cases + France trip, c fix-on repros 9, d bundle A/Bs 5, e rare water jobs 2, f BP soak 45 min: 0 >= 500 ms, worst 498 ms), plus 5 extra jobs PASS (`2026-10-24-queue-release-015-extra.md`: README comparison runs per world, SV soak on v0.1.5, IP v0.1.4 rerun). Per-part numbers, the part d off -> on table and natural play vs v0.1.4 and vanilla: docs/release-notes/v0.1.5-dev.md.

### Release check: queue from what changed (docs/170 §4)

After `release-features.ts <ids> --write`, the build and the asi copy:

1. `node tools/release-check.ts v0.1.4` (dry run): prints the ship set, what changed since v0.1.4 (git diff, map `tools/harness/feature-repros.json`), the time per part, the features without a repro and the coverage table against release-validate.json. Before the write, `--features <ids>` shows the same for a candidate set.
2. `node tools/release-check.ts v0.1.4 --write --install-repros`: writes `tools/harness/queues/release-check.json` and splices the derived `rc-*` loop repros into `repros.json` (one block at the end; only when nobody else edits it).
3. `node tools/harness/queue.ts tools/harness/queues/release-check.json`. Parts: a smoke (BP/IP/SV 6 min), b BP save matrix + france trip, c forced repro per new/changed shipped feature with the fix on only (fixed event count), d bundle A/B on those loop repros (ABBA, 10 firings per arm; clean night), e the release-validate.json rare jobs of shipped features whose code changed (incl. SV/IP matrices and the other trips when save code changed), f 45 min BP soak.

Estimate for the suggested v0.1.5 set (slotenabledro, unseenthrottle, stuckcheckasync, errortrapspread): 41 jobs, ~6.7 h, of which a-d + f ~2.9 h and e ~3.8 h (routegate.cpp and savewrite.cpp changed since v0.1.4, so the route and save rare jobs come back). A release with unchanged shipped code: a + b + f ~1.5 h plus c + d for its new features.

## Release wrap-up in parallel (Rodney 2026-10-24)

Once the last test of the release check is green: compute the numbers once (hitch tables per run, soak, test table), then write in parallel (agents for the README table and the test/dev-notes tables, the main session for the player notes and the public-copy prep), then one read-through for plain wording and matching numbers before the commit. Never start this while a timing run is still going (the number crunching adds load); text-only work may start earlier. Release ETAs count the wrap-up at its parallel length (~20 min after the last green test: ~5 min numbers, ~10 min parallel writing, ~5 min read-through), not the serial sum.

## CI-skipped selftests

GitHub runners are shared and slow, so selftest checks that depend on wall-clock time or thread interleaving flaked there.
On CI (`GITHUB_ACTIONS` or `CI` set) these checks print `selftest <name> SKIPPED on CI (timing-dependent, runs locally)`
and still run locally; the logic asserts around them (no writer inside a job, counters, refusals) stay on everywhere.
`TIMING_ASSERT` in `native/ts3native/selftest.cpp`. Each one should become deterministic (fake clock, event handshakes
instead of sleeps, fixed interleavings) and then go back to plain `assert`.

| Test (file) | Checks skipped on CI |
| --- | --- |
| AllocSites (`allocsites-test.h`) | first hook entry while another thread holds the PEB / process heap lock (3 s wait; failed 3 CI runs 2026-10-26/27) |
| P5b (`p5b-test.h`) | destroys and time lent, steering / gate reads inside lent bodies, mean waits drop per mode, most lent waits granted, admitted wait per job drops |
| P5c (`p5c-test.h`) | HoldKeyed gates admitted inside jobs, sim calls got the lock inside job bodies, FreeLock share and mean waits (failed locally twice), HoldKeyed=2 site 10 admitted |
| TailUnlock (`tailunlock-test.h`) | steering granted in a lent body, retake < 1 s |
| Gate0RO (`gate0ro-test.h`) | waited >= 10 / 8 ms (Sleep on a helper thread), served hit < 5 ms |
| H2 window (`selftest.cpp`) | job-wait plan < 200 ms, watchdog wait < 2 s, > 50 starts in 300 ms, held-job release < 100 ms, window hold < 250 ms |
| H2Ic, RcGuard (`h2ic-test.h`, `rcguard-test.h`) | job-wait plan < 200 ms / < 1 s |
| RouteAsync (`selftest.cpp`) | lock deadline 30 ms returns < 250 ms, foreign-cap own job < 200 ms |
| LotStream pump, DeferFootprint(2) (`selftest.cpp`) | empty pump pass < 1.5 ms, record age < 300 ms |
| UnseenProbe, UnseenFallback (`unseen-test.h`, `unseenfbc-test.h`) | part age < 399 ms, lock deadline <= now + 110 ms (GetTickCount ticks 15.6 ms) |
| MemExp (`memexp-test.h`) | census < 250 ms |

## Before publishing (Rodney OKs each)

1. **Validation queue passed** on a clean night: `node tools/harness/queue.ts tools/harness/queues/release-validate.json` (33 jobs, ~18 h of timeouts, about 10-12 h real; split with `--only`). Before it: copy `native/ts3native/out/ts3native.asi` to `game-copy/ts3native.asi.rel0360` (check the sha256 above), run from a checkout with the release ini merged (the queue writes `Bin/ts3native.ini` = repo ini; no job overrides it), install the release `ts3fixes.package`. Pass rules are in each job's note: 0 crashes, 0 watchdog lines, `ts3native 0.36.0` in the log, `asyncStarted > 0`, stuck Sims within docs/TESTING.md, every save matrix case PASS, travel cases PASS, rare repros ran with their Sims routing.
2. **Every crash triaged** against the change ledger (our build crashed = our bug until proven); a fix means a new build, new hashes here, and a rerun of the affected group (save path: the whole matrix).
3. **CHANGELOG `## v0.1.4` filled**: test table with case count, cases and results from the result page; date in the heading; measured-gain references checked (119 -> 50 ms/s and +10-12% iterations/s come from PLAN.md "Async routing (H6) state" and the 2026-10-16/17 `h6-m1` result pages).
4. **docs/PLAYERS.md** names every enabled section and every ts3fixes True tunable (CI fails otherwise).
5. **Gains wording OK** for players (no claim beyond what the runs show).
6. **Shortcuts on in the attached package** (test queues may switch them off at runtime; the release never ships them off): `grep -aoE '<k(GoHome|BuildingTrip|LotTrip|VirtualHome)Shortcut value="[A-Za-z]+"' build/ts3fixes.package` shows all four `True`, and the package hash matches the table above.

## Publishing (after the OKs; never from the private repo)

Via the scrubbed public copy only (Rodney): sync the tree into a clone of the public repo with `<user>` -> `<user>` and the machine name -> `<pc>` replaced, run `node tools/check-docs-code.ts`, open a PR, squash-merge, tag `v0.1.4`. The tag's CI run builds ts3native, checks the stable list and PLAYERS.md, and drafts the release from the CHANGELOG section; attach `ts3fixes.package` to the draft by hand (CI cannot build it: EA assemblies), then publish the draft. Never push local `master` to the public repo.

**Scripted:** `tools/publish-public.ts` runs these steps. Dry run by default (prints every step, does the clone, scrub, `check-docs-code`, secret scan and commit in a temp clone, stops before the push); `--yes` pushes, opens the PR, watches checks and squash-merges with `--admin`. With `GH_TOKEN="$PERSONAL_GITHUB_TOKEN"` set:
- `node tools/publish-public.ts sync --message-file msg.txt [--yes]`: public sync up to the merged PR.
- `node tools/publish-public.ts release v0.1.6 --native-version 0.36.40 --message-file msg.txt --ini <tested ts3native.ini> --package build/ts3fixes.package [--yes]`: sync, then tag the merge commit, watch the tag's build.yml run, check the draft's zip (asi `ts3native X.Y.Z`, ini sha256 = tested ini, enabled sections), check the package tuning (four shortcuts True, `tools/release-features.ts` tunables), attach it and publish the draft as latest.
- `node tools/publish-public.ts --self-check`: offline checks plus a full `sync` dry run against a local bare repo.
The manual steps above stay as the fallback.

After the release is out, record its published asi in `docs/data/builds.json`, so the harness knows Bin holds a release after a restore (`queue.ts` warns "not a release build" otherwise; `apex-run.ts --release` picks the newest `ts3native.asi.rel*` entry): unzip `ts3native-vX.Y.Z.zip` and run `node tools/builds.ts add <dir>/ts3native.asi --name ts3native.asi.relXYZ --commit <tag commit> --note "vX.Y.Z release asi (published asset ts3native-vX.Y.Z.zip)"`. Record the published file: the CI build is not byte-identical to a local build of the same commit (v0.2.0: published `ab16d8d7`, local `ac313272`).

From v0.1.5 also on ModTheSims, right after the GitHub release and with the same three files: `release/mts/CHECKLIST.md`.
