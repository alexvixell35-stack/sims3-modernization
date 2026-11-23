# 653. Trigger battery: fixes that never fired, and live triggers for them

2026-11-19 (UTC). Phase 1 of "every fix fires once": inventory of every ts3fixes switch, whether its counters ever moved in a valid fixes-ON phase run, and a ts3agent step (`trigger_battery`) that drives the cheap ones in one session. Branch `feat/triggerbattery`.

## 1. Method and counts

- Switches: every `case` of `Fixes.SwDefault` (`mods/ts3fixes/src/Fixes.cs`), 786 names [verified, source].
- Counter slots per switch: exact from the `B582Counters` / `B4Counters` / `PhCounters` label blocks, else a `docs/registry.md` row that names one switch, else the switch whose `ShortcutOn` call is nearest the `PatchCounters()[N]` in the same file [inferred for that last rule: a shared file can mis-assign a slot].
- Runs: `~/dev/ts3-runs/phase-2026111[7-9]-*` with an `earlyfix.txt` and `leak-*.json`, minus the 14 runs whose `autotest.txt` says `in live mode ()` (no active household, every fix exempt): **73 runs**. A slot fired when its highest value in any `leak-*.json` of a run is > 0; a switch also counts as fired (`log`) when `ts3fixes: <switch>` lines appear in that run's log.
- **Finding [verified, LeakCensusHandlers.cs]: `leak_census` reported only slots >= 2000**, so the 377 switches whose slots are all below 2000 have no run data at all (`n/d` below), including `gohome`, `doorlockowner` and the elevator fixes. This branch makes `leak_census` report every non-zero slot, so the next runs measure them.
- Proven: `node tools/harness/proven-fixes.ts`. Repro: a `tools/harness/repros.json` entry that turns the switch on (`patches` arg or `switches`); `live` = it has steps beyond patches / reflect_call / timing, `check` = only a `*Check` reflect_call (a pure or replay check, which does not move the live counters).

| fired? | switches | meaning |
|---|---|---|
| yes | 115 | counter > 0 in a valid ON run |
| log | 7 | no counter moved, but its log lines appear |
| no | 170 | slots >= 2000 exist and all stayed 0 |
| n/d | 377 | all slots below 2000: never reported, unknown |
| n/c | 117 | no counter slot found |

Of the 292 switches with data (`yes`+`log`+`no`), 122 fired and 170 never did. Never fired or unknown: 547; of those, 23 are save-time or load-time only and 127 sit in groups that need travel, a specific EP event or days of play (§2 c).

## 2. Never-fired switches by the situation that triggers them

Group = first keyword match on the switch name, then its repro bug text (save/load words first) [inferred: a keyword heuristic; a few rows are in a neighbour group]. (a) existing agent steps that set the situation up, (b) what a scripted trigger needs, (c) short run or not.

| group | no | n/d | (a) existing steps | (b) trigger needs | (c) short run? |
|---|---|---|---|---|---|
| routing/unseen/perf | 18 | 91 | `setup_sims`, `go_here`, `crowd_go`, `townie_trips`, `unseen_*`, `story_burst` | speedups and unseen paths; many are verify/diag switches with no bug to trigger | mostly natural play on the right world; out of scope for the battery |
| objects/misc | 26 | 66 | `do_interaction`, `buy_object`, `interaction_sweep`, `social_round` | the bugged interaction on a specific object, usually cut or reset at one moment | per fix: `interaction_sweep` covers many objects; a cut at the right step is not scripted |
| story progression/households | 8 | 54 | `story_days`, `story_burst`, `move_household`, `spawn_sims`, `remove_sims`, `kill_sim` | story progression actions (move in, split, pension), household changes | `story_days` Accelerated in a 20 min run reaches some; many need days |
| EP events | 20 | 24 | `travel_vacation`, `travel_university`, `travel_future`, `throw_party`, `story_burst` | graduation, prom, festivals, vacations, weddings, award ceremonies | needs travel or a scheduled event: not in a 20 min run except `throw_party` (party*) and City Hall ceremonies (`ceremony` phase) |
| CAS/UI | 21 | 19 | `cas_open/category/close`, `buy_open/category/close`, `build_mode`, `catalog_loop`, `set_speed`, `camera` | the UI flow (CAS rows, buy grid, speed lock around build mode) | short: `browse`/`casb`/`buy` phases + battery `speedrecalc` `speedskip` `speedlock` |
| careers | 11 | 28 | `go_to_work`, `career`, `career_ladder` | a specific career event (firefighter, daycare quit, stylist gig, burglar) | job hours and EP careers: not on demand in a short run |
| occults | 12 | 23 | `make_occult`, `zombies` | an occult NPC doing the bugged interaction (genie banish, vampire hunt, fairy aura) | `make_occult` + `do_interaction` per fix: cheap only where the interaction is on a base object |
| weather/seasons | 6 | 21 | `weather` (set weather), `clock_hour` | a specific weather, season or holiday (snow fight, trick-or-treat, holiday party) | Seasons EP; holidays need the calendar day; most short only on a world with Seasons and `weather` |
| pets/horses | 6 | 20 | `spawn_sims` species, `spawn_horse`, `pet_adopt`, `pet_guard` | a pet/horse NPC in the bugged action (carried pet put-down, wild herd, equestrian) | Pets EP; wild horses need Appaloosa Plains |
| load-time-only | 16 | 4 | EARLYFIX (switch on before the world loads) | the stale state must already be in the save (fix runs once at world load) | short only with EARLYFIX and a save that has the state; never by a live trigger |
| phone | 10 | 5 | none | incoming/outgoing call timing (7:00 schedule, prom call), a phone saved busy | mostly load-time or a specific hour: run past 7:00 after a load before 7:00 (phonecallschedule) |
| doors/locks | 6 | 7 | `doors` (read only), `build_cc_cycle` portals phase builds doors; no lock-setting step before this doc | lock a door on an NPC lot to a resident with `CommonDoor.SetLockTypeAndOwner`, then the owner leaves (doorlockowner); others need a privacy/NPC-door or shell-door situation | short: battery `doorlock`; shell/VIP doors need EP lots |
| in-use/reservations | 3 | 5 | `inv_stuff` (puts items in NPC inventories), `do_interaction` | an inventory item in use when its Sim resets; treasure chest open cut (chestuselist) needs a chest | short: battery `inuse`; chest and bouquet need EP objects |
| put-down/surfaces | 4 | 2 | `do_interaction`, `put_on` | a put-down whose ParentToSlot fails (putdownsurfaceuse) or is cut after the in-use mark (putdowninuse) | short: battery `putdown` (cut window); ParentToSlot failure not on demand |
| elevators | 1 | 5 | none before this doc (Bridgeport apartment lots have elevators) | an NPC rides an elevator; reset while a rider waits (resetriders); kitten/savedlog need a pet rider or a save | short: battery `elevator` on Bridgeport; elevatorsavedlog load-time |
| save-time-only | 0 | 3 | `save_game` (skip: save/load is owned elsewhere) | a save | skip (no save/load work) |
| stairs | 2 | 0 | `go_here`, `sim_route_to` | a stair wait that lasts 30 sim-minutes (stairwaitend) or a disposed Sim bound twice (simdisposelink): neither happens on demand | no short trigger: the existing `*-check` replays stay the proof |

Save-time-only (skipped, save/load belongs to another owner): `freshclothingresume`, `savecleanupaggressive`, `compactionfail`.

Load-time-only (fire at world load when EARLYFIX turns them on before the load and the save already holds the stale state): `nullservice`, `photosession`, `shelllighting`, `fireactivejob`, `curfewalarm`, `trickortreatalarm`, `permazombiemoon`, `fairyauratimer`, `npcseasonalparty`, `protestlastday`, `phoneloadreset`, `movingflag`, `tombmusic`, `flowersinuse`, `uselistprune`, `lotmgrcallbacks`, `elevatorsavedlog`, `boardingschoolmoney`, `visitlisteners`, `savedstacks`.

## 3. `trigger_battery` (ts3agent, `TriggerBatteryHandlers.cs`)

`trigger_battery {triggers?: [...], on?: true, keep_on?: false}` starts one task that runs the triggers back to back; `{status: true}` returns one row per trigger (result, own-slot deltas, any other slot that moved, seconds) and `fired` per switch; `{stop: true}` ends it. Each trigger sets its switches on in `ts3fixes.switches` and bumps the switch generation (what `patches` does), and puts them back at the end unless `keep_on`. Sims: only uncontrolled NPCs (`DrNpc`: has a household, not the active one, not selectable), human teen or older, no role or service, not on the home lot, the one farthest from the camera; the speed and build-mode triggers touch no Sim. Each trigger reaches EA's own hooked method with the bug's precondition, so the fix's live hook counts [verified against each hook in source; untested in game].

| trigger | switches (slots) | what it does | needs |
|---|---|---|---|
| `speedrecalc` | speedrecalclock (3411-3413) | `Gameflow.LockGameSpeed(Pause)`, `RecalculateGameSpeed()`, unlock | nobody else holding the lock |
| `speedskip` | speedskiplevel (3405-3407) | waits up to 20 s for the active Sim to have no head interaction (read only), asks Skip, restores the speed | an idle active Sim |
| `heartbroken` | heartbrokenbcast (3421-3423) | `SocialCallback.AddHeartBroken` twice on an NPC couple (second replaces the first broadcaster); removes the moodlet if it was new | an instantiated NPC couple (partner/spouse/fiance/romantic, positive) |
| `doorlock` | doorlockowner (1147-1151) | door on an all-NPC household lot locked `SelectedActor` to a member, `RemoveSimKeepHousehold` (the hooked `Household.Remove`), `Add` back; door reopened if still locked | an all-NPC household of 2+ with an unlocked door |
| `inuse` | resetinusefree (2689-2690) | item in an NPC inventory set in use, NPC `SetObjectToReset` | an NPC; item made from the EA catalog if it has none (removed after) |
| `putdown` | putdowninuse (2691-2692) | NPC put-down (`PutDown.DisallowCarrySingleton`) from inventory; at the moment the item is marked in use, the NPC is reset | the put-down reaching the surface step within 45 s |
| `gohome` | gohomewalk (2477-2478) | `GoHome` with `WalkStyle = Run` queued on an NPC away from home | an NPC off its home lot |
| `elevator` | elevatorridertimeout (1171-1173), elevatorresetriders (1155-1157) | NPC placed at the lowest elevator of a non-home lot, Go Here to the top elevator floor; when it waits inside, the elevator is reset | a lot with an elevator on two levels (Bridgeport) |
| `speedlock` | speedlockowner (3414-3416) | Pause lock held, `TransitionToBuildMode` on the home lot, back to live, unlock | last: leaves live mode briefly |

Repro entry `trigger-battery` in `tools/harness/repros.json`. Not covered by a live trigger (kept on their `*-check` replays): stairwaitend, simdisposelink, putdownsurfaceuse, barputdownwait, uselistprune / flowersinuse (load-time), elevatorkitten (pet rider), elevatorsavedlog (save).

Phase for `~/dev/ts3-runs/phasesoakr.sh` (not in this repo; one line to add to its `case`):

```bash
      battery) [ -n "${TB_DONE:-}" ] && { sleep 30; continue; }; TB_DONE=1; ag trigger_battery '{"keep_on":true}' | head -c 200 >> "$S/$ph.txt"; for i in $(seq 60); do ag trigger_battery '{"status":true}' | grep -q '"running":false' && break; sleep 10; done; ag trigger_battery '{"status":true}' > "$S/battery.json"; say "  battery: $(grep -o '"fired":{[^}]*}' "$S/battery.json")";;
```

## 4. Proposed runs (about 20 min each)

1. **A, Bridgeport (elevators, apartments, doors, UI)**: `WORLD=Bridgeport.world EARLYFIX="<list A>" phasesoakr.sh 4 "battery:6 browse:4 buy:3 routine:5"` (18 min of phases). EARLYFIX = the 163 switches of the battery plus the never-fired doors, elevators, in-use, put-down, CAS/UI and objects groups (list below). Expected: the 10 battery switches, plus CAS/UI leak fixes from `browse`/`buy`.
2. **B, Isla Paradiso or Moonlight Falls (occults, pets, story progression)**: `WORLD=IslaParadiso.world EARLYFIX="<list B>" phasesoakr.sh 4 "story:5 socials:4 party:4 deaths:3 routine:4"`. EARLYFIX = 175 switches (occults, pets/horses, careers, story progression, phone). Expected: story-progression and social fixes; occult/pet ones only where NPCs of that kind live in the world.
3. **C, a Seasons world with a save that has history (load-time, weather, EP events)**: `EARLYFIX="<list C>" phasesoakr.sh 4 "idle:3 ceremony:6 party:4 routine:5"` with `weather` set to snow/rain by hand or a step. EARLYFIX = 87 switches (load-time, weather/seasons, EP events); the load-time ones fire at load only if the save holds the stale state.

Together A+B+C turn on every never-fired or unknown switch except save-time-only, routing/perf and test aids (verify, shadow, probe, diag, log, test). They need the agent package of this branch: `leak_census` now reports all slots, so these runs also give the first data for the 377 `n/d` switches. Read the result per run from `battery.json` and from the slot deltas between `leak-0.json` and the last `leak-*.json` against §5.

<details><summary>EARLYFIX run A (163)</summary>

```
ageupkeepoutfits artsellnoartist awkwardscience backpacklotleak bandjam barputdownwait bathcleanup bathsituationfx betrayednullsim blogorphan bonfirespice bouncerrole bribeguard broadcast bubblepipeguard bubblingspringsreset buffgrid buffpausedtimeout buffremovedelayed buycellrecycle buyiconleak cancelposturedef casexitsweep casfiltergrid casmemo caspuckevents casrowleak casuihandlers celebalarmnull chessdefeated chestuselist chipcomponentorphan cleanflag cleanupvfx compositionnull computergametype datadiscputback deadrefconvo deadsimhollow displaycasereset disposevirtualslot distressgate djcleanup dogswimaroundlist doorlockowner doorprivacynull dreamrootorphan eatoutsidecount eatpostonce elevatorkitten elevatorresetriders elevatorridertimeout facialidleweight famcursorleak fastreftable fearfaintundo fightbroadcastend filtergridleak fireplaceherb floorswitchuselist foodtraygrill gohomewalk guitargnomeboost harvestextras heartbrokenbcast hiddenroomspawn hottubwoohoofx hudunsub initstacktrace innerbeautyonce intmultiple jazzparam krakenfriendsailor laundrymoodeach leaveconvnull lecturebroadcaster lightcustom lookatcopy lottotooltip megaphonerun meltalarm meteormagnetorphan midlifeuiending moodidleflavor moodtankreset motivebar motivemultonce mountfishcost newspaperdef nocommercialcar oppcategoryprotect oppdeadlisteners oppfailcharge opptrackerrelisten overwatchstucknotice owresettime paparazzicooldown petputdownwait photosubjectstars picnicants picniccamera playpentalkcast portasknohome portraitpose prankditchempty privacyothers putdowninuse putdownplaced putdownsurfaceuse queueproximity rabbitholedoor reactioncopy reactiongrumpy resetinusefree residentspawnslot residentspawnwalk returnhomealarms rhentryrelaxnear romancecooldown roundupreturn royaltyorphan rubbleuselist rummagebcast sandscoins sandwichalarm saunatubclear scavengernolot sciencefxstop sellcursorleak shelldoorsummon shoodef shroomslots shuffleboardnpccap slavewakereason snublistener socialnoeffect speedlockowner speedrecalclock speedskiplevel speedskipnoactor statueline stereoupgrade straygnometns swiperestore tagbagsurfaceleak talkoccupationchance tinyuieventreg tooltipthrottle traiticondislike traitlistenerremove traitremovenull trashoutsidefallback tvdisclistener tvsyncalarm tvwatchskill twinbellyshape upgradefailalarm vehfrankentaxi vehiclefxhelper viproomrefresh wellstealcooldown wildlotskip zoomtier
```
</details>

<details><summary>EARLYFIX run B (175)</summary>

```
ageupswap badsecretkey balancingballcareer bartendinghourlypay birdtvnull boatgetoutguard boatparkgone bodysculptorweight boxofmysterywoohoo broomarenachoice broomtipperformer bsrefund buffupdate bullridercleanup burglaridle butlerpayalarm carpoolschoolskip catchpreyodds catgymwatch cattoypickerrel chipanycount chipfamilycrystal chocfountaingeyser compositorghost compositorleak consignmentlastday consignmentskillcurve consignmentstockleak coopeggquality copyhomeworkfx dancefloorlistguard daycarelatealarm daycareliking daycarequit deadrefguards deadrefsweep digsiteemptypick disposebase dogswimslots donationbuffchance dragoncurserun dreamcleanup eanullguards easpreadpets eatpreycancel elvisgnomecap equestriantrophyname fallenrockcollected fieldtripadult firefightercount firepitchair firetrapcount fireworksfizzle fortunetellerscamodds fragmentreconstruct fvstandskillcurve genealogycycle geniebanish ghostspawnargs gigmaster gigwaitleave goodbyepure gourmetpetrecipe grouptalkstale harvesteatvfx hauntedgnomeinv hayscattertime herbqualityfloor hhcleanupdup homeworkcopyold homeworkhelp homeworkskillbonus horseranchchallenge horsetraitscore hottubseatposture houdinignomeleak hypnotrickrelease influencefollowerbonus invnotyet jetpackflyherecleanup jobyield junglegymexitqueue lifeguardquit lifeguardsurvey loyaldoglay ltrhistorybits maildogchaser mermaiddehydratelook mermaidsharknull metricskillmissing minihomelot moondiallistener moonriselistener moveinpartner mummycursetrait nectarconsignvalue nectarcopylevel10 nemesisnoromance npcdriverpool npcstallend nullsituation oppchainworld outfitwaitend outfitwaitsaved ovenmenunull petdreamtimer petfireplan petharvestvfx petpoolghost petwakebed phonecallrearm phonecallschedule photofunds pingpongopponent pokerdeadplayer posturepopcycle preferredservice privateeyecase reaperflow reltablesapart repairmanskill resumetns rewardgiver roleunprotect roulettehackreset sarcmummynpc serviceassignexit serviceleave serviceshowalarm showstagefxend showstagethrowables simrefsdeadsim snackbowlslobvfx sneakoutlistener socialworkeronlot spacesimpartner spbucketindex spclonedict spcollectnull spcreatemovein spellbooksummonload spelltargetend spjobatwork splashroof splitpartner splotdensity splotlookup spoccultpick spoonslavechecks sppensionprompt sprecheck sprelcount springdanceclear sproommatejob starlightreset storeworldevents streetartmasterboost suatgradebonus takeariskguard teleportcommunitylot trashpileimport treewishingwell tricksocials tutorballoonalarm uncleguard unicornspawnfail vampirefruitsnack vampirehuntglow virtualhomeshare virtualmoveindup voodoofairyfadein werewolffind wildherdmother wildhorselot woohootywait
```
</details>

<details><summary>EARLYFIX run C (87)</summary>

```
agingalarmcancel awardkeep bakesaleseller birthdayagerecord birthdayagers boardingschoolmoney buffetemptypick ceremonyspot comboopp combosituation curfewalarm dreamnodedeadsim eventcrowdring fairyauratimer fireactivejob flowersinuse frolicexit futuredescendantsize0 gigpullaudience gohereboatlist graduationtable hauntedumbrella holidaylights igloolookats jetpackflyingbuff jogterrain kegbreakage labobjectpush leafburnfail lightningweights lotmgrcallbacks midlifewishsave mopunreachable movingflag nanitemerge npcseasonalparty nullservice observatorymsg outerwearautoloop outerwearrestore partycleanupnull partylotlistener partymotive partyoutcome permazombiemoon petpuddletile phoneloadreset photosession pondclearscan professorterm rainsheltercheap rolenullguard savedstacks seasondup seasonimpostercold seasonsdeadsim sentiencename shelllighting simfestkeep snowfightjoinwait soakedpetselfreact soakedstat teenpartybreakup tempforecast tombmusic trainboost trainsimgone trickortreatalarm tspawninvdup unigradkeep unitravelflag uselistprune vacationalarms vacexitreturn vacexport vacimportfix vaclistenernull vaclistenerpad vacreturnskip vacstoreguard vacteendelete visitlisteners weathermanagerthrow weatherstonelistener weatherstonerelisten weddingcleanup weddinglistener
```
</details>

## 5. Every switch

fired: yes / log / no / n/d / n/c (§1). repro: first live repro, else first check repro, plus the battery trigger. Trigger: the repro's bug text, shortened.

### 5.1 Never fired or unknown (547)

| switch | area | fired? | proven? | repro? | trigger needed |
|---|---|---|---|---|---|
| `betrayednullsim` | CAS/UI | n/d | - | check `betrayednullsim-check` | Betrayal notice and break-up run when the betrayer is not instantiated |
| `bouncerrole` | CAS/UI | n/d | yes | check `bouncerrole-check` | The bouncer notices sneakers within the tuned radius (squared) and ropes without a bouncer role do not throw… |
| `broadcast` | CAS/UI | n/d | - | - | reaction broadcaster pulses, skipped |
| `bubblingspringsreset` | CAS/UI | n/d | yes | check `bubblingspringsreset-check` | A reset hot spring disposed its Nature's Hot Tub broadcaster and never recreated it; now recreated (household… |
| `buffgrid` | CAS/UI | n/d | - | live `ui-buffgrid-inplace` | SimDisplay.PopulateBuffGrid clears the moodlet grid and loads one HUDMoodletGridCell layout per moodlet on ev… |
| `casmemo` | CAS/UI | n/d | - | live `cas-grid` | Opening CAS hair freezes the sim thread ~2.2 s while all 261 tiles are built in one call |
| `displaycasereset` | CAS/UI | n/d | yes | check `displaycasereset-check` | A reset of the gem/metal/potion display case keeps its contents (household-visible) |
| `fightbroadcastend` | CAS/UI | n/d | yes | check `fightbroadcastend-check` | A fight's reaction broadcaster ends with the social when the fight never reaches AfterFight |
| `lecturebroadcaster` | CAS/UI | n/d | - | check `lecturebroadcaster-check` | One great-lecture broadcaster per lecturer instead of one more per lecture |
| `lottotooltip` | CAS/UI | n/d | yes | check `lottotooltip-check` | The lotto 'not available' tooltip shows the real start hour (B12 §41, docs/592 S8) |
| `midlifeuiending` | CAS/UI | n/d | - | check `midlifeuiending-check` | Reading the midlife crisis moodlet tooltip no longer ends the crisis |
| `motivebar` | CAS/UI | n/d | - | live `ui-motivebar-cache` | MotiveBar.Update loads the moodlet icon and three background UI images (a new UI texture each) on every motiv… |
| `overwatchstucknotice` | CAS/UI | n/d | - | live `owprobeplans-stuckcheck-ab` | NRaas Overwatch Stuck Check V2 shows one notice per reset Sim (StuckCheck:Unroutable from StuckCheck.ResetTas… |
| `owresettime` | CAS/UI | n/d | - | - | (no repro text; see HitchCause.cs) |
| `photosubjectstars` | CAS/UI | n/d | yes | check `photosubjectstars-check` | Photography journal subject stars use float division (UI) |
| `playpentalkcast` | CAS/UI | n/d | yes | check `playpentalkcast-check` | A second learn-to-talk broadcaster orphans the first, which never stops (household-visible) |
| `straygnometns` | CAS/UI | n/d | yes | check `straygnometns-check` | The stray-wildlife gnome notice names the chosen animal, once (household-visible) |
| `tooltipthrottle` | CAS/UI | n/d | - | check `tooltip-throttle` | TooltipManager.Simulate walks the window chain under the cursor every sim tick (~33-44/s), reading each windo… |
| `traiticondislike` | CAS/UI | n/d | yes | check `traiticondislike-check` | Pie-menu entries that lower a trait motive show the dislike icon (B12 §42, docs/592 S9) |
| `bonfirespice` | CAS/UI | no | - | check `bonfire-spice-herb` | BonFire SpiceItUp removes the herb before routing (failed route = herb lost), never destroys the used herb, a… |
| `buycellrecycle` | CAS/UI | no | - | - | built 2026-11-11: catalog cells dropped by AddGridItem (2269, also off) / returned to a cache (2270) / errors… |
| `buyiconleak` | CAS/UI | no | - | - | built 2026-11-11 (v2): Buy/Build exits seen (2217) / stale `mCustomControlInstanceDict` entries removed after… |
| `casexitsweep` | CAS/UI | no | - | - | built 2026-11-12: detached live entries registered during the CAS visit, at CAS exit (2300, also off) / clean… |
| `casfiltergrid` | CAS/UI | no | - | - | built 2026-11-12: CAS filter grids at CASPuck.Unload (2287, also off) / restored (2288) / errors (2289) |
| `caspuckevents` | CAS/UI | no | - | - | built 2026-11-11: CASPuck disposed with its dashboard handler still attached (2284, also off) / handler pairs… |
| `casrowleak` | CAS/UI | no | - | - | built 2026-11-11: empty CAS clothing rows `CASClothingCategory.AddGridItem` left unattached, also with the sw… |
| `casuihandlers` | CAS/UI | no | - | - | built 2026-11-08: handlers of closed CAS windows (CASPuck, CAF, CASHair, CABPartSelection) removed from Downl… |
| `famcursorleak` | CAS/UI | no | - | - | built 2026-11-11: family-screen drag cursor windows still alive at CASFamilyScreen.Unload (2293, also off) /… |
| `filtergridleak` | CAS/UI | no | - | - | built 2026-11-11: filter grids at Buy/Build Unload (2281, also off) / DisposeOnDetach restored (2282) / error… |
| `heartbrokenbcast` | CAS/UI | no | yes | check `heartbrokenbcast-check` + battery `heartbroken` | SocialCallback.AddHeartBroken assigns a new never-expiring ReactionBroadcaster over an already HeartBroken Si… |
| `hudunsub` | CAS/UI | no | - | - | (no repro text; see HudUnsub.cs) |
| `picniccamera` | CAS/UI | no | - | check `picnic-camera-callback` | PicnicBasket.Serve success path leaves OnMapViewEnabled on CameraController.OnCameraMapViewEnabledCallback: e… |
| `portraitpose` | CAS/UI | no | - | check `portrait-pose-left` | Paint Portrait failing before the grab (route, UI check) or a painter reset leaves the subject waiting up to… |
| `rummagebcast` | CAS/UI | no | yes | check `rummagebcast-check` | TrashcanOutside+Rummage.Cleanup does not destroy the can's rummage ReactionBroadcaster, so a Sim reset mid-ru… |
| `sellcursorleak` | CAS/UI | no | - | - | built 2026-11-11: sell panels closed with drag-cursor windows noted (2242, also with the switch off) / window… |
| `speedlockowner` | CAS/UI | no | yes | check `speedlockowner-check` + battery `speedlock` | LotManager.EnterBuildBuyMode locks Pause only when nobody holds a lock, but ExitBuildBuyMode always calls Gam… |
| `speedskiplevel` | CAS/UI | no | yes | check `speedskiplevel-check` + battery `speedskip` | Gameflow.SetGameSpeed(GameSpeed, bool) sends the engine speed level from the requested speed (IL_0095 ldarg.0… |
| `speedskipnoactor` | CAS/UI | no | yes | check `speedskipnoactor-check` | Gameflow.SetGameSpeed(Skip) with Sim.ActiveActor null leaves the time scale at 0 and mGameSpeed unchanged, so… |
| `tinyuieventreg` | CAS/UI | no | - | - | built 2026-11-09: Tiny UI Fix event-registration entries of detached windows removed (2094 entries, 2095 hand… |
| `zoomtier` | CAS/UI | no | yes | check `zoomtier-check` | an on-screen NPC too small to see (camera >= 250 m or map view) counts as seen, so tier-2 relaxed paths stay… |
| `agingalarmcancel` | EP events | n/d | yes | check `agingalarmcancel-check` | Cancelling a Sim's aging alarms also cancels the graduation early-message alarm |
| `bakesaleseller` | EP events | n/d | - | check `bake-sale-dead-seller-check` | issue #102: BakeSaleTable keeps a deleted seller (mSeller is cleared only by StopTending and the table reset)… |
| `buffetemptypick` | EP events | n/d | yes | check `buffetemptypick-check` | Resort buffet second helping only picks tables with food (no ArgumentException) (household-visible) |
| `futuredescendantsize0` | EP events | n/d | yes | check `futuredescendantsize0-check` | Future trip: size-0 descendant households no longer unpack progenitors or abort the descendant load step (hou… |
| `kegbreakage` | EP events | n/d | yes | check `kegbreakage-check` | The juice kegger breakage multiplier applies only at a juice kegger party (B12 §31, docs/591 R10) |
| `labobjectpush` | EP events | n/d | yes | check `labobjectpush-check` | University lab students without a lab item use the lab's own objects (intent inferred) (household-visible) |
| `midlifewishsave` | EP events | n/d | - | check `midlifewishsave-check` | Midlife crisis wish count and update date survive travel export/import |
| `nanitemerge` | EP events | n/d | yes | check `nanitemerge-check` | Future travel merges the collected nanite list (and copies the schematics list) (household-visible) |
| `partycleanupnull` | EP events | n/d | - | check `partycleanupnull-check` | Party and wedding cleanup finishes when the host or a guest is gone |
| `partylotlistener` | EP events | n/d | yes | check `partylotlistener-check` | Hostless NPC party lot-change listener removes itself |
| `protestexit` | EP events | n/d | yes | check `protestexit-check` | A cancelled NPC protest ends with Exit(), so the child protest's lot-change handler is removed (B12 §1, docs/… |
| `rolenullguard` | EP events | n/d | yes | check `rolenullguard-check` | Barista and future-food waiter with no role answer no instead of throwing |
| `teenpartybreakup` | EP events | n/d | yes | check `teenpartybreakup-check` | Break Up Party no longer throws when the party ended during the route (household-visible) |
| `vacationalarms` | EP events | n/d | yes | check `vacationalarms-check` | Alarms on vacation arrival are not shifted twice (B12 §38, docs/592 S3) |
| `vacexitreturn` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vacexport` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vacimportfix` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vaclistenernull` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vaclistenerpad` | EP events | n/d | - | check `vacation-listener-pad` | docs/210 §6: OpportunityExportInfo.ListenerStates is a fixed new [3]; the travel import (AcceptOpportunityFro… |
| `vacreturnskip` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vacstoreguard` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `vacteendelete` | EP events | n/d | - | live `bp-vacation-saves` | issue #97 rows 1, 2, 6, 7: the free vacation pickup destroys the adults without the travel export; they come… |
| `weddingcleanup` | EP events | n/d | - | - | built 2026-11-16 (bugfix/ip-jitfault-scripterror-1116), default on (rare EA crash fix), NOT RUN: EA WeddingCe… |
| `weddinglistener` | EP events | n/d | yes | check `weddinglistener-check` | Wedding party that ends drops its marriage listener |
| `awardkeep` | EP events | no | - | check `award-keep-check` | A City Hall award ceremony whose GiveAward fails (honoree route tantrum, mayor route fail, no sync) is cleane… |
| `birthdayagerecord` | EP events | no | yes | check `birthdayagerecord-check` | BirthdayParty.OnAgeTransition returns ListenerAction.Remove after recording the first guest who ages up, so l… |
| `birthdayagers` | EP events | no | yes | check `birthday-agers` | Birthday party records only the first Sim who ages up |
| `ceremonyspot` | EP events | no | - | check `event-ceremony-block` | City Hall award ceremony: the NPC crowd gathers on and around the award jig (RouteToGatheringArea centres on… |
| `comboopp` | EP events | no | - | check `combo-rabbithole-check` | Combined rabbit holes : each piece of a combo building is out of the world (Position OutOfWorld, LotCurrent t… |
| `combosituation` | EP events | no | - | check `combo-rabbithole-check` | Combined rabbit holes : each piece of a combo building is out of the world (Position OutOfWorld, LotCurrent t… |
| `dreamnodedeadsim` | EP events | no | - | - | built 2026-11-08: EA dream nodes' owner-less trigger listeners (ActiveDreamNode.ResetListener for third-party… |
| `eventcrowdring` | EP events | no | - | check `event-ceremony-ring` | City Hall award ceremony: the NPC crowd's gathering goal (RouteToGatheringArea, radius 0-6 m from RoutingSlot… |
| `gigpullaudience` | EP events | no | yes | check `gigpullaudience` | Removed gigs still pull an NPC audience to the venue |
| `graduationtable` | EP events | no | - | check `graduation-table-check` | docs/508: School.GraduateInCityHall / AttendGraduation WaitingLoopDel read School.sGraduatingSims without a n… |
| `jetpackflyingbuff` | EP events | no | yes | check `jetpackflyingbuff-check` | Free Flying stays paused when Fly Around ends early |
| `observatorymsg` | EP events | no | - | check `observatory-stardust-msg` | StellarObservatory+TravelToSimulonV tests dark matter twice (num3 > 0 // num3 > 0) before its summary notice:… |
| `partymotive` | EP events | no | - | check `party-outcome-motive` | HouseParty core : (1) with University Life installed, GetPartyOutcome never returns a lame party outside a Un… |
| `partyoutcome` | EP events | no | - | check `party-outcome-motive` | HouseParty core : (1) with University Life installed, GetPartyOutcome never returns a lame party outside a Un… |
| `professorterm` | EP events | no | yes | check `professorterm-check` | University professors are fired at every term end and never replaced |
| `sentiencename` | EP events | no | yes | check `sentiencename-check` | Removing the Sentience chip sets the first name to the full name |
| `simfestkeep` | EP events | no | - | - | built 2026-11-14: SimFest award wrap-ups queued after an early sync failure EA left without one (2324) / erro… |
| `tspawninvdup` | EP events | no | yes | check `tspawninvdup-check` | TreasureSpawnPoint.SetupEventListening adds the treasure's OnAddToInventoryEvent handler without removing it… |
| `unigradkeep` | EP events | no | - | - | built 2026-11-14, default on since 2026-11-15 (unigradkeep stays off) (PR #405 in-game off/on, Dalexlol), NOT… |
| `unitravelflag` | EP events | no | yes | check `unitravelflag-check` | An enrolment that queues no moving van leaves saving and travel blocked |
| `balancingballcareer` | careers | n/d | yes | check `balancingballcareer-check` | Balancing ball trick offered to performance artists only (Run assumes the career) (household-visible) |
| `bartendinghourlypay` | careers | n/d | yes | check `bartendinghourlypay-check` | Bartending hourly pay that EA announces is paid (household-visible) |
| `butlerpayalarm` | careers | n/d | yes | check `butlerpayalarm-check` | A butler whose job ended stops charging the household every week (B13 §16, docs/593 V1) |
| `carpoolschoolskip` | careers | n/d | yes | check `carpoolschoolskip-check` | A school child skipping the bus (horse, own lot) goes to school instead of crashing the pickup |
| `consignmentlastday` | careers | n/d | yes | check `consignmentlastday-check` | Consigned items sell on day 5 at the last price and return after it (no IndexOutOfRange in the daily alarm) (… |
| `consignmentskillcurve` | careers | n/d | yes | check `consignmentskillcurve-check` | Consignment sale chance uses the skill curve, not the frequency curve (sales no longer fall as Consignment sk… |
| `consignmentstockleak` | careers | n/d | yes | check `consignmentstockleak-check` | Expired and rotated shop-stock objects of the three consignment registers are destroyed, not leaked (househol… |
| `copyhomeworkfx` | careers | n/d | yes | check `copyhomeworkfx-check` | A homework copy cut short removes its cheating broadcaster (B12 §13, docs/590 A2) |
| `daycarelatealarm` | careers | n/d | yes | check `daycarelatealarm-check` | A late-parent daycare alarm for a child who already left is skipped instead of throwing (household-visible) |
| `daycareliking` | careers | n/d | yes | check `daycareliking-check` | Daycare experience uses the child's real liking for the owner (B12 §30, docs/591 R9) |
| `eanullguards` | careers | n/d | - | check `bp-eafixes-check` | issues #86 (Service.RemovePreferredSim removes only the first entry naming a Sim), #88 (VelvetRopes.ChargeSim… |
| `firefightercount` | careers | n/d | yes | check `firefightercount-check` | No more firefighters come than the pool's maximum (B13 §13, docs/593 V8) |
| `firepitchair` | careers | n/d | - | check `bp-eafixes-check` | issues #86 (Service.RemovePreferredSim removes only the first entry naming a Sim), #88 (VelvetRopes.ChargeSim… |
| `homeworkcopyold` | careers | n/d | - | check `homeworkcopyold-check` | Copying old homework uses EA's old-homework multiplier |
| `homeworkskillbonus` | careers | n/d | yes | check `homeworkskillbonus-check` | Homework skill bonus below max level restored (household-visible) |
| `jobyield` | careers | n/d | - | - | built 2026-10-24 : 248 step-asides (a plan that would wait for a running route job slept instead), 249 ticks… |
| `lifeguardquit` | careers | n/d | yes | check `lifeguardquit-check` | A lifeguard who quit mid-survey stops the loop instead of throwing in RescueCheck (B12 §3, docs/590 B4) |
| `lifeguardsurvey` | careers | n/d | yes | check `lifeguardsurvey-check` | Completed lifeguard beach survey's repeating event alarm removed (EA left it throwing forever); load removes… |
| `nectarconsignvalue` | careers | n/d | yes | check `nectarconsignvalue-check` | Consignment-shop nectar bottles roll their value over the tuned range, not its minimum (household-visible) |
| `nullsituation` | careers | n/d | yes | check `nullsituation-check` | A service whose situation could not be made no longer throws in AddAssignment and stops every service for the… |
| `preferredservice` | careers | n/d | - | check `bp-eafixes-check` | issues #86 (Service.RemovePreferredSim removes only the first entry naming a Sim), #88 (VelvetRopes.ChargeSim… |
| `repairmanskill` | careers | n/d | yes | check `repairmanskill-check` | A repairman's handiness is the rolled level, not the roll added on every visit (B13 §14, docs/593 V9) |
| `serviceassignexit` | careers | n/d | yes | check `serviceassignexit-check` | Service Sim asked to hang out leaves no stale (saved) assignment; load removes exited ones |
| `serviceshowalarm` | careers | n/d | yes | check `serviceshowalarm-check` | Service route retries do not re-arm the pending make-visible alarm (NPC shown once) |
| `showstagethrowables` | careers | n/d | - | check `bp-eafixes-check` | issues #86 (Service.RemovePreferredSim removes only the first entry naming a Sim), #88 (VelvetRopes.ChargeSim… |
| `spjobatwork` | careers | n/d | yes | check `spjobatwork-check` | A Sim who takes a new job while at work keeps it when the old job's deferred leave runs (household-visible) |
| `suatgradebonus` | careers | n/d | yes | check `suatgradebonus-check` | SUAT grade bonus indexed best-first on both paths (GPA order inverted, school A skipped) (household-visible) |
| `virtualhomeshare` | careers | n/d | yes | check `virtualhomeshare-check` | Service Sims' virtual homes come from the tuned cheapest share of shells |
| `bsrefund` | careers | no | - | - | built 2026-11-14, NOT RUN: boarding-school tuition refunded after a failed pickup (2351) / of them no-car exi… |
| `burglaridle` | careers | no | - | check `burglar-idle` | BurglarSituation.Stealing.Init with a fire on the lot only pushes ReactToFire and relies on its tail calling… |
| `daycarequit` | careers | no | - | check `daycare-quit-managers` | Daycare.LeaveJobNow removes the child managers only when a workday situation runs; quitting outside work hour… |
| `firetrapcount` | careers | no | yes | check `fire-trap-count` | Firefighter large fire / earthquake: a trapped Sim who leaves the lot (or is deleted) unfreed keeps its 'free… |
| `fireworksfizzle` | careers | no | - | check `fireworks-fizzle-check` | When a firework fizzles, EA's FireworksBase.Watch.LoopDel asks every watcher's FireworksBigBooms state machin… |
| `homeworkhelp` | careers | no | - | check `homework-help-jig` | HelpWithHomework started after the child finished reads the destroyed two-person jig (NullReferenceException… |
| `metricskillmissing` | careers | no | - | check `metric-skill-missing-check` | MetricSkillX (a career level's skill metric) takes its skill from the career XML by name only, so a hex id or… |
| `npcdriverpool` | careers | no | - | check `npc-driver-pool-check` | issue #166 build 37: NpcDriversManager.CreateNpcDriver hands the top pooled description out again whenever it… |
| `privateeyecase` | careers | no | - | check `privateeye-case` | Investigator case interactions: LookThroughMail dereferences a null apartment wall-mailbox door (or a null ca… |
| `serviceleave` | careers | no | - | check `service-leave-reset` | A service NPC reset (stuck reset, any mod) deletes its DeleteOnReset timeouts and no service situation re-arm… |
| `sneakoutlistener` | careers | no | yes | check `sneakout-listener` | A caught teen's left-over sneak-out listener gives Snuck Out on the next trip (school carpool) |
| `disposevirtualslot` | doors/locks | n/d | yes | check `disposevirtualslot-check` | A disposed townie household with only a virtual home leaves its lot slot, NPC door and mailbox |
| `doorlockowner` | doors/locks | n/d | yes | check `doorlockowner-check` + battery `doorlock` | Door locks to a dead or moved-out owner opened, re-pointed or pruned (household-visible) |
| `doorprivacynull` | doors/locks | n/d | yes | check `doorprivacynull-check` | A privacy situation with no sync target no longer strips the privacy of later doors (B12 §23, docs/591 C7) |
| `initstacktrace` | doors/locks | n/d | yes | check `initstacktrace-check` | InteractionInstance.Init and OnAddedToQueue build O(depth^2) StackTraces nobody reads (reader is compiled out… |
| `prankditchempty` | doors/locks | n/d | yes | check `prankditchempty-check` | Doorbell-ditch prank with nobody left at home ends like EA's null pick instead of throwing (prankster reset) |
| `residentspawnslot` | doors/locks | n/d | yes | check `residentspawnslot-check` | Apartment resident spawn slot judged with the resident's door permissions, not the selected Sim's |
| `shelldoorsummon` | doors/locks | n/d | yes | check `shelldoorsummon-check` | Summoned proprietor's exit queued only after its teleport |
| `rabbitholedoor` | doors/locks | no | - | check `rabbit-hole-door-check` | docs/505: RabbitHole.AnimateDoorOpen raises mDoorOpenCounts[door] and asks the RabbitHoleGeneric state machin… |
| `rhentryrelaxnear` | doors/locks | no | yes | check `rhentryrelaxnear-check` | an off-screen NPC near viewers whose rabbit hole entry plan failed gives up although the same rabbit hole has… |
| `speedrecalclock` | doors/locks | no | yes | check `speedrecalclock-check` + battery `speedrecalc` | Gameflow.RecalculateGameSpeed calls SetGameSpeed(.., lockSpeed: false), clearing mGameSpeedLocked and persist… |
| `statueline` | doors/locks | no | - | check `statue-line-dequeue` | BoxOfDanger+DoTrick.Cleanup does `if (line.Contains(Actor)) line.Dequeue()` on the static persisted statue li… |
| `trashoutsidefallback` | doors/locks | no | - | - | built 2026-11-19, default off, NOT RUN: NPC DisposeOutside on a lot whose outdoor can had no path: EA's non-f… |
| `viproomrefresh` | doors/locks | no | - | check `vip-room-refresh-modified` | ResortManager.RefreshVIPRooms removes a VIP room whose doors are all gone with VIPRooms.Remove inside its for… |
| `elevatorkitten` | elevators | n/d | yes | check `elevatorkitten-check` | Kittens and puppies refused by the elevator route planner as the doors refuse them |
| `elevatorresetriders` | elevators | n/d | yes | check `elevatorresetriders-check` + battery `elevator` | Riders waiting in a resetting elevator get the failsafe reset at once, not after 30 sim-minutes (household-vi… |
| `elevatorridertimeout` | elevators | n/d | yes | check `elevatorridertimeout-check` + battery `elevator` | Each elevator rider keeps its own 30-minute failsafe start (household-visible) |
| `queueproximity` | elevators | n/d | yes | check `queueproximity-check` | SimQueue.FindNewPlaceInLine (OrderByProximity) compares a squared distance with plain distances, so a new Sim… |
| `residentspawnwalk` | elevators | n/d | - | check `residentspawnwalk-check` | Resident's NPC-door spawn slot that EA's selected-Sim plan refuses is judged by the room walk with the reside… |
| `hiddenroomspawn` | elevators | no | - | check `hidden-room-spawn` | EA's unchecked placements : when Sim.AttemptToFindSafeLocation finds nothing, Sim.AttemptToPutInSafeLocation… |
| `backpacklotleak` | in-use/reservations | n/d | yes | check `backpacklotleak-check` | A backpack destroyed while in use stayed on the static lot-change event and threw on every lot change; now un… |
| `dogswimaroundlist` | in-use/reservations | n/d | yes | check `dogswimaroundlist-check` | A dog swimming around stays on the pool's use list (household-visible) |
| `floorswitchuselist` | in-use/reservations | n/d | yes | check `floorswitchuselist-check` | Standing on a tomb floor switch cut by a reset left the Sim on the switch's use list (Cleanup skips base); no… |
| `rubbleuselist` | in-use/reservations | n/d | yes | check `rubbleuselist-check` | Clearing a rubble pile cut by a reset left the Sim on the pile's use list (Cleanup skips base); now taken off… |
| `saunatubclear` | in-use/reservations | n/d | yes | check `saunatubclear-check` | The sauna stays marked in use after a reset or exception while bathing (household-visible) |
| `chestuselist` | in-use/reservations | no | yes | check `chestuselist-check` | A cut treasure-chest open keeps the Sim on the chest use list |
| `resetinusefree` | in-use/reservations | no | yes | check `resetinusefree-check` + battery `inuse` | An item stays in use in a Sim inventory after the Sim is reset mid-interaction |
| `vehfrankentaxi` | in-use/reservations | no | - | live `veh-leak-soak` | docs/544 L1-L5: EA vehicle leak paths (get-in interrupted, Sim destroyed while seated, dead parking reservati… |
| `boardingschoolmoney` | load-time-only | n/d | yes | check `boardingschoolmoney-check` | Boarding-school phone-call money ranges each read into their own pair (world load) |
| `elevatorsavedlog` | load-time-only | n/d | yes | check `elevatorsavedlog-check` | Log only: saved elevator rider entries by state at world load |
| `savedstacks` | load-time-only | n/d | - | - | built 2026-11-06: 760 saved method entries pointed at a stub at load, 761 entries left dropped, 762 stubs wit… |
| `visitlisteners` | load-time-only | n/d | yes | check `visitlisteners-check` | A removed visit host's listeners are not re-added after a load (B12 §28, docs/591 R6) |
| `curfewalarm` | load-time-only | no | yes | check `curfew-alarm` | Curfew police alarm handle survives a world quit |
| `fairyauratimer` | load-time-only | no | yes | check `fairyauratimer-check` | A fairy Creative or Mind aura gets its full time back on every load |
| `fireactivejob` | load-time-only | no | yes | check `fire-active-job-stale` | ActiveFireFighter.sActiveJob (plain static) survives a world quit: the loaded emergency is not adopted, house… |
| `flowersinuse` | load-time-only | no | yes | check `flowersinuse-check` | A rejected Give Flowers leaves the bouquet in use for good |
| `lotmgrcallbacks` | load-time-only | no | yes | check `lotmgrcallbacks-check` | LotManager.PreWorldLoad adds six World / LoadSaveManager callbacks (portal locked, door visible in live mode,… |
| `movingflag` | load-time-only | no | yes | check `movingflag-check` | A stale move-in-progress flag blocks every move after load |
| `npcseasonalparty` | load-time-only | no | yes | check `npcseasonalparty-check` | After a holiday reload the player's party no longer cancels the NPC holiday party |
| `nullservice` | load-time-only | no | - | check `null-service-check` | docs/504: Services.sAllServices is [PersistableStatic]; a saved service whose type no longer loads comes back… |
| `permazombiemoon` | load-time-only | no | yes | check `permazombie-moon` | A PermaZombie is never cured at the new moon after a save/load |
| `phoneloadreset` | load-time-only | no | yes | check `phoneloadreset-check` | A phone saved during a call stays busy after load: no calls out, incoming calls aborted |
| `photosession` | load-time-only | no | - | check `photo-session-check` | docs/508: a greeting-card photo session (festival booth, City Hall) ends only when SimDoneWithPhoto empties i… |
| `protestlastday` | load-time-only | no | yes | check `protestlastday-check` | The NPC protest cooldown static survives a world quit |
| `shelllighting` | load-time-only | no | - | check `shell-lighting-after-load` | BuildableShell.UpdateLightAlarms drops every lighting time already past today, so after a load a shell keeps… |
| `tombmusic` | load-time-only | no | yes | check `tombmusic-check` | Tomb music stays silent after quitting inside a tomb |
| `trickortreatalarm` | load-time-only | no | yes | check `trickortreat-alarm` | The weekly NPC trick-or-treat alarm survives a save/load (NPC trick-or-treaters every week) |
| `uselistprune` | load-time-only | no | yes | check `uselistprune-check` | Objects stay in use by Sims who are gone (EA validator never called) |
| `ageupkeepoutfits` | objects/misc | n/d | - | live `bp-deadrefs-saves` | issue #100: a failed or cancelled age-up (SimBuilderTask.Simulate catch-all, then FinishOutfitSwap with empty… |
| `awkwardscience` | objects/misc | n/d | yes | check `awkwardscience-check` | The Socially Awkward science gain bonus applies to Science only (B12 §18, docs/591 C1) |
| `bathcleanup` | objects/misc | n/d | yes | check `bathcleanup-check` | A queued second bath's cleanup leaves the bather's duck alarm and footprint alone |
| `bathsituationfx` | objects/misc | n/d | yes | check `bathsituationfx-check` | A bath's privacy situation is not exited twice and the old bubble effect is disposed |
| `buffremovedelayed` | objects/misc | n/d | yes | check `buffremovedelayed-check` | Removing a buff with a pending delayed add also removes the live buff |
| `cancelposturedef` | objects/misc | n/d | yes | check `cancelposturedef-check` | Cancelling a posture does not queue a second stand-up when one is at the head |
| `celebalarmnull` | objects/misc | n/d | yes | check `celebalarmnull-check` | Celebrity alarm removal works for a celebrity who is not instantiated |
| `chessdefeated` | objects/misc | n/d | yes | check `chessdefeated-check` | The highest-ranking defeated chess opponent is the strongest one, stored with its rank (B12 §19, docs/591 C2) |
| `chipcomponentorphan` | objects/misc | n/d | yes | check `chipcomponentorphan-check` | Consumed trait chip components destroyed instead of left in the world (found by the RE pass) |
| `cleanupvfx` | objects/misc | n/d | yes | check `cleanupvfx-check` | An interaction's named effects are disposed at its cleanup in a live game (also in the djcleanup copy) (B12 §… |
| `compositionnull` | objects/misc | n/d | yes | check `compositionnull-check` | Singing and band composition imports skip missing keys and unknown compositions (B12 §16, docs/590 A7, 591 C8) |
| `computergametype` | objects/misc | n/d | yes | check `computergametype-check` | An autonomous computer game's random pick is not kept in the shared definition |
| `deadrefconvo` | objects/misc | n/d | - | check `conversation-gone-check` | Conversation.mMembers keeps a Sim reset or destroyed mid-talk; EA's RemoveSim cannot take it out and throws l… |
| `distressgate` | objects/misc | n/d | yes | check `distressgate-check` | The higher-priority distress test finds buff distress too |
| `eatpostonce` | objects/misc | n/d | yes | check `eatpostonce-check` | A finished eat is not posted a second time by EatHeldFood.Cleanup (kEatIEdible, Stuffed, food-truck rolls onc… |
| `facialidleweight` | objects/misc | n/d | yes | check `facialidleweight-check` | Buff and trait facial idles share their chance as floats (7+ idles no longer weigh 0) |
| `fastreftable` | objects/misc | n/d | - | - | save identity tables converted, most entries in one table (on grow and Clear), table grows |
| `fearfaintundo` | objects/misc | n/d | yes | check `fearfaintundo-check` | A Fear of Humans faint that ends early restarts the robot and removes its jig (B12 §10, docs/590 B13) |
| `foodtraygrill` | objects/misc | n/d | yes | check `foodtraygrill-check` | A food tray whose grill step fails is put down and a failed grill push no longer throws (B12 §15, docs/590 A6) |
| `fridgetestnull` | objects/misc | n/d | yes | check `fridgetestnull-check` | Opening a fridge with no inventory component is tested without a throw |
| `guitargnomeboost` | objects/misc | n/d | yes | check `guitargnomeboost-check` | Guitar play takes off every Elvis gnome boost it added |
| `harvestextras` | objects/misc | n/d | yes | check `harvestextras-check` | Gatherer and Science extra harvestables reach the harvester's inventory, and mushroom collecting credits the… |
| `innerbeautyonce` | objects/misc | n/d | yes | check `innerbeautyonce-check` | A mean social is handled once however many Sims have Inner Beauty |
| `intmultiple` | objects/misc | n/d | yes | check `intmultiple-check` | RandomUtil.GetIntMultiple can return its top multiple (sales up to 80%, idles up to 30 min) (B12 §22, docs/59… |
| `krakenfriendsailor` | objects/misc | n/d | yes | check `krakenfriendsailor-check` | Friend of the Kraken immunity kept when a Sailor sits before the friend |
| `laundrymoodeach` | objects/misc | n/d | - | check `laundrymoodeach-check` | Every Sim dressed from an expensive-washer load gets the mood multiplier once |
| `leaveconvnull` | objects/misc | n/d | yes | check `leaveconvnull-check` | Leaving a conversation the Sim never joined does not count as a social (B12 §37, docs/592 S2) |
| `lightcustom` | objects/misc | n/d | yes | check `lightcustom-check` | A room or house custom light level copies the chosen level (B12 §33, docs/591 R12) |
| `lookatcopy` | objects/misc | n/d | yes | check `lookatcopy-check` | Asking for a room's look-at objects without Sims no longer removes the Sims from the registry (B12 §40, docs/… |
| `meltalarm` | objects/misc | n/d | yes | check `meltalarm-check` | An ice sculpture with no known artist melts without a throw and a restart replaces its melt alarm (B12 §35, d… |
| `meteormagnetorphan` | objects/misc | n/d | yes | check `meteormagnetorphan-check` | A cancelled meteor-magnet attract destroys its out-of-world fragment |
| `moodidleflavor` | objects/misc | n/d | yes | check `moodidleflavor-check` | MoodIdle continuation rows join their flavour instead of being dropped |
| `moodtankreset` | objects/misc | n/d | yes | check `moodtankreset-check` | A recovered mood clears the remembered angry tank so the next angry mood tanks again |
| `motivemultonce` | objects/misc | n/d | yes | check `motivemultonce-check` | The Carefree / LaidbackLemon / EfficientChip multiplier is applied once per motive update, not per matching d… |
| `mountfishcost` | objects/misc | n/d | yes | check `mountfishcost-check` | Mounting a fish charges that fish's cost, not the last fish tested (B13 §11, docs/593 F1) |
| `newspaperdef` | objects/misc | n/d | yes | check `newspaperdef-check` | Reading the newspaper autonomously no longer renames the newspaper menu root (B12 §36, docs/591 R21) |
| `nocommercialcar` | objects/misc | n/d | yes | check `nocommercialcar-check` | Driverless traffic car with no commercial lot waits and drives off instead of throwing |
| `oppcategoryprotect` | objects/misc | n/d | yes | check `oppcategoryprotect-check` | Opportunity accept that EA rejects adds no cull protection |
| `oppfailcharge` | objects/misc | n/d | yes | check `oppfailcharge-check` | A failed opportunity's deleted-object penalty is charged once, not twice (B13 §9, docs/593 O1) |
| `opptrackerrelisten` | objects/misc | n/d | - | check `opptrackerrelisten-check` | Opportunity completion listener is not registered twice when its target Sim appears |
| `picnicants` | objects/misc | n/d | yes | check `picnicants-check` | A packed picnic basket loses its ant alarm and the ant effect is disposed (B12 §26, docs/591 R3) |
| `portasknohome` | objects/misc | n/d | - | check `portasknohome-check` | Ask-to-change-port social test returns false for a Sim with no home lot instead of throwing |
| `privacyothers` | objects/misc | n/d | yes | check `privacyothers-check` | A Sim who left a private room's lot gets a new room check when it comes back (B12 §20, docs/591 C4) |
| `reactioncopy` | objects/misc | n/d | yes | check `reactioncopy-check` | Each nearby Sim starts a one-shot reaction from the caller's reaction, not an earlier Sim's trait reaction |
| `reactiongrumpy` | objects/misc | n/d | yes | check `reactiongrumpy-check` | A Grumpy Sim's non-positive reaction type is set instead of staying Angry |
| `romancecooldown` | objects/misc | n/d | yes | check `romancecooldown-check` | The bad-date cooldown in a group of three or more lasts hours, not minutes (B12 §32, docs/591 R11) |
| `roundupreturn` | objects/misc | n/d | yes | check `roundupreturn-check` | A curfew round-up with no active Sim leaves instead of throwing in Init (B13 §15, docs/593 V3) |
| `sandwichalarm` | objects/misc | n/d | yes | check `sandwichalarm-check` | A theatre removes the daily sandwich-board reset alarm it holds before a show-end re-adds it, so back-to-back… |
| `scavengernolot` | objects/misc | n/d | yes | check `scavengernolot-check` | Scavenger opportunity in a world without community lots is accepted without the throw |
| `sciencefxstop` | objects/misc | n/d | yes | check `sciencefxstop-check` | A science outcome's old effect set is stopped before a new one starts (B12 §29, docs/591 R7) |
| `shoodef` | objects/misc | n/d | yes | check `shoodef-check` | The Dislikes Children shoo uses its own definition instead of writing into the shared shoo Singleton (B12 §5,… |
| `shroomslots` | objects/misc | n/d | yes | check `shroomslots-check` | Mushrooms fill their slots in order and get their harvest timeout instead of skipping slots and throwing (B13… |
| `slavewakereason` | objects/misc | n/d | yes | check `slavewakereason-check` | BedSleep.Run's spooning-slave wait re-reads Actor.ExitReason every pass, so the reason that woke the slave (H… |
| `snublistener` | objects/misc | n/d | yes | check `snublistener-check` | A destroyed Sim drops its SnubManager age-up listener |
| `socialnoeffect` | objects/misc | n/d | yes | check `socialnoeffect-check` | Social starts with no matching rule are counted (the fix is ts3native ilp_socialnoeffect) |
| `stereoupgrade` | objects/misc | n/d | yes | check `stereoupgrade-check` | A speaker upgrade does not start music on a silent stereo; a dance pair's end dismisses only its own dancers |
| `talkoccupationchance` | objects/misc | n/d | yes | check `talkoccupationchance-check` | Talk-about-occupation Unemployment chance 1/(count+1) as a float, not integer division |
| `topictestnull` | objects/misc | n/d | - | check `topictestnull-check` | Conversation topic tests answer instead of throwing for a missing Sim or Athletic skill |
| `traitlistenerremove` | objects/misc | n/d | - | check `traitlistenerremove-check` | A lost trait's event listeners are removed |
| `traitremovenull` | objects/misc | n/d | - | check `traitremovenull-check` | Removing a trait missing from the trait dictionary finishes instead of throwing for an instantiated Sim |
| `tvdisclistener` | objects/misc | n/d | yes | check `tvdisclistener-check` | A new home-movie disc replaces the old disc's disposed-listener |
| `tvsyncalarm` | objects/misc | n/d | yes | check `tvsyncalarm-check` | A TV channel change resets the sync-event alarm handles so viewer reactions re-arm |
| `tvwatchskill` | objects/misc | n/d | yes | check `tvwatchskill-check` | A WatchTV that never started does not stop the Sim's channel skill gain |
| `twinbellyshape` | objects/misc | n/d | yes | check `twinbellyshape-check` | Belly shape between twins and triplets uses float division |
| `vehiclefxhelper` | objects/misc | n/d | yes | check `vehiclefxhelper-check` | Vehicle tire / hover effect helper destroyed before EA creates the next one (orphan helpers kept 2 global lis… |
| `wellstealcooldown` | objects/misc | n/d | yes | check `wellstealcooldown-check` | Coins cannot be stolen from a wishing well right after a wish (EA's cooldown) (B13 §8, docs/593 B2b) |
| `artsellnoartist` | objects/misc | no | yes | check `artsellnoartist-check` | Selling art whose painter is gone pays and keeps the item |
| `bandjam` | objects/misc | no | - | check `band-jam-sweetmove` | JamController.SetSweetMove tests (j != null && s != Exited) // s != PlayingSweetMove: a Sweet Move on a leavi… |
| `barorderlog` | objects/misc | no | - | - | (no repro text; see BarOrderLog.cs) |
| `blogorphan` | objects/misc | no | - | - | built 2026-11-11: root listeners left by thrown-away tests (2260, also off) / removed (2261) / errors (2262)… |
| `bribeguard` | objects/misc | no | - | check `bouncer-bribe-null` | VelvetRopes.ChargeSimForBribe reads mBribeDictionary without a null check; NightlyCleanUp nulls it, so a brib… |
| `bubblepipeguard` | objects/misc | no | - | check `bubblebar-pipe-null` | BubbleBar+BlowBubbles.Run uses the pipe prop (SetHiddenFlags) before its null check, so a pipe that was not c… |
| `buffpausedtimeout` | objects/misc | no | yes | check `buffpausedtimeout-check` | RemoveElement(buff,id,inv) with only paused sources left re-adds the moodlet with timeout float.MinValue |
| `cleanflag` | objects/misc | no | - | check `clean-flag-left` | FirePit+PutAway and PicnicBasket+CleanUp set BeingCleaned and return true on their already-cleaned exit witho… |
| `datadiscputback` | objects/misc | no | yes | check `datadiscputback-check` | DataDiscStorage AddAllDataDiscs / DumpAllDataDiscs remove each disc from one inventory and return false when… |
| `deadsimhollow` | objects/misc | no | - | - | built 2026-11-09, test: destroyed Sims held 6+ sim hours by an unknown holder get their own reference fields… |
| `djcleanup` | objects/misc | no | - | check `dj-cleanup-other-set` | DJTurntable DoPlay.Cleanup ends whoever owns the playing booth (exit reason, ExitDJBooth, tip jar destroyed)… |
| `dreamrootorphan` | objects/misc | no | - | - | built 2026-11-11: root listeners left by thrown-away tests (2260, also off) / removed (2261) / errors (2262)… |
| `eatoutsidecount` | objects/misc | no | - | check `eat-outside-count` | Bistro EatOutside: a follower that reported done and is then cancelled is decremented twice, so the leader st… |
| `fireplaceherb` | objects/misc | no | yes | check `fireplaceherb-check` | Fireplace.AddHerb overwrites a burning herb's VFX and haze ReactionBroadcaster without stopping or disposing… |
| `hottubwoohoofx` | objects/misc | no | - | check `hottub-woohoo-fx` | HotTubBase CuddleSeatedWooHoo starts the woohoo effect at animation event 110 and stops it only at event 111… |
| `jazzparam` | objects/misc | no | - | check `jazz-param-check` | issue #166 build 21: Sim.UpdateJazzParameter null-checks the interaction half but reads Posture.CurrentStateM… |
| `megaphonerun` | objects/misc | no | yes | check `megaphonerun-check` | Sim.WakeWithMegaphone.Run calls Actor.RequestWalkStyle(Run) (a Sim-level count) before pushing the RouteAway… |
| `oppdeadlisteners` | objects/misc | no | - | check `opp-dead-listeners` | Opportunity.RemoveAllIncompleteListeners never removes mSimDiedListener / mSimDescriptionDisposedListener / m… |
| `paparazzicooldown` | objects/misc | no | - | - | (no repro text; see PaparazziCooldown.cs) |
| `returnhomealarms` | objects/misc | no | yes | check `returnhomealarms-check` | Quitting right after a return home skips the next return-home sequence |
| `royaltyorphan` | objects/misc | no | yes | check `royaltyorphan-check` | An orphan royalty alarm piles up per trip for each writer |
| `sandscoins` | objects/misc | no | yes | check `sandscoins-check` | Sands of Understanding bonus coins are never given |
| `shuffleboardnpccap` | objects/misc | no | - | - | (no repro text; see ShuffleboardNpcCap.cs) |
| `swiperestore` | objects/misc | no | yes | check `swiperestore-check` | TraitFunctions+SwipeSomething.Run removes the victim's object from the world before adding the stolen copy to… |
| `upgradefailalarm` | objects/misc | no | - | check `upgrade-fail-alarm` | A failed Handiness upgrade cut short (cancel, interrupt) leaves its Failure Upgrade Alarm, which later adds C… |
| `wildlotskip` | objects/misc | no | - | - | built 2026-11-19, default off, NOT RUN: (animal, lot) pairs remembered after a PortalLocked / NoPath GoToLot… |
| `broomarenachoice` | occults | n/d | yes | check `broomarenachoice-check` | Ride in Arena from a broom uses that broom (and no longer rewrites a running ride's broom) (household-visible) |
| `broomtipperformer` | occults | n/d | yes | check `broomtipperformer-check` | Broom arena watcher skips the tip when the ride ended during the route (no paid NRE) (household-visible) |
| `elvisgnomecap` | occults | n/d | yes | check `elvisgnomecap-check` | One Elvis gnome per home lot (intent inferred from MagicGnomeVampire) (household-visible) |
| `goodbyepure` | occults | n/d | yes | check `goodbyepure-check` | Building the goodbye pie menu no longer removes SoCold or despawns a Haunted ghost |
| `hauntedgnomeinv` | occults | n/d | yes | check `hauntedgnomeinv-check` | A haunted gnome loaded in an inventory kept spawning ghosts and scaring cowards; now cleaned like on inventor… |
| `houdinignomeleak` | occults | n/d | yes | check `houdinignomeleak-check` | The magician gnome created near a magic box leaked when it could not be placed, and a failed create threw; no… |
| `hypnotrickrelease` | occults | n/d | yes | check `hypnotrickrelease-check` | A hypnotized volunteer stays frozen because the trick never clears the route lock (household-visible) |
| `mermaiddehydratelook` | occults | n/d | yes | check `mermaiddehydratelook-check` | A mermaid's look-ats come back after Dehydrate (household-visible) |
| `mermaidsharknull` | occults | n/d | yes | check `mermaidsharknull-check` | Summon Shark failed placement takes EA's existing-shark fallback instead of a NullReferenceException (househo… |
| `moondiallistener` | occults | n/d | yes | check `moondiallistener-check` | A deleted moon dial removes its moon-change listener |
| `moonriselistener` | occults | n/d | yes | check `moonriselistener-check` | Fairy / werewolf kMoonRise listener removed before EA re-adds it at each instantiation |
| `mummycursetrait` | occults | n/d | yes | check `mummycursetrait-check` | Mummy curse removes a visible trait (not a hidden one) before adding Evil (household-visible) |
| `nemesisnoromance` | occults | n/d | - | check `nemesisnoromance-check` | The Imminent Nemesis spell is not limited to romance-eligible Sims (user decision, option A) |
| `outfitwaitend` | occults | n/d | yes | check `outfitwaitend-check` | Sim.SwitchOutfitHelper.Wait loops on Sleep(1) until the native swap is ready, which never happens while the S… |
| `petpoolghost` | occults | n/d | yes | check `petpoolghost-check` | Pet pool ids whose pet is gone are dropped before a random removal |
| `posturepopcycle` | occults | n/d | - | live `posture-pop-loop-check` | issue #89: EA Sim.set_Posture takes its Compatible branch on a pop (new posture = current.PreviousPosture), s… |
| `spellbooksummonload` | occults | n/d | yes | check `spellbooksummonload-check` | A learned Summon spell is dropped from the spellbook on every import (travel, move, exported Sim) (household-… |
| `spelltargetend` | occults | n/d | yes | check `spelltargetend-check` | A spell's target stops waiting when the cast ends (B12 §14, docs/590 A3) |
| `spoccultpick` | occults | n/d | yes | check `spoccultpick-check` | Story occult removals pick a random occult Sim, not always the first |
| `tricksocials` | occults | n/d | yes | check `tricksocials-check` | A magician trick that ends early turns the watcher's socials back on (B12 §9, docs/590 B13) |
| `vampirefruitsnack` | occults | n/d | yes | check `vampirefruitsnack-check` | An Outstanding vampire fruit gave Sated instead of Sanguine Snack (> instead of the tuned >=); now >= (househ… |
| `voodoofairyfadein` | occults | n/d | yes | check `voodoofairyfadein-check` | A fairy bound to a voodoo doll stays invisible when the transform is cut short (household-visible) |
| `werewolffind` | occults | n/d | yes | check `werewolffind-check` | A werewolf hunt with no metal or gem in range finds nothing instead of throwing, and an insect adds to the hu… |
| `ageupswap` | occults | no | - | check `ageup-swap-check` | docs/503 (I-503-1): SimBuilderTask.FinishModelLoad ignores a refused Sim.SwitchToOutfitDelayed and waits fore… |
| `compositorghost` | occults | no | - | - | built 2026-11-11: ghost CASCompositorController entries after Unload (2296, also off) / removed (2297) / left… |
| `dragoncurserun` | occults | no | yes | check `dragoncurserun-check` | BabyDragon.PhantasmalCurse.DoAnimation calls TargetSim.SimRoutingComponent.RequestWalkStyle(Run) before its R… |
| `geniebanish` | occults | no | - | check `genie-banish-pick` | OccultGenie+RemoveSim.Run picks allCommunityLots[RandomUtil.GetInt(Count)]; GetInt(max) is inclusive, so 1 ba… |
| `ghostspawnargs` | occults | no | - | check `ghost-spawn-args` | Urnstone.GhostSpawn(bool, Vector3, Lot, bool) drops its lot and isAngry arguments: Ghost Hunter angry ghosts… |
| `outfitwaitsaved` | occults | no | - | - | built 2026-11-09: MagicMirror MagicalMakeover.ChangeOutfitCB alarm tasks (AlarmOneShot, resumed from the save… |
| `reaperflow` | occults | no | - | check `reaper-flow` | The Grim Reaper stays on the lot for good when its vending-machine soda fails (no failure callback), and Chal… |
| `sarcmummynpc` | occults | no | yes | check `sarcmummynpc-check` | Sarcophagus.CommonCleanup (Dispose, DoReset) destroys the tomb mummy Sim but leaves its SimDescription in Hou… |
| `simrefsdeadsim` | occults | no | - | - | built 2026-11-09: live Sims' references to gone Sim objects dropped hourly: SocialComponent.mLastTalkingTo cl… |
| `storeworldevents` | occults | no | - | check `store-world-events` | Store objects (DeepFryerVegas, IceCreamMaker, VoodooDoll, ChocolateFountain, WalkerClassic) add instance hand… |
| `unicornspawnfail` | occults | no | yes | check `unicornspawnfail-check` | OccultUnicorn+UnicornSpawn.Run fades the NPC unicorn out (it was spawned at opacity 0) and returns false when… |
| `vampirehuntglow` | occults | no | - | check `vampire-hunt-glow` | OccultVampire VampireHunt.LoopDel drops hunt candidates that left the lot without clearing the HeatVision vis… |
| `catchpreyodds` | pets/horses | n/d | yes | check `catchpreyodds-check` | Snakes and turtles are a sure catch only with Slither Stalker, like lizards (B12 §25, docs/591 R2) |
| `catgymwatch` | pets/horses | n/d | - | check `catgymwatch-check` | Sim passing a cat on the jungle gym gets the gym set so the watch reaction plays |
| `cattoypickerrel` | pets/horses | n/d | yes | check `cattoypickerrel-check` | Cat wall dancer Play With lists Sims with no relationship at the shipped -10 threshold (household-visible) |
| `deadrefguards` | pets/horses | n/d | - | live `bp-deadrefs-saves` | issue #91: EA holders keep disposed SimDescriptions and destroyed Sims (Relationship.Get(d) re-adds a key for… |
| `disposebase` | pets/horses | n/d | yes | check `disposebase-check` | JunkPile, ShoppingRegister, PetBedReward, BubbleColumn and FogMarker Dispose run GameObject.Dispose |
| `dogswimslots` | pets/horses | n/d | yes | check `dogswimslots-check` | A hydrophobic dog sent to swim with every pool slot blocked no longer throws (household-visible) |
| `dreamcleanup` | pets/horses | n/d | yes | check `dreamcleanup-check` | Wish manager clean-up index kept right (pet-subject nodes unfiled when removed, woken repeatable wishes filed… |
| `easpreadpets` | pets/horses | n/d | - | check `bp-easpread-pets` | EA's pet pool alarm (PetPoolManager.AlarmCallBack) builds one random pet through CAS part searches in one ala… |
| `eatpreycancel` | pets/horses | n/d | yes | check `eatpreycancel-check` | A cancelled or failed prey eat keeps the prey and re-enables its interactions (B12 §34, docs/591 R13) |
| `equestriantrophyname` | pets/horses | n/d | yes | check `equestriantrophyname-check` | Equestrian trophies read the horse name from the rider key on import, showing the rider as the horse; now rea… |
| `gourmetpetrecipe` | pets/horses | n/d | yes | check `gourmetpetrecipe-check` | Gourmet pet food: horse-only household keeps granola; no empty-list throw (household-visible) |
| `harvesteatvfx` | pets/horses | n/d | yes | check `harvesteatvfx-check` | Pet harvestable-eating effects stored and stopped (no leaked native effects) (household-visible) |
| `horseranchchallenge` | pets/horses | n/d | yes | check `horseranchchallenge-check` | Visit-a-horse-ranch challenge hidden for households without a horse (household-visible) |
| `loyaldoglay` | pets/horses | n/d | yes | check `loyaldoglay-check` | A Loyal dog's lie-down near its human targets the dog so its test passes (B12 §27, docs/591 R5) |
| `maildogchaser` | pets/horses | n/d | yes | check `maildogchaser-check` | Mail carrier's chasing dog cleared when the chase ends (EA kept it for every later delivery) |
| `petdreamtimer` | pets/horses | n/d | yes | check `petdreamtimer-check` | A sleeping pet's dream balloon timer resets after a dream of its human too (B12 §17, docs/590 A10) |
| `petharvestvfx` | pets/horses | n/d | yes | check `petharvestvfx-check` | A pet eating from a harvest plant stops its eating effect (B13 §17, docs/593 H1) |
| `petwakebed` | pets/horses | n/d | yes | check `petwakebed-check` | Pet WakeUpSim Cleanup leaves the bed part's ContainedSim set to the pet when the pet is reset between jumping… |
| `pingpongopponent` | pets/horses | n/d | yes | check `pingpongopponent-check` | Ping pong opponents and picker exclude pets, babies and toddlers (household-visible) |
| `wildhorselot` | pets/horses | n/d | yes | check `wildhorselot-check` | Wild horses weight big parks as tuned and respect stray-wildlife gnomes in the fallback (household-visible) |
| `birdtvnull` | pets/horses | no | - | check `bird-tv-null-channel` | MinorPetBirdLarge.ShouldReactToTv dereferences TV.CurrentChannel for every TV that is on; a TV switched on wh… |
| `deadrefsweep` | pets/horses | no | - | live `bp-deadrefs-saves` | issue #91: EA holders keep disposed SimDescriptions and destroyed Sims (Relationship.Get(d) re-adds a key for… |
| `fieldtripadult` | pets/horses | no | - | check `field-trip-adult` | FieldTripSituation.BringAdultToSchool loops forever (game freeze) when no NPC adult passes its test, since re… |
| `horsetraitscore` | pets/horses | no | yes | check `horsetraitscore-check` | Horse traits never count in equestrian competitions (the rider is checked) |
| `petfireplan` | pets/horses | no | - | check `pet-fire-plan` | FireSource.ReactToFire.Run plans a route to the nearest fire for every actor, but only DoHumanReactions uses… |
| `wildherdmother` | pets/horses | no | yes | check `wildherdmother-check` | Taming or adopting a wild herd mother with foals throws and aborts |
| `boatparkgone` | phone | n/d | - | - | built 2026-11-11, default ON (shipped without repro, Rodney 11-11): BoatOwnable.Park calls skipped because th… |
| `dancefloorlistguard` | phone | n/d | - | - | built 2026-11-08 (pair dfguard-1108, not run): 928 guarded FindAndRouteOntoADanceFloorWithFollower calls, 929… |
| `gigmaster` | phone | n/d | yes | check `gigmaster-check` | Gig Master completes at the number of gigs its text names (B12 §21, docs/591 C5) |
| `photofunds` | phone | n/d | yes | check `photofunds-check` | A phone photo with short funds takes the funds instead of adding them |
| `socialworkeronlot` | phone | n/d | - | check `bp-socialworker-onlot` | issue #87: SocialWorker*Situation WaitToRoute.Init goes to RouteToLot at once when the worker is already on t… |
| `buffupdate` | phone | no | - | check `buff-update-check` | issue #166 build 20: BuffManager.UpdateBuffs walks the live buff table and calls each timed buff's OnTimeOutU… |
| `compositorleak` | phone | no | - | - | built 2026-11-11: `CASCompositorController.Unload` calls with a tool, also with the switch off (2223) / dict… |
| `genealogycycle` | phone | no | - | - | built 2026-11-11: ancestor repeats skipped (2229) / parent loops stopped (2230) / Divorce with no spouse (223… |
| `gigwaitleave` | phone | no | - | check `gig-wait-leave` | Stylist makeover / Singer singagram wait pulse calls ForceLeaveJob when the customer waited too long but does… |
| `invnotyet` | phone | no | - | check `inv-not-yet-check` | Sim.TryAddObjectToInventory(IGameObject) calls base.Inventory.TryToAdd(obj) with no null check; Inventory is… |
| `phonecallrearm` | phone | no | yes | check `phonecallrearm-check` | Random phone calls stop for the session after the prom-call branch throws |
| `phonecallschedule` | phone | no | yes | check `phonecallschedule-check` | A load or travel arrival before 7:00 gets no random phone calls that day |
| `resumetns` | phone | no | yes | check `resumetns-check` | The upload-resume phone notice never shows for return-home graduates |
| `rewardgiver` | phone | no | - | check `reward-giver-check` | GameObject.SetRewardGiver(sim) calls AddComponent<ObjectOwnerComponent>(sim, null); AddComponentInternal type… |
| `woohootywait` | phone | no | yes | check `woohootywait-check` | The WooHooty-text invitee can wait forever for the texter |
| `putdownplaced` | put-down/surfaces | n/d | yes | check `putdownplaced-check` | Putting an object on a surface fires the placed event, not removed (B12 §44, docs/592 S13) |
| `tagbagsurfaceleak` | put-down/surfaces | n/d | yes | check `tagbagsurfaceleak-check` | Street art prep leaked its surface object when no target lot was found; now destroyed (household-visible) |
| `barputdownwait` | put-down/surfaces | no | yes | check `barputdownwait-check` | BarAdvanced+PutDownCarriedObjectHelper.Wait loops `while (!mHasExecuted) Sleep(0)`, and only the anim-event h… |
| `petputdownwait` | put-down/surfaces | no | yes | check `petputdownwait-check` | A carried pet waits 30 sim-min for a carrier whose PutDownPet already failed (no FinishLinkedInteraction on i… |
| `putdowninuse` | put-down/surfaces | no | yes | check `putdowninuse-check` + battery `putdown` | A put-down on a surface cut after the in-use mark leaves the item in use and the surface held |
| `putdownsurfaceuse` | put-down/surfaces | no | yes | check `putdownsurfaceuse-check` | PutDown.TryToPlaceOnSurface adds the Actor to the surface's use list and sets the item in use, then returns f… |
| `asyncreplan` | routing/unseen/perf | n/d | - | - | (no repro text; see HitchCause.cs) |
| `asyncreplanforeign` | routing/unseen/perf | n/d | - | live `ip-foreign-replan-loop` | docs/176: Isla Paradiso visible freezes of 300-450 ms from synchronous ReplanFromPosition plans on the sim th… |
| `autlastsearch` | routing/unseen/perf | n/d | - | - | (no repro text; see AutLive.cs) |
| `autliveguard` | routing/unseen/perf | n/d | - | - | go-live cost guard: 538 windows closed (`autliveguard_windows`), 539 skip windows (`_skipwindows`), 540 eligi… |
| `autlivelate` | routing/unseen/perf | n/d | - | - | late slices checked (`autlivelate_slices`), refused by age / epoch / state / lot / head / actor / other (`_re… |
| `autlivelod` | routing/unseen/perf | n/d | - | - | built 2026-11-15 (lane L5), default off, NOT RUN: go-live results dropped because `Autonomy.mbHighLOD` flippe… |
| `autliveseen` | routing/unseen/perf | n/d | - | - | built 2026-11-15 (feature/mono-autshadow-1115), default off, NOT RUN: seen-NPC searches posted on the exact g… |
| `autliveseendry` | routing/unseen/perf | n/d | - | - | built 2026-11-15 (feature/mono-autshadow-1115), default off, NOT RUN: seen-NPC dry compares that differ from… |
| `autlivetune` | routing/unseen/perf | n/d | - | - | built 2026-11-15 (lane L5), default off, NOT RUN: go-live results dropped because an allow-listed pair's tuni… |
| `autoffenv` | routing/unseen/perf | n/d | - | - | env 448 items, 449 A0, 450 A2, 451 A4, 452 A5, 453 exceptions, 454 dry checks, 455 dry equal (cut answer = li… |
| `autofft2ic` | routing/unseen/perf | n/d | - | live `tier2-ic-rows-dry` | none |
| `autofftick` | routing/unseen/perf | n/d | - | - | Mode R tick item: 534 items, 535 A0 (worker read the cut's tick), 536 A2 (`off-tick` record), 537 unfinished… |
| `autoffunseen` | routing/unseen/perf | n/d | - | - | built 2026-11-13, default off, NOT RUN: fresh env / bcast answers sent to the game's own path by scope: env 9… |
| `autshadowverify` | routing/unseen/perf | n/d | - | - | built 2026-10-20 : 106-111 Mode R classes A0-A5, 112-115 Mode W report (pairs both scored, equal score, Test… |
| `autslicetime` | routing/unseen/perf | n/d | - | live `autonomy-memo` | Autonomy pair scoring asks Sim.IsAllowedInRoom many times per pair (GetLot + two dictionary probes + the port… |
| `autt2callpet` | routing/unseen/perf | n/d | - | live `tier2-p3-dry` | none |
| `autt2dragwarm` | routing/unseen/perf | n/d | - | live `tier2-p1-dry` | none |
| `autt2l0` | routing/unseen/perf | n/d | - | - | 903-905 local-phase searches by actor class (household incl. unknown, seen, unseen non-household), 906-908 th… |
| `autt2lod` | routing/unseen/perf | n/d | - | live `tier2-h7-dry` | none |
| `autt2suite` | routing/unseen/perf | n/d | - | live `tier2-p2-dry` | none |
| `autunseencmp` | routing/unseen/perf | n/d | - | - | built 2026-11-01 (unseen4): worker-pass/game-fail compare pairs whose worker score is 0 (BestScores drops bot… |
| `autunseenrh` | routing/unseen/perf | n/d | - | - | built 2026-11-01 (unseen4): unseen-direct pairs with a RabbitHole target sent to the game's C1; in Dry the sa… |
| `backoff` | routing/unseen/perf | n/d | - | live `bp-route-cache` | Repeated route plans to an unreachable spot each run a full failed search |
| `boatgetoutdiag` | routing/unseen/perf | n/d | - | live `ip-boat-getout-stress` | docs/257 §7-§9: EA Boat.GetOut checks the SittingInBoat posture, clears RoutingParent, yields in FadeOut, the… |
| `boatreplanlog` | routing/unseen/perf | n/d | - | - | built 2026-11-12, NOT RUN: boat replans booked with no `sUbLastWhy` entry (948, agent `unseenboat_norefusal_n… |
| `boatstaleswimmer` | routing/unseen/perf | n/d | yes | check `boatstaleswimmer-check` | Boat route start removes swimmers with no Sim before EA's loop (no throw, swimmers waited for) |
| `businessplannerrun` | routing/unseen/perf | n/d | yes | check `businessplannerrun-check` | Business planner: autonomous use is rolled per run and a Sim who gives up waiting no longer pockets the plann… |
| `cachedreflection` | routing/unseen/perf | n/d | - | - | serializer type caches started with the switch on (one per save), answers from the cache, answers stored |
| `caneearly` | routing/unseen/perf | n/d | - | live `cane-early-out` | Cane.SimRequiresCaneWalk searches the Sim's inventory (Inventory.FindAll<Cane>) before testing the walk style… |
| `catnamecache` | routing/unseen/perf | n/d | - | live `lane-l4-memos` | Motive.Update removes perma-moodlet buffs the Sim does not have (RemoveElement probes an enum-keyed dictionar… |
| `dancefloorlistdiag` | routing/unseen/perf | n/d | - | live `dance-together-floor` | docs/253: DanceFloor.FindAndRouteOntoADanceFloorWithFollower threw InvalidOperationException "Collection was… |
| `disposespread` | routing/unseen/perf | n/d | - | - | built 2026-10-24 : 280 destroys queued, 281 destroyed by our task (one per tick), 282 destroyed at once by a… |
| `doormirror` | routing/unseen/perf | n/d | - | - | built 2026-10-18 : `CommonDoor.ImportContent` postfixes run, lot invalidations, lot door tables built, builds… |
| `easpreaddecay` | routing/unseen/perf | n/d | - | check `bp-easpread-decay` | EA's midnight relationship decay (SocialComponent.RelationshipDecay -> Relationship.DecayAll) steps every rel… |
| `easpreadlots` | routing/unseen/perf | n/d | - | check `bp-easpread-lots` | EA's 3 AM venue cleanup LotManager.CleanUpLotsAndInventories cleans every lot (Lot.MagicallyCleanUp) and arms… |
| `elvisgnomeguard` | routing/unseen/perf | n/d | yes | check `elvisgnomeguard-check` | Elvis gnome spawn/music guards: no-home throws, FPBB plate NRE, leaked gnome, stacked music loops (household-… |
| `errortrapspread` | routing/unseen/perf | n/d | - | check `bp-errortrap-spread-inventory` | NRaas ErrorTrap's load-up checks run in one FunctionTask tick one sim-minute after every load: a 0.5-0.9 s fr… |
| `errortrapspreadverify` | routing/unseen/perf | n/d | - | live `bp-nraas-upstream-verify` | NRaas ErrorTrap's load-up checks run in one FunctionTask tick one sim-minute after every load: a 0.5-0.9 s fr… |
| `failreplan` | routing/unseen/perf | n/d | - | - | obstacle replans switched off, route-fail walks skipped |
| `gm6trace` | routing/unseen/perf | n/d | - | - | (no repro text; see AutShadow.cs) |
| `gohome` | routing/unseen/perf | n/d | - | live `bp-hogans-gohome` | Townies placed by the building shortcut at Hogan's Deep-Fried Diner later stand stuck in Go Home (bp-mod run,… |
| `gotocommunity` | routing/unseen/perf | n/d | - | live `lot-trip-shortcut-loop` | The lot-trip shortcuts visit / visitlot / gotolot / gotocommunity (kLotTripShortcut, on since v0.1.0) had no… |
| `gotolot` | routing/unseen/perf | n/d | - | live `lot-trip-shortcut-loop` | The lot-trip shortcuts visit / visitlot / gotolot / gotocommunity (kLotTripShortcut, on since v0.1.0) had no… |
| `harvesterunripe` | routing/unseen/perf | n/d | yes | check `harvesterunripe-check` | The Harvester no longer harvests unripe or dead plants (household-visible) |
| `harvestregion` | routing/unseen/perf | n/d | yes | check `harvestregion-check` | A poorly grown plant's harvest gets no quality bonus (EA's bottom growth region) (B13 §7, docs/593 H4) |
| `lotprune` | routing/unseen/perf | n/d | yes | check `lotprune-check` | The player's lot-popularity map drops deleted, residential and no-longer-visited lots (B12 §11, docs/590 B14) |
| `namecache` | routing/unseen/perf | n/d | - | live `sim-name-cache` | SimDescriptionCore.get_FirstName/get_LastName/get_FullName run the localized-string lookup (native FNV-64, ta… |
| `naniteroutefail` | routing/unseen/perf | n/d | yes | check `naniteroutefail-check` | Nanite terrarium not destroyed when reverse engineering ends before the pickup (household-visible) |
| `overwatchchain` | routing/unseen/perf | n/d | - | - | built 2026-10-22 : 244 Overwatch alarms run as a chain (types one tick apart in its own task), 245 yields bet… |
| `overwatchspread` | routing/unseen/perf | n/d | - | check `bp-overwatch-spread-inventory` | NRaas Overwatch's nightly inventory cleanup looks up a localized catalog name for every world object in one F… |
| `overwatchspreadverify` | routing/unseen/perf | n/d | - | live `bp-nraas-upstream-verify` | NRaas Overwatch's nightly inventory cleanup looks up a localized catalog name for every world object in one F… |
| `overwatchstuck` | routing/unseen/perf | n/d | - | - | NRaas Overwatch stuck-check plans seen, yields |
| `overwatchstuckslice` | routing/unseen/perf | n/d | - | - | built 2026-10-24 : 255 ticks taken inside the Stuck Check's walk over every Sim, 256 Sims walked, 257 longest… |
| `owprobeplans` | routing/unseen/perf | n/d | - | live `owprobeplans-stuckcheck-ab` | NRaas Overwatch Stuck Check V2 (StuckCheck.OnPostPlan) counts EA's stuck-check probe plans (LotManager.RunStu… |
| `plantdormantflip` | routing/unseen/perf | n/d | yes | check `plantdormantflip-check` | A fruit tree with frost and leaf cover disagreeing stays dormant instead of flipping every evaluation (B13 §3… |
| `plantdormanttime` | routing/unseen/perf | n/d | yes | check `plantdormanttime-check` | A plant coming out of dormancy does not lose a whole dormant span of water at once (B13 §4, docs/593 G2) |
| `portaladaptorleak` | routing/unseen/perf | n/d | - | live `ip-portal-leak` | Isla Paradiso saves leak routing objects (ScriptCore.Route, PortalAnimateThroughAction, Sim script/steering/p… |
| `preplancb` | routing/unseen/perf | n/d | - | live `route-aboutplan-order` | docs/185 §2 (I-319-3/4): ts3fixes sends the async START before the original Route.Plan runs AboutToPlanCallba… |
| `rabbithole` | routing/unseen/perf | n/d | - | live `ip-school-run-safespot-loop` | docs/74 §11 (b), docs/95 §5.1: the rabbit hole shortcut (switch rabbithole, on) places an unseen school child… |
| `replandefer` | routing/unseen/perf | n/d | - | - | built 2026-10-21 : 132 door/elevator replans sent to the Sim's interrupt action, 133 eligible calls kept sync… |
| `replanspread` | routing/unseen/perf | n/d | - | - | built 2026-10-21 : 132 door/elevator replans sent to the Sim's interrupt action, 133 eligible calls kept sync… |
| `routablecache` | routing/unseen/perf | n/d | - | - | routability cache hits, plans run and recorded |
| `routefaillog` | routing/unseen/perf | n/d | - | live `showoff-badges-route` | player report : a child of the active household cannot do the scouting Show Off Badges social to any adult, i… |
| `routelen` | routing/unseen/perf | n/d | - | - | built 2026-11-15 (lane L6), diagnostic, off, NOT RUN: successful plans seen (1004) / metres (1005) / >= 40 m… |
| `routesrc` | routing/unseen/perf | n/d | - | live `bp-bouncerskip-loop` | docs/151 §6 D3 / docs/170 §5: VelvetRopes+WatchDoor.LoopDel re-routes the bouncer to RoutingSlot_2 every 5 s… |
| `safelocunhide` | routing/unseen/perf | n/d | - | check `safelocunhide-check` | A Sim placed by the reset fallback is unhidden as on EA's found-spot path |
| `scarepriority` | routing/unseen/perf | n/d | yes | check `scarepriority-check` | An autonomous Scare/StaticShock receiver still queued after the scarer routed is raised to NonCriticalNPCBeha… |
| `seasonmarkercache` | routing/unseen/perf | n/d | - | - | built 2026-10-24 /(b), offline only): 270 marker queries answered from the live list, 271 marker queries pass… |
| `slotmachinedefs` | routing/unseen/perf | n/d | yes | check `slotmachinedefs-check` | Autonomous slot play rewrites the shared stakes menu entries (household-visible) |
| `spatialcache` | routing/unseen/perf | n/d | - | - | lot object query cache hits, misses, clears on object destroy |
| `stations` | routing/unseen/perf | n/d | - | - | station detours skipped |
| `stuckcheckasync` | routing/unseen/perf | n/d | - | live `hitch-stuckcheck-check` | EA's 3 AM stuck check (LotManager.RunStuckCheckHelper): every actor that has not moved gets up to 10 route pl… |
| `stuckcheckbusy` | routing/unseen/perf | n/d | - | - | 3 AM stuck checks skipped for busy Sims |
| `stuckcheckcap` | routing/unseen/perf | n/d | - | - | built 2026-10-25 : 297 PROBE marks ts3native accepted, then one per stuck check (any switch): 298 no plan, 29… |
| `subroutefail` | routing/unseen/perf | n/d | yes | check `subroutefail-check` | SimQueue.WaitForTurn plays a blocking route-fail animation and balloon after TryReplacementInteraction pushed… |
| `unseencars` | routing/unseen/perf | n/d | - | - | built 2026-10-21 : 197 `LiteDriver` parking-spot asks, 198 spots granted for an unseen car to an unseen spot,… |
| `unseencost` | routing/unseen/perf | n/d | - | - | built 2026-11-02, not run: per unseen-check site i 0..10 (far, refresh, simunseen, townie, gohome, pos, lot,… |
| `unseenfbfinish` | routing/unseen/perf | n/d | - | live `unseenfbfinish-far-walkers-loop` | docs/202 §3.4.2: C1 / C2's fake answers ("no obstacles" / "nothing to avoid") for unseen walkers waiting on a… |
| `unseenfbloopbreak` | routing/unseen/perf | n/d | - | - | docs/202 §5.7 E: loops flagged, Sim fixes, lot fixes finished, lot fixes stopped (scope), lot steps, breaks r… |
| `unseenfbpre` | routing/unseen/perf | n/d | - | live `unseenfb-hold-overwatch` | docs/202 §3.7: a D walk-time hold keeps an unseen Sim standing in the same interaction for up to 2 sim-hours;… |
| `unseenfbsites` | routing/unseen/perf | n/d | - | live `unseenfbc-far-walkers-loop` | docs/202 §3.4: after phase A caps the waits behind unseen Sims' route jobs, an unseen Sim can still wait for… |
| `unseenfbteleport` | routing/unseen/perf | n/d | - | check `unseen-tp-goal-closed` | docs/267 F1: the unseen teleport (unseenfbteleport, on by default) placed a cut own Route.Plan's unseen NPC a… |
| `unseenmotives` | routing/unseen/perf | n/d | - | - | built 2026-10-21 : 146-148 environment calls classified / unseen / skipped, 149-151 motive updates classified… |
| `unseenoutfitdefer` | routing/unseen/perf | n/d | - | - | built 2026-11-16, default off, natural-play runs on 4 worlds: instant sets deferred (1981) / of those replaci… |
| `unseenpark` | routing/unseen/perf | n/d | - | live `unseen-park-loop` | An idle unseen low-LOD Sim re-enters AutonomyManager's queue as soon as EA's 5-minute idle delay has passed,… |
| `unseenroute` | routing/unseen/perf | n/d | - | live `unseenroute-fenced-repair` | docs/202 §3.9.7: a relaxed placement without a verdict (B pass on a public lot with a fenced corner) could le… |
| `venuememo` | routing/unseen/perf | n/d | - | live `venue-memo` | Meta venue score (Lot.HowMuchItWantsSimToCome per venue type, every passing meta pair) re-scans MetaAutonomyM… |
| `virtualhome` | routing/unseen/perf | n/d | - | check `bp-virtualhome-fade` | Homeless townies walk across Bridgeport to a random lot before leaving the world (4.4% of script time) |
| `visit` | routing/unseen/perf | n/d | - | live `lot-trip-shortcut-loop` | The lot-trip shortcuts visit / visitlot / gotolot / gotocommunity (kLotTripShortcut, on since v0.1.0) had no… |
| `visitlot` | routing/unseen/perf | n/d | - | live `lot-trip-shortcut-loop` | The lot-trip shortcuts visit / visitlot / gotolot / gotocommunity (kLotTripShortcut, on since v0.1.0) had no… |
| `watchdogidle` | routing/unseen/perf | n/d | - | live `bp-idle-unroutable` | An idle townie standing on an unroutable spot (enclosed room, fenced corner, pool tile) stays there for good;… |
| `bowlinglane` | routing/unseen/perf | no | - | check `bowling-lane-wait` | BowlingLane+BowlInBowlingLane: Run's start wait (while !ReadyToStart) ignores cancel and waits for listed bow… |
| `carnoroute` | routing/unseen/perf | no | - | check `car-no-route-check` | issue #166 build 33: CarNpcManager.DriveFromPointToMatrix creates a route on car.CarRoutingComponent with no… |
| `convmerge` | routing/unseen/perf | no | - | check `conv-merge-region` | Conversation.Merge drops the two old conversations without cleaning their discourage regions: hidden DynamicS… |
| `datingprofile` | routing/unseen/perf | no | - | check `dating-profile-check` | OnlineDatingManager.GetRecordForSim adds an empty record for any Sim it is asked about, and DidSimsMeetOnline… |
| `deadsimcount` | routing/unseen/perf | no | - | - | built 2026-11-09, read-only: destroyed Sim objects still in memory (weak references to every Sim seen in LotM… |
| `eggspot` | routing/unseen/perf | no | - | check `egg-spot-check` | docs/508: EggHuntController.IsValidEggPosition accepts any outdoor routable point, so eggs land off the hunt… |
| `eventrouteretry` | routing/unseen/perf | no | - | check `event-ceremony-block` | City Hall award ceremony: the NPC crowd gathers on and around the award jig (RouteToGatheringArea centres on… |
| `gohomewalk` | routing/unseen/perf | no | - | check `gohome-walkstyle-left` + battery `gohome` | GoHome.Run requests its WalkStyle on the Sim (Sim.RequestWalkStyle, a per-Sim count) and never unrequests it:… |
| `gradvisitors` | routing/unseen/perf | no | yes | check `gradvisitors-check` | The graduation visitor list offers dead Sims, toddlers and pets |
| `npcshowstuck` | routing/unseen/perf | no | yes | check `npcshowstuck` | NPC shows stop for the rest of the save once an NPC gig is removed |
| `routableasync` | routing/unseen/perf | no | - | - | built 2026-11-15, default off, NOT RUN: PlanFromPoint STARTs let through for unseen NPCs' IsPointInLotSafelyR… |
| `safespotpool` | routing/unseen/perf | no | - | - | built 2026-11-16, default off: placement answers in a pool or under the ground skipped / cached start spots s… |
| `sculptpose` | routing/unseen/perf | no | - | check `sculpt-pose` | SculptingStation.SculptSim failure paths (route fail, CanAfford false after Committed, reset) leave the subje… |
| `socialreplanlog` | routing/unseen/perf | no | - | - | (no repro text; see SocialReplanLog.cs) |
| `spotcheck` | routing/unseen/perf | no | - | - | built 2026-11-16, default off: GoHome spots in view, walked / lot-trip spots in view, the game's own trip |
| `spotwaitattr` | routing/unseen/perf | no | - | - | built 2026-11-18, default off, log only: kind-2 lot-edge check waits >= 250 ms, frame-bound, job-bound, frame… |
| `tagunseen` | routing/unseen/perf | no | - | - | built 2026-11-16, default off: Tag laps checked / laps run without a route / errors or members not found / a… |
| `visitwalkfinish` | routing/unseen/perf | no | - | - | built 2026-11-15, default off, NOT RUN: long autonomous NPC visits judged (2722) / coin flips that kept drivi… |
| `compactionfail` | save-time-only | n/d | - | live `compaction-abort-bp` | docs/204 §6 Compaction failure path on Bridgeport: the reload throws after the loading screen is up; the scre… |
| `freshclothingresume` | save-time-only | n/d | yes | check `freshclothingresume-check` | Fresh Clothing resumes its saved time after a swim or shower, not restarted |
| `savecleanupaggressive` | save-time-only | n/d | - | live `bp-multigen-aggr` | Long multi-generation saves keep memory pictures of former household members, per-Sim histories, outfits of t… |
| `simdisposelink` | stairs | no | yes | check `simdisposelink-check` | Sim.Dispose sets its SimDescription.CreatedSim to null even when the description is bound to another live Sim… |
| `stairwaitend` | stairs | no | yes | check `stairwaitend-check` | Stairs+StairsPortalComponent.OnPortalStart sleeps in a Sleep(10) loop until the native stair callback (Handle… |
| `badsecretkey` | story progression/households | n/d | - | check `badsecretkey-check` | Bad relationship secret uses the right string key |
| `boatgetoutguard` | story progression/households | n/d | - | - | built 2026-11-13, default off, NOT RUN: stale-posture NREs out of Boat.GetOut swallowed for NPCs, Sim reset (… |
| `bodysculptorweight` | story progression/households | n/d | yes | check `bodysculptorweight-check` | The body sculptor no longer mirrors thin weights (Thin makes thin Sims thinner) (household-visible) |
| `boxofmysterywoohoo` | story progression/households | n/d | yes | check `boxofmysterywoohoo-check` | Try for Baby in the Box of Mystery rolls pregnancy only after a woohoo that happened (household-visible) |
| `bullridercleanup` | story progression/households | n/d | yes | check `bullridercleanup-check` | A second rider's cleanup no longer clears the Sim still riding the mechanical bull (household-visible) |
| `chipanycount` | story progression/households | n/d | yes | check `chipanycount-check` | "Any" chip component takes only the missing count from medium/large blanks (household-visible) (found by the… |
| `chipfamilycrystal` | story progression/households | n/d | yes | check `chipfamilycrystal-check` | Trait chip crafting consumes family-inventory crystals from the family inventory (household-visible) |
| `chocfountaingeyser` | story progression/households | n/d | yes | check `chocfountaingeyser-check` | Each refill of the chocolate fountain adds another repeating geyser alarm (household-visible) |
| `coopeggquality` | story progression/households | n/d | yes | check `coopeggquality-check` | Chicken coop eggs come out one quality tier low and never Perfect (household-visible) |
| `digsiteemptypick` | story progression/households | n/d | yes | check `digsiteemptypick-check` | An empty dig keeps the dig site treasure (household-visible) |
| `donationbuffchance` | story progression/households | n/d | yes | check `donationbuffchance-check` | The giggle-fit and plasma-faint donation buffs rolled the percent chance inverted (85% instead of the shipped… |
| `fallenrockcollected` | story progression/households | n/d | yes | check `fallenrockcollected-check` | An imported meteorite's untracked timeout alarm destroyed it after the household collected it; now a collecte… |
| `fortunetellerscamodds` | story progression/households | n/d | yes | check `fortunetellerscamodds-check` | Scam-artist fortune tellers at levels 1-9 use the scam odds, not the mystic odds (shared array) (household-vi… |
| `fragmentreconstruct` | story progression/households | n/d | yes | check `fragmentreconstruct-check` | Reassembling fragments uses the clicked fragment and no extra one (and no NRE when too few are free) (househo… |
| `fvstandskillcurve` | story progression/households | n/d | yes | check `fvstandskillcurve-check` | The fruit and veggie stand builds its skill curve from the frequency arrays (household-visible) |
| `hayscattertime` | story progression/households | n/d | yes | check `hayscattertime-check` | Hay bale scatter time was never reset, so later scatters on a haystack or stacked bale finished at once; now… |
| `herbqualityfloor` | story progression/households | n/d | yes | check `herbqualityfloor-check` | Herbal medicine experiment no longer lowers a Horrifying herb out of range (household-visible) |
| `hhcleanupdup` | story progression/households | n/d | yes | check `hhcleanupdup-check` | Household.Cleanup folds a duplicate household only into a valid one and re-homes its unshared members (househ… |
| `hottubseatposture` | story progression/households | n/d | yes | check `hottubseatposture-check` | Hot tub seated socials no longer stack sore-buff alarms (household-visible) |
| `influencefollowerbonus` | story progression/households | n/d | yes | check `influencefollowerbonus-check` | Influence skill-gain bonus scales with blog followers (float division; intent inferred) (household-visible) |
| `jetpackflyherecleanup` | story progression/households | n/d | yes | check `jetpackflyherecleanup-check` | Fly Here cleanup without an active jetpack skips StopFX (no NRE, jigs destroyed) (household-visible) |
| `junglegymexitqueue` | story progression/households | n/d | yes | check `junglegymexitqueue-check` | Look-out tower exit queue drops Sims who left (no permanent 45-minute wait) (household-visible) |
| `ltrhistorybits` | story progression/households | n/d | yes | check `ltrhistorybits-check` | A forced relationship change keeps the friendship history bits |
| `minihomelot` | story progression/households | n/d | - | check `minihomelot-movein-check` | docs/266: a Sim keeps or gets the wish Move In With <Sim> for a Sim of its own household (player report twopo… |
| `nectarcopylevel10` | story progression/households | n/d | yes | check `nectarcopylevel10-check` | Bought/tasted nectar copies keep the level-10 maker flag and its effects (household-visible) |
| `oppchainworld` | story progression/households | n/d | yes | check `oppchainworld-check` | Opportunity chain history cleared from the world change when only the source needs another world (household-v… |
| `ovenmenunull` | story progression/households | n/d | yes | check `ovenmenunull-check` | Picking food from an empty or unaffordable restaurant menu throws (household-visible) |
| `pokerdeadplayer` | story progression/households | n/d | yes | check `pokerdeadplayer-check` | A seated Sim who no longer exists throws in the poker dealer and kills the table (household-visible) |
| `roleunprotect` | story progression/households | n/d | yes | check `roleunprotect-check` | A townie removed from an inactive role loses the role's story-progression protection (B12 §43, docs/592 S11) |
| `roulettehackreset` | story progression/households | n/d | yes | check `roulettehackreset-check` | A reset leaves the roulette table hacked forever (household-visible) |
| `showstagefxend` | story progression/households | n/d | yes | check `showstagefxend-check` | A show end during a stage one-shot effect was dropped, so stage lights and EP6 effects machines stayed on aft… |
| `snackbowlslobvfx` | story progression/households | n/d | yes | check `snackbowlslobvfx-check` | Slob snack-bowl food effect is a one-shot (no leaked effect per snack) (household-visible) |
| `spawncapprobe` | story progression/households | n/d | - | check `spawncap-check` | docs/147 §5: every spawner try (insects, rocks/gems/metals, seeds) calls MinorPet.CanSpawnMoreMinorPetsInWorl… |
| `spbucketindex` | story progression/households | n/d | yes | check `spbucketindex-check` | Histogram-targeted story actions pick from the bucket that was chosen |
| `spclonedict` | story progression/households | n/d | yes | check `spclonedict-check` | Scoring clones of the too-big / too-small demographics get their own lot dictionary |
| `spcollectnull` | story progression/households | n/d | yes | check `spcollectnull-check` | Demographics.Collect skips a null household entry instead of throwing |
| `spcreatemovein` | story progression/households | n/d | yes | check `spcreatemovein-check` | Create and Move In predicts the new family once, on its lot |
| `splotdensity` | story progression/households | n/d | yes | check `splotdensity-check` | Lot density counts households only on the lots it counts |
| `splotlookup` | story progression/households | n/d | yes | check `splotlookup-check` | Predicted moves count toward too-big, too-small and box stalls (lot looked up by lot id) |
| `spoonslavechecks` | story progression/households | n/d | yes | check `spoonslavechecks-check` | BedSleep.LoopHandler returns for the spooning slave partner before EA's per-sleeper checks, so the slave of a… |
| `sppensionprompt` | story progression/households | n/d | yes | check `sppensionprompt-check` | Rejoining the retired career asks the pension prompt before the old job is left (household-visible) |
| `sprecheck` | story progression/households | n/d | yes | check `sprecheck-check` | Story progression rechecks right before Execute that the action does not reach the active, travel or selected… |
| `sprelcount` | story progression/households | n/d | yes | check `sprelcount-check` | Story progression's friend, best-friend, enemy and romance histograms count each Sim once (no incremental ske… |
| `springdanceclear` | story progression/households | n/d | yes | check `springdanceclear-check` | Spring dance floor tally cleared after the king/queen judging (household-visible) |
| `sproommatejob` | story progression/households | n/d | yes | check `sproommatejob-check` | Story roommates outside University get the job EA picked for them |
| `starlightreset` | story progression/households | n/d | yes | check `starlightreset-check` | A lit StarLight reset no longer resets the selected Sim (household-visible) |
| `streetartmasterboost` | story progression/households | n/d | yes | check `streetartmasterboost-check` | The Avant-Garde masterpiece boost went to row 3, not the Master row, so large murals never got it; now the Ma… |
| `takeariskguard` | story progression/households | n/d | - | check `takeariskguard-check` | Political.PoliticalTakeARisk updates the boss's and co-workers' liking through GetRelationship(x, true).LTR w… |
| `teleportcommunitylot` | story progression/households | n/d | yes | check `teleportcommunitylot-check` | Teleport to community lot not offered when no valid lot exists (household-visible, menu) |
| `trashpileimport` | story progression/households | n/d | - | check `trashpileimport-check` | Imported trash pile releases its first age setup and an Ancient pile is built through Old (no second alarm, n… |
| `treewishingwell` | story progression/households | n/d | yes | check `treewishingwell-check` | The Tree of Prosperity never sees a wishing well, so its night shows never spawn (household-visible) |
| `tutorballoonalarm` | story progression/households | n/d | yes | check `tutorballoonalarm-check` | Tutoring thought balloons start with tutoring, not when queued (no leaked alarm) (household-visible) |
| `virtualmoveindup` | story progression/households | n/d | yes | check `virtualmoveindup-check` | A repeat virtual move-in does not list the service Sim twice |
| `worldpondskatetest` | story progression/households | n/d | yes | check `worldpondskatetest-check` | Skate on a frozen world pond honours the shared skate test (robot form, already skating, full area) (househol… |
| `grouptalkstale` | story progression/households | no | - | check `group-talk-stale` | Sims keep departed Sims (other lot, destroyed) in their group talk: relationship gains, social bumps and Chat… |
| `moveinpartner` | story progression/households | no | - | check `movein-partner-cap` | Story progression MoveInWithPartner picks the mover without the room test and moves an NPC Sim into a partner… |
| `npcstallend` | story progression/households | no | - | - | built 2026-11-19, default off, NOT RUN: NPC stalls ended with EA's cancel (4100) / exempt: household, selecte… |
| `reltablesapart` | story progression/households | no | - | check `rel-tables-apart-check` | When x's relationship table has no entry for y but y's table holds one for x, EA's Relationship.GetInternal(c… |
| `spacesimpartner` | story progression/households | no | - | check `spacesim-partner-wait` | SpaceSimulator+RideWith.Run pushes DoRideB on the partner and can return before the ride starts (rider cancel… |
| `splashroof` | story progression/households | no | - | check `splash-roof-puddle` | Sim+AutonomousSplashInPuddle.Run plans a route to the nearest three rain puddles that Terrain.SplashInPuddle.… |
| `splitpartner` | story progression/households | no | yes | check `splitpartner-check` | A story-progression split leaves the stayer with a one-sided partner and can move a teen out alone |
| `uncleguard` | story progression/households | no | - | - | built 2026-11-11: attraction handlers seen (2245) / controllers stopped for a removed Sim (2246) / errors (22… |
| `frolicexit` | weather/seasons | n/d | yes | check `frolicexit-check` | Any critical motive ends a fairy frolic, not only the last one in the list (B12 §4, docs/590 B5) |
| `gohereboatlist` | weather/seasons | n/d | yes | check `gohereboatlist-check` | A deleted Sim leaves the go-here boat and hover-train lists (B12 §7, docs/590 B10) |
| `holidaylights` | weather/seasons | n/d | yes | check `holidaylights-check` | Holiday house lights: pulse of at least 1, held trickle alarm removed before re-add and unsaved; load removes… |
| `hovertraintest` | weather/seasons | n/d | yes | live `hovertraintest-check` | Sim.NPCUseHoverTrain.Definition.Test runs a world Queries.GetObjects<IHoverTrainStation>() for every autonomo… |
| `igloolookats` | weather/seasons | n/d | yes | check `igloolookats-check` | An igloo builder's look-ats are turned back on after the build (B12 §6, docs/590 B8) |
| `jogterrain` | weather/seasons | n/d | yes | check `jogterrain-check` | A fatigued jogger's walk back is created on the terrain instead of throwing every minute (B12 §2, docs/590 B3) |
| `leafburnfail` | weather/seasons | n/d | yes | check `leafburnfail-check` | A leaf pile whose fire never started is usable again (B12 §24, docs/591 R1) |
| `lightningweights` | weather/seasons | n/d | yes | check `lightningweights-check` | Lightning can strike Sims and objects again after a strike that found none (B13 §2, docs/593 L1) |
| `mopunreachable` | weather/seasons | n/d | yes | check `mopunreachable-check` | One unreachable or blocked puddle ends a mop run while reachable puddles stay wet, and autonomy re-picks the… |
| `outerwearautoloop` | weather/seasons | n/d | yes | check `outerwearautoloop-check` | Autonomous ChangeIntoOuterwear / ChangeOutOfOuterwear / ChangeToddlerClothes tests pass while Run cannot chan… |
| `outerwearrestore` | weather/seasons | n/d | yes | check `outerwearrestore-check` | ChangeIntoOuterwear (TemperatureTooCold) does not store the outfit to return to, so removing outerwear gives… |
| `petpuddletile` | weather/seasons | n/d | yes | check `petpuddletile-check` | A pet drinking from or playing in the closest puddle removes the puddle under the click point instead of the… |
| `pondclearscan` | weather/seasons | n/d | yes | check `pondclearscan-check` | PondManager.AllPondsClear runs a world GetObjects<LotPond> on every Seasons tick while ponds try to melt with… |
| `rainsheltercheap` | weather/seasons | n/d | yes | check `rainsheltercheap-check` | The 10-sim-minute rain pass runs two native shelter tests on every Sim and Soil before the cheap on-fire / wa… |
| `seasonimpostercold` | weather/seasons | n/d | - | live `bp-seasonimposter-season` | issue #82 on the game's own path: Lot.MagicallyChangeSeason -> 1-sim-minute yielding alarm -> Lot.ChangeSeaso… |
| `soakedpetselfreact` | weather/seasons | n/d | yes | check `soakedpetselfreact-check` | A soaked pet shaking off water queued the shake reaction on itself; now only other Sims react (household-visi… |
| `soakedstat` | weather/seasons | n/d | yes | check `soakedstat-check` | BuffSoaked.OnReAddition increments TombStats 'times soaked' on every re-add, and rain re-adds Soaked every 2… |
| `tempforecast` | weather/seasons | n/d | yes | check `tempforecast-check` | The temperature forecast range follows the real day curve instead of its mirror (B13 §10, docs/593 T1) |
| `trainsimgone` | weather/seasons | n/d | yes | check `trainsimgone-check` | Training with the trainee gone ends instead of throwing |
| `weathermanagerthrow` | weather/seasons | n/d | yes | check `weathermanagerthrow-check` | RainEvent.PutOutFires reads Firefighter.sFirefighterDictionary without a null test; it is null in worlds with… |
| `weatherstonelistener` | weather/seasons | n/d | yes | check `weatherstonelistener-check` | WeatherStone.Dispose leaves the summon's kWeatherStarted/kWeatherStopped listener on the tracker, holding the… |
| `hauntedumbrella` | weather/seasons | no | yes | check `haunted-umbrella` | Haunted house umbrella check is inverted |
| `seasondup` | weather/seasons | no | - | - | built 2026-11-11: wish managers created (2254, also off) / object listeners moved (2255) / errors (2256) / du… |
| `seasonsdeadsim` | weather/seasons | no | - | - | built 2026-11-08: EA Motives kSeasonTransition listeners removed whose Sim was gone (2073) / whose Motives wa… |
| `snowfightjoinwait` | weather/seasons | no | yes | check `snowfightjoin-wait` | The snowball arena join-wait counter never resets, later games end at once |
| `trainboost` | weather/seasons | no | - | check `train-boost` | AthleticGameObject.TrainSim.Run raises the trainee's Athletic skill-gain and shape-delta multipliers and lowe… |
| `weatherstonerelisten` | weather/seasons | no | yes | check `weatherstonerelisten-check` | WeatherStone.AnimateChangeWeatherStoneState(AfterSuccessfulSummon) overwrites mWeatherChangedEventListener wi… |

### 5.2 Fired (122) and no counter (117)

| switch | area | fired? | proven? | repro? | runs |
|---|---|---|---|---|---|
| `alarmtime` | CAS/UI | log | - | - |  |
| `bookhand` | put-down/surfaces | log | - | live `book-hand-check` |  |
| `callcensus` | routing/unseen/perf | log | - | - |  |
| `ufplansplit` | routing/unseen/perf | log | - | live `spotasync-trip-loop` |  |
| `unseenboatland` | routing/unseen/perf | log | - | - |  |
| `watchdog` | routing/unseen/perf | log | - | live `bp-apartment-gohome` |  |
| `savecleanup` | save-time-only | log | - | live `bp-save-cleanup` |  |
| `autoffbcast` | CAS/UI | n/c | - | - |  |
| `autoffbcastapply` | CAS/UI | n/c | - | - |  |
| `buffremovenoop` | CAS/UI | n/c | - | live `lane-l4-memos` |  |
| `buyclearrecycle` | CAS/UI | n/c | - | - |  |
| `buygridsweep` | CAS/UI | n/c | - | - |  |
| `casgrid` | CAS/UI | n/c | - | live `cas-grid` |  |
| `unseenbroadcast` | CAS/UI | n/c | - | - |  |
| `comboprotest` | EP events | n/c | - | check `combo-rabbithole-check` |  |
| `combovenue` | EP events | n/c | - | check `combo-rabbithole-check` |  |
| `hsgradkeep` | EP events | n/c | - | check `hs-grad-keep-check` |  |
| `timeportalvfx` | EP events | n/c | yes | check `timeportalvfx-check` |  |
| `autt2frontdoor` | doors/locks | n/c | - | live `tier2-p2-dry` |  |
| `bouncerskip` | doors/locks | n/c | yes | check `bouncerskip-check` |  |
| `doorgen` | doors/locks | n/c | - | - |  |
| `failrepeat` | doors/locks | n/c | - | live `route-sv-seacheck-freeze` |  |
| `farthestsorted` | doors/locks | n/c | - | - |  |
| `indoorcap` | doors/locks | n/c | - | - |  |
| `tpaptgoal` | doors/locks | n/c | yes | check `unseen-tp-apt-goal` |  |
| `autt2inuse` | in-use/reservations | n/c | - | live `tier2-p1-dry` |  |
| `vehparkreserve` | in-use/reservations | n/c | - | live `veh-park-reserve-dead` |  |
| `safespotyield` | load-time-only | n/c | - | - |  |
| `deadrefcooking` | objects/misc | n/c | - | check `cooking-handler-gone-check` |  |
| `dnpobjkeys` | objects/misc | n/c | - | - |  |
| `dnpupdateguard` | objects/misc | n/c | - | - |  |
| `exitcbsweep` | objects/misc | n/c | - | - |  |
| `motivebuf` | objects/misc | n/c | - | - |  |
| `motivebufcheck` | objects/misc | n/c | - | - |  |
| `picnicexit` | objects/misc | n/c | - | - |  |
| `wardrobewandvfx` | occults | n/c | yes | check `wardrobewandvfx-check` |  |
| `dnppetkeys` | pets/horses | n/c | - | - |  |
| `petdroolvfx` | pets/horses | n/c | yes | check `petdroolvfx-check` |  |
| `petlonghome` | pets/horses | n/c | - | - |  |
| `petstartleskip` | pets/horses | n/c | - | - |  |
| `stcsocials` | phone | n/c | - | - |  |
| `stcsocialscheck` | phone | n/c | - | - |  |
| `bookhanddiag` | put-down/surfaces | n/c | - | live `book-hand-check` |  |
| `asyncjigsync` | routing/unseen/perf | n/c | - | live `showoff-jig-race` |  |
| `asyncroute` | routing/unseen/perf | n/c | - | - |  |
| `autledger` | routing/unseen/perf | n/c | - | - |  |
| `autlive` | routing/unseen/perf | n/c | - | live `aut-live-save` |  |
| `autoffdry` | routing/unseen/perf | n/c | - | live `offload-ranks-camera` |  |
| `autoffenvapply` | routing/unseen/perf | n/c | - | - |  |
| `autofflot` | routing/unseen/perf | n/c | - | live `offload-ranks-camera` |  |
| `autoffnoref` | routing/unseen/perf | n/c | - | - |  |
| `autofft2` | routing/unseen/perf | n/c | - | live `offload-ranks-camera` |  |
| `autroommemo` | routing/unseen/perf | n/c | - | live `autonomy-memo` |  |
| `autroommemoverify` | routing/unseen/perf | n/c | - | live `autonomy-memo` |  |
| `autseengame` | routing/unseen/perf | n/c | - | - |  |
| `autt2cache` | routing/unseen/perf | n/c | - | live `tier2-h7-dry` |  |
| `autt2cewarm` | routing/unseen/perf | n/c | - | live `tier2-p2-dry` |  |
| `autt2drag` | routing/unseen/perf | n/c | - | live `tier2-h7-dry` |  |
| `autt2fire` | routing/unseen/perf | n/c | - | live `tier2-p1-dry` |  |
| `autt2tuning` | routing/unseen/perf | n/c | - | live `tier2-h7-dry` |  |
| `autunseen` | routing/unseen/perf | n/c | - | - |  |
| `autunseenpace` | routing/unseen/perf | n/c | - | - |  |
| `autunseenpacecredit` | routing/unseen/perf | n/c | - | - |  |
| `catnamecacheverify` | routing/unseen/perf | n/c | - | live `lane-l4-memos` |  |
| `dnpsubjectcache` | routing/unseen/perf | n/c | - | - |  |
| `drivertaskasync` | routing/unseen/perf | n/c | - | - |  |
| `easpreaddecayverify` | routing/unseen/perf | n/c | - | check `bp-easpread-decay` |  |
| `easpreadlotsverify` | routing/unseen/perf | n/c | - | check `bp-easpread-lots` |  |
| `errortrapspreadfakeupstream` | routing/unseen/perf | n/c | - | check `bp-errortrap-spread-upstream-yield` |  |
| `goalhhexempt` | routing/unseen/perf | n/c | - | - |  |
| `hoursmemo` | routing/unseen/perf | n/c | - | live `hours-memo` |  |
| `hoursmemoverify` | routing/unseen/perf | n/c | - | live `hours-memo` |  |
| `idlememo` | routing/unseen/perf | n/c | - | - |  |
| `invcache` | routing/unseen/perf | n/c | - | live `event59-invcache` |  |
| `invcacheverify` | routing/unseen/perf | n/c | - | live `event59-invcache` |  |
| `legreplanskip` | routing/unseen/perf | n/c | - | - |  |
| `overwatchspreadfakeupstream` | routing/unseen/perf | n/c | - | check `bp-overwatch-spread-upstream-yield` |  |
| `permamemo` | routing/unseen/perf | n/c | - | live `lane-l4-memos` |  |
| `permamemoverify` | routing/unseen/perf | n/c | - | live `lane-l4-memos` |  |
| `poolfailmemo` | routing/unseen/perf | n/c | - | - |  |
| `safespotcap` | routing/unseen/perf | n/c | - | - |  |
| `safespotreuse` | routing/unseen/perf | n/c | - | - |  |
| `safespotreuseverify` | routing/unseen/perf | n/c | - | check `safespotreuse-verify` |  |
| `spotspread` | routing/unseen/perf | n/c | - | - |  |
| `stuckchecknear` | routing/unseen/perf | n/c | - | live `hitch-stuckcheck-check` |  |
| `stuckcheckown` | routing/unseen/perf | n/c | - | live `route-stuckcheck-cap-loop` |  |
| `unseenboat` | routing/unseen/perf | n/c | - | live `ip-unseen-boat-loop` |  |
| `unseenboatstart` | routing/unseen/perf | n/c | - | live `ip-houseboat-leave-loop` |  |
| `unseenenv` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbarrive` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbleave` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbloopdet` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbplan` | routing/unseen/perf | n/c | - | live `ip-gvunseen-defer-loop` |  |
| `unseenfbstuck` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbtrip` | routing/unseen/perf | n/c | - | - |  |
| `unseenfbtriphold` | routing/unseen/perf | n/c | - | - |  |
| `unseenjam` | routing/unseen/perf | n/c | yes | check `unseenjam-check` |  |
| `unseenloopskip_offscreen_dup_removed` | routing/unseen/perf | n/c | - | - |  |
| `unseenmusicwatch` | routing/unseen/perf | n/c | yes | check `unseenmusicwatch-check` |  |
| `unseenpianist` | routing/unseen/perf | n/c | yes | check `unseenpianist-check` |  |
| `unseenposcache` | routing/unseen/perf | n/c | - | - |  |
| `unseenprobe` | routing/unseen/perf | n/c | - | live `unseen-park-loop` |  |
| `unseenshow` | routing/unseen/perf | n/c | yes | check `unseenshow-check` |  |
| `unseentablecopy` | routing/unseen/perf | n/c | - | - |  |
| `venuememoverify` | routing/unseen/perf | n/c | - | live `venue-memo` |  |
| `wakepark` | routing/unseen/perf | n/c | - | live `wake-park-loop` |  |
| `wakepoll` | routing/unseen/perf | n/c | - | live `wake-park-loop` |  |
| `wakeprobe` | routing/unseen/perf | n/c | - | live `wake-park-loop` |  |
| `watchdogasync` | routing/unseen/perf | n/c | - | live `ip-watchdog-lockwait-loop` |  |
| `watchdogasyncshadow` | routing/unseen/perf | n/c | - | live `spotasync-trip-loop` |  |
| `watchdoglockskip` | routing/unseen/perf | n/c | - | live `ip-watchdog-lockwait-loop` |  |
| `wildflowertask` | routing/unseen/perf | n/c | - | live `wildflower-save-abort` |  |
| `attractionalarm` | story progression/households | n/c | - | - |  |
| `chemistryvfx` | story progression/households | n/c | yes | check `chemistryvfx-check` |  |
| `dartboardvfx` | story progression/households | n/c | yes | check `dartboardvfx-check` |  |
| `detailprio` | story progression/households | n/c | - | live `detail-priority-cache` |  |
| `detailprioverify` | story progression/households | n/c | - | live `detail-priority-cache` |  |
| `haystackvfx` | story progression/households | n/c | yes | check `haystackvfx-check` |  |
| `spawncapcount` | story progression/households | n/c | - | check `spawncap-check` |  |
| `spouseunlink` | story progression/households | n/c | - | - |  |
| `vehseatdestroy` | story progression/households | n/c | - | live `veh-leak-soak` |  |
| `dnpdisposelistener` | weather/seasons | n/c | - | - |  |
| `seasonimposter` | weather/seasons | n/c | - | live `bp-seasonimposter-season` |  |
| `wildflowerprobe` | weather/seasons | n/c | - | live `wildflower-drain` |  |
| `alarmzeroperiod` | CAS/UI | yes | yes | check `alarmzeroperiod-check` | 68 |
| `maptagtaskalive` | CAS/UI | yes | yes | check `maptagtaskalive-check` | 67 |
| `mapviewkeep` | CAS/UI | yes | - | - | 2 |
| `rhentryrelax` | CAS/UI | yes | yes | check `rhentryrelax-check` | 17 |
| `shuffleboardbcast` | CAS/UI | yes | - | check `shuffleboard-broadcast-leak` | 39 |
| `spoilbroadcast` | CAS/UI | yes | yes | check `spoilbroadcast-check` | 60 |
| `synctime` | CAS/UI | yes | - | - | 55 |
| `wishsubjectgone` | CAS/UI | yes | yes | check `wishsubjectgone-check` | 1 |
| `combojob` | EP events | yes | - | check `combo-rabbithole-check` | 17 |
| `comboprotestlot` | EP events | yes | - | check `combo-rabbithole-check` | 6 |
| `courselistener` | EP events | yes | - | check `course-listener-leak` | 1 |
| `efficientchip` | EP events | yes | yes | check `efficientchip-check` | 1 |
| `plumbotfacelistener` | EP events | yes | yes | check `plumbotfacelistener-check` | 15 |
| `resortweddingalarm` | EP events | yes | yes | check `resortweddingalarm-check` | 8 |
| `vehprotestcar` | EP events | yes | - | live `veh-leak-soak` | 26 |
| `vhomewalk` | EP events | yes | yes | check `vhomewalk-check` | 1 |
| `weddingarchkeep` | EP events | yes | - | check `wedding-arch-keep-check` | 13 |
| `weddingcancel` | EP events | yes | - | check `wedding-cancel` | 2 |
| `gotoworkstale` | careers | yes | yes | check `gotoworkstale-check` | 67 |
| `playerfirst` | careers | yes | yes | check `playerfirst-check` | 48 |
| `promcarpool` | careers | yes | yes | check `prom-carpool` | 1 |
| `promoteshift` | careers | yes | yes | check `promoteshift-check` | 3 |
| `unseenservice` | careers | yes | yes | check `unseenservice-check` | 15 |
| `unseenservice_offscreen` | careers | yes | yes | check `unseenservice-check` | 5 |
| `unseenservicewait` | careers | yes | yes | check `unseenservicewait-check` | 9 |
| `autonomyfailblock` | doors/locks | yes | - | check `autonomy-fail-block` | 71 |
| `autonomyinuseblock` | doors/locks | yes | yes | check `autonomyinuseblock-check` | 61 |
| `bouncerplace` | doors/locks | yes | yes | check `bouncerplace-check` | 13 |
| `tpdoorrights` | doors/locks | yes | - | - | 48 |
| `hiddenroomnpc` | elevators | yes | - | check `hidden-room-trap` | 2 |
| `dormantprobe` | in-use/reservations | yes | - | - | 2 |
| `photoboothfunds` | in-use/reservations | yes | - | check `photobooth-funds-stuck` | 1 |
| `vehgetinleak` | in-use/reservations | yes | - | live `veh-getin-reset` | 20 |
| `lunacysocials` | load-time-only | yes | yes | check `lunacysocials-check` | 44 |
| `phonefixup` | load-time-only | yes | yes | check `phonefixup-check` | 71 |
| `steadygigmiss` | load-time-only | yes | yes | check `steadygigmiss` | 2 |
| `uniformbackup` | load-time-only | yes | - | check `career-uniform-backup-check` | 11 |
| `ageupabort` | objects/misc | yes | - | check `age-up-abort-check` | 2 |
| `alarmcmpnobox` | objects/misc | yes | yes | check `alarmcmpnobox-check` | 43 |
| `alarmkilllog` | objects/misc | yes | - | - | 2 |
| `bookcarry` | objects/misc | yes | - | check `book-carry-check` | 70 |
| `buffremovemerge` | objects/misc | yes | yes | check `buffremovemerge-check` | 44 |
| `buffsrcempty` | objects/misc | yes | yes | check `buffsrcempty-check` | 45 |
| `celebritydeadsim` | objects/misc | yes | - | - | 71 |
| `customctlsweep` | objects/misc | yes | - | - | 71 |
| `enumcmpnobox` | objects/misc | yes | yes | check `enumcmpnobox-check` | 43 |
| `groupchatpriority` | objects/misc | yes | yes | check `groupchatpriority-check` | 6 |
| `menumaven` | objects/misc | yes | - | check `menumaven-all-recipes-check` | 70 |
| `mooddrift` | objects/misc | yes | - | - | 32 |
| `mooddriftnoalloc` | objects/misc | yes | yes | check `mooddriftnoalloc-check` | 44 |
| `moodpending` | objects/misc | yes | - | - | 68 |
| `newcartest` | objects/misc | yes | yes | live `newcartest-check` | 68 |
| `npcbusypartner` | objects/misc | yes | yes | check `npcbusypartner-check` | 49 |
| `pushwaitskip` | objects/misc | yes | yes | check `pushwaitskip-check` | 34 |
| `seasickpaused` | objects/misc | yes | yes | check `seasickpaused-check` | 10 |
| `socialfinish` | objects/misc | yes | - | - | 26 |
| `socialreceivefail` | objects/misc | yes | - | - | 44 |
| `socialworkerpromise` | objects/misc | yes | yes | check `socialworkerpromise-check` | 1 |
| `wishfeedbackkeep` | objects/misc | yes | yes | check `wishfeedbackkeep-check` | 1 |
| `buffstate` | occults | yes | - | check `buff-shared-state` | 3 |
| `familyinvghost` | occults | yes | - | - | 35 |
| `ghostreactdeadsim` | occults | yes | - | - | 26 |
| `joyridecostume` | occults | yes | - | - | 59 |
| `outfitwaitpoll` | occults | yes | - | check `outfit-wait-dead-check` | 11 |
| `littercap` | pets/horses | yes | yes | check `littercap-check` | 62 |
| `petpoolorphan` | pets/horses | yes | - | check `pet-pool-orphan-check` | 67 |
| `wildhorsegender` | pets/horses | yes | yes | check `wildhorsegender-check` | 6 |
| `aptleave` | phone | yes | - | check `apartment-leave-farewell` | 42 |
| `buffbufcheck` | phone | yes | - | - | 41 |
| `handsetreset` | phone | yes | yes | check `handsetreset-check` | 67 |
| `invitecallreply` | phone | yes | yes | check `invitecallreply-check` | 10 |
| `leavejobdispose` | phone | yes | - | check `leavejob-dispose-check` | 1 |
| `phonegive` | phone | yes | yes | check `phonegive-check` | 68 |
| `swslots` | phone | yes | yes | check `swslots-check` | 51 |
| `timeralarmleak` | phone | yes | yes | check `timeralarmleak-check` | 49 |
| `putdownplace` | put-down/surfaces | yes | - | check `putdown-child-loop` | 25 |
| `autmetarows` | routing/unseen/perf | yes | yes | check `autmetarows-check` | 42 |
| `autshadow` | routing/unseen/perf | yes | - | live `tier2-p1-dry` | 63 |
| `birthroute` | routing/unseen/perf | yes | - | check `birth-hospital-retry` | 31 |
| `boatroamcap` | routing/unseen/perf | yes | - | live `ip-boat-roam-cap` | 1 |
| `boattaximemo` | routing/unseen/perf | yes | - | live `ip-boat-nopath-memo` | 5 |
| `cookclicklistener` | routing/unseen/perf | yes | - | check `cook-click-listener-leak` | 69 |
| `errortrapctl` | routing/unseen/perf | yes | - | - | 71 |
| `errortraplookup` | routing/unseen/perf | yes | - | - | 71 |
| `errortrapsims` | routing/unseen/perf | yes | - | - | 71 |
| `etderefdiag` | routing/unseen/perf | yes | - | - | 71 |
| `houseboatgangwayrescue` | routing/unseen/perf | yes | - | live `ip-houseboat-gangway-rescue` | 1 |
| `houseboatlegmemo` | routing/unseen/perf | yes | - | live `ip-houseboat-gangway-rescue` | 3 |
| `overwatchdeadsim` | routing/unseen/perf | yes | - | - | 49 |
| `repairtimecache` | routing/unseen/perf | yes | - | check `repair-time-cache` | 1 |
| `rhexitunseen` | routing/unseen/perf | yes | yes | check `rhexitunseen-check` | 63 |
| `routecauselog` | routing/unseen/perf | yes | - | - | 66 |
| `routefailprobe` | routing/unseen/perf | yes | yes | check `routefailprobe-check` | 48 |
| `spotasync` | routing/unseen/perf | yes | yes | live `spotasync-trip-loop` | 50 |
| `spotbusydefer` | routing/unseen/perf | yes | yes | check `spotbusydefer-check` | 4 |
| `spotcut` | routing/unseen/perf | yes | yes | check `spotcut-check` | 36 |
| `spotedgememo` | routing/unseen/perf | yes | yes | check `spotedgememo-check` | 34 |
| `stuckwatchscan` | routing/unseen/perf | yes | yes | check `stuckwatchscan-check` | 51 |
| `tipsnpcend` | routing/unseen/perf | yes | yes | check `tipsnpcend-check` | 49 |
| `unseenboatgenboat` | routing/unseen/perf | yes | - | - | 1 |
| `unseenloopskip` | routing/unseen/perf | yes | yes | check `unseenloopskip-check` | 45 |
| `unseenloopskip_offscreen` | routing/unseen/perf | yes | - | check `unseenloopskip-check` | 19 |
| `visitcar` | routing/unseen/perf | yes | - | - | 71 |
| `visitgreetalarm` | routing/unseen/perf | yes | - | check `visit-greet-alarm` | 19 |
| `visittoolate` | routing/unseen/perf | yes | - | check `visit-too-late` | 34 |
| `convturn` | story progression/households | yes | - | check `conversation-turn-check` | 51 |
| `detailprioexact` | story progression/households | yes | yes | check `detailprioexact-check` | 71 |
| `lifeeventidfirst` | story progression/households | yes | yes | check `lifeeventidfirst-check` | 44 |
| `relremovesafe` | story progression/households | yes | - | - | 71 |
| `spawnerskip` | story progression/households | yes | yes | check `spawnerskip-check` | 67 |
| `spoonreset` | story progression/households | yes | - | check `spoon-reset` | 26 |
| `puddlereach` | weather/seasons | yes | yes | check `puddlereach-check` | 17 |
| `seasonlistener` | weather/seasons | yes | - | - | 71 |
| `swapdelegates` | weather/seasons | yes | - | check `swapdelegates-check` | 71 |
| `wishupdatelost` | weather/seasons | yes | yes | check `wishupdatelost-check` | 1 |
