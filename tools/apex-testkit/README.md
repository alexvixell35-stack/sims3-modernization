# Apex test kit

Test kit, **not a release**. It lets a tester of Apex Radiance pull requests run them the way we do: the same ts3native build, the same jobs (adapted to West Arbor), the same pass/fail lines and the same counter numbers. Players should use the normal [releases](https://github.com/rodneypantonial/sims3-modernization/releases).

Target: The Sims 3 Steam edition, patch 1.67. The kit has no EA files and no saves.

## What is in it

| File | What |
|---|---|
| `ts3native-v0.2.12.zip` | The pinned ts3native pair (`ts3native.asi` 0.36.247 + `ts3native.ini`), byte-equal to the v0.2.12 release asset. Every Apex PR test request of ours names this build. |
| `ts3fixes.package` | The v0.2.12 script mod that goes with the pair. |
| `SHA256SUMS.txt` | sha256 of the zip, the package and the asi and ini inside the zip. |
| `expect.json` | Per PR: build flavour, bridge, job and command, pass and fail regexes (each with a real matching and a non-matching line), checks that need eyes, our own result, what to send back. Plus the known non-failures. |
| `jobs/` | Copies of the PR job files with the save set to `West Arbor` (MIT, from the Apex repo, see `jobs/NOTICE.txt`). |
| `kit.ts` | Reader: log check per PR, counter lines, FrameTimeline columns, hitch numbers, sha check. Node >= 22.18, no dependencies, Windows/Linux/macOS. |

## Setup

1. Game `Game\Bin` (Steam: `<SteamLibrary>\steamapps\common\The Sims 3\Game\Bin`, called `<GAME_BIN>` below). Keep a backup of the folder and of `Documents\Electronic Arts\The Sims 3\`.
2. **Bridge:** the RTX Remix bridge installed as Apex installs it (`d3d9.dll` + `.trex\` in `<GAME_BIN>`). For a PR that changes `native/remix-bridge/`, use that PR's CI artifact `RemixBridge-<sha>` (the zip unpacks into `<GAME_BIN>`). Bridge CI: loinyx/Sims3-ApexRadiance#115.
3. **Apex:** the Developer flavour of the PR head (`build.ps1 -Flavor Developer -DevelopmentVersion`), passed with `-Asi`. Once loinyx/Sims3-ApexRadiance#114 is merged, CI keeps it as artifact `ApexRadiance-dev-<sha>`: `gh run download <run id> -R loinyx/Sims3-ApexRadiance -n ApexRadiance-dev-<sha>` instead of a local build.
4. **ts3native:** unzip `ts3native-v0.2.12.zip` into `<GAME_BIN>` (Ultimate ASI Loader loads it, as for Apex), and copy `ts3fixes.package` to `Documents\Electronic Arts\The Sims 3\Mods\Packages\`. Check: `node kit.ts sha <GAME_BIN>` must print `ok` for `ts3native.asi` and `ts3native.ini`. After a run, `ts3native.log` in `<GAME_BIN>` must show `release: 0.36.247` (`node kit.ts counters <GAME_BIN>\ts3native.log` prints it).
5. Do not change `ts3native.ini`: our numbers are with its defaults.

## Saves

All kit jobs load `West Arbor` (`-Save 'West Arbor'` on the runner, or the `jobs/` copies). If a job needs the Apex test saves, make them yourself as the Apex runner README says (`tools/ingame_runner/README.md`, "Test saves"): `ApexTest-Town` is a populated town in live mode by day, a household with several Sims on a furnished lot, the camera at street height over the lot; `ApexTest-Night` is the same town at night (21:00 to 23:00) with lit lots in view. Make them with the packs and mods you run with. Jobs with fixed camera positions that only fit Sunset Valley carry a `kitNote`.

## Run and check

Per PR, `prs.<N>.jobs[].run` in `expect.json` is the exact command (placeholders `<GAME_BIN>`, `<prN>` for the PR checkout). Then:

```
node kit.ts check --pr 94 <log> [<log>...]      # PASS/FAIL on the log lines; prints manual checks
node kit.ts counters <log> [Tag ...]             # e.g. RoomSolveShare AO DeadlineSlicing RouteGate MemDiag
node kit.ts timeline <ApexRadiance_FrameTimeline.csv> room_solve_waited apex_moving=1
node kit.ts hitch <ApexRadiance_FrameTimeline.csv> frame_ms
node kit.ts --self-check
```

`check` exits 0 on pass, 1 on fail. Known non-failures are listed as `note`, not failed: `[ERROR] [Transition routing] Pattern not found` (ts3native's routing gate on Apex heads without #113) and a runner close timeout (#112 adds a hang dump for it).

## What to send back (PR comment)

- Apex head sha, Developer or Public, bridge (installed version or `RemixBridge-<sha>`), `release:` line of ts3native.log.
- Save used, the command, `node kit.ts check` output.
- The PR's `sendBack` items from `expect.json` (profiler lines, `counters` or `timeline` output, `hitch` per variant).
- How the game closed (normal, close timeout, crash), and any line `check` marked FAIL with 5 lines around it.

Compare with `prs.<N>.ours` in `expect.json`.
