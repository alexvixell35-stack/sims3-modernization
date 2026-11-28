# MTS listing draft: Sims 3 Modernization v0.3.1

**Draft for Rodney. Not posted.** Nothing here has been sent to ModTheSims (MTS). Launch readiness (PLAN.md "Launch readiness before any promotion") decides when it goes up; Rodney clicks the final Submit himself (`release/mts/CHECKLIST.md` part C).

This replaces the v0.1.5 text in `release/mts/listing.md` for the next MTS post. The upload kit around it still applies: `release/mts/RULES.md` (rules research of 2026-10-21), `release/mts/CHECKLIST.md` (wizard steps), `release/mts/package.md` + `tools/mts-zip.ts` (the zip), `tools/mts-images.ts` (info images). What changed since then is in "MTS rules, rechecked 2026-11-22" and "Open questions" below.

## Wizard fields

| Field | Value |
|---|---|
| Game | The Sims 3 |
| Section | **Game Mods > Miscellaneous**, Type **Other** (Rodney's decision of 2026-10-21; precedent: an `.asi` + `.package` mod in that section, Type "Other", rechecked 2026-11-22). Fallback: Game Mods > Script & Core Mods. |
| Title | `Sims 3 Modernization v0.3.1: Less Stutter, Fewer Crashes (Script Mod + ASI, Steam 1.67 / EA app 1.69)` |
| Packs required | **None.** Tick no EP, SP or store pack (see "Packs" below). |
| Main file(s) | see "Files to attach" |
| Thumbnail + screenshots | see "Images needed" |
| Summary (if asked) | see "Summary" |
| Description | see "Description (BBCode)" |
| Additional credits (if asked) | see "Additional credits" |

### Packs: why "None"

`ts3fixes` compiles only against the script assemblies of the base game's `Game\Bin`, and `ts3native` patches only the game exe, which is the same file with any pack mix [verified, `release/mts/listing.md` "Packs"]. Fixes for pack features just never run when the pack is missing [inferred from the code paths]. **Tested only with all expansion and stuff packs installed.**

## Summary

> Less stutter and fewer crashes for The Sims 3 (Steam 1.67 or EA app 1.69.47): a script mod with over 400 fixes for the game's own bugs and an ASI plugin that moves work off the main thread. Measured: 13-28% more sim steps per second and far fewer long freezes in three towns. Nothing is stored in your saves.

## Description (BBCode)

```
[size=4][b]Sims 3 Modernization v0.3.1[/b][/size]
[i]Less stutter and fewer crashes for The Sims 3 on modern PCs: a script mod plus a native ASI plugin. Steam (patch 1.67) or EA app (patch 1.69.47).[/i]

[b]What it does[/b]
The Sims 3 runs its whole simulation on one CPU core, and much of that time goes to work you never see: townies walking across town off screen, route plans that fail and retry, nightly clean-ups that run all at once, slow script code. This mod skips that invisible work, moves route planning to a second CPU core, runs the game's busiest script methods as compiled code, and fixes hundreds of the game's own bugs. Your household and the selected Sim always get the game's own behaviour.

[b]What kind of mod it is[/b]
[list]
[*][b]ts3fixes.package[/b] is a [b]script mod[/b] (needs Script Mods enabled). At game start it replaces game methods with fixed versions; it replaces no game files. Same file for Steam and the EA app.
[*][b]ts3native.asi[/b] + [b]ts3native.ini[/b] is a [b]native plugin[/b] for the game's exe, loaded by an ASI loader. It checks the bytes of every place it changes first and switches itself off on any other game version. Settings are in ts3native.ini. There is one build for Steam 1.67 and one for the EA app 1.69.47.
[/list]
Each part works on its own; together they give the most.

[b]Measured results (v0.3.1 vs baseline)[/b]
10 minutes of play at speed 3 on aged 14-day saves, 2 runs per side, alternated. Baseline = the same setup without our mods: Apex Radiance with its RTX Remix bridge, Sims 3 Settings Setter, NRaas and other community fixes. One laptop (i7-11800H, 32 GB RAM, RTX 3070 Laptop GPU), Steam.
[list]
[*]Sim steps per second: Bridgeport 42.6 -> 54.5 (+28%), Isla Paradiso 41.9 -> 48.0 (+15%), Sunset Valley 59.4 -> 67.0 (+13%).
[*]Slowest 1% of sim steps (ms): Bridgeport 111 -> 78, Isla Paradiso 287 -> 129, Sunset Valley 82 -> 55.
[*]Worst single step (ms): Bridgeport 610 -> 272, Isla Paradiso 819 -> 530, Sunset Valley 1006 -> 303.
[*]Steps of 300 ms or longer per run: Bridgeport 5.5 -> 0.5, Isla Paradiso 151 -> 18, Sunset Valley 10 -> 0.5.
[*]Cost: our mods use about 115-220 MB of the game's address space.
[/list]
All numbers come from one machine and one set of saves. Treat them as "this is what we saw", not as a promise; reports from other PCs are welcome. Method and full table: the README on GitHub.

[b]Switched on in this release (short list)[/b]
[list]
[*]Townies you can't see skip the long walk or boat ride; they still do the last steps and arrive as normal.
[*]Route planning runs on a second CPU core; the game's own planner does the work.
[*]The game's busiest script methods run as compiled code (Steam), with the same results as the game's interpreter.
[*]About 440 fixes for the game's own bugs: socials that never finish, Sims stuck at activities, ceremonies, services, moods that don't match the moodlets, memory left behind by menus, and many more.
[*]Crash guards for rare EA crashes (loading screens, Sims and objects coming and going, custom-content meshes without a material).
[*]Fewer script memory clean-up pauses, faster saving in big towns, faster unpacking of game files.
[/list]
The full list of what is on, in plain words, is in the release notes on GitHub (docs/PLAYERS.md).

[b]Save safety[/b]
[list]
[*][b]Nothing of ours is stored in your saves.[/b] Where a feature must remember something per save, it keeps a small text file in Documents\Electronic Arts\The Sims 3\ts3native\ instead.
[*][b]Remove it any time.[/b] Saves made with the mod load in the unmodded game (tested for v0.3.0).
[*][b]Every fix has its own switch:[/b] a section in ts3native.ini, or one tuning value in ts3fixes.package.
[*]A few bug fixes clean the game's own leftovers while you play (for example references to Sims who are gone); the game saves that cleaner state as usual. The bigger save clean-ups are off.
[/list]
Keep a backup of Documents\Electronic Arts\The Sims 3\ anyway, as with any mod.

[b]Requirements[/b]
[list]
[*]The Sims 3 from [b]Steam[/b] (patch 1.67) or the [b]EA app[/b] (patch 1.69.47), Windows 10 or 11. Use the ts3native zip for your edition. Origin, disc and other patches are not supported: the plugin detects a different exe and does nothing.
[*]No expansion or stuff pack needed. Tested with all packs installed.
[*]An ASI loader for the plugin (not included): if you already use Sims 3 Settings Setter or another .asi mod, you have one. Otherwise get [url=https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases]Ultimate ASI Loader[/url] (Win32 zip) and use its dinput8.dll renamed to wininet.dll.
[*][b]Recommended:[/b] [url=https://github.com/loinyx/Sims3-ApexRadiance]Apex Radiance[/url] with its RTX Remix bridge (rendering in a separate 64-bit process). All our release tests run on it. Plain DXVK still works but is best effort.
[/list]

[b]Install[/b]
[list=1]
[*]Close the game and back up Documents\Electronic Arts\The Sims 3\ (saves and mods).
[*][b]Script mod:[/b] put ts3fixes.package in Documents\Electronic Arts\The Sims 3\Mods\Packages\. No Mods folder yet? Create Mods\Packages and a Mods\Resource.cfg (text in README.txt in the download).
[*][b]Native plugin:[/b] open the game's Game\Bin folder (Steam: right-click The Sims 3 > Manage > Browse local files; EA app: C:\Program Files\EA Games\The Sims 3\Game\Bin by default) and put ts3native.asi and ts3native.ini from the zip for your edition there. Add the ASI loader (wininet.dll) if you don't have one yet.
[*]Start the game from Steam or the EA app and allow script mods when asked.
[*]Check: Game\Bin now has a ts3native.log file. On the EA app it says "host build: EA app 1.69.47".
[/list]
Updating: replace all files, and use the [b]new[/b] ts3native.ini.

[b]Uninstall[/b]
Delete ts3fixes.package from Mods\Packages, and ts3native.asi and ts3native.ini from Game\Bin (plus wininet.dll if you added it only for this). Your saves keep loading without them. The folder Documents\Electronic Arts\The Sims 3\ts3native\ can be deleted too.

[b]Compatibility[/b]
[list]
[*][b]Tested together with[/b] Apex Radiance and its RTX Remix bridge, Sims 3 Settings Setter (S3SS), NRaas ErrorTrap and Overwatch, Smooth Patch and the Sims 3 Performance Patch (S3PP); DXVK as a smoke test only.
[*][b]NRaas:[/b] not replaced. Overwatch's nightly clean-up and ErrorTrap's after-load check run in steps; 24 of our fixes step aside when NRaas StoryProgression, GoHere, Consigner or Woohooer is installed.
[*][b]Other ASI plugins:[/b] every patch of ours checks the original bytes first and skips itself if another plugin changed them; we mapped every address S3SS and S3PP patch. Where Apex Radiance has a fix for the same code, ours steps aside.
[*][b]Sims3MonoModder (S3MM):[/b] its profiler is [b]not compatible[/b] with ts3native; don't use them together.
[*][b]Tiny UI Fix:[/b] works, but one of our speed-ups may switch itself off.
[*]A static code scan of all 100 of MTS's most downloaded TS3 mods against ours (842 files, 2026-11-22) is on GitHub (docs/COMPATIBILITY.md). It is a code scan, not a game test.
[*][b]Check your own Mods folder:[/b] use [url=https://github.com/loinyx/Sims3-ApexRadiance/blob/main/tools/mod_conflicts/README.md]Apex Radiance's mod conflict scan[/url] (by loinyx and the Apex Radiance project). It only reads your Mods folder and Game\Bin and lists mods that clash with each other. Windows, .NET 8 SDK, in a copy of the Apex Radiance repository: [code]dotnet run --project tools/mod_conflicts[/code]
[/list]

[b]Known conflicts (seen on our setup)[/b]
These two stopped the game on our Steam 1.67 test setup [b]with none of our mods installed[/b], so they are not caused by this mod. Seen on our setup, not proven for every setup:
[list]
[*]EnhancedWitches.package: the game hangs before the main menu.
[*]velocitygrass_AddAnyLotSize.package: the world load never finishes. A tester on the EA app 1.69.47 loads fine with it.
[/list]
If your game hangs at the same point, try without that package first.

[b]Known issues[/b]
[list]
[*]Apex Radiance 4.0.13 with the RTX Remix bridge: a rare crash in d3d9.dll when entering Buy mode. It happens on the Apex side, before any of our code runs; please report it to Apex Radiance.
[*]Apex Radiance: a rare freeze between two of Apex's own locks and the game's drawing (Apex issue #90).
[*]EA app: townies you can't see are animated every frame (one speed-up is off there for now).
[*]Measured on one laptop only; numbers on other PCs may differ.
[/list]

[b]Antivirus[/b]
One antivirus engine family (Bitdefender and engines built on it) flagged an older release zip with a generic detection; we reported it as a false positive. An .asi plugin changes the game's code in memory, which generic scanners can react to. The plugin is built by GitHub Actions from the public source, and each GitHub release lists the SHA-256 of the built file, so you can check it. Details in the FAQ on GitHub.

[b]If the game crashes[/b]
First remove our files and see if the crash goes away. Then please [url=https://github.com/rodneypantonial/sims3-modernization/issues]open an issue on GitHub[/url] with Game\Bin\ts3native.log (copy it before you start the game again). Nothing is ever sent automatically, and neither file contains any network code.

[b]FAQ[/b]
Save safety, what each switch changes, NRaas / S3SS / S3PP overlap, Apex Radiance, how we measure and antivirus flags: [url=https://github.com/rodneypantonial/sims3-modernization/blob/HEAD/docs/FAQ.md]FAQ on GitHub[/url].

[b]How it is made (AI disclosure)[/b]
The code and docs are written with AI coding agents, directed, reviewed and tested by the maintainer and contributors. Every fix has a repro, its own switch and test evidence before it ships, and new features ship off until measured. How each release file is built and how to check it: [url=https://github.com/rodneypantonial/sims3-modernization/blob/HEAD/docs/HOW-WE-BUILD.md]How we build[/url].

[b]Credits[/b]
The script mod and the plugin are our own code. No other creator's files are included.
[list]
[*][b]Dalexlol[/b]: most of the 400+ EA bug fixes, crash fixes and the Buy/Build pause fix, each with an in-game test.
[*][url=https://github.com/loinyx/Sims3-ApexRadiance]Apex Radiance[/url] by loinyx: the recommended renderer setup.
[*][url=https://github.com/sims3fiend/Sims3SettingsSetter]Sims 3 Settings Setter[/url] by sims3fiend: engine patches; reference for our native hooks.
[*][url=https://modthesims.info/d/658759]Smooth Patch[/url], [url=https://github.com/LazyDuchess/MonoPatcher]MonoPatcher[/url] and the Banding Fix by LazyDuchess: frame pacing and the method-patching approach our script mod follows.
[*][url=https://www.nraas.net/]NRaas[/url] (Twallan, Chain-Reaction and the NRaas team): ErrorTrap and Overwatch, studied for compatibility.
[*][url=https://www.nexusmods.com/thesims3/mods/235]Sims 3 Performance Patch[/url] and the Catalogue Lag Fix by Sonja92yu.
[*][url=https://modthesims.info/d/659969]simler90's Gameplay Core Mod[/url] by simler90, the House Boat Fix by FoulPlay, the routing fixes on MTS by their authors, ellacharmed's world routing fixes: part of our test setup.
[*][url=https://github.com/ThirteenAG/Ultimate-ASI-Loader]Ultimate ASI Loader[/url] (ThirteenAG) and [url=https://github.com/doitsujin/dxvk]DXVK[/url] (doitsujin and contributors): not included.
[*][url=https://github.com/Critical-Reynolds/Sims3-MCP]Sims3-MCP[/url] by Critical-Reynolds: base of our in-game test agent (not part of this download).
[*]Tools: Ghidra, REA, ILSpy, Frida.
[*]Every player who sent a crash report or log.
[/list]

[b]Source code and updates[/b]
Source code (MIT), full release notes and every measurement: [url=https://github.com/rodneypantonial/sims3-modernization]github.com/rodneypantonial/sims3-modernization[/url]. The same files are on the [url=https://github.com/rodneypantonial/sims3-modernization/releases/tag/v0.3.1]GitHub release v0.3.1[/url].

[size=1]Not affiliated with or endorsed by Electronic Arts or Maxis. Use at your own risk and keep backups of your saves.[/size]
```

## Additional credits

```
Dalexlol (EA bug fixes), loinyx (Apex Radiance), sims3fiend (Sims 3 Settings Setter), LazyDuchess (Smooth Patch, MonoPatcher, Banding Fix), Twallan, Chain-Reaction and the NRaas team, Sonja92yu (Sims 3 Performance Patch, Catalogue Lag Fix), simler90, FoulPlay, ellacharmed, ThirteenAG (Ultimate ASI Loader), doitsujin (DXVK), Critical-Reynolds (Sims3-MCP). Made with AI coding agents, directed and tested by the maintainer.
```

## Images needed

MTS rules (rechecked 2026-11-22, below): at least one **in-game** screenshot that shows the mod working; each image at least 800x600, at most 2592x1944 px (png up to 4.77 MB); at most 4 full-size images inside the description (screenshot slots don't count); no ReShade; no social-media logos.

| # | Image | Status |
|---|---|---|
| 1 | **In-game screenshot** (required): a busy town or lot during play, ideally Isla Paradiso, with the game UI visible. Optional second shot: the same scene with `ts3native.log` open beside it, or a Sim on a busy lot. No ReShade, no CC that is not credited. | **Rodney takes it** (we never drive the game window by hand). |
| 2 | Benchmark chart: baseline vs v0.3.1 for the three towns (sim steps per second and steps of 300 ms or longer), from the README table. | To make. `tools/mts-images.ts` draws the old v0.1.5 metrics (no fixes / v0.1.4 / v0.2.0); it needs a new metric layout before it can draw these (open question 4). |
| 3 | Features image: the short list "Switched on in this release". | To redraw with the v0.3.1 list (same tool, `mts-data.json` `features`). |
| 4 | Install diagram: `ts3fixes.package` to `Mods\Packages`, `ts3native.asi` + `.ini` to `Game\Bin`, Steam and EA app paths. | Exists for Steam only (`release/mts/images/3-install.png`); needs the EA app path. |
| 5 | Thumbnail (320x240 in the old kit; MTS crops the first screenshot otherwise). | Redraw with the v0.3.1 claim, for example "+13-28% sim steps per second". |

## Files to attach

Same bytes as the GitHub release v0.3.1 assets (checked 2026-11-22 with `gh release view v0.3.1`): `ts3fixes.package` (3,190,444 bytes, sha256 starts `0662ff4a`), `ts3native-v0.3.1.zip` (Steam, 918,488 bytes), `ts3native-ea169-v0.3.1.zip` (EA app, 930,952 bytes).

Proposed MTS archive(s), built from those assets, nothing else inside (no ASI loader, no test packages, no `ts3agent`):

- `Sims3Modernization_v0.3.1_Steam.zip`: `Mods/Packages/ts3fixes.package`, `Game/Bin/ts3native.asi`, `Game/Bin/ts3native.ini` (Steam build), `README.txt`.
- `Sims3Modernization_v0.3.1_EA-app.zip`: the same with the EA app `ts3native.asi` and `ts3native.ini`.

`tools/mts-zip.ts` builds the Steam zip today. It does not know the EA app build yet, and its `README.txt` template (`release/mts/README.txt`) still says the EA app is not supported; both need an update before the post (open question 2).

## MTS rules, rechecked 2026-11-22

Read logged out from the public rubric pages (https://modthesims.info/rubrics/ and the pages named), no login, nothing posted. The rubric pages showed their TS4 view by default; the TS3 view may word things differently.

| Rule | What it says now | What it means for us |
|---|---|---|
| Description, AI usage (rubric 3884) | AI-assisted mods are allowed **with full disclosure of the extent of AI use**; not disclosing can get the mod removed. Mods that change the game's code (`.asi` / `.exe`) must have **full source on GitHub**. At most 4 full-size images inline. | New since the 2026-10-21 research. The description has an AI disclosure section and the GitHub source link. Rodney words the extent (open question 1). |
| Usage of AI LLMs (4180) | Mods that talk to LLM providers are not allowed. Nothing about mods written with AI help. | Neither file has network code [verified]. |
| File type (3914) | TS3: `.package` and/or `.sims3pack`, zipped; `.exe` programs get an extra virus review. `.asi`, `.dll` and `.ini` are not named. | Same as before: allowed by precedent, not by a written rule. |
| Advertising (4169) | No links to your own site or social media; links to required content and credits are fine. GitHub is not named. | Only GitHub (source, as 3884 asks), the ASI loader and credited projects. No Discord or Patreon. |
| Informative screenshot (4166), size (3885) | Game mods need at least one **in-game** screenshot that shows the function. At least 800x600, at most 2592x1944 px. | The old kit had only info images; image 1 above is new and required. |
| Source code (4177) | Programs and Utilities need a GitHub source link. | We go to Game Mods, but link the source anyway. |
| Section | Game Mods > Miscellaneous (forum 638) and Game Mods > Script & Core Mods (637) both exist. The ASI precedent "Randomized Loading Screens (ASI mod)" is in Game Mods > Miscellaneous, Type "Other", and discloses AI use; Smooth Patch (`.asi` + `wininet.dll`) is in Programs and Utilities. | Keep Game Mods > Miscellaneous, Type Other. |

**Could not verify:** whether `.asi` / `.ini` files are allowed by a written rule (none lists them); the archive size limit and archive rules (rubric 4144 not read); whether one upload may carry two archives (Steam and EA app) or needs one archive with two folders; the TS3 wording of rubric 4180; the upload wizard's fields (it needs a login).

## Open questions for Rodney

1. **AI disclosure wording.** Rubric 3884 wants "the extent" of AI use. The draft says the code and docs are written with AI coding agents, directed, reviewed and tested by the maintainer and contributors. Name the tool (the ASI precedent says "Claude Code was utilized for parts of this mod"), or keep it generic?
2. **EA app download.** Two archives (Steam, EA app) or one archive with two `Game/Bin` folders? Either way `tools/mts-zip.ts` and `release/mts/README.txt` need an EA app update (~30 min of agent work).
3. **Known conflicts in public.** Name `EnhancedWitches.package` and `velocitygrass_AddAnyLotSize.package` on MTS, or only in the FAQ on GitHub? Both were seen only on our setup, with our mods off. Tell the authors first? velocitygrass's result (bisect of 2026-11-22) is not yet written into docs/314.
4. **Images.** `tools/mts-images.ts` needs a new metric layout for baseline vs v0.3.1 before it can draw the chart and thumbnail; and the required in-game screenshot is yours to take.
5. **Launch gate.** PLAN.md's checklist still lists visual proof (video or frame-time graph), independent testers ("tested by N players on M setups") and endorsements before promotion. Post with those open, or wait?
6. **Version.** Post v0.3.1, or wait for the next release? The text and the files must then move together.
