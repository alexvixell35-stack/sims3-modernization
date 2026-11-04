# 214. First-time Mono runtime work reachable from autonomy scoring after load

Written 2026-10-30. Static closure plus one 30-minute live run.

**Question** (docs/206 §8 X11): which methods and classes reachable from autonomy scoring still make Mono do first-time work after a world has loaded and played for a few minutes? First-time work means a method transform to mint code, a class init or `.cctor`, a vtable build, a lazy generic instance, a first delegate, an image load, a thunk or an icall lookup. That work is not safe on the `[AutShadow]` worker, so it must be warmed up first or make the worker abort.

**Output:** `docs/data/autonomy-cold-paths.csv` (`method_or_type, kind, reached_from, rare_branch_condition, confidence`, 910 rows).

## Method and limits

| Part | How | Tag |
|---|---|---|
| Static closure | `tools/autonomy-cold-paths.ts` (details below) | [verified] reach in IL |
| Live probe | `tools/frida/cold-paths.js` (details below) | [verified] per event |
| Join | Live rows and static rows (rules below). | as tagged per row |

**Static closure:**
- **Roots:** the docs/75 entry-list rows (`docs/data/autonomy-entry-list.csv`) that resolve to a definition, a family member, a base path or a scoring function. That gives 73 roots. 21 rows are skipped: 19 unresolved `Definition.Test` buckets, "base path: C2 self" and `.ctor`. The live run covers these.
- **Walk:** depth 12 over the docs/78 index, with the same edges as `managed-callgraph.ts` `closure()`. Delegate creation is not followed.
- **Result:** 3,468 methods. 236 types whose `.cctor` a reached method can trigger (a static field access, a `newobj`, or a static call into a type with a `.cctor`). 54 delegate targets created (`ldftn`). 264 generic members.

**Live probe:**
- **Hooks:** 8 read-only hooks, each behind a prologue byte check:
  - transform `0xE643B0`;
  - vtable slow path `0xEA1E30`;
  - `mono_runtime_class_init` `0xEA3370`, past its fast exit;
  - thunk `0xE51400`;
  - EA image loader `0xD6FCB0`, on a cache miss;
  - `mono_class_init` `0xE6EE20`, past its fast exit;
  - `mono_lookup_internal_call` `0xE82680`;
  - finalizer registration `0xE78F60`.
- **Per event:** the first-hit time, the count, and the interpreter frames of that thread (TLS thread context `[0x11F4160]`, frame chain per docs/130 §2).
- **Run:**
  - Save: Moonlight Falls on day 406, which is the task-3 long-running save.
  - Setup: release v0.2.4 on, the player's full mod set with ts3fixes in `Overrides`.
  - Attach: a few seconds after the world reached live mode with a household.
  - Play: autopilot at triple speed for 30 minutes.
- **Probe health:** 19,027 distinct events, 0 dropped, 0 read errors, no hook refused [verified, the probe's own counters].

**Join:**
- **Live row:** a first hit at minute 5 or later after attach, whose interpreter stack has a scoring frame. A scoring frame is an entry-list root, `Autonomy::*`, `*Definition*::Test` / `CalculateScore`, or `*ScoringFunction`. `reached_from` is the innermost such frame.
- **Static row:** a member within depth 3 of a root that was never transformed by the end of the run, or a trigger type that was never initialised by the end of the run (the end-of-run snapshot: 32,308 classes, 49,991 transformed methods).

Limits:

- **One run on one save:** one household, one mod set. The 62 mod rows depend on this install. The method needs a second run on another save before any row counts as typical. Rodney's rule is two clean runs.
- **"Scoring frame" is broad.** 136 live rows have only `Autonomy.Update` above them. ts3fixes renames the original to `UpdateOrig`, and no definition frame sits in between. They may be outside scoring (motive or buff work called from `Autonomy.Update`). Their `confidence` says so. The other 352 rows have a definition `Test` / score frame or an `Autonomy` search frame.
- **The static walk does not follow virtual dispatch,** so the static rows are a floor. Mod overrides and EA virtual targets show up only through the live run. docs/127's typed closure is the wider static set.
- **Generic and delegate kinds are named heuristically.**
  - `generic` = a transform or vtable of a name with a backtick.
  - `delegate` = a transform of an `Invoke` wrapper, or a created delegate target that never ran.
  - The probe does not hook generic instantiation itself.
- **Warm-up minute counts from the attach, not the load.** The attach came a few seconds after live mode. Load-time work before that (the game's own load, ErrorTrap's load scan) is not in the probe's counts.
- **Exe.** The player's exe is not Steam's byte-for-byte (LAA flag set, outside a Steam library). All 8 prologues matched [verified].

## Findings

1. **The cold paths do not run out in 30 minutes** [verified].
   - Across all stacks, transforms per minute were 9,833, 4,942, 1,881 and 1,186 in minutes 1-4. In minutes 21-30 they were still 47-465 per minute.
   - After minute 5 there were 7,523 transform calls. As distinct names that is 4,596 transforms, 390 vtables, 326 class `.cctor` runs, 69 class inits, 55 thunks, 54 icall lookups and 1 image load. Every one of these had interpreter frames.
   - So a warm-up of "play a few minutes" does not cover the scoring closure.
2. **488 distinct first hits had a scoring frame** [verified]: transform 438, generic 18, vtable 14, cctor 11, class init 4, delegate 1, icall 1, thunk 1.
   - By 5-minute bucket from minute 5: 165, 80, 111, 102, 30. Late first hits keep coming.
   - The top definitions were rare-branch ones:
     - pet definitions (`SleepAndNapOnObject` base 15, `Sim+CommandTrick` 8, `Terrain+PetPuddleInteraction` 7, `Sim+PetPlayOnCarpet` 5);
     - venue and hobby definitions (`DaySpa` relaxation 9, `HorseshoeCourt+Join` 5);
     - couple definitions (`StartBedCuddleA` 4, `CuddleRelaxingKiss` 3);
     - 40 social tests and occult checks under `Autonomy::FindBestSocial`, such as the vampire tests, `CanBecomeVampire` and `CanSimProvidePlasma`. They run the first time a Sim of that kind, or a target with that trait, comes up in a social search.
3. **One lazy icall binding with a thunk, on a scoring stack** [verified event]. `ScriptCore.World::World_HasFloorFeaturesWorldPosImpl` was looked up and got its thunk at minute 23.0, from `Terrain+PetPuddleInteraction` `Test`.
   - On the worker this would race the runtime's icall table and the thunk cache [inferred, docs/130].
   - The warm-up must bind every IC in the closure up front. docs/119 lists 108 bindings at depth 12, and docs/127 adds the virtual ones.
4. **11 `.cctor` runs on scoring stacks, after minute 5** [verified]:
   - EA: `PetSurfacePosture+PetSurfaceFloorJig`, `OccupationJobEvent`, `DaySpaCoupon`, `NestedSocialInteraction`1`, `Sim+MopFloor`, and `HorseshoeCourt+TakeTurn` / `+WaitToThrow`.
   - Mod: the player's Alcohol mod (`DrunkInteractions+PassOut`, `+WetSelf`, `+BadBuzz`) and NRaas Hybrid (`TakeShowerEx+Definition` display class).
   - A `.cctor` runs managed code with static writes. Running it early changes the order of static init, so "init them all at load" is not free either [inferred].
5. **Array and interface classes are built lazily too** [verified]: class init and vtable for `HopscotchCourt[]`, `BuffInstanceCommodityDecayModifier[]`, `ICurtain[]` and `IHasGuid`1`. Typed object queries make them on first use, and the element type comes from the definition being scored [inferred].
6. **Mods are 62 of the 488** [verified].
   - By mod: Twinsimming Sickness & Health 18, NRaas 10, Alcohol 9, wexx 8, Savanita 6, zoeoe water mod 4 (including vtables for `CustomCleanToilet` and `CustomScubaAdventure`), and others.
   - A warm-up list cannot be fixed at build time. It has to come from the loaded assemblies, for example through the docs/132 scan types.
7. **422 static rows were reachable within depth 3 and never ran in 30 minutes** [verified reach, verified not run]: transform 307, generic 70, delegate 36, cctor 9.
   - The 9 `.cctor` types: `SpellBook`, `UTFConnectorSimulator`, `SeasonsManager+SeasonVariable`1`, `GenericManager`3`, and the BCL generics `List`1`, `Queue`1`, `Stack`1`, `EqualityComparer`1` and `Comparer`1`.
   - Each new instance of these generics gets its own class init, so the open types say little about which instance comes next.
   - The top roots are the shared base paths: `Autonomy.CalculateScoreForObjectInteraction` 75, `SocialInteractionA+Definition` 61, `Autonomy.CheckAvailability` 43.
8. **One image load during play, not on a scoring stack** [verified]: at minute 19.3, from `VictorAndrade.GetToKnow` (DeepConversations). simler90Extras loaded two images at attach time, from `Sim.GetNpcVehicle`.

## What it means for the worker [inferred]

- **A full warm-up at load is large and not complete.**
  - Transforming the 3,468 static methods costs load time. It still misses the virtual targets, the mod types and the lazy generic or array instances (rows 5-7).
  - Running the 236 `.cctor`s early changes semantics (finding 4).
- **The cheaper and complete rule is abort on cold.** The worker checks the same flags the probe's fast exits read, and aborts the job to the sim thread when one is cold. The sim thread then does the first-time work, the path is warm next time, and the worker retries. This fits the docs/122 abort model (`ic` abort). The checks:
  - method not transformed: the `RuntimeMethod` code pointer;
  - class not initialised: class `+0x14` bit 0;
  - vtable `.cctor` not run: vtable `+0x18` bit 1;
  - icall not bound: the cached pointer;
  - thunk missing.
- **A warm-up list still helps.** Pre-transform and pre-bind (no `.cctor`) the static closure plus the ICs. That cuts the abort rate on the first jobs, because the run shows a long tail but a heavy head: 70% of the transforms (17,842 of 25,619) happen in the first 4 minutes.

## Files and reproduction

- `docs/data/autonomy-cold-paths.csv`.
- `tools/autonomy-cold-paths.ts` (`--self-check` covers reach, candidates, joins and CSV).
- `tools/frida/cold-paths.js` (read-only: it writes no game memory and calls no game function).
- Probe, attached once the world is in live mode:

  ```
  python -m frida_tools.repl -n TS3W.exe -l tools/frida/cold-paths.js -P '{"out":"<abs path>/run","minutes":30}' -q -t 1980
  ```

- Join:

  ```
  node tools/autonomy-cold-paths.ts --live <abs path>/run-coldpaths.json --warm-min 5 --depth 12 --row-depth 3 > docs/data/autonomy-cold-paths.csv
  ```

## Next

- Run a second save (a young world, harness-driven), and a run of 60 minutes or more to see where the tail flattens.
- Re-run the join with docs/127's typed closure as the static set.
- Count aborts per job under the abort-on-cold rule in monohost Mode R before the worker goes live.
