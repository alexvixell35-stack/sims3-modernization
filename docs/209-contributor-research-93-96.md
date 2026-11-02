# 209. Contributor research #93-#96: verified and actionable

Four research issues from a contributor (GitHub #93 Mono offload read-only methods, #94 unseen-Sim throttle map, #95 CAS/catalog/STBL memory, #96 streaming for off-camera objects). This note spot-checks the key claims against our docs, the ILSpy decompile of the 1.67 script DLLs and the Steam `TS3W.exe` bytes, and lists what we act on. Reply drafts: `docs/data/upstream/issue-9{3,4,5,6}-reply.md` (not posted).

Tags: **[verified]** = checked by us in this pass (decompile, exe bytes or our own doc that closed the same question); **[contributor-measured]** = their live measurement, not repeated by us; **[inferred]** = estimate or reasoning.

## #93 Mono offload: read-only methods (96 methods, CSV in the issue)

Verdict: **sound; its two docs/129 corrections are right and were already closed independently by docs/208.**

- Environment score: events and reactions run per visited object and per visited Sim inside the scan, not only on the buff-add branch. docs/129 §2.1 (line "all on the buff-add branch") is wrong [verified: docs/208 §2 describes the same interleaving from `tools/offload3-closure.ts`]. Their movable estimate ~9-12 ms/s matches docs/208's ~8-10 [inferred, both].
- Motive decay: "no write, cleanest closure" (docs/129 §2.3) holds for statics only. `Motive.Decay`, `Motives.EnergyGainRate`, the Frostbitten timeout and the at-work fun delta are written [verified: docs/208 §4 lists the same set]. They found one writing posture override (baby swing); docs/208 found two (`InSwingPosture`, `InWalkerPosture`), so their list is incomplete there [verified, docs/208].
- Broadcaster candidate set is a pure reader and must keep the game's query order (`PulseSims` takes the first Dictionary entry) [verified: docs/208 §3.3 has the same order constraint].
- New for us: `Motive.EarlyOutMotiveUpdate` skip decision is pure, ~2.3 ms/s [inferred from docs/129 sample counts]; `Utilities.Curve.Fx` stores its argument in a shared `m_x` (a hidden write in the "pure" decay helpers) [unverified, not audited by them either]; autonomy LOD gate needs `World_IsObjectOnScreenImpl` and `Simulator_GetTickCountImpl` snapshots (both unclassified in ic-safety) [verified tool, theirs].
- Mod census (docs/132 G-M5) for the new virtuals (`GetPostureMotiveDecayUpdate`, `GetEnvironmentScore`, `OnTimeOutUpdated`, `RoomCheck`, `GetTargetLot`) is still open.

Actionable: fix docs/129 §2.1 and §2.3 text to point at docs/208; add `EarlyOutMotiveUpdate` as a rider on the docs/208 motive design; audit `Curve.m_x` readers before any decay helper runs on the worker.

## #94 Unseen-Sim throttle map (41 rows, CSV in the issue)

Verdict: **key code claims verified; costs of the new targets are models, not measurements.**

- `SimUpdate` is `[Persistable(false)]` and `InteractionQueue.UpdateTimer` has exactly one caller, `SimUpdate.Simulate` [verified, decompile]. So parking an unseen low-LOD Sim's `SimUpdate` task after `UpdateTimer` loses no saved state.
- `Sim.WaitForExitReason` polls with `Simulator.Sleep(1)` [verified, decompile].
- Correction to docs/144 §1/§2.1: broadcasters pulse once per **sim-minute** (`kSimulateFrequencyMinutes = 1`, one pulse per `Simulate` call, then the accumulator drops by 1), not "about once per sim-second" [verified, decompile]. Effect on our docs/144 numbers: the N=16 lateness is ~16 sim-minutes, not ~16 sim-seconds; the ms/s estimates came from counters and stay.
- Main new insight: EA's LOD limits what an unseen Sim computes, not how often its script tasks wake (SimUpdate `Sleep(0)` per pass, exit-reason poll per tick, idle `Sleep(2)`). None of our built switches (`unseenmotives`, `unseenenv`, `unseenbroadcast`, `[UnseenThrottle]`) touches these wake-ups [verified against docs/143/144].

New throttle targets (all for unseen Sims under the docs/202 rule, cached per Sim):

| Target | Cost now | Visibility risk | Confidence |
|---|---|---|---|
| SimUpdate low-LOD pass parked K ticks (hook after `UpdateTimer`, sim-time cap ~4 sim-min) | ~5-20 ms/s BP, ceiling ~30 | fade/LOD flip up to K late when a Sim walks into view; timed interactions run up to K longer | [inferred] |
| `WaitForExitReason` poll every K ticks, capped at its own deadline, woken by `AddExitReason` | ~1.5-7.5 ms/s | a seen partner waits only if an exit reason bypasses `AddExitReason` | [inferred] |
| Idle loop `Sleep(2)` stretched, cap 1 sim-min | ~1-5 ms/s | townies idle slightly longer | [inferred] |
| `Lot.UpdateReactions` on far lots every K passes (K x sim-min per pass < 1) | ceiling ~18 ms/s | none if K bounded; larger K turns into a pulse throttle | [inferred] |
| Environment look-ats skipped for unseen high-LOD | ~0.3-1 ms/s | none (head tracking) | [inferred] |

Risks they name that apply to us: hooks that sleep inside a Sim's interaction task across a save or while paused (the v0.2.2 `UfHold` family-picker hang class); seeded divergence from changed wake order, so each switch is judged alone.

Actionable: a no-skip probe first (passes, polls, idle passes, `UpdateReactions` calls per second by seen/unseen x LOD, 1-in-64 timing), one BP and one IP job; build only rows >= ~2 ms/s.

## #95 CAS part cache, catalog and string tables

Verdict: **correction verified; sizes are contributor-measured and say these structures are not where heavy-CC address space goes.**

- `[0x11CE404]` is a one-time init guard bit, not the CAS part cache: the bytes at `0x5A4A89` are a `test byte` of bit 0 followed by `jnz`, and at `0x5A4A92` an `or` of bit 0; the exe holds only those 2 immediate references; `0x5AE140` is a getter that returns `[0x11CE42C]` [verified, exe bytes]. docs/190 #21/Q2 and docs/193 §3.4 call it the cache container ("lazy build"); that is wrong. The cache is owned by the `CASPartCacheService` at `[0x11CE42C]`, built in `CASPartCacheService_Init 0x5AEDF0` [verified getter; build path theirs].
- Sizes [contributor-measured, one install]: CAS part cache ~0.7 MB (7,279 parts, ~100 B each); catalog object products ~12.8 MB (11,025, ~1.2 KB each, mostly two UTF-16 key strings); STBL 4,682 tables at 2,112 B each with S3PP's bucket layout (~9.4 MB), ~31.7 KB each without it (~148 MB) [inferred].
- Owner: all EA-heap structures in the engine/memory area, so Apex Radiance's side; none worth an exe patch (max ~3-6 MB here).

Actionable: correct docs/190 #21/Q2 and docs/193 §3.4; close Q2 with their numbers; mark the CAS/catalog compaction ideas "not worth it" in docs/190.

## #96 Streaming for off-camera objects

Verdict: **plausible and consistent with docs/40/38; all numbers contributor-measured (one heavy-CC save, DXVK, `MaxActiveLots = 1`), not repeated by us.**

- Textures are ~70% of the ResourceCache (346-396 of ~530 MB); meshes report ~10 MB [contributor-measured].
- EA already drops render data of lots the camera leaves (`rc` 528 -> 351-359 MB, textures 2,115 -> ~1,100, twice) [contributor-measured; matches docs/38 §1 lot deactivation, verified there].
- ~180 MB of textures (874, 53 of them >= 1 MB = 112 MB) stay regardless of camera; owners unknown [contributor-measured].
- Under DXVK, `rc` overstates address-space cost: 168 MB of textures leaving moved mapped memory by only -22 MB and free space by +40 MB [contributor-measured, one pair; consistent with docs/40 §4-5]. So texture streaming is a VRAM/pagefile/upload-stutter feature for DXVK users and an Error 12 feature only on native D3D9 [inferred]. This matters for how we read `rc` in `diag: mem` lines.
- EA heap cores (592-608 MB, 28-32 MB free, largest free chunk < 200 KB) are not hoarding; Boehm's free space (up to 118 MB) is the reclaimable pool (docs/198 already covers it) [contributor-measured].
- Owner: texture streaming, mip drop and cache layout are Apex Radiance's area. Ours: the diag line (EA cores today fall into `rest`).

Actionable: add EA-core reserved/committed to `[MemDiag] Light` (two dword reads per core over the list at heap `+0x448`); note in docs/40/201 that `rc` is not address space under DXVK; hand the camera-independent texture set (owners open) and the 2048-class load-time mip drop idea (docs/40 E2) to Apex.

## Ranked action list

| # | Action | Owner | Gain | Estimate |
|---|---|---|---|---|
| 1 | Unseen wake-up probe (no skips): SimUpdate passes, exit-reason polls, idle passes, `UpdateReactions` calls per second by seen/unseen x LOD; one BP + one IP job | ours | decides ~10-40 ms/s of new targets | **built** (docs/211, `wakeprobe`, queue `wake-probe.json`, not run) |
| 2 | If the probe confirms: `unseensimupdate` switch (park after `UpdateTimer`, sim-time cap, snap-back) + `WaitForExitReason` deadline-capped poll with `AddExitReason` wake | ours | ~5-20 + ~1.5-7.5 ms/s BP [inferred] | **built** (docs/211, `wakepark` / `wakepoll`, queue `wake-park-ab.json`, not run) |
| 3 | Doc corrections: docs/129 §2.1/§2.3 -> docs/208; docs/144 pulse rate per sim-minute; docs/190 #21/Q2 and docs/193 §3.4 CAS guard bit | ours | correctness | ~30 min |
| 4 | `[MemDiag] Light`: EA-core reserved/committed field; note DXVK `rc` vs address space | ours (diag) | better Error 12 triage | ~1 h + smoke |
| 5 | `EarlyOutMotiveUpdate` rider + `Curve.m_x` reader audit + G-M5 mod census for the new virtuals, folded into the docs/208 motive design | ours | ~2.3 ms/s on top of decay [inferred] | ~1-2 h static |

Not taken: CAS/catalog/STBL compaction (<= ~6 MB); texture streamer (Apex area, mostly VRAM under DXVK); inline animation polls and count-based `DoLoop` callbacks (not safe, per #94).
