# 504. A null service from the save breaks every loop over `Services.AllServices` (`nullservice`)

Contributor doc (range 500-599). Switch `nullservice` (ts3fixes, off by default), counter 2014, ledger §903. Status: verified in
game 2026-11-01 on BP and LR (§4).

## 1. The bug

`Services.sAllServices`, the list of every service (maid, mail carrier, repairman, Grim Reaper, babysitter, the resort
services...), is a `[PersistableStatic]` field, so it is written into the save as it is. When a saved service's type no
longer loads (a removed mod's or an EP's service), the list comes back from the save with a null element in its place.
**[verified, IL]** EA's readers of the list never check for null:

- 15 methods read it through the `Services.AllServices` getter, among them `ResortStaffedObjectComponent.GetService`,
  `Services.Simulate`, `AddService`, `RemoveService`, `RemoveSim`, `ClearServicesForLot`, `CreateNewNPCForPool` and
  `TownieMaker.GetModifiableServices`.
- 3 read the field directly: `Services.PostWorldLoadStartup`, `Services.PostWorldLoadShutdown` and
  `Services.IsSimDescriptionInAnyServicePool`.

Each one calls a member of the element (mostly `get_ServiceType`) and throws `NullReferenceException` on the null.

What that does **[verified, live; inferred for Simulate]**:

- **At object load:** `GameObject.Init` starts each component, and `ResortStaffedObjectComponent.OnStartup` calls
  `Register` and then `GetService`, which throws at IL 0x16. The exception skips the rest of that object's startup, so its
  later components never start. Seen live twice on a player's save:
  - 2026-10-26: seven exceptions;
  - 2026-11-01 (kit5 A load): eight exceptions.

  All of them came from one custom-content bar object per lot, and all were reported by NRaas ErrorTrap.
- **`Services.PostWorldLoadStartup`** throws at the null in its first loop, so its later loops never run. One of those is
  the per-service clean-up of pools that point at Sims who no longer exist.
- **`Services.Simulate`** catches only `ResetException`. If the services task reaches the null, it ends for the session:
  no new maid, mail, repair or Grim Reaper visits until a reload **[inferred from the IL; not seen live, because NRaas
  Overwatch removed the null first on the save above]**.

**Prior art [verified on the save above]:** NRaas Overwatch removes null services in `CleanupServicePools` ("Empty Service
Removed"). It does so only in its delayed world-load step, after object load, so the object-load exceptions above still
happen with Overwatch installed, and players without Overwatch keep the null. No ts3fixes or ts3native fix touched this
list before.

## 2. The fix (`nullservice`, `mods/ts3fixes/src/NullService.cs`)

With the switch on:

- **The `Services.AllServices` getter drops every null element** from the list before returning it. This uses EA's own idiom
  from `GameObject.Init`: remove null until none is left (`NullServiceCore.DropNulls`).
- **`Services.PostWorldLoadStartup`**, which reads the field directly, **calls the getter first**.

EA's code runs unchanged. [2014] counts the null elements dropped, and the first five drops are logged.

- **Why the getter:** 15 of the 18 readers go through it, and the services task reads it every few sim minutes, so the list
  is clean long before the other two direct readers run.
- **Enumerators (self-test):** with no null in the list, the drop changes nothing, not even the list version. So a caller
  that is enumerating the list while something inside its loop reads the getter is not disturbed. With a null in the list,
  that caller would have thrown on it anyway.
- **When the hooks apply:** ts3fixes swaps both methods from its static constructor at script load, before any world loads.
  So with the switch on in the tuning, the object-load path in §1 sees the clean list.
- **Side effects [inferred]:** none on a list without nulls. A dropped null was never a service EA could use, so nothing is
  lost. The save written afterwards no longer holds the null.

## 3. Checks

- **Self-test** (`node tools/selftest/nullservice/run.ts`): every placement of nulls in lists of 0-6 elements (count, order
  of the rest), the null list, and a drop inside an enumeration of a clean list.
- **Repro** `null-service-check` (`tools/harness/repros.json`, queue `null-service.json`, jobs `bp-null-service-check` and
  `lr-null-service-check`, 5 min each). The test saves hold no null service, so the check
  (`Ts3Fixes.Fixes.NullServiceCheck`) builds the bug state for one call only:
  1. adds a null to `Services.sAllServices`;
  2. runs an EA reader of the getter, `TownieMaker.GetModifiableServices` (read-only);
  3. takes the null out again before returning, so nothing else in the session ever sees it.

  `broken` (switch off) throws unless the reader threw `NullReferenceException`. `fixed` (switch on) throws unless the
  reader returned, so a wrong outcome fails the step and the job. `fixesRequire` `nullservice_dropped>=1`.
- **Not covered by the repro:** the object-load and `PostWorldLoadStartup` paths. They need a save that holds a null
  service, which none of the shared test saves do. The object-load path goes through the same getter.

## 4. Results

Queue `null-service`, 2026-11-01, build a03b824 (the same code as this branch; rows in
`docs/data/results/2026-11-01-queue-null-service.md`) **[verified]**:

| job | save | switch off: the reader | switch on: the reader | `nullservice_dropped` | result |
|---|---|---|---|---|---|
| `bp-null-service-check` | Baseline-BP (31 services) | threw `NullReferenceException`, our null still in the list | returned 27 services, our null gone | 1 | PASS, 8.1 min |
| `lr-null-service-check` | BigSave-LR, day 406 (32 services) | threw `NullReferenceException`, our null still in the list | returned 30 services, our null gone | 1 | PASS, 8.4 min |

Before each step the list held 0 nulls (the shared saves hold no null service), and after each step it held 0 again.

The two earlier rows of that day are setup errors, not results:

- 09:10: the worktree had no `build/ts3-nointro.package`, so `cycle.ts --restart` failed before the game started.
- 09:11: the off arm failed, because the reader returned. That player's own core mod already patches
  `Services.get_AllServices` to drop nulls, so the getter took our null out with the switch off. The PASS rows ran with
  that core mod moved aside and simler90's original core in, so the getter was EA's. The off step's wrong-outcome
  message now names this case.

**Not shown in game:** the object-load and `PostWorldLoadStartup` paths (§3) and I-903-3. None of the shared saves holds
a null service.

Written with Claude Code (AI help).
