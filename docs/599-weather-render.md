# 599. Weather render cost: snow build-up and rain (2026-11-16)

Question: can snow building up on the ground and rain on screen be made cheaper? Measure first. This doc has the static
map of where weather is driven, the tools added to measure it, and the run script. Nothing is built natively; no fix yet.
Ghidra: `tools/ghidra/run-headless-linux.sh DecompileTo.java / XrefsTo.java / ScalarRefs.java` on the repack exe.

## 1. Script side (decompiled Sims3GameplaySystems / ScriptCore)

- [verified] `SeasonsManager.Simulate` is a threaded ScriptObject loop: each pass runs Progress, Temperature, Weather,
  TerrainCover and Tree managers, then `WeatherControl.SendSettings()`, then `Simulator.Sleep(0)`. So once per sim tick.
- [verified] `ScriptCore.WeatherControl.SendSettingsToWeatherControlHost` allocates a new `float[23]` each call and passes it
  as a SafeArray to the `WeatherControl_SetSettings` p/invoke (Sims3Common). The 23 values: precip type, strength, season,
  sunrise/sunset offsets, two audio indexes, terrain type, frost, snow thickness, road track depth, wet, leaf loss, fall
  set flag, fall colour, precip fog, pond freeze, heat shimmer, world leaves, snow melting flag, cloudiness, wind, fog.
- [verified] Snow and puddles on the script side are scalars. `TerrainCoverManager.Update` = `UpdateWet`, `UpdateSnow`,
  `UpdateFrost`, `UpdatePondFreezing`, `UpdateRegularPuddles`, `UpdateWeatherController`: float arithmetic on 0-100
  levels (`mSnowLevelValue`, `mWetLevel`, `mFrostLevel`, `mSnowTrackDepth`), then `SetTerrainCoverAmounts(level / 100 ...)`
  and `SetWeatherTerrain`. No per-cell or per-object snow work in script. The only world walk is
  `PuddleManager.RemoveAllPuddlesNotShelteredFromPrecipitation`, at most once per sim hour while puddles are disallowed
  (wet > 25 or snow > 0.01).
- Per-tick script cost is therefore small: one 23-float array (GC garbage, ~120 bytes per tick) plus one icall. [inferred:
  not timed; tick_stats below will show whether weather moves the tick at all.]

## 2. Native entry and consumer (TS3W.exe 1.69 repack)

- [verified] `0x7846E0` (the SetSettings icall): returns early without the host (`[0x11ECBC4]+0x74`), unmarshals the
  SafeArray, requires length 0x17, reads the current block (`0xD698D0`, copy of 23 dwords from host `+0x924` under the
  critical section `+0x980`), repacks the floats into the host's internal struct (type floor'd to int, flags from the
  two bool floats), applies the mode at `obj+0x3DC` (`0xC5FED0`), then `0xD69900` writes the 23 dwords to `+0x924`
  and sets dirty `+0x9A0 = 1`. No rendering work happens on the sim thread here.
- [verified] The consumer is `0xD6E350` (one caller, `0xC6D570`; takes a dt float): under `+0x980` it copies `+0x924` to
  `+0x9EC` when dirty and clears `+0x9A0`. [inferred] it runs once per frame on the render/main thread (dt argument,
  frame-side caller); not traced to the frame loop.
- Other readers of the block: `0xD68CA0` (via `0xC636D0`) and `0xD68790` (via `0xD68A70`) also copy it with `0xD698D0`;
  `0xD68CA0` walks objects within a distance and calls `0xD68BF0` per object. [inferred: per-object weather state such as
  wet/snow on objects; how often it runs is not traced.] OpenBarrio's function map (GPL, names only, docs/547) names
  `0x86DE70` `ts3::audio::b18impl::UpdateWeather`: one of the 7 callers of the mode getter `0xC5FED0`, so weather audio
  reads the same host. Their map has no render-side weather function.

## 3. Is snow on the terrain a shader parameter or a CPU mask?

- [verified] Shader parameter. `0xD69A30` (called from `0xD6E350` when the settings changed, `0xD6A2F0` compare, or when the
  indoor flag `+0xB05` flips) clamps snow thickness, wet, frost and road track depth to 0..1 into a static vec4 block at
  `0x11EE140..0x11EE1E4`, then `0xD69010` bumps the version of each entry in the global shader-parameter table
  `[0x11D7530]` (16-byte entries: counter, frame stamp, pointer to the vec4) and binds the weather textures
  (`0x799CB0` calls for the texture slots at `+0xA4..+0xF0`). The snow look is then GPU work in the terrain and
  object shaders. Updating it costs a few stores per change.
- [verified] There is one CPU-side 2D loop in `0xD6E350`: under `+0x9A8`, when the stored size `+0xB08 * +0xB0C` is 0, it
  reads two 16-bit textures (`[0x11EE2CC]`, `[0x11EE2D8]`, format 0x10) texel by texel (`0xAEFD60`) into a CPU map and
  stores its size. [inferred] a one-time load of a weather mask per world (size stays non-zero after), not per frame.
- So snow build-up has no per-frame CPU mask update. Any cost of snow on screen is GPU (terrain/object shader branches,
  extra texture samples) and, under Apex/Remix, whatever the bridge does with those shaders. [inferred until measured]

## 4. Rain and snow on screen

- [verified] Per frame while precipitating (type 1 or 5 with strength above a threshold), `0xD6E350` walks the lot list
  (`0xC68960(1)`, the manager's `+0x204` vector, rebuilt when `+0x1CC` is dirty) and, per lot with a `+0x364 -> +0x240`
  component, either ensures its precipitation effects exist (`0xBF0550`: does work only when its effect list is empty)
  or clears them (`0xBE9950`). O(lots) cheap checks per frame. A second walk applies threshold crossings
  (`+0x230 / +0x254 / +0x240` components) only when the new value crosses the stored flag.
- [verified] `0xD669D0` (called when `+0xBF4` is set, with dt) is the precipitation-volume update: a small grid search
  around the camera and a loop over 12 cells that creates (0x50-byte objects) or moves effect emitters, then per-emitter
  updates (`0xD661B0`). [inferred] the rain and snow particles are camera-local emitters; their CPU cost is bounded
  (12 cells), the likely cost is GPU particle fill and overdraw, plus the bridge's per-draw cost for many small batches.
- [verified] `0xD6A1E0` sets one global (`0x11EE1A0`) from the precipitation strength (fog/visibility factor).

## 5. What we can measure, and what was added

- Existing: `hitch_watch` (ts3agent) keeps only sim gaps >= min_ms, no averages. `[StallWatch]`, `[AnimPhase]`,
  `[HitchAttr]`, `[MemDiag]` are sim-stall samplers, animation timers, route-wait attribution and memory lines; none
  writes a frame-time or tick-time average or percentile to ts3native.log. `tools/harness/frame-times.js` (docs/162) has
  per-presented-frame times (hook `0xEC9F00`, main thread) but was only loaded by profile-world.ts / tests-be.ts through
  `frida_tools.repl`, which the Wine Python on the Linux setup does not have.
- Added (script side, no native build):
  - ts3agent `tick_stats` (HitchWatchHandlers.cs): every sim-iteration interval into 0.1 ms bins; `{"start":true}`,
    `{}`, `{"reset":true}` (window stats then clear), `{"stop":true}` -> ticks, ticks/s, avg, p50, p95, p99, max, sim minutes.
  - ts3agent `weather` (HarnessHandlers.cs, live mode only via GateHandlers):
    `{"set":"clear|rain|heavyrain|snow|blizzard|hail|fog","hours":N[,"season":"winter"]}` through
    `SeasonsManager.ForceRequestWeather / ForceRequestRainWithIntensity / ForceRequestSnowWithIntensity` (Moderate for
    rain/snow, Heavy for heavyrain/blizzard); `{"get":true}` -> weather, forced, intensity, temperature, season, snow
    level, terrain type, road wet, puddle block, the 0-100 levels and the floats last sent to the renderer.
  - `tools/harness/frames-attach.py`: loads frame-times.js with the plain frida module (Wine Python) until a stop file.
  - `tools/harness/weather-stats.ts <run>`: per segment frames n, fg %, avg, p50, p95, p99, max, >= 50 ms, plus the
    tick_stats columns. `--self-check` covers the percentile math.
  - `tools/harness/weather-ab.sh` (copy of the run script): drain asi + ab-tp ini keys, one Bridgeport session, camera on
    the home lot, normal speed, winter, clear -> rain -> heavyrain -> snow -> blizzard -> clear, 6 min each.
- Native note (not built): a `[FrameStats]` section in ts3native could time `0xEC9F00` itself (QPC at entry, histogram,
  one log line per N s with avg/p95 and a `mark` hook), which avoids the second Frida session. Worth it only if
  frames-attach.py proves unreliable next to the probe.

## 6. Reading the run

- Sim tick: weather segments vs the first clear. Expect no difference (script side is scalar); a rise would point at
  `SimUpdate.CalculateSimEnvironmentScore` shelter tests or the 10-minute rain shelter pass (docs/594).
- Frames: rain/heavyrain/snow/blizzard vs clear = falling precipitation (particles); last clear vs first clear = snow on
  the ground (shader). If the frame delta is real and the main-thread stages of frame-times.js (drawMs, presentMs) carry
  it, the cost is render/bridge side; options then are lower emitter density (12-cell volume, `0xD669D0` tuning) or a
  cheaper snow shader path, both GPU-side, not sim-side.

## Measured (2026-11-16, Bridgeport, winter, Remix bridge, screen on, 2.5 min per segment) [verified]

| segment | frames avg / p95 / p99 ms | frames >= 50 ms | sim tick avg / p99 ms | state at end |
|---|---|---|---|---|
| clear | 21.0 / 17.7 / 20.0 (avg includes the first 1 s frame) | 25 | 33.4 / 36 | - |
| heavy rain | 16.7 / 18.4 / 23.1 | 11 | 33.3 / 35 | wet 0.73, precip 0.97 |
| blizzard | 16.7 / 18.0 / 22.4 | 9 | 33.4 / 36 | snow 0.27, precip 0.99 |
| clear, snow on ground | 16.7 / 20.0 / 23.3 | 16 | 33.3 / 37 | snow 0.30 |

Weather costs no sim time (ticks stay frame-paced at 30/s, 33.3 ms) and only ~2-3 ms at the frame p95/p99; 60 fps holds in every segment. Rain, snow particles and ground snow are not a performance problem on this setup; nothing to build. The 21:05 crash (forced rain at 15 MB free address space with 73 needy Sims) did not repeat at normal load.
