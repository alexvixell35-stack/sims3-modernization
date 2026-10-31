# 207. Compile candidates: never-yielding managed time

Written 2026-10-30. Static, from existing runs: no build, no game run, no queue. Question (Rodney): how much sim-thread interpreter time is spent in managed methods that can never pause a script task, directly or through any callee? That is the ceiling for an ahead-of-time or at-load compiler of non-yielding methods (docs/29 option d). Tool: `tools/compile-candidates.ts` (`--self-check`). Data: `docs/data/compile-candidates.csv`.

Tags: **[measured]** = counted from existing managed samples; **[verified]** = from the call-graph index of the Steam 1.67 script assemblies (docs/78); **[inferred]** = reasoning, not yet measured.

## 1. Method

- **Samples.** Managed sampler stacks (`probe-out/profile/<tag>-managed.jsonl`, leaf first) of the v0.2.x release builds, two 10-minute runs per world pooled: `sim-budget-v024-2026-10-29-<w>-simbudget-v024` (docs/160 queue) and `release-validate-2026-10-28-<w>-rel-play`. BP 2,050 samples, IP 1,772, SV 1,771.
- **Interpreter self time.** A sample counts when its leaf is a managed method. Leaves that are internal calls (native, the docs/160 "ICs" bucket) and `ScriptCore.Route.*` leaves (route sync plans) are left out, as in docs/160. Counted: BP 780, IP 606, SV 668. The counted share of all samples (BP 38%) matches the docs/160 split of the BP sim-budget run (329 Mono of 886, 37%) [measured].
- **Yield closure.** Every method of the docs/78 index gets one class [verified]:
  - **Y** (can yield): the method yields itself (`Simulator.Sleep`/`Yield*`, `SACS_AcquireDriverImpl`, `StateMachineClient.RequestState`, the docs/78 `YIELD` rules), or any callee can. Virtual, interface and constrained calls count every game override (class hierarchy, as docs/127 `virtualTargets`).
  - **D**: no yield except possibly through a delegate `Invoke`, whose target is unknown.
  - **N**: never yields.
  - **IC**: native binding.
  - **?**: the frame is not in the index (mods, our own ts3fixes hooks).
  A sample frame name has no parameter list, so it maps to every overload, and the worst class wins.
- **ms/s.** Shares are applied to docs/160's v0.2.4 "Mono script" bucket: BP 334.3, IP 335.7, SV 358.0 ms/s (`probe-out/queue/simbudget-v024-report.txt`). At speed-up s, the gain is share × Mono × (1 − 1/s) [inferred].
- **Region root.** For each N or D leaf, the tool walks outward while the frames stay N or D. The outermost such frame is where a compiled region would be entered (CSV rows of kind `root`).

## 2. Result per world

| World | Managed-leaf samples | N | D | Y | ? (mods) | N+D ms/s | Gain at 2x | Gain at 3x |
|---|---|---|---|---|---|---|---|---|
| BP | 780 | 71.4% | 2.4% | 20.0% | 6.2% | 246.9 | 123 | 165 |
| IP | 606 | 72.3% | 3.3% | 20.3% | 4.1% | 253.7 | 127 | 169 |
| SV | 668 | 71.9% | 3.3% | 19.2% | 5.7% | 269.0 | 134 | 179 |

The shares are [measured]. The ms/s values are shares times the docs/160 bucket. The gains are [inferred] upper bounds (see §5). Counting N only, without the D share, the gain is BP 119 / 159, IP 121 / 162 and SV 129 / 172 ms/s at 2x / 3x.

About **72% of interpreter self time is in methods that can never yield**, the same in all three worlds [measured]. The remaining ~20% is self time inside yielding methods: the simulate loops and interaction bodies. Its share is small because those methods mostly call out and spend little time in their own bodies.

## 3. Top never-yielding methods (mean ms/s over the three worlds, self time)

| # | Method | Assembly | IL bytes | ms/s | IL features |
|---|---|---|---|---|---|
| 1 | `Dictionary`2.GetPrev` | mscorlib | 106 | 12.9 | callvirt, newobj, box, throw, generic |
| 2 | `Enum.Equals` | mscorlib | 8 | 9.8 | call (reflection-backed, see §5) |
| 3 | `RuntimeTypeHandle.get_Value` | mscorlib | 7 | 7.4 | none |
| 4 | `Enumerator.MoveNext` (all collection enumerators) | mscorlib + game | 1,603 total | 5.7 | generic, box, switch |
| 5 | `Enumerator.get_Current` | mscorlib + game | 937 total | 5.5 | generic |
| 6 | `Array.Resize` | mscorlib | 76 | 4.8 | newarr, generic |
| 7 | `Dictionary`2.TryGetValue` | mscorlib | 46 | 4.6 | generic |
| 8 | `GameObject.GetComponent` | Sims3GameplaySystems | 88 | 4.5 | callvirt, unbox, cast, generic |
| 9 | `InteractionObjectPair.Equals` | Sims3GameplaySystems | 48 | 3.7 | callvirt, cast |
| 10 | `Dictionary`2.GetSlot` | mscorlib | 29 | 3.5 | generic |
| 11 | `Array.Copy` | mscorlib | 755 (overloads) | 3.1 | try, throw |
| 12 | `CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags` | SimIFace | 977 | 3.0 | none (bit tests) |
| 13 | `GameObject.GetObject` | Sims3GameplaySystems | 54 | 3.0 | unbox, cast, generic |
| 14 | `Type.GetTypeFromHandle` | mscorlib | 13 | 2.8 | call |
| 15 | `DefaultComparer.Equals` | mscorlib | 38 | 2.8 | box, generic |
| 16 | `SimClock.ElapsedTimeInMinutes` | Sims3GameplaySystems | 17 | 2.7 | call |
| 17 | `List`1.GetEnumerator` | mscorlib | 7 | 2.3 | newobj, generic |
| 18 | `GameObject.get_Parent` | Sims3GameplaySystems | 19 | 2.2 | call |
| 19 | `IEquatableOfTEqualityComparer`1.GetHashCode` | mscorlib | 14 | 2.0 | generic |
| 20 | `List`1.Add` | mscorlib | 51 | 1.8 | generic |
| 21 | `List`1.get_Item` | mscorlib | 33 | 1.8 | throw, generic |
| 22 | `ScriptObject.get_ObjectId` | Sims3GameplaySystems | 23 | 1.7 | callvirt, ldsfld |
| 23 | `List`1.GrowIfNeeded` | mscorlib | 47 | 1.6 | generic |
| 24 | `UInt64.GetHashCode` | mscorlib | 14 | 1.5 | none |
| 25 | `ObjectGuid.op_Inequality` | SimIFace | 20 | 1.5 | none |
| 26 | `GameObject.get_LotCurrent` | Sims3GameplaySystems | 55 | 1.5 | callvirt, cast |
| 27 | `Enum.GetHashCode` | mscorlib | 14 | 1.4 | callvirt |
| 28 | `Members.GetActorsOfType` | Sims3GameplaySystems | 85 | 1.3 | try, newobj |
| 29 | `Array.IndexOf` | mscorlib | 320 (overloads) | 1.3 | generic, throw |
| 30 | `Slots.GetParentId` | SimIFace | 12 | 1.2 | callvirt, ldsfld |

IL sizes and features come from the IL bodies [verified]. The ms/s values carry ±20-40% Poisson error at 10-80 hits per row [measured]. Call counts are not available, because the sampler records stacks, not calls. Mint superinstructions (docs/130, `0x1AB`-`0x1C0`) are an interpreter-time fusion and do not show in IL.

**Profile of the candidates.** The top 30 are almost all tiny (median ~40 IL bytes). Two thirds are BCL generics: collections, comparers and enum/type helpers. The rest are game accessors. The **region roots** are small too: `Household.get_AllActors`, `Dictionary.TryGetValue`, `GameObject.get_LotCurrent`, `RuntimeTypeHandle.get_Value`, and the native event delegate `OnNativeEventDelegate.Invoke`. Each region is one accessor plus the BCL calls under it, entered straight from yielding interpreted code.

## 4. What stays interpreted (top Y, self time, mean ms/s)

| Method | IL bytes | ms/s | First yield path |
|---|---|---|---|
| `SimUpdate.Simulate` | 1,817 | 9.2 | `Simulator.Sleep`, 1 hop |
| `Lot.Simulate` | 214 | 5.3 | `Sleep`, 1 hop |
| `Sim.DoInteraction` | 737 | 2.9 | `Sleep`, 1 hop |
| `Motive.UpdateMotiveWithDecay` | 5,132 | 2.1 | `RequestState`, 8 hops |
| `Motive.UpdateMotiveBuffs` | 1,566 | 2.0 | `Sleep`, 6 hops |
| `InteractionDefinitionUtilities.CommonTests` | 926 | 1.8 | through a game override |
| `Sim.WaitForExitReason` | 69 | 1.6 | `Sleep`, 1 hop |
| `Lot.UpdateReactions` | 209 | 1.6 | `RequestState`, 10 hops |
| `InteractionDefinition`3.Test` | 518 | 1.4 | `RequestState` |
| `IdleManager.DistressIdle` | 246 | 1.4 | `RequestState`, 3 hops |

Several of these are Y only through deep or virtual paths: the motive updates, `CommonTests` and `Test`. With per-call-site receiver typing (docs/127), or a guard that falls back to the interpreter when a yield is reached, some of them could join the compilable set [inferred]. That is worth up to the ~20% Y share, mostly spread thin.

## 5. Why the gain is a ceiling, and blind spots

- **Small methods.** The compiled set is dominated by 7-100 byte methods. docs/130 puts ~43% of interpreter self time into call/return. A compiler that compiles only leaves, while callers stay interpreted, keeps one interpreted-to-compiled transition per call, so 2-3x on these bodies is optimistic. A realistic first estimate is the 2x row or below, unless whole regions, inlining or a compiled-to-compiled calling convention cut the transitions [inferred].
- **Runtime helpers inside self time.** `Enum.Equals`, `RuntimeTypeHandle.get_Value`, `Type.GetTypeFromHandle`, `box` and `newobj` call into the Mono runtime (type lookups, boxing, allocation). Compiled code still calls those helpers, so part of their self time does not shrink [inferred]. `Enum.Equals` plus the type-handle helpers alone are ~20 ms/s, and a cheaper option for them is a fast path in the runtime (docs/139) or a script change that avoids boxing an enum.
- **Over-approximation.** Every game override of a virtual counts as a possible yield (CHA), so N is a lower bound for "truly never yields". Delegate invokes are put in D (2-3%). BCL virtual targets are not expanded (docs/127). A game type that overrides a BCL virtual and yields, such as an iterator `MoveNext`, would be missed. None is known [inferred].
- **Name matching.** Frames have no parameter list and print nested types without their outer type, so `Enumerator.MoveNext` merges every collection enumerator. The worst class across overloads wins.
- **Mods.** 4-6% of self time is in mod or ts3fixes frames that are not in the index (NRaas ErrorTrap, our hooks): class `?`, left out of N. The index is the game-only build of 2026-10-16. Mod overrides of game virtuals are not in the yield closure (docs/128).
- **Sampler.** About 1,800-2,000 samples per world, from two runs of one release family. The sampler sees the managed stack only, so the leaf is the innermost managed frame and time in runtime helpers below it is booked to that frame. No call counts.

## 6. Answer

About **72% of sim-thread interpreter self time (~245-270 ms/s of the ~335-358 ms/s Mono bucket)** is in methods that can never yield [measured share, docs/160 scale]. Compiling all of it would save **~120-135 ms/s at 2x and ~165-180 ms/s at 3x** [inferred upper bound]. The time is spread over many tiny BCL generics and accessors, so the reachable part depends on removing per-call transitions, not just on speeding up method bodies. Next step if pursued: measure the interpreted-to-compiled transition cost with a monohost prototype that compiles the top 30 (docs/139 bench), before any in-game build.

## 7. P0 prototype: a method compiler in monohost (2026-10-30)

Rodney approved P0: a monohost-only compiler for the hottest never-yielding methods, to measure the real speed-up and the cost of switching between the interpreter and compiled code before anything goes near the game. Everything below ran offline in monohost (docs/119, docs/139). No game run, no queue, nothing in the game folder. Code: `native/monohost/monohost-jit.cpp` (included by `monohost.cpp`), workload `HostTest.JitBench` in `native/monohost/HostTest.cs`.

Tags as above, plus **[measured offline]** = monohost run on this machine (machine in normal use, pinned core, best of N).

### 7.1 How it works [verified: host runs]

| Piece | What it does |
|---|---|
| Hook | transform `0xE643B0(RuntimeMethod*, ThreadContext*)` is wrapped (prologue `55 8B EC 83 EC 78` checked, trampoline). After EA's transform and peephole, a method whose name ends in a listed `Class.Method` is compiled. Generic instantiations are separate `RuntimeMethod`s, so each one is compiled on its own. |
| Entry | Every instruction start of a compiled method gets its opcode word replaced by `0x1A9`, an opcode EA never emits (its jump-table entry was the "Unimplemented opcode" handler). The table entry for `0x1A9` points to `JitEnter`. The original words are kept in a shadow array. Operands, ip values, branch offsets and clause ranges are not touched. |
| `JitEnter` | Looks up `edi` (the ip) in an open-addressing table (65,536 x 16-byte entries, ~10 instructions on a hit), loads `ebx` = argument area (`MonoInvocation+0x10`) and `edi` = locals (`[ebp-0x14]`), and jumps to that instruction's native code. So entry works from a call, from a return into the middle of a compiled method, and after any exit. |
| Native code | Runs inside the interpreter's own C frame. `esi` stays the eval-stack pointer and every argument, local and stack slot stays in the interpreter's frame memory, written with the same widths as EA's handlers (a 4-byte load writes 4 bytes of the 8-byte slot, as EA does). The conservative GC sees exactly the memory it sees today (docs/57): no managed reference lives only in a register across anything that can collect. |
| Templates | 150 base opcodes have templates: argument, local and field loads and stores of every integer width, `ldthis`, address-of, constants, `dup`/`pop`, all short and long integer branches, `ldlen`, integer `ldelem`/`ldind`/`stind`, integer ALU, compares and widening conversions. EA's 22 superinstructions are compiled as their base sequences (the inner opcode words are still in the code, docs/139 §4.1). |
| Exits | Everything else (calls, `callvirt`, `newobj`, `ret`, boxing, casts, statics, strings, float, value-type moves, switch, throw) leaves through an exit: `edi` = ip, `[ebp-4]` = ip, `eax` = `edx` = the base opcode, then `jmp` to EA's own handler. The handler ends in the dispatch head, which reads the next word: `0x1A9` again, so control comes back to native code. Every check that can throw (null receiver, array bounds) is tested first and, on failure, exits to EA's handler before anything changed; EA then throws with the same ip, frame and stack (P0 bails to the interpreter on every throw path). |
| Switches | `MONOHOST_JIT=off\|dump\|on\|verify`, `MONOHOST_JITLIST=<names>` (per-method enable list; default 38 names: the §3 methods that run offline, their BCL callees and three bench methods), `MONOHOST_JITEXITALL=1` (every instruction an exit: switch-cost measurement), `MONOHOST_JITCOUNT=1` (re-entries and exits counted), `MONOHOST_JITVERIFY=bench` (verify only the bench workloads), `MONOHOST_JITREPS=<n>`. The bench toggles compiled code on and off in one process by restoring or re-patching the opcode words, so "interpreted" is EA's untouched code. |
### 7.2 Verify mode

`MONOHOST_JIT=verify` runs every verifiable call twice from the same state [verified: host runs]:

1. At a fresh call (`JitEnter` at word 0) it snapshots the frame's arena (arguments, locals, eval stack), the free arena space after it (where callee frames go, so dead bytes that `ret` copies are the same in both passes), the stack arguments, and the **whole object graph reachable from the receiver and the arguments** (Boehm blocks via `GC_base` `0xE4DC30` and the block header, plus 32 bytes behind a byref that points into a caller frame of the same activation). Limit 4,096 objects / 1 MB; a larger graph runs compiled only and is counted.
2. Pass 1 runs the compiled code. Compiled callees run compiled.
3. At the compiled `ret` it records the return value and all snapshot bytes, keeps copies of objects the pass allocated, restores the snapshot and the interpreter frame words `[ebp-8]..[ebp-0x1C]`, and restarts at word 0 in EA's own handlers.
4. Pass 2 runs EA's interpreter for the method **and every compiled callee** (one verification in flight per activation), so it is a pure-interpreter reference for the whole call tree.
5. At pass 2's `ret` it compares the return value and every snapshot byte. Two words that differ only because each pass allocated its own object are equal when the objects are equal word by word (recursively, 3 levels). Dead memory is restored but not compared: eval-stack slots at or above the `ret`'s stack pointer (EA's superinstructions skip temporary stores their base sequences make) and the callee-frame space. A difference gets one retry from the same entry state (a one-time effect of the first pass, such as a class initialiser or a lazily filled cache outside the graph, is then in place for both passes); a real compiler difference repeats.
6. Then the bench cases and `MintBench` run again with EA's untouched code (opcode words restored) and their checksums must match.

Two harness details were needed to get there, both recorded because they are traps for any later verifier: a shallow snapshot is not enough (Mono 1.2.3's `Dictionary` relinks its `Slot` objects on resize, three references away from the dictionary, so restoring only the first level corrupted the dictionary for pass 2), and `GC_malloc_atomic` blocks keep old bytes after the value, so verify mode redirects `GC_malloc_atomic` `0xE4BDC0` to `GC_malloc` `0xE4BE30` while no M5 worker runs (the redirect is off while M5 installs its P3 detours on the same entry points).

**Result [verified: host run 2026-10-30]** over the whole selftest (M1.1-M1.5 including the 9,840-type `.cctor` sweep, M2, M5 up to the [AutShadow] install check) plus the bench (21 cases at n = 1,024) and `MintBench(300)`:

| Item | Count |
|---|---|
| Methods compiled | 236 instantiations of 38 listed names |
| Verified calls (outermost compiled frame, both passes compared) | **1,992,404** |
| Compiled calls inside a verified caller (compared as part of the caller's tree) | 6,859,040 |
| **Mismatches** | **0** |
| Retried after a first-attempt difference | 1 (then equal: a one-time effect in the `.cctor` sweep) |
| Ran compiled only, graph over the snapshot limit | 2 |
| Abandoned by an exception / methods with clauses | 0 / 0 |
| Bench and `MintBench` checksums vs EA's untouched code | 21 of 21 equal, `MintBench` equal |
| Selftest checks | 32 passed, the same set as with the compiler off; the one failure (`AS-install`: [AutShadow] contract version) fails identically with `MONOHOST_JIT=off` and belongs to the [AutShadow] lane |

Not covered by verify [inferred]: writes to static fields of classes not reachable from the arguments (the graph walk does not visit static storage unless an object points at it), the M5 worker thread (compiled code runs there unverified; M5's own result checks pass), and the game's `Sims3Gameplay*` accessors, which need world objects and were not compiled offline.
### 7.3 Speed per method [measured offline]

`MONOHOST_JIT=on`, `HostTest.JitBench`: each case calls one candidate 200,000 times from an interpreted loop; best of 9 runs per side, compiled and interpreted interleaved in one process (opcode words patched and restored between runs), one pinned core, high priority, machine in normal use (other load ~9% of the CPU during the runs). The empty loop (10.8 ns per iteration) is subtracted. Times are per call and include the interpreted caller's call into the method, which compiled code does not change. Checksums equal in every case.

| # | Case (compiled methods on the path) | Interpreted | Compiled | Speed-up | Switches per call |
|---|---|---|---|---|---|
| 0 | `Dictionary<int,Shape>.TryGetValue` (TryGetValue, GetSlot, GetPrev, comparer) | 197.7 ns | 173.6 ns | 1.14x | 12.8 |
| 1 | same on `Dictionary<ulong,int>` with a 256-entry collision chain (GetPrev loop, `UInt64.Equals`) | 11,245 ns | 9,635 ns | 1.17x | 973 |
| 2 | `Dictionary<ObjectGuid,int>.TryGetValue` (`DefaultComparer`, boxing) | 302.0 ns | 316.0 ns | 0.96x | 30.5 |
| 3 | `List<T>` foreach, per element (`MoveNext`, `get_Current`) | 59.8 ns | 42.2 ns | 1.42x | 2.0 |
| 4 | `List<T>.get_Item` + `Count` | 43.5 ns | 40.2 ns | 1.08x | 2.0 |
| 5 | `List<int>.Add` with `GrowIfNeeded`, `Array.Resize` | 101.8 ns | 94.8 ns | 1.07x | 5.6 |
| 6 | boxed `Enum.Equals` + `Enum.GetHashCode` | 679.0 ns | 691.9 ns | 0.98x | 6.0 |
| 7 | `GetType() == typeof(...)` (`Type.GetTypeFromHandle`) | 198.4 ns | 170.4 ns | 1.16x | 4.0 |
| 8 | `Array.Resize<int>` | 493.1 ns | 485.7 ns | 1.02x | 6.9 |
| 9 | `Array.IndexOf<int>` over 64 elements | 1,956 ns | 1,662 ns | 1.18x | 136 |
| 10 | `ObjectGuid !=` | 53.8 ns | 60.9 ns | 0.88x | 4.0 |
| 11 | `CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags` (977 IL bytes of bit tests) | 89.1 ns | 52.5 ns | **1.70x** | 3.1 |
| 12 | `Dictionary` foreach, per element | 220.7 ns | 234.8 ns | 0.94x | 8.1 |
| 13 | `ContainsKey` + `set_Item` | 437.9 ns | 358.4 ns | 1.22x | 27 |
| 14 | `JitId(x)`: trivial, compiled (`stinarg`, `ldarg`, `ret`) | 13.9 ns | 13.0 ns | 1.06x | 1.0 |
| 18 | `JitSum`: 64-element loop, no calls, per call | 1,056 ns | 307 ns | **3.4x** | 0.02 |
| - | `MintBench(3000)` (docs/139 workload: List/Dictionary generics, enumerators, boxed `Enum.Equals`, `GetType`) | 349.7 ms | 286.6 ms | **-18.1%** | - |

"Switches per call" = interpreter-to-compiled re-entries (`MONOHOST_JITCOUNT=1`); every one follows an exit (call, `callvirt`, `ret`, `newobj`, box, unsupported opcode). Twin cases with the same bodies left interpreted (`JitPlain`, `JitOutPlain`, `JitSumPlain`) stayed within ±4%, which is the noise of this setup.

What the numbers say:

- **Straight-line and loop bodies compile well**: 3.4x on a call-free loop, 1.7x on the bit-test method, 1.4x on the `List` enumerator. A template compiler without register allocation already removes most of the ~1.9 ns per mint instruction (docs/139 §1) where the code stays in compiled code.
- **The candidates are call-bound**: the hot BCL methods are a few instructions between calls (`Enum.Equals` is 4 mint instructions: argument move, `this`, `call`, `ret`). Every call, `callvirt` and `ret` still takes EA's call path (~190 instructions per call, docs/139 §5) plus one switch, so 1.0-1.2x is all these methods get; the ones that are almost only calls and boxing (`ObjectGuid !=`, boxed enums, `DefaultComparer`, `Dictionary` enumeration) get 0.88-0.98x and must stay interpreted.
- `MintBench` gains 18% because its hot loop is dominated by the enumerator and dictionary bodies with few calls per instruction; it is the optimistic end, not the game's mix.

### 7.4 Switch cost [measured offline]

- **Interpreter to compiled (entry)**: the interpreted call path is unchanged; `JitEnter` replaces the first instruction's dispatch with a hash lookup (~10 instructions) plus two frame loads. `JitId` (trivial, compiled) took 13.0 ns per call against 13.9 ns interpreted: the entry costs nothing measurable beyond the dispatch it replaces.
- **Compiled to interpreter and back (exit + re-entry)**: measured with `MONOHOST_JITEXITALL=1` (every instruction an exit) against the interpreter, divided by the counted switches: **~1.0-1.5 ns per round trip** in most cases (`JitId` 1.0, `JitSum` 1.42, `Array.IndexOf` 1.43, `CASUtils` 1.45, `List` foreach 1.39), up to ~3 ns in the call-heaviest cases (`TryGetValue` 3.0, `ContainsKey + set_Item` 3.4). That is ~0.5-1.5x the cost of one mint dispatch on top of the instruction itself. With every instruction an exit, `MintBench` runs 92% slower.
- So switching does not eat the body speed-up (methods with 2-6 switches per call still win where they have straight-line work), but it sets the floor: a method that is mostly calls cannot win while calls go through the interpreter.

### 7.5 Projection to the game [inferred]

Each top-30 method's self time (§3, mean of the three worlds) times (1 - 1/speed-up), with the speed-up of the bench case that runs it (the game accessors `GameObject.GetComponent`, `InteractionObjectPair.Equals`, `get_Parent`, `get_ObjectId`, `get_LotCurrent`, `GetObject`, `SimClock.ElapsedTimeInMinutes`, `GetActorsOfType`, `Slots.GetParentId` could not run offline and get the `List.get_Item` accessor ratio, 1.08x); a method that gets slower stays interpreted (0); `RuntimeTypeHandle.get_Value` and `Array.Copy` were not compiled (native body / clauses):

| Scope | Self time | Gain |
|---|---|---|
| Top 30 never-yielding methods (§3) | 103 ms/s | **~11 ms/s** (10.9%) |
| All never-yielding self time at the same ratio (§2: ~250 ms/s) | ~250 ms/s | **~27 ms/s** |
| Upper end: the whole never-yielding set behaving like `MintBench` (-18%) | ~250 ms/s | ~45 ms/s |

**Low ~11 ms/s, realistic ~20-27 ms/s, optimistic ~45 ms/s.** The realistic figure scales the top-30 ratio to a long tail of even smaller accessors, so it is more likely high than low. Not counted: the extra instructions a sampling profile books to a caller (a compiled callee's `ret` exit is booked to the callee here), GC or cache effects of 150+ KB more code, and the sim's own data-cache misses (the `callvirt` receiver stall, docs/139 §6), which no compiler removes.

### 7.6 Verdict: stop the body compiler; the call path is the lever

The P0 stop rule is met: the realistic projection (~20-27 ms/s) is under ~30 ms/s. The switch cost is not the problem (~1-1.5 ns per round trip, entry free); the problem is that the never-yielding time sits in tiny methods whose cost is EA's call and return path, which a compiler that keeps EA's frames (the GC and exception model need them, docs/57) still runs for every call. Recommendation:

1. **Do not take the compiler to the game as it is.** Keep `monohost-jit.cpp` as the measuring tool (verify mode included).
2. **Build docs/139 §5 (call/return fast path) next** if script stutter is worked on: it cuts the ~190-instruction call for interpreted and compiled code alike (~15-30 ms/s inferred there), and it is the part this prototype shows is missing.
3. **Revisit the compiler only on top of that**, with compiled-to-compiled calls (a compiled call site that builds the callee's frame directly, no exit and no `JitEnter`) and inlining of tiny callees with no exception path (`get_Item`, `GetSlot`, `List` enumerator members). Re-run this bench then: the same harness gives the answer in one session.
### 7.7 Address space and W^X

- **Code size** [measured offline]: the default list compiled 234 instantiations (37 listed names; every generic instantiation the selftest and bench created is its own method) into **~150 KB** of x86 (4,917 template instructions, 1,090 exits; ~650 bytes per method, ~24 bytes per mint instruction, exits ~20 bytes each). The ip table is 1 MB as built (65,536 x 16-byte entries; 6,007 used), the shadow of original words 12 KB.
- **In game** [inferred]: the game has more instantiations of the same names (every `Dictionary<K,V>` the scripts use), so a top-30 list would compile a few hundred to ~1,000 methods: ~0.2-0.7 MB of code. Sizing the ip table to the compiled set (8,192 entries = 128 KB) keeps the total under ~1 MB of the 32-bit address space, allocated once at load (no growth in play). That is small next to the docs/66 budget, but it is not free: it should come out of a reserved region made at startup, not from fragmenting the heap mid-session.
- **W^X**: the prototype allocates one `PAGE_EXECUTE_READWRITE` pool (4 MB reserved). In game: emit into `PAGE_READWRITE` pages, `VirtualProtect` to `PAGE_EXECUTE_READ` and `FlushInstructionCache` before publishing the ip-table entries and patching the opcode words (compile happens once per method right after transform, so the protect calls are a load-time cost). The mint code itself is in ordinary heap memory (written by EA's transform), so patching opcode words needs no protection change. The jump-table entry for `0x1A9` lives in the exe's `.rdata`-like table and is written once with `VirtualProtect`, as docs/139 does for its table writes.
### 7.8 The [AutShadow] write log (docs/206 option 2b) and compiled code

Another lane builds docs/206 option 2b: while an [AutShadow] worker job runs, sim-thread managed stores log (address, old value) on the first write, and worker-thread loads read the logged old value. It works by swapping mint's store and load handlers in the jump table. Compiled code does not go through those handlers, so a compiler past P0 must cooperate [inferred, design]:

- **Stores on the sim thread.** Every compiled store to managed memory (`stfld.*`, `stind.*`, `stelem.*` once templated, and any store through a byref) gets the barrier inline: `cmp byte [job_active], 0` / `jne slow`. The slow path calls the same log routine the swapped handler uses (first write only), then does the store. Stores to the frame (arguments, locals, eval stack) need nothing, since the worker never reads another thread's frame. Cost while no job runs: one compare and one not-taken branch per heap store, about 0.3-0.5 ns [inferred]; P0's hot methods do 0-3 heap stores per call, against 30-700 ns per call (§7.5).
- **Loads on the worker.** Simplest and safe: compiled code never runs on a worker thread. `JitEnter` already sees the thread (TLS or a per-thread flag); on a worker it dispatches the original opcode (the "interpreted" path of verify mode), so the worker runs EA's handlers, which the other lane swapped. A later step could emit worker-side load templates that check the log, but the worker gets the speed-up only then.
- **Switch flips.** The barrier is compiled in unconditionally (the flag test), so turning [AutShadow] on or off needs no recompile. If the write-log handlers are installed by swapping table entries, compiled exits still jump to whatever handler the table had at compile time (P0 caches `g_opHandler`); a P1 must read the live table entry at exit (one extra load) or re-link exits when the other lane swaps.
- **Effect on the offload's numbers.** Compiling the sim thread's never-yielding methods makes the sim's own scoring and lookups cheaper, so the share of sim time the worker moves off the thread shrinks by the same factor for compiled methods (the worker runs interpreted code). On the other side, the consume re-score at the cut (docs/124: top-N ads re-tested on the sim thread) gets cheaper too, and if the worker later runs compiled code, its jobs get shorter, which shortens the window in which the write log is active (fewer logged first writes). Net: the two add, but each one's measured gain is smaller with the other on; A/Bs must state which of the two was on.

## 8. P0 again, on top of the call fast path (2026-10-30)

Rodney asked for the P0 bench again once docs/139 §8 (call/return fast path) was proven, with two P0 features added first: (a) compiled-to-compiled direct calls and (b) inlining of tiny leaf methods, four arms on the same benches, 0 mismatches in each. Everything offline in monohost; code in `native/monohost/monohost-jit.cpp` (inliner, `MONOHOST_JITINLINE=1`), `native/ts3native/mintcall.cpp` (fast path, `MONOHOST_MINTCALL=fast|verify`), cases 21/22 (`JitInl`, a compiled loop calling three tiny instance methods, and its interpreted twin) in `HostTest.cs`.

### 8.1 The two features as built

- **(b) Inlining [built, verified].** At a compiled `call` or `callvirt` (non-virtual, reference type) site whose callee is a transformed instance method with no clauses and no EA native body and whose body is `stinarg*` plus at most 12 of `ldthis`, `ldarg` (32-bit), `ldfld` on `this` (32-bit), `ldc`, 32-bit ALU / `mul`, compares, `ceq0`, `inc`/`dec`/`neg`/`not`, narrow `conv`, `pop`, ending in one `ret` / `ret.void`, the body runs in registers on the caller's eval stack. `this == null`, a set remoting class or a callee frame that would not fit the current arena chunk take the normal call path from the same state (the fast path when installed; EA's handler throws as before). The caller's stack ends bit-equal to EA's call: the result slot gets EA's high dword (the callee's eval slot 0 in the arena, which EA's 8-byte return copy carries). Listed methods' callees are transformed before the caller compiles (EA would transform them at their first call), so their mint code can be read. Static methods are not inlined (a call could be the one that runs a class initializer).
- **(a) Direct calls [not built as a separate path; reasoned].** With the fast path installed, a compiled call site already enters the fast handler directly (no EA handler on the way: P0's exits jump to the table entry read at install, which is ours). What a dedicated direct path could still remove is the dispatch head + `JitEnter` hash probe at the callee's first word and at the return word, ~10 instructions of ~190-200 per call + return (~5%). The rest is the frame itself: every record, `MonoInvocation` and arena word must exist for exceptions, stack traces, yields and the GC (docs/139 §8.2: the protocol is store-bound), so a "direct" call that keeps EA's frame cannot be much cheaper than the fast path. A real direct call needs lazily built frames (records and `MonoInvocation` written only when an exception, a yield or a native transition needs them): that is P1 design, not a P0 feature.

### 8.2 Correctness [verified: host runs]

| Arm | Verify run | Result |
|---|---|---|
| fast path only | `MINTCALL=verify`, every eligible call | 19.3 M calls compared, 0 mismatches, 39/39 |
| fast path + compiler | `MINTCALL=fast JIT=verify` | 2,002,864 verified compiled calls (+6.76 M inside them), **0 mismatches**, 23 of 23 bench checksums and `MintBench` equal to the interpreter, 39/39 |
| fast path + compiler + inlining | `MINTCALL=fast JIT=verify JITINLINE=1` | 2,012,497 verified (+6.84 M), **0 mismatches**, 23/23, 39/39; 3 call sites inlined, 235 refused |

Why so few inlined sites: the BCL tiny methods on the compile list are struct methods (`Int32.Equals`, the `List` / `Dictionary` enumerators), virtual or interface calls (`EqualityComparer`), static (`ObjectGuid` operators, 8-byte arguments) or plain getters that **mint's own transform already inlines** (a `callvirt` to `return V;` costs ~1 ns in the host). The inliner reaches the next tier (getters with arithmetic, Equals-style compares); the game's `Sims3Gameplay` accessors could not run offline (§7.5), so their coverage is unknown.

### 8.3 Speed, four arms [measured offline; best of 9 pinned, compiled and interpreted interleaved per process; one process per arm]

Quiet machine (2026-10-30 07:08-07:13 UTC, game queue held), ns per iteration (loop overhead included, as §7.3), `MintBench(3000)` in ms. Process to process the same arm moves ~±5% (a second fast + compiler process gave 285.4 / 240.7 ms; the first one's compiled column had a slow outlier on two `List` cases, 267.5 ms). The first, noisy set (a game correctness job running) is kept below it.

| Case (quiet) | stock | fast path | fast + compiler | fast + compiler + inline |
|---|---|---|---|---|
| `Dictionary<int,Shape>.TryGetValue` | 173.8 | 149.3 | 132.0 | 133.7 |
| `Dictionary<ulong,int>.TryGetValue` (collisions) | 9,795 | 9,591 | 8,242 | 8,421 |
| `Dictionary<ObjectGuid,int>.TryGetValue` | 260.0 | 236.6 | 235.9 | 239.4 |
| `List` foreach | 54.1 | 50.8 | 39.0 (2nd process) | 37.7 |
| `List` get_Item + Count | 40.1 | 38.2 | 37.0 (2nd process) | 35.9 |
| `Array.IndexOf<int>` (64) | 1,663 | 1,626 | 1,437 | 1,466 |
| `CASUtils` flags | 71.2 | 69.0 | 33.0 | 34.4 |
| `ContainsKey` + `set_Item` | 376.4 | 337.4 | 307.0 | 305.7 |
| `JitInl` 64 (three tiny instance calls per element) | 62.6 | 59.9 | 53.1 | **~0** (the calls are gone) |
| **`MintBench(3000)`** | **286.1** | **272.4 / 285.4** | **240.7 (-15.9%)** | **234.6 (-18.0%)** |

Stock + compiler (the §7 configuration) in the same window: 243.7 ms (-14.8%).

Noisy set (first runs):

| Case | stock | fast path | fast + compiler | fast + compiler + inline |
|---|---|---|---|---|
| `Dictionary<int,Shape>.TryGetValue` | 187.3 | 162.9 | 141.1 | 141.7 |
| `Dictionary<ulong,int>.TryGetValue` (collisions) | 10,222 | 10,075 | 9,414 | 8,795 |
| `Dictionary<ObjectGuid,int>.TryGetValue` | 283.2 | 251.8 | 252.9 | 261.9 |
| `List` foreach | 56.9 | 56.8 | 39.9 | 40.9 |
| `List` get_Item + Count | 41.7 | 41.4 | 38.4 | 38.7 |
| `Array.IndexOf<int>` (64) | 1,753 | 1,762 | 1,544 | 1,534 |
| `CASUtils` flags | 74.7 | 73.9 | 40.2 | 36.2 |
| `ContainsKey` + `set_Item` | 395.7 | 364.0 | 305.4 | 320.6 |
| boxed `Enum.Equals` / `GetType` / `ObjectGuid !=` | 340 / 164 / 51 | 340 / 167 / 52 | 344 / 166 / 57 | 349 / 167 / 56 |
| `JitInl` 64 (three tiny instance calls per element) | 66.3 | 65.3 | 58.0 | **~0** (the calls are gone) |
| **`MintBench(3000)`** | **310.3** | **297.8 (-4.0%)** | **245.9 (-20.8%)** | **242.2 (-21.9%)** |

Noisy set: stock + compiler gave 267.5 ms (-13.8%). Read together: the fast path adds ~1-3 points to the compiler's cut (quiet: -14.8% -> -15.9%; noisy: -13.8% -> -20.8%), inlining ~2 more in the quiet set; inlining is decisive where it applies (`JitInl`: three calls, ~53-58 ns, removed) and otherwise reaches 3 sites offline.

### 8.4 Projection to the game [inferred]

| Scope | §7.5 (stock calls) | Now (fast path + compiler + inlining) |
|---|---|---|
| fast path alone (all interpreted calls, docs/139 §8.5) | - | ~5-10 ms/s |
| compiler on never-yielding code: ratio of the quiet `MintBench` cuts, -18.0% (fast + compiler + inline) vs -14.8% (stock + compiler) = x1.2 on the §7.5 figures | low 11, realistic 20-27, optimistic 45 | low ~13, realistic ~24-33, optimistic ~55 (includes the fast path's and the inliner's share on that code) |
| fast path on the yielding remainder (not in the line above) | - | ~+3-5 |
| inlining beyond the bench (Sims3Gameplay accessors unknown offline) | - | ~0-5 |
| **Total** | **low 11, realistic 20-27, optimistic 45** | **low ~16, realistic ~27-40, optimistic ~65 ms/s** |

The realistic range straddles the ~30 ms/s rule. It rests on the same offline-to-game scaling as §7.5, which the in-game fast-path A/B (`mintcall-ab`) tests directly.

### 8.5 Go / stop for the full compiler (P1-P3)

**Conditional GO for P1**, gated in this order:

1. Quiet-machine rerun of the four arms: **done** (§8.3): stock -> fast + compiler -15.9%, with inlining -18.0%: at the bar, not above it. The offline case alone does not carry P1.
2. `mintcall-verify` passes (0 mismatches in game, BP / IP / SV), then `mintcall-ab` shows the fast path saving **at least ~5 ms/s** Mono time on BP and IP. **Under ~3 ms/s: STOP** the compiler too, since its projection uses the same scaling.
3. P1 scope then: lazily built frames for compiled-to-compiled calls (records and `MonoInvocation` written only when an exception, a yield or a native transition needs them: the real "direct call"), Sims3Gameplay accessor coverage measured on a world image (docs/123 M3, so the game's accessors can run offline), inlining tier 2 (null-checked `ldfld` on any base with an exit before any write, value-type `this` through `call`). P2 / P3 (register allocation, wider templates) only after P1's in-game number.

Commands (monohost, `native/monohost/out`): stock `MONOHOST_JIT=on`; fast path `MONOHOST_MINTCALL=fast MONOHOST_JIT=on`; inlining adds `MONOHOST_JITINLINE=1`; verify replaces `on` with `verify`; `MONOHOST_JITREPS=9`; each run: `monohost.exe <repo>\game-copy\monohost\TS3W.exe <repo>\game-copy\assemblies selftest <repo>\game-copy\monohost\hostdata.txt`.

## 9. Gameplay accessors offline: what each needs from the world (2026-10-31)

Round 3, task B5. Static, from the call-graph index (docs/78) and `docs/data/ic-safety.csv`: no build, no host run. Tool: `node tools/offline-accessors.ts [--depth 3] [--top 100] [--write]`, table in `docs/data/offline-accessors.csv` (one row per method: closure size, EA ICs with their ic-safety verdict, Mono ICs, instance fields, static fields, virtual / interface calls, static constructors, effort, fixture recipe).

Scope: the never-yielding (class N / ?) Sims3Gameplay* methods among the top 100 of §3's ranking (mean self ms/s over the three worlds): **19 methods, 26.1 ms/s** [measured: compile-candidates.csv]. P0 ran none of them; §7.5 gave the top nine the `List.get_Item` ratio instead.

| # | Method | ms/s | Needs from the world [verified: call-graph index, closure depth 3] | Fixture |
|---|---|---|---|---|
| 1 | `GameObject.GetComponent` | 4.53 | fields mObjComponents (List: _items, _size), RuntimeTypeHandle.value; virtual ObjectComponent.get_BaseType | M |
| 2 | `InteractionObjectPair.Equals` | 3.70 | fields mInteraction, mTarget; virtual Object.Equals | M |
| 3 | `GameObject.GetObject` | 3.00 | fields ScriptProxy.mTarget, ObjectGuid.mValue; statics InvalidObjectGuid, gSimulator; IC Simulator_GetTaskImpl (PROXY) | M |
| 4 | `SimClock.ElapsedTimeInMinutes` | 2.73 | field DateAndTime.Ticks; statics sSimTickCnt, sStartTime, sTicksAdvanced; no IC, no .cctor | S |
| 5 | `GameObject.get_Parent` | 2.20 | fields mTarget, mParent, mValue; statics InvalidObjectGuid, gSimulator, gSlots; IC Slots_GetParentIdImpl (PROXY); virtual ITask.get_ObjectId | M |
| 6 | `ScriptObject.get_ObjectId` | 1.73 | field mParent; static InvalidObjectGuid; virtual ITask.get_ObjectId | M |
| 7 | `GameObject.get_LotCurrent` | 1.47 | fields mTarget, mLotCurrent, mParent, mValue; statics InvalidObjectGuid, gSimulator, gSlots; virtual ITask.get_ObjectId | M |
| 8 | `Household+Members.GetActorsOfType` | 1.27 | 10 fields (mSimDescriptions, mPetSimDescriptions, SimDescription mSim / mSimFlags, List internals); 6 interface calls over the lists | M |
| 9 | `RoutingComponent.get_IsRouting` | 0.70 | fields mRouteActions (List._size) | S |
| 10 | `Lot.get_CommercialLotSubType` | 0.57 | field mCommercialSubType | S |

Effort classes: **S** = hand-built objects only (no EA IC, no virtual call, at most 8 fields, no .cctor that reaches an EA IC); **M** = up to 3 EA ICs with recorded answers, or virtual calls whose receiver class the fixture picks; **L** = more ICs, an UNSAFE / MAIN IC, or a closure over 60 methods (none in the top 19). Totals: S 8 methods (6.3 ms/s), M 11 (19.8 ms/s), L 0.

Findings:

- **No method needs a world image.** The top 19 read a handful of fields on objects the fixture can build, plus at most two EA ICs (`Simulator_GetTaskImpl`, `Slots_GetParentIdImpl`, `World_IsObjectOnScreenImpl`, `Simulator_GetTickCountImpl`; all PROXY in ic-safety.csv, so a recorded answer per call is enough) [verified: index]. The world image of docs/123 M3 is not needed for accessor coverage [inferred].
- **`SimClock.ElapsedTimeInMinutes` (2.7 ms/s) needs nothing from the world but three static fields**, and `SimClock` has no static constructor [verified: index]. It could have run in P0 with the statics set by reflection.
- **The ObjectGuid -> object path** (`GetObject`, `get_Parent`, `get_LotCurrent`: 6.7 ms/s together) goes through `Simulator.gSimulator` and `Slots.gSlots`, which the M1.2 service objects already build [verified: monohost.cpp M1.2 b], and one IC each. Answering those two ICs from a table the fixture fills (guid -> object, guid -> parent guid) covers all three [inferred].
- **Fixture constraints in this game's mscorlib** [verified: index]: `FormatterServices.GetUninitializedObject` does not exist, so objects come from their real constructors (`GameObject` is abstract: a concrete subclass, or a test-assembly class deriving from it); `FieldInfo.SetValue` exists for the private fields and statics.
- Blind spots: the closure stops at depth 3 (`--depth` raises it); a virtual call is listed by its declared target, so the fixture's receiver class decides which override runs; field reads through `ldflda` on value types count once.

Fixtures: **not built.** This machine has no C++ compiler and the CI does not build `native/monohost`, so nothing could be run, and an unrun fixture would only move the debugging to the next person. The recipe per method is the `fixture` column. For the top 5: rows 2 and 4 (no IC) are JitBench cases on objects from constructors plus `FieldInfo.SetValue`; rows 1, 3 and 5 also need a concrete `GameObject` subclass, and rows 3 and 5 need an M2 answer code that reads the guid table from a static field of the test assembly.

## 10. Differential corpus: compiled vs interpreted, bit for bit (2026-10-31)

Round 6, task B4. Goal: every case that P0 compiles must give the same results and the same exceptions as EA's interpreter.

**Tools.**
- `node tools/jit-corpus.ts --write` generates `native/monohost/JitCorpus.cs`, which `build.cmd` compiles into `MonoHostTest.dll`. It has 148 cases:
  - 90 arithmetic kernels over typed edge inputs: div / rem with `INT_MIN / -1` and zero, `*.ovf`, `conv.ovf` (also from `r8`), shifts past the width.
  - x87-sensitive float kernels: NaN payloads, signaling NaN, denormals, -0, 53- vs 64-bit rounding, float stores.
  - Statement cases: switch, value types, casts, boxing, arrays.
  - One kernel per EA superinstruction `0x1AB`-`0x1C0`.
  - The BCL / SimIFace methods P0 compiles (Dictionary with six key types incl. colliding keys, List, `Array.Resize` / `IndexOf`, Enum, Math, ObjectGuid, CASUtils).
  - 8 try / catch / finally cases, run last because a CPU fault stops the corpus.
- `MONOHOST_JITCORPUS=1` runs each case three times. It runs twice on EA's interpreter with the code unpatched; the two must agree, or the case is "unstable". It then runs once compiled, with the per-call verifier on (`MONOHOST_JIT=verify`).
- A case hashes its results and every caught exception. An exception counts by type plus the throwing frame's offset and method. The frame's opcode name and argument values are left out: a compiled frame has no opcode name, and arguments hold object addresses.
- On a difference, monohost logs both exception trails.
- `--report <log> --dump <jit-dump.txt>` lists the differences by case name. It labels a difference "stack-trace text only" when the trails differ only by the superop rule below and the per-call verifier saw no value mismatch. It also checks which superops the compiled methods contain.
- Run: `MONOHOST_JIT=verify MONOHOST_MINTCALL=fast MONOHOST_JITCORPUS=1 MONOHOST_DIVGUARD=1 MONOHOST_JITLIST=<--env> monohost.exe ... selftest ...`. `MONOHOST_JITCORPUS_FROM` / `_TO` pick a range of cases. `MONOHOST_DIVGUARD=1` installs eafix's `MintDivGuard` stubs, so `INT_MIN / -1` runs as with that fix in the game.

**Results [measured offline: 2 runs with the same verdicts, fork CI builds of this branch]:**

| | Result |
|---|---|
| Cases | 148, 0 unstable |
| Behaviour differences | **0** |
| Stack-trace text differences | 1 (`JitKO.S1AF`, see finding 2) |
| Superops in compiled, verified methods | 22 of 22 |
| Per-call verifier during the corpus | 71,469 / 71,573 calls equal, 0 mismatches (2.1 M over the whole selftest) |
| JitBench checksums | equal to the interpreter's |

**Findings.**

1. **P0 bug, fixed: `leave.s` jumped to a wild ip** [verified: bytes and runs]. Before the fix, all 8 clause cases faulted, even a plain `leave` out of a try with no finally and no exception.
   - Cause: EA's `leave` / `leave.s` handler `0xE59315` re-reads the opcode word at `ip` to choose the operand size (`0xE5933A`, compare with `0x9C`). In a compiled method that word is `JIT_OP` (`0x1A9`). The handler then reads `leave.s`'s 16-bit offset plus the next word (`JIT_OP` again) as one 32-bit offset, here `0x01A90009`. The dispatcher `0xE544D5` faults on the resulting ip.
   - Scope: a scan of all mint handlers found no other handler that reads its own opcode word. The scan covers each handler's straight-line code up to the first jump or the first change to `edi`.
   - Fix: `JitWord` keeps the original word at `leave.s` positions. `leave.s` always exits to EA's handler, so it needs no `JIT_OP` entry. Long `leave` already worked, because `0x1A9 != 0x9C` picks the 32-bit form.
2. **EA's stack-trace printer reports offset 0 at a superop** [verified: `0xE52A0F`].
   - When the word at a frame's ip is `>= 0x1AA`, EA's printer writes offset 0 and the name "superop". A compiled frame has `JIT_OP` there, which is below `0x1AA`, so it prints the true offset with a blank name.
   - Catching is not affected. Both throw stubs of a superop store the current ip to `frame+0x1C` before throwing (`0xE5947E`, `0xE595CA`), the same positions the base `ldfld.o` stores. So the catch-clause lookup sees the same ip in both arms.
   - The difference shows only in `Exception.StackTrace` text. Not fixed: matching it would mean faking the trace.
3. **Verifier false positives, fixed.** `GC_malloc_atomic` leaves a block's slack uninitialised. The per-call verifier compared whole GC blocks, so a boxed `long` (16 bytes in a 24-byte block) mismatched on garbage: 30 calls in the `Box` case. The earlier `Array.Resize` mismatch is gone too [measured].
   - New-object compares now use the class's bytes. Strings use 8 + 2 x length. Other non-arrays use `MonoClass.instance_size` `+0x54` [verified: `0xE6F5A0` reads it]. Arrays keep the length heuristic. The class comes from `MonoVTable+0`; name `+0x34` and namespace `+0x38` are verified live.
4. **Superop coverage needs whole runs.** EA fuses a sequence only when a whole run between two boundaries equals it (docs/139 §4.1). So the kernels start a run at a ternary join or after a call. 14 of the 22 superops already appeared in compiled BCL methods (`Dictionary.GetSlot` / `GetPrev`, `Array.IndexOf`, the enumerators); the reshaped kernels add the other 8.
5. **Corpus artefacts removed.**
   - Identity hashes differ between runs, so a `JitKO` node hashes by its field and other reference types by type name.
   - Trace frames carry `this` and argument addresses, so the hash keeps the offset and method only.
   - An `OverflowException` from `newarr` with a negative size escapes the C# `catch` even in the interpreter. The driver hashes an escaped exception by its type name, the same way in both arms.

**Limits.**
- Yields are out of scope: P0 compiles only never-yielding methods, and monohost runs no task scheduler (P1 adds them).
- The float kernels ran at monohost's default 53-bit x87 control word. docs/213 shows that 24-bit precision changes `add.r8`, so a control-word sweep belongs to B2's templates.
- Clause methods are compiled but not verified per call (`unverified (clauses / branch to 0)`). The corpus hash is their only check.

## 11. Exit census: what P0 still leaves to EA's handlers (2026-10-31)

Round 6, task B1. Goal: find which opcodes still exit from compiled code to EA's handlers, how often, and how much self time sits behind them.

**Method.**
- Count mode (`MONOHOST_JITCOUNT=1`, `MONOHOST_JIT=on`) now logs every compiled method as `jitcalls m calls positions-run positions name`. `positions-run` is a per-method counter incremented at the start of every compiled position. It also logs every exit site, including the ones never taken, as `jitexit m op name op|slow n`.
- Prep: `node tools/jit-exit-census.ts --prep prep.txt` takes the top 60 never-yielding self-time frames of `compile-candidates.csv` (mean over the three worlds; a world that does not list a frame counts 0). It writes the non-generic ones that resolve through the call-graph `methods.csv` as a `MONOHOST_JITPREP` list.
  - 27 frames resolved to 31 methods (overloads). 17 compiled newly in the run; the other 14 were already compiled from kJitList or refused.
  - 16 frames are methods of generic classes: the bench runs their instances.
  - 17 frames are not in `methods.csv`: generic methods (`Array.Resize<T>`, `GetComponent<T>`), nested short names, mod or renamed methods, and one unnamed frame.
- `node tools/jit-exit-census.ts --census <log>` writes `docs/data/jit-exit-census.csv` (method, assembly, ms_s, opcode, exits_per_call, est_ms_s_behind, template_effort, basis). The census excludes MonoHostTest's own kernels and bench drivers.
  - **Run basis:** a method the bench ran at least 100 times uses measured exits per call. Its share is exits / positions run.
  - **Static basis:** a prep-only method was called once with null / zero arguments and faulted, so it uses its exit sites / positions. A slow path counts 0 there, because a normal call does not take it.
  - `est_ms_s_behind` = ms_s x share [inferred: every mint instruction costs the same; an exit costs more than an average instruction, so this understates the exits].

**Results [measured offline, 1 count run; shares inferred as above].**
- 52 compiled methods, 343 rows: 240 on the run basis, 103 static.
- The 47 methods with a candidate time hold 109.1 ms/s of self time across the three worlds' means. **About 33.8 ms/s of it sits behind exits.**

| # | Template (opcode) | est. ms/s behind | methods | Effort |
|---|---|---|---|---|
| 1 | `ret` | 10.41 | 36 | L: frame teardown (B3) |
| 2 | `call` | 5.70 | 22 | L: compiled-to-compiled calls (B3) |
| 3 | `callvirt` | 2.63 | 16 | L: compiled-to-compiled calls (B3) |
| 4 | `vtresult` | 2.44 | 16 | M: value-type copy |
| 5 | `stinarg.vt` | 1.65 | 14 | M: value-type copy |
| 6 | `ret.vt` | 1.34 | 5 | L: frame teardown (B3) |
| 7 | `ldarg.vt` | 0.81 | 10 | M: value-type copy |
| 8 | `newobj` | 0.80 | 12 | M: allocation helper |
| 9 | `ret.void` | 0.78 | 8 | L: frame teardown (B3) |
| 10 | `ldloc.r4` | 0.59 | 2 | L: x87-exact float (B2) |

By effort class:

| Effort class | est. ms/s behind |
|---|---|
| Call boundary (B3): returns | 12.5 |
| Call boundary (B3): calls | 9.4 |
| Value-type copies, M (incl. `vtresult`) | 5.6 |
| x87-exact float, L (B2) | 3.0 |
| Allocation, M | 1.2 |
| Constants and tables, S | 0.9 |
| Class checks, M | 0.6 |
| Bounds checks, M | 0.2 |

Read-out:
- **The call boundary is two thirds of what is left**, 21.9 of 33.8 ms/s. That fits §7.6, which found that the call path, not the body, is the lever. B3 is the next step.
- Value-type moves around calls (`vtresult`, `stinarg.vt`, `ldarg.vt`) are the largest M class and mostly belong to the same call sequences.
- No slow path was taken on the bench: the null, bounds and overflow checks stay native.

Effort classes [inferred from what P0's templates do today, §8]:
- **S:** a constant or table known at compile time.
- **M:** a helper call or an inline check with a cache.
- **L:** needs the B2 (float) or B3 (calls, returns) design.

**Limits.**
- The static basis assumes every position runs once per call, so loops count once.
- The bench inputs are synthetic. `Dictionary.GetPrev` shows 39 `callvirt` exits per call on the bench's colliding keys.
- The gameplay methods have no measured exits until the §9 fixtures exist.

## 12. Compiled-to-compiled calls with lazy frames (2026-10-31)

Round 6, task B3. Goal: a call from compiled code to a compiled method should not build EA's frame (record, `MonoInvocation`, depth, `ThreadContext`), unless an exception, a yield or a native transition needs it (§8.5 step 3). Gate: the B4 corpus (§10), run twice. With `MONOHOST_JIT=verify`, the per-call verifier compares each verified call, but it then keeps the interpreter's pass (§7.2 steps 3-4), so a wrong compiled value does not reach the case hash. With `MONOHOST_JIT=on`, the case hashes compare compiled results end to end.

### 12.1 Which call edges carry the calls

**Tools.**
- Count mode now also logs each compiled `call` / `callvirt` site: `jitedge caller site op n misses callee flags native-body words clauses name`.
- For `callvirt`, the callee is the site's inline-cache method at the end of the run. "Misses" counts calls whose receiver class differed from the cached class before the call; each one makes EA look up the method and rewrite the cache.
- `node tools/jit-exit-census.ts --edges <log>` writes `docs/data/jit-call-edges.csv`, one row per caller, callee and kind:
  - calls, cache misses;
  - the callee's traits from its own exit sites: compiled, positions, inner calls per call, throws, allocates, EA native body;
  - the dispatch rule, the frame rule and the inline candidates (below).

**Result [measured offline: count run, bench and selftest workloads]: 96 edges, 22.7 M calls.**

| Dispatch at the site | When a lazy frame would be written | Edges | Calls | Share |
|---|---|---:|---:|---:|
| direct | never (the callee makes no inner call and cannot throw) | 4 | 15,820,985 | 69.7% |
| class guard (cache misses under 1%) | at the callee's first inner call (1 per call) | 4 | 5,646,238 | 24.9% |
| none (callee not compiled, or not run) | | 64 | 854,407 | 3.8% |
| direct | at the callee's first inner call (0.1-34 per call) | 10 | 343,193 | 1.5% |
| class guard | never | 1 | 24,508 | 0.1% |
| polymorphic (1% or more misses) | | 6 | 329 | 0.0% |
| direct or class guard, callee not run | unknown | 7 | 15 | 0.0% |
| **all** | | **96** | **22,689,675** | |

Read-out:
- **Direct calls to leaves are 70% of the calls**, but one edge carries most of them: `Math.Min(float)` -> `Single.IsNaN`, 15.8 M. Without it, the class-guarded `callvirt`s lead.
- "Leaf" in the census means the frame is never needed. v1 needs more: every position of the callee compiles natively. `Single.IsNaN` is `stinarg.r4`, `ldarg.r4` x2, `ceq.r8`, `ceq0.i4`, and the float opcodes still exit (1 native position of 6). It becomes a v1 leaf only with B2's x87-exact float templates.
- **The class-guarded `callvirt`s are monomorphic.** `Dictionary.GetPrev` -> `IEquatableOfTEqualityComparer.Equals` misses 10 times in 4.7 M calls (the first call of each instantiation). A guard on the cached class is enough.
- **Their callees are not leaves.** Each one makes one `con.call` per call, a constrained call on a value-type `T` (`Int32.Equals`). So the frame rule for this 25% needs lazy frames that chain (12.6).

### 12.2 Frame rules

A lazy call writes no frame. The frame must exist before anything can read it:

| Event in the callee | Who reads the frame | Rule |
|---|---|---|
| Exception (any throw, incl. EA's null, bounds and overflow checks) | EA's exception search walks `MonoInvocation` parents and the records | Write the frame at the exit, before EA's handler runs |
| Call to another method, allocation, `ldsfld` (class init can run a cctor), any other exit | EA's handler (it may call, allocate, throw or reach native code) | Write the frame at the exit |
| Yield | The yield save `0xE53620` reads every record | Only through a call (an exit): the frame exists first |
| Native transition (icall, P/Invoke, runtime invoke) | `ThreadContext +8`, stack walks, the GC | Only through a call: the frame exists first |
| GC from another thread | Conservative scan of the stack | No rule needed: the block and the arena are on the stack or in the arena |
| Sampling profiler | Our sampler reads the `MonoInvocation` chain | The callee's time counts for the caller, as with inlining |

So the rule is simple: **the frame is written at the callee's first exit to an EA handler.** A callee whose normal path has no exit (a leaf) never writes it.

### 12.3 v1 as built (`MONOHOST_JITLAZY=1`, off by default)

**Scope.** v1 covers direct call sites only: `call`, and `callvirt` of a non-virtual method of a reference type. The callee, static or instance, must be compiled and a leaf by construction: every position compiles natively, or is `ret` / `ret.void`.

**Call site** (normal body).
- It tests everything EA's call path and McCall (docs/139 §8.1) test, before any write:
  - the callee's lazy body exists;
  - MonoPatcher ForceJIT is off;
  - for an instance method of a reference type: `this` is not null, its class is not the remoting class, and for `callvirt` it is not a value type;
  - the next record slot already holds a `MonoInvocation`, which the frame would need;
  - the arena chunk has room for the callee's frame.
  Any failed test takes the normal exit (the fast path or EA's handler).
- The site bakes in the callee's frame sizes (eval stack, vt area, locals, total), so it is built only for a callee that is already transformed. With the switch on, the compiler transforms static callees before their caller, as it already did for instance callees (the inliner). First corpus run without that rule: an untransformed static callee had sizes 0, its eval stack overlapped its arguments, and the per-call verifier caught wrong results in every lazy case plus a CPU fault in `JitKL.CallVoid`.
- Then it:
  1. bumps the arena;
  2. writes EA's return-slot word (the caller's vt cursor);
  3. pushes a 36-byte block: arguments, `this`, return ip, caller sp, return slot, method, caller `ebx` / `edi`, call ip;
  4. clears the locals if the callee has init-locals;
  5. calls the callee's lazy body.
- On return, it copies the return slot's 8 bytes to the caller's new top, as EA's frame return does, and pops the block.

**Lazy body.** Each such leaf gets a second native body.
- `ldthis` and `stinarg` read the block instead of `MonoInvocation`.
- `ret` / `ret.void` copy the value to the return slot, pop the arena and return natively. If values are left on the eval stack, they take EA's `ret` instead, which logs them.
- Every other exit first runs `JitLazyMat`. It writes the frame EA's call would have written, from the block, then drops the native call and takes the same exit. The frame covers: the caller's record, depth and max depth, the callee's record, `MonoInvocation`, `ThreadContext +8` and the frame words.
- From then on, the callee is an ordinary interpreted frame. It continues in its normal body, and the caller resumes through EA's return (JitEnter at its next word), as after any interpreted call.

**Not written by a lazy call** (dead after the return, and not read above the current depth [inferred: every reader walks records 0..depth]):
- the callee's record and `MonoInvocation` fields;
- the caller's throw ip `MonoInvocation +0x1C` (every throw path writes it first, §10 finding 2);
- EA's scratch frame slots (docs/139 §8.2).
EA's `callvirt` handler also rewrites the inline cache on a miss. A non-virtual method gives the same callee either way.

### 12.4 Results [measured: monohost, 2026-10-31]

**Gate.** The B4 corpus has 10 more cases (`JitKL`, 158 in all): static leaves (with init locals), an 8-byte return, non-virtual instance leaves through `callvirt` (null receiver too), a void leaf with `stfld` (null: the exception is thrown in the callee), bounds and null exceptions in the callee, a compiled caller that catches the callee's exception, recursion to depth 30 / 70 / 130, and a value-type `this`.

| Run | Cases that differ | Per-call verifier |
|---|---|---|
| verify, lazy on | 1 (§10 "stack-trace text only", as with lazy off) | 71,583 ok, 0 mismatches, 1,851 abandoned (exception); 5 frames written at an exit |
| verify, lazy off | 1 (the same) | 71,584 ok, 0 mismatches |
| `on`, lazy on | 1 (the same) | (off) |
| `on`, lazy off | 1 (the same) | (off) |

The first verify run, before the transformed-callee rule (12.3), had 12 mismatches in `JitKL.CallStatic`, `CallLong`, `CallInst`, `CallInstLong` and `JitKO.S1B4`, and a CPU fault in `JitKL.CallVoid`. With the verifier keeping the interpreter's result, the case hashes of the first four still matched (the gate note at the top).

**Coverage.** On the 12.1 workload with lazy frames on, v1 builds 21 lazy bodies and 339 lazy call sites, from 251 callees seen at call sites. 13,038 calls ran lazily (0.06% of the 22.7 M calls on compiled edges) and no frame was written. Coverage is this low for the two reasons in 12.1: the top leaf needs B2, and the class-guarded edges need v2.

**Speed** [preliminary: one run per arm, the game ran on the same machine, and the corpus ran first in the same process]. `HostTest.JitLz` is a compiled loop with two calls to compiled int leaves per iteration (a static `LzClamp`, a non-virtual `LzBox.Get`). `JitLzPlain` is its interpreted twin. Times are per iteration, best of 7, with the loop overhead added back (the bench subtracts an interpreted loop's overhead, which a compiled loop does not have):

| Arm | `JitLz`, compiled | `JitLzPlain`, interpreted | Speedup |
|---|---:|---:|---:|
| lazy off (the call exits to the docs/139 fast path or EA's call, the callee enters through `JitEnter`, its `ret` exits) | 39.5 ns | 56.2 ns | 1.4x |
| lazy on | 17.5 ns | 57.4 ns | 3.3x |

So a lazy call saves about 11 ns over a compiled call that exits to EA. `MintBench(3000)` has no lazy edges: compiled -17.2% (lazy on) and -16.4% (lazy off) against the interpreter in the same process. A quiet rerun (`MONOHOST_JITREPS=9`, no corpus, both arms alternating) is still to do.

Not explained yet: `JitInl` (three interface calls per element, inlining off) took 47.1 ns compiled with lazy on and 94.8 ns with lazy off, against 53.1 ns in the §8.3 table. Its calls are virtual, so lazy frames do not apply. The difference is that lazy on also pre-transforms the callees. To recheck in the quiet run.

### 12.5 Limits of v1

- Only callees whose every position compiles natively. Float opcodes (B2), `ldsfld`, allocation and calls in the callee all disqualify it.
- Only direct sites: `call`, and `callvirt` of a non-virtual method. No `con.call`, no class guard.
- A lazy body cannot make lazy calls (it has no calls).
- The materialization path (`JitLazyMat`) ran 5 times in the corpus, always for an exception in the callee. No corpus case reaches it through a yield, because a v1 leaf cannot yield.
- Offline only. Nothing here is in ts3native or the game.

### 12.6 Next (v2)

- Lazy chains: a lazy body may make lazy calls. The block gets a "caller is lazy" flag, and `JitLazyMat` writes the outer frames first. This covers the 25% class-guarded edges, whose callees make one inner call.
- `callvirt` with a class guard (12.1: those sites miss under 1%). Each site gets a cell with (class, lazy body), filled from the inline cache on the normal path.
- `con.call` (`0xEB` [op, class, method]) skips the class word and then is a plain `call` (docs/213 §1 row 3, verified), so a lazy site can treat it as `call` with the method in word 2. The callee is a value-type method: `this` is a pointer, as in `JitKL.CallStruct`.
- The gameplay accessors need the world image (docs/123 M3) before their edges can be measured.
