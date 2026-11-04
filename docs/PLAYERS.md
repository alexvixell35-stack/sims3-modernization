# For players: what's switched on and how to install

This part is added to every release. It lists **everything that is switched on** in the current release, not only what changed, and how to install it step by step.

**You don't need to change any settings.** Both files come set up. The setting names at the end of this page are only for testers and developers.

**Works with:** The Sims 3 from **Steam**, patch 1.67, Windows 10 or 11. Any mix of expansion and stuff packs. The Origin / EA app version is **not** supported (the plugin checks the game file and switches itself off on any other version, so nothing breaks, it just does nothing).

## What's switched on

### `ts3fixes.package` (script mod)

Less stutter, mostly by skipping work nobody can see:

- **Townies you can't see skip the walk.** A townie far from the camera and from your Sims, going home, to work, school, a shop, a park, a venue or another home, arrives without walking or driving the whole way. The last steps and the arrival still happen, so what the Sim does next is unchanged. Your household and anyone you can see always walk.
- **Homeless townies leave quietly.** Unseen homeless townies leaving town fade out straight away instead of first walking to a random lot.
- **No pointless route retries.** An unseen townie whose route failed skips the route-fail animation nobody sees, waits 30 sim-minutes before trying the same trip again, and keeps its planned route instead of re-rolling for a subway or hover train detour.
- **Stuck Sims get unstuck.** A Sim frozen in place, blocked in a doorway, trapped on a spot it can't walk off, in a hidden room or failing routes over and over gets the game's own reset or is sent home, at most once or twice per Sim per day.
- **No leftovers from swimming and houseboats.** Swimming out to sea or boarding a houseboat no longer leaves dead route data behind (it piled up in Isla Paradiso saves).
- **Cheaper background work.** Music, fires and smells far from the camera check for nearby Sims less often, and walk styles are looked up faster.
- **Faster saving in big towns.** Saves came out 14-24% faster in our tests, with the same save file.
- **Lighter Sim panel.** The needs bars, the moodlet grid and Sim names no longer rebuild from scratch on every small change.
- **No freeze from the 3 AM stuck-Sim check.** The game's nightly check for stuck Sims now handles one Sim at a time instead of all at once. Same checks, same outcome.
- **NRaas Overwatch: no nightly clean-up freeze.** If you use Overwatch, its nightly clean-up jobs run one after another instead of all in the same moment.
- **NRaas Overwatch: no reset for a Sim that is only asleep.** The game's own night-time stuck check tries a few test walks from where a Sim stands. Overwatch counted those failed test walks as the Sim being stuck and reset sleeping Sims. Now it only counts the Sim's own walks.
- **NRaas ErrorTrap: no hitch right after loading.** If you use ErrorTrap, the check it runs a moment after every load is spread out. A newer ErrorTrap that does this itself switches ours off on its own.

### `ts3native` (native plugin: `ts3native.asi` + `ts3native.ini`)

- **Fewer memory clean-up pauses.** The game's script memory starts bigger and grows in larger steps, so it stops to clean up far less often.
- **Faster boat and swimming routes.** Route searches over water use a faster version of the game's own search, with exactly the same results.
- **Faster unpacking of game files.** Textures, objects and other packed data unpack faster, with identical results.
- **Saving freezes a little less.** The backup copy of your old save is made in the background while the game writes the new one.
- **Route planning in the background.** When a Sim plans a walk or drive, the plan runs on a second CPU core while the game goes on, instead of freezing the game until it is done. The game's own route planner still does the work; only where and when it runs changes. Needs `ts3fixes.package` from the same release.
- **Moving Sims and objects don't wait for route plans.** When a Sim sits, stands up or picks something up while a route is planned in the background, the game no longer stands still until the plan is done.
- **Fewer short freezes in crowds.** When many Sims look around them while a route is planned in the background, the game waits less.
- **Crash guards for background route planning.** Leftover route data of removed objects is cleaned up, every route search starts from clean values, and searches for a spot on the shore wait for the background planner instead of crashing.

Everything else in `ts3native.ini` is **off**: experiments still being tested. Leave them off unless you want to help test, and keep backups.

- **Test setting `[UnseenFallback] DeadlineMs` (only used with `Enabled=1`, off by default):** how long (in ms) the game may search for a route for a Sim you cannot see before it gives up. Lower means fewer and shorter freezes caused by off-screen Sims, but more of those Sims have their walk cancelled or are moved straight to where they were going; higher means more of their routes are planned for real. Default 300, allowed 175 to 300 (other values are set to the nearest limit, and `ts3native.log` says so).

## How to install

You need two downloads from the release page: **`ts3fixes.package`** and **`ts3native-<version>.zip`**. You can install just one of them if you like; they work on their own.

1. **Close the game.**
2. **Back up your saves.** Copy the folder `Documents\Electronic Arts\The Sims 3\` somewhere safe (it holds your saves and mods).
3. **Install the script mod (`ts3fixes.package`):**
   1. Open `Documents\Electronic Arts\The Sims 3\`. If there is no `Mods` folder, create `Mods`, and inside it `Packages`.
   2. If `Mods` has no `Resource.cfg` file, create one (a text file, named exactly `Resource.cfg`) with this text:
      ```
      Priority 500
      PackedFile Packages/*.package
      PackedFile Packages/*/*.package
      PackedFile Packages/*/*/*.package
      ```
      Most mod guides give the same file; if you already have one, keep it.
   3. Put `ts3fixes.package` in `Mods\Packages\`. If you had an older version, replace it.
4. **Install the native plugin (`ts3native`):**
   1. Find the game folder: in Steam, right-click **The Sims 3** > **Manage** > **Browse local files**, then open `Game\Bin`.
   2. **ASI loader:** the plugin needs one. If you already use Sims 3 Settings Setter or another `.asi` mod, you have one: skip this step. Otherwise download [Ultimate ASI Loader](https://github.com/ThirteenAG/Ultimate-ASI-Loader/releases) (the 32-bit / Win32 zip), take `dinput8.dll` out of it, rename it to `wininet.dll` and put it in `Game\Bin`.
   3. Unzip `ts3native-<version>.zip` and put `ts3native.asi` and `ts3native.ini` in `Game\Bin`. Replace older copies.
5. **Start the game from Steam.** On the first start with the script mod, the game asks whether to enable script mods: say yes (or turn on **Script Mods** in the game options).
6. **Check it works:** after starting the game, `Game\Bin` has a new file `ts3native.log`. For the script mod, the game's "Mod Scripts Found" message at start is a good sign.

### Uninstall

Close the game and delete what you added: `ts3fixes.package` from `Mods\Packages\`, and `ts3native.asi` and `ts3native.ini` from `Game\Bin` (plus `wininet.dll` if you added it only for this). Your saves are not changed by the install or the removal.

### Reporting a problem (stuck save, freeze, slowdown): send ts3native.log

Close the game first (a game that hangs: end it in Task Manager), then attach **`Game\Bin\ts3native.log`** to your report (GitHub issue or Discord). Do not start the game again before you copy it: every start rewrites the file. From version 0.36.53 on it already answers the first questions (docs/188): plugin and ts3fixes versions and settings, your game edition (only the Steam edition is supported), other plugins in `Game\Bin`, your mods (count, size, script mods, the first 200 file names), the world and its size, CPU, RAM, GPU and Windows version, and for every save each step with its time, plus a line every 5 seconds while a save is still running. It contains file names only: folder paths with your user name are replaced by `<user>` or `<home>`. Open it in Notepad to check before you send it.

### If the game crashes: sending a crash report

This is **opt-in** and still being tested, so it is off in the release. Nothing is ever sent without you saying yes.

1. **Switch it on:** in `Game\Bin\ts3native.ini`, under `[CrashReport]`, set `Enabled=1`.
2. **Play as usual.** When the game crashes, the plugin writes a text file to `Documents\Electronic Arts\The Sims 3\ts3native-crashes\` (`crash-<date>-<time>-<n>.txt`). If the game just vanished without a report, the next start writes a `...-hard.txt` file instead. The file has the plugin version, which switches were on, where in the game's code it crashed, your package file names, the last lines of `ts3native.log`, and your CPU, RAM and Windows version. Your user name, your machine name and folder paths with your name in them are replaced by placeholders such as `<user>`. Open it in Notepad to see for yourself.
3. **Send it (optional):** install [Node.js](https://nodejs.org/) 22.18 or newer, download [`tools/report-crash.ts`](https://github.com/rodneypantonial/sims3-modernization/blob/HEAD/tools/report-crash.ts) (or the whole repository), and run `node report-crash.ts` in a terminal. It shows each new report and **exactly** what would be sent, and asks first. If you have the [GitHub CLI](https://cli.github.com/) logged in (`gh auth login`), it adds your report to an existing issue for the same crash or opens a new one, as you. Otherwise it opens a prefilled GitHub page in your browser; check it and press **Submit new issue** (you need a free GitHub account). Answer `s` to never be asked about a report again.
4. **Without the tool:** open a [crash report issue](https://github.com/rodneypantonial/sims3-modernization/issues/new?template=crash.yml), copy the `signature id:` line into the first box and paste the whole file into the report box.

To switch it off again, set `Enabled=0`. The folder `ts3native-crashes` can be deleted at any time.

### Error 12 report (test feature)

Error 12 means the game ran out of room in its memory (the game is a 32-bit program, so it can use at most 4 GB, however much RAM your PC has). Heavy CC and many mods make it come sooner. If you get Error 12 often, this report shows us what filled the memory, and whether our mod had any part in it.

This is a **test feature**: it is off in the release and still being tested. Nothing is ever sent without you saying yes.

- **Always on, logging only:** every 5 minutes of play `ts3native.log` already gets one short line of memory numbers (`[MemDiag] Light=1`). That alone often helps, so send `ts3native.log` with any Error 12 report.
- **The Error 12 report adds more detail:** which parts of the game and which other plugins hold the memory, and what grew before the error.

1. **Switch it on:** in `Game\Bin\ts3native.ini`, under `[MemDiag]`, set `AllocSites=1`.
2. **Play as usual until Error 12 happens.** Then close the game (end it in Task Manager if it hangs). Do not start the game again before step 3: every start rewrites `ts3native.log`.
3. **Send it (optional):** with [Node.js](https://nodejs.org/) 22.18 or newer and [`tools/report-crash.ts`](https://github.com/rodneypantonial/sims3-modernization/blob/HEAD/tools/report-crash.ts), run `node report-crash.ts --memory --log "<your game folder>\Game\Bin\ts3native.log"`. It keeps only the memory lines and the last lines of the log, replaces folder paths with your user name, your user name, your PC's name and email addresses with placeholders such as `<user>`, and replaces your package file names with short ids (`pkg-1a2b3c4d`). It shows **exactly** what would be sent, saves the same text as `ts3native-memory-report.txt`, and asks first. Then, like the crash report, it opens a new issue with your logged-in [GitHub CLI](https://cli.github.com/) or a prefilled GitHub page you submit yourself. If you are fine with sharing your package file names (it helps us see which CC or mods take the memory), add `--names`.
4. **Without the tool:** open an [Error 12 report issue](https://github.com/rodneypantonial/sims3-modernization/issues/new?template=memory.yml) and attach `ts3native.log`. Open it in Notepad first: it lists your package file names.

**What it costs:** the extra bookkeeping takes some memory and time while you play; the exact cost is still being measured. Switch it off again (`AllocSites=0`) once you have sent the report.

**With Apex Radiance:** memory is Apex Radiance's area, so the Error 12 report switches itself off when Apex Radiance is installed (`[Compat] ApexStandDown=1`). The short memory line (`Light`) still logs; send `ts3native.log` to both projects.

**What we do with it:** we read the memory lines with our own tools to find what grows until Error 12. If it is our mod, we fix it. If it is the game, another mod or a CC item, we tell you, and with your permission the mod's author. The report lives in the GitHub issue you open, which anyone can read: that is why the tool removes personal details first.

### Other mods

If you use NRaas Overwatch and notice a half-second freeze about once per game day, turn off City Hall > NRaas > Overwatch > Settings > Clean up Inventory. That nightly job looks up the name of every inventory item; with it off, broken or orphaned inventory items are no longer removed. The setting is saved per save game.

Tested together with DXVK, Sims 3 Settings Setter, NRaas (ErrorTrap, Overwatch), the Smooth Patch and the Sims 3 Performance Patch. If something looks wrong, remove our files first and see if it goes away, then [open an issue](https://github.com/rodneypantonial/sims3-modernization/issues) with what you saw.

## Help port to the EA app version

The fixes are built for the Steam edition only. If you own The Sims 3 from the **EA app or Origin**, you can help port them without sending any game files:

1. Download `Sims3PortProbe.ps1`, `port-sigs.txt` and `Run-PortProbe.cmd` from `tools/port-probe/` (or the release zip) into one folder.
2. Start the game and wait for the **main menu**. Then double-click `Run-PortProbe.cmd` (no admin needed). It reads the running game's code in memory, read only, and after about a minute writes `Sims3PortReport-xxxxxxxx.zip` to your Desktop. Close the game afterwards as usual.
3. Send the zip to Rodney: [open a port report](https://github.com/rodneypantonial/sims3-modernization/issues/new?template=port-report.yml) and drag the zip in, or send it in a Discord DM.

The report holds only numbers, names and hashes (addresses, function sizes, imported function names, string texts the game uses, script method sizes and hashes, your installed pack folders and Windows version). It holds no game code, no paths, no user or machine name. The script is plain PowerShell you can read; it uses no AI and sends nothing by itself. Windows only (it reads another program's memory). Rarely Rodney may send back a small `questions.json`: run `powershell -ExecutionPolicy Bypass -File Sims3PortProbe.ps1 -Questions questions.json` with the game at the main menu and send the answers zip the same way.

## For testers and developers: setting names

You don't need anything in this section to play. It maps the list above to the settings that are on in the shipped files, so testers can tell what is what. Technical notes per release: `docs/release-notes/`.

- `ts3fixes.package` tunables set to True: `kGoHomeShortcut`, `kBuildingTripShortcut`, `kLotTripShortcut` (townies skip the walk), `kVirtualHomeShortcut` (homeless townies), `kNoRouteFailWalkOffscreen`, `kRouteFailureBackoff`, `kNoStationDetourOffscreen` (route retries), `kStuckWatchdog`, `kStuckWatchdogIdle` (stuck Sims), `kPortalAdaptorLeak` (swimming and houseboats), `kBroadcasterThrottle`, `kWalkStyleComparer` (background work), `kFastRefTable` (saving), `kMotiveBarImageCache`, `kBuffGridInPlace`, `kSimNameCache` (Sim panel), `kStuckCheckAsync` (3 AM check), `kOverwatchChain`, `kOverwatchIgnoreStuckProbes` (Overwatch), `kErrorTrapSpread` (ErrorTrap).
- `ts3native.ini` sections with `Enabled=1`: `[GC]` (memory), `[WaterJps]` (water routes), `[Decompress]` (unpacking), `[SaveWrite]` (saving), `[RouteAsync]` with `[RouteGate]` and `[Discourage]` (background routing and its safety locks, which must stay on together; `[Discourage]` `DeferFootprint=1` = moving Sims and objects, `[RouteAsync]` `RefcountGuard=1` = crowds), `[LotRemoveGuard]` and `[OptsInit]` (crash guards).
- New in v0.2.0, on: `ts3fixes.package` tunables `kEaSpreadLots`, `kEaSpreadPets`, `kEaSpreadDecay` (nightly chores in small steps), `kOverwatchStuckSlice` (NRaas Overwatch stuck check split up), `kUnseenFallbackStuck`, `kUnseenFallbackPlan`, `kUnseenFallbackTeleport`, `kUnseenFallbackPre`, `kUnseenFallbackArrive`, `kUnseenFallbackLeave`, `kUnseenFallbackLoopDetect` (hitch guard for Sims you can't see); `ts3native.ini` sections `[UnseenFallback]` (the same guard, native side) and `[UserDiag]` (the problem-report lines in `ts3native.log`, since v0.1.6).
- `ts3native.ini` `[EaCrashFixes]` keys set to 1 (fixes for the game's own crashes): `GeomNoMaterial` (custom-content hair or clothing mesh without a material: that part is skipped at that distance instead of crashing, since v0.2.5), `WorldAStarRefs` and `PortalFlagRefs` (Isla Paradiso routing crash, since v0.2.5). The other keys there stay 0 until they are proven the same way.
- To switch background routing off: `Enabled=0` under `[RouteAsync]` in `ts3native.ini`; the game then plans every route itself as before.
