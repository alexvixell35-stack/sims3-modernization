---
name: ts3-track
description: Switch game test tracks cleanly (stop chain scripts and queue.ts, clear stale job-running/main.lock/holds/tail watchers, start the next chain) and open/close a build window. Use for "switch track", "stop the chain", "start chain X instead", "build window", "what is running", stale hold or job-running.
---
# Test track switch

From any checkout (works on the main checkout's `probe-out/queue`), Node >= 22.18:

```
node tools/harness/track.ts status                         # hold, job (pid alive?), TS3W.exe, chains, queues, main.lock, tail watchers; flags STALE
node tools/harness/track.ts stop --after-job               # hold, wait for the running job, stop chains + queue.ts, clean, release hold
node tools/harness/track.ts stop                           # same now: also kills the job's tree and TS3W.exe
node tools/harness/track.ts switch probe-out/queue/<chain>.sh [--after-job]   # stop, then start it detached (out: probe-out/queue/<chain>.out)
node tools/harness/track.ts build-window <reason>          # hold "build-window: <reason>", wait for the job to end, "ok to build"
node tools/harness/track.ts build-done                     # release the hold, only if it is a build-window hold
```

Prefer `--after-job` while a job runs (no lost run). After a hard `stop` that killed TS3W.exe, check the Game\Bin pair before the next run. `build-done` leaves other holds (Rodney's) alone; `stop` releases any hold, so read `status` first. Report the output as is.
