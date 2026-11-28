# 266. "Move In With" wish for a Sim already in the household (stale mini home lot)

2026-11-14. Player report (Discord, twopointjj, second time): "my sim wished to move in with his wife but he is already
living together with her". Setup: v0.2.12, NRaas StoryProgression, Overwatch, ErrorTrap, StingTweaks, AnnoyancesDisabler,
Tiny UI Fix, ExpandedGenealogy. Switch `minihomelot` (ts3fixes `MiniHomeLot.cs`, EA bug fix, on by default: small fix +
one repro). Counters 991-993.

## 1. Existing fixes

- Our docs: docs/545 wish fixes are listener leaks (`dnp*`, `dreamrootorphan`); nothing on this wish. No `MoveIn` switch in
  ts3fixes.
- NRaas: Traveler (`TravelerSpace/States/LiveModeStateEx.cs`, back home from a trip) sets every resident's
  `MiniSimDescription.LotHomeId` to its `LotHome.LotId`: the same repair, only on travel return and only with Traveler
  installed. The player has no Traveler. Overwatch and StoryProgression do not touch the field. [verified, NRaas source grep]
- Web: the symptom is a known glitch (Carl's guide forum, "Married and Not 'Moved In'",
  https://www.carls-sims-4-guide.com/forum/index.php?topic=14132.0) [community-reported]; the workaround given there, evict
  the Sim in Edit Town and place it in the spouse's home, goes through `Household.Add` -> `OnHouseholdChanged`, the one path
  that rewrites the mini (§2), which fits the cause [inferred]. No mod fixes the wish.

## 2. Cause [verified in the decompiled scripts]

- `DreamsAndPromisesDelegateFunctions.MoveInWithSimCheckFunction` (Sims3GameplayObjects): `kThrowAway` when
  `Subject.SimDescription.HasSameHomeLot(Owner.SimDescription)` or the subject is an NPC roommate, else `kBecomeActive`.
  The manager retests visible wishes every `kHoursBetweenRetestingVisibleNodes` (2 sim-hours, `RetestNode`), so a wrong
  wish would be dropped if the check answered right.
- `SimDescription.HasSameHomeLot` compares `HomeWorld` and `LotHomeId`. `SimDescription.LotHomeId` returns
  `MiniSimDescription.Find(id).LotHomeId` whenever a mini exists, before `LotHome.LotId`. Every Sim of the world gets a mini at
  the first trip (`MiniSimDescription.AddMiniSims`), and minis are saved.
- The mini's `mHomeLotId` is written only in its constructor and in `SimDescription.OnHouseholdChanged` (joining a household,
  `bUpdateHome: true`; the `false` branch writes `LotHomeId` back to itself, a no-op). `SetMemberFields` (travel refresh) does
  not touch it. `Household.SetLot` (from `Lot.MoveIn`: the household moves to another lot) never updates the members' minis.
- So after a household move the members keep the old lot in their minis, while a Sim that joins later gets the new lot.
  Spouse in the moved household + Sim that moves in afterwards: `HasSameHomeLot` = false, the wish is offered and kept.
  Every other `HasSameHomeLot` test (`SimLivesInMyHouseholdRightNowCheckFunction`, ...) is wrong the same way.

## 3. Mod involvement

None of his mods causes it. NRaas StoryProgression's `MoveInLotScenario` moves households with EA's own `Lot.MoveIn`, so
with StoryProgression households move far more often and the stale state is common, but the bug is EA's
(`Household.SetLot`). No upstream PR: StoryProgression uses EA's API as intended; Traveler already carries the repair.

## 4. Fix (`minihomelot`)

- After EA's `Household.SetLot(Lot)`: each member's mini home lot is set to the household's lot, the value EA's own
  `OnHouseholdChanged` writes on a join (counter 991).
- World load (Instantiator `OnWorldLoadFinished`): every household's members are repaired the same way; stale minis already
  in a save are fixed, and EA's 2-hour retest then drops a wrong visible wish by itself (counter 992).
- Only at home: not while changing worlds, only Sims whose `HomeWorld` is the current world, only real lots (homeless
  households keep EA's value). Rule in `MiniHomeLotCore.ShouldSync`, self-check `tools/selftest/minihomelot`.
- Household Sims: affected, as an EA bug fix (their `HasSameHomeLot` answers become the ones without the stale field). No mod
  data in saves: the field is EA's and holds EA's own value. Off = EA unchanged.
- Swap table: `Household.SetLot` can yield (`Sim.OnHomeLotChanged` > ... > `StateMachineClient.RequestState`), stub remap
  as every other swap (docs/242, `docs/data/swap-stack-audit.csv`).

## 5. Repro `minihomelot-movein-check` (queue `minihomelot-1114.json`, Baseline-BP)

Active household's first married pair (else two humans teen or older); B's mini home lot set to another lot (a mini is
created if B has none). Off: `expect apart` after pick and after EA's `SetLot` (the bug). On: apart after pick, `expect same`
after `SetLot` (the moved-in-later path) and after the world-load repair. Counters `minihomelot_setlot >= 1`,
`minihomelot_load >= 1`. Result: PASS 2026-11-14 12:12 UTC, job `bp-minihomelot-check` (docs/data/results/2026-11-14-queue-minihomelot-1114.md). Stale mini lot: `HasSameHomeLot` false (apart); after the `SetLot` hook both members hold the household lot, so the `MoveInWithSim` check throws the wish away; the world-load repair fixes a stale mini. Counters `minihomelot_setlot >= 1`, `minihomelot_load >= 1`, `minihomelot_apart >= 3`. Verdict: default ON (small fix + correctness + one repro).
