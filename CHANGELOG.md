# Changelog

Player-facing changes per release. Each `## vX.Y.Z` section becomes the GitHub release notes for that tag (`.github/workflows/build.yml`).

## Unreleased

### Changed

- **Car and boat trips plan their next leg off the main thread (`[RouteAsync] ForeignKinds=4`, on).** When a car or boat that carries Sims needs a new route, the route search now runs on a background thread instead of making the game wait for it, and the answer is the same route the game would have found. Measured in Isla Paradiso: 37.5 ms less route work per second on the main thread, with no new freezes, crashes or stuck Sims.
- **Townies you can't see choose what to do on a second thread (`[AutShadow] Unseen=1`, on).** Sims you don't play and can't see now get their next action from scores worked out on a second CPU thread, so the game's main thread does less: 6.4 ms less work per second in Bridgeport and 12.2 ms in Isla Paradiso, in 6 test runs per town, with no new crash or stuck Sim and no more freezes than without it. Sims in your household, the selected Sim and any Sim on screen or near your Sims still choose exactly as in the unmodded game. Steam only. To turn it off, set `Unseen=0` under `[AutShadow]` in `ts3native.ini`.
- **Less memory held by old outfit textures (`[CasTrim] Mode=3`, on).** The game keeps every outfit texture it ever built for a Sim until you quit, even for townies long gone from town. Now, right after each save, the textures the game's own save left out are let go: only ones no Sim in your household, no selected Sim and no other kept texture still needs, and none used in the last minute. Your saves do not change. In our tests: two long runs with saves, a reload and a vacation trip, no crash, no texture missing.
- **Crash detector for saves and travel (`[CasTrim] Detect=1`, on).** One crash during a travel save was seen once in four test runs and its cause is not known yet. At each save the plugin now checks the game's outfit list before the game reads it and writes what it finds to `ts3native.log`; it only logs and changes nothing. **If the game crashes during a save or while travelling, please send your `ts3native.log`** (see "Reporting a problem" in the player guide).

### Fixed

- **Crash after travelling/changing worlds with S3SS 1.6.3 LotStreamingOptimizations streamingSettings: ts3native now redirects S3SS's stale camera-threshold write; stands aside for other S3SS versions (`[S3SSCompat] StaleWriteGuard`, on).** Sims 3 Settings Setter 1.6.3 remembers where the camera speed threshold lives at the first world load and writes 5.0 there again at every later world load, after the game has freed and reused that memory. The plugin now sends that write to the current setting instead (S3SS's setting keeps working), only for the known S3SS 1.6.3 build; any other S3SS version is left alone.
- **Fixed a rare crash on loading screens (EA bug, `[MonoSigGuard]`, on).** When a loading screen stayed up longer than 2 seconds (travel, world load), the game read one value past the end of a small block of memory while starting the next loading picture, and crashed if that value happened to be garbage. Seen twice in test runs right after travel. The plugin now checks first, so the game gives the same answer it always did without the bad read.

- **Fixed a rare crash when the game frees a Sim's outfit (EA bug).** Seen once in a test run right after a vacation trip; an outfit kept a broken entry that the game then tried to free. The plugin now skips such an entry (a few bytes stay in memory) and writes a line to `ts3native.log`. If you see a `cell guard skipped` line there, please send us the log.

- **Small fixes for rare game errors (on).** A failed or cancelled age-up keeps the Sim's outfits. A career uniform backup no longer uncaches a new work outfit. Music buffs, buff updates, cars with no route, deferred age-ups, opportunity rewards and taxi, limo, bus and fire-truck drivers no longer throw game errors. Five of these are by Dalexlol (GitHub alexvixell35-stack): a Sim with no inventory yet refuses an object instead of erroring, a career skill that is not registered shows an empty name, a firework that fizzles ends cleanly, pets dropped from the adoption pools with no family are removed, and two Sims' relationship tables that came apart are joined again.

- **Routing fallback could mix up two Sims' plans** (PR #306, by Dalexlol): while one Sim's plan was paused, its result could be applied to another Sim's plan. Each plan now keeps its own result. Not tested in game yet.

## v0.2.10 (2026-11-07)

Hotfix on top of v0.2.9, Steam and the EA app 1.69.47. The stove and fridge bug below was ours, and this fix is Blastorix's (Discord): he reported it and his test logs found the cause.

**EA app:** `ts3native-ea169-v0.2.10.zip` (EA app 1.69.47): `ts3native.asi` and `ts3native.ini`; replace both. It has the same stove and fridge fix as the Steam build. Kitchens already blocked by v0.2.8 or v0.2.9 clear when you reload your save.

### Download

- `ts3native-v0.2.10.zip` (Steam): `ts3native.asi` and `ts3native.ini`; replace both.
- `ts3fixes.package`: the same file as v0.2.9. If you have that one, keep it.
- The log shows `ts3native 0.36.168e`.

### Fixed

- **Stoves and fridges that can no longer be used.** Since v0.2.8, an object removed or moved while a background route was running could leave an invisible blocker behind where it stood. Your Sims, and every other Sim, could then keep failing to reach the stove, oven or fridge next to it. Blockers no longer stay behind. Report and fix: Blastorix (Discord).
- **Sims walking off right after a step at the stove.** Right after finishing a step at the stove, a Sim could fail one route and walk off somewhere else, even outside, then come back. Routes now wait until the game has finished removing what that step left behind. Reported by Blastorix (Discord).
- **Kitchens that are already blocked.** Blockers left by v0.2.8 or v0.2.9 clear when you reload your save. If a stove or fridge is still blocked after a reload, please report it with your `ts3native.log`.

### Tested

- The stove, oven, fridge repro ran on the development build with the same fix: with the fix off, 29 of 40 tries left a blocker and the route failed; with it on, 0 of 40.
- The walk-off repro ran on a test build with the same fix: with the fix off, 14 of 14 tries failed; with it on, 0 of 16.
- Smoke test of this exact build: Bridgeport world, 5 minutes, no crash, no hang.
- Repeat test on this build: 50 stove cycles in a row, 47 reached the oven, no crash, no blocker left behind.
- `selftest-all`: 191 passed, 5 skipped (they need game files).

## v0.2.9 (2026-11-07)

Hotfix on top of v0.2.8, Steam and the EA app 1.69.47: same fixes, same switches.

### Download

- `ts3fixes.package`: new in this release, the same file for Steam and the EA app. Replace it together with the files below: the new routing behaviour needs both.
- `ts3native-v0.2.9.zip` (Steam): `ts3native.asi` and `ts3native.ini`; replace both.
- `ts3native-ea169-v0.2.9.zip` (EA app 1.69.47): `ts3native.asi` and `ts3native.ini`; replace both.
- Both logs show `ts3native 0.36.168c`; the EA log also shows `release: v0.2.9 EA`.

### Fixed

- **Your Sims saying they can't get there on their own lot.** To stop long routes from freezing the game, v0.2.8 gave up on a route after a fraction of a second, and that also hit the Sims you are playing or looking at. Go Here, the toilet, the fridge or leaving for work could fail on a path that exists. Sims in your household, their pets and their cars, and Sims you can see, now get all the time they need. Sims you can't see keep the short limit, so a busy town stays smooth.
- **Your Sims being reset after repeated route failures.** The game's route watchdog no longer resets anyone in your household.
- **Off-screen household Sims skipping animation updates.** They always animate at the full rate now.
- **Off-screen household animation fix now works in the default setup.** It no longer needs an extra setting to be switched on.
- **Crash after the game ran out of memory on a long play session.** The game's script memory could keep growing while large free blocks sat unused, until nothing was left. It now reuses those blocks first.

### Changed

- **A very long route by a Sim you can see can pause the game for a moment.** That is the price of the fix above, and it is how the unmodded game behaves. A route that runs for 10 seconds is stopped, so a broken route can't hang the game. Both limits are in `ts3native.ini`: `KdExemptSeen` and `KdSeenCeilMs`.
- **Better logs for route problems.** `ts3native.log` now says which route failed and why, so a bug report needs only the log. The lines stay small and limited in number.

### Tested

- Steam smoke test: 3 of 3 passed (Base Game, Island Paradise and Sunset Valley worlds, about 7 minutes each, no crash; log shows `ts3native 0.36.168c`).
- The household route-cut and animation-hold proofs ran on the development build with the same fixes, in repro runs: 0 route cuts on household or visible Sims with the fix on, and the held-animation table works.
- EA app smoke test: 3 of 3 passed, no crash. No timing numbers for the EA app.
- `selftest-all`: 196 passed.

### Known issues

- A stove placed behind a wall can leave a Sim with no path to it. One player report; the cause is not found yet, we are investigating.

## v0.2.8 (2026-11-06)

One release for Steam and the EA app 1.69.47: same source, same fixes, same switches. It is bigger than a hotfix: besides the crash fixes it turns on newer routing changes on both versions. They were tested for stability, not for speed. Thanks to Dalexlol (GitHub `alexvixell35-stack`) for PRs #212 and #271 and the other fixes imported in #272.

### Download

- `ts3fixes.package`: unchanged from v0.2.6, the same file for Steam and the EA app (sha256 starts `302c33cf`).
- `ts3native-v0.2.8.zip` (Steam): `ts3native.asi` and `ts3native.ini`; replace both.
- `ts3native-ea169-v0.2.8.zip` (EA app 1.69.47): `ts3native.asi` and `ts3native.ini`; replace both.
- Both logs show `ts3native 0.36.168b`; the EA log also shows `release: v0.2.8 EA`.

### Fixed

- **Error 12 on every save of an Isla Paradiso world.** A lot with no world grid point at its centre (a lot made in Edit Town, or a save started on another version of the world) never finished its routing setup, and that made every save of the world fail. Such a lot now finishes its setup the way the game does for a lot with nothing to connect. Thanks to Dalexlol.
- **Sim thumbnail render reading past the end of a pose.** When a Sim's thumbnail was drawn with a pose that had fewer bones than the Sim, the game read memory past the pose. That pose is now skipped, as the game already does for a model with no pose. This is aimed at one crash seen in old saves; that it stops that crash is not proven yet. Thanks to Dalexlol.
- **Sim thumbnail render reading a released pose, and custom-content meshes that name a bone that doesn't exist.** Two more fixes for crashes in the game's own code. Thanks to Dalexlol.
- **Crash when a Sim's texture can't be created.** The game used a texture that was never made. That Sim's combined texture is now skipped instead of crashing.

### Changed

- **Newer routing changes are on.** Many of the game's checks, such as steering, footsteps, doors and placing objects, now read a safe copy of the routing data instead of waiting on route work.
- **Threads the game starts itself reserve less address space.**

### Tested

- Steam: tested 11-06 on one machine: 10 minute runs on Isla Paradiso (twice), Bridgeport and Sunset Valley, and three saves on Isla Paradiso. No crash, no hang.
- EA app: tested 11-06 on the EA app 1.69.47, on one machine with an Intel integrated GPU and a small mod set: the texture crash repro with the fix off and on, a 10 minute run with a save and a clean quit, and 10 minutes with a patched Apex Radiance 2.17.1.

### Known

- This release was checked for crashes, not timed. Hitches were not measured.
- The texture crash fix was run against its crash repro on the EA app only.
- When a Sim's texture can't be created, how that Sim looks hasn't been checked.
- Script fixes that came after v0.2.6 are not in this release, because `ts3fixes.package` is unchanged.
- EA: tested on one machine with a small mod set, not a big custom content folder. Not tested with the public Apex Radiance 2.18.x.
- EA: each session may log one harmless handled error in EA launcher memory (+0x67CC2).
- EA: with Apex Radiance the log shows handled errors inside Apex. Apex confirmed one and plans a fix.
- EA: the log can show two handled errors at exit. The game quits normally.
- EA: one change for Sims you can't see, cheaper walking steps, stays off until it has been run on the EA version.

## v0.2.7 (2026-11-05)

The **EA app 1.69.47** version is now available. The Steam files are unchanged from v0.2.6.

### Download

- `ts3fixes.package`: unchanged from v0.2.6.
- `ts3native-v0.2.7.zip` (Steam): `ts3native.asi` and `ts3native.ini`, unchanged from v0.2.6.
- `ts3native-ea169-v0.2.7.zip` (EA app 1.69.47): `ts3native.asi` and `ts3native.ini`; replace both. Install steps: docs/PLAYERS.md, section "EA app version (1.69.47)".

### EA app build

- Made from the ea169-port branch at 703cd87, tested 11-03 on one machine, not retested with the v0.2.6 `ts3fixes.package`.
- Tested: 18 of 18 runs passed on the EA app 1.69.47 (8 start-up checks + 10 full play-test jobs), plus a 10-job vanilla harness baseline.
- Known issues (EA): MemDiag self-test fails (Light off); FootprintIndex 4 unmatched (off); WorldImage off (unmapped); benign handled AV in EA launcher memory (+0x67CC2); tested on one machine (Intel Xe iGPU, slim Mods), heavy CC only on Steam; ImageGuard off.

## v0.2.6 (2026-11-05)

Hotfix: **the game froze and crashed on the fifth Power Cell** (Into the Future), and objects were destroyed without their own cleanup (v0.2.0 to v0.2.5, everyone). This release is a static fix: it comes from reading the game's code and has not been run in the game yet. Thanks to u/IntelligentMight1124, whose report and logs found it.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native.asi` and `ts3native.ini` are unchanged from v0.2.5; you only need the zip if you don't have v0.2.5 yet.

### Fixed

- **No more freeze and crash on the fifth Power Cell.** A hook of ours, which only collected memory statistics, turned off a step of the game's object cleanup. The game's Power Cell loop then never ended, used up memory and crashed. The hook is gone.
- **Objects get their full cleanup again.** The same hook affected every object the game destroys: an object's own cleanup was skipped, such as leaving an inventory, destroying what it holds, or a Sim's own cleanup. This can have caused leftover references in saves played on v0.2.0 to v0.2.5. Tools like NRaas ErrorTrap may report and clean up some of these once.
- **Three smaller hooks removed for the same reason**, each of which skipped the game's special versions of a method:
  - Rabbit hole doors: Into the Future buildings, hideouts and a few others now animate their own doors again. This takes back the v0.2.5 fix for a door that can't open (issue #167) until it is rebuilt, so a Sim can again fail to go in on such a door.
  - Service Sims: the Grim Reaper, the time traveler and resort staff are picked by their own rules again.
  - Returning from a trip: saved progress of opportunity counters is restored in full again.

## v0.2.5 (2026-11-05)

The speed changes in this release are not timed as a set. What was checked on the release build: a back-to-back run on Bridgeport against v0.2.4 showed no regression (+1.9% iterations per second, inside the 4.4% spread between repeat runs; the machine was not quiet enough for this to count as formal timing evidence), and short smoke runs on Sunset Valley and Isla Paradiso passed.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native-v0.2.5.zip` (Steam) or `ts3native-ea169-v0.2.5.zip` (EA app): `ts3native.asi` and `ts3native.ini`; replace both.

### Faster

- **Sims you can't see cost less to animate.** For a Sim the camera cannot see, the game now updates its body pose every few frames instead of every frame, with the skipped time added in, so nothing is lost. Sims you can see, Sims near them and Sims near your household always update every frame. On by default (`[UnseenAnim] Enabled=1`, `CapPasses=4`; objects stay as before).
- **Cheaper held-action check** (PR #245, by Dalexlol): the check for held actions walked every object in the world every few ticks; it now walks only the list of Sims, which the reporter measured at about 5% of the main thread on a 14,500-object save (issue #156; their number, not ours). On by default.
- **One "can the player see it?" answer per object per frame.** Several of our speed-ups ask whether you can see a Sim or object; they now share one answer per frame instead of each asking the game again. On by default (`[UnseenTable] Enabled=1`). The speed-up above builds on it.

### Fixed

- **Rare memory runaway at game start.** The game's script memory manager avoids placing big blocks on memory pages that look in use, and how picky it is grows with the heap size. With our larger start heap it could refuse one big start-up file again and again and keep asking Windows for more memory instead of cleaning up, until the 32-bit game ran out of address space and crashed (seen on the EA app 1.69 version in about 4 of 24 launches; never seen on Steam, but the same code is there). Now it is lenient at start-up, at the main menu and on every loading screen, and goes back to the game's own rule once your world is loaded, so long play sessions are unchanged. `[GC] BlackListCapKB=1024` in `ts3native.ini` (0 = off). Tested on Steam: start-ups, 6 save/reload cycles with and without it, travel and normal play, all passed. Found and fixed on the EA app port (PR #238).
- **No more crash from custom-content meshes without a material** (issue #80): a hair or clothing mesh made from a morph mesh crashed the game while it built a Sim. That part is now left out at that distance instead. On by default (`[EaCrashFixes] GeomNoMaterial=1`).
- **Isla Paradiso routing crash** (issues #132, #136): on by default (`[EaCrashFixes] WorldAStarRefs=1`, `PortalFlagRefs=1`).

- **Paired socials no longer fail with "cannot go there"** (Show Off Badges and other two-Sim socials that set up a spot first): with async routing the partner Sim could plan its walk before the spot existed. Plans now stay synchronous while that spot is being set up (switch `asyncjigsync`, on by default). On v0.2.4, set `Enabled=0` under `[RouteAsync]` in `ts3native.ini` as a workaround.
- **Rabbit hole doors no longer block Sims** (issue #167): a rabbit hole door the animation can't open no longer stops the Sim from going in. Found by Dalexlol. On by default (switch `rabbitholedoor`).
- **Saves with a removed service no longer break services** (issue #164): when a save holds a service whose mod or pack is gone, the maid, mail carrier, repairman and other services kept failing. The broken entry is now skipped. Found by Dalexlol. On by default (switch `nullservice`).

### Test features (off, opt-in)

- **Walking Sims re-check (test feature, off).** The game re-checks every animated object several times per frame for walking Sims; with `Enabled=1` under `[UnseenRootMotion]` in `ts3native.ini` (`Mode=1`, `Shortlist=1` are preset) it re-checks only the ones that are actually walking, same calls in the same order. Off in this release until we have a clean measurement.
- **Smaller save files (SaveSlim, test feature, keep backups).** Every time a Sim leaves a lot, the game stores a new copy of that Sim's outfits in your save and never throws the old copy away, so long-played saves keep growing. With `Enabled=1` under `[SaveSlim]` in `ts3native.ini` (needs `[SaveWrite] Enabled=1`), after each normal save a background job rewrites the save's outfit store without the wasted space and, in `Mode=2` (the default), without outfit copies that nothing in the save uses any more. It checks the new file byte for byte before it replaces the old one; if anything goes wrong, your save stays exactly as the game wrote it. The file from before each rewrite is kept in `Documents\Electronic Arts\The Sims 3\SaveSlimBackup\<save name>.preslim\` (only the newest one), so an outfit that was dropped by mistake can be restored. `Mode=0` only measures and writes nothing. Tested: 12 of 12 runs passed over 4 saves (Bridgeport, Bridgeport with lots of custom content, a 2+ year NRaas save, Moonlight Falls), in Mode 0, Mode 2 and save-then-reload runs. By Dalexlol (PR #122). It changes your save files: copy your saves before you try it.

### Known issues

- **Tiny UI Fix is not supported yet in this release:** with it installed, the Mono speed-up (AutShadow) may switch itself off. The game still works; you just don't get that speed-up. Found by Dalexlol.

## v0.2.4 (2026-10-29)

Hotfix: **the Play arrow in the family picker grayed out and the family never loaded** (v0.2.0 to v0.2.3, for anyone who starts a family from the picker).

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native-v0.2.4.zip`: the same `ts3native.asi` and `ts3native.ini` as v0.2.0; you only need it if you don't have v0.2.x yet.

### Fixed

- **Play loads your family again.** The game only runs Play when no other interface work is pending, and it counted our mod's background tasks as pending interface work, so Play waited forever. Our tasks no longer count. The same applies to Play after importing a family or returning from university, and to placing a moved lot in Edit Town.
- If Play ever does nothing for 10 seconds, the mod starts the family itself and turns the buttons back on (safety net).
- `ts3native.log` names what holds Play back, if anything ever does again (`play:` lines).

## v0.2.3 (2026-10-29)

Diagnostics hotfix: better problem reports for starting a family. No gameplay changes from v0.2.2.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native-v0.2.3.zip`: the same `ts3native.asi` and `ts3native.ini` as v0.2.0; you only need it if you don't have v0.2.x yet.

### What's new

- **`ts3native.log` now records what you do and when the family starts:** each switch between the family picker, live mode, Build, Buy, Create a Sim and Edit Town, the active family, and, when you press Play, whether the family started (checked every 5 seconds for a minute, naming any Sim that is still busy). If a family ever fails to load, send us that file and we can see exactly where it stopped.
- Logging only: a few short lines per mode change, nothing per frame.

## v0.2.2 (2026-10-29)

Hotfix: **families could not be loaded** with v0.2.0 / v0.2.1 on some setups (the Play arrow grays out and nothing happens).

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native-v0.2.2.zip`: the same `ts3native.asi` and `ts3native.ini` as v0.2.0; you only need it if you don't have v0.2.0 yet.

### Fixed

- **Loading a family works again.** Our off-screen hitch guard could keep a Sim waiting while the game was paused (for example in the family picker), and the game then waited for that Sim forever. The guard now notices right away when the game is paused or the family is being started.
- Includes the v0.2.1 fix for simler90's nightly jobs.

## v0.2.1 (2026-10-29)

Hotfix for players with **simler90's Gameplay Systems Core Mod**.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one.
- `ts3native-v0.2.1.zip`: the same `ts3native.asi` and `ts3native.ini` as v0.2.0; you only need it if you don't have v0.2.0 yet.

### Fixed

- **simler90's nightly jobs run again.** simler90's core mod adds its own jobs to the game's 3 AM cleanup (aging up homeless pets and young townies, restoring missing household members, turning off noisy electronics, sending young Sims home). v0.2.0 split that cleanup into small steps but skipped simler90's jobs. v0.2.1 runs them again, inside the split-up cleanup. Tested with simler90 UPDATE242.
- Nothing changes for players without simler90.

## v0.2.0 (2026-10-28)

Routing release: fewer of the long "everyone stops" freezes when Sims plan their walks. Technical notes: [v0.2.0 developer notes](https://github.com/rodneypantonial/sims3-modernization/blob/v0.2.0/docs/release-notes/v0.2.0-dev.md).

### Download

- `ts3native-v0.2.0.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` and replace the old ones. You still need an ASI loader, as before (see "How to install" below).
- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one. Both files must come from this release.

### What's new

- **Time limits on route planning.** A Sim looking for a path that does not exist no longer holds up the whole game for seconds: plans that run too long end early with the game's own "can't get there" answer.
- **Hitch guard for Sims you can't see.** When an off-screen Sim's route goes wrong, the game no longer freezes waiting for it; the Sim gets a short cut or is moved along.
- **Nightly chores in small steps.** The game's 3 AM venue cleanup, the pet adoption pool and midnight relationship decay now run over many frames instead of one frozen frame. NRaas Overwatch's nightly stuck-Sim check is split up too.
- **Crash fix.** A rare crash where the game's script engine cleared more memory than it should.

### Results (our test saves, autopilot play, 10-15 minute loads)

Cells: no fixes -> v0.1.5 -> **v0.2.0**.

| | Bridgeport | Sunset Valley | Isla Paradiso |
|---|---|---|---|
| freezes of 300 ms or longer, per 10 minutes | 4.8 -> 5.0 -> **0.0** | 13.7 -> 4.3 -> **5.8** | 93.9 -> 20.0 -> **13.1** |
| sim iterations per second | 59.3 -> 56.9 -> n/a | 91.3 -> 73.2 -> **87.7** | 63.2 -> 65.8 -> **73.0** |
| p99 sim step | 79 -> 79 -> **84 ms** | 59 -> 67 -> **57 ms** | 168 -> 105 -> **107 ms** |

v0.2.0 numbers are the mean of several loads (Isla Paradiso 9, Sunset Valley 4, Bridgeport 1); v0.1.5 and no-fixes are one load each, so small differences are within noise. The Bridgeport v0.2.0 load ran with our profiler's samplers on, which slow the game, so its speed is left out.

### Good to know

- Your saves are not changed by installing or removing this release. As always, back up your saves first.
- Upgrading: replace both files; use the new `ts3native.ini` (the new features are switched on there).
- The guard's part for Build/Buy edge cases ships switched off and comes in v0.2.1.

## v0.1.6 (2026-10-25)

Hotfix: `ts3native.log` now tells us what we need to help you. No gameplay changes: same fixes, same settings as v0.1.5.

**You don't need to change any settings.** Install the two files below and play. Technical notes: [v0.1.6 developer notes](https://github.com/rodneypantonial/sims3-modernization/blob/v0.1.6/docs/release-notes/v0.1.6-dev.md).

### Download

- `ts3native-v0.1.6.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` and replace the old ones. You still need an ASI loader, as before (see "How to install" below).
- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one. Both files must come from this release.

### What's new

- **Better problem reports.** If something goes wrong (a save that never finishes, a freeze, a slowdown), close the game and send us `Game\Bin\ts3native.log` (GitHub issue or Discord) before you start the game again: every start writes a new one. The log now shows:
  - which version you run and how the game is installed, which other plugins sit next to the game (for example DXVK or other ASI mods), your graphics card, and how many mods and script mods you have (file names only);
  - how big your world is when it loads (lots, households, Sims, objects) and which of our settings are on;
  - for every save, each step of the save as it happens, what it waits for, and a line every 5 seconds while a save is still running. A save that gets stuck now shows where.
- **Privacy.** The log holds file names, never the folders they sit in, your Windows user name or your PC's name. Nothing is sent anywhere: you decide whether to send the file. Open it in Notepad to check before you send it.
- **Logging only.** It writes a few lines at start, when a world loads and when you save; nothing per frame. To switch it off, set `Enabled=0` under `[UserDiag]` in `ts3native.ini`.
- **Memory line (on).** Every 5 minutes of play the log gets one short line of memory numbers (`[MemDiag] Light=1`), so an Error 12 report already shows how the memory filled up. Logging only.
- **Error 12 report (test feature, off).** If you get Error 12 often, set `AllocSites=1` under `[MemDiag]` in `ts3native.ini`, play until it happens, and send us the log with `node report-crash.ts --memory` (it removes paths, names and emails and replaces your package file names with ids unless you add `--names`, shows you everything and asks first). It shows what filled the memory and whether our mod had a part in it. Its cost is still being measured: switch it off again after you sent the report. Off by itself when Apex Radiance is installed. See "Error 12 report" in the player notes.

### Good to know

- Your saves are not changed by installing or removing this release. As always, back up your saves first.
- Upgrading from v0.1.5: replace both files. The new `ts3native.ini` has the same settings as v0.1.5 plus the `[UserDiag]` section; a v0.1.5 `ts3native.ini` also works (the new log lines are then on by default).

### Tested

On the final build, because the new log lines sit on the save path: saving in many situations and reloading the saves in Bridgeport and Isla Paradiso (10 of 10 save tests passed; one earlier Isla Paradiso run failed its reload check on a game script error in a wedding clean-up that our mods do not touch, and the rerun passed); one save in an Isla Paradiso game 13 days in (passed); a trip to France and back with saves on the way (passed); saving twice, reloading and saving again with Apex Radiance v2.15.5 installed (passed, all saves finished in about 8 seconds); short play in Isla Paradiso (passed); the new log lines checked for content and privacy (passed). 8 of 9 test runs passed, no crashes.

Not tested: autosaves with NRaas Saver. Our test could not make Saver autosave during the run, so this case says nothing either way; the fix of the test is in progress. v0.1.6 changes nothing in how saving works, only what the log writes about it.

## v0.1.5 (2026-10-24)

Fewer freezes in the situations that caused the worst ones: Sims getting off boats, crowds, the game's 3 AM stuck-Sim check, NRaas Overwatch's nightly clean-up and NRaas ErrorTrap right after loading. Plus a crash fix. In everyday play it feels about the same as v0.1.4, which already had far fewer hitches than the unmodded game (in Isla Paradiso about 20 instead of about 94 freezes of a third of a second or longer per 10 minutes).

**You don't need to change any settings.** Install the two files below and play. The `ts3native.ini` file comes set up; its settings are for testers and developers only (technical notes: [v0.1.5 developer notes](https://github.com/rodneypantonial/sims3-modernization/blob/HEAD/docs/release-notes/v0.1.5-dev.md)).

### Download

- `ts3native-v0.1.5.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` and replace the old ones. **Use the new `ts3native.ini`**: the old one keeps the new fixes off. You still need an ASI loader, as before (see "How to install" below).
- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` and replace the old one. Both files must come from this release.

### What's better

- **No more 8-second freeze when Sims get off boats in Isla Paradiso.** When a Sim or an object moved while the game was planning a route in the background, the game sometimes stood still until the plan was done. In our boat test that was one freeze of 8.3 seconds; now there is none.
- **Fewer short freezes in crowds.** When many Sims look around them while a route is being planned, the game waits less.
- **The 3 AM stuck-Sim check no longer freezes the game.** Every night the game checks which Sims are stuck and plans new routes for them, all at once. That now happens one Sim at a time. In our test the worst freeze went from 1.2 seconds to under 0.15 seconds.
- **NRaas Overwatch: the nightly clean-up freeze is gone.** If you use Overwatch, its nightly clean-up now runs its jobs one after another instead of all in the same moment. In our test the worst freeze went from 0.3 seconds to 0.1 seconds.
- **NRaas ErrorTrap: no hitch right after loading a save.** If you use ErrorTrap, the check it runs a moment after every load is spread out (worst freeze 0.28 seconds down to 0.06 in our test).

### Fixed

- **A rare crash near water.** With background route planning on, the game could crash when it looked for a spot on the shore (for example a Sim getting off a boat in Isla Paradiso) at the same moment a route was being planned. Fixed and checked in two long test runs without a crash.

### Good to know

- The game does slightly fewer simulation steps per second than without mods, the same as v0.1.4. You won't see this: the clock runs at the speed you pick, and what you see is fewer and shorter hitches.
- Your saves are not changed by installing or removing this release. As always, back up your saves first.

### Tested

On the final build: play in Bridgeport, Isla Paradiso and Sunset Valley; saving in many situations and reloading the saves; a trip to France and back; each new fix forced on in its test scenario; all fixes off versus on in the same scenarios; water and boat scenarios; and two 45-minute play sessions. 27 of 27 tests passed, no crashes.

Still to do: in Sunset Valley the game can still freeze for a second or more now and then (in our 45-minute test: 4 times, the longest 3.6 seconds). v0.1.4 does the same; it comes from route plans the game still makes on its main loop for one Sim on behalf of another. That is the main target of the next release.

## v0.1.4 (2026-10-20)

Background route planning: Sims plan their walks and drives on a second CPU core while the game keeps running.

### Download

- `ts3native-v0.1.4.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` (replace the v0.1.3 files; **use the new `ts3native.ini`**, the old one keeps background routing off). Needs an ASI loader, as before. Built as ts3native 0.36.0.
- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` (replace the v0.1.3 file). Background routing needs this package: it is what hands a Sim's route plan to `ts3native`. Its settings are the same as v0.1.3 (no default changed).

### New in `ts3native` (on by default)

- **Route planning in the background (`[RouteAsync]`, with `[RouteGate]` `Sites=1` `Enforce=1` and `[Discourage]`).** When a Sim plans a route from its own task, the game's own planner now runs on a worker thread while the simulation goes on; the Sim waits tick by tick for its answer. Routing locks keep the main thread off the routing data while a plan runs (`[RouteGate]`), and discourage-area changes made during a plan are applied in order right after it (`[Discourage]`, design D with the D-X destroy and dead-object fixes). Measured on Bridgeport, same build, seeded 10-minute runs, async on vs off: simulation time spent on routing **119 -> 50 ms/s**, **+10-12% simulation iterations/s**, **-17% hitches**; the worst hitches (p99) unchanged within noise (PLAN.md "Async routing (H6) state"; runs: `docs/data/results/2026-10-16-queue-h6-m1.md`, `docs/data/results/2026-10-17-queue-h6-m1.md`; realistic test environment env-off/env-on pairs for Bridgeport and Isla Paradiso: `docs/data/results/2026-10-17-queue-clean-night.md`, build 50bb1d4, jobs `bp-env-off`, `bp-env-on`, `ip-env-on`, `ip-env-off`, all PASS). Replans (`[RouteAsync] Kinds`) stay on the main thread. To turn it off: `[RouteAsync] Enabled=0`.
- **Already on since earlier builds, now listed:** `[LotRemoveGuard]` (flushes a lot's deferred footprint removals before the game walks them; prevents a crash at `0x63576A`) and `[OptsInit]` (the route planner's search options start at 0 instead of stale stack values; fixes an uninitialised read in the game).

### Shipped but off (switches for testers)

- **Routing snapshot (`[RouteSnap]`, P3/P4 copy, P5 / P5b / P5c answers).** The code is in the build; `ts3native.ini` keeps `Enabled=0`, `Mode=0`, `Lots=none`. It is not part of the measured gains above and has not passed the release checks; turn it on only to help test, with backups.
- Every other experimental section stays off as in v0.1.3 and the list below: `[ThreadStack]`, `[AutShadow]`, `[MonoThread]`, `[WorldImage]`, `[SteerRO]`, `[SlotEnabledRO]`, `[LotFrontDoorRO]`, `[ObstructedRO]`, `[RouteCache]`, `[RouteWorker]`, `[RouteBudget]`, `[Alloc]` and the rest.

### Tests (release validation, `tools/harness/queues/release-validate.json`)

Results: `docs/data/results/2026-10-20-queue-release-validate.md`. All jobs on the release build (sha256 in `docs/RELEASE.md`) with the release `ts3native.ini`, no per-job overrides; the save matrix runs the fixes as shipped (`--fixes shipped`).

| Group | Jobs | Cases | Result |
|---|---|---|---|
| Play, crash-free (12 min seeded) | 3 | Bridgeport, Isla Paradiso, Sunset Valley | 3/3 PASS (BP 16,608 background plans, 0 watchdogs, 0 stuck Sims) |
| Saves | 3 | Bridgeport, Sunset Valley, Isla Paradiso: 15 saves each with background routing on | 45/45 saves written (result 0); 3/3 reloads PASS (one save per world made unpaused with background routing on, loaded and played 3 min: `docs/data/results/2026-10-20-queue-release-reload.md`). No save code changed since v0.1.3, so the full save matrix was not rerun |
| Travel round trips + travel save | 6 | France, China, Egypt, University, Future (queued and direct start) | 4/6 PASS; Future: both trips arrived without a crash, the harness could not queue the trip home (known harness gap, not a game fault) |
| Rare routing scenarios (docs/117) | 21 | winter pond, thunderstorm, full moon werewolf, route bits, ghost walk, occult walks, death walk, big crowd, IP water, world walk (University, France), owned vehicles, stroller, horse ride, zombies, broom flight | 20/21 PASS, 0 crashes; stroller: the harness could not create a stroller object (setup step, not a game fault) |

33 jobs run: 27 PASS, 0 crashes. Failures are harness-side only (Future trip home x2, stroller setup) plus the 3 earlier save-matrix jobs that could not switch experimental save fixes (replaced by the saves row).

### Recommended setting (optional)

- **NRaas Overwatch: turn off "Clean up Inventory".** Once per game day Overwatch's nightly cleanup looks up the name of every inventory item and the game freezes for about half a second (docs/96). In game: City Hall > NRaas > Overwatch > Settings > Clean up Inventory = off. For new saves the installer entry `overwatch-no-invcleanup` sets it. Trade-off: broken or orphaned inventory items are no longer removed. Not yet measured; see queue job `bp-overwatch-cleanup-ab`.

### New in `ts3fixes` (experimental, off by default)

- **SafeSpot placement reuse (`kSafeSpotReuse`, switch `safespotreuse`).** A townie placed by a trip shortcut at a spot already verified on that lot no longer runs another native placement search (`FindGoodLocation`, up to five per child at the Isla Paradiso school run); it gets the spot and facing stored from the first search while the spot's cached routability verdict holds. Experimental: off in the tuning; not tested in a live game yet and no gain is claimed (queue job `ip-safespot-ab`).
- **Spring wildflowers without the hitch (`kWildflowerTask`, switch `wildflowertask`).** With Seasons, each spring the game grows wildflowers on up to ~32 lots, one lot per sim hour, and tries up to 20 spots per lot inside one alarm call: 0.3-1 s freezes (docs/110). With this on the tries run one per sim tick in their own task: same lots, flowers, try limit and outcome; a flower may appear a fraction of a second later. A save while a try is pending drops that one flower. A measure mode (`wildflowerprobe`) logs tries and times per fire without changing anything. Experimental: off in the tuning; not tested in a live game yet and no gain is claimed (queue `wildflower.json`).
- **Door lock table for background routing (`kDoorMirror`, switch `doormirror`).** Groundwork for answering door locks without asking the game's scripts during a route search (docs/105 §10.5): ts3fixes keeps a per-lot table of every door's lock settings and marks a lot out of date when a door's lock is imported (placing a lot from the library or bin). At each route plan, the start and goal lots' tables are sent to ts3native's routing snapshot when its copy is out of date. ts3native does not use them in route searches yet, so this changes nothing in play. Off by default.

### New in `ts3native` (experimental, off by default)

- **Routing snapshot shadow, part A (`[RouteSnap] Mode=1`, test builds only).** Step P4 of the planned routing snapshot (docs/94 §18): after a background route job finishes, the route thread can plan the same request again on a private copy of the routing data and compare the two answers; the game's own answer is always the one used. Since 0.29.0 the copy is the routing snapshot lot copy below (`Lots=replay`), brought to the moment each background plan starts (docs/94 §21); since the private pools build below the copy no longer shares the game's memory pools. Off in `ts3native.ini` (`Mode=0`).
- **Private memory pools for the routing snapshot (`[RouteSnap] Lots=replay`, test builds only).** The game's routing quadtree and map memory pools have no lock, and many game paths use them outside the routing lock (docs/94 §16.6). The routing snapshot lot copy and the shadow plans on it now take their quadtree, map, A\*, goal and cell nodes from pools of their own, so they can no longer corrupt the game's pools or keep using memory the game frees when it resets its route planner (docs/94 §16.7). The same build fixes the order of lots in the copy's lot table after a lot is re-copied. Earlier `Lots=replay` and `Mode=1` test builds should not be run any more. Since 0.31.5 a shadow plan that frees a node of the game's pools, or a thread that frees a node of the copy's A\* pools without owning it, leaks that node instead of returning it to a pool another thread uses at the same moment (docs/67 §8). Off in `ts3native.ini`.
- **Background route plans that check line of sight stay on the main simulation thread (`[RouteAsync]`).** Plans that ask for physics-based line of sight change which floor of a lot is shown while they run; with background routing on, that could leave the wrong floor showing after a floor change. Those plans are no longer moved to the background (docs/94 §17.9).
- **Routing snapshot lot copy (`[RouteSnap] Lots=replay`, test builds only).** 0.29.0: no longer fails on one kind of object footprint (the copy used to be rebuilt and fail every quarter second), leaves a lot out instead of giving up when it meets an unknown footprint kind, and copies the snapshot's terrain cost maps in larger pieces again (cheaper). Step P3-B of the planned routing snapshot (docs/94 §16): keeps a private copy of every lot's routing footprints and quadtrees up to date by replaying the game's own changes in order on a background thread, and checks it against the game's data; optional `Prebuild=1` builds every lot's routing graph on the loading screen. Nothing reads the copy yet and it is off in `ts3native.ini`. The same build fixes three findings of the world-image test: the tuning value set by the game during play, a full key table that made Isla Paradiso's cost-map updates expensive, and smaller cost-map tiles.
- **Routing snapshot world images (`[RouteSnap] Enabled`, test builds only).** Step P3 of the planned routing snapshot (docs/94 §14): keeps an up-to-date copy of the game's world-level routing data (waypoint graph, water and cost grids, footprint grid, terrain mask, tuning) and checks it against the game's own data. Nothing reads the copy yet, so play does not change. Off in `ts3native.ini` (`Enabled=0`).
- **Routing change counters (`[RouteSnap] Count`, test builds only).** A measurement switch for the planned routing snapshot (docs/94 phase P1): it counts every write to the game's routing data and what caused it, and changes nothing in play. Off in `ts3native.ini` (`Count=0`); only test runs turn it on. The same switch now also names the remaining causes (furniture placement, platforms, trees, terrain lookups, deferred updates) and measures how many changes per second the planned snapshot would have to copy and what that would cost (docs/94 §15).
- **Read-only steering (`[SteerRO]`).** While a background route job holds the routing lock, walking Sims can check their steering against a read-only copy instead of waiting for the job. Experimental: switched off in `ts3native.ini` (`Enabled=0`); no speed gain is claimed yet, and the first live test was correct but slower.
- **Background replans (`[RouteAsync] Kinds`).** Sims' route replans (Replan, PlanFromPoint, ReplanFromPoint) can be planned on the background route thread like their first plans, while the game keeps running. Experimental: switched off in `ts3native.ini` (`Kinds=0`), needs the matching ts3fixes; not tested in a live game yet and no gain is claimed.
- **Read-only lot front door (`[LotFrontDoorRO]`).** The lookup of a lot's front door can be answered without waiting for a background route job. Experimental: switched off in `ts3native.ini` (`Enabled=0`); not tested in a live game yet and no gain is claimed.
- **Read-only "is a Sim standing here" check (`[ObstructedRO]`).** After a route, the check for Sims standing on the goal can be answered without waiting for a background route job. Experimental: switched off in `ts3native.ini` (`Enabled=0`); not tested in a live game yet and no gain is claimed.
- **Read-only slot position (`[SlotEnabledRO]`).** When a Sim asks for a routing slot's position while a background route job runs, the lookup runs under a shared lock instead of waiting for the job. Experimental: switched off in `ts3native.ini` (`Enabled=0`); not tested in a live game yet and no gain is claimed.

## v0.1.3 (2026-10-16)

Hotfix: the v0.1.2 speed-ups were shipped switched off.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` (replace the v0.1.2 file). **Please update if you installed v0.1.2.**
- `ts3native-v0.1.3.zip`: same as v0.1.2 (no changes); keep your v0.1.2 files or replace them.

### Fixed

- **Faster saving and the lighter Sim panel are now really on.** In v0.1.2 the package's settings file still listed these four fixes as off, and the game uses that file over the built-in defaults, so the v0.1.2 package ran with them off. v0.1.3 ships the settings file with `kFastRefTable`, `kMotiveBarImageCache`, `kBuffGridInPlace` and `kSimNameCache` on. Nothing else changed; the tests listed under v0.1.2 apply.

## v0.1.2 (2026-10-16)

Faster saves and a lighter Sim panel.

### Download

- `ts3fixes.package`: put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` (replace the v0.1.1 file).
- `ts3native-v0.1.2.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` (replace the v0.1.1 files). Needs an ASI loader, as before. The new native save options below are off unless you switch them on in `ts3native.ini`.

### New in `ts3fixes.package` (on by default)

- **Lighter Sim panel.** The needs bars, the moodlet grid and Sim names in the panel no longer rebuild from scratch every time a need changes: the bars keep their pictures while the moodlet is the same, the moodlet grid updates its cells in place, and names come from a small cache. Only the panel changes; nothing in the game itself or in your save. Tested in one load per town with the fixes switching on and off every 3 minutes: Bridgeport PASS (30.2 vs 27.5 game updates per second and 4.1 vs 5.1 hitches per second with the fixes on vs off; one run, so treat it as a hint, not a measurement). Isla Paradiso PASS (44.0 vs 43.0 updates per second, 2.5 hitches per second either way).
- **Faster saving.** The game keeps a list of every object it has already written into the save, and in big towns that list got slow past about 16,000 objects. It now stays fast. The save file comes out the same (script data byte-identical with the fix on or off). Saves: Bridgeport 12.7 -> 10.3 s (-19%); Sunset Valley -17% to -24% over 3 paired saves (11.5 -> 8.7 s, 11.3 -> 9.3 s, 10.7 -> 8.8 s); Isla Paradiso -14% and -23% over 2 pairs (11.8 -> 10.1 s, 12.8 -> 9.8 s). One Isla Paradiso all-off vs all-on pair came out equal (11.0 vs 11.1 s): single saves there vary by up to 2 s, so treat the per-world numbers as ranges. It passed every save test below.

### New in `ts3native` (off, opt-in)

- **Faster lot footprint lookup when saving (`[FootprintIndex]`, off).** Saving a lot no longer scans every footprint in the town to find its owner. Same save result (checked with the built-in verify: 330 levels, 82 lots, 0 fallbacks). Reason for off: the gain (about 0.4 s, estimated) does not show above normal save-to-save variation, so it is not worth a new default yet.
- **Save writes to disk later (`[SaveFlush]`, needs `[SaveWrite]`, off).** The game waits less for the disk at the end of a save. Fixed during testing: a flush could fail on `Meta.data` when another program held the file for a moment (error 32); it now retries. Reason for off: no gain on an NVMe SSD; it should help slow or spinning disks (not measured), so it is for players who want to try it.
- **`[SaveCompress]` (stays off).** Measurement only: it reports compression time per save (0.85-0.9 s in Bridgeport) and changes nothing. Compressing in parallel is not possible without changing the save layout.

All four on together: Bridgeport save 13.1 -> 10.1 s (-23%).

### Save tests

Each world: one paused load, the fixes switched at runtime, every save compared resource by resource with the fixes off (`tools/harness/save-matrix.ts`). Cases: control, identical (all off vs all on), per-fix, overwrite, back-to-back, unpaused, build mode, verify counters, reload.

| World / case | Date | Result |
|---|---|---|
| Bridgeport, 9 cases | 2026-10-14 | 8/9 PASS (build mode: the game refuses to save there, so nothing to compare; reload: off and on saves both load with 0 script errors). Verify counters failed on a wrong check in the test tool, fixed since and passed in the worlds below |
| Sunset Valley, 9 cases | 2026-10-15 | 9/9 PASS (after the `Meta.data` retry fix; the run before it had 1 flush fail per save) |
| Isla Paradiso, 9 cases | 2026-10-15 | 9/9 PASS (one per-fix run failed because the town changed between saves while paused, with the off save differing too; rerun PASS) |
| Vacation travel (Isla Paradiso -> France -> home) | 2026-10-15 | PASS |
| University and future travel | 2026-10-16 | **Open.** Our test tool could not start these trips yet (the Sim dropped the queued trip before leaving), so no save ran; the save code was never reached. Vacation travel uses the same travel save and passed. A direct-trip test is in progress for v0.1.3. |

28 cases run: 27 PASS, 1 test-tool fail (fixed). University and future travel open (see above).

## v0.1.1 (2026-10-14)

Two more `ts3native` fixes switched on after in-game testing. `ts3fixes.package` is unchanged from v0.1.0.

### Download

- `ts3native-v0.1.1.zip`: put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\` (replace the v0.1.0 files). Needs an ASI loader, as before.
- `ts3fixes.package`: same as v0.1.0; keep yours or replace it.

### New in `ts3native` (on by default)

- **Faster unpacking of game files.** The game unpacks compressed textures, objects and other data all the time while loading and playing. A faster unpacker gives exactly the same results (checked against the game's own unpacker on every file of a full Bridgeport load: 0 differences; about 144,000 files unpacked over 4 test sessions without a problem). The gain is small: less work on load screens and when new items stream in.
- **Saving freezes the game a little less.** When you save over an existing save, the game first makes a backup copy of the old one. That copy now happens in the background while the game writes the new save, instead of the game stopping to do it. Saves and backups come out the same and load normally.

### Tested in game, no change needed

- The swimming and houseboat leftover fix from v0.1.0: confirmed working (with the fix, leftover route data is cleaned up; without it, it piles up).
- The smoother NRaas Overwatch nightly check from v0.1.0: confirmed working.

### Still being tested (off, not part of this release)

Faster Create-a-Sim hair lists, a faster memory allocator, a route cache and a few smaller caches. They did not show a clear win yet, so they stay off.

### Where these fixes come from

Same as v0.1.0: all code in this release is our own, nothing copied from other mods, no EA files. S3SS also speeds up the same unpacking step with its own decoder; ours is separate code and leaves S3SS's patch in place.

## v0.1.0 (2026-10-14)

First release: our two fixes for The Sims 3 (Steam 1.67, all expansions). Less stutter, more game time per real second, fewer stuck Sims.

### Download

- `ts3fixes.package`: the script mod with the stutter fixes. Put it in `Documents\Electronic Arts\The Sims 3\Mods\Packages\` (needs the usual `Mods\Resource.cfg`; script mods enabled in the game options).
- `ts3native-v0.1.0.zip`: the native plugin. Put `ts3native.asi` and `ts3native.ini` in `The Sims 3\Game\Bin\`. Needs an ASI loader (for example Ultimate ASI Loader as `wininet.dll`).

Close the game before installing. To undo, delete the files again.

### What you will notice

Measured over 10 minutes at speed 3 on the same saves, with and without `ts3fixes`:

| World | Game time per real second | Stutters (frames of 50 ms or more) |
|---|---|---|
| Bridgeport | +61% | -39% |
| Sunset Valley | +85% | -47% |
| Isla Paradiso | +133% | -51% |

### Fixes in `ts3fixes.package` (on by default)

- **Townies you can't see skip the walk.** A townie going home, to work, to school, to a shop or to a park, far from the camera and from your Sims, arrives without walking or driving the whole way. The game still runs the last few steps and the arrival, so what the Sim does next is unchanged. Your household and anyone you can see always walk.
- **No pointless route retries.** An unseen townie whose route failed waits 30 sim-minutes before trying the same trip again, skips the route-fail animation nobody sees, and keeps its planned route instead of re-rolling for a subway or hover train detour.
- **Homeless townies leave quietly.** Unseen townies leaving town fade out straight away instead of first walking to a random lot.
- **Stuck Sims get unstuck.** A Sim frozen in place, blocked in a doorway, trapped on a spot it can't walk off, in a hidden room, or failing its routes over and over gets the game's own reset or is sent home. At most once or twice per Sim per day.
- **Smoother nights with NRaas Overwatch.** Overwatch's nightly stuck check is spread over several moments instead of one ~300 ms freeze.
- **No more leftovers from swimming and houseboats.** A Sim swimming out to sea or boarding a houseboat no longer leaves dead route data behind, which piled up in Isla Paradiso saves.
- **Cheaper background work.** Music, fires and smells far from the camera check for nearby Sims less often, and walk styles are looked up faster.

This release contains only these stable fixes. Features still in testing stay switched off.

### Fixes in `ts3native` (on by default)

- **Fewer memory clean-up pauses.** The game's script memory starts bigger and grows in larger steps, so it pauses to clean up far less often (128 forced clean-ups down to 1 in 6 minutes in Bridgeport).
- **Faster boat and swimming routes.** Route searches over water use a faster version of the game's own search: about 4x faster in an offline test, with exactly the same results (5.4 million in-game searches in Isla Paradiso compared with the game's own code: 0 differences).

Only these two are switched on in the shipped `ts3native.ini`; everything else in it stays off until it is verified in game.

### Where these fixes come from

- **All code in this release is our own.** It was written from scratch in this repo. No code from another mod was copied, ported or repackaged, and no EA game files or decompiled EA code are included.
- **Other mods are not part of this release.** Community fixes (DXVK, Sims 3 Settings Setter, NRaas, the Smooth Patch, and others) stay with their authors: get them from their own pages. The repo's installer sets them up only so developers and testers get the same test setup we measure with. `docs/01-existing-fixes-comparison.md` lists them with credits.
- **New, as far as we know:** the unseen-townie shortcuts, route retry backoff, the houseboat and swimming leak fix, the background-work throttles, and the water route speed-up. We know of no other mod that does these.
- **Our own version of a known idea:** the stuck-Sim checks do a job NRaas Overwatch's Stuck Check also does (we studied what it does and wrote our own code), and the memory setting builds on a known Boehm GC heap tweak. These are credited, not claimed as new.
