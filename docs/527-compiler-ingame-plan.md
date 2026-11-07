# 527. The method compiler in the game: integration plan with the IL patcher hook (round 10 C6)

Status: plan, 2026-11-02; step 1 built 2026-11-06 (§8, ts3native 0.36.169, C5 templates and list 0.36.170, off by default, no game run yet). Every claim is tagged. Round 10, task C6: "in-game integration plan with
#200: ts3native switch (off by default), in-game verify mode with a mismatch counter, A/B judged by script ms/s per arm
(`arm-ms.ts`)". It supersedes rows S1 and S3 of docs/223 §7 with what rounds 8 to 10 built in monohost (docs/207 §10 to
§16) and with the transform hook #200 / #204 put into ts3native (docs/506 §2, docs/526). Ledger: docs/51 §931.

## Summary

- **One owner of the transform entry `0xE643B0`, two clients.** The IL patcher (`ilpatch.cpp`) owns it today and refuses
  another owner. The compiler does not need a second detour: it runs *after* EA's transform on the mint code the transform
  produced, so the shared hook is pre-pass (IL patch: body rewrite) → EA's transform → post-pass (compiler: opcode words
  into compiled blocks). One detour, a client list, each client behind its own switch.
- **Switch:** `[MonoJit] Enabled=0` (default off), `Mode=1` on / `2` verify, `VerifyEvery`, `Direct` (lazy frames),
  `Groups` (template mask), `List` (test override of the built-in list). A runtime byte in the counter block for `--ab`.
- **Verify in game** = the host's per-call verify (docs/207 §7.2, §11) sampled by `VerifyEvery`, sim thread only, with a
  mismatch counter; the first mismatch of a method unpatches that method and logs it. Pass rule as `mintcall-verify`'s.
- **A/B** = `jit-ab.json` shaped as `mintcall-ab.json`, read with `tools/harness/arm-ms.ts` summed sim-thread ms/s per arm
  (script is the column the compiler moves), hitches as a no-regression guard only. No ms/s bar (below).
- **Expectation:** with today's templates the list that passes docs/207 §16's rule is 10 methods, 26 ms/s of self time;
  the in-game number will be small until §16.3's template order (`call`, `ldfld.vt`, `callvirt`) is worked through. The
  A/B is the first real number, not a gate.
- **Measure (Rodney R2, 2026-11-06):** the compiler is measured statically: coverage (methods and IL compiled, exits,
  instructions per opcode, coverage weighted by call counts) plus correctness gates. Gains add up; no ms/s stop rules.

## 1. One hook, two clients [verified: ilpatch.cpp, monohost-jit.cpp:132-154]

| | IL patcher (#200, built) | Compiler (monohost today) |
|---|---|---|
| Hook | `InstallDetour("il patch transform 0xE643B0", ...)`, prologue check, refuses another owner | `Jmp(JIT_XFORM, JitTransformHook)` after its own 6-byte prologue check |
| When it works | before EA's transform: rewrites the IL body the transform reads, restores the header after | after EA's transform returns: reads the mint code it produced and replaces opcode words with `JIT_OP` (`0x1A9`) entries |
| Per-method cache | verdict per runtime method (patched body or none): a re-transform costs one lookup | the method table (`g_jitMeth`) by runtime method; a re-transform today recompiles |

Plan: `ilpatch.cpp`'s detour becomes the one owner with two calls around `g_ilpNext` (EA's transform): the pre-pass is
the patcher as built; the post-pass calls the compiler when `[MonoJit] Enabled=1`, with the runtime method and the
transformed code. Each client has its own switch and counters; either alone installs the detour. The compiler sees the
*patched* body's mint code, which is what it must compile (the player runs the patched method), and the IL-hash guard
(§4) therefore lists both the EA form and each spec's `patched` SHA-256 (the specs carry it, docs/526 §1).

**MonoPatcher's ForceJIT** (`0xE5637D` `jne` → `90 90`: transform on every call) makes the post-pass run per call. The
IL patcher pays one lookup; the compiler would recompile or re-patch words per call. Rule as `mintcall.cpp` line 35: test
the byte per call (`0x75` expected) and leave the method to EA while it is patched. Same for `[GcRootCheck] TailWatch`
at `0xE563B2` (another owner of the arena bump the exits model).

## 2. The switch and its runtime byte [planned]

`ts3native.ini` `[MonoJit]`: `Enabled=0|1` (default 0); `Mode=1` compiled, `Mode=2` verify (compiled runs, then the
per-call check); `VerifyEvery=16` (one call in N verified, as `[MonoFast] VerifyEvery`); `Direct=0|1` lazy frames
(docs/207 §12, off at first); `Groups=255` (the `MONOHOST_JITC2` template mask, for bisecting a mismatch in game);
`List=` (comma list of `Class.Method`, test override; empty = the built-in list, §5). Counters block v901 (reserved
when built, `tools/registry.ts reserve native`): `jitOn`, `jitSwitch` (the runtime byte), `jitMethods` (compiled),
`jitRefused` (hash miss, clause, size, ForceJIT, other thread), `jitCalls`, `jitExits`, `jitVerified`, `jitMismatch`,
`jitFirstBad` (method index + 1), `jitAbandoned` (exception during a verified call), `jitLarge` (graph too large, compiled
only), `jitOtherTid`, `jitFaults`, `jitCodeKB`.

`jitSwitch` is the `--ab` arm: the harness flips it through `counterSwitches` (`tools/harness/native-switches.ts`) as it
flips `mintcall`. Off restores every compiled method's original opcode words at the top of the next
`MonoScriptHost_Simulate` tick (docs/223 §6: a yielded task whose saved ip is a compiled word resumes at the original
opcode; a lazy frame cannot span a yield, since a yield materializes or exits, docs/207 §12.3); on re-patches the words.
Compiled code stays allocated either way, so the two arms differ only in what runs.

## 3. Verify mode in game [planned; the mechanism is the host's, verified offline: docs/207 §11, §14]

The host's per-call verify runs the compiled body, restores the reachable-graph snapshot, runs EA's interpreter on the
same state and compares every live word; it abandons a call that ends by exception and skips a graph too large
(docs/207 §14: 2.4 M calls verified, 0 mismatches, 1,502 abandoned in one corpus run). In game it is the same code,
sampled: one call in `VerifyEvery` per method, sim thread only (the worker's entries dispatch the original word, docs/223
§4 point 3 and C4's thread check). A mismatch increments `jitMismatch`, sets `jitFirstBad` once, unpatches the method's
words (it runs interpreted for the rest of the session) and logs method, position and the first differing word; the
session goes on, so one run collects every mismatch.

Queue `tools/harness/queues/jit-verify.json` (`--no-smoke`): BP, IP, SV, 10 min each, `Enabled=1 Mode=2 VerifyEvery=16
Direct=0`; pass = `jitMethods` ≥ the list's compilable count, `jitVerified` > 100,000, `jitMismatch` 0, `jitFaults` 0, no
crash, the ScriptError count as the job's baseline. Then the same three with `Direct=1`. `mintcall-verify` is the
template (docs/139 §: `mcReady` 7, `mcVerified` > 100,000, `mcMismatch` 0).

What verify does not cover, carried from the host: a call that ends by exception (abandoned), a method with clauses
(hashed by the corpus, not compared word for word), a `throw` out of a callee (docs/207 §9.2: it leaves the case body
in monohost; in game the clause search is EA's and the compiled frame is not on it when `Direct=0`, since compiled code
adds no native frame; `Direct=1` is C4's territory, docs/207 §14). These are why a clean night (§6 step 4) follows verify.

## 4. Safety gates before the first in-game run [planned; each reuses a built piece]

1. **Exe gate**: every address is Steam 1.67's (`TS3W.exe` PE `52DEC247`, `0xE5A000`); ts3native already logs the build
   and skips another edition. The compiler installs only on that build, in the same place `[MonoFast]` installs (after
   every other code patch, before `[AutShadow]`).
2. **IL-hash guard** (docs/223 §5, G-M6's table): compile only a body whose hash is known, EA's or a spec's patched form.
3. **Sim thread only** (C4: `JitEnter` thread check, 34,894 worker entries in the host, none compiled).
4. **Stores under Satb** (C2, docs/507): the compiled store templates carry the barrier; `[AutShadow]` Mode R is one of
   the verify arms as in `mintcall-verify`.
5. **Exceptions and stack walks through compiled frames** (C4, docs/207 §14): verified in the host; in game, `Direct=0`
   first (no compiled-to-compiled frames at all), `Direct=1` after its own verify round.
6. **Code region**: one reserved region at install (the host uses a 4 MB `PAGE_EXECUTE_READWRITE` pool,
   monohost-jit.cpp:1991); in game RW while writing, RX after (`VirtualProtect` per method), and a fault inside it is
   named by the crash report through the method table (eip range → method), as the IL patcher's `__try` names its reads.
7. **Refuse on another owner**: ForceJIT, TailWatch, a differing transform prologue, a mint jump table whose `0x1A9`
   entry is not EA's (the host's `JIT_OP` is an unused opcode slot; Satb's swap, satb.cpp:141, is the precedent for
   writing the shared table and must keep the compiler's entry when a span goes on and off).

## 5. The list and the A/B [planned]

The built-in list is docs/207 §16's rule applied once per release (`tools/jit-hot.ts --write`, the rows with the
verdict `compile`): today `RuntimeTypeHandle.get_Value`, `Dictionary.GetSlot`, `Array.Copy`, `CASUtils.CASAGS...`,
`List.Add`, `List.get_Item`, `List.GrowIfNeeded`, `ObjectGuid.op_Inequality`, `Slots.GetParentId`, `Vector3..ctor`
(26 ms/s of self time). Each template that lands in §16.3's order moves rows from `template first` to `compile`, and the
list is regenerated. The game has no call counter: the threshold is a cut on the sampler ranking, not a counter at the
hook.

**Parked 2026-11-06 (Rodney): not run, the compiler is judged statically; the file stays.** Queue `tools/harness/queues/jit-ab.json`, after `jit-verify` passed: BP, IP, SV off-first and on-first, 10 min, `--ab
jit` with 2-min slices, a declared 4-min smoke job first, quiet machine, `[AutShadow]` arm stated per job (docs/223 §4
point 4). Read with `node tools/harness/arm-ms.ts jit-ab`: mean sim-thread ms/s per arm of script, route, animPose,
locomotion and their sum, then on minus off; script is the number. `hitch-ab.ts` as the no-regression guard. Every A/B
is a measurement, not a go / stop: the ≥ 5 ms/s script bar that stood here was removed 2026-11-06 (Rodney R2, docs/207
§8.5). The lane stops only on a failed correctness gate.

## 6. Steps and gates

| Step | What | Offline gate | In game | Hours |
|---|---|---|---|---|
| 1 | Port the host compiler core to ts3native behind `[MonoJit]`: templates and exits, the method table, `JitEnter` with the thread check, the per-call verify, the post-pass on the shared `0xE643B0` detour of `ilpatch.cpp`, counters v901, the exe and hash gates, the code region | ts3native selftest cases (the `ilpatch-test.h` pattern) for the hook order, the word patch / unpatch round trip and the runtime byte; monohost unchanged (corpus 197, 0 differ) | none | 10-16 |
| 2 | `jit-verify` (§3), `Direct=0` then `Direct=1` | - | 3 + 3 jobs, 10 min each, pass rule §3 | 1.5 h game time per round |
| 3 | `jit-ab` (§5), parked 2026-11-06, skip | - | not run | - |
| 4 | One clean night, `Enabled=1 Mode=1 Direct=1` on the user's play save, crash and ScriptError check; then the PR leaves draft with the default off | - | night run | - |

Step 1 needs #200 / #204 merged (the detour it extends). Steps 2 to 4 are the user's machine (the CI has no game).

## 7. Open, and what this plan does not do

- The in-game gain with today's templates is expected small (§5); the plan measures it rather than projecting it.
- Compiled code on the [AutShadow] worker: never in this plan (docs/223 §4).
- Methods with clauses and the `throw`-out-of-callee path stay the verify blind spots named in §3.
- A ts3fixes-side list or hash source (docs/223 §9 point 4): not needed; ts3native hashes the IL it reads at the hook.
- UPDATE242 / simler90 cores: the hash guard covers them per method (their IL differs per method, the exe is the same).

## 8. Step 1 as built [verified: source, CI selftest; nothing ran in the game]

`native/ts3native/monojit.cpp` is `native/monohost/monohost-jit.cpp` without lazy frames, the inliner, count mode and the dump; monohost itself is unchanged. #200 and #204 landed through sync #272 (their own PRs are closed, unmerged), so the detour this step extends is on main. Differences from the plan above:

- **Counters are v910, 0x5CA8-0x5CDF, not v901** (`[FootstepRO]` took v901; moved from v908 at the merge, docs/registry.md). Same 14 fields as §2. `jitCalls` counts `JitEnter` entries on the sim thread: calls and every return into a compiled method. The harness switch is `--ab jit` (0x5CAC).
- **`Direct=1` was not built in step 1; it is now (docs/537, ts3native 0.36.170, counters v909, not run).** docs/223 §8 risk 1, added after this plan, gates S2 and S3 on a correctness run with all frame readers active. The key exists and only logs a line, so step 2 has one round (`Direct=0`), not two.
- **The list is 11 rows and 17 bodies** (`native/ts3native/monojit-list.h`, `tools/jit-hot.ts --emit-native`): `Dictionary.GetPrev` joined the compile rows after docs/207 §15.6, so self time is 38.9 ms/s of the 177.3 sampled, not 26. Each row hashes every overload. The list is read from `docs/data/jit-hot.csv`; the tool's hashes match a spec's `sha256` on b15 (`ScoreInteractionsForLocalAutonomy`).
- **A body with exception clauses is refused**, as `jitRefused`. In the list that is one `Array.Copy` overload (501 bytes of IL); §3 already had clauses outside the compare.
- **ForceJIT is tested at the post-pass, not per call.** `mintcall.cpp` tests per call because its handlers run for every method. Compiled code is entered only through words in mint code; ForceJIT makes EA build new mint code on each call, which the post-pass refuses, so old patched words are not reached. A method compiled before MonoPatcher flips the byte is the one case left: its code is no longer dispatched once the re-transform has replaced it.
- **Thread rule:** `JitEnter` compares against the thread the first Simulate iteration ran on; before that iteration every entry takes the original opcode's handler, so a method can compile during load and nothing compiled runs until the sim loop runs. `SimLoopAdd` is the tick (end of each iteration, not the top of `MonoScriptHost_Simulate`): there is no `[MonoThread]` dependency.
- **Verify additions to the host's code:** the snapshot's region buffer is a GC root (an object only the snapshot still names must survive a collection inside the compiled pass; the host had no such case in its corpus runs, the game has other threads that can start one). `GC_malloc_atomic` is redirected only in Mode=2, and left alone under `[MonoThread] GcLock=1`, where a new array's slack can then read as a mismatch (the log says so).
- **W^X:** the region is one RW allocation; each method's range goes RW for the emit and RX after. Compiled code never runs during a compile: compiles run on the sim thread between exits, or before the first iteration.

**CallPath and step 1.** The 2026-11-04 A/B (`docs/data/results/2026-11-04-queue-v025-mintcall-ab.md`) gave no number: job void, other apps on 22% of the game CPUs. The only figures are the noisy 2026-10-31 pairs (BP ~7.7, IP ~17.7 ms/s). `CallPath` stays 0 as shipped. Step 1 does not depend on it: every exit reads the live table entry, so a call or ret from compiled code takes whichever handler is installed, and `CallPath=2` is refused at install because it owns entry `0x1A9`. With `CallPath=0` each compiled method pays EA's call and ret (209-233 instructions for a call, docs/139), which is what docs/223 §2.5 calls working but slower. docs/223's `MintBench` gate was stated with the fast path, so `jit-ab` must state `CallPath` per job.

**Static numbers** [verified unless tagged; the data is the offline sampler and host corpus, not this build]. The table is the list at f41e85edc; C5 replaced it with 14 methods / 17 bodies / 1,505 bytes, 55.3 of 177.3 ms/s (docs/207 §16.5, with exits for lazy frames on and off):

| What | Value | Source |
|---|---|---|
| Methods / bodies / IL | 11 / 17 / 2,177 bytes, 16 bodies compile, 1 refused for clauses | `docs/data/jit-hot.csv`, method headers |
| Sampled self time | 38.9 of 177.3 ms/s (22%), ±20-40% per row | sampler (docs/207 §3) |
| Calls, weighted | 7 of the 11 rows have host counts: 409,202 of 23.4 M counted executions (1.7%) | `jit-hot.csv` host run; the game has no call counter |
| Exits other than `ret` | 3,632 in those 409,202 executions (0.009 each); worst `List.GrowIfNeeded` 0.051 (`call`), `List.Add` 0.018 (`stelem.ref`), `Dictionary.GetSlot` 0.016 (`call`) | same |
| Not counted | `RuntimeTypeHandle.get_Value`, `Array.Copy`, `Slots.GetParentId`, `Vector3..ctor`: not in the host's list, so no exit count (they are 12.7 of the 38.9 ms/s) | same |
| Selftest | Templates run in CI on a fake table: constants, arithmetic, locals, branches, switch, fields, r4, arrays, value-type move, exits | `monojit-test.h` |

Per opcode, read from the emitter: `ldloc`, `ldarg`, `stloc`, `add` are 3 x86 instructions and no dispatch; `ldfld` 5 with its null check; a conditional branch 3. An exit is 6 and then EA's handler. `JitEnter` is about 14 on a hit, the hash the host measured at ~10 plus the thread check and the counter. EA's side per opcode cannot be counted on this machine (no exe, no disassembler); docs/207 §7 has the measured ratios: 3.4x on a call-free loop, 1.4-1.7x on small bodies, `MintBench` +18%. `docs/data/jit-lift-census.csv` has EA's handler sizes for 132 opcodes. **A method whose time is mostly call and return gets little from this step**, and with `CallPath=0` it can lose: entry and exit add ~20 instructions to a call of 209-233.

**Not proven:** mismatch-free behaviour in the game; anything about `monojit.cpp` from the monohost corpus, which runs the host copy only; that `g_ilpHeader` returns the generic definition's IL for an inflated method [inferred: mono's header path]; the snapshot root under real collections; Satb under compiled stores with a span on (the barrier code is the host's, the selftest runs it with the span off); the four uncounted rows' templates on real mint code (the host corpus does not list them); verify's cost in play.

**Alienware for `jit-verify`:** `ts3native.asi` from this branch (the `ts3native` artifact of its CI run, or `native/ts3native/build.cmd`) as `game-copy/ts3native.asi.jit5`; `game-copy/ts3fixes.package.mc13` for the last job. Then `node tools/harness/queue.ts tools/harness/queues/jit-verify.json --no-smoke`. Before it, the monohost corpus has not changed, since monohost has no diff. The first thing to read is the `jit:` log lines: how many bodies compiled, and any `refused` with the hash line.
