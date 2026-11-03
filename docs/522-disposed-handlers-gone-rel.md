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

Phase 12 of the spread pass (hourly; since 2026-11-03 only after a Sim leaves or daily, docs/205 §2.3a) (`DeadRefs.cs` `HandlerHolders`), for every `GameObject` with a subscribed
`ObjectDisposed`: each handler whose target is a `GameObjectRelationship` nobody holds (`RelGone`: its description null
or not valid, or the relationship not in that description's `GameObjectRelationships`) leaves the invocation list
(`Delegate.Remove`, the field written back once). The event's backing field and the relationship's `mSimDesc` are
private, so both are read by reflection. Counter [2048] counts handlers removed (`deadrefsweep_handlers`). The pass
before a save is unchanged (phases 1-3 only). Nothing runs with `deadrefsweep` off.

## 3. Test

Repro `disposed-handler-gone-check` (queue `tools/harness/queues/disposed-handler-gone.json`, save Baseline-BP). The
hard state is built by the agent check `DeadRefHandlerCheck seed` without destroying a Sim: a car made out of world
(`CarHatchback`, destroyed again at `expect`; a vehicle of the world is the fallback) and a townie's description (same
pick rule as docs/516), `Vehicle.GetNewRelationship` makes the relationship (and subscribes the handler) and it is never
added to the description's list, so it is one nobody holds. Off arm:
`DeadRefCheck sweep` refuses and `expect left` sees our handler still subscribed (the check then removes it). On arm:
the pass is started at once and `state` polled until our handler is gone; `expect cleaned` requires that; the job
requires `deadrefsweep_handlers >= 1`.

Run 1 (2026-11-02 00:42, al15) FAIL, a seed fault: it took the first vehicle of the world, a `LightweightTaxi`, which
despawned about 20 s after the seed; a destroyed object is outside the pass, `HandlerGone` reads 0 on it while `mine`
still read the field, so the on arm polled `mine` for 300 s. The off arm had passed for the same wrong reason (the
taxi was gone before `expect left` looked). The seed now makes its own car, `state` reports `destroyed`, and `expect`
throws when the vehicle is gone instead of judging. Rerun pending.

Result (2026-11-02 03:18, `bp-disposed-handler-gone-check`, PASS in 15.1 min, al18 rerun): off arm, the seed made a `CarHatchback` out of world, gave Lenny Shutter a relationship with it (EA's constructor subscribed the handler) and dropped the relationship from the description; the sweep refused (switch off); 20 s later `expect left` found the handler still subscribed (mine true, gone 1) and removed it. On arm, the same seed; the pass started at once (safe mode) and the first poll 20 s later read handlers 0, mine false, gone 0; `expect cleaned` passed; `deadrefsweep_handlers` 1 (the job's gate). No script error in either arm.

## 4. What is left

Relationships a live description still holds are never touched, deleted or not reachable otherwise. Core build 41's
other half (the relationship list of a deleted description itself) is EA's `SimDescription.Dispose` business and is not
in this phase.
