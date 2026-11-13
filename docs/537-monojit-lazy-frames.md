# docs/537: `[MonoJit] Direct`, lazy frames in the game, and the frame-reader gate (Mono compiler C6, C7)

Built 2026-11-06 on `feature/monojit-c6-c7` (base PR #291). ts3native 0.36.170, counters v909. Default off, not in a release, **never run in the game**. Everything below marked verified is source, disassembly or selftest; the dynamic half of the gate (§3) needs an in-game run.

## Summary

- **What it is:** the port of the host's lazy frames v2 (docs/207 §12.7) into `monojit.cpp`. A compiled call of a compiled method runs as a native call and skips EA's 209-233 instruction call path (docs/139 §8.2). No frame exists until the callee's first exit to an EA handler; a `ret` that never exited returns natively.
- **C6 census result:** 64 frame-handle sites in 31 places, **0 unknown readers**, and no EA reader is reachable from a lazy body [verified for the handle sites; inferred for indirect calls, §2]. Nothing shows C7 unsafe, so the port went ahead. C7 still cannot ship before `jit-reader-trap.json` passes.
- **Static coverage:** with the 11-name list only 2 call edges of the 96 in `docs/data/jit-call-edges.csv` became lazy, 0.54% of the calls of that table, and none with `DirectLeaf=1` (§5). §8 grows the list: 9 edges, 95.56%, and 1 edge, 69.61%, with `DirectLeaf=1`.

## 1. What was ported, what differs from the host

`native/monohost/monohost-jit.cpp` is the source and stays unchanged. Same block (`B_*`, 44 bytes), `EmitLazyCall`, `EmitLazyRet`, `JitLazyMat`, guard sites, two-pass emit, 768-byte position bound.

| Differs | Why |
|---|---|
| `DirectLeaf=1` is the default of `Direct=1`: leaf lazy bodies, no chains, no guarded callvirt | docs/223 §8 "narrow scope first". `DirectLeaf=0` is the host's v2 |
| `JitPreTransform` transforms only **listed** callees | nothing else gets a lazy body; the host's list is a test corpus |
| A killed method (a verify mismatch) clears its lazy cell and guard entries (`JitLazyOff`) | the host has no kill |
| Cells and guards are carved from the region's data part, 2048 + 1024 | no CRT heap on this path |
| `jitTrap*` stubs at the outermost call | §3 |

Not built: docs/223 §8 "Recovery" SEH around compiled code. Compiled code runs inside EA's frame and a fault there is EA's crash path, as with `Direct=0`. The per-method kill exists. "Valid chain head as fallback" holds differently from the text: a lazy call writes nothing, so the chain head stays the caller's valid frame and a reader sees a shorter, correct chain.

`Mode=2` works with `Direct=1`: pass 1 takes the lazy calls, pass 2 is EA's interpreter for the whole call tree (`outer` in `JitVerify`), so a mismatch names a lazy-frame bug [source read, selftest covers the two modes apart, not together].

## 2. Census (C6, static) [verified on Steam 1.67 unless marked]

`tools/ghidra/FrameReaders.java` (read-only, project `game-copy/ghidra`) lists every access to the handles, then `node tools/frame-readers.ts --census <prefix>` classifies them into `docs/data/frame-readers.csv`; a site with no class exits 1.

- **Handles:** TLS key `0x11F4160` (ThreadContext; +8 chain head), globals `0x11F4170` (current state), `0x11F416C` (records), the record array `ebp-0x4C74`, host fields `+0xBC8`, `+0xB84`.
- **Reach:** TLS gives a thread only its own context, so another thread can reach the sim thread's frames only through the two globals or the host. Those are read in `0xE534E0` and `ves_exec` only, plus the script host on the sim thread. Of 9 thread and callback entries (`0xF6F070`, `0xF49730`, `0x4DB220`, `0x9F0DE0`, `0x4E2E30`, `0x4E30A0`, `0xEBB500` APC, `0x5BFB90`, `0xB9C560`), none is a direct-call ancestor of a handle function.
- **Sim thread:** a lazy body reaches only `JitLazyMat`, EA's memcpy thunk, `_CIfmod`, memmove, `JitSatbStore`, `JitGuardFill`. None reads a frame. Every other EA code runs after an exit, and an exit writes the frames first. So no EA reader runs inside a window.
- **Inferred:** indirect calls are not followed by the ancestor walk. The 2613 sites that only match `[reg+0x13C/0x140/0x144]` are taken as other structs' fields unless a handle reaches them. A mod's icall is EA-side code after an exit, so the same rule covers it; a mod's native thread is what the trap's other-thread watch is for.
- **Our own tools:** `autledger.cpp` AsLgFrames, gcroots, the crash report, probe `/managedstack`, the sampler walk `ThreadContext+8`: they see a shorter valid chain across a lazy call, and callee time counts for the caller. `satb.cpp` reads the arena chunks, not the frames: the lazy bump is in `[chunk+8, top)`, so GC marking (`0xE69520`) sees the same words.
- **Host readers:** `0xD71CC0` (vtable data `0x108B7E4`) reads `rec[depth-k]` through `0xE534E0`; depth and records are EA's between calls, unchanged by a lazy call that returned.

## 3. Reader trap (C6, dynamic) [selftest; not run in game]

`[MonoJit] Trap=1` (needs `Direct=1`, `TrapEvery=1024`). One lazy call in N opens a window, from the outermost lazy call to its return or first frame write:

- **Poison:** `0xBADF0000 | slot` in the words of the dead record slots above the depth and their MonoInvocations, up to 8 slots. Live frames are not touched. Every poisoned word is written by `JitLazyMatC` or EA's push before a read, so a reader of an unwritten slot gets a pointer that faults. Selftest: after a materialize, no poison is left in the frame it wrote.
- **Debug registers** (no guard pages: the frames are on the sim stack): on the sim thread, the chain head, the depth, the next slot's `+4` and `0x11F4170`, read or write. On the other threads, every 5 s from `JitTick`: both globals, the last chain head and depth.
- **Counts:** a hit from compiled code (the site's own depth test) does not count. `jitTrapHits` = the rest. `jitTrapUnknown` = any hit in a window, or off the sim thread outside `kJitReaders` and ts3native. Log `jit trap: UNKNOWN|known hit ...` with module, thread and the stack's return addresses, first 64.
- **Gate:** `jitTrapUnknown` 0 and no `jit trap: UNKNOWN` line over `jit-reader-trap.json`, with `jitTrapWindows` > 100. A window hit needs `node tools/frame-readers.ts --log <ts3native.log>`, then a census row or a fix. The sampler job reads with `--allow-frida` (never for the sim thread).

`XcptLog` skips both the arm exception and the single steps (`JitOwnsXcpt`), as with `[GcRootCheck]` TailWatch.

## 4. Counters v909 (0x5C6C-0x5C97)

`jitDirect` (0, 1 leaf, 2 chains), `jitLazyBodies`, `jitLazySites`, `jitGuardSites` (both count a site per body, lazy and normal), `jitLazyCalls`, `jitLazyMats`, `jitLazyFrames`, `jitTrapOn`, `jitTrapWindows`, `jitTrapHits`, `jitTrapUnknown`.

## 5. Static coverage [measured on the host corpus's edge table, no game data]

`docs/data/jit-call-edges.csv`: 96 edges, 22.69 M calls, of which 21.8 M direct or class guard. Both ends in the 11-name list of `monojit-list.h`:

| Edge | Calls | Lazy? |
|---|---|---|
| `Dictionary.GetSlot` -> `GetPrev` | 98,173 | `DirectLeaf=0`; GetPrev's inner callvirt is not listed, so the frame is written at that call (1.50 per call) |
| `List.Add` -> `GrowIfNeeded` | 24,801 | `DirectLeaf=0`, same at its first inner call (0.11 per call) |
| `Array.Copy` -> `Array.Copy` | 24,942 | no: the callee has clauses |
| `Slots.GetParentId` (polymorphic) | 1 | no |

So `DirectLeaf=1` has 0 listed edges and `DirectLeaf=0` has 122,974 calls, 0.54% of the table, and those calls save the call path only up to the first inner call, where the frame is written anyway. The large edges (`Math.Min` -> `Single.IsNaN` 15.8 M, `GetPrev` -> `Equals` 4.7 M) have callers or callees off the list. Next step for gain: rows in `jit-hot` for those, with C5's templates. The body compiler's coverage itself did not change.

## 6. Queues and what a run must show

- `tools/harness/queues/jit-reader-trap.json`: `bp-jit-trap-chain`, `ip-jit-trap-chain` (repro `jit-reader-trap`, samplers off, the gate), `ip-jit-trap-samplers` (own off-thread readers).
- `tools/harness/queues/jit-verify.json`: `bp-jit-lazy-chain`, `ip-jit-lazy-chain` (`Mode=2 Direct=1 DirectLeaf=0`).
- All need `game-copy/ts3native.asi.jitall` (minCounters 911, updated from `.jit2` 2026-11-06). Order: trap, then verify, then a clean night (docs/527 §6). `jit-ab` is parked (2026-11-06, Rodney): no timing job, the compiler is measured statically, and no `Direct=1` gate reads it.

## 7. Open

- No game run, so `JitPreTransform` (`IlpXform` on a listed callee) and every real opcode path of the lazy sites are host-proven only.
- `kJitReaders` ranges are wide (the script host, all of `ves_exec`): a new reader inside them counts as known off the sim thread.
- `DirectLeaf=0`'s `jitLazyMats` per call is the number to read first: a body that always materializes pays the block and the frame.

## 8. The list for lazy frames (2026-11-06, feature/monojit-all)

Both gates below hold for host and game; the code is a copy, as with the rest of the port.

**The `lazyOk` gate.** It was a speed rule: a `K_EXIT` position anywhere meant no lazy body, because a hot exit costs the block plus the frame. A position whose straight run ends in `throw` (`JitMarkCold`: walk back from `throw`, stop at `ret*`, a branch, `switch`, `leave`, `end*`) no longer counts [verified: source; host corpus]. Such an exit is the same exit as a null or bound exit: `EmitExit` in a lazy pass writes the frame first, then EA's handler runs and the native call is abandoned. A hot path can branch into the run, but the run still ends in the throw, and a compiled method has no clauses, so nothing resumes in the lazy body. New corpus case `JitKI.Cold`: a list and a dictionary change under the enumerator, `VerifyState`'s throw block runs from a lazy `MoveNext`.

**The float compares (group bit 256).** The 25 forms of docs/221, `ceq/cgt/clt(.un).r8` and `b*.r8(.s)`: `fucomip` without EA's two `_isnan` calls, the spec's `fcmp` and `REL` as they are [emu: 49,152 cases, docs/221, the spec's bytes, not our emitter's; host: corpus `CmpR8`, new `CmpR8V` over the R8 patterns incl. NaN, ±0, inf; selftest: `ceq.r8`, `blt.r8.s` on NaN]. The reason for §16.3's drop of `Math.Min` and `Single.IsNaN` is gone: `IsNaN` ran 15.1 M `ceq.r8` exits, now 0.

**The rows.** `tools/jit-hot.ts`: `DROPPED` is gone; `LAZY_ROWS` lists 11 callees that are below the 1.0 ms/s cut or not sampled, with the exit rule kept and the threshold waived; `--emit-native` walks every assembly of a merged `Enumerator` frame and every nested type of that name. `--coverage <count log>` prints the table below. The host logs `jitlazy leaf|chain <name>` per lazy body.

| Static coverage, `docs/data/jit-call-edges.csv` (96 edges, 22.69 M calls) | Before | After |
|---|---:|---:|
| Listed methods / bodies | 14 / 17 | 27 / 58 |
| IL bytes | 1,505 | 3,729 |
| Sampled self time on the list, of 177.3 ms/s | 55.3 | 66.0 |
| Held back at 1.0 ms/s | 48.7 | 41.7 |
| `DirectLeaf=0` edges, calls, share | 2, 122,974, 0.54% | 9, 21,683,082, 95.56% |
| `DirectLeaf=1` edges, calls, share | 0 | 1, 15,793,600, 69.61% |

[measured, host only] The table is the host's bench: `Math.Min` -> `Single.IsNaN` is 69.6% of it and only 1.1 ms/s of sampled game time. Read the sampled row for the game, the edge rows for the mechanism.

**`DirectLeaf=1`.** Fixed, 1 edge. Other edges stay chains: `IEquatableOfT.Equals` and `GetSlot` make an inner call, so `DirectLeaf=1` gives them no lazy body by rule (`--coverage` prints each row). `GetPrev` -> `IEquatable.Equals` (4.74 M) needs `DirectLeaf=0`.

**Not moved.** `Enumerator.get_Current` 5.5 ms/s: `newobj` of `KeyValuePair`, a value type, 0.6 per execution, plus `ret.vt`, which never gets a lazy ret. `Array.Resize`, `Array.Copy`: `Array.FastCopy` and `GetLowerBound` can raise. `GetComponent`, `get_Parent`, `GetObject`, `get_LotCurrent`, `ElapsedTimeInMinutes`: `castclass`, `ldtoken`, `ldind.r4`, `sub.r8`, native calls. `Math.Max`: the `long` overload's `bgt.i8.s`.

**Check** [host, feature/monojit-all]: corpus 206 cases, 0 differ, 0 unstable, lazy frames on and off; per-call verify ok 33,593 and 33,528, mismatch 0, abandoned 1,562 and 1,553. ts3native selftest ok. `M5-concurrent-noalloc` and `M5-n2m-private` fell to 26-29 main passes under `MONOHOST_JIT=verify MONOHOST_JITLAZY=1` from 421 (`jit=on`: 404, wrong results 0 every time): verify snapshots each call of a method the workload now calls a lot, so their bar is 4 per round under verify. `GM4-sweep` fails in every run of this tree, base included, with 3 overriding types, before any change of this lane (game-copy sweep, not the compiler; not checked on plain master). Ledger §436.

## 9. S4: inlining (2026-11-06, ts3native 0.36.172, off by default)

`[MonoJit] Inline=0|1|2`, host `MONOHOST_JITINLINE`. The host's inliner of docs/207 §8 is ported, the game's copy is the same text. Off with `Inline=0`; no opcode, table or list change.

**Tier 1 was dead code.** The host tested `cmp [transparent_proxy_class], 0; jne slow`: that class is always set, so every inlined site took its slow path, and all earlier "corpus clean with the inliner" results said nothing about the inline body [verified: 0x01220EC0 holds the class, `EmitLazyCall` compares it with the receiver's class]. The test is now the receiver's class against it, as EA's call path does. A count run shows the fast paths run: 158,368 on the corpus.

**Also fixed.** `ldfld.r4` has width 7, added after the inliner, and only 6 was refused: such a getter would have returned raw float bits. Refused now. Mint inlines plain `ldthis; ldfld; ret` getters itself at transform, so these never reach the inliner [inferred: `JitIN.V` and `F` appear in neither log row].

**The site.** All tests run before any write, so each leaves EA's call path from the same state: `this` null, remoting class, ForceJIT (`0xE5637D`, as lazy sites), frame does not fit the arena chunk, and at tier 2 a null base. The high dword of EA's return copy is read after the body now. The callee must be compiled: its IL passed the hash gate, so a game update or a mod's replacement cannot be inlined. `JitPreTransform` runs for `Inline` too, so a listed callee compiles before its caller.

**Tier 2.** `ldfld` on any base, `ldind`, `ldlen`, a value-type `this` (`call`, `con.call`). No store, no branch, no call, nothing 8 bytes wide, up to 12 instructions. A body with a store stays a call: `Dictionary.set_Count`.

| Host corpus, 207 cases | Sites inlined | Refused | Corpus differ | Per-call mismatch |
|---|---:|---:|---:|---:|
| `Inline=0`, lazy off | 0 | 0 | 0 | 0 |
| `Inline=1`, lazy off | 3 | 241 | 0 | 0 |
| `Inline=2`, lazy off | 51 | 245 | 0 | 0 |
| `Inline=1`, lazy on | 3 | 384 | 0 | 0 (corpus), 1 `kept -1` in the bench, §11 |
| `Inline=2`, lazy on | 87 | 384 | 0 | 0 |

[measured, host] A lazy pass counts a site twice. New corpus case `JitKIN.Run`: tiny getters, a null at each base, value-type `this`, a hiding method. Selftest `TestMonoJitInline`: value and high dword, null, remoting class, ForceJIT, no room, null inner base, tier 1 refusals, `ldind`, `ldlen`, `con.call`.

**Coverage in the game:** `List.get_Capacity`, `Int32.Equals`, `Int32.GetHashCode` are listed and inline; the 19 accessors of docs/207 §9 mostly call out, branch or store, and their corpus rows stay as they are. Most refusals are size: `GetPrev`, `GetSlot`, `DoHash`, `GrowIfNeeded`. The gain is small unless the list gets more tiny callees. No game run yet. Counters v911: `jitInlined`, `jitInlineNo`.

## 10. S5: frame-free exits and clauses (2026-11-06, off by default)

`[MonoJit] Wide=1`, host `MONOHOST_JITWIDE=1`. Group bit 512, added to `Groups` only by `Wide`: with `Wide=0` no table row and no refusal changes.

| Template | Source | Check |
|---|---|---|
| `add/sub/mul/div.r8`, `neg.r8`, `conv.r4.r8`, `conv.r4/r8.i4/i8` | docs/221 table, x87 as EA | emu 49,152 cases on the spec bytes; corpus `Add/Sub/Mul/Div`, `NegR8`, `ConvR4R8`; selftest |
| `conv.i1/i2/i4.r8`, `conv.u1/u2.r8` | docs/221, `cvttsd2si`; signed forms only when `[0x1234374]` is set at compile time | corpus. The flag read 0 in the host: `conv.i4.r8` stays an exit there, so the template has **not run** |
| `conv.u4/i8/u8.r8` | `fisttp` for the chop and `_ftol2`, needs SSE3 | C10 below; corpus `ConvU4R8`, `ConvI8R8`, `ConvU8R8`; selftest incl. NaN |
| `ldind.r4/r8`, `stind.r4/r8` | x87, Satb barrier; a null exits, the fault stays EA's | selftest, new corpus `JitKI.Wide` |
| `ldstr` | `0xE56135`: slot = `items[w1]` | selftest, corpus |
| `ldtoken` | `0xE57A11`: slot = cursor, `[cursor] = items[w1]`, cursor += 8 | selftest, corpus |
| `castclass` | `0xE56449`, `EmitIsinst`'s tests; null passes, a failed test exits and EA throws; remoting and array classes stay exits | selftest, new corpus `JitKI.CastS`, `CastI` |
| Methods with clauses | the host's way: normal body, no lazy body, `verifyOk` false, so not verified per call | corpus only |

**`box` not built.** It allocates: a collection under an unwritten lazy frame is outside the census of §3, and the exit cost in a normal body is small. **No list rows for clause methods:** the switch lets a listed one compile, none is listed.

**C10** [measured: i7-11800H, `tools/emu/fisttp-check.cpp`]: `fisttp q` against `fnstcw; or 0xC00; fldcw; fistp q`: 600,465,607 inputs (specials, 8,193 neighbours each of ±2^31, 2^32, 2^63, 2^64, 300 M random bit patterns, 300 M in the integer range), 154,569,941 integer-indefinite results, 0 differ in value, exception flags or stack top. One CPU. Not compared with EA's `_ftol2` itself; the corpus covers `conv.i8.r8` and `conv.u8.r8` through the real handlers.

| Host, `--count`, lazy on | `Wide=0` | `Wide=1` |
|---|---:|---:|
| Native ops, static | 14,667 | 14,839 |
| Exit sites, static | 2,002 | 1,830 |
| Lazy bodies | 594 | 633 |
| `mul.r8` + `add.r8` + `sub.r8` exits taken | 58,226 | 0 |

[measured, host] Exits taken do not fall overall (13.30 M, 13.44 M): more lazy bodies mean more lazy `ret` exits counted, the bench is not the game. Accessors of docs/207 §9: `GetComponent` keeps `callvirt`, `ret`, `call` and loses `ldtoken`, `castclass`; `get_LotCurrent` loses `castclass`; `SimClock.ElapsedTimeInMinutes` loses `ldind.r4`, `sub.r8`, `stind.r4`.

Corpus, 209 cases: 0 differ, 0 unstable, per-call mismatch 0: `Wide=1` lazy off and on, `Wide=1 Inline=2` lazy on. Not run: `Wide=1 Inline=2` lazy off. The list is unchanged: `tools/jit-hot.ts` judges rows with `Wide=0` exits. No game run. Counters v911: `jitWide` sites compiled (a lazy pass counts again), `jitWideNo` left as exits for a missing flag or class.

## 11. The verifier's `kept -1` mismatches (2026-11-06)

F4's host loop (`tools/monohost-stress.ts --arms jit,subjit --bench 300`, docs/139 §4.4, I-430-7) found rare `jit verify MISMATCH` lines with `kept -1`: 11 lines in 8 runs, 10 of them this shape, stock included. The corpus runs of this lane showed none, then one run of §9 did.

**Cause [verified: logged, host].** A free GC cell sits in the entry snapshot. A stale word in the graph reaches a cell, and the cell's word 0, the free-list link, reaches the next one. `JitKeepNew` skips any address the entry snapshot holds, so what pass 1 allocates in that cell gets no kept copy, and pass 2 allocates the next cell of the list. The two words differ and `JitAllocEqual` has nothing to compare. Each logged line, with the mismatch now printing the compiled word's region: `region-of-compiled 1308 word0 0D9892A0 now 0D9892A0`, interpreted `0D9892A0`, `gc 1093->1093`. So the compiled pass's object is a region of the entry snapshot, its entry word 0 is the very address the interpreted pass got, and no collection ran in the call. 12 of the 13 lines of the loop below have that shape.

It is the verifier, not compiled code: the bench checksum and the corpus stay right in every run. It matters in the game: under `Mode=2` a mismatch switches the method off and fails the `jit-verify` gate.

**Fix.** `JitFreeCell` (word 0 is 0 or a GC address, a vtable never is): `JitSnapRef` leaves such a cell out, in host and game. A raw GC block with a pointer in word 0 goes with it: managed code does not see one as an object, and a byref into a block is not touched by the test. The restore no longer writes old links into cells either.

| Host loop, `selftest`, 3 workers, bench 1, `kept -1` fix as the only difference | Runs | Runs with a mismatch | Lines |
|---|---:|---:|---:|
| Before the fix | 20 | 7 | 13 |
| With the fix (1.0-1.2 M cells left out per run) | 20 | 0 | 0 |
| Final build, plain verify | 12 | 0 | 0 |
| Final build, lazy, `Inline=2`, `Wide=1` | 12 | 0 | 0 |

[measured, host] No other check failed in these 64 runs (`GM4-sweep` ignored, it fails in every run).

**Not explained** (later: the game's `List.GrowIfNeeded` line and a sweep fix, §15). 1 of the 13 lines is `kept -1` on `List.GrowIfNeeded` with a compiled word that is not in the snapshot: a new 512-byte array got no kept copy for another reason, 1 run in 20 before the fix, 0 in 44 after. 1 host run without the fix faulted in the `M1.3` `.cctor` sweep (21 faults, `strnlen` on a bad name, then a jump into data); not seen again in 64 runs, of which 20 without the fix. A restore writing an old link into a cell allocated since could do it [inferred]. The `Single[]` word of I-430-8 did not repeat: 0 of 64 here, no fusion arm run with the fix. F4's `tools/monohost-stress.ts` reads the log when monohost's launcher exits, while its child still runs, so a run's count can come from a partial log or the file can be busy; these loops waited for the child.

`C5`'s `JitLiveBytes` does not fix this shape: it limits the bytes compared in a region that holds the arena, and the `kept -1` regions are `Slot[]` objects. It can only explain the `Array.Resize` line, region 3 of 2048 bytes with no class: F4's branch has no `JitLiveBytes`.


## 12. First game run: the trap hid the agent mailbox (2026-11-06, ts3native 0.36.174, pair `mono1106c` with §13)

`bp-jit-trap-chain` on `mono1106` failed with `agent answering: not reached within 300 s`. The game was fine; the harness could not reach it.

**Cause [verified: logged].** `JitTrapArmOthers` armed every other thread, Frida's script thread too (thread 50552, start `frida-agent.dll+0xc1e00c`, thread start addresses read in the running job). The chain head and the depth lie in the 369 MB GC-heap range that also holds the 1 MB mailbox array (`0x7FFF64C8` and `0x8003A840` in the next run; range `0x7FFF0000`, mailbox `0x86BC0010`). The mailbox scan (`findMailbox`, `tools/probe/agent.js`) reads through that range. On a failing start `Memory.scanSync` threw, the old bare `catch` skipped the range, and the mailbox was never found: with scan logging added, `/agentstatus` showed `0x7fff0000 +369098752: single-step triggered` and no address. In the other run of the same build the scan got through, and the same reads landed in `JitTrapVeh` instead: 8 `jit trap: UNKNOWN hit ... frida-agent.dll`, which fails the gate on its own. Which handler takes the single step is not read from Gum's source [inferred].

**Suspects ruled out [measured, one load each].**

| Arm, same `mono1106` pair, BP Spring | Agent answers | World load |
|---|---:|---:|
| `MonoJit.Enabled=0` | 46 s | ok |
| `Direct=1 DirectLeaf=0`, `Trap=0` | 45 s | ok |
| `Trap=1 TrapEvery=64`, scan got through | 45 s | 27 s |
| `Trap=1 TrapEvery=64`, scan failed | never | not started |

So no startup regression in master, no slowdown from the windows or `TrapEvery` at load (374,361 windows 5 min after launch, 0 faults), no hang at agent startup. The first run's main menu and its 81,646 windows were normal: only the first agent command was missing. The 8 `crash|exception` lines of the failing log are 5 first-chance exceptions (`spfileq.dll`, `setupapi.dll`, two C++ throws) found in every baseline log, plus 3 header lines.

**Fix [verified: 0 frida-agent hits and the agent at 45-46 s in 2 runs].** `JitTrapArmOthers` leaves out threads whose start address is in `frida-agent` (`JitTrapProbeThread`) and logs how many. Game, mod and ts3native threads are armed as before, and the count rules are unchanged: `jitTrapUnknown` and the `UNKNOWN` log rule still gate. Frida hook code on a game thread still counts. `tools/probe/agent.js` keeps the failed range in `lastScan` (`/agentstatus`, also once the mailbox is found).

**Limit.** A read by Frida's own thread is no longer seen, so `ip-jit-trap-samplers` cannot show the probe's stack walks as hits. A sampled stack is the probe's own view; the gate is about game code.

`jit-reader-trap.json` now has `stopQueueOnFail`.

## 13. Second round: dead slots counted as readers (2026-11-06, ts3native 0.36.175)

With Frida out, `bp-jit-trap-chain` on the 0.36.174 build failed with `jitTrapUnknown` 71, 0 window hits. Only 9 of the 71 were logged, because the 5 s arm line shared the 64-line log budget. All 9 are one watched address, `4391AA48`, in `ntdll`, on two game threads, with `RtlLeaveCriticalSection+0xB0` or `RtlEnterCriticalSection+0x63` on the stack, called from TS3W `0x4E3FA5`.

**Reading.** The other threads' last-window chain head and depth belong to one interpreter run, and they stay armed up to 5 s after it. A task state is `GC_malloc(0x150)` and is freed with the task; `runtime_invoke`'s state is on the sim stack, and `0xE59FD0` can alloca a temporary ThreadContext [verified: disassembly, docs/119 §11, `CanYield` note in ts3native.cpp]. After the run that address is other memory. The nine hits are contended critical-section paths, so `4391AA48` was a lock or a wait block by then [inferred: the address was not dumped, and the same pattern did not come back].

**Rule.** A lazy call runs in the innermost ves_exec state, the one in `0x11F4170`. So a hit off the sim thread on a per-run address, while the armed state is not current, is logged as `known ... state not running` and counts in `jitTrapHits` only. Hits on the two globals, hits while the state is current, and every sim-thread hit are judged as before. **This is a narrower count than §3:** a thread reading a task's frames while that task is parked is no longer unknown; those frames were written by EA, no lazy call is out.

The premise holds in the log: every arm line prints windows whose state was not current, 0 of 106,597 in the 8 lines of the passing run. The arm line has its own log budget now, and a hit line prints the armed state.

| `bp-jit-trap-chain`, BP Spring, 10 min | Windows | Hits | Unknown | Faults | Result |
|---|---:|---:|---:|---:|---|
| 0.36.174 | not read | 71 | 71 | 0 | FAIL |
| 0.36.175 | 1,064,230 | 0 | 0 | 0 | PASS |

[measured] The dead-slot rule did not fire in this PASS run (hits 0), so it cannot be what passed it. It fired in the later runs below.

**Later runs (§14):** the rule fired in every job with hits: 14, 1, 8, 24 and 32 hits, each with the watched address at exactly armed state `+0x140`, in `ntdll`, called from the same TS3W `0x4E3FA5` path, off the sim thread. Each one is paired with a known hit in ts3native on `011F4170`: the rule's own read of `*g_curState` on a thread that watches that global [inferred from the pairing: same count, same thread, the eip was not mapped]. Harmless, counted in `jitTrapHits` only. The states sit in the 0x43xxxxxx range, the sim stack in the hang dump (`esp` 0x43969E68), so the reused memory is the sim stack [inferred: stack bounds of those runs were not read].

**Decision (Rodney, 2026-11-07):** the dead-slot rule is kept as written.

## 14. Third round: hang in the samplers job, Frida's exception lock (2026-11-06, ts3native 0.36.175)

`jit-reader-trap` on mono1106c: `bp-jit-trap-chain` and `ip-jit-trap-chain` PASS. `ip-jit-trap-samplers` hung 20 s into the managed sampler: `agent not answering for 61 s`, kill dump, queue row CRASH. No crash report, no game exception: a deadlock. The queue reran the job as `noisy machine` and the rerun passed; that says nothing about the hang.

**Evidence** [verified: `probe-out/dumps/ts3w-47076-20261106T092706.dmp`, read with a throwaway minidump parser]:

- Sim thread 50560 sits inside Frida's `KiUserExceptionDispatcher` path (`frida-agent.dll+0x66794`), two instructions after the return of a lock call, on the trap's own `RaiseException(0xE0503909)`, record at `0x4396AAE0`.
- The lock is `0x04BAA76C`, an SRW lock: the sim frame holds that pointer at `esp+0x18`, the word is `0x01E3EE43` (locked, waiters queued, first wait block on thread 48760's stack).
- Thread 48760 (a game thread in its own exception dispatch) and thread 44560 (Frida's JS thread, `frida-agent.dll+0x8843A` to `+0x66668`) both wait on it, `edi` `0x04BAA76C` on both.

**Cause.** `interpFrames` (the managed sampler) suspends the sim thread and reads its frames with Gum memory reads. Gum takes its exceptor lock for a read's fault scope, and the sim thread takes the same lock on every raised exception. Suspended while holding it, the sim thread never lets go, and the JS thread that would resume it waits for it. That Gum's reads and default `NativeFunction` calls are what take the lock, and that the sim thread was suspended (the dump lists suspend count 0 for all 156 threads), are [inferred]: the JS stack was not mapped to Gum symbols. Trap=1 is what opens the window, about 2,000 raised exceptions per second on the sim thread, so normal runs do not see it [inferred]. `threadStack` (`/simstack`) had the same risk through `CTX.writeU32` and its `NativeFunction` calls.

**Fix** (`tools/probe/agent.js`, probe only, the pair stays mono1106c). Between `SuspendThread` and `ResumeThread` nothing uses Gum: `SuspendThread`, `ResumeThread`, `GetThreadContext` and a new `ReadProcessMemory` run with `exceptions: 'propagate', scheduling: 'exclusive'`, results land in a buffer read through `ArrayBuffer.wrap`. `threadStack` writes the context flags before the suspend and reads the registers after the resume.

| `ip-jit-trap-samplers`, IP Spring, 10 min | Windows | Hits | Unknown | Faults | Managed samples, non-empty | Result |
|---|---:|---:|---:|---:|---:|---|
| old agent, first try | 107,083 | 0 | 0 | 0 | none | HANG 20 s into the sampler |
| old agent, queue rerun | 1,230,163 | 16 | 0 | 0 | 887, 640 | PASS |
| new agent, run 1 | 1,270,788 | 48 | 0 | 0 | 885, 637 | PASS |
| new agent, run 2 | 1,338,250 | 64 | 0 | 0 | 887, 608 | PASS |

[measured] The new agent's samples match the old ones: depth 14.0 against 14.8, the same top methods, 876 against 872 sim stacks, 0 sim samples lost. The hang came once in 2 Trap=1 sampler jobs on the old agent, too rare for 2 clean runs to prove the fix on their own: the dump shows the mechanism, the runs show the new windows work.

## 15. `List.GrowIfNeeded` `kept -1` in the game: dead cells in the snapshot (2026-11-06, ts3native 0.36.178, asi `mono1106d`)

`jit-verify` on mono1106c: `bp`, both `ip` and `sv` jobs each log one `jit verify MISMATCH List`1.GrowIfNeeded: region 3 (24 bytes, class List`1) at +4 ... kept -1 size 0/48`, at the same log line during the load. Region 3 is `this`, +4 is `_items`. The interpreted word is a fresh length-4 array (capacity 0 to 4), the compiled word 48 bytes above it, so both passes allocated. The method then runs interpreted for the session. `bp-jit-verify` still read PASS: the load falls before the job's `/native` before snapshot, and no job had a rule on the log.

**Verdict: the verifier, not the compiled code.**

- **Compiled growth is right [verified: host].** New corpus case `List growth: size, version, capacity, elements` (`int`, `ulong`, `ObjectGuid`, `string`, `object`; new list, doubling, capacity argument, `AddRange`, `Insert`, `Capacity =`; `_version` read by reflection): 211 cases, 0 differ, per-call mismatch 0. The hash leaves the array's identity out.
- **Cause [verified: host, logged, then reproduced to a fault].** `JitFreeCell` (§11) sees only swept cells. A dead cell of a block not yet swept still holds its vtable, so a stale word brings it into the entry snapshot as an object. The pass's first allocation sweeps the block: the new array lands in a cell `s0` holds, `JitKeepNew` takes it for an old object and keeps no copy, which is `kept -1`. Other cells of that block are now on the free list, and `Restore` writes their old vtables over the links. The next allocation from that list pops the vtable's address.
- **Repro.** Case `List growth into dead cells`: a stale word names dead 512-byte arrays of mixed blocks (an all-dead block is freed at once and never reaches this), and the case drains the swept cells so that the growth is the allocation that sweeps. Run it alone (`MONOHOST_JITCORPUS_FROM=139 MONOHOST_JITCORPUS_TO=139`): the corpus's per-method verify cap (2048) is used up before case 139 in a full run.
- **The game's own line was not this: §16.** It was [inferred] to be this; `mono1106d` refuted it. The host logged the new array in a snapshot cell 11 times and the vtable-over-link state, then faulted at `0xE4C223` (`GC_ea_alloc_unmark`) with a null block header, so it never reached a `kept -1` line. The game's line does not say whether the word was in the snapshot; `mono1106d` prints `compiled word in entry snapshot`. The game is more exposed than the host: its sweep is spread over frames (docs/13 Q3), and the load allocates hard between them.

Ruled out in the host only: the kept set filling (host peak 149 KB of 1 MB, 0 dropped; wrong for the game, §16). Also a free cell (§11), a method that did not grow (the two addresses are one size class apart, as two allocations are).

**Fix.** `JitSweep` before each snapshot, host and game. It does what EA's per-frame step does at `0xE49F60`, disassembled: if a sweep is pending (`[0x11F2F94]`), run the owed finish `0xE49A50` when `[0x11947E4]` != -1, `GC_reclaim_all(NULL, 0)` `0xE50380`, clear the flag. It stands off while a mark runs or GC is disabled (`[0x11F30A8]`), and takes the `[MonoThread]` lock when one exists: the first try, without the lock, crashed in the host's M5 test with the worker allocating. `[MonoJit] Mode=2` only; a stall at the first verified call after a collection, nothing in a shipped mode. No allow entry: an allow rule would have hidden the free-list damage. The sweep stays for that fault; it did not end the game's line.

| Host, `MONOHOST_JIT=verify` | Repro case alone | Stress `--arms jit --bench 1`, 3 workers |
|---|---|---|
| Before the fix | fault at `0xE4C223` in the compiled arm | 6 runs, 0 mismatches, 2 with the `M1.3` `.cctor` faults |
| With the fix | 2 runs: 2640 verified, 0 differ, 0 mismatches | 12 runs, 0 mismatches, 0 `M1.3` faults |

[measured] One stress run with the fix failed `AS-G-A2` (main passes 198), the timing bound of docs/139 under 3 workers. `GM4-sweep` fails in every run, as in §11. **Not solved:** the `M1.3` faults of §11. They came in 2 of 6 runs before the fix and in 1 of 15 with it (the 12 stress runs, 3 single runs), so the free-list damage is not their whole cause. Both logs show the same stack: `strnlen` on a bad name under `0xE6F8C1`.

**Queue.** All 8 `jit-verify.json` jobs get `logForbid {"jit verify MISMATCH": 0}`, with no allow entry. A `jitMismatch` delta rule misses a load-time mismatch, the four lazy rows' rule included. Running the queue on mono1106c fails at the first job until it is repointed. `mono1106d` is registered, in no queue yet; fixes package stays `mono1106c`.

## 16. The game's `GrowIfNeeded` line: a full kept set (2026-11-07, ts3native 0.36.179, asi `mono1106e`)

`bp-jit-verify` on `mono1106d` failed on the same line, and the field §15 added names the cause [verified: logged, `...-bp-jit-verify-1550-ts3native.log:649`]: `compiled 81FD0660 interpreted 81FD0630; kept -1 ... compiled word in entry snapshot 0, kept n 2048 used 156316`. Not a dead cell: the set of kept copies was full at `JIT_NEW_N` = 2048 objects. The zero words in the line are the missing copy, not memory.

**Deep compare already existed.** `JitAllocEqual` compares two objects allocated in the passes by class, length and elements, bounded at 3 levels. It never ran here: the compiled word had no copy, and a word with no copy fails closed.

**Cause.** `JitKeepNew` went through the regions in order and walked each changed word depth first. It cannot tell a new object from an old one the entry graph did not reach, and the callee area comes before `this`. Which word filled the game's set is [inferred], not logged: an old object's graph left in a callee frame, or a local.

**Fix.** `JitKeepPass`, host and game. The first round takes every compared word that changed, heap regions and the return value before the frame. The second follows those copies breadth first, to the same 3 levels. The callee area is skipped: it is never compared. `over` counts words lost to a full set and goes on the mismatch line. A lost word still fails: no compare was relaxed.

Limit: a new object nested inside a kept one can still lose its place to a flood in the same call. That reads as a mismatch with `over` > 0, a false failure, never a false pass.

| Host, case `Kept set flood` alone, 64 calls | Verified | Mismatch |
|---|---:|---:|
| Before (a local names 3000 old arrays, the heap gets a new array) | 0 | 64, `kept -1`, the game's shape |
| After | 64 | 0 |
| After, negative control: one element flipped in the kept copy (one-off build) | 0 | 64, `first differing word 5` |

**Second fix, same compare.** The full corpus with these cases gave 13 `List.Add` mismatches, `over` 0, kept copy present, first difference past the elements: an `int[4]` in a 48-byte cell, and the compare assumed 8-byte elements because they fit. The corpus runs with `GC_malloc_atomic` as it is, so that slack is stale. `JitAllocEqual` now reads the element size at `MonoClass +0x5C` [measured: `Int32[]` 4, byte enum 1, `ulong` enum 8, references 4] and stops at the last element. A different length still fails. The `GcLock` row needs no slack note any more.

[measured] The host's flood mismatch is on the frame word, the game's on `this`. Full corpus: 212 cases, 0 differ, per-call mismatch 0 (13 before the second fix).

## 17. Real inputs for the host: capture in game, replay in the host (2026-11-07, ts3native 0.36.182, asi `jitcap1107`, no game run)

The host corpus calls listed methods with arguments we wrote. The game calls them with Sims, lots and half-built lists. Rodney's decision 11-07: close that gap by sampling real calls and running them in the host, interpreter against compiled, the way `JitCorpus` does.

**Capture, `[MonoJit] Capture=1` (`native/ts3native/monojit-capture.h`, off by default, not for players).** Keys: `CaptureFile` (required), `CaptureFirst=8`, `CaptureReservoir=8`, `CaptureDepth=4`, `CaptureObjs=256`, `CaptureBytes=65536`, `CaptureObjMax=16384`, `CaptureBudgetUs=2000` per second, `CaptureMaxMb=64`. At word 0 of a compiled method (`JitEnter`, a cold branch behind `g_jcOn`) it samples the first N calls per method, then a reservoir of R slots. A sample is `this`, the raw argument slots, a breadth-first walk of the objects those words name, and the static fields the method's mint code loads. Objects are raw bytes plus a class name; as in `JitSnapGraph`, a word that is the start of a GC object counts as a reference, so strings, arrays, boxed values and cycles need no typed code.

Sim-thread rules: memory comes from one `VirtualAlloc`, no GC allocation, no suspend, no call into EA's runtime, no save data. Every walk is bounded and SEH drops a sample that faults. Each skip is counted by kind in the `jit capture:` line (budget, buffer room, file full, faults, cuts, no class, free cell, outside the frames, static skipped). The file is written from `JitTick`, and every write ends on a record, so a killed game leaves a readable file. A word naming an object past a bound is zeroed in the copy, so no game address reaches the replay; `cuts ... (n not listed)` counts those past the 512-row cut list. No counter was added, so `docs/registry.md` has no new slot.

Only calls through `JitEnter` are sampled: the queue runs `Mode=1 Direct=0 Inline=0`, and with Direct or Inline on the startup line says their calls are not sampled.

**Replay, `MONOHOST_JITREPLAY=<file>` (`native/monohost/monohost-jitreplay.cpp`, `JitReplay.cs`, `tools/jit-replay.ts`).** `tools/jit-replay.ts --capture <file> --out <text>` applies the reservoir rule and writes one text input per kept sample. `JitReplay.cs` resolves each type and method against the `game-copy` DLLs, allocates objects without constructors, and the host writes the captured bytes into them with pointers remapped. Each input runs warm once, then twice interpreted and once compiled, on restored bytes; the arms compare result, heap hash over the rebuilt objects and exception type. Two interpreted arms that disagree are `unstable`, not a difference. Captured statics are set for the call and restored after it. Inputs that cannot be rebuilt are skipped and counted per method with a reason (`jit replay reason ...`): delegate, reflection object, thread, native pointer field, multi-dimensional array, type not in `game-copy`, value parameter refused or null, generic method whose argument type is unknown.

Capture data is game state: `*.jitcap` and `probe-out/` are git-ignored, and only the tool and `native/monohost/jit-replay-fixture.json` (hand-made, no game data) are committed. Queue validation refuses a `MonoJit.CaptureFile` outside `probe-out/`.

**Dummy before the game (probe safety).** The same capture code runs in the host over the corpus (`--capture-corpus`), so the walk has met a real GC heap, real metadata and the real entry path. `TestMonoJitCapture` in the selftest covers fake metadata, the vtable identity check, the sampling arithmetic, a faulting sample and the file header.

Layout facts found on the way [verified in host dumps]: MonoGenericClass +0 class inst, +4 container; MonoGenericInst +4 argc, +8 pointer to the arguments; MonoClassField +0 type, +8 offset, +0xC name, +0x14 parent. The first guess (+4 name) gave `statics 0, skipped 32`.

| Host run (`node tools/jit-replay.ts`) | Inputs | Ran | Differ | Unstable | Skipped | Per-call verify |
|---|---:|---:|---:|---:|---:|---|
| Fixture: cycle, string, array, null, static, exception, delegate, missing type | 7 | 6 | 0 | 0 | 1 | ok 5, abandoned 1, MISMATCH 0 |
| Corpus capture, `--mode verify` | 4144 | 4005 | 0 | 0 | 139 | ok 187711, abandoned 518, MISMATCH 0 |
| Same, `--mode on` | 4144 | 4005 | 0 | 0 | 139 | off |
| Same, `--mode verify --lazy` | 4144 | 4005 | 0 | 0 | 139 | ok 187445, abandoned 262, MISMATCH 0 |
| Negative control: one bit flipped in a rebuilt object before the compiled arm (one-off build) | 3597, stopped on a fault | 3486 | 1467 | 0 | 110 | MISMATCH 0 |

[measured, host] Corpus capture: 212 cases, 0 differ with capture on; 7822 samples, 25105 objects, 2718 KB, 0 faults, 0 no-class, statics 579 (2 skipped); cost 19-25 ms in total, worst sample 1.1-1.3 ms. The game's heap is larger, so `CaptureBudgetUs` is what bounds it there.

Run: `node tools/harness/queue.ts tools/harness/queues/jit-capture.json --no-smoke` (BP, IP, SV, 10 min each), then for each file `node tools/jit-replay.ts --capture probe-out/jit-capture-bp.jitcap --out probe-out/jit-capture-bp.txt --summary` and `--run probe-out/jit-capture-bp.txt --mode verify`.

Skips by reason, rows of methods: reflection object 17, delegate 10, generic method with an unknown argument 9, native pointer field 9, type not in `game-copy` 9, argument not rebuilt 7, value blob shorter than the type 3 (a warning, the input still runs), static not rebuilt 1. Two harness bugs came out of this run and are fixed: a refused value type passed its native word as an object, and so did a `T` of an open generic method; both are skips now.

**Game capture replayed (2026-11-07, master e1ef04b3, ts3native 0.36.189, host only).** The three files of queue `jit-capture` (`docs/data/results/2026-11-07-queue-jit-capture.md`, BP, IP, SV, `Mode=1 Direct=0 Inline=0`), `--mode verify`:

| Capture | Inputs | Ran | Differ | Unstable | Skipped | Faulted | Per-call verify |
|---|---:|---:|---:|---:|---:|---:|---|
| BP | 4410 | 3799 | 0 | 0 | 611 | 0 | ok 1137668, abandoned 41, MISMATCH 0 |
| IP | 4409 | 3843 | 0 | 0 | 566 | 0 | ok 1164053, abandoned 40, MISMATCH 0 |
| SV | 4451 | 3849 | 0 | 0 | 602 | 0 | ok 1198866, abandoned 46, MISMATCH 0 |
| Merged, 23 method names, 455 rows | 5468 | 4789 | 0 | 0 | 679 | 0 | ok 1413277, abandoned 51, MISMATCH 0 |

[measured, host] No divergence between interpreter and compiled on real game inputs. Coverage has holes: of the 679 skipped, 511 are `Dictionary.TryGetValue` (32 of 543 ran: reflection objects, byrefs to reference types), and `Array.IndexOf` (16) and `RuntimeTypeHandle.get_Value` (15, null `this`) never ran. The merged capture cut 603423 references past the bounds, so many inputs are shallow. `Direct` and `Inline` calls are not in these samples. `--lazy` and `--mode on` were not repeated on game captures.

Limits: [inferred] a sample is the state at entry, so an object the method reaches past Depth arrives as null and may send the replay down a path the game did not take. It can make a false difference, never a false pass of that path; read cuts next to the result. Sampling at entry does not see Direct or Inline calls. A static the method writes is not compared unless an object it reaches is rebuilt. The game-side cost and the fault count are unproven until the queue runs.
## 18. Host gates: per-method proof offline, one game batch (2026-11-07, Rodney's decision)

**The rule.** Per-method correctness and gain are judged in the host: coverage bar, differential corpus (docs/207 §10), bench. The game gets one `tools/harness/queues/jit-batch.json` before each release: BP, IP, SV, ~15 min each, `Mode=2` with the full mode set (`Direct=1 DirectLeaf=0 Inline=2 Wide=1`), the same `logForbid` as `jit-verify`. No per-method game runs. Before a run, point its `asi` and `minCounters` at the batch build. Also in docs/TESTING.md §3.

**What the host cannot show.** Real GC (pauses, moving blocks of a real heap, the game's atomic allocations), game objects and their shapes, real threads (the Mono worker, sim yields, tasks), load order (which class is initialised when a method compiles, EA's call-site caches). That is why the batch stays.

**Coverage** (`MONOHOST_JITCOVER=1`, `tools/jit-coverage.ts`, `native/monohost/monohost-jitgate.cpp`).
- Each position calls `JitCoverHit` first. A branch has no call or throw in it, so the next position hit on the thread is its successor, also when the branch runs as an exit to EA's handler. With the switch off nothing is emitted [verified: source, every emit is behind the switch; not compared byte for byte].
- Hits count only inside `JitCorpusRun`, in the compiled arm, and add up over a lazy-off and a lazy-on run. Edges come from the IL of each row of `monojit-list.h` (SHA-256 as in the game), so a body the corpus never reached counts in the total. The k-th mint branch maps to the k-th IL branch; branch count and target order are checked per runtime method. A `box !T; brtrue` folds in a value-type instance, so those branches may drop out of the match [verified: 0 instances unmapped].
- Bar: 100% of edges, or a line with reason text in `docs/data/jit-coverage-reasons.csv`. An unlisted miss, a blank reason or a stale reason FAILs `--self-check`, which `selftest-all` runs on the checked-in csv.

| Coverage | Before | After 21 new cases (`JitCorpusGates.cs`) |
|---|---:|---:|
| Corpus cases, differ, unstable | 212, 0, 0 | 233, 0, 0 |
| Per-call verify in the corpus, lazy off | 33,715 ok, 0 mismatch | 42,783 ok, 0 mismatch |
| Edges of 58 bodies, hit | 137 of 315, 43.5% | **308 of 315, 97.8%** |
| Bodies not entered | 26 | 0 |
| Listed reasons | | 7 |

The 7 listed edges are dead code [verified: IL]: `SortedList(`2)+Enumerator.invalid` is never true (2), and 5 sides of a binary search over `flags & 0xCF00` that no value can take.

No listed body has clauses, so the clause-entry edge exists in the tool but was never exercised, and monohost does not report handler hits: a clause row would show as a miss until rm's clause table is mapped (`shortcut:` comment in the tool). Clause-bearing methods reach the game only under `Wide=1`.

Limits of the number:
- An edge counts as hit if **any** generic instance took it: only 52 of 189 branching runtime methods hit all their own edges. A hit is proof for that instance's code, not for every instantiation.
- These are IL branches. Slow paths inside a template (null, bounds, class-init exits) are not edges; `MONOHOST_JITCOUNT` sees those.
- A hit means the compiled path ran and the case hash matched. Inputs are the corpus's, not the game's (`feature/jit-arg-replay` adds real arguments).

**`RuntimeTypeHandle.get_Value`, open.** The transform folds a direct call into a field load [verified: mint of `Type.GetTypeFromHandle`, `ldarga` + `ldfld.i4`], so the host enters this body only by reflection, once per pass. The game's sampler books 7.4 ms/s to the name in all three worlds. Those two do not fit, and the sampler's cause is not known. This row gets no modelled gain.

**Bench** (`MONOHOST_JITBENCH=1`, `tools/jit-bench.ts`, `docs/data/jit-bench.csv`). Self ns per call over whole corpus passes, interpreter vs compiled, pinned, 21 passes per arm with the order swapped each pass; median and interquartile spread. Stamps sit at word 0 and each `ret*`, in the body for the compiled arm and in a stamp + EA exit stub for the interpreter arm. A stamp pair subtracts listed callees; an unlisted callee counts as self in both arms, and a frame an exception unwinds is dropped. The C-loop floor of a stamp pair (7.9 ns) comes off both arms; `JitEnter` and the stub's few instructions do not, so ratios of bodies under ~10 ns mean little.

**The ms/s are modelled, not measured.** No game run has calls per method. Source for game weight: `docs/data/jit-hot.csv` `ms_s_mean`, sampled self time of `sim-budget-v024` and `release-validate-2026-10-28` (docs/207 §1), 10 days old, before the compiler existed. Gain = self ms/s x (1 - new / old), weighted by host self time inside a shared key; implied calls/s = self ms/s / old ns. Direct=0, EA's call path, corpus inputs.

| | Modelled [not measured] |
|---|---:|
| Total | **20.3 ms/s of 58.6 sampled** in the committed csv |
| Same method, other runs | ~16 to ~21 ms/s: a game chain ran on the same machine, ~44% CPU |
| Top rows | `Enumerator.MoveNext` 3.2, `CASUtils` 2.4, `Enum.Equals` 1.7, `TryGetValue` 1.7, `List.get_Item` 1.6 |
| Bodies under 16 host calls a pass | row kept, ns are thin; a key below 16 gets no gain (`get_Value`) |

Read this as an upper-side estimate of the shape. 38 of 58 bodies see under 100 calls a pass, and game inputs differ. Rerun on a quiet machine before quoting a number anywhere.

**Direct calls** (`call` rows, the fixed bench cases, lazy frames off vs on, ns per iteration; best of 7, one run each, same load): `JitLz` 48.5 -> 26.3, `JitLzChain` 69.7 -> 36.7, `JitLzVirt` 26.1 -> 14.4. Interpreted twins 65.0, 87.4, 34.5. `JitLzChain` read 119.6 in one earlier run, so these are loud. The cost of a materialised frame (`JitLazyMat`) has no bench case and is not shown.
## 19. Capture holes closed: Direct and Inline sampled, three replay root causes (2026-11-08, ts3native 0.36.192, asi `jitcap1108`, host only)

The §17 replay ran 4789 of 5468 game inputs. `Dictionary.TryGetValue` ran 32 of 543, `Array.IndexOf` and `RuntimeTypeHandle.get_Value` never ran, and calls through `Direct=1` and `Inline=2` were never sampled. Each hole has its own cause.

**`TryGetValue`: EA's reflection invoke drops the byref of a generic-instance class [verified: host fixture].** `JitReplay.Ready` refused every byref to a reference type ("byref to a reference type") after 50 interpreter faults (a write to 0) in the first run. The fixture shows the real limit. Through `MethodBase.Invoke`, `JitReplayBag.TryGet(int, out List<int>)` faults the interpreted arm, while `TryNode(int, out JitReplayNode)` runs. Old Mono's invoke passes a byref to a generic-instance class as its value (null) and not the slot's address, so the callee's store to the out slot writes to 0. 427 of the captured `TryGetValue` slots have a generic-instance value type. The fix: such inputs run through a closed delegate (`JrOut1`/`JrOut2`, called by `Out1`/`Out2` through reflection with no byref parameter). That is a normal call, so the out slot is a real local, and the out value goes back into the argument array, which the arm hash covers. The shape is limited to an instance method whose last of at most two parameters is the byref (`shortcut:` comment in `OutVia`).

A second fault came out of the first full run with this fix (merged input 1703, `Dictionary.GetPrev`, a host fault while rebuilding). A byref-to-reference slot whose old word named no rebuilt object passed that game address as an object. `JrValue` now returns null for any slot word that is not a rebuilt object.

**`Array.IndexOf`: arguments decoded against the open generic method [verified: source + fixture].** `JitReplay.Arg` decoded each word by the parameter type of the method as resolved by token, which is `IndexOf<T>` with an open `T`. An open `T` is no value type, so the `int` value word was kept as an object (null), and `Ready` then refused it ("value argument not in the capture", 16 of 16). `Arg` now keeps the raw words, and `Ready` decodes them after it has closed the method from the array argument. Fixture case `JitReplayFix.Find<T>(T[], T)`.

**`get_Value`: a value-type `this` in no frame and no GC block [verified: capture data, `this` = 0x4423xxxx in all 15 samples].** EA calls this getter with `this` pointing at C-stack memory (a native invoke's copy of the handle). The capture only knew frames and GC blocks, so it counted `outside` and the replay saw "null this". `JcRoot` now takes the value size of a value-type `this` (rm flag 0x400, class instance size less the 4-byte header) and stores those bytes as a blob. The replay builds the handle box itself (`ArgBlob`), because `Refuse` says "native pointer field" for the handle's IntPtr. The getter only reads that word, so the game's MonoType* rides along unchanged (`shortcut:` for this one getter). Fixture case `JitReplayPt.Dot` (a value-type `this` blob). The existing game captures have no bytes for this `this`, so `get_Value` stays at 0 of 15 until the queue runs again.

**Direct and Inline sampled (`monojit-capture.h`).** While `Capture=1` is on at compile time, the compiler emits two calls. The first instruction of each lazy body is `call JcLazyStub`, which reads `B_RM`, `B_THIS` and `B_ARGS` from the call site's block. An inlined site, after its tests and before the body, emits `push rm; call [g_jcInlineStubP]`; the stub reads this and the arguments from the caller's eval stack. Both stubs keep every register (pushad/popad). With capture off nothing is emitted [verified: selftest, the first byte of a lazy body compiled with capture off is not `E8`]. The per-method first-N and reservoir counts cover all three paths. File version 2 adds a path word after the slot: 0 JitEnter, 1 lazy body, 2 inlined. `tools/jit-replay.ts` reads versions 1 and 2, and `--summary` splits samples by path. The `jit capture:` line counts "in lazy bodies" and "inlined". The startup line names the sampled paths.

Tests: `TestMonoJitCaptureVia` in the native selftest covers a real lazy call and a real inlined getter compiled with capture on (result correct, so registers kept; one sample per path, tagged in the file), a null `this` that takes EA's call before the inline sample, and the value-type `this` blob. The `jit-replay.ts` self-check covers the path word round trip, the summary split, a version 1 file, and that the path does not change the replay input. The fixture has 4 new cases (11 inputs, 10 ran, every expected value matched; `Missing` is skipped by design).

| Host run | Inputs | Ran | Differ | Unstable | Skipped | Faulted | Per-call verify |
|---|---:|---:|---:|---:|---:|---:|---|
| Merged BP+IP+SV game capture, master 0.36.189 (§17) | 5468 | 4789 | 0 | 0 | 679 | 0 | ok 1413277, MISMATCH 0 |
| Same capture, this branch | 5468 | **5282** | 0 | 0 | 186 | 0 | ok 1413770, abandoned 51, MISMATCH 0 |
| of which `TryGetValue` / `IndexOf` / `get_Value` | 543 / 16 / 15 | 509 / 16 / 0 | 0 | 0 | 34 / 0 / 15 | 0 | |
| Corpus capture with `--lazy --inline 2` (dummy for the new paths): 8886 samples, 2120 lazy, 253 inlined, 0 faults; replay `--list corpus` | 4887 | 4732 | 0 | 0 | 155 | 0 | ok 264388, MISMATCH 0 |
| Same, replay `--lazy` | 4887 | 4732 | 0 | 0 | 155 | 0 | ok 263070, MISMATCH 0 |

[measured, host] Corpus under capture with lazy and inline: 233 cases, 0 differ; capture cost 21.8 ms in total, worst sample 1.0 ms. The 34 `TryGetValue` inputs still skipped hold reflection objects (`Type` keys), delegates, or an argument that cannot be rebuilt. The game-side cost of sampling lazy and inlined calls is unproven until the queue runs.

Next: queue `jit-capture` on `jitcap1108` with `Mode=1 Direct=1 DirectLeaf=0 Inline=2 Wide=1` (BP, IP, SV, 10 min each, ts3fixes `g2pair1108`, same pass rules), then `--summary` (the lazy and inline columns must be > 0) and `--run --mode verify` on the merged files.

## 20. Four verdicts: ClauseLazy vs #362, C2b, `get_Value`, A3 (2026-11-12, branch `analysis/monojit-1112`, static and existing game data only, no build or run)

Data: the three C0 count runs on master1108 (ts3native 0.36.211, `Count=1 VtOps=1 CallExit=1 NewVt=1 Direct=1 Inline=2 Wide=1`, `FastCopy=0 Ldsflda=0`, 127 list rows), logs `probe-out/queue/correctness-1109-2026-11-08-<w>-jit-exits-count-*-ts3native.log` and their `-native.json`. The counters are cumulative, so rates below are interval #3 minus interval #2 over 300 s. Sampler: the six `managed.jsonl` files behind `docs/data/jit-hot.csv` (docs/207 §1). Ledger §475.

### 20.0 Two facts the four answers rest on

**The 2 MB code region is full in every game run since 11-08 [verified: counters; effect inferred, I-475-1].** `jitCodeKB` reads 2033-2045 of 2048 in every count, batch, capture and bundle job since the lazy bodies went in, and `jitMethods` is the same number in all three worlds of each arm: 841 (count), 1034 (c3g capture), 1050 (c3g bundle on), 1142 (c3g bundle off), 1118 (jit-batch). On 11-06/07 it was 734-752 KB for ~1082 methods. A world-independent method count is a capacity stop, not traffic. `JitCompile` refuses when the space left is under `need = ((lazy ? 768 : 256) + (Count ? 256 : 0)) * positions + 2 * words + 512` (`native/ts3native/monojit.cpp` ~1157), so near the end even a 3-position body (~3.6 KB with Count) is refused with "code region full". The log cannot show which methods: `jit: ... refused` shares the 256-line cap with `jit: compiled`, and all 256 lines are compile lines. So the C0 census covers the first 841 bodies EA transformed, not the list. Sampled self time of list keys with no `jitcalls` row: 14.0 ms/s, of which 12.6 are the binder-bound `Enum.Equals` and `Type.GetTypeFromHandle` (next paragraph); the rest (~1.4, mostly `ObjectGuid.op_Inequality`) is never run, inlined or refused. **Fix (small, ready):** log refusals under their own cap with the reason, and a larger region or a tighter `need` bound. Each MB of region is 1 MB of address space, so size it from the refusal log, not by guess.

**Six game-list keys are EA native bodies (binder), so their compiled mint never runs [verified: data; mechanism from docs/130 D17].** `docs/data/binder-bodies.csv` binds `Enum.Equals` (row 194), `Type.GetTypeFromHandle` (338), `SimClock.get_CurrentTicks` (359), the three `SimClock.ElapsedTime` overloads (92, 966, 978), `SimClock.ConvertFromTicks` and one `Math.Min` overload (match by class + method; overloads not checked). The interpreter calls a bound body at `RuntimeMethod+0x24` (`0xE5925F` -> `0xE525C0`, docs/130 line 74) instead of the mint. In all three count runs `GetTypeFromHandle` and `Enum.Equals` compile (compile lines 1 and 176, both with a lazy body) and still have **no** `jitcalls` row, and no `SimClock` name appears anywhere in any count, batch or c3g log.

### 20.1 Q1: #362 vs ClauseLazy (`feature/monojit-arraycopy5`)

**Verdict: #362's no-go stands for ClauseLazy. Keep `ClauseLazy=0`, do not merge the emitter part; part of #362's evidence is wrong and is corrected here.**
- Same mechanism, different method set. #362's ceiling (0.75-0.80 ms/s, all call exits in the C0 census) counts calls from bodies compiled in the C0 build. ClauseLazy gives a lazy body to the clause methods themselves (host: 30 methods, the 5-arg `Array.Copy` and `DesireCurve.GetYFromX` among them), so its gain is the frame write saved on calls **into** them from compiled callers. That set is not in the C0 build at all.
- #362's "`Array.Copy` has no row in the game census, so 0.0 ms/s in game" is not supported. `Array.Copy` is not on master's game list (`monojit-list.h` has no row; arraycopy5 adds four), so it cannot have a row, and the region was full anyway (§20.0). It is hot: 3.07 ms/s sampled self time in all three worlds (`jit-hot.csv` row 13) [verified]. It is not a binder method [verified: no binder-bodies row].
- Most of that time is not what a lazy body touches [inferred, I-475-6]. The sampler books the copy itself (the `FastCopy` icall) to `Array.Copy`'s frame (docs/207 §5). In game its hot callers are interpreted (`Array.Resize` 4.8 ms/s and `List.set_Capacity` are not on the game list), and those calls pay EA's frame with or without ClauseLazy. Only calls from compiled callers (mainly the 3-arg overload into the 5-arg one, once the list rows are in) gain the lazy-call difference, 22-33 ns per call (host bench `JitLz` 48.5 -> 26.3, `JitLzChain` 69.7 -> 36.7, §18). Under 1 ms/s needs fewer than ~30k such calls/s. The host's whole `Array.Copy` modelled gain was 0.14 ms/s (C3 table).
- Worth a build: **no** (est. <= 0.3 ms/s, at most ~0.75; the bar is 1 ms/s to reopen and 3-5 for ON). Keep the branch's host-side pieces that stand alone (KCL corpus cases, the verifier label fix 61b4d679, `MONOHOST_JITDIRECTLEAF`) for a later merge. Add the `Array.Copy` list rows only after the code region fix, because today every new row pushes out another body. **Reopen:** a count run with the region fixed and `Array.Copy` on the list shows calls into the 5-arg `Copy` from compiled bodies at >= 30k/s on some world.

### 20.2 Q2: C2b, `SimClock.get_CurrentTicks` without an ldsflda template

**Verdict: neither loss nor gain in game; take it off the game list (with the other binder-bound rows). No ldsflda work for it.**
- Game: `get_CurrentTicks` is an EA native body (binder row 359, `0xE057C0`, reader, no managed re-entry), and so are its callers `ElapsedTime` (3) and `ElapsedTimeInMinutes` (row 598). The C0 runs had it on the list (127 rows, `Ldsflda=0`), and no `SimClock` method has a `jitcalls`, `jitexit` or `jitbail` row in BP, IP or SV [verified]. `jit-hot.csv` rows 162, 173 and 174 have 0 samples. The "1.22 ldsflda exits per call" exists only in the host, where the mint runs.
- Host: the exit was the width-0 `K_LDSFLDA` row, already fixed by `[MonoJit] Ldsflda` (handoff C-int: 74 exits in 71 calls -> none). So with `Ldsflda=1` it is not a loss in the host either.
- Action (~30 min, ready): `tools/jit-hot.ts --emit-native` skips rows whose full signature is in `docs/data/binder-bodies.csv`, plus a self-check. That drops `get_CurrentTicks`, `ConvertFromTicks`, `ElapsedTime` x3, `GetTypeFromHandle` and `Enum.Equals` from `monojit-list.h`; check `Math.Min` per overload. The host list keeps them for the corpus. Gain 0 ms/s direct, plus region space for bodies that do run. The jit-hot verdicts for those rows become `binder: native body`.

### 20.3 Q3: `RuntimeTypeHandle.get_Value`, 7.4 ms/s the host cannot reproduce

**Verdict: different call shape. Every game call is a native-to-managed call (`ScriptHost_NativeToManaged 0xD8A8E0`) from EA's native body of `Type.GetTypeFromHandle`, and the sampler books that round trip to `get_Value`. Compiling the body cannot recover it; a binder patch can (est. 5-7 ms/s per world). Worth a build.**
- The caller [verified: disassembly of the Steam 1.67 exe, `game-copy/bin/TS3W.exe`]. EA's body for `Type.GetTypeFromHandle` at `0xDB0940` (adapter `0xDB0A00`) memsets its locals and calls `0xD8A8E0(method = [0x11EF06C], this = &handle on the C stack ([ebp+0x18]), 0, &result [ebp-0x44], exc [ebp+0xC], ...)` at `0xDB0976`. Then it passes the result to `[0x11EF074]`, a native function taken from init reference 8. The binder init at `0xDB08E1` fills `[0x11EF06C]` from init reference 3. `0xDB0970` is the only reader of that global. The IL of `GetTypeFromHandle` is `ldarga handle; call get_Value; call internal_from_handle; ret`, so reference 3 is `get_Value` and reference 8 is `internal_from_handle` [inferred from that shape, I-475-3]. It matches §19's capture: `this` = 0x4423xxxx, C-stack memory, in 15 of 15 samples.
- The sampler [verified: the 6 pooled files]: all 44 `get_Value` samples have a stack depth of exactly 1, with no managed caller. `0xD8A8E0` runs the callee on a fresh interpreter state (docs/re/native-symbols.md), so the probe's frame walk (`ThreadContext+8` chain, `tools/probe/agent.js`) sees only `get_Value`. The other depth-1 leaves are also reflection (`MonoType.get_Assembly` 9, `Object.GetType` 3, `Type.IsAssignableFrom` 2).
- The rate [verified: count]: `get_Value` runs 11,203 (BP), 10,888 (IP) and 15,215 (SV) calls/s, each through its compiled normal body: 3 positions, exactly one exit per call (`ret`), 0 bails, 0 slow paths. 7.4 ms/s / 11.2k = **~660 ns per call** [inferred, I-475-2]. The body is 3 mint words (under ~20 ns compiled), so over 95% of the booked time is the n2m entry and exit around it.
- Why the host shows none of it: the host runs `GetTypeFromHandle` as mint, where the transform folds the call into `ldarga` + `ldfld.i4` (§18), so it never makes this n2m call.
- Ruled out: inlined icall (the body has no call); GC or lock wait as the main cause (the count shows no slow path; a wait inside the n2m window cannot be split from the entry cost with this data, and it is removed with it either way).
- **Recovery: `[Binder] TypeHandleDirect` (name open, default 0).** At `0xDB095E`-`0xDB097D` (32 bytes, today `56 57 8d 55 bc 52 8d 4d 18 8b c1 6a 00 89 4d b4 8b 0d 6c f0 1e 01 50 51 e8 65 9f fd ff 83 c4 24`), check the bytes and write `mov eax,[ebp+0x18]; mov [ebp-0x44],eax` + nops. The high result word is already 0 from the memset, `[edi]` (exception out) is zeroed by the adapter (`0xDB0A0A`), and `esi`/`edi` are loaded before the window. `get_Value` is `return value;` (IL 7 bytes, no side effects, `this` is never null here), so the result is identical. A bonus: the body is worker-reachable (`v0` in binder-bodies.csv) and the worker must never enter through `0xD8A8E0` (docs/56), so the patch also takes one n2m entry out of the worker closure [inferred].
- Estimate: `get_Value`'s 7.4 ms/s (44 samples, about +-15%) goes away. Part of `GetTypeFromHandle`'s own 2.77 may go too, if `0xD8A8E0`'s prologue runs before the new frame is visible. **~5-7 ms/s per world [inferred]**, above the 3-5 ms/s ON bar if a timing run confirms it. Effort ~1-2 h (patch with a byte check, ini read-back on the OK line, native selftest on the window bytes), then one sampler A/B per world (10 min arms) before it can be ON. Check S3SS / Apex sources for a patch of `0xDB0940` before building (the check-existing-fixes rule; nothing in this repo touches it).
- It belongs to us, not Apex (script binding / Mono lane). It is not a compiler piece, so it does not touch the call-exit work.

### 20.4 Q4: A3, the depth-slot bail

**What it is [verified: host A3 + EA disassembly in the handoff].** A lazy call site needs a free `MonoInvocation` slot at `depth + level + 1` in the current `ves_exec`'s frame-record array (`depth + 1 (+ pending) >= [ebp-0x30]` = bail). EA makes those slots with an `alloca(0x28)` on `ves_exec`'s C stack the first time a call reaches that depth (`0xE562E1`-`0xE5631F`). A compiled body cannot alloca in `ves_exec`'s frame, so the site takes EA's normal call, which makes the slot. Every later call at that depth in the same `ves_exec` is lazy.

**How often in game [verified: count, interval #2 -> #3].**

| | BP | IP | SV |
|---|---|---|---|
| depth-slot bails/s | 3,227 | 4,922 | 3,936 |
| all bails/s (no lazy body / depth / arena) | 23,695 | 28,309 | 22,154 |
| compiled calls/s | 137,300 | 153,575 | 146,359 |
| sites | 12 | 12 | 12 |
| top sites (share of depth bails) | GetSlot w9 25%, GetPrev w24 23%, TryGetValue w9 23%, IEquatable GetHashCode w4 14% | 29%, 27%, 27%, 8% | 27%, 24%, 24%, 11% |
| depth bails per call of the method | GetPrev 0.08, GetSlot 0.07, TryGetValue 0.11 | 0.11, 0.11, 0.16 | 0.10, 0.09, 0.16 |

All the volume is in Dictionary lookups whose `ves_exec` is fresh. Each runtime invoke or n2m entry (2,844 call sites of `0xD8A8E0` in the exe, rough byte scan) starts with no slots, so the first lazy call at each depth bails [inferred, the host A3 reason, now in game].

**Cost [inferred, I-475-5].** A bail costs EA's call minus a lazy call: 22-33 ns (host bench, loud), so 3.2-4.9k/s x 22-33 ns = **0.07-0.16 ms/s**.

**Options.**
1. Leave it (the host A3 decision).
2. Fill on the normal path, EA's alloca clone in every normal-body site (built and measured in the host: depth bails 1559 -> 21, census unchanged, +5 instructions on each normal-body site). In game that is 5 instructions on ~140k compiled calls/s, ~0.1-0.3 ms/s spent to save <= 0.16.
3. Fill on the bail path from a side pool of `MonoInvocation` cells. `[rec]` may point anywhere, but the cell has to live as long as the `ves_exec` frame and survive yield save / resume (docs/130 frame save copies these records), which is a lifetime problem in the riskiest code.
4. Pre-fill slots in `JitEnter` at a fresh `ves_exec`: the same alloca problem as 3, since `JitEnter` runs above `ves_exec`'s frame.

**Recommended: option 1. No build.** A docs/51 entry for the cost model is in §475; the counts are measured. **Reopen** if a count run shows depth-slot bails above ~30k/s on some world (~1 ms/s at 33 ns).

### 20.5 Summary

| # | Decision | Est. ms/s | Effort | Build? |
|---|---|---|---|---|
| Q1 ClauseLazy | #362 stands: ClauseLazy stays 0, emitter not merged; "Array.Copy 0.0 in game" corrected (not on the list, region full; 3.07 ms/s sampled) | <= 0.3 (cap ~0.75) | - | no; reopen on a count after the region fix |
| Q2 C2b | binder-bound in game (never runs); drop binder rows from the game list, no ldsflda work for it | 0 (frees region) | ~30 min | yes, small (list filter) |
| Q3 get_Value | n2m round trip from `GetTypeFromHandle`'s EA body (0xDB0976); patch the call to a direct read | ~5-7 per world | ~1-2 h + sampler A/B | **yes** |
| Q4 A3 | keep "no slot fill"; game rate 3.2-4.9k/s | 0.07-0.16 | - | no |
| 20.0 region | the 2 MB code region is full in every run since 11-08; log refusals, then size | enables the rest | ~30-60 min | yes, before any new list rows |

### 20.6 Built 2026-11-12: R1, C2c, E1 (branch `feature/monojit-r1-c2c-e1`, ts3native 0.36.245, counters v188) — built, NOT RUN

Host and native self-tests only (Rodney: no game runs for this build). Nothing below is measured in game yet.

**R1, code region [built, NOT RUN].**
- Sizing [measured, host + existing game data]. Host replay of the three c3g captures (`probe-out/jit-capture-<w>-c3g.txt`, game list keys, `--lazy` + `VtOps=1,CallExit=1,NewVt=1,Inline=2,Wide=1`, Count off): 401-419 bodies compiled, 815-840 KB, **2.00-2.03 KB a body, 23 positions a body**; the 11-06 58-row list on the same inputs: 378-393 bodies, 2.08-2.10 KB. The replay only reaches captured methods, so the host body count is a floor. Game: the same 58-row list compiled 1081-1085 bodies in 734-751 KB with `Direct=0` (11-06, not full) and filled 2045 KB at 1092-1118 bodies with `Direct=2`; the 127-row list stops at 841 (Count=1, 2.49 KB a body in its first 256 compile lines, 20.8 positions) to 1142 bodies (Count=0, 1.79 KB). Estimate for the whole list in game [inferred]: ~1150-1250 bodies x 2.0 KB = **~2.3-2.5 MB, ~3.1 MB with Count=1**.
- `[MonoJit] CodeKB` (1024-16384, **default 4096**). Cost: **+2 MB of address space** over the old fixed 2 MB, plus +0.6 MB of tables (ip table 2^15 -> 2^16 entries, +512 KB; position arrays 24576 -> 49152, +96 KB), only with `[MonoJit] Enabled=1` (off in the release ini). The table had to grow with it: at ~21 positions a body, 24576 positions hold ~1170 bodies, so a larger region alone would have stopped at "position table full". `JitEnter`'s mask is now `0xFFFF`.
- Refusal log: refusals no longer share the compile lines' 256-line cap. Each reason (string) gets a slot (16 slots) with its own count; the first 32 names of each reason are logged (`jit: <name> refused: <why> (#k of this reason)`), then one "names not logged from here on" line. A summary line `jit: refusals by reason (first tick|save): <reason> <n>, ...; code region <used> of <CodeKB> KB, <methods> methods, <n> refused "code region full" asking <KB> KB` at the first Simulate tick and after each save when the count moved. Counters v188 (0x6008): `jitFullN` (bodies refused with "code region full"), `jitFullBytes` (the `need` bound they asked for, summed), `jitRegionKB`. The OK line prints `code region <KB> KB (CodeKB)`.
- Selftest `TestMonoJit`: every reason has its own slot, 35 "code region full" refusals past the 32-name cap all counted (`jitFullN`, `jitFullBytes`, the slot), room again = compiled, the CodeKB clamp.
- Next: `tools/harness/queues/jit-region.json` (prepared, not run): BP 5 min smoke at `CodeKB=4096`, then the same at 2048 for the contrast. Pass: OK line reads `code region 4096 KB (CodeKB)`, the first-tick summary line, `jitRegionKB` 4096, no MISMATCH, `jitFaults` 0. Read: `jitFullN` 0 and `jitCodeKB` under 4096 at 4096; `jitMethods` world-dependent once the BP/IP/SV count arms rerun (I-475-1 pass rule).

**C2c, binder-bound rows off the game list [built, NOT RUN].** `tools/jit-hot.ts --emit-native` reads `docs/data/binder-bodies.csv` (`managed_signature`) and leaves out every overload whose signature EA binds to a native body; a body shared with an unbound overload stays. Regenerated `monojit-list.h`: 127 -> **115 bodies**, exactly 12 rows gone and no other drift: `Enum.Equals`, `Type.GetTypeFromHandle`, `SimClock.ElapsedTime` x3, `SimClock.get_CurrentTicks`, `SimClock.ConvertFromTicks`, and **all five `Math.Min` bodies**. Correction to §20.0: not one `Math.Min` overload but all 11 (`int`, `long`, `float`, `double`, `byte`, `sbyte`, `short`, `ushort`, `uint`, `ulong`, `Decimal`; binder rows 8, 58, 274, 288, 364, 562, 569, 613, 839, 871, 894) are binder-bound [verified: binder-bodies.csv], which fits the game data (`Math.Min` compiled in all three C0 count runs with a lazy body, no `jitcalls` row). Self-check: signatures parsed from quoted csv rows, a bound-only body dropped, a shared body kept, all bound = no row. The host list (`kJitList`) and the corpus keep them; `jit-hot.csv` verdicts are unchanged (the filter sits in `--emit-native`).

**E1, `[Binder] TypeHandleDirect` [built, NOT RUN].** `native/ts3native/bindertd.cpp`, default 0 (repo ini `[Binder] TypeHandleDirect=0`), applied at DllMain after `[MonoSigGuard]`.
- Patch at `0xDB095E`-`0xDB097D` (32 bytes): `8B 45 18` `mov eax,[ebp+18h]`; `89 45 BC` `mov [ebp-44h],eax`; `83 C4 0C` `add esp,0Ch`; `EB 15` `jmp 0xDB097E`; 21 x `90`. Correction to §20.3: a plain read + nops would unbalance the stack. The window's `add esp,24h` pops nine dwords: its own six pushes **and** the three memset arguments pushed before `0xDB0953` (cdecl, cleaned late) [verified: disassembly]. Without the `add esp,0Ch`, `pop edi` / `pop esi` at `0xDB09F6` would read the memset arguments.
- Check before the write: the 86 bytes `0xDB0940`-`0xDB0995` (frame, memset call, the window, the `cmp [edi],0` / `jne` and the result reads after it) must be EA's; else `SKIP binder type handle direct: bytes at 0x00DB0940 differ`. `OK   binder type handle direct (docs/537 §20.3) ...` and `tdOn` = 1 when installed. Registered in `docs/68` (hook-sites `--check` ok); `tools/patch-bytes-check.ts` reads the expectation (`Matches(kTdBody)` ok on the Steam exe).
- Same result for every handle [verified: selftest `TestBinderTypeHandle`]: EA's 32 bytes (with `[0x11EF06C]` and the `0xD8A8E0` call pointed at a stand-in that does what `get_Value` does, `result = *(IntPtr*)this`) and ours run in a copy of `0xDB0940`'s frame (sub esp,8Ch, push esi / edi, memset of `[ebp-4Ch]` with its arguments left on the stack) for 1024 handles (0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, heap-like and random): equal result words (`[ebp-44h]`, high word 0), esp balanced in both, esi / edi kept, exception cell untouched, no stand-in call from ours. Also: the patch writer refuses other bytes, writes only the window, and refuses a second time. What the stand-in cannot show: that `[0x11EF06C]` is `get_Value` (I-475-3, inferred from the IL shape and the only reader) and that `0xD8A8E0` has no other effect the game needs (its own interpreter state and exception frame; `get_Value` makes no call and cannot throw).
- Other owners: none found. `docs/24`, `docs/148`, `docs/254`, the S3SS source (`game-copy/s3ss-src`) and the Apex 3.0.0 / 3.1.0 sources and binaries have no `0xDB09xx` address, `GetTypeFromHandle`, `RuntimeTypeHandle` or `0x11EF06C` [verified: grep and a byte scan for every address `0xDB0940`-`0xDB09FF`].
- Next: `tools/harness/queues/binder-typehandle.json` (prepared, not run): BP / IP / SV, off and on, 10 min each, release ini, samplers on, order off/on, on/off, off/on. Pass rule from I-475-2: `get_Value` gone from the sampler and the Mono bucket down >= 4 ms/s per world. If it passes at 3-5 ms/s+ with no script errors it is a default-ON candidate (clear-gain rule); until then it stays 0.

## 21. Call exits from the C0 game count: scope, and why nothing was built (2026-11-12, branch `feature/monojit-callexits`, static + host, no game run)

C4 (allocation exits: box, newarr, newobj) stays parked since 2026-11-10. This section ranks the call exits the C0 game count still shows (Dalexlol PR #355 census, his PR #362, the three C0 count logs of §20, which comes from branch `analysis/monojit-1112`) and checks which shapes are safe to build. Rodney's compiler rule applies: per-method correctness and the gain proven in the host, no stop rule on small gains. Ledger §476.

**All C0 numbers are a lower bound** (§20.0, I-475-1): the 2 MB code region was full in the count runs, so only the first 841 bodies compiled. Exits in bodies past the cap were never counted, and a method missing from the table below may simply not have compiled.

### 21.1 Scope, ranked by ms/s held back

`est_ms_s_behind` of the game census (BP, non-ret exits) [measured, census model]; the bail split is the last interval of `game-count-bp.log` [measured, game count]. The "12.9 ms/s" for `GetPrev` and "5.7" for `MoveNext` quoted in the task are the whole method's sampled self time (docs/207), not the cost of the exit.

| Rank | Method | Exit | ms/s held back (BP) | What holds it back | Verdict |
|---:|---|---|---:|---|---|
| 1 | `List.get_Item` | ret.vt | 0.162 | frame teardown at ret | not a call exit (B3 piece) |
| 2 | `IEquatableOfTEqualityComparer.GetHashCode` | con.callvirt (ED, ip 4) | 0.156 | "no lazy body": 6.76 M lazy + 3.07 M normal bails/run | callee is an EA native body (21.2): needs a new shape |
| 3 | `Dictionary.GetPrev` | callvirt (EC, ip 59/24/55/20) | 0.154 | "no lazy body": 2.2 + 0.93 + 0.64 + 0.44 M | callee class unknown in the game; a `DefaultComparer` of a value `T` boxes (C4) |
| 4 | `List.GrowIfNeeded` | call | 0.124 | "no lazy body" (ip 27 / 23, 0.39 + 0.37 M) | callee allocates (`Array.Resize`, C4) |
| 5 | `List.Enumerator.MoveNext` | ldelema | 0.104 | value-type element address | not a call exit (vt-ops piece) |
| 6 | `IEquatableOfTEqualityComparer.GetHashCode` | con.call (EB) | 0.085 | struct `T.GetHashCode` | struct callees not identified |
| 7 | `MoveNext` | call | 0.052 | "no lazy body" (ip 1 / 6, 0.52 + 0.48 M) | `VerifyState` throws (exception path) |
| 8 | `Array.IndexOf` | callvirt | 0.052 | `EqualityComparer.Default` per call | class-init path |
| 9 | `Dictionary.GetSlot` | call | 0.028 | "depth slot" (ip 9, 0.9 M) | not a compile shape (§20.4) |
| 10 | `IEquatableOfTEqualityComparer.Equals` | con.callvirt (ED, ip 17) | 0.025 | "no lazy body": 9.32 M lazy + 1.76 M normal | as rank 2 |
| 11 | `Dictionary.TryGetValue` | call | 0.018 | "depth slot" (ip 9, 0.7 M) | as rank 9 |
| - | ret (all) | ret | 4.39, of which `RuntimeTypeHandle.get_Value` 2.47 | frame teardown | B3 piece; `get_Value` see §20.3 |

**Bail split** (BP, last interval): 32.87 M bails; "no lazy body" 28.27 M (86%), "depth slot" 3.27 M, "arena full" 1.32 M, "cache class" ~0. IP and SV look the same.

**C6 ("cache class") is not worth a build** [measured, game count]: ~0 here, 239 of 50 M in Dalexlol's census (max 0.024%, `GetPrev` site 55).

### 21.2 The two biggest "no lazy body" sites call EA native bodies [inferred, ledger I-476-1]

Ranks 2 and 10 are the string-keyed comparer: `T.GetHashCode()` and `IEquatable<T>.Equals(T)` through con.callvirt. Together they are 20.9 M of the 28.27 M "no lazy body" bails per BP run (74%). In the host, listing `String.GetHashCode` and `String.Equals` took those bails to 0 (ip 4: 3047 -> 0, ip 17: 936 -> 0) with corpus, verify and the three c3g game-capture replays clean (4307 / 4235 / 4275 inputs ran, 0 differ, 0 faulted, MISMATCH 0).

That does not carry over to the game. `docs/data/binder-bodies.csv` binds `String::GetHashCode()`, `Equals(string)`, `Equals(object)`, `Equals(string,string)` and `op_Equality` to EA native code (`RuntimeMethod+0x24 != 0`; the interpreter calls them through `0xE5925F` -> `0xE525C0`, docs/130). `JitGuardFill` gives a target with a native body no lazy entry (`monojit.cpp`, "no this, or an EA native body"), so the guarded site bails with "no lazy body" whether or not `String` is on the list. The host has no binder, so it cannot show this. It also explains why the sampler has no `String.GetHashCode` samples (native code is not mint) while `UInt64.GetHashCode` has 9.

So these 20.9 M bails are calls into EA C++ that already run natively, and listing `String` gains nothing (native-body keys get no rows, §20.0). A static `String.Equals(string,string)` direct site would even run our mint instead of EA's body.

**What would remove them (next shape, not built):** a guarded site whose filled target has a native body calls EA's bound body directly (the `0xE525C0` path) instead of exiting. This saves the exit + EA's call dispatch, not the callee's work. It needs `0xE525C0`'s argument convention and a host stand-in for the binder before it can be tested; estimate one agent study (~1 h), then a build.

### 21.3 Built and dropped: `StrKeys`

A first build behind `[MonoJit] StrKeys` (default 0) did three things: the two `String` rows, `RuntimeHelpers.get_OffsetToStringData` inline (EA's icall `0x927420` is `mov eax, 8; ret`, slot `0x10B5E0C`, bytes compared at install), and leave / leave.s natively in a method without clauses. It passed the native selftest, the corpus (261 cases, 0 differ, per-call MISMATCH 0), the fixture replay and the three c3g replays. Without the `String` rows, a host count shows **0** native leave sites and **0** `OffsetToStringData` sites on the compile list (pure icall sites 24 with the key on and off). With the rows gone as native-body keys, the other two shapes have no target, so the build was removed again. Kept for a later piece [verified, disassembly; ledger I-476-2]: EA's leave `0xE59315` + `handle_finally` `0xE54198` with no clause sets `esi` to the stack base, `[inv+0x1C]` = ip, writes the target to the record's finally slot (`rec+0x28 + n*4`, count `rec+0x128`, asserts at n >= 0x40), pops it at `0xE54315` (`[rec+0x12C]` = 0), sets `esi = [inv+0x18]`, then dispatches the target. An exception in flight (`[inv+0x24] != 0`) takes the clause path.

### 21.4 Skipped, and why

- **Allocation exits (C4)**: parked 2026-11-10.
- **C6 cache class**: ~0 share.
- **Depth slot** (`GetSlot` / `TryGetValue` ip 9): EA's `alloca` per depth in `ves_exec` (§20.4), not code we compile.
- **`GetPrev` callvirt**: the comparer class is unknown in the game; a `DefaultComparer` of a value `T` boxes (C4).
- **ret teardown** (4.39 ms/s): a B3 piece.
- **`MoveNext` ldelema**: not a call exit.
- **A2 (why callees materialize frames so often, per method)**: the game count has no per-callee mat column; the host shows the comparer callees materializing only at depth-slot bails. A per-callee count needs a count build that names the callee class per guard bail. The same build would confirm I-476-1 (callee + native-body flag per bail) and name the `GetPrev` comparer. One game count run, no timing.

Truncation (§20.0) affects this ranking as follows. Ranks 2-11 come from bodies that did compile, so they are counted. A hot caller past the 841-body cap is missing entirely. Once the region is sized (R1), the census has to be read again before the next call-exit piece.

## 22. BoundCall: call EA's native String bodies from compiled code (2026-11-13, branch `feature/monojit-boundcall`, ts3native 0.36.250, ledger §484; built + host gates, NOT RUN in game)

§21.2 left 74% of the "no lazy body" bails (20.9 M of 28.27 M per BP run) at the string comparer's two guarded sites, whose targets are EA native bodies. `[MonoJit] BoundCall=1` (default 0) lets such a site call the bound body itself, the way EA's interpreter does, and run on compiled instead of exiting. Code: `native/ts3native/monojit-bound.h` (shared with monohost), `monojit.cpp` (`JitGuardFill`, `EmitLazyCall`, `ApplyMonoJit`).

### 22.1 EA's path for a bound body [verified: disassembly, Steam 1.67]

- `ves_exec` call path `0xE56287`-`0xE563ED`: push a frame record, depth + 1, fill the callee's MonoInvocation (+0 parent = the current one `[ebp-8]`, +4 RuntimeMethod, +8 return slot = the caller's eval top, +0xC `this`, +0x14 args = top - 8 x params, +0x1C ip / +0x20 exception / +0x24 clause = 0), ThreadContext+8 = the new frame, transform if needed, arena bump; then `cmp [rm+0x24], 0; jne 0xE5925F`.
- `0xE5925F` calls `0xE525C0(frame, ctx)`. A set `[frame+0x20]` takes the exception path; else `0xE543C7`, the common return: the slot's 8 bytes go to the caller's new top (the receiver's slot), and ret.void copies nothing.
- `0xE525C0` is cdecl `(frame, ctx)`: it saves ctx+0x10 (jmp_buf*) and ctx+0xC (env frame), runs `_setjmp3` (`0xF7EE92`), sets ctx+0x10 = its jmp_buf and ctx+0xC = frame, calls `[[frame+4]+0x24](frame, ctx)`, then puts both back. A native raise longjmps to that setjmp and leaves the exception in frame+0x20, so `0xE525C0` always returns normally.
- The binder (`0xD9A7B0`, run at RuntimeMethod creation `0xE52108` and at transform `0xE645FF`) writes +0x24 invoker, +0x28 body and +0x2C one-shot init from the registration map (key = FNV over name + signature via `mono_method_signature` `0xE85040`).

### 22.2 Calling convention, `this`, exceptions, GC [verified unless marked]

- **Frame.** `JitBoundCall(rm, top, self, ebp)` builds the 0x28-byte MonoInvocation on its own C stack with the words above, sets ctx+8 to it, calls `0xE525C0` (or whatever detour sits on it), and puts the caller's ctx+8 back. It writes no frame record, no depth and no arena: the two listed bodies read none of them (their invokers read frame +4/+8/+0xC/+0x14/+0x20 only).
- **`this`.** For the two rows it is the string reference (`rm+0x44` 0x200 set, 0x400 clear). A bound target with the value-type flag 0x400 is refused at fill (`bcVtThis`): its `this` would be struct data in the caller's vt area, and docs/206 §17 shows EA bodies with such a `this` (DateAndTime) read it through a copy we do not make. The guard's earlier checks (receiver not null, has a vtable, not the remoting proxy, not a boxed value type, class = the cache's class) all run before this path, so a null receiver still exits.
- **Exceptions.** A body that leaves `[frame+0x20]` set (for example `GetHashCode` on a null `this` via `0xE770E0`) makes `JitBoundCall` return 0: the site takes its normal exit at the callvirt with nothing of the caller's written, and EA's interpreter runs the call again and raises as vanilla. That double run is only allowed because both bodies are pure readers (no write, allocation or managed call once `this` is not null, docs/220 class `reader`). The guard rules out null receivers, so in practice this path is for the setjmp-raised case and has run 0 times in the host.
- **SEH / setjmp.** Calling `0xE525C0` itself keeps EA's setjmp around the body, so a raise inside it cannot unwind through our compiled frame [verified: `0xE525C0`]. Compiled code itself has no SEH frame of its own here.
- **GC [inferred].** EA's Mono uses Boehm with conservative stack scanning, so the MonoInvocation on the C stack, the receiver in `ecx` / the eval stack and the arguments stay roots during the call. Neither body allocates, so no collection can start inside them anyway.
- **ForceJIT.** The site rechecks MonoPatcher's ForceJIT byte (`0xE5637D`) and exits when it is set, as the lazy path does.

### 22.3 Satb and the worker rule

`[AutShadow]` detours `0xE525C0` (`SatbBindCall`, `satb.cpp` ~1553-1640). Calling `g_jitBoundEntry` = `0xE525C0` goes through that detour exactly as EA's interpreter would, so a worker-side Satb run and a sim-side run behave as before; the sim side with a body Satb does not know just calls the next hop [verified: source]. Compiled code only runs on the sim thread (`JitEnter`), so the game never reaches this path from a worker; the host's worker corpus pass (below) runs it on another thread anyway and stays at 0 differ.

Household / selected / unknown-scope Sim rules do not apply: these are pure `String` reads with results equal to EA's (the same EA body runs), with no Sim in scope.

### 22.4 Callees: allowed and excluded

| Callee | EA invoker / body | Verdict | Why |
|---|---|---|---|
| `String.GetHashCode()` | `0xDBFBF0` / `0xDBFAE0` | **allowed** (row 0) | hash loop h = h*31 + c over the chars; reads `[0x11EF7BC]` (OffsetToStringData's icall `0x927420` = `mov eax, 8; ret`); 4-byte result; null `this` = NRE in +0x20 without a longjmp [verified] |
| `String.Equals(string)` | `0xDDD900` / `0xDDD880` (+ `0xDA4200`) | **allowed** (row 1) | calls Equals(string,string)'s bound body `0xDA4200` directly when `[[0x11F06E0]+0x28] != 0` (lengths, then chars; reads only its arguments), else the n2m `0xD8A8E0` [verified] |
| `String.Equals(object)` | `0xDAA650` | excluded | calls `mono_runtime_invoke`: a managed call inside, not a pure reader |
| `Equals(string,string)`, `op_Equality` | `0xDA4310` / `0xDA4200` | excluded | static: no guard site (only callvirt / con.callvirt sites have guards) |
| bound bodies with a value-type `this` (DateAndTime, others in `binder-bodies.csv`) | - | excluded | `this` is struct data (22.2) |
| any other bound body | - | excluded until listed | each row needs its own byte check, purity proof and corpus cases; it counts as `bcNotAllowed` |

Each row carries the FNV-1a of its invoker + body (+ the extra callee) bytes: `0x958D9EA6` (0x28 + 0x104 bytes) and `0x8B23206C` (0x2E + 0x78 + 0x10A bytes). A row whose bytes differ stays off, and `[MonoJit] BoundCallMask` (bit per row, default 3) turns rows off for a per-callee A/B. MonoJit as a whole is Steam 1.67 only.

### 22.5 Estimated gain [inferred, I-484-1]

The C0 count runs (§21.1; intervals #3 - #2 over 300 s) give comparer guard bails of ~16.0k/s on BP (Equals 2.67 M + GetHashCode 2.12 M per run), ~18.9k/s on IP and ~13.9k/s on SV, and every one of them also materializes the comparer's lazy frame (jitmat 6.76 M / 9.32 M). BoundCall removes, per call, the exit, the frame write, EA's dispatch and record push, and the return re-entry into compiled code; the body's own work stays. At ~45-125 ns saved per call that is **~0.7-2.0 ms/s per world** (SV low end, IP high end). Upper bound ~2.6 ms/s: the comparer's sampled self time on BP (GetHashCode 2.00 + Equals 0.57 ms/s), which includes the exits but also work that stays. The census is a lower bound while the code region was full (§20.0), so a hot caller past the cap could add more. Measured by the in-load A/B below.

### 22.6 Design

- **Fill.** `JitGuardFill` on an IC miss: a transformed target with `rm+0x24 != 0` and `this` gets `bound = JitBoundOf(rm)` (row + 1, or 0 for value-type `this` / off the list), with `entry` left NULL as before.
- **Site.** With BoundCall installed, the guard's "no lazy body" jump goes to a new block: `bound == 0` -> exit (why 6, as before); `bcOn == 0` (runtime off) -> exit; ForceJIT -> exit (why 7); else `push ebp; push ecx (this); push esi (top); push [site.rm]; call JitBoundCall`; eax 0 (exception) -> exit (why 6); else `esi = eax` and continue after the call. No new count reason: the old exits keep their reasons.
- **Switch.** `[MonoJit] BoundCall=1` (default 0; needs `Direct=1`, `DirectLeaf=0`, since guards only exist then), `BoundCallMask=3`. Installed at `ApplyMonoJit`; `bcOn` (0x6278) is also a runtime switch (`--ab boundcall`, `tools/harness/native-switches.ts`): 0 sends every bound site back to its exit.
- **Counters v194** (0x6278-0x62A7, 12 int32): `bcOn`, `bcFills`, `bcHits`, `bcExc`, `bcNotAllowed`, `bcVtThis`, `bcCallee[4]` (hits per row), `bcSpare[2]`. 0x6258-0x6277 (v193) is held for `[Crash2Diag]` on another branch.
- **Count build folded in (I-476-1, A2).** With `Count=1`, each guard bail with why 6 also records its target (`JcnGuardBail`, 4 targets per site), and the report adds `jitcallee <caller> <ip> <op> <lazy|normal> <target> native=<0|1> bound=<row|0> nolazy=<n>` lines. One count run therefore names the remaining native callees per site, the `GetPrev` comparer, and which callees materialize frames.
- **StrKeys stays out (I-476-2).** BoundCall does not turn `String` methods into compiled keys (they stay native-body keys), so the `StrKeys` shapes (OffsetToStringData inline, native leave) still have no sites. Not re-added.

### 22.7 Host (monohost)

- **Stand-in binder** `MONOHOST_JITBIND=1`: the host runs no registration table (docs/119 §9.4), so `JitBindStandIn` binds `System.String.GetHashCode()` and `System.String.Equals(string)` right after their transform by name + signature (`0xE85040`: param count, type byte 0x0E, not byref) to the same EA invokers and bodies, and does the one-shot inits (`[0x11EF7BC] = 0x927420`; `[0x11F06E0]` = a block whose +0x28 = `0xDA4200`). `MONOHOST_JITBOUNDCALL=<mask>` installs BoundCall. `tools/jit-replay.ts --switches` takes `Bind=1,BoundCall=3`.
- **Corpus case** (`tools/jit-corpus.ts`, 263 cases): `EqualityComparer<string>.Default` GetHashCode / Equals, `string.GetHashCode`, `Equals(string)`, `Equals(object)`, `==` over null, empty, 1-3 chars, equal strings with different references, non-ASCII (`été`, CJK, a surrogate pair), an embedded NUL and a 257-char string, plus a `Dictionary<string,int>` on that comparer.
- **Results (final build)** with `MONOHOST_JIT=verify MONOHOST_JITLAZY=1 VtOps CallExit NewVt Inline=2 Wide`, `JITBIND=1`:

| Arm | Corpus (main) | Worker pass | Per-call verify | Bound calls |
|---|---|---|---|---|
| BoundCall=3 | 263 cases, 0 differ, 0 unstable | 209 cases, 0 differ | ok 61,468, MISMATCH 0 | 27,782 (GetHashCode 18,315, Equals 9,467), exceptions 0, off list 0, vt this 0; 6 guard sites bound |
| BoundCall=3 + Count=1 | 263 / 0 differ | 209 / 0 differ | MISMATCH 0 | same |
| BoundCall=0 | 263 / 0 differ | 209 / 0 differ | MISMATCH 0 | 0 |

SELFTEST 41/41 in every arm. The c3g game-capture replays (`node tools/jit-replay.ts --run probe-out/jit-capture-<w>-c3g.txt --lazy --switches VtOps=1,CallExit=1,NewVt=1,Inline=2,Wide=1,Bind=1,BoundCall=3`):

| World | Inputs ran | Differ / unstable / faulted | Per-call verify | Bound calls |
|---|---|---|---|---|
| BP | 4,307 of 4,466 | 0 / 0 / 0 | ok 1,080,046, MISMATCH 0 | 1,047 (GetHashCode 626, Equals 421), exceptions 0 |
| IP | 4,235 of 4,374 | 0 / 0 / 0 | ok 1,049,936, MISMATCH 0 | 910 (599, 311), exceptions 0 |
| SV | 4,275 of 4,416 | 0 / 0 / 0 | ok 1,084,301, MISMATCH 0 | 1,008 (636, 372), exceptions 0 |

The same three with `BoundCall=0` (stand-in binder only): BP 4,307 / IP 4,235 / SV 4,275 ran, 0 differ, 0 faulted, MISMATCH 0 (identical counts to the BoundCall=3 runs).

- **Native selftest** (`TestMonoJitBoundCall`): frame words, result in the receiver's slot, ctx+8 put back, exception -> exit with the slot unchanged, ForceJIT, runtime off then on, off-list and value-type refusals, a BoundCall=0 compile, counter offsets. It caught a real bug before any run: the first emitter pushed the arguments in the wrong order (ecx, ebp swapped).

### 22.8 Game port and checks

ts3native 0.36.250 (crash2diag took 0.36.249 on master first; the v193 slots 0x6258-0x6277 are its c2* counters). Checks: native selftest ok, `hook-sites --check` ok (no new patch site: BoundCall calls `0xE525C0` and only reads bytes), `patch-bytes-check` 3144 expectations, mismatch 0, `selftest-all` 227 PASS / 0 FAIL, `native-switches --self-check` ok.

Pair `boundcall-1113`: `game-copy/ts3native.asi.boundcall-1113` + the release package `ts3fixes.package.rc1111e`. Queue `tools/harness/queues/boundcall.json` (prepared, NOT RUN): `bp-boundcall-smoke` (5 min, `--ab boundcall` 1-min slices, stops the queue on FAIL), `bp`/`ip-boundcall-correct` (10 min, Mode=2 verify, CodeKB=4096, BoundCall=1, Count=1; gates script errors 0, OK line `BoundCall=0x3`, `bcHits>=1000`, `bcFills>=1`, `jitMismatch` / `jitFaults` 0, a `jitcount: end` line), `bp`/`ip-boundcall-ab` (10 min in-load A/B, 2-min slices, Mode=1, Count=0). Run: `node tools/harness/queue.ts tools/harness/queues/boundcall.json`.

### 22.9 Open

- I-484-1: the gain (A/B above).
- I-484-2: the count run's `jitcallee` lines name the remaining native callees (expected: none of the two rows at the comparer sites) and the `GetPrev` comparer class.
- I-484-3: the host stand-in binder matches the game's binder for these two rows (same invoker, body and inits). The correctness jobs check it in game (bound calls > 0, script errors 0, verify MISMATCH 0).
