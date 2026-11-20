# 681: Firefighter locked out of the emergency lot (`firegreet`)

2026-11-20 (UTC). Player report: "the lot that has the fire doesn't open so i cant go inside to put out fire or rescue sims /
i had to click on the door to visit the household". A second report from the same emergency: the firefighter carried a
crying toddler outside the main door and kept trying to bring the child back in.

## 1. EA's greeting rules [verified, decompiled]

- A household's greeting is one saved list of lots (`Household.mGreetedLots`; `AddGreetedLotToHousehold`,
  `IsGreetedOnLot`). One greeted family Sim greets the whole family on that lot. It survives save and load.
- `Sim.UpdateVisibleLotsForActiveHousehold` / `UngreetLotIfNoOneIsInside` (via `RemoveGreetedLotForSim`) drop the lot from
  the list as soon as no Sim of the household is left inside it. This applies to every residential lot, not only apartments
  (`GreetSimOnLot` only adds a HUD refresh for apartments).
- The firefighter is greeted for an emergency in three places only: the Respond to Emergency interaction (`Target`),
  `FireFightingJob` setup for large fires and earthquakes (`Sim.ActiveActor`, the selected Sim), and
  `FindIsolatedFirePosition` (small fires, when a fire is placed).

So the doors lock when the firefighter arrives by another path (walks or drives there, a save loaded mid-emergency), or
steps out once (a child carried out, a hose or a back door) while no other family Sim is inside: the greeting is gone for
the rest of the emergency. "Visit household" works because it greets again.

## 2. Is it ours?

Not found to be. The three door caches cannot hold a door shut after a greeting: DoorShare and DoorMemoX store only
"unlocked" answers (a "locked" answer is never kept), DoorMemoX does not serve without `doorgen` armed and never for household
Sims, and `[RouteAsync] DoorMemo` lives inside one route plan. `doorgen` also bumps the door generation on every greet and
ungreet. `fireactivejob` and `firetrapcount` do not touch greetings. Not ruled out: other mods in the player's set.

## 3. Fix (`firegreet`, ts3fixes `FireGreet.cs`, default off)

Once a sim-minute (a NeverPersisted alarm made at world load; the switch is read on each tick), while
`ActiveFireFighter.sActiveJob` is set up (`HasBeenSetup`): its firefighter (`Occupation.OwnerDescription.CreatedSim`) is
greeted with EA's own `Sim.GreetSimOnLot(lot)` when in the world, not greeted, on the job lot or within 8 m of its edge,
and the lot is not their home. Nothing happens outside an emergency. EA bug fix for a household Sim (CLAUDE.md: EA bug fixes
stay); the result is the one Respond to Emergency gives.

Counters: [4248] greetings given, [4249] errors. Repro `fire-greet-lockout` (`FireGreetCheck run`: the decision for the
reported case with controls; off BROKEN, on FIXED; prints the live emergency when one is on).

Not covered: why the firefighter kept carrying the toddler (the child rescue task or autonomy); needs the player's save.
Not run in game yet with a live emergency.

## 4. The same pattern elsewhere [verified, decompiled; not built]

- A second ungreet path: EA's `GoOutside` (Sims3.Gameplay.Core) calls `UngreetLotIfNoOneIsInside` once the Sim is outside,
  so stepping out of the front door is enough (no lot change needed). `firegreet` covers it (on the lot or near it).
- The other active careers greet once per trip: private eye (`SimArrivedAtLot`), ghost hunter (before arrival), interior
  designer (renovation visit `Run`), daycare transport situations. Each trip greets again, so they are only exposed when the
  Sim steps outside mid-job with no family Sim inside. Not reported by players: documented, not fixed (confirmed bugs only).
- `FireFightingJob` large-fire / earthquake setup greets `Sim.ActiveActor`. Because a greeting is per household, this only
  matters when the selected Sim is not of the firefighter's household (the only `ActiveActor` greeting in the game).
