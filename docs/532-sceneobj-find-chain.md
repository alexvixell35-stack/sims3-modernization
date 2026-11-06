# 532. Scene-object find chains: `[SceneObjFind]` counts the nodes `SceneObjMgr_FindObject` walks

Written 2026-11-06. Static + disassembly + the native selftest; no game run yet. Off by default: `[SceneObjFind] Enabled=0`.

## 1. What it measures

`SceneObjMgr_FindObject 0x6FDFB0` is the scene-object hash find (main-thread profile, docs/71: 2.1% self, the top non-idle
self function). The table has a fixed 1,033 buckets (`key.lo % 0x409`, a literal in the code, so it never grows), inline at
the manager's `+0`, with an end bucket at `+0x1024`. Chains are singly linked through the link at object `+8`: `next` =
node `+0`, key = node `+8` / `+12`. It runs per object per frame: one find per FrameDb object-delta record inside the FrameDb
`+0x48` lock (`FrameDb_ConsumeAndInterpolate 0x693780`), the render-set draw, scene windows, Swarm render messages and the
rig pose. 56 direct callers sit in the manager's own block `0x6FE3C3..0x7004xx`; `SceneObjMgr_FindObjectPtr 0x6FE3B0` has
52 more. With N scene objects the average chain is N / 1033; the question is whether that is long enough to matter.

`native/ts3native/sceneobjfind.cpp` detours the find's entry (prologue `83 EC 08 56 8B 74 24 14`, checked against the
installed 1.67 exe; a different build or another owner is logged `SKIP` and nothing is hooked). On the main thread the
detour walks the same chain once more, read only, counts the nodes it compares (the hit's depth + 1, the whole chain on a
miss), then runs EA's find through the trampoline, so every answer is EA's own. Calls from other threads are only counted.
The extra walk roughly doubles the find's own cost while the switch is on: use the counter run for chain lengths, not for
frame times.

Apex Radiance hooks the same entry: its ObjectLookupIndex feature (on by default) chains `RenMapFind 0x6FDFB0`, with
`RenMapErase 0x6FDF40` and `RenMapInsert 0x6FE1F0`, to keep its own id index (`docs/data/apex-hooks.csv`). Apex installs
after us, so with Apex loaded and ObjectLookupIndex on, `[SceneObjFind]` stands down (`compat: stood down for Apex` log
line, `[Compat] ApexStandDown=1`). Measure without Apex (or with its ObjectLookupIndex off). If Apex's index already answers
these finds, candidate C1 only matters for players without Apex; the docs/71 profile run should be checked for which
setup it used.

## 2. Switch and counters

`[SceneObjFind] Enabled=1` (default 0). Counters v911 (contributor block, `docs/registry.md`; after a pad at 0x5C34-0x5CB3 for the v908-v910 branches), read by `tools/probe/agent.js`:

| Field | Offset | Meaning |
| --- | --- | --- |
| `sfKCalls` | +0x5CB4 | main-thread finds, in units of 1024 |
| `sfKNodes` | +0x5CB8 | nodes those finds compared, in units of 1024 |
| `sfMaxNodes` | +0x5CBC | most nodes one find compared |
| `sfOffMain` | +0x5CC0 | finds from other threads (not walked) |

The totals are kept in 64 bits and published in 1024 units, because a BP pan does thousands of finds per frame and a 32-bit
node count would wrap within an hour.

How to read them: `sfKNodes / sfKCalls` is the mean walk per find. Below 2 the table is fine and candidate C1 is dropped.
At 4 or more (the fixpaths go rule), move-to-front or a side index can save most of the 2.1%. `sfMaxNodes` far above the
mean means a few crowded buckets (a key pattern that is bad for `% 1033`), not a too-small table. `sfOffMain` must stay 0: a
non-zero value means another thread uses the table, and any change to its order is then unsafe.

## 3. A/B plan

1. Counter run: BP save (the large streamed world), `[SceneObjFind] Enabled=1`, a camera pan over the main lots plus a
   still slice of 3 min each. Read the four counters at the end of each slice (deltas).
2. Only if the mean walk is 4 or more: build step 2 (below) behind its own switch and run `--ab` in one BP load, 3-min
   slices, the same pan: frames.csv p50 / p95, the FrameDb consume hold time (`vfFdbHolds`), and the docs/71 sampler's
   self share of `0x6FDFB0` on vs off. Expect p50 -0.5 ms or more; the counter build itself is not part of that A/B.

## 4. Step 2 (move-to-front) is not built

Move-to-front would unlink the found node at depth > 0 and relink it at its bucket head: 5 lines, no memory, same chain
content. It is skipped here because its safety cannot yet be shown:

- An iterator over the table (eastl style: `node = next`, and on 0 step the bucket pointer to the next bucket, stopping at
  the non-zero end bucket `+0x1024`) that calls the find inside its loop would skip or revisit a node once that node moves
  ahead of it. The private main-thread decompile has the bucket count `0x409` and the end bucket `+0x1024` only inside
  `0x6FDFB0` itself; the 56 direct callers in `0x6FE3C3..0x7004xx` (the insert, erase and per-message handlers that take
  the find's `{node, bucket}` iterator and then edit the bucket) and any clear / destructor loop are not in it. So "no
  iterator walks the table while a find runs" is not shown.
- A caller that keeps a node's predecessor across a find and then unlinks through it would break; the eastl erase walks
  from the bucket head and is fine, but the 56 sites are not read.
- Main-thread-only use is expected (every listed caller is a main-thread service or the render pass) but not measured:
  `sfOffMain` from the counter run is that check.

Before building it: disassemble `0x6FE3C3..0x7004xx` for loops that step a bucket pointer from `+0` to `+0x1024` and for
callers that write through the returned bucket pointer, and check that none of the loops reaches a find on the same table.
