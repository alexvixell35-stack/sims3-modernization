# Harness: more to read when a job fails (2026-11-03, alexvixell35-stack)

Three gaps seen in tonight's failed and finished jobs on the old save.

## 1. Hang summary named only the wait

A world load hung on 2026-11-03 23:04 (`world load: nothing moved ... for 60 s`). The summary line was
`top frame ntdll.dll+0x7837c (5/5 same); 142 other thread stacks; ...`, which says only that the sim thread waits. Reading
`threadstack.json` by hand [measured]:

- the sim thread and 3 others waited under `Sims3SettingsSetter.asi+0x4c68c` (called from `TS3W.exe+0xe27a9`);
- the main thread waited under `ApexRadiance.asi+0x3d98ae` (`ntdll.dll+0x41e02` on top);
- 86 threads sat at `TS3W.exe+0xdaefe` (idle pool).

**Change** (`game-alive.ts` `captureHang`): `ownFrame` = the first frame outside the OS modules; the line adds
`under <frame>` for the sim stack, `main thread under <frame>`, and `thread frames` (the four largest groups by that frame).
Self-check: fixtures for all three, plus the healthy-line regex.

The hang's rerun loaded normally (same queue, 23:07), so its cause is not known; the next one now says which module waits.

## 2. Counters of a module without a `COUNTERS` entry were lost

`row.counters` keeps the counters of the job's ini sections only (`pickCounters`). A job of a module that has no
`COUNTERS` entry (a local `[FootstepRO]` build) passed, but its counters were in no file: profile runs without an A/B
write no `-ab-native.jsonl`.

**Change** (`queue.ts`): every job with a `/native` read writes `<queue>-<date>-<job>-<HHMM>-native.json`
(`{ before, after }`, the whole counter set) beside its machine file; `row.nativeFile` names it.

## 3. `wait-ab.ts` read the last run's slice table

`profile-world.ts` empties `<tag>-ab-native.jsonl` at the start of an A/B, but `<tag>-ab.json` is written only at the end.
A rerun with the same tag that stopped early (noise check) left the earlier run's table beside the new, cut snapshots,
and `wait-ab.ts` printed a table from the two mixed [observed 2026-11-03, discarded].

**Change** (`profile-world.ts`): the old `<tag>-ab.json` is removed at the start too, so a cut run reads "no ab.json".

## Checks

- `node tools/harness/game-alive.ts --self-check`: ok. `node tools/harness/queue.ts --self-check`: ok.
- `profile-world.ts` parses (type-stripped `node --check`).
