# 251. A book left in the hand without a carry machine: PutAwayLeftovers NRE in `CarrySystem.PutDownOnSurface`

Static triage, 2026-11-08, no game run, no build. Job `bp-castrim-live-mode3-r2` (queue `tools/harness/queues/castrim-live.json`,
pair castrimdiag1108 = ts3native 0.36.194 + ts3fixes g2pair1108b), save Baseline-BP, script error at 03:50:21 UTC
(29 s after the world loaded at 03:49:52 UTC, before any agent step touched a Sim). First time seen in our local ScriptError files.

## 1. What ErrorTrap recorded

- Household Sim Lala Angelista (active household, home lot Air Spray, Bridgeport), autonomous `ServingContainer+PutAwayLeftovers`.
- Error 1: `NullReferenceException` at `CarrySystem:PutDownOnSurface` `0x000ad callvirt`, called from `PutDownOnNearestSurface` <-
  `PutDown(sim, Normal, forceRoute true)` <- `Food:PutHeldObjectDownOnCounterTableOrFloor` <- `PutAwayLeftovers:Run`.
  Argument values in the frame: surfaceSlotType 1, forceRoute 1, requireAvailableChair 0, playRouteFail 0.
- Error 2, same tick: `InteractionQueue.PutDownCarriedObjects` -> `OnError`: "PutAwayLeftovers left an object in the Sim's hand with
  no carry state machine: Sims3.Gameplay.Objects.BookGeneral" (`InteractionQueue.cs:869`).

## 2. The null [inferred by elimination, strong]

The frame offsets and opcode names are mint (the game's Mono interpreter) code, not IL: `ret.void`, `brfalse.i4.s`, `stloc.u1` are
mint opcodes [verified: not IL mnemonics], and IL offset `0xad` of `PutDownOnSurface(Sim, ISurface, SurfaceType, bool, bool, bool)` is the
`get_Surface` call inside `if (requireAvailableChair ...)` after `FinishPutDown`, which cannot run with requireAvailableChair 0
[verified, ilspycmd IL of `Sims3GameplaySystems.dll`]. So 0xad cannot be read as an IL offset.

In `CarrySystem.cs:754-791` every reference the method uses before `FinishPutDown` is checked except one: `surface` (null check),
the right-hand object (null check), `surfaceSlot` (null check), `surface.Surface` (a struct, `SurfacePair`), `sim` (used at the
top already). The unchecked one is `sim.CarryStateMachine.SetParameter("Height", surfaceSlot.Height)` (`CarrySystem.cs:781`), and
error 2 proves `Sim.CarryStateMachine` was null with a `BookGeneral` in the right hand. So the null is **`Sim.CarryStateMachine`**
(the callvirt is `StateMachineClient.SetParameter`), not the surface, slot or route result. The slot route itself
(`RouteToSurfaceSlot`, forceRoute) returned true, or the method would have returned false before that line.

How the state arises [verified, `Book.cs:378-391`, `Book.cs:309-317`]: `Book.StopUsingBook` with `bPutInInventory` (standing or
sitting read, book came from the inventory) adds `PutBookInInventoryHandler` as a one-shot event 100 handler on the read machine,
sets `Actor.CarryStateMachine = null` at once, and requests "Exit". The book is parented to the hand until event 100 of that
clip fires. If the clip never reaches event 100 (machine ended, Sim reset, posture or animation cut), the book stays in the hand
with no carry machine. Every later `CarrySystem.PutDown` on that Sim then throws at `CarrySystem.cs:781`, and
`PutDownCarriedObjects` throws error 2. Where Lala's read ended this way is not in any log [unknown].

## 3. Our features on this path

| Feature | On here? | Effect on this Sim | Evidence |
|---|---|---|---|
| `bookcarry` (`BookCarry.cs`, docs/515) | off | none; its hook only counts. When on it ends the dropped *carry* machine, not the read machine that fires event 100 | `kBookCarry = false` (`Instantiator.cs:149`); absent from both "settings on" lines (ts3native log lines 579, 581) [verified] |
| Async routing / KD cuts / unseen fallback | on | household exempt: both routes "uncut (exempt) ... seen" | log lines 846-849 [verified] |
| Route result | - | 2x `Plan async FailedBecauseNoPathToDestination`, goal 1246.1,69.6,1748.5, blocker DynamicSlot footprint of object 0x38B400F52337E260 | lines 847, 849; same goal failed on 2026-11-06 (`mintfuse-verify ... 1714` log, blocker None) and appears in ~20 older logs [verified] |
| `EaFixes` CarryHook | on | only `EnterWhileHolding(sim, null)`; not on this path | `EaFixes.cs:83` [verified] |
| CasTrim, MemDiag | on | no carry or interaction code | [inferred] |
| Agent / harness | - | no command to Lala before the error: steps before 03:50:21 were `test_env` (weather, season) at 03:50:16, `seed_rng`, `time`; the first Sim step (`outfit_change`) was at 03:52:10 | job log [verified] |

The failing route is vanilla behaviour at a goal EA's geometry blocks, and it only chooses the branch of `Run` that calls
`PutHeldObjectDownOnCounterTableOrFloor`; any route failure of `PutAwayLeftovers` reaches the same put-down. The precondition is the
book in hand with no carry machine, which no feature of ours sets. Whether `test_env`'s weather or season switch 5 s earlier ended
a read before event 100 is open [inferred possible, not checked]; it is a harness action, not a player one.

## 4. Known EA bug?

No NRaas handling: no `PutDownCarriedObjects`, `PutAwayLeftovers`, `PutDownOnSurface` or `CarryStateMachine` code in
`game-copy/nraas-src` Overwatch or ErrorTrap [verified, grep]. No web hit for the error text. EA's own code path explains it
(section 2), so this is an EA bug [inferred, strong]; not proven in game.

## 5. Verdict

EA bug, not ours [inferred, strong; verify with the logging below]. The NRE is EA's unchecked `CarryStateMachine` in
`PutDownOnSurface` after `Book.StopUsingBook` nulled it ahead of the event that moves the book to the inventory. Our features on the
path are household-exempt or off. Not caused by a harness command to Lala. Open: what ended the read before event 100.

## 6. Proposed fix (fix-ea-bugs-too; not built)

`bookhand` (new ts3fixes switch, off until a repro passes): a prefix on `CarrySystem.PutDown(Sim, SurfaceType, bool)` and
`InteractionQueue.PutDownCarriedObjects`: when `sim.CarryStateMachine == null` and the right-hand object is a `Book` still parented
to the Sim, call EA's own `book.PutBookInInventoryHandler(null, null)` (Inventory.TryToAdd, UnParent, ResetBindPose) and return
as put down. This is the outcome EA's code already chose for that book, so household behaviour equals the intended vanilla result
(no cap, skip, teleport or fake); other objects in that state are left to EA. Counters: book moved, book add failed
(reserve 2 fixes slots at build time).

Logging (ships with or before the fix, keep-crash-logging): in the existing `BookCarryHook.StopHook` path, when
`bPutInInventory` and the plain branch ran, arm a 10 s sim-tick check; if the book is still `Parent == Actor` with
`CarryStateMachine == null`, log Sim, household or not, the current and previous interaction, posture, and whether the read
machine is valid. That names what interrupts the Exit clip.

Repro: `book-hand-check` agent action: Sim reads a book from the inventory standing (`bookcarry read`), then `bookcarry stop`
followed at once by a Sim reset (or `StopAllAnimation`) before event 100, then queue a put-down; switch off = this NRE,
switch on = book in the inventory, no script error. Add to `repros.json`.

## 7. Built (2026-11-08, branch `bugfix/bookhand-ea-book-in-hand`, NOT RUN)

`bookhand` (ts3fixes `kBookHand`, default off) and `bookhanddiag` (`kBookHandDiag`, default off), `mods/ts3fixes/src/BookHand.cs`,
decisions in `BookHandCore.cs` (self-check `tools/selftest/bookhand`). Counters `fixes-876` `bookhand_seen` (stuck books seen at an
EA put-down that would throw, also with the switch off) and `fixes-877` `bookhand_fixed` (of those, moved out of the hand).

- **Guard.** Two prefix hooks (stubbed swaps, docs/242 rows in `swap-stack-audit.csv`): `CarrySystem.PutDown(Sim, SurfaceType, bool)`
  and `InteractionQueue.PutDownCarriedObjects`. Each acts only where EA would throw on a stuck book (book in the right hand, `Parent ==`
  the Sim, `CarryStateMachine == null`): `PutDown` when the Sim has a current lot (with none EA returns at once; otherwise every branch,
  surface or floor, reads the null machine [verified, `CarrySystem.cs` PutDownOnSurface, PutDownOnSpot]); `PutDownCarriedObjects` past its own
  early returns (next queued interaction of another group, no carrying posture) [verified, `InteractionQueue.cs:738-870`]. Switch on, it
  calls EA's `Book.PutBookInInventoryHandler(null, null)` first, then EA's method runs on an empty hand.
- **Why it is vanilla-equal (applies to household Sims too).** `StopUsingBook` already chose this outcome for this book: it registered that
  same handler for event 100 and dropped the carry machine. The guard runs only in a state where EA's next step is an exception, and its
  result is the one EA's own handler gives (inventory add, unparent, bind pose reset). No cap, skip, teleport, delay or fake; a later
  event 100 finds `Parent == null` and does nothing. Other objects in that state stay with EA. One visible difference: in `PutDown`, EA
  would first route the Sim to a surface and then throw; with the guard the Sim does not walk there.
- **Logging.** `BookCarryHook.StopHook` (the existing `Book.StopUsingBook` swap) takes, before EA's method, the read, its
  `CancelledReason`, the Sim's `ExitReason` and the first other queued interaction (the likely canceller); when the call leaves a stuck
  book and `bookhand` or `bookhanddiag` is on, a task checks 10 s (real time) later and logs one `bookhand: still in hand 10 s after
  StopUsingBook:` line (Sim, household yes/no, current interaction, posture, the stop context). Each guard action logs a `bookhand:` line.
  Cap 50 lines per session. Cost: one context string per read end while a switch is on, one task only for a stuck book.
- **Repro** `book-hand-check` (on) / `book-hand-check-off` (off), agent check `Fixes.BookHandCheck`, Tiara Angelista on Baseline-BP:
  three tries, each EA's ReadBook from the inventory until the book is in hand, then (1) cancel + Go Here far away, (2) cancel + a season
  and weather switch (`test_env`, as 5 s before the original error), (3) stress, labelled: cancel with `AnimationUtil.StopAllAnimation`
  one tick into `StopUsingBook`. A probe 15 s later records whether the book is stuck. Off records which try (if any) gets there; on
  throws when a probe finds a stuck book or `bookhand_fixed < bookhand_seen`, and says N/A when the state never occurs.
- **Queue** `tools/harness/queues/book-hand.json`: `bp-book-hand-repro-off` then `bp-book-hand-repro` (8 min each, pair `bookhand1108` =
  ts3native.asi.castrimdiag1108 0.36.194 + ts3fixes.package.bookhand1108); the on job has `scriptErrorsMax` 0.
- **Open.** The natural trigger is still unknown; the off job's tally and the diag line name it. Default stays off until the on job
  passes with the state reproduced.

## 8. Crowd repro with the put-down (2026-11-12, branch `feature/bookhand-fix-repro-1112`, NOT RUN)

- **Why.** `bookhand-verify-1112` (book-hand-check on rc1111d) showed bookhand_seen 0 / fixed 0: try 2 reached the stuck state
  (STUCK, current ReadBook, SittingPosture) but the next `cure` put the book away before any EA put-down, and try 3 stalled
  when Tiara left for work (GetInCarpool -> WorkInRabbitHole) [verified, probe-out/bookhand-verify-1112-run.log].
- **Diag line never logged** [verified in code, `Book.cs:378-391`; hang inferred from try 2]: the 10 s "still in hand" check was
  armed after EA's `StopUsingBook` returned, but `RequestState("x", "Exit")` waits for the clip, so in the stuck case the call had
  not returned (try 2: still in ReadBook 15 s later) and the check never ran. Now armed before the call (`BhStopStart`); log only.
- **Repro** `book-hand-fix-check` (on) / `book-hand-fix-check-off` (repros.json): `BookHandCheck "crowd ..."` = Tiara (household)
  plus up to 20 non-household readers at home bookshelves (bookcarry's crowd picker, `BcCrowdRead`); stop with animstop
  (stress, labelled), probe (stuck N of M), then `crowd push`: a user-directed `Terrain.GoHere` on every stuck Sim, so EA's
  `PutDownCarriedObjects` (runs at every interaction end, `InteractionQueue.cs:715,730`) meets each stuck book. Off passes on
  >= 1 stuck and bookhand_seen >= 1; on also needs every seen put-down fixed and no Sim stuck at the end. bookhand acts on
  household Tiara too (EA bug fix, allowed for controlled Sims).
- **Queue** `tools/harness/queues/bookhand-fix-1112.json`: `bp-bookhand-fix-off` then `bp-bookhand-fix-on`, rc1111d asi + ini,
  ts3fixes `ts3fixes.package.rc1111c-bhcrowd` (rc1111c + the crowd check, TEST ONLY; same tuning), `quitJobs`.
- **Open** [inferred]: if a stuck read stays blocked in `RequestState` (ReadBook never ends), no put-down runs and the Go Here
  only queues behind it; the run then fails at `crowd push` (seen not reached in 60 s) and the diag line says where it hangs.

### 8.1 Run 1 and the plant stress (2026-11-12)

- **Run 1** (rc1111c-bhcrowd) [verified, probe-out/queue/bookhand-fix-1112-2026-11-12-bp-bookhand-fix-off.log]: off stuck 15 of 21,
  on 15 of 21, but `crowd push` failed in both: 14-15 Sims still in ReadBook after 60 s, seen 0. The open point above happened:
  with the clips stopped, `StateMachineClient.RequestState` (yielding, `SimIFace StateMachineClient.cs` ~810-940) waits for an
  event that never comes (a null event only sleeps; AbortRequested and the 60 sim-minute timeout are read only after a real
  event), so `StopUsingBook` never returns, ReadBook never ends and no put-down can run. Forcing an abort would throw
  SacsAbortException out of the read (a Sim reset, which clears the hand without EA's put-down), so that route was not taken.
- **Plant stress** (labelled, crowd `stop` now): after EA's `StopUsingBook` returns with the book already in the inventory, the
  check takes it out and parents it to the right hand. With `CarryStateMachine` null (EA set it), that is the stuck state
  after a read that ended, and the read's own end runs `PutDownCarriedObjects` (ReadBook.Run -> StandardExit -> queue end,
  `InteractionQueue.cs:715`), as in the natural hits. `crowd push` removed. A Sim counts as stuck when a probe finds it or a
  put-down met its book (BhAt), since with the switch on the fix clears it before any probe.
- **Tiara's BedSleep ScriptError (off run, 01:27:13Z)**: "BedSleep left an object in the Sim's hand: BookGeneral. Attempting to
  put down that object failed: Put In Inventory" (`InteractionQueue.PutDownFailed.Callback`). Not bookhand's state: that branch
  runs only with a carry machine present (`InteractionQueue.cs:831-863`): Baseline-BP loads Tiara holding a book with a carry
  machine, our `crowd read` cancelled her BedSleep, EA queued PutInInventory and it failed. bookhand stays as it is (no hook on
  that path); `crowd read` now puts any book in Tiara's hand away first (test cleanup). Whether that EA failure is worth its own
  fix is open (one sample, caused by the test's cancel).
- **Queue** now pins `ts3fixes.package.rc1111c-bhcrowd2` (rc1111c + this change, TEST ONLY, same tuning).
