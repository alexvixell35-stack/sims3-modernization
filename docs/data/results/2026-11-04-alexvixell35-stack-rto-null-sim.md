# RouteToObjRO: a call without a Sim (2026-11-04)

Machine: Steam 1.67, contributor alexvixell35-stack. Save "1111 kit5" (old save), seed 12345, `-disablestoryprogression`.

## The crash

A 30-minute soak with the route stack of PR #254 / #256 on (including `[RouteToObjRO] Enabled=1 Mode=1`) died after 29.3
minutes:

- `ACCESS_VIOLATION reading from address 0x000002c0` in ts3native.asi, on the thread that owns the route job.
- The faulting instruction is `mov eax, [eax+0x2C0]` with `eax = 0`. It is `in.simList = *(sim + 0x2C0)` in the clone's
  preparation (rtoro.cpp), reached from `RtoCallStub` with `sim == NULL`.
- The stack holds `0x831638`, the return of R-call (`0x831633` -> `0x830340`).

## The game's answer

`0x830340` checks its Sim argument first (`cmp [ebp+0xC], 0` at `0x830352`). When it is NULL, it writes 0 to `out[0]`,
`out[2]` and `out[3]` and returns, reading nothing else [verified disassembly]. The R-acq stub had already skipped the
lock, so the stub must answer without the game's lock too.

## Fix

In `RtoCallStub`, before the clone runs:

- skip path: answer code 0 (`out[0] = out[2] = out[3] = 0`), state `RTO_ANSWERED`, no clone, no game call, no lock;
- shadow path (lock held): the game answers.

The clone's other reads of the Sim (`+0x48`, `+0x4C`) come after the preparation, so the early answer covers them too.

## Checks

- `native/ts3native/build.cmd`: asi and selftest ok. New case in `TestRouteToObjRO` (rtoro-test.h): a skipped call
  with a NULL Sim is answered 0, the state is `RTO_ANSWERED`, the game's route is not called, `rtoTaken` does not move,
  and the release leaves the lock untouched.
- In game: the same stack ran again with the fix (results on the PR when the run is done).
