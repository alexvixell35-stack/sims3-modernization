# 212. Native writers into managed memory that `[AutShadow] Satb` does not log

Written 2026-10-30. Static only: no game run, no queue, no build. Question (docs/206 §7.7 risk 1): which native functions write a managed object's fields, an array, a string or a StringBuilder buffer on the sim thread during live mode, outside what Satb logs today (37 mint store opcodes, 45 wrapped runtime icall entries, the EA store helper `0xE69820`, 180 EA ICs in `native/ts3native/satb-ics.h`)? Data: `docs/data/native-managed-writers.csv` (273 rows, one per writer or writer group).

## Method and limits

| Checked | How | Tag |
|---|---|---|
| Mono runtime icalls | static table `0x10B5838` (613 entries, 533 distinct functions) against the 329 icalls the game's mscorlib declares; callers and argument provenance from the managed call graph | [verified] table and metadata |
| Dynamic icalls | every call of the registration function `0xE82550`: 4 interpreter icalls (`0xE5A740`-`0xE5A77A`) and EA's IC loop (`0xD7E6C7`) | [verified] |
| How icalls resolve | `mono_lookup_internal_call 0xE82680` -> `0xE82580` reads the patched table (`0xE825B4`); EA binder init `0xD89F00` caches what it returns, so cached icall pointers in EA bodies get Satb's wrappers | [verified] |
| Direct callers of wrapped functions | E8/E9 xrefs of every `kSatbRtAddr` entry and of `0xE69820` | [verified] |
| EA binder native bodies (docs/130 D17) | all 1,085 registrations; each body is classified by the IL of the managed method it replaces (a proxy); 11 bodies checked for stores in the disassembly; docs/220 classifies every body from its own code | [inferred] proxy, [verified] for the 11 |
| EA glue | static-field address users (`0xE6F720` + `0xE9DD40`), host `+0xC20`/`+0xC24`, 12 of 34 `mono_object_unbox 0xEA1650` sites, EA ICs with class-typed parameters | [verified] for the sites read |

Limits:

- **Reach** is a reverse call-graph walk from game-assembly methods (depth 10, call targets as declared in IL). It does not follow virtual dispatch: an override called through its base (`StringBuilder.ToString()` through `Object.ToString()`) shows "no reach". It also does not prove a method runs in live mode, so some "blind spot" rows may only run at load. [inferred]
- **The IL proxy** stands for the 233 EA native-body rows. A native body can do less than the IL it replaces, or more. 11 bodies were disassembled (CSV `how_found`).
- **Not followed:** indirect calls (callers of the host-interface static setters `0xD79EF0`/`0xD79F50`, native callbacks), 22 of the 34 unbox sites, where the destination of the `String.memcpy*` callers comes from, the exception fill on throw, and Mono internals with no icall (class init, string intern, delegate ctor, boxing). All of these write fresh objects [community, inferred].
- **Verdicts:** `covered` (Satb logs it or stops the worker), `blind spot` (writes memory that existed at the cut, with no log), `fresh-only` (objects allocated after the cut, or the caller's own stack), `not reachable` (no game reach, or class init / load / save only), `not a writer`.

## Findings, ranked by risk to Satb

| # | Writer | Writes | Why it matters | Tag |
|---|---|---|---|---|
| 1 | `MonoScriptHost_Simulate 0xD81840`: stores `0xD81D39`, `0xD81D49` | `ScriptCore.Simulator.sSimTickCnt` and `SimClock.sSimTickCnt` (8 bytes each) through host `+0xC20`/`+0xC24` (set at `0xD7E93D`/`0xD7E98D`) | Runs every iteration, so it lands during every concurrent job. `SimClock::sSimTickCnt` is a base read in `docs/data/worker-read-set.csv` (listed "no-IL-writer"). Its reader `SimClock.get_CurrentTicks` is a native body, so the worker's load redirect does not see the read either. | [verified] |
| 2 | `SimClock.ElapsedTimeInMinutes(ref float)` body `0xE0A6D0` (store `0xE0A755`), `ElapsedTimeHours(ref float)` `0xE0A790` (`0xE0A815`) | the caller's byref float | Callers pass field addresses every tick: `Autonomy.mLastTime`, `Lot.mLastTime`, `SimUpdate.mLastTime`, static `Sim.sLastTimeSimsGardeningWasUpdated`, BirdCage, Easel, HoloDisc, `Plant.EvaluatePlantGrowth`. None of these fields is in today's read set, but they break the invariant. | [verified] store, [inferred] callers from IL |
| 3 | `PriorityQueue.Add` `0xE0D450`, `RemoveAt` `0xE0C640`, `SetCapacity` `0xE0D290` | `mCount`, `mCapacity`, `mItems` and 16-byte items, all plain stores | `AlarmManager.mTimerQueue` changes on every alarm add, fire and remove. `PriorityQueue` `mCount`/`mItems`/`mCapacity` and `AlarmManager.mTimerQueue` are in the read set. | [verified] stores, [inferred] fields |
| 4 | StringBuilder bodies (15 rows: `Append*` incl. `0xDD7880`/`0xDD7AD0`, `Insert*`, `Remove`, `Replace`, `set_Length`, `InternalEnsureCapacity` `0xDD4ED0`, `ToString` `0xDA3F60`), `String.InternalSetChar` `0xDDD630` (16-bit store `0xDDD68D`), `String.InternalSetLength` `0xDA07E0`, `String.memcpy`/`memcpy1/2/4` | `_length`, `_str`, `_cached_str`, the buffer's chars and `String.length` | Only matters for a builder allocated before the cut (most builders live inside one call). `String.length`/`start_char` and the builder fields are in the read set. | [inferred], stores [verified] for 3 bodies |
| 5 | `Hashtable.Rehash` `0xDCA870` | the `Hashtable`'s own fields (the new bucket array is fresh) | Any growth of an existing `Hashtable` (`PutImpl`); `modificationCount` is in the read set. | [verified] stores, [inferred] fields |
| 6 | `Curve.Fx(float)` `0xE0A950` | `m_x` of a shared tuning curve (`fstp [obj+0x14]` at `0xE0AD21`) | `MotiveTuning.GetDecay`/`GetMoodContribution`/`GetInitialValue` and `Motive.AutoSatisfyMotive` call it on shared curves. It is a cache that the next `Fx` call writes again. | [verified] store |
| 7 | `SimClock.set_TicksAdvanced` `0xE04F40`, `set_StartTime` `0xE06310` | statics `0x11F1750`, `0x11F16F4` | Load, plus the AddOneHour/AddOneDay cheats (live, rare) | [verified] sites |
| 8 | runtime icalls not wrapped: `MonoPropertyInfo.get_property_info` `0xE7B4D0`, `CultureInfo.construct_internal_locale_from_lcid` `0xE874B0`, `MonoMethod.InternalInvoke` `0xE7CDC0` | fields of an existing reflection or culture object; the `params` array of an invoke | Fills a cache with the same values each time, or writes a caller's array | [inferred] |
| 9 | host static setters `0xD79EF0` (int) / `0xD79F50` (float), slots `+0xAC`/`+0xB0` of host vtable `0x108B690` | any 4-byte static, by name | No `call [reg+0xAC]`/`call [reg+0xB0]` site in `.text`: the caller is not known | [verified] body, [inferred] unused |
| 10 | Boehm weak-link clearing during a GC on the sim | native handle slots, read through `GCHandle.GetTarget 0xE78FD0` | An object that only a weak reference holds can die during a job | [inferred] |

Rows 1-3 are the ones that matter for the pilot closure. Row 1 makes every concurrent job see the next tick count.

## What is covered or safe [verified unless tagged]

- **Covered (18 rows):** the mint stores, `0xE69820` (about 80 runtime call sites plus EA `0xD760E1`, `0xD79BDA`), every wrapped writer icall, and the 180 ICs. Two indirect paths reach Satb's wrappers too, because EA bodies resolve their icall pointers through the patched table: `Array.Clear` `0xD9F8C0` calls cached pointer `0x11EE9D8`, `StringBuilder.Append(string)` calls `0x11EFF50` (which icall each one is: [inferred]). GC icalls are covered by the log roots of docs/206 §7.1 [inferred].
- **Wrapper bypass, not reachable:** `Array.SetValue(object,int[])` `0xE79CE0` (icall 44) calls `SetValueImpl 0xE793D0` directly (`0xE79D77`, `0xE79E05`), but no game code calls it [verified calls, inferred reach].
- **Dynamic icalls the table patch misses:** `StackFrame.get_frame_info` `0xE531D0`, `StackTrace.get_trace` `0xE53290`, `Delegate.CreateDelegate_internal` `0xE53370`. They write only fresh objects [inferred].
- **Fresh-only (163 rows):** XML readers and parser contexts, `StringReader`, string builders of new strings (`Replace`, `ToUpperInvariant`, `ParseFormatSpecifier`), runtime icalls that write into caller locals (`Double.ParseImpl`, `get_enum_info`, `ValueType.InternalEquals`, `Module.Resolve*`), `MonoEnumInfo.GetInfo`, `Math.DivRem` [inferred].
- **Not reachable in live mode (55 rows):** Persist / Formatter / FastBaseStream bodies (save, load, cache). The `TaskControl_SetFieldValue` and `Serializer_ReadFastFields`/`ReadPrimitiveArray` ICs (load only; these are not in `satb-ics.h` because no guard thunk covers them). Curve setup. Class-init static stores through `0xD89BF0`. P/Invoke and remoting jit icalls (I-119-13). The Monitor and Thread stubs, and Interlocked (not declared in the game's mscorlib) [inferred].
- **Not a writer:** the 12 unbox sites read: each one compares, loads, or copies the value out to native memory (`0xF7E8A6`).

## Gaps and next steps

1. **Read-side twin of the same gap [inferred].** EA native bodies that run on the worker read live memory and skip the redirect: `get_CurrentTicks`, the PriorityQueue and StringBuilder bodies, `Curve.Fx`. Fixing only the write side does not make rows 1-4 exact.
2. **Fix shape [design].** EA bodies are bound through `0xD9A860` (hash, adapter, body, init), so the binder table can be patched at install the same way as the icall table. The sim side logs before rows 2-6. The worker side redirects or stops. For row 1, log the two statics at `SatbBegin` and give the worker the cut tick.
3. **To verify:** disassemble the remaining 222 IL-proxy rows, at least the 36 blind spots and the reachable fresh-only rows. Classify the `String.memcpy*` destinations. Read the other 22 unbox sites. Find the caller of the host setter slots.
4. **Dynamic check:** Mode R A1-A3 (docs/206 §7.7) is the only test that catches a writer this list missed.
5. Inferred claims are not yet in docs/51.
