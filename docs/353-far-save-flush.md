# 353. Far save flush: one design for every far feature (SD-4 save part, 2026-11-26)

Status: static analysis and design only. Nothing built, nothing run. Steam 1.67 (`game-copy/bin/TS3W.exe`, raw
disassembly with capstone; decompiled script assemblies; NRaas sources read for behaviour only). EA 1.69 not checked.
Package SD-4 of docs/340 §6.2, save / flush part. The facade rows, the id rule and the stage ladder of SD-4 are
already covered by docs/344 §3, §4 and §6 and are only referenced here (§12).
Ledger: docs/51 §622. Handoff: `docs/handoff/sd4-save-flush-1126.md`.

## 0. Short answer

- **Every save in the game goes through one native call.** `0xAB1030` has one caller in the whole image, the call at
  `0x742A95` inside the script call `LoadSaveManager_SaveGame_Impl` `0x742A50` [verified, V-622-1]. Normal saves and
  travel saves both arrive there. Managed code reaches it only through two static methods of
  `Sims3.SimIFace.LoadSaveManager`: `SaveGame` and `SaveTravel` [verified, V-622-2].
- **The flush hub sits on those two static methods** (prefix = hard flush, postfix = commit and restore), not on
  `OptionsModel.SaveGame`. This closes docs/343 Q3: `OptionsModel.SaveGame` misses every travel save (EA's three sites
  and NRaas Traveler's own two), returns before the save for asynchronous callers, and has dialogs and a yield between
  it and the native call [verified, V-622-3..6].
- The existing `OptionsModel.SaveGame` hook (`DisposeSaveHook`) stays as an **early quiesce** step: it only stops new
  go-far and lets owners start work that needs to yield.
- **The hard flush never yields.** Between the hub prefix and the native call there is no yield, and the native save
  itself runs no script task. An owner whose restore needs a yield must finish in the quiesce step or keep the item
  live.
- **docs/346 Q3: keep the `AlarmManager.Write` bracket (Hook B).** The hub covers every save, but persistence writers
  also run outside `0xAB1030` (lot and household export to the bin). The bracket is the only layer that is correct for
  those by construction, and it costs nothing when the table is empty.
- **docs/343 Q4: no write-ahead per record.** A crash between saves loses nothing, because the last save was written
  from a full flush. The repair file is enough if one rule is added: when an L2 object cannot be created again and its
  repair line cannot be written to disk, the normal save is refused with EA's own error dialog and the previous save
  stays intact; a travel save cannot be refused, so it saves and counts (§7, decided 2026-11-26).
- Rough save-time cost: L0 (parked Sims, held alarms, parked lots) under 50 ms. T4 placeholders and L2 records add
  seconds and a memory peak; they need a memory guard and stay opt-in until measured (§10).

## 1. EA's save entry points [verified unless marked]

### 1.1 Native

| Item | Address | Note |
|---|---|---|
| Script call `LoadSaveManager_SaveGame_Impl(handle, isTravelSave, isTraveling)` | `0x742A50` | only reference is the script call table slot `0x11601E0`; no direct call |
| Call into the save | `0x742A95` | the only `call` or `jmp` to `0xAB1030` in `.text`; no data reference to `0xAB1030` (no vtable slot) |
| Save body | `0xAB1030` | steps 3-11 of docs/15 "Phase 7"; travel save stops before the copy to `Saves\` (`0xAB1319`) |
| Package write | `0xAAC110`, one caller `0xAB12AF` | header, listeners, world, routing, post listeners (docs/15 step 5) |
| World write | `0xC6D460`, one caller `0xAAC320` | lots, objects and the script stream (docs/26 §1.2) |
| Script call `LoadSaveManager_SaveWorld(callback)` | `0x743680` (table slot `0x11601F8`) | calls `0x743470`; not the save body. Only reached in Edit In Game from the world tool (§1.3) |

ts3native already owns the `0x742A95` call site (`SaveGameHook` in `native/ts3native/savewrite.cpp`, installed with
`[SaveWrite] Enabled=1`; docs/68 row `[SaveWrite]`). No other mod patches it (`node tools/hook-sites.ts --check`).
Thread: sim thread, inside a script call made by the saving script task. No lock of ours is held.

### 1.2 Managed chain

`Sims3.SimIFace.LoadSaveManager.SaveGame(info, traveling)` (LoadSaveManager.cs:175) and `SaveTravel()` (:184) are
static forwarders to the `ILoadSaveManager` instance. The instance methods (`ScriptCore.LoadSaveManager.SaveGame`
ScriptCore/LoadSaveManager.cs:126, `SaveTravel` :131) each make the one script call, with `isTravelSave` false or
true.

The instance method is **not** a safe hook site: the NRaas source tree holds a `ScriptCore.LoadSaveManagerEx` class
that re-implements `SaveGame` and calls the script call itself (`game-copy/nraas-src/Sims3/Compiler/ErrorTrap.cs:181`,
:214) [verified in the source; inferred that a shipped build installs it]. A patch of the base instance method would
not run with that class installed. The static forwarders are above it and are what every caller uses.

Neither forwarder has one of the six inlined shapes of docs/293 (a null test and a `callvirt`) [inferred from the
shape table; `Fixes.Patch` checks it at install and refuses otherwise].

Callbacks the native save makes into script, in order (ScriptCore/LoadSaveManager.cs): `BeginSaveObjects` :396, then
per lot group `SaveObjectGroup` :427 (fires the `ObjectGroupSaving` event, subscribers `LotManager.cs:967` and
`Camera.cs:1323`, then the group's `PreSave`), per object `SaveObject` :404 and `SaveObjectStack` :416, then
`EndSaveObjects` :446. Objects that implement `IPersistPreSave` get their call from the writer
(`PersistWriter.cs:484`). `AlarmManager.Write` (AlarmManager.cs:997) runs inside this stream. All of these are plain
calls on the sim thread; none can create or destroy world objects safely, because the native lot object lists are
being written around them. So nothing can be flushed from inside the save; the flush must be complete before
`0xAB1030` is entered.

### 1.3 Every path that triggers a save

| # | Path | Site | Reaches | Sync? |
|---|---|---|---|---|
| S1 | Options menu Save | UI `OptionsMenu.cs:172` | `OptionsModel.SaveGame()` | **async**: queues `SaveGameTask` (OptionsModel.cs:520, :933) and returns |
| S2 | Save / Save As from the game entry puck | `PuckCommon.cs:452`, :465 | `OptionsModel.SaveGame(true ...)` | sync |
| S3 | Edit Town prompt "Save and continue" | `InWorldState.cs:758` (`PromptedSave`) | same | sync |
| S4 | Quit prompts "Save and quit" | `InWorldState.cs:804`, :814, :989, :1031 | same, then `TransitionToLeaveInWorld` only if the save returned true | sync |
| S5 | Save when a modal closes (forced file name on a world move) | `LiveModeState.cs:221` | same | sync |
| S6 | Move to another world, "Save" | `MovingWorldUtil.cs:185`, :187 | same (Save As form) | sync |
| S7 | Passport | `Passport.cs:926`, `PassportModel.cs:287` | same | sync |
| S8 | EA soak-test autosave task | `AutoSave.cs:81` | same, own file names | sync |
| S9 | NRaas Saver (autosave mod) | `NRaasSaver/Saver.cs:347` | `OptionsModel.SaveGame(true, true, saveAs)` | sync |
| S10 | NRaas Traveler prompt save | `NRaasTraveler/Traveler.cs:239` | `OptionsModel.SaveGame(false, true, true)`, then waits on the in-progress flag | **async** |
| T1 | Travel start and travel end | `GameStates.cs:1643`, :1678 (`SwitchWorlds`) | `LoadSaveManager.SaveTravel()` directly | sync, loading screen up |
| T2 | Edit another town, and back | `GameStates.cs:1785` (`EditOtherTownSwitchWorlds`) | `SaveTravel()` directly | sync |
| T3 | NRaas Traveler's own copy of the travel code | `GameStatesEx.cs:1599`, :1716 | `SaveTravel()` directly | sync |
| W1 | Edit In Game from the world tool | `OptionsModel.cs:961` | `LoadSaveManager.SaveWorld()` | not a game save |
| X1 | Bin and library export (household for travel or a move, lot or household to the library, Edit Town clipboard) | `Bin.ExportHousehold*`, lot export helper `0xABC880` (docs/344 §4) | persistence writers, **not** `0xAB1030` | - |
| N1 | Quit without saving, world change from the menu | `GameStates.TransitionToLeaveInWorld`, `World.OnWorldQuit` (World.cs:1674) | no save | - |

S1-S10 all end in `OptionsModel.SaveGameSynchronous` (OptionsModel.cs:944), which calls the static forwarder once
(:1109). Inside it, before that call: the save-disabled and modal tests, the W1 branch, up to three dialogs, the
pause lock, **one `Simulator.Sleep(0)`**, a second gate test that can still cancel, and the thumbnail work. After it:
the result switch, metadata and the challenge save.

The travel sites never look at the result of `SaveTravel` (assigned to a local that is not read) [verified]. So a
travel save cannot be refused: a refusal would continue the trip with a stale working copy.

Before `SaveTravel` EA resets or sends home resident Sims and yields once (`SwitchWorlds`, travel end branch). The
world written by a travel save becomes that world's `.nhd` at the next normal save (docs/15 step 7, docs/343 §6), so
a travel save needs the same flush as a normal one.

## 2. Why `OptionsModel.SaveGame` alone is not enough (docs/343 Q3)

| Gap | Evidence |
|---|---|
| Travel saves T1-T3 do not pass it | §1.3 |
| Async callers S1 and S10: the hook returns before the save starts; the save runs in a later task | OptionsModel.cs:511-520 |
| Dialogs and one yield between the hook and the native call, also for sync callers; the Save As name dialog can stay open for minutes | OptionsModel.cs:944-1109 |
| The save can still be cancelled after the hook ran (gates, dialogs) | same |
| A postfix does not see the result of an async save | same |

Far state flushed at that hook could go far again before the save. The current users of `DisposeSaveHook` (dispose
spread, dead references, service lists, played-household key) are cleanups that stay correct if they run early, which
is why the gap did not matter until now.

## 3. The hub

Name for the builders: `FarSave` (ts3fixes), switch `farsave` implied by any far switch (no own default; with every
far switch off the hooks are not installed).

| Hook | Site | Does |
|---|---|---|
| H1 quiesce | prefix of `OptionsModel.SaveGame(bool, bool, bool)` (existing `DisposeSaveHook`) and of the travel request hooks FS-8 already needs (EP6 `travel start`, docs/345 §10) | sets `FarSave.Quiesce`: no new go-far, no new park; owners may start yielding restores through the spread queue. Cleared by H3 or after 2 sim-minutes without a save (cancelled dialog) |
| H2 hard flush | prefix of static `LoadSaveManager.SaveGame` and `SaveTravel` | §4, no yield, no budget |
| H3 commit and restore | postfix of the same two, with the result code | §5 |
| H4 native check | `SaveGameHook` at `0x742A95`, before the original | reads the shared count of items still far (owners publish it); non-zero = the hub was bypassed: counter `farSaveUnflushed`, one log line, and the L2 rule of §7. Never calls script |
| H5 export | prefix of the bin export entry points (X1) | L2 only: materialise the lots the export reads; L0 needs nothing (objects exist, Hook B covers alarms) |
| H6 no-save exits | `World.OnWorldQuit`, Edit Town entry, W1 | `ReleaseAll` and table reset (docs/345 §11, docs/346 §3.5); no file write |

`FarSave.InSave` is true from H2 to H3. It replaces `OptionsModel.SaveGameInProgress` as the "saving" test of the far
code, because that flag is not set for travel saves.

Every owner call in H2 and H3 is wrapped: an exception is logged, counted in `farFlushFailed` and turns that owner's
kill path on (promote all, `ReleaseAll`). Nothing is ever thrown into EA's save code.

W1: with `World.IsEditInGameFromWBMode()` true, every far switch stands down for the session (no far state exists).

## 4. Hard flush (H2): order and what must be true after it

Order is lots first, then Sims, as promotion (docs/346 §3.4).

| Step | Owner | Action | True after it |
|---|---|---|---|
| 1 | LZ (L2) | materialise every lot record; failed items go to the repair list | every object EA should save exists, with its id |
| 2 | LZ (L1) | restore native parts of stripped objects | no object lacks what the native lot writer reads |
| 3 | FO | apply the computed model for parked lots up to the save tick (docs/331 §6.2) where it is closed-form; timers stay held | lot state is what EA could have reached at this tick |
| 4 | FS (T4) | rematerialise every placeholder we own; destroy ours that have a Sim | no placeholder of ours is in the world |
| 5 | FS (T3) | docs/345 §9.4 steps 1, 3, 4: evaluate `F(t)`, object at the place of `F(t)`, drift applied, our hide and park marks cleared | a saved far Sim is a plain EA Sim |
| 6 | FarHold | nothing; Hook B brackets each `AlarmManager.Write` | saved alarms are EA alarms with original due times |
| 7 | hub | if the repair list is not empty, write the repair file (docs/343 §7) | repair lines are on disk before the save starts |
| 8 | hub | publish 0 to the shared count read by H4 | H4 passes |

Rules:

- **No yield.** `Sim.Placeholder.Rematerialize` has no sleep in its own body [verified, Sim.cs:44329-44339];
  whether `SimDescription.Instantiate` and the outfit switch it calls can yield is not read to the end [inferred no;
  open question 2]. Dematerialize does sleep (docs/349 P1), so a go-far that is still running when H2 arrives must be
  cancellable without a yield, or T4 go-far must not start while `FarSave.Quiesce` is set and H2 must find none
  running. FS-16 picks one and tests it.
- **State does not change.** Records, states, seeds and the trace stay (docs/345 §9.4). Only the EA-side form
  changes. Drift apply at a save must be split-invariant (apply to the save tick, then to go-live, equals one apply),
  otherwise a save changes the outcome. Test M-save in §11.
- **Household.** No step touches a household Sim, the selected Sim, a household lot or anything of unknown scope:
  the owners' tables cannot hold them (docs/345 §10 EP1-EP9, docs/343 §8). The hub re-checks each id with the shared
  predicate before step 4 and 5 and promotes on a hit (`farsimHhCaughtInFar`, must be 0). `HouseholdToo` is not read.
- **Unknown owner state** (table and world disagree): that item is promoted through EA's path and counted; the save
  goes on.

## 5. After the save (H3)

| Result | Action |
|---|---|
| Normal save, success | read the key again (Save As gives a new one), write the sidecar block (docs/343 §6 step 3), delete the repair file if the repair list was empty; clear `InSave` and `Quiesce`; owners go back to their stage through the spread queue (L0 marks at once per Sim in id order, T4 and L2 at the normal pace) |
| Normal save, error code | no sidecar write; old sidecar still fits the old save; repair file deleted (its stamp belongs to a save that does not exist); owners restore as above |
| Travel save | home world block kept in memory (docs/343 §6); repair file stays on disk; then H6 at world unload |

The `.sims3` on disk after a successful save holds only EA state. Our files missing at the next load: every Sim and
lot is live, far state starts from EA state (docs/343 §5.2). This is the vanilla fallback and needs no code path of
its own.

## 6. What must be true at each path

| Path | Before EA writes | After |
|---|---|---|
| S1-S10 normal save | H1 then H2 complete; `farSaveUnflushed` 0 | H3 |
| Save As, first save | same; key read after the save | sidecar under the new key |
| T1-T3 travel save | H2 complete. Cannot be refused, so L2 should already be empty: travel request is a materialise-all trigger (docs/344 §6.2) and L2 parking is off while `GameStates.IsTravelling` or travel data exists. A record that is still there and fails twice: save and count, repair file (§7 rule 4) | block in memory, `ReleaseAll`, tables reset |
| X1 export | L2 lots the export reads are live (H5); L0 unchanged | owners restore |
| W1 world tool | far switches off for the session | - |
| Edit Town, Build / Buy entry | L2 materialise all, far passes stop (docs/345 §11) | resume on return to live |
| N1 quit, world change | nothing written; `ReleaseAll`, tables reset before the world is destroyed | - |
| Crash at any time | §8 | - |

## 7. Repair file or write-ahead (docs/343 Q4)

A write-ahead record per L2 object would protect against one case only: the process dies while records exist in RAM.
In that case no save was written after the objects went far (every save flushes first), so the last save on disk
holds all of them. Nothing is lost that vanilla would have kept. Write-ahead buys nothing there and costs a disk
write per parked lot.

The real loss case is inside H2: an object cannot be created again. Then:

1. Second try through the script create with a new id (docs/344 §6.2; allowed for leaf classes only).
2. Still failing: a `P` line with the full record (class, id, lot, transform, exported bytes) goes into the repair
   file, written and flushed before the save (step 7 of §4).
3. Repair file write fails too (disk full, antivirus), **normal save: the save is refused** (decided by Rodney
   2026-11-26). The hub returns an error code instead of calling the save. The player sees EA's "save failed, error
   code" dialog (OptionsModel.cs default branch of the result switch) plus one notification of ours that names the
   cause; "Save and quit" stays in the game (S4). The previous save on disk is not touched, because `0xAB1030` is
   never entered. The records stay in RAM and the next save tries again. Counter `farSaveRefused`.
4. Same double failure, **travel save: save and count.** A travel save cannot be refused (§1.3: the result is never
   read). The save runs, the object is missing from it, counter `farTravelSaveLost` and one log line per object. The
   repair file is still attempted in step 2 and, when it was written, the repair pass restores the object at the next
   load of that world. §6 keeps L2 empty during travel so that this path is a last resort, not a normal one.
5. H4 applies rules 3 and 4 natively when the hub was bypassed and the shared count says L2 records exist.

The code returned is a value EA never produces for a real failure, so the log line names us.

With this rule the repair file is sufficient and L2 is not blocked by Q4. L0 and T3 never produce repair lines
(nothing is taken out of EA's lists). T4 produces none either: a description whose Sim could not be rebuilt is a
resident without a Sim, which EA saves and loads as such; the `CleanOnLoad` step of FS-10 instantiates it at home.

## 8. Crash safety

| Crash point | On disk | Next load |
|---|---|---|
| Between saves, far state in RAM | last save (full EA state), its sidecar | normal; far state restarts |
| In H2, before the native call | same, plus maybe a repair file whose stamp fits no finished save | repair file ignored and deleted, or applied with no effect (every `P` id already exists in that save) |
| Inside EA's save | EA's own rules (working copy, `.backup`, `.bad`; docs/15 steps 8-10) | as vanilla; sidecar is `stale` or fits the old save |
| After the save, before the sidecar write | new save, old sidecar | `stale`: empty far state |

Memory: H2 creates Sims and objects at the moment the process is closest to Error 12 (the save maps a large view,
docs/215). Rule **FarMemGuard**: when the largest free block falls under a threshold (the docs/204 measure), T4 and L2
stop parking and drain in the background, so a later save needs no burst. L0 allocates nothing in H2.

## 9. Answers to the open questions passed to SD-4

| Question | Answer |
|---|---|
| docs/343 Q3: do mod save paths reach `OptionsModel.SaveGame`? | NRaas Saver and Traveler's prompt save do; all travel saves (EA's and Traveler's) do not. Hub goes on the two static `LoadSaveManager` methods, native check at `0x742A95` (§2, §3) |
| docs/343 Q4: repair file or write-ahead? | repair file plus the refuse rule (§7) |
| docs/343 Q7: fields of `P.f` | class key, script class, id, lot id, transform, level, design preset, export bytes of `0x939870` (docs/344 §6.2); only written in a repair file |
| docs/346 Q3: `Write` bracket or hub only? | keep the bracket; the hub does not call `ReleaseAll` for a save (§0, path X1) |
| docs/345 §9.4 hook position | H2; step 2 unchanged; step 4 holds whatever flags EA persists, so §16 Q6 no longer decides save safety (it still decides `CleanOnLoad`) |
| docs/345 §11 "save in progress" row | test is `FarSave.InSave`, not the options model flag |
| docs/349 Q1 (is the placeholder's description field saved?) | no longer on the save path: no placeholder of ours is saved. Still needed for EA's own placeholders, unchanged |

## 10. Cost estimate [inferred, rough; nothing measured]

Reference: a Bridgeport save is 11-13 s (docs/15).

| Part | Per item | Typical count | Added per save |
|---|---|---|---|
| H1, H3, H4 fixed work | - | - | under 1 ms |
| T3 Sim (evaluate, move, clear marks, drift) | ~0.1 ms | 100-150 | 10-15 ms |
| FarHold bracket | list move per held timer | a few thousand | 1-5 ms |
| Parked lot model apply | ~0.1 ms per lot | 100-200 | 10-20 ms |
| Sidecar write (docs/343 §10 cap) | - | one file | 5-20 ms |
| T4 rematerialise | 50-150 ms per Sim (outfit build) | 50-100 | **3-15 s** |
| L2 materialise | 0.2-1 ms per object | 5,000-20,000 | **1-20 s**, plus the memory peak |

L0 is free in practice. T4 and L2 can double the save time, and they pay again after the save to go far. Each owner
logs `farFlushMs` and item counts per save; T4 and L2 stay opt-in until those numbers and the FarMemGuard behaviour
are measured. A cheaper T4 flush (keep placeholders in the save) is rejected: a player who removes the mod would keep
residents without Sims.

## 11. What the builders owe

- Hub selftest with fake owners: order of §4, exception in one owner, error result, travel result, quiesce time-out.
- M-save: same seed, one run with a save in the middle, one without: equal trace hash (docs/345 §13).
- Save round trip per owner: save with far state, load with the mod removed (no ts3fixes, no sidecar): no hidden Sim,
  no placeholder of ours, alarm count equal to a vanilla save of the same tick, object count equal.
- Counters: `farFlushFailed`, `farSaveUnflushed`, `farSaveRefused`, `farTravelSaveLost` (all must be 0 in the
  normal matrix rows for a switch to go ON), `farFlushMs`, `farFlushItems` per owner. Slots through
  `tools/registry.ts`.
- Save matrix (memory note "save change matrix"): Save, Save As, async menu save, quit with save, NRaas Saver,
  travel out and back with and without NRaas Traveler, Edit Town prompt save, export a far lot to the library.
- Forced double-failure rows (test hook: one L2 create fails twice, repair write fails):
  - normal save: dialog and our notification shown, `farSaveRefused` 1, save folder and `.backup` byte-equal to
    before, the next save without the fault succeeds and holds the object;
  - "Save and quit" with the fault: the game stays in the world;
  - travel save with the fault: trip goes on, `farTravelSaveLost` 1, log line names the object;
  - travel save with only the create failing: repair file written, object back after the return load.

## 12. Rest of SD-4 (facade, ids, stages)

Unchanged from docs/344: ids are stable across save and load and can be chosen at create time through `0x937F60` or
`0x939B60` (calling convention still to be read, docs/344 Q4); facade rows per choke point in docs/344 §3; stage
order and kill criteria in docs/344 §6. This doc adds two materialise-all triggers to docs/344 §6.2: travel request
and bin export (H5).

## 13. Open questions

1. **Closed (Rodney 2026-11-26).** Double L2 failure: a normal save is refused, the player sees a message and the
   previous save stays intact; a travel save cannot be refused, so it saves and counts, with a repair file (§7).
2. Can `SimDescription.Instantiate(Vector3)` or the outfit switch in `Rematerialize` yield? If yes, T4 flush moves
   to H1 and H2 only verifies. (FS-16, static read)
3. Is a running dematerialize task cancellable without a yield? (FS-16)
4. Census of X1 entry points (script and native) that write script objects outside `0xAB1030`. (LZ-6)
5. Does a shipped NRaas build install the `LoadSaveManagerEx` class? Does any top-100 mod call the save script call
   by reflection? (compat row, docs/328)
6. EA 1.69: addresses of `0x742A95`, `0xAB1030`, `0x743680` and the managed line numbers. (RX lane)
7. Threshold and measure for FarMemGuard. (LZ-1 with docs/204 numbers)
8. Measured `farFlushMs` for T4 and L2. (FS-16, LZ-6 first runs)
