# 686: Sims waiting on each other and on blocked spots (route waits)

Contributor doc, 2026-11-21/22. On the contributor's combo branch this file has §1-§13 (obstacle stand-and-wait,
portal lanes, pets, chores, beds); this copy carries only §13, the part this change adds. The section numbers are kept
so the code comments (`docs/686 §13`) match. Built with `node tools/setup/build-fixes.ts`; nothing here needs ts3native.

## 13. A bed half that cannot be reached is made from the other side (`makebedboth`, `makebedbothhh`; 2026-11-22, `feat/makebedboth-1122`, built, default off; forced repro run, §13.5)

A player's wish: when half of a bed cannot be reached, the Sim makes it from the other side and the whole bed is
made. `MakeBedBoth.cs`, rules in `MakeBedBothCore.cs`, counters 4462-4469, check
`MakeBedBothCheck run | result | lot`, selftest `tools/selftest/makebedboth`, repro `makebedboth-check`.

### 13.1 How Make Bed works [verified, decompiled `Sims3.Gameplay.Objects.Beds` unless marked]

- **The walk.** `BedMakeBed.Run` collects the routing slots of every part that is unmade with no Sim in it and
  walks to one (`RouteToSlotList`). A double bed has two parts (`BedData`, slots 0 and 1), a single bed too
  (`BedSingleData`, slots 4 and 5: its two sides).
- **The run.** State machine `BedMakeBed`, `Enter_MakeBed_<Route0|Route1>`, loop `MakeBed`,
  `Exit_MakeBed_<...>`, then `BedMade = true` on that part. When the other part is unmade the interaction pushes
  itself again as a continuation.
- **The bug.** When the other part's slot is closed the continuation's walk fails. The bed stays half made, the
  definition's `Test` passes for any unmade part not in use, so autonomy offers the bed again and fails again,
  and the room keeps the unmade bed's penalty. A bed whose only unmade part is the closed one fails on the first
  walk. With `blockeduse` (§10, a separate change) Sims get into and out of a closed half, which makes such halves common.
- **The look.** No script call sets a half's made or unmade look. `BedData.BedMade` keeps a flag and the "dirty"
  alarm; `Bed.DoReset` sets the flags only. The look is the bed model's last animation pose, left by the bed's
  track of the get-out and make clips.
- **The clips** [verified, JAZZ `BedMakeBed` in `Game/Bin/Jazz/JazzData.package`, CLIP names from
  `FullBuild0`]. The Sim plays `a2o_bed_make_start / pat / smooth / stop_x`, the bed `..._bed`, as two tracks.
  `Route1` mirrors both. The parameter `MakeOppositeSide` changes the bed's mirror alone: at `Route0` the bed is
  mirrored when it is set, at `Route1` the bed is mirrored when it is not set. So with the parameter the Sim plays
  at the side it stands on and the bed plays the other half. The Sim is not moved: its clips are relative to its
  own slot.
- **Who sets it.** `BedData.SetPartParameters` sets it false; `BedSingleData` sets it to "other part unmade".
  A single bed's parts copy "made" to each other and Make Bed walks to the unmade part, so in the game the
  parameter is never true during Make Bed [inferred].

### 13.2 What is built

At the start of `BedMakeBed.Run` (swap with stub `RunOrig`), for a bed with two plain parts (`BedData` or
`BedSingleData`; no tent, igloo, pod, upper bunk) of which exactly one is unmade and free:

1. A route plan to that part's slots succeeds: the game's run. Double beds whose both sides are open are walked
   round as before.
2. Otherwise, when the other part is made, has no Sim and is not in use, and a plan to its slots succeeds: the two
   parts' made flags are swapped for the run. The game's own run then walks to the other part's slot (no walk when
   the Sim stands there after the first half), animates there and sets that part made at its end. Both parts are
   made, the game pushes no continuation, a Murphy bed gets its Close, the events fire as in the game.
   `BedData.SetPartParameters` and `BedSingleData.SetPartParameters` (two swaps, no yield) set
   `MakeOppositeSide` for that part while this Sim is in it, so the bed animates the far half.
3. The run does not end with that part made (walk failed, part taken, reset, error): the flags are put back.

Both halves unmade and one closed: the game's run makes the open one, and its continuation is case 2. The plans
are read-only (`Route.PlanToSlot`), at most two per Make Bed run on such a bed. With the bed made `Test` no longer
passes, so autonomy stops offering it. No change to `Test` itself.

Scope: `makebedboth` = Sims the player does not control, `makebedbothhh` = household and selected Sims, each
stands alone. Off = counted (`would`), nothing changed. `makebedbothlog` writes one `makebedboth:` line per act.

| Counter | Meaning |
|---|---|
| 4462 | Make Bed runs on a bed with exactly one unmade free half |
| 4463 | second half made from the first side |
| 4464 | closed half made from the other side (first make) |
| 4465 | kept: the half can be reached (the game's walk) |
| 4466 | kept: no side to make it from (other half taken, or closed too) |
| 4467 | errors (`makebedboth: error at begin | end | parameter | run`) |
| 4468 | would: switch off, or household without `makebedbothhh` |
| 4469 | run ended early, flags put back |

### 13.3 Limits [inferred unless marked]

- **By eye, not yet seen.** The Sim pats the half in front of it while the far half is the one that moves. The
  mirrored bed track on a double bed with the Sim's unmirrored track is the game's own state-machine path, but one
  the game never takes, so the far half's look after it needs a look in the game. If the track does not move the
  far half, the flags are still right (no retries, no penalty) and that half looks unmade until it is next used.
- The bed's mirror is not cleared on the `Route0` exit path of the state machine. Whether that outlives the state
  machine is not known.
- During the walk in case 2 the flags are swapped: another Sim's Make Bed can pick the same part, as it can in the
  game for any unmade part. A Sim that gets into the part first makes the game's run fail and the flags go back.
- A save made during such a run resumes in the stub (the game's run): the bed ends made, the parameter is not set
  again after the load.
- The closed half's "dirty" state restarts its 30-minute alarm when the flags are put back.
- With `blockeduse` on as well this rule comes first: the Sim stays at the open side.

### 13.4 Checks (2026-11-22, no game) and run plan

- `node tools/selftest/makebedboth/run.ts`: 406 checks (the decision table over every input, the end of the
  run, second half or first make, the parameter, scope). `build-fixes.ts` builds; the selftest compiles
  `BlockedUseCore.cs` too (the scope rule is shared with `blockeduse`); `swap-stack-audit` has the three new rows (`BedMakeBed.Run` yields, stub `RunOrig`).
- The forced repro `makebedboth-force` (a room built with `build_layout`, the bed set up with the agent's
  `bed_setup` step) stays on the contributor's combo branch until that agent step is upstream; §13.5 gives its results.
- Live: `makebedboth-check` once (off `BROKEN`, on `FIXED`, `members found=True`). A household with a double bed
  whose one side stands at a wall or behind furniture, `blockeduse blockedusehh` on so both halves get used:
  off arm 4462 and 4468 up, the bed stays half made; on arm `makebedboth makebedbothhh makebedbothlog`: 4463 or
  4464 up, `makebedboth: ... made from the ... side`, 4467 = 0, no `makebedboth: error at` line, the bed no
  longer offered. `MakeBedBothCheck lot` lists the lot's beds with an unmade half and the plan to each part.
  By eye: the far half's look after the run (13.3).

### 13.5 Results (2026-11-22, Steam Deck)

- Forced repro `makebedboth-force`, run mb-r2: off, one half stays unmade (`would` 1, `Left half closed: would be
  made from the Right side`); on, both halves made (`made from the Right side (second half)`), errors 0.
- By hand, a double bed with one side at a wall (session bu-m1): off, the Right half stays unmade with two
  `routefail: ... BedMakeBed ... FailedBecauseNoDestinationSpecified` lines; on, both halves made.
- Not confirmed: whether the far half's look visibly changes to made (13.3); the counters and the bed's flags say made.

