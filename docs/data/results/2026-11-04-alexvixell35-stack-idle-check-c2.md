# Idle check site C2: the IC's footprint loop on the clone (2026-11-04)

Machine: Steam 1.67, contributor alexvixell35-stack. Save "1111 kit5" (old save), seed 12345, 10 min per job, in-load A/B
(`--slice 2`), `-disablestoryprogression`. Base on both arms: the final setup of
`2026-11-04-alexvixell35-stack-route-waits.md` (the eleven route pieces, DestroyPark, DestroyParkDx, the route-to-object
tail, FootstepRO MissNear=1 MissLast=0) plus `[IdleCheckRO] SiteC=1 SiteCMode=1`. Build: this branch (counters v905), on PR #254.

## What it does

After the three pieces of #254, idle check site C (`0x755990`) was the biggest sim wait left (about 18 ms/s on). Its call at
`0x75598B` almost always finds footprints near the point, so C1 (empty answers only) answered 0.46% of calls. C2 runs the
idle check's own loop (`0x7559A0..0x755A4C`) on the clone's footprints inside GoalValidRO's QL shared hold. When no footprint
decides (answer 0), the vector stays empty and the IC returns 0 without the game's call. Any other answer, or a doubt, calls
the game. Every call in the loop is a read [verified disassembly]; details in docs/195 §8.

## Shadow (no job: the loop on the game's list and on the clone, compared)

| Round | compares | mismatches | code diffs | doubts | answer 0 |
|---|---|---|---|---|---|
| 1 | 598 | 0 | 0 | 1,132 | 426 (71%) |
| 2 (kind 9 added) | 1,737 | 0 | 0 | 0 | 1,514 (87%) |

Round 1's doubts were all one kind getter (`0x624E00`, kind 9) missing from the table; the doubt log (reason codes, the first
24 with their slot targets) found it.

## A/B `--ab idlecheckc2` (round 2 build)

| Job | runs | taken | site C `0x755990`, ms/s on vs off | all keyed sim waits, ms/s | arm sum, ms/s | stuck Sims |
|---|---|---|---|---|---|---|
| c2e-ab | 1,268 | 1,028 (81%) | 4.7 vs 13.7 | 23.9 vs 30.7 (-22%) | 81.6 vs 76.0 (+7%) | 5 |
| c2e-ab-c (ThumbPose on) | 1,135 | 936 (82%) | 3.8 vs 15.5 | 23.6 vs 32.1 (-26%) | 73.3 vs 88.7 (-17%) | 8 |
| c2e-ab-d (ThumbPose on) | 1,247 | 994 (80%) | 6.0 vs 18.8 | 24.3 vs 39.0 (-38%) | 73.3 vs 85.9 (-15%) | 1 |

- Not taken: 0 doubts; 240, 199 and 253 calls decided by a footprint (mostly 0x10, a footprint of type 1), which call the game.
- Part of the wait moves to goal validation (`0x75A64C`, 7-8 off, 8-11 ms/s on), but the keyed total falls in every run.
- Cost: about 9 us per taken call (clone and loop).
- Hitches (`hitch-ab.ts`): no on-arm hitch >= 300 ms; p99 62-66 ms on vs 71-77 off.

## Crash during testing

`c2e-ab-b` (same build, ThumbPose off) died after about 8 minutes: crash W (docs/67 §27), the main thread's Sim thumbnail
render reading a released pose at `0x54E79A`, an EA bug that also happens in vanilla. The stack held only TS3W addresses.
It was the first crash W in this machine's runs. The next two jobs ran with `[EaCrashFixes] ThumbPose=1` on both arms
(`tpHeld` 186 and 438, `tpLastRef` 0) and passed.

## Checks

- `native/ts3native/build.cmd`: asi and selftest ok (`TestIdleCheckC`: the loop cases, kind 9, the frame check, the job path,
  the shadow compare).
- `node tools/harness/native-switches.ts --self-check`: ok (100 offsets).

## Open

- C2b: a jump at `0x755990` to the IC's return label would also skip the game for the 20% that a footprint decides
  (docs/195 §8).
- Goal validation (`0x75A64C`) is now the next wait; its loop is larger (an AddRef per element, a 23-way type switch,
  geometry), so the same approach does not carry over cheaply.
