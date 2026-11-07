---
name: ts3-layout-repro
description: Turn a player's sketch into a repro: a text map built on a lot with the real build and buy tools (agent step build_layout), parser check offline, smoke queue. Use for "build this layout", "repro this floor plan", "turn the sketch into a repro".
---
# Layout repro

Step `build_layout {map, legend?, origin?, rotation?, level?, house?, margin?, lights?, clear?}`, then poll `{result: true}` (repro `until`: `passed` true, `failOn: "error"`). Full arg list: top of `mods/ts3agent/src/plugin/Handlers/LayoutHandlers.cs`.

```
W w w w
E X w s      w wall, e empty, X the selected Sim's start, s stove, b small trash can; legend adds chars:
E E w e      {"f": {"name": "fridge", "facing": "N"}}
E e w e
E b w e
```

A thin wall takes no tile. A column or row with a run of 2+ `w` collapses to the grid line between its neighbours; a lone `w`, or an object on a wall line, is an error. So X and `s` above stand on opposite faces of one wall; size 3 x 4, walls x=2 and z=0 (rules: `mods/ts3agent/src/plugin/LayoutMap.cs`).

Check a map offline, no game:

```
node tools/harness/layout-map.ts <map.txt>
node tools/harness/layout-map.ts --self-check
```

Smoke job, correctness only: `tools/harness/queues/layout-repro-smoke.json` (repro `layout-repro-smoke`). The report is in the `stove-wall: layout` lines of ts3native.log. Do not start the game or cycle.ts from this skill; a chain owns the game.
