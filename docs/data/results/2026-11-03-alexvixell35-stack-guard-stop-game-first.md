# Harness: a fail-fast guard stop closes the game before the child (2026-11-03, alexvixell35-stack)

The noise, routing and early-gate guards in `queue.ts` `run` stopped a job with `killTree(p.pid)` (`taskkill /T /F`) while
the game was still running. For a profile job the child is `profile-world.ts`, which holds Frida sessions inside the game
(the stutter profiler, and the `frame-times.js` CModule hook). Force-killing it tore those sessions down from outside,
and the game crashed.

## Seen

Six noise stops on the old save on 2026-11-03 21:20-22:03 (a game client was running beside the queue, so every A/B job
failed `noisy machine early`) [measured, queue rows]:

| queue | job | `crashed` | crash triage |
|---|---|---|---|
| roall-ab-oldsave | 1 | false | |
| roall-ab-oldsave | 2 | true | no crash report with the run's pid |
| roall-ab-oldsave | 3 | true | `ACCESS_VIOLATION writing to address 0x00593600 ... PAGE_EXECUTE_READ at 0x9bd4ad50 in frida-agent.dll` |
| fs-roall | 1 | false | |
| fs-roall | 2 | true | no crash report with the run's pid |
| fs-roall | 3 | false | |

Two crashed jobs in a row also stopped the queue ("two jobs in a row crashed").

## Fix

`stopGameFirst()` before `killTree` in the three guard branches: the game is killed first (the Frida sessions detach
with it), and a game the guard closed is not counted as a crash (`guardClosed`, reset with the row).

## Checks

- `node tools/harness/queue.ts --self-check`: ok.
- One-job queue with an early gate that always fails (`asyncReady <= 0` from 30 s, profile job, Frida sessions attached),
  2026-11-03 22:39: `FAIL: early gate: ... child stopped` at 30 s, game closed, `crashed: false`, no crash report, no
  Windows application error event; Bin and Mods restored [measured]. One check only: the crash rate before was 3 of 6.
