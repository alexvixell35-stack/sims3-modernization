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

## C2b: `SiteCLoopJump=1`, A/B `--ab idlecheckc2b` (C2 on in both arms)

Same save, seed and setup as above (ThumbPose on). Waits per arm are probe P0's keyed sim-thread waits (`blockKey` /
`blockUs`) between the slice snapshots, warm-up slice left out; the same reading gives the C2 rows above.

| Job | jumped | site C `0x755990`, ms/s off vs on | all keyed sim waits, ms/s | arm sum, ms/s | stuck Sims |
|---|---|---|---|---|---|
| c2b-ab | 188 | 3.9 vs 0.0 | 29.9 vs 24.1 (-19%) | 87.6 vs 70.9 (-19%) | 11 |
| c2b-ab-b | 207 | 18.5 vs 0.0 | 68.0 vs 31.5 (-54%) | 88.8 vs 70.9 (-20%) | 3 |

- The site C wait left after C2 (the calls a footprint decides) is gone: 0.0 ms/s in both on arms.
- The off arm of c2b-ab-b had a burst of waits (goal validation `0x75A64C` 18.8, `0x833697` 13.0 ms/s), so its -54% is
  partly that burst. Goal validation is flat on the quiet run (9.8 vs 9.2).
- The arm sums differ mostly in animPose (-11.5 and -10.3 ms/s), which C2b does not touch. Read them as noise (single-run
  arm sums move about 10% with nothing changed); the keyed waits are the measure.
- Stuck Sims: 11 and 3. Other runs on this save gave 1 to 20 with unrelated switches.
- Soak: 35 min with C2b on the whole time, PASS. 2,050 jumps; the loop ran 8,594 times, took 6,544, 0 doubts.

## Crash during testing

`c2e-ab-b` (same build, ThumbPose off) died after about 8 minutes: crash W (docs/67 §27), the main thread's Sim thumbnail
render reading a released pose at `0x54E79A`, an EA bug that also happens in vanilla. The stack held only TS3W addresses.
It was the first crash W in this machine's runs. The next two jobs ran with `[EaCrashFixes] ThumbPose=1` on both arms
(`tpHeld` 186 and 438, `tpLastRef` 0) and passed.

The first C2b build crashed every job 3 to 10 s into play: an access violation in the IC loop at `0x7559AC`, the vector's
end holding `RcLeaveThunk`. It crashed with `SiteCLoopJump=0` too, and the C2 build passed on the same setup. The cause was
`[RouteAsync] RefcountGuard`. `StubKey` keys the call from IcStub by the IC's own site, so RcGuard took it for the listed
caller and used the query's saved ebp as that caller's frame. IcStub omits the frame pointer, and in that build it kept
the vector pointer in ebp. So the leave thunk was written into the vector's end. Fixed in `1ffa846`: a query under a
call-site stub passes through (rcPass[1]). The runs above are on the fixed build.

## Checks

- `native/ts3native/build.cmd`: asi and selftest ok (`TestIdleCheckC`: the loop cases, kind 9, the frame check, the job path,
  the shadow compare).
- `node tools/harness/native-switches.ts --self-check`: ok (100 offsets).

## Open

- Goal validation (`0x75A64C`) is now the next wait; its loop is larger (an AddRef per element, a 23-way type switch,
  geometry), so the same approach does not carry over cheaply.
