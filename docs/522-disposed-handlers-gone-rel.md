# 522. An object's ObjectDisposed event keeps relationships nobody holds (`deadrefsweep` phase 12)

Issue #166, core build 41. `GameObjectRelationship` (a Sim's liking of a car or a bird) subscribes its
`GameObjectDisposed` handler to the object's `ObjectDisposed` event in its constructor, and nothing unsubscribes it:
`SimDescription.Dispose` leaves `GameObjectRelationships` alone, and a decayed relationship (`RemoveRelationship`,
liking at 0) only leaves the Sim's list. The handler, and through it the relationship and its description, is saved
with the object; a load-time graph reset (NRaas ErrorTrap) then resets the car (the harness saw a CarExpensive1 and a
MotorcycleRacing reset for deleted descriptions). This doc describes the holder and the ts3fixes clean-up: a twelfth
phase of the `deadrefsweep` switch (off by default; counter 2048, registry `fixes-2048`; ledger §918; docs/205 §2 for
the sweep, docs/519 for the phase it follows).

Claim marks: **[verified]** = read in the decompiled game code, or seen live; **[inferred]** = our reading, not yet
checked in game.

## 1. The holder

- `GameObjectRelationship(SimDescription, IGameObject)` does `gameObject.ObjectDisposed += GameObjectDisposed`; no
  method of the class or of `SimDescription` removes it **[verified]**. `Vehicle.GetNewRelationship` and the bird's
  equivalent are the creators.
- The handler only removes the relationship from `mSimDesc.GameObjectRelationships` and tells the HUD **[verified]**:
  run for a relationship that list no longer has, or whose description is deleted, it does nothing useful.

## 2. The clean-up

Phase 12 of the hourly spread pass (`DeadRefs.cs` `HandlerHolders`), for every `GameObject` with a subscribed
`ObjectDisposed`: each handler whose target is a `GameObjectRelationship` nobody holds (`RelGone`: its description null
or not valid, or the relationship not in that description's `GameObjectRelationships`) leaves the invocation list
(`Delegate.Remove`, the field written back once). The event's backing field and the relationship's `mSimDesc` are
private, so both are read by reflection. Counter [2048] counts handlers removed (`deadrefsweep_handlers`). The pass
before a save is unchanged (phases 1-3 only). Nothing runs with `deadrefsweep` off.

## 3. Test

Repro `disposed-handler-gone-check` (queue `tools/harness/queues/disposed-handler-gone.json`, save Baseline-BP). The
hard state is built by the agent check `DeadRefHandlerCheck seed` without destroying anything: a vehicle of the world
and a townie's description (same pick rule as docs/516), `Vehicle.GetNewRelationship` makes the relationship (and
subscribes the handler) and it is never added to the description's list, so it is one nobody holds. Off arm:
`DeadRefCheck sweep` refuses and `expect left` sees our handler still subscribed (the check then removes it). On arm:
the pass is started at once and `state` polled until our handler is gone; `expect cleaned` requires that; the job
requires `deadrefsweep_handlers >= 1`.

## 4. What is left

Relationships a live description still holds are never touched, deleted or not reachable otherwise. Core build 41's
other half (the relationship list of a deleted description itself) is EA's `SimDescription.Dispose` business and is not
in this phase.
