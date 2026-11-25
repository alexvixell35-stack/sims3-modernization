# 604: Logic scan of the hot EA methods, shopping and ambient systems

2026-11-16, static, r63, NOT RUN. A Roslyn walker (`tools/static/hot-logic-walker/`) plus the existing semgrep rule
sets were run over three targets: (1) the most frequently called EA methods and their callees, (2) grocery and buying
code, (3) the systems that run in every world all the time (Showtime careers, tourists, celebrities, ambient NPC
services). One new EA fix was built (`pushwaitskip`), and one wiring bug in our own code was fixed (`suatgradebonus` was
never applied). Scan output stays in `game-copy/analysis/hotlogic/` (it quotes EA names and line numbers).

## 1. Tools and hot list

- **Hot list** (`tools/static/hot-logic-walker/hotlist.py`): EA frames ranked by sampled sim-thread time
  (`docs/data/compile-candidates.csv`, docs/207; `docs/data/jit-hot.csv`), then the callcensus counts in tonight's
  runs (phase-20261116-2018, -2157, -2218, -2233: 18 methods, e.g. `BuffManager.HasAnyElement` 4.8 M calls,
  `ActionData.Test` 290 k, `Conversation.IsSuitable` 168 k), then `Definition.Test` of the 40 interaction types with the
  most autonomy time (`docs/data/usage-rank-1115.csv`). Top 160 methods; with their direct callees from the managed call
  graph 1,038 names, 864 found as method bodies in the decompile. A second pass with callees to depth 2 (1,850 names,
  1,689 bodies) was run as well.
- **hot-logic-walker** (new, committed; net10 console that uses the Roslyn shipped with the dotnet SDK, so it builds
  offline). Syntax-only classes: INFLOOP (no break/return/throw/yield, or a condition nothing in the body changes),
  MODENUM (a foreach over C that mutates C and does not leave), FWDREMOVE (ascending index loop removing at the index
  without stepping back), FIRSTEXIT (body exits on every path), NESTEDSCAN (world/lot/household scan inside a loop over
  another), WRONGVAR (declared, tested and stepped variables differ; inner index unused while the outer one indexes),
  OFFBYONE (`<= Count/Length`, `x[x.Count]`, a loop that starts at Count and indexes `[i]`), NULLAFTERDEREF (`x.M` then
  `x == null` in the same block with no write between), EVENTNOREMOVE (`+=` of a handler with no `-=` of that name
  anywhere), REPEATCALL (a scan call whose inputs do not change inside the loop). `--files <list>` scans whole files;
  every run also writes `<out>.ranges` (method line ranges) so other scanners' hits can be filtered to the same set.
  Calibration on all 117,728 EA methods: 305 hits after removing four false-positive shapes found by hand (ILSpy's
  cached-bound loops, removals followed later by `break`/`return`, `Remove(other[i])` on a different list, loops that
  start at 1). The full-tree MODENUM, FWDREMOVE, FIRSTEXIT and OFFBYONE hits that were left are listed in §4.
- **semgrep** (`ea-loop-logic`, `ea-gameplay-logic`, `ea-script-logic`) on the 271 files of the depth-2 hot set, the
  52 shopping files and the 164 ambient files; hits filtered to hot method ranges and checked against
  `~/dev/ts3-runs/scan-baseline.txt` and docs.

## 2. Results, target 1 (hot methods)

| Source | Hits | Real |
|---|---|---|
| walker, hot 160 + callees (864 methods) | NULLAFTERDEREF 6 | 0: dead null checks after a dereference (`InteractionQueue.InsertInteraction` entry, `BuffManager.AddBuff` mActor x2, `Autonomy.CalculateScoreForSocial` sim, `CalculateScoreForObjectInteraction` target, `Camera.ComputeQuadrantForLot` lot); each value cannot be null there in practice, and a fix would change nothing [verified, decompiled] |
| walker, depth 2 (1,689 methods) | the same 6 | 0 |
| semgrep on hot ranges | self-compare 9, ternary-same 5, sleep-wait-no-exit 3, self-assign 2, broadcaster-overwrite 2, while-true-no-exit 2 | 0: NaN tests, ILSpy operator artifacts, the simulate loops (they sleep), and a broadcaster made only when the effect is null (`BuffSickAndTired`) |

The hot methods are clean of these loop and logic shapes [verified]: the sampled-hot code is short, well-trodden
getters and tests. The one real bug the scan found near the hot paths is a routing callee (the full-tree walker run,
FWDREMOVE): §3.

### 2.1 pushwaitskip (built, default off, slots 2790-2792)

[verified, decompiled + IL] `RoutingComponent.PushSimsStatic` (8-argument overload; the 6-argument one and
`Sim.PushSims*` call it) pushes the Sims in the way and, with `waitForPushes`, idles the pusher until they are done:
each pass drops the Sims that are destroyed or no longer in a push, then waits 10 sim-minutes, for at most
`TotalSimMinutesToWaitForSimsToBePushed` (15). The pass is a `for (i = 0; i < Count; i++)` whose keep branch also does
`i++` (IL_0208..IL_0212: two `add 1` on the keep path), the shape of the `while` filter for conversations just above
it. So a busy Sim hides the next one, and a removal hides the Sim that slides into its index. A done Sim that was
skipped keeps the pusher waiting one more 10-minute pass, up to the 15-minute timeout. With two Sims in the way
(A busy, B done) the pusher waits 20 sim-minutes where EA's own test says 10. The pusher is often a household Sim
heading for a bed, sofa, counter or stove. docs/572 noted the double step and handed it to routing; nobody fixed it.

Fix as EA intended (`PushWait604.cs`, `kPushWaitSkip`): our copy of the method, statement for statement, except that the
pass steps once on keep and not at all on remove. The same Sims are pushed with the same interactions in the same order;
only the number of 10-minute waits can go down. It is an EA bug fix, so it applies to household Sims too. Calls without
`waitForPushes` run EA's body. The method yields, so the swap keeps a stub (`PushOrig`) and has a swap-stack-audit row.
Counters: [2790] done Sims that EA's pass would have skipped (each one saves a wait pass), [2791] waiting calls run on
EA's body (switch off), [2792] errors (rethrown, as EA's body would throw them).
Check `PushWaitSkipCheck`: EA's pass against ours on fixed patterns. Busy+done: EA leaves the done Sim, ours drops it.
Done x3: EA leaves one. Controls: an all-busy list and a single done Sim give the same result both ways. EpLive asserts
the swap. Repro `pushwaitskip-check` (off BROKEN / on FIXED). For an in-game trigger, push two NPCs off a bed footprint
with one of them in an uncancellable idle. That repro is not built, because the pure pass is the whole difference.

## 3. Results, target 2 (shopping and buying)

52 files: the coordinator's 34 (registers, pedestals, food stands, Grocery / Bookstore / ScienceLab rabbit holes,
`ShoppingRabbitHole`, `ShoppingModel`, `PurchaseVenue`, `Mailbox`, ice-cream and food trucks, shopping UI), every
`*Register` class (EP shops, consignment, potion, relic, nectar, bot, hobby, pet store), `Consignment`, pizza
delivery, `Phone`. There is no separate grocery-delivery class in the decompile; delivery is the pizza service and the
mailbox bills.

- walker: NULLAFTERDEREF 7 (Phone x5, ResortFrontDesk x2), all dead checks; no loop-shape hits.
- semgrep: 44 hits. The ones in purchase paths were checked by hand: `ShoppingRegister` self-compare (operator
  artifacts), `PizzaDeliverySituation.ReceivePizza` flag (it is reset on the failure path, so not a bug),
  `IceCreamTruck.RouteToTruck` (`else if` on a different distance band, fine), Grocery listener (already reviewed in the
  baseline).
- `ModifyFamilyFunds` in the purchase paths (11 sites) by hand. Charge and credit stay paired: `FVShoppingRegister` and
  `LemonadeStand` debit the buyer and credit the owner household. `ServoBotPedestal` charges after the Routed sync and
  before Completed (EA's order; a failure after the charge keeps the money spent, [inferred] rare).
- **New lead, parked:** `FVShoppingRegister.SellConsignedItems` clamps the Gardener level to 9 into `level` but indexes
  `kSelfEmployedGardnerPurchaseBonus[career.Level]` (10 entries). A level-10 self-employed gardener's consignment sale
  throws there, before the items are destroyed and before the payout [verified, decompiled; the max level of 10 is
  inferred]. docs/587 §13 parked this method (it doubles every sale and adds the bonus as a whole number); the fix needs
  a copy of the method together with those two parked items, so it was not built here.

## 4. Results, target 3 (ambient systems) and the full-tree walker run

164 files: Showtime (`SingerCareer`, `MagicianCareer`, `PerformanceCareer`, `Gig`, `ShowStage`), tourists and visitors,
`CelebrityManager`, `RolePaparazzi`, repo, newspaper, mail, ice-cream truck manager, ghosts (`Urnstone`), service
situations.

- walker: 52 hits. 44 are dead NULLAFTERDEREF checks. NESTEDSCAN 6 are one-shot household or load scans, not per tick.
  INFLOOP `Sim.Simulate` is the debug `OneSimActive` branch. EVENTNOREMOVE `IceCreamMaker.OnStartup` is already fixed
  by `StoreWorldEvents.cs`.
- semgrep: 74 hits, none new and real. `MagicianCareer` volunteer wait and the other sleep waits are parked in docs/572
  (each needs its `Run` replaced). The paparazzi jig is in docs/247. `RepomanSituation` rebuilds its broadcaster in
  `PostLoad` only when it is null, which is fine. `Gig.OnAudienceRouteFail` tests `Occupation != null` after
  `Occupation.OwnerSim` (a dead check; the occupation is set for the gig's life). `IceCreamTruckManager` sleeps 300
  ticks per loop, which is fine.
- Ranking: tonight's callcensus covers only the 18 methods in §1, none of them in these systems; the manager loops found
  here sleep between passes.
- Full-tree walker leftovers, not hot, all checked: `ResortManager.RefreshVIPRooms` (already `viproomrefresh`),
  `IndustrialOven.OrderFood` (removes from another list), `GnubbField`, `LogicSkill`, `PieMenu`, `CAPCCMAdvanced`
  (1-based loops, fine), `CASMakeup` (fills the tail from Length to the array size, fine), `Bathtub` and `EditTown*` FIRSTEXIT (ILSpy goto/switch state machines, not real loops) [verified, decompiled].

## 5. Own-code fix: suatgradebonus never applied

`Fixes.cs` had `ApplySuatGradeBonus();` inside the comment of the line before it (`ApplyPetHarvestVfx(); // docs/593 H1
B13 §17            ApplySuatGradeBonus(); ...`, a merge join from 04969a5b). So docs/587 EP1 §2 (`suatgradebonus`) never
patched `UniversityWelcomeKit.TakeAptitudeTest.GetSUATScore`, and its Check could not reach FIXED (EpLive swaps 0/1).
The call is now on its own line. It needs a new off/on pair, which is in s73-pairs.

## 6. Gates and package

`build-fixes.ts --on kHitchCauseHooks` built and installed nothing. These pass: refs-resolve, check-live-map (527
hooks), fix-repro-check (783 switches), reflect-members-check, swap-stack-audit (with --self-check), and proven-fixes
--self-check. For proven-fixes, the s72 commit had left 11 proven switches in the excluded block too; they were removed
there. `selftest-all`: the failures that are left are the existing Windows-only csc runners (`tools/selftest/*/run.ts`,
layout-map, rewind-census, stove-repeat-core, night-run, machine-load), and they fail the same way without these
changes. Package `~/dev/ts3-runs/pkg/ts3fixes.r63.package` (kHitchCauseHooks variant). Pairs: `pushwaitskip
PushWaitSkipCheck`, `suatgradebonus SuatGradeBonusCheck`.
