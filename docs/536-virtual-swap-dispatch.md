# 536. A hooked virtual method lost its overrides (`SwapMethod` copied the hook's flags and slot)

ts3fixes hooks a method by copying the hook's 40-byte `MonoMethod` header over the original's (docs/04). That copy also
moved the hook's flags word and vtable slot into the original. When the original is a virtual method that call sites
name (the slot-introducing declaration), the interpreter then treated it as non-virtual: every receiver ran the hook,
and no override ran. Since v0.2.3 this hit `ScriptObject.Destroy`, so every `x.Destroy()` ran only the base body. The
fix keeps a virtual original's flags and slot (`KeepVirtual` in `Fixes.cs`). It has no switch and no counter. The
ledger section is §940, and the live check is the `virtual-swap-check` repro.

Claim marks: **[verified]** = read in the exe's disassembly or the decompiled game code, or seen live; **[inferred]** =
our reading, not yet checked in game.

## 1. The bug

- **What the copy moved [verified, `Fixes.SwapMethod`; docs/107 §5.1]:** bytes `+0x00..+0x17` and `+0x1C..+0x27`
  come from the hook, and only the name at `+0x18` stays. `+0x00` is the flags word (`MethodAttributes`), and the
  dword at `+0x1C` holds the vtable slot in bits 12-31.
- **How the resolver reads them [verified, TS3W.exe 1.67]:** the interpreter's callvirt resolver `0xE51FC0` reads the
  flags word of the method that the call site names (`movzx eax, word [esi]`). If FINAL (`0x20`) is set or VIRTUAL
  (`0x40`) is clear, it returns that method without dispatching (`0xE51FCC`-`0xE51FD6`). Otherwise it takes the slot
  from `+0x1C >> 12` and loads the receiver class's slot table at `+0xBC` (`0xE52007`-`0xE5201F`).
- **Why the hook replaced every override [verified]:** a hook is a plain instance method, so it has no VIRTUAL flag and
  slot -1. After the copy, a callvirt site that names the hooked method ran the hook for any receiver.
- **Which sites name it [verified]:** C# names the slot-introducing declaration at every virtual call. For example,
  zoeoe's wash names `ScriptObject::Destroy` for a `GameObject` local. So all of those sites were affected.
- **Hooks on overrides were not hurt [verified]:** for example `GoHome.Run`. Their call sites name the base
  declaration, which was unchanged, and the slot table still holds the same `MonoMethod*`. Only hooks on a
  slot-introducing virtual that has overrides broke dispatch.

### 1.1 The affected hooks [verified, metadata audit of every `Patch(typeof(...), "...")` target against the game DLLs]

| Hook | Target (newslot virtual) | Overrides that never ran | In release |
|---|---|---|---|
| memalive (`MemSuspects.cs`, `AliveDestroyHook`) | `ScriptObject.Destroy` | 75, among them `GameObject.Destroy`, `PreparedFood.Destroy`, `Sim.Destroy` | v0.2.3, v0.2.4, v0.2.5 |
| preferred service (`EaFixes.cs`, `PreferredServiceHook`) | `Service.FindSimForAssignment` | `GrimReaper`, `TimeTraveler`, `ResortMaintenance` | v0.2.5 |
| rabbit-hole door (`EaFixes.cs`, `RabbitHoleDoorHook`) | `RabbitHole.AnimateDoorOpen` / `AnimateDoorClose` | 9 / 8 rabbit holes (`FutureTheatre`, `PirateDiner`, `CityHallEP11`, `Hideout`, ...) | v0.2.5 |
| vacation listeners (`EaVacation.cs`, `VacListenerHook`) | `EventListener.RestoreState` | `CountListener`, `CountListenerDistinct`, `CountListenerExclusive`, `TotaledListener` (their own hooks were never reached either) | v0.2.5 |

All four are applied at startup whatever their switch says, so the switch did not matter. The audit only reads The v0.2.6 hotfix (main 32032d0, docs/67 §33) stops patching all four; with `KeepVirtual` they can be patched again.
literal `typeof` targets. Targets built at run time (the `AutonomyT` hooks, `ApplyTripSubclasses`) were not
checked, but they go through the same `SwapMethod` and get the same fix.

### 1.2 What players saw

- **Destroy [verified, run al231 on the old save]:** a Sim washed a 3-bowl stack with zoeoe's `CustomWashDishes`. The
  wash ends with `EventTracker.SendEvent(kCleanedObject)` and then `objectInRightHand.Destroy()`.
  - The bottom bowl A was gone afterwards, but `kCleanedOrThrewAwayObject` never fired. `PreparedFood.Destroy` sends
    it first, so that override did not run.
  - The middle bowl B was left with no parent at 0,0,0, with the top bowl C still in its slot.
    `GameObject.Destroy`'s containment loop would have destroyed B, so it did not run either. A listener census of all
    three events found nothing else involved.
  - **[inferred]:** the same base-only destroy skips the cleanup of all 75 overrides: alarms, broadcasters, VFX,
    inventories and contained objects.
- **Grim Reaper [inferred]:** `GrimReaper.FindSimForAssignment` keeps one reaper, `mPool[0]`. The base version picks
  any free pool NPC, and when none is free it creates a new one. So a busy reaper could get a second Grim Reaper
  description in the pool.
- **Rabbit-hole doors, vacation listeners [inferred]:** these rabbit holes ran the base door animation instead of
  their own. Count and total listeners restored only their base state after travel.

## 2. The fix

`SwapMethod` calls `KeepVirtual(saved, repl)` before it writes. When the original's flags have VIRTUAL, the new header
keeps the original's flags word and the slot bits (`+0x1C` bits 12-31). The low 12 bits stay the hook's.

- **Hooked base virtual [verified, code]:** the resolver dispatches again. Overrides run, and the hook runs when a
  receiver has no override, or when an override calls `base.X()` (a `call`, so no resolver).
- **Hooked override [verified, code]:** nothing changes. It stays reachable through the base slot, as before.
- **Stub swap `SwapMethod(stub, target)` [verified, code]:** the original is the stub, which is non-virtual, so nothing
  changes. The stub keeps receiving the target's whole header, and hooks call it with `call`.
- **Agent's own copy [verified, code]:** the ts3agent `PatchHandlers.SwapMethod` (test commands only) still copies the
  old way. It was left unchanged.

**What changes in play [inferred]:** every `Destroy()` runs its overrides again, as EA wrote them. Containers destroy
their contents, food sends its event, and Sims run `Sim.Destroy`. Memalive's tracker now sees an object only when its
override chain reaches `ScriptObject.Destroy` through `base.Destroy()`, not once per call site. An override that does
not call base is no longer tracked.

## 3. The check (`VirtualSwapCheck`, repro `virtual-swap-check`)

- **Test pairs:** two fresh base/derived pairs (`F` returns 1 / 2) are set up, then each base `F` is hooked at check
  time (the hook returns 10 + the original). One pair is hooked the old way and one with `KeepVirtual`. Each is called
  through a base-typed callvirt site that is compiled after the swap.
  - Expected for the old way: the derived receiver gives 11. This is the bug.
  - Expected for `KeepVirtual`: derived 2, base 11.
- **The real target:** `ScriptObject.Destroy` must still read as virtual. On main since the v0.2.6 hotfix nothing hooks it, so
  this part and the bowl part pass with or without `KeepVirtual`; only the test pairs test the fix there.
- **A real destroy:** a new bowl with a second bowl in its stacking slot is destroyed. Both bowls must send
  `kCleanedOrThrewAwayObject`. If the stack cannot be built out of the world, the one bowl must send it.

## 4. Result

**PASS** (2026-11-05, run al233): job `bp-virtual-swap-check`, 11.2 min, on the player's mods (simler90 core + patches),
release v0.2.5 asi, Baseline-BP. The check returned: old way: derived 11, base 11; KeepVirtual: derived 2, base 11;
`ScriptObject.Destroy` virtual True; bowl stack built True, `kCleanedOrThrewAwayObject` x2. No new script errors. The
one ErrorTrap report is its load-time correction log (43 destroyed objects in the save), the same as a load without the
fix 14 minutes earlier. This branch is on main after the v0.2.6 hotfix, which no longer
hooks `ScriptObject.Destroy`: the Destroy and bowl parts therefore show EA dispatch, not the fix on a hooked Destroy (a run
with memalive's hook and `KeepVirtual`, on the old save, is next). Results: `docs/data/results/2026-11-05-alexvixell35-stack-virtual-swap.md`.

**The old save with memalive's hook and `KeepVirtual` (2026-11-05, run al235, 30.6 min, PASS):** ts3fixes at main 056fd22
(v0.2.5 + #272, still hooking `ScriptObject.Destroy`) plus `KeepVirtual`, the player's mods, the same 3-bowl washes with
zoeoe's `CustomWashDishes` as al231 (§1.2).
- Two washes of a 3-bowl stack: all three bowls gone each time; `kCleanedOrThrewAwayObject` fired three times per wash
  (bottom, middle, top), so `PreparedFood.Destroy` ran and `GameObject.Destroy` destroyed the bowls it contained.
  Nothing was left at 0,0,0. In al231, without the fix, the bottom bowl sent no event and the middle one was left.
- No exceptions: the 7 ScriptError files are status logs (WishVariety, LagScope) and ErrorTrap correction logs.
- **New:** 7 min in, ErrorTrap's reference scan found 15 destroyed objects still referenced (`Bowl` 8, `BowlLarge` 2,
  `CarPickup2door` 4, `MotorcycleChopper` 1) and dropped the references. No old-save run without the fix logged one.
  [inferred] The destroys that now run (contained bowls, vehicles' overrides) leave a reference in some list, as in the
  unmodded game; ErrorTrap clears them. Which list is not known.
- Stuck Sims 35 (the two earlier runs of the job: 40, 28), `stuckcheck_reset` 3 (2, 2).

Not tested:
- a long run with the fix: the Destroy overrides that now run again may surface their own bugs, which the base-only
  destroy had hidden;
- the reaper, door and listener effects in play.
