# 514. A taxi, limo, bus or fire-truck driver built from a pooled description that is gone (`npcdriverpool`, issue #166 build 37)

One player session (issue #166, build 37; the player's core log, run 21) logged `NullReferenceException` twice when a Sim
called a taxi: first in `Motives.CreateMotives` (from `Sim.OnCreation`), then in `SimDescription.Instantiate`, under
`NpcDriversManager.CreateNpcDriver` <- `Taxi.CreateDriver` <- `Vehicle.CreateTaxi` <- `Sim.GetNpcVehicle`. ErrorTrap's
report on the driver's description said "Valid: False", "No Household". This doc describes the ts3fixes switch
`npcdriverpool`. Off by default. Counters 2041-2042 (registry `fixes-2041`), ledger §912.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. Why a gone description comes back

- **The pools** [verified, IL]. `NpcDriversManager` (a field of `CarNpcManager`, saved with it) keeps one stack of
  `SimDescription` per driver kind in `mDescPools`; the index is the driver kind minus `Taxi`. `ReleaseNpcDriver` pushes a
  released driver's description on its stack and does not destroy the Sim.
- **No check on reuse** [verified, IL]. `CreateNpcDriver(kind)` looks at the top entry only. When its `CreatedSim` is
  null, it pops it and, if the description has the career outfit, instantiates it. It never checks that the description
  is still valid (`Dispose` clears `IsValidDescription`) or still in `Household.NpcHousehold`, where
  `Service.CreateSimDescription` puts every driver. A null entry would throw on the `CreatedSim` test itself.
- **What follows** [inferred from the run 21 log]. A description with no household is instantiated: `CreateMotives` throws
  on the Sim's household, the half-built Sim has no routing component, and `Instantiate` throws on it. The Sim who
  called the taxi gets no driver.
- **Prior fixes** [verified, IL]: NRaas ErrorTrap's `DerefNpcDriversManager` removes pool entries that match an object
  ErrorTrap is already dereferencing. A description that was disposed or left its household some other way is not
  matched, and run 21 had ErrorTrap installed. Nothing in `deadrefguards` or `deadrefsweep` reads the driver pools. The
  player's core build 37 prunes the pool inside EA's method (run 23 dropped one such entry and logged no error).

## 2. The fix (`npcdriverpool`, `mods/ts3fixes/src/NpcDriverPool.cs`)

A hook on `NpcDriversManager.CreateNpcDriver(NpcDrivers)`. Before EA's method, it reads the stack EA will read. When the
top entry is bad (`NpcDriverPoolCore.Bad`: no entry, not valid, or not in the NPC household) and EA would hand it out (no
Sim), the call is counted [2041], also with the switch off. With the switch on, `NpcDriverPoolCore.PruneTop` pops the bad
entries off the top [2042 per entry]. EA's method then runs unchanged: it reuses the next good entry or makes a new driver,
as for an empty pool. Entries below a good one are left for a later call, since EA reads only the top. The check sits in a
try/catch, so it never stops EA's method.

## 3. Checks

- **Self-test** (`node tools/selftest/npcdriverpool/run.ts`): every input combination of `Bad`, and `PruneTop` on a stack
  with two bad entries on top of a good one, a good one on top of a bad one, all bad, empty, and no stack.
- **Repro** `npc-driver-pool-check` (queue `npc-driver-pool.json`, Baseline-BP, `Fixes.NpcDriverPoolCheck`). It needs
  EA's method, so it runs with the original core mod (a core with the player's build 37 prunes the pool itself). In each
  arm, `make` asks EA for a taxi driver (`CreateNpcDriver`), releases it (`ReleaseNpcDriver` puts its description on the
  taxi pool), destroys the Sim and takes the description out of the NPC household: the state of run 21, a pooled driver
  with its outfits and no household. 5 s later the expect step throws unless that description is on top of the pool with
  no Sim, then asks EA for a taxi driver. `expect handed out` (off): EA hands it out (an exception, or a driver built from
  it), seen +1, dropped +0. `expect dropped` (on): a new driver from another description in the NPC household, the bad
  one off the pool, seen +1, dropped +1; the arm releases and destroys that driver again.
- **What the repro does not build**: a description disposed by the game itself. The repro makes the bad entry by taking it
  out of the household, one of the two conditions run 21's report showed.

## 4. Results

Run 1, 2026-11-01, Baseline-BP, original simler90 core mod, release v0.2.4 asi: **PASS in 8.7 min** (both steps in one
load).

| Arm | `make` | Expect step |
|-----|--------|-------------|
| off | a taxi driver made, released, destroyed, out of the NPC household; on top of the pool, no household | EA popped it and threw `NullReferenceException`; seen +1, dropped +0 |
| on | another taxi driver built the same way | EA made a new driver from another description; the bad one off the pool; seen +1, dropped +1 |

The off arm shows the bug [verified]: EA hands out the bad description and throws. The step records the exception type
only, not the method that threw (run 21's log names `Motives.CreateMotives`, then `SimDescription.Instantiate`).
