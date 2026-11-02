# 523. A celebrity's waiting-to-react list keeps Sims that are gone (`deadrefsweep` phase 13)

Issue #166 (docs/205 §4's `Celebrity.mWaitingToReact` item). A celebrity Sim's `Celebrity` object (`Sim.mCelebrity`,
made when the celebrity broadcaster starts) keeps `mWaitingToReact`: the Sims who rolled a reaction to the celebrity on
entering its broadcaster and have not reacted yet. Only the reaction's own end (`RemoveSimFromWaitingToReactList`, from
the reaction interactions) removes a reactor; one reset or destroyed before that stays, and the list is saved with the
celebrity Sim. This doc describes the holder and the ts3fixes clean-up: a thirteenth phase of the `deadrefsweep` switch
(off by default; counter 2049, registry `fixes-2049`; ledger §919; docs/205 §2 for the sweep, docs/522 for the phase it
follows).

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The holder

- `Celebrity.OnEntered` (the broadcaster's enter callback) adds the entering Sim to `mWaitingToReact` when the reaction
  roll succeeds; the reaction interactions remove it when they end **[verified]**. Nothing removes a reactor whose Sim
  object was destroyed first **[verified: no caller of the Remove on the destroy or reset path]**.
- `mWaitingToReact` is a plain persisted `List<Sim>` of the `Celebrity` object, which `Sim` persists in `mCelebrity`
  **[verified]**.

## 2. The clean-up

Phase 13 of the hourly spread pass (`DeadRefs.cs` `CelebrityHolders`), for every Sim with a `Celebrity` object: each gone
reactor (null or `HasBeenDestroyed`) leaves through EA's own `RemoveSimFromWaitingToReactList`. Both fields are private,
so they are read by reflection. Counter [2049] counts entries (`deadrefsweep_celebrity`). The pass before a save is
unchanged (phases 1-3 only). Nothing runs with `deadrefsweep` off.

## 3. Test

Repro `celebrity-waiting-gone-check` (queue `tools/harness/queues/celebrity-waiting-gone.json`, save Baseline-BP, a
world with celebrities). The hard state is built by the agent check `DeadRefCelebrityCheck seed`: the first Sim of the
world whose `Celebrity` object exists gets one townie (same pick rule as docs/516) in its waiting list, then that
townie's Sim object is destroyed, as a reset does; at the end of each arm an entry of ours still there is removed and
the townie instantiated again where it stood. Off arm: `DeadRefCheck sweep` refuses and `expect left` sees the gone
entry. On arm: the pass is polled until the list holds no gone entry; `expect cleaned` requires that; the job requires
`deadrefsweep_celebrity >= 1`.

Result (2026-11-02 01:34, `bp-celebrity-waiting-gone-check`, PASS in 15.1 min, al16): off arm, the seeded state was met 2 s after the seed (Lenny Shutter waiting to react to Katrina Pala, his Sim object destroyed: gone 1, waiting 1) and `expect left` found it unchanged; on arm, the pass ran at once and the state poll 2 s after the seed already read gone 0, waiting 0; `expect cleaned` passed and `deadrefsweep_celebrity` reached 1. Both townies were put back.

## 4. What is left

The reaction records (`mReactionRecords`, by object id, no Sim object) are not a holder. Cooking appliance handlers,
the last docs/205 §4 item, are still not built.
