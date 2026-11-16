---
name: ts3-night-run
description: Start or resume unattended game test queues (night runs, chains of queues) with clean-machine checks. Use for "start tonight's run", "run these queues", "resume the night run".
---
# Night / chained runs

```
node tools/harness/night-run.ts                                   # report only: lock/idle state, apps, resume plan
node tools/harness/night-run.ts --queue-list a.json,b.json --close [--stop-on-fail]
node tools/harness/night-run.ts --queue-list a.json,b.json --resume  # every unfinished queue continues from its first job without a row
```

Rules: timing queues only when the machine is quiet (Rodney says when he uses it); Spotify is stopped only on night runs (`TS3_NIGHT=1`, set by night-run.ts) or with `queue.ts --close-spotify`, daytime runs leave his apps alone; other apps close only with `--close` on Rodney's go (needs lock or idle). Correctness tests may run in the daytime when they unblock work. Check progress with the ts3-status skill.

Rewind (docs/270 §30): queues with `"rewind": "auto"` start same-start jobs from a rewind instead of a fresh game start. The chain sets the Bin state the queue needs before it: `ST=$(node tools/harness/queue-rewind.ts state tools/harness/queues/<q>.json)` gives `apex-rewind` or `apex`, then `node tools/turbo-compat.ts state <snapshot dir> $ST` (bridge only, no DXVK arms). `node tools/harness/queue.ts <q> --dry-run` shows which jobs rewind; the md rows say `start: fresh | rewind (N ms) | rewind refused/failed -> fresh` and the end line the time saved.

Main session: run this and decide only; hand log reads, triage and fixes to a sub-agent (AGENTS.md, "Main agent orchestrates").

## Watching the run

Watch the chain or queue log with `node tools/harness/watch.ts <log> --match "<verdict re>" --until "<end re>" --timeout 1800` in a Monitor, never `tail -F` (a Monitor timeout leaves `tail.exe` running on Windows). Exit 2 = timeout: re-arm. `node tools/harness/track.ts clean` kills leftover orphan `tail` watchers.
