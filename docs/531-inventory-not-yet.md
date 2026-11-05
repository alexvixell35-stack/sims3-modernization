# 531. A Sim being created gets an object before it has an inventory (`invnotyet`)

Issue #166 build 13, the first of the six builds of the player's patched core that were left after the IL patcher
batches (docs/521, docs/526). `Sim.TryAddObjectToInventory(IGameObject)` reads the Sim's inventory with no null check,
and a Sim has no inventory yet while `Sim.OnCreation` runs. This doc describes the bug, the ts3fixes hook (switch
`invnotyet`, off by default; counter 2058, registry `fixes-2058`; ledger §935) and the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `Sim.TryAddObjectToInventory(IGameObject obj)` is `if (!base.Inventory.TryToAdd(obj)) { obj?.Destroy(); return false; }`
  then `return true` **[verified]**. `GameObject.Inventory` is `InventoryComp?.Inventory` **[verified]**: null until the
  Sim's `InventoryComponent` exists. simler90's UPDATE242 core keeps EA's body (the core patch was written against it).
- Seen in the player's game on 2026-10-19 (ErrorTrap, 6 reports in one minute, Isla Paradiso): NPC teens and children
  of a household with no home lot threw `NullReferenceException` at `TryAddObjectToInventory` IL_0007 while being
  created **[verified live]**. The stack: `Sim.OnCreation` -> `SimDescription.AssignSchool` ->
  `CareerManager.AcquireOccupation` -> `Career.SetStartingLevel` -> `Career.GiveCareerLevelSkills` ->
  `SkillManager.AddElement` -> `BandSkill.OnSkillAddition`.
- `BandSkill.OnSkillAddition` gives a non-selectable Sim, who is not travelling, the skill's free instrument through
  `CreatedSim.TryAddObjectToInventory(MedatorInstanceNameForNpcObject, ...)` **[verified]**.
- EA's own school levels give no skill. Career-metric mods do: SimsMx Career Metrics gives school levels a
  `MetricBestSkillFromList` metric whose skill is whatever it last named, so a new pupil can get level 1 of Guitar,
  Piano, Drums or BassGuitar (all `BandSkill`) **[verified in the player's career XML]**.
- The exception stops the creation half way: the Sim is left at 0,0,0 with no lot, and other Sims' routing then throws
  in `RoutingComponent.IsSimPushable` when it pushes that Sim **[verified live, 2026-10-19]**.

## 2. The fix

`mods/ts3fixes/src/InvNotYet.cs`, hook `InvNotYetHook.AddHook` swapped over `Sim.TryAddObjectToInventory(IGameObject)`
(`EaFixes.cs` patch list; the string overloads call this one). With the switch on and no inventory, it does what the
method already does when the inventory refuses the object: destroys the object (null-safe) and returns false. Every
caller already handles false **[inferred: the method's own refusal path returns false today]**. The Sim keeps the skill
and goes without the free instrument. With an inventory, or with the switch off, the original runs. Counter [2058]
`invnotyet_refused`.

## 3. Test

Repro `inv-not-yet-check`, queue `tools/harness/queues/inv-not-yet.json`, save Baseline-BP, **simler90's UPDATE242 core
without the player's patches** (the player's core carries build 13, so its off arm would not throw). The agent check
`Ts3Fixes.Fixes.InvNotYetCheck` sets the active Sim's `InventoryComponent.Inventory` to null for one synchronous call,
the state `OnCreation` is in, and restores it in a `finally` before anything else runs. A `SocialJigTwoPerson` made out
of world goes through `TryAddObjectToInventory`. Off arm (`expect broken`): EA's `NullReferenceException`. On arm
(`expect fixed`): false and the hook's `Destroy` (`invnotyet_refused` reaches 1); `expect gone`, a step later, finds the object gone. `Destroy` only drops the object's proxy after the call (`HasBeenDestroyed` is `Simulator.GetProxy(ObjectId) == null`), so the on arm cannot see it at once. The check fails if the Sim's
inventory does not come back. Early gate: the counter at 3 min.

Result, first run (2026-11-05, pure UPDATE242 core): the off arm passed (EA's `NullReferenceException`). The on arm's hook
ran (no exception, false, `invnotyet_refused` 1), but the check then read `HasBeenDestroyed` in the same call, which is
still false right after `Destroy`, and failed. The check now counts the hook's `Destroy` and looks for the object a step
later. Rerun (2026-11-05, same core): **PASS** in 11.4 min. Off: `NullReferenceException`, false, the object left. On: no
exception, false, the hook's `Destroy` ran (`invnotyet_refused` 1). `expect gone` 2 s later: the object is gone.

## 4. What is left

- A real Sim creation through the fixed path (the check calls the method directly). Needs a school career metric that
  gives a band skill, as SimsMx Career Metrics does; the harness has no step to build one.
