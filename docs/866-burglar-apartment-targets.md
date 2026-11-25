# 866: The burglar picks loot no Sim can reach on apartment lots, `burglarapt`

2026-11-22 (UTC), static (EA code read in a local decompile of `Sims3GameplaySystems.dll`, not committed). Fix built,
default off. Decision check passed in game (pass-11 contributor beta); no live burglary yet.

## 1. Question

On a Late Night apartment lot (`Lot.HasVirtualResidentialSlots`) the household lives in one unit; the other units are
hidden rooms of virtual neighbours, and hallways, lobby and outdoors are public rooms. Which objects can the burglar pick,
what happens when it picks one it cannot reach, and can that stall the burglar?

## 2. Findings [verified, decompiled]

`BurglarSituation+Stealing` (the burglar's main state):

| Step | What |
|---|---|
| `Init` | after the police / sleeper / occult checks: `mObjectToSteal = FindNextObjectToSteal()`, then `Situation.ForceSituationSpecificInteraction(obj, worker, obj.GetStealInteraction(), null, StealSuccess, StealFailed, NonCriticalNPCBehavior)`; nothing found: `CheckIfQuotaMet` |
| `FindNextObjectToSteal` | `FindObjectToSteal(false)`; nothing and nothing stolen yet: `FindObjectToSteal(true)` (the cheapest) |
| `FindObjectToSteal` | stops at `Burglar.MaxNumberOfObjectsToSteal` or a met quota; `Lot.GetObjects<GameObject>(CanBeStolen)`, sorted by `Value` descending; the first one that fits the quota (or the last one for "cheapest") |
| `CanBeStolen` (private) | `Value > 0`, not `InUse`, `IsStealable()`, not in `Parent.IgnoreObjects`, `InWorld`, `LotCurrent == Parent.Lot`, `GetContainmentSlots()` not null and nothing contained. **No room test.** |
| `Steal+Definition.Test` | refuses `InUse`, not stealable, and `IsInPublicResidentialRoom` |
| `Steal.Run` | disables the target's interactions, plans a route into the target's room (`PlanToPointRadius`, `DoRouteFail = false`); route failed: returns false |
| `StealFailed` | unless the burglar has `ExitReason.Canceled` / `HigherPriorityNext`: adds the object to `IgnoreObjects`; then `CheckIfQuotaMet` |
| `CheckIfQuotaMet` | a 1 sim-minute alarm to `StealAgain` -> `LeaveOrStealMore`: another object -> a new `Stealing` state, none -> `LeaveSuccessful` |

The room rules:

- `GameObject.IsInHiddenResidentialRoom` / `IsInPublicResidentialRoom`: on a lot with `HasVirtualResidentialSlots`,
  `Lot.IsRoomHidden(RoomId)` / `Lot.IsRoomPublic(RoomId)`; `IsRoomPublic` also answers yes for the outside room
  (`LotManager.RoomIdIsOutside`), so outdoor objects of an apartment lot count as public.
- `CommonDoor.IsAllowedThrough` answers `DoorRestriction` for any door touching a hidden room, before the service
  ignore-lock, stray, one-way and lock-type rules (docs/552). So no Sim, the burglar included, routes into a hidden unit
  through a door. [inferred] Other portal kinds (arches as `NonAnimatedDoor`, stairs, elevators) do not test hidden rooms;
  EA's apartment units are closed by doors, so this is taken as unreachable.
- EA's own filter for the same case: `GameObject.CanBeRepossessed` refuses `IsInPublicResidentialRoom` and
  `IsInHiddenResidentialRoom` (the repo man never takes them).

So on an apartment lot `CanBeStolen` accepts valuables in the neighbours' units and in public rooms, and the burglar tries
them most valuable first:

1. Hidden unit: the queue test passes, `Steal.Run` fails its route plan, `StealFailed` ignores the object.
2. Public room: the queue's insert test (`Steal+Definition.Test`) refuses it, and `InteractionQueue.InsertInteraction`
   calls `StealFailed` at once, inside `Init`.

Each such object costs one `Stealing` state, a sim-minute of standing on the lot (plus a route plan for a hidden one), with
no cap. A lot full of neighbour valuables keeps the burglar on the lot for that many sim-minutes before it either finds a
reachable item or leaves.

### Does it stall? No: `StealFailed` fires on every failure path [verified]

- Insert-time test failure: `InteractionQueue.AddNext` -> `AddInteraction` -> `InsertInteraction`: `entry.Test()` false ->
  `entry.CallCallbackOnFailure(actor)` -> `IopWithCallbacks.CallbackOnFailure` -> `StealFailed` (docs/568 §1 found the same).
- Test failure when the queue runs it: `RunTest` false -> `OnValidInteractionFailed` -> `DeQueue(false)` ->
  `RemoveInteraction` -> `CleanupInteraction(succeeded: false)` -> `CallCallbackOnFailure`.
- `Steal.Run` returning false (route failed): `DeQueue(false)` -> the same failure callback.
- Only exception: `RemoveInteraction` skips the cleanup (so the callback) while the burglar has
  `ExitReason.SuspensionRequested`. Not specific to this bug.

[inferred, not seen] The insert-time `StealFailed` runs inside `Init`; if the burglar still carries `Canceled` or
`HigherPriorityNext` bits then, the object is not added to `IgnoreObjects` and the next state picks it again. The fix
removes that object from the list, so this possible repeat goes with it.

## 3. Fix: `burglarapt` (ts3fixes `BurglarApt.cs`, `BurglarAptCore.cs`, tuning `kBurglarApt`, default off)

A swap on `Stealing.CanBeStolen` (`BaHook.StealHook`, stub `StealOrig`): EA's answer first; a yes for an object on a lot
with `HasVirtualResidentialSlots` that is `IsInHiddenResidentialRoom || IsInPublicResidentialRoom` becomes no
(`BaCore.Refuse`), the exact test of EA's `CanBeRepossessed`. With the switch off the hook keeps EA's answer and counts the
answers it would turn (2196). An exception keeps EA's answer (2198).

- Only targets nobody can steal are dropped: public-room objects already fail EA's `Steal` test, hidden-unit objects
  cannot be reached through a door. The household's own unit and every non-apartment lot are unchanged.
- This changes the burglar's choice on the household's own home: it is an EA bug fix (AGENTS.md household rule). No
  household Sim is touched; the burglar is a service NPC. [inferred] The visible effect: a burglar whose only reachable
  loot is gone leaves sooner (EA's `LeaveSuccessful`, as when nothing is left), instead of standing a sim-minute per
  neighbour valuable.
- Not a save change: nothing is stored; `IgnoreObjects` is EA's own list.
- Swap-stack audit: the call graph reaches a yield from `CanBeStolen` through virtual `InUse` overrides
  (`BBallHoop+BBallHoopBall.get_InUse` ...), so the row is `yes`, kept with the stub remap (SavedStacks.cs).
- [inferred] `Lot.GetObjects<GameObject>(CanBeStolen)` calls the method through a delegate; the swap replaces the method
  body, so the delegate runs the hook too. Counter 2196 (off) or 2195 (on) rising on a burglary of an apartment lot
  confirms it.

Larger or streamed worlds: per call, a room-id lookup on the object's own lot; nothing tied to world size.

## 4. Counters (contributor range, reserved by hand: free in source and registry here and in the open contributor work)

Per `CanBeStolen` answer, so one object counts once per search (a burglary runs several): [2195] answers turned to no,
[2196] would be turned to no with the switch off, [2197] spare, [2198] errors.

## 5. Check and repro

`Ts3Fixes.Fixes.BurglarAptCheck`: `run` puts two layouts (a valuable in a hidden neighbour unit, one in a public room of an
apartment lot) and controls (a house-lot object, a hidden room on a lot that is not an apartment (tomb), the household's
own-unit object, an object EA refuses) through `BaCore`: off `verdict: BROKEN`, on `verdict: FIXED` (UNEXPECTED if the
swap is not in). `scan`: every apartment lot of the world, the objects EA's `CanBeStolen` rules accept (without the
situation's ignore list) split into own unit / hidden / public, the top value in hidden units, and the room class of the
most valuable one (EA's first pick when the quota allows); read-only, no burglar needed. `result`: the counters. Repro
`burglar-apt`; offline selftest `tools/selftest/burglarapt`.

End to end (not done): Bridgeport, the household in an apartment, a burglar called at night (or the agent's service
push); off: 2196 rises and the burglar tries hidden-unit loot first (route fails, a sim-minute each); on: 2195 rises and its
first target is in the household's unit.

## 6. Not done / next

- In game (contributor beta pass 11, Bridgeport apartment save, 28 apartment lots): repro `burglar-apt` off `verdict: BROKEN`,
  on `verdict: FIXED`, 2198 = 0, live path ok. [verified] `scan`: 0 stealable objects in hidden units on every EA apartment
  lot of that save; 2-45 in public rooms or outdoors per lot (45 on the active household's home); on 6 of 28 lots EA's most
  valuable pick is a public-room object (up to 36000). So the case players hit is the public one (each public object ranked
  above the reachable loot costs a failed insert and a sim-minute); the hidden-unit case needs a player-built apartment.
- Still to do: the end-to-end burglary on an apartment.

## Import 2026-11-22 (maintainer)

PR #644, counters 2195-2198 kept. `burglarapt` is **ON by default** at import: in-game repro `burglar-apt` off BROKEN / on FIXED, errors 0, live path ok (Bridgeport apartment save, 28 lots). [verified in his run] No live burglary was run. Household rule: the burglar is a service NPC; the fix only drops loot nobody can steal (EA bug fix on the household's own apartment lot).
