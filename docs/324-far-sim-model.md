# 324. Far-Sim model: calculated state for NPC Sims far from the household (2026-11-22)

Status: **design only.** Nothing built, nothing run. Rodney's idea (approved for design 2026-11-22). Branch
`feature/far-sim-model-1122`. Prior art: docs/326 (branch `feature/unbounded-sim-research-1122`); op costs for the
spread queue: docs/325 (inst-probe, measured 2026-11-22, branch `feature/inst-probe-1122`). Rodney's decisions of 2026-11-22 are in §11 and applied in every
section below.

Inputs: docs/322 (cost by distance band, measured), docs/320 (large worlds, §5 fog of war, §7 region streaming),
docs/321 (unbounded sweep, lane A lifecycle hub), docs/243 (unseen-lots thread study: tick skipping measured dead,
park at the boundary), docs/326 (prior art: ideas #1-#12, pitfalls P1-P10, tier ladder), docs/588 (EA story
progression), docs/586 (`unseenoutfitdefer`), docs/582 §8 (`residentspawnslot`), docs/512 and `AgeUpDupInst.cs`
(#553), docs/612 (duplicate Sims), docs/146/147 (spread pattern). EA script DLLs decompiled read-only in
`game-copy/decompiled/`, EA's Motives tuning XML and the NRaas source as copied under `game-copy/` (read only; nothing
quoted). Tags: **[verified]** read in the decompile, the tuning XML or our code; **[measured]** run data named;
**[community]**; **[inferred]** reasoning, still to confirm. Proposed docs/51 entries: §12.

## 0. Short answer

- docs/322: beyond 150 m from the viewers, NPCs own 85-89% of NPC sim-thread time (BP 250, IP 251, SV 195 ms/s
  attributed) and a Sim costs ~4 ms/s wherever it is [measured]. The unseen fallbacks cut per decision and do not make
  a far Sim cheaper per second; docs/243 measured tick skipping as ~0 because cost is per decision [measured]. The lever
  left for far Sims is **to make no decisions for them at all**.
- A far NPC (tier T3) is not simulated. Its state (needs, place, activity) is a lazy pure function of its
  SimDescription, the game time and a versioned seed, computed only when something reads it or when it is promoted
  ("snap"). Then EA runs it live again. One owner per Sim at any time: EA or the model.
- First target: T3 with the Sim object kept, hidden and inert (stage 2a). The static check (§5) finds this safe for
  references and saves; it saves CPU, not memory. T4 (SimDescription only, stage 2b) is what a 4x or streamed world
  needs for memory. It is opt-in and default off even with the model on. Its five risks (§5.6) each get a guard. The
  largest is NRaas Overwatch `RecoverMissingSims`, which rebuilds missing residents every day; our runtime precedence
  stops it from touching T4 Sims (§5.6).
- Far Sims are not frozen: NPC-NPC relationships, skills and career performance drift by seed, bounded, and never
  touch a relationship with a household Sim (§2.7). The model also proposes visits, with a distance-decay weight, and
  the real game spawns and runs them (§3.9).
- The model stays active with NRaas StoryProgression installed and takes precedence at runtime where they conflict
  (§4).
- Household Sims, the selected Sim, Sims of unknown scope and any Sim about to touch the household are always live.
  Default off. Nothing goes into the .sims3. No Sim is ever culled.
- Ceiling gain: most of the 195-251 ms/s above. Realised share after exclusions, residuals and churn: 50-80%
  [inferred] (§7). On a 4x world NPC cost follows the Sims near the viewers, not the world population (§8).

## 1. Goal, rules, tiers

### 1.1 Goal

Cut the sim-thread cost of NPC Sims the player cannot see or reach. The player's Sims must see no difference. NPC cost
must not grow with world size (AGENTS.md "Build for unbounded worlds").

### 1.2 Hard rules (each one is a code-level check)

1. **Household untouched.** Never demoted below T1: every active-household Sim, the selected Sim, and any Sim of
   unknown scope (no fresh household table, id not resolvable, mid-travel, mid-household-change). Use the shared
   predicates (`Fixes.HouseholdExempt`, `mods/ts3fixes/src/PlayerSims.cs`; native `HhExempt`). `[Experimental]
   HouseholdToo=1` does **not** apply: parking is a skip and snap is a teleport / fake, both outside HouseholdToo
   (docs/271).
2. **Always live** (§3.6): any Sim about to interact with the household is promoted before it happens. Relationships
   with household Sims change only through real interactions.
3. **Default off.** ts3fixes `farsim` (master) + per-tier sub-switches (`farsimt2`, `farsimt3`, `farsimt4`); native
   `[FarSim] Enabled=0`. `farsimt4` stays off even when `farsim` is on (decision 5).
4. **Sidecar only, deltas only.** Nothing new in the .sims3. Before every save the model flushes (T3: computed state
   applied to the real object, so EA writes EA state). `<Save>.farsim.json` holds only the generator version and
   pending events (§2.5), with a size cap and pruning. A missing or mismatched sidecar is ignored and logged ("clean
   up our damage on load").
5. **Big life events stay with story progression** (births, deaths, marriages, moves, age-ups, promotions): EA story
   progression or NRaas StoryProgression decide them (§4). The model never invents a life event; its drift (§2.7)
   stays below every life-event threshold. Where NRaas StoryProgression and the model conflict on a model-owned Sim,
   ours takes precedence at runtime (§4, decision 4).
6. **Never cull.** No Sim, household or description is ever deleted to save cost (docs/326 P5; TS4 culling lost
   favourites [community]). T4 removes only a Sim *object*, and only through EA's own paths (§5.6).
7. **Clean up on load.** A load with the switch off, or after a crash while Sims were parked, leaves only EA state:
   T3's hidden and inert state is runtime-only (§5.2).

### 1.3 Tier ladder

Base: docs/326 §4. Viewer points = camera target, camera position, every household Sim (as `FarCompute` and docs/322).
Radii come from the ini, in metres from the viewers, never as fractions of the world.

| Tier | Who | Enter | Leave | What runs |
|---|---|---|---|---|
| **H** | household, selected, unknown scope | always | never | full EA (hard rule) |
| **T1 Full** | NPC < 150 m; on a household or visited lot; interacting with a household Sim; target of an open call, invite or opportunity; observed (§3.7) | at once when any trigger holds | to T2 when no trigger holds for 30 sim-min and d > 200 m (periodic pass) | full EA + today's unseen fallbacks |
| **T2 Reduced** | NPC 150-400 m, or nearer but unseen for long | from T1 by the rule above; from T3 by the pre-expand ring (§6.3) | to T3 beyond 450 m, or in a rabbit hole / unseen lot for 1 sim-hour, at a safe point (§3.2) | EA at a coarse cadence: Sim task and autonomy at 1/N, no idle animation, relaxed routing |
| **T3 Calculated** (stage 2a) | NPC > 450 m, or parked in a rabbit hole / unseen lot | from T2 at a safe point, partial effects credited | promote by time-to-view (§6.2), due visit, travel, observation; snap at promotion | object kept, hidden and inert; no tasks; state = f(seed, version, elapsed) through the facade (§5.4) |
| **T4 Data only** (stage 2b) | T3 Sims inside a rabbit hole or indoors on an unseen lot first, then any T3 Sim | from T3 when memory matters (4x / streamed world) | re-instantiate through the queue before the predicted view time | `SimDescription` only, same facade |

**Relationship weight** (docs/326 #6): a Sim with a family, romance, best-friend, coworker or classmate tie to a
household Sim, or a pending opportunity or call, stays one tier higher than its distance says (never below T2 while
the tie holds).

**Check against docs/322 bands [measured shares, inferred fit]:**

| docs/322 band | BP share | IP share | SV share | Tier |
|---|---:|---:|---:|---|
| < 50 m, 50-150 m | 15% | 11% | 14% | T1 (unchanged) |
| 150-400 m | 20% | 11% | **73%** | T2 (T1 -> T2 past 200 m) |
| 400-1000 m | **64%** | **77%** | 12% | T3 (past 450 m) |
| >= 1000 m | 2% | 0% | 0% | T3 / T4 |

- The T3 edge at 450 m sits just inside docs/322's 400 m edge, so almost all of BP's and IP's dominant band becomes
  T3. The 400-450 m slice stays T2 [inferred: small, the band is 600 m wide].
- SV's cost sits in 150-400 m. So in SV, T2 is the tier that matters, and T2 is the riskiest gain. docs/322 found
  per-Sim cost flat under the existing throttles, and docs/243 found cadence skips ~0 [measured]. **Decision R3
  (§11):** T2 may gain nothing, and that is accepted. T2 is the hand-off tier on the way to T3 (safe points, partial
  credit, pre-expand), so it ships with T3 even without its own gain: household-exempt and default off like the rest.
- **Decision 1:** radii 150 / 200 / 450 m. The T3 edge is then set **per world** (ini key per world name, default
  450 m) to ~300 m where shadow calibration (§10 P1) shows the model is close enough at that distance. SV is the first
  candidate.
- No re-cut of the band edges is needed: the docs/322 probe can be rerun with `Bands=150,200,450,1000` to read T1 /
  T2 / T3 shares directly.

## 2. The state model (T3 and T4)

### 2.1 Inputs, all derivable

| Input | Source | Saved by EA? |
|---|---|---|
| Sim id | `SimDescription.SimDescriptionId` | yes |
| Day index | `SimClock` elapsed days | yes |
| World / region id | world name; region tile id on a streamed world (docs/320 §7.2) | yes / derivable |
| Age, species, traits | `SimDescription` | yes |
| Occupation and level | `SimDescription.Occupation` (`Career`, `School : Career`) | yes |
| Home lot | `SimDescription.LotHome` (null for homeless townies) | yes |
| Snap point (t0, motives at t0, lot at t0) | runtime: captured at demotion | no: rebuilt at load (§2.6) |
| Generator version, pending events | sidecar (§2.5) | no |

**Versioned seed** (docs/326 #8, P9): `S(day) = H_v(SimDescriptionId, dayIndex, worldId, logRevision)` with a fixed
64-bit mix, and `v` = the generator version. No `System.Random` state is carried across days. Same save + same day +
same version = same plan on every machine and after every reload. A new generator version applies only to days after
the version change (the sidecar records the day it changed); a Sim snapped under the old version keeps its applied
state.

**Lazy** (docs/326 #1, P3): nothing runs per tick or per day for a T3 Sim. The plan for day d is built on first read
(snap, facade read) and kept in a small per-Sim cache keyed by `(day, version, logRevision)`.

### 2.2 Needs (motives)

EA's motive update is `Motive.UpdateMotiveWithDecay` (from `Motive.Update` / `Motives.UpdateMotives`); the tuning
comes from the `Motives` XML (`MotiveTuning.Load`) [verified]. Per commodity there is a `DecayType`
(`DecayFromAutoSatisfy`, `DecayToZero`, `FixedDecay`, `NoDecay`, `VariableDecayByMoodlet`) and a `decayValue`
[verified, `MotiveTuning`, `DecayType`]. Motives run from -100 to 100.

Human values, any age unless noted, from the copy of the tuning XML [verified, file read]. The live game may load an
EP override of the same resource, so the model reads `MotiveTuning` at runtime and never uses these numbers directly.

| Motive | DecayType | Rate per hour (value band -> decay) |
|---|---|---|
| Hunger | VariableDecayByMoodlet | -35..100: 11.25; -60..-36: 8; -100..-61: 0.79 (toddler, baby faster) |
| Bladder | VariableDecayByMoodlet | -60..100: 13.33 (toddler, elder 16; baby 17.8); -90..-61: 14.5; -100..-91: 9 |
| Energy | human special case | `EnergyFromFullToSleepy / (kEnergyHoursInDay - EnergyFromFullToSleepy / EnergyGainRate)`. Full to sleepy = max minus the top of the Sleepy trigger = 100 - (-60) = 160; `kEnergyHoursInDay` = 23; `EnergyGainRate` comes from the bed-sleep interaction tuning (`Motives.kBedSleepTuningSignature`) [verified, `Motive.UpdateMotiveWithDecay`, `Motives.EnergyFromFullToSleepy`] |
| Hygiene | FixedDecay | (max - min) / 42 h = ~4.76 |
| Social | FixedDecay | 200 / 72 h = ~2.78 (Loner 168 h; toddler and baby by band) |
| Fun | DecayToZero | toward 0 (`UpdateDecayToZero`, `UpdateFun`) |

Multipliers EA applies [verified, `Motive` tunables and `UpdateMotiveWithDecay`]:
- while sleeping: bladder x0.1667 (`kBladderMultiplierWhileSleeping`), hunger x0.25, fun x0.25;
- `GetCommodityDecayMultiplier` (traits, occult);
- posture deltas (`GetPostureMotiveDecayUpdate`);
- buff overrides (potions, Unnaturally Clean, pregnancy for pets, werewolf and full-moon energy).

Model formula per commodity between plan-block edges (closed form, no stepping):
- `VariableDecayByMoodlet`: piecewise linear. Integrate at the band's rate until the value crosses the band edge,
  then continue at the next band's rate. There are 4-6 bands, so a snap costs O(bands).
- `FixedDecay`: `v(t) = v0 - (Max - Min) * m * dt / decayValue`, with `dt / decayValue` capped at 1 as EA does.
- Energy: linear at EA's rate while awake; gain at `EnergyGainRate` in a sleep block.
- `DecayToZero`: linear toward 0 at the tuning rate, clamped at 0.
- `m` = the sleep multiplier inside sleep blocks times `GetCommodityDecayMultiplier` (traits and occult from the
  description). Posture and transient buffs are ignored [inferred: a few percent of a day's decay; shadow mode
  measures the error].
- Satisfaction events inside the plan (§2.3), each with seeded values:
  - a meal block sets hunger to a value in [60, 90];
  - a hygiene block after waking sets hygiene to [70, 95];
  - a bladder event every ~6-7 awake hours resets bladder to [80, 100];
  - social rises by a seeded amount at a work, school or community block;
  - fun rises in a leisure block.
- **Snap clamp:** applied motives are clamped to >= -40. An NPC never snaps into distress, which would start an
  expensive autonomy chain the moment it goes live and could fire `Motive.MotiveDistress` / social-worker paths
  (decision 2, Rodney 2026-11-22). EA itself starts a new townie with fresh `initialMin..initialMax` values, so NPC motives
  at instantiation are already not continuous [verified, `MotiveTuning` initial values].

These are allowed fakes for Sims the player does not control (unseen-Sim fallback rule). They never touch household
Sims.

### 2.3 Day plan

Blocks for day d (lazy, §2.1):

1. **Sleep:** wake time `W` = `Occupation.WakeupTime` [verified]:
   - with a carpool: pickup time - 1 h;
   - with no occupation: `Sim.kDefaultWakeupTime` = 6:00.

   EA's bed-time window is `kTypicalNumHoursToSleep` = 8 h before W (`Sim.IsTimeForBed`,
   `Autonomy.ConsiderSleepingEvenWhenNotSleepy` with its -60 energy threshold) [verified]. Model: sleep from
   `W - 8 h - j` to `W`, with j seeded 0-90 min.
2. **Work / school:** `Occupation.IsWorkHour(time)` / `Career.ShouldBeAtWork` over `Occupation.DaysOfWeekToWork`, plus
   `StartTime`, `DayLength` and `FinishTime` of `CurLevel` [verified, `Career`, `Occupation`, `CareerLevel`]. Place =
   the career's rabbit hole or the school; leave at `StartTime - 1 h` (`Career.kHoursBeforeStartCarpoolArrives`)
   [verified]. Overrides (`LawEnforcement`, `Medical`, active careers) are called at runtime; the model calls EA's
   method and does not copy the rule. Work itself runs as EA's own work interaction (§3.4), so pay and performance
   stay EA's.
3. **Meals:** breakfast after waking, lunch at work, school or home, dinner 17:00-20:00, each seeded +-60 min.
4. **Free time:** a seeded choice per free block (>= 2 h) among:
   - home;
   - an open community lot of a venue type weighted by traits (e.g. Athletic -> gym or park, Bookworm -> library,
     Party Animal -> bar in the evening) [inferred weights; shadow mode tunes them];
   - a visit to an **NPC** friend's home lot (relationship read from `Relationship`, never written).

   Lot candidates come from a per-region open-lot list refreshed by the lot load / unload hub (§6.5), never from a
   full-world scan per Sim.
5. **Homeless townies and service NPCs** (no `LotHome`): no plan position. They are "off world", like EA's own
   un-instantiated townies, and come live only through EA's own spawn paths (§3.5).

### 2.4 Position and activity at time t

- `Place(t)` = the block's lot (home, rabbit hole, community lot, friend's lot), or "in transit" for a 20-40 min gap
  at block edges.
- `Activity(t)` = sleep, work, eat, leisure, idle or transit.
- Snap maps them to a real placement and a real EA interaction (§3.3).

### 2.5 Event log and sidecar

External live-world events change a far Sim's plan:
- fire or burglary on its home lot;
- a phone call or invite from the household (these promote it anyway, §3.6);
- a live NPC inviting it (NPC-NPC situations);
- a future household arrangement (date, group outing).

Story progression actions (move, marriage, new job) need no log entry: the plan is rebuilt from the changed
description.

Entry: `{simId, kind, gameTime, lotId, otherId}`. Each entry bumps that Sim's `logRevision`, so its seeded plan
changes deterministically from that point. The log is kept in memory. At save, `<Save>.farsim.json` receives the
generator version history and only the entries still pending (a future visit or date). Cap: 4096 entries; entries
older than 7 sim-days are pruned [inferred size: a handful per save].

### 2.6 After a load

EA restores every resident Sim object as today: EA has no rebuild pass for missing residents, the objects come back
from the save (§5.3) [verified]. Each eligible far Sim is demoted to T3 at the next periodic pass, with t0 = load time
and its loaded motives as v0. So no snap-point data has to be saved.

### 2.7 Seeded drift: relationships, skills, career performance (decision 3)

A model-owned Sim's life does not freeze. Its NPC-NPC relationships, skills and (T4 only) career performance drift
by seed. The drift is computed lazily for the elapsed days and applied at snap or at the save flush, the single
apply point (docs/326 P8).

Common rules:
- **Deterministic and versioned:** each delta comes from `H_v(SimDescriptionId, otherId or skillId, dayIndex,
  worldId, logRevision)`, so the same save and day give the same drift on every machine. A generator version change
  applies only to later days (§2.1).
- **Driven by the plan, not invented:** a delta needs a plan reason. Two Sims share a block (coworkers in one rabbit
  hole, a visit to an NPC friend, the same community lot in the same block), or a leisure block maps to a skill.
- **Bounded per day and per far stretch** (a far stretch runs from demotion to the next promotion; caps are ini
  values).
- **Applied through EA's own APIs** at snap (relationship LTR setter, `SkillManager` add-points path, career
  performance setter), so EA's own clamps, level-ups and listeners run. Exact method names are confirmed in P0
  [inferred: EA's public setters; to verify].
- **Never** touches a relationship in which either side is a household Sim, the selected Sim or a Sim of unknown
  scope. The pair filter is the shared household predicate (§1.2), checked for both ids.

| Kind | Delta per plan reason | Bounds | Stays below |
|---|---|---|---|
| NPC-NPC relationship (LTR) | seeded in [-1, +2] per shared block, sign biased by the pair's current LTR and trait compatibility | +-3 per pair per day, +-15 per far stretch | the romance, steady, engaged, married and enemy state changes: drift moves LTR only inside the pair's current relationship state; state changes stay with story progression |
| Skill points | seeded share (0.25-0.5) of EA's own gain rate for that skill x leisure hours | +1 skill level per far stretch; never a skill the Sim has not started | — (a level-up fires EA's own NPC level-up path at snap) |
| Career performance (T4 only) | seeded step toward EA's level average, sign from mood at snap | +-10 points per sim-day | promotion and demotion: those stay with EA's own work-day evaluation |

In T3, work blocks run EA's own work interaction (§3.4). Career performance therefore stays real there and gets no
drift, so nothing is counted twice. In T4 there is no object at work, so the seeded career drift stands in for it.

Shadow calibration (§10 P1) compares the drift distributions with what EA's full simulation gives over the same days
(LTR change per pair-day, skill points per Sim-day, performance per work day) and tunes the shares and bounds.

## 3. Ownership, hand-off and snap rules

### 3.1 One owner (docs/326 #2, P6)

Every NPC has exactly one owner: **EA** (H, T1, T2) or **the model** (T3, T4). A model-owned Sim runs no Sim task, no
autonomy, no motive update and no locomotion. The ownership flag lives in one place (a native table keyed by Sim id,
mirrored in ts3fixes), and every hook checks it. EA alarms that target a model-owned Sim are either deferred to the
next promotion or answered through the facade (§5.4). They never run half on a parked Sim.

### 3.2 Demotion (EA -> model) at a safe point

A Sim is demoted when it is eligible (§3.5) and has met the tier's leave rule (§1.3). Demotion happens only at a safe
point:
- no running interaction with another live Sim;
- no situation membership (party, date, outing, `Situation` list);
- no role (`RoleManager` service Sims, mail carrier, maid, repo, paparazzo, firefighter);
- no carried object or child; not inside a portal or on a route through one; not in a vehicle that carries others;
- the current interaction has finished, or only autonomous low-priority interactions are queued (cancelled through
  EA's own queue cancel, never a reset);
- never on a household lot, never visible, never while saving, travelling, in Edit Town, build/buy or CAS.

**Partial actions are credited:** when a demotion happens right after an interaction ended or was cancelled, its EA
effects already happened (EA ran them). The capture takes the post-interaction motives as v0, and the plan block
starts from the interaction's kind. Example: a Sim that ate before demotion gets no extra meal in the first block. No
effect is applied twice, and none is lost.

On demotion: capture `(t0, v0, lot, activity)`, set the owner, make the object inert (§5.2).

### 3.3 Snap and promotion (model -> EA)

Triggers:
- time-to-view below the op cost plus margin (§6.2), or the Sim enters the pre-expand ring;
- a household Sim's route destination or travel target is the Sim's current plan lot;
- a scheduled visit, call or meeting is due (prebuilt ahead, §6.3);
- an EA or mod call needs the Sim (on-demand guard, §5.5);
- observation (§3.7);
- the switch is turned off;
- before a save (flush).

What a snap applies:
- motives from §2.2;
- place from §2.4, using these placements:
  - at home: a routable spot on the home lot through EA's own resident spawn path (the door slot logic that
    `residentspawnslot` fixes, docs/582 §8), never inside a reserved object's footprint;
  - at work or school: inside the rabbit hole through EA's own hide (`RabbitHole.AnimateEnterRabbitHole` precedent,
    §5.2); the Sim leaves by EA's own exit;
  - community or friend's lot: at the lot's off-lot arrival point, out of view, then routed in;
  - in transit: the nearest out-of-view point on the line between the two block lots, then routed on.
- Activity: push the EA interaction the block implies at non-user priority (sleep -> bed on the home lot; eat ->
  autonomy with hunger low), or push nothing and let autonomy pick.

A Sim is never snapped inside the camera frustum (docs/326 P1). A late Sim that must be live while already in view is
placed at the nearest out-of-view point and routes in (§6.4).

### 3.4 Work blocks

At a T3 Sim's work start, EA's global work-day alarm `Career.RegularWorkDayGoToWorkHandle` pushes the work interaction
only when the Sim exists (it checks `OwnerDescription.CreatedSim`) [verified]. Pay comes from `Occupation.FinishWorking`,
called at the end of the work interaction (`Sim.cs` caller) [verified].

Design: the push to a model-owned Sim is the hand-off. The Sim is promoted to EA ownership *inside* its rabbit hole:
an unseen teleport with the route skipped, NPC only. EA runs its own work interaction there. EA rabbit-hole Sims sleep
inside one interaction, so this costs little [verified: rabbit-hole hide; inferred: cost]. The Sim is demoted again
when it leaves, if still far. Career pay, performance and work-day events therefore stay EA's, and the model never
writes career state.

### 3.5 Eligibility for T3

NPC only (§1.2). Not eligible:
- in labour, dying, or a ghost in transition;
- in an active age-up (`AgingState` in progress; `AgeUpDupInst.cs` and docs/512 show a second instantiate during a
  deferred age-up already causes bugs);
- a pending event within `PrebuildHours`;
- a relationship tie (stays one tier higher, §1.3);
- a pet of the household, a service or role Sim;
- a `Sim.Placeholder` child (EA's own data-only state, §5.1).

T4 adds three conditions:
- not pregnant (§5.3);
- an inventory that holds only the item kinds `AddInitialObjects` gives (§5.6);
- not in a group NRaas skips (docs/318): boarding-school students, adults away while a parents-leaving-town
  situation runs, early vacation returners, SimPort travellers. Overwatch's instantiation check destroys such a Sim
  right after it is built, so the queue never demotes them to T4 or rebuilds them; they stay T3 or EA's own.

### 3.6 Always live

These promote the Sim to T1 **before** they happen:
- a household Sim starts any interaction with it (social, phone call, text, invite, gift, fight, romance), or picks
  it in a UI picker;
- EA schedules it as a visitor to a household lot (visit situation, party guest list, `VisitLot` / `GoToLot` with a
  household lot as target), or as a service for the household (`Services` / `RoleManagerTask` picks);
- a household phone call or invite targets it (`Phone.CanCallSim`, `Phone.GetSimToLot`, which instantiates a missing
  receiver [verified]);
- it is in a situation with a household Sim, or the household travels to its current lot.

Relationship changes with household Sims therefore come only from real EA interactions on a live Sim. NPC-NPC
relationships, skills and (T4) career performance drift by seed while the model owns the Sim (§2.7, decision 3).

### 3.7 Observation promotes (docs/326 #7)

Decision 8: "looking" at a Sim promotes it in exactly these cases:
- the Sim info panel of that Sim;
- the relationship panel (the Sims listed in it);
- the phone contact list (the contacts shown);
- selecting the Sim on the map.

Hover and map thumbnails do **not** count; nor does simply opening the map view. A promotion from a UI view goes
through the queue with TTV = 0. A list (relationship panel, contacts) promotes its entries in list order within the
queue budget. Hook points are found in P0.

### 3.8 Arrivals and ambient roles (docs/326 #11, #12)

| Arrival kind | Rule |
|---|---|
| Visitor to the household lot | walk in from the lot edge (prebuilt live >= 1 sim-hour before) |
| Party / event guest | walk in (EA's guest arrival) |
| Coworker / classmate arriving with a household Sim | leaves the rabbit hole with them (EA) |
| Service NPC (maid, mail, repo) | EA's own spawn, unchanged |
| NPC arriving on a community lot the household is on | walk in from the lot edge |
| NPC on a lot the camera jumps to (map travel) | may snap in while unseen (behind the loading screen or out of frustum) |

Snap targets are route-checked: a snapped Sim must have a route out of its spot (EA `IsPointInLotSafelyRoutable`
style check), else the next candidate spot is used.

Ambient walk-by roles (joggers, idle-service roles) fill only near viewers, by time of day, and **only roles, never
named Sims**. EA's own `RoleManagerTask` / `CarNpcManager` keep choosing who fills them [verified as EA's spawn path,
§5.1]. The model only says how many roles a far zone needs (zero).

### 3.9 Visitors: the model proposes visits (decision 9)

**How EA makes a visit** [verified]: a visit is not scheduled. An NPC's autonomy (or a phone call) pushes `VisitLot`
to another household's lot (`Phone` pushes `VisitLot.Singleton` at the callee's `LotHome`). When the Sim arrives,
`Sim.LotChanged` sees a lot that is not its home and has a household, and calls `VisitSituation.Create(sim, lot)`.
That runs the doorbell, greeting, socializing and leave logic. `VisitSituation.IsTooLateToVisit` blocks the
unacceptable night hours (`kStartUnacceptableVisitTime` .. `kEndUnacceptableVisitTime`).

A model-owned Sim runs no autonomy, so without a proposal far friends would never visit. The model therefore
**proposes visits** the way EA's autonomy would choose them, and the real game spawns and runs them:

1. **Candidates**, once per household lot per sim-day, lazily on first need: model-owned NPCs (T3 / T4) with a
   relationship to a household Sim. Sims in T1 / T2 still visit through EA's own autonomy, so nothing is counted
   twice.
2. **Weight** per candidate:
   `w = base x rel(LTR, relationship kind) x decay(T) x ea_ok`
   - `rel`: rises with the best LTR to any household Sim; family, best friend and romance kinds get higher factors
     [inferred factors; tuned in P1].
   - `decay(T) = 1 / (1 + (T / T0)^2)`: falls with travel time T from the Sim's plan lot at the visit hour to the
     household lot (route distance / the Sim's travel speed: car, bike or walk from its description; straight-line
     distance x 1.3 when no route length is known). T0 = ~15 sim-min [inferred]. People farther away visit less
     often, but never zero, so Sims from far locations still visit.
   - `ea_ok`: EA's own conditions, called, not copied: not `IsTooLateToVisit` at the arrival time; not at work or
     school then (`Occupation.IsWorkHour`); the household lot accepts visitors (lot and situation checks EA's visit
     path uses); not already visiting.
   - `base`: calibrated so that the total visit rate of a far friend set matches what EA's autonomy gives the same
     Sims when they run live (P1 shadow).
3. **Draw:** a seeded draw per day picks zero or more visits and their hour inside the visitor's free blocks. Each
   becomes an event-log entry (§2.5), so it is deterministic and survives a save if pending.
4. **Run:** the visitor is prebuilt live `PrebuildHours` before (§6.3), out of view. At the visit hour the model
   pushes EA's own `VisitLot` interaction to the household lot at non-user priority. From then on EA owns everything:
   the route in (walk-in, §3.8), `VisitSituation`, greeting, leave. The model never fakes a visitor.

Cars driving through are EA's ambient traffic, unchanged.

### 3.10 Food, bills and objects on their lot

- Food: the model consumes no fridge food and leaves no dishes or leftovers (they "ate out") [inferred: NPC fridge
  stock is not visible to the player in normal play].
- Money: no spending, and no income beyond EA's own career pay (§3.4). NPC household funds stay EA's.
- Objects: the model never reserves, uses, breaks or dirties an object. Snap picks a free spot and lets EA's
  interaction pick objects; when one is in use, autonomy picks something else.
- Lot events (fire, burglary, repo) on a far lot are events (§2.5). EA's lot systems run regardless. Residents are
  promoted only when the household is involved or the camera comes.

## 4. EA story progression and NRaas StoryProgression

- **EA:** `StoryProgressionService` runs once a day at 1 AM (once a minute in accelerated mode) and does 4-10 rounds
  of `TryOneAction` over demographics built from every lot, household and Sim (`Demographics.Collect`). It targets NPC
  households only (`SelectRandom*` skips the active and travel households) [verified, docs/588 §1]. It works on
  descriptions and households; actions that need a body go through EA's instantiate paths.
  - In T3 every Sim keeps its object, so story progression sees the same world.
  - Its reads of far Sims (location, money, job, `LotCurrent` in demographics) must come through the facade (§5.4,
    docs/326 P7). With T3's physical facade they read the plan lot.
  - Any action that pushes an interaction on, moves or instantiates a model-owned Sim goes through the on-demand guard
    (§5.5) and promotes it first. The plan is rebuilt from the changed description afterwards.
  - One apply point (docs/326 P8): model state is applied at snap only, never in a second daily pass next to 1 AM.
- **NRaas StoryProgression** [verified, NRaas source under `game-copy/`]:
  - Its instantiation helpers (`NRaas/Helpers/Instantiation`: `Perform`, `PerformOffLot`, `EnsureInstantiate`;
    `ManagerSim.Instantiate`) are called on move-in (`ManagerHousehold`), for situations (`ManagerSituation`), job
    finding, career pushes and parties.
  - It dereferences `CreatedSim` 343 times with 93 null checks, many of the rest inside try/catch (heuristic count).
  - In T3 these see a live object, so they work as today, through the on-demand guard. In T4 they would instantiate
    behind our queue's back unless guarded.
  - **Decision 4: we override it and stay active.** The model runs with NRaas StoryProgression installed, and where
    the two conflict on a model-owned Sim, ours takes precedence at runtime:
    - *Its instantiate path:* our guard hooks NRaas's helpers by method name at load (`Instantiation.Perform`,
      `PerformOffLot`, `EnsureInstantiate`, `ManagerSim.Instantiate`). Every call for a model-owned description goes
      through our one-instance guard (§5.5): it returns the existing Sim, or builds through our queue at TTV 0, in
      age-up order. They all end in EA's `SimDescription.Instantiate` choke point as well, which our guard also
      holds.
    - *Its actions on a model-owned Sim:* a scenario that pushes an interaction, moves the Sim or starts a situation
      promotes it first through the on-demand guard. The scenario then runs on a real Sim (its effect is real).
    - *Its repairs that fight the model:* scenario code that rewrites a model-owned Sim's state the model also
      writes (for example its motive repair in `ManagerHousehold`) is skipped for model-owned Sims, and the model's
      state wins at snap. The list of such sites comes from the P0 census of NRaas reads and writes.
    - *Its bugs:* fixed ourselves in ts3fixes behind switches, with an upstream PR and a note to the author where they
      are real NRaas bugs (AGENTS.md "Bugs in other mods"). NRaas has no license: its source is read and cited for
      behaviour only, never copied or ported line by line.
  - If NRaas internals change (method not found at load), the hook logs and the model falls back to promoting the Sim
    on any NRaas touch it can see (pushes, positions), never to standing down silently.
- **NRaas Overwatch** `RecoverMissingSims` (default `kRecoverMissingSims = true`) runs once a day at its alarm hour.
  For every household with a home lot it rebuilds each missing Sim through `RecoverMissingSimTask` ->
  `Instantiation.Perform`. It skips placeholders, boarding-school students, parents away, early vacation returners and
  SimPort travellers [verified]. Left alone, it would undo T4 daily; it does not affect T3. **Decision: we override
  it** (mechanism in §5.6).
- **The full NRaas audit** (docs/318 "Every other NRaas project", NRaas source at commit `2f57186`, read and cited
  only) adds these facts [verified unless marked]:
  - The missing-Sim rebuild has **two triggers**: Overwatch's nightly alarm, and **ErrorTrap's check on every world
    load**, which rebuilds every resident without a Sim object.
  - The rebuild task is shared NRaas code that **each assembly compiles in**. ErrorTrap, Overwatch, Debug Enabler,
    Porter, MasterController, StoryProgression, Careers, Register and Traveler each carry a copy with the same type
    name. A hook resolved by name must patch **every loaded copy**. Our current `HitchCause` timing hooks patch only
    Overwatch's copy.
  - NRaas finds a placeholder **only on the Sim's home lot**.
  - Overwatch's manual "Recover Missing Sims" menu run **ignores placeholders**, so it could build an EA placeholder
    child a second time (possible, rare).
  - Overwatch's on-by-default instantiation check destroys a Sim right after it is built when it belongs to a group
    NRaas skips (boarding school, parents away, early vacation return, SimPort).
  - **Alarm marking:** every 15 sim-minutes Overwatch marks every one-shot alarm to run in its own task. This may move
    where EA's stuck-check alarm runs (docs/134 I-199-1) [inferred]. It also bears on our held-alarm rule (§5.2).
- **Aging:** `AgingManager` alarms run on descriptions [verified, mostly null-checked: 8 of 12 uses]. An age-up of a
  T3 Sim promotes it first, through the guard. In T4, EA's age-up of a description without a body
  (`AgeTransitionWithoutCake`; `AgingState` builds out of world) must never meet a second instantiate from our queue
  during a deferred age-up (`AgeUpDupInst.cs`, #553). The queue skips any Sim with an age-up in progress.

## 5. Stage 2: far Sims without a live Sim

Source: a static pass over the EA gameplay DLLs, the NRaas source and ts3fixes (2026-11-22). Counts come from a
pattern match (approximate); each site named was read.

### 5.1 How EA already runs Sims without a Sim object

- Every way to create a Sim ends in `SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)`;
  `Instantiate(Lot)` and `InstantiateOffScreen` funnel into it. Its only guard is "a Sim exists, return it". It may
  wait in `AgingState.PreInstantiateSim` for a deferred age-up's outfit, so two calls at once can collide (docs/512,
  #553, docs/612) [verified].
- Homeless townies leave the world and are destroyed (`GoToVirtualHome` / `GoToVirtualHomeInternal.Run`: fade out and
  destroy). `Sim.MakeSimGoHome` only sends Sims with a home lot home and never destroys them [verified].
- Service NPCs are reused only when they have no Sim (`Service.FindSimForAssignment`). Role Sims are built on demand
  (`RoleManagerTask.FillInWorldRole`, `Role.StartRole`). Fake-autonomy pool Sims are built and destroyed (docs/854,
  contributor branch). Sims from other worlds exist only as `MiniSimDescription` [verified].
- EA does remove resident Sims in a few places: NPC children become `Sim.Placeholder` objects when no caregiver is home
  (caregiver routing monitor `DematerializeChildren`; `AgeUpNpcSituation`), and `Placeholder.Rematerialize` brings
  them back. Other removals happen only on dispose, pack-up to a mini description, travel or houseboat removal
  (`SimDescription.Dispose`, `PackUpToMiniSimDescription`, `Household.MoveToPreviousTravelerHousehold`) [verified].
  **The placeholder is EA's own "resident without a Sim" state, and NRaas Overwatch already skips it.**

### 5.2 T3 (stage 2a): object kept, hidden and inert

EA precedents [verified]:
- `RabbitHole.AnimateEnterRabbitHole` fades a Sim out without destroying it, moves it to
  `PositionToHideSimInRabbitHole`, sets `InRabbitHolePosture` and turns off its dynamic footprint;
- `Sim.RemoveFromWorld` / `GameObject.RemoveFromWorld` clear the in-world flag, the commodity map and the hidden flags;
- EA builds Sims at `Vector3.OutOfWorld` in `AgingState`, `Pregnancy` and `GameStates`.

Design: a T3 Sim is hidden the rabbit-hole way. Its object is placed on its plan lot (or inside the rabbit hole), out
of the scene, footprint off. It runs inside one "parked" interaction that yields until promotion (as a rabbit-hole Sim
sleeps inside its work interaction). On top of that:
- motive updates are frozen (`Motive.FreezeDecay` / `SetDecay`, EA's own API [verified methods exist]);
- autonomy is off for the Sim;
- the native locomotion agent update and `[UnseenAnim]` pose skip it (ownership flag, §3.1).

A hidden Sim would otherwise still run its task, needs and alarms [inferred]. That is why the parked interaction and
the frozen motives are needed, not only the hide. Its object-bound alarms are held until promotion (decision 7, below).

Safety: the object, its alarms and every reference stay valid, so the 293 unguarded `CreatedSim.` uses (§5.4) see a
real Sim [verified count, inferred safety]. Saves contain the object as today (§5.3). Hidden and inert state is
runtime-only: on load the Sim is a normal Sim again (rule 7).

**Alarms on a T3 Sim wait until it goes live (decision 7).** An alarm whose owner object is a model-owned Sim (buff
timeouts, skipping-work, per-Sim situation alarms) is held when it comes due instead of running on the parked Sim. At
promotion the held alarms fire once each, in due-time order, before the Sim's first live tick. A repeating alarm fires
once, not once per missed period. Global alarms that only read the Sim (the work-day alarm, §3.4) are not held.
Mechanism: a hook on EA's alarm dispatch that checks the alarm's owner against the ownership table [inferred: alarm
owner field and dispatch site to confirm in P0]. At the save flush, held alarms stay EA's (the object is saved with its
alarms not yet run), so the .sims3 holds plain EA state.

With NRaas Overwatch installed, its **alarm marking** (every 15 sim-minutes it marks every one-shot alarm to run in its
own task, docs/318) changes where a held alarm would run [verified marking; effect on our rule inferred]. The hold
check therefore sits at EA's dispatch of the alarm callback, not at the task that runs it, so a marked alarm is still
held. A held one-shot alarm released at promotion may then run in Overwatch's own task, not inline. That is the same
as vanilla with Overwatch, so it is accepted. The same marking may move where EA's stuck-check alarm runs (docs/134
I-199-1) [inferred]; the docs/318 game check of the stuck-check counters with Overwatch on covers both.

### 5.3 Save and load

- No script code runs per Sim at save time: there are no pre-save hooks in `SimDescription`, `Household` or `Sim`.
  Descriptions without a Sim are saved inside their household; the townie pool proves that works [verified].
- EA does **not** rebuild missing residents at load. `SimDescription.PostLoadFixUp` only builds boarding-school
  children, and only without University. Residents come back because their Sim objects are saved, and `Sim.OnCreation`
  reconnects them (travelling households: `GameStates.PostTravelingFixUp`) [verified].
- T3: the object is saved. The flush before save (rule 4) applies the computed motives and moves the object to its
  plan place, through the same hook `UnseenOutfitDefer` uses before `OptionsModel.SaveGame` (`DisposeSaveHook`,
  docs/586) [verified hook]. **Safe** [inferred from the above; save matrix to prove].
- T4: saving a resident with no Sim is safe in itself, but after load it stays without a Sim, because EA has no
  rebuild pass [inferred from verified absence]. So T4 needs our own load-time pass that rebuilds T4 residents through
  the queue, or, better, it stores them as **placeholders** (§5.6), which EA's own `Placeholder.Rematerialize` handles.
- `Household.AllActors` lists only built Sims (`Household.cs`), so code that loops over it silently skips missing ones;
  `Household.Remove` checks for a missing Sim [verified].
- Timers on the Sim object die with it [verified]:
  - Pregnancy's hourly and contraction alarms are on the mother's Sim; `Sim.OnCreation` restarts the pregnancy
    (`Pregnancy.Continue`), but hours without a Sim are lost [inferred];
  - career "skipping work" and hunger alarms are on the Sim;
  - the work-day alarms are global and check for a missing Sim (`RegularWorkDayGoToWorkHandle`).

  So pregnant Sims are never T4 (§3.5 extended).

### 5.4 Facade: EA's reads of far Sims (docs/326 #3, P7)

Census of the read paths that decide what happens to a far Sim (heuristic grep over `Sims3GameplaySystems` +
`Sims3GameplayObjects`, uses / files) [verified counts]:

| Read | Uses | Files | Facade answer for T3 | T4 |
|---|---:|---:|---|---|
| `.LotCurrent` | 3895 | 788 | **physical**: the parked object sits on its plan lot (moved at block edges by one queue op) | `CreatedSim == null` path, EA's own |
| `.Position` | 2411 | 630 | physical, as above | same |
| `.CurrentInteraction` | 989 | 304 | the parked interaction (non-null, autonomous, cancellable). Callers that cancel it trigger the on-demand guard | null |
| `Motives.GetValue` / `GetMotiveValue` | 210 | 94 | lazy: the guard applies the computed motives on read (§5.5) | n/a |
| `.IsSleeping` | 151 | 83 | true in a sleep block (parked interaction reports sleep) | false |
| `.IsAtWork` | 57 | 40 | EA's own: work blocks are EA-owned in the rabbit hole (§3.4) | EA's own |
| `IsInRabbitHole` / `InRabbitHolePosture` | 49 | 25 | true for work blocks and parked-in-rabbit-hole | n/a |
| Phone `CanCallSim` / `GetSimToLot` | 2 sites | `Phone.cs` | promote on call (§3.6); `GetSimToLot` already instantiates a missing receiver | EA's own |
| Party / invite guest lists (`InviteOver`, guest lists) | 173 | 16 | promote on pick | EA's own |
| Story progression `Demographics.Collect` | 1 | docs/588 | physical lot + description data | description data |

Reading of the census:
- Hooking each read is impossible (3895 `LotCurrent` uses in 788 files). So the T3 facade is mostly **physical**: the
  hidden object is kept where the plan says. Position and lot reads are then right with no hook. That costs one queue
  op per Sim per block edge, ~4-8 per sim-day [inferred].
- Only value reads that would be visibly wrong get a lazy hook: motives on read (applied and cached until the next
  block), and the sleep / busy answer of the parked interaction.
- The T4 facade is EA's own `CreatedSim == null` handling, which those callers already have where EA expects missing
  Sims (Phone, AgingManager, Household, Relationship partly null-checked). The 293 unguarded uses are the T4 risk
  (§5.6).

### 5.5 On-demand guard

- Choke point: `SimDescription.Instantiate(Vector3, ResourceKey, bool, bool)` (T4: rebuild through our queue at TTV 0
  and return the Sim; never a second build during an age-up) [verified choke point].
- EA callers that build a missing Sim [verified]: `GameStates.PostTravelingFixUp`, `SimDescription.PostLoadFixUp`,
  `Placeholder.Rematerialize`, `Role.StartRole`, `RoleManagerTask.FillInWorldRole`,
  `BarBrawlSituation.CallPoliceAndJoinBrawl`, Prom, `AddRoommate.Execute`, `Phone.GetSimToLot`,
  `Annex.StartGraduationCeremonyOneShot`.
- T3: `InteractionQueue.Add` on a model-owned Sim (any push from EA or a mod), a position set, or a cancel of the
  parked interaction promotes the Sim synchronously before the call proceeds. A T3 promotion is cheap (unhide + apply),
  so it may run outside the budget when forced.
- Reads that do not build the Sim (the 293 unguarded `CreatedSim.` uses) are not covered by the choke point
  [inferred]. In T3 they meet a real object. In T4 they are the risk list below.

**One-instance guard per SimDescription** (risk decision: duplicates and builds during a deferred age-up are guarded
and verified):
- A per-`SimDescriptionId` build state: `none`, `building`, `live`. Every build path (EA's `Instantiate` choke point,
  `Placeholder.Rematerialize`, NRaas's helpers, our queue) checks it first.
  - `live` with a `CreatedSim`: return that Sim, build nothing.
  - `building`: the second caller waits for the first build and gets its Sim (the `AgeUpDupInst.cs` semantics,
    #553); never a second build.
- **Age-up ordering:** if the description has a deferred age-up in progress (`AgingState` waiting in
  `PreInstantiateSim` for its outfit), the queue does not start a build. The age-up's own instantiate runs first, and
  the queue's op then finds `live` and becomes a no-op promotion. Our queue never calls `Instantiate` while an age-up
  holds the description.
- Dispose side: `Sim.Dispose` of a Sim that is not the description's current `CreatedSim` must not clear it
  (docs/612 `simdisposelink`); the guard moves to `none` only when the current Sim is disposed.
- Self-tests (pure state machine, `tools/selftest/farsim`): two concurrent requests give one build; a request during
  `building` returns the first Sim; age-up first then queue gives no build; dispose of a stale Sim keeps `live`; a
  failed build returns to `none` and logs.
- Correctness repros: `farsim-dup-build` (two build paths on one description in the same tick: one Sim, counter
  `dupBuildBlocked` > 0, no duplicate in `LotManager.Actors`) and `farsim-ageup-build` (age-up of a T4 Sim while the
  queue wants to promote it: one Sim with the new age, no null reference).

### 5.6 T4 (stage 2b): risks and the placeholder variant

Top 5 risks [verified sites, inferred effect] and their guards:
1. **NRaas missing-Sim rebuilds** of every missing resident: Overwatch's nightly `RecoverMissingSims` and ErrorTrap's
   check on **every world load** (§4, docs/318), plus the same task copied into seven more NRaas assemblies. Effect:
   thrash, plus a full build burst at the alarm hour and inside every load. **Guard (we override it, runtime
   precedence), four layers:**
   - **Placeholder on the home lot:** T4 residents are demoted through EA's `Sim.Placeholder` path (below), and the
     placeholder is placed on the Sim's **home lot** with its description set. NRaas looks for placeholders only
     there, so a placeholder left at the Sim's last position would not be found. The placeholder's position is the
     home lot's spawn spot. The Sim's real last position stays in the model's snap point and is used at
     rematerialize (Vector3 entry, §6.1), not EA's placeholder position.
   - **Hook every loaded copy:** at load, the hook finds every loaded NRaas assembly that holds the rebuild task type
     (by type name: ErrorTrap, Overwatch, Debug Enabler, Porter, MasterController, StoryProgression, Careers, Register,
     Traveler, and any other assembly found with the same type). It patches the per-Sim recovery in **each copy** to
     skip descriptions our ownership table marks as model-owned, placeholder or not. The hook logs one line per patched
     copy and one per copy found but not patched. The existing `HitchCause` hooks (Overwatch's copy only) are widened
     the same way when this is built.
   - **One-instance guard as the last layer:** any copy not patched, and Overwatch's manual "Recover Missing Sims"
     menu run, which ignores placeholders, still end in `Instantiation.Perform` -> `SimDescription.Instantiate`.
     There the one-instance guard (§5.5) answers with the existing Sim, or turns the call into a queue promotion at
     TTV 0. So neither a T4 Sim nor an EA placeholder child is ever built twice. A counter logs every call caught
     this way.
   - **Installer setting (Apex installer list, docs/318):** turn Overwatch `mRecoverMissingSims` off **only when
     `farsimt4` is on**, as a second layer behind the hooks. ErrorTrap's load rebuild has no setting, so the hooks are
     the only guard for it.
2. **Object-bound alarms are lost** (pregnancy, work-skipping, buff timeouts); time does not pass for them. Guard:
   pregnant Sims are never T4 (§5.3); with the placeholder variant the Sim's alarms are handled as EA handles a
   placeholder child's [inferred; P0 static check].
3. **293 unguarded + 86 copied-then-used `CreatedSim` uses** in EA code. Unguarded by area: Skills 75, Careers 46,
   ActorSystems 31, ActiveCareers 28, Roles 14. Risky sites: `AgingState.AgeTransitionCompleted` (age-down
   `IsPet`), `BuffMalePregnancy.OnTimeOutUpdated`, `OccultWitch.OnRemoval`, `School.UpdateDetention` /
   `SimsGoingToVisit` / `GiveGraduationBuffs`, `Phone` `OnAnswered` / `OnCallFinished`,
   `PromSituation.PushAllNpcTeensToLocationOneShot`, the `WeddingParty` and `FieldTripSituation` constructors,
   `CelebrityManager` alarms (docs/588 P4). ts3fixes: 53 files use `CreatedSim`, 28 of 36 direct uses are checked; the
   one real unchecked use (`Batch582iLogic.cs`, `TargetSim.CreatedSim.BuffManager`) is inside a try/catch.
4. **Double build:** two rebuilds at once, or one during a deferred age-up, give a null reference or a duplicate Sim
   (docs/512, docs/612, #553). Guard: the one-instance guard and age-up ordering (§5.5), with self-tests and two
   repros.
5. **Build cost on approach:** each rebuild is a full Sim build (outfit, compositor, init) as the player gets close.
   That is the stutter we are trying to remove, plus placement risk (`residentspawnslot`). Measured in docs/325
   (steady p50 / p95: BP 10.8 / 58.4, IP 15.1 / 20.5, SV 21.5 / 35.4 ms, plus a deferred `Sim.OnStartup` of 4-9 ms
   p50 one step later) [measured]. Guard: the spacing rule, the Vector3 entry, prebuild and resource warm-up (§6.1).
   EA has no load-time rebuild pass (§5.3).

**Placeholder variant (the T4 design, decision 6):** demote a T4 resident through EA's own `Sim.Placeholder` path
(the one EA uses for NPC children without a caregiver) instead of a bare destroy. Overwatch skips placeholders, and
EA's own `Placeholder.Rematerialize` rebuilds them through the choke point. Adults may use it, behind two gates
before `farsimt4` can be switched on:
- a static check (P0): every reader of `Sim.Placeholder` and every caller of `DematerializeChildren` /
  `Rematerialize`, for age tests or child-only assumptions (caregiver monitor, `AgeUpNpcSituation`), plus what the
  save writes for a placeholder;
- a correctness repro `farsim-placeholder-adult`: an adult NPC demoted to a placeholder, saved, reloaded,
  rematerialized on approach; one Sim, same description, same inventory, no script errors, Overwatch installed and
  not rebuilding it.

EA's placeholder path [verified, `Sim.Placeholder.DoDematerialize` / `Rematerialize`]: dematerialize disables
autonomy, waits for the Sim's interactions on the lot to finish, resets it if it is not standing, copies position and
facing, and calls `Sim.Destroy`. Rematerialize calls `SimDescription.Instantiate(Position)`, which is the Vector3 entry
with `addInitialObjects: true`, then `AttemptToPutInSafeLocation` and an outfit switch without spin. So the
placeholder already uses the cheap entry the spacing rule wants (§6.1).

**Inventory on re-instantiation (docs/325 item b)** [verified, decompile]:
- The inventory belongs to the **Sim object**, not the `SimDescription`: `Sim` adds its own `InventoryComponent` in
  its init, and the description keeps only passport-time items (`mInventoryItemsWhileInPassport`).
- `SimDescription.Instantiate(..., addInitialObjects)` calls `Sim.AddInitialObjects(!IsSelectable)`. For an NPC human
  this seeds the inventory (`SeedNPCInventory`, plus a diploma for University graduates without one).
- `Sim.Destroy` (and so the placeholder path) does not move the inventory anywhere [inferred: no inventory transfer
  found on the destroy path; EA moves inventories only in explicit paths such as `Inventory.MoveObjectsTo`].
- So a T4 round trip **loses the Sim's inventory and re-seeds a fresh one**. Nothing is duplicated, but it is not
  equal to vanilla, where the resident keeps its object and its inventory.

Decision in this design, so the inventory stays equal to vanilla:
1. T4 eligibility adds an inventory test: a Sim goes to T4 only if its inventory holds nothing but the item kinds
   `AddInitialObjects` would give it (seed items, diploma). Any other item (bought, gifted, collected, crafted)
   keeps the Sim in T3.
2. Such a Sim is re-instantiated with `addInitialObjects: true` (EA's own placeholder behaviour), so it gets the
   same kinds back. Whether `SeedNPCInventory` gives the same items each time (random choice or fixed set) is
   checked in P0 [inferred]. If it is random, the test compares kinds, not instances, and the repro checks kinds.
3. Never `addInitialObjects: false` for a T4 Sim: it would save docs/325's 0-8 ms `AddInitialObjects` p50 but leave
   the Sim with an empty inventory (no phone).
4. A later variant may move the items out before destroy and back after re-instantiation (`Inventory.MoveObjectsTo`
   to a holder, then `addInitialObjects: false`). It is not designed here: the holder would be a saved object and
   needs its own save-matrix check.

**Verdict:** T3 (2a) is safe enough to build behind the switch: references, alarms and saves stay EA's [verified
precedents, inferred whole]. T4 (2b) is opt-in and default off even with the model on (decision 5). It uses the
placeholder variant, overrides Overwatch and NRaas StoryProgression at runtime instead of standing down, excludes
pregnant Sims, and needs the save matrix and a docs/325 cost before anyone switches it on.

## 6. Spread queue

### 6.1 Operations

| Op | T3 | T4 | Sliceable |
|---|---|---|---|
| Op | T3 | T4 | Cost (docs/325, steady p50 / p95) | Sliceable |
|---|---|---|---|---|
| Demote | capture + hide + inert | capture + EA `Sim.Placeholder` path (§5.6) | T4: `Sim.Destroy` 0.7-0.9 / <= 2.3 ms [measured]; T3 hide not measured [inferred: ~ a rabbit-hole entry] | no (one Sim) |
| Promote | apply + unhide | `Placeholder.Rematerialize` = `Instantiate(Vector3)` + apply | T4: call BP 10.8 / 58.4, IP 15.1 / 20.5, SV 21.5 / 35.4 ms, then deferred `Sim.OnStartup` 4-9 / 10-18 ms one step later [measured]; T3 not measured | no; one Sim is the unit |
| Move (block edge) | move hidden object to the next plan lot | none | not measured [inferred: a position set] | no (cheap) |
| Plan | build day plan | same | not measured [inferred: microseconds] | yes (pure) |

Where a T4 promotion's time goes (docs/325 §3, steady p50) [measured]:
- engine object create (rig, model; native, inside `CreateObjectWithOverrides`): 8-16 ms, 50-75% of the call on every
  world;
- `AddInitialObjects` (starting inventory, script): 0-8 ms;
- skills, occult, listeners, health, footprint, age-up check: < 0.5 ms together;
- `Sim.OnStartup` does **not** run inside the call (ledger I-615-1 refuted). It runs later as a second sim-thread
  chunk of 4-9 ms; the next frame is 2-11 ms longer at p50 (`f1x`);
- `Sim.OnCreation` is not inside the call either, and **its cost is still unmeasured** (docs/51 §615 open). The
  budget below assumes it is small; the docs/325 probe should time it before T4 is switched on.
- The `Instantiate(Lot)` entry adds a spot search: up to 92.5 ms p95 during load on BP (14.5 ms p50), 77 ms p95 on
  travel. The Vector3 entry has none.

**Budget** (docs/325 proposal, adopted). A single build cannot be split and does not fit a 16 ms frame at SV's p50, so
the budget is a spacing rule, not a ms cap per frame:
- **At most one far-Sim instantiation per 3 sim frames:** the call, the deferred `OnStartup`, and one quiet frame.
- **None in a frame that already ran longer than 25 ms** (sim-frame time read from the existing hitch / frame
  counters); the build waits for the next frame under the limit.
- **Always `SimDescription.Instantiate(Vector3, ...)`** with the spot the model holds: the plan place or the Sim's
  last position, route-checked (§3.8). Never the `Instantiate(Lot)` entries. The on-demand guard (§5.5) rewrites a
  Lot-entry request for a model-owned Sim into the Vector3 entry with the model's spot.
- **Removals need no spreading** (Destroy p95 <= 2.3 ms). Up to ~10 per frame are fine; they only avoid sharing a
  frame with an instantiation.
- T3 promotions and block-edge moves are not instantiations and are not limited by the spacing rule. They use a
  small op cap per frame (start: 4) until their cost is measured.
- Throughput: one build per 3 frames = ~10-20 builds per real second at 30-60 sim frames per second. That is enough
  for the prebuild horizon. Only a camera jump outruns it, and that case is handled by "late Sims appear out of view"
  (§6.4).
- Cost spread per build: ~25-30 ms at p50 over ~50 ms of frames, about a tenth of the 10-Sim visitor burst's worst
  frame (docs/325).

Queue entries `{simId, op, dueRealMs, priority}` run in a sim-thread task using the EaSpread pattern
(`EaSpreadCore.SliceFull`, `SlicedPass<T>`; docs/146, docs/147). The task enforces the spacing rule for builds and the
op cap for the rest.

**Resource warm-up before a build (docs/325 item a)** [inferred; needs a finer split of the engine create]: the engine
create is the largest part of every build. The cheapest way to shorten it is not to move it, but to have its inputs
ready: when a T4 promotion is queued (hours ahead for prebuilds, seconds ahead for TTV promotions), a worker thread
pre-reads the outfit's model and texture resources into the resource cache. The sim-thread create then finds them
resident. Gate: a docs/325 follow-up that splits the native create (resource reads vs rig / scene setup) must show the
resource reads are a real share before this is built. It must also respect the in-process memory budget (docs/539
compositor growth; warm only the next few Sims in the queue).

### 6.2 Priority = time-to-view (docs/326 #5, P10)

`TTV = (d - R_reach) / max(v_toward, v_min)`:
- d = distance to the nearest viewer point;
- `R_reach` = the camera reach `FarCompute` uses (it grows with zoom);
- `v_toward` = the camera speed component toward the Sim (from the camera points each tick);
- `v_min` = a floor (e.g. 5 m/s).

For scheduled visits, TTV = visitTime - now, in real time at the current game speed. Relationship-tied Sims get a
lower TTV (weight). Lowest TTV first. Demotions run only when no promotion is waiting. A Sim whose predicted view is
short (the camera moves past) is skipped (docs/326 P10).

### 6.3 Hysteresis and pre-expand ring (docs/326 #4, P2)

- Promote at the inner radius (T1 150 m); demote only past the outer radius (T1 -> T2 at 200 m after 30 sim-min,
  T2 -> T3 at 450 m), in a **periodic pass** (every 5 s real time), never per frame.
- Pre-expand ring: Sims on lots within ~100 m of the camera target and along the camera motion vector (1-2 s ahead)
  are promoted to T2 before the camera arrives.
- Prebuild: the plan is deterministic, so known visits, calls and household-lot arrivals are queued `PrebuildHours`
  (default 2 sim-hours) ahead. A Sim whose block lies on a lot a household Sim routes to (route destination, rabbit
  hole, a community lot the household travels to) is promoted when that route starts.
- **The prebuild window covers the whole build (docs/325 item c):** a build counts as done only when its deferred
  `Sim.OnStartup` has run (seen as the Sim's first SimUpdate step / startup flag), not when the `Instantiate` call
  returns. The visit's push (`VisitLot`, §3.9) waits for that point. The window's floor is then: queue wait (at most
  one build per 3 frames, times the builds ahead of it) + the call + the `OnStartup` step + the route-in time from the
  out-of-view spot. Two sim-hours is far above that at normal speed. At speed 3 and with many visits due together,
  the queue raises a due visit's priority to TTV 0 when its floor gets within 2x of the time left.

### 6.4 Loading screens and late Sims

- World load and lot travel: the queue runs without the spacing rule behind the loading screen. It demotes all
  eligible far Sims after load and promotes everyone whose block is on the destination lot before the screen ends.
  Builds still use the Vector3 entry, because the load's own `Instantiate(Lot)` spot search is the 92 ms p95 case
  in docs/325.
- Late Sim (TTV < op cost): made live at the nearest out-of-view point on its way, then routes in; never pops in view.

### 6.5 Hooks

- The lot load / unload hub of docs/321 lane A (`native/ts3native/lotstream.cpp`, lot add / remove remote calls
  `0xABD3C0` / `0xABD3E0`; today measurement only and OFF) [verified hook sites, docs/321]:
  - lot add near the viewers -> queue promotion of its far residents and visitors by TTV;
  - lot remove -> demotion candidates;
  - both bump the region's open-lot list generation.

  The model needs lane A built first.
- World change, save, travel, Edit Town: flush (apply all T3) or drain, as `UnseenOutfitDefer` already does before
  `OptionsModel.SaveGame` and in non-live modes (`DisposeSaveHook`, docs/586).

### 6.6 Outfit builds

A T4 promotion builds the Sim's outfit. `unseenoutfitdefer` (ON) holds instant outfit builds of unseen townies, but
not a Sim's first set after `SimDescription.Instantiate` (docs/586, `UnseenOutfitDefer.cs` comment) [verified, code
comment]. The prebuild horizon (§6.3) covers this: the first build happens hours before the Sim can be seen. T3 has no
outfit build at promotion.

## 7. Cost and gain estimates

From docs/322 §3 (attribution shares with heavy tools on; seam self cost ~3.4 ms/s) [measured]:

| World | NPC total ms/s | >= 150 m ms/s | 150-400 m | >= 400 m | Sims >= 150 m per pass | per Sim ms/s |
|---|---:|---:|---:|---:|---:|---:|
| BP | 293.9 | 249.8 | 57.7 | 192.1 | 62.6 | ~4.0 |
| IP | 283.0 | 250.8 | 31.7 | 219.1 | 17.0 | (boats / rabbit holes) |
| SV | 228.0 | 194.7 | 167.4 | 27.3 | 26.2 | ~4.8-8.2 |

Estimates [inferred]:
- **T3 ceiling** (>= 400 m, every Sim eligible): BP ~190, IP ~219, SV ~27 ms/s attributed.
- **T2 ceiling** (150-400 m at 1/N cadence): unknown. docs/243 and docs/322 found cadence skips ~0, so T2 counts as 0
  here. It ships anyway as the hand-off tier (decision R3, §11); its gain is whatever P5 measures. Where the T3 edge
  moves to ~300 m for a world after calibration (decision 1), SV gains up to ~half of its 167 ms/s.
- **Drift and visit proposals** (§2.7, §3.9): evaluated lazily at snap or once per household lot per day; ~0.
- **Exclusions** (role, situation, relationship-tied, mid-interaction Sims): unknown; P1 shadow measures them. Assume
  20-40% of far Sims are excluded at any time.
- **T3 residual:** an inert object still shows up in `LotManager.Actors`, in AutShadow `AddRow` per Sim (docs/321
  M5), in the `UnseenTable` walk (M6) and in hourly sweeps (M16, L11, L12). Guess 0.05-0.3 ms/s per Sim. Block-edge
  moves: ~6 per Sim per sim-day, negligible. T4 removes the residual.
- **Model cost:** lazy plan per Sim per sim-day when read, plus one closed-form evaluation per snap: ~0.
- **Churn:** docs/243 §3 counted 2.9 unseen-class flips/s in BP under harness camera moves [measured]. Far-tier flips
  with 150/200/450 m hysteresis and a 30 sim-min dwell should be one to two orders of magnitude rarer. At an assumed
  2-5 ms per T3 promotion and 0.1 promotions/s, churn costs < 0.5 ms/s. T4 promotion: ~15-30 ms p50 per Sim (call +
  `OnStartup`, docs/325 [measured]), plus the unmeasured `OnCreation`. At 0.1 promotions/s that is ~2-3 ms/s, so T4
  must keep churn low: T4 only after a long T3 stretch, never for Sims near the ring.
- **Realised:** BP ~95-150, IP ~115-175, SV ~15-25 ms/s (T3 only; SV more with a lower edge or a working T2) of
  sim-thread time. This is upper-bound reasoning over attribution shares; only the in-load A/B decides (§10).
- **Memory (T4 only):** ~0.15 MB per Sim object (docs/66 M11), plus outfit and compositor entries (docs/539) and
  animation state. BP's far Sims (~60) -> ~10-60 MB of address space [inferred]. Small today, essential on 4x worlds.

## 8. 4x and streamed worlds

- At today's density a 4x-area world holds ~4x the NPCs, nearly all in the far bands. With flat per-Sim cost, NPC
  time would grow from ~290 to ~1,100 ms/s (docs/322 §3) [inferred]. With the far model the EA-owned NPC count is
  bounded by Sims near the viewers, near the household and tied to it, so NPC cost stops scaling with world area.
- Everything is keyed by Sim id, lot id and region id. Radii are metres from the viewers. Open-lot lists are per
  region and invalidated by the lane A hub. No table is sized from today's population: they grow at use (AGENTS.md
  rule; docs/321's caps M1, M3, M5 and M6 must be fixed first, or parked Sims would still fill them).
- Plan positions are lot ids, not coordinates, so plans stay valid when regions page (docs/320 §7.3).
- T4 is the "Instantiated NPC Sims: paged, drop CreatedSim, keep the SimDescription" row of docs/320 §7.3 and phase
  b1 of docs/320 §8: a region page-out demotes its NPCs to T4 first, and page-in promotes them by TTV. A streamed world
  cannot keep objects of NPCs in unloaded regions, so T4 (placeholder variant) is required there and its §5.6 risks
  must be closed first.
- Sidecar per save (`<Save>.farsim.json`), sharing the `<Save>.lw/` folder of docs/320 §7.6 on a streamed world.
- Household tiles stay active (docs/320 §7.2) and household Sims are never far, so the household rule holds on any
  world size.

## 9. Prior art and design checks

Prior art: **docs/326** (Bethesda, Kingdom Come, RDR2 / GTA / AC Unity, Minecraft, Dwarf Fortress, RimWorld, X4,
Elite / NMS, Star Citizen, Bannerlord / CK, Sims 4, SimCity / Cities, simulation LOD research). This design adopts
docs/326 §2 ideas #1-#12. Where each lives here:

| docs/326 idea | Here |
|---|---|
| #1 lazy far state | §2.1, §2.2 |
| #2 single owner, safe-point handover with credit | §3.1, §3.2 |
| #3 script-facing facade + census | §5.4 |
| #4 hysteresis, pre-expand ring | §6.3 |
| #5 budgeted queue by time-to-view | §6.1, §6.2 |
| #6 relationship-weighted tier | §1.3, §3.5 |
| #7 observation promotes | §3.7 |
| #8 versioned seed, delta-only sidecar | §2.1, §2.5 |
| #9 shadow calibration before timing | §10 P1 |
| #10 reduced middle tier T2 | §1.3, §10 P5 |
| #11 walk-in or snap per arrival kind | §3.8 |
| #12 walk-by roles only near viewers, never named Sims | §3.8 |

**Design checks** (docs/326 §3 pitfalls; each one is a test or a counter in §10):

| # | Pitfall | Check here |
|---|---|---|
| P1 | pop-in or teleport at snap | snap only out of frustum; walk-in kinds (§3.8); counter `snapInView` must stay 0 |
| P2 | thrash at the tier border | hysteresis + dwell + periodic pass (§6.3); counter promotions/demotions per Sim-hour |
| P3 | frozen world | closed-form catch-up from elapsed time (§2.2) |
| P4 | outcome bias between tiers | shadow calibration (P1): motive, place and activity distributions vs EA |
| P5 | save bloat, cleanup deleting what matters | **never cull** (rule 6); sidecar deltas only, cap + prune (§2.5) |
| P6 | two owners advance one Sim | single owner (§3.1); counter `ownerConflict` must stay 0 |
| P7 | scripts see the LOD | physical facade + lazy motive reads (§5.4), on-demand guard (§5.5) |
| P8 | uneven catch-up | one apply point: at snap only (§4) |
| P9 | changed generator rewrites the past | versioned seed, version history in the sidecar (§2.1) |
| P10 | repeated far / full flips cost more than they save | TTV skip of short views (§6.2), budget (§6.1) |

## 10. Phased plan and tests

Wall-clock time for Claude with agents (CLAUDE.md effort rule); game runs listed separately. Every switch is default
off, with the household exemption in code and a repro per fix (AGENTS.md). Order follows docs/326 §2.

| Phase | Work | Agent time | Game runs |
|---|---|---|---|
| P0 | Prerequisites and static checks: docs/321 lane A hub (lot load / unload generation + managed hook) and caps M1 / M3 / M5 / M6; docs/325 op costs (other agent); facade census refined (§5.4: exact call sites per read for phone, invite, story progression); census of NRaas StoryProgression / Overwatch reads, writes and instantiate sites on model-owned Sims (§4); EA setters for drift (§2.7); alarm owner and dispatch site (§5.2); UI hook points for decision 8 (§3.7); placeholder static check for adults (§5.6) | ~2-3 h lane A + ~2-3 h static | 1 correctness + travel / save matrix (lane A) |
| P1 | **Shadow calibration, our machine only, before any timing:** compute plan, state, drift and visit proposals for far Sims with EA still running them; when a far Sim comes near (or for a sample, every sim-hour), compare model vs EA (motive error per commodity, lot, activity, LTR change per pair-day, skill points per Sim-day, visits per friend-day by distance); count eligible / excluded Sims by reason; set the per-world T3 edge (450 m or ~300 m, decision 1); rerun the docs/322 probe with `Bands=150,200,300,450,1000` | ~2-3 h | smoke + 3 x 10 min (BP / IP / SV) |
| P2 | **One-instance guard** (§5.5) with self-tests, first, since every later phase builds Sims through it | ~1 h | 2 repros (`farsim-dup-build`, `farsim-ageup-build`) |
| P3 | **T2 + T3 demote / promote** behind `farsim` / `farsimt2` / `farsimt3`: ownership table, T2 cadence as the hand-off tier, parked interaction, frozen motives, held alarms (decision 7), physical facade (block-edge moves), lazy motive reads, on-demand guard, flush before save, always-live hooks, observation hooks (decision 8), seeded drift (§2.7) | ~5-7 h | correctness repros 1-10 (below) |
| P4 | Spread queue with TTV priority, hysteresis pass, pre-expand ring, prebuild, loading-screen batching; visit proposals with distance decay (§3.9) | ~2-3 h | 3 jobs (camera sweep, travel, visit rate) |
| P5 | Timing: T3 in-load A/B on BP / IP / SV; T2 on vs off inside it (measured, ships as the hand-off tier either way) | ~30 min | smoke + 3 x 2 arms x 10 min, + 1 T2 arm pair |
| P6 | NRaas override (decision 4): hooks on its instantiate helpers and repair sites, our precedence, fixes for its bugs we hit (upstream PRs); EA story progression night with T3 Sims | ~2-4 h | 3 jobs (EA SP night, NRaas core SP night, NRaas scenario touching a T3 Sim) |
| P7 | **T4 placeholder variant** (`farsimt4`, default off even with the model on), Overwatch / ErrorTrap / every NRaas rebuild copy overridden (§5.6 risk 1), placeholder on the home lot, installer setting `mRecoverMissingSims` off only with `farsimt4` on, NRaas skip groups excluded, inventory eligibility test and `SeedNPCInventory` determinism check (§5.6), build spacing rule and Vector3 entry (§6.1), `Sim.OnCreation` timing added to the docs/325 probe, sidecar event log, career drift, save matrix (save-change rules); resource warm-up only after a docs/325 native-create split shows the resource reads are worth it | ~5-7 h | save matrix + `farsim-placeholder-adult` + Overwatch repro + 3 timing |
| P8 | 4x world proof with `tools/world-gen.ts` (docs/320 §1.5) | ~1-2 h | 3 jobs |

**Correctness repros** (each a `tools/harness/repros.json` entry with off / on arms, rewind on):

1. `farsim-household-never`: counter `demotedHousehold` stays 0 over a 10-min soak with household Sims spread across
   town (at work, on community lots).
2. `farsim-pan-promote`: the camera pans to a far lot; residents are promoted out of view, `snapInView` = 0, motives in
   range, no script errors.
3. `farsim-visit-due`: a scheduled NPC visit to the household lot; the visitor is live >= 1 sim-hour before and walks
   in through EA's visit code.
4. `farsim-phone-invite`: the household calls or invites a T3 Sim; it is live before the call, and the relationship
   delta equals the off arm's.
5. `farsim-save-parked`: save with T3 Sims, reload with the switch off; the .sims3 holds only EA state, no script
   errors, no missing Sims (save matrix for T4).
6. `farsim-travel`: travel to a far community lot with T3 Sims on it.
7. `farsim-sp-night`: 1 AM story progression with T3 Sims (EA); move-in and marriage targets are promoted by the
   guard; `ownerConflict` = 0.
8. `farsim-ageup-parked`: age-up of a T3 Sim; in T4, an age-up while the queue wants to instantiate the Sim.
9. `farsim-work-day`: a far NPC's work day runs EA's work interaction in the rabbit hole, and pay equals the off
   arm's.
10. `farsim-drift-bounds`: three sim-days with a far NPC friend group; NPC-NPC LTR, skill and (T4) performance deltas
    within bounds and identical on a rerun of the same save; every relationship with a household Sim unchanged
    (counter `driftHousehold` = 0).
11. `farsim-visit-far`: a far friend of the household (> 450 m) visits within the expected rate, walks in through
    `VisitSituation`; a nearer friend visits more often over a 3-day run (decay check).
12. `farsim-held-alarms`: a T3 Sim with a buff timeout due while parked; it fires once at promotion, not while parked.
13. `farsim-nraas-sp`: NRaas StoryProgression scenario targeting a T3 Sim (move-in or situation): promoted by our
    hook first, one Sim, the model's state wins on conflicting repairs.
14. `farsim-dup-build`, `farsim-ageup-build` (§5.5), `farsim-placeholder-adult` (§5.6).
15. `farsim-nraas-rebuild` (§5.6 risk 1), with ErrorTrap + Overwatch + MasterController installed:
    - a reload with T4 Sims (ErrorTrap's load rebuild);
    - Overwatch's nightly alarm;
    - the manual "Recover Missing Sims" menu run with an EA placeholder child and a T4 Sim present.

    Pass: one Sim per description, counter `nraasRebuildSkipped` > 0, a patched-copy log line per loaded NRaas
    assembly that holds the task, no build from an unpatched copy.

**Timing:** in-load A/B (`profile.ab`) on BP / IP / SV, 10 min per arm, Bin state `apex`, with the docs/322 band probe
on to read the band table per arm. Judge by sim ms/s by band, hitch p99 / p99.9 / max and sim it/s (AGENTS.md
regression bars). queue.ts runs the measurement smoke first. No timing run before P1 shadow passes.

## 11. Decisions (Rodney, 2026-11-22) and open questions

Rodney decided the open questions of the first version of this doc on 2026-11-22. Each is applied in the section
named.

| # | Question | Decision | Where |
|---|---|---|---|
| 1 | Radii | 150 / 200 / 450 m; the T3 edge is set per world to ~300 m after shadow calibration where the model is close enough | §1.3, §10 P1 |
| 2 | Snap clamp | yes: far NPCs never snap back into distress (motives >= -40) | §2.2 |
| 3 | NPC-NPC life while model-owned | relationships, skills and career performance **drift by seed**: deterministic, versioned seed, bounded, never touching relationships with household Sims | §2.7 |
| 4 | NRaas StoryProgression | **override it and fix it ourselves**: our guard covers its instantiate path, the model stays active with it installed, ours takes precedence at runtime; NRaas source read / cite only, never copied | §4 |
| 5 | T4 | opt-in, default off even when the far-Sim model is on | §1.2, §5.6 |
| 6 | Placeholder path for adults | allowed, behind a static check plus a repro | §5.6 |
| 7 | Alarms on a hidden T3 Sim | wait until it goes live | §5.2 |
| 8 | What counts as "looking" | Sim info, relationship panel, phone contacts, map selection; not hover, not map thumbnails | §3.7 |
| 9 | Visits | the model **proposes visits too**, following EA's visit logic; frequency falls with distance (distance-decay weight with relationship strength and EA's visit conditions); the real game spawns and runs the visit | §3.9 |
| R1 | NRaas Overwatch `RecoverMissingSims` | override it: it must not undo T4 (placeholder path it skips, plus our hook winning) | §5.6 risk 1 |
| R2 | Duplicate Sims, build during a deferred age-up | guard and verify: one-instance guard per SimDescription plus age-up ordering, self-tests and repros | §5.5 |
| R3 | T2 may gain nothing | accepted: T2 ships as the hand-off tier to T3 without its own gain, household-exempt and default off | §1.3, §7, §10 P5 |

Open questions that remain (to settle in P0 / P1, not blocking):
1. Visit decay constant T0 and the `rel` factors: calibrated in P1 against EA's live visit rate; Rodney to check the
   resulting visits per friend-week feel right in play.
2. Drift bounds (§2.7): first values are guesses; P1 compares them with EA's full simulation.
3. If NRaas renames or removes the hooked methods in a later version, the model falls back to promote-on-touch (§4);
   is a log line enough, or should the player see a notice?

## 12. Proposed docs/51 entries (not written; reserve with `node tools/registry.ts reserve ledger` when building)

- EA has no load-time rebuild pass for missing residents (`SimDescription.PostLoadFixUp` builds only boarding-school
  children, only without University) [verified].
- NRaas Overwatch `RecoverMissingSims` is on by default and rebuilds missing residents of households with a home lot
  once a day; it skips placeholders [verified].
- `Pregnancy.Continue` restarts a pregnancy at `Sim.OnCreation`; hours without a Sim are lost [inferred].
- `Career.RegularWorkDayGoToWorkHandle` pushes the work interaction only when `CreatedSim` exists; pay is in
  `Occupation.FinishWorking` from the work interaction [verified].
