# 342. Far-Sim read sites and UI "looking" hooks (ST-1, ST-5) (2026-11-26)

Packages ST-1 and ST-5 of docs/332 §2.2 (kept in docs/340 phase R). Static only: decompiled Steam 1.67 script DLLs
(`Sims3GameplaySystems`, `Sims3GameplayObjects`, `UI`) under `game-copy/decompiled` (local only), NRaas sources read
for behaviour only. Nothing built, nothing run, no native disassembly. EA 1.69 not diffed. Ledger: docs/51 §612.
Handoff: `docs/handoff/st1-st5-1126.md`.

docs/324 (far-Sim model, branch `feature/far-sim-model-1122` until CP-0) owns the tier names used here: **H**
(household, selected, unknown scope: always full EA), **T3** (Sim object kept, hidden and inert, state computed),
**T4** (`SimDescription` only). This doc fills docs/324 §5.4 with sites and docs/324 §3.7 with hook points; it does
not change either design.

`file:line` values point into the local decompile. Markers: [verified] = read in the decompiled code,
[inferred] = not read or reasoned from a verified part.

## 0. Short answer

- **ST-1.** 37 read sites (table rows) in 7 kinds are named below (§2). 24 are answered by a model value: in T3 by
  the kept object standing on its plan lot, in T4 by EA's own "no Sim object" branch. **10 force go-live** before the
  call proceeds (all are EA paths that build the Sim or queue an interaction on it). 3 are household-only or belong
  to ST-4.
- EA's story progression never reads live state of a Sim: 25 `CreatedSim` / `Instantiate` lines in 16 methods, all
  null guards, builds of new Sims or destroys [verified count]. A computed value is enough there.
- **Five reads are wrong in T4 without a facade row** (§2.8): phone availability (a T4 Sim at work answers as free),
  the friend map tag (it disappears), ghosts and service Sims in the relationship panel (they disappear),
  story-progression house parties (a T4 household never hosts), and career mood metrics. All five are right in T3.
- **Two new facade rows for T3** that docs/324 §5.4 does not list: `RabbitHoleCurrent` (map tag and camera read it)
  and `MoodManager` mood for the career mood metric.
- **ST-5.** 12 UI paths were read (§3). **4 force go-live** (relationship-panel camera jump, map tag click, camera
  focus / follow, forced selection), **2 count as seen as a list** (relationship panel rows, phone and party
  pickers), 6 do not count (hover tooltips, known-info tooltip, family tree, map view open, visitor strip, world
  hover).
- Decision 8 of docs/324 names a "Sim info panel". EA has no such panel for an NPC: the HUD Sim panels serve only the
  selected Sim, and the only NPC info surface is the known-info hover tooltip, which reads description data. So
  three of the four decision-8 paths get a hook; the fourth needs none (§3, U4; open question Q1).
- One hook each on `Camera.FocusOnSim` and `Camera.FollowSim` covers every camera jump to a Sim (5 UI callers, 41
  gameplay files) [verified]. The phone picker is modal and pauses the simulator, so "promote the listed contacts"
  collides with the pause rule (Q2).

## 1. Method and limits

- Sites were found by pattern search, then each site named below was opened and read. Counts marked "direct" count
  lines of the form `<description>.CreatedSim.<member>`; reads through a local variable are not counted, so they are
  lower bounds [verified as lower bounds].
- Scope is the paths ST-1 names. The wider "who assumes a Sim object" sweep is RX-6 (docs/341 §3, about 2,300
  lines, not classified). §2.7 gives RX-6 a start for careers.
- Verdict words: **model** = the far model's value is enough (T3 through the kept object, T4 through EA's own null
  branch); **live** = the Sim must be promoted before the call goes on; **exempt** = only household or selected Sims
  reach the site.

## 2. ST-1: where EA reads a far Sim

### 2.1 Phone (`Phone.cs`, Sims3GameplayObjects, nested classes of `Phone`)

| # | Site | Reads | Verdict |
|---|---|---|---|
| P1 | `Phone.Call.CanCallSim` :803-889; 12 callers (:606, :907, :996, :1089, :1434, :1464, :2003, :2517, :3559, :3689, :7828, :9467) plus the instance wrappers :12102-12114 | receiver description validity, household, home world; if both Sims exist: same lot (:852), tourist household (:871), selectable and distance (:875); celebrity gate (:879) | model. T3: lot is physical. T4: every object read is null-guarded [verified] |
| P2 | `Phone.Call.IsSimAvailableForCall` :1249-1295 with `CheckCareer` :1223-1247; callers :1490 (player call) and :12594 (random NPC call) | clock hours; only when the Sim object exists: job and school `IsAtWork`, working at a job on its current lot, due at work, hours until work; then service and role | model in T3 (work blocks are EA-owned, docs/324 §3.4). **T4 wrong:** with no object the career checks are skipped and the Sim answers as available (:1269-1270) [verified] |
| P3 | `Phone.Call.GetAllValidCallees` :973-1023 (and :1297-1318 for other worlds) | walks every household's descriptions, then P1 and the relationship | model; this is the contact list (§3 U5) |
| P4 | `Phone.Call.GetAppropriateCallers` :891-918, `FindCallParticipants` :12577-12642 | NPC caller picked from descriptions, P1, P2; prom date test reads the caller's object (:12610) | model for the pick |
| P5 | `CallInviteOver.GetSimToLot` :2596-2662 (from `OnCallFinished` :2592) | takes the Sim object or builds it on the caller's home lot (:2607), then adds `VisitLot` / `GoToLot` to its queue (:2630, :2643); builds the "bring along" Sim too (:2622, :2639) | **live** |
| P6 | `SendWooHootyText.GetSimToLot` :8532-8580 | same shape: build (:8537), queue add (:8547-8560) | **live** |
| P7 | Other builds after a call: `CallInviteToLot.OnCallFinished` :3764, chess :3017, spar :3132, soccer :3258, sing-a-gram :4294, `CallTurnInOpportunity.OnCallFinished` :7005 | description built on a lot, then pushed | **live** |
| P8 | `CallChat.DoChat` :2126; test of `SendWooHootyText` :8448; challenger tests :2970, :3103, :3211 | same-lot test on the object | model |
| P9 | `CallPrank.OnPrankCallFinished` :6654-6657 | **writes** a buff on the pranked Sim when its object exists | model in T4 (guarded, buff lost as in EA). T3: a buff write on a model-owned Sim; credit at promotion or promote [inferred, no guard exists in the model yet] |

Phone has 10 lines that build a Sim (P5-P7) [verified]. All of them already sit behind docs/324 §5.5's choke point
(`SimDescription.Instantiate`), and in T3 behind the `InteractionQueue.Add` guard.

### 2.2 Invites and parties

| # | Site | Reads | Verdict |
|---|---|---|---|
| I1 | `HouseParty.Prepare.TimeToInviteGuests` HouseParty.cs:306-313 | guest object or build on the party lot | **live** |
| I2 | `NpcParty.BringNpcGuests` NpcParty.cs:2532-2538 | guest object or build on the host's home lot | **live** when a household Sim is invited or the lot is seen; otherwise the model may hold the party as a plan block [inferred] |
| I3 | `ForeignVisitorsSituation.TryInvite.InviteGuests` :190, `PromSituation` :712 and :1046 | build and push | **live** |
| I4 | Party guest picker, Phone.cs:4538 (`PartyPickerDialog.Show`) fed by the party variant of P3 | descriptions | model; list is seen (§3 U5) |
| I5 | `ThrowHouseParty` (story progression) :38 and :88 | hosts must have a Sim object | model in T3. **T4 wrong:** a T4 household is never a host, so NPC party rate drops [verified read, inferred effect] |

### 2.3 Story progression (`Sims3.Gameplay.StoryProgression`)

25 lines in 16 methods touch `CreatedSim` or `Instantiate` [verified count]:

- 9 builds of new or moved-in Sims (`AddSim`, `AddPet`, `AddHorse`, `AddRoommate`, `AddServoBot`, `CreateHousehold`,
  `GenerateOffspring`, two in `News`). They create Sims; they do not read a far Sim.
- 3 destroy paths, each null-guarded: `KillSim.KillSimExecute` :182-203, `KillRoommate.KillSimExecute` :182-203,
  `RemovePet.Execute` :147-149.
- 4 null guards: `MoveInWithPartner.Execute` :147, `Notifications.ThrowParty` :180, and I5.
- 0 direct reads of lot, position, interaction, motives, sleep or work state [verified by pattern].

Verdict: **model** for every action. One duty for the model: a kill or remove action destroys the object, so a
T3 Sim must be released from the model at `Sim.Destroy` / `Sim.Dispose` (the dispose side of docs/324 §5.5).

### 2.4 Relationship panel (model side: `HudModel.cs`, Sims3GameplaySystems)

| # | Site | Reads | Verdict |
|---|---|---|---|
| R1 | `HudModel.CurrentRelationships` getter :1234-1320; read only by `RelationshipsPanel` (10 reads, §3 U1) | the selected Sim's relationship list; per other Sim: description validity, household, alive, creating service; object reads: occult manager for genies (:1266-1268), same lot as the selected Sim for service Sims and mummies (:1273), selectable or same lot for ghosts (:1286); other-world Sims from mini descriptions (:1297) | model in T3. **T4 wrong for ghosts and service Sims:** with no object the row is dropped [verified] |
| R2 | `HudModel.UIRelationship` constructor :120-136 | `Relationship.GetOtherSim` (object id or invalid, :132), liking, relationship names from descriptions | model. T4: invalid id, so no camera jump, which is what EA does for a Sim that is not in the world |
| R3 | `HudModel.GetKnownInfo` :3367 and :3402, `GetKnownInfoList` :3532-3761 | traits, job title from the career manager, service, role, partner: description data only | model, both tiers |
| R4 | `SimDescription.OnPickFromPanel` SimDescription.cs:4968-5056 | builds the pie menu; with an object: call over (:4983-4985), call pet (:5034), menu on the object (:5046); without: phone entries only | model for the menu; **live** when an entry is picked (queue add) |
| R5 | `SimDescription.GetThumbnailKey` :7101-7106 | object thumbnail key when the object exists | model |
| R6 | `Relationship.GetOtherSim` Relationship.cs:570-578 | the two `CreatedSim` fields | model; base of R2, M1 and most social code |
| R7 | `Relationship.DecayAll` :1185-1195 | "no object or NPC" on both sides selects the NPC decay multiplier | model, both tiers [verified] |

### 2.5 Sim info and map tags

| # | Site | Reads | Verdict |
|---|---|---|---|
| M1 | `MapTagManager.Reset` MapTagManager.cs:210-218 | per relationship of the tag owner: R6, not on a service job, has met; adds the Sim's tag and its home-lot tag | model in T3. **T4 wrong:** no object, no tag, so a friend's tag disappears [verified] |
| M2 | `Sim.CreateMapTag` Sim.cs:61166-61181 | selectable and household, role type, sing-a-gram flag: picks the tag class | model |
| M3 | `NPCSimMapTag` :15, :27, :36-56, :58-70 (same shape in `FamilySimMapTag`, `SelectedSimMapTag`) | liking; name; "inside a tagged lot" from `LotCurrent`, the world lot and a vehicle posture; "inside a rabbit hole" from `RabbitHoleCurrent` | model in T3 **if the parked Sim carries the right `RabbitHoleCurrent`**: new facade row |
| M4 | `MapTagController.GetMaptagPos` UI MapTagController.cs:684-695 | position by object id unless the tag has a custom position | model: physical position [inferred: the native position read was not disassembled] |
| M5 | `NeighborLotMapTag` :40-52, :109-120, :131-142 | `Lot.GetSims()`: residents at home, household Sims inside | model: a hidden T3 Sim on its home lot is listed as at home, which matches the plan |
| M6 | `HudModel.CreateSimInfo` :5321-5370, `GetSimInfo` :2404 | motives, mood, buffs of the Sim | exempt: built for the selected household's Sims only |

### 2.6 Wants and opportunities

| # | Site | Reads | Verdict |
|---|---|---|---|
| O1 | `ActiveDreamNode.RunCheckFunction` :1670 and :1751, `RunFeedbackFunction` :1783; `DreamsAndPromisesManager` :2742, :2816 | the wish owner's object (a household Sim); a boarding-school subject with no object | exempt / model |
| O2 | `Opportunity.TryForceRequirements` Opportunity.cs:2871-2880 | coworker object or build on the owner's lot | **live** |
| O3 | `Opportunity.ForceAcquaintanceAndInstantiateOffScreen` :3056-3065 | target object or build | **live** |
| O4 | `Opportunity.OpportunityQualifiesForPhoneTurnIn` :4572, then P7 :7005 | target object test, then build on turn-in | model, then **live** |

docs/324 §1.3 already keeps the target of an open opportunity at T1, so O2-O4 meet a live Sim in practice.

### 2.7 Career and school (`Sims3.Gameplay.Careers`; start for RX-6)

346 `CreatedSim` lines in 36 files; 45 direct live-member reads in 35 methods [verified count, lower bound]:
buffs 17, inventory 9, selectable 8, current interaction 6, interaction queue 5, occupation 2, motives 1, mood 1,
school 1.

| # | Site | Reads | Verdict |
|---|---|---|---|
| C1 | `Career.RegularWorkDayFastWalkAlarmHandler` :2890, `RegularWorkDayRunAlarmHandler` :2902, beeper handlers (`LawEnforcement` :960, `Medical` :1071) | interaction queue of the worker | alarm callbacks: ST-4's hold decides; a queue add promotes |
| C2 | `Career.LeaveJob` :3652, `PromoteIfShould` :3929, `DemoteSim` :4181-4188, `SetLevel` :4251-4259 | current interaction (the work interaction), selectable, motives | model: they run inside EA-owned work blocks (docs/324 §3.4) [inferred: callers outside work hours not traced] |
| C3 | `MetricMood.FindMetricValue` MetricMood.cs:18 | mood of the worker | **new facade row:** the model must hand a mood value, or career performance of a far Sim freezes |
| C4 | `School` :2081, :2257, :2298-2405 (graduation), :2123-2141 (graduation buffs) | selectable; buff writes | model; buff writes as P9 |
| C5 | Reward and uniform sites (`Medical` :565-696, `LawEnforcement` :775, `Science` :596, `XpBasedCareer` :642, `Culinary` :129) | inventory of the worker | T3 model (object has its inventory). T4: docs/341 Q2 (inventory with no object) |

Roles and services are outside the model: `Role.StartRole` :424-429 and `EndRole` :472-479 write motives and the
queue of the role Sim, and `Service.CreateNpcSim` Service.cs:1234 builds the service Sim. Role and service Sims are
not eligible for T3 (docs/324 §3.5), which this confirms.

### 2.8 Counts and the T4 list

| Kind | Rows | model | live | exempt or ST-4 |
|---|---:|---:|---:|---:|
| Phone (P1-P9) | 9 | 6 | 3 | 0 |
| Invites, parties (I1-I5) | 5 | 2 | 3 | 0 |
| Story progression (§2.3, one row) | 1 | 1 | 0 | 0 |
| Relationship panel (R1-R7) | 7 | 6 | 1 | 0 |
| Sim info, map tags (M1-M6) | 6 | 5 | 0 | 1 |
| Wants, opportunities (O1-O4) | 4 | 0 | 3 | 1 |
| Career, school (C1-C5) | 5 | 4 | 0 | 1 |
| **Total** | **37** | **24** | **10** | **3** |

Rows that are both (R4, O4) are counted once, under "live". O1 and M6 are exempt; C1 is an alarm callback, which
ST-4 owns. Several rows are group rows (P1 has 12 callers, P7 six sites, §2.3 sixteen methods).

T4 needs a facade answer or a promotion for: P2 (availability), I5 (party hosts), R1 (ghost and service rows),
M1 (friend map tags), C3 (mood metric; T3 too). These go to RX-9's facade row list for LZ-4.

## 3. ST-5: UI paths that look at a Sim

UI code is in `UI.dll` (`Sims3.UI`, `Sims3.UI.Hud`); it reaches gameplay through `IHudModel`, `IMapTag` and
`IMiniSimDescription`. Hooks belong on the gameplay side of those interfaces where one exists, because mods replace
UI handlers (§4).

| # | Player action | UI site | Gameplay site (hook point) | Counts as |
|---|---|---|---|---|
| U1 | Relationship panel shows its rows | `RelationshipsPanel.Repopulate` :261 (callers :211, :653, :711, :723, :892, :902, :915, :935, :987) and `OnTick` :746 | `HudModel.CurrentRelationships` getter HudModel.cs:1234 (post: the returned list) | **seen, as a list**: promote rows in list order through the queue (decision 8) |
| U2 | Left click on a row | `OnSimOnLotMouseUp` :945, `OnSimNotOnLotMouseUp` :969 | `SimDescription.OnPickFromPanel` SimDescription.cs:4968 | covered by U1 plus the queue-add guard; no own hook (NRaas bypasses it, §4) |
| U3 | Right click on a row: camera jumps to the Sim | RelationshipsPanel.cs:964 | `HudModel.MoveCameraToSim` :2523, then U8 | **force go-live** |
| U4 | Known-info tooltip (hover on a row, on a queue icon, in the family tree, in the time almanac) | RelationshipsPanel.cs:521, InteractionQueueItem.cs:751 / :1495 / :1744 / :1785, FamilyTreeThumb.cs:120-122, TimeAlmanacDialog.cs:86-89 | `HudModel.GetKnownInfo` :3367 / :3402 | **does not count**: hover, description data only |
| U5 | Phone contact list, party guest list, door and mailbox Sim pickers | `PhoneSimPicker.Show` PhoneSimPicker.cs:39 / :44 / :49 into :151; `PartyPickerDialog.Show`; `DualPaneSimPicker.Show` | 19 call sites: Phone.cs :1371, :1417, :1445, :1475, :1809, :3440, :9697, :4538 (party); SocialCallback.cs :7269-7420 (6); Sim.cs:21908; Mailbox.cs:1596; TransmorgrifierAppModel.cs :78, :93; CommonDoor.cs:498 | **seen, as a list**; the picked Sims go **live** through P5-P7. See Q2 (the dialog pauses the simulator) |
| U6 | Click on a Sim's map tag | `MapTagController.OnMapTagMouseUp` MapTagController.cs:607-646 | `NPCSimMapTag.ClickedOn` NPCSimMapTag.cs:85 (pie menu on the Sim); base `MapTag.ClickedOn` MapTag.cs:130-154 picks the target for the other Sim tag classes; right click moves the camera to the tag position (:637-646) | **force go-live** |
| U7 | Select a Sim | Skewer.cs:261, `HudModel.SelectSim` :2489 | `PlumbBob.SelectActor` PlumbBob.cs:211 into `DoSelectActor` :221-333; refuses a non-selectable Sim unless forced (:241) | **force go-live**; from then on the Sim is H (selected) |
| U8 | Camera focus or follow | SimDisplay.cs:613, Skewer.cs:241, RelationshipsPanel.cs:964, StyledNotification.cs:873 (`CheckAndMoveCameraToSim` :2545), `CenterCameraOnSim` :2556 | `Camera.FocusOnSim` Camera.cs:1368-1383 (4 overloads into :1383) and `Camera.FollowSim` :1487 into :1492; 41 gameplay files call them | **force go-live** before the position read |
| U9 | Hover on a map tag | MapTagWin.cs:330-332 (`MapTagSimTooltip` with the Sim's name), `MouseFocusChanged` MapTagController.cs:657 | `NPCSimMapTag.HoverText` :27 | does not count (decision 8) |
| U10 | Open the map view | `MapTagController` tag pass :55, :762-768 | M3, M4 | does not count (decision 8) |
| U11 | Visitor strip click | VisitorSkewer.cs:72 (`IVisitor.OnLeftClick`) | - | no hook: visitors of the active lot are T1 already |
| U12 | Hover or click on a Sim in the world | - | `Sim.CreateTooltip` Sim.cs:56772-56797 (name, celebrity level) | no hook: a hidden T3 Sim is not rendered, so it cannot be hovered or picked [inferred] |

`Camera.FocusOnSim` reads the Sim's position, rabbit hole, lot and display position, and `FollowSim` hands the
object id to the native follow (Camera.cs:1383-1436, :1492-1510) [verified]. A T3 Sim must therefore be promoted
(unhide, apply) before the method body runs, not after. After the jump the camera target is a viewer point
(docs/324 §1.3), so distance keeps the Sim at T1 with no further rule.

Decision 8's four paths against this table: relationship panel = U1 + U3; phone contact list = U5; selecting the Sim
on the map = U6; Sim info panel = no NPC path exists (M6 and U4), so no hook.

Exemptions for every hook above: household Sims, the selected Sim and Sims of unknown scope are never model-owned,
so a promote request for them is a no-op by construction; the hook still has to test with the shared predicate
(`Fixes.HouseholdExempt`) before it touches the queue, and must do nothing when the far model is off.

## 4. Hook notes

- **Mods that replace the UI side** [verified in the NRaas sources]: NRaas RelationshipPanel removes the panel's two
  mouse handlers and installs its own (PanelTask.cs:186-189), which calls its own pick helpers instead of
  `SimDescription.OnPickFromPanel` (:92-109) but still calls `Repopulate`, `CurrentRelationships` (:88) and
  `MoveCameraToSim` (:117). So U1 and U3 hold with that mod; a hook on `OnPickFromPanel` would not. NRaas also ships a
  `HudModelProxy` that wraps `IHudModel`; a hook in the EA method body runs under the wrapper [inferred: the proxy
  delegates, its body was not read end to end].
- **NRaas Tagger** adds its own tags for Sims (MapTagHelper.cs:70). Its tags call the same base `ClickedOn`
  [inferred]; hook the base `MapTag.ClickedOn` as well as `NPCSimMapTag.ClickedOn`.
- **Thread:** the handlers above are script code reached from UI events and from `HudModel`; they run on the script
  thread [inferred, not traced to the native event pump]. A synchronous T3 promotion from them is the same case as
  docs/324 §5.5's forced promotion.
- **docs/68:** no native hook site is proposed here, so there is no entry to add; `tools/hook-sites.ts --check` was
  not needed. Our own code has no hook on any site of §3 today (search of `mods/ts3fixes/src`) [verified].
- Hook order for FS-9: U8 first (one hook, widest cover), then U6, U1, U5.

## 5. Open questions

- **Q1.** "Sim info panel" in decision 8 (docs/324 §3.7): EA has none for NPCs. Is the known-info tooltip meant
  (U4)? It is a hover and reads only description data, so this doc treats it as not counting.
- **Q2.** Phone and party pickers are modal and pause the simulator (`PauseMode.PauseSimulator` at every call site
  read). Promoting the listed contacts while the dialog is open breaks the "no sim-state work while paused" rule.
  Proposal: promote only the returned picks, on return. Needs Rodney's call, since decision 8 says "the contacts
  shown".
- **Q3.** U1: does the HUD read `CurrentRelationships` while the panel is hidden (the read at RelationshipsPanel.cs:211
  runs at panel init)? The hook should test the panel's visibility; not verified for every caller.
- **Q4.** P9 and C4: buff writes on a model-owned T3 Sim. Credit at promotion or promote on write? No design row yet.
- **Q5.** I2: may an NPC party with no household guest stay a plan block, or must EA's `NpcParty` run live?
- **Q6.** C2: callers of the level and leave-job paths outside work hours (story progression `GetJob`, retirement,
  ageing) were not traced. RX-6.
- **Q7.** Family tree dialog shows NPC relatives from description data. Not in decision 8, so not counted; confirm.
- **Q8.** EA 1.69: the managed sites were not diffed against the retail DLLs.
- **Q9.** M4 and U12 rest on native behaviour (position by object id; a hidden object cannot be picked) that was
  not disassembled.
