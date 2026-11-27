# 326. How other big-world games simulate what the player is not near, and what TS3 can take (2026-11-22)

Status: research only (web, papers, our own docs). Nothing built or run. Feeds the far-Sim model (docs/324, branch
`feature/far-sim-model-1122`) and docs/320 §5.4.

Tags:
- [dev] = the developer said it (talk, blog, official wiki or docs);
- [paper] = peer-reviewed or published paper, read by us;
- [community] = wiki, forum or modder report, not confirmed by the developer;
- [inferred] = our reasoning, not confirmed;
- [verified] / [measured] = from our own docs (cited).

Our starting point (not re-researched here):
- NPC Sims beyond 150 m from any viewer point use 85-89% of NPC sim-thread time.
- Each Sim costs about 4 ms/s wherever it is: cost follows the Sim count, not the distance (docs/322 §3) [measured].
- Draft far-Sim model:
  - far NPCs run on a seeded state machine, with a sidecar event log;
  - they snap to the computed state on approach, travel or a due visit;
  - visitors and cars stay real;
  - stage 2 keeps far Sims as `SimDescription` data only and re-instantiates them through a budgeted queue;
  - household Sims are always fully simulated.

## 1. Sources

Each entry has four parts:
- **What**: what they do, with the source.
- **Technique**: the method in plain words.
- **Fit**: whether it fits our constraints (32-bit, one sim thread, Mono scripts, household untouched, no mod data in
  the save, sidecar files allowed), and how.
- **Design**: what it changes in the far-Sim model.

### 1.1 Bethesda (Oblivion Radiant AI, Fallout, Skyrim)

**What.** Actors have processing levels.
- High: in or near the player's cell, updated every frame.
- Middle-high and middle-low: updated every few seconds to minutes.
- Low: persistent actors in unloaded cells.
A community plugin doc gives about 18 s for middle-high and about 120 s for low [community]. Non-persistent actors in
unloaded cells, and actors flagged "No low level processing", run no AI at all until the player enters their cell
[community]. Each actor runs the first AI package whose time window and conditions match (a day schedule).
`MoveToPackageLocation` places an actor where its current package says it should be [dev]. Modders report two problems:
- off-screen actors on travel packages arrive instantly;
- actors in a cell entered by console sit idle until their package is evaluated again.
Sources: [UESP Script Processing talk](https://cs.uesp.net/wiki/Talk:Script_Processing),
[GetActorProcessingLevel](https://geckwiki.com/index.php/GetActorProcessingLevel),
[UESP Performance Problems](https://cs.uesp.net/wiki/Performance_Problems),
[CK MoveToPackageLocation](https://ck.uesp.net/wiki/MoveToPackageLocation_-_Actor),
[travel package teleports](https://forums.nexusmods.com/topic/11305983-spawning-offscreen-with-a-travel-package-instantly-teleports-to-destination).

**Technique.**
- Several update rates chosen by distance.
- Unloaded actors follow a coarse schedule.
- When the cell loads, the actor is placed at the spot its schedule says.
- Unimportant actors get no off-screen simulation at all.

**Fit.** Good. EA's Sim already has a day shape: job hours, school, sleep, rabbit holes. We can only place a Sim on its
schedule location when nobody sees the move (no visible teleports). Persistent versus non-persistent maps to named Sims
versus purely ambient roles [inferred].

**Design.**
- Confirms snap on approach.
- Adds a middle tier with a slow cadence. Bethesda has four rates, not two.
- Adds the rule that a snap target must be checked to be reachable before it is used. Bethesda's "find a navmeshed
  spot" behaviour is only community-reported.

### 1.2 Kingdom Come: Deliverance 1 and 2 (Warhorse)

**What.** GDC 2026 talk "Supporting Thousands of NPCs" by Petr Smrcek. NPCs keep their daily routines when the player
is long gone. New AI-simulation level-of-detail techniques keep frame time and memory low, and the scripting interface
stays "as oblivious to the LODs as possible" [dev]. Only the session summary was available, not the slides.
Sources: [GDC Vault](https://gdcvault.com/play/1035718/Supporting-Thousands-of-NPCs-in),
[KCD2 routines interview](https://wccftech.com/kingdom-come-deliverance-2-dev-talks-about-dynamic-npc-routines-and-emergent-storytelling-for-side-quests/).

**Technique.** AI LOD hidden behind the script API. Quest and story scripts never ask "what LOD is this NPC in?"; they
get a consistent answer at every level.

**Fit.** Very relevant. EA's Mono scripts query far Sims all the time: story progression, phone calls, invitations,
opportunities, `IsAtWork`, current interaction, motives. If a far Sim is parked, those reads must still return
believable values [inferred].

**Design.** Adds a requirement: a script-facing facade. Every EA field or call that other code reads about a far Sim
either stays live or returns the model's computed value. The far-Sim model must list those reads; static analysis can
find them.

### 1.3 Red Dead Redemption 2, GTA, Assassin's Creed Unity

**What.**
- **GTA.** Population density comes from `popcycle.dat` data: for each zone, a maximum number of pedestrians and cars
  per two-hour slot, weekday and weekend, and a mix of ped groups [community; modder docs of game files]. Ambient
  pedestrians spawn near the player and despawn out of view; story characters are separate and persist [community].
- **RDR2.** No Rockstar talk on population density was found. The GDC talks cover animals, horses and locomotion.
- **AC Unity.** "Massive Crowd ... AI Recycling" (GDC 2015, Cournoyer): an AI LOD with a budget of 40 real AIs and 120
  high-resolution models carries about 10,000 crowd NPCs. NPCs are recycled from a pool, and low-detail NPCs are
  swapped for high-detail ones without the player noticing [dev, session abstract].
Sources: [popcycle.dat guide](https://red-ita.gitbook.io/learn-more/popcycle.dat),
[AC Unity GDC Vault](https://www.gdcvault.com/play/1022141/Massive-Crowd-on-Assassin-s),
[RDR2 animals GDC](https://gdconf.com/article/learn-how-rockstar-breathed-life-into-the-wildlife-of-red-dead-redemption-2-at-gdc/).

**Technique.**
- A fixed budget of fully simulated agents around the camera.
- Density by zone and time of day.
- Agents that are not persistent come and go.
- Upgrades from low to high detail happen only where the player cannot see.

**Fit.**
- **Partial.** TS3 townies are named and persistent, so we cannot despawn them.
- **Hard budget: fits.** A hard budget for "real" NPCs (for example N fully simulated NPCs, ranked by importance)
  matches stage 2 [inferred].
- **Pooling Sim instances: does not fit.** A TS3 Sim object is tied to its `SimDescription`, outfits and rig.
  Reusing an instance for another Sim is not realistic [inferred].
- **Purely ambient roles.** Walk-bys, joggers and idle service NPCs can follow a "near viewers only" density rule
  [inferred].

**Design.**
- Adds a hard cap on fully simulated NPCs, as a safety net on top of distance.
- Adds the rule "upgrade only while unseen".
- Keeps "visitors and cars stay real".

### 1.4 Minecraft

**What.**
- Render distance decides what is drawn; simulation distance decides what is ticked.
- Mobs, crops, water and redstone stop outside the simulation distance [dev, Microsoft Learn].
- In Java Edition, chunks inside the simulation distance square are ticked fully. Chunks loaded some other way still
  get random ticks, so crops grow there [community, Minecraft Wiki].
- Unloaded chunks do not catch up in vanilla [community]. The "Unloaded Activity" mod stores the last tick time and
  computes the state directly from the elapsed ticks (crops, furnaces), with no replay [community].
Sources: [Microsoft Learn: simulation distance](https://learn.microsoft.com/en-us/minecraft/creator/documents/simulationrenderdistanceguide),
[Tick](https://minecraft.wiki/w/Tick), [Chunk](https://minecraft.wiki/w/Chunk),
[Unloaded Activity](https://modrinth.com/mod/unloaded-activity).

**Technique.**
- Two radii: draw and simulate.
- Outside the simulate radius the world freezes.
- Catch-up is a closed-form formula of elapsed time: "state at t + dt" computed once.

**Fit.** Very good. Needs, energy, hunger and the time left in a job shift are all closed-form functions of elapsed
time for an NPC that is not interacting. Decay rates are EA tuning that we can read [inferred].

**Design.**
- Confirms the seeded state machine.
- Sharpens it: far Sims do no per-tick work at all. Their state is computed only when it is read or when they snap.
- Warns against the vanilla default: a frozen world with no catch-up.

### 1.5 Dwarf Fortress

**What.**
- During play, world activities run at world-map level. Historical figures marry, have children, take jobs and die.
  Armies, bandits and questers travel on the map. Caravans, diplomats, migrants and megabeasts simply teleport to the
  site [community, DF wiki].
- Tarn Adams's design rule: "Operate at the level of what the player sees or one layer below" [dev, Game AI Pro 2,
  ch. 41].
Sources: [World activities](https://dwarffortresswiki.org/index.php/World_activities),
[Simulation Principles from Dwarf Fortress](http://www.gameaipro.com/GameAIPro2/GameAIPro2_Chapter41_Simulation_Principles_from_Dwarf_Fortress.pdf).

**Technique.**
- Two levels: a world map of abstract figures and groups, and a detailed local site.
- Each kind of arrival either travels for real or teleports, by design choice.
- Model only one layer below what the player can see.

**Fit.** Good. "One layer below what is seen" is a good scope rule for the far-Sim state:
- keep: where the Sim is (lot or rabbit hole), what kind of activity, motives in bands, money and job;
- skip: per-interaction detail.
Arrivals at the household lot (visitors) stay real, which matches "visitors stay real".

**Design.**
- Confirms the scope of the computed state.
- Adds a per-arrival-type choice: walk in from off-screen or snap in, written down for each arrival kind.

### 1.6 RimWorld world pawns

**What.** Pawns that leave the map become world pawns. Community reports describe three states:
- alive: fully ticked;
- mothballed: stored, with only age updated about every quarter day;
- discarded.
A periodic garbage collector removes pawns that do not matter. It tries to keep pawns related to colonists and pawns
used in tales. Mods with more aggressive cleanup have deleted pawns that were still needed and left dangling
references [community]. No developer source was found.
Sources: [Steam discussion](https://steamcommunity.com/app/294100/discussions/0/3810655055431734476),
[Better GC mod comments](https://steamcommunity.com/sharedfiles/filedetails/comments/2982026860).

**Technique.**
- Importance comes from relationships to the player's people.
- Unimportant pawns are stored with minimal updates (age only).
- Garbage collection by importance.

**Fit.**
- Relationship-based importance fits well: friends, romance, family or coworkers of household Sims.
- Deleting does not fit. We never delete Sims (household rule, no save change).

**Design.**
- Adds a priority input: relationship to the household raises a Sim's tier or promotion priority.
- Confirms the data-only tier: a mothballed Sim is the same as stage 2.
- The GC failures argue against any culling.

### 1.7 X3 / X4: Foundations (Egosoft), out-of-sector simulation

**What.**
- The Egosoft-hosted wiki describes two modes. High attention is the full simulation near the player. Low attention
  approximates the outcome of distant fights cheaply [dev wiki].
- Players report a 30 s step for low-attention movement and combat (5 s while that sector's map is open) and damage
  rolled from probability tables [community].
- Players report that outcomes differ between the two modes: fleets sit still out of sector, and some weapons work in
  only one mode [community].
Sources: [lowattentioncombat thread](https://forum.egosoft.com/viewtopic.php?p=5003861),
[OOS combat resolution](https://forum.egosoft.com/viewtopic.php?p=2474200).

**Technique.**
- A coarse fixed step for everything far away.
- Results come from an aggregate formula instead of the detailed simulation.
- Watching a sector (map open) raises its rate.

**Fit.** The idea of raising a Sim's tier while the player looks at it fits: map view, the relationship panel or a
phone call can promote a Sim. The coarse step fits the reduced tier.

**Design.**
- Adds "observation raises the tier": map view, Sim info panel and phone calls are viewers too.
- Adds a hard requirement from X4's player complaints: check the far model against the full simulation (§3, pitfall
  P4).

### 1.8 Elite Dangerous and No Man's Sky: seed-based universes

**What.**
- **Elite.** Stellar Forge builds each system from its coordinates and a seeded random generator, with real star
  catalogues as input [dev interviews; community for "nothing else stored"]. Faction state runs as a separate
  background simulation. It updates once a day, and player actions are applied at that update [community; dev notices
  on tick fixes].
- **No Man's Sky.** Sean Murray: "every planet has a single number, a random seed" [dev, quoted by press]. Player
  changes are kept separately from the generated planet [dev, details not public].
Sources: [80.lv on Stellar Forge](https://80.lv/articles/generating-the-universe-in-elite-dangerous/),
[BGS issues notice](https://forums.frontier.co.uk/threads/elite-dangerous-action-taken-to-address-recent-bgs-issues.611225),
[MIT Technology Review on NMS](https://www.technologyreview.com/2014/07/22/12940/no-mans-sky-a-vast-game-crafted-by-algorithms/).

**Technique.**
- Content is a pure function of (seed, position).
- Only player-caused changes are stored.
- A slow abstract simulation runs on a fixed daily update.

**Fit.** Very good. A far Sim's day is a pure function of (Sim id, day number, world seed, traits, job). The sidecar
stores only the changes: the event log. TS3 already runs EA story progression as a daily update at 1 AM (docs/588 §1)
[verified].

**Design.**
- Confirms the seeded state machine and the sidecar event log.
- Adds a version number on the generator, stored in the sidecar. A changed formula must not rewrite a Sim's past
  after an update.
- Adds one fixed time for applying catch-up (pitfall P8).

### 1.9 Star Citizen: object container streaming, server meshing, entity graph

**What.** From CIG's Comm-Link Q&A [dev]:
- the entity graph is a database of the state of every replicated entity;
- persistent streaming writes entity state into it;
- streaming loads and unloads entities into a server as the area is needed;
- each entity is owned by exactly one server at a time, and ownership is handed over explicitly.
Sources: [Server Meshing and Persistent Streaming Q&A (Comm-Link mirror)](https://api.star-citizen.wiki/comm-links/18397),
[Object Container Streaming](https://starcitizen.tools/Object_Container_Streaming).

**Technique.**
- Separate stored state (the database) from live objects (streamed in).
- One owner per entity.
- Explicit handover when the owner changes.

**Fit.**
- Fits as a rule, not as tech.
- Stage 2 is already "the `SimDescription` is the database, the `Sim` object is the streamed-in instance".
- The single-owner rule is what keeps far and full simulation from both advancing the same Sim [inferred].

**Design.** Adds the single-authority rule:
- a Sim is advanced by either EA's simulation or the far model, never both;
- the handover runs at a safe point (no running interaction, or one cancelled cleanly) and is logged.
This matches EA alarms and story progression too: they must not fire on a Sim whose state the model owns, or they must
go through the facade (§1.2).

### 1.10 Mount & Blade II: Bannerlord, Crusader Kings (Paradox)

**What.**
- **Bannerlord.** Parties move on an abstract campaign map. Battles the player does not fight are resolved by a
  simulation that applies casualties step by step and shows a live report [dev, TaleWorlds blog]. Players complained
  that the results were unfair compared with fought battles; later patches improved it [community/press].
- **CK2.** A developer optimisation diary notes that a 400-year save held about 30,000 living characters, about twice
  the test peak [dev].
Sources: [Bannerlord dev blog](https://taleworlds.com/en/Games/Bannerlord/Blog/69),
[auto-resolve criticism](https://www.gamepressure.com/newsroom/mount-and-blade-2-bannerlord-auto-resolve-annoys-players/zd1c18),
[CK2 dev diary 18](https://forum.paradoxplaza.com/forum/threads/ck2-dev-diary-18-optimization-and-modding.962057/).

**Technique.**
- Abstract outcomes for everything off-screen.
- Character counts grow without bound unless something trims them.

**Fit.** Calibration is the lesson. An abstract result that is biased against the player feels like a bug. For TS3 the
bias would show as Sims who never come to work or never get hungry. Population growth is EA story progression's job,
not ours.

**Design.**
- Adds a calibration step: compare model and EA results on the same Sims.
- Confirms that we do not touch population size.

### 1.11 The Sims 4 and The Sims 3 itself

**What.**
- **Sims 4.**
  - Neighborhood Stories moves unplayed households through careers, retirement, move-ins and move-outs, with per-group
    toggles. "My Households" are off by default [dev, EA news post].
  - Players report culling: at a world Sim cap, whole unplayed households are removed, Sims with relationships to
    played Sims are kept first, and favourites have been lost [community].
- **Sims 3.** EA story progression runs once a day at 1 AM (once a minute in accelerated mode) and picks a few actions
  by demographic error (docs/588 §1) [verified].
  - It targets NPC households only (`SelectRandom*` skips the active and travel households), with one known stale-guard
    exception (#7) [verified].
  - It keeps "virtual home" slots on lots (docs/588 C1, C3) [verified].
  - Many of its rules fail because its demographics count the world wrong (docs/588 §2) [verified].
Sources: [EA: Neighborhood Stories](https://www.ea.com/games/the-sims/the-sims-4/news/neighborhood-stories-system),
[TS4 culling reports](https://forums.ea.com/discussions/the-sims-4-gameplay-en/unplayed-households-disappear-from-the-world--lots-become-empty/12707135),
[Sims Wiki: Story progression](https://www.thesimswiki.com/wiki/Story_progression), docs/588.

**Technique.**
- Life events run on a slow daily abstract simulation with no instantiation needed.
- Sims 4 also caps population by culling.

**Fit.** TS3 already splits life events (story progression, daily) from moment-to-moment simulation (Sim tasks,
autonomy). The far-Sim model replaces only the moment-to-moment part for far Sims. Culling is out (no save change,
household rule).

**Design.**
- Confirms that the far model does not own life events (aging, jobs, move-ins); EA story progression keeps them.
- Adds a check: story progression reads demographics over all Sims. Its reads of far Sims (location, money, job) must
  come through the facade (§1.2), or it acts on stale data.

### 1.12 SimCity (GlassBox) and Cities: Skylines

**What.**
- **GlassBox** (SimCity 2013) [dev]:
  - simulation state lives in units, resources, maps and globals, tied together by script rules;
  - agents (people, cars, water) are kept simple and only carry resources between units, so that over 10,000 can run
    at once;
  - this reverses older SimCity games, where statistics drove the picture.
- **Cities: Skylines.** Players read the limits as about 1M citizens (data), but only 65,536 "citizen instances". An
  instance exists only while a citizen is travelling or out; inside a building it is released [community].
Sources: [Inside GlassBox (talk page)](https://www.andrewwillmott.com/talks/inside-glassbox),
[GDC 2012 GlassBox write-up](https://gamedeveloper.com/design/gdc-2012-breaking-down-em-simcity-em-s-glassbox-engine),
[Cities: Skylines instance limits](https://steamcommunity.com/app/255710/discussions/0/6406956248977521381).

**Technique.**
- Separate the data record (cheap, many) from the moving instance (expensive, few).
- An instance exists only when the agent is in transit or visible.

**Fit.** This is stage 2 almost exactly: `SimDescription` = citizen, `Sim` = citizen instance. A Sim inside a rabbit
hole or a far lot needs no instance [inferred]. The open question is instantiation cost (docs/325, inst-probe).

**Design.** Confirms stage 2. Adds the trigger "in a rabbit hole or indoors on an unseen lot" as the cheapest first
data-only case.

### 1.13 Research: simulation LOD and LOD AI

**IVE, Charles University (Šerý, Poch, Šafrata, Brom 2006; Brom, Šerý, Poch 2007)** [paper, read].
- **Location tree.** The world is a tree of locations (kingdom, town, district, house). An elastic "membrane" cuts the
  tree. Only locations above it exist, and the framework pushes it toward low detail.
- **Two levels per object.** Each object has an existence level (below it, the object is not simulated) and a view
  level (it forces detail where the object is). The player's avatar has a low existence level and a high view level.
- **Process states.** A process (eat, cook, fetch) is not-existing, atomic or expanded.
  - Atomic: it waits until its finish time, then changes the world in one step.
  - Shrinking a running process: it is stopped, "partially evaluated", then discarded.
- **Smooth borders.** "LOD influence" between neighbouring locations makes a crater instead of a narrow hole, so places
  near the player get ready before the player arrives.
- **No thrash.** Locations expand at once when needed, but shrink only in a periodic cleanup pass.
Sources: [IVE-LOD-2006.pdf](https://artemis.ms.mff.cuni.cz/main/papers/IVE-LOD-2006.pdf),
[IVA 2007 chapter](https://www.springerprofessional.de/en/simulation-level-of-detail-for-virtual-humans/2882154).

**Sunshine-Hill and Badler, "Perceptually Realistic Behavior through Alibi Generation" (AIIDE 2010)** [paper,
abstract read].
- Simulate only a small part of the world with cheap approximations.
- Generate an agent's "alibi" (goal and backstory) only when it is observed.
- The results are statistically "perceptually indistinguishable" from the full simulation.
Source: [AIIDE 2010](https://ojs.aaai.org/index.php/AIIDE/article/view/12389).

**Sunshine-Hill, "Managing Simulation Level-of-Detail with the LOD Trader" (MIG 2013).** We found the title and venue
only (DOI 10.1145/2522628.2541250); the content was not read. The title suggests picking each agent's LOD under a
budget, but that is [inferred] from the title. Source: [researchr](https://researchr.org/publication/Sunshine-Hill13/bibtex).

**Wright and Marshall, "More AI in Less Processor Time: 'Egocentric' AI"** (Gamasutra 2000), cited by IVE. The article
was not found online [not read].

**Technique.**
- A location hierarchy with hysteresis.
- Pre-expand around the player (the crater).
- Partial evaluation when detail drops.
- Make state up only when someone looks (alibi).

**Fit.** All four fit TS3:
- **Hierarchy.** It maps to world > neighbourhood area > lot > room.
- **Crater.** It maps to the lots next to the camera target and the household's destination.
- **Partial evaluation.** Demoting a Sim mid-interaction must credit part of the effect: half a meal eaten, half a work
  shift.
- **Alibi.** It is the lazy "compute on promotion" idea with a statistical safety net.

**Design.** Adds:
- hysteresis: expand at once, shrink only in a periodic pass;
- pre-expansion of neighbouring lots;
- partial-effect crediting on demotion;
- distributional checks (P4).

## 2. Ideas to adopt, ranked

Benefit is in sim-thread ms/s of today's NPC cost: docs/322 has 228-294 ms/s total NPC time, measured with heavy
probes on. Shares are reliable; absolute values are high because of probe overhead. Effort is wall-clock time for
Claude with agents and Rodney. Game runs are listed separately.

| # | Idea (source) | Benefit | Risk | Effort |
|---|---|---|---|---|
| 1 | **Lazy far state** (Minecraft catch-up, alibi, IVE atomic process): far Sims do no per-tick work; state = f(seed, elapsed time) is computed only when read or snapped | Largest: up to the >400 m band share (BP 64%, IP 77% of NPC time), and more once the 150-400 m band joins [inferred from docs/322 shares] | Medium: everything rests on the facade (#3) and the handover (#2) | Core of docs/324: about 1-2 days of agent builds; 3-4 game runs |
| 2 | **Single owner and safe-point handover** (Star Citizen authority, IVE partial evaluation): a Sim is advanced by EA or by the model, never both; demotion waits for a safe point or cancels cleanly and credits partial effects | Prevents double-advanced motives and duplicate alarms; makes the rest debuggable | Low on its own, high if skipped | 2-4 h build, 1 correctness run |
| 3 | **Script-facing facade** (KCD "scripts oblivious"; TS3 story progression reads): EA reads of a far Sim (location, at work, motives, current interaction, availability for calls) return the model's values | Needed for correctness: invitations, phone calls, opportunities and story progression behave the same | Medium: the list of reads is long; a static census first | 2-3 h census (static agent), then 4-8 h build |
| 4 | **Hysteresis and pre-expand ring** (IVE crater and periodic shrink, Bethesda cell buffer): promote at r_in, demote only beyond r_out after a dwell time, in a periodic pass; pre-promote lots near the camera target and along camera motion | Removes boundary thrash hitches; hides snaps | Low | 1-2 h; 1 shadow run |
| 5 | **Budgeted promotion queue, ranked by time-to-view** (AC Unity budget, LOD Trader title, Cities instances): at most K snaps or instantiations per frame; priority = predicted time until visible, relationship weight, due visits | Spreads instantiation cost; caps hitches | Medium: needs docs/325 costs | 2-4 h on top of stage 2 |
| 6 | **Relationship-weighted importance** (RimWorld protection, TS4 culling priority): Sims with family, romance, best-friend or coworker ties to the household, or pending opportunities or calls, stay one tier higher | Fewer visible oddities where the player cares most; small cost | Low | 1 h |
| 7 | **Observation raises the tier** (X4 map-open rate): map view, Sim info or relationship panel, phone call, hover and Edit Town count as viewers | Catches "looked at a far Sim and it was frozen" | Low | 1-2 h, needs UI hook points |
| 8 | **Versioned seed function and delta-only sidecar** (Elite, NMS): the day plan is f(SimDescriptionId, day, world seed, generator version); the sidecar keeps only events and the version | Repeatable answers, small sidecar, safe updates | Low | Part of #1; +30 min |
| 9 | **Calibration against full simulation** (X4 and Bannerlord complaints): a shadow mode runs EA for a sample of far Sims and compares the model's snap state | Proves "realistic"; catches bias before players do | Low (measurement only) | 2-3 h build; 2 runs of 10 min per world |
| 10 | **Reduced middle tier with a coarse step** (Bethesda middle levels, X4 30 s step, RimWorld mothball): 150-400 m Sims keep EA's simulation but run Sim tasks and autonomy at 1/N | Recovers part of the 150-400 m band (SV 73% of NPC time) where a full snap is too close | Medium: EA tasks are coroutines; cadence skips can stall waits (our earlier unseen throttles did not lower per-Sim cost, docs/322) | 4-6 h; 2 runs |
| 11 | **Per-arrival-type snap or walk-in** (DF teleporting caravans versus travelling armies, Bethesda placement): each arrival kind (visitor, coworker, service NPC, party guest) is marked "walk from lot edge / rabbit hole" or "may snap unseen"; snap targets are route-checked | No pop-in at the household lot | Low | 1-2 h |
| 12 | **Ambient roles near viewers only** (GTA popcycle density by zone and time): walk-by, jogger and idle-service roles fill only near viewers, by time of day | Small extra saving in busy worlds | Medium: TS3 ambient Sims are often named townies; only roles, never the Sims | 2-3 h; later |

Order of work:
1. #3 census.
2. #2 and #8 inside #1.
3. #4 and #6.
4. #9 shadow before any timing.
5. #5 with stage 2.
6. #7, #11, #10, #12 after the first timing.

## 3. Pitfalls other games hit

| # | Pitfall | Seen in | Our guard |
|---|---|---|---|
| P1 | Visible pop-in or teleport at snap | Bethesda travel packages arrive instantly; AC Unity swaps only unseen [community / dev] | Snap only while unseen; walk-in for arrivals (#11); pre-expand ring (#4) |
| P2 | Thrash at the tier border: a Sim near r flips every frame | IVE paper (the reason for periodic shrink) [paper] | Hysteresis: r_in < r_out, dwell time, shrink in a periodic pass |
| P3 | Frozen world: far things stop and never catch up | Minecraft vanilla; Bethesda NPCs without low-level processing [community] | Closed-form catch-up from elapsed time (#1) |
| P4 | Outcome bias between tiers: far results differ from what the full simulation would give | X4 out-of-sector fleets, Bannerlord auto-resolve [community/press] | Shadow calibration (#9): distributions of motives, location and attendance |
| P5 | Save or population bloat, and cleanup that deletes what matters | CK2 30k characters [dev]; RimWorld GC mods deleting needed pawns, TS4 culling favourites [community] | No culling ever; sidecar only, deltas only (#8), with a size cap and pruning of old events |
| P6 | Two owners advance one entity | Star Citizen's reason for one-owner authority [dev] | Single owner with handover (#2); EA alarms on model-owned Sims are deferred or routed through the facade |
| P7 | Scripts see the LOD: quest or story code reads stale state | KCD's stated design goal implies the risk [dev] | Facade (#3) plus the census of reads |
| P8 | Catch-up applied unevenly in time | Elite's daily update not happening everywhere at once [community / dev notices] | One defined apply point: at snap, or at one daily slot next to EA story progression's 1 AM, never both |
| P9 | A changed generator rewrites the past | Inferred for any seed-based design (Elite, NMS store no history) | Generator version in the sidecar; old version kept for old days or frozen at snap |
| P10 | Repeated far-to-full changes cost more than they save | Inferred from IVE thrash and AC Unity pooling | Budget queue (#5); the time-to-view rank skips Sims that will leave view again soon |

## 4. Proposed tier ladder for TS3

Viewer points are the camera target, the camera position and every household Sim (docs/322). Distances are first
guesses from docs/322's bands, to be tuned by #9 and timing.

| Tier | Who | Enter | Leave | What runs |
|---|---|---|---|---|
| **H. Household** | Household and selected Sims, and any Sim of unknown scope | Always | Never | Full EA (hard rule) |
| **T1. Full** | NPCs < 150 m from a viewer; on a household or visited lot; interacting with a household Sim; target of an open call, invite or opportunity; observed (#7) | At once when any trigger holds | Demote to T2 only when no trigger holds for 30 sim-minutes and the Sim is beyond 200 m (periodic pass) | Full EA; today's unseen fallbacks still apply |
| **T2. Reduced** | NPCs 150-400 m, or nearer but out of view for a long time | From T1 by the rule above; from T3 by pre-expand (#4) | To T3 beyond 450 m, or in a rabbit hole or unseen lot for 1 sim-hour, at a safe point (#2) | EA simulation at a coarse cadence (#10); no idle animation; relaxed routing |
| **T3. Calculated** | NPCs > 400 m, or parked in a rabbit hole or unseen lot | From T2 at a safe point, with partial effects credited | Promote to T2 or T1 by time-to-view (#5), due visit, travel or observation; snap at promotion | Instance parked, no tasks; state = f(seed, version, elapsed) through the facade; sidecar events |
| **T4. Data only** (stage 2) | T3 Sims inside a rabbit hole or indoors on an unseen lot first, then any T3 Sim | From T3 when instance memory or cost matters | Re-instantiate through the budget queue (#5) before the predicted view time | `SimDescription` only; same facade |

Triggers that skip tiers:
- camera jump or map travel to a lot: promote that lot's Sims and its ring through the queue;
- a due visit from the event log: T1 at the arrival point by walk-in (#11);
- a household call or invite to a far Sim: T1 through the facade's answer, then a real arrival.

## 5. Claims that are not from developers

Only community-reported:
- Bethesda update rates and the unloaded-cell rules;
- GTA density data;
- Minecraft "no vanilla catch-up";
- all RimWorld world-pawn details;
- X4 step lengths and out-of-sector outcome gaps;
- Elite "nothing but the seed is stored" and the daily-update details;
- Cities: Skylines instance limits;
- TS4 culling rules;
- Bannerlord auto-resolve unfairness.

Inferred (ours):
- every "Fit" and "Design" line;
- the LOD Trader method (title only);
- the benefit figures in §2, which are shares from docs/322 and not timing results;
- all tier distances in §4.

Not found:
- a Rockstar source on RDR2 population density;
- the KCD talk content beyond its summary;
- the Wright and Marshall 2000 article.
