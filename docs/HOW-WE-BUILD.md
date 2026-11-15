# How we build

What goes into a release file, who builds each part, and how you can check it. Short version: `ts3native.asi` is built by GitHub Actions from the tagged source; `ts3fixes.package` is built by the maintainer, because it compiles against EA's own game files, and those can never be in the repo or in CI. Neither file is code-signed yet.

## The two release files

| File | Built by | Where | Source |
|---|---|---|---|
| `ts3native.asi` + `ts3native.ini` (native plugin, C++) | GitHub Actions, `windows-latest`, MSVC x86 | job `native` in `.github/workflows/build.yml` runs `native\ts3native\build.cmd`, which also runs `selftest.exe` | `native/ts3native/` |
| `ts3fixes.package` (script mod, C#) | the maintainer, on his machine | `tools/setup/build-fixes.ts` with the Windows `csc.exe`, compiled against the script assemblies of his own game install | `mods/ts3fixes/` |

Why the split: `ts3fixes` needs EA's assemblies to compile. We never commit or upload EA files, so CI cannot build it. The workflow says so in its header comment, and the draft release is made without that file; the maintainer attaches it.

## What CI runs on every push and pull request

- `native`: build `ts3native` and run its self-test (`selftest.exe`), upload the plugin as an artifact.
- EA app build: the same sources with `/DTS3A_TABLE` for the EA app 1.69.47 exe (docs/228), plus a check that the EA ini rows resolve.
- `node` (Ubuntu): `node tools/selftest-all.ts --ci`, a compile check of every script under `tools/` and `mods/` and every self-check that needs no local data (CONTRIBUTING.md "Tool scripts").

CI cannot run the game, so anything that needs the game (timing runs, repros, crash checks) is done by the maintainer and contributors with the harness (docs/TESTING.md) and reported with build hashes.

## What a version tag does

Pushing a tag `v*` runs the `release` job after `native` and `node` pass:

1. Release notes = the `## <tag>` section of `CHANGELOG.md`, followed by `docs/PLAYERS.md`. The job fails if the section is missing.
2. The job fails if `ts3native.ini` enables any section that is not on the stable list (new behaviour ships off until measured, docs/adr/0007).
3. The job fails if `docs/PLAYERS.md` does not name every enabled native section and every `ts3fixes` tunable shipped on.
4. It zips `ts3native.asi` and `ts3native.ini` and creates a **draft** GitHub release. A person reviews the draft, attaches `ts3fixes.package` and publishes.
5. The notes end with a "Built by CI" note: the Actions run link and the SHA-256 of the `ts3native.asi` in the zip.

## Check a download yourself

- Compute the SHA-256 of `ts3native.asi` from the zip (`Get-FileHash ts3native.asi` in PowerShell) and compare it with the value in the release notes; the run link in the same note shows the build log.
- Read the source: everything is in this repository (code MIT, docs CC BY 4.0, see README "License").
- Build it yourself: [docs/BUILD.md](BUILD.md). The toolchain version can change the asi bytes; the ini should match exactly (docs/RELEASE.md "Rebuild check").
- The files are not code-signed, so Windows SmartScreen or an antivirus may warn about an unknown file. Signing and a VirusTotal link per release are planned but need a signing account (PLAN.md, "Player confidence checklist"); until then the hash and the build log are the check.

## Rules that apply to every build

- No EA game files, decompiled code, saves or dumps in the repository or in CI.
- New behaviour is off by default and switchable at run time (docs/adr/0007).
- Household Sims stay untouched (docs/adr/0006).
- Scripts work on Windows, Ubuntu and macOS unless they are Windows-only by nature (CONTRIBUTING.md).
