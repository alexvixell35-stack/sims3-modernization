# 607: World Adventures tomb and travel registries hunt (s74)

2026-11-17, static, package r64, NOT RUN. Branch `work/pr405-1114`. Scope: the WA registries not covered by docs/574
(skills, careers), docs/210 (free vacations), docs/560 (travel flow) and docs/587 (EP sweep): travel/visa state
(`VisaManager`, `TravelUtil`, `GameStates` travel data and early departures), tomb object registries (`TombRoomManager`,
`TombObjectData` trigger links, `TreasureSpawnPoint`, `Sarcophagus` mummies, traps, secret doors, dig-site spawners,
relics, ancient coins), WA opportunities and Nectar / Martial Arts / Photography.

## 1. Tools run

- **hot-logic-walker** (`tools/static/hot-logic-walker`, `--files` over 186 WA files, 3,828 methods): 10 hits, all
  false or harmless: 4 `REPEATCALL` in `Opportunity.CollectiblesForOpp` / `TryForceRequirements` (rare opportunity
  setup), 4 `NULLAFTERDEREF` (dead null checks), 1 `EVENTNOREMOVE` (`NectarRack.OnStartup` adds to its own
  component's treasure-info event; same object, not persisted, cosmetic).
- **semgrep** (`ea-loop-logic`, `ea-gameplay-logic`, `ea-script-logic`) over the same files: 52 hits, reviewed by hand.
  The `self-compare` hits are EA's `x == null && ...` greyed-tooltip idiom; `sleep-wait-no-exit` hits are UI waits
  (`EnsureNoModalDialogsUp`, loading screen unload) and the interaction-state semaphore, all with real exits;
  `inner-break-outer-continues` in `TombObjectData.ClearLinksToActivatedBehavior` removes one link per trigger, which
  is the intended shape (links are unique per target and behaviour).
- **NRaas** (Chain-Reaction/NRaas, no licence: read for behaviour only) ErrorTrap dereference performers for
  `Sarcophagus.mTombMummy`, `TreasureSpawnPoint.mSpawnedTreasure`, `TombObjectData.mOwner`, `TravelData.mEarlyDepartures`,
  `VisaManager.mValues` pointed at which registries hold dead objects. `[community]` No public fix for the two bugs
  below was found (GitHub code search).
- Gates: build (`--on kHitchCauseHooks`), check-live-map, refs-resolve-check, reflect-members-check, fix-repro-check,
  swap-stack-audit, proven-fixes and the selftest (the remaining failures are the Windows-only csc runners and
  `machine-load` / `night-run`, unchanged).

## 2. tspawninvdup: treasure pickup fires the tomb trigger twice (built, default off)

`[verified, decompiled]` `TreasureSpawnPoint.SetupEventListening` subscribes the spawner to the treasure on it:
`ObjectDisposed` gets `-=` then `+=`, but `ItemComp.OnAddToInventoryEvent` only `+=`. EA calls it from `OnStartup` (also
run on every world load, `GameObject` startup with `postLoad`), from `PlaceTreasure`, and again in the static
`OnTravelComplete` pass right after `SpawnTreasure`. So after every arrival in a vacation world the treasure on each
spawner has two subscriptions (three for one placed by that pass). Pickup runs `OnObjectAddedToInventory` per copy:
`Trigger.RemovedItem` fires two or three times (a `Toggle` or logic-gate input link flips back, `FireOnce`/`Arm` links
run again, `kTombObjectTrigger` is sent again), and `CleanupEventListening`'s single `-=` leaves the rest on the item,
so the empty pedestal fires `RemovedItem` again whenever that relic is added to an inventory later in the visit.
The delegate type is `[Persistable(false)]`, so saves are not affected.

Fix as EA intended (`TreasureSpawn74.cs`, switch `tspawninvdup`, counters 3388-3390): after EA's body, extra copies of
the spawner's own handler are removed so one stays. Household-visible (tomb triggers fire once per pickup for everyone).
Check `TreasureSpawnInvDupCheck` (pure: EA's count after a load plus travel passes vs ours; controls: one setup gives one
copy both ways; EpLive of the swap and the three reflected members). Repro `tspawninvdup-check` (any world; counter 3388
grows on arrival in Egypt/China/France with spawners).

## 3. sarcmummynpc: reset sarcophagus leaves its mummy in NpcHousehold (built, default off)

`[verified, decompiled]` `Sarcophagus.TombMummyCreate` adds the mummy's description to `Household.NpcHousehold`;
`TombMummyDestroy` removes it before the fade-out. `CommonCleanup` (from `Dispose` and `DoReset`) only calls
`mTombMummy.Destroy()`; `Sim.Dispose` clears `CreatedSim` and leaves the description in its household. Each reset or
deletion of a sarcophagus with its mummy out leaves a Sim-less elder mummy in the NPC household of the vacation world
(saved with it), and `mTombMummysTarget` keeps the followed Sim.

Fix (`TreasureSpawn74.cs`, switch `sarcmummynpc`, counters 3391-3393): after EA's cleanup, the live mummy's description
is removed from `NpcHousehold` and the follow target cleared, as `TombMummyDestroy` does. Skipped while the game shuts
down or changes worlds, and only for a description still in `NpcHousehold` (never a household Sim). `CommonCleanup` can
yield (`Sim.Destroy`), so the swap keeps its stub and has a swap-stack-audit row. Check `SarcMummyNpcCheck` (pure, with
controls: no mummy, not in NpcHousehold, world going away). Repro `sarcmummynpc-check`; in play it needs an Egypt tomb.

## 4. Leads checked and dropped

- `TombObjectData.DeactivateTrigger` returns only the last link's result (`result =`, not `|=`); only `LogicGate`'s
  output bookkeeping reads it. Low impact, not built.
- `AncientCoinPile.OnStartup` rolls `mCointCount` again on every load (the debug set value is lost). Could be design;
  `OnAddToInventoryEvent` there is added once per load (not persisted), so no double coins.
- `PerformEarlyDepartureToHomeWorld` adds the last Sim to `mEarlyDepartures` while still in the household;
  `Household.AddSim` is guarded, so no duplicate member.
- `VisaManager.MergeVisaData` skips `mLastTimeOnVacation`, but it has no callers.
- `TreasureChest` / `TreasureComponent` subscribe `GameStates.TravelComplete` in `OnStartup`; `ClearTravelStatics`
  nulls the event on each world change, so no growth.
- Dig-site spawners: `ParseData` and `SpawnObject` are fine; the empty-pick bug is docs/582 `digsiteemptypick`.

## 5. Next

s74 pairs (`tspawninvdup TreasureSpawnInvDupCheck`, `sarcmummynpc SarcMummyNpcCheck`): off BROKEN / on FIXED, pure,
any world. Counter evidence for §2 needs an Egypt arrival with a spawner treasure picked up (3388 > 0, the pedestal's
trigger fires once).

Registry note: the contributor slot range (2000-2799) is full; slots 3388-3393 were pinned with `--at` past the other
sessions' 2800-3387 reservations and `kCounterSlots` went from 2800 to 3400. A plain `registry.ts reserve fixes`
(no `--contributor`) handed out 2000-2002 twice in this checkout (it read the "2000+" row as the maintainer maximum);
those two rows were removed by hand.
