# 645. NPC loop fixes, batch A (`picnicexit`, `poolfailmemo`, `petstartleskip`, `petlonghome`)

2026-11-19. Source: OFF / sweep runs `phase-2026111[5-9]-*` (`ts3native-full.log`, `ts3native.log`, `health.jsonl`),
decompiled Sims3GameplaySystems / Sims3GameplayObjects. All four switches built, default off, NOT RUN. Managed only
(ts3fixes). Package tag `npcloopsa`.

Counter slots 4070-4079 (contributor block 4000-4089, docs/registry.md). Ten slots for four fixes, so the exempt and error
counts are shared and the log lines say which switch they came from:

| slot | switch | meaning |
|---|---|---|
| 4070 | picnicexit | finished picnic situations exited |
| 4071 | picnicexit | finished picnics skipped (a Sim holds a role, or the situation's cleanup interaction is still queued) |
| 4078 | all four | exempt: a household, selected or unknown-scope Sim met (HouseholdExempt or IsPlayerSim), nothing changed |
| 4079 | all four | errors (caught; EA's code ran unchanged) |

Agent `patches` read-back: `picnicexit_exited`, `picnicexit_skipped`, `npcloops_exempt`, `npcloops_errors`.
Every log line is capped at 10 a minute per switch; the counters are the measure.

## 1. Finished picnics never leave the situation list: `picnicexit` (`PicnicExit.cs`)

**Root cause [verified, decompiled]:** `PicnicBasketSituation.EndPicnic` (EA's alarm at the end of the picnic, or the basket
cleanup interaction calling `PicnicBasket.EndPicnic`) runs `PicnicSituation.EndPicnic` (alarm and broadcaster gone) and
`SetState(new CleanUpPicnic(this))`. The old `BeAtPicnic` state's CleanUp removes every Sim's role and the BeAtPicnic
motive. `CleanUpPicnic.Init` then pushes the basket cleanup on a household Sim (`ForceCleanUp`, with an `OnFailure` callback
that starts EA's `AsyncCleanup`) or starts `AsyncCleanup` directly, and sets `Basket.Situation = null`. It calls `Exit()` only
when the basket is already gone. Nothing else ever exits the parent: `AsyncCleanup` does not, and `PicnicBasket.Dispose`
only disposes through `mSituation`, which is null by then. The root situation stays in `Situation.sAllSituations` (its
constructor added it; only `Dispose` removes it) and is saved with the world. OFF runs [log analysis of the sweep]: Isla
Paradiso 19 -> 54 picnic situations a session, Sunset Valley 9 -> 41.

**Fix (default off):** `PicnicBasketSituation.EndPicnic` is wrapped (`PicnicExitHook`). After EA's body, every listed
`PicnicBasketSituation` in the `CleanUpPicnic` state is checked (`PicnicExitCore.Decide`):
- already exited (`SituationExited`): left alone, never a second `Exit` (EA's Exit marks the whole chain first);
- still its basket's situation: not finished;
- any Sim with the situation in `Autonomy.SituationComponent.Situations` (one pass over `LotManager.Actors`): skipped;
- a situation interaction of the cleanup state still in a Sim's queue (`mInteractions`, the ForceCleanUp push): skipped,
  because `Exit` runs `ClearSituationSpecificInteractions`, which clears that interaction's callbacks, so EA's
  `OnFailure -> AsyncCleanup` fallback could no longer run. It is tried again at the next picnic's end;
- else EA's own `Situation.Exit()`: `CleanUp` + `Dispose` from the CleanUpPicnic leaf up to the root (the same call
  `CleanUpPicnic.Init` makes when the basket is gone). `AsyncCleanup`, if it runs, only uses the child's cached basket.

Finished picnics loaded from an older save are swept the same way at the next EndPicnic. No Sim is touched, so no household
exemption is needed. Counters 4070 / 4071 / 4079.

**Log:** `picnicexit: exited N finished picnic(s), M skipped (sims present|cleanup pending), K picnic situations left listed,
T situations in all`.

**Proof:** agent leak census (`LeakCensusHandlers` situations block) now also reports `picnic_finished`: listed
PicnicBasketSituations in the CleanUpPicnic state. OFF arm: `picnic_finished` grows with every picnic that ends (and
`by_type` PicnicBasketSituation with it); ON arm: it stays at 0-1 (a picnic whose cleanup is still queued), 4070 counts the
exits, no new exceptions. Self-check: `tools/selftest/picnicexit` (no double exit, Sims or queued cleanup skip, only one input
combination exits).
