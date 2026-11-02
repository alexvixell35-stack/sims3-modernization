# 507. Compiler groundwork: monohost-jit in ts3native, and which mint opcodes can be generated

Static, 2026-11-01. No build, no game run. Contributor round 7 task 3 (offline).

Status 2026-11-02: §b's generatable class is lifted and checked under emulation in docs/524 (132 of 136 opcodes, 0
mismatches; `tools/emu/specs/mint_lift.py`; the template bytes stay local under `tools/emu/specs/out/`, the census is
`docs/data/jit-lift-census.csv`).

Inputs: `native/monohost/monohost-jit.cpp` (P0, docs/207), `native/ts3native/*.cpp`, `docs/data/mint-opcode-spec.csv`
(docs/213), `docs/data/jit-exit-census.csv` (docs/207, weighted by `compile-candidates.csv` ms/s). Output:
`docs/data/jit-opcode-table.csv` from `node tools/jit-opcode-table.ts` (`--check`, `--self-check`).

Tags: **[verified]** = read in the source or the data files; **[inferred]** = reasoning, with its check.

## Summary

- **a) Shared source: yes, behind an adapter of 8 functions, with two hook conflicts and one correctness gap.**
  `monohost-jit.cpp` is included by `monohost.cpp` and uses only a few host symbols. `mintcall.cpp` already works this
  way: one source, included by both ts3native and monohost, with the includer defining `MC_CTR` [verified:
  mintcall.cpp header]. The conflicts are the transform detour, which satb already owns, and the handler-table and code
  writes, which must go through ts3native's checked helpers. The gap is the satb write barrier: compiled stores do not
  pass through the jump-table store handlers that satb wraps.
- **b) Opcodes: 153 covered, 136 generatable, 160 hand** (449 in docs/213). The exits behind the 33.8 ms/s the census
  measures split as:
  - covered: 12.5 ms/s, all of it `ret` / `ret.vt` / `ret.void` frame teardown;
  - generatable: 8.3 ms/s, mostly value-type and float moves;
  - hand: 13.0 ms/s, mostly `call` / `callvirt`. The 110 hand opcodes that only throw add up to 0.7 ms/s.

## a) monohost-jit.cpp inside ts3native

Host symbols the file uses, found by a scan of identifiers it uses but does not define that `monohost.cpp` defines
before the include [verified], plus the Win32 and game state it reaches:

| # | Dependency (where in monohost-jit) | ts3native has | Adapter |
|---|---|---|---|
| 1 | `Log(fmt, ...)` (41 uses) | `Log` in ts3native.cpp:22, printf style [verified] | none (same name) |
| 2 | `mt::Write(addr, bytes, n)`: handler table entry `JIT_OP`, call patches (lines 1229, 1524, 1571) | `Write(void*, const void*, size_t)` ts3native.cpp:51 [verified] | `namespace mt { using ::Write; }` |
| 3 | `Jmp(at, target)`: transform wrap (line 119) | `InstallDetour` with a byte check, used by satb's cold path on the same address [verified] | **conflict**: one transform-entry dispatcher with satb, the compiler and the IL patcher as clients (docs/506 §3) |
| 4 | `MintName(op, buf)`: dumps and the exit log only (lines 85, 1273) | no | `JitOpName`: hex, or the names from `mint-opcodes.csv` compiled in |
| 5 | Script-thread check: `g_jitMainTid = GetCurrentThreadId()` at set-up (line 1557), tested on every compiled entry (line 1078) | `g_mainTid`, `RgIsSim`, `g_rtSimTidSlot` [verified] | `JitIsScriptThread()`: the sim thread, not the set-up thread (set-up runs at load) |
| 6 | Mode, inline, lazy and the method list: `MONOHOST_*` environment variables | ini switches | `JitConfig()`: one ini section, off by default |
| 7 | Code and table memory: `VirtualAlloc` RWX 4 MB code, a table of `JIT_TAB_N` + 4096 entries, 1 MB per thread buffer (lines 1543-1566) | plain `VirtualAlloc` elsewhere | `JitAlloc(size, exec)`: counts the address space it takes (32-bit process, CLAUDE.md) |
| 8 | Test drivers: corpus (`SafeInvoke`, `Cls`, `Meth`, `Describe`, `LoadImageByName`, `Narrow`, lines 1297-1385), `MONOHOST_DIVGUARD` stubs (line 1492), verify-mode `JitAtomic` (`GC_malloc_atomic` 0xE4BDC0, line 1229) | not needed: `eafix.cpp` has MintDivGuard; `monothread-m5.cpp` detours 0xE4BDC0 [verified] | `#ifdef MONOHOST` around them; `JitAtomic` must never run in the game (it would break M5's byte check) |

Not a host symbol, but a gap: **satb**. While satb's marking span runs, the sim thread's reference stores must pass the
SATB barrier. satb gets them by swapping the mint jump-table entries of the store opcodes [verified: satb.cpp jump-table
swap, `kSatbOps`]. A compiled method replaces each opcode word with `JIT_OP` and runs its own stores, so the swapped
entries never see them. The ts3native build must either emit the barrier in the `stfld.o` / `stind.ref` / `stelem.ref`
templates (and `stloc.o` where satb wraps it) or turn compiled entry off during a span. `0x1A9` itself is free: satb does
not wrap it [verified: no `0x1A9` entry in satb.cpp], and mintcall uses only 0x06, 0x07, 0xEA and 0xEC [verified].

Verdict: the file compiles into ts3native unchanged except for `#ifdef MONOHOST` around item 8. The adapter is items
2 and 4-7 (5 small functions plus a namespace alias) and the dispatcher of item 3. The satb barrier is design work, not
adapter work [inferred: the size estimate comes from the uses above, not from a build].

## b) Opcode table

`tools/jit-opcode-table.ts` joins three sources:
- P0's template table `kJitTmpl` (covered = has a template);
- docs/213's per-opcode columns;
- the exit census (time behind each opcode's exits; `X slow path` rows count under X).

Classes:
- **generatable**: no template, `branches_or_calls` is "-" or a plain branch, no allocation in `heap_writes`, and
  `can_throw` is "-" or only a native fault. The handler's own bytes can then be lifted as the template, and each of
  them already has a docs/213 test case (0 without one) for its corpus case.
- **hand**: the rest, with the reason (call / alloc / throw).

| Class | Opcodes | With exit time | ms/s behind | Largest |
|---|---|---|---|---|
| covered | 153 | 3 | 12.5 | `ret` 10.4, `ret.vt` 1.3, `ret.void` 0.8 (frame teardown, B3) |
| generatable | 136 | 21 | 8.3 | `vtresult` 2.4, `stinarg.vt` 1.6, `ldarg.vt` 0.8, `ldloc.r4` 0.6, `ldarg.r4` 0.5, `stloc.r4` 0.4 |
| hand | 160 | 26 | 13.0 | `call` 5.7, `callvirt` 2.6, `newobj` 0.8, `con.call` 0.6, `con.boxcallvirt` 0.5, `isinst` 0.4 |
| hand, throw only | 110 | — | 0.7 | could move to generatable with a stub that exits to EA's handler on the throw path |

Lifting a handler [inferred, checked per opcode by its emulated case (docs/221 harness) before use]:
- keep the body;
- drop the `ip` advance and the dispatch tail (jump to the head 0xE544D5, or the inlined `jmp [op*4 + 0xE59880]`);
- relocate rel32 targets.

Two groups need more than that:
- The value-type moves (`vtresult`, `*.vt`) also use the frame's value-type stack pointer, so their templates must use
  the compiled frame's copy of it.
- The float ones keep EA's x87 instructions as they are, which is what the census's "x87-exact float (B2)" effort asks
  for.

Order this suggests (time per opcode, not a plan):
1. `ret` teardown (covered, 12.5 ms/s);
2. compiled-to-compiled `call` / `callvirt` (hand, 8.3 ms/s);
3. the 21 generatable opcodes with time (8.3 ms/s, mechanical);
4. the throw-only stub (0.7 ms/s).

## c) The Satb barrier, built (2026-11-02, contributor round 8 B3; host build + host selftest)

Section a) left one gap: while an `[AutShadow] Satb` span is on (docs/206 §7), the sim thread's stores must be logged, and
Satb gets the interpreter's by swapping the store handlers in the mint jump table, which compiled code never reaches.
Reading the code for the fix showed a second one: every compiled exit to a handler jumped to the handler pointers read at
install (`g_opHandler`), not to the live table, so even the store opcodes the compiler does not cover (`stsfld`, `stelem.*`,
`stobj`, the value-type stores) skipped Satb's stubs inside a compiled method [verified: `EmitExit`, `JitOrig`, `JitLazyMat`].

Proof of the gap before the fix, offline (`monohost.exe ... selftest`, `MONOHOST_SATB=1`, `MONOHOST_JIT=on`,
`MONOHOST_JITLIST=SatbWorld.Mutate,SatbWorld.WriteRefI4,...,SatbWorld.Score,SatbWorld.Fill`: the Satb test's own
mutator and scorer compiled, 9 methods, 691 native ops, 458 exits) [measured]:

| Run | AS-satb-exact | Rest of the selftest |
|---|---|---|
| JIT off | A0 6000 of 6000, A1-A3 0 | 47 of 47 |
| JIT on, no barrier | **A0 1 of 6000, A2 5999** (the worker saw the compiled stores); survive and rtwriters fail with it | 44 of 47 |
| JIT on, barrier | A0 6000 of 6000, A1-A3 0; 627,628 compiled heap stores logged in spans, 12,283,402 worker entries sent back to the interpreter | 47 of 47 |

The fix is three changes in `monohost-jit.cpp` (the shared source; nothing in satb.cpp):

1. **Store templates.** `stfld.*` and `stind.*` (the only heap stores the compiler emits; `stloc` / `starg` / `stinarg` write
   the frame arena, which Satb treats as private) call `JitSatbStore(address, bytes)` before the store. The helper is
   `SatbSimSide()` then `SatbSimRange(addr, n)`: the same path as the stubs, so a frame-arena target is skipped, a full log
   or a store over `SATB_BIGW` words waits for the worker. Emitted: `cmp dword ptr [g_satbOn], 0; je skip; push base;
   lea edx, [base+disp]; push n; push edx; call; add esp, 8; pop base` (19 bytes; outside a span one compare and a taken
   jump; the base register survives, eax/ecx/edx are scratch at both sites).
2. **Exits through the live table.** `EmitExit`, `JitOrig` (verify mode) and `JitLazyMat` jump through `[0xE59880 + op*4]`
   instead of the install-time snapshot, so a handler Satb (or mintcall) has swapped is the one that runs.
3. **The worker never runs compiled code in a span.** `JitEnter` tests `g_satbOn`, then the thread id (`fs:[0x24]`) against
   `g_satbWTid`; the worker goes to the live handler of the original opcode (`JitEnt.orig`) with the exit protocol
   (`edi` = ip, `[ebp-4]` = ip, `eax` = `edx` = opcode), so its loads keep Satb's redirect (act mode). Lazy callees and
   inlined bodies are entered only from compiled code, so the bail at every `JitEnter` covers them too.

Cost, same host run, best of 20 with a game test running on the machine [measured, noisy]: 100 mutations 1.51 ms with
Satb off / 1.85 ms on when compiled, 1.39 / 1.71 ms interpreted. The per-store barrier cost is inside that noise; what
the worker loses is the compiled speed of its own job during a span (every entry bails), which the ts3native build can
measure when it exists.

Not covered: verify mode's `JitVerifyExitStub` path (never on with Satb); a ts3native build still needs the adapter of
section a). Also found: the host selftest on main stopped at `AS-install` because it expected 31 AutShadowNative ICs and
`Setup` version 10 while the code has had 34 and 11 since the PR #169 import; the expectation is updated in the same
change. Ledger: docs/51 §924.
