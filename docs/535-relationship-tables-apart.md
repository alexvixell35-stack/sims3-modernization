# 535. Two Sims' relationship tables come apart, and the next lookup throws (`reltablesapart`)

Issue #166 build 14 (its first part). EA keeps one relationship table per Sim and puts one shared `Relationship` into
both. When the two tables disagree, the next lookup that may create the relationship throws. This doc describes the
bug, the ts3fixes hook (switch `reltablesapart`, off by default; counter 2065, registry `fixes-2065`; ledger §939) and
the live check.

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The bug

- `Relationship.sAllRelationships` maps each Sim's description to a table of its relationships. `GetInternal(x, y,
  createIfNone, checkForAttraction)` (private, behind `Relationship.Get`) returns `x`'s entry for `y` when there is one;
  otherwise, with `createIfNone`, it makes a new `Relationship(x, y)` and ends with `value.Add(y, r)` on `x`'s table and
  `value3.Add(x, r)` on `y`'s **[verified]**.
- If `y`'s table already holds `x` while `x`'s table has no `y` (the tables came apart), the second `Add` throws
  `ArgumentException` "An element with the same key already exists" **[verified]**, after the first `Add` put a second
  relationship object for the pair into `x`'s table.
- Seen live on 2026-10-22 (Isla Paradiso): NRaas MasterController's Reset All (`ResetSimTask` ->
  `DreamCatcher.HouseholdStore.Dispose` -> `Career.OnOwnerBecameSelectable` -> `SetCoworkersAndBoss` -> `CheckBoss` ->
  `Relationship.Get`) threw it **[verified live]**. How the tables came apart is not known; a reset that rebuilds a
  household is where it showed.

## 2. The fix

`mods/ts3fixes/src/RelTablesApart.cs`, static hook `RelTablesApartHook.GetHook` swapped over `Relationship.GetInternal`
(`EaFixes.cs` patch list). With the switch on, `createIfNone`, and `x`'s table missing `y` while `y`'s table holds a
relationship for `x`: that relationship (its liking and history) is put into `x`'s table (made if missing) and
returned, so the tables agree again and nothing is created twice. Every other case runs the original. Counter [2065]
`reltablesapart_rejoined`.

Not ported: build 14's second part, a guard around `simler90Extras.Other.GetOwnedAndUsableVehicleForStations` (a
`NullReferenceException` for an elder townie living on a houseboat). That method is simler90's own code, not EA's; it
belongs with that mod.

## 3. Test

Repro `rel-tables-apart-check`, queue `tools/harness/queues/rel-tables-apart.json`, save Baseline-BP, **simler90's
UPDATE242 core without the player's patches** (the player's core carries build 14). The agent check
`Ts3Fixes.Fixes.RelTablesApartCheck` takes the first pair of valid descriptions whose two tables share one
relationship `r`, removes `r` from `x`'s table and calls `Relationship.Get(x, y, true)` once; `x`'s table gets `r` back in
a `finally` either way. Off arm (`expect broken`): EA's `ArgumentException`. On arm (`expect fixed`): `r` is returned and
is in both tables; `reltablesapart_rejoined` reaches 1. Early gate: that counter at 3 min.

Result (2026-11-05, pure UPDATE242 core, `docs/data/results/2026-11-05-alexvixell35-stack-rel-tables-apart.md`): PASS.
Off arm: EA's `ArgumentException`. On arm: the shared relationship returned and in both tables; `reltablesapart_rejoined` 1.

## 4. What is left

- What makes the tables come apart. A census of pairs where only one side holds the other, over a long session and
  around NRaas resets, would show how often it happens.
