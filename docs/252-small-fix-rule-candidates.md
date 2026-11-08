# Small-fix rule: which waiting fixes fit (2026-11-08)

Rule (Rodney 2026-11-08): a really small, localized fix (a few hooks or a guard in one narrow EA path that acts only in the broken state; no per-frame or per-Sim work on the normal path) needs self-checks, a compile and ONE repro run where the bug triggers and the fix handles it. No timing, no A/B, no second clean run; N/A once = report, no rerun. Hot paths (routing, autonomy, Mono, per-frame hooks, throttles) keep the normal gates.

Static scan, no game run. Sources: PLAN.md (Imports lines 116/122, 200, 259, 318, 322), `docs/data/builds.json` (latest ts3fixes tuning `bookhand1108c`: every switch below is False), `Instantiator.cs` defaults, `tools/harness/repros.json`, `tools/harness/queues/*.json`, `docs/data/results/*-queue-*.md`. Size = lines of the source file (`mods/ts3fixes/src/`, one file per switch unless noted). Why they wait: PLAN 11-05 "Still off ... next: a clean bundle run of these defaults".

## Fits, repro exists (queue `small-fixes-1108.json`, 15 jobs, 92 min of play)

| Switch | File (lines) | Path it touches | Repro | Last result | Note |
|---|---|---|---|---|---|
| `jazzparam` | JazzParam.cs (78) | `Sim.UpdateJazzParameter`, music buff add/remove: catches one NRE | jazz-param-check | PASS 11-01; PASS in bundle-1105 and 1105b | |
| `buffupdate` | BuffUpdate.cs (175) | `BuffManager.UpdateBuffs`: catches out-of-sync, runs it once more | buff-update-check | PASS 11-01 | borderline: one try frame on a per-Sim update, no other work |
| `carnoroute` | CarNoRoute.cs (84) | `CarNpcManager.DriveFromPointToMatrix`: false for a destroyed car | car-no-route-check | PASS 11-01; PASS bundle-1105/1105b | |
| `npcdriverpool` | NpcDriverPool.cs (115) | `NpcDriversManager.CreateNpcDriver`: drops a dead pooled description | npc-driver-pool-check | PASS 11-01, 1105; 1105b FAIL (case not reached, reused session #2) | |
| `bookcarry` | BookCarry.cs (154) | `Book.StopUsingBook` standing/sitting branch: ends the dropped carry machine | book-carry-check | PASS 11-01; bundle-1105 FAIL (reflect_call) | 12 min repro |
| `ageupabort` | AgeUpAbort.cs (123) | `AgingState.PreInstantiateSim`: catches the NRE after an abort | age-up-abort-check | PASS 11-01; PASS 1105 | |
| `rewardgiver` | RewardGiver.cs (65) | `GameObject.SetRewardGiver`: builds the owner component | reward-giver-check | PASS 11-01; PASS 1105/1105b | custom opportunities only |
| `uniformbackup` | UniformBackup.cs (212) | static `Occupation.backupOutfit` cleared once EA uncached it | career-uniform-backup-check | PASS 11-01 BP and LR | BP job only in the queue |
| `invnotyet` | InvNotYet.cs (79) | `Sim.TryAddObjectToInventory`: null inventory refuses | inv-not-yet-check | Dalexlol PASS (forced state); not run on this tree | |
| `metricskillmissing` | MetricSkillMissing.cs (99) | career panel skill metric: empty name, neutral value | metric-skill-missing-check | Dalexlol PASS (forced state); not run on this tree | |
| `fireworksfizzle` | FireworksFizzle.cs (120) | fizzle watcher: finishes instead of SacsTimeout | fireworks-fizzle-check | Dalexlol PASS (forced fizzle only); not run on this tree | |
| `petpoolorphan` | PetPoolOrphan.cs (99) | adoption/horse pool drop: disposes the orphan pet | pet-pool-orphan-check | Dalexlol PASS (forced); not run on this tree | changes save data: Rodney to say whether the save matrix still applies |
| `reltablesapart` | RelTablesApart.cs (83) | `Relationship.GetInternal` split tables: rejoin | rel-tables-apart-check | Dalexlol PASS (forced); not run on this tree | changes save data (same question); cause of the split unknown |
| `ageupkeepoutfits` | EaFixes.cs (563, one hook) | `SimBuilderTask.FinishOutfitSwap` skipped on a failed build | bp-ageup-outfits-check | PASS 10-30 (twice) | |
| `bookhand` | BookHand.cs (223) | put-down that would throw after `StopUsingBook` | book-hand-check | 11-08 13:33 on arm FAIL (repro cure bug, fixed in b4276229) | gate seen>=1, fixed>=1 |

Counter gates per job are the repros' own (`<switch>_caught|false|dropped|ended|added|refused|names|finished|disposed|rejoined|cleared|kept >= 1`), 0 script errors, no crash. Pair: asi `ts3native.asi.castrimrel1108` (0.36.196, repo ini as shipped incl. CasTrim) + `ts3fixes.package.bookhand1108c` (newest ts3fixes; superset of castrimrel1108).

## Fits the rule but needs a repro

| Switch | File (lines) | Why no usable repro |
|---|---|---|
| `ageupswap` | AgeUpSwap.cs (180) | hang unreachable in 4 game runs (docs/503 §6); the 11-01 run passes only the control |
| `socialworkeronlot` | EaFixes.cs | `bp-socialworker-onlot` is natural only, no forced trigger |
| `eanullguards` | EaFixes.cs | four guards, one repro `bp-eafixes-check` FAILed 10-29 (TargetException, invalid target); needs one forced case per guard |
| `posturepopcycle` | PosturePop.cs (57) | static only (`posture-pop-loop-check`); planner-adjacent, check it is not on the routing hot path |
| `vaclistenernull` | EaVacation.cs | tiny `RestoreState(null)` guard, but only the whole vacation set has repros (`bp-vacation-*`, not run) and v0.2.6 already removed the base-class patch |

## Does not fit

| Switch | Why |
|---|---|
| `convturn` | hooks `SocialComponent.ScoreSocialsOnSims` (autonomy path, runs for every scoring pass) |
| `deadrefguards` | `Relationship.Get` is hot; repros FAILed 10-30 |
| `deadrefsweep`, `savecleanup*` | spread passes, changes save data, full save matrix |
| `preferredservice`, `showstagethrowables`, `firepitchair` | change save data (save matrix) |
| `vacexport`, `vacstoreguard`, `vacreturnskip`, `vacteendelete`, `vacexitreturn`, `vacimportfix` | a 510-line feature set that rewrites the vacation flow and save/load; `ea-vacation.json` never run |
| `menumaven` | behaviour change (more recipes count), not a broken-state guard; Rodney's call |
| native `[EaCrashFixes] ConformActor` (off) | route-follow body, per-frame routing path |
| native `[ArenaGuard] Enabled`, `kd*`, `RouteAsync`, `SyncCapMs` and the other "OFF until A/B" ini keys | interpreter frames, routing, throttles: normal gates |
| all `kUnseen*`, `kWake*`, `kReplan*`, caches, throttles, `kAutLiveLate` | hot paths, normal gates |

Not checked: the 455 queues one by one (only those named for the switches above); `[IlPatch]`, `[Compaction]`, `[MonoJit]` are Mono or stopped work.

Queue: `tools/harness/queues/small-fixes-1108.json` (dry-run OK, not run).
