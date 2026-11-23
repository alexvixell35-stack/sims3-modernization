# 322. Unseen-Sim cost by distance band (2026-11-22)

docs/320 §5.4 step 1 ("safe first step"): before any cap, skip or park threshold is tightened by distance, measure which
distance band from the viewers owns the sim-thread time spent on Sims the player does not control. Measurement only.

Branch `feature/unseen-distance-probe-1122`; ts3native `0.36.299-uband2` (master e1122 + this; uband1 smoke showed the TaskSeam side table full: 957,961 of 1.34M BP task runs unattributed, so uband2 raises it to 65,536 nodes and 4,096 hashed classes); ledger §576.

## 1. What was built

`[UnseenProbe] Bands=` (ts3native.ini, default empty = off; needs `[UnseenProbe] Enabled=1`). Ascending edges in metres, up to 6.
The queue uses `50,150,400,1000` (5 bands: <50, 50-150, 150-400, 400-1000, >=1000 m). Nothing is tied to the world size: the
edges come from the ini and every distance is computed at use from this tick's points.

**Distance** = the nearest of the points `FarFromViewers` uses (`mods/ts3fixes/src/Fixes.cs` `FarCompute`) [verified, code]:
camera target, camera position and every active-household Sim in the fresh viewer table (`UnseenViewers.cs` pushes them every
5 ticks; native `g_utView`). With no fresh table the camera points alone count (`camOnly` counter). The camera reach is not a
band edge: on-screen / within-reach Sims are still split by the existing `[UnseenProbe]` class.

**Rows:** NPC bands 0..n; `household` = an active-household Sim (the selected Sim is one of them) or a Sim of unknown scope (no
fresh household table, `AnxHousehold` / the TaskSeam id test): never in a band; `no position` = an NPC whose position could not
be read (no transform, not found by id, no camera and no viewers).

**Sites** (each a sub-part of a site an existing probe already times; file `native/ts3native/unseenband.cpp`):

| # | Site | Hook (existing) | Needs |
|---|---|---|---|
| 0 | locomotion agent update of a Sim (`0x957210` whole call) | `unseen.cpp` `UnAgentBody` | `[UnseenProbe]` |
| 1 | keyed `+0x918` waits inside that update (part of 0) | `UnWait` | `[RouteGate] Sites=1` keys |
| 2 | `UpdateSimFootprint 0x833560` of a Sim (part of 0 or of a script run) | `UnUsfHook` | `Footprint=1` |
| 3 | `IC_Route_Plan` with a Sim follower (part of a script run) | `UnPlan` | `[RouteAsync]` |
| 4 | rig pose `0x940430` of a Sim | `unseenanim.cpp` `AnxPose` | `[AnimPhase] Enabled=1 Split=1` |
| 5 | the Sim's own script task run (ScriptProxy target `Sim`: `Sim.Simulate`, interactions, idle, script route calls) | `taskseam.cpp` `TskRunHook` | `[Diag] TaskSeam=1` |
| 6 | `AutonomyManager` task runs (the one global search per tick, go-live included), by search actor | `TskRunHook` + ts3fixes `UbAutActor` | TaskSeam |

Disjoint total per band = 0 + 4 + 5 + 6; sites 1-3 are shown, not added. The actor for site 6 comes from ts3fixes: at
`Autonomy.ScoreInteractionsOnObjects` entry (the AutShadow B7 hook, sim thread only) it sends the actor's ObjectId on the
viewer channel (codes 27 low, 28 high); ts3native answers 0 when bands or TaskSeam are off and ts3fixes stops for that load.
A run is put in the band of the actor named last (a yielded search resumes in a later run with the same actor) [inferred,
I-576-1]. Sims inside a rabbit hole keep a position (inside or at the building) and land in that band, or in `no position`
when the object is gone [inferred, I-576-2].

Output: export `ts3native_unseenband` (v1, 0x260 bytes, docs/registry.md), read by `tools/probe/agent.js` as `ubN_<site>_<row>`,
`ubUs_<site>_<row>`, `ubSimUpd_<row>` (Sim agent updates per row; / `unPasses` = Sims per band per locomotion pass); one
`unseen bands <site>` line per site and one `unseen bands:` summary line in `ts3native.log` every minute (cumulative);
report `node tools/harness/unseen-band-report.ts <native.json>` (table per world, bands over 5 ms/s).

**Behaviour:** none changed. Every hook calls EA with the same arguments; the band code only reads positions and adds to
sums. Household Sims are untouched by construction and are reported in their own row. The viewer table is now also accepted
with only bands on (before: only with `[UnseenThrottle]`, `[UnseenAnim]`, `[UnseenTable]` or `[AutShadow] TierMs`).

**Cost:** per Sim agent update / pose / task run: one viewer scan (camera points + up to 32 household Sims, squared floats);
per Sim task run also one object lookup by id + transform read; per footprint / plan one class read. Heavy together with
`[AnimPhase] Split` and TaskSeam (a QPC pair per pose and task run): the ms/s are attribution shares, not a timing verdict.

**4x / streamed worlds:** the band edges are metres from the viewers, not fractions of the world, so the same edges apply to
a 4096 m world; the per-call work is proportional to Sims that update, not to area or lot count. No table or array depends on
world size (rows are fixed by the ini, at most 6 edges).

## 2. Queue

`tools/harness/queues/unseen-bands-1122.json`: job 1 measurement smoke (BP 2 min, stops the queue on fail: version, bands OK
line with the edges read back, task seam + anim split OK lines, minute line with ready bits loco + pose + tasks + autonomy
named, Apex + bridge lines, 0 script errors, ub* counters moved), then BP, IP, SV 10 min each (profile seed 12345, no forced
actions, release defaults + the probes above, external-load check on: a noisy job reruns once). Bin state `apex` (Apex +
RTX Remix bridge via `tools/turbo-compat.ts` snapshot -> state -> restore), chain `probe-out/queue/chain-unseen-bands-1122.sh`.
Runs from the lane worktree (its agent.js reads the new export). Rodney approved the run 2026-11-22.

## 3. Results (2026-11-22, run 2, all 4 jobs PASS)

Full tables: `docs/data/results/2026-11-22-unseen-bands-1122-digest.md`. 600 s windows, Bin apex, release defaults + probes,
0 script errors, machine load 7-9% (no reruns). Run 1 (uband1) was stopped after its smoke: the TaskSeam node table (8192) and
class list (512) were full and 70% of task runs had no owner; uband2 raised them (side table full 0, 913 classes) [measured].

Sim-thread ms/s per band (total = loco + pose + Sim task + autonomy; Sims/pass = Sims with a locomotion agent update) [measured]:

| band | BP ms/s | BP Sims | IP ms/s | IP Sims | SV ms/s | SV Sims |
|---|---:|---:|---:|---:|---:|---:|
| <50 m | 11.6 | 2.7 | 2.9 | 0.1 | 8.5 | 1.4 |
| 50-150 m | 31.8 | 8.7 | 28.7 | 0.7 | 24.0 | 4.0 |
| 150-400 m | 57.7 | 15.4 | 31.7 | 1.9 | **167.4** | 20.5 |
| 400-1000 m | **187.5** | 46.4 | **218.6** | 15.1 | 27.1 | 5.7 |
| >=1000 m | 4.6 | 0.8 | 0.5 | 0.0 | 0.2 | 0.0 |
| no position | 0.8 | 0.0 | 0.6 | 0.0 | 0.7 | 0.0 |
| household (not a band) | 44.8 | 8.1 | 68.0 | 1.7 | 123.4 | 3.3 |
| NPC total | 293.9 | | 283.0 | | 228.0 | |

By site, the far band's time is mostly the Sims' own script tasks (interactions, idle, script route calls): BP 400-1000 m
122.5 Sim task + 30.4 autonomy + 27.6 loco + 7.0 pose; IP 400-1000 m 149.8 + 36.8 + 26.0 + 6.0 (plans 10.7 inside); SV
150-400 m 94.0 + 40.0 + 24.8 + 8.6. Keyed waits and footprint updates are < 2 ms/s in every band.

Readings:
- **Bands over ~5 ms/s** (docs/320 §5.4 step 4 candidates): every band from 50 m out in all three worlds; the dominant one is
  400-1000 m in BP (64% of NPC time) and IP (77%), 150-400 m in SV (73%). Beyond 150 m: BP 85%, IP 89%, SV 85% of NPC time.
- **Cost follows the Sim count, not the distance.** Per Sim with an agent update, BP costs 3.7-4.3 ms/s in every band
  and SV 4.8-8.2, so a Sim 600 m away costs as much as one 40 m away [measured]. The existing unseen fallbacks (EA's
  off-screen brain, `[UnseenAnim]`, caps and skips) do not make a far Sim cheaper per second than a near one in this profile.
  (IP has few walking agents per pass: its Sims are on boats or in rabbit holes, so its per-Sim figure is not meaningful.)
- `no position` < 1 ms/s everywhere and autonomy runs with no named actor 0-1: I-576-1 and I-576-2 hold for these runs.
- These are attribution shares with heavy tools on (a QPC pair per pose and task run; seam self cost ~3.4 ms/s), not a
  timing verdict.

**4x / streamed world [inferred]:** at the same density a 4x-area world holds ~4x the NPCs, and nearly all of the added ones
land in the >=400 m bands (the viewers do not move). With cost per Sim flat by distance (~4 ms/s per BP NPC), the NPC share
would grow from ~290 to ~1,100 ms/s: more than the sim thread has. So a large or streamed world needs far bands to become
cheaper per Sim (park, dormant, longer search and task intervals by band), not only the existing per-decision cuts. Next
step per docs/320 §5.4: tighten the existing caps / skips / park thresholds for >=150 m first (household and selected Sims
exempt, unknown scope = household), then measure the same table again.
