# 361. Seed-only far Sims: a population that is recreated from a seed (2026-11-26)

Design only. Nothing built, nothing run in game. One offline measurement on four saves (§5.1). Ledger: docs/51 §631.
Rodney's idea (2026-11-26): the sidecar stores only a seed and the time for far Sims; they need no saved state and
are recreated on load or at go-live.

Read first: docs/345 (far-Sim machine, `F(t)` pure), docs/343 (sidecar), docs/353 (save flush), docs/355 (go-live
contract), docs/354 (shadow build), docs/359 (`simdescindex`), Atlas docs/007 (paged lots), 012 (memory path), 019
(regions).

## 0. Verdict

- **Go for option (a) only: populations that we generate for Atlas regions.** A far region's residents exist as
  `(region seed, lot, sim time)` and nothing else. They never enter the `.sims3` unless they become real (§2.3).
  This is also the answer to Atlas docs/012 open point 2 (population of far towns at new game): neither in the
  pages nor in a population file, but a generator.
- **No-go for EA Sims.** A Sim that EA wrote into the save keeps its EA record. Option (b) is what docs/345 already
  builds (EA record frozen at go-far, `F(t)` at go-live): it saves CPU, no save size and no memory. Option (c),
  paging descriptions out of the `.sims3`, is dropped: a description is referenced by id from everywhere, so a
  no-mod load would show broken relationships and family trees, not just bare lots (§2.2).
- **The idea needs one change to work: no event log.** A seed-only Sim has, by construction, no state that the
  player, EA or another mod caused. The moment such state would exist, the Sim's whole household becomes a normal
  EA household ("promotion", §1.3) and EA saves it from then on. So nothing has to be logged per Sim and the
  sidecar keeps what Rodney asked for: one seed and one generator version per world. The time is the save's own
  clock.
- **Gains are real only on big worlds** (§5). On a 2048 m EA world descriptions are 1.0-2.3% of the save folder
  [measured]: not worth it. On a filled 8192 m world (16 towns): 14-37 MB of script stream not built at each save,
  about 12-30 s of the save's script walk, 4-11 MB of save file, and 26-139 MB of managed memory [inferred, D0
  measures it]. The stronger reason is that an 8192 m world must be born paged (Atlas docs/007 §0): its population
  cannot first exist as 4,000 EA descriptions either.
- **No-mod load (Rodney's answer to Q1, 2026-11-26, §2.4):** Sims go back to the main world and relationships to
  far Sims are removed if necessary. Since no code of ours runs then, it is a rule for what gets written: every Sim
  in the `.sims3` stands at a place EA can load, a left-behind Sim of a split household is written on its home lot
  in the main world (real placement in a small placement file), and no relationship in the save points at a Sim
  that is not in it. A relationship to a seed-only Sim cannot exist at save time by construction; a pre-save audit
  promotes the Sim if an id slipped through, and stripping is the last resort.
- **First static check S1: passed with five conditions** (§6.4, 2026-11-26). A household can be created with
  chosen ids and stored outfit keys; the four EA draws left on the path have results we overwrite. EA's dispose
  path clears the lists except three things we clear ourselves. The reference census cannot be closed by hand
  (3,418 instance fields), so the leak gate is a weak-reference count in P3.

## 1. What must stay exact

### 1.1 Three classes of far-Sim state

| Class | Examples | Function of (seed, time, inputs)? | Kept where |
|---|---|---|---|
| A. Generated | who lives on a lot, names, looks, traits, age, job, family inside the household; needs curve, day plan, place (docs/345 §6) | yes: `G(seed, lot, t)` and `F(seed, t)` | nowhere |
| B. World inputs | weather, season, holiday, venue hours, tuning (docs/345 I5, I6, I10) | recorded or re-read; the same for every Sim | sidecar `H` ring (docs/343), tuning |
| C. Caused from outside | relationship with a household Sim, phone call, marriage, job change, death, gift, inventory, money; EA story progression writes; NRaas StoryProgression writes; CAS and Edit Town edits | **no** | EA's `SimDescription` in the `.sims3` |

`F(t)` of docs/345 has no memory: the plan of a day depends on that day's draws and inputs only (docs/345 §7.1,
§7.3). The only history it carries is `ep`, `rev` and the drift ledger. For a seed-only Sim all three are dropped:
`ep` and `rev` are fixed at 0 and drift is not applied (cumulative values are closed forms of `t`, §1.2).

### 1.2 The generator

- **Slot = residential lot.** `G(seed, lotId, gen)` gives the household of that lot: 1-8 members with
  `SimDescriptionId`s, names, age stages, traits, careers, relations inside the household, and an index into a
  baked looks pool (§3.1).
- **Time is closed form.** `gen = floor((t + offset(lot)) / lifespanTicks)`; inside a generation each member's age,
  career level and skill levels are step functions of `t - genStart`. An elder past the lifespan is gone, and the
  next generation of the lot takes over. No births, marriages or moves across households in the first version:
  genealogy is closed inside the household, so no family link can point at a Sim that does not exist.
- **Seed:** `seed = M(M(M(worldSeed) xor lotId) xor pg)`, mix `M` and `range(n)` exactly as docs/345 §8, with `pg` =
  population generator version. Integer arithmetic only.
- **Own tables only.** Name lists, trait and career weights are tables shipped with the mod, one frozen set per
  `pg`. `G` never reads live tuning, so a tuning mod cannot change who exists.

### 1.3 Promotion replaces the event log

A generated household is in one of three states.

| State | EA objects | In the `.sims3` | Leaves the state when |
|---|---|---|---|
| virtual | none | no | a viewer comes near (docs/355 boundary) |
| transient | `Household` + descriptions (+ Sim objects when near), created from `G` at time `t` | only if still present at a save, see §2.3 | far again: evicted if the evict test passes, else promoted |
| promoted | normal EA household | yes, EA's own record, for good | never (one way in the first version) |

**Evict test** (run at go-far, on the Sim's safe point, docs/345 §5.4). The household goes back to virtual only if
all hold:

1. No member is household, selected or of unknown scope, and none ever was (shared predicate, both directions).
2. No relationship, however small, with a Sim outside the household that is not itself transient. That covers every
   household Sim and every EA Sim.
3. No inbound reference from outside the household: genealogy, urnstone, situation, service or role assignment,
   alarm with a member as owner, party or invite lists, another Sim's known-info lists (census list from S1).
4. The **durable set** equals what `G(seed, lot, t)` gives now: name, traits, age stage, career and level, household
   members, home lot, inventory empty, family funds inside the generated band, no occult, pregnancy, celebrity
   level, degree or wish beyond the generated ones.
5. Everything else is **forgettable**: motives, buffs, queue, place, outfit worn, conversation topics. The model
   computes these again and overwrites nothing of the player's.

Any failed line promotes the whole household. So: a player-caused event, an EA story progression write and an NRaas
write are all handled by the same rule without hooking any of them. They can only land on EA objects, and a changed
EA object fails the test and stays. That is exact by construction (memory rule "no gaps"): no writer has to be found.

Relationships between two transient Sims of different households are forgettable (unseen NPC drift, allowed by the
unseen-Sim rules); with docs/345 `farsimdrift` they would be bounded drift anyway.

### 1.4 Log versus snapshot

| Design | Per far Sim | Per sim day | Verdict |
|---|---|---|---|
| Seed + time + append-only event log | 0 | 4-8 transitions per Sim (docs/345 §7.2) at about 110 bytes per `E` line = 0.4-0.9 KB per Sim; 4,000 Sims = 1.8-3.5 MB per sim day | not needed: `F(t)` is memoryless, the `E` ring stays a diagnosis aid (docs/343, 2000 lines) |
| Periodic snapshot + deltas | 64 + about 100 bytes (docs/345 §3.1) = 0.66 MB per snapshot at 4,000 Sims | deltas as above | breaks the 1 MB block cap of docs/343 §10 on its own |
| **Seed + time + promotion** | **0** | **0 for Sims; world inputs only: `H` ring, capped at 512 lines, about 30 KB** | chosen |

Sidecar content for this feature: the `W` line that docs/343 already has (`seed`, `ticks`) and one more model
version, `pg`, in `mv`. Nothing per Sim, nothing per lot.

## 2. The hard constraint: the `.sims3` is what EA would write

### 2.1 The three options

| | (a) generated region population | (b) EA Sim frozen at go-far | (c) descriptions paged out like lots |
|---|---|---|---|
| Sim exists in EA's save | no, until promoted | yes, always | no while far |
| Save size win | yes | none | yes |
| Memory win | yes | Sim object only (docs/344 L2) | yes |
| CPU win | yes | yes (docs/345) | yes |
| `.sims3` is pure EA data | yes | yes | yes, but with ids pointing at nothing |
| Loads without the mod | yes: far towns unpopulated, promoted households present | yes, complete | no: relationships, genealogy, urnstones and 788 `SimDescription.Find*` sites (docs/344 §4) meet missing Sims |
| Status | **this doc** | built as docs/345 / 354 | dropped |

(c) differs from paged furniture in one point: a chair is referenced by its lot, a Sim by id from the whole world.
The only EA Sims that could be paged safely are those with no inbound reference and no changed durable state, and
those are worth nothing to keep exact. If measurement D0 (Atlas docs/012) shows descriptions are heavy, the EA-exact
compact form is EA's own `MiniSimDescription` (218-222 bytes in the save [measured, §5.1], used by EA for Sims of
other worlds, docs/324 §5.1). Level 2 of Atlas docs/019 (region worlds behind a world switch) gets that form from
EA for free.

### 2.2 What the save holds under (a)

- Virtual households: nothing.
- Promoted households: EA's normal records, written by EA, loadable anywhere.
- No field of ours, no marker class, no changed format. `SimDescriptionId`s of generated Sims are values from `G`
  instead of EA's generator. EA's ids are a clock-and-counter code from `0x7CFC00`, not a random draw, but no
  script code decodes one and EA itself writes stored ids into fresh descriptions [verified, §6.4 item 2].
  Generated ids set bit 0, which EA's generator never sets.

### 2.3 Saves, loads and a lost sidecar

- **At a save** (hub of docs/353, hard flush): every transient household that passes the evict test and has no Sim
  near a viewer is evicted first, with EA's own destroy calls. Those that are near, or fail the test, are promoted
  and EA writes them. So a save never contains a half-owned Sim, and no repair line is needed.
- **Promoted is recognised from EA state, not from our files:** a residential lot with an EA household is not a
  generator slot. `G` is asked only for lots that EA holds as empty. No tombstone list exists that could be lost.
- **A promoted household that EA later removes** (all dead, moved out by story progression): the lot is empty
  again and the lot's current generation moves in. It reads as new people moving in.
- **Sidecar lost or stale** (docs/343 §5): the world gets a new seed. Promoted households are untouched, because
  they are in the `.sims3`. Virtual households come out different: strangers the player never dealt with are other
  strangers. Nothing the player caused is lost.
- **Load without the mod, then save, then load with it:** the same as above; EA has kept every promoted household.

### 2.4 No-mod contract (Rodney's answer to Q1, 2026-11-26)

> "If world is loaded without the mod, we should move sims back to main world and if necessary remove the
> relationships from far off sims."

Without the mod none of our code runs at load. So the answer cannot be a load step: it has to be true of every
`.sims3` we let EA write. Three invariants, checked in the save hub (docs/353) before the native save call.
**Main world** = the world the save opens in (the household's home world).

**N1. Every Sim in the `.sims3` is at a place EA can load.**

| Where the Sim really is | What EA writes | Placement kept by us |
|---|---|---|
| Same EA world, any region (Atlas docs/019 level 1) | the Sim where it stands. It is an ordinary EA position, and the lot under a household Sim is pinned and resident (docs/019 §H floor), so its lot is whole in the save | nothing |
| Another region world (level 2), the world is loaded | EA's own travel save for that world; the home world's `.nhd` holds the household at home, as in any EA trip | nothing |
| Another region world, left behind there at a world switch (split household, docs/019 §4.3) | **in the main world, as a member of its household on the home lot.** The move is made at the switch, behind the loading screen, with EA's travel-return path for that Sim, so the record is the one EA writes for a returned traveller | one placement line: Sim id, region world, lot, position, leave time |
| Generated Sim, promoted | its household on its lot, normal EA records | nothing |
| Generated Sim, virtual | not in the save | nothing (seed) |

- The target of a move is always the **home lot**, never "the nearest lot": it is owned, always resident, it is where
  EA's own travel return and `AttemptToPutInSafeLocation(sendHome: true)` put a Sim, and it needs no search.
- No live household Sim is ever moved to make a save. Level 1 needs no move at all, and the level 2 move happens
  where EA moves Sims itself (a world switch). If the hub ever finds a household Sim at a place that is not in the
  loaded EA world, that is a bug: counter `saveSimOutsideWorld`, and a normal save is refused with EA's own dialog
  (the refuse rule of docs/353 §7); a travel save cannot be refused and counts.
- **Placement file.** The sidecar of docs/343 refuses household data (§8 there), so placement lines go into their
  own small file next to the played-household store, bound to the save by the same stamp (docs/343 §5.2). It holds
  ids and places only, never needs, queue or relationships.
- **Modded load:** stamp matches -> the Sim is shown greyed with its region (docs/019 §4.3) and is not instantiated
  at home; choosing it switches to that region and EA's travel-arrival path puts it on the stored lot. Stamp does not
  match or the file is gone -> the Sim is simply at home, which is what the `.sims3` says.
- **Change to docs/019 §4.3:** a left-behind Sim's EA record no longer lives in the frozen region's `.nhd` (point
  (2) "new against EA" there goes away). The frozen region still holds its lots and NPCs untouched, and no far
  machine runs on it while a placement line points into it.
- Not settled (S3, Q7): what EA does with a household member that has no Sim object while its home world runs
  (shown, aged, instantiated at load?). The split household needs EA to instantiate such a member at home on a
  no-mod load. If it does not, the fallback of docs/019 §4.3 applies: the household crosses together.
- **Update 2026-11-26 (Rodney's Q6 and Q7 decisions; design in Atlas docs/022, which replaces the split-household
  rows above where they differ).**
  - Q6: every Atlas set has an EA-size main world that loads without the mod; the regions are separate 2048 m
    world files with a manifest. "Main world" in this section is that file.
  - Q7: the left-behind Sim is **frozen** until fetched and stays exact. Method (docs/022 §6): the Sim stays live
    in its region's `.nhd`, which is the truth; the record on the home lot is EA's own home copy of a traveller,
    which the main world keeps during every trip [verified docs/022 fact 3: the return path finds it by id and
    merges into it]. So N1 row 3 changes: **nothing is written for the Sim at the switch, and EA's travel-return
    path is not run for it** (its export runs before the travel save [verified docs/022 fact 1] and would change
    the truth). While the main world runs with the mod, the home copy is held still (frozen park of the Sim
    object, aging state out of the aging manager's list with the saved flag untouched, alarms shifted at release;
    census F0b there). Without the mod the Sim stands at home as it was when it left home.
  - S3, first answer [verified docs/022 fact 7]: EA instantiates members without a Sim object only in
    `Household.PrepareToBecomeActiveHousehold`, called when a household is chosen for play, not at a save load
    [the "not" is inferred]. So the no-mod record needs a Sim object, which the home copy has [inferred, F0].
    EA's Free Vacation and boarding-school states are not a freeze (both keep aging).
  - S3 is widened to F0 of docs/022 §11; kill criterion 9 and phase P5 follow docs/022 §9-10 (FZ-1, FZ-2).
  - **F0 result (2026-11-26, static, Steam 1.67 script decompile; Atlas docs/022 §11.1).** The split household
    holds on the script side; the fallback "household crosses together" is not triggered. What changes here:
    - The home copy has a Sim object [verified in script order: `GameStates.SwitchWorlds` saves the home world
      with the travellers only reset, GameStates.cs:1625-1646], so the no-mod record needs no instantiate path.
      It is saved **without buffs**: `BuffManager.StoreBuffsForTravel` (BuffManager.cs:1732) removes all of a
      traveller's buffs before that save and keeps the travel-flagged ones on the description [verified].
    - "Nothing is written for the Sim at the switch" needs two steps before EA's switch: the Sim leaves the
      travel household (or `Household.MoveToPreviousTravelerHousehold`, Household.cs:3868, destroys its Sim
      object) and its id leaves the traveller list (or `GameStates.PostTravelingFixUp`, GameStates.cs:2533-2555,
      treats the missing traveller as dead and disposes the home copy) [verified].
    - EA then treats the home copy as a member who stayed at home and merges relationship values, family tree
      and age stage into it from the mini record (`SimDescription.MergeTravelInformation`,
      SimDescription.cs:7273) [verified]. So N3 "exact" for a left-behind Sim on a no-mod load means: EA's
      stay-at-home record, no buffs, state of the day it left home otherwise.
    - Aging hold: the aging manager's list is not saved and is rebuilt from the saved flag at load
      (AgingManager.cs:25, 1216-1256; SimDescription.cs:654, 3657) [verified]; taking a member off it changes no
      saved flag. `RemoveSimDescription` also cancels the three age-transition alarms, which are saved handles.
    - No check forces the household to travel together (`TravelUtil.FinalBoardingCall`, TravelUtil.cs:2575)
      [verified]. A switch with no traveller fails before the home save (GameStates.cs:1631) [verified].
    - Still open: the Sim's parking household across the region's save and a later load, the fetch (both
      RG-3 of Atlas docs/019), the alarm hold census (F0b).
  - **F0b result (2026-11-26, static, Steam 1.67 script decompile; Atlas docs/022 §11.2).** The hold census of
    the home copy passes with conditions. "Alarms shifted at release" is not the whole hold:
    - One pump moves the Sim-object state: `Autonomy.Update` (Autonomy.cs:990-1015; callers SimUpdate.cs:225 and
      the low-detail autonomy run, Autonomy.cs:1075) runs motives, buff timeouts, mood, skills, the career tick,
      opportunities and wishes. Its float time stamp is a saved field (Autonomy.cs:239) [verified]. Hold: one
      guard at its head, and the stamp moved by the held time.
    - Alarms are absolute tick deadlines, and the save writes the alarm queue only (AlarmManager.cs:997-1009)
      [verified]. Hold: the Sim's and the description's timers leave the queue (found by the owner map) and go
      back with a new deadline: whole periods for day and week alarms (EA's own clock-jump arithmetic,
      AlarmManager.cs:1020-1044), plus the held time for the rest.
    - Aging: off the manager's list directly; EA's remove call would cancel three of the four age alarms
      (AgingManager.cs:2216-2228, AgingState.cs:54-60) [verified].
    - Relationship decay is the daily pass `Relationship.DecayAll` (Relationship.cs:1142; there is no
      `Relationship.UpdateRelationship`): pairs with a held member are skipped the way the loop skips a
      boarding-school pupil (1170).
    - Bills need no hold: no member count in the bill (Mailbox.cs:2570) [verified]. EA's after-travel catch-up
      and mini-record aging do not run for a region (InWorldState.cs:247-254, MiniSimDescription.cs:731)
      [verified].
    - **Save and load:** every hold is released before the native save and applied again after it (save hub,
      docs/353), and again after a load. The save then holds a plain Sim with the remaining times of the day it
      left home, and no hold time is stored anywhere [inferred, FZ-2].
    - **Parking household in the region:** the load fix-up keeps every household's members and marks their mini
      records instantiated; only descriptions in no household are disposed (SimDescription.cs:2891-2920,
      3125-3145); `Sim.OnLoad` has no destroy path for such a Sim [verified, script side; game run = RG-3].
    - **Fetch:** EA's travel import with one managed branch in the arrival loop (GameStates.cs:2122-2166): the
      imported twin is dropped, the region's Sim joins the travel household, no merge [inferred, RG-3]. The hold
      ends before the home save of that departure.
    - Holes, each a list still to close: per-Sim alarms with no owner (about 25 sites), 160 walks over the
      active household's members (4 read), stored times on the Sim (about 75 fields), listeners with no actor
      key, one-shot hour-of-day alarms. Ledger: docs/51 §631, V-631-13..16, M-631-2, I-631-22..25.
    - **Holes closed (2026-11-26, static; Atlas docs/022 §11.3 has the hold table).**
      - H1: 13 per-Sim timers in 6 classes beyond the Sim- and description-owned ones (mermaid dry-out,
        performance-career gigs and steady-gig notice, gig audience pull, singagram, Simulon V cooldown, attracted
        NPC date/gift/letter). Found by an owner set (career, gig, job, the attraction controller of B's pairs),
        handle fields, or the callback target. School field trip and graduation keep Sims in **static** lists
        (School.cs:1593-1599, 2427-2470; never saved): dropping B there is what a vanilla save and load do
        [verified sites].
      - H2: 170 sites read: 106 need a skip (24 clock-driven), 6 already exclude B, 58 need none. One guard in
        `Household.Members.GetActorsOfType` (Household.cs:108-118, keeps only members with a Sim object, which
        is how EA's away states leave the list) covers 85; 21 description-list sites need their own
        [verified predicate; per-site sort by a helper agent].
      - H3: 141 fields and stamps (99 shift, 14 pair, 28 none). The update task's stamp (SimUpdate.cs:56) is
        not saved, and four saved fields must not move (midlife day gate MidlifeCrisisManager.cs:917, passport
        return and opportunity display dates, history node), so the shift is a per-field list, not a reflection
        pass [verified].
      - H4: key-0 listeners hear every Sim's events (EventTracker.cs:77-100). Werewolf, fairy and
        permanent-zombie moon rise, robot conversation end, missed job (PerformanceCareer.cs:3128) and
        third-party wishes (ActiveDreamNode.cs:738-740) change B with no actor test; one dispatch guard skips
        them, integrity events (target died or deleted) still run [verified sites; guard inferred].
      - H5: the timer record has no name, and both one-shot adds give the same record (AlarmManager.cs:20-60,
        140-157); one-shot hour-of-day timers are told apart by the callback method from a closed list of 13
        sites and move by whole days with their tied dates (gig start, opportunity window) [verified list; rule
        inferred].
      - NRaas (source read, names only): StoryProgression's allow test refuses a selectable active-household
        Sim, except active-target scenarios (stand down while those are on); Overwatch `StuckCheck` and
        `ResetCheck` leave a selectable, non-routing Sim alone; `CleanupAging` re-adds B to the aging list at
        load, so our re-apply runs after it [inferred from source].
      - Still unknown: two struct records' persistence, ErrorTrap's load check, the other StoryProgression
        managers, EA 1.69. Ledger: V-631-17..21, M-631-3, I-631-26..29; I-631-25 closed.

**N2. No relationship in the `.sims3` points at a Sim that is not in it.**

Can one exist at save time? By construction no, with one gap to check:

- A `Relationship` holds two `SimDescription`s. A virtual Sim has none, so no relationship object can name it.
- A transient Sim with a relationship outside its household fails evict test line 2 and is promoted. The save hub
  evicts only what passes the test (§2.3), so both ends of every relationship are written by EA in the same save.
- A Sim of another region world: EA's own form is the relationship plus a `MiniSimDescription`, as for locals met
  on an EA trip (docs/324 §5.1). That is valid without the mod.
- **The gap: Sim ids kept as numbers** (invite and party lists, pending phone calls, learned-info tables; the list
  is S1 item 3). An id can outlive an eviction if the census misses a holder.

So the hub runs a **pre-save audit** over the census holders: any id that belongs to a virtual household
(a membership test on `G`, no table) gets its household created and **promoted before the save**. Promotion is
preferred to stripping: nothing is removed from the player's game, nothing has to be restored, and the save is
complete. Counter `seedPromotedAtSave`.

**Strip, the last resort** (Rodney's "if necessary"): only when that create fails inside the hard flush (memory
guard, EA create error) and the save cannot be refused (travel save). Then the entry is removed with EA's own
removal call for a deleted Sim, written as one line to the placement file, and put back after the save and on a
modded load. It is still what EA would write: EA removes exactly these entries itself when a Sim is deleted
[inferred, S1 item 2], so the result is the save of a world in which that Sim does not exist. For a normal save the
hub refuses instead. Counter `seedRelStripped`, expected 0 in every queue.

**N3. Both fallback paths end in a playable EA save.**

| | Load without the mod | Sidecar or placement file lost or stale (mod present) |
|---|---|---|
| Household Sims | all present, exact; left-behind Sims stand on the home lot instead of in their region | same |
| Their relationships, family tree, inventory, money | intact (N2) | intact |
| Promoted households, EA Sims | present on their lots | present |
| Virtual households | absent: far towns have no residents | generated again from a new seed: other strangers |
| Far lots | building and portals, no paged furniture (Atlas docs/007 §5) | unchanged, pages are found by their own stamp |
| Region the left-behind Sim was in | not reachable; its `.nhd` stays on disk | is a plain far region again (no household Sim in it), Sim at home |
| Stripped entries (N2 last resort) | gone | gone if the placement file is lost |
| If the player then saves | the stamp changes, so sidecar and placement file are stale for that save: the next modded load is the right column | - |

Nothing in either column is damage to clean up at a later load: the save is a normal EA save in both.

## 3. Determinism risks

| Risk | Answer |
|---|---|
| EA random numbers at go-live | Creating a Sim with EA's townie generator draws from `RandomUtil` and would give another person each time. `G` produces every field itself and sets it through EA setters; looks come from a baked pool (§3.1). No call into `RandomUtil`, `RandomGen` or `System.Random` (docs/345 H1, H2). S1 lists every draw inside the create path that we cannot avoid |
| Mono float differences | No float in `G` or `F`. A float field EA needs (genetic sliders, weights) is set from `k / 256`, exact in any float mode |
| A new mod version changes the generator | `pg` is part of the seed and is stored per world in the sidecar. Every released `pg` keeps its frozen tables and its code path for good; a world keeps the `pg` it was created with. Selftest: golden vectors per `pg` (ids and a field hash for fixed seeds), on Steam 1.67 and EA 1.69 |
| Looks pool changes | the pool is versioned with `pg`; a pool entry is never edited, only added under a new `pg` |
| Household rule | A seed-only Sim has no `SimDescription`, so it cannot be in the household or be selected. Joining a household needs an EA object, and evict test line 1 keeps it promoted. Unknown scope = promoted. `[Experimental] HouseholdToo` does not apply (creates and removes Sims) |
| Sims referenced by household Sims | A reference can only be made to an EA object. Evict test lines 2 and 3 keep every referenced Sim. Safety net for ids kept as numbers: a `SimDescription.Find` miss (the miss path of docs/359 §4) asks `G` whether the id belongs to a virtual household of this world, creates it and promotes it. Counter `seedFindMiss` should stay 0 |
| EA story progression sees empty lots | far residential lots look empty to EA, so EA may move households into them (S2). If it does, the lot is simply consumed (§2.3), but a flood would undo the gain: counter `seedLotTakenByEa` per sim week, and EA's move-in is limited for far lots only if S2 shows a clean switch |
| NRaas StoryProgression and other mods | they walk EA's lists, never see a virtual household, and cannot write one. A write to a transient household promotes it (§1.3). Same empty-lot question as above for NRaas immigration |
| Tuning or pack set changes | `G` uses its own tables. A generated trait, career or outfit that is not installed (pack removed) is replaced by the table's base-game fallback, chosen by the same draw |
| Two creates at once | `SimDescription.Instantiate` can collide with a deferred age-up (docs/512, docs/324 §5.1). Generated Sims never have a deferred age-up: age stage is set before create |
| Leaks | create and evict must return managed bytes and every list entry (memory rule "no leaks in new builds"): counters `seedHouseholdsLive`, `seedDescsLive`, `seedEvictLeft` (must be 0 after quit to menu) |

### 3.1 Looks pool

EA's CAS code builds an outfit from random genetics and is slow. So looks are baked once, by the Atlas authoring
tool, in game, with EA's own generator: a few hundred outfit sets per age and gender, stored as EA outfit
resources in the region's base pack (read-only, shared by all saves, never in a save or in the sidecar). `G` picks
by index and sets hair and skin colour from draws. Size: a sim outfit resource is 3-4 KB in our saves [measured,
§5.1], so 500 looks with five outfit categories are about 10 MB of pack on disk [inferred].

## 4. Fit with the other far work

- **docs/345:** a transient Sim is an ordinary far-machine Sim. A virtual one is evaluated by the same `F`, from a
  record without a `SimDescription` (this is Atlas docs/019 open point 3; the plan inputs come from `G` instead of
  the description).
- **docs/355:** go-live gains one step in front of line 1, "create household from `G`", inside the same budget and
  time-to-view order. The create is the expensive part (docs/324: 15-30 ms per Sim object, description cost
  unmeasured), so households are created one ring earlier than Sims are instantiated.
- **docs/350 / 358 (`FarLight`, `FarHold`):** unchanged. A virtual household owns no alarms; `FarHold` holds a
  transient Sim's timers like any far Sim's, and evict test line 3 covers timers that outlive the hold.
- **Atlas docs/007:** a virtual household's lot is a paged lot. Page-in of the lot and create of its household
  are one unit, ordered lot first.
- **Atlas docs/019 §H:** virtual Sims cost nothing in budget B. A D3 square holds about 16 descriptions (250 per
  town over 16 squares), 0.1-0.6 MB [inferred], against 12-27 MB of lot content.
- **Atlas docs/019 §4.3 (split household):** a frozen world's virtual households cost nothing to freeze: `F` and `G`
  are functions of that world's own clock.

## 5. Gains

### 5.1 Measured: a description in the save

Offline, with `readObjs` of `tools/harness/save-sources.ts` over the script state (resource `0x06B981ED`) of four
harness saves. Records are summed per class and classes sorted into "description side" (CAS, skills, careers,
relationships, life events, wishes, traits, households) and "Sim object side" (`Actors.Sim`, autonomy, motives,
queue, buffs) by class name. Low end = named records only; high end = with a proportional share of the untyped
array and string records (14-16% of the stream). The class sorting is by name, not by reference walk [inferred].

| Save | Descriptions / mini / Sim objects | Script state, MB (packed) | Per description, KB | Per Sim object, extra KB | Description share of script state | of the save folder |
|---|---|---|---|---|---|---|
| Sunset Valley, fresh | 142 / 6 / 67 | 6.0 (1.7) | 4.7-5.6 | 12.0-14.4 | 10.7-12.9% | 1.0% |
| Sunset Valley, aged | 241 / 108 / 96 | 8.2 (2.6) | 6.6-8.2 | 10.5-12.9 | 19.0-23.5% | 2.0% |
| Isla Paradiso, aged | 266 / 169 / 96 | 7.0 (2.1) | 3.7-4.5 | 8.8-10.6 | 13.7-16.5% | 1.4% |
| Bridgeport, aged | 264 / 291 / 100 | 9.0 (2.8) | 7.6-9.3 | 10.9-13.4 | 21.6-26.6% | 2.3% |

- The `SimDescription` record itself is 640 bytes; the rest is life events, wishes, genetics, skills,
  relationships. The 99 descriptions Sunset Valley gained while aging cost 9.4 KB each.
- A `MiniSimDescription` is 218-222 bytes: 17-43 times smaller.
- Description-side records are 13-32% of the named records (86-249 records per description). 35-55 households
  per town.
- Sim portraits (`0x0580A2CE`) are 13-16 KB each but exist for 34-101 Sims per save only.
- Script state packs to 29-31% in the save.

### 5.2 Scaling [inferred from §5.1, linear in population]

Per town of about 250 descriptions: 1.0-2.5 MB of script stream, 0.3-0.7 MB of save file. Far share from Atlas
docs/007 §9 (93% at 4096 m, 98% at 8192 m; taken as all towns but one).

| | 2048 m | 4096 m | 8192 m | 4 region worlds of 8192 m |
|---|---|---|---|---|
| Towns / descriptions | 1 / 250 | 4 / 1,000 | 16 / 4,000 | 64 / 16,000 |
| Far descriptions, all virtual | 0 | 750 | 3,750 | 15,750 |
| Script stream not built per save, MB | 0 | 3-7 | 14-37 | 14-37 (only the loaded world is saved) |
| Writer tables not built (40 bytes per record, Atlas docs/007 §9), MB | 0 | 2-8 | 10-39 | same |
| Save file smaller, MB | 0 | 0.9-2.1 | 4-11 | 4-11 per world file |
| Save time, script walk (6.3 s per town today, docs/15; description share 13-32%), s | 0 | 2-6 | 12-30 | same |
| Load time | - | - | same share of the script read, unmeasured | - |
| Managed memory, at 7-37 KB live per description (2-4 times the stream, unmeasured), MB | 0 | 5-28 | 26-139 | 26-139; other worlds are not in memory anyway |
| Sidecar, bytes | - | one `W` line | one `W` line | one `W` line per world |

Per far Sim: 0 bytes in the save, 0 in the sidecar, 0 in memory while virtual, against 3.7-9.3 KB of stream and
an unmeasured 7-37 KB of memory for a full `SimDescription`. Atlas docs/012 row 9 estimates descriptions and
households at +75-300 MB for 16 towns; the measured stream size points at the lower half. D0 settles it.

What the numbers do not show: promoted households come off the gain. Aged Sunset Valley has 341 relationship
records for 241 Sims after EA story progression and play, so most residents of a town the household lives in end up
referenced. That is the town the household is in. For towns the household only passes through, the shadow run
(P3) counts promotions per sim week.

## 6. Phases, effort, kill criteria

### 6.1 Phases

Wall-clock for Claude with agents. Starts after Atlas docs/007 P1 and the docs/354 shadow build have run, and on
the purpose-built test world (no 4096 m loads before it exists).

| Phase | Work | Effort | Game runs |
|---|---|---|---|
| P0 | S1, S2 and S3 static reads (§6.2); D0 managed bytes per description (Atlas docs/012) | 3-4 h | 1 job (D0) |
| P1 | `G` in ts3fixes: mix, tables for `pg` 1, golden-vector selftest on both editions | 2-4 h | 0 |
| P2 | Looks pool bake in the Atlas authoring tool; pool reader | 4-8 h | 2 jobs |
| P3 | Shadow on an EA world: create a generated household on an empty lot far from the household, let it go live, run the evict test, evict; counters for promotions per sim week, create and evict cost, bytes returned | 6-10 h | 4 jobs |
| P4 | Join with paged lots and the far machine (virtual record without a description); save hub step with the pre-save audit and the refuse rule (§2.4 N1, N2); save test matrix (memory rule "save change matrix"), each matrix save also loaded **with the mod removed** and with the sidecar deleted, checked against the table of §2.4 N3 | 10-15 h | 8-11 jobs |
| P5 | Split household: placement file, move home at the world switch, greyed member, restore at the switch back. Only with Atlas RG-3 (docs/019 §7) and after S3 | 4-6 h | 3 jobs, one of them a no-mod load |

Total about 29-47 h of agent work and 18-21 live jobs. Default off throughout (`[Far] SeedPopulation`); Atlas
worlds only: on an EA world the generator has no lots.

### 6.2 First static checks (no game run)

- **S1** (first, 1-2 h, decompiled script assemblies; **done, result in §6.4**):
  1. Create path: can `Household` and `SimDescription` be built field by field with a given `SimDescriptionId`
     and given outfit keys, and which calls on that path draw from `RandomUtil` or assign a new id.
  2. Remove path: what `Household.Destroy` / `SimDescription.Dispose` leave behind. Every global list that holds a
     description or household (household list, description lookups, `MiniSimDescription` table, genealogy, portrait
     cache, the docs/359 index).
  3. The inbound-reference census for evict test line 3: every class with a field of type `SimDescription`, `Sim`
     or `Household`, or a Sim id as `ulong`.
- **S3** (1 h, same agent; **done, result in §6.5**): a household member without a Sim object. What EA does with it at load (instantiate at
  home or leave it), while the world runs (aging, alarms, the household panel), and which EA cases produce it
  (travel return, a Sim away). And the travel-return path itself: can it be run for one Sim at a world switch.
- **S2** (1 h, same agent): where EA story progression and NRaas StoryProgression pick empty lots for move-ins,
  and what they count as the town's population.

### 6.3 Kill criteria

1. S1: a household cannot be created without EA random draws we cannot avoid or with chosen ids, or removal
   leaves an entry we cannot clear -> stop. Fallback worth one more check: EA's household import from the bin with
   baked household files instead of `G`.
2. A matrix save loaded without the mod shows a missing household Sim, a Sim at an invalid place, a relationship or
   list entry naming a Sim that is not there, or a script error from one -> never default on; fix or stop.
3. D0: far descriptions on 8192 m under 50 MB live **and** under 5 s of save time -> not a memory item. Keep it only
   if a world born paged needs the generator as its population source (Atlas docs/012 open 2).
4. P3: more than 10% of a town's lots promoted per sim week while the household only passes through -> one rework
   of the durable set, then park.
5. Any golden-vector difference between editions, machines or two runs -> stop until explained.
6. Any household counter non-zero, or any difference a household Sim can observe in a promoted Sim before and after
   a save and load -> stop.
7. Create plus evict of 100 households does not return managed bytes within the run-to-run spread -> no eviction;
   then the cost bound is "every household ever seen" and criterion 4 decides.
8. S2: EA or NRaas fills far lots faster than one per sim week and there is no clean switch -> the generator
   yields to them (lots consumed), gain recounted.
9. S3: EA does not bring a household member without a Sim object back at a no-mod load -> no split household
   through placement; the household crosses together (docs/019 §4.3 fallback). The rest of this doc stands.
   **S3 (§6.5): met only by a form that saves the member without a Sim object; the Q7 design keeps it.**
10. `seedRelStripped` or `saveSimOutsideWorld` non-zero in a queue that does not force it -> a bug in the audit or
   the census; no default on until explained.

### 6.4 S1 result (2026-11-26, static, Steam 1.67; EA 1.69 not read)

**Verdict: pass with five conditions (C1-C5). Kill criterion 1 is not met.** Read: the decompiled gameplay
assemblies (file:line below are the local decompile, method names as EA's) and `TS3W.exe` for the id source.
Method-level search for `RandomUtil`, `RandomGen`, `new Random` and `GenerateGUID` in 30 files; callees were
followed one level, deeper levels are [inferred]. Ledger: docs/51 §631, V-631-1..8.

**1. Create path.** [verified]

| Step | EA entry | Draws | Bypass |
|---|---|---|---|
| household | `Household.Create` (Household.cs:1893) | field initialiser of the telemetry id (Household.cs:441): 1 draw per `new Household`, cannot be avoided | the field is public: overwrite it from `G` |
| household id | same, static counter (Household.cs:424, 1896) | no draw; each create uses one counter value | none needed: the counter is re-derived at load (Household.cs:1322-1332) |
| description | the `SimOutfit` constructor (SimDescription.cs:3012), which runs `Init` (3019) | `Init`: handedness (3029) only when the outfit's flags carry none; staggered age (3025 -> AgingManager.cs:1264) when the age changes; allergy (3095 -> HealthManager.cs:243) for every human | bake handedness into the pool outfit or set it after; overwrite the age progress; the allergy field is private (HealthManager.cs:47): write it by reflection or leave it forgettable (C2) |
| id | `MakeUniqueId` inside `Init` (3077, body 6235) | no random draw: one value from the native id generator (item 2) | afterwards the public id setter (1946), then `MakeUniqueId` again: it keeps an id that is set and unique and resets the three owner ids (6247-6258). EA sets a stored id on a fresh description itself in `MiniSimDescription.UnpackSim` (MiniSimDescription.cs:1300-1303) |
| join | `Household.Add` (2866) | `LearnInfoAboutHouseholdMember` (InformationLearnedAboutSim.cs:732): a count draw and random known traits, per pair | `AddSilent` (2985) + `OnHouseholdChanged` (SimDescription.cs:3535), then `Relationship.Get` with `checkForAttraction` false (Relationship.cs:1313; the attraction score draws at 1939) and the known-info flags set from `G` |
| traits, skills, wish, zodiac, names, favourites | `TraitManager.AddElement`, `SkillManager.AddElement` (SkillManager.cs:689) + level, property setters | none in these; the zodiac getter draws when unset (SimDescriptionCore.cs:439), so set it | - |
| career | `CareerManager.AcquireOccupation` (CareerManager.cs:372) | none for an uncontrolled Sim without a Sim object: boss and co-workers are chosen at random only for the active household (Career.cs:4489, Occupation.cs:3011). Career event timers draw their times (Career.cs:974, 1162) [inferred to be on this path]: forgettable | the level's uniform is built by EA's outfit builder at run time (Career.cs:4224 -> Occupation.cs:3497): the only CAS work on the path. Cheaper: give the job at promotion |
| not used | `Genetics.MakeSim` / `MakeDescendant` / `AssignTraits`, `GeneticsPet.*`, `OutfitUtils` random parts, `SimUtils.MakeTypicalFamily`, `TraitManager.AddRandomTrait`, `SimDescription.RandomizePreferences` | all draw | never called |

No draw in `OccultManager`, `VisaManager`, `CareerManager`, `SkillManager.AddElement`, the `Relationship` constructor
(Relationship.cs:521) or the `Genealogy` constructor. So every draw left on the path (telemetry id, staggered age,
allergy, and handedness if not baked) has a result we overwrite; none decides who the Sim is.

**Outfits from keys need no CAS.** `new SimOutfit(key)` (SimOutfit.cs:347) is a native load of the stored resource;
`Init` copies age, gender, skin tone and body weights from it (3022-3052); `AddOutfit` is a list insert
(SimDescriptionCore.cs:1008). EA's outfit builder runs only for a career uniform, an age-up or an EA outfit change.

**2. Ids.** [verified]

- Managed: `MakeUniqueId` loops the generator until `IsSimDescriptionIdUnique` (6190): not 0, not a
  `MiniSimDescription` key, not in any household, the bin or an urnstone. Callers: `Init`, `Fixup` (3312, only for id
  0), `ImportContent` (5720, only with the remap flag), bin placement, character import, passport, wishing well. A
  stored id survives save and load as written.
- Native: the script call `0x996B90` is `push 0; call 0x7CFC00`, the object id generator of Atlas docs/016. It is
  not the script random stream. `0x7CFC00` initialises once (`0x7CFAC0`) and repeats `0x7CF930` until the value
  differs from the last one handed out (`0x11D8900`). `0x7CF930` packs a clock count, a same-tick counter (max
  `0x100`) and a 16-bit value from `0x7CEC60`; its last shifts leave **bit 0 clear and bit 1 = the argument**, so
  every id EA generates here has bits 0-1 clear.
- So the id source is a clock-and-counter code, not a random draw (the fail line of I-631-6 as written). But no
  script code decodes an id (no arithmetic on a `SimDescriptionId` in the four gameplay assemblies, one use as a
  cache name), the generator's state is touched only by its own calls, and EA itself writes a stored id into a
  fresh description. **A chosen id cannot confuse the allocator.** Native consumers take the id as a key [inferred].
- Collisions: EA tests a new id only against live ids, so it cannot see a virtual household's id. Rule C4: `G` sets
  bit 0 of every id; no id from EA's generator can then equal it [inferred from the bit layout; ids in shipped
  worlds and bin files come from the same generator, not checked]. `MakeUniqueId` replaces a duplicate **silently**
  (and records the old id), so `G` calls the public uniqueness test itself before the setter and counts a hit
  (`seedIdClash`, must be 0). Each create also takes one value from EA's generator, as any EA create does.

**3. Remove path.** [verified unless marked]

Sequence, as EA's own emigration does it (EmigrateHousehold.cs:116-128): move out, destroy Sim objects, dispose each
description. The last `Household.Remove` runs `HandleLastSimsDeath` (Household.cs:3796): move out and `Destroy`, which
ends in `Household.Dispose` (1432) [the native step between the two not read].

| Holder | Cleared by |
|---|---|
| household list, delete list | `Household.Dispose` (1470-1473) |
| member lists | `Members.DisposeEachMember` (251) |
| lot's household, mailbox, parties on the lot, services | `Lot.MoveOut` (Lot.cs:6093) |
| shared inventories, real estate, household alarms, persist group | `Household.Dispose` |
| `Relationship.sAllRelationships`, both directions | `SimDescription.Dispose` (3977 -> Relationship.cs:1460) |
| aging list and aging alarms | `Dispose` (3981 -> AgingManager.cs:1235) |
| celebrity listeners | `Dispose` (3985) |
| job and school: the career location's worker list, other Sims' co-worker and boss fields, work alarms | `Dispose` (3990 -> Career.cs:3663) |
| partner, pregnancy, service, role, virtual home, pet and trait-chip managers, description alarms | `Dispose` (3993-4069) |
| wishes that name the Sim, on other Sims | the two events `Dispose` sends (3971, 4040) [handlers not read] |
| event listeners | keyed by Sim object and removed with it; the description-side managers register none without a Sim object (grep of 11 manager files; CelebrityManager.cs:1962) |

**Leftovers** (not touched by either dispose):

| # | Leftover | Effect | Our step |
|---|---|---|---|
| L1 | the `MiniSimDescription` entry, if one exists: `Dispose` only clears its instantiated bit. One is made by `GetMiniSimForProtection` (SimDescription.cs:5416), which EA story progression calls just to **test** a Sim (KillElderSim.cs:83, EmigrateHousehold.cs:98); travel adds them too (GameStates.cs:1665) | the id counts as used: the next create of the same Sim gets another id | `MiniSimDescription.RemoveMSD` (595) at evict; add "has a mini entry with protection flags" to the evict test |
| L2 | genealogy nodes: `Dispose` only clears the node's Sim (Genealogy.cs:1325), relatives keep the node | none inside the household (all go together) | `ClearAllGenealogyInformation` (1250) per member first, as EA's `ExterminateHousehold` does (Household.cs:2411) |
| L3 | the Everyday outfits: `Dispose` uncaches all others (4037) | native outfit cache entries [inferred] | `ClearOutfits` on each member first |
| L4 | lists of Sim ids kept as numbers: dirty-name ids and the pet-added table (Household.cs:444, 2918), `AcademicCareer.sAcademicSims`, `RoleManager.sPreferredSimsForRoles`, the playground, sculpting and umbrella tables | stale id; the same person returns under the same id, so a stale entry points at the right Sim [inferred harmless] | evict test line 3 reads the ones that matter for play (roles, academics) |
| L5 | statics filled by play: graduation and field-trip lists (School.cs:1135, 1150), full-moon zombies, drink recipes, imaginary-friend and fake-autonomy destroy lists, thumbnail atlas queue, neighbour pet adoption (PetAdoption.cs:407), driver pools, future-descendant and almanac data | object kept alive | evict test: a member in any of them = promote. NRaas's own description cleanup handles the last four groups too (nraas-src `Helpers/Annihilation`, read for behaviour only) |
| L6 | `Relationship.Get(description)` (Relationship.cs:1289) adds an empty table entry for any description passed, also a disposed one; the table is saved | a stale holder that asks later re-adds the key | covered by C5 |
| L7 | household id counter moved by one per create | none | - |

Census for evict test line 3: 48 static fields hold a description, a household or a table of Sim ids (all classified
above), and **3,418 instance fields in 694 files** are typed `SimDescription`, `Household`, `MiniSimDescription` or
`Genealogy`. The instance holders are live objects (situations, interactions, parties, other Sims' careers and
wishes). They cannot be listed one by one, so line 3 cannot be a closed hand list (I-631-5 stays inferred). C5: P3
keeps a weak reference to every evicted description and household and counts the ones still alive after a full
collection (`seedEvictLeft`); the game's runtime has `WeakReference`. A non-zero count names the holder class
through the docs/343 diagnosis ring and that class joins the evict test.

**4. Promotion cost.** A transient household is already EA's: promoting it is "stop evicting", cost 0. Creating
from virtual is synchronous: none of the methods above sleeps or yields. (`Household.Add` and `Instantiate` do, with
a Sim object: docs/data/swap-stack-audit.csv.) Per household: 1 `Household` with 3 member lists and a native
script object. Per Sim: 1 description, about 17 owned objects from `Init` and the field initialisers (genealogy, managers and trackers, aging state,
outfit maps), 5-7 native outfit loads, traits and skills, and per pair of members 1 relationship with 5
sub-objects. About 60-120 managed allocations per Sim, 250-900 per household of 4-8 [inferred from the code read,
not measured; aged descriptions in saves hold 86-249 records, §5.1]. The part that grows with the town:
`MakeUniqueId` walks every household, the bin and **one world-wide urnstone query per Sim** (6223, the same query
docs/359 removes from `Find`). Times: P3. Refined in §6.5 item 6: the flow of C1 runs that test twice per
Sim; with the docs/359 detour it runs no world query per Sim.

**Conditions.**

- C1: `SimOutfit` constructor, id setter, `MakeUniqueId` again; `AddSilent` instead of `Add`; attraction check off.
- C2: one private field written by reflection (allergy), as `ts3fixes` already does elsewhere, or allergy left
  forgettable.
- C3: evict runs L1-L3 before EA's dispose.
- C4: bit 0 set in every generated id, own uniqueness test, `seedIdClash`.
- C5: weak-reference count after evict in P3; kill criterion 7 stays the gate.

Open after S1: the native step from `Destroy` to `Household.Dispose` (**read, §6.5 item 5: dispose runs one
script tick later, condition C6**); callees below one level under
`TraitManager.AddElement` and skill addition; EA 1.69; the cost of a pool outfit load.

### 6.5 S3 result (2026-11-26, static, Steam 1.67; EA 1.69 not read)

**Verdict: S3 passes for the design of the Q7 decision (§2.4 update), where the home copy keeps its Sim object. It
confirms the F0 result there and makes its "not at a save load" [verified].** Kill criterion 9 is met only by a form
that saves a member without a Sim object: EA never brings such a member back. Three results go to the hold census
F0b: EA has no freeze for a Sim whose world runs (item 3 lists every system and EA's own stop), relationship decay
is a daily pass and not an alarm, and EA's own park calls are saved state. Also here: the native step of `Destroy`
(item 5, new condition C6) and the promotion cost (item 6). Ledger: docs/51 §631, I-631-12, 13, 15, 16,
V-631-10..12, I-631-19..21.

**1. EA's travel return is a world switch for the whole household.** [verified]

- `ReturnFromVacationWorld` (GameStates.cs:1399) starts `SwitchWorlds` (1580): it exports the active household
  (1665), saves, and loads the home world. There `ImportTravellingHousehold` (2059), end-of-trip branch (2213-2330),
  finds the home copy of each exported description by id, merges travel data, buffs and pregnancy into it, imports the
  inventory, and instantiates the home copy only when it has no Sim object (2226-2234).
- A traveller id that is not in the export stays on the dead list (2064, 2221), and `PostTravelingFixUp` removes that
  Sim from the household and disposes it (2535-2553) [loop read, the condition around it not]. **A traveller left out
  of the return is deleted at home.**
- EA's "one Sim goes home early" (phone interaction, 196-260) returns nobody. The Sim leaves the household's Sim
  list, its motives are frozen, autonomy and pie menu are off, and its object is taken out of the world but kept. It
  joins the household again when everybody returns (1431-1438).
- The home world does not run during a trip. Members who stay home are disposed from the travelling copy and only
  counted (2130-2142). EA never has a traveller and a running home world at the same time.
- So there is no return "for one Sim". The Q7 design does not need one: it keeps the home copy and runs no return
  for the left-behind Sim. The path would only work with that Sim in the whole household's export, and then EA
  merges the trip into the home copy.

**2. A member without a Sim object while its world runs.** [verified]

| EA case | Marker in the save | Sim object | Sim panel | Comes back by |
|---|---|---|---|---|
| boarding school | enrolment record (SimDescription.cs:5295) | destroyed | grey entry with the school's name (HudModel.cs:5173-5188) | graduation or the remove call; without the pack `PostLoadFixUp` instantiates it at home (SimDescription.cs:3160-3168) |
| SimPort tour | flag and a return alarm owned by the description (5313-5319) | destroyed (HudModel.cs:5372-5379) | grey entry with the return time (5191-5200) | the alarm: instantiate off screen, walk home, reward and notice (5337-5347) |
| parents' free vacation | static table of ids and inventories (ParentsLeavingTownSituation.cs:1143) | destroyed | grey entry (HudModel.cs:5206-5222) | the situation: instantiate off screen, inventory restored, one paid day off (406-432) |
| early departure on a trip | id in the travel data | kept, out of world | grey in the relationship panel (HudModel.cs:132) | the end of the trip |

- A member with no Sim object and none of these markers gets no panel entry (`CreateSimInfo`, HudModel.cs:5321,
  5206-5213), and no load step instantiates it. All callers of `Instantiate` in the gameplay assemblies were listed:
  the only household-wide repairs are `PrepareToBecomeActiveHousehold` (Household.cs:4232, 4250-4274), which runs
  when the player picks a household to play (PlayFlowModel.cs:985, Edit Town), and the travel return above. **On a
  no-mod load such a Sim is in the household, invisible, and aged by the world's clock.**
- NRaas Overwatch has a nightly task that instantiates such members on their home lot (nraas-src
  `NRaasOverwatch/OverwatchSpace/Alarms/RecoverMissingSims.cs`, read for behaviour only). A hidden member would be
  put back by it; a member that has its Sim object is not touched.

**3. What advances a Sim in a running world, and how EA stops it.** The home copy of the Q7 design has a Sim
object, so both halves of the table apply to it.

| System | Lives on | Runs without a Sim object | EA's stop | Stop is saved |
|---|---|---|---|---|
| aging | description | yes: the day callback adds to every aging state (AgingManager.cs:1682-1700) [verified]; age-up and elder death then run without the Sim [callback not read] | whole world: off in vacation and university worlds (559-566). One Sim: the aging flag (SimDescription.cs:939-952, 3657-3669), or off the manager's list (V-631-9) | flag yes, list no |
| relationship decay | global table | yes: `DecayAll` walks every pair once a day (Relationship.cs:1142-1182, from SocialComponent.cs:2392) [verified] | only the Long Distance Friend trait, or boarding school on one side (1155, 1170) | both are saved |
| job and school | description | yes: the work alarms belong to the description; a day not worked is marked as skipped (Career.cs:2910-2917) [verified; effect on performance not read] | paid days off (Occupation.cs:3700); EA gives one on return from the free vacation | yes |
| motives | Sim object | no | freeze of every motive's decay (Motives.cs:1763; used for early departure, GameStates.cs:240, and a hosted SimPort Sim, Passport.cs:869), undone by `RestoreDecays` (1788) | yes [inferred: the motive class is persistable, Motive.cs:35, 2299] |
| autonomy | Sim object | no | disable counter (Autonomy.cs:2472; early departure, GameStates.cs:241) | yes: the counter is a saved field (Autonomy.cs:206) [verified]; no load step clears it [grep] |
| buffs, opportunities | Sim object | no | a trip moves them to the description before the save (GameStates.cs:230-237; F0) | yes |
| pregnancy | Sim object | no: the hourly alarm belongs to the Sim object (Pregnancy.cs:1776-1840) | none; EA keeps pregnant Sims out of the free vacation (ParentsLeavingTownSituation.cs:1199) | - |
| sickness | Sim object | no: alarms on the Sim object (HealthManager.cs:339-448) | none | - |
| bills | household | [inferred household level, not read] | - | - |
| celebrity decay, midlife crisis, occult timers | [not read] | | | |

- **EA freezes travellers by not running their world**, and by nothing else: the home world is a file while they are
  away and its clock goes on from the stored leave time (1345). In a running world EA has no freeze. Its three away
  cases of item 2 let aging, decay and work days run.
- "Held fully frozen exactly as EA does for travellers": **not by an EA mechanism.** Every hold on the home copy in
  a running main world is ours, per system. The table is the list F0b has to close.
- **Relationship decay is not an alarm.** "Alarms shifted at release" does not cover it: the daily pass lowers the
  values between the home copy and every Sim of the main world, the played household included. EA's two stops are
  saved markers and change the Sim. The hold has to keep the pass off the home copy's pairs or put the values back
  at release [design open, F0b].
- **EA's park calls are saved state.** A save written while the home copy has frozen motives and autonomy off gives
  a no-mod load a home Sim that never gets hungry and never acts. So the park either uses nothing that is saved, or
  the save hub undoes it before the native save and redoes it after (counter `seedParkInSave`, must be 0). The same
  holds for paid days off and the aging flag; the aging list of V-631-9 is the one stop that is not saved.
- A parked Sim that is pregnant or sick keeps its Sim-object alarms running: EA has no stop. F0b needs one, or the
  split is refused for such a Sim, as EA does for the free vacation.

**4. Consequence for §2.4.**

- N1 for the left-behind Sim rests on the home copy having a Sim object (F0, V-631-8). A design that destroys it
  and counts on EA to recreate it fails: kill criterion 9.
- The no-mod state of that Sim is only as good as the save hub's undo of the park (item 3).
- NRaas Overwatch does not touch a member that has a Sim object, so the kept home copy is safe from its nightly
  repair (item 2) [inferred from its test: it compares the Sim list with the description list].

**5. The native step of `Destroy`.** [verified disassembly unless marked]

- `Household.Destroy` is `ScriptObject.Destroy` (ScriptObject.cs:78-82): `World_RemoveObjectFromObjectManager`
  (`0x784E30` -> `0x798D40`), then `IC_Simulator_DestroyObjectImpl` `0x76D120`.
- `0x76D120` takes the host interface (`[0x11D4BBC]`, slot `+0x17C` = `0xD7CFD0`: host = interface - 8) and calls
  host slot `+0xC` = `0xD70B80`. That destroys the world object if the object service has one for the id
  (`0xD70EB0` -> `0x939560`, then `ObjectService_DestroyById` `0x939420`) and calls interface slot `+0x10C` =
  `0xD745A0`.
- `0xD745A0` destroys nothing. It puts the id on the pending list at host `+0xC04` (the exclusion list of docs/122)
  when a handle exists in the map `+0x1660` or `+0x1680`.
- `ScriptHost_ScriptTick` `0xD80BC0` drains the list (loop `0xD80E9E`-`0xD814E7`). Per id: erase from the host maps
  `+0x1764`, `+0x1728` (and its tree `+0x1748`; one native reference is released after the loop) and `+0x1680`;
  `0xD80460` calls `ITask.Stop` once (method slot host `+0x1624`) and erases `+0x16A0` and `+0x16E0`; `0xD74B20`
  calls **`IDisposable.Dispose`** (slot `+0x1658`) through `mono_runtime_invoke` and then `Simulator.DerefObj` (slot
  `+0x1644`), which drops the managed root (ScriptCore Simulator.cs:210); last the handle in `+0x1660` and the
  16-byte list node. An id that is still on the pending-add list (`+0xBD8`, map `+0xBE4`) is disposed from there
  (`0xD8126C`). An exception from `Dispose` goes to `0xD74100` [not read] and the teardown goes on.
- Released for one household: its handle; the two shared-inventory script objects (Household.cs:1443, 1451) and per
  description the packing task (SimDescription.cs:4044), all by the same path. No id is given back: object ids come
  from the clock generator (§6.4 item 2) and there is no pool.
- **No native leak found** in the bodies read [the helpers `0xD74A10`, `0xD73640`, `0xD72680` and the map erase
  routines were not read]. A `Household` has no world object [inferred: it is added with `Simulator.AddObject` only,
  Household.cs:1897], so the world side has nothing to free.
- **Dispose is deferred to the next script tick.** When `Destroy` returns, the household is still in the household
  list with every holder of §6.4 item 3 intact. An evict and a save in the same task would write the evicted
  household. **Condition C6:** an evict ends at least one script tick before the save hub's native save call; the hub
  checks that no evicted household is still listed (`seedEvictPending`, must be 0; else the save waits a tick, or is
  refused by the rule of docs/353 §7). The weak-reference count of C5 is taken after a tick too.

**6. Promotion cost: the urnstone query.**

- The flow of C1 runs EA's full uniqueness test **twice per Sim**: once inside `Init` on the id EA has just drawn
  (SimDescription.cs:3077), once in the second `MakeUniqueId`. Each test is the mini-description lookup, a walk of
  every household, the bin, and one world-wide urnstone query (6190-6233) [verified].
- The second one can go. With the id kept, `MakeUniqueId` does nothing but reset the owner id of three managers
  (6235-6258), and those resets are public (CelebrityManager.cs:704, PetManager.cs:134, TraitChipManager.cs:171). So:
  id setter, our own test, the three resets.
- The first one is inside EA's constructor. Every public constructor that makes a valid description runs `Init`
  (2952-2965, 3012-3016); the empty one (2944) skips it but leaves the private valid flag (658) unset. It cannot be
  skipped without a hook. The id it tests is thrown away one step later, so its result does not matter.
- **Replace it through docs/359.** That build already detours `MakeUniqueId` as an id writer (branch
  `feature/simdesc-index-1126`, `SimDescIndex.cs:80`, not on master). Inside a create window of `G` the detour sets
  the generated id, answers the test from the index (id -> description, exact by docs/359 §2), and does the three
  resets. EA's generator is then not called at all, which is safe: its state is its own (V-631-2). World queries per
  Sim: 0.
- The urn part cannot simply be dropped. An urn can hold a generated id (bit 0 set, C4) only for a promoted Sim
  that died; with the sidecar intact that household is in the durable set and `G` does not create it again, and with
  the sidecar lost the new seed gives other ids [inferred]. It stays as a guard at the price docs/359 §4 already
  pays: its urn set, or without that build EA's query **once per household**, the array reused for every member.
- Without docs/359: one EA test per Sim (inside `Init`) plus our own test, half of C1's cost.

## 7. Open

| # | Question | Who |
|---|---|---|
| Q1 | **Closed (Rodney 2026-11-26).** Without the mod, Sims are back in the main world and relationships to far Sims are removed if necessary. Design in §2.4; far towns without residents follow from it | - |
| Q6 | **Closed (Rodney 2026-11-26): an EA-size main world in every set, regions as separate world files; Atlas docs/022 §2-5.** Left open there: saves of the seamless block mode with a household Sim outside the main world (docs/022 Q-A). Question was: The guarantee needs the main world to load in an unmodded game. That holds for level 2 with a main world of EA size and for level 1 worlds up to the size vanilla loads; a single 8192 m world cannot load unpaged at all (Atlas docs/007 §0), so it has no no-mod path whatever we write. Accept that for 8192 m level 1, or require a main world of EA size? | Rodney |
| Q7 | **Closed (Rodney 2026-11-26): frozen until fetched, exact; Atlas docs/022 §6.** Question was: A left-behind Sim's record sits in the main world's household. When the main world runs while that Sim is away, EA's clock there applies to it (aging). docs/019 §4.3 wanted it frozen with its region. Accept aging by the main world's clock, or hold the main world's play until the Sim is fetched? Needs S3 first | Rodney, after S3 |
| Q2 | Is choosing `SimDescriptionId` values ourselves inside "never write the `.sims3` differently than EA would"? Taken as yes: EA writes the same field with a random value. If no, ids come from EA and a `Find` miss cannot be resolved (the safety net of §3 goes, the evict test stays) | Rodney, after S1 |
| Q3 | Cross-household life in the generator (marriage, births, moves) as closed forms of `t`: later version, new `pg` | builder, after P3 |
| Q4 | Demotion of a promoted household that the player has not touched for long: not designed, would need proof that the durable set is back at `G` | - |
| Q5 | Service NPCs, role Sims and homeless townies stay EA's (docs/324 §5.1); whether far regions need their own role Sims | P4 |

## 8. Method

- §5.1: scratch script over `Documents\Electronic Arts\The Sims 3\Saves\Baseline-*`, using the repo's `readObjs`
  and `readIndex`; byte sums per class checked against the record total by assertion. Not committed (scratch).
- Everything else: reading the docs listed at the top. No address, no decompiled method was read new for this doc;
  claims about EA code are cited from those docs or marked [inferred] and listed in docs/51 §631.
- §6.4 (S1): the local decompile of the Steam 1.67 script assemblies, a scratch method index (search hits mapped to
  their enclosing method) and a field census by declared type; capstone disassembly of `0x996B90`, `0x7CFC00`,
  `0x7CF930` and `0x7CFAC0` in the `game-copy` exe. Nothing run in game.
- §6.5 (S3): the same decompile (travel states, household, HUD model, aging, relationship, career and motive
  files; every caller of `Instantiate` listed by grep); capstone disassembly of `0x76D120`, `0x784E30`,
  `0xD70B80`, `0xD70EB0`, `0xD745A0`, `0xD80460`, `0xD74B20`, the drain loop of `0xD80BC0` and the method-slot
  setup in `0xD7E560`; host vtables `0x108B690` and `0x108B818` read from the PE; docs/359 read from its branch.
  Nothing run in game.
