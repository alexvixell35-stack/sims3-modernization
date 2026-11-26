---
name: ts3-experimental
description: Daily experimental pre-release for testers (latest master, verified switches on), its switch evidence list and its scheduled task. Use for "experimental build", "tester build", "daily pre-release", "is a switch verified", "update verified-switches", "why is X off in the experimental build".
---
# Experimental pre-release

docs/279. Run from the repo root (Node >= 22.18, Windows for the build):

```
node tools/release/experimental.ts --dry-run [--ignore-busy]   # build + gates + switch apply + scan, publishes nothing (probe-out/experimental/out/<tag>)
node tools/release/experimental.ts [--ignore-busy] [--force]   # also public sync, GitHub pre-release v<next>-exp.<YYYYMMDD>, prune to 7
node tools/release/experimental.ts --self-check
powershell -NoProfile -File tools/release/register-experimental-task.ps1 [-At 12:30] [-Unregister]
```

Needs `PERSONAL_GITHUB_TOKEN` (never print it). Builds while chains run but skips while a timing job runs (docs/279 step 1), when master and the switch list match the last experimental release, and when today's tag exists. Never touches the stable release, `latest`, or `docs/data/builds.json`.

Switch list: `docs/data/verified-switches.json`. When a correctness job of a switch passes (or fails), update its entry in the same commit as the result page: `status` verified (its `on` values go in) / blocked (its `off` values are forced) / unverified (master default), `evidence` = repo paths of the result pages, one-line `reason`. Household mismatch, crash or open FAIL = blocked. Keep `knownIssues` current. Log: `probe-out/experimental/experimental.log`.
