# 336. deadrefdelta + deadrefslimuse: holder index for the deadrefsweep object step (LC-5)

Status 2026-11-23: **built + selftests, queues written and dry-run, NOT RUN.** Package LC-5 of
[docs/333](333-loop-cost-scaling.md) §6, full scope: the hybrid index and the slim use-list walk. Branch
`feature/lc5-reverse-index-1123` off master cd3070f8a. Ledger §590 (first build) and §592 (hybrid + slim + leaks).
**LC-6a (§9, branch `feature/lc6a-perlot-lists-1123`, ledger §594):** the 17 direct use-list writers are pinned, so
`deadrefslimuse` is exact; `deadrefperlot` (default OFF) gathers the typed phases from per-lot lists instead of the snapshot.
History: build 1 (8fe5d7047, `lc5delta1`) indexed all holders in one set; build 2 (this text, `lc5delta2`) keys them by
what dies and adds the slim use walk.

## 1. What it does

The deadrefsweep object pass (docs/330, `DrObjectStep`) runs four holder bodies on every world object (~11k on BP / IP / SV)
whenever a Sim was destroyed or a description disposed since the last walk: phase 10 use / reference / routing-reference
lists, phase 11 owner component (`OwnerBody`), phase 12 relationship `ObjectDisposed` handlers (`HandlerBody`), phase 14
cooking handlers (`CookingBody`).

`deadrefdelta` (default **OFF**, sub-switch of `deadrefcadence`) keeps a hybrid index (`DeltaIndex` in `DeadRefsCore.cs`):

| Part | Key | Stepped when | Members |
|---|---|---|---|
| by Sim | owner / thief Sim `ObjectId` | `kSimDestroyed` names that Sim | objects whose owner component names it |
| by description | `SimDescriptionId` | `kSimDescriptionDisposed` names it, or one of its relationships decays (`GameObjectRelationship.RemoveRelationship`, hooked) | objects with a relationship handler of that description |
| unkeyed | - | every delta pass | objects with a cooking handler (a process lets go of the object through 6 unhooked field writes), holders whose entry was already gone when noted (owner Sim already destroyed, relationship already gone) |
| use (with `deadrefslimuse`) | - | every delta pass | objects with a non-empty use, reference or routing-reference list |

On an event-driven pass with a seeded index (`DeadRefsCore.UseIndex`), `DrDeltaBegin` takes the marked keys and the unkeyed set
(`TakeHolders`), and the use set (`TakeUse`). `DrDeltaStep` runs phases 11, 12, 14 only on the taken holders, phase 10 on the
use set (on every object when `deadrefslimuse` is off), and notes again what each stepped object still holds. With
`deadrefslimuse` on, the pass builds its item list from the taken ids only (`DrDeltaItems`); otherwise it walks the snapshot.
The first pass after a load, the 24 h floor (`kDeadRefObjHours`), any pass with an unseeded index and a change of the slim
setting walk every object with the old `DrObjectStep` and re-seed (`BeginSeed` ... `EndSeed`; marks made during the walk stay).

`deadrefslimuse` (default **OFF**, needs `deadrefdelta`) is a sub-switch so the slim use walk can be A/B'd and switched off
alone if its unhooked writers matter (§3).

Code: `mods/ts3fixes/src/DeadRefsDelta.cs`, wiring in `DeadRefs.cs` (`DeadRefSweepRun`, `DrObjectStep`, `StartDeadRefSweep`,
the two event listeners, `DrGiveReward`).

## 2. Index writers [verified decompiled]

| Holder | Writer hooked | Why that is every writer |
|---|---|---|
| owner component | `GameObject.SetStealActors`, `GameObject.SetRewardGiver` (existing `RewardGiverHook`, plus our `DrGiveReward` repro seed), `ObjectOwnerComponent.ImportContent` | the only `AddComponent<ObjectOwnerComponent>` sites are in those two methods; `ObjectStolen` is called only from `SetStealActors`; import covers lot / household placement and travel; the relink in `OwnerBody` is re-noted by the step |
| relationship handler | `Vehicle.GetNewRelationship`, `MinorPetBirdLarge.GetNewRelationship`; decay: `GameObjectRelationship.RemoveRelationship` (the `CarRelationship` override calls base) | the only creators of `CarRelationship` / `BirdRelationship` (the base ctor is the only `+=`); the other list removals are `SimDescription` load fixup (a load re-seeds) and the object's own disposed handler (the object is gone) |
| cooking handler | `CookingProcess.AddObjectListeners` (chains with `CookClickListener`'s hook) | the only `+=` of `ObjectClickedOnDestroyed`; letting go is unhooked, so these objects stay unkeyed |
| use lists | `GameObject.AddToUseList` (chains with `BSlUseHook`; overrides that call base reach it), `ServingContainer.AddToUseList` (adds without base, along the stacking slot), `AddToReferenceList`, `AddToRoutingReferenceList` | not every writer: ~17 other direct `ActorsUsingMe.Add` sites (philosopher stone, picnic blanket, MP3 player, Bonehilda coffin, gnubb, shuffleboard, terrarium, subway, chicken coop, FV register, ice cream maker x2, lemonade stand x3, playpen, teppanyaki) |

Objects restored from a save carry their holders without a writer: the first pass after a load is a full seed walk. Events:
`kSimDestroyed` is sent with the Sim as target (`Sim.cs:54808`), `kSimDescriptionDisposed` as a `SimDescriptionEvent`
(`SimDescription.cs:4040`); both listeners already exist (docs/330) and now also mark the index. A hook call with a switch off
resets the index (the write is lost to it), so the next pass walks.

## 3. Exactness and household Sims

The index only chooses which objects the unchanged bodies run on; no body or gone test changes. For the holders (phases 11, 12,
14) every way an object can come to hold a gone entry is covered: a Sim destroyed (by-Sim key, event), a description disposed
or a relationship decayed (by-desc key, event or hook), a cooking process letting go (unkeyed, every pass), anything already
gone when noted (unkeyed). So delta passes remove the same entries as the full walk, household Sims included. **No household
exemption is needed and none is added: results for controlled Sims equal the full walk, and the full walk's results are the
released deadrefsweep's.** The remaining gaps are events EA drops (`EventTracker` null, an exception before the send) and
other mods' writers; the 24 h walk takes those, as before LC-5, and verify counts them.

`deadrefslimuse` is not exact on its own: entries written by the ~17 unhooked EA sites (§2) are cleaned by the next full walk
(24 h floor, next load) instead of the next event pass. That is still at least what vanilla does (EA never cleans them).
Verify reports them as `deadrefdelta_verify_use_miss`; if they show up, either hook the busiest sites or keep
`deadrefslimuse` off (`deadrefdelta` alone is exact).

Proof aids:
- `deadrefdeltaverify` (test only, default OFF): a delta pass walks every object; an object the delta pass would skip is tested
  with the read-only `UseListGone` / `OwnerGone` / `HandlerGone` / `CookingGone` counts first; a hit is logged (first 5),
  counted (`_verify_miss` holders, must stay 0; `_verify_use_miss` use lists), and cleaned, so the verify arm removes what the
  full walk removes. Indexed objects the snapshot does not hold are counted (`_verify_outside_snapshot`).
- `tools/selftest/deadrefs`: unit cases (add, duplicates, Sim destroyed with 0 / many holders, relink, description disposed or
  decayed, prune, load reset, a mark during the seed walk, both caps) and a model world (300 rounds): owner / thief with
  relink, relationships with decay and dispose, cooking going stale without a hook, use lists, hooked and unhooked writers,
  object destruction, reloads, switch toggles. After every pass each live object must equal what the full walk leaves
  (compared state by state); without a bypass writer verify finds no miss.

## 4. Memory: no leaks

- **No strong references.** Keys and members are ids (`ObjectId.Value`, `SimDescriptionId`); the index never keeps a Sim,
  description or object alive. The per-pass dictionaries (`sDrHolders`, `sDrUseTaken`, `sDrVerifyLeft`) are dropped at the
  end of phase 10 and when a pass stops.
- **Removal.** A destroyed Sim's or disposed description's key is removed when its pass takes it; the unkeyed and use sets are
  emptied by every delta pass and refilled only from live objects that still hold something; every delta pass prunes ids of
  dead objects from all four sets first (`Prune`, one `GameObject.GetObject` per entry). A holder that lets go is dropped
  when stepped.
- **Reset.** Every world load (`StartDeadRefSweep`, which travel and quit-to-another-save pass through) empties the index;
  a seed walk starts from empty; a switch turned off resets it.
- **Bounds.** More than `kPendingMax` (4096) marks, or more entries than 2 x live objects + 1024, reset the index (the next
  pass walks every object). Entries under a live Sim's key that an object no longer holds (an owner changed by a hooked
  writer) stay until that Sim dies, a reset, or the 24 h re-seed; the cap bounds them.
- **Checks.** Counters: entries now and high-water (`_index`, `_high_entries`, `_high_use`), per part (`_by_sim`, `_by_desc`,
  `_unkeyed`, `_use_set`), pruned ids, resets at load, overflow resets. Verify adds `_verify_stale` (entries naming a dead
  object right after the prune, must stay 0) and `_verify_over_live` (passes with more entries than the snapshot has objects,
  must stay 0). Selftest: destroy-heavy churn (20k ops) keeps the index under 4 x live objects with no stale entry after any
  pass; 50 load / quit cycles leave 0 entries.

## 5. Switches and counters

| Switch | Default | Meaning |
|---|---|---|
| `deadrefdelta` (`kDeadRefDelta`) | OFF | event passes step owner / handler / cooking holders from the hybrid index |
| `deadrefslimuse` (`kDeadRefSlimUse`) | OFF | delta passes walk only the use set (needs `deadrefdelta`) |
| `deadrefdeltaverify` | OFF, test only | delta passes walk every object and compare |

Slots 1671-1698 (registry `fixes-1671`, `fixes-1683`; list in the `DeadRefsDelta.cs` header). Agent names `deadrefdelta_*`.

## 6. Per-pass work and expected gain [inferred until the counters run]

| | Full step (released) | Build 1 (`lc5delta1`) | Build 2 (`lc5delta2`) |
|---|---|---|---|
| holder objects checked per event pass | every object, ~11k | every holder object (owner components + relationship + cooking; hundreds, `_index`) | per destroyed Sim: the objects it owns or stole (typically 0-3); per disposed / decayed description: its car or bird objects (0-2); plus the unkeyed set (appliances with cooking listeners, a few to tens) |
| objects walked by phase 10 | ~11k | ~11k | the use set: objects with a Sim in a use / reference / routing list (~1-3 per active Sim, ~100-300 on BP / IP / SV), plus the taken holders |
| per object | 3.65 us (docs/333 §9.1) | ~1-1.5 us (use list + lookup) | ~2-3 us on the few stepped objects; one `GetObject` per indexed id for the prune |

So a delta pass drops from ~40 ms (11k x 3.65 us) to roughly 1 ms on today's worlds, and it grows with the Sims that die and
the Sims using objects, not with world content. docs/333 §6 scaling case: ~25-30 ms/s at 4x and ~65-80 at 10x for "p10 to a
slim walk"; build 2 removes the slim walk's own linear term too, leaving the world snapshot (still built for phases 5-9) and
the 24 h walk. Measured by `_from_keys`, `_from_unkeyed`, `_slim_walked` / `_slim_passes` and the phase-10 ms per pass.

## 7. Builds and queues

- `ts3fixes.package.lc5-reverse-index-1123b` (copy `game-copy/ts3fixes.package.lc5delta2`) and
  `ts3agent.package.lc5-reverse-index-1123b` (`game-copy/ts3agent.package.lc5delta2`): see `docs/data/builds.json`. Build 1
  (`lc5delta1`) stays registered for reference.
- `tools/harness/queues/lc5-delta-correct-1123.json` (correctness, any machine, ~3 h 35 min, 15 jobs): BP smoke; BP / IP / SV
  10 min natural play with all three on (household-untouched counters, a seed walk, >= 1 delta pass, verify >= 1000 objects,
  0 holder misses, leak gates `_verify_stale<=0`, `_verify_over_live<=0`, `_overflows<=0`, `_resets>=1`); BP 10 min without
  verify (the real slim path, `_slim_passes>=1`); the eight docs/330 deadref repros with all three on
  (`bp-deadrefs-saves` saves and reloads in one session: `_resets>=2` shows the index reset at the reload; `bp-deadrefs-check` has neither step).
- `tools/harness/queues/lc5-delta-time-1123.json` (TIMING, needs quiet machine, ~1 h 50 min): smoke, then BP / IP / SV 25 min
  in-load ABBA `--ab deadrefdelta` with `deadrefslimuse` on in both arms. Each on slice starts unseeded (the off arm resets the
  index), so its first pass is a seed walk: the on arm understates the gain.
- ON rule (docs/333 §6): correctness PASS + no timing regression + the §6 scaling case. Decide `deadrefslimuse` on the
  `_verify_use_miss` count.

## 8. Risks

- Natural 10-min runs may see no Sim destroyed after the seed walk: `deadrefdelta_passes>=1` then fails the job as hollow, not
  as wrong (rerun longer or with the agent `sweep`).
- Unhooked use-list writers (§3) delay those cleanups to the 24 h walk with `deadrefslimuse` on.
- Ten hooks are always patched and pass through with the switches off (one `ShortcutOn` read per call, and none while the
  index is unseeded). `AddToUseList` and `AddObjectListeners` chain with existing hooks.
- Index entries for objects outside `GetObjects<GameObject>` would be stepped by the slim path but not by the full walk;
  verify counts them (`_verify_outside_snapshot`) [inferred: the snapshot holds every script object, out-of-world ones too,
  since the docs/519 repro jigs made out of world are cleaned by the full walk].

## 9. LC-6a: exact slim use (pins) and per-lot gather (`deadrefperlot`)

Status 2026-11-23: **built + selftests, queues written and dry-run, NOT RUN.** Branch `feature/lc6a-perlot-lists-1123` off
`feature/lc5-reverse-index-1123` (cc4fec2d9). Ledger §594. Slots 1707-1726 (`fixes-1707`). Build `lc6a1`. This section
supersedes §2's "not every writer" row, §3's `deadrefslimuse` caveat and §8's unhooked-writer risk.

### 9.1 Part 2 first: every direct use-list writer pinned (`deadrefslimuse` exact by construction)

The 17 direct `ActorsUsingMe.Add` sites of §2 [verified decompiled: the only `ActorsUsingMe.Add` / `mActorsUsingMe.Add`
outside `GameObject.AddToUseList` and `ServingContainer.AddToUseList`; no `Insert` / `AddRange`, no write through an alias
variable, no reassignment of the list field; `ReferenceList` and `RoutingReferenceList` are written only by the two hooked
helpers] are all hooked (`DeadRefsPin.cs`). None is skipped: no site was proven dead.

Why pins and not "note after the write": most hooked methods are long interaction `Run`s or call methods the call graph says
can yield, so a delta pass can run between the write and any later note. A **pin** (`DeadRefsCore.UsePins`) closes that
window without yield analysis: on entry the hook resolves the objects the method may write and opens a pin for the running
task (`Simulator.CurrentTask`); every object created in that task while the pin is open joins it (`GameObject.Init` hook:
`Init` is a sealed override every GameObject runs once [verified decompiled]); every delta pass steps all pinned ids; on
return (`finally`) the ids are noted once more and the normal re-note (non-empty list) takes over. A pin whose task died
without returning is noted once and dropped at the next pass (or every 256 opens). A pin over 256 ids, or more than 4096
open pins, switches to "all": while it is open, and for one pass after it closes, the delta pass walks every object for the
use lists (exact, slower; counted).

| Site (decompiled) | Hook | Pinned at entry | Created inside (Init attaches) |
|---|---|---|---|
| PhilosopherStone.TransmuteGold.Run | `DrPinStoneHook` | target + its slot object | - |
| PicnicPosture ctor (only creator: PicnicBasket.SitDown.Run) | `DrPinPicnicHook` | basket + `Blanket` | - |
| PortableMp3Player.StartMusic | `DrPinMp3Hook` | the player | - |
| BonehildaCoffin.SpawnBonehilda | `DrPinBonehildaHook` | the coffin | - |
| GnubbField.GetTeam, Shuffleboard.GetTeam | `DrPinGnubbHook`, `DrPinShuffleHook` | the field / board | - |
| Terrarium.RadiationExperiment.OnEpicSuccess | `DrPinTerrariumHook` | target | the new terrarium |
| Subway.PlayForTips.StartPlayingForTips | `DrPinSubwayHook` | `mInstrument` | - |
| ChickenCoopClassic.HarvestEggs.Run | `DrPinCoopHook` | target | the egg prop |
| FVShoppingRegister.TendRegister.Run | `DrPinFvHook` | target | the fruit prop |
| IceCreamMaker.MakeDeepFriedIceCreamFritter.Run (2 sites) | `DrPinIceCreamHook` | maker + right-hand serving container | - |
| LemonadeStand.GetPsychiatricHelp / Purchase / Sell.Run | `DrPinLemon*Hook` | stand (+ `mChair`) | new chair, juice cup, fruit prop |
| PlaypenClassic.InPenIdle.Run | `DrPinPlaypenHook` | the playpen | - |
| TeppanyakiGrill.TGCook.Run | `DrPinTeppanyakiHook` | target | the bowl prop |

Guard: `tools/uselist-writers.ts` (selftest-all, local: needs the call-graph index) lists every game method that touches a
use / reference / routing list **and** calls a list `Add` / `Insert` / `AddRange`: 55 today, 20 hooked, 1 covered through its
only caller (the PicnicPosture constructor, checked against the index's callers), 34 reviewed readers (they add to a list of
their own; decompiled check). A new candidate (a game update), a stale row, a hook missing from the source or a new caller
of a "via" row fails it. Swap audit: 19 new rows, 17 can yield; all have stubs (SavedStacks remap, docs/242).

`deadrefdelta_verify_use_miss` must now be 0: the queues gate it (`<=0`) on every verify job.

### 9.2 Part 1: per-lot gather (`deadrefperlot`, default OFF)

The typed phases (5 Sims, 6 fish, 7 cars / boats, 8 horseshoe courts, 9 dartboards, 13 / 15 Sims) gather from a per-lot index
(`DeadRefsCore.PerLotIndex`, ids only: lot id -> object id -> kind bits, plus object id -> lot) instead of filtering the
world snapshot. Hooks: `GameObject.Init` (add), `GameObject.Destroy` (drop once the world no longer resolves the id),
`GameObject.InternalOnSetLotCurrent` (lot key; all 10 EA overrides call base [verified decompiled]). A gather walks every
lot (LC-6a: all lots are live), resolves each id once (`GameObject.GetObject`), drops ids the world no longer resolves and
keeps destroyed objects the world still lists (as the snapshot does: phases 7, 13 and 15 act on those too). Every snapshot a
pass takes anyway (first pass after a load, 24 h floor, unseeded holder index, slim off, verify) re-seeds the index in one
classify loop (which also replaces the five type filters), so the floor walk catches any bypass writer. Lot keys use
`InternalLotCurrent` (the field the hook sets); carried / inventory objects keep their own lot field, which matters only
once lots park (LC-6).

Verify (`deadrefdeltaverify`): the per-lot gather is compared with the snapshot's typed lists as id sets per kind
(`_verify_miss`: in the snapshot, not per-lot; `_verify_extra`: the reverse; both must be 0); the pass then uses the snapshot
lists.

Not in LC-6a (waits for FO-4 / CP-2): parking and the live-lot filter (`PerLotIndex.Walk(lotLive, ...)` is the hook point),
dropping a lot's list on a CP-2 `OnLotLoad` remove (CP-2 is lane A, not merged; LC-6a stays self-contained and needs no lot
event: objects on streamed-out lots stay script objects), the far-model skip.

### 9.3 No leaks

- Pins: ids only, keyed by task id; removed at return, dead tasks pruned every delta pass and every 256 opens, reset at load;
  bounded by running writers (caps 4096 pins / 256 ids each turn into "all" mode, not growth). Selftest: 60k random ops with
  nested writers, killed tasks, overflow and loads: 0 use misses after every pass (a mutation that skips the pinned ids
  fails with 694); all pins drain; 50 load / quit cycles leave 0.
- Per-lot index: ids only; dropped on destroy, pruned at every gather, reset at load (`DrIdxLoadReset`: every world load,
  travel and quit-to-another-save pass through it), at switch-off and when over live objects + 1024; empty lot buckets are
  removed. Counters: entries now / high-water / lots (`deadrefperlot_index`, `_high_entries`, `_lots`) and verify
  `_verify_over_live` (entries > snapshot objects, must stay 0). Selftest: 80k random ops (create, destroy hooked / unhooked /
  still listed, lot moves, reseeds, loads): every gather equals the snapshot's typed set, lot keys follow moves, size <= the
  indexed live objects; 50 load / quit cycles leave 0 entries.

### 9.4 Switches and counters

| Switch | Default | Meaning |
|---|---|---|
| `deadrefperlot` (`kDeadRefPerLot`) | OFF | typed phases gather from per-lot lists (needs `deadrefcadence`) |
| pins | always (no switch) | bookkeeping only; they change what `deadrefslimuse` steps, nothing else |

[1707] pin opens, [1708] pinned ids stepped, [1709] objects created inside a pin, [1710] open pins, [1711] high-water,
[1712] pins dropped (task died), [1713] "all" passes, [1714] pin overflows (agent `deadrefpin_*`); [1715] per-lot gathers,
[1716] objects gathered, [1717] entries, [1718] high-water, [1719] lots, [1720] seeds, [1721] hook notes, [1722] drops,
[1723] verify miss, [1724] verify extra, [1725] verify over-live, [1726] last gather us (agent `deadrefperlot_*`).

### 9.5 Household Sims

Neither part changes what a pass removes: pins only add objects to the slim walk (bodies and gone tests unchanged), and the
per-lot gather hands the phases the same items as the snapshot (verify checks the set equality). Results for controlled Sims
equal the full walk's, so no household exemption is needed (as in §3).

### 9.6 Per-pass cost [inferred until the counters run]

| Typed gather per sim-group pass | 1x (SV, ~11.5k objects) | 10x (~115k) |
|---|---:|---:|
| today: snapshot + copy ~3-3.5 ms (p5 gather measured 6.0-6.5 ms with its filter), four more filters ~2.7 ms each (docs/333 §9.2) | ~17 ms | ~170 ms (first slice ~60 ms) |
| `deadrefperlot`: one walk of the typed members (~0.5-1k at 1x), one `GetObject` each at ~0.5 us [inferred] | ~0.3-0.7 ms | ~3-7 ms (Sims and cars scale with the world) |
| passes that still take the snapshot (seed, 24 h floor, slim off, verify) | snapshot + one classify loop (below today's five filters) | same |

Measured by `deadrefperlot_gather_us`, `deadrefcadence_snapshots` and the phase 5-9 ms per arm.

### 9.7 Builds and queues

- `ts3fixes.package.lc6a-perlot-lists-1123` (copy `game-copy/ts3fixes.package.lc6a1`) and `ts3agent.package.lc6a-perlot-lists-1123`
  (`game-copy/ts3agent.package.lc6a1`): `docs/data/builds.json`.
- `tools/harness/queues/lc6a-perlot-correct-1123.json` (correctness, any machine, ~3 h 15 min, 13 jobs): the LC-5 queue with
  `deadrefperlot` added to every job (bundle style): smoke; BP / IP / SV 10 min with verify (holder and use misses 0, per-lot
  miss / extra / over-live 0, the LC-5 leak gates); BP 10 min without verify (`deadrefperlot_gathers>=1`); the eight docs/330
  repros (`bp-deadrefs-saves` reloads: `deadrefdelta_resets>=2`, `deadrefperlot_seeds>=2`).
  Audit additions 2026-11-23 (repros UNVERIFIED, never run): `bp-lc6a-buy-edit` (repro `lc6a-buy-edit-verify`: a dartboard and a car
  moved live, moved again inside Buy mode and sold in Buy mode, per-lot verify miss / extra 0, `deadrefperlot_gathers>=2`; the Buy-mode
  move needs agent `ts3agent.package.lc6a2-1123`: `move_object` in Buy mode replays the hand tool's script callbacks in native order,
  `LotManager.OnHandToolPickup` (kPickupTypeGrabbed), SetPosition, `OnHandToolMovement`, `OnHandToolEdit` (kEditTypePlaced) [verified static,
  World.cs / LotManager.cs]; the native hand tool's own state is not touched [inferred: the game's footprint and lot updates come from SetPosition
  as in live mode]) and `bp-lc6a-save-in-hook` (repro `lc6a-save-in-hook`: LemonadeStand Sell started, save mid-body, reload, 0 script
  errors, `deadrefpin_opens>=1`, `deadrefpin_open<=0` after the load).
- `tools/harness/queues/lc6a-perlot-time-1123.json` (TIMING, needs quiet machine, ~1 h 50 min): smoke, then BP / IP / SV 25 min
  in-load ABBA `--ab deadrefperlot` with `deadrefdelta` + `deadrefslimuse` on in both arms.
- ON rule (docs/333 §6): correctness PASS + no timing regression.

### 9.8 Risks

- 17 of the 19 new swaps sit on methods that can yield (interaction `Run`s): a save taken inside one holds our hook frame;
  covered by the stubs and the SavedStacks remap (docs/242), like the 600+ existing yielding swaps; the save-reload repro
  exercises it only by chance.
- `GameObject.Init` / `Destroy` / `InternalOnSetLotCurrent` are hot: with the switch off each costs a field test.
- A long writer (playpen idle, register tending) keeps its pinned objects in every slim pass while it runs (a handful of
  objects per running writer).

## 10. LC-7: phase 4 from a reverse index (`deadrefdescrev`)

Built 2026-11-23 (branch `feature/lc7-desc-reverse-1123` off LC-6a d42198361 + the pause gate ff85f8311), NOT RUN.
Ledger §601. Slots 1744-1757.

### 10.1 Measured gain basis (M-1, `loopcost-measure-1123`, 10-min natural play, not timing) [measured]

| | BP | IP | SV |
|---|---:|---:|---:|
| p4 ms / items (household descriptions) | 166 / 24 393 | 194 / 27 751 | 201 / 19 819 |
| p1 + p2 + p3 ms (stay event-gated) | 68 | 60 | 91 |
| Sim-group runs (passes - skipped) | 91 / 112 | 111 / 131 | 89 / 106 |
| `lcm_ev_descdisposed` / `lcm_ev_simdestroyed` | 96 / 340 | 75 / 306 | 61 / 225 |
| sweep total ms | 6 030 | 7 297 | 8 515 |

p4 costs ~6.8-10 us per description and runs on almost every Sim-group run, because a Sim is destroyed every ~2 s of
play. p4 is ~0.28-0.34 ms/s at 1x (2.4-2.8% of the sweep now) and linear in descriptions x runs: ~3 ms/s at 10x before
any rise in the event rate [inferred]. After LC-5 / LC-6 remove p10 and the gathers, p1-p4 are the 75% of the remainder
that docs/333 §3.2 names. LC-7 attacks p4 only. p1-p3 stay event-gated as designed: they are 60-91 ms per 10 min and walk
dictionaries that have no cheaper key.

### 10.2 Design

- `DescRevIndex<SimDescription>` (DeadRefsCore.cs) wraps a `DeltaIndex`. Each holder SimDescriptionId is filed under the
  SimDescriptionId it references (coworker, FormerBoss, chess challenger) or under the ObjectId of the pregnancy dad Sim.
  A holder whose reference is already gone when it is noted goes into the unkeyed set. The holder objects are kept so a
  pass needs no `SimDescription.Find`, which walks every household.
- `kSimDestroyed` / `kSimDescriptionDisposed` (the existing listeners) mark the key. A disposed holder leaves the map at
  its own event.
- Phase 4 with the switch on: the first run after a load, an unseeded index, an index over its cap, or a run 4 sim hours
  after the last full run (`kDeadRefIdleHours`, the docs/333 floor) walks every description and seeds. Every other run
  takes the holders of the marked keys plus the unkeyed set and steps only those that are in the walk
  (`Household.EverySimDescription` membership, tested per holder). The body is `DescriptionHolders`, unchanged.
  Stepped holders are noted again.
- Writers hooked (post) [verified decompiled, every assembly grepped]: `Occupation.AddCoworker(SimDescription)` (no
  override), `School.SetCoworkersAndBoss` and `AcademicCareer.SetUpStudent` (direct `Coworkers.Add` on both sides: the
  owner and each coworker are noted again), `Career.SetBoss`, `Occupation.ImportContent` (FormerBoss),
  `LogicSkill.FindSuitableChessRankOpponent` (the only challenger writer; Batch591Logic invokes it, so the hook runs there
  too), `Pregnancy.StartInternal(Sim, SimDescription)` / `StartFromMerge` (the public field `SimDescription.Pregnancy` is
  set there), `Pregnancy.Continue`, `PetPregnancy.Continue` (`CheckForDad` is not swapped: DeadRefs.cs invokes it by reflection; its EA caller `StartFromMerge` is hooked). A Career clone starts from the
  static template, which has no references [inferred, I-601-2]. Mods write these public fields without the hooks, so the
  4 h full walk stays as the floor.
- Pause: the hooks are bookkeeping and run in any mode. Phase 4 runs only inside the sweep, which waits while the game is
  frozen (`SimFrozenOrNotLive`, DrWaitWhileFrozen) and stops outside live mode.
- Household Sims are included. This is an exact speedup: the same body runs, on the holders that can have changed.
- The #100 empty-outfit count (c[392]) is updated by full and verify runs only.

### 10.3 Verify (`deadrefdeltaverify`, test only)

On a delta run every description is walked. A **miss** is a description outside the taken holders that holds a gone
reference no event of this run explains (`DdGoneRefs`: the read-only tests of `DescriptionHolders`, less keys marked
since the take). An **extra** is a taken holder in a walk household that the walk never reached. Both are logged (first
5) and counted, and both must stay 0. A miss is still cleaned by the walk, so verify runs behave like the full sweep.

### 10.4 Leaks

Holder objects drop at their dispose event. `Prune` runs every delta run and drops gone holders plus every entry
naming an id no longer held. Any index reset (pending overflow, cap of 2 x descriptions + 1024, switch off at a hook
call) clears the holder map too. The index resets at every world load (`DdLoadReset`). A stopped phase 4 resets it.
Gauges: entries [1746], holders [1747], high-water [1757]. Offline selftest (tools/selftest/deadrefs): seed, take,
dispose-drop, prune, overflow-clears-map, and a 50-cycle churn that ends at 0 holders and 0 entries.

### 10.5 Switches and counters

`deadrefdescrev` (`kDeadRefDescRev`, default OFF; needs deadrefsweep + deadrefcadence). Agent names `deadrefdescrev_*`:
[1744] `runs` (delta runs), [1745] `stepped`, [1746] `index`, [1747] `holders`, [1748] `notes`, [1749] `seeds` (full
runs), [1750] `verify_checked`, [1751] `verify_miss`, [1752] `verify_extra`, [1753] `unseeded_off`, [1754] `pruned`,
[1755] `resets`, [1756] `overflows`, [1757] `high`.

### 10.6 Builds and queues

- `ts3fixes.package.lc7-desc-reverse-1123` (copy `game-copy/ts3fixes.package.lc7a`), agent
  `ts3agent.package.lc7-desc-reverse-1123` (copy `game-copy/ts3agent.package.lc7a`).
- `lc7-desc-correct-1124` (correctness, any machine, ~2 h): smoke; BP / IP / SV 10 min with verify; BP real delta
  path; save-reload (`bp-deadrefs-saves`); travel round trip x3 (`leak-travel-roundtrip-x3`); 60 s pause
  (`pause-cas-buy-check`: no slice counter moves).
- `lc7-desc-time-1124` (timing, needs a quiet machine, NOT scheduled): `--ab deadrefdescrev` with LC-5 / LC-6a on in both
  arms, BP / IP / SV 25 min ABBA after a smoke.
- Expected [inferred]: with the 4 h floor, p4 drops by ~70% (about 1 full run in 4 Sim-group runs), to ~0.08-0.1 ms/s
  at 1x and ~0.8-1 ms/s at 10x. With a 24 h floor (as LC-5 uses for objects) it would drop by ~96%. That is a decision
  for Rodney after the correctness run.

### 10.7 Risks

- A writer missed by the census, a mod writer, or an exception before an event send leaves a gone reference until the
  next 4 h walk. That is the same delay the cadence floor already allows. Verify counts it.
- Verify extras can show falsely when a household joins the walk while phase 4 is in progress (rare).
- `Occupation.ImportContent` is hooked on the base class. A career override that skips `base` writes no FormerBoss, so
  it needs no note.

## 11. LC-7b: no writer missed, by construction (writer guard)

Built 2026-11-23 on `feature/lc7-desc-reverse-1123` after an independent static audit found writers the LC-7 hooks
miss. Ledger §602. Slots 1758-1769. Same switch (`deadrefdescrev`, default OFF); no new switch.

### 11.1 Gaps and status

| Gap | Writer | Fix |
|---|---|---|
| G1 (EA) [verified decompiled] | `CareerManager.ImportContent` -> `ImportJobContent` -> `Occupation.ImportContent` writes FormerBoss before `mJob` / `mSchool` are assigned, so the hook read the old job (bin / library placement, mod imports) | the hook files the owner **unkeyed** (`DdNoteHeld`): the next delta run steps it and notes the keys of the job it holds by then. Exact whatever order the caller assigns in. Counter [1768] |
| Pregnancy start | EA `Pregnancy.StartInternal` (hooked) and NRaas `Pregnancies.Start` (Woohooer, SP, MasterController copies) both send `kGotPregnant` after the assignment [verified source] | listener (`OnDdPregEvent`, registered with the sweep's other listeners at load) notes the actor and the pregnancy's mother. Counter [1767] |
| M1-M6 (NRaas) | NewBossScenario.SetBoss, ChessPushScenario.FindNextOpponent, AttachLostFathersScenario, Pregnancies.Start, MaleHaveBabyHomeEx, PregnancyProxy.CopyPregnancy, FindJob / UpdateCareer / coworker scenarios | the writer scan (§11.2) finds each and marks its kind untrusted. No NRaas method is hooked: their signatures carry NRaas types our hook classes cannot name, and a body swap of a method already compiled is refused |
| T1 (ours) | `CompatResolveChecks.BirthModRepro` set `SimDescription.Pregnancy` | `DdSetPregnancy` (write + note); `TakeARiskCheck` swaps a career's Coworkers and restores them, then notes the owner |
| Unknown mods (the other 321) | any | the writer scan |

### 11.2 Writer scan (`DescWriterScan.cs`)

Once per process, at the first world load with `deadrefdescrev` on (under the load screen; the result is kept, assemblies
do not change after start): every loaded assembly that is not EA's core (AutShadow `kGameModules`) and references
`Sims3GameplaySystems` is read with the reader AutShadow's G-M6 hashes use in game (`MethodBase.GetMethodBody` +
`GetILAsByteArray`, `Module.ResolveMember`, `AutShadowCore.Operand` / `MemberName`). Our own assembly is scanned too,
less its hook classes (their stubs hold EA's original bodies; `GetMethods` on them was the `0x58D9B6` assert, docs/242)
and three reviewed methods (`DescriptionHolders`: removals only; `DdSetPregnancy`; `TakeARiskCheck`).

Targets: `Occupation.Coworkers` (list), `Occupation.FormerBoss`, `LogicSkill.mChessRankingNextChallenger`,
`SimDescription.Pregnancy`, `Pregnancy.mDad`. All five are fields; none has a setter [verified decompiled]. A method writes
a kind when it has:
- `stfld` / `stsfld` of a target, unless the value is a plain `ldnull` (a removal; not when the store is a branch target);
- `ldflda` of a target, unless the ref is only read (`ldind.ref`), cleared (`initobj`, `ldnull; stind.ref`) or passed to a
  callee that does no more (one call deep: non-virtual, has IL, outside EA's core, every use of that argument checked);
- a load of the Coworkers list not consumed at once by a null test, `pop`, a read-only or removing `List` /
  `ICollection` / `IEnumerable` member (removals cannot make a holder hold a reference the index lacks), the `List`
  copy constructor, or a callee that does no more (as above). Kept in a local, returned, `dup`'d: a write.

Fail-safe: a method that must have IL but gives none, a token that does not resolve outside a generic context, a type
list or method list that cannot be read, or a scan exception = that method / assembly writes **every kind**. TS3's
mscorlib has no `ResolveMember` with a generic context: a token that fails inside a generic type or method is taken as a
member of a generic type, never a target (their declaring types are not generic) [inferred, I-602-2].

Untrusted kind: every delta run steps that kind on every description of the walk (`DescriptionHolders(d, kinds)`), the
taken holders get every kind as before; verify counts misses only for trusted kinds. All four untrusted = the full walk on
every run (as with the switch off). Not seen by the scan: writes through reflection (`FieldInfo.SetValue` by name) and IL
emitted at run time; the 4 h full walk and verify stay for those (§11.4).

Log: one `deadrefdescrev scan:` line with assemblies, methods, unreadable, us and the untrusted kinds, then up to 40
`<assembly> <Type::Method> writes <kinds>` lines (no paths). Counters [1758]-[1766].

### 11.3 What the scan finds offline (`node tools/static/desc-writer-scan.ts`, .NET Framework, not the game) [measured]

| Set | Methods | Time | Untrusted (writers) |
|---|---:|---:|---|
| ts3fixes (lc7b, hook classes skipped) + ts3agent plugin | 7 692 | ~300 ms | none (3 ts3fixes methods unreadable offline only: `ElCache` implements EA's private `Lot.IObjectCache` through the publicized reference) |
| NRaas ErrorTrap + Overwatch (the test install) | 4 293 | ~200 ms | coworkers: ErrorTrap `DerefOccupation.Perform` hands Coworkers to its generic `Remove<S>(List<S>)`, whose `List<!!0>` members do not resolve without a generic context. FormerBoss / chess / mDad clears through `Remove<S>(ref S)` pass the callee rule. Offline-only unreadables: `InteractionInjectorList.Perform` (EA private nested type), ErrorTrap `OnPreLoad` (its own ScriptCore), Overwatch `YieldAllAlarms.OnYieldAll` (token 0x0a0003e9, `AlarmManager.mTimerQueue`, does not resolve on .NET) |
| SP core + modules + Woohooer + MasterController + GoHere + Dreamer | ~29 000 | ~1 s | coworkers, formerboss, chess, pregnancy (SP + Career + Skill + Woohooer; see §11.1) |

In game the scan costs ~20-60 us per method [inferred from AutShadow G-M4's 65-110 ms for ~690 methods, which also
hashes every token; I-602-3]: ~0.25-0.7 s for the test install, ~0.7-2 s with SP + Woohooer, once per process at the
first load with the switch on. Counter [1766] measures it.

### 11.4 Exactness

Every way a description can come to hold a reference: an EA writer (hooked, or the import hold), a pregnancy start (event),
a foreign IL writer (its kind untrusted: walked every run), a load (seed walk), reflection / emitted IL (not seen: 4 h
walk, verify). The last row is the one residual and is the class verify was built for.

### 11.5 Queue changes

`lc7-desc-correct-1124` and `lc7-desc-time-1124` pinned to lc7b. Every `deadrefdescrev` job of the correctness queue
also requires `deadrefdescrev_scan_assemblies>=2`, `_scan_unreadable<=0`, `_untrusted<=1` (test install: at most
coworkers). New job `bp-lc7-nraas` (set `lc7-nraas-1123`: SP core + Career module + Woohooer; repro `lc7-modwrite`: NPC
FormerBoss written by reflection, NPC pregnancy through NRaas `Pregnancies.Start`, the referenced NPC disposed): untrusted
mask 11 (coworkers + formerboss + pregnancy, chess trusted), writers found, `_preg_notes>=1`, `_kind_steps>=1`, delta runs,
verify miss 0 / extra 0.

### 11.6 Risks

- If TS3 Mono cannot read or resolve a method the scan needs (Overwatch `OnYieldAll` is the candidate), the fail-safe makes
  every kind untrusted and LC-7 is the full walk for that install. Resolved in §12 (LC-7c): a missing member names no
  target; other read failures stay fail-safe.
- With NRaas StoryProgression (very common) most kinds are untrusted, so the gain shrinks to what the kind walks save over
  the full `DescriptionHolders` (no outfit counts, no chess lookup when chess is trusted). Not measured.
- Mono keeps reflection objects it created for the scan (MemberInfo caches): a one-time RAM cost, not measured.

## 12. LC-7c: pinned NRaas writers, unresolvable tokens, reflection guard

Built 2026-11-23 on `feature/lc7-desc-reverse-1123` (Rodney approved the three options of §11.6). Ledger §602
(I-602-6 to I-602-9). Slots 1770-1775. Same switch (`deadrefdescrev`, default OFF); no new switch. Build lc7c.

### 12.1 What changed in the scan (`DescWriterScan.cs`)

| Rule | Why it is exact |
|---|---|
| A token whose member or type is missing names no target; the instruction is skipped, the rest of the method is still scanned. "Missing" = a member / type table token (TypeRef, TypeDef, Field, Method, MemberRef, TypeSpec, MethodSpec) that `Module.ResolveMember` rejects with `ArgumentException` (not `ArgumentOutOfRangeException`; offline also .NET's Missing* / TypeLoadException). Any other failure stays fail-safe (every kind). Counter [1770] | the instruction cannot run, so it writes nothing. TS3's mscorlib carries `resolve_token_exception` / `ResolveTokenError` [verified strings]; the exception mapping is Mono 1.2.3's `Module.cs` [community]; TS3 runs Mono's interpreter, which transforms a whole method at its first call (`transform.c`, Mono 1.2.3 [community], docs/29) and resolves the same token there [inferred, I-602-6]. Per-instruction instead of the whole method: exact whether Mono refuses the method or throws at the instruction |
| `newobj List`1::.ctor()` (or `.ctor(int)`) straight into `stfld` (not a branch target) is no write | a fresh empty list holds no reference; a list kept in a local and filled later still counts (`dup` / `stloc` before the store) |
| Argument loads between the list and its consumer may be `ldfld` on an argument and calls that take only values pushed after the list (stack-depth walk, 12 instructions) | such a call never receives the list; its own body is scanned (or is EA's, hooked) |
| A list passed as the source of `List`1.AddRange` / `InsertRange` is read | BCL semantics: copies from the argument, does not change it |
| Reflection guard: an indexed field name (`Coworkers`, `FormerBoss`, `mChessRankingNextChallenger`, `Pregnancy`, `mDad`) as an `ldstr` in a method that also calls `Type.GetField` / `GetProperty` / `GetMember` or `FieldInfo` / `PropertyInfo.SetValue` / `SetValueDirect` makes that kind untrusted; any `Type.InvokeMember` or `System.Reflection.Emit` member makes every kind untrusted. Counter [1771] | narrows the §11.4 residual to names built at run time and code emitted by an unscanned assembly |

Our `Fixes..cctor` is allowed (reviewed): its `GetField("mDad")` is `sPregDad`, whose only `SetValue` is the removal in
`DescriptionHolders`. The repro's FormerBoss write no longer uses reflection (§12.4).

### 12.2 Pinned writers

A pin names assembly, type, method and signature (`AutShadowCore.SigKey`) and the IL hash `AutShadowCore.MethodIlHash`
gives that method (the same hash G-M6 uses; `node tools/autshadow-il-hashes.ts --hash-file <dll> <SigKey>` prints it
offline). At the scan, before the method is read: hash equal = covered (hook installed, or no hook needed) and not
scanned; hash different = counted [1773] and scanned (its kind untrusted); method or assembly missing = nothing.

| Pin | Writer (source line) | Kind | Cover | Hash |
|---|---|---|---|---|
| M1 SP Career V269 `NewBossScenario.SetBoss(Career, SimDescription)` | `FormerBoss = Boss` (NewBossScenario.cs:225) | formerboss | hook `DdPinSetBossHook`: notes `job.OwnerDescription` | eb5d81662c6a16fc |
| M2 SP Skill V269 `ChessPushScenario.FindNextOpponent(SimDescription)` | challenger (ChessPushScenario.cs:228) | chess | hook `DdPinChessHook`: notes `sim` | ff242d38a28d4b0a |
| M3 SP V269 `AttachLostFathersScenario.PrivateUpdate(ScenarioFrame)` | `pregnancy.mDad` (AttachLostFathersScenario.cs:139) | pregnancy | hook `DdPinLostFatherHook`: notes the scenario's `Sim` (read through the `Sim` property found at install; none = refused) | 0a061f3384c96e95 |
| M4 NRaas Common `Pregnancies.Start(Sim, SimDescription, bool)` (SP V269 and Woohooer V130 copies hash alike) | `SimDescription.Pregnancy`, then `kGotPregnant` (Pregnancies.cs:56-58) | pregnancy | no hook: the §11 listener notes the mother | b250e0d1b00580cc |
| M5 Woohooer V130 `MaleHaveBabyHomeEx.Cleanup()` | `Pregnancy` set to null and back (MaleHaveBabyHomeEx.cs:120-129) | pregnancy | hook `DdPinBabyCleanupHook`: notes `InstanceActor` | d6c3b6cccf19b9b5 |
| M5 Woohooer V130 `PregnancyProxy.CopyPregnancy(Pregnancy, Pregnancy)` | `dst.mDad` (PregnancyProxy.cs:51) | pregnancy | hook `DdPinCopyPregHook`: notes `dst.Mom` | 8f541ac5e93fe2c5 |
| ErrorTrap (test install) `DerefOccupation.Perform(Occupation, FieldInfo, List<ReferenceWrapper>)` | Coworkers to its generic `Remove<S>(List<S>, ...)` (only `RemoveAt`), FormerBoss to `Remove<S>(ref S)` (= default) | coworkers, formerboss | no hook: removals only (read in source) | 54b286e509c5a4d5 |
| M6 NRaas Careers V87 `OmniCareer+CareerOmniValue`1.Set` | `Coworkers` / `FormerBoss` copied (OmniCareer.cs:2280-2281) | coworkers, formerboss | **not pinned**: a method of a generic class has one body per instantiation, a swap of the definition does not reach them; its kinds stay untrusted with NRaas Careers | (082b80cdd982f8ba) |

Hooks are `Fixes.Patch` body swaps, as CompatResolve's hooks on mod methods: the hook classes name NRaas types as
`object`, each notes in a `finally` (a throw after the write still notes). `Patch` refuses a method already compiled
(counter [1774], the kind stays untrusted). Hooks [1772], covered without a hook [1775]. The MasterController copy of
`Pregnancies.Start` hashes differently (f6d8bc515de063fd, not read): it stays scanned.

The SP coworker writers of §11.1 (FindJob `AskForJob`, `UpdateCareerScenario`, `NewCoworkerScenario.IsValid`) store a
fresh empty list; `CoworkerBaseScenario.PrivateUpdate` copies the list (`AddRange` source) and removes through EA's
`RemoveCoworker`; `NewBossScenario.ChooseBoss` calls `Coworkers.Remove(job.Boss)`. None needs a pin under §12.1.

### 12.3 Offline result (`node tools/static/desc-writer-scan.ts`, .NET) [measured]

| Set | Untrusted | Pins |
|---|---|---|
| ts3fixes lc7c + ErrorTrap + Overwatch (test install) | none, except offline-only: ts3fixes `ExMakeCaches` (EA private interface) and Overwatch `OnYieldAll` (I-602-5) | ErrorTrap DerefOccupation covered |
| SP core + Career + Woohooer (set lc7-nraas-1123) | none for SP core and Career; Woohooer's 15 unreadables are .NET type-load failures (sealed parents, EA private types) the game loads | 6 matched (4 hooks, 2 no-hook) |
| + SP Skill | Skill: 3 offline-only unreadables; ChessPush pinned | +1 hook |
| MasterController | pregnancy (its `Pregnancies.Start` copy differs) | 1 mismatch |

Scan time offline: ~1.1 s for SP core + Career + Woohooer + Skill (up from ~0.6 s: strings and MemberRef calls are now
resolved too). In game see I-602-3; [1766] measures it.

### 12.4 Expected mask per mod set (in game)

| Set | Untrusted mask | Pins hooked / no hook / mismatch / refused |
|---|---|---|
| Test install (ts3fixes, ts3agent, ErrorTrap, Overwatch) | 0 [inferred: I-602-5, I-602-8] | 0 / 1 / 0 / 0 |
| + lc7-nraas-1123 (SP core + Career + Woohooer) | 0 [inferred: I-602-7] | 4 / 3 / 0 / 0 |
| + SP Skill | 0 | 5 / 3 / 0 / 0 |
| + MasterController | 8 (pregnancy) | mismatch 1 |
| + NRaas Careers | coworkers + formerboss at least (M6) | |
| An older or newer NRaas build | the changed methods' kinds (mismatch) | |

`bp-lc7-nraas` now requires `_untrusted<=0`, `_pins_hooked>=4`, `_pins_nohook>=3`, `_pins_mismatch<=0`,
`_pins_refused<=0`; the repro's FormerBoss write calls NRaas `NewBossScenario.SetBoss` twice (boss = ref, then back), so
the pinned hook is what keeps verify at 0 misses. Every other deadrefdescrev job requires `_untrusted<=0` and no
mismatch / refusal. The mismatched-hash case is offline: the deadrefs selftest pins `WsSubjects.Stfld` with a wrong hash
and requires FormerBoss untrusted.

### 12.5 Risks

- A pinned hook is refused when SP has already compiled the method at the scan (first load with the switch on). The
  scan runs in `DdLoadReset` under the load screen, before scenarios run [inferred, I-602-9]; [1774] shows it.
- In-game IL hashes of NRaas methods are assumed equal to the offline ones (the G-M6 generator is built to match; NRaas
  types are named the same way). A difference shows as mismatches, the kind stays untrusted: slower, never wrong.
- Reflection by a name built at run time and `GetFields()` loops are still unseen (verify + the 4 h walk).

## 13. LC-7d: popular NRaas covered, job swaps

Built 2026-11-23 on `feature/lc7-desc-reverse-1123` (Rodney: most players use NRaas, cover them). Ledger I-602-10 to
I-602-13. Slots 1776-1777. Same switch (`deadrefdescrev`, default OFF). Build lc7d.

### 13.1 New writer class: job swaps (gap found while covering NRaas)

Phase 4 reads `cm.Occupation` / `cm.School`, that is `CareerManager.mJob` / `mSchool`. A store of a job object that
already holds Coworkers / FormerBoss changes what a description holds without writing an indexed field (NRaas
`SafeStore.Dispose` restores a stored job; `SchoolBooter`, `AssignSchoolScenario`, MasterController `ChooseCareerOption`,
Traveler `MergeTravelInformation`, `CareerManagerEx.ImportContent` swap or move jobs). The scan now treats
`CareerManager::mJob` / `mSchool` stores as coworkers + formerboss writes (`= null` stays a removal). Our code and the test
install have none outside the pinned copies [measured offline]. EA's own `mJob` / `mSchool` stores are core code and not
scanned: EA's job swaps beyond `CareerManager.ImportContent` (G1 hold) are not censused [open, I-602-13].

### 13.2 Coverage per NRaas mod (2026-11-22 downloads, all 45 packages scanned offline; source grep of the NRaas tree for the same fields agrees)

| Mod | Writers of indexed fields | Cover |
|---|---|---|
| StoryProgression core V269 | Pregnancies.Start; AttachLostFathers; SafeStore.Dispose; CareerManagerEx.ImportContent | event; hook (note Sim); hook (note mSim); dirty hook |
| SP Career V269 | NewBossScenario.SetBoss; AssignSchoolScenario.PrivateUpdate (mJob) | hook (note owner); dirty hook |
| SP Skill V269 | ChessPushScenario.FindNextOpponent | hook (note sim) |
| SP Money / Population / Relationship / Extra | none | |
| Woohooer V130 | Pregnancies.Start; MaleHaveBabyHomeEx.Cleanup; PregnancyProxy.CopyPregnancy; SafeStore | event; hooks |
| MasterController V135 | Pregnancies.Start (own IL build f6d8bc515de063fd, same token sequence: store, then `PregnancyEvent` + `SendEvent`); ChooseCareerOption+CareerItem.Perform (mJob); SafeStore | event (no hook); dirty hook; hook |
| MC Cheats / Integration / Progression / ExpandedTattoo | none | |
| Careers V87 | OmniCareer `CareerOmniValue`1.Set` (proxy career only, below); SchoolBooter.Enroll (mJob); SafeStore | no hook; dirty hook; hook |
| Traveler V90 | SimDescriptionEx.MergeTravelInformation(dest, source, bool) (mJob); CareerManagerEx; SafeStore | dirty hooks; hook |
| Mover V28, Porter V42 | CareerManagerEx; SafeStore | dirty hook; hook |
| Register V81, Selector V14, Consigner V22, GoHere V46, PortraitPanel V34, RelationshipPanel V21, ErrorTrap, Overwatch | SafeStore (ErrorTrap also DerefOccupation, removals) | hook (no hook for DerefOccupation) |
| Retuner, Relativity, Hybrid, Shooless, Decensor, Animator, Dresser, Dreamer, Saver, Tagger, Traffic, HomeOpener, Cupcake, DebugEnabler, OnceRead, SecondImage, AntiMagicScroll, PocketProtector | none | |
| Vector | not in the downloads: not checked | |

SafeStore.Dispose has 3 IL builds across 16 copies, CareerManagerEx.ImportContent 4 builds across 4 copies; a key may carry
several pins (one per known build), any equal hash matches.

**OmniCareer (M6), exact without a hook:** `CareerOmniValue<T>` builds its own career (`mCareer = new T()` in its
constructor) and keeps it only in `OmniCareer.mValues`; `Set` copies Boss / Coworkers / FormerBoss onto that proxy. No
CareerManager ever holds a proxy: every `mJob` / `mSchool` store in the NRaas tree is in the table above and none stores a
proxy [verified source]. EA writers called on a proxy (AddCoworker, SetBoss: hooked) note `proxy.OwnerDescription`, the
real owner (`Init` copies it), whose Coworkers list the proxy shares. Option (a), hooking the callers, would cover the same
writes; (b), a hook per closed instantiation, is not possible with a definition swap.

### 13.3 Hooks for many copies (`DeadRefsDescRevPins.cs`)

A body swap needs one hook class per target, so hook classes come in slots per shape (instance no-argument void: 32;
static 4-argument bool: 8; 4 each for the other shapes). NRaas types are `object`; value-type parameters must match
(else no slot, refused). SafeStore hooks note the store's `mSim` (its Dispose restores only that Sim's job / school).
The other job-swap hooks set a dirty mark: the next phase-4 run is a seed walk (every description, exact for any write,
the cost of one full walk) [1776] / [1777].

### 13.4 Expected mask (in game)

| Set | Mask | Hooks / no hook (at least) |
|---|---|---|
| Test install (ErrorTrap, Overwatch) | 0 | 2 / 1 |
| lc7-nraas-1123 (SP core + Career + Woohooer) | 0 | 9 / 3 |
| lc7-nraas-full-1123 (24 popular NRaas packages, docs/data/lc7-nraas-full-1123.json) | 0 | 25 / 5 |
| Any NRaas build other than these downloads | the changed methods' kinds (mismatch) | |

New queue job `bp-lc7-nraas-full` (set lc7-nraas-full-1123): mask 0, `_pins_hooked>=25`, `_pins_nohook>=5`, no
mismatch / refusal, verify 0 / 0. Offline: every package above scans to mask 0 except .NET-only type-load failures
(Careers 22, Woohooer 15, Cupcake 8, Skill 3, Mover 1, DebugEnabler 1, Overwatch 1), which the game loads [I-602-7].

### 13.5 Risks

- SafeStore runs often with SP (moves, MasterController edits): each Dispose re-notes one Sim (cheap). Dirty hooks force a
  seed walk; how often they fire with SP is not measured ([1776]).
- The `SchoolBooter.Enroll` pin uses the `--hash-file` hash; .NET cannot load NRaasCareer's types offline, so the C#
  hash there differs (names missing). The game's hash should equal `--hash-file`'s [I-602-8]; a mismatch only leaves the
  kinds untrusted.

## 14. LC-7e: EA job stores censused (I-602-13 closed)

Built 2026-11-23 on `feature/lc7-desc-reverse-1123`, build lc7e, slots 1778-1779, same switch (default OFF).

### 14.1 Census

IL scan of Sims3GameplaySystems, Sims3GameplayObjects, Sims3StoreObjects (offline `DWS_JOBS=1 node
tools/static/desc-writer-scan.ts <dlls>`: only `CareerManager::mJob` / `mSchool` stores, `= null` counted as removal) and the
callgraph field writers (`fw` of the two fields) agree: four EA methods store a non-null job [verified IL + callgraph]. UI
does not reference Sims3GameplaySystems; GameplayObjects and StoreObjects store none.

| EA store | Callers (callgraph, one hop) | Paths it carries | Cover |
|---|---|---|---|
| `CareerManager.AssignJob(Occupation)` (`mJob = c`) | only `Occupation.AssignOccupation`, called from `Career` / `XpBasedCareer.SetAttributesForNewJob` (every career override funnels there; entered from `CareerManager.AcquireOccupation`) and `Occupation.LeaveJobNow` (null) | new job, job transfer (leave + acquire), rehire of a QuitCareers job object (keeps its Coworkers / FormerBoss), retirement rehire, school-to-career, university / vacation career swaps, OccupationEntryTuning joins (all through AcquireOccupation) | new hook `DdAssignJobHook`: notes the owner after the store |
| `CareerManager.AssignSchool(School)` (`mSchool = school`) | only `School.AssignOccupation` (from the same SetAttributesForNewJob path: `SimDescription.AssignSchool`, age-up, boarding school, moving, imaginary friend, Urnstone) | age-up and school assignment, boarding-school return | new hook `DdAssignSchoolHook`: notes the owner |
| `CareerManager.set_School` | only `School.LeaveJobNow`, stores null | removal | none needed |
| `CareerManager.ImportContent` | load, travel return, CAS import | a job read from the stream | G1 import hold (§11) |
| `CareerManager.OnLoadFixup` | load | null stores only | none needed |

A new `CareerManager` (SimDescription.cs, 4 sites) starts with no job. Travel return and the university / vacation swaps do
not store a live job object outside these: they import (ImportContent) or acquire (AssignJob / AssignSchool) [verified
callgraph: no other `fw` writer]. Noting the owner after the store is exact for any caller: DdNote re-reads what the owner's
job holds.

Side find: Sims3StoreObjects has 15 reflection-guard hits (slot machine, blackjack, poker, roulette, ballet barre,
turnstile: skill reports through reflection). EA core is not scanned in game, so they change nothing; noted for the guard's
false-positive rate.

### 14.2 Dirty seed frequency

`[1778]` sim hours since the first phase-4 run of this load, `[1779]` most dirty-forced seed walks in one sim hour. Agent:
`deadrefdescrev_sim_hours`, `_dirty_max_per_simh`, `_dirty_per_simh_x100` (`[1776]` x 100 / `[1778]`). The lc7 correctness
jobs (smoke, correct, nraas, nraas-full) read them into the verdict; no threshold until a run shows the SP rate.

## 15. LC-7f: the smoke's untrusted mask 3 found (simler90Extras), scan findings logged

Built 2026-11-24 on `feature/lc7-desc-reverse-1123`, build lc7f, no new slots, same switch (default OFF).

### 15.1 Failure

`bp-lc7-smoke` (lc7e, BP test install) failed twice on `deadrefdescrev_untrusted=3`: coworkers + formerboss untrusted,
`_writers_coworkers=1`, `_writers_formerboss=1`, chess / pregnancy 0, 12 assemblies, 12730 methods, 0 unreadable, pins
2 hooked / 1 no-hook / 0 mismatch [verified, results jsonl]. One method wrote both kinds: a `CareerManager.mJob` / `mSchool`
store (LC-7d counts those as both). The finding lines went only to the 40-line ShortcutLog ring.

### 15.2 The writer

Offline scan of every script assembly in the test install's Mods folder (`tools/static/desc-writer-scan.ts`, same
DescWriterScan.cs and pins as the game):

| Assembly | Result |
|---|---|
| simler90Extras (simler90 Gameplay Core Mod UPDATE242) | `simler90Extras.Other::SimDescriptionMergeTravelInfoCareer`: coworkers + formerboss |
| NRaasErrorTrap, NRaasOverwatch, Ts3AgentPlugin, Ts3Fixes (this branch), fp.houseboatfix, LazyDuchess.SmoothPatch | none (offline-only .NET type-load failures aside) |

Counts equal the game's (1 / 1 / 0 / 0) [verified offline; that the game's one writer is this method is inferred from the
match, I-602-15]. Not ours, not a false positive: simler90's `SimDescription.MergeTravelInformation` calls it on travel
return, and it stores `importedsim.CareerManager.mSchool.Clone()` (Coworkers / FormerBoss with it) into `mSchool`, plus job
and retired-career clones into `QuitCareers` / `mRetiredCareer`, which phase 4 does not walk [verified, decompiled offline].
The core-mod's own Sims3GameplaySystems stores mJob / mSchool only where EA does (`DWS_JOBS=1`: set_School, AssignJob,
AssignSchool, ImportContent) [verified offline], so the LC-7e hooks cover it.

### 15.3 Fix

- Pin: `simler90Extras.Other::SimDescriptionMergeTravelInfoCareer(SimDescription,SimDescription)`, hash `c88127ea403a709c`
  (C# hasher and `tools/autshadow-il-hashes.ts --hash-file` agree, all types load offline), hook "Dirty" (new slot shape S2V,
  `DdPinS2V0Hook`): the next phase-4 run after a travel merge seeds. Another simler90 build = hash mismatch = kinds stay
  untrusted (exact, slower).
- Durable evidence: every scan line (summary, each finding, pin mismatch / refused) also goes to ts3native.log
  (`DiagLine`, queued until the diag channel answers, retried on each `DdUntrusted` call). A finding now names
  `IL_<offset> <opcode> <Type::field> <- <member stored from>` or `-> <member passed to>` (up to 2 per method), and the
  reflection call for reflection hits (`DescWriterScan.Hit`, selftest cases in tools/selftest/deadrefs).

### 15.4 Expected (test install, lc7f)

Mask 0, pins 3 hooked / 1 no-hook / 0 mismatch / 0 refused; ts3native.log has `deadrefdescrev scan:` lines. The smoke keeps
`deadrefdescrev_untrusted<=0` (the one third-party writer in that set is now pinned) and adds `_pins_hooked>=3`.

## 16. LC-7g: EA 1.69 travel return stalled with the five switches on (2026-11-24)

### 16.1 Failure

EA app 1.69.47, Bridgeport -> France -> Bridgeport, eafinal2 asi + ini, ts3fixes lc7f: arm E (deadrefdelta, deadrefslimuse,
deadrefperlot, deadrefdescrev, deadrefdeltaverify on) stalled on the first return 3/3; arm C (same package, switches off) and
arm A (vanilla) passed (live 66-67 s after travel_home) [verified, queue lc7-travel-triage-ea-1124]. Stall snapshot: travel
phase EndVacation, `inworld_post_init` false (InWorldState.Startup never got past `ImportTravellingHousehold`), in_travel
true, Pause, household 2/2 in the world, empty Sim and managed stacks, no script error, no ts3fixes error line.

### 16.2 What differs between E and C on the return path

- The deadref hooks are swapped in both arms (ApplyDeadRefs, not switch-gated); with the switches on they only act on a live
  index. Every index (sDrIdx, sDd, sPl) is reset in StartDeadRefSweep on OnWorldLoadFinished, which runs before
  InWorldState.Startup (ToInWorldState.OnWorldLoadFinished), and the sweep refuses while travelling or on vacation
  (DeadRefUnsafe), so during the import every hook returns at its `Live` / `Seeded` test [verified, code]. The stall counters
  agree: descrev runs 0, all verify / sweep counters 0.
- `deadrefdelta_sims_marked` / `_descs_marked` (157 / 343) count kSimDestroyed / kSimDescriptionDisposed events; the listener
  runs in both arms and `DeltaIndex.Mark` returns at once while unseeded. Marking writes only our dictionaries, never a Sim,
  SimDescription or MiniSimDescription [verified, DeadRefsDelta.cs DrIdxMarkEvent, DeadRefsCore.cs Mark].
- The one E-only change that runs inside the import: the writer scan (run at the France load, under the travel load
  screen) body-swapped `simler90Extras.Other.SimDescriptionMergeTravelInfoCareer` (pin hook "Dirty", slot S2V). simler90's
  `SimDescription.MergeTravelInformation(SimDescription)` calls it as its last statement, and EA's
  `ImportTravellingHousehold` calls that for each returning traveller; France arrival never calls it, so the first call of
  the swapped method is the return [verified, decompiled simler90 UPDATE242 and EA GameStates]. A static private method of a
  third-party assembly called from another assembly was never swapped before. That the swap is what stops the import is
  inferred: no stack, no log line (I-602-16).

### 16.3 Fix

- The simler90Extras pin takes the new hook kind "TravelWorld": no swap, counted no-hook (`_pins_nohook`); the method writes
  only when `GameUtils.IsAnyTravelBasedWorld()` (its first test), so in a travel-based world its kinds join the run's
  untrusted mask (DdGather: those kinds walk every description, exact), elsewhere it is covered. The import-time merge needs
  nothing: every load resets the index and the first run seeds. Slot S2V and DdPinS2V0Hook removed.
- The writer scan (and every pin swap) no longer runs in a travel load; the first live sweep scans.
- Our hooks that run in the travel import (Occupation.ImportContent, ObjectOwnerComponent.ImportContent,
  CareerManager.AssignJob / AssignSchool, Pregnancy.StartFromMerge) log `deadref travel step: <hook> enter|done` once per
  travel load, plus `ImportTravellingHousehold load finished` and `PostTravelingFixUp reached (import done)`
  (GameStates.TravelComplete). A throw of our bookkeeping logs `deadref travel FAIL: <hook>: <exception>` once and turns the
  cadence work off for the session (DrCadence false: full walks, as deadrefcadence off). The game's own calls are unchanged.
- No Sim behaviour change: the hooks and the index are bookkeeping only (household rule unaffected).

### 16.4 Verify

Queue `deadref-travel-verify-ea-1124` (EA laptop), repro `travel-roundtrip-bp-lc7on-x2`: a live sweep before each trip
(scan in live BP, `_pins_nohook >= 1` = this build), two trips, each return 120 s then FAIL with the stall snapshot (cleanup).
PASS: 4 `PostTravelingFixUp reached` lines, no `deadref travel FAIL`. Steam: no Steam bp-lc7-travel result found; the Steam
test install has the same simler90Extras hash (pinned there first), so Steam runs the same swap; whether it stalls there is
unknown (EA's 1.69 Mono differs from Steam's, inferred).
