# 516. A dartboard keeps players that are gone (`deadrefsweep` phase 9)

Issue #166, core build 42. A Sim that is reset or destroyed in the middle of a darts game stays in the dartboard's
`Players`, in its `Team`, and as `CurrentPlayer` and `CheatingSim`. The fields are saved with the board, so the destroyed
Sim object and what it holds go into every later save, and a load-time graph reset (NRaas ErrorTrap) resets the board and
drops whoever is using it. This doc describes the holder and the ts3fixes clean-up: a ninth phase of the existing
`deadrefsweep` switch (off by default; counter 2045, registry `fixes-2045`; ledger §914; docs/205 §2 for the sweep).

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The holder

- `Dartboard.Players` (`List<Sim>`), `Teams` (each `Team.TeamPlayers`), `CurrentPlayer`, `CurrentTeam`, `CheatingSim`:
  public fields of a `[Persistable]` object, saved with the board **[verified]**.
- EA clears them in `DoFinishGame` only **[verified]**. `Quit(sim)`, which every Wait / TakeTurnThrowingDarts clean-up
  calls, takes the Sim out of its team (turn index adjusted, an empty team removed, `ActorsUsingMe`) or out of `Players`,
  but leaves `CurrentPlayer` and `CheatingSim` **[verified]**.
- A Sim reset or destroyed mid-game never runs that clean-up, so its destroyed object stays in all four places
  **[verified: the first harness case was a dartboard's `CurrentPlayer` in a load-time reset list, core build 42]**.
- EA reads `CurrentPlayer` and `CheatingSim` behind null checks (`PushNextInteractionAfterPlay`, `TakeTurnThrowingDarts.Run`,
  `DoFinishGame`), so clearing them is safe **[verified]**.

## 2. The clean-up

Phase 9 of the hourly spread pass (`DeadRefs.cs` `DartboardHolders`), for every `Dartboard` (the expensive one is a
subclass): each gone player (null or `HasBeenDestroyed`) leaves through EA's own `Quit`, so the team's turn index and the
`Teams` list stay right; a gone `CurrentPlayer` or `CheatingSim` is set to null. Counter [2045] counts entries
(`deadrefsweep_dartboards`). The pass before a save is unchanged (phases 1-3 only). Nothing runs with `deadrefsweep` off.

## 3. Test

Repro `dart-gone-check` (queue `tools/harness/queues/dart-gone.json`, save Baseline-BP). The hard state is built by the
agent check `DeadRefDartCheck seed`: a dartboard made out of world (catalog name `DartboardCheap` with `ProductVersion.EP3`; the two Late
Night boards are `DartboardCheap` and `DartboardExpensive`, and the one-argument overload would build a base-game key), one townie (not selectable, no pet, not of the active
household, not on its home lot, no role or service) added as team player, `CurrentPlayer` and `CheatingSim`, then that
Sim object destroyed, as a reset does; at the end of each arm the board is destroyed and the townie instantiated again
where it stood. Off arm: `DeadRefCheck sweep` refuses and `expect left` sees the three gone entries. On arm: the pass is
started at once and `state` polled until the board is clean; `expect cleaned` requires gone 0, `Players` and `Teams`
empty, both fields null; the job requires `deadrefsweep_dartboards >= 3`.

Offline self-test: `tools/selftest/deadrefs` (the holder helpers) is unchanged; the phase adds no pure code.

Result (2026-11-02 00:30, `bp-dart-gone-check`, PASS in 13.1 min, al12): off arm, the seeded state was met 2 s after the seed (gone 3, players 1, teams 1, current and cheating set) and `expect left` found it unchanged; on arm, the pass ran at once and the state poll 2 s after the seed already read gone 0, players 0, teams 0, both fields null; `expect cleaned` passed and `deadrefsweep_dartboards` reached 3. Both townies were put back where they stood (Lenny Shutter, Diana Jones).

## 4. What is left

`Dartboard.Darts` and the thrown dart objects are not touched. The other gone-Sim holders of #166 (object use lists and
conversations, build 40; the object owner component, build 43; routing reference lists, build 38) are separate phases.
