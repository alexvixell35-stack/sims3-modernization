# 567: Household objects, repair / upgrade, fire, burglar, inventory batch

**In-game status:** `upgradefailalarm`, `repairtimecache`: proven in game 2026-11-14/15, Bridgeport, off BROKEN / on FIXED; all-on soak clean 2026-11-15.

2026-11-15, static (decompiled EA scripts; `game-copy/analysis/gameplay-scan/ranked.tsv` and `grep-sleep-wait.txt`
filtered to plumbing, electronics, appliances, trash, phones, repair / upgrade components, fire, burglar and inventory
files, an alarm add/remove census of those folders, then reads of Run / Cleanup / component start-stop paths). Built,
proven in game (status above). Both switches off by default; EA bug fixes only, using EA's own methods. Scope: docs/555-566 not repeated.

| § | Switch | Counters | Check | Repro |
|---|---|---|---|---|
| 1 | `upgradefailalarm` | 2550-2552 | `Ts3Fixes.Fixes.UpgradeFailAlarmCheck` | `upgrade-fail-alarm` |
| 2 | `repairtimecache` | 2553-2555 | `Ts3Fixes.Fixes.RepairTimeCacheCheck` | `repair-time-cache` |

The checks evaluate the fix decisions with controls (off prints BROKEN, on FIXED); neither changes the world.

## 1. Failed upgrade cut short cancels a later action (`upgradefailalarm`, UpgradeFailAlarm.cs)

[verified, decompiled] `UpgradableComponent.StartUpgrade` decides the outcome up front. For Failure / BadFailure /
Shocked it arms "Failure Upgrade Alarm" on the object (`mScriptObject.AddAlarm`, handle discarded), whose callback adds
`ExitReason.CanceledByScript` to the upgrading Sim when the failure time is up. When the attempt ends earlier (player
cancel, a need or a pushed interaction), `GetUpgradeResult` (through `StopUpgrade`, or directly in JuiceKeg and
WaterTrough) books the progress; nothing removes the alarm. It fires later and cancels whatever that Sim does then
(meal, sleep, a new upgrade attempt: a retry that would succeed is cut off and only part-credited). ~70 upgrade
interactions use this path. Reach: any upgrade attempt rolled as a failure and interrupted [verified path; frequency
inferred: low-skill Sims fail often, upgrades run 1-2 sim-hours].

Fix: `StartUpgrade` notes the alarm handles it added to the object (EA's `AlarmManager.mGameObjectIndex`, before /
after, global and lot managers); `GetUpgradeResult` and the next `StartUpgrade` on that object remove a noted alarm that
is still pending (`GetTimeLeft` > 0) with `AlarmManager.RemoveAlarm`. A failure that ran its full time (alarm already
fired) is unchanged. Counters 2550 removed at booking, 2551 at restart, 2552 errors.

## 2. Repair time cached for the wrong Sim (`repairtimecache`, RepairTimeCache.cs)

[verified, decompiled] `RepairableComponent.mRepairTime` caches the repair time: `GetRepairTime(actor)` fills it only
when <= 0 (actor's Handiness level, or 1 minute with Instant Handiness, times the share still to repair); `StopRepair`
resets it to -1. Every repair interaction calls `GetRepairTime` at its start (`ConfigureStages`), before routing and
before `StartRepair`. Any attempt that ends without `StopRepair` leaves the first Sim's time for the next one, for its
stage and for `StopRepair`'s progress math:
- the repair is cancelled or fails its route / in-use check before `StartRepair` (common);
- `StartRepair` electrocutes the Sim (returns false; TV, Stereo, Computer, ... never call `StopRepair` then);
- the repairing Sim is reset.
So a skill-0 Sim's long time sticks for a skill-10 Sim or the repairman, and an Instant Handiness 1-minute time sticks
for a normal Sim. The field is persisted, so this survives save/load.

Fix: `GetRepairTime` notes which Sim the cache belongs to. When another Sim asks and the noted Sim is not repairing the
object now (its current interaction does not target it), the stale cache is dropped and EA's body computes it for the
asker. While the noted Sim is mid-repair, the asker gets its own time from EA's body and the cache is put back, so the
running repair is untouched (EA would hand the asker the running Sim's time; with Instant Handiness EA even overwrote the
running repair's time with 1). Counters 2553 dropped, 2554 own time while another repairs, 2555 errors.

## 3. Checked, not fixed (leftovers, ranked)

1. `FireManager.UnregisterFire` pays fire insurance from `mHouseholdValuePreFire`, which is only set when the lot's
   household is active and no emergency-fire situation runs, and never cleared: a later fire on that lot under an
   inactive (NPC) household uses the old value [verified path, no player effect found].
2. `BurglarSituation.Stealing.Init` idling: traced and fixed in docs/568 §1 (`burglaridle`). The "no steal interaction"
   case is not real (`ForceSituationSpecificInteraction` always returns the instance, and a failed queue test calls
   StealFailed); the real idle paths are the fire branch and the no-household return.
3. `Phone ... GoToLotSuccess` (woohooty text) sleeps in a completion callback until the texter's queue is empty
   (bounded by the cancel it just issued).
4. `UpgradableComponent`: cancelling a doomed attempt credits progress at the failure-time rate (the next attempt can
   finish early); player-favourable, left as is.
5. Read, no bug: bath pet / large-dog tub use lists (`Cleanup` removes them), Sprinkler `IsBeingUpgraded` (object reset
   clears it), Cleanable absent-minded alarm (callback re-checks the current interaction), shower woohoo waits, unique
   object inventory moves (`UniqueObject.MoveToInventory`), outdoor trash rummage alarm (persistent daily alarm by
   design).
