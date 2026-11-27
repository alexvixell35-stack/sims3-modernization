# farmodel-bands-1123 digest: T3 far-band edge per world (ME-4, 2026-11-26)

Package ME-4 of docs/332 §2.1 / docs/340 §9 (phase S). Input: ME-1, queue `farmodel-bands-1123`
(`docs/data/results/2026-11-23-queue-farmodel-bands-1123.md`), run 2026-11-23 11:37-12:20 PHT, 4/4 PASS, set nsplit1,
`[UnseenProbe] Bands=150,200,300,450,1000`. Static digest, no game run.

## 1. Verdict

**The per-world T3 edge cannot be chosen from ME-1: its band tables no longer exist.** The edges below are bounds from the
older docs/322 run (edges 50 / 150 / 400 / 1000 m) and are marked [inferred]. A rerun of one existing queue (§4) gives the
measured answer.

## 2. What is left of ME-1

- The queue ran from the lane worktree `.claude/worktrees/nonsim-split-1122`, so the row file
  (`farmodel-bands-1123-2026-11-23.jsonl`), the `*-native.json` profile dumps and the kept `ts3native.log` copies were written
  to that worktree's `probe-out/queue/` [verified: `probe-out/queue/chain-farmodel-bands-1123-q.out`, `results:` line].
- `cleanup.ts` removed that worktree later the same day [verified: `probe-out/cleanup/` log line
  `removed ...\.claude\worktrees\nonsim-split-1122 0.3 GB`]. `probe-out/` is not in git. A search of the main checkout and
  every sibling worktree finds no `farmodel-bands-1123*` profile, row or log file [verified 2026-11-26].
- What survives: the results table (PASS, sim ms/s, hitch percentiles, build read-back) and the chain output. Neither holds a
  band line: `unseen bands` appears 0 times in the chain output [verified]. The results row is cut (`[cut: full row in the
  jsonl]`), so the smoke's read-back `edges 150,200,300,450,1000 m (6 bands` (the ME-1 gate) cannot be shown from kept files;
  the smoke PASS implies it [inferred: the smoke job stops the queue when a `logRequire` line is missing].
- Kept per-job numbers [measured, attribution run with heavy samplers, timing invalid]: BP sim 174.3 ms/s, IP 172.7, SV 131.8.
  They do not split by band.

## 3. Bounds from docs/322 (run 2026-11-22, edges 50 / 150 / 400 / 1000 m) [measured], edge reading [inferred]

NPC sim-thread ms/s and Sims with a locomotion agent update per pass. A T3 edge at 450 m moves at most the `>= 400 m` column;
an edge at 300 m moves that column plus an unknown part of the 150-400 m band.

| World | NPC total | >= 400 m: ms/s (share), Sims | 150-400 m: ms/s (share), Sims | T3 at 450 m moves | T3 at 300 m moves |
|---|---:|---:|---:|---:|---:|
| Bridgeport | 293.9 | 192.1 (65%), 47.2 | 57.7 (20%), 15.4 | <= 192.1 | 192.1 .. 249.8 |
| Isla Paradiso | 283.0 | 219.1 (77%), 15.1 | 31.7 (11%), 1.9 | <= 219.1 | 219.1 .. 250.8 |
| Sunset Valley | 228.0 | 27.3 (12%), 5.7 | 167.4 (73%), 20.5 | <= 27.3 | 27.3 .. 194.7 |

Working edge per world until the rerun [inferred, not a measured choice]:

| World | Working T3 edge | Why | Open |
|---|---|---|---|
| Bridgeport | 450 m (default) | 65% of NPC time is already past 400 m; 300 m adds at most 20% | size of the 400-450 m slice |
| Isla Paradiso | 450 m (default) | 77% past 400 m; 300 m adds at most 11% | size of the 400-450 m slice |
| Sunset Valley | 300 m | at 450 m T3 holds at most 12% of NPC time: no real gain | how much of the 73% band lies past 300 m; if little, SV gains nothing from T3 at either edge and depends on T2 |

No other world was measured (ME-1 and docs/322 both ran BP, IP, SV only). The edge is also bounded by model accuracy, not
only by cost: docs/324 §1.3 decision 1 lowers a world to ~300 m only where shadow calibration (FS-4) shows the model is close
enough there. This table is the cost side only.

Non-Sim ms/s past 200 m (the docs/331 §2.1 row ME-4 was to add): not available. docs/331 §2.1 splits at 150 m only
(for example `Lot` reaction pulses BP 26.1 total / 19.4 far); the 200 m split was in the lost ME-1 dumps.

## 4. Measurement that gives the answer

Rerun the same measurement, kept this time:

- Queue `tools/harness/queues/farmodel-bands-1123-m1123.json` (on master, pinned to set m1123; the m1123 asi, ts3fixes and
  agent files are in `game-copy/` [verified by file name, not by `builds.ts check`]). Smoke + BP / IP / SV 10 min, ~60 min of
  game time, Bin state `apex`. A repin to the current master set is optional: the probes are attribution only.
- Run it from the **main checkout**, not from a lane worktree, so the dumps land in the main `probe-out/queue/` where
  `cleanup.ts` does not remove them with a worktree.
- Digest in the same session: `node tools/harness/unseen-band-report.ts <job>-native.json` (Sims and ms/s per band) and
  `node tools/harness/nonsim-split-report.ts` (non-Sim rows per band), then write the six-band tables into this file and
  replace the working edges of §3. Agent time ~25 min.
- Needs Rodney's go for the game run. No build.

FS-4 (shadow calibration) can start on the working edges of §3; its own per-world error tables decide the final edge.
