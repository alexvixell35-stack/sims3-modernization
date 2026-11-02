<!-- Draft reply for issue #97. Not posted. -->
Thanks, this is a careful write-up. We checked all seven rows against the decompiled scripts, and each one holds in the code. We took the in-game effects from your runs; none of this has run on our side yet.

- **Row 1, verified.** The pickup's `Dispose` stores only the inventory and bot chips before `Destroy()`. `SetupSelectableSimManagers` drops `DnPExportData` outside travel worlds and Passport. `Sim.OnCreation` already reads `TravelBuffs` back, and `NeedsOpportunityImport` already triggers `TravelFixup`. So the export plus the dream branch is all the return needs.
- **Row 2, verified (the read).** We found no EA path that leaves a live opportunity with an empty slot during play. Mods or a missed `Fixup` are our guess for the trigger. A second way the same store throws: `ListenerStates` is a fixed array of 3, so an opportunity with more listeners gets `IndexOutOfRange`.
- **Row 3, verified.** We also found that when every stored adult is skipped, no `GoHome` is pushed. The trip then sits in `ParentsAreBack` for good, because `OnGetHome` is its only exit.
- **Rows 4 and 5, verified.** These are the paths through `NotifyParticipantDeleted` and `CleanUp` -> `ResetStatics`.
- **Row 6, verified.** The four overrides read `info` again after `base`, so we guard each of them, not only the base.
- **Row 7, verified, (a) and (b).** (a) is wider than `TryResumeOpportunity`: any target whose Sim object was re-created since the opportunity was accepted has a stale id. (b) also hits EA's own vacation return home.

What's built (`mods/ts3fixes/src/EaVacation.cs`, docs/210). There is one switch per row, all off by default, written from our own reading. Your code isn't in the issue, so nothing was copied.

- `vacexport`: the pickup runs EA's travel export before `Destroy()`. An adult on the trip gets its dream manager built from the export.
- `vacstoreguard`: a missing slot is re-added the way `Fixup` does it, and a store that still throws drops only that one opportunity. The rest come back, so we don't hold the whole import flag.
  - Load repair: teens rejoin first. A dead pickup whose adults never left ends the trip. Otherwise, a trip with no alarm, no teen or a dead pickup gets a bring-back 60 sim-minutes later.
  - Unlike your repair, we clear `ParentsAreGettingPickedUp` first. While it is still true, `ParentsNeedToReturn` looks for the pickup and returns.
- `vacreturnskip`: dead and moved-out adults are skipped. A return that throws, or that brings nobody back, ends the trip, and the exception is rethrown.
- `vacteendelete`: the teen leaves the trip, and a check runs 10 sim-minutes later.
- `vacexitreturn`: before `ResetStatics`, adults who are alive, in the household and without a Sim are re-created on the home lot. This is skipped while travelling and after the world-quit event. That event may fire too late to stop the shutdown case you saw in run 107; if so, it does the same harmless work you saw.
- `vaclistenernull`: `RestoreState(null)` gives a fresh listener.
- `vacimportfix`: stale target and source ids are pointed at the exported description's Sim. The expiry check is skipped only for tick 0, for that one call.

Counters `vacexport_exported` ... `vacimportfix_expiry` (slots 466-480). Self-check `tools/selftest/vacation`. Repros `bp-vacation-roundtrip`, `-exit`, `-teendelete`, `-orphan` and `-saves`; the saves repro includes your open case, a save while the taxi is still on its way. Queue `tools/harness/queues/ea-vacation.json`.

Two questions:
1. In runs 107-109, did any adult hold an opportunity with more than three listeners? That would show whether the `IndexOutOfRange` case is real.
2. For the save that failed right after accepting (run 73), do you still have the ts3native.log or ErrorTrap output?

The phone-chat portrait crash is a separate EA null read. We'd take it as its own issue (a candidate for `eanullguards`).
