# 213. Mint opcode spec: every opcode of the embedded interpreter, with test cases

Written 2026-10-30 (UTC). Static reading plus emulation of the game's own handler code; no game run, no build, no queue. Question (Rodney, research task 2): what exactly does each of the 449 mint opcodes of the Steam 1.67 `TS3W.exe` do, including edge cases, so that a compiler (docs/207 §7), the worker-thread load/store emulation (docs/206 §7) and crash triage can rely on one reference? Data: `docs/data/mint-opcode-spec.csv`, one row per opcode, the same 449 rows as `docs/data/mint-opcodes.csv` (opcodes `0x000`-`0x1C0`; that file has 450 lines including its header).

Tags: **[verified]** = read in the handler code of the Steam 1.67 exe (address given); **[community]** = Mono 1.2.3 source (git tag `mono-1-2-3`, `mono/interpreter/interp.c`, `mintops.def`, `transform.c`), read only, cited by file and function or opcode case; **[inferred]** = reasoning, not run. In the CSV `test_cases` column, **[emu]** marks a test that ran the game's own handler bytes in an x86 emulator and matched the stated result; that is [verified] behaviour of the handler. **[not run]** marks a case written from the handler code that the harness could not run; treat it as [inferred].

## 1. Method and limits

- **Handler code [verified].** Jump table `0xE59880` (449 entries, 394 distinct handlers), name table `0x11949E0`, operand-length table `0x1195088` (docs/130 §1.2). Every handler was read from its table entry through its shared tails, fault stubs and the two element sub-tables `0xE59F84`/`0xE59FB0` (docs/130 §10.7). Every CSV row's `notes` starts with `[verified 0x<handler>]`. No row is unchecked and no row is source-only.
- **Source comparison [community].** The 1.2.3 `mintops.def` has 406 opcodes; 1.2.4 through 2.0 have the same list, so names that are not in it are EA additions, not a later upstream version. Where a row says "upstream same" or "EA differs", the named `interp.c` case was read for that opcode.
- **Emulation [verified].** The approach of docs/63 (`tools/emu/`: one game function under Unicorn), extended to a whole interpreter frame. A scratch harness, not committed for this note, loads the PE sections of `TS3W.exe` read-only at their virtual addresses into the Unicorn emulator, builds a fake interpreter frame with the register and frame layout of docs/130 §1.3 (`edi` = ip in bytes, `esi` = next free 8-byte eval slot, `[ebp-4]` ip copy, `[ebp-8]` MonoInvocation, `[ebp-0xC]` vt stack top, `[ebp-0x10]` RuntimeMethod, `[ebp-0x14]` locals, `[ebp-0x2C]` frame record), and starts at the opcode's table entry. A run ends at the dispatch loop `0xE544D5`, the exception search `0xE540AB` (the exception builder that ran is recorded, e.g. `0xE770E0` NullReferenceException), the call block `0xE56287`, the frame return `0xE543C7`, the finally pass `0xE54198`, a CPU fault, or a call the test did not expect. CRT helpers (`_ftol2`, `_ftol2_x87`, `_alldiv`, `_allmul`, `_allshl`, ...) run for real; `_isnan`, `_finite`, `memcpy`, `memset` and `memmove` are replaced by C-semantics stubs; runtime calls that a test needs (class init `0xEA3370`, `mono_object_isinst` `0xEA3820`, the callvirt lookup `0xE51FC0`, `GC_malloc` `0xE4BE30`, ...) are stubbed with a fixed return value, so their own behaviour is not covered.
- **Superinstructions.** Each of the 22 superops ran against its base sequence in the same harness (the dispatch loop allowed to continue while ip stays inside the sequence), in three heap scenarios, and the final state (ip, eval slots, locals, args, exception and the word it reports) was compared. This adds a dynamic check to the static equivalence of docs/130 §9.1.
- **Counts.** 746 tests ran, all match; 25 cases in 23 rows are [not run]. 430 rows have at least one [emu] test; the 19 without one need runtime state the harness does not build (static-field class init, the transform, the call paths of `newobj`/`calli`/`jmp`, `box`/`stobj` type dispatch, CRT `fmod`, the unimplemented-opcode path).
- **Limits.** (1) Float results depend on the x87 control word of the thread that interprets. The tests ran at 53-bit precision (`0x027F`); one test shows that at 24-bit precision `add.r8` rounds to float precision. The game creates its D3D9 device without `D3DCREATE_FPU_PRESERVE` (docs/37), so a thread whose FPU D3D switched would compute every `r8` opcode at float precision; docs/37 says the sim thread is not that thread [inferred there]. (2) `conv.i1/i2/i4.r8` and the checked `i1/i2/i4` float conversions call `_ftol2`, which takes an SSE2 or an x87 path by the CRT flag `[0x1234374]`; the tests ran with the flag set (any SSE2 CPU) and one test shows the other result. (3) The emulator's x87 is a software model, not bit-exact for every extended-precision corner [inferred]. (4) One build only: Steam 1.67 with the LAA flag.

## 2. How to read the CSV

| Column | Meaning |
|---|---|
| `operands` | Words after the opcode word: `wN` = u16 at word N, `dN` = u32 in words N and N+1, `qN` = u64. "item" = index into the method's data items (RuntimeMethod `+0x14`). |
| `stack_in` / `stack_out` | Eval slots, bottom to top. `i4` results write only the low dword of the 8-byte slot; `r4` values live as doubles on the stack. `vt` = a slot holding a pointer into the vt stack. |
| `heap_reads` / `heap_writes` | Any memory beyond the eval stack: managed objects, statics, the frame arena (args, locals), the vt stack, data items. |
| `can_throw` | Managed exceptions and native faults (access violation, CPU divide error) the handler can produce itself. |
| `branches_or_calls` | ip changes other than "next instruction", and native or runtime calls. |
| `notes` | Semantics, sharing, EA versus upstream, bugs. |
| `test_cases` | `input -> result` cases, `;`-separated, each tagged [emu] or [not run]. |

Conventions that hold for every row [verified]: branch offsets count 16-bit words from the branch opcode itself; `switch` offsets count from the end of the switch instruction. "At this instruction" means the exception's frame ip (MonoInvocation `+0x1C`) points at the opcode word. Value types on the vt stack are 8-byte aligned. Objects have a 4-byte header (vtable only), arrays keep the length at `+8` and data at `+0x10`, strings the length at `+4` and chars at `+8` (docs/130 §2).

## 3. EA-specific differences from Mono 1.2.3

| # | What | EA build | Mono 1.2.3 | Tag |
|---|---|---|---|---|
| 1 | Opcode list | 425 base opcodes + 2 unimplemented slots + 22 superops. **21 EA-only base opcodes:** `vtldfld.*` `0x4B`-`0x57` (13), `con.call` `0xEB`, `con.callvirt` `0xED`, `con.boxcallvirt` `0xEE`, `ldelem.any.vt` `0x111`, `stelem.any.vt` `0x112`, `sim_sleep`/`sim_chkyld`/`sim_chkyld2` `0x1A6`-`0x1A8`. `vcall`/`vcallvirt` removed. Numbers shift from `0x4B` on, so upstream opcode numbers are wrong for this build. | 406 opcodes | verified / community |
| 2 | `callvirt` `0xEC` | 4 words with a monomorphic inline cache (cell = 2 data items: class, target; a miss calls `0xE51FC0` and rewrites the cell); null `this` or null vtable -> NullReferenceException | 2 words, lookup every call | verified (docs/133) |
| 3 | `con.*` calls | `constrained.` prefix forms: `con.call` skips the class word; `con.callvirt` dereferences the receiver pointer (struct: copies it to the vt stack); `con.boxcallvirt` boxes `*pointer` (`mono_value_box` `0xEA3140`) and calls through the box | `vcall`/`vcallvirt` | verified; which form the transform picks is [inferred] |
| 4 | `vtldfld.*` | field load from a struct temp on the vt stack, fused with releasing the temp; `vtldfld.vt` copies the field down over the temp | ldfld after a separate copy | verified |
| 5 | `ldelem.any.vt` / `stelem.any.vt` | whole struct element to/from the vt stack in one opcode, bounds-checked, no write barrier | `ldelema` + `ldobj.vt` / `stobj.vt` | verified |
| 6 | `vtresult` `0x9` | aligns the released size to 8, skips the move when source = destination, repoints the slot | unaligned subtract, always moves, slot not repointed | verified / community |
| 7 | `conv.r4.i4`/`conv.r4.i8` | no rounding to float: the value stays a double (compiler kept it in the x87 register) | casts to `float` | verified / community; compiler cause [inferred] |
| 8 | `localloc` | `GC_malloc` (zeroed, freed only by the collector, one GC allocation per execution) | `alloca` on the C stack | verified / community |
| 9 | `ldelema` | adds a null check, but reports the NullReferenceException at the next instruction | no null check | verified / community |
| 10 | `castclass`/`isinst` | interface test through `Class_GetInterfaceOffset` `0xD70870` | reads `vtable->interface_offsets` | verified / community |
| 11 | `mono_icall_*` (9) | one helper `0xE52E70(context, opcode, sp, function)` | one inline case each | verified / community |
| 12 | `sim_*` | `Simulator.Sleep` and yield-context checks: throw NotSupportedException "Attempting to yield in a non-yielding context!" when `may_yield` `0xE53550` says no; `sim_sleep` saves the frame chain and returns from the interpreter | none | verified |
| 13 | Finally targets | 64-entry array in the frame record (docs/130 §1.3) | GSList | verified / community |
| 14 | Superops | 22 fused sequences `0x1AB`-`0x1C0` (docs/130 §9.1), now also checked by differential runs | none | verified |

Everything else matched the 1.2.3 case for the same opcode, including the bugs in §4.

## 4. Bugs and CLI deviations (all [verified] in the handler; the source column says whether 1.2.3 has them)

| Opcode | Behaviour | CLI expects | 1.2.3 | Reach from game C# [inferred] |
|---|---|---|---|---|
| `div.i4` `0x127`, `rem.i4` `0x143` | `INT_MIN / -1` and `INT_MIN % -1` hit `idiv` unguarded: CPU divide error (#DE), not caught by the interpreter, so the game crashes [verified in emulation: the CPU raised vector 0; neither `MonoScriptHost_Simulate` `0xD81840` nor `ves_exec_method_with_context` `0xE53EB0` sets up an SEH frame in its prologue [verified], and 1.2.3 installs no SIGFPE handler [community]; outer EA frames not checked, so the crash itself is inferred] | OverflowException (div), 0 or ArithmeticException (rem) | same | any `int` division with runtime operands |
| `mul.ovf.i4` `0x130`, `mul.ovf.i8` `0x131` | `MIN * -1` returns MIN without an exception; `(MIN+1) * -1` throws although it fits; `-1 * MIN` (other order) throws correctly | OverflowException only for MIN * -1 | same (`CHECK_MUL_OVERFLOW`) | `checked` code only |
| `call` `0xEA` (and `calli` `0xEF`) | the dead transparent-proxy test reads `this->vtable->klass` with no null check, so a null `this` on a non-virtual call to a reference-type instance method is a native access violation | NullReferenceException | same | C# emits `callvirt` for most instance calls; reachable by other compilers or IL rewriters |
| `brfalse.r8` `0xA2` | long form decoded as the short form: 16-bit offset, falls through 2 words; a backward offset lands on `0xFFFF` and goes to the unimplemented-opcode handler | n/a | same (`ZEROP_S`) | only for `brfalse` on a float, which C# never emits |
| `conv.r4.i4`/`.i8` | no float rounding (§3 #7). A following `stloc.r4`/`stfld.r4` rounds on the store, so only uses before a store see the extra precision (docs/139 counts `conv.r4.i4+stloc.r4` among the frequent pairs of a monohost run) | rounded to float | EA only | `(float)i` used in arithmetic or a compare |
| `conv.ovf.*.r8` | the range test uses the untruncated value against inclusive bounds: `2147483647.5` and `-0.5` (for unsigned) throw; NaN passes and converts to the integer-indefinite value; `conv.ovf.i8.r8` accepts `2^63` and returns `INT64_MIN` | truncate first; NaN throws | same | `checked` casts from double |
| `conv.ovf.i4.un.r8` `0x17C` | uses the u4 range, so 2^31..2^32-1 give negative `int` without an exception | OverflowException above `INT_MAX` | same | rare |
| `conv.ovf.u8.r8`/`conv.ovf.i8.un.r8` | range `[0, 2^63]`; valid values above `2^63` throw | `[0, 2^64)` | same | rare |
| `conv.u8.r8` `0x16B` | shares the `conv.i8.r8` handler: values >= `2^63` and NaN give `0x8000000000000000` | unsigned conversion | same | `(ulong)double` above `long.MaxValue` |
| `conv.ovf.i4.un.i8` `0x17B` | the check can never fire (`> 0xFFFFFFFF` on a dword); keeps the low dword | OverflowException above `INT_MAX` | same | `checked((int)ulongValue)` |
| `ldrmfld.vt` `0x4A`, `vtldrmfld.vt` `0x57` | a first `memcpy` reads at `obj +` (word 1 of the next instruction) before the correct copy overwrites it: a latent out-of-bounds read | n/a | same (`ip` advanced before use) | proxy-free build: these opcodes are emitted for `MarshalByRefObject` fields [inferred] |
| `cpblk`/`initblk` `0x101`/`0x102` | null address -> NullReferenceException reported one word before the instruction | n/a | same | `unsafe` code only |
| `ldind.*`, `stind.*`, `initobj`, `ldobj`, `stobj` | no null check: null is a native access violation | NullReferenceException | same | null `ref`/pointer |
| `shl`/`shr` `.i4` | count masked to 5 bits (x86); `.i8` forms take the count's low byte through the CRT (64..255 -> 0 or sign, 256 wraps) | unspecified for counts >= width | same | C# masks the count itself, so never |

## 5. Corrections and closes for earlier notes

- **`MonoClass+0xA0` is `byval_arg`** [verified for the uses]: `box`, `ldobj`, `stobj`, `con.callvirt` and `stackval` calls pass `class+0xA0` as the MonoType (type byte at `+0xA6`). This closes docs/130 §7 "`+0x98` vs `+0xA0`"; the docs/130 §3 table row that names `+0x98` as `byval_arg` should read `this_arg` at `+0x98` [inferred from the 1.2.3 field order: `this_arg` then `byval_arg`, 8 bytes each].
- **EA-only opcode count**: 21 (18 general + 3 `sim_*`), not counted anywhere before; docs/133 had the `callvirt` layout right.
- **`call` null `this`**: docs/130 §1.4 and D21 describe the proxy test as dead, which is right, but its load of `this->vtable` still runs and faults on null (§4).
- **`calli` `0xEF`** has the same proxy test against `0x1220EC0` (docs/130 §10.2 lists `0xE54848`).

## 6. What this means for current work

- **Compiler / fast paths (docs/207 §7, docs/139 §5).** A template compiler that copies these handlers inherits every §4 row; one that writes its own arithmetic must decide per row whether to match EA (bit-exact with interpreted code) or the CLI. Matching EA is the safe default for verify mode; the #DE crash and the `call` null fault are worth fixing in both [inferred].
- **ts3fixes candidates [inferred, not built].** (1) Guard `div.i4`/`rem.i4` for `-1` divisors by a table patch of the two handlers; the cost is one compare on the divide path. Built as `[EaCrashFixes] MintDivGuard` (§8, ts3native, not ts3fixes). (2) A null check in `call` before the proxy load. Both are crash fixes for code paths no measured game script has hit; each needs a repro first (AGENTS.md "Repro per fix").
- **Worker emulation (docs/206 §7.2).** The load emulation follows the same rules: `vtldfld.*` and `ldelem.any.vt` read value-type temps on the vt stack, `con.callvirt`/`con.boxcallvirt` read through the `this` slot, and the NullReferenceException ip of `ldelema` is the next instruction.

## 7. Gaps

- [not run] rows: static-field opcodes with class init and thread statics (`0x066`, `0x069`-`0x06C`), the call paths (`con.callvirt` dereference, `con.boxcallvirt`, `calli`, `calli.nat`, `jmp`, `callint`, `callrun`, `newobj`), `box`, `stobj`, `rem.r8` (CRT `fmod`), `mono_retobj`, `sim_sleep` on the yield path, and the unimplemented-opcode path. Their notes are read from the handler code; the runtime calls they make are described by address only.
- Which IL the transform maps to `con.callvirt` versus `con.boxcallvirt`, to `vtldfld.*`, and to `ldelem.any.vt` is [inferred]; `transform.c` is the 1.2.3 version and EA's transform `0xE643B0` was not read for this note.
- The `0x1A9` row has 17 self samples in `docs/data/mint-opcodes.csv` although no opcode uses it; they are probably code laid out after another handler at that address range [inferred].
- The harness is not in the repo. Porting it to `tools/emu/` as a frame-aware spec (it maps the contributor's own exe copy at run time, like docs/63) would let the 746 cases run as a regression check.
- Not checked: the x87 precision the sim thread really runs at in game (docs/37 open item); the CRT SSE2 flag value in game (expected set).

## 8. Fix built: `[EaCrashFixes] MintDivGuard` (0.36.149, issue #114, eafix.cpp, counters v144, off by default; ledger docs/51 §412)

This builds the design in issue #114, with one change: the rem stub also checks for `INT_MIN`. So `x % -1` for every other `x` still runs the game's `idiv`, which gives 0, and the counter counts only the cases that used to fault.

| | |
|---|---|
| Patch | Jump-table entries `0x127` (`0xE59D1C`, `div.i4` `0xE55B4C`) and `0x143` (`0xE59D8C`, `rem.i4` `0xE55C0F`) of the table at `0xE59880` point to two naked stubs. Each stub compares the divisor `[esi-8]` with -1 and the dividend `[esi-0x10]` with `INT_MIN`; any other pair jumps to the game's handler with every register as it came. One aligned 4-byte store per entry in DllMain, before any script runs. Install refuses (one SKIP line) unless both entries still name the game's handlers and both handlers start with `83 7E F8 00 0F 84` (`cmp [esi-8],0; je` the DivideByZero stub `0xE593F8`) [verified disassembly] |
| `div.i4 INT_MIN / -1` | jumps to `0xE57AA5`, the OverflowException stub the `*.ovf` opcodes use. It stores `edi` (this instruction) as the frame ip and throws, so `catch` blocks and ErrorTrap see the right IL offset [verified disassembly: `mov [ebx+0x1C],edi` at `0xE57AA8`] |
| `rem.i4 INT_MIN % -1` | does what the game's handler does after its `idiv`, with result 0: pops the divisor (`esi -= 8`), writes 0 over the dividend, `edi += 2`, `[ebp-4] = edi`, then jumps to the dispatch head `0xE544D5` [verified: the handler tail `0xE55C22`-`0xE55C2B`] |
| Counters (v144) | `mdOn`, `mdDivOverflow`, `mdRemMinus1` (each counts only the `INT_MIN` by -1 cases), `mdSpare`. The first 8 hits log `ea fix: MintDivGuard: <op>: <outcome>; script stack: ...` |
| Overlap | `[MonoFast]` CallPath dispatches through the same table, so it sees the stubs. `[AutShadow] Satb` swaps only its own wrapped entries (stores, loads, superinstructions, address opcodes), restores them from the copy it takes at install, and does not wrap these two. No superinstruction contains `div.i4` or `rem.i4` |
| Tests (offline) | selftest `TestEaCrashFixes` runs both stubs on a fake eval stack, with landing pads in place of the game's targets. 18 normal cases (`7/2`, `-7/2`, `MIN/1`, `5/-1`, `5/0`, `MIN/2`, `MAX/-1`, `MIN/0`, `0/-1`, as div and rem) reach the game's handler with `esi`, `edi`, `[ebp-4]` and both stack slots unchanged. `MIN/-1` reaches the overflow stub with nothing popped. `MIN%-1` reaches the dispatch head with 0, `esi` one slot lower and `edi`/`[ebp-4]` = ip + 2. The patch on a stand-in table: foreign handler bytes mean nothing is written, and the table is patched only once |
| In game (2026-10-30, one session, CI build) | `RandomUtil.GetIntMultiple(int.MinValue, int.MinValue, -1)` through the test agent (on stock it froze the sim thread) returned `OverflowException: Number overflow.`. The game ran on, `mdDivOverflow` went 0 -> 1, the log printed `ea fix: MintDivGuard: div.i4 INT_MIN / -1: OverflowException; script stack: Sims3.Gameplay.Core.RandomUtil.GetIntMultiple`, and control calls answered before and after [verified]. `(int.MinValue, 0, -1)` is not a repro: its first division is not `MIN / -1` |

## Reproduce

- Handler addresses, names and operand lengths: `docs/data/mint-opcodes.csv` (docs/130 Reproduce). Second-level element tables: `0xE59F84` (11 entries, `ldelem.*`), `0xE59FB0` (8, `stelem.*`).
- Harness, described so it can be rebuilt without EA files in git: map each PE section of `TS3W.exe` at its virtual address in Unicorn (x86-32); map separate regions for the C stack, MonoInvocation, RuntimeMethod, frame record, data items, arg offsets, vt stack, locals, args, stack args, eval stack, code and a heap; fill the frame fields named in §1; write the opcode and operand words to the code region; set `edi` = code, `esi` = eval top, `eax` = `edx` = opcode; start at the jump-table entry; stop at the addresses in §1. Patch the IAT slots of `_isnan`, `_finite`, `memcpy`, `memset`, `memmove` to stubs. For `_ftol2`, write the SSE2 flag `[0x1234374]` first. For superops, run the base sequence with dispatch allowed to continue while ip stays inside it.
- Source: Mono git tag `mono-1-2-3`, `mono/interpreter/` (`mintops.def`, `interp.c`, `transform.c`); compare `mintops.def` names and lengths with the EA name and length tables for §3 #1.
