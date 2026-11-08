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
