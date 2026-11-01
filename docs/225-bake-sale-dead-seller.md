# 225. A bake sale table that keeps a deleted seller (#102, `bakesaleseller`)

Issue #102 (a player save, two ScriptErrors a minute after load) found an NPC bake sale table whose seller had been
deleted. Every buyer threw, and the table was never cleaned up. This doc explains why and describes the ts3fixes switch
that fixes it. The switch is off by default. Status (2026-10-31): built, self-tested, and the repro passes in game (§4).
The same three changes ran live as a patch of the player's own script build in two sessions (issue #102). Counters
683-684 (registry `fixes-683`).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live in #102;
**[inferred]** = our reading, not yet checked in game.

## 1. Why the seller stays

All methods are on `BakeSaleTable` (`Sims3GameplayObjects`, `Sims3.Gameplay.Objects.Miscellaneous`).

- `mSeller` is set by `TendTable`. Only `StopTending` and the table's own reset clear it [verified].
- The seller's posture (`TendTablePosture`) clears nothing when the seller leaves another way: `OnReset`, `OnExitPosture`,
  and Sim disposal (the base `Posture.OnSimDisposed` is empty) [verified]. So a seller who is reset or deleted while
  tending stays the seller.
- `HasSeller` is only `mSeller != null` [verified].
- With a deleted seller (`HasBeenDestroyed` true, `InteractionQueue` null):
  - `PurchaseAutonomously` passes its test, which checks `HasSeller`. `Run` then calls `AddNext` on the seller's null
    queue and throws `NullReferenceException` [verified live, #102].
  - `NPCCleanUpAlarmHandler`, the NPC table's timed clean-up, first removes its own alarm. It then tests `HasSeller` and
    calls `CancelPosture` on the seller's null queue, so it throws before `CleanupTable` and `Destroy` [verified].
  - The table stays in the world with no alarm, and every later buyer throws too. The save keeps the reference
    [verified live, #102].
- EA makes NPC tables from `tableBakeSale` (EP2) and gives each one the clean-up alarm in `SetUpForNPC` (alarm type
  `AlwaysPersisted`) [verified].

## 2. The fix (`bakesaleseller`, `mods/ts3fixes/src/BakeSale.cs`)

- **Switch off:** EA's three methods run.
- **Switch on:**
  - `GetSeller` reads a seller who is destroyed, or has no interaction queue, as no seller.
  - `HasSeller` is `GetSeller != null`, so the buyers' test and the clean-up handler agree.
  - `OnStartup` gives an NPC table that has no clean-up alarm a new one, a sim-minute out, with EA's handler and alarm
    type. Only a table orphaned this way has none: `SetUpForNPC` always sets the alarm, and the handler that clears it
    destroys the table. EA's handler then removes the table.
- Not changed: a seller who was reset but is still alive stays the seller until `StopTending` or the table's alarm.
  That is EA's behaviour, and #102 saw no error from it.
- The `OnStartup` part runs at load only, so an in-load A/B shows only the `GetSeller` / `HasSeller` part.
- Logs: one `ts3native.log` line at the first dead-seller read of a session, and one for each re-armed table.
- The decisions are in `BakeSaleCore.cs` (self-test `tools/selftest/bakesale`).

| switch | tunable | default | counters |
|---|---|---|---|
| `bakesaleseller` | `kBakeSaleSeller` | off | 683 `bakesaleseller_reads` (seller reads that found a deleted seller), 684 `bakesaleseller_rearmed` (orphaned NPC tables given a new clean-up alarm) |

## 3. Checks

- **Self-test** (`node tools/selftest/bakesale/run.ts`): every input of both decisions, plus the #102 cases.
- **Repro** `bake-sale-dead-seller-check` (Baseline-BP, `Fixes.BakeSaleCheck`): it builds the bug state on a test NPC
  table out of the world, with a new Sim as seller that is then deleted the way `remove_sims` deletes a Sim. No
  household Sim is touched. Off: `HasSeller` true, and the handler throws and keeps the table. On: `HasSeller` false,
  the re-arm gives the table an alarm, and the handler destroys the table. "rearm" runs the load-time part directly.
- `Sim` disposal leaves `InteractionQueue` null on the test seller, as on the #102 seller [verified in game, §4].

## 4. Game run (2026-10-31, queue `bake-sale`, results `docs/data/results/2026-10-31-queue-bake-sale.md`)

Repro `bp-bake-sale-check` passes [verified], on Baseline-BP with the v0.2.4 asi:

- Off: the deleted seller stays: destroyed True, queue null, `HasSeller` True. EA's handler throws
  `NullReferenceException`, and 3 s later the table still exists. Counters stay 0.
- On: the same state reads as no seller (`HasSeller` False, `GetSeller` null, [683] counts each read). The re-arm gives
  the table EA's clean-up alarm ([684] = 1). EA's handler returns, and 3 s later the table is destroyed and its proxy is
  gone. Both log lines appear.
- `Destroy()` finishes after the calling task yields: a read in the same call still sees the table. The first run read
  it there, so the check now reads it in a later step.
