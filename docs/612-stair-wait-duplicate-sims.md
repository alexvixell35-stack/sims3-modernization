# 612: stair wait deadline, duplicate Sims (Sim.Dispose link)

2026-11-17, static, r70; both fixes proven in game by their force checks (§4). Two default-off ts3fixes switches in `mods/ts3fixes/src/StairDup82.cs`. RE with tools
only: `ilspycmd -il` on `game-copy/assemblies/Sims3GameplaySystems.dll` (`Stairs+StairsPortalComponent`,
`ElevatorInterior+ElevatorPortalComponent`, `Sim`), grep over the decompiled tree for every `CreatedSim = ...` write, every
`new SimInitParameters` and every `SetSimDescription` caller, NRaas ErrorTrap / Overwatch source (`game-copy/nraas-src`) as the
map of known duplicate causes.

| switch | slots | hook (swap) | Check / repro |
|---|---|---|---|
| `stairwaitend` | 3446-3449 | `Stairs+StairsPortalComponent.OnPortalStart(Sim)` (SwePortalHook) | `StairWaitEndCheck`, `stairwaitend-check` |
| `simdisposelink` | 3450-3452 | `Sim.Dispose()` (SdlDisposeHook) | `SimDisposeLinkCheck`, `simdisposelink-check` |

## 1. Sources

- Stair lead: docs/610 §4 leads (PR #524; semgrep `sleep-wait-no-exit`).
- Duplicate Sims: no doc covered it. NRaas map [community]: ErrorTrap treats a `Sim` whose `SimDescription.CreatedSim` is
  neither null nor itself as an orphan (DereferenceManager "immediate" rule), drops zero-reference `SimDescription`s
  (DereferenceSimDescription) and re-creates missing Sims (`CheckRecovery`: description living in world with
  `CreatedSim == null`). Overwatch has no separate duplicate-Sim pass.
- Census: the ~/dev/ts3-runs phase-* logs (246 runs) have no ErrorTrap Sim or SimDescription correction line (`Out of World
  Object Found`, `SimDescription Removed`, `Destroyed: ...Sim`): 0 hits. The saves are read-only and were not parsed;
  `SimDisposeLinkCheck run` prints an in-game census instead (unbound live Sims, descriptions with two Sim objects,
  SimDescriptionIds held by two descriptions).

## 2. stairwaitend

[verified, IL] `StairsPortalComponent.OnPortalStart`: after `Route.StartStairTraversal` returns Success the Sim's task puts an
`ActiveUserEntry` in `mSleepingTasks` and loops `Sleep(10)` while `mbSleeping`. Only `HandleStairsCompleted` (the native
`OnStairsCompleted` callback) and `WakeAll` (OnLoad) clear it: no deadline. The method stores
`SimClock.ElapsedTime(Minutes)` in local V_6 (IL_00df/IL_00e4) and never loads V_6 again: an unfinished deadline.
[verified, IL] The sibling `ElevatorPortalComponent.OnPortalStart` (same ActiveUserEntry / mbSleeping design) ends its wait
when `ElapsedTime(Minutes, mFailsafeTime) >= 30` (`ldc.r4 30`) with `SetObjectToReset` and returns false; `Ladder` waits in
line with `kMaxWaitTime = 30`.

Fix (switch on): each stair wait arms one 30 sim-minute NeverPersisted alarm, removed when OnPortalStart returns. If the Sim
is still asleep in that stair's list when it fires, the entry ends as `HandleStairsCompleted(false)` ends it (mbResult
false, mbSleeping false) and the Sim gets the elevator's failsafe (`SetObjectToReset`). Household, selected, selectable and
unknown-scope Sims keep EA's wait (`HouseholdExempt`, counted in 3448). No stuck-on-stairs case is in the logs or
`docs/data/stuck-log.md`, so this is a guard on an EA design gap, not a seen bug.

Hooked method re-read in full [verified, decompiled]: the wait loop is the no-exit loop fixed here; its inner `RemoveAt`
filter is O(n^2) on Sims within 0.6 m (a handful), left as EA's; it allocates a new list every 10 ticks (EA's; our hook
calls EA's body unchanged).

Counters: [3446] waits ended, [3447] past 30 min with the switch turned off since arming, [3448] past 30 min kept by the
household rule, [3449] errors.

## 3. simdisposelink (duplicate Sims)

Where EA makes a second Sim object [verified, decompiled + IL]:

- `SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)`: every Instantiate path (lot, off-screen, virtual home,
  service, visits, travel return `GameStates` EndVacation `Instantiate(Vector3.OutOfWorld)`) funnels here; its only guard is
  `CreatedSim != null` -> return it.
- `Sim.OnCreation` / `Sim.OnLoad` -> `SetSimDescription` -> `CreatedSim = this`: every Sim object rebinds its description,
  last one wins.
- `Sim.get_HasBeenDestroyed` returns true when `mSimDescription.CreatedSim != this`: EA's own rule is that the bound Sim is
  the Sim and any other Sim object of the description is destroyed.
- `Sim.Dispose` IL_0013..IL_001a: `mSimDescription.CreatedSim = null` with no check that it is bound to this Sim.
- Other `new SimInitParameters(description)` users do not bind a real description: `PieMenuSimHead` clones it
  (`new SimDescription(activeActor.SimDescription)`) and `CASGeneticsPreviewSim` is a GameObject, not a Sim.
- `CreatedSim = null` writes in `SimDescription.Dispose`, `FutureDescendantService`, `GameStates` aged-up travel fixup and
  `Sim.OnStartup` (bad outfit) destroy their own bound Sim first: no duplicate.

Cause [verified chain; how a save first gets two Sim objects for one description is inferred]: when an unbound Sim (a second
saved Sim object of a description, or one any cleanup destroys) is disposed, EA unbinds the live Sim: the household gets
`kSimRemoved` for the live Sim (`Household.OnMemberChanged`), the live Sim reads `HasBeenDestroyed == true` while in the
world, and the next Instantiate of the description creates a second live Sim object. This is also why NRaas only deletes an
orphan "immediately" when `CreatedSim` is null or itself.

Fix (switch on, as EA's HasBeenDestroyed rule intends): Dispose of a Sim whose description is bound to another live Sim
(same description, valid id, proxy present) clears the raw `mSim` field before EA's body (EA's set becomes a no-op, so no
household event for the live Sim) and sets it back to the bound Sim after. A bound Sim, or a description bound to nobody
or to a gone Sim, runs EA's Dispose unchanged. No household exemption: the household keeps its live member, EA's intended
result. Not ported: NRaas's blanket orphan cleanup.

Hooked method re-read in full [verified, decompiled]: loops are copies (buff list, situations, alarm clocks) or a pass over
all lots: none without exit, none O(n^2). Leads: an unbound Sim's Dispose also runs `TraitListener.Remove`,
`Occupation.OnDispose` and `RemoveFacePaint` on the shared description (the live Sim's), left as EA.

Counters: [3450] links kept, [3451] unbound disposes with the switch off, [3452] errors.

## 4. Test

Force checks `stairwaitend StairWaitEndCheck` and `simdisposelink SimDisposeLinkCheck`, one load, package r70
(`ts3fixes-on-kHitchCauseHooks`): both switch off BROKEN, switch on FIXED, controls ok, members found, error counters 0.
- `stairwaitend`: live replay on a staircase: the wait is still asleep past 30 sim-minutes with the switch off and ends
  (result False) with it on; no real stair wait was asleep at the time (0 both ways).
- `simdisposelink`: live replay on a Sim: EA's path would unbind the live Sim with the switch off; with it on, EA's set is a
  no-op and the link is restored. Census both ways: 0 unbound live Sims, 0 descriptions with two Sim objects, 0 ids held
  by two descriptions.
