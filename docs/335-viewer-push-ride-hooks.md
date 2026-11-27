# 335. Viewer push without Actors walks: `viewerridehooks` (docs/333 LC-4, 2026-11-23)

Package LC-4 of docs/333 (§0 order, handoff `docs/data/loop-cost-scaling-handoff.md` "LC-4"). Built, selftests, queues written,
NOT RUN. Branch `feature/lc4-viewer-push-1123` (off `feature/ts3fixes-loop-cost-1122`, which has `viewerpushdelta`). Ledger §589.

Tags: **[verified]** read in our source or the decompiled scripts; **[inferred]** arithmetic or reasoning, to confirm (docs/51 §589).

## 1. What changes

`UnseenViewers.cs`, sub-switch **`viewerridehooks`, default off**, needs `viewerpushdelta` (ON). Diagnostic
**`viewerridehookshadow`, default off**. Only the held table's ride part changes; the viewer table (codes 1-5) is untouched.

Today (`viewerpushdelta`) the held table (codes 12-15) is the household plus, from a `LotManager.Actors` walk, every ridden
horse with its rider and both sides of a running `Mount` / `Dismount` / `BeingDismounted`. The walk runs every wake while a
ride is in progress, else every 4th wake: ~0.74 us x Actors per wake, ~2.9 us x Actors with rides in progress (docs/333 §2).

With `viewerridehooks` on:
- **Ride set from hooks.** Hooks note Sims into a set (`sRides`):
  - `RidingPosture.StartRiding` (rider and horse, after EA's body returns true);
  - the entry of `Sim.Mount.Run`, `Sim.Dismount.Run` and `Sim.BeingDismounted.Run` (actor and target).
  
  Each wake tests the set with the walk's own test (`RideCandidate`, then `HasBeenDestroyed`). It drops Sims that fail and
  holds the rest (`HoldRide`, as the walk does).
- **Household rides are read every wake** from the household list, as before, and so is the horse under a household rider
  (`RidingPosture.Mount`, held as the walk holds a ridden horse). Neither depends on the hooks.
- **Full walk kept:**
  - for the seed: the first wake of a load, the first wake after the switch turns on, after a hook error or a set over 256;
  - every 10 s as a floor, for mods that start rides past the hooks.

  A floor walk that finds a ride missing from the set counts it (`[1666]`) and switches to a walk on every wake for the rest
  of the load. That is the same as `viewerpushdelta` with a ride in progress.
- **Hooks refused** (another mod compiled or swapped one first): `sRideHooksIn` is false and the old walk stays.
- **Shadow** (`viewerridehookshadow`): every wake also builds the walk-every-wake held table and compares the two sets
  (`[1668]` must stay 0). Below the 32-entry cap it compares sets; at the cap it compares order too, since order picks who is
  cut. The hook table is the one sent.

Not built from the handoff: **native reads of household positions by id**. It would change the viewer table's content: fresher
positions, not the ones the managed side sent. It is also a native change, and the cost it removes is the per-wake intercept
(~21 us, household <= 32), which does not grow with the world. Not needed for the scaling case below.

## 2. Why the set is exact [verified decompiled scripts, except where marked]

A Sim passes `RideCandidate` only if one of these holds:
- **(a) `Posture is BeingRiddenPosture`.** The only `new BeingRiddenPosture` in the scripts is in `RidingPosture.StartRiding`.
  `PreviousPosture` restores (`PoolLounger`, `HotairBalloon`) do not touch riding.
- **(b) `CurrentInteraction is Mount / Dismount / BeingDismounted`.** `CurrentInteraction` is the queue head while
  `mIsHeadInteractionActive` is set. `InteractionQueue` sets that flag right before `RunInteractionWithoutCleanup`. That method
  reaches `Run()` without a yield: the inventory posture pops need the target in the actor's inventory, which a horse never is.
  So our loop (a separate task) can never see (b) before `Run` is entered.

A set member is dropped only on a wake where both (a) and (b) are false. It can come back only through a new `StartRiding` or a
new `Run`, which the hooks see.

Exceptions:
- **Saved rides:** posture objects restored by the load skip `StartRiding`. The seed walk covers them.
- **Mod subclasses** that override `Run`: `is Sim.Mount` still matches them, but our hook does not run. EA has no subclass
  (`: Sim.Mount` etc. not found in the scripts). The floor walk covers this case.
- **Other posture writers** in mods [inferred, I-589-1]: also covered by the floor walk.

Shadow and floor counters measure all three.

**Known gap (open, for Rodney):** an NPC ride that starts past the hooks is held only from the next floor walk, up to 10 s late.
Today's `viewerpushdelta` is at most 3 wakes late. After the first such miss the load walks every wake, so the gap occurs once
per load. Household Sims and horses under household riders are not affected (read every wake). A 2 s floor would close most
of the gap for ~0.5 ms/s at 4x and ~1.3 ms/s at 10x. Decide after the correctness queue's `floor_miss` count.

**Order:** the set is held in note order, not Actors order. The held table is a set to ts3native (`UtHeldObj` matches ids one by one in `unseen.cpp`
[verified our source]). Order
matters only when household plus riders pass the 32-entry table, where the walk's Actors order picks who is cut. That is the
same edge as today, with a different tie order [inferred, I-589-2].

## 3. Household and safety

- **Household:** held first on every wake (unchanged code). Their rides come from the household list every wake, plus the
  hooks. Unknown scope does not arise: the held table only adds Sims, so a Sim missing from it gets the non-held side.
  Exactness against the walk is the gate (shadow diff 0). The native 2 s table age is unchanged: the held table is still
  re-sent at least every 500 ms (`kViewerBeatMs`), and ts3native drops older tables.
- **Hooks:** four method swaps, always installed (as other ts3fixes hooks). With the switch off each hook is one switch read,
  then EA's body.
  - `Mount` / `Dismount` / `BeingDismounted.Run` yield, so a saved task frame can sit in the hook body. Precedent: the
    `fireworkswatchlate` `Watch.Run` hook, handled by the stub / swap-delegate records (docs/242, docs/576 §1).
  - Hook bodies catch their own errors and never block EA's body.
- **References:** the set keeps Sims only until the next wake test, capped at 256. It is cleared at load.

## 4. Cost and the 4x / 10x case (ON rule 2026-11-23)

Inputs (docs/333 §2) [measured]:
- 41-52 wakes/s; Actors 89 / 67 / 81 (BP / IP / SV);
- 0.74 us per actor per wake at 1 walk in 4, 2.9 us per actor walked;
- viewer push 3.64 / 3.38 / 4.15 ms/s.

With the hooks, the walk runs once per 10 s (~1 wake in 450). The set test is ~1 us per ride Sim per wake [inferred].

Results, ms/s, wakes taken as 45/s [inferred, I-589-3]:

| World content | Walk part today (1 walk in 4) | With rides in progress (walk every wake) | `viewerridehooks` walk + set | Saving |
|---|---:|---:|---:|---:|
| 1x (BP / IP / SV) | 3.0 / 2.2 / 2.7 | 11.6 / 8.7 / 10.6 | ~0.03 + set | **~2-3** (~9-12 with rides) |
| 4x | 12 / 9 / 11 | 46 / 35 / 42 | ~0.1 | **~9-12** (~35-46 with rides) |
| 10x | 30 / 22 / 27 | 116 / 87 / 106 | ~0.3 | **~22-30** (~87-116 with rides) |

- The per-wake intercept (~1 ms/s, household positions and sends) stays at every size.
- The floor walk is one atomic wake of 2.9 us x Actors: ~0.26 ms today, ~2.6 ms at 10x, once per 10 s.
- **ON case:** the saving is linear in Actors, so it holds at 4x and 10x whenever correctness passes. The timing ABBA must show
  no regression today (expected: a small gain, ~2-3 ms/s).

## 5. Queues (written, NOT RUN; dry-run OK)

`tools/harness/queues/lc4-viewer-push-correct-1123.json` (~80 min, Bin state apex). Every job turns on
`viewerridehooks` + `viewerridehookshadow` and requires:
- `viewerridehooks_shadow_diff<=0`, `viewerridehooks_floor_miss<=0`;
- `utViewerBad<=0`, `scriptErrorsMax: 0`;
- the save guard.

Jobs:
- smoke: BP 3 min, stops the queue on fail;
- BP / IP / SV: 10 min natural play;
- `unseen-horses`: NPC rides, needs Pets, never run before;
- `rare-horse-ride`: household ride, never run before.

  A hollow repro is N/A, not PASS.

`tools/harness/queues/lc4-viewer-push-time-1123.json`: TIMING, **needs quiet machine**.
- BP / IP / SV, 25 min each in one load, ABBA, `--ab viewerridehooks`, shadow off; queue.ts runs the smoke first.
- Read per arm with `node tools/harness/loopcost-report.ts <tag>-ab-fixes.jsonl --arm viewerridehooks` (the `--arm` option is
  new on this branch), and wait-ab / profile-compare for the sim thread.

## 6. Counters (slots 1662-1668, agent names `viewerridehooks_*`)

| Slot | Name | Meaning |
|---|---|---|
| 1662 | `noted` | Sims noted by the hooks |
| 1663 | `set_wakes` | wakes served from the set |
| 1664 | `set_tested` | set Sims tested |
| 1665 | `walks` | seed / floor / fallback walks (also in `viewerpush_actor_walks`) |
| 1666 | `floor_miss` | floor walks that found a ride the set lacked (walk every wake from then) |
| 1667 | `shadow_wakes` | shadow compares |
| 1668 | `shadow_diff` | shadow compares whose sets differ (must stay 0) |

Selftests: `tools/selftest/loopcost` (walk cadence, set compare, and a 100-round model: hooked-only starts give equal sets on
every wake; an unhooked start differs only until the next floor walk). `loopcost-report.ts --self-check` covers `--arm`.
