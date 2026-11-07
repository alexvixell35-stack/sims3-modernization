# virtual-swap-check: a hooked virtual keeps its flags and slot (2026-11-05)

- Change: `fix/virtual-swap-dispatch` 8b8d481 (docs/536), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: the player's (simler90's UPDATE242 core + patches); the fix is in ts3fixes and does not depend on the core.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `virtual-swap-check`, queue `tools/harness/queues/virtual-swap.json`.
- Machine load ok; crashes: none; script errors: none new (the one ErrorTrap report is its load correction log, 43 destroyed objects, the same as a load without the fix).

| job | result | old-way pair (derived / base) | KeepVirtual pair (derived / base) | `ScriptObject.Destroy` virtual | two-bowl stack destroyed |
|---|---|---|---|---|---|
| `bp-virtual-swap-check` | PASS in 11.2 min | 11 / 11 (the derived override skipped) | 2 / 11 | True | stack built; `kCleanedOrThrewAwayObject` x2 |

This base (main after the v0.2.6 hotfix) no longer hooks `ScriptObject.Destroy`, so the last two columns show EA dispatch with or without the fix; the test pairs are what tests `KeepVirtual` here.

Before the fix (run al231, old save, memalive's hook in place, no check): a washed 3-bowl stack sent no `kCleanedOrThrewAwayObject` and left its middle bowl unparented at 0,0,0.

Not tested: a long run with the fix (Destroy overrides that run again), the old save's dish stacks after a wash, the reaper / door / listener effects.

## Old save, memalive's hook + KeepVirtual (run al235)

- Change: ts3fixes at main 056fd22 (still hooks `ScriptObject.Destroy`) + the `KeepVirtual` commit of this branch; native: local build of 056fd22. Save: my old save (not shared). Player mods. 30.6 min, seed 12345, queue job `old-dishwash` (local), PASS; crashes: none.

| what | without the fix (al231) | with KeepVirtual (al235) |
|---|---|---|
| 3-bowl washes (zoeoe `CustomWashDishes`) | bottom bowl gone without `kCleanedOrThrewAwayObject`; middle bowl left unparented at 0,0,0 with the top one in its slot | 2 washes: all 3 bowls gone each time, `kCleanedOrThrewAwayObject` x3 per wash |
| exceptions | none | none (7 ScriptError files, all status / correction logs) |
| ErrorTrap "Destroyed Object Found" | none | 15 once at 7 min: Bowl 8, BowlLarge 2, CarPickup2door 4, MotorcycleChopper 1 |
| stuck Sims / `stuckcheck_reset` | 40, 28 / 2, 2 (the job's two earlier runs) | 35 / 3 |

