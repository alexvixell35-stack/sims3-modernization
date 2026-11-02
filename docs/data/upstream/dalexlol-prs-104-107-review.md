# Review: contributor PRs #104-#107 and results issues #108 / #109 (2026-10-30)

Contributor dalexlol (GitHub `alexvixell35-stack`) answered research tasks 1-5. This is our review, static only: no game run, no build. PR text, issue text and CSVs were read as data. Nothing was posted, approved or merged. Reply drafts sit next to this file (`pr-104-reply.md` ... `issue-109-reply.md`).

Tags: [verified] = we re-read it ourselves (disassembly of the Steam 1.67 exe with capstone, our source, our logs); [theirs] = the contributor's claim, not re-checked; [inferred] = our reasoning.

| Item | Verdict | Merge |
|---|---|---|
| #105 docs/208 native writers (task 1) | **Correct and important.** It finds a real hole in `[AutShadow] Satb`: EA binder native bodies (docs/130 D17) and a few raw native stores write managed memory, and on the worker those bodies read live memory. Neither the barrier nor the redirect sees them. | merge after renumbering (-> docs/212) |
| #107 docs/210 autonomy cold paths (task 5) | Good, careful method. For the act runs we have now (Mode R), it does not explain our cold give-ups: those come from ts3fixes' own worker-only helpers. It matters for go-live / Mode W. | merge after renumbering (-> docs/214); check `--self-check` is wired into `tools/selftest-all.ts` |
| #106 docs/209 mint opcode spec (task 2) | High quality. All 449 rows match `docs/data/mint-opcodes.csv` on name and handler. The 10 spot checks pass. It also documents a real EA crash path (`div.i4`/`rem.i4` with `INT_MIN / -1`). | merge after renumbering (-> docs/213) |
| #104 harness fixes | Correct, small, harness only. Two nits, neither blocking. | merge |
| #108 sim budget on big saves (task 3) | Useful. It changes one assumption: old saves do **not** make autonomy scoring dearer. | reply only |
| #109 mod compat v0.2.4 on vs off (task 4) | Useful. The big finding is that `[AutShadow]` is off for every session on this install (MVID guard). Also a new Apex crash site. | reply only |

## 1. PR #105: native writers vs the Satb log (most important)

### 1.1 What Satb covers today [verified, docs/206 §7.2, §9-§13, `native/ts3native/satb.cpp`]

- **Sim barrier (log before write):**
  - the 37 mint store opcodes;
  - the 45 runtime icall writers, patched in the static icall table `0x10B5838` (`kSatbRtAddr`): Array.Clear/FastCopy/SetValue, Buffer.BlockCopy/SetByte, String.InternalStrcpy x4/InternalCopyTo, Interlocked.*, InitializeArray, Marshal.Write*/copy_from_unmanaged/PtrToStructure, MonoField.SetValueInternal;
  - EA's field store `0xE69820`;
  - byref and array arguments of the 180 EA ICs in `satb-ics.h`.
- **Worker redirect (read the cut value):**
  - the 41 mint load opcodes, `con.callvirt`/`con.boxcallvirt`, `getchr`/`strlen`;
  - the reader icalls (FastCopy, Clone, BlockCopy, GetGenericValue, InternalStrcpy, CopyTo, MonoField.GetValueInternal);
  - EA ICs answered from snapshot rows by the guard thunk.
- **Not covered at all:** EA's 1,085 binder native bodies (`RuntimeMethod+0x24..+0x2C`, called through `0xE525C0`; binder `0xD9A7B0`, registrations `0xD9A860`). `satb.cpp` and `autshadow*.cpp` have no hook on `0xE525C0`, the binder table or the static-field helpers `0xD89BF0`/`0xD89C60`/`0xD89CD0` [verified, grep].
  - Each native body runs its own stores and loads natively, so neither side sees them.
  - On the sim, its stores are not logged.
  - On the worker, its loads read live memory.
- **docs/122 §12.1 assumption, now stale:** I-122-9 said the sim-written statics these bodies read are "constant in the window". That held while the worker ran inside the sim's window. It does not hold for `Satb=2`, where the job keeps running through the next `MonoScriptHost_Simulate` (`satbConc`).

### 1.2 Their writers against our coverage

Rows 1-10 are the PR's ranking. Rows 11-12 are ours, found while checking.

| # | Writer | Covered? | Risk for the `Satb=2` worker | Fix |
|---|---|---|---|---|
| 1 | `MonoScriptHost_Simulate` stores `0xD81D39` / `0xD81D49`: the tick into `Simulator.sSimTickCnt` / `SimClock.sSimTickCnt` through host `+0xC20` / `+0xC24` [verified: `mov eax,[esi+0xC20]; mov [eax],ecx; mov [eax+4],edx`, pointers set at `0xD7E93D` / `0xD7E98D`] | **no**: a raw native store; not a mint opcode, not `0xE69820`. The reader `SimClock.get_CurrentTicks` is a native body, so the worker's read is not redirected either | **high in principle.** Every concurrent job overlaps the next Simulate, so any time-based score (`CurrentTicks`, elapsed-time and hours-since checks) can read tick t+1. satb5's A0 31,038 of 31,038 says the current allow-list did not hit it in that run. It is not exact by construction | Log both 8-byte statics at `SatbBegin`. On the worker, answer `sSimTickCnt` reads in the SimClock bodies from the cut copy (with 12, or a worker detour on `get_CurrentTicks`). Cheapest first step: a stop-the-worker check if the tick moved while a job runs, as a counter |
| 2 | `SimClock.ElapsedTimeInMinutes(ref float)` `0xE0A6D0` / `ElapsedTimeHours(ref float)` `0xE0A790` [verified store `0xE0A755` `fstp dword [eax]`] | **no**: a native body, not one of the 180 ICs, so the IC byref logging does not apply | **medium.** The sim writes `Autonomy.mLastTime`, `Lot.mLastTime`, `SimUpdate.mLastTime` and `Sim.sLastTimeSimsGardeningWasUpdated` through it every tick [theirs]. None of these is in today's read set [theirs], but a wider allow-list would read stale-or-new without knowing it | Give binder bodies with byref/array parameters the IC treatment: log the argument range on the sim before the body. Same table shape as `satb-ics.h`, generated from the binder keys (`managed-callgraph.ts bindkeys`) |
| 3 | `Sims3.Collections.PriorityQueue.Add` `0xE0D450`, `RemoveAt` `0xE0C640`, `SetCapacity` `0xE0D290` | **no** | **medium-high.** `AlarmManager.mTimerQueue` changes on every alarm add, fire and remove during the job. `PriorityQueue` `mCount` / `mItems` / `mCapacity` and `AlarmManager.mTimerQueue` are in `docs/data/worker-read-set.csv`. Not `Sims3.Gameplay.Autonomy.PriorityQueue` (managed, fresh per `BestScores`) | Wrap the 3 bodies on the sim: log `this`'s header fields plus the touched item range (or the whole `mItems` array, up to the IC cap) before the body |
| 4 | String/StringBuilder native bodies (15 rows: `Append*`, `Insert*`, `Remove`, `Replace`, `set_Length`, `InternalEnsureCapacity`, `ToString`), `String.InternalSetChar` `0xDDD630` [verified store `0xDDD68D` `mov word [eax],cx`], `InternalSetLength`, `String.memcpy*` | **partial.** Bodies that copy chars through the cached `InternalStrcpy` / `Array.Clear` pointers reach our wrapped icalls [theirs, plausible: the binder resolves through `mono_lookup_internal_call`, which reads the patched table]. Their own `_length` / `_str` / `_cached_str` / `String.length` stores are not logged | **low.** Builders allocated before the cut and shared with the worker are rare; the closure builds its own. docs/206 §7.7 risk 3 already names the read side | Low priority. When building the binder wrap (row 2), log the builder header (16 bytes) for `this` outside the sim's private range |
| 5 | `Hashtable.Rehash` `0xDCA870` | **no** | **medium.** A shared `Hashtable` growing on the sim during a job swaps `buckets` and updates `count`/`modificationCount` with native stores. A worker reading it sees the new table, with no log entry for the old pointer, so the old one is not kept alive as a root | Log the Hashtable header words before `Rehash` (sim). It is reached from `PutImpl`, so the wrap is one body |
| 6 | `Curve.Fx(float)` `0xE0A950` [verified store `0xE0AD21` `fstp dword [edi+0x14]`; Fx itself does not read `+0x14` back, verified in its body] | **no**, and the worker writes it too | **low for exactness, but it breaks an invariant.** The worker calls `Fx` on shared tuning curves (`MotiveTuning.GetDecay` and the rest), so the worker stores into shared memory outside the Satb redirect, and the sim can see the worker's `m_x`. Harmless only if no managed reader uses `m_x` (not checked) | Worker side: skip the store, or give up when `this` is not worker-private. Check the managed readers of `Curve.m_x` first |
| 7 | `SimClock.set_TicksAdvanced` `0xE04F40`, `set_StartTime` `0xE06310` (statics `0x11F1750`, `0x11F16F4`) | **no**. These go through the static set helper `0xD89BF0`, which is not wrapped [inferred from docs/130 §9: 17 sites, 15 fields] | **low**: load, plus the AddOneHour/AddOneDay cheats | Wrap `0xD89BF0` (row 11) |
| 8 | Unwrapped runtime icalls: `MonoPropertyInfo.get_property_info` `0xE7B4D0`, `CultureInfo.construct_internal_locale_from_lcid` `0xE874B0`, `MonoMethod.InternalInvoke` `0xE7CDC0` | **no** (not in `kSatbRtAddr`) | **low.** The first two refill a cache with the same values. `InternalInvoke` writes the caller's `params` array, and its only closure path (`DesiredSimFunction`) is aborted on the worker by H5 | Add all three to the runtime writer list as sized writers (property-info struct, CultureInfo fields, `params` elements). Cheap |
| 9 | Host static setters `0xD79EF0` (int) / `0xD79F50` (float), host vtable `0x108B690` `+0xAC`/`+0xB0` | **no** | **unknown, likely none.** No direct caller found [theirs] | Insurance: a detour that logs the destination and counts. If it ever fires during a span, stop the worker |
| 10 | Boehm weak-link clearing in a sim GC during a job | **no** (by design) | **low-medium.** An object reachable only through a weak handle at the cut can die. The worker's `GCHandle.GetTarget` `0xE78FD0` then returns null where the cut had an object | Worker: on a weak-handle read, give up if a collection ran since the cut (GC count epoch) |
| 11 | **(ours)** EA static-field helpers in native bodies: set `0xD89BF0` (17 sites, 15 fields), get-address `0xD89C60` (3 sites; the caller writes through it), get `0xD89CD0` (166 sites) [docs/130 §9] | **no** on either side | **medium.** These carry the SimClock / DateAndTime static traffic of rows 1 and 7 | Sim: wrap `0xD89BF0` and log the destination (the helper computes it). Worker: wrap `0xD89CD0` with the same log lookup as the load stubs |
| 12 | **(ours)** Read side of every binder body on the worker (78 in the closure, docs/122 §12.1) | **no** | **medium.** This is the PR's gap 1: even with every write logged, a native body on the worker reads live words, not the logged cut values | Pick one: (a) patch the binder entries (`RuntimeMethod+0x28` or the `0xD9A860` table) so the worker runs a redirecting copy for the few hot bodies (SimClock, PriorityQueue, Curve, Math); (b) **give up on any binder body whose `this` / arguments point at memory the sim logged since the cut** (one log lookup per pointer argument). (b) is cheap and exact; (a) is only for bodies (b) gives up on too often |

Covered and fine as the PR says: the mint stores, `0xE69820` (about 80 runtime sites plus EA `0xD760E1` / `0xD79BDA`), the wrapped icalls, the 180 ICs; `Array.SetValue(object,int[])` bypasses the wrapper but has no game caller [theirs]; the dynamic icalls `StackFrame.get_frame_info`, `StackTrace.get_trace`, `Delegate.CreateDelegate_internal` write fresh objects only [theirs].

### 1.3 What to do (proposal, nothing built)

1. **Now, cheap, before the next act queue:**
   - row 1 cut copy (or a moved-tick counter);
   - row 11 sim-side log on `0xD89BF0`;
   - rows 3 and 5 sim-side wraps;
   - row 12 (b) give-up on logged pointer arguments;
   - one Mode R job with a scored definition that reads `SimClock.CurrentTicks`, to prove row 1 was the reason A0 held.

   About one agent build round [inferred].
2. **With the allow-list widening (docs/126 tier 2):** row 2's generated byref table for binder bodies, rows 6, 8, 9, 10.
3. **Ledger:** the PR's claims are not in docs/51 yet (their gap 5). Reserve one section for I-212-* when merging.

## 2. PR #107: cold paths vs our cold give-ups

**Our data** [verified, `probe-out/queue/satb-act-2026-10-30-*-ts3native.log`, 8 logs, 5 with the 0.36.141 name lines]:

- Every run with names shows the same cold give-ups: op `0xC000` (transform) x5 and op `0xC001` (native-call thunk) x2. That matches docs/206 §13's 7 per run.
- The names are the same 7 methods in every run:

| Method | Runs | What it is |
|---|---|---|
| `Ts3Fixes.AutShadowNative:Abort` | 5 | ts3fixes IC: worker-only |
| `Ts3Fixes.AutShadowNative:ResetAbort` | 5 | ts3fixes IC: worker-only |
| `Ts3Fixes.AutShadowNative:IsWorker` | 4 | ts3fixes IC |
| `Ts3Fixes.Fixes:RainShelterBranch` | 5 | H11 hook branch (docs/127): taken only on the worker |
| `Ts3Fixes.Fixes+RainProbe:SeasonsOn` | 5 | same branch |
| `Ts3Fixes.Fixes+RainProbe:TargetOutside` | 5 | same branch |
| `System.Collections.Generic.Dictionary`2:get_Count` | 5 | one generic instance, not named by the log |

So in Mode R **none of our cold paths is a game scoring method**. The reference run on the sim warms every game path the worker takes. Only branches that exist only on the worker stay cold. Their 488 scoring-stack first hits (pet, venue, couple and social definitions coming up late) are the cold paths of **Mode W / go-live** (docs/124), where the sim no longer runs the same search first.

**Warm-up list (proposal):**

- **Tier A, now (Mode R / act):**
  - Transform the 7 methods above on the sim at world load, without calling them: a ts3native call into the transform `0xE643B0` for the `RuntimeMethod` by name. For the 3 ICs, also bind the IC and build its native-call thunk (`0xE82680` + `0xE51400`).
  - `IsWorker` may simply be called once on the sim (it returns false). `Abort` / `ResetAbort` must not be called on the sim.
  - The `Dictionary`2.get_Count` instance: log the generic class name in the cold line (`+8` `MonoMethod` -> class), then add that instance.
  - Expected: cold give-ups 7 -> 0 per run [inferred].
- **Tier B, before go-live:**
  - Bind every IC of the closure up front: docs/119's 108 bindings plus docs/127's 346 virtual ones, lookup + thunk, no call. Their finding 3 shows a lazy IC binding on a scoring stack at minute 23.
  - Pre-transform the static closure to depth 3, no `.cctor`: their 422 never-run rows plus the 438 live first-transforms, as a list built from the loaded assemblies so mod types are included (their finding 6).
  - Do not run `.cctor`s early (their finding 4).
- **Adopt their abort-on-cold pre-check.** Our act-only detours (`SatbInstallCold`) give up after the first-time work has already started on the worker (docs/206 §8 X11: "the call itself still runs once on the worker"). Their check reads the same flags before the work:
  - `RuntimeMethod` code pointer;
  - class `+0x14` bit 0;
  - vtable `+0x18` bit 1;
  - cached icall pointer;
  - thunk present.

  That closes the residual X11 race instead of detecting it afterwards. It needs a check at the worker's call push and at the `newobj` / static-access sites, so it is a design item, not a one-liner.

PR nits: run the new tool's `--self-check` from `tools/selftest-all.ts` (project rule). `tools/frida/cold-paths.js` is read-only [theirs]. Its install-specific rows (62 mod rows) need a second save before they count as typical, as the doc itself says.

## 3. PR #106: mint opcode spec, spot check

**Whole-file check** [verified]: all 449 rows have the same opcode, name and handler address as `docs/data/mint-opcodes.csv` (0 differences); 430 rows carry at least one `[emu]` test.

**10 spot checks** (CSV handler vs ours; semantics read in the handler):

| Opcode | Name | Handler (theirs = ours) | Semantics check |
|---|---|---|---|
| `0x00A` | `ldc.i4.m1` | `0xE545F2` | `mov [esi],-1; add esi,8; ip +1 word` [verified] |
| `0x029` | `starg.i4` | `0xE585C7` | shared with `starg.p` (ours: 2 ops on the handler) |
| `0x064` | `strmfld` | `0xE56C43` | one of the 2 store opcodes docs/206 §7.1 added; null -> NRE stub |
| `0x0EC` | `callvirt` | `0xE549EB` | 4 words, 2-item inline cache, as docs/130/133 |
| `0x0ED` | `con.callvirt` | `0xE549EB` | shares callvirt's handler; EA-only, as docs/206 §7.1 |
| `0x111` | `ldelem.any.vt` | `0xE57382` | EA-only, unsigned bounds check |
| `0x127` | `div.i4` | `0xE55B4C` | only the divisor-0 check, then `cdq; idiv` with no `INT_MIN / -1` guard [verified] |
| `0x1A6` | `sim_sleep` | `0xE5939C` | yield intrinsic, as docs/130 |
| `0x1AB` | superop | `0xE58A73` | `ldthis + ldfld.o`, same base sequence as ours |
| `0x1C0` | superop | `0xE5904D` | 15-op sequence, same as ours |

**Verdict:** high quality and directly useful for the docs/207 compiler and the Satb emulation. Two notes:

- **`div.i4` / `rem.i4` `INT_MIN / -1`** raises a CPU divide error that the interpreter does not catch. Under our "fix EA bugs too" rule this is a candidate for a small fix: a guard in the two handlers that throws `OverflowException` / returns 0, with a switch, counters and a selftest. Whether any game or mod code can reach it is open [inferred].
- **x87 precision note:** if a D3D call ever switches the sim thread's FPU to 24-bit, every `r8` opcode loses precision. Worth one live read of the sim thread's control word in a probe run.

## 4. PR #104: harness fixes

| Change | Review |
|---|---|
| `SubState()` walks `BaseType` to the `GameStates` assembly | Correct for NRaas Traveler's `LiveModeStateEx`. Unmodded installs unchanged. Nit: if a game state class ever lived in another EA assembly, the walk would end at `Object` and report `"Object"`. Stopping at the first type whose assembly name starts with `Sims3` / `ScriptCore` would be safer. Not blocking: all ~31 `"LiveModeState"` compares are in-world states |
| AutoDialog lifetime wish | Logic correct: an open `WishPickerDialog` is cancelled first, then `SelectWish(first)` + Okay, run through `RunAsync` like the other button presses. Side effects: (1) the harness now picks a lifetime wish (gameplay changes vs a stall, fine for tests); (2) `"customize"` in `kRejectWords` lowers any button with that word in every dialog. Nit: `select` is looked up on `wishType` even on the picker path. A null `wishType` with a non-null picker would throw, which cannot happen in the shipped UI assembly |
| `content-health.ts` skips a non-directory | Correct (`statSync` is already imported). No side effect |

**Verdict:** merge. It needs a CHANGELOG / PLAN line only, which it already has.

## 5. Issue #108: sim-thread budget on big long-running saves

Key numbers (Ryzen 9 7945HX, not our i7; 1 corrected off run per save) [theirs]:

| | young saves (MF, IP with heavy CC; SV control) | day-406 save (LR) |
|---|---|---|
| Mono ms per sim-minute, off | 38-40 | 55 |
| Mono ms per sim-minute, on (release) | 32-36 | 51 |
| ICs ms per sim-minute | 11-17 | 18-22 |
| sim-minutes per 10 real minutes | 6,388-7,787 | 5,163-5,786 |

Per sim-minute buckets, LR on vs SV on:

| Bucket | LR on | SV on |
|---|---|---|
| autonomy search | 6.2 | 6.0 |
| NRaas | 8.3 | 3.2 |
| SimUpdate | 11.2 | 7.6 |
| `Lot.Simulate` | 6.5 | 3.9 |
| `Query_GetObjects` | 3.5 | 1.6 |
| `Query_GetObjectsOnLot` | 2.4 | 1.0 |

Other results:

- Mono per real second is 395-533 ms/s on every save (always saturated at max speed). Per real second, our test saves look the same.
- Release on cuts route sync plans from 37-94 to 6-15 ms/s. It adds 54-84 ms/s of `+0x918` / job waits.
- Frames >= 300 ms: on 0-3 vs corrected off 3-11.

**For our plans:**

- **Rodney's idea "big saves make the Mono offload gain more" holds only partly.** It holds for Mono as a whole: an old save costs ~35-60% more script time per sim-minute. It does not hold for **autonomy scoring**, the docs/122/124 pilot's target, which costs the same per sim-minute on day 406 as on day 11.
- The old-save extra sits in `SimUpdate` (motives, buffs), `Lot.Simulate` / reactions, object queries and NRaas code. That is the docs/129 / docs/208 (offload3: environment, broadcasters, motives) list. So offload3 and the query snapshot work gain more on old saves; the autonomy pilot gains about the same.
- Worth one run on our own machine with a long save, to put our absolute ms/s next to theirs. Their off-arm gap (4 default-on switches) also tells us the docs/160 off-arm list should live in one shared place, not in each queue.

## 6. Issue #109: mod compatibility, release v0.2.4 on vs off

- **132 packages:** 59 with scripts (326 assemblies), 3,854 CASP, 4,852 OBJD, plus Bin add-ons (Apex, S3SS, S3PP, DXVK, ASI loader, a loading-screen ASI).
- **Breakage the release causes: none seen.**
  - One EA `NullReferenceException` (`CookingProcess.InitSingleServingFromGroupServing`, on arm, control save, no frame of ours or of any mod).
  - Stuck Sims: on is equal to or better than the corrected off arm on every save. LR 19 vs 22, MF 3/8 vs 65, IP 1 vs 1, SV 0 vs 3.
  - EatOutsideRestaurant: 4 of 19 stuck on LR on, re-stuck after our moves. A watch item, one run.
- **Crash:** Apex Radiance, off arm. AV at `0x68EE50` from `0x6F673C` (model part draw `0x6F6250`, part entry `0x20`). This is a new site in the docs/186 family, and `crashfx` does not guard `0x6F673C` [theirs; the crashfx list matches `crashfx.cpp`].
- **Explained, off arm only:** wexx WorkUniforms `TimerGuard` took over the global alarms after EA's 3 AM cleanup plus simler90's `AgingFixHomelessPetsAndYoungsters` blocked them. `easpreadlots` prevents it on the on arm.
- **Harness issues:** all fixed by #104.
- **`[AutShadow]` impact (most important for the plan):**
  - simler90's core mod and tiny-ui-fix both replace `Sims3GameplaySystems` / `Sims3GameplayObjects`, so the MVID guard turns the worker **off for every session** on this install. A heavily modded player gets no Mono offload gain at all.
  - ErrorTrap's own `ScriptCore` is not checked by the MVID guard (L3 per-method only).
  - 60 mod types can reach the worker.

**For the compat matrix:**

- Add the script-mod rows and the 6 Bin rows as "ok on v0.2.4, autopilot only" with their caveat: "ok" means nothing showed in 10 minutes of autonomous play, not tested interactions.
- Mark simler90 core + tiny-ui-fix as "Mono offload off (replaced core assemblies)".
- Mark ErrorTrap as "unchecked module".

**For the plan:**

- Move the docs/122 §12.4 mod guard from MVID-per-session to per-method checks (item G) **before** go-live. Otherwise the largest group of players, those with simler90 / tiny-ui-fix, gets nothing.
- Add `0x6F673C` to the crashfx draw guards or send it to Apex with the docs/186 report: their area (Apex coordination rule), our guard as a stopgap.
- Take the contributor's per-mod A/B offer for the 4 most-flagged mods + EatOutsideRestaurant.

## 7. Doc number clash and renumbering

Master already has docs/208 (offload3), 209 (contributor research 93-96), 210 (free vacation) and 211 (unseen wake-ups).

| PR | Their file | Proposed | Also change |
|---|---|---|---|
| #105 | `docs/208-native-managed-writers.md` | `docs/212-native-managed-writers.md` | heading `# 208.` -> `# 212.`; its CLAUDE.md and PLAN.md lines |
| #106 | `docs/209-mint-opcode-spec.md` | `docs/213-mint-opcode-spec.md` | heading, CLAUDE.md, PLAN.md |
| #107 | `docs/210-autonomy-cold-paths.md` | `docs/214-autonomy-cold-paths.md` | heading, CLAUDE.md, PLAN.md, and the `docs/210` header comment in `tools/autonomy-cold-paths.ts` and `tools/frida/cold-paths.js` |

- The CSV names do not clash (`native-managed-writers.csv`, `mint-opcode-spec.csv`, `autonomy-cold-paths.csv`).
- The three docs do not cite each other by number [verified, grep], so the renumbering touches only the lines listed.
- Easiest path: ask the contributor to rename in the PRs (reply drafts say so), or rename in the merge commit with `tools/agent-merge.cjs`. Their CLAUDE.md lines also need moving into the right list position.

## 8. EA bug issues (listed only, not analysed here)

- #101: EA bug: stale static `Occupation.backupOutfit` uncaches a re-applied career uniform (career outfit lost after reload).
- #102: EA bug: a bake sale table keeps a deleted seller (every buyer throws, an orphaned NPC table is never cleaned up).
