# 517. Object use, reference and routing-reference lists keep Sims that are gone (`deadrefsweep` phase 10)

Issue #166, core build 40. Every `GameObject` has three persisted `List<Sim>`s: `mActorsUsingMe` (who uses it),
`mReferenceList` (who holds a reference, e.g. an interaction's target) and `mRoutingReferenceList` (who is routing to
it). A Sim reset or destroyed while in one of them never runs the paired Remove, so the destroyed Sim object stays in
the list and goes into every later save; a load-time graph reset (NRaas ErrorTrap) then resets the object, and whoever
is using it is dropped (the harness saw bars, a fridge and velvet ropes reset this way). This doc describes the holder
and the ts3fixes clean-up: a tenth phase of the `deadrefsweep` switch (off by default; counter 2046, registry
`fixes-2046`; ledger §915; docs/205 §2 for the sweep, docs/516 for the dartboard phase it follows).

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The holder

- The three lists are fields of `GameObject` without `[Persistable(false)]`, so they are saved with the object
  **[verified]**. `mRoutingReferenceList` is null until the first `AddToRoutingReferenceList`.
- EA's Remove methods are `RemoveFromUseList` (virtual; also tells the science component and takes the Sim out of the
  object's `SimLine` queue), `RemoveFromReferenceList` and `RemoveFromRoutingReferenceList` (plain list removes)
  **[verified]**. They run from the using Sim's own clean-up (interaction exit, route end), which a Sim that was reset or
  destroyed mid-way never reaches **[verified: core build 40's load scans listed destroyed Sims in all three lists]**.

## 2. The clean-up

Phase 10 of the spread pass (hourly; since 2026-11-03 only after a Sim leaves or daily, docs/205 §2.3a) (`DeadRefs.cs` `UseListHolders`), for every `GameObject`: each gone entry (null or
`HasBeenDestroyed`) leaves through EA's own Remove method, one call per occurrence, so the science component and the
`SimLine` queue see the removal too. Counter [2046] counts entries (`deadrefsweep_uselists`). The phase walks every
object, so it is the longest of the pass; it runs in the same ~2 ms slices as the others. The pass before a save is
unchanged (phases 1-3 only). Nothing runs with `deadrefsweep` off.

## 3. Test

Repro `use-list-gone-check` (queue `tools/harness/queues/use-list-gone.json`, save Baseline-BP). The hard state is built
by the agent check `DeadRefUseListCheck seed`: a `SocialJigTwoPerson` made out of world, one townie (same pick rule as
docs/516) added to its three lists with EA's Add methods, then that Sim object destroyed, as a reset does; at the end of
each arm the jig is destroyed and the townie instantiated again where it stood. Off arm: `DeadRefCheck sweep` refuses
and `expect left` sees the three gone entries. On arm: the pass is started at once and `state` polled until the jig is
clean; `expect cleaned` requires gone 0 and all three lists empty; the job requires `deadrefsweep_uselists >= 3`.

Offline self-test: `tools/selftest/deadrefs` (the holder helpers) is unchanged; the phase adds no pure code.

Result (2026-11-01 23:58, `bp-use-list-gone-check`, PASS in 15.1 min, al11): off arm gone 3, use 1, reference 1, routing 1, unchanged at `expect left`; on arm gone 0 and all three lists empty, `expect cleaned` passed, `deadrefsweep_uselists` reached 3; both townies put back (Lenny Shutter, Diana Jones). The longest-slice counter ([407]) is not in the job's readback; I-915-3 stays open until a run reads it.

## 4. What is left

Part seats (`ContainedSim` of a destroyed Sim) and conversation members that are gone (core build 40's other half,
`Conversation.mMembers`, which EA's `RemoveSim` cannot take out once the Sim's social component is null) are not in this
phase; conversations are the next phase.
