# 280. Camera sweep for camera-stutter profiling (2026-11-17, built + selftest, NOT RUN)

Step 0 of the docs/278 proof plan (§6) needs a camera script that moves the way players move it: zoom and rotate as well
as pan. Apex's own camera-pan profile (sent 2026-11-17: Sunset Valley, pan only at 40 m and 150 m, Developer build with the
frame profiler on) found that the game's lot room solve `0x006A8BA0` (called at `0x00ADB9AD` from the lot lighting update)
leads 81% of the moving hitches. They did not test zoom or rotation. This doc adds a fixed sweep that both projects can run
under our harness, with the same profiler where the build allows it.

## 1. Agent command `camera_sweep` (ts3agent, `CameraSweepHandlers.cs`)

- `camera_sweep {lots?, minDist?, maxDist?, pitchDeg?, panHeight?}` (live mode only, as are stop and status) starts the sweep and returns at once. It moves only the
  camera, through `CameraController.SetPositionAndTarget` (the same call as `camera_pan`). It changes no Sim, lot, object or
  save. It needs no household exemption, because nothing it touches belongs to a Sim.
- The busy area is the `lots` (default 8) lots around the densest lot (`DenseLotPath`, the same path as `camera_pan`). The
  orbit centre is their mean, at terrain height.
- The schedule is fixed and runs by wall clock, in a game task that sets the camera every tick:

  | Segment | Duration | Motion |
  |---|---|---|
  | `still_start` | 5 s | Still at mid zoom |
  | `zoom_smooth` | 30 s | Mid, then `maxDist` (default 180 m), then `minDist` (default 5 m), then back to mid. A sine on a log scale |
  | `zoom_steps` | 30 s | 12 jumps, one every 2.5 s, over 6 levels spaced on a log scale: max to min to max, then mid |
  | `rotate` | 30 s | One 360° orbit at mid zoom |
  | `rotate_fast` | 20 s | Two orbits |
  | `still_pan` | 5 s | Still at the start of the pan |
  | `pan` | 50 s | Through the lots and back, at `panHeight` (default 25 m), as `camera_pan` does |
  | `still_end` | 10 s | Still, to measure the backlog once the camera stops |

  Mid zoom is sqrt(min x max), which is 30 m with the defaults. The pitch is 40° above the horizon.
- The first reply holds the plan: `epochMs0`, `tickMs0`, and each segment's offset and length.
- `{status:true}` returns the segments run so far. Each segment carries:
  - `epochMs0` / `epochMs1`: the same clock as `frames.csv` `epochMs`.
  - `tickMs0` / `tickMs1`: `Environment.TickCount` as uint32, which aligns with Apex's `FrameTimeline.csv` `tick_ms` [inferred, §500].
  - `sets`: how many times the camera was set.
  - `distEnd`: the distance from camera to target at the segment's end, which shows the zoom the game actually allowed.
- `{stop:true}`, `camera_pan` or `camera_jumps` ends a running sweep, because they share the same generation. A segment
  that ends early is marked `cut`.
- Build `ts3agent.package.camera-sweep-1117`, sha256 `77afb2d5458702bee676f21e283ea3dde25bc0701573f2093d8393cfbfd581ef`
  (docs/data/builds.json).

## 2. Repro, report and queues

- Repro `camera-sweep` (`tools/harness/repros.json`): a 60 s lead-in, `camera_sweep {}` with a 182 s wait, `{status:true}`,
  then `{stop:true}`. It runs in a 5-minute profile window.
- `profile-world.ts` runs `camera-sweep-report.ts` (which has `--self-check`) when a repro step is `camera_sweep`. The report
  writes `<tag>-camsweep.json` and a table. For each segment it gives:
  - frames, p50, p99, p99.9 and max frame ms;
  - counts of frames over 25, 50 and 100 ms;
  - `apexDefHitches`: Apex's hitch rule applied to our frames, frame > max(2 x median of the previous 120 frames, 8 ms).
- The pass line is `PASS: camera sweep: 8 segments`: every segment closed from a status reply, none cut, frames in every
  moving segment.
- Rerun by hand with `node tools/harness/camera-sweep-report.ts --tag <tag> [--apex-timeline <csv>]`.
- Queue `camera-sweep-1117.json` (measurement + correctness):
  - Jobs `bp-camsweep` and `ip-camsweep` on the master-1116c set, Bin state `apex`, `scriptErrorsMax: 0`.
  - Version and Apex compat are read back (regexCases taken from real lines).
  - No timing A/B: one arm. A quiet machine matters only for the absolute numbers.
- Queue `camera-sweep-apexprof-1117.json`: the same two jobs with `apexProfiler: true` (§3).
- Both queues were dry-run. Neither has been run.

## 3. Apex frame profiler from the harness

Read on Apex `origin/main` (`features/frame_profiler.cpp`, `docs/features/frame-profiler.md`, `build_flavor.h`,
`apex_config.cpp`) [verified]:

- **Settings.** The profiler reads `Documents\Electronic Arts\The Sims 3\Apex Radiance\ApexRadiance.toml`, table
  `[qol.frame_profiler]`. `enabled` is always forced back to false at load. For scripted runs, `start_at_load = true` applies
  the Timing run preset when the settings load. There is no hotkey; otherwise it is started from the ImGui menu (Developer >
  Performance).
- **Developer mode.** `[ui] developer_mode = true` is needed first, and changing it needs a restart.
- **Build flavour.** The public release is built with `APEX_NO_DEV_TOOLS` (`kDevToolsBuild = false`), and it refuses
  developer mode. **The profiler therefore runs only in a Developer build of Apex.**
- **Output files**, in the same folder:
  - `ApexRadiance_FrameTimeline.csv`: one row per frame, flushed every second. Its columns include `tick_ms`
    (GetTickCount64), `hitch` (their rule), `camera`, `room_solve_calls`, `room_solve_max_ms`, and per-category self ms with a
    `Lot room solve` column.
  - `ApexRadiance_Hitches.txt`: hitch blocks, appended. It never shrinks.
  - The Apex log line `[FrameProfiler] Timeline section N starts ... time_s 0 = tick_ms T`.
- **Hitch reports.** `report_every_s` writes a report every N s, but it also runs a Clear, which starts a new timeline file.
  We leave it at 0 and split the per-frame timeline by our segment tick markers, so every hitch block falls in exactly one
  segment.

What we built:

- **Queue key `apexProfiler: true`** (queue.ts).
  - For the job it sets these keys in `ApexRadiance.toml`: `ui.developer_mode`, `qol.frame_profiler.start_at_load` and
    `write_file`. It puts the original back after the job, reads it back, and keeps a copy in `probe-out/queue/bin-before`.
  - It keeps the job's part of `Hitches.txt`, measured from its size at launch.
  - It logs whether the profiler ran: `ran` when the Timeline section line is in the job's Apex log, otherwise `did NOT start`.
- **Timeline per segment.** `profile-world.ts` copies `FrameTimeline.csv` to `<tag>-ApexRadiance_FrameTimeline.csv` when the
  file was written during the window. The report then splits it by `tick_ms` and adds these columns per segment:
  - `apexFrames` and `apexHitches`;
  - `roomSolveCalls`, `roomSolveMs` and `roomSolveMaxMs`;
  - `hitchesLedByRoomSolve` and `roomSolveLeadPct`, the hitch frames whose largest category column is the lot room solve.
    This is their "top counter" measure.

What is missing, and what we need from Apex's author:

1. A Developer build in Bin, either from him or built by us from the Apex source. With the public release the profiler
   cannot start; the job log says so, and the sweep data stays valid without it.
2. Confirmation that `start_at_load` plus `developer_mode` in the toml, with no menu input, is enough in his build.
3. Whether `tick_ms` is written as the full 64-bit value. We compare it modulo 2^32.

He can run `camera-sweep-apexprof-1117.json` on his own setup as it stands.

## 4. Inference

§500 (docs/51): `Environment.TickCount` in the game's Mono equals the low 32 bits of `GetTickCount64`, so the segment
`tickMs` values line up with Apex's `tick_ms`. Check: on the first apexprof run, the `still_start` rows should show
`camera` = 0, and `zoom_smooth` rows should show `eye_target_m` changing.
