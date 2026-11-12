# 258. The "IC-transition lock" 0x11F43A8: Mono's loader lock, taken per field by `ValueType.InternalEquals`

Written 2026-11-12. **Static analysis only** (capstone on the Steam 1.67 copy `game-copy/bin/TS3W.exe`, the binder table docs/220, existing counters and samples in `probe-out/`). No game run, no build. Answers docs/23 big win #4.

## Summary

- **The lock** is Mono's global `loader_mutex`, a recursive `CRITICAL_SECTION` at `0x11F43A8` [verified, docs/29 §5.1, recounted here].
- **"IC transition" was the wrong name.** `0xDA5970` is EA's native body for `System.ValueType::DefaultEquals(object,object)` (binder row 152). Its callback `[0x11EE6CC]` is `ves_icall_System_ValueType_InternalEquals` `0xE7A590`. That icall walks the class fields with `mono_class_get_fields` `0xE70180`, which takes and releases the loader lock on **every** call. One `InternalEquals` call therefore costs `field.count + 1` lock pairs, and static fields count too. A boxed enum has `value__` plus one static literal per member, so it costs members + 2 pairs [verified code path; that the count includes statics is inferred from upstream metadata, I-474-3].
- **Cost:** about 410-420k lock enters/s, all on the sim thread, in the only counted run (Verify, 2026-10-16). That works out to ~8.7 ms/s on the live sample chain, ~21 ns per pair. So the cost is **call rate, not contention** [measured counts, inferred split].
- **Contention:** none in that run (main 0, other threads 0). It predates `[AutShadow] Unseen=1`, which is ON by default on master (`ts3native.ini`, CHANGELOG "Unreleased") and can call `Enum.Equals` on its worker. Whether worker and sim now contend is **not measured**. `[IcLockCheck]` is built, but its one run (`bp-iclock-soak`, 2026-10-16) saved no counters. No known hitch is attributed to this lock.
- **Recommendation:** build a per-class fast path for `InternalEquals` (option C, §5): a cached compare plan for value types whose instance fields are all 4- or 8-byte integers, which covers int-backed enums. It drops every lock pair and the field walk for those classes. Estimated 6-9 ms/s [inferred], exact by construction, no Apex overlap, about 2-3 h build plus a monohost exactness corpus. Skipping the lock based on `size_inited` is **unsafe** (§4).

## 1. What the lock is [verified]

| Item | Address | Evidence |
|---|---|---|
| `loader_mutex` (`CRITICAL_SECTION`, recursive) | `0x11F43A8` | `mono_loader_init` `0xE84880`: `InitializeCriticalSection(0x11F43A8)` then `TlsAlloc` (loader error slot `[0x1220F38]`) (docs/29 §5.1) |
| `mono_loader_lock` / `mono_loader_unlock` | `0xE85020` / `0xE85030` | 12 bytes each: `push 0x11F43A8`, `call [EnterCriticalSection]` / `[LeaveCriticalSection]`, `ret`. Recount 2026-11-12: **65** `call 0xE85020` and **80** `call 0xE85030` sites (docs/29 counted 89 unlock xrefs, which includes non-call references) |
| Inline users | 20 `push 0x11F43A8` sites in `0xE85040`, `0xE85390`, `0xE85810`, `0xE85CB0`, `0xE85DF0` (loader.c) | docs/29 §5.1, docs/58 §2 |
| Owners outside Mono | none: every site is in the Mono range `0xE65FE0-0xEB1E60` | docs/29 §5.1 |

It guards the metadata loader's lazily filled state: class setup, method and field lookup, the name, method and generic caches, the icall hash, and EA's interface-offset layout `0xD790A0` (docs/58, docs/130 I-130-5).

## 2. Which path takes it and how often

### 2.1 The path [verified, this note]

| Step | Address | What it does |
|---|---|---|
| EA binder body #152 `System.ValueType::DefaultEquals(object,object)` | `0xDA5970` (adapter `0xDA5BB0`, hash `0x278643D5`) | `docs/data/binder-bodies.csv` row 152. Returns false on a null second argument (`0xDA599E`). Calls `[0x11EE6CC](this, that, &fields)` at `0xDA5A0E`, or at `0xDA59FA` inside a `setjmp` when `[0x11F2D84] > 0`. Then runs each returned pair through `0xE520E0` with the method in `[0x11EE6D0]` (the per-pair `Equals`) |
| Bind | `0xDA5910` | class-hash check `0xECDEB3B7`, then `0xD9A890(method, IL offset 0x0A)` gives `[0x11EE6C8]` (the `call` token at IL 9-10, i.e. `InternalEquals`), `0xD89F00(+8)`, then `mono_lookup_internal_call` gives `[0x11EE6CC]` (stored at `0xDA594E`). IL offset `0x30` gives `[0x11EE6D0]` |
| Callers of `0xDA5970` | `0xDA7FF2` in EA body #194 `System.Enum::Equals(object)` `0xDA7FB0` (when `[0x11EE5B4]+0x28` is set); `0xDC48D2` in body #687 `System.Attribute::Equals(object)` `0xDC4870`; its own adapter `0xDA5BD6` | `call` scan |
| The callback | **`0xE7A590` = `ves_icall_System_ValueType_InternalEquals`** | corlib icall name table `0x10B4EA0` (613 entries), `InternalEquals` at index 603 (name `0x1093BB0`); function table `0x10B5838`, slot `0x10B61A4` = `0xE7A590`. That slot is the only static reference to `0xE7A590`. Neighbours check out: 604 `InternalGetHashCode` = `0xE7A410`, 601/602 `ToObject`/`ToObjectInternal` = `0xE81DB0` |
| Field walk | `0xE7A590` loop: `0xE70180(klass, &iter)` at `0xE7A5DA` and `0xE7A7CA` | Null `that` raises `ArgumentNullException` (`0xEA18F0`). A vtable mismatch returns false before `*fields` is written. Static fields (`type attrs & 0x10`) and special-name `_Deleted` fields (string `0x108B558`) are skipped. Type switch on `type+6`: I4/U4 compare 4 bytes, I8/U8 compare 8 bytes, STRING compares length then bytes. Every other kind (R4, R8, I1-U2, CHAR, BOOLEAN, references, structs) is boxed through `0xEA3990` into a pair array (`mono_array_new` `0xEA30F0`) for the managed loop |
| `mono_class_get_fields` | `0xE70180` | Returns 0 when `iter` is NULL. Otherwise it always runs **lock, `0xE6E600(klass)`, unlock**, then steps the iterator (stride `0x1C`, count `klass+0x68`, array `klass+0x88`). 38 call sites |
| `mono_class_setup_fields` | `0xE6E600` | Early exit when `klass+0x14` bit 2 (`size_inited`) is set (`0xE6E619`) |

So the lock time "under the IC transition" (docs/29 §5.1: 87 of 111 unlock and 16 of 20 lock samples return to `0xDA5A14`) is `InternalEquals` iterating fields. Most of that traffic is boxed enum compares through `Enum.Equals`. Those come from dictionaries and `List.Contains` with enum keys through the default comparer (docs/04, docs/74, docs/97).

### 2.2 Numbers that exist [measured unless tagged]

| Source | What | Value |
|---|---|---|
| `probe-out/queue/verify-2026-10-16-{bp,ip}-verify-native.json` (build c24fd55, Verify slot 46 on `0xE85020`, 8 min `verify-v22` sweep, deltas from after load to end) | wrapper enters | BP 202.1 M (~421k/s), IP 197.3 M (~411k/s) |
| same | threads | sim only: `vfMonoE85020Main` 0, `Other` 0 (ledger I-58-1) |
| same | `vfMonoE85020Us`: per-call whole microseconds, so calls under 1 µs add 0 (the slow tail) | BP 192 ms in 480 s (0.40 ms/s), IP 243 ms (0.51 ms/s) |
| same | longest single enter | BP 5.6 ms, IP 5.3 ms. Once each, with no other hooked thread inside: preemption or an inline-site holder [inferred] |
| docs/130 §10.6 (206 sim-thread files, 75,742 samples, live-chain filter) | lock + unlock | 0.14% + 0.73% (~8.7 ms/s at 1% ≈ 10 ms/s, docs/28) |
| same | `0xDA5970` inclusive / `0xDA7FB0` inclusive | 0.99% / 0.72% |
| docs/207 §3 (managed sampler, `sim-budget-v024` + `release-validate-2026-10-28`) | `Enum.Equals` self (the sampler has no icall frames, so the icall time lands here) | 9.8 ms/s, the #2 never-yielding method |
| `bp-iclock-soak` 2026-10-16 (`[IcLockCheck]` on, 10 min) | counters | **none saved** (no native json for the job). The only `[IcLockCheck]` run has no data |

Implied cost per lock pair: 8.7 ms/s ÷ ~420k/s ≈ **21 ns**, which is the price of an uncontended `EnterCriticalSection` + `LeaveCriticalSection` [inferred]. The 5:1 unlock-to-lock sample ratio that docs/29 read as contention fits sampling skid after the locked instruction just as well. With no other thread entering, there is nothing to wake [inferred, I-474-4].

**Not known:** `InternalEquals` calls/s, the fields-per-call distribution, and which classes are involved. The ~420k/s count also includes the Verify sweep (CAS, build/buy) and other callers. Docs/29 puts ~80% of the lock samples under `0xDA5970`.

## 3. Who contends

| Thread | Takes the lock? | Evidence |
|---|---|---|
| Sim / script thread | yes, all counted enters | Verify slot 46 (I-58-1) |
| Main / render thread | no | `vfMonoE85020Main` = 0 |
| Engine job threads | no: they run no Mono code | `Other` = 0. No EA code outside Mono takes it (§1) |
| Route workers (`[RouteAsync]`) | no: managed invokes are proxied back to the caller thread | docs/18 [inferred]; `Other` = 0 |
| **AutShadow worker** (`[AutShadow] Enabled=1 Unseen=1`, the master ini default; CHANGELOG "Unreleased") | **can**: it interprets autonomy scoring on its own thread, and binder rows 152/194 are `reachable_from_worker = yes` (docs/220). Scoring compares boxed enums (docs/97 `GetTuning`) | static [inferred, I-474-5]; **no counter exists from a run with AutShadow on** |

**Hitch / ms/s attribution:** the measured cost (~8.7-9.8 ms/s) is per-call overhead on the sim thread. It is not a wait. None of the hitch causes in docs/247 §11 (wildflower, boat replans, CasTriSort) involve this lock. With the AutShadow worker enabled, any overlap between worker scoring and sim `Enum.Equals` traffic turns uncontended pairs into kernel waits. That would be a new sim-thread cost that no run has measured.

**Counter needed:** one short BP job with `[IcLockCheck] Enabled=1` (counters v15 at `+0x1DC`: `icLockEntersK`, `icLockContended`, `icLockWaitUs`, `icLockMaxWaitUs`, `icLockOwnerChanges`, `icLockPrevTid`/`icLockLastTid`, `icLockLeaveWaiters`) **with the master default `[AutShadow] Unseen=1`**, run as a profile job so the native counter json is saved. The 10-16 soak lost its counters because it was a soak, not a profile. Pass: `icLockContended` / `icLockEntersK*1024` < 0.1% and `icLockWaitUs` < 0.5 ms/s, meaning contention is negligible. Otherwise contention is real and option C also cuts the worker's share.

## 4. What must not be done: skip the lock when `size_inited` is set [verified]

Upstream later Mono skips the lock in `mono_class_get_fields` once fields are set up, so a lock-skip patch at `0xE70180` keyed on `klass+0x14` bit 2 looks tempting. In this build that bit is a **recursion guard, not a done flag**. `0xE6E600` sets it at `0xE6E7A3` (`or [ebp+0x14], 4`) *before* it allocates the field array (`0xE9A3F0`, stored to `+0x88` at `0xE6E7BE`) and fills the fields. A second thread that sees the bit could read a NULL or half-filled array. Today only the lock prevents that, by holding the reader until setup finishes.

## 5. Design options

1% of sim samples ≈ 10 ms/s (docs/28). Every estimate here is [inferred] from §2.2 and needs an A/B.

| # | Option | Cuts | Est. ms/s | Build effort | Safety risk | Verdict |
|---|---|---|---|---|---|---|
| A | Skip the lock in `0xE70180` when `size_inited` is set | lock pair per `get_fields` | ~8 | 30 min | **unsafe** (§4): half-built field arrays visible to the AutShadow worker | reject |
| B | **Done-set** skip in `0xE70180`: our own per-class set of classes whose setup finished. A class is marked after a locked `0xE6E600` returns while the section's `RecursionCount` is 1 (outermost holder, so no setup in flight on this thread). A hit skips the lock and the setup call | lock pair + setup call per `get_fields`, all 38 callers (reflection too) | 5-8 (ceiling ~8.7) | 1-2 h + selftest | low-medium: assumes classes are never freed (TS3 does not unload domains [inferred]); lock-free set reads; the field walk stays | fallback if C is blocked |
| C | **Per-class fast path for `InternalEquals`** (detour at `0xE7A590`, prologue `55 8B EC 83 EC 10`). After a vanilla call returns for a class whose instance fields are all I4/U4/I8/U8 (at most 4 fields, no STRING, R4/R8, short ints or references), cache `(class, [offset,size]...)`, published with `InterlockedCompareExchange`. On a hit with non-null `that`: vtable compare (false if different, as vanilla), compare the cached ranges, `*fields = NULL`, return. Everything else goes to vanilla | **all** lock pairs and the whole field walk for int-backed enums and int-only structs | **6-9** (ceiling ~9.9 = `0xDA5970` live chain; `Enum.Equals` self 9.8) | 2-3 h + monohost corpus | low: the result is defined by vanilla's own switch for exactly the cached kinds; the cache is built only from classes a finished vanilla call already walked; null and exception paths stay vanilla | **recommended** |
| D | Reader/writer split (SRW lock in place of the CS) | nothing | ~0 | 2-4 h | high: the CS is entered recursively (setup recurses through parent and generic classes with the lock held), SRW is not recursive, and an uncontended SRW costs about the same | reject |
| E | Batching: one lock around the whole `InternalEquals` walk | K+1 of K+2 pairs | 4-7 | 1-2 h | medium (holds the global lock across boxing allocations) | subsumed by C |
| F | Remove the calls in script: typed comparers (docs/74) and per-site ts3fixes hooks (the walk-style hook, docs/04) | per call site | per site | per site | per hook | ongoing, complementary |

**Overlaps checked:**

- **Apex Radiance:** none. `game-copy/apex-src-3.0.0` has none of `0x11F43A8`, `0xE85020`/`30`, `0xE70180`, `0xE6E600`, `0xE7A590`, `0xDA5970`, `0xDA7FB0`. Apex's nearest Mono site is the type-object path `0xEA8A00`. This is the Mono and script lane, which docs and memory assign to us.
- **Our own hooks:** `[IcLockCheck]` and `[Verify]` slot 46 detour `0xE85020`/`0xE85030` (docs/68). C does not touch those sites. A run with C on shows fewer enters in both, which is the expected signal. MonoJit's compiled `Enum.Equals` still calls the EA body and gains the same way. S3PP's Mono caches sit at `0xEBBDE0` and `0xEA3820` (docs/17), not here.
- **Household rule:** C is an exact runtime speedup with the same boolean for every caller and no Sim scope, so it needs no household exemption (CLAUDE.md "proven-exact speedups stay").

**Optional extension of C:** add 1- and 2-byte enums (I1/U1/I2/U2). Vanilla boxes both values and allocates a pair array for these, then calls the managed `Equals`. A byte compare gives the same boolean without the allocations. It needs its own corpus case and a sub-switch, because it changes allocation counts, though not results.

## 6. Recommendation and gates

Build **C**, off by default, with its own counters: calls, hits, misses by reason (not cached / null / kind), and classes cached.

1. **Exactness (host):** monohost corpus over every enum and value type in the 10 script assemblies plus mscorlib, with random value pairs (equal, unequal, cross-type). Fast result must equal vanilla on every pair; `*fields` NULL on hits.
2. **Measurement smoke + count:** one BP job with C on and `[IcLockCheck]` on. Gives hit rate, lock enters before and after, and the §3 contention answer with AutShadow on.
3. **Timing:** BP/IP A/B per the throttle rules. ON by default if it shows a consistent 3-5 ms/s and exactness passes. Expected 6-9 ms/s.

If 1 fails on a class kind, drop that kind from the cache. If 2 shows hit rate < 50%, fall back to B for the field walks that remain.

Ledger: docs/51 §474.

## 7. Option C built, NOT RUN (2026-11-12, ts3native 0.36.246, branch `feature/valuetype-equals-fast`)

No game run yet. Gate 1 (host exactness) passed. Gates 2 and 3 are prepared in `tools/harness/queues/equalsfast-1112.json`.

**What was built** (`native/ts3native/equalsfast.cpp`, ini `[EqualsFast] Enabled=0`, off in code and in the repo ini):

- Detour on `0xE7A590`, 6 prologue bytes byte-checked (`55 8B EC 83 EC 10`). Listed in docs/68 next to `[IcLockCheck]`. `--ab equalsfast` flips `eqMode` (+0x5F94).
- **Plan building.**
  - Trigger: the first call for a class goes to vanilla. A plan is built after that call returns.
  - Field walk: the game's own `0xE70180` walk, under the game's lock, with vanilla's skip rules (statics; special-name `_Deleted`).
  - Eligible classes: byval_arg type VALUETYPE (0x11), only I4/U4/I8/U8 instance fields, at most 8 of them. Int-backed enums are `value__` only.
  - Everything else is cached as "not eligible" and always goes to vanilla. That covers R4/R8 (NaN, -0), short ints, bool, char, string, refs, nested structs, I/U, and generic instances (0x15).
- **Publication.** Each plan is written to a private pool slot, then published with one `InterlockedCompareExchangePointer`. Plans are immutable after that. The pool holds 4096 plans; the open-address table has 8192 slots with a probe window of 16.
- **Hit path.** No lock: vtable compare, dword compares of the planned ranges, `*fields = NULL`.
- **Fall-through to vanilla:** null `that`, different vtables, not eligible, switch off, pool full.
- **Thread that holds the loader lock** (`OwningThread` = this thread): it gets the vanilla answer and builds no plan, because class setup may be in flight (§4). Option A is not used.
- Plans assume MonoClass memory is never freed (a `shortcut:` comment in the code).

**Counters, v186 at 0x5F90-0x5FCF** (docs/registry.md; 0x5F80-0x5F8F is held for v185 `sb*`):

| Group | Counters |
|---|---|
| State | `eqOn`, `eqMode` |
| Results | `eqHits`, `eqPlans`, `eqRejects` |
| Fall-through | `eqFtNull`, `eqFtType`, `eqFtKind`, `eqFtNew`, `eqFtBusy`, `eqFtFull` |
| Reject reasons | `eqRejGeneric`, `eqRejKind`, `eqRejMany` |
| Spare | `eqSpare[2]` |

The game log names the first 48 classes it saw: either `equals fast: plan ...` or `... stays on EA's walk: <reason>`.

**Checks [measured on the host, no game]:**

- **Native selftest** (`equalsfast-test.h`) passed. It runs a C model of vanilla's switch over fake MonoClass layouts and covers:
  - an enum, an I8+U4 struct, mixed R4 with -0 and NaN, U1, generic 0x15, and 9 fields
  - `_Deleted` against another special name
  - null `that`, different vtables, the switch off, a busy loader lock, and a full pool
  - 20000 random pairs, and 4 threads × 64 classes racing to build and publish
  - the ini on and off
- **Monohost exactness corpus** (`MONOHOST_EQFAST=1`, `native/monohost/EqCorpus.cs`): passed with 0 mismatches.
  - Scope: the game's own Mono runs every value type in mscorlib, System, System.Xml and the 7 script assemblies, plus 3 KeyValuePair generic instances.
  - Inputs: 6 values per type (equal, unequal, -0/NaN for floats, enum values), all pairs, and a cross-type pair.
  - Comparison: vanilla `0xE7A590` first, then the hook, twice (the first call builds the plan, the second answers from it).
  - Mismatch rule: a different answer, or a hit that leaves `*fields` non-NULL.

  `PASS EQ-corpus-exact 61159 pairs, 0 mismatches` and `PASS EQ-corpus-hits 46872 plan answers (44247 enum), 2232 plans, 548 not eligible`. The full summary line:

  `2777 value types (0 skipped, 4 with pointer fields left out), 61159 pairs ..., 0 mismatches; plans 2232, not eligible 548 (not VALUETYPE 15, field kind 532, > 8 fields 1)`

  Structs with pointer fields are left out of the corpus because vanilla boxes PTR fields and that path hangs in the host (seen on `SimpleCollator+Context`). Such structs are not eligible for a plan anyway (field kind).
- **Other host checks:**
  - The monohost selftest (40 passed) and the `jit-replay --fixture` run (differ 0) still pass.
  - `hook-sites.ts --check`, `native-switches.ts --self-check`, `fix-repro-check.ts` (EXEMPT: exact speedup), and `queue.ts --self-check` plus `--dry-run` of the queue file all pass.

**Next (gates 2 and 3):** `node tools/harness/queue.ts tools/harness/queues/equalsfast-1112.json` (about 55 min). Build ts3native and ts3fixes first.

- **`bp-iclock-eqfast-ab`** is a count job: `[IcLockCheck]` on, AutShadow at the repo default.
  - Off arms answer the §3 contention question.
  - On arms give the hit rate. If it is below 50%, fall back to B.
- **`bp-eqfast-ab` and `ip-eqfast-ab`** are the timing pair: in-load ABBA of `--ab equalsfast`, 1.5-min slices, 4 per arm. Read them with `hitch-ab.ts` and `arm-ms.ts`. ON by default only with a consistent 3-5 ms/s gain.
