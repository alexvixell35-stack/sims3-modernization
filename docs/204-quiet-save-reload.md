# 204. Quiet save-reload ("Compaction")

Status 2026-10-30: **design, measurement and the feature built (§11), nothing run.** Rodney's go rule is any measurable
gain (§2), so the Compaction was built while the §2 queue waits its turn; both are default off.

Idea (Rodney 2026-10-30): when the game is close to Error 12, a normal player save turns into a **Compaction**. The game
saves as usual, then, behind a "Compacting..." screen, unloads the world and loads the same save again **in the same
process**. It then restores what the save does not carry (game speed, open panel, what visible Sims were doing) and drops
the screen. Above the threshold a save behaves exactly as today. A Compaction is rare by design: at most once per long
session, and never when it would not help (§3.4).

Error 12 recap (docs/66): TS3W.exe is 32-bit, so its address space is 4 GB at most with LAA. Error 12 means an allocation
or mapping found no free block big enough. The **largest free block** is therefore the signal, not total free and not commit
[verified, docs/66 §1]. Everything that grows over a session and is not returned costs that block: caches, the Boehm heap
that never unmaps (docs/198), CC catalogue state (docs/193), travel (-80 MB per round trip, docs/201) and routesnap
high-water (docs/66 §7). A full restart resets all of it. The open question is how much a world unload resets **without**
a restart.

## 1. What is known before measuring

| Fact | Source | Mark |
|---|---|---|
| EA has two in-process unload paths: Quit to Main Menu (`GameStates.TransitionToLeaveInWorld` -> `LeaveInWorldState` -> `MainMenu`) and the in-game Load (`TransitionToGameStateReloadWorld` -> `ReloadWorldState`, no menu step). Both call `GameUtils.TransitionToNotInWorld` and run the `PreWorldInitializers` shutdown | decompiled `GameStates`, `LeaveInWorldState`, `ReloadWorldState` | [verified] |
| `GameStates.ClearStaticsOnReturnToMainMenu` exists; whether `ReloadWorld` reaches it is not checked | decompiled `GameStates` | [inferred: menu route clears more] |
| The camera position and target are in the save (`Camera.PersistedData`) and set again at load (`Camera.RestorePersistedState`), so the camera comes back by itself unless `-campos`/`-camtarget` is on the command line | decompiled `Camera` | [verified] |
| The active household, the selected Sim and the clock are save data | game behaviour | [community] |
| Interaction queues are not kept: after a load every Sim starts with an empty queue | game behaviour | [community; checked again by the §2 run] |
| Players are told to restart the game before loading another save, because in-session loads end in Error 12 sooner | forum advice | [community] |
| ts3native samples the largest free block at every world load, at save start and end, every 10 min, and at exit (`UdMemSample` / `UdMemAsk`). It keeps the session low (`g_udLowBigMB`) and warns below 256 MB (`kUdLowBigMB`). C# cannot read any of these yet: the `0x87` GetOption2 text channel only goes from C# to native | `native/ts3native/userdiag.cpp` | [verified] |
| Apex Radiance "Room to Save" (`MemoryGuard`): every 4 s it reads the largest free block and keeps a 128 MB reserve. Below 320 MB it trims idle caches (at most once a minute), below 160 MB it also releases the reserve, and it takes the reserve back once a 640 MB block is free again (and 30 s have passed). Before each world save (hook on the save call `0xAAC320` and its dispatch at `0xAB12AF`) it releases the reserve and trims. It logs `[MemoryGuard] World save done/FAILED (Error 12): largest free block N MB, N MB for the save`. No export, ini value or shared memory gives another DLL its verdict | `game-copy/apex-src/features/memory_guard.cpp`, its `docs/features/performance/room-to-save.md` | [verified: source read] |
| Largest free block on BP: menu 1.69 GB; live 1.53 GB at start and 1.34 GB after 7-30 min; 45-min soak 1533 -> 1211 MB; with routesnap on 1301 -> 1097 MB after 10-16 min; heavy CC (cc-2k) 1447 -> 1231 MB over one stress run (-360 MB from load); vanilla without DXVK 176 MB after a load | docs/66 §2/§7, docs/201 §1/§13, docs/05 | [measured] |
| How much of the largest block a save itself needs: **no measurement**. docs/190 flags an `xfer` peak over 32 MB, or a save start under 300 MB | docs/190 §5 | [gap: closed by §2] |

## 2. Measurement (built, not run)

Queue `tools/harness/queues/save-reload-memory.json` (correctness; `node tools/harness/queue.ts tools/harness/queues/save-reload-memory.json --no-smoke`),
driver `tools/harness/save-reload.ts`, report `node tools/harness/save-reload-report.ts`, agent command `main_menu` (EA Quit
to Main Menu without the dialog), repro `save-reload-menu` (short direct version).

| Job | Flow | Marks |
|---|---|---|
| `bp-save-reload`, `ip-save-reload` | Baseline-*-Spring loaded and only read. 25 min of realistic autopilot at x3 with a CAS visit and a Buy mode visit at 1/3 and 2/3 of the time (the `store-cas-browse` sequence). Then (a) a normal save to the scratch `SaveReload-<world>`, (b) the menu route: save + `main_menu` + `load_save` from the menu, then 10 min of play, (b2) the direct route: save + `load_save reload:true` (EA ReloadWorld), then 10 min of play | `sr-pre`, `sr-save`, `sr-reload`, `sr-reload10`, `sr-direct`, `sr-direct10` |
| `bp-save-reload-control`, `ip-save-reload-control` | (d) fresh game start + load of the scratch save (the file the direct route reloaded), 10 min of play; restart time = now minus the TS3W.exe start (upper bound) | `sr-ctl`, `sr-ctl10` |
| `bp-cc-save-reload` | the BP play job on the cc-2k-v2 heavy-CC set: 15 min of play, menu route only | as BP without `sr-direct*` |

Each mark records the probe's `/memory` (largest free, free, commit, private, mapped, Boehm heap) and the `diag: mem mark` line
([MemDiag] fields `rc`, `gcHeap`, `gcFree`, `miCom`, `eaDir`, `rp`, `pl`, `sp`, `rest`). Every save and every unload + load also
records its **address-space need**: the probe reads `/memory` back to back (~200 ms apart; the probe reads the game from
outside, so it keeps reading while the game thread saves or loads). The need is the largest free block before the phase
minus the lowest value seen during it, plus the commit peak and the lowest total free. A peak shorter than one read can be
missed, so the need is a lower bound [inferred].

The report prints, per world:
- the time of a normal save, the reload cycle by route (save, unload, menu, load) and a full restart;
- every field at every mark;
- **closes** = (reload - before) / (restart - before): the share of a full restart's change the in-process reload reaches.
  It is computed for the menu route (before = `sr-pre`) and the direct route (before = `sr-reload10`, the same file as the control);
- the gap left 10 min later (`sr-reload10` / `sr-direct10` minus `sr-ctl10`).

**Go / no-go for building the feature:**
- **Go (Rodney, 2026-10-30):** any real gain. Even ~10% of the restart gap back is worth building, because memory decides how long a player can keep playing before Error 12; the point is that play only ends at the true limit, not early because of wasted or fragmented allocations. There is no 60% / 30% bar.
- **Stop:** only when the reload gives back nothing measurable (inside run-to-run noise at both marks), or costs more than it returns 10 min later.
- **Either way:** the fields show which owner keeps its memory over a reload (rc, gcHeap, eaDir, mapped, pools), and that owner gets its own release step in the Compaction (an Apex-area item, coordinated with Apex, issue #32).

The faster route that still closes as much becomes the Compaction route (expected: direct, unless the menu route's statics clear is the difference).

## 3. Trigger and threshold

### 3.1 Signal

The signal is the **largest free block**, read by ts3native's existing memory worker at save start (`UdSaveStart` ->
`UdMemAsk`, a ~30 ms VirtualQuery walk off the sim thread). C# gets it through a new op on the `0x87` channel: op 6 =
"Compaction verdict for the save that just started", returning 0 (no), 1 (yes) or 2 (yes, but the last Compaction did
not help, see §3.4). Native decides; ts3fixes only asks.

The verdict uses the reading **after** Apex's pre-save release when Apex is loaded. ts3native detects Apex already
(`ApexStandDown`). With Apex, the verdict reads again at the first save phase line (`UdPhase`, after the save call Apex
hooks), so Apex's 128 MB reserve is counted as free in both our verdict and its log line. Ordering check:
Apex's hook on `0xAAC320` must run before our phase hook. The hook-site map (`tools/hook-sites.ts --check`) shows whether
the two share an address [inferred until checked].

### 3.2 Threshold from data

`T_on = need_save + need_reload + margin`:
- `need_save` = the p95 of the save needs in §2 (BP, IP, CC; scaled up for larger saves by the save-folder size ratio, since save work grows with the world);
- `need_reload` = the unload + load need (the load builds the new world before all of the old one is returned [inferred]: the §2 run shows whether the low point falls in the load);
- `margin` = 128 MB, Apex's reserve size, so with Apex both tools act at the same distance from the edge.

Placeholder until §2 runs: `T_on = 320 MB`, which equals Apex's `kLowBytes` and sits just above ts3native's 256 MB warning
and docs/190's 300 MB save flag. With Apex installed, `T_on = max(ours, 320)`, so we never compact while Apex still thinks
the save has room, and Apex's cache trim gets the first try. If §2 shows `need_save + need_reload` above ~190 MB, `T_on` goes
up for both. In that case Apex is told (PR or issue #32 note), because a save that starts at 320 MB and needs more than that
fails with their guard alone.

### 3.3 Hysteresis

A Compaction is armed only when all of these hold:
1. The largest free block at save start is under `T_on`.
2. It is the first Compaction this session, or since the last one the largest free block has been back above `T_on + 256 MB` (armed again) and then fell under `T_on`.
3. At least 60 min of play have passed since the last Compaction or the last world load.
4. No travel, no Edit Town, no CAS/Build/Buy and no emergency (fire, burglar: EA's own disaster save prompt) is open (the same states EA refuses for Quit to Main Menu).

### 3.4 Never when it does not help

After each Compaction, ts3native compares the largest free block after the load with `T_on`.
- If it is not at least `T_on + 256 MB`, Compaction is off for the session. The next saves under `T_on` show one line: "Memory is still low after compacting; restart the game to get the most room."
- Example: vanilla without DXVK sits at ~176 MB right after a fresh load (docs/05). There a Compaction can never help, and the first one tells the player to install DXVK (docs/02) instead of looping.

### 3.5 How often it would fire (estimate, [inferred] from the §1 numbers)

| Setup | Largest free after load | Loss over play | Hours to reach 320 MB | Compactions |
|---|---|---|---|---|
| BP, our stack (DXVK, release ini) | ~1.3-1.5 GB | ~7 MB/min at first, slowing (1533 -> 1211 in 45 min) | ~3-5 h | 0 in a normal session; ~1 in a 4-6 h one |
| IP / large saves | lower start [inferred: ~1.1-1.3 GB] | similar or faster | ~2.5-4 h | ~1 per long session |
| Heavy CC (cc-2k, many travel trips) | ~1.2-1.4 GB | -360 MB per CC stress run, -80 MB per travel round trip | ~1.5-3 h | ~1 per 2 h |
| Vanilla, no DXVK | ~176 MB | n/a | at load | 1, then off (§3.4) |

The run data that replaces this table: the 45-min soaks' periodic `diag: mem` lines (every 5-10 min) give the slope. The
§2 jobs give the start value per world, and `bp-cc-save-reload` the heavy-CC point. `save-reload-report.ts` gets a
`--fire-estimate` section once the numbers exist (not built: data first).

## 4. The hidden reload flow

1. **Save.** The player saves as usual: Save, Save As, or EA's autosave via `OptionsModel.SaveGame(... forceUseSaveName)`. ts3fixes wraps `OptionsModel.SaveGameTask`: after the game's own `SaveGameSynchronous` returns, it asks native op 6. A failed save (result false, or native `save: result` not 1) never compacts.
2. **Record** (sim thread, between the save and the unload; §5): game speed (`Gameflow.CurrentGameSpeed`), the open main panel (HUD state: Sim info, inventory, relationships...), the selected Sim's id, the camera (as a check only, since the save already holds it) and the interaction queues of visible and nearby Sims. This all goes to a small file next to the save (`<save>.compact.json` in the ts3fixes data folder; never inside the save, so a crash leaves the save untouched).
3. **Screen.** EA's own loading screen (`LoadingScreenController.LoadSaveGameLoadingScreen(metadata)`: the save's thumbnail and tips) comes up at once, with the title text replaced by "Compacting... (your game is saved)". The game shows this screen on every load anyway, so nothing new is drawn over D3D and DXVK/Apex/ReShade cannot conflict. A "screenshot of the last frame" overlay was rejected: it needs a D3D9 present hook (Apex owns that area, docs/187) and a frozen image for ~1-2 min looks like a hang.
4. **Unload + load.** `GameStates.TransitionToGameStateReloadWorld(file, metadata, LiveMode, force: true)`, the path EA's in-game Load uses. No main menu is shown and no dialog: the save just finished, so EA's "save first?" prompt does not apply. If §2 shows the menu route closes clearly more, use `TransitionToLeaveInWorld` plus a `load_save` from the `MainMenu` state's entry instead, with the loading screen held up the whole time (the menu UI is never drawn).
5. **Restore** (ts3fixes `OnWorldLoadFinished`, file present):
   - the camera is already restored from the save; check it within 2 m, else `CameraController.SetPositionAndTarget`;
   - the selected Sim: `PlumbBob.ForceSelectActor` if the save picked another one;
   - the queues (§5);
   - the open panel;
   - game speed is set last, after the queues are pushed.
6. **Drop the screen**, delete the file, and show one notification: "Memory compacted: +N MB of headroom".

Where to hook the save end: in managed code, around `OptionsModel.SaveGameTask` (async saves) and in the `SaveGame(synchronous: true)` callers (Quit and Save, Save and Travel). The Compaction runs only from the task path: a synchronous save is always followed by EA's own quit, travel or move, which unloads anyway. Native already has the save end (`savewrite.cpp` "save: result"), and the verdict is final at save start, so no new native hook is needed.

## 5. Visible and nearby Sims: interaction queues

**Scope.** Sims the player can see or would notice: the active household, Sims on the active lot, and Sims within 30 m of the camera target. This uses the ts3fixes unseen rule (`UfWhy`, docs/202) for "seen". Off-screen Sims are not touched: after a load they pick new autonomous actions, which nobody can see.

**Record per Sim** (`InteractionQueue` list, head first, max 8):
- definition type name and its `InteractionDefinition` key (the same key `InteractionObjectPair` uses);
- target: Sim (`SimDescriptionId`), world object (object type + lot id + position rounded to 0.1 m, since object ids are not stable across loads [inferred]), lot (`LotId`) or terrain point;
- priority, autonomous flag, cancellable flag, and whether it is the running head.

**Re-push after load** (one sim tick after the world is up, before the game speed comes back):
1. Look up the definition: its singleton, or `CreateInstance` from the stored type through the same reflection path the agent's `do_interaction` uses.
2. Find the target again: Sims by description id; objects by lot + type + nearest within 0.5 m.
3. Run the game's own `Test`. Push only if it passes: `InteractionQueue.Add` with the stored priority and flags.
4. The running head is pushed first. It restarts from the beginning: losing the middle of an animation is accepted (Rodney 2026-10-30).

**Cannot be restored:**
- the middle of any running interaction: the animation, a half-cooked meal and other progress that is not save data;
- interactions whose definition needs a pie-menu path or picker answers (dialogs, `GetPath` with player choices), anything mid-dialog, and social chains (only the head social is pushed; the conversation state starts fresh);
- routing in progress, so Sims walk again;
- interactions a mod made from its own private definitions when the mod's type cannot be found by name (skipped and counted);
- situations and visitor scripts that EA rebuilds at load in its own way (services, parties: EA's load handles them);
- the exact simulation state seconds after the save. It matches the save itself: the same as a manual load.

## 6. Failure handling

| Failure | What the player sees | What we do |
|---|---|---|
| Save fails | EA's normal save error | No Compaction (the verdict is only acted on after a result of 1) |
| The verdict channel is missing (ts3native absent or an old version) | Nothing; a normal save | ts3fixes treats op 6 = 0 |
| The reload fails to start (state refuses) | The loading screen never appears; the game keeps running | Log it, notify "Compaction skipped", Compaction off for the session |
| The load fails or crashes mid-way | EA's load error dialog or a crash | The save is complete, because it finished before the unload. The player restarts and loads it: the same outcome as an Error 12 crash, minus the lost progress. The crash report (`[CrashReport]`, docs/135) tags the session with `compaction=1` |
| Error 12 during the reload itself | A crash during the load | Only possible when `T_on` is set too low: §3.2 sizes `T_on` to cover `need_reload`; the §2 data decides it |
| Restore step fails (queue push throws) | The Sim stands idle and picks a new action | Per-Sim try/catch; the counter `compact_requeue_fail` |
| Repeated Compactions do not help | One notification "restart the game" | §3.4 |

## 7. Trigger options (Rodney decides)

1. **Opt-in setting** `[Compaction] Enabled=0` (default off), threshold auto (§3.2). This is the safest first release.
2. **Offered when low:** when a save starts under `T_on`, a two-button dialog asks "Memory is low. Compact now (about N s)?" with the answers Compact / Not now. The player chooses each time; the dialog is shown at most once per §3.3 arm.
3. **Automatic when low** (Rodney's design): no question. This is the default only after option 1 has run cleanly on test installs.

Recommendation: ship 1, plus 2 as the default UI once §2 passes, and move to 3 after a release with no Compaction-related reports.

## 8. Mods

- **NRaas.** ErrorTrap's load-up scan runs again after every load (docs/146: a 0.5-0.9 s tick one sim-minute after the load). Overwatch re-arms its alarms (handled through the world-quit/load events it already uses). StoryProgression rebuilds its managers at load. All of these run on any in-session load, a path EA supports, so nothing new is exercised [inferred]. Our ts3fixes and ts3agent statics already reset on `OnWorldQuit` (the agent's mailbox, the ts3fixes loops are `LoopTask`s since v0.2.4).
- **Mods with static state that never reset.** Their caches survive the reload: memory they hold is not given back, and there is a small risk of stale world references. The `closes` share in §2 is measured with our mod set; `docs/data/script-leak-candidates.csv` (docs/192) lists statics that keep world references. A mod whose statics break after an in-session load is already broken for players who load a second save; this is a compat-matrix note, not a blocker.
- **Apex Radiance.** Memory is their area: the Compaction verdict follows §3.1/§3.2. Their `MemoryGuard` takes its reserve back after the reload once 640 MB are free (by design). Their render caches rebuild at load. Coordinate before release (issue #32): share the §2 data; ask for a read export of the MemoryGuard verdict, or agree on the same 320 MB line.
- **S3SS / S3PP.** Their hooks stay installed across the reload, since the process is the same. S3SS lot streaming and WorldCache settings apply as on any load.

## 9. Effort (wall-clock, with agents; game runs listed separately)

| Step | Work | Runs |
|---|---|---|
| §2 measurement | built (this commit); agent build ~10 min | 1 queue: ~3 h (5 jobs) |
| Decision from §2 data | ~30 min | none |
| Native op 6 + verdict + hysteresis + Apex ordering check | ~1-2 h | selftest |
| ts3fixes Compaction flow (§4: save wrap, record, loading screen + text, ReloadWorld, restore) | ~3-5 h | 2-3 repro runs (BP, IP, CC) |
| Queue record + re-push (§5) with the Test-gated push, counters | ~2-4 h | 2 runs + a mod-set run (NRaas installed) |
| Failure paths, notification texts, `[Compaction]` ini, CHANGELOG/FAQ | ~1-2 h | 1 failure-injection run |
| Save matrix (save-path change: docs/save-change-test-matrix rule) | ~1 h of agent work | the full save matrix |

Total ~9-15 h of work plus ~6-9 game runs (about one night) after the §2 data says go.

## 10. Decisions for Rodney

1. Go/no-go rule in §2: decided 2026-10-30, any measurable gain is a go (no 60% / 30% bar).
2. Trigger option (§7): 1 + 2 first, 3 later?
3. Loading screen with "Compacting..." text (chosen) vs a last-frame screenshot overlay (rejected here: needs a D3D present hook in Apex's area).
4. Talk to Apex about a shared threshold or an export before building (§8).

## 11. Build (2026-10-30, ts3native 0.36.128, not run)

Trigger options 1 and 2 of §7 are built; option 3 (automatic) is not. Everything is off by default.

**Switches** (`ts3native.ini` `[Compaction]`, read at game start; needs `[UserDiag] Enabled=1` and `[SaveWrite] Enabled=1`):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | 0 | 0 off, 1 opt-in (compact after a low save), 2 offered (ask "Compact now?" with Compact / Not now) |
| `ThresholdMB` | 320 | `T_on`; with Apex MemoryGuard on, at least 320 (§3.2) |
| `RearmMB` | 256 | §3.3 (2) and §3.4 |
| `MinMinutes` | 60 | §3.3 (3), real time since the last compaction or world load |
| `Route` | 0 | 0 direct (`TransitionToGameStateReloadWorld`), 1 main menu (`TransitionToLeaveInWorld`, load from `MainMenuState.Startup`) |
| `AfterMinutes` | 10 | the "+N min" memory line |

ts3fixes has one test-only runtime switch, `compactionfail` (agent `patches`): the reload throws after the loading screen is up (§6 failure path).

**Native** (`native/ts3native/compaction.cpp`, selftest `compaction-test.h`). The verdict is op **7** on the `0x87` channel; op 6
was already taken by the played-household store (docs/14 §8.6), so §3.1's "op 6" became op 7. GetOption2 answers a bool,
so numbers (largest free at the verdict, the gain) are read bit by bit. Deviations from §3.1:
- the verdict reads the largest free block **after** the save (one VirtualQuery walk on the sim thread, ~0.5-30 ms, once per
  save), not at save start. Apex releases its reserve before the save and takes it back only once 640 MB are free, so the
  reserve counts as free either way, and no phase hook is needed [inferred from Apex's source, docs/204 §1];
- the save must be this task's (the save count moved since the task began), end with native result 1, within the last minute;
- a declined offer uses the arm up (§7 option 2: at most once per arm);
- the "restart the game" notice comes only after a compaction that did not help (§3.4), never after a failure;
- the "+N min" line is written at the first periodic memory sample at least `AfterMinutes` after the reload (5 or 10 min period).

Log lines: `compaction: on ...` (start), `compaction: verdict COMPACT|no after save #N (result R): largest free X MB, threshold T MB, armed|not armed|off for the session`,
`compaction: begin #N (direct|main menu route), largest free before X MB`, `compaction: done #N after S s: largest free X MB after the reload (before B, gain G MB)`,
`compaction: +N min after #N: largest free X MB ...`, `compaction: skipped or failed (<why>): off for this session`, `compaction: the player chose Not now ...`,
and from ts3fixes `ts3fixes: compaction: restored ...`.

**Managed** (`mods/ts3fixes/src/Compaction.cs`, rules in `CompactionCore.cs`, selftest `tools/selftest/compaction`). Hooks are
installed only when native answers "on" (op 7, 8). `OptionsModel.SaveGameTask` is wrapped (async saves only, §4); the menu
route hooks `MainMenuState.Startup` (if that method was compiled before ts3fixes loads, the hook is missing and the direct
route is used, with a log line).

What is restored (all in ts3fixes memory, nothing in the save and no sidecar file: the process survives the reload, so the
record lives in a static field and is dropped if the next load is another file or more than 10 min later):

| Item | How | Gaps |
|---|---|---|
| Camera | the save restores it; set again only when the target is > 2 m off | none known |
| Selected Sim | `PlumbBob.ForceSelectActor` when the save picked another | none known |
| Queues of visible Sims | active household, Sims on the active lot, Sims within 30 m of the camera target (`CompactionCore.InScope`; the docs/202 `UfWhy` rule is not used); max 8 entries, head first; the definition's static `Singleton` field is kept and read again at the restore (a mod may replace it at load); target Sim by description id, lot by id, object by type + lot + nearest within 0.5 m; the game's own `Test`, then `InteractionQueue.Add` | skipped and counted (`compaction_not_restorable`): immediate interactions, picker answers, pie-menu paths (`GetPath` not empty, e.g. most socials), definitions without a static singleton, terrain targets (GoHere points: the hit point is not kept by the instance); the running head restarts from the beginning |
| Open HUD panel | `HudController.InfoState` / `SetInfoState` | other windows (dialogs, Edit Town) are never open: those states refuse |
| Game speed | `ClockSpeedModel.CurrentGameSpeed`, set last | none known |

Loading screen: EA's own save loading screen (`LoadingScreenController.LoadSaveGameLoadingScreen`) is shown before the
transition, the way EA's travel start does it (`EnableSceneDraw(false)`, screen, `Simulator.Sleep(0)`); the text is the
household-name line of a fresh `SaveGameMetadata` ("Compacting... (your game is saved)") [inferred: the screen survives the
world unload as it does on travel; the verify queue checks it].

Refused states (no verdict asked, the arm stays): not in live mode (Build/Buy/CAS/Edit Town), travelling, travel data
(vacation, university, future), another town, a move, a modal dialog, an emergency (`HudModel.CheckForEmergency`).

**Counters** (ts3fixes, `docs/registry.md` `fixes-427`, `fixes-439`): 427 saves seen, 428 started, 429 offered, 430 accepted,
431 declined, 432 refused by state, 433 skipped / failed, 434 queue entries recorded, 435 re-pushed, 436 not pushed (Test
failed, target or Sim gone, queue refused), 437 not restorable, 438 pushes that threw, 439 restores finished (agent names
`compaction_*`).

**Tests** (none run): native selftest `TestCompaction` (verdict rules, Apex threshold, rearm, 60 min, did-it-help, notice,
bit answers); `node tools/selftest/compaction/run.ts`; repros `compaction-optin-bp|ip`, `compaction-offer-accept-bp|ip`,
`compaction-offer-decline-bp|ip`, `compaction-refused-build-bp|ip`, `compaction-abort-bp|ip`, `compaction-menu-bp`; queue
`tools/harness/queues/compaction-verify.json` (those 11 + the save matrix on BP (with travel) and IP with the hooks on but never
firing, `ThresholdMB=1`). Run: `node tools/harness/queue.ts tools/harness/queues/compaction-verify.json --no-smoke` with
`game-copy/ts3native.asi.compact1` and this branch's ts3fixes / ts3agent.

**Risks** [inferred, the verify queue decides]: the loading screen may not survive the unload (then the player sees EA's
normal unload, as on an in-game Load); queue re-push can push an interaction the game would not have chosen at that moment
(Test-gated, same priority and autonomous flag); the menu route may draw the main menu for a frame; mods with statics that
never reset keep their memory (§8); a crash during the reload loses nothing (the save finished first) but `[CrashReport]` does
not tag `compaction=1` yet (§6; not built).
