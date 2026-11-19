# 650: own-bug fixes (NPC show alarm, unseen service, alarmcmpnobox start, StreetParking listener)

2026-11-19. Slots 4110-4119 (`kCounterSlots` 4120). Package tag `ownbugs`. Built, NOT RUN. Self-check `tools/selftest/ownbugs` (pure rules in `mods/ts3fixes/src/OwnBugsCore.cs`).

## §1 EA alarm guard: our hooks no longer let EA's body kill a repeating alarm

**Evidence [verified, logs]:** `alarmkilllog: repeating alarm removed after its callback threw: Ts3Fixes.Fixes+NpcShowHook.CreateNPCShow` in phase-20261119-1345 (day 13 hour 1.30) and phase-20261118-0645 (day 5 hour 1.27). `npcshowstuck` was on in those runs. The alarm fires at 1:00. `alarmtime` lines show the callback ending `yield`, so EA's body sleeps and throws about 17 sim-minutes later. The exception itself was never logged.

**Cause [verified, code]:** `NpcShowHook.CreateHook` (ShowtimeLogic.cs) guards only its own `npcshowstuck` part, then calls `CreateOrig()` (EA's `NPCShowManager.CreateNPCShow`) unguarded. The hook is patched whether or not the switch is on. When EA's body throws, AlarmManager removes the daily "NPC Show Alarm", so no NPC show or tip performer is scheduled for the rest of the session.

**Where EA throws [inferred, decompiled; not yet confirmed by a log]:** in `GetRandomNPC`'s townie branch, EA instantiates a random townie on a random lot, quits its old performance career and calls `AcquireOccupation` without checking the result. Then it reads `NPCSim.Occupation.HighestLevel` (careerLow false), `NPCSim.CareerManager.Occupation.PromoteSim()` (level > 1), and in `CreateNPCShow` `NPCSim.OccupationAsPerformanceCareer.CreateNPCGig`. When `AcquireOccupation` returns false, or `Instantiate` returns null (`CreateObjectWithOverrides` failed, then `NPCSim.CareerManager`), each of these is a NullReferenceException. This is EA's code, not ours: none of our hooks on the path (`sppensionprompt` on AcquireOccupation, `residentspawnslot` / `unseenoutfitdefer` on Instantiate) changes a result there. The new log line names the real type and frame on the next run.

**Fix:** EA's body now runs in try/catch inside the hook. `ResetException` is rethrown. Any other exception is counted, and `EaAlarmThrew` logs `eaalarmguard: <site> threw <type> at <first frame>[, inner ...], alarm kept, day D hour H` (capped at 24 lines; first frame capped at 160 chars, `OwnBugsCore.FirstFrame`). The alarm survives. No switch: this is a crash guard that does not change a result. A throw that happens in EA's vanilla code would kill the alarm in vanilla too; for the household this is an EA bug fix (the alarm keeps running as EA intended).

**Other hooks with the same pattern [verified, grep]:** our `Patch` targets were matched against EA's `AddAlarmDay` / `AddAlarmRepeating` callbacks in the decompiled code. These hooks also called the EA original of a repeating alarm unguarded. All are guarded the same way now (counter 4111):

| Hook | EA alarm callback | Alarm |
|---|---|---|
| `B13PtHook.EvalHook` (Batch593Logic.cs, plantdormanttime) | `Plant.EvaluatePlantGrowth` | daily, per plant, AlwaysPersisted |
| `LotCleanupHook.CleanHook` (EaSpread.cs, easpreadlots off / verify) | `LotManager.CleanUpLotsAndInventories` | daily venue cleanup |
| `DecayHook.RunHook` (EaSpread.cs, easpreaddecay off) | `SocialComponent.RelationshipDecay` | daily relationship decay |
| `BEp3St1.MagicHook` / `BEp3St2.MagicHook` (EpBatch3Logic.cs) | `PotionShopConsignmentRegister` / `BotShopRegister.OnMagicHourAlarm` | daily magic hour |
| `BEgHook.CreateHook` (EpBatch1bLogic.cs, elvisgnomeguard / cap) | `MagicGnomeElvis.TryToCreateElvisGnome` | daily |

Not guarded: one-shot alarm callbacks (AlarmManager removes them after the call anyway), and our own spread tasks (`LotCleanupTask`, `DecayTask`), which already run our code. EA callbacks that alarmkilllog lists under EA's own name (EvilMagicGnomeUtil.TryToMailEvilGnome 74, PotionShopConsignmentRegister.OnDailyAlarm 74, LunarCycleManager.TriggerLunarEvents 69, UnicornPool.SpawnAlarmCallBack 67, MagicGnomeFall / Winter) are not under a hook of ours and are out of scope here.

**Counters:** [4110] NPC show throws caught, [4111] other guarded alarm callbacks' throws caught. Agent `patches`: `eaalarmguard_npcshow_caught`, `eaalarmguard_other_caught`.

## §2 unseenservice: drinks never served; failure step logged, cleanup in a finally

**Evidence [verified, fix_counters in leak-*.json, 15 runs phase-20261115..19 with the switch on]:** conversations served 289 + 29 (tier 1 / tier 2), food 92 + 10, **drinks 0 + 0**, errors [3610] 186. phase-20261117-1703: 65 conversations, 24 food, 0 drinks, 35 errors. phase-20261117-1537: 23 + 6 tier 2, 0 drinks, 8 errors. So every in-scope OrderDrink that reached `SvcServe` failed. Each one fell back to EA's linked hand-off, which is the slow path the fix was meant to skip. The ~30-40 % error share is the drink share of the orders.

**Cause [inferred, decompiled; the new log confirms it on the next run]:** the drink path did not keep EA's order. `Bartending.CreateGlassOutOfWorld` makes the glass with `CreateObjectOutOfWorld` (not in the world). `SvcServe` then called `Glass.OnPoured()` on that glass (geometry state, drink and fizz effects parented to the glass's FX joint), then `ShouldMakeFlamingDrink`, then `ParentToSlot` to the destination. EA never pours a glass that is out of the world. `MakeDrink` pours in the server's hand (the glass is in the world after `CreateDrinksAndSetActors` parents it to the hand). `CreateRoundsOfDrinks` calls `OnPoured` only after the tray is placed. Food worked because `Recipe.CreateFinishedFood` uses `CreateObject(..., Vector3.OutOfWorld)`, which is an in-world object, and nothing is poured. There was also no record of which step failed: a `ParentToSlot` false and a throw both just counted [3610].

**Fix:**
- The drink is placed first, with `ParentToSlot(dest, slot, ref returnCode)` (`EnsureConsistencyWithParent` adds it to the world). It is poured and set on fire after that, as in EA.
- Every step is named (`OwnBugsCore.kSvcStep*`). The first 16 failures are logged as `unseenservice: <conversation|drink|food> failed at <step>: <type> at <first frame>` (the reflection wrapper is unwrapped to EA's inner exception), or `ParentToSlot <return code>` / `nothing made`. Errors outside `SvcServe` (scope, preconditions) are logged with step `scope or preconditions`.
- A `finally` ends commodity updates that were begun but not ended (`EndCommodityUpdates(false)`, [4113]). It also destroys a glass or plate that was made but never reached the slot, or that was placed while a later step before the customer's `RunBehavior` threw ([4112]). EA's hand-off that follows then finds the slot empty. Once `RunBehavior` has run, the object is EA's to handle. The server's instance is always unlinked (as before).
- Scope, tiers and household rules are unchanged (household, selected and selectable Sims are tier 0 = EA's hand-off).

**Counters:** [4112] made objects destroyed after a failed service, [4113] commodity updates ended after a throw, [4114] failed services logged by step. [3610] keeps its meaning (errors).
