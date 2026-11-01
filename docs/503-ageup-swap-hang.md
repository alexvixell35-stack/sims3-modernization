# 503. An age-up that never finishes when EA refuses the outfit swap (`ageupswap`)

A static scan of the script DLLs for wait loops (2026-11-01) found 34 loops that only sleep and re-read one flag. Most
are safe because something always sets that flag. One is not: the wait at the end of an age-up model rebuild. When the
outfit swap it asks for is refused, nothing ever sets its flag, so that age-up never finishes and the age-up queue stops
behind it. This doc explains when that happens and describes the ts3fixes switch that fixes it. The switch is off by
default. Status (2026-11-01): built and self-tested; four game runs could not reach the hang (§6), so the in-game repro is open. Counters 2006-2007 (registry `fixes-2006`).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or in the game's tuning;
**[inferred]** = our reading, not yet checked in game; **[community-reported]** = from forum threads.

## 1. The bug

- `SimBuilderTask` is the task that rebuilds a Sim's model and outfits at an age-up. `AgingState.StartSimBuilderTask`
  creates it (the `AgeUp` interaction calls that), and `AgingState.PreInstantiateSim` waits on it. Its last step, `FinishModelLoad`, first
  waits for the new model, then asks `Sim.SwitchToOutfitDelayed(category, callback)` to put the Sim back into the outfit
  category it wore (or Everyday, which one branch of the age-up outfit rebuild picks). It then sleeps one tick at a time until
  that callback has run. The callback only sets the task's "outfit loaded" flag. **[verified]**
- `FinishModelLoad` drops the bool that `SwitchToOutfitDelayed` returns. When it returns false, no swap was started, the
  callback never runs, and the loop has no other way out: no abort check, no timeout. **[verified]**
- `SimBuilderTask.Simulate` sets `Done` and calls `AgingManager.StartAgingQueue` only after `FinishModelLoad` returns.
  So a hung task never sets `Done`. **[verified]**
- `Simulate` rebuilds the model, and calls `FinishModelLoad`, only when the outfit rebuild reports a match: the Sim is
  in the world and visibly wears the outfit the task started from, which is its Naked[0] outfit (Everyday[0] when it
  has no Naked outfit). Otherwise the task finishes without a model rebuild and without the wait. **[verified]** So the
  hang needs an age-up during which the Sim is shown in that outfit when the task starts **[inferred: the visible age-up
  sequence]**.
- Effects: `AgingQueue` serves its sorted head (the active household first) and sleeps until that head is `Done` or
  `Failed`, so later age-ups of the same or lower rank wait too. `AgingState.PreInstantiateSim` also waits on `Done`.
  **[verified]** What the player sees is a Sim whose age-up never ends, and other Sims that stop aging that session
  **[inferred]**. Forum threads about Sims that do not age name broken outfits as one cause and suggest NRaas tools to
  age them up **[community-reported]**.

## 2. When `SwitchToOutfitDelayed` refuses

It returns false without starting a swap in four cases **[verified]**:

1. Outerwear without Seasons installed.
2. The category is not allowed for the Sim's age: `Sim.AcceptableClothingCategoryForAge` looks the category up in
   `Sim.sOutfitAgeRules` and returns false when the category is missing from it. The rules come from the `Sim` class
   tuning: Athletic, Career, Swimwear, Singed and Makeover are child and up; Formalwear, Sleepwear, Supernatural and
   Outerwear are toddler and up; MartialArts and SkinnyDippingTowel are teen and up; Firefighting is young adult and up;
   Everyday and Naked are every age; the horse categories are horses only. Special, Massage and AuxCareer have no rule
   at all, so they are refused for everyone. **[verified]**
3. The Sim has no valid outfit in that category at index 0, and the category is Singed or above (Career, Makeover,
   Outerwear, Supernatural and the others with a value of 64 or more). For the primary categories below 64, EA first
   builds one from Everyday. **[verified]**
4. Building that outfit from Everyday fails. **[verified]**

The trait test it also runs passes for this call (reason 0). **[verified]**

So the hang needs a Sim that ages while wearing a category from case 2 (Special or Massage, for example) or case 3 (for
example Career or Outerwear when the rebuild made no outfit in that category for the new age) **[inferred: the rules
only grow with age, so case 2 needs a category with no rule]**. How often normal play hits it is not known; the repro
(§5) forces it.

## 3. The fix (`ageupswap`, off by default)

The switch hooks the two-argument `Sim.SwitchToOutfitDelayed(category, callback)`. `FinishModelLoad` calls this
overload, and so do two mermaid paths. EA's method runs first, unchanged. Only when it returns false and the callback
belongs to a `SimBuilderTask` does the hook act (`AgeUpSwapCore.Next`):

- It asks EA again for Everyday. Every age may wear Everyday. If EA starts that swap, the task finishes the normal way
  with the Sim in Everyday. Counter 2006 (`ageupswap_everyday`).
- If Everyday is refused too, it runs the callback at once and returns true. The task leaves its wait, sets `Done` and
  restarts the aging queue. The Sim keeps the outfit its new model came with **[inferred]**. Counter 2007
  (`ageupswap_released`).

Each case writes one line to the ts3fixes log, with the Sim and the refused category.

Why this is safe **[verified unless marked]**:

- A swap EA accepts is returned as is; the hook adds one bool test to that path.
- Callers that are not a `SimBuilderTask` (the mermaid paths) get EA's result unchanged.
- The callback only sets the flag the task waits on, so running it directly cannot run any other code early.
- With the switch off, the hook returns EA's result. The only difference is one switch lookup per refused swap.
- Everyday is what EA itself picks in one branch of the age-up outfit rebuild, so a Sim in Everyday after an age-up is
  normal **[inferred]**.

Not changed: the mermaid callers ignore the result in the same way. `RemoveMermaidWithVortex` waits in an interaction
`DoLoop`, not in a bare sleep loop, and `OccultMermaid.AddOutfits` does not wait at all **[verified]**. So a refused swap
cannot hang them like this; whether the `DoLoop` always ends is not checked **[inferred]**.

## 4. Prior art

NRaas (Chain-Reaction/NRaas, code search 2026-11-01) has no code for `FinishModelLoad`, `mbOutfitLoaded` or
`AgingQueue`. Overwatch's `CleanupAging` works on aging data, and the shared `AgeTransitionTask` cancels a Sim's
interactions before an age transition. Neither releases a builder task that waits forever. **[verified, search only]**

## 5. Test

- Self-test `tools/selftest/ageupswap` (PASS): a simulated `FinishModelLoad`. EA's stub refuses random categories. With
  the switch off, every refused swap for a builder task waits forever; with it on, every builder task finishes, refused
  swaps go to Everyday when Everyday is accepted, and other callers keep EA's result.
- Repro `ageup-swap-check` (Baseline-BP; agent check `Fixes.AgeUpSwapCheck`). The harness never saves. The check picks a
  toddler, child or teen that it has not aged yet (other households first, since an active-household age-up can open
  the modal trait dialog) and ages it with
  `AgingManager.AgeTransitionWithoutCake`. In "special" mode a watcher sets the task's post-swap category to Special
  until the model has loaded, which is the state a Sim wearing a Special outfit would be in. Since run 2 the watcher
  first keeps the Sim in its Naked[0] outfit until the task is seen, so the task rebuilds the model and runs
  `FinishModelLoad` (§1), and every part sets its own post-swap category. Parts:
  1. Switch off, special: the task's model loads, the outfit never does, and `Done` stays false (the bug). "unstick" then
     sets the flag, and the task finishes.
  2. Switch on, special: the task finishes, counter 2006 or 2007 goes up by one.
  3. Switch on, everyday (control): the task finishes, counters unchanged.
  4. Switch on, outerwear (if a Sim with an Outerwear outfit is in the world): post-swap Outerwear, case 3 of §2. EA
     accepts it if the rebuild made an Outerwear outfit for the new age; otherwise the hook moves it to Everyday.
- Confirms I-503-1: part 1 hangs and part 2 finishes. Refutes it: part 1 finishes by itself.
- Queue `tools/harness/queues/ageup-swap.json`.

## 6. Results

**Run 1** (2026-11-01, Baseline-BP, job `bp-ageup-swap` PASS in 17.1 min): **inconclusive**. Four age-ups (three teens
and a child, one per part) all finished. In each, the builder task was seen and set `Done` with
model loaded False and outfit loaded False. So `FinishModelLoad` never ran (§1, last point): none of these Sims wore
its Naked outfit when the task started. The off part could not hang, and the switch had nothing to do (2006 and 2007
stayed 0). **[verified, log]**

Repro change for run 2: the watcher keeps the Sim in Naked[0] until the age-up task is seen, then sets the post-swap
category to the part's category (Special, Everyday, Outerwear) until the model has loaded.

**Run 2** (2026-11-01, same job, PASS in 17.2 min): **inconclusive** again. Each Sim wore Naked when the age-up
started, and the task was seen 200 to 1535 ticks later, but all four tasks finished with model loaded False. EA
compares the worn outfit with the old Naked[0] only after it has rebuilt the outfits for the new age, and by then the
Sims had changed (they ended in Everyday or Outerwear) **[inferred: the compare comes after the rebuild in
`UpdateSimOutfitsOnAgeUp`; the Sims' outfit at that moment was not logged]**. Repro change for run 3: the watcher keeps
the old Naked[0] outfit object (taken before the age-up, so a forced switch after the rebuild does not put on the new
age's Naked outfit) on the Sim until the model has loaded or the task is done, and logs each put-back.

**Run 3** (2026-11-01, PASS in 17.2 min): **inconclusive**. The old Naked[0] was put back 1 to 35 times per part,
mostly before the task was seen; one off-part age-up did not start within the part's 2 min. Every task that finished
again had model loaded False. A closer reading of the IL contradicts the runs: `UpdateSimOutfitsOnAgeUp` also sets its
out outfit when the Sim wears any outfit it rebuilds, and, failing that, falls back to a new outfit (with post-swap
Everyday) whenever the Sim is instantiated; `Simulate` then calls `FinishModelLoad`. So for an instantiated human Sim
`FinishModelLoad` should always run **[verified, IL]**, yet in the 11 age-ups that finished over three runs it never did (only its
callback sets model loaded, and it is never reset) **[verified, log]**. The cause is open: something in this setup
(another mod's aging code, or the task seen by the check not being the one that ran) changes the path **[open]**. The
live repro is parked until that is found; the self-test covers the hook.

**Run 4** (2026-11-01, PASS in 17.3 min, with simler90's original core instead of that player's own core, which has its
own aging patches): **inconclusive** in the same way. All four tasks (two young adults, two teens) finished with model
loaded False; one ended Failed True (the outerwear part). So the player's core mod was not what changed the path. Over
four runs, 15 age-ups on Baseline-BP finished and none ran `FinishModelLoad` **[verified, log]**. The cause is still open
**[open]**.

The repro steps now assert their outcome: `expect stuck` (off, special) throws unless the task was seen with model loaded
True, outfit loaded False and Done False; `expect released` (on, special) throws unless the task finished and [2006] +
[2007] went up; `expect control` (on, everyday) throws unless it finished with both unchanged; `expect done` (on,
outerwear) throws unless it finished. With these, runs 1-4 would have failed at `expect stuck`, which is the honest result:
the live repro does not reach the bug yet. Cleanup sets the missing flag (`unstick`), so a task that did hang finishes.

A second way to hang **[inferred]**: `FinishModelLoad` starts the model swap with `SetModelInGameObjectDelayed`, whose
callback goes into the same one-per-Sim table as outfit swaps (docs/224 §5, I-414-8). If the Sim still has a swap
callback registered (an outfit swap that nobody completed, aborted or called back), the model callback is not stored,
the model-loaded flag is never set and the task waits forever. The switch here does not cover that case; the drop
logger of docs/224 counts it.
