# 505. A rabbit hole door the animation cannot open ends the Sim's entry (`rabbitholedoor`)

Contributor doc (range 500-599). Switch `rabbitholedoor` (ts3fixes, off by default), counter 2015, ledger §904. Status: verified in
game 2026-11-01 on BP (§4).

## 1. The bug

Seen live on Baseline-BP (Bridgeport) on 2026-10-31 and again on 2026-11-01 **[verified, ErrorTrap reports]**: a teen going
to school at Public School 67, through NRaas Careers' `GoToSchoolInRabbitHoleEx`, stopped at the entrance with

    SacsErrorException: SACS failed to process a request without indicating a specific reason for failure. (actor = 'Door2', state = 'Open')

thrown from `StateMachineClient.RequestState`, called by `RabbitHole.AnimateDoorOpen` and `RabbitHole.AnimateEnterRabbitHole`.

How EA's code gets there **[verified, IL]**:

- `RabbitHole.GetRabbitHoleStateMachine` acquires a `RabbitHoleGeneric` state machine for the rabbit hole the first time it is
  needed. `SetupStateMachineDoors` then gives the actor `Door<n>` to the door number of every enter and exit slot
  (`SlotToSlotInfo`) and puts it in state `Closed`.
- `AnimateDoorOpen(door)` raises `mDoorOpenCounts[door]` (an array of 10) first. When the count goes from 0 to 1, it asks the
  state machine for state `Open` of `Door<door>`. `AnimateDoorClose(door)` lowers the count and, at 1 to 0, asks for
  `Closed`.
- Nothing catches a failed request. The `SacsErrorException` leaves `AnimateEnterRabbitHole` and the interaction, so that
  Sim's entry ends.
- The count stays raised. Later Sims at that door raise it further, make no request and get in. So the error hits the first
  Sim at that door per load. That fits one report per run on this save **[inferred]**.

Why that door cannot open on that rabbit hole is not known: the animation data may have no `Door2`, or the slot may name a
door the model does not have **[inferred]**. The same failure was seen without the More Entry Points mod.

**Prior fixes checked:** no NRaas mod catches it (Careers' own interaction calls EA's `AnimateEnterRabbitHole`). A web search
found only general door-placement advice on the EA forums.

## 2. The fix (`rabbitholedoor`, `mods/ts3fixes/src/RabbitHoleDoor.cs`)

Hooks on `RabbitHole.AnimateDoorOpen` and `RabbitHole.AnimateDoorClose`. Both run EA's method unchanged inside a try/catch.
With the switch on, a `SacsErrorException` from it is skipped (counter 2015, the first five logged with the rabbit hole, lot
and door). The door stays as it is, and the caller goes on as if the animation had played.

- The count changed exactly as in EA's code, so open and close stay paired: the Sim that went in opens, and later closes.
- Every other exception passes through, and so does a `SacsErrorException` with the switch off.
- Only `RabbitHole` defines these two methods in the gameplay assemblies, so the hook covers every rabbit hole that uses
  them. `CityHallEP11` has its own door methods and is not covered.
- Cost: none when no request fails. A failing door costs one caught exception per open or close.

## 3. Checks

- **Self-test** (`node tools/selftest/rabbitholedoor/run.ts`) runs the swallow rule: only a `SacsErrorException`, only with
  the switch on. It also runs a model of EA's count pattern with every request failing:
  - switch on: the count ends at 0 and nothing reaches the caller;
  - switch off: the first open throws and the count stays raised, then the next open makes no request.
- **Repro** `rabbit-hole-door-check` (`tools/harness/repros.json`, queue `rabbit-hole-door.json`, job
  `bp-rabbit-hole-door-check`, 5 min, Baseline-BP). `Ts3Fixes.Fixes.RabbitHoleDoorCheck`:
  1. `broken` (switch off) opens, then closes again, each slot door with count 0 of every rabbit hole. It stops at the first
     door whose open throws `SacsErrorException` at the caller, puts that count back to 0 and keeps the door. It throws if no
     door threw.
  2. `fixed` (switch on) opens and closes the kept door. It throws unless both return, the count goes 0, 1, 0 and counter
     2015 went up.

  `fixesRequire` `rabbitholedoor_skipped>=1`.
- **Needs EA's door code:** a core mod that already catches this error makes the off step fail; run it with an EA or
  original simler90 core.
- **Not covered by the repro:** a Sim's real entry through the bad door. The check calls the two door methods directly, the
  way `AnimateEnterRabbitHole` does.

## 4. Results

Queue `rabbit-hole-door`, 2026-11-01, build 8d454cb (rows in `docs/data/results/2026-11-01-queue-rabbit-hole-door.md`),
`bp-rabbit-hole-door-check` **PASS** in 8.1 min **[verified]**:

- **Switch off:** of the 29 rabbit holes on Baseline-BP, the check tried 78 slot doors with count 0. Only one open threw
  `SacsErrorException` at the caller: `SchoolRabbitHole` on Public School 67, `Door2`. That is the door of both live reports.
  The other 77 opened and closed without an error.
- **Switch on:** open and close of that door both returned, the count went 0, 1, 0, and `rabbitholedoor_skipped` was 2. So
  the `Closed` request fails on that door too.
- The applied log listed `RabbitHole.AnimateDoorOpen: ok` and `RabbitHole.AnimateDoorClose: ok`.
- The run used simler90's original core: that player's own core mod already catches this error, so the off step would not
  see it.

**Not shown in game:** a Sim's real entry through that door with the switch on (§3), and I-904-3.

Written with Claude Code (AI help).
