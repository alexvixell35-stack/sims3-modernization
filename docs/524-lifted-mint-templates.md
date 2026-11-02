# 524. Lifted mint templates: EA's handler bytes as compiler templates, checked under emulation (round 8 B5)

Emulation only (2026-11-02): no build, no game run. Contributor round 8 task B5, the template generator for the 136
"generatable" opcodes of docs/507 §b. `tools/emu/specs/mint_lift.py` reads each handler fragment of the embedded
interpreter from TS3W.exe, rewrites it as a template the docs/207 compiler can place in a method body, and runs EA's
handler and the template on the same mint frame under Unicorn (the docs/221 harness). **132 of the 136 lift; 2,189 cases,
0 mismatches, in both renderings of every template [emu].** The four left to hand templates are listed in §4, each with
its reason. Output: `docs/data/jit-lift-census.csv` (the committed record: per opcode the status, byte and slot counts,
calls, cases and mismatches) and, local only, `tools/emu/specs/out/jit-lifted-templates.json` (the template bytes and
their slots: EA's handler code, so git-ignored and never committed; `--write` regenerates it from the player's exe).

Claim marks: **[verified]** = read in the handler bytes or the tool's output on this exe; **[emu]** = the emulated
comparison; **[inferred]** = our reading, with its check.

## 1. What a lifted template is

The handler fragment (from its jump-table entry to the dispatch loop `0xE544D5`, including shared tails such as the
value-type store tail at `0xE56C21`) is walked with a symbolic ip offset, and every instruction gets one of these roles
[verified in `lift()`]:

| In the handler | In the template |
|---|---|
| operand read through the ip: `movzx ecx, word ptr [edi + 2]`, `movsx`, `mov r32, dword ptr [edi + k]` | `mov r32, imm32`: an **imm** slot (operand byte offset, size 2 or 4, zero- / sign-extend or plain) the compiler fills from the instruction |
| ip advance `add edi, n` / `lea edi, [edi + n]`, ip store `mov [ebp-4], edi`, dispatch `jmp 0xE544D5` | dropped; the dispatch becomes a **next** slot (a `jmp rel32` to the following instruction, or nothing if it is the last byte) |
| taken branch `lea edi, [edi + ecx*2]` (ecx = the operand) | a **taken** slot: `jmp rel32` to ip + 2 × the signed operand |
| jump to an exception stub (`0xE5947E` null, `0xE5949D` range, `0xE57AA5` overflow, `0xE5952A` not finite) | an **exit** slot after `add esi, -delta` puts the eval-stack pointer back, so P0's exit re-runs EA's handler on the instruction's own state (no generatable handler has one; the rule is there for the next class) |
| `call rel32` into the CRT (`memcpy` thunk `0xF7E8A6`, `memset` `0xF7E8B8`, `_ftol2` `0xF7EC76` / `_ftol2_sse` `0xF7EC40`, `_allmul` `0xF7EB40`, `_aullshr` `0xF7EEE0`, the i8 shift helpers `0xF7EFC0` / `0xF7EFF0`) | a **call** slot with the absolute target |
| `call dword ptr [import]` (`_isnan` in the float compares and branches, `memmove` in `vtresult`) | kept as is (absolute) |
| a write to ebx (the compiler keeps the argument arena there) | the template is wrapped in `push ebx` / `pop ebx`, with a `pop` before every slot that leaves |
| everything else | its own bytes: the x87 sequences, the frame reads (`[ebp-8]` invocation, `[ebp-0xC]` vt stack pointer, `[ebp-0x10]` runtime method, `[ebp-0x14]` locals, the scratch slots `[ebp-0x1C]`, `[ebp-0x20]`, `[ebp-2]`) |

A handler the rules cannot express is refused with the reason (§4): edi used for anything else, an operand read after
the ip became operand-dependent, a fall-through advance that is not the instruction's length, a jump out of the
interpreter, two paths meeting with different ip or esi offsets.

Two renderings come from the same role list: the **handler form** (the ip bookkeeping kept, the operands as immediates)
runs in the handler's own convention, and the **compiler form** (the local JSON) has the slots. The check runs both.

The local file has one entry per generatable opcode: `opcode`, `handler` (the fragment's address), `words`, `status`
(`lifted` or `hand` with its `reason`), `template` (the compiler-form bytes), `slots`, `calls` (absolute CRT targets),
`clobbers` (`ebx` when wrapped), `uses` (frame reads and import calls), `exitAfterSideEffect`, `operandBytes` and `verify`
(`cases`, `mismatches`). A slot is `{at, kind}` with `at` the byte offset inside the template: `imm` adds `reg`, `operand`
(the operand's byte offset in the mint instruction), `size` (2 or 4) and `ext` (`zx`, `sx` or `mov`); `next` and `taken`
are `jmp rel32` fields the emitter fills from the position labels (`taken` adds `operand`, the branch operand's byte
offset); `call` carries `target`; `exit` is the P0 exit after the eval-stack pointer is put back. Only the census (no bytes) is in the repo.

## 2. The check [emu]

Per opcode, `CASES` = 12 random cases plus an edge set (both operands 0, ±0, 1, ±inf, NaN, 2^53 + 2, 2^63 for r8;
0, ±1, the i64 limits, 2^32 for i8; 5 branch offsets including negative ones; 4 of the x87 control words for the float
opcodes; both states of the CRT SSE2 flag for the `_ftol2` callers). The frame is docs/221's: eval stack, locals,
argument and stack-argument areas, a pointer target, a value-type stack with its pointer at `[ebp-0xC]`, the item table
at runtime-method + 0x14, `this` at invocation + 0xC. After the run the comparison covers the exit taken, esi, every byte
of those regions, the vt pointer, `this`, the ip (so a branch's decision and distance), the x87 top and control word.
A fault on either side is a mismatch, never a pass (the first pass of the tool had eight value-type opcodes "passing"
because both sides faulted on an unstubbed `memcpy` import; the rule and the stub came from that).

Results over the 132 lifted opcodes: 2,189 cases, 0 mismatches; every opcode reached the dispatch in every case; all 50
branch templates took both directions in the sample (`tools/emu/run.py tools/emu/specs/mint_lift.py` prints the PASS;
`--write` regenerates the files).

## 3. The templates [verified]

| | |
|---|---|
| Lifted | 132 of 136 (23 conversions, 50 i8 / r8 branches, 11 `starg.*`, 3 each `ldloc` / `stloc` / `ldarg` / `stinarg` / `ldc`, the value-type moves `vtresult`, `ldloc.vt`, `stloc.vt`, `ldarg.vt`, `starg.vt`, `stinarg.vt`, `dup.vt`, `initobj`, `initlocals`, the item loads `ldstr` / `ldftn` / `ldtoken` / `mono_ldptr`, `ldthisa`, `stthis`, the i8 / r8 arithmetic and compares, `nop`) |
| Bytes | 5,604 in all; 5 (`nop`) to 107 (`ceq.r8`, two `_isnan` calls kept) |
| Slots | 91 imm (36 dword, 28 word zero-extended, 27 word sign-extended), 147 next, 50 taken, 17 call, 0 exit |
| ebx wrapped | 32 templates (the float compares and branches, the value-type moves, `stinarg.r8`) |
| Import calls kept | 26 (`_isnan` ×2 in each float compare / branch, `memmove` in `vtresult`) |
| Frame slots read | invocation 19, runtime method 8, locals 7, vt pointer 5, scratch 6 |

What the compiler side still has to do (not built here: no C++ toolchain on the contributor's machine, docs/521 says
the same for the IL patcher): a `K_LIFT` kind in `kJitTmpl` whose emitter copies the template bytes and fills the slots
(imm from the instruction's operand words, next / taken from the position labels as `K_BR` does, call targets absolute),
and the two convention notes of docs/507 §b: the value-type moves read `[ebp-0xC]`, so the compiled frame must keep the
interpreter's vt pointer there; the locals come from `[ebp-0x14]` in the templates (P0 keeps them in edi, both are the
same memory). `MONOHOST_JIT=verify` is then the live check per docs/207 §8, and it must stay at 0 mismatches as it is
now for the hand templates.

## 4. Left to hand templates (4) [verified]

| Opcode | Why | What instead |
|---|---|---|
| `brfalse.r8` (long) | EA's handler decodes the long form as the short one: 16-bit operand, falls through 2 words of a 3-word instruction (docs/213 §4 row `0xA2`). A lifted template would place the next instruction 2 bytes early. | P0 already exits on it; keep the exit |
| `switch` | reads its case table through the ip with the index (`mov eax, [edi + ecx*4]`): a table, not an operand | P0 compiles `switch` as an exit |
| `stobj.vt` | the value size comes from the class (`call 0xE6F5A0` on the `MonoClass*` item): the frame has no class model to check it against, and the template would carry a call into the runtime | hand template, after a class model in the emu frame |
| `rem.r8` | calls the CRT `_CIfmod`, an x87 `fprem` loop this emulator does not model (docs/221 skips it too) | the handler-form template is sound by construction (the body is EA's) but unverified here |

## 5. Reproduce

```
python tools/emu/run.py tools/emu/specs/mint_lift.py             # 0 mismatches, RESULT: PASS
python tools/emu/specs/mint_lift.py --write                      # tools/emu/specs/out/jit-lifted-templates.json (local) + docs/data/jit-lift-census.csv
```

Needs `game-copy/bin/TS3W.exe` (1.67) and the Python packages `unicorn` and `capstone`, as docs/221.
