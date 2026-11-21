# 676. World-level routing work: path smoothing (`[WorldPrecompute] SmoothCut`)

2026-11-20 / 2026-11-21 (UTC), contributor. This file carries §15 only. On the contributor's branch §1-§14 cover the
world A\*, the water plan and the post pass (`[WorldPrecompute] WaterScanTable`, `Probe`, `SmoothMemo`, `WorldAlt`); they
are not in this repo yet and come with their own PR. §15 stands alone: references to §11-§14 below name those sections,
and the facts this section needs from them are repeated here. Background: docs/98 (the quadtree and its cell evaluator),
docs/663 §2, docs/673. Tags as in AGENTS.md.

## 0. Answer

Path smoothing (post pass 3, `0x64DD40`) asks about 121 segment tests a pass, two failing ones per anchor sample; on a lot
each test walks every quadtree cell the segment crosses because the walk's callback never stops it. `[WorldPrecompute]
SmoothCut` (default off) runs that query through a copy of the walk's loop over the game's own cell functions and ends it
at the first cell that decides "blocked", trying the last blocking cells first. Same answers for every Sim (shadow: 218,969
queries, 0 mismatches); the post pass went from 5,634 to 4,887 ms a 2-minute storm (-13%, s202 -> s222), no hitch.

## 15. Pass 3: where the 2.5 ms go, and `[WorldPrecompute] SmoothCut` (default off; shadow and serve ran, §15.10)

Branch `feat/post-pass-1120`, 2026-11-20. Inputs: the `Probe` lines of s195 / s196 / s198 / s200 (the §13 probe ran),
SmoothMemo's lines of s212 (shadow) and s213 (serve), Ghidra headless on a private copy of the project (`0x64DD40`,
`0x644560`, `0x644080`, `0x643E60`, `0x6418B0`, `0x641A10`, `0x6443E0`, `0x641B40`, `0x6675E0`, `0x667350`, `0x663B20`,
`0x662D30`, `0x66A040`, `0x66A760`, `0x669CB0`, `0x669C30`, `0x669BE0`, `0x66B090`, `0x658A00`, `0x658870`) and objdump of
the sites. Bytes read from the contributor's game copy only. In-game results: §15.10.

### 15.1 The numbers [measured, s195: 2-minute storm, `Probe=1`, times include the probe's stubs]

| | ms | calls | per pass 3 call | each |
|---|---|---|---|---|
| pass 3 `0x64DD40` | 6,179 | 1,779 | | 3.47 ms |
| segment test `0x644560` | 5,647 | 215,942 | 121 | 26 us |
| lot test `0x643E60` | 3,143 | 135,799 | 76 | 23 us |
| world (off-lot) test `0x658A00` | 2,213 | 149,557 | 84 | 15 us |
| nudge `0x642420` | 236 | 1,779 | | |
| next lot, corners, sample predicate | 84 | | | |

- The segment tests are 91% of the pass; the lot test is half of it and the world test a third. The dot-product reject in
  `0x644560` fired 27 times in 215,942. `+0x72` was set in 9,640 of 215,915 lot walks (4%): the mode of §11.2 that builds a
  footprint set per test is rare.
- s196 / s198 / s200 give the same shares (lot test 55-58% of the segment tests, 16-19 us each).
- SmoothMemo's s213 line: 9,476,096 cell evaluations in 1,648 passes, 5,750 a pass. With 76 lot tests a pass that is
  about 75 cells per lot test [inferred: the two figures are from different storms].

### 15.2 Why SmoothMemo gave nothing

- s212 shadow: 4,348,062 records compared, mismatch 0: the memo is exact. s213 serve: 3.59 M of 9.48 M evaluations came
  from a record and the post pass was 5,659 ms / 1,648 calls against 5,634 / 1,672 in s202. No gain.
- Its "would have replaced 10 ms" figure was a timer bug on the contributor's branch (whole microseconds summed per
  evaluation); what s213 measured is the end to end time, and that did not move. SmoothMemo is not in this repo.
- So an evaluation costs about what the memo's own key (12 words, a hash of the options' 0x38 bytes) and probe cost
  [inferred from the end to end result]. Most cells a segment crosses hold no footprint entry, and such a cell is the tree's
  default (docs/98 §5.2 step 8). The time of a lot test is the walk itself: per final cell about one and a third inner
  nodes, four child steps `0x66A040` and four box tests `0x66A760` each, plus the callback.
- "Slots taken 4,487,401" is the table of 4,096 filling in long passes (846 records kept a pass on average; the long
  passes carry the count). A larger table would raise the hits, not the gain per hit. Not pursued.

### 15.3 What the loop does [verified: decompile and disassembly `0x64E0F3..0x64E359`, re-read; it corrects §11.1 in one place]

Cursor = (node index, x, z, level, lot, next lot, step, steps, dx, dz) (`0x6418B0`); a path segment longer than 5 units
has `ceil(0.2 x length)` steps. Anchor A at `[esp+0x68]`, probe P at `[esp+0x28]`, the last good probe position G.

1. P = the node after A, G = P (not tested: it is A's own path segment), P steps on.
2. While the node-type gate passes and (G is at the path's start or the segment A-P is clear): G = P; P steps on. At the
   last node: commit and return.
3. At the first blocked P: step P once more and test A-P again (the one lookahead). Clear: G = P, back to 2.
4. Stop. If G is still the node after A: **A steps one sample** (`0x641A10` on A). Else commit A-G (`0x6443E0`: rewrites
   the nodes), which leaves A at the shortcut's start and steps it **one sample** too.
5. P = the node after A again (`0x6418B0` on P with A's index + 1: §11.1 said "behind the anchor"), G = P, back to 2.

So every anchor stop ends in **two failing tests** (one when the gate or the last node ends it), and A then moves at most 5
units and asks again for nearly the same segments: from A's next sample to the same one or two samples past the next
node. 121 tests a pass is what "two failing tests per anchor sample, plus one per sample the probe gains" gives for a
path of some tens of samples [inferred: the probe counts calls, not results; `SmoothCut`'s line will show the split].
No segment is tested twice, but **the failing fan from the moving anchor to the same target is the pass**.

### 15.4 What a failing lot test pays [verified]

- `0x6675E0` (one caller: `0x643E60` at `0x644039`): the two end cells' multipliers m0, m1 (`fstp [esp+0x58]`, `[esp+0x08]`);
  either is `FLT_MAX` (the float at `0x11584C8`): blocked, no query. Else the segment query `0x667350` (one caller:
  `0x6676CE`), and the answer is **clear exactly when the query saw a cell and `m0 >= largest` or `m1 >= largest`**
  (`comiss` + `jae` at `0x6676E5`, `0x6676F0`). The least value comes back too and is not read.
- `0x667350`: flushes deferred edits when the tree is not pinned (`+0x48 < 1`), pins, runs the walk `0x663B20` with the
  callback `0x662D30` and a block (count, least, largest, flag byte), unpins.
- The callback takes the cell's multiplier, makes it `FLT_MAX` when the flag byte is 0 and the class is above 1, updates
  count, least and largest, and **returns 1 on every path** (`mov al, 1` at both exits). The walk would stop on 0
  (`0x663DBF`), but this callback never asks for it. A segment that the first cell blocks is walked to its end.
- The walk: 13 stack cursors, depth-first, children 0..3; a child is entered when its box test passes; a final cell
  (`0x669CB0`: no node, or shape 0x15 and up) goes to the callback. A cell that is not final at level 13 ends the whole
  walk (`cmp esi, 0xD` at `0x663DC5`).

### 15.5 `SmoothCut`: the design, and why the answers are the game's

`native/ts3native/smoothcut-core.h` is the walk's loop again over the game's own functions (constructor `0x669BE0`, root
set-up `0x669C30`, final test `0x669CB0`, child step `0x66A040`, box test `0x66A760`, callback `0x662D30`), in the game's
order, with two early ends:

- **cut:** after a callback, when the largest value so far is above both m0 and m1, the walk ends.
- **hint:** each thread keeps the last four cells that ended a walk this way, as (tree, child numbers from the root, two
  bits a level). Before walking, a query of the same tree goes down each hint's child numbers with the game's child step
  and box test; if every box passes and a final cell is reached, the game's callback runs on it; a value above both end
  multipliers ends the query without a walk. In the fan of §15.3 the cell that blocked A's test is nearly always the cell
  that blocks the next sample's.

| claim | status |
|---|---|
| The largest value never falls during a walk (`comiss v, largest; jbe`), and "blocked" is "largest above both end multipliers" or "no cell": once it is above both, the rest of the walk cannot change `0x6675E0`'s answer | [verified, §15.4]. A NaN never passes our test (ordered compares), so NaN cases walk whole |
| A hint's cell is evaluated only when every box from the root down passes: the condition under which the game's walk enters it | [verified: the walk's only tests are final / box, `0x663C95..0x663DE3`] |
| ... unless the game's walk ended before it at the depth stop | [inferred not to happen]: a cell not final at level 12 makes the game step into a 14th cursor, which lies on the walk's own return address (13 cursors of 0x24 bytes from `esp+0x7C`, frame 0x250); the game would crash one level before the stop. Our walk keeps the stop all the same; shadow mode would show a difference |
| The callback and the evaluator under it leave no lasting write except the context's class word `+0x4C`, written only when it is 2 or more, not 13, and not below the cell's class | [verified here for the class word: decompile of `0x66B090`; the rest is §14.2's list, backed by s212's 4.3 M equal replays]. **A query whose class word is 2 or more and not 13 is passed to the game untouched** (`scNoCut`), so skipping or reordering evaluations changes nothing |
| A hint holds nothing of the tree but child numbers; a stale hint (tree changed, another tree at the same address) fails a box test, ends on a non-final cell, or finds a cell that does not block | [by construction; host test with trees that change between queries] |
| Flush and pin are done as in `0x667350`, before anything is read | [verified against `0x667357..0x667367`] |
| m0 and m1 are read from `0x6675E0`'s frame relative to the "largest" out pointer (`-0x10`, `-0x60`) | [verified: `0x667647`, `0x667655`, `0x66768C..0x667698`, `0x6676D9..0x6676F3`; those bytes are compared at install] |
| The least value on an early end is the least so far, not the walk's | does not matter: [verified] `0x6675E0` reads only the result and the largest |

Different paths: none. Household Sims: the same answers, so no scope rule; the switch is one for all.

Site and install: one `E8` retarget at `0x6676CE`, no address row. Found from the post pass's row `0x64E380` (its call of
pass 3 at `+0x48`, pass 3's segment test at `+0x426`, the lot walk at `+0xB8`, the lot test at `+0x189`) down to the lot test, then `+0x1D9` -> `0x6675E0`, `+0xEE` -> `0x667350`, whose `+0x7C` must
call the row `0x663B20` and whose `+0x3A` pushes the callback. Checked byte for byte: the two `fstp`, the out-pointer
pushes and the compare in `0x6675E0`; the head and `ret 0x24` of `0x667350`; in the walk the root arguments, the depth
compare, `ret 0x24`, and that its three final-test calls and two box-test calls agree; the final test's 24 bytes; both
`ret 0xC` of the callback. Anything else: `SKIP ... nothing written`. Calling conventions read from the disassembly:
`0x669C30` `ret 0x28`, `0x66A040` `ret 8`, `0x66A760` `ret 0x14` (result in `al` only), `0x669CB0` and `0x669BE0` `ret`,
`0x666A40` `ret` with `ecx`, the callback `ret 0xC` with `ecx` = tree.

Beside the others: no other hook of this repo sits on `0x6676CE` or on the chain above it (`[RouteAsync] TailUnlock` owns
passes 16-19 of the post pass, `[RouteJobPhases]` times its entry). On the contributor's branch it also runs beside
`[WorldPrecompute] SmoothMemo` and `Probe` (not in this repo).

Keys: `[WorldPrecompute] SmoothCut` 0 off (default), 1 serve, 2 shadow; `SmoothCutHints` 0-4 (default 4). Runtime switch
`native_set smoothcut` (export `ts3native_smoothcut` `+0x00`, docs/registry.md); the hook installs only with a non-zero ini
value. Shadow: ours runs, then the game's query; the game's result is returned; a whole walk must match in "saw a cell",
least and largest bit for bit, an early end must be a game "blocked"; else `scMismatch` and `SMOOTH CUT MISMATCH` (10
lines). Line, once a minute: `world precompute: smooth cut mode M ready 0xR: lot segment queries by our walk N (ms):
blocked by a remembered cell N (tries, cells reached), blocked at a cell in the walk N, walked whole N blocked + N clear
(cells evaluated in walks N); passed to the game N, with mode 0 N; shadow checks N mismatch N (the game's queries ms)`.
The two times are tick sums, converted once.

### 15.6 Expected [inferred; the shadow arm measures it]

- Touches the lot tests with `+0x72` clear: 3.1 s of 6.2 s of pass 3 in s195, so about half of pass 3 and 0.4 of the
  post pass (5.6 s a storm in s202): about 2.2 s a storm in reach.
- If two tests in three fail (§15.3) and the walk is 0.85 of a lot test: a cut alone saves half of a failing walk on
  average (the blocking cell comes at a random place in the walk's order): 0.67 x 0.85 x 0.5 = 0.28 of the lot test time,
  about 0.6 s a storm. With a hint hit on 70% of the failing tests (a hit costs 1-2 us against 23): 0.67 x 0.7 x 0.9 +
  the cut on the rest = about 0.5 of the lot test time, about 1.1 s a storm, a fifth of the post pass.
- Lower bound: tests that fail at an end cell or at different levels cost nothing today and gain nothing; clear tests
  pay up to four hint tries (about 1 us of 23). If most lot tests are clear, the gain is near zero and
  `SmoothCutHints=0` removes the tries.
- The split is in the line: `blocked by a remembered cell` + `blocked at a cell in the walk` against `walked whole`.
- Not touched: the world test `0x658A00` (2.2 s in s195). It ends at its first failing point already; its points are the
  segment's own midpoints, so nothing carries from one anchor sample to the next.

### 15.7 Designed, not built: fewer tests (other paths, NPC only)

The fan itself (two failing tests per 5-unit anchor step) is by the loop's design; an exact change is not possible
because every test is a new segment. For Sims the player does not control, behind its own switch:

- **Skip anchor samples that cannot help:** after a stop with G still the next node, step A to the node instead of one
  sample when the two failing tests were blocked by a cell that also blocks from the node's side (the hint cell is
  inside the triangle A, node, target). Paths differ only where the game would have found a shortcut from a mid-segment
  sample: they keep the corner at the node. Every committed segment is still tested clear by the game's test.
- **Gallop the probe:** test A to samples 1, 2, 4, 8, ... and bisect, instead of one by one: the clear tests of a long
  straightening drop from n to about 2 log n. The shortcut found is valid (tested) but can be longer or shorter than the
  greedy one where visibility along the path is not monotone.

Both need the loop of `0x64DD40` replaced (its cursors and `0x6443E0`), not a call-site hook. Decide after the `SmoothCut`
line shows clear against blocked tests: the first idea pays if blocked tests dominate after the cut, the second if clear
ones do. About 2-3 hours to build either with a host test, one storm each.

### 15.8 Checks (no game)

- `smoothcut-host-test.cpp` (Linux, 32-bit, freestanding; `smoothcut-test.h` as is): a fake quadtree with the game's
  layout and fake cell functions; the game's query written as a recursion, not as the loop under test; a fake `0x6675E0`
  with the frame slots. 4,000 random segments and anchor fans in serve mode against the game's query (same answer every
  time, clearly fewer evaluations, hints and cuts both used, flush and pin as the game's); shadow (the game's outputs
  bit for bit, no mismatch); a forced mismatch; the class word rule (12, 13, 1, 0); no context; other flags; a tree that
  changes between queries with the hints kept; two trees in turn; no hints; a pinned tree; a root that is the only cell;
  the depth stop; NaN and `FLT_MAX` multipliers. Twelve deliberate bugs (cut on one end only, cut at equal, a hint
  without its box test, a hint cell not checked, no depth stop, three children, no class word rule, no flush, pin not
  released, the wrong frame slot, shadow returning ours, the flag byte lost) each fail it; a hint for another tree
  passes, as it must.
- clang-cl `/Zs /W4` with the xwin headers on `ts3native.cpp` (Steam and `/DTS3A_TABLE`) and `selftest.cpp`: 0 errors,
  no warning in `smoothcut*`. `node tools/selftest-all.ts --ci` pass. The MSVC build and the Windows selftest are the CI
  `native` job.

### 15.9 Repro

`ip-smoothcut-storm` (repros.json): ini `SmoothCut=2`; arms off, shadow, serve by `native_set smoothcut`. Shadow arm passes
with mismatch 0 and no `SMOOTH CUT MISMATCH`; its line gives our time against the game's on the same queries. Serve arm:
the post pass time (`[RouteJobPhases]`) against the off arm.

### 15.10 Results [measured: Isla Paradiso, 2-minute route storms, every proven fix on; Steam Deck, Linux Wine + DXVK]

| run | mode | result |
|---|---|---|
| s216 | shadow | 218,969 lot segment queries compared, **mismatch 0**; our walk 1,810 ms against the game's 2,218 ms on the same queries |
| s217 | serve | post pass 5,248 ms against 5,634 ms in the baseline s202; 0 hitch frames (>= 300 ms); failed plans 95 a minute |
| s222 | serve (rerun) | post pass **4,887 ms (-13% against s202)**; 0 hitch frames; failed plans 66 a minute |

- The failed-plan rate of s217 was run-to-run noise: s216 (shadow, the game's answers) had 62 a minute and the s222 rerun
  66 [measured]. SmoothCut gives the game's answer for every query (§15.5), so it cannot change which plans fail.
- One run per arm; the post pass saving (0.4-0.7 s a storm) is near the low end of §15.6's estimate [measured vs inferred].
- Not measured here: the share of hint hits against cuts per storm beyond the minute lines; other worlds.

Files: `native/ts3native/smoothcut-core.h`, `smoothcut.cpp`, `smoothcut-test.h`, `smoothcut-host-test.cpp` (new);
`ts3native.cpp` (include, install call, counter line), `selftest.cpp`; `ts3native.ini`, `tools/ini-sections.json`,
`tools/harness/native-switches.ts`, `tools/harness/repros.json`, `docs/registry.md`, `docs/68-hook-conflict-map.md`,
`docs/index.md`.
