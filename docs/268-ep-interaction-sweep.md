# 268 EP/SP interaction sweep: force every pack interaction to run

2026-11-15, design + build, static (no game run). Rodney: "for those EP/SP interactions, can we force repro them so they occur? and add all the possible interactions in the test harness?" Follows docs/267: per pack 70-271 script types were never seen in our runs, 4-19 of them likely hot.

Markers: **[verified]** = read in `game-copy/decompiled` or in our code; **[inferred]** = reasoning, to confirm in the first run.

Built: ts3agent `interaction_sweep` (`mods/ts3agent/src/plugin/Handlers/InteractionSweepHandlers.cs`), `tools/harness/interaction-sweep-report.ts` (report + repros generator), repros `sweep-ep1` .. `sweep-sp9` (generated, the last block of `repros.json`), queues `sweep-packs-home-1115.json` and `sweep-packs-dest-1115.json` (unrun).

## 1. What "all possible interactions" means per pack

A pack's interactions are the `InteractionDefinition` classes nested in its script types (docs/data/ep-pack-types.csv `type`, 1,930 rows). The game offers them through one call: `GameObject.GetAllInteractionsForActor(actor)` [verified, GameObject.cs:4630]. It returns the object's own pairs, the actor's self-object pairs and opportunity pairs. On a Sim target (Sim.cs:57722) it also returns posture socials, every social from `SocialComponent.GetAllInteractionsForSim` (`SocialInteractionA.Definition` per ActionKey), Join / Ask to Join, and pet-initiated socials. So the sweep covers five target kinds, and each kind brings its own classes of definitions:

| Kind | Target | Which definitions |
|---|---|---|
| object | each concrete pack object class (CSV type or a subclass): an in-world instance, else one bought from the catalogue and placed on the far community lot | all of them |
| rabbithole / world | one instance of every other concrete object class in the world, rabbit holes included | pack definitions only (outer declaring type in the pack's type list): EP content added to base objects, e.g. bar drinks, pet object use |
| terrain | `Terrain.Singleton`, hit at the actor's spot | pack definitions only (terrain interactions such as dig, snow, swim-here) |
| lot | the lot the actor stands on | pack definitions only |
| self, social | the actor itself; a social partner NPC | pack definitions only: self interactions (umbrella, transform, skills), pack socials (by ActionKey) |

- **ITUN / tuning-only definitions.** EA's `InteractionDefinition.Test` returns `Tuning_DisallowUserDirected` when the ITUN tuning forbids player use (InteractionDefinition.cs:309) [verified]. Those definitions only ever run autonomously. The sweep retests them as autonomous (priority Autonomous, `InteractionFlags.Autonomous`) and pushes them that way (`test: "pass-auto"`).
- **Code-version gated pairs** (`CheckIfInteractionValid` false: an installed-pack check in the tuning) are logged as `code version`, not pushed.
- **Career and situation interactions** (`ActiveFireFighter`, `Stylist`, `PrivateEyeInteractions`, daycare, festivals) show up only while their situation or career holds the Sim. The first sweep logs their fail reasons. A situation set-up pass (§4 later steps) forces them.
- **Rabbit-hole interactions** are object rows on rabbit-hole targets. The Sim routes there and goes inside. `secsPerRun` caps the run, then the cancel and, if the Sim does not come out, the reset.

## 2. Actors

- **NPCs picked by the agent:** one human per age (youngadult, adult, elder, teen, child), from known, non-active, non-service households, not inside a rabbit hole. Also one dog, cat and horse when the world has them (EP5 jobs first run `spawn_horse`). A separate youngadult/adult NPC is the social partner. Explicit `actors: [refs]` (names, ids, `picked:N`) replaces the age picks. An active-household Sim passed there is refused.
- **Household Sim: only on test saves.** `household: true` adds the selected Sim as the last actor, so only definitions that no NPC can run reach it (EA Tests that need `IsSelectable` / active household). This is allowed for a dedicated test Sim in the active household of a test save, never on a player save. The mod rule "household untouched" is about our fixes, not about harness actions. The queues here leave it off.
- **Per definition:** EA's Test runs for every actor. The first actor that passes is pushed. The row keeps up to three fail reasons from the actors before it (the result code plus the greyed-out tooltip text when there is one).

## 3. The forced run per definition

The rules are the repro rules (memory repro-actions, clean-repro-sequence): teleport, cancel/max/clear, force, never wait for events.

1. **Setup, once:** the actors and the partner are teleported to the sweep spot (`near`, default the far community lot: setup_sims rules, so queues are cancelled, motives maxed and autonomy turned off and tracked).
2. **Test** each candidate. If the actor stands more than 8 m from the target, teleport it there (`SetupSimsAt`), plus the partner for socials. Then **test again** at the new spot (`retest after move` when it now fails), and **push**: the first picker row is picked if the definition has a picker, then `CreateInstanceFromParameters`, then `InteractionQueue.Add` (immediate interactions go to `ImmediateInteractionTask`). The move and the push run in one task slice with no yield, so nothing moves in between. That has the same effect as pause / act / unpause without stopping the clock that the run needs.
3. **Run** for `secsPerRun` real seconds (default 4). `ran` = the instance became `CurrentInteraction` (it entered Run). `ms` = time from Run start to the end or the cap. The row ends early when the instance leaves the queue.
4. **Cancel and reset:** `CancelAllInteractions` on the actor (and the partner). A queue that does not empty in 3 s means `SetObjectToReset` (EA's reset end state, `reset: true`).
5. **Robustness:** each row runs in try/catch. An exception (`ex`) resets the actors and the sweep goes on. Actors that die or are destroyed drop out (no actor left = stop). `maxMinutes` caps the whole sweep (`state: capped`, `next` = the resume index, start again with `from: next`). The row index is resumable: targets are sorted by class name, and candidates follow the game's pair order per actor. **[inferred]**: that order is stable for one save, to confirm with two runs.
6. **Per definition at most `maxPerDef` pushes (default 1)** and 3 x maxPerDef logged tries over all targets, so a definition offered by 300 objects does not run 300 times.

Logged per row: `i`, `pack`, `cls` (target class), `tk` (kind), `def` (definition class + `:ActionKey` for socials), `name` (menu label), `test` (`pass`, `pass-auto`, `fail`, `skipped: destructive (<prefix>)`), `fails`, `actor`, `pushed`, `why` (not pushed), `ran`, `imm`, `ms`, `se` + `err` (script errors while the run was open: NRaas ErrorTrap's `ScriptCore.ExceptionTrap.OnScriptError`, a second subscriber as in sim_gone_errors; with no ErrorTrap, `trap: none` and no per-row count), `ex`, `reset`, `dialog` (a modal dialog was open afterwards; the queue's safe-default clicks answer it), `picked`.

Rows go to the `result` answer (a cursor: each row once, at most 1500 per answer; `peek: true` = counts only, for the `until` poll) and to ts3native.log as `isweep row {json}`. The log line is cut to the 470-character diag line, but it survives a crash. The end line is `isweep done <pack> state <s> rows <n> next <i> pushed .. ran .. se ..`.

## 4. Preconditions

| Need | How the sweep meets it | Status |
|---|---|---|
| age | one actor per age, plus pets | built |
| motives | maxed at setup (setup_sims rules) | built |
| season, weather, moon | `test_env {mode: fixed, season, moon_phase}` step before the sweep: EP8 winter, EP7 fall + full moon. The queue job sets `noEnv: true` so the env guard does not undo it | built |
| horse | `spawn_horse` step in sweep-ep5 | built |
| user-directed disallowed by ITUN | autonomous retest | built |
| skill, career, occult, money, relationship | the first run's fail reasons say which definitions need what (`fails`). Next step: a prep pass per reason class: `set_skill` to the needed level, `career` join, occult via the agent's occult/transform commands where they exist, NPC household funds raised, `modify_relationship` for social gates. Then the sweep reruns only the rows that failed (report lists them) | design |
| situations (festival, firefighter emergency, daycare, bar brawl, graduation) | start the situation through its own EA entry point, then sweep its definitions on its Sims | design, per situation |

## 5. Skipped: destructive definitions

A row is skipped (`test: skipped: destructive (<prefix>)`) when a `.`/`+` segment of the definition class name starts with one of these (case-insensitive, `kIswDestructive`): Death, Die, Dying, Kill, Murder, Euthan, Reaper, Disintegrat, Sell, Delete, Destroy, Bulldoze, Demolish, Scrap, Recycle, MoveOut, MoveIn, AskToMoveIn, KickOut, Evict, Disown, Adopt, Abandon, Emigrat, Travel, Vacation, GoToUniversity, GoToSchoolAbroad, GoToFuture, GoBackToPresent, ReturnHome, TimeTravel, LeaveWorld, Quit, AgeUp, Propose, Marry, Wedding, Elope, BreakUp, Divorce, TryForBaby, Abduct, SetOnFire, Arson, Burn, Pregnan, SaveGame, BuyLot, SellLot, ChangeLot. `skip: [...]` adds prefixes for one run. Reasons: Sims die or leave the world, households change, objects or lots go away, the game travels or saves.

`destructive: true` runs them as well. Use it only in a job whose save is a throwaway copy (never a Baseline save in place, never a player save). Only definitions that stay inside the world (no Travel or SaveGame) are worth it. **[inferred]**: the prefix list catches the classes by name. The first report's skipped rows show what it caught, and its ran rows have to be read for anything destructive that slipped through (a dead Sim, a moved household).

## 6. Queues (prepared, not run)

- `sweep-packs-home-1115.json`: one correctness job per EP/SP in a home world, SP1 first as the smoke job (`stopQueueOnFail`). EP1, EP4, EP7, EP8, EP9 and EP11 run in SV, EP2, EP3, EP6 and the SPs in BP, EP5 and EP10 in IP. ~12 h.
- `sweep-packs-dest-1115.json`: France, China and Egypt (sweep-ep1), University (sweep-ep9), Oasis Landing (sweep-ep11), via profile.travel from Baseline-BP. ~6 h.
- **Pass:** no crash, the last repro step passes (`require`: state done, >= 1 row), logRequire `isweep done <PACK> state done rows [1-9]`, switches read back, no FAIL line. **Script errors:** an all-EA stack is a finding. It is listed through `scriptErrorsReport` (regex `EA_ONLY_RE`: a stack with no `Ts3Fixes.` or `Ts3Agent.` frame) and is never a FAIL. One with our frame counts toward `scriptErrorsMax: 0` = FAIL (known-script-errors rules).
- **Before the run:** merge `feature/interaction-sweep-1115` and rebuild the agent in `build/`. The queues take ts3agent from `build/`, so the lane's package `game-copy/ts3agent.package.lane-sweep-1115` must be copied there.
- **After the run:** `node tools/harness/interaction-sweep-report.ts <tag> <job ts3native.log copy> --out docs/data/results/<date>-sweep-<pack>.md`. Per pack it lists seen / tested / ok / pushed / ran / failed / skipped / never available, the top fail reasons and the top script errors. It then gives each docs/267 never-seen type with definitions a status: ran, pushed, ok not pushed, test failed, skipped, or never available.

## 7. Limits and risks

- **[inferred] Catalogue scan cost.** A pack object class with no in-world instance is found by creating catalogue products out of world (name-hinted first, up to `scanMax` 3000) and reading their class, as `buy_object {type}` does. Expect tens of seconds at setup, in 10-product slices. A class with no product (world-placed only, e.g. tomb objects outside the WA worlds) is noted `no instance` and not swept in that world.
- **[inferred] Script-error attribution is by time window.** Every script error while a run is open counts for that row, from any task. A natural NPC error at the same moment is misattributed. The full ScriptError xml in the user folder is the ground truth.
- **[inferred] Placed objects** sit on the far community lot and are destroyed at the end (`cleanup`). An object that a forced interaction moved into an inventory stays there.
- **Interactions that open a modal dialog** are flagged `dialog`. The harness's safe-default click answers them. A dialog with no safe default stops the run in the mode guard, as in any job.
- **Not covered yet:** situation-only and career-only definitions (§4 design rows), definitions that only appear in a state the sweep never creates (in a boat, on a horse, underwater), and other Sims' specific social contexts (romance levels).
- **Ledger:** no new docs/51 claim. The sweep is harness tooling, and its first run gives the evidence.
