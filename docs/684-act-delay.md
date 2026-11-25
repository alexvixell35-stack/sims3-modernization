# 684. Act delay: where a Sim's time goes between "queued" and "acting"

2026-11-20 / 2026-11-21 (UTC). **This repo holds §9 only** (ts3native `[OutfitFirst]` and its results). §1-§8 and §10
describe the contributor's read-only ts3fixes probe `actdelay` (`ActDelay.cs` / `ActDelayCore.cs`), which is not part of
this repo; the measurements in §9.1 were taken with it on. Section numbers are kept so references from the contributor's
notes still match.

Goal: a Sim that was given, or picked, an interaction should not visibly stand and wait before it starts. The probe found
one wait with a native cause in the order of work, not in EA's design: the outfit build queue (§9).

## 9. The outfit build queue: `[OutfitFirst]` (ts3native, default off)

**Where the swap waits** [verified: Ghidra decompile + objdump of the Steam 1.67 code, analysis copy]. Script side:
`SwitchOutfitHelper.Start` -> `Sim.SwitchToOutfitDelayed` -> `CASUtils.SetOutfitInGameObjectDelayed` (IC `0x5A9E90`);
`Sim.SwitchToOutfitWithoutSpin` reaches the same call. Native side:

1. `0x5ADD40` (the swap message, id `0x30290F0`) re-posts itself while the outfit's resources load or the Sim has no
   animation instance (docs/224 §5), then posts `0x6A4479F1` with the outfit object's provider interface (object `+0x28`)
   and the callback.
2. The model manager's handler `0x703CB0` -> `0x700150` -> `0x6F4910` -> `0x6DD160` (thiscall on the model proxy,
   `ret 0Ch`): stores callback and provider in "DelayedSwapParams" (proxy `+0x44`) and calls the provider's attach
   (`+0x18` = `0x5E6720`) with the Sim's scene object id.
3. The proxy's update `0x6DC5B0` asks the provider (`+0x10` = `0x5E4EB0` -> SimService `RequestSim 0x5EE910`) every step
   until the LOD is built, then runs the callback, which is the helper's `Ready`.
4. `RequestSim` puts a build job in the SimService's list; one job is built at a time, and when it is done `0x5EDB30`
   takes the ready job whose scene object is nearest the camera (thumbstall.cpp header). Its one call site is `0x5F12A5`.

So the swap's job competes by camera distance with every Sim LOD build. A Sim that changes clothes away from the camera
(a household Sim on another lot or floor, any townie) stands in `SwitchOutfitHelper.Wait` (`LoopIdle`) behind every
nearer Sim's build. That order is a choice of EA's that fits LOD builds; for a swap a script task is waiting on it is a
priority inversion, not a tuning value.

**What `[OutfitFirst]` does** (`thumbstall.cpp`, core `outfitfirst-core.h`):

- The call of `0x6DD160` at `0x6F492C` (18 bytes checked at `0x6F4926`) goes through `OfSwap`, which notes the
  provider's object when the provider's vtable has the RequestSim wrapper at `+0x10`, and calls the original with the
  same arguments.
- The pick's one call site (`0x5F12A5`, shared with `[ThumbStall]` Action 3 through `TsPickInstall`) runs `TsPick`:
  `Mode=1` calls the game's pick and files what it took (a noted object's job: how long it sat ready and how many jobs
  were built before it); `Mode=2` takes the oldest pending swap's ready job first, unlinked as the game unlinks its own
  choice. `[ThumbStall]`'s own pick still comes first.
- Same jobs, same builds, another order; the swap, its callback and the script side are untouched. Nothing is skipped,
  faked or cut, so every Sim is covered, household included (the visible result is the same change, sooner). A noted
  swap with no job after `MaxAgeMs` (60 s) is dropped: its LOD was cached or the swap was cancelled. 32 swaps at most
  are held (the oldest is pushed out).
- Not built: starting the build when the interaction is queued. The only script way to build without applying is a
  second `SwitchOutfitHelper.Start`, and EA's own helper then aborts ours (the build restarts) or loses its callback
  while ours is pending (docs/224 §5, the one-callback table); not exact for a household Sim.

Ini (`ts3native.ini`): `[OutfitFirst] Enabled=0` (1 = install), `Mode=1` (1 measure, 2 build first), `LogMs=100`,
`MaxAgeMs=60000`. No runtime switch, no counter version, no exported block: log lines only.

Lines: `OK   outfit first ...`, `outfitfirst: swap job taken first | taken by the game's order N ms after the swap was
set up; ready for N ms, the game built K other jobs meanwhile; ...` (from `LogMs` ready, or passed over), and a summary
once a minute: `outfitfirst: mode N, swaps noted N (again, pushed out, no job), jobs taken first N, by the game's order N,
other jobs built past a ready swap N; ready ms in all N (longest N), set-up to build ms in all N`.

Checks: Linux host test of `outfitfirst-core.h` (`outfitfirst-host-test.cpp`, clang `-m32`, exit 0);
`TestOutfitFirstCore` (`outfitfirst-test.h`) and `TestOutfitFirstPick` (`thumbstall-test.h`: the swap hook's argument
pass-through, off / Mode 1 / Mode 2 on a fake ready ring, `[ThumbStall]`'s pick first) in the Windows selftest.
EA 1.69 and retail 1.67: three rows (`0x5E4EB0`, `0x6DD160`, `0x6F4926`) are unresolved (0), the feature logs SKIP
there.

Repro `outfitfirst-check`: the agent's `outfit_change` (EA's `SwitchToOutfitWithoutSpin`, the same delayed swap) on the
household and then on townies over the town, with the job ini at Mode 1 (off arm) or Mode 2 (on arm).

### 9.1 Results (natural play, Isla Paradiso) [measured]

Contributor rig: Linux, Wine + DXVK. Isla Paradiso, 6-minute calm natural-play soaks with the `actdelay` probe on (it
times every `SwitchOutfitHelper.Wait`). Single runs per arm; the two arms of a pair run the same save and keys.

| pair | run | mode | time swaps sat ready (longest) | other jobs built past a ready swap | outfit wait, all Sims p50 / p90 | household max | selected max |
|---|---|---|---|---|---|---|---|
| 1 | s233 | 1 (measure) | 6,327 ms (2,607) | 8 | 480 / 1,408 ms | 1,449 ms | 994 ms |
| 1 | s234 | 2 (first) | 3,988 ms (1,570) | 0 | 176 / 1,152 ms | 804 ms | 97 ms |
| 2 | s236 | 1, `[JobYield]` on | 7,255 ms (2,967) | | | | |
| 2 | s237 | 2, `[JobYield]` on | 2,472 ms (1,660), **-66%** | | | | |

- Hitches (frames >= 300 ms): pair 1 0 / 2, pair 2 3 / 1; they swap sides between pairs and are sim-CPU frames, not
  outfit builds: no link to the mode.
- The ready time left in Mode 2 is a swap whose job became ready while another swap's job was being built (one build
  at a time); the rest of the outfit wait is the build itself (resource load + model build, docs/42 §4), which this
  feature does not touch.
- Verified: the call chain and the pick site (Ghidra + objdump); the order change in the log lines (Mode 2 `taken
  first`, 0 jobs past a ready swap). Inferred: the gain on other worlds (not measured).
