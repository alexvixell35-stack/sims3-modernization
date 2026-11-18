# 615: scan triage pass 2: spoil broadcaster, weather stone listener, birthday age-up record (s85)

2026-11-17, static (ilspycmd IL of game-copy/assemblies), r73, built, NOT RUN.

Input: the five pass-2 hits of the 2026-11-17 scan triage. Each was checked in IL before building: three are real and
built default off, two are false (§4). Code: `mods/ts3fixes/src/Scan85.cs`; repros `<switch>-check`; EpLive rows;
swap-stack-audit rows.

## 1. spoilbroadcast (off, 3476-3478)

[verified, IL `PreparedFood::ForceSpoil` IL_00fb/IL_0100] `newobj ReactionBroadcaster` / `stfld mDisgustedBroadcaster` with
no load or Dispose of the old value; IL_0066 `ldftn ForceSpoil` re-arms it through `mSpoilageAlarm` at every stage until
the last. Only `Dispose` and `Unspoil` dispose the field, so each earlier broadcaster stays on the lot's reaction list
(ticked by `Lot.UpdateReactions`, still pushing Disgusted) until the food is destroyed. The sibling call in the same method,
`ServingContainer.CreateDirtyReactionBroadcaster`, is guarded right (IL_0006 and IL_002b `brtrue` on `mDirtyBroadcaster`),
so it is not part of the bug. Fix: after EA's body, dispose the broadcaster it replaced. All households (a food object).

## 2. weatherstonerelisten (off, 3479-3481)

[verified, IL `WeatherStone::AnimateChangeWeatherStoneState` IL_017d/IL_0182] the AfterSuccessfulSummon case stores a new
`kWeatherStarted` listener in `mWeatherChangedEventListener` with no RemoveListener first. Reach [verified, decompiled]:
`DoReset` maps AfterSuccessfulSummon to Resting without touching the listener (IL: no use of the field in DoReset), midnight
`OnMidnightReset` makes it ReadyForSummoning, and the next success adds a second listener: OnWeatherStarted twice, two
kWeatherStopped listeners. Not covered by `weatherstonelistener` (docs/594 §2), which removes it only in `Dispose`. Fix:
before EA's body for AfterSuccessfulSummon, remove the held listener and null the field. Rare (needs a reset mid-summon).

## 3. birthdayagerecord (off, 3482-3484)

[verified, IL `BirthdayParty::OnAgeTransition` IL_003b-IL_0041] after `mSimsWhoAgedUp.Add(sim)` it returns `ldc.i4.1`
(ListenerAction.Remove): later guests who age up at the same party are never recorded, though the list and its per-Sim
reader `DidSimAgeUpAtParty` (wish and challenge feedback) are per Sim. BirthdayParty has no CleanUp that removes the
listener, so the fix keeps it while the party runs and returns Remove on the first event after `SituationExited` (which
also releases the listener of a party where nobody aged up, EA's own leak). Household Sims get the fix: EA's per-Sim record.

## 4. Leads and verdicts

| lead | IL / decompiled check | verdict |
|---|---|---|
| PreparedFood.ForceSpoil, broadcaster per stage | no dispose before stfld; self re-arming | real, `spoilbroadcast` |
| ServingContainer.CreateDirtyReactionBroadcaster | both creations null-guarded | false |
| SnakeCharmingBasket from inventory, basket lost | `Charm.Cleanup` sets opacity 1 and re-adds the basket when `mbFromInventory` and not in the inventory; every subclass calls base.Cleanup | false |
| RideBroomArena route fail, broom invisible | fail path parks (`BroomStand.ParkBroom` FadeIn) or `Inventory.TryToAdd` (`World.ObjectSetOpacity(1)`) | false |
| WeatherStone duplicate weather listener | no remove before the add; reset path reaches it; docs/594 §2 covers Dispose only | real, `weatherstonerelisten` |
| BirthdayParty only first age-up recorded | returns Remove after the first add | real, `birthdayagerecord` |
