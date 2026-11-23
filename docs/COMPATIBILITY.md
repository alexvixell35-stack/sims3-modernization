# Compatibility with other mods

Date: 2026-11-22 (scan of our v0.3.1 mods with their release settings; updated 2026-11-23 with the ranks 101-500 tuning set, the code audit, NRaas copies and "Their fix or ours"). Data: [`docs/data/compat-matrix.csv`](data/compat-matrix.csv) (1451 rows; with ranks 101-500 of the most downloaded mods, see Coverage).

**Every verdict here comes from a static code-overlap scan, not from playing the game with the mod. "No overlap" does not guarantee that a mod works with ours, and "overlap" does not mean it is broken.**

## Check your own Mods folder

To check your own Mods folder, use Apex Radiance's **mod conflict scan** (`ModConflicts`, by loinyx and the Apex Radiance project; it started as a port of our `compat-scan.ts`). It reads your `Mods` folder and `Game\Bin` and changes nothing there. It lists mods that clash with each other:

- the same resource in two mods, with the likely winner
- two core mods replacing the same game file
- old and new copies of one script mod
- two mods replacing the same interaction
- two plugins (`.asi`) patching the same game address. Where our `ts3native` shares an address with another plugin on purpose (one side steps aside), it shows INFO, not a conflict.

How to run it: on Windows with the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), download or clone [Apex Radiance](https://github.com/loinyx/Sims3-ApexRadiance). In that folder, run:

```
dotnet run --project tools/mod_conflicts
```

With no folder given, it scans the game it finds. Add a folder path to scan another folder. `--offline` skips its one download of the hook table. Exit code 1 means it found a conflict. A WARNING is a hint, not proof of a clash. Full description: [tools/mod_conflicts/README.md](https://github.com/loinyx/Sims3-ApexRadiance/blob/main/tools/mod_conflicts/README.md) in the Apex Radiance repository. Thanks to the Apex Radiance team for maintaining it.

The table below answers a different question: which popular mods reach the same game code as **our** mods.

## What the table means

The scan reads a mod file (it never runs it) and compares it with what our mods touch. On the `ts3fixes` side, that is the game methods it patches, each marked live or off with the release settings: 790 of 936 patched methods are live, and 630 of 847 switches are on by default. On the `ts3native` side, it is the hook addresses (1,333). Each address is marked by whether its module is on in the v0.3.1 Steam ini or the EA app ini. One row is one mod file. Plain custom content (CC) packages without overlap are left out.

| Column | Meaning |
|---|---|
| `mod`, `file` | Mod name and the file's place in a Mods folder or MTS download |
| `kind` | `script`, `tuning`, `script+tuning`, `cc`, `archive` (could not be read here) or `empty` |
| `overlap` | Which of our features (and their switches) touch the same code; `[off by default]` marks features whose switches are off in the release |
| `verdict` | `no overlap` (1226 rows), `overlap` (223), `overlap, off by default` (1: the mod only touches features that are off), `unknown` (1) |
| `status` | Overlap rows only: `in our test setup` (7), `same code, not tested together` (215), `known limit` (1) |
| `note` | Our reading of the row, when there is one |
| `evidence` | What the scan saw (for example "overrides X::Y" or "replaces module Z"), tagged with the scan it came from |

An overlap means a mod and one of our features reach the same game code. It shows where to look and is not a bug report. Only `in our test setup` is based on game runs: those files are in the install that every one of our game tests uses.

## Coverage

- **All 100** of Mod The Sims' 100 most downloaded TS3 game mods (list of 2026-11-22). Every download was scanned: 49 mods from 2026-11-05 were rescanned and the other 51 were downloaded for this scan ([docs/24](24-community-re-projects.md)). 842 files in total, together with our test install.
- **Ranks 101-500 (scanned 2026-11-22 and 23):** the script mods among Mod The Sims' ranks 101-500 (all dates) plus the tuning and other override mods dated 2014 or later: 218 items (153 script, 65 tuning/override; CC-like and miscellaneous items left out). Scanned so far: 216 items, 734 files: no overlap 614, overlap 119. Skipped as too big and without script or tuning: HD Textures Terrain Pack (666599, 3.5 GB of textures) and More BITS! soundtrack (635893, 0.9 GB of audio). Only part of two downloads finished (the site sent some files too slowly): TS4 Music (636565, 1 file of several) and Arms Dealing Mod (677382, without its translation strings). Sources: [docs/24](24-community-re-projects.md).
- Our own test install: NRaas ErrorTrap and Overwatch, the Sims 3 Store packages, simler90's core mod and other fixes.
- A player's Mods folder of 108 files (Dalexlol, issue #165, scanned 2026-11-02). These rows were not rescanned and still reflect our mods as of 2026-11-02.

Counts for our own scan (609 rows): no overlap 519, overlap 90 (7 in our test setup, 83 same code, not tested together), unknown 0.

## What changed since 2026-11-05

- **Our mods touch much more code now.** v0.3.0 and v0.3.1 added about 440 EA bug fixes in `ts3fixes`, the script compiler and many `ts3native` hooks. The scan now reads its list of patched methods straight from the code (including the `Hook(...)` swaps it missed before) and uses the release default of each switch. A feature that is off by default no longer counts as an overlap.
- **Coverage went from 49 to 100** of the MTS top 100. Rows: 334 to 717. Verdicts: no overlap 308 to 612, overlap 25 to 104, unknown 1 to 1 (the only unknown is in the 2026-11-02 player-folder rows; our scan has none, because `.rar` / `.7z` downloads are unpacked now and zips nested in zips are read).
- **New overlaps with features that are on by default:**
  - Three EA fixes sit in base methods that many interaction mods override: `cleanupvfx` (`InteractionInstance.Cleanup`), `initstacktrace` (`InteractionInstance.Init` / `OnAddedToQueue`) and `serviceshowalarm` (`ServiceSituation.OnServiceStarting` / `CleanUp`). This is most of the new overlap rows.
  - A second core mod: Consort's 2x Weight and Fitness Integration.
  - Castable RabbitHole Replacements (Lyn), which subclasses the hover train station (`hovertraintest`).
  - XCAS now meets more of our UI fixes.
- **Known conflicts** found by game runs were added (below).

## The overlap rows (2026-11-22)

Nobody has found a fault in any of these pairs. We do not call any pair "works" or "compatible". In-game checks for the rows that are not in our test setup are written up as queue `compat-overlap-1122` but have not run yet. This page updates when they finish.

### Harmless overlap (read the code, not tested in game)

"Harmless" means we read the code and found that our hooks and the mod do not get in each other's way. It is not an in-game test.

- **Interaction mods (27 rows, 18 mods):** Weapons System and Death Note (Desecrate), Housecleaning For All Sims, Buy Takeout, Scribbling Pad (Buzz), Non-Core Global Online Banking, Study Skills Online, Toddler Interactions (Cherry), More Alchemy Potions, Cook With Any Ingredient, The Sims 3 Passion And Romance, Napping Mat, Children can care for their lil' siblings, More Baby / Toddler / Pregnancy / Romantic Interactions (TSS), Gardener Service. Their interactions override `InteractionInstance.Cleanup` / `Init` (and Gardener and Buy Takeout also override `ServiceSituation`). Our EA fixes in those base methods run when the mod's override calls the base method. When it does not, that interaction keeps EA's original behaviour. Either way the mod's own code runs as written.
- **XCAS (cmar), 4 files:** replaces the UI module. An IL diff of its Steam 1.67 builds (`tools/mod-method-diff.ts`) found 51 changed methods, and none of them is one of the 28 UI methods our default-on features patch.
- **Castable RabbitHole Replacements (Lyn):** its ZEPHYR station subclasses the hover train station. Our hover train memo turns itself off when a mod declares its own station start-up, so the game's own check answers.
- From the 2026-11-02 player folder (not rescanned): Seasons Expanded, Library_5/10/14/15/16, icarusallsorts EatOutsideRestaurant, NRaas MasterController and Register. Tiny UI Fix is a known limit: one speed-up of ours (AutShadow) may turn itself off with it.

### Real overlap, tested in every run of ours

These files are in our test install, so every game test of ours ran with them loaded.

- **simler90 Gameplay Core Mod** (UPDATE242, Steam): replaces game modules and overlaps most features (see its CSV row). The Mono offload, which is off by default, needs a guard for this mod (`docs/128`).
- **NRaas ErrorTrap**, **NRaas Overwatch**: our features overlap with them and ship with repros.
- **Sims 3 Store packages** (Store Fixes, Updates 001-020, 021-040, 041-051): the same base-method overlap as the interaction mods above.

### Real overlap, game test pending

- **Moar Interactions (Buzz):** its visit interactions override the `Run` methods we patch without calling the base method, so our visit hooks skip them (queue `compat-top`). It also has the interaction base-method overlap above.
- **Overrides.package** (Lyn, player folder): overrides `VisitCommunityLot::Run`, which our hook wraps.
- **2x Weight and Fitness Integration Core Mod (Consort), 47 files** (every version and edition): a core mod with its own `Sims3GameplaySystems`, like simler90's. Every `ts3fixes` patch on that module lands on its copy. It cannot be used together with another core mod, and we have not tested it.

### Real overlap, can't test here

- **simler90 + fixes** (a player's own build): we have no copy.
- **Sims3MonoModder (S3MM, ASI)** (source read, docs/291): it shares Mono call paths with our JIT, fast call path and AutShadow. `ts3native` turns those three off by itself when `Sims3MonoModder.asi` is installed (`[Compat] S3mmStandDown=1`, the default). We have not tested the two together in game.
  - **S3MM's profiler is not compatible with ts3native.** S3MM always installs the profiler at start, and it hooks Mono functions we also hook. Whether it crashes depends on which mod loads first. It also clashes with our `[Verify]` probe, and its numbers are wrong under our JIT. S3MM has no setting to turn the profiler off yet (we proposed one upstream).
  - The rest of S3MM works with the stand-down. We do not ship or install S3MM.

### Known conflicts (seen in game runs, not ours)

Two packages stopped the game on our setup (Steam 1.67, Apex Radiance + RTX Remix bridge, an Isla Paradiso test save) **with none of our mods installed**. We found them while bisecting a large Mods folder ([docs/314](314-kit-ab-mode.md)):

- **`EnhancedWitches.package`** (Enhanced Witches, MTS 608185; sha256 `684514e4...11bb`): the game hangs before the main menu, with this package alone, twice (2026-11-21).
- **`velocitygrass_AddAnyLotSize.package`** (Add Any Lot Size, MTS 471164; sha256 `316f12dc...2bad`): the main menu comes up, but loading the world never finishes, with this package alone, twice (2026-11-22). A tester on the EA app 1.69.47 loads fine with it, so this may depend on the game version or setup.

Seen on our setup, not caused by our mods, and not proven for every setup. If your game loads with one of them, keep it. If it hangs at the same point, try without it first. `compat-scan.ts` flags both by file name (`docs/data/known-bad-mods.json`, status `hang-seen`).

### Tools that rewrite your Mods folder

- **SmellyMods CC String Merge** (MTS, offline tool, added 2026-11-19, source read, docs/304): compatible, no overlap. It runs while the game is closed. It moves the string tables of your CC into one table per language, and with `--merge` it also packs the CC into a few big packages. It leaves script mods (ours included) alone. Nothing of ours hooks what it changes, and none of our caches depend on your CC files.
  - Text can change in two rare cases. A package that replaces one of the game's own string tables (some translations and text edits) still shows its text, but the keys it leaves out now show the game's text instead of nothing. When two CC packages give the same string different text, the tool picks by file name, which may not match the game's own pick for files in different subfolders.
  - `compat-scan.ts` names the tool's output files and flags packages that replace a game string table.
  - After merge mode, known-bad checks by file name no longer work; checks by resource key still do.

### Ranks 101-500: new overlap rows

Most new rows are the same base-method overlap as the interaction mods above (`cleanupvfx`, `initstacktrace`, `serviceshowalarm`): Working Sim's Bed, Modern Elevators, More Vampire Powers, Eat Outside Restaurant, Online Center, Canning Station Overhaul, Toddlers Extra Activities, Enhanced Vampires, Yoga Mod, Pick Up Toddler Fixes, Bistro set, Consort's Age, Zerbu's Ultimate Careers and its 2.0 rework, Professional Massage Table, Pool Jobs for Lifeguards, Toddlers Can Learn to Walk Alone, Level Up Video Game Skill, No Shopping From Fridge, The Savvier Seller, Eiffel Tower, Working Futon, Gardening Spells, Reheat Food in Microwave, Mud Bathtub, Custom Beverages, Family Kiss, Break Into House, Take Sims to Court, One With Nature, Investment Mod, Livestreaming Mod, Shop for Clothes, Dolphin's Magic Gardening Gnome and Mood Manager, Jogging Track Marker, Japanese Culture Trait, Exotic Dancers Stage, Take a Stroll, Generic Food Maker, Mangaka Active Career, Buzzler's Scribbling Pad Fixed, Arms Dealing Mod, Baking Mod. Rows that reach more of our code:
- **Job Overhaul (gamefreak130, 629524):** overlaps our trip fixes (go home, rabbit holes, safe spots). Harmless: the code was read, and it calls the game's own code wherever we hook ([docs/318](318-mod-audit.md)).
- **University Student Union and Library shells (611709):** it replaces the game's community-lot visit in every world, so our visit fixes (arrival, on-lot, car and walk) do not run while it is installed. Outside the University world its visit is the game's own, so play is as without our visit fixes. Our shell lighting fix still runs, but not on the mod's own light pieces. Not tested in game.
- **Debug Console Command Enabler (343068) and No Shoo Mod (354513):** core mods from 2009 (game patches 1.0-1.2) that replace the whole gameplay module with a copy of that time. Every one of our script fixes lands on that old copy. They lack parts that every later game module needs, so they cannot load on today's game (read from the files, not tested).
- **Chef Service (495287), The Chef Plant (495651):** our service fixes and a group of EA gardening and harvest fixes. Harmless (code read).
- These replace a game step with their own copy, so one fix of ours does not run for what they handle (code read, not tested in game): **Fun in the Sun (530601)**, our boat roaming cap for rowboats and paddleboats; **Toddlers Can Use Stairs (655350**, also its GoHere variant), our stair wait fix; **Enhanced Hospitals (666384)**, most of our birth trip fix; **Pool Jobs for Lifeguards (574660)**, our lifeguard-quit fix. The last one hides the game's method instead of overriding it, which the scan does not see; the row shows only the base-method overlap.
- **Functioning Well and Off-Grid Plumbing (637857), Functional Sewing Table (643837), Flower Arranging (667032), Kitchen Tweaks (691504):** besides the base-method fixes, our bath clean-up, fight broadcast and object-use fixes. Harmless (code read). The Sewing Table's own world-quit step always fails, which can skip quit steps that run after it, ours included (inferred). Functioning Well and Kitchen Tweaks replace the same serving clean-up action, so only one of them wins.
- **Break Into House (650590):** carries its own copy of the game's break-in action, so our private-eye case fix does not reach that copy. **Modern Elevators (469658):** its own copy of the elevator logic, so our elevator rider fixes do not reach it. Nothing breaks in either case (code read, not tested in game).
- **Private Room Door (566837):** only our door mirror feature, which is off by default. With that switch on, its key-only door would probably count as open (inferred); ours to fix before the switch goes on.
- **The Randomizer Mod (668302)** and **Gardener Service 2 (575912):** our service fixes (`serviceshowalarm`, `serviceleave`). Harmless: the code was read; for Gardener Service 2 one of our fixes stops a duplicate alarm.

Several of the mods above that keep one of our fixes from running now get that fix on their own code. See "Their fix or ours" below.

### Ranks 101-500: tuning mods of 2014 and later

All 28 tuning mods (Overrides > Tuning Mods, dated 2014 or later) of ranks 101-500 were scanned: 130 files, **no overlap** in any of them (static scan, 2026-11-22). They change game values (XML tuning), and none of our features reads or replaces the same tuning. The set: Learn Cooking Recipes by Watching TV (541675), Faster Upgrades Times (526870), More Realistic Dining For Sims (529986), Opportunity phone call fix (560635), Non Default Complete Skill Books Revised (500201), Babysitter Mod (544734), Faster Gardening Mod (668108), Better Sleeping Babies & Toddlers (519354), Free Your Klepto and Observant Sims (392602), Find All Jobs in Newspaper or on any Computer (524832), Unrestricted Sponge Baths TYAE (518284), Quicker Cooking & Eating OverRide (599349), Food and Cooking Tweaks (541518), More Dangerous Zombies (520154), No More University Life NPC Protests (566803), Enhanced Wands (614681), Extended Buffs (502573), Improved Writing Skill (556888), Watch Laser Rhythm-a-Con FIX (517669), Better Bartending (541922), Clutter Your Map (614360), Vampires Are Not Kind (657644), Chance To Learn Teen Hangout (689963), No_Mascot_CameraZoom (590518), Boarding Schools Skill, Career, & Trait Additions (687016), Fish Tank Capacity and Fish Bowl feeding mods (541635), Autonomous Play In The Ocean Fix (686121), No Bad Photos (583390). Two tuning mods that change the same value still clash with each other; the Apex conflict scan (above) shows that.

### Ranks 101-500: what the code audit found in the mods themselves

The overlap mods of ranks 101-500 were read in four batches ([docs/318](318-mod-audit.md), static only, every finding checked a second time). Every script item was also checked for hidden copies of game code we patch; apart from the copies named above (Pool Jobs, Break Into House, Modern Elevators and others), none was found. A copy under a new name that does not derive from the game's class would still be missed.

Some mods have bugs of their own, with or without our mods. We list them here so you know where a problem comes from; we do not fix other mods' code. The confirmed ones a player can run into:
- **Take Sims to Court (670856):** Serve Jail Time can freeze the game when a Sim serves the full sentence (hang, confirmed in the code).
- **Break Into House (650590):** when a resident catches the burglar, the alarm step can throw a script error on lots without a burglar alarm or in towns with several (confirmed in the code).
- **Enhanced Vampires (648599):** the Friendship, Romance and Hatred aura pulses always throw a script error (confirmed in the code).
- **Functional Sewing Table (643837):** its world-quit step throws every time, so every quit step after it is skipped (confirmed in the code; see `modquitguard` below).
- **Arms Dealing (677382):** one household Sim without the mod's skill stops the weekly payments for that session (confirmed in the code).
- **Pool Jobs for Lifeguards (574660):** a lifeguard who quits or is fired mid-shift throws in the rescue check. The base game has the same bug; our fix now covers the mod's copy (below).

The full list, with rarer and possible issues, is in docs/318.

### NRaas mods

The NRaas source was read for StoryProgression, GoHere and 17 more NRaas mods ([docs/318](318-mod-audit.md), NRaas section). The release files are not scanned here: nraas.net asks for no automated downloads.
- **StoryProgression:** its trips go through our fixes; our own fixes to EA's story progression go idle while it runs; Sims it creates skip three of our Sim-creation fixes. Game test written (queue `compat-sp-1122`), not run.
- **GoHere:** the largest overlap found. Our fallbacks for off-screen Sims deliberately leave NRaas interactions alone, and GoHere puts its own interaction on most NPC trips, so much of that work of ours does not run with it. Not tested in game.

Added 2026-11-23: the release files of 45 NRaas mods, downloaded on 2026-11-22, were checked for copies of game code we patch (binary check against the source; not in the CSV). What we found and what we do ([docs/328](328-compat-rules.md) §7.2, §7.5):
- **NRaas occult helper (20 mods:** Careers, Consigner, Debug Enabler, Dresser, ErrorTrap, GoHere, Hybrid, MasterController, Mover, Overwatch, Porter, Portrait Panel, Register, Relationship Panel, Relativity, Selector, StoryProgression, Tagger, Traveler, Woohooer): their shared code changes a Sim's occult type without the game's own step, so a door answer we cache could go stale after an NRaas occult change (rare, verified in the code). Our door refresh now runs after their copy too.
- **NRaas Overwatch** and **StoryProgression Skill:** each has its own copy of the harvest step with EA's bug that loses Gatherer and Science extras. Our harvest fix now runs instead of that copy (a money tree still uses theirs). Overwatch's other copies were compared and need nothing from us.
- **NRaas Dreamer V15:** its own wish update runs before the game's, so two of our wish fixes rarely ran. They now run around Dreamer's copy, and Dreamer's own error catch stays.
- **NRaas ErrorTrap:** our script-error hook was built for its version and wraps it (no change). Its occult copy is covered as above.
- **NRaas GoHere V46:** its routes skipped our failed-route re-plan for every Sim; that fix now runs after GoHere's route setup. Letting our off-screen shortcuts also handle GoHere's visit interactions is a separate switch, `unseenfbgohere`, **off by default** (see below).
- **NRaas Woohooer V130:** its WooHooty text has a wait with no way out. Our bounded wait now runs before it (KamaSimtra's version is not covered).
- **NRaas MasterController V135:** its copy of the CAS row build keeps EA's leak of empty rows. Our CAS row fix now covers its copy.
- Not changed, recorded only: NRaas's own relationship removal (already guarded), route helpers, and a few more copies listed in docs/328 §7.2.

## Their fix or ours (2026-11-23)

Where a mod and one of our fixes reach the same code, we compared the two ([docs/328](328-compat-rules.md) §7). If the mod's fix were better, our fix would step aside for it. If ours is better, ours now also runs on the mod's own code (we never change the mod's files). The verdict counts (§7.1):

| set | rows compared | ours better | theirs better | equal or side by side | old build |
|---|---|---|---|---|---|
| top 100 (master matrix) | 49 | 5 | 0 | 4 | 38 |
| ranks 101-500 | 65, plus Fun in the Sun rechecked | 14 | 0 | 8 | 42 |

Two rows are not decided yet (Enhanced Witches, Smooth Patch). No mod fixed the same EA bug better than we do, so **nothing new stands down for another mod**: the stand-down list is still S3MM only (see above). "Old build" means a mod that carries a copy of a game module for an older patch: there is nothing to compare, and you need the mod's current build.

**What our fixes now do on the mods' own code.** Each of these follows its own fix's switch, needs no new setting, and writes one line per mod method to `ts3native.log` at world load (`compat precedence: ... ok`, or why it was left alone). In-game check `ip-rules-r-resolve-on`: PASS (verified: every hook goes on, no script errors, no crash). What each hook changes in play is verified by the code reading, and in game only where a repro is named.
- **Enhanced Hospitals (666384):** our birth fix (`birthroute`, **on**) now also runs on the mod's donor and surrogate births, so a birth that ends without a baby no longer retries every minute for ever. Repro PASS in game (verified: an NPC mom stops after a few tries; with the fix off the loop comes back). A household mom keeps the mod's own one-minute retry, as without our mods.
- **Autonomous Fun in the Sun (530601):** our boat roaming cap now covers its rowboats and paddleboats.
- **Toddlers Can Use Stairs (655350,** both builds): an NPC stuck waiting on a stair gets our 30 sim-minute deadline. Household, selected and unknown Sims keep the mod's wait. The mod's own cancel exit stays.
- **Pool Jobs for Lifeguards (574660):** our lifeguard-quit fix now stops the rescue check for a Sim with no job.
- **Sims 3 Store Updates 021-040 / 041-051, More Romantic Interactions v2 (691427), Enhanced Vampires (648599):** our effect clean-up runs after their clean-up. Store objects are covered from the next world load after their code first loads.
- **University Student Union / Library shells (611709):** the game's visit shortcut works again outside the University world. Our arrival, on-lot and car fixes still do not run for its visits.
- **Break Into House (650590),** all 6 builds: the front-door lock is put back after its break-in, as our fix does for the game's own break-in.
- **Modern Elevators (469658):** riders waiting when its elevator resets are no longer lost. Our rider time-out does not cover its elevator.
- **Functional Sewing Table (643837):** new switch `modquitguard`, **on by default**. Its quit step's error stops at that step, so the game's, other mods' and our quit steps after it run. Tested in game both ways (verified: with the switch on, the error is caught and the game reaches the main menu; with it off, the error is passed on as before).
- **Private Room Door (566837):** our door mirror (off by default) skips lots with this kind of door, so the game answers there.
- **Business as Unusual Bistro set (513025):** its oven's clean-up is empty, so EA's effect leak stays for it. We cannot hook an empty method (not fixable from our side).

**Off by default, test pending:** `unseenfbgohere` lets our off-screen shortcut and fallback handle NRaas GoHere's visit interactions for unseen NPCs (household and selected Sims never). Its in-game test (`bp-gohere-ab`) failed on our test tool's switch read-back before the game test ran, so there is no verdict yet. It stays **off** until a rerun passes. Leave it off unless you are testing it.

**What you need to do:**
- Nothing for the mods above: keep them installed, our fixes adapt at world load.
- **2x Weight and Fitness Integration (Consort), old builds:** use its 1.66, 1.67 or 1.69 build. The older builds carry a game module for patch 1.63 or older.
- **Debug Console Command Enabler (343068), No Shoo Mod (354513):** remove them. They are 2009 copies of the game module and cannot load on today's game (read from the files).
- NRaas Overwatch and Cupcake settings that help with our fixes are drafted for Apex Radiance's setup installer; they are not shipped yet, and you do not need to change anything now.

## Our scan, for developers

```
node tools/compat-scan.ts "<your Mods folder>" --matrix my-compat.csv
```

It needs Node 22.18 or newer and your game folder (set `TS3_GAME` if it is not in the default Steam path). It only reads files. With a checkout of this repository it uses our current sources and the release inis (`--ini`, `--ea-ini`). Run `node tools/compat-scan.ts --self-check` to check that it works. For players, Apex Radiance's conflict scan (above) is the easier tool.

## Is my PC throttled?

Our fixes move work to other CPU threads. Those gains get smaller on a PC that slows its CPU (heat, power limit, power-saver plan, battery), runs other heavy programs, or limits the game to a few cores.

Builds with `[PcCheck]` (docs/299) check this while you play and write `pccheck-summary.txt` in `Game\Bin`. It has one line that says either "CPU: no throttle seen" or "CPU: clock limited X% of the time", plus the power plan, battery, other programs' CPU use and the game's affinity, priority and efficiency mode. With `[PcCheck] Notify=1` (off by default), a notice appears in the game only when it finds a real problem, with a one-line fix.

The tester kit prints the same short check before it installs (warnings only). The check only reads Windows numbers and changes nothing.

## Report

Found something? Open [an issue](https://github.com/rodneypantonial/sims3-modernization/issues/new) and say what happened in the game. Attach the Apex conflict scan output, your `my-compat.csv`, or the row you doubt. File names in `my-compat.csv` are relative to the folder you scanned, and CC-only packages without overlap are left out. A scan row alone does not prove a problem.
