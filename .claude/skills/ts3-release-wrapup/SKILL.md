---
name: ts3-release-wrapup
description: Release wrap-up for sims3-modernization: README/CHANGELOG numbers from runs, then the scrubbed public sync and GitHub release. Use for "release numbers", "fill the release notes", "publish the release", "sync the public repo".
---
# Release wrap-up

Follow docs/RELEASE.md. Steps in order (wrap-up runs in parallel once tests are green; ETA ~20 min after the last green test):

1. Numbers: `node tools/harness/release-numbers.ts --vanilla <tags> --prev <tags> --rel <tags> [--soak <tags>]` -> paste the tables into README.md / CHANGELOG.md (plain player words in CHANGELOG; setting names only in docs/PLAYERS.md's developer section and docs/release-notes/<v>-dev.md).
   Watch rows for the dev notes (docs/release-notes/<v>-dev.md, not the player tables): `node tools/harness/satb-giveups.ts <release queue tag prefix> --row` (satb worker give-ups by reason per run; a new top reason or a jump vs the last release is a triage item, not a blocker), and the stack cost `node tools/harness/arm-ms.ts <mono-measure-g3stack-1115 tags> --json` when that queue ran.
2. Verify the release package: `node tools/builds.ts check build/ts3fixes.package --expect ts3fixes.<version>` (release jobs pin `fixesBuild`).
3. Public sync / release, DRY RUN first: `node tools/publish-public.ts release <tag> --native-version <x.y.z> --message-file <msg> --ini <tested ts3native.ini> --package build/ts3fixes.package`
4. Only after Rodney's explicit OK for this release: same command with `--yes` (needs `GH_TOKEN="$PERSONAL_GITHUB_TOKEN"`; never print it). Never push local master to the public repo.
