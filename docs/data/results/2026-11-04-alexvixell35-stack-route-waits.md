# Route-job sim waits: footstep cache, route-to-object tail, idle check site C, DestroyParkDx (2026-11-04)

Machine: Steam 1.67, contributor alexvixell35-stack. Save "1111 kit5" (old save), seed 12345, 10 min per job, in-load A/B
(`--ab <switch> --slice 2`), `-disablestoryprogression`. Base on both arms: the eleven route pieces of the combo rounds
(SlotEnabledRO, SteerRO, AvoidRO, Gate0RO, RouteToObjRO, PlacementRO, IdleCheckRO, LotFrontDoorRO, GoalValidRO,
ObstructedRO, DeferFootprint 1+2) with RouteAsync H2 / TailUnlock / ReplanDedupe and DestroyPark. Build: main b1d6b3c plus this
branch (counters v901 footstep, v902 tail, v903 site C, v904 DestroyParkDx).

Rodney's rule (2026-11-02): judge by summed sim-thread ms/s (`arm-ms.ts`), hitches only as a guard (`hitch-ab.ts`). One run's
arm sum moves by about 10% with nothing changed: a job whose switch never acted (FailMemo, fmRep 0) read -11%, and the site C
shadow job, which cannot help, read -6%.

## Results

| Job | A/B switch | Key wait, ms/s (on vs off) | All keyed sim waits, ms/s | Arm sum, ms/s |
|---|---|---|---|---|
| fs-near | `footstepro` (MissNear=1, MissLast=1) | gate 12 F-F `0x62FB74` 1.0 vs 13.1 | 36.4 vs 42.6 | 78.9 vs 101.5 (-22%) |
| stack-fs-nolast | `footstepro` (MissNear=1, MissLast=0) | gate 12 F-F 2.6 vs 16.0 | 34.8 vs 45.4 | 73.5 vs 106.2 (-31%) |
| rtotail | `routetoobjtail` | `0x83156D` 0.2 vs 10.2 | 39.7 vs 38.9 | -3% |
| stack-icc-shadow | `idlecheckcshadow` | (none) | see the key bug below | 76.4 vs 81.5 (-6%, noise) |
| stack-icc | `idlecheckc` | site C answered 3 of 2,080 calls | see the key bug below | 83.9 vs 89.0 (-6%, noise) |
| stack-tail-icc | `routetoobjtail,idlecheckc` | `0x83156D` 0.1 vs 11.5 | 34.6 vs 32.2 | 77.8 vs 93.1 (-16%) |
| dx-a | `destroyparkdx` | destroy #4 `0x2000003` 0.4 vs 13.9 | 41.7 vs 45.3 | 80.2 vs 88.8 (-10%) |
| dx-b | `destroyparkdx` (repeat) | destroy #4 0.9 vs 11.7 | 33.5 vs 35.6 | 75.9 vs 74.4 (+2%) |
| new3 | `destroyparkdx,routetoobjtail,footstepro` | F-F 0.5 vs 14.5, `0x83156D` 0.1 vs 7.9, destroy #4 0.3 vs 10.1 | 31.3 vs 47.0 (-33%) | 78.5 vs 91.8 (-15%) |
| final-a | the same three, MissLast=0, site C off | F-F 3.4 vs 16.5, `0x83156D` 0.0 vs 9.8, destroy #4 0.3 vs 9.6 | 42.5 vs 54.4 (-22%) | 76.6 vs 126.7 (-40%) |
| final-b | repeat of final-a | F-F 3.2 vs 17.4, `0x83156D` 0.2 vs 8.9, destroy #4 0.4 vs 8.2 | 42.4 vs 50.7 (-16%) | 77.6 vs 98.7 (-21%) |

Guards: every job PASS (no crash, early gates `p5cCountLow` and `fp2ApplyInJob` 0). Shadows: route-to-object tail 13,062
compares, 0 mismatches; site C 1,747 compares, 0 mismatches. No hitch regression (`hitch-ab.ts`: no on-arm wait >= 500 ms; max
on 186-426 ms vs off 277-321 ms). Stuck Sims per job (both arms pooled) 1, 3, 8, 15, 2, 2, 5, 3 and 0, inside the 1-34 spread of the earlier
old-save rounds.

## Footstep cache (`[FootstepRO]`, counters v901)

- MissLast=1 run: job calls 973: 590 cell hits, 210 neighbours, 173 last surface, 0 to the game.
- MissLast=0 run: job calls 1,308: 831 cell hits, 342 neighbours, 135 to the game (10%).
- Wrong surface (checks with no job, against the game's answer): cell 1.1-1.7%, neighbour 5.2-6.9%, last surface 28.9-30.2%.
- So MissLast=1 saves about 1.6 ms/s of F-F wait and plays another surface's sound on about 5% of job steps. MissLast=0 gives
  about 2%. Recommendation: MissLast=0. Defaults stay off.

## Route-to-object tail and site C: the wait moves

Removing one +0x918 taker moves the sim's wait to the next taker: `0x83156D`, then `0x755990` (idle check site C), destroy #4
`0x2000003`, then `0x82F4AB`. Site C1 (empty answers only) does not pay: only 0.46% of the shadow's answers were empty, because
the idle check almost always finds footprints near the point. It stays off. Only C2 (non-empty answers under QL shared) could
pay, and it needs the footprint refs the idle check's loop reads.

## Harness bug: stub waits keyed by an asi address

With site C installed, the idle check's wait was keyed by `IcStub`'s own return address in ts3native.asi, not by
`0x755990`. That address changes every run and differs per call instruction. The 32 P0 key slots fill first-come, so the off
arm's key got no slot: its wait went to the untimed `blockOther` (262 vs 143 per min), and the off arm's keyed total read about
11 ms/s low. Fix: `StubKey` in `GvStub` and `IcStub` keys the outermost ObstacleQuery wait by the game site (`0x755990`,
`0x75A64C`), as AvSite does with `0x846D21`; both keys are seeded. Selftest: `TestSeedWaitKeys` and the RT_STEER key test.

## DestroyParkDx (`[Discourage] DestroyParkDx`, counters v904)

Destroys whose object id has queued discourage records (D-X) park too, instead of waiting for the job to apply the records first.
The records stay queued, and the next drain applies the gone-object rules: Remove, usf disable and pf removes are swept by id;
Add, usf enable and phase-2 updates are dropped; a reused id is dropped (`discIdMismatch`). Opt-in: `DestroyParkDx=0` by default,
needs `DestroyPark=1`. Design and safety: docs/88 §11.

- Per arm, about 240 s each, dx-a / dx-b: 802 / 925 such destroys parked; 374 / 318 kept waits on the off arm.
- Dead-id sweeps 236 / 182, 105 / 62 of them removed a footprint. Phase-2 updates dropped for gone objects about 900 per arm.
- `discIdMismatch` 0, `discDeadKeyMap` 0, `p5cCountLow` 0. Stuck Sims 2, 2 and 5 (new3).
- Tripwire T-D5 with DestroyParkDx on is `discDeadSweep = discDropRemove + usfGoneEnable0 + pfSwept`; it held in every arm.
- The D-X drain wait rises 1.2-1.5 ms/s (forced drains about double).

Alone, DestroyParkDx removes destroy #4 but most of the wait moves to site C and goal validation: keyed total -2 to -4 ms/s,
arm sums inside the noise. With the footstep cache and the tail (new3) the three together cut all keyed sim waits by a third.
The biggest wait left is idle check site C (`0x755990`, 19.5 ms/s on), then goal validation (`0x75A64C`, 7.4 ms/s).

The recommended setup (final-a, final-b: MissLast=0, site C off) gave -16% and -22% keyed sim waits and -21% and -40% arm
sums; with new3 that is three samples of the three pieces together, arm sums -15% to -40%. MissLast=0 leaves about 3 ms/s of
F-F wait (0.5 with MissLast=1). The moved wait shows at site C (8-9 off, 18-19 ms/s on) and goal validation (2 off, 6-9 on).

## Checks

- `native/ts3native/build.cmd`: asi and selftest ok (`TestIdleCheckC`, `TestDestroyPark` case 8, `TestSeedWaitKeys`, RT_STEER key).
- `node tools/harness/native-switches.ts --self-check`, `wait-ab.ts --self-check`, `queue.ts --self-check`: ok.
- `node tools/hook-sites.ts --check`: 921 addresses, 0 not in docs/68. `node tools/check-docs-code.ts`: ok.
