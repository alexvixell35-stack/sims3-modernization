# 630. Dormant residential lots: probe and design

Status (2026-11-17): **`dormantprobe` built, default off, not run.** `dormantlots` **not built**: §5 shows it cannot be exact, and the probe has not yet shown a gain worth a realistic-only switch. Slots 3620-3659 (`fixes-3620`). Package `ts3fixes.r82-dormant.package` (probe only).

Goal: make the open world lighter, like a half-closed world, while the town still feels alive. A residential lot "goes dormant" when nobody the player controls is on it, no visitor is on it, and nothing on it is seen. While dormant, residents skip routed autonomy and the lot's objects stop ticking. The lot wakes when the camera nears, a visitor or player Sim arrives, or a resident has to leave.

Sources: decompiled 1.67 assemblies in `game-copy/decompiled` (grep), IL from `ilspycmd -il` on `game-copy/assemblies/Sims3GameplaySystems.dll`. EA code is quoted only by member name and IL offset.

## 1. What EA already does for an unseen residential lot [verified]

Most of "dormant" already exists in EA's own low-LOD layer. That matters, because only the remainder can be gained.

| EA rule | Where | Effect on a lot the player does not see |
|---|---|---|
| Lot sleep | `Lot.ShouldSimulate`: false for any lot that is not `LotManager.ActiveLot`, not the world lot and not the active Sim's lot, unless it is commercial; `Lot.Simulate` runs `UpdateReactions(full)` only when it is true | Residential lots away from the active Sim already skip full lot simulation (docs/243 §2.2 finding 2). |
| Sim LOD | `Autonomy.IsSufficientlyOnScreenForHighLODSimulation` (re-checked every 60 ticks): high LOD for the active household, the active lot, the active Sim's lot, hosted guests, daycare, resort workers, or an outdoor Sim on screen | Every resident of an unseen NPC lot is low LOD. |
| Low-LOD SimUpdate | `SimUpdate.Simulate`, the `else` branch of `IsRunningHighLODSimulation` | No autonomy update, no environment score, no curfew, no look-ats. An NPC that is not routing, indoors, on a residential lot that is not the active household's is made invisible (`SetOpacity(0)`, `WasHiddenForLowAutonomy`), and made visible again on the first high-LOD pass. EA's own "dormant look". |
| Needs off screen | `Motive.Update(float, float, Sim)` IL_000e-IL_0037: `!get_IsRunningHighLODSimulation()` (or a gap over `kMaxUpdateHoursBeforeAutoSatisfy` = 1 h) calls `Motive::AutoSatisfyMotive`, which reads `MotiveTuning.AutoSatisfyCurve` by time of day (+ a `RandomUtil.GetFloat(kMaxMotiveTimeDelta)` offset on first use) | Low-LOD motives follow a daily curve, not decay: no starving, no build-up. Same rule as rabbit-hole Sims, who are hidden and therefore low LOD. Some rabbit-hole interactions also freeze decay (`RabbitHole.GetCaught.InRabbitHole`: `Motives.FreezeDecayEverythingExcept()`, `Cleanup`: `RestoreDecays()`); `WorkInRabbitHole.InRabbitHole` uses commodity updates only. |
| Motive refresh | `Autonomy.RunAutonomy(bool)`: when not high LOD, calls `Update()` before scoring | A low-LOD Sim's motives, buffs, skills and relationships catch up once per autonomy search, with the gap as a parameter (docs/245). |
| Low-LOD interaction length | `Interaction.DoLoop`: an autonomous loop of a low-LOD Sim ends after `kMaxTimeForLowLODInteractionDuringTheDay` (420 sim-min) or `...DuringTheNight` (120) | Unseen residents already run long, cheap loops. |

So the parts of the idea that are "lot objects stop ticking" and "needs handled off screen" are largely EA's today. What is left on an unseen residential lot [docs/243 §2.2, measured]: interaction steps, the route plans and SACS work they start, the autonomy searches that pick them, `UpdateReactions` passes while Sims sit in broadcasters, and object alarms (alarms are global: `AlarmManager.Global`, not gated by `ShouldSimulate`).

## 2. Step 1, the probe: `dormantprobe` (built, default off)

`mods/ts3fixes/src/DormantProbe.cs`. Log and counters only; with the switch off every site is one `SwOn` slot read.

Candidate lot (cached per lot per sim tick): residential, a household that is not the active one, not the active lot, not `Household.ActiveHouseholdLot`, `UfLotSeen` false (camera lot, active-household lot, lot sphere near the viewers), and every Sim on it (`Lot.GetAllActors`) is a resident (`LotHome == lot`), not `IsPlayerSim`, not `UcOnScreen`, and `FarFromViewers`. That is TownieUnseen's camera part without its standing-posture test, since a dormant resident may sit or sleep.

| What | Hook (existing, reused) | Slots |
|---|---|---|
| Route plans and wall ms: all, unseen Sims, candidate residents on their home lot | `UfPlan` in the route hook (UnseenFallback.cs; same place as the `ufplan` / `planwait` lines) | 3624 all, 3620/3621 unseen, 3622/3623 dormant |
| Interaction starts: one per head interaction the queue ran | `AibHook.DeQueueHook` (EA calls `InteractionQueue.DeQueue` only at the four exits of `ProcessOneInteraction`) | 3627 all, 3625 unseen, 3626 dormant, 3638 dormant and autonomous |
| Broadcaster pulses | `BroadcasterPulseHook` (`ReactionBroadcaster.PulseSims`) by the object's lot | 3629 all, 3628 on candidates |
| Census at each sim-hour: candidate lots of NPC residential lots, residents present, objects, alarms owned by their Sims and objects (`AlarmManager.mGameObjectIndex` -> `mTimers`), fires per sim-hour from the repeat lengths (one-shots count 1) | the hourly alarm, set at world load next to `callcensus` | 3630 passes, 3631-3636 last census, 3637 errors |

**How to read it.** Turn `dormantprobe` on, play or soak at least 3 sim-hours. ts3native.log gets one line per sim-hour (the first hour is a baseline only):

```
dormantprobe: 1.00 sim-h, plans all P unseen U (ms) dormant D (ms), starts all S unseen u dormant d (autonomous a),
  pulses all B dormant b, census lots L of N npc-res, residents R, objects O, alarms A (F fires/sim-h), errors E, day, hour
```

Decision rule: the prize is `dormant ms / sim-h` of plans plus the dormant share of starts times the per-start cost (docs/243 §2.1: interaction step 96-197 ms/s, plans 11-272 ms/s on the sim thread). If dormant plans are under ~10% of unseen plan ms and dormant starts under ~10% of unseen starts, stop: the unseen work is in Sims away from home (on trips and community lots), which `unseenpark`, `unseenfb*` and the trip shortcuts already target. `alarms` / `fires` bound what "objects stop ticking" could add; `pulses dormant` bounds the reaction side (EA's `ShouldSimulate` already sleeps the rest).

Counter read-back: `ts3fixes` counters 3620-3638 through the usual census/`counters` handler.

## 3. "Alive" rules for a dormant resident

| Rule | How it holds while dormant |
|---|---|
| Ages | `AgingManager` alarms run on the `SimDescription`, not the lot: untouched. |
| Gets paid, works, goes to school | Work and school are alarm pushes (`Career.RegularWorkDayGoToWorkHandle` -> `InteractionQueue.Add(CreateWorkInteractionInstance())`; `School` sets the same handle). Any non-autonomous add to a dormant resident's queue wakes the lot (§4 W3); pay is computed by the career when the work interaction ends. |
| Socializes via story progression | EA story progression (`Sims3.Gameplay.StoryProgression` actions) works on `SimDescription`s and relationships, not routed autonomy: untouched. NRaas StoryProgression (`game-copy/nraas-src`, ideas only) likewise acts on descriptions and pushes interactions; a push wakes. |
| Has motives | Low-LOD auto-satisfy (§1) as for rabbit-hole Sims. A dormant Sim must still get `Autonomy.Update()` at EA's cadence or longer gaps take the 1-hour auto-satisfy path anyway (`kMaxUpdateHoursBeforeAutoSatisfy`), which is the same curve. |
| Outings at EA's rate | Outings to community lots come from meta-autonomy inside the same search (`Autonomy.ScoreInteractionsForMetaAutonomy` -> `ScoreInteractionsForMetaObject` over public venues and known lots; `ScoreMetaObject(daily random, current vs desired Sims)`). The rate is "searches per hour x chance a meta pick wins". To keep it, a dormant resident must keep searching at EA's cadence and only *local routed* picks may be replaced; a meta pick (and anything that leaves the lot) wakes the Sim and runs as EA's. |
| Plausible idle on wake | Do not freeze or move Sims: the cheapest dormant form keeps the current interaction running (EA's low-LOD loop, capped at 420/120 min) and only declines new routed local picks, so on wake the Sim is in its last real interaction or standing idle where it was; EA's first high-LOD pass restores opacity. |

## 4. Wake triggers (if built)

W1 camera: `UfLotSeen(lot)` or any resident `UcOnScreen` / `!FarFromViewers`, checked per tick (map-tag jumps and Edit Town included, as `IsSufficientlyOnScreen...` is). W2 arrival: any non-resident or `IsPlayerSim` actor on the lot (`Lot.GetAllActors`, or `Sim.OnLotChanged` hook). W3 must leave: a queue add at priority above Autonomous (work, school, phone invites, situations, NRaas pushes), a meta pick, or a resident's career/school alarm. W4 household change: move-in/out, death, birth, household split (story progression), lot sale or rebuild. W5 any reset of a resident (Overwatch, our watchdogs).

## 5. EA systems that would notice a dormant Sim

| System | What it would see | Handling |
|---|---|---|
| Motive failure deaths (`Motive` hunger/temperature kill paths) | nothing: auto-satisfy keeps values on the curve | none needed |
| Buffs, moodlets, traits | gap-based in `Autonomy.Update` | keep the update cadence |
| Careers, school, pay, promotions | the work push and the work day | W3 wakes; performance comes from the rabbit-hole work interaction, unchanged |
| Aging, birthdays, deaths of old age | alarms on the description | none |
| Pregnancy and birth (`HaveTheBaby`, `HaveBabyHospital` pushes) | queue push | W3 |
| Meta-autonomy outings | search cadence and pick | keep searches, wake on a meta pick (§3) |
| Phone / invites / NpcParty / visit situations / burglar / services | queue pushes, situation joins | W2/W3 |
| EA story progression, NRaas StoryProgression | descriptions; pushes | W3/W4 |
| Skills, relationships among residents, wishes | **change**: home socials and skill-building objects no longer start | not exact (§6) |
| Objects: cooking, fires from stoves, dirty dishes, spoilage, plants, laundry, puddles, bills | **change** where driven by residents' interactions; object alarms keep firing (global) | not exact; stopping alarms would change saves and is excluded (another dev owns save/load) |
| Reaction broadcasters, `Lot.UpdateReactions` | fewer pulses | already mostly off via `ShouldSimulate` |
| RNG (`RandomUtil` global stream) | every skipped search/interaction shifts every later random draw for everyone | makes "exact vs vanilla" impossible for any skip |
| Low-LOD hide (`WasHiddenForLowAutonomy`) | unchanged | none |
| Our watchdogs (idle check, Overwatch-style resets, `stuckwatch`) | a resident with an empty queue for hours | exempt dormant residents or keep their current interaction |
| Pets on the lot | pet autonomy, puddles | dormant only when pets follow the same rules; otherwise the lot stays awake |
| Apartments (`BuildableShells`), houseboats | several households / moving lot | excluded from candidates |

## 6. Verdict on exactness

**Not exact, and cannot be made exact.** Any skip of a resident's routed autonomy changes what that Sim does at home (skills, home socials, object state, fires) and, through the shared `RandomUtil` stream, every later random draw in town. The rules of §3 can keep the town looking alive (aging, pay, work, story progression, motives and outings at EA's rate), so a `dormantlots` would be a realistic-only, NPC-only feature (household exempt by `IsPlayerSim`, never `HouseholdToo`), not a proven-exact speedup. Its prize is bounded by what is left after EA's own low-LOD layer (§1): the dormant share of plans and interaction starts, which the probe measures.

Per the task's rule, `dormantlots` is not built. Slots 3639-3659 stay reserved for it. Next step: one soak with `dormantprobe` on (BP and IP, 3+ sim-hours each), then decide by the §2 rule. If it pays, the smallest design is "decline new local routed picks for dormant residents, keep searching, wake on meta picks and pushes" with a forcing Check that shows a dormant resident's routed pick declined (on) vs run (off).

## 7. Files

`mods/ts3fixes/src/DormantProbe.cs` (new); one-line call sites in `UnseenFallback.cs` (UfPlan), `AutonomyInUseBlock.cs` (DeQueueHook), `Fixes.cs` (BroadcasterPulseHook, switch default, slot comment), `UnseenTeleportGoal.cs` (world-load alarm).
