# 221. Mint float opcodes: compiled templates that match EA bit for bit

Emulation only (2026-10-31): no build, no game run. Round 3, task B2. The game's float handlers and our templates run on the same mint frame under Unicorn, and every case compares the full state. Spec: `tools/emu/specs/mint_float.py`. Run it with `python tools/emu/run.py tools/emu/specs/mint_float.py`, and add `--controls` to run the alternatives; set `TS3W_EXE` to your own 1.67 exe. Follows docs/207 §7-8 (the P0 method compiler) and docs/213 (the opcode spec). Tags: **[emu]** = shown in this harness; **[verified]** = read in the handler; **[inferred]** = not shown here.

## Summary

- **84 float opcodes, 49,152 cases, 0 mismatches [emu].** Each template below gives the same bits as EA's handler in every eval slot, local, argument, field, array element and pointer target. It also matches EA's exception exit, branch decision, x87 control word and x87 stack top.
- **Keep x87 for arithmetic, `neg`, r4 stores and `conv.r4.r8`.** With SSE2 `mulsd`/`divsd`, 10 and 2 cases differ even at the default control word. These are subnormal products and quotients, where EA's x87 rounds twice. At any other precision or rounding mode, SSE2 arithmetic differs in about a third of the cases.
- **Compares and branches can drop EA's two `_isnan` import calls each.** `fucomip` or `ucomisd` plus parity gives the same truth table, NaN included.
- **`conv.u4/i8/u8.r8` stay on EA's slow path for now.** SSE3 `fisttp` would replace a 33-byte control-word dance or a CRT call with 4 bytes. This emulator gets `fisttp` wrong for NaN and out-of-range values, so it needs one check on a real CPU (§5).

## 1. Method

- **Frame.** The docs/213 layout. `edi` = ip and `esi` = next free eval slot. `ebp` = the interpreter's C frame:
  - `[ebp-8]` MonoInvocation (args `+0x10`, stack args `+0x14`);
  - `[ebp-0xC]` vt stack top;
  - `[ebp-0x10]` RuntimeMethod (arg offsets `+0x1C`);
  - `[ebp-0x14]` locals;
  - `[ebp-0x1C]` and `[ebp-0x20]` scratch.

  P0 code runs in this frame with the same registers (docs/207 §7), so a template is straight-line code on the same memory. Every region is filled with random bytes per case. A template that leaves a stale high dword where EA writes one (or the reverse) shows up.
- **Run.** EA's handler runs from its jump-table entry until it reaches the dispatch loop `0xE544D5` or an exception exit:
  - null `0xE5947E`;
  - index `0xE5949D`;
  - overflow `0xE57AA5`;
  - not finite `0xE5952A`.

  The template runs the same way. Both may run only their own code plus the CRT helpers EA calls (`_ftol2_sse`, `_ftol2`, the `_finite` wrapper). `_isnan` and `_finite` are C-semantics stubs, as in docs/213.
- **Compared.** Exit kind, `esi`, every eval slot (8 bytes each), locals, args, stack args, the object, array and pointer targets, the vt stack top, ip, the x87 control word and the x87 stack top.
- **Inputs.**
  - 63 double patterns: quiet and signalling NaNs with payloads, ±0, ±inf, subnormals, float and integer range limits, x.5 ties, 2^53 + 2, 2^63, 2^64 and float rounding ties.
  - 15 float patterns, 9 int32 and 13 int64 edge values, and 20 random values per opcode.
  - All pairs of a smaller double set for the binary opcodes.
  - Products that round twice in the subnormal range: `(1 + 2^-52)^2 * 2^-1024` and scaled variants.
  - Six x87 control words: `0x027F` (the default: 53-bit precision, round to nearest), 24-bit and 64-bit precision, and round down, up and to zero.
  - Both states of the CRT SSE2 flag `[0x1234374]`, wherever the handler calls `_ftol2_sse`.
- **Encoding check.** The templates are hand-encoded bytes. At startup the spec decodes every template instruction with capstone and compares it with the text the encoder meant. A wrong byte stops the run.
- **Not run:**
  - `rem.r8`: the handler and the template both call the CRT `_CIfmod`.
  - `ldc.r8`: integer moves, no FPU.
  - The `brfalse.r8` long form: docs/213 §4 bug. Compiled code exits to EA's handler here.

## 2. What EA's handlers do with the FPU [verified, each shown in emulation]

1. **Eval slots hold doubles.** r4 values are widened on every load (`fld dword` then `fstp qword`, which is exact). They are narrowed only when stored to r4 memory and by `conv.r4.r8`; both go through the dword at `[ebp-0x1C]`. `conv.r4.i4` and `conv.r4.i8` never narrow (docs/213 §3 #7). A template that rounds to float, as the CLI asks, differs from EA in 25 of 29 `i4` edge cases and 23 of 33 `i8` cases [emu].
2. **Arithmetic** (`add/sub/mul/div.r8`) is one x87 operation at the thread's precision control. The 64-bit store then rounds it again.
   - At the default 53-bit precision, this rounds twice in the subnormal range. `(1 + 2^-52)^2 * 2^-1024` gives `0x0004000000000000`; the correctly rounded result is `0x0004000000000001`. `2^-1022 / 1.5` differs too [emu].
   - At 24-bit precision every r8 result has float precision (docs/213 §1, limit 1).
3. **Compares and conditional branches** call `_isnan` on both operands (an import call each), then use `fcomp` and the status word. NaN makes the ordered forms false and the `.un` forms true. `brtrue.r8` and `brfalse.r8` compare with 0.0, so NaN counts as non-zero.
4. **Integer results** write only the low dword of the slot. The high dword keeps the old double's bits, and a template must do the same.
5. **To integer:**
   - `conv.i1/i2/i4.r8` call `_ftol2_sse`. It uses `cvttsd2si` when the SSE2 flag is set, which it is on every SSE2 CPU. Without the flag it uses the x87 `_ftol2`, which keeps the low bits of out-of-range values instead of returning `0x80000000`.
   - `conv.u1/u2.r8` use `fistp dword` with the control word switched to chop.
   - `conv.u4.r8` uses `fistp qword` with chop and keeps the low dword.
   - `conv.i8/u8.r8` call `_ftol2`.
   - The checked forms first compare the untruncated value with inclusive bounds (docs/213 §4: `127.5` overflows `i1`, NaN passes).
6. **From unsigned.** `conv.r.un.i4` loads the value as signed and adds 2^32 only when it is negative, so only negative inputs round at 24-bit precision. `conv.r.un.i8` loads the low 63 bits and adds the top bit as a constant: one rounding, at the add.
7. **`ckfinite`** calls `_finite` through a small wrapper.

## 3. Templates

All templates are our own code. There is one listing per class; operands are compiled in as constants.

| Opcodes | Template | Bytes | Why |
|---|---|---|---|
| `add/sub/mul/div.r8` | `fld q[esi-16]; fadd q[esi-8]; fstp q[esi-16]; sub esi, 8` | 12 | same x87 operations, so the same precision control and double rounding |
| `neg.r8` | `fld q[esi-8]; fchs; fstp q[esi-8]` | 8 | x87 quiets a signalling NaN on load, as EA's does; an integer sign flip would not (§5) |
| `ceq/cgt/clt(.un).r8` | `fld b; fld a; fucomip st(1); fstp st(0); setcc al` (+ `setnp`/`setp cl` and `and`/`or`); `movzx eax, al; sub esi, 8; mov d[esi-8], eax` | 22-27 | no `_isnan` calls; parity gives the NaN rule; writes the low dword only |
| `b*.r8(.s)` (20 forms) | the same compare, then a native `jcc` | 15-20 + jcc | same truth table [emu] |
| `brtrue.r8(.s)`, `brfalse.r8.s` | `mov eax, d[esi-4]; add eax, eax; or eax, d[esi-8]; setne/sete al` + `jcc` | 15 + jcc | integer test, no FPU: NaN and subnormals are non-zero, ±0 is zero |
| `conv.i1/i2/i4.r8` | `cvttsd2si eax, q[esi-8]` (+ `movsx`); `mov d[esi-8], eax` | 8-11 | when `[0x1234374]` is set at compile time; otherwise `call _ftol2_sse` |
| `conv.u1/u2.r8` | `cvttsd2si eax, q[esi-8]; movzx eax, al/ax; mov d[esi-8], eax` | 11 | NaN and values outside int32 give `0x80000000`; its low byte and word are 0, as EA's chop gives [emu] |
| `conv.u4.r8`, `conv.ovf.u4.r8`, `conv.ovf.i4.un.r8` | EA's method: `fnstcw`, `or 0xC00`, `fldcw`, `fistp q[ebp-0x20]`, take the low dword, `fldcw` back | 33 | `fisttp` would do the same in 4 bytes on real CPUs (§5) |
| `conv.i8/u8.r8`, `conv.ovf.i8(.un)/u8.r8` | `fld q[esi-8]; call _ftol2; mov [esi-8], eax; mov [esi-4], edx` | 14 | `fisttp q[esi-8]` would replace the call (§5) |
| `conv.ovf.*.r8` | EA's two bound compares (our constants), then the template above | 37-51 | same bounds, same NaN pass |
| `conv.r8.i4`, `conv.r4.i4`, `conv.r8.i8`, `conv.r4.i8` | `fild d/q[esi-8]; fstp q[esi-8]` | 6 | no narrowing to float, as EA (§2 #1) |
| `conv.r4.r8` | `fld q; fstp d[ebp-0x1C]; fld d[ebp-0x1C]; fstp q` | 12 | rounds with the x87 rounding mode, not MXCSR's |
| `conv.r.un.i4` | `mov eax, d[esi-8]; fild d[esi-8]; test eax, eax; jge +6; fadd q[2^32]; fstp q[esi-8]` | 19 | adds only when negative, as EA |
| `conv.r.un.i8` | `mov eax, d[esi-4]; shr eax, 31; fild q[esi-8]; fadd q[tbl + eax*8]; fstp q[esi-8]`, with `tbl = {-0.0, 2^64}` | 19 | one rounding, at the add; `-0.0` keeps EA's sign of zero when rounding down [emu] |
| `ckfinite` | `mov eax, d[esi-4]; and eax, 0x7FF00000; cmp eax, 0x7FF00000; je <ArithmeticException exit>` | 21 | integer exponent test, no `_finite` call |
| `ldc.r4` | `mov d[ebp-0x1C], imm; fld d[ebp-0x1C]; fstp q[esi]; add esi, 8` | 15 | the constant pre-widened into two immediates needs §5 |
| `ld{loc,arg,fld,ind,elem}.r4/.r8`, `vtldfld` forms | base address, (null / bounds test to EA's exits), `fld d/q[src]; fstp q[slot]` | 11-33 | widens through x87, as EA |
| `st{loc,arg,inarg,fld,ind,elem}.r4` | base address, (tests), `fld q[esi-8]; fstp d[dst]` | 12-33 | EA's dword round trip is redundant here: one `fstp dword` gives the same bits [emu] |
| `st*.r8` | base address, (tests), `fld q[esi-8]; fstp q[dst]` | 12-33 | through x87, as EA (§5) |

## 4. Results [emu]

**Templates vs EA:** 49,152 cases, 84 opcodes, 0 mismatches. The cases include:
- every exception exit (null, index, overflow, not finite);
- both outcomes of every branch form;
- the double-rounding products;
- all six control words and both SSE2 flag states.

**The alternatives a compiler might pick** (`--controls`): cases that differ from EA. Cases with a signalling-NaN input are left out (§5).

| Alternative | Default control word | 24-bit precision | 64-bit precision | Other rounding modes |
|---|---|---|---|---|
| SSE2 `addsd` | 0 of 3616 | 109 of 306 | 0 of 301 | 159 of 922 |
| SSE2 `subsd` | 0 of 3614 | 112 of 309 | 0 of 307 | 167 of 915 |
| SSE2 `mulsd` | **10 of 3625** (the double-rounding products) | 73 of 300 | 10 of 317 | 99 of 923 |
| SSE2 `divsd` | **2 of 3618** (subnormal quotients) | 71 of 306 | 0 of 305 | 126 of 916 |
| SSE2 `ucomisd` for compares and branches | 0 | n/a | n/a | n/a |
| `cvtsd2ss` + `movss` for `stloc/starg.r4` | 0 of 60 | 0 of 60 | n/a | 33 of 180 |
| `cvtsd2ss` + `cvtss2sd` for `conv.r4.r8` | 0 of 80 | 0 of 80 | 0 of 80 | 62 of 240 |
| `cvttsd2si` for `conv.u4.r8` (values outside int32) | 27 of 80 | 25 of 80 | 28 of 80 | 77 of 240 |
| float rounding for `conv.r4.i4` (the CLI rule) | 25 of 29 | 25 of 29 | 24 of 29 | 70 of 87 |
| float rounding for `conv.r4.i8` | 23 of 33 | 23 of 33 | 23 of 33 | 77 of 99 |
| zero-extend + `fild qword` for `conv.r.un.i4` | 0 of 29 | 16 of 29 | 0 of 29 | 0 of 87 |
| integer sign flip for `neg.r8` | 0 of 80 | 0 of 80 | 0 of 80 | 0 of 240 |
| integer moves for `stloc/starg.r8` | 0 of 60 | n/a | n/a | n/a |

"n/a" = the spec draws no cases with that control word for those opcodes. A control word cannot change a compare.

**What this means for P1.** SSE2 arithmetic is safe only at the default control word and away from subnormal results. That the sim thread always runs at `0x027F` is only inferred (docs/37; docs/213 §1, limit 1). So keep x87 for arithmetic and narrowing, and use `fucomip` or `ucomisd` for compares and branches.

## 5. Emulator limits, and the candidates they leave open

- **Signalling NaNs.** This Unicorn (2.1.4, QEMU softfloat) does not quiet a signalling NaN on `fld`: an x87 load of `0x7F800001` keeps the signalling bit. Real x87 CPUs quiet it when the invalid exception is masked (Intel SDM, FLD). So the harness cannot tell x87 moves from integer moves, or x87 `neg` from an integer sign flip, when the input is a signalling NaN. The templates use x87 wherever EA does, so they also match on real CPUs [inferred]. Pre-widening the `ldc.r4` constant with the hardware rule differs here only for the two signalling-NaN floats [emu], and should match on real CPUs [inferred].
- **`fisttp`.** For NaN and out-of-range values this emulator returns `INT64_MAX` instead of the integer-indefinite `0x8000000000000000`. Real CPUs return the indefinite value, as EA's `fistp` and `_ftol2` do [inferred: Intel SDM]. So `fisttp` (SSE3) for `conv.u4/i8/u8` needs one check on a real CPU. A monohost `MONOHOST_JIT=verify` run of these opcodes is the natural place.
- **The x87 model is software** (docs/213 §1, limit 3). Both sides run on the same model, so an error the model makes the same way on both sides would not show.
