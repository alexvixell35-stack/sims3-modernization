# 527. The method compiler in the game: integration plan with the IL patcher hook (round 10 C6)

Status: steps 1-4 run in game, 2026-11-06 (step 2 verify passed; step 3 A/B, short and long: no gain measurable; step 4 soak: one crash, cause open, §11) (§8, §9; `[MonoJit]`, off by default). The rest is the
plan of 2026-11-02; every claim is tagged. Round 10, task C6: "in-game integration plan with
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

## 8. Step 1 as built (2026-11-06)

- **Code**: the compiler core moved from `native/monohost/monohost-jit.cpp` to `native/ts3native/monojit.cpp`, shared by
  both builds (monohost includes it with `MJ_MT mt::`). Same code generation [verified: the monohost selftest under
  `MONOHOST_JIT=verify MONOHOST_MINTCALL=fast` before and after the move: 290 methods, 6,253 native ops, 1,142 exits,
  205,680 bytes both; 2.40 M and 2.45 M verified calls, 0 mismatches both; the same 39 / 40, the one FAIL is `GM4-sweep`
  "3 overriding types", in both builds and in the round 10 log, not the compiler].
- **Changes to the core**, each a no-op in monohost: `JitEnter`'s fast path runs compiled code on `g_jitMainTid` only
  (other threads: EA's handler of the original word, `jitOtherTid`); verify sampling (`VerifyEvery`, one fresh call in N
  per method; `jitSampled`); a mismatch unpatches that method for the session (`JitUnpatchMeth`, also unpublishes its lazy
  body); the code pool is `PAGE_EXECUTE_READ` and `PAGE_READWRITE` only inside `JitCompiled`; `JitSetup` shared.
- **In game** (`MJ_GAME` part of monojit.cpp): a post-pass client `g_ilpPost` of ilpatch.cpp's transform detour
  (`IlpInstallDetour`: whichever of `[IlPatch]` / `[MonoJit]` installs first); a listed method compiles on the sim thread
  (the thread of the `SimLoopAdd` callback); one transformed elsewhere or before the first iteration is queued and
  compiled at the end of the next iteration. The switch flips in the same callback.
- **Gates at install**: the jump-table entry of `0x1A9` is EA's default handler `0xE596C2`; `0xE563B2` unpatched (no
  TailWatch); in verify mode `GC_malloc_atomic 0xE4BDC0` unpatched (MonoThread P3 patches it too); the transform prologue;
  the Simulate loop hook. Per method: ForceJIT off (`0xE5637D` = `0x75`), not compiled before, and with `HashGuard=1` an
  IL hash in `kMjHash`.
- **Differences from §2, §4**: counters v910, not v901 (`jitCalls` / `jitExits` dropped: the fast path counts nothing;
  `jitHashMiss`, `jitSampled` added); the `--ab` switch is `monojit`; `kMjHash` is empty until the first run logs the
  hashes (`HashGuard=0`), so `Enabled=1` with the default `HashGuard=1` compiles nothing yet; a fault inside the pool is
  not named by the crash report: each "monojit: compiled" log line gives the method's code range instead.
- **Tests**: `TestMonoJit` in selftest.exe (post-pass order, unpatch after a mismatch, install gates without the game).

## 9. First in-game verify runs (2026-11-06)

Local queue `jit-verify-local` (not committed), `Mode=2 VerifyEvery=16`, 10 min per save, the build of each run named.

- **al253** (BP, `HashGuard=0`) [verified: run log]: 1,793 compiles in 5 min, then "tables full" and 541 refusals.
  The game compiles each instantiation of a listed generic as its own runtime method; all of them share one IL hash.
  Tables x4: ip table 2^18, methods 16,384, positions 2^20, code pool 16 MB. The run also had 2 mismatches, in
  `List`1.Add` and `List`1.GrowIfNeeded`: region 3 (the list) at +4 (`_items`), the compiled pass's new array 0x30 or
  more bytes above the interpreter's.
- **Verifier fixes**, each found by the run before it. The mismatches were the verifier, not the compiled code:
  1. Free Boehm blocks reached through stale words went into the entry snapshot. Their free-list links pulled the
     free list in, so the compiled pass's new array counted as old (fix1, `JitLooksFree`).
  2. Restoring memory that EA's lazy reclaim may free during the pass: the snapshot now skips objects with the free bit,
     and unmarked objects in a block not stamped this cycle. It also abandons the call when `GC_gc_no` (`0x011F2F90`)
     changed during the pass. Before: host verify crashed in 1 of 2 runs, AV writing `0x9C` at `0xE4C223` in
     `GC_malloc_atomic`, after such a restore [inferred]. After: 3 of 3 clean.
  3. al255's mismatch line showed the new array unkept: the new-object set was full (2,048 objects, 160 KB) of old
     objects that dead frame words point at. The set now keeps only objects the pass can have allocated (unmarked, not
     free, block stamped this cycle), read from the compared words only. A difference while the set is full is
     abandoned, not counted [verified: al256 below, host 2.52-2.57 M verified calls, 0 mismatches, set full 0].
- **al256** (`Direct=0`, `HashGuard=1`) [verified: native.json of each job]: PASS on BP, IP and SV.

| Save | Methods compiled | Calls verified | Mismatches | Too large (compiled only) | Code KB | Other thread |
|---|---|---|---|---|---|---|
| BP | 2,625 | 1,267,846 | 0 | 3,796,915 | 1,939 | 0 |
| IP | 2,629 | 1,179,245 | 0 | 3,733,848 | 1,942 | 0 |
| SV | 2,533 | 1,419,724 | 0 | 3,932,645 | 1,879 | 0 |

- **Coverage gap**: about 3 in 4 sampled calls reach more state than the snapshot holds. They run compiled, unchecked
  (`jitLarge`). So 0 mismatches covers the quarter that fits.
- IP and SV load a fourth `Array.Copy` body (45 bytes, sha256 `a395357028e6bcd9`), absent from al253's BP log. It is in
  `kMjHash` from the al257 build on.
- **al257** (`Direct=1`, build a8881117fbfa) [verified: native.json of each job]: PASS on BP, IP and SV.

| Save | Methods compiled | Calls verified | Mismatches | Too large (compiled only) | Code KB | Other thread |
|---|---|---|---|---|---|---|
| BP | 2,607 | 1,297,292 | 0 | 3,571,435 | 6,274 | 0 |
| IP | 2,606 | 1,140,309 | 0 | 3,373,724 | 6,261 | 0 |
| SV | 2,560 | 1,400,284 | 0 | 3,604,108 | 6,166 | 0 |

  `Direct=1` uses 3.2x the code (the lazy bodies). That is 6.3 MB of the 16 MB pool.

## 10. First A/B (2026-11-06, al258 / al259)

Local queue `jit-ab-local` (not committed): `Install=1 Mode=1 Direct=1 HashGuard=1`, build a8881117fbfa. `--ab monojit`
with 2-min slices, off-first and on-first per save, 10 min, quiet machine (other CPU 1.3-1.9 %), `[AutShadow]` off.
Every log has the `monojit: switch 1: N methods patched` / `switch 0` lines, so the on arm ran compiled code
[verified]. Arm means come from each run's seconds.csv. "threaded - route" is the Sims' script time without the
route plans they make inside it.

| Run | script (arm-ms) on - off | threaded - route on - off |
|---|---:|---:|
| BP off-first | -0.3 | -4.8 |
| BP on-first | -0.1 | +11.2 |
| IP off-first | +0.4 | -71.9 |
| IP on-first (rerun) | -0.0 | -27.3 |
| SV off-first | -0.2 | +6.0 |
| SV on-first | +1.2 | +0.7 |

- **No gain measurable** [measured]. The median of the right column is about -2 ms/s; one run swings ±40 ms/s, so
  this design cannot see a change under about 20 ms/s.
- **The script column is the wrong number for this lane** [verified: seconds.csv header]. arm-ms's `script` is a
  ~10 ms/s column. The Mono time the compiler touches is in `threaded` (~520 ms/s without route), so §5's "script is
  the number" should read "threaded - route".
- **What to expect** [inferred]. docs/207 §8.3 measured these methods 5-50 % faster offline. On the list's 41 ms/s
  of sampler self time (monojit.cpp `kMjList`), that is about 4-8 ms/s in game: under this A/B's noise floor and
  under docs/207 §8.5's 5 ms/s bar. The bar needs the next templates in docs/207 §16.3: `call` holds 36.2 ms/s,
  `ldfld.vt` 12.9 and `callvirt` 7.3.
- **One hang** in the first IP on-first run [measured: hang capture]. The sim thread stayed in route post pass 16
  (`0x64C7C0`, samples at `0x64CF22`-`0x64CF55`) through TailUnlock's `TuS16`, from Route_PlanFromPoint on the
  houseboat gangway path. No compiled code was on the sim stack (pool `0x06100000`-`0x07100000`). The rerun with the
  same seed passed. Cause not known; it is not attributed to `[MonoJit]` and not ruled out.

## 11. Long A/B and soak (2026-11-06, al260 / al261)

Build b91e56a23772 (74e7132), `Mode=1 Direct=1 HashGuard=1`, quiet machine (other CPU 1.5 %, TS3W pinned every
sample). Numbers are the Mono script time of hitch-ab.ts:517 (`taskObjects + threaded + oneShot + script - route -
rtLock - rtOtherLock - rtJob`), per 1-min slice, on minus off.

**Long A/B** (`--ab monojit`, 1-min slices, 60 min planned per save):

| Save | Minutes (off / on slices) | Mono ms/s off | on | on - off | se | sim iterations/s on - off |
|---|---|---:|---:|---:|---:|---:|
| BP | 16 (8 / 9) | 532.8 | 581.8 | +49.0 | 68.7 | -30.9 (se 48.4) |
| IP | 51 (26 / 26) | 746.9 | 681.3 | -65.6 | 79.7 | -0.0 (se 9.6) |
| SV | 48 (24 / 25) | 538.9 | 544.6 | +5.7 | 25.6 | -4.3 (se 15.5) |

- **No gain measurable** [measured]. The inverse-variance mean of the three saves is +4.6 ms/s (se 23), so the 95 %
  range is about -41 to +50 ms/s. §10's 4-8 ms/s gain is inside it and so is no change; even 60 min per save cannot
  resolve it, because the spread between 1-min slices (sd) is about 90-290 ms/s. The IP harness arm column gives -67.3 (paired slices
  -69.8, se 73.2), the same as the table.
- BP and SV stopped early on a career dialog the autopilot has no safe button for (BP: "The president of the studio
  ...", Actor / Director; SV: "One of your coworkers just started a rumour ..."). The game paused and the fail-fast
  guard stopped the job. Both are harness gaps, not `[MonoJit]`: the compiler was off in both slices. Their seconds.csv
  has no arm column (the job ended before the tagging), so the arm of slice s is the `jitSwitch` of ab-native record
  s+1; that rule gives the harness's number on IP.

**Soak** (60 min compiled per save, `Enabled=1`, no switching):

| Save | Result | jitMethods | code | jitFaults |
|---|---|---:|---:|---:|
| BP | **crash at 16 min**: exit `0xC0000409` | 2,784 | 6.7 MB | 0 |
| IP | PASS, 63.6 min | 2,802 | 6.7 MB | 0 |
| SV | PASS, 63.6 min | 2,718 | 6.5 MB | 0 |

- **The BP crash** [measured: probe detach `process-terminated`, exit `0xC0000409`]: fail-fast (a /GS cookie check or
  `abort`). No ts3native crash report and no Windows error event, since a fail-fast skips both. The harness logged it as
  "unrequested mode exit: household Angelista -> none", one second before the process ended. It is the first
  `0xC0000409` in every run the probe recorded. Cause not known. `[MonoJit]` is the first suspect, not proven: compiled
  code writes the interpreter's frame on the native stack, and a write past it would trip exactly this check.
- The many first-chance access violations in ApexRadiance.asi in these logs also occur in runs without `[MonoJit]`.

**What this means** [inferred]. With the 11-method list the compiler is not worth shipping: its gain is under the
in-game noise floor, and one soak crash is not explained. Next, if the lane goes on: a crash dump of a compiled soak
(full dump on fail-fast), then the templates of docs/207 §16.3 to reach a measurable gain.
