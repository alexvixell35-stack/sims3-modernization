# 2026-11-18: Dalexlol switches with his in-game repro turned ON (build set dalexlol-540-1118b)

By Dalexlol. Contributor data from his harness, copied from his PRs, `tools/harness/sets/proven-fixes.txt` (his set of
switches proven in game: off `verdict: BROKEN`, on `verdict: FIXED`, controls ok) and the docs each switch cites; not rerun here.

Rule (Rodney 2026-11-18): Dalexlol switches stay ON when he has test results that reproduce the bug on his end. Our live
counters at 0 (no natural hit) and "no gain yet" are not reasons to keep them off. Exceptions that stay off: save-changing
fixes (opt-in), switches he keeps off himself, work moved to Apex, and anything that fails the CLAUDE.md household rules.
Household and save review: one read of every hook body (ts3-rule-reviewer, 2026-11-18).

## Turned ON (ts3fixes default + tuning)

| Switch(es) | His evidence | Household check |
|---|---|---|
| birthdayagers, sneakoutlistener, promcarpool, curfewalarm, trickortreatalarm, snowfightjoinwait, hauntedumbrella, permazombiemoon, steadygigmiss, npcshowstuck, gigpullaudience | proven-fixes.txt, in-game soak 2026-11-15 off BROKEN / on FIXED (docs/571) | EA bug fixes, same for every Sim; npcshowstuck only touches NPC shows |
| lunacysocials, fairyauratimer, npcseasonalparty, professorterm, protestlastday, seasickpaused, resortweddingalarm, plumbotfacelistener, efficientchip, sentiencename, jetpackflyingbuff, horsetraitscore, wildhorsegender, littercap | proven-fixes.txt, soak 2026-11-15 (docs/572) | EA bug fixes; littercap restores EA's own 10-member household rule |
| phoneloadreset, phonecallschedule, phonecallrearm, invitecallreply, gradvisitors, resumetns, unitravelflag, returnhomealarms, phonefixup, phonegive | proven-fixes.txt, soak 2026-11-15 (docs/573) | EA bug fixes; phoneloadreset only drops a stale call at load |
| wildherdmother, splitpartner, movingflag, sandscoins, tombmusic, artsellnoartist, royaltyorphan, promoteshift, gotoworkstale | proven-fixes.txt, soak 2026-11-15 (docs/574) | EA bug fixes; splitpartner acts on NPC households only; royaltyorphan removes only the duplicate alarm EA leaks |
| fireactivejob, firetrapcount | registry: proven in game 2026-11-15, Bridgeport off BROKEN / on FIXED with controls (docs/570) | EA bug fixes, same for every Sim |
| tipsnpcend, putdownsurfaceuse, swiperestore, unicornspawnfail | PR #529 + registry: one load off BROKEN / on FIXED, r72 force check (docs/613, docs/614) | tipsnpcend and unicornspawnfail: unseen NPCs only, IsPlayerSim exempt (HouseholdToo never lifts it); others object-level |
| spoilbroadcast, weatherstonerelisten, birthdayagerecord | PR #532 + registry: one load off BROKEN / on FIXED, r73 force check (docs/615) | object-level / EA bug fix |
| pushwaitskip | PR #497: Bridgeport, one load, off BROKEN / on FIXED, controls ok, r63 (docs/604 §2) | EA bug fix, every Sim (same Sims pushed, only the extra wait goes) |
| tspawninvdup | PR #515: one load, off BROKEN / on FIXED, controls ok, r64 (docs/607 §2) | object-level (duplicate inventory handler) |
| takeariskguard | PR #540 comment 2026-11-17: off, EA threw on the null boss 5 of 5; on, 0 of 5 threw, 10 skips | crash guard; same result as vanilla otherwise |
| bouncerskip | proven-fixes.txt (his s60 check); also verified naturally on our census 2026-11-18 (PLAN.md) | exact: skips a re-route to the slot the bouncer already stands on |

## Turned ON (ts3native.ini)

| Key | His evidence | Household check |
|---|---|---|
| `[RouteAsync] ReadyDrain=1`, `ReadyDrainGapUs=800` | PR #468: two Bridgeport runs (gap 300 / 800): calls served past the first 28,390 / 38,576, jobs done in the same poll 71 / 340, game hitches 1 / 0, frames at the 3 ms budget 0 / 4 | same calls, same order, same thread for every Sim; only served sooner |

## Still OFF

| Switch | Why |
|---|---|
| woohootywait | his repro exists (docs/573), but the wait cap (400 polls) applies to every actor with no household / selected / unknown-scope exemption (PhoneLogic.cs): fails the household rule |
| sarcmummynpc | his repro exists (PR #515), but its effect is removing a mummy description from the persisted NpcHousehold: save-data change, opt-in |
| routableasync | PR #469 run needs native-wide prerequisites that are off on master (`[RouteAsync] Kinds=2`, `H2Ic=1`, `KdOwnPlanFromPointMs=0`); turning those on changes more than this switch, needs Rodney's call |
| gohomespot2 | PR #481 is not merged (code not on master; its slots 3424-3426 clash with batch 2); his gm4/gm6 counter runs count as evidence, so it needs an import first |
| dormantprobe | measurement only (docs/630), not a fix |
| unseenloopskip_offscreen | he keeps it off himself (docs/632) |
