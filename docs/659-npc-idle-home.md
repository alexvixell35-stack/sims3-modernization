# 659. NPCs standing idle at home for hours, and 4 h+ street "Visit" walks

2026-11-19. Existing `health.jsonl` censuses (run folders `phase-2026111*`, the 64 runs whose `autotest.txt` reads
`in live mode ("active_household":"<name>")`) + local decompile (`game-copy/decompiled`, not in git). No game run.
No code change: both roots are EA design (§1) or already covered by our switches (§3). No slots used.

Scripts (scratch, not in git): per-run idle share, split by "on the selected Sim's lot", and street visit episodes
(target-lot distance per snapshot from the mean position of Sims seen on that lot).

## 1. Idle at home: EA's low-LOD simulation [verified]

**Not an artifact.** `SimHealthHandlers.ShRow` (ts3agent): `still_min` = time since the Sim last moved more than 0.5 m
(sampled by `ShSample` every `sample_ticks`), `interaction` = the queue head's name at the last head change (null when
the head is null), `queue` = `InteractionQueue.Count`, `posture` = the live posture. Of the 42 140 "standing, no
interaction, still >= 120 min" NPC rows, 42 087 have `queue` 0: the queue really is empty. No idle sub-interaction
hides there (EA's idle animation loop runs with no head).

**The decisive split [measured].** Human NPCs on any non-street lot, all 64 runs:

| Where | Rows | Idle >= 2 h, no interaction |
|---|---|---|
| on the selected Sim's lot, outside | 476 | 0.2% |
| on the selected Sim's lot, inside | 75 | 1.3% |
| any other lot, outside | 49 897 | 36.4% |
| any other lot, inside | 61 185 | 39.2% |

(The brief's ~57% counted residential lots only; this table counts every non-street lot. The worlds differ:
Bridgeport 28-45%, Isla Paradiso 42-48%, Moonlight Falls 55.5%, Oasis Landing 57.0%, Starlight Shores 20-26%.)

**EA code [decompiled, Autonomy.cs].**
- `IsSufficientlyOnScreenForHighLODSimulation` (:472-532): high LOD only for the active household, the active lot,
  the active Sim's lot, hosted guests of a selectable host, daycare, resort workers, or a Sim that is outside
  (`RoomId` 0/-1) and on screen. Everyone else is low LOD.
- `ShouldRunLocalAutonomy` (:453) then returns false, `ScoreInteractionsOnObjects` (:2098) scores only meta autonomy
  (lots, rabbit holes), and with no pick `Sim.MakeSimGoHome` (:1143) has nothing to do for a Sim already at home.
- Low-LOD motives are auto-satisfied (docs/601 §2: `Motive.Update` -> `AutoSatisfyMotive`); measured here: in 26%
  of idle->idle snapshot pairs hunger rose by more than 5 while the Sim stood still. That is why energy looks normal.
- `autlastsearch` rows on idle Sims: 8 459 "none" (the meta search ran and chose nothing), ~390 picks (garden,
  community lot, GoHome), the rest not recorded (switch off).

So "standing at home for hours" is EA's off-screen simulation: a low-LOD Sim at home has no local autonomy. Same
conclusion as docs/601 §2 and docs/630 (dormant lots), now with the active-lot control. Towers rank worst because they
hold the most residents and their Sims are indoors (the on-screen exception covers outdoor Sims only).

## 2. Our mod and the harness: ruled out [measured]

- **Switch count.** Bridgeport runs with 87-122 switches on (phase-20261115-*), 246-285 (20261116-*) and 378
  (20261117-1853) all sit at 28-40%. The A/B arms show no step: `apt-on` 35.5% / `apt-off` 39.1%, `tp-on` 33.9% /
  `tp-off` 35.0%, `dlo-off` 35.2%, `bis3-8off` 37.6%.
- **Never on in these runs:** `unseenmotives`, `unseenpark`, `autunseen` (`earlyfix.txt`). `unseenloopskip` is on
  only from 20261117-1853, with no change (34.5%).
- **Elevators** (docs/655 stale `mAssignedSims`): worlds without towers are as idle or more (Moonlight Falls 55.5%,
  Oasis Landing 57.0%), and the idle rows have `routing` false and `route_fails` 0. Not the cause.
- **unseenfb holds:** `uf_hold` false on all idle rows; `unseen_why` is 0 (unseen) for 86%, 4 (active lot) for 13%.
- **Harness:** the agent only reads state; `phase health` pushes nothing. No ts3fixes-off run with `health.jsonl`
  exists (the 20261110 baseline logs predate the handler). The active-lot control above stands in for it: same mod,
  same run, only EA's LOD predicate differs, and the idle share drops from ~38% to ~0.5%.

## 3. Street "Visit <lot>" for 4 h+: EA's walk flip, mostly cut by `visitcar` / `visitwalkfinish` [measured]

- Every 4 h+ episode is **walking, not stalled**: `routing` true in 98% of snapshots, `still_min` ~0, distance to the
  target shrinking every snapshot (Sebastian Striker, Visit Bridgeport Acres: 965 -> 926 -> 790 -> 581 -> 410 -> 191
  -> 28 m over 817 sim-min, about 1.2 m per sim-minute). Most arrive (53 of 153) or are replaced by GoHome / the next
  pick; `route_fails` 0.
- Root (docs/584 §1, decompiled `VisitCommunityLot.Run` :344-355): when the front-door route does not run, an
  autonomous visit turns car planning off with `RandomChance(kChanceOfGoingToCommunityLotByCar = 50)`: half walk
  however far the lot is.
- **Ours helps.** 4 h+ share of street visit episodes: 15.1% (87 / 575) with `visitwalkfinish` off vs 3.7% (66 / 1787)
  with it on (`visitcar` on too; run 1853: 608 long visits judged, 218 kept driving, 5 walk interrupts, 3 finished).
- **Residual (43 human, non-resort episodes with the switch on):** 15 are `unseen_why` 7 (held: umbrella in rain,
  carrying a child), exempt by the "carrying stays held" rule; resort lots (Buena Vista) and pet households are exempt
  by design (followers / EP10 check-in); about 25 unseen walkers remain (Alexander French, Visit Plasma 501, 480 min;
  Maebe Kolb, Visit Crystal Waters Beach, 706 min). `VwfPass` asks one interrupt per Run (`t.Asked`) and filters by
  `VwfLotOk` (pets, grouping situation, shells), `TownieUnseen` and `FarFromViewers(lot)`; the counters do not say which
  filter dropped these. Not confirmed, so not fixed.

## Verdicts

- Idle at home: **EA design** (low-LOD Sims get no local autonomy). Not our mod, not the harness. No fix.
  Changing it means forcing high LOD for off-screen Sims, a CPU cost EA avoided on purpose.
- Street Visit 4 h+: **EA's 50% walk flip**, real walks; `visitcar` + `visitwalkfinish` cut it by 4x. Residual = held
  Sims and designed exemptions, plus a small unexplained remainder.
- Next step (if wanted): per-filter counters in `VwfPass` (lot / unseen / viewers / not routing / already asked) in
  one IP + one Bridgeport run, then decide whether a retry after a refused interrupt or a held-umbrella exception is
  worth it.
