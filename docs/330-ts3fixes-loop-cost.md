# 330. Our own loop cost: deadrefsweep and the viewer push (2026-11-22)

Rodney: "We fix our own mod issues first." The provisional non-Sim split (docs/329 §3, branch `feature/nonsim-split-1122`, from
managed-sample shares of the m1122 sim-budget run) put our ts3fixes `LoopTask` loops at **27 / 38 / 69 ms/s (BP / IP / SV)**,
led by `DeadRefSweepRun` (14 / 24 / 29) and the viewer push (7 / 7 / 15) [inferred: samples x scheduler stage time]. This doc
makes both cheaper without changing what they compute, behind two sub-switches that stay ON (fixes to default-ON code) and turn
the old cadence back on for A/B.

Branch `feature/ts3fixes-loop-cost-1122` (off master e555c9a9c = m1122). ts3fixes build `ts3fixes.package.ts3fixes-loop-cost-1122`
(`game-copy/ts3fixes.package.loopcost1`), agent `ts3agent.package.ts3fixes-loop-cost-1122`; asi / ini unchanged (m1122).
Counter slots 1543-1584 (`fixes-1543`), ledger §583. Tags: [verified] = our source or decompiled code; [measured] = a run;
[inferred] = to confirm (docs/51 §583).

**Status: correctness run 2026-11-22/23 (§8): 13 of 14 rows PASS, 1 FAIL in `conversation-gone-check` (a repro that never passed on any build); timing NOT RUN (gated on correctness).**

## 1. Where the time goes (m1122 managed samples, minutes 1-4, 5 Hz) [measured samples, inferred split]

One sample ~1.1 ms/s; the counts are small, so the split is rough. Samples whose stack holds the loop:

| Loop / part | BP | IP | SV |
|---|---:|---:|---:|
| `DeadRefSweepRun` (all) | 12 | 21 | 24 |
| - `Objects<T>` = `Queries.GetObjects<T>` (one full world walk per typed query; 8 per pass) | 2 | 7 | 4 |
| - `StopWatch.GetElapsedTime` per item (`SlicedPass.RunSlice` reads the clock before every item) | 2 | 1 | 6 |
| - object phases 10-12+14 (`UseListHolders` list copies, `OwnerHolders`, `HandlerHolders`, `CookingHandlers`; 4 `HasBeenDestroyed` per object) | 2 | 8 | 10 |
| - per-item dispatch, description phases, rest | 6 | 5 | 4 |
| viewer push `ViewerPushLoop` (all) | 6 | 6 | 13 |
| - `PushHeld` walking `LotManager.Actors` (`HasBeenDestroyed` -> `Simulator.GetProxy`, `CurrentInteraction`, `Posture`) | 5 | 5 | 11 |
| - `Household.AllActors` (a new list per call, twice per push) | 1 | 1 | 2 |
| - `ViewerSend` (`Route.GetOption2` icalls) | 0 | 0 | 0 |

The interop sends are not the cost; the walks are. Per-loop timers were missing for the viewer push and per phase for the sweep,
so this build adds them in both modes (§5): calls, ms, items per phase and per wake.

## 2. deadrefsweep: `deadrefcadence` (ts3fixes, default ON; off = the pass as before)

`DeadRefs.cs`. The pass still starts from the same hourly alarm (and the agent's `sweep`), runs in ~2 ms slices in one script
task, re-tests each item at its turn, stands down on every unsafe state, and keeps the pre-save pass unchanged. With the switch on:

1. **One world snapshot per pass.** `GetObjects<GameObject>()` once; phases 5-9, 13 and 15 take their items from it by type
   (`Sim`, `Fish`, `CarOwnable` / `BoatOwnable`, `HorseshoeCourt`, `Dartboard`); phases 10-12+14 use it as before. Old: eight typed
   queries per pass (`Sim` three times), each a native walk of every world object. Same items: the typed query has isinst
   semantics (an abstract `CarOwnable` query returns its concrete subclasses) and every queried class derives from `GameObject`
   [verified decompiled: `Sim`, `Dartboard`, `HorseshoeCourt`, `Vehicle` -> `Car` / `Boat` -> `CarOwnable` / `BoatOwnable`; `Fish` via
   `Ingredient`]. Difference: an object created after the snapshot (taken at the first object phase) waits for the next pass
   instead of being in a later phase's query of this pass; a destroyed one is skipped at its turn as before.
2. **Phases 10-12 and 14 as one step per object** (`DrObjectStep`): one `HasBeenDestroyed` test, then use lists, owner component,
   ObjectDisposed handlers, cooking handlers in that order, each in its own try (an exception in one counts [410] and the next
   still runs, as when they were separate phases). The four holders act on this object only and none destroys it [inferred,
   I-583-1], so the per-object result equals running them as four walks. A use list is copied only when it lists a gone Sim
   (`UseListGone` scan first; the old code copied all three lists of every object).
3. **Slice clock every 8 items** (`SlicedPass.RunSliceEvery`): the same items in the same order; a slice runs at most 7 items past
   its 2 ms budget (object items are a few us; the worst is a phase-1 description with a large relationship dictionary).
4. **Event-gated phase groups.** Description group = phases 1-3 (relationship keys, aging states, alarm owners) and 6 (fish
   fisher): they act only on descriptions that are invalid. Sim group = phases 4, 5, 7-9, 13, 15 (they test gone Sims and gone
   descriptions). A group runs on a pass when (`LoopCostCore.GroupDue`):
   - an event that can create its dead refs fired since the group last started: `kSimDescriptionDisposed` for both groups,
     `kSimDestroyed` for the Sim group (the existing listeners; the object walk keeps its own gate, unchanged);
   - its last run removed something (a trickle keeps it hourly); or
   - `kDeadRefIdleHours` = 4 sim hours passed since its last finished run.
   Every group runs on the first pass after a load (descriptions a load left invalid send no event) and after the agent's
   `sweep`. A pass stopped inside a group marks it due again.

**Why the description group can wait for the event [verified decompiled]:** `SimDescription.Dispose` is the only place a live
description's `mIsValidDescription` goes false (the other assignments set it true: constructors and `Fixup`), and the same
method sends `kSimDescriptionDisposed` a few statements later with no return in between. A description that a load leaves
invalid (no `Fixup`) is covered by the first pass after the load.

**Staleness bounds (exact results, bounded delay):**

| Dead ref | Old | New |
|---|---|---|
| made by a destroyed Sim or a disposed description (the events) | next hourly pass | next hourly pass (unchanged) |
| any other (a stale pointer copied into a holder later, a Dispose that threw before its event, a destroyed vehicle in `Sim.ReservedVehicle`) | next hourly pass | within 4 sim hours (+1 h alarm grain), hourly again while finds continue |
| relationship keys, aging states, alarm owners at save time | pre-save pass | pre-save pass (unchanged): the save holds the same entries |
| an object created during a pass | that pass, if its phase's query ran after it | the next pass |

Nothing the sweep guards is read in a way the delay can break: the sweep exists so the dead object graphs do not go into saves
(issue #91); the readers of these fields already meet dead entries for up to an hour today, the gone objects stay valid managed
objects (`IsValidDescription` false, `HasBeenDestroyed` true), and the crash-relevant cases stay with `deadrefguards`, which runs
inline in `Relationship.Get` / `IsAgingStateValid` and is unchanged. Every entry the old pass removed is still removed, through the
same EA calls.

**Household:** unchanged scope. The sweep removes the same entries (household descriptions included, as EA bug fixes since
2026-11-18); only when changes. Phase 15 keeps its household audit (`HhAuditCore.DeadHolderDrop`).

**4x / streamed worlds:** one object walk per pass instead of eight; the description phases run per disposal event (work follows
change, not the description count) plus a 4-hour floor; nothing sized by the world. Still proportional to the world's object count
on each pass that walks (the object group and the Sim group, hourly while Sims are destroyed); a per-lot dirty set for the object
walk (the destroyed Sim's last lot) would make it proportional to change; not built (it needs the use-list and owner holders on
other lots to be ruled out first).

## 3. Viewer push: `viewerpushdelta` (ts3fixes, default ON; off = both tables every wake)

`UnseenViewers.cs`. The loop still wakes every 5 ticks and reads the active household's positions. With the switch on:

- **Viewer table** (codes 1-5): sent when the viewer count changed, any viewer moved more than 0.5 m from its last sent position,
  or 500 ms passed since the last send (`LoopCostCore.ViewersDue`). One `AllActors` list per wake (was two).
- **Held table** (codes 12-15): the household first (from that list) on every wake; rides from `LotManager.Actors` on every wake
  while the last walk or the household had a ride candidate (`BeingRiddenPosture`, or a running `Mount` / `Dismount` /
  `BeingDismounted`), else every 4th wake (`LoopCostCore.HeldWalkDue`); in a walk the ride test comes before `HasBeenDestroyed`
  (a field and a list read before the native proxy lookup; same Sims held). Sent when the ids changed or 500 ms passed.
- A refused or part-sent table is sent again on the next wake (old: the next wake anyway).

**Staleness bounds:**
- Viewer positions in ts3native: within 0.5 m of the household's positions at the last wake (old: equal), so with the movement
  between wakes (v x 5 ticks, both modes) the error is at most 0.5 m more than today. Every consumer compares distances against
  edges (`[UnseenThrottle]` 80 m, `[AutShadow] TierMs`, `[UnseenAnim]`, `[UnseenTable]`, `[UnseenProbe] Bands`): a decision can
  differ only for an object whose distance to the nearest household Sim is within 0.5 m of an edge; beyond that band every
  decision is the old one. A household member added or removed is sent at once (count change).
- Table age: at most 500 ms plus one wake (old: one wake); ts3native drops a table older than 2000 ms, so a sim-thread stall
  over ~1.4 s (old ~1.9 s) makes it stale, where every consumer falls back to its safe side (`NeedViewers=1`: nothing skipped;
  held table stale = everyone household in `[UnseenAnim]`, bands and TaskSeam).
- Held ids: exact on every walking wake. A ride that starts while none is in progress anywhere is held at most 3 wakes (15
  ticks) late. Household rides are read every wake and walk at once; a horse ridden by a household Sim is within 0 m of a viewer
  (its rider), so it is never unseen either way. The late window applies only to NPC rides started more than 80 m from every
  household Sim, at the start of the rider's `Mount` (still routing to the horse) [inferred, I-583-3].

**Household:** exact. Both tables hold every household actor on every wake that sends; the household's own rides are tested
every wake. **4x / streamed worlds:** per wake, work is the household (<= 32) plus 1 in 4 walks of the Sims in the world while
no ride is in progress; nothing tied to world size.

## 4. Other ts3fixes loops in the split, and the expected saving

| Loop (samples BP / IP / SV) | What it is | Verdict |
|---|---|---|
| `PetTask` (0 / 3 / 9) | EA's `PetPoolManager` alarm work (`CreatePet`), spread by `easpreadpets` | EA's work, already spread; not ours to cut |
| `OdSweep` (1 / 2 / 5) | `unseenoutfitdefer` apply loop | runs only while outfits wait, work limited per `kOdTickMs`; the per-tick wake is one `TickCount` read; no cheap exact win |
| `LotCleanupTask` (0 / 0 / 6) | EA's daily venue cleanup, spread (`easpreadlots`) | EA's work, daily; no change |
| `VlHourly` (1 / 1 / 0), `DecayTask` (1 / 0 / 1), `OwChainTheirs` (2 / 0 / 0) | hourly park pass, relationship decay spread, NRaas Overwatch chain | small; no change |

**Expected saving [inferred, I-583-2; the timing queue measures it]:** deadrefsweep ~45-60% of 14 / 24 / 29 (snapshot ~7/8 of
the query share, clock ~7/8 of its share, object step roughly half, description phases on events) and the viewer push ~50-65%
of 7 / 7 / 15 (3 of 4 `Actors` walks skipped while no ride runs; fewer sends): **~8-15 BP, ~12-22 IP, ~18-32 SV ms/s**. A world
with NPC rides in progress keeps walking every wake (no push saving there).

## 5. Counters (slots 1543-1584, agent names)

| Slots | Name | Meaning |
|---|---|---|
| 1543-1557 | `deadrefsweep_p1_ms` .. `p15_ms` | ms per phase, numbered as in the `DeadRefs.cs` header (both modes; the merged object step counts as phase 10) |
| 1558-1572 | `deadrefsweep_p1_items` .. `p15_items` | items stepped per phase |
| 1573 / 1574 | `deadrefcadence_desc_skipped` / `_sim_skipped` | passes that skipped the description / Sim group |
| 1575 / 1576 | `deadrefcadence_snapshots` / `_snapshot_objects` | world snapshots / objects in the last one (cadence only) |
| 1577 / 1578 / 1579 | `viewerpush_wakes` / `_ms` / `_max_wake_us` | wakes, ms in wakes, longest wake (both modes) |
| 1580 / 1582 | `viewerpush_table_sends` / `_held_sends` | viewer / held tables sent |
| 1581 / 1583 / 1584 | `viewerpush_actor_walks` / `_actors_walked` / `_walks_skipped` | `LotManager.Actors` walks, Sims walked, walks skipped (delta only) |

Existing: `deadrefsweep_passes` [405], `_slices` [406], `_max_slice_us` [407], `_total_ms` [408], `_objwalks` [2055] (now counted at
the object group's start in both modes), `_objms` [2056]. Self-checks: `tools/selftest/loopcost` (group cadence and its delay
bound by simulation, viewer threshold and table age by simulation, held-walk delay, `RunSliceEvery` order / overrun / clock reads)
and `node tools/harness/loopcost-report.ts --self-check`.

## 6. Queues (written, dry-run OK, NOT RUN)

Bin state `apex` (Apex latest + RTX Remix bridge, turbo-compat snapshot -> state -> restore). asi / ini `game-copy/*.m1122`,
ts3fixes `game-copy/ts3fixes.package.loopcost1`, agent `game-copy/ts3agent.package.loopcost1` (copied into the worktree `build/`).
Run from the lane worktree.

**Correctness** `tools/harness/queues/ts3fixes-loop-cost-correct-1122.json` (~150 min, `--no-smoke`): `bp-loopcost-smoke` (BP 3 min,
stops the queue on fail), `bp` / `ip` / `sv-loopcost-correct` (10 min natural, seed 12345: a full pass, snapshot taken, tables sent,
walks skipped, `utViewerBad` 0 = every table well-formed, 0 script errors), then the eight deadrefsweep repros under the new
cadence (`bp-deadrefs-check`, dart, use list, owner, handler, cooking, celebrity, conversation: each still cleans its seeded dead
refs; `deadrefcadence_snapshots` proves the new pass ran). Switch read-back: `deadrefcadence_snapshots` and
`viewerpush_walks_skipped` move only with the switches on.

**Timing** `tools/harness/queues/ts3fixes-loop-cost-time-1122.json` (**needs quiet machine**, ~100 min, after correctness): queue
smoke `bp-loopcost-ab-smoke` (BP 5 min, 1-min slices), then `bp` / `ip` / `sv-loopcost-ab`: 25 min in one load, `--ab
deadrefcadence,viewerpushdelta` (both flipped together, read back every slice), 5-min slices, warm-up then off,on,on,off. Read:
`node tools/harness/loopcost-report.ts probe-out/profile/<tag>-ab-fixes.jsonl` (our ms/s per arm, per phase, sends and walks per
s), `node tools/harness/wait-ab.ts <tag>` and `node tools/profile-compare.mjs <tag>:on <tag>:off` (sim thread, hitch p99 / p99.9 /
max). Pass: ours on < off on every world, hitches not worse beyond noise.

**Chain:** `probe-out/loopcost-wait-1122.sh` (no `chain-` prefix, outside `probe-out/queue`) starts
`probe-out/queue/chain-ts3fixes-loop-cost-1122.sh` once no chain is listed and `chain-compat-rules-proof-1122.log` has CHAIN END or
CHAIN STOP (order: inst-probe -> caps -> overflow-fixes -> nonsim-split -> sp-instantiate-guard -> compat-rules-proof -> this).
The chain runs the correctness queue only. **Timing (Rodney 2026-11-22: queued):** `probe-out/loopcost-time-wait-1122.sh` starts
`probe-out/queue/chain-ts3fixes-loop-cost-time-1122.sh` last, once `chain-watergrid-next-1122.log` and the correctness chain log
show CHAIN END or CHAIN STOP and no chain is listed. It runs only if correctness ended with all 12 jobs PASS (else it logs why),
then a quiet-machine pre-check (`machine-load.ts` verdict: other CPU >= 10% or a clock drop = noisy; retried every 10 min,
6 tries, then CHAIN STOP to reschedule), then the timing queue (smoke first, queue.ts's in-job noise checks and noisy reruns), then
`loopcost-report.ts` per world into `probe-out/queue/chain-ts3fixes-loop-cost-time-1122-report.txt`; results go to §8.

## 7. Next

After correctness: timing on a quiet machine. If ours on < off: keep both ON (they are already ON). Then the object-walk per-lot
dirty set (§2, 4x worlds) and the per-phase ms table from §5 decide whether phase 1 (relationship entries) needs its own cursor.

## 8. Results

### 8.1 Correctness (chain-ts3fixes-loop-cost-1122, 2026-11-22 18:05 - 2026-11-23 02:39 PHT, Bin state apex, m1122 asi + loopcost1)

Result file `docs/data/results/2026-11-22-queue-ts3fixes-loop-cost-correct-1122.md`. Queue exit 1.

| Job | Result |
|---|---|
| bp-loopcost-smoke | PASS 5.2 min |
| bp-loopcost-correct | VOID (known Apex 4.0.x present-lock ABBA, `apex-401-present-lock-cycle`), rerun `-kc` PASS 12.1 min |
| ip-loopcost-correct / sv-loopcost-correct | PASS 12.1 / 12.2 min |
| bp-deadrefs-check / dart / use-list / object-owner / disposed-handler / celebrity-waiting (cadence) | PASS (10.2-14.1 min) |
| bp-cooking-handler-gone-check-cadence | VOID (same Apex ABBA), rerun `-kc` PASS 12 min |
| bp-conversation-gone-check-cadence | **FAIL** 2.7 min: `expect cleaned: wrong outcome; gone 0, members 0`, plus 1 new script error |

Every switch-scoped check passed in the 10-min worlds (snapshots taken, groups skipped, viewer tables sent, `utViewerBad` 0, 0 script
errors) and every merged object step repro cleaned its seeded refs (I-583-1 holds for use list, owner, handler, cooking).

**The one FAIL [inferred, not caused by the cadence]:** the on arm's poll saw the gone member removed (`gone 0`), but the member list was
empty instead of holding the live host. After phase 15 takes the gone Sim out, the host's own leave is EA's normal
empty-conversation exit (§ phase 15 comment in `DeadRefs.cs`), which can run inside the 20 s poll window; the check then wants
`members 1`. This repro has never passed on any build: 2026-11-15 FAIL (members 0 on the seed poll) and CRASH (lane-l3
deadrefs results). The script error at 18:37:20 UTC came in the *off* arm (switches off, before the on arm), the window where
the bug the phase fixes (#166) is live. Its XML was not kept, so its stack is unread.

### 8.2 Timing (chain-ts3fixes-loop-cost-time-1122, 2026-11-23 08:46-10:24 PHT, ts3fixes `-1122d`, Bin state apex) [measured]

Quiet pre-check passed (other CPU 3.9%, clock 178% -> min 174%). In-load ABBA, 25 min per world, 5-min slices, warm-up then
off,on,on,off (~10 min per arm), `--ab deadrefcadence,viewerpushdelta`. Queue exit 0: smoke PASS, BP PASS, IP first attempt N/A
(EA package-lock deadlock, docs/539 §13.8, auto-retried) and `ip-loopcost-ab-retry` PASS, SV PASS; noise low on all three
timing rows. Our loops from the ts3fixes counters (`loopcost-report.ts`, report file
`probe-out/queue/chain-ts3fixes-loop-cost-time-1122-report.txt`):

| World | ours ms/s off | on | saved | deadrefsweep off / on | viewer push off / on | table sends/s off / on | Actors walks/s off / on |
|---|---:|---:|---:|---|---|---|---|
| BP | 26.96 | 11.87 | **15.09** | 17.70 / 8.23 | 9.26 / 3.64 | 39.9 / 17.7 | 39.9 / 10.2 |
| IP | 29.88 | 14.41 | **15.46** | 22.48 / 11.04 | 7.39 / 3.38 | 49.0 / 11.7 | 49.1 / 12.2 |
| SV | 37.06 | 15.56 | **21.50** | 27.53 / 11.41 | 9.53 / 4.15 | 51.4 / 14.5 | 51.4 / 12.9 |
| BP smoke (1-min slices) | 27.21 | 13.52 | 13.69 | 18.49 / 10.06 | 8.73 / 3.47 | 42.3 / 20.7 | 42.3 / 10.4 |

Our loop cost falls by 52-58% on every world, inside the expected range (§4: 8-15 / 12-22 / 18-32). Passes per minute equal
in both arms (10.5 / 13.3 / 13.8), so the sweep does the same number of passes for less. Per phase (on): phases 11, 12, 14
fall to 0 (merged into phase 10 as one step per object), phase 10 itself stays ~5.5-7.9 ms/s (the object walk is now the
whole sweep cost), description phases 1-3 / 6 drop ~3x (event gate), the Sim phases 5, 7-9 cost about the same (5 rises
0.6-0.7 -> 0.9-1.2: the snapshot is taken there). Next cut, if wanted: the object walk (phase 10), e.g. the per-lot dirty set
of §2.

Sim thread and hitches per arm (`hitch-ab.ts`, iterations / it/s / p99 / p99.9 / max ms, >= 100 / >= 300 ms counts):

| World | off | on |
|---|---|---|
| BP | 22147, 52.1 it/s, 70 / 133 / 283, 81 / 0 | 23031, 54.8 it/s, 71 / 133 / 243, 82 / 0 |
| IP | 31153, 69.5 it/s, 73 / 215 / 565, 210 / 7 | 30207, 67.3 it/s, 99 / 222 / 628, 300 / 14 |
| SV | 32261, 77.9 it/s, <50 / 108 / 353, 37 / 1 | 33330, 80.9 it/s, <50 / 93 / 340, 28 / 2 |

BP and SV: more iterations per second with the switches on (+5%, +4%) and hitches equal or lower. IP: on is worse in this run
(p99 99 vs 73 ms, 300 vs 210 iterations >= 100 ms, 14 vs 7 >= 300 ms, -3% it/s) [measured, cause not known]. Our loop cost
in the same IP arms fell 15.5 ms/s, and neither change adds work on the hitch path, so the gap is most likely IP's natural
hitch spread across ABBA slices (IP has the most natural hitches of the three; one 25-min load) [inferred, I-583-5]. Static
read in §8.4: not our code, route demand; a reverse-order IP run (BAAB) is queued before shipping; until then the switches stay ON (they are
fixes to default-ON code; off = old cadence).

### 8.4 IP hitch excess: static read of the 11-23 IP run (Rodney 2026-11-23: logs and code first)

Source: tag `ts3fixes-loop-cost-time-1122-2026-11-23-ip-loopcost-ab-retry` (hitches.jsonl task attribution, per-second
seconds.csv, ab-native.jsonl counter deltas per slice, `hitch-ab.ts --causes`). Slices: 1 off, 2 on, 3 on, 4 off.

- **Not our code [verified].** Our tasks inside the >= 300 ms iterations: DeadRefSweepRun 2-3.4 ms, in 3 off and 3 on
  iterations; ViewerPush never. Over all >= 100 ms iterations our tasks are small and similar per arm (DeadRefSweepRun 245 ms
  total off vs 165 on, VlHourly 97 vs 105). The phase-10 merge (DrObjectStep, clock every 8 items) shows no slice over 8 ms.
- **What the hitches are [verified].** 14 on-arm >= 300 ms iterations: all `ScriptProxy>Sims3.Gameplay.Actors.Sim`, mostly sync
  route plans (Route_Replan, IC Plan NoStart) and locomotion; no GC mark, no lot add, no load lock. The top task of >= 100 ms
  iterations is the same mix in both arms (RouteBackoffHook.AsyncWait, OnAboutToStartFollowingRoute, PerformRoute).
- **Where [verified].** 6 of the 14 are one burst at 786-791 s (slice 2, route 346-470 ms each). Route demand was higher in both
  on slices: routes/s 4.45 / 4.17 on vs 3.65 / 3.95 off, route ms/s 57 / 50 vs 35 / 36; ufSync +16%, ufMarks +9%.
- **viewerpushdelta did not change who counts as seen [verified].** Viewer pushes fell 30011 -> 7006 and held pushes
  30012 -> 1157 as designed, but the unseen-table builds and hits are equal (utbBuilds -3%, utbHits -1%, kdCutUnseen 63 vs 67);
  the 500 ms beat keeps the tables inside ts3native's 2000 ms age, and a 0.5 m position lag cannot move many Sims across a band.
- **Verdict: not pinned [inferred].** The excess is route demand (more routes and one replan burst in the on slices), not our
  loops; nothing in the diff makes Sims route more, but a world-time effect versus the switches cannot be told apart from one
  ABBA. Hence the reverse-order run below.

Queued (TIMING, needs quiet machine, RUN after unbounded-notests): `tools/harness/queues/ts3fixes-loop-cost-time-ip-baab-1123.json`
= BP smoke job (4 min, 1-min slices) then IP 25 min BAAB (`abOnFirst`, counted on,off,off,on), same build (loopcost4), seed and
settings. Chain chain-ts3fixes-loop-cost-time-ip-baab-1123 (quiet check, Bin state apex, report with `hitch-ab.ts --causes`),
started by probe-out/loopcost-baab-wait-1123.sh. Read: on worse again in BAAB = the switches (then per-arm route counts by
Sim); on equal or better = the 11-23 gap was slice placement.

BAAB run 2026-11-23 12:24-12:58 PHT: smoke job PASS (8.2 min); the IP job was stopped from outside about 20 min in (chain
and queue gone at 12:58, no queue verdict, no Bin restore by the chain). Cancelled job = no data; I-583-5 stays open. The
chain's quiet check also let a NOISY machine through (other CPU 15.5%): `$(... | tail -n 1)` returned tail's exit code.
Fixed in both loop-cost timing chains (the check's own exit code now decides); the 11-23 ABBA was not affected (queue.ts
in-job noise check: low). Rerun: `bash probe-out/loopcost-baab-wait-1123.sh` once the machine is quiet and nothing is listed.

### 8.3 Option (a): verdict fix and rerun (Rodney 2026-11-23)

- **Fix** (`DeadRefs.cs`, `DeadRefsCore.ConvCheckOk`, selftest `tools/selftest/deadrefs`): `expect cleaned` passes when the
  switches are on, no gone member is listed, and either the live host is still listed (members 1) or the list is empty and
  phase 15 removed a member from *the seeded conversation* (`sCvRemovedHere`, counted inside `ConversationHolders` only for
  that conversation, not the global [995], so another conversation's clean cannot pass it). A gone member never removed, an
  empty list without the phase acting, or switches off still fail (10 selftest cases). The off arm is unchanged.
- **Script-error XML:** the 2026-11-22 file was not kept (the harness restores the user folder), so its stack is unread.
  `queue.ts` on this branch (harness change) now copies every new `ScriptError_*.xml` of a job into `probe-out/queue` as
  `<queue>-<date>-<job>-ScriptError_*.xml` before the chain's restore; the rerun keeps it.
- **Build** `ts3fixes.package.ts3fixes-loop-cost-1122b` (`game-copy/ts3fixes.package.loopcost2`, sha ec84dccffdcc, tuning
  1467b8d4c707 = loopcost1; loop code unchanged). Timing queue repinned to it.
- **Queue** `ts3fixes-loop-cost-conv-rerun-1123` (the one job, ~15 min), chain `chain-ts3fixes-loop-cost-conv-rerun-1123.sh`,
  started by `probe-out/loopcost-conv-wait-1123.sh` after `wait-dalexlol-637-644-1122`; the same waiter then runs the re-armed
  `chain-ts3fixes-loop-cost-time-1122.sh`, whose gate now needs every correctness row PASS on its last attempt (VOID + passing
  `-kc` = PASS) and the rerun PASS, else it logs why and stops.

**Rerun result (2026-11-23 06:11-06:27 PHT, `chain-ts3fixes-loop-cost-conv-rerun-1123`): FAIL on the script-error rule only.**
Every repro step passed: off arm `gone 1, members 1, removed by phase 15 0`; on arm `gone 0, members 1, removed by phase 15 1`
(this time the host stayed). The FAIL is one new script error, kept as
`ts3fixes-loop-cost-conv-rerun-1123-2026-11-22-bp-conversation-gone-check-cadence-ScriptError_..._06-13-33_...xml` [verified, read]:

- `NullReferenceException` in EA `Conversation.RemoveSim` <- EA `SocialComponent.LeaveConversation` (called through our
  pass-through hook `B12LvHook.LeaveHook` -> `LeaveOrig`, `leaveconvnull`: it only acts when the conversation is null, here it
  is not) <- `InteractionDefinitionUtilities.StartInteraction` <- the interaction queue of **Diana Jones**, the host of the
  **off-arm** seed, at 06:13:32 PHT: 6 s after the off-arm seed, 17 s before the off-arm check, with deadrefsweep and
  deadrefconvo **off**.
- That is the #166 bug the phase fixes (EA's RemoveSim takes the destroyed partner and throws), produced by the repro's own
  off-arm hard state. Our code is in the stack only as the pass-through hook; the cause is the repro (a hard state we build) with
  the fix switched off, not deadrefcadence or viewerpushdelta. The 2026-11-22 error came at the same point (off arm, 22 s
  after the seed). The second file of the run (06:14:23) is ErrorTrap's correction log (known, ignored).
- So the repro cannot pass `scriptErrorsMax: 0` whenever the off-arm host walks away inside its 20 s window. Timing chain
  stopped at its gate (06:27 PHT, `conversation rerun not PASS`). Options for Rodney: allow exactly this stack in the off arm
  (job key `scriptErrorsReport` with a regex on `Conversation:RemoveSim` + `LeaveOrig`: reported, not counted; `scriptErrorsAllow` only covers stack-free ErrorTrap correction logs) and re-arm timing, or shorten the off arm
  (check right after the seed) so the host has no time to leave.

**Try 2 (Rodney 2026-11-23: option 1 + several samples), built, queued:**

- **Multi-sample check** (`DeadRefs.cs`): `seed` builds 5 conversations per arm (distinct townies, each pair unrelated to each
  other and to every other seeded Sim, so one Destroy cannot reach another sample; < 3 pairs = the seed throws). Phase-15
  removals are counted per seeded conversation. Each sample is judged by `DeadRefsCore.ConvSampleVerdict` (pass / fail / n/a):
  off arm = gone member kept (pass), gone with the host still in it (fail), host left before the check (n/a); on arm = host
  still listed and no gone member (pass), empty list with that conversation's own removal (pass), empty without it while the
  host left (n/a), a gone member left while the host is there or after a removal (fail). `ConvArmVerdict`: any fail = FAIL,
  fewer than 3 pass = inconclusive (FAIL of the step), else PASS. The step result lists every sample as
  `<host>: <verdict> (gone g, members m, removedHere r[, host left])`. 18 selftest cases (`tools/selftest/deadrefs`).
  `repros.json`: the off seed waits for >= 3 samples holding their gone member; the on poll waits for `pending` 0.
- **Off-arm-only report** (`queue.ts`, harness): job key `scriptErrorsReportBefore: "on"`: a `scriptErrorsReport` match counts as
  studied only when its XML `Log-Time` is before the repro's `[on]` step; at or after it, or with no readable window, it
  counts. Regex `System\.NullReferenceException ... #0: ...Socializing\.Conversation:RemoveSim ( ... #1: ...SocialComponent:(LeaveOrig|LeaveConversation) (`
  (two adjacent frames), with job regexCases (match: our hook / EA frame; noMatch: another caller, another exception, another
  Conversation method). Checked on the kept try-1 XML: matches, Log-Time 06:13:32 PHT < `[on]` 06:13:49 PHT = studied; the
  ErrorTrap correction log does not match. queue.ts self-check covers the on-arm throw, other stacks, unknown window.
- **Build** `ts3fixes.package.ts3fixes-loop-cost-1122c` (`game-copy/ts3fixes.package.loopcost3`, sha 4fd939842e0e; loop code
  unchanged). Rerun and timing queues pinned to it; `deadrefconvo_members >= 3`.
- **Queue:** `probe-out/loopcost-conv2-wait-1123.sh` after `chain-eh-birth-repro-1123` (compat-resolve, running since 06:44 PHT,
  ~50-70 min), then the rerun chain (try 1 logs renamed `*-try1`), then the timing chain with its gate on the new rerun log.

**Try 2 result (2026-11-23 07:15-07:29 PHT): every sample PASS in both arms; job FAIL on the script-error rule (harness bug).**

- Off arm (switches off): `pass: 5 pass, 0 fail, 0 n/a`. Seeds: Diana Jones / Katrina Pala, Alexy Fresco / Elvira Slayer,
  Polly Maloney / Rafael Striker, Lilly-Bo Chique / Tom Wordy, Lisa King / (name cut in the log). Logged lines: Diana Jones
  `pass (gone 1, members 1, removedHere 0, host left)`, Alexy Fresco `pass (gone 2, members 2, ...)`; the rest of the line was
  cut at 200 characters (the repro logger's default; `max: 2000` set on these steps since).
- On arm: `pass: 5 pass, 0 fail, 0 n/a`. Seeds: Johnny Jones / Hannah Smyth, Matilda Smart / Polly Maloney, Romeo Rake /
  Jessica Talon, Bianca Rubble / Big Hartley, Wayne Bumble / (cut). Logged: Johnny Jones `pass (gone 0, members 0, removedHere 1,
  host left)` (phase cleaned, then the host left by EA's exit), Matilda Smart `pass (gone 0, members 1, ...)`.
  `deadrefconvo_members >= 3` PASS.
- The one counted script error (07:17:47 PHT) is again the off-arm #166 throw: `Conversation:RemoveSim` <- `LeaveOrig` <-
  `B12LvHook` <- Diana Jones's interaction, 17 s before this run's `[on]` (07:18:04 PHT). It counted because the job log is
  appended per same-day run and `reproStepStart` took try 1's `[on]` (06:13:49 PHT). Fixed: the last matching step, dated by
  the last ISO stamp before it (self-check case); checked on this log: last `[on]` 23:18:04 UTC, so the throw is studied.
- Try 3 (harness fix only; same build `-1122c`): `probe-out/loopcost-conv3-wait-1123.sh`, FIFO after
  `chain-sp-instantiate-guard-rerun1-1123` (its waiter started first at 07:31), then the timing chain. Try-2 logs: `*-try2`.

**Try 3 result (2026-11-23 07:53-08:08 PHT, same build `-1122c`, fixed report window): steps PASS, job FAIL on one on-arm script error.**

| Arm | Sample (host / destroyed member) | Verdict | gone / members / removedHere |
|---|---|---|---|
| off | Diana Jones / Katrina Pala | pass | 1 / 1 / 0, host left (her #166 throw 07:55:46 PHT, before `[on]` 07:56:02: reported, not counted) |
| off | Alexy Fresco / Elvira Slayer | pass | 2 / 2 / 0, host left |
| off | Polly Maloney / Rafael Striker | pass | 1 / 2 / 0 |
| off | Lilly-Bo Chique / Tom Wordy | pass | 1 / 2 / 0 |
| off | Lisa King / Wayne Bumble | pass | 1 / 2 / 0 |
| on | Johnny Jones / Hannah Smyth | pass | 0 / 0 / 1, host left after the clean |
| on | Matilda Smart / Polly Maloney | pass | 0 / 0 / 1, host left after the clean |
| on | Romeo Rake / Jessica Talon | pass | 0 / 0 / 1, host left after the clean |
| on | Bianca Rubble / Big Hartley | pass | 0 / 0 / 1, host left after the clean |
| on | Tatiana Angelista (townie, household Arnett) / Wayne Bumble | **n/a** | 1 / 1 / 0, host left before the phase |

Off arm 5 pass; on arm 4 pass + 1 n/a (>= 3 conclusive, no fail); `deadrefconvo_members >= 3` PASS. The counted error
(07:56:05 PHT) is the same #166 stack (`Conversation:RemoveSim` <- `LeaveOrig` <- `B12LvHook` <- `StartInteraction`) from
Tatiana Angelista, the n/a sample: the on-arm seed returned at 07:56:04, she started a new interaction and left the
conversation 1 s later, and the sweep step started at 07:56:06. So the throw falls in the window between the seed and the
sweep's first pass, before phase 15 could act [verified by the step and Log-Time stamps]. That window exists for any periodic
sweep fix (old cadence too: until the next hourly pass after a Destroy); it is not a loop-cost change. The rule (an on-arm
#166 throw fails the job) did what it was set to do. Timing chain stopped at its gate (08:08 PHT). Decision for Rodney in
the report: accept this run (the throw belongs to the n/a sample), or shrink the window (seed and start the pass in one
agent call) and rerun.

**Try 4 (Rodney 2026-11-23: close the seed-to-sweep gap), built, queued:** the on arm's seed step calls the repro-only verb
`seed clean`: it seeds the five conversations and steps phase 15's `ConversationHolders` over every Sim in the same agent call
(one sim-thread call, so no host can run an interaction and leave in between), gated by `DeadRefsCore.ConvSeedCleanGate`
(deadrefsweep on and the pass's own `DeadRefUnsafe` check; 4 selftest cases). Production behaviour, the pass, its cadence and
the pass rules are unchanged: the sweep step still runs after it (`deadrefcadence_snapshots`), and an on-arm #166 throw still
fails. Build `ts3fixes.package.ts3fixes-loop-cost-1122d` (`game-copy/ts3fixes.package.loopcost4`, sha 69a5c485fb3c); rerun and
timing queues pinned to it. Queue: `probe-out/loopcost-conv3-wait-1123.sh` (same name and log as try 3, so
`unbounded-notests-wait-1123.sh`, which declares itself after it, keeps waiting) starts after `chain-compat-resolve-1122f`, then
the rerun, then the timing chain.

**Try 4 result (2026-11-23 08:29-08:44 PHT, `-1122d`): PASS (queue exit 0, 0 counted script errors).**

| Arm | Sample (host / destroyed member) | Verdict | gone / members / removedHere |
|---|---|---|---|
| off | Diana Jones / Katrina Pala | pass | 1 / 1 / 0, host left |
| off | Alexy Fresco / Elvira Slayer | pass | 2 / 2 / 0, host left |
| off | Polly Maloney / Rafael Striker | pass | 1 / 2 / 0 |
| off | Lilly-Bo Chique / Tom Wordy | pass | 1 / 2 / 0 |
| off | Lisa King / Wayne Bumble | pass | 1 / 2 / 0 |
| on | Johnny Jones / Hannah Smyth | pass | 0 / 1 / 1 |
| on | Matilda Smart / Polly Maloney | pass | 0 / 0 / 1, host left after the clean |
| on | Romeo Rake / Jessica Talon | pass | 0 / 0 / 1, host left after the clean |
| on | Bianca Rubble / Big Hartley | pass | 0 / 0 / 1, host left after the clean |
| on | Bronson Littler / Hyacinth Atkins | pass | 0 / 1 / 1 |

Off 5/5, on 5/5. **Caveat [verified in the log]:** the `seed clean` step reported `phase 15 now: 74 Sims stepped, 0 members
removed`: right after `Sim.Destroy` in the same call the victims do not yet test as gone (the seed's own poll waits ~2 s for
`destroyed`), so the in-call step found nothing and every removal (removedHere 1) came from the sweep pass afterwards. The
gap was therefore not closed by the new verb; this run passed because no on-arm host left in that window. A later run can
still hit the #166 throw there. Options: run the in-call step on the seeded conversations treating the just-destroyed
victims as gone (repro-only), or accept the window as a property of any periodic sweep.
