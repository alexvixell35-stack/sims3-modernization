# 690. camsafe: one camera-safe "unseen" rule for every off-screen shortcut

Status: built 2026-11-26, default off (`camsafe`, `camsafelog`). Proof: see §6.

## 1. Problem

Every off-screen shortcut asked its own version of "is this unseen?":

| Fix | Test before camsafe |
|---|---|
| trip shortcuts (gohome, rabbithole, visit, lot trips, ...) | `TownieUnseen` = not a player Sim, standing, not in a hidden room, `!UcOnScreen(sim)`, `FarFromViewers(pos)` |
| elevskip / elevskiphhunseen (ElevSkip.cs, docs/686 §33) | NPC: TownieUnseen + doors off screen + `FarFromViewers(exit)`; household: `FarFromCamera` |
| fridgeff / fridgeffunseen (FridgeFf.cs, §36.11) | the same shape, with the fridge and its slot |
| rhskipunseen, bookskipunseen, servingsharehhunseen, gymtwin (UnseenSkip.cs, §37) | `UnsScopeUnseen`: TownieUnseen for NPCs; `UnsFar` (FarFromViewers without the Sim itself) for household opt-ins |
| RabbitHoleDirect `DoorSeen`, staged arrivals, ... | `UcOnScreen` / `FarFromViewers` |

All of them test the camera **now**: off screen this tick, and farther than the reach `max(80 m, 2 x camera height)` from
the camera target and eye. The camera can move fast (edge scroll, rotate, zoom, portrait click, lot jump, map view), so a
spot that passes can be on screen a moment later. One bug already slipped through this way: `FarFromViewers` counts
household Sims as viewers, so a household Sim was its own viewer and the household opt-ins never fired (fixed by
`FarFromCamera` / `UnsFar`).

## 2. Design (as built)

References: Sunshine-Hill's LOD Trader (judge by the chance of being seen soon, not seen now), alibi generation (decide
once, keep a valid end state ready), simulation LOD; docs/228 §4.2 / §11.2 (margin + camera-jump hold for unseen root
motion, the native side of the same idea).

`CamSafeCore.cs` (game-independent, offline selftest `tools/selftest/camsafe`) holds the rules; `CamSafe.cs` wires them
into the shared primitives, so every shortcut moves with one switch and none keeps its own copy.

1. **Predictive margin.** `RefreshViewersBody` (once a sim tick that asks) samples the camera target, eye and old reach.
   camsafe's reach = the largest reach of the last `HoldS` s x (1 + `MarginPct`/100) + smoothed speed x `LeadS` + the
   farthest the target or eye has been from its current place in the last `HoldS` s. Speed = max(this sample's speed,
   the old speed x exp(-dt / `SpeedTauMs`)), capped at `SpeedMax`.
2. **Recently-seen hold.** Spots: the last term above (a point within the old reach of any camera pose of the last `HoldS`
   s is within reach + that distance now, by the triangle inequality, so one sphere covers the whole recent camera path).
   Objects and Sims: `UcOnScreen` notes the real time an object is seen on screen; with camsafe on it also answers true
   for `HoldS` s after. Only objects that a shortcut asks about are noted (cost stays with the askers, 1024 ids max).
3. **Camera-jump pause.** A target, eye or reach change above `JumpM` between two samples (and the first sample) holds
   everything as seen for `JumpHoldS` s (camsafe reach = unlimited).
4. **Where it acts.** With camsafe on, `sReach2` (used by `FarFromViewers`, `FarFromCamera`, `UnsFar`) becomes
   max(old, camsafe reach)^2; `UcOnScreen` adds the hold; `TownieUnseen`'s camera half judges both rules
   (`CsTownieCamera`) and returns camsafe's. Every change only makes more things "seen", so camsafe can only make a
   shortcut act less often, never in a spot the old test refused.
5. **Shared API and the alibi rule.** `CsDecide(sim, hhOptIn, obj, spots...)`: the decision of one shortcut, made once at
   its start (NPC: TownieUnseen, obj off screen, spots away from every viewer; household, only with the fix's own
   `...hhunseen` opt-in: the Sim and obj off screen, Sim and spots away from the camera; never the selected Sim).
   `CsSnap(sim, end)`: for a shortcut that ends later than it starts; true = the Sim or its end spot came into view, so
   the caller finishes into a valid end state at once (the game's own result, or the step behind a fade) and never shows
   a half state. elevskip uses it: an armed set-through whose Sim came into view goes through behind a fade.
   elevskip and fridgeff call `CsDecide` when camsafe is on (the same test they had, on the shared code path).
6. **Household scope** is unchanged: NPCs by TownieUnseen's rules, household Sims only through each fix's explicit
   `...hhunseen` opt-in, the selected Sim never unseen.
7. **camsafe off** = every test exactly as before (`sReach2` untouched, `UcOnScreen` without the hold, TownieUnseen's old
   branch). `camsafelog` (camsafe off) samples the camera and counts what camsafe would say, behaviour unchanged: the
   A/B off arm.
8. **Late-seen audit** (camsafe or camsafelog): each TownieUnseen verdict "unseen" (rule in force), and each "old unseen,
   camsafe seen", puts its Sim on a watch list (128 max); every 250 ms the list is checked with `World.IsObjectOnScreen`,
   and a Sim on screen within `AuditS` s counts ([4893] / [4895]). [4893] is the direct safety measure: an unseen verdict
   whose Sim was on screen moments later.

## 3. Tunables (Instantiator, conservative defaults)

| Tunable | Default | Meaning |
|---|---|---|
| `kCamSafe` / `kCamSafeLog` | false / false | switches `camsafe` / `camsafelog` |
| `kCamSafeMarginPct` | 25 | reach grown by this many percent (screen-border margin) |
| `kCamSafeLeadS` | 2 | prediction: seconds of camera travel at the smoothed speed |
| `kCamSafeSpeedMax` | 150 | speed cap, m/s |
| `kCamSafeSpeedTauMs` | 1000 | smoothed speed decay, real ms |
| `kCamSafeHoldS` | 5 | recently-seen hold (objects, camera path, largest reach), real s |
| `kCamSafeJumpM` | 25 | a sample-to-sample change above this is a jump, m |
| `kCamSafeJumpHoldS` | 3 | everything seen after a jump, real s |
| `kCamSafeAuditS` | 3 | late-seen audit window, real s |

All times are real milliseconds (`Environment.TickCount`), so ultra speed does not shorten the holds.

## 4. Counters 4880-4899

Registry row in docs/registry.md. Read with `CamSafeCheck result` (repro `camsafe-check`; `run` evaluates CamSafeCore on a
scripted camera, read-only).

## 5. Repros

- `camsafe-check`: the rules, off / on (pure).
- `camsafe-camera`: the forced case: the off-screen shortcuts on, `camera_jumps` between the 8 most populated lots (3 s
  dwell) and `camera_pan` sweeps, first under `camsafelog` (old rule, counted), then under `camsafe`.

## 6. Measurement and proof status

Runs s605 / s606 (2026-11-26): combo 8f8c7d21 + camsafe, merged asi 0.36.305-m1124d with its own ini as the base, s598
setup (heap 512, route storm 30 min, seed 156, new game). Off-screen shortcuts on in every arm (elevskip, elevskiphh,
elevskiphhunseen, fridgeff, fridgeffunseen, rhskipunseen, rhskiphhunseen, bookskipunseen, bookskiphhunseen, gymtwin,
servingsharehhunseen, plus the default trip shortcuts). Camera still for the first 15 min, then the forced case to the
end: `camera_jumps` between the 6 most populated lots (3 s dwell) for 90 s, `camera_pan` 60 s, still 20 s, repeated.
Counters read every 2 min (`CamSafeCheck result`). Off arm = `camsafelog` (old rule in force, camsafe counted); on arm =
`camsafe`. Every arm: "Bin pair restored", errors 0.

| World | arm (run) | TownieUnseen asks | old unseen | camsafe would veto | of which jump / hold / margin | watched | **on screen within 3 s after an in-force unseen verdict** | vetoed, then on screen within 3 s |
|---|---|---|---|---|---|---|---|---|
| Sunset Valley | old (s605a) | 169,835 | 146,175 | 45,730 (31 %) | 40,164 / 121 / 5,445 | 25,176 | **152** (3 while still) | (counted in the left column) |
| Sunset Valley | camsafe (s606b) | 173,454 | 147,226 | 46,899 (32 %) | 41,959 / 107 / 4,833 | 25,227 | **8** (3 while still) | 146 |
| Sims University | old (s606c) | 155,553 | 138,430 | 43,759 (32 %) | 33,908 / 25 / 9,826 | 23,807 | **116** (2 while still) | (counted in the left column) |
| Sims University | camsafe (s605d) | 156,736 | 140,061 | 42,284 (30 %) | 33,936 / 12 / 8,336 | 23,084 | **0** | 103 |

- **How often the old test acted in a risky spot:** in the old arms 31-32 % of "unseen" verdicts came while camsafe said
  seen (mostly within 3 s of a camera jump), and 152 / 116 of the watched Sims were on screen within 3 s of being judged
  unseen. With camsafe the same count is 8 / 0 (-95 % / -100 %), while 146 / 103 Sims that camsafe kept "seen" did
  appear on screen within 3 s (its vetoes were right).
- **Cost:** with a still camera camsafe keeps 97 % (SV) / 91 % (SU) of the old unseen verdicts (static first 11 min:
  63,657 of 65,835; 60,000 of 65,894). Over the whole run with the camera jumping every 3 s it keeps 68-70 %.
- **Snaps 0:** elevskip never armed in these storms (no elevator rides in SV / SU storms: elevskip set through 0 in every
  arm), so `CsSnap` is built and unit-checked but not exercised in game. bookskipunseen acted 3 times, rhskipunseen 0.
- **Remaining gap (inferred):** SV camsafe still had 8 late-seen Sims, 3 of them with a still camera. The reach test is
  a sphere around the camera, not the view frustum: with a low camera looking across town, a Sim far beyond the reach can
  be on screen. A frustum test (docs/228 §11.2 does it natively) would close this; not built.
- s605b (ran into another agent's game) and s605c (loaded with camsafe on as well as camsafelog, source not found) are
  not used; s606 reran them. s604 (pre-merge asi) is not used.

Proof status: **forced case proven in game** (late-seen 268 -> 8 over two worlds, errors 0, Bin pair restored every
arm); the CsSnap path is unexercised in game; FPS / hitch effect not measured (camsafe only removes shortcuts).
