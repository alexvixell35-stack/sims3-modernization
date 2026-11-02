# 513. A service car with no car routing component drives to a lot (`carnoroute`, issue #166 build 33)

A harness session (issue #166, build 33) logged `NullReferenceException` in `CarNpcManager.DriveFromPointToMatrix`, called
from `DriveToLotInServiceCar.Run` through `DriveToLotInServiceCarWithinTime` and `DriveFromPointToMatrixWithinTime`. The
service Sim was already in the driver's seat (posture `SittingInVehicle`, set by `InitCarForLotRoute`). This doc
describes the ts3fixes switch `carnoroute`. Off by default. Counters 2039-2040 (registry `fixes-2039`), ledger §911.

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here) or seen live;
**[inferred]** = our reading, not yet checked in game.

## 1. The throw

- **No check** [verified, IL]. `DriveFromPointToMatrix(car, startPoint, targetMat)` takes `car.CarRoutingComponent`, creates
  a route on it, plans it and drives it. Neither the component nor the route is checked. `Vehicle.CarRoutingComponent` is
  the object's routing component cast to `CarRoutingComponent`, so it is null once the car is destroyed (no components
  left) or for a car whose routing component is of another kind.
- **What the throw leaves** [verified, IL]. `DriveFromPointToMatrixWithinTime` registers a "Teleport Car" alarm and a
  teleport helper before the drive, and removes both after it. The throw skips the removal: the helper stays in the
  manager's list and the alarm fires later. The interaction ends with the service Sim in a car that may be off screen
  (the car starts out of the world when it starts off screen) [inferred from the log and the IL].
- **What `false` does** [verified, IL]. A route that fails to plan makes the method return false. Both callers handle that:
  `DriveFromPointToMatrixWithinTime` removes its alarm and helper, then runs the helper's callback, which teleports the car
  to the target. The callback looks the car up by its id and does nothing when the car is gone. `DriveToLotInCar` returns
  the value to its caller.
- **Prior fixes**: none in the NRaas assemblies we have (ErrorTrap and Traveler refer to the car classes, not to this
  method). The player's core build 33 adds the same check inside EA's method.

## 2. The fix (`carnoroute`, `mods/ts3fixes/src/CarNoRoute.cs`)

A hook on `CarNpcManager.DriveFromPointToMatrix(Car, Vector3, Matrix44)`. Before EA's method, `CarNoRouteCore.CannotDrive`
checks for no car or no car routing component. Such a drive is counted [2039], also with the switch off. With the switch
on the hook returns false [2040], as for a route that failed to plan, and EA's method does not run. Every other drive runs
EA's method unchanged. The check sits in a try/catch, so it can never stop a drive.

## 3. Checks

- **Self-test** (`node tools/selftest/carnoroute/run.ts`): the four car and component combinations.
- **Repro** `car-no-route-check` (queue `car-no-route.json`, Baseline-BP, `Fixes.CarNoRouteCheck`). It needs EA's method,
  so it runs with the original core mod (a core with the player's build 33 returns false itself). In each arm, `make`
  creates a maid's service car with EA's `CreateServiceCar` and destroys it. The game disposes it a little later, and
  `Dispose` drops the object's components [verified, IL; `GetComponent` then returns null]. 5 s later the expect step
  throws if the car still has its routing component, and calls the method on it through a delegate (the hooked method,
  as EA's callers reach it). `expect thrown` (off): EA's
  `NullReferenceException`, seen +1, returned +0. `expect false` (on): false, no exception, seen +1, returned +1. Each throws
  on a wrong outcome.
- **Not covered**: `CreateRoute` returning null on a car that has its component. The player's build 33 also checks that.
  A hook can check it only by calling `CreateRoute` itself before EA does, so it is left out. The repro calls the method
  directly; it does not run a service visit with a destroyed car.

## 4. Results

**Run 1** (2026-11-01, Baseline-BP, FAIL in the set-up): right after `Destroy`, the car still had its routing component,
and the step threw as written. The components go only when the game disposes the object, so the repro now destroys the
car in a step of its own and waits 5 s.

**Run 2** (2026-11-01, Baseline-BP, `bp-car-no-route-check` PASS in 8.5 min, original core mod) **[verified, log]**: right
after `Destroy` the maid's van still had its routing component; 5 s later it was gone. `expect thrown` (off): EA's
`NullReferenceException`, seen +1, returned +0. `expect false` (on): false, no exception, seen +1, returned +1.
