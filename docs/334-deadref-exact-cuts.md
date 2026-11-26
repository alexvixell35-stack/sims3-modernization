# 334. deadrefsweep exact cuts: `deadrefexact` (LC-1 of docs/333) (2026-11-23)

Status: built, selftests PASS, queues written, NOT RUN. Branch `feature/lc1-exact-cuts-1123`, built on the loop-cost lane
(`feature/ts3fixes-loop-cost-1122`, docs/330). Package LC-1 of docs/333 §6. Ledger §588.

## 1. What changes

`deadrefexact` is a ts3fixes sub-switch, **default OFF**. It refines the `deadrefcadence` pass (docs/330) and does nothing while
cadence is off. It makes three exact cuts. Each list and each removal stays the same as in the cadence pass.

| Cut | Before (cadence pass) | With `deadrefexact` |
|---|---|---|
| (a) typed lists | Five filter passes over the world snapshot (`DrOfType<Sim>`, `<Fish>`, car / boat, `<HorseshoeCourt>`, `<Dartboard>`), each unsliced inside the first slice of its phase | One index loop over the snapshot sorts each item into the five lists (`DrTypeMask` + `LoopCostCore.PartitionAdd`). Each list keeps snapshot order, as its filter made it. The loop reads the clock every 8 items, and between slices it does `Sleep(0)` and the unsafe re-check, like a phase. The time goes to phase 5, the first that asks. It runs only on passes that run the Sim group; a description-only pass keeps the one fish filter |
| (b) ObjectDisposed | `HandlerBody` and `CookingBody` each read the field by reflection and each build an invocation list | `LoopCostCore.SharedDrop`: one read and one list. The field is read again only when the handler part removed something, or when it threw. Each part keeps its own try. The cooking part still re-reads at each removal, as `CookingBody` did |
| (c) per-object lookups | `ShortcutOn("deadrefcooking")` and `PatchCounters()` once per object | Once per slice. A flip of the switch applies from the next slice (at most `kSliceUs` later) |

Code: `DeadRefs.cs` (`DrExact`, `DrPartition`, `DrPartitionVerify`, `DrObjectStepExact`, run-loop hooks);
`LoopCostCore.cs` (`PartitionAdd`, `SameItems`, `SharedDrop`).

**Same items and same removals** [verified by selftest, our source]:
- (a): the snapshot is the pass's own list and nothing else holds it [verified, DeadRefs.cs `objWalk`]. The type tests are
  the same, and an item goes to every list whose test it passes [verified source; `PartitionAdd` checked offline against the
  filters; `DrTypeMask` itself only live, by `deadrefexactverify`]. Items are still re-tested at their turn, as before
  [verified, the phase steps].
- (b): selftest `tools/selftest/loopcost` runs 2,000 random handler sets (relationship and cooking handlers in random order,
  gone or stale or neither, throws in the handler part) through the old two-body flow and through `SharedDrop`. The removals,
  error counts, remaining handlers, cooking process state and dropped listeners match. The field is read once fewer whenever
  the handler part changes nothing.
- (c): the merged step has no yield inside a slice [verified, `RunSliceEvery` and the step have no `Sleep`]. The switch is
  live and agent-settable, so a flip inside one slice applies one slice later. That only matters for an A/B flip, and the
  sweep's next pass sees it anyway.

**Two deliberate differences from the cadence pass** (not in the removals):
- The partition yields between its slices, and it re-checks the unsafe state there, like any phase slice. The five filters were
  atomic. A pass can now stop inside the partition (`[410]`, then the `finally` re-sets the dirty flags, as at every other stop
  point). The next pass repeats the group. That is the reason for slicing: at 10x the filters are ~25 ms of one atomic slice.
- Objects destroyed during a partition yield are still in the snapshot. They are skipped at their turn, as before
  [verified, `HasBeenDestroyed` / `SimGone` tests in each step].

**Household** [verified, our source]. No new Sim-facing logic. Every phase sees the same Sims, the same objects and the same holders. The existing
exemption (phase 15 skips household conversations, [996]) is untouched. The sweep removes only references to destroyed Sims or
disposed descriptions, so it is EA-identical cleanup for every Sim, household included. HH class: R (same removals).

**Test-only switch `deadrefexactverify`** (default OFF): after every partition, it also runs the five old filter passes,
unsliced, and compares each list item by item. That covers household Sims too, so it is the queue's household-untouched check.

## 2. Counters (slots 1658-1661, 1669-1670; agent names)

| Slot | Agent name | Meaning |
|---|---|---|
| 1658 | `deadrefexact_partitions` | partition passes (switch read-back: > 0 only when the switch is on) |
| 1659 | `deadrefexact_partition_objects` | objects in the last partition |
| 1660 | `deadrefexact_shared_lists` | objects whose cooking part reused the handler part's list (cut (b) taken) |
| 1661 | `deadrefexact_rereads` | field re-reads after a handler removal |
| 1669 | `deadrefexact_verify_lists` | lists compared (verify switch) |
| 1670 | `deadrefexact_verify_diff` | lists that differed: **must stay 0** |

`tools/harness/loopcost-report.ts` takes `--arm deadrefexact` to sort the A/B slices by this switch alone.

## 3. Queues (written, dry-run OK, NOT RUN)

- **`lc1-exact-correct-1123`** (correctness, any machine, ~2 h 10 min). Smoke first: BP 3 min, verify on, partition ran,
  `verify_diff` 0. Then BP / IP / SV 10 min natural play with `deadrefexact` and `deadrefexactverify` on (at least one full
  pass, a partition, `shared_lists` >= 1, `verify_diff` 0, `scriptErrorsMax` 0). Then the eight docs/330 deadref repros with
  the switch on, keeping their existing pass rules and adding partition >= 1 and `verify_diff` 0; handler repro `rereads` >= 1,
  cooking repro `shared_lists` >= 1. Switch read-back: `deadrefexact_partitions` (0 while off).
- **`lc1-exact-time-1123`** (TIMING, **needs quiet machine**, ~1 h 50 min, run only after correctness PASS). Smoke job first
  (BP 5 min, 1-min slices, `--ab deadrefexact`). Then BP / IP / SV 25 min in-load ABBA (5-min slices, off,on,on,off, 10 min
  per arm), `--ab deadrefexact`, with `deadrefcadence` on in both arms. Per arm: `loopcost-report.ts --arm deadrefexact`
  (deadrefsweep ms/s, p5 and p10 ms/s), plus `hitch-ab.ts <tag>` for p99 / p99.9 / max.

Both run on the loop-cost lane's set: m1122 asi + ini on Apex + bridge (Bin state `apex`), package
`game-copy/ts3fixes.package.lc1exact1`, and agent `game-copy/ts3agent.package.lc1exact1` installed into the worktree `build/`.

## 4. ON case (Rodney 2026-11-23, docs/333 §6)

The switch goes ON once (1) `lc1-exact-correct-1123` passes, and (2) `lc1-exact-time-1123` shows no regression in our ms/s or
in hitch p99 / p99.9 / max beyond noise, and (3) the scaling case shows a gain at 4x and 10x. A small or zero gain at 1x is
accepted.

**Scaling case** [inferred, I-588-2]. It uses the docs/333 §3 model: content factor k on objects and Sims; at k >= 2 the Sim
group and the object walk run on every pass; SV pass rate ~0.23 per wall second (11.4 ms/s over 49.6 ms per pass).

| Part | Grows with | 1x (SV) | 4x | 10x |
|---|---|---:|---:|---:|
| (a) filter passes cut to one loop: 5 loop overheads -> 1, same 6 type tests per object | world objects x Sim-group runs | 0-~2 ms/s (the Sim group runs only after events) | ~0-10 ms/s | ~0-25 ms/s |
| (b) + (c): one field read, one invocation list, one switch lookup and one counter fetch fewer per object in p10 | world objects x object walks | ~1-2 ms/s (docs/333 §9.1: 15-25% of p10) | ~5-9 ms/s | ~14-24 ms/s |
| **Total** | | **~1-4** | **~5-19** | **~14-49** |
| docs/333 §6 table (same model, single figure) | | | ~5-7 | ~12-17 |

The (a) bracket is wide because the split of the measured ~0.25 us per object per filter pass between loop overhead (cut) and
type tests (kept) is unknown. The lower bound assumes all of it is type tests, so (a) saves nothing. Even then (b) + (c) alone
gives ~5 ms/s at 4x and ~14 at 10x, so **the scaling case shows a gain at 4x and 10x**. Timing at 1x settles where (a) falls
(p5 ms/s per arm). M-1's per-part counters (docs/333 §8) size (b).

The worst slice also changes. Today at 10x it is a ~60 ms atomic first slice (a ~35 ms snapshot plus ~25 ms of filters,
docs/333 §9.2). The partition is sliced, so only the snapshot stays atomic (~35 ms, LC-6).

## 5. Risks and open points

- At 1x the gain may be inside noise: the Sim group runs only after events. That is accepted by the ON rule. The timing queue
  is there to prove no regression.
- If `is` tests dominate, cut (a) saves little. The verify switch costs the five old passes again. It is test only and stays off
  in timing.
- After the last partition slice, the phase's first slice continues without a yield. That is the same placement as the
  gather today (docs/333 §9.2), so a slice can run up to one budget plus 8 items past the budget.
- Base: the loop-cost lane (`-1122d` code), not M-1 (`feature/loopcost-measure-1123`). Both edit `DeadRefs.cs`, so the merger
  must reconcile M-1's hook lines in `DrObjectStep` with `DrObjectStepExact`.
