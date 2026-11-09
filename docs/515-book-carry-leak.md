# 515. Book.StopUsingBook drops the reader's carry state machine without an exit (`bookcarry`)

A player saw two Sims in one household hold their arms as if reading, with page-flip sounds, after they put their books
away; a reset fixed it, and a save made in that state loaded with the pose. Looking for the cause, we found that EA's
`Book.StopUsingBook` drops the carry state machine of every standing or sitting reader without asking it to exit. That
leak is real, and this doc describes it and the ts3fixes switch `bookcarry` (off by default; counters 2043-2044,
registry `fixes-2043`; ledger §913). **It is not the cause of the stuck arms**: a live test with the leak in place left
the Sim's arms normal (§1, "What the player sees"). The arm bug is still open.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. The dropped machine

- **Two state machines** [verified, IL]. `ReadBook.DoReadBook` plays the read on the posture's machine (standing and
  sitting use the jazz machine "ReadBook", an upper-body read). Before that, `ReadBook.GetBook` takes the book with
  `CarrySystem.PickUp`, which acquires a second machine, a "CarryGeneric" carry machine, and stores it in
  `Sim.CarryStateMachine`.
- **No exit** [verified, IL and live]. `Book.StopUsingBook(Sim, StateMachineClient, bool)` (one definition, three
  callers) branches on the Sim's posture at the end of the read: rocking, relaxing and lying on the front have their own
  branches; standing and sitting share the plain one. In the plain branch with `bPutInInventory`, it adds the handler
  that puts the book in the inventory on the read machine, sets `Sim.CarryStateMachine` to null, and asks only the read
  machine to exit. The carry machine is never asked to exit. EA's own carry exits (`CarrySystem.ExitAndKeepHolding`,
  `CarryUtils.ExitCarry`) request "ExitNoPut" or "Exit" on that machine before they clear the field. A probe hook that
  logged every `StopUsingBook` call saw 303 such calls in our sessions on the player's save (236 sitting, 67 standing),
  every one with the carry machine dropped.
- **What it costs** [verified live for the driver; the cost is inferred]. The dropped machine keeps its engine driver
  (`mDriver.mHandle` nonzero) until the garbage collector finalizes the script client, whose finalizer releases it. One
  driver per read is held for that time.
- **What the player sees** [verified live: nothing]. With the switch off, a Sim whose arm pose had been restarted read a
  book with the carry, and the read ended through this branch, dropping the machine; we kept a reference, so it stayed
  alive. The player saw her arms normal. The stuck-arm pose comes from something else. An earlier session had ended one
  stuck Sim's pose by disposing a carry machine, but that machine dated from a glass she put away through EA's own exit,
  not a book, and the other Sim later recovered with no help, so that cure may have been a coincidence.
- **Why the book "teleports"** [verified, IL]. `ReadBook.ShouldPutInInventory` sends the book to the inventory when it came
  from there, or on the home lot when the posture allows it and the read has no exit reason. That is EA's rule.
- **Prior fixes**: none found. No third-party mod we have reads `StopUsingBook` or sets `Sim.CarryStateMachine`.

## 2. The fix (`bookcarry`, `mods/ts3fixes/src/BookCarry.cs`)

A hook on `Book.StopUsingBook`. It reads `Sim.CarryStateMachine` before EA's method and again after it. When a machine was
set before, was not the read machine, and the field is empty after (`BookCarryCore.Dropped`), the call is counted
[2043], also with the switch off. With the switch on, the dropped machine is ended the way `ExitAndKeepHolding` ends a
carry (request "ExitNoPut", the state that keeps the object where it is, since EA's handler has already put the book
away), then disposed, which releases its driver [2044]. The request waits for the state when the reading interaction
allows it, and is made without waiting otherwise. EA's method runs unchanged, and the check sits in a try/catch, so it
never changes its result. The dispose follows the request at once, so the engine may cut the exit transition's blend
**[inferred; not checked in game]**; the book is already in the inventory, so nothing but that blend can be lost.

Not covered: the rocking, relaxing and lying branches, which keep `Sim.CarryStateMachine` set (the next interaction's
`InteractionQueue.PutDownCarriedObjects` clears it, also without an exit).

## 3. Checks

- **Self-test** (`node tools/selftest/bookcarry/run.ts`): `Dropped` with a carry that is cleared, kept, replaced, absent,
  or the read machine itself.
- **Repro** `book-carry-check` (queue `book-carry.json`, Baseline-BP, `Fixes.BookCarryCheck`). It runs with the player's
  core mod, which does not change `StopUsingBook`. In each arm, `cure` first cancels Tiara's interactions, restarts her
  idles (the save loads her with stuck arms) and puts her in the everyday outfit, then `read` queues EA's `ReadBook`
  (from the inventory) on a book in her inventory, so the book goes back there at the end. The repro polls `state` until
  she reads with the book in hand; `stop` then cancels the read, so EA's method ends it through `StopUsingBook` (a read
  that already ended that way passes), and the repro waits for the drop. Only her drops count. `expect left` (off): a
  dropped carry machine that still holds its engine driver, ended +0. `expect ended` (on): every dropped machine ended,
  and the last one's driver released (handle 0). Each expect step throws on any other outcome, then holds 45 s so a
  player watching can see the arms. `IsValid` is no test here: it asks whether the machine's event queue is valid.
- **Repro, crowd form** (2026-11-09, replaces the Tiara steps above in `repros.json`; the one-Sim checks stay for hand
  use). Three runs at game hour 3 (bundle-1105, correctness-1109, fixes-1109) failed the same way: `read` returned
  "queued True", but for 180 s Tiara had no current interaction, `carry` true (the stuck pose the save loads with, which
  `cure` does not clear) and no read, so `ready` never came; the one run at hour 15 (bundle-1105b) passed. `ready`
  itself is computed right; why the queued read never ran is not proven [inferred: `ReadBook.Definition.Test` with the
  book in the inventory and a `StandingPosture` asks for the book in the right hand, so the queue head can drop it].
  Now each arm runs `crowd read 20`: up to 20 non-household Sims (never the active household, its lot or a selectable
  Sim), each at a bookshelf with a book on its **own home lot** (at most 2 per shelf), because EA puts the book in the
  inventory, the only branch that drops the carry machine, only on the reader's home lot (`ShouldPutInInventory`; a
  shelf elsewhere gets the book back and drops nothing). Queues cancelled, motives maxed, Teleport Me Here in front of
  the shelf, then EA's `Bookshelf_ReadSomething` at high priority. `crowd state` counts sims, reading, carry, ready,
  dropped, droppedSims and current interactions; the repro waits for `any`, `crowd stop` cancels every crowd read, the
  repro waits for `droppedSims >= 1`, and `crowd expect left|ended` judges all crowd drops and reports "N of M Sims dropped".

## 4. Results

Run 4, 2026-11-01, Baseline-BP, the player's core mod, release v0.2.4 asi: both arms passed (runs 1-3 failed on the
test design: `IsValid` as the test, a read cancelled before the book was in hand, a shower cancelled into a towel).

| Arm | Expect step | Arms (player) |
|-----|-------------|---------------|
| off | seen +1, ended +0, driver held | not looked at in the hold; a second off read by hand right after: normal |
| on | seen +1, ended +1, driver released | normal |

While the switch was on (about 4 min), it ended 16 dropped machines across the town, so the leak is common.
