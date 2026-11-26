# 852. `[Discourage] UofDefer`: event 9's footprint insert is queued behind the object's records while a job runs (2026-11-16, contributor, default ON since 2026-11-19)

Code: `native/ts3native/discourage.cpp` (`UofQueue`, `UofApply`, record kind `fp2 == 3`, one branch in `DiscApplyOne`, `UolFlushFor`), selftest block "UofDefer (docs/852)". Needs `[Discourage] LostRemovalFix=1` (docs/89 §16). Tags: [verified] = log or code; [measured] = run numbers; [inferred] = reasoning.

## 1. Problem

Event 9 (an object's lot change) calls the footprint insert `0x833680` (`UpdateObjectLot`). With LostRemovalFix on, the object's removal (kind 2) was queued while a route job ran; the insert then flushes that record first (`UolFlushFor`), which waits for the job, or `0x833680` waits for `+0x918` itself. On Bridgeport the sim thread waited 10.2 s per 12-minute game at the `rtwait` site, most of it here [measured, local run ji1]. The job waited for is another Sim's.

## 2. Change

With `UofDefer=1`, while a job runs and the call is on the owner thread, in the live world, outside a snapshot run, the insert becomes a kind 3 record behind the object's other records instead (`UofQueue`): same pass-through rules as the other deferred records, FIFO per object (the drain runs remove, then insert, vanilla's order), applied at the existing drain points and flushes (destroy, private footprint and `Fp2FlushFor` readers, save, travel, quit). The new footprint shows up at the next drain, the staleness phase 2 already accepts [inferred]. Two inserts of one object are not merged: vanilla inserts twice too.

- No job running, an unknown object, a full queue (`g_udFull`), a world change or a nested call: EA's path, no record.
- At apply the object is looked up again: gone or a reused id is dropped (`g_udDropped`), so no stale pointer is used.
- Household: the change is not scoped to Sims. It defers a footprint update for any object of any lot, as the existing kind 1 / 2 records do; no Sim is moved, faded or changed.
- Off: `g_udMode` is 0; `UofSite` runs the same path as before.

## 3. Evidence

Not measured alone. The jk2 run (Bridgeport, Twinbrook, Isla Paradiso; 3 games, local build) carried it with the other switches of that run: Bridgeport logged 556 inserts queued, 536 applied, 20 dropped (object gone), 0 queue-full [measured, ts3native.log]. In the same run the `rtwait` waits fell (Bridgeport 11.4 -> 2.9 s against run jj1), but eight changes differed between the runs, so none of that is credited to this switch.

Selftest: a lot move during a job queues remove, remove, insert with no wait and the drain after the job leaves vanilla's map; a lone insert is applied at the drain with the lot it holds then; a gone object is dropped; with no job the path is today's.

Deciding test (not run): Bridgeport apartment in-load A/B of `UofDefer` alone, 2 games, both orders; compare `rtwait` ms, LR-flush waits (`fp2lrFlushUs`) and the dropped count.

## 4. Counters

No counter fields (the `ts3native_counters` layout is unchanged). `DiscRfLog` writes "uof defer: mode N queued N applied N dropped N full N".

## Evidence and default (2026-11-19)

Dalexlol's 17 fresh game loads (Apex + bridge, 493.5 min, all PASS): inserts queued 3,249, applied 3,236, dropped because the object was gone 13 (at most 6 in a job), queue full 0, 0 access violations, 0 crashes. The result of the insert is vanilla's (the same 0x833680 call on the same object, only later, behind the object's own records; a gone or reused object is dropped). It touches no Sim, so household, selected and unknown-scope Sims are unaffected. Default ON (`UofDefer=1`, needs `LostRemovalFix=1`) for the set after rc-1119b. The Bridgeport rtwait gain is still not measured alone.
