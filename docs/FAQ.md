# FAQ

Short answers to the questions players ask most, each with the page that has the details. Claims are marked the way the rest of the docs mark them: `[verified]` (code, disassembly or a test run), `[community]` (reported by others) or `[inferred]`.

## Is it safe for my saves?

- **Nothing of ours is stored in your saves** [verified]. Neither `ts3fixes.package` nor `ts3native` adds objects, fields or data of its own to a save file. Where a feature has to remember something per save, it writes a small text file under `Documents\Electronic Arts\The Sims 3\ts3native\` instead, which you can read or delete.
- **You can remove it any time** [verified for v0.3.0]. In the v0.3.0 release tests, a save made with our mods loaded in the unmodded game and with the previous release, and saves made without our mods loaded with it ([CHANGELOG.md](../CHANGELOG.md), v0.3.0 "Tested").
- **What can change inside a save:** a few EA bug fixes that are on by default clean up the game's own leftovers while you play (references to Sims who died or left, relationship entries that came apart, an old spouse link after a new marriage, alarms for Sims who are gone). The game saves that cleaner state in its own format, the way it would without the bug, and the cleanup stays when you remove the mod. These passed the save test bundle before v0.3.0 [verified]. The bigger clean-ups (`kSaveCleanup`, `kSaveCleanupAggressive`, `[SaveSlim]`) are **off**.
- **Your household is not touched.** The speed-ups that skip or shorten work (shorter walks, skipped animations, route limits) only apply to Sims you don't control and can't see. Your household and the selected Sim always get the game's own behaviour (rule in [AGENTS.md](../AGENTS.md), audit in [docs/249](249-household-sims-audit.md)).

Full list: [docs/PLAYERS.md, Save safety](PLAYERS.md#save-safety). Keep a backup of `Documents\Electronic Arts\The Sims 3\` anyway, as with any mod.

## What do the switches change, and where is the list?

- **What is on:** [docs/PLAYERS.md](PLAYERS.md) lists everything that is switched on in the current release in player words, plus every setting name at the end. The same text is added to every GitHub release. CI fails a release whose list misses a setting that is on [verified, `.github/workflows/build.yml`].
- **`ts3native` (the plugin):** each feature is a section in `ts3native.ini`, with a comment that says what it does. `Enabled=0` under a section turns that feature off; no rebuild needed. Everything not listed in PLAYERS.md is off.
- **`ts3fixes.package` (the script mod):** each fix is one tuning value inside the package. The table in [mods/ts3fixes/README.md](../mods/ts3fixes/README.md) says what each one does and whether it is on. There is no in-game settings menu yet: to turn one fix off you need a package editor, or you remove the whole package.
- **New things ship off.** A new feature stays off until it is measured and tested in game ([docs/adr/0007](adr/0007-default-off-until-measured.md)).

## Does it conflict with NRaas, S3SS or the Sims 3 Performance Patch (S3PP)?

All three are part of our own test setup, so every game test of ours runs with them loaded ([README](../README.md#results-so-far), `tools/setup/fixes.json`) [verified].

| Mod | What it does | What we do next to it |
|---|---|---|
| **NRaas** (ErrorTrap, Overwatch, ...) | Error repair, nightly clean-ups, stuck-Sim checks, many gameplay mods | We don't replace NRaas. Two fixes make NRaas run smoother: Overwatch's nightly clean-up runs job by job instead of all at once (`kOverwatchChain`), and ErrorTrap's check after loading is spread out (`kErrorTrapSpread`); a newer ErrorTrap that does this itself switches ours off. 24 of our EA bug fixes step aside when NRaas StoryProgression, GoHere, Consigner or Woohooer is installed (CHANGELOG v0.3.0). We read NRaas code to stay compatible, but never copy it (README "Project rules"). On the EA app 1.69.47 we found no ErrorTrap build that works, so our EA tests run without it ([PLAYERS.md](PLAYERS.md#ea-app-version-16947)). |
| **Sims 3 Settings Setter (S3SS)** | Engine settings and patches: thread stacks, world cache, memory allocator, tick limit, lot streaming | Our plugin's own hooks were mapped address by address against every S3SS 1.6.3 patch ([docs/68](68-hook-conflict-map.md)), and every patch of ours checks the original bytes first and skips itself if they differ [verified]. Lot streaming belongs to S3SS; ours stays off ([docs/148](148-apex-radiance-compat.md) §7). With S3SS 1.6.3 and `LotStreamingOptimizations` on, its camera speed setting writes into freed memory after travel; `ts3native` redirects that one write (`[S3SSCompat] StaleWriteGuard`, README "Quick start: players"). |
| **S3PP** (Sims 3 Performance Patch) | Address space, load time and string-table savings | Its features run next to ours. Where one of our fixes covers the same thing and measures better, our installer turns that one S3PP feature off instead of forking S3PP ([docs/adr/0003](adr/0003-prefer-own-fixes-over-s3pp.md); feature by feature in [docs/69](69-s3pp-vs-ours.md), 2026-10-16). |

Other mods: [docs/COMPATIBILITY.md](COMPATIBILITY.md) has a static code-overlap scan of 334 mod files (49 of the 100 most downloaded MTS mods). It is a code scan, not a game test.

## How does it relate to Apex Radiance and its RTX Remix bridge?

[Apex Radiance](https://github.com/loinyx/Sims3-ApexRadiance) (by loinyx) is a separate project. Its RTX Remix bridge moves the game's rendering into a separate 64-bit process, which leaves the 32-bit game much more address space. It is our recommended setup since 2026-11-13, and our release tests run on it (README "Recommended setup").

The two projects split the work: Apex Radiance owns rendering, memory and engine speed-ups; we own routing, the script engine (Mono) and the game's own bugs. Where both have a fix for the same code, ours steps aside by itself when Apex is installed (`[Compat] ApexStandDown=1`, [docs/148](148-apex-radiance-compat.md) §7) [verified in code]. You can use either one alone. Plain DXVK without Apex still works but gets no test runs from us (best effort).

## How do you measure the gains?

- **Harness:** `tools/harness/profile-world.ts` loads a test save, plays 10 minutes at speed 3 and records every simulation step. Runs alternate (off, on, on, off per town), with a check that no other program loaded the CPU during the run ([docs/TESTING.md](TESTING.md)).
- **Saves:** aged 14-day saves of Bridgeport, Isla Paradiso and Sunset Valley, so the towns carry the clutter of real play.
- **Baseline vs latest:** "Baseline" is the same setup (Apex Radiance + bridge, S3SS, NRaas and the other community fixes) without our mods; it is compared with the latest release. The numbers are in the [README results table](../README.md#results-so-far): steps per second, the slowest 1% and 0.1% of steps, the worst step, and how many steps took 300 ms or more.
- **Limits:** all numbers come from one laptop and one set of saves. Treat them as "this is what we saw", not as a promise; results from other PCs are welcome.
- **Who builds what, and how to check a file:** [docs/HOW-WE-BUILD.md](HOW-WE-BUILD.md).

## My antivirus flags the download. Is it a virus?

We have one report so far: Bitdefender flagged a `ts3native` release zip as `Gen:Variant.Draftor.29826` ([issue #67](https://github.com/rodneypantonial/sims3-modernization/issues/67)). On VirusTotal, 7 of 65 engines flagged that zip, and all 7 (ALYac, Arcabit, BitDefender, Emsisoft, eScan, GData, VIPRE) use Bitdefender's scan engine, so it is one detection seen seven times [verified 2026-10-27]. We sent Bitdefender a false-positive report.

- **Why a plugin like this gets flagged** [inferred]: `Gen:Variant` is a generic, behaviour-based detection, not a known malware family. `ts3native.asi` is a DLL that the ASI loader injects into the game; to fix the game it changes the game's code in memory, hooks some Windows functions and can write crash reports. Those are also techniques malware uses, which is what generic scanners react to. Neither file contains network code [verified: no socket, HTTP or `System.Net` use in `native/ts3native` or `mods/ts3fixes`], and with our settings the game makes no network calls while you play ([docs/16](16-online-features.md)).
- **Check it yourself:** the source is public. `ts3native.asi` is built by GitHub Actions from the tagged public code, and each release note ends with a "Built by CI" note: the Actions run link and the SHA-256 of the `.asi` in the zip. Compare it with `Get-FileHash ts3native.asi`. `ts3fixes.package` is built by the maintainer, because it compiles against EA's own game files, which can never be in CI ([docs/HOW-WE-BUILD.md](HOW-WE-BUILD.md)). The files are not code-signed.
- **Scans:** we scan builds from time to time as a reference, to learn what gets flagged and change our code where we can. We do not scan every release, so a new release has no VirusTotal link of its own.
- If you prefer to wait, that is fine. If you use it, exclude only that one file, never a whole folder or drive.

## Are there known mod conflicts?

Two mods stopped the game on **our** test setup (Steam 1.67, Apex Radiance + RTX Remix bridge, an Isla Paradiso test save), **with none of our mods installed**, so neither is caused by our mods. We found them while bisecting a large Mods folder:

- **`EnhancedWitches.package`**: the game hangs before the main menu, with this package alone, twice ([docs/314](314-kit-ab-mode.md), 2026-11-21) [verified on our setup]. Why it hangs (a missing pack or dependency, or an error in its start-up) is not known yet.
- **`velocitygrass_AddAnyLotSize.package`** (Add Any Lot Size): the main menu comes up, but loading the world never finishes, with this package alone, twice (bisect of 2026-11-22, [docs/314](314-kit-ab-mode.md)) [verified on our setup]. A tester who uses it on the EA app 1.69.47 loads fine [community], so this may depend on the game version or the setup.

Seen on our setup, not proven for every setup. If you use one of these and your game loads, keep it; if your game hangs at the same point, try without it first.

Two other mods have known limits with ours ([docs/PLAYERS.md](PLAYERS.md#other-mods)):

- **Sims3MonoModder (S3MM):** our plugin turns its own script speed-ups off when `Sims3MonoModder.asi` is installed, but S3MM's profiler is **not compatible** with `ts3native` (both hook the same script-engine functions; it can crash depending on load order).
- **Tiny UI Fix:** works, but one speed-up of ours (AutShadow) may turn itself off.

Found another one? Open an [issue](https://github.com/rodneypantonial/sims3-modernization/issues) with `Game\Bin\ts3native.log`.

## How do I check my own Mods folder?

Use Apex Radiance's **mod conflict scan** (`ModConflicts`, by loinyx and the Apex Radiance project; it started as a port of our `compat-scan.ts`). It reads your `Mods` folder and `Game\Bin`, changes nothing there, and lists which of your mods clash with each other: the same resource in two mods, two core mods replacing one game file, old and new copies of one script mod, two mods replacing the same interaction, and two plugins (`.asi`) patching the same game address. Overlaps with our `ts3native` that are coordinated on purpose (one side steps aside) show as INFO, not as a conflict.

How to run it (Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)): download or clone [Apex Radiance](https://github.com/loinyx/Sims3-ApexRadiance), then run this in that folder:

```
dotnet run --project tools/mod_conflicts
```

Without a folder it scans the game it finds; add a folder path to scan another one, `--offline` to skip its one download of the hook table. Exit code 1 means it found a conflict. Details: [tools/mod_conflicts/README.md](https://github.com/loinyx/Sims3-ApexRadiance/blob/main/tools/mod_conflicts/README.md). A WARNING there is a hint, not proof of a clash. Our own static scan of popular mods against our mods is in [COMPATIBILITY.md](COMPATIBILITY.md).
