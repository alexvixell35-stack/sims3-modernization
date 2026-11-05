# 527. The method compiler in the game: integration plan with the IL patcher hook (round 10 C6)

Status: plan, 2026-11-02. Nothing here is built; every claim is tagged. Round 10, task C6: "in-game integration plan with
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
- **A/B** = `jit-ab.json` shaped as `mintcall-ab.json`, judged by `tools/harness/arm-ms.ts` summed sim-thread ms/s per arm
  (script is the column the compiler moves), hitches as a no-regression guard only.
- **Expectation:** with today's templates the list that passes docs/207 §16's rule is 10 methods, 26 ms/s of self time;
  the in-game number will be small until §16.3's template order (`call`, `ldfld.vt`, `callvirt`) is worked through. The
  A/B is still the first real number and the gate for the rest of the lane (docs/207 §8.5).

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

Queue `tools/harness/queues/jit-ab.json`, after `jit-verify` passed: BP, IP, SV off-first and on-first, 10 min, `--ab
jit` with 2-min slices, a declared 4-min smoke job first, quiet machine, `[AutShadow]` arm stated per job (docs/223 §4
point 4). Read with `node tools/harness/arm-ms.ts jit-ab`: mean sim-thread ms/s per arm of script, route, animPose,
locomotion and their sum, then on minus off; script is the number. `hitch-ab.ts` as the no-regression guard. Gate
(docs/207 §8.5 scaled to the list above): the first A/B is a measurement, not a go / stop; the lane's go / stop stays
docs/207 §8.5 point 2's bar (≥ 5 ms/s script on BP and IP) applied once the `call` and `ldfld.vt` rows of §16.3 compile.

## 6. Steps and gates

| Step | What | Offline gate | In game | Hours |
|---|---|---|---|---|
| 1 | Port the host compiler core to ts3native behind `[MonoJit]`: templates and exits, the method table, `JitEnter` with the thread check, the per-call verify, the post-pass on the shared `0xE643B0` detour of `ilpatch.cpp`, counters v901, the exe and hash gates, the code region | ts3native selftest cases (the `ilpatch-test.h` pattern) for the hook order, the word patch / unpatch round trip and the runtime byte; monohost unchanged (corpus 197, 0 differ) | none | 10-16 |
| 2 | `jit-verify` (§3), `Direct=0` then `Direct=1` | - | 3 + 3 jobs, 10 min each, pass rule §3 | 1.5 h game time per round |
| 3 | `jit-ab` (§5) | - | 7 jobs, ~85 min; `arm-ms.ts` | 1.5 h game time |
| 4 | One clean night, `Enabled=1 Mode=1 Direct=1` on the user's play save, crash and ScriptError check; then the PR leaves draft with the default off | - | night run | - |

Step 1 needs #200 / #204 merged (the detour it extends). Steps 2 to 4 are the user's machine (the CI has no game).

## 7. Open, and what this plan does not do

- The in-game gain with today's templates is expected small (§5); the plan measures it rather than projecting it.
- Compiled code on the [AutShadow] worker: never in this plan (docs/223 §4).
- Methods with clauses and the `throw`-out-of-callee path stay the verify blind spots named in §3.
- A ts3fixes-side list or hash source (docs/223 §9 point 4): not needed; ts3native hashes the IL it reads at the hook.
- UPDATE242 / simler90 cores: the hash guard covers them per method (their IL differs per method, the exe is the same).
